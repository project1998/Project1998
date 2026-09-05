using System.Diagnostics;
using Shared;

namespace Server;

/// <summary>A read-only snapshot of a player entity, so a peer can draw it without touching that
/// session's mutable state. Built under no lock (fields are only written by the owning session's
/// read-loop; a torn read at worst mis-places a peer by one tile until its next move packet).</summary>
public readonly record struct PlayerSnapshot(
    uint Id, ushort X, ushort Y, byte Dir, byte Sex, byte Face, byte Armor, byte Weapon, byte Shield, bool Mounted, bool Dead, string Name,
    byte ArmorColor = 0, ushort MorphLook = 0, byte MorphColor = 0, bool Faded = false, byte HairColor = 0);

/// <summary>A stack of an item lying on the map floor, drawn to every client on that map via 0x16
/// (Item.epf frame = <see cref="Graphic"/>). <see cref="Id"/> is the entity id (find/despawn key). Carries
/// enough to reconstruct an <see cref="Shared.InvItem"/> when a player picks it up.</summary>
public sealed class GroundItem
{
    public uint   Id;
    public int    ItemId;
    public ushort X, Y;
    public int    Amount = 1;
    public ushort Dura;
    public ushort Graphic;       // Item.epf frame (item's Icon) — the 0x16 graphic id
    public string CustomName = "";
    // Bound owner carried WITH the stack while it sits on the ground, so a bonded item (a totem helm, a subpath
    // weapon) that its owner drops stays bound to them: whoever picks it up gets it still owned by the dropper
    // and can't equip it. Empty for ordinary loot. See ItemDef.Bonded / Session.GivePlaced.
    public string Owner = "";

    // LOOTER LOCK (RTK flooritem_data.looters[] + .timer, gated by player.lua's canLoot/isYours). 0 = ordinary
    // free-for-all floor loot, which is almost everything. Non-zero means this stack was torn off a corpse and
    // belongs to that player id until LockedUntil passes — nobody else can pick it up, filch it, or grab it out
    // from under them, and the owner can pull it back from two tiles away via F1 "Recover Death Pile" even with
    // a would-be thief parked on top of it. RTK stores the drop time and adds 300s at every read; an absolute
    // Environment.TickCount64 deadline is the same rule with the arithmetic done once.
    public uint LooterId;
    public long LockedUntil;

    /// <summary>True while this stack is still reserved for someone other than <paramref name="pickerId"/>.</summary>
    public bool LockedAgainst(uint pickerId)
        => LooterId != 0 && LooterId != pickerId && Environment.TickCount64 < LockedUntil;

    /// <summary>True if this stack is <paramref name="pickerId"/>'s own death pile and the lock is still live —
    /// the only thing "Recover Death Pile" will pick up (RTK <c>isYours</c>, which tests <c>looters[1]</c>).</summary>
    public bool BelongsTo(uint pickerId)
        => LooterId != 0 && LooterId == pickerId && Environment.TickCount64 < LockedUntil;
}

/// <summary>A hidden hazard placed by a Rogue trap spell (RTK NPCs/trap/rogue_traps/*): invisible — no
/// ground graphic is ever drawn for it (unlike <see cref="GroundItem"/>) — until a mob steps onto its
/// tile, at which point its effect fires once and it's removed. See <see cref="World.PlaceTrap"/>/
/// <see cref="World.TrapAt"/> and Session.CastTrap/CastSpotTraps.</summary>
public sealed class Trap
{
    public uint   Id;
    public ushort X, Y;
    public string Kind = "";   // "dart"/"snare"/"repeating"/"flash"/"spear"/"poison"/"death"/"sleep"/"bladestorm"
    public uint   OwnerId;     // caster's player id — credited with any exp from a trap kill
    public long   ExpiresAt;   // 0 = never (the 8-kind hazard family); "bladestorm" auto-clears if untriggered
}

/// <summary>Where a player was standing when an arrival began — the tile it still holds while the move is
/// in flight. Only <see cref="ArrivalPolicy.AdjacentFreeElseStack"/> consults it; see
/// <see cref="World.PlacePlayer"/>'s <c>from</c> parameter for why it has to.</summary>
public readonly record struct FromTile(ushort Map, ushort X, ushort Y);

/// <summary>How <see cref="World.PlacePlayer"/> turns a requested arrival tile into the one the player
/// actually lands on.
///
/// <para><b>Every arrival in the tree uses one of these two today, and both reproduce exactly what that
/// caller already did</b> — this is a lock-scope change, not a behaviour one. The question of what the
/// original game did when a warp's destination was occupied is open (#99, "Needs source check"), so no
/// policy that would answer it exists yet: there is deliberately no Refuse and no "step aside on a warp".
/// When the source check lands, its answer arrives here as a third member and a change of default.</para>
/// </summary>
public enum ArrivalPolicy
{
    /// <summary>Take the requested tile, clamped to the map, occupied or not. What every warp, scripted-tile
    /// entrance, world-map hop, Gateway and GM teleport has always done — a bounds clamp was the whole of the
    /// validation. Two players through one door land on the same tile, as they always have.</summary>
    Clamp,

    /// <summary>The first free cardinal neighbour of the requested tile (N/E/S/W in that order), else the
    /// requested tile itself. Free = in bounds, not blocked by ground or an object wall, and holding neither
    /// a mob nor a player. <c>@approach</c> and <c>@bring</c> only, which is where this search used to live
    /// as <c>Session.ApproachTile</c> — reading through <c>World.PeerAt</c> and <c>World.MobAt</c>, each its
    /// own acquisition, with the write happening later under none of them.</summary>
    AdjacentFreeElseStack,
}

/// <summary>A peer and the tile it was standing on when <c>_lock</c> was last held — what the viewport
/// reconcile gates on. It exists because the reconcile CANNOT read <c>Session.PlayerX</c>/<c>PlayerY</c>
/// itself: those are two separate <c>ushort</c> reads of another session's character, and every writer of
/// them holds <c>_lock</c>, so a reader outside it can see one tile's X against the previous tile's Y. The
/// player list was already snapshotted under the lock and used outside it; this carries the coordinates in
/// the same snapshot, so the gate now sees the map exactly as the lock saw it rather than a mixture.</summary>
public readonly record struct PeerTile(Session Session, ushort X, ushort Y);

/// <summary>Why <see cref="World.TryMovePlayer"/> refused a step. Flags, not a single value, because the
/// walk log prints " mob" and " player" INDEPENDENTLY and always has: a mob and a player can share a tile
/// (a warp lands players on object tiles, and nothing keeps a summon off an occupied one), so collapsing
/// the two would quietly change a log line the ticket counts as behaviour.</summary>
[Flags]
public enum BlockReason
{
    None = 0,
    /// <summary>A living mob stands there — NPCs included, exactly as <see cref="World.MobAt"/> counts them.</summary>
    Mob = 1,
    /// <summary>A living OTHER player stands there. The mover is living too; the dead never block the living.</summary>
    Player = 2,
    /// <summary>Another GHOST stands there, and the mover is a PvP ghost. Logged as " player", like the living
    /// case — the log has never distinguished them.</summary>
    Ghost = 4,
}

/// <summary>
/// The single shared game world: every connected player and every live mob, grouped by map. One
/// instance is created in <see cref="TkListener"/> and handed to every <see cref="Session"/>, so all
/// clients observe the SAME entities — players see each other, and everyone fights the same mobs.
///
/// This replaces the old per-Session mob ownership for GAMEPLAY mobs (@summon / @rabbit). The debug
/// lab (look-lab dummies, monster/colour sweeps) stays session-local — those are single-screen
/// diagnostics that shouldn't broadcast to the whole map.
///
/// Threading: all collections are guarded by <c>_lock</c>. Socket writes NEVER happen while holding
/// the lock — broadcasts snapshot the recipient list under the lock, then send outside it, and each
/// send is exception-guarded so a peer whose socket just closed can't break a broadcast.
///
/// <c>_lock</c> is the INNER lock of the two the server has: the order is a session's state monitor
/// first, then this one (#29, Server/Session.State.cs). Nothing may enter a session while holding
/// <c>_lock</c> — the tick obeys that by queueing every session-facing call and applying it after the
/// lock is released, and <see cref="HoldsWorldLock"/> is what lets the session side assert it.
/// </summary>
public sealed partial class World
{
    private readonly object _lock = new();

    /// <summary>Whether the calling thread is inside <c>_lock</c>. Exists for the lock-order assert on the
    /// session side (Session.EnterState): the pair of locks has one legal order and this is the only way the
    /// outer one can tell it is being taken second.</summary>
    internal bool HoldsWorldLock => Monitor.IsEntered(_lock);

    /// <summary>Run <paramref name="body"/> holding <c>_lock</c> — for the test that pins the lock-order
    /// assert. Production code never needs this: every path that wants the world lock is already inside
    /// this class. Kept next to the lock it takes rather than hidden in the test project, because a reader
    /// of <c>_lock</c> should be able to see everything that acquires it.</summary>
    internal void UnderWorldLockForTest(Action body) { lock (_lock) body(); }

    // internal, not private: MobTickContext (World.MobAiTick.cs) carries one, and a field on an internal type
    // cannot be of a private one. Still a World-only type by convention; nothing outside World constructs it.
    internal sealed class MapState
    {
        public readonly List<Session> Players = new();
        public readonly List<Mob> Mobs = new();
        public readonly List<GroundItem> Items = new();
        public readonly List<Trap> Traps = new();
        // The weather state (0 clear / 1 rain / 2 snow) last BROADCAST to players on this map. Not the source
        // of truth — that is the deterministic WeatherModel (+ any zone override) — this is the cached
        // last-sent value the tick compares against on a period rollover to decide whether to re-broadcast.
        public byte Weather;
    }
    private readonly Dictionary<ushort, MapState> _maps = new();

    // Server-wide online-account registry (independent of the per-map Players lists above, which a session
    // only joins AFTER its own arrival/load logic runs). Keyed by CharacterStore.Key(username). Exists
    // solely for the duplicate-login guard: RegisterOnline lets HandleArrival atomically detect + evict a
    // stale session for the same account BEFORE loading, so a slow-to-unwind old session can never clobber
    // the new one's fresher save (SQLite's persistence is blind last-write-wins). Guarded by the same _lock
    // as everything else here — registration/eviction is rare (once per login), so sharing the lock costs
    // nothing measurable against the map operations.
    private readonly Dictionary<string, Session> _online = new();

    // The two spawn systems — the POINT roster (Spawn) and the GROUP roster (SpawnGroup) — and everything
    // that builds, materialises and refills them live in World.SpawnDirector.cs (#37). Constructed before
    // PopulateSpawns, which is the first call into it.
    private readonly SpawnDirector _spawnDirector;
    private long _tick;                                                  // heartbeat counter (TickMs each)

    /// <summary>World heartbeat period, and the unit every mob timer accumulates in — so it is also the
    /// FLOOR on how often any creature can act. Override with <c>P1998_TICK_MS</c>.
    ///
    /// <para><b>Was 600ms; now 333.</b> Lowering it does not make anything faster: every timer is
    /// <c>timer += TickMs</c> compared against a per-mob interval in real milliseconds, and the leftover is
    /// carried (<c>timer -= interval</c>) rather than reset, so a 2000ms creature still moves every 2000ms
    /// on average either way. What changes is GRANULARITY — the smallest action interval the world can
    /// express at all. At 600 the fastest possible creature managed 1.7 actions/sec, which could not
    /// represent Sute, who was observed moving and striking twice a second in a 333/333/rest rhythm (see
    /// Server/SuteAi.cs). 333 divides that rhythm exactly.</para>
    ///
    /// <para>Cost: the tick body runs ~1.8x as often. It only walks maps that have players on them, and the
    /// slow-tick watchdog (<see cref="SlowTickMs"/>, which scales off this) has never fired in this repo's
    /// logs, so the headroom was there. If it starts firing, raise this back — nothing but Sute's cadence
    /// depends on the smaller value.</para></summary>
    private static readonly int TickMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_TICK_MS"), out var tm) && tm >= 50 ? tm : 333;

    /// <summary>Poison/venom damage cadence, RTK's <c>while_cast_1500</c>. Shared by the mob DoT, the Rogue
    /// poison trap and the player-side venom, so the rate NexusAtlas quotes ("1000 damage a second") converts
    /// against one number in one place — see <see cref="Session.ReceivePoison"/>.</summary>
    public const int PoisonTickMs = 1500;
    // Fallback respawn delay for a spawn POINT whose creature somehow carries no SpawnTime (~18s, derived
    // from TickMs so it stays 18s whatever the heartbeat is). Every mob in the table does carry one, so this
    // is a floor, not the cadence: a point's
    // real delay is its own MobDef.SpawnTime — see SpawnDirector.SpawnTicksFor.
    private static readonly int RespawnTicks = Math.Max(1, 18_000 / TickMs);
    // How often the batch-refill sweep runs, in ticks. RTK's spawner NPC is an actiontime-driven NPC firing
    // about once a second; its timers are whole seconds and the shortest in the table is 2s, so sampling at
    // ~1.2s costs nothing and keeps the sweep off the per-beat hot path.
    private static readonly int BatchSweepTicks = Math.Max(1, 1_200 / TickMs);
    // Placement attempts per mob when filling a group, as a multiple of the group's cap. RTK gives up the
    // same way (`if fail >= maxMobs[z] * 4 then` treat the mob as done), which is what stops a boxed spawn
    // from spinning forever when the box is mostly wall.
    private const int PlacementTriesPerMob = 4;
    // How far a mob may wander from its spawn tile (Chebyshev). Kept small so town critters hug their
    // spawn points instead of clustering into a dense knot that constantly overlaps on screen.
    private const int WanderRadius = 2;
    // Farthest (Chebyshev, from its home tile) a provoked mob will chase an attacker before giving up and
    // resuming normal wandering — bigger than WanderRadius so a fight can range beyond the idle-hop leash,
    // but still bounded so a player can outrun pursuit rather than being chased across the whole map.
    //
    // RTK doesn't home-leash an ACTIVE chase at all: mob_ai_basic.move forces `mob.returning = false` the
    // moment `mob.target ~= 0`, so its `retDist` governs only idle wandering, and a mob with a target chases
    // it via FindCoords with no distance cap (target loss is by threat/area, not range). We keep a bound
    // deliberately, but it MUST exceed the notice box below (NoticeX/Y) — otherwise an aggressive mob that
    // spots a player at the screen edge would acquire and, on the very same tick, drop the target for being
    // past the leash, jittering in place instead of pursuing. Sized to a full screen so an off-screen
    // aggressive mob gives a real chase (user, 2026-08-24: "just off screen should pursue"); leading it this
    // far from its den still shakes it.
    private const int ChaseLeash = 16;
    // Mythic boss animations RTK hardcodes rather than carrying per-boss: Last Stand flashes 11
    // (Spells/last_stand.lua `sendAnimation(11)`), a curse shrug flashes 10 and plays no sound
    // (mob_ai_mythic.move). The heal animation/sound pair IS per-boss and lives in MobBosses.csv.
    private const int LastStandAnim  = 11;
    private const int CurseShrugAnim = 10;
    // The Ice Beast questline (Northeast Koguryo, map 3040). RTK Mobs/ice_beast.lua ends its `move` hook by
    // checking the lava row at 29-30 x 14-16 and, if it is standing on it, removing its own full health — so
    // you defeat the beast by luring it onto the lava, not by out-trading its 300k one-shot. It is UNLEASHED for
    // that (see the chase-leash test in Tick), because its spawn (29,3) is farther than ChaseLeash from the
    // lava and an ordinary mob would give up the pursuit long before reaching it. IceBeastMeltAnim is the
    // burst it flashes as it melts (RTK's sendAnimation on the same tiles).
    private const ushort IceBeastMap      = 3040;
    private const string IceBeastKey      = "ice_beast";
    private const int    IceBeastMeltAnim = 5;
    private static bool IsIceBeastLava(int x, int y) => (x == 29 || x == 30) && y >= 14 && y <= 16;
    // How far (Chebyshev, from the mob's CURRENT tile) an aggressive mob (MobDef.Aggressive, RTK MobBehavior==1)
    // scans for an unprovoked target each move tick — RTK's mob_find_target runs over a full-screen-ish area;
    // this is scoped to roughly what the player can see on their own screen (17x15 viewport, Session.InView).
    // Kept for the PET foe scan (a summon fighting what's around it), which is about reach, not screen edges.
    private const int AggroRadius = 8;
    // The box (half-extents from the mob's tile) in which a mob NOTICES a player — for unprovoked aggro, for
    // re-picking a reachable target when walled off, and for keeping threat on someone at the screen edge.
    // The drawn viewport is 19x17 (x±9, y±8 — Session.SayHalfW/H, the same rect the client renders), so a
    // plain square AggroRadius=8 fell two tiles short horizontally: a mob you could plainly see at the left or
    // right edge sat inert until you stepped closer, and one just off-screen never pursued. This is the
    // viewport PLUS one tile on every side, so behaviour reaches at least a tile past what's visible (user,
    // 2026-08-24: "extend outside the viewport slightly … at least 1 tile"; "just off screen should pursue").
    private const int NoticeX = Session.SayHalfW + 1;   // 10 — a tile past the horizontal draw edge
    private const int NoticeY = Session.SayHalfH + 1;   // 9  — a tile past the vertical draw edge
    /// <summary>Is (px,py) inside a mob at (mx,my)'s notice box — the drawn viewport plus a one-tile margin?</summary>
    private static bool Notices(int mx, int my, int px, int py) =>
        Math.Abs(px - mx) <= NoticeX && Math.Abs(py - my) <= NoticeY;
    // The mirror of AggroRadius for PREY (MobDef.Flees — rabbit, blue rooster): how close (Chebyshev) a player
    // gets before the creature starts backing away. Deliberately much shorter than the aggro scan — a rabbit
    // notices you when you're nearly on top of it, not from across the screen, otherwise a town full of them
    // would evaporate the moment anyone walked in. Doubled while the creature is panicking (see PanicMs), so a
    // swing sends it running from further off than a stroll past does.
    private const int FleeRadius = 2;
    // ---- flee DART sizes (see Dart) -------------------------------------------------------------
    // Tiles covered in ONE move turn by a fleeing creature. RTK expresses running away as several
    // `mob:move()` calls in a single script invocation rather than as a shorter timer, so distance per turn
    // — not milliseconds per tile — is the dial, and it is the same dial for all three fleers.
    //
    // PREY (rabbit, blue rooster): two tiles, from the user's own observation of the real game (2026-08-22) —
    // a rabbit you walk up to or swing at hops two spaces at once. No RTK reference exists for this: RTK
    // gives a rabbit a wolf's AI, and the Flees flag is ours (Content.LoadMobFlees).
    //   * A rabbit (MoveTime 3000) covers exactly the same ground per second as the shortened-timer version
    //     this replaced (2 tiles / 3000ms == 1 tile / 1500ms), so it is no harder to catch — only the motion
    //     changes, from a hurried walk to the hop it should always have been.
    //   * A blue rooster (MoveTime 500, already under the 600ms heartbeat) genuinely does get quicker. Its
    //     flee used to be expressible only as direction, because one tile per tick was the hard ceiling on
    //     pace; distance per turn has no such ceiling.
    public const int PreyDartTiles = 2;
    // THE WOUNDED ROUT (nine-tailed fox, Maletic, Citelam): three, straight off RTK's own
    // `mob:move() mob:move() mob:move()`. Previously approximated by stepping every heartbeat instead.
    public const int RoutDartTiles = 3;
    // How long a prey creature stays spooked after a player swings at it or damages it (World.Spook /
    // TryDamage), refreshed by each further hit. Panic doesn't lengthen the dart — it widens the
    // notice radius and keeps the creature running after you stop chasing, so a swing sends it properly away
    // instead of it settling down the moment you step back.
    private const int PanicMs = 4000;

    // Ground-item forage spawns (RTK itemspawner.lua): keep up to Max stacks of a gatherable item scattered on
    // passable tiles within a box, topped up periodically. Chestnuts fill the Kugnae farm (map 0) and a Buya
    // patch (map 330) — the tutorial's stage-3 gather. A stack is MinQty..MaxQty items on one tile.
    // Forage spawn boxes are data-driven (game-data/ForageAreas.csv -> Content.ForageAreas); hot-reloads
    // via @reload. See TopUpForageLocked.
    private static readonly int ForageTicks = Math.Max(1, 18_000 / TickMs);   // top up ~every 18s, like RTK's periodic itemspawner

    // ---- world calendar (opcode 0x20) ---------------------------------------------------------
    // The calendar itself lives in Shared.GameCalendar — a pure function of wall-clock time since a fixed
    // epoch, with RTK's own cadence constants and the reasoning for deriving rather than counting. It is in
    // Shared because the LOGIN server, a separate process with no World, stamps a new character's "Born in
    // ..." legend with the same date this server is showing.
    //
    // What World adds is the broadcast: RTK's change_time_char (map.c:1661) pushes clif_sendtime to every
    // connected session on each in-game hour, so we cache the calendar and watch for the hour to roll over.
    // Only hour+year go on the wire (see Session.SendTime); day/season are tracked because the year cadence
    // is defined in terms of them. Nothing reports the season to a player any more (@time is gone) — it
    // reaches them only through legend text (GameCalendar.Stamp) and whatever scripts read it.
    private int _hour, _day = 1, _season = 1, _year = 1;
    private long _gameHour = -1;          // whole in-game hours since the epoch; -1 = not yet synced
    private int? _hourOverride;           // @clock pin: when set, this hour REPLACES the derived one
    public (byte hour, byte year) Time => ((byte)_hour, (byte)_year);
    public string SeasonName => GameCalendar.SeasonName(_season);
    public (int hour, int day, int year) ClockNow => (_hour, _day, _year);
    public int? HourOverride => _hourOverride;

    /// <summary>Pin the shared in-game hour (@clock), or release it (null). The day/season/year keep
    /// deriving from the real epoch — only the HOUR is pinned, because the hour is what gates behavior
    /// (totem-time windows). Forcing <c>_gameHour = -1</c> makes the next tick's <see cref="SyncClock"/>
    /// report a change, so every session gets a fresh 0x20 within one tick in both directions.</summary>
    public void SetHourOverride(int? hour)
    {
        lock (_lock)
        {
            _hourOverride = hour;
            _gameHour = -1;
            if (hour is int h) _hour = h;   // immediate, so a readout or IsTotemTime right after is correct
        }
    }

    /// <summary>Re-read the calendar; true when the in-game hour changed, i.e. it is time to broadcast
    /// <c>0x20</c>.</summary>
    private bool SyncClock()
    {
        long gameHour = GameCalendar.HoursNow();
        if (gameHour == _gameHour) return false;
        _gameHour = gameHour;
        (_hour, _day, _season, _year) = GameCalendar.At(gameHour);
        if (_hourOverride is int oh) _hour = oh;   // @clock pin wins over the derived hour
        return true;
    }

