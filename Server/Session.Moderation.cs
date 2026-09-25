using Shared;

namespace Server;

/// <summary>
/// The GM-facing moderation commands: @ban / @unban / @mute / @unmute / @kick / @banip / @bans / @modlog.
/// The state and the audit log live in <see cref="Moderation"/> (Shared, because the login process enforces
/// bans too); this file is only the command surface.
///
/// <para><b>Two axes, deliberately.</b> An account ban is evaded by making a new character; an IP ban
/// catches everyone behind one address. RTK keeps both (<c>ChaBanned</c> + a <c>BannedIP</c> table) and so
/// do we — the GM picks which fits, and for a serious case uses both.</para>
///
/// <para><b>Duration defaults to permanent.</b> Every command here reads "no duration given" as permanent
/// rather than as zero. The alternative — a mistyped command silently expiring instantly — fails in the
/// direction where nobody notices; this one fails in the direction a GM notices immediately and can undo.</para>
///
/// <para><b>Applying to an online player is immediate.</b> A ban kicks them now, a mute lands on their
/// session so the next line they type is already blocked. Waiting for the next login would make every
/// moderation action useless against the behaviour that prompted it.</para>
/// </summary>
public sealed partial class Session
{
    // "<name> [minutes] [reason]" — the tail after an optional numeric duration is free text.
    // Returns false (and complains) if no name was given.
    private bool ParseModArgs(string args, string usage, out string name, out long until, out string reason)
    {
        name = ""; until = Moderation.Forever; reason = "";
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { SendLog(usage); return false; }

        name = parts[0];
        int i = 1;
        // A leading number is a duration in minutes. If the second word ISN'T a number the whole tail is the
        // reason — so "@ban cheater duping items" works without the GM having to type a duration first.
        if (parts.Length > 1 && double.TryParse(parts[1], out double mins)) { until = Moderation.Deadline(mins); i = 2; }
        if (i < parts.Length) reason = string.Join(' ', parts[i..]);
        return true;
    }

    // @ban <name> [minutes] [reason]
    private void BanCmd(string args)
    {
        if (!ParseModArgs(args, $"Usage: {Prefix}ban <name> [minutes] [reason]   (no duration = permanent)",
                          out var name, out var until, out var reason)) return;

        if (!CharacterStore.CharacterExists(name)) { SendLog($"No character named \"{name}\"."); return; }
        if (StaffAccounts.IsGm(name)) { SendLog("You cannot ban a GM. Remove them from the GM roster first."); return; }
        if (string.Equals(Auth.Key(name), UserKey, StringComparison.Ordinal)) { SendLog("You cannot ban yourself."); return; }

        if (!Moderation.Ban(name, until, reason, _char.Name)) { SendLog("Ban FAILED — the write did not land. See the log."); return; }

        // Kick them off NOW if they're online. A ban that waits for the next login lets the behaviour that
        // triggered it carry on for as long as they stay connected.
        //
        // Save, tell, drop — ONE critical section on the banned player, the same three statements in the same
        // order as @kick eighty lines down (#29 rule 2, Server/Session.State.cs:25-33). Everything in there is an
        // entry into ANOTHER session's state from the operator's thread: SendMessage writes their _gameInc
        // (blanket rule 2, not a torn byte — the `_gameInc++` note above Session.Send in Session.WorldApi.cs),
        // Disconnect reads their _char.Name for its log line, and FlushNow serialises their whole character graph.
        //
        // THE FLUSHNOW IS NEW HERE, and it is the one behaviour change on this path. What the ban saved before:
        // nothing at ban time — Disconnect only closes the connection, and the save came later and elsewhere,
        // when the target's own read loop unwound into its finally and ran WithState(TearDownWorldState)
        // (Session.EndReadLoopAsync, which RunAsync's finally awaits), whose last act is the fenced
        // `_dirty = true; FlushNow();` final save at the end of TearDownWorldState. That still happens and is
        // still the backstop. What it does NOT give is the guarantee @kick's explicit FlushNow
        // gives: a save taken at the INSTANT of the command, inside the same section as the notice and the drop,
        // so nothing of theirs can move between the snapshot and the teardown and nothing is riding on their read
        // loop actually getting to unwind. A ban must not cost the player progress they had earned any more than
        // a kick must, and there was no reason for the two commands to differ. FlushNow is dirty-gated, so for a
        // clean session it costs a flag test.
        //
        // No new lock and no new lock order. FlushNow's own EnterState becomes the re-entrant case (rule 3), so
        // the target's monitor IS held across CaptureAndWrite's lock (_writeGate) section — the nested guard is
        // `default` (Session.State.cs:137) and disposes to nothing (Session.State.cs:175-180). That nesting is
        // not new: it is KickForReplacement's (Session.CharacterApi.cs, around its unconditional write), @kick's,
        // and the one the read loop's own teardown takes on every ordinary disconnect. Nothing in the tree takes
        // a session monitor while holding a _writeGate (Session.FlushPair, in Session.TimedEffects.cs, closes its
        // WithStatePair first), so monitor -> _writeGate cannot cycle. Rule 1 holds: FindPlayer takes and
        // releases World._lock inside itself (OnlineRegistry.FindPlayer, World.OnlineRegistry.cs) and
        // CloseConnection takes no lock at all. The operator's own SendLog stays outside, so no peer monitor is
        // held while we report to ourselves. The notice text is computed OUTSIDE the section too: BanMessageFor
        // opens SQLite and reads the moderation row for the name we were given (LoginAuth.BanMessageFor,
        // Shared/LoginAuth.cs), which is none of the target's state, so holding their monitor across a database
        // open and SELECT buys nothing. Hoisted, the section holds only the target's
        // own work, which is the same shape @kick's sends — a string already in hand.
        var online = _world.Online.FindPlayer(name);
        if (online is not null)
        {
            var notice = LoginAuth.BanMessageFor(name);
            online.WithState(() =>
            {
                // Save before dropping them — a ban must never cost the player progress they'd earned.
                online.FlushNow();
                online.SendMessage(notice);
                online.Disconnect("banned");
            });
        }

        SendLog($"Banned {name} ({Moderation.Describe(until)})"
              + (reason.Length > 0 ? $": {reason}" : ".") + (online is not null ? "  [kicked]" : ""));
        Log.Info($"   -> {Prefix}ban by '{_char.Name}': {name} until={Moderation.Describe(until)} reason='{reason}'");
    }

