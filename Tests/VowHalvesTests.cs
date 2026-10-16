using System.Diagnostics;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// A broken-off engagement or a divorce clears only the vow that names the player who asked for it, and a half whose
/// partner no longer names it back ends at that player's next login, for free (PR #341's tier A review,
/// <c>reviews/PR341-by-fable.md</c>, F1; Caleb, 2026-10-11, "Fix it", with two rules).
///
/// <para><b>Before.</b> The Chapel ends a vow on both sides when the partner is online, and on the clicker's side only
/// when they are offline: the partner's saved row keeps its legend, its fiancé or spouse and its keepsake. Nothing read
/// that row again. So in ordinary play: A divorces B while B is offline, A marries C, and B logs in still married to A.
/// Nobody can propose to B (the proposal refuses anyone holding an "engaged" or "married" legend), and the Chapel
/// charges B the full price to divorce. When B pays while A is online, B's divorce ran A's half of it without asking
/// whom A's half named, and wiped A's marriage to C on A's side only: A ends with no spouse while C stays married to A.
/// The engagement path did the same to a new fiancé.</para>
///
/// <para><b>Now.</b> Rule 1: inside the partner's monitor, the partner's half is cleared only when it names the
/// clicker; otherwise nothing of theirs is touched. The clicker's own half takes the same check, so a confirm that
/// arrives after the partner already ended the vow takes no second keepsake. Rule 2: an arrival whose fiancé or spouse
/// no longer names it back, or no longer exists, ends that half (the legend, the name, the keepsake) without a Chapel
/// visit or a charge, and says so in one status line. The partner is read inside their monitor when they are online
/// and from their saved row through the arrival's own store when they are not. A consistent pair, and a partner whose
/// row cannot be read, are left as they are.</para>
///
/// <para><b>The path.</b> Every player logs in through the real <c>0x10</c> arrival on an isolated database and logs
/// out through the read loop's real exit (<c>Session.EndReadLoopAsync</c>, whose teardown saves). The Chapel is
/// Crystal's dialog on the Crystalline Chapel (map 3815) through the real <c>0x43</c> click and <c>0x3A</c> replies,
/// driven from a thread of its own because the dialog resumes inline on the thread that delivers the reply. The
/// proposal is the real <c>0x0F</c> cast of "propose" and its name box. A vow's starting state is written the way the
/// proposal and the wedding write it (the legend, the name and, for a marriage, one love keepsake), in memory under the
/// player's monitor for an online player and into the row for an offline one.</para>
///
/// <para><b>Collection "world", on a World of its own.</b> The <see cref="SessionFixture"/> is a class fixture, so
/// Crystal on map 3815 is this class's alone. The other players stand on content-free maps 62430-62437, which no other
/// class uses and <c>game-data/Maps.csv</c> has no row for. Each fact has a database of its own
/// (<see cref="IsolatedDatabase"/>).</para>
/// </summary>
[Collection("world")]
public sealed class VowHalvesTests : IClassFixture<SessionFixture>
{
    private const ushort StaleMap = 62430, HeldMap = 62431, ProposeMap = 62432, SceneMap = 62433,
                         WrongVowMap = 62434, ArrivalRaceMap = 62435, UnreadableMap = 62436, HeldArrivalMap = 62437;

    /// <summary>Crystal, the Crystalline Chapel's Chapel NPC (game-data/NPCs.csv, NpcId 397, map 3815, tile 9,9).</summary>
    private const int ChapelNpcId = 397;

    /// <summary>The two tiles beside Crystal the clickers stand on.</summary>
    private static readonly (ushort X, ushort Y) NearCrystal = (9, 11), AlsoNearCrystal = (11, 11);

    private const int ChapelRaceRounds = 100;
    private const int ArrivalRaceRounds = 60;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;
    private static int _serial;

    public VowHalvesTests(SessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>Where the partner a stale half names stands when its owner logs in.</summary>
    public enum PartnerState
    {
        /// <summary>Offline, their row vowed to someone else.</summary>
        MovedOnOffline,
        /// <summary>Offline, their row vowed to nobody.</summary>
        EndedOffline,
        /// <summary>Online, vowed to someone else.</summary>
        MovedOnOnline,
        /// <summary>Online, vowed to nobody.</summary>
        EndedOnline,
        /// <summary>No row: the character was deleted (LoginServer/Admin.cs deletes the row).</summary>
        Deleted,
    }

    // ===== rule 2: a stale half ends at login, for free ==================================================

    /// <summary>
    /// <b>A half whose partner no longer names it back ends at login, for free.</b> B's row is engaged or married to A;
    /// A has moved on to C, or ended it and stayed single, or no longer exists, and is offline or online. B logs in:
    /// B's legend, fiancé or spouse and (a marriage) love keepsake are gone, in memory and in B's saved row, B's
    /// experience is what it was, and B reads one status line naming A. A's and C's rows are byte for byte as they were,
    /// and an online A still names whom it named.
    /// <para>Red on 3690542, Debug and Release: B logs in still engaged or married to A, its row unchanged.</para>
    /// </summary>
    [Theory]
    [InlineData(false, PartnerState.MovedOnOffline)]
    [InlineData(false, PartnerState.EndedOffline)]
    [InlineData(false, PartnerState.MovedOnOnline)]
    [InlineData(false, PartnerState.EndedOnline)]
    [InlineData(false, PartnerState.Deleted)]
    [InlineData(true, PartnerState.MovedOnOffline)]
    [InlineData(true, PartnerState.EndedOffline)]
    [InlineData(true, PartnerState.MovedOnOnline)]
    [InlineData(true, PartnerState.EndedOnline)]
    [InlineData(true, PartnerState.Deleted)]
    public void AStaleHalfEndsAtLoginForFree(bool married, PartnerState state)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B"), c = Name("C");
        bool movedOn = state is PartnerState.MovedOnOffline or PartnerState.MovedOnOnline;
        bool online = state is PartnerState.MovedOnOnline or PartnerState.EndedOnline;
        var aRow = NewCharacter(a, StaleMap, 5, 5);
        var bRow = NewCharacter(b, StaleMap, 7, 5);
        var cRow = NewCharacter(c, StaleMap, 9, 5);
        if (movedOn) { VowRow(aRow, c, married); VowRow(cRow, a, married); }
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow, cRow);
        if (state == PartnerState.Deleted) DeleteRow(db, a);

