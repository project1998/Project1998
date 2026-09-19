using System.Buffers.Binary;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The lock-free pre-check in <see cref="Session.TickSleep"/> and <see cref="Session.TickPoison"/>.
///
/// <para><b>Why this needs tests by the "would this fail loudly?" rule.</b> Every failure mode of the change
/// is silent. If the pre-check is put back under the monitor, nothing is wrong except that the tick thread
/// waits behind 400 session monitors a beat again, which no test and no log line would show. If the
/// pre-check reads the wrong field, a venomed player simply never takes a tick and a slept player never
/// redraws — no throw, no error line, just a status that does nothing. And the cross-thread read has no
/// observable symptom at all on the machine it is written on, which is exactly why the contract is pinned
/// here in writing rather than left to a comment.</para>
///
/// <para>The three facts are: the no-op path enters no monitor; an afflicted player is still ticked under
/// the monitor and still hits the never-kill floor and the lapse; and a write made on another thread is
/// seen by the tick thread.</para>
/// </summary>
[Collection("world")]
public class StatusEarlyOutTests
{
    /// <summary>A hold long enough that a blocked call is unmistakable and short enough that a red fact does
    /// not stall the suite. The base blocks for the whole of it; the head does not touch the monitor.</summary>
    private const int HoldMs = 3_000;

    /// <summary>What the no-op path is allowed to take while another thread owns the monitor. Two orders of
    /// magnitude under <see cref="HoldMs"/>, so the fact cannot pass by being slow.</summary>
    private const int NoOpBudgetMs = 250;

    private readonly SessionFixture _fx;

    public StatusEarlyOutTests(SessionFixture fx) => _fx = fx;

