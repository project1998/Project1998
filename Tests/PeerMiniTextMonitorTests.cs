using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The cross-session status-text sends outside the party family: clan chat, subpath chat, the parcel notice,
/// the two GM lines, the mentorship line, the Lua world shout, the spell flavour line and the kick line. Each
/// of them composes a line on ONE player's thread and hands it to ANOTHER player's session, and each did so
/// with no monitor at all.
///
/// <para><b>What the monitor is for here.</b> #29 rule 2 (<c>Server/Session.State.cs</c>) is a blanket rule:
/// entering another session's state happens under that session's monitor. It is NOT a torn-byte repair.
/// <c>SendMiniText</c>/<c>SendMessage</c> write the recipient's <c>_gameInc</c>, but that byte is read once
/// and passed BY VALUE both into the encrypted body and into the frame header, so every frame decrypts with
/// the increment it declares and a lost update can only repeat a nonce — recorded as harmless in the note above
/// <c>Session.Send</c> (<c>Server/Session.WorldApi.cs</c>), and visible in <see cref="MonitorProbe"/> below, which decrypts
/// with <c>pkt.Increment</c>. The accesses that were genuinely unguarded are the foreign READS these sites
/// carry on the same path: the recipient's clan/subpath toggles, class and clan names, ignore list, carnage
/// counter and character name.</para>
///
/// <para><b>How "under the monitor" is observed.</b> The send helpers have no seam of their own, so each fact
/// gives the recipient a <see cref="MonitorProbe"/> outbound: <c>Session.Send</c> hands it the finished frame
/// synchronously on the sending thread, so the probe can read <c>Session.StateHeld</c>
/// (<c>Monitor.IsEntered(_state)</c>) at exactly the instant the frame was built. The probe records both
/// channels these sites use — <c>0x0A</c> minitext and the kick's <c>0x02</c> login-box message.</para>
/// </summary>
[Collection("world")]
public sealed class PeerMiniTextMonitorTests
{
    private readonly SessionFixture _fx;

    public PeerMiniTextMonitorTests(SessionFixture fx)
    {
        _fx = fx;
        GmRoster.Ensure();
    }

    // ---- 1. clan chat ----------------------------------------------------------------------------------

    /// <summary>Clan chat ("!" as the whisper target) walks every online player from the SENDER's thread,
    /// reading each one's clan-chat toggle, clan name, character name and ignore list before deciding to
    /// deliver. Those four reads and the send are now one critical section on the recipient. Both halves of
    /// the ignore rule (RTK <c>clif_isignore</c>: either side blocking drops the line) and the recipient's own
    /// clan-chat gate must still hold, and the sender's own echo must still arrive.</summary>
    [Fact]
    public void ClanChatReachesEachClanmateInsideTheirMonitorAndStillSkipsAnIgnoringPair()
    {
        const string Clan = "MonitorClan";
        const string sender = "ClanChatSender", blocker = "ClanChatBlocker";

        var (low, lowProbe, _) = ProbePlayer("ClanChatLow", c => { c.ClanName = Clan; c.ClanChat = true; });
        var (self, selfProbe, _) = ProbePlayer(sender, c => { c.ClanName = Clan; c.ClanChat = true; });
        var (high, highProbe, _) = ProbePlayer("ClanChatHigh", c => { c.ClanName = Clan; c.ClanChat = true; });
        // The sender ignores this one; this one ignores the sender. Both directions must drop the line.
        var (ignoredBySender, ignoredProbe, _) =
            ProbePlayer("ClanChatIgnored", c => { c.ClanName = Clan; c.ClanChat = true; });
        var (blocks, blockerProbe, _) =
            ProbePlayer(blocker, c => { c.ClanName = Clan; c.ClanChat = true; c.IgnoreList.Add(sender); });
        // In the clan but with clan chat OFF, and in no clan at all: neither may hear it.
        var (off, offProbe, _) = ProbePlayer("ClanChatOff", c => { c.ClanName = Clan; c.ClanChat = false; });
        var (outsider, outsiderProbe, _) = ProbePlayer("ClanChatOutsider", c => { c.ClanChat = true; });

        // Built in rank order on purpose: the one loop DESCENDS into `low` and ASCENDS into everyone after the
        // sender, so both branches of rule 2 are exercised by a single send.
        Assert.True(low.StateRank < self.StateRank && self.StateRank < high.StateRank);

        SetIgnoreList(self, ignoredBySender.CharName);
        foreach (var p in new[] { lowProbe, selfProbe, highProbe, ignoredProbe, blockerProbe, offProbe, outsiderProbe })
            p.Clear();

        self.Receive(ClanChatFrame("field is clear"));   // the real "!" whisper, on this thread

        string line = $"<!{sender}>";
        Assert.Equal(1, lowProbe.Count(line));
        Assert.Equal(1, highProbe.Count(line));
        Assert.Equal(1, selfProbe.Count(line));          // the sender's own echo (you cannot ignore yourself)
        Assert.True(lowProbe.AllHeld(line), lowProbe.Explain(line));
        Assert.True(highProbe.AllHeld(line), highProbe.Explain(line));
        Assert.True(selfProbe.AllHeld(line), selfProbe.Explain(line));

        // Recipients unchanged: both ignore directions, the clan-chat-off clanmate and the outsider stay out.
        Assert.Equal(0, ignoredProbe.Count(line));
        Assert.Equal(0, blockerProbe.Count(line));
        Assert.Equal(0, offProbe.Count(line));
        Assert.Equal(0, outsiderProbe.Count(line));

        // Line text unchanged: RTK's "<!<name>> (<class>) <message>".
        Assert.Equal($"<!{sender}> (Peasant) field is clear", lowProbe.Only(line));
    }

