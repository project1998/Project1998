using System.Diagnostics;
using Shared;

namespace Server;

// The per-mob half of World.Tick (#36). World.cs keeps the heartbeat, the lock and the flush; this file
// keeps what one creature does on one beat. Both types are NESTED in World rather than top-level so they
// reach MapState, the step helpers (Dart, StepMobToward, TriggerTrapLocked...) and the AI constants as
// they are, without widening any of them to internal — the one widening the split needed is MapState
// itself, because a field on an internal type cannot be of a private one.
public sealed partial class World
{
    /// <summary>
    /// One beat's outbound work, built ONCE per tick by <see cref="Tick"/> before it takes <c>_lock</c> and
    /// drained ONCE by <see cref="FlushTick"/> after it releases it. Nothing here sends: the locked phases
    /// decide and queue, the flush acts (<c>docs/common/Locking.md</c>, "decide under the lock, act outside
    /// it"). The lists are the tick's own and are shared by every map's <see cref="MobTickContext"/> that
    /// beat, so their order across maps is the order the maps were walked in.
    ///
    /// <para>The last three are not queues and are deliberately mutable: <see cref="Forage"/> and
    /// <see cref="WeatherChanges"/> stay NULL unless the beat produced any (the flush tests for null rather
    /// than walking an empty list), and <see cref="TimeChanged"/> is the day/night rollover flag, which
    /// drives its broadcast independently of the weather one. They ride here for the same reason the queues
    /// do — they are written under the lock and read after it — so the flush needs one parameter.</para>
    /// </summary>
    internal sealed class TickQueues
    {
        public readonly List<(ushort map, uint id, ushort x, ushort y, byte dir)> Moves = new();
        public readonly List<(ushort map, uint id, byte dir)> Turns = new();
        public readonly List<(ushort map, Mob mob, Session target)> Hits = new();
        /// <summary>A creature's spell at a player (MobSpells.csv) and its idle flavour line — both queued for
        /// the same reason as <see cref="Hits"/>: landing a spell broadcasts, curses, and can kill.</summary>
        public readonly List<(Mob mob, Session target, Content.MobSpellDef spell)> MobCasts = new();
        public readonly List<(ushort map, Mob mob, byte channel, string line)> Chatter = new();
        /// <summary>A pet's swing at another mob (mob-on-mob, the only place that happens) — deferred out of
        /// the lock for the same reason as <see cref="Hits"/>: applying it broadcasts and can award the owner
        /// exp.</summary>
        public readonly List<(ushort map, Mob attacker, Mob victim)> MobHits = new();
        /// <summary>Real damage from a triggered trap (instant hit) or a poison tick — both need
        /// Session-facing broadcasts (damage number, death despawn, owner exp) that must run outside the
        /// lock, same as <see cref="Hits"/>.</summary>
        public readonly List<(ushort map, Mob mob, int dmg, uint ownerId)> TrapDamage = new();
        /// <summary>Repeating status effects (RTK <c>while_cast</c>): venom re-draws its animation every
        /// poison tick, doze and sleep re-draw theirs for as long as the hold runs. Broadcasting is socket
        /// I/O, so — like every other visual here — the tick only QUEUES them under the lock and sends them
        /// after it's released.</summary>
        public readonly List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> FxRepeats = new();
        /// <summary>Over-head HP bars to redraw for a mob whose health changed with NO hit behind it — Sute's
        /// self-heal is the only source. Damage draws its own bar through <c>Session.ShowDamageResult</c>; a
        /// heal has no such path, so without this his bar silently stayed where the last blow left it and the
        /// heal was invisible to the player fighting him.</summary>
        public readonly List<(ushort map, Mob mob)> HealthShows = new();
        public readonly List<(ushort map, Mob mob)> ExpiredPets = new();
        public readonly List<Session> ExpiredMorphs = new();
        public readonly List<Session> ExpiredStealth = new();

        /// <summary>Null unless this beat's forage top-up ran and placed something.</summary>
        public List<(ushort map, GroundItem gi)>? Forage;
        /// <summary>Set when the in-game hour rolled over; drives the 0x20 broadcast on its own.</summary>
        public bool TimeChanged;
        /// <summary>Null unless the weather period rolled over this beat.</summary>
        public List<(ushort map, byte weather)>? WeatherChanges;
    }

    /// <summary>
    /// Everything <see cref="MobAiTick.Step"/> needs about the map it is stepping on, built ONCE per map per
    /// tick by <see cref="Tick"/> just before the mob loop. The two tile sets are the beat's collision index
    /// (kept current as mobs move, so two creatures cannot step onto the same tile in one sweep); the lists
    /// are the tick's outbound queues — a step never sends, it only queues, and the tick flushes every list
    /// after <c>_lock</c> is released (see <c>docs/common/Locking.md</c>, "decide under the lock, act outside
    /// it"). The queues are the tick's own lists, shared by every map's context that beat, so their order
    /// across maps is the order the maps were walked in — exactly as before the split.
    ///
    /// <para>What it deliberately does NOT carry is the live <see cref="MapState"/>. <c>Step</c> resolves the
    /// map through <see cref="World.Map"/>, which is private to <c>World</c>; a context handed to a test
    /// (<see cref="MobTickContextForTest"/>) therefore reaches its own queues and its own tile-set copies and
    /// nothing the tick shares — the #108 review's point that <c>InternalsVisibleTo("Tests")</c> makes an
    /// <c>internal</c> field no seal at all.</para>
    /// </summary>
    internal sealed class MobTickContext
    {
        public readonly World World;
        public readonly ushort MapId;
        /// <summary>The map's size from the registry, or (0,0) for a map with no registry row — the step
        /// helpers read a zero width as "unbounded", which is what a content-free test map wants.</summary>
        public readonly (ushort Xs, ushort Ys) Dims;
        /// <summary>The map's collision layers, or null when <see cref="Dims"/> is zero.</summary>
        public readonly MapData? Terrain;
        /// <summary>Every player's tile this beat — a mob never steps onto one.</summary>
        public readonly HashSet<(ushort, ushort)> Occupied;
        /// <summary>Every living mob's tile — kept current as they move, so a mob won't step onto another.</summary>
        public readonly HashSet<(int, int)> MobTiles;

        public readonly List<(ushort map, uint id, ushort x, ushort y, byte dir)> Moves;
        public readonly List<(ushort map, uint id, byte dir)> Turns;
        public readonly List<(ushort map, Mob mob, Session target)> Hits;
        public readonly List<(Mob mob, Session target, Content.MobSpellDef spell)> MobCasts;
        public readonly List<(ushort map, Mob mob, byte channel, string line)> Chatter;
        public readonly List<(ushort map, Mob attacker, Mob victim)> MobHits;
        public readonly List<(ushort map, Mob mob, int dmg, uint ownerId)> TrapDamage;
        public readonly List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> FxRepeats;
        public readonly List<(ushort map, Mob mob)> HealthShows;
        public readonly List<(ushort map, Mob mob)> ExpiredPets;

