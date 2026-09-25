using System;
using System.Reflection;
using System.Threading;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

[Collection("world")]
public sealed class GroupKillExpMonitorTests
{
    private readonly SessionFixture _fx;

    public GroupKillExpMonitorTests(SessionFixture fx) => _fx = fx;

    [Fact]
    public void GroupKillPaysEachMemberUnderTheirOwnStateMonitor()
    {
        var (killer, _, killerCharacter) = _fx.PlayerWith("GroupExpKiller", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
        });
        var (member, _, memberCharacter) = _fx.PlayerWith("GroupExpMember", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        FormParty(killer, member);

        var error = Record.Exception(() =>
            killer.WithState(() => killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "monitor_test_mob")));

        Assert.Null(error);
        Assert.Equal((uint)8, killerCharacter.Exp);
        Assert.Equal((uint)8, memberCharacter.Exp);
        Assert.Equal(1, killer.KillCount("monitor_test_mob"));
        Assert.Equal(1, member.KillCount("monitor_test_mob"));
    }

    /// <summary>The descending-rank path. <c>StateRank</c> is allocation order, so building the member
    /// FIRST makes it outrank the killer: when the killer's thread — already holding its own monitor —
    /// enters the member's, <see cref="Session.EnterState"/> has to drop the killer's monitor, take the
    /// member's, and put the killer's back on top (Session.State.cs rule 2). Every other fact here builds
    /// the killer first and so only ever exercises the ascending branch.</summary>
    [Fact]
    public void GroupKillPaysWhenTheMemberOutranksTheKiller()
    {
        var (member, _, memberCharacter) = _fx.PlayerWith("DescendExpMember", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        var (killer, _, killerCharacter) = _fx.PlayerWith("DescendExpKiller", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
        });
        Assert.True(member.StateRank < killer.StateRank);
        FormParty(killer, member);

        var error = Record.Exception(() =>
            killer.WithState(() => killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "descend_test_mob")));

        Assert.Null(error);
        Assert.Equal((uint)8, killerCharacter.Exp);
        Assert.Equal((uint)8, memberCharacter.Exp);
        Assert.Equal(1, killer.KillCount("descend_test_mob"));
        Assert.Equal(1, member.KillCount("descend_test_mob"));
    }

    /// <summary>#168: a group share decided BEFORE a member's teardown, and paid after it, is kept — in the row
    /// the member's logout leaves, and across a fast re-login of the same account. That is Caleb's constraint
    /// ("a plain logout keeps its share"), and the reason the held patch that refused writes after the
    /// teardown was rejected: it lost this share in every order.
    ///
    /// <para>The interleaving is forced (the PR #166 review's H5, and the held patch's test turned round). The
    /// member's thread holds the member's monitor, as its read loop does across the teardown. The killer's
    /// thread decides who is eligible (the member is: alive, grouped, in range) and then blocks entering the
    /// member's monitor for the tally. Only once it is parked there does the member tear down — leave, park the
    /// account's slot, final save — and let go. The killer's thread then tallies and pays the member a share
    /// decided before the teardown. The member is built FIRST so it outranks the killer: the killer's thread
    /// drops its own monitor while it waits (Session.State.cs rule 2), which is what lets the teardown's party
    /// broadcast reach the killer without deadlocking.</para>
    ///
    /// <para>Then the same account logs in again inside the departed window: <c>ClaimAccountSlot</c> fences the
    /// departed member (its kick writes the row once more and latches it) and the load carries the share and
    /// the tally.</para>
    ///
    /// <para>A green run can only come from the forced order: if the teardown ran before the killer decided,
    /// the member would have left the party and been paid nothing.</para>
    ///
    /// <para>Falsified (see the #168 report) by latching <c>_replaced</c> as the teardown's last statement, after
    /// its final save — the held patch's design, expressed through this change's gate: red on the row's exp,
    /// "Expected: 8 / Actual: 0".</para></summary>
    [Fact]
    public void AShareLandingAfterTheMembersTeardownIsKeptAcrossAFastReLogin()
    {
        const string memberName = "LateShareMember";
        string key = CharacterStore.Key(memberName);
        var (member, _, memberCharacter) = _fx.PlayerWith(memberName, c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        var (killer, _, _) = _fx.PlayerWith("LateShareKiller", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
        });
        Assert.True(member.StateRank < killer.StateRank);
        var online = _fx.World.Online;
        var relogin = new Session(new RecordingOutbound($"recorder:{memberName}:relogin"), 2005, _fx.Store, _fx.World);

        try
        {
            online.Register(key, member, out _);   // the member's own arrival claimed the slot
            FormParty(killer, member);

            using var memberHeld = new ManualResetEventSlim(false);
            using var tearDownNow = new ManualResetEventSlim(false);
            Exception? memberError = null, killerError = null;

            var memberThread = new Thread(() => memberError = Record.Exception(() => member.WithState(() =>
            {
                memberHeld.Set();
                tearDownNow.Wait(TimeSpan.FromSeconds(30));
                TearDown.Invoke(member, null);      // re-entrant: the same critical section, as in RunAsync
            }))) { IsBackground = true };
            memberThread.Start();
            Assert.True(memberHeld.Wait(TimeSpan.FromSeconds(30)), "the member's thread never took its monitor");

            var killerThread = new Thread(() => killerError = Record.Exception(() =>
                killer.WithState(() => killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "late_share_mob")))) { IsBackground = true };
            killerThread.Start();

            // Parked on the member's monitor, with the member already in its eligible list.
            Assert.True(SpinWait.SpinUntil(() => (killerThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                                           TimeSpan.FromSeconds(30)), "the killer's thread never blocked on the member");
            tearDownNow.Set();

            Assert.True(memberThread.Join(TimeSpan.FromSeconds(30)), "the member's teardown never finished");
            Assert.True(killerThread.Join(TimeSpan.FromSeconds(30)), "the killer's payout never finished");
            Assert.Null(memberError);
            Assert.Null(killerError);
            Assert.True(online.HoldsDepartedForTest(key, member), "the member's teardown did not park its slot");

            // The late share reached the member's character, after the teardown had written 0 ...
            Assert.Equal((uint)8, memberCharacter.Exp);
            // ... and the row: a plain logout keeps its share.
            var afterLogout = LoadCharacter(memberName);
            Assert.Equal((uint)8, afterLogout.Exp);
            Assert.Equal(1, afterLogout.Kills.GetValueOrDefault("late_share_mob"));

            // A fast re-login: the fence, then the load.
            relogin.WithState(() => relogin.ClaimAccountSlot(memberName));
            Assert.True(member.IsReplaced, "the re-login did not fence the departed member");
            var reloaded = LoadCharacter(memberName);
            Assert.Equal((uint)8, reloaded.Exp);
            Assert.Equal(1, reloaded.Kills.GetValueOrDefault("late_share_mob"));
        }
        finally
        {
            online.Unregister(key, relogin);
            online.Unregister(key, member);
        }
    }

    /// <summary>#168 item 5b, the party-eligibility part: a member a newer login has replaced is skipped by
    /// <c>AwardKillExp</c>'s eligibility loop. It is not paid, it gets no kill credit, and it does not count
    /// toward the group size, so the other two are paid the two-member share (ceil(10 x 0.70339) = 8) rather
    /// than the three-member one (ceil(10 x 0.67339) = 7). Its row is refused anyway since item 2b; this is
    /// what stops it lowering everyone else's share.
    ///
    /// <para>Falsified (see the #168 report) by deleting the <c>IsReplaced</c> skip: red on the killer's exp,
    /// "Expected: 8 / Actual: 7".</para></summary>
    [Fact]
    public void AReplacedMemberIsNotPaidAndDoesNotShrinkTheShare()
    {
        var (killer, _, killerCharacter) = _fx.PlayerWith("ReplacedShareKiller", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
        });
        var (replaced, _, replacedCharacter) = _fx.PlayerWith("ReplacedShareGone", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        var (other, _, otherCharacter) = _fx.PlayerWith("ReplacedShareOther", c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
            c.Grouped = true;
        });
        FormParty(killer, replaced);
        FormParty(killer, other);

        replaced.KickForReplacement();   // the same account logged in elsewhere; it is still in the party

        killer.WithState(() => killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "replaced_share_mob"));

        Assert.Equal((uint)8, killerCharacter.Exp);
        Assert.Equal((uint)8, otherCharacter.Exp);
        Assert.Equal((uint)0, replacedCharacter.Exp);
        Assert.Equal(1, killer.KillCount("replaced_share_mob"));
        Assert.Equal(1, other.KillCount("replaced_share_mob"));
        Assert.Equal(0, replaced.KillCount("replaced_share_mob"));
    }

    [Fact]
    public void SoloKillStillPaysTheFullReward()
    {
        var (killer, _, character) = _fx.PlayerWith("SoloExpKiller", c => c.Totem = 4);

        killer.WithState(() =>
            killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "solo_monitor_test_mob"));

        Assert.Equal((uint)10, character.Exp);
        Assert.Equal(1, killer.KillCount("solo_monitor_test_mob"));
    }

    [Fact]
    public void OutOfRangeMemberGetsNeitherExperienceNorKillCredit()
    {
        var (killer, _, killerCharacter) = _fx.PlayerWith("RangeExpKiller", c => c.Totem = 4);
        var (member, _, memberCharacter) = _fx.PlayerWith("RangeExpMember", c =>
        {
            c.Totem = 4;
            c.Grouped = true;
        }, x: 20);
        FormParty(killer, member);

        killer.WithState(() =>
            killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "range_monitor_test_mob"));

        Assert.Equal((uint)10, killerCharacter.Exp);
        Assert.Equal(1, killer.KillCount("range_monitor_test_mob"));
        Assert.Equal((uint)0, memberCharacter.Exp);
        Assert.Equal(0, member.KillCount("range_monitor_test_mob"));
    }

