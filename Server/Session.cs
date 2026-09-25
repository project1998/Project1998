using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

/// <summary>
/// One client connection. Frames incoming packets, decrypts, dispatches, and replies.
/// This is the disposable 4.95 adapter behavior; the reusable world logic will live elsewhere.
/// </summary>
public sealed partial class Session
{
    /// <summary>Where this session's frames go. Everything above it — the 42 opcode handlers, world entry,
    /// trade, combat — only ever calls Send, so what sits underneath can be a socket or a recorder.</summary>
    private readonly IOutbound _out;

    /// <summary>The TCP transport, when this session came off a socket; null for a socket-free one. ONLY the
    /// read loop needs it (it owns the inbound half, which <see cref="IOutbound"/> deliberately does not
    /// cover); every outbound path goes through <see cref="_out"/>.</summary>
    private readonly TcpOutbound? _tcp;

    /// <summary>The socket's OWN peer address, which is the proxy's when one is in front — deliberately not
    /// <see cref="_remoteIp"/>, which is the player's. The re-login throttle and IP ban read this because
    /// that is what they read when it was <c>_client.Client.RemoteEndPoint</c>. <c>IPAddress.None</c> for a
    /// socket-free session, matching the fallback that address expression already had.</summary>
    private readonly System.Net.IPAddress _peerIp;

    private readonly int _port;
    private readonly string _remote;
    private readonly string _remoteIp;   // address only (no port) — handoff tokens are bound to it
    private readonly CharacterStore _store;
    private readonly World _world;   // the shared world (players + mobs); every broadcast goes through it

    /// <summary>Every staff override this session is under (#57 finding 31): @clip, @peace, @anywarp,
    /// @showwarps and its marker frames, and the three melee sfx slots. <c>readonly</c> and never null, so
    /// a read site is a field load through one indirection and nothing else — <c>SendMapRect</c> reads
    /// <c>_gm.NoClip</c> once per streamed cell. All of them are OFF/default at login by design; see
    /// <see cref="GmOverrides"/>. <c>@toggles</c> is the readout.</summary>
    private readonly GmOverrides _gm = new();

    private string _user = "?";
    private bool _enteredWorld;   // true once world entry loaded _char; gates the disconnect save

    // Party/trade (§11 party+trade): transient, session-owned, never persisted — matches RTK, where both
    // "groups" and "exchange" live only in the in-memory USER struct, not the DB.
    private Party? _party;
    private Trade? _trade;

    // Outbound decoupling (DDoS / tick-stall defense) lives in TcpOutbound, below: Send() hands the frame
    // to _out and never blocks, and the socket write happens on that transport's own writer task.
    private int _closed;   // 0 until the connection is being torn down; set once (Interlocked) — idempotent close
    // True from the FIRST statement of TearDownWorldState on, written and read only under this session's monitor
    // (#173). _closed cannot say this: on an ordinary disconnect CloseConnection sets it only AFTER the teardown
    // has returned, and the teardown can drop our monitor part-way (RemoveFromParty's broadcast descends into a
    // lower-ranked member), so a peer holding our monitor needs its own way to see we are on our way out.
    private bool _leaving;

    // Slow-loris defense: the budget for a freshly-accepted connection's FIRST valid framed packet, and the
    // watchdog that enforces it, are FrameReader's (P1998_HANDSHAKE_MS — see FrameReader.DefaultHandshakeMs
    // for the whole rationale, which both processes now share). The latch stays here: the status probe below
    // reads it from outside the read loop, and StatusResponder documents it here.
    private int _established;   // 0 until the first valid packet is parsed; gates the handshake timeout

    // --- robust persistence (dirty-flag autosave, see MarkDirty/FlushNow) ---
    // Bounds worst-case data loss on a hard server-process crash (OOM / kill / power loss, none of which
    // can run the graceful-shutdown flush hook) to roughly AutoSaveMs, without rewriting the whole
    // multi-KB character blob on every single mutation. A mutation site calls MarkDirty(); the session's
    // own read-loop thread flushes it (FlushIfDue, race-free — mutations and the flush both run on this
    // same thread) at most once per AutoSaveMs, and World's periodic sweep (AutoSaveLoop) catches an IDLE
    // dirty player (one who mutated state, then stopped sending packets) on the same cadence.
    // Internal (not private) so World.AutoSaveLoop ticks on the exact same cadence as this session's own
    // FlushIfDue, instead of duplicating the env-var parsing and risking the two drifting apart.
    internal static readonly int AutoSaveMs = ServerConfig.Current.AutoSaveMs;
    private volatile bool _dirty;
    private long _lastSaveAtMs;

    // ---- input-silence diagnostics (see Watchdog.ScanSessions) --------------------------------------
    // The reported symptom is "mobs keep moving but my character can't move or act": the world is fine and
    // we are still sending to this client, but it has stopped sending US anything. Neither side of that is
    // visible in the packet log — an idle player and a wedged one look identical — so record the last time
    // each direction carried traffic and let the watchdog spot the asymmetry.
    private long _lastInboundMs = Environment.TickCount64;
    private long _lastOutboundMs = Environment.TickCount64;
    internal long LastInboundMs  => Volatile.Read(ref _lastInboundMs);
    internal long LastOutboundMs => Volatile.Read(ref _lastOutboundMs);
    internal byte LastInboundOp;    // opcode of the last packet the client sent us
    internal byte LastOutboundOp;   // opcode of the last frame we queued for it
    internal string Remote => _remote;

    /// <summary>One-line dump of everything that could plausibly be gating this client's input, for the
    /// silence watchdog. Deliberately reads only cheap fields — it runs off the watchdog thread.</summary>
    internal string DiagState()
    {
        long now = Environment.TickCount64;
        return $"last-in 0x{LastInboundOp:x2} {now - LastInboundMs}ms ago, " +
               $"last-out 0x{LastOutboundOp:x2} {now - LastOutboundMs}ms ago, " +
               $"outq {_out.QueueDepth}, pos ({_char.X},{_char.Y}) map {_char.Map}, " +
               $"action-budget {_actionCount}/{ActionBudget} (window {_actionWindow}), " +
               // A non-null _dlgReply means we are awaiting a 0x3A and the client is sitting in a MODAL
               // dialog — which is itself a state where it will not send walks. Prime suspect for a
               // "can't move but the world keeps going" report, so it is called out explicitly.
               $"queued-casts {_queuedCasts.Count}, awaiting-dialog-reply {(_dlgReply is not null ? "YES" : "no")}, " +
               $"trade {(_trade is not null ? "OPEN" : "none")}, dirty {_dirty}";
    }
    // Set once a newer login for the same account superseded this session (World.OnlineRegistry.Register,
    // Session.KickForReplacement). Gates the disconnect save, so a slow OLD session never clobbers the NEW one.
    private int _replaced;
    internal bool IsReplaced => Volatile.Read(ref _replaced) != 0;   // no monitor: read under World._lock (#183)
    // Serializes the DATABASE WRITE for this session, and nothing else (#29). It used to be _saveGate and it
    // used to cover the capture as well; consistency of the captured bytes is the state monitor's job now
    // (see FlushNow), so what is left here is purely write ORDER — the snapshot is taken under the monitor
    // and written with it released, and without this two flushes could land out of order and roll a
    // character back to an older snapshot. Never held across a mutation, so it can't deadlock or stall the
    // read loop.
    private readonly object _writeGate = new();
    private long _saveSeq;      // ++ per captured snapshot, under the state monitor
    private long _writtenSeq;   // the newest snapshot actually written, under _writeGate

    // --- client version, tagged by the port the connection arrived on (unified dual-client server) ---
    // 4.95 speaks the original protocol (local map files; incoming 0x06 = walk-sync). 5.33 streams
    // terrain from the server (incoming 0x05/0x06 = map-data requests -> reply 0x06). Rather than sniff
    // the wire we give 5.33 its own listener ports and stamp the version here; the proven 4.95 path is
    // never entered by a 5.33 session. Login 2000 / game 2005 = V495; login 2001 / game 2006 = V533.
    public enum ClientVersion { V495, V533 }
    private readonly ClientVersion _ver;
    private bool IsLoginPort => ChannelPorts.IsLogin(_port);

    // --- world-light diagnostic knobs (env-tweakable, no rebuild needed to sweep) ---
    //   P1998_LIGHT      integer 0..65535, the map light/darkness value (default 232, proven bright on 4.95)
    //   P1998_LIGHT_FMT  how to encode it on the 0x15: "beu16" (default, 4.95), "leu16", or "u8"
    // 5.33 draws terrain black with the 4.95-proven be-u16 232; sweeping these isolates whether the
    // client reads the light field at a different width/endianness (leading 00 -> light 0 -> black).
    private static readonly int LightValue = ServerConfig.Current.LightValue;
    private static readonly string LightFmt = ServerConfig.Current.LightFormat;

    // Per-version tile translation lives in TileTranslation. A 4.x ground word selects one of TWO legacy
    // sheets (>= 0xC000 means "sheet 2, index v-0xC000"), which 5.33 merged into one: sheet 1 comes out as
    // identity and sheet 2 needs a lookup table. The object short addresses the SObj.tbl id space and is
    // NOT renumbered — though 5.33 re-authored its collision flags, which TileTranslation.Object works
    // around. See that class for the evidence and the env knobs.

    // 5.33 terrain-render diagnostic for the 0x06 stream (set via a .bat so PowerShell's `set` quirk
    // can't bite). "" = real map tiles. "sweep" = ramp the ground index across the whole visible rect
    // over the FULL 16-bit range (0..28550) so we can read off which indices actually draw a tile.
    // "solid:N" = fill with ground index N. Both send the tile UNTRANSLATED and UNMASKED — they probe the
    // client's own numbering, so running them through the translation would beg the question.
    // "ground:N" is the opposite and usually the one you want: it puts the 4.x ground WORD N through the
    // real translation, so it exercises the sheet selector (ground:49152 = sheet-2 frame 0).
    //
    // A uniform fill proves less than it looks like: tile sheets group related terrain, so several
    // consecutive frames are often the same material and one screenshot can be consistent with three
    // different offsets at once. The authoritative check is the offline both-pipelines render comparison
    // in docs/5.x/Reverse-Engineering.md, which never involves the client.
    private static readonly string MapDiag = ServerConfig.Current.MapDiag;

    // Server-side passability (collision). P1998_PASS=0 disables it (walk through anything) if the 4.x
    // top-2-bits polarity turns out wrong for a given map. Default on. Mithia 7.x: read_pass!=0 => blocked.
    private static readonly bool PassEnforce = ServerConfig.Current.PassEnforce;
    // Parsed block value for the passtest:N diagnostic (default 1); shared by the map stream + collision.
    private static readonly int PassTestN =
        MapDiag.StartsWith("passtest:") && int.TryParse(MapDiag.AsSpan(9), out var ptn) ? ptn : 1;

    // 4.95 self-walk: 0x0C starts a LOCAL-PREDICTION animation, entirely self-timed client-side (confirmed
    // live via Frida: the walk-frame counter at entity+0x18e advances on its own, ~90ms/tick, with ZERO
    // packets from us in between). But the client caps local prediction at exactly 2 ticks (~180ms) and
    // then FREEZES — it will not advance further until our 0x04 arrives, at which point it snaps straight
    // to completion. So 0x04 must land just AFTER that natural ~180ms window: too early truncates the
    // prediction (looks like an instant snap); too late just prolongs the freeze before the snap.
    // P1998_V495_WALK_MS tunes it (0 = old same-frame slide, sent before ANY tick can play).
    private static readonly int V495WalkMs = ServerConfig.Current.WalkMs;

    // Self-walk drive mode for 4.95, chosen by static RE of the client's animation path.
    //   start-walk @0x462320  sets walk-active [+0x18c]=1 and registers the anim timer (0x41b5d0)
    //                         UNCONDITIONALLY, then repositions logical=dest ONLY if current!=dest.
    //   walk-render @0x44b140 draws screen = logical + forward_step*(frameCtr/4)  [dir->delta @0x44aad0].
    //   0x04 handler @0x44faf0 -> @0x44c660 sets decaying scroll offsets [+0xb8]=-12/[+0xbc]=-10 = a
    //                         SMOOTH camera scroll, independent of 0x0C.
    // So the OVERSHOOT (sprite lands on dest then slides past) comes only from the reposition, which is
    // gated on current!=dest. The LEG cycle comes from walk-active + anim timer, set before that gate.
    // Modes (P1998_V495_SELF_MOVE):
    //   0 = 0x04 only            -> smooth camera scroll, but NO legs (walk-active never set)
    //   1 = 0x0C(dest)+delay+0x04-> legacy; legs but forward overshoot + snap on the final step
    //   2 = 0x0C(SOURCE)+0x04    -> legs on (walk-active set) with reposition SKIPPED (dest==current);
    //                              still cancelled by 0x04's move-commit, so no visible legs.
    //   3 = send nothing         -> pure local walk; 1 PERFECT animated step, then the client blocks
    //                              awaiting a server ack (proven live: local controller does move+legs+camera).
    //   5 = delay + 0x04         -> DEFAULT: send nothing immediately so the LOCAL controller animates the
    //                              full step (legs + slide + camera), then after the anim completes send 0x04
    //                              to unblock the next step. By then move-commit's unregister is a no-op, so
    //                              the legs are NOT cancelled. P1998_V495_ACK_MS tunes the delay.
    //   7 = nothing on a good walk (RTK-faithful) -> DEFAULT: client moves/animates/scrolls locally; 0x04
    //       is sent ONLY as a correction (desync/block). Stops our per-step 0x04 from re-scrolling the
    //       camera the client already moved (the residual "wonkiness" + fighting realm-center).
    private static readonly int V495SelfMove = ServerConfig.Current.SelfMove;