    // ---- 2. subpath chat -------------------------------------------------------------------------------

    /// <summary>"/sp &lt;msg&gt;" walks every online player from the SENDER's thread and reads two fields off
    /// each — their subpath-chat toggle and their class name — before delivering. Those reads and the send are
    /// now one critical section on the recipient; who hears it is unchanged.
    /// <para>The players are Muses (a PC subpath) and the outsider a Spy: since the PC-subpath foundation only
    /// the twelve PC subpaths may speak on the channel (Paths.csv PthChat, as RTK), so the Poets this used
    /// first would now be refused before the loop is reached.</para></summary>
    [Fact]
    public void SubpathChatReachesEachSamePathPlayerInsideTheirMonitor()
    {
        const string sender = "SubpathSender";
        var (low, lowProbe, _) = ProbePlayer("SubpathLow", c => { c.ClassName = "Muse"; c.SubpathChat = true; });
        var (self, selfProbe, _) = ProbePlayer(sender, c => { c.ClassName = "Muse"; c.SubpathChat = true; });
        var (high, highProbe, _) = ProbePlayer("SubpathHigh", c => { c.ClassName = "Muse"; c.SubpathChat = true; });
        var (off, offProbe, _) = ProbePlayer("SubpathOff", c => { c.ClassName = "Muse"; c.SubpathChat = false; });
        var (other, otherProbe, _) = ProbePlayer("SubpathOther", c => { c.ClassName = "Spy"; c.SubpathChat = true; });
        Assert.True(low.StateRank < self.StateRank && self.StateRank < high.StateRank);

        foreach (var p in new[] { lowProbe, selfProbe, highProbe, offProbe, otherProbe }) p.Clear();

        self.Receive(SayFrame("/sp field is clear"));

        string line = $"<@{sender}>";
        Assert.Equal(1, lowProbe.Count(line));
        Assert.Equal(1, highProbe.Count(line));
        Assert.Equal(1, selfProbe.Count(line));
        Assert.True(lowProbe.AllHeld(line), lowProbe.Explain(line));
        Assert.True(highProbe.AllHeld(line), highProbe.Explain(line));
        Assert.True(selfProbe.AllHeld(line), selfProbe.Explain(line));

        Assert.Equal(0, offProbe.Count(line));     // toggle off
        Assert.Equal(0, otherProbe.Count(line));   // different path
        Assert.Equal($"<@{sender}> (Muse) field is clear", lowProbe.Only(line));
    }

