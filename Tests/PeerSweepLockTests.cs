using System.Diagnostics;
using System.Reflection;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The rest of the peer-state class #339 and #341 started on: what one player's thread does to, or reads from,
/// another player's state. The source lists are #341's sweep (reports/server-peer-writes-lock-opus.md, "The
/// sweep") and its tier A review (PR341-by-fable.md, F2 and the pre-existing list). The groups, each added with its
/// fix:
/// <list type="number">
/// <item><b>A dialog prompt opened on another player.</b> A prompt is two things: the 0x30 frame the player sees
/// and the pending reply (<c>_dlgReply</c>) their next 0x3A answers. The proposal, the mentorship offer and the
/// wedding's question open them on the OTHER player from the asker's thread, and the wedding's closing box is opened
/// on the bride from the fiancé's thread (the ceremony resumes there). Both halves were written without that
/// player's monitor, while their own read loop reads and clears <c>_dlgReply</c> under it.</item>
/// <item><b>Sends into another player</b> on those paths. No fact here: a send writes nothing a monitor guards
/// (the report says why), so nothing changed.</item>
/// <item><b>Reads of another player</b> without their monitor: the slot checks over the target's buff list, the click
/// profile's legends and gear, the mentor's and the proposer's checks on the other player, and where the other player
/// stands for <c>@approach</c> and the Approach spell.</item>
/// <item><b>Mana Gift's lost regen beat</b>: the gift computed the target's new mana before it took their monitor,
/// so a beat that landed in between was overwritten.</item>
/// </list>
///
/// <para>Shapes, as in <see cref="PeerWritesLockTests"/>: the action through its real entry (the 0x0F cast, the
/// typed name and menu picks as 0x3A replies, the 0x43 click, a Chapel NPC's dialog); the same action while a third
/// thread holds the other player's monitor, in both <c>StateRank</c> orders; a <see cref="StallWatch"/> race, both
/// ways at once where the action allows it; and, Debug only, the entry under <c>World._lock</c> asserts. Where the
/// write is a frame, the recipient's outbound records whether the sending thread held the recipient's monitor at the
/// instant the frame was handed over (<see cref="HeldRecorder"/>, the probe <see cref="PeerMiniTextMonitorTests"/>
/// uses). The races call the handler bodies under the actor's monitor, as the packet handler does, below the action
/// budget, and a dialog is driven from a thread of its own so its awaits resume inline on the thread that delivers
/// the reply, as on the read loop. Content-free maps no other class uses; the wedding stands the bride beside Fen at
/// the Gale Chapel (2511), which no other class uses either.</para>
/// </summary>
[Collection("world")]
public sealed class PeerSweepLockTests
{
    // Content-free maps (no Maps.csv row) that no other class uses: 62430-62453, one line per group.
    private const ushort PromptMap = 62430, PromptHeldMap = 62431, PromptRaceMap = 62432, PromptPinMap = 62433,
                         MentorMap = 62434, MentorHeldMap = 62435, WeddingMap = 62436, WeddingRaceMap = 62437,
                         PromptMidMap = 62453;
    private const ushort SlotRaceMap = 62438, ClickHeldMap = 62440, ClickRaceMap = 62441, AskRaceMap = 62442,
                         ApproachFrom = 62446, ApproachTo = 62447, ApproachSpellFrom = 62448, ApproachSpellTo = 62449,
                         ApproachRaceFrom = 62450, ApproachRaceTo = 62451, ApproachPinMap = 62452;
    private const ushort GiftHeldMap = 62443, GiftRaceMap = 62444, GiftPinMap = 62445;

    /// <summary>Fen, the Gale Chapel's Chapel NPC (game-data/NPCs.csv, NpcId 230, map 2511): the ceremony's real
    /// entry (Server/NpcAbility.cs, ChapelAbility's "Marriage").</summary>
    private const int ChapelNpcId = 230;

    private const int Rounds = 300;
    private const string Ping = "peer sweep race ping";
    private const string Declined = "What but love can last forever?";

    /// <summary>The staff name the approach facts run as: the same name and roster content <c>CommandTableTests</c>
    /// and <c>PeerWritesLockTests</c> write, because <c>StaffAccounts.Load</c> replaces the roster wholesale.</summary>
    private const string GmName = "cmdgm";

    private readonly SessionFixture _fx;
    private static int _serial;
    private static bool _rosterWritten;

    private static readonly MethodInfo ApplyCast =
        typeof(Session).GetMethod("ApplyCast", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo DlgReplyField =
        typeof(Session).GetField("_dlgReply", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public PeerSweepLockTests(SessionFixture fx)
    {
        _fx = fx;
        EnsureGmRoster();
    }

    // ===== 1. a dialog prompt opened on another player =================================================

    /// <summary>A proposal through its real entry: the 0x0F cast of Propose, then the beloved's name typed into the
    /// prompt. The beloved's accept/decline menu is opened inside the beloved's monitor, and accepting it engages
    /// both, as before: the legends, the fiancé fields, the ring taken, a line to each.
    /// <para>Red on 3690542 in both builds: the menu went to the beloved from the proposer's thread, which held the
    /// proposer's monitor and not the beloved's.</para></summary>
    [Fact]
    public void AProposalOpensTheBelovedsPromptInsideTheirMonitor()
    {
        var propose = Content.SpellByKey("propose")!;
        var (b, bOut, _) = Player("Beloved", PromptMap, 22, 20);
        var (p, pOut, _) = Player("Proposer", PromptMap, 20, 20, c => c.Spells.Add(propose.Id));
        try
        {
            GiveRing(b);
            OnOwnThread(() =>
            {
                p.Receive(SpellCastSupport.CastFrame(0));
                Assert.True(p.DialogBusy, "the name prompt is not waiting");
                p.Receive(InputFrame(b.CharName));
            });

            var ask = LastMenu(bOut, $"{p.CharName} proposes marriage. Do you accept?");
            Assert.True(ask.Held, "the proposal was opened on the beloved outside their monitor");
            Assert.Equal(new[] { "Yes! I am madly in love.", "I must decline." }, ask.Labels);
            Assert.True(b.DialogBusy);

            OnOwnThread(() => b.Receive(MenuFrame(1)));
            Assert.False(b.DialogBusy);
            b.WithState(() => Assert.True(b.HasLegend("engaged") && b.CharFiance == p.CharName && b.CharIsProposee
                                          && b.CountItem("engagement_ring") == 0, "the beloved is not engaged"));
            p.WithState(() => Assert.True(p.HasLegend("engaged") && p.CharFiance == b.CharName && !p.CharIsProposee,
                                          "the proposer is not engaged"));
            Assert.Contains($"You are now engaged to {p.CharName}!", bOut.MiniTexts());
            Assert.Contains($"{b.CharName} accepts!", pOut.MiniTexts());
        }
        finally
        {
            Leave(PromptMap, p, b);
        }
    }

    /// <summary>The reproduction that waits: the proposal's last step, as the proposer's read loop runs it (inside the
    /// proposer's monitor), opens nothing on the beloved while a third thread holds the beloved's monitor, in both
    /// <c>StateRank</c> orders; it opens once that thread lets go, and a decline still says both lines.
    /// <para>Red on 3690542 in both builds: the menu was sent and the pending reply replaced while the third thread
    /// held the beloved's monitor.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProposalWaitsForTheBelovedsMonitor(bool proposerRanksFirst)
    {
        Session p, b;
        HeldRecorder pOut, bOut;
        if (proposerRanksFirst)
        {
            (p, pOut, _) = Player("HeldProposer", PromptHeldMap, 20, 20);
            (b, bOut, _) = Player("HeldBeloved", PromptHeldMap, 22, 20);
        }
        else
        {
            (b, bOut, _) = Player("HeldBeloved", PromptHeldMap, 22, 20);
            (p, pOut, _) = Player("HeldProposer", PromptHeldMap, 20, 20);
        }
        Assert.Equal(proposerRanksFirst, p.StateRank < b.StateRank);
        string prompt = $"{p.CharName} proposes marriage. Do you accept?";

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, b, () => p.WithState(() => _ = b.PromptProposal(p)),
                                    untouched: () => !b.DialogBusy && !bOut.HasMenu(prompt));
            AssertWaited(run, "the proposal", "the beloved's prompt was opened while another thread held their monitor");

            Assert.True(b.DialogBusy);
            Assert.True(LastMenu(bOut, prompt).Held);
            OnOwnThread(() => b.Receive(MenuFrame(2)));
            Assert.Contains($"{b.CharName} regretably must decline.", pOut.MiniTexts());
            Assert.Contains(Declined, bOut.MiniTexts());
        }
        finally
        {
            if (run.Settled) Leave(PromptHeldMap, p, b);
        }
    }

