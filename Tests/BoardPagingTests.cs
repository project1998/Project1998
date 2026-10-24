using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Boards and the mailbox answer the list request (<c>0x3B</c> sub-2) one page at a time, as RTK does: up to 20
/// entries, newest first (RTK <c>mapif_parse_showposts</c>, <c>LIMIT bcount*20,20</c>).
///
/// <para>The 4.95 request, from a static read of the client (not a live capture), is
/// <c>02 board(u16BE) start(u16BE) F0 00</c>. The window sends start <c>7F FF</c> when it opens or refreshes,
/// and (the oldest id it holds) - 1 when the list is scrolled to the bottom; it stops asking once that is 0,
/// and merges every reply that repeats its board id and mode byte into the open list by id. So a page is the
/// entries whose id is at most start, and <c>7F FF</c>, or a body too short to carry a start, is the top.</para>
///
/// <para>Why these need tests by the "would this fail loudly?" rule: nothing complains about a wrong page. The
/// old whole-list reply wrote the count as <c>(byte)Count</c>, which the 4.95 client reads signed, so 128-255
/// entries drew an empty list and 300 drew 44 rows; a page that skipped or repeated an entry, or a header that
/// stopped repeating the board id, would only show as a list that never grows.</para>
///
/// <para>Board ids 0x7E10-0x7E13 are unknown boards (empty name) that no other fact writes to, so their post
/// ids are 1, 2, 3, ...; each mailbox belongs to a fresh name. Two facts then delete posts and letters, so the
/// ids have gaps: with dense ids a cut by position and the cut by id give the same pages. The board-sign fact uses the one live
/// board-sign row in <c>game-data/BoardLocations.csv</c> (Buya 330, board 5) and reads its expectation back
/// from the database, so it holds whatever else board 5 carries.</para>
/// </summary>
[Collection("world")]
public sealed class BoardPagingTests
{
    private readonly SessionFixture _fx;
    public BoardPagingTests(SessionFixture fx) => _fx = fx;

    private const int Top = 0x7FFF;