    // Delay (ms) before the mode-5 unblock 0x04. Must be >= the client's local walk animation (~4 frames,
    // ~360ms) so the 0x04 lands AFTER the legs finish and doesn't cancel them. Too short => truncated legs;
    // too long => sluggish walk cadence (the client gates the next step on this ack).
    private static readonly int V495AckMs = ServerConfig.Current.AckMs;

    // FAST-MOVE OFF (server-authoritative) response strategy for 4.95. When fast-move is off the client
    // makes NO local prediction — it sends the walk request and waits for the server to assign the step
    // before moving/animating. The old behavior (0x04 only) slides with no legs because 0x04's handler
    // (0x44faf0 -> move-commit) teleports the sprite and never runs the leg cycle. But 0x0C runs the SAME
    // start-walk (0x462320) a PEER uses: it sets walk-active(+0x18c)=1, frameCtr(+0x18e)=0, REGISTERS the
    // anim list (0x41b5d0), moves logical->dest, and sets up the source->dest screen interpolation
    // (0x44b090) — a full animated step with legs. And 0x0C does NOT branch self-vs-peer, so the self
    // animates exactly like the peers we already drive smoothly. Strategies:
    // The render (0x44b140) draws  screen = screen(entity.logical) + FORWARD_unit*(frameCtr/4)  where
    // FORWARD_unit is the dir delta (0x44aad0) and start-walk (0x462320) sets logical=DEST at frame 0 —
    // BUT ONLY IF the 0x0C tile != current logical (the reposition guard). So the tile we put in the 0x0C
    // decides the anchor:  0x0C(DEST) -> logical jumps to dest, sprite starts ON dest and drifts to +2
    // (OVERSHOOT); 0x0C(SOURCE) -> guard trips, logical stays at source, sprite animates source->dest
    // correctly, and the delayed 0x04(dest) commits the landing. Strategies:
    //   0 = 0x04 only            -> legacy: assigns tile + camera, but SLIDES (no legs).
    //   1 = 0x0C(dest) only      -> legs but OVERSHOOTS to +2 then needs a commit to snap back.
    //   2 = 0x0C(SOURCE)+delayed 0x04(dest) -> DEFAULT: source-anchored legs (no overshoot); the delayed
    //                               0x04 commits logical=dest AFTER the legs finish so it can't cancel them.
    //   3 = 0x0C(dest)+delayed 0x04 -> the overshoot variant (kept to compare against 2).
    //   4 = 0x0C(SOURCE) only    -> source-anchored legs but no commit -> may snap back to source.
    //   5 = 0x26 self-walk (DEFAULT) -> the real smooth primitive: routes to handlerB (0x4903d0) which
    //       move-commits the step + starts the next locally ([+0x65f3]=1, no wait, no 0x04, no forced
    //       scroll). Same packet 5.33 uses; respects realm-center. See HandleWalk for the full RE trail.
    private static readonly int V495SlowMove = ServerConfig.Current.SlowMove;

    // "Realm center" (F4 in RTK) — a CLIENT camera mode signalled by a flag byte in the 0x15 mapinfo
    // packet (RTK clif_sendmapinfo byte +12; our SendMapInfo body[7]). When ON the client locks the camera
    // dead-center on the character (the world scrolls under a fixed sprite); when OFF the camera uses the
    // edge-aware/offset behavior that can lag during a walk. The 4.95 client honors it: its 0x15 handler
    // (@0x44f8b0) reads this byte, computes (realm==0), and feeds it to the view/camera rebuild (@0x44c570).
    // P1998_V495_REALM=1 enables it to test whether a locked camera fixes the walk "wonkiness".
    private static readonly byte RealmCenter = ServerConfig.Current.RealmCenter ? (byte)1 : (byte)0;

    /// <summary>The production constructor. It is now an ADAPTER: it wraps the socket in a
    /// <see cref="TcpOutbound"/> — the queue, the writer task and the address bookkeeping all moved there,
    /// unchanged — and hands that to the real constructor below.</summary>
    /// <param name="realIp">The client's true address when a trusted proxy sits in front and the listener has
    /// already consumed its PROXY header (see Shared/ProxyProtocol.cs). Null on a direct connection, where the
    /// socket's peer IS the client. See <see cref="TcpOutbound"/> for why every address downstream has to be
    /// the player's rather than the proxy's.</param>
    public Session(TcpClient client, int port, CharacterStore store, World world,
                   System.Net.IPAddress? realIp = null)
        : this(new TcpOutbound(client, OutboundOptions.Game, realIp), port, store, world)
    {
    }

    /// <summary>The transport-free constructor. Takes whatever the session should write to instead of a
    /// socket, which is what lets a test build one, drive a handler and read back the exact bytes that would
    /// have gone on the wire. <see cref="RunAsync"/> is the only member that needs a real socket and refuses
    /// to run without one; every handler works the same either way.</summary>
    /// <param name="character">The character the arrival path (<c>HandleArrival</c>) would have loaded out of
    /// the store. Supplying one also marks the session as having entered the world, because that is the state
    /// a handler test needs: the character API is live and saves flush. Null leaves both untouched.</param>
    public Session(IOutbound outbound, int port, CharacterStore store, World world,
                   Character? character = null)
    {
        _out = outbound;
        // The read loop owns the INBOUND half, which IOutbound deliberately does not describe — so it needs
        // the socket itself. A session built on any other transport simply has no read loop.
        _tcp = outbound as TcpOutbound;
        _port = port;
        _store = store;
        _world = world;
        _ver = ChannelPorts.IsV533(port) ? ClientVersion.V533 : ClientVersion.V495;
        // Keep the proxy's own address in the log line: when the allow-list or the HAProxy backend is
        // misconfigured, "which proxy claimed this" is the only thing that distinguishes a real player
        // from a forged header, and it is not recoverable after the fact.
        _remote = outbound.Remote;
        _remoteIp = _tcp?.RemoteIp ?? System.Net.IPAddress.None.ToString();
        _peerIp = _tcp?.PeerAddress ?? System.Net.IPAddress.None;
        if (character is not null) { _char = character; _enteredWorld = true; }
    }

    public async Task RunAsync()
    {
        // The inbound half is TCP-only: a session built on some other IOutbound has no stream to read.
        var tcp = _tcp ?? throw new InvalidOperationException(
            "RunAsync needs a TCP transport; this session was built on a socket-free IOutbound.");

        Log.Info($"++ CONNECT from {_remote} on port {_port} [{_ver}]");
        // Start the dedicated outbound writer BEFORE any Send() so the very first packet (the welcome) is
        // enqueued and flushed in order. All socket writes happen on this one task; the read loop below never
        // writes to the stream directly.
        var writer = Task.Run(() => tcp.RunWriterAsync(CloseConnection));
        try
        {
            if (IsLoginPort)   // login channel: send the 0x7E welcome
            {
                Send(Welcome.Bytes);
                Log.Info($"   -> sent welcome ({Welcome.Bytes.Length}B)");
            }
            else                 // game channel: the client speaks first (sends 0x10). Send NOTHING now.
            {
                // Reversing NexusTK.exe shows the game socket's receive path is identical to login's
                // (mode 0x3aa4c=5 -> recv loop 0x477fc0 -> decrypt 0x478680 -> WndProc queue). There is
                // no server greeting on the game port and no seed/ack handshake. Sending unsolicited
                // packets before the client's 0x10 only risks desyncing its frame assembler, so we wait.
                Log.Info("   == game connect: waiting for client 0x10 arrival (no pre-arrival sends) ==");
            }

            // The read loop itself is shared with the login process (Protocol.Tk495/FrameReader): the
            // handshake watchdog, the 4KB chunk, the TkPacket framing and the two wire dumps are all its.
            // What is left here is what this process does differently, at the points it did them before.
            var frames = new FrameReader(tcp.Stream, _port, _remote, new FrameReader.Hooks
            {
                // Gated on _established so a late watchdog fire can never drop a connection that has already
                // spoken; closing the socket makes the pending read throw and unwind into the finally cleanup.
                Established = () => Volatile.Read(ref _established) != 0,
                OnEstablished = () => Volatile.Write(ref _established, 1),   // first valid frame parsed -> handshake satisfied
                OnHandshakeTimeout = () => CloseConnection("handshake timeout"),
                OnRead = _ => Volatile.Write(ref _lastInboundMs, Environment.TickCount64),   // silence watchdog
                // Status probe: on the GAME port the client speaks first, so a connection whose first
                // bytes are "GET " is an HTTP status poll, never a real client (see StatusResponder).
                // Answered with a direct stream write — safe here precisely because nothing else has
                // been sent on a pre-established game connection (the writer task is idle) — then the
                // loop breaks and the normal finally cleanup closes the socket.
                OnBufferedAsync = async buf =>
                {
                    if (Volatile.Read(ref _established) != 0 || IsLoginPort)
                        return false;

                    // The reader's next step drops every non-0xAA head, so finish sniffing only a proper
                    // prefix here. Append exactly one bounded stream read at a time to this live buffer; a
                    // coalesced request tail must be consumed too, or closing with it unread can reset the
                    // response. The reader's handshake watchdog still closes this pending read on deadline.
                    while (StatusResponder.IsHttpPrefix(buf))
                    {
                        var followUp = new byte[FrameReader.ReadBufferBytes];
                        int n = await tcp.Stream.ReadAsync(followUp);
                        if (n == 0) return true;
                        for (int i = 0; i < n; i++) buf.Add(followUp[i]);
                    }

                    if (!StatusResponder.LooksLikeHttp(buf)) return false;
                    await tcp.Stream.WriteAsync(StatusResponder.Build(_world));
                    Log.Info($"   -> status probe from {_remote} answered ({_world.Online.Count} online)");

                    // Half-close, then drain, BEFORE the loop exits into CloseConnection: closing a socket that
                    // still has unread bytes in its receive queue makes Winsock answer with an RST, which
                    // discards the response we just wrote (measured in the PR #228 review, F2: a probe whose
                    // request tail arrives after the fourth byte got zero bytes while the log said "answered").
                    // Shutting the send side down instead delivers the response and a FIN, and the discarding
                    // read loop below leaves nothing unread when the socket finally closes. Bytes read here are
                    // CONSUMED, not appended to the live buffer: the hook returns true immediately after, so
                    // the reader stops and would never frame them.
                    try { tcp.Stream.Socket.Shutdown(SocketShutdown.Send); }
                    catch (Exception e) when (e is SocketException or ObjectDisposedException)
                    {
                        // The peer already reset or our own CloseConnection got here first. Nothing left to
                        // half-close, and nothing left to drain either.
                        Log.Warn($"   -> status probe from {_remote}: send shutdown skipped ({e.GetType().Name})");
                        return true;
                    }

                    // No new timer: the reader's handshake watchdog is the bound. A probe never frames a packet,
                    // so _established stays 0 and the watchdog still fires on its budget, closing the socket
                    // under this pending read; the exception unwinds into RunAsync's typed catch, exactly as it
                    // does for the prefix wait above.
                    var drain = new byte[FrameReader.ReadBufferBytes];
                    while (await tcp.Stream.ReadAsync(drain) > 0) { }
                    return true;
                },
                AfterRead = FlushIfDue,   // throttled autosave; no-op unless MarkDirty()'d and AutoSaveMs has elapsed
            });

            await foreach (var pkt in frames.ReadFramesAsync())
            {
                LastInboundOp = pkt.Opcode;
                Handle(pkt);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // The socket went away under the read: a reset from the client, a half-open link timing out,
            // or our own CloseConnection (handshake timeout, slow-client drop) racing the pending ReadAsync.
            // This happens on every live server several times an hour and the stack is the same handful of
            // runtime frames each time, so it gets type + message only. Anything ELSE reaching this catch is
            // unexpected — handlers are guarded per packet in Handle — and the clause below keeps the stack.
            Log.Warn($"{_remote} read loop ended: {e.GetType().Name}: {e.Message}");
        }
        catch (Exception e) { Log.Error($"{_remote} read loop threw — dropping the connection", e); }
        finally
        {
            await EndReadLoopAsync(writer);
        }
    }

    /// <summary>The read loop's exit: the world teardown, then the connection close, the writer await and the
    /// CLOSE line. Split out of <see cref="RunAsync"/>'s <c>finally</c> so a socket-free session can run it.
    ///
    /// <para><b>The close is in a <c>finally</c> of its own (#168).</b> Anything the teardown throws used to leave
    /// this method before <c>CloseConnection</c>: the socket stayed open on our side, a client whose read loop
    /// ended on our own exception stayed connected to a session nobody reads, and no CLOSE line was logged. The
    /// exception still leaves (TkAcceptor logs it and releases the admission slot, as before), but only after
    /// the connection is closed and the CLOSE line is written. The teardown's own save is fenced inside it, so
    /// a character the serializer rejects no longer reaches here at all.</para></summary>
    internal async Task EndReadLoopAsync(Task writer)
    {
        try
        {
            // Teardown is one critical section (#29). It is the last thing that touches this session's state
            // and it runs while the tick and the autosave sweep are still perfectly entitled to enter us —
            // the leave/unregister is exactly what stops that, so everything before it has to be inside the
            // monitor too, or a RegenTick can land between the final flush and the deregistration.
            WithState(TearDownWorldState);
        }
        finally
        {
            CloseConnection("read-loop exit");   // completes the outbound channel + closes the socket
            // EXPECTED: the writer task has its own catch and has already logged whatever it hit, with a stack
            // where it warranted one. Logging the same exception again here would double every writer fault.
            try { await writer; } catch { /* writer logs its own errors */ }
            Log.Info($"-- CLOSE {_remote}");
        }
    }

    /// <summary>Everything the read loop's exit does to this session's own state, in one place so it can run
    /// as one critical section (see <see cref="EndReadLoopAsync"/>). In order: the leaving latch (#173), the
    /// trade and the party, the map, the account slot — parked for the next login's fence rather than dropped
    /// (#168) — and the final save, which is fenced so a capture that throws is logged as a lost save instead
    /// of escaping (#168).</summary>
    private void TearDownWorldState()
    {
        _leaving = true;   // first, before anything below can drop the monitor: TryStartTrade refuses from here on (#173)
        // Drop out of any live party/trade so the other side(s) aren't left waiting on someone who's gone
        // (RTK: a dropped exchange partner's session simply vanishes from map_id2sd, which is exactly what a
        // disconnect does here too — the difference is we also close the survivor's exchange window with
        // RTK's own "Exchange cancelled." box rather than leaving it open on a ghost).
        if (_trade is not null) EndTrade(_trade, "Exchange cancelled.");
        if (_party is { } party) RemoveFromParty(this, party);   // read under our own monitor: this runs inside WithState

        // Leave the shared world: despawn us for the other players on our map. World mobs persist
        // (they belong to the map, not this session), so they keep wandering for whoever remains.
        if (_enteredWorld) _world.LeaveMap(this, _char.Map);
        // Give the account's slot back, parked in the departed table rather than dropped (#168), and only if we
        // still own it. The next login for the account within one autosave interval is handed us and fences us
        // (HandleArrival -> ClaimAccountSlot -> KickForReplacement), so a late write still landing under our
        // monitor — a group share decided before this teardown, a mob swing queued before it — lands before
        // that login loads the row, and nothing from us lands after.
        if (_enteredWorld) _world.Online.Depart(UserKey, this, Environment.TickCount64);
        // Persist the last state (position/stats) only for a session that actually entered the world
        // AND wasn't superseded by a newer login for the same account (KickForReplacement already
        // wrote the freshest state; saving again here from this now-stale session would clobber it —
        // see the duplicate-login guard, World.OnlineRegistry.Register — and since #168 CaptureAndWrite
        // refuses it as well). The login-channel session never
        // populates _char, so saving it would clobber the real record with defaults.
        if (_enteredWorld && Volatile.Read(ref _replaced) == 0)
        {
            _dirty = true;
            // Fenced (#168). A capture that throws (a value the serializer rejects) re-dirties and rethrows out
            // of CaptureAndWrite; left alone it escaped the teardown with the save lost and nothing saying so in
            // those words. We have already left the map, so no sweep will retry it. Only the next login for this
            // account, within one interval, tries once more (its fence).
            try
            {
                FlushNow();
                Log.Info($"   -> persisted '{_char.Name}' at map {_char.Map} ({_char.X},{_char.Y})");
            }
            catch (Exception e)
            {
                Log.Error($"   -> disconnect save of '{_char.Name}' threw — save LOST: the session has left the " +
                          "world and no sweep will retry it", e);
            }
        }
    }

    // Ordinary failures drop the socket immediately. A rejection that just queued its explanation instead
    // lets TCP drain asynchronously, with a deadline; neither path waits while holding session/world locks.
    private void CloseConnection(string reason) => CloseConnection(reason, drain: false);

    private void CloseConnection(string reason, bool drain)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (drain && _tcp is not null) _tcp.CloseAfterDrain();
        else _out.Close();
        Log.Info($"   -> connection teardown ({reason})");
    }