    /// <summary>What the swap could do to a player. The beloved is in a conversation of their own and has just picked
    /// "Yes" in it; their read loop is handling that pick (a third thread holds their monitor, as <c>Handle</c> does)
    /// when a proposal arrives. The pick answers the beloved's own menu, and the proposal opens after it and waits for
    /// an answer of its own, in both orders.
    /// <para>Red on 3690542 in both builds: the proposal replaced the pending reply in the middle of the handler, the
    /// pick resolved the PROPOSAL, and the beloved was engaged by a click they made on another box, which itself never
    /// answered.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProposalArrivingWhileTheBelovedAnswersAnotherBoxDoesNotTakeTheirAnswer(bool proposerRanksFirst)
    {
        var (npc, _) = ChapelNpc();   // any NPC can frame the beloved's own menu
        Session p, b;
        HeldRecorder bOut;
        if (proposerRanksFirst)
        {
            (p, _, _) = Player("MidProposer", PromptMidMap, 20, 20);
            (b, bOut, _) = Player("MidBeloved", PromptMidMap, 22, 20);
        }
        else
        {
            (b, bOut, _) = Player("MidBeloved", PromptMidMap, 22, 20);
            (p, _, _) = Player("MidProposer", PromptMidMap, 20, 20);
        }
        Assert.Equal(proposerRanksFirst, p.StateRank < b.StateRank);
        string prompt = $"{p.CharName} proposes marriage. Do you accept?";

        Task<int>? own = null;
        OnOwnThread(() => b.WithState(() => { own = b.DlgMenu(npc, "Will you buy a lotus?", new[] { "Yes", "No" }); }));
        var run = new HeldRun();
        bool ownAnswered = false, engaged = false;
        try
        {
            WhileAnotherThreadHolds(run, b, () => p.WithState(() => _ = b.PromptProposal(p)),
                untouched: () =>
                {
                    b.Receive(MenuFrame(1));   // the pick being handled: "Yes" on the beloved's own menu
                    ownAnswered = own!.IsCompleted && own.Result == 1;
                    engaged = b.HasLegend("engaged");
                    return ownAnswered && !engaged;
                });
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.True(ownAnswered && !engaged, $"the beloved's pick was taken by the proposal: their own menu answered "
                                                + $"{ownAnswered}, engaged {engaged}");
            Assert.False(run.FinishedWhileHeld, "the proposal finished while another thread held the beloved's monitor");

            b.WithState(() => Assert.False(b.HasLegend("engaged") || b.CharFiance.Length > 0, "the beloved was engaged"));
            Assert.True(b.DialogBusy, "the proposal is not waiting for its own answer");
            Assert.True(LastMenu(bOut, prompt).Held);
            OnOwnThread(() => b.Receive(MenuFrame(2)));
            Assert.Contains(Declined, bOut.MiniTexts());
        }
        finally
        {
            if (run.Settled) Leave(PromptMidMap, p, b);
        }
    }

    /// <summary>A mentorship offer through its real entry: the 0x0F cast of Mentor, the protégé's name, the four pages
    /// and the "Are you sure" menu. The protégé's accept/decline menu is opened inside the protégé's monitor, and
    /// accepting binds them as before: the mentor slot, the "being mentored" legend, a line to each.
    /// <para>Red on 3690542 in both builds: the menu went to the protégé from the mentor's thread.</para></summary>
    [Fact]
    public void AMentorshipOfferOpensTheProtegesPromptInsideTheirMonitor()
    {
        var mentor = Content.SpellByKey("mentor")!;
        var (t, tOut, _) = Player("Protege", MentorMap, 22, 20, c => c.Level = 5);
        var (m, mOut, _) = Player("Mentor", MentorMap, 20, 20, c => c.Spells.Add(mentor.Id));
        try
        {
            OnOwnThread(() =>
            {
                m.Receive(SpellCastSupport.CastFrame(0));
                m.Receive(InputFrame(t.CharName));
                for (int i = 0; i < 4; i++) m.Receive(NextFrame());   // the four pages about mentoring
                Assert.True(m.DialogBusy, "the mentor's last question is not waiting");
                m.Receive(MenuFrame(1));                               // "Are you sure ...?" Yes
            });

            var offer = LastMenu(tOut, $"{m.CharName} would like to offer you mentorship. Do you accept?");
            Assert.True(offer.Held, "the offer was opened on the protégé outside their monitor");
            Assert.True(t.DialogBusy);

            OnOwnThread(() => t.Receive(MenuFrame(1)));
            t.WithState(() =>
            {
                Assert.Equal(m.CharName, t.Quest(Mentorship.Key).GetText(Mentorship.MentorSlot));
                Assert.True(t.HasLegend(Mentorship.BeingMentoredLegend));
            });
            Assert.Contains($"{m.CharName} is now your mentor.", tOut.MiniTexts());
            Assert.Contains(mOut.MiniTexts(), l => l.StartsWith($"{t.CharName} accepts your offer of mentorship!"));
        }
        finally
        {
            Leave(MentorMap, m, t);
        }
    }

    /// <summary>The offer's last step, as the mentor's read loop runs it, waits for the protégé's monitor in both
    /// orders, and a decline still tells the mentor. Red on 3690542 in both builds: opened while it was held.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMentorshipOfferWaitsForTheProtegesMonitor(bool mentorRanksFirst)
    {
        Session m, t;
        HeldRecorder mOut, tOut;
        if (mentorRanksFirst)
        {
            (m, mOut, _) = Player("HeldMentor", MentorHeldMap, 20, 20);
            (t, tOut, _) = Player("HeldProtege", MentorHeldMap, 22, 20, c => c.Level = 5);
        }
        else
        {
            (t, tOut, _) = Player("HeldProtege", MentorHeldMap, 22, 20, c => c.Level = 5);
            (m, mOut, _) = Player("HeldMentor", MentorHeldMap, 20, 20);
        }
        Assert.Equal(mentorRanksFirst, m.StateRank < t.StateRank);
        string prompt = $"{m.CharName} would like to offer you mentorship. Do you accept?";

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, t, () => m.WithState(() => _ = t.PromptMentorship(m)),
                                    untouched: () => !t.DialogBusy && !tOut.HasMenu(prompt));
            AssertWaited(run, "the offer", "the protégé's prompt was opened while another thread held their monitor");

