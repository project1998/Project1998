using System.Diagnostics;
using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// A Dispell-family cast at another player (Dispell, Remove Magic, Return Natural, Restore Balance) writes that
/// player's buffs and timers inside that player's own monitor (PR #338 re-checks, "Pre-existing").
///
/// <para><b>What it did.</b> The four reach the <c>cleanse</c> verb (game-data/spell_verbs.lua). On a won roll it
/// calls <c>flushTarget</c>, which ran the TARGET's <c>FlushDurations</c> and <c>SendStats</c> on the caster's
/// thread, holding the caster's monitor and the Lua gate but not the target's. <c>FlushDurations</c> clears the
/// target's buff list (<c>BuffClear</c>, one of the four guarded <c>_buffs</c> writers) and its fury, stealth,
/// Backstab, Flank and four-way timers. In a Debug build the guard fired inside the verb: the caster read
/// "Dispell isn't working right now." with the 200 mana already spent, and nothing was cleared. In a Release
/// build it was an unguarded write to another session's state, from another thread.</para>
///
/// <para><b>How "inside the target's monitor" is observed in Release, where the guard is compiled out.</b> A
/// third thread takes the target's monitor and holds it while the caster's cast runs. A write made inside the
/// target's monitor has to wait for it; the old write did not, and cleared the buffs while that thread still
/// held the monitor (<see cref="ADispellAtAnotherPlayerWaitsForThatPlayersMonitor"/>).</para>
///
/// <para>Driven through the real 0x0F cast frame on <c>Session.Receive</c>, as <c>BarrierSpellTests</c> does; the
/// race and the repeated casts call <c>ApplyCast</c> under the caster's monitor, as <c>HandleCast</c> does, below
/// the packet handler's action budget. A won roll is made certain where a fact needs one: the success rate is
/// max(10, ceil((120 + clamp(target AC, -60, 70) - floor((target Will - caster Will) / 10)) / 2)), so a caster
/// with Will 255 against a target with the default AC (99, clamped to 70) and Will 3 rolls against 108. Content-free
/// maps no other class uses.</para>
/// </summary>
[Collection("world")]
public sealed class DispellTargetLockTests
{
    private const ushort CastMap = 62390, HeldMap = 62391, SelfMap = 62392, RaceMap = 62393, RollMap = 62394,
                         PinMap = 62395;

    /// <summary>The probe entries a target carries before a Dispell: a stat buff, and a categorised status in
    /// the <c>paras</c> slot (a Human Barrier's hold), both written by the same writer a curse uses.</summary>
    private const string BuffKey = "dispell_probe_might", HoldKey = "dispell_probe_hold";

    private const uint Mana = 10_000, Cost = 200;

    /// <summary>The party line the race's Poets send each other.</summary>
    private const string Ping = "dispell race ping";

    private readonly SessionFixture _fx;
    private static int _serial;