    /// <summary>300 posts: the opener gets 300-281, each later request for (oldest - 1) gets the next 20, the
    /// fifteenth page ends on post 1, and a start of 0 gets an empty page that still repeats the board id and
    /// mode byte. Every post arrives exactly once, no count passes 20, and a refresh (<c>7F FF</c>) and our
    /// facts' 3-byte request both get the opener's page again. Red without the change: the opener carries all
    /// 300 rows under a count byte of 44.</summary>
    [Fact]
    public void ABoardOf300PostsOpensWithTheNewest20AndPagesBackToItsFirstPost()
    {
        const int board = 0x7E10;
        var (s, rec) = _fx.Player("BpBoard" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            for (int i = 1; i <= 300; i++)
                Assert.Equal(i, Boards.Post(board, $"A{i}", $"T{i}", "B", 1, 2).Id);

            var open = Parse(Ask(s, rec, Sub2(board, Top)));
            AssertBoardHeader(open, board, rights: 3, name: "");
            Assert.Equal(Desc(300, 281), open.Ids);
            AssertBoardRows(open);

            var pages = ScrollToTheEnd(s, rec, board, open, p =>
            {
                AssertBoardHeader(p, board, rights: 3, name: "");
                AssertBoardRows(p);
            });
            Assert.Equal(15, pages.Count);
            Assert.Equal(Desc(20, 1), pages[^1].Ids);
            Assert.Equal(Desc(300, 1), pages.SelectMany(p => p.Ids));

            var past = Parse(Ask(s, rec, Sub2(board, 0)));
            AssertBoardHeader(past, board, rights: 3, name: "");
            Assert.Empty(past.Ids);

            Assert.Equal(open.Body, Ask(s, rec, Sub2(board, Top)));
            Assert.Equal(open.Body, Ask(s, rec, 2, board >> 8, board & 0xFF));
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>The mailbox the same: 300 letters open with 300-281 under mode byte 4, board 0 and "Mailbox",
    /// page back to letter 1, and a start of 0 gets an empty mailbox page. The mailbox openers — a refresh,
    /// the 3-byte request and sub-9 — all get the top page. Red without the change: all 300 letters under a
    /// count byte of 44.</summary>
    [Fact]
    public void AMailboxOf300LettersOpensWithTheNewest20AndPagesBackToItsFirstLetter()
    {
        var (s, rec, c) = _fx.PlayerWith("BpMail" + Guid.NewGuid().ToString("N")[..8], _ => { });
        try
        {
            for (int i = 1; i <= 300; i++)
                Assert.Equal(i, Mail.Send(c.Name, $"S{i}", $"T{i}", "B", 3, 4).Position);

            var open = Parse(Ask(s, rec, Sub2(0, Top)));
            AssertMailboxHeader(open);
            Assert.Equal(Desc(300, 281), open.Ids);
            AssertMailRows(open);

            var pages = ScrollToTheEnd(s, rec, 0, open, p =>
            {
                AssertMailboxHeader(p);
                AssertMailRows(p);
            });
            Assert.Equal(15, pages.Count);
            Assert.Equal(Desc(20, 1), pages[^1].Ids);
            Assert.Equal(Desc(300, 1), pages.SelectMany(p => p.Ids));

            var past = Parse(Ask(s, rec, Sub2(0, 0)));
            AssertMailboxHeader(past);
            Assert.Empty(past.Ids);

            Assert.Equal(open.Body, Ask(s, rec, Sub2(0, Top)));
            Assert.Equal(open.Body, Ask(s, rec, 2, 0, 0));
            Assert.Equal(open.Body, Ask(s, rec, 9));
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>21 entries: the opener gets 21-2 and the next request (start 1) gets entry 1 alone, on a board
    /// and in the mailbox. Red without the change: the opener carries all 21.</summary>
    [Fact]
    public void TheTwentyFirstEntryComesOnTheNextPage()
    {
        const int board = 0x7E12;
        var (s, rec, c) = _fx.PlayerWith("BpNext" + Guid.NewGuid().ToString("N")[..8], _ => { });
        try
        {
            for (int i = 1; i <= 21; i++)
            {
                Assert.Equal(i, Boards.Post(board, $"A{i}", $"T{i}", "B", 1, 2).Id);
                Assert.Equal(i, Mail.Send(c.Name, $"S{i}", $"T{i}", "B", 3, 4).Position);
            }

            var open = Parse(Ask(s, rec, Sub2(board, Top)));
            AssertBoardHeader(open, board, rights: 3, name: "");
            Assert.Equal(Desc(21, 2), open.Ids);
            var last = Parse(Ask(s, rec, Sub2(board, 1)));
            AssertBoardHeader(last, board, rights: 3, name: "");
            Assert.Equal(new[] { 1 }, last.Ids);
            AssertBoardRows(last);

            var mailOpen = Parse(Ask(s, rec, Sub2(0, Top)));
            AssertMailboxHeader(mailOpen);
            Assert.Equal(Desc(21, 2), mailOpen.Ids);
            var mailLast = Parse(Ask(s, rec, Sub2(0, 1)));
            AssertMailboxHeader(mailLast);
            Assert.Equal(new[] { 1 }, mailLast.Ids);
            AssertMailRows(mailLast);
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>A board whose posts have gaps: 150 posted, then <see cref="DeletedInTheGapFacts"/> removed, which
    /// leaves 105 with ids 4-149. Walked the way the window walks it, every remaining post comes exactly once,
    /// newest first, as the post its id names; every page is full while anything older is left, including the
    /// pages that span the 28-id gap; and the request for start 3 (the oldest left is 4) gets an empty page with
    /// the board's header. With ids 1..N and no gaps, as in the facts above, a page cut by POSITION (skip
    /// "newest id - start" entries, the shape a <c>LIMIT</c> offset would take) gives the same pages as the cut
    /// by id; here it skips posts. Red with that positional cut.</summary>
    [Fact]
    public void ABoardWithDeletedPostsReachesEveryRemainingPostOnce()
    {
        const int board = 0x7E13;
        var (s, rec) = _fx.Player("BpGapB" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            for (int i = 1; i <= 150; i++)
                Assert.Equal(i, Boards.Post(board, $"A{i}", $"T{i}", "B", 1, 2).Id);
            for (int i = 1; i <= 150; i++)
                if (DeletedInTheGapFacts(i)) Assert.True(Boards.Delete(board, i, $"A{i}"));
            var remaining = Boards.PostsFor(board).Select(p => p.Id).ToList();
            Assert.Equal(Enumerable.Range(1, 150).Where(i => !DeletedInTheGapFacts(i)).Reverse(), remaining);
            Assert.Equal((105, 149, 4), (remaining.Count, remaining[0], remaining[^1]));

            var open = Parse(Ask(s, rec, Sub2(board, Top)));
            var pages = ScrollToTheEnd(s, rec, board, open, p =>
            {
                AssertBoardHeader(p, board, rights: 3, name: "");
                AssertBoardRows(p);
            });
            AssertWalkReachesEachOnce(pages, remaining);
            Assert.Equal(new[] { 20, 20, 20, 20, 20, 5, 0 }, pages.Select(p => p.Ids.Count));
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>The mailbox the same: 150 letters, the same ones deleted, 105 left with ids 4-149. Every remaining
    /// letter comes exactly once, newest first; no page is short while older letters are left; start 3 gets an
    /// empty mailbox page. Red with the positional cut.</summary>
    [Fact]
    public void AMailboxWithDeletedLettersReachesEveryRemainingLetterOnce()
    {
        var (s, rec, c) = _fx.PlayerWith("BpGapM" + Guid.NewGuid().ToString("N")[..8], _ => { });
        try
        {
            for (int i = 1; i <= 150; i++)
                Assert.Equal(i, Mail.Send(c.Name, $"S{i}", $"T{i}", "B", 3, 4).Position);
            for (int i = 1; i <= 150; i++)
                if (DeletedInTheGapFacts(i)) Assert.True(Mail.Delete(c.Name, i));
            var remaining = Mail.InboxFor(c.Name).Select(m => m.Position).ToList();
            Assert.Equal(Enumerable.Range(1, 150).Where(i => !DeletedInTheGapFacts(i)).Reverse(), remaining);

            var open = Parse(Ask(s, rec, Sub2(0, Top)));
            var pages = ScrollToTheEnd(s, rec, 0, open, p =>
            {
                AssertMailboxHeader(p);
                AssertMailRows(p);
            });
            AssertWalkReachesEachOnce(pages, remaining);
            Assert.Equal(new[] { 20, 20, 20, 20, 20, 5, 0 }, pages.Select(p => p.Ids.Count));
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>A board or mailbox of 20 or fewer entries sends exactly what it sent before this change, byte for
    /// byte, for the 4.95 opener (<c>7F FF</c>) and for our facts' 3-byte request alike: at every size 0-20 the
    /// reply equals the pre-change builder lines copied below as the oracle. Green before and after the change
    /// by design; it goes red if the page size drops below 20 or the header changes.</summary>
    [Fact]
    public void TwentyOrFewerEntriesSendTheBytesTheyAlwaysSent()
    {
        const int board = 0x7E11;
        var (s, rec, c) = _fx.PlayerWith("BpSame" + Guid.NewGuid().ToString("N")[..8], _ => { });
        try
        {
            for (int n = 0; n <= 20; n++)
            {
                if (n > 0)
                {
                    Boards.Post(board, $"Author{n}", $"Topic number {n}", "Body", (byte)(n % 12 + 1), (byte)n);
                    Mail.Send(c.Name, $"Sender{n}", $"Subject {n}", "Body", (byte)(n % 12 + 1), (byte)n);
                    if (n % 3 == 0) Mail.MarkRead(c.Name, n);   // read and unread rows both
                }
                byte[] oldBoard = OldBoard(board, "", Boards.PostsFor(board));
                byte[] oldMailbox = OldMailbox(Mail.InboxFor(c.Name));
                Assert.Equal(n, oldBoard[5]);
                Assert.Equal(oldBoard, Ask(s, rec, Sub2(board, Top)));
                Assert.Equal(oldBoard, Ask(s, rec, 2, board >> 8, board & 0xFF));
                Assert.Equal(oldMailbox, Ask(s, rec, Sub2(0, Top)));
                Assert.Equal(oldMailbox, Ask(s, rec, 2, 0, 0));
            }
        }
        finally { _fx.World.LeaveMap(s, SessionFixture.HomeMap); }
    }

    /// <summary>The board-sign path (<c>Session.TryOpenBoardSign</c>, a real <c>0x11</c> turn to face north
    /// below the Buya sign) pops board 5 open on its newest 20 posts, with the popup rights byte (2). Red
    /// without the change: every post on the board.</summary>
    [Fact]
    public void TheBoardSignOpensOnTheNewest20Posts()
    {
        const int board = 5;   // game-data/BoardLocations.csv: 330,127,79,5
        var (s, rec) = _fx.Player("BpSign" + Guid.NewGuid().ToString("N")[..8], map: 330, x: 127, y: 80);
        try
        {
            for (int i = 0; i < 25; i++) Boards.Post(board, "Signer", $"Notice {i}", "B", 5, 6);
            var expected = Boards.PostsFor(board).Take(20).Select(p => p.Id).ToList();
            Assert.True(Boards.PostsFor(board).Count > 20);

            rec.Clear();
            s.Receive(SessionFixture.Frame(ClientOp.Turn, new byte[] { 0 }));
            var page = Parse(Assert.Single(rec.BodiesOf(0x31)));
            AssertBoardHeader(page, board, rights: 2, name: "Community Events");
            Assert.Equal(expected, page.Ids);
        }
        finally { _fx.World.LeaveMap(s, 330); }
    }

    // ===== the pre-change builders (the oracle) ======================================================

    /// <summary>The board branch of <c>SendBoardPosts</c> as it was before paging (flags1 3, the menu path),
    /// with only the <c>SendMap</c> turned into a return.</summary>
    private static byte[] OldBoard(int boardId, string name, List<BoardPost> posts)
    {
        var d = new PacketWriter().U8(2).U8(3);
        d.U16BE((ushort)boardId);
        var bn = Ascii(name);
        d.U8((byte)bn.Length);
        d.Bytes(bn);
        d.U8((byte)posts.Count);
        foreach (var p in posts)
        {
            d.U8(0);
            d.U16BE((ushort)p.Id);
            var an = Ascii(p.Author);
            d.U8((byte)an.Length);
            d.Bytes(an);
            d.U8(p.Month);
            d.U8(p.Day);
            var tn = Ascii(p.Topic);
            d.U8((byte)tn.Length);
            d.Bytes(tn);
        }
        return d.ToArray();
    }

    /// <summary>The mailbox branch of <c>SendBoardPosts</c> as it was before paging.</summary>
    private static byte[] OldMailbox(List<MailPost> inbox)
    {
        var d0 = new PacketWriter().U8(4).U8(3);
        d0.U16BE((ushort)0);
        var mbn = Ascii("Mailbox");
        d0.U8((byte)mbn.Length);
        d0.Bytes(mbn);
        d0.U8((byte)inbox.Count);
        foreach (var m in inbox)
        {
            d0.U8(0);
            d0.U16BE((ushort)m.Position);
            var sn = Ascii(m.Sender);
            d0.U8((byte)sn.Length);
            d0.Bytes(sn);
            d0.U8(m.Month);
            d0.U8(m.Day);
            var topic = (m.IsRead ? "" : "* ") + m.Topic;
            var tn0 = Ascii(topic);
            d0.U8((byte)tn0.Length);
            d0.Bytes(tn0);
        }
        return d0.ToArray();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s ?? "");

    // ===== asking and reading ========================================================================

    /// <summary>The 4.95 list request: <c>02 board(u16BE) start(u16BE) F0 00</c>.</summary>
    private static byte[] Sub2(int board, int start) =>
        new byte[] { 2, (byte)(board >> 8), (byte)board, (byte)(start >> 8), (byte)start, 0xF0, 0 };

    /// <summary>Send one <c>0x3B</c> body and return the one <c>0x31</c> body it caused.</summary>
    private static byte[] Ask(Session s, RecordingOutbound rec, params byte[] body)
    {
        rec.Clear();
        s.Receive(SessionFixture.Frame(0x3B, body));
        return Assert.Single(rec.BodiesOf(0x31));
    }

    private static byte[] Ask(Session s, RecordingOutbound rec, int sub, int a, int b) =>
        Ask(s, rec, new[] { (byte)sub, (byte)a, (byte)b });

    /// <summary>What the 4.95 window does as the player keeps scrolling to the bottom: from the opener's page,
    /// ask for (oldest id held - 1) until that is 0. Returns every page, the opener's first. An empty page ends
    /// the walk: it is the answer when nothing older is left (the oldest left is above 1 once low ids are
    /// deleted), and the window gets nothing new by asking again. Every other page must hold 1-20 entries, all at
    /// or below the start, newest first, so a server that ignored the start fails here instead of looping; and a
    /// board needs at most 32767 / 20 requests, so this stops after 1,700.</summary>
    private static List<Page> ScrollToTheEnd(Session s, RecordingOutbound rec, int board, Page open,
        Action<Page> check)
    {
        var pages = new List<Page> { open };
        while (pages[^1].Ids.Count > 0 && pages[^1].Ids[^1] > 1)
        {
            Assert.True(pages.Count < 1700, "the pages never reached the oldest entry");
            int start = pages[^1].Ids[^1] - 1;
            var page = Parse(Ask(s, rec, Sub2(board, start)));
            check(page);
            pages.Add(page);
            if (page.Ids.Count == 0) break;
            Assert.InRange(page.Ids[0], 1, start);
            Assert.Equal(page.Ids.OrderByDescending(id => id), page.Ids);
        }
        return pages;
    }

    /// <summary>The checks for a walk over a list with gaps: the pages together are exactly the remaining ids,
    /// newest first, each once; the walk ends on one empty page exactly when the oldest left is above 1; and no
    /// page is short while anything older is left.</summary>
    private static void AssertWalkReachesEachOnce(List<Page> pages, List<int> remaining)
    {
        Assert.Equal(remaining, pages.SelectMany(p => p.Ids));
        bool endsEmpty = remaining[^1] > 1;
        Assert.Equal(endsEmpty, pages[^1].Ids.Count == 0);
        foreach (var p in endsEmpty ? pages.SkipLast(1) : pages)
        {
            Assert.NotEmpty(p.Ids);
            if (remaining.Any(id => id < p.Ids[^1])) Assert.Equal(20, p.Ids.Count);
        }
    }

    /// <summary>Which of 1-150 the gap facts delete: the three lowest (so the oldest left is 4), 31-58 (28 in a
    /// row, wider than a page), the newest (150, so the newest id is not the count), and every ninth.</summary>
    private static bool DeletedInTheGapFacts(int id) =>
        id <= 3 || (id >= 31 && id <= 58) || id == 150 || id % 9 == 0;

    private sealed record Row(int Id, string Name, byte Month, byte Day, string Topic);
    private sealed record Page(byte Mode, byte Rights, int Board, string Name, List<Row> Rows, byte[] Body)
    {
        public List<int> Ids => Rows.Select(r => r.Id).ToList();
    }

    /// <summary>The reply walked the way the client walks it: the header, the count, then exactly that many
    /// rows, which must end on the body's last byte. The count byte must be what the client reads as
    /// positive.</summary>
    private static Page Parse(byte[] b)
    {
        int at = 0;
        byte U8() => b[at++];
        int U16() { int v = (b[at] << 8) | b[at + 1]; at += 2; return v; }
        string Str8() { int n = U8(); var v = Encoding.ASCII.GetString(b, at, n); at += n; return v; }

        byte mode = U8(), rights = U8();
        int board = U16();
        string name = Str8();
        int count = U8();
        Assert.InRange(count, 0, 20);
        var rows = new List<Row>();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0, U8());
            int id = U16();
            string who = Str8();
            byte month = U8(), day = U8();
            rows.Add(new Row(id, who, month, day, Str8()));
        }
        Assert.Equal(b.Length, at);
        return new Page(mode, rights, board, name, rows, b);
    }

    private static void AssertBoardHeader(Page p, int board, byte rights, string name)
    {
        Assert.Equal(2, p.Mode);
        Assert.Equal(rights, p.Rights);
        Assert.Equal(board, p.Board);
        Assert.Equal(name, p.Name);
    }

    private static void AssertMailboxHeader(Page p)
    {
        Assert.Equal(4, p.Mode);
        Assert.Equal(3, p.Rights);
        Assert.Equal(0, p.Board);
        Assert.Equal("Mailbox", p.Name);
    }

    /// <summary>Each row is the post its id names (author A&lt;id&gt;, topic T&lt;id&gt;), not just the id.</summary>
    private static void AssertBoardRows(Page p)
    {
        foreach (var r in p.Rows)
            Assert.Equal(new Row(r.Id, $"A{r.Id}", 1, 2, $"T{r.Id}"), r);
    }

    /// <summary>Each row is the letter its id names (sender S&lt;id&gt;, unread topic "* T&lt;id&gt;").</summary>
    private static void AssertMailRows(Page p)
    {
        foreach (var r in p.Rows)
            Assert.Equal(new Row(r.Id, $"S{r.Id}", 3, 4, $"* T{r.Id}"), r);
    }

    private static int[] Desc(int from, int to) => Enumerable.Range(to, from - to + 1).Reverse().ToArray();
}