    // ---- RTK's global action budget (clif_parse gates + pc_timer) ------------------------------------
    // NOTHING to do with aethers. RTK counts *every* action packet into one shared per-second budget,
    // `sd->time`, which pc_timer zeroes on a fixed 1000ms tick (pc.c:613). Each gated opcode does
    // `sd->time += 1; if (sd->time < 4) ...`, so the 1st/2nd/3rd action in a window run and the 4th onward
    // are DROPPED SILENTLY — no minitext, no reply, no log to the player. This is what actually stops spell
    // spam for the ~87% of spells whose script never calls setAether (114 of 394 RTK spell scripts do).
    //
    // The budget is SHARED across opcodes, and attack (0x13) increments it WITHOUT being gated by it (0x13
    // has its own attack_speed timer instead) — so swinging at full speed really does eat your own cast
    // allowance. Surprising, but it's RTK's, so it's kept.
    //
    // FIXED window, not sliding: RTK resets to 0 on a wall-clock tick, so 3 casts at the end of one second
    // plus 3 at the start of the next (6 back-to-back) is legal. TickCount64/1000 reproduces that exactly
    // without needing a per-session timer; the only drift is that RTK's window is anchored to the player's
    // login and ours to server boot, which nothing can observe.
    private const int ActionBudget = 4;   // RTK's `if (sd->time < 4)` -> 3 actions per window
    private long _actionWindow;           // which 1s window _actionCount belongs to
    private int  _actionCount;            // RTK sd->time

    private void RollActionWindow()
    {
        long window = Environment.TickCount64 / 1000;
        if (window != _actionWindow) { _actionWindow = window; _actionCount = 0; }
    }

    // RTK `sd->time += 1` — the raw increment, for 0x13 which pays into the budget but isn't gated by it.
    private int BumpActionTime() { RollActionWindow(); return ++_actionCount; }

    // RTK `sd->time += 1; if (sd->time < 4)` — the usual gated form.
    private bool ActionAllowed(byte op)
    {
        if (BumpActionTime() < ActionBudget) return true;
        Log.Info($"   -- op=0x{op:x2} dropped: action budget spent ({_actionCount} this second)");
        return false;
    }

    // RTK `if (sd->time < 4)` with NO increment — unequip (0x1F) only.
    private bool ActionBudgetLeft() { RollActionWindow(); return _actionCount < ActionBudget; }

    // ---- Queue over-budget casts instead of discarding them (P1998_CAST_QUEUE=0 to disable) ------------
    // Holding a cast key makes the client send 0x0F every ~31ms (OS auto-repeat, after a ~260ms initial
    // delay — both measured live). With a plain drop-gate that yields three casts spaced 31ms apart, then
    // ~940ms of silence, and 31ms of separation on three identical sounds is an audible flam. Real NexusTK
    // is ONE animation and ONE sound with three casts landing together, which is what you get if the
    // over-budget casts are HELD and released at the next window boundary instead of thrown away.
    //
    // INFERRED, NOT SOURCED. RTK discards (clif_parsemagic just falls through) and there is no 4.95 source
    // for this either way — it is here because it reproduces the real game's behavior by ear, which is the
    // only authority available. Treat it as a working reconstruction: if a real 4.95 source ever contradicts
    // it, the source wins. Depth is capped at the budget and keeps the NEWEST casts, so the queue means
    // "the key is still down", never a 30-deep backlog that keeps firing after you let go.
    private static readonly bool CastQueueEnabled = ServerConfig.Current.CastQueue;
    // How long a queued cast stays valid. Depth-capping bounds how MANY casts wait; this bounds how LONG,
    // which is the part that matters once the key comes up: the client stops sending, so nothing triggers a
    // drain, and the next packet of ANY kind (a walk step, seconds later) would otherwise fire casts from
    // the last time you held the key. While the key really is held the client repeats every ~31ms, so the
    // newest 3 are at most ~125ms old when the drain runs; 250ms clears that with room for the ~46ms jitter
    // seen live, while staying far below the ~1s that would let a stale cast leak somewhere visible.
    private const int CastQueueMaxAgeMs = 250;
    private readonly Queue<(byte[] Body, long Tick)> _queuedCasts = new();

    private void QueueCast(byte[] dec)
    {
        while (_queuedCasts.Count >= ActionBudget - 1) _queuedCasts.Dequeue();   // keep the newest only
        _queuedCasts.Enqueue((dec, Environment.TickCount64));
    }

    // Drained at the top of every inbound packet: the client is repeating at ~31ms while the key is held, so
    // this fires within ~31ms of the boundary without needing a timer. Uses the same bump-then-test as the
    // live gate, so a released cast costs budget exactly like a live one and the net rate stays 3/second —
    // the queue only changes WHEN they land (together), not HOW MANY.
    private void DrainQueuedCasts()
    {
        if (_queuedCasts.Count == 0) return;

        // Expire stale entries BEFORE the spent-window early-return, or a queue left over from a released
        // key would sit untouched until something else happened to drain it. FIFO with monotonic stamps, so
        // the head is always the oldest.
        long now = Environment.TickCount64;
        while (_queuedCasts.Count > 0 && now - _queuedCasts.Peek().Tick > CastQueueMaxAgeMs)
        {
            _queuedCasts.Dequeue();
            Log.Info("   -- queued cast expired (cast key released)");
        }
        if (_queuedCasts.Count == 0) return;

        RollActionWindow();
        if (_actionCount >= ActionBudget) return;               // still inside the spent window

        // ONE PASS over what is queued right now. HandleCast can put a cast STRAIGHT BACK on the queue when
        // the shared cast/swing slot is still busy (a held Invisible waiting out a swing), so an unbounded
        // `while (_queuedCasts.Count > 0)` would dequeue and re-enqueue the same entry until the budget ran
        // out. Bounding the pass leaves it queued for the next inbound packet instead — which is ~31ms away
        // while a key is held, and is exactly when we want to retry.
        for (int pass = _queuedCasts.Count; pass > 0 && _queuedCasts.Count > 0; pass--)
        {
            if (BumpActionTime() >= ActionBudget) break;
            Log.Info("   -- queued cast released at window start");
            HandleCast(_queuedCasts.Dequeue().Body);
        }
    }

    /// <summary>The inbound half of the test seam: deliver one already-framed packet exactly as the read loop
    /// would. Production traffic does NOT come through here — <see cref="RunAsync"/> frames straight off the
    /// socket and calls the same dispatcher — but a socket-free session has no read loop, so this is how a
    /// test drives a handler with real wire bytes and then reads the answer back off its
    /// <see cref="IOutbound"/>. The read loop's connection bookkeeping (the handshake latch, the throttled
    /// autosave) is deliberately NOT reproduced: those belong to a live socket, not to a packet.</summary>
    public void Receive(byte[] frame)
    {
        if (!TkPacket.TryParse(frame, out var pkt, out _))
            throw new ArgumentException($"not a framed packet: {Log.Hex(frame)}", nameof(frame));
        Volatile.Write(ref _lastInboundMs, Environment.TickCount64);
        LastInboundOp = pkt.Opcode;
        Handle(pkt);
    }

    private void Handle(TkPacket pkt)
    {
        // Framing (TkPacket.TryParse, in the read loop) and the cipher are the two failures that legitimately
        // END a session: after either, nothing later on the stream can be trusted. So the decrypt stays
        // OUTSIDE the guard below — a throw here still unwinds to RunAsync's catch and drops the connection.
        var dec = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
        if (Log.WireEnabled)
        {
            Log.Info($"   <- pkt op=0x{pkt.Opcode:x2} inc=0x{pkt.Increment:x2} len={pkt.Body.Length + 2} body={pkt.Body.Length}B");
            Log.Info($"        dec : {Log.Hex(dec)}");
        }

        // Past this point everything is a handler, and a handler that throws is a bug in THIS process, not
        // evidence that the stream is bad. Until #25 the only catch was RunAsync's, so one IndexOutOfRange
        // on a short body — or one NullReference in a 2,000-line partial — logged a single stackless line
        // and kicked the player, whose world state (party, trade, position) was then torn down as a normal
        // disconnect. The packet's own bytes are in the line because they are the repro: an unhandled edge
        // in a handler is nearly always a body shape the parser did not expect.
        // The session's state monitor wraps the WHOLE handler (#29), which is what makes a packet an
        // atomic unit of work against this player: all 42 handlers, every GM command, and every NPC dialog
        // continuation (the driver completes its TaskCompletionSource inline on this thread, so an awaited
        // behaviour resumes still holding it). The tick, the autosave sweep and peer sessions take the same
        // monitor at their own entry points.
        //
        // ONE EXCEPTION, and it is deliberate: a handler that enters Lua while some other thread is already
        // in Lua takes the gate's slow path, which drops this monitor while it waits (Session.State.cs). So a
        // CONTENDED cast is two critical sections with a gap, not one — the alternative was a deadlock, and
        // the gap is still strictly better than the nothing that guarded this before. Everything else, and
        // the uncontended cast, really is atomic.
        try { WithState(() => Dispatch(pkt, dec)); }
        catch (Exception e)
        {
            Log.Error($"{_remote} handler for opcode 0x{pkt.Opcode:x2} threw — the packet is dropped, the session continues; " +
                      $"body {dec.Length}B: {Convert.ToHexString(dec).ToLowerInvariant()}; {DiagState()}", e);
        }
    }

