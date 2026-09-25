using Protocol.Tk495;
using Shared;

namespace Server;

/// <summary>
/// The NATIVE exchange window — RTK's real binary trade UI, not a dialog stand-in.
///
/// <para>Server -&gt; client is opcode <c>0x42</c> (RTK <c>clif_startexchange</c> / <c>clif_exchange_additem</c> /
/// <c>clif_exchange_money</c> / <c>clif_exchange_message</c> / <c>clif_exchange_sendok</c>, clif.c:14756-15230);
/// client -&gt; server is <c>0x4a</c> (<c>clif_parse_exchange</c>, clif.c:14647). Both halves were RE'd out of the
/// clients on 2026-08-21 rather than taken from RTK, because RTK is a 7.x server and 4.95 diverges from it in
/// this packet family exactly the way it does on the profile/bag packets (see the sub-2 note below).</para>
///
/// <para><b>4.95 (<c>NexusTK_local.exe</c>).</b> The world dispatcher's <c>0x42</c> trampoline is
/// <c>0x451120</c>, and it handles <b>only</b> <c>body[0]==0</c>: it allocates the <c>0x288</c>-byte window and
/// runs its ctor <c>0x420e80</c>, which reads the target id into <c>[win+0x278]</c> and the label string. Any
/// other sub-type returns <c>al=0</c> ("not mine") and falls through the dispatcher chain to the WINDOW's own
/// packet handler <c>0x4216a0</c> (vtable <c>0x4caf14</c> slot +0x4c): it re-checks the opcode is <c>0x42</c>,
/// then jumps <c>body[0]</c> 1..5 through the table at <c>0x421704</c> —
/// 1 -&gt; <c>0x421830</c>, 2 -&gt; <c>0x4218a0</c>, 3 -&gt; <c>0x421980</c>, 4 -&gt; <c>0x421a00</c>,
/// 5 -&gt; <c>0x421b00</c>. 5.33 is the same design: trampoline <c>0x463971</c> (window <c>0x264</c>, ctor
/// <c>0x42b300</c>), window handler <c>0x42be60</c>, table <c>0x42bfb4</c>.
/// <b>This is why "the server never sent 0x42" looked like a dead protocol:</b> the only handler the opcode
/// map lists is the one that opens the window, and it rejects everything else — the rest of the conversation
/// belongs to an object that does not exist until sub-type 0 has been sent.</para>
///
/// <para><b>Outbound <c>0x42</c> bodies</b> (body[0] = sub-type; offsets below are body-relative, i.e. RTK's
/// <c>WFIFOB(fd,5)</c> is <c>body[0]</c>):</para>
/// <list type="table">
/// <item><term>0 open</term><description><c>00 targetId(u32BE) nameLen(u8) name[] level(u16BE)</c>. The ctor
///   reads the id and the name only — <b>neither client reads the level</b>; it is sent because RTK does and it
///   costs two bytes. Goes to BOTH sides, each naming the OTHER.</description></item>
/// <item><term>1 ask amount</term><description><c>01 slot(u8, 1-based)</c>. Pops the client's own quantity
///   prompt, which answers with inbound type 2.</description></item>
/// <item><term>2 add row</term><description><c>02 side(u8) rowKey(u8) icon(u16BE) [colour(u8) — 5.33 ONLY]
///   nameLen(u8) name[]</c>. <c>side</c> 0 = the recipient's OWN list (control 5), non-0 = the other party's
///   list (control 8). <b>The 4.95 divergence:</b> 4.95 (<c>0x4218a0</c>) reads the name length at body[5]
///   straight after the icon; 5.33 (<c>0x42c240</c>) reads an icon-COLOUR byte there first — the same extra
///   byte 5.33 adds to <c>0x0F</c>, <c>0x39</c> and <c>0x34</c>. <b><c>rowKey</c> is a KEY, not a
///   position:</b> <c>0x421d60</c> looks it up with <c>0x421dd0</c> (a linear scan comparing each row's stored
///   first byte) and REPLACES that row when it already exists, appending only when it does not. We key rows by
///   the offerer's bag slot, so re-offering a slot rewrites its row instead of duplicating it.</description></item>
/// <item><term>3 gold</term><description><c>03 side(u8) gold(u32BE)</c>, same <c>side</c> convention (controls
///   6 / 9). A DECREASE trips the client's own 10-second warning flash (<c>0x421600</c>) — that is the stock
///   anti-scam cue and it is client-side, so we simply keep the value honest.</description></item>
/// <item><term>4 message</term><description><c>04 extra(u8) len(u8) text[]</c>. Pops an OK box with the text
///   and <b>closes the window</b> — so this is the cancel/refusal path, not a status line.</description></item>
/// <item><term>5 finish</term><description><c>05 extra(u8) len(u8) text[]</c>. A two-step latch:
///   <c>extra != 0</c> sets the window's "them" flag, <c>extra == 0</c> sets its "me" flag, and only once BOTH
///   are set does it pop the box and close. So the FIRST confirm must go out as <c>extra=1</c> to both sides
///   and the finalizing one as <c>extra=0</c> to both — exactly RTK's <c>clif_exchange_sendok</c>.</description></item>
/// </list>
///
/// <para><b>Inbound <c>0x4a</c> bodies</b> — every one carries the target id, so a stale window can be told
/// from the live one: 0 <c>00 id(u32BE) 00</c> initiate; 1 <c>01 id(u32BE) slot(u8) 00</c> offer a bag slot;
/// 2 <c>02 id(u32BE) slot(u8) amount(u8) 00</c> offer N of it (the amount is a u8 — 5.33 clamps it to 255 at
/// <c>0x42d489</c>); 3 <c>03 id(u32BE) gold(u32BE) 00</c>; 4 <c>04 id(u32BE) 00</c> cancel; 5
/// <c>05 id(u32BE) 00</c> confirm. Builders: 4.95 <c>0x422300</c> / <c>0x4229b0</c> / <c>0x4217c0</c> /
/// <c>0x421770</c> / <c>0x421720</c>.</para>
///
/// <para><b>Two deliberate deviations from RTK,</b> both carried over from the dialog implementation and both
/// about not losing items: (1) <b>no escrow</b> — RTK <c>pc_delitem</c>s an offered item out of your bag at
/// offer time and <c>clif_exchange_close</c> puts it back, so an RTK crash mid-trade eats the goods. Here an
/// offer is only a promise; <c>TransferItems</c> re-checks the sender still holds it at finalize and can only
/// ever under-deliver. (2) <b>any offer change un-confirms both sides</b>, which RTK gets for free from escrow
/// (an escrowed offer can only grow). There is no "un-confirm" packet, so the client keeps showing its latched
/// indicator; that is cosmetic — completion only ever happens on the <c>05 extra=0</c> we choose to send.</para>
/// </summary>
public partial class Session
{
    // 0x42 sub-types, server -> client.
    private const byte ExcOpen = 0, ExcAskAmount = 1, ExcAddRow = 2, ExcGold = 3, ExcMessage = 4, ExcFinish = 5;