    // ---- 3. the parcel notice --------------------------------------------------------------------------

    /// <summary>Posting a parcel to an online player lights their bag icon and tells them it arrived, both
    /// from the SENDER's thread. <c>RefreshMailFlags</c> already took the recipient's monitor for itself; the
    /// line that explains the icon did not. Both now sit in one section on the recipient, and the notice text
    /// is unchanged.</summary>
    [Fact]
    public void TheParcelNoticeReachesTheRecipientInsideTheirMonitor()
    {
        var (sender, _, _) = ProbePlayer("ParcelSender");
        var (recipient, recipientProbe, _) = ProbePlayer("ParcelRecipient");
        recipientProbe.Clear();

        NotifyParcelRecipient(sender, recipient.CharName);

        const string needle = "[PARCEL]:";
        Assert.Equal(1, recipientProbe.Count(needle));
        Assert.True(recipientProbe.AllHeld(needle), recipientProbe.Explain(needle));
        Assert.Equal("[PARCEL]: You got a parcel from ParcelSender!", recipientProbe.Only(needle));
    }

    // ---- 4. the mentorship culmination line ------------------------------------------------------------

    /// <summary>Culminating a mentorship writes three things on the protégé inside their monitor (already
    /// true) and then tells them so (was not). The line now goes out in the same section discipline, and the
    /// ORDER of the two lines — the mentor's first, then the protégé's — is unchanged, which is why it is its
    /// own section rather than an extra statement inside the mutation block.
    ///
    /// <para>Driven through the real flow: <c>LuaMentor</c> opens the "Who would you like to mentor?" input
    /// box and returns at that await, then two genuine <c>0x3A</c> replies resume it on the mentor's own
    /// read-loop thread — which is the thread shape the site actually runs on, since
    /// <c>HandleNpcDialog</c> completes the prompt inline from inside <c>Handle</c>'s <c>WithState</c>.</para></summary>
    [Fact]
    public void TheMentorshipCulminationLineReachesTheProtegeInsideTheirMonitor()
    {
        var (mentor, mentorProbe, _) = ProbePlayer("MentorshipMentor");
        var (protege, protegeProbe, _) = ProbePlayer("MentorshipProtege", c => c.Level = Mentorship.CulminateLevel);
        // Already this mentor's protégé. Under their own monitor, because a text slot's write saves the character
        // and the Debug guard on that chokepoint (AssertStateHeld, Session.State.cs:314-320) rightly refuses a bare
        // write.
        protege.WithState(() => protege.Quest(Mentorship.Key).SetText(Mentorship.MentorSlot, "MentorshipMentor"));
        mentorProbe.Clear(); protegeProbe.Clear();

        mentor.LuaMentor(new SpellDef(0, "mentor", "Mentor", 1, 0, 40, 0, "Who would you like to mentor?"));
        mentor.Receive(DialogInputFrame(protege.CharName));   // the name box
        mentor.Receive(DialogMenuFrame(1));                   // "Yes, that's fine."

        const string theirs = "This culminates your mentorship under";
        const string ours = "This culminates your mentorship of";
        Assert.Equal(1, protegeProbe.Count(theirs));
        Assert.True(protegeProbe.AllHeld(theirs), protegeProbe.Explain(theirs));
        Assert.Equal("This culminates your mentorship under MentorshipMentor. Hopefully you have learned much " +
                     "from their teachings.", protegeProbe.Only(theirs));

        // The mentor's own line still goes out, and the protégé never receives the mentor's copy.
        Assert.Equal(1, mentorProbe.Count(ours));
        Assert.Equal(0, protegeProbe.Count(ours));
        // The relationship really culminated — the line is not being sent down a dead branch.
        Assert.Equal("", protege.Quest(Mentorship.Key).GetText(Mentorship.MentorSlot));
    }

    // ---- 5. "@carnage" -------------------------------------------------------------------------------

