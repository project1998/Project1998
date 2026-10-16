using System.Diagnostics;
using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Five more writes into another player's state take that player's monitor (#339's worker report, "Other verbs with
/// the same defect", and its review, hunt 4 and F4). Each one ran on the ACTING player's thread, which holds that
/// player's own monitor (and, for a cast, the Lua gate), and wrote the other player's state without the other's
/// monitor:
/// <list type="number">
/// <item>The Valor family at another player (Valor, Strengthen, Bless Muscles, Power Burst; Mage and Poet):
/// <c>LuaApplyBuff</c> to the target's <c>ReceiveTimedBuff</c>, which opens with the guarded <c>BuffRemoveAll</c>.
/// In a Debug build the <c>_buffs</c> guard fired inside the verb with the mana already spent.</item>
/// <item>The Sanctuary family (Sanctuary, Protect Soul, Guard Life, Magic Shield): <c>ApplySanctuaryDeduction</c>
/// wrote <c>_sancDeduct*</c>. No guard covers those fields.</item>
/// <item>Mana Gift and Mana Steal: <c>LuaSetTargetMana</c> wrote the target's <c>_char.Mp</c>. No guard.</item>
/// <item>Breaking off an engagement and a divorce at the Chapel: the partner's legend, engagement, spouse and
/// keepsake, written from the clicking player's dialog. <c>SaveChar</c>'s <c>_char</c> guard fired.</item>
/// <item><c>ApplyMute</c>, from a GM's <c>@mute</c>/<c>@unmute</c>: the target's <c>_mutedUntil</c> and
/// <c>_muteReason</c>. No guard.</item>
/// </list>
///
/// <para><b>How "inside the target's monitor" is observed in Release, where the guards are compiled out.</b> As in
/// <see cref="DispellTargetLockTests"/>: a third thread takes the target's monitor and holds it while the action runs
/// on its own thread. A write made inside the target's monitor has to wait for it; the old writes did not, and the
/// third thread finds the state changed while it still holds the monitor. Both <c>StateRank</c> orders, so both of
/// <c>EnterState</c>'s branches are the ones that wait.</para>
///
/// <para>Each action is driven through its real entry: the 0x0F cast frame, the Chapel NPC's dialog through the
/// 0x43 click and the 0x3A replies, and the GM's chat line. The races call the handler bodies under the actor's
/// monitor, as the packet handler does, below its action budget. A dialog is driven from a thread of its own: the
/// dialog's awaits resume inline on the thread that delivers the reply (<c>Session.HandleNpcDialog</c>), which is
/// the read loop in production, and a test thread's synchronization context would move them elsewhere.
/// Content-free maps no other class uses; the Chapel facts stand the clicker on the Crystalline Chapel (3815),
/// which no other class uses either.</para>
/// </summary>
[Collection("world")]
public sealed class PeerWritesLockTests
{
    private const ushort ValorMap = 62410, ValorHeldMap = 62411, ValorRaceMap = 62412, ValorPinMap = 62413,
                         SancMap = 62414, SancHeldMap = 62415, SancRaceMap = 62416, SancPinMap = 62417,
                         ManaMap = 62418, ManaHeldMap = 62419, ManaRaceMap = 62420, ManaPinMap = 62421,
                         VowMap = 62422, VowHeldMap = 62423, VowRaceMap = 62424, VowPinMap = 62425,
                         MuteMap = 62426, MuteHeldMap = 62427, MuteRaceMap = 62428, MutePinMap = 62429;

    /// <summary>Crystal, the Crystalline Chapel's Chapel NPC (game-data/NPCs.csv, NpcId 397, map 3815): the real NPC
    /// whose Break Off Engagement and Divorce entries reach the vow writers (Server/NpcAbility.cs, ChapelAbility).</summary>
    private const int ChapelNpcId = 397;

    private const uint Mana = 10_000;
    private const int Rounds = 300;

    /// <summary>The party line the races' players send each other: a peer entry through <c>EnterState</c>
    /// outside Lua, going the other way from the action under test.</summary>
    private const string Ping = "peer write race ping";

    /// <summary>The staff name the mute facts run as: the same name and roster content <c>CommandTableTests</c>
    /// writes, because <c>StaffAccounts.Load</c> replaces the roster wholesale.</summary>
    private const string GmName = "cmdgm";

    private const string MutedPrefix = "You are muted and cannot speak";
    private const string UnmutedLine = "You are no longer muted.";

    private readonly SessionFixture _fx;
    private static int _serial;
    private static bool _rosterWritten;

    private static readonly MethodInfo ApplyCast =
        typeof(Session).GetMethod("ApplyCast", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo BuffsField =
        typeof(Session).GetField("_buffs", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public PeerWritesLockTests(SessionFixture fx)
    {
        _fx = fx;
        EnsureGmRoster();
    }

    public static TheoryData<string> ValorFamily => new()
    {
        "valor_poet", "strengthen_poet", "bless_muscles_poet", "power_burst_poet",
        "valor_mage", "strengthen_mage", "bless_muscles_mage", "power_burst_mage",
    };

    public static TheoryData<string> SanctuaryFamily => new()
    {
        "sanctuary_poet", "protect_soul_poet", "guard_life_poet", "magic_shield_poet",
        "sanctuary_mage", "protect_soul_mage", "guard_life_mage", "magic_shield_mage",
    };

    /// <summary>Mana Gift (Inspire and its three reskins) and Mana Steal (Draw Energy and its three).</summary>
    public static TheoryData<string> ManaTransfers => new()
    {
        "inspire_poet", "share_energy_poet", "bestow_power_poet", "release_focus_poet",
        "draw_energy_poet", "harness_power_poet", "combine_focus_poet", "inspiration_poet",
    };

    // ===== 1. the Valor family ========================================================================

    /// <summary>Each of the eight, cast at another player through the real frame, lands its timed buff on that
    /// player (the spell's key in the <c>mights</c> slot, five minutes), takes its 30 mana, says "You cast X." to
    /// the caster and the target's line to the target. Red on 93b079c in Debug: the <c>_buffs</c> guard fires inside
    /// the verb, the caster reads "X isn't working right now." with the mana spent, and nothing lands.</summary>
    [Theory]
    [MemberData(nameof(ValorFamily))]
    public void AValorFamilyCastAtAnotherPlayerLandsTheirBuff(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (v, vOut, _) = Plain("ValorV", ValorMap, 20, 20);
        var (c, cOut, cc) = Poet(ValorMap, 22, 20, new[] { sp });
        try
        {
            c.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));

            var cLines = SpellCastSupport.MiniTexts(cOut);
            Assert.DoesNotContain($"{sp.Name} isn't working right now.", cLines);
            Assert.Contains($"You cast {sp.Name}.", cLines);
            Assert.Equal(Mana - (uint)Content.FxFor(sp)!.Mana, cc.Mp);
            AssertValorLanded(v, sp);
            Assert.Contains(TargetLine(sp, c), SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            Leave(ValorMap, v, c);
        }
    }

    /// <summary>The reproduction for both builds: a Valor at another player waits for that player's monitor, in both
    /// <c>StateRank</c> orders, and lands only once the third thread lets go, with the same mana and lines as any
    /// cast. Red on 93b079c in Release: the buff is written while the third thread holds the monitor (the cast then
    /// blocks on <c>TellTarget</c>'s own, correct, entry into the target). Red there in Debug as in the fact above:
    /// the cast finishes, refused, while the monitor is held.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AValorAtAnotherPlayerWaitsForThatPlayersMonitor(bool casterRanksFirst)
    {
        var sp = Content.SpellByKey("valor_poet")!;
        Session c, v;
        RecordingOutbound cOut, vOut;
        Character cc;
        if (casterRanksFirst)
        {
            (c, cOut, cc) = Poet(ValorHeldMap, 30, 30, new[] { sp });
            (v, vOut, _) = Plain("ValorHeldV", ValorHeldMap, 32, 30);
        }
        else
        {
            (v, vOut, _) = Plain("ValorHeldV", ValorHeldMap, 32, 30);
            (c, cOut, cc) = Poet(ValorHeldMap, 30, 30, new[] { sp });
        }
        Assert.Equal(casterRanksFirst, c.StateRank < v.StateRank);

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, v, () => c.Receive(SpellCastSupport.CastFrame(0, v.PlayerId)),
                                    untouched: () => SpellCastSupport.BuffEntry(v, sp.Key) is null);
            AssertWaited(run, "the Valor", "the target's buff was written while another thread held its monitor");

            var cLines = SpellCastSupport.MiniTexts(cOut);
            Assert.DoesNotContain($"{sp.Name} isn't working right now.", cLines);
            Assert.Contains($"You cast {sp.Name}.", cLines);
            Assert.Equal(Mana - (uint)Content.FxFor(sp)!.Mana, cc.Mp);
            AssertValorLanded(v, sp);
            Assert.Contains(TargetLine(sp, c), SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            if (run.Settled) Leave(ValorHeldMap, v, c);
        }
    }

    /// <summary>Two Poets cast Valor at each other, then Dispell at each other, again and again, while each sends
    /// the other a party line, steps at the other's tile and the world ticks. Each Valor and each Dispell writes the
    /// other player's buff list inside that player's monitor, taken by <c>StateRank</c> from inside the caster's
    /// own, so one direction ascends and the other descends; each party line enters the other's monitor from inside
    /// the sender's, outside Lua, so the gate does not keep it apart from the other's cast; each step takes the
    /// stepper's monitor and then <c>World._lock</c>. Nothing may stall (<see cref="StallWatch"/>). Every Valor
    /// either lands (its target is told) or is refused because the slot still holds the last one (the Dispell
    /// before it lost its roll); no write lands in a Poet's own list while that Poet holds its monitor for a fifth
    /// of a millisecond; each list ends whole.
    /// <para>No buff in this race expires: <c>HasStatusCategory</c>, which the verb runs on the TARGET's list from
    /// the caster's thread before it writes, reads without the target's monitor (a read; listed in the report), and
    /// only the Lua gate keeps it apart from the casts' writes, not from the tick's expiry.</para>
    /// <para>Red on 93b079c in both builds. Debug: the guard fires inside every Valor at the other. Release: a
    /// write lands inside the other Poet's own section, or that Poet's walk of its own list throws "Collection was
    /// modified".</para>
    /// <para>It stalls with the write taken under <c>World._lock</c> (one Poet holds the lock and waits for the
    /// other's monitor while the other, mid-step, holds its monitor and waits for the lock), and with a plain
    /// <c>lock</c> on the other's monitor instead of <c>EnterState</c> (the lower-ranked Poet's party line holds its
    /// own monitor and waits for the other's, while the other, mid-cast, holds its own and waits for the lower-ranked
    /// one's).</para></summary>
    [Fact]
    public void TwoPlayersCastingValorAtEachOtherNeverStall()
    {
        var valor = Content.SpellByKey("valor_poet")!;
        var dispell = Content.SpellByKey("dispell_poet")!;
        var (a, aOut, _) = Poet(ValorRaceMap, 10, 10, new[] { valor, dispell }, mp: 10_000_000, will: 255);
        var (b, bOut, _) = Poet(ValorRaceMap, 11, 10, new[] { valor, dispell }, mp: 10_000_000, will: 255);
        var race = new Race();
        int torn = 0, landedA = 0, landedB = 0;

        void Caster(Session self, Session other, Action landed)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() =>
                {
                    string before = BuffFingerprint(self);
                    Dwell();   // the rest of a handler's work, still inside this Poet's monitor
                    if (BuffFingerprint(self) != before) Interlocked.Increment(ref torn);
                });
                bool ok = false;
                self.WithState(() => ok = Cast(self, valor, other.PlayerId));
                if (ok) landed();
                self.WithState(() => Cast(self, dispell, other.PlayerId));
                self.WithState(() => other.NotifyGroup(Ping));
                Step(self, other, ValorRaceMap);
                race.Progress.Bump();
            }
        }

        try
        {
            RunRace(race, "the two Poets casting Valor at each other", tick: true,
                    () => Caster(a, b, () => landedA++), () => Caster(b, a, () => landedB++));

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            var aLines = SpellCastSupport.MiniTexts(aOut);
            var bLines = SpellCastSupport.MiniTexts(bOut);
            Assert.DoesNotContain("Valor isn't working right now.", aLines);
            Assert.DoesNotContain("Valor isn't working right now.", bLines);
            Assert.NotEqual(TargetLine(valor, a), $"{a.CharName} casts Dispell on you.");
            int toldA = bLines.Count(l => l == TargetLine(valor, a)), toldB = aLines.Count(l => l == TargetLine(valor, b));
            int refusedA = aLines.Count(l => l == "You already cast that spell.");
            int refusedB = bLines.Count(l => l == "You already cast that spell.");
            Assert.Equal((landedA, landedB), (toldA, toldB));
            Assert.Equal((Rounds, Rounds), (toldA + refusedA, toldB + refusedB));
            Assert.True(toldA > 0 && toldB > 0, $"no Valor landed ({toldA}, {toldB})");
            int wonA = bLines.Count(l => l == $"{a.CharName} casts Dispell on you."), lostA = aLines.Count(l => l == "Something went wrong.");
            int wonB = aLines.Count(l => l == $"{b.CharName} casts Dispell on you."), lostB = bLines.Count(l => l == "Something went wrong.");
            Assert.Equal((Rounds, Rounds), (wonA + lostA, wonB + lostB));
            Assert.Equal((Rounds, Rounds), (aLines.Count(l => l == Ping), bLines.Count(l => l == Ping)));
            AssertBuffListWhole(a);
            AssertBuffListWhole(b);
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(ValorRaceMap, a, b);
        }
    }