    /// <summary>Whether the shared world clock is currently in <paramref name="totem"/>'s totem time
    /// (RTK isTotemTime) — the +5% kill-exp window. Reads the live hour; see <see cref="Content.IsTotemTime"/>.</summary>
    public bool IsTotemTime(int totem) => Content.IsTotemTime(_hour, totem);

    // ---- weather (opcode 0x1F / RTK clif_sendweather) ------------------------------------------
    // Weather is now a deterministic function of region-zone + time-period + season (see WeatherModel) rather
    // than the old per-map random roll: it is identical for every player, survives restarts, persists while a
    // player steps indoors, and is driven by the season. This world only (a) broadcasts a change when the
    // weather PERIOD rolls over for an active map and (b) holds optional admin OVERRIDES set via @weather.
    // 0=clear, 1=WRAIN(rain), 2=WSNOW(snow) — the three states the 4.95 client can draw.
    private static readonly int AdviceTicks = Math.Max(1, 900_000 / TickMs);   // ~15 minutes — the "Listen to advice" hint cadence (RTK pc_timer)

    // Admin/debug weather overrides keyed by WeatherModel.ZoneOf(map). When present, a zone shows this state
    // instead of the seasonal model until "@weather auto" clears it (indoors still wins → clear). Guarded by _lock.
    private readonly Dictionary<int, byte> _weatherOverride = new();
    private long _lastWeatherPeriod = -1;          // last WeatherModel period broadcast; -1 forces the first tick to sync

    // Effects raised from inside the lock (a boss shrugging off a killing blow, say) and flushed by the next
    // Tick — TryDamage can't broadcast where it stands, and its callers only know how to draw the damage.
    private readonly List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> _deferredFx = new();
    // Sprung traps whose spot-traps MARKER still has to be rubbed out on every client that revealed it (RTK
    // removeTrapItem, called by every trap NPC right before it deletes itself). Filled from inside the tick's
    // _lock by TriggerTrapLocked, flushed with _deferredFx once the lock is released — the clear is socket I/O.
    private readonly List<(ushort map, uint trapId)> _deferredTrapClears = new();

    // Lua mob hooks raised from inside the lock, run by the next Tick OUTSIDE it. This queue is the whole
    // reason MobScript is safe: a hook is free to speak, heal, vanish or touch a player's quest registry,
    // all of which re-enter the world — doing that while still holding _lock would deadlock it.
    private readonly List<(string key, string hook, ushort map, Mob mob, Session? actor)> _hooks = new();

    /// <summary>Queue a Lua AI hook for the creature, if it defines that hook. Cheap (one hash lookup) for
    /// the overwhelming majority of mobs, which define none. Safe to call under <c>_lock</c>.</summary>
    private void QueueHook(string hook, ushort map, Mob mob, Session? actor)
    {
        if (MobScript.Has(mob.Key, hook)) _hooks.Add((mob.Key, hook, map, mob, actor));
    }

    /// <summary>Point a creature at whoever has earned it (RTK <c>threat.calcHighestThreat</c>). Two rules,
    /// and the order is the point:
    /// <list type="number">
    /// <item><b>Cornered.</b> If the mob is boxed in by players and the one it is fighting isn't within
    /// reach, it turns on the highest-threat player it CAN reach. This is what stops a mob standing in a
    /// crowd swinging uselessly at someone behind a wall.</item>
    /// <item><b>Otherwise</b> it fights whoever has hurt it most, anywhere in sight.</item>
    /// </list>
    /// Threat only ever accrues from damage, so this can never make a mob attack an innocent bystander —
    /// the worst it does is move aggro between people already in the fight, which is the intent (a group
    /// CAN peel a mob off whoever pulled it, by out-damaging them).
    /// <para>Callers hold <c>_lock</c>. Players who have left the map simply aren't considered; their threat
    /// stays banked in case they come back, exactly as RTK's per-mob table does.</para></summary>
    private void RetargetByThreat(MapState m, Mob mob)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        bool Adjacent(Session p) =>
            (p.PlayerX == mob.X && Math.Abs(p.PlayerY - mob.Y) == 1) ||
            (p.PlayerY == mob.Y && Math.Abs(p.PlayerX - mob.X) == 1);

        // Rule 1's precondition: someone is in arm's reach and the current target is not.
        bool cornered = false;
        if (mob.TargetId != 0)
        {
            var current = m.Players.FirstOrDefault(p => p.PlayerId == mob.TargetId);
            if (current is null || !Adjacent(current))
                cornered = m.Players.Any(p => !p.IsDead && Adjacent(p));
        }

        long now = Environment.TickCount64;
        Session? best = null;
        long bestThreat = 0;
        foreach (var p in m.Players)
        {
            if (p.IsDead) continue;
            // RTK's non-cornered scan is `mob:getObjectsInArea(BL_PC)` — what the creature can see, not the
            // whole map. Without the bound a mob would swap onto someone who hurt it once and then walked to
            // the far side of the level, and chase a player it has no way of knowing is there.
            if (!Notices(mob.X, mob.Y, p.PlayerX, p.PlayerY)) continue;
            if (cornered && !Adjacent(p)) continue;
            if (mob.HasForgotten(p.PlayerId, now)) continue;   // Amnesia: this one isn't here as far as it knows
            long t = mob.ThreatOf(p.PlayerId);
            if (t > bestThreat) { bestThreat = t; best = p; }
        }