        /// <summary>Caller holds <c>_lock</c>: the two tile sets are read off live player and mob lists.
        /// <paramref name="map"/> is read here and not kept — see the class doc. The ten queue fields are
        /// aliases of <paramref name="q"/>'s lists, not copies, so <c>Step</c>'s body reaches the tick's own
        /// queues exactly as it did when they were thirteen separate constructor arguments.</summary>
        public MobTickContext(World world, ushort mapId, MapState map, TickQueues q)
        {
            Debug.Assert(world.HoldsWorldLock, "MobTickContext reads the live player and mob lists; build it under World._lock");
            World = world; MapId = mapId;
            Moves = q.Moves; Turns = q.Turns; Hits = q.Hits; MobCasts = q.MobCasts; Chatter = q.Chatter; MobHits = q.MobHits;
            TrapDamage = q.TrapDamage; FxRepeats = q.FxRepeats; HealthShows = q.HealthShows; ExpiredPets = q.ExpiredPets;

            Dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
            Terrain = Dims.Item1 > 0 ? MapData.For(mapId, Dims.Item1, Dims.Item2) : null;
            Occupied = map.Players.Select(p => (p.PlayerX, p.PlayerY)).ToHashSet();
            // Every living mob's tile — so a mob won't step onto another (kept current as they move below).
            MobTiles = new HashSet<(int, int)>();
            foreach (var mo in map.Mobs) if (mo.Alive) MobTiles.Add((mo.X, mo.Y));
        }
    }

