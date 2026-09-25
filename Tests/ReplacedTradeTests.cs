using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #168 item 4b: an exchange cannot finish against a session a newer login has replaced.
///
/// <para><b>The hole</b> (the PR #291 review's pre-existing defect). O and P are trading and O has confirmed.
/// The player logs in again: the new login's kick writes O's row and latches <c>_replaced</c>, and the new login
/// loads that row. Before O's own teardown ends the trade, P confirms. <c>FinalizeTradeLocked</c> moved the goods
/// under both monitors and <c>FlushPair</c> wrote both rows — the pair write does not go through the
/// single-session chokepoint that refuses a replaced session. The new login still holds in memory whatever O
/// gave, so its next save puts it back: a duplication. Whatever P gave O is erased the same way.</para>
///
/// <para><b>The fix.</b> <c>FinalizeTradeLocked</c> refuses, before anything moves, when either side is replaced,
/// and ends the trade with the ordinary "Exchange cancelled."; the confirm gate asks the same question earlier.
/// The re-check inside the pair is the one that holds: the kick latches under O's monitor, which the pair
/// holds.</para>
///
/// <para>Gold on both sides of the table, so "nothing moved" is two balances and two rows.</para>
/// </summary>
[Collection("world")]
public class ReplacedTradeTests
{
    private const byte ExchangeIn = 0x4a, ExchangeOut = 0x42;
    private const byte ExcOpen = 0, ExcGold = 3, ExcConfirm = 5;
    private const string CancelText = "Exchange cancelled.";

    /// <summary>Content-free map ids; no other class stands here.</summary>
    private const ushort ConfirmMap = 61730, FinalizeMap = 61731;