            Assert.True(t.DialogBusy);
            OnOwnThread(() => t.Receive(MenuFrame(2)));
            Assert.Contains($"{t.CharName} regretably must decline.", mOut.MiniTexts());
        }
        finally
        {
            if (run.Settled) Leave(MentorHeldMap, m, t);
        }
    }

    /// <summary>The wedding through Fen's real dialog: the bride (the proposee) clicks Fen, picks "Marriage" and
    /// confirms. The ceremony asks the fiancé "Do you, X take Y as your partner?" inside the fiancé's monitor, and a
    /// decline still tells both. Red on 3690542 in both builds: the question went to the fiancé from the bride's
    /// thread, which held the bride's monitor and not the fiancé's.</summary>
    [Fact]
    public void TheWeddingsQuestionOpensInsideTheFiancesMonitor()
    {
        var (npc, def) = ChapelNpc();
        var (fi, fiOut, _) = Player("Fiance", WeddingMap, 20, 20);
        var (br, brOut, _) = Player("Bride", def.Map, (ushort)def.X, (ushort)(def.Y + 2), c => Wide(c, def.Map));
        try
        {
            Engage(br, fi, proposee: true);
            Engage(fi, br, proposee: false);
            OnOwnThread(() => OpenCeremony(br, brOut, npc, def));

            var question = LastMenu(fiOut, $"Do you, {fi.CharName} take {br.CharName} as your partner?");
            Assert.True(question.Held, "the wedding's question was opened on the fiancé outside their monitor");
            Assert.True(fi.DialogBusy);

            OnOwnThread(() => fi.Receive(MenuFrame(2)));   // "I don't."
            Assert.Contains($"{fi.CharName} regretably must decline.", brOut.MiniTexts());
            Assert.Contains(Declined, fiOut.MiniTexts());
            br.WithState(() => Assert.False(br.HasLegend("married")));
        }
        finally
        {
            Leave(def.Map, br);
            Leave(WeddingMap, fi);
        }
    }

    /// <summary>The fiancé's "I do" resumes the ceremony on the FIANCÉ's thread, and the Chapel then shows the bride its
    /// closing box from there. That box is opened inside the bride's monitor; the bride's "next" closes it, and both are
    /// married as before: the legend, the spouse, a love keepsake each, the engagement gone, the line to each.
    /// <para>Red on 3690542 in both builds: the box went to the bride from the fiancé's thread without her
    /// monitor.</para></summary>
    [Fact]
    public void TheWeddingsClosingBoxOpensInsideTheBridesMonitor()
    {
        var (npc, def) = ChapelNpc();
        var (fi, fiOut, _) = Player("Groom", WeddingMap, 24, 20);
        var (br, brOut, _) = Player("Bride", def.Map, (ushort)(def.X + 1), (ushort)(def.Y + 2), c => Wide(c, def.Map));
        try
        {
            Engage(br, fi, proposee: true);
            Engage(fi, br, proposee: false);
            OnOwnThread(() => OpenCeremony(br, brOut, npc, def));
            OnOwnThread(() => fi.Receive(MenuFrame(1)));   // "I do."

            var box = LastBox(brOut, "Congratulations! You are both now married.");
            Assert.True(box.Held, "the Chapel's closing box was opened on the bride outside her monitor");
            Assert.True(br.DialogBusy);
            OnOwnThread(() => br.Receive(NextFrame()));
            Assert.False(br.DialogBusy);

            br.WithState(() => Assert.True(Married(br, fi.CharName), "the bride is not married"));
            fi.WithState(() => Assert.True(Married(fi, br.CharName), "the fiancé is not married"));
            Assert.Contains("I now pronounce you (married)", brOut.MiniTexts());
            Assert.Contains("I now pronounce you (married)", fiOut.MiniTexts());
        }
        finally
        {
            Leave(def.Map, br);
            Leave(WeddingMap, fi);
        }
    }

    /// <summary>Two players open prompts on each other, again and again: each proposes to the other and offers the
    /// other a mentorship, as the asker's read loop does it (inside the asker's monitor), and answers whatever is
    /// waiting on itself with a decline through the real 0x3A; between them each holds its own monitor a fifth of a
    /// millisecond and checks its pending reply did not change, then steps at the other's tile while the world
    /// ticks. Each prompt enters the other player's monitor from inside the asker's, so one direction ascends and the
    /// other descends. Nothing may stall, and no prompt may land inside a player's own section.
    /// <para>Red on 3690542 in both builds: prompts land inside the other's section. It stalls with the prompt
    /// opened under <c>World._lock</c> (one player holds the lock and waits for the other's monitor while the other,
    /// mid-step, holds its own and waits for the lock), and with a plain <c>lock</c> instead of <c>EnterState</c>
    /// (each holds its own and waits for the other's).</para></summary>
    [Fact]
    public void TwoPlayersOpeningPromptsOnEachOtherNeverStall()
    {
        var (a, aOut, _) = Player("PromptRaceA", PromptRaceMap, 10, 10, c => c.Level = 5);
        var (b, bOut, _) = Player("PromptRaceB", PromptRaceMap, 11, 10, c => c.Level = 5);
        var race = new Race();
        int torn = 0;

        void Asker(Session self, Session other)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() =>
                {
                    object? before = DlgReplyField.GetValue(self);
                    Dwell();
                    if (!ReferenceEquals(DlgReplyField.GetValue(self), before)) Interlocked.Increment(ref torn);
                });
                self.WithState(() => _ = other.PromptProposal(self));
                self.Receive(MenuFrame(2));
                self.WithState(() => _ = other.PromptMentorship(self));
                self.Receive(MenuFrame(2));
                Step(self, other, PromptRaceMap);
                race.Progress.Bump();
            }
        }

        try
        {
            RunRace(race, "the two players opening prompts on each other", tick: true,
                    () => Asker(a, b), () => Asker(b, a));

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            Assert.True(aOut.Count(Declined) > 0 && bOut.Count(Declined) > 0, "no proposal was declined both ways");
            Assert.Equal(((ushort)10, (ushort)11), (a.PlayerX, b.PlayerX));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(PromptRaceMap, a, b);
        }
    }

    /// <summary>A wedding cannot run both ways (only the proposee starts one), so its race pairs the bride's question
    /// with the fiancé's party line back into her. Round after round the bride asks, as the Chapel's continuation does
    /// it inside her monitor; the fiancé holds his monitor a fifth of a millisecond and checks his pending reply did
    /// not change, answers "I don't" through the real 0x3A (the ceremony resumes on his thread and tells them both),
    /// and sends the bride a party line. The fiancé is created first, so the bride's question descends. Nothing may
    /// stall; nothing lands inside his section; every decline tells both.
    /// <para>It stalls with the question under <c>World._lock</c> and with a plain <c>lock</c> on the fiancé's monitor
    /// (the bride holds hers and waits for his, while he holds his and waits for hers in the party line).</para></summary>
    [Fact]
    public void ABrideAskingAFianceWhoWritesBackNeverStalls()
    {
        var (fi, fiOut, _) = Player("WedRaceFiance", WeddingRaceMap, 10, 10);
        var (br, brOut, _) = Player("WedRaceBride", WeddingRaceMap, 11, 10);
        Assert.True(fi.StateRank < br.StateRank);
        var race = new Race();
        int torn = 0;
        try
        {
            Engage(br, fi, proposee: true);
            Engage(fi, br, proposee: false);

            RunRace(race, "the bride asking while the fiancé writes back", tick: true,
                () =>
                {
                    for (int i = 0; i < Rounds; i++)
                    {
                        br.WithState(() => _ = br.RunMarriageCeremony());
                        Step(br, fi, WeddingRaceMap);
                        race.Progress.Bump();
                    }
                },
                () =>
                {
                    for (int i = 0; i < Rounds; i++)
                    {
                        fi.WithState(() =>
                        {
                            object? before = DlgReplyField.GetValue(fi);
                            Dwell();
                            if (!ReferenceEquals(DlgReplyField.GetValue(fi), before)) Interlocked.Increment(ref torn);
                        });
                        fi.Receive(MenuFrame(2));
                        fi.WithState(() => br.NotifyGroup(Ping));
                        Step(fi, br, WeddingRaceMap);
                        race.Progress.Bump();
                    }
                });

            Assert.Null(race.Fault);
            Assert.Equal(0, torn);
            int declined = fiOut.Count(Declined);
            Assert.True(declined > 0, "the fiancé never declined");
            Assert.Equal(declined, brOut.Count($"{fi.CharName} regretably must decline."));
            Assert.Equal(Rounds, brOut.Count(Ping));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(WeddingRaceMap, fi, br);
        }
    }

#if DEBUG
    /// <summary>A prompt on another player is opened the ordered way, asserted: with <c>World._lock</c> held the
    /// lock-order assert fires before anything is sent or set waiting; from a lower- and a higher-ranked player's
    /// monitor it is silent and the prompt waits. Debug-only by construction. The assert is raised inside the async
    /// prompt, so it is the prompt's task that carries it.
    /// <para>Red without the fix: with no monitor nothing fires and the prompt opens under the world lock.</para></summary>
    [Fact]
    public void OpeningAPromptOnAnotherPlayerUnderTheWorldLockAsserts()
    {
        var (npc, _) = ChapelNpc();
        var (low, _, _) = Player("PromptPinLow", PromptPinMap, 60, 60);
        var (v, vOut, _) = Player("PromptPinV", PromptPinMap, 62, 60);
        var (high, _, _) = Player("PromptPinHigh", PromptPinMap, 64, 60);
        var labels = new[] { "Yes", "No" };
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);

            Task<int>? wrongWay = null;
            _fx.World.UnderWorldLockForTest(() => { wrongWay = v.DlgMenu(npc, "Pinned under the world lock?", labels); });
            Assert.True(wrongWay!.IsFaulted, "the prompt opened under the world lock without asserting");
            Assert.Contains("lock order violated: World._lock is held while entering a session monitor",
                            wrongWay.Exception!.InnerException!.Message);
            Assert.False(v.DialogBusy);
            Assert.False(vOut.HasMenu("Pinned under the world lock?"));

            Task<int>? fromLow = null, fromHigh = null;
            Assert.Null(Record.Exception(() => low.WithState(() => { fromLow = v.DlgMenu(npc, "Pinned from below?", labels); })));
            Assert.False(fromLow!.IsFaulted);
            Assert.True(v.DialogBusy);
            Assert.Null(Record.Exception(() => high.WithState(() => { fromHigh = v.DlgMenu(npc, "Pinned from above?", labels); })));
            Assert.False(fromHigh!.IsFaulted);
            Assert.True(LastMenu(vOut, "Pinned from above?").Held);
        }
        finally
        {
            Leave(PromptPinMap, low, v, high);
        }
    }
