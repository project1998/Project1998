using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    /// <summary><see cref="InvItem.Slot"/> is ZERO-based; every wire field that names a bag slot is
    /// one-based. The client keeps its bag as a 164-byte-stride array and indexes it with this value
    /// verbatim — the `0x0F` store (`0x48f070`) and the `0x2f` sell grid's row loop (`0x455541`) compute the
    /// same address, `base + 164*n + 0x13e` — so anything that puts a slot on the wire must go through here.
    /// Getting it wrong renders a *blank* row rather than nothing (the loop's `test cl,cl; je` skips the draw
    /// but still counts the row), which reads as a list that's shifted rather than one that's misindexed.</summary>
    private static byte WireSlot(InvItem it) => (byte)(it.Slot + 1);

    // 0x0F add-item-to-slot: slot(u8=idx+1) icon(u16) iconColor(u8) [dispName u8len+txt] [baseName u8len+txt]
    //   amount(u32) [block: stack/0(u8) dura(u32) protected(u8)] [owner u8len+txt] 00 00 00.
    private void SendAddItem(InvItem it)
    {
        var def = Content.ItemById(it.ItemId);
        if (def is null) return;
        string name = string.IsNullOrEmpty(it.CustomName) ? def.Name : it.CustomName;
        // RTK clif_sendadditem (clif.c:7096) bakes the count into the display NAME: a stack as "(N)", a
        // charged consumable (ITM_SMOKE: wine/liquor/pipes) as "[N unit]" using ItmText -> "Wine [50 sips]".
        // A charged item whose Dura is still 0 (older save, not yet seeded) shows its full charge count
        // until first use lazily seeds it (see HandleUseItem), so the number never visibly jumps.
        string disp = it.Amount > 1 ? $"{name} ({it.Amount})"
                    : def.IsCharged ? $"{name} [{(it.Dura == 0 ? def.Durability : it.Dura)} {def.Text}]"
                    : name;

        var d = new List<byte> { WireSlot(it) };
        d.AddRange(Be(IconWire(IconOf(def))));   // client Item.epf frame; encode for the +0x4000 resolver
        // 5.x (V533) carries an icon-color byte here; 4.95 (V495) does NOT — it reads the name length
        // right after the icon. Proven live: on 4.95 an extra byte here made the client read the name
        // one byte early (Apple iconColor=0 → empty name "You ate ."; Poison apple iconColor=12 → 12-char
        // garbled "⊥Poison appl"). See docs §11c.
        if (_ver == ClientVersion.V533) d.Add(def.IconColor);
        var dn = Ascii(disp); d.Add((byte)dn.Length); d.AddRange(dn);
        // 4.95 carries a SECOND, base-name string here; 5.33 does NOT — its parser (sub_4d8290) reads
        // slot, icon, iconColor, one length-prefixed name, and then goes straight to the u32 amount.
        // Confirmed two ways: the disassembly's call order, and a live read trace whose offsets line up
        // only under this layout (name len 13 at body[4], name body[5..17], amount body[18..21], next
        // field body[22]). Sending 4.95's second string made 5.33 eat its length byte as part of the
        // amount and shift every field after it — which is why the inventory pane drew nothing.
        //
        // Worth knowing for the next packet: the amount is read through 0x4a3ec0, an ADVANCING u32BE
        // helper that was missing from re/grammar_533.py's primitive list, so those reads were invisible
        // in every trace before this. See docs/5.x/Wire-Divergences.md §8.
        if (_ver != ClientVersion.V533)
        {
            var bn = Ascii(def.Name); d.Add((byte)bn.Length); d.AddRange(bn);
        }
        d.AddRange(Be32((uint)it.Amount));
        if (def.IsEquip) { d.Add(0); d.AddRange(Be32(it.Dura)); d.Add(0); }
        else { d.Add((byte)(def.Stackable ? 1 : 0)); d.AddRange(Be32(0)); d.Add(0); }
        d.Add(0);                 // owner name length (0 = unowned)
        d.AddRange(Be(0));        // trailing u16
        d.Add(0);                 // trailing u8
        SendMap(0x0F, _gameInc++, d.ToArray(), $"additem(0x0F) slot={it.Slot} '{name}' x{it.Amount}");
    }

    // 0x10 remove-from-slot: slot(u8=idx+1) reason(u8) 00 00. The reason picks the line the CLIENT prints;
    // 12 is the only silent one. Full table swept live 2026-08-07 — see Content.EquipDelReason.
    private void SendDelItem(byte slot, byte reason) =>
        SendMap(0x10, _gameInc++, new byte[] { (byte)(slot + 1), reason, 0, 0 }, $"delitem(0x10) slot={slot} r={reason}");

    // 0x37 equip-window: equipType(u8) icon(u16) iconColor(u8) [name u8len+txt] [baseName u8len+txt] dura(u32) 00 00.
    private void SendEquip(InvItem worn)
    {
        var def = Content.ItemById(worn.ItemId);
        if (def is null) return;
        string name = string.IsNullOrEmpty(worn.CustomName) ? def.Name : worn.CustomName;
        var d = new List<byte> { worn.Slot };     // worn.Slot holds the wire equip-slot byte
        d.AddRange(Be(IconWire(IconOf(def))));     // +0x4000 resolver encoding (see SendAddItem / IconWire)
        if (_ver == ClientVersion.V533) d.Add(def.IconColor);   // 4.95 omits the icon-color byte (see SendAddItem)
        var nn = Ascii(name); d.Add((byte)nn.Length); d.AddRange(nn);
        var bn = Ascii(def.Name); d.Add((byte)bn.Length); d.AddRange(bn);
        d.AddRange(Be32(worn.Dura));
        d.AddRange(Be(0));
        SendMap(0x37, _gameInc++, d.ToArray(), $"equip(0x37) slot={worn.Slot} '{name}'");
    }

    // The profile-screen equipment ICON cells (helm + two rings). 4.95 has no character-sprite layer for these
    // slots, so both profile views (0x39 self, 0x34 other) show them as ground-icon boxes fed by three u16
    // fields. Encoded with IconWire, exactly like the 0x37 equip window (the old bug proved these boxes render
    // an IconWire value — it wrongly showed the weapon there). Client wire slots (from 0x1F captures): helm=4,
    // left ring=7, right ring=8. Returns 0 (empty box) when nothing is worn in that slot.
    private ushort ProfileCellIcon(byte wireSlot)
    {
        var worn = _char.Equipment.FirstOrDefault(e => e.Slot == wireSlot);
        var def = worn is null ? null : Content.ItemById(worn.ItemId);
        return def is null ? (ushort)0 : IconWire(IconOf(def));
    }

    /// <summary>The Item.epf frame to send this client for <paramref name="def"/>. 4.95 has no colour byte
    /// anywhere in the item graphics path, so the colour is folded into the frame (<see cref="ItemDef.ClientIcon"/>);
    /// 5.x carries <c>iconColor</c> as its own field on 0x0F/0x37 and must keep the base icon so the two
    /// aren't applied twice. This is the ONLY place that difference lives — every icon we emit goes through
    /// here (bag, equip window, profile cells, floor items, item dialog portraits).</summary>
    private ushort IconOf(ItemDef def) => _ver == ClientVersion.V533 ? def.Icon : def.ClientIcon;

    // 0x38 unequip-window: spot(u8) 00.
    private void SendUnequip(byte wireSlot) =>
        SendMap(0x38, _gameInc++, new byte[] { wireSlot, 0 }, $"unequip(0x38) slot={wireSlot}");

    /// <summary>Draw a floor item AT REST via the 0x07 static-object path (NOT 0x16). Full RE (2026-07-24):
    /// 0x16 builds a WALK projectile (vtable 0x4cd18c, tick 0x463270) that interpolates in then drops off the
    /// moving-list / self-destructs on arrival -> invisible at rest (that was the bug). The 0x07 handler
    /// (0x44fdb0 @ 0x44fe7f) routes any look OUTSIDE 0x8000..0xbfff to descriptor type 2 = the BASE object
    /// (vtable 0x4cd118, tick 0x4601a0 = `xor al,al;ret` no-op) built by 0x462ec0 alone: it never moves, never
    /// self-destructs, and is drawn by the shared render loop exactly like a monster but stationary. IconWire
    /// frames (0..1310) map to 0xc000..0xc51e, all > 0xbfff, so they hit type 2 and resolve (look+0x4000)&0xffff
    /// against Item.epf -- the SAME resolver the bag/0x0F path uses. Caveat: 0x07 has a viewport gate (0x424310),
    /// so the tile must be on-screen when spawned (true for drop/throw at the player's feet).</summary>
    /// <para><see cref="GroundItem.Graphic"/> is the base <c>ItmIcon</c> (world state, shared by every viewer),
    /// so the per-client colour fold happens here rather than at drop time. Coin piles carry ItemId -1 and no
    /// def, and keep the graphic they were dropped with.</para>
    /// <para>VIEWPORT-GATED, and tracked in <c>_shownItems</c>. The 0x07 gate above is not advisory: a draw
    /// for an off-screen tile is thrown away by the client, and since floor items never move, nothing would
    /// ever re-send it. So a call for an out-of-view tile is dropped here rather than pretended, and
    /// <see cref="SyncGroundItems"/> draws the item for real once we walk it into view. (Callers that hand us
    /// a synthetic marker — the spot-traps overlay in Session.Spells — are always at the caster's feet, so
    /// they pass the gate on the same terms a drop at your own feet does.)</para>
    public void ShowGroundItem(GroundItem gi)
    {
        if (!InView(gi.X, gi.Y, ShowPad)) return;
        var def = gi.ItemId >= 0 ? Content.ItemById(gi.ItemId) : null;
        SendCreatureList(new[] { (gi.Id, IconWire(def is null ? gi.Graphic : IconOf(def)), gi.X, gi.Y, (byte)0, (byte)0) });
        using (EnterView()) _shownItems.Add(gi.Id);
    }

    // The 4.95 type-0 form has three gear-driven look bytes: weapon [5], armor [3] and shield [6]. Weapon/
    // shield are derived live from Equipment by WeaponLook()/ShieldLook() (0xFF = bare), so equipping any of
    // the three must re-draw self + peers; only armor still needs its cached _char.Armor byte written here.
    private void ApplyAppearance(ItemDef def, bool equip)
    {
        if (def.Type == 4) _char.Armor = equip ? ArmorWireLook(def.Look) : (byte)0;         // ITM_ARMOR (cached in [3])
        else if (def.Type == 3) _char.Weapon = equip ? WeaponWireLook(def.Look) : (byte)0;  // ITM_WEAP (kept for combat/GM; wire byte, see WeaponWireLook)
        else if (def.Type != 5) return;                                           // not weapon/armor/shield -> no look change
        RefreshAppearance();
    }

    // ---- recv handlers (client -> server) ----

    // 0x07 pick up: grab whatever floor item sits on my tile; coins (sentinel ItemId<0) go to the purse.
    // The client sends pickuptype at body[0] (RTK clif_parsegetitem: RFIFOB(fd,5)): ',' = 0 (grab the top
    // item), '<'/Shift+, = 1 (grab EVERYTHING stacked on the tile). Either way, play the bend-down action
    // first — type 4, time 40; the crouch sprite carries the pickup sound — on self AND peers, even when the
    // tile is empty (matches RTK, which sends the action before it looks at the floor).
    private void HandlePickup(byte[] dec)
    {
        bool pickAll = dec.Length > 0 && dec[0] != 0;
        SendAction(_char.Id, 4, 40, 0);                                                     // our crouch + sound
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 4, 40, 0), except: this);   // peers see it too

        // The ATTEMPT is what drops Invisible, not a successful grab — bending down in plain sight gives you
        // away whether or not there was anything there. So this sits with the crouch, ahead of the floor
        // lookup, exactly as the action itself does. Covers both keys: ',' (top item) and Shift+, (the whole
        // stack), since the client routes both through this one opcode and differs only in body[0].
        // It used to live inside the loop below, after the null check, so an empty tile or someone else's
        // death pile left you invisible — a free way to test a tile without breaking stealth.
        BreakStealth();

        do
        {
            // Pass our id so someone else's death pile is passed over rather than pocketed (RTK canLoot).
            var gi = _world.PickUp(_char.Map, _char.X, _char.Y, _char.Id, ownOnly: false, out bool locked);
            if (gi is null)
            {
                if (locked) SendMiniText("That item does not belong to you.");   // RTK canLoot's own refusal
                return;                                   // tile empty (or nothing here we may take)
            }
            if (gi.ItemId < 0) { _char.Coins += (uint)gi.Amount; SendStats(); MarkDirty(); continue; }   // coins -> purse
            var def = Content.ItemById(gi.ItemId);
            if (def is null) continue;
            if (!GiveItem(def, gi.Amount, gi.Dura, gi.CustomName, owner: gi.Owner))   // preserve any bond off the ground
            {
                // pack full — put it straight back on the floor so it isn't lost, and stop grabbing.
                _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = gi.ItemId,
                    X = _char.X, Y = _char.Y, Amount = gi.Amount, Dura = gi.Dura, Graphic = gi.Graphic, CustomName = gi.CustomName,
                    Owner = gi.Owner });
                return;
            }
        } while (pickAll);                                // ',' runs once; '<' loops until the tile is empty
    }

    // 0x08 drop: dec[0]=slot(1-based). Drop the whole stack onto my tile.
    private void HandleDropItem(byte[] dec)
    {
        if (dec.Length < 1) return;
        // RTK clif_parsedropitem gates on player state first (dead/mounted can't drop).
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (BlockedByMount()) return;
        int slot = dec[0] - 1;
        // dec[1] = the "all" flag: 'd' (drop one) sends 0, 'D'/Shift+d (drop whole stack) sends 1.
        // Confirmed live: client emits `08 <slot+1> 00 00` for d and `08 <slot+1> 01 00` for D.
        bool dropAll = dec.Length > 1 && dec[1] != 0;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;

        // Dropping the leviathan talisman in front of a cage is how the quest instructions say to free a
        // captive ("walk up to one of the cages and drop your talisman on the ground"). Checked BEFORE the
        // NoDrop refusal on purpose: the talisman is registry-flagged NoDrop (user-specified, 2026-08-28 — a
        // one-shot quest item must not be losable to a stray keypress), and the rite is not a drop at all —
        // the talisman never touches the ground. In the pen the rite answers; anywhere else the flag does.
        if (TryLeviathanTalismanDrop(def)) return;

        // Dropping the sacred water in front of The Infected is likewise the rite, not a drop — and for the
        // same reason it is checked before the NoDrop refusal that guards the vial everywhere else. See
        // Session.TrySacredWaterDrop and Server/PoetWhipQuest.cs.
        if (TrySacredWaterDrop(def)) return;

        if (def.NoDrop) { SendLog($"You can't drop {def.Name}."); return; }

        // Dropping a pick/axe/sickle beside a resource node is how you gather on 4.95 — the drop IS the
        // swing, and the tool stays in the bag so you can keep dropping. Anything else (or nothing to
        // harvest nearby) falls through and drops normally. See Session.Harvest.cs.
        if (TryHarvest(def)) return;

        // Dropping a White amber in the middle of the Mythic Nexus is the Star chain's prerequisite rite,
        // not a drop — it is absorbed where it falls. See BlessedByTheStars.
        if (TryStarBlessing(def, slot)) return;

        // Bend-down drop animation + sound (RTK clif_parsedropitem: type 5, time 20 — a distinct pose from
        // pickup's type 4). Fired only once the drop is allowed, on self AND peers, before the item leaves the bag.
        SendAction(_char.Id, 5, 20, 0);                                                     // our drop crouch + sound
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 5, 20, 0), except: this);   // peers see it too

        int count = dropAll ? it.Amount : 1;
        int remaining = it.Amount - count;
        if (remaining <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, 1); }  // reason 1 = Drop
        else { it.Amount = remaining; SendAddItem(it); }   // stack shrinks: redraw the slot with the new count
        MarkDirty();
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
            X = _char.X, Y = _char.Y, Amount = count, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName,
            Owner = it.Owner });   // a dropped bound item stays bound to its owner on the ground
    }

    // 0x17 throw: dec[0]=confirm, dec[1]=slot(1-based). Throw one, land it a few tiles ahead.
    private void HandleThrow(byte[] dec)
    {
        if (dec.Length < 2) return;
        // RTK clif_parsethrowitem gates on player state first (dead/mounted can't throw).
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (BlockedByMount()) return;
        int slot = dec[1] - 1;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        if (def.NoDrop) { SendLog("You can't throw this item."); return; }   // same restriction as dropping (RTK itemdb_droppable)
        SendAction(_char.Id, 2, 20, 0);                                                    // throw animation (self)
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 2, 20, 0), except: this);   // peers see the throw too
        it.Amount -= 1;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, 4); }  // reason 4 = Throw
        else SendAddItem(it);
        MarkDirty();
        // Fly up to 3 tiles in the facing direction, but STOP at the last passable tile — a thrown item
        // must not land past a wall/off the map into an unreachable spot. Step tile-by-tile and halt before
        // the first blocked/off-map cell (same collision the player walk uses). If the tile directly ahead is
        // solid, the item just lands on the thrower's own tile.
        int tx = _char.X, ty = _char.Y, dx = 0, dy = 0;
        switch (_facing & 3) { case 0: dy = -1; break; case 1: dx = 1; break; case 2: dy = 1; break; case 3: dx = -1; break; }
        var tmap = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        for (int step = 0; step < 3; step++)
        {
            int cx = tx + dx, cy = ty + dy;
            if (cx < 0 || cy < 0 || cx >= _char.MapXs || cy >= _char.MapYs) break;   // off the tile grid
            // Same two-layer collision the walk uses: ground pass flag OR the SObj.tbl directional object-wall
            // for the throw heading — a thrown item halts at a building wall, not just at water/cliffs.
            if (PassEnforce && tmap != null
                && (Blocked(tmap, cx, cy) || ObjectFlags.Blocks(tmap.Obj(cx, cy), _facing & 3))) break;
            tx = cx; ty = cy;
        }
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
            X = (ushort)tx, Y = (ushort)ty, Amount = 1, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName,
            Owner = it.Owner });   // a thrown bound item stays bound to its owner
    }

    // 0x09 ';' Look: name whatever occupies the tile we're facing, RTK's PC -> mob/NPC -> item order
    // (clif_parselookat_sub / commented clif_parselookat_scriptsub give the exact text shape per entity
    // kind — bare name, stack count in parens for a floor item). The reply goes to the STATUS/MINI-TEXT
    // box below the inventory (SendMiniText / 0x0A), NOT the chat bubble — matching RTK, whose look-at
    // ends in clif_sendminitext. NPCs are stationary mobs (IsNpc-tagged) in the same shared list, so the
    // mob check already covers them; an empty tile gets no reply, same as RTK (no clif_sendminitext call
    // when nothing's found).
    private void HandleLookAt(byte[] dec)
    {
        int tx = _char.X, ty = _char.Y;
        switch (_facing & 3) { case 0: ty--; break; case 1: tx++; break; case 2: ty++; break; case 3: tx--; break; }

        var peer = _world.PeerAt(_char.Map, tx, ty);
        if (peer is not null) { SendMiniText(peer.Snapshot().Name); return; }

        var mob = _world.MobAt(_char.Map, tx, ty);
        if (mob is not null) { SendMiniText(mob.Name); return; }

        // Session-local debug dummies (@cre/@mob/@crow/@crecol/look-lab) never join the shared world, so
        // they're invisible to _world.MobAt — check our own dummy list too (e.g. @crecol's "col<N>" labels).
        var dummy = MobAt(tx, ty);
        if (dummy is not null) { SendMiniText(dummy.Name); return; }

        var gi = _world.ItemsOn(_char.Map).LastOrDefault(i => i.X == tx && i.Y == ty);
        if (gi is null) return;
        string name = gi.ItemId < 0 ? "coins" : string.IsNullOrEmpty(gi.CustomName) ? Content.ItemById(gi.ItemId)?.Name ?? "an item" : gi.CustomName;
        SendMiniText(gi.Amount > 1 ? $"{name} ({gi.Amount})" : name);
    }

    // 0x1C use / 0x1A eat: dec[0]=slot(1-based). Equipment -> wear it; consumable -> run its RTK use-script
    // effect (see ApplyItemEffect + the ItemParams.csv/item_verbs.lua verb/row system).
    private void HandleUseItem(byte[] dec, bool eat)
    {
        if (dec.Length < 1) return;
        // Nothing gets used, eaten or worn from horseback. This funnel covers eat AND use AND the wear path
        // (EquipFromSlot below), so the gate belongs here rather than only on the branch that equips.
        if (BlockedByMount()) return;
        int slot = dec[0] - 1;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        // Path/mark restriction goes HERE, not in EquipFromSlot: RTK puts it in pc_useitem (pc.c:1998), which
        // is the funnel for eating, using AND wearing, so it covers the 17 non-equip items that carry an
        // ItmPthId — a Rogue's stealth powder, a Warrior's shard bomb, the eight rogue darts. See CanUsePath.
        if (!CanUsePath(def)) return;
        // "that", never the item's name — the refusal is about the attempt, not the object.
        if (def.IsEquip) { if (eat) { SendMiniText("You can't eat that."); return; } EquipFromSlot(slot); return; }
        if (eat && def.Type != 0) { SendMiniText("You can't eat that."); return; }   // ITM_EAT only — same line as gear
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }

        // Only the true consumable classes (ITM_EAT/ITM_USE/ITM_SMOKE) may be SPENT by the use key. For every
        // other type RTK pc_useitem either runs the item's use script and never delitems (ITM_ETC/BAG/MAP —
        // the script decides), or does nothing at all (mounts, dyes, traps — the default case). Our ItemParams
        // row IS the script, so a non-consumable without one is RTK's scriptless no-op: nothing happens and the
        // item STAYS IN THE BAG. Treating these as consumables let 'u' destroy the leviathan talisman (ITM_ETC,
        // scriptless in RTK too) — inert "You used", captive still caged, quest dead-ended, since Dae-Whan only
        // ever makes one. A non-consumable WITH a row (the type-18 potions and scrolls) falls through: for
        // those the shared consume below stands in for the RTK script's own removeItem call.
        if (!def.IsConsumable && !Content.ItemParams.ContainsKey(def.Key)) return;

        _useGesturePlayed = false;
        if (!ApplyItemEffect(def)) return;   // gate refused (e.g. ward already active) -> not consumed, RTK's own early-return

        // EVERY food (ITM_EAT) consume shows the eat pose + sound, even a zero-effect one. Chestnuts, meat
        // scraps and the like carry no ItemParams row and no Vita, so the fallback below never animated and
        // they vanished silently — RTK is no better (no chestnut script, empty global `use` hook), so this
        // is a deliberate rule of ours (user-specified 2026-08-19), not a port. Guarded by the gesture flag
        // so an effect verb that already showed its own gesture (heal/fatal's eat, a drink's sip, harden-
        // body's cast pose) doesn't play twice — and the mana items (wine, pipes) keep their sip, not this.
        if (def.Type == 0 && !_useGesturePlayed) ItemEatAnim();

        // Narration (user-specified, 2026-08-07): a consumable is SILENT until it's gone, and speaks exactly
        // once — on the use that removes the last of it. The line comes from the CLIENT itself, off the
        // delitem reason: FOOD (ITM_EAT) gets 2 -> "You ate <item>."; every other consumable (wine/liquor
        // charges, herbs, powders, ITM_USE) gets 6 -> "You used <item>." A use that leaves stock behind sends
        // no delitem at all, hence no line. (Wearing gear narrates nothing at all — see EquipFromSlot, which
        // used to borrow that same reason 6 and so announced armour as though it had been drunk.)
        // Key off the item TYPE, not the eat/use keypress: 0x1C on food is still eating. Full reason table in
        // Content.EquipDelReason. Reason 3 ("You smoked") is deliberately NOT used for the pipes even though
        // it exists and ItmText would identify them exactly (only "puffs" vs "sips" appear in the registry) —
        // user's call, 2026-08-07. Pipes take 6 like every other non-food consumable.
        bool food = def.Type == 0;   // ITM_EAT
        byte goneReason = (byte)(food ? 2 : 6);

        // Charged consumables (RTK ITM_SMOKE: wine/liquor/cigarettes) hold N uses in their durability field
        // with a unit label in ItmText ("sips"/"puffs"). A use spends ONE charge, not the whole item; it is
        // removed only when charges reach 0 -- matching RTK pc_useitem's ITM_SMOKE path (pc.c:2281:
        // dura-=1; dura==0 ? delitem : re-send additem). Old saves may carry an unseeded Dura=0 -> seed here.
        if (def.IsCharged)
        {
            if (it.Dura == 0) it.Dura = def.Durability;
            it.Dura = (ushort)(it.Dura - 1);
            if (it.Dura == 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, goneReason); }
            else SendAddItem(it);   // re-send: the "[N unit]" charge count in the name updates in place
            MarkDirty();
            return;
        }

        it.Amount -= 1;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, goneReason); }
        else SendAddItem(it);
        MarkDirty();
    }

    // Runs one consumable's real RTK use-script effect via the data-driven verb/row Lua system
    // (game-data/ItemParams.csv names each item's verb + params; game-data/item_verbs.lua is the
    // logic; see Server/ItemScript.cs). The verb acts through ItemContext, whose primitives delegate to the
    // Item* methods below — the SAME plumbing the old C# switch used. Gate verbs (ward/hardenbody) check FIRST
    // and skip the eat animation on refusal, matching every reviewed script's guard-before-effect order.
    // Returns false — WITHOUT consuming the item — when a gate verb refused, or when the verb raised. Items with
    // no ItemParams row fall back to the item DB's own Vita/Mana columns (almost none carry them). Both files
    // hot-reload via @reload.
    private bool ApplyItemEffect(ItemDef def)
    {
        if (Content.ItemParams.TryGetValue(def.Key, out var row))
        {
            var verb = row.GetValueOrDefault("verb", "");
            switch (ItemScript.Apply(verb, new ItemContext(this), row))
            {
                case VerbResult.Ok:       return true;    // Lua ran it: consume
                case VerbResult.Declined: return false;   // a gate refused and has said so: don't consume
                case VerbResult.Errored:
                    // The verb raised part-way. Whatever it applied before that stands (a heal already sent is
                    // not clawed back), but the item is NOT spent and the player hears a refusal rather than
                    // nothing. Until #25 this case was indistinguishable from Missing below, so a runtime
                    // error in a hot-reloaded item_verbs.lua fell through to the DB Vita/Mana path, returned
                    // true, and HandleUseItem consumed the item: a typo destroyed consumables with no sign to
                    // the player. Substituting the C# path for a verb that exists but is broken is the one
                    // thing this must not do — the error is already in the log with its Lua location.
                    SendMiniText("That isn't working right now.");
                    return false;
                case VerbResult.Missing:  break;          // no such verb -> the DB Vita/Mana fallback below
            }
        }

        // No effect row (or the Lua path was unavailable): the rare item that actually carries Vita/Mana in the
        // item DB heals by those columns; anything else is an inert consume.
        bool healed = false;
        if (def.Vita > 0) { _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)def.Vita); healed = true; }
        if (def.Mana > 0) { _char.Mp = Math.Min(EffMaxMp, _char.Mp + (uint)def.Mana); healed = true; }
        if (healed) { ItemEatAnim(); SendStats(); }
        return true;
    }

    // ---- Item-effect verb primitives (called by ItemContext; see Server/ItemScript.cs) --------------------
    // Thin wrappers reusing the exact plumbing the old C# ApplyItemEffect switch used, so the Lua route can't
    // drift into a second implementation. (Stat reads level/might/hp/maxHp/mp reuse the shared Lua* accessors
    // defined in Session.Spells.cs; say/message/restoreMana reuse LuaSay/LuaMessage/LuaRestoreMana.)
    internal int ItemArmor => Math.Clamp(_char.Ac + Totals().armor, -80, 70);   // RTK harden-body's clamped armor

    // Set by each gesture primitive during one HandleUseItem run (reset there before ApplyItemEffect), so
    // the "every food animates" guarantee knows whether the item's effect verb already showed a gesture.
    private bool _useGesturePlayed;

    internal void ItemEatAnim()   // the shared eat/use pose + sound, self and peers (RTK action 8)
    {
        _useGesturePlayed = true;
        SendAction(_char.Id, 8, 40, 0);
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 8, 40, 0), except: this);
        PlayEatSfx();   // the action sprite carries no sound of its own — 403+006 over 0x19 (see EatSfxA/B)
    }

    /// <summary>The drink/smoke pose (RTK action 7), self and peers, with NO sound. Wine, liquor and the
    /// pipes are a different gesture from eating in RTK, not a variant of it: every food script is
    /// <c>sendAction(8, 25)</c> and plays nothing, while every drink and smoke script — wine.lua,
    /// herb_pipe.lua, sonhi_pipe.lua and the rest — is <c>sendAction(7, 20)</c> followed by an explicit
    /// <c>playSound(22)</c>. Ours ran both classes through the eat path, so a pipe chewed and gulped.
    ///
    /// The pose is ported; the sound deliberately is NOT. RTK's sound ids belong to a LATER client's sound
    /// space than 4.95's (the same trap that made the combat ids wrong), and the eat pair we do play was
    /// arrived at by ear on the live 4.95 client rather than read out of RTK. Playing 22 here would be
    /// guessing in the wrong numbering; silence is the safe half of the fix, and a real sip/puff id can be
    /// added the way the others were — by listening.</summary>
    internal void ItemSipAnim()
    {
        _useGesturePlayed = true;
        SendAction(_char.Id, 7, 20, 0);
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 7, 20, 0), except: this);
    }

    internal void ItemCastPose() { _useGesturePlayed = true; SendAction(_char.Id, 6, 40, 0); }   // harden-body cast pose (self only, as RTK)

    internal void ItemHeal(int amt)
    {
        if (amt <= 0) return;
        _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)amt);
        if (_char.Hp == EffMaxHp) SendMiniText("You feel satiated.");   // RTK: fires whether already full or capped here
        SendStats();
    }

    internal void ItemLoseHp(int amt)   // drink/smoke's small HP cost — never below 1
    {
        if (amt <= 0) return;
        _char.Hp = (uint)Math.Max(1, (int)_char.Hp - amt);
        SendStats();
    }

    internal void ItemKill()   // poison_apple: always-lethal
    {
        _char.Hp = 0;
        SendStats();
        Die();
    }

    internal bool ItemHasStatus(string key)          => HasStatusFlag(key);

    /// <summary>The item-verb host's door into the ward store (item_verbs.lua's <c>ward</c>/<c>hardenbody</c>
    /// through ItemContext). Guarded like any other entry point (#29): in production the Lua runs inside a
    /// handler and this is a free re-entrant no-op, but it is a real boundary — the state it writes is read
    /// on the autosave thread — so it stands on its own rather than on where its caller happens to be.</summary>
    internal void ItemSetStatus(string key, int ms)
    {
        using var _ = EnterState();
        SetStatusFlag(key, ms);
    }

    // ---- item wards that have a SPELL equivalent ---------------------------------------------------------
    // Sanctuary (aqua/green/lime potion), Harden Armor (brown/muddy) and Curse Protection (scroll of
    // protection/defense) are the same effects the spell system already implements, so the potion applies
    // them through the SAME slot the spell uses rather than a parallel flag. That is what makes RTK's own
    // guard work: aqua_potion.lua refuses on `checkIfCast(sanctuaries)`, which has to see the spell too.
    // ItemParams.csv drives it — a `category` column on the row picks this route (see item_verbs.lua ward).
    //
    // "deduction" is the odd one out because the sanctuary line is not a stat delta but a damage MULTIPLIER
    // living in its own slot (_sancDeduct), so it is checked and applied through that instead of _buffs.
    internal bool ItemWardBlocked(string category) =>
        category == "deduction" ? SancDeductActive : HasStatusCategory(category);

    internal void ItemApplyWard(string category, string stat, double amount, int ms, string key, string name)
    {
        if (ms <= 0) return;
        if (category == "deduction") { ApplySanctuaryDeduction(amount, ms, name); return; }
        // A protection carries no stat at all — it is a pure category-slot occupier, which is what makes
        // curses bounce off it (spell_verbs.lua BLOCKS: curses are blocked by "protections").
        ReceiveCurse(stat ?? "", (int)Math.Round(amount), ms, key, name, category);
    }
    internal bool ItemChance(int pct)                => Random.Shared.Next(1, 101) <= pct;   // 1..100 <= pct = success
    internal void ItemWarpHome()                     => ReturnToInn();   // RTK returnFunc -> a random tavern in your nation

    // RTK's setDuration/hasDuration namespace: named timed flags, key -> Environment.TickCount64 expiry.
    // Written by USE items (the item_verbs.lua "ward"/"hardenbody" verbs) AND by spell verbs through
    // LuaSetDuration — one store, because RTK has one, and the cross-talk is the whole point: the black
    // potion's `chin_baek_ho_ryung` is read by five warrior strike scripts. Persisted across a relog by
    // Session.TimedEffects (absolute unix deadlines), so logging out cannot bank a ward.
    //
    // Distinct from _buffs, which models a stat DELTA plus a category slot. A ward that has a spell
    // equivalent belongs there instead, not here — see ItemApplyWard, which routes Sanctuary, Harden Armor
    // and Curse Protection into the very slots their spell versions use so the two share exclusivity. What
    // stays here is the genuinely flag-shaped: `harden_body` (damage immunity), `chin_baek_ho_ryung` (a
    // warrior strike multiplier), `purple_potion` (a regen bonus). Each has a real reader; a flag nothing
    // reads is an item that silently does nothing, which is what this whole store used to be.
    private readonly Dictionary<string, long> _statusFlags = new();
    private bool HasStatusFlag(string key) => _statusFlags.TryGetValue(key, out var exp) && exp > Environment.TickCount64;

    // The two writers. Guarded (#29): the ward store is read on the autosave thread (CaptureTimedEffects) and
    // written from the read loop and from death on the tick thread, so every write has to be under the state
    // monitor or the dictionary can be resized under an enumeration.
    private void SetStatusFlag(string key, int durationMs)
    {
        AssertStateHeld("_statusFlags");
        _statusFlags[key] = Environment.TickCount64 + durationMs;
    }

    /// <summary>The same write, but given an already-computed TickCount64 deadline rather than a duration —
    /// the relog restore (Session.TimedEffects.RestoreTimedEffects) converts an absolute unix deadline back
    /// into this clock and must not re-base it off "now".</summary>
    private void SetStatusFlagUntil(string key, long expiresAtTick)
    {
        AssertStateHeld("_statusFlags");
        _statusFlags[key] = expiresAtTick;
    }

    private void ClearStatusFlags()
    {
        AssertStateHeld("_statusFlags");
        _statusFlags.Clear();
    }

    // Sum of every stat line across all worn gear. Equipment NEVER writes back into the character's base
    // stats (those stay in _char.*); the effective values the client sees are base + these, recomputed on
    // every SendStats / profile / attack. That keeps a relog — which reloads Equipment and redraws it via
    // RefreshInventory — from drifting or double-counting, since nothing was ever baked into the base.
    // Cached so the ~10-slot sum isn't recomputed on every SendStats / RegenTick / ApplyMobHit (×3) / Lua stat
    // read. Equipment changes rarely; InvalidateEquipTotals() clears it at each mutation site (equip/unequip/
    // break). NOT keyed on durability — EquipTotals sums def stat lines, which dura decay never touches.
    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam)? _equipTotals;
    private void InvalidateEquipTotals() => _equipTotals = null;

    // ARMOR SIGN — the one field in this tuple where "more" is WORSE. Every other slot is a straight bonus
    // (more might is more might); `armor` is an AC DELTA, and AC works the other way round: damage taken is
    // raw x (1 + ac/100), so MORE AC = MORE DAMAGE and -1 AC = 1% less. That is why every armor item in
    // Items.csv is NEGATIVE (spring garb -4) and why the handful of positive ones are real penalties
    // (wedding dress +30 — you wear it for the ceremony, not the fight). Gear, buffs (SpellParams.csv's
    // `armor` stat: bolster -4, pestilence +5) and mobs (mobs.csv MobArmor) all speak these same units, and
    // every consumer just ADDS this to _char.Ac. Nothing anywhere negates.
    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) EquipTotals()
    {
        if (_equipTotals is { } cached) return cached;
        int hp = 0, mp = 0, mt = 0, wl = 0, gr = 0, ar = 0, ht = 0, dm = 0;
        foreach (var e in _char.Equipment)
        {
            var def = Content.ItemById(e.ItemId); if (def is null) continue;
            hp += def.Vita; mp += def.Mana; mt += def.Might; wl += def.Will; gr += def.Grace;
            ar += def.Armor; ht += def.Hit; dm += def.Dam;
        }
        var t = (hp, mp, mt, wl, gr, ar, ht, dm);
        _equipTotals = t;
        return t;
    }

    // A weapon's real swing range, summed across worn gear like EquipTotals (RTK pc_calcstat sums
    // itemdb_minSdam/maxSdam/minLdam/maxLdam over every equip slot, same loop as Armor/Hit/Dam — it isn't
    // weapon-slot-only, though in practice only weapons carry nonzero values). Previously unparsed entirely
    // — Items.csv carries these columns but ItemDef never read them, so player melee had no real
    // damage-range component at all (see PlayerSwingDamage).
    //
    // BARE-HANDED IS S 1-2, NOT ZERO (live-measured 2026-08-15). A zero range is DETERMINISTIC, and every
    // unarmed sample we own shows exactly two adjacent damage values in a ~50/50 split — a level-65 rogue
    // reads 19/20 on a squirrel, 17/18 on a deer, 13/14 on fox and wolf. The endpoints were pinned by
    // cross-referencing an armed run: a military fork (S 90-100) on the same character produced a window of
    // 113-123, which fixes the non-weapon term to [9.00, 9.05), and the unarmed low roll then HAS to
    // contribute 0.5. Hence minS 1. Do NOT "simplify" this back to zero.
    private (int minSDam, int maxSDam, int minLDam, int maxLDam) WeaponTotals()
    {
        int minS = 0, maxS = 0, minL = 0, maxL = 0;
        foreach (var e in _char.Equipment)
        {
            var def = Content.ItemById(e.ItemId); if (def is null) continue;
            minS += def.MinSDam; maxS += def.MaxSDam; minL += def.MinLDam; maxL += def.MaxLDam;
        }
        if (maxS <= 0) { minS = 1; maxS = 2; }   // bare-handed
        else if (Content.PathIdForClass(_char.ClassName) == 3)   // Mage
        {
            // MAGE WEAPON PENALTY — live-measured 2026-08-17, absent from RTK.
            // A mage reads a weapon's S range at roughly a QUARTER of face value. The transform is
            // applied to the RANGE ENDPOINTS and then rolled uniformly, exactly as for any other class
            // (not applied per-roll — see below).
            //   wooden saber S 5-10  -> 2-3     viperhead S 15-25 -> 5-7     Staff of Power S 15-20 -> 5-6
            // Measured on a level 16 and level 18 mage vs squirrel (AC 100, x2.00 so no flooring) and
            // green squirrel (x1.90), n = 13 + 27 + 23. All three ranges land exactly on
            // floor((x+5)/4). Rival maps floor(x/4)+1 and ceil(x/4) both give 4 where 15 must give 5,
            // and are beaten 8700:1 and infinitely (ceil cannot produce the 8s the staff run showed).
            // ENDPOINT-TRANSFORM vs PER-ROLL-TRANSFORM: both produce these same three ranges, but they
            // differ in the DISTRIBUTION inside the range. Endpoint-then-uniform fits better on all
            // three weapons (total LL -54.62 vs -55.99, ~4:1) and is the more plausible implementation.
            // NOT an off-path gate: the Staff of Power is ItmPthId 3 — the mage's OWN path weapon — and
            // is scaled identically. It is a flat property of the class.
            // Only the S range is affected; the item's Dam/Might/etc lines are untouched, and an
            // unarmed mage keeps the normal 1-2 (no weapon, so no weapon transform).
            // POET (PathId 4) IS UNTESTED — deliberately not included. Do not extend without measuring.
            minS = (minS + 5) / 4;
            maxS = (maxS + 5) / 4;
        }
        return (minS, maxS, minL, maxL);
    }

    /// <summary>Drop the weapon enchant (Ingress &amp; kin) if the item leaving the body is the weapon.
    /// The enchant is bound to the weapon it was cast on — "until the weapon is taken off or person log
    /// off" — so EVERY path that takes a weapon off funnels through here: the 0x1F single-slot unequip,
    /// the equip-over-a-worn-slot SWAP (the client's `w`+letter is a swap, not an unequip-then-equip, so
    /// hooking only the unequip opcode would miss the common case), typed-"A" bulk unequip, and a
    /// durability break. Cheap no-op when nothing is enchanted, so callers don't gate it.</summary>
    private void DropEnchantIfWeapon(ItemDef? def)
    {
        if (def is not null && def.EquipSlot == 1) DropWeaponEnchant();
    }

    /// <summary>True when any equipped item sits in the weapon slot. Only feeds the Warrior's +2 Dam
    /// bonus below, which is conditional on being armed at all.</summary>
    private bool HasWeaponEquipped()
    {
        foreach (var e in _char.Equipment)
        {
            var def = Content.ItemById(e.ItemId);
            if (def is not null && def.EquipSlot == 1) return true;
        }
        return false;
    }

    // RTK swingDamage.lua's per-class flat bonus (_classFactors, 1-indexed by baseClass+1): only Warrior
    // and Rogue get one; Peasant/Mage/Poet don't (magic users deal their real damage through spells, not
    // melee). pathId -1 (no class chosen yet) falls through to the Peasant case.
    /// <summary>SUPERSEDED 2026-08-16 — see ClassFactor below. The "measured at 0" conclusion was
    /// correct only for the LOW-LEVEL band it was taken in (a lvl 13-18 rogue at might 12); the term is
    /// near zero there and climbs with level, reaching 5.5 on a lvl-65 rogue. Kept for the derivation.
    /// ORIGINAL NOTE: Flat path bonus added to the raw swing. MEASURED AT 0 (2026-08-02) — the
    /// 9 (Warrior) / 7.5 (Rogue) from Rogue Tutor Melalye's post does not apply in this era.
    /// Derivation: with mob AC known independently (the Spark fixed-damage probe, base ~55.5),
    /// <c>K = observed/(1+ac/100) - (s/2 + dam*2.5 + might/8)</c> must be CONSTANT across mobs
    /// since it's a property of the attacker. Across five mobs spanning AC 65-95 it came out
    /// -0.71/-0.71/-0.62/-0.41/-0.31 (mean -0.55; +0.5 integer-floor bias => ~0), i.e. the rest
    /// of the formula is exact and there is no room for a +7.5 term. Two contaminants had to be
    /// removed first, both of which fake a positive K: the positional x2 (flee-prone mobs turn
    /// their backs, so Mouse showed 22% doubled hits vs Bat's 6%) and mob-label/look mixing.
    /// WHY IT MATTERED: this is a FLAT add, so at level 1 it dwarfs everything else — a wooden
    /// saber's whole raw swing is ~6.6, so +9 was +136% and one-shot an 18hp squirrel. By level
    /// 99 it's noise. That asymmetry is exactly why the game felt wrong only in the early game.
    /// Rogue is measured; WARRIOR IS INFERRED — no warrior data exists, but its 9 comes from the
    /// same post as the disproven 7.5, so it is not evidence either. Re-measure with a warrior.</summary>
    // Per-path bonus added to the raw swing. RTK's swingDamage.lua carries a FLAT table
    // (_classFactors = {0, 9, 7.5, 0, 0} — warrior 9, rogue 7.5, everyone else 0) and Rogue Tutor
    // Melalye's board post gives the same two numbers independently. Both are right about the VALUES and
    // wrong about the SHAPE: applied flat, +9 predicts deer 45-54 for a level-28 warrior who actually
    // hits for 28-37 (~60% high), and at level 1 it one-shots an 18hp squirrel. The term is ~0 in the
    // early game and climbs with LEVEL (not with might — proven by holding a lvl-65 rogue's level fixed
    // and walking might 35 -> 42: the damage moved by exactly the MightTerm step and nothing more).
    //
    // DELIBERATELY A LOOKUP, NOT A FORMULA. Four successive shapes were fitted and each was falsified by
    // the next measurement: linear-to-99, saturating-at-90, sublinear, and finally plain monotonicity.
    // The measured points below are each pinned to +/-0.05 by the collision method (see MightTerm) and
    // are exact; the interpolation between them is a placeholder. Note they sit on the same 0.5 grid as
    // MightTerm, so this likely STEPS rather than slopes — resolve that before inventing a fifth curve.
    //
    // NOT INCLUDED: warrior level 20, measured at 0.78. It is the only reading where two weapons on one
    // character disagreed (fork [2.778,2.833] vs viperhead [2.723,2.777]) and the only one that breaks
    // monotonicity; every other level was confirmed by 2+ independent runs that agreed exactly. Treated
    // as contaminated. Re-measure before trusting it.
    //
    // The level-99 entries are NOT measured — they are RTK's table plus the board post, on the assumption
    // those are endgame readings. Everything below level 28 (warrior) / 65 (rogue) is real data.
    // The classes do NOT share a curve: no single spacing reproduces both warrior lvl28 = 0.5 and rogue
    // lvl65 = 5.5. Keep them separate.
    // MEASURING THIS: pick the mob by whether it SEPARATES the candidates, not by raw precision. Deer has
    // the tightest signature (+/-0.028) but at level 30 both cf=0.17 and cf=0.5 produce the identical
    // 28-37 window there, so the answer hides in which value collides — and 91 deer swings still tied
    // three ways. Fox (ded 1.4) shifts the WINDOW between those two, and 33 swings settled it. Check that
    // the windows differ before farming; endpoints need ~40 samples, a collision needs ~150.
    // MEASURED BANDS (what the data permits, not point estimates):
    //   lvl16 [0.000, 0.055] n=67 viperhead+unarmed | lvl25 [0.500, 0.571) n=60 fox, wolf agrees
    //   lvl28 [0.500, 0.555] viperhead AND fork identical | lvl30 [0.214, 0.611) loosest
    //   lvl32 [0.429, 1.143) window, collision picks 0.50 | lvl35 [1.000, 1.054] window AND collision
    // IT IS A STAIRCASE, NOT A RAMP — it steps in 0.5s, the same quantum as MightTerm. Two step
    // boundaries are now bracketed: 0 -> 0.5 within levels 17-25, and 0.5 -> 1.0 within levels 33-35.
    // Flat 0.50 across 25-32 (four readings). Interpolation between measured levels should really be a
    // step, not a line, but until the boundaries are pinned to a single level the difference is under
    // half a damage point. lvl30 is the loosest reading; it sits between clean 0.50s either side.
    // NOTE: a `WarriorDamFlipLevel` constant used to live here, for a level at which a warrior's own
    // Dam line supposedly stepped -2 -> 0. It does not exist — see the long note in PlayerSwingDamage.
    // The bracket kept moving (11, 15, 16, 23) because it was fitting the seam between two different
    // characters, one of which was running with stale stats from a live-server bug. Do not re-add it.

    /// <summary>A STAIRCASE in 0.5s, level-driven. Warrior/Rogue step, and the step LEVELS are irregular.
    ///
    /// MAGE IS NOT FLAT 0 — measured 0.5 at level 16 (see MageClassFactor). POET is still untested and
    /// still returns 0; do not assume it is 0 just because we ship 0. RTK's flat {0, 9, 7.5, 0, 0} is
    /// NOT evidence for either — it is the same source whose warrior/rogue entries we already proved to
    /// be endgame constants rather than per-level behaviour.
    /// See docs/common/Melee-Damage.md "The mage anomaly".
    ///
    /// A LOOKUP, NOT A FORMULA — and that is a considered decision, not laziness. A uniform-period fit
    /// was derived and committed on 2026-08-16: the first step is exactly level 8 (lvl7 reads 0.0 and
    /// lvl8 reads 0.5, adjacent), and with steps 2 and 3 then bracketed to 17-25 and 33-35, only
    /// period 13 fits both, giving 8/21/34. It reproduced all eleven readings known at the time.
    /// A level-18 run FALSIFIED it the same day: cf is already 1.0 at 18, so step 2 is at 17 or 18, not
    /// 21. The gaps are 9-10 then 15-17 — they GROW. Five ramp/period shapes have now been fitted and
    /// killed in turn; do not fit a sixth without a measurement in every gap it spans.
    ///
    /// WARRIOR — measured at levels 5,6,7 (0.0), 8,9,14,15,16 (0.5), 18,19,25,28,30,32 (1.0), 35 (1.5).
    /// Steps: #1 EXACTLY 8. #2 in 17-18. #3 in 33-35. Nothing above 35 is measured; the table holds at
    /// 1.5 rather than extrapolating, because the gap growth makes extrapolation guesswork.
    /// ROGUE — lvl~15 = 1.0 (early, low precision), lvl18 and lvl19 = 1.0 (mined out of re/auto/swings.csv,
    /// see below), lvl65 = 6.0. Its step SIZE has still never been observed. The interpolation between 19
    /// and 65 is a placeholder; only the endpoints are real.
    /// The lvl18/19 readings are the first ADJACENT rogue levels ever measured. Bands:
    ///   lvl18 cf in [0.87, 1.14)  — green squirrel/novice sword INTERSECT big bat/swift sword, n=80+86
    ///   lvl19 cf in [0.87, 1.34)  — green squirrel/novice sword, n=76
    /// CAUTION: those bands are ~0.3 wide, so they do NOT establish that the rogue is FLAT across 15-19.
    /// A smooth ramp of ~0.10/level fits both bands too (1.10 at 18, 1.21 at 19) — and a ramp of exactly
    /// that slope is what it takes to reach the measured 6.0 at level 65. Do not quote "rogue is flat
    /// through 19" as a finding; it is only "cf is within 0.3 of 1.0 at 18 and 19".
    ///
    /// RTK/board warrior 9 / rogue 7.5 are NOT encoded as level-99 anchors. They are real constants
    /// from two independent sources, but flat in RTK, and nothing measured here is climbing toward them
    /// at a rate that would arrive by 99.
    ///
    /// Values read 0.5 higher than the raw measurements quoted elsewhere in this file: MightTerm's
    /// offset moved -0.5 -> -1.0 (pinned by a level-1 peasant) and this absorbed the same 0.5, so
    /// warrior/rogue damage is bit-identical and Peasant lands on exactly 0.</summary>
    private static readonly (int Level, double Cf)[] WarriorClassFactor =
        { (1, 0.0), (7, 0.0), (8, 0.5), (16, 0.5), (18, 1.0), (32, 1.0), (35, 1.5), (36, 1.5), (37, 1.5), (38, 1.5) };
    /// <summary>ROGUE — MEASURED: 0.0 @5, 0.5 @7, 1.0 @18 and @19, 6.0 @65.
    ///
    /// !! THE ROGUE IS NOT ON THE WARRIOR'S LADDER. Rogue step #1 is at EXACTLY 7, warrior step #1 at
    /// EXACTLY 8 — both pinned on adjacent-level pairs with saturated windows:
    ///   rogue   lvl6 = 0.0 (n=28)   lvl7 = 0.5 (n=26)
    ///   warrior lvl6 = 0.0 (n=23)   lvl7 = 0.0 (n=15)   lvl8 = 0.5 (n=24)
    /// The warrior half was re-run on a FRESH RELOGGED character and agrees with the original, which
    /// also proves the stale-stat bug hit base Dam ONLY and never touched classFactor. The "one shared
    /// level ladder for all classes" idea is dead; do not resurrect it without explaining level 7.
    ///
    /// STEP #1 IS PINNED TO EXACTLY LEVEL 7 — level 6 measures 0.0 (n=28, window 4-9, chi2 2.00/5df)
    /// and level 7 measures 0.5 (n=26, window 5-10), adjacent levels, both windows saturated. The
    /// rogue steps exactly ONE level before the warrior, whose step #1 is at 8. Step #2 is bracketed to a wide 8-17 and the table assumes 18, which is the
    /// least-invented choice — but an older, low-precision reading put the rogue at ~1.0 by level 15,
    /// which a rogue-runs-earlier ladder would make CORRECT. Levels 15/16 are the pending test.
    /// Everything between 19 and 65 is a placeholder ramp; only the endpoints are real.</summary>
    private static readonly (int Level, double Cf)[] RogueClassFactor =
        { (1, 0.0), (6, 0.0), (7, 0.5), (17, 0.5), (18, 1.0), (19, 1.0), (65, 6.0) };
    /// <summary>TWO MEASURED POINTS: level 16 = 0.5, level 18 = 1.0. Both live, unarmed, vs a green
    /// squirrel (x1.90) and cross-checked at 16 on a plain squirrel (x2.00). Might was 9 then 10 —
    /// BOTH in the 8-11 mightTerm band, so mightTerm is 0.0 in both runs and the change is PURE cf
    /// with no confound.
    ///
    /// !! THE MAGE STEPS AT THE SAME LEVELS AS THE WARRIOR. Mage 0.5@16 -> 1.0@18 and warrior
    /// 0.5@16 -> 1.0@18 are the same step in the same 17-18 window. At level 18 all three measured
    /// classes read 1.0 (warrior, rogue, mage). See docs/common/Melee-Damage.md "Is classFactor
    /// class-independent?" — if it is, these per-class tables collapse into one level ladder and RTK's
    /// per-class constants are late/subpath artifacts. NOT yet assumed: the tables stay separate until
    /// a mid-level rogue reading tests it.
    ///
    /// The step at level 8 is BORROWED FROM THE WARRIOR, not measured. Levels 4-15, 17 and everything
    /// above 18 are unmeasured for a mage. Treat 8 as a guess.</summary>
    private static readonly (int Level, double Cf)[] MageClassFactor =
        { (1, 0.0), (7, 0.0), (8, 0.5), (16, 0.5), (18, 1.0) };

    /// <summary>classFactor at level 99, per class — the endpoint the ramp above the last measured knot
    /// climbs toward, then holds past 99. Warrior 9 and Rogue 7.5 are KNOWN: RTK's swingDamage.lua
    /// _classFactors flat per-class bonus ({0, 9, 7.5, 0, 0}); we treat them as the level-99 saturation
    /// value rather than a flat-from-level-1 constant, since low-level readings measured well below them.
    /// Mage's RTK entry is 0 (known wrong — mage measured 1.0 by level 18), so its 99 target is still a
    /// PLACEHOLDER guess to be tuned as high-level mage readings land.</summary>
    private const double WarriorCf99 = 9.0;
    private const double RogueCf99   = 7.5;
    private const double MageCf99    = 3.0;

    private static double ClassFactor(int pathId, int level)
    {
        (int Level, double Cf)[] pts;
        double cf99;
        switch (pathId)
        {
            case 1: pts = WarriorClassFactor; cf99 = WarriorCf99; break;
            case 2: pts = RogueClassFactor;   cf99 = RogueCf99;   break;
            case 3: pts = MageClassFactor;    cf99 = MageCf99;     break;
            default: return 0;                           // Peasant measured flat 0; POET UNTESTED
        }
        if (level <= pts[0].Level) return pts[0].Cf;
        for (int i = 1; i < pts.Length; i++)
        {
            if (level > pts[i].Level) continue;
            var (l0, c0) = pts[i - 1];
            var (l1, c1) = pts[i];
            return c0 + (c1 - c0) * (level - l0) / (double)(l1 - l0);
        }
        // Above the last MEASURED knot: ramp linearly to the placeholder (99, cf99), then hold.
        var (lastL, lastC) = pts[^1];
        if (lastL >= 99) return lastC;                   // last reading already at/past 99: hold it
        if (level >= 99) return cf99;                    // at/above 99: hold the placeholder max
        return lastC + (cf99 - lastC) * (level - lastL) / (double)(99 - lastL);
    }

    /// <summary>The Might contribution to a raw swing. TWO REGIMES, because the published formula is a
    /// local linearization (see nexustk-published-formulas-are-endgame-fits):
    /// <list type="bullet">
    /// <item>Low might: <c>might/8</c> with NO intercept. LIVE-MEASURED at might 12 — the non-weapon term
    /// falls in [1.368, 1.842) and might/8 = 1.500 lands inside. (The archive's other low-might rule,
    /// "1 damage per 5 Might", gives 2.4 and is OUTSIDE that bracket, so it is not the low-end law.)</item>
    /// <item>High might: <c>might/8 + 8.8125</c>, the Klanx/Yari nmail formula, explicitly qualified
    /// "For Mights of 70 or greater". At might 130 this gives 25.1, reproducing the archive's worked
    /// example of 26 — which is what proves 8.8125 is a tangent-line INTERCEPT, not a flat bonus.</item>
    /// </list>
    /// No single line fits both ends (a through-origin fit needs slope ≤0.1535 at might 12 but 0.2 at 130),
    /// so the two regimes are real, not a measurement artifact. The 40→70 ramp between them is pure
    /// INTERPOLATION — we have zero data in that band and no source describes the transition. It is chosen
    /// only to be continuous at both ends and to keep the intercept away from the measured low-might
    /// bracket (starting the ramp at 0 would give might*0.25 = 3.0 at might 12, outside it).
    /// Applying the intercept unconditionally is what one-shot starter mobs; omitting it entirely
    /// under-powers endgame by ~35% of the Might term. Re-measure once a character reaches might 40+.</summary>
    /// <summary>SUPERSEDED 2026-08-16. The two-regime story above was an artifact of never having
    /// measured a step boundary: the term is QUANTIZED, not sloped, and the 8.8125 "intercept" was almost
    /// certainly the warrior's flat 9 from RTK's classFactor table leaking into a might-only fit.
    /// Live-measured: +0.5 for every 4 points of Might, stepping at multiples of 4.
    ///
    /// STEP POSITIONS confirmed at might 16, 28, 32, 36 and 40 across two classes. The clinching evidence is
    /// the FLAT stretches — a level-65 rogue reads identical damage at might 37, 38 and 39, which no
    /// continuous term can produce, and a level-25 warrior reads identical damage with a sword at might
    /// 16 and 19. STEP SIZE confirmed at 0.50 (not 0.33) by a fixed-level sweep on that rogue: at might
    /// 39 with a military fork the observed collision set was {81,84,86}, chi2 3.41 vs 10.28 for the
    /// 0.33 alternative, the tell being a damage value seen ONCE where 0.33 needs it doubled.
    ///
    /// THE -1.0 OFFSET IS FIXED BY A LEVEL-1 PEASANT, not chosen. A peasant has classFactor 0 (RTK's
    /// table, and nothing else applies at level 1), so a peasant swing measures MightTerm directly with
    /// no other unknown. Live: might 3, wooden saber S 5-10, Dam 0, vs an AC-100 squirrel gives a window
    /// of 3-8 over n=25. Squirrel's x2 deduction makes dmg = s + 2M exactly, so 2M = -2 and M = -1.00.
    /// It was -0.5 until 2026-08-16, which put every Peasant/Mage/Poet swing one damage high; warriors
    /// and rogues were unaffected because their ClassFactor entries absorbed the same 0.5.
    /// Equivalent to (floor(might/4) - 2)/2.</summary>
    private static double MightTerm(double might) => Math.Floor(might / 4.0) / 2.0 - 1.0;

    // The player's real melee formula (RTK swingDamage.lua _getPlayerSwingDamage + the shared armor/
    // positional resolution in swingDamage() itself), replacing the old flat EffMight-based stand-in.
    // Returns the final damage AND whether it crit (for the 0x13 visual byte at the call site).
    //   s               = weapon's Small swing range. The Large (L) range is INERT in the real 4.95 game —
    //                      every live-server tutor post says only S is used ("L makes no difference on large
    //                      enemies, only S is working currently"), so we never read minLDam/maxLDam.
    //   dam/might        = gear/buff Dam total and effective Might, each floored at 1
    //   classFactor      = ClassFactor above
    //   enchant          = EffEnchant — multiplies ONLY the raw weapon-swing term (s/2), 1 normally, up to
    //                      9x (Spirit Blade) while an enchant tier is active (Session.CastEnchant). The
    //                      ladder is archive-corroborated — see Content.EnchantSpells for sources+caveats.
    //   rage             = EffRage — 1 normally, up to 5x while a Fury tier is active (Session.CastRage)
    //   invisible        = 5 while Stealthed (Session.CastStealth), else 1 — a one-shot sneak-attack burst
    //                      (tswolf 8/2001, era-matched to the 4.95 client: "Invisible increases attack by 5
    //                      times"; later boards' 9x is a post-4.95 rebalance); landing this hit strips the
    //                      stealth immediately after (RTK "attacking breaks it")
    //   critical         = 3 on a crit (Combat.RollPlayerSwingRtk's 3% roll), else 1
    // The hit/crit roll (Combat.RollPlayerSwingRtk) happens FIRST: a failed roll is
    // a genuine MISS and returns (0, false) — the caller renders a whiff and a miss deals no damage, deducts
    // no durability, and does NOT break stealth (RTK drops invis only on a NONZERO hit). Then, on a landed
    // hit: armor deduction against the TARGET's Ac (mob-target floor -95), then ONE positional bonus of at
    // most x2 (armor BEFORE position, as swingDamage.lua orders it) — "attacked from behind while both face
    // the same way", Combat.IsBehindTarget, always live. RTK's Lua ran backstab/flank as separate sequential
    // if-blocks that could compound; we do NOT, because no source supports stacking and the board post gives
    // behind-x2 as a single universal rule. Backstab/Flank no longer multiply at all — they RETARGET, at
    // reduced damage, via the `reach` scale below (see Session.SwingTargets).
    //   reach = 1.0 on an ordinary faced-tile swing; 0.5 when Backstab reaches the rear tile or Flank
    //           reaches a side one. Applied LAST, after armor and after the positional x2, because the
    //           source states it as a fraction of "the front attack damage" — i.e. of the whole result.
    //           Spell-driven attacks (lethal strike etc.) always pass the default 1.0.
    // The defender stats a player swing needs, abstracted so a MOB and a PLAYER both feed the ONE live-validated
    // formula below (no parallel PvP copy to drift). grace/level feed the hit+crit roll; Ac + ArmorFloor the
    // armor mitigation (mob floor -95 = RTK minimumArmor for a mob; player floor -80 = RTK's human floor, same
    // as ApplyMobHit); X/Y/Dir the positional rear-x2. Of(Session) folds in the target's gear/buff armor+grace
    // exactly as ApplyMobHit computes a player's effective defense.
    private readonly record struct SwingTarget(int Ac, int ArmorFloor, int Grace, int Level, int X, int Y, byte Dir)
    {
        public static SwingTarget Of(Mob m) => new(m.Ac, -95, m.Grace, m.Level, m.X, m.Y, m.Dir);
        public static SwingTarget Of(Session s) => new(
            s._char.Ac + s.Totals().armor, -80, s._char.Grace + s.Totals().grace, s._char.Level,
            s._char.X, s._char.Y, (byte)(s._facing & 3));
    }

    private (int dmg, bool crit) PlayerSwingDamage(SwingTarget target, double reach = 1.0)
    {
        var eq = Totals();
        int pathId = Content.PathIdForClass(_char.ClassName);
        double might = Math.Max(EffMight, 1);

        // Hit/crit roll first — a miss is a real whiff (0 damage). LIVE-VALIDATED clamped-RTK grace/level
        // formula (Combat.RollPlayerSwingRtk): hit chance keys off the LEVEL+GRACE gap, NOT AC. AC stays a
        // pure damage-reduction term below (ApplyArmor). Replaced the old AC-based Astrael regression, which the
        // 7.x combat tap proved wrong (it hit ~88-100% vs mobs where live is ~50-63%). pathId/HitBase/might
        // no longer feed hit chance — kept only for the damage terms.
        var outcome = Combat.RollPlayerSwingRtk(_char.Grace + eq.grace, _char.Level, _char.Hit + eq.hit,
                                                target.Grace, target.Level);
        if (outcome == Combat.SwingOutcome.Miss) return (0, false);
        bool crit = outcome == Combat.SwingOutcome.Crit;

        var w = WeaponTotals();
        int lo = w.minSDam, hi = w.maxSDam;   // S only — L is inert in 4.95 (see formula note above)
        int s = lo >= hi ? lo : Random.Shared.Next(lo, hi + 1);
        // NO floor to 1. LIVE-MEASURED 2026-08-02: a level-1 peasant with a 0-dam wooden saber
        // (S 5m10) hits an AC-100 squirrel for 4-8. The floored term alone contributes 2.5 raw
        // = 5 damage after that mob's x2 armor — more than half the observed hit, and it pushed
        // the predicted range to 10-14. A 0-dam weapon must contribute 0.
        // The term stays LINEAR (dam * 2.5) rather than the step floor(dam/2)*5 that "every 2
        // dam adds 5 more damage" also permits: the level-18 rogue data (Swift sword, dam=1)
        // only reconciles to classFactor ~0 if dam=1 contributes 2.5, not 0. So the rate reading
        // is right and it was only the artificial floor that was wrong.
        double dam = eq.dam;
        // WARRIOR WEAPON BONUS: equipping ANY weapon adds a flat +2 on top of the item's own Dam
        // line, at EVERY level, from the moment the character joins the Warrior path. The warrior's
        // own base Dam is 0, same as everyone else's. Absent from RTK's swingDamage.lua.
        //
        // The +2 is pinned by the client's own stat readout on ONE character at level 20, equipping two
        // weapons back to back: military fork gave item Dam +1 and CHARACTER Dam 3; viperhead woodsaber
        // gave item Dam +0 and character Dam 2. Items.csv agrees on both item lines (ItmDam 1 and 0),
        // so the character total is item Dam + 2 in both cases. The bonus lives on the character, not
        // on the item — the sword of power's tooltip is +1 might / +1 hit / +10 vita and no Dam at all,
        // matching its row exactly, yet a warrior wearing it reads character Dam 2.
        // Damage corroborates: a lvl15 warrior with the sword of power hits a wolf for 23-27, which
        // needs effective Dam 2 (Dam 0 predicts 16-20).
        //
        // THERE IS NO LEVEL-BASED FLIP. A `WarriorDamFlipLevel` constant lived here for a day, on the
        // theory that base Dam stepped -2 -> 0 somewhere in levels 20-28. It was an artifact of
        // splicing TWO characters' runs at the seam between them: warrior #1 supplied levels 15-35 and
        // warrior #2 supplied levels 1-19, and the boundary between the characters was read as a
        // boundary between levels. Both characters were measured at level 15 with the SAME weapon and
        // came out ten damage apart (#2: green squirrel 21-26, n=45, needs Dam 0; #1: wolf 23-27,
        // needs Dam 2), which no level rule can produce.
        //
        // Warrior #2 was running on a LIVE-SERVER BUG, not a different rule: base Dam and base AC are
        // stored stats that the real 4.95 server only recomputes when the character loads, so a
        // character who joins the Warrior path mid-session keeps the peasant-era values until they log
        // out and back in — and the server SWINGS with the stale value, it is not merely a display
        // fault. That is why warrior #2's sheet read -2 base / 0 equipped for fifteen levels and its
        // damage agreed with the sheet. We deliberately do NOT reproduce that bug.
        //
        // Warrior #2's runs remain valid evidence for MightTerm and ClassFactor: Dam was genuinely 0
        // throughout them, which is what those fits assumed. Only the Dam conclusion was wrong.
        if (pathId == 1 && HasWeaponEquipped()) dam += 2;
        // FLOOR AT ZERO. RTK has math.max(player.dam, 1); we deleted that outright when a level-1
        // peasant with a 0-dam weapon measured as contributing 0, not 2.5. Kept at 0 as a defensive
        // floor against negative-Dam gear; with the flip gone nothing in the live data reaches it.
        dam = Math.Max(0, dam);
        double classFactor = ClassFactor(pathId, _char.Level);
        bool wasStealthed = Stealthed;   // read once — landing the hit clears it below

        double swing = (s / 2.0 * EffEnchant + dam * 2.5 + MightTerm(might) + classFactor) * EffRage * (wasStealthed ? 5 : 1) * (crit ? 3 : 1);
        // Pass the RAW DOUBLE into ApplyArmor — do NOT truncate here. Flooring twice (once to int, once
        // inside ApplyArmor) is disproven live; see the ApplyArmor doc comment for the n=178 evidence.
        int dmg = Combat.ApplyArmor(swing, target.Ac, floor: target.ArmorFloor);   // -95 mob / -80 player (see SwingTarget)
        // POSITIONAL BONUS — AT MOST x2, EVER. This is deliberately ONE decision feeding ONE multiply
        // rather than the old pair of independent `if (...) dmg *= 2;` lines, which could in principle
        // compound to x4. They never actually did (IsBehindTarget requires attackerDir == targetDir while
        // IsBackstabAngle requires the OPPOSITE facing, so the two are mutually exclusive), but nothing
        // stated or enforced that — one edit to either table would have silently produced x4.
        //
        // NOTE the direction passed is Combat.AttackDir, NOT _facing: with the Flank stance the blow lands
        // on a SIDE tile, and these rules are about the geometry of the blow. On an ordinary swing the two
        // are identical, so this changes nothing outside flank — but it is what makes "flank works the same
        // as turning and swinging" actually true, including earning the rear x2 off a turned back.
        //
        // The Backstab/Flank STANCES are deliberately absent from this decision. They are targeting spells
        // (Session.SwingTargets) — "Strikes an enemy behind you" / "Enables to Attack to the Warrior's Sides"
        // — not damage ones, and Combat.IsBackstabAngle/IsFlankAngle are retired. Do not re-add them here.
        byte attackDir = Combat.AttackDir(_char.X, _char.Y, target.X, target.Y);
        if (Combat.IsBehindTarget(attackDir, target.Dir, _char.X, _char.Y, target.X, target.Y)) dmg *= 2;

        // Reach penalty LAST — the sources state it as a fraction of "the front attack damage", i.e. of the
        // finished number, so it comes after armor and after the positional x2. Still floored at 1: a
        // connected hit never deals 0 (that is what a MISS is, and it returned above).
        if (reach != 1.0) dmg = Math.Max(1, (int)Math.Floor(dmg * reach));

        if (wasStealthed) { _stealthUntil = 0; RevertStealth(); }   // RTK: landing a hit strips stealth (removeDuras(invis)) — drop the faded look now

        return (dmg, crit);
    }

    // Active timed stat buffs (from casting Buff spells). Session-local, like cooldowns — they clear on relog.
    // Each carries the stat it boosts, the amount, and the tick it expires at. Expired ones are pruned on read.
    // Category groups a status into a MUTUALLY-EXCLUSIVE family (RTK spellTables.lua: curses/venoms/minorcurses/
    // protections). Positive buffs leave it "". Only one status per non-empty category can be active at once — a
    // curse spell is blocked if the target already has one of the same category (that guard is the whole point of
    // self-pestilence: occupy your own 'curses' slot with a mild curse so an enemy can't land a worse one). Cure
    // spells remove statuses BY category. See nexustk-495-curse-status-system.
    private sealed class ActiveBuff { public string Stat = ""; public int Amount; public long Expires; public string Key = ""; public string Name = ""; public string Category = ""; }
    private readonly List<ActiveBuff> _buffs = new();

    // ---- the only writers of _buffs (#29) ------------------------------------------------------------
    // This list is the worked example in the ticket: the tick thread removes from it (ExpireBuffs), the read
    // loop adds to it (eighteen sites in Session.Spells.cs), and the autosave thread enumerates it
    // (CaptureTimedEffects). Every write funnels through these four so a Debug build fails loudly on any
    // path that reaches them without the state monitor, instead of losing an entry in silence. READS are
    // deliberately not funnelled — a torn read of a list of value-ish records shows a stale total for one
    // frame, where a torn write loses a buff for good.
    private void BuffAdd(ActiveBuff b)
    {
        AssertStateHeld("_buffs");
        _buffs.Add(b);
    }

    private int BuffRemoveAll(Predicate<ActiveBuff> match)
    {
        AssertStateHeld("_buffs");
        return _buffs.RemoveAll(match);
    }

    private void BuffRemoveAt(int index)
    {
        AssertStateHeld("_buffs");
        _buffs.RemoveAt(index);
    }

    private void BuffClear()
    {
        AssertStateHeld("_buffs");
        _buffs.Clear();
    }

    // ---- the only writers of the worn-gear list (#29) ------------------------------------------------
    // Same shape as _buffs and the same reason: equipping happens on the read loop, per-hit durability
    // (DeductDura) can happen on the tick thread through ApplyMobHit, and the autosave thread serializes the
    // list. `foreach (var worn in _char.Equipment.ToArray())` in TakeDamage is the copy that used to be the
    // only thing standing between a mob swing and a "collection was modified" mid-save.
    private void EquipAdd(InvItem worn)
    {
        AssertStateHeld("_char.Equipment");
        _char.Equipment.Add(worn);
    }

    private void EquipRemove(InvItem worn)
    {
        AssertStateHeld("_char.Equipment");
        _char.Equipment.Remove(worn);
    }

    private void EquipClear()
    {
        AssertStateHeld("_char.Equipment");
        _char.Equipment.Clear();
    }

    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) BuffTotals()
    {
        long now = Environment.TickCount64;
        int hp = 0, mp = 0, mt = 0, wl = 0, gr = 0, ar = 0, ht = 0, dm = 0;
        // Don't remove expired buffs here — Session.ExpireBuffs (RegenTick) is the single removal point (so the
        // fade line fires exactly once); just skip any that have lapsed but aren't swept yet.
        foreach (var b in _buffs) { if (b.Expires <= now) continue; switch (b.Stat)
        {
            case "hp": case "maxhp": hp += b.Amount; break;
            case "mp": case "maxmp": mp += b.Amount; break;
            case "might": mt += b.Amount; break;
            case "will":  wl += b.Amount; break;
            case "grace": gr += b.Amount; break;
            case "armor": ar += b.Amount; break;
            case "hit":   ht += b.Amount; break;
            case "dam":   dm += b.Amount; break;
        } }
        return (hp, mp, mt, wl, gr, ar, ht, dm);
    }

    // Gear + active timed buffs: the full bonus layered on the character's base stats. Everything the client
    // sees (HUD, profile) and every derived calc (heals, melee) reads through this so buffs are reflected live.
    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) Totals()
    {
        var e = EquipTotals(); var b = BuffTotals();
        return (e.hp + b.hp, e.mp + b.mp, e.might + b.might, e.will + b.will,
                e.grace + b.grace, e.armor + b.armor, e.hit + b.hit, e.dam + b.dam);
    }

    // Effective (base + gear + buffs) caps/attributes used by the HUD, heals and melee. AC is signed and LOWER
    // is better in TK; gear/buff armor is an AC delta in those same units, so it simply ADDS (see EquipTotals).
    private uint EffMaxHp => (uint)Math.Max(1, (int)_char.MaxHp + Totals().hp);
    private uint EffMaxMp => (uint)Math.Max(0, (int)_char.MaxMp + Totals().mp);
    private int  EffMight => Math.Clamp(_char.Might + Totals().might, 0, 255);

    /// <summary>Path/class + subpath-rank restriction on USING an item at all — worn or swallowed (RTK
    /// <c>pc_useitem</c>, pc.c:1998).
    /// <para><b>Why here and not in <see cref="EquipFromSlot"/>:</b> RTK splits its item gates by what they are
    /// ABOUT, not by opcode. Path and mark are properties of the item's own identity, so they sit on the use
    /// funnel and cover consumables as well as gear — 17 non-equip items carry an ItmPthId (a Rogue's stealth
    /// powder/explosives, a Warrior's shard and shatter bombs, holy dust, and the eight rogue darts; none are
    /// ItmThrown, so the use path really is how they're spent). Level/might/sex, the 2-handed-vs-shield pairing
    /// and the cursed-stat floor are instead about putting a thing ON YOUR BODY — they need the equipment slots
    /// to reason about — so those stay in <c>pc_canequipitem</c>/EquipFromSlot. Collapsing the two would either
    /// leak body-slot checks onto food or drop the class gate off half the items that carry one.</para>
    /// ItmPthId 0 means anyone may use it (2058/2545 items, so most content is unrestricted); 1..5 names a BASE
    /// path and is satisfied by every subpath under it (a Chung ryong still counts as a Warrior — RTK compares
    /// <c>classdb_path</c> of the player's class, not the class id itself); 6+ names ONE exact subpath class. A
    /// restricted item additionally requires the player's subpath mark to have reached ItmMark.
    /// Dreamweavers/Archons (base path 5) skip the whole check, matching RTK's `classdb_path(class) == 5`
    /// branch. GM accounts do NOT (removed 2026-08-07): the bypass made the gate look broken to the only
    /// account actually used for testing, and the sibling wear gates below — sex, level, might — never had
    /// one either, so a GM was already refused a female-only robe while walking off in a Rogue's waistcoat.
    /// <para>469 of the 1241 wearable items carry a path restriction and 146 a mark — none of it was enforced
    /// before (ItmPthId/ItmMark weren't even parsed), which is why a Poet could wear a Warrior's sword.</para>
    /// Refusals are RTK's own <c>map_msg</c> lines, sent as system minitext like every other use/equip refusal.
    /// <para>Deliberately NOT applied to the throw path (0x17): RTK's <c>clif_parsethrow</c> doesn't route
    /// through pc_useitem and has no path check either, and no path-restricted item is ItmThrown anyway.</para></summary>
    private bool CanUsePath(ItemDef def)
    {
        if (def.PathId == 0) return true;                       // unrestricted
        int myClass = CharClassId;                              // -1 when ClassName matches no Paths row
        int myBase  = myClass < 0 ? 0 : Content.PathBaseOf(myClass);
        if (myBase == 5) return true;                           // Dreamweaver/Archon
        bool ok = def.PathId < 6 ? myBase == def.PathId : myClass == def.PathId;
        if (!ok) { SendMiniText("Your Path has forbidden itself from this vulgar implement."); return false; }
        // RTK MAP_ERRITMMARK reuses the level message verbatim ("You need more experience."); say which rank
        // instead, since nothing in the client explains that a subpath mark is what's missing. Only equipment
        // carries a mark in the live registry (146 rows, all wearable), so "wear" is always the right verb.
        if (_char.Mark < def.Mark) { SendMiniText($"You must bear the {MarkName(def.Mark)} mark to wear {def.Name}."); return false; }
        return true;
    }

    /// <summary>Subpath rank name for a mark number (RTK Paths.PthMark1..5 — the "Il san (W)" column family,
    /// class suffix dropped since the mark itself is class-agnostic here).</summary>
    internal static string MarkName(int mark) => mark switch
    {
        0 => "unmarked",
        1 => "Il san", 2 => "Ee san", 3 => "Sam san", 4 => "Sa san", 5 => "Oh san", _ => $"rank-{mark}"
    };

    // Move a bag item onto the body: bumps any item already in that gear slot back to the bag first.
    private void EquipFromSlot(int slot)
    {
        // RTK pc_equipitem gates on player state before anything else (dead/mounted can't change gear).
        // Every refusal below is RTK clif_sendminitext (system message) -- pc_equipitem's state checks
        // (line ~1551/1557), pc_canequipitem's sex/level/might via map_msg[ret].message (line ~1575), and
        // pc_canequipstats's cursed-stat check (line ~1585) -- never a spoken clif_sendmsg chat bubble.
        if (_char.Hp == 0) { SendMiniText("Spirit's can't do that."); return; }
        if (BlockedByMount()) return;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null || !def.IsEquip) return;
        // Bound gear (what an NPC forges FOR you — ItemDef.Bonded) only equips for the owner it was stamped
        // with when obtained (InvItem.Owner). Anyone else may hold, drop or trade it, just never wear it.
        if (!string.IsNullOrEmpty(it.Owner) && it.Owner != _char.Name)
        { SendMiniText("This does not belong to you."); return; }
        // Wear requirements (RTK item_data): sex-locked gear, a minimum level, and a minimum MIGHT (checked
        // against effective might so already-worn +might gear counts).
        // ItmSex: 0 = male-only, 1 = female-only, 2 = UNISEX (the common case — 1944/2545 items, incl. most
        // weapons). Character.Sex uses the same 0=M/1=F encoding, so a sex-locked item (0 or 1) must match;
        // anything >= 2 is unrestricted. (The old `!= 0` test wrongly blocked every unisex item.)
        // The refusal wording on all three is the real game's, verbatim (user-supplied 2026-08-07, might line
        // 2026-08-15) — short, and naming neither the item nor the number you're missing. The might line used
        // to be ours ("You need N might to wear X."), which leaked both.
        if (def.Sex < 2 && def.Sex != _char.Sex) { SendMiniText("This doesn't fit you."); return; }
        if (def.Level > _char.Level) { SendMiniText("You need more experience."); return; }
        if (def.MightReq > EffMight) { SendMiniText("You can't lift it above your waist much less wield it."); return; }
        // (The path/mark gate is NOT here — it's on the use funnel, HandleUseItem. See CanUsePath.)
        // Two-handed weapon vs shield (RTK pc_canequipitem, MAP_ERRITM2H): a weapon whose LOOK falls in
        // 10000..29999 is two-handed art, and the client has no way to draw it alongside a shield. Blocks the
        // pairing from BOTH directions — putting a shield on over a 2H weapon, and a 2H weapon on over a shield.
        bool wearing2H = _char.Equipment.FirstOrDefault(e => e.Slot == 1) is { } wep
                         && Content.ItemById(wep.ItemId) is { Look: >= 10000 and <= 29999 };
        bool wearingShield = _char.Equipment.Any(e => e.Slot == 3);
        if ((def.Type == 5 && wearing2H) || (def.Type == 3 && def.Look is >= 10000 and <= 29999 && wearingShield))
        { SendMiniText("You can't equip a 2-handed weapon with a shield."); return; }
        // Cursed/malus gear (negative Vita/Mana): RTK pc_canequipstats blocks it if the penalty would exceed
        // your current effective max — it'd zero out the pool entirely. 14/19 items in the registry carry a
        // negative Vita/Mana line, so this is reachable, not theoretical.
        if (def.Vita < 0 && -def.Vita > EffMaxHp) { SendMiniText("You lack the health required to wield that."); return; }
        if (def.Mana < 0 && -def.Mana > EffMaxMp) { SendMiniText("You lack the wisdom required to wield that."); return; }
        byte wire = def.EquipSlot;
        // Rings/gauntlets are all Type 7 (wire slot 7 = left ring) but share TWO interchangeable slots — 7 and
        // 8 (right ring). Wear the second one in the free right slot instead of replacing the left. Only when
        // BOTH are taken does a new ring replace the left. (Slot 8 carries no items in the data, so it's only
        // ever filled by this path.)
        if (wire == 7 && _char.Equipment.Any(e => e.Slot == 7) && _char.Equipment.All(e => e.Slot != 8))
            wire = 8;

        _char.Inventory.Remove(it);
        // This 0x10 is MANDATORY, however it narrates. Suppressing it (to keep wearing gear silent) left a
        // ghost row in the bag that couldn't be dropped, equipped or used — the server had already let the
        // item go while the client still drew it. The bag is a separate structure from the equip window: only
        // 0x48f0b0, reached from the 0x10 handler, clears a bag entry, and the 0x37 below never touches it.
        // See Content.EquipDelReason for the reason-byte options and how to sweep for a quiet one.
        if (Content.EquipDelReason >= 0) SendDelItem((byte)slot, (byte)Content.EquipDelReason);

        var prev = _char.Equipment.FirstOrDefault(e => e.Slot == wire);
        bool replacing = prev is not null;   // swapping over worn gear sounds different than dressing a bare slot (GearSfx)
        if (prev is not null)
        {
            var pdef = Content.ItemById(prev.ItemId);
            // Bag FIRST, gear second, and only proceed if it landed — see HandleUnequip for what the other
            // order costs. The incoming item's slot was freed just above so there is normally room; if there
            // somehow isn't, put the incoming item back rather than destroy the one being replaced.
            if (pdef is not null && !GiveItem(pdef, 1, prev.Dura, prev.CustomName, owner: prev.Owner))
            {
                _char.Inventory.Add(it);
                SendAddItem(it);
                return;
            }
            EquipRemove(prev);
            DropEnchantIfWeapon(pdef);        // swapping weapons drops the enchant, same as taking one off
            SendUnequip(wire);
            if (pdef is not null) ApplyAppearance(pdef, equip: false);
        }

        var worn = new InvItem(wire, def.Id, 1, it.Dura == 0 ? def.Durability : it.Dura) { CustomName = it.CustomName };
        EquipAdd(worn);
        InvalidateEquipTotals();                      // gear changed (this add + any prev swap above)
        SendEquip(worn);
        ApplyAppearance(def, equip: true);
        SendStats();                                  // push the new gear bonuses to the HUD
        PlayGearSfx(wire, equipping: true, replacing: replacing);
        MarkDirty();
        // (No "Equipped X" over-head bubble — the paperdoll + gear stats are feedback enough; SendLog here
        // spoke it as 0x0D chat over the character, which the player didn't want.)
    }

    // 0x1F unequip: dec[0]=wire equip-slot byte. Take the worn item off and return it to the bag.
    private void HandleUnequip(byte[] dec)
    {
        if (dec.Length < 1) return;
        if (BlockedByMount()) return;              // taking gear off is a swap too
        byte wire = dec[0];
        var worn = _char.Equipment.FirstOrDefault(e => e.Slot == wire);
        if (worn is null) return;
        var def = Content.ItemById(worn.ItemId);
        // Get it into the bag BEFORE taking it off, and abort if it won't fit. The old order removed the
        // gear first and then ignored GiveItem's result, so taking anything off with a full pack DESTROYED
        // it outright. Failing here leaves the item worn, which is the only harmless outcome. (UnequipAll
        // already had this order; the single-slot path did not.)
        if (def is not null && !GiveItem(def, 1, worn.Dura, worn.CustomName, owner: worn.Owner)) return;
        EquipRemove(worn);
        InvalidateEquipTotals();
        DropEnchantIfWeapon(def);
        SendUnequip(wire);
        if (def is not null) ApplyAppearance(def, equip: false);
        SendStats();                                  // drop the gear bonuses from the HUD
        PlayGearSfx(wire, equipping: false);
        MarkDirty();
    }

    // Typed-"A" bulk unequip: strips every worn slot back into the bag, same per-item plumbing as
    // HandleUnequip (SendUnequip + appearance revert + GiveItem). Stops the moment the bag can't take the
    // next item back — GiveItem already sends "You can't have more." and leaves that item (and everything
    // after it) equipped, rather than dropping it on the ground or destroying it.
    private void UnequipAll()
    {
        foreach (var worn in _char.Equipment.ToList())
        {
            var def = Content.ItemById(worn.ItemId);
            if (def is not null && !GiveItem(def, 1, worn.Dura, worn.CustomName, owner: worn.Owner)) break;   // bag full — stop, leave the rest equipped
            EquipRemove(worn);
            InvalidateEquipTotals();
            DropEnchantIfWeapon(def);
            SendUnequip(worn.Slot);
            if (def is not null) ApplyAppearance(def, equip: false);
            PlayGearSfx(worn.Slot, equipping: false);
        }
        SendStats();
        MarkDirty();
    }

    // ---- durability decay / breakage (RTK clif_deductweapon/deductarmor/checkdura, clif.c:6646-6844) -----
    // On landing or taking a hit, each relevant equipped slot has a ~49% chance (rnd(100) > 50) to lose 1
    // point of durability. Indestructible gear and gear with no Durability rating never decays. Durability
    // loss is disabled entirely on PvP maps (RTK: "disable dura loss from mobs on pvp map").

    /// <summary>Roll durability loss for one worn item, warning at 50/25/10/5/1% and destroying it at 0.</summary>
    private void DeductDura(InvItem worn)
    {
        if (Content.IsPvpMap(_char.Map)) return;
        var def = Content.ItemById(worn.ItemId);
        if (def is null || def.Indestructible || def.Durability == 0) return;
        if (worn.Dura == 0) worn.Dura = def.Durability;   // lazily fill (equip already does this; belt-and-suspenders)
        if (Random.Shared.Next(100) <= 50) return;        // RTK: rnd(100) > 50 triggers the deduction
        worn.Dura = (ushort)Math.Max(0, worn.Dura - 1);
        MarkDirty();   // covers CheckDura's own equipment mutations too (a Repair-threshold flag, or BreakItem)
        CheckDura(worn, def);
    }

    /// <summary>RTK clif_checkdura: fire each threshold warning at most once (tracked by worn.Repair), then
    /// destroy the item once its durability bottoms out.</summary>
    private void CheckDura(InvItem worn, ItemDef def)
    {
        double pct = (double)worn.Dura / def.Durability;
        // RTK clif_checkdura sends these through clif_sendmsg(sd, 5, buf) -- type 5 "System", the same
        // 0x0A minitext packet as clif_sendminitext (type 3) just tagged differently -- not the chat log.
        if (pct <= .50 && worn.Repair == 0) { SendMiniText($"Your {def.Name} is at 50%.", type: 5); worn.Repair = 1; }
        if (pct <= .25 && worn.Repair == 1) { SendMiniText($"Your {def.Name} is at 25%.", type: 5); worn.Repair = 2; }
        if (pct <= .10 && worn.Repair == 2) { SendMiniText($"Your {def.Name} is at 10%.", type: 5); worn.Repair = 3; }
        if (pct <= .05 && worn.Repair == 3) { SendMiniText($"Your {def.Name} is at 5%.",  type: 5); worn.Repair = 4; }
        if (pct <= .01 && worn.Repair == 4) { SendMiniText($"Your {def.Name} is at 1%.",  type: 5); worn.Repair = 5; }
        if (worn.Dura <= 0) BreakItem(worn, def);
    }

    /// <summary>RTK clif.c:6805 onward: the item is gone for good — unequipped, appearance reverted, stats
    /// recalculated.</summary>
    private void BreakItem(InvItem worn, ItemDef def)
    {
        SendMiniText($"Your {def.Name} was destroyed!", type: 5);   // RTK clif_checkdura: type 5 "System"
        EquipRemove(worn);
        InvalidateEquipTotals();
        DropEnchantIfWeapon(def);
        SendUnequip(worn.Slot);
        ApplyAppearance(def, equip: false);
        SendStats();
    }

    /// <summary>RTK's "protected" charge (clif_checkdura / clif_checkinvbod): instead of breaking, the item is
    /// restored to full durability and one charge is spent. No row in the live registry sets ItmProtected and
    /// nothing grants a per-instance charge yet, so this never fires today — it exists so the break paths below
    /// can be written the way RTK writes them rather than silently dropping the branch.</summary>
    private bool TryProtectFromBreak(InvItem it, ItemDef def)
    {
        if (!def.Protected && it.Protected == 0) return false;
        if (it.Protected > 0) it.Protected--;
        it.Dura = def.Durability;
        it.Repair = 0;                                       // full dura -> the warning ladder starts over
        SendMiniText($"Your {def.Name} has been restored!", type: 5);
        return true;
    }

    // ---- death penalties: gear (RTK player.lua deathDuraLoss -> clif_deductduraequip + clif_checkinvbod) ----

    /// <summary>Dying batters every worn slot (RTK <c>clif_deductduraequip</c>, clif.c:6846): a flat 10% of the
    /// item's MAX durability off each, through the same warning ladder as ordinary wear, and anything that
    /// bottoms out breaks. Break-on-death gear (ItmBoD, 77 items) is destroyed outright regardless of its
    /// durability. Skipped entirely on a PvP map, like every other durability loss (RTK's early
    /// <c>if (map[..].pvp) return</c>).</summary>
    private void DeathDuraLoss()
    {
        if (Content.IsPvpMap(_char.Map)) return;
        foreach (var worn in _char.Equipment.ToArray())
        {
            var def = Content.ItemById(worn.ItemId);
            if (def is null || def.Indestructible) continue;          // "ethereal" gear never decays
            bool rated = def.Durability > 0;                          // unrated gear can't wear down — only BoD kills it
            if (rated)
            {
                if (worn.Dura == 0) worn.Dura = def.Durability;
                worn.Dura = (ushort)Math.Max(0, worn.Dura - def.Durability / 10);
            }
            if (def.BreakOnDeath || (rated && worn.Dura == 0))
            {
                if (TryProtectFromBreak(worn, def)) continue;
                BreakItem(worn, def);
            }
            else if (rated) CheckDura(worn, def);                     // 50/25/10/5/1% warnings only
        }
        MarkDirty();
    }

    /// <summary>Break-on-death items sitting in the BAG (RTK <c>clif_checkinvbod</c>, clif.c:6968) — a separate
    /// pass from the worn one above, and the reason a spare BoD weapon in your pack is no safer than the one in
    /// your hand. Unlike the gear pass this ignores durability entirely: only the ItmBoD flag matters.</summary>
    private void DeathInventoryBod()
    {
        foreach (var it in _char.Inventory.ToArray())
        {
            var def = Content.ItemById(it.ItemId);
            if (def is null || !def.BreakOnDeath) continue;
            if (TryProtectFromBreak(it, def)) continue;
            _char.Inventory.Remove(it);
            SendDelItem((byte)it.Slot, 13);                           // reason 13 = Broke (RTK clif_senddelitem table)
            SendMiniText($"Your {def.Name} was destroyed!", type: 5);
        }
        MarkDirty();
    }

    /// <summary>How long a death pile stays reserved for the player it fell off (RTK player.lua
    /// <c>canLoot</c>/<c>isYours</c>: <c>os.time() >= item.timer + 300</c>). After this it is ordinary floor
    /// loot anyone may take.</summary>
    private const int DeathPileLockMs = 300_000;   // 300 s

    /// <summary>The death pile (RTK player.lua <c>deathPileDrop</c>): every bag slot independently coin-flips,
    /// and on heads a droppable stack is spilled onto the corpse tile. Bound gear (ItmDroppable, our NoDrop)
    /// stays with you — RTK's <c>if not item.droppable</c>. Every stack is LOOTER-LOCKED to the dead player for
    /// <see cref="DeathPileLockMs"/> (RTK's <c>groundItemsToCurse[i].looters = {player.ID}</c>), which is what
    /// makes F1 "Recover Death Pile" worth having: nobody else can take it, and the owner can pull it back from
    /// two tiles off even with someone parked on top.
    /// <para>One deliberate narrowing: RTK curses every BL_ITEM already lying in the cell, so dying on top of a
    /// stranger's dropped loot silently reserves theirs too. Only what actually fell off this corpse is locked
    /// here.</para></summary>
    private void DeathPileDrop()
    {
        bool dropped = false;
        foreach (var it in _char.Inventory.ToArray())
        {
            var def = Content.ItemById(it.ItemId);
            if (def is null || def.NoDrop) continue;
            if (Random.Shared.Next(2) != 0) continue;                 // RTK: math.random(1,2) == 1
            _char.Inventory.Remove(it);
            SendDelItem((byte)it.Slot, 1);                            // reason 1 = Drop
            _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
                X = _char.X, Y = _char.Y, Amount = it.Amount, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName,
                Owner = it.Owner,   // a bound item stays bound through a death pile (survives the looter-lock expiring)
                LooterId = _char.Id, LockedUntil = Environment.TickCount64 + DeathPileLockMs });
            dropped = true;
        }
        if (dropped) SendMiniText("Your items are ripped from your body.");
        MarkDirty();
    }

    /// <summary>Coin spill on death (RTK player.lua <c>deathDropGold</c>): a random 5–35% of the purse lands on
    /// the corpse tile as one pile, tiered to the same coin icons as a manual gold drop. Looter-locked like the
    /// item pile — RTK drops the gold first and then curses the whole cell, so the coins are covered too.</summary>
    private void DeathDropGold()
    {
        if (_char.Coins == 0) return;
        uint amt = (uint)(_char.Coins * Random.Shared.Next(5, 36) / 100);
        if (amt == 0) return;
        _char.Coins -= amt;
        ushort gfx = amt < 2 ? (ushort)22 : amt < 100 ? (ushort)73 : (ushort)72;
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = -1,
            X = _char.X, Y = _char.Y, Amount = (int)amt, Graphic = gfx,
            LooterId = _char.Id, LockedUntil = Environment.TickCount64 + DeathPileLockMs });
        SendMiniText($"You dropped {amt:N0} coins.");
        MarkDirty();
    }

    /// <summary>F1 "Recover Death Pile" (RTK player.lua <c>recoverDeathPile</c>): sweep your own tile and the
    /// two tiles you're FACING, and pull back every stack still looter-locked to you — coins to the purse,
    /// items to the bag. Reads like a two-tile Filch that can only ever take your own pile; anything unlocked,
    /// or locked to someone else, is left where it lies. Returns how many stacks came back, so the caller can
    /// tell "nothing here" from "recovered". Stops early if the pack fills (RTK says as much in its own help
    /// text: "If you do not have enough room in your inventory, you will be unable to recover all of your
    /// items.") — whatever is left stays locked on the floor for the rest of the grace period.</summary>
    internal int RecoverDeathPile()
    {
        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        int taken = 0;
        // Own tile first — a corpse normally drops the pile underfoot — then 1 and 2 tiles ahead.
        for (int step = 0; step <= 2; step++)
        {
            int tx = _char.X + dx * step, ty = _char.Y + dy * step;
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) break;
            while (true)
            {
                var gi = _world.PickUp(_char.Map, tx, ty, _char.Id, ownOnly: true);
                if (gi is null) break;                                  // nothing of ours left on this tile
                if (gi.ItemId < 0) { _char.Coins += (uint)gi.Amount; SendStats(); taken++; continue; }
                var def = Content.ItemById(gi.ItemId);
                if (def is null) continue;
                if (!GiveItem(def, gi.Amount, gi.Dura, gi.CustomName, owner: gi.Owner))
                {
                    // Pack full (GiveItem already said so). Put it back exactly as it was, lock intact.
                    _world.DropItem(_char.Map, gi);
                    MarkDirty();
                    return taken;
                }
                taken++;
            }
        }
        if (taken > 0) MarkDirty();
        return taken;
    }

    /// <summary>Whether anything within reach of <see cref="RecoverDeathPile"/> is still locked to us — the
    /// look-before-you-leap the F1 branch does to decide between the help text and the actual recovery (RTK
    /// f1npc.lua's <c>deathPileFound</c> pre-scan).</summary>
    internal bool DeathPileInReach()
    {
        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        foreach (var gi in _world.ItemsOn(_char.Map))
            if (gi.BelongsTo(_char.Id))
                for (int step = 0; step <= 2; step++)
                    if (gi.X == _char.X + dx * step && gi.Y == _char.Y + dy * step) return true;
        return false;
    }

    // 0x24 drop gold: dec[0..3]=amount(u32BE). Spill coins onto my tile as a pickup-able gold pile.
    private void HandleDropGold(byte[] dec)
    {
        if (dec.Length < 4) return;
        // RTK clif_parsedropgold gates on player state first (dead/mounted can't drop gold).
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (BlockedByMount()) return;
        uint amt = (uint)((dec[0] << 24) | (dec[1] << 16) | (dec[2] << 8) | dec[3]);
        if (amt > _char.Coins) amt = _char.Coins;
        if (amt == 0) { SendLog("You have no coins to drop."); return; }
        _char.Coins -= amt;
        SendStats();
        MarkDirty();
        ushort gfx = amt < 2 ? (ushort)22 : amt < 100 ? (ushort)73 : (ushort)72;   // coins_1 / _2_99 / _100_999 icons
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = -1,
            X = _char.X, Y = _char.Y, Amount = (int)amt, Graphic = gfx });
    }

    // 0x30 (Shift+C) "rearrange a pane": dec[0] = which pane (0 = bag, 1 = spellbook), dec[1] = start slot,
    // dec[2] = stop slot, both 1-based. RTK's case 0x30 splits on dec[0]: ==1 routes to the spellbook
    // (clif_parsechangespell, clif.c:10521), ==0 swaps two bag slots (clif_parsechangepos -> pc_changeitem,
    // clif.c:10281/1953), anything else answers "You are busy." Live-confirmed shape (user capture 2026-08-17):
    // `30 01 01 02 00` = spell pane, swap slots 1 and 2.
    private void HandleChangePos(byte[] dec)
    {
        if (dec.Length < 3) return;
        int a = dec[1] - 1, b = dec[2] - 1;
        switch (dec[0])
        {
            case 0: SwapBagSlots(a, b); break;
            case 1: SwapSpellSlots(a, b); break;
            default: SendMiniText("You are busy."); break;   // RTK clif_parsechangepos's own else-branch line
        }
    }

    // Swap two bag slots in place (RTK pc_changeitem, pc.c:1953): move the entries, then redraw each affected
    // slot — an occupied slot via 0x0F, a now-empty one via 0x10. The 0x10 is REQUIRED to clear the client's
    // bag cell (it is the ONLY thing that clears one — see the 164-byte array note at the top of this file).
    // Consequence: swapping two OCCUPIED slots is silent (two 0x0F redraws), but moving an item ONTO a
    // previously-empty slot narrates one delitem line for the emptied source ("<item> removed."), because no
    // 0x10 reason is silent on 4.95 (docs §11c). RTK sends the same delitem here, reason 0.
    private void SwapBagSlots(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= _char.MaxInv || b >= _char.MaxInv) return;
        var itA = InvAt(a);
        var itB = InvAt(b);
        if (itA is null && itB is null) return;
        if (itA is not null) itA.Slot = (byte)b;
        if (itB is not null) itB.Slot = (byte)a;
        if (itB is not null) SendAddItem(itB); else SendDelItem((byte)a, 0);   // slot a now holds itB (or is empty)
        if (itA is not null) SendAddItem(itA); else SendDelItem((byte)b, 0);   // slot b now holds itA (or is empty)
        MarkDirty();
    }

    /// <summary>"Blessed by the Stars": a White amber dropped in the middle circle of the Mythic Nexus is
    /// absorbed rather than dropped, and marks the player as eligible for the Star armor chain. True when
    /// the drop was consumed here, so <see cref="HandleDropItem"/> stops — the same contract as
    /// <see cref="TryHarvest"/>, and the same reason (RTK's <c>player.fakeDrop = 1</c>).
    ///
    /// <para>Silent on every non-qualifying case: wrong item, wrong tile, or the mark already held all fall
    /// through and drop the amber normally. Level is the one exception — someone standing on the right tile
    /// with the right item has clearly been told what to do, so being turned away for age gets a reason.
    /// See <see cref="BlessedByTheStars"/> for the sourcing.</para></summary>
    private bool TryStarBlessing(ItemDef def, int slot)
    {
        if (def.Key != BlessedByTheStars.Offering) return false;
        if (!BlessedByTheStars.AtAltar(_char.Map, _char.X, _char.Y)) return false;
        if (HasLegend(ArmorQuest.BlessedLegend)) return false;

        if (_char.Level < BlessedByTheStars.MinLevel)
        {
            SendMiniText("The stars take no notice of one so young.");
            return true;                        // consumed the ATTEMPT, not the amber — nothing is taken
        }

        var it = InvAt(slot);
        if (it is null || it.ItemId != def.Id) return false;
        if (!TakeItem(BlessedByTheStars.Offering, 1)) return false;

        SendEffect(_char.Id, BlessedByTheStars.CloudEffect);
        SendEffect(_char.Id, BlessedByTheStars.SwirlEffect);
        SendMiniText("Energy from the stars fills your body.");
        SendMiniText("The white amber is absorbed in a flash of light.");
        AddLegend($"Was blessed by the stars ({Character.GameDate})", ArmorQuest.BlessedLegend,
                  BlessedByTheStars.LegendIcon, BlessedByTheStars.LegendColor);
        Log.Info($"   -> BLESSED BY THE STARS at ({_char.X},{_char.Y}) on map {_char.Map}");
        return true;
    }

}