    private static readonly MethodInfo FinalizeTrade =
        typeof(Session).GetMethod("FinalizeTrade", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo TradeField =
        typeof(Session).GetField("_trade", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;

    public ReplacedTradeTests(SessionFixture fx) => _fx = fx;

    /// <summary>The partner's confirm, through the real <c>0x4a</c> frame, after the other side was kicked by a
    /// new login. After: P's window closes with "Exchange cancelled.", both purses are as they were, and neither
    /// row moved. Before: P's confirm finalized, 500 and 300 coin crossed over, and O's row took P's 500 while
    /// the new login held O's 300.
    ///
    /// <para>Falsified (see the #168 report) by removing BOTH refusals, the confirm gate's and
    /// <c>FinalizeTradeLocked</c>'s: red on the purses. Removing only the confirm gate stays green, because the
    /// re-check inside the pair then refuses; that re-check has its own fact below.</para></summary>
    [Fact]
    public void AConfirmAgainstAReplacedPartnerCancelsAndMovesNothing()
    {
        const string oName = "TradeReplacedO", pName = "TradeReplacedP";
        var (o, _, oChar) = _fx.PlayerWith(oName, c => c.Coins = 300, ConfirmMap, 5, 5);
        var (p, pOut, pChar) = _fx.PlayerWith(pName, c => c.Coins = 500, ConfirmMap, 6, 5);

        try
        {
            o.Receive(ExchangeRequest(ExcOpen, p.PlayerId));
            Assert.Single(pOut.BodiesOf(ExchangeOut));                    // the window is open on P's side
            o.Receive(ExchangeRequest(ExcGold, p.PlayerId, Be32(300)));
            p.Receive(ExchangeRequest(ExcGold, o.PlayerId, Be32(500)));
            o.Receive(ExchangeRequest(ExcConfirm, p.PlayerId));          // O has confirmed

            o.KickForReplacement();                                       // the new login's kick
            string oRow = RowJson(oName);
            var pRowBefore = _fx.Store.Load(pName).Status;

            pOut.Clear();
            p.Receive(ExchangeRequest(ExcConfirm, o.PlayerId));          // the partner's finalizing confirm

            Assert.Equal(300u, oChar.Coins);                              // nothing moved, either way
            Assert.Equal(500u, pChar.Coins);
            Assert.Equal(Message(CancelText), Assert.Single(pOut.BodiesOf(ExchangeOut)));
            Assert.Equal(oRow, RowJson(oName));
            Assert.Equal(pRowBefore, _fx.Store.Load(pName).Status);
            Assert.Null(TradeField.GetValue(p));                          // P is free to trade again
        }
        finally
        {
            _fx.World.LeaveMap(o, ConfirmMap);
            _fx.World.LeaveMap(p, ConfirmMap);
        }
    }

    /// <summary>The re-check inside the pair, on its own: the finalize is entered as a confirm that passed its
    /// gate BEFORE the kick would enter it, and the kick lands before the finalize takes the pair. That is the
    /// order the confirm gate cannot see; <c>FinalizeTrade</c> is invoked directly to put it there without a
    /// race.
    ///
    /// <para>Falsified (see the #168 report) by removing only <c>FinalizeTradeLocked</c>'s replaced check: red
    /// on the purses, with O's row carrying P's 500.</para></summary>
    [Fact]
    public void AFinalizeAgainstAReplacedSideInsideThePairCancelsAndMovesNothing()
    {
        const string oName = "TradeFinalizeO", pName = "TradeFinalizeP";
        var (o, oOut, oChar) = _fx.PlayerWith(oName, c => c.Coins = 300, FinalizeMap, 5, 5);
        var (p, pOut, pChar) = _fx.PlayerWith(pName, c => c.Coins = 500, FinalizeMap, 6, 5);

        try
        {
            o.Receive(ExchangeRequest(ExcOpen, p.PlayerId));
            o.Receive(ExchangeRequest(ExcGold, p.PlayerId, Be32(300)));
            p.Receive(ExchangeRequest(ExcGold, o.PlayerId, Be32(500)));
            o.Receive(ExchangeRequest(ExcConfirm, p.PlayerId));
            var trade = TradeField.GetValue(p);
            Assert.NotNull(trade);

            o.KickForReplacement();
            string oRow = RowJson(oName);
            var pRowBefore = _fx.Store.Load(pName).Status;

            pOut.Clear();
            p.WithState(() => FinalizeTrade.Invoke(null, new[] { trade }));   // on P's thread, as its confirm runs

            Assert.Equal(300u, oChar.Coins);
            Assert.Equal(500u, pChar.Coins);
            Assert.Equal(Message(CancelText), Assert.Single(pOut.BodiesOf(ExchangeOut)));
            Assert.Equal(oRow, RowJson(oName));
            Assert.Equal(pRowBefore, _fx.Store.Load(pName).Status);
            Assert.Null(TradeField.GetValue(p));
            Assert.Null(TradeField.GetValue(o));
        }
        finally
        {
            _fx.World.LeaveMap(o, FinalizeMap);
            _fx.World.LeaveMap(p, FinalizeMap);
        }
    }

    // ===== plumbing =====================================================================================

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>An inbound <c>0x4a</c>: sub-type, the partner's entity id, then whatever that sub-type carries
    /// (RTK's trailing zero when it carries nothing). <c>TradeTeardownTests</c>' shape.</summary>
    private static byte[] ExchangeRequest(byte sub, uint targetId, byte[]? tail = null) =>
        SessionFixture.Frame(ExchangeIn, new byte[] { sub }.Concat(Be32(targetId))
                                                           .Concat(tail ?? new byte[] { 0 }).ToArray());

    /// <summary>Sub-type 4 as it goes out: <c>04 | 00 | len | text</c>, the box that closes a window that did
    /// not finish.</summary>
    private static byte[] Message(string text) =>
        new byte[] { 4, 0, (byte)text.Length }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();

    private string RowJson(string name)
    {
        var load = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return CharacterStore.Serialize(Assert.IsType<Character>(load.Character));
    }
}