    private static readonly MethodInfo ApplyCast =
        typeof(Session).GetMethod("ApplyCast", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageAmountField =
        typeof(Session).GetField("_rageAmount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo BuffsField =
        typeof(Session).GetField("_buffs", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public DispellTargetLockTests(SessionFixture fx) => _fx = fx;

    public static TheoryData<string> DispellFamily => new()
        { "dispell_poet", "remove_magic_poet", "return_natural_poet", "restore_balance_poet" };

    // ===== another player =============================================================================

    /// <summary>Each of the four, cast at another player through the real frame, clears that player's buff list
    /// (the stat buff and the hold) and their fury, takes its 200 mana, says "You cast X." to the caster and
    /// "&lt;caster&gt; casts X on you." to the target. Red on 08ed6ff in Debug: the guard fires inside the verb,
    /// the caster reads "X isn't working right now." with the mana spent, and the target keeps everything.</summary>
    [Theory]
    [MemberData(nameof(DispellFamily))]
    public void ADispellFamilyCastAtAnotherPlayerClearsTheirBuffsAndFury(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (v, vOut, _) = Plain("DispellV", CastMap, 20, 20);
        var (d, dOut, dc) = Poet(CastMap, 22, 20, will: 255, sp);
        try
        {
            GiveEffects(v);
            Assert.True(v.Paralyzed);

            d.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));

            var dLines = SpellCastSupport.MiniTexts(dOut);
            Assert.DoesNotContain($"{sp.Name} isn't working right now.", dLines);
            Assert.Contains($"You cast {sp.Name}.", dLines);
            Assert.Equal(Mana - Cost, dc.Mp);
            AssertEffectsCleared(v);
            Assert.Contains($"{d.CharName} casts {sp.Name} on you.", SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            _fx.World.LeaveMap(v, CastMap);
            _fx.World.LeaveMap(d, CastMap);
        }
    }

    /// <summary>The reproduction for both builds. A third thread holds the target's monitor while the caster's
    /// Dispell runs on its own thread. The write has to wait for the monitor: while that thread holds it, the
    /// cast does not finish and the target's buffs and fury are all still there. Once it lets go, the cast
    /// finishes and clears them, with the same mana and lines as any won cast. Both orders of the two players'
    /// <c>StateRank</c>, so both of <c>EnterState</c>'s branches (the plain one, and the one that drops the
    /// caster's monitor while it waits) are the ones that wait.
    /// <para>Red on 08ed6ff in Release: the cast finishes while the third thread holds the target's monitor, and
    /// that thread finds the buffs gone from under it. Red there in Debug as in the fact above.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADispellAtAnotherPlayerWaitsForThatPlayersMonitor(bool casterRanksFirst)
    {
        var sp = Content.SpellByKey("dispell_poet")!;
        Session d, v;
        RecordingOutbound dOut, vOut;
        Character dc;
        if (casterRanksFirst)
        {
            (d, dOut, dc) = Poet(HeldMap, 30, 30, will: 255, sp);
            (v, vOut, _) = Plain("DispellHeldV", HeldMap, 32, 30);
        }
        else
        {
            (v, vOut, _) = Plain("DispellHeldV", HeldMap, 32, 30);
            (d, dOut, dc) = Poet(HeldMap, 30, 30, will: 255, sp);
        }
        Assert.Equal(casterRanksFirst, d.StateRank < v.StateRank);

        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        bool presentWhileHeld = false;
        Exception? holderFault = null, casterFault = null;
        var holder = new Thread(() =>
        {
            try
            {
                v.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    presentWhileHeld = SpellCastSupport.BuffEntry(v, BuffKey) is not null
                                       && SpellCastSupport.BuffEntry(v, HoldKey) is not null
                                       && SpellCastSupport.Rage(v).LeftMs > 0;
                });
            }
            catch (Exception e) { holderFault = e; }
        }) { IsBackground = true };
        var caster = new Thread(() =>
        {
            try { d.Receive(SpellCastSupport.CastFrame(0, v.PlayerId)); }
            catch (Exception e) { casterFault = e; }
        }) { IsBackground = true };
        try
        {
            GiveEffects(v);
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");

            caster.Start();
            WaitUntilDoneOrBlocked(caster);
            bool finishedWhileHeld = !caster.IsAlive;
            letGo.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
            Assert.True(caster.Join(TimeSpan.FromSeconds(60)), "the cast never finished after the monitor was free");

            Assert.Null(holderFault);
            Assert.Null(casterFault);
            Assert.False(finishedWhileHeld, "the Dispell finished while another thread held the target's monitor");
            Assert.True(presentWhileHeld, "the target's buffs or fury were cleared while another thread held its monitor");

            var dLines = SpellCastSupport.MiniTexts(dOut);
            Assert.DoesNotContain($"{sp.Name} isn't working right now.", dLines);
            Assert.Contains($"You cast {sp.Name}.", dLines);
            Assert.Equal(Mana - Cost, dc.Mp);
            AssertEffectsCleared(v);
            Assert.Contains($"{d.CharName} casts {sp.Name} on you.", SpellCastSupport.MiniTexts(vOut));
        }
        finally
        {
            letGo.Set();
            // A cast still stuck on the monitor would hold the Lua gate; leaving the map would then wait on it.
            if (!holder.IsAlive && !caster.IsAlive)
            {
                _fx.World.LeaveMap(v, HeldMap);
                _fx.World.LeaveMap(d, HeldMap);
            }
        }
    }

    // ===== the caster's own buffs =====================================================================

