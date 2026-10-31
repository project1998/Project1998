using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    /// <summary>
    /// Best-effort world-entry burst, extrapolated from the RTK 6.x/7.x reference sequence
    /// (intif.c char-load callback). ROUGH: formats/increments/flags are first-pass guesses —
    /// each packet is logged so we can see exactly where the client stops or reacts.
    /// </summary>
    private void SendWorldEntry()
    {
        Log.Info($"   == WORLD ENTRY burst for '{_char.Name}' (map={_char.Map} @ {_char.X},{_char.Y}) ==");

        SendMap(ServerOp.Ack, 0, new byte[] { 0x06, 0x00 }, "ack(0x1E)");
        { var (h, y) = _world.Clock.Time; SendMap(ServerOp.Time, 3, new byte[] { h, y }, $"time(0x20) hour={h} year={y}"); }
        SendId();
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, MapTitle(_char.Map), 232);
        Log.Info("   -> mapinfo(0x15)");
        SendStats();
        SendSelfLook();
        SendXy();
        SendMap(ServerOp.MapDone, 3, Array.Empty<byte>(), "map-done(0x22)");
        PlayMapMusic(_char.Map);   // 0x19: start this map's background track
        SendWeather(_world.Weather.Get(_char.Map));   // 0x1F: whatever this map's weather already is

        Log.Info("   == burst sent; watching for client packets (walk/request = progress, disconnect = a packet was rejected) ==");
    }

    /// <summary>The name the 0x15 map-info shows in the client's title bar, from map_index.csv.
    ///
    /// Four of the six SendMapInfo call sites passed the literal "Nexus" instead — login, both
    /// realm-center/weather in-place refreshes, and the hard refresh — so every map in the world
    /// announced itself as "Nexus", and map 41 in particular showed "Nexus" rather than "Mythic
    /// Nexus". Only the warp path and the (unused) alternate entry burst ever read the real name.
    /// It reads like a placeholder that outlived its stub: the name has been in map_index.csv all along.</summary>
    private static string MapTitle(ushort mapId) =>
        Content.TryMap(mapId, out var md) && !string.IsNullOrWhiteSpace(md.Name) ? md.Name : "Nexus";

    // 0x05 = the client's OWN entity id, decoded from the working 6.x capture:
    //   05 | entityId(u32BE) | 00 00 00 02 | 00 00 00 00
    private void SendId()
    {
        var d = new List<byte>();
        d.AddRange(PacketWriter.U32BEBytes(_char.Id));   // your entity id
        d.AddRange(PacketWriter.U32BEBytes(2));          // field2 = 2 (per 6.x)
        d.AddRange(PacketWriter.U32BEBytes(0));          // field3 = 0
        SendMap(ServerOp.PlayerId, _gameInc++, d.ToArray(), "id(0x05) — YOUR entity id");
    }

    // 0x08 self-stats -> the always-on HUD. Opcode + full byte layout decoded empirically (2026-07-24):
    // a real 6.x server capture (jeedee/TkServer) proved stats = 0x08; a self-describing gradient packet
    // (@stg, body[i]=i) then pinned every 4.95 field offset by reading the value off the HUD. flags=0x78
    // selects the full-stats form. Multi-byte stat fields are big-endian u32 (verified: HP=0x18191A1B at
    // offset 24, Exp=0x20212223 at 32, etc.). maxHP[5]/maxMP[9] CONFIRMED via @hp (sending 100/1000
    // drops the bar to ~10%). Nation id table (CONFIRMED via @nat, see Character.NationName).
    //   [0]=flags(0x78) [1]=nation [2]=totem [4]=level [5..8]=maxHP u32BE [9..12]=maxMP u32BE
    //   [13]=might [14]=will [17]=grace [24..27]=HP u32BE [28..31]=MP u32BE [32..35]=exp u32BE
    //   [36..39]=coins u32BE
    private void SendStats()
    {
        // Effective = base (_char.*) + worn-gear bonuses + active timed buffs. Clamp current HP/MP to the
        // effective caps so an unequip that lowers max Vita/Mana can't leave the bar reading over 100%.
        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        if (_char.Hp > maxHp) _char.Hp = maxHp;
        if (_char.Mp > maxMp) _char.Mp = maxMp;

        // 5.33 uses the SAME 0x08 field offsets as 4.95 (verified against BOTH consumers: the HUD parser
        // sub_4e2450 reads flags at body[1], HP at body[25], grace at body[18] — which in PAYLOAD terms,
        // where SendMap prepends the opcode so d[k]==body[k+1], is d[0]=flags, d[24]=HP, d[17]=grace,
        // i.e. exactly the 4.95 layout @stg pinned live. An earlier "composite offsets differ by a few
        // bytes" reading was an off-by-one — it shifted every field +1 and produced 1.6-billion HP and
        // grace 0. Do NOT reintroduce a V533-specific layout here. See docs/5.x/Wire-Divergences.md §6.5.
        var d = new byte[58];
        d[0] = 0x78;                        // flags: full-stats form
        d[1] = _char.Nation;
        d[2] = TotemWire();                 // 5.33 clamps totem to 0..3; "None" (4) must go out as 0xFF — see below
        d[4] = _char.Level;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(5), maxHp);             // maxHP  (offset [5] confirmed via @hp bar-fill test) — base + gear
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(9), maxMp);             // maxMP  (offset [9] confirmed) — base + gear
        d[13] = (byte)Math.Clamp(_char.Might + eq.might, 0, 255);
        d[14] = (byte)Math.Clamp(_char.Will  + eq.will,  0, 255);
        d[17] = (byte)Math.Clamp(_char.Grace + eq.grace, 0, 255);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(24), _char.Hp);         // current HP (confirmed)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(28), _char.Mp);         // current MP (confirmed)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(32), _char.Exp);        // experience (confirmed)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), _char.Coins);      // coins      (confirmed)
        if (!_mailFlagsSeeded) { _mailFlags = ComputeMailFlags(); _mailFlagsSeeded = true; }   // one SQLite read at first stats (login)
        d[45] = _mailFlags;                 // bottom-left HUD notify: 0x10=n-mail arrow, 0x01=parcel bag (body[45] confirmed live 2026-07-28)
        // body[46] = the client's RUNTIME fast-move flag. The 0x08 handler (client 0x48fc40, reached from the
        // network dispatcher 0x48eb40) copies this byte VERBATIM into the game-state singleton at [state+0x451]
        // — the exact byte the walk path reads to decide client- vs server-authoritative movement (see
        // docs/4.x/Fast-Move.md, RE 2026-08-19: 0x08 payload[47] == our body[46]; the getbyte helper
        // 0x475c90 is a plain `*ptr`, the store is `mov [state+0x451],al`, so it is a straight copy). This is
        // how fast-move actually persists: every stats packet reasserts the flag, and the login entry-burst
        // stats packet sets it before the first step. Leaving it 0 (the old behavior) FORCED fast-move OFF on
        // every stats refresh — every mob swing / heal / regen silently clobbered a live toggle back to OFF,
        // which is why it never "stuck". _fastMove is the server's authority (persisted in SettingFlags bit 9,
        // restored at HandleArrival); driving the client from it here keeps the two in lockstep.
        // 5.33 DIVERGENCE (RE 2026-08-21, docs/5.x/Wire-Divergences.md): the 5.33 0x08 parser (case
        // 0x4d5fa9 -> sub_4d8160) also reads this trailing byte (its parser offset 0x2f == our body[46])
        // — but there a NONZERO value additionally fires sub_4857c0, a deferred move-commit/view rebuild,
        // whenever its pending-move gate [state+0x6878] is set. Live symptom: every stats refresh (equip,
        // eat, cast, regen "doing nothing") made the client re-request the full map rect (0x05) and redraw
        // the scene, visually wiping whatever pane was open while the panel-manager index still said
        // "open" — so `i` could not reopen the inventory. 4.95 fast-move was never negotiated with 5.33,
        // so send 0 and leave its walk machinery alone.
        d[46] = (byte)(_ver != ClientVersion.V533 && FastMoveTrustToggle && _fastMove ? 1 : 0);
        SendMap(ServerOp.Stats, _gameInc++, d, "stats(0x08)");
    }

    /// <summary>Totem index for the 0x08 HUD, made ROUND-TRIP STABLE for the receiving client.
    ///
    /// This was the 5.33 pane-wipe (live-traced 2026-08-21, docs/5.x/Wire-Divergences.md §6.5). Our totem
    /// table has 4 = "None" (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None). 5.33's 0x08 parser
    /// (sub_4e2450) CLAMPS the totem field to 0..3 before storing it, and fires the totem widget's
    /// change-notify whenever the byte it reads differs from what it stored. Sending 4 meant the client
    /// stored 3 but kept reading 4 — a phantom "totem changed" on EVERY stats packet (equip, eat, cast,
    /// kill, idle regen) — and that notify schedules a full sidebar rebuild that paints over any open pane
    /// (inventory, and the click-profile page while it is up). The clamp treats 0xFF as the stable
    /// "no totem" sentinel (0xFF stores as 0xFF, so a resend matches and no notify fires). 4.95 accepts 4
    /// directly and is left unchanged. Any totem in 0..3 already round-trips on both clients.</summary>
    private byte TotemWire() =>
        _ver == ClientVersion.V533 && _char.Totem > 3 ? (byte)0xFF : _char.Totem;

    // The 0x08 body[45] mail/parcel HUD-notification byte (RTK FLAG_MAIL=0x10 / FLAG_PARCEL=0x01, both=0x11).
    // Confirmed live: the 4.95 client draws the bottom-left arrow (unread n-mail) / bag (unclaimed parcel)
    // straight off this byte. CACHED (_mailFlags): computing it is two SQLite queries (Mail.InboxFor +
    // Parcel.HasAny), and SendStats fires on every mob swing / heal / cast / regen — so it must NOT hit the DB
    // each time. The cache is seeded on the first stats packet (login) and refreshed by RefreshMailFlags() on
    // the events that can change it: this session's own read/delete, another player mailing/parcelling us (their
    // session pokes ours), and a low-frequency backstop in RegenTick for any path we missed.
    private byte _mailFlags;
    private bool _mailFlagsSeeded;

    private byte ComputeMailFlags()
    {
        byte f = 0;
        var inbox = Mail.InboxFor(_char.Name);
        if (inbox.Any(m => !m.IsRead))                       f |= 0x10;  // an unread letter -> arrow
        // Bag icon = a parcel waiting: either a real messenger parcel (Parcel.cs) or a reward-mail's
        // unclaimed attachment (RTK's own reward mail carries its parcel on the letter).
        if (Parcel.HasAny(_char.Name)
            || inbox.Any(m => m.ItemId >= 0 && !m.Claimed))  f |= 0x01;
        return f;
    }

    /// <summary>Recompute the mail/parcel HUD flag (the only place the two SQLite reads happen) and, if it
    /// changed, push a fresh stats packet so the arrow updates now. Callable cross-session — the sender's
    /// session invokes this on the recipient's when it mails/parcels them.</summary>
    internal void RefreshMailFlags()
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        byte f = ComputeMailFlags();
        _mailFlagsSeeded = true;
        if (f == _mailFlags) return;
        _mailFlags = f;
        SendStats();   // the flag changed -> refresh body[45] on the HUD now
    }


    // ---- natural HP/MP regeneration (RTK Player.regen migration) -------------------------------
    // RTK heals a resting player every 25s: Accepted/player.lua `Player.regen` fires on timerTick%50
    // (the timer runs at 0.5s/tick), restoring ceil(maxHP * 0.02 * (1 + healing/100)) vita and
    // ceil(maxMP * 0.02) mana, then pushing a status update. We don't carry RTK's derived `healing`
    // stat, so HP regen scales with Grace and MP regen with Will (so vitals come back "based on your
    // stats", as expected), keeping RTK's 2% base and 25s cadence.
    //
    // THE 25s CLOCK IS THE WORLD'S, NOT THIS PLAYER'S (World._regenClockMs, passed in as regenDue).
    // RTK's timerTick%50 is one global timer: everyone below full heals in the same moment every 25s,
    // taking damage does not restart anything, and two players in a fight always tick together. This
    // server had drifted to a per-player accumulator that was reset at the topped-off check, so the
    // first tick after damage was always exactly 25s later and a player chipped every 20s never
    // regenerated at all. The project owner decided on 2026-09-19 to go back to the original game's global clock,
    // and that is the ONLY player-visible change in this pass: the amount, the caps, the dead check,
    // the topped-off skip and the stats packet are all untouched.
    //
    // Threading: runs on the world-tick thread and writes _char.Hp/Mp, which the session's own
    // read-loop also writes (damage/heal). Both are plain field writes with no lock — consistent with
    // the codebase's lock-free _char posture (see PlayerSnapshot) — so a regen tick landing in the
    // same instant as a hit could at worst drop one small increment. The 25s cadence makes that
    // vanishingly rare and self-correcting on the next tick.
    private long _mailAccum;
    private const int MailBackstopMs = 30_000;   // defensive re-check of the mail/parcel HUD flag (event-driven otherwise)

    /// <summary>One beat of this player's regen step: the mail backstop, the buff expiry pass, the Chung
    /// Ryong fury wear-off, Second Sight's notice (Session.SecondSight.cs, a fifth job decided the same way as the
    /// four below, from <c>_secondSightUntil</c>, <c>_secondSightRuns</c> and the tick-thread-owned scan clock), and — on
    /// a beat where <paramref name="regenDue"/> — the natural regeneration.
    /// <paramref name="regenDue"/> is the world's 25 s clock (<c>World._regenClockMs</c>), so every player
    /// below full regenerates on the same beat.
    /// <para><paramref name="regenDue"/> defaults to false only so the pre-#29 acceptance fact
    /// <c>Tests/SessionActorTests.ConcurrentRegenTickAndBuffApplyLosesNoEntry</c>, which drives this method
    /// for its buff-expiry pass alone, keeps compiling unedited. The tick — the one production caller —
    /// always passes it.</para>
    ///
    /// <para><b>The method decides whether it has anything to do BEFORE taking the monitor.</b> The tick
    /// thread calls this on every online player every beat — 400 monitor entries a beat on the load the
    /// holds run — and on all but a handful of those beats all four jobs are idle. Entering the monitor to
    /// find that out is not work, it is waiting: the tick queues behind whichever session thread is
    /// mid-send or inside a Lua call, which is what PR #254's review measured directly (the whole flush
    /// stalled on this method with one session's monitor held from a second thread,
    /// <c>reviews/PR254-by-fable.md</c> F3). So each of the four jobs is decided from a field the tick
    /// thread can read without the monitor, and the monitor is taken only when one of them says yes:
    /// <list type="bullet">
    /// <item><c>regenDue</c> is the world's clock, passed in — not this session's state at all.</item>
    /// <item><c>_mailAccum</c> is written HERE and nowhere else, on the tick thread, so the accumulate moves
    /// above the monitor with it and it is not shared state.</item>
    /// <item><c>_nextBuffExpiry</c> is the exact minimum expiry, maintained by the four <c>_buffs</c>
    /// writers under the monitor they already hold (see <c>Session.Items.cs</c>).</item>
    /// <item><c>_crRageTier</c> is an <c>int</c> written only under the monitor; a fury is either up or it
    /// is not.</item>
    /// </list>
    /// Everything the method actually DOES still happens under the monitor, including a second, guarded
    /// look at each condition — the pre-check only decides whether to bother acquiring.</para>
    ///
    /// <para><b>The one race, and why it is benign.</b> A buff cast, a fury armed or a mail poke that lands
    /// between the pre-check's read and its return makes the tick skip this player for ONE beat, 333 ms;
    /// nothing is lost, because the next beat reads the new value. A buff still fades on the first beat at
    /// or after its expiry (the hint is exact, not approximate), the fury wear-off still fires on the first
    /// beat at or after <c>_rageUntil</c>, and the 30 s mail backstop is a backstop. This is the same shape
    /// as the sleep/poison pre-check one slice earlier (PR #254). <c>Volatile.Read</c> rather than a plain
    /// read so the JIT cannot hoist either load out of the beat; a 64-bit read is already atomic on every
    /// runtime this server targets.</para>
    ///
    /// <para>The early return happens before <c>EnterState()</c>'s lock-order asserts, which removes no
    /// check that could have fired: the <c>(5) regen</c> loop runs in the part of <c>FlushTick</c> that is
    /// outside <c>World._lock</c> (the snapshot above it takes and releases the lock), and it holds no view
    /// lock, so the thread on this path holds nothing to assert about.</para></summary>
    public void RegenTick(int ms, bool regenDue = false)
    {
        // Tick-thread-owned, so it is accumulated here, above the monitor, and only the reset below needs
        // the monitor. Mail-flag backstop: a resting or ghosted player still notices mail that arrived via
        // a path we forgot to poke. Event-driven refresh is the norm; this is one cheap DB re-check every
        // 30s, vs the old two-queries-per-stats-packet.
        _mailAccum += ms;

        bool sightDue = SecondSightScanDue();   // Session.SecondSight.cs: no monitor, like the rest
        bool due = regenDue
                || _mailAccum >= MailBackstopMs
                || Volatile.Read(ref _nextBuffExpiry) <= Environment.TickCount64
                || Volatile.Read(ref _crRageTier) > 0
                || sightDue;
        if (!due) return;   // the common case: no monitor entered at all

        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (_mailAccum >= MailBackstopMs) { _mailAccum = 0; RefreshMailFlags(); }

        ExpireBuffs();   // send each faded buff's live "fade" line + drop it (runs even when dead/topped-off)
        // Chung Ryong's Rage draining vita when it wears out (its price) — checked here so it fires whether the
        // fury lapses in or out of combat, resting or not. EffRage/the AC buff already stop themselves on time.
        if (_crRageTier > 0 && Environment.TickCount64 >= _rageUntil) ChungRyongRageWearOff();
        // Second Sight's notice, after the expiry pass, so a run that lapsed on this beat says nothing.
        if (sightDue) SecondSightScan();

        if (!regenDue) return;       // not a regen beat: the jobs above are all this beat owed
        if (_char.Hp == 0) return;   // dead: no natural regen (RTK bails on health==0 / state==1)

        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        if (_char.Hp >= maxHp && _char.Mp >= maxMp) return;   // already topped off

        // 2% of max per tick, scaled by the governing attribute (Grace->vita, Will->mana). Ceil keeps a
        // low-level character (small max) ticking up by at least 1 instead of rounding to nothing.
        // Purple Potion adds a flat +20 to the vita term for its 300s (RTK Player.regen: `if
        // player:hasDuration("purple_potion") then regen = regen + 20 end`, on top of RTK's `healing` stat,
        // which is the stat Grace stands in for here). It is the only consumable that touches regeneration.
        int regenBonus = HasStatusFlag("purple_potion") ? 20 : 0;
        int hpGain = (int)Math.Ceiling(maxHp * 0.02 * (1 + (_char.Grace + eq.grace + regenBonus) / 100.0));
        int mpGain = (int)Math.Ceiling(maxMp * 0.02 * (1 + (_char.Will  + eq.will)  / 100.0));

        uint newHp = Math.Min(maxHp, _char.Hp + (uint)hpGain);
        uint newMp = Math.Min(maxMp, _char.Mp + (uint)mpGain);
        if (newHp == _char.Hp && newMp == _char.Mp) return;   // no change -> skip the HUD packet

        _char.Hp = newHp;
        _char.Mp = newMp;
        SendStats();   // push the refreshed HP/MP to the always-on HUD
    }

    // When a timed buff in _buffs lapses by TIME, send its live "fade" line (Content.FadeTextFor, e.g. Might ->
    // "Your strength returns to normal.") to the player and drop it. This is the SINGLE place expired buffs are
    // removed (TotalsSnapshot.At/BuffBoxText only skip them in-place), so the fade line fires exactly once. A recast's
    // refresh (RemoveAll by Key) is a separate, silent removal — only genuine expiry narrates a fade. Morph
    // timers ride _buffs too but have no fade text and revert independently (World.Tick -> RevertMorph), so
    // dropping their marker here is harmless.
    private void ExpireBuffs()
    {
        long now = Environment.TickCount64;
        bool any = false;
        for (int i = _buffs.Count - 1; i >= 0; i--)
        {
            if (_buffs[i].Expires > now) continue;
            var fade = Content.FadeTextFor(_buffs[i].Key);
            if (fade.Length > 0) SendMiniText(fade);
            BuffRemoveAt(i);
            any = true;
        }
        if (any) SendStats();   // effective caps/attributes dropped back -> refresh the HUD
    }

    /// <summary>
    /// 0x33 self/character appearance. Format decoded from handler 0x44fef0:
    ///   X(u16BE) Y(u16BE) dir(u8) entityId(u32BE) type(u8=0)
    ///   [7 appearance bytes] flag(u8) nameLen(u8) name[]
    /// type=0 selects the 7-byte appearance form (reader 0x436120). type must be 0 or 1 or the
    /// client bails. The 7 bytes are body-form/hair/face/colors/gear; exact semantics TBD, using
    /// plausible values so the sprite is visible.
    /// </summary>
    private void SendSelfLook()
    {
        // While morphed, the self view must use the SAME 0x07 self-id creature path ShowPlayer(this) uses,
        // NOT the human 0x33 look below — otherwise every appearance refresh that funnels through here (map
        // change EnterMap, casting Invisible via RefreshAppearance, equip/mount changes) would redraw us as a
        // human and silently wipe the disguise on our OWN client. Peers already stay correct (they redraw us
        // via ShowPlayer, which is morph-aware); this keeps self in sync. Removal stays dispel/duration-only.
        if (_morphLook != 0)
        {
            SendCreatureList(new[] { (_char.Id, (ushort)(0x8000 | _morphLook), _char.X, _char.Y, _morphColor, _facing) });
            return;
        }
        // 4.95 type-0 appearance layout (decoded via the look-lab sweeps):
        //   [0]=body/sex(0=M,1=F)  [1]=form/state(0=normal; 1 ghost, 3 mount, 5 invisible-spell)
        //   [2]=face  [3]=armor/coat  [4]=armor dye/palette index (0..8 all render as base color; 16/32/64/
        //   128/255 visibly recolor it, LIVE-confirmed 2026-07-27 -- exact mapping not catalogued, hardcoded
        //   0 for now)  [5]=weapon  [6]=shield
        // NOTE the old code put "Hair" in [1] = the FORM byte — that's what blanked the character.
        //   ... [5]=weapon (Honor sword/Flame blade/…), [6]=shield
        SendLook(_char.Id, _char.X, _char.Y, dir: _facing, SelfAppearance(), renderKind: 1, _char.Name, "self(0x33)",
                 hairColor: DisguisedAs?.HairColor);   // a player disguise's hair colour (5.33 draws it); else our own
    }

    /// <summary>Our 7 appearance bytes. Shared by the 0x33 self look and the 0x30 dialog paperdoll head (the
    /// client parses both with the same function, 0x436120 — see Session.Dialog.WriteHead), so a portrait can
    /// never drift from what's actually drawn on the map.
    /// <para><paramref name="face"/> overrides the worn head WITHOUT touching character state — that's what
    /// makes the Change Face browse a real try-on rather than a live mutation. An overridden portrait also
    /// forces the plain-human form byte: a preview should show a person, not the horse you happen to be
    /// sitting on (or, for a ghost at a shaman, nothing at all).</para></summary>
    private byte[] SelfAppearance(int face = -1) =>
        // A disguise as another player (Costume; Session.LuaDisguise) is our own view too, as RTK's gfx clone is drawn
        // to the whole area: their look, our form byte. A face preview (the Change Face browse) shows our own.
        face < 0 && DisguisedAs is { } d
            ? new byte[] { d.Sex, MountForm(), d.Face, d.Armor, d.ArmorDye, d.Weapon, d.Shield }
            : new byte[]
            {
                (byte)_char.Sex,
                face < 0 ? MountForm() : (byte)0,
                face < 0 ? FaceLook() : (byte)Math.Clamp(face, 0, FaceCount - 1),
                ArmorWireLook(_char.Armor), ArmorDye(), WeaponLook(), ShieldLook(),
            };

    /// <summary>Head/face sprite byte for appearance[2], forced into the range the client actually has art
    /// for. The 4.95 client ships exactly <see cref="FaceCount"/> heads (NexusTK.dat -> Head.tbl: "NumFaces
    /// 90", ids 0..89) and silently draws NOTHING for anything above that — a headless character, no error.
    /// The clamp exists because a face id is persisted player data that can outlive the bug that wrote it:
    /// the shaman's Change Face used to hand out RTK's 200..216 (a later client's id space), so saved
    /// characters can still carry an unrenderable value. The arrival puts the creation face back over one
    /// (<c>CharacterFactory.RestoreUnusableFromCreation</c>); this clamp still covers a creation byte past 89,
    /// which has nothing better to fall back to, so nobody has to pay 3,000 gold to a shaman to get a head back.</summary>
    internal const int FaceCount = 90;
    private byte FaceLook() => (byte)Math.Clamp((int)_char.Face, 0, FaceCount - 1);

    // Weapon/shield look bytes for the 0x33 appearance. CRITICAL: look 0 is a REAL weapon/shield sprite,
    // so an EMPTY slot must send 0xFF ("-1", proven live and matching RTK clif.c, which sends 0xFFFF for
    // weapon/shield when !pc_isequip). The slot is "occupied" iff a matching item is actually worn — a worn
    // weapon whose Look happens to be 0 (e.g. Novice sword) still shows sprite 0, only a bare slot is 0xFF.
    // Form/state byte for appearance[1]: 1 = ghost (Hp==0, see Die()), 3 = mounted (horse+rider composite),
    // 0 = normal human. Dead outranks mounted — a horse doesn't survive its rider's death. Other documented
    // values (5 invisible-spell) aren't driven from here.
    private byte MountForm() => _char.Hp == 0 ? (byte)1 : Stealthed ? (byte)5 : _char.Mounted ? (byte)3 : (byte)0;   // 5 = invisible-spell (faded/see-through)

    /// <summary>Redraw self + broadcast to every co-located peer after an appearance change (equip/unequip,
    /// mount/dismount, ghost/revive, morph, gender). RE'd root cause (2026-07-27) of the "abandoned red
    /// triangle nametag" litter bug: a peer's client renders us via its own 0x33 handler (0x44fef0 ->
    /// entity factory 0x44d7d0), which is SUPPOSED to look up our existing entity by id and destroy it
    /// before building the new one — but that destroy-before-create branch doesn't reliably fire for a
    /// bare re-send of 0x33 with an unchanged id (unlike a real 0x0E despawn, which walks a distinct,
    /// always-thorough teardown path — confirmed by disassembling both `0x44da40`/`0x44d9f0` destroy
    /// helpers). When it's skipped, the OLD entity (and its floating nameplate marker) is orphaned
    /// alongside the new one — exactly the "walk away and back" repro, since leaving view fully despawns
    /// (0x0E) before anything re-spawns. Fix: force the proven-reliable despawn ourselves before every
    /// peer redraw, instead of trusting the client to replace in place. Self's own view isn't affected —
    /// SendSelfLook updates the persistent self entity directly rather than destroying/recreating it.
    ///
    /// <para>SUPERSEDED for the common case (2026-08-08): <c>0x1d</c> is the packet this always wanted.
    /// Its handler <c>0x450db0</c> takes <c>id(u32BE) kind(u8) look[7]</c>, looks the entity up with
    /// <c>0x44b120</c> and <b>bails if it doesn't exist</b> — it never constructs one — then walks the look
    /// field by field and only reacts to the ones that actually changed. There is no destroy, no create, no
    /// name in the packet, so there is no orphan and no nameplate marker to leak, and no despawn/respawn
    /// flicker either. The teardown path above stays for the cases <c>0x1d</c> genuinely cannot express: a
    /// morph (a <c>0x07</c> creature sprite, not a 7-byte human look) and stealth (per-viewer visibility, so
    /// the answer is a despawn rather than any look at all). <c>LookUpdateInPlace,0</c> + <c>@reload</c>
    /// reverts to the old path wholesale.</para></summary>
    private void RefreshAppearance()
    {
        SendSelfLook();

        // 0x1d can only patch an entity the peer already has, and only a plain human one. If we were
        // morphed/faded on the last broadcast, peers may hold a creature sprite or nothing at all, so the
        // in-place patch would land on the wrong object or silently do nothing — take the full path and let
        // ShowPlayer re-decide per viewer. Same when we're special NOW.
        bool special = _morphLook != 0 || Stealthed || PvpGhostHidden;
        if (Content.LookUpdateInPlace && !special && _peersHoldHumanLook)
        {
            var look = SelfAppearance();
            byte hair = DisguisedAs?.HairColor ?? _char.HairColor;   // OUR colour (or a player disguise's), captured here — peers must not paint us with their own
            _world.Broadcast(_char.Map, p => p.UpdatePlayerLook(_char.Id, look, hair), except: this);
            return;
        }

        _world.Broadcast(_char.Map, p => p.DespawnEntity(_char.Id), except: this);
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
        _peersHoldHumanLook = !special;
    }

    /// <summary>Whether the last thing we broadcast to peers was a plain human <c>0x33</c> — i.e. whether a
    /// <c>0x1d</c> in-place look patch has something valid to land on. Set by every full redraw; consulted by
    /// <see cref="RefreshAppearance"/>. Peers who arrive later are drawn by <see cref="ShowPlayer"/> anyway,
    /// so this only has to track OUR own last broadcast, not per-viewer state.</summary>
    private bool _peersHoldHumanLook;

    /// <summary>0x1d — patch an already-drawn entity's appearance in place, no despawn/respawn.
    /// Body: <c>id(u32BE) kind(u8) look[7]</c>. <c>kind</c> 0 selects the same 7-byte player-look reader
    /// (<c>0x436120</c>) that <c>0x33</c> and the <c>0x30</c> dialog paperdoll use, so the bytes are
    /// byte-for-byte <see cref="SelfAppearance"/> — a look can never drift between the three.
    /// <para>Client-side quirk worth knowing: if the FORM byte is <c>2</c>, the handler (0x450e42) rewrites it
    /// to 5 (faded) unless the viewer is the subject or has state byte <c>[0x4fd400+0x1dc]</c> set — the very
    /// same "can see hidden" byte the user-list window checks before drawing a hidden row (§13c). So form 2 is
    /// a GM-invisibility the CLIENT enforces; we don't use it (stealth is server-side per-viewer), but don't
    /// send 2 by accident.</para></summary>
    internal void UpdatePlayerLook(uint id, byte[] look, byte hairColor = 0)
    {
        var d = new List<byte>();
        d.AddRange(PacketWriter.U32BEBytes(id));
        d.Add(0);                     // kind 0 = the player look
        // Same record, same divergence as 0x33: the 5.33 handler (0x46a6d0) calls the very same appearance
        // parser 0x449880, so an in-place patch has to carry the 11-byte form or it re-introduces exactly
        // the shift SendLook just fixed — and this path runs on every equip/unequip, which is where the
        // symptom was reported. hairColor is the SUBJECT's (this is the viewer's session), same reason as
        // ShowPlayer/SendLook.
        WriteAppearance(d, look, hairColor);
        SendMap(ServerOp.LookUpdate, _gameInc++, d.ToArray(), $"look-update(0x1d) id={id}");
    }

    /// <summary>Hp==0 is this server's whole "dead" state (matches the pre-existing Gateway/regen checks) —
    /// a ghost that can't fight, can't cast, and won't regen until <see cref="ReviveAt"/>/<see cref="ReviveInPlace"/> restores it.</summary>
    public bool IsDead => _char.Hp == 0;

    /// <summary>A dead player on a PvP map (Vale, the Sire Pit, any Content.IsPvpMap) is a HIDDEN ghost:
    /// invisible to the LIVING and non-colliding with them (no-clip), so the living neither see nor bump the
    /// corpse — but other GHOSTS still see it and still CLIP it (the dead share the arena). This is per-viewer
    /// visibility exactly like Rogue stealth, so it MUST take ShowPlayer's full despawn+redraw path
    /// (RefreshAppearance treats it as `special`, since the 0x1d in-place look patch can't express "invisible to
    /// some viewers"). It gates: ShowPlayer (hide from LIVING viewers — see the `!IsDead` there), HandleWalk
    /// (ghost mover clips other ghosts, no-clips the living — it is what HandleWalk passes as
    /// World.TryMovePlayer's `ghostMover`, which picks that branch of the occupancy check), and — because whether WE can see
    /// ghosts flips when OUR OWN state changes — ResyncPeers on death/revive. The ghost still sees itself and
    /// the living. Reviving (Hp>0) drops the predicate and the next RefreshAppearance/EnterMap redraws for all.</summary>
    public bool PvpGhostHidden => IsDead && Content.IsPvpMap(_char.Map);

    private byte WeaponLook()   // Type 3 = weapon; 0xFF = bare hands (_char.Weapon already holds a wire byte)
    {
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type == 3);
        if (e is not null) return WeaponWireLook(Content.ItemById(e.ItemId)?.Look ?? 0);
        return _char.Weapon != 0 ? _char.Weapon : (byte)0xFF;
    }
    private byte ShieldLook()   // Type 5 = shield
    {
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type == 5);
        return e is null ? (byte)0xFF : ShieldWireLook(Content.ItemById(e.ItemId)?.Look ?? 0);
    }

    /// <summary>Translate an <c>ItmLook</c> into the 0x33 appearance[5] weapon byte. The two speak DIFFERENT
    /// id spaces: ItmLook is RTK's flat space — family = look/10000 (0 sword, 1 spear/2H, 2 bow, 3 fan), art
    /// = look%10000 — while the 4.95 client splits its single byte into family RANGES. RE'd 2026-08-19
    /// (classifier <c>0x432fe0</c> + the art dispatch in the player-draw fn <c>0x432320</c>, which picks the
    /// EPF archive by the classifier's 1..4):
    /// <c>0x00..0x7F</c> Sword.epf art=byte · <c>0x80..0xBF</c> Spear.epf art=byte-0x80 ·
    /// <c>0xC0..0xDF</c> Bow.epf art=byte-0xC0 · <c>0xE0..0xFE</c> Fan.epf art=byte-0xE0 · <c>0xFF</c> bare.
    /// The old raw <c>(byte)Look</c> truncation is why every 2H weapon drew as a random sword (Frozen spear
    /// 10011 → 27) — the icon (a separate Item.epf id space) was right all along.
    /// <para>Art counts are the client's own (NexusTK.dat *.tbl "NumWeapons"): 95 swords, 31 spears, ZERO
    /// bows, 4 fans. An art index the client has no art for draws as the family's art 0 instead of garbage
    /// (Items.csv should stay within range — the 2026-08-19 sweep re-pointed every such row; this is the
    /// backstop). Bows are the exception: with 0 arts nothing can render — the byte is kept in the bow range
    /// (empty hand), which is the 4.95 client's authentic best. See re/render_weapons.py for the sheets.</para></summary>
    /// <summary>The 5.33 weapon field: a <b>u16 in RTK's flat <c>ItmLook</c> space</b> (family = value/10000),
    /// carried big-endian at appearance wire[6..7] — <i>not</i> 4.95's packed byte.
    ///
    /// <para>Established live: pinning wire[6] to 50 (u16 12800, family 1) drew a two-handed spear and 100
    /// (u16 25600, family 2) drew a bow, which is the flat space's own family arithmetic and nothing like the
    /// 4.95 byte ranges, where both values sit in Sword.epf. Sending 0 here — as this did before the field was
    /// identified — is flat-space sword art 0, a REAL sword, which is why an unarmed character was drawn
    /// holding one.</para>
    ///
    /// <para>Derived by inverting <see cref="WeaponWireLook"/> rather than by threading the raw look through
    /// every caller, because the appearance record is passed around as an opaque 7-byte array. That inverse is
    /// exact for every art index 4.95 actually has, and only loses out-of-range indices that WeaponWireLook
    /// has already clamped to the family base — which today is none of them, since Items.csv was re-pointed
    /// into range. If 5.33-only weapon art is ever used, this is the seam that needs the real look threaded
    /// through instead.</para></summary>
    public static ushort Weapon533(byte packed) => packed switch
    {
        0xFF => 0xFFFF,                                    // bare hands — RTK sends 0xFFFF when !pc_isequip
        <= 0x7F => packed,                                 // sword: flat art == the byte
        <= 0xBF => (ushort)(10000 + (packed - 0x80)),      // spear / two-handed
        <= 0xDF => (ushort)(20000 + (packed - 0xC0)),      // bow
        _ => (ushort)(30000 + (packed - 0xE0)),            // fan
    };

    public static byte WeaponWireLook(int look)
    {
        int family = look / 10000, art = look % 10000;
        return family switch
        {
            0 => (byte)(art < 95 ? art : 0),
            1 => (byte)(0x80 + (art < 31 ? art : 0)),
            2 => (byte)(0xC0 + Math.Min(art, 0x1F)),   // no 4.95 bow art; any index draws an empty hand
            _ => (byte)(0xE0 + (art < 4 ? art : 0)),
        };
    }

    /// <summary>appearance[6] shield byte: a DIRECT Shield.epf art id (no family ranges — the draw fn
    /// pushes SHIELD.EPF for any non-0xFF value). The 4.95 client ships 13 arts (Shield.tbl), so later-client
    /// ids fall back to art 0 rather than drawing nothing.</summary>
    public static byte ShieldWireLook(int look) => (byte)(look is >= 0 and < 13 ? look : 0);

    /// <summary>appearance[3] body byte for a worn armor's <c>ItmLook</c>. Body.tbl has 67 bodies (0..66);
    /// later-era cosmetics in Items.csv carry looks like 210 or 10008 that have no 4.95 body — clamp to the
    /// naked base body instead of persisting a truncated byte that silently draws the WRONG armor
    /// ((byte)10008 = 40, a wind armor).</summary>
    public static byte ArmorWireLook(int look) => (byte)(look is >= 0 and <= 66 ? look : 0);

    /// <summary>appearance[4] — the body layer's colour. RE'd end-to-end 2026-08-07 (player draw 0x432320,
    /// tinted blit 0x428c10): the client draws the body through a SEPARATE blit entry whenever this byte is
    /// non-zero, and that blit does <c>if (pixel &gt;= 0x30) pixel += colour * 8</c> before the palette lookup.
    /// So the garment's colours live in 8-entry ramps from index 0x30 and this byte picks the ramp, while
    /// skin/outline indices below 0x30 stay put. That is why the 67 Body.epf sprites all look green: green is
    /// merely the BASE ramp, and every seasonal colour is the same art shifted.
    /// <para>Source of the value is the worn armor's own <c>ItmLookColor</c> (earth armor = 6, sun = 5, …) —
    /// previously never sent, so every armor rendered at ramp 0 and the doll was always "spring" no matter
    /// what the bag icon showed. The Arena Master's war paint (<see cref="Character.ArmorColor"/>) overrides
    /// it when set, which is exactly RTK's behaviour: dyed gear shows the dye, undyed gear shows its own
    /// colour. Bleaching back to 0 restores the item colour rather than forcing green.</para>
    /// <para><b>The remap (2026-08-09).</b> appearance[4] is a ramp shift, and the client resolves the shifted
    /// index against <b>the body sprite's own palette</b> — <c>Body.tbl</c> assigns one per body, and only
    /// bodies <c>0..35</c> use Palette 0, the seasonal one the <c>ItmLookColor</c> 0..9 convention indexes.
    /// Bodies <c>36..43</c> (the wind armors) are on Palette 1, which shares only ramps 24/28/29/30/31 with
    /// Palette 0 and whose own 24 ramps all run light→dark where Palette 0's run dark→light. So the SAME
    /// number is a different colour on different armor: ramp 10 is the grayscale/black ramp on Palette 0 and a
    /// BROWN ramp on Palette 1 — a Hyun moo (black team) dye came out brown on wind armor, and Ju jak came out
    /// olive. Every value the rest of the server speaks (war paint AND <c>ItmLookColor</c>) is canonical, i.e.
    /// Palette 0's meaning; <see cref="Content.DyeRampFor"/> converts it to the ramp that renders that same
    /// colour on the body actually worn. Identity for Palette 0 and 2 bodies, so only wind needs rows today.
    /// Bodies 44..56 (ice + late gear) are Palette 2, whose ramps 0..31 are byte-identical to Palette 0.</para>
    /// <para>Worth knowing: wind armor's own <c>ItmLookColor</c> is 24 and RTK's Chung ryong war paint is
    /// <b>also</b> 24 (both mean the same azure ramp, and 24 is one of the shared rows so it needs no remap) —
    /// which is the one dye that still cannot change a wind armor's appearance, because it is already exactly
    /// what an undyed one renders. If a Chung ryong wind-armor player needs to be distinguishable from an
    /// undyed one in a team battle, point that pair at another blue: <c>36,43,24,12</c> in ArmorDyeRamps.csv
    /// (Palette 1 ramp 12 is a periwinkle blue, shading direction preserved).</para></summary>
    private byte ArmorDye()
    {
        if (_char.ArmorColor != 0)                                          // war paint wins while it's applied
            return Content.DyeRampFor(_char.Armor, _char.ArmorColor);
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type is 4 or 16);
        byte lc = e is null ? (byte)0 : Content.ItemById(e.ItemId)?.LookColor ?? 0;
        return Content.DyeRampFor(_char.Armor, lc);
    }

    // The swing sfx of the weapon currently in hand, by weapon CATEGORY (see IsStaffWeapon) rather than
    // RTK's own ItmSound field. That field turned out to be nearly useless for this: 331 ("most swords") is
    // shared not just by Maxcaliber/Wooden Saber/Novice Sword but by EVERY staff too (augury_staff,
    // staff_of_defense, wand_of_fire, the whole ju_jak/hsiao_chu/staff_of_chi families, …) — checked directly
    // against Items.csv. So the CATEGORY split stays: what the field is good for is the blade number itself,
    // and 331 is what it gives every blade — Novice Sword, Wooden Saber, Maxcaliber and Spike alike (all four
    // are ItmSound 331), which matches the live 2026-08-04 calibration. 0 if unarmed or the weapon has no sound.
    private int EquippedWeaponSound()
    {
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type == 3);
        var def = e is null ? null : Content.ItemById(e.ItemId);
        if (def is null) return 0;
        return IsStaffWeapon(def.Key) ? 335 : 331;
    }

    // Staff/wand/rod-category weapons swing differently (335.wav, confirmed live via a staff) than blades
    // (331.wav, live 2026-08-04 via Novice Sword / Wooden Saber). Every Items.csv row actually named "staff" or "wand"
    // (augury_staff, staff_of_power, wand_of_fire, the ju_jak/hsiao_chu/staff_of_chi upgrade chains, …) is a
    // genuine caster weapon, so a plain identifier match is reliable here — unlike ItmSound (see
    // EquippedWeaponSound's doc), Items.csv actually differentiates these by name consistently.
    private static bool IsStaffWeapon(string key) =>
        key.Contains("staff", StringComparison.OrdinalIgnoreCase) || key.Contains("wand", StringComparison.OrdinalIgnoreCase);

    // General 0x33 "create/look" for ANY entity (self or a test dummy). Type=0 = the 7-byte player
    // appearance form (parser 0x436120). renderKind (byte after the 7 appearance bytes) MUST be 1/2/3
    // or handler 0x44fef0 bails before allocating the sprite (1 = player sprite). appearance[0] is the
    // body form; [1]/[2] are the sprite layers whose valid id space we're mapping with the look-lab.
    private void SendLook(uint id, ushort x, ushort y, byte dir, byte[] app, byte renderKind,
                          string name, string label, byte? hairColor = null)
    {
        var nm = Encoding.ASCII.GetBytes(name);
        var d = new List<byte>();
        d.AddRange(PacketWriter.U16BEBytes(x));
        d.AddRange(PacketWriter.U16BEBytes(y));
        d.Add(dir);
        d.AddRange(PacketWriter.U32BEBytes(id));
        d.Add(0);                                   // type = 0 (player appearance form) — same on both clients
        WriteAppearance(d, app, hairColor);         // hairColor set only when drawing a peer (the subject's, not the viewer's)
        d.Add(renderKind);
        d.Add((byte)nm.Length);
        d.AddRange(nm);
        SendMap(ServerOp.PlayerLook, _gameInc++, d.ToArray(), label);
    }

    /// <summary>Write the 0x33 type-0 appearance in the <b>11-byte 5.33 form</b>, from our canonical 7-byte
    /// 4.95 record (<see cref="SelfAppearance"/>).
    ///
    /// <para>5.33 re-authored this record; it did not merely extend it. Its parser is <c>sub_449880</c>
    /// (returns <b>11</b>, where 4.95's <c>sub_436120</c> returns <b>7</b>). The slot meanings below were
    /// established by sweeping each byte live against the rendered sprite with <c>@look533</c> — the
    /// disassembly gives the widths and the read order, but only the sweep says what a field DRAWS, and
    /// where the two disagreed the sweep won:</para>
    /// <code>
    ///   wire  4.95                     5.33                       how we know
    ///   [0]   body/sex                 body/sex                   swept: 0 male, 1 female
    ///   [1]   form/state               form/state                 swept: 0/4 normal, 1 dead, 2+5 faded,
    ///                                                             3 mounted, 6+ no sprite
    ///   [2]   face/head                FACE/HEAD                  swept
    ///   [3]   armor                    HAIR COLOUR  (new in 5.33) swept
    ///   [4]   colour ramp              armor/coat                 swept: 0 unequipped, 1+ armors
    ///   [5]   weapon                   armor colour ramp          swept (needs a coat on to show)
    ///   [6-7] --                       WEAPON, u16 BE flat look   swept: 50 -> spear, 100 -> bow
    ///   [8]   --                       unknown                    swept, no visible effect
    ///   [9]   --                       shield                     swept
    ///   [10]  --                       shield colour?             swept: also moves the shield
    /// </code>
    /// <para>Note [2]/[3]: 4.95 has ONE head byte carrying hairstyle and all; 5.33 split hair colour into its
    /// own field directly after it. That split is why the first version of this sent the face id into the
    /// hair-colour slot and 0 into face — hair rendered an arbitrary colour and the head was always id 0.</para>
    ///
    /// <para>Sending the 4.95 seven bytes to 5.33 shifted every slot from face onward: face read our armor
    /// (equipping a coat restyled your hair), armor read our dye ramp (a naked character wore a coat), the
    /// ramp read our weapon, and weapon/shield read the name-length byte and the first character of the
    /// name (a naked character carrying a shield).</para>
    ///
    /// <para>The slots 4.95 has no equivalent for default to 0. <c>P1998_LOOK533_EXTRA="hair,tail"</c>
    /// changes those defaults, and <c>@look533 &lt;i&gt; &lt;v&gt;</c> pins any of the eleven live, which is how
    /// the table above was filled in and how the remaining "unconfirmed" rows should be settled.</para></summary>
    private static readonly (byte Hair, byte Tail) Look533Extra = ParseLook533Extra();

    private static (byte, byte) ParseLook533Extra()
    {
        var raw = ServerConfig.Current.Look533Extra;
        if (string.IsNullOrWhiteSpace(raw)) return ((byte)0, (byte)0);
        var p = raw.Split(',');
        byte a = p.Length > 0 && byte.TryParse(p[0].Trim(), out var x) ? x : (byte)0;
        byte c = p.Length > 1 && byte.TryParse(p[1].Trim(), out var z) ? z : (byte)0;
        Log.Info($"[look533] extra appearance fields: hair[3]={a} tail[10]={c}");
        return (a, c);
    }

    internal const int Look533Len = 11;

    /// <summary>Per-session per-byte overrides for the 5.33 appearance record, set by <c>@look533</c>.
    /// Null = send the derived value. This exists because three of the eleven fields have no 4.95
    /// equivalent and no reference server documents them, and the only way to learn what a field drives
    /// is to change it and look at the sprite — which is a two-second job in-game and a rebuild-and-relog
    /// job in code.</summary>
    private readonly byte?[] _look533Override = new byte?[Look533Len];

    /// <summary>The appearance record as the given client parses it, from our canonical 7-byte 4.95 one.
    /// <b>Pure and static so both shapes can be pinned by test</b> (Tests/AppearanceRecordTests) — the whole
    /// point of the version split is that adding 5.33 must not move a single 4.95 byte, and that is a claim
    /// worth enforcing rather than repeating.
    /// <para>V495 returns the 7 bytes unchanged. V533 returns the 11-byte form described above.</para></summary>
    public static byte[] AppearanceRecord(ClientVersion ver, byte[] app, byte hair = 0, byte tail = 0)
    {
        byte At(int i) => i < app.Length ? app[i] : (byte)0;
        if (ver != ClientVersion.V533)
        {
            var seven = new byte[7];
            for (int i = 0; i < 7; i++) seven[i] = At(i);
            return seven;
        }
        ushort weapon = Weapon533(At(5));
        return new[]
        {
            At(0),                    // [0]     body / sex
            At(1),                    // [1]     form / state (ghost, mounted, invisible)
            At(2),                    // [2]     face / head          <- 4.95 [2], same slot
            hair,                     // [3]     hair colour          <- new in 5.33
            At(3),                    // [4]     armor / coat         <- 4.95 [3]
            At(4),                    // [5]     armor colour ramp    <- 4.95 [4]
            (byte)(weapon >> 8),      // [6..7]  WEAPON, u16 BE in the flat ItmLook space (4.95 packs it
            (byte)(weapon & 0xFF),    //         into the single byte [5] instead)
            (byte)0,                  // [8]     unknown — swept with no visible effect; weapon colour?
            At(6),                    // [9]     shield               <- 4.95 [6]
            tail,                     // [10]    new in 5.33 — also moves the shield (shield colour?)
        };
    }

    /// <summary>The record this session should send, with any <c>@look533</c> pins applied (V533 only).</summary>
    // hairColor: the SUBJECT's hair-colour palette index. Non-null when drawing a PEER (ShowPlayer passes the
    // subject's colour from its snapshot) — because `this` is then the VIEWER, so reading this._char would paint
    // every peer with the viewer's hair. Null when drawing self / a paperdoll, where our own colour (plus any
    // in-progress dye preview) is correct.
    private byte[] AppearanceFor(byte[] app, byte? hairColor = null)
    {
        // Hair colour is per-character (Character.HairColor), the byte the rogue-hall dye service sets, with a
        // transient _hairPreview override so the dye menu can live-preview a candidate without persisting it.
        // (Look533Extra's hair component is superseded by this; its tail component still feeds appearance[10].
        // The @look533 live-pin loop below still wins for ad-hoc testing.)
        var (_, tail) = Look533Extra;
        byte hair = hairColor ?? _hairPreview ?? _char.HairColor;
        var r = AppearanceRecord(_ver, app, hair, tail);
        if (_ver == ClientVersion.V533)
            for (int i = 0; i < r.Length; i++)
                if (_look533Override[i] is byte ov) r[i] = ov;
        return r;
    }

    // Transient hair-colour preview: while the dye menu is browsing, this holds the candidate palette index so
    // the player's own sprite shows it (via RefreshAppearance) WITHOUT touching persisted Character.HairColor.
    // Null = show the real colour. Cleared on commit or cancel.
    private byte? _hairPreview;

    /// <summary>Append the appearance record in whichever shape this session's client parses. Every packet
    /// that carries one (0x33 look, 0x1d in-place patch, 0x30 dialog paperdoll) goes through here, because
    /// the 5.33 client resolves all three to the same parser (0x449880) and they must not disagree.</summary>
    private void WriteAppearance(List<byte> d, byte[] app, byte? hairColor = null) => d.AddRange(AppearanceFor(app, hairColor));

    // 0x16 CREATURE spawn (handler 0x450a00 -> builder 0x44dbc0 -> ctor 0x463020 -> base 0x462ec0).
    // Unlike 0x33 (which ALWAYS draws from the player sprite archive 0x4f2a84, so it can only render
    // players/NPCs that look human), 0x16 has its OWN graphic — this is the real monster/creature path.
    // Decoded field layout (offsets from the opcode byte; multi-byte = big-endian):
    //   +1  u32 owner/parent id (unused by the ctor; send 0)
    //   +5  u16 GRAPHIC id  -> stored at sprite+0x130 by 0x462ec0  (THE creature sprite)
    //   +7  u32 entity id   -> find/despawn key (must match what we track + pass to 0x0E)
    //   +0xb u16 X   +0xd u16 Y            (resting tile -> stored at entity+0x10c/+0x110)
    //   +0xf u16 X'  +0x11 u16 Y'          (the "walked-from" tile)
    //   +0x13 u32 flags (color/hp-bar?; send 0)   +0x17 u8 dir
    // There is NO name field and NO viewport gate (0x44dbc0 skips the 0x424310 check), so a creature
    // can be placed anywhere. The graphic id-space is unknown — swept live via @mobrow.
    //
    // *** CRITICAL: (X',Y') MUST differ from (X,Y). *** The ctor 0x463020 computes the walk distance
    // `[obj+0x148] = |X-X'| + |Y-Y'|`, and the per-frame screen-position code (0x463160) does
    // `idiv [obj+0x148]`. A stationary spawn with (X',Y')==(X,Y) → distance 0 → **integer
    // divide-by-zero → client crash** (found live via the Frida crash-catcher). So a creature always
    // "walks in" one tile: we send the from-tile one step north (or south at the top edge), distance 1.
    private void SendCreature(uint id, ushort sprite, ushort x, ushort y, byte dir, string label)
    {
        ushort fromX = x;
        ushort fromY = (ushort)(y > 0 ? y - 1 : y + 1);   // 1 tile away so the walk distance != 0
        var d = new List<byte>();
        d.AddRange(PacketWriter.U32BEBytes(0));         // +1  owner/parent id
        d.AddRange(PacketWriter.U16BEBytes(sprite));      // +5  graphic id
        d.AddRange(PacketWriter.U32BEBytes(id));        // +7  entity id
        d.AddRange(PacketWriter.U16BEBytes(x));           // +0xb resting X
        d.AddRange(PacketWriter.U16BEBytes(y));           // +0xd resting Y
        d.AddRange(PacketWriter.U16BEBytes(fromX));       // +0xf walked-from X
        d.AddRange(PacketWriter.U16BEBytes(fromY));       // +0x11 walked-from Y
        d.AddRange(PacketWriter.U32BEBytes(0));         // +0x13 flags
        d.Add(dir);                  // +0x17 dir
        SendMap(ServerOp.FloorItem, _gameInc++, d.ToArray(), label);
    }

    // *** 0x07 = the REAL creature/monster spawn (the "area characters" list). *** Handler 0x44fdb0
    // loops a u16 count of entities, each built by the SAME entity factory 0x44d7d0 that 0x33 uses —
    // BUT here the look descriptor's `look` field selects a DIRECT sprite instead of the 7-byte player
    // appearance. RE'd path: 0x44fdb0 -> 0x44d7d0 -> (look in 0x8000..0xbfff => descriptor type 1) ->
    // 0x44d8c8 -> ctor 0x461a50 (entity vtable 0x4cd098). That entity's draw 0x461c70 branches on
    //   [ent+0x178] (=type): type!=0 -> 0x461d37 -> monster resolver 0x434020/0x4342e0 which pushes
    //   "MONSTER.EPF" (0x4f1d18) and resolves the sprite from Monster.epf via 0x433d00 (Monster.tbl).
    // So look = 0x8000 + monsterLookId draws a real monster. (look < 0x8000 or > 0xbfff => descriptor
    // type 2 -> 0x462ec0, vtable 0x4cd118 = the item/object base, i.e. the invisible 0x16 path.)
    //
    // Per-entry layout (12 bytes; body[0..1] = count, entries follow; multi-byte = big-endian):
    //   +0 X(u16)  +2 Y(u16)  +4 id(u32)  +8 look(u16=0x8000|monsterLookId)  +10 color(u8)  +11 dir(u8)
    // color -> palette (ent+0x18e via resolver), dir/state -> ent+0x18d. Unlike 0x16 there IS a viewport
    // gate (0x424310): entries outside the camera rect are silently skipped, so spawn inside view.
    // rawColor: send the colour byte exactly as given, bypassing the V533 palette remap below. The colour/
    // look SWEEP tools (@crecol, @crow) set this — they exist to FIND the right 5.33 index, so the override
    // must not pre-empt them. Real spawns leave it false so horses (etc.) get the corrected hue.
    private void SendCreatureList(IReadOnlyList<(uint id, ushort look, ushort x, ushort y, byte color, byte dir)> es, bool rawColor = false)
    {
        if (es.Count == 0) return;
        var d = new List<byte>();
        d.AddRange(PacketWriter.U16BEBytes((ushort)es.Count));           // body[0..1] = entity count
        foreach (var e in es)
        {
            d.AddRange(PacketWriter.U16BEBytes(e.x));                    // +0  X
            d.AddRange(PacketWriter.U16BEBytes(e.y));                    // +2  Y
            d.AddRange(PacketWriter.U32BEBytes(e.id));                 // +4  entity id
            d.AddRange(PacketWriter.U16BEBytes(e.look));                 // +8  look (0x8000|monsterId => Monster.epf)
            // +10 palette/color — a RAMP SHIFT the client applies over the mob's own palette block, not a
            // palette index. Era-tuned colours >= 32 select SUPER{n}.PAL on 5.33 (the era client just
            // wrapped — horse colour 35 = brown ramp 3 there, SUPER0 blue on 5.33), so remap those pairs
            // for V533 only; 4.95 sends the colour unchanged. See Content.Mob5xPalettes and its CSV header.
            byte color = !rawColor && _ver == ClientVersion.V533 ? Content.Palette5x((ushort)(e.look & 0x7fff), e.color) : e.color;
            d.Add(color);
            d.Add(e.dir);                           // +11 dir/state
        }
        SendMap(ServerOp.CreatureList, _gameInc++, d.ToArray(), $"creature-list(0x07) x{es.Count}");
    }

    // Register a monster server-side AND draw it via the real Monster.epf path (0x07). lookId is the
    // Monster.tbl look index (0..~326); we OR in 0x8000 to hit the direct-monster-sprite branch.
    private Mob SpawnMonster(ushort lookId, ushort x, ushort y, string name, int hp, byte dir = 2, byte color = 0)
    {
        var mob = new Mob(_nextMobId++, lookId, x, y, name, hp) { Dir = dir };
        _mobs.Add(mob);
        SendCreatureList(new[] { (mob.Id, (ushort)(0x8000 | lookId), x, y, color, dir) });
        Log.Info($"   -> spawn MONSTER {mob.Id} '{name}' look={lookId} @({x},{y}) hp={hp}");
        return mob;
    }

    // 0x0E despawn (server->client).
    //
    // 4.95 (handler 0x450440): count(u8) then that many entity ids (u32BE). The client destroys each by
    // id (0x44d9f0) and stops early on a 0 id, so never pass id 0.
    //
    // *** 5.33 IS NOT A LIST. *** Its case body (0x46369f) is four instructions long:
    //     inc esi                     ; body base -> body+1, i.e. body offset 0
    //     push esi / call 0x4a1250    ; ONE u32 BE
    //     test eax,eax / je done      ; id 0 -> no-op
    //     push eax / call 0x466550    ; destroy that entity
    // No count byte, no loop. Sending the 4.95 shape makes the client read `count<<24 | id>>8` — a
    // nonexistent id — so it destroys nothing and silently returns 1 (handled). That is the whole of
    // "mobs don't disappear when killed" and "ground items don't disappear when picked up": Ctrl+R
    // appeared to fix them only because a map reload wipes every entity and re-streams from scratch.
    // A multi-id despawn therefore has to go out as N packets on this client.
    private void SendDespawn(params uint[] ids)
    {
        foreach (var body in DespawnBodies(_ver, ids))
            SendMap(ServerOp.Despawn, _gameInc++, body, $"despawn(0x0E) {body.Length}B");
    }

    /// <summary>The 0x0E bodies to send for these ids — list-shaped bodies of at most 255 ids each on 4.95,
    /// one 4-byte body per id on 5.33. Pure and static so both framings are pinned by test
    /// (Tests/ClientVersionWireTests, the despawn facts).
    ///
    /// <para>The 4.95 count is ONE byte, so more than 255 ids go out as several bodies, each count matching
    /// the ids that follow it. One body used to carry them all under a count of 255, and the client's loop
    /// stops at its count, so every id past the 255th stayed drawn until a map reload. Only a GM reaches that
    /// today: <c>@crecol</c> takes its range unclamped, and <c>@kill</c> then clears every local dummy in one
    /// call. Up to 255 ids it is the same single body as before, byte for byte. 5.33 has no count to wrap.</para></summary>
    public static List<byte[]> DespawnBodies(ClientVersion ver, params uint[] ids)
    {
        var live = ids.Where(i => i != 0).ToArray();   // id 0 stops the 4.95 loop and no-ops on 5.33
        var outp = new List<byte[]>();
        if (live.Length == 0) return outp;
        if (ver == ClientVersion.V533)
        {
            foreach (var id in live) outp.Add(PacketWriter.U32BEBytes(id));
            return outp;
        }
        foreach (var chunk in live.Chunk(255))
        {
            var d = new List<byte> { (byte)chunk.Length };   // 1..255: the count of exactly these ids
            foreach (var id in chunk) d.AddRange(PacketWriter.U32BEBytes(id));
            outp.Add(d.ToArray());
        }
        return outp;
    }

    // NexusTK has NO floating combat numbers — combat feedback is the over-head HP bar (0x13, below) plus the
    // effect/spell animations (0x29, SendEffect). The old melee path used to abuse 0x29 with a raw damage value
    // (which played effect #(dmg-1), an unintended graphic); that is gone — hits now send the 0x13 damage packet.
    //
    // 0x13 (server->client) = the combat DAMAGE packet: over-head HP bar + a hit-reaction animation +
    // an optional hit sound, all in ONE packet. Decoded from the 4.95 client handler 0x4508f0 (disx):
    //   body = id(u32BE) | critical(u8) | percent(u8) | hitSound(u8)
    //   * critical -> plays overlay animation (0x8f - critical, SIGNED) over the entity (a hit spark; the
    //     hit-effect is queued for 0x78 ticks via 0x4622d0->0x41b5d0). RTK uses 33 (normal) / 255 (crit).
    //   * percent  -> the over-head HP bar fill, 0..100 (the client skips the bar if percent > 100; 0 = empty).
    //   * hitSound -> played through the sound manager if nonzero (RTK's u32 damage tail lands here as its high
    //     byte = 0, so no hit sound normally; the 4.95 client ignores everything past body[7]). We leave this
    //     at 0 and send impact sfx over 0x19 instead (Session.PlayHitSfx) — it's a BYTE, and the calibrated
    //     player hit sound (349.wav) doesn't fit in one. The parameter stays because the field is real.
    // This is what draws the "remaining HP bar above a monster's head" on every hit. RTK's clif_send_mob_health
    // builds the same shape (plus the ignored u32 damage). critical is retunable live: it is the HitCrit row
    // in game-data/ServerTuning.csv (33 = RTK normal hit), so @reload picks up a change.
    private static byte HitCritByte => Content.HitCritByte;
    private void SendDamage(uint id, byte percent, byte critical, byte hitSound = 0)
    {
        if (percent > 100) percent = 100;                 // >100 would make the client skip the bar entirely
        var d = new List<byte>();
        d.AddRange(PacketWriter.U32BEBytes(id));        // body[1..4] entity id (u32BE)
        d.Add(critical);            // body[5] hit type -> overlay anim 0x8f-critical
        d.Add(percent);             // body[6] HP bar fill 0..100
        d.Add(hitSound);            // body[7] optional hit sfx (0 = none)
        SendMap(ServerOp.Damage, _gameInc++, d.ToArray(), $"damage(0x13) id={id} pct={percent} crit={critical}");
    }
    public void DamageOver(uint id, byte percent, byte critical, byte hitSound = 0) => SendDamage(id, percent, critical, hitSound);  // peer-facing

    // Remaining-HP percent for the over-head bar (1..100 for a live mob so its bar never reads empty; caller
    // passes 0 explicitly on death). Guards against MaxHp<=0 and negative Hp (a killing blow overshoots).
    private static byte HpPercent(Mob m)
    {
        int max = Math.Max(1, m.MaxHp);
        int cur = Math.Clamp(m.Hp, 0, max);
        if (cur <= 0) return 0;
        int pct = (int)((long)cur * 100 / max);
        return (byte)Math.Clamp(pct, 1, 100);
    }

    // Death beat: on a killing blow, empty the target's HP bar (0x13 percent=0, which also plays the final hit
    // overlay), then remove the corpse (0x0E) after a short delay so it doesn't just pop out of existence. 4.95
    // monsters have no death frame-set (monsfrm.tbl defines only walk/attack states), so the "death animation" is
    // this beat: last hit spark + empty bar, held briefly, then despawn. The delay is the DeathDespawnMs row
    // in game-data/ServerTuning.csv.
    private static int DeathDespawnMs => Content.DeathDespawnMs;

    // Show the result of a hit that already resolved in the world: draw the over-head HP bar for everyone on the
    // map, and on death run the death beat (empty bar + delayed despawn). `mob` is read for its remaining HP%.
    // `onMap` is the creature's map when it is not the hitter's: a periodic spell tick can strike a creature its
    // caster has just left (Session.LuaStrikeVictim).
    private void ShowDamageResult(uint mobId, Mob mob, bool died, byte critical = 0, byte hitSound = 0, ushort? onMap = null)
    {
        ushort map = onMap ?? _char.Map;
        byte pct = died ? (byte)0 : HpPercent(mob);
        _world.BroadcastWideArea(map, mob.X, mob.Y, p => p.DamageOver(mobId, pct, critical != 0 ? critical : HitCritByte, hitSound));
        if (died) ScheduleDespawn(map, mobId, DeathDespawnMs);
    }

    // Remaining-HP percent for OUR OWN over-head bar (mirrors HpPercent(Mob) above), reflecting worn-gear
    // bonuses like SendStats does. 0 only when actually dead (Hp==0) — a live player's bar never reads empty.
    private byte PlayerHpPercent()
    {
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + Totals().hp);
        uint cur = Math.Min(_char.Hp, maxHp);
        if (cur == 0) return 0;
        return (byte)Math.Clamp((int)(cur * 100 / maxHp), 1, 100);
    }

    // ---- Harden Body: total damage immunity ---------------------------------------------------------------
    // The four wards that make a blow simply not land — the poet spell and its three alignment reskins, plus
    // the plain flag the Scroll of Immortality sets. The RTK sourcing lives with the gate that reads this, in
    // Server/DamageIntake.cs; there is exactly one such gate now. It used to be copy-pasted into whichever
    // intake remembered it, and the comment here used to justify that with "there is no shared helper" — which
    // held for two sites and then quietly stopped holding for five. Two of them never got the check; one of
    // those turned out to be a real bug and one turned out to be right, and neither could be told apart from
    // the other until the check had a single home (#28).
    private static readonly string[] HardenBodyWards =
        { "harden_body", "harden_body_poet", "deaths_guard_poet", "lifes_protection_poet", "body_of_alignment_poet" };
    internal bool DamageImmune
    {
        get { foreach (var w in HardenBodyWards) if (HasStatusFlag(w)) return true; return false; }
    }

    // Called by World.Tick (the shared mob-AI heartbeat) when a provoked mob lands a swing on us: apply the
    // damage, refresh our HUD, and show the over-head hit/HP-bar to the whole map (the same 0x13 feedback a
    // mob takes, just aimed at our own entity id) — dying (Hp hits 0) triggers Die() below.
    //
    // The sequence itself is Session.TakeDamage; what is left here is the three things only a creature swing
    // knows: whose blow it was, whether it came from behind, and the crit roll.
    public void ApplyMobHit(Mob mob, int rawDmg)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        // Positional "attacked from behind while both face the same way" 2x (RTK swingDamage.lua's
        // side==target.side rule; the pipeline doubles its once-floored figure, in RTK's order, #78). NOT
        // ported: the item-flag-gated backstab/flank abilities a handful of legendary weapons grant (see
        // Combat.cs).
        bool behind = Combat.IsBehindTarget(mob.Dir, _facing, mob.X, mob.Y, _char.X, _char.Y);
        int critChance = 0;   // written by the deferred roll below, read by the log line

        TakeDamage(new DamageIntake(DamageKind.MobMelee, rawDmg)
        {
            IgnoresHardenBody = false,
            StampMobAttacker  = mob,
            DoubleForRear     = behind,
            // RTK hitCritChance.lua: mobs DO roll a crit chance, but real swingDamage.lua's
            // _getMobSwingDamage never multiplies mob damage by it — only a PLAYER's own swing gets the x3
            // (see Combat.RollCritChance's doc). We still roll it purely for the wire-visual crit byte.
            // Deferred so an immune target costs no draw from Random.Shared — see DamageIntake.RollCritByte.
            RollCritByte = () =>
            {
                critChance = Combat.RollCritChance(attackerIsMob: true,
                    atkGrace: 0 /* unused on the mob-attacker branch */, atkLevel: mob.Level, atkHit: mob.Hit,
                    tgtGrace: _char.Grace + Totals().grace, tgtLevel: _char.Level);
                return critChance == 2 ? (byte)0xFF : HitCritByte;   // RTK: 33 normal / 255 critical
            },
            HitSound = MobHitSfx,
            LogLine  = dmg => $"   -> mob {mob.Id} '{mob.Name}' hit {_char.Name} for {dmg}{(behind ? " (from behind x2)" : "")}{(critChance == 2 ? " (crit flavor)" : "")} -> {_char.Hp}/{_char.MaxHp}",
            // The blow landed and they lived: roll whatever this creature procs on a hit (MobSpells.csv rows
            // with Trigger=onhit -- the caverns' venom is the one that needs it). Deliberately last, so the
            // swing's own damage, durability and HUD update are all committed first, and so a killing blow
            // never trails a proc behind the corpse.
            AfterSurvivedHit = () => TryMobOnHitSpell(mob),
        });
    }

    // Take incoming SPELL damage from another player (PvP) or from yourself (self-cast, e.g. sparking yourself
    // in an arena). The deduction damage-reduction (sanctuary line / Baekho's Cunning) applies the same as a
    // melee hit, and physical AC is netted HERE — centralised so every PvP spell path gets it exactly once and
    // none of them can forget: the archetype damage spells, the 4-way and 5-way zaps, the sacrifice strikes
    // and the overflow splash all arrive through this method. Callers must pass the RAW figure — LuaSacApply
    // and ApplyOverflow used to net it themselves and no longer do, or it would land twice.
    //
    // ("Physical AC applies to magic" used to be the other way round, on the reasoning that the caster's
    // deflect roll already gates a spell — refuted by the Spark probe, whose ten self-cast readings tracked
    // the caster's OWN AC as it was varied by swapping gear, solving to floor(base * (1 + ac/100)) with zero
    // residual. Same term, same -80 human floor as a creature swing.)
    //
    // The over-head HP bar goes to the whole map; a hit BY someone else prints "<name> hits you with <spell>."
    // and marks both sides as PvP foes; dying drops you to ghost form via Die(), exactly like a mob kill.
    // attacker==this on a self-cast (no "hits you", no foe mark).
    /// <returns>The damage this hit actually applied, AFTER the sleep amplifier and Deduction but NOT capped
    /// by the victim's remaining HP — so a killing blow reports the full figure and the caller can see how far
    /// past zero it went. 0 when nothing landed (already dead, or immune). Overkill is
    /// <c>returned - hpBeforeTheCall</c>, which is what the vita strikes' overflow/backflow are computed from;
    /// every other caller ignores it.</returns>
    public int ReceiveSpellDamage(double rawDmg, Session attacker, string spellName)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        bool fromAnother = !ReferenceEquals(attacker, this);
        return TakeDamage(new DamageIntake(DamageKind.PlayerSpell, rawDmg)
        {
            IgnoresHardenBody = false,
            CritByte = HitCritByte,
            MiniText = fromAnother ? $"{attacker._char.Name} hits you with {spellName}." : null,
            PvpFoe   = fromAnother ? attacker : null,
            LogLine  = dmg => $"   -> {(fromAnother ? attacker._char.Name : "self")} '{spellName}' hit {_char.Name} for {dmg} -> {_char.Hp}/{_char.MaxHp}",
        });
    }

    // Take incoming MELEE damage from another player (PvP). The melee twin of ReceiveSpellDamage — but the
    // attacker-side PlayerSwingRaw has ALREADY applied our physical AC (it read our real defense via
    // SwingTarget.Of(this)), so `rawDmg` is post-armor and the intake must not net it again. The positional
    // rear-x2 comes over as `fromBehind` and doubles the intake's floored figure (#78, RTK's order). What is left
    // is what the intake side owns: Harden Body immunity, the sleep-family amplifier, the deduction reduction
    // (sanctuary / Baekho's Cunning), HP, per-hit durability (RTK clif_deductarmor rolls
    // every worn slot on a hit, unlike magic), the over-head HP bar to the whole map, the mutual PvP-foe mark
    // (so arena pets know who to go for), and death -> ghost. No "X hits you" chat line: melee shows only the
    // HP bar, matching a mob hit. crit is the wire-visual byte from the swing roll. `rawDmg` arrives unfloored
    // (PlayerSwingRaw), so the intake's one floor is the only rounding the blow takes (#78).
    public void ReceiveMeleeDamage(double rawDmg, Session attacker, bool crit, bool fromBehind = false)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        TakeDamage(new DamageIntake(DamageKind.PlayerMelee, rawDmg)
        {
            IgnoresHardenBody = false,
            DoubleForRear = fromBehind,
            CritByte = crit ? (byte)0xFF : HitCritByte,
            PvpFoe   = ReferenceEquals(attacker, this) ? null : attacker,
            LogLine  = dmg => $"   -> {attacker._char.Name} MELEE hit {_char.Name} for {dmg} -> {_char.Hp}/{_char.MaxHp}",
        });
    }

    // Defeated by a mob: redraw as a ghost (appearance[1]=1 via MountForm(), see IsDead/Snapshot/ShowPlayer)
    // and STAY that way — RTK has no auto-revive timer at all. A ghost wakes up only by pressing F1 and
    // picking "Silver Thread" (RunF1MenuAsync/SilverThread, §11k), which offers a choice of Shaman to warp
    // to and revives on arrival. Matches "players die to ghost form" (§8) with the real RTK revival gate,
    // replacing the earlier simplified fixed-timer/home-city stand-in.
    private void Die()
    {
        Log.Info($"   -> DIED: {_char.Name} on map {_char.Map} @ ({_char.X},{_char.Y})");
        _char.Mounted = false;                                            // a horse doesn't carry a ghost
        _char.MountKey = "";                                              // ...nor a stag: the ridden creature goes with it
        ClearAllTimedEffects();                                           // RTK pc_diescript wipes every timer on death — buffs, curses, stances, and any morph/stealth disguise (must run before the ghost redraw so it draws from the real look, not the morph)
        RefreshAppearance();                                              // redraw self as a ghost + everyone watching — every one of its broadcast lambdas has US as the subject, so it takes peers' _viewLock and OUR snapshot, never a peer's monitor (which is why it stays here, where the peers need it)
        ApplyDeathPenalties();                                            // exp/coin/gear/pile — see below
        SendMiniText("You have been defeated! Press F1 and choose \"Silver Thread\" to find your way back.");
        SaveChar();                                                       // the penalties above must survive a crash, not just a clean logout
        // ---- LAST, not first: the two acquisitions that can drop our own monitor -----------------------
        //
        // A session monitor is entered in ascending StateRank (#29 rule 2, Session.State.cs): when a nested
        // acquisition would DESCEND, it exits every monitor this thread already holds that outranks the target
        // while it blocks on that target, then retakes them. Both statements below descend into somebody else,
        // so both can leave OUR state unheld in the middle of the death — and a revive reaches us on another
        // thread from three shipped paths (a GM `@rez <us>`, an NPC Rebirth, a poet's Resurrect). Anything
        // running after a drop can therefore be running on a player who is alive again: charged the death
        // penalty, told they were defeated, and saved that way. So everything the death READS is above, and
        // the two descending calls are the last things Die() does.
        //
        //   ResyncPeers -> SyncPeers -> ReconcilePeer -> ShowPlayer(peer) -> peer.Snapshot()  (#177)
        //       reads every peer we can see under THAT peer's monitor (Session.WorldApi.cs). Descends for
        //       every peer that ranks below us, which is most of them on a busy map — no trade required, just
        //       somebody standing in our viewport whose monitor is busy. This one is older than the teardown
        //       below; it was above the penalties until #177 moved it here.
        //
        //   EndTrade -> WithStatePair(partner, us)                                            (#57, #172)
        //       #57: a ghost cannot finish an exchange. TryStartTrade already refuses to OPEN a window on a
        //       dead player; until this line nothing closed one that was already open, so a corpse could still
        //       confirm and the goods moved. Nothing is lost by ending the trade late — a partner's confirm
        //       arriving while the penalties run is already refused by the ConfirmExchange gate
        //       (`other.IsDead`), which is exactly what that gate is for.
        //
        // The peers still get the ghost from RefreshAppearance above, at the point they always did; what moved
        // is only the reconcile of OUR OWN viewport (whether we can see the other PvP ghosts now that we are
        // one), which nothing between here and there reads.
        ResyncPeers();                                                    // we're a ghost now — reveal the OTHER ghosts to us (PvP), and re-evaluate what we can see
        if (_trade is not null) EndTrade(_trade, "Exchange cancelled.");
    }

    // The RTK death penalty, ported from Spells/baseFunc/death_save.lua's `uncast` (the hook that actually runs
    // when a player's HP hits 0 — pc_diescript in the C engine only clears timers and threat, so reading mob.c/
    // pc.c alone gives the false impression that dying in RTK is free). Order and gating are the Lua's:
    //
    //   GM                     -> nothing (RTK revives them outright instead of laying them out)
    //   PvP kill               -> nothing ("no loss of dura and shit for pvp")
    //   safe city              -> nothing (dying in Kugnae/Buya/Nagnang/the Mythic Nexus is free)
    //   instance (59000-65000) -> 10% exp only
    //   anywhere else          -> coin spill, 20%-of-a-level exp, gear damage + break-on-death, death pile
    //
    // RTK's exact map list is by TITLE, not id, so it is reproduced here as names — "KaMing's Encampment" is
    // later content with no row in our Maps.csv and simply never matches.
    private static readonly HashSet<string> SafeDeathMaps = new(StringComparer.OrdinalIgnoreCase)
    { "Kugnae", "Buya", "Nagnang", "Mythic Nexus", "KaMing's Encampment" };

    private void ApplyDeathPenalties()
    {
        if (IsGm) return;
        // A PvP map is where players kill each other on purpose; RTK charges nothing for it (and the durability
        // helpers bail on those maps of their own accord anyway).
        if (Content.IsPvpMap(_char.Map)) return;
        if (Content.Maps.TryGetValue(_char.Map, out var mi) && SafeDeathMaps.Contains(mi.Name)) return;

        if (_char.Map is >= 59000 and <= 65000) { DeathExpLoss(0.10); return; }   // instance: exp only

        DeathDropGold();
        DeathExpLoss(0.50);      // the 0.50 only bites at level 99; below that it's 20% of the level band
        DeathDuraLoss();         // 10% max-dura off every worn slot + break-on-death gear
        DeathInventoryBod();     // break-on-death items in the BAG (a separate RTK pass)
        DeathPileDrop();         // ~half the droppable bag spills onto the corpse tile
    }

    // Leave ghost state: full heal (gear/buffs included) + warp to (map,x,y). Used by Silver Thread to
    // revive at the chosen Shaman; also reachable as a fresh-character/GM fallback via HomeCityFor.
    //
    // Ends in SaveChar, not MarkDirty (#196). A revive is the undo of a death, and Die() persists itself
    // synchronously because its penalties must survive a crash — so a revive that only marked the character
    // dirty left the CORPSE on disk for up to AutoSaveMs (15 s), and a crash or a hard kill in that window
    // loaded a ghost with the death penalty still charged. Cheap and symmetric: one write per revive, of
    // which nobody performs many.
    //
    // internal rather than private only so Tests/ReviveSaveTests can reach it: the only shipped caller is
    // Session.LuaReviveTarget (the poet Resurrect family), which needs a caster, a spell row and the script
    // gate to reach, none of which the persistence fact is about.
    //
    // A player who is logging out is neither healed nor moved (#330). The caster's thread looked them up
    // without their monitor, so their logout can run to the end before this EnterState, and the heal used to
    // land anyway: EnterMap put the departed session back on the map and SaveChar wrote a revived row for a
    // player who logged out dead. So _leaving is read before the heal, and when it is set nothing below runs:
    // no heal (an in-memory one is what the next login's fence would write, KickForReplacement), no move, no
    // save. EnterMap reads the same flag, but only after its exchange step, which can drop this monitor while
    // it waits for a lower-ranked partner; a logout in that gap would save the healed row. So that step is
    // taken HERE, before the check, with EnterMap's own call: a ghost cannot open another exchange
    // (TryStartTrade refuses IsDead on both sides, inside the pair), so EnterMap then has none to end, and
    // nothing between the check and the move gives the monitor up. A ghost holds an exchange only inside
    // Die(), whose EndTrade is its last statement, after a ResyncPeers that can drop the monitor. The step is
    // skipped for the heal-in-place fallback, which never ended an exchange. The caster's cost is the verb's,
    // paid before it calls here, and stays as it was: a raced Resurrect still costs its mana and its cooldown.
    internal void ReviveAt(ushort map, ushort x, ushort y, string arrivalMsg)
    {
        using var _ = EnterState();   // #29: a resurrection spell reaches here on the CASTER's thread
        var dest = Content.Maps.TryGetValue(map, out var mi) ? mi : null;
        if (dest is not null && _trade is { } open) EndTrade(open, "Exchange cancelled.");   // EnterMap's own step
        if (_leaving)
        {
            Log.Info($"   -> REVIVE refused: '{_char.Name}' is logging out — nobody healed or moved");
            return;
        }
        _char.Hp = EffMaxHp;
        _char.Mp = EffMaxMp;
        if (dest is not null)
        {
            // Never null for a ghost after the check above (see the note). Read anyway: a target someone else
            // revived since the verb's check is alive, and can open an exchange while our own step waits; if a
            // logout then gets in at EnterMap's step, nobody moved, and the stats, the line and the save below
            // are not made for a session that has left.
            if (EnterMap(dest.Id, dest.Xs, dest.Ys, x, y, dest.Name) is null) return;
        }
        else SendSelfLook();   // fallback: just heal in place if the map isn't loaded
        SendStats();           // push the restored HP/MP to the HUD (EnterMap doesn't send stats itself)
        SendMiniText(arrivalMsg);
        SaveChar();            // #196: the death that got us here ended in SaveChar, so the row on disk is a corpse until this runs
        Log.Info($"   -> REVIVED: {_char.Name} at map {_char.Map} @ ({_char.X},{_char.Y})");
    }

    /// <summary>Leave ghost state WITHOUT moving — the Shaman/Priest NPC revival (RTK shaman.lua's `click` and
    /// totem_npc.lua's `_resurrect`, which both do `state = 0; health = maxHealth; magic = maxMagic`). The
    /// player walked their own ghost to the NPC, so there is nothing to warp to. Same restoration as
    /// <see cref="ReviveAt"/> minus the map change; <see cref="RefreshAppearance"/> is what drops the ghost
    /// form here (ReviveAt gets that implicitly from EnterMap).
    /// <para>Ends in <c>SaveChar</c> for the reason given on <see cref="ReviveAt"/> (#196): the death this
    /// undoes saved itself, so a revive that only marked the character dirty left the corpse on disk until
    /// the next autosave sweep.</para>
    /// <para><b>A player who is logging out is not revived</b> (#330's class; PR #358 review, R5; owner,
    /// 2026-10-21), the check <see cref="ReviveAt"/> makes, in the same place: <c>_leaving</c>, read under this
    /// monitor before the heal. <c>@rez &lt;name&gt;</c> looks its target up on the GM's thread with no monitor,
    /// so the target's whole logout can run before this <c>EnterState</c>; the heal then landed on the departed
    /// session (what the next login's fence writes), was broadcast, and <c>SaveChar</c> wrote a revived row for a
    /// player who logged out dead. Nothing is done then, and false tells the caller nobody was revived. There is
    /// no exchange step to take first, as <c>ReviveAt</c> takes <c>EnterMap</c>'s: nothing before the heal
    /// gives this monitor up. A logout that gets in later, while <c>ResyncPeers</c> waits on a peer, finds a
    /// living player and saves them so. The NPC Rebirth and the Shaman's revival reach here on the player's own
    /// thread and never meet the flag.</para></summary>
    /// <returns>False when nobody was revived: the player is logging out. Only <c>@rez &lt;name&gt;</c> reads it.</returns>
    internal bool ReviveInPlace(string message)
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        if (_leaving)
        {
            Log.Info($"   -> REVIVE (in place) refused: '{_char.Name}' is logging out — nobody healed");
            return false;
        }
        _char.Hp = EffMaxHp;
        _char.Mp = EffMaxMp;
        RefreshAppearance();   // redraw self + everyone watching as living again (MountForm drops form 1)
        ResyncPeers();         // living again — stop seeing the PvP ghosts we could see while dead
        SendStats();
        SendMiniText(message);
        SaveChar();   // #196: MarkDirty left the death's row (Hp 0, penalised exp) on disk for up to AutoSaveMs
        Log.Info($"   -> REVIVED (in place): {_char.Name} at map {_char.Map} @ ({_char.X},{_char.Y})");
        return true;
    }

    // Home-city placement (HomeCityFor / PlaceNewCharacter) moved to Shared/CharacterFactory so the login
    // server (account creation) and this game server (world entry for never-seen accounts) agree on the
    // spawn. Revive points that reused HomeCityFor call CharacterFactory.HomeCityFor.

    // Broadcast a corpse despawn (0x0E) to the map after `ms`, so the death beat is visible first. The mob is
    // already gone from the world's mob list (World.TryDamage removed it), so nothing ticks it in the meantime,
    // and mob ids are monotonic so the id can't be reused before this fires. 0 ms = despawn immediately.
    private void ScheduleDespawn(ushort map, uint id, int ms)
    {
        if (ms <= 0) { _world.Broadcast(map, p => p.DespawnEntity(id)); return; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ms); _world.Broadcast(map, p => p.DespawnEntity(id)); }
            catch (Exception ex) { Log.Error($"delayed despawn of entity {id} on map {map} threw", ex); }
        });
    }

    // Play effect `effectId` over an entity via 0x29 — the real 4.95 spell-animation path. The wire byte maps
    // DIRECTLY to RTK's sendAnimation id (EfxWireOffset = 0), proven live: casting Ion (pcalign 0 → anim 4 =
    // unaligned zap) with a +1 offset showed the anim-5 graphic (unaligned heal) — i.e. wire N draws the anim-N
    // effect, so no adjustment. (The handler's internal index-1 is cancelled by the effect table being loaded
    // 1-based.) Overridable via P1998_EFX_WIRE_OFFSET. A/B/C = 0 → centered, default style. effectId < 0 = no-op.
    private static readonly int EfxWireOffset = ServerConfig.Current.EfxWireOffset;
    private void SendEffect(uint id, int effectId)
    {
        if (effectId < 0) return;
        int b = effectId + EfxWireOffset;
        if (b < 1 || b > 255) return;   // outside the u8 / effect-table range — skip rather than send garbage
        var d = new List<byte>();
        d.AddRange(PacketWriter.U32BEBytes(id));
        d.Add((byte)b);
        d.AddRange(PacketWriter.U16BEBytes(0)); d.AddRange(PacketWriter.U16BEBytes(0)); d.AddRange(PacketWriter.U16BEBytes(0));   // A/B/C = centered, default style
        SendMap(ServerOp.Effect, _gameInc++, d.ToArray(), $"effect(0x29) id={id} efx={effectId}");
    }

    // One-shot sound effect via 0x19. RTK's clif_playsound (type 3, a positional descriptor) is a LATER-client
    // format that the 4.95 client's TLV parser mis-walks into garbage (verified live: it built a sound object with
    // mode 4 / random soundId and stayed silent). This is the reverse-engineered 4.95 layout instead.
    //
    // The 0x19 handler (0x450ad0): body[1]=type. type 0 = sfx -> reads soundId(u16BE)@body[3..4], volume(u8)@body[5],
    // then runs a TLV tail (0x450c48) starting at body[2 (=P0)]+3. The tail's "C" field becomes the sound object's
    // MODE (ctor 0x463950 -> [obj+0x148]); the play wrapper (0x463ab0) plays only for mode 1 (-> 0x463ae8 ->
    // play fn 0x4798c0(soundId, type, gain, 0)). soundId/type/gain come from body[3..5] via the type-group. So we
    // put a minimal TLV *after* the 5-byte header (P0=3) that yields tagA=3, B0=0, C=1 with all skip bytes 0:
    //
    //   19 | 00(type=sfx) | 03(P0) | soundId u16BE | 64(vol=100 -> 0 dB) | 03(tagA) 00(B0) 01(C=mode1) 00 00 00 …
    //
    // volume 100 -> gain 0 (full): the client does log10(vol*0.01)*2000 dB (const @0x4cc408 = 0.0, so >0 is audible).
    // Sound id space is NexusTK.snd (zap 56, heal 4, fire 88, …). entityId is unused (this plays as a global sfx,
    // which is what spell sounds want — full volume, not distance-attenuated). Does NOT touch _bgm.
    private void SendSound(int soundId, uint entityId, byte volume = 100)
    {
        // soundId rides body[3..4] as a u16; the play wrapper reads [obj+0x134] as a WORD, so keep it in range.
        if (soundId <= 0 || soundId > 0xffff) { Log.Info($"   -x sound skipped sfx={soundId} (out of 1..65535)"); return; }
        var d = new List<byte> { 0x00, 0x03 };   // type 0 (sfx); P0=3 -> TLV tail begins after the 5-byte header
        d.AddRange(PacketWriter.U16BEBytes((ushort)soundId));          // body[3..4] soundId (u16BE)
        d.Add(volume);                            // body[5] volume (0..100 -> dB gain; 100 = 0 dB)
        d.Add(0x03); d.Add(0x00); d.Add(0x01);    // body[6..8] tagA=3, B0=0, C=1 (C -> object mode 1 = "play")
        d.Add(0x00); d.Add(0x00); d.Add(0x00);    // body[9..11] B1=0, F=0, B2=0 (all skips 0 -> clean parse exit)
        d.Add(0x00);                              // trailing pad
        SendMap(ServerOp.Audio, _gameInc++, d.ToArray(), $"sound(0x19) sfx={soundId}");
    }
    public void SoundAt(int soundId, uint entityId) => SendSound(soundId, entityId);   // peer-facing (broadcast)

    // Broadcast a cast's effect graphic (0x29) + its sound (0x19) over `overId` to everyone within range of it
    // (see RANGE below), caster included, so visuals + audio match RTK. Effect id / sound id come from the
    // pcalign ladder (Content.EffectAnim / EffectSound). anim/sound < 0 are skipped.
    //
    // The sound itself goes out as a 0x19 type 0 (a GLOBAL, full-volume sfx) — RTK's clif_playsound uses type 3,
    // a positional descriptor bound to the source entity, but that is a LATER-client layout the 4.95 TLV parser
    // mis-walks into silence (see the long note on SendSound). So the 4.95 client gives us NO distance falloff:
    // a sound is either at full volume or not sent at all, which makes the recipient box below the ONLY thing
    // keeping a swing on one side of a map from being heard on the other.
    private void BroadcastFx(uint overId, int anim, int sound)
    {
        // "Believe in magic" / Magic Effect (0x1b sub-5, RTK FLAG_MAGIC): the 0x29 spell graphic — and, per the
        // in-game description, its sound — are sent only to viewers who left the option ON (RTK gates this in
        // clif_sendanimation). It's a per-RECIPIENT filter, so your cast still shows for everyone else.
        //
        // RANGE: RTK binds both halves to the ENTITY the effect plays over -- clif_playsound(&mob->bl, ...)
        // and map_foreachinarea(clif_sendanimation, ..., mob->bl.x, mob->bl.y, AREA) -- so a spell landing
        // across the room is heard FROM THERE, and only inside that box (sound +/-9/+/-8, graphic +/-19/+/-17;
        // see World.SoundHalfW). We fall back to our own tile when the target is already gone (a mob that died
        // to this very cast), which still covers everyone who could see the fight and keeps the caster in range.
        var (cx, cy) = _world.EntityPos(_char.Map, overId) ?? (_char.X, _char.Y);
        if (anim >= 0)  _world.BroadcastWideArea(_char.Map, cx, cy, p => { if (p.WantsMagicFx) p.EffectOver(overId, anim); });
        if (sound > 0)  _world.BroadcastSameArea(_char.Map, cx, cy, p => { if (p.WantsMagicFx) p.SoundAt(sound, overId); });
    }

    /// <summary>"Believe in magic" / Magic Effect (0x1b sub-5): when off, this viewer is sent no spell graphics
    /// or sounds (see <see cref="BroadcastFx"/>). On by default (SettingBit(5) is in the SettingFlags seed).</summary>
    internal bool WantsMagicFx => _char.HasSetting(0x05);

    // Screen tile where the self is drawn (viewport anchor): the client's own tile->screen conversion
    // centers the player around here, NOT at a fixed spot -- it's edge-aware (clamped near map borders),
    // per Mithia 7.x's clif_sendxy. We previously hardcoded a flat (5,5), which only roughly matched near
    // spawn; anywhere else it diverged from where the CLIENT itself draws the character on screen, and
    // the mismatch is what showed up as "teleport -> animate -> pulled back": our camera scroll (driven by
    // this wrong anchor) fought the client's own correctly-centered render position every step.
    // Default mid-anchor (8,7) matches a 17-wide x 15-tall viewport, same as 5.33's SendSelfWalk.
    private (ushort vx, ushort vy) ViewAnchor()
    {
        // Realm-center (F4) FREEZES the camera: the client stops scrolling and the character walks across a
        // static view. The 0x04 handler writes the map origin as (X - vx, Y - vy) [0x44c660], so to keep the
        // origin pinned at the value captured when F4 was pressed (_lockOx,_lockOy) we draw the self at screen
        // (X - Ox, Y - Oy). That makes (X-vx, Y-vy) == (Ox,Oy) every step -> no scroll. If a step carries the
        // anchor outside the viewport, the client's scroll-gate (0x44c8f0 bounds check) rejects it, which ALSO
        // leaves the origin frozen — so the camera never moves regardless. Without realm-center, use the normal
        // edge-aware anchor (self centered, clamped near map borders).
        if (_realm != 0)
            return ((ushort)(_char.X - _lockOx), (ushort)(_char.Y - _lockOy));
        return EdgeAwareAnchor(_char.X, _char.Y);
    }

    // The client's tile viewport, confirmed against the binary (see below) and against every anchor ever
    // sent: vx=16 and vy=14 have both rendered, and the gate below is a strict `<`, so these are exact.
    private const int ViewW = 17;
    private const int ViewH = 15;

    // The normal follow-camera anchor: the screen tile the self is drawn at. The 0x04 handler derives the
    // map origin as (X - vx, Y - vy), so the anchor IS the placement control — raise vx and the map slides
    // right under a self drawn further into the view.
    //
    // WHAT THE CLIENT ACTUALLY CONSTRAINS (RE'd 2026-08-09, NexusTK_local.exe):
    //   * gate 0x44c8f0 (0x44c660 skips the ENTIRE origin write when it returns 0) checks only four things:
    //         0 <= X < mapW,  0 <= Y < mapH,  0 <= vx < ViewW,  0 <= vy < ViewH
    //     It never looks at the origin. Hence the clamps below are exactly the gate's bounds.
    //   * viewport builder 0x44c950 clamps the drawn rect to the map on all four sides:
    //         top  = max(0, originY-1)      bottom = min(mapH, originY + ViewH + 1)
    //         left = max(0, originX-1)      right  = min(mapW, originX + ViewW + 1)
    //     so a negative or over-long origin never reads outside the map — it just leaves empty screen.
    // Together: ANY origin is safe, and a map smaller than the viewport can be placed anywhere in it.
    //
    // MAPS SMALLER THAN THE VIEWPORT. The home interiors are 12x12, smaller than the 17x15 view, so they
    // can never fill it. The follow-camera branches below are meaningless there (there is nothing to
    // follow) and merely shoved the map into a corner. Instead, centre it: split the leftover margin, which
    // for odd leftovers leaves the extra row/column on the bottom-right. A 12x12 map lands with 2 blank
    // columns left / 3 right and 1 blank row top / 2 bottom.
    //
    // NOTE: an earlier revision of this comment claimed the client accepted only one vertical origin
    // (ys-ViewH) and rendered nothing otherwise. The disassembly above disproves that — the origin is not
    // gated and the draw rect is clamped — so that is NOT why the first Buya spawn came up blank. That
    // cause is still unexplained; don't let this comment be cited as if it were the answer.
    private (ushort vx, ushort vy) EdgeAwareAnchor(int cx, int cy)
    {
        int xs = _char.MapXs, ys = _char.MapYs;
        int vx = xs < ViewW      ? cx + (ViewW - xs) / 2   // narrower than the view -> centre it
               : cx < ViewW / 2  ? cx
               : cx >= xs - 8    ? cx - xs + ViewW
               : ViewW / 2;
        int vy = ys < ViewH      ? cy + (ViewH - ys) / 2   // shorter than the view -> centre it
               : cy < ViewH / 2  ? cy
               : cy >= ys - 7    ? cy - ys + ViewH
               : ViewH / 2;
        return ((ushort)Math.Clamp(vx, 0, ViewW - 1), (ushort)Math.Clamp(vy, 0, ViewH - 1));
    }

    // 0x04: absolute self (X,Y) + screen anchor. The handler (0x44faf0) sets camera scroll via
    // 0x44c660 AND calls 0x44b140 on the self entity, which advances/commits the self's walk that
    // 0x0C started. Sent at spawn and after every walk step.
    private void SendXy() => SendXyAt(_char.X, _char.Y);

    private void SendXyAt(ushort x, ushort y)
    {
        var (vx, vy) = ViewAnchor();
        var d = new List<byte>();
        d.AddRange(PacketWriter.U16BEBytes(x));
        d.AddRange(PacketWriter.U16BEBytes(y));
        d.AddRange(PacketWriter.U16BEBytes(vx));
        d.AddRange(PacketWriter.U16BEBytes(vy));
        d.Add(0);
        SendMap(ServerOp.Position, _gameInc++, d.ToArray(),
                $"xy(0x04) pos=({x},{y}) scroll=({x - vx},{y - vy})");
    }

    // Complete/unblock a server-authoritative walk WITHOUT scrolling the camera. The 0x04 handler
    // (0x44faf0) always calls the camera fn (0x44c660) THEN the walk-completion (0x44b140). 0x44c660
    // skips its ENTIRE scroll block — settle-offset, origin write, viewport rebuild — when the bounds-gate
    // 0x44c8f0 fails, and that gate rejects any view anchor outside the viewport. So sending vx/vy way
    // out of range makes the camera code a no-op (zero jerk) while 0x44b140 still runs: it advances the
    // self's logical to the walk destination and clears the walk-active gate so the next step is allowed.
    // This is how realm-center (frozen camera) coexists with fast-move OFF (which REQUIRES the 0x04 to
    // unblock — 0x0C commits the tile but never clears the gate, proven live: the client freezes at
    // frameCtr 2 until a 0x04 arrives).
    private void SendXyCommitNoScroll()
    {
        var d = new List<byte>();
        d.AddRange(PacketWriter.U16BEBytes(_char.X));
        d.AddRange(PacketWriter.U16BEBytes(_char.Y));
        d.AddRange(PacketWriter.U16BEBytes(0xFFFF));   // vx: out of viewport -> scroll-gate (0x44c8f0) fails -> camera untouched
        d.AddRange(PacketWriter.U16BEBytes(0xFFFF));   // vy: out of viewport
        d.Add(0);
        SendMap(ServerOp.Position, _gameInc++, d.ToArray(), $"xy-commit(0x04 no-scroll) pos=({_char.X},{_char.Y})");
    }

}