    // The opcode switch, split from Handle so the guard above wraps exactly the handlers and nothing else.
    private void Dispatch(TkPacket pkt, byte[] dec)
    {
        // A draining disconnect keeps the socket alive briefly for delivery, never for more commands.
        if (Volatile.Read(ref _closed) != 0) return;
        StartEntryMusicIfArmed(pkt.Opcode);   // login music waits for proof the client's world object is live

        switch (pkt.Opcode)
        {
            case ClientOp.Arrival:          HandleArrival(pkt); break;
            // 0x0B = "I just left the world for the select screen" (Alt+X). Answer it by sending the client
            // BACK to the login server, which is what RTK does and the only reason account creation from
            // that screen can work at all: NameCheck (0x02) and CreateAppearance (0x04) are handled by the
            // LoginServer process and there is no dispatch for them here. See HandleExitToSelect.
            case ClientOp.ExitToSelect:     HandleExitToSelect(); break;   // body is a constant 00
            // Login (0x03) arrives here when the client stayed on the game socket anyway — the pre-0x0B
            // behaviour, kept as a fallback: re-authenticate and hand it back to this same game port like
            // the old unified server did, so a client that ignores the bounce still gets in. See HandleReLogin.
            case ClientOp.Login:            HandleReLogin(dec); break;
            case ClientOp.WalkAlternate:                    HandleWalk(dec); break;   // client walk step -> confirm move
            // 0x11 = "side" (turn to face a direction, NO movement) for BOTH clients. The 4.95 client's
            // 0x11 recv handler (@0x450350) reads id(u32)@+1, side(u8)@+5, looks the entity up and calls
            // its turn method (@0x462410) -- exactly SendSide's layout. Previously dropped for 4.95, which
            // left facing unconfirmed until the next walk ("press a new direction, first step goes the OLD
            // way"). In NexusTK the first press in a new direction turns in place; only the second walks.
            case ClientOp.Turn:
                HandleTurn(dec);
                break;
            // 0x1b = client setting toggle. body[0] = which setting (RTK settings-parse cases):
            //   0x07 = Realm center (F4)   0x09 = Fast move   ... others not yet handled.
            case ClientOp.Setting:
                HandleSetting(dec);
                break;
            // Map/walk split — the SAME for both clients (4.95 corrected 2026-08-07):
            //   0x05 = map-data request (view rect) -> stream terrain back as 0x06 (HandleMapRequest).
            //   0x06 = walk (dir @ body[0], + reported pos/viewport) -> confirm move.
            // This block used to read "4.95 differs: 0x05 unused" and dropped every 4.95 0x05 with a
            // "no V495 handler" log line. That was wrong: 4.95 sends the IDENTICAL request, 2161 of them
            // in one session log, body =
            //     x0(u16BE) y0(u16BE) w(u8) h(u8) 00 checksum(u16BE) 00
            //     00 00 00 00 0c 0c 00 63 c2 00   -> (0,0)     12x12
            //     00 6d 00 7e 13 11 00 d1 1c 00   -> (109,126) 19x17   (the 17x15 viewport + pad)
            // — which is exactly what HandleMapRequest already parses. The 4.95 client streams its terrain
            // from the server like every later client; the local .map files are a CACHE it verifies (hence
            // the checksum, which the walk 0x06 also carries), not the only source. That's why some 4.x
            // client distributions ship no Maps directory at all.
            case ClientOp.MapRequest:
                HandleMapRequest(dec);
                break;
            case ClientOp.Walk:
                HandleWalk(dec);
                break;
            // 0x38 = hard refresh (Ctrl+R): the client grays the screen and asks the server to re-assert
            // authoritative state. RTK's clif_refresh replies with sendmapinfo + sendxy + re-drawn entities
            // (0x04 is the re-anchor primitive here — authoritative position + recentered camera). See §refresh.
            case ClientOp.Refresh:
                HandleRefresh(dec);
                break;
            case ClientOp.Chat:                    HandleChat(dec); break;   // client chat -> echo as over-head speech
            // 0x1d = emotion request (the ':' emote wheel). body[0] = emote index; the client plays action
            // type = index + 11 (RTK clif_parseemotion: sendaction(index+11)). Broadcast as a 0x1A action.
            case ClientOp.Emotion:                    if (ActionAllowed(ClientOp.Emotion)) HandleEmotion(dec); break;
            // 0x13 pays into the shared action budget but is NOT gated by it (RTK clif.c:11446) — melee has
            // its own attack_speed timer, and HandleAttack applies our equivalent swing pacing.
            case ClientOp.Attack:                    BumpActionTime(); HandleAttack(dec); break;  // client attack (spacebar) -> echo 0x13 anim
            case ClientOp.ProfileRequest:                    HandleProfileRequest(dec); break;  // profile key -> self-profile (0x39)
            case ClientOp.ClickInfo:                    HandleClickInfo(dec); break;       // click entity -> profile / NPC dialog
            // 0x3A = NPC dialog response (RTK clif_parsenpcdialog): the client sends this after the player
            // acts on a dialog we opened via 0x30. body[0] = kind (01 text next/close, 02 menu pick, 04 input
            // text). See HandleNpcDialog — a logging stub until the 0x30 send format is confirmed live.
            case ClientOp.NpcDialog:                    HandleNpcDialog(dec); break;
            case ClientOp.ChangeProfile:                    HandleChangeProfile(dec); break;   // edit profile -> save pic + blurb
            // ---- items (opcode numbers from RTK 7.x recv dispatch; confirmed to align with 4.95 by the
            // walk/turn/chat/attack/setting opcodes already matching). See §11c. ----
            case ClientOp.Pickup:                    if (ActionAllowed(ClientOp.Pickup)) HandlePickup(dec); break;    // pick up the floor item under me
            case ClientOp.DropItem:                    HandleDropItem(dec); break;  // drop a bag slot to the floor
            case ClientOp.Throw:                    HandleThrow(dec); break;     // throw a bag slot (flies ahead)
            case ClientOp.Eat:                    HandleUseItem(dec, eat: true); break;   // eat/consume a slot
            // 0x12 = the WIELD hotkey (press 'w', then the item's letter). Body = [slot(1-based), 00] — the
            // same shape as 0x1C, confirmed by live capture (wield sent `12 01 00`). Double-click already used
            // 0x1C; the hotkey just uses a different opcode, so route it to the same use/equip path.
            case ClientOp.Wield:                    HandleUseItem(dec, eat: false); break;  // wield hotkey -> equip a slot
            case ClientOp.UseItem:                    HandleUseItem(dec, eat: false); break;  // use/equip a slot
            // RTK checks the budget here WITHOUT incrementing (clif.c:11514) — unequip is free but blocked
            // once the second's allowance is already gone. 0x12/0x1C (wield/use) are ungated in RTK too.
            case ClientOp.Unequip:                    if (ActionBudgetLeft()) HandleUnequip(dec); break;   // remove a worn item back to the bag
            case ClientOp.DropGold:                    HandleDropGold(dec); break;  // drop a gold amount
            // 0x30 = Shift+C "rearrange a pane" (RTK case 0x30 -> clif_parsechangepos/clif_parsechangespell):
            // dec[0] picks the pane (0=bag, 1=spellbook), dec[1]/dec[2] = the two 1-based slots to swap.
            // Live-confirmed shape (user capture 2026-08-17): `30 01 01 02 00`. See HandleChangePos.
            case ClientOp.ChangePos:                    HandleChangePos(dec); break;
            // 0x29 / 0x2A = the native hand-item / hand-gold gestures ('h'/'H' with a bag item, and the gold
            // gesture), aimed at the tile you're facing (RTK clif_handitem/clif_handgold). See Session.Social.
            case ClientOp.HandItem:                    HandleHandItem(dec); break;
            case ClientOp.HandGold:                    HandleHandGold(dec); break;
            // 0x20 = the 'o' / Open key (RTK clif_parse case 0x20 "Clicked 'O'" -> clif_cancelafk + clif_open_sub
            // -> onOpen script). A deliberate action (RTK's handler clears AFK, so NOT a heartbeat): in NexusTK it
            // toggles the faced door object's open/closed graphic in place. See HandleOpen (swaps the object tile
            // via the 0x06 cell-patch and broadcasts it to the map).
            case ClientOp.Open:                    HandleOpen(dec); break;
            // 0x0F = cast a learned spell (RTK clif_parsemagic): body[0]=book slot+1, then per spell type
            // 1 -> typed answer string, type 2 -> target entity id (u32BE), type 5 -> nothing. See HandleCast.
            case ClientOp.Cast:
                if (ActionAllowed(ClientOp.Cast)) HandleCast(dec);
                else if (CastQueueEnabled) QueueCast(dec);
                break;
            // 0x66 = right-click "examine item" on a bag slot. Answered with a 0x66 reply that the client's
            // handler 0x4511b0 renders as the item-detail popup (stats + wear requirements). Both directions
            // are RE'd from the 4.95 binary — see HandleItemInfoRequest / SendItemInfo for the wire formats
            // and the builder/handler addresses. Leaving it unanswered is what made the client retry ~6×.
            // body[0] splits the two: 0 = examine (`00 cursorY 00 01 01 SLOT 01 00 00 00`), 1 = "send me
            // the town/nation table" (the fixed `01 00 01 01 00 01 01 00` the client emits from 0x449ed0
            // when its own table is empty, right before the 0x18 user-list request). See Session.UserList.
            case ClientOp.ItemOrTownInfo:
                if (dec.Length > 0 && dec[0] == 1) HandleTownListRequest(dec);
                else HandleItemInfoRequest(dec);
                break;
            // 0x09 = the ';' Look key (RTK clif_parselookat_2). No coordinates in the body — it always
            // inspects the tile immediately in front of us (facing direction). See HandleLookAt.
            case ClientOp.LookAt:                    HandleLookAt(dec); break;
            // 0x19 = whisper (Shift+' , type a name, Enter, type the message, Enter). LIVE-confirmed
            // 2026-07-26: body = dstlen(u8) dst_name[dstlen] msglen(u8) msg[msglen] 00 — exactly RTK
            // clif_parsewisp's wire layout (clif.c:7644). See HandleWhisperPacket.
            case ClientOp.Whisper:                    HandleWhisperPacket(dec); break;
            // 0x3B = the 'b' key (Board). LIVE-confirmed 2026-07-26: body `01 00` = sub-command 1
            // ("Show Board"). Matches RTK's clif_parse dispatch exactly (clif.c:11613: `case 0x3B:
            // clif_handle_boards(sd);`). See HandleBoard.
            case ClientOp.Board:                    HandleBoard(dec); break;
            // 0x41 = the mail-arrow widget's PARCEL-bag click (empty body). RE'd 2026-07-28: the widget's
            // parcel branch (0x469760) stages the single byte 0x41 and sends it. RTK maps it to
            // clif_parseparcel (clif.c:15508) = a minitext pointing at the messenger — and that's exactly what
            // a parcel needs (collect it from a MessengerNpc, see MessengerAbility), so we mirror it verbatim.
            case ClientOp.Parcel:                    SendMiniText("You should go see your kingdom's messenger to collect this parcel."); break;
            // 0x2E = RTK's party-invite opcode (clif_addgroup: body = nameLen(u8) name[nameLen], same shape
            // as 0x19 whisper above) — the "Group" button on another player's profile window, and since the
            // "@party" chat fallback was removed, the ONLY way into a group. Bad/garbage bytes just fail the
            // name lookup, so nothing risky is ever sent back.
            case ClientOp.PartyInvite:                    HandlePartyInvite(dec); break;
            // 0x4A = RTK's exchange sub-protocol (clif_parse_exchange) — every message the client's real
            // trade WINDOW sends: 0 initiate (the profile window's "Exchange" button), 1/2 offer a bag slot,
            // 3 offer gold, 4 cancel, 5 confirm. The window itself is opcode 0x42 going the other way. Full
            // wire format + the client RE behind it: Session.Exchange.cs, docs §11l.
            case ClientOp.Exchange:                    HandleExchangeRequest(dec); break;
            // 0x3F = world-map click / ESC reply (§11m). LIVE-CONFIRMED 2026-07-26: body =
            // mapId(u32BE) x(u16BE) y(u16BE) 00 -- RTK's case 0x3F map-change. See HandleWorldMapSelect.
            case ClientOp.WorldMapSelect:                    HandleWorldMapSelect(dec); break;
            // 0x18 = "send me the user list" (empty body; client 0x490e00 stages the lone byte 0x18 and
            // sends length 1). Reached from modifier+'W' (0x48e5cf) and menu-action 3 (table 0x430914),
            // both via 0x48e3d0. RTK dispatches the same opcode to clif_user_list. See Session.UserList.
            case ClientOp.UserList:                    HandleUserListRequest(); break;
            // 0x39 in = the answer from any 0x2f merchant window (RTK case 0x39 -> clif_handle_menuinput),
            // tagged by the byte the server put at body[1]. Unrelated to 0x39 OUT, which is the self-profile.
            case ClientOp.ShopReply:                    HandleShopReply(dec); break;
            // Dump the BODY, not just the opcode. An unhandled opcode is nearly always one we're mid-way
            // through decoding, and its bytes are the whole point — without them every probe needs Frida
            // running on the client just to read what the client already told us.
            default:
                Log.Info($"   ?? no handler for opcode 0x{pkt.Opcode:x2} {dec.Length}B: " +
                         Convert.ToHexString(dec).ToLowerInvariant());
                break;
        }

        // AFTER the switch, deliberately. Draining first let queued casts claim a fresh window's budget
        // before an attack packet in that same window ever bumped it — and since 0x13 pays into the budget
        // without being gated by it, casting and swinging stopped competing at all. They are supposed to:
        // RTK spends ONE shared counter on both (`sd->time`), so a held attack key starves casting outright.
        // Draining here means this packet's own action is charged first and the queue only gets what's left.
        DrainQueuedCasts();   // no-op unless a held cast key left casts waiting on a window roll
    }