    // ---- inbound (0x4a) -------------------------------------------------------------------------------

    /// <summary>RTK <c>clif_parse_exchange</c>. Sub-type 0 opens a trade with the id the click-profile window
    /// handed back (see <c>SendClickProfile</c>); 1-5 all drive an already-open window and are dropped unless
    /// they name the partner we actually have.</summary>
    private void HandleExchangeRequest(byte[] dec)
    {
        if (dec.Length < 5) return;
        byte sub = dec[0];
        uint targetId = (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]);

        if (sub == ExcOpen)
        {
            var target = _world.Online.ById(targetId);
            if (target is not null) TryStartTrade(target);
            return;
        }

        var trade = _trade;
        if (trade is null || trade.Ended) return;
        // RTK's type-5 branch disconnects the socket on a mismatch here; a stale window is not worth a kick,
        // so the packet is dropped instead. Either way the point is the same: one window never drives another
        // window's trade.
        if (trade.Other(this).PlayerId != targetId) return;

        switch (sub)
        {
            case 1: if (dec.Length >= 6) OfferBagSlot(trade, dec[5], amount: 0); break;
            case 2: if (dec.Length >= 7) OfferBagSlot(trade, dec[5], dec[6]);    break;
            case 3:
                if (dec.Length >= 9)
                    OfferGold(trade, (uint)((dec[5] << 24) | (dec[6] << 16) | (dec[7] << 8) | dec[8]));
                break;
            case 4: EndTrade(trade, "Exchange cancelled."); break;
            case 5: ConfirmExchange(trade); break;
        }
    }

    /// <summary>Sub-types 1 and 2 share this. <paramref name="wireSlot"/> is the client's own 1-BASED bag slot
    /// — the same number <c>WireSlot</c> puts in the 0x0F that drew the item, and the same one RTK converts
    /// with <c>id = RFIFOB(fd,10) - 1</c> before indexing its inventory array. <c>InvItem.Slot</c> is 0-based,
    /// so this has to subtract; feeding <see cref="InvAt"/> the raw wire byte offers the NEXT item down the
    /// bag instead of the one that was picked.
    ///
    /// <para><paramref name="amount"/> 0 means the client has not picked a quantity yet (sub-type 1): a stack
    /// of more than one bounces back as an "ask amount" prompt — RTK's own <c>inventory[id].amount &gt; 1</c>
    /// branch — and anything else is staged as a single. The prompt echoes <paramref name="wireSlot"/>
    /// unchanged, because the client's amount dialog stores it and hands it straight back on sub-type 2.</para></summary>
    private void OfferBagSlot(Trade trade, byte wireSlot, byte amount)
    {
        if (wireSlot == 0) return;                 // 1-based on the wire; 0 is not a slot
        var it = InvAt(wireSlot - 1);
        if (it is null) return;
        if (Content.ItemById(it.ItemId) is null) return;

        if (amount == 0)
        {
            if (it.Amount > 1) { SendExchangeAskAmount(wireSlot); return; }
            amount = 1;
        }
        RecordTradeItemOffer(trade, it, Math.Clamp((int)amount, 1, it.Amount));
    }

    /// <summary>Stage an item into this side's offer and redraw its row on BOTH windows. Shared with the
    /// native hand-item gesture (<see cref="HandItemToPlayer"/>). Keyed by bag slot, which is also the row key
    /// the client de-duplicates on, so offering one slot twice rewrites a row rather than adding a second.</summary>
    private void RecordTradeItemOffer(Trade trade, InvItem it, int amount)
    {
        var def = Content.ItemById(it.ItemId);
        if (def is null) return;

        // ItmExchangeable, the registry's own trade ban (RTK clif_exchange_additem, refusal line verbatim).
        // Gated HERE because this is the one funnel both offer routes share — the window's sub-type 1/2 and
        // the native hand-to-player gesture. NOT gated on NoDrop: RTK's exchange checks only this flag, and
        // most of its thousand NoDrop rows trade freely.
        if (def.NoTrade) { SendMiniText("You cannot exchange that."); return; }

        var mine = trade.OfferOf(this);
        mine.Items.RemoveAll(x => x.Slot == it.Slot);
        mine.Items.Add(new InvItem(it.Slot, it.ItemId, amount, it.Dura) { CustomName = it.CustomName, Owner = it.Owner });
        UnconfirmBoth(trade);

        // The row key is the WIRE slot, so it lives in the same 1-based space the client keys its own bag
        // rows by — the staged InvItem keeps the internal 0-based Slot, which is what finalize matches on.
        byte rowKey = WireSlot(it);
        SendExchangeRow(mine: true, rowKey, def, it, amount);
        trade.Other(this).SendExchangeRow(mine: false, rowKey, def, it, amount);
    }

    /// <summary>RTK <c>clif_parse_exchange</c> case 3: an over-budget amount is refused by re-echoing what is
    /// actually staged, so the window snaps back to the truth instead of showing a number we will not honour.
    /// Shared with the native hand-gold gesture (<see cref="HandleHandGold"/>).</summary>
    private void OfferGold(Trade trade, uint gold)
    {
        var mine = trade.OfferOf(this);
        if (gold <= _char.Coins) { mine.Gold = gold; UnconfirmBoth(trade); }

        SendExchangeGold(mine: true, mine.Gold);
        trade.Other(this).SendExchangeGold(mine: false, mine.Gold);
    }

    /// <summary>RTK <c>clif_exchange_sendok</c>: the SECOND confirm finalizes. The <c>extra</c> byte drives the
    /// client's two-flag latch, so the first confirm is broadcast as 1 and the finalizing one as 0 — see the
    /// class doc.</summary>
    private void ConfirmExchange(Trade trade)
    {
        var mine  = trade.OfferOf(this);
        var other = trade.Other(this);

        // #57: re-ask TryStartTrade's gate. It used to be asked exactly once, when the window opened, and
        // nothing looked again — so a party who had warped away or been killed could still finalize. The
        // teardowns in Die() and Session.EnterMap close the window at the moment either of those happens;
        // this is the check that agrees with them, and it covers the orders they cannot: a confirm already on
        // our read loop when the victim's Hp reaches 0 (that write and Die()'s teardown are two steps under
        // the victim's monitor, and this runs under ours), and any future path that relocates a player
        // without going through Session.EnterMap.
        //
        // Refused on BOTH confirms, not only the finalizing one: a latch a dead trade is allowed to set is a
        // second state to reason about for no gain, and the window has to close either way. Same line the
        // cancel and disconnect teardowns use — RTK has no separate walk-away text to port.
        //
        // #168: a side a newer login has replaced is refused here too, for the early answer. The re-check inside
        // the pair (FinalizeTradeLocked) is the one that holds, since the kick can land after this read.
        if (IsDead || other.IsDead || other.CharMap != CharMap || IsReplaced || other.IsReplaced)
        {
            EndTrade(trade, "Exchange cancelled.");
            return;
        }

        mine.Confirmed = true;

        if (trade.OfferOf(other).Confirmed) { FinalizeTrade(trade); return; }

        SendExchangeFinish(1, TradeDoneText);
        other.SendExchangeFinish(1, TradeDoneText);
    }

    internal const string TradeDoneText = "You exchanged, and gave away ownership of the items.";

    // ---- outbound (0x42) ------------------------------------------------------------------------------

    /// <summary>Sub-type 0 — open the window. <paramref name="other"/> is whoever THIS window is about, named
    /// in RTK's <c>"&lt;name&gt;(&lt;class&gt;)"</c> form.</summary>
    private void SendExchangeOpen(Session other)
    {
        var oc = other._char;
        SendMap(ServerOp.Exchange, _gameInc++, ExchangeOpenBody(oc.Id, $"{oc.Name}({ClassTitleOf(oc)})", oc.Level),
                $"exchange-open(0x42/0) with={oc.Name} id={oc.Id}");
    }

    /// <summary>The sub-type 0 body, split out so a test can pin it (see Tests/ExchangeWireTests.cs). Both
    /// clients' ctors read the id and the label and stop; the trailing level is RTK's and is sent for
    /// fidelity, not because anything consumes it.</summary>
    public static byte[] ExchangeOpenBody(uint targetId, string label, byte level)
    {
        var d = new List<byte> { ExcOpen };
        d.AddRange(PacketWriter.U32BEBytes(targetId));
        AddLenStr(d, label);
        d.AddRange(PacketWriter.U16BEBytes(level));
        return d.ToArray();
    }

    /// <summary>Sub-type 1 — "how many?" for a bag slot. The client owns the prompt and answers with 0x4a
    /// type 2; nothing is staged until it does.</summary>
    private void SendExchangeAskAmount(byte slot) =>
        SendMap(ServerOp.Exchange, _gameInc++, new[] { ExcAskAmount, slot }, $"exchange-askamount(0x42/1) slot={slot}");

    /// <summary>Sub-type 2 — draw or replace one row. <paramref name="mine"/> picks which of the two lists it
    /// lands in on THIS recipient's window.</summary>
    private void SendExchangeRow(bool mine, byte rowKey, ItemDef def, InvItem it, int amount)
    {
        var body = ExchangeRowBody(_ver, mine, rowKey, IconWire(IconOf(def)), def.IconColor,
                                   ExchangeRowLabel(def, it, amount));
        SendMap(ServerOp.Exchange, _gameInc++, body,
                $"exchange-row(0x42/2) {(mine ? "mine" : "theirs")} key={rowKey} {def.Name} x{amount}");
    }

    /// <summary>The sub-type 2 body — the ONE place in this packet family where the two clients disagree, so
    /// it is split out and pinned by a test (Tests/ExchangeWireTests.cs). 5.33's parser reads an icon-colour
    /// byte between the icon and the name length; 4.95's does not, and feeding it one shifts the name length
    /// into the colour byte — the same failure the bag (0x0F) and profile (0x39/0x34) packets have already
    /// been through.</summary>
    public static byte[] ExchangeRowBody(ClientVersion ver, bool mine, byte rowKey, ushort iconWire,
                                           byte iconColor, string label)
    {
        var d = new List<byte> { ExcAddRow, (byte)(mine ? 0 : 1), rowKey };
        d.AddRange(PacketWriter.U16BEBytes(iconWire));
        if (ver == ClientVersion.V533) d.Add(iconColor);
        AddLenStr(d, label);
        return d.ToArray();
    }

    /// <summary>Sub-type 3 — one side's gold cell.</summary>
    private void SendExchangeGold(bool mine, uint gold)
    {
        var d = new List<byte> { ExcGold, (byte)(mine ? 0 : 1) };
        d.AddRange(PacketWriter.U32BEBytes(gold));
        SendMap(ServerOp.Exchange, _gameInc++, d.ToArray(), $"exchange-gold(0x42/3) {(mine ? "mine" : "theirs")} {gold}");
    }

    /// <summary>Sub-type 4 — pop an OK box and CLOSE the window. This is the cancel/refusal path.</summary>
    private void SendExchangeMessage(string text)
    {
        var d = new List<byte> { ExcMessage, 0 };
        AddLenStr(d, text);
        SendMap(ServerOp.Exchange, _gameInc++, d.ToArray(), $"exchange-message(0x42/4) \"{text}\"");
    }

    /// <summary>Sub-type 5 — the confirm latch. <paramref name="extra"/> 1 = "a side has confirmed",
    /// 0 = "the second side confirmed"; the window only closes once it has seen both.</summary>
    private void SendExchangeFinish(byte extra, string text)
    {
        var d = new List<byte> { ExcFinish, extra };
        AddLenStr(d, text);
        SendMap(ServerOp.Exchange, _gameInc++, d.ToArray(), $"exchange-finish(0x42/5) extra={extra}");
    }

    /// <summary>The row caption, in RTK <c>clif_exchange_additem</c>'s order: the name is truncated to 15
    /// characters, a stack shows its count, and a durability-bearing item shows condition instead — the
    /// percentage form for gear and the charge form for a charged consumable, both matching what the bag's own
    /// 0x0F label already says so one item never reads two ways.</summary>
    private static string ExchangeRowLabel(ItemDef def, InvItem it, int amount)
    {
        string name = string.IsNullOrEmpty(it.CustomName) ? def.Name : it.CustomName;
        if (name.Length > 15) name = name[..15];
        ushort dura = it.Dura == 0 ? def.Durability : it.Dura;
        if (def.IsEquip && def.Durability > 0) return $"{name} ({dura * 100 / def.Durability}%)";
        if (def.IsCharged) return $"{name} [{dura} {def.Text}]";
        return amount > 1 ? $"{name} ({amount})" : name;
    }
}