        if (best is null || best.PlayerId == mob.TargetId) return;
        mob.TargetId = best.PlayerId;
        mob.TargetMobId = 0;
        mob.DetourDir = NoDetour;
        mob.DetourLeft = 0;
    }

    // Facing (0=N 1=E 2=S 3=W) toward a delta, preferring the larger axis — used to turn a mob to face
    // whatever it's about to melee.
    private static byte FaceDelta(int dx, int dy) =>
        Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? (byte)1 : (byte)3) : (dy >= 0 ? (byte)2 : (byte)0);

    // Disjoint entity-id pools so a player id can never collide with a shared-mob id.
    //   players:     1 ..            (bound to each client's camera via 0x05)
    //   world mobs:  100000 ..       (session-local debug dummies use their own 5000+ pool, invisible
    //                                 to other clients, so those ranges never need to be globally unique)
    //   ground items: 500000 ..    (disjoint from players + mobs so a floor-item id never collides)
    private uint _nextPlayerId = 1;
    private uint _nextMobId = 100_000;
    private uint _nextNpcId = 300_000;    // NPCs get their own id band (disjoint from mobs) so a click can tell them apart

    // The NpcDef each placed NPC was built from, so @reload can tell an edited row from an unchanged one
    // (see ReconcileNpcToggles). Guarded by _lock, same as the maps it mirrors.
    private readonly Dictionary<int, NpcDef> _npcPlaced = new();
    private uint _nextItemId = 500_000;

    /// <summary>The scheduled-restart clock (@restart, or the run/restart_at file a deploy writes). Kept on
    /// the World because a restart warning is a server-wide broadcast and AllPlayers lives here.</summary>
    public RestartSchedule Restarts { get; }

    /// <summary>Builds the world's in-memory state and NOTHING that runs on its own: no tick thread, no
    /// autosave sweep, no watchdog, no restart scheduler, no status writer. Everything with a heartbeat is
    /// in <see cref="Start"/>, which the process entry point calls (Net.cs) and a test does not — that split
    /// is what lets a test hold a real World without the server's background machinery attached to it.</summary>
    public World()
    {
        _spawnDirector = new SpawnDirector(this);
        PopulateSpawns();                 // build the persistent roster from Content.Spawns (needs Content.Load first)
        PopulateNpcs();                   // place the stationary NPCs (Content.Npcs) as non-fighting mobs
        SyncClock();                      // derive the in-game calendar from the fixed real-world epoch
        Log.Info($"=== clock: Yuri {_year}, {SeasonName}, day {_day}, hour {_hour}:00");
        Restarts = new RestartSchedule(this);
    }

    private int _started;   // 0 until Start() has run; makes a second call a no-op instead of a second tick thread

    /// <summary>Whether <see cref="Start"/> has run — i.e. whether this world has threads attached to the
    /// process. Public so a test can assert the guarantee the constructor makes, rather than trusting it.</summary>
    public bool IsStarted => Volatile.Read(ref _started) != 0;

    /// <summary>Start the background machinery: the tick and autosave threads, the watchdog probes, the
    /// restart ladder and the status writer. Idempotent — a second call is a no-op rather than a duplicate
    /// set of threads.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        // DEDICATED THREADS, not Task.Run. Both of these used to be thread-pool work items, which put the
        // world heartbeat behind every other pool item in the process: session read-loop continuations, the
        // synchronous SQLite saves below, Lua, and any stray blocking call. When the pool ran out of threads
        // the runtime injected replacements at only ~1-2 per second, and the tick simply did not run in the
        // meantime — a multi-second, self-recovering freeze of the entire world with nothing in the log to
        // show for it. A dedicated thread cannot be starved by pool pressure.
        new Thread(TickLoop)     { IsBackground = true, Name = "world-tick" }.Start();
        new Thread(AutoSaveLoop) { IsBackground = true, Name = "world-autosave" }.Start();

        // Pool headroom + the pool-latency and client-silence probes. Started here because this is the
        // first point where a World exists for the silence scanner to walk.
        Watchdog.RaiseMinThreads();
        Watchdog.Start(this);

        _ = Task.Run(Restarts.Loop);      // restart-warning ladder + the deploy's file trigger (1s cadence, not latency-critical)
        _ = Task.Run(() => StatusFile.Loop(this));   // run/status.json for the launcher's "N online" pill
    }

    // ---- persistent spawn roster --------------------------------------------------------------

    /// <summary>Build the persistent spawn-point roster from the static table (<see cref="Content.Spawns"/>,
    /// fixed tiles) and the Lua area spawns (<see cref="Content.AreaSpawns"/>, a count of mobs per map/box).
    /// Runs once at startup (Content is already loaded). This only builds cheap point objects — no mob is
    /// instantiated and no map file is read until the first player enters that map (<see cref="SpawnDirector.EnsureMaterialized"/>),
    /// so the ~21k hunting-map mobs don't flood memory or stall boot. Dead points refill via <see cref="Tick"/>.</summary>
    private void PopulateSpawns()
    {
        int points, skipped, groups, capped;
        lock (_lock) (points, skipped, groups, capped) = _spawnDirector.Build();
        Log.Info($"spawns: {points} spawn points (materialized lazily) across {_spawnDirector.PointMapCount} map(s)" +
                 (skipped > 0 ? $" ({skipped} skipped — unknown map/mob)" : ""));
        Log.Info($"spawns: {groups} batch groups capping {capped} mobs across {_spawnDirector.GroupMapCount} map(s)");
    }

    /// <summary>Every tile on a map that something is standing on. Built once per refill sweep and updated as
    /// mobs are placed: the placement test runs up to four times the group's cap, and the big woodcutting maps
    /// cap at 500, so re-scanning the map's entity lists per attempt would be a quarter-million comparisons
    /// under the world lock. Caller holds <c>_lock</c>.</summary>
    private HashSet<(int, int)> OccupiedTiles(ushort mapId)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var map = Map(mapId);
        var taken = new HashSet<(int, int)>(map.Mobs.Count + map.Players.Count);
        foreach (var m in map.Mobs) if (m.Alive) taken.Add((m.X, m.Y));
        foreach (var p in map.Players) taken.Add((p.PlayerX, p.PlayerY));
        return taken;
    }

    /// <summary>Place every stationary NPC (Content.Npcs) into the world as a non-fighting mob. NPCs ride
    /// the exact same 0x07 creature render + viewport streaming as a real mob (see Session.ShowMob/SyncMobs),
    /// so they render + stream for free; they simply never wander, never respawn, and can't be damaged
    /// (World.TryDamage rejects <see cref="Mob.IsNpc"/>). Clicking one opens its dialog (Session.HandleClickInfo).
    /// Runs once at startup after Content.Load; the NPC's home tile is its spawn tile and it holds position.</summary>
    private void PopulateNpcs()
    {
        int placed = 0;
        lock (_lock)
        {
            foreach (var n in Content.Npcs)
            {
                if (!n.Enabled) continue;   // switched off in NPCs.csv (Enabled=0)
                PlaceNpc(n);
                placed++;
            }
        }
        Log.Info($"npcs: {placed} stationary NPC(s) placed");
    }

    /// <summary>Instantiate one NPC def as a stationary (or pacing) non-fighting mob and add it to its map.
    /// Shared by startup placement and the live <see cref="EnableNpc"/> toggle. Caller holds <c>_lock</c>.</summary>
    private void PlaceNpc(NpcDef n)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        // Don't stack on a mob spawn sharing the tile, but DO stand where NPCs.csv says, wall or not.
        var (nx, ny) = FreeSpawnTile(n.Map, n.X, n.Y, avoidSolid: false);
        _npcPlaced[n.Id] = n;   // the def this instance was built from — see ReconcileNpcToggles
        // RTK gives some NPCs (animals, town dogs, roaming merchants) a MoveTime + ReturnDistance so they
        // pace; the rest stand still. A leash of 0 means "don't stray", i.e. stationary.
        bool paces = n.MoveTime > 0 && n.ReturnDistance > 0;
        var npc = new Mob(_nextNpcId++, n.Look, nx, ny, n.Name, hp: 1)
        {
            IsNpc = true, NpcDefId = n.Id, Color = n.Color, Dir = n.Dir,
            Wander = paces, MoveTime = paces ? n.MoveTime : 2500, Leash = n.ReturnDistance,
        };
        Map(n.Map).Mobs.Add(npc);
    }

    /// <summary>Remove every placed instance of NPC def <paramref name="npcId"/> from the world and despawn
    /// it (0x0E) for everyone watching. Returns how many instances were removed. Called by
    /// <see cref="ReconcileNpcToggles"/> on <c>@reload</c> — toggling is config (the Enabled column of
    /// NPCs.csv), not a live GM action, so this has no separate persistence step of its own.</summary>
    public int DisableNpc(int npcId)
    {
        var removed = new List<(ushort map, uint id)>();
        lock (_lock)
        {
            foreach (var (mapId, m) in _maps)
            {
                var gone = m.Mobs.Where(x => x.IsNpc && x.NpcDefId == npcId).ToList();
                foreach (var g in gone) { m.Mobs.Remove(g); removed.Add((mapId, g.Id)); }
            }
            _npcPlaced.Remove(npcId);
        }
        foreach (var (map, id) in removed)
            Broadcast(map, p => p.DespawnEntity(id));   // socket I/O — outside the lock
        return removed.Count;
    }

    /// <summary>Place NPC def <paramref name="npcId"/> back into the world (idempotent — a no-op if it's
    /// already placed). The periodic viewport sync streams it to anyone in range. Returns true if it was
    /// placed. See <see cref="DisableNpc"/>.</summary>
    public bool EnableNpc(int npcId)
    {
        lock (_lock)
        {
            foreach (var (_, m) in _maps)
                if (m.Mobs.Any(x => x.IsNpc && x.NpcDefId == npcId)) return false;   // already present
            var def = Content.Npcs.FirstOrDefault(n => n.Id == npcId);
            if (def is null) return false;
            PlaceNpc(def);
        }
        return true;
    }

    /// <summary>Hot-reload hook (the <c>@reload</c> command, after <see cref="Content.Reload"/> re-reads
    /// <c>NPCs.csv</c>): re-sync stationary-NPC placement against the just-reloaded defs — spawns any NPC newly
    /// enabled, despawns any newly disabled, and re-places any whose def CHANGED. That last case used to be
    /// missed entirely: <see cref="EnableNpc"/> returns false as soon as the NPC is placed at all, so an edited
    /// tile (or look, or colour) sat in the CSV doing nothing until the next full restart, and the world quietly
    /// disagreed with the file. <see cref="NpcDef"/> is a record, so one structural compare covers every column.
    /// Returns how many NPCs' placement changed.</summary>
    public int ReconcileNpcToggles()
    {
        int changed = 0;
        foreach (var n in Content.Npcs)
        {
            if (!n.Enabled) { if (DisableNpc(n.Id) > 0) changed++; continue; }

            bool moved;
            lock (_lock) moved = _npcPlaced.TryGetValue(n.Id, out var prev) && !prev.Equals(n);
            if (moved) DisableNpc(n.Id);          // drop the stale instance, then fall through and re-place it
            if (EnableNpc(n.Id)) changed++;
        }
        return changed;
    }

    /// <summary>Build one live creature, add it to the map and fire its spawn hook. Shared by both spawn
    /// systems so a batch-spawned cave mob is identical in every respect to a point-spawned town one — the
    /// only difference between the two is what decides WHEN and WHERE. Home defaults to the spawn tile.
    /// Caller holds <c>_lock</c>.</summary>
    private Mob BuildMob(ushort mapId, MobDef d, ushort x, ushort y, ushort? homeX = null, ushort? homeY = null)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var mob = new Mob(_nextMobId++, d.Look, x, y, d.Name, d.Hp)
        {
            // Color byte = RTK's MobLookColor. (The client Monster.tbl palette turned out wrong here — it
            // rendered every mob green — so we use RTK's per-mob colour, which matches for most creatures.)
            Key = d.Key, DefId = d.Id,   // identifier for quest kill-matching, id for spawn-group counting
            Color = d.Color, Exp = d.Exp, Level = d.Level, Will = d.Will, Aggressive = d.Aggressive, Flees = d.Flees,
            MinDam = d.MinDam, MaxDam = d.MaxDam, Hit = d.Hit, IsBoss = d.IsBoss, Protection = d.Protection, Ac = d.Ac, Grace = d.Grace,
            // Wander is the default; MobStationary.csv opts a creature out (a penned captive whose RTK AI
            // script only ever turns on the spot — see Content.LoadMobStationary).
            Dir = 2, HomeX = homeX ?? x, HomeY = homeY ?? y, Wander = !d.Stationary, Leash = WanderRadius,
            MoveTime = d.MoveTime, MoveTimer = Random.Shared.Next(d.MoveTime),   // stagger so they don't all step at once
            WorldSpawned = true,           // placed by the world, so it drops loot (see Mob.WorldSpawned)
        };

        // Spawn HP jitter (RTK AI/mob_on_spawn.lua — the DEFAULT on_spawn every creature without its own
        // gets): max HP moves by up to +/-(minDam + maxDam) * 2, so two of the same creature are never
        // quite the same fight. Floored at 1 — RTK's own version can drive a small, hard-hitting mob to
        // zero HP, which would spawn it already dead.
        if (Content.MobHpJitter)
        {
            int swing = Math.Max(1, (d.MinDam + d.MaxDam) * 2);
            // The jitter is scaled by DAMAGE, which assumes damage and HP share a rough scale — true of an
            // ordinary creature. A hard-hitter whose swing dwarfs its own HP (the Ice Beast one-shots for
            // 300k but has only 10k HP) would otherwise have its HP scrambled to anywhere from 1 to ~1.2M,
            // half the time spawning AT 1 HP — which for the Ice Beast would let a player one-hit it and
            // skip the lava-lure entirely. Only jitter when the swing fits inside the mob's own health.
            if (swing < mob.MaxHp)
            {
                int delta = Random.Shared.Next(1, swing + 1) * (Random.Shared.Next(2) == 0 ? 1 : -1);
                mob.MaxHp = Math.Max(1, mob.MaxHp + delta);
                mob.Hp = mob.MaxHp;
            }
        }
        Map(mapId).Mobs.Add(mob);
        QueueHook(MobScript.OnSpawn, mapId, mob, null);
        return mob;
    }

    /// <summary>The spawn tile if it's open, else the nearest tile (within 2) that's in-bounds, not already
    /// occupied by a live mob, and — for a real creature — not solid, so two spawns on one tile (or a respawn
    /// onto a wanderer) don't stack. Falls back to the spawn tile if everything nearby is taken.
    ///
    /// <paramref name="avoidSolid"/> is false for NPCs: standing on solid ground is NORMAL for them and has to
    /// be honoured, not corrected. RTK's own authored placements do it (Mignok 4716(4,9) and Tominaru 4716(13,8)
    /// are both wall tiles), it's how an NPC stands behind a counter or on a shrine block, and nothing about an
    /// NPC needs walkable ground: it renders through the same 0x07 creature path as any mob and
    /// <see cref="Session.HandleClickInfo"/> opens its dialog by entity id with no adjacency check. Bumping them
    /// silently moved every such NPC a few tiles off its authored spot. Caller holds <c>_lock</c>.</summary>
    private (ushort x, ushort y) FreeSpawnTile(ushort mapId, ushort x, ushort y, bool avoidSolid = true)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var m = Map(mapId);
        var dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        var terrain = dims.Item1 > 0 ? MapData.For(mapId, dims.Item1, dims.Item2) : null;

        bool Free(int tx, int ty)
        {
            if (tx < 0 || ty < 0 || (dims.Item1 > 0 && (tx >= dims.Item1 || ty >= dims.Item2))) return false;
            if (avoidSolid && terrain is not null && terrain.Solid(tx, ty)) return false;
            foreach (var mo in m.Mobs) if (mo.Alive && mo.X == tx && mo.Y == ty) return false;
            return true;
        }

        if (Free(x, y)) return (x, y);
        for (int r = 1; r <= 2; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;   // walk the ring at radius r
                    if (Free(x + dx, y + dy)) return ((ushort)(x + dx), (ushort)(y + dy));
                }
        return (x, y);   // everything nearby is taken — accept the overlap rather than drop the mob
    }

    // Refill every forage box to its target stack count on random passable tiles (RTK itemspawner.lua:
    // count existing stacks of the item in the box, drop the shortfall). Runs under _lock; returns the new
    // drops (with their map) so the caller can broadcast them once the lock is released.
    private List<(ushort map, GroundItem gi)>? TopUpForageLocked()
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        List<(ushort, GroundItem)>? drops = null;
        foreach (var area in Content.ForageAreas)
        {
            if (!_maps.TryGetValue(area.Map, out var m) || m.Players.Count == 0) continue;   // no one watching
            var def = Content.ItemByKey(area.ItemKey);
            if (def is null) continue;

            int have = m.Items.Count(gi => gi.ItemId == def.Id &&
                                           gi.X >= area.MinX && gi.X <= area.MaxX &&
                                           gi.Y >= area.MinY && gi.Y <= area.MaxY);
            int need = area.Max - have;
            if (need <= 0) continue;

            var (xs, ys) = Content.Maps.TryGetValue(area.Map, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
            var terrain = xs > 0 ? MapData.For(area.Map, xs, ys) : null;
            for (int i = 0; i < need; i++)
            {
                int tx = Random.Shared.Next(area.MinX, area.MaxX + 1);
                int ty = Random.Shared.Next(area.MinY, area.MaxY + 1);
                if (terrain is not null && terrain.Solid(tx, ty)) continue;   // passable tiles only (getPass==0)
                var gi = new GroundItem
                {
                    Id = _nextItemId++, ItemId = def.Id, X = (ushort)tx, Y = (ushort)ty,
                    Amount = Random.Shared.Next(area.MinQty, area.MaxQty + 1), Graphic = def.Icon,
                };
                m.Items.Add(gi);
                (drops ??= new()).Add((area.Map, gi));
            }
        }
        return drops;
    }

    public uint AllocatePlayerId() { lock (_lock) return _nextPlayerId++; }
    public uint AllocateMobId()    { lock (_lock) return _nextMobId++; }
    public uint AllocateItemId()   { lock (_lock) return _nextItemId++; }

    private uint _nextTrapId = 1;

    /// <summary>Place a hidden trap (see <see cref="Trap"/>). Never broadcast — traps have no ground
    /// graphic; only <see cref="TrapsNear"/> (spot_traps) ever reveals one, and only to its caster.</summary>
    public Trap PlaceTrap(ushort mapId, ushort x, ushort y, string kind, uint ownerId, long expiresAt = 0)
    {
        var t = new Trap { Id = _nextTrapId++, X = x, Y = y, Kind = kind, OwnerId = ownerId, ExpiresAt = expiresAt };
        lock (_lock) Map(mapId).Traps.Add(t);
        return t;
    }

    // RTK bladestorm_trap.lua's block.side -> {x[],y[]} table: 4 tiles fanned out AHEAD of the TRIGGER's own
    // facing (0=N/1=E/2=S/3=W, this codebase's usual Dir convention) — not the caster's facing at cast time.
    private static readonly (int dx, int dy)[][] BladestormFan =
    {
        new[] { (0,-1), (-1,-2), (0,-2), (1,-2) },   // dir 0 = north
        new[] { (1,0), (2,-1), (2,0), (2,1) },       // dir 1 = east
        new[] { (0,1), (-1,2), (0,2), (1,2) },       // dir 2 = south
        new[] { (-1,0), (-2,1), (-2,0), (-2,-1) },   // dir 3 = west
    };

    /// <summary>Bladestorm's PC-trigger path (see Content.IsBladestormTrap) — the only trap kind a PLAYER can
    /// set off; the hazard family (dart/snare/…) stays mob-only. Called from Session.HandleWalk right after a
    /// successful step commits the new tile — HandleWalk holds no lock of its own, so the resulting damage
    /// can be applied directly here with no deferred queue (unlike the mob-trigger case in
    /// TriggerTrapLocked, which fires from inside World.Tick's own lock).</summary>
    public void CheckPlayerTrapTrigger(Session player, ushort mapId, ushort x, ushort y, byte facing)
    {
        Trap? trap;
        var coneTargets = new List<Mob>();
        string? ambushMsg = null;
        lock (_lock)
        {
            var m = Map(mapId);
            // The trap kinds a PLAYER sets off: bladestorm (an AoE decoy), shiver (a cosmetic fall echo — see
            // Session.TryMythicFallRoom), and ambush (a cave mob-spawn tile — see Content.AmbushMapDef). The
            // hazard family (dart/snare/…) stays mob-only.
            trap = m.Traps.FirstOrDefault(t => (t.Kind == "bladestorm" || t.Kind == "shiver" || t.Kind == "ambush"
                                                || t.Kind == SuteAi.FrigidTrapKind) && t.X == x && t.Y == y);
            if (trap is null) return;
            m.Traps.Remove(trap);
            if (trap.Kind == SuteAi.FrigidTrapKind) RefillFrigidLocked(mapId);   // relocate the sprung tile
            if (trap.Kind == "bladestorm")
                foreach (var (dx, dy) in BladestormFan[facing & 3])
                {
                    var t = m.Mobs.FirstOrDefault(o => o.Alive && o.X == x + dx && o.Y == y + dy);
                    if (t is not null) coneTargets.Add(t);
                }
            else if (trap.Kind == "ambush")
                ambushMsg = FireAmbushLocked(mapId, x, y);   // spawns the burst + relocates a replacement trap
        }
        // The trap is gone, so its revealed marker goes with it (RTK removeTrapItem before npc:delete) — on
        // every client that spotted it, not just the stepper's. Runs for all three PC-triggerable kinds; only
        // ambush is revealable today, so for the other two it's a no-op on every session.
        Broadcast(mapId, p => p.ClearTrapMarker(trap.Id));
        // Shiver is pure flavor: sense the sprung trap, nothing else (RTK WarpTrapShiverNpc).
        if (trap.Kind == "shiver") { player.FeelShiver(); return; }
        // Sute's Cave cold tile: a flat hit and its own line. Its replacement was already placed under the
        // lock above, so the room does not get safer as it is walked.
        if (trap.Kind == SuteAi.FrigidTrapKind) { player.TakeFrigidBlast(); return; }
        // Ambush: the burst mobs are already on the map (built under the lock above); the caller's post-step
        // SyncMobs renders them for the stepper. Just deliver the "Rabbits ambush you!" style status line.
        if (trap.Kind == "ambush") { if (ambushMsg is not null) player.ShowAmbushText(ambushMsg); return; }
        // Bladestorm: ONE damage number, computed from the trigger (RTK applies it uniformly, not per-target) —
        // Session owns the armor/HP math for a player trigger and caps its OWN loss to leave 1 HP; the cone
        // targets it catches take the same (uncapped) value via the existing trap-damage pipeline.
        int dmg = player.ApplyBladestormSelfDamage();
        foreach (var t in coneTargets) Try(() => ApplyTrapDamage(mapId, t, dmg, player.PlayerId), "ApplyTrapDamage (bladestorm cone)");
    }

    // ---- Ambush traps (Content.Ambushes / AmbushBursts; RTK mob_spawn.lua + rabbitTrap.lua + tigerTrap.lua) ----
    // A hidden "ambush" trap tile a player steps on spawns a burst of cave mobs around them, then a replacement
    // trap is placed elsewhere (while live mobs stay under the map's cap). Caller holds _lock; the burst mobs
    // are built here (BuildMob needs the lock) and the stepper's own post-step SyncMobs renders them. Returns
    // the ambush status message to show the stepper. Bosses are NOT rolled here — they stay on the rare
    // spawn-point system (AreaSpawnsTrap), which already reproduces their 1/10 + cooldown surprise.
    private string? FireAmbushLocked(ushort mapId, ushort x, ushort y)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        if (!Content.Ambushes.TryGetValue(mapId, out var cfg)) return null;
        bool onStepper = cfg.PrimaryKind == "single";   // RTK block:spawn(id, block.x, block.y) — see AmbushBurstTile
        int slot = 0;
        foreach (var mobId in SelectAmbushBurst(cfg, y))
        {
            var def = Content.MobById(mobId);
            int i = slot++;                              // consumed even if the id doesn't resolve — RTK indexes the LIST
            if (def is null) continue;
            var (sx, sy) = onStepper ? (x, y) : AmbushBurstTile(mapId, x, y, i);
            BuildMob(mapId, def, sx, sy);
        }
        RefillAmbushLocked(mapId);   // relocate the sprung trap (and top up) while under the mob cap
        return cfg.Message;
    }

    /// <summary>Where the <paramref name="index"/>'th member of a burst lands: RTK spreads them over the four
    /// tiles AROUND the stepper in a fixed order — east, west, north, south — and only drops one on the
    /// stepper's OWN tile when that neighbour is unusable (wall, occupied, off-map or a warp). Members past
    /// the fourth (a 5-strong sentry pack) land on the stepper too, exactly as the Lua's trailing <c>else</c>
    /// does. See rabbitTrap.lua <c>spawnRabbitMobN</c> / mob_spawn.lua <c>spawnMob</c>: each z-index has its
    /// own hard-coded neighbour and its own <c>getPass</c>/<c>getObjectsInCell</c>/<c>getWarp</c> guard.
    ///
    /// <para>Deliberately NOT <see cref="FreeSpawnTile"/>. That helper treats the trigger tile as free when no
    /// MOB is on it — it never looks at players — so the first member of every burst spawned directly under
    /// the stepper's feet before the rest ringed outward: the reported "mobs spawn around me, but one is
    /// stacked on top of me". The single-creature caves (Kugnae trapdoor spider, Buya scorpion lurker) still
    /// land on the stepper, because RTK's own <c>block:spawn(102, block.x, block.y, 1)</c> does — one creature
    /// dropping onto you is that trap, not this bug.</para>
    ///
    /// Caller holds <c>_lock</c>.</summary>
    private (ushort x, ushort y) AmbushBurstTile(ushort mapId, ushort x, ushort y, int index)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var (dx, dy) = index switch
        {
            0 => (1, 0),    // east
            1 => (-1, 0),   // west
            2 => (0, -1),   // north
            3 => (0, 1),    // south
            _ => (0, 0),    // 5th and beyond: on the stepper (RTK's `else block:spawn(mob, block.x, block.y)`)
        };
        if (dx == 0 && dy == 0) return (x, y);

        int nx = x + dx, ny = y + dy;
        var (xs, ys) = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        if (nx < 0 || ny < 0 || (xs > 0 && (nx >= xs || ny >= ys))) return (x, y);
        var terrain = xs > 0 ? MapData.For(mapId, xs, ys) : null;
        if (terrain is not null && terrain.Solid(nx, ny)) return (x, y);
        if (Content.TryWarp(mapId, (ushort)nx, (ushort)ny, out _)) return (x, y);
        var m = Map(mapId);
        foreach (var mo in m.Mobs) if (mo.Alive && mo.X == nx && mo.Y == ny) return (x, y);
        foreach (var p in m.Players) if (p.PlayerX == nx && p.PlayerY == ny) return (x, y);
        return ((ushort)nx, (ushort)ny);
    }

    // Pick the mob-id list this trap fires: the sentry burst when the stepper is in the top half
    // (y <= SentryTopY — RTK's Hare Summit split / the guardroom's always-on sentries), else a 1-in-BigChance
    // "big mob" burst (tiger Dark Pen), else the primary — a random burst variant, a single creature
    // (spider/scorpion), or an ogre 4-tile burst (RTK spawnMob).
    private int[] SelectAmbushBurst(AmbushMapDef cfg, ushort y)
    {
        if (cfg.SentryTable.Length > 0 && y <= cfg.SentryTopY) return PickVariant(cfg.SentryTable);
        if (cfg.BigTable.Length > 0 && cfg.BigChance > 0 && Random.Shared.Next(cfg.BigChance) == 0) return PickVariant(cfg.BigTable);
        return cfg.PrimaryKind switch
        {
            "burst"  => PickVariant(cfg.PrimaryTable),
            "single" => new[] { cfg.PrimaryMob },
            "ogre"   => Enumerable.Repeat(
                            cfg.OgreAltChance > 0 && Random.Shared.Next(cfg.OgreAltChance) == 0 ? cfg.OgreAltMob : cfg.PrimaryMob, 4)
                        .ToArray(),
            _        => Array.Empty<int>(),
        };
    }

    private int[] PickVariant(string table) =>
        Content.AmbushBursts.TryGetValue(table, out var vs) && vs.Count > 0 ? vs[Random.Shared.Next(vs.Count)] : Array.Empty<int>();

    // Top a configured map's hidden ambush traps back up to its target count, but only while live mobs stay
    // under the map's cap — RTK's population governor (its trap refiller stops adding while the map is already
    // full of mobs). Caller holds _lock. Called on every map entry (EnsureMaterialized) and after a trap fires.
    private void RefillAmbushLocked(ushort mapId)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        if (!Content.Ambushes.TryGetValue(mapId, out var cfg)) return;
        var m = Map(mapId);
        int traps = 0; foreach (var t in m.Traps) if (t.Kind == "ambush") traps++;
        if (traps >= cfg.Count) return;
        int mobs = 0; foreach (var mob in m.Mobs) if (mob.Alive && !mob.IsNpc) mobs++;
        if (mobs >= cfg.MobCap) return;

        var taken = OccupiedTiles(mapId);
        foreach (var t in m.Traps) taken.Add((t.X, t.Y));   // don't stack two traps on one tile
        int budget = cfg.Count * PlacementTriesPerMob;
        while (traps < cfg.Count && budget-- > 0)
        {
            if (!TryPickMapTile(mapId, taken, out var tx, out var ty)) continue;
            m.Traps.Add(new Trap { Id = _nextTrapId++, X = tx, Y = ty, Kind = "ambush", OwnerId = 0 });
            taken.Add((tx, ty));
            traps++;
        }
    }

    // ---- Sute's Cave cold tiles (Server/SuteAi.cs) ----------------------------------------------
    // The "A blast of frigid cold hits you." hazard: hidden trap tiles scattered through all seven rooms,
    // topped back up to SuteAi.FrigidTrapsPerMap on every entry and after each one springs. Deliberately the
    // same machinery as the cave ambush traps above rather than a per-step dice roll, so they can be SPOTTED
    // (spot_traps reveals any trap kind) and so a sprung one relocates instead of thinning the room out.
    // Caller holds _lock.
    private void RefillFrigidLocked(ushort mapId)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        if (Array.IndexOf(SuteAi.CaveMaps, mapId) < 0) return;
        var m = Map(mapId);
        int traps = 0; foreach (var t in m.Traps) if (t.Kind == SuteAi.FrigidTrapKind) traps++;
        if (traps >= SuteAi.FrigidTrapsPerMap) return;

        var taken = OccupiedTiles(mapId);
        foreach (var t in m.Traps) taken.Add((t.X, t.Y));   // don't stack two traps on one tile
        int budget = SuteAi.FrigidTrapsPerMap * PlacementTriesPerMob;
        while (traps < SuteAi.FrigidTrapsPerMap && budget-- > 0)
        {
            if (!TryPickMapTile(mapId, taken, out var tx, out var ty)) continue;
            m.Traps.Add(new Trap { Id = _nextTrapId++, X = tx, Y = ty, Kind = SuteAi.FrigidTrapKind, OwnerId = 0 });
            taken.Add((tx, ty));
            traps++;
        }
    }

    // A random passable, object-free, non-warp, unoccupied tile anywhere on a map (the whole-map analogue of
    // SpawnDirector.TryPickGroupTile). Caller holds _lock.
    private bool TryPickMapTile(ushort mapId, HashSet<(int, int)> taken, out ushort x, out ushort y)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        x = y = 0;
        var (xs, ys) = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        if (xs == 0 || ys == 0) return false;
        int tx = Random.Shared.Next(1, xs), ty = Random.Shared.Next(1, ys);
        if (taken.Contains((tx, ty))) return false;
        var terrain = MapData.For(mapId, xs, ys);
        if (terrain is not null && terrain.Solid(tx, ty)) return false;
        if (Content.TryWarp(mapId, (ushort)tx, (ushort)ty, out _)) return false;
        x = (ushort)tx; y = (ushort)ty;
        return true;
    }

    /// <summary>Every trap within <paramref name="radius"/> tiles (Chebyshev) of a point — spot_traps'
    /// reveal, or a debug listing. Doesn't consume/remove anything.</summary>
    public Trap[] TrapsNear(ushort mapId, int x, int y, int radius)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return Array.Empty<Trap>();
            return m.Traps.Where(t => Math.Max(Math.Abs(t.X - x), Math.Abs(t.Y - y)) <= radius).ToArray();
        }
    }

    // Flat damage for the four "instant hit" trap kinds (RTK NPCs/trap/rogue_traps/*.lua — dart/repeating
    // are byte-for-byte the same script despite the name difference; only their spell-side level gate and
    // mana cost differ).
    private static readonly Dictionary<string, int> TrapDamage = new()
        { ["dart"] = 500, ["repeating"] = 500, ["spear"] = 3500, ["death"] = 11650 };

    /// <summary>Trap kinds only a PLAYER springs — a wandering mob walks over one and leaves it alone. RTK
    /// gates both inside the trap NPC's own click hook: <c>MobSpawnNpc</c> (our "ambush") and the tiger
    /// caves' <c>WarpTrapShiverNpc</c> (our "shiver") each bail out unless <c>block.blType == BL_PC</c>. The
    /// rogue combat family is the opposite — those exist to catch mobs — so it stays triggerable by both.
    ///
    /// <para>Without this, cave fauna quietly ATE the hidden ambush tiles as they wandered (the mob-step
    /// lookup removed the trap, then <see cref="TriggerTrapLocked"/> had no case for it and did nothing), so
    /// a cave's spawn triggers kept silently vanishing and reappearing elsewhere on the next refill — the
    /// "the spawn triggers seem to despawn and move around" report.</para></summary>
    private static bool IsPcOnlyTrap(string kind) => kind is "shiver" or "ambush";

    // Caller holds _lock (called mid-movement-loop, mob has just stepped onto the trap's tile). Damage
    // kinds are queued for World.Tick's deferred pass (needs Session-facing broadcasts, which mustn't run
    // under the lock); status kinds (snare/sleep/flash — all simplified to the same "can't act" mechanic as
    // a cast Debuff, since this server has no separate armor-debuff/blind stat) and poison (a real DOT,
    // ticked every 1500ms by the poison check above) mutate the mob directly since that's lock-only state.
    private void TriggerTrapLocked(ushort mapId, Mob mob, Trap trap, List<(ushort map, Mob mob, int dmg, uint ownerId)> damageQueue)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        long now = Environment.TickCount64;
        // RTK's every-trap epilogue: removeTrapItem(npc) before npc:delete(). A trap that has gone off takes
        // its revealed marker with it, so a Spot Traps / Watchful Eye sword never outlives the trap it marks.
        _deferredTrapClears.Add((mapId, trap.Id));
        switch (trap.Kind)
        {
            case "dart" or "repeating" or "spear" or "death":
                damageQueue.Add((mapId, mob, TrapDamage[trap.Kind], trap.OwnerId));
                break;
            case "snare": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 75000); break;   // RTK: armor+20 debuff, simplified to a hold
            case "sleep": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 38000); break;
            case "flash": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 10000); break;    // RTK: blind.cast, simplified to a hold
            case "poison":
                mob.PoisonUntil = now + 1 + Random.Shared.Next(1500, 30001);   // RTK: 1 + random(1500,30000) for a MOB target
                mob.PoisonNextTick = now + 1500;
                mob.PoisonTickDam = Math.Clamp((int)(mob.MaxHp * 0.01), 1, 1000);
                mob.PoisonOwnerId = trap.OwnerId;
                break;
            case "bladestorm":
            {
                // ONE HP-percent damage number computed from the trigger (RTK block.health*0.75, or *0.05 on
                // instance/high maps ids >= 60000), applied uniformly to the trigger itself AND every mob the
                // facing cone catches — see Content.IsBladestormTrap. The PC-trigger case (World.
                // CheckPlayerTrapTrigger) mirrors this but nets against the trigger's OWN armor instead.
                var mm = Map(mapId);
                int dmg = Math.Max(1, (int)(mob.Hp * (mapId < 60000 ? 0.75 : 0.05)));
                foreach (var (dx, dy) in BladestormFan[mob.Dir & 3])
                {
                    var t = mm.Mobs.FirstOrDefault(o => o.Alive && !ReferenceEquals(o, mob) && o.X == mob.X + dx && o.Y == mob.Y + dy);
                    if (t is not null) damageQueue.Add((mapId, t, dmg, trap.OwnerId));
                }
                damageQueue.Add((mapId, mob, dmg, mob.Id));   // the trigger takes it too (RTK block.health -= damage)
                break;
            }
        }
    }

    /// <summary>Apply a trap hit / poison tick's damage (deferred out of the movement lock — see
    /// <see cref="TriggerTrapLocked"/>): mutate HP via the normal <see cref="TryDamage"/> path, broadcast the
    /// over-head damage number (and death despawn), and credit the trap owner with exp on a kill.</summary>
    private void ApplyTrapDamage(ushort mapId, Mob mob, int dmg, uint ownerId)
    {
        if (!TryDamage(mapId, mob, dmg, out bool died, ownerId)) return;
        byte pct = died ? (byte)0 : (byte)Math.Clamp(mob.Hp * 100 / Math.Max(1, mob.MaxHp), 1, 100);
        BroadcastWideArea(mapId, mob.X, mob.Y, p => p.DamageOver(mob.Id, pct, 33));
        if (died)
        {
            uint mobId = mob.Id;
            _ = Task.Run(async () => { try { await Task.Delay(600); Broadcast(mapId, p => p.DespawnEntity(mobId)); } catch (Exception e) { Log.Error($"delayed despawn of mob {mobId} threw", e); } });
            uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
            PlayerById(ownerId)?.AwardKillExp(reward, mapId, mob.X, mob.Y, mob.Key);
        }
    }

    /// <summary>A pet's swing landing on another mob. Same shape as <see cref="ApplyTrapDamage"/> — damage,
    /// over-head bar, delayed corpse despawn, exp to the human who owns the attacker — plus the melee
    /// swing/impact sfx a mob-on-mob hit should make, and retaliation.
    /// <para><b>attackerId stays 0 into TryDamage on purpose.</b> That parameter marks a PLAYER as the
    /// victim's new target, and routing the pet's owner in there would make every pet swing pull the mob
    /// straight onto its owner — the opposite of what a pet is for. Retaliation is handled below instead,
    /// and only when the victim isn't already busy with a player, so a pet can't soak a fight that was never
    /// aimed at it.</para></summary>
    private void ApplyMobOnMobHit(ushort mapId, Mob attacker, Mob victim, int dmg)
    {
        // Either side can have died to an earlier entry in this same batch — don't play a dead pet's swing.
        if (!attacker.Alive || !victim.Alive) return;
        BroadcastSameArea(mapId, attacker.X, attacker.Y, p => p.ActionOver(attacker.Id, Session.MobSwingActionType, Session.MobSwingActionTime, 0));  // visibly swing
        BroadcastSameArea(mapId, attacker.X, attacker.Y, p => p.SoundAt(Session.MobSwingSfx, attacker.Id));   // 009.wav on the swing itself, SAMEAREA like RTK's clif_playsound
        if (!TryDamage(mapId, victim, dmg, out bool died)) return;
        // The ONLY 0x13 that carries a hitSound byte, so this one is range-gated: 001.wav would otherwise ring
        // map-wide every time a pet landed a swing. RTK sends 0x13 over AREA everywhere (clif.c ~1305), but the
        // sound half belongs in the tighter SAMEAREA box, so the two go out separately.
        byte vpct = died ? (byte)0 : (byte)Math.Clamp(victim.Hp * 100 / Math.Max(1, victim.MaxHp), 1, 100);
        BroadcastWideArea(mapId, victim.X, victim.Y, p => p.DamageOver(victim.Id, vpct, 0));
        BroadcastSameArea(mapId, victim.X, victim.Y, p => p.SoundAt(Session.MobHitSfx, victim.Id));
        if (died)
        {
            uint victimId = victim.Id;
            _ = Task.Run(async () => { try { await Task.Delay(600); Broadcast(mapId, p => p.DespawnEntity(victimId)); } catch (Exception e) { Log.Error($"delayed despawn of mob {victimId} threw", e); } });
            // The owner gets the kill: RTK credits a mob's damage to map_id2sd(mob->owner) the same way
            // (clif.c's `tmob->owner < MOB_START_NUM` lookup), so a pet kill counts as yours.
            // …but NOT for a conjured victim. Now that a poet's pets will turn on a sibling he has attacked,
            // paying exp here would be the same summon-and-kill loop Session.ResolveSwing refuses, just
            // routed through a second pet. Same rule, same reason.
            if (!victim.Summoned)
            {
                uint reward = (uint)(victim.Exp > 0 ? victim.Exp : victim.MaxHp);
                PlayerById(attacker.OwnerId)?.AwardKillExp(reward, mapId, victim.X, victim.Y, victim.Key);
            }
            return;
        }
        lock (_lock) if (victim.Alive && victim.TargetId == 0) victim.TargetMobId = attacker.Id;
    }

    /// <summary>Confuse (RTK mage/confuse.lua) SUCCESS effect: wipe the mob's ENTIRE threat table and drop its
    /// current target so it forgets everyone. Confuse is a full aggro RESET, not the per-caster peel that
    /// Amnesia is. If a creature is standing on a cardinally-adjacent tile, the confused mob is turned on THAT
    /// creature (<see cref="Mob.TargetMobId"/>) — which is how two mobs side by side, Blinded first so they
    /// don't simply re-aggro you, can be spammed with Confuse into fighting each other; the victim's own
    /// retaliation (<see cref="ApplyMobOnMobHit"/>) then closes the loop. No status and no timer: this just
    /// re-points the AI once, so a still-sighted aggressive mob is free to re-acquire the nearest player next
    /// tick (blind it first if you want the redirect to stick). Caller need not hold the lock.</summary>
    public void ConfuseMob(ushort mapId, Mob mob)
    {
        lock (_lock)
        {
            mob.Threat?.Clear();
            mob.TargetId = 0;
            mob.AmnesiaBy = 0; mob.AmnesiaUntil = 0;   // a full reset supersedes any earlier Amnesia peel
            mob.AttackTimer = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0;
            mob.TargetMobId = 0;
            if (!_maps.TryGetValue(mapId, out var m)) return;
            var foes = m.Mobs.Where(o => o.Alive && !o.IsNpc && o.Id != mob.Id
                            && ((o.X == mob.X && Math.Abs(o.Y - mob.Y) == 1) || (o.Y == mob.Y && Math.Abs(o.X - mob.X) == 1)))
                         .ToList();
            if (foes.Count > 0) mob.TargetMobId = foes[Random.Shared.Next(foes.Count)].Id;
        }
    }

    /// <summary>One step of a chase toward <c>(tx,ty)</c> — a port of RTK's <c>FindCoords</c>
    /// (<c>rtklua/Accepted/Mobs/mob.lua:299</c>), which is the real 4.95 chase step. Caller holds
    /// <c>_lock</c>. True if the mob moved.
    /// <para>The single chase-movement path in the world: the provoked-mob chase, both pet movers (closing on
    /// the foe it is assisting against, and heeling back to its owner), and pet retaliation all run through
    /// here, so obstacle handling can't differ between them.</para>
    ///
    /// <para><b>This is deliberately dumb. Do NOT make it a pathfinder.</b> No A*, no map search, no
    /// lookahead, no wall-following, no memory of where it has been. RTK's version tries ONLY the one or two
    /// directions that close on the target — vertical then horizontal, or horizontal then vertical, on a
    /// coin flip (<c>checkmove = math.random(0, 2)</c>, ≥1 picks vertical-first) — and takes the first that
    /// isn't blocked. That coin flip is the entire cleverness of 4.95 mob pathing.</para>
    ///
    /// <para>What that produces: a mob diagonal from you rounds an ordinary corner by itself, because when one
    /// axis is blocked the other one is still "toward" you. When NO toward-step is open (squared up against a
    /// wall, cornered, or pitted) it falls into RTK's "nothing worked" branch — one step in a fully RANDOM
    /// direction, up to 11 random draws until a tile is free (the random side can be sideways OR straight
    /// away). MEASURED behaviour (re/scratchpad sim, 2026-08-24): clears a 1-wide rock and rounds a wall of any
    /// width when you're diagonal to the mob, but JITTERS at the face — never reaching you — when you stand
    /// directly behind a wall ≥3 wide (re/sim_mob_stepping.py), because the toward-step re-aligns it each tick; and it does not escape
    /// an enclosed pit. It is a random walk with a restoring pull, not a solver.</para>
    ///
    /// <para><b>History:</b> this used to replace RTK's random flail with a sideways-ONLY shuffle. On 2026-08-24
    /// the user reported "not enough pacing/exploration to get to the user if they're blocked"; a simulation of
    /// the alternatives showed a committed-run rule reaches you behind head-on walls (100%) while literal RTK
    /// does not (0%), yet the user chose the literal RTK port anyway, for accuracy over reach, with that
    /// tradeoff in front of them. So the jitter-at-a-head-on-wall and no-pit-escape are the CHOSEN behaviour,
    /// not bugs — don't quietly upgrade this to the committed-run version without re-asking.</para>
    ///
    /// <para>One departure from RTK's branch remains: RTK also re-rolls <c>mob.target</c> to a random nearby
    /// player when stuck; that half lives in the caller's <c>towardBlocked</c> block (gated on
    /// <see cref="Mob.Aggressive"/>), not here.</para></summary>
    private bool StepMobToward(ushort mapId, MapState m, Mob mob, int tx, int ty,
                               (ushort Xs, ushort Ys) dims, MapData? terrain,
                               HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                               List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                               List<(ushort map, uint id, byte dir)> turns,
                               List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        return StepMobToward(mapId, m, mob, tx, ty, dims, terrain, occupied, mobTiles, moves, turns, trapDamage, out _);
    }

    /// <param name="towardBlocked">True when NOTHING that closes the gap was open this step — the mob is
    /// walled off from its target rather than merely taking a longer route. RTK's <c>canmove == false</c>.
    /// Callers use it to decide whether to go looking for a target they can actually reach.</param>
    /// <inheritdoc cref="StepMobToward(ushort, MapState, Mob, int, int, ValueTuple{ushort, ushort}, MapData, HashSet{ValueTuple{ushort, ushort}}, HashSet{ValueTuple{int, int}}, List{ValueTuple{ushort, uint, ushort, ushort, byte}}, List{ValueTuple{ushort, uint, byte}}, List{ValueTuple{ushort, Mob, int, uint}})"/>
    private bool StepMobToward(ushort mapId, MapState m, Mob mob, int tx, int ty,
                               (ushort Xs, ushort Ys) dims, MapData? terrain,
                               HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                               List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                               List<(ushort map, uint id, byte dir)> turns,
                               List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage,
                               out bool towardBlocked)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        towardBlocked = false;
        int dx = tx - mob.X, dy = ty - mob.Y;
        if (dx == 0 && dy == 0) { mob.DetourDir = NoDetour; mob.DetourLeft = 0; return false; }

        // Take one cardinal step if that tile is free (bounds + no player + no other mob + the two-layer
        // terrain test), turning onto it first and springing any trap it lands on. RTK's mob:move().
        bool Step(byte dir)
        {
            int nx = mob.X + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
            int ny = mob.Y + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            if (nx < 0 || ny < 0) return false;
            if (dims.Xs != 0 && (nx >= dims.Xs || ny >= dims.Ys)) return false;
            if (occupied.Contains(((ushort)nx, (ushort)ny))) return false;      // never onto a player
            if (mobTiles.Contains((nx, ny))) return false;                      // nor another creature
            if (MobBlocked(mapId, terrain, nx, ny, dir)) return false;   // pass flag / SObj wall / warp tile
            if (mob.Dir != dir) { mob.Dir = dir; turns.Add((mapId, mob.Id, dir)); }
            return StepMobTo(mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage);
        }

        // RTK FindCoords proper (mob.lua:307-352): only the directions that close the gap, on a coin-flipped
        // axis order. First unblocked one wins.
        var toward = new List<byte>(2);
        byte? vert = dy > 0 ? (byte)2 : dy < 0 ? (byte)0 : null;
        byte? horz = dx > 0 ? (byte)1 : dx < 0 ? (byte)3 : null;
        if (Random.Shared.Next(3) >= 1) { if (vert is byte a) toward.Add(a); if (horz is byte b) toward.Add(b); }
        else                            { if (horz is byte c) toward.Add(c); if (vert is byte d) toward.Add(d); }

        foreach (byte dir in toward)
            if (Step(dir)) return true;

        // Nothing that closes the gap is open.
        towardBlocked = true;

        // RTK's "nothing worked" branch (mob.lua:361-382), ported faithfully: take ONE step in a fully random
        // direction, retrying up to 11 random draws until a tile is open —
        //     for i = 0, 10 do if (not found) then mob.side = math.random(0, 3); found = mob:move() end end
        // Any of the four sides is fair game — sideways OR straight AWAY from the target. One tile per call, at
        // the mob's move cadence; the loop only searches for an open side, it doesn't stack hops.
        //
        // What this ACTUALLY produces (measured, re/sim_mob_stepping.py, 2026-08-24 — don't re-optimise on a hunch):
        // it clears a 1-wide rock and rounds a wall of any width when the player is DIAGONAL to the mob, but
        // when the player is directly behind a wall ≥3 tiles wide the mob JITTERS at the face (~0% reach) —
        // every random sideways step is undone next tick by the toward-step snapping it back into alignment.
        // For the same reason it does NOT escape an enclosed pit. Both are faithful RTK, and the user chose it
        // over the more capable committed-run alternative AFTER seeing this exact data (2026-08-24): the goal
        // was RTK-accuracy, not maximal reach. If a report says "mobs jitter at a wall / can't reach me behind
        // one", that is this branch being literally RTK — revisit the decision, don't silently make it smarter.
        // RTK's companion move here — re-rolling the target to a random nearby player — lives in the caller's
        // `towardBlocked` block, keyed off the flag set just above.
        for (int i = 0; i < 11; i++)
            if (Step((byte)Random.Shared.Next(4))) return true;

        // Boxed in on all four sides. Face the target so it at least reads as wanting to reach you.
        if (toward.Count > 0 && mob.Dir != toward[0]) { mob.Dir = toward[0]; turns.Add((mapId, mob.Id, toward[0])); }
        return false;
    }

    /// <summary>No sideways shuffle in progress — see <see cref="Mob.DetourDir"/>. Vestigial now the blocked
    /// fallback is RTK's stateless random walk (see <see cref="StepMobToward(ushort, MapState, Mob, int, int, ValueTuple{ushort, ushort}, MapData, HashSet{ValueTuple{ushort, ushort}}, HashSet{ValueTuple{int, int}}, List{ValueTuple{ushort, uint, ushort, ushort, byte}}, List{ValueTuple{ushort, uint, byte}}, List{ValueTuple{ushort, Mob, int, uint}}, bool)"/>); the field and its resets are harmless and kept to avoid churn.</summary>
    private const byte NoDetour = 0xFF;

    /// <summary>A tile a MOB may not step onto. The two static collision layers — ground pass flag plus the
    /// client's <c>SObj.tbl</c> directional object-walls, via <see cref="MapData.BlockedMove"/> — PLUS warp
    /// source tiles. A mob can't warp, so letting it wander onto a door/stair/portal tile just parks it on the
    /// threshold, and following you onto one reads as walking through the wall the warp sits in — the reported
    /// "mobs no-clip … warps". Blocking warp tiles for mobs is a deliberate deviation (user, 2026-08-24): RTK's
    /// own mob move is pass-only and clips these, and the PLAYER walk still treats a warp as walkable-and-
    /// transiting (<see cref="Session"/>.HandleWalk) — this gate is mob-only. Caller has already bounds-checked
    /// (nx,ny) ≥ 0 and in-dims, so the ushort casts are safe.</summary>
    private static bool MobBlocked(ushort mapId, MapData? terrain, int nx, int ny, byte dir) =>
        (terrain is not null && terrain.BlockedMove(nx, ny, dir))
        || Content.TryWarp(mapId, (ushort)nx, (ushort)ny, out _);

    /// <summary>Commit a validated one-tile move: update the tile index, queue the broadcast, spring a trap.</summary>
    /// <summary>One step of a RETREAT from <c>(tx,ty)</c> — the mirror image of <see cref="StepMobToward"/>, and
    /// a port of RTK's <c>RunAway</c> (<c>rtklua/Accepted/Mobs/mob.lua:427</c>). Caller holds <c>_lock</c>.
    /// True if the mob moved.
    /// <para>RTK's routine has two cases and this keeps both. Standing right next to the player
    /// (<c>moveIntent == 1</c>): turn 180° and go, which is the bolt when you close to melee range. Otherwise:
    /// try each direction that increases the gap, coin-flipping whether the vertical or the horizontal one is
    /// attempted first, and take the first that isn't blocked — the away-mirror of FindCoords' axis flip.</para>
    /// <para>The last resort differs. RTK, having nowhere to run, picks a random nearby player as its new target
    /// and flails at up to 10 random sides; a prey creature has no target to pick, so a cornered one takes any
    /// open SIDEWAYS step instead (never back toward what is chasing it) and simply stands still if even that is
    /// walled off. Cornering a rabbit against a cliff is supposed to be how you catch it.</para>
    /// <para>Home moves with the mob on every retreat step. The wander leash below tests the DESTINATION against
    /// Home, so a creature that fled past its leash could never step anywhere again — it would freeze the moment
    /// you walked away. Re-homing keeps it wandering wherever it ends up, which is also what a spooked animal
    /// looks like: it doesn't run back to the exact tile it was born on.</para></summary>
    private bool StepMobAway(ushort mapId, MapState m, Mob mob, int tx, int ty,
                             (ushort Xs, ushort Ys) dims, MapData? terrain,
                             HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                             List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                             List<(ushort map, uint id, byte dir)> turns,
                             List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        int dx = tx - mob.X, dy = ty - mob.Y;

        bool Step(byte dir)
        {
            int nx = mob.X + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
            int ny = mob.Y + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            if (nx < 0 || ny < 0) return false;
            if (dims.Xs != 0 && (nx >= dims.Xs || ny >= dims.Ys)) return false;
            if (occupied.Contains(((ushort)nx, (ushort)ny))) return false;
            if (mobTiles.Contains((nx, ny))) return false;
            if (MobBlocked(mapId, terrain, nx, ny, dir)) return false;
            if (mob.Dir != dir) { mob.Dir = dir; turns.Add((mapId, mob.Id, dir)); }
            if (!StepMobTo(mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage)) return false;
            mob.HomeX = mob.X; mob.HomeY = mob.Y;   // see the doc note on re-homing
            return true;
        }

        // Cornered by an adjacent player (RTK's moveIntent branch): about-face and bolt.
        bool adjacent = (dx == 0 && Math.Abs(dy) == 1) || (dy == 0 && Math.Abs(dx) == 1);
        if (adjacent && Step((byte)((mob.Dir + 2) & 3))) return true;

        // Otherwise: the directions that open the gap, vertical-or-horizontal first on a coin flip.
        var away = new List<byte>(2);
        byte? vert = dy > 0 ? (byte)0 : dy < 0 ? (byte)2 : null;   // player south of us -> run north
        byte? horz = dx > 0 ? (byte)3 : dx < 0 ? (byte)1 : null;   // player east of us  -> run west
        if (Random.Shared.Next(3) >= 1) { if (vert is byte a) away.Add(a); if (horz is byte b) away.Add(b); }
        else                            { if (horz is byte c) away.Add(c); if (vert is byte d) away.Add(d); }
        foreach (byte dir in away) if (Step(dir)) return true;

        // Nowhere to retreat: slip sideways rather than back into them.
        var sides = new List<byte>(2);
        if (dx == 0) { sides.Add(1); sides.Add(3); }
        else if (dy == 0) { sides.Add(0); sides.Add(2); }
        if (sides.Count == 2 && Random.Shared.Next(2) == 1) sides.Reverse();
        foreach (byte dir in sides) if (Step(dir)) return true;
        return false;
    }

    // ---- the flee DART: the one way anything in this world runs away --------------------------------
    /// <summary>How each hop of a <see cref="Dart"/> picks its direction.</summary>
    private enum DartMode
    {
        /// <summary>Open the gap from (tx,ty) — <see cref="StepMobAway"/>, sideways slip and all.</summary>
        Away,
        /// <summary>Close on (tx,ty), stopping the moment the mob is in reach — <see cref="StepMobToward"/>.</summary>
        Toward,
        /// <summary>Straight ahead in <see cref="Mob.Dir"/>, wherever that points (the blind rout).</summary>
        Straight,
    }

    /// <summary>
    /// Cover up to <paramref name="tiles"/> tiles inside ONE move turn, stopping early at the first hop that
    /// can't be taken. Returns how many were actually covered — 0 means boxed in, which is what every caller
    /// reads as "cornered".
    ///
    /// <para><b>This is RTK's own idiom for running away, and the only one this server uses.</b> RTK expresses
    /// a break-off by calling <c>mob:move()</c> several times in a single script invocation —
    /// <c>AI/bosses/nine_tailed_fox.lua</c> does three in a row — which the client draws as a creature
    /// covering several tiles at once rather than walking faster. The three fleers here (prey, the wounded
    /// rout, Sute) all used to approximate it differently: prey ran on a shortened timer, the rout stepped
    /// every heartbeat, and Sute paced tile by tile. They now share this, so "runs away" looks the same
    /// everywhere and there is one place to change it.</para>
    ///
    /// <para>Every hop goes through the ordinary step helpers, so walls, occupancy, map bounds and trap tiles
    /// all apply to each one individually — a fleeing creature can absolutely bolt onto a trap. (The old
    /// hand-rolled rout skipped the trap check by moving the mob itself; going through
    /// <see cref="StepMobTo"/> fixes that inconsistency.)</para>
    ///
    /// <para>Caller holds <c>_lock</c> and has already decided this is the mob's move turn.</para>
    /// </summary>
    private int Dart(DartMode mode, int tiles, ushort mapId, MapState m, Mob mob, int tx, int ty,
                     (ushort Xs, ushort Ys) dims, MapData? terrain,
                     HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                     List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                     List<(ushort map, uint id, byte dir)> turns,
                     List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        int hops = 0;
        while (hops < tiles)
        {
            bool stepped;
            switch (mode)
            {
                case DartMode.Toward:
                    int dx = tx - mob.X, dy = ty - mob.Y;
                    // In reach: stop here rather than trying to walk through them.
                    if ((dx == 0 && Math.Abs(dy) == 1) || (dy == 0 && Math.Abs(dx) == 1)) return hops;
                    stepped = StepMobToward(mapId, m, mob, tx, ty, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                    break;
                case DartMode.Straight:
                    stepped = StepMobStraight(mapId, m, mob, dims, terrain, occupied, mobTiles, moves, trapDamage);
                    break;
                default:
                    stepped = StepMobAway(mapId, m, mob, tx, ty, dims, terrain, occupied, mobTiles, moves, turns, trapDamage);
                    break;
            }
            if (!stepped) break;
            hops++;
        }
        return hops;
    }

    /// <summary>Remaining-HP percent for a mob's over-head bar — 1..100 while alive so a living creature's
    /// bar never reads empty. Mirrors Session's own private HpPercent(Mob); the two must agree or a healed
    /// bar would jump to a different scale than a damaged one.</summary>
    private static byte MobHpPercent(Mob m)
    {
        int max = Math.Max(1, m.MaxHp);
        int cur = Math.Clamp(m.Hp, 0, max);
        if (cur <= 0) return 0;
        return (byte)Math.Max(1, (int)((long)cur * 100 / max));
    }

    /// <summary>One hop straight ahead in the mob's current facing, with the same bounds/occupancy/terrain
    /// checks every other step takes. The blind half of the wounded rout: RTK picks a random side once and
    /// then runs, so the creature does not steer around anything — it just stops when it hits something.
    /// Caller holds <c>_lock</c>.</summary>
    private bool StepMobStraight(ushort mapId, MapState m, Mob mob,
                                 (ushort Xs, ushort Ys) dims, MapData? terrain,
                                 HashSet<(ushort, ushort)> occupied, HashSet<(int, int)> mobTiles,
                                 List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                                 List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        byte dir = mob.Dir;
        int nx = mob.X + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
        int ny = mob.Y + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
        if (nx < 0 || ny < 0) return false;
        if (dims.Xs != 0 && (nx >= dims.Xs || ny >= dims.Ys)) return false;
        if (occupied.Contains(((ushort)nx, (ushort)ny))) return false;
        if (mobTiles.Contains((nx, ny))) return false;
        if (MobBlocked(mapId, terrain, nx, ny, dir)) return false;
        return StepMobTo(mapId, m, mob, nx, ny, dir, mobTiles, moves, trapDamage);
    }

    /// <summary>A player swung at <paramref name="mob"/> — hit OR miss. A prey creature (<see cref="Mob.Flees"/>)
    /// bolts: it stays spooked for <see cref="PanicMs"/>, refreshed by each further swing, which WIDENS the
    /// distance at which it notices you (<see cref="FleeRadius"/>) rather than changing how far its dart
    /// carries — see <see cref="Dart"/> and <see cref="PreyDartTiles"/>. No effect on anything
    /// else — an ordinary mob is provoked by <see cref="TryDamage"/>, which needs damage to have landed.</summary>
    public void Spook(Mob mob)
    {
        if (!mob.Flees) return;
        lock (_lock) mob.PanicUntil = Environment.TickCount64 + PanicMs;
    }

    /// <summary>Commit a validated mob step and trigger any trap on its destination. Caller holds
    /// <c>_lock</c>.</summary>
    private bool StepMobTo(ushort mapId, MapState m, Mob mob, int nx, int ny, byte dir,
                           HashSet<(int, int)> mobTiles,
                           List<(ushort map, uint id, ushort x, ushort y, byte dir)> moves,
                           List<(ushort map, Mob mob, int dmg, uint ownerId)> trapDamage)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        ushort ox = mob.X, oy = mob.Y;
        mobTiles.Remove((mob.X, mob.Y));
        mob.X = (ushort)nx; mob.Y = (ushort)ny;
        mobTiles.Add((nx, ny));
        moves.Add((mapId, mob.Id, ox, oy, dir));
        // The Ice Beast melts the instant it steps onto its lava (RTK ice_beast.lua move hook). Lethal
        // self-damage is queued like a trap hit so it flows through the normal death path: its Ice heart drops
        // on the tile (MobDrops 100%) and its spawn frees. ownerId 0 pays no exp — which is what RTK's lava
        // kill does; the beast is worth none, the reward is the heart on the floor.
        if (mapId == IceBeastMap && mob.Key == IceBeastKey && IsIceBeastLava(nx, ny))
        {
            trapDamage.Add((mapId, mob, mob.MaxHp, 0));
            _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, IceBeastMeltAnim, 0));
        }
        var trap = m.Traps.FirstOrDefault(t => t.X == nx && t.Y == ny && !IsPcOnlyTrap(t.Kind));
        if (trap is not null) { m.Traps.Remove(trap); TriggerTrapLocked(mapId, mob, trap, trapDamage); }
        return true;
    }

    private MapState Map(ushort id)
    {
        if (!_maps.TryGetValue(id, out var m)) { m = new MapState(); _maps[id] = m; }
        return m;
    }

    // ---- players joining / leaving a map ------------------------------------------------------

    /// <summary>Register <paramref name="s"/> on <paramref name="mapId"/>, broadcast it to everyone
    /// already there, and return the peers + mobs the caller should draw for the newcomer.</summary>
    public (PeerTile[] peers, Mob[] mobs) EnterMap(Session s, ushort mapId)
    {
        // BEFORE the lock: first entry to a map runs EnsureMaterialized, whose spawn placement reads the
        // terrain (FreeSpawnTile -> MapData.For) — a disk read, a full cell decode and a SQLite query. Held
        // under _lock that froze every player on every OTHER map too, for as long as the load took. Warming
        // the cache out here makes the locked section a pure in-memory hit. See MapData.Prewarm.
        MapData.Prewarm(mapId);

        PeerTile[] peers; Mob[] mobs; PeerTile newcomer;
        lock (_lock)
        {
            _spawnDirector.EnsureMaterialized(mapId);                 // instantiate this map's spawns on first entry
            var m = Map(mapId);
            if (!m.Players.Contains(s)) m.Players.Add(s);
            // Seed the weather cache to what the newcomer is about to be shown (Session sends it on entry via
            // GetWeather), so the tick's period-rollover diff compares against the on-screen state and never
            // skips a real change as a no-op — otherwise a player who entered mid-period could stay stuck on
            // stale weather when the period rolls to a value that happens to match the default-0 cache.
            m.Weather = WeatherForLocked(mapId);
            peers = m.Players.Where(p => p != s).Select(p => new PeerTile(p, p.PlayerX, p.PlayerY)).ToArray();
            mobs = m.Mobs.ToArray();
            // The newcomer's own tile is snapshotted here too: the loop below draws THEM on every peer's
            // client, so it is their coordinates the peers' viewport gates read.
            newcomer = new PeerTile(s, s.PlayerX, s.PlayerY);
        }
        foreach (var p in peers) Try(() => p.Session.SyncPeer(newcomer), "SyncPeer (EnterMap)");   // tell the room about the newcomer (view-gated + tracked)
        return (peers, mobs);
    }

    /// <summary>Read-only: the peers + mobs on <paramref name="mapId"/> (excluding <paramref name="s"/>),
    /// WITHOUT registering or broadcasting. Used to re-assert the view after a client-side map rebuild
    /// (e.g. an in-place 0x15 refresh) drops all foreign entities.</summary>
    public (PeerTile[] peers, Mob[] mobs) View(Session s, ushort mapId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return (Array.Empty<PeerTile>(), Array.Empty<Mob>());
            return (m.Players.Where(p => p != s).Select(p => new PeerTile(p, p.PlayerX, p.PlayerY)).ToArray(),
                    m.Mobs.ToArray());
        }
    }

    /// <summary>Remove <paramref name="s"/> from <paramref name="mapId"/> and despawn it for the rest.</summary>
    public void LeaveMap(Session s, ushort mapId)
    {
        Session[] peers;
        uint id = s.PlayerId;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            m.Players.Remove(s);
            peers = m.Players.ToArray();
        }
        foreach (var p in peers) Try(() => p.DespawnEntity(id), "DespawnEntity (LeaveMap)");
    }

    // ---- broadcasts ---------------------------------------------------------------------------

    /// <summary>Run <paramref name="send"/> for every player on <paramref name="mapId"/> (except
    /// <paramref name="except"/>), outside the lock and exception-guarded.</summary>
    public void Broadcast(ushort mapId, Action<Session> send, Session? except = null)
    {
        Session[] peers;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            peers = m.Players.Where(p => p != except).ToArray();
        }
        foreach (var p in peers) Try(() => send(p), "Broadcast");
    }

    /// <summary>Like <see cref="Broadcast"/>, but only to players inside a box of ±<paramref name="halfW"/> ×
    /// ±<paramref name="halfH"/> around (<paramref name="cx"/>, <paramref name="cy"/>) — RTK's SAMEAREA
    /// proximity range, used for normal speech (clif_sendscriptsay's clif_send(..., SAMEAREA), the x±9/y±8 box)
    /// and for every sound (clif_playsound). Shout is NOT one of these: RTK's engine sends it SAMEMAP and we
    /// follow speech.lua's distance 16 instead, so it passes a bigger box and turns the shift off.
    /// <para><paramref name="edgeShift"/> reproduces RTK's SAMEAREA edge behaviour — see
    /// <see cref="ShiftedBox"/>. Leave it on for a SAMEAREA box; turn it off for anything else.</para></summary>
    public void BroadcastArea(ushort mapId, int cx, int cy, int halfW, int halfH, Action<Session> send,
                              Session? except = null, bool edgeShift = true)
    {
        var (x0, y0, x1, y1) = edgeShift
            ? ShiftedBox(mapId, cx, cy, halfW, halfH)
            : (cx - halfW, cy - halfH, cx + halfW, cy + halfH);
        Session[] peers;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            peers = m.Players.Where(p => p != except
                && p.PlayerX >= x0 && p.PlayerX <= x1
                && p.PlayerY >= y0 && p.PlayerY <= y1).ToArray();
        }
        foreach (var p in peers) Try(() => send(p), "BroadcastArea");
    }

    /// <summary>The SAMEAREA box for a map id — <see cref="ShiftBox"/> against that map's dims. A map the
    /// registry doesn't know has no edges to slide against, so it falls back to a plain centred box.</summary>
    private static (int x0, int y0, int x1, int y1) ShiftedBox(ushort mapId, int cx, int cy, int halfW, int halfH)
        => Content.Maps.TryGetValue(mapId, out var mi) && mi.Xs > 0 && mi.Ys > 0
            ? ShiftBox(cx, cy, halfW, halfH, mi.Xs, mi.Ys)
            : (cx - halfW, cy - halfH, cx + halfW, cy + halfH);

    /// <summary>RTK's SAMEAREA box (map.c <c>map_foreachinarea</c>): a ±<paramref name="halfW"/>/±<paramref
    /// name="halfH"/> rect around the source that is SLID back inside the map rather than clipped when it would
    /// hang off an edge. Standing against the west wall therefore reaches twice as far east — the box keeps its
    /// full size, it just stops being centred on you. Per axis, and SAMEAREA only: RTK's AREA case passes a
    /// plain centred box that <c>map_foreachinblockva</c> merely clamps, which is why
    /// <see cref="BroadcastWideArea"/> opts out.
    /// <para>A map SMALLER than the box collapses to the whole map, exactly as RTK's does — both shifts fire
    /// and cancel. Bounds are INCLUSIVE on both ends, matching map_foreachinblockva's <c>&gt;= x0 &amp;&amp;
    /// &lt;= x1</c>.</para></summary>
    public static (int x0, int y0, int x1, int y1) ShiftBox(int cx, int cy, int halfW, int halfH, int xs, int ys)
    {
        int x0 = cx - halfW, y0 = cy - halfH, x1 = cx + halfW, y1 = cy + halfH;
        // Order matters and mirrors RTK exactly: push off the low edge first, then pull back off the high one.
        if (x0 < 0)   { x1 += -x0; x0 = 0; if (x1 >= xs) x1 = xs - 1; }
        if (y0 < 0)   { y1 += -y0; y0 = 0; if (y1 >= ys) y1 = ys - 1; }
        if (x1 >= xs) { x0 -= x1 - xs + 1; x1 = xs - 1; if (x0 < 0) x0 = 0; }
        if (y1 >= ys) { y0 -= y1 - ys + 1; y1 = ys - 1; if (y0 < 0) y0 = 0; }
        return (x0, y0, x1, y1);
    }

    // ---- hearing / seeing ranges (RTK map.c map_foreachinarea) -----------------------------------
    //
    // RTK never sends a sound or a spell graphic to a whole map: clif_playsound ends in
    // clif_send(..., SAMEAREA) and clif_sendanimation is always driven by map_foreachinarea(..., AREA),
    // and BOTH resolve to a box around the SOURCE entity, not the map:
    //
    //   SAMEAREA (sound)     x +/- 9,  y +/- 8    -- map.c's x0/y0/x1/y1, i.e. one screen
    //   AREA     (animation) x +/- 19, y +/- 17   -- AREAX_SIZE+1 / AREAY_SIZE+1, about two screens
    //
    // The sound box is deliberately the tighter one: it is roughly the 17x15 viewport, so the rule is
    // "you hear what you can see". The animation box is looser because a 0x29 over an entity the client
    // never drew is a no-op anyway, so RTK does not bother trimming it to the screen.
    //
    // The sound box also carries RTK's edge SHIFT (see ShiftedBox): against a wall it slides inward instead
    // of being clipped, so hugging the west edge lets you hear twice as far east. That is SAMEAREA-only in
    // RTK -- the AREA case passes a plain centred box -- so BroadcastWideArea below opts out of it.
    public const int SoundHalfW = 9,  SoundHalfH = 8;
    public const int FxHalfW    = 19, FxHalfH    = 17;

    /// <summary>RTK's <c>SAMEAREA</c> box (+/-9/+/-8, edge-shifted) around the tile the thing happens ON.
    /// Carries <c>clif_playsound</c> (every sfx) and <c>clif_sendaction</c> (the 0x1A pose). Centre it on the
    /// entity the packet is BOUND to, not on whoever caused it — RTK binds a landed hit to the VICTIM
    /// (<c>clif_playsound(&amp;mob-&gt;bl, itemdb_soundhit(...))</c>).</summary>
    public void BroadcastSameArea(ushort mapId, int cx, int cy, Action<Session> send, Session? except = null)
        => BroadcastArea(mapId, cx, cy, SoundHalfW, SoundHalfH, send, except);

    /// <summary>RTK's looser <c>AREA</c> box (+/-19/+/-17, plain and centred — RTK does not edge-shift this
    /// one). Carries <c>clif_sendanimation</c> (0x29) and <c>clif_damage</c> (0x13, the over-head HP bar).
    /// Strictly contains the client's drawn rect (19x17 tiles = +/-9/+/-8), so it can never cut something a
    /// viewer could actually have seen.</summary>
    public void BroadcastWideArea(ushort mapId, int cx, int cy, Action<Session> send, Session? except = null)
        => BroadcastArea(mapId, cx, cy, FxHalfW, FxHalfH, send, except, edgeShift: false);

    /// <summary>The tile an entity id currently occupies on <paramref name="mapId"/> -- a live mob first,
    /// then a connected player -- or null when it is already gone (a mob that died between queueing an
    /// effect and flushing it). Callers use it to centre a sound/effect box on the thing making the noise.</summary>
    public (ushort x, ushort y)? EntityPos(ushort mapId, uint id)
    {
        lock (_lock)
        {
            if (_maps.TryGetValue(mapId, out var m))
            {
                var mob = m.Mobs.FirstOrDefault(mo => mo.Alive && mo.Id == id);
                if (mob is not null) return (mob.X, mob.Y);
            }
            var pc = PlayerByIdLocked(id);
            return pc is null ? null : ((ushort, ushort)?)(pc.PlayerX, pc.PlayerY);
        }
    }

    /// <summary>Current weather for a map (0=clear/1=rain/2=snow), for a player entering/re-entering it.
    /// Deterministic from the season + the map's region-zone + the time period (WeatherModel), unless an
    /// admin override is pinned on the zone; indoors is always clear. Needs no map to be "active".</summary>
    public byte GetWeather(ushort mapId) { lock (_lock) return WeatherForLocked(mapId); }

    // The weather a map should currently show, computed under _lock: clear indoors, else a zone override if
    // one is pinned, else the seasonal model. This is the single source of truth GetWeather and the tick share.
    private byte WeatherForLocked(ushort mapId)
    {
        if (Content.IsIndoor(mapId)) return WeatherModel.Clear;
        if (_weatherOverride.TryGetValue(WeatherModel.ZoneOf(mapId), out var forced)) return forced;
        return WeatherModel.For(mapId);
    }

    /// <summary>Pin a weather state onto a map's whole region-zone (the "@weather" admin lever) until
    /// <see cref="ClearWeatherOverride"/>. Broadcasts to everyone on any active map in that zone right away.</summary>
    public void SetWeather(ushort mapId, byte weather)
    {
        lock (_lock) _weatherOverride[WeatherModel.ZoneOf(mapId)] = weather;
        BroadcastZoneWeather(mapId);
    }

    /// <summary>Drop a zone's admin override so it returns to the seasonal model, and re-broadcast the now-live
    /// weather to everyone on it.</summary>
    public void ClearWeatherOverride(ushort mapId)
    {
        lock (_lock) _weatherOverride.Remove(WeatherModel.ZoneOf(mapId));
        BroadcastZoneWeather(mapId);
    }

    // Re-broadcast the current weather to every active map sharing this map's zone, updating each map's
    // last-sent cache. Used after an override is set or cleared so the change lands immediately, not at the
    // next period rollover.
    private void BroadcastZoneWeather(ushort mapId)
    {
        int zone = WeatherModel.ZoneOf(mapId);
        List<(ushort map, byte w)> hits = new();
        lock (_lock)
        {
            foreach (var (id, pm) in _maps)
            {
                if (pm.Players.Count == 0 || WeatherModel.ZoneOf(id) != zone) continue;
                byte w = WeatherForLocked(id);
                pm.Weather = w;
                hits.Add((id, w));
            }
        }
        foreach (var (id, w) in hits) Broadcast(id, p => p.SendWeather(w));
    }

    // ---- mobs ---------------------------------------------------------------------------------

    /// <summary>Full population hot-reload (the <c>@reload</c> path, after <see cref="Content.Reload"/> swapped
    /// the spawn/NPC registries): tear down every spawn-backed mob AND stationary NPC, rebuild the spawn roster
    /// + NPC placement from the fresh <see cref="Content"/>, and re-materialize the maps that currently have
    /// players (the rest build lazily on next entry). Unlike the old in-place re-stat, this picks up
    /// ADDED / REMOVED / REPOSITIONED spawn rows and NPCs — editing <c>AreaSpawns.csv</c>/<c>Spawns.csv</c> or
    /// an NPC's tile now takes effect on <c>@reload</c> without a restart. Cost is bounded: only populated maps
    /// re-materialize; the ~23k lazy points elsewhere are cheap point objects again. Players on a populated map
    /// briefly see mobs blink (despawn now, re-stream on the next <see cref="Tick"/>) — acceptable for an admin
    /// reload; a wounded mob also resets to full, since area spawns have no stable identity to preserve.
    /// Returns (mobs torn down, NPCs placed, maps re-materialized).</summary>
    public (int mobs, int npcs, int maps) RebuildPopulation()
    {
        var despawn = new List<(ushort map, uint id)>();
        var populated = new List<ushort>();
        int npcs = 0;
        lock (_lock)
        {
            // 1. tear down every shared mob (spawn-backed AND stationary NPC) on every map, remembering which
            //    maps have players so we know what to re-materialize. (Session-local debug dummies aren't in
            //    _maps, so they're untouched.)
            foreach (var (mapId, m) in _maps)
            {
                if (m.Players.Count > 0) populated.Add(mapId);
                foreach (var g in m.Mobs) despawn.Add((mapId, g.Id));
                m.Mobs.Clear();
            }
            // 2. rebuild the spawn roster + NPC placement from the just-reloaded Content (fresh defs, positions,
            //    and any added/removed rows). NPCs are placed on every map (cheap, ~340); mobs stay lazy.
            _spawnDirector.Clear();
            _spawnDirector.Build();
            foreach (var n in Content.Npcs) if (n.Enabled) { PlaceNpc(n); npcs++; }
            // 3. re-materialize only the maps that currently have players; the rest build lazily on next entry.
            foreach (var mapId in populated) _spawnDirector.EnsureMaterialized(mapId);
        }
        // Despawn the torn-down entities on clients (socket I/O outside the lock). The freshly placed NPCs +
        // materialized mobs stream back to players via the next Tick's viewport sync (~one tick later).
        foreach (var (map, id) in despawn) Broadcast(map, p => p.DespawnEntity(id));
        return (despawn.Count, npcs, populated.Count);
    }

    /// <summary>Map ids that currently have at least one player — used by the @reload path to pre-warm the
    /// terrain cache OUTSIDE this lock before <see cref="RebuildPopulation"/> re-materializes them (so the
    /// .map re-reads don't happen under the world lock, per the reload-stall fix).</summary>
    public List<ushort> PopulatedMapIds()
    {
        lock (_lock) return _maps.Where(kv => kv.Value.Players.Count > 0).Select(kv => kv.Key).ToList();
    }

    /// <summary>Add a shared mob to a map and stream it to everyone whose viewport it falls in (players
    /// out of range receive it later, as they approach, via <see cref="Tick"/>'s per-player sync).</summary>
    public void AddMob(ushort mapId, Mob mob)
    {
        lock (_lock) Map(mapId).Mobs.Add(mob);
        var one = new[] { mob };
        Broadcast(mapId, p => p.SyncMobs(one));
    }

    /// <summary>Give a mob to a player for a fixed duration, if nobody owns it already.</summary>
    public bool CharmMob(Mob mob, uint ownerId, int durMs)
    {
        lock (_lock)
        {
            if (mob.OwnerId != 0) return false;
            mob.OwnerId = ownerId;
            mob.PetExpiresAt = Environment.TickCount64 + durMs;
            mob.TargetId = 0;
            return true;
        }
    }

    /// <summary>Give a newly created summon to its owner; unlike Endear, this cannot be refused.</summary>
    public void OwnSummonedMob(Mob mob, uint ownerId, int durMs)
    {
        lock (_lock)
        {
            Debug.Assert(mob.OwnerId == 0, "a newly created summon must not already have an owner");
            mob.OwnerId = ownerId;
            mob.PetExpiresAt = Environment.TickCount64 + durMs;
            mob.Summoned = true;
            mob.TargetId = 0;
        }
    }

    /// <summary>Give a conjured creature a LIFESPAN but no owner — a scripted ambush (Master Dagger's
    /// assassins) rather than a pet. It hunts like any wild mob, it pays nothing when killed (that is what
    /// <see cref="Mob.Summoned"/> already means: made out of nothing seconds ago), and <see cref="Tick"/>
    /// despawns it when the timer lapses, on the same path a lapsed summon takes.</summary>
    public void ExpireUnowned(Mob mob, int durMs)
    {
        lock (_lock)
        {
            mob.PetExpiresAt = Environment.TickCount64 + durMs;
            mob.Summoned = true;
        }
    }

    /// <summary>Make a mob forget one player's threat for a fixed duration.</summary>
    public void ForgetPlayer(Mob mob, uint playerId, int durMs)
    {
        lock (_lock)
        {
            mob.AmnesiaBy = playerId;
            mob.AmnesiaUntil = Environment.TickCount64 + durMs;
            mob.ClearThreat(playerId);
            if (mob.TargetId == playerId) mob.TargetId = 0;
        }
    }

    /// <summary>Arm the one-hit damage multiplier left by a sleep-family hold.</summary>
    public void ArmDamageAmp(Mob mob, double mult, int durMs)
    {
        lock (_lock)
        {
            mob.DamageAmp = mult;
            mob.DamageAmpUntil = Environment.TickCount64 + durMs;
        }
    }

    /// <summary>Heal a living mob, capped at its maximum HP.</summary>
    public bool HealMob(Mob mob, int amount)
    {
        lock (_lock)
        {
            if (amount <= 0 || !mob.Alive) return false;
            mob.Hp = Math.Min(mob.MaxHp, mob.Hp + amount);
            return true;
        }
    }

    /// <summary>Give a world mob an item to carry (RTK's hand-off: the creature drops it back when killed —
    /// see <see cref="TryDamage"/>). Under <c>_lock</c> because <c>Mob.Handed</c> is a plain <c>List</c> that
    /// the death path ENUMERATES under this lock while the player's read loop was adding to it: not a stale
    /// read but an "InvalidOperationException: Collection was modified" thrown inside the world lock, or a
    /// list lost outright when two hands race the <c>??=</c>. Same shape as <see cref="HealMobFromScript"/>:
    /// the caller decides what to hand over, the world owns the write.</summary>
    internal void HandItemToMob(Mob mob, InvItem item)
    {
        lock (_lock) (mob.Handed ??= new()).Add(item);
    }

    /// <summary>Settle a harvest node's claim and take it if it is free: the lapse check, the lazy heal that
    /// follows it, the owner test and the re-stamp, all in ONE acquisition. Returns false if somebody else
    /// holds it, and the caller says so.
    ///
    /// <para><b>Why one method and not the three the issue sketched.</b> Reset-then-check-then-claim as
    /// separate lock-owning calls would put each write under the lock and leave the DECISION spanning three
    /// of them — two players swinging at the same node in the same instant could both see it unclaimed and
    /// both stamp their own id, which is the exact check-then-act shape #26 exists to remove. The semantics
    /// are unchanged: a claim lapses on time, a lapsed node heals to full, a live claim belonging to someone
    /// else refuses, and your own claim is refreshed.</para>
    ///
    /// <para>The node's HP damage is not here: it already goes through <see cref="TryDamage"/>, which takes
    /// this lock itself.</para>
    ///
    /// <para><b>The clock is read here, inside the acquisition, and not by the caller before it.</b> The
    /// first cut took <c>now</c> as a parameter, so the lapse decision and the new expiry both used a reading
    /// from before the wait for the lock: a claim stamped fractionally short, and a claim that lapsed DURING
    /// the wait not seen. The magnitude is one lock hold against a two-minute claim, so nothing a player
    /// could feel — but the code this replaced took no lock at all, so the window was new, and "the semantics
    /// are exactly what they were" is only literally true with the read on this side of it.</para>
    ///
    /// <para><paramref name="clock"/> is a test seam and nothing else. Production passes nothing and the
    /// ternary reads <see cref="Environment.TickCount64"/> directly, so no caller-supplied delegate runs
    /// inside <c>_lock</c> on any real path; a test's clock has to stay pure, because reaching back into the
    /// world from inside this acquisition is the deadlock the #90 rule is about.</para></summary>
    internal bool TryClaimHarvestNode(Mob node, uint claimant, long claimMs, Func<long>? clock = null)
    {
        lock (_lock)
        {
            long now = clock is null ? Environment.TickCount64 : clock();

            // A node nobody is touching has nothing to observe it, so a lapsed claim is settled lazily on the
            // next swing rather than on a tick — indistinguishable from RTK's timer, and no per-tick work.
            if (node.HarvestClaimUntil != 0 && now > node.HarvestClaimUntil)
            {
                node.Hp = node.MaxHp;
                node.HarvestClaimBy = 0;
                node.HarvestClaimUntil = 0;
            }
            if (node.HarvestClaimBy != 0 && node.HarvestClaimBy != claimant) return false;

            node.HarvestClaimBy = claimant;
            node.HarvestClaimUntil = now + claimMs;
            return true;
        }
    }

    /// <summary>The Lua hook's heal (<c>MobContext.heal</c>), under <c>_lock</c> — the write it used to do
    /// straight to <c>mob.Hp</c> from inside a script, with no lock held at all.
    ///
    /// <para><b>Why this is not just <see cref="HealMob"/>.</b> That method refuses a non-positive amount
    /// and refuses a dead mob; this one does neither, because a script can already do both today and
    /// adopting those guards would change what a Lua heal DOES rather than only when its write lands.
    /// <c>heal(-5)</c> currently takes HP off a creature, and a <c>heal</c> from <c>after_death</c>
    /// currently revives one (<see cref="Mob.Alive"/> is <c>Hp &gt; 0</c>). Neither looks intended, and
    /// neither is this PR's to decide — see the follow-up note on <c>MobContext.heal</c>.</para>
    ///
    /// <para>Called from inside the Lua gate, which is the legal direction: the rule (#90) is that
    /// <c>_lock</c> must not be held when ENTERING the gate, not that the gate may not call into the world.
    /// <c>MobContext.vanish</c> (<see cref="DespawnMob"/>) and <c>MobContext.say</c>
    /// (<see cref="BroadcastArea"/>) have taken this lock from inside a script since the host was
    /// written.</para></summary>
    internal void HealMobFromScript(Mob mob, int amount)
    {
        lock (_lock) mob.Hp = Math.Min(mob.MaxHp, mob.Hp + amount);
    }

    /// <summary>The Lua hook's status test (<c>MobContext.hasStatus</c>), under <c>_lock</c> — the read it
    /// used to do straight off the mob from inside a script, with no lock held.
    ///
    /// <para>The same shape and the same reason as <see cref="HealMobFromScript"/>, and the same direction of
    /// call (#90: <c>_lock</c> must not be held ENTERING the Lua gate; the gate calling into the world is
    /// fine). This one is a READ, which is why the #100 review had to point it out — the PR that fixed the
    /// heal said the remaining <c>MobContext</c> reads were simple field reads and out of scope, and that was
    /// true of every member except this one. <c>Mob.HasStatus</c> walks <c>Mob.Statuses</c>, a plain
    /// <c>Dictionary</c> that <c>World.SetStatus</c>/<c>ClearStatus</c> write under the lock, and an
    /// unsynchronised dictionary read against a concurrent write is not a stale answer — it can loop or throw
    /// inside the lookup. It is also the one <c>MobContext</c> member a shipped script actually calls
    /// (<c>game-data/mob_ai.lua</c>), so it is live, not theoretical.</para></summary>
    internal bool MobHasStatusFromScript(Mob mob, string category)
    {
        lock (_lock) return mob.HasStatus(category, Environment.TickCount64);
    }

    /// <summary>Hold a mob for a fixed duration unless it is already held.</summary>
    public bool HoldMob(Mob mob, int durMs)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.FrozenUntil > now) return false;
            mob.FrozenUntil = now + durMs;
            return true;
        }
    }

    /// <summary>Apply a player's targeted timed stat buff (Session.CastTargetBuff — e.g. Valor/Harden Armor on a
    /// pet) to a mob, refresh-not-stack by spell key. Taken under <c>_lock</c> so the mob.Buffs list mutation
    /// can't race the Tick's expiry-revert pass (which runs the same list under the lock).</summary>
    public void ApplyMobBuff(Mob mob, string stat, int amount, int durMs, string key)
    {
        if (string.IsNullOrEmpty(stat) || amount == 0 || durMs <= 0) return;
        lock (_lock)
        {
            mob.Buffs ??= new();
            for (int i = mob.Buffs.Count - 1; i >= 0; i--)   // refresh: revert + drop any prior cast of THIS spell
                if (mob.Buffs[i].Key == key) { mob.AdjustBuffField(mob.Buffs[i].Stat, mob.Buffs[i].Amount, -1); mob.Buffs.RemoveAt(i); }
            mob.AdjustBuffField(stat, amount, +1);
            mob.Buffs.Add(new Mob.TimedBuff { Stat = stat, Amount = amount, ExpiresAt = Environment.TickCount64 + durMs, Key = key });
        }
    }

    /// <summary>Apply a hostile categorised status to a mob (RTK <c>checkIfCast</c> + <c>setDuration</c>): the
    /// exclusivity slot, whichever AI field the status actually drives, and — for the ones RTK re-draws in
    /// <c>while_cast</c> — the repeating over-head animation.
    /// <para>Returns <b>false</b> if a status of <paramref name="category"/> is already running, which is the
    /// whole point: an offensive hold cannot be stacked or refreshed on top of itself, so the victim gets the
    /// hold's full window back before it can be re-applied.</para>
    /// <paramref name="hold"/> freezes movement (paralyze/sleep/slow), <paramref name="blind"/> takes its
    /// sight. Both false = a pure stat curse, which only occupies the slot.</summary>
    public bool ApplyMobStatus(Mob mob, string category, int durMs, bool hold, bool blind,
                               int fxAnim = 0, int fxSound = 0, int fxEveryMs = 0, string spellKey = "")
    {
        if (durMs <= 0) return false;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.HasStatus(category, now)) return false;          // RTK checkIfCast — no stacking, no refresh
            long until = now + durMs;
            mob.SetStatus(category, until, spellKey);
            // Take the LATER of any running hold and this one, so a short paralyze can't cut a long sleep
            // short — the two are different categories and are allowed to overlap.
            if (hold)  mob.FrozenUntil = Math.Max(mob.FrozenUntil, until);
            if (blind) { mob.BlindUntil = Math.Max(mob.BlindUntil, until); mob.TargetId = 0; }
            if (fxAnim > 0 && fxEveryMs > 0) mob.SetFxRepeat(fxAnim, fxSound, fxEveryMs, until, now);
            return true;
        }
    }

    /// <summary>Does this mob already carry a status of <paramref name="category"/>? (The read-only half of
    /// <see cref="ApplyMobStatus"/>, for a verb that needs to check before it commits to anything.)</summary>
    public bool MobHasStatus(Mob mob, string category)
    {
        lock (_lock) return mob.HasStatus(category, Environment.TickCount64);
    }

    /// <summary>The spell key holding <paramref name="category"/>'s slot on this mob right now, or "" if it is
    /// free. Lets a blocked cast say whether it bounced off ITS OWN running spell or somebody else's.</summary>
    public string MobStatusKey(Mob mob, string category)
    {
        lock (_lock) return mob.StatusKey(category, Environment.TickCount64);
    }

    /// <summary>Apply a venom/poison damage-over-time to a mob (RTK mage venom.lua family — the SAME engine the
    /// Rogue poison-dart trap drives, see <see cref="TriggerTrapLocked"/>'s "poison" case): ticks MaxHp*1% every
    /// 1500ms for a random window (1 + random(<paramref name="lowMs"/>, <paramref name="highMs"/>)), the per-tick
    /// damage clamped to [1, <paramref name="tickCap"/>] so it can never itself land the killing blow (World.Tick
    /// only ticks while Hp > the tick amount). Returns false if the mob is already venomed (checkIfCast(venoms)).
    /// <para><paramref name="flatTick"/> &gt; 0 replaces the proportional MaxHp*1% with a FLAT per-tick amount —
    /// RTK's Burn (Spells/NPCs/burn.lua) is the one member of this family whose while_cast deals a hardcoded
    /// 1000 rather than a percentage, and clamping a flat 1000 through <paramref name="tickCap"/> would silently
    /// weaken it against anything under 100k HP.</para></summary>
    /// <param name="fxAnim">Effect id to re-draw over the victim on every 1500ms tick, mirroring RTK's
    /// <c>while_cast_1500</c>, which calls <c>target:sendAnimation(1)</c> each time it deals damage — the
    /// poison is meant to keep flashing for its whole window, not once at cast. 0 = no repeat (the trap
    /// path, whose RTK script draws nothing per tick).</param>
    public bool PoisonMob(Mob mob, int tickCap, int lowMs, int highMs, uint ownerId, int flatTick = 0,
                          int fxAnim = 0, int fxSound = 0, string spellKey = "")
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.PoisonUntil > now) return false;                        // already venomed — RTK checkIfCast(venoms)
            mob.PoisonUntil     = now + 1 + Random.Shared.Next(lowMs, highMs + 1);
            mob.PoisonNextTick  = now + PoisonTickMs;
            mob.PoisonTickDam   = flatTick > 0 ? flatTick : Math.Clamp((int)(mob.MaxHp * 0.01), 1, tickCap);
            mob.PoisonOwnerId   = ownerId;
            mob.SetStatus("venoms", mob.PoisonUntil, spellKey);
            // Same 1500ms cadence as the damage tick, so the flash and the hit land together.
            if (fxAnim > 0) mob.SetFxRepeat(fxAnim, fxSound, PoisonTickMs, mob.PoisonUntil, now);
            return true;
        }
    }

    /// <summary>How many of this owner's pets (RTK Poet "Call of the Wild" summons) are currently alive on
    /// this map — the spawn cap in Content.PetCapFor is checked against this (RTK cotw_spawnCheck: same-map
    /// only, matching <c>player:getObjectsInMap</c>).</summary>
    public int PetCountFor(ushort mapId, uint ownerId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return 0;
            return m.Mobs.Count(mo => mo.Alive && mo.OwnerId == ownerId);
        }
    }

    /// <summary>The first living mob on (x,y) of <paramref name="mapId"/>, or null.</summary>
    public Mob? MobAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.FirstOrDefault(mo => mo.Alive && mo.X == x && mo.Y == y);
        }
    }

    /// <summary>
    /// A player's step: decide whether (<paramref name="nx"/>,<paramref name="ny"/>) is occupied and, if the
    /// step stands, write the mover's position — both inside ONE acquisition of <c>_lock</c> (#30).
    ///
    /// <para><b>Why it has to be one.</b> <c>HandleWalk</c> used to ask <see cref="MobAt"/>, then a
    /// <c>PlayerAt</c>/<c>PvpGhostAt</c> pair — three acquisitions, each taken and released — and then commit
    /// <c>_char.X/Y</c> under none of them. Two sessions stepping onto the same empty tile in the same instant
    /// both passed the check and both committed, and the tick's <c>occupied</c> snapshot was stale for any walk
    /// that committed mid-beat. A check whose answer is acted on after the lock is dropped is not a check. The
    /// two former helpers are gone with the race: their predicates live here, where the write can share them.</para>
    ///
    /// <para><b>What stays outside.</b> Everything that is not occupancy: the map edge, the ground pass flag,
    /// object walls, warps and scripted tiles. That is immutable content — no other thread can change it under
    /// the caller — so it does not belong in a critical section. The caller folds its own verdict in through
    /// <paramref name="otherwiseBlocked"/> so the commit is still suppressed, and passes
    /// <paramref name="enforceOccupancy"/> false for a no-clipper (<c>@clip</c>), whose reasons are still
    /// COMPUTED but do not hold it. They are computed rather than skipped so the caller's log line is
    /// byte-identical to the pre-#30 one in the case that prints them — a REFUSED step. A no-clipper's step
    /// is not refused, so it logs no suffix, exactly as before.</para>
    ///
    /// <para><b>Both predicates run even once one has fired.</b> The walk log prints " mob" and " player"
    /// independently, and the code it replaces computed both eagerly; short-circuiting would change it.</para>
    ///
    /// <para><b>Lock order (#29).</b> The caller is <c>HandleWalk</c>, already inside the mover's own state
    /// monitor, and this takes <c>_lock</c> second — session-state THEN world, the one legal order. Nothing
    /// here enters another session or Lua: the peer reads are the same plain <c>PlayerX</c>/<c>PlayerY</c>
    /// field reads the tick does under this lock, and the only write is to the mover, whose monitor the
    /// calling thread already holds.</para>
    /// </summary>
    /// <param name="ghostMover">The mover is a PvP ghost (Session.PvpGhostHidden), so it is blocked by other
    /// GHOSTS and no-clips through the living, instead of the other way round.</param>
    /// <returns>True if the position was written; false if the step was refused, and the caller snaps back.</returns>
    public bool TryMovePlayer(Session mover, ushort mapId, int nx, int ny,
                              bool ghostMover, bool enforceOccupancy, bool otherwiseBlocked,
                              out BlockReason why)
    {
        lock (_lock)
        {
            why = BlockReason.None;
            if (_maps.TryGetValue(mapId, out var m))
            {
                // A living mob occupies its tile, NPCs included — the same predicate MobAt uses.
                if (m.Mobs.Any(mo => mo.Alive && mo.X == nx && mo.Y == ny))
                    why |= BlockReason.Mob;
                // A PvP ghost is blocked ONLY by another ghost; everyone else only by a living player. The
                // dead never block the living either way, which is why the living branch tests !IsDead.
                if (ghostMover)
                {
                    if (m.Players.Any(p => !ReferenceEquals(p, mover) && p.IsDead && p.PlayerX == nx && p.PlayerY == ny))
                        why |= BlockReason.Ghost;
                }
                else if (m.Players.Any(p => !ReferenceEquals(p, mover) && !p.IsDead && p.PlayerX == nx && p.PlayerY == ny))
                {
                    why |= BlockReason.Player;
                }
            }
            // An unknown map blocks nothing and commits, which is what the three helpers did between them.
            if (otherwiseBlocked || (enforceOccupancy && why != BlockReason.None)) return false;

            mover.SetPositionUnderWorldLock((ushort)nx, (ushort)ny);
            return true;
        }
    }

    /// <summary>
    /// An ARRIVAL: resolve the tile a player is being put on and write it, both inside one acquisition of
    /// <c>_lock</c> (#99 part 1). <see cref="TryMovePlayer"/> is the same idea for a step; this is every
    /// other way a player's position changes — warps, scripted-tile entrances, world-map travel, the
    /// Gateway, GM teleports.
    ///
    /// <para><b>What this replaces.</b> <c>Session.EnterMap</c> clamped the requested tile to the map's
    /// bounds and assigned <c>_char.X/Y</c> with no lock held, and <c>@approach</c>/<c>@bring</c> chose their
    /// tile beforehand through <c>World.PeerAt</c> then <c>World.MobAt</c> — two more acquisitions, both
    /// released before the write. The tile a search picked could therefore be taken by the time it was
    /// written to.</para>
    ///
    /// <para><b>This is a lock-scope change and nothing else.</b> <see cref="ArrivalPolicy.Clamp"/> is the
    /// default and is what all 21 non-GM-adjacency callers pass, and it does exactly what the old inline
    /// clamp did, occupancy included: it does not test it. Two players through one door still land on one
    /// tile. Whether they SHOULD is #99's open source question, and answering it is not this method's job —
    /// see <see cref="ArrivalPolicy"/>.</para>
    ///
    /// <para><b>Lock order (#29)</b> is the same as <see cref="TryMovePlayer"/>'s: the caller is
    /// <c>Session.EnterMap</c>, already inside the mover's own state monitor, and this takes <c>_lock</c>
    /// second. The prewarm is outside the lock for the reason <see cref="EnterMap"/> documents — a cold
    /// <c>MapData.For</c> is a disk read, a full cell decode and a SQLite query, and none of that belongs in
    /// a critical section every player on every map is waiting behind.</para>
    /// </summary>
    /// <param name="from">Where the mover is coming from. <c>Session.EnterMap</c> has already called
    /// <see cref="LeaveMap"/> by the time it reaches here, so the mover is in no map's player list — and a
    /// search that cannot see them would offer them the tile they are standing on, which is not what
    /// <c>@approach</c>/<c>@bring</c> did before. Consulted only by
    /// <see cref="ArrivalPolicy.AdjacentFreeElseStack"/>, and only when its map matches the destination.</param>
    public void PlacePlayer(Session mover, ushort mapId, ushort xs, ushort ys, int x, int y,
                            ArrivalPolicy policy, FromTile from,
                            out ushort placedX, out ushort placedY)
    {
        // BEFORE the lock, and MapData.For rather than Prewarm: Prewarm returns early for a map with no
        // Maps.csv row, which would leave AdjacentFreeElseStack's own MapData.For to do the cold load —
        // a disk read, a full cell decode and a SQLite query — INSIDE the critical section every player on
        // every map is waiting behind. Reachable through @approach/@bring on an unregistered map, and the
        // path every policy test takes. The result is discarded; the point is the cache entry. Only the
        // searching policy needs it — Clamp never looks at terrain, so it does no terrain work at all, which
        // is also what the old inline clamp in Session.EnterMap did.
        if (policy == ArrivalPolicy.AdjacentFreeElseStack) _ = MapData.For(mapId, xs, ys);
        lock (_lock)
        {
            var (tx, ty) = policy switch
            {
                ArrivalPolicy.AdjacentFreeElseStack => AdjacentFreeLocked(mapId, xs, ys, x, y, from),
                _ => (x, y),
            };
            // The clamp itself is unchanged, and still last: the old code clamped whatever tile it was handed,
            // and a search's fallback tile has to go through it for the same reason.
            placedX = (ushort)Math.Clamp(tx, 0, Math.Max(0, xs - 1));
            placedY = (ushort)Math.Clamp(ty, 0, Math.Max(0, ys - 1));
            mover.SetPositionUnderWorldLock(placedX, placedY);
        }
    }

    /// <summary>The first free cardinal neighbour of (<paramref name="tx"/>,<paramref name="ty"/>), else that
    /// tile itself — <c>Session.ApproachTile</c>'s search, moved here so it shares the acquisition that writes
    /// its answer. The predicates are that method's, unchanged: in bounds, not ground- or object-wall-blocked
    /// for the direction stepped, and holding neither a player (any player, living or dead — the old
    /// <c>PeerAt</c> filtered neither) nor a living mob.</summary>
    private (int x, int y) AdjacentFreeLocked(ushort mapId, ushort xs, ushort ys, int tx, int ty, FromTile from)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var terrain = MapData.For(mapId, xs, ys);
        _maps.TryGetValue(mapId, out var m);

        for (int dir = 0; dir < 4; dir++)
        {
            int nx = tx + (dir == 1 ? 1 : dir == 3 ? -1 : 0);   // 0=N 1=E 2=S 3=W, as everywhere else
            int ny = ty + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            if (nx < 0 || ny < 0 || nx >= xs || ny >= ys) continue;
            if (terrain is not null && terrain.BlockedMove(nx, ny, dir)) continue;
            // The mover still holds the tile it is leaving; see the `from` parameter on PlacePlayer.
            if (from.Map == mapId && from.X == nx && from.Y == ny) continue;
            if (m is not null && m.Players.Any(p => p.PlayerX == nx && p.PlayerY == ny)) continue;
            if (m is not null && m.Mobs.Any(mo => mo.Alive && mo.X == nx && mo.Y == ny)) continue;
            return (nx, ny);
        }
        return (tx, ty);
    }

    /// <summary>Write a player's position with no occupancy test — the walk handler's snap-back.
    /// <see cref="TryMovePlayer"/> is the move that ACQUIRES a tile and therefore has to check; this is the
    /// one that gives a tile up, or re-asserts the one the client says the mover is already on, so there is
    /// nothing to check. It still goes through <c>_lock</c>: X and Y are two writes, and every reader of them
    /// (this class's tile scans, the tick's <c>occupied</c> snapshot) reads both under this lock, so a
    /// snap-back outside it is a torn (new X, old Y) waiting to be read.</summary>
    public void SetPlayerPosition(Session mover, int x, int y)
    {
        lock (_lock) mover.SetPositionUnderWorldLock((ushort)x, (ushort)y);
    }

    /// <summary>The nearest living, non-NPC mob within <paramref name="radius"/> tiles (Chebyshev) of a
    /// point matching <paramref name="match"/>, or null. Used by the 'r' ride key (RTK clif_findmount) to
    /// locate a rideable "horse" mob — called with <c>radius 0</c> at the player's <c>FrontTile()</c> so it
    /// only matches the exact tile faced (cardinal only, matching the player's own melee reach).</summary>
    public Mob? MobNear(ushort mapId, int x, int y, int radius, Func<Mob, bool> match)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.Where(mo => mo.Alive && !mo.IsNpc && match(mo)
                                       && Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)) <= radius)
                          .OrderBy(mo => Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)))
                          .FirstOrDefault();
        }
    }

    /// <summary>Remove a live mob from the map WITHOUT a kill (no loot roll, no exp) — used when a player
    /// rides it away ('r' key). If it's a spawn point's mob, the point is freed to respawn like a normal
    /// death; an ad-hoc mob (e.g. one summoned by a dismount) is just dropped. Broadcasts the despawn.</summary>
    public bool DespawnMob(ushort mapId, Mob mob)
    {
        lock (_lock)
        {
            if (!mob.Alive || mob.IsNpc) return false;
            if (!_maps.TryGetValue(mapId, out var m)) return false;
            m.Mobs.Remove(mob);
            _spawnDirector.ReleasePoint(mob);
        }
        Broadcast(mapId, p => p.DespawnEntity(mob.Id));
        return true;
    }

    /// <summary>The player standing on (x,y) of <paramref name="mapId"/>, or null. Used by the ';' look key
    /// (RTK clif_parselookat checks PC before mob/item/NPC).</summary>
    public Session? PeerAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Players.FirstOrDefault(p => p.PlayerX == x && p.PlayerY == y);
        }
    }

    /// <summary>The connected player with this character name (case-insensitive, any map), or null if
    /// they're offline. Used by whisper/tell (RTK clif_parsewisp's target lookup).</summary>
    public Session? FindPlayer(string name)
    {
        // CharName, not Snapshot().Name: this runs under _lock, and Snapshot takes the session's state
        // monitor, which is the wrong way round (#29 — session state THEN _lock). Building a whole
        // PlayerSnapshot — face, armour, weapon, shield, dye, all off the equipment list — per player per
        // lookup, to read one string, was never the intent either.
        lock (_lock)
            return _maps.Values.SelectMany(m => m.Players)
                                .FirstOrDefault(p => string.Equals(p.CharName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The connected player with this entity id (any map), or null. Used by click-profile's "view
    /// another player" path (RTK <c>clif_clickonplayer</c>, §9.5/§11l) and the exchange-initiate opcode
    /// <c>0x4A</c> (RTK <c>clif_parse_exchange</c> type 0), both of which address a player by id — the
    /// client already knows it from the entity it rendered — rather than by name.</summary>
    public Session? PlayerById(uint id)
    {
        lock (_lock)
            return PlayerByIdLocked(id);
    }

    /// <summary>Same lookup for callers that already hold <c>_lock</c>. The monitor is re-entrant so taking it
    /// twice would work, but saying which methods expect it is how this file stays readable.</summary>
    private Session? PlayerByIdLocked(uint id)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        return _maps.Values.SelectMany(m => m.Players).FirstOrDefault(p => p.PlayerId == id);
    }

    // One gate covers the entire disk-to-live sequence below, not just Content.Load: cache invalidation,
    // staff reload, terrain pre-warm and population rebuild must observe the same content generation. A GM
    // invokes this on the session read loop, so contention is bounded rather than stalling packet handling.
    private static readonly SemaphoreSlim ReloadGate = new(1, 1);
    private static readonly TimeSpan ReloadWaitTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Hot-reload every file-backed registry and rebuild the world population from it — the work behind the
    /// <c>@reload</c> GM command, lifted out of <see cref="Session"/> because the OTHER caller has no session:
    /// a content-only deploy drops a <c>run/reload_now</c> sentinel and <see cref="RestartSchedule.Loop"/>
    /// calls this. That is the whole point of the content lane — a CSV or Lua fix ships without kicking anyone.
    ///
    /// <para>Returns (ok, report). A load error keeps the previous content publication and comes back
    /// <c>ok: false</c> rather than taking down a running world.</para>
    /// </summary>
    public (bool ok, string report) ReloadFromDisk()
    {
        if (!ReloadGate.Wait(ReloadWaitTimeout)) return (false, "reload already in progress");
        try
        {
            string summary;
            try { summary = Content.Reload(); }
            catch (Exception e)
            {
                Log.Error("content reload failed — the previous content publication remains live", e);
                return (false, e.Message);
            }
            ObjectFlags.Invalidate();   // Overrides arrive in Content.Load's commit; this only drops the SObj.tbl cache
            MapData.Invalidate();
            StaffAccounts.Load();   // the staff rosters are file-backed config too — promote/demote without a restart
            // Pre-warm the terrain cache for populated maps OUTSIDE _lock, so RebuildPopulation's re-materialization
            // (FreeSpawnTile/SpawnDirector.PickAreaHome -> MapData.For) hits a warm cache instead of reading .map files from disk
            // while holding the world lock (the old reload-stall).
            foreach (var mapId in PopulatedMapIds())
                if (Content.Maps.TryGetValue(mapId, out var mi)) MapData.For(mapId, mi.Xs, mi.Ys);
            var (mobs, npcs, maps) = RebuildPopulation();
            return (true, $"{summary}. Rebuilt population: {mobs} mob(s) torn down, {npcs} NPC(s) placed, " +
                          $"{maps} live map(s) re-materialized; map cache cleared.");
        }
        finally { ReloadGate.Release(); }
    }

    /// <summary>Make every mob everywhere forget <paramref name="playerId"/> — target and threat table both.
    /// The @peace toggle calls this on switch-on so a mob already mid-chase lets go; staying ignored from
    /// then on is the scan-side exclusions in Tick (a peace player is skipped by unprovoked aggro and the
    /// stuck-mob retarget). Damage still re-acquires: TryDamage points a mob at whoever hit it, peace or not.</summary>
    public void PacifyPlayer(uint playerId)
    {
        lock (_lock)
            foreach (var (_, pm) in _maps)
                foreach (var mob in pm.Mobs)
                {
                    mob.ClearThreat(playerId);
                    if (mob.TargetId == playerId) { mob.TargetId = 0; mob.AttackTimer = 0; }
                }
    }

    /// <summary>Every connected player, across every map — a server-wide (not map-scoped) roster snapshot.
    /// Used by channels that reach beyond one map, like subpath chat (RTK clif_sendsubpathmessage loops
    /// every session, not just one map's block list).</summary>
    public List<Session> AllPlayers()
    {
        lock (_lock)
            return _maps.Values.SelectMany(m => m.Players).ToList();
    }

    /// <summary>How many players are in the world right now. Separate from <see cref="AllPlayers"/> because
    /// the status publisher wants only the number, and materialising every session into a list on a timer to
    /// read <c>.Count</c> off it is pure garbage.</summary>
    public int OnlinePlayerCount()
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var m in _maps.Values) n += m.Players.Count;
            return n;
        }
    }

    /// <summary>Duplicate-login guard: atomically register <paramref name="s"/> as the online session for
    /// <paramref name="key"/> (CharacterStore.Key(username)), returning whatever session previously held
    /// that slot via <paramref name="old"/> (null if this is a fresh login). Called from HandleArrival
    /// BEFORE the character is loaded from disk, so a second concurrent arrival for the same account can
    /// never both pass unnoticed — the dictionary write is atomic under _lock. The caller (HandleArrival)
    /// is responsible for kicking <paramref name="old"/> (Session.KickForReplacement) so its state is
    /// flushed before the new session's own Load runs.</summary>
    public void RegisterOnline(string key, Session s, out Session? old)
    {
        lock (_lock)
        {
            _online.TryGetValue(key, out old);
            _online[key] = s;
        }
    }

    /// <summary>Remove <paramref name="s"/> from the online registry, but ONLY if it still owns that slot —
    /// a compare-and-remove so a session that was already kicked/replaced (RegisterOnline overwrote its
    /// slot with the newer session) can't accidentally evict the session that replaced it when its own
    /// (now-stale) teardown finally runs.</summary>
    public void Unregister(string key, Session s)
    {
        lock (_lock)
        {
            if (_online.TryGetValue(key, out var cur) && ReferenceEquals(cur, s))
                _online.Remove(key);
        }
    }

    /// <summary>Periodic crash-safety backstop (see AutoSaveLoop): flush every connected player's pending
    /// mutation, regardless of the per-session AutoSaveMs throttle. Its unique job is an IDLE dirty player
    /// (mutated, then stopped sending packets, so their own read-loop FlushIfDue never gets another
    /// iteration to fire on) — an ACTIVE player is already covered by their own on-thread flush.</summary>
    private void AutoSaveTick()
    {
        foreach (var s in AllPlayers()) FlushIsolated(s, "autosave");
    }

    /// <summary>One player's flush, fenced so it can't take the rest of a sweep with it. Before this, one
    /// throw from FlushNow unwound the whole foreach in AutoSaveLoop's catch, and every player AFTER the
    /// unlucky one in that snapshot silently missed the interval. Idle dirty players are exactly who the
    /// sweep exists for (see AutoSaveTick), so a skipped sweep is a real crash-safety hole, not a delay.
    ///
    /// <para>The throw it was written for was a collection mutated under the serializer by that player's own
    /// thread; #29 closed that off — FlushNow now serializes a snapshot taken under the session's state
    /// monitor — so what is left to catch here is a bad disk. The fence stays: "one player's failure must not
    /// cost every later player their interval" is worth keeping whatever the cause.</para>
    ///
    /// <para>Returns whether the flush succeeded, because the two callers face different consequences and
    /// must say different things. The periodic sweep genuinely does retry on its next interval. The
    /// shutdown flush has no next interval: a throw there is the player's last state LOST, and reporting it
    /// as "retried" — or, worse, counting it as saved — is the one thing an operator reading the final
    /// lines of a log must not be told. <paramref name="lastChance"/> picks the wording.</para></summary>
    private static bool FlushIsolated(Session s, string sweep, bool lastChance = false)
    {
        try { s.FlushNow(); return true; }
        catch (Exception e)
        {
            Log.Error($"{sweep}: flush of '{s.UserKey}' ({s.Remote}) threw — " +
                      (lastChance ? "save LOST — process is exiting, there is no retry"
                                  : "that player's save is retried next sweep, the others continue"), e);
            return false;
        }
    }

    // Own thread (see the constructor): each FlushNow serializes a multi-KB character graph to JSON and does
    // a synchronous SQLite write, so a sweep of a full server is a long block. On the thread pool that was
    // a pool thread held for the duration, competing with the heartbeat.
    private void AutoSaveLoop()
    {
        while (true)
        {
            Thread.Sleep(Session.AutoSaveMs);
            // AutoSaveTick isolates each player's flush; this only sees a throw from AllPlayers itself.
            try { AutoSaveTick(); }
            catch (Exception e) { Log.Error("autosave sweep threw — retrying on the next interval", e); }
        }
    }

    /// <summary>Graceful-shutdown flush: force-save every connected player right now, ignoring the dirty
    /// flag entirely is NOT needed here — FlushNow already no-ops a clean session cheaply. Cannot help
    /// against a hard crash/kill — that's what the periodic AutoSaveLoop sweep + each session's own
    /// on-thread flush bound instead.
    ///
    /// <para>Returns (saved, failed) rather than the population count. It used to return
    /// <c>players.Count</c> whatever happened, so the shutdown hook's "flushed N player(s)" was the number
    /// of players CONNECTED, not the number persisted — a run that lost three characters' last hour logged
    /// exactly what a clean one did. The caller reports both numbers (see TkListener.Shutdown).</para></summary>
    public (int saved, int failed) SaveAllPlayers()
    {
        var players = AllPlayers();
        int saved = 0, failed = 0;
        foreach (var s in players)
        {
            if (FlushIsolated(s, "shutdown save", lastChance: true)) saved++;
            else failed++;
        }
        return (saved, failed);
    }

    /// <summary>NPCs (stationary, IsNpc) within <paramref name="radius"/> tiles (Chebyshev) of a point, nearest
    /// first. Used to route a player's speech to a nearby NPC's say-handler (RTK onSayClick).</summary>
    public List<Mob> NpcsNear(ushort mapId, int x, int y, int radius)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return new();
            return m.Mobs.Where(mo => mo.IsNpc &&
                                      Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)) <= radius)
                         .OrderBy(mo => Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)))
                         .ToList();
        }
    }

    /// <summary>The nearest living mob with this identifier within <paramref name="radius"/> tiles (Chebyshev)
    /// of a point, or null. One pass under the world lock — the alternative, probing <see cref="MobAt"/> over
    /// a box, takes the lock once per tile and gets expensive fast for anything wider than a couple of steps.</summary>
    public Mob? NearestMobByKey(ushort mapId, int x, int y, int radius, string key)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            Mob? best = null; int bestDist = int.MaxValue;
            foreach (var mo in m.Mobs)
            {
                if (!mo.Alive || !string.Equals(mo.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                int d = Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y));
                if (d <= radius && d < bestDist) { bestDist = d; best = mo; }
            }
            return best;
        }
    }

    /// <summary>The live world mob with this entity id on the map, or null (used by targeted spell casts,
    /// where the client sends the target's entity id rather than a tile).</summary>
    public Mob? MobById(ushort mapId, uint id)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.FirstOrDefault(mo => mo.Alive && mo.Id == id);
        }
    }

    /// <summary>Apply damage under the lock (so concurrent attackers can't double-kill). Returns
    /// false if the mob was already dead; otherwise sets <paramref name="died"/> and, on death,
    /// removes it from the map, schedules its spawn point to respawn, and rolls floor loot (which this
    /// method drops + broadcasts). The caller still broadcasts the damage number / corpse despawn.
    /// <paramref name="attackerId"/> (0 = none, e.g. a session-local debug hit) marks the mob as targeting
    /// that player — <see cref="Tick"/> then has it chase and fight back instead of just wandering.</summary>
    public bool TryDamage(ushort mapId, Mob mob, int dmg, out bool died, uint attackerId = 0)
    {
        died = false;
        List<GroundItem>? drops = null;
        lock (_lock)
        {
            if (!mob.Alive || mob.IsNpc) return false;   // NPCs are indestructible (a click talks to them, not fights)
            // Sleep-family amplifier: "the NEXT attack upon the target" lands harder (Doze 1.3x, Sleep 1.5x).
            // Consumed here so it applies to exactly one hit, and applied before the HP subtraction so the
            // over-head bar and any kill it causes both reflect the amplified number.
            double amp = mob.TakeDamageAmp(Environment.TickCount64);
            if (amp > 1.0) dmg = (int)Math.Round(dmg * amp);
            // A mythic boss does not simply die (RTK mob_ai_mythic.on_attacked). Its lethal-blow ladder, in
            // RTK's order, because the order is the mechanic:
            //
            //   1. FIRST lethal blow of its life -> Last Stand (RTK gates this on `mob.magic == 100`, and the
            //      spell spends all 100 with nothing to give it back, so it is once per life).
            //   2. Otherwise OVERKILL: a blow big enough to punch through its HP *and* the heal it would get
            //      (`attacker.damage >= mob.health + healAmount`) kills it outright, no roll. This is the
            //      whole reason a boss is beatable — you out-damage the save rather than out-waiting it.
            //      Note RTK skips this test on the Last Stand branch, so the first brink is always survivable.
            //   3. Then the save roll — 1/2, 2/3 or 3/4 by tier. Fail it and the blow lands normally.
            //
            // Runs BEFORE the subtraction, so the heal is what the blow lands on.
            if (dmg >= mob.Hp && Content.MobBosses.TryGetValue(mob.Key, out var boss) && boss.HealAmount > 0)
            {
                bool lastStand = !mob.SecondWindUsed && boss.LastStandMs > 0;
                // The save roll runs on the Last Stand branch too: RTK casts the spell and *then* rolls, so a
                // boss really can go into its last stand and drop dead on the same blow.
                bool overkill  = !lastStand && dmg >= mob.Hp + boss.HealAmount;
                bool saved     = !overkill && Random.Shared.Next(Math.Max(2, boss.HealChance)) != 0;

                if (lastStand)
                {
                    // RTK Spells/last_stand.lua: scrub own curses, animation 11, 8s duration, and PARALYSE
                    // ITSELF for it. The boss stands frozen and heals every tick while the window runs — it
                    // is a window to burst it down or back off, not an untouchable enrage.
                    mob.SecondWindUsed = true;
                    mob.LastStandUntil = Environment.TickCount64 + boss.LastStandMs;
                    mob.FrozenUntil = Math.Max(mob.FrozenUntil, mob.LastStandUntil);
                    mob.ClearStatus("curses"); mob.ClearStatus("minorcurses");
                    _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, LastStandAnim, boss.Sound));
                }

                if (saved)
                {
                    mob.Hp = Math.Min(mob.MaxHp, mob.Hp + boss.HealAmount);
                    if (!lastStand) _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, boss.Anim, boss.Sound));
                }
            }

            mob.Hp -= dmg;
            died = !mob.Alive;
            // Threat accrues with the damage (RTK swing.lua `player:addThreat(mob.ID, damage)`), whether or
            // not this hit takes the target — Tick's retarget then reads it. Counted BEFORE the death check
            // so the killing blow still counts, which matters for a pet deciding what to assist against.
            mob.AddThreat(attackerId, dmg);
            // Hitting something that has forgotten you reminds it (RTK amnesia.lua on_takedamage_while_cast).
            // Only the forgotten player breaks it — anyone else can keep hitting it without giving you away.
            if (mob.AmnesiaBy != 0 && mob.AmnesiaBy == attackerId) { mob.AmnesiaBy = 0; mob.AmnesiaUntil = 0; }

            // Lua AI hooks for this creature, if it has any (queued — see QueueHook).
            var actor = attackerId == 0 ? null : PlayerByIdLocked(attackerId);
            QueueHook(MobScript.OnAttacked, mapId, mob, actor);
            if (died) QueueHook(MobScript.AfterDeath, mapId, mob, actor);
            // Provoked -> fight back (mob_ai_normal on_attacked). Getting hit ALWAYS wins: it drops whatever
            // mob it was scrapping with (a pet) and re-points it at the player, and it overrides the
            // stuck-mob retarget in Tick — so zapping something always drags its aggro onto you, wall or no
            // wall, however unreachable you are.
            if (!died && attackerId != 0) { mob.TargetId = attackerId; mob.TargetMobId = 0; mob.DetourDir = NoDetour; mob.DetourLeft = 0; }
            // Being hit wakes a sleeping creature (RTK sleep.lua on_takedamage_while_cast). Paralyze
            // deliberately does NOT clear here — a paralyzed mob stays held while you beat on it.
            if (!died && mob.HasStatus("sleeps", Environment.TickCount64))
            {
                mob.ClearStatus("sleeps"); mob.FrozenUntil = 0; mob.FxRepeatUntil = 0;
            }
            // …unless it's PREY, which has no fight in it: being hurt by anything (a spell, a trap, a swing)
            // panics it instead. Tick clears the TargetId set just above before it can ever be acted on; this
            // is what makes a spell as alarming as a sword. A pure MISS never reaches here — Session.ResolveSwing
            // calls Spook directly for that case.
            if (!died && mob.Flees) mob.PanicUntil = Environment.TickCount64 + PanicMs;
            // Sute, below the red bar, buys the person hitting him one answering swing before he breaks off
            // again — two if he is cornered and cannot run at all. See SuteAi.OnDamaged.
            if (!died && mob.Key == SuteAi.MobKey) SuteAi.OnDamaged(mob, Environment.TickCount64);
            if (died && _maps.TryGetValue(mapId, out var m))
            {
                m.Mobs.Remove(mob);
                _spawnDirector.RecordDeath(mapId, mob);   // the boss death registry, and its spawn point freed
                // Loot is a property of the CREATURE, not of which system placed it. Rolling it inside the
                // spawn-point branch above meant a mob without a point dropped nothing at all — which, once
                // the hunting maps moved to batch groups, would have been every mob in them. Gated on
                // WorldSpawned so a conjured pet still can't be farmed for its wild counterpart's drops.
                if (mob.WorldSpawned && Content.MobByKey(mob.Key) is { } dropDef)
                    drops = RollDropsLocked(m, mob, dropDef);  // adds to m.Items under the lock
                // Anything a player HANDED this creature falls back to the floor when it dies — regardless of
                // whether the creature is a loot-dropper (the sword handed to a cat). A no-kill DespawnMob never
                // reaches here, so a ridden-away / quest-released creature keeps carrying them (they're lost,
                // which is the intended "you gave it away" for those paths). Each keeps its Dura/name/owner.
                if (mob.Handed is { Count: > 0 })
                {
                    drops ??= new List<GroundItem>();
                    foreach (var h in mob.Handed)
                    {
                        var hdef = Content.ItemById(h.ItemId);
                        if (hdef is null) continue;
                        var gi = new GroundItem { Id = _nextItemId++, ItemId = h.ItemId, X = mob.X, Y = mob.Y,
                            Amount = h.Amount, Dura = h.Dura, Graphic = hdef.Icon, CustomName = h.CustomName, Owner = h.Owner };
                        m.Items.Add(gi);
                        drops.Add(gi);
                    }
                }
            }
        }
        if (drops is not null)
            foreach (var gi in drops) Broadcast(mapId, p => p.ShowGroundItem(gi));
        return true;
    }

    /// <summary>Roll a slain mob's loot, add each stack to the floor, and return them for broadcasting.
    /// Caller holds <c>_lock</c>.</summary>
    private List<GroundItem> RollDropsLocked(MapState m, Mob mob, MobDef def)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var drops = new List<GroundItem>();
        foreach (var roll in Content.RollDrops(def, Random.Shared))
        {
            GroundItem gi;
            if (roll.Gold)
            {
                // Mirrors Session.HandleDropGold's icon tiering (coins_1 / _2_99 / _100_999).
                ushort gfx = roll.Amount < 2 ? (ushort)22 : roll.Amount < 100 ? (ushort)73 : (ushort)72;
                gi = new GroundItem { Id = _nextItemId++, ItemId = -1, X = mob.X, Y = mob.Y, Amount = roll.Amount, Graphic = gfx };
            }
            else
            {
                gi = new GroundItem { Id = _nextItemId++, ItemId = roll.Item!.Id, X = mob.X, Y = mob.Y,
                    Amount = roll.Amount, Graphic = roll.Item.Icon, Dura = roll.Item.Durability };
            }
            m.Items.Add(gi);
            drops.Add(gi);
        }
        return drops;
    }

    // ---- ground items (dropped/thrown stacks lying on the floor) -------------------------------

    /// <summary>Drop <paramref name="gi"/> onto <paramref name="mapId"/> and draw it for everyone there.</summary>
    public void DropItem(ushort mapId, GroundItem gi)
    {
        lock (_lock) Map(mapId).Items.Add(gi);
        Broadcast(mapId, p => p.ShowGroundItem(gi));
    }

    /// <summary>Read-only snapshot of the floor items on a map (for drawing to a newcomer / on redraw).</summary>
    public GroundItem[] ItemsOn(ushort mapId)
    {
        lock (_lock)
            return _maps.TryGetValue(mapId, out var m) ? m.Items.ToArray() : Array.Empty<GroundItem>();
    }

    /// <summary>Remove the topmost (last-dropped) floor item on (x,y) under the lock — so two players
    /// grabbing the same tile can't both win — and despawn it for everyone. Null if the tile is empty.
    /// <para><paramref name="pickerId"/> is who is grabbing (0 = an anonymous/system grab, which ignores locks).
    /// Death-pile stacks reserved for someone else are SKIPPED rather than taken, and
    /// <paramref name="blocked"/> comes back true so the caller can say why nothing happened — RTK
    /// <c>canLoot</c>'s "That item does not belong to you." Set <paramref name="ownOnly"/> to take ONLY the
    /// picker's own still-locked pile and pass over everything else (RTK <c>isYours</c>, the F1 recovery).</para></summary>
    public GroundItem? PickUp(ushort mapId, int x, int y, uint pickerId = 0, bool ownOnly = false)
        => PickUp(mapId, x, y, pickerId, ownOnly, out _);

    /// <inheritdoc cref="PickUp(ushort, int, int, uint, bool)"/>
    public GroundItem? PickUp(ushort mapId, int x, int y, uint pickerId, bool ownOnly, out bool blocked)
    {
        GroundItem? gi = null;
        blocked = false;
        lock (_lock)
        {
            if (_maps.TryGetValue(mapId, out var m))
            {
                // last match = most recently dropped (drawn on top)
                for (int i = m.Items.Count - 1; i >= 0; i--)
                {
                    var it = m.Items[i];
                    if (it.X != x || it.Y != y) continue;
                    if (ownOnly) { if (!it.BelongsTo(pickerId)) continue; }
                    else if (pickerId != 0 && it.LockedAgainst(pickerId)) { blocked = true; continue; }
                    gi = it; m.Items.RemoveAt(i); break;
                }
            }
        }
        if (gi is not null) { blocked = false; Broadcast(mapId, p => p.DespawnEntity(gi.Id)); }
        return gi;
    }

    /// <summary>Despawn every mob on a map for all its players (the shared @kill).</summary>
    public int ClearMap(ushort mapId)
    {
        uint[] ids;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m) || m.Mobs.Count == 0) return 0;
            ids = m.Mobs.Select(mo => mo.Id).ToArray();
            m.Mobs.Clear();
        }
        foreach (var id in ids) Broadcast(mapId, p => p.DespawnEntity(id));
        return ids.Length;
    }

    // ---- shared mob AI (one heartbeat drives every wandering mob on every map) -----------------

    /// <summary>A tick this slow (work OR scheduling delay, ms) gets a diagnostic line. 150ms is a quarter of
    /// the heartbeat — well clear of normal jitter, low enough to catch a stall long before a player would
    /// call it lag. <c>P1998_SLOW_TICK_MS</c> tunes it; 0 disables the watchdog.</summary>
    private static readonly int SlowTickMs =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_SLOW_TICK_MS"), out var st) && st >= 0 ? st : TickMs / 4;

    private long _lockWaitMs;   // how long the last Tick() waited to acquire _lock (watchdog attribution)

    // Fixed-cadence heartbeat on its own thread. Schedules against an absolute deadline rather than sleeping
    // TickMs between iterations, so the tick's own work doesn't accumulate into drift (the old
    // `await Task.Delay(600)` loop actually ran at ~612ms). If we fall a whole period behind we resync to
    // now instead of trying to catch up — the world would rather skip a beat than run several back-to-back.
    private void TickLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long next = TickMs;
        while (true)
        {
            int wait = (int)(next - clock.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);

            // Measured BEFORE the tick body: this is time the thread was not running at all (GC pause, OS
            // preemption, the machine swapping) as opposed to time the tick spent working.
            long late = clock.ElapsedMilliseconds - next;
            next += TickMs;
            if (next <= clock.ElapsedMilliseconds) next = clock.ElapsedMilliseconds + TickMs;

            long t0 = clock.ElapsedMilliseconds;
            var gc0 = GC.GetTotalPauseDuration();
            _lockWaitMs = 0;
            try { Tick(); }
            catch (Exception e) { Log.Error("world tick threw — this beat is abandoned, the next runs on schedule", e); }

            if (SlowTickMs <= 0) continue;
            long work = clock.ElapsedMilliseconds - t0;
            if (work < SlowTickMs && late < SlowTickMs) continue;
            long gcMs = (long)(GC.GetTotalPauseDuration() - gc0).TotalMilliseconds;
            // Read this line as: LATE with gc ~= late  -> a GC pause. LATE with gc ~0 -> the OS didn't
            // schedule us (machine-wide contention). WORK with lock ~= work -> a session thread was holding
            // _lock (something slow ran inside a critical section). WORK with lock ~0 -> the tick body
            // itself is genuinely too big for the population it's driving.
            Log.Info($"!! SLOW TICK: work {work}ms (lock-wait {_lockWaitMs}ms), late {late}ms, gc {gcMs}ms — " +
                     $"{PlayerCount} player(s), {MobCount} mob(s) on {ActiveMapCount} active map(s)");
        }
    }

    /// <summary>Counts for the slow-tick diagnostic. Cheap, and only read on the watchdog path.</summary>
    private int PlayerCount    { get { lock (_lock) return _maps.Sum(kv => kv.Value.Players.Count); } }
    private int MobCount       { get { lock (_lock) return _maps.Sum(kv => kv.Value.Mobs.Count); } }
    private int ActiveMapCount { get { lock (_lock) return _maps.Count(kv => kv.Value.Players.Count > 0); } }

    // One heartbeat: (1) refill dead spawn points that are due, (2) wander every live mob OR, if provoked,
    // chase and swing at its target instead (queuing any landed swings), (3) reconcile each player's
    // viewport (mobs that moved in/out of view, plus this tick's respawns, appear/disappear), (4) stream
    // moves/turns to observers, (4.5) apply this tick's queued mob swings, (5) regen every connected
    // player, (6) broadcast the day/night hour and any map's changed weather. All map mutation happens
    // under the lock; no socket I/O does. Only maps with at least one player are processed — an empty map's
    // roster stays put. (1)-(2) are this method; (3)-(6) are FlushTick, after the lock is released.
    private void Tick()
    {
        _tick++;
        // This beat's outbound work, in one object: the locked phases below fill it, FlushTick drains
        // it once the lock is released (World.MobAiTick.cs).
        var q = new TickQueues();

        // (0) Warm every active map's terrain BEFORE taking the lock. Both the respawn refill (Materialize ->
        // FreeSpawnTile) and the wander loop below call MapData.For, which on a miss reads the .map off disk,
        // decodes every cell and runs a SQLite query. Under _lock that stalled the whole world; out here it
        // costs nothing on the overwhelmingly common cache-hit path. See MapData.Prewarm.
        ushort[] active;
        lock (_lock) active = _maps.Where(kv => kv.Value.Players.Count > 0).Select(kv => kv.Key).ToArray();
        foreach (var id in active) MapData.Prewarm(id);

        long lockT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            // Time spent BLOCKED here means another thread was inside a _lock critical section. The
            // slow-tick watchdog prints it, which is what distinguishes "someone else stalled us" from
            // "this tick body is too slow".
            _lockWaitMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lockT0) * 1000 / System.Diagnostics.Stopwatch.Frequency;

            // (1) respawns: refill any due spawn point on a map someone is watching. Points only — the
            // hunting maps refill in batches at (1.1), not one mob at a time as they die.
            _spawnDirector.RespawnDuePoints(_tick);

            // (1.1) batch refills: every due spawn group on a map someone is hunting (RTK's spawner NPC,
            // whose own `#pc > 0` test this mirrors). A map nobody is on is skipped here and caught by
            // EnsureMaterialized when someone walks in, so the room is full before their viewport is built
            // rather than filling in around them. Sampled every BatchSweepTicks — these clocks are in whole
            // seconds and the shortest is 2s, so there is nothing to gain from looking every 600ms.
            _spawnDirector.RefillDueGroups(_tick);

            // (1.2) morph expiry (Session.CastMorph/RevertMorph): purely cosmetic per-player visual state
            // with no server-side entity of its own — the revert broadcast is socket I/O, so it's deferred
            // outside the lock same as trapDamage/expiredPets below.
            foreach (var (_, pm) in _maps)
                foreach (var p in pm.Players)
                    if (p.IsMorphExpired) q.ExpiredMorphs.Add(p);
            foreach (var (_, pm) in _maps)
                foreach (var p in pm.Players)
                    if (p.IsStealthExpired) q.ExpiredStealth.Add(p);   // faded (invisible-spell) look lapsed with no hit — revert

            // (1.3) bladestorm auto-expiry: an untriggered decoy despawns silently after its 21s lifetime —
            // traps have no ground graphic (same precedent as the hazard family), so this is a plain in-lock
            // removal, no broadcast/deferral needed.
            foreach (var (_, pm) in _maps)
                pm.Traps.RemoveAll(t => t.ExpiresAt != 0 && Environment.TickCount64 >= t.ExpiresAt);

            // (1.5) forage top-up: on a slow cadence, refill each forage box (chestnuts &c.) to its target count.
            if (_tick % ForageTicks == 0) q.Forage = TopUpForageLocked();

            // (1.6) day/night clock (see the Epoch doc): re-derive the shared calendar from wall-clock time
            // and, on an in-game hour rollover, flag every connected session for a fresh 0x20 broadcast.
            // Checked every tick rather than every 750th, so the broadcast lands within 600ms of the true
            // rollover instead of drifting by however far into an hour the process happened to start.
            if (SyncClock()) q.TimeChanged = true;

            // (1.7) weather: when the deterministic weather PERIOD rolls over (WeatherModel.PeriodHours, ~15
            // real min), recompute each active map's weather and broadcast to any whose sky actually changed.
            // A season change lands on a period boundary too, so this pass catches those as well. Cheap: the
            // period only advances a couple of times an hour. Overrides are broadcast eagerly elsewhere.
            long period = WeatherModel.PeriodNow();
            if (period != _lastWeatherPeriod)
            {
                _lastWeatherPeriod = period;
                q.WeatherChanges = new List<(ushort, byte)>();
                foreach (var (mapId, pm) in _maps)
                {
                    if (pm.Players.Count == 0) continue;
                    byte w = WeatherForLocked(mapId);
                    if (w == pm.Weather) continue;
                    pm.Weather = w;
                    q.WeatherChanges.Add((mapId, w));
                }
            }

            // (2) wander: each mob acts only when its own MoveTime has elapsed (RTK MobMoveTime), and even
            // then usually just turns instead of stepping — mirroring RTK mob_ai_normal (checkmove: pick a
            // random side, only step when it matches the current facing, else 4-in-11 step straight ahead).
            // This paces a rabbit (3000ms) to a hop every few seconds, not every 600ms heartbeat.
            //
            // Two exception boundaries, one per mob and one per map, so a creature that throws costs ITSELF the
            // beat and nothing else. Before them the only catch was TickLoop's, which abandoned the whole tick:
            // every list queued above and below was dropped unsent while the mobs already stepped stayed
            // stepped. A skipped mob is left exactly where the throw found it: nothing here unwinds. Its
            // tile-set entry, its timers and any queue entry it added before throwing all stand, and a throw
            // part-way through a step can leave work half done in ways a completed step never does — a
            // creature standing on a trap it stepped onto but never sprang (the move is queued before the
            // trigger runs), a queued swing whose cooldown was never advanced. That is the same torn state the
            // old shape left, when the whole beat was lost on top of it; it is not new, and it is not repaired.
            // Plain try/catch rather than Try(): this is the hot loop, and a closure per mob per beat is an
            // allocation the sweep does not need.
            foreach (var (mapId, m) in _maps)
            {
                if (m.Mobs.Count == 0 || m.Players.Count == 0) continue;   // no observers -> don't bother
                try
                {
                    // The map's collision index and this tick's queues, packaged for MobAiTick.Step (World.MobAiTick.cs).
                    var ctx = new MobTickContext(this, mapId, m, q);

                    foreach (var mob in m.Mobs)
                    {
                        if (!mob.Alive) continue;
                        try { MobAiTick.Step(ctx, mob); }
                        catch (Exception e)
                        {
                            Log.Error($"mob AI step threw — {mob.Key}#{mob.Id} on map {mapId} is skipped this beat, the rest of the sweep continues", e);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"mob AI sweep threw — map {mapId} is skipped this beat, the other maps continue", e);
                }
            }
        }

        // (3)-(6): everything queued above, sent now the lock is released.
        FlushTick(q);
    }

    /// <summary>
    /// The back half of one heartbeat: everything <see cref="Tick"/> queued under <c>_lock</c>, sent now that
    /// it is released. Phases (3) to (6), in the order they are written — viewports first (so a mob that left
    /// the screen is despawned rather than sent an off-screen move), then moves and turns, then the deferred
    /// visuals, then this beat's swings, then regen, then the clock and the weather.
    ///
    /// <para>Three of those boundaries are BEHAVIOUR rather than layout, and
    /// <c>Tests/MobAiTickTests.cs</c> pins exactly those three on the wire: viewports before moves, moves
    /// before turns, turns before swings. The rest of the (3)-(6) sequence — the deferred visuals, the Lua
    /// hooks, regen, the clock, the weather — is NOT pinned by a test; it is preserved because the body was
    /// moved here verbatim from <c>Tick</c> (135 lines, 0 differing), and reordering any of it would be a
    /// change no test would catch. Treat it as load-bearing until someone proves otherwise.</para>
    ///
    /// <para>Runs OUTSIDE <c>_lock</c> and must keep doing so — it sends. It takes the lock only where the
    /// body always did (the <c>_deferredFx</c> / <c>_hooks</c> / <c>_deferredTrapClears</c> drain and the
    /// player snapshot), and <see cref="ReconcileViews"/>, its first statement, takes its own.</para>
    /// </summary>
    private void FlushTick(TickQueues q)
    {
        // (3) Reconcile viewports FIRST, using the mobs' NEW positions: spawn any that stepped into view,
        // despawn any that stepped out. Doing this before the moves means a mob that just left the screen is
        // despawned (0x0E) rather than sent an off-screen 0x0C the client would cull — the desync that made
        // mobs vanish for good.
        ReconcileViews();

        // (4) Now stream moves/turns, but only to players who still have that mob in view (MoveMob/SideMob
        // are no-ops otherwise) — bounding on-wire traffic to on-screen mobs even on a 400-spawn map.
        foreach (var mv in q.Moves)
            Broadcast(mv.map, p => p.MoveMob(mv.id, mv.x, mv.y, mv.dir));
        foreach (var tn in q.Turns)
            Broadcast(tn.map, p => p.SideMob(tn.id, tn.dir));

        // Repeating status effects queued above (venom's per-tick zap, doze/sleep's drowse) — the same 0x29 +
        // 0x19 pair a cast plays, re-sent over the afflicted creature for as long as the status holds.
        // Bars for health that changed without a hit (the self-heal). Same 0x13 the damage path uses, so the
        // bar animates up exactly the way it animates down.
        foreach (var hs in q.HealthShows)
        {
            byte pct = MobHpPercent(hs.mob);
            BroadcastWideArea(hs.map, hs.mob.X, hs.mob.Y, p => p.DamageOver(hs.mob.Id, pct, 0));
        }

        foreach (var fr in q.FxRepeats)
        {
            BroadcastWideArea(fr.map, fr.x, fr.y, p => p.EffectOver(fr.id, fr.anim));
            if (fr.sound > 0) BroadcastSameArea(fr.map, fr.x, fr.y, p => p.SoundAt(fr.sound, fr.id));
        }

        // …and anything raised from inside TryDamage, which has no way to send where it stands.
        List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> deferred;
        List<(string key, string hook, ushort map, Mob mob, Session? actor)> hooks;
        List<(ushort map, uint trapId)> trapClears;
        lock (_lock)
        {
            deferred = new List<(ushort, uint, ushort, ushort, int, int)>(_deferredFx); _deferredFx.Clear();
            hooks = new List<(string, string, ushort, Mob, Session?)>(_hooks); _hooks.Clear();
            trapClears = new List<(ushort, uint)>(_deferredTrapClears); _deferredTrapClears.Clear();
        }
        // Traps that went off this tick: rub out their revealed marker on everyone who had spotted them.
        foreach (var tc in trapClears) Broadcast(tc.map, p => p.ClearTrapMarker(tc.trapId));
        foreach (var fx in deferred)
        {
            // The tile was captured when the effect was queued, not looked up now — the mob that raised it may
            // already be dead and off the map by the time this flushes, and a death effect still has to be seen
            // and heard from where the body fell.
            if (fx.anim  > 0) BroadcastWideArea(fx.map, fx.x, fx.y, p => p.EffectOver(fx.id, fx.anim));
            if (fx.sound > 0) BroadcastSameArea(fx.map, fx.x, fx.y, p => p.SoundAt(fx.sound, fx.id));
        }

        // Lua AI hooks, run here and only here — outside the lock (see _hooks).
        foreach (var h in hooks)
            Try(() => MobScript.Fire(h.key, h.hook, new MobContext(this, h.map, h.mob, h.actor)), $"mob hook {h.key}.{h.hook}");

        // The PLAYER half of the same thing: a dozed player's drowse redraws and their hold lapses. Kept out
        // here with the other broadcasts rather than in the mob loop — it is per-session, not per-mob, and it
        // sends. Only sleepers do any work; TickSleep returns immediately for everyone else.
        foreach (var s in AllPlayers()) { Try(s.TickSleep, "TickSleep"); Try(s.TickPoison, "TickPoison"); }

        // Wisdom / "Listen to advice" (0x1b sub-4): a gameplay hint into the chat channel every ~15 minutes for
        // players who left the option on. RTK runs this per-player from login; we fire it server-wide on the
        // same cadence as the weather roll. SendAdvice is a no-op for anyone with the option off.
        if (_tick % AdviceTicks == 0)
            foreach (var s in AllPlayers()) Try(s.SendAdvice, "SendAdvice");

        // Newly-foraged ground items (chestnuts &c.): draw them for everyone on that map (0x16).
        if (q.Forage is not null)
            foreach (var (map, gi) in q.Forage)
                Broadcast(map, p => p.ShowGroundItem(gi));

        // (4.5) Resolve this tick's mob swings (queued above while still under the lock) — applying player
        // damage runs Session-side (HUD update + broadcast + possible death), so it happens out here like
        // every other socket-touching step.
        foreach (var h in q.Hits)
        {
            // On the SWING itself, hit or miss — this is the point where the mob commits to the attack. The
            // 0x1A action makes the mob visibly swing (matching the player's own swing anim in HandleAttack) and
            // 009.wav is the swing sfx; the landed-hit sound (001.wav) is layered on separately by ApplyMobHit.
            BroadcastSameArea(h.map, h.mob.X, h.mob.Y, p => p.ActionOver(h.mob.Id, Session.MobSwingActionType, Session.MobSwingActionTime, 0));
            BroadcastSameArea(h.map, h.mob.X, h.mob.Y, p => p.SoundAt(Session.MobSwingSfx, h.mob.Id));
            int dmg = MobSwingDamage(h.mob.MinDam, h.mob.MaxDam);
            Try(() => h.target.ApplyMobHit(h.mob, dmg), $"ApplyMobHit {h.mob.Name} -> {h.target.Remote}");
        }

        // Creature spells + idle flavour queued above — both broadcast, and a spell can kill, so neither can
        // run under the lock.
        foreach (var c in q.MobCasts) Try(() => c.target.ApplyMobSpell(c.mob, c.spell), $"ApplyMobSpell {c.mob.Name} -> {c.target.Remote}");
        foreach (var ch in q.Chatter)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(
                ch.channel == 0 ? $"{ch.mob.Name}: {ch.line}" : ch.line);   // RTK talk(0) attributes, talk(2) doesn't
            // Proximity-gated around the creature, matching RTK bll_talk's map_foreachinarea(..., AREA, ...).
            BroadcastArea(ch.map, ch.mob.X, ch.mob.Y, Session.SayHalfW, Session.SayHalfH,
                p => p.SpeakEntity(ch.channel, ch.mob.Id, bytes));
        }

        // Pet swings queued above: same damage roll as any other mob swing, but landing on a mob.
        foreach (var ph in q.MobHits)
            Try(() => ApplyMobOnMobHit(ph.map, ph.attacker, ph.victim, MobSwingDamage(ph.attacker.MinDam, ph.attacker.MaxDam)), "ApplyMobOnMobHit");

        // Trap hits + poison ticks queued above (same reasoning as the mob-swing pass: Session-facing
        // broadcasts/exp can't run under the lock).
        foreach (var td in q.TrapDamage)
            Try(() => ApplyTrapDamage(td.map, td.mob, td.dmg, td.ownerId), "ApplyTrapDamage (tick)");

        // Expired pets queued above — plain despawn, no kill/loot.
        foreach (var ep in q.ExpiredPets)
            Try(() => DespawnMob(ep.map, ep.mob), "DespawnMob (expired pet)");

        // Expired morphs queued above — revert the peer-visible disguise back to our real human look.
        foreach (var mp in q.ExpiredMorphs)
            Try(() => mp.RevertMorph(), "RevertMorph");

        // Expired stealth queued above — restore the normal look once the invisible-spell timer lapses w/o a hit.
        foreach (var sp in q.ExpiredStealth)
            Try(() => sp.RevertStealth(), "RevertStealth");

        // (5) natural HP/MP regen for EVERY connected player (not gated on mobs/viewport, unlike the
        // steps above). Each session tracks its own 25s accumulator and only emits a status packet on a
        // real change — see Session.RegenTick. Snapshot the player list under the lock, tick outside it.
        Session[] players2;
        lock (_lock) players2 = _maps.Values.SelectMany(m => m.Players).ToArray();
        foreach (var p in players2) Try(() => p.RegenTick(TickMs), "RegenTick");

        // (6) day/night + weather broadcasts queued above — every connected session hears the new hour
        // (RTK broadcasts clif_sendtime server-wide, not per-map), each affected map hears its own weather.
        if (q.TimeChanged)
        {
            var (h, y) = Time;
            foreach (var p in players2) Try(() => p.SendTime(h, y), "SendTime");
            // Nothing to persist: the calendar is derived from the epoch, so a restart resumes it exactly.
        }
        if (q.WeatherChanges is not null)
            foreach (var (map, w) in q.WeatherChanges)
                Broadcast(map, p => p.SendWeather(w));
    }

    // Snapshot each populated map's (players, mobs) under the lock, then reconcile every player's viewport
    // outside it. Cheap: a few hundred in-view checks per player per tick, no allocation on the hot path
    // beyond the snapshot arrays.
    private void ReconcileViews()
    {
        // Floor items ride along with the mobs: a forage top-up or another player's drop lands on the map
        // while we're standing still, and its own broadcast is viewport-gated like every other 0x07 — so the
        // tick is what eventually draws it for whoever is close enough. Hence `|| m.Items.Count > 0`: a map
        // with items but no mobs still needs reconciling. And `|| m.Players.Count > 1`: a peer walking toward
        // us is viewport-gated the same way (0x33), so a mob-less, item-less map with two players still needs
        // the tick to draw each into the other's view as they close the distance.
        // The coordinates come out WITH the player list, in the same acquisition. They used to be read back
        // off each Session inside ReconcilePeer, out here with no lock held — two ushort reads of a character
        // whose owner writes both under _lock, so the gate could test one tile's X against the previous
        // tile's Y (and its two InView calls could each see a different pair). See PeerTile.
        (PeerTile[] players, Mob[] mobs, GroundItem[] items)[] snapshot;
        lock (_lock)
        {
            snapshot = _maps.Values
                .Where(m => m.Players.Count > 0 && (m.Mobs.Count > 0 || m.Items.Count > 0 || m.Players.Count > 1))
                .Select(m => (m.Players.Select(p => new PeerTile(p, p.PlayerX, p.PlayerY)).ToArray(),
                              m.Mobs.ToArray(), m.Items.ToArray()))
                .ToArray();
        }
        foreach (var (players, mobs, items) in snapshot)
            foreach (var p in players) Try(() => { p.Session.SyncPeers(players); p.Session.SyncMobs(mobs); p.Session.SyncGroundItems(items); }, "ReconcileViews");
    }

    /// <summary>Run one per-player / per-mob step in isolation: a throw in one player's RegenTick, one
    /// mob's AI hook or one peer's broadcast delivery must not abort the rest of the sweep — and on the tick
    /// thread it must not reach <see cref="TickLoop"/>'s outer catch, where it would cost EVERY later step
    /// of this beat.
    ///
    /// <para>This used to be <c>catch { }</c> on the theory that the only thing that could throw here was a
    /// send to a dead socket. That has not been true since the outbound channel: <c>Session.Send</c> is a
    /// non-blocking <c>TryWrite</c> that closes the session on a full queue and never throws. So anything
    /// caught here is a real bug in the wrapped code — and nineteen sites, including <c>ApplyMobHit</c>,
    /// <c>ApplyMobSpell</c> and every Lua AI hook, were swallowing those with no trace at all.
    /// <paramref name="what"/> names the site so the log says which of the nineteen it was.</para></summary>
    private static void Try(Action a, string what)
    {
        try { a(); }
        catch (Exception e) { Log.Error($"isolated step '{what}' threw — skipped, the sweep continues", e); }
    }

    /// <summary>A mob's raw melee swing (RTK <c>swingDamage.lua</c> <c>_getMobSwingDamage</c>): three
    /// independent uniform draws over the range split into thirds, summed and floored, +1. This is NOT a
    /// flat roll across [MinDam,MaxDam] — three thirded draws concentrate the result near the midpoint
    /// (Irwin-Hall-ish), matching RTK's actual distribution. The target's armor is applied separately, by
    /// the target itself (<see cref="Session.ApplyMobHit"/>), since AC/gear/buffs are session-local state.</summary>
    private static int MobSwingDamage(int minDam, int maxDam)
    {
        double lo = minDam / 3.0, hi = maxDam / 3.0;
        double sum = 0;
        for (int i = 0; i < 3; i++) sum += lo + Random.Shared.NextDouble() * (hi - lo);
        return 1 + (int)Math.Floor(sum);
    }
}