    /// <summary>The casts that clear the caster's own buffs are unchanged. A Dispell on yourself, through the real
    /// frame with your own id, clears your buff list and your fury, takes 200 mana, says "You cast Dispell." and
    /// tells you nothing else. (Against yourself the rate is at most 95%, so a lost roll is cast again; each cast
    /// costs 200 either way.) Cure Paralysis clears the caster's own <c>paras</c> entry, Purge its <c>venoms</c>
    /// entry and Atone its <c>curses</c> entry, each for its row's mana. Cure Paralysis is called as
    /// <c>HandleCast</c> calls it, past the handler's paralysis gate: a held player cannot cast it through the frame
    /// (Session.Barrier.cs). Not a guard of this fix: green before and after it, in both builds.</summary>
    [Fact]
    public void ADispellOnYourselfAndTheCuresStillClearTheCastersOwnBuffs()
    {
        var dispell = Content.SpellByKey("dispell_poet")!;
        var cureParalysis = Content.SpellByKey("cure_paralysis_poet")!;
        var purge = Content.SpellByKey("purge_poet")!;
        var atone = Content.SpellByKey("atone_poet")!;
        var (p, pOut, pc) = Poet(SelfMap, 40, 40, will: 255, dispell, cureParalysis, purge, atone);
        try
        {
            p.ReceiveCurse("might", 5, 600_000, BuffKey, "Probe Might", "");
            SetRage(p, 3, 600_000);

            int casts = 0;
            while (SpellCastSupport.BuffEntry(p, BuffKey) is not null && casts < 20)
            {
                if (casts > 0) Thread.Sleep(400);   // the next window of the handler's action budget
                pOut.Clear();
                p.Receive(SpellCastSupport.CastFrame(0, p.PlayerId));
                casts++;
            }
            var lines = SpellCastSupport.MiniTexts(pOut);
            Assert.Null(SpellCastSupport.BuffEntry(p, BuffKey));
            Assert.Equal((1, 0L), SpellCastSupport.Rage(p));
            Assert.Equal(Mana - Cost * (uint)casts, pc.Mp);
            Assert.Contains("You cast Dispell.", lines);
            Assert.DoesNotContain("Dispell isn't working right now.", lines);
            Assert.DoesNotContain(lines, l => l.EndsWith("casts Dispell on you.", StringComparison.Ordinal));

            foreach (var (cure, category, slot) in new[] { (cureParalysis, "paras", 1), (purge, "venoms", 2), (atone, "curses", 3) })
            {
                string key = $"dispell_probe_{category}";
                p.ReceiveCurse("", 0, 600_000, key, $"Probe {category}", category);
                Assert.NotNull(SpellCastSupport.BuffEntry(p, key));
                uint before = pc.Mp;
                bool ok = false;
                if (category == "paras")
                    p.WithState(() => ok = (bool)ApplyCast.Invoke(p, new object?[] { cure, null, null })!);
                else
                {
                    pOut.Clear();
                    p.Receive(SpellCastSupport.CastFrame(slot));
                    ok = SpellCastSupport.MiniTexts(pOut).Contains($"You cast {cure.Name}.");
                    Thread.Sleep(400);
                }
                Assert.True(ok, $"{cure.Name} did not cast");
                Assert.Null(SpellCastSupport.BuffEntry(p, key));
                Assert.Equal(before - (uint)Content.FxFor(cure)!.Mana, pc.Mp);
            }
        }
        finally
        {
            _fx.World.LeaveMap(p, SelfMap);
        }
    }

    // ===== mana on a cast that does not land ===========================================================