    // ---- game server ----
    // NOTE: account creation (name-check 0x02, appearance 0x04) lives in the separate LoginServer process.
    // The game server handles world-entry arrival (0x10) and RE-LOGIN (0x03, below); shared
    // appearance/placement helpers moved to Shared/CharacterFactory so both processes decode the same way.

    // Re-login on the game port. When the client exits to the select screen (Alt+X) it does NOT drop the
    // game connection — it re-sends the login packet (0x03) on it and waits for a handoff redirect, exactly
    // as it would on the login channel. The old single-process server answered 0x03 on every port; after
    // the split the game server must still answer it or the client hangs. Re-authenticate (shared rule)
    // and hand the client back to THIS game server with a fresh single-use token.
    private void HandleReLogin(byte[] dec)
    {
        int ulen = dec[0];
        var user = Encoding.ASCII.GetString(dec, 1, ulen);
        var pass = LoginAuth.ReadPassword(dec, 1 + ulen);
        // Same per-IP failed-attempt budget the login channel enforces — the re-login path accepts the very
        // same credentials, so leaving it ungated would just move the brute-force target to port 2005.
        var ip = _peerIp;
        if (LoginThrottle.IsBlocked(ip))
        {
            Log.Info($"   -> RE-LOGIN BLOCKED (failed-attempt budget exhausted) for user='{user}'");
            SendMessage(LoginThrottle.BlockedMessage);
            return;
        }

        if (Moderation.IsIpBanned(ip.ToString(), out var ipReason))
        {
            Log.Info($"   -> RE-LOGIN REJECTED (ip banned) for user='{user}'");
            SendMessage(string.IsNullOrWhiteSpace(ipReason)
                ? "This address is banned from the server."
                : $"This address is banned from the server: {ipReason}");
            return;
        }

        var auth = LoginAuth.Authenticate(user, pass);
        if (auth != LoginResult.Ok)
        {
            // See the matching note in LoginSession: a ban is not a failed credential, so it must not eat
            // the per-IP failed-attempt budget.
            if (auth == LoginResult.Banned)
            {
                Log.Info($"   -> RE-LOGIN REJECTED (banned) for user='{user}'");
                SendMessage(LoginAuth.BanMessageFor(user));
                return;
            }
            int left = LoginThrottle.RecordFailure(ip);
            Log.Info($"   -> RE-LOGIN REJECTED ({auth}) for user='{user}' " +
                (LoginThrottle.IsExempt(ip) ? "(throttle-exempt address)" : $"({left} attempt(s) left)"));
            SendMessage(LoginAuth.MessageFor(auth));
            return;   // no handoff; the client stays on the login screen
        }
        LoginThrottle.RecordSuccess(ip);

        var nonce = HandoffTokens.Mint(user, _remoteIp);
        var host = GameHostOctets;
        int gport = _port;   // redirect back to this same game port (2005 V495 / 2006 V533)
        Send(LoginRedirect.Build(host, gport, user, nonce));
        Log.Info($"   -> RE-LOGIN ok for '{user}' — handoff back to {host[0]}.{host[1]}.{host[2]}.{host[3]}:{gport} (token minted)");
    }

    // Exit to the select screen (Alt+X). This is the RTK `case 0x0B -> clif_closeit` path (rtk/src/map/
    // clif.c:11397, :655) and the whole reason character CREATION used to hang: the client leaves the world
    // and drives the select screen over whatever socket the reply names, and the game process has no
    // name-check (0x02) or create (0x04) handler to drive it with. RTK's answer is not to build one — its
    // map dispatch has no 0x02/0x03/0x04 case at all — it is to send the SAME 0x03 redirect struct the login
    // handoff uses, pointed BACK at the login server (its `log_ip`/`log_port`, i.e. map.conf loginip/
    // loginport). The client reconnects there and creation/login run where they already live.
    //
    // Live 4.95 evidence this is the right opcode: a session that exited to select sent exactly
    // `aa 00 03 0b <inc> <..>` (body `00`) and then NOTHING until the player typed a different character's
    // credentials six seconds later, which arrived as a 0x03 on the still-open GAME socket. The client was
    // never told to go back to login, so it didn't — the doc's old claim that it "never reconnects to the
    // login server" described our silence, not the client.
    //
    // Confirmed in the binary too (Protocol.md §4.2): sender 0x44ed50, whose single caller 0x44a6e2 sits in
    // the WORLD OBJECT'S TEARDOWN — every cached resource is released first, 0x0B goes out, and the
    // replacement screen is constructed in the next instructions. That ordering is what makes replying
    // safe: 0x03 means "download/URL" to the world dispatcher (0x44f0e0) and "redirect" to the login
    // screen, and by the time this reply crosses the wire the world dispatcher is already gone.
    //
    // Deliberately NO world teardown here (RTK's 0x0B doesn't call clif_handle_disconnect either): the
    // client drops the game socket once it acts on the redirect, and the read loop's finally block does the
    // real leave/persist. If the client does NOT act on it, the player is simply still in the world and
    // HandleReLogin above still answers the 0x03 — the old behaviour, unchanged, as a fallback.
    private void HandleExitToSelect()
    {
        var host = LoginHostOctets;
        int lport = LoginRedirectPort;
        // No handoff token: this redirect points at the LOGIN server, and the next real handoff mints its
        // own nonce when the player logs back in. The 5 bytes are padding that keeps the reply the exact
        // width of the proven-working handoff — the client parses it as a FIXED-SIZE redirect struct and a
        // different length breaks the parse (see Protocol.md §4.1). The client DOES still announce on the
        // login connection with a 0x10 carrying these zero bytes — a redirect is a redirect to it — and
        // the login server logs and ignores that (observed live 2026-08-24; this comment used to claim
        // login "never sees a 0x10", which the first logout through this path disproved).
        Send(LoginRedirect.Build(host, lport, _user, new byte[LoginRedirect.TailBytes]));
        Log.Info($"   -> EXIT-TO-SELECT for '{_user}' — redirect to login {host[0]}.{host[1]}.{host[2]}.{host[3]}:{lport}");
    }

    // Game host the re-login handoff redirects to (must match how the client reached this game server).
    // Defaults to loopback; set P1998_GAME_HOST for a split-box deployment (same var the login server uses).
    // Parsed ONCE now: this used to re-read the environment and re-parse the string on every redirect, and
    // the environment does not change under a running process. Read-only by convention — every consumer
    // copies octets out of it (LoginRedirect.Build reverses them into a fresh frame).
    private static readonly byte[] GameHostOctets = HostAddress.Parse(ServerConfig.Current.GameHost);

    // Login host the exit-to-select bounce redirects to. Falls back to P1998_GAME_HOST because the common
    // deployment runs both processes on one box (and behind HAProxy both front doors share the ONE public
    // address — see the infra split), so a single var usually covers both. P1998_LOGIN_HOST overrides for a
    // genuinely split deployment. Either way this must be the address the CLIENT can reach, not the bind.
    // ServerConfig.LoginHost holds that fallback, so the rule is stated once rather than per call site.
    private static readonly byte[] LoginHostOctets = HostAddress.Parse(ServerConfig.Current.LoginHost);

    // Which login port the exit-to-select bounce names. The two channels are PAIRED by client version (see
    // Shared/ChannelPorts), so the default is derived from the port this session arrived on rather than
    // hardcoded — bouncing a 5.33 player onto the 4.95 login would round-trip them back to the 4.95 game
    // port. P1998_LOGIN_PORT overrides for a custom layout; unset it resolves to null and the pairing wins.
    private int LoginRedirectPort => ServerConfig.Current.LoginPort ?? ChannelPorts.LoginFor(_port);

    /// <summary>HandleArrival's register-and-kick block, split out so a test can run an arrival's slot claim
    /// without a handoff token. Takes the account's online slot and kicks whatever session the registry hands
    /// back, BEFORE the caller loads the row.
    ///
    /// <para><b>Live:</b> another session is still connected for this account. As it always was.</para>
    ///
    /// <para><b>Departed (#168):</b> the account's last session tore down less than an autosave interval ago.
    /// Its teardown wrote the row, but a write decided before that teardown can still land after it: a group
    /// share whose killer was waiting on the departed session's monitor, or a mob swing the tick queued
    /// before it. A login that loaded in between would have that write land under it, and its own next write
    /// would then erase the share. The kick is the fence: it enters the departed session's monitor, so it
    /// waits out whatever still holds it (for the two late writers above, the rest of one payout or one death,
    /// each ending in one database write), writes the row, and latches <c>_replaced</c>, so anything later is
    /// refused. The caller's load then sees every write that won the race, and none can land after it.</para>
    ///
    /// <para>A departed kick that THROWS is logged and the arrival carries on with the row as it stands. The
    /// only route to a throw is a character the serializer rejects, whose teardown save already threw and was
    /// logged as lost; letting it escape would refuse this login, which the same logout without the fence
    /// lets in. A live kick's throw still propagates, as it always has.</para></summary>
    internal void ClaimAccountSlot(string user)
    {
        _world.Online.RegisterArrival(CharacterStore.Key(user), this, out var oldSession, out bool departed);
        if (oldSession is null) return;
        ArrivalFenceProbeForTest?.Invoke(oldSession);   // null except under test; see the field
        if (!departed)
        {
            Log.Info($"   -> ARRIVAL: '{user}' already online — kicking previous session");
            oldSession.KickForReplacement();
            return;
        }
        Log.Info($"   -> ARRIVAL: '{user}' left moments ago — fencing that session's last write before the load");
        try { oldSession.KickForReplacement(); }
        catch (Exception e)
        {
            Log.Error($"   -> ARRIVAL: the departed session's final write for '{user}' threw — its save is LOST; " +
                      "loading the row as it stands", e);
        }
    }

    /// <summary>Test seam (#168): called with the session <see cref="ClaimAccountSlot"/> is about to kick,
    /// after the registry handed it back and before the kick enters its monitor. A fence fact uses it to know
    /// the arrival has reached the fence without timing anything. Null outside the test host.</summary>
    internal static Action<Session>? ArrivalFenceProbeForTest;