#endif

    // ===== 3. reads of another player's lists ===========================================================

    /// <summary>Two Poets cast Valor at each other again and again while each one's own buff list turns over: a
    /// thread per Poet keeps writing that Poet's own short buffs under that Poet's monitor, as its own casts and the
    /// tick's expiry do, over a list four hundred entries long. The Valor family's slot check reads the TARGET's list from
    /// the caster's thread (<c>HasStatusCategory</c>, then <c>HasStatusFromSpell</c> once the slot holds the last
    /// Valor). Every cast either lands or is refused with "You already cast that spell."; none fails with "Valor isn't
    /// working right now.", which is what an exception inside the verb reads as.
    /// <para>Red on 3690542 in both builds: the walk of the other player's list throws "Collection was modified" when
    /// that player's own write lands under it, and the cast fails.</para></summary>
    [Fact]
    public void TwoPlayersBuffingEachOtherWhileTheirOwnBuffsTurnOverNeverFail()
    {
        var valor = Content.SpellByKey("valor_poet")!;
        var (a, aOut, _) = Poet(SlotRaceMap, 10, 10, valor);
        var (b, bOut, _) = Poet(SlotRaceMap, 11, 10, valor);
        var race = new Race();
        var stat = new[] { "hit" };
        var one = new[] { "1" };
        void Write(Session s, int key)
        {
            s.WithState(() => s.ReceiveTimedBuff(stat, one, 600_000, $"sweep_churn_{key}", "Churn", ""));
            Thread.SpinWait(200);
        }

        try
        {
            const int Buffs = 4 * Ballast, Casts = 3 * Rounds;
            for (int key = 0; key < Buffs; key++) { Write(a, key); Write(b, key); }
            int ka = 0, kb = 0;
            RunWithTurnover(race, "the two Poets buffing each other",
                askers: new Action[]
                {
                    () => { for (int i = 0; i < Casts; i++) { a.WithState(() => Cast(a, valor, b.PlayerId)); race.Progress.Bump(); } },
                    () => { for (int i = 0; i < Casts; i++) { b.WithState(() => Cast(b, valor, a.PlayerId)); race.Progress.Bump(); } },
                },
                turnovers: new Action[] { () => Write(a, ka++ % Buffs), () => Write(b, kb++ % Buffs) });

            Assert.Null(race.Fault);
            var aLines = aOut.MiniTexts();
            var bLines = bOut.MiniTexts();
            Assert.Equal((0, 0), (aLines.Count(l => l == "Valor isn't working right now."),
                                  bLines.Count(l => l == "Valor isn't working right now.")));
            Assert.Equal((Casts - 1, Casts - 1), (aLines.Count(l => l == "You already cast that spell."),
                                                  bLines.Count(l => l == "You already cast that spell.")));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(SlotRaceMap, a, b);
        }
    }

    /// <summary>Clicking another player (the real 0x43) builds their profile card from their character inside their
    /// monitor: it waits while a third thread holds it, in both orders, and the card comes once it is free. Red on
    /// 3690542 in both builds: the card was built and sent while the third thread held the monitor.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AClickOnAnotherPlayerWaitsForTheirMonitor(bool viewerRanksFirst)
    {
        Session v, t;
        HeldRecorder vOut;
        if (viewerRanksFirst)
        {
            (v, vOut, _) = Player("HeldViewer", ClickHeldMap, 20, 20);
            (t, _, _) = Player("HeldClicked", ClickHeldMap, 22, 20);
        }
        else
        {
            (t, _, _) = Player("HeldClicked", ClickHeldMap, 22, 20);
            (v, vOut, _) = Player("HeldViewer", ClickHeldMap, 20, 20);
        }
        Assert.Equal(viewerRanksFirst, v.StateRank < t.StateRank);

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, t, () => v.Receive(ClickFrame(t.PlayerId)),
                                    untouched: () => vOut.Bodies(ServerOp.PeerProfile).Count == 0);
            AssertWaited(run, "the click", "the card was built while another thread held the clicked player's monitor");
            Assert.Single(vOut.Bodies(ServerOp.PeerProfile));
        }
        finally
        {
            if (run.Settled) Leave(ClickHeldMap, v, t);
        }
    }

    /// <summary>Two players click each other's profile again and again while each one's own legends come and go: a
    /// thread per player keeps adding and removing that player's legends under that player's monitor, over a hundred
    /// legends that stay. Every click answers with exactly one card, and every card is whole: everything before the
    /// legends is what a quiet card says, and the legend count is the number of legends that follow, to the last byte.
    /// <para>Red on 3690542 in both builds: a click is lost ("Collection was modified" in the walk, which the handler's
    /// guard turns into a dropped packet), or a card's count disagrees with its list.</para></summary>
    [Fact]
    public void TwoPlayersClickingEachOtherWhileTheirLegendsTurnOverAlwaysGetAWholeCard()
    {
        var (a, aOut, ac) = Player("ClickRaceA", ClickRaceMap, 10, 10, WithBallast);
        var (b, bOut, bc) = Player("ClickRaceB", ClickRaceMap, 11, 10, WithBallast);
        var race = new Race();
        try
        {
            a.Receive(ClickFrame(b.PlayerId));
            b.Receive(ClickFrame(a.PlayerId));
            byte[] quietB = aOut.Bodies(ServerOp.PeerProfile).Single().Body;
            byte[] quietA = bOut.Bodies(ServerOp.PeerProfile).Single().Body;
            int bAt = LegendsOffset(quietB, bc), aAt = LegendsOffset(quietA, ac);
            aOut.Clear();
            bOut.Clear();

            RunWithTurnover(race, "the two players clicking each other",
                askers: new Action[]
                {
                    () => { for (int i = 0; i < 2 * Rounds; i++) { a.Receive(ClickFrame(b.PlayerId)); race.Progress.Bump(); } },
                    () => { for (int i = 0; i < 2 * Rounds; i++) { b.Receive(ClickFrame(a.PlayerId)); race.Progress.Bump(); } },
                },
                turnovers: new Action[] { () => TurnOver(a, ac), () => TurnOver(b, bc) });

            Assert.Null(race.Fault);
            AssertWholeCards(aOut, quietB, bAt, "the first player's view of the second");
            AssertWholeCards(bOut, quietA, aAt, "the second player's view of the first");
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(ClickRaceMap, a, b);
        }
    }

    /// <summary>Two players offer each other a mentorship again and again while each one's legends turn over under
    /// its own monitor (a thread per player, over a hundred legends that stay): each casts Mentor and types the other's
    /// name. The mentor's checks read the protégé's mentor slot, legends and level from the mentor's thread. Every cast
    /// reaches the first of its four pages.
    /// <para>Red on 3690542 in both builds: the walk of the protégé's legends throws "Collection was modified" and the
    /// conversation dies silently (the async flow faults; nobody is told).</para></summary>
    [Fact]
    public void TwoPlayersMentoringEachOtherWhileTheirLegendsTurnOverNeverLoseTheCast()
    {
        var mentor = Content.SpellByKey("mentor")!;
        var (a, aOut, ac) = Player("MentorRaceA", AskRaceMap, 10, 10, c => { c.Level = 5; WithBallast(c); });
        var (b, bOut, bc) = Player("MentorRaceB", AskRaceMap, 11, 10, c => { c.Level = 5; WithBallast(c); });
        var race = new Race();

        void Asker(Session self, Session other)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() => Cast(self, mentor, null));
                self.Receive(InputFrame(other.CharName));
                race.Progress.Bump();
            }
        }

        try
        {
            RunWithTurnover(race, "the two players mentoring each other",
                askers: new Action[] { () => Asker(a, b), () => Asker(b, a) },
                turnovers: new Action[] { () => TurnOver(a, ac), () => TurnOver(b, bc) });

            Assert.Null(race.Fault);
            Assert.Equal((Rounds, Rounds), (aOut.BoxesStarting(FirstMentorPage), bOut.BoxesStarting(FirstMentorPage)));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(AskRaceMap, a, b);
        }
    }

    private const string FirstMentorPage = "Mentoring someone is a great way to show your knowledge of the lands";

    /// <summary>A player proposes again and again to a player whose legends turn over under their own monitor (a
    /// thread of their own, over a hundred legends that stay): the proposer casts Propose and types the beloved's name,
    /// and the proposal's checks read the beloved's legends and ring from the proposer's thread. Every proposal reaches
    /// the beloved's menu. One way only: a proposal opens a prompt on the beloved, which would orphan the beloved's own
    /// name prompt if they were proposing too (both ways is <see cref="TwoPlayersOpeningPromptsOnEachOtherNeverStall"/>'s
    /// job).
    /// <para>Red on 3690542 in both builds: the walk of the beloved's legends throws and the proposal dies
    /// silently.</para></summary>
    [Fact]
    public void AProposalToAPlayerWhoseLegendsTurnOverNeverLosesTheAsking()
    {
        var propose = Content.SpellByKey("propose")!;
        var (p, _, _) = Player("ProposeRaceP", AskRaceMap, 20, 10);
        var (b, bOut, bc) = Player("ProposeRaceB", AskRaceMap, 21, 10, WithBallast);
        var race = new Race();
        try
        {
            GiveRing(b);
            RunWithTurnover(race, "the proposer and the turning-over beloved",
                askers: new Action[]
                {
                    () =>
                    {
                        for (int i = 0; i < Rounds; i++)
                        {
                            p.WithState(() => Cast(p, propose, null));
                            p.Receive(InputFrame(b.CharName));
                            race.Progress.Bump();
                        }
                    },
                },
                turnovers: new Action[] { () => TurnOver(b, bc) });

            Assert.Null(race.Fault);
            Assert.Equal(Rounds, bOut.MenusWith($"{p.CharName} proposes marriage."));
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(AskRaceMap, p, b);
        }
    }

    /// <summary>How many entries the turnover races keep in the list another thread walks, so a walk takes long enough
    /// to meet a write.</summary>
    private const int Ballast = 100;

    /// <summary>A hundred legends that stay, so a walk of the list takes a while.</summary>
    private static void WithBallast(Character c)
    {
        for (int k = 0; k < Ballast; k++) c.Legends.Add(new Legend(1, 1, $"Ballast {k}", "sweep_ballast"));
    }

    /// <summary>One turn of a player's own legends, as their own handler makes it: one added and one removed, each
    /// inside their monitor, then a pause outside it so a thread waiting for the monitor can have it.</summary>
    private static void TurnOver(Session s, Character c)
    {
        s.WithState(() => c.Legends.Add(new Legend(1, 1, "Turnover", "sweep_turnover")));
        s.WithState(() => c.Legends.RemoveAll(l => l.Name == "sweep_turnover"));
        Thread.SpinWait(200);
    }

    /// <summary>A race whose <paramref name="askers"/> run their rounds while each of <paramref name="turnovers"/> runs
    /// again and again on a thread of its own until every asker is done. Only an asker's round counts as progress: a
    /// turnover that keeps running beside two askers stuck on each other must not hide the stall from
    /// <see cref="StallWatch"/>.</summary>
    private void RunWithTurnover(Race race, string what, Action[] askers, Action[] turnovers, bool tick = false)
    {
        int left = askers.Length;
        var workers = askers.Select(ask => (Action)(() =>
            {
                try { ask(); }
                finally { Interlocked.Decrement(ref left); }
            }))
            .Concat(turnovers.Select(turn => (Action)(() =>
            {
                while (Volatile.Read(ref left) > 0 && !race.Done) turn();
            })))
            .ToArray();
        RunRace(race, what, tick, workers);
    }

    /// <summary><c>@approach</c> through the GM's real chat line, at a player caught mid-warp: a third thread holds the
    /// player's monitor and has written the first half of a warp (the new map and its size, as
    /// <c>Session.EnterMap</c> does first) but not yet the tile. The command waits for that monitor, in both orders,
    /// and once the warp is finished and the monitor let go the GM lands beside the player on the new map.
    /// <para>Red on 3690542 in both builds: the command read the new map with the old tile while the warp was half
    /// done, and the GM landed on that tile clamped onto the new map, (19,19), where the player never stood.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnApproachReadsAPlayerMidWarpWhole(bool gmRanksFirst)
    {
        Session gm, t;
        Character gc, tc;
        if (gmRanksFirst)
        {
            (gm, _, gc) = Gm(ApproachFrom, 10, 10);
            (t, _, tc) = Player("WarpingT", ApproachFrom, 90, 90);
        }
        else
        {
            (t, _, tc) = Player("WarpingT", ApproachFrom, 90, 90);
            (gm, _, gc) = Gm(ApproachFrom, 10, 10);
        }
        Assert.Equal(gmRanksFirst, gm.StateRank < t.StateRank);

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, t, () => gm.Receive(ChatFrame($"@approach {t.CharName}")),
                untouched: () =>
                {
                    bool still = gc.Map == ApproachFrom && gc.X == 10 && gc.Y == 10;
                    tc.X = 5; tc.Y = 5;   // the warp's second half: the tile on the new map
                    return still;
                },
                begin: () => { tc.Map = ApproachTo; tc.MapXs = 20; tc.MapYs = 20; });
            AssertWaited(run, "the approach", "the GM moved while the player's warp was half done");
            gm.WithState(() => Assert.True(Beside(gc, ApproachTo, 20, 5, 5),
                $"the GM landed on map {gc.Map} ({gc.MapXs}x{gc.MapYs}) at ({gc.X},{gc.Y})"));
        }
        finally
        {
            if (run.Settled)
            {
                Leave(ApproachFrom, t);
                Leave(gc.Map, gm);
            }
        }
    }

    /// <summary>The Approach spell through the real 0x0F frame with the player's name typed in, at a group member caught
    /// mid-warp the same way. The move waits for the member's monitor, in both orders, and the caster lands beside them
    /// on the new map, paying the spell's mana once.
    /// <para>Red on 3690542 in both builds: the move read the new map with the old tile and landed the caster at
    /// (19,19).</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnApproachSpellReadsAPlayerMidWarpWhole(bool casterRanksFirst)
    {
        var approach = Content.SpellByKey("approach_spell")!;
        Session c, t;
        HeldRecorder tOut;
        Character cc, tc;
        void Shape(Character ch) { ch.Level = 30; ch.Nation = 1; ch.Grouped = true; }
        if (casterRanksFirst)
        {
            (c, _, cc) = Player("ApproachC", ApproachSpellFrom, 10, 10, ch => { Shape(ch); ch.Spells.Add(approach.Id); ch.MaxMp = ch.Mp = 1_000; });
            (t, tOut, tc) = Player("ApproachT", ApproachSpellFrom, 90, 90, Shape);
        }
        else
        {
            (t, tOut, tc) = Player("ApproachT", ApproachSpellFrom, 90, 90, Shape);
            (c, _, cc) = Player("ApproachC", ApproachSpellFrom, 10, 10, ch => { Shape(ch); ch.Spells.Add(approach.Id); ch.MaxMp = ch.Mp = 1_000; });
        }
        Assert.Equal(casterRanksFirst, c.StateRank < t.StateRank);

        var run = new HeldRun();
        try
        {
            SessionFixture.FormParty(c, t);
            Assert.Contains($"{t.CharName} is joining the group.", tOut.MiniTexts());

            WhileAnotherThreadHolds(run, t, () => c.Receive(TypedCastFrame(0, t.CharName)),
                untouched: () =>
                {
                    bool still = cc.Map == ApproachSpellFrom && cc.X == 10 && cc.Y == 10;
                    tc.X = 5; tc.Y = 5;
                    return still;
                },
                begin: () => { tc.Map = ApproachSpellTo; tc.MapXs = 20; tc.MapYs = 20; });
            AssertWaited(run, "the Approach", "the caster moved while the member's warp was half done");
            c.WithState(() =>
            {
                Assert.True(Beside(cc, ApproachSpellTo, 20, 5, 5), $"the caster landed on map {cc.Map} ({cc.MapXs}x{cc.MapYs}) at ({cc.X},{cc.Y})");
                Assert.Equal(1_000u - (uint)Content.FxFor(approach)!.Mana, cc.Mp);
            });
        }
        finally
        {
            if (run.Settled)
            {
                Leave(ApproachSpellFrom, t);
                Leave(cc.Map, c);
            }
        }
    }

    /// <summary>A GM approaches a player again and again while that player warps between a 100x100 map (at 90,90) and a
    /// 20x20 one (at 5,5) on a thread of their own, a third thread keeps sending the GM a party line from inside the
    /// player's monitor, and the world ticks (which holds <c>World._lock</c>, so a warp waits for it between writing
    /// the map and writing the tile). Every
    /// approach that moves the GM lands beside the player on one of the two maps, never at a mix of the two. The player
    /// is created first, so the GM's read descends. An approach that finds the player between maps is refused, as
    /// before ("isn't online"), and is not judged.
    /// <para>Red on 3690542 in both builds: a GM lands at the old tile clamped onto the new map, (19,19), or at the new
    /// tile on the old map, (5,4). It stalls with the read under <c>World._lock</c> (the GM holds the lock and waits for
    /// the player's monitor while the player, mid-warp, holds it and waits for the lock) and with a plain <c>lock</c>
    /// on the player's monitor (the GM holds its own and waits for the player's, while the party line holds the
    /// player's and waits for the GM's).</para></summary>
    [Fact]
    public void AGmApproachingAPlayerWhoWarpsAndWritesBackAlwaysLandsBesideThem()
    {
        const int Approaches = 10_000;
        var (t, _, _) = Player("RaceWarpT", ApproachRaceFrom, 90, 90);
        var (gm, gmOut, gc) = Gm(ApproachRaceFrom, 10, 10);
        Assert.True(t.StateRank < gm.StateRank);
        var race = new Race();
        int moved = 0, mixed = 0;
        var mixes = new List<string>();
        try
        {
            RunWithTurnover(race, "the GM approaching the warping player", tick: true,
                askers: new Action[]
                {
                    () =>
                    {
                        for (int i = 0; i < Approaches; i++)
                        {
                            gmOut.Clear();
                            gm.Receive(ChatFrame($"@approach {t.CharName}"));
                            if (gmOut.Bodies(ServerOp.MapInfo).Count > 0)
                            {
                                moved++;
                                bool whole = false;
                                gm.WithState(() => whole = Beside(gc, ApproachRaceFrom, 100, 90, 90) || Beside(gc, ApproachRaceTo, 20, 5, 5));
                                if (!whole) { mixed++; if (mixes.Count < 5) mixes.Add($"map {gc.Map} ({gc.MapXs}x{gc.MapYs}) at ({gc.X},{gc.Y})"); }
                            }
                            race.Progress.Bump();
                        }
                    },
                },
                turnovers: new Action[]
                {
                    () =>
                    {
                        Warp(t, ApproachRaceTo, 20, 20, 5, 5);
                        Warp(t, ApproachRaceFrom, 100, 100, 90, 90);
                    },
                    () =>
                    {
                        t.WithState(() => gm.NotifyGroup(Ping));
                        Thread.SpinWait(500);
                    },
                });

            Assert.Null(race.Fault);
            Assert.True(moved > Approaches / 10, $"only {moved} of {Approaches} approaches moved the GM");
            Assert.True(mixed == 0, $"{mixed} of {moved} approaches landed at a mix of two places: {string.Join("; ", mixes)}");
        }
        finally
        {
            race.Done = true;
            if (race.Settled)
            {
                Leave(t.CharMap, t);
                Leave(gc.Map, gm);
            }
        }
    }

    /// <summary>Whether <paramref name="c"/> stands on <paramref name="map"/> (square, <paramref name="size"/> a side)
    /// on or beside (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static bool Beside(Character c, ushort map, ushort size, int x, int y) =>
        c.Map == map && c.MapXs == size && c.MapYs == size && Math.Abs(c.X - x) + Math.Abs(c.Y - y) <= 1;

    private static readonly MethodInfo EnterMapMethod =
        typeof(Session).GetMethod("EnterMap", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>A warp as a door or a Gateway makes it, on the player's own thread: <c>Session.EnterMap</c> inside their
    /// monitor.</summary>
    private static void Warp(Session s, ushort map, ushort xs, ushort ys, ushort x, ushort y) =>
        s.WithState(() => EnterMapMethod.Invoke(s, new object[] { map, xs, ys, x, y, $"Sweep {map}", ArrivalPolicy.Clamp }));

#if DEBUG
    /// <summary>A player's place is read the ordered way, asserted: with <c>World._lock</c> held the lock-order assert
    /// fires; from a lower- and a higher-ranked player's monitor it is silent and reads the place. Debug-only by
    /// construction, and it names <c>PlaceForPeer</c>, so it came with the fix.
    /// <para>Red without the fix: with no monitor nothing fires.</para></summary>
    [Fact]
    public void ReadingAnotherPlayersPlaceUnderTheWorldLockAsserts()
    {
        var (low, _, _) = Player("PlacePinLow", ApproachPinMap, 60, 60);
        var (v, _, _) = Player("PlacePinV", ApproachPinMap, 62, 60);
        var (high, _, _) = Player("PlacePinHigh", ApproachPinMap, 64, 60);
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);
            AssertLockOrderFired(Record.Exception(() => _fx.World.UnderWorldLockForTest(() => v.PlaceForPeer())));
            var expected = (ApproachPinMap, (ushort)100, (ushort)100, (ushort)62, (ushort)60);
            Assert.Null(Record.Exception(() => low.WithState(() => Assert.Equal(expected, v.PlaceForPeer()))));
            Assert.Null(Record.Exception(() => high.WithState(() => Assert.Equal(expected, v.PlaceForPeer()))));
        }
        finally
        {
            Leave(ApproachPinMap, low, v, high);
        }
    }
