using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{
    /// <summary>Which tier of the '@' command table this session may reach. Read fresh from
    /// <see cref="StaffAccounts"/> on every command rather than cached at login, so a @reload-driven demotion
    /// takes effect immediately instead of at the offender's next relog. Keyed on the PERSISTED character
    /// name (case-insensitively), which world entry now refuses to invent — so staff status can't be claimed
    /// by connecting with a made-up name.</summary>
    private AccessLevel Access => _enteredWorld ? StaffAccounts.LevelFor(_char.Name) : AccessLevel.Player;

    /// <summary>Full operator access. Testers are deliberately excluded: the three things this gates (the
    /// user-list colour, the death-penalty exemption, the '!'-prefix nudge) are world-facing privileges
    /// rather than tooling, and a tester is an ordinary player as far as the world is concerned.</summary>
    private bool IsGm => Access == AccessLevel.Gm;

    // ---- item GM commands ----

    // "@items [filter]": browse the item registry, fuzzy-ranked by name.
    private void ListItems(CommandArgs a)
    {
        string q = a.Raw;
        var found = Content.SearchItems(q, 15);
        if (found.Count == 0) { Reply(q.Length == 0 ? "no items loaded (check game-data/Items.csv)" : $"no items match \"{q}\""); return; }
        // One line per item, short enough to survive the pane: the id first so the eye has a fixed column to
        // land on even though the font is proportional and nothing can actually be aligned. The old line
        // carried a trailing "(@item <name>)" hint that repeated the name it followed and pushed every entry
        // to three or four wrapped lines; the hint is now one line for the whole list.
        ReplyList($"items{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count}/{Content.Items.Count})",
                  found.Select(i => $"#{i.Id} {i.Name} — {(i.IsEquip ? $"equip {i.Dam}/{i.Armor}" : i.IsConsumable ? "use" : "etc")}"));
        Reply($"{Prefix}item <name> to summon");
    }

    // "@coins <n>" (alias "@gold <n>") — add n coins to the purse (updates the HUD + persists). A negative n
    // removes that many, floored at 0; "@coins" alone defaults to +10000. Coins aren't in the item registry
    // (they're a negative item id on the wire), so @item can't grant them — this is the direct GM path.
    private void GiveCoinsCmd(CommandArgs a)
    {
        int amount = 10000;                       // bare @coins is the common case; see the row's help
        if (!a.None && !a.Int(0, out amount)) { Refuse(a.Usage()); return; }

        if (amount >= 0) AwardGold((uint)amount);
        else
        {
            uint take = Math.Min(_char.Coins, (uint)(-(long)amount));
            _char.Coins -= take;
            SendStats(); SaveChar();
        }
        Reply($"Coins: {_char.Coins:N0} (changed by {amount:+#;-#;0}).");
    }

    // "@npc [name|id]" — bare: the switched-off readout below. With a name: find the NPC and jump beside
    // it. Quest testing is NPC-centric, and reaching one used to mean already knowing its map for @warp —
    // this removes the lookup step. An exact name wins outright; an ambiguous fragment lists the matches
    // rather than guessing. A disabled/era-gated NPC still resolves (you land at its empty spot, told so).
    private void NpcCmd(CommandArgs a)
    {
        string q = a.Raw;
        if (q.Length == 0) { NpcToggleCmd(a); return; }

        var matches = (int.TryParse(q, out var id)
            ? Content.Npcs.Where(n => n.Id == id)
            : Content.Npcs.Where(n => n.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0) { Refuse($"no NPC matches \"{q}\"."); return; }

        var npc = matches.FirstOrDefault(n => n.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
                  ?? (matches.Count == 1 ? matches[0] : null);
        if (npc is null)
        {
            ReplyList($"NPCs ~ \"{q}\" ({matches.Count})",
                      matches.Take(10).Select(n =>
                          $"#{n.Id} {n.Name} — map {n.Map} ({n.X},{n.Y}){(n.Enabled ? "" : " [off]")}"));
            return;
        }

        if (!Content.TryMap(npc.Map, out var md))
        { Refuse($"{npc.Name} (#{npc.Id}) is on map {npc.Map}, which isn't in the map registry."); return; }
        // Beside the NPC, not on top of it — the same search @approach uses, now run under the world lock
        // with the write it feeds (#99 part 1). The NPC's own tile is the fallback when it is boxed in.
        EnterMap(npc.Map, md.Xs, md.Ys, npc.X, npc.Y, md.Name, ArrivalPolicy.AdjacentFreeElseStack);
        Reply($"{npc.Name} (#{npc.Id}) — {md.Name} (map {npc.Map}) at ({npc.X},{npc.Y})." +
                (npc.Enabled ? "" : "  [disabled — the spot is empty]"));
    }

    // "@npc" / "@npc list" — show which NPCs are switched off. Read-only: toggling an NPC means setting the
    // Enabled column in game-data/NPCs.csv and running @reload (World.ReconcileNpcToggles spawns/despawns
    // to match), not a live GM mutation command. The tavern-hand "small guy" NPCs (Ox/Taur) are off by default.
    //
    // The two ways to be off are reported separately because the fix differs: an era-gated NPC (NPCs.csv
    // EraFeature — Yarlof, who arrives with the 2005 Druid bouquet quest) is absent because he does not exist
    // yet, and no amount of editing the Enabled column will bring him back.
    private void NpcToggleCmd(CommandArgs a)
    {
        static string Describe(NpcDef n) => $"#{n.Id} {n.Name} (map {n.Map})";

        var off = Content.Npcs.Where(n => !n.Enabled).OrderBy(n => n.Id).ToList();
        var unborn = off.Where(n => n.EraFeature.Length > 0 && !Era.Has(n.EraFeature)).ToList();
        var manual = off.Except(unborn).ToList();

        Reply((manual.Count == 0 ? "No NPCs are switched off."
                                   : $"Switched-off NPCs ({manual.Count}): " + string.Join(", ", manual.Select(Describe))) +
                "  (edit the Enabled column in game-data/NPCs.csv + @reload to change)");

        if (unborn.Count > 0)
            Reply($"Not yet in this era ({unborn.Count}): " +
                    string.Join(", ", unborn.Select(n => $"{Describe(n)} [{n.EraFeature}]")) +
                    "  (move EraDate in game-data/ServerTuning.csv — see @era)");
    }

    // "@craft" / "@craft list" — show which crafting skills are era-gated on/off. Read-only: the toggle
    // itself is config, not live GM state — edit game-data/CraftingToggles.csv and run @reload to
    // change it (see Server/CraftingToggles.cs + docs/common/Crafting-Values.md for why Jewelry and Food
    // Preparation/Chef default off).
    private void CraftToggleCmd(CommandArgs a)
    {
        var lines = CraftingToggles.AllSkills
            .Select(s => $"{s}={(CraftingToggles.IsEnabled(s) ? "ON" : "off")}");
        Reply("Crafting skills (edit game-data/CraftingToggles.csv + @reload to change): " +
                string.Join(", ", lines));
    }

    // "@era" — what date the world is pretending it is, and which dated features that includes. Read-only
    // for the same reason as @craft: the target date is deployment config (ServerTuning.csv EraDate), not
    // live GM state, so it moves by editing the file and running @reload. A feature with no row in
    // EraFeatures.csv is always present and deliberately isn't listed — see Server/Era.cs.
    private void EraCmd(CommandArgs a)
    {
        var now = Era.Today;
        if (now is null)
        {
            Reply("Era gating is OFF (EraDate=0 in game-data/ServerTuning.csv) — all dated content is present.");
            return;
        }

        // "from X" / "until X" / "X..Y" / "undated" — the window as declared, independent of whether the
        // current date happens to fall inside it (the ON/off flag already says that).
        static string Window(Shared.EraWindow? w) => w switch
        {
            null                                          => "undated",
            { Introduced: { } i, Retired: { } r }         => $"{i:yyyy-MM-dd}..{r:yyyy-MM-dd}",
            { Introduced: { } i }                         => $"from {i:yyyy-MM-dd}",
            { Retired:    { } r }                         => $"until {r:yyyy-MM-dd}",
            _                                             => "undated",
        };

        var lines = Era.KnownFeatures.Select(f =>
            $"{f}={(Era.Has(f) ? "ON" : "off")} ({Window(Era.Window(f))})");
        Reply($"Era date {now.Value:yyyy-MM-dd} — {string.Join(", ", lines)}  " +
                "(edit EraDate in game-data/ServerTuning.csv + @reload to change)");
    }

    // "@clock [0-23 | real]" — read or pin the shared in-game hour. The calendar otherwise derives strictly
    // from the real-world epoch (WorldClock.Sync), so the totem-time window — the thing @exp kill exists to
    // exercise — was only testable when the real clock happened to land in it. Pinning is WORLD-scoped (the
    // hour is one shared value; every session's 0x20 clock follows within a tick) and hour-only: day, season
    // and year keep deriving, because nothing behavioral hangs off them. `real` releases the pin.
    private void ClockCmd(CommandArgs a)
    {
        if (!a.None)
        {
            if (a.Is(0, "real")) _world.Clock.SetHourOverride(null);
            else if (a.Int(0, out var h) && h is >= 0 and <= 23) _world.Clock.SetHourOverride(h);
            else { Refuse(a.Usage()); return; }
            Log.Info($"   -> @clock '{_char.Name}': {(a.Is(0, "real") ? "released" : $"hour pinned to {a.Word(0)}")}");
        }

        var (hour, day, year) = _world.Clock.ClockNow;
        var totems = string.Join(", ", Enumerable.Range(0, 4)
            .Where(t => Content.IsTotemTime(hour, t)).Select(Content.TotemName));
        Reply($"In-game time: hour {hour} — day {day} of {_world.Clock.SeasonName}, Yuri {year}. " +
                $"Totem time: {(totems.Length > 0 ? totems : "none")}." +
                (_world.Clock.HourOverride is not null ? $" [hour pinned — {Prefix}clock real to release]" : ""));
    }

    // "@killtrack [clear]" — the eight-slot kill track, most-recent-first, which is what the mythic
    // alliances count (NOT the lifetime tally). Without this there is no way to see WHY a hand-in was
    // refused: a boss that has been pushed off the end looks exactly like a boss that was never killed.
    // `clear` wipes it, which is what accepting an alliance does. See Server/MythicAlliance.cs.
    private void KillTrackCmd(CommandArgs a)
    {
        if (a.Is(0, "clear"))
        {
            ClearKillTrack();
            Reply("Kill track cleared (this is what accepting a mythic alliance does).");
            return;
        }

        var rows = KillTrackRows;
        if (rows.Count == 0) { Reply("Kill track is empty."); return; }

        var lines = rows.Select((e, i) => $"{i + 1}. {Content.MobByKey(e.Mob)?.Name ?? e.Mob} x{e.Count}");
        Reply($"Kill track ({rows.Count}/{KillTrack.Slots} kinds, newest first): " + string.Join(", ", lines));
    }

    private void GiveItemCmd(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        // A trailing number is the AMOUNT, not part of the name — but only a positive one, so "@item Rice -1"
        // still looks for an item called "Rice -1" rather than granting a negative pile.
        int amount = 1;
        if (a.NameThenTrailingInt(out string q, out int n) && n > 0) amount = n; else q = a.Raw;
        var def = Content.FindItem(q);
        if (def is null) { Refuse($"no item matches \"{q}\" — try  {Prefix}items {q}"); return; }
        if (def.Stackable) GiveItem(def, amount);
        else for (int i = 0; i < amount; i++) if (!GiveItem(def)) break;
        Reply($"Gave {def.Name}{(amount > 1 ? $" x{amount}" : "")} (#{def.Id}, {(def.IsEquip ? $"equip slot {def.EquipSlot}" : def.IsConsumable ? "use" : "etc")}).");
    }

    // "@take <name|id> [amount|all]" — remove an item from the BAG (worn gear is untouched: unequip first).
    // The single-item cleanup @clearinv is too blunt for — testing "the NPC takes your item" without nuking
    // the rest of the pack. Goes through TakeItem, the same removal path quests use, so stacks drain low
    // slots first and every touched slot is redrawn. Asking for more than you hold takes all of them.
    private void TakeItemCmd(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        // Same trailing-count shape as @item, plus the "all" keyword in the same position.
        int amount = 1;
        string q = a.Raw;
        if (a.Count > 1 && a.Is(a.Count - 1, "all")) { amount = int.MaxValue; q = a.Rest(0, a.Count - 1); }
        else if (a.NameThenTrailingInt(out var name, out var n) && n > 0) { amount = n; q = name; }
        var def = Content.FindItem(q);
        if (def is null) { Refuse($"no item matches \"{q}\" — try  {Prefix}items {q}"); return; }
        int held = CountItem(def.Key);
        if (held == 0) { Refuse($"You aren't carrying any {def.Name}."); return; }
        int take = Math.Min(amount, held);
        TakeItem(def.Key, take);
        Reply($"Took {def.Name}{(take > 1 ? $" x{take}" : "")} — {held - take} left.");
    }

    // "@exp <n> [kill]" — award raw experience through the same funnels every real grant uses, so the whole
    // leveling path runs for real: the exp curve, multi-level carries, the Peasant wall, LevelUp's stat/HP/MP
    // gains. @lvl can't test any of that — it REBUILDS at a level. Bare @exp reports where you stand.
    //
    // The two forms are the two funnels, not one funnel with a flag (#155). Without `kill` this is a
    // quest-style grant: AwardExp, the caller's own, no totem window. With `kill` it goes through
    // AwardKillExp with the CALLER'S OWN TILE standing in for the corpse, so it behaves the way a mob dying
    // there behaves — split across the group members in range by the per-head share and their standing, and
    // the group-wide totem rule rather than the caller's own. It used to call AwardExp(n, killExp: true)
    // directly, which bought the 1.05 totem bonus and nothing else: a grouped caller took the whole grant and
    // the party standing next to them took nothing, which is not what "as if from a kill" says.
    //
    // No mobKey, deliberately: a GM grant is not the death of any creature, so no quest tally moves for
    // anyone it pays. AwardKillExp treats a null key as a no-op ("keyless kills (debug summons) are ignored"),
    // which is the same call shape a summoned mob's death already uses. The help text says so.
    private void ExpCmd(CommandArgs a)
    {
        // uint, not int: the grant feeds AwardExp, and a negative one has no meaning there.
        if (!uint.TryParse(a.Word(0), out var n) || n == 0)
        { Refuse($"exp is {_char.Exp:N0}. {a.Usage()}"); return; }
        if (a.Is(1, "kill")) AwardKillExp(n, _char.Map, _char.X, _char.Y);
        else AwardExp(n);
    }

    // "@dura <name|id> <n>" — set an item's current durability, bag first then worn, clamped to the item's
    // max. The only other way to wear something down is to actually grind it down, which makes repair NPCs
    // and breakage untestable in any reasonable time. Redraws the touched slot so the client shows the new
    // value immediately.
    private void DuraCmd(CommandArgs a)
    {
        // The same name-then-trailing-int shape as @item and @take, but the count is REQUIRED here.
        if (!a.NameThenTrailingInt(out var q, out var n) || n < 0) { Refuse(a.Usage()); return; }
        var def = Content.FindItem(q);
        if (def is null) { Refuse($"no item matches \"{q}\" — try  {Prefix}items {q}"); return; }
        ushort v = (ushort)Math.Min(n, (int)def.Durability);

        var bag = _char.Inventory.FirstOrDefault(i => i.ItemId == def.Id);
        var worn = bag is null ? _char.Equipment.FirstOrDefault(e => e.ItemId == def.Id) : null;
        if (bag is null && worn is null) { Refuse($"You aren't carrying or wearing {def.Name}."); return; }
        if (bag is not null) { bag.Dura = v; SendAddItem(bag); }
        else { worn!.Dura = v; SendEquip(worn); }
        MarkDirty();
        Reply($"{def.Name}: durability {v}/{def.Durability}{(bag is null ? " (worn)" : "")}.");
    }

    // "@clearinv": empty the bag + gear (test reset).
    private void ClearInventory()
    {
        foreach (var it in _char.Inventory.ToList()) SendDelItem(it.Slot, DelReason.Removed);
        _char.Inventory.Clear();
        foreach (var e in _char.Equipment.ToList()) SendUnequip(e.Slot);
        EquipClear();
        if (_char.Weapon != 0 || _char.Armor != 0)
        {
            _char.Weapon = 0; _char.Armor = 0;
            RefreshAppearance();
        }
        // Push the gear-less stats, as every unequip path does: SendStats clamps current HP/MP to the new
        // caps, so 1006/1006 in Cimmerian steel reads 6/6 now rather than at the next stats push (#206).
        SendStats();
        Reply("Cleared your pack and gear.");
    }

    // The client's item-sprite resolver (0x435ab0) does `spriteId = iconField + 0x4000`, then the frame
    // indexer (0x431450) bounds-checks the LOW 16 BITS against the Item.epf frame count (1310) — so to
    // render Item.epf frame N (== client item id), the packet icon field must be (N - 0x4000) & 0xFFFF,
    // which wraps back to N after the client's +0x4000. Sending N raw overflows (N+0x4000 >= 1310 → blank).
    private static ushort IconWire(int clientFrame) => (ushort)((clientFrame - 0x4000) & 0xFFFF);

    // "@mob <look> [hp] [color]": drop one monster on the tile in front of you as a REAL, SHARED world
    // entity — registered with World, streamed to every player whose viewport it enters, and fought by all
    // of them against one authoritative HP pool. Same path as @rabbit / @summon; the difference is that this
    // one takes a bare Monster.tbl look id and needs no registry row, so it can show anything in Monster.epf.
    //
    // It used to be a SESSION-LOCAL dummy (drawn straight to the caller over 0x16, never registered), which
    // meant nobody else could see what a GM spawned — and, more quietly, that it sat outside the _drawnMobs
    // bookkeeping every other entity is tracked by. The raw-sprite 0x16 probe that behaviour existed for is
    // still available, under @mobraw.
    //
    // Stationary on purpose (wander: false): these are calibration dummies for melee / sfx / sprite work, and
    // one that wanders off mid-measurement is worthless. Use @summon for a mob with its registry AI.
    //
    // HP 1 or more. An HP below 1 is refused with the usage line and spawns nothing: it used to spawn a
    // creature that is not Alive (Mob.Alive is Hp > 0), which SyncMobs never draws and World.DespawnMob
    // refuses, so only @kill could take it off again (PR #320's verdict, follow-ups).
    private void MobOne(CommandArgs a)
    {
        int look = a.Int(0, 0);
        int hp = a.Int(1, 6);
        int color = a.Int(2, 0);
        if (hp < 1) { Refuse(a.Usage()); return; }
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        var mob = SummonWorldMob((ushort)look, x, y, $"m{look}", hp,
                                 dir: (byte)((_facing + 2) & 3),   // face the spawner on arrival
                                 color: (byte)color, wander: false);
        Reply($"spawned look {look} (hp {hp}, colour {color}) — everyone on this map can see it");
        Log.Info($"   -> MOB world spawn {mob.Id} look={look} c{color} hp={hp} @({x},{y}) map {_char.Map}");
    }

    private void KillMobs()
    {
        int world = _world.ClearMap(_char.Map);   // shared mobs -> despawned for EVERYONE on this map
        int local = _mobs.Count;                  // session-local debug dummies -> just us
        if (local > 0) { SendDespawn(_mobs.Select(m => m.Id).ToArray()); _mobs.Clear(); }
        if (world + local == 0) { Reply("no mobs to clear"); return; }
        Reply($"cleared {world} world mob(s) + {local} local dummy(s)");
        Log.Info($"   -> KILL: despawned {world} world + {local} local mobs on map {_char.Map}");
    }

    // A small pack of REAL, killable monsters around the player (via 0x07 = Monster.epf). "@spawn
    // [lookId] [hp]" — lookId is the Monster.tbl monster index (0..326); defaults to 0.
    private void SpawnCritters(CommandArgs a)
    {
        int look = a.Int(0, 0);
        int hp = a.Int(1, 6);
        (int dx, int dy)[] spots = { (0, -2), (2, 0), (-2, 0), (0, 2) };
        foreach (var (dx, dy) in spots)
        {
            ushort x = (ushort)Math.Clamp(_char.X + dx, 0, _char.MapXs - 1);
            ushort y = (ushort)Math.Clamp(_char.Y + dy, 0, _char.MapYs - 1);
            SpawnMonster((ushort)look, x, y, $"monster{look}", hp, dir: 2);
        }
        Log.Info($"   -> SPAWN monster pack look={look}");
    }

    // "@ride" / "@mount [0|1]" — toggle (or set) the mounted-on-horse state. Flips appearance[1] to the
    // form byte 3, which makes the client draw the horse+rider composite (SPR 344/345) instead of the human
    // sprite. Re-draws self and every co-located peer in place (same path ApplyAppearance uses for gear).
    private void ToggleMount(CommandArgs a)
    {
        _char.Mounted = a.Toggle(0, _char.Mounted);
        _char.MountKey = "";                                              // the toggle's mount is always a horse
        RefreshAppearance();                                              // redraw self on the horse + everyone watching
        Reply(_char.Mounted ? "The powerful steed takes you where you want to go."   // same lines as the
                                   : "You precariously step again onto the ground.");       // real 'r' ride key
        Log.Info($"   -> MOUNT {( _char.Mounted ? "on" : "off")}");
    }

    /// <summary>Every staff override this session is under, in one place — the readout half of the
    /// <see cref="GmOverrides"/> gathering (#57 finding 31). Session-scoped overrides first in the order
    /// they are declared, then the two world-wide groups (the mob swing pose and the @wmpos dots), then the
    /// two pins that deliberately live elsewhere and are shown READ-ONLY here: the @clock hour on
    /// <c>World.Clock</c> and the zone weather, read back exactly as @weather reads it.
    ///
    /// <para>Sits next to @clip because that is where the toggles a GM actually hunts for live; the sfx and
    /// mob-pose lines are the ones nothing else would ever show you.</para></summary>
    private void TogglesCmd()
    {
        ReplyList("overrides", new[]
        {
            SettingLine("No-clip", _gm.NoClip),
            SettingLine("Peace", _gm.Peace),
            SettingLine("Any-warp", _gm.WaiveWarpGate),
            SettingLine("Show warps", _gm.ShowWarps),
            $"marker frames {_gm.WarpMarkFrame}/{_gm.DoorMarkFrame}",
            $"swing sfx {_gm.SwingSfx}",
            $"fist sfx {_gm.FistSfx}",
            $"hit sfx {_gm.HitSfx}",
            $"mob swing type {GmOverrides.MobSwingActionType} time {GmOverrides.MobSwingActionTime}",
            $"world-map dots pinned {GmOverrides.WorldDotOverride.Count}",
            $"clock hour {(_world.Clock.HourOverride?.ToString() ?? "real")}",
            $"zone weather {WeatherNames[Math.Min(_world.Weather.Get(_char.Map), (byte)2)]}",
        });
    }

    // "@clip [0|1]" — no-clip, for walking quest routes without fighting the geometry. Session-scoped and
    // always OFF at login (a persisted flag could strand a tester's ordinary character inside a wall on a
    // later login with no staff around). Two layers must move together, because collision is enforced twice:
    // HandleWalk stops refusing the step server-side, and the streamed pass layer goes out as "walkable
    // everywhere" (SendMapRect) so the CLIENT's own local prediction stops refusing it too — without the
    // second half the toggle would be a no-op, since the client never sends a walk it believes is blocked.
    // The re-prime below re-stamps the already-streamed window immediately; every later strip follows suit.
    // Two holes, both accepted: the 5.33 client also collides locally against its own SOBJ.TBL directional
    // flags, which no stream can override without blanking the artwork — so a thin object-wall (hut sides)
    // may still refuse client-side there. And on 4.95 the pass bits ARE the sheet-2 art selector (one word),
    // so blocked terrain draws as the wrong tile while clip is on; it heals the moment clip turns off.
    // Warps deliberately still fire — @clip changes collision, not doorways; a tester walking a quest route
    // still wants portals to carry them. Mob/AI behaviour is untouched: you can share a tile with a mob and
    // it can still hit you.
    private void ClipCmd(CommandArgs a)
    {
        bool was = _gm.NoClip;
        _gm.NoClip = a.Toggle(0, was);
        PrimeViewport("clip");   // re-stamp the visible window so the client's pass layer flips NOW, not next strip
        Reply(SettingLine("No-clip", _gm.NoClip));
        GmOverrides.Log(this, "no-clip", was, _gm.NoClip);
    }

    // "@peace [0|1]" — unprovoked mobs don't notice you: the survey companion to @clip, for walking a
    // hostile map (@showwarps, quest routes) without collecting a train. Session-scoped and always OFF at
    // login, same reasoning as @clip. Switch-on also makes every mob already chasing you let go
    // (World.PacifyPlayer clears target + threat); staying ignored is the aggro-scan exclusions in
    // World.Tick. Deliberately NOT invulnerability: anything you attack re-acquires you through TryDamage
    // and fights back — combat stays testable with the toggle on.
    /// <summary>The name <c>World.MobAiTick</c>'s aggro scans read, unchanged by #57 part 3's gathering:
    /// the field behind it is now <c>_gm.Peace</c>, and this still compiles to a field load (through the
    /// session's readonly <c>_gm</c>) with no lock of its own — the tick reads it under
    /// <c>World._lock</c> exactly as it did before.</summary>
    internal bool PeaceMode => _gm.Peace;
    private void PeaceCmd(CommandArgs a)
    {
        bool was = _gm.Peace;
        _gm.Peace = a.Toggle(0, was);
        if (_gm.Peace) _world.PacifyPlayer(PlayerId);
        Reply(SettingLine("Peace", _gm.Peace));
        GmOverrides.Log(this, "peace", was, _gm.Peace);
    }

    // "@anywarp [0|1]" — use any walk-onto warp or gated doorway regardless of the destination's
    // requirements. Waives every character-requirement refusal a doorway can make: the Maps.csv entry gate
    // (level/vitals/mark/path and the max-level barrier — TryWarpGate), the WarpQuestLocks.csv quest
    // switches, and the scripted doorways with their own entry rules — mythic zodiac caves, event caves,
    // arena side doors and the path-hall class doors (each waive point lives in that doorway's handler,
    // Session.Movement/Navigation). The checks still RUN; a failing one is echoed as "[anywarp] … would
    // have said: <denial>" instead of refusing, so a tester can verify the gate's verdict while being
    // carried through it. The tiered doorways enter tier 1 when waived — an unqualified character gives the
    // depth picker nothing to read, so the shallowest copy is the predictable choice. Session-scoped and
    // OFF at login for the same reason as @clip: a persisted waiver could follow a tester's ordinary
    // character into normal play. Purely server-side, unlike @clip — the client has no idea entry
    // requirements exist, so there is no second half. The quest-ITEM tiles (the Hermit's door, Sute's
    // sealed cave mouth, the lava row) are waived too — while on, each behaves as plain ground or a plain
    // portal, and NOTHING is spent (the powder and the shoes are kept): the command exists to test the map
    // behind a gate, not the gate's economy, so the mechanic is only narrated, never run.
    private void AnyWarpCmd(CommandArgs a)
    {
        bool was = _gm.WaiveWarpGate;
        _gm.WaiveWarpGate = a.Toggle(0, was);
        Reply(SettingLine("Any-warp", _gm.WaiveWarpGate));
        GmOverrides.Log(this, "any-warp", was, _gm.WaiveWarpGate);
    }

    // "@showwarps [0|1]" — overlay a marker on every warp and gated doorway of the current map, visible to
    // THIS session only. Rides the spot-traps marker machinery (Session.WorldApi.SyncGroundItems): each
    // marker is a synthetic GroundItem — ItemId -1 like a coin pile, so it is no registry item, has no name,
    // and can never be picked up (pickup reads the WORLD's floor list, which never holds these) — drawn and
    // hidden by the same viewport reconcile as real floor items, so markers beyond the view rect appear as
    // you walk toward them. Ground items occupy no tile: a marked warp still fires when stepped on.
    // Follows you across maps (EnterMap re-stamps the overlay) until toggled off; session-scoped and OFF at
    // login like @clip/@anywarp. Toggling on also prints the map's doorway list with destinations, with
    // quest-locked warps flagged.
    //
    // "@showwarps look [warpFrame] [doorFrame]" — tune the marker sprites live (re-stamps at once). FRAMES,
    // not colours: 4.95's item graphics path has NO colour channel anywhere — the draw takes only a frame
    // index and pulls the palette from Item.tbl, so a colour variant IS a separate frame (ItemDef.ClientIcon
    // remarks; a first cut of this command sent the 0x07 colour byte and it was silently ignored). So "make
    // it blue" means "find a natively blue frame": run "@icons <start>" to page the frame space in the bag,
    // then "look <frame>" what you found onto the floor — and trust the IN-GAME sweep over a rendered
    // contact sheet: sheet labels don't reliably line up with wire ids and already mis-picked one default.
    // Both kinds default to frame 877, a blue pinwheel confirmed in-game; one shape for both was the
    // operator's call ("doors are basically warps"), and the two-argument form still splits them for anyone
    // who wants warp and doorway told apart. Frames are per-CLIENT art: 877 exists on both shipped clients,
    // but anything found on a 5.33 sheet past 1310 simply does not exist in 4.95's Item.epf.
    private void ShowWarpsCmd(CommandArgs a)
    {
        if (a.Is(0, "look"))
        {
            // Per-version bound: 4.95's Item.epf has 1310 frames, 5.33's 2304 (counted from the shipped
            // Misc.dat) — an id past the client's own count draws blank, so clamp to the session's client.
            int maxId = _ver == ClientVersion.V533 ? 2303 : 1310;
            string wasFrames = MarkerFrames();
            if (a.Int(1, out var warpFrame)) _gm.WarpMarkFrame = (ushort)Math.Clamp(warpFrame, 0, maxId);
            if (a.Int(2, out var doorFrame)) _gm.DoorMarkFrame = (ushort)Math.Clamp(doorFrame, 0, maxId);
            GmOverrides.Log(this, "marker frames", wasFrames, MarkerFrames());
            Reply($"Marker look: warp frame {_gm.WarpMarkFrame}, doorway frame {_gm.DoorMarkFrame} " +
                    $"(find frames with {Prefix}icons <start>; ids run 0..{maxId} on this client).");
            // Say what the re-stamp actually painted: "look <n> did nothing" has already been reported once
            // when every marker in view was the OTHER kind and the changed frame had nothing to redraw.
            if (_gm.ShowWarps)
            {
                var (w, d) = StampWarpMarkers();
                Reply($"Re-stamped {w} warp + {d} doorway marker(s) on this map.");
            }
            return;
        }

        bool was = _gm.ShowWarps;
        _gm.ShowWarps = a.Toggle(0, was);
        Reply(SettingLine("Show warps", _gm.ShowWarps));
        if (_gm.ShowWarps) StampWarpMarkers(list: true);
        else ClearWarpMarkers();
        GmOverrides.Log(this, "show warps", was, _gm.ShowWarps);
    }

    /// <summary>The two @showwarps marker frames as one value, so the override log line can say what the
    /// pair was and what it became in one breath (the "look" form may set either or both).</summary>
    private string MarkerFrames() => $"warp {_gm.WarpMarkFrame} door {_gm.DoorMarkFrame}";

    /// <summary>Stamp the @showwarps overlay for the CURRENT map (replacing any previous overlay). Doorway
    /// sources: Warps.csv, plus every scripted walk-onto doorway with a tile index — mythic zodiac caves,
    /// event caves, arena side doors, path-hall doors, and the Forever Tree crevasse (whose tile is a
    /// literal in TryForeverTreeEntrance, mirrored here). The world-map travel edges are deliberately NOT
    /// marked: they span whole map borders, and a border of diamonds is noise, not signal.</summary>
    private (int warps, int doors) StampWarpMarkers(bool list = false)
    {
        ClearWarpMarkers();
        ushort map = _char.Map;
        var marks = new List<GroundItem>();
        var lines = new List<string>();

        void Mark(ushort x, ushort y, ushort frame) =>
            marks.Add(new GroundItem { Id = _world.AllocateItemId(), ItemId = -1, X = x, Y = y, Graphic = frame });

        foreach (var (from, to) in Content.Warps.Where(w => w.Key.m == map)
                                                .OrderBy(w => w.Key.y).ThenBy(w => w.Key.x))
        {
            Mark(from.x, from.y, _gm.WarpMarkFrame);
            string dest = Content.TryMap(to.m, out var dm) ? dm.Name : $"map {to.m}";
            lines.Add($"  ({from.x},{from.y}) -> {dest} ({to.x},{to.y})" +
                      (Content.WarpQuestLocks.ContainsKey((map, to.m)) ? "  [quest-locked]" : ""));
        }
        int warpCount = marks.Count;   // everything added past here is a scripted doorway

        void Door(ushort x, ushort y, string what) { Mark(x, y, _gm.DoorMarkFrame); lines.Add($"  ({x},{y}) {what}"); }

        foreach (var (k, cave) in Content.MythicCaveTiles) if (k.Map == map) Door(k.X, k.Y, $"mythic {cave.Animal} cave (tiered)");
        foreach (var (k, cave) in Content.EventCaveTiles)  if (k.Map == map) Door(k.X, k.Y, $"event cave '{cave.Key}' (tiered)");
        foreach (var (k, door) in Content.ArenaDoorTiles)  if (k.Map == map) Door(k.X, k.Y, $"arena door '{door.Label}'");
        if (Content.PathHalls.ContainsKey(map))
        {
            Door(1, 23, "path hall guild door");   Door(2, 23, "path hall guild door");
            Door(8, 1, "path hall sanctum door");  Door(9, 1, "path hall sanctum door");
        }
        if (map == 1002) Door(19, 91, "Forever Tree crevasse");

        using (EnterView()) _warpMarkers.AddRange(marks);
        // The markers are this session's own sweep subjects and the map itself did not change, so the tick's
        // sweep skip has nothing to notice. The sweep on the next line draws the in-view ones now; the flag
        // is for the beat after it. See Session.MarkSweepPending.
        MarkSweepPending();
        SyncGroundItems(_world.ItemsOn(map));   // draw the in-view markers now; the rest appear as you walk

        var counts = (warps: warpCount, doors: marks.Count - warpCount);
        if (!list) return counts;
        if (marks.Count == 0) { Reply("No warps or scripted doorways on this map."); return counts; }
        const int Cap = 18;   // a screenful; past it the markers themselves are the better map
        ReplyList($"doorways ({counts.warps} warp, {counts.doors} scripted)", lines.Take(Cap));
        if (lines.Count > Cap) Reply($"...and {lines.Count - Cap} more; the markers show them all.");
        return counts;
    }

    /// <summary>Take down the @showwarps overlay: despawn every marker this client actually drew (the
    /// viewport gate means most were never sent) and forget the set. Same shape as ClearTrapMarker.</summary>
    private void ClearWarpMarkers()
    {
        var gone = new List<uint>();
        using (EnterView())
        {
            foreach (var m in _warpMarkers) if (_shownItems.Remove(m.Id)) gone.Add(m.Id);
            _warpMarkers.Clear();
        }
        if (gone.Count > 0) SendDespawn(gone.ToArray());
    }

    // The 'r' Ride key (HandleSetting case 0x00): a real RTK-shaped find-a-horse mount, distinct from the
    // @ride/@mount GM toggle above. Mounting requires an actual "horse" mob (MobDef key "horse" — the plain
    // wild horse wandering Buya/Horse Valley, not a combat mob like "wild_horse"/"horse_guardsman" that just
    // shares the word) standing on the SINGLE tile you're facing (cardinal only, same FrontTile() the melee
    // attack uses — RTK has no 8-way/diagonal reach and neither does the player's own swing) and despawns it
    // (ridden away, no loot/exp — see World.DespawnMob). Dismounting sets a fresh horse back down on the
    // first free tile clockwise from the one you face (see DismountTile).
    // ARAWN'S STAG RIDES TOO (Druid's Steed): RTK's onMount takes either a `horse` or an `arawns_stag`
    // (RTK Scripts/on_event.lua:353-377), and its onDismount sets down again the creature that was ridden, by the
    // mount's id it kept in its saved registry (:379-465). So the creature ridden is kept with the character
    // (Character.MountKey, saved: the project owner, 2026-10-31, "As RTK") and set down at the dismount, after a relog too.
    // On 4.95 a rider on either is drawn on a horse: the mounted form is one horse-and-rider frame of the worn body
    // art, with no second mount to draw (RTK draws the stag's rider as look 126).
    private void TryRideHorse()
    {
        if (!_char.Mounted)
        {
            var (hx, hy) = FrontTile();
            var horse = _world.MobNear(_char.Map, hx, hy, 0, mo => mo.Key is "horse" or "arawns_stag");   // radius 0 = exact tile
            // The three ride lines are the real game's, verbatim (they aren't in the client's Inter.dat line
            // table, so they were always server-sent — ours were stand-ins). This is the 'r' KEY, not a
            // command, so it sends its own status-pane lines rather than going through Reply.
            if (horse is null) { SendMiniText("Good try, but there is nothing here that you can ride."); return; }
            _world.DespawnMob(_char.Map, horse);
            _char.Mounted = true;
            _char.MountKey = horse.Key;
            RefreshAppearance();
            SendMiniText("The powerful steed takes you where you want to go.");
            Log.Info($"   -> MOUNT on (rode away world {horse.Key} {horse.Id})");
        }
        else
        {
            _char.Mounted = false;
            RefreshAppearance();
            SendMiniText("You precariously step again onto the ground.");

            string key = _char.MountKey is { Length: > 0 } ridden ? ridden : "horse";   // empty: a horse (the GM toggle, an older row)
            _char.MountKey = "";
            var def = Content.Mobs.FirstOrDefault(m => m.Key == key);
            if (def is not null)
            {
                var (x, y, dir) = DismountTile();
                SummonWorldMob(def.Look, x, y, def.Name, def.Hp, dir: dir,
                                color: def.Color, exp: def.Exp, moveTime: def.MoveTime, key: def.Key, def: def);
                Log.Info($"   -> MOUNT off (set {key} down at {x},{y} dir {dir})");
            }
            else Log.Info($"   -> MOUNT off (no '{key}' MobDef — nothing set down)");
        }
    }

    /// <summary>Where to set the horse down when dismounting: the first free CARDINAL neighbour, checked
    /// CLOCKWISE from the tile the player faces (faced, right, behind, left — dir, dir+1, dir+2, dir+3 in
    /// the 0=N 1=E 2=S 3=W encoding, which is already clockwise); if all four are taken, the player's OWN
    /// tile. Returns the tile plus the direction the horse should face (always back toward the rider).
    /// Only 4 slots: this game has no diagonal adjacency anywhere — movement, melee reach and mount range
    /// are all cardinal — so a horse must never land on a corner tile.
    /// Free = in bounds, not blocked (<see cref="MapData.BlockedMove"/> — ground pass flag AND the SObj
    /// directional object-wall, the same two-layer test the player's walk uses), and not already holding a
    /// mob or another player.
    /// The old code just clamped the faced tile to the map bounds and dropped the horse there, which put it
    /// inside walls, in water, and on top of whatever already stood in front of you.
    /// Stacking on the rider is the deliberate last resort (same principle as World.FreeSpawnTile's
    /// accept-the-overlap fallback): a boxed-in player must still get their horse back.
    /// <para>The walk, the bounds test and the take-the-first-survivor loop are
    /// <c>MapData.FreeNeighbour</c>'s, shared with the spawn fallback (#57 finding 31). The two
    /// predicates and the fallback below are this path's own and are unchanged — they ride in as a STRUCT so
    /// the search allocates nothing (see the comment over MapData.FreeNeighbour).</para></summary>
    private (ushort x, ushort y, byte dir) DismountTile()
    {
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        var free = MapData.FreeNeighbour(
            MapData.CardinalWalk(_char.X, _char.Y, _facing), _char.MapXs, _char.MapYs,
            new DismountTest(this, md));

        return free is { } t ? ((ushort)t.x, (ushort)t.y, (byte)Opposite(t.side))   // face back toward the rider
                             : (_char.X, _char.Y, (byte)Opposite(_facing));         // fully boxed in — stack it on us
    }

    /// <summary>The dismount's own two tests, exactly as the inline loop wrote them: the two-layer
    /// <see cref="MapData.BlockedMove"/> (ground pass AND the directional object wall, which is why the walk
    /// carries the side), and a tile holding a mob OR a peer. A readonly struct rather than a pair of lambdas
    /// because <see cref="MapData.FreeNeighbour{TWalk, TTest}"/> takes the tests by generic type and so
    /// allocates nothing per call.</summary>
    private readonly struct DismountTest(Session s, MapData? md) : MapData.ITileTest
    {
        public bool Blocked(int x, int y, int side) => md is not null && md.BlockedMove(x, y, side);
        public bool Occupied(int x, int y) =>
            s.TileHasMob(x, y) || s._world.PeerAt(s._char.Map, x, y) is not null;
    }

    // "@might N" / "@will N" / "@grace N" — set one BASE character stat so wear-requirements can be exercised
    // on the fabricated bring-up character. The base stats are bytes, so they clamp to 0-255. A later @lvl/
    // @class/@mark/@align recomputes the stats from the class curve and discards whatever was set here — set
    // the class/level first, then poke individual stats. (@stats sets all three plus the pools in one shot.)
    private void SetBaseStat(string which, CommandArgs a)
    {
        byte b = (byte)Math.Clamp(a.Int(0, 0), 0, 255);
        switch (which)
        {
            case "might": _char.Might = b; break;
            case "will":  _char.Will  = b; break;
            case "grace": _char.Grace = b; break;
        }
        if (_enteredWorld) StoreSave();
        SendStats();
        byte now = which switch { "will" => _char.Will, "grace" => _char.Grace, _ => _char.Might };
        Reply($"{which} set to {now}");
        Log.Info($"   -> {which.ToUpperInvariant()} set to {now}");
    }

    // "@hp <n>" / "@mp <n>" — set the BASE max pool (vita/mana) and top the current value up to the new max.
    // The individual-stat counterpart to @stats' first two arguments; the reply shows the effective max after
    // gear/buffs. Like @might/@will/@grace, a later @lvl/@class/@mark recomputes vitals from the curve and
    // discards this — set the class/level first, then the pools.
    private void SetMaxPool(bool hp, CommandArgs a)
    {
        if (!a.Int(0, out var n)) { Refuse(a.Usage()); return; }
        if (hp) { _char.MaxHp = (uint)Math.Max(1, n); _char.Hp = EffMaxHp; }
        else    { _char.MaxMp = (uint)Math.Max(0, n); _char.Mp = EffMaxMp; }
        if (_enteredWorld) StoreSave();
        SendStats();
        Reply(hp ? $"max HP set to {_char.MaxHp:N0}{(EffMaxHp != _char.MaxHp ? $" ({EffMaxHp:N0} with gear)" : "")}, HP refilled."
                       : $"max MP set to {_char.MaxMp:N0}{(EffMaxMp != _char.MaxMp ? $" ({EffMaxMp:N0} with gear)" : "")}, MP refilled.");
        Log.Info($"   -> {(hp ? "MAXHP" : "MAXMP")} set to {(hp ? _char.MaxHp : _char.MaxMp)}");
    }

    // "@nation <id>" / "@totem <id>" — set the character's kingdom / totem crest and PERSIST it (survives
    // relog), then push the HUD. Distinct from the GM @nat / @totemsweep RE probes, which only flash a crest
    // at the HUD for a single packet without touching the saved character.
    private void SetNationCmd(CommandArgs a)
    {
        // The current value is live state, not a usage string: the shape comes from the table, the number
        // from the character.
        if (!a.Int(0, out var id))
        { Refuse($"{a.Usage()}   (now: {_char.Nation} — {Character.NationName(_char.Nation)})"); return; }
        // 0..7 only: the crest table the @nat sweep mapped (Character.Nations). What the 4.95 HUD draws past it
        // was never observed. A login keeps a saved 0..7 and re-derives only a nation past the table
        // (CharacterFactory.RestoreUnusableFromCreation), so what this stores is what the character keeps
        // (PR #337 review F1).
        _char.Nation = (byte)Math.Clamp(id, 0, Character.Nations.Length - 1);
        if (_enteredWorld) StoreSave();
        SendStats();
        Reply($"nation set to {_char.Nation} ({Character.NationName(_char.Nation)}).");
        Log.Info($"   -> NATION set to {_char.Nation}");
    }

    private void SetTotemCmd(CommandArgs a)
    {
        if (!a.Int(0, out var id)) { Refuse($"{a.Usage()}   (now: {_char.Totem})"); return; }
        _char.Totem = (byte)Math.Clamp(id, 0, 3);   // 0..3 only — 5.33 clamps out-of-range and then reports a phantom change every stats packet (pane wipe); see TotemWire
        if (_enteredWorld) StoreSave();
        SendStats();
        Reply($"totem set to {_char.Totem}.");
        Log.Info($"   -> TOTEM set to {_char.Totem}");
    }

    // "@karma <value|tier>" — set the hidden virtue score outright. A number sets it exactly (may be negative
    // or fractional); a tier NAME (cat, dog, angel, …) snaps it into that band, using the same ladder every
    // gate reads (see Karma.ValueForName). Persisted like the other character setters. Note karma is NOT on
    // the 4.95 profile (Karma.cs remarks), so this reply is the only feedback — and a later @lvl/@class/@mark
    // rebuild leaves karma alone, unlike the stat curve, so it doesn't get discarded.
    private void SetKarmaCmd(CommandArgs a)
    {
        string arg = a.Raw;
        if (arg.Length == 0)
        {
            Reply($"karma is {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)}).");
            Reply($"{a.Usage()}   tiers: {string.Join(" · ", Karma.TierNames)}");
            return;
        }

        // A tier name wins over number parsing, but no tier name is a number, so there's no ambiguity.
        // NumberStyles.Float also accepts "NaN", "Infinity" and "-Infinity" — System.Text.Json refuses to
        // serialize a non-finite double, so letting one through here would leave the character unsaveable
        // (every later sweep/FlushNow/logout save throws) until a GM set karma again. Refused the same way
        // as an unparseable argument.
        double value;
        if (Karma.ValueForName(arg) is { } byName) value = byName;
        else if (double.TryParse(arg, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var byNum)
                 && double.IsFinite(byNum)) value = byNum;
        else { Refuse($"'{arg}' isn't a number or a karma tier. Tiers: {string.Join(" · ", Karma.TierNames)}"); return; }

        _char.Karma = value;
        if (_enteredWorld) StoreSave();
        SendEffect(_char.Id, Karma.Effect);        // the same sparkle a real karma change plays
        Reply($"karma set to {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)}).");
        Log.Info($"   -> KARMA set to {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)}) by '{_char.Name}'");
    }

    // "@dispel" — strip every buff and debuff currently on you (see DispelSelf). Handy for resetting a test
    // character to a clean baseline between casts, or shaking off a curse/hold applied during a fight.
    private void DispelCmd()
    {
        DispelSelf();
        Reply("All buffs and debuffs removed.");
    }

    // "@die" — lay yourself out exactly as a mob's killing blow would: ghost form plus the real death
    // penalties, since a tester is an ordinary player as far as the world is concerned. Revive with @rez.
    // Reuses the poison-apple lethal path (ItemKill: HP -> 0, push HUD, run Die()).
    private void DieCmd()
    {
        if (IsDead) { Refuse($"You're already down — {Prefix}rez to get back up."); return; }
        ItemKill();
        Log.Info($"   -> @die by '{_char.Name}' on map {_char.Map}");
    }

    // "@carnage <name> [n]" — record carnage victories on an online player (default 1; a negative n takes
    // them away, and the tally never goes below zero). Carnage was a GM-hosted PvP event, so who won it is
    // knowledge only the host has: there is no fight for the server to score. Warrior Sun armor's first step
    // wants two wins (nexusatlas + the tutor guide), and this is what feeds it. Self is allowed by naming
    // yourself, deliberately — an unnamed form would make the most common misuse the easiest one to type.
    private void CarnageWinCmd(CommandArgs a)
    {
        // "name = n" and "name n" both work; the '=' form is here because names can contain spaces.
        string arg = a.Raw, name = arg;
        int add = 1;
        int eq = arg.LastIndexOf('=');
        if (eq >= 0)
        {
            name = arg[..eq].Trim();
            if (!int.TryParse(arg[(eq + 1)..].Trim(), out add)) { Refuse(a.Usage()); return; }
        }
        else if (a.NameThenTrailingInt(out var named, out var n)) { name = named; add = n; }

        if (name.Length == 0) { Refuse(a.Usage()); return; }
        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;

        // Read, write, tell — one critical section on the TARGET (#29 rule 2, Server/Session.State.cs). The
        // read-modify-write really is one here: `now` is their carnage counter plus `add`, and the registry read
        // is a bare `_char.Quests` lookup with no monitor (QuestState.ISessionStore.Read, Session.CharacterApi.cs)
        // made from the operator's thread, so two GMs scoring the same player could both have read the old tally
        // and the second write would lose the first. The registry write takes the monitor for itself
        // (QuestState.ISessionStore.Write, the same file) and is now the re-entrant case (rule 3). Their NAME is
        // read in here too, for the same reason — it is their state, and @ckm parks a marker string in
        // _char.Name for the length of one packet. The operator's own confirmation stays OUTSIDE, so we are not
        // holding a peer's monitor while sending to ourselves.
        // Rule 1 holds: FindPlayer takes and releases World._lock inside itself (World.OnlineRegistry.cs:45-53).
        int now = 0;
        string them = "";
        target.WithState(() =>
        {
            var registry = target.Quest(QuestState.Registry);
            now = Math.Max(0, registry.Get(ArmorQuest.CarnageWinsReg) + add);
            registry.Set(ArmorQuest.CarnageWinsReg, now);
            them = target._char.Name;
            // Addressed to the TARGET, so it is not a reply to the operator and does not go through Reply —
            // see the channel rule in Commands.cs. The operator's own confirmation is the next line.
            target.SendMiniText(add >= 0 ? "Your victory in the Carnage is recorded."
                                         : "Your Carnage record has been amended.");
        });
        Reply($"{them}: {now} carnage victory(ies).");
        Log.Info($"   -> @carnage '{them}' {add:+#;-#;0} -> {now}");
    }

    // "@approach <username>" — teleport to an online player: their map, on a free tile beside them (their own
    // tile if they're boxed in). EnterMap is the only reliable self-relocate on 4.95 (a bare 0x04 snaps back —
    // see GoCmd), so this jumps the same way the world-map/leap paths do.
    private void ApproachCmd(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0) { Refuse(a.Usage()); return; }
        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;
        if (ReferenceEquals(target, this)) { Refuse("You're already right here."); return; }

        // The target's map, its size and their tile, read whole inside the target's monitor (PlaceForPeer, which the
        // Approach spell shares): read bare, a target mid-warp could give the new map with the old tile.
        var (map, xs, ys, tx, ty) = target.PlaceForPeer();
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        // The free-tile search used to happen here, in ApproachTile, through a World.PeerAt and a World.MobAt
        // that each took and released the world lock — and the tile it picked was then written by EnterMap
        // under no lock at all. It is now ArrivalPolicy.AdjacentFreeElseStack, which runs the same search and
        // the write in one acquisition (#99 part 1). Same predicates, same N/E/S/W order, same stack-on-them
        // fallback when they are boxed in.
        // Our own move, on our own read loop: EnterMap refuses only a player who is logging out (#330), which we
        // cannot be here, so the null arm is never taken.
        if (EnterMap(map, xs, ys, tx, ty, mapName, ArrivalPolicy.AdjacentFreeElseStack) is not (var x, var y)) return;
        Reply($"Approached {target._char.Name} on {mapName} at ({_char.X},{_char.Y}).");
        Log.Info($"   -> @approach '{_char.Name}' -> '{target._char.Name}' at map {map} ({x},{y})");
    }

    // "@where [username]" — the read-only half of @approach: report where a player is without going there.
    // Bare @where lists everyone online with their location — the ops "who's where" view the client's own
    // 0x36 user list doesn't give (it shows names, not places).
    private void WhereCmd(CommandArgs a)
    {
        static string Line(Session p)
        {
            string mapName = Content.TryMap(p._char.Map, out var md) ? md.Name : "Nexus";
            return $"{p._char.Name} — {mapName} (map {p._char.Map}) at ({p._char.X},{p._char.Y})";
        }

        string name = a.Raw;
        if (name.Length == 0)
        {
            var all = _world.Online.All().OrderBy(p => p._char.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ReplyList($"online ({all.Count})", all.Select(Line));
            return;
        }
        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;
        Reply(Line(target));
    }

    // "@bring <username>" — the inverse of @approach: pull an online player to a free tile beside YOU (your
    // own tile if you're boxed in), via the same EnterMap jump run on the TARGET's session. Also the rescue
    // for someone wedged in geometry. The player is told who moved them, so it doesn't read as a bug.
    private void BringCmd(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0) { Refuse(a.Usage()); return; }
        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;
        if (ReferenceEquals(target, this)) { Refuse("You're already right here."); return; }

        ushort map = _char.Map, xs = _char.MapXs, ys = _char.MapYs;
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        // Same move as @approach, with the roles swapped: the anchor is US, the mover is THEM. See the note
        // there on why the search is a policy now.
        if (target.EnterMap(map, xs, ys, _char.X, _char.Y, mapName,
                            ArrivalPolicy.AdjacentFreeElseStack) is not (var x, var y))
        {
            // #330: they logged out after the lookup above, before the move or while its exchange step waited, and
            // EnterMap left them off every map. The lookup's own line: had the command come a moment later, that
            // is what it would have said. Nothing is sent to them.
            Refuse($"'{name}' isn't online.");
            Log.Info($"   -> @bring '{_char.Name}' <- '{name}': logged out before the move — nobody moved");
            return;
        }
        // The move itself was already guarded: EnterMap opens with `using var _ = EnterState()`
        // (Session.Navigation.cs:1375), so every write the summon makes on them happens under their monitor.
        // What was not guarded is this line and the two reads of their NAME below — #29 rule 2, so they get a
        // section of their own here rather than a wider one around EnterMap, which would change nothing about
        // the writes and would hold the monitor across World._lock for longer than the move needs it. Rule 1
        // holds: FindPlayer released World._lock before returning (World.OnlineRegistry.cs:45-53) and EnterMap
        // has released it again by the time it returns. The operator's own Reply stays outside, so no peer
        // monitor is held while we send to ourselves.
        string them = "";
        target.WithState(() =>
        {
            target.SendMiniText($"You have been summoned by {_char.Name}.");   // to the TARGET, not a Reply
            them = target._char.Name;
        });
        Reply($"Brought {them} to ({x},{y}).");
        Log.Info($"   -> @bring '{_char.Name}' <- '{them}' to map {map} ({x},{y})");
    }

    // "@announce <message>" — say something to every player online on the same 0x0A system channel the
    // restart countdown uses. Deliberately NOT prefixed with the GM's name: this is the server speaking
    // (event notices, "carnage starts in five minutes"), and staff who want to speak as themselves have
    // ordinary chat. The per-session try/catch mirrors RestartSchedule.Announce — one dead socket must not
    // stop the message reaching everyone else.
    //
    // Each recipient's line is built inside THEIR monitor (#29 rule 2, Server/Session.State.cs:25-33), taken by
    // SystemAnnounce at its own definition (Session.Dialog.cs) rather than here, because the restart ladder is
    // the other caller of exactly this loop and both are cross-session — the NotifyGroup shape. One peer monitor
    // at a time: SystemAnnounce disposes its guard before this loop moves on, so two are never held at once and
    // rule 2 resolves each acquisition on its own, ascending or descending. The operator is in Online.All() too,
    // and their own copy is rule 3's re-entrant no-op, so it still goes out in roster order. Rule 1 holds:
    // Online.All() (World.OnlineRegistry.cs:77-81) snapshots under World._lock and returns with it released, and
    // the Reply below sits outside every peer's section.
    private void AnnounceCmd(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        int heard = 0;
        foreach (var s in _world.Online.All())
        {
            try { s.SystemAnnounce(a.Raw); heard++; }
            catch (Exception e) { Log.Error($"@announce to {s.Remote} threw — the others still hear it", e); }
        }
        Reply($"Announced to {heard} player(s).");
        Log.Info($"   -> @announce '{_char.Name}': \"{a.Raw}\"");
    }

    // ApproachTile lived here: the first free CARDINAL neighbour of the target (N/E/S/W), else the target's
    // own tile. It is World.AdjacentFreeLocked now, reached through ArrivalPolicy.AdjacentFreeElseStack, so
    // that the search and the position write it feeds share one acquisition of the world lock (#99 part 1).
    // The predicates did not change; where they run did.

    // "@mark <0-3>" — set the subpath rank (RTK status.mark: 0 base · 1 Il san · 2 Ee san · 3 Sam san) and
    // rebuild the character at it. A rank is earned at level 99, so this FORCES level 99 first: "@lvl 99" is
    // the base class, "@mark 1" is Il san at the same level, and so on. A rank adds NO stats — the rebuild
    // gives exactly @lvl 99's vita, mana, Might/Grace/Will and AC (see RespecTo), as Bon-Hwa's promotion
    // gives none — and brings its own secrets; the base 1-99 book is unchanged underneath.
    //
    // Stops at Sam san — see Content.MaxMark: Spells.csv has no mark-4 or mark-5 rows, so Sa san and Oh san
    // would be a title over nothing.
    //
    // Bon-Hwa's Il San trials (Server/BonHwa.cs) now earn mark 1 in play; nothing earns Ee or Sam san yet, so
    // for those this is still the only way to satisfy the other gates that read the mark: mark-restricted gear
    // (ItmMark), map entry (MapReqMark), unmarked-only doors, and minor-quest eligibility.
    private void SetMark(CommandArgs a)
    {
        // Name the ranks of the character's OWN path, not the generic Il san ladder — a Ju jak's ranks are
        // Force / Inferno / Pandemonium, and telling them otherwise is just wrong.
        int p = Math.Max(0, CharClassId);
        string ladder = string.Join(" · ", Enumerable.Range(1, Content.MaxMark).Select(m => $"{m} {Content.PathTitle(p, m)}"));

        // A missing OR non-numeric argument is the readout, so "@mark" and "@mark soon" both report rather
        // than rebuilding the character at rank 0.
        if (!a.Int(0, out var want))
        {
            Reply($"mark is {_char.Mark} ({ClassTitle}). {a.Usage()} — {ladder}, each at level 99 with no added stats.");
            return;
        }
        // Refuse rather than clamp: silently turning "@mark 5" into Sam san would read as a working Oh san.
        if (want > Content.MaxMark)
        {
            Refuse($"{Content.PathTitle(p, Content.MaxMark)} (mark {Content.MaxMark}) is as far as the ranks go — " +
                    $"there are no mark-{Content.MaxMark + 1} spells in the game data yet. Ranks: {ladder}.");
            return;
        }
        RespecTo(99, Math.Max(0, want));
    }

    // "@dog [0|1]" — skip the bark/woof/grrowl chain and hand over (or take back) the Dog Linguist standing.
    // The flag ALONE grants no spells: the Dog itself still teaches those for kills and goods, so a GM testing
    // the teach flow sets the flag, walks to the Dog, and starts where a finished linguist starts.
    //
    // What the flag DOES change is the character rebuild. @lvl / @class / @mark / @align rebuild the book from
    // the entitlement set (Content.RespecSpellSet), and a finished linguist IS entitled to its class's Dog
    // spells at 70 and 99 — so "@dog 1" then "@lvl 99" hands them over, and "@dog 0" then a rebuild takes them
    // back. Before this the rebuild had no idea the Dog set existed and silently forgot every one of them,
    // including spells earned honestly at the Dog.
    //
    // Eligibility is base classes + NPC subpaths only (Content.CanLearnDogSpells), checked by the Dog and by
    // the rebuild alike — said here too, because a PC subpath can hold the legend and still get nothing.
    // "@rez [username]" (tester/GM): bring a target player — or yourself, if no name is given — back to life at
    // full HP/MP. ReviveInPlace drops the ghost form, refills both bars and pushes the HUD — harmless on a
    // living character (just a full heal), so no dead-only guard.
    private void RezCmd(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0)
        {
            ReviveInPlace(IsDead ? "You have been restored to life." : "You are restored to full health.");
            return;
        }
        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;
        if (!target.ReviveInPlace(target.IsDead ? "You have been restored to life." : "You are restored to full health."))
        {
            // They logged out after the lookup above (#330's class, PR #358 review R5): nobody was revived. The
            // lookup's own line, as @bring answers the same race.
            Refuse($"'{name}' isn't online.");
            return;
        }
        Reply($"Restored {target._char.Name} to full health.");
        Log.Info($"   -> @rez '{_char.Name}' -> '{target._char.Name}'");
    }

    private void SetDogFlag(CommandArgs a)
    {
        int p = Math.Max(0, CharClassId);
        bool want = a.Toggle(0, HasDogFlag);      // bare "@dog" toggles

        var registry = Quest(QuestState.Registry);
        registry.Set(Content.DogFlagReg, want ? 1 : 0);
        registry.Set(DogChainReg, want ? DogChainDone : 0);
        if (want) AddLegend($"Dog linguist ({Character.GameDate})", DogChainReg, 3, 128);
        else RemoveLegend(DogChainReg);

        Reply(want
            ? $"Dog Linguist granted — say \"secret\" to your class's Dog to be taught, or {Prefix}lvl " +
              $"{_char.Level} to have the rebuild hand over the Dog spells you qualify for (70 and 99)." +
              (Content.CanLearnDogSpells(p)
                  ? ""
                  : $" NOTE: {Content.PathTitle(p, _char.Mark)} is a PC subpath and will be refused — only the four " +
                    $"base classes and the NPC subpaths (Chung ryong · Baekho · Ju jak · Hyun moo) may learn Dog spells.")
            : $"Dog Linguist cleared; the chain starts over at Mutt. ({Prefix}lvl {_char.Level} to drop the " +
              $"Dog spells from the book.)");
    }

    // "@sage [0-5]" — set the Share Wisdom rung outright, skipping the Sage's price and his 90-day wait.
    //
    // It exists because the ladder is otherwise UNREACHABLE from the staff tooling and slow by design: the
    // spells are gated to one NPC (Content.IsNpcGrantedOnly), so no @lvl/@class rebuild will ever hand one
    // over on its own, and buying the ladder honestly is 500,000 gold and 360 real days of waiting. A tester
    // who wants to see what rung 3 reaches cannot get there any other way.
    //
    // Sets BOTH halves, because they answer different questions: the spell in the book is what you can cast
    // now, and Content.SageRungReg is what a rebuild hands back afterwards. Setting only the book would mean
    // "@sage 5" followed by "@lvl 99" silently undid itself, which is the exact trap this command was asked
    // for after. Also clears the wait, so the Sage himself will sell the next rung immediately — the point is
    // to test the flow, not to sit out a quarter of a year.
    private void SetSageRung(CommandArgs a)
    {
        int held = Content.SageLadder.Select(Content.SpellByKey)
                          .Select((sp, i) => sp is not null && KnowsSpellId(sp.Id) ? i + 1 : 0)
                          .DefaultIfEmpty(0).Max();
        // A missing OR non-numeric rung reports, like bare "@dog" toggles: the read is the common case.
        var registry = Quest(QuestState.Registry);
        if (!a.Int(0, out var asked))
        {
            long left = registry.Get(Content.SageTimerReg) - NowUnix;
            string name = Content.SageSpellForRung(held) is { } k && Content.SpellByKey(k) is { } s ? s.Name : "none";
            Reply($"Sage rung {held}/{Content.SageLadder.Length} ({name})" +
                    $"; paid-for rung on record: {registry.Get(Content.SageRungReg)}" +
                    (left > 0 ? $"; next upgrade in {left / 86400}d {left % 86400 / 3600}h." : "; no wait outstanding.") +
                    $"  {a.Usage()} to set it.");
            return;
        }

        int rung = Math.Clamp(asked, 0, Content.SageLadder.Length);

        // One rung at a time, as the ladder itself works — every other rung comes out of the book first.
        foreach (var key in Content.SageLadder)
            if (Content.SpellByKey(key) is { } sp && KnowsSpellId(sp.Id) && Content.SageRungOf(key) != rung)
                ForgetOneSpell(sp.Id);

        registry.Set(Content.SageRungReg, rung);
        registry.Set(Content.SageTimerReg, 0);

        if (rung == 0)
        {
            Reply($"Sage ladder cleared. The Sage will start you again at {Content.SageLadder[0]} " +
                    $"(map 1230, from the Wilderness at 126,7).");
            Log.Info($"   -> @sage '{_char.Name}' -> rung 0 (cleared)");
            return;
        }

        var want = Content.SpellByKey(Content.SageSpellForRung(rung)!)!;
        if (!KnowsSpellId(want.Id) && !LearnSpellFromNpc(want))
        {
            Refuse($"Your spellbook is full — free a slot and run {Prefix}sage {rung} again.");
            return;
        }

        Reply($"Sage rung {rung}/{Content.SageLadder.Length}: {want.Name}, and the upgrade wait is cleared." +
                (_char.Level < Content.SageLevel
                    ? $"  NOTE: level {_char.Level} is below the Sage's {Content.SageLevel}, so a rebuild " +
                      $"({Prefix}lvl/{Prefix}class/{Prefix}mark/{Prefix}align) will drop it until you are {Content.SageLevel} again."
                    : ""));
        Log.Info($"   -> @sage '{_char.Name}' -> rung {rung} ({want.Key})");
    }

    /// <summary>The Dog Linguist chain's progress key and legend id — one name, as in RTK
    /// (npc_dialog.lua <c>DOG_LEGEND</c>). Stage 4 is the finished chain.</summary>
    private const string DogChainReg = "dog_linguist";
    private const int DogChainDone = 4;

    // "@quest [key] [stage]" — the raw quest registry, readable and writable, so any quest can be re-tested
    // without a purpose-built command per chain. Bare @quest dumps every key the character carries (the int
    // stage machine AND the string registry), which is how a tester DISCOVERS the key in the first place —
    // none of this is visible in-game anywhere else. "@quest <key>" reads one; "@quest <key> <n>" sets it
    // (0 removes the entry outright — same read-back as 0, and the dump stays clean); a non-numeric value
    // sets the STRING registry instead (e.g. the minor-quest selection). Stage meanings are per-quest — see
    // docs/common/Quest-Registry.md for the full catalogue of keys, stages and the legends that pair with
    // them (most chains gate on the LEGEND, not the stage, so a re-test usually needs @legend too).
    private void QuestCmd(CommandArgs a)
    {
        if (a.None)
        {
            var saved = QuestState.Saved(_char);
            var texts = QuestState.SavedText(_char);
            if (saved.Count == 0 && texts.Count == 0)
            { Reply($"No quest keys set. ({Prefix}quest <key> <stage> to set one; see docs/common/Quest-Registry.md.)"); return; }
            ReplyList($"quests ({saved.Count}" +
                      $"{(texts.Count > 0 ? $"+{texts.Count} str" : "")})",
                      saved.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} = {e.Value}")
                        .Concat(texts.OrderBy(e => e.Key, StringComparer.Ordinal)
                                     .Select(e => $"{e.Key} = \"{e.Value}\"")));
            return;
        }

        string key = a.Word(0);
        if (a.Count == 1)
        {
            if (QuestState.Saved(_char).TryGetValue(key, out int cur)) Reply($"{key} = {cur}");
            else if (QuestState.SavedText(_char).TryGetValue(key, out var cs)) Reply($"{key} = \"{cs}\"");
            else Reply($"{key} is not set (reads as stage 0).");
            return;
        }

        string val = a.Rest(1);
        if (int.TryParse(val, out int stage))
        {
            if (stage == 0)
            {
                bool had = QuestState.Remove(_char, key) | QuestState.RemoveText(_char, key);
                SaveChar();
                Reply(had ? $"{key} cleared (was set; now reads as stage 0)." : $"{key} was not set — nothing to clear.");
            }
            else
            {
                Quest(QuestState.Registry).Set(key, stage);
                Reply($"{key} = {stage}.");
            }
        }
        else
        {
            Quest(QuestState.Registry).SetText(key, val);
            Reply($"{key} = \"{val}\" (string registry).");
        }
        Log.Info($"   -> @quest '{_char.Name}': {key} <- {val}");
    }

    /// <summary>Keyed legend marks that @questreset must NOT touch: they are relationship and mentorship
    /// state, not quest progress, and each is half of a pair the rest of the server keeps in step.
    /// "married"/"engaged" travel with <c>SetSpouse</c>/<c>ClearEngagement</c> (Session.CharacterApi), so
    /// dropping the mark alone leaves a character married in state with nothing on the profile saying so —
    /// a half-divorce no real code path can produce. The mentorship trio is a lifetime record ("Mentored 12
    /// new players"), not a flag any chain gates on, and clearing it destroys history a replay never restores.
    ///
    /// <para>A DENY-list rather than an allow-list because quest legends are open-ended — every new chain
    /// adds one — so an allow-list would silently stop clearing the newest quest's mark, which is exactly the
    /// failure this command exists to prevent.</para></summary>
    private static readonly HashSet<string> NonQuestLegends = new(StringComparer.Ordinal)
    { "married", "engaged", "mentored", "mentored_by", "being_mentored_by" };

    // "@questreset" — clear the WHOLE quest registry and every keyed legend, so every chain can be walked
    // again from nothing. This is "@quest <key> 0" for all keys at once plus the half that command cannot
    // reach: most chains gate on the LEGEND rather than the stage (see the note above LegendCmd), so wiping
    // stages alone re-tests nothing — the giver still sees the mark and skips to "you have already done
    // this". Clearing both is the only combination that actually replays a quest, which is why one command
    // owns them rather than leaving a tester to pair @quest with @legend and find out later they missed one.
    // The seeded "Born in ..." mark has no key and so survives, exactly as it does in @legend.
    // Kills and the kill track are deliberately LEFT ALONE: quests read a kill delta since accept, so a
    // lifetime tally never blocks a replay, and "@killtrack clear" already owns the eight-slot track.
    private void QuestResetCmd(CommandArgs a)
    {
        int stages = QuestState.Saved(_char).Count;
        int strings = QuestState.SavedText(_char).Count;
        int marks = _char.Legends.RemoveAll(l => l.Name.Length > 0 && !NonQuestLegends.Contains(l.Name));
        if (stages + strings + marks == 0) { Reply("Nothing to reset."); return; }

        QuestState.Clear(_char);
        QuestState.ClearText(_char);
        SaveChar();

        // Kept short on purpose: Reply wraps at PaneWidth (30), so a sentence of prose here arrives as a
        // five-line wall on the status pane. The counts are the whole message.
        Reply($"Quest reset: {stages} stages, {strings} strings, {marks} marks.");
        Log.Info($"   -> @questreset '{_char.Name}': {stages} stages, {strings} strings, {marks} legends");
    }

    // "@legend [key] [0 | <icon> <color> <text...>]" — the legend list with its INTERNAL keys showing. The
    // profile window renders only each mark's text; the key (RTK's legend name) is what quests gate on
    // (HasLegend), so this is the only place a tester can see which key a mark answers to. "@legend <key> 0"
    // removes a mark; "@legend <key> <icon> <color> <text...>" (re)creates one — replace-by-key, same as
    // AddLegend everywhere — so a post-quest state can be entered directly with the values from
    // docs/common/Quest-Registry.md. The seeded "Born in …" mark has no key and so can't be addressed here,
    // which doubles as its protection. NOTE: a legend and its quest stage are independent — most chains
    // check the legend, so clearing only the stage usually re-tests nothing.
    private void LegendCmd(CommandArgs a)
    {
        if (a.None)
        {
            ReplyList($"legends ({_char.Legends.Count})", _char.Legends.SelectMany(l => new[]
            {
                $"{(string.IsNullOrEmpty(l.Name) ? "(no key)" : l.Name)} — icon {l.Icon} col {l.Color}",
                $" \"{l.Text}\"",
            }));
            return;
        }

        string key = a.Word(0);
        var held = _char.Legends.FirstOrDefault(l => l.Name == key);
        if (a.Count == 1)
        {
            Reply(held is null ? $"{key}: not held."
                                 : $"{key}: \"{held.Text}\" (icon {held.Icon}, color {held.Color})");
            return;
        }

        if (a.Count == 2 && a.Word(1) == "0")
        {
            RemoveLegend(key);
            Reply(held is null ? $"{key} was not held — nothing to remove." : $"{key} removed (\"{held.Text}\").");
            Log.Info($"   -> @legend '{_char.Name}': removed {key}");
            return;
        }

        if (a.Count >= 4 && byte.TryParse(a.Word(1), out byte icon) && byte.TryParse(a.Word(2), out byte color))
        {
            string body = a.Rest(3);
            AddLegend(body, key, icon, color);
            Reply($"{key} {(held is null ? "added" : "replaced")}: \"{body}\" (icon {icon}, color {color}).");
            Log.Info($"   -> @legend '{_char.Name}': {key} <- icon {icon} color {color} \"{body}\"");
            return;
        }

        Refuse(a.Usage());
    }

    // "@class <name>" — set the class/path and rebuild the character as one. `Character.ClassName` stores the
    // BASE name and is the single source of truth for the path id (Content.PathIdForClass), which drives spell
    // learning, the ItmPthId gear restriction and the subpath chat channel; what a player SEES is
    // ClassTitle (base + rank), so the stored string stays stable while the displayed one changes with @mark.
    //
    // ACCEPTS RANK NAMES TOO. "@class Inferno" is Ju jak at mark 2, "@class Il san (W)" is Warrior at mark 1 —
    // the rank titles are what a character is actually called, so refusing them would mean the name shown in
    // the profile is one you can't type back. A base name leaves the current rank alone.
    //
    // RESTRICTED to Content.PlayablePaths: the four base classes, Peasant, the four NPC subpaths (Chung ryong /
    // Baekho / Ju jak / Hyun moo) and the twelve PC subpaths (Barbarian … Muse). Only RTK's GM branch
    // (Dreamweaver, Archon) is refused. ANY SUBPATH FORCES LEVEL 99, for the same reason @mark does: an NPC
    // subpath is joined at the cap and its own spell is pinned there; a PC subpath is joined from 50 (by
    // @subpath admit, which keeps the level), and "@lvl 50" after this gives a tester that member. A PC
    // subpath's own spells do not come with it (Content.IsPcSubpathSpell) — @spell teaches them one at a time.
    //
    // The rebuild keeps the current level and rank and re-derives everything else from the new class: stats
    // follow the new path's growth curve (a Warrior turned Mage loses the warrior HP roll and gains the mage
    // MP one) and the book becomes the new class's. Without that, "@class Mage" left a Warrior's HP, a
    // Warrior's skills, and a class line that no longer described either.
    private void SetClass(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0)
        {
            Reply($"class is '{ClassTitle}' ({a.Usage()})");
            Reply($"  {string.Join(" · ", Content.PlayablePathNames())}  — or any rank title up to Sam san " +
                    $"(Il san (W), Fury, Inferno, …)");
            return;
        }
        if (Content.PathRankForName(name) is not { } pick)
        { Refuse($"'{name}' isn't a known class or rank. Try: {string.Join(" · ", Content.PlayablePathNames())}"); return; }
        if (!Content.IsPlayablePath(pick.PathId))
        {
            Refuse($"'{Content.PathName(pick.PathId)}' isn't playable here — it is RTK's GM branch, not a class. " +
                    $"Try: {string.Join(" · ", Content.PlayablePathNames())}");
            return;
        }

        if (pick.Mark > Content.MaxMark)
        {
            Refuse($"'{name}' is rank {pick.Mark}, past the {Content.PathTitle(pick.PathId, Content.MaxMark)} cap — " +
                    $"there are no mark-{Content.MaxMark + 1} spells in the game data yet. " +
                    $"Try  {Prefix}class {Content.PathTitle(pick.PathId, Content.MaxMark)}.");
            return;
        }

        bool subpath = Content.PathBaseOf(pick.PathId) != pick.PathId;   // NPC or PC
        int mark  = pick.Mark > 0 ? pick.Mark : Math.Min((int)_char.Mark, Content.MaxMark);   // a rank name sets the rank; a base name keeps it
        int level = subpath || mark > 0 ? 99 : _char.Level;               // subpaths and ranks live at the cap

        // A new path is nobody's elder, as joining and leaving a PC subpath already make it: otherwise "@class"
        // back into the subpath would revive an old elder mark with no appointment (PR #393's review, F2). A rank
        // title on the same path keeps it.
        if (pick.PathId != CharClassId) ClearSubpathElder();
        _char.ClassName = Content.PathName(pick.PathId);
        RespecTo(level, mark);
        SendSelfProfile();
    }

    /// <summary>"@stats &lt;vita&gt; &lt;mana&gt; &lt;all&gt;" or "@stats &lt;vita&gt; &lt;mana&gt;
    /// &lt;might&gt; &lt;grace&gt; &lt;will&gt;" — set the vitals and the three base stats outright, for
    /// setting up a test character without grinding a level curve. Two arguments sets the pools alone.
    ///
    /// This is the DIRECT counterpart to <c>@lvl</c>: that one resets to the level-1 baseline and re-applies
    /// real LevelUps so every number is legitimate for the level, which is what you want to test progression
    /// — and is exactly wrong when you just want 50k vita to stand in front of a boss. Note the two fight
    /// each other by design: a later @lvl (or a @class/@mark resync) recomputes vitals from the curve and
    /// discards whatever was set here. Set the class/level first, then @stats.
    ///
    /// Might/grace/will are bytes on the character, so they clamp to 0-255; the pools are u32 and clamp to
    /// at least 1 vita (a max HP of 0 would make the character permanently dead). HP/MP are refilled to the
    /// new maxima, gear bonuses included.</summary>
    private void SetStatsCmd(CommandArgs a)
    {
        // Positional, so every word has to BE a number: "@stats 1 2 x" is a three-argument call with a bad
        // third argument, not a two-argument one. ParseInts used to drop the "x" and quietly run the short
        // form.
        if (a.Count is not (2 or 3 or 5) || !a.Int(0, out var vita) || !a.Int(1, out var mana))
        {
            Refuse(a.Usage());
            Refuse($"  now: vita {_char.MaxHp:N0}, mana {_char.MaxMp:N0}, might {_char.Might}, " +
                    $"grace {_char.Grace}, will {_char.Will}");
            return;
        }

        _char.MaxHp = (uint)Math.Max(1, vita);
        _char.MaxMp = (uint)Math.Max(0, mana);
        if (a.Count == 3)
            _char.Might = _char.Grace = _char.Will = (byte)Math.Clamp(a.Int(2, 0), 0, 255);
        else if (a.Count == 5)
        {
            _char.Might = (byte)Math.Clamp(a.Int(2, 0), 0, 255);
            _char.Grace = (byte)Math.Clamp(a.Int(3, 0), 0, 255);
            _char.Will  = (byte)Math.Clamp(a.Int(4, 0), 0, 255);
        }

        _char.Hp = EffMaxHp;                  // top up to the new maxima, gear/buffs included
        _char.Mp = EffMaxMp;
        if (_enteredWorld) StoreSave();
        SendStats();
        // Report BASE and EFFECTIVE separately: what you set is the base, but the HUD shows base + gear +
        // buffs, so "@stats 50000 …" reading back as 50,400 on screen is equipment, not a rounding bug.
        var eq = Totals();
        Reply($"base: vita {_char.MaxHp:N0}, mana {_char.MaxMp:N0}, might {_char.Might}, grace {_char.Grace}, will {_char.Will}.");
        if (EffMaxHp != _char.MaxHp || EffMaxMp != _char.MaxMp || eq.might != 0 || eq.grace != 0 || eq.will != 0)
            Reply($"with gear: vita {EffMaxHp:N0}, mana {EffMaxMp:N0}, might {_char.Might + eq.might}, " +
                    $"grace {_char.Grace + eq.grace}, will {_char.Will + eq.will}.");
        Log.Info($"   -> {Prefix}stats: hp {_char.MaxHp} mp {_char.MaxMp} " +
                 $"M{_char.Might}/G{_char.Grace}/W{_char.Will}");
    }

    // 0x0D speech: chatType(u8) entityId(u32BE) msgLen(u8) msg[]. Handler 0x450170 shows msg
    // over the entity's head.
    private void SendSpeech(byte chatType, uint id, byte[] msg)
    {
        var d = new List<byte> { chatType };
        d.AddRange(PacketWriter.U32BEBytes(id));
        d.Add((byte)msg.Length);
        d.AddRange(msg);
        SendMap(ServerOp.Speech, _gameInc++, d.ToArray(), "speech(0x0D)");
    }


}