    private void HandleArrival(TkPacket pkt)
    {
        // plaintext body: <klen> "NexonInc." <ulen> "<user>" <token>
        var body = pkt.Body;
        byte[] token = Array.Empty<byte>();
        try
        {
            int klen = body[0];
            int ulen = body[1 + klen];
            _user = Encoding.ASCII.GetString(body, 2 + klen, ulen);
            int tokenStart = 2 + klen + ulen;
            // What's left of the handoff nonce. NOT always 5 bytes: the client copies <ulen><user><nonce>
            // into one fixed 13-byte NUL-terminated field, so a longer username eats into the nonce (see
            // Shared/HandoffTokens.SurvivingBytes). Take whatever is here and let Consume decide how much
            // of it must match.
            if (tokenStart < body.Length) token = body[tokenStart..];
        }
        catch (Exception e)
        {
            // A body too short for its own length prefixes: a broken client or a probe. Treated as an empty
            // username and token, which the handoff check below refuses like any other bad credential.
            Log.Warn($"{_remote} arrival body could not be parsed ({body.Length}B: {Convert.ToHexString(body).ToLowerInvariant()})", e);
        }

        // Validate the single-use handoff token the login server minted for this username (see
        // Shared/HandoffTokens). This is what stops a client from connecting straight to the game port and
        // claiming ANY username — identity now rests on a login-verified secret, not the client's claim.
        // Safety valve: P1998_ENFORCE_HANDOFF=0 downgrades a failure to a warning (fallback only if a
        // deployment hits a token problem); the default is to enforce.
        if (!HandoffTokens.Consume(token, _user, _remoteIp))
        {
            // Resolved at startup, not per arrival — this was one of the four knobs the issue names as
            // re-read from the environment on every call.
            bool enforce = ServerConfig.Current.EnforceHandoff;
            if (enforce)
            {
                Log.Info($"   -> ARRIVAL REJECTED: invalid/expired handoff token for user='{_user}' from {_remoteIp} " +
                         $"(token {Log.Hex(token)}, {HandoffTokens.SurvivingBytes(_user)} byte(s) expected) — closing connection");
                CloseConnection("arrival rejected (bad handoff token)");
                return;
            }
            Log.Info($"   -> ARRIVAL WARN: invalid handoff token for user='{_user}' — allowed (P1998_ENFORCE_HANDOFF=0)");
        }

        // Ban check AGAIN, here at the actual door to the world. The login channel already refused a banned
        // account, but a handoff token minted moments BEFORE the ban was placed is still valid until it
        // expires, and P1998_ENFORCE_HANDOFF=0 skips the token check entirely. This is the authoritative
        // gate: nothing enters the world without passing it.
        if (Moderation.IsBanned(_user, out _, out _))
        {
            Log.Info($"   -> ARRIVAL REJECTED: account '{_user}' is banned — closing connection");
            SendMessage(LoginAuth.BanMessageFor(_user));
            CloseConnection("arrival rejected (account banned)", drain: true);
            return;
        }

        // Duplicate-login guard: if this account is already connected (stale client + a fresh reconnect
        // after a network blip, or someone else with the password), force the OLD session out and flush it
        // FIRST — otherwise its eventual disconnect save could clobber THIS session with stale data, since
        // CharacterStore.Save is a blind last-write-wins upsert. Must run BEFORE _store.Load below so the
        // kicked session's flush (if any) is visible to our own load. Since #168 the same kick also fences a
        // session for this account that tore down moments ago; see ClaimAccountSlot.
        ClaimAccountSlot(_user);

        // Load the persisted character (created on the login channel, or saved at last logout). There is NO
        // fallback spawn any more: world entry never invents a character. A missing record here means the
        // login server minted a token for a name that has no character (it checks first, so this is either a
        // race with an admin deleting the record, or a hand-rolled client with P1998_ENFORCE_HANDOFF=0).
        // Creating one on the spot is what let anyone materialize a character by typing a new name at login.
        var load = _store.Load(_user);
        if (load.Status == CharacterLoadStatus.NotFound)
        {
            Log.Info($"   -> ARRIVAL REJECTED: no character record for user='{_user}' — closing connection");
            _world.Online.Unregister(CharacterStore.Key(_user), this);   // give back the online slot we just claimed
            CloseConnection("arrival rejected (no character record)");
            return;
        }
        if (load.Status == CharacterLoadStatus.Unreadable)
        {
            SendMessage("Your character record could not be loaded. Please contact an administrator.");
            _world.Online.Unregister(CharacterStore.Key(_user), this);
            CloseConnection("arrival rejected (unreadable character record)", drain: true);
            return;
        }
        if (load.Status == CharacterLoadStatus.StorageError)
        {
            SendMessage("Character storage is temporarily unavailable. Please try again.");
            _world.Online.Unregister(CharacterStore.Key(_user), this);
            CloseConnection("arrival rejected (character storage unavailable)", drain: true);
            return;
        }
        _char = load.Character!;
        // Keep the CASING the character was created with — _user is whatever the player typed at the login
        // prompt, and logins are case-insensitive, so assigning it here would rewrite "Snuggle" to "snuggle"
        // (and then broadcast that to every peer) on the first lowercase login.
        if (string.IsNullOrEmpty(_char.Name)) _char.Name = _user;
        CharacterFactory.ApplyAppearance(_char);   // re-derive appearance (incl. nation/totem) for records saved before this existed
        // Totem is picked at creation and can be changed, but NEVER "unset" — the valid crests are 0..3
        // (JuJak/Baekho/HyunMoo/ChungRyong). Force any stored out-of-range value into range at login so a
        // bad record self-heals. This matters beyond tidiness on 5.33: that client clamps the 0x08 totem
        // field to 0..3 and stores the clamped value, then reports a phantom "totem changed" on every
        // stats packet if what we keep sending differs — which rebuilds the sidebar and wipes any open
        // pane (the equip/eat/cast/kill/idle pane-wipe, live-traced 2026-08-21). A character sitting at
        // the legacy 4 ("none") default is exactly that case; clamping here removes the divergence at the
        // source. (TotemWire is the belt-and-suspenders at the wire boundary; SetTotem clamps the setter.)
        if (_char.Totem > 3) _char.Totem = 3;
        RestoreTimedEffects();                     // buffs/curses/stances/morph/stealth that were still running at logout
        LoadModerationState();                     // mute deadline onto the session, so the chat path needs no DB read
        _char.Ac = (sbyte)Math.Clamp(100 - _char.Level, -128, 127);   // naked base AC = 100-level; recompute on load so records saved under the old decrement/gate logic self-correct
        // Fast-move (movement authority) is restored from the persisted preference (SettingFlags bit 9). The
        // client boots its runtime flag [state+0x451] OFF, but the entry-burst stats packet (SendStats body[46],
        // sent below in HandleArrival) copies THIS value straight into that flag before the first step — that
        // packet IS the "login re-engage mechanism" that was thought missing. So server and client agree from
        // the first walk: bit 9 set -> _fastMove ON -> body[46]=1 -> client self-paces AND we send the per-step
        // no-scroll 0x04 ack. (The earlier "always boot OFF" was a workaround for not knowing the stats packet
        // drove the flag; leaving body[46]=0 had been clobbering fast-move OFF on every stats refresh — see
        // SendStats / docs/4.x/Fast-Move.md. RE'd 2026-08-19.)
        _fastMove = _char.HasSetting(9);
        _enteredWorld = true;
        // Assign a UNIQUE world entity id (the old default was 1 for everyone, which made every player
        // collide on the shared-world broadcast key). This id binds the client's camera (0x05/SendId) and
        // is how peers address this player's move/speech/despawn packets. It is a runtime handle, not a
        // persistent key, so we overwrite whatever was loaded and never save it back meaningfully.
        _char.Id = _world.AllocatePlayerId();
        Log.Info($"   -> ARRIVAL user='{_user}' — loaded character '{_char.Name}' at map {_char.Map} ({_char.X},{_char.Y}) (entity id {_char.Id})");

        // *** THE MISSING TRIGGER (found by reversing NexusTK.exe) ***
        // After 0x10 the client is on the loading screen; its game-WORLD object doesn't exist yet,
        // so the world dispatcher (handles opcodes 0x03-0x68) never runs and every world packet is
        // dropped. Handler 0x444de0 shows the client only builds that world object when it receives
        // opcode 0x02 whose first payload byte is 0x00. The 6.x/7.x reference servers never send this,
        // which is why every prior attempt sat silent. Send it FIRST.
        SendMap(ServerOp.Message, _gameInc++, new byte[] { 0x00 }, "ENTER-WORLD (0x02.00)");

        // Now the world object exists. Replicate the PROVEN 6.x entry order (Replay6x): the map
        // alone loads (confirmed by Frida: CreateFileW("Maps\TK32.map") ok) but the client stays
        // black and won't move because it was never told its OWN entity id. 0x05 supplies that.
        //   0x1E ack, 0x20 time  -> handshake acks (harmless, part of the working sequence)
        //   0x05 = YOUR entity id (binds camera/input to the self player)  <-- the missing piece
        //   0x15 = enter-map (loads Maps\TK<mapId>.map), 0x04 = coords, 0x33 = our appearance
        SendMap(ServerOp.Ack, _gameInc++, new byte[] { 0x06, 0x00, 0x00 }, "ack(0x1E)");
        { var (h, y) = _world.Clock.Time; SendTime(h, y); }
        SendId();
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, MapTitle(_char.Map), 232, _gameInc++);
        Log.Info("   -> mapinfo(0x15)");
        SendXy();
        SendSelfLook();
        PrimeViewport("login");   // 0x06 fill the window now — don't wait on the client's own 0x05
        SendStats();
        ArmEntryMusic();           // 0x19 music: ARMED here, sent on the client's first packet — see Handle()
        SendWeather(_world.Weather.Get(_char.Map));   // 0x1F: whatever this map's weather already is
        SendSound(412, _char.Id);  // "successfully logging in" sfx, confirmed live 2026-07-27

        Log.Info("   == entry sent: 0x02 trigger + 0x1E/0x20 acks + 0x05 id + 0x15 map + 0x04 xy + 0x33 self + 0x08 stats + 412 login sfx (music armed) ==");