    // @unban <name>
    private void UnbanCmd(string args)
    {
        var name = args.Trim();
        if (name.Length == 0) { SendLog($"Usage: {Prefix}unban <name>"); return; }

        var rec = Moderation.Get(name);
        if (rec?.IsBanned != true) { SendLog($"{name} is not banned."); return; }

        SendLog(Moderation.Unban(name, _char.Name) ? $"Unbanned {name}." : "Unban FAILED — the write did not land.");
        Log.Info($"   -> {Prefix}unban by '{_char.Name}': {name}");
    }

    // ---- mute ---------------------------------------------------------------------------------------
    //
    // Held as an absolute unix-SECONDS deadline on the SESSION, not re-read from the database per line. A
    // DB round-trip on every chat message would put a synchronous read on the packet path for state that
    // changes maybe twice a week. It is loaded once at world entry (LoadModerationState) and pushed
    // directly onto the live session by @mute/@unmute (Session.ApplyMute), so both the placement and the
    // lifting are immediate; the deadline being absolute is what makes EXPIRY work with no timer at all.
    private long _mutedUntil;
    private string _muteReason = "";

    internal bool IsMuted() => _mutedUntil > Moderation.Now;

    /// <summary>Load this account's mute state into the session. Called once, at world entry.</summary>
    internal void LoadModerationState()
    {
        if (Moderation.IsMuted(_user, out var reason, out var until)) { _mutedUntil = until; _muteReason = reason; }
        else { _mutedUntil = 0; _muteReason = ""; }
    }

    /// <summary>Apply a mute/unmute to an ALREADY-ONLINE session, so a GM's command takes effect on the next
    /// line the player types rather than at their next login.</summary>
    internal void ApplyMute(long until, string reason)
    {
        _mutedUntil = until;
        _muteReason = reason ?? "";
        if (IsMuted()) ReportMuted();
        else SendLog("You are no longer muted.");
    }

    private void ReportMuted()
    {
        string left = _mutedUntil >= Moderation.Forever ? "" : $" ({Moderation.Describe(_mutedUntil)} remaining)";
        SendLog(string.IsNullOrWhiteSpace(_muteReason)
            ? $"You are muted and cannot speak{left}."
            : $"You are muted and cannot speak{left}: {_muteReason}");
    }