    /// <summary>"@carnage &lt;name&gt; [n]" reads the target's carnage counter, writes counter+n back and tells
    /// them, all from the OPERATOR's thread. The read and the write are now one section on the target — they
    /// are a genuine read-modify-write, and the registry read is a bare <c>_char.Quests</c> lookup — and so
    /// is the line and the read of their name. The operator's own confirmation stays outside that section and
    /// still carries the same text.</summary>
    [Fact]
    public void TheCarnageLineReachesTheTargetInsideTheirMonitorAndTheTallyIsUnchanged()
    {
        var (gm, gmProbe, _) = ProbePlayer(GmRoster.Name);
        var (target, targetProbe, _) = ProbePlayer("CarnageTarget");
        gmProbe.Clear(); targetProbe.Clear();

        gm.Receive(SayFrame($"@carnage CarnageTarget 2"));

        const string won = "Your victory in the Carnage is recorded.";
        Assert.Equal(1, targetProbe.Count(won));
        Assert.True(targetProbe.AllHeld(won), targetProbe.Explain(won));
        Assert.Equal(2, target.Quest(QuestState.Registry).Get(ArmorQuest.CarnageWinsReg));
        // The operator's own confirmation, wrapped to the 30-char status pane (Commands.cs:462).
        Assert.Equal(1, gmProbe.Count("CarnageTarget: 2 carnage"));
        Assert.Equal(0, gmProbe.Count(won));   // the target's line is the target's alone

        // The negative form is the other half of the same branch, and the tally never goes below zero.
        targetProbe.Clear();
        gm.Receive(SayFrame($"@carnage CarnageTarget -5"));
        const string amended = "Your Carnage record has been amended.";
        Assert.Equal(1, targetProbe.Count(amended));
        Assert.True(targetProbe.AllHeld(amended), targetProbe.Explain(amended));
        Assert.Equal(0, target.Quest(QuestState.Registry).Get(ArmorQuest.CarnageWinsReg));
    }

    // ---- 6. "@bring" ---------------------------------------------------------------------------------

    /// <summary>"@bring &lt;name&gt;" runs the summon on the TARGET's session (already inside their monitor,
    /// EnterMap opens with one) and then tells them who moved them (was not). The line now sits in its own
    /// section on them, and the operator's confirmation — which reads the target's name — reads it from inside
    /// that section.</summary>
    [Fact]
    public void TheSummonLineReachesTheTargetInsideTheirMonitor()
    {
        var (gm, gmProbe, _) = ProbePlayer(GmRoster.Name);
        var (target, targetProbe, _) = ProbePlayer("BringTarget");
        gmProbe.Clear(); targetProbe.Clear();

        gm.Receive(SayFrame("@bring BringTarget"));

        string line = $"You have been summoned by {GmRoster.Name}.";
        Assert.Equal(1, targetProbe.Count(line));
        Assert.True(targetProbe.AllHeld(line), targetProbe.Explain(line));
        Assert.Equal(1, gmProbe.Count("Brought BringTarget"));   // the operator's confirmation, by name
        Assert.Equal(0, gmProbe.Count(line));
    }

    // ---- 7. the Lua world shout ------------------------------------------------------------------------