    /// <summary>
    /// One creature's turn on one heartbeat: buff and ownership expiry, the status timers, then whichever
    /// of pet / prey / blind / rout / chase / retaliation / wander applies. Mutates the mob's own fields and
    /// the context's tile sets, and adds to the context's queues; it never sends and never enters a
    /// session (<c>docs/common/Locking.md</c> row 2) or the Lua gate (row 1) — everything session-facing is
    /// queued for the tick to apply after <c>_lock</c> is released.
    ///
    /// <para>Runs under <c>World._lock</c>, and only there; the assert at the top is the contract in Debug
    /// builds (it is compiled out of Release, like every lock assert in this tree — <c>Tests/MobAiTickTests.cs</c>
    /// pins it firing). It complements the <c>Tests/MobAiLockTests.cs</c> scan rather than replacing it: that
    /// scan covers the files that hold a mob they do not own, and this file is <c>World</c>, the owner of the
    /// lock, which is why <c>World.cs</c> is not scanned either. The exception boundary is the CALLER's:
    /// <see cref="World.Tick"/> wraps each call so one creature that throws is logged and skipped while the
    /// rest of the sweep — and every packet already queued — goes on. A test that wants to see a throw
    /// drives this directly and expects it to propagate.</para>
    /// </summary>
    internal static class MobAiTick
    {
        public static void Step(MobTickContext ctx, Mob mob)
        {
            Debug.Assert(ctx.World.HoldsWorldLock, "MobAiTick.Step runs under World._lock and nowhere else");

            // The context, unpacked under the names the body has always used — so the move below is a move.
            // The map comes from World, not the context (see MobTickContext): one dictionary probe per mob per
            // beat, measured at noise level against the sweep.
            var w = ctx.World; ushort mapId = ctx.MapId; var m = w.Map(mapId);
            var dims = ctx.Dims; var terrain = ctx.Terrain; var occupied = ctx.Occupied; var mobTiles = ctx.MobTiles;
            var moves = ctx.Moves; var turns = ctx.Turns; var hits = ctx.Hits; var mobCasts = ctx.MobCasts;
            var chatter = ctx.Chatter; var mobHits = ctx.MobHits; var trapDamage = ctx.TrapDamage;
            var fxRepeats = ctx.FxRepeats; var healthShows = ctx.HealthShows; var expiredPets = ctx.ExpiredPets;

            // Sute's action rhythm (Server/SuteAi.cs): he acts on two beats out of every three and
            // rests on the third, which is what makes both his steps and his swings arrive in pairs.
            // Gated here, at the very top of his turn, so it governs everything uniformly — chasing,
            // wandering, breaking off and striking — rather than only the parts his AI block reaches.
            // Skipping the turn outright (rather than zeroing timers) is what makes the rest a real
            // pause: nothing accumulates, so he cannot bank the beat and act twice on the next one.
            if (mob.Key == SuteAi.MobKey && SuteAi.RestBeat(mob)) return;

            // Targeted-buff expiry (Session.CastTargetBuff, e.g. Valor/Harden Armor on a pet): revert each
            // lapsed buff's stat delta off the mob's raw combat fields. Field-only, so it's safe in-lock.
            if (mob.Buffs is { Count: > 0 })
            {
                long bnow = Environment.TickCount64;
                for (int i = mob.Buffs.Count - 1; i >= 0; i--)
                    if (mob.Buffs[i].ExpiresAt <= bnow)
                    {
                        mob.AdjustBuffField(mob.Buffs[i].Stat, mob.Buffs[i].Amount, -1);
                        mob.Buffs.RemoveAt(i);
                    }
            }

            // Lifespan expiry — two DIFFERENT endings, keyed on Mob.Summoned:
            //   conjured (CotW pet, Giasomo bird)  -> plain despawn, no kill/loot/exp, same as riding a
            //     mob away (RTK cotw_SpawnSetThreat's spawnTime). DespawnMob does socket I/O so it must
            //     run outside this lock, hence the deferred list.
            //   mind-controlled (Endear & kin)     -> the creature was always a real world mob, so it
            //     just stops being yours: RTK endear's `uncast` is exactly `mob.owner = 0; mob.target = 0`.
            //     Clearing TargetId means it forgets whoever it was fighting FOR you and re-acquires
            //     normally next tick — including you.
            // The conjured half does NOT require an owner: a scripted ambush (World.ExpireUnowned — Master
            // Dagger's assassins) is conjured and timed but belongs to nobody, so it must not fall into the
            // pet AI below and must still vanish here.
            if (mob.PetExpiresAt != 0 && Environment.TickCount64 >= mob.PetExpiresAt)
            {
                if (mob.Summoned) { expiredPets.Add((mapId, mob)); return; }
                mob.OwnerId = 0; mob.TargetId = 0; mob.TargetMobId = 0; mob.PetExpiresAt = 0;
                // Re-home it where it actually is. A charmed creature that chased something you were
                // fighting can end up well outside the leash box around the Home it spawned at, and
                // the wander step below leashes on ABSOLUTE distance from Home — without this it would
                // fail every candidate step and stand frozen. (The walk-home in the wander block
                // covers the same hazard for wild mobs; this one has no spawn point to walk back to.)
                mob.HomeX = mob.X; mob.HomeY = mob.Y;
            }

            // Poison trap DOT (RTK poison_dart_trap.lua while_cast_1500): ticks every 1500ms regardless
            // of freeze/wander state, and — per RTK — never fires a tick that would finish the kill.
            if (mob.PoisonUntil > Environment.TickCount64 && Environment.TickCount64 >= mob.PoisonNextTick)
            {
                mob.PoisonNextTick = Environment.TickCount64 + PoisonTickMs;
                // "Poison will not kill a target but rather bring them to the lowest possible health"
                // (NexusAtlas). RTK's while_cast says the same in code: `if health > damage then
                // remove else health = 1`. This used to SKIP the tick once HP fell to the tick
                // amount, which left the victim parked wherever it happened to be instead of at 1 —
                // so a venomed creature stopped short of the state the spell is supposed to leave it in.
                int lethal = Math.Max(0, mob.Hp - 1);
                int dam = Math.Min(mob.PoisonTickDam, lethal);
                if (dam > 0) trapDamage.Add((mapId, mob, dam, mob.PoisonOwnerId));
            }

            // Repeating status animation (RTK `while_cast`): venom's per-tick zap, doze/sleep's drowse.
            // Driven here rather than off each status's own timer so one cadence covers them all, and
            // so it keeps running while the mob is frozen — which is exactly when you need to see it.
            if (mob.FxRepeatUntil > Environment.TickCount64)
            {
                if (Environment.TickCount64 >= mob.FxRepeatNext)
                {
                    mob.FxRepeatNext = Environment.TickCount64 + mob.FxRepeatEvery;
                    fxRepeats.Add((mapId, mob.Id, mob.X, mob.Y, mob.FxRepeatAnim, mob.FxRepeatSound));
                }
            }
            else if (mob.FxRepeatUntil != 0) mob.FxRepeatUntil = 0;

            // Last Stand: while it runs, the boss claws HP back every heartbeat (RTK mob_ai_mythic
            // heals on every move tick that `mob:hasDuration("last_stand")`). Before the freeze check,
            // so a paralysed boss still regenerates — being held is not a free win against one.
            if (mob.LastStandUntil != 0 && Content.MobBosses.TryGetValue(mob.Key, out var lsBoss))
            {
                if (Environment.TickCount64 >= mob.LastStandUntil) mob.LastStandUntil = 0;
                else if (mob.Hp < mob.MaxHp)
                {
                    mob.Hp = Math.Min(mob.MaxHp, mob.Hp + lsBoss.HealAmount);
                    fxRepeats.Add((mapId, mob.Id, mob.X, mob.Y, lsBoss.Anim, lsBoss.Sound));
                }
            }

            // …and while it is held it HEALS THROUGH the hold — every ~3s, a 1-in-2 roll for another
            // full heal (RTK mob_ai_mythic.move: `os.time() % 3 == 0 and mob.paralyzed`). Note it does
            // NOT break free: RTK never clears `mob.paralyzed` here, so paralysis on a boss still
            // holds it still — it just stops being a way to win, because the boss out-heals the hold.
            if (mob.FrozenUntil > Environment.TickCount64
                && Content.MobBosses.TryGetValue(mob.Key, out var pBoss) && pBoss.ParaBreakChance > 0
                && Environment.TickCount64 >= mob.ParaBreakAt)
            {
                mob.ParaBreakAt = Environment.TickCount64 + 3000;   // RTK's `os.time() % 3 == 0` cadence
                if (Random.Shared.Next(pBoss.ParaBreakChance) == 0 && mob.Hp < mob.MaxHp)
                {
                    mob.Hp = Math.Min(mob.MaxHp, mob.Hp + pBoss.HealAmount);
                    fxRepeats.Add((mapId, mob.Id, mob.X, mob.Y, pBoss.Anim, pBoss.Sound));
                }
            }

            // Curse shrug (RTK mob_ai_mythic.move: `os.time() % 10 == 0` and not paralysed, a 1-in-3
            // roll to wipe EVERY curse on itself and flash animation 10). A mythic boss will not stay
            // debuffed: land one and you have a few seconds of it, not the fight.
            if (mob.FrozenUntil <= Environment.TickCount64
                && Content.MobBosses.TryGetValue(mob.Key, out var cBoss) && cBoss.HealAmount > 0
                && Environment.TickCount64 >= mob.CurseShrugAt)
            {
                mob.CurseShrugAt = Environment.TickCount64 + 10_000;
                if (Random.Shared.Next(3) == 0
                    && (mob.HasStatus("curses", Environment.TickCount64) || mob.HasStatus("minorcurses", Environment.TickCount64)))
                {
                    mob.ClearStatus("curses"); mob.ClearStatus("minorcurses");
                    fxRepeats.Add((mapId, mob.Id, mob.X, mob.Y, CurseShrugAnim, 0));
                }
            }

            if (mob.FrozenUntil > Environment.TickCount64) return;   // paralyzed/asleep — hold still

            // Blind (RTK's `target.blind = true`): a blinded creature can't SEE. It drops whoever it
            // was fighting, the unprovoked-aggro scan below is skipped, and — this is the part that
            // used to be wrong — it does NOT wander either. A mob with no sight has nowhere to go, so
            // it holds its ground; the old code fell straight through to the wander block, which made
            // a blinded mob spin on the spot and read as though the spell had done nothing.
            // What it CAN still do is lash out at whatever is within arm's reach, turning to face it:
            // being blind doesn't stop you swinging at someone who walks into you.
            if (mob.BlindUntil > Environment.TickCount64)
            {
                mob.TargetId = 0;
                // Normally a blind creature forgets its mob target too. The one exception is CONFUSE,
                // which turns a mob on a neighbour (World.ConfuseMob sets TargetMobId): a blind mob
                // keeps swinging at a creature on the adjacent tile — it just can't CHASE one, so the
                // target clears the instant that creature isn't next to it. This is what lets two
                // blinded mobs, side by side, be spammed with Confuse into fighting each other.
                Mob? bfoe = mob.TargetMobId != 0 ? m.Mobs.FirstOrDefault(o => o.Alive && o.Id == mob.TargetMobId) : null;
                if (bfoe is not null)
                {
                    int cfdx = bfoe.X - mob.X, cfdy = bfoe.Y - mob.Y;
                    if ((cfdx == 0 && Math.Abs(cfdy) == 1) || (cfdy == 0 && Math.Abs(cfdx) == 1))
                    {
                        byte cff = FaceDelta(cfdx, cfdy);
                        if (cff != mob.Dir) { mob.Dir = cff; turns.Add((mapId, mob.Id, cff)); }
                        mob.AttackTimer += TickMs;
                        if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; mobHits.Add((mapId, mob, bfoe)); }
                        return;
                    }
                    mob.TargetMobId = 0;   // not adjacent any more — a blind mob can't go looking for it
                }
                else mob.TargetMobId = 0;
                // Prey never fights (see the flee block below), and an owned creature has no business
                // swinging at people off a PK map — the same two exemptions the sighted paths apply.
                Session? reach = null;
                if (!mob.Flees && (mob.OwnerId == 0 || Content.IsPvpMap(mapId)))
                    foreach (var p in m.Players)
                    {
                        if (p.IsDead || p.PlayerId == mob.OwnerId) continue;
                        int bdx = p.PlayerX - mob.X, bdy = p.PlayerY - mob.Y;
                        if ((bdx == 0 && Math.Abs(bdy) == 1) || (bdy == 0 && Math.Abs(bdx) == 1)) { reach = p; break; }
                    }
                if (reach is null) { mob.AttackTimer = 0; return; }
                byte bface = FaceDelta(reach.PlayerX - mob.X, reach.PlayerY - mob.Y);
                if (bface != mob.Dir) { mob.Dir = bface; turns.Add((mapId, mob.Id, bface)); }
                mob.AttackTimer += TickMs;
                if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; hits.Add((mapId, mob, reach)); }
                return;
            }

