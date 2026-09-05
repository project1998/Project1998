using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ---- profile window (the "Mind's Eye") ----
    // The client opens the self-profile window when the profile key is pressed by sending 0x2D. Byte 0
    // == 0 is the self-profile request (byte != 0 is group status in 7.x). We reply with 0x39, the
    // self-profile packet (clif_mystaytus): AC/clan/title/class/legend. Without this reply the window
    // never appears — that's the bug the user hit.
    // ---- 0x66 "examine item" (right-click a bag slot) --------------------------------------------
    // REQUEST, RE'd out of the 4.95 binary 2026-08-07. It is built by the INVENTORY pane's mouse handler
    // (client 0x43bf40, mouse-message kind 4) through packet builder 0x43c290, which writes the body byte
    // for byte:
    //   body[0]=00  body[1]=cursor Y, pane-local  body[2]=00  body[3..4]=01 01
    //   body[5]=SLOT  body[6]=01  body[7..9]=00
    // The pane shows 15 cells with a page byte at widget+0x104, so a click resolves to
    // `cell + 15*page + 1` (0x43bf94) and then through 0x43c5a0 ("the Nth occupied slot") to the id in
    // body[5]. The SAME id is what a LEFT-click sends as 0x1C (use item, builder 0x43c160), so the slot
    // convention here is identical to HandleUseItem's — 1-based.
    // The item is in body[5], NOT body[1]: an earlier note had the two swapped, which is why every logged
    // decode said "no bag item at that slot" (it was reading the cursor, 196..225). The two track each
    // other because both derive from the same click — that ratio was the cell pitch, not an id.
    // body[1] is the click's Y IN PANE COORDINATES, and it exists to be ECHOED BACK as the 0x59 tooltip's
    // anchor so the box centers on the mouse (see SendItemTooltip). Proof it's Y, not X (RE'd 2026-08-27):
    // the pane's cell hit-test (0x43c540) PtInRects the message's coord pair against cell rects built at
    // 0x43c420 as (left=0x1e, top=13N+0x24, right=0xa0, bottom=13N+0x31) for cell N — 13px row pitch —
    // and the coordinate the builder copies into body[1] ([msg+8], 0x43bfab) is the one tested against
    // the 13N range, i.e. the row axis. That also matches the old 196..225 captures (rows ~12-14; a
    // pane-local X can never exceed 0xa0 and still hit a cell).
    private void HandleItemInfoRequest(byte[] dec)
    {
        int slot = (dec.Length > 5 ? dec[5] : 0) - 1;      // 1-based on the wire, like 0x1C
        byte cursorY = dec.Length > 1 ? dec[1] : (byte)0;
        // The request can only come from the bag widget, so don't fall back to Equipment: worn gear numbers
        // its slots on a different scale (ItemDef.EquipSlot) and would answer with the wrong item.
        var it = InvAt(slot);
        var def = it is null ? null : Content.ItemById(it.ItemId);
        Log.Info($"   -> ITEM-INFO (0x66) slot={slot + 1} cursorY={cursorY}" +
                 (def is null ? "  [empty slot]" : $"  -> '{def.Name}' #{def.Id}"));
        if (it is null || def is null) return;
        SendItemInfo(def, ItemInfoText(def, it), cursorY);
    }

    // ---- THE REPLY: `0x59` sub-kind 0, the inventory pane's own tooltip -------------------------------
    // This is the real thing — the blue multi-line box that hangs off the item list. It is NOT a dialog and
    // NOT a window: opcode 0x59 is claimed by the INVENTORY PANE itself (its packet handler 0x43c110 takes
    // 0x0F, 0x10 and 0x59), and sub-kind 0 lands in 0x43c1b0:
    //     body[0] = 0            sub-kind. (1 is a different feature entirely — RTK's clif_sendtowns sends
    //                            0x59 with body[0]=64, and the world dispatcher's 0x59 trampoline at
    //                            0x44b9e9 gates on body[0]==1, so the two never collide.)
    //     body[1] = Y anchor u8  the box's VERTICAL CENTER, in pane-local pixels — NOT a row index (an
    //                            earlier note read it as one, which pinned the box to the top of the
    //                            screen: slot ids are 1..52, so `top = anchor - height/2` went negative
    //                            and the clamp parked it at y=0). Echo the request's body[1] — the click's
    //                            pane-local Y — and the box centers on the mouse, which is what the real
    //                            server did and what the period screenshots show.
    //     body[2..3] = u16BE len 1..0x3FF. Out of that range and the handler bails silently.
    //     body[4..]  = text
    // The handler asks the pane for its own rectangle (0x425380), computes the pane's horizontal midpoint,
    // and builds a 0x108-byte overlay object (0x42f450) with the text, both coordinates and a `0x2710`
    // (10000) lifetime. The ctor places the box at (midX - width/2, anchor - height/2) (0x42f570..0x42f598),
    // clamps it inside the pane and the screen (the OffsetRect calls against 0x470580/0x470590), then
    // shifts it by the pane origin — so X self-centers on the item list, Y follows the anchor, and it
    // times out on its own. No close button, which is exactly what the period screenshots show.
    // Line breaks: 0x42f450's own scan (0x42f4ed) counts CR (0x0d), LF (0x0a) AND TAB (0x09) as breaks, so
    // any of the three works and the separator doesn't need calibrating.
    private void SendItemTooltip(byte anchorY, IReadOnlyList<string> lines)
    {
        var t = PopupAscii(string.Join(_itemInfoSep, lines));
        if (t.Length > 0x3FF) t = t[..0x3FF];          // hard client range check, not a soft cap
        if (t.Length == 0) return;                     // len 0 is also rejected by the handler
        var body = new List<byte> { 0, anchorY, (byte)(t.Length >> 8), (byte)t.Length };
        body.AddRange(t);
        SendMap(0x59, _gameInc++, body.ToArray(), $"item-tooltip(0x59) anchorY={anchorY} {t.Length}B");
    }

    // REPLY — the same opcode back, parsed by client handler 0x4511b0. **`0x66` IS A "GO TO THIS URL"
    // PACKET.** Every kind takes a URL; they differ only in which browser opens it:
    //   kind 0   -> [u16BE len][URL]                       the client's OWN EMBEDDED BROWSER.
    //   kind 1/2 -> [u16BE len1][URL][u16BE len2][text]    a centred "DLGBBS01.EPF + OK" popup whose second
    //               string is a message (<=999 chars); OK ShellExecutes the URL in the EXTERNAL browser.
    //               kind 1 additionally quits the game.
    // All lengths are u16 BIG-endian (client reader 0x475ca0 is `hi<<8 | lo`).
    //
    // PROOF for kind 0 (RE'd 2026-08-07). The window class built at 0x406250 hosts an Internet Explorer
    // ActiveX control: vtable slot +0x8c (0x406680) does `CoCreateInstance(CLSID_WebBrowser@0x4d00a8, ...)`
    // and queries `IID_IWebBrowser2@0x4d0098`; feeding it our string reaches 0x4053c0, which does
    // `SysAllocString(str)` and then `call [vtbl+0x2c]` with five args = **IWebBrowser2::Navigate(BSTR url,
    // flags, targetFrame, postData, headers)**. The window sits at (10,10)-(630,470) — near-fullscreen.
    // PROOF for kinds 1/2: the popup's OK handler is vtable slot +0x70 = 0x488480, two actions —
    //     ShellExecuteA(0, 0, url, 0, 0, SW_SHOWNORMAL)
    //     if (flagByte at window+0x280) { SetEvent(app+0x818); app+0x815 = 1 }   // 0x403030 -> shutdown
    // RTK 7.x confirms both INDEPENDENTLY: `clif_sendurl(sd, type, url)` (clif.c:265) builds this exact
    // packet and comments the type byte "0 = ingame browser, 1 = popup open browser then close client,
    // 2 = popup". **Never send kind 1.**
    //
    // SO: right-clicking a bag item asks the server for a WEB PAGE. Original NexusTK served item pages off
    // nexustk.com and rendered them in that embedded browser — which is why nothing in the client draws an
    // item-detail panel, and why Item.tbl carries no text.
    //
    // We support both. With ItemInfoUrl set we answer kind 0 and the real in-game browser opens; with it
    // empty we fall back to the kind-2 popup carrying the stat text, which needs no web server.
    //
    // ⚠ THE POPUP'S URL MUST NOT BE EMPTY. An empty string makes ShellExecute open the client's own install
    // directory in Explorer (observed 2026-08-07). It has to be a string the shell will *reject*: '<' and
    // '>' are illegal in filenames and it carries no scheme, so this can only fail to SE_ERR_FNF — which is
    // what makes OK a plain dismiss. A bare word would be worse: ShellExecute resolves those against
    // App Paths/PATH and could launch a program.
    private const string NoUrl = "<none>";

    // ⚠ THE IN-GAME BROWSER (kind 0) IS DEAD IN THIS BUILD — do not route the feature through it. Live test
    // 2026-08-07 with a real URL: the window's ctor asks for sprite category "X" (XBUTTON.EPF, its close
    // button), the load throws an uncaught allocation-failure (`_CxxThrowException` via 0x430c7a ->
    // 0x406cb6, the XBUTTON.EPF site) and the client dies on a null deref at 0x470ff4. The asset simply
    // isn't in this install's archives, so the browser chrome can't build. Nothing can select it any more —
    // @iteminfo, the only mode switch, was removed — but the branch is kept as the record of what NOT to
    // route through, since the packet shape is otherwise inviting.

    /// <summary>How the examine reply is delivered. <c>Tooltip</c> = the `0x59` inventory-pane overlay — the
    /// real one, and now the only reachable value (the @iteminfo switch is gone; change the initializer to
    /// try another). <c>Overlay</c> = a `0x0A` message type. <c>Popup</c> = the `0x66` OK dialog (which the
    /// game uses for the Rogue Judge/Spy spells, so it's the wrong frame here). <c>Browser</c> = `0x66`
    /// kind 0, which crashes this build.</summary>
    private enum ItemInfoMode { Tooltip, Overlay, Popup, Browser }
    private ItemInfoMode _itemInfoMode = ItemInfoMode.Tooltip;

    /// <summary>0x0A message type for <see cref="ItemInfoMode.Overlay"/>. Types 2, 3 and 8 all reach the
    /// bordered word-wrap box; which one the live client actually paints is a one-command sweep.</summary>
    private byte _itemInfoType = 2;

    /// <summary>URL template for the (broken) in-game browser, e.g. <c>http://host/item/{id}</c>.</summary>
    private string _itemInfoUrl = "";
    private string _itemInfoSep = "\n";

    private void SendItemInfo(ItemDef def, IReadOnlyList<string> lines, byte anchorY = 1)
    {
        switch (_itemInfoMode)
        {
            case ItemInfoMode.Tooltip:
                SendItemTooltip(anchorY, lines);
                return;

            case ItemInfoMode.Overlay:
                SendItemOverlay(lines);
                return;

            case ItemInfoMode.Browser:
            {
                var url = PopupAscii(_itemInfoUrl.Replace("{id}", def.Id.ToString())
                                                 .Replace("{name}", Uri.EscapeDataString(def.Name)));
                var b = new List<byte> { 0 };
                b.AddRange(Be((ushort)url.Length)); b.AddRange(url);
                SendMap(0x66, _gameInc++, b.ToArray(), $"item-info(0x66) browser url={Encoding.ASCII.GetString(url)}");
                return;
            }

            default:
            {
                var body = PopupAscii(string.Join(_itemInfoSep, lines));
                if (body.Length > 999) body = body[..999];
                var url = PopupAscii(NoUrl);
                var b = new List<byte> { 2 };
                b.AddRange(Be((ushort)url.Length));  b.AddRange(url);
                b.AddRange(Be((ushort)body.Length)); b.AddRange(body);
                SendMap(0x66, _gameInc++, b.ToArray(), $"item-info(0x66) popup {body.Length}B");
                return;
            }
        }
    }

    // The examine OVERLAY: the multi-line blue text box that hangs off the inventory pane. It is not a
    // window at all — it is opcode 0x0A, the same message channel as mini-text, on a type that routes to
    // the client's bordered word-wrap box instead of the status line.
    //
    // That box is the class at 0x47b400-0x47c700: it builds a nine-slice frame out of MSGBORD.EPF (the ten
    // sprite loads at 0x47b4c6..0x47b58f) and lays the text out itself with a real word-wrap pass
    // (0x47c310 walks the string measuring each glyph via 0x423880 and breaking on whitespace via
    // 0x4ae324) — which is why the reference screenshot wraps "Dégâts: Petit … Grand …" onto two lines.
    // Its 0x0A handler is 0x47c520: `body[0]` = type, and the jump table at 0x47c6c4 covers types 2..8,
    // of which **2, 3 and 8** reach the text path and 4/5/7 fall through to other widgets.
    //
    // The text limit is the client's own widen buffer: 0x8000 chars, vs the 999 the 0x66 popup allows and
    // the 255 our mini-text used to assume. A full stat block fits with room to spare.
    private void SendItemOverlay(IReadOnlyList<string> lines)
        => SendMiniText(string.Join(_itemInfoSep, lines), _itemInfoType);

    // The popup is a plain ANSI/wide text control — anything outside printable ASCII would widen into
    // garbage, so fold it out here rather than at every call site.
    private static byte[] PopupAscii(string s)
    {
        var b = Encoding.ASCII.GetBytes(s);       // non-ASCII already becomes '?'
        for (int i = 0; i < b.Length; i++)
            if (b[i] < 0x20 && b[i] != (byte)'\n' && b[i] != (byte)'\r' && b[i] != (byte)'\t') b[i] = (byte)' ';
        return b;
    }

    /// <summary>The examine tooltip's body, laid out the way the real game's box does it — name, durability,
    /// the small/large damage pair, one combined Armor/Hit/Dam line, then the "&lt;stat&gt; increase:" column,
    /// owner, and the class-level requirement. Labels sit left, values at a fixed column. Only lines the item
    /// actually earns are emitted.</summary>
    private IReadOnlyList<string> ItemInfoText(ItemDef def, InvItem it)
    {
        var L = new List<string> { string.IsNullOrEmpty(it.CustomName) ? def.Name : it.CustomName };
        if (it.Amount > 1) L.Add($"Quantity: {it.Amount}");

        // Charged consumables (wine/pipes) report their charges where gear reports durability.
        if (def.IsCharged)
            L.Add($"{def.Text}: {(it.Dura == 0 ? def.Durability : it.Dura)}");
        else if (def.Durability > 0)
            L.Add($"Durability: {(it.Dura == 0 ? def.Durability : it.Dura)} / {def.Durability}");

        // The weapon's real swing range (the S/L columns Combat rolls from), not the ItmDam bonus. The
        // second line indents by "Damage: " (8 chars) so "Large:" sits under "Small:", as in the original.
        if (def.MaxSDam > 0 || def.MaxLDam > 0)
        {
            L.Add($"Damage: Small: {def.MinSDam}{DamRangeSep}{def.MaxSDam}");
            L.Add($"        Large: {def.MinLDam}{DamRangeSep}{def.MaxLDam}");
        }

        // One combined line, printed whole for anything wearable even when a term is 0 — that's how the
        // original reads, and "Armor: 0" is information (it tells you the item gives none).
        if (def.IsEquip || def.Armor != 0 || def.Hit != 0 || def.Dam != 0)
            L.Add($"Armor: {def.Armor}  Hit: {def.Hit}  Dam: {def.Dam}");

        Increase(L, "Vitality", def.Vita);
        Increase(L, "Mana",     def.Mana);
        Increase(L, "Might",    def.Might);
        Increase(L, "Will",     def.Will);
        Increase(L, "Grace",    def.Grace);
        Increase(L, "Healing",  def.Healing);
        Increase(L, "Wisdom",   def.Wisdom);
        if (def.Protection != 0) L.Add($"Protection: {def.Protection}");

        // "Strength:" is the STR the wearer must have to wield it (ItmMightRequired) — the real box labels
        // the requirement "Strength", not "Might", and the shop blurb agrees ("Strength of 35 req"). It sits
        // right after Protection in every surviving screenshot (Titanium 100, Heavy polearm 130, Ice garb 10).
        // A description, not an enforcement: the wear gate lives in CanWear, this only states the number.
        if (def.MightReq > 0) L.Add($"Strength: {def.MightReq}");

        // "Owner:" is the BOUND owner, not whoever is holding it — which is why it shows on some items in
        // the original and not on others with an otherwise identical layout. Binding is a real mechanic
        // (NPC subpath weapons arrive bound; a quest upgrade like Spike -> Enchanted Spike binds the result),
        // so this is keyed off the bind and stays absent for ordinary loot.
        if (!string.IsNullOrEmpty(it.Owner)) L.Add($"Owner: {it.Owner}");

        // The class/rank requirement, exactly as the original box prints it — ONE line, not the level and
        // the mark on separate rows:
        //   * Mark > 0 -> the path's own RANK TITLE alone, no level: "Il san (P)" (Warded robes), and for a
        //     Peasant-path rank item just "Peasant" (Molten blade is mark 2 / level 99 yet reads "Peasant").
        //   * else with a level -> "<Path> Level <n>": "Mage Level 99", "Peasant Level 50".
        //   * else (equip, no level) -> the bare path name "Peasant" (Heavy/Military polearm), never "Level 0".
        // Stated flat, as a requirement: the box describes the ITEM, so it reads the same whoever holds it and
        // never annotates which gates the viewer currently fails — that's the wear path's business (CanWear).
        if (def.Mark > 0)
            L.Add(Content.PathTitle(def.PathId, def.Mark));
        else if (def.IsEquip || def.Level > 0)
            L.Add(def.Level > 0 ? $"{Content.PathName(def.PathId)} Level {def.Level}"
                                : Content.PathName(def.PathId));
        // NO sex line. `ItmSex` is a wear gate, not a description: the original box never printed one, and
        // the enforcement lives where it belongs — EquipFromSlot refuses the item outright (2026-08-07).
        if (def.NoDrop) L.Add("Cannot be dropped.");
        // Break-on-death is a warning, not a field: shown only when it's true, and always last so it reads
        // as the closing note on the item rather than another stat row.
        if (def.BreakOnDeath) L.Add("Break on death");
        return L;
    }

    /// <summary>What the original prints between a damage range's two numbers. It renders as a lowercase
    /// 'm' in every surviving screenshot of the box (English "90m140", French "55m65"); whether that is a
    /// literal 'm' or the client's glyph for a range character is unknown, so this reproduces what is on
    /// screen. One character to change if it turns out to be a tilde.</summary>
    private const string DamRangeSep = "m";

    // "<Stat> increase: N" — a single space after the colon (the real box does NOT column-align the values;
    // "Grace increase: 1" and "Strength: 10" sit at different depths in every screenshot). Omitted entirely
    // when the item doesn't touch that stat.
    private static void Increase(List<string> lines, string stat, int v)
    {
        if (v != 0) lines.Add($"{stat} increase: {v}");
    }

    private void HandleProfileRequest(byte[] dec)
    {
        byte sub = dec.Length > 0 ? dec[0] : (byte)0;
        Log.Info($"   -> PROFILE request (0x2D) sub={sub}");
        if (sub == 0) SendSelfProfile();
    }

    // The F2 key is NOT a menu — it's bound to "Subpath Chat" (RTK rtklua/.../welcomeNmail.lua: "F2 - Turn
    // Subpath Chat On/Off!"). It fires through the SAME 0x43 click-info packet as a real entity click, but
    // with the sentinel id 0xFFFFFFFE instead of a real entity id (RTK clif.c clif_handle_clickgetinfo:
    // `if (RFIFOL(...) == 0xFFFFFFFE) { toggle subpath_chat; sendminitext; return; }`, checked BEFORE the
    // normal map_id2bl lookup). Subpath chat is a server-wide channel to every other player of your same
    // class who also has it toggled on (clif_sendsubpathmessage) — see DoSubpathChat.
    private const uint SubpathChatSentinel = 0xFFFFFFFE;

    // F1 is the adjacent sentinel: RTK map.h `#define F1_NPC 4294967295` (0xFFFFFFFF). Clicking it opens
    // "Central Functions" — a virtual NPC dialog with no physical map presence (RTK clif.c bypasses the
    // usual click proximity check for it: `nd->bl.m == 0` — it exists on every map at once). See
    // RunF1MenuAsync / §11k.
    private const uint F1MenuSentinel = 0xFFFFFFFF;

    // The client clicks an entity to inspect it: 0x43 = 01 entityId(u32BE) 00.
    private void HandleClickInfo(byte[] dec)
    {
        uint id = 0;
        if (dec.Length >= 5) id = (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]);
        Log.Info($"   -> CLICK-INFO (0x43) id={id}");

        if (id == SubpathChatSentinel) { ToggleSubpathChat(); return; }
        if (id == F1MenuSentinel) { OpenF1Menu(); return; }

        // id 0 (or explicitly our own id, e.g. "@click") -> our own PUBLIC profile — the 0x34 view-others
        // card, deliberately the same thing other players see when they click you. This is NOT the `s`
        // character sheet (0x39); clicking yourself and pressing `s` are different views by design, so both
        // clients answer a self click with 0x34.
        //
        // 5.33 note. The client also fires 0x43(self) ~3-4x/second unprompted; SendClickProfile throttles the
        // identical resends (see there) so that flood does not swamp the pane.
        if (id == 0 || id == _char.Id) { SendClickProfile(this); return; }

        // An NPC click opens its dialog instead of a profile. NPCs live in the shared mob list (as
        // non-fighting mobs), so MobById finds them; the IsNpc flag distinguishes them from a real creature.
        if (_world.MobById(_char.Map, id) is { IsNpc: true } npc) { OpenNpcDialog(npc); return; }

        // Clicking a real (non-NPC) mob: RTK's own handler (clif.c clif_handle_clickgetinfo, BL_MOB case)
        // runs "onLook", whose player-facing branch is gated on player.gmLevel > 0 -- stock RTK gives
        // regular players nothing back here. We deliberately diverge from that (2026-07-26, user request):
        // right-click-to-walk is client-local pathing we can't intercept (see §11 self-walk note), so the
        // only server-controllable feedback for "what IS that" is this click-info reply -- a name-only
        // mini-text readout, short of the GM-only name/id/level/HP/AC dump onLook does.
        // Just the NAME, no sentence around it ("Brown Rabbit", not "It's a Brown Rabbit.") — the mini-text
        // pane is a readout, and every other look-at line there is bare too.
        if (_world.MobById(_char.Map, id) is { } mob)
        {
            ObserveMob(mob);
            return;
        }

        // Otherwise, if the id resolves to another connected PLAYER, show THEIR real profile (RTK
        // clif_clickonplayer, same 0x34 opcode, populated from the target's own data via the SendClickProfile
        // overload above). This is the real "view others" window — its Group/Exchange status cells are what
        // the client uses to enable those buttons, which is how a real player actually starts a party/trade
        // (§11l), not a chat command. An id matching nobody at all (stale/disconnected) is a no-op.
        var target = _world.PlayerById(id);
        if (target is not null) SendClickProfile(target);
    }

    /// <summary>"What is that creature" — the one place both ways of looking at a mob converge: the 0x43
    /// click on its sprite (above, RTK's <c>onLook</c>) and the ';' look-at key (<see cref="HandleLookAt"/>,
    /// RTK's <c>clif_parselookat_2</c>, which RTK does not script at all). Both print the bare name, and both
    /// are the moment a quest may notice you looked — a player told to go and look at a bird must not be told
    /// they have not looked at it because they used the other key.</summary>
    private void ObserveMob(Mob mob)
    {
        SendMiniText(mob.Name);
        NoteBlueRooster(mob);
    }

    /// <summary>Step 3 of the Dagger Uniform quest: seeing the Blue Rooster that wanders southern Buya (see
    /// <see cref="DaggerUniformQuest"/>). Recorded only while Dagger is actually waiting for it, so looking at
    /// a rooster leaves no state behind for anyone else. RTK's <c>onLook</c> sets its flag silently; the
    /// mini-text is added because "look at a bird" is not an act a player can otherwise tell succeeded.</summary>
    private void NoteBlueRooster(Mob mob)
    {
        if (mob.Key != DaggerUniformQuest.RoosterMob) return;
        if (QuestStage(DaggerUniformQuest.Key) != DaggerUniformQuest.Stage.WatchForRooster) return;
        SetQuestStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.SeenRooster);
        Notify(DaggerUniformQuest.RoosterNoticed);
    }

    // F2: flip the subpath-chat toggle and confirm via mini-text (RTK: "Subpath Chat: ON"/"OFF" — same
    // wording, same channel used elsewhere for status confirmations). Persisted so it survives a relog.
    private void ToggleSubpathChat()
    {
        _char.SubpathChat = !_char.SubpathChat;
        SendMiniText($"Subpath Chat: {(_char.SubpathChat ? "ON" : "OFF")}");
        SaveChar();
        Log.Info($"   -> subpath chat {(_char.SubpathChat ? "ON" : "OFF")} for {_char.Name}");
    }

    // "/subpathchat <msg>" (alias "/sp") — RTK clif_sendsubpathmessage: broadcast to every OTHER ONLINE
    // player who shares your class AND has subpath chat toggled on (not map-scoped — this is a server-wide
    // channel, unlike say/shout). Formatted "<@Name> (ClassName) message" per RTK, rendered via the same
    // mini-text channel as whisper/status text.
    private void DoSubpathChat(string msg)
    {
        if (!_char.SubpathChat) { SendMiniText("Turn on Subpath Chat first (F2)."); return; }
        if (IsMuted()) { ReportMuted(); return; }   // server-wide channel — the LAST one a mute may leave open
        if (!Content.CanTalk(_char.Map)) { SendMiniText("Your voice is swept away by a strange wind."); return; }
        // Speaker label is the RANK title ("Inferno"), the audience is the PATH — ranks of one class share a
        // channel, which is what makes it a subpath channel rather than a rank channel.
        string line = $"<@{_char.Name}> ({ClassTitle}) {msg}";
        foreach (var p in _world.AllPlayers())
            if (p._char.SubpathChat && string.Equals(p._char.ClassName, _char.ClassName, StringComparison.OrdinalIgnoreCase))
                p.SendMiniText(line);
        Log.Info($"   -> subpath chat: \"{line}\"");
    }

    // ===== NPC dialog =============================================================================
    // Clicking an NPC (0x43) runs its behaviour here. An NPC is a COMPOSITION of reusable abilities
    // (Shop, Bank, Transport, …) declared in NpcScripts; its own definition holds only what's unique to it.
    // The flow is async: a behaviour awaits each prompt and the client's 0x3A reply (HandleNpcDialog)
    // completes that await, so behaviours read as linear code (menu -> branch -> loop) rather than a
    // callback tree — mirroring RTK's coroutine scripts. Everything runs on the read thread (the reply
    // completes the TaskCompletionSource inline), so it never races the session's other state.
    private readonly record struct DialogReply(byte Kind, int Step, int MenuIndex, string Input);
    private TaskCompletionSource<DialogReply>? _dlgReply;   // the prompt currently awaiting a 0x3A reply

    private void OpenNpcDialog(Mob npc)
    {
        var def = Content.NpcById(npc.NpcDefId);
        Log.Info($"   -> NPC dialog: id={npc.Id} '{npc.Name}' def={npc.NpcDefId}");
        if (def is null) { SendScriptMessage(npc.Id, $"{npc.Name}\n\nGreetings, traveller.", NpcPortrait(npc), npc.Color); return; }
        _ = RunNpcAsync(npc, def);   // fire-and-forget: suspends on the first prompt, resumes on the reply
    }

    // Assemble the NPC's top menu from its abilities' entries and dispatch the pick. Identical for every
    // NPC — the abilities carry all the behaviour, so nothing NPC-specific lives here.
    private async Task RunNpcAsync(Mob npc, NpcDef def)
    {
        try
        {
            var ctx = new NpcContext(this, npc, def);

            // A ghost in the tutorial area: EVERY NPC here stands in for a Shaman. The area has none of its
            // own and Silver Thread is refused inside it, so without this a player who dies in the first
            // rooms is stuck as a ghost with nothing in the world able to help them. Deliberately the whole
            // area and every NPC in it — a beginner should not have to work out WHICH one to click.
            //
            // Runs ahead of both the Lua and C# dispatch so it also covers NPCs whose own dialog would
            // otherwise talk past the fact that the player is dead. Reuses the Shaman's own script rather
            // than a copy, so the wording can only ever drift in one place.
            if (IsDead && Content.IsTutorialMap(_char.Map)) { await ReviveAbility.Resurrect(ctx); return; }

            // Data-driven Lua dialog (game-data/npc_dialog.lua): if this NPC identifier has a Lua script,
            // it OWNS the conversation (run it, done). Strictly additive — only NPCs we've authored a script for
            // take this path; every other NPC (and a broken/absent .lua) falls straight through to the C#
            // abilities below, unchanged. Hot-reloads via @reload. See Server/NpcScript.cs.
            if (NpcScript.Has(def.Key)) { await NpcScript.RunAsync(ctx, def.Key); return; }

            var abilities = NpcScripts.For(def);
            var entries = abilities.SelectMany(a => a.Entries(ctx)).ToList();
            if (entries.Count == 0)
            {
                // A speech-only NPC (only INpcSayHandler, no click options) does nothing on click — you
                // interact by speaking to it. Only a truly featureless NPC gives the generic greeting.
                if (!abilities.OfType<INpcSayHandler>().Any()) await ctx.Say("Greetings, traveller.");
                return;
            }

            // One-option NPCs (the tutors Jadespear/Ironheart, a lone-service vendor) skip straight into that
            // service — a "How can I help you today? -> [the only thing]" wrapper menu is pure friction and
            // isn't how RTK scripts behave (they dive into their dialog on click). The picker only appears
            // when there's a real choice to make.
            if (entries.Count == 1) { await entries[0].run(ctx); return; }

            int choice = await ctx.Menu($"{def.Name}: How can I help you today?", entries.Select(e => e.label).ToList());
            if (choice >= 1 && choice <= entries.Count) await entries[choice - 1].run(ctx);
        }
        catch (Exception e) { Log.Error($"NPC dialog '{npc.Name}' threw for '{_char.Name}': {LuaVerbHost.Describe(e)} — conversation abandoned", e); }
    }

    // ===== F1: "Central Functions" menu ===========================================================
    // RTK's f1npc.lua has ~15 entries (GM tools, Kan donations, tutor management, minigame stats, webpage
    // profile settings…) that depend on systems this server doesn't model. Trimmed to what's real here:
    // Silver Thread (shaman resurrection — RTK's actual answer to "how do you get un-ghosted", replacing
    // the old fixed-timer auto-revive; always listed, gated inside) and Choose a Path (the same Peasant-level-5 guild warp §11j's Peasant
    // wall points at, offered as a menu shortcut instead of walking to the physical hall). The old "Toggles"
    // submenu (just the Subpath Chat flip) was removed — that toggle is F2's own binding (ToggleSubpathChat).

    // A virtual "npc" for the F1 dialog wire format — portrait/menu framing only. It's never spawned or
    // looked up; SendNpcMenu/SendScriptMessage just need an id+sprite for the packet header. Sprite 0 ->
    // NpcPortrait renders no portrait icon, matching "this isn't a real character".
    private static readonly Mob F1VirtualNpc = new(F1MenuSentinel, 0, 0, 0, "F1Npc", 1);

    private void OpenF1Menu() => _ = RunF1MenuAsync();

    private async Task RunF1MenuAsync()
    {
        var npc = F1VirtualNpc;
        var opts = new List<string>();
        // Always listed, like "Recover Death Pile" below: the branch itself explains what it's for when you're
        // alive ("...you are not dead, so you have no path with me"), so a living player still learns that F1
        // is how you get back from the dead. Hiding it until you're already a ghost teaches nobody.
        opts.Add("Silver Thread");
        if (CharClassId == 0 && _char.Level >= 5) opts.Add("Choose a Path");
        // Always offered, exactly as RTK does it: the branch itself is what TEACHES the ability, explaining the
        // facing/two-step rule when there's nothing to recover. Hiding it until a pile happens to be underfoot
        // would mean nobody ever discovers it exists.
        opts.Add("Recover Death Pile");

        // (The Subpath Chat toggle that used to live under "Toggles" is F2's job — see ToggleSubpathChat.)
        int choice = await DlgMenu(npc, $"Hello {_char.Name}! How can I help you today?", opts);
        if (choice < 1 || choice > opts.Count) return;

        switch (opts[choice - 1])
        {
            case "Silver Thread": await SilverThread(npc); break;
            case "Choose a Path": await ChoosePathMenu(npc); break;
            case "Recover Death Pile": await RecoverDeathPileMenu(npc); break;
        }
    }

    // "Recover Death Pile" (RTK f1npc.lua's branch of the same name + player.lua recoverDeathPile): pull your
    // own looter-locked death pile back from up to two tiles in front of you — the point being that it works
    // even when someone is standing on it, which an ordinary ',' pickup can't do. Only YOUR still-locked stacks
    // move; unlocked floor loot and other people's piles are untouched (Session.RecoverDeathPile).
    // The gates and both refusal texts are RTK's own, including the help text that doubles as the tutorial.
    private async Task RecoverDeathPileMenu(Mob npc)
    {
        if (!DeathPileInReach())
        {
            await DlgSay(npc, "This ability allows you to recover your lost items when an unscrupulous player is standing over them. " +
                              "To use this ability you must first face the items you dropped upon death. You must be only one or two steps away from them.");
            await DlgSay(npc, "Then press F1 and select \"Recover Death Pile\". Your items will be recovered even if a would-be thief is standing on them! " +
                              "To use this ability, you must be alive. If you do not have enough room in your inventory, you will be unable to recover all of your items.");
            return;
        }
        if (IsDead) { await DlgSay(npc, "You can't recover your death pile while you are dead."); return; }

        int taken = RecoverDeathPile();
        if (taken == 0) return;   // pack was already full on the first stack — GiveItem said so
        // RTK: sendAction(6, 20) then talk(2, "I'll take that.") — the reach-out pose, then a PUBLIC bubble
        // (chatType 2, the same line Filch speaks), so anyone loitering over the pile sees who took it back.
        SendAction(_char.Id, 6, 20, 0);
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 6, 20, 0), except: this);
        var line = AsciiBytes("I'll take that.");   // RTK talk(2) — proximity-gated to onlookers near the pile
        _world.BroadcastArea(_char.Map, _char.X, _char.Y, SayHalfW, SayHalfH, p => p.SpeakEntity(2, _char.Id, line));
        Log.Info($"   -> death pile recovered: {taken} stack(s) by {_char.Name} @({_char.X},{_char.Y}) facing {_facing}");
    }

    // "Silver Thread": always listed but gated inside, which is RTK's own shape — picking it while alive just
    // says so and does nothing (no warp, no state change). Offers a Shaman by nation (RTK's country branches collapse to our two home nations);
    // picking one is PASSAGE ONLY — it warps the ghost to that Shaman's hut and leaves it a ghost. The
    // revival itself belongs to the Shaman NPC you then click (ReviveAbility), which is RTK's own split:
    // f1npc.lua's Silver Thread branch ends in a bare `player:warp(...)` and never touches state/health, and
    // shaman.lua's click is what does `state = 0; health = maxHealth`. The warp targets below are f1npc.lua's
    // literal coordinates. (This used to revive on arrival — a stand-in from when the Shaman NPCs had no
    // behaviour of their own and a warp alone would have stranded the player as a permanent ghost.)
    private async Task SilverThread(Mob npc)
    {
        // The tutorial area is sealed on purpose: every Shaman this offers is out in the world, so letting a
        // player take passage would eject them from the chain permanently (there is no warp back in) — and
        // they could do it ALIVE, since the passage itself never checked. Answer with what the ability is
        // for instead, and point at the area's own stand-in: while dead, any NPC here revives you
        // (see RunNpcAsync). Checked before the IsDead branch so it covers a living player who picks it out
        // of curiosity, which is the more common way to find this menu.
        if (Content.IsTutorialMap(_char.Map))
        {
            await DlgSay(npc, "This ability will let you return to a shaman to resurrect.  " +
                              "For now just click on one of the merchants in this area.");
            return;
        }

        if (!IsDead)
        {
            await DlgSay(npc, "This is for the dead of the land to find a path to the shaman. You are not dead, so you have no path with me.");
            return;
        }

        var shamans = _char.Nation == 2
            ? new (string label, ushort map, ushort x, ushort y)[]
              { ("Felis, to the West of Buya.", 338, 4, 4), ("Storm, to the East of Buya.", 339, 3, 5) }
            : new (string label, ushort map, ushort x, ushort y)[]
              { ("Dusk, to the West of Kugnae.", 8, 6, 4), ("Dawn, to the East of Kugnae.", 9, 3, 5) };

        await DlgSay(npc, "Ah, another who walks amongst the ranks of the dead... but it is not your time yet... " +
                          "I will give you passage to a Shaman to give you life again.");
        int choice = await DlgMenu(npc, "Which Shaman would you like to visit?", shamans.Select(s => s.label).ToList());
        if (choice < 1 || choice > shamans.Length) return;
        var s = shamans[choice - 1];
        if (Warp(s.map, s.x, s.y)) SendMiniText("Speak with the Shaman to return to the world of the living.");
    }


    // "Choose a Path": warp to the guild-entrance map for the chosen class (per-nation, PathHalls' outer map
    // ids) — a menu shortcut for the same Peasant-level-5 milestone the physical path halls gate on
    // (TryPathHallWarp). Doesn't assign the class itself; a Guildmaster NPC inside does that (NpcAbility's
    // path-choice ability, SetCharClass) — matches RTK's own level5popupDialog, which only warps too.
    private async Task ChoosePathMenu(Mob npc)
    {
        var guilds = _char.Nation == 2
            ? new (string name, ushort map)[] { ("Warrior's Guild", 341), ("Rogue's Guild", 343), ("Mage's Guild", 342), ("Poet's Guild", 344) }
            : new (string name, ushort map)[] { ("Warrior's Guild", 11), ("Rogue's Guild", 15), ("Mage's Guild", 13), ("Poet's Guild", 17) };

        int choice = await DlgMenu(npc, "Please select a guild that you'd like to visit.", guilds.Select(g => g.name).ToList());
        if (choice < 1 || choice > guilds.Length) return;
        var g = guilds[choice - 1];
        if (Content.Maps.TryGetValue(g.map, out var mi)) EnterMap(mi.Id, mi.Xs, mi.Ys, 8, 7, mi.Name);
    }

    // ---- async dialog primitives (used by NpcContext, which abilities call) ---------------------
    // Each sends a 0x30 and awaits the client's 0x3A. A menu returns the 1-based pick (0 = cancelled).
    internal async Task<int> DlgMenu(Mob npc, string prompt, IReadOnlyList<string> options)
    {
        SendNpcMenu(npc, prompt, options);
        var r = await AwaitReply();
        return r.Kind == 0x02 ? r.MenuIndex : 0;
    }

    /// <summary>A menu whose portrait is the PLAYER's own paperdoll wearing <paramref name="face"/> instead of
    /// the NPC's head — a try-on that touches nothing but this one packet (RTK does the same thing with a
    /// throwaway `clone` NPC and `player.gfxFace`, since neither server wants to mutate the real character to
    /// preview a purchase). Used by AppearanceAbility's Change Face browse.</summary>
    internal async Task<int> DlgMenuFace(Mob npc, string prompt, IReadOnlyList<string> options, int face)
    {
        SendNpcMenuP(npc, DialogPortrait.Player(SelfAppearance(face), npc), prompt, options);
        var r = await AwaitReply();
        return r.Kind == 0x02 ? r.MenuIndex : 0;
    }

    internal async Task DlgSay(Mob npc, string text)
    {
        // next:true gives the box a "continue" affordance — the click the client answers with a 0x3A that
        // resumes this await. A prev/next-less box has "nothing to do": dismissing it sends no reply and hangs.
        SendScriptMessage(npc.Id, text, NpcPortrait(npc), npc.Color, next: true);
        await AwaitReply();   // hold the script until the player advances the box
    }

    // Free-text input box. Returns the typed string, or null if the player cancelled. The client confirms a
    // real submit with kind 4 + step 2 (RTK clif_parsenpcdialog requires RFIFOB(fd,13)==2); anything else is
    // a cancel/close.
    internal async Task<string?> DlgInput(Mob npc, string prompt)
    {
        SendInputBox(npc, prompt);
        var r = await AwaitReply();
        return r.Kind == 0x04 && r.Step == 0x02 ? r.Input : null;
    }

    /// <summary>Is a prompt currently waiting on the client's 0x3A? True means the player is sitting in a
    /// MODAL box, and anything that would open another one has to stand down — <see cref="AwaitReply"/>
    /// overwrites the pending completion source, which orphans the conversation that was waiting on it.</summary>
    internal bool DialogBusy => _dlgReply is not null;

    /// <summary>Push a multi-page dialog at the player with NO NPC in front of them — a scripted interjection
    /// (a milestone briefing) rather than a conversation they started. Same 0x30 frame as
    /// <see cref="DlgSeq"/>, differing only in what goes in the entity-id and portrait slots:
    ///
    /// <list type="bullet">
    /// <item>The id is the PLAYER's own. The client only uses it to associate the box with an on-screen
    /// entity, and our 0x3A handler never reads it back (see <see cref="HandleNpcDialog"/>) — but it must be
    /// an entity the client actually has, and the player is the one entity always in their own view. A real
    /// NPC's id would not do: the speaker is typically a city away.</item>
    /// <item>The portrait is passed explicitly rather than derived from a <see cref="Mob"/>, since there is no
    /// mob here. Callers hand over the look/colour of whoever is notionally speaking.</item>
    /// </list></summary>
    internal async Task DlgPush(int look, int color, IReadOnlyList<string> pages)
    {
        var p = DialogPortrait.Look(look, color);
        foreach (var page in pages)
        {
            SendScriptMessageP(_char.Id, page, p, prev: false, next: true);
            await AwaitReply();
        }
    }

    /// <summary>The input-box twin of <see cref="DlgPush"/> — ask a free-text question on behalf of a speaker
    /// that has no mob in the world (the Fox spirit, which is conjured by a step, never placed). Same
    /// entity-id and portrait reasoning as DlgPush. Returns the typed string, or null if they cancelled.</summary>
    internal async Task<string?> DlgInputPush(int look, int color, string prompt)
    {
        SendInputBox(_char.Id, DialogPortrait.Look(look, color), prompt);
        var r = await AwaitReply();
        return r.Kind == 0x04 && r.Step == 0x02 ? r.Input : null;
    }

    private Task<DialogReply> AwaitReply()
    {
        var tcs = new TaskCompletionSource<DialogReply>();
        _dlgReply = tcs;      // a new click orphans any previous pending prompt (it's GC'd, never resumes)
        return tcs.Task;
    }

    // ---- shop ability implementation (Buy / Sell) ----------------------------------------------
    // Looped so the window stays open: pick -> confirm -> back to the list; cancel (0) to leave. Reads as a
    // shop should — the async layer is what makes this straight-line instead of a web of callbacks.
    internal async Task DlgBuy(Mob npc, Shops.Category[]? catalogue)
    {
        var cats = catalogue?.Where(c => c.Keys.Any(k => Content.ItemByKey(k) is not null)).ToList() ?? new();
        if (cats.Count == 0) { await DlgSay(npc, "I've nothing to sell right now."); return; }

        Shops.Category cat;
        if (cats.Count == 1) cat = cats[0];   // flat shop (inn) — no category step
        else
        {
            int ci = await DlgMenu(npc, "What would you like to buy?", cats.Select(c => c.Name).ToList());
            if (ci < 1 || ci > cats.Count) return;
            cat = cats[ci - 1];
        }

        // The native icon grid (0x2f sub-kind 4) rather than a text menu — it carries the item's real Item.epf
        // icon, price and blurb, which a menu line can't. It answers by NAME, so the row list is what maps a
        // reply back to an item; duplicate names in one catalogue would be indistinguishable (none exist today).
        var items = cat.Keys.Select(Content.ItemByKey).OfType<ItemDef>().ToList();
        while (true)
        {
            SendBuyGrid(npc, "What would you like?", items.Select(d => ShopRow(d, d.BuyPrice)).ToList());
            var pick = await AwaitShopReply();
            if (pick.Name.Length == 0) return;                      // closed the window -> done shopping
            var it = items.FirstOrDefault(d => d.Name.Equals(pick.Name, StringComparison.OrdinalIgnoreCase));
            if (it is null) { Log.Info($"   ?? buy: no catalogue row named '{pick.Name}'"); return; }
            if (_char.Coins < (uint)it.BuyPrice) { await DlgSay(npc, $"You can't afford {it.Name} ({it.BuyPrice} gold)."); continue; }
            // Pack full ends the visit with a dialog, not a bubble — it's the one refusal the player has to
            // act on before anything else here can work. The carry cap sends its own minitext from inside
            // GiveItem, so CarryRoom distinguishes the two failures.
            if (!GiveItem(it, quiet: true))
            {
                if (CarryRoom(it) > 0)
                    await DlgSay(npc, "You don't have enough hands to carry all of that, free up some space in your inventory then come back to me.");
                return;
            }
            _char.Coins -= (uint)it.BuyPrice;
            SendStats();
            MarkDirty();
            Log.Info($"   -> BUY '{it.Name}' -{it.BuyPrice}g (coins now {_char.Coins})");
            await DlgSay(npc, $"You bought {it.Name} for {it.BuyPrice} gold.");
        }
    }

    /// <summary><paramref name="buysFrom"/> is this NPC's accept list (item keys) — null for "buys anything
    /// sellable", which is what every shop did before <see cref="Shops.BuysFrom"/> existed. The grid is built
    /// from it, so an item the shop won't take simply isn't offered rather than being refused after the
    /// player has picked it.</summary>
    internal async Task DlgSell(Mob npc, IReadOnlySet<string>? buysFrom = null)
    {
        while (true)
        {
            var sellable = _char.Inventory.OrderBy(i => i.Slot)
                .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
                .Where(t => t.def is { NoDrop: false } && t.def.SellPrice > 0)
                .Where(t => buysFrom is null || buysFrom.Contains(t.def!.Key))
                .ToList();
            if (sellable.Count == 0) { await DlgSay(npc, "You have nothing I'd buy."); return; }

            // Sell grid (0x2f sub-kind 5) — only the bag slots go on the wire; the client draws each row's
            // icon and name from the inventory it already has. Same one-based wire slot as 0x0F, so it goes
            // out through WireSlot and the echoed reply comes home as -1.
            SendSellGrid(npc, "What would you like to sell?",
                         sellable.Select(t => WireSlot(t.inv)).ToList());
            var pick = await AwaitShopReply();
            var hit = sellable.FirstOrDefault(t => WireSlot(t.inv) == pick.Slot);
            if (hit.def is null) return;               // closed the window, or a slot we didn't offer
            var (inv, def) = hit;

            // A stack asks how many, in the client's own amount box, rather than silently selling one.
            int qty = 1;
            if (inv.Amount > 1)
            {
                var n = await AskAmount(npc, $"How many {def!.Name} would you like to sell?", def.Name, inv.Amount);
                if (n is null) return;
                qty = Math.Clamp(n.Value, 0, inv.Amount);
                if (qty <= 0) continue;                // typed 0 / cancelled -> back to the list
            }

            // Quote the total and let them back out. The price is only visible at this point — the sell grid
            // draws from the client's own bag, so no row can show what it's worth — which is exactly why the
            // real game asks. Anything but Yes falls back to the list rather than ending the conversation.
            int total = def!.SellPrice * qty;
            if (await DlgMenu(npc, $"I'll pay you {total} gold for that, is it a deal?",
                              new[] { "Yes", "No" }) != 1) continue;
            _char.Coins += (uint)total;
            // Reason 10 is literally "You sold <item>." (9 is "You gave", for a bank deposit — an earlier
            // comment here had those two the wrong way round). Sent only when the whole entry goes; selling
            // part of a stack redraws it and stays silent. That client line is the whole confirmation — the
            // gold figure was already quoted and accepted a step ago, so a dialog box repeating it would be
            // a second thing to dismiss between the sale and the list reappearing.
            inv.Amount -= qty;
            if (inv.Amount <= 0) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 10); }
            else SendAddItem(inv);
            SendStats();
            MarkDirty();
        }
    }

    // ---- parcels (MessengerAbility; RTK messenger.lua "Send Parcel"/"Receive Parcel" -> Parcel.lua) ----
    // Parcels are item/gold sent player-to-player, queued at the messenger for pickup — SEPARATE from n-mail
    // (see Parcel.cs). Both flows are ordinary 0x30 dialogs (menu/input/say), so unlike the mail compose
    // window there's nothing client-side to reverse: this is fully server-driven.

    /// <summary>Does the player have any parcel waiting (gates the messenger's "Receive Parcel" entry)?</summary>
    internal bool HasWaitingParcels => Parcel.HasAny(_char.Name);

    // RTK sendParcelTo: choose Gold or Item, name a recipient (offline OK — resolved against the char store),
    // pay a seal, and it's queued at the messenger. Items must be tradeable (not NoDrop), non-food (Type 0),
    // and fully repaired; a 5% seal fee (RTK item.price*.05) is charged per item parcel. Gold parcels are
    // free to send, matching RTK. Coin/possession are RE-checked after each async prompt before committing.
    internal async Task ParcelSendFlow(Mob npc)
    {
        int kind = await DlgMenu(npc, "What would you like to send?", new[] { "Gold", "Item" });
        if (kind < 1) return;

        if (kind == 1)   // ---- gold ----
        {
            var amtStr = await DlgInput(npc, "How much gold would you like to send?");
            if (amtStr is null) return;
            if (!int.TryParse(amtStr.Trim(), out int gold) || gold <= 0) { await DlgSay(npc, "That's not a valid amount."); return; }
            if (_char.Coins < (uint)gold) { await DlgSay(npc, "You don't have that much gold."); return; }

            var to = await DlgInput(npc, $"Who do you want to send this {gold:N0} gold to?");
            if (to is null) return;
            var recip = ResolveParcelRecipient(to.Trim());
            if (recip is null) { await DlgSay(npc, "Character does not exist."); return; }
            if (recip.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { await DlgSay(npc, "You can't send a parcel to yourself."); return; }
            if (_char.Coins < (uint)gold) { await DlgSay(npc, "You don't have that much gold."); return; }

            _char.Coins -= (uint)gold;
            var g = DateTime.UtcNow;
            Parcel.Send(recip, _char.Name, -1, gold, 0, "", (byte)g.Month, (byte)g.Day);
            SendStats(); MarkDirty();
            NotifyParcelRecipient(recip);
            await DlgSay(npc, $"Your {gold:N0} gold has been sent in a parcel to {recip}.");
            return;
        }

        // ---- item ----
        var sendable = _char.Inventory.OrderBy(i => i.Slot)
            .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
            .Where(t => t.def is not null && t.def.Type != 0 && !t.def.NoDrop)   // no food (Type 0 EAT), no bound/no-drop
            .ToList();
        if (sendable.Count == 0) { await DlgSay(npc, "You have nothing you could send."); return; }

        int pick = await DlgMenu(npc, "What would you like to send?", sendable.Select(t => t.def!.Name).ToList());
        if (pick < 1 || pick > sendable.Count) return;
        var (item, def) = sendable[pick - 1];

        int amount = 1;
        if (def!.Stackable && item.Amount > 1)
        {
            var aStr = await DlgInput(npc, $"How many {def.Name} do you want to send?");
            if (aStr is null) return;
            if (!int.TryParse(aStr.Trim(), out amount) || amount <= 0) { await DlgSay(npc, "That's not a valid amount."); return; }
            amount = Math.Min(amount, item.Amount);
        }

        if (item.Dura != def.Durability) { await DlgSay(npc, "Item must be in perfect condition to send. Go and repair it first!"); return; }

        var to2 = await DlgInput(npc, $"Who do you want to send this {amount} {def.Name} to?");
        if (to2 is null) return;
        var recip2 = ResolveParcelRecipient(to2.Trim());
        if (recip2 is null) { await DlgSay(npc, "Character does not exist."); return; }
        if (recip2.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { await DlgSay(npc, "You can't send a parcel to yourself."); return; }

        int fee = Math.Max(1, (int)Math.Ceiling((def.BuyPrice > 0 ? def.BuyPrice : def.SellPrice) * 0.05 * amount));
        if (_char.Coins < (uint)fee) { await DlgSay(npc, $"I need {fee:N0} gold for the seal. Come back when you can afford it."); return; }

        // Re-verify possession AFTER the async prompts, then remove the stack and charge the seal.
        if (!RemoveInventoryStack(item, amount)) { await DlgSay(npc, $"You no longer have {amount} {def.Name}."); return; }
        _char.Coins -= (uint)fee;
        var d = DateTime.UtcNow;
        Parcel.Send(recip2, _char.Name, def.Id, amount, item.Dura, item.CustomName, (byte)d.Month, (byte)d.Day, item.Owner);
        SendStats(); MarkDirty();
        NotifyParcelRecipient(recip2);
        await DlgSay(npc, "Your parcel has been sent.");
    }

    // RTK receiveParcelFrom: hand over the oldest waiting parcel — gold to the purse, an item to the bag (or
    // dropped at the player's feet if the pack is full, the same recovery as reading a mail attachment). Loops
    // one parcel at a time while more remain and the player wants to keep collecting.
    internal async Task ParcelReceiveFlow(Mob npc)
    {
        while (true)
        {
            var list = Parcel.ListFor(_char.Name);
            if (list.Count == 0) { await DlgSay(npc, "You have no parcels waiting."); return; }

            var p = list[0];                                   // FIFO by position

            // ONE transaction: the parcel leaves the queue and the goods land in the bag together, or
            // neither happens. Previously the claim committed on its own and the character saved up to
            // AutoSaveMs later — a crash in that window destroyed the parcel outright (gone from the queue,
            // never delivered). The conditional DELETE inside the transaction is still the double-claim
            // guard, so nothing is lost by moving it in here.
            var snapshot = SnapshotBag();
            ParcelItem? got = null;
            string? say = null;
            // The pack-full fallback drops the goods at the player's feet. That drop is deferred until AFTER
            // the commit on purpose: ground items are pure runtime state with no database row, so dropping
            // inside the transaction and then failing to commit would leave the item lying there AND the
            // parcel still claimable — the exact duplication this whole path exists to prevent.
            GroundItem? pendingDrop = null;
            bool committed = _store.SaveWith(_char, (cn, tx) =>
            {
                got = Parcel.ClaimIn(cn, tx, _char.Name, p.Position);
                if (got is null) return false;                  // already taken by another path — re-list

                if (got.IsGold)
                {
                    _char.Coins += (uint)got.Amount;
                    say = $"You receive {got.Amount:N0} gold from {got.Sender}.";
                    return true;
                }

                var def = Content.ItemById(got.ItemId);
                if (def is null)
                {
                    say = "One of your parcels held something I no longer recognize; I've discarded it.";
                    return true;   // still consume the row — an item id we can't resolve isn't deliverable
                }

                bool gotIt = GiveItem(def, got.Amount, (ushort)Math.Max(0, got.Dura), got.Engrave, owner: got.Owner);
                if (!gotIt)
                    pendingDrop = new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
                        X = _char.X, Y = _char.Y, Amount = got.Amount, Dura = (ushort)Math.Max(0, got.Dura), Graphic = def.Icon,
                        Owner = got.Owner };
                say = gotIt
                    ? $"You receive a parcel from {got.Sender}: {def.Name} x{got.Amount}."
                    : $"A parcel from {got.Sender} held {def.Name} x{got.Amount}, but your pack was full — it's at your feet.";
                return true;
            });

            if (!committed)
            {
                // Either the row was already gone (re-list and try the next one) or the write failed. Both
                // need the in-memory give rolled back: the callback may have run to completion and only the
                // COMMIT failed, and leaving that standing would let the next autosave persist an item whose
                // parcel row is still in the queue — a dupe.
                RestoreBag(snapshot);
                if (got is null) continue;
                await DlgSay(npc, "I couldn't hand that over just now — try me again in a moment.");
                Log.Info($"!! parcel claim FAILED for '{_char.Name}' pos={p.Position} — rolled back, parcel kept");
                return;
            }

            if (pendingDrop is not null) _world.DropItem(_char.Map, pendingDrop);   // committed — safe to materialize
            SendStats();
            if (say is not null) await DlgSay(npc, say);

            RefreshMailFlags();   // claiming a parcel may clear the HUD bag flag (SendStats above sent the stale cache)
            if (!Parcel.HasAny(_char.Name)) return;
            int more = await DlgMenu(npc, "You have more parcels waiting. Collect another?", new[] { "Yes", "No" });
            if (more != 1) return;
        }
    }

    /// <summary>Resolve a typed recipient name to a deliverable one: an online player, else an existing stored
    /// character (offline delivery, like mail). Null if nobody by that name exists. The table is COLLATE
    /// NOCASE so the exact casing stored here doesn't affect later lookups.</summary>
    private string? ResolveParcelRecipient(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_world.FindPlayer(name) is not null) return name;
        return _store.Exists(name) ? name : null;
    }

    /// <summary>Remove <paramref name="amount"/> from a bag stack and update the client (whole stack removed
    /// with reason 7 = "You posted &lt;item&gt;.", the client's own parcel line — it was reason 4, which says
    /// "You threw &lt;item&gt;."). False without change if the stack is gone or too small — the possession
    /// re-check after the async send prompts.</summary>
    private bool RemoveInventoryStack(InvItem inv, int amount)
    {
        if (!_char.Inventory.Contains(inv) || inv.Amount < amount) return false;
        inv.Amount -= amount;
        if (inv.Amount <= 0) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 7); }
        else SendAddItem(inv);
        MarkDirty();
        return true;
    }

    /// <summary>Light an online recipient's HUD bag icon immediately and tell them a parcel arrived (RTK
    /// msg(12, "[PARCEL]: You got a parcel from X!")). No-op if they're offline — they'll see the icon on
    /// their next login, driven by MailParcelFlags.</summary>
    private void NotifyParcelRecipient(string name)
    {
        var p = _world.FindPlayer(name);
        if (p is null) return;
        p.RefreshMailFlags();   // recompute + push the recipient's bag flag (SendStats alone would send the stale cache)
        p.SendMiniText($"[PARCEL]: You got a parcel from {_char.Name}!");
    }

    // ---- spoken shop shortcut ("buy [my] [all|N] <item>") — see ShopAbility.OnSay ----------------
    // Spoken "buy [all|N] <item>": sell up to `amount` (whole stack if <= 0) of a fuzzy-matched
    // item, by name, from the bag. Tries the plural form as typed, then singularized (item names in the
    // registry are singular, e.g. "acorn", while the spoken word is often plural, "acorns"). Returns false
    // (not a dialog line) when nothing matches, so unrelated speech still falls through to normal chat.
    internal bool SellItemToNpcByName(Mob npc, string name, int amount, IReadOnlySet<string>? buysFrom = null)
    {
        var def = Content.FindItem(name) ?? Content.FindItem(Singularize(name));
        if (def is null || def.SellPrice <= 0 || def.NoDrop) return false;
        // Not on this shop's accept list (see DlgSell): a real item, so the speech IS handled — it just gets a
        // refusal rather than falling through to open chat and shouting "buy my sword" at the whole map.
        if (buysFrom is not null && !buysFrom.Contains(def.Key))
        { NpcBubble(npc, $"I don't buy {def.Name}."); return true; }

        var stack = _char.Inventory.Where(i => i.ItemId == def.Id).OrderBy(i => i.Slot).ToList();
        if (stack.Count == 0) { NpcBubble(npc, "You don't have enough."); return true; }   // RTK sellNoConfirm

        int remaining = amount > 0 ? amount : stack.Sum(i => i.Amount);
        int sold = 0; uint earned = 0;
        foreach (var inv in stack)
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, inv.Amount);
            earned += (uint)def.SellPrice * (uint)take;
            sold += take;
            remaining -= take;
            inv.Amount -= take;
            if (inv.Amount <= 0) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 10); }   // "You gave X.", as above
            else SendAddItem(inv);
        }
        _char.Coins += earned;
        SendStats();
        MarkDirty();
        // RTK: "I bought <item> for <N> coins." — the count goes in parens before the item when > 1.
        NpcBubble(npc, sold == 1 ? $"I bought {def.Name} for {earned} coins."
                                 : $"I bought ({sold}) {def.Name} for {earned} coins.");
        return true;
    }

    private static string Singularize(string s) => s.Length > 1 && s.EndsWith('s') ? s[..^1] : s;

    // ---- spoken buy shortcut ("i buy [all] <item> [number N]") — see ShopAbility.OnSay -------------
    // Buy from THIS NPC's catalogue by name: `amount` units, or as many as gold + pack allow when <= 0 ("all").
    // Only items this NPC actually stocks can be bought (unlike selling, which any shop accepts). Returns false
    // when the name matches no known item at all, so unrelated speech falls through to normal chat.
    internal async Task<bool> BuyItemFromNpcByName(Mob npc, string name, int amount)
    {
        await Task.CompletedTask;   // async for symmetry with the sell/deposit shortcuts; no awaits needed
        var def = Content.FindItem(name) ?? Content.FindItem(Singularize(name));
        if (def is null) return false;

        var catalogue = Shops.For(Content.NpcById(npc.NpcDefId)?.Key ?? "");
        bool stocked = catalogue is not null &&
                       catalogue.Any(cat => cat.Keys.Any(k => Content.ItemByKey(k)?.Id == def.Id));
        if (!stocked || def.BuyPrice <= 0) { NpcBubble(npc, $"I do not sell {def.Name} here, please check elsewhere."); return true; }

        int want = amount > 0 ? amount : (int)(_char.Coins / (uint)Math.Max(1, def.BuyPrice));   // "all" = as many as affordable
        if (amount > 0 && _char.Coins < (long)def.BuyPrice * amount)   // explicit N you can't fully afford — RTK refuses the whole buy
        {
            NpcBubble(npc, amount == 1 ? $"You do not have enough coins to buy {def.Name}"
                                       : $"You do not have enough coins to buy ({amount}) {def.Name}");
            return true;
        }
        if (want <= 0) { NpcBubble(npc, $"You do not have enough coins to buy {def.Name}"); return true; }

        int bought = 0; uint spent = 0;
        for (int i = 0; i < want; i++)
        {
            if (_char.Coins < (uint)def.BuyPrice) break;
            // Pack full — the shopkeeper says so at length. quiet:true keeps GiveItem's bare minitext out of
            // the way, EXCEPT for the carry-cap refusal, which sends its own and needs no shopkeeper line.
            if (!GiveItem(def, quiet: true))
            {
                if (CarryRoom(def) > 0)
                    NpcBubble(npc, "You don't have enough hands to carry all of that, free up some space in your inventory then come back to me.");
                break;
            }
            _char.Coins -= (uint)def.BuyPrice;
            spent += (uint)def.BuyPrice;
            bought++;
        }
        if (bought > 0)
        {
            SendStats();
            MarkDirty();
            // RTK: "I sold <item> for <N> coins." — the count goes in parens before the item when > 1.
            NpcBubble(npc, bought == 1 ? $"I sold {def.Name} for {spent} coins."
                                       : $"I sold ({bought}) {def.Name} for {spent} coins.");
        }
        return true;
    }

    // ---- spoken vault query ("what have i deposited?") — see BankAbility.OnSay ---------------------
    // RTK checkBank answers with the COUNT of stored item stacks (coin isn't counted here): "I am keeping N of
    // your things." / "I am not keeping any of your things."
    internal void ShowBankContents(Mob npc)
    {
        int stacks = _char.BankItems.Count(bi => Content.ItemById(bi.ItemId) is not null);
        NpcBubble(npc, stacks == 0 ? "I am not keeping any of your things."
                                   : $"I am keeping {stacks} of your things.");
    }

    // ---- bank ability implementation (vault: coin + item storage) ------------------------------
    // Each action is its own NPC menu entry (BankAbility) rather than a combined "Banking" submenu, so they
    // are entered directly and return to the NPC menu when done. Storage lives on the Character
    // (BankMoney / BankItems) and persists via the store. Joint/shared accounts (RTK's multi-owner vaults)
    // are intentionally out of scope for a single-owner vault. Coin comes back out by voice
    // ("give my coins back" -> WithdrawItemFromBank), which is why there is no Withdraw Money entry.
    //
    // Every outcome is SPOKEN by the NPC (NpcBubble), never a dialog box, and in the same words the spoken
    // commands use — the menu and the voice command are two ways into one teller, so they should sound alike
    // (and bystanders hear the teller either way). Only the questions are dialogs.
    internal async Task BankDepositMoney(Mob npc)
    {
        var s = await DlgInput(npc, $"You carry {_char.Coins} coins. How much will you deposit?");
        if (s is null) return;   // cancelled
        long amt = Math.Min(Math.Min(ParseAmount(s), _char.Coins), Content.BankMax - _char.BankMoney);
        if (amt <= 0) { NpcBubble(npc, "You deposit nothing."); return; }
        _char.Coins -= (uint)amt;
        _char.BankMoney += (uint)amt;
        SendStats();
        MarkDirty();
        NpcBubble(npc, $"You deposit {amt} coins.");
    }

    private static bool IsCoinWord(string s) => s.Equals("coin", StringComparison.OrdinalIgnoreCase) || s.Equals("coins", StringComparison.OrdinalIgnoreCase);

    // Spoken "take my <item|coin> [count]" (BankAbility.OnSay) — deposits `amount` (whole stack if <= 0) of a
    // fuzzy-matched item, or coin if the word is "coin"/"coins", straight into the vault, no menu round trip.
    internal bool DepositItemToBank(Mob npc, string name, int amount)
    {
        if (IsCoinWord(name))
        {
            long amt = Math.Min(Math.Min(amount > 0 ? amount : _char.Coins, _char.Coins), Content.BankMax - _char.BankMoney);
            if (amt <= 0) { NpcBubble(npc, "You deposit nothing."); return true; }
            _char.Coins -= (uint)amt;
            _char.BankMoney += (uint)amt;
            SendStats();
            MarkDirty();
            NpcBubble(npc, $"You deposit {amt} coins.");
            return true;
        }

        var def = Content.FindItem(name) ?? Content.FindItem(Singularize(name));
        if (def is null) return false;

        var stack = _char.Inventory.Where(i => i.ItemId == def.Id).OrderBy(i => i.Slot).ToList();
        if (stack.Count == 0) { NpcBubble(npc, $"You don't have any {def.Name} to store."); return true; }

        // ItmDepositable, the registry's bank ban (RTK player.lua depositNoConfirm, refusal line verbatim).
        if (def.NoDeposit) { NpcBubble(npc, "You cannot deposit that item."); return true; }

        int intended = amount > 0 ? Math.Min(amount, stack.Sum(i => i.Amount)) : stack.Sum(i => i.Amount);

        // RTK refuses used/damaged goods for storage: gear below full durability, or a charged consumable with
        // spent charges (e.g. a sipped wine). Dura holds current durability for gear and remaining charges for
        // charged items; Durability is the full value, so Dura < Durability means it's been used. (Dura == 0 is
        // an unseeded legacy stack — treated as full.) Scan only the items this deposit would actually move.
        int need = intended;
        foreach (var inv in stack)
        {
            if (need <= 0) break;
            if ((def.IsEquip || def.IsCharged) && inv.Dura != 0 && inv.Dura < def.Durability)
            { NpcBubble(npc, "I don't want your junk. Ask a smith to fix it."); return true; }
            need -= Math.Min(need, inv.Amount);
        }

        // RTK charges a safe-keeping fee of 10% of the item's sell value per unit (rtklua depositNoConfirm);
        // the coin must be in hand up front or the deposit is refused.
        long fee = (long)Math.Ceiling(def.SellPrice * 0.10 * intended);
        if (fee > _char.Coins) { NpcBubble(npc, $"Excuse me you didn't give me enough. It's {fee} coins."); return true; }

        int remaining = intended;
        int moved = 0;
        foreach (var inv in stack)
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, inv.Amount);
            moved += take;
            remaining -= take;
            // Reason 10 = "You gave <item>." — right for handing the whole entry over, and only sent when the
            // entry really leaves the pack. A partial deposit takes the SendAddItem branch below and stays
            // silent: nothing left your bag, the count just dropped. (See BankDepositItem for the same split.)
            if (take >= inv.Amount) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 9); VaultAdd(inv); }
            else { inv.Amount -= take; SendAddItem(inv); VaultAdd(new InvItem(0, def.Id, take, inv.Dura)); }
        }
        if (fee > 0) { _char.Coins -= (uint)fee; SendStats(); }
        SaveChar();
        NpcBubble(npc, $"I'll take your {def.Name}. {moved} of them.");
        if (fee > 0) NpcBubble(npc, $"The fee is {fee} coins.");
        return true;
    }

    // Spoken "give my <item|coin> [count]" — the withdraw mirror of the above.
    internal bool WithdrawItemFromBank(Mob npc, string name, int amount)
    {
        if (IsCoinWord(name))
        {
            long amt = Math.Min(amount > 0 ? amount : _char.BankMoney, _char.BankMoney);
            if (amt <= 0) { NpcBubble(npc, "You withdraw nothing."); return true; }
            _char.BankMoney -= (uint)amt;
            _char.Coins += (uint)amt;
            SendStats();
            MarkDirty();
            NpcBubble(npc, $"Here's your {amt} coins.");
            return true;
        }

        var def = Content.FindItem(name) ?? Content.FindItem(Singularize(name));
        if (def is null) return false;

        var stack = _char.BankItems.Where(i => i.ItemId == def.Id).ToList();
        if (stack.Count == 0) { NpcBubble(npc, $"You didn't give me any {def.Name}."); return true; }   // RTK withdrawNoConfirm

        int remaining = amount > 0 ? amount : stack.Sum(i => i.Amount);
        int moved = 0;
        foreach (var bi in stack)
        {
            if (remaining <= 0) break;
            int slot = FreeSlot();
            // No room to hand it back: the banker refuses out loud AND the rule goes to minitext, since the
            // two say different things — one is her declining, the other is why.
            if (slot < 0) { if (moved == 0) { NpcBubble(npc, "I can't return that to you."); SendMiniText("You can't have more."); } break; }
            int take = Math.Min(Math.Min(remaining, bi.Amount), CarryRoom(def));
            if (take <= 0) { if (moved == 0) CarryCapNotice(def); break; }
            moved += take;
            remaining -= take;
            if (take >= bi.Amount) { _char.BankItems.Remove(bi); bi.Slot = (byte)slot; _char.Inventory.Add(bi); SendAddItem(bi); }
            else { bi.Amount -= take; var give = new InvItem((byte)slot, def.Id, take, bi.Dura); _char.Inventory.Add(give); SendAddItem(give); }
        }
        SaveChar();
        // RTK: "Here's your <item>." — count in parens when > 1.
        if (moved == 1) NpcBubble(npc, $"Here's your {def.Name}.");
        else if (moved > 1) NpcBubble(npc, $"Here's your {def.Name} ({moved}).");
        return true;
    }

    /// <summary>Put an entry in the vault, MERGING into an existing stack of the same item and condition
    /// instead of adding a second row. Merging is what makes the withdraw grid workable: it identifies the
    /// picked row by name, so two rows of one item would be indistinguishable. Gear never merges (it isn't
    /// stackable and each piece carries its own durability), so the grid still has to disambiguate labels.</summary>
    private void VaultAdd(InvItem it)
    {
        // Capped at ItemDef.StackCap like a bag slot, so the vault can't build a pile the player could never
        // have carried — and so a withdrawal always fits in one slot. Merging is deliberately bounded: an
        // unbounded merge is how a 271-Acorn stack (cap 201) became withdrawable.
        if (Content.ItemById(it.ItemId) is { Stackable: true } def)
        {
            int cap = def.StackCap, left = it.Amount;
            foreach (var b in _char.BankItems
                         .Where(b => b.ItemId == it.ItemId && b.Dura == it.Dura && b.Amount < cap).ToList())
            {
                if (left <= 0) break;
                int put = Math.Min(cap - b.Amount, left);
                b.Amount += put;
                left -= put;
            }
            while (left > 0)
            {
                int put = Math.Min(cap, left);
                _char.BankItems.Add(new InvItem(0, it.ItemId, put, it.Dura));
                left -= put;
            }
            return;
        }
        it.Slot = 0;                            // vault slots are meaningless
        _char.BankItems.Add(it);
    }

    internal async Task BankDepositItem(Mob npc)
    {
      while (true)
      {
        var items = _char.Inventory.OrderBy(i => i.Slot)
            .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
            .Where(t => t.def is not null)
            .ToList();
        // The native grid (0x2f sub-kind 5): the client draws each row's own icon and name straight out of
        // the bag, so only the slots go on the wire. An empty pack shows an empty list, not a bail-out.
        SendSellGrid(npc, "Which item will you store?", items.Select(t => WireSlot(t.inv)).ToList());
        var pick = await AwaitShopReply();
        var hit = items.FirstOrDefault(t => WireSlot(t.inv) == pick.Slot);
        if (hit.def is null) return;            // closed the window
        var (inv, def) = hit;

        // RTK refuses used/damaged goods for storage (rtklua depositNoConfirm): gear below full durability, or
        // a charged consumable with spent charges. Dura holds current durability for gear and remaining charges
        // for charged items; Dura == 0 is an unseeded legacy stack, treated as full. Checked before the
        // quantity question — there's no point asking how many of something she won't take.
        if ((def!.IsEquip || def.IsCharged) && inv.Dura != 0 && inv.Dura < def.Durability)
        { NpcBubble(npc, "I don't want your junk. Ask a smith to fix it."); continue; }

        // ItmDepositable, the registry's bank ban (same depositNoConfirm, refusal line verbatim).
        if (def.NoDeposit) { NpcBubble(npc, "You cannot deposit that item."); continue; }

        // A stack asks how much of it to store — dropping the whole pile in was never a choice the player got
        // to make. A single item skips the question (there's only one answer).
        int take = inv.Amount;
        if (inv.Amount > 1)
        {
            var n = await AskAmount(npc, "How many do you want me to hold for you?", def.Name, inv.Amount);
            if (n is null) return;                                     // cancelled
            if (n.Value <= 0) { NpcBubble(npc, "You store nothing."); continue; }
            // Refused, not clamped. There is no UI element that can report a correction, so quietly storing
            // a different number than the one typed would be indistinguishable from it having worked.
            if (n.Value > inv.Amount) { NpcBubble(npc, "That many?"); continue; }
            take = n.Value;
        }

        // RTK's safe-keeping fee: 10% of the item's sell value per unit, in hand up front or no deal.
        long fee = (long)Math.Ceiling(def.SellPrice * 0.10 * take);
        if (fee > _char.Coins) { NpcBubble(npc, $"Excuse me you didn't give me enough. It's {fee} coins."); continue; }

        if (take >= inv.Amount)
        {
            _char.Inventory.Remove(inv);
            // Reason 9 = "You gave <item>." — the whole entry is leaving the pack. A partial deposit takes
            // the else branch, which sends no delitem and so says nothing (the count just drops).
            SendDelItem((byte)inv.Slot, 9);
            VaultAdd(inv);                      // whole stack goes to the vault
        }
        else
        {
            inv.Amount -= take;                 // part of it: shrink the bag stack, redraw it, vault the rest
            SendAddItem(inv);
            VaultAdd(new InvItem(0, def.Id, take, inv.Dura));
        }
        if (fee > 0) { _char.Coins -= (uint)fee; SendStats(); }
        MarkDirty();
        NpcBubble(npc, $"I'll take your {def.Name}. {take} of them.");    // same lines the spoken deposit gives
        if (fee > 0) NpcBubble(npc, $"The fee is {fee} coins.");
      }
    }

    internal async Task BankWithdrawItem(Mob npc)
    {
      while (true)
      {
        var stored = _char.BankItems
            .Select(bi => (bi, def: Content.ItemById(bi.ItemId)))
            .Where(t => t.def is not null)
            .ToList();
        // The buy grid (0x2f sub-kind 4) with the stored COUNT in the price column — the same argument RTK's
        // own bank Lua fills with bankCountTable. No empty-vault special case: an empty vault just opens the
        // same list with nothing in it, which is its own answer.
        //
        // The reply names the row rather than indexing it, so every label must be unique. Stacks merge on
        // deposit (VaultAdd), but gear doesn't stack, so two identical swords still need telling apart.
        var byLabel = new Dictionary<string, (InvItem bi, ItemDef def)>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<GridRow>();
        foreach (var (b, d) in stored)
        {
            string label = d!.Name;
            for (int n = 2; byLabel.ContainsKey(label); n++) label = $"{d.Name} ({n})";
            byLabel[label] = (b, d);
            rows.Add(ShopRow(d, b.Amount) with { Name = label });
        }
        SendBuyGrid(npc, "Here's what I've been holding of yours. What do you want back?", rows);
        var pick = await AwaitShopReply();
        if (!byLabel.TryGetValue(pick.Name, out var got)) return;      // closed the window
        var (bi, def) = got;

        // Mirror of the deposit side: take back as much of the stack as you ask for, not all of it.
        int take = bi.Amount;
        if (bi.Amount > 1)
        {
            var n = await AskAmount(npc, "How many do you want back?", def.Name, bi.Amount);
            if (n is null) return;                                     // cancelled
            if (n.Value <= 0) { NpcBubble(npc, "You withdraw nothing."); continue; }
            // Refused, never clamped — see the deposit side. Asking for more than is stored gets a flat no.
            if (n.Value > bi.Amount) { NpcBubble(npc, "That many?"); continue; }
            take = n.Value;
        }

        // This path adds to the bag directly rather than through GiveItem, so it has to honour the
        // inventory-wide carry cap itself — otherwise the vault is a way to hold two stacks of a one-stack
        // item. Refused rather than trimmed, like every other quantity here.
        if (take > CarryRoom(def)) { CarryCapNotice(def); continue; }

        int slot = FreeSlot();
        if (slot < 0) { NpcBubble(npc, "I can't return that to you."); SendMiniText("You can't have more."); continue; }
        if (take >= bi.Amount)
        {
            _char.BankItems.Remove(bi);
            bi.Slot = (byte)slot;               // assign a fresh bag slot (vault slots are meaningless)
            _char.Inventory.Add(bi);
            SendAddItem(bi);
        }
        else
        {
            bi.Amount -= take;                  // part of it: the rest stays in the vault
            var give = new InvItem((byte)slot, def!.Id, take, bi.Dura);
            _char.Inventory.Add(give);
            SendAddItem(give);
        }
        MarkDirty();
        // Same lines the spoken withdraw gives ("Here's your Acorn." / "Here's your Acorn (13).").
        NpcBubble(npc, take > 1 ? $"Here's your {def!.Name} ({take})." : $"Here's your {def!.Name}.");
      }
    }

    // Digits-only amount parse (mirrors RTK inputNumberCheck), capped so it can't overflow the coin math.
    private static long ParseAmount(string? s)
    {
        long v = 0;
        if (s is not null)
            foreach (char ch in s)
                if (char.IsDigit(ch)) { v = v * 10 + (ch - '0'); if (v > Content.BankMax) return Content.BankMax; }
        return v;
    }

    // Portrait = the NPC's creature sprite drawn from Monster.epf — the SAME 0x8000|look encoding the on-map
    // spawn uses (RTK clif.c:3190 sends the NPC graphic as look+32768). The dialog's kind-1 "npc gfx" range
    // is exactly [32768, 49151], so an encoded creature look lands there; a look of 0 -> no portrait.
    private static ushort NpcPortrait(Mob npc) => npc.Sprite == 0 ? (ushort)0 : (ushort)(0x8000 | npc.Sprite);

    // 0x30 clif_scriptmenuseq (type-0, graphic head): a text prompt + picker buttons. Same frame mapping as
    // SendScriptMessage (RTK WFIFO(fd,N) -> body[N-5]); the menu differs only in the kind bytes
    // (body[0..1] = 02 02, RTK WFIFOB(5)=WFIFOB(6)=2) and the item list appended after the prompt:
    //   body[23+L] = item count (u8), then each item = len(u8) + ASCII text, contiguous.
    private void SendNpcMenu(Mob npc, string prompt, IReadOnlyList<string> options)
        => SendNpcMenuP(npc, DialogPortrait.Npc(npc), prompt, options);

    // Menu with an EXPLICIT portrait (so a script can put the player's own paperdoll in the head — see
    // WriteHead). Offsets below are for the default 4-byte sprite head; a paperdoll head pushes everything
    // after it out by 4, which the client accounts for from the parser's return value.
    private void SendNpcMenuP(Mob npc, DialogPortrait p, string prompt, IReadOnlyList<string> options)
    {
        var pr = Encoding.ASCII.GetBytes(prompt);

        var d = new List<byte>();
        d.Add(0x02); d.Add(0x02);          // [0..1] kind = menu (RTK WFIFOB(5)=2, WFIFOB(6)=2)
        d.AddRange(Be32(npc.Id));          // [2..5] npc entity id
        WriteHead(d, p);                   // [6..14] head kind + portrait descriptor + trailing descriptor
        d.AddRange(Be32(1));               // [15..18]
        d.Add(0);                          // [19] prev button
        d.Add(0);                          // [20] next button
        d.AddRange(Be((ushort)pr.Length)); // [21..22] prompt length
        d.AddRange(pr);                    // [23..] prompt text
        d.Add((byte)options.Count);        // [23+L] menu item count
        foreach (var label in options)
        {
            var ob = Encoding.ASCII.GetBytes(label);
            d.Add((byte)ob.Length);
            d.AddRange(ob);
        }
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-menu(0x30) id={npc.Id} x{options.Count}");
    }

    // 0x30 clif_inputseq (type-0, graphic head): a free-text entry box. Same head as the menu; kind bytes
    // are 04 04 (RTK WFIFOB(5)=WFIFOB(6)=4). After the prompt come RTK's secondary lines we don't use:
    //   [+1] dialog2 len(=0)   [+1] '*' separator(42)   [+1] dialog3 len(=0)   [+2] trailing (0,0).
    // The client returns the text via 0x3A kind 4 (HandleNpcDialog -> DlgInput).
    private void SendInputBox(Mob npc, string prompt) => SendInputBox(npc.Id, DialogPortrait.Npc(npc), prompt);

    private void SendInputBox(uint entityId, DialogPortrait portrait, string prompt)
    {
        var pr = Encoding.ASCII.GetBytes(prompt);

        var d = new List<byte>();
        d.Add(0x04); d.Add(0x04);          // [0..1] kind = input (RTK WFIFOB(5)=WFIFOB(6)=4)
        d.AddRange(Be32(entityId));        // [2..5] npc entity id
        WriteHead(d, portrait);            // [6..14] head kind + portrait descriptor + trailing descriptor
        d.AddRange(Be32(1));               // [15..18]
        d.Add(0);                          // [19] prev button
        d.Add(0);                          // [20] next button
        d.AddRange(Be((ushort)pr.Length)); // [21..22] prompt length
        d.AddRange(pr);                    // [23..] prompt text
        d.Add(0);                          // dialog2 length (unused)
        d.Add(42);                         // '*' separator
        d.Add(0);                          // dialog3 length (unused)
        d.Add(0); d.Add(0);                // trailing pad (RTK advances len by +3 past dialog3)
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-input(0x30) id={entityId}");
    }

    // 0x30 clif_scriptmes (type-0, graphic head): a plain NPC text box. Ported from RTK clif.c; the RTK
    // WFIFO(fd,N) offsets map to this server's body[N-5] (frame = AA len len opcode inc, body at wire+5).
    //   body[0..1] u16=1   [2..5] npc id(u32BE)   [6] head kind(0 none/1 npc gfx/2 item gfx)   [7]=1
    //   [8..9] gfx(u16BE)   [10] color   [11]=1   [12..13] gfx   [14] color   [15..18] u32=1
    //   [19] prev-button    [20] next-button   [21..22] msg len(u16BE)   [23..] msg (ASCII)
    // prev/next 0 => a single OK/close box; the client answers a close with 0x3A kind 1 (HandleNpcDialog).
    private void SendScriptMessage(uint npcId, string msg, ushort gfx, byte color,
                                   bool prev = false, bool next = false)
    {
        // Head kind, classified from the graphic id exactly as RTK does (clif_scriptmes): 0 -> none,
        // >=49152 -> item gfx (kind 2), else -> npc/creature gfx (kind 1).
        byte head = gfx == 0 ? (byte)0 : gfx >= 49152 ? (byte)2 : (byte)1;
        SendScriptMessageP(npcId, msg, new DialogPortrait(head, gfx, color), prev, next);
    }

    // A dialog portrait: the head-kind byte (0 none / 1 creature-look / 2 item-icon) plus the graphic id and
    // palette carried in the 0x30 head. The client reads head kind from the byte directly, so — unlike the
    // range-derived helper above — this lets a script pick an item-icon portrait (kind 2) whose small Item.epf
    // frame would otherwise be misread as a creature look. RTK: convertGraphic(look,"monster") = 0x8000|look.
    //
    // Doll (non-null) selects the PLAYER-PAPERDOLL head instead of a sprite: the same 7 appearance bytes the
    // 0x33 player look carries. See WriteHead for the client-side proof that 4.95 supports this.
    private readonly record struct DialogPortrait(byte Head, ushort Gfx, byte Color, byte[]? Doll = null)
    {
        public static readonly DialogPortrait None = new(0, 0, 0);
        public static DialogPortrait Npc(Mob npc)  => npc.Sprite == 0 ? None : new(1, (ushort)(0x8000 | npc.Sprite), npc.Color);
        public static DialogPortrait Look(int look, int color) => look <= 0 ? None : new(1, (ushort)(0x8000 | look), (byte)color);
        public static DialogPortrait Item(ushort icon, byte color) => new(2, icon, color);
        /// <summary>A live paperdoll of the player themselves (7-byte 0x33 appearance). The NPC's own graphic
        /// still rides along in the trailing descriptor, exactly as RTK's dialogtype-2 does.</summary>
        public static DialogPortrait Player(byte[] appearance, Mob npc) =>
            new(1, npc.Sprite == 0 ? (ushort)0 : (ushort)(0x8000 | npc.Sprite), npc.Color, appearance);
    }

    // The 0x30 head, shared by all three dialog forms (text box / menu / input) — they parse it identically.
    //
    // RE'd out of the 4.95 client (2026-08-06) rather than guessed: 0x44f530 -> 0x46c050 switches on body[0]
    // (9 dialog kinds) and every branch's ctor does the same three things — `lea edx,[edi+6]` (i.e. &body[7]),
    // `call 0x4360e0`, then `lea esi,[eax+0xa]` to find the next field. 0x4360e0 is a DISCRIMINATED UNION on
    // that tag byte at body[7]:
    //     tag 0 -> 0x436120, returns 7  == the 7-byte PLAYER APPEARANCE, byte-for-byte the same parser the
    //                                     0x33 player look uses (§8), special-case on byte[3] and all
    //     tag 1 -> 0x436200, returns 3  == u16 sprite id + palette byte (what every dialog sent before this)
    //     tag 2 -> 0x436240              == item icon
    //     else  -> returns 0, no head
    // 0x4360e0 adds 1 for the tag itself, so it returns 4 or 8, and EVERY field after the head shifts by that
    // much. body[6] (head kind) is only inspected for the value 2, which force-overwrites the tag with 2; the
    // tag byte is what actually selects the form. A fixed 4-byte trailing descriptor follows the head and is
    // skipped by the parser (`eax + 0xa` == head size + the 4 trailing bytes + the 6 bytes before the head).
    private void WriteHead(List<byte> d, DialogPortrait p)
    {
        d.Add(p.Head);                        // [6] head kind (0 none / 1 sprite-or-doll / 2 item icon)
        if (p.Doll is not null)
        {
            d.Add(0);                         // [7] tag 0 -> player paperdoll
            // Same union, same divergence as 0x33/0x1d: on 5.33 tag 0 resolves to appearance parser
            // 0x449880, which reads ELEVEN bytes in a different order (see AppearanceRecord). Sending
            // the 4.95 seven here would shift the paperdoll's face/armor/dye/weapon/shield exactly the
            // way the on-map sprite was shifted — and it would also mis-size the head, so every field
            // after it in the dialog (text, options) would land four bytes early.
            WriteAppearance(d, p.Doll);       // 4.95: the 7 bytes as-is. 5.33: the 11-byte form.
        }
        else
        {
            d.Add(1);                         // [7] tag 1 -> plain sprite
            d.AddRange(Be(p.Gfx));            // [8..9]
            d.Add(p.Color);                   // [10]
        }
        d.Add(1); d.AddRange(Be(p.Gfx)); d.Add(p.Color);   // trailing descriptor (parsed past, never read here)
    }

    // Core 0x30 text-box sender with an EXPLICIT portrait (head kind not re-derived). Same frame as
    // SendScriptMessage; only the head bytes carry the caller's portrait.
    private void SendScriptMessageP(uint npcId, string msg, DialogPortrait p, bool prev, bool next)
    {
        var m = Encoding.ASCII.GetBytes(msg);
        var d = new List<byte>();
        d.AddRange(Be(1));                 // [0..1] type/count = 1
        d.AddRange(Be32(npcId));           // [2..5] npc entity id
        WriteHead(d, p);                   // [6..14] head kind + portrait descriptor + trailing descriptor
        d.AddRange(Be32(1));               // [15..18]
        d.Add((byte)(prev ? 1 : 0));       // [19] prev button
        d.Add((byte)(next ? 1 : 0));       // [20] next button
        d.AddRange(Be((ushort)m.Length));  // [21..22] message length
        d.AddRange(m);                     // [23..] message text
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-dialog(0x30) id={npcId} {m.Length}B head={p.Head}");
    }

    // ---- multi-page dialog (RTK dialogSeq): one portrait, N text pages the player clicks through. Non-final
    // pages show the "next" affordance; the last is a plain close. Each page awaits the client's 0x3A so the
    // whole sequence reads as linear script. The three public wrappers pick the portrait (NPC / creature / item).
    private async Task DlgSeq(Mob npc, DialogPortrait p, IReadOnlyList<string> pages)
    {
        if (pages.Count == 0) return;
        // Every page carries the "next" affordance (next:true) — the click the client answers with a 0x3A that
        // resumes the await and drives the next page. A button-less box (prev/next both off) can't be advanced:
        // dismissing it sends no reply, so the sequence hangs on page one. RTK drives multi-page dialog the same
        // way (moreFlag -> the next arrow). The last page's "next" click simply ends the sequence.
        foreach (var page in pages)
        {
            SendScriptMessageP(npc.Id, page, p, prev: false, next: true);
            await AwaitReply();
        }
    }
    internal Task DlgSayNpc(Mob npc, IReadOnlyList<string> pages)  => DlgSeq(npc, DialogPortrait.Npc(npc), pages);
    internal Task DlgSayLook(Mob npc, int look, int color, IReadOnlyList<string> pages) => DlgSeq(npc, DialogPortrait.Look(look, color), pages);
    internal Task DlgSayItem(Mob npc, string itemKey, IReadOnlyList<string> pages)
    {
        var def = Content.ItemByKey(itemKey);
        // IconOf folds the colour into the frame on 4.95 (no colour channel) and keeps it separate on 5.x.
        return DlgSeq(npc, def is null ? DialogPortrait.Npc(npc)
                                       : DialogPortrait.Item(IconOf(def), _ver == ClientVersion.V533 ? def.IconColor : (byte)0), pages);
    }

    // 0x3A = the client's reply to a 0x30 we sent (RTK clif_parsenpcdialog). body[0]=kind (01 text/close,
    // 02 menu pick, 04 input), [8]=step, [10]=menu index (1-based) or input length, [11..]=input text. We
    // just complete the prompt that's awaiting a reply; the suspended behaviour resumes and drives what's
    // next (nested menu, purchase, loop back). No routing table here — the await IS the continuation.
    private void HandleNpcDialog(byte[] dec)
    {
        byte kind = dec.Length > 0 ? dec[0] : (byte)0;
        int step = dec.Length > 8 ? dec[8] : 0;
        int menuOrLen = dec.Length > 10 ? dec[10] : 0;
        string input = "";
        if (kind == 0x04 && dec.Length > 11)   // input box returned text
        {
            int n = Math.Min(menuOrLen, dec.Length - 11);
            if (n > 0) input = Encoding.ASCII.GetString(dec, 11, n);
        }
        Log.Info($"   -> NPC-DIALOG (0x3A) kind={kind} step={step} menu/len={menuOrLen}" +
                 (input.Length > 0 ? $" input='{input}'" : ""));

        var tcs = _dlgReply;
        _dlgReply = null;
        tcs?.TrySetResult(new DialogReply(kind, step, menuOrLen, input));
    }

    // The client sends 0x4F when the player saves their profile from the edit box. Body (matches the
    // client's own change-profile parse): [picSize u16BE][picSize bytes][blurbLen u8][blurb bytes][00].
    // We persist both so a later click (0x34) shows the player's own words + drawing.
    private void HandleChangeProfile(byte[] dec)
    {
        if (dec.Length < 3) return;
        int picLen = (dec[0] << 8) | dec[1];
        int off = 2;
        if (picLen > 0 && off + picLen <= dec.Length)
        {
            _char.ProfilePic = dec[off..(off + picLen)];
            off += picLen;
        }
        else
        {
            _char.ProfilePic = null;
        }

        if (off < dec.Length)
        {
            int tlen = dec[off++];
            if (tlen >= 0 && off + tlen <= dec.Length)
                _char.ProfileText = Encoding.ASCII.GetString(dec, off, tlen);
        }

        if (_enteredWorld) StoreSave();
        Log.Info($"   -> CHANGE-PROFILE (0x4F) saved: pic={_char.ProfilePic?.Length ?? 0}B text=\"{_char.ProfileText}\"");
        SendMessage("Your profile has been saved.");
    }

    // ---- 0x49 — "resend your profile picture" (server -> client) ---------------------------------------
    // Empty body; the dispatch trampoline doesn't even pass a packet pointer, so there is nothing to fill in.
    // The client (0x44edc0) answers on 0x4F with EXACTLY the packet the profile editor sends, which
    // HandleChangeProfile above already parses — so this is a safe probe: every failure path still replies,
    // just with picSize = 0.
    //
    // What it reads, and therefore what a player has to put on disk for a picture to exist at all
    // (validation reversed from 0x44ef0c onward, all four checks are hard `jne` bails):
    //   file      <client cwd>/users/<CharacterName>.epf   — retried as .face
    //   size      EXACTLY 0xb1c = 2844 bytes
    //   dword@8   == 0xaf0 = 2800   (the EPF's TOC offset, i.e. 12-byte header + 2800 bytes of pixels)
    //   frame box  toc[1].top - toc[0].bottom == 0x38  and  toc[1].left - toc[0].right == 0x30
    //              -> the picture is a single 48 x 56 frame. See re/make_profile_epf.py.
    // The user's own capture shows the miss: CreateFileW(".../users/Zaleroo.epf") -> INVALID_HANDLE, and the
    // 0x4F that followed carried picSize 0. Nothing is wrong server-side when that happens — the file just
    // isn't there.
    internal void SendResendProfilePic()
    {
        SendMap(0x49, _gameInc++, Array.Empty<byte>(), "resend-profile-pic(0x49)");
        Log.Info("   -> 0x49 asked the client to re-upload users/<name>.epf; watch for the 0x4F reply " +
                 "(picSize 0 = the client couldn't read a valid 2844-byte file)");
    }

    // 0x39 self-profile ("Mind's Eye"). This FIRST block is the 7.x/6.x shape — kept only to show what 4.95
    // is NOT. Layout decoded from the 7.x clif_mystaytus builder and confirmed against a real 6.x capture
    // (jeedee/TkServer) that decrypts to this exact shape (AC=99, class "Peasant", legend "Born in Hyul 31,
    // Winter"). Body:
    //   [AC u8][dam u8][hit u8]
    //   [clan  : len u8 + bytes]        (len 0 = clanless)
    //   [clanTitle : len u8 + bytes]
    //   [title : len u8 + bytes]
    //   [spouse : len u8 + bytes]       <- 7.x ONLY. The 4.95 field in this position is the PARTY BOX.
    //   [group u8]  [TNL u32BE]
    //   [className : len u8 + bytes]
    //   14 × equip slot (each 10 bytes, all zero = empty)
    //   [exchange u8]
    //   [0 u8] [legendCount u16BE]
    //   legendCount × { icon u8, color u8, textLen u8, text bytes }
    //
    // WIRE FORMAT (reverse-engineered from the client parser at 0x4732a0 — the mode-0 widget picked by the
    // shared profile dispatcher 0x424820; the mode-1/other-view widget 0x48b6a0 is a DIFFERENT, larger layout):
    //   [AC u8][dam u8][hit u8]
    //   [clan str][clanTitle str][title str][PARTY BOX str]    (each: u8 len + bytes)
    //   [group u8][TNL u32BE][className str]
    //   [g0 u16BE][g1 u16BE][g2 u16BE]                         (three portrait/graphic ids — see below)
    //   [BUFF BOX str]                                         (multi-line; client maps TAB->CR)
    //   [flag u8]
    //   [legendCount u8]  then legendCount × { icon u8, color u8, len u8, text }
    //
    // THE PROFILE PANE HAS THREE PAGES, and this packet feeds all three. The parser ends by pushing text
    // into three separate controls on the widget:
    //   +0x104  <- the 6th string (TAB->CR'd)   PAGE 1 — buffs      (BuffBoxText)
    //   +0x108  <- the 4th string               PAGE 2 — the group  (PartyBoxText, above the stats)
    //   +0x10c  <- the legend array             PAGE 3 — legend marks
    // The 4th string was documented as "spouse" (from 7.x clif_mystaytus) until the page-2 box was found
    // to be empty for a grouped player — see PartyBoxText for the disassembly that settles it. 4.95 has no
    // spouse FIELD because it doesn't need one: marriage shows as a legend mark ("Married to <name>
    // (<date>)", key "married" — Session.RunMarriageCeremony), i.e. on page 3 with every other legend.
    // CRITICAL: 4.95 has NO packed equipment-icon array and the legend count is a single u8. The old code
    // sent a 6.x/RTK-shaped 14-cell/113-byte equip region (that fork has more item slots — hence the bigger
    // block); on 4.95 it pushed the legend count into the padding (count read as 0 -> no legends) and spilled
    // icons into the wrong fields (gear rendered in the wrong paperdoll slots). Proven by decoding a real 6.x
    // capture with this exact grammar: it aligns perfectly up to the legend count, then the 6.x equip block
    // remains unconsumed. The self paperdoll BODY is drawn from the live on-map character sprite, not this
    // packet, so g0/g1/g2 = 0 (default) exactly matches the known-good capture.
    private void SendSelfProfile()
    {
        if (_ver == ClientVersion.V533) { SendSelfProfile533(); return; }
        var eq = Totals();                    // fold worn-gear bonuses + active buffs into the displayed AC/dam/hit
        var d = new List<byte>();
        d.Add((byte)(sbyte)Math.Clamp(_char.Ac + eq.armor, -128, 127));   // AC: lower is better; gear/buff armor is a signed AC delta
        d.Add((byte)Math.Clamp(_char.Dam + eq.dam, 0, 255));
        d.Add((byte)Math.Clamp(_char.Hit + eq.hit, 0, 255));
        AddLenStr(d, _char.ClanName);
        AddLenStr(d, _char.ClanTitle);
        AddLenStr(d, _char.Title);
        // The PAGE-2 text box (the scrollable window above VITA/MANA/AC/DAM/HIT) — the party list. This
        // field was long mislabelled "spouse" by analogy with 7.x clif_mystaytus; the 4.95 parser proves
        // otherwise (see PartyBoxText). Nothing else on the wire carries the group roster.
        AddLenStr(d, PartyBoxText());
        d.Add((byte)(_char.Grouped ? 1 : 0));   // group/sociable flag (Shift+G)
        d.AddRange(Be32(_char.Tnl));    // experience to next level
        AddLenStr(d, ClassTitle);       // class + rank ("Inferno"), not the stored base name

        // The three equipment ICON cells beside the doll: helm, left ring, right ring. These slots have no
        // character-sprite layer in 4.95, so the profile shows them as ground-icon boxes fed by these u16s.
        d.AddRange(Be(ProfileCellIcon(4)));   // helm  (wire slot 4)
        d.AddRange(Be(ProfileCellIcon(7)));   // left ring  (wire slot 7)
        d.AddRange(Be(ProfileCellIcon(8)));   // right ring (wire slot 8)

        // The PAGE-1 text box: active buff/debuff names + remaining seconds, empty when nothing is active.
        // TAB-separated, NOT CR like the party box above: the client rewrites TAB->CR here and only here
        // (the loop at 0x47359b), and that one-field pre-pass is why TAB is the wire separator on both
        // clients — see BuffBoxSep, where sending CR instead costs 5.33 its live countdown. (The other-view
        // 0x34 puts the GEAR list in its own box instead — self=buffs, other=gear. That one is a DIFFERENT
        // parser, 0x48b6a0, with its own conversion, and also uses TAB.)
        AddLenStr(d, BuffBoxText());
        d.Add((byte)(_char.Exchange ? 1 : 0));   // trailing flag = exchange/trade status (client field +0x935)

        var legs = _char.Legends ?? new List<Legend>();
        d.Add((byte)Math.Min(legs.Count, 255));   // legend count is a single u8 in 4.95 (NOT u16)
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        SendMap(0x39, _gameInc++, d.ToArray(),
            $"self-profile(0x39) ac={_char.Ac} class='{_char.ClassName}' buffs={_buffs.Count} legends={legs.Count}");
    }

    /// <summary>The 5.33 self-profile. Same opcode, materially different record — parser <c>sub_49cdd0</c>.
    ///
    /// <para>The layout was recovered by firing an ALL-ZERO body (game-data/packets/probe39.txt) and watching
    /// which offsets the client read. With every string empty and every count zero nothing has variable
    /// width, so the read offsets are the minimal record exactly:</para>
    /// <code>
    ///   [0][1][2] u8 x3   [3] u8+str   u8+str   u8+str   u8+str   u8
    ///   u32BE             u8+str
    ///   (u16,u8) x5                                              <- FIVE (icon, colour) cells
    ///   u8+str            u8   u8
    /// </code>
    /// <para>31 bytes minimum against 4.95's 22. The ONE structural break is the equipment cells: 4.95 puts
    /// THREE bare u16 icons where 5.33 reads FIVE cells of (u16 icon + u8 colour) — 15 bytes against 6.
    /// Sending the 4.95 record shifted everything from there onward, which is why the gear boxes, buff box,
    /// profile text and legend list were all empty or garbage at once, and why the parser ran off the end of
    /// the body (observed reading at offset 146 of a 130-byte packet).</para>
    ///
    /// <para>THE STRING REGION IS 4.95's, UNCHANGED — clan, clan title, title, party box. The zero probe read
    /// it as "three strings plus a loose u8 at [4]" because the tracer only sees a string when the parser
    /// routes it through the stack copy helper <c>0x46cbc0</c>, and the SECOND string skips that helper: it
    /// goes straight from the body into <c>MultiByteToWideChar</c> at <c>0x49ceb1</c>, so only its length byte
    /// was recorded. Widths matched either way (an empty string and a zero byte are the same byte), so the
    /// packet parsed — the strings just landed one slot late: clan title in the title line, title in the party
    /// box, and the title line of the pane blank. Disassembling <c>sub_49cdd0</c> settles it; the four strings
    /// are copied to the widget's <c>+0x112 / +0x312 / +0x512</c> wide buffers and the <c>+0x932</c> list box,
    /// and the draw method <c>sub_49d590</c> paints the first three at y=48/65/82 in that order.</para>
    ///
    /// <para>Also settled by the same disassembly: the legend record kept its 4.95 <c>icon/colour/len/text</c>
    /// shape (loop at <c>0x49d32d</c>). Still open: which equipment slots the two extra cells are for.</para></summary>
    private void SendSelfProfile533()
    {
        var eq = Totals();
        var d = new List<byte>();
        d.Add((byte)(sbyte)Math.Clamp(_char.Ac + eq.armor, -128, 127));   // AC
        d.Add((byte)Math.Clamp(_char.Dam + eq.dam, 0, 255));              // Dam
        d.Add((byte)Math.Clamp(_char.Hit + eq.hit, 0, 255));              // Hit
        // Four strings, 4.95's order. The first three are the stacked lines at the top of the pane —
        // widget +0x112 / +0x312 / +0x512, painted at y=48/65/82 by sub_49d590 — and the fourth is the
        // page-2 roster list box (+0x932). See the summary above for why the zero probe missed one.
        AddLenStr(d, _char.ClanName);                                     // line 1
        AddLenStr(d, _char.ClanTitle);                                    // line 2
        AddLenStr(d, _char.Title);                                        // line 3
        AddLenStr(d, PartyBoxText());                                     // party roster box
        d.Add((byte)(_char.Grouped ? 1 : 0));                             // group/sociable flag
        d.AddRange(Be32(_char.Tnl));                                      // experience to next level
        AddLenStr(d, ClassTitle);                                         // class + rank

        // FIVE (icon u16, colour u8) cells. 4.95 folds the colour into the frame id; 5.x carries it as its
        // own byte, which is what IconOf/IconColor already split for 0x0F and 0x37 — same rule here.
        WriteProfileCell533(d, 4);   // helm
        WriteProfileCell533(d, 7);   // left ring
        WriteProfileCell533(d, 8);   // right ring
        WriteProfileCell533(d, 0);   // 4th cell — slot unidentified, sends an empty box
        WriteProfileCell533(d, 0);   // 5th cell — ditto

        // Buff/debuff box — TAB-separated (BuffBoxSep). 5.33 does not just print this one: it rewrites
        // TAB->LF, splits it per line, and runs each line as a live one-second countdown. CR here collapses
        // the whole box into a single timer entry, which is what "only the last buff ticks" looked like.
        AddLenStr(d, BuffBoxText());
        d.Add((byte)(_char.Exchange ? 1 : 0));                            // exchange/trade status

        // Legend record is 4.95's, confirmed against the parser loop at 0x49d32d: count u8, then each
        // { icon u8, colour u8, len u8, text }.
        var legs = _char.Legends ?? new List<Legend>();
        d.Add((byte)Math.Min(legs.Count, 255));                           // legend count
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        SendMap(0x39, _gameInc++, d.ToArray(),
            $"self-profile533(0x39) {d.Count}B ac={_char.Ac} class='{_char.ClassName}' " +
            $"buffs={_buffs.Count} legends={legs.Count}");
    }

    /// <summary>One 5.33 profile equipment cell: icon u16BE + colour u8. Slot 0 means "no such slot here",
    /// which writes an empty box rather than whatever happens to be in equipment slot 0.</summary>
    private void WriteProfileCell533(List<byte> d, byte wireSlot) => WriteProfileCell533(d, this, wireSlot);

    /// <summary>As above, for a cell describing SOMEONE ELSE (the 0x34 view panel). The icon id-space is
    /// chosen by the VIEWER's client version — <see cref="IconOf"/> reads this session's <c>_ver</c>, not
    /// the target's — because the bytes have to make sense to the client that will draw them.</summary>
    private void WriteProfileCell533(List<byte> d, Session target, byte wireSlot)
    {
        if (wireSlot == 0) { d.AddRange(Be(0)); d.Add(0); return; }
        var worn = target._char.Equipment.FirstOrDefault(e => e.Slot == wireSlot);
        var def = worn is null ? null : Content.ItemById(worn.ItemId);
        d.AddRange(Be(def is null ? (ushort)0 : IconWire(IconOf(def))));
        d.Add(def?.IconColor ?? 0);
    }

    /// <summary>The PAGE-2 box of the self-profile: your group roster, one name per line, sorted
    /// alphabetically, the leader marked <c>*Name</c>. Empty (a blank box) when you aren't in a group.
    ///
    /// SEPARATOR IS CR, NOT TAB. The page-1 buff box gets a TAB->CR pass in the client (0x47359b) before it
    /// is handed to the text control; this field does NOT — it goes straight from MultiByteToWideChar into
    /// the control. The control's own copy loop (0x480b20) passes 0x0d/0x0a through as line breaks, so CR
    /// separates lines here. A tab would render as one run-together line.
    ///
    /// WHY THIS FIELD. The 4th string of 0x39 was documented as "spouse", by analogy with 7.x
    /// clif_mystaytus. It isn't: the 4.95 parser at 0x4732a0 copies it to the widget's +0x938 buffer and
    /// then ADDs it to the text control at +0x108 — the same store-then-add shape it uses for the buff box
    /// (+0xb38 -> control +0x104) and the legend list (control +0x10c). Those three controls are the
    /// profile's three pages. A spouse name would be a label, not a scrolling list; this is the roster box,
    /// and it was rendering blank because an unmarried character sends an empty spouse. 4.95 never wanted a
    /// spouse field: marriage is a LEGEND MARK ("Married to &lt;name&gt; (&lt;date&gt;)", key "married" —
    /// <see cref="RunMarriageCeremony"/>), so it already displays, on page 3.</summary>
    private string PartyBoxText()
    {
        if (_party is null) return "";
        var leader = _party.Leader;
        return string.Join('\r', _party.Members
            .Select(m => (Name: m.Snapshot().Name, IsLeader: ReferenceEquals(m, leader)))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.IsLeader ? $"*{x.Name}" : x.Name));
    }

    // The self-view buff/effect box (issue #6), PAGE 1: one line per active buff/debuff with the remaining
    // time in seconds. Grouped by spell so a multi-stat buff shows once, and TAB-separated — see
    // BuffBoxSep, which is the one thing in this box that is NOT free-form text.
    //
    // On 4.95 the box is dead text and reopening the profile is what re-reads the durations. On 5.33 the
    // client TICKS it: every line becomes an entry in a live countdown list, so the seconds we send here
    // are a starting value, not a snapshot.
    private string BuffBoxText()
    {
        long now = Environment.TickCount64;
        // Skip lapsed buffs (Session.ExpireBuffs owns removal + the fade line); don't remove them here.
        var lines = _buffs
            .Where(b => b.Expires > now)
            // Chung Ryong's AC buff is not its own effect — it is one tier of the fury, riding _buffs only
            // because that is where a stat delta has to live. The fury already prints its own line below on
            // the same deadline, so letting this one through renders the spell twice from Rage 3 up.
            .Where(b => b.Key != CrRageAcKey)
            .GroupBy(b => b.Key)
            .Select(g =>
            {
                int secs = (int)Math.Max(0, (g.Max(x => x.Expires) - now + 999) / 1000);
                var name = string.IsNullOrEmpty(g.First().Name) ? g.Key : g.First().Name;
                return $"{name} {secs}s";
            })
            .ToList();

        // The dedicated combat-stance slots (deduction / rage / backstab / flank) live OUTSIDE _buffs — they're
        // scalar timers, not stat deltas — so surface them here too, or a spell like Sanctuary or Baekho's
        // Cunning would show no duration at all. Each is "Label (Ns)".
        static int Secs(long until, long now2) => (int)((until - now2 + 999) / 1000);
        // Deduction has two independent sources (Sanctuary line + Baekho's Cunning). Sanctuary overrides
        // Cunning while both run, but both timers are real (Cunning re-asserts when Sanctuary lapses), so
        // surface both so the player can see the ladder. Name + duration ONLY — the box shows a spell's NAME,
        // not its magnitude ("Sanctuary 48s", never "Sanctuary -50% 48s"); every other line here follows
        // that shape and this one was the odd one out.
        if (SancDeductActive)     lines.Add($"{(SancDeductName.Length > 0 ? SancDeductName : "Protection")} {Secs(SancDeductUntil, now)}s");
        if (CunningDeductActive)  lines.Add($"Cunning {(SancDeductActive ? "suppressed " : "")}{Secs(CunningDeductUntil, now)}s");
        if (now < _rageUntil && _rageAmount > 1)  lines.Add($"{(_rageName.Length > 0 ? _rageName : "Fury")} {Secs(_rageUntil, now)}s");
        // Four-way (Cunning 4+) reaches the same tiles as Backstab+Flank and more, so it stands in for both
        // rather than printing three lines that describe one swing.
        if (now < _fourWayUntil)                  lines.Add($"Four-way {Secs(_fourWayUntil, now)}s");
        else
        {
            if (now < _backstabUntil)             lines.Add($"Backstab {Secs(_backstabUntil, now)}s");
            if (now < _flankUntil)                lines.Add($"Flank {Secs(_flankUntil, now)}s");
        }
        // Stealth (Invisible/Spirit's Form/Life's Cloak/Glass Form) is a scalar timer outside _buffs too, so it
        // never showed a duration before — surface it here (works whether or not you're also morphed).
        if (Stealthed)                            lines.Add($"{_stealthName} {Secs(_stealthUntil, now)}s");

        return BuffBoxJoin(lines);
    }

    /// <summary>The buff box's line separator, and the reason it is TAB and not CR.
    ///
    /// <para>Both profile parsers run a pre-pass over THIS FIELD AND NOTHING ELSE that rewrites TAB into
    /// the break character the rest of that client expects — 4.95 turns it into CR (<c>0x47359b</c>),
    /// 5.33 into LF (<c>0x49d131</c>). A pre-pass that exists for one field is the original Nexon server
    /// telling us what it sent: TAB. Sending the post-pass character instead works on 4.95, where the box
    /// is inert text, and BREAKS 5.33, where it isn't.</para>
    ///
    /// <para><b>5.33 parses this box into a live countdown list.</b> After the pre-pass, <c>0x49f6a0</c>
    /// reads it with a getline (<c>0x453b80</c>) that splits on <b>LF only</b> — CR is not a break there —
    /// and turns each line into an 8-byte entry: name = everything before the last space or tab
    /// (<c>find_last_of(L" \t")</c>), seconds = <c>atoi</c> of the tail. A vtable timer (<c>0x49f4e0</c>)
    /// then decrements every entry once a second, drops the ones that reach zero, and re-renders the whole
    /// list through <c>Str.res[220]</c> = <c>"%s %3ds"</c> — which is exactly the shape we already build,
    /// so the box reads the same whether the client or the server formatted the line.</para>
    ///
    /// <para>With CR, the getline saw ONE line: the whole box became a single entry whose "name" was
    /// <c>"Might 300s\rProtection 60s\rFury"</c> and whose timer was the LAST number in the box. That is
    /// the bug this constant exists to prevent — every buff rendered, but only the bottom one counted
    /// down, because every other line's seconds were frozen inside that name string.</para>
    ///
    /// <para>PartyBoxText stays on CR: page 2 gets no pre-pass on either client, so TAB would be dropped
    /// outright there (4.95's copy loop <c>0x480b20</c> allows only <c>0x0d</c>/<c>0x0a</c> below
    /// <c>0x20</c>) and the roster would run together on one line.</para></summary>
    public const char BuffBoxSep = '\t';

    /// <summary>Join buff-box lines for the wire. Split out from <see cref="BuffBoxText"/> so the wire
    /// shape 5.33 depends on is testable without a live session — see <c>Tests/ClientVersionWireTests</c>,
    /// which re-runs 5.33's own grammar over the result.</summary>
    public static string BuffBoxJoin(IEnumerable<string> lines) => string.Join(BuffBoxSep, lines);

    // length-prefixed ASCII string: [len u8][bytes]. Empty string -> a single 0 byte.
    private static void AddLenStr(List<byte> d, string? s)
    {
        var b = Encoding.ASCII.GetBytes(s ?? "");
        d.Add((byte)b.Length);
        d.AddRange(b);
    }

    // "@leg" — replay the EXACT 0x39 self-profile captured from a real 6.x server (jeedee/TkServer),
    // decrypted with the shared NexonInc cipher. Known-good content: AC 99, class "Peasant", legend
    // "Born in Hyul 31, Winter". If the 4.95 profile window opens and shows these, the format is shared
    // and our native SendSelfProfile is correct; if it garbles, we diff against this capture.
    private static readonly byte[] Profile6x =
    {
        0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x2b, 0x07,
        0x50, 0x65, 0x61, 0x73, 0x61, 0x6e, 0x74,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x80, 0x17,
        0x42, 0x6f, 0x72, 0x6e, 0x20, 0x69, 0x6e, 0x20, 0x48, 0x79, 0x75, 0x6c, 0x20, 0x33, 0x31, 0x2c,
        0x20, 0x57, 0x69, 0x6e, 0x74, 0x65, 0x72,
    };

    private void SendProfileReplay6x()
    {
        SendMap(0x39, _gameInc++, Profile6x, "replay6x-profile(0x39)");
        Log.Info("   -> REPLAY 6.x self-profile on 0x39 (expect: AC 99, class Peasant, legend 'Born in Hyul 31, Winter')");
    }

    // 0x34 = the "click" profile: the public view shown when you click a character. Distinct from the
    // profile-key window (0x39, stats/legend), it carries the character PORTRAIT, the writable profile
    // TEXT + PICTURE, nation, and legend. Layout REVERSED from the 4.95 client's own parser (0x48b6a0,
    // profile-page vtable+0x5c) — NOT the 7.x clif_clickonplayer, which is a different, much larger shape.
    // All multi-byte ints are BIG-ENDIAN. Body (after opcode/increment):
    //   5 header strings (u8 len + bytes): title, clan, clanTitle, class, name  (order confirmed live)
    //   appearance: tag u8 (=0) + 7 look bytes (same 7-byte form as 0x33 type-0)
    //   3 × portrait graphic id (u16BE) -> FACE.EPF
    //   profile TEXT blurb (u8 len + bytes)
    //   numeric attr (u32BE)   look-selector A (u8)   look-selector B (u8)   NATION (u8)
    //   profile PICTURE (u16BE len + bytes)
    //   legend count (u8) + legends { icon u8, color u8, textLen u8, text }
    // NOTE: 4.95's click popup has NO totem slot (TOTEM.EPF is unreferenced in the client).
    // <paramref name="target"/> is whoever the profile is ABOUT (self, for your own "@click"/profile key;
    // another connected player for a real click — RTK clif_clickonplayer). The packet always goes out over
    // THIS session's own socket (Send()/SendMap() are instance methods of the VIEWER); the DATA comes from
    // the target's own character/equipment, which is legal to read cross-instance here since WeaponLook,
    // ShieldLook, ProfileCellIcon and GearListText are all private instance methods of this same Session
    // class — calling target.WeaponLook() runs them against the target's own _char, not the viewer's.
    private void SendClickProfile(Session target)
    {
        var tc = target._char;
        var d = new List<byte>();

        // header strings — order pinned by the marker test (each renders in its labeled slot)
        AddLenStr(d, tc.Title);
        AddLenStr(d, tc.ClanName);
        AddLenStr(d, tc.ClanTitle);
        AddLenStr(d, ClassTitleOf(tc));   // class + rank, same as the self-profile shows
        AddLenStr(d, tc.Name);

        // appearance descriptor — tag 0 selects the player look, identical to the 0x33 self-look that
        // already renders this character correctly on the map. Which is the point: this window's doll and
        // the on-map sprite must agree, and they only do if BOTH go through WriteAppearance — 5.33's
        // click-profile parser (0x4d19c0) resolves tag 0 to the same 11-byte reader (0x449880) as 0x33, so
        // handing it the 4.95 seven here reproduces the shifted-slot ragdoll on this window alone.
        d.Add(0);
        WriteAppearance(d, new byte[]
        {
            (byte)tc.Sex, 0, target.FaceLook(), tc.Armor,
            target.ArmorDye(), target.WeaponLook(), target.ShieldLook(),
        });

        // Equipment ICON cells beside the doll (no sprite layer for these slots in 4.95, so they render as
        // ground-icon boxes). Same IconWire encoding as the 0x37 equip window.
        //
        // 4.95 reads THREE bare u16 icons here; 5.33 reads FIVE cells of (u16 icon + u8 colour) — 15 bytes
        // against 6 — exactly as on 0x39. Recovered by firing an all-zero body and watching the offsets
        // (game-data/packets/probe34.txt): the cells land at body[17..31], right after the 11-byte
        // appearance record at [6..16]. Sending 4.95's six bytes shifted the gear list, the status flags,
        // the profile picture and the legends all nine bytes early, which is why this panel came up blank.
        if (_ver == ClientVersion.V533)
        {
            WriteProfileCell533(d, target, 4);   // helm
            WriteProfileCell533(d, target, 7);   // left ring
            WriteProfileCell533(d, target, 8);   // right ring
            WriteProfileCell533(d, target, 0);   // 4th cell — slot unidentified, empty box
            WriteProfileCell533(d, target, 0);   // 5th cell — ditto
        }
        else
        {
            d.AddRange(Be(target.ProfileCellIcon(4)));   // helm  (wire slot 4)
            d.AddRange(Be(target.ProfileCellIcon(7)));   // left ring  (wire slot 7)
            d.AddRange(Be(target.ProfileCellIcon(8)));   // right ring (wire slot 8)
        }

        // FIELD #10 — PAGE-1 gear/item list (u8 len + text). Item names are TAB-separated (client
        // converts 0x09 -> CR for multiline). Empty until inventory/equipment exists.
        //
        // 5.33 quirk (confirmed in the client): the list-populate `0x4c0170` NO-OPS on a zero-length
        // string — at `0x4c019b` a count of 0 jumps straight to the return, so it never clears the gear
        // box. A NON-empty string replaces the whole list (select-all `0x4bf670(0,0x7fff)` then copy).
        // Net effect: an unequipped character keeps showing whatever gear was last sent ("no matter what
        // I do, it still shows the old items"), while the paperdoll — a separate field — correctly goes
        // bare. Send a lone space when the list would be empty so the client runs the replace and the box
        // clears to blank. 4.95 uses a different control and is left alone.
        var gearList = target.GearListText();
        if (gearList.Length == 0 && _ver == ClientVersion.V533) gearList = " ";
        AddLenStr(d, gearList);

        // The TARGET'S ENTITY ID — not a spare scalar. RE'd 2026-08-21 from BOTH clients' 0x34 parsers
        // (4.95 0x48b6a0 reads it with the u32BE helper 0x475ce0 into the window field +0xb24; 5.33
        // 0x4d19c0 does the same via 0x4a1250 into +0xa88), and confirmed against RTK
        // clif_clickonplayer, which writes SWAP32(bl->id) in exactly this slot. The window STORES it and
        // the "Exchange" button hands it straight back: 4.95 0x48c7c7 does `mov eax,[edi+0xb24]` -> the
        // 0x4a builder at 0x48cd00 (`00 targetId(u32BE) 00`), 5.33 0x4d2cb2 the same from +0xa88. Sending
        // 0 here is why exchange did nothing on either client: the button fired, but every 0x4a arrived as
        // `00 00 00 00 00 00` and HandleExchangeRequest's PlayerById(0) found nobody. The click that OPENS
        // this window (0x43) already carries the id, so the client never needed to remember it — it reads
        // it back out of the reply.
        d.AddRange(Be32(tc.Id));
        // The two status cells beside the name — group (sociable) and exchange (trade), in RTK's order
        // (clif_clickonplayer writes FLAG_GROUP then FLAG_EXCHANGE right after the id). 0xff renders a cell
        // as a blank WHITE box; a real 0/1 shows the off/on indicator. Note what actually gates the two
        // BUTTONS: 4.95 stores this first byte at +0xb28 and BOTH handlers (Group 0x48c7ae, Exchange
        // 0x48c7c7) test only `cmp byte [edi+0xb28], 0xff` — 0xff kills both buttons, any other value
        // enables both. So these bytes are indicators, not per-button enables; whether a trade is actually
        // allowed is decided server-side in TryStartTrade (RTK does the same, in clif_startexchange).
        d.Add((byte)(tc.Grouped  ? 1 : 0));   // group / sociable status
        d.Add((byte)(tc.Exchange ? 1 : 0));   // exchange / trade status
        d.Add(tc.Nation);      // nation index -> NATION_E.EPF

        // FIELD #15 — profile PICTURE bitmap: u16BE size + bytes (empty = 00 00)
        var pic = tc.ProfilePic ?? Array.Empty<byte>();
        d.AddRange(Be((ushort)pic.Length));
        d.AddRange(pic);

        // FIELD #16 — PAGE-2 writable profile BLURB (u8 len + text). This is the free-text box, a
        // SEPARATE field from the page-1 gear list. Omitting it desyncs the legend count.
        var blurb = Encoding.ASCII.GetBytes(tc.ProfileText ?? "");
        if (blurb.Length > 255) blurb = blurb[..255];
        d.Add((byte)blurb.Length);
        d.AddRange(blurb);

        // FIELD #17/#18 — legends: count u8, then each { icon u8, color u8, textLen u8, text }
        var legs = tc.Legends ?? new List<Legend>();
        d.Add((byte)Math.Min(legs.Count, 255));
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        // FIELD #19 (5.33 only) — the GUARDIAN BACKDROP frame. This was the "broken/flipping background".
        // The 5.33 parser reads ONE trailing u8 after the legends (0x4d2184, gated on client build >= 0x213)
        // and stores it at window +0xb12; the profile paint (0x4d2354) uses it as the frame index into
        // SELFLOOK.EPF — the four guardian panes (0=? .. matching the totem: Chung Ryong / Ju Jak / Hyun Moo
        // / Baek Ho). We never sent it, so the client read whatever byte sat PAST our packet — garbage that
        // shifts with packet length, which is exactly why the backdrop was a random guardian (or an
        // out-of-range "corrupted" frame) and why armor/helm broke it far more often than gloves. The `s`
        // character sheet (0x39) was unaffected because it takes the guardian from the client's OWN cached
        // totem; the click card needs the TARGET's totem in the packet, since the client can't know another
        // player's. Clamp to 0..3 (a stray value would index a nonexistent frame = the corrupted pane).
        // 4.95's parser stops after its legends, so this trailing byte is invisible to it.
        if (_ver == ClientVersion.V533)
            d.Add((byte)Math.Clamp((int)tc.Totem, 0, 3));

        SendMap(0x34, _gameInc++, d.ToArray(), $"click-profile(0x34) id={tc.Id} nation={tc.Nation} blurb={blurb.Length}B legends={legs.Count}");
    }

    // Page-1 gear/item list for the click profile (the "inspect another player" view), TAB-separated (the
    // client turns 0x09 -> CR, one per line). Only FILLED slots get a line — an empty slot is omitted, not
    // shown as a bare label. Each line is the client's own equip LETTER + slot name + item name, in slot order:
    //   w:Weapon:<name>  a:Armor:<name>  s:Shield:<name>  h:Helmet:<name>  l:LHand:<name>  r:RHand:<name>
    // Slot keys are the WIRE slots the items are actually worn in (Equipment.Slot), not ItemDef.EquipSlot —
    // a second ring is worn in slot 8 (RHand) even though its def says 7. Called on whichever Session the
    // profile is ABOUT (see SendClickProfile), so this always reads ITS OWN _char.
    private static readonly (byte Slot, char Letter, string Label)[] GearListSlots =
    {
        (1, 'w', "Weapon"), (2, 'a', "Armor"), (3, 's', "Shield"),
        (4, 'h', "Helmet"), (7, 'l', "LHand"), (8, 'r', "RHand"),
    };

    private string GearListText()
    {
        var lines = GearListSlots
            .Select(s => (s, worn: _char.Equipment.FirstOrDefault(e => e.Slot == s.Slot)))
            .Select(x => (x.s, x.worn, def: x.worn is null ? null : Content.ItemById(x.worn.ItemId)))
            .Where(x => x.def is not null)
            .Select(x => $"{x.s.Letter}:{x.s.Label}:{(string.IsNullOrEmpty(x.worn!.CustomName) ? x.def!.Name : x.worn.CustomName)}");
        return string.Join('\t', lines);
    }

    // "@click" (self) / "@click <name>" (another connected player) — the debug entry point for the same
    // 0x34 packet a real click sends (HandleClickInfo). Useful for eyeballing the "view others" window
    // (and its Group/Exchange buttons, §11l) without needing a second live client to click you.
    private void ClickProfileCmd(string text)
    {
        string name = text.Trim();
        if (name.Length == 0) { SendClickProfile(this); return; }
        var target = _world.FindPlayer(name);
        if (target is null) { SendBlueMessage($"{name} is nowhere to be found."); return; }
        SendClickProfile(target);
    }

    // "@ckm" — send a 0x34 click-profile with DISTINCT MARKER strings in every text field, so we can
    // read off which window slot each field lands in and pin the true 4.95 layout (the 7.x port
    // misaligns). Numeric appearance (nation/totem/sprite) is handled by the parser RE separately.
    private void SendClickMarker()
    {
        var save = (_char.Title, _char.ClanName, _char.ClanTitle, _char.ClassName, _char.Name, _char.ProfileText, _char.Legends);
        _char.Title     = "TTL";
        _char.ClanName  = "CLAN";
        _char.ClanTitle = "CRANK";
        _char.ClassName = "CLASS";
        _char.Name      = "NAME";
        _char.ProfileText = "BLURBTEXT";
        _char.Legends   = new List<Legend> { new Legend(0, 0, "LEGEND") };
        SendClickProfile(this);
        (_char.Title, _char.ClanName, _char.ClanTitle, _char.ClassName, _char.Name, _char.ProfileText, _char.Legends) = save;
        Log.Info("   -> MARKER click-profile sent (TTL/CLAN/CRANK/CLASS/NAME/BLURBTEXT/LEGEND)");
    }

    /// <summary>Build an encrypted game packet, send it, and log it.</summary>
    private void SendMap(byte opcode, byte inc, byte[] data, string label)
    {
        var pkt = MapBuild(opcode, inc, data);
        Send(pkt);
        Log.Info($"   -> {label}: {pkt.Length}B  {Log.Hex(pkt)}");
    }

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private void SendMapInfo(ushort mapId, ushort xs, ushort ys, string title, ushort light, byte inc = 0)
    {
        var t = Encoding.ASCII.GetBytes(title);
        var b = new List<byte>();
        b.AddRange(Be(mapId));
        b.AddRange(Be(xs));
        b.AddRange(Be(ys));
        // Render mode. RTK's clif_sendmapinfo writes 5 normally and 4 when the player's "Weather change"
        // toggle is on (clif.c:4600), so this cell arms the map for weather drawing — the 0x1F state alone
        // isn't enough. Follows the same setting bit SendWeather gates on. Because this byte is the master
        // arm/disarm switch and only rides mapinfo, the 0x1b sub-6 toggle re-sends mapinfo in place
        // (Session.RefreshMapInPlace) so disabling weather clears it NOW instead of at the next map change.
        b.Add(_char.HasSetting(0x06) ? (byte)4 : (byte)5);
        b.Add(_realm);       // realm-center camera lock (0=off edge-aware, 1=on centered); toggled by F4
        b.Add((byte)t.Length);
        b.AddRange(t);
        // light field — encoding chosen by P1998_LIGHT_FMT so 5.33's parse can be probed live.
        var lv = LightValue;
        switch (LightFmt)
        {
            case "u8":    b.Add((byte)(lv & 0xFF)); break;                 // single byte (5.x may have narrowed it)
            case "leu16": b.Add((byte)(lv & 0xFF)); b.Add((byte)(lv >> 8)); break;  // little-endian u16
            default:      b.AddRange(Be((ushort)lv)); break;              // big-endian u16 (4.95-proven)
        }
        Log.Info($"   -> mapinfo(0x15) light={lv} fmt={LightFmt}");
        Send(MapBuild(Opcode.MapInfo, inc, b.ToArray()));
    }

    // In-world command feedback that lands in the CHAT LOG. The client's chat pane + over-head bubbles
    // are both driven by 0x0D speech (RE: handler 0x450170 → 0x44dc90 registers a 3s text object into the
    // world message-manager at world+0x418). The 0x02 SendMessage path is a login-style message BOX that
    // doesn't stack for multi-line output (why @maps/@mobs showed nothing). So command results speak as
    // the player's own entity → one chat-log line each. ASCII, clamped to the 0x0D u8 length field.
    // The 4.95 client's text boxes render a plain ASCII/codepage font, and Encoding.ASCII flattens anything
    // outside 0x00-0x7F to '?'. We routinely write typographic punctuation in messages (em-dash, curly quotes,
    // ellipsis), so transliterate those to ASCII first — otherwise "You cast Sanctuary — ..." shows as "?".
    private static byte[] AsciiBytes(string s)
    {
        s = s.Replace('—', '-').Replace('–', '-')     // em / en dash -> hyphen
             .Replace('‘', '\'').Replace('’', '\'')   // curly single quotes -> '
             .Replace('“', '"').Replace('”', '"')     // curly double quotes -> "
             .Replace("…", "...");                          // ellipsis -> ...
        return Encoding.ASCII.GetBytes(s);
    }

    private void SendLog(string text)
    {
        if (text.Length > 250) text = text[..250];
        SendSpeech(0, _char.Id, AsciiBytes(text));
    }

    // The client's STATUS / MINI-TEXT box — the scrolling log pane that sits below the inventory (where
    // "item dropped", "experience gained", look-at names, etc. belong). This is a DIFFERENT channel from
    // both the 0x0D chat bubble (SendLog) and the 0x02 login message box (SendMessage). RTK drives it via
    // clif_sendminitext → clif_sendmsg(sd, 3, msg).
    // type: 0=wisp(blue) · 3=mini/status text · 5=system · 11=group · 12=clan.
    //
    // TRUE LAYOUT (RE'd 2026-08-07): `type(u8) len(u16BE) text` — NOT `type(u16LE) len(u8)`. Every 0x0A
    // handler in the client reads it that way (e.g. 0x47c520, 0x490930, 0x40eeb0: u8 at body[0], then
    // 0x475ca0 — the big-endian u16 reader — at body[1]). The old reading happened to produce identical
    // bytes because our type high byte is always 0 and our text was always under 256 chars; it would have
    // silently truncated the moment either stopped being true. The text cap here is the client's own
    // 0x8000-char widen buffer, not 255.
    private void SendMiniText(string text, ushort type = 3)
    {
        var t = AsciiBytes(text);
        if (t.Length > 0x7FFF) t = t[..0x7FFF];
        var body = new List<byte> { (byte)type, (byte)(t.Length >> 8), (byte)t.Length };
        body.AddRange(t);
        SendMap(0x0A, _gameInc++, body.ToArray(), $"minitext(0x0A) type={type} {t.Length}B");
    }

    /// <summary>RTK's clif_sendbluemessage — the whisper/wisp BLUE chat channel (0x0A type 0, the same
    /// channel whispers themselves render on). Whisper-family feedback (target not found, can't hear you,
    /// not-in-a-group/clan) belongs HERE, not on SendLog: SendLog is 0x0D self-speech, so routing an error
    /// line through it made the client speak the failure out loud as the player's own words.</summary>
    private void SendBlueMessage(string text) => SendMiniText(text, type: 0);

    /// <summary>A server-wide announcement (restart warnings — see <see cref="RestartSchedule"/>).
    ///
    /// <para>The 0x0A system line (type 5) ONLY. This used to also call <see cref="SendLog"/> on the theory
    /// that a restart notice is worth saying twice — but SendLog is <c>SendSpeech(0, _char.Id, …)</c>, the
    /// very same call the normal say path uses, so the second copy came out as a SPEECH BUBBLE over the
    /// player's own head: the server appeared to be putting words in their mouth, and it was truncated at
    /// SendLog's 250-char chat cap into the bargain. One channel, and it's this one.</para></summary>
    internal void SystemAnnounce(string text) => SendMiniText(text, type: 5);

    // ---- helpers ----
    private void SendMessage(string text)
    {
        var t = AsciiBytes(text);
        var body = new List<byte> { 0x0F, (byte)t.Length };
        body.AddRange(t);
        body.Add(0);
        var enc = TkCrypt.Crypt(body.ToArray(), 0x02, TkCrypt.LoginKey);
        Send(TkPacket.Build(0x02, 0x02, enc));
        Log.Info($"   -> message: {text}");
    }

    /// <summary>
    /// Game packet: AA | len(u16 BE) | op | inc | body. The body is encrypted with the SAME
    /// simple NexonInc cipher as the login channel — confirmed by reversing NexusTK.exe: 4.95
    /// has ONE cipher (decrypt 0x478680 / key buffer 0x50211c built only from "NexonInc.",
    /// keylen 9, identity table 0x4f3358). No name-derived/table cipher, no 3 trailer bytes —
    /// those are 7.x-only and were the bug in the previous version of this method.
    /// </summary>
    private byte[] MapBuild(byte opcode, byte inc, byte[] data)
    {
        var enc = TkCrypt.Crypt(data, inc, TkCrypt.LoginKey);
        return TkPacket.Build(opcode, inc, enc);
    }

    private static byte[] Be(ushort v) => new[] { (byte)(v >> 8), (byte)(v & 0xFF) };

}