    // @mute <name> [minutes] [reason]
    private void MuteCmd(string args)
    {
        if (!ParseModArgs(args, $"Usage: {Prefix}mute <name> [minutes] [reason]   (no duration = permanent)",
                          out var name, out var until, out var reason)) return;

        if (!CharacterStore.CharacterExists(name)) { SendLog($"No character named \"{name}\"."); return; }
        if (StaffAccounts.IsGm(name)) { SendLog("You cannot mute a GM."); return; }

        if (!Moderation.Mute(name, until, reason, _char.Name)) { SendLog("Mute FAILED — the write did not land."); return; }

        // Push it onto the live session so the very next line they type is already blocked.
        var online = _world.Online.FindPlayer(name);
        online?.ApplyMute(until, reason);

        SendLog($"Muted {name} ({Moderation.Describe(until)})" + (reason.Length > 0 ? $": {reason}" : "."));
        Log.Info($"   -> {Prefix}mute by '{_char.Name}': {name} until={Moderation.Describe(until)} reason='{reason}'");
    }

    // @unmute <name>
    private void UnmuteCmd(string args)
    {
        var name = args.Trim();
        if (name.Length == 0) { SendLog($"Usage: {Prefix}unmute <name>"); return; }

        var rec = Moderation.Get(name);
        if (rec?.IsMuted != true) { SendLog($"{name} is not muted."); return; }

        if (!Moderation.Unmute(name, _char.Name)) { SendLog("Unmute FAILED — the write did not land."); return; }
        _world.Online.FindPlayer(name)?.ApplyMute(0, "");

        SendLog($"Unmuted {name}.");
        Log.Info($"   -> {Prefix}unmute by '{_char.Name}': {name}");
    }

    // @kick <name> [reason] — disconnect without any lasting record beyond the mod log.
    private void KickCmd(string args)
    {
        var parts = args.Trim().Split(' ', 2);
        var name = parts[0].Trim();
        var reason = parts.Length > 1 ? parts[1].Trim() : "";
        if (name.Length == 0) { SendLog($"Usage: {Prefix}kick <name> [reason]"); return; }

        var target = ResolveOnlinePlayer(name, $"{name} is not online.", RefuseChannel.Log);
        if (target is null) return;
        if (target == this) { SendLog("You cannot kick yourself."); return; }

        // Save, tell, drop — one critical section on the TARGET (#29 rule 2, Server/Session.State.cs), which
        // is exactly what KickForReplacement already does for the duplicate-login eviction
        // (Session.CharacterApi.cs: EnterState, its unconditional write and then the _replaced latch, a line,
        // CloseConnection). The two halves were guarded unevenly here: FlushNow enters the monitor for itself to
        // take the snapshot (FlushNow and CaptureAndWrite in Session.CharacterApi.cs), while the notice and
        // Disconnect's read of their _char.Name for the log line ran bare from the operator's thread. Entering once around all three closes that and makes
        // the save, the notice and the teardown one section, so nothing of theirs can move between the snapshot
        // and the drop; FlushNow's own EnterState becomes the re-entrant case (rule 3), and that is precisely
        // why the target's monitor IS held across CaptureAndWrite's lock (_writeGate) section: the nested
        // EnterState returns a default guard (Session.State.cs:137) whose Dispose releases nothing
        // (Session.State.cs:175-180), so CaptureAndWrite's own using (EnterState()) exits without dropping
        // anything and its lock (_writeGate) { _store.SaveJson(...) } (CaptureAndWrite's write-gate block) runs
        // with the monitor still held. That is not a new lock order. It is the nesting KickForReplacement has
        // had all along (KickForReplacement, Session.CharacterApi.cs) and the one the read loop's own teardown
        // takes on every ordinary disconnect (Session.EndReadLoopAsync's WithState(TearDownWorldState), then the
        // teardown's final FlushNow()), and nothing in the tree takes a session monitor while holding a
        // _writeGate — FlushPair (Session.TimedEffects.cs) closes its WithStatePair before taking either gate —
        // so monitor -> _writeGate cannot cycle. The cost, not a hazard: the kicked player's monitor is held
        // across a synchronous store write, where on master FlushNow released it first. No new
        // lock and no new lock order. Rule 1 holds: FindPlayer takes and releases World._lock inside itself
        // (OnlineRegistry.FindPlayer, World.OnlineRegistry.cs), and CloseConnection takes no lock at all.
        target.WithState(() =>
        {
            // Save before dropping them — a kick must never cost the player progress they'd earned.
            target.FlushNow();
            target.SendMessage(reason.Length > 0 ? $"You were disconnected by a GM: {reason}" : "You were disconnected by a GM.");
            target.Disconnect("kicked");
        });

        Moderation.Log(_char.Name, "kick", name, reason);
        SendLog($"Kicked {name}." + (reason.Length > 0 ? $" ({reason})" : ""));
        Log.Info($"   -> {Prefix}kick by '{_char.Name}': {name} reason='{reason}'");
    }