#endif

    // ===== 4. Mana Gift's lost regen beat ===============================================================

    /// <summary>The review's probe P4 as a fact, through the real 0x0F frame: a Mana Gift (Inspire) at another player
    /// waits for that player's monitor; while it waits, the third thread that holds it adds 500 mana, which is what a
    /// regen beat does under that monitor. The gift then lands on top of the beat: 1,000 + 500 + 3,000. The caster
    /// pays the same 3,000 and both read the same lines as any gift.
    /// <para>Red on 3690542 in both builds: the gift wrote the value it computed before it waited, 4,000, and the beat
    /// was lost.</para></summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AManaGiftKeepsARegenBeatThatLandsWhileItWaits(bool casterRanksFirst)
    {
        var gift = Content.SpellByKey("inspire_poet")!;
        Session c, v;
        HeldRecorder cOut, vOut;
        Character cc, vc;
        if (casterRanksFirst)
        {
            (c, cOut, cc) = Poet(GiftHeldMap, 20, 20, gift, mp: 3_000);
            (v, vOut, vc) = Player("GiftHeldV", GiftHeldMap, 22, 20, ch => { ch.MaxMp = 10_000; ch.Mp = 1_000; });
        }
        else
        {
            (v, vOut, vc) = Player("GiftHeldV", GiftHeldMap, 22, 20, ch => { ch.MaxMp = 10_000; ch.Mp = 1_000; });
            (c, cOut, cc) = Poet(GiftHeldMap, 20, 20, gift, mp: 3_000);
        }
        Assert.Equal(casterRanksFirst, c.StateRank < v.StateRank);

        var run = new HeldRun();
        try
        {
            WhileAnotherThreadHolds(run, v, () => c.Receive(SpellCastSupport.CastFrame(0, v.PlayerId)),
                                    untouched: () => { bool same = vc.Mp == 1_000; vc.Mp += 500; return same; });
            AssertWaited(run, "the gift", "the target's mana was written while another thread held its monitor");

            Assert.Equal(4_500u, vc.Mp);
            Assert.Equal(0u, cc.Mp);
            Assert.Contains($"You cast {gift.Name}.", cOut.MiniTexts());
            Assert.Contains(TargetLine(gift, c), vOut.MiniTexts());
        }
        finally
        {
            if (run.Settled) Leave(GiftHeldMap, c, v);
        }
    }

    /// <summary>Two Poets give each other their mana again and again, send each other a party line and step at each
    /// other's tile after each gift, while each one's own regen beats land under its own monitor (a thread per Poet, beating until both have cast
    /// their rounds). A gift moves mana and creates none, and a beat adds exactly what it adds, so at the end the two
    /// pools hold what they started with plus every beat: no beat is lost under a gift. Each gift enters the other's
    /// monitor from inside the caster's and the Lua gate; each party line enters it from inside the sender's, outside
    /// Lua, so the gate does not keep it apart from the other's gift. Nothing may stall.
    /// <para>Red on 3690542 in both builds: a beat that lands between the gift's read of the target's mana and its
    /// write is overwritten. It stalls with the add under <c>World._lock</c> (one Poet holds the lock and waits for the
    /// other's monitor while the other, mid-step, holds its own and waits for the lock) and with a plain <c>lock</c> on
    /// the target's monitor (one Poet's party line holds its own monitor and waits for the other's, while the other,
    /// mid-gift, holds its own and waits for the first's).</para></summary>
    [Fact]
    public void TwoPlayersGivingEachOtherManaKeepEveryRegenBeat()
    {
        var gift = Content.SpellByKey("inspire_poet")!;
        const uint Start = 100_000, Beat = 7;
        var (a, _, ac) = Poet(GiftRaceMap, 10, 10, gift, mp: Start, maxMp: 10_000_000);
        var (b, _, bc) = Poet(GiftRaceMap, 11, 10, gift, mp: Start, maxMp: 10_000_000);
        var race = new Race();
        long beats = 0;

        void Giver(Session self, Session other)
        {
            for (int i = 0; i < Rounds; i++)
            {
                self.WithState(() => Cast(self, gift, other.PlayerId));
                self.WithState(() => other.NotifyGroup(Ping));
                Step(self, other, GiftRaceMap);
                race.Progress.Bump();
            }
        }

        void Regen(Session self, Character mine)
        {
            self.WithState(() => mine.Mp += Beat);
            Interlocked.Increment(ref beats);
            Thread.SpinWait(200);
        }

        try
        {
            RunWithTurnover(race, "the two Poets giving each other mana",
                askers: new Action[] { () => Giver(a, b), () => Giver(b, a) },
                turnovers: new Action[] { () => Regen(a, ac), () => Regen(b, bc) });

            Assert.Null(race.Fault);
            ulong expected = 2UL * Start + (ulong)Interlocked.Read(ref beats) * Beat;
            ulong total = 0;
            a.WithState(() => total += ac.Mp);
            b.WithState(() => total += bc.Mp);
            Assert.Equal(expected, total);
        }
        finally
        {
            race.Done = true;
            if (race.Settled) Leave(GiftRaceMap, a, b);
        }
    }