    /// <summary>The Sage ladder's world channel reaches every online player from ONE caster's thread, so it
    /// enters every online monitor in turn. Driven the way the real path drives it — the caster's own monitor
    /// held, and the Lua gate held around it, because <c>SpellContext.worldShout</c> runs inside
    /// <c>LuaVerbHost.Invoke</c>'s <c>EnterScriptGate</c>. Own monitor -&gt; gate -&gt; peer monitor is the
    /// order <c>SessionActorTests.LuaGateAgainstAPeerMonitorCannotDeadlock</c> already proves cannot cycle;
    /// this fact is the same order with the shipped loop in the middle. The three recipients are built in rank
    /// order so the one loop crosses both directions of rule 2.</summary>
    [Fact]
    public void TheWorldShoutEntersEveryOnlineSessionsMonitorUnderTheLuaGate()
    {
        var (low, lowProbe, _) = ProbePlayer("ShoutLow");
        var (caster, casterProbe, _) = ProbePlayer("ShoutCaster");
        var (high, highProbe, _) = ProbePlayer("ShoutHigh");
        Assert.True(low.StateRank < caster.StateRank && caster.StateRank < high.StateRank);
        lowProbe.Clear(); casterProbe.Clear(); highProbe.Clear();

        // Exactly what the shipped path holds before it reaches LuaWorldShout: Handle holds the caster's
        // monitor, LuaVerbHost holds the gate.
        caster.WithState(() =>
        {
            using (Session.EnterScriptGate()) Assert.True(caster.LuaWorldShout("field is clear"));
        });

        const string line = "[ShoutCaster]: field is clear";
        foreach (var probe in new[] { lowProbe, casterProbe, highProbe })
        {
            Assert.Equal(1, probe.Count(line));
            Assert.True(probe.AllHeld(line), probe.Explain(line));
            Assert.Equal(line, probe.Only(line));
        }
        // The caller walks out holding exactly what it walked in with — rule 2's descending drop put it back.
        Assert.False(caster.StateHeld);
    }

    // ---- 8. the spell flavour line ---------------------------------------------------------------------

    /// <summary>A spell cast on another player sends them their flavour line from the CASTER's thread.
    /// <c>TellTarget</c> is wrapped at its own definition (the <c>NotifyGroup</c> precedent), so all nine call
    /// sites are covered at once; the self-cast branch is rule 3's re-entrant no-op. Driven through
    /// <c>LuaHealTarget</c>, one of those nine, with the caster's monitor and the Lua gate held as the real
    /// path holds them.</summary>
    [Fact]
    public void TheSpellFlavourLineReachesTheTargetInsideTheirMonitor()
    {
        var (caster, casterProbe, _) = ProbePlayer("FlavourCaster");
        var (target, targetProbe, _) = ProbePlayer("FlavourTarget", c => { c.Hp = 10; c.MaxHp = 100; });
        casterProbe.Clear(); targetProbe.Clear();

        // Type 2 is the targeted shape ("Which target? >"); an unknown key has no recorded flavour, so the
        // generic "<caster> casts <X> on you." is the line — the fallback half of the same branch.
        var sp = new SpellDef(0, "monitorprobe", "Mend Wounds", 2, 0, 1, 0, "Which target?");
        caster.WithState(() =>
        {
            using (Session.EnterScriptGate()) caster.LuaHealTarget(5, sp, target.PlayerId);
        });

        const string line = "FlavourCaster casts Mend Wounds on you.";
        Assert.Equal(1, targetProbe.Count(line));
        Assert.True(targetProbe.AllHeld(line), targetProbe.Explain(line));
        Assert.Equal(line, targetProbe.Only(line));
        Assert.Equal(0, casterProbe.Count(line));   // the caster never gets the target's flavour
        Assert.False(caster.StateHeld);
    }

    // ---- 9. the kick line ------------------------------------------------------------------------------

    /// <summary>"@kick &lt;name&gt; [reason]" saves the target, tells them why they are going and drops them,
    /// all from the OPERATOR's thread. <c>FlushNow</c> took the target's monitor for its snapshot and the
    /// notice and <c>Disconnect</c>'s read of their name did not; all three are now one section, the shape
    /// <c>KickForReplacement</c> has always had. The notice is the <c>0x02</c> login-box channel rather than
    /// minitext, which is why the probe decodes both, and it must still go out BEFORE the connection
    /// closes.</summary>
    [Fact]
    public void TheKickLineReachesTheTargetInsideTheirMonitorBeforeTheDisconnect()
    {
        var (gm, _, _) = ProbePlayer(GmRoster.Name);
        var (target, targetProbe, _) = ProbePlayer("KickTarget");
        targetProbe.Clear();

        gm.Receive(SayFrame("@kick KickTarget being a nuisance"));

        const string line = "You were disconnected by a GM: being a nuisance";
        Assert.Equal(1, targetProbe.Count(line));
        Assert.True(targetProbe.AllHeld(line), targetProbe.Explain(line));
        Assert.Equal(line, targetProbe.Only(line));
        Assert.True(targetProbe.Closed, "the kick must still close the connection after the line");
    }