#if DEBUG
    /// <summary>The write's entry is the ordered one, asserted: giving another player a timed buff with
    /// <c>World._lock</c> held fires the lock-order assert before anything lands; from a thread holding a
    /// lower-ranked player's monitor (the plain branch) and a higher-ranked one's (the branch that drops it while it
    /// waits) it is silent and lands. Debug-only by construction, like the asserts it pins.
    /// <para>Red without the fix: with no monitor the <c>_buffs</c> guard fires on every entry, the legal ones too;
    /// with a plain <c>lock</c> nothing fires under <c>World._lock</c>.</para></summary>
    [Fact]
    public void GivingAnotherPlayerATimedBuffUnderTheWorldLockAsserts()
    {
        var (low, _, _) = Plain("ValorPinLow", ValorPinMap, 60, 60);
        var (v, _, _) = Plain("ValorPinV", ValorPinMap, 62, 60);
        var (high, _, _) = Plain("ValorPinHigh", ValorPinMap, 64, 60);
        var might = new[] { "might" };
        var three = new[] { "3" };
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);

            var wrongWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(
                () => v.ReceiveTimedBuff(might, three, 600_000, "pin_valor_a", "Pin Valor", "mights")));
            AssertLockOrderFired(wrongWay);
            Assert.Null(SpellCastSupport.BuffEntry(v, "pin_valor_a"));

            Assert.Null(Record.Exception(() => low.WithState(
                () => v.ReceiveTimedBuff(might, three, 600_000, "pin_valor_a", "Pin Valor", "mights"))));
            Assert.NotNull(SpellCastSupport.BuffEntry(v, "pin_valor_a"));
            Assert.Null(Record.Exception(() => high.WithState(
                () => v.ReceiveTimedBuff(might, three, 600_000, "pin_valor_b", "Pin Valor", "mights"))));
            Assert.NotNull(SpellCastSupport.BuffEntry(v, "pin_valor_b"));
        }
        finally
        {
            Leave(ValorPinMap, low, v, high);
        }
    }