    /// <summary>(a) A player with neither a hold nor a venom is ticked without entering their session
    /// monitor at all: with a background thread holding that monitor for three seconds, both calls return
    /// on the test thread inside a quarter of a second.
    ///
    /// <para>Falsification: move the <c>Volatile.Read</c> pre-check back under <c>EnterState()</c> in either
    /// method and the call blocks until the holder lets go. Run, confirm red, restore. Recorded in
    /// <c>briefs/reports/status-early-out-opus.md</c>: with the pre-check under the monitor this fact fails
    /// on the elapsed assertion at the full hold.</para>
    ///
    /// <para>The holder is released in a <c>finally</c>, so a red here never leaves a monitor owned by a
    /// dead test's thread.</para></summary>
    [Fact]
    public void TheNoOpPathEntersNoMonitor()
    {
        var (idle, _) = _fx.Player("EarlyOutIdle", SessionFixture.HomeMap, x: 5, y: 10);
        using var holding = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var _ = idle.EnterState();
            holding.Set();
            release.Wait(HoldMs * 2);
        }) { IsBackground = true, Name = "EarlyOutHolder" };
        Thread? sweeps = null;

        try
        {
            holder.Start();
            Assert.True(holding.Wait(HoldMs), "the background thread never took the monitor");
            Assert.False(idle.Asleep);
            Assert.False(idle.Poisoned);

            Exception? thrown = null;
            var sweep = new Thread(() =>
            {
                try { idle.TickSleep(); idle.TickPoison(); }
                catch (Exception e) { thrown = e; }
            }) { IsBackground = true, Name = "EarlyOutSweep" };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            sweep.Start();
            bool done = sweep.Join(NoOpBudgetMs);
            sw.Stop();

            Assert.True(done,
                $"TickSleep/TickPoison did not return in {NoOpBudgetMs} ms (waited {sw.ElapsedMilliseconds} ms) "
              + "with another thread holding the session monitor — the no-op path is entering the monitor");
            Assert.Null(thrown);
            sweeps = sweep;
        }
        finally
        {
            release.Set();
            holder.Join(HoldMs * 2);
            sweeps?.Join(HoldMs * 2);   // a red leaves it blocked on the monitor; let it finish after the release
            _fx.World.LeaveMap(idle, SessionFixture.HomeMap);
        }
    }

    /// <summary>(b) An afflicted player is still ticked, under the monitor, exactly as before: the drowse
    /// redraws over a sleeper, the venom takes health and stops at the 1 HP floor rather than killing, and
    /// both statuses end at their lapse — the wake and the cure, through the appliers' own durations rather
    /// than by writing the <c>_until</c> fields.
    ///
    /// <para>Falsification: make <c>TickSleep</c>'s pre-check read <c>_poisonUntil</c> instead of
    /// <c>_sleepUntil</c>. The sleeper is not venomed, so the pre-check returns and the drowse is never
    /// redrawn and the hold never lapses. Run, confirm red, restore. Recorded in the report: the drowse
    /// assertion goes red on zero 0x29 frames.</para></summary>
    [Fact]
    public void AnAfflictedPlayerIsStillTickedUnderTheMonitor()
    {
        const int Anim = 2;
        var (sleeper, sleeperOut, _) = _fx.PlayerWith(
            "EarlyOutSleeper", c => { c.Hp = 500; c.MaxHp = 500; }, SessionFixture.HomeMap, x: 5, y: 11);
        var (victim, victimOut, hurt) = _fx.PlayerWith(
            "EarlyOutVictim", c => { c.Hp = 5; c.MaxHp = 500; }, SessionFixture.HomeMap, x: 6, y: 11);
        try
        {
            // ---- the drowse redraws over a sleeper -------------------------------------------------
            sleeper.ReceiveSleep("sleeps", durMs: 400, key: "test_doze", name: "Doze", anim: Anim, repeatFxMs: 1);
            Assert.True(sleeper.Asleep);
            sleeperOut.Clear();
            Assert.True(
                SpinWait.SpinUntil(() => { sleeper.TickSleep(); return EffectIdsOver(sleeperOut, sleeper.PlayerId).Count > 0; }, 2000),
                "the drowse was never redrawn over the sleeping player");
            Assert.Contains(Anim, EffectIdsOver(sleeperOut, sleeper.PlayerId));

            // ---- the venom takes health and stops at 1 HP ------------------------------------------
            victim.ReceivePoison(
                dps: 1, durMs: 4000, by: 0, anim: 0, key: "test_venom", name: "venom",
                perTick: 1000, tickMinMs: 1, tickMaxMs: 1);
            Assert.True(victim.Poisoned);
            Assert.True(
                SpinWait.SpinUntil(() => { victim.TickPoison(); return hurt.Hp < 5; }, 2000),
                "the first venom tick never landed");
            Assert.Equal(1u, hurt.Hp);                 // never the killing blow, however large the tick
            Assert.False(victim.IsDead);
            for (int i = 0; i < 50; i++) { victim.TickPoison(); Thread.Sleep(1); }
            Assert.Equal(1u, hurt.Hp);                 // and it keeps flashing without hurting further
            Assert.False(victim.IsDead);

            // ---- both statuses end at their own lapse ----------------------------------------------
            // The lapse is SILENT on both paths and always has been — WakeUp and CurePoison only speak when
            // the status was still live when they were called, and at a lapse it is not. So the fact is that
            // the tick stops: the drowse is no longer redrawn and the venom no longer bites.
            Assert.True(SpinWait.SpinUntil(() => !sleeper.Asleep, 2000), "the sleep never lapsed");
            sleeper.TickSleep();                       // the beat that clears the slot
            Assert.False(sleeper.Asleep);
            sleeperOut.Clear();
            for (int i = 0; i < 20; i++) { sleeper.TickSleep(); Thread.Sleep(1); }
            Assert.Empty(EffectIdsOver(sleeperOut, sleeper.PlayerId));

            hurt.Hp = 400;                             // out of the floor, so a tick after the lapse would show
            Assert.True(SpinWait.SpinUntil(() => !victim.Poisoned, 6000), "the venom never lapsed");
            victim.TickPoison();                       // the beat that clears the slot
            Assert.False(victim.Poisoned);
            for (int i = 0; i < 20; i++) { victim.TickPoison(); Thread.Sleep(1); }
            Assert.Equal(400u, hurt.Hp);
        }
        finally
        {
            _fx.World.LeaveMap(sleeper, SessionFixture.HomeMap);
            _fx.World.LeaveMap(victim, SessionFixture.HomeMap);
        }
    }

    /// <summary>(c) A hold applied on ANOTHER thread is seen by the thread that runs the tick: the applier
    /// runs on a background thread, the thread is joined, and the drowse is redrawn on the test thread.
    ///
    /// <para><b>The honest note about the falsification.</b> Replacing <c>Volatile.Read</c> with a plain
    /// field read is NOT a red on x64: the hardware does not reorder the load, and the join alone is a
    /// full fence, so the test passes either way on this machine. That was run and it was green; no red is
    /// claimed for it. The fact is here to pin the CONTRACT — the tick thread reads a field it does not own
    /// the monitor for — and the reason it holds is the runtime's memory model, not this assertion.</para></summary>
    [Fact]
    public void AHoldAppliedOnAnotherThreadIsSeenByTheTickThread()
    {
        const int Anim = 3;
        var (sleeper, sleeperOut) = _fx.Player("EarlyOutCrossThread", SessionFixture.HomeMap, x: 7, y: 11);
        try
        {
            var applier = new Thread(() =>
                sleeper.ReceiveSleep("sleeps", durMs: 2000, key: "test_doze_x", name: "Doze", anim: Anim, repeatFxMs: 1))
                { IsBackground = true, Name = "EarlyOutApplier" };
            applier.Start();
            Assert.True(applier.Join(5000), "the applier thread did not finish");

            sleeperOut.Clear();
            Assert.True(
                SpinWait.SpinUntil(() => { sleeper.TickSleep(); return EffectIdsOver(sleeperOut, sleeper.PlayerId).Count > 0; }, 2000),
                "the tick thread did not see the hold another thread applied");
            Assert.Contains(Anim, EffectIdsOver(sleeperOut, sleeper.PlayerId));
        }
        finally
        {
            using (var _ = sleeper.EnterState()) sleeper.WakeUp(byDamage: false);   // WakeUp writes _buffs
            _fx.World.LeaveMap(sleeper, SessionFixture.HomeMap);
        }
    }

    /// <summary>The effect ids this recorder was told to draw over <paramref name="id"/>. The 0x29 body is
    /// <c>id(u32 BE) | efx(u8) | A(u16) | B(u16) | C(u16)</c> — see <c>Session.SendEffect</c> — and the wire
    /// byte carries <c>Session.EfxWireOffset</c>, which is subtracted back off here.</summary>
    private static List<int> EffectIdsOver(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect)
                .Where(b => b.Length >= 5 && BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0)) == id)
                .Select(b => b[4] - ServerConfig.Current.EfxWireOffset)
                .ToList();

    /// <summary>The mini-text lines this recorder was sent. The 0x0A body is
    /// <c>type(u8) | len(u16 BE) | ascii[len]</c> — see <c>Session.SendMiniText</c>.</summary>
    private static List<string> MiniTexts(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.MiniText)
                .Where(b => b.Length >= 3)
                .Select(b => System.Text.Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3)))
                .ToList();
}