#if DEBUG
    /// <summary>The gift's add is entered the ordered way, asserted: with <c>World._lock</c> held the lock-order assert
    /// fires before anything is added; from a lower- and a higher-ranked player's monitor it is silent and adds.
    /// Debug-only by construction, and it names <c>ReceiveAddMana</c>, so it came with the fix.
    /// <para>Red without the fix: with no monitor nothing fires.</para></summary>
    [Fact]
    public void AddingToAnotherPlayersManaUnderTheWorldLockAsserts()
    {
        var (low, _, _) = Player("GiftPinLow", GiftPinMap, 60, 60);
        var (v, _, vc) = Player("GiftPinV", GiftPinMap, 62, 60, ch => { ch.MaxMp = 10_000; ch.Mp = 1_000; });
        var (high, _, _) = Player("GiftPinHigh", GiftPinMap, 64, 60);
        try
        {
            Assert.True(low.StateRank < v.StateRank && v.StateRank < high.StateRank);
            AssertLockOrderFired(Record.Exception(() => _fx.World.UnderWorldLockForTest(() => v.ReceiveAddMana(500))));
            Assert.Equal(1_000u, vc.Mp);
            Assert.Null(Record.Exception(() => low.WithState(() => v.ReceiveAddMana(500))));
            Assert.Equal(1_500u, vc.Mp);
            Assert.Null(Record.Exception(() => high.WithState(() => v.ReceiveAddMana(500))));
            Assert.Equal(2_000u, vc.Mp);
        }
        finally
        {
            Leave(GiftPinMap, low, v, high);
        }
    }