#endif

    // ===== 2. the Sanctuary family ====================================================================

    /// <summary>Each of the eight, cast at another player through the real frame, gives that player the half-damage
    /// deduction under the spell's name for five minutes, takes its 60 mana, and says "You cast X." and the target's
    /// line. Not a guard (no guard covers these fields, so this is green on 93b079c in both builds): it pins what a
    /// successful cast does, before and after.</summary>
    [Theory]
    [MemberData(nameof(SanctuaryFamily))]
    public void ASanctuaryFamilyCastAtAnotherPlayerGivesThemTheDeduction(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (v, vOut, _) = Plain("SancV", SancMap, 20, 20);
        var (c, cOut, cc) = Poet(SancMap, 22, 20, new[] { sp });
        try
        {
            c.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));

            var cLines = SpellCastSupport.MiniTexts(cOut);
            Assert.DoesNotContain($"{sp.Name} isn't working right now.", cLines);
            Assert.Contains($"You cast {sp.Name}.", cLines);
            Assert.Equal(Mana - (uint)Content.FxFor(sp)!.Mana, cc.Mp);
            AssertDeduction(v, sp.Name);
            Assert.Contains(TargetLine(sp, c), SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            Leave(SancMap, v, c);
        }
    }

    /// <summary>The reproduction for both builds: a Sanctuary at another player waits for that player's monitor, in
    /// both orders. Red on 93b079c in Debug and Release alike: the deduction is written while the third thread holds
    /// the monitor.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASanctuaryAtAnotherPlayerWaitsForThatPlayersMonitor(bool casterRanksFirst)
    {
        var sp = Content.SpellByKey("sanctuary_poet")!;
        Session c, v;
        RecordingOutbound cOut, vOut;
        Character cc;
        if (casterRanksFirst)
        {
            (c, cOut, cc) = Poet(SancHeldMap, 30, 30, new[] { sp });
            (v, vOut, _) = Plain("SancHeldV", SancHeldMap, 32, 30);
        }
        else
        {
            (v, vOut, _) = Plain("SancHeldV", SancHeldMap, 32, 30);
            (c, cOut, cc) = Poet(SancHeldMap, 30, 30, new[] { sp });
        }
        Assert.Equal(casterRanksFirst, c.StateRank < v.StateRank);

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, v, () => c.Receive(SpellCastSupport.CastFrame(0, v.PlayerId)),
                                    untouched: () => !v.SancDeductActive);
            AssertWaited(run, "the Sanctuary", "the target's deduction was written while another thread held its monitor");

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(cOut));
            Assert.Equal(Mana - (uint)Content.FxFor(sp)!.Mana, cc.Mp);
            AssertDeduction(v, sp.Name);
            Assert.Contains(TargetLine(sp, c), SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            if (run.Settled) Leave(SancHeldMap, v, c);
        }
    }

    /// <summary>Two Poets cast Sanctuary and Protect Soul at each other in turn (so each landing changes the
    /// target's deduction name as well as its deadline) while each sends the other a party line, steps at the other's
    /// tile and the world ticks. Nothing may stall; every cast lands (the family has no slot, so a recast refreshes)
    /// and its target is told; no write lands in a Poet's deduction while it holds its own monitor.
    /// <para>Red on 93b079c in both builds: the writes land inside the other Poet's own section. It stalls with the
    /// write under <c>World._lock</c> or under a plain <c>lock</c>, the two cycles the Valor race names.</para></summary>
    [Fact]
    public void TwoPlayersCastingSanctuaryAtEachOtherNeverStall()
    {
        var sanctuary = Content.SpellByKey("sanctuary_poet")!;
        var soul = Content.SpellByKey("protect_soul_poet")!;
        var (a, aOut, _) = Poet(SancRaceMap, 10, 10, new[] { sanctuary, soul }, mp: 10_000_000);
        var (b, bOut, _) = Poet(SancRaceMap, 11, 10, new[] { sanctuary, soul }, mp: 10_000_000);
        var race = new Race();
        int torn = 0, castsA = 0, castsB = 0;

        void Caster(Session self, Session other, Action cast)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() =>
                {
                    var before = (self.SancDeductUntil, self.SancDeductName);
                    Dwell();
                    if ((self.SancDeductUntil, self.SancDeductName) != before) Interlocked.Increment(ref torn);
                });
                var sp = i % 2 == 0 ? sanctuary : soul;
                bool ok = false;
                self.WithState(() => ok = Cast(self, sp, other.PlayerId));
                if (ok) cast();
                self.WithState(() => other.NotifyGroup(Ping));
                Step(self, other, SancRaceMap);
                race.Progress.Bump();
            }
        }

        try
        {
            RunRace(race, "the two Poets casting Sanctuary at each other", tick: true,
                    () => Caster(a, b, () => castsA++), () => Caster(b, a, () => castsB++));

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            Assert.Equal((Rounds, Rounds), (castsA, castsB));
            var aLines = SpellCastSupport.MiniTexts(aOut);
            var bLines = SpellCastSupport.MiniTexts(bOut);
            int toldA = bLines.Count(l => l == TargetLine(sanctuary, a) || l == TargetLine(soul, a));
            int toldB = aLines.Count(l => l == TargetLine(sanctuary, b) || l == TargetLine(soul, b));
            Assert.Equal((Rounds, Rounds), (toldA, toldB));
            Assert.Equal((Rounds, Rounds), (aLines.Count(l => l == Ping), bLines.Count(l => l == Ping)));
            AssertDeduction(a, soul.Name);   // the last of an even number of rounds is Protect Soul
            AssertDeduction(b, soul.Name);
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(SancRaceMap, a, b);
        }
    }

#if DEBUG
    /// <summary>The deduction's entry is the ordered one, asserted: with <c>World._lock</c> held the lock-order assert
    /// fires and nothing is written; from a lower- and a higher-ranked player's monitor it is silent and writes.
    /// Debug-only by construction.
    /// <para>Red without the fix: with no monitor (and no guard on these fields) nothing fires under
    /// <c>World._lock</c> and the deduction is written; with a plain <c>lock</c> nothing fires either.</para></summary>
    [Fact]
    public void GivingAnotherPlayerADeductionUnderTheWorldLockAsserts()
    {
        var (low, _, _) = Plain("SancPinLow", SancPinMap, 60, 60);
        var (v, _, _) = Plain("SancPinV", SancPinMap, 62, 60);
        var (high, _, _) = Plain("SancPinHigh", SancPinMap, 64, 60);
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);

            var wrongWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(
                () => v.ApplySanctuaryDeduction(0.5, 600_000, "Pin Sanctuary")));
            AssertLockOrderFired(wrongWay);
            Assert.False(v.SancDeductActive);

            Assert.Null(Record.Exception(() => low.WithState(() => v.ApplySanctuaryDeduction(0.5, 600_000, "Pin Low"))));
            AssertDeduction(v, "Pin Low");
            Assert.Null(Record.Exception(() => high.WithState(() => v.ApplySanctuaryDeduction(0.5, 600_000, "Pin High"))));
            AssertDeduction(v, "Pin High");
        }
        finally
        {
            Leave(SancPinMap, low, v, high);
        }
    }