#if DEBUG
    /// <summary>The two world-thread callers (World.ApplyTrapDamage and World.ApplyMobOnMobHit, both
    /// reached from FlushTick's drain) call <see cref="Session.AwardKillExp"/> holding no session monitor
    /// at all. A SOLO player's pet or trap kill therefore ran the early-out's AwardExp -> SaveChar with
    /// nothing held: Debug FailFast on the state guard, Release write/save race against that player's own
    /// read loop. A bare thread stands in for the tick thread here.</summary>
    [Fact]
    public void SoloKillFromAThreadHoldingNoMonitorPaysTheFullReward()
    {
        var (killer, _, character) = _fx.PlayerWith("BareThreadSoloKiller", c => c.Totem = 4);

        Exception? captured = null;
        var thread = new Thread(() => captured = Record.Exception(() =>
            killer.AwardKillExp(10, SessionFixture.HomeMap, 5, 10, "bare_solo_mob")));
        thread.Start();
        thread.Join();

        Assert.Null(captured);
        Assert.Equal((uint)10, character.Exp);
        Assert.Equal(1, killer.KillCount("bare_solo_mob"));
    }

    [Fact]
    public void BareMemberAwardUnderAnotherSessionMonitorTripsTheGuard()
    {
        var (caller, _) = _fx.Player("BareAwardCaller");
        var (member, _) = _fx.Player("BareAwardMember");

        var error = Record.Exception(() =>
            caller.WithState(() => member.AwardExp(1, killExp: true, totemTime: false)));

        Assert.NotNull(error);
        Assert.Contains("_char mutated session state outside the state monitor", error!.Message);
    }
#endif

    private static void FormParty(Session leader, Session member) => SessionFixture.FormParty(leader, member);

    private static readonly MethodInfo TearDown =
        typeof(Session).GetMethod("TearDownWorldState", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Character LoadCharacter(string name)
    {
        var load = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return Assert.IsType<Character>(load.Character);
    }
}