        Session? aS = null, bS = null;
        try
        {
            if (online) (aS, _) = Arrive(db.Store, a);
            string? aJson = RowJsonOrNull(db, a);
            string cJson = RowJson(db, c);

            (bS, var bOut) = Arrive(db.Store, b);
            string atLogin = VowState(bS);
            var lines = SpellCastSupport.MiniTexts(bOut);
            _out.WriteLine($"[{Kind(married)}, A {state}] B at login: {atLogin}; B's row: {RowVowState(db.Store, b)}; " +
                           $"lines: [{string.Join(" | ", lines)}]");

            Assert.True(VowGone(bS, married), $"B logged in still holding its half: {atLogin}");
            if (married) Assert.Equal(0, bS.WithState(() => bS.CountItem("love")));
            Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row kept its half: {RowVowState(db.Store, b)}");
            Assert.Equal(bRow.Exp, bS.CharExp);
            Assert.Equal(1, lines.Count(l => l == Line(married, a)));

            Assert.Equal(aJson, RowJsonOrNull(db, a));
            Assert.Equal(cJson, RowJson(db, c));
            if (aS is not null)
                Assert.Equal(movedOn ? c : "", aS.WithState(() => married ? aS.CharSpouseName : aS.CharFiance));
        }
        finally
        {
            Release(a, aS);
            Release(b, bS);
        }
    }

    /// <summary>
    /// <b>A consistent pair is left as it is at login.</b> A and B are engaged or married to each other; A is offline
    /// or online. B logs in still holding its whole half, B's row is not written by the arrival (byte for byte as it
    /// was), A's row is byte for byte as it was, and B reads no line. With A online, A's own login made the same check
    /// against B's offline row, and kept A's half too.
    /// <para>Not a guard against the fix's absence (green on 3690542): it is red against a check that ends a half
    /// without asking the partner.</para>
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AConsistentPairIsLeftAsItIsAtLogin(bool married, bool partnerOnline)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, StaleMap, 11, 5);
        var bRow = NewCharacter(b, StaleMap, 13, 5);
        VowRow(aRow, b, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);

        Session? aS = null, bS = null;
        try
        {
            RecordingOutbound? aOut = null;
            if (partnerOnline) (aS, aOut) = Arrive(db.Store, a);
            string aJson = RowJson(db, a), bJson = RowJson(db, b);

            (bS, var bOut) = Arrive(db.Store, b);

            Assert.True(Vowed(bS, a, married, keepsakes: 1), $"B lost its half of a consistent pair: {VowState(bS)}");
            Assert.Equal(bJson, RowJson(db, b));
            Assert.Equal(aJson, RowJson(db, a));
            Assert.DoesNotContain(SpellCastSupport.MiniTexts(bOut), l => l.Contains(" has ended."));
            if (aS is not null)
            {
                Assert.True(Vowed(aS, b, married, keepsakes: 1), $"A lost its half of a consistent pair: {VowState(aS)}");
                Assert.DoesNotContain(SpellCastSupport.MiniTexts(aOut!), l => l.Contains(" has ended."));
            }
        }
        finally
        {
            Release(a, aS);
            Release(b, bS);
        }
    }

    /// <summary>
    /// <b>A partner whose row cannot be read leaves the half as it is.</b> A's row holds JSON the store cannot parse
    /// (<c>CharacterLoadStatus.Unreadable</c>), so there is no evidence either way. B logs in still holding its half,
    /// reads no line, and A's stored JSON is as it was.
    /// <para>Not a guard against the fix's absence (green on 3690542): it is red against a check that reads an
    /// unreadable row as a deleted one.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APartnerWhoseRowCannotBeReadLeavesTheHalf(bool married)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B");
        var aRow = NewCharacter(a, UnreadableMap, 5, 5);
        var bRow = NewCharacter(b, UnreadableMap, 7, 5);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow);
        Exec(db, "UPDATE characters SET json=$j WHERE username=$u;", ("$j", "{\"Name\":"), ("$u", CharacterStore.Key(a)));
        Assert.Equal(CharacterLoadStatus.Unreadable, db.Store.Load(a).Status);
        string aJson = RowJson(db, a), bJson = RowJson(db, b);

        Session? bS = null;
        try
        {
            (bS, var bOut) = Arrive(db.Store, b);

            Assert.True(Vowed(bS, a, married, keepsakes: 1), $"B lost its half on an unreadable partner row: {VowState(bS)}");
            Assert.Equal(bJson, RowJson(db, b));
            Assert.Equal(aJson, RowJson(db, a));
            Assert.DoesNotContain(SpellCastSupport.MiniTexts(bOut), l => l.Contains(" has ended."));
        }
        finally
        {
            Release(b, bS);
        }
    }

    /// <summary>
    /// <b>The arrival reads an online partner inside that partner's monitor, once it is on its own map.</b> A is online
    /// and married (or engaged) to C; B's row still names A. A third thread holds A's monitor while B's arrival runs on a
    /// thread of its own: the arrival waits for it, B's row keeps its half while it waits, and B can already be found by
    /// name (it is on its map), so a Chapel visit of A's at that moment would reach B; once the third thread lets go B's
    /// half ends. Both <c>StateRank</c> orders, so both of <c>EnterState</c>'s branches are the ones that wait. A and B
    /// stand on different maps, so B's own map entry does not reach A.
    /// <para>Red on 3690542, Debug and Release: the arrival never reads A, so it finishes while A's monitor is held,
    /// and B is still vowed after it. Red with the read made before the map entry: B cannot be found while it
    /// waits.</para>
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void TheArrivalReadsAnOnlinePartnerInsideTheirMonitor(bool married, bool arrivingRanksFirst)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B"), c = Name("C");
        var aRow = NewCharacter(a, HeldMap, 5, 5);
        var bRow = NewCharacter(b, HeldArrivalMap, 5, 5);
        var cRow = NewCharacter(c, HeldMap, 7, 5);
        VowRow(aRow, c, married);
        VowRow(cRow, a, married);
        VowRow(bRow, a, married);
        Seed(db, aRow, bRow, cRow);

        Session aS, bS;
        RecordingOutbound bOut;
        if (arrivingRanksFirst)
        {
            (bS, bOut) = NewSession(db.Store, b);
            (aS, _) = Arrive(db.Store, a);
        }
        else
        {
            (aS, _) = Arrive(db.Store, a);
            (bS, bOut) = NewSession(db.Store, b);
        }
        Assert.Equal(arrivingRanksFirst, bS.StateRank < aS.StateRank);

        var run = new HeldRun();
        bool findable = false;
        try
        {
            WhileAnotherThreadHolds(run, aS, () => bS.Receive(ArrivalFrame(b)), untouched: () =>
            {
                findable = _fx.World.Online.FindPlayer(b) is not null;
                return RowVowed(db.Store, b, a, married);
            });
            AssertWaited(run, "the arrival", "B's row lost its half before A's monitor was free");
            Assert.True(findable, "B could not be found by name while it waited to read A: a Chapel visit of A's then misses B");

            Assert.False(bOut.Closed, "the arrival was refused");
            Assert.True(VowGone(bS, married), $"B kept its half after reading A: {VowState(bS)}");
            Assert.Equal(1, SpellCastSupport.MiniTexts(bOut).Count(l => l == Line(married, a)));
            Assert.True(Vowed(aS, c, married, keepsakes: 1), $"A's own vow changed: {VowState(aS)}");
        }
        finally
        {
            if (run.Settled)
            {
                Release(a, aS);
                Release(b, bS);
            }
        }
    }

    /// <summary>
    /// <b>A player who logs in with a stale half can be proposed to.</b> B's row is engaged or married to A, who has
    /// ended it. B logs in holding an engagement ring D gave them, and D casts "propose" (the real <c>0x0F</c>) and
    /// names B in its box: the proposal reaches B ("D proposes marriage. Do you accept?"), D is not told B is
    /// committed, and B's "Yes" engages the two.
    /// <para>Red on 3690542, Debug and Release: D reads "B is already committed to someone else!" and B is never
    /// asked.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStalePlayerCanBeProposedToOnceTheyLogIn(bool married)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B"), d = Name("D");
        var propose = Content.SpellByKey("propose")!;
        var aRow = NewCharacter(a, ProposeMap, 5, 5);
        var bRow = NewCharacter(b, ProposeMap, 7, 5);
        var dRow = NewCharacter(d, ProposeMap, 9, 5);
        VowRow(bRow, a, married);
        GiveRow(bRow, "engagement_ring");
        dRow.Spells.Add(propose.Id);
        int proposeSlot = dRow.Spells.IndexOf(propose.Id);
        Seed(db, aRow, bRow, dRow);

        Session? bHeld = null, dHeld = null;
        try
        {
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            var (dS, dOut) = Arrive(db.Store, d);
            dHeld = dS;
            dOut.Clear();
            bOut.Clear();

            OnOwnThread(() =>
            {
                dS.Receive(SpellCastSupport.CastFrame(proposeSlot));
                Assert.True(SpinWait.SpinUntil(() => dS.DialogBusy, Bound), "the proposal never asked for a name");
                dS.Receive(DialogInputFrame(b));
            });

            var dLines = SpellCastSupport.MiniTexts(dOut);
            _out.WriteLine($"[{Kind(married)}] D's lines: [{string.Join(" | ", dLines)}]");
            Assert.DoesNotContain($"{b} is already committed to someone else!", dLines);
            var answer = LastMenu(bOut, $"{d} proposes marriage. Do you accept?");
            Assert.Equal(2, answer.Count);

            OnOwnThread(() => bS.Receive(MenuFrame(1)));
            Assert.True(Vowed(bS, d, married: false, keepsakes: 0), $"B is not engaged to D: {VowState(bS)}");
            Assert.True(Vowed(dS, b, married: false, keepsakes: 0), $"D is not engaged to B: {VowState(dS)}");
        }
        finally
        {
            Release(b, bHeld);
            Release(d, dHeld);
        }
    }

    // ===== the whole story, through the Chapel and the arrival ===========================================

    /// <summary>
    /// <b>An ex who comes back never ends their former partner's new vow.</b> The review's sequence, end to end: A and
    /// B are engaged (or married); B logs out; A ends it at Crystal's dialog (B's row keeps its half, as the Chapel's
    /// offline path does); A gets engaged (or married) to C; B logs back in and visits Crystal, taking Break Off
    /// Engagement (or Divorce) if she offers it. A and C are still vowed to each other, in memory and in their rows;
    /// B holds nothing of the vow and paid nothing; and Crystal no longer offers B a vow to end.
    /// <para>Red on 3690542, Debug and Release: B is offered the entry, pays the full price, and B's visit clears A's
    /// vow to C on A's side only (A ends with no fiancé or spouse, C still names A).</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExWhoComesBackNeverEndsTheirFormerPartnersNewVow(bool married)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B"), c = Name("C");
        Seed(db, NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y),
                 NewCharacter(b, def.Map, AlsoNearCrystal.X, AlsoNearCrystal.Y),
                 NewCharacter(c, SceneMap, 5, 5));

        Session? aHeld = null, bHeld = null, cHeld = null;
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bFirst, _) = Arrive(db.Store, b);
            bHeld = bFirst;
            var (cS, _) = Arrive(db.Store, c);
            cHeld = cS;
            Vow(aS, b, married);
            Vow(bFirst, a, married);

            Logout(bFirst);
            Release(b, bFirst);
            bHeld = null;
            Assert.True(RowVowed(db.Store, b, a, married), "B's row lost its half at logout");

            OnOwnThread(() =>
            {
                OpenVowEntry(aS, aOut, npc, def, married);
                aS.Receive(MenuFrame(1));
                aS.Receive(NextFrame());
            });
            Assert.Equal(1, Shown(aOut, married ? "You are now divorced." : "It is done."));
            Assert.True(VowGone(aS, married), $"A's own visit did not end A's half: {VowState(aS)}");
            Assert.True(RowVowed(db.Store, b, a, married), "the Chapel's offline path touched B's row");

            Vow(aS, c, married);
            Vow(cS, a, married);
            uint bExp = db.Store.Load(b).Character!.Exp;

            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            string bAtLogin = VowState(bS);
            bool offered = false;
            OnOwnThread(() => offered = OfferedVowEntry(bS, bOut, npc, def, married));
            if (offered)
            {
                OnOwnThread(() =>
                {
                    OpenVowEntry(bS, bOut, npc, def, married);
                    bS.Receive(MenuFrame(1));
                    bS.Receive(NextFrame());
                });
            }
            _out.WriteLine($"[{Kind(married)}] B at login: {bAtLogin}; Crystal offered B the entry: {offered}; after: " +
                           $"A {VowState(aS)}, C {VowState(cS)}, B {VowState(bS)}, B paid {bExp - bS.CharExp}");

            Assert.True(Vowed(aS, c, married, keepsakes: 1), $"A's vow to C was ended: {VowState(aS)}");
            Assert.True(Vowed(cS, a, married, keepsakes: 1), $"C's vow to A was ended: {VowState(cS)}");
            Assert.True(RowVowed(db.Store, a, c, married), $"A's row lost its vow to C: {RowVowState(db.Store, a)}");
            Assert.True(RowVowed(db.Store, c, a, married), $"C's row lost its vow to A: {RowVowState(db.Store, c)}");
            Assert.True(VowGone(bS, married), $"B still holds its half: {VowState(bS)}");
            Assert.True(RowVowGone(db.Store, b, married, keepsakes: 0), $"B's row still holds its half: {RowVowState(db.Store, b)}");
            Assert.Equal(bExp, bS.CharExp);
            Assert.False(offered, "Crystal still offered B a vow to end");
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
            Release(c, cHeld);
        }
    }

    // ===== rule 1: the partner's half is cleared only when it names the clicker ===========================

    /// <summary>
    /// <b>A Chapel visit on a stale half leaves the partner's new vow alone.</b> B, online, still names A (a half that
    /// outlived its login: the partner's row could not be read then, say); A is engaged (or married) to C. B takes Break
    /// Off Engagement (or Divorce) at Crystal: B's half ends and B pays as before, and A, whose half names C, is not
    /// touched: A and C keep their vow, keepsakes and rows.
    /// <para>Red on 3690542, Debug and Release: B's visit runs A's half unconditionally, so A ends with no fiancé or
    /// spouse and no keepsake while C still names A.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChapelVisitOnAStaleHalfLeavesThePartnersNewVowAlone(bool married)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B"), c = Name("C");
        Seed(db, NewCharacter(a, WrongVowMap, 5, 5),
                 NewCharacter(b, def.Map, NearCrystal.X, NearCrystal.Y),
                 NewCharacter(c, WrongVowMap, 7, 5));

        Session? aHeld = null, bHeld = null, cHeld = null;
        try
        {
            var (aS, _) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            var (cS, _) = Arrive(db.Store, c);
            cHeld = cS;
            Vow(bS, a, married);
            Vow(aS, c, married);
            Vow(cS, a, married);
            uint bExp = bS.CharExp;

            OnOwnThread(() =>
            {
                OpenVowEntry(bS, bOut, npc, def, married);
                bS.Receive(MenuFrame(1));
                bS.Receive(NextFrame());
            });
            _out.WriteLine($"[{Kind(married)}] after B's visit: A {VowState(aS)}, C {VowState(cS)}, B {VowState(bS)}");

            Assert.True(Vowed(aS, c, married, keepsakes: 1), $"B's visit ended A's vow to C: {VowState(aS)}");
            Assert.True(RowVowed(db.Store, a, c, married), $"A's row lost its vow to C: {RowVowState(db.Store, a)}");
            Assert.True(Vowed(cS, a, married, keepsakes: 1), $"C's vow changed: {VowState(cS)}");
            Assert.Equal(1, Shown(bOut, married ? "You are now divorced." : "It is done."));
            Assert.True(VowGone(bS, married), $"B's own half was not ended: {VowState(bS)}");
            Assert.Equal(bExp - (married ? 1_000u * 2550 : 100u * 1000), bS.CharExp);
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
            Release(c, cHeld);
        }
    }

    /// <summary>
    /// <b>A confirm that arrives after the partner already ended the marriage takes no second keepsake.</b> A and B are
    /// married and each also carries a love bought from the Chapel's shop: two each. A opens Divorce at Crystal and
    /// leaves its last question open; B divorces at Crystal meanwhile, which ends both halves and takes one keepsake
    /// from each; then A answers "Yes". A's half, already ended, is not ended again: each ends with the bought love,
    /// one each.
    /// <para>Red on 3690542, Debug and Release: A's late "Yes" runs A's own half whatever it holds and takes A's
    /// bought love (A ends with 0). B keeps its own: A's half is empty by then, so the partner looked up by that
    /// empty name is nobody. The partner's side of a crossing is the race below.</para>
    /// <para>What A pays for the late "Yes" is not asserted: the Chapel charges it as before (see the PR's
    /// Follow-ups).</para>
    /// </summary>
    [Fact]
    public void AConfirmAfterThePartnerEndedTheMarriageTakesNoSecondKeepsake()
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        Seed(db, NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y),
                 NewCharacter(b, def.Map, AlsoNearCrystal.X, AlsoNearCrystal.Y));

        Session? aHeld = null, bHeld = null;
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            Vow(aS, b, married: true);
            Vow(bS, a, married: true);
            TopUpLove(aS, 2);
            TopUpLove(bS, 2);
            uint aExp = aS.CharExp;

            OnOwnThread(() => OpenVowEntry(aS, aOut, npc, def, divorce: true));
            OnOwnThread(() =>
            {
                OpenVowEntry(bS, bOut, npc, def, divorce: true);
                bS.Receive(MenuFrame(1));
                bS.Receive(NextFrame());
            });
            Assert.True(VowGone(aS, married: true), $"B's divorce did not end A's half: {VowState(aS)}");
            Assert.Equal(1, aS.WithState(() => aS.CountItem("love")));

            OnOwnThread(() =>
            {
                aS.Receive(MenuFrame(1));
                aS.Receive(NextFrame());
            });
            _out.WriteLine($"after A's late Yes: A {VowState(aS)}, B {VowState(bS)}; A paid {aExp - aS.CharExp}");

            Assert.Equal(1, Shown(aOut, "You are now divorced."));
            Assert.Equal(1, aS.WithState(() => aS.CountItem("love")));
            Assert.Equal(1, bS.WithState(() => bS.CountItem("love")));
            Assert.True(VowGone(aS, married: true));
            Assert.True(VowGone(bS, married: true));
        }
        finally
        {
            Release(a, aHeld);
            Release(b, bHeld);
        }
    }

    // ===== races ===========================================================================================

    /// <summary>
    /// <b>Two partners at the Chapel at once never stall, and each loses one keepsake.</b> A and B stand beside Crystal
    /// engaged (or married) to each other, each with a bought love besides the keepsake. Each round both renew their
    /// half, both open Break Off Engagement (or Divorce) to its last question, and both answer "Yes", each on its own
    /// thread, while the world ticks: at the same moment, or one up to 7 ms after the other (the delay steps by 1 ms
    /// every other round and alternates sides), so the answers both cross and follow one another. Each answer ends its
    /// own half and then the partner's inside the partner's monitor, so one write ascends and the other descends.
    /// Nothing may stall; both end every round with no vow, and (the marriage) with exactly the bought love: one
    /// keepsake taken from each, never two.
    /// <para>Red on 3690542, Debug and Release (the marriage): a second love is taken. When the two answers cross, each
    /// partner's half is run again by the other's answer, whomever it names by then. When one answer finishes first, the
    /// other's own half is run again, whatever it holds. The engagement has no keepsake, so its round ends the same
    /// either way: it pins that nothing stalls.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoPartnersAtTheChapelAtOnceNeverStallAndEachLosesOneKeepsake(bool married)
    {
        using var db = new IsolatedDatabase();
        var (npc, def) = ChapelNpc();
        string a = Name("A"), b = Name("B");
        Seed(db, NewCharacter(a, def.Map, NearCrystal.X, NearCrystal.Y, maxHp: 100),
                 NewCharacter(b, def.Map, AlsoNearCrystal.X, AlsoNearCrystal.Y, maxHp: 100));

        Session? aHeld = null, bHeld = null;
        var race = new Race();
        int wrong = 0;
        string? firstWrong = null;
        try
        {
            var (aS, aOut) = Arrive(db.Store, a);
            aHeld = aS;
            var (bS, bOut) = Arrive(db.Store, b);
            bHeld = bS;
            var barrier = new Barrier(2);

            void Partnered(Session self, RecordingOutbound selfOut, string other, int side)
            {
                for (int i = 0; i < ChapelRaceRounds; i++)
                {
                    selfOut.Clear();
                    Vow(self, other, married);
                    if (married) TopUpLove(self, 2);
                    barrier.SignalAndWait(race.Cancel.Token);
                    OpenVowEntry(self, selfOut, npc, def, married);
                    barrier.SignalAndWait(race.Cancel.Token);
                    if (i % 2 == side) Spin(Stagger(i, stepMs: 1.0));
                    self.Receive(MenuFrame(1));
                    self.Receive(NextFrame());
                    barrier.SignalAndWait(race.Cancel.Token);
                    string state = VowState(self);
                    bool right = VowGone(self, married) && !self.DialogBusy &&
                                 (!married || self.WithState(() => self.CountItem("love")) == 1);
                    if (!right && Interlocked.Increment(ref wrong) == 1) firstWrong = $"round {i}: {self.CharName} {state}";
                    barrier.SignalAndWait(race.Cancel.Token);
                    race.Progress.Bump();
                }
            }

            RunRace(race, married ? "the two spouses divorcing at the Chapel at once" : "the two partners breaking it off at once",
                    () => Partnered(aS, aOut, b, side: 0), () => Partnered(bS, bOut, a, side: 1));
            _out.WriteLine($"[{Kind(married)}] {race.Progress.Rounds} partner-rounds, {wrong} wrong; first: {firstWrong ?? "none"}");

            Assert.Null(race.Fault);
            Assert.Equal(2 * ChapelRaceRounds, race.Progress.Rounds);
            Assert.True(wrong == 0, $"{wrong} partner-rounds ended wrong; the first: {firstWrong}");
        }
        finally
        {
            race.Done = true;
            if (race.Settled)
            {
                Release(a, aHeld);
                Release(b, bHeld);
            }
        }
    }

    /// <summary>
    /// <b>An arrival racing the partner's Chapel visit never stalls, and ends clear.</b> Each round A and B are engaged
    /// (or married) to each other, consistent, with B offline; A logs in. Then, at the same moment, B logs in through
    /// the real arrival on one thread while A ends the vow on another (<c>BreakOffEngagement</c> or
    /// <c>FinishDivorce</c> under A's monitor, as the Chapel's dialog runs it). Nothing may stall, and every round ends
    /// with neither holding the vow, in memory or in their rows, and one keepsake taken from each: whichever reaches B
    /// first, A's visit (B already on its map, so found) or B's own check (A already cleared). The rounds alternate
    /// which of the two sessions was built first, so both <c>StateRank</c> orders are run, and A's visit starts after a
    /// stagger of 0 to 2.8 ms that steps every other round, so it lands before, during and after B's arrival.
    /// <para>Red on 3690542, Debug and Release: when A's visit runs before B is on a map, B keeps its half.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnArrivalRacingThePartnersChapelVisitNeverStallsAndEndsClear(bool married)
    {
        using var db = new IsolatedDatabase();
        string a = Name("A"), b = Name("B");
        var race = new Race();
        int wrong = 0, byVisit = 0, byLogin = 0;
        string? firstWrong = null;
        Session? aS = null, bS = null;

        try
        {
            var barrier = new Barrier(2);

            void BSide()
            {
                for (int i = 0; i < ArrivalRaceRounds; i++)
                {
                    var aRow = NewCharacter(a, ArrivalRaceMap, 5, 5);
                    var bRow = NewCharacter(b, ArrivalRaceMap, 7, 5);
                    VowRow(aRow, b, married);
                    VowRow(bRow, a, married);
                    Seed(db, aRow, bRow);
                    RecordingOutbound bOut;
                    if (i % 2 == 0) { (bS, bOut) = NewSession(db.Store, b); (aS, _) = Arrive(db.Store, a); }
                    else { (aS, _) = Arrive(db.Store, a); (bS, bOut) = NewSession(db.Store, b); }
                    Assert.True(Vowed(aS, b, married, keepsakes: 1), $"round {i}: A's own login ended a consistent pair");

                    barrier.SignalAndWait(race.Cancel.Token);
                    bS.Receive(ArrivalFrame(b));
                    barrier.SignalAndWait(race.Cancel.Token);

                    bool told = SpellCastSupport.MiniTexts(bOut).Count(l => l == Line(married, a)) == 1;
                    bool right = !bOut.Closed
                                 && VowGone(aS, married) && VowGone(bS, married)
                                 && (!married || (aS.WithState(() => aS.CountItem("love")) == 0
                                                  && bS.WithState(() => bS.CountItem("love")) == 0))
                                 && RowVowGone(db.Store, a, married, keepsakes: 0)
                                 && RowVowGone(db.Store, b, married, keepsakes: 0);
                    if (right && told) byLogin++;
                    else if (right) byVisit++;
                    else if (Interlocked.Increment(ref wrong) == 1)
                        firstWrong = $"round {i}: A {VowState(aS)}, B {VowState(bS)}, B's row {RowVowState(db.Store, b)}";

                    Logout(bS);
                    Release(b, bS);
                    Logout(aS);
                    Release(a, aS);
                    (aS, bS) = (null, null);
                    race.Progress.Bump();
                }
            }

            void ASide()
            {
                for (int i = 0; i < ArrivalRaceRounds; i++)
                {
                    barrier.SignalAndWait(race.Cancel.Token);
                    var self = aS!;
                    Spin(Stagger(i));
                    self.WithState(() => { if (married) self.FinishDivorce(); else self.BreakOffEngagement(); });
                    barrier.SignalAndWait(race.Cancel.Token);
                }
            }

            RunRace(race, married ? "a spouse logging in while the other divorces" : "a fiance logging in while the other breaks it off",
                    BSide, ASide);
            _out.WriteLine($"[{Kind(married)}] {race.Progress.Rounds} rounds: B cleared by A's visit {byVisit}, by B's own " +
                           $"login {byLogin}; {wrong} wrong; first: {firstWrong ?? "none"}");

            Assert.Null(race.Fault);
            Assert.Equal(ArrivalRaceRounds, race.Progress.Rounds);
            Assert.True(wrong == 0, $"{wrong} rounds ended wrong; the first: {firstWrong}");
        }
        finally
        {
            race.Done = true;
            if (race.Settled)
            {
                Release(a, aS);
                Release(b, bS);
            }
        }
    }

    // ===== logging in and out ==============================================================================

    private static string Name(string tag) => $"Vh{tag}{Interlocked.Increment(ref _serial)}";

    private static string Kind(bool married) => married ? "marriage" : "engagement";

    /// <summary>The line an arrival reads when its half ends there.</summary>
    private static string Line(bool married, string partner) =>
        married ? $"Your marriage to {partner} has ended." : $"Your engagement to {partner} has ended.";

    /// <summary>A character as creation writes it, standing on <paramref name="map"/>, with experience enough for the
    /// Chapel's prices (a divorce is <c>MaxHp * 2550</c>, a broken engagement <c>MaxMp * 1000</c>).</summary>
    private static Character NewCharacter(string name, ushort map, ushort x, ushort y, uint maxHp = 1_000)
    {
        var c = CharacterFactory.FromCreate(name, new byte[] { 0x29, 0, 1, 0, 0 });
        var info = Content.Maps.GetValueOrDefault(map);
        c.Map = map; c.X = x; c.Y = y;
        c.MapXs = info?.Xs ?? 20; c.MapYs = info?.Ys ?? 20;
        c.Level = 50;
        c.MaxHp = maxHp; c.Hp = maxHp;
        c.MaxMp = 100; c.Mp = 100;
        c.Exp = 1_000_000_000;
        return c;
    }

    private static void Seed(IsolatedDatabase db, params Character[] rows)
    {
        foreach (var c in rows) Assert.True(db.Store.Save(c), $"the setup save of {c.Name} failed");
    }

    /// <summary>Write <paramref name="c"/>'s half of a vow to <paramref name="other"/> into its row: what the proposal
    /// (<c>PromptProposal</c>) or the wedding (<c>RunMarriageCeremony</c>) leaves behind.</summary>
    private static void VowRow(Character c, string other, bool married)
    {
        if (married)
        {
            c.Legends.Add(new Legend(6, 1, $"Married to {other} ({Character.GameDate})", "married"));
            c.Spouse = other;
            GiveRow(c, "love");
        }
        else
        {
            c.Legends.Add(new Legend(6, 1, $"Engaged to {other} ({Character.GameDate})", "engaged"));
            c.Fiance = other;
            c.IsProposee = false;
            c.MarriageTimer = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 259200;
        }
    }

    private static void GiveRow(Character c, string key)
    {
        var def = Content.ItemByKey(key)!;
        byte slot = 0;
        while (c.Inventory.Any(i => i.Slot == slot)) slot++;
        c.Inventory.Add(new InvItem(slot, def.Id, 1));
    }

    /// <summary>The same half, written to an online player under its own monitor, as the proposal and the wedding
    /// write it.</summary>
    private static void Vow(Session self, string other, bool married) =>
        self.WithState(() =>
        {
            if (married)
            {
                self.AddLegend($"Married to {other} ({Character.GameDate})", "married", 6, 1);
                self.SetSpouse(other);
                self.GiveRewardItem("love", 1);
            }
            else
            {
                self.AddLegend($"Engaged to {other} ({Character.GameDate})", "engaged", 6, 1);
                self.SetEngaged(other, isProposee: false, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 259200);
            }
        });

    /// <summary>Loves in the bag up to <paramref name="count"/>: the keepsake, plus any bought from the Chapel's shop
    /// (which sells the same item).</summary>
    private static void TopUpLove(Session s, int count) =>
        s.WithState(() => { while (s.CountItem("love") < count) s.GiveRewardItem("love", 1); });

    private (Session Session, RecordingOutbound Outbound) NewSession(CharacterStore store, string name)
    {
        var outbound = new RecordingOutbound($"recorder:{name}");
        return (new Session(outbound, 2005, store, _fx.World), outbound);
    }

    /// <summary>A session that has logged in through the real <c>0x10</c> arrival.</summary>
    private (Session Session, RecordingOutbound Outbound) Arrive(CharacterStore store, string name)
    {
        var (s, outbound) = NewSession(store, name);
        s.Receive(ArrivalFrame(name));
        Assert.False(outbound.Closed, $"the arrival for '{name}' was refused");
        Assert.Contains(s, _fx.World.Online.All());
        return (s, outbound);
    }

    /// <summary>The read loop's exit on a socket-free session: the teardown leaves the map, parks the slot and saves.
    /// Its only await is on an already-completed writer, so it runs to its end here.</summary>
    private static void Logout(Session s) =>
        Assert.True(s.EndReadLoopAsync(Task.CompletedTask).IsCompletedSuccessfully, "the logout did not run to its end");

    /// <summary>Off the map, and the account's slot out of the registry, so the next login for this name is not handed
    /// this session to fence (its kick would write this session's character over a row the next round seeded).</summary>
    private void Release(string name, Session? s)
    {
        if (s is null) return;
        string key = CharacterStore.Key(name);
        _fx.World.LeaveMap(s, s.CharMap);
        _fx.World.Online.Register(key, s, out _);
        _fx.World.Online.Unregister(key, s);
    }

    private static byte[] ArrivalFrame(string user)
    {
        var body = new List<byte> { 9 };
        body.AddRange(Encoding.ASCII.GetBytes("NexonInc."));
        body.Add((byte)user.Length);
        body.AddRange(Encoding.ASCII.GetBytes(user));
        body.AddRange(HandoffTokens.Mint(user, System.Net.IPAddress.None.ToString()));
        return TkPacket.Build(Opcode.Arrival, 0, body.ToArray());
    }

    // ===== what a player holds ================================================================================

    /// <summary>The whole half of a vow to <paramref name="partner"/>: the legend, the name and, for a marriage,
    /// <paramref name="keepsakes"/> loves. Read under the player's monitor.</summary>
    private static bool Vowed(Session s, string partner, bool married, int keepsakes) =>
        s.WithState(() => married
            ? s.HasLegend("married") && s.CharSpouseName == partner && s.CountItem("love") == keepsakes
            : s.HasLegend("engaged") && s.CharFiance == partner);

    /// <summary>No half of the vow: no legend and no name, and for an engagement no proposee flag or ceremony timer.
    /// The keepsake count is each fact's own business.</summary>
    private static bool VowGone(Session s, bool married) =>
        s.WithState(() => married
            ? !s.HasLegend("married") && s.CharSpouseName == ""
            : !s.HasLegend("engaged") && s.CharFiance == "" && !s.CharIsProposee && s.CharMarriageTimer == 0);

    private static string VowState(Session s) =>
        s.WithState(() => $"[married={s.HasLegend("married")} engaged={s.HasLegend("engaged")} spouse='{s.CharSpouseName}' " +
                          $"fiance='{s.CharFiance}' love={s.CountItem("love")}]");

    private static Character Row(CharacterStore store, string name)
    {
        var load = store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return load.Character!;
    }

    private static int Loves(Character c)
    {
        int id = Content.ItemByKey("love")!.Id;
        return c.Inventory.Where(i => i.ItemId == id).Sum(i => i.Amount);
    }

    private static bool RowVowed(CharacterStore store, string name, string partner, bool married)
    {
        var c = Row(store, name);
        return married
            ? c.Legends.Any(l => l.Name == "married") && c.Spouse == partner && Loves(c) == 1
            : c.Legends.Any(l => l.Name == "engaged") && c.Fiance == partner;
    }

    private static bool RowVowGone(CharacterStore store, string name, bool married, int keepsakes)
    {
        var c = Row(store, name);
        return married
            ? c.Legends.All(l => l.Name != "married") && c.Spouse == "" && Loves(c) == keepsakes
            : c.Legends.All(l => l.Name != "engaged") && c.Fiance == "" && !c.IsProposee && c.MarriageTimer == 0;
    }

    private static string RowVowState(CharacterStore store, string name)
    {
        var c = Row(store, name);
        return $"[legends={string.Join("+", c.Legends.Where(l => l.Name != "").Select(l => l.Name))} spouse='{c.Spouse}' " +
               $"fiance='{c.Fiance}' love={Loves(c)}]";
    }

    // ===== the database ======================================================================================

    private static string RowJson(IsolatedDatabase db, string name) =>
        RowJsonOrNull(db, name) ?? throw new Xunit.Sdk.XunitException($"no row for {name}");

    private static string? RowJsonOrNull(IsolatedDatabase db, string name)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT json FROM characters WHERE username=$u;";
        cmd.Parameters.AddWithValue("$u", CharacterStore.Key(name));
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The row gone, as <c>LoginServer/Admin.cs</c>'s delete leaves it.</summary>
    private static void DeleteRow(IsolatedDatabase db, string name)
    {
        Exec(db, "DELETE FROM characters WHERE username=$u;", ("$u", CharacterStore.Key(name)));
        Assert.Equal(CharacterLoadStatus.NotFound, db.Store.Load(name).Status);
    }

    private static void Exec(IsolatedDatabase db, string sql, params (string Name, object Value)[] args)
    {
        using var cn = db.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ===== the Chapel =========================================================================================

    /// <summary>Crystal and her row. Asserted, not assumed: she must be standing on her map in this world.</summary>
    private (Mob Npc, NpcDef Def) ChapelNpc()
    {
        var def = Content.NpcById(ChapelNpcId);
        Assert.True(def is not null && def.Key == "ChapelNpc", $"NPC {ChapelNpcId} is not a Chapel NPC");
        var npc = _fx.World.NpcsNear(def!.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == ChapelNpcId);
        Assert.True(npc is not null, $"{def.Name} is not placed in the world");
        return (npc!, def);
    }

    private static void Click(Session s, Mob npc)
    {
        uint id = npc.Id;
        s.Receive(SessionFixture.Frame(ClientOp.ClickInfo,
            new byte[] { 0x01, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x00 }));
    }

    /// <summary>Whether Crystal's top menu offers <paramref name="s"/> Break Off Engagement (or Divorce). The menu is
    /// then closed with a pick of 0, which ends the conversation (<c>Session.RunNpcAsync</c>).</summary>
    private static bool OfferedVowEntry(Session s, RecordingOutbound o, Mob npc, NpcDef def, bool divorce)
    {
        Click(s, npc);
        var labels = LastMenu(o, $"{def.Name}: How can I help you today?");
        s.Receive(MenuFrame(0));
        Assert.False(s.DialogBusy, "the top menu did not close");
        return labels.Contains(divorce ? "Divorce" : "Break Off Engagement");
    }

    /// <summary>Click Crystal and walk Break Off Engagement (or Divorce) up to its last question, which is left
    /// waiting: "Are you sure you want to end the engagement?" or "It will cost N experience. Are you sure you want
    /// this divorce?". The entry is found by its label on the wire.</summary>
    private static void OpenVowEntry(Session s, RecordingOutbound o, Mob npc, NpcDef def, bool divorce)
    {
        Click(s, npc);
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
        var body = o.BodiesOf(ServerOp.NpcDialog).LastOrDefault(bd =>
            bd.Length > 2 && bd[0] == 0x02 && bd[1] == 0x02 && bd.AsSpan().IndexOf(tail) >= 0);
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

    /// <summary>A menu pick (0x3A kind 2, the 1-based index at [10]; 0 picks nothing).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>An input box's answer (0x3A kind 4: the length at [10], the text from [11]).</summary>
    private static byte[] DialogInputFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Run a dialog on a thread of its own, which has no synchronization context: the dialog's awaits then
    /// resume inline on the thread that delivers each reply, as on the read loop.</summary>
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

    // ===== the held-monitor run ===============================================================================

    /// <summary>What <see cref="WhileAnotherThreadHolds"/> saw. <see cref="Settled"/> is false while either thread
    /// may still be parked on a lock: a fact must then leave its maps as they are, since cleaning up would wait on
    /// what the stuck thread holds.</summary>
    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a thread
    /// of its own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the state it guards is as it was. Then the third thread lets go and
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
        Assert.False(run.FinishedWhileHeld, $"{what} finished while another thread held the partner's monitor");
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

    // ===== the race ===========================================================================================

    /// <summary>Round <paramref name="round"/>'s head start for the other side: 0 to 7 steps of
    /// <paramref name="stepMs"/>, each step held for two rounds so both sides (or both <c>StateRank</c> orders) run
    /// it.</summary>
    private static TimeSpan Stagger(int round, double stepMs = 0.4) => TimeSpan.FromMilliseconds(round / 2 % 8 * stepMs);

    /// <summary>Busy-wait <paramref name="span"/> on the calling thread, holding whatever it holds.</summary>
    private static void Spin(TimeSpan span)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < span) Thread.SpinWait(20);
    }

    /// <summary>One race: the workers' round counter, the first fault, a cancellation that releases a worker parked
    /// at a barrier when its partner has failed, and whether every thread has finished.</summary>
    private sealed class Race
    {
        public readonly StallWatch.RoundCounter Progress = new();
        public readonly StallWatch.RoundCounter Ticks = new();
        public readonly ManualResetEventSlim Start = new();
        public readonly CancellationTokenSource Cancel = new();
        public Exception? Fault;
        public volatile bool Done;
        public bool Settled = true;

        public void Record(Exception e)
        {
            Interlocked.CompareExchange(ref Fault, e, null);
            Cancel.Cancel();
        }
    }

    /// <summary>Start every worker on its own thread and the world's tick on another, release them together, and fail
    /// on a stall (<see cref="StallWatch"/>) rather than a stopwatch. A worker that throws records the fault and
    /// cancels the barrier its partner may be parked at, so a fault is reported as a fault, not as a stall.</summary>
    private void RunRace(Race race, string what, params Action[] workers)
    {
        race.Settled = false;
        var threads = workers.Select(w => new Thread(() =>
        {
            try { race.Start.Wait(); w(); }
            catch (OperationCanceledException) when (race.Fault is not null) { }
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
        ticker.Start();
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
        StallWatch.RunUntilDoneOrStalled(new[] { ticker }, () => race.Ticks.Rounds,
            StallWatch.StallQuiet, StallWatch.StallCap, "the tick thread");
        race.Settled = true;
    }
}