#endif

    // ===== 3. Mana Gift and Mana Steal ================================================================

    /// <summary>Each of the eight at another player through the real frame. A gift tops the target up from the
    /// caster's own pool (the caster's 10000 against the target's missing 4000: caster 6000, target 5000); a steal,
    /// cast at a party member, takes all of the target's mana into the caster's (1000 + 3000, target 0). "You cast X."
    /// to the caster, the target's line to the target. Not a guard (no guard covers <c>Mp</c>; green on 93b079c in
    /// both builds): it pins what a successful cast does, before and after.</summary>
    [Theory]
    [MemberData(nameof(ManaTransfers))]
    public void AManaGiftOrStealAtAnotherPlayerMovesTheirMana(string key)
    {
        var sp = Content.SpellByKey(key)!;
        bool steal = Content.IsManaStealSpell(sp);
        var (v, vOut, vc) = Plain("ManaV", ManaMap, 20, 20, ch => { ch.MaxMp = 5_000; ch.Mp = steal ? 3_000u : 1_000u; ch.Grouped = true; });
        var (c, cOut, cc) = Poet(ManaMap, 22, 20, new[] { sp }, shape: ch => { ch.Mp = steal ? 1_000u : Mana; ch.Grouped = true; });
        try
        {
            if (steal) { SessionFixture.FormParty(c, v); cOut.Clear(); vOut.Clear(); }

            c.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));

            var cLines = SpellCastSupport.MiniTexts(cOut);
            Assert.DoesNotContain($"{sp.Name} isn't working right now.", cLines);
            Assert.Contains($"You cast {sp.Name}.", cLines);
            Assert.Equal(steal ? (4_000u, 0u) : (6_000u, 5_000u), (cc.Mp, vc.Mp));
            Assert.Contains(TargetLine(sp, c), SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            Leave(ManaMap, v, c);
        }
    }

    /// <summary>The reproduction for both builds: a Mana Gift and a Mana Steal at another player wait for that
    /// player's monitor, in both orders, and move the same mana once it is free. Red on 93b079c in Debug and Release
    /// alike: the target's mana is written while the third thread holds the monitor.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void AManaGiftOrStealAtAnotherPlayerWaitsForThatPlayersMonitor(bool steal, bool casterRanksFirst)
    {
        var sp = Content.SpellByKey(steal ? "draw_energy_poet" : "inspire_poet")!;
        Session c, v;
        RecordingOutbound cOut, vOut;
        Character cc, vc;
        Action<Character> target = ch => { ch.MaxMp = 5_000; ch.Mp = steal ? 3_000u : 1_000u; ch.Grouped = true; };
        Action<Character> caster = ch => { ch.Mp = steal ? 1_000u : Mana; ch.Grouped = true; };
        if (casterRanksFirst)
        {
            (c, cOut, cc) = Poet(ManaHeldMap, 30, 30, new[] { sp }, shape: caster);
            (v, vOut, vc) = Plain("ManaHeldV", ManaHeldMap, 32, 30, target);
        }
        else
        {
            (v, vOut, vc) = Plain("ManaHeldV", ManaHeldMap, 32, 30, target);
            (c, cOut, cc) = Poet(ManaHeldMap, 30, 30, new[] { sp }, shape: caster);
        }
        Assert.Equal(casterRanksFirst, c.StateRank < v.StateRank);

        var run = new HeldRun();
        try
        {
            if (steal) { SessionFixture.FormParty(c, v); cOut.Clear(); vOut.Clear(); }
            uint before = vc.Mp;

            WhileAnotherThreadHolds(run, v, () => c.Receive(SpellCastSupport.CastFrame(0, v.PlayerId)),
                                    untouched: () => vc.Mp == before);
            AssertWaited(run, sp.Name, "the target's mana was written while another thread held its monitor");

            Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(cOut));
            Assert.Equal(steal ? (4_000u, 0u) : (6_000u, 5_000u), (cc.Mp, vc.Mp));
            Assert.Contains(TargetLine(sp, c), SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            if (run.Settled) Leave(ManaHeldMap, v, c);
        }
    }

    /// <summary>Two Poets in one party give each other mana and steal it back, again and again, while each sends the
    /// other a party line, steps at the other's tile and the world ticks. Nothing may stall; every gift lands (its
    /// target is told) or is refused for want of mana, and every steal lands; no write lands in a Poet's mana while
    /// it holds its own monitor.
    /// <para>Red on 93b079c in both builds: the writes land inside the other Poet's own section. It stalls with the
    /// write under <c>World._lock</c> or under a plain <c>lock</c>, the two cycles the Valor race names.</para></summary>
    [Fact]
    public void TwoPlayersGivingAndStealingEachOthersManaNeverStall()
    {
        var gift = Content.SpellByKey("inspire_poet")!;
        var steal = Content.SpellByKey("draw_energy_poet")!;
        Action<Character> pool = ch => { ch.Mp = 5_000_000; ch.Grouped = true; };
        var (a, aOut, ac) = Poet(ManaRaceMap, 10, 10, new[] { gift, steal }, mp: 10_000_000, shape: pool);
        var (b, bOut, bc) = Poet(ManaRaceMap, 11, 10, new[] { gift, steal }, mp: 10_000_000, shape: pool);
        var race = new Race();
        int torn = 0, steals = 0;

        void Caster(Session self, Character mine, Session other)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() =>
                {
                    uint before = mine.Mp;
                    Dwell();
                    if (mine.Mp != before) Interlocked.Increment(ref torn);
                });
                self.WithState(() => Cast(self, gift, other.PlayerId));
                bool ok = false;
                self.WithState(() => ok = Cast(self, steal, other.PlayerId));
                if (ok) Interlocked.Increment(ref steals);
                self.WithState(() => other.NotifyGroup(Ping));
                Step(self, other, ManaRaceMap);
                race.Progress.Bump();
            }
        }

        try
        {
            SessionFixture.FormParty(a, b);
            aOut.Clear(); bOut.Clear();

            RunRace(race, "the two Poets moving each other's mana", tick: true,
                    () => Caster(a, ac, b), () => Caster(b, bc, a));

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            Assert.Equal(2 * Rounds, steals);
            var aLines = SpellCastSupport.MiniTexts(aOut);
            var bLines = SpellCastSupport.MiniTexts(bOut);
            Assert.NotEqual(TargetLine(gift, a), TargetLine(steal, a));
            int giftsA = bLines.Count(l => l == TargetLine(gift, a)), giftsB = aLines.Count(l => l == TargetLine(gift, b));
            int refusedA = aLines.Count(l => l == "Not enough mana."), refusedB = bLines.Count(l => l == "Not enough mana.");
            Assert.Equal((Rounds, Rounds), (giftsA + refusedA, giftsB + refusedB));
            Assert.True(giftsA > 0 && giftsB > 0, $"no gift landed ({giftsA}, {giftsB})");
            Assert.Equal((Rounds, Rounds), (bLines.Count(l => l == TargetLine(steal, a)), aLines.Count(l => l == TargetLine(steal, b))));
            Assert.Equal((Rounds, Rounds), (aLines.Count(l => l == Ping), bLines.Count(l => l == Ping)));
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(ManaRaceMap, a, b);
        }
    }

#if DEBUG
    /// <summary>The mana write's entry is the ordered one, asserted: with <c>World._lock</c> held the lock-order assert
    /// fires and the mana is unchanged; from a lower- and a higher-ranked player's monitor it is silent and writes.
    /// Debug-only by construction, and it names <c>ReceiveSetMana</c>, so it came with the fix.
    /// <para>Red without the fix: with no monitor (and no guard on <c>Mp</c>) nothing fires under <c>World._lock</c>
    /// and the mana is written; with a plain <c>lock</c> nothing fires either.</para></summary>
    [Fact]
    public void SettingAnotherPlayersManaUnderTheWorldLockAsserts()
    {
        var (low, _, _) = Plain("ManaPinLow", ManaPinMap, 60, 60);
        var (v, _, vc) = Plain("ManaPinV", ManaPinMap, 62, 60, ch => { ch.MaxMp = 5_000; ch.Mp = 1_000; });
        var (high, _, _) = Plain("ManaPinHigh", ManaPinMap, 64, 60);
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);

            var wrongWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(() => v.ReceiveSetMana(4_000)));
            AssertLockOrderFired(wrongWay);
            Assert.Equal(1_000u, vc.Mp);

            Assert.Null(Record.Exception(() => low.WithState(() => v.ReceiveSetMana(4_000))));
            Assert.Equal(4_000u, vc.Mp);
            Assert.Null(Record.Exception(() => high.WithState(() => v.ReceiveSetMana(0))));
            Assert.Equal(0u, vc.Mp);
        }
        finally
        {
            Leave(ManaPinMap, low, v, high);
        }
    }
