using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ---- mob / combat lab ----
    // The 4.95 creature GRAPHIC id-space is unknown, so we discover it live (look-lab style) via 0x16.
    //   "@mob <hi> <lo> [hp]"   spawn ONE creature on the tile in front of you (gfx = hi*256+lo) so you
    //                           can see it and immediately whack it.
    //   "@mobrow <lo> <hi> [step]"  spawn a W->E row sweeping graphic id lo..hi (step defaults to 1).
    //                           The gfx id is a FRAME index into the monster archive (client adds
    //                           0x4000, category "I"), and Monster.tbl's "Starting" column lists each
    //                           monster's idle frame — the first ~19 monsters start at 0,20,40,...,360.
    //                           So "@mobrow 0 360 20" shows one idle sprite per monster 0..18.
    //   "@spawn [hi] [lo]"      drop a little pack of critters around you at one graphic id.
    //   "@kill"                 despawn every mob.


    // "@cre <lookId> [hp] [color]": spawn ONE real monster (Monster.epf, via 0x07) on the tile in front
    // of you, so you can see it AND immediately melee it (combat is unchanged — it hits any Mob on the
    // tile). [color] is the 0x07 color byte we're trying to identify as a recolor/palette selector.
    private void CreatureOne(CommandArgs a)
    {
        int look = a.Int(0, 0);
        int hp = a.Int(1, 6);
        int color = a.Int(2, 0);
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMonster((ushort)look, x, y, $"c{look}", hp, dir: (byte)((_facing + 2) & 3), color: (byte)color);
    }

    // ===== MVP: spawn a rabbit, watch it wander, kill it =====================================
    // The whole lifecycle end-to-end, kept deliberately hardcoded (one rabbit, look 21, 6 HP, random
    // wander near its spawn) before generalizing into a real mob/AI/spawn system. It mirrors how the RTK
    // map-server drives a mob: the server owns the entity + HP, ticks its AI on a timer, streams walk
    // steps (0x0C) to the client, and despawns it (0x0E) on death. Combat is the EXISTING melee path —
    // face the rabbit and press space; HandleAttack finds it on the front tile and deals damage.
    private const ushort RabbitLook = 21;   // Monster.tbl look id — validated shape-match: rabbit = 21

    // "@rabbit": drop a single wandering rabbit into the SHARED world on the tile in front of you.
    // Everyone on the map sees it, everyone fights the SAME one, and World.Tick drives its wander — no
    // per-session task anymore (that only moved the rabbit on the spawner's screen).
    private void SpawnRabbit()
    {
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        byte dir = (byte)((_facing + 2) & 3);   // face the player on arrival
        // Real registry entry (mobs.csv id 1, key "rabbit"): look 21 color 3, 10hp, 5xp. This used to
        // hardcode color 0, which for look 21 renders like the "Hare" family (id 116+, same look, color
        // 37+) instead of the actual Rabbit — reported live 2026-07-26.
        var def = Content.FindMob("rabbit");
        if (def is not null) SummonWorldMob(def.Look, x, y, def.Name, hp: def.Hp, dir: dir, color: def.Color, exp: def.Exp, moveTime: def.MoveTime, key: def.Key, def: def);
        else SummonWorldMob(RabbitLook, x, y, "Rabbit", hp: 6, dir: dir, color: 0, exp: 5, moveTime: 3000);   // registry missing -> old fallback
        Reply("A rabbit appears. Face it and press space to attack.");
    }

    // Register a mob in the SHARED world (drawn via 0x07 = Monster.epf) and broadcast the spawn to every
    // player on the map. World.Tick then wanders it (leashed to its spawn tile); combat resolves against
    // the world's authoritative HP in HandleAttack. This is the gameplay-mob path (@rabbit / @summon);
    // the debug lab (@cre/@mob/@crow/look-lab) still uses the session-local SpawnMonster/SpawnMob.
    // `def`, when given, is the real registry entry — its full combat stat block (MinDam/MaxDam/Ac/Grace/
    // Hit/IsBoss/Protection/Will/Aggressive) rides along, exactly like World.Materialize's real spawns.
    // Without it (the `@rabbit` no-registry fallback), a summon defaults to a harmless vanilla mob (1-1
    // damage, 0 AC) rather than silently under-tuned — previously EVERY debug/GM summon (@rabbit/@summon/
    // the ridden-horse re-spawn) dropped these fields entirely, so testing a fix like this one via @summon
    // would never have shown the real numbers.
    /// <param name="wander">Leave true for a gameplay summon. false pins it to its tile — what a GM test
    /// spawn (<c>@mob</c>) wants, since a dummy that strolls off mid-measurement is useless for the melee /
    /// sound / sprite calibration those commands exist for.</param>
    private Mob SummonWorldMob(ushort look, ushort x, ushort y, string name, int hp, byte dir, byte color,
                               int exp = 0, int moveTime = 2500, string key = "", MobDef? def = null,
                               bool wander = true)
    {
        var mob = new Mob(_world.AllocateMobId(), look, x, y, name, hp)
        {
            Key = key,   // MobDef identifier (for quest kill-matching); empty for keyless debug summons
            Dir = dir, Color = color, Exp = exp, HomeX = x, HomeY = y, Wander = wander,
            MoveTime = moveTime, MoveTimer = Random.Shared.Next(moveTime),
            Level = def?.Level ?? 0, Will = def?.Will ?? 0, Aggressive = def?.Aggressive ?? false, Flees = def?.Flees ?? false,
            MinDam = def?.MinDam ?? 1, MaxDam = def?.MaxDam ?? 1, Hit = def?.Hit ?? 0,
            IsBoss = def?.IsBoss ?? false, Protection = def?.Protection ?? 0, Ac = def?.Ac ?? 0, Grace = def?.Grace ?? 0,
        };
        _world.AddMob(_char.Map, mob);   // broadcasts the 0x07 spawn to every player on the map (incl. us)
        Log.Info($"   -> world spawn mob {mob.Id} '{name}' look={look} c{color} @({x},{y}) hp={hp} dmg={mob.MinDam}-{mob.MaxDam} on map {_char.Map}");
        return mob;
    }

    // ===== navigation: warp + map/mob listing + data-driven summon ==========================

    // ---- Mythic Nexus zodiac cave entrances ----
    // RTK gates each of the 12 zodiac caves behind a level/vitals check (Scripts/mythicCaveReqCheck.lua) and
    // an easy/dangerous/deadly tier picker (NPCs/mythic/mythic_cave_selector.lua). With the picker menu off
    // (the default — it's GM/Config-only), RTK auto-warps to the DEEPEST tier the player qualifies for, so we
    // reproduce that: tier 1 -> base map, tier 2 -> base+3000, tier 3 -> base+4000.
    //
    // The entrance tiles, destinations, and per-tier requirements are DATA-DRIVEN from
    // game-data/MythicCaves.csv (Content.MythicCaves / Content.MythicCaveTiles), editable + hot-reloadable
    // via @reload. The requirement numbers are archival — cross-referenced against 4 tutor posts (see the CSV
    // Sources column + Sources.csv tutor-caves-*); the tile/destination geometry is RTK routing.

    // Plural form for the mythic-cave denial line ("Mythic Oxen dwell here"). Every zodiac animal takes a
    // plain "s" except Ox, whose plural is irregular.
    private static string PluralAnimal(string animal) => animal == "Ox" ? "oxen" : animal.ToLowerInvariant() + "s";

    // Deepest tier (1..3) the player unlocks for this cave, or a negative "how close" code when locked out:
    // 0 = within 3 levels, -1 = within 4-7, -2 = 8+ levels short. Mirrors mythicCaveReqCheck.lua exactly.
    private int MythicCaveTier(Content.MythicCaveDef cave)
    {
        for (int i = 2; i >= 0; i--)   // check tier 3 -> 1, return the first satisfied
        {
            var r = cave.Tiers[i];
            if (_char.Level >= r.Level && (_char.MaxHp >= r.Vita || _char.MaxMp >= r.Mana))
                return i + 1;
        }
        int levelsUntil = cave.Tiers[0].Level - _char.Level;
        if (levelsUntil >= 8) return -2;
        if (levelsUntil >= 4) return -1;
        return 0;
    }

    // Handle a step onto a zodiac entrance tile: warp into the deepest unlocked cave tier, or refuse (snap back
    // + flavour line) when under-levelled. Returns false if (current map, x, y) isn't a configured entrance.
    private bool TryMythicCaveEntrance(ushort x, ushort y)
    {
        if (!Content.MythicCaveTiles.TryGetValue((_char.Map, x, y), out var cave)) return false;
        int tier = MythicCaveTier(cave);
        if (tier < 1)
        {
            string denyMsg = tier switch   // status box (RTK clif_sendminitext), not the login message box
            {
                -2 => $"That would be unwise. Mythic {PluralAnimal(cave.Animal)} dwell here.",
                0  => "You almost understand the secrets of this entrance.",
                _  => "You are not yet ready to enter here.",
            };
            // @anywarp: unqualified for EVERY tier, so the waiver carries them into tier 1 (the base cave) —
            // the deepest-unlocked rule has nothing to pick from, and the shallowest is the predictable choice.
            if (_waiveWarpGate)
            {
                tier = 1;
                SendMiniText($"[anywarp] mythic gate waived — would have said: {denyMsg}");
                Log.Info($"   -> MYTHIC {cave.Animal} entrance WAIVED (@anywarp, level {_char.Level}) -> tier 1");
            }
            else
            {
                SendXy();   // cancel the client's step prediction / unblock the next step — the entrance holds them out
                SendMiniText(denyMsg);
                Log.Info($"   -> MYTHIC {cave.Animal} entrance REFUSED (tier {tier}, level {_char.Level})");
                return true;
            }
        }

        ushort destMap = (ushort)(cave.DestMap + (tier == 3 ? 4000 : tier == 2 ? 3000 : 0));
        if (!Content.TryMap(destMap, out var dm)) { destMap = cave.DestMap; Content.TryMap(destMap, out dm); }
        if (dm is null) { SendXy(); return true; }   // map data missing — don't strand the player
        Log.Info($"   -> MYTHIC {cave.Animal} cave {tier} -> map {destMap} '{dm.Name}' ({cave.DestX},{cave.DestY}) [level {_char.Level}]");
        EnterMap(dm.Id, dm.Xs, dm.Ys, cave.DestX, cave.DestY, dm.Name);
        return true;
    }

    // ---- Tiered "event cave" entrances ----
    // A doorway into a dungeon that exists as FIVE parallel copies, one per depth, where which copy you get
    // is read off the character. RTK does this with one shared helper called from each entrance
    // (Player.getEventCaveLevel + Player.eventCaveLevelPrompt, rtklua/Accepted/player.lua); ours is the same
    // shape, with the ladder and the entrances both in flat files (game-data/EventCaveTiers.csv,
    // game-data/EventCaves.csv -> Content.EventCaveBands / Content.EventCaveTiles), hot-reloadable via @reload.
    //
    // The Buya Library Caverns doorway (map 486, tiles 13:0 and 14:0) is the entrance this was built for. It
    // used to be two ordinary Warps.csv rows straight into tier 1, which meant the four deeper tiers were
    // reachable only by @warp — the caverns shipped as one cave wearing five hats.
    //
    // Two things make it a scripted tile rather than a warp: the destination is conditional, and the entry is
    // a DIALOG. The player walks up, reads three pages about the dark and the stench, and only then is either
    // let down (straight in, or via the two-tunnel menu when they sit in a split band) or turned around. A
    // character below the ladder's floor still gets the whole speech first and finds themselves back outside
    // when it ends — the doorway is not a wall, it is a door that doesn't take you.

    private static readonly Mob EventCaveVirtualNpc = new(0xFFFFFFFA, 0, 0, 0, "EventCave", 1);

    // Handle a step onto an event-cave entrance tile. Returns false if (current map, x, y) isn't one — every
    // other step pays only a single hash probe. Returns true the moment it takes the step over, BEFORE the
    // dialog has been answered: the step is cancelled either way, so there is nothing left for the walk
    // handler to do and nothing to wait for.
    private bool TryEventCaveEntrance(ushort x, ushort y)
    {
        if (!Content.EventCaveTiles.TryGetValue((_char.Map, x, y), out var cave)) return false;
        // Already sitting in a modal box: AwaitReply would overwrite that conversation's completion source
        // and orphan it. Hold them at the from-tile and let the next step try again.
        if (DialogBusy) { SendXy(); return true; }

        // Cancel the client's step prediction up front, so the player is standing still for the whole
        // conversation and both outcomes start from the same place — the refusal needs it, and the warp
        // doesn't care (EnterMap redraws everything anyway).
        SendXy();
        _ = RunEventCaveEntryAsync(cave);
        return true;
    }

    private async Task RunEventCaveEntryAsync(Content.EventCaveDef cave)
    {
        ushort startMap = _char.Map;

        // The entry pages: portrait-less boxes on the player's own entity id, exactly as DlgPush describes —
        // nobody is speaking here, the room is. Awaited one at a time so PREVIOUS/NEXT paginate for real.
        foreach (var page in cave.Pages)
        {
            SendScriptMessageP(_char.Id, page, DialogPortrait.None, prev: false, next: true);
            await AwaitReply();
            if (_char.Map != startMap) return;   // a GM warp / death / another dialog moved them mid-read
        }

        var band = Content.EventCaveBandFor(_char.Level, _char.Mark);
        int tier;
        string label;
        if (band is null)
        {
            // Below the ladder's floor. @anywarp waives that with the usual echo and takes tier 1 — no band
            // means no depth to read off the character, so the shallowest copy is the predictable choice.
            if (!_waiveWarpGate)
            {
                // RTK bumps the player two tiles clear of the doorway; we already held them at the
                // from-tile, so all that is left is the line.
                SendXy();
                if (cave.DenyMsg.Length > 0) SendMiniText(cave.DenyMsg);
                Log.Info($"   -> EVENTCAVE '{cave.Key}' REFUSED (level {_char.Level} mark {_char.Mark}) for {_char.Name}");
                return;
            }
            SendMiniText($"[anywarp] entry requirement waived — would have said: " +
                         (cave.DenyMsg.Length > 0 ? cave.DenyMsg : "(silent refusal)"));
            Log.Info($"   -> EVENTCAVE '{cave.Key}' WAIVED (@anywarp, level {_char.Level} mark {_char.Mark}) -> tier 1");
            tier = 1;
            label = "waived";
        }
        else
        {
            var b = band.Value;
            tier = b.Tier;
            label = b.Label;
            if (b.Alt > 0)
            {
                // A split band: both depths are open and the player chooses. Closing the menu (0) is a real
                // answer — they back out of the doorway and stay where they are, which is RTK's behaviour too
                // (its menuSeq result falls through both branches and no warp happens).
                int choice = await DlgMenu(EventCaveVirtualNpc, cave.Prompt, new[] { cave.OptionNear, cave.OptionFar });
                if (choice != 1 && choice != 2)
                {
                    Log.Info($"   -> EVENTCAVE '{cave.Key}' split declined by {_char.Name} ({b.Label})");
                    return;
                }
                if (_char.Map != startMap) return;   // moved on while the menu was open
                tier = choice == 1 ? b.Tier : b.Alt;
            }
        }

        ushort destMap = cave.MapForTier(tier);
        if (!Content.TryMap(destMap, out var dm) || dm is null)
        {
            SendMiniText("The way down is blocked.");   // map data missing — say so rather than strand them
            Log.Info($"   ?? EVENTCAVE '{cave.Key}' tier {tier} -> map {destMap} has no map data");
            return;
        }
        Log.Info($"   -> EVENTCAVE '{cave.Key}' tier {tier} ({label}) -> map {destMap} '{dm.Name}' " +
                 $"({cave.DestX},{cave.DestY}) [level {_char.Level} mark {_char.Mark}]");
        EnterMap(dm.Id, dm.Xs, dm.Ys, cave.DestX, cave.DestY, dm.Name);
    }

    // ---- Forever Tree entrance (RTK onScriptedTiles: the "crevasse" at Wilderness 19,91) ----
    // The Forever Tree area (map 1228) is entered by walking onto the crevasse tile at Wilderness (map 1002)
    // 19,91 — a scripted tile, not an SQL warp, because it pops a warning box first (nexusatlas
    // forevertree.php: "Walk onto (19,91) ... You will receive a popup message. Hit Ok. You will appear inside
    // the Forever Tree area ... in the little indent on the bottom right side"). NO gate: the box only WARNS
    // ("only the truly mighty could survive"), the archive has you hit Ok and enter, and its own tips ("Soloing
    // the Forever tree isn't that easy") assume under-prepared players go in and die to the ravens/tree. The
    // Bon-Hwa NPC does the real level-99 + Enchanted-rank gating (see Server/BonHwa.cs). The way back OUT is the
    // ordinary Warps.csv pair at 1228 (21,15)/(22,15)-ish -> 1002 (19,93), so the landing (21,16) sits one tile
    // south of the exit source, in the same bottom-right pocket, and is not itself a warp tile.
    private const ushort ForeverTreeMap = 1228;

    private bool TryForeverTreeEntrance(ushort x, ushort y)
    {
        if (_char.Map != 1002 || x != 19 || y != 91) return false;
        if (DialogBusy) { SendXy(); return true; }   // mid-dialog: hold at the from-tile, retry on the next step
        SendXy();                                     // cancel the client's step prediction — stand still for the box
        _ = RunForeverTreeEntryAsync();
        return true;
    }

    private async Task RunForeverTreeEntryAsync()
    {
        ushort startMap = _char.Map;
        SendScriptMessageP(_char.Id,
            "You spot a crevasse leading into a sandy area. Deathly screeches echo from within. A sense of " +
            "doom overcomes you; you realize that only the truly mighty could survive in there.",
            DialogPortrait.None, prev: false, next: true);
        await AwaitReply();
        if (_char.Map != startMap) return;   // a GM warp / death / another dialog moved them mid-read
        if (!Content.TryMap(ForeverTreeMap, out var dm) || dm is null) { SendXy(); return; }
        Log.Info($"   -> FOREVER TREE entrance -> map {ForeverTreeMap} '{dm.Name}' (21,16) for {_char.Name}");
        EnterMap(dm.Id, dm.Xs, dm.Ys, 21, 16, dm.Name);
    }

    // Class path-hall interior warps (onScriptedTilesPathHalls.lua). Each Kugnae/Buya path hall (Warrior/Rogue/
    // Mage/Poet, both cities) has two scripted-tile doorways that are NOT in the SQL warp table: the SOUTH edge
    // (x 1-2, y 23) into that class's guild hall — class-gated to members of that base class (RTK also lets a
    // Tutor in, a staff role we don't model) — and the NORTH edge (x 8-9, y 1) into the player's alignment
    // sanctum (Unaligned/Kwisin/Mingken/Ohaeng, indexed by Character.Alignment 0-3). Only the map-exit warp is
    // in Warps.csv, so before this the leader-room and hall doors did nothing (or read as solid). The hall/
    // sanctum geometry is data-driven (game-data/PathHalls.csv -> Content.PathHalls); hot-reloads via @reload.
    private bool TryPathHallWarp(ushort x, ushort y)
    {
        if (!Content.PathHalls.TryGetValue(_char.Map, out var hall)) return false;

        // South doorway -> class guild hall (members of that base class only).
        if ((x == 1 || x == 2) && y == 23)
        {
            if (CharClassId != hall.BaseClass)
            {
                // @anywarp waives the class gate with the usual echo; otherwise refuse as RTK does.
                if (_waiveWarpGate)
                {
                    SendMiniText("[anywarp] class gate waived — would have said: You are not the right class to enter here.");
                    Log.Info($"   -> PATHHALL guild door WAIVED (@anywarp, class {CharClassId} vs {hall.BaseClass})");
                }
                else
                {
                    // RTK onScriptedTilesPathHalls.lua: player:sendMinitext(str) — the status box, not chat.
                    SendMiniText("You are not the right class to enter here.");
                    SendXy();   // refuse: hold at the from-tile (RTK bumps 2 tiles north — same net effect)
                    return true;
                }
            }
            return WarpHall(hall.GuildMap, (ushort)(x + 6), 3);
        }

        // North doorway -> the player's alignment sanctum (the path-leader room).
        if ((x == 8 || x == 9) && y == 1)
        {
            byte a = _char.Alignment <= 3 ? _char.Alignment : (byte)0;
            return WarpHall(hall.Sanctum[a], (ushort)(x - 3), 18);
        }
        return false;
    }

    // PvP arena doors (onScriptedTilesArena.lua -> arenaPVPCheckAndWarp.lua). Tower Arena is a hub: five side
    // doors, each opening into one level-banded PvP arena. NONE of them are SQL warps — only the return leg is
    // — so before this every door in the room was dead. Geometry + bands are data-driven
    // (game-data/ArenaDoors.csv -> Content.ArenaDoorTiles) and hot-reload via @reload.
    //
    // RTK's own rejection is a 2-tile shove based on facing; we hold at the from-tile with SendXy() like the
    // mythic-cave and path-hall refusals, which is the same net effect on a 4.95 client (self-walk is local,
    // so the step never commits) without needing the facing. The two denial lines are RTK's verbatim, and
    // deliberately NOT the engine's map-req cascade in TryWarpGate — the arena script has its own wording.
    // The "be careful, you may be slain..." entry warning isn't sent here: every arena map is MapPvP=1, so
    // EnterMap's own PvP-crossing warning already fires (same string).
    private bool TryArenaDoor(ushort x, ushort y)
    {
        if (!Content.ArenaDoorTiles.TryGetValue((_char.Map, x, y), out var door)) return false;

        bool low  = _char.Level < door.MinLevel || (door.Unmarked && CharMark != 0);
        // RTK's arena check ORs the two vital caps (the engine's map-req check ANDs them — this is the script's,
        // so it stays OR): being over EITHER cap keeps you out of the capped band.
        bool high = (door.MaxLevel > 0 && _char.Level > door.MaxLevel)
                 || (door.MaxVita > 0 && (long)_char.MaxHp > door.MaxVita)
                 || (door.MaxMana > 0 && (long)_char.MaxMp > door.MaxMana);

        if (low || high)
        {
            string denyMsg = low ? "Nightmarish visions of your own death repel you."
                                 : "Your honor forbids you from entering.";
            if (_waiveWarpGate)
            {
                SendMiniText($"[anywarp] arena gate waived — would have said: {denyMsg}");
                Log.Info($"   -> ARENA '{door.Label}' door WAIVED (@anywarp, {(low ? "under" : "over")}-qualified: level {_char.Level}, vita {_char.MaxHp}, mana {_char.MaxMp})");
            }
            else
            {
                SendXy();   // cancel the client's step prediction — the door holds them out
                SendMiniText(denyMsg);
                Log.Info($"   -> ARENA '{door.Label}' door REFUSED ({(low ? "under" : "over")}-qualified: level {_char.Level}, vita {_char.MaxHp}, mana {_char.MaxMp})");
                return true;
            }
        }

        if (!Content.TryMap(door.DestMap, out var dm) || dm is null) { SendXy(); return true; }   // dest unrenderable -> don't strand
        ushort dx = door.DestX2 > door.DestX ? (ushort)Random.Shared.Next(door.DestX, door.DestX2 + 1) : door.DestX;
        Log.Info($"   -> ARENA '{door.Label}' -> map {door.DestMap} '{dm.Name}' ({dx},{door.DestY}) [level {_char.Level}]");
        EnterMap(dm.Id, dm.Xs, dm.Ys, dx, door.DestY, dm.Name);
        return true;
    }

    private bool WarpHall(ushort destMap, ushort dx, ushort dy)
    {
        if (!Content.TryMap(destMap, out var dm)) { SendXy(); return true; }   // dest not renderable -> don't strand
        Log.Info($"   -> PATHHALL map {_char.Map} -> {destMap} '{dm.Name}' ({dx},{dy})");
        EnterMap(dm.Id, dm.Xs, dm.Ys, dx, dy, dm.Name);
        return true;
    }

    // ---- After-step scripted tiles (fire once the step has completed, i.e. standing on the new tile) ----
    // RTK runs these from onScriptedTile on every walk. We only port the two that are self-contained AND live
    // entirely on maps the 4.95 client can render: mythic-cave fall-rooms and bush/tree foraging.
    private void OnScriptedTileStep()
    {
        TryForage();                         // adjacent apple tree / rose bush -> small chance of an item
        TryGinseng();                        // Guol Tiger Pass ginseng rocks -> young_ginseng (Chu Rua quest)
        TryFoxSpirit();                      // Worn path/trail: 1 step in 10 a fox pops up with a riddle
        if (TryLeviathanHermitDoor()) return;// the Hermit's hut door: in if you freed one, shoved back if not (warps)
        if (TrySuteCaveMouth()) return;      // Buya's north edge: coated -> into Sute's Cave, else shoved back (warps)
        if (TryGauntletEntrance()) return;   // Nagnang's west alcove: on the shield trial -> the Gauntlet, else shoved back
        if (TryGauntletAltar()) return;      // Objective: the statue of Chung Ryong pays (or refuses) the trial
        if (TryIceBeastLava()) return;       // Northeast Koguryo lava row: shoes gate + spend-on-return (warps)
        if (TryMythicFallRoom()) return;     // mythic cave trap floor -> drop to a lower sub-room (warps)
        TryWorldMapTravel();                 // town edge tile -> inter-continent travel picker
    }

    // The lava row that splits Northeast Koguryo (map 3040, tiles 29-30 x 14-16) — the Nameless Hermit on the
    // near bank, the Ice Beast on the far one (RTK onScriptedTilesQuest.lua, the "Northeast Koguryo" block).
    // Traveling shoes, the Hermit's gift for an Aged wine, are what let you cross to reach the beast:
    //   * carrying the beast's Ice heart  -> the shoes are spent making the crossing back: consumed, and you
    //     land on the south bank at 30,17 (this is checked FIRST, exactly as RTK orders it, so the trip home
    //     always takes the shoes even though you are still wearing them);
    //   * shoes but no heart              -> you cross freely (the outbound trip to the beast);
    //   * no shoes                        -> the heat throws you back to the south bank at 30,17.
    // Returns true when it moved the player, so the remaining step hooks are skipped.
    private bool TryIceBeastLava()
    {
        if (_char.Map != 3040) return false;
        if (!((_char.X == 29 || _char.X == 30) && _char.Y >= 14 && _char.Y <= 16)) return false;

        // @anywarp: the row becomes plain ground — no shove, no forced return-hop, and the shoes are NOT
        // spent — so a tester can walk the whole map. The branch that would have fired is echoed instead.
        if (_waiveWarpGate)
        {
            if (CountItem("ice_heart") > 0)
                SendMiniText("[anywarp] lava return-crossing waived — would have spent your shoes and landed you on the south bank.");
            else if (CountItem("traveling_shoes") == 0)
                SendMiniText("[anywarp] lava gate waived — would have said: You'll burn your feet if you walk there!");
            Log.Info($"   -> LAVA row WAIVED (@anywarp) for {_char.Name} at ({_char.X},{_char.Y})");
            return false;
        }

        if (CountItem("ice_heart") > 0)
        {
            TakeItem("traveling_shoes", 1);   // RTK removeItem — the return crossing spends them
            SendMiniText("Your shoes protect your feet as you cross.");
            return Warp(_char.Map, 30, 17);
        }
        if (CountItem("traveling_shoes") > 0)
        {
            SendMiniText("Your shoes protect your feet as you cross.");
            return false;                     // let them walk the lava to reach (or lure) the beast
        }
        SendMiniText("You'll burn your feet if you walk there!");
        return Warp(_char.Map, 30, 17);       // no shoes -> thrown back onto the south bank
    }

    // ---- Fox spirits (onScriptedTilesQuest.lua "Worn path"/"Worn trail"; see Server/FoxSpirit.cs) -
    // One step in ten on either map conjures a fox with a riddle. Answer it and you keep a fox charm;
    // get it wrong and it throws you back out to Nagnang, which costs another pelt at the Border patrol
    // to undo. Nothing about it is a warp on the spot, so this returns void and the remaining step hooks
    // still run — the fox is an interjection, not a gate.
    //
    // Fire-and-forget: the encounter awaits the player's answer, and a step handler cannot block on that.
    // Guarded on DialogBusy because opening a box while one is already pending orphans the conversation
    // waiting on it (AwaitReply overwrites the completion source) — a fox that interrupts a shopkeeper
    // would hang the shop. Dead players are skipped: a ghost walking home should not be quizzed.
    private void TryFoxSpirit()
    {
        if (!FoxSpirit.IsFoxCountry(_char.Map)) return;
        if (IsDead || DialogBusy) return;
        if (QuestRandom(FoxSpirit.OddsOneIn) != 1) return;
        _ = RunFoxSpiritAsync();
    }

    private async Task RunFoxSpiritAsync()
    {
        try
        {
            Notify(FoxSpirit.Finds);

            // Already bested one: he pays the compliment and goes. RTK returns before the riddle, so a
            // second charm is impossible and a wrong answer can never cost you the one you have.
            if (CountItem(FoxSpirit.Charm) > 0)
            {
                await DlgPush(FoxSpirit.Look, FoxSpirit.Color, new[] { FoxSpirit.AlreadyCharmed });
                return;
            }

            var (question, answer) = FoxSpirit.Riddles[QuestRandom(FoxSpirit.Riddles.Length) - 1];
            var given = await DlgInputPush(FoxSpirit.Look, FoxSpirit.Color, question);
            if (given is null) return;   // closed the box — the fox neither rewards nor punishes a non-answer

            if (given.Trim().ToLowerInvariant() == answer)
            {
                GiveRewardItem(FoxSpirit.Charm, 1);
                Notify(FoxSpirit.Success);
                return;
            }
            Warp(FoxSpirit.FailMap, FoxSpirit.FailX, FoxSpirit.FailY);
        }
        catch (Exception e) { Log.Error($"fox spirit encounter threw for '{_char.Name}' — abandoned, no reward and no penalty", e); }
    }

    // ---- Leviathan quest: freeing a captive (see Server/LeviathanQuest.cs) -----------------------

    /// <summary>Dropping the talisman in front of a cage frees the captive inside. This is the ONLY way to do
    /// it, and it is a DROP, not a step: "Walk up to one of the cages and drop your talisman on the ground.
    /// The leviathan inside will vanish, along with the talisman."
    ///
    /// <para>There used to be a step trigger here, ported from RTK's <c>onScriptedTilesQuest.lua</c>, which
    /// fired on standing on the cage-door row with the captive directly above. It could never run: the 4.95
    /// map has shut cell doors (object 600, SObj 0x01) along that row, so it is unreachable from the only
    /// side you can approach from. RTK gets away with it because RTK's own copy of the map has those doors
    /// open — it edited the terrain to suit its script. The client's map is the shipped original, and it
    /// agrees with the player-facing instructions: you stand outside a shut cage and the spell breaks through
    /// the bars. See <see cref="LeviathanQuest.PenMap"/>.</para>
    ///
    /// <para>Range is <see cref="LeviathanQuest.DropRange"/> (Chebyshev), which is exactly the gap the shut
    /// door forces between you and the captive. Loose enough to allow standing a little off to the side,
    /// and it cannot misfire on anything else — a captive only ever exists in this pen.</para>
    ///
    /// <para><b>On the pen map every refusal SPEAKS, and keeps the talisman.</b> This rite was silent on
    /// every failing branch and it cost two rounds of "I dropped it and nothing happened" with no way to tell
    /// which gate was closed. Anywhere else a talisman drop is just a drop and stays quiet; standing in the
    /// pen is unambiguously an attempt, so it always gets an answer, and returning true means
    /// <see cref="HandleDropItem"/> stops before the item hits the floor — a refused rite must not dump a
    /// one-shot quest item on the ground.</para>
    ///
    /// <para>Returns true if the drop was consumed (performed OR refused with a reason), so
    /// <see cref="HandleDropItem"/> stops — the same contract as <see cref="TryHarvest"/> and
    /// <see cref="TryStarBlessing"/>.</para></summary>
    private bool TryLeviathanTalismanDrop(ItemDef def)
    {
        if (def.Key != LeviathanQuest.Talisman) return false;
        if (_char.Map != LeviathanQuest.PenMap) return false;   // elsewhere the NoDrop flag refuses the drop — the pen is the ONLY place it leaves the bag

        // Nearest captive ANYWHERE on the pen, not just in range: the distance is what turns a failed
        // attempt into an explanation instead of silence. PenSearch spans the map from any corner.
        var captive = _world.NearestMobByKey(_char.Map, _char.X, _char.Y, PenSearch, LeviathanQuest.CaptiveMob);
        int dist = captive is null ? -1 : Math.Max(Math.Abs(captive.X - _char.X), Math.Abs(captive.Y - _char.Y));
        Log.Info($"   -> LEVIATHAN talisman dropped by {_char.Name} at ({_char.X},{_char.Y}) map {_char.Map}: " +
                 $"captive={(captive is null ? "NONE ON MAP" : $"({captive.X},{captive.Y}) dist {dist}")}, " +
                 $"freedLegend={HasLegend(LeviathanQuest.LegendFreed)}, enemyLegend={HasLegend(LeviathanQuest.LegendEnemy)}, " +
                 $"stage={QuestStage(LeviathanQuest.Key)}");

        if (HasLegend(LeviathanQuest.LegendEnemy))
        { Notify("The talisman lies cold in your hand. The Leviathans have not forgiven you."); return true; }
        if (HasLegend(LeviathanQuest.LegendFreed))
        { Notify("You have already freed one of their kind."); return true; }
        if (captive is null)
        { Notify("There is no captive here to free."); return true; }
        if (dist > LeviathanQuest.DropRange)
        { Notify("You must stand closer to one of the cages."); return true; }

        if (!TakeItem(LeviathanQuest.Talisman, 1)) return false;
        FreeLeviathanCaptive(captive);
        return true;
    }

    /// <summary>How far <see cref="TryLeviathanTalismanDrop"/> looks for a captive when deciding WHAT to say.
    /// Wider than the pen (24x24) so it finds one from any corner; the actual rite still needs
    /// <see cref="LeviathanQuest.DropRange"/>.</summary>
    private const int PenSearch = 32;

    /// <summary>Break the spell on a penned captive and send it home — shared by the talisman DROP
    /// (<see cref="TryLeviathanTalismanDrop"/>) and the hand gesture (Session.TryQuestHandToMob). The
    /// captive is DESPAWNED rather than killed: no exp, no loot, and its spawn point refills normally, so the
    /// next player finds a captive to free. (RTK removes 9,999,999 health from a mob with a million HP, which
    /// is the same thing said in the engine's only vocabulary.) The talisman is consumed by the CALLER; this
    /// is only the release itself. The captive is on the caller's own map.</summary>
    internal void FreeLeviathanCaptive(Mob captive)
    {
        Notify("You cast Release leviathan.");
        NpcBubble(captive, "Thank you puny one.");   // NpcBubble prefixes the speaker's own name
        _world.DespawnMob(_char.Map, captive);
        SetQuestStage(LeviathanQuest.Key, LeviathanQuest.StageFreed);
        Log.Info($"   -> LEVIATHAN freed at ({captive.X},{captive.Y}) by {_char.Name}");
    }

    // The Hermit's hut door. Freed his kindred and it lets you in; otherwise it shoves you four tiles south
    // with a "Go AWAY!". True when it moved the player (either way), so the remaining step hooks are skipped.
    private bool TryLeviathanHermitDoor()
    {
        if (_char.Map != LeviathanQuest.DoorMap || _char.Y != LeviathanQuest.DoorY) return false;
        if (!LeviathanQuest.DoorX.Contains(_char.X)) return false;

        if (!HasLegend(LeviathanQuest.LegendFreed))
        {
            // @anywarp: waive the legend gate with the usual echo and let the door open below.
            if (_waiveWarpGate)
            {
                SendMiniText("[anywarp] quest gate waived — would have said: Go AWAY!");
                Log.Info($"   -> HERMIT door WAIVED (@anywarp) for {_char.Name}");
            }
            else
            {
                Warp(LeviathanQuest.DoorMap, (ushort)_char.X, LeviathanQuest.DoorPushToY);
                Notify("Go AWAY!");
                return true;
            }
        }
        return Warp(LeviathanQuest.HutMap, LeviathanQuest.HutX, LeviathanQuest.HutY);
    }

    // ---- Sute's cave mouth (onScriptedTilesQuest.lua; see Server/SuteQuest.cs) --------------------
    // The blue cave on Buya's north edge, and the ONLY door into maps 441-447. It is a scripted tile rather
    // than a Warps.csv row precisely because it is conditional: Eldritch's powder (an armor dye) is what gets
    // you past Sute's seal, and the crossing spends it.
    //
    //   * coated   -> the dye is stripped, the flag cleared, and you land just inside Sute's Welcome on one
    //                 of two tiles (RTK math.random(10, 11)),
    //   * uncoated -> "You are missing something." and a step back south onto the row the cave's exit warps
    //                 (Warps.csv 1425/1426) use, so bouncing off the seal leaves you where walking out would.
    //
    // Only the ENTRANCE is scripted; leaving is those two ordinary warps, which is what makes falling out
    // cost a fresh 200 gold — tswolf's "be careful not to fall out". Returns true when it moved the player.
    private bool TrySuteCaveMouth()
    {
        if (_char.Map != SuteQuest.BuyaMap || _char.Y != SuteQuest.MouthY) return false;
        if (!SuteQuest.MouthX.Contains(_char.X)) return false;

        // @anywarp: the seal becomes a plain portal — nothing checked, nothing spent, so a coated tester
        // keeps the powder — with the usual echo of what the seal would have done.
        if (_waiveWarpGate)
        {
            SendMiniText(QuestCounter(SuteQuest.DyeReg) == 1
                ? "[anywarp] Sute's seal waived — passed without spending the powder."
                : "[anywarp] Sute's seal waived — would have said: You are missing something.");
            Log.Info($"   -> SUTE cave mouth WAIVED (@anywarp) for {_char.Name} (coated={QuestCounter(SuteQuest.DyeReg) == 1})");
            return Warp(SuteQuest.WelcomeMap,
                        (ushort)(QuestRandom(2) == 1 ? SuteQuest.LandX0 : SuteQuest.LandX1), SuteQuest.LandY);
        }

        if (QuestCounter(SuteQuest.DyeReg) != 1)
        {
            Notify("You are missing something.");
            return Warp(_char.Map, (ushort)_char.X, SuteQuest.MouthPushToY);
        }

        SetArmorColor(0);                                   // the powder is spent (also clears the war-paint slot)
        SetQuestStage(SuteQuest.DyeReg, 0);
        Notify("The powder disappears as you pass the portal.");
        return Warp(SuteQuest.WelcomeMap,
                    (ushort)(QuestRandom(2) == 1 ? SuteQuest.LandX0 : SuteQuest.LandX1), SuteQuest.LandY);
    }

    // ---- The Gauntlet (onScriptedTilesQuest.lua; see Server/NagnangShieldQuest.cs) ----------------
    // Nagnang's warrior trial. Two scripted tiles: the cave mouth, and the statue at the far end.
    //
    // The MOUTH is a scripted tile rather than a Warps.csv row for the same reason Sute's is — the
    // destination is conditional. It is only a door for a Warrior who has paid Sword the green squirrel pelt
    // and has not already won the shield, and WHICH of the five parallel copies of the cave it opens onto is
    // read off the character's level. Everyone else it simply does not carry, and (as in RTK) says nothing
    // about it: the trial has no refusal line in any surviving source, and inventing one would put words in
    // an NPC's mouth. Crossing snapshots the forbidden kill counters, which is what makes the trial a
    // per-RUN test rather than a lifetime one.
    private bool TryGauntletEntrance()
    {
        if (_char.Map != NagnangShieldQuest.NagnangMap || _char.Y != NagnangShieldQuest.MouthY) return false;
        if (!NagnangShieldQuest.MouthX.Contains(_char.X)) return false;

        bool onTrial = CharBasePathId == NagnangShieldQuest.WarriorPath
                       && QuestStage(NagnangShieldQuest.StageReg) >= 1
                       && !HasLegend(NagnangShieldQuest.Legend);
        ushort dest = NagnangShieldQuest.EntranceFor(_char.Level);

        if (!onTrial || dest == 0)
        {
            // @anywarp: the mouth becomes a plain portal into the tier the level would have picked (or the
            // shallowest, below the ladder's floor), with the usual echo of what it would have done.
            if (!_waiveWarpGate)
            {
                Log.Info($"   -> GAUNTLET mouth REFUSED for {_char.Name} (path {CharBasePathId} level {_char.Level} " +
                         $"stage {QuestStage(NagnangShieldQuest.StageReg)} done={HasLegend(NagnangShieldQuest.Legend)})");
                return Warp(_char.Map, (ushort)_char.X, NagnangShieldQuest.MouthPushToY);
            }
            SendMiniText("[anywarp] Gauntlet entry requirement waived — the trial would not have let you in.");
            if (dest == 0) dest = NagnangShieldQuest.Tiers[0].Map;
        }

        SetQuestStage(NagnangShieldQuest.KillSnapshotReg, ForbiddenGauntletKills());
        Log.Info($"   -> GAUNTLET entrance -> map {dest} for {_char.Name} (level {_char.Level})");
        return Warp(dest, (ushort)(QuestRandom(2) == 1 ? NagnangShieldQuest.LandX0 : NagnangShieldQuest.LandX1),
                    NagnangShieldQuest.LandY);
    }

    /// <summary>Lifetime kills of the six creatures the trial forbids. The trial compares this against the
    /// snapshot taken at the mouth, so only what died on THIS run counts.</summary>
    private int ForbiddenGauntletKills()
    {
        int n = 0;
        foreach (var key in NagnangShieldQuest.Forbidden) n += KillCount(key);
        return n;
    }

    // The statue of Chung Ryong at the end of every tier's Objective room. Standing on the ring of tiles
    // around it IS touching it (RTK runs the same check off its own perimeter box), so there is no click:
    //   * nothing red or blue died on this run -> the shield and the legend, then the statue's speech,
    //   * something did                        -> two lines and a throw back out to the cave mouth. The
    //     stage stays at 1, so the run can be walked again — and re-entering re-snapshots, which is what
    //     "the run" means.
    // Returns true when it fired, so the remaining step hooks are skipped.
    private bool TryGauntletAltar()
    {
        if (!NagnangShieldQuest.IsObjective(_char.Map)) return false;
        if (!NagnangShieldQuest.AtAltar(_char.X, _char.Y)) return false;
        if (HasLegend(NagnangShieldQuest.Legend)) return false;   // already won — it is stone again
        if (IsDead || DialogBusy) return false;

        if (ForbiddenGauntletKills() > QuestCounter(NagnangShieldQuest.KillSnapshotReg))
        {
            foreach (var line in NagnangShieldQuest.StatueRefusal) SendMiniText(line);
            Log.Info($"   -> GAUNTLET altar REFUSED {_char.Name} — killed a forbidden creature on this run");
            return Warp(NagnangShieldQuest.NagnangMap, (ushort)NagnangShieldQuest.MouthX[0],
                        NagnangShieldQuest.MouthExitY);
        }

        // Shield first: a full pack must not consume the trial. Nothing is spent until it lands.
        if (!GiveRewardItem(NagnangShieldQuest.Shield, 1))
        {
            SendMiniText("There is no room in your pack for the shield.");
            return true;
        }
        SetQuestStage(NagnangShieldQuest.StageReg, 0);
        AddLegend($"Completed the Nagnang Warrior Trial ({Character.GameDate})", NagnangShieldQuest.Legend,
                  NagnangShieldQuest.LegendIcon, NagnangShieldQuest.LegendColor);
        Log.Info($"   -> GAUNTLET altar PAID {_char.Name} the Nagnang shield");

        // Fire-and-forget: the statue's five pages are awaited one at a time and a step handler cannot block.
        _ = RunGauntletAltarAsync();
        return true;
    }

    private async Task RunGauntletAltarAsync()
    {
        try { await DlgPush(NagnangShieldQuest.StatueLook, NagnangShieldQuest.StatueColor, NagnangShieldQuest.StatueReward); }
        catch (Exception e) { Log.Error($"Gauntlet statue speech threw for '{_char.Name}' — the shield and legend are already granted", e); }
    }

    // ---- Newbie area, quest 3: the coordinate lesson (npc_dialog.lua TutorialNpc1) ------------------
    // The Deep Forest tutor's task is "walk from here to 0021 0020", and 21,20 is a warp tile (Warps.csv
    // 4714 (21,20) -> 4715 (3,2)) — so the moment the lesson is passed is the moment the player steps onto
    // it and is carried on, not the moment his dialog closes. Paying the exp at the end of his speech (which
    // is where it used to be) rewarded clicking through pages; this rewards actually finding the tile.
    //
    // Called from the WARP branch of HandleWalk rather than OnScriptedTileStep, because a warp returns
    // before the after-step hooks ever run — the player never "stands on" 21,20.
    //
    // Once only, via its own registry flag: the stage can't be used as the marker because stage 5 is also
    // what TutorialNpc2 gates his magic lesson on, and bumping it here would skip that.
    private const string NewbCoordsLearned = "newbie_coords_learned";

    private void TryNewbieCoordinateLesson(ushort mapId, ushort x, ushort y)
    {
        if (mapId != 4714 || x != 21 || y != 20) return;
        if (QuestStage("newbie_area_quest") < 5) return;         // hasn't been set the task yet
        if (QuestCounter(NewbCoordsLearned) != 0) return;        // already paid
        SetQuestStage(NewbCoordsLearned, 1);
        AwardExp(50);                                            // NEWB_STAGE_EXP, same as every other beat
    }

    // ---- Inter-continent travel ("world map" screen) ----
    // RTK triggers this from onScriptedTile on EVERY step (onScriptedTilesMap.lua checks the current
    // map's title + x/y against hardcoded edge coordinates), then opens a destination picker via
    // clif_mapselect (sendWorldMap.lua) — a full-screen "click a location on a map graphic" UI, NOT an
    // NPC/ferry menu. The real click-a-destination flow applies NO level/quest/req gate at all: pc_warp
    // doesn't validate the (map,x,y) the client echoes back, so every listed destination is always usable
    // (RTK gates only one entry, Mount Baekdu, by simply omitting it from the list pre-quest).
    //
    // SendWorldMap's body was recovered by statically disassembling THIS project's own 4.95 client (not
    // guessed, and NOT trusting RTK 7.x, whose clif_mapselect has a different shape): opcode 0x2e's receive
    // handler is 0x450580 (verified via the real two-level dispatch table at 0x44bc80/0x44bbd4:
    // sel = idx[opcode-3], jmp jumptab[sel]; opcode 0x2e -> sel 22 -> stub 0x44bac4 -> call 0x450580).
    // The 0x450580 parser reads, in order, straight off the packet body (payload = bytes AFTER the opcode):
    //   bgNameLen(u8)  <- payload[0] IS the length; there is NO leading "kind" byte
    //   bgName[bgNameLen]
    //   destCount(u8)
    //   one still-unexplained byte
    //   per-destination:  x0(u16BE) y0(u16BE)  name(u8 len + bytes)  mapId(u32BE)  x1(u16BE) y1(u16BE)
    // (each entry is exactly 2 u16 + name + 4 u16; the client reads mapId as two of those u16 slots.)
    // The background is "field10" = "Map of the Kingdom" (the overview world-map art in Inter.dat, one of
    // field10..field18 = the whole-kingdom + per-region maps; NATION_E is only a 20KB flag icon, too small
    // to be a 640x480 background -- that's why it rendered black). Confirmed by rendering the candidate EPFs
    // to a grayscale contact sheet and reading their baked-in title banners. An earlier version of
    // this code sent a spurious leading kind=0 byte, which the client read as bgNameLen=0 -> empty name ->
    // a "%s.epf" path builder produced "." -> catlookup2(".") -> and every later field was shifted one byte,
    // so destCount/offsets became garbage and the handler eventually made a bogus huge allocation and threw.
    // That was OUR one-byte framing error, not a client bug (the client is retail-shipped and works). The
    // client's click/ESC reply is LIVE-CONFIRMED (opcode 0x3F, body mapId(u32BE) x(u16BE) y(u16BE) 00 --
    // RTK's case 0x3F map-change); HandleWorldMapSelect below decodes it and either warps to the clicked
    // destination or, for ESC/unrecognized coords, back to the origin. Of RTK's nine destinations, only
    // Mount Baekdu is omitted outright: its map 4259 has no renderable map data here (game-data/map_index.csv).
    // Hamgyong Nam-Do IS carried, but not to RTK's target: RTK warps it to map 99 ("North Hamgyong Valley"),
    // which has no map data, so it goes to map 114 -- the map literally NAMED "Hamgyong Nam-Do" -- landing on
    // (13,1), just inside the map's north gate. Its return trigger is 114's north edge, y=0 x∈12..15, so the
    // arrival tile sits directly below it. Those four tiles are ALSO Warps.csv 283-286 (114 -> map 99), but that warp
    // never fires here: the warp branch in HandleWalk is gated on Content.TryMap(dest.m), and 99 has no map
    // data, so the step completes normally and the after-step hook below gets the tile. Nagnang IS carried at
    // RTK's own numbers: trigger "Nagnang Gathering" (2520) y=5, x∈7..9 — the top row of that map's walkable
    // corridor, with no competing Warps.csv row — landing back on (8,8). Hausson (1025) is renderable too and
    // could be added the same way; it simply isn't listed yet.
    // X,Y = landing tile on the destination map. Destinations + their field10 dot pixels are data-driven
    // (game-data/WorldMapDests.csv -> Content.WorldDests, order-significant); the trigger tiles that open
    // the screen live in Content.WorldMapTriggers (WorldMapTriggers.csv). Both hot-reload via @reload.
    //
    // DOT PIXELS: DotX/DotY is the CENTRE of the label button, not its top-left. Proven in the client at
    // 0x423600, which the world-map draw loop (0x423500) calls once per entry:
    //     w = textWidth(name) + 0xc ; h = fontHeight * 2
    //     left = x0 - w/2 ; top = y0 - h/2 ; right = left + w ; bottom = top + h
    // So DO NOT scale RTK's 7.x x0/y0 into this space -- those numbers are pixels in a DIFFERENT background
    // image (RTK's "WMkru"), and no scale factor makes them land correctly; that is what put every button in
    // the wrong place. Pick coordinates straight off the real 640x480 artwork instead:
    //     python re/worldmap_plot.py --grid
    // renders field10.epf out of the client's own Inter.dat and draws each button with the exact geometry
    // above, flagging any that fall on the wooden frame or under the "Map of the Kingdom" banner. Iterate
    // there with --move/--add, then bake the numbers into WorldMapDests.csv. ("@wmpos <i> <x> <y>" still
    // works for a live in-client nudge, but the plot tool is the faster loop.)

    // Ephemeral live-tuning overrides for the world-map dot pixels, set by "@wmpos <i> <x> <y>" (index into
    // Content.WorldDests). Not persisted — you eyeball a dot live, then bake the final number into
    // WorldMapDests.csv and @reload. Empty = every dot uses its CSV DotX/DotY.
    private static readonly Dictionary<int, (int X, int Y)> WorldDotOverride = new();

    // True while a world-map screen we sent is (as far as we know) still open on the client, so a stray
    // 0x3F that happens to coincide with a real destination can't be mistaken for a real click.
    private bool _worldMapPending;
    // Where the player was standing when the world map opened. Opening the map makes the client "leave the
    // world" (full-screen modal); pressing ESC sends a 0x3F carrying these origin coords, and we warp back
    // here to restore the view (RTK exits the same way -- see HandleWorldMapSelect).
    private ushort _worldMapReturnMap, _worldMapReturnX, _worldMapReturnY;

    // Fires the native full-screen world-map screen at the real trigger tiles (re-enabled 2026-07-26 after
    // the one-byte framing bug was found and fixed -- see SendWorldMap). Falls back to nothing if bgName
    // resolution fails client-side; if a fresh crash ever recurs, revert this to RunWorldMapMenuAsync().
    private void TryWorldMapTravel()
    {
        if (!Content.WorldMapTriggers.TryGetValue(_char.Map, out var trig) || !trig.Hits(_char.X, _char.Y)) return;
        SendWorldMap("field10");
    }

    // The earlier "crashes regardless of content / client memory-lifetime bug" conclusion was WRONG: the
    // crash was a one-byte framing error in the packet BELOW (a spurious leading kind=0 byte that the client
    // read as bgNameLen=0, misaligning every field -- see the class comment above SendWorldMap). Once that
    // byte is removed and a real background name is used (field10 = "Map of the Kingdom"), the packet parses
    // correctly. The retail client is not buggy. "@wmtest <name>" tries alternate background graphics.
    // 5.33 ADDENDUM -- the world map is a GRAPH there, and the 4.95 body CRASHES it.
    // Recovered by disassembling NextAeon533\NexusTK.exe (full write-up in docs/5.x/Wire-Divergences.md
    // section 10). 0x2e is owned by the world dispatcher, case 0x4636f9 -> parser sub_469c80. The header
    // and the per-entry prefix are byte-identical to 4.95's sub_450580, but every entry ends with a LINK
    // LIST that 4.95 does not have:
    //     u16BE linkCount ; linkCount x u16BE nodeIndex
    // which the parser folds into an n x n adjacency bitset (bit[i*n + j] = "from i you can reach j", set
    // at 0x469fa8). Sending the 4.95 shape makes the client read the NEXT entry's dot-x as linkCount -- a
    // several-hundred-iteration inner loop that runs off the end of the packet and ORs bits at unbounded
    // indexes into a heap block sized for n*n bits. THAT is the "Win32Error: not enough memory resources"
    // crash: our packet, not a client bug -- same lesson as the 4.95 one-byte framing error above.
    //
    // The graph is not decoration. sub_4e6360 BFSes it from the origin node and stores predecessors at
    // [obj+0x1e0]; the draw loop sub_4e51a0 DIMS any node the BFS did not reach, and clicking a lit one
    // walks an animated marker hop-by-hop along the edges before the reply goes out. Edges are DIRECTED
    // (the BFS only follows i->j), so we emit a complete graph: every node links to every other, which
    // leaves every destination lit and exactly one hop away. Model real routes here later if the marker
    // should follow roads instead of flying straight.
    //
    // The byte after the count -- "unexplained" on 4.95 -- is the ORIGIN NODE INDEX. sub_4e4b80 stores it
    // at [obj+0x174]/[obj+0x178] and uses it three ways: BFS root, the "you are here" icon (WMICON.EPF
    // frame 1 instead of frame 0), and the centre of the scrolling camera. It is also what ESC sends
    // back -- the key table at 0x4e6fb0 maps VK_ESCAPE (0x1b) to 0x4e6bcf, which transmits node[origin]'s
    // own map/x/y. So 5.33 cancels explicitly rather than by 4.95's "ESC replies with entry 0" accident;
    // since we already put the origin first for 4.95's sake, index 0 satisfies both.
    //
    // Two more 5.33 findings, both already satisfied by what we send:
    //   * mapId. The parser reads it as two u16s and the wrapper at 0x467870 DROPS the high half, so the
    //     client only ever knows the low 16 bits -- and echoes back a u16. We keep sending u32BE (the
    //     parser consumes both halves either way); the narrower reply is decoded in HandleWorldMapSelect.
    //   * assets. The background is "<bgName>.EPF" plus "<bgName>.PAL" (name literals at 0x555678 /
    //     0x5556b4) and the dots come from WMICON.EPF. field10.epf is BYTE-IDENTICAL between the two
    //     clients' archives (4.95 Inter.dat vs 5.33 NInt.dat, md5 c29f3007..), so the WorldMapDests.csv
    //     dot pixels carry straight over -- with one caveat to eyeball: 4.95 centres the label box on
    //     DotX/DotY, while 5.33 centres only horizontally and hangs the text BELOW the anchor (0x4e52e9:
    //     top = y + 8). Expect 5.33 labels to sit about a half-line lower than 4.95's.
    //
    // Limits the client will not check for us: bgName <= 23 chars (its wide buffer is 0x18), entry names
    // <= 63 chars (the node struct's inline name field is 0x80 bytes), at most 256 entries.
    public readonly record struct WorldMapEntry(string Name, ushort Map, ushort X, ushort Y, int DotX, int DotY);

    /// <summary>
    /// The 0x2e body for one client. Pure and static so Tests/ClientVersionWireTests can pin both shapes:
    /// V495 is byte-for-byte what the working client has always received, V533 adds the per-entry link
    /// list and puts the origin node index in the byte after the count.
    /// </summary>
    public static byte[] WorldMapBody(ClientVersion ver, string bgName, IReadOnlyList<WorldMapEntry> entries, int originIndex)
    {
        bool v533 = ver == ClientVersion.V533;
        int n = Math.Min(entries.Count, 255);
        var d = new List<byte>();
        AddLenStr(d, bgName);                       // payload[0] IS the length -- no leading kind byte
        d.Add((byte)n);
        // 4.95 ignores this byte (we have always sent 0); 5.33 reads it as the origin node index.
        d.Add((byte)(v533 && n > 0 ? Math.Clamp(originIndex, 0, n - 1) : 0));
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            d.AddRange(Be((ushort)e.DotX));
            d.AddRange(Be((ushort)e.DotY));
            AddLenStr(d, e.Name);
            d.AddRange(Be32(e.Map));                // 5.33 keeps only the low half of this
            d.AddRange(Be(e.X));
            d.AddRange(Be(e.Y));
            if (!v533) continue;
            d.AddRange(Be((ushort)(n - 1)));        // complete graph: every other node is one hop away
            for (int j = 0; j < n; j++) if (j != i) d.AddRange(Be((ushort)j));
        }
        return d.ToArray();
    }

    private void SendWorldMap(string bgName)
    {
        var dests = Content.WorldDests;
        // Origin = where the player opened the map. Captured up-front because it's both the ESC/cancel
        // landing AND the entry-0 override below.
        ushort originMap = _char.Map, originX = _char.X, originY = _char.Y;

        // ESC-CANCEL FIX (2026-07-29, live-proven): the 4.95 client's ESC (exit without choosing) sends
        // back the FIRST destination in the list we send -- there is NO cancel opcode and NO origin echo.
        // (Proof: with Kugnae first, ESC's 0x3F body was byte-identical to the Kugnae dot, 1011/18/14,
        // regardless of where the map was opened -- so it ALWAYS warped to Kugnae. The old code comment
        // claiming ESC "carries the origin" was wrong.) Every trigger map IS one of these destination maps,
        // so we put the player's CURRENT continent first with its landing tile overridden to the exact
        // origin tile: ESC then round-trips to origin (which matches no real WorldDests row, so
        // HandleWorldMapSelect's cancel branch restores the player in place), while every other dot travels
        // as before. Dot PIXELS are unchanged (each dot keeps its own DotX/DotY) -- only wire order shifts.
        var order = new List<int>(dests.Count);
        for (int i = 0; i < dests.Count; i++) if (dests[i].Map == originMap) order.Add(i);
        for (int i = 0; i < dests.Count; i++) if (dests[i].Map != originMap) order.Add(i);
        if (order.Count == 0 || dests[order[0]].Map != originMap)
            Log.Info($"   -> WORLDMAP WARN: opened on map {originMap} with no matching destination row; ESC-cancel will not work (add a WorldMapDests row for this map)");

        var entries = new List<WorldMapEntry>(order.Count);
        foreach (int i in order)
        {
            var dest = dests[i];
            // Dot position is field10's own pixel coordinate (WorldMapDests.csv DotX/DotY), unless a live
            // "@wmpos" tweak is overriding it this session -- placed directly on the displayed map, not scaled
            // from RTK. Clamp defensively to the 640x480 art.
            var (dotX, dotY) = WorldDotOverride.TryGetValue(i, out var ov) ? ov : (dest.DotX, dest.DotY);
            // The current-continent entry (position 0) lands on the EXACT origin tile, so an ESC that
            // selects it returns the player precisely where they stood -- not the continent's default tile.
            bool isOrigin = dest.Map == originMap;
            entries.Add(new WorldMapEntry(dest.Name, dest.Map,
                                          isOrigin ? originX : dest.X,
                                          isOrigin ? originY : dest.Y,
                                          Math.Clamp(dotX, 0, 639), Math.Clamp(dotY, 0, 479)));
        }
        // Origin-first ordering above means the origin is entry 0 whenever it was found at all; 5.33 wants
        // that index explicitly (BFS root / "you are here" icon / camera centre / what ESC echoes back).
        int originIndex = entries.FindIndex(e => e.Map == originMap);
        if (originIndex < 0) originIndex = 0;
        _worldMapPending = true;
        _worldMapReturnMap = originMap;
        _worldMapReturnX   = originX;
        _worldMapReturnY   = originY;
        SendMap(0x2e, _gameInc++, WorldMapBody(_ver, bgName, entries, originIndex),
                $"worldmap(0x2e) bg='{bgName}' {entries.Count} dests (origin map {originMap} @ index {originIndex}){(_ver == ClientVersion.V533 ? " +graph" : "")}");
    }

    // Parses the client's world-map click / ESC reply -- RTK's case 0x3F map-change (clif.c:11619, pc_warp
    // with the client-supplied map/x/y). There is NO separate cancel opcode on either client: opening the
    // map makes the client "leave the world", and BOTH a destination click and ESC send this same 0x3F.
    // The body is one of two widths (see the version split below):
    //     4.95   mapId(u32BE) x(u16BE) y(u16BE) 00
    //     5.33   mapId(u16BE) x(u16BE) y(u16BE)          -- no high half, no trailing NUL
    // ESC does NOT echo the origin TILE on either (the old comment was wrong -- see SendWorldMap's
    // ESC-CANCEL FIX): 4.95 echoes the FIRST list entry, and 5.33 echoes node[originIndex] outright. We
    // make both the same thing -- the player's current continent, landing on the exact origin tile. So:
    // if the reply is the origin tile, treat it as ESC/cancel and restore in place;
    // else warp to the matching known destination; else (unrecognized) also fall back to restoring origin,
    // so the player can never be stranded on the map screen or mis-warped to arbitrary client-chosen coords.
    private void HandleWorldMapSelect(byte[] dec)
    {
        if (!_worldMapPending) return;
        _worldMapPending = false;
        uint map; ushort x, y;
        if (_ver == ClientVersion.V533)
        {
            // 5.33's reply is SIX body bytes, not eight: its 0x2e wrapper (0x467870) throws away the high
            // half of the map id we sent, so the node only ever holds a u16 and all five send sites
            // (0x4e5cd3 click-confirm, 0x4e5daf marker-arrived, 0x4e6960 clicked-own-node, 0x4e6b00,
            // 0x4e6be9 ESC) emit the same 7-byte frame: 3F mapId(u16BE) x(u16BE) y(u16BE). The trailing
            // NUL those builders write at buf[7] is NOT transmitted -- the send call passes length 7.
            if (dec.Length < 6) return;
            map = (uint)((dec[0] << 8) | dec[1]);
            x   = (ushort)((dec[2] << 8) | dec[3]);
            y   = (ushort)((dec[4] << 8) | dec[5]);
        }
        else
        {
            if (dec.Length < 8) return;
            map = (uint)((dec[0] << 24) | (dec[1] << 16) | (dec[2] << 8) | dec[3]);
            x   = (ushort)((dec[4] << 8) | dec[5]);
            y   = (ushort)((dec[6] << 8) | dec[7]);
        }
        // ESC / clicked own location: the reply is the origin tile (entry 0). Restore in place -- must
        // still EnterMap to rebuild the view the modal world-map screen tore down.
        if (map == _worldMapReturnMap && x == _worldMapReturnX && y == _worldMapReturnY)
        {
            if (Content.TryMap(_worldMapReturnMap, out var sm))
            {
                Log.Info($"   -> WORLDMAP (esc/cancel) stay at {_worldMapReturnMap} '{sm.Name}' ({_worldMapReturnX},{_worldMapReturnY})");
                EnterMap(sm.Id, sm.Xs, sm.Ys, _worldMapReturnX, _worldMapReturnY, sm.Name);
            }
            return;
        }
        foreach (var dest in Content.WorldDests)
        {
            if (dest.Map != map || dest.X != x || dest.Y != y) continue;
            if (!Content.TryMap(dest.Map, out var dm)) return;
            Log.Info($"   -> WORLDMAP (native) {_char.Map} -> {dest.Map} '{dm.Name}' ({dest.X},{dest.Y})");
            EnterMap(dm.Id, dm.Xs, dm.Ys, dest.X, dest.Y, dm.Name);
            return;
        }
        // Not a known destination -> treat as ESC/cancel: restore the player to their origin.
        if (Content.TryMap(_worldMapReturnMap, out var om))
        {
            Log.Info($"   -> WORLDMAP (esc/cancel) back to {_worldMapReturnMap} '{om.Name}' ({_worldMapReturnX},{_worldMapReturnY}) [reply map={map} ({x},{y})]");
            EnterMap(om.Id, om.Xs, om.Ys, _worldMapReturnX, _worldMapReturnY, om.Name);
        }
    }

    // "@travel" — chat-command fallback using the already-proven async dialog primitives, so travel keeps
    // working end-to-end even before the native screen's click-reply format (above) is confirmed live.
    private static readonly Mob WorldMapVirtualNpc = new(0xFFFFFFFC, 0, 0, 0, "WorldMap", 1);

    private async Task RunWorldMapMenuAsync()
    {
        // The menu await can suspend for as long as the player takes to answer, during which something
        // else entirely could move them (a GM @warp, death+revive, another dialog, disconnect). Re-verify
        // they're still on the same map when the reply comes back -- same "don't trust state from before
        // the await" discipline as the trade flow re-validating live inventory at finalize.
        ushort startMap = _char.Map;
        int choice = await DlgMenu(WorldMapVirtualNpc, "Where would you like to travel?",
            Content.WorldDests.Select(d => d.Name).ToList());
        if (choice < 1 || choice > Content.WorldDests.Count) return;
        if (_char.Map != startMap) return;   // moved on since we opened the menu
        var d = Content.WorldDests[choice - 1];
        if (!Content.TryMap(d.Map, out var dm)) return;   // dest not renderable here -- silently ignore
        Log.Info($"   -> WORLDMAP (menu) {_char.Map} -> {d.Map} '{dm.Name}' ({d.X},{d.Y})");
        EnterMap(dm.Id, dm.Xs, dm.Ys, d.X, d.Y, dm.Name);
    }

    // Chu Rua's young ginseng (onScriptedTilesQuest.lua, "Guol Tiger Pass" = map 1116): the rocks at x 5-6,
    // y 2-4 hold one young_ginseng. The tiger guards them until you distract him (say "rabbit" -> Forest, which
    // sets chu_rua_tiger_gone); until then it's "too dangerous". (RTK warps to a tiger-free copy, map 1117, but
    // that map isn't renderable here, so we gate on the flag instead and keep you on 1116.)
    private void TryGinseng()
    {
        if (_char.Map != 1116) return;
        if (!((_char.X == 5 || _char.X == 6) && _char.Y >= 2 && _char.Y <= 4)) return;
        if (CountItem("young_ginseng") > 0) return;

        var def = Content.ItemByKey("young_ginseng");
        if (def is null) return;

        // BOTH outcomes are a dialog pop-up carrying the ginseng's own icon, not a minitext: that is what RTK
        // sends (dialogSeq against the item portrait) and what the screenshots on both walkthroughs show
        // (tswolf grabthatdamginseng.gif, nexusatlas churuaginseng.gif / chuaruastrange.gif). Single page,
        // fire-and-forget with the player's own entity id — exactly as the PvP-entry warning in EnterMap does
        // it — because there is no NPC here to hang the dialog on and nothing needs to await the dismissal.
        // (A stray 0x3A with no pending awaiter is a no-op; see HandleNpcDialog.)
        var icon = DialogPortrait.Item(IconOf(def), _ver == ClientVersion.V533 ? def.IconColor : (byte)0);

        if (_char.Quests.GetValueOrDefault("chu_rua_tiger_gone") != 1)
        {
            SendScriptMessageP(_char.Id, "You see a strange root in the rocks here. But with the tiger nearby, " +
                                         "it is too dangerous to try to climb up to it.",
                               icon, prev: false, next: false);
            return;
        }

        if (!GiveItem(def, 1)) return;
        SendScriptMessageP(_char.Id, "Snuggled between the rocks is a young root of ginseng. Was this what Chu Rua meant?",
                           icon, prev: false, next: false);
    }

    // Mythic cave "fall rooms": inside a zodiac cave, every step has a 1/500 chance to drop through the floor
    // to a fixed landing tile in a lower sub-room (onScriptedTilesMythicFallRooms.lua). The source->landing
    // map is data-driven (game-data/FallRooms.csv -> Content.FallRooms, already tier-expanded); hot-reloads
    // via @reload.
    private const int FallRate = 500;

    private bool TryMythicFallRoom()
    {
        if (!Content.FallRooms.TryGetValue(_char.Map, out var f)) return false;
        if (Random.Shared.Next(FallRate) != 0) return false;
        if (!Content.TryMap(f.Map, out var dm)) return false;   // dest not renderable -> no fall (don't strand)

        // Leave a one-shot "shiver" echo on the tile we fall THROUGH so the next passer-by senses a trap
        // sprang here (RTK's WarpTrapShiverNpc — tiger-only in RTK, unified onto every fall cave by design).
        // Never expires (matches RTK: the marker sits until someone steps on it). PC-only cosmetic — mobs
        // ignore it (World mob-trap lookups skip it) and Watchful Eye doesn't flag it (CastSpotTraps skips it).
        _world.PlaceTrap(_char.Map, _char.X, _char.Y, "shiver", _char.Id);

        Log.Info($"   -> FALL through map {_char.Map} -> {f.Map} '{dm.Name}' ({f.X},{f.Y})");
        EnterMap(dm.Id, dm.Xs, dm.Ys, f.X, f.Y, dm.Name);
        SendMiniText("You fall into a steep winding passage.");   // RTK warp_trap flavor, unified to all fall caves
        return true;
    }

    // Bush/tree foraging (onScriptedTilesBushTree.lua): standing next to an apple tree (object ids 860-864)
    // or a rose bush (876-889), each step has a 1/50 chance to pick an apple / rose. Objects are read from the
    // map's OWN object layer (same ids RTK's checkProximityObjects uses), scanned in the 3x3 around the player.
    //
    // GATED TO THE TWO CAPITALS FOR NOW. The trigger is a SPRITE id, not a placed script, so unrestricted it
    // fires on all 829 apple trees / 37 maps the 4.95 terrain ships — 13720 walkable tiles inside a tree's 3x3
    // apron, 73% of Orchard Grove's floor, 32% of Southern Koguryo's. (RTK ran the same script over far fewer
    // trees: its own .map files were re-landscaped, and its Vale and Orchard Grove have none at all.) Kugnae
    // and Buya alone is a deliberate holding position while the real scope is decided — revisit and widen or
    // move this to content data then.
    private static readonly ushort[] ForageMaps = { 0, 330 };   // Kugnae, Buya
    private const int ForageRate = 50;
    private void TryForage()
    {
        if (Array.IndexOf(ForageMaps, _char.Map) < 0) return;

        var map = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (map is null) return;

        string? item = null;
        for (int dy = -1; dy <= 1 && item is null; dy++)
        for (int dx = -1; dx <= 1 && item is null; dx++)
        {
            int tx = _char.X + dx, ty = _char.Y + dy;
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) continue;
            ushort o = map.Obj(tx, ty);
            if (o >= 860 && o <= 864) item = "apple";
            else if (o >= 876 && o <= 889) item = "rose";
        }
        if (item is null) return;
        if (Random.Shared.Next(ForageRate) != 0) return;

        var def = Content.FindItem(item);
        if (def is null || !GiveItem(def)) return;
        SendMiniText(item == "apple" ? "You found an apple." : "You find a beautiful rose!");   // RTK onScriptedTilesBushTree.lua: sendMinitext, exact wording
        Log.Info($"   -> FORAGE {item} on map {_char.Map} @({_char.X},{_char.Y})");
    }

    // Move the player to another map (or a far tile) and redraw. On 4.95 the client loads its OWN local
    // Maps\TK<id>.map from the 0x15 mapId, so a warp is just: update tracked position, then re-send the
    // entry trio — 0x15 (map) + 0x04 (coords + camera) + 0x33 (our sprite). The world object (0x02) and
    // our entity id (0x05) are already established this session, so those are NOT resent.
    /// <param name="arrival">How the requested tile becomes the tile landed on. The default is what every
    /// arrival in this file has always done — clamp to the map and take it, occupied or not. Only
    /// <c>@approach</c>/<c>@bring</c> pass anything else; see <see cref="ArrivalPolicy"/>.</param>
    /// <returns>The tile actually landed on. Callers that report it (<c>@bring</c>) read it from here rather
    /// than from what they asked for, since a policy may have moved it.</returns>
    private (ushort x, ushort y) EnterMap(ushort mapId, ushort xs, ushort ys, ushort x, ushort y, string mapName,
                                          ArrivalPolicy arrival = ArrivalPolicy.Clamp)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        // Captured before anything below overwrites it: the tile we still hold while this move is in flight.
        // LeaveMap takes us off the map's player list a few lines down, so a policy that searches for a free
        // tile could otherwise offer us the one we are standing on. See World.PlacePlayer's `from`.
        var from = new FromTile(_char.Map, _char.X, _char.Y);
        // Warn on crossing INTO a PvP realm (RTK MapPvP flag — Content.IsPvpMap) from a non-PvP one, e.g.
        // stepping through an arena door into Sire Pit/Yusa Pit. Skipped when already in a PvP map (tier
        // warps within the same arena chain shouldn't re-nag every hop).
        // DISABLED per request — the pop-up on arena entry was unwanted. Flip PvpEntryWarning to re-enable
        // the whole thing (the message block below is kept intact on purpose).
        const bool PvpEntryWarning = false;
        bool warnPvp = PvpEntryWarning && Content.IsPvpMap(mapId) && !Content.IsPvpMap(_char.Map);

        // Leave the OLD map in the shared world (despawn us for the players we're leaving behind), and
        // clear our session-local debug dummies (the client drops all foreign entities on a map change).
        _world.LeaveMap(this, _char.Map);
        _mobs.Clear();
        ResetStreamCoverage();   // terrain streamed for the old map says nothing about the new one
        _dlgReply = null;    // orphan any NPC prompt awaiting a reply — its NPC is on the old map
        _worldMapPending = false;   // any open world-map screen is meaningless once we've already warped
        ForgetShownMobs();   // new map -> the client wiped every foreign entity; re-stream from scratch

        _char.Map = mapId;
        _char.MapXs = xs;
        _char.MapYs = ys;
        // The position write goes through the world (#99 part 1): the arrival tile is resolved and written in
        // ONE acquisition of World._lock, the lock every reader of PlayerX/PlayerY takes. It used to be a bare
        // clamp and two assignments here, under no lock at all — and for @approach/@bring the tile had been
        // chosen even earlier, through two more acquisitions that were long released by the time it was used.
        // The default policy is the clamp itself, so what lands where is unchanged.
        _world.PlacePlayer(this, mapId, xs, ys, x, y, arrival, from, out ushort placedX, out ushort placedY);
        MarkDirty();   // map + position, same reasoning as HandleWalk

        SendMapInfo(mapId, xs, ys, mapName, 232, _gameInc++);   // 0x15 (light arg ignored; uses LightValue)
        SendXy();                                                // 0x04 coords + camera anchor
        SendSelfLook();                                          // 0x33 draw self on the new map
        PrimeViewport("warp");                                   // 0x06 fill the window before the client asks
        PlayMapMusic(mapId);                                     // 0x19 swap to the new map's track (if different)
        SendWeather(_world.GetWeather(mapId));                   // 0x1F whatever the new map's weather already is

        // Join the NEW map: draw the players + mobs already there for us, and broadcast us to them.
        var (peers, mobs) = _world.EnterMap(this, mapId);
        SyncPeers(peers);   // stream the in-view players of the new map (0x33, viewport-gated + tracked)
        SyncMobs(mobs);   // stream the in-view mobs of the new map
        SyncGroundItems(_world.ItemsOn(mapId));   // in-view floor items of the new map (0x07, viewport-gated)
        if (_showWarps) StampWarpMarkers();       // @showwarps follows across maps: overlay the NEW map's doorways
        SyncMapDoors(mapId);
        if (warnPvp)
        {
            SendScriptMessageP(_char.Id,
                "Be careful, you may be slain by another player within this realm and items on the floor " +
                "can be destroyed by bombs!", DialogPortrait.None, prev: false, next: false);
        }
        Log.Info($"   -> ENTER map {mapId} '{mapName}' {xs}x{ys} @({_char.X},{_char.Y}) — {peers.Length} player(s), {mobs.Length} mob(s) here");
        return (placedX, placedY);
    }

    // Bring the arriving client's object layer in line with the server's. The 4.95 client draws its own local
    // .map file for everything except the narrow 0x06 cell-patch mechanism (the same one door toggles use), so
    // ANY server-side object change is invisible until we replay it — and self-walk is client-local
    // ([[nexustk-495-selfwalk-turn]]), so a door the client still believes is shut keeps refusing the step no
    // matter what the server thinks. Three things need replaying, and MapData.PatchRuns covers all of them at
    // once because every one of them goes through SetObj:
    //   * doors that START open (Content.DoorDefaultOpen, applied in MapData.Load — e.g. the city gates),
    //   * doors another player has toggled since the map was first loaded (previously invisible to later
    //     arrivals, who saw the file state and then got a first 'o' that appeared to do nothing),
    //   * ForceOpen tiles (Doors.cs), which have no real "open" sprite and are simply cleared to object 0.
    private void SyncMapDoors(ushort mapId)
    {
        var md = MapData.For(mapId, _char.MapXs, _char.MapYs);
        if (md is null) return;
        // ForceOpen tiles used to be stamped on here, per session, which mutated shared map state from a
        // session path — they are an AUTHORED override now and applied once in MapData.Load.
        foreach (var (x, y, objs) in md.PatchRuns()) PatchObjRow(x, y, objs);
    }

    // "@warp <map name or id> [x y]": jump to another map by fuzzy name or numeric id, optional coords.
    // Trailing "x y" integers are the destination tile; the rest is the map query. Defaults to map centre.
    private void Warp(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }

        // Trailing "x y" only counts as coordinates when something is left over to name the map with —
        // "@warp 12 5" is map 12 at whatever's left, not a nameless map at (12,5).
        int? cx = null, cy = null; int end = a.Count;
        if (a.Count >= 3 && a.Int(a.Count - 1, out var py) && a.Int(a.Count - 2, out var px))
        { cx = px; cy = py; end = a.Count - 2; }

        string query = a.Rest(0, end);
        var map = Content.FindMap(query);
        if (map is null) { Refuse($"no map matches \"{query}\" — try  {Prefix}maps {query}"); return; }

        ushort x = (ushort)(cx ?? map.Xs / 2);
        ushort y = (ushort)(cy ?? map.Ys / 2);
        EnterMap(map.Id, map.Xs, map.Ys, x, y, map.Name);
        Reply($"Warped to {map.Name} (map {map.Id}, {map.Xs}x{map.Ys}) at ({_char.X},{_char.Y}).");
    }

    // "@go <x> <y>": jump to a tile on the map you are ALREADY on — the short, no-map-lookup half of @warp.
    // Anything that isn't two in-bounds integers (missing argument, a word, a coordinate off the edge of this
    // map) lands you on (0,0) rather than refusing: the command always moves you somewhere, and the reply
    // says which of the two happened.
    private void GoCmd(CommandArgs a)
    {
        // -1 stands in for "absent or not a number", which the bounds check below rejects anyway — the
        // out-parameter form cannot be used here because && short-circuits before the second one is assigned.
        int gx = a.Int(0, -1), gy = a.Int(1, -1);
        bool ok = gx >= 0 && gx < _char.MapXs && gy >= 0 && gy < _char.MapYs;
        if (!ok) { gx = 0; gy = 0; }

        // Same map, so the live dims are already right; the registry only supplies the 0x15 name string.
        // EnterMap is the ONLY proven way to relocate the self entity on 4.95 — a bare 0x04 is a one-tile
        // snap-back, not a teleport — which is why the Rogue leap spells jump the same way, and it is
        // same-map-safe: the World leave/enter pair just re-registers us where we already were.
        bool named = Content.TryMap(_char.Map, out var md);
        string name = named ? md.Name : "Nexus";   // HandleRefresh's fallback: 0x15 needs SOME name string
        string where = named ? $"{name} (map {_char.Map})" : $"map {_char.Map}";
        EnterMap(_char.Map, _char.MapXs, _char.MapYs, (ushort)gx, (ushort)gy, name);

        // The move you ASKED for either happened or it did not, and "sent you to (0,0)" is recovery rather
        // than the thing you wanted — so a bad coordinate is a refusal, loud, and the pane line under it
        // confirms where the recovery actually put you. Both halves, because both are true.
        if (ok) { Reply($"Moved to ({_char.X},{_char.Y}) on {where}."); return; }
        Refuse(a.Usage());
        Reply($"0..{_char.MapXs - 1} / 0..{_char.MapYs - 1} on {where}; sent you to (0,0).");
    }

    // "@maps [filter]": list maps, fuzzy-ranked by name (blank = alphabetical). Capped so we don't flood.
    private void ListMaps(CommandArgs a)
    {
        string q = a.Raw;
        var found = Content.SearchMaps(q, 15);
        if (found.Count == 0) { Reply(q.Length == 0 ? "no maps loaded (run re/build_map_index.py)" : $"no maps match \"{q}\""); return; }
        ReplyList($"maps{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count}/{Content.Maps.Count})",
                  found.Select(m => $"{m.Id}: {m.Name} ({m.Xs}x{m.Ys})"));
    }

    // "@mobs [filter]": list summonable mobs, fuzzy-ranked by name.
    private void ListMobs(CommandArgs a)
    {
        string q = a.Raw;
        var found = Content.SearchMobs(q, 15);
        if (found.Count == 0) { Reply(q.Length == 0 ? "no mobs loaded (check game-data/mobs.csv)" : $"no mobs match \"{q}\""); return; }
        // Two short lines per mob rather than one long one, and no trailing "(@summon <name>)" repeating the
        // name it follows — same reshaping as @items, for the same 30-character pane.
        ReplyList($"mobs{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count}/{Content.Mobs.Count})",
                  found.SelectMany(m => new[]
                  {
                      m.Name,
                      $" look {m.Look} c{m.Color} {m.Hp}hp {m.Exp}xp",
                  }));
        Reply($"{Prefix}summon <name> to spawn");
    }

    // "@summon <mob name or id>": spawn a real, named creature from the registry on the tile in front of
    // you — correct look + palette colour + HP + exp, all data-driven. Same 0x07 spawn + melee-kill loop
    // as @rabbit, but any of the 700+ mobs by name. (No wander AI yet — that generalizes next.)
    private void Summon(CommandArgs a)
    {
        string q = a.Raw;
        if (q.Length == 0) { Refuse(a.Usage()); return; }
        var mob = Content.FindMob(q);
        if (mob is null) { Refuse($"no mob matches \"{q}\" — try  {Prefix}mobs {q}"); return; }

        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SummonWorldMob(mob.Look, x, y, mob.Name, mob.Hp, dir: (byte)((_facing + 2) & 3), color: mob.Color, exp: mob.Exp, moveTime: mob.MoveTime, key: mob.Key, def: mob);
        Reply($"Summoned {mob.Name} — look {mob.Look} c{mob.Color}, {mob.Hp}hp, dmg {mob.MinDam}-{mob.MaxDam}.");
    }

    // @reload — hot-reload all file-backed game content (mob stats, items, warps, shop stock, spells, spawns,
    // NPC placements + on/off toggles, crafting-skill toggles, map metadata, mob drops, map BGM, and the Lua
    // verb/dialog scripts) WITHOUT restarting the server, so content fixes ship live. Re-reads the CSVs +
    // Lua, clears the map-terrain cache, and fully rebuilds the world population (World.RebuildPopulation) so
    // ADDED/REMOVED/REPOSITIONED spawns and NPCs take effect — editing AreaSpawns.csv or an NPC's tile no
    // longer needs a restart. The terrain cache for maps that currently have players is pre-warmed OUTSIDE the
    // world lock first, so the .map re-reads don't stall the world under the lock. Everything file-backed is
    // reloadable now — no compile-time content tables remain that a restart would be needed for.
    //
    // The work itself lives in World.ReloadFromDisk, because a content deploy has no GM logged in to type
    // this — the CI content lane drops a run/reload_now sentinel and the world picks it up (see
    // RestartSchedule.Loop). This method is now just the chat-facing half: run it, report it to the GM.
    private void ReloadContent()
    {
        var (ok, report) = _world.ReloadFromDisk();
        // Contention is not a content failure, and this is the GM's read-loop thread: say why no work started
        // without making them wait behind the reload already doing the same disk-to-live sequence.
        // Contention is a REFUSAL, not a readout: this invocation started nothing, and telling the GM
        // quietly that someone else's reload is running reads exactly like their own having worked.
        if (ok) Reply($"Reloaded: {report}");
        else if (report == "reload already in progress") Refuse(report);
        else Refuse($"{Prefix}reload FAILED: {report}");
        Log.Info($"   -> @reload by '{_char.Name}': {report}");
    }

    // @restart [minutes] [reason] | @restart cancel | @restart  (status)
    //
    // The in-game half of RestartSchedule; the other trigger is the run/restart_at file a deploy writes.
    // Note this is deliberately NOT an immediate kill — there is no "@restart now" shorthand, because the
    // whole point of the ladder is that players get told. A GM who genuinely wants it down this second can
    // say "@restart 0", which still announces, still flushes every player, and still takes the grace period.
    private void RestartCmd(CommandArgs a)
    {
        var sched = _world.Restarts;

        if (a.None)
        {
            long left = sched.RemainingMs;
            Reply(left < 0
                ? $"No restart scheduled. {a.Usage()}"
                : $"Restart in {left / 60000}m{left / 1000 % 60:00}s. {Prefix}restart cancel to call it off.");
            return;
        }

        if (a.Is(0, "cancel") || a.Is(0, "off"))
        {
            // Nothing to cancel is a refusal for the same reason: you asked for something that did not
            // happen, and a quiet pane line is indistinguishable from having called off a real restart.
            if (sched.Cancel()) Reply("Restart cancelled.");
            else Refuse("Nothing to cancel.");
            return;
        }

        // "<minutes> [reason]" — the tail after the number is free text, so "@restart 30 deploying 1.2" works.
        // double, not Int: the ladder takes fractional minutes.
        if (!double.TryParse(a.Word(0), out double minutes) || minutes < 0 || minutes > 24 * 60)
        {
            Refuse(a.Usage());
            return;
        }
        string reason = a.Rest(1);

        sched.Schedule(minutes, reason);
        Log.Info($"   -> {Prefix}restart by '{_char.Name}': {minutes} min ({(reason.Length == 0 ? "no reason" : reason)})");
    }

}