            // Wounded rout (RTK bosses/nine_tailed_fox.lua + ogre_maletic.lua, which is Maletic AND
            // Citelam): below 15% of its max HP the creature STOPS FIGHTING for good and bolts —
            // `local rand = math.random(0,3); mob.side = rand; mob:move() mob:move() mob:move()`
            // replaces the whole of move AND attack, so it will not swing again even if you corner it.
            // Not our prey-flee (MobDef.Flees), which is about a rabbit backing away from anyone: this
            // is an unwounded boss fighting normally right up to the moment it breaks.
            //
            // (`Hp < MaxHp` first so an untouched creature — nearly all of them, every tick — costs a
            // comparison rather than a dictionary probe. A threshold of 100 would break this, which is
            // why the loader's range is capped below it.)
            if (mob.Hp < mob.MaxHp
                && Content.MobSpawnRules.TryGetValue(mob.Key, out var fleeRule) && fleeRule.FleeBelowPct > 0
                && mob.Hp * 100 <= mob.MaxHp * fleeRule.FleeBelowPct)
            {
                mob.TargetId = 0; mob.TargetMobId = 0; mob.AttackTimer = 0; mob.Returning = false;
                mob.MoveTimer += TickMs;
                if (mob.MoveTimer >= mob.MoveTime)
                {
                    mob.MoveTimer -= mob.MoveTime;
                    // A fresh random side each turn, then RoutDartTiles tiles along it — RTK's
                    // `mob.side = rand; mob:move() mob:move() mob:move()`, one for one. No leash and
                    // no steering: it is running, not wandering, and it stops when it hits something.
                    byte side = (byte)Random.Shared.Next(4);
                    if (side != mob.Dir) { mob.Dir = side; turns.Add((mapId, mob.Id, side)); }
                    w.Dart(DartMode.Straight, RoutDartTiles, mapId, m, mob, mob.X, mob.Y,
                         dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                }
                return;
            }

            // ---- PET AI: a mob with an OWNER (a Poet's Call of the Wild summon, or an Endear'd
            // captive) does not behave like a wild one. Three rules, applied in order:
            //   1. never fight your owner,
            //   2. fight what your owner has attacked, or what has attacked your owner,
            //   3. otherwise stand still — where you were summoned.
            // Before this, Mob.OwnerId existed but drove NO behaviour at all, which is why both halves
            // looked broken from the outside: a CotW pet (every cotw_* MobDef is MobBehavior 0) just
            // wandered off on its spawn leash and never swung at anything, and Endear on an aggressive
            // creature handed it to you for a fraction of a second before the unprovoked-aggro scan
            // below re-acquired the nearest player — you — and it turned right back around.
            //
            // All three are RTK's (mob_ai_cotw.move/attack), including the standing still: its move
            // ends `target = mob:getBlock(mob.owner)` and then `if target.blType == BL_PC then return
            // end`, so an idle pet never takes a step toward its owner. A summon is a thing you PLACE.
            //
            // NOT ported, deliberately: a pet does not fight back when something hits IT and only it.
            // RTK's `cotw.on_attacked` looks like retaliation (`if mob.target == mob.owner then
            // mob.target = attacker.ID`), but the very next move tick recomputes the target from the
            // owner's threat list and throws it away, so it never survives to be acted on.
            //
            // What RTK does establish, and we honour, is that a mob's damage is credited to
            // `mob->owner` when that id is a player (clif.c) — that part is ApplyMobOnMobHit.
            if (mob.OwnerId != 0)
            {
                if (mob.TargetId == mob.OwnerId) mob.TargetId = 0;   // rule 1, every tick
                // Off a PK map a pet has no business fighting PEOPLE at all (RTK cotw: `blType ==
                // BL_PC -> return`), so drop any player target it picked up — from a PvP map it was
                // just led off, say. On a PK map it keeps them until they die or leave, exactly like
                // any other mob, so a pet being beaten on can still fight back.
                if (mob.TargetId != 0 && !Content.IsPvpMap(mapId)) mob.TargetId = 0;
                var owner = m.Players.FirstOrDefault(p => p.PlayerId == mob.OwnerId && !p.IsDead);

                if (owner is null)
                {
                    mob.TargetMobId = 0;
                    // A CONJURED pet with no owner here vanishes — RTK mob_ai_cotw.move opens with
                    // exactly this (`owner == nil` or `owner.m ~= mob.m` -> `mob:vanish()`), and it
                    // also saves us a stranded summon wandering a map its poet left. A merely
                    // mind-controlled creature is a real world mob and stays put, wandering until its
                    // charm lapses (RTK routes those through mob_ai_normal, which has no vanish).
                    if (mob.Summoned) { expiredPets.Add((mapId, mob)); return; }
                }
                else
                {
                    // Rule 2a — PvP. On a PK map a pet also fights PEOPLE: whoever its owner is
                    // currently trading blows with (Session.PvpFoeId, set on both sides of a player
                    // spell exchange and expiring after 15s so nobody is chased across the map over a
                    // stale grudge). This is a deliberate departure — RTK's cotw AI refuses player
                    // targets outright (`if attacker.blType == BL_PC then return`), which is right for
                    // the open world and wrong for an arena — so it is scoped to maps already flagged
                    // MapPvP, the same gate that lets a player's own spell damage land at all.
                    // Setting TargetId and NOT continuing hands the pet to the ordinary player-chase
                    // branch below, which already knows how to close on a Session and swing at it.
                    if (Content.IsPvpMap(mapId) && owner.PvpFoeId != 0 && owner.PvpFoeId != mob.OwnerId
                        && m.Players.Any(p => p.PlayerId == owner.PvpFoeId && !p.IsDead))
                    {
                        mob.TargetId = owner.PvpFoeId;
                        mob.TargetMobId = 0;
                    }
                }

                if (owner is not null && mob.TargetId == 0)   // no person to fight — serve the owner
                {
                    // Rule 2, recomputed from scratch every tick (RTK's move and attack branches both
                    // re-walk the threat list; there is no sticky target) so the pet picks up a new
                    // attacker the moment its current one dies, leashes off, or is out-threatened.
                    // Never an NPC, and never the pet itself — everything else is fair game, gated
                    // purely on threat (see the OWNED-CREATURE note below).
                    //
                    // A PET IS REACTIVE, NOT A BODYGUARD, and it fights exactly two kinds of creature:
                    //
                    //   what you have attacked   -> `o.ThreatOf(owner) > 0`. Our threat table is keyed
                    //                               by the player who DEALT the damage, so any mob you
                    //                               have hit carries threat from you. This is RTK's
                    //                               `mobs[i]:checkThreat(mob.owner) > 0` list.
                    //   what has attacked you    -> `owner.RecentMobAttackerId`, stamped by
                    //                               Session.ApplyMobHit on a LANDED blow. RTK's
                    //                               `owner.attacker` fallback, and the reason a pet
                    //                               defends you from something you never touched.
                    //
                    // Both halves need a real blow to have been struck by somebody. A creature that has
                    // merely noticed you, or is walking at you, is invisible to the pet — which is what
                    // makes the corner-wall real: stand in a corner with two summons and nothing moves
                    // until the first hit lands, in either direction.
                    //
                    // AN OWNED CREATURE IS NOT EXEMPT. This used to filter `o.OwnerId == 0`, so a pet
                    // would never look at another pet — and since the owner can now swing at his own
                    // summons, that made hitting one of your own a fight nobody would join. The
                    // threat test is the whole gate: a sibling only becomes a target once you have
                    // actually hit it, so pets still ignore each other (and other poets' pets)
                    // completely until someone starts something. `o.Id != mob.Id` keeps a pet from
                    // picking ITSELF once you've hit it.
                    //
                    // Bounded by AggroRadius because RTK's list comes from `getObjectsInArea` — the pet
                    // fights what is around it, and won't cross a dungeon to reach a high-threat mob it
                    // cannot see. Distance only breaks ties, so it still walks past a rabbit to reach
                    // whatever is actually killing you. The threat list is searched first and the
                    // attacker is the fallback, in RTK's order.
                    uint bit = owner.RecentMobAttackerId;
                    var foe = m.Mobs.Where(o => o.Alive && !o.IsNpc && o.Id != mob.Id
                                     && (o.ThreatOf(owner.PlayerId) > 0 || o.Id == bit)
                                     && Math.Max(Math.Abs(o.X - mob.X), Math.Abs(o.Y - mob.Y)) <= AggroRadius)
                                     .OrderByDescending(o => o.ThreatOf(owner.PlayerId))
                                     .ThenBy(o => Math.Max(Math.Abs(o.X - mob.X), Math.Abs(o.Y - mob.Y)))
                                     .FirstOrDefault();
                    mob.TargetMobId = foe?.Id ?? 0;

                    // A pet steps EVERY heartbeat rather than on its own MobMoveTime. That cadence is a
                    // wander timer (a panda's is 2s), far too slow to close on something mid-fight.
                    if (foe is not null)
                    {
                        int fdx = foe.X - mob.X, fdy = foe.Y - mob.Y;
                        if ((fdx == 0 && Math.Abs(fdy) == 1) || (fdy == 0 && Math.Abs(fdx) == 1))
                        {
                            byte face = FaceDelta(fdx, fdy);
                            if (face != mob.Dir) { mob.Dir = face; turns.Add((mapId, mob.Id, face)); }
                            mob.AttackTimer += TickMs;
                            if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; mobHits.Add((mapId, mob, foe)); }
                        }
                        else w.StepMobToward(mapId, m, mob, foe.X, foe.Y, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                        return;
                    }

                    // Nothing to fight: a pet HOLDS ITS GROUND. It does not heel, does not follow, does
                    // not drift — it stands where it was summoned until something hits you or you hit
                    // something (RTK's move ends `target = getBlock(mob.owner)` and then bails on
                    // `target.blType == BL_PC`, so its pet never paths to its owner either). Walk away
                    // from your summons and you leave them behind, which is what makes them placeable:
                    // two of them in a doorway are a wall, not an escort.
                    return;
                }
            }

            // ---- PREY AI (MobDef.Flees, game-data/MobFlees.csv): a rabbit or a blue rooster does
            // not fight and does not stand there. It DARTS PreyDartTiles tiles away (see Dart) from
            // anyone who gets within FleeRadius, and a swing (PanicMs) widens that radius and keeps it
            // running after you back off. This runs BEFORE everything below because
            // TryDamage sets TargetId on any landed hit — without this intercept, hitting a rabbit
            // would drop it straight into the ordinary chase-and-swing branch and it would fight back.
            // Clearing the target every tick is what "no attacking" actually means here: the attack
            // branch is reached only via TargetId/TargetMobId, so a prey creature can never enter it.
            // An OWNED prey creature (Endear'd) is exempt — it's a pet now, and the pet block above
            // already returned for every case where it has an owner to serve.
            if (mob.Flees && mob.OwnerId == 0)
            {
                mob.TargetId = 0; mob.TargetMobId = 0; mob.AttackTimer = 0;
                bool panicking = mob.PanicUntil > Environment.TickCount64;
                int notice = panicking ? FleeRadius * 2 : FleeRadius;
                Session? scare = null;
                int nearest = int.MaxValue;
                foreach (var p in m.Players)
                {
                    if (p.IsDead) continue;   // a ghost doesn't frighten anything
                    int d = Math.Max(Math.Abs(p.PlayerX - mob.X), Math.Abs(p.PlayerY - mob.Y));
                    if (d <= notice && d < nearest) { nearest = d; scare = p; }
                }
                if (scare is not null)
                {
                    // It DARTS — PreyDartTiles tiles in one move turn, not a hurried walk. See Dart.
                    mob.MoveTimer += TickMs;
                    if (mob.MoveTimer < mob.MoveTime) return;
                    mob.MoveTimer -= mob.MoveTime;
                    w.Dart(DartMode.Away, PreyDartTiles, mapId, m, mob, scare.PlayerX, scare.PlayerY,
                         dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                    return;
                }
                // Nobody near enough to spook it: fall through to the ordinary wander below.
            }

            // Unprovoked aggro (RTK mob.c mob_find_target, gated on MobBehavior==1 "type": engine-level,
            // separate from and runs before mob_ai_normal.lua): an aggressive mob with no target yet
            // locks onto the nearest living player within AggroRadius, same as if it had just been hit —
            // the chase/attack branch right below then takes over on this same tick.
            // OWNED mobs are excluded outright: a charmed creature must not re-acquire the poet who
            // just charmed it (the pet block above already returned for every case where it has an
            // owner it can serve, so this only skips the "owner isn't here" leftovers).
            // (No blind check here any more — a blinded mob never reaches this line; its own branch
            // above handles it and continues.)
            // (@peace players are invisible to this scan — RTK's own FindCoords fallback skips GMs
            // the same way. Hitting a mob still re-acquires via TryDamage, peace or not.)
            if (mob.TargetId == 0 && mob.Aggressive && mob.OwnerId == 0)
            {
                var victim = m.Players.FirstOrDefault(p => !p.IsDead && !p.PeaceMode
                    && Notices(mob.X, mob.Y, p.PlayerX, p.PlayerY));
                if (victim is not null) mob.TargetId = victim.PlayerId;
            }

            // Idle flavour (MobChatter.csv). RTK puts these in each mob's `move` hook — the whole
            // "custom AI" of the grim ogre is a 1-in-100 roll to grunt — so it belongs here, before
            // any of the targeting work and regardless of whether the mob is fighting.
            if (Content.MobChatter.TryGetValue(mob.Key, out var chat) && Random.Shared.Next(chat.Chance) == 0)
                chatter.Add((mapId, mob, chat.Channel, chat.Lines[Random.Shared.Next(chat.Lines.Length)]));

            // Amnesia (RTK amnesia.lua while_cast): the mob drops the player it has forgotten, then
            // re-picks from the rest of its threat table below. Checked before the retarget so the
            // forgotten player can't simply be chosen straight back.
            if (mob.AmnesiaBy != 0)
            {
                if (Environment.TickCount64 >= mob.AmnesiaUntil) { mob.AmnesiaBy = 0; mob.AmnesiaUntil = 0; }
                else if (mob.TargetId == mob.AmnesiaBy) { mob.TargetId = 0; mob.AttackTimer = 0; }
            }

            // A PASSIVE creature forgets anyone who leaves the map entirely (RTK mob_ai_basic:
            // `if mob.behavior == 0 and target.m ~= mob.m then ... setThreat(mob.ID, 0)`). An
            // aggressive one keeps the grudge banked, so walking out and back in doesn't launder it.
            if (!mob.Aggressive && mob.TargetId != 0 && m.Players.All(p => p.PlayerId != mob.TargetId))
            {
                mob.ClearThreat(mob.TargetId);
                mob.TargetId = 0; mob.AttackTimer = 0;
            }

            // Threat (RTK mob_ai_normal calls threat.calcHighestThreat at the top of both its move and
            // attack branches, so it is re-evaluated every tick a mob is in a fight — not just when it
            // is hit). Owned creatures are exempt: a pet's target comes from its owner, above.
            if (mob.Threat is { Count: > 0 } && mob.OwnerId == 0) w.RetargetByThreat(m, mob);

            // Combat AI (RTK mob_ai_normal: on_attacked sets the target; move/attack chase + swing at
            // it): a provoked mob (World.TryDamage set TargetId) abandons wandering to path toward and
            // melee its attacker instead, until the target dies/leaves/logs off or strays past
            // ChaseLeash tiles from the mob's home — then it falls back to normal wandering below.
            if (mob.TargetId != 0)
            {
                mob.Returning = false;   // RTK: `if (mob.target ~= 0) then mob.returning = false end`
                var target = m.Players.FirstOrDefault(p => p.PlayerId == mob.TargetId);
                // An OWNED creature has no leash: it belongs to a player, not to a spawn point, so
                // tethering it to the tile it was summoned on would make it quit mid-fight.
                bool inRange = target is not null && !target.IsDead
                               && (mob.OwnerId != 0
                                   // The Ice Beast is unleashed so it can be pulled onto its lava (its
                                   // spawn sits farther than ChaseLeash from those tiles); it dies the
                                   // moment it reaches them, so the pursuit is self-limiting.
                                   || mob.Key == IceBeastKey
                                   || Math.Max(Math.Abs(target.PlayerX - mob.HomeX), Math.Abs(target.PlayerY - mob.HomeY)) <= ChaseLeash);
                if (!inRange) { mob.TargetId = 0; mob.AttackTimer = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0; }
                else
                {
                    int tdx = target!.PlayerX - mob.X, tdy = target.PlayerY - mob.Y;

                    // Spellcasting (MobSpells.csv). Rolled here rather than in the swing branch below
                    // because RTK's casters do it from `move`, at their own range — a mythic boss
                    // throws lightning from five tiles out, a raven pecks from arm's reach. Cast is IN
                    // ADDITION to the swing, never instead of it (RTK's raven runs `peck.cast(...)`
                    // and then falls straight through to mob_ai_basic.attack).
                    if (Content.MobSpells.TryGetValue(mob.Key, out var repertoire)
                        && Environment.TickCount64 >= mob.SpellReadyAt)
                    {
                        int reach = Math.Max(Math.Abs(tdx), Math.Abs(tdy));
                        foreach (var sp in repertoire)
                        {
                            // An `onhit` row belongs to the swing, not to this timer — it is rolled
                            // in Session.TryMobOnHitSpell when a blow actually lands. Firing it here
                            // too would give the creature two independent chances at the same spell.
                            if (sp.OnHit) continue;
                            if (reach > sp.Range || Random.Shared.Next(Math.Max(1, sp.Chance)) != 0) continue;
                            // A `melee` row is a BONUS SWING with the creature's own weapon rather
                            // than a spell — RTK's Gim Yi (bosses/gimyi.lua) casts `ambush`, whose
                            // whole payload for an already-adjacent mob is a shout and a second
                            // `mob:attack(target.ID)`. Routing it through the normal hit queue means
                            // it uses his real damage band, hit, crit and your AC, instead of a flat
                            // number in a CSV that would drift from him the moment his stats changed.
                            // (ApplyMobSpell says the line for a real cast, so a melee row says its
                            // own — RTK's ambush uses `mob:talk(2, ...)`, the unattributed channel.)
                            if (sp.Effect == "melee")
                            {
                                hits.Add((mapId, mob, target));
                                string mSay = sp.PickSay();
                                if (mSay.Length > 0) chatter.Add((mapId, mob, (byte)2, mSay));
                            }
                            else mobCasts.Add((mob, target, sp));
                            mob.SpellReadyAt = Environment.TickCount64 + sp.EveryMs;
                            break;   // one spell per opportunity, first match in file order wins
                        }
                    }

                    // Cardinal adjacency ONLY (matches the player's own melee, which only ever checks
                    // its single FrontTile — a diagonal target is neither attackable by the player nor,
                    // now, by a mob; RTK has no 8-way reach either). A diagonal target falls through to
                    // the chase step below, which moves on a single axis and closes to cardinal in ~1 tick.
                    bool adjacent = (tdx == 0 && Math.Abs(tdy) == 1) || (tdy == 0 && Math.Abs(tdx) == 1);

                    // The Forever Tree (Man-shik) strikes ONLY its north face — RTK
                    // man_shik_forever_tree.lua attacks the single cell at (mob.x, mob.y-1), never the
                    // sides. Paired with its Stationary rooting below, that is what lets a group chop it
                    // down from the flank unharmed while anyone standing in front is flattened by its
                    // 1,000,000 damage (nexusatlas: "he hasn't been fighting back from the front, so he
                    // hasn't been too deadly … what makes him a pain is his astounding vitality").
                    if (mob.Key == "man_shik") adjacent = tdx == 0 && tdy == -1;

                    // ---- Sute's bespoke boss AI (Server/SuteAi.cs) --------------------------------
                    // The one creature that does not simply close and swing: he fights in bursts and
                    // backs off above half health, and runs below a quarter. SuteAi only DECIDES —
                    // the stepping and the swing stay here, using the same helpers as everything else,
                    // so his movement obeys the same collision, leash and trap rules as any other mob.
                    if (mob.Key == SuteAi.MobKey)
                    {
                        long nowMs = Environment.TickCount64;
                        // The wounded self-heal. Queued as an ordinary repeat-fx so the cast is
                        // visible; there is no shout because no source records one.
                        if (SuteAi.TryHeal(mob, nowMs))
                        {
                            fxRepeats.Add((mapId, mob.Id, mob.X, mob.Y, SuteAi.HealAnim, SuteAi.HealSound));
                            healthShows.Add((mapId, mob));   // …and let his bar actually climb
                        }

                        var act = SuteAi.Decide(mob, adjacent, nowMs);
                        if (act == SuteAi.Act.Hold) { mob.AttackTimer = 0; return; }
                        if (act == SuteAi.Act.Retreat || act == SuteAi.Act.Approach)
                        {
                            mob.MoveTimer += TickMs;
                            if (mob.MoveTimer < mob.MoveTime) { mob.AttackTimer = 0; return; }
                            mob.MoveTimer -= mob.MoveTime;

                            // ONE tile per turn — unlike the other two fleers he does not hop
                            // several at once (see SuteAi.StepTilesPerTurn). His speed comes from a
                            // 333ms MobMoveTime, i.e. a step every acting beat, not a longer stride.
                            // Still routed through Dart so the step rules stay shared.
                            int hops = w.Dart(act == SuteAi.Act.Retreat ? DartMode.Away : DartMode.Toward,
                                            SuteAi.StepTilesPerTurn, mapId, m, mob, target.PlayerX, target.PlayerY,
                                            dims, terrain, occupied, mobTiles, moves, turns, trapDamage);

                            if (act == SuteAi.Act.Retreat)
                            {
                                // Boxed in? Recomputed EVERY beat he tries to move, never latched —
                                // see the note on SuteAi.Phase.Flee. Latching it deadlocked him: a
                                // single blocked step made Decide return Normal forever, which meant
                                // this branch never ran again to clear the flag, and a boss on 15%
                                // health stood and fought to the death instead of running.
                                mob.SuteCornered = hops == 0;
                                mob.SuteRetreatLeft = Math.Max(0, mob.SuteRetreatLeft - hops);
                            }
                            if (hops > 0) { mob.AttackTimer = 0; return; }   // moved: no swing this beat

                            // Nowhere to go. In the wounded rout that means he turns and fights THIS
                            // beat (falling through to the swing below); above half health the
                            // cornered flag routes him to Hold on the next one instead.
                            if (mob.SutePhase != SuteAi.Phase.Flee) { mob.AttackTimer = 0; return; }
                        }
                    }

                    if (adjacent)
                    {
                        byte face = FaceDelta(tdx, tdy);
                        if (face != mob.Dir) { mob.Dir = face; turns.Add((mapId, mob.Id, face)); }
                        mob.AttackTimer += TickMs;
                        if (mob.AttackTimer >= mob.AttackTime)
                        {
                            mob.AttackTimer = 0;
                            hits.Add((mapId, mob, target));
                            // Committing to the swing is what spends a burst slot (see SuteAi.OnSwung).
                            if (mob.Key == SuteAi.MobKey) SuteAi.OnSwung(mob);
                        }
                        return;   // adjacent: swing instead of stepping
                    }

                    // A ROOTED creature (RTK's empty `move`, modelled as Stationary/!Wander) never
                    // closes the gap: it strikes only what is already adjacent and otherwise holds.
                    // The Forever Tree is the one such aggressive creature — it cannot chase you down.
                    if (!mob.Wander) { mob.AttackTimer = 0; return; }

                    mob.MoveTimer += TickMs;
                    if (mob.MoveTimer < mob.MoveTime) return;   // not this mob's turn yet
                    mob.MoveTimer -= mob.MoveTime;

                    // Step toward the target — the direction(s) that close the gap first, then a
                    // sideways shuffle. See StepMobToward: this used to be an inline greedy step that
                    // gave up when blocked, i.e. "mob stands on one tile facing you through a wall".
                    w.StepMobToward(mapId, m, mob, target.PlayerX, target.PlayerY,
                                  dims, terrain, occupied, mobTiles, moves, turns, trapDamage,
                                  out bool towardBlocked);

                    // Can't get at them at all? Look for somebody it CAN reach. This is RTK's own
                    // FindCoords fallback (`tList = mob:getObjectsInArea(BL_PC)` then a random pick,
                    // skipping GMs) — usually a no-op, since there's usually only one player nearby.
                    // Gated on Aggressive: a creature that only fights because you provoked it should
                    // keep pacing after YOU, not go find someone else. Note this can only ever hand a
                    // stuck mob a NEW victim — landing a hit re-points it at whoever hit it
                    // (World.TryDamage), so zapping something always drags its aggro back to you,
                    // wall or no wall.
                    if (towardBlocked && mob.Aggressive)
                    {
                        var reachable = m.Players.Where(p => !p.IsDead && !p.PeaceMode && p.PlayerId != mob.TargetId
                                && Notices(mob.X, mob.Y, p.PlayerX, p.PlayerY))
                            .ToList();
                        if (reachable.Count > 0)
                        {
                            mob.TargetId = reachable[Random.Shared.Next(reachable.Count)].PlayerId;
                            mob.AttackTimer = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0;
                        }
                    }
                    return;
                }
            }

            // Retaliation against a PET. ApplyMobOnMobHit points a mob at whatever pet just hit it,
            // but only when it wasn't already busy with a player — so this is purely the "a pet can't
            // beat on something with total impunity" case, not a general mob-vs-mob war. Same leash as
            // the player chase: stray too far from home and it gives up and goes back to wandering.
            if (mob.TargetId == 0 && mob.TargetMobId != 0)
            {
                var foe = m.Mobs.FirstOrDefault(o => o.Alive && o.Id == mob.TargetMobId);
                if (foe is null || Math.Max(Math.Abs(foe.X - mob.HomeX), Math.Abs(foe.Y - mob.HomeY)) > ChaseLeash)
                { mob.TargetMobId = 0; mob.AttackTimer = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0; }
                else
                {
                    int rdx = foe.X - mob.X, rdy = foe.Y - mob.Y;
                    if ((rdx == 0 && Math.Abs(rdy) == 1) || (rdy == 0 && Math.Abs(rdx) == 1))
                    {
                        byte face = FaceDelta(rdx, rdy);
                        if (face != mob.Dir) { mob.Dir = face; turns.Add((mapId, mob.Id, face)); }
                        mob.AttackTimer += TickMs;
                        if (mob.AttackTimer >= mob.AttackTime) { mob.AttackTimer = 0; mobHits.Add((mapId, mob, foe)); }
                    }
                    else
                    {
                        mob.MoveTimer += TickMs;
                        if (mob.MoveTimer >= mob.MoveTime)
                        {
                            mob.MoveTimer -= mob.MoveTime;
                            w.StepMobToward(mapId, m, mob, foe.X, foe.Y, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                        }
                    }
                    return;
                }
            }

            if (!mob.Wander) return;

            // Walk home after giving up a chase (RTK mob_ai_basic.move's `returning` block: once the
            // creature is retDist from its start it sets `mob.newMove = 250` and paths back via
            // toStart, clearing the flag on arrival).
            //
            // This is not cosmetic. A mob that chased you to the ChaseLeash edge and gave up is
            // sitting several tiles outside its wander box, and EVERY candidate tile in the wander
            // block below fails its `|nx - HomeX| <= Leash` test — so it would stand frozen on that
            // tile forever, and a pulled-and-dropped patch of a map would slowly fill up with
            // statues. It sprints back (RTK's 250ms, which at this heartbeat is a tile per tick)
            // rather than strolling, so the patch resets promptly.
            if (!mob.Returning && mob.Leash > 1
                && Math.Max(Math.Abs(mob.X - mob.HomeX), Math.Abs(mob.Y - mob.HomeY)) > mob.Leash)
                mob.Returning = true;

            if (mob.Returning)
            {
                if (mob.X == mob.HomeX && mob.Y == mob.HomeY) { mob.Returning = false; mob.MoveTimer = 0; }
                else
                {
                    w.StepMobToward(mapId, m, mob, mob.HomeX, mob.HomeY,
                                  dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                    return;
                }
            }

            mob.MoveTimer += TickMs;
            if (mob.MoveTimer < mob.MoveTime) return;   // not this mob's turn yet
            mob.MoveTimer -= mob.MoveTime;                // carry the remainder (steady cadence)

            byte oldside = mob.Dir;
            byte stepDir;
            if (Random.Shared.Next(0, 11) >= 4)          // ~64%: reconsider facing
            {
                byte side = (byte)Random.Shared.Next(4);
                mob.Dir = side;
                if (side != oldside) { turns.Add((mapId, mob.Id, side)); return; }  // just turned
                stepDir = side;                          // faced the same way -> take the step
            }
            else stepDir = mob.Dir;                       // ~36%: step straight ahead

            int nx = mob.X, ny = mob.Y;
            switch (stepDir) { case 0: ny--; break; case 1: nx++; break; case 2: ny++; break; case 3: nx--; break; }

            bool ok = nx >= 0 && ny >= 0
                      && (dims.Item1 == 0 || (nx < dims.Item1 && ny < dims.Item2))
                      && Math.Abs(nx - mob.HomeX) <= mob.Leash
                      && Math.Abs(ny - mob.HomeY) <= mob.Leash                             // leash to spawn
                      && !occupied.Contains(((ushort)nx, (ushort)ny))                     // not onto a player
                      && !mobTiles.Contains((nx, ny))                                      // not onto another mob
                      && !MobBlocked(mapId, terrain, nx, ny, stepDir);                     // pass flag / SObj wall / warp tile
            if (!ok) return;   // blocked/leashed: hold position (already facing stepDir)

            ushort ox = mob.X, oy = mob.Y;                   // SOURCE tile (see the move broadcast below)
            mobTiles.Remove((mob.X, mob.Y));                 // vacate the old tile
            mob.X = (ushort)nx; mob.Y = (ushort)ny;
            mobTiles.Add((nx, ny));                          // occupy the new one
            // Broadcast the SOURCE tile, not the destination: the 4.95 client's 0x0C walk always ends
            // one tile PAST the packet tile in the walk direction (forward-slide overshoot, proven by
            // live trace), and for a single-stepping mob there's no 0x04 commit to correct it. Sending
            // source makes client_final = source + forward(dir) = the real destination.
            moves.Add((mapId, mob.Id, ox, oy, stepDir));
            var wanderTrap = m.Traps.FirstOrDefault(t => t.X == nx && t.Y == ny && !IsPcOnlyTrap(t.Kind));
            if (wanderTrap is not null) { m.Traps.Remove(wanderTrap); w.TriggerTrapLocked(mapId, mob, wanderTrap, trapDamage); }
        }
    }

    // ---- test seams (Tests/MobAiTickTests.cs) ---------------------------------------------------------
    // Kept beside the code they open rather than in the test project, for the same reason as
    // UnderWorldLockForTest: a reader of Tick should be able to see everything that runs it.

    /// <summary>One heartbeat on the calling thread. Production runs <see cref="Tick"/> only from
    /// <see cref="TickLoop"/>; the isolation test wants the beat without the thread, and wants a throw that
    /// escapes the tick to reach it — which is exactly what the per-mob guard has to prevent.</summary>
    internal void TickOnceForTest() => Tick();

    /// <summary>A per-map context over <paramref name="q"/>, or over its own fresh queues when none is
    /// given, for driving <see cref="MobAiTick.Step"/> on one creature and reading back what it queued.
    /// Caller holds <c>_lock</c> (<see cref="UnderWorldLockForTest"/>).
    ///
    /// <para>Passing the queues in is what lets a test drive <see cref="MobAiTick.Step"/> and then
    /// <see cref="FlushTickForTest"/> over the SAME beat's queues — the only way to check that a queue the
    /// context filled is the queue the flush drains, which for the two same-typed fields
    /// (<c>HealthShows</c> and <c>ExpiredPets</c>) the compiler cannot check.</para></summary>
    internal MobTickContext MobTickContextForTest(ushort mapId, TickQueues? q = null) =>
        new(this, mapId, Map(mapId), q ?? new TickQueues());

    /// <summary>Drain one beat's queues with no beat in front of them. <see cref="TickOnceForTest"/> builds
    /// its own <see cref="TickQueues"/> and keeps them, so a test that needs to put a specific entry on a
    /// specific queue and watch what leaves the wire cannot use it.</summary>
    internal void FlushTickForTest(TickQueues q) => FlushTick(q);
}