#endif

    // ===== 4. breaking off an engagement, and a divorce ===============================================

    /// <summary>At the Chapel, through the real dialog: Break Off Engagement and Divorce clear the clicking player
    /// AND their partner when the partner is online. The engagement: both lose the "engaged" legend and the
    /// fiancé, the proposee flag and the ceremony timer; the clicker pays <c>MaxMp * 1000</c> exp and reads "It is
    /// done.". The divorce: both lose the "married" legend, the spouse and one love keepsake; the clicker pays
    /// <c>MaxHp * 2550</c> exp and reads "You are now divorced.". The partner's saved row says the same.
    /// <para>Red on 93b079c in Debug: the partner's first save (<c>SaveChar</c>) fires the <c>_char</c> guard inside
    /// the dialog, the conversation is abandoned without its last line, and the partner is left half-cleared.</para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChapelBreakOrDivorceClearsTheOnlinePartnerToo(bool divorce)
    {
        var (npc, def) = ChapelNpc();
        var (a, aOut, ac) = Partner("VowA", def.Map, (ushort)def.X, (ushort)(def.Y + 2));
        var (b, bOut, _) = Partner("VowB", VowMap, 20, 20);
        try
        {
            Vow(a, b, divorce);
            Vow(b, a, divorce);
            uint exp = ac.Exp;

            OnOwnThread(() =>
            {
                OpenVowEntry(a, aOut, npc, def, divorce);
                a.Receive(MenuFrame(1));
                a.Receive(NextFrame());
            });

            Assert.False(a.DialogBusy);
            Assert.Equal(1, Shown(aOut, divorce ? "You are now divorced." : "It is done."));
            Assert.Equal(exp - (divorce ? 1_000u * 2550 : 100u * 1000), ac.Exp);
            a.WithState(() => Assert.True(Unvowed(a, divorce), "the clicker was not cleared"));
            b.WithState(() => Assert.True(Unvowed(b, divorce), "the partner was not cleared"));
            var saved = _fx.Store.Load(b.CharName).Character!;
            Assert.DoesNotContain(saved.Legends, l => l.Name == (divorce ? "married" : "engaged"));
            Assert.Equal("", divorce ? saved.Spouse : saved.Fiance);
        }
        finally
        {
            Leave(def.Map, a);
            Leave(VowMap, b);
        }
    }

    /// <summary>Today's offline path, kept: when the partner is not online the Chapel clears the clicking player only,
    /// and the partner's saved row is left byte for byte as it was. (<c>FindPlayer</c> finds nobody, so nothing of the
    /// partner's is touched; their legend, fiancé or spouse and keepsake stay until their next login ends them, which
    /// <see cref="VowHalvesTests"/> pins.) Not a guard: green on 93b079c and after, in both builds.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChapelBreakOrDivorceWithThePartnerOfflineLeavesTheirRowAsItWas(bool divorce)
    {
        var (npc, def) = ChapelNpc();
        var (a, aOut, _) = Partner("VowOffA", def.Map, (ushort)def.X, (ushort)(def.Y + 2));
        var (b, _, _) = Partner("VowOffB", VowMap, 24, 20);
        try
        {
            Vow(a, b, divorce);
            Vow(b, a, divorce);
            _fx.World.LeaveMap(b, VowMap);   // the partner logs off: no map holds them, so FindPlayer finds nobody
            Assert.Null(_fx.World.Online.FindPlayer(b.CharName));
            string row = RowJson(b.CharName);
            Assert.Contains(divorce ? "\"married\"" : "\"engaged\"", row);

            OnOwnThread(() =>
            {
                OpenVowEntry(a, aOut, npc, def, divorce);
                a.Receive(MenuFrame(1));
                a.Receive(NextFrame());
            });

            Assert.Equal(1, Shown(aOut, divorce ? "You are now divorced." : "It is done."));
            a.WithState(() => Assert.True(Unvowed(a, divorce), "the clicker was not cleared"));
            Assert.Equal(row, RowJson(b.CharName));
        }
        finally
        {
            Leave(def.Map, a);
        }
    }

    /// <summary>The reproduction for both builds: the partner's half of a Break Off Engagement or a Divorce waits for
    /// the partner's monitor, in both orders, and clears them once it is free; the dialog then closes on its usual
    /// line. Red on 93b079c in Release: the partner's legend is removed while the third thread holds their monitor
    /// (the dialog then blocks in the partner's own save, which takes the monitor). Red there in Debug: the guard
    /// fires in that save and the dialog is abandoned, with the legend already gone.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void AChapelBreakOrDivorceWaitsForThePartnersMonitor(bool divorce, bool clickerRanksFirst)
    {
        var (npc, def) = ChapelNpc();
        Session a, b;
        RecordingOutbound aOut;
        if (clickerRanksFirst)
        {
            (a, aOut, _) = Partner("VowHeldA", def.Map, (ushort)(def.X + 2), (ushort)(def.Y + 2));
            (b, _, _) = Partner("VowHeldB", VowHeldMap, 20, 20);
        }
        else
        {
            (b, _, _) = Partner("VowHeldB", VowHeldMap, 20, 20);
            (a, aOut, _) = Partner("VowHeldA", def.Map, (ushort)(def.X + 2), (ushort)(def.Y + 2));
        }
        Assert.Equal(clickerRanksFirst, a.StateRank < b.StateRank);

        var run = new HeldRun();
        try
        {
            Vow(a, b, divorce);
            Vow(b, a, divorce);
            OnOwnThread(() => OpenVowEntry(a, aOut, npc, def, divorce));

            WhileAnotherThreadHolds(run, b, () => a.Receive(MenuFrame(1)),
                                    untouched: () => Vowed(b, a.CharName, divorce));
            AssertWaited(run, divorce ? "the divorce" : "the broken engagement",
                         "the partner was written while another thread held their monitor");

            OnOwnThread(() => a.Receive(NextFrame()));
            Assert.False(a.DialogBusy);
            Assert.Equal(1, Shown(aOut, divorce ? "You are now divorced." : "It is done."));
            a.WithState(() => Assert.True(Unvowed(a, divorce), "the clicker was not cleared"));
            b.WithState(() => Assert.True(Unvowed(b, divorce), "the partner was not cleared"));
        }
        finally
        {
            if (run.Settled)
            {
                Leave(def.Map, a);
                Leave(VowHeldMap, b);
            }
        }
    }

    /// <summary>Two partners end their engagement (or their marriage) at the same moment, again and again: each
    /// renews the vow on its own side, holds its monitor a fifth of a millisecond, checks the vow is still there, then
    /// runs the Chapel's break (or divorce) under its own monitor as the dialog does, which clears its own side and
    /// then the partner's inside the partner's monitor; then steps at the other's tile while the world ticks. The
    /// action runs both ways at once, so one partner's write ascends and the other's descends. Nothing may stall;
    /// nothing written from outside lands inside a partner's own section; both end with no vow, and (the divorce) no
    /// keepsake, since each round gives one and takes what is there.
    /// <para>Red on 93b079c in both builds. Debug: the guard fires in the partner's first save. Release: the
    /// partner's break lands inside the other's own section.</para>
    /// <para>It stalls at once with the partner's half under <c>World._lock</c> (one partner holds the lock and waits
    /// for the other's monitor while the other holds its own and waits for the lock) and with a plain <c>lock</c> on
    /// the partner's monitor (each holds its own and waits for the other's).</para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoPartnersEndingTheirVowsAtOnceNeverStall(bool divorce)
    {
        var (a, _, _) = Partner("VowRaceA", VowRaceMap, 10, 10);
        var (b, _, _) = Partner("VowRaceB", VowRaceMap, 11, 10);
        var race = new Race();
        int torn = 0;

        void Partnered(Session self, Session other)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() =>
                {
                    Vow(self, other, divorce);
                    Dwell();
                    if (!Vowed(self, other.CharName, divorce)) Interlocked.Increment(ref torn);
                });
                self.WithState(() => { if (divorce) self.FinishDivorce(); else self.BreakOffEngagement(); });
                Step(self, other, VowRaceMap);
                race.Progress.Bump();
            }
        }

        try
        {
            RunRace(race, divorce ? "the two spouses divorcing each other" : "the two partners breaking it off",
                    tick: true, () => Partnered(a, b), () => Partnered(b, a));

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            a.WithState(() => Assert.True(Unvowed(a, divorce), "the first partner ended with a vow"));
            b.WithState(() => Assert.True(Unvowed(b, divorce), "the second partner ended with a vow"));
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(VowRaceMap, a, b);
        }
    }

#if DEBUG
    /// <summary>A partner's half of a broken engagement or a divorce is entered the ordered way, asserted: with
    /// <c>World._lock</c> held the lock-order assert fires before anything is cleared; from a lower- and a
    /// higher-ranked player's monitor it is silent and clears. Debug-only by construction, and it names
    /// <c>EndEngagement</c> and <c>EndMarriage</c>, so it came with the fix.
    /// <para>Red without the fix: with no monitor <c>SaveChar</c>'s <c>_char</c> guard fires on every entry, the legal
    /// ones too; with a plain <c>lock</c> the legend is already gone when the first save's own entry asserts.</para></summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndingAPartnersVowUnderTheWorldLockAsserts(bool divorce)
    {
        var (low, _, _) = Partner("VowPinLow", VowPinMap, 60, 60);
        var (v, _, _) = Partner("VowPinV", VowPinMap, 62, 60);
        var (high, _, _) = Partner("VowPinHigh", VowPinMap, 64, 60);
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);

            Vow(v, low, divorce);
            var wrongWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(() => EndVow(v, low.CharName, divorce)));
            AssertLockOrderFired(wrongWay);
            Assert.True(Vowed(v, low.CharName, divorce), "the vow was cleared under the world lock");

            Assert.Null(Record.Exception(() => low.WithState(() => EndVow(v, low.CharName, divorce))));
            v.WithState(() => Assert.True(Unvowed(v, divorce)));
            Vow(v, high, divorce);
            Assert.Null(Record.Exception(() => high.WithState(() => EndVow(v, high.CharName, divorce))));
            v.WithState(() => Assert.True(Unvowed(v, divorce)));
        }
        finally
        {
            Leave(VowPinMap, low, v, high);
        }
    }

    /// <summary>End <paramref name="s"/>'s half of a vow to <paramref name="with"/>: the half names the partner
    /// it ends with (the name check added for the PR #341 review, F1).</summary>
    private static void EndVow(Session s, string with, bool married)
    {
        if (married) s.EndMarriage(with);
        else s.EndEngagement(with);
    }