    /// <summary>Today's Release behaviour, kept. A Dispell that loses its roll still costs its 200 mana (the verb
    /// spends before it rolls) and says "Something went wrong."; the target is told nothing and keeps everything.
    /// One refused before the roll costs nothing: with too little mana ("You do not have enough mana.") and with
    /// nobody to aim at (silent). The roll is made unlikely to win (caster Will 0 against Will 255 and AC -60: 18%)
    /// and cast until it loses. Not a guard of this fix: green before and after it, in Release.</summary>
    [Fact]
    public void ADispellThatLosesItsRollStillCostsItsManaAndARefusedOneCostsNothing()
    {
        var sp = Content.SpellByKey("dispell_poet")!;
        var (v, vOut, _) = Plain("DispellRollV", RollMap, 50, 50, c => { c.Will = 255; c.Ac = -60; });
        var (d, dOut, dc) = Poet(RollMap, 50, 52, will: 0, sp);
        var (poor, poorOut, poorc) = Poet(RollMap, 54, 50, will: 255, sp);
        try
        {
            poorc.Mp = Cost - 1;
            poor.Receive(SpellCastSupport.CastFrame(0, v.PlayerId));
            Assert.Equal(Cost - 1, poorc.Mp);
            Assert.Equal(new[] { "You do not have enough mana." }, SpellCastSupport.MiniTexts(poorOut));

            // An id nobody online has, and nobody on the faced tile: nothing resolves.
            d.Receive(SpellCastSupport.CastFrame(0, 0x7FFF_FFF0));
            Assert.Equal(Mana, dc.Mp);
            Assert.Empty(SpellCastSupport.MiniTexts(dOut));

            GiveEffects(v);
            vOut.Clear();
            bool lost = false;
            for (int casts = 1; casts <= 200 && !lost; casts++)
            {
                dOut.Clear();
                uint before = dc.Mp;
                bool ok = false;
                d.WithState(() => ok = (bool)ApplyCast.Invoke(d, new object?[] { sp, v.PlayerId, null })!);
                Assert.True(ok, $"cast {casts} was refused");
                Assert.Equal(before - Cost, dc.Mp);
                lost = SpellCastSupport.BuffEntry(v, BuffKey) is not null;
                if (!lost) { GiveEffects(v); vOut.Clear(); continue; }
                Assert.Equal(new[] { "Something went wrong." }, SpellCastSupport.MiniTexts(dOut));
                Assert.Empty(SpellCastSupport.MiniTexts(vOut));
                Assert.NotNull(SpellCastSupport.BuffEntry(v, HoldKey));
                Assert.True(SpellCastSupport.Rage(v).LeftMs > 0);
            }
            Assert.True(lost, "200 casts at 18% never lost a roll");
        }
        finally
        {
            foreach (var s in new[] { v, d, poor }) _fx.World.LeaveMap(s, RollMap);
        }
    }

    // ===== locks ======================================================================================

