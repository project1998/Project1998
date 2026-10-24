using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // Client chat (0x0E): chatType(u8) msgLen(u8) msg[]. Echo it back as over-head speech (0x0D)
    // attributed to the sender's entity so the bubble appears above the character.
    private void HandleChat(byte[] dec)
    {
        if (dec.Length < 2) return;
        byte chatType = dec[0];
        int msgLen = dec[1];
        if (msgLen < 0 || 2 + msgLen > dec.Length) return;
        var msg = dec[2..(2 + msgLen)];
        var text = Encoding.ASCII.GetString(msg);

        // ==== chat commands ======================================================================
        // Every command, player and GM alike, lives in the Server/Commands.cs table: that file owns the
        // '@' prefix, the name -> handler map, the per-command GM gate and @help. Nothing here needs to
        // know a single command name. Anything that isn't a command falls through to speech below.
        if (TryRunCommand(text)) return;

        // Client-native RTK typed commands. A DIFFERENT namespace from our '@' prefix (these are what the
        // real client's speech.lua /help lists), so they stay as their own checks rather than table rows.
        if (text.StartsWith("/subpathchat ", StringComparison.OrdinalIgnoreCase)) { DoSubpathChat(text[13..].Trim()); return; }
        if (text.StartsWith("/sp ", StringComparison.OrdinalIgnoreCase)) { DoSubpathChat(text[4..].Trim()); return; }

        // Commands moved from '!' to '@'. Staff leading with '!' are near-certainly reaching for the old
        // prefix, and letting that through would SHOUT the old-style command to everyone on the map — so nudge
        // instead. Only for staff (either tier): for an ordinary player '!' has never meant anything, and
        // their shouts must keep working.
        if (Access > AccessLevel.Player && text.Length > 1 && text[0] == '!' && char.IsLetter(text[1]))
        { SendLog($"Commands now start with '{Prefix}' — try  {Prefix}{text[1..]}"); return; }

        // "A" (uppercase, exact) — remove ALL equipped items at once, same effect as clicking every worn
        // slot's 0x1F unequip in a row. Case-sensitive so ordinary chat ("a", "aww", …) still speaks
        // normally; nothing else in this client sends a bare capital letter as a message.
        if (text == "A") { UnequipAll(); return; }

        // Muted players may still run '@' commands (handled above) but cannot SPEAK. The gate sits here,
        // after the command table, so a mute silences the player without also taking away any other command.
        if (IsMuted()) { ReportMuted(); return; }

        // Real chat (not a ! command). Broadcast the over-head bubble (0x0D) to co-located players INCLUDING
        // us, so we see our own bubble too. Prefix with who said it (client keybind help, re/str_eng.res:102/105,
        // documents dedicated Say ''' and Shout '!' hotkeys — this is the server-side text the client shows in
        // both the bubble and the chat-log line for either). chatType is passed through UNCHANGED from the
        // client's own byte: whatever mode it used to pick the hotkey ('=say vs !=shout) is presumably also
        // what the client uses to pick bubble/log color on playback, so relaying its own value back should
        // already render correctly without us inventing a color scheme.
        //
        // HEARING RANGE (RTK speech.lua onSay: say distance 8, shout distance 16): both Say and Shout are
        // proximity-gated, Shout just reaches twice as far. Say (type 0) uses the ±9/±8 SAMEAREA box; Shout
        // (type 1) uses the ±16 box — the yellow shout bubble carries about double, but is NOT map-wide.
        // (say=0 is confirmed by every chat message that has worked so far; type 1 = shout is RTK's talktype.)
        bool shout = chatType != 0;
        string formatted = shout ? $"{_char.Name}! {text}" : $"{_char.Name}: {text}";
        if (formatted.Length > 250) formatted = formatted[..250];
        var outMsg = Encoding.ASCII.GetBytes(formatted);
        // Say is a genuine RTK SAMEAREA send, so it gets the edge shift (World.ShiftedBox) — speaking with your
        // back to a wall carries further the other way. Shout does NOT: its ±16 comes from speech.lua's distance,
        // not from map_foreachinarea, so there is no RTK box to slide and it stays plainly centred on us.
        int halfW = shout ? ShoutHalfW : SayHalfW, halfH = shout ? ShoutHalfH : SayHalfH;
        _world.BroadcastArea(_char.Map, _char.X, _char.Y, halfW, halfH,
            p => p.SpeakEntity(chatType, _char.Id, outMsg), edgeShift: !shout);
        Log.Info($"   -> speech type={chatType}{(shout ? " (shout, ±16)" : " (say, area)")}: \"{text}\" -> map {_char.Map}");

        // …and let a nearby NPC react to it (RTK onSayClick: "i'd like to fish", a tutor's name, …).
        DispatchSpeech(text);
    }

    // ---- whisper/tell (RTK clif_parsewisp, clif.c:7644-7790) ---------------------------------------------
    // Native client input: Shift+' opens the whisper prompt, then a name + Enter, then a message + Enter.
    // LIVE-confirmed 2026-07-26 (real capture): op=0x19 body = dstlen(u8) dst_name[dstlen] msglen(u8)
    // msg[msglen] 00 — exactly RTK's wire layout, and the ONLY entry point: the "@whisper"/"@w" chat
    // commands that used to wrap DoWhisper were removed once this was confirmed real. Message TEXT is RTK's
    // real wording wherever portable (not-found, map-silenced). Not modelled: per-player whisper on/off,
    // silence/mute, and ignore lists — none of those exist yet.
    private void HandleWhisperPacket(byte[] dec)
    {
        if (dec.Length < 1) return;
        int dstLen = dec[0];
        if (dstLen <= 0 || 1 + dstLen + 1 > dec.Length) return;
        string name = Encoding.ASCII.GetString(dec, 1, dstLen);
        int msgLen = dec[1 + dstLen];
        int msgStart = 1 + dstLen + 1;
        if (msgLen < 0 || msgStart + msgLen > dec.Length) return;
        string msg = Encoding.ASCII.GetString(dec, msgStart, msgLen);
        DoWhisper(name, msg);
    }

    private void DoWhisper(string name, string msg)
    {
        if (name.Length == 0 || msg.Length == 0) return;

        // A mute that only stopped map speech would be no mute at all — whisper is the obvious way around it.
        if (IsMuted()) { ReportMuted(); return; }

        // RTK: map[sd->bl.m].cantalk == 1 blocks whisper with this exact line (only 2 maps set it).
        // clif_parsewisp delivers it via clif_sendminitext — the type-3 status pane, not blue.
        if (!Content.CanTalk(_char.Map)) { SendMiniText("Your voice is swept away by a strange wind."); return; }

        // RTK clif_parsewisp routes special recipient "names" to a broadcast channel instead of a person:
        // "!!" = group/party chat, "!" = clan chat, "@" = subpath chat — the same channel as /sp, so it goes
        // through DoSubpathChat and its PC-subpath gate. (RTK also has "?" = novice, which isn't modelled.)
        // These sit after the mute + cantalk gates so those apply to the channels too, exactly as RTK does.
        if (name == "!!") { DoGroupChat(msg); return; }
        if (name == "!")  { DoClanChat(msg);  return; }
        if (name == "@")  { DoSubpathChat(msg); return; }

        // RTK's literal wording, on the BLUE wisp channel (clif_sendbluemessage) — same channel the whisper
        // itself would have used, NOT self-speech.
        var target = ResolveOnlinePlayer(name, $"{name} is nowhere to be found.", RefuseChannel.Blue);
        if (target is null) return;

        // The two ignore reads, the delivery and the read of their name for our echo are ONE critical section
        // on the TARGET (#29 rule 2, Server/Session.State.cs) — the DoClanChat shape one screen down, applied
        // to the 1:1 case. What was genuinely unguarded here is the foreign READS: `target._char.Name` (twice)
        // and `target.IsIgnoring(...)`, another session's character and ignore list read bare from OUR thread;
        // `ReceiveWhisper` -> SendBlueMessage -> SendMiniText also writes their `_gameInc`, which rule 2 covers
        // for the same blanket reason NotifyGroup is wrapped, not because the byte tears (a lost increment is a
        // duplicate nonce, benign per the note above Session.Send in Session.WorldApi.cs).
        //
        // Wrapped HERE rather than at ReceiveWhisper's definition, further down this file: the reads that decide
        // whether the line goes out at all have to be in the SAME section as the send, and this is
        // ReceiveWhisper's only caller, so the definition wrap would buy nothing and would leave the gate
        // outside.
        //
        // ONE peer monitor at a time and no new lock or lock order: rule 2 resolves this single nested
        // acquisition ascending or descending, and nothing inside the body reaches a second peer. Our OWN
        // ignore list and name are read inside the body deliberately — the descending case drops our monitor to
        // take theirs and puts it back BEFORE the body runs (Session.State.cs:151-159), so inside it both are
        // held. Rule 1 holds: FindPlayer takes and releases World._lock inside itself
        // (World.OnlineRegistry.cs:45-53), so no world lock is held here. The refusal and the echo are sent
        // OUTSIDE the section, so no peer monitor is held while we send to ourselves.
        //
        // RTK clif_isignore: a whisper is blocked if EITHER side has the other on their ignore list — not
        // just the recipient blocking the sender, but also the sender's own list (so you can't be pestered
        // by someone you've muted even if THEY never muted you). canwhisper's real wording on failure, blue.
        bool ignored = false;
        string them = "";
        target.WithState(() =>
        {
            if (IsIgnoring(target._char.Name) || target.IsIgnoring(_char.Name)) { ignored = true; return; }
            target.ReceiveWhisper(_char.Name, msg);
            them = target._char.Name;
        });
        if (ignored) { SendBlueMessage("They cannot hear you right now."); return; }

        // Sender's own echo — RTK clif_retrwisp, verbatim "%s> %s": the TARGET's name (proper-cased from
        // their session, not as typed) + "> " + the message, on the blue wisp channel.
        SendBlueMessage($"{them}> {msg}");
    }

    // "!!" whisper target — group/party chat (RTK clif_sendgroupmessage). Reaches every member of your group
    // on ANY map (not proximity-scoped, unlike say), INCLUDING you, so it doubles as your own echo. Rendered
    // on the type-11 group channel; label/format is RTK's "[!<name>] (<class>) <message>". A recipient who has
    // either side on ignore is skipped (RTK clif_isignore), and you can't ignore yourself so the echo survives.
    private void DoGroupChat(string msg)
    {
        if (_party is null) { SendBlueMessage("You are not in a group"); return; }   // RTK's literal wording (blue, no period)
        string line = $"[!{_char.Name}] ({ClassTitle}) {msg}";
        if (line.Length > 250) line = line[..250];
        // Each recipient's ignore reads and their send are ONE critical section on THEM (#29 rule 2).
        // `p._char.Name` and `p.IsIgnoring(...)` are another session's state read bare from our thread —
        // those reads are the genuinely unguarded accesses here — and `p.SendMiniText` writes their
        // `_gameInc`, which rule 2 covers for the same blanket reason NotifyGroup is wrapped, not because
        // the byte tears (a lost increment is a duplicate nonce, benign per the note above Session.Send in
        // Session.WorldApi.cs).
        // ONE peer monitor at a time (the guard is released before the next member), so this is the same
        // single nested acquisition Party.Broadcast makes and rule 2 resolves it the same way, ascending or
        // descending; nothing here ever holds two peers at once. Our OWN list and name are read inside the
        // body deliberately: the descending case drops our monitor to take theirs and puts it back before the
        // body runs, so inside it both are held. Our own echo is re-entrant (rule 3), so it still goes out in
        // roster order like every other line. That drop is also a window master did not have: a kick can
        // land while we sit in Monitor.Enter on a lower-ranked peer, and the loop then finishes delivering
        // the already-composed line to the roster it snapshotted before the kick. Inherent to rule 2 —
        // holding our own monitor across the whole loop is exactly what the rule forbids — and the invite
        // path documents the same shape at Session.Social.cs:84-88; the blast radius is one in-flight line.
        foreach (var p in _party.Members)
            p.WithState(() =>
            {
                if (IsIgnoring(p._char.Name) || p.IsIgnoring(_char.Name)) return;
                p.SendMiniText(line, type: 11);
            });
        Log.Info($"   -> group chat: \"{line}\"");
    }

    // "!" whisper target — clan chat (RTK clif_sendclanmessage). Reaches every ONLINE player who shares your
    // clan AND has clan chat on, on any map, INCLUDING you. Both ends must have clan chat enabled — the sender
    // gate here (RTK's status.clan_chat check in clif_parsewisp), the recipient gate in the loop. Clan
    // membership is only a name string here (no clan roster/id like RTK's status.clan), so we match on that.
    // Rendered on the type-12 clan channel; format is RTK's "<!<name>> (<class>) <message>".
    private void DoClanChat(string msg)
    {
        if (string.IsNullOrEmpty(_char.ClanName)) { SendBlueMessage("You are not in a clan"); return; }   // RTK wording (blue, no period)
        if (!_char.ClanChat) { SendBlueMessage("Clan chat is off."); return; }                            // RTK wording (blue)
        string line = $"<!{_char.Name}> ({ClassTitle}) {msg}";
        if (line.Length > 250) line = line[..250];
        // Same shape as DoGroupChat above, for the same reason (#29 rule 2, Server/Session.State.cs): every
        // read of the recipient's state — their clan-chat toggle, their clan name, their name, their ignore
        // list — and the send that follows are ONE critical section on THEM. Those four reads are the
        // genuinely unguarded accesses here; the `_gameInc` the send writes is covered by the same blanket
        // rule, not because the byte tears (a lost increment is a duplicate nonce, benign per the note above
        // Session.Send in Session.WorldApi.cs). Rule 1 holds: Online.All() snapshots under World._lock and returns
        // with it released (World.OnlineRegistry.cs:77-81), so no world lock is held when we enter a peer.
        // ONE peer monitor at a time — the guard is released before the next player — so rule 2 resolves each
        // acquisition on its own, ascending or descending, and two peers are never held at once. Our own name
        // and ignore list are read inside the body deliberately: the descending case drops our monitor to take
        // theirs and puts it back before the body runs, so inside it both are held. Our own line is the
        // re-entrant case (rule 3), so it still goes out in roster order like every other line.
        foreach (var p in _world.Online.All())
            p.WithState(() =>
            {
                if (p._char.ClanChat
                    && string.Equals(p._char.ClanName, _char.ClanName, StringComparison.Ordinal)
                    && !(IsIgnoring(p._char.Name) || p.IsIgnoring(_char.Name)))
                    p.SendMiniText(line, type: 12);
            });
        Log.Info($"   -> clan chat: \"{line}\"");
    }

    // Case-insensitive membership check against THIS character's own ignore list (RTK strcmpi).
    private bool IsIgnoring(string name) => _char.IgnoreList.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Deliver a whisper's text to THIS session (the recipient), via the non-entity 0x0A channel
    /// (SendMiniText, type 0 = RTK's "Wisp/blue text") rather than 0x0D over-head speech: a whisper must
    /// reach the chat log with NO head bubble and work cross-map, and 0x0D is entity-bound (bubble always
    /// shown; delivering via our own entity would misattribute it as self-speech, delivering via the
    /// sender's entity id would silently vanish whenever sender/recipient aren't on the same map — the
    /// common case). 0x0A itself is already proven live (look-at names, item-pickup text both use it via
    /// SendMiniText's type=3); only the type=0/"blue chat window, not the status box" routing is unconfirmed.
    ///
    /// <para>Format: RTK clif_sendwisp is sender + '"' + message — the double-quote IS the whisper marker
    /// (vs the sender echo's '>'). RTK also inserts the sender's class in parens (<c>Sender" (Class) msg</c>);
    /// we deliberately drop that half — user-specified 2026-08-19: incoming reads <c>Sender" message</c>.</para></summary>
    internal void ReceiveWhisper(string fromName, string msg) => SendBlueMessage($"{fromName}\" {msg}");

    /// <summary>Party join/leave/kick/disband broadcasts. Delivered on the SAME type=3 mini/status channel
    /// as the "You cast X." casting info (SendMiniText's default), NOT the type=11 "group" channel: on the
    /// 4.95 client type 11 lands in the scrolling chat box as blue text, which reads as someone talking
    /// rather than a status event. Type 3 puts it in the status/mini pane where group events belong.
    ///
    /// <para><b>Wrapped at its own definition</b> — the <see cref="SendAdvice"/> shape, #29 rule 2. Almost
    /// every call is a CROSS-SESSION one: <see cref="Party.Broadcast"/> walks the roster on the thread of
    /// whichever member invited, left, kicked or disconnected, so the body below runs on a thread that does
    /// not own this session. That alone is what puts it under the monitor: rule 2 is a BLANKET rule on
    /// entering a peer's state, not a repair for a specific corruption. In particular the <c>_gameInc</c>
    /// this ends up writing (<c>SendMiniText</c> → <c>SendMap</c>, Session.cs) is NOT a torn-byte hazard —
    /// it is a byte, read once by <c>_gameInc++</c> and passed BY VALUE both into the body's encryption and
    /// into the frame header, so every frame decrypts with the increment it declares and the worst a lost
    /// update can do is emit the same nonce twice. The note above <c>Session.Send</c> in Session.WorldApi.cs
    /// already records that as benign ("a rare duplicate is harmless since each packet carries its own inc in
    /// the header"), and nothing here contradicts it. The accesses on this path that were genuinely unguarded
    /// are the foreign READS the same slice closes: <c>p._char.Name</c> and <c>p.IsIgnoring(...)</c> in
    /// <c>DoGroupChat</c>, and <c>target.IsDead</c> in the invite (Session.Social.cs).
    /// Wrapping here rather than at the call sites is what rule 3 is for: the two calls already inside
    /// <c>member.WithState</c> (<c>RemoveFromParty</c>) see a re-entrant no-op and pay nothing.</para></summary>
    internal void NotifyGroup(string text)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        SendMiniText(text, type: 3);
    }

    /// <summary>Wisdom / "Listen to advice" (0x1b sub-4): stream a periodic gameplay hint into the chat channel
    /// (SendMiniText type 11 — RTK's advice type 99 -> 11, the "group &amp; subpath" chat channel, which is where
    /// the tips belong). A no-op for anyone who turned the option off. Fired on the world's ~15-minute advice
    /// tick (World.Tick / AdviceTicks), mirroring RTK's per-player pc_timer advice cadence.</summary>
    internal void SendAdvice()
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (!_char.HasSetting(0x04)) return;
        if (Content.RandomAdvice() is string hint) SendMiniText(hint, type: 11);
    }

}