#endif

    // ===== 5. @mute and @unmute ========================================================================

    /// <summary>The GM's commands reach an online player through the real chat line: "@mute" mutes them at once and
    /// tells them how long and why, and their next line is refused; "@unmute" lifts it and says so; the GM is told
    /// both times. Not a guard (no guard covers the mute fields; green on 93b079c in both builds): it pins the lines
    /// both sides read, before and after.</summary>
    [Fact]
    public void TheMuteCommandsReachAnOnlinePlayerAndTellBothSides()
    {
        var (p, pOut, pc) = Plain("MuteP", MuteMap, 20, 20);
        SaveRow(pc);
        var (gm, gmOut, _) = Gm(MuteMap, 22, 20);
        try
        {
            Run(gm, $"@mute {p.CharName} 30 cool off");
            Assert.True(p.IsMuted());
            Assert.Contains(SpeechLines(pOut), l => l.Contains($"{MutedPrefix} (") && l.Contains("remaining): cool off"));
            Assert.Contains(SpeechLines(gmOut), l => l.Contains($"Muted {p.CharName} (") && l.Contains("): cool off"));

            pOut.Clear();
            p.Receive(ChatFrame("hello"));
            Assert.DoesNotContain(SpeechLines(pOut), l => l.Contains($"{p.CharName}: hello"));
            Assert.Contains(SpeechLines(pOut), l => l.Contains(MutedPrefix));

            pOut.Clear(); gmOut.Clear();
            Run(gm, $"@unmute {p.CharName}");
            Assert.False(p.IsMuted());
            Assert.Contains(SpeechLines(pOut), l => l.Contains(UnmutedLine));
            Assert.Contains(SpeechLines(gmOut), l => l.Contains($"Unmuted {p.CharName}."));
        }
        finally
        {
            Leave(MuteMap, p, gm);
        }
    }

    /// <summary>The reproduction for both builds: "@mute" and "@unmute" wait for the target's monitor, in both
    /// orders, and land with the same lines once it is free. Red on 93b079c in Debug and Release alike: the command
    /// finishes, and the target's mute changes, while the third thread holds their monitor.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void AMuteCommandWaitsForTheTargetsMonitor(bool unmute, bool gmRanksFirst)
    {
        Session p, gm;
        RecordingOutbound pOut, gmOut;
        Character pc;
        if (gmRanksFirst)
        {
            (gm, gmOut, _) = Gm(MuteHeldMap, 22, 20);
            (p, pOut, pc) = Plain("MuteHeldP", MuteHeldMap, 20, 20);
        }
        else
        {
            (p, pOut, pc) = Plain("MuteHeldP", MuteHeldMap, 20, 20);
            (gm, gmOut, _) = Gm(MuteHeldMap, 22, 20);
        }
        Assert.Equal(gmRanksFirst, gm.StateRank < p.StateRank);

        var run = new HeldRun();
        try
        {
            SaveRow(pc);
            if (unmute) { Run(gm, $"@mute {p.CharName}"); pOut.Clear(); gmOut.Clear(); }
            bool before = p.IsMuted();
            Assert.Equal(unmute, before);

            WhileAnotherThreadHolds(run, p, () => Run(gm, unmute ? $"@unmute {p.CharName}" : $"@mute {p.CharName} 30 cool off"),
                                    untouched: () => p.IsMuted() == before);
            AssertWaited(run, unmute ? "the unmute" : "the mute", "the target's mute changed while another thread held their monitor");

            Assert.Equal(!unmute, p.IsMuted());
            if (unmute)
            {
                Assert.Contains(SpeechLines(pOut), l => l.Contains(UnmutedLine));
                Assert.Contains(SpeechLines(gmOut), l => l.Contains($"Unmuted {p.CharName}."));
            }
            else
            {
                Assert.Contains(SpeechLines(pOut), l => l.Contains($"{MutedPrefix} (") && l.Contains("remaining): cool off"));
                Assert.Contains(SpeechLines(gmOut), l => l.Contains($"Muted {p.CharName} ("));
            }
        }
        finally
        {
            if (run.Settled)
            {
                if (!unmute) Moderation.Unmute(p.CharName, GmName);
                Leave(MuteHeldMap, p, gm);
            }
        }
    }

    /// <summary>A GM mutes and unmutes a player again and again through the real commands while the player, inside
    /// its own monitor, checks its mute does not change under it, sends the GM a party line (a peer entry into the
    /// GM's monitor from inside its own, the other way), and steps at the GM's tile while the world ticks. The
    /// command cannot run both ways: it refuses to mute a GM (Server/Session.Moderation.cs, <c>MuteCmd</c>), so the
    /// party line is the other direction. The player is created first, so the GM's write descends and the player's
    /// line ascends. Nothing may stall; every command is told to both sides; no mute lands inside the player's own
    /// section; the player ends unmuted.
    /// <para>Red on 93b079c in both builds: the mute lands inside the player's own section. It stalls with the mute
    /// under <c>World._lock</c> (the GM holds the lock and waits for the player's monitor while the player, mid-step,
    /// holds its own and waits for the lock) and with a plain <c>lock</c> on the player's monitor (the GM holds its own
    /// and waits for the player's while the player's party line holds the player's and waits for the GM's).</para></summary>
    [Fact]
    public void AGmMutingAPlayerWhoWritesBackNeverStalls()
    {
        var (p, pOut, pc) = Plain("MuteRaceP", MuteRaceMap, 10, 10);
        var (gm, gmOut, _) = Gm(MuteRaceMap, 11, 10);
        Assert.True(p.StateRank < gm.StateRank);
        var race = new Race();
        int torn = 0;

        void Moderator()
        {
            for (int i = 0; i < Rounds; i++)
            {
                Run(gm, $"@mute {p.CharName} 30 race");
                Run(gm, $"@unmute {p.CharName}");
                race.Progress.Bump();
            }
        }

        void Muted()
        {
            for (int i = 0; i < Rounds; i++)
            {
                p.WithState(() =>
                {
                    bool before = p.IsMuted();
                    Dwell();
                    if (p.IsMuted() != before) Interlocked.Increment(ref torn);
                });
                p.WithState(() => gm.NotifyGroup(Ping));
                Step(p, gm, MuteRaceMap);
                race.Progress.Bump();
            }
        }

        try
        {
            SaveRow(pc);
            RunRace(race, "the GM muting a player who writes back", tick: true, Moderator, Muted);

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            var pLines = SpeechLines(pOut);
            var gmLines = SpeechLines(gmOut);
            Assert.Equal(Rounds, pLines.Count(l => l.Contains($"{MutedPrefix} (") && l.Contains("remaining): race")));
            Assert.Equal(Rounds, pLines.Count(l => l.Contains(UnmutedLine)));
            Assert.Equal(Rounds, gmLines.Count(l => l.Contains($"Muted {p.CharName} (")));
            Assert.Equal(Rounds, gmLines.Count(l => l.Contains($"Unmuted {p.CharName}.")));
            Assert.Equal(Rounds, SpellCastSupport.MiniTexts(gmOut).Count(l => l == Ping));
            Assert.False(p.IsMuted());
            Assert.Equal(((ushort)10, (ushort)11), (p.PlayerX, gm.PlayerX));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(MuteRaceMap, p, gm);
        }
    }

#if DEBUG
    /// <summary>The mute's entry is the ordered one, asserted: with <c>World._lock</c> held the lock-order assert fires
    /// and nothing changes; from a lower- and a higher-ranked player's monitor it is silent and lands. Debug-only by
    /// construction.
    /// <para>Red without the fix: with no monitor (and no guard on these fields) nothing fires under
    /// <c>World._lock</c> and the mute lands; with a plain <c>lock</c> nothing fires either.</para></summary>
    [Fact]
    public void MutingAnotherPlayerUnderTheWorldLockAsserts()
    {
        var (low, _, _) = Plain("MutePinLow", MutePinMap, 60, 60);
        var (v, _, _) = Plain("MutePinV", MutePinMap, 62, 60);
        var (high, _, _) = Plain("MutePinHigh", MutePinMap, 64, 60);
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);

            var wrongWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(
                () => v.ApplyMute(Moderation.Forever, "pin")));
            AssertLockOrderFired(wrongWay);
            Assert.False(v.IsMuted());

            Assert.Null(Record.Exception(() => low.WithState(() => v.ApplyMute(Moderation.Forever, "pin"))));
            Assert.True(v.IsMuted());
            Assert.Null(Record.Exception(() => high.WithState(() => v.ApplyMute(0, ""))));
            Assert.False(v.IsMuted());
        }
        finally
        {
            Leave(MutePinMap, low, v, high);
        }
    }