    /// <summary>Two Poets Dispell each other again and again while each also buffs itself, sends the other a party
    /// line, steps at the other's tile and the world ticks. Each Dispell writes the other player's buffs inside
    /// that player's monitor, taken by <c>StateRank</c> from inside the caster's own, so one direction ascends and
    /// the other descends; each self-buff writes the caster's own list; each party line enters the other's
    /// monitor from inside the sender's, outside Lua, so the gate does not keep it apart from the other's Dispell;
    /// each step takes the stepper's monitor and then <c>World._lock</c>, and is refused (the tile is taken); the
    /// tick's regen beat takes each monitor to expire buffs. Nothing may stall (<see cref="StallWatch"/>). Every
    /// cast and party line goes through and every won roll is told to
    /// its target; no self-buff disappears inside its own caster's critical section (held a fifth of a
    /// millisecond past the cast, as a handler's other work would), which is what a write from outside the
    /// monitor does; and each buff list ends whole, its expiry hint the real minimum.
    /// <para>Red on 08ed6ff in both builds. Debug: the guard fires inside every won Dispell, so only the lost
    /// rolls go through. Release: a Poet enumerating its own list inside its own monitor (lapsing its Might)
    /// throws "Collection was modified", because the other Poet's Dispell cleared that list from outside it.</para>
    /// <para>Two cycles are what this pins. Writing the other player's buffs inside <c>World._lock</c>: one Poet
    /// holds the lock and waits for the other's monitor while the other, mid-step, holds its monitor and waits for
    /// the lock. Taking the other's monitor with a plain <c>lock</c> instead of <c>EnterState</c>: the
    /// lower-ranked Poet, sending its party line, holds its own monitor and waits for the other's, while the other,
    /// mid-Dispell, holds its own and waits for the lower-ranked one's.</para></summary>
    [Fact]
    public void TwoPlayersDispellingEachOtherWhileBuffingThemselvesNeverStall()
    {
        const int Rounds = 300;
        var dispell = Content.SpellByKey("dispell_poet")!;
        var might = Content.SpellByKey("might_mage")!;
        var (a, aOut, _) = Poet(RaceMap, 10, 10, will: 255, dispell, might, mp: 10_000_000);
        var (b, bOut, _) = Poet(RaceMap, 11, 10, will: 255, dispell, might, mp: 10_000_000);
        var progress = new StallWatch.RoundCounter();
        var start = new ManualResetEventSlim();
        Exception? fault = null;
        bool done = false;
        int dispellsA = 0, dispellsB = 0, buffsA = 0, buffsB = 0, lostInSection = 0;

        void Caster(Session self, Session other, Action dispelled, Action buffed)
        {
            try
            {
                start.Wait();
                for (int i = 0; i < Rounds; i++)
                {
                    // Lapse this Poet's own Might so the recast is not refused by the mights slot, whatever the
                    // other thread's last Dispell did.
                    SpellCastSupport.EndBuff(self, might.Key);
                    self.WithState(() =>
                    {
                        if (!(bool)ApplyCast.Invoke(self, new object?[] { might, null, null })!) return;
                        buffed();
                        Dwell();   // the rest of a handler's work, still inside this Poet's monitor
                        if (SpellCastSupport.BuffEntry(self, might.Key) is null) Interlocked.Increment(ref lostInSection);
                    });
                    bool ok = false;
                    self.WithState(() => ok = (bool)ApplyCast.Invoke(self, new object?[] { dispell, other.PlayerId, null })!);
                    if (ok) dispelled();
                    // A peer entry outside Lua, from inside this Poet's monitor: a party line to the other.
                    self.WithState(() => other.NotifyGroup(Ping));
                    self.WithState(() => _fx.World.TryMovePlayer(self, RaceMap, other.PlayerX, other.PlayerY,
                                                                 ghostMover: false, enforceOccupancy: true,
                                                                 otherwiseBlocked: false, out _));
                    progress.Bump();
                }
            }
            catch (Exception e) { fault ??= e; }
        }

        var ta = new Thread(() => Caster(a, b, () => dispellsA++, () => buffsA++)) { IsBackground = true };
        var tb = new Thread(() => Caster(b, a, () => dispellsB++, () => buffsB++)) { IsBackground = true };
        var tick = new Thread(() =>
        {
            try
            {
                start.Wait();
                while (!Volatile.Read(ref done)) { _fx.World.TickOnceForTest(); progress.Bump(); }
            }
            catch (Exception e) { fault ??= e; }
        }) { IsBackground = true };
        try
        {
            ta.Start(); tb.Start(); tick.Start();
            start.Set();
            StallWatch.RunUntilDoneOrStalled(new[] { ta, tb }, () => progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the two Dispelling Poets");
            Volatile.Write(ref done, true);
            StallWatch.RunUntilDoneOrStalled(new[] { tick }, () => progress.Rounds,
                StallWatch.StallQuiet, StallWatch.StallCap, "the tick thread");

            Assert.Null(fault);
            Assert.Equal(0, lostInSection);
            Assert.Equal((Rounds, Rounds), (buffsA, buffsB));
            Assert.Equal((Rounds, Rounds), (dispellsA, dispellsB));
            var aLines = SpellCastSupport.MiniTexts(aOut);
            var bLines = SpellCastSupport.MiniTexts(bOut);
            Assert.DoesNotContain("Dispell isn't working right now.", aLines);
            Assert.DoesNotContain("Dispell isn't working right now.", bLines);
            // Every Dispell either won (its target was told) or lost (its caster was told), and no other way.
            int wonA = bLines.Count(l => l == $"{a.CharName} casts Dispell on you.");
            int wonB = aLines.Count(l => l == $"{b.CharName} casts Dispell on you.");
            int lostA = aLines.Count(l => l == "Something went wrong.");
            int lostB = bLines.Count(l => l == "Something went wrong.");
            Assert.Equal((Rounds, Rounds), (wonA + lostA, wonB + lostB));
            Assert.True(wonA > 0 && wonB > 0, $"no Dispell won ({wonA}, {wonB})");
            Assert.Equal((Rounds, Rounds), (aLines.Count(l => l == Ping), bLines.Count(l => l == Ping)));
            AssertBuffListWhole(a);
            AssertBuffListWhole(b);
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            Volatile.Write(ref done, true);
            // A stalled run leaves its map as it is: cleaning it up would wait on the lock the stuck threads
            // hold, and the fact would hang instead of reporting the stall.
            if (!ta.IsAlive && !tb.IsAlive && !tick.IsAlive)
            {
                _fx.World.LeaveMap(a, RaceMap);
                _fx.World.LeaveMap(b, RaceMap);
            }
        }
    }

#if DEBUG
    /// <summary>The write's entry is the ordered one, asserted rather than merely arranged: the target's monitor
    /// is taken through <c>EnterState</c>, so writing another player's buffs with <c>World._lock</c> held fires
    /// the lock-order assert before anything is cleared. The legal entries are silent and clear: from a thread
    /// holding a lower-ranked player's monitor (the plain branch) and a higher-ranked one's (the branch that drops
    /// it while it waits). Debug-only by construction, like the asserts it pins (SessionActorTests).
    /// <para>Red without the fix: with no monitor the <c>_buffs</c> guard fires on every entry, the legal ones
    /// too; with a plain <c>lock</c> on the target's monitor nothing fires under <c>World._lock</c>.</para></summary>
    [Fact]
    public void FlushingAnotherPlayerUnderTheWorldLockAsserts()
    {
        var (low, _, _) = Plain("DispellPinLow", PinMap, 60, 60);
        var (v, _, _) = Plain("DispellPinV", PinMap, 62, 60);
        var (high, _, _) = Plain("DispellPinHigh", PinMap, 64, 60);
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);
            GiveEffects(v);

            var wrongWay = Record.Exception(() => _fx.World.UnderWorldLockForTest(v.ReceiveFlush));
            Assert.NotNull(wrongWay);
            Assert.Contains("lock order violated: World._lock is held while entering a session monitor", wrongWay!.Message);
            Assert.NotNull(SpellCastSupport.BuffEntry(v, BuffKey));

            Assert.Null(Record.Exception(() => low.WithState(v.ReceiveFlush)));
            AssertEffectsCleared(v);

            GiveEffects(v);
            Assert.Null(Record.Exception(() => high.WithState(v.ReceiveFlush)));
            AssertEffectsCleared(v);
        }
        finally
        {
            foreach (var s in new[] { low, v, high }) _fx.World.LeaveMap(s, PinMap);
        }
    }