    // ===== plumbing =====================================================================================

    /// <summary><see cref="StaffAccounts"/> is empty by default (a fresh deployment has no staff), so the two
    /// GM facts would otherwise be answered with "Unknown command". Writes the roster into the redirected test
    /// state directory (#23 points P1998_STATE at a temp dir before anything loads) under the process-wide
    /// gate. The name is deliberately the same one <c>CommandTableTests</c> uses and the file content is
    /// identical, so whichever class writes it first, the other's rewrite is a no-op and neither can demote
    /// the other's sessions mid-run.</summary>
    private static class GmRoster
    {
        public const string Name = "cmdgm";
        private static bool _done;

        public static void Ensure()
        {
            lock (TestProcessState.Gate)
            {
                if (_done) return;
                Directory.CreateDirectory(TestProcessState.StateDirectory);
                File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), Name + "\n");
                StaffAccounts.Load();
                _done = true;
            }
        }
    }

    /// <summary>A socket-free session whose outbound is a <see cref="MonitorProbe"/>, built the way
    /// <c>SessionFixture.PlayerWith</c> builds one — same world, same store, same map, 4.95 port — but with
    /// the probe in place of the recorder so every frame carries the monitor answer with it.</summary>
    private (Session session, MonitorProbe probe, Character character) ProbePlayer(
        string name, Action<Character>? configure = null)
    {
        var character = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = name,
            Map = SessionFixture.HomeMap,
            X = 5,
            Y = 10,
        };
        configure?.Invoke(character);

        var probe = new MonitorProbe($"probe:{name}");
        var session = new Session(probe, 2005, _fx.Store, _fx.World, character);
        probe.Owner = session;                    // set before anything can send
        _fx.World.EnterMap(session, SessionFixture.HomeMap);
        probe.Clear();
        return (session, probe, character);
    }

    /// <summary>The "!" whisper — RTK's clan channel (<c>clif_parsewisp</c>). Wire body, as
    /// <c>Session.HandleWhisperPacket</c> reads it: <c>dstLen(u8) dst[dstLen] msgLen(u8) msg[msgLen] 00</c>.</summary>
    private static byte[] ClanChatFrame(string msg) => WhisperFrame("!", msg);

    private static byte[] WhisperFrame(string dstName, string msg)
    {
        byte[] dst = Encoding.ASCII.GetBytes(dstName);
        byte[] text = Encoding.ASCII.GetBytes(msg);
        var body = new List<byte> { (byte)dst.Length };
        body.AddRange(dst);
        body.Add((byte)text.Length);
        body.AddRange(text);
        body.Add(0);
        return SessionFixture.Frame(ClientOp.Whisper, body.ToArray());
    }

    /// <summary>Ordinary speech (<c>0x0E</c>): <c>chatType(u8) msgLen(u8) msg[]</c> — the frame the client
    /// sends for anything typed into the chat bar, including the client-native "/sp" command.</summary>
    private static byte[] SayFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { 0x00, (byte)t.Length };
        body.AddRange(t);
        return SessionFixture.Frame(ClientOp.Chat, body.ToArray());
    }

    /// <summary>The client's reply to a prompt (<c>0x3A</c>, RTK <c>clif_parsenpcdialog</c>):
    /// <c>[0]=kind, [8]=step, [10]=menu index or input length, [11..]=input text</c>. Kind 2 is a menu pick,
    /// kind 4 + step 2 a real input-box submit.</summary>
    private static byte[] DialogMenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

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

    /// <summary><c>Session.NotifyParcelRecipient</c> by reflection. The parcel post itself is a multi-step NPC
    /// conversation with an inventory stack behind it; what this fact is about is the notice that conversation
    /// ends in, so it is driven at that seam rather than through six dialog frames of setup.</summary>
    private static readonly MethodInfo NotifyParcel =
        typeof(Session).GetMethod("NotifyParcelRecipient", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void NotifyParcelRecipient(Session sender, string name) =>
        NotifyParcel.Invoke(sender, new object[] { name });

    private static readonly FieldInfo CharField =
        typeof(Session).GetField("_char", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary><c>Character.IgnoreList</c> by reflection — nothing public exposes it, the same reason
    /// <c>PartyNotifyMonitorTests</c> reaches for it.</summary>
    private static void SetIgnoreList(Session s, params string[] names)
    {
        var c = (Character)CharField.GetValue(s)!;
        c.IgnoreList.Clear();
        c.IgnoreList.AddRange(names);
    }

    /// <summary>
    /// An <c>IOutbound</c> that records each line this session was sent together with the answer to
    /// <c>Session.StateHeld</c> at the moment the frame reached the transport.
    ///
    /// <para>That instant is the one that matters: <c>Session.SendMap</c> builds the packet with
    /// <c>_gameInc++</c> and calls <c>Send</c> straight through on the same thread, so a line recorded with
    /// <c>Held == false</c> is a line built outside the owning session's monitor. Both channels these sites
    /// use are decoded: <c>0x0A</c> minitext (<c>type(u8) len(u16BE) text</c>, encrypted under its own
    /// increment) and the kick's <c>0x02</c> login-box message (<c>0x0F len(u8) text 00</c>, fixed increment
    /// <c>0x02</c>). Thread-safe, unlike <c>RecordingOutbound</c>.</para>
    /// </summary>
    private sealed class MonitorProbe : IOutbound
    {
        private readonly List<(string Text, bool Held)> _lines = new();
        private readonly object _lock = new();

        /// <summary>The session this probe belongs to. Assigned right after construction — the session cannot
        /// be handed its own outbound before the outbound exists — and read only from <see cref="Send"/>.</summary>
        internal Session? Owner;

        public MonitorProbe(string remote) => Remote = remote;

        public string Remote { get; }
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Closed { get; private set; }

        public bool Send(byte[] frame)
        {
            bool held = Owner is not null && Owner.StateHeld;
            if (!TkPacket.TryParse(frame, out var pkt, out _)) return true;
            var body = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
            string? text = null;
            if (pkt.Opcode == ServerOp.MiniText && body.Length >= 3)
                text = Encoding.ASCII.GetString(body, 3, body.Length - 3);
            else if (pkt.Opcode == 0x02 && body.Length >= 3 && body[0] == 0x0F)
                text = Encoding.ASCII.GetString(body, 2, Math.Min(body[1], body.Length - 2));
            if (text is not null) lock (_lock) _lines.Add((text, held));
            return true;
        }

        public void Close() => Closed = true;

        public void Clear() { lock (_lock) _lines.Clear(); }

        private List<(string Text, bool Held)> Matching(string needle)
        {
            lock (_lock) return _lines.Where(l => l.Text.Contains(needle)).ToList();
        }

        /// <summary>How many lines carrying <paramref name="needle"/> this session was sent.</summary>
        internal int Count(string needle) => Matching(needle).Count;

        /// <summary>The one line carrying <paramref name="needle"/>, for asserting the exact text.</summary>
        internal string Only(string needle) => Assert.Single(Matching(needle)).Text;

        /// <summary>True when there is at least one such line and EVERY one of them was built while this
        /// session's own monitor was held.</summary>
        internal bool AllHeld(string needle)
        {
            var hits = Matching(needle);
            return hits.Count > 0 && hits.All(l => l.Held);
        }

        /// <summary>The failure message for <see cref="AllHeld"/>: which lines were seen and how.</summary>
        internal string Explain(string needle)
        {
            var hits = Matching(needle);
            if (hits.Count == 0) return $"{Remote}: no line carrying \"{needle}\" was sent at all";
            return $"{Remote}: " + string.Join(", ",
                hits.Select(l => $"\"{l.Text}\" monitor {(l.Held ? "HELD" : "NOT held")}"));
        }
    }
}