#endif

    // ===== the held-monitor run =======================================================================

    /// <summary>What <see cref="WhileAnotherThreadHolds"/> saw. <see cref="Settled"/> is false while either thread
    /// may still be parked on a lock: a fact must then leave its maps as they are, since cleaning up would wait on
    /// what the stuck thread holds, and the fact would hang instead of reporting.</summary>
    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a
    /// thread of its own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the target's state is as it was. Then the third thread lets go and
    /// both are joined.</summary>
    private static void WhileAnotherThreadHolds(HeldRun run, Session target, Action act, Func<bool> untouched)
    {
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            try
            {
                target.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    run.UntouchedWhileHeld = untouched();
                });
            }
            catch (Exception e) { run.HolderFault = e; }
        }) { IsBackground = true };
        var actor = new Thread(() =>
        {
            try { act(); }
            catch (Exception e) { run.ActorFault = e; }
        }) { IsBackground = true };

        run.Settled = false;
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");
            actor.Start();
            WaitUntilDoneOrBlocked(actor);
            run.FinishedWhileHeld = !actor.IsAlive;
        }
        finally
        {
            letGo.Set();
        }
        Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
        Assert.True(actor.Join(TimeSpan.FromSeconds(60)), "the action never finished after the monitor was free");
        run.Settled = true;
    }

    private static void AssertWaited(HeldRun run, string what, string touched)
    {
        Assert.Null(run.HolderFault);
        Assert.Null(run.ActorFault);
        Assert.False(run.FinishedWhileHeld, $"{what} finished while another thread held the target's monitor");
        Assert.True(run.UntouchedWhileHeld, touched);
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight (an action
    /// waiting for a monitor), or 10 s have passed.</summary>
    private static void WaitUntilDoneOrBlocked(Thread t)
    {
        var clock = Stopwatch.StartNew();
        long blockedSince = -1;
        while (clock.ElapsedMilliseconds < 10_000 && !t.Join(5))
        {
            if ((t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) blockedSince = -1;
            else if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
            else if (clock.ElapsedMilliseconds - blockedSince >= 100) return;
        }
    }

    // ===== the race ===================================================================================

    /// <summary>One race: the workers' round counter (the tick keeps its own, so a stuck pair of workers is a stall
    /// whatever the tick does), the start line, the first fault, and whether every thread has finished.</summary>
    private sealed class Race
    {
        public readonly StallWatch.RoundCounter Progress = new();
        public readonly StallWatch.RoundCounter Ticks = new();
        public readonly ManualResetEventSlim Start = new();
        public Exception? Fault;
        public volatile bool Done;
        public bool Settled = true;

        public void Record(Exception e) => Interlocked.CompareExchange(ref Fault, e, null);
    }

    /// <summary>Start every worker on its own thread, and the world's tick on another when <paramref name="tick"/>;
    /// release them together; fail on a stall (<see cref="StallWatch"/>) rather than a stopwatch.</summary>
    private void RunRace(Race race, string what, bool tick, params Action[] workers)
    {
        race.Settled = false;
        var threads = workers.Select(w => new Thread(() =>
        {
            try { race.Start.Wait(); w(); }
            catch (Exception e) { race.Record(e); }
        }) { IsBackground = true }).ToArray();
        var ticker = new Thread(() =>
        {
            try
            {
                race.Start.Wait();
                while (!race.Done) { _fx.World.TickOnceForTest(); race.Ticks.Bump(); }
            }
            catch (Exception e) { race.Record(e); }
        }) { IsBackground = true };

        foreach (var t in threads) t.Start();
        if (tick) ticker.Start();
        race.Start.Set();
        try
        {
            StallWatch.RunUntilDoneOrStalled(threads, () => race.Progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, what);
        }
        finally
        {
            race.Done = true;
        }
        if (tick)
            StallWatch.RunUntilDoneOrStalled(new[] { ticker }, () => race.Ticks.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the tick thread");
        race.Settled = true;
    }

    /// <summary>A step at <paramref name="other"/>'s tile, refused because it is taken: the stepper's monitor and then
    /// <c>World._lock</c>, the order every walk takes.</summary>
    private void Step(Session self, Session other, ushort map) =>
        self.WithState(() => _fx.World.TryMovePlayer(self, map, other.PlayerX, other.PlayerY, ghostMover: false,
                                                     enforceOccupancy: true, otherwiseBlocked: false, out _));

    /// <summary>A fifth of a millisecond of work on the calling thread, without giving up whatever it holds.</summary>
    private static void Dwell()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedTicks < Stopwatch.Frequency / 5_000) Thread.SpinWait(20);
    }

    // ===== spells =====================================================================================

    /// <summary>A cast as <c>HandleCast</c> makes it, below the frame: the caller holds the caster's monitor.</summary>
    private static bool Cast(Session s, SpellDef sp, uint? targetId) =>
        (bool)ApplyCast.Invoke(s, new object?[] { sp, targetId, null })!;

    /// <summary>The line a cast's target reads: its spell's flavor line, or "&lt;caster&gt; casts X on you."
    /// (<c>Session.TellTarget</c>).</summary>
    private static string TargetLine(SpellDef sp, Session caster)
    {
        var flavor = Content.TargetTextFor(sp.Key);
        return flavor.Length > 0 ? flavor : $"{caster.CharName} casts {sp.Name} on you.";
    }

    private static void AssertValorLanded(Session v, SpellDef sp)
    {
        var entry = SpellCastSupport.BuffEntry(v, sp.Key);
        Assert.NotNull(entry);
        Assert.Equal(("mights", sp.Name), (entry!.Value.Category, entry.Value.Name));
        Assert.InRange(entry.Value.LeftMs, 290_000, 300_000);
    }

    private static void AssertDeduction(Session v, string name)
    {
        v.WithState(() =>
        {
            Assert.True(v.SancDeductActive, "no deduction");
            Assert.Equal(0.5, v.EffDeduction);
            Assert.Equal(name, v.SancDeductName);
            Assert.InRange(v.SancDeductUntil - Environment.TickCount64, 1, 600_000);
        });
    }

    private static void AssertLockOrderFired(Exception? wrongWay)
    {
        Assert.NotNull(wrongWay);
        Assert.Contains("lock order violated: World._lock is held while entering a session monitor", wrongWay!.Message);
    }

    /// <summary>Each entry of the buff list, by key and deadline, read under the session's monitor.</summary>
    private static string BuffFingerprint(Session s)
    {
        var sb = new StringBuilder();
        foreach (var entry in (System.Collections.IEnumerable)BuffsField.GetValue(s)!)
        {
            var t = entry.GetType();
            sb.Append(t.GetField("Key")!.GetValue(entry)).Append('@').Append(t.GetField("Expires")!.GetValue(entry)).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>A buff list a write from outside the monitor can leave torn: a null entry where a clear and an add
    /// crossed, or an expiry hint that is not the list's real minimum.</summary>
    private static void AssertBuffListWhole(Session s)
    {
        s.WithState(() =>
        {
            long min = long.MaxValue;
            foreach (var entry in (System.Collections.IEnumerable)BuffsField.GetValue(s)!)
            {
                Assert.NotNull(entry);
                long expires = (long)entry.GetType().GetField("Expires")!.GetValue(entry)!;
                if (expires < min) min = expires;
            }
            Assert.Equal(min, s.NextBuffExpiryForTest);
        });
    }

    // ===== vows =======================================================================================

    /// <summary>Give <paramref name="self"/> its half of an engagement to <paramref name="other"/> (the legend
    /// <c>PromptProposal</c> writes, and the fiancé), or of a marriage (the legend, the spouse and a love keepsake,
    /// as <c>RunMarriageCeremony</c> leaves them), under its own monitor.</summary>
    private static void Vow(Session self, Session other, bool married) =>
        self.WithState(() =>
        {
            if (married)
            {
                self.AddLegend($"Married to {other.CharName} ({Character.GameDate})", "married", 6, 1);
                self.SetSpouse(other.CharName);
                self.GiveRewardItem("love", 1);
            }
            else
            {
                self.AddLegend($"Engaged to {other.CharName} ({Character.GameDate})", "engaged", 6, 1);
                self.SetEngaged(other.CharName, isProposee: false, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 259200);
            }
        });

    /// <summary>Whether <paramref name="s"/> still holds its whole half of the vow to <paramref name="partner"/>.
    /// Read under <paramref name="s"/>'s monitor (re-entrant from a caller already inside it).</summary>
    private static bool Vowed(Session s, string partner, bool married)
    {
        bool vowed = false;
        s.WithState(() => vowed = married
            ? s.HasLegend("married") && s.CharSpouseName == partner && s.CountItem("love") == 1
            : s.HasLegend("engaged") && s.CharFiance == partner);
        return vowed;
    }

    /// <summary>Whether <paramref name="s"/> holds nothing of the vow. Called under its monitor.</summary>
    private static bool Unvowed(Session s, bool married) => married
        ? !s.HasLegend("married") && s.CharSpouseName == "" && s.CountItem("love") == 0
        : !s.HasLegend("engaged") && s.CharFiance == "" && !s.CharIsProposee && s.CharMarriageTimer == 0;

    /// <summary>Crystal and her row. Asserted, not assumed: she must be standing on her map in this world.</summary>
    private (Mob Npc, NpcDef Def) ChapelNpc()
    {
        var def = Content.NpcById(ChapelNpcId);
        Assert.True(def is not null && def.Key == "ChapelNpc", $"NPC {ChapelNpcId} is not a Chapel NPC");
        var npc = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == ChapelNpcId);
        Assert.True(npc is not null, $"{def.Name} is not placed in the world");
        return (npc!, def);
    }

    /// <summary>Click <paramref name="npc"/> (the real 0x43) and walk the Chapel's Break Off Engagement or Divorce
    /// branch up to its last question, which is left waiting: "Are you sure you want to end the engagement?" or "It
    /// will cost N experience. Are you sure you want this divorce?". The top menu's entry is found by its label on the
    /// wire, so the shop's entries ahead of it do not matter.</summary>
    private static void OpenVowEntry(Session s, RecordingOutbound o, Mob npc, NpcDef def, bool divorce)
    {
        uint id = npc.Id;
        s.Receive(SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 }));
        var labels = LastMenu(o, $"{def.Name}: How can I help you today?");
        int pick = labels.IndexOf(divorce ? "Divorce" : "Break Off Engagement") + 1;
        Assert.True(pick > 0, $"no {(divorce ? "Divorce" : "Break Off Engagement")} in [{string.Join(", ", labels)}]");
        s.Receive(MenuFrame((byte)pick));
        s.Receive(NextFrame());                // "How sad this is necessary..." / "Oh no! ..."
        if (divorce) s.Receive(NextFrame());   // "However, I can help you get that divorce you want."
        Assert.True(s.DialogBusy, "the Chapel's last question is not waiting");
        var question = LastMenu(o, divorce ? "Are you sure you want this divorce?" : "Are you sure you want to end the engagement?");
        Assert.Equal(2, question.Count);
    }

    /// <summary>The labels of the last 0x30 menu whose prompt ends with <paramref name="promptTail"/>: after the
    /// prompt come the item count and each item as u8 length + ASCII (<c>Session.SendNpcMenuP</c>).</summary>
    private static List<string> LastMenu(RecordingOutbound o, string promptTail)
    {
        byte[] tail = Encoding.ASCII.GetBytes(promptTail);
        var body = o.BodiesOf(ServerOp.NpcDialog).LastOrDefault(b =>
            b.Length > 2 && b[0] == 0x02 && b[1] == 0x02 && b.AsSpan().IndexOf(tail) >= 0);
        Assert.True(body is not null, $"no menu ending \"{promptTail}\" was sent");
        int i = body!.AsSpan().IndexOf(tail) + tail.Length;
        int count = body[i++];
        var labels = new List<string>();
        for (int k = 0; k < count; k++)
        {
            int len = body[i++];
            labels.Add(Encoding.ASCII.GetString(body, i, len));
            i += len;
        }
        return labels;
    }

    /// <summary>How many NPC text boxes said exactly <paramref name="text"/>: a box ends with its message, after the
    /// message's u16BE length, in ASCII.</summary>
    private static int Shown(RecordingOutbound o, string text)
    {
        byte[] want = Encoding.ASCII.GetBytes(text);
        return o.BodiesOf(ServerOp.NpcDialog).Count(body =>
        {
            int at = body.Length - want.Length;
            return at >= 2 && (body[at - 2] << 8 | body[at - 1]) == want.Length && body.AsSpan(at).SequenceEqual(want);
        });
    }

    /// <summary>The client's "next" on a text box (0x3A kind 1).</summary>
    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>A menu pick (0x3A kind 2, the 1-based index at [10]).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own, with no synchronization context, and wait for it:
    /// a dialog's awaits then resume inline on the thread that delivers each reply, as on the read loop.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { fault = e; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the dialog thread never finished");
        if (fault is not null) throw new Xunit.Sdk.XunitException($"the dialog thread failed: {fault}");
    }

    private string RowJson(string name)
    {
        using var cn = Db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT json FROM characters WHERE username=$u LIMIT 1;";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(name));
        return cmd.ExecuteScalar() as string ?? "";
    }

    // ===== GM commands ================================================================================

    /// <summary>A command the way the read loop runs one: a framed 0x0E chat line into <c>Session.Receive</c>.</summary>
    private static void Run(Session session, string command) => session.Receive(ChatFrame(command));

    private static byte[] ChatFrame(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var body = new byte[2 + bytes.Length];
        body[1] = (byte)bytes.Length;
        bytes.CopyTo(body, 2);
        return SessionFixture.Frame(ClientOp.Chat, body);
    }

    /// <summary>Every 0x0D line a session was sent, as ASCII: <c>SendLog</c> lands here, which is where both the
    /// muted player's notices and the GM's confirmations go.</summary>
    private static List<string> SpeechLines(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Speech).Select(b => Encoding.ASCII.GetString(b)).ToList();

    /// <summary>The mute commands refuse a name with no character row, so the target's row is written directly
    /// (the shape <c>AnnounceMonitorTests</c> uses for <c>@ban</c>).</summary>
    private void SaveRow(Character c)
    {
        Assert.True(_fx.Store.SaveJson(CharacterStore.Key(c.Name), CharacterStore.Serialize(c)),
                    "the setup save failed: see the '[db] !!' line in the captured log. That is a broken test "
                    + "setup, not the command refusing anything.");
        Assert.True(CharacterStore.CharacterExists(c.Name));
    }

    private static void EnsureGmRoster()
    {
        lock (TestProcessState.Gate)
        {
            if (_rosterWritten) return;
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
            _rosterWritten = true;
        }
    }

    // ===== players ====================================================================================

    /// <summary>A level-99 caster with <paramref name="book"/> in slots 0.., on a 100x100 walkable map;
    /// <paramref name="shape"/> runs last.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Poet(ushort map, ushort x, ushort y,
        SpellDef[] book, uint mp = Mana, byte will = 50, Action<Character>? shape = null)
    {
        int n = Interlocked.Increment(ref _serial);
        return _fx.PlayerWith($"PeerCaster{n}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = mp; ch.Mp = mp;
            ch.Will = will;
            Wide(ch, map);
            foreach (var sp in book) ch.Spells.Add(sp.Id);
            shape?.Invoke(ch);
        }, map, x, y);
    }

    /// <summary>A player with no spells, on a 100x100 walkable map.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Plain(string name, ushort map, ushort x, ushort y,
                                                                           Action<Character>? shape = null) =>
        _fx.PlayerWith($"{name}{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50; ch.MaxHp = 1_000; ch.Hp = 1_000;
            Wide(ch, map);
            shape?.Invoke(ch);
        }, map, x, y);

    /// <summary>A Chapel customer: exp enough for either branch's price (<c>MaxMp * 1000</c> and
    /// <c>MaxHp * 2550</c>).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Partner(string name, ushort map, ushort x, ushort y) =>
        Plain(name, map, x, y, ch => { ch.Exp = 100_000_000; ch.MaxMp = 100; ch.Mp = 100; });

    /// <summary>A GM on <paramref name="map"/>. The roster names one account, so every GM session shares its name;
    /// nothing here looks the GM up by it.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Gm(ushort map, ushort x, ushort y) =>
        _fx.PlayerWith(GmName, ch => { ch.Level = 99; ch.MaxHp = 1_000; ch.Hp = 1_000; Wide(ch, map); }, map, x, y);

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    /// <summary>Content-free maps have no size of their own; the cast and walk handlers need one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