#endif

    // ===== helpers ====================================================================================

    /// <summary>The target's effects before a Dispell: a stat buff, the hold (a categorised status in the
    /// <c>paras</c> slot), and a fury (one of the timers <c>FlushDurations</c> zeroes) with 10 minutes to run.</summary>
    private static void GiveEffects(Session v)
    {
        v.ReceiveCurse("might", 5, 600_000, BuffKey, "Probe Might", "");
        v.ReceiveCurse("", 0, 600_000, HoldKey, "Probe Hold", Session.ParalysisSlot);
        SetRage(v, 3, 600_000);
    }

    private static void AssertEffectsCleared(Session v)
    {
        Assert.Null(SpellCastSupport.BuffEntry(v, BuffKey));
        Assert.Null(SpellCastSupport.BuffEntry(v, HoldKey));
        Assert.False(v.Paralyzed);
        Assert.Equal((1, 0L), SpellCastSupport.Rage(v));
    }

    /// <summary>Arm a fury the way <c>stance_rage</c> leaves one: the multiplier and its deadline, under the
    /// session's monitor.</summary>
    private static void SetRage(Session s, int amount, int ms) =>
        s.WithState(() =>
        {
            RageAmountField.SetValue(s, amount);
            RageUntilField.SetValue(s, Environment.TickCount64 + ms);
        });

    /// <summary>A buff list a write from outside the monitor can leave torn: a null entry where a clear and an
    /// add crossed, or an expiry hint that is not the list's real minimum.</summary>
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

    /// <summary>A fifth of a millisecond of work on the calling thread, without giving up whatever it holds.</summary>
    private static void Dwell()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedTicks < Stopwatch.Frequency / 5_000) Thread.SpinWait(20);
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight (a cast
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

    /// <summary>A level-99 caster with <paramref name="book"/> in slots 0.., on a 100x100 walkable map.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Poet(ushort map, ushort x, ushort y, byte will,
                                                                          params SpellDef[] book) =>
        Poet(map, x, y, will, book, Mana);

    private (Session session, RecordingOutbound outbound, Character c) Poet(ushort map, ushort x, ushort y, byte will,
                                                                          SpellDef first, SpellDef second, uint mp) =>
        Poet(map, x, y, will, new[] { first, second }, mp);

    private (Session session, RecordingOutbound outbound, Character c) Poet(ushort map, ushort x, ushort y, byte will,
                                                                          SpellDef[] book, uint mp)
    {
        int n = Interlocked.Increment(ref _serial);
        return _fx.PlayerWith($"Dispeller{n}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = mp; ch.Mp = mp;
            ch.Will = will;
            Wide(ch, map);
            foreach (var sp in book) ch.Spells.Add(sp.Id);
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

    /// <summary>Content-free maps have no size of their own; the cast and walk handlers need one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