#endif

    private static void AssertLockOrderFired(Exception? wrongWay)
    {
        Assert.NotNull(wrongWay);
        Assert.Contains("lock order violated: World._lock is held while entering a session monitor", wrongWay!.Message);
    }

    // ===== the held-monitor run =======================================================================

    /// <summary>What <see cref="WhileAnotherThreadHolds"/> saw. <see cref="Settled"/> is false while either thread may
    /// still be parked on a lock: a fact must then leave its maps as they are.</summary>
    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor and runs <paramref name="begin"/> inside it (a
    /// write it is in the middle of); then <paramref name="act"/> runs on a thread of its own until it finishes or sits
    /// blocked for 100 ms; then, still inside the third thread's section, <paramref name="untouched"/> says whether the
    /// state is as it was. Then the third thread lets go and both are joined.</summary>
    private static void WhileAnotherThreadHolds(HeldRun run, Session target, Action act, Func<bool> untouched,
                                                Action? begin = null)
    {
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            try
            {
                target.WithState(() =>
                {
                    begin?.Invoke();
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

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight, or 10 s have
    /// passed.</summary>
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

    /// <summary>A cast as <c>HandleCast</c> makes it, below the frame: the caller holds the caster's monitor.</summary>
    private static bool Cast(Session s, SpellDef sp, uint? targetId) =>
        (bool)ApplyCast.Invoke(s, new object?[] { sp, targetId, null })!;

    private static string TargetLine(SpellDef sp, Session caster)
    {
        var flavor = Content.TargetTextFor(sp.Key);
        return flavor.Length > 0 ? flavor : $"{caster.CharName} casts {sp.Name} on you.";
    }

    // ===== vows =======================================================================================

    /// <summary>An engagement ring in <paramref name="s"/>'s bag, given under its own monitor.</summary>
    private static void GiveRing(Session s) =>
        s.WithState(() => Assert.True(s.GiveRewardItem("engagement_ring", 1), "no room for the ring"));

    /// <summary><paramref name="self"/>'s half of an engagement to <paramref name="other"/>, as an accepted proposal
    /// leaves it, with the three-day wait already over.</summary>
    private static void Engage(Session self, Session other, bool proposee) =>
        self.WithState(() =>
        {
            self.AddLegend($"Engaged to {other.CharName} ({Character.GameDate})", "engaged", 6, 1);
            self.SetEngaged(other.CharName, proposee, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        });

    /// <summary>Whether <paramref name="s"/> is married to <paramref name="partner"/> exactly as the ceremony leaves
    /// it. Called under its monitor.</summary>
    private static bool Married(Session s, string partner) =>
        s.HasLegend("married") && !s.HasLegend("engaged") && s.CharSpouseName == partner && s.CharFiance == ""
        && s.CountItem("love") == 1;

    /// <summary>Fen and her row. Asserted, not assumed: she must be standing on her map in this world.</summary>
    private (Mob Npc, NpcDef Def) ChapelNpc()
    {
        var def = Content.NpcById(ChapelNpcId);
        Assert.True(def is not null && def.Key == "ChapelNpc", $"NPC {ChapelNpcId} is not a Chapel NPC");
        var npc = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == ChapelNpcId);
        Assert.True(npc is not null, $"{def.Name} is not placed in the world");
        return (npc!, def);
    }

    /// <summary>Click <paramref name="npc"/> (the real 0x43), pick "Marriage" by its label on the wire, and confirm:
    /// the ceremony then asks the fiancé.</summary>
    private static void OpenCeremony(Session s, HeldRecorder o, Mob npc, NpcDef def)
    {
        s.Receive(ClickFrame(npc.Id));
        var labels = LastMenu(o, $"{def.Name}: How can I help you today?").Labels;
        int pick = labels.IndexOf("Marriage") + 1;
        Assert.True(pick > 0, $"no Marriage in [{string.Join(", ", labels)}]");
        s.Receive(MenuFrame((byte)pick));
        LastMenu(o, "Are you certain you wish to devote yourself to this man or woman for life?");
        s.Receive(MenuFrame(1));
    }

    // ===== cards ======================================================================================

    /// <summary>Every card is whole: the bytes before the legends are the quiet card's, and the legend count is the
    /// number of legends that follow, ending on the last byte. One card per click.</summary>
    private static void AssertWholeCards(HeldRecorder o, byte[] quiet, int legendsAt, string what)
    {
        var cards = o.Bodies(ServerOp.PeerProfile);
        int torn = 0;
        foreach (var (body, _) in cards)
        {
            if (body.Length <= legendsAt || !body.AsSpan(0, legendsAt).SequenceEqual(quiet.AsSpan(0, legendsAt)))
            { torn++; continue; }
            int count = body[legendsAt], i = legendsAt + 1, seen = 0;
            while (i + 3 <= body.Length && seen < count) { i += 3 + body[i + 2]; seen++; }
            if (seen != count || i != body.Length) torn++;
        }
        Assert.True(cards.Count == 2 * Rounds && torn == 0,
                    $"{what}: {cards.Count} cards for {2 * Rounds} clicks, {torn} of them not whole");
    }

    /// <summary>Where the legend section starts on a quiet card about <paramref name="about"/>: the count byte, after
    /// which each legend is icon, colour, u8 length and its text, to the end of the card.</summary>
    private static int LegendsOffset(byte[] quiet, Character about)
    {
        int at = quiet.Length - 1 - about.Legends.Sum(l => 3 + Encoding.ASCII.GetByteCount(l.Text));
        Assert.Equal(about.Legends.Count, quiet[at]);
        return at;
    }

    // ===== frames =====================================================================================

    /// <summary>The 0x43 click on an entity: 01 id(u32BE) 00.</summary>
    private static byte[] ClickFrame(uint id) =>
        SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 });

    /// <summary>A command the way the read loop runs one: a framed 0x0E chat line.</summary>
    private static byte[] ChatFrame(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var body = new byte[2 + bytes.Length];
        body[1] = (byte)bytes.Length;
        bytes.CopyTo(body, 2);
        return SessionFixture.Frame(ClientOp.Chat, body);
    }

    /// <summary>The 0x0F cast frame for a type-1 (typed answer) spell: <c>slot+1</c>, the answer, a NUL.</summary>
    private static byte[] TypedCastFrame(int slot, string answer)
    {
        byte[] text = Encoding.ASCII.GetBytes(answer);
        byte[] body = new byte[text.Length + 2];
        body[0] = (byte)(slot + 1);
        text.CopyTo(body, 1);
        return SessionFixture.Frame(ClientOp.Cast, body);
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

    /// <summary>A submitted input box (0x3A kind 4, step 2 at [8], the length at [10], the text from [11]).</summary>
    private static byte[] InputFrame(string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>The last 0x30 menu whose prompt contains <paramref name="prompt"/>: its labels, and whether the
    /// sending thread held the recipient's monitor. After the prompt come the item count and each item as u8 length +
    /// ASCII (<c>Session.SendNpcMenuP</c>).</summary>
    private static (List<string> Labels, bool Held) LastMenu(HeldRecorder o, string prompt)
    {
        byte[] want = Encoding.ASCII.GetBytes(prompt);
        var hit = o.Bodies(ServerOp.NpcDialog).LastOrDefault(f =>
            f.Body.Length > 2 && f.Body[0] == 0x02 && f.Body[1] == 0x02 && f.Body.AsSpan().IndexOf(want) >= 0);
        Assert.True(hit.Body is not null, $"no menu with \"{prompt}\" was sent");
        var body = hit.Body!;
        int promptLen = body[21] << 8 | body[22];
        int i = 23 + promptLen;
        int count = body[i++];
        var labels = new List<string>();
        for (int k = 0; k < count; k++)
        {
            int len = body[i++];
            labels.Add(Encoding.ASCII.GetString(body, i, len));
            i += len;
        }
        return (labels, hit.Held);
    }

    /// <summary>The last 0x30 text box that says exactly <paramref name="text"/>, and whether it was sent inside the
    /// recipient's monitor.</summary>
    private static (byte[] Body, bool Held) LastBox(HeldRecorder o, string text)
    {
        var hit = o.Bodies(ServerOp.NpcDialog).LastOrDefault(f => HeldRecorder.IsBox(f.Body, text));
        Assert.True(hit.Body is not null, $"no box saying \"{text}\" was sent");
        return hit;
    }

    // ===== players ====================================================================================

    /// <summary>A level-50 player on a 100x100 walkable map, whose outbound records whether each frame was handed over
    /// inside their own monitor. <paramref name="shape"/> runs last.</summary>
    private (Session session, HeldRecorder outbound, Character c) Player(string name, ushort map, ushort x, ushort y,
                                                                       Action<Character>? shape = null,
                                                                       bool exactName = false)
    {
        var c = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = exactName ? name : $"{name}{Interlocked.Increment(ref _serial)}",
            Map = map,
            X = x,
            Y = y,
            Level = 50,
            MaxHp = 1_000,
            Hp = 1_000,
        };
        Wide(c, map);
        shape?.Invoke(c);
        var o = new HeldRecorder($"held:{c.Name}");
        var s = new Session(o, 2005, _fx.Store, _fx.World, c);
        o.Owner = s;
        _fx.World.EnterMap(s, map);
        o.Clear();
        return (s, o, c);
    }

    /// <summary>A GM on <paramref name="map"/>. The roster names one account, so every GM session shares its name;
    /// nothing here looks the GM up by it.</summary>
    private (Session session, HeldRecorder outbound, Character c) Gm(ushort map, ushort x, ushort y) =>
        Player(GmName, map, x, y, ch => ch.Level = 99, exactName: true);

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

    /// <summary>A level-99 Poet with <paramref name="sp"/> in slot 0.</summary>
    private (Session session, HeldRecorder outbound, Character c) Poet(ushort map, ushort x, ushort y, SpellDef sp,
                                                                     uint mp = 10_000_000, uint maxMp = 0) =>
        Player("SweepPoet", map, x, y, ch =>
        {
            ch.Level = 99;
            ch.MaxMp = maxMp > 0 ? maxMp : mp;
            ch.Mp = mp;
            ch.Will = 50;
            ch.Spells.Add(sp.Id);
        });

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

    /// <summary>
    /// An outbound that keeps every frame, as <see cref="RecordingOutbound"/> does, and also whether the thread that
    /// handed it over held the recipient's monitor at that instant (<see cref="Session.StateHeld"/>):
    /// <c>Session.Send</c> hands the finished frame over synchronously on the sending thread. Thread-safe.
    /// </summary>
    private sealed class HeldRecorder : IOutbound
    {
        private readonly List<(byte[] Frame, bool Held)> _frames = new();
        private readonly object _gate = new();

        /// <summary>The session this recorder belongs to: set right after the session is built, before anything sends.</summary>
        internal Session? Owner;

        public HeldRecorder(string remote) => Remote = remote;

        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame)
        {
            bool held = Owner is not null && Owner.StateHeld;
            lock (_gate) _frames.Add((frame, held));
            return true;
        }

        public void Close() => Closed = true;

        public void Clear()
        {
            lock (_gate) _frames.Clear();
        }

        /// <summary>The decrypted bodies of every frame carrying <paramref name="opcode"/>, in order, each with whether
        /// it was handed over inside the recipient's monitor.</summary>
        public List<(byte[] Body, bool Held)> Bodies(byte opcode)
        {
            (byte[] Frame, bool Held)[] frames;
            lock (_gate) frames = _frames.ToArray();
            var bodies = new List<(byte[], bool)>();
            foreach (var (frame, held) in frames)
            {
                if (!TkPacket.TryParse(frame, out var pkt, out _) || pkt.Opcode != opcode) continue;
                bodies.Add((TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey), held));
            }
            return bodies;
        }

        /// <summary>Every 0x0A mini-text line, as ASCII.</summary>
        public List<string> MiniTexts() =>
            Bodies(ServerOp.MiniText).Where(b => b.Body.Length >= 3)
                .Select(b => Encoding.ASCII.GetString(b.Body, 3, Math.Min((b.Body[1] << 8) | b.Body[2], b.Body.Length - 3)))
                .ToList();

        /// <summary>How many mini-text lines (any channel) say exactly <paramref name="line"/>.</summary>
        public int Count(string line) => MiniTexts().Count(l => l == line);

        /// <summary>Whether any 0x30 menu carried <paramref name="prompt"/>.</summary>
        public bool HasMenu(string prompt) => MenusWith(prompt) > 0;

        /// <summary>How many 0x30 menus carried <paramref name="prompt"/>.</summary>
        public int MenusWith(string prompt)
        {
            byte[] want = Encoding.ASCII.GetBytes(prompt);
            return Bodies(ServerOp.NpcDialog).Count(f =>
                f.Body.Length > 2 && f.Body[0] == 0x02 && f.Body[1] == 0x02 && f.Body.AsSpan().IndexOf(want) >= 0);
        }

        /// <summary>How many 0x30 text boxes start with <paramref name="start"/>.</summary>
        public int BoxesStarting(string start)
        {
            byte[] want = Encoding.ASCII.GetBytes(start);
            return Bodies(ServerOp.NpcDialog).Count(f =>
                f.Body.Length > 23 && f.Body[0] == 0x00 && f.Body[1] == 0x01 && f.Body.AsSpan(23).StartsWith(want));
        }

        /// <summary>Whether a 0x30 body is a text box saying exactly <paramref name="text"/>: the message ends the body,
        /// after its u16BE length.</summary>
        public static bool IsBox(byte[] body, string text)
        {
            byte[] want = Encoding.ASCII.GetBytes(text);
            int at = body.Length - want.Length;
            return at >= 2 && body[0] == 0x00 && body[1] == 0x01 && (body[at - 2] << 8 | body[at - 1]) == want.Length
                   && body.AsSpan(at).SequenceEqual(want);
        }
    }
}