    // @banip <ip> [minutes] [reason] | @banip remove <ip>
    private void BanIpCmd(string args)
    {
        args = args.Trim();
        if (args.Length == 0) { SendLog($"Usage: {Prefix}banip <ip> [minutes] [reason] | {Prefix}banip remove <ip>"); return; }

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts[0].Equals("remove", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("unban", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2) { SendLog($"Usage: {Prefix}banip remove <ip>"); return; }
            SendLog(Moderation.UnbanIp(parts[1], _char.Name) ? $"Lifted the ban on {parts[1]}." : "Failed.");
            return;
        }

        if (!System.Net.IPAddress.TryParse(parts[0], out _)) { SendLog($"\"{parts[0]}\" is not an IP address."); return; }

        long until = Moderation.Forever;
        int i = 1;
        if (parts.Length > 1 && double.TryParse(parts[1], out double mins)) { until = Moderation.Deadline(mins); i = 2; }
        string reason = i < parts.Length ? string.Join(' ', parts[i..]) : "";

        SendLog(Moderation.BanIp(parts[0], until, reason, _char.Name)
            ? $"Banned {parts[0]} ({Moderation.Describe(until)})" + (reason.Length > 0 ? $": {reason}" : ".")
            : "Failed.");
        Log.Info($"   -> {Prefix}banip by '{_char.Name}': {parts[0]} until={Moderation.Describe(until)}");
    }

    // @bans — everyone currently banned or muted.
    private void BansCmd(string args)
    {
        var list = Moderation.ActiveList();
        if (list.Count == 0) { SendLog("Nobody is banned or muted."); return; }

        SendLog($"{list.Count} active:");
        foreach (var r in list.Take(20))
        {
            if (r.IsBanned)
                SendLog($"  {r.Username}  BAN {Moderation.Describe(r.BanUntil)} by {r.BanBy}"
                      + (r.BanReason.Length > 0 ? $" — {r.BanReason}" : ""));
            if (r.IsMuted)
                SendLog($"  {r.Username}  MUTE {Moderation.Describe(r.MuteUntil)} by {r.MuteBy}"
                      + (r.MuteReason.Length > 0 ? $" — {r.MuteReason}" : ""));
        }
        if (list.Count > 20) SendLog($"  … and {list.Count - 20} more.");
    }

    // @modlog [n] — the last n moderation actions, newest first.
    private void ModLogCmd(string args)
    {
        int n = int.TryParse(args.Trim(), out var parsed) ? Math.Clamp(parsed, 1, 40) : 15;
        var rows = Moderation.RecentLog(n);
        if (rows.Count == 0) { SendLog("The moderation log is empty."); return; }

        foreach (var (at, actor, action, target, detail) in rows)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("MM-dd HH:mm");
            SendLog($"  {when}  {actor} {action} {target}" + (detail.Length > 0 ? $"  [{detail}]" : ""));
        }
    }

    /// <summary>Drop this session's connection. The read loop's finally block does the rest (party/trade
    /// teardown, map exit, final save), exactly as it does for a player who closed their client.</summary>
    internal void Disconnect(string why)
    {
        Log.Info($"   -> disconnecting '{_char.Name}': {why}");
        // EXPECTED: the player may have dropped before the kick reached them; the outcome we want (they are
        // not connected) is the same either way, and the line above already records the intent.
        CloseConnection($"kicked: {why}", drain: true);
    }
}