        // Join the shared world: register on this map, draw everyone/everything already here for us, and
        // let EnterMap broadcast US to them. From now on peers see our moves/speech and we see theirs.
        var (peers, mobs) = _world.EnterMap(this, _char.Map);
        SyncPeers(peers);                          // existing players in view -> draw on our client (0x33, viewport-gated + tracked)
        SyncMobs(mobs);                            // shared mobs in view -> draw on our client (0x07, streamed)
        SyncGroundItems(_world.ItemsOn(_char.Map));   // floor items in view -> draw (0x07, viewport-gated)
        RefreshInventory();                       // fill the bag + equipment windows (0x0F / 0x37)
        RefreshSpells();                          // fill the spell/skill book (0x17) with learned spells
        // No login-time "you have mail" line: the HUD already says it. Unread mail raises the 0x08 mail-arrow
        // flag (SendStats, flags2 bit 0x10) and a waiting parcel lights the bottom-left bag icon — both are
        // standing indicators, so a minitext on top of them is a duplicate the player didn't ask for.
        Log.Info($"   == world join: map {_char.Map} has {peers.Length} other player(s), {mobs.Length} mob(s) ==");
    }

    // ---- terrain streaming (opcode 0x06) — BOTH clients ----
    // The client asks for the tiles in its viewport with a view-rect request (0x05), and we reply with an
    // 0x06 cell block. Request layout is identical on 4.95 and 5.33 (confirmed from the 5.33 binary handler
    // sub_469060, the Mithia 7.x reference clif_parsemap/clif_sendmapdata, AND a live 4.95 capture):
    //   request body : x0(BE u16) y0(BE u16) w(u8) h(u8) [00 checksum(BE u16) 00 on 4.95]
    // The REPLY differs in cell width, because the two clients pack passability differently:
    //   5.33 : x0 y0 w h | { tile(BE) pass(BE) obj(BE) } * w*h        -- 3 shorts, pass in its own short
    //   4.95 : x0 y0 w h | { ground(BE) object(BE) }    * w*h        -- 2 shorts, pass in ground's top 2 bits
    // SendObjRow (the door cell patch) emits the SAME per-version shape — it has to, because both go to
    // this one opcode and each client's handler reads a fixed cell width with no length check. 4.95's is
    // recv handler 0x44fb90 (4 bytes/cell), 5.33's is sub_469060 (6 bytes/cell); both write each cell into
    // the client's LIVE map array and then redraw the patched rect, so the write is not viewport-gated.
    // No leading flag byte on either: the 5.33 handler reads x0 immediately
    // after op+inc (a spurious 0x00 shifts every field by one, w reads as 0, and you get a black void).
    // Mithia 7.x's clif_sendmapdata DOES emit a leading 0 here; both of these clients differ.
    private void HandleMapRequest(byte[] dec)
    {
        if (dec.Length < 6) { Log.Info($"   ?? map-req too short ({dec.Length}B)"); return; }
        // The 4.95 client fires two (0,0) 12x12 requests at connect, BEFORE 0x15 enter-map — at that point
        // _char has no map or dims, so MapData.For would build a 0-cell map and we'd answer with an empty
        // rect. Ignore anything that arrives before the world join; the client re-asks once it's in.
        if (!_enteredWorld || _char.MapXs == 0 || _char.MapYs == 0) return;
        _stepsSinceMapReq = 0;   // the client is keeping itself fed; hold the push off (see StreamViewport)
        SendMapRect((dec[0] << 8) | dec[1], (dec[2] << 8) | dec[3], dec[4], dec[5], "req");
    }

    /// <summary>Emit one 0x06 cell block for a rectangle, clamped to the map. Shared by the client's own
    /// 0x05 request and by the walk-driven viewport stream (<see cref="StreamViewport"/>).</summary>
    private void SendMapRect(int x0, int y0, int reqW, int reqH, string why)
    {
        var map = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (map is null) { Log.Info($"   ?? map-req for map {_char.Map}: no server-side tile data"); return; }

        // Clamp the rect to the map so the header w/h EXACTLY match the emitted cell count — the client
        // reads w*h cells sequentially, so a mismatch desyncs its stream.
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        int w = Math.Clamp(reqW, 0, Math.Max(0, map.Xs - x0));
        int h = Math.Clamp(reqH, 0, Math.Max(0, map.Ys - y0));
        if (w <= 0 || h <= 0) return;

        // NO leading flag byte: the 5.33 client (handler sub_469060) reads x0 immediately after op+inc
        // — calibrated via the 0x15 handler and confirmed by the client-side Frida probe (a spurious
        // leading 0x00 shifted every field by one byte, making w read as 0 -> zero cells -> black void).
        // (Mithia 7.x's clif_sendmapdata DOES emit a leading 0 here; 5.33 differs.)
        var b = new List<byte>();
        b.AddRange(PacketWriter.U16BEBytes((ushort)x0));
        b.AddRange(PacketWriter.U16BEBytes((ushort)y0));
        b.Add((byte)w);
        b.Add((byte)h);

        int total = w * h;
        bool solid = MapDiag.StartsWith("solid:");
        int solidN = solid && int.TryParse(MapDiag.AsSpan(6), out var sn) ? sn : 0;
        // passtest:N -> floor 651 everywhere (all visible), but stamp pass=N onto a vertical wall-line
        // every 5 tiles (mx % 5 == 2). Same tile graphic on wall/non-wall cells, so any movement block is
        // purely collision from the `pass` short and any visual change is purely `pass` affecting render.
        // The exact same pattern is enforced by PassAt() in HandleWalk, so seen wall == blocked wall.
        bool passtest = MapDiag.StartsWith("passtest:");
        // ground:N -> fill with the 4.x ground WORD N put through the real translation. Unlike solid:N
        // (which is raw) this exercises the sheet selector, so `ground:49152` is sheet-2 frame 0 and
        // `ground:652` is sheet-1 TileA[651]. This is the one to reach for when checking the tile map.
        bool ground = MapDiag.StartsWith("ground:");
        int groundN = ground && int.TryParse(MapDiag.AsSpan(7), out var gn) ? gn : 0;
        int ci = 0;
        for (int iy = 0; iy < h; iy++)
        for (int ix = 0; ix < w; ix++, ci++)
        {
            int mx = x0 + ix, my = y0 + iy;
            ushort tile, pass, obj;
            if (MapDiag == "sweep")
            {
                tile = (ushort)((long)ci * 28550 / Math.Max(1, total));   // 0..28550 across the rect, unmasked
                pass = 0; obj = 0;
            }
            else if (solid)
            {
                tile = (ushort)solidN; pass = 0; obj = 0;
            }
            else if (ground)
            {
                tile = TileTranslation.Ground((ushort)groundN, _ver); pass = 0; obj = 0;
            }
            else if (passtest)
            {
                // Floor 651 everywhere; put a VISIBLE object marker on the wall columns so the tester can
                // see the walls they're blocked by. Objects render but don't collide (only `pass` does),
                // so the marker is purely visual and the block is purely `pass` — the two stay separable.
                bool wall = (mx % 5 == 2);
                tile = 651;
                obj  = (ushort)(wall ? 1542 : 0);
                pass = PassAt(map, mx, my);   // pass=N on the same wall columns (collision)
            }
            else
            {
                // GroundWord, NOT Tile: the top two bits are a SHEET SELECTOR (>=0xC000 means "second
                // legacy sheet, index v-0xC000"), not just a passability flag, so masking them off before
                // translation silently rewrites 30% of the world into unrelated tiles. TileTranslation
                // needs the whole word. The object id space is shared, so objects pass through.
                // @clip streams pass as "walkable everywhere": the client collides locally against its own
                // copy of this layer, so waiving the server check alone (HandleWalk) would change nothing —
                // the client would refuse the step before a walk packet ever went out. On 4.95 this value
                // is re-packed into the ground word's top bits (MapCell.Write), which are ALSO the sheet-2
                // selector, so blocked terrain (water/cliffs — every blocked cell is sheet-2 there) draws
                // as the wrong tile while clip is on; toggling off re-primes the window and the art heals.
                tile = TileTranslation.Ground(map.GroundWord(mx, my), _ver);
                pass = _gm.NoClip ? (ushort)0 : PassAt(map, mx, my);
                obj  = TileTranslation.Object(map.Obj(mx, my), _ver);
            }
            // One shared writer with the door cell patch — see MapCell for why that matters. On 4.95's real
            // path `tile` already IS the ground word and the re-pack inside is an identity; on the diag
            // paths it is a bare sheet-1 index with pass supplied separately, which needs packing.
            MapCell.Write(b, tile, pass, obj, _ver);
        }

        string mode = MapDiag.Length == 0 ? $"real {TileTranslation.Describe(_ver)}" : $"DIAG={MapDiag}";
        Log.Info($"   -> map-data(0x06) [{why}] rect ({x0},{y0}) req {reqW}x{reqH} -> {w}x{h} cells={total} " +
                 $"[{mode}] {(_ver == ClientVersion.V533 ? "3-short" : "2-short")} cells");
        Send(TkPacket.BuildGame(ServerOp.MapCells, _gameInc++, b.ToArray()));
        NoteStreamed(x0, y0, w, h);
    }

    // ---- walk-driven viewport streaming ----------------------------------------------------------------
    // The client only sends its 0x05 map-data request on MAP ENTRY, not per step. Walk far enough from where
    // you arrived and you run off the end of what was streamed and hit unwritten cells — a black wall. (It
    // shows as a hard edge rather than a gradual fade because the client's map array is the memory-mapped
    // cache file, freshly zero-filled for a map it has never cached.) So the SERVER has to push terrain as
    // the player moves; that is what the walk 0x06's view checksum is for.
    //
    // We track the rectangle already sent for the current map and, after each step, extend it. Only the
    // newly-exposed strip goes out (a step exposes one row or one column, ~21 cells / ~84 bytes) rather than
    // the whole viewport, which would be ~1.3 KB per step per player for no benefit.
    private ushort _streamMap;          // which map _stream* describes (0 = nothing streamed yet)
    private bool _streamValid;
    private int _streamX0, _streamY0, _streamX1, _streamY1;   // inclusive bounds already sent

    // The client asks for 18x16 / 19x17 around itself. Stream a larger window so a walking player can never
    // outrun the coverage. The DRAWN rect is 19x17 (the builder at 0x44c950 extends one tile past the 17x15
    // viewport on every side — see Session.Entity.cs), so measure margin from that, not from 17x15: a 27x25
    // window centred on the player puts its edge 13 tiles out against a visible edge ~9 tiles out, i.e. 4
    // tiles of lookahead. Cost is per-STRIP, not per-window (one step exposes one 25-cell column = ~100 B),
    // so widening the window is nearly free once the initial fill is done.
    private const int StreamW = 27, StreamH = 25;

    // The client normally re-requests on its own about every 5 steps (measured: 0.20 requests per walk,
    // the same with or without the server replying, so it is scroll-driven rather than retrying). But its
    // own request is only 18x16 around itself — barely wider than the 19x17 it draws — so five steps in one
    // direction runs the drawn edge past the requested rect and you see black cells a step or two ahead of
    // you. And it does NOT always re-request: on map 1000 (18x25, exactly viewport-width so its x0 is pinned
    // at 0) a player walked 11 tiles out of the requested rect with no further request at all.
    //
    // So the push now runs on EVERY step (grace 0). The old 8-step grace deferred to the client's own
    // requests, which is where the visible black boxes came from. The duplication that grace was avoiding is
    // one ~100-byte strip per step; NoteStreamed no longer lets a client request shrink the tracked window,
    // so a request costs one margin fill, not a full re-send every time. P1998_V495_PUSHGRACE restores the
    // old deferral, P1998_V495_PUSHMAP=0 disables the push entirely.
    private static readonly bool PushMap = ServerConfig.Current.PushMap;
    private static readonly int PushGraceSteps = ServerConfig.Current.PushGraceSteps;
    private int _stepsSinceMapReq;

    // Deliberately the LAST window, not a running union of everything sent. A union is a bounding box, and a
    // bounding box claims coverage that was never sent: walk a long way north (box grows tall) then east, and
    // the new eastern columns only went out for the CURRENT rows, yet the box would mark them covered for the
    // whole accumulated height — walk back north along that edge and you'd hit black with the tracker
    // insisting it was already streamed. Last-window can't lie. The cost is re-sending a strip when a player
    // walks back over ground they've seen, which is ~84 bytes.
    private void NoteStreamed(int x0, int y0, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int x1 = x0 + w - 1, y1 = y0 + h - 1;
        // A rect strictly INSIDE the tracked one adds no coverage, so keep the larger one — it was itself
        // sent as a single rect, so it stays an honest claim (this is not the union the note above rejects;
        // the tracked rect only ever holds a rectangle we actually sent whole). This matters because the
        // client's own 0x05 (18x16) is smaller than our push window: without it, every client request would
        // shrink the tracker and the next step would re-send the whole margin back out to 27x25.
        if (_streamValid && _streamMap == _char.Map
            && x0 >= _streamX0 && y0 >= _streamY0 && x1 <= _streamX1 && y1 <= _streamY1) return;
        _streamMap = _char.Map; _streamValid = true;
        _streamX0 = x0; _streamY0 = y0; _streamX1 = x1; _streamY1 = y1;
    }

    /// <summary>Reset the coverage tracker — the player changed map, so nothing is streamed yet.</summary>
    private void ResetStreamCoverage() { _streamValid = false; _streamMap = 0; }

    /// <summary>The rect we want covered around the player: the stream window clamped to the map (so a map
    /// smaller than the window is simply sent whole).</summary>
    private (int x0, int y0, int w, int h) StreamWindow()
    {
        int xs = _char.MapXs, ys = _char.MapYs;
        int w = Math.Min(StreamW, xs), h = Math.Min(StreamH, ys);
        return (Math.Clamp(_char.X - w / 2, 0, xs - w), Math.Clamp(_char.Y - h / 2, 0, ys - h), w, h);
    }

    /// <summary>Send the whole stream window NOW, ignoring the grace and the coverage tracker. Call right
    /// after the 0x15/0x04/0x33 entry trio on every map entry (login, warp, refresh).
    ///
    /// Warping is the worst case for terrain and used to be the one path with no push at all: the client's
    /// map array is its memory-mapped cache file, freshly zero-filled for a map it has never visited, and
    /// the only thing that filled it was the client's own 0x05 — which lags the 0x15, covers just 18x16
    /// around the arrival tile, and (map 1000) sometimes never comes. So you landed inside a small island
    /// of real tiles with black in every direction, and the walk-driven push only repaired it a strip at a
    /// time as you walked into it. Priming the full window on arrival is ~2.7 KB once per map entry.</summary>
    private void PrimeViewport(string why)
    {
        if (!PushMap || !_enteredWorld || _char.MapXs == 0 || _char.MapYs == 0) return;
        var (x0, y0, w, h) = StreamWindow();
        _stepsSinceMapReq = 0;   // we just fed the client a full window; any configured grace restarts here
        SendMapRect(x0, y0, w, h, why);
    }

    /// <summary>Push any terrain the player's viewport now covers but we haven't sent. Call after a
    /// committed step.</summary>
    private void StreamViewport()
    {
        if (!_enteredWorld || _char.MapXs == 0 || _char.MapYs == 0) return;
        _stepsSinceMapReq++;
        if (!PushMap || _stepsSinceMapReq < PushGraceSteps) return;   // the client is asking for itself

        var (x0, y0, w, h) = StreamWindow();
        int x1 = x0 + w - 1, y1 = y0 + h - 1;

        // Nothing streamed for this map yet, or the window has jumped clear of the last one (a warp landing
        // inside the same map) -> send the whole thing; strips only make sense against an overlapping rect.
        bool overlaps = _streamValid && _streamMap == _char.Map
                        && x0 <= _streamX1 && x1 >= _streamX0 && y0 <= _streamY1 && y1 >= _streamY0;
        if (!overlaps) { SendMapRect(x0, y0, w, h, "walk-init"); return; }
        if (x0 >= _streamX0 && y0 >= _streamY0 && x1 <= _streamX1 && y1 <= _streamY1) return;   // already sent

        // A step moves the window one tile on one axis, so normally exactly one of these fires. Corners can
        // be sent twice when two do; re-writing a cell the client already has is harmless.
        // NoteStreamed runs inside SendMapRect, so capture the old bounds before the first send.
        int ox0 = _streamX0, oy0 = _streamY0, ox1 = _streamX1, oy1 = _streamY1;
        if (x1 > ox1) SendMapRect(ox1 + 1, y0, x1 - ox1, h, "walk-e");
        if (x0 < ox0) SendMapRect(x0, y0, ox0 - x0, h, "walk-w");
        if (y1 > oy1) SendMapRect(x0, oy1 + 1, w, y1 - oy1, "walk-s");
        if (y0 < oy0) SendMapRect(x0, y0, w, oy0 - y0, "walk-n");
        NoteStreamed(x0, y0, w, h);   // the window as a whole is now covered
    }

    private Character _char = new();
    private byte[] _encTable = Array.Empty<byte>();
    private byte _gameInc = 0;   // per-packet increment for game-channel sends


    // Live creatures the player can fight. Server-authoritative HP; the client only draws them.
    // Populated by the mob commands (@mob/@mobrow/@spawn); entries are removed on death (0x0E).
    private readonly List<Mob> _mobs = new();
    private uint _nextMobId = 5000;      // entity-id pool for spawned creatures (well above the self id)
    // Last direction the player faced (0=N 1=E 2=S 3=W); drives melee, the 'o'/open key, board signs, pet
    // placement and everything else that needs a front tile. A VIEW over Character.Dir rather than a session
    // field of its own, so it rides the character blob the same way X/Y do: every existing `_facing = …`
    // write persists for free, and the loaded value is already in place by the time HandleArrival draws us.
    // (It used to be session-local, which is why every login faced north no matter which way you logged out.)
    private byte _facing { get => (byte)(_char.Dir & 3); set => _char.Dir = (byte)(value & 3); }
    private byte _realm = RealmCenter;   // realm-center camera lock; toggled live by F4 (0x1b sub-cmd 0x07)
    private int _lockOx, _lockOy;        // camera origin frozen when realm-center turned ON (map top-left tile)
    // Fast-move = the client's movement model (RTK clif_parsewalk gates on FLAG_FASTMOVE):
    //   ON  = client-authoritative: the client moves/animates freely and is only corrected on desync.
    //         The server must send the walker NOTHING on a good step (0x26 self-walk is skipped).
    //   OFF = server-authoritative: the client will NOT move until the server assigns the tile, so every
    //         step must be answered with a position/move packet.
    // The client toggles it locally and notifies us via 0x1b sub-cmd 0x09 (it does NOT report its state on
    // connect). The client PERSISTS fast-move across launches; RTK keeps server and client in lockstep by
    // ALSO persisting it server-side (SettingFlags bit 9) and restoring it at login, then flipping both only
    // together via 0x1b/09 (RTK never seeds it over the wire — clif_sendoptions is dead code). So the real
    // value is loaded from the character at world entry (see HandleArrival: `_fastMove = _char.HasSetting(9)`);
    // this field initializer is only the pre-entry placeholder and must match a FRESH character (bit 9 clear
    // = OFF), so a brand-new player and a fresh client agree. The old ON default was the desync bug: it
    // assumed every client booted with fast-move enabled. P1998_V495_FASTMOVE_DEFAULT=1 forces the old
    // assumed-ON placeholder if ever needed.
    private bool _fastMove = FastMoveDefault;


    private static readonly bool FastMoveDefault = ServerConfig.Current.FastMoveDefault;

    // Fast-move engagement model. The per-walk high-bit read (dec[1] & 0x80) HandleWalk originally shipped
    // with is never true on the wire — the client sets that 0x80 only on a LOCAL self-move command, never in
    // the network packet (live RE) — so the branch never engaged and every step took the server-authoritative
    // 0x26 path. The real model (verified live 2026-08-19, corroborated by RTK clif_parsewalk): fast-move is a
    // SESSION flag ([state+0x451] on the client, _fastMove here) toggled in lockstep via 0x1b/09. When ON the
    // client draws the step itself (selfWalkAnim @0x48f2c0) but its walk-active gate only clears on a server
    // ack, so we send a per-step NO-SCROLL 0x04 (HandleWalk clientFast branch); when OFF we send the 0x26.
    // PERSISTENCE (RE'd 2026-08-19, docs/4.x/Fast-Move.md): the client's runtime flag [state+0x451] is
    // driven straight off the 0x08 STATS packet — its handler copies body[46] verbatim into that byte on every
    // update. So the server owns the flag: _fastMove is restored from SettingFlags bit 9 at HandleArrival, the
    // login entry-burst stats packet sets the client flag to match before the first step, and every later stats
    // packet reasserts it. The freeze/desync saga was three bugs, all fixed: (1) SendStats left body[46]=0,
    // which CLOBBERED fast-move OFF on every stats refresh -> now driven from _fastMove; (2) the post-toggle
    // 0x23 re-seed nudged the flag via the options apply path -> removed (HandleSetting 0x09); (3) the per-walk
    // high-bit (dec[1] & 0x80) is never set on the wire -> drive off _fastMove (FastMoveTrustToggle). DEFAULT ON
    // (proven smooth on a horse); P1998_V495_FASTMOVE_TRUST_TOGGLE=0 forces the old always-0x26 behavior.
    private static readonly bool FastMoveTrustToggle = ServerConfig.Current.FastMoveTrustToggle;

    // Viewport-streamed world mobs: the shared-mob ids currently drawn on THIS client. The client's
    // 0x07 spawn silently drops entities outside the camera rect, so a 400-mob map can't be blanket-sent —
    // instead SyncMobs spawns mobs as they enter view and despawns them as they leave, keeping the client to
    // a screenful. Guarded by _viewLock (touched by both this read-loop and the World tick thread).
    //
    // _viewLock CAN participate in a deadlock, and the claim that used to sit here that it could not was
    // about Send() alone (a lock-free channel enqueue). Since #29 it is one of five lock families in the
    // process and it has a rule: session monitors are OUTSIDE it. Take it only through EnterView(), which
    // counts the depth so entering a session monitor underneath it trips an assert — see Session.State.cs
    // and the cycle ReconcilePeer used to close.
    // ONE ENTRY PER DRAWN MOB, and the value is which of the two drawn states it is in: DrawnInside, or
    // DrawnBand for one sitting in the overdraw band (outside the strict 17x15, inside the drawn 19x17).
    // The client MAY have culled a banded entity; if one steps back into the strict rect we re-assert its
    // 0x07 rather than assume it's still there. That is what makes HidePad > ShowPad safe — see SyncMobs.
    // The full semantics, and what this replaced, are on DrawnInside below.
    private readonly Dictionary<uint, byte> _drawnMobs = new();
    // Same story for GROUND ITEMS, and for the same reason: ShowGroundItem draws through the very same
    // viewport-gated 0x07 path (see Session.ShowGroundItem's RE note), so a floor item spawned or replayed
    // for an off-screen tile is silently dropped by the client and — because items never move — would never
    // be re-sent. That is why forage drops (chestnuts) were invisible: the world spawns them across a whole
    // farm-sized box while the player only ever has a screenful of it in view. SyncGroundItems reconciles
    // this set exactly like SyncMobs does for mobs. Guarded by _viewLock alongside the mob store.
    private readonly HashSet<uint> _shownItems = new();
    // Spot Traps / Watchful Eye markers: trap id -> the synthetic floor item drawn on that trap's tile for
    // THIS client only (RTK's item-99 drop plus its addTrapSpotters visibility tag). Kept as real GroundItems,
    // and reconciled by SyncGroundItems alongside the world's own floor items, for two reasons: the reveal
    // reaches 15 tiles but ShowGroundItem's 0x07 is viewport-gated to ~8, so without the sync the far half of
    // what you "sensed" was drawn into the void and — items never moving — never re-sent as you walked to it;
    // and keying by TRAP id is what lets a sprung trap take its own marker with it (World.ClearTrapMarker /
    // RTK removeTrapItem) and a re-cast re-mark the same trap instead of stacking a second sword on the tile.
    // Guarded by _viewLock alongside the sets above.
    private readonly Dictionary<uint, GroundItem> _trapMarkers = new();
    // @showwarps overlay: synthetic floor-item markers on every warp/doorway tile of the CURRENT map, for
    // THIS client only — the same machinery as the trap markers above (merged into SyncGroundItems, so
    // out-of-view markers draw as you walk to them), but toggled by a command rather than a spell and
    // re-stamped per map by EnterMap while the toggle is on. A plain list (not keyed) because markers are
    // only ever stamped and cleared wholesale. Guarded by _viewLock alongside the sets above.
    private readonly List<GroundItem> _warpMarkers = new();
    // And the SAME story for PEER PLAYERS. A peer's 0x33 look draw is viewport-gated by the client with the
    // very same camera rect test as the 0x07 mob spawn, so a peer we're too far from at map-entry — or one who
    // walks toward us from off-screen — has its draw silently dropped and, because nothing re-sends it as we
    // move, stays invisible until a room change re-draws them in view (or a Ctrl+R, if they're in view then).
    // That is the "can't see users I walk up to, but gating in next to them shows them" report. SyncPeers
    // reconciles this store exactly like SyncMobs does for mobs, in the same two states.
    // Guarded by _viewLock alongside the mob store and the item set.
    private readonly Dictionary<uint, byte> _drawnPeers = new();

    /// <summary>THE DRAWN-STATE STORE, and the whole of its semantics. <c>_drawnPeers</c> and
    /// <c>_drawnMobs</c> hold one entry per entity this client has been told to draw; the value is that
    /// entity's state, and ABSENCE is "not drawn". Two states, because that is the state machine the sweep's
    /// hysteresis rule is: <see cref="DrawnInside"/> — drawn, and inside the strict 17x15 rect where the
    /// client accepts a 0x07/0x33; <see cref="DrawnBand"/> — drawn, but loitering in the overdraw band
    /// (outside the strict rect, inside the drawn 19x17), so the client MAY have culled it and a step back
    /// into the strict rect must re-assert the draw.
    ///
    /// <para><b>It replaces a pair of <c>HashSet&lt;uint&gt;</c> per entity kind</b> (<c>_shownMobs</c> /
    /// <c>_edgeMobs</c>, <c>_shownPeers</c> / <c>_edgePeers</c>) and is exactly equivalent to them, term for
    /// term:</para>
    /// <list type="bullet">
    /// <item><c>shown.Contains(id)</c> is "the id is present".</item>
    /// <item><c>edge.Contains(id)</c> is "present with value <c>DrawnBand</c>".</item>
    /// <item><c>shown.Add(id)</c> is "set <c>DrawnInside</c>" (every <c>shown.Add</c> in the base was a first
    /// show, which is inside the strict rect by definition and never in the band).</item>
    /// <item><c>edge.Add(id)</c> is "set <c>DrawnBand</c>", a no-op when it is already <c>DrawnBand</c>.</item>
    /// <item><c>edge.Remove(id)</c> returning true is "the value was <c>DrawnBand</c>; set
    /// <c>DrawnInside</c>"; returning false is "the value was <c>DrawnInside</c>", and writes nothing.</item>
    /// <item><c>shown.Remove(id); edge.Remove(id)</c> is "remove the entry".</item>
    /// </list>
    ///
    /// <para><b>Why the pair collapses at all</b>, which is the invariant the base kept without stating it:
    /// the band set was always a SUBSET of the shown set. Every <c>edge.Add</c> in the base sat on a branch
    /// that had already found the id in <c>shown</c> (the sweeps' last <c>else</c>, and the dropped-show
    /// rollback, which only runs when the membership test it is behind has just confirmed the id is drawn);
    /// every removal from <c>shown</c> — the despawn branch, <see cref="DespawnEntity"/>, the wholesale
    /// clears — removed from the band set in the same breath. So no id could be in the band set and not in
    /// the shown set, and one entry with a state carries both facts.</para>
    ///
    /// <para><b>What it buys</b>: one hash lookup per entity per beat instead of two. The base probed
    /// <c>shown.Contains(id)</c> and then, for the common case of a drawn entity inside the strict rect,
    /// <c>edge.Remove(id)</c>, which almost always returned false. Now the sweep does one
    /// <c>TryGetValue</c>, decides off the value, and writes only on a state transition — which in the
    /// steady state is never.</para>
    ///
    /// <para><b>What that is worth, measured</b> on the viewport profile's 400-viewer / 305-mob fixture, per
    /// viewer per beat, as the median of three interleaved base/head runs on an idle machine. <b>In
    /// Release</b> the peer sweep 4,310 ns -&gt; 3,487 ns and the mob sweep 2,942 ns -&gt; 2,426 ns, the
    /// whole sweep 7,382 ns -&gt; 6,094 ns and <c>ReconcileViews</c> 7,483 ns -&gt; 6,189 ns, which at 400
    /// players is 0.52 ms off a beat. <b>In Debug</b> nothing outside the run-to-run spread, on either
    /// sweep. An in-process A/B of the two representations on one fixture agrees with both halves
    /// (Release -836 ns on peers and -512 ns on mobs; Debug of either sign, within a microsecond), and the
    /// reason is the ordinary one: in Debug nothing is inlined, so one hash probe is a small share of a
    /// per-entity cost that is mostly call overhead, and the second probe the base made was against a band
    /// set that is EMPTY on that fixture — an empty HashSet's Remove returns without hashing anything.
    /// Allocation is unchanged at 0 B per viewer per beat. See briefs/reports/drawn-set-state-opus.md.</para>
    ///
    /// <para>Ground items keep a plain <c>HashSet</c> (<see cref="_shownItems"/>): they have no overdraw band
    /// — see <see cref="SyncGroundItems"/> — so there is no second state for them to be in.</para>
    ///
    /// <para><b>Locking is unchanged</b>: the store is this session's own state, guarded by <c>_viewLock</c>
    /// exactly as the four sets were, read and written only under it.</para></summary>
    private const byte DrawnInside = 0;
    /// <summary>See <see cref="DrawnInside"/>: drawn, and in the overdraw band.</summary>
    private const byte DrawnBand = 1;
    // The companion of the drawn state above that makes a DEFERRED send safe: entity id -> the serial number of
    // the most recent decision taken about that entity. All three sweeps decide for every entity under one
    // acquisition and send after releasing it, so a send can be parked (ShowPlayer -> the subject's Snapshot,
    // which takes that session's monitor; or simply descheduled anywhere on the send pass) while another
    // thread's walk reconcile decides the opposite about the SAME entity. The send pass re-takes _viewLock
    // immediately before each frame and drops a decision that is no longer the latest one for that id —
    // PR #245's finding F1. Touched ONLY when a decision produces a frame, which in the steady state is never,
    // so the sweeps' hot path neither reads nor writes it. An entry lives exactly as long as the id is drawn
    // or has a send in flight: the send pass drops it once the decision leaves the id undrawn, DespawnEntity
    // drops it, and the wholesale clears clear it with the sets. Guarded by _viewLock.
    //
    // ONE MAP FOR EVERY ENTITY KIND, because there is one id space: World hands out player ids from 1, mob
    // ids from 100,000 and ground-item ids from 500,000 (World._nextPlayerId/_nextMobId/_nextItemId), the
    // client addresses all of them through the same entity id on the wire, and DespawnEntity below already
    // removes one id from every set without knowing which kind it was.
    private readonly Dictionary<uint, uint> _sendStamp = new();
    private uint _sendSeq;                        // last serial handed out; read and written under _viewLock only
    private readonly object _viewLock = new();
    // SHOW at the strict 17x15 edge, HIDE at the drawn edge one tile further out.
    //
    // ShowPad must stay 0: the 0x07 spawn is viewport-gated (0x424310 is a rect test against the camera
    // viewport), so a spawn for an off-screen tile is silently dropped and would mark a mob "shown" that
    // the client never created.
    //
    // HidePad is 1 because the client DRAWS one tile past the viewport on every side: the viewport builder
    // 0x44c950 clamps to originX-1 .. originX+ViewW+1 (see Session.Entity.cs), i.e. a 19x17 drawn rect
    // around a 17x15 viewport. Despawning at 17x15 therefore yanks mobs off a tile that is still on screen
    // — the reported "mobs pop out one tile too soon". The dead zone this note used to warn about (we think
    // it's drawn, the client already culled it, we never re-send) is closed by the DrawnBand state: anything that
    // spends time in the band gets a fresh 0x07 when it re-enters the strict rect.
    private const int ShowPad = 0;
    private const int HidePad = 1;

    // Speech is proximity-gated: a player only hears a bubble from someone close enough. Two ranges, both
    // half-extents of a box centered on the speaker (see Session.Chat.HandleChat + World.BroadcastArea):
    //  • Say (type 0, ' / ':')  — box x±9, y±8, RTK's SAMEAREA (the 19×17 draw rect / speech.lua distance 8).
    //  • Shout (type 1, '!')    — box ±16, DOUBLE the say range. speech.lua sets distance=16 for a shout, so
    //    the yellow shout bubble carries about twice as far as normal speech but is NOT map-wide. (The engine's
    //    clif_sendscriptsay hardcodes SAMEMAP for shout, but that whole-map reach doesn't match the live feel;
    //    the Lua distance is the intent we follow.)
    internal const int SayHalfW = 9;
    internal const int SayHalfH = 8;
    internal const int ShoutHalfW = 16;
    internal const int ShoutHalfH = 16;

}
