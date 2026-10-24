# NexusTK 4.95 Client — Server Protocol & Implementation Reference

This document describes everything needed to stand up a server that a **2001-era NexusTK 4.95
client** (`NexusTK_local.exe`) will connect to, enter the game world with, and play in. It is
written to be self-contained: a developer should be able to implement a working server from this
document alone.

Everything here was established by **reverse-engineering the client binary** (static x86 disassembly
+ live Frida instrumentation) and confirming against a live client. Where a fact came from a
controlled experiment, that is noted so you can trust it; where something is a hypothesis or unknown,
that is called out too.

> **Binary under study:** `NexusTK_local.exe`, PE32 x86, **ImageBase `0x400000`, no ASLR** (it always
> loads at `0x400000`, so file offsets in this doc are absolute virtual addresses you can use directly
> in a disassembler or Frida). All `0x4xxxxx` addresses below refer to this binary.

---

## Table of Contents

1. [High-level architecture](#1-high-level-architecture)
2. [Wire framing](#2-wire-framing)
3. [Encryption (the "NexonInc" cipher)](#3-encryption-the-nexoninc-cipher)
4. [Connection lifecycle](#4-connection-lifecycle)
5. [The "enter world" trigger — the crux](#5-the-enter-world-trigger--the-crux)
6. [World-entry packet sequence (the working burst)](#6-world-entry-packet-sequence-the-working-burst)
7. [Packet reference](#7-packet-reference)
8. [The appearance system](#8-the-appearance-system)
&nbsp;&nbsp;&nbsp;&nbsp;8.2 [Drawing a player: layers, order and frames](#82-drawing-a-player-the-four-layers-their-order-and-their-frames)
9. [Character creation & the creation packet](#9-character-creation--the-creation-packet)
10. [Movement model](#10-movement-model)
11. [Speech & actions](#11-speech--actions)
11b. [Shared world (multiplayer)](#11b-shared-world-multiplayer--players-see-each-other--the-same-mobs-)
&nbsp;&nbsp;&nbsp;&nbsp;11b.1 [Persistent spawns, mob AI, collision & viewport streaming](#11b1-persistent-spawns-mob-ai-collision--viewport-streaming-)
11c. [Items — bag, gear, ground, combat](#11c-items--bag-gear-ground-and-combat--built-awaiting-live-495-verification)
11d. [Spells & skills](#11d-spells--skills--built-awaiting-live-495-verification)
11e. [NPCs & dialog](#11e-npcs--dialog--live-confirmed-on-495)
11g. [Durability, warp gating, whisper, spell resist](#11g-durability-warp-gating-whisper-and-spell-resist-added-2026-07-25)
11h. [Bulletin boards](#11h-bulletin-boards-added-2026-07-26--unverified-reply-shapes)
11i. [F2 / Subpath chat](#11i-f2--subpath-chat-added-2026-07-25--awaiting-live-confirmation)
11j. [Experience & leveling](#11j-experience--leveling-added-2026-07-25--awaiting-live-confirmation)
11k. [F1 / Central Functions menu & Silver Thread revival](#11k-f1--central-functions-menu--silver-thread-revival-added-2026-07-25--awaiting-live-confirmation)
11l. [Party & trade](#11l-party--trade-added-2026-07-26--awaiting-live-confirmation)
11m. [Inter-continent travel (world map)](#11m-inter-continent-travel-world-map-added-2026-07-26--native-screen-confirmed-broken-dialog-fallback-is-primary)
12. [Maps](#12-maps)
13. [Full opcode → client-handler table](#13-full-opcode--client-handler-table)
14. [Learnings, gotchas, things tried & failed](#14-learnings-gotchas-things-tried--failed)
15. [Reverse-engineering toolkit & methodology](#15-reverse-engineering-toolkit--methodology)
16. [Open problems / where to continue](#16-open-problems--where-to-continue)
17. [Reference servers & version gaps](#17-reference-servers--version-gaps)
18. [Key binary addresses (quick reference)](#18-key-binary-addresses-quick-reference)

---

## 1. High-level architecture

NexusTK uses **two TCP channels**, historically two separate servers:

| Channel | Default port | Role |
|---|---|---|
| **Login** | `2000` | Account creation, login, character select. Ends by handing the client off to the game server. |
| **Game** | `2005` | The actual world: enter-world, map, movement, chat, combat. |

The client connects to the login channel first, authenticates, and receives a **handoff** packet
telling it the game server's address/port. It then opens a *second* TCP connection to the game
channel and continues there. **These are separate connections** (separate sockets, separate session
objects on the server) — anything created during login (e.g. a character record) must be persisted
somewhere both channels can read (a DB or file), because the game-channel connection cannot see the
login-channel connection's memory.

Both channels use the **same framing and the same cipher** (see below). This is a 4.95-specific fact
— later versions (7.x) use a different game-channel cipher.

---

## 2. Wire framing

Every packet, both directions, both channels, except the two server frames listed at the end of this
section:

```
AA | length(u16, big-endian) | opcode(u8) | increment(u8) | body[...]
```

- `AA` — constant start-of-packet byte.
- `length` — big-endian u16. **Counts `opcode + increment + body`** (i.e. everything after the length
  field). Total bytes on the wire = `3 + length`.
- `opcode` — the packet type.
- `increment` — a per-packet sequence/nonce byte used by the cipher (see §3). The server chooses its
  own increment sequence for outgoing packets; the client does likewise. It generally starts at 0 and
  increases by 1 per packet on each side, but treat it as "whatever the sender put there" — you must
  feed the *received* increment byte back into the decrypt routine.
- `body` — opcode-specific payload, **encrypted** (except for a few plaintext opcodes, see §3).

There is **no trailer and no checksum** in 4.95. (7.x appends 3 "index" bytes — do **not** do that
for 4.95; it desyncs the client's frame assembler.)

**Multi-byte integers in packet bodies are big-endian** (u16 and u32). The client's field readers are:
- `0x475c90` — read u8
- `0x475ca0` — read u16 big-endian
- `0x475ce0` — read u32 big-endian

Length-prefixed strings are `len(u8) bytes[len]`, ASCII.

Multiple packets are commonly concatenated in a single TCP segment; parse in a loop: while at least 5
bytes and `buf[0]==0xAA`, read length, consume `3+length`, repeat. Handle partial packets by
buffering the remainder.

**Two server → client frames break this layout.** Every other server frame has the standard layout: all but
one go through `TkPacket.Build` or `TkPacket.BuildGame` (`Protocol.Tk495/TkPacket.cs`), and that one, the literal
name-available `0x02` in `LoginSession.NameAvailable`, is a standard frame too (increment `0x01`, a body of four
zero bytes ciphered with it). The server splits every inbound frame as above (`TkPacket.Parse`):

| Opcode | On the wire | Written by |
|---|---|---|
| `0x03` redirect (§4.1) | `AA len(u16BE) 03`, then one fixed-size struct: **no increment byte and no cipher**. `length` counts the opcode and the struct, `25 + nameLen` | `Protocol.Tk495/LoginRedirect.cs` `LoginRedirect.Build`; sent by `LoginServer/LoginSession.cs` `HandleLogin` (the handoff) and `Server/Session.cs` `HandleReLogin` and `HandleExitToSelect` |
| `0x7E` welcome (§4.1) | `AA 00 13 7E 1B "CONNECTED SERVER\n"`: the layout's shape, with a constant `0x1B` where the increment goes, but the text is **not ciphered** | `Protocol.Tk495/Welcome.cs` `Welcome.Bytes`; sent on connect by `LoginServer/LoginSession.cs` `RunAsync`, and by `Server/Session.cs` `RunAsync` on login-port connections |

A reader that takes an increment from every frame reads the redirect one byte late (`LoginProbe/Program.cs`
reads the reversed address from raw byte 4, straight after the opcode). The client skips the decipher for
`0x03` (receive paths testing the opcode at `0x477e1e` and `0x4780fd`), but none of its five decipher sites
exempts `0x7E`, so it deciphers the welcome text with increment `0x1B`; logins work with the frame as sent.
Fixed increments are not exceptions, since the field is "whatever the sender put there": the login channel's
`0x02` replies always carry `0x02` (`LoginSession.SendStatus`, `SendMessage`) or `0x01` (the name-available
frame in `LoginSession.NameAvailable`). The client's `0x10` arrival keeps the standard header; only its body is
plaintext (§3; `Session.HandleArrival` reads the body as received).

---

## 3. Encryption (the "NexonInc" cipher)

**4.95 uses ONE cipher on both channels**: a simple, self-inverse, 3-stage XOR keyed by the static
ASCII string `"NexonInc."` (9 bytes, including the trailing period).

> This was proven by RE: the key buffer at `0x50211c` is only ever built from `"NexonInc."`
> (replicated ×4 = 36 bytes; two build sites `0x475740` and `0x478b10`, both hardcode the string;
> key length = `strlen("NexonInc.")` = 9). Decrypt routine `0x478680`, encrypt `0x478760`, XOR
> primitive `0x478850`, identity table `0x4f3358`.
>
> There is **NO** name-derived key, **NO** per-session table cipher, **NO** `Urk#nI7ni` key, and **NO**
> trailer/index bytes in 4.95. Those are all 7.x-only and will break a 4.95 client.

The algorithm, applied to the **body only** (opcode and increment are sent in the clear), where `inc`
is the packet's increment byte and `key = "NexonInc."`:

```csharp
byte[] Crypt(ReadOnlySpan<byte> data, byte inc, byte[] key) // key.Length == 9
{
    var o = data.ToArray();
    for (int i = 0; i < o.Length; i++)
    {
        byte group = (byte)(i / 9);      // block counter (which group of 9) — a BYTE, see below
        o[i] ^= key[i % 9];              // stage 1: keystream
        o[i] ^= group;                   // stage 2: block counter
        if (group != inc)                // stage 3: mix in the increment, except on block == inc
            o[i] ^= inc;
    }
    return o;
}
```

It is **self-inverse**: the same function encrypts and decrypts. To send: `Crypt(plaintextBody, inc,
key)`. To receive: `Crypt(encryptedBody, receivedInc, key)`.

> **The block counter is 8-bit, and the stage-3 comparison must use the WRAPPED value.** The client's
> decrypt (`0x478680`) holds the group index in `EBX` and tests it as `cmp byte ptr [ebp+8], bl` — `inc`
> against the counter's **low byte** — then indexes the identity table with `ebx & 0xff`. Comparing an
> unwrapped `int` instead is invisible until a body exceeds `9 * 256 = 2304` bytes: group `256 + inc`
> carries the same byte value as `inc`, so the client skips the stage-3 XOR over those 9 bytes while a
> naive implementation applies it. The symptom is 9 corrupt bytes in one otherwise-fine large packet,
> only for `inc <= (bodyLen / 9) - 256`. Terrain streaming (§10.8) is the first thing the server sends
> that is long enough to trip it: a 27x25 prime window is a 2706-byte body, so ~18% of them carried three
> garbled cells until this was fixed.

**Plaintext opcodes (bypass the cipher entirely):** received opcodes `0x00`, `0x03`, and `0x40` are
handled as plaintext by the client. Most importantly for the server, the **game-channel arrival packet
`0x10` is sent by the client in plaintext** (it carries the handoff token as readable ASCII). Treat
`0x10`'s body as plaintext. Everything else is ciphered.

---

## 4. Connection lifecycle

> **Process split (2026-07-27):** the login channel (2000/2001) and game channel (2005/2006) now run as
> **two separate processes** (`LoginServer` and the game `Server`) sharing one SQLite DB (`state/project1998.db`).
> They can crash/restart independently. The login server is the internet-facing front door and does not load
> the game world/content.

### 4.1 Login channel (port 2000) — handled by the LoginServer process

1. **Server → client, on connect:** a plaintext welcome banner.
   ```
   AA 00 13 7E 1B "CONNECTED SERVER\n"
   ```
   (opcode `0x7E`, a constant `0x1B` where the increment goes, then the ASCII string, not ciphered; §2
   lists it among the frames that break the layout.) The client expects this immediately on connecting to 2000.

2. **Client → server `0x62`** — client signature, body `"baram\0"`. (The client identifies as the
   "baram" engine.) No meaningful reply required.

3. **Client → server `0x00`** — client version handshake (plaintext). No meaningful reply required.

4. Then either **account creation** (§9) or **login**:

   **Login — client → server `0x03`:** body = `nameLen name pwLen pw 00`. Example decrypted:
   `03 04 "test" 07 "dragon5" 00`. **The client DOES send a password**; the server verifies it with
   **BCrypt** against the `accounts` table. (Creation `0x02` carries `nameLen name pwLen pw 00 00 00` —
   same password field.)

   **Password rule (corrected 2026-10-24; this item used to say "3–8 chars").** The server's rule is **4 to 16
   characters with at least one digit** (`LoginServer/LoginSession.cs:206-216`, `PasswordProblem`), and it is
   applied only where a password is chosen: the `0x02` name check when one is supplied (`:158`), creation
   `0x04` (`:247`) and the `0x26` change (`:393`). Login does not re-apply it; `HandleLogin` reads the password
   and verifies it against the stored hash (`:286-287`, `:313`), so an account made under an older rule still
   logs in. The field is u8-length, so 16 is the server's choice, not a wire limit. "3–8" survives only as what
   the retail client's entry box is believed to accept; nobody has measured it (the server logs the length,
   never the password, to find out: `:176-179`).

   **Login never creates anything (hosting pass).** Three refusals, each a `0x02` message box with no
   handoff — the client stays on the login screen showing the text:

   | Case | Message |
   |---|---|
   | No character with that name | `That character does not exist.` |
   | Character exists, wrong password | `Incorrect password.` |
   | Character exists, no hash on file (pre-auth record) | `That character has no password set. Contact the server admin.` |

   Trust-on-first-use is **gone** (`P1998_ALLOW_TOFU=1` re-enables it for a one-off legacy adoption): it
   made every unregistered name a free account and let anyone claim a legacy passwordless character. The
   only way a character comes into existence is the creation flow in §9. Names match **case-insensitively**
   (both tables are `COLLATE NOCASE` on the normalized key) while the character keeps the casing it was
   created with — world entry no longer overwrites the stored name with whatever the player typed.

   Failed attempts are budgeted per source IP (default 10 / 5 min, `P1998_LOGIN_FAILS`), refused before the
   BCrypt verify runs; a short password does not survive an unmetered guessing loop, and `ConnGuard`
   only limits how often an address may *connect*, not what it sends once connected. The same gate is on
   the game channel's re-login path — otherwise the brute-force target just moves to port 2005. Both
   channels charge the budget to, and check address bans against, the same address: the player's, which is
   the PROXY header's behind a trusted proxy (`P1998_TRUST_PROXY`) and the socket's peer otherwise.

   **Logging:** the login channel's wire dump is OFF by default (`P1998_LOG_WIRE=1` to enable; `0` is off,
   and any other value takes the default and warns at startup). These packets carry the password in the
   clear, and the cipher is a fixed published XOR, so a "raw" dump is just as readable as a decrypted one.
   Both processes share one logger (`Shared.Core/Log.cs`) and read that variable the same way — only the
   default differs, and the login server's is off.

   **Server → client `0x03` (handoff):** tell the client where the game server is. **The envelope is an
   exception to §2:** `AA len(u16BE) 03` is followed directly by the struct below, with **no increment byte**
   and **no cipher**, and the client reads it as **one fixed-size struct** (`Protocol.Tk495/LoginRedirect.cs`
   `LoginRedirect.Build`, the only writer; the same struct also sends a game client back to login). Struct
   (as implemented and confirmed working):
   ```
   AA  len(u16BE)  03             // envelope: len = 25 + nameLen, no increment follows
   ip[3] ip[2] ip[1] ip[0]        // 4 bytes, IP octets REVERSED (e.g. 127.0.0.1 -> 01 00 00 7F)
   port(u16, big-endian)          // game server port, e.g. 2005 -> 07 D5
   17 00 09                       // (constants observed in the working handoff)
   "NexonInc."                    // the 9-byte key string, echoed
   nameLen name                   // the username
   <token: 5 bytes>               // handoff token, echoed back by the client in 0x10
   ```
   On receiving this the client opens a new TCP connection to the game port.

   **Handoff token — single-use nonce (2026-07-27).** The 5-byte token slot was a static constant
   (`00 01 12 11 00`); it is now a **server-minted, single-use, 60s-TTL nonce** bound to the username
   **and to the login connection's source address** (SHA-256 stored in `handoff_tokens`), so the game port
   can't be reached by claiming any username. **Hard wire facts, learned live:**
   - **Do NOT grow the token past 5 bytes** — the client parses the `0x03` reply as a fixed-size redirect
     struct; a 16-byte token corrupts the parse and breaks login.
   - **The username and the token SHARE one 13-byte field, so a long name truncates the nonce**
     (corrected 2026-08-08). The client does not keep the token in a slot of its own: it copies the tail of
     this reply — `<nameLen><name><token>` — into a single fixed **13-byte NUL-terminated** field
     (12 bytes + a forced terminator) and echoes that field back verbatim in `0x10`. That is why **every
     `0x10` body is exactly 23 bytes** whatever the name length. Surviving nonce bytes = `11 - nameLen`:

     | name length | nonce bytes echoed back |
     |---|---|
     | ≤ 7 | 4 (the full nonce) |
     | 8 | 3 |
     | 9 | 2 |
     | 10 | 1 |
     | 11 | 0 — nothing survives; the length byte and the name fill all 12 bytes |
     | 12 | 0 — and the **name itself** loses its last letter, so the arrival can never match |

     The earlier note here — "the client preserves the first 4 bytes and forces the 5th to 0" — was
     probed only with 7-character names, where `11 - 7 = 4` makes the two rules look identical. Validating
     a fixed 4 bytes therefore **rejected every account with a name longer than 7 characters**: it could log
     in, then bounced at world entry with `invalid/expired handoff token`. Both sides now derive the
     surviving length from the username (`HandoffTokens.SurvivingBytes`) so they cannot disagree.

     **So 11 letters is the longest name that can enter the world, and creation refuses anything longer**
     (#299, 2026-09-27): "Names must be 3 to 11 letters." The cap is not a number of its own. It is
     `HandoffTokens.MaxNameLength = HandoffFieldBytes - 1`, the 12 usable bytes less the length byte, and
     `Shared/NameRules` applies it at both the `0x02` name check and the `0x04` create (§9). Creation used to
     allow 12: the test client's 12-letter `tutorninetyb` got "Account created." on 2026-09-25 and was then
     dropped by the game server on its `0x10` arrival, while an 11-letter name on the same pair entered the
     world. After the length byte the field has room for 11 letters, so the username the game server reads
     is not the one the token was minted for. An account already created with a 12-letter name stays locked
     out.
   - Because that leaves as little as one byte (or none), **the address binding, not the nonce, is what
     carries the security for long names.** Mint records the login connection's IP; the `0x10` arrival must
     come from the same address. Keep the significant nonce bytes **non-zero** — the client's copy stops at
     the first NUL.

5. **Password change — client → server `0x26`** *(added 2026-08-25, observed live from the 5.33 client)*:
   body = `nameLen name oldLen old newLen new` — the `0x03` login body plus a third length-prefixed
   string, and **no trailing NUL** where `0x03` carries one (the live capture was exactly 18 bytes for a
   4+5+6-char triple). Three sources agree on the shape: the live capture, RTK's login server
   (`rtk/src/login/clif.c` `case 0x26` → char-server `0x1004` → `intif_parse_changepass`), and the 5.33
   binary's own server-message name table (`kServerNewPasswordCheck`). The 4.95 client has not been
   swept for whether its login screen can emit this; the handler is client-agnostic either way.

   **The dialog is MODAL — it freezes the whole login screen until the server answers.** That is how the
   missing handler presented: press OK, client hangs forever, while its `0x71` keepalive keeps ticking
   underneath (the connection is fine; only the dialog is waiting). The reply is the standard `0x02`
   status frame (`code len text 00`) and the **code routes it in the client**, same sub-dispatch as the
   creation ack:

   | Code | Meaning | RTK lane |
   |---|---|---|
   | `0x00` | changed — dismisses the dialog | `LGN_CHGPASS` |
   | `0x03` | bad name / wrong old password / banned / blocked | `LGN_ERRUSER` / `LGN_WRONGPASS` |
   | `0x05` | new password fails the strength rules | `LGN_ERRPASS` |

   The new password passes the same gate creation enforces (length + at least one digit), checked
   **before** the BCrypt verify so a malformed request costs nothing. And because `0x26` verifies the old
   password, it is **a login attempt to the per-IP throttle**: refused while blocked, burns budget on a
   wrong old password, clears it on success. Without that the change dialog is a brute-force oracle that
   bypasses the `0x03` budget entirely. `LoginSession.HandleChangePassword`,
   `LoginAuth.TryReadChangePassword`; pinned by `Tests/ChangePasswordTests.cs`.

   > **`0x26` is channel-scoped — same byte, two unrelated meanings.** On the **login** channel it is this
   > password change. On the **game** channel it is **self-walk** (client→server walk commit / server→client
   > walk pace, §10.3). They never collide because each channel has its own dispatcher and its own connection;
   > just don't let the shared `Opcode.ChangePassword = 0x26` constant fool you into thinking one handler
   > serves both. (5.33 routes the login-side `0x26` through its `ui-b` login/select dispatcher — see
   > `docs/5.x/Wire-Divergences.md`.)

### 4.2 Game channel (port 2005)

1. **The client speaks first** — do **not** send anything on connect. The client sends **`0x10`
   (arrival)** with a **plaintext** body in the standard §2 envelope, increment byte included:
   ```
   AA len(u16BE) 10 inc | 09 "NexonInc." | nameLen "name" | <handoff token bytes>
   ```
   Only the body skips the cipher; the frame is split like any other (`TkPacket.Parse`,
   `Protocol.Tk495/TkPacket.cs:61-66`), and `body[0]` is the byte after the increment
   (`Server/Session.cs:1104-1116`; corrected 2026-10-24, this block used to leave out the header).
   Parse the username from it: `klen = body[0]; ulen = body[1+klen]; user = body[2+klen .. +ulen]`.
   The trailing bytes are the **handoff token** (`body[2+klen+ulen ..]`); the game server
   **validates and single-use-consumes** it against the username (must exist, be unexpired, unconsumed,
   and bound to this user *and this source address*) — otherwise it **closes the connection**. This is what
   stops a client from connecting straight to the game port and claiming any username.
   (`P1998_ENFORCE_HANDOFF=0` downgrades a failure to a warning as a fallback.)
   **The token is NOT always 5 bytes here** — it is however much the client's shared 13-byte field had
   room for after the name (§4.1), so compare only the first `11 - nameLen` bytes.

2. The server now drives the **world-entry burst** (§5, §6).

3. **Exit to the select screen (Alt+X) — `0x0B`, and the answer is a bounce BACK to login** ✅
   *(corrected 2026-08-20; this section previously had cause and effect backwards)*

   Leaving the world does **not** drop the game socket. The client sends **`0x0B`** (body: a constant `00`)
   on it and then waits. The correct answer is the **`0x03` redirect struct pointed at the LOGIN server** —
   the same packet as the handoff in §4.1, just with the login address in it. The client reconnects to the
   login port and the whole select screen (name check `0x02`, login `0x03`, create `0x04`) runs there, where
   those handlers already live. `Session.HandleExitToSelect`.

   **This is verbatim what RTK does.** `case 0x0B: clif_cancelafk(sd); clif_closeit(sd);`
   (`rtk/src/map/clif.c:11397`), and `clif_closeit` (`:655`) builds the redirect from `log_ip`/`log_port` —
   `map.conf`'s `loginip:` / `loginport:`. The corroboration that makes it conclusive: **RTK's map dispatch
   has no `0x02`, `0x03` or `0x04` case at all** (pre-session it accepts only `0x10`; in-world it runs
   `0x05`–`0x85`). Its map server is structurally incapable of serving a select screen, so its clients must
   be going back to login — there is no other path.

   **What this section used to say, and why it was wrong.** It claimed "the client never reconnects to the
   login server" and had the game server answer the select screen's `0x03` itself, redirecting back to its
   own game port. The client *does* then re-login successfully — which is why the bug hid for so long — but
   it is stranded on the game socket, where character **creation** has no handler and hangs forever at the
   name field. The client never reconnected because **we never sent it the bounce**; the observed behaviour
   was our own silence, not a client limitation. Live trace of the broken flow:
   ```
   [08:31:38]  <~ RAW 6B on :2005: aa 00 03 0b 2c 62      <- exit to select
   [08:31:38]  <- pkt op=0x0b inc=0x2c len=3 body=1B   dec: 00
   [08:31:38]  ?? no handler for opcode 0x0b            <- dropped; nothing sent back
   [08:31:44]  <- pkt op=0x03 ... dec: 09 'facethree' 05 'asdf1' 00   <- 6s later, SAME game socket
   ```

   **`Session.HandleReLogin` is kept as a fallback** for exactly that case: a `0x03` arriving on the game
   port is re-authenticated and handed back to this same game port, which is the old behaviour unchanged.
   Nothing depends on it once the bounce works, but a client that ignores the redirect still gets in.

   **Where the client sends it — RE'd 2026-08-20.** `0x0044ed50` is a no-argument sender that emits exactly
   the 6-byte packet in the trace: `push 0xb` → write-byte helper `0x475bf0`, `[ebp-0x7f] = 0` (the body),
   `push 1` (body length) → `0x4759f0` (fetch the connection object at `[0x50214c]`) → `0x475ab0` (send). It
   has **one** caller, `0x0044a6e2`, and that call site settles the timing question below:

   ```
   0x0044a660..0x0044a6d6   release every cached world resource (a dozen `if (g) release(g)` globals)
   0x0044a6db   cmp byte ptr [ebp+8], bl      ; teardown takes a "notify the server" bool
   0x0044a6de   je   0x44a6e7                 ;   -> skip the send when it is 0
   0x0044a6e2   call 0x44ed50                 ; *** send 0x0B ***
   0x0044a6f7   push 1 / call 0x4759f0 / call 0x475bd0   ; conn[+0x3aa52] = 1
   0x0044a711   push 0x20c / call 0x43fd80 / call 0x4404e0  ; allocate + construct the replacement screen
   0x0044a730   push 0 / call 0x4759f0 / call 0x475bd0   ; conn[+0x3aa52] = 0
   ```

   So `0x0B` is sent from the **world object's teardown** (the ctor is `0x44a090`, §5) — *after* the world's
   resources are released and *immediately before* the replacement screen object is built, with a
   connection-object flag at `[+0x3aa52]` raised across the swap and cleared once the new screen is live.
   (`0x4759f0` is a getter for the global connection object, not a packet builder; `0x475bd0` writes that
   one flag byte. The receive-mode byte documented in §4.2's opening note lives 6 bytes earlier at
   `+0x3aa4c`.)

   > **Why the timing works.** The client's `0x03` dispatch is **state-dependent**: at the login/select
   > screen it is the redirect (proven — the fallback path above works), but the *world* dispatcher routes
   > `0x03` to `0x44f0e0`, download/URL (§13). The disassembly above shows the world object is already
   > dismantled when `0x0B` goes out and the replacement screen is constructed in the very next
   > instructions, so a reply that crosses the network cannot land in the world dispatcher. **Still worth
   > watching on a live run** — the flag-guarded window is inferred from its shape, not measured, and this
   > is the one part of the flow with no behavioural trace behind it.

   **What the client does on arrival back at login — observed live 2026-08-24.** The client treats the
   bounce like any redirect: its first packet on the new login connection is a **`0x10` Arrival
   announce**, exactly as it would greet a game server after a real handoff. Observed body:
   `09 "NexonInc." 04 "Test"` + 8 zero bytes — the length-prefixed key string, the length-prefixed
   name, and an all-zero tail (the bounce's token field is 5 bytes of padding; `HandleExitToSelect`).
   The login server logs and ignores it — `LoginSession.Handle` has an explicit `Opcode.Arrival` case,
   kept out of the `??` unknown-opcode path so a routine logout doesn't cry wolf — and the select
   screen proceeds with the normal `0x02`/`0x03`/`0x04` flow on the same connection.

### 4.3 Robustness / DDoS hardening (app-layer, 2026-07-27)

App-layer defenses on both front doors (they complement — do not replace — infra-layer defense: upstream
scrubbing, OS SYN-flood protection, a firewall restricting the game ports to post-handshake traffic).

- **Guarded accept loops** — a transient `AcceptTcpClientAsync` throw is logged and skipped, never faulting
  the listener task.
- **Connection admission (`Shared/ConnGuard.cs`)**, checked on every accept before a session is spawned:
  - **Global cap** (load-shed): past `P1998_<GAME|LOGIN>_MAXCONN` (default 2000) accept-then-close.
  - **Per-IP concurrent cap**: `P1998_<…>_PERIP` (default **8** — sized to reliably support ~2 players/IP
    incl. the brief login→game socket overlap and a lingering half-open "ghost" socket; it is **not** a hard
    player quota — enforce that in login logic if wanted).
  - **Per-IP open-rate limit** (fixed window): `P1998_<…>_RATE` opens per `_RATEWIN_MS` (default 30 / 10 s),
    catching connect/disconnect churn floods the concurrent cap wouldn't.
  - **Loopback is exempt** from the per-IP + rate gates (local dev, the client test box, and the same-box
    login→game hop all originate from 127.0.0.1) but still counts toward the global cap. Rate table is
    soft-capped (fails **open** past 100k distinct IPs) so it can't itself become a memory-DoS.
- **Handshake watchdog** — a freshly-accepted connection must send its first **valid framed** packet within
  `P1998_HANDSHAKE_MS` (default 15 s) or it is dropped (slow-loris defense). Only the first packet is gated
  (via an `_established` flag), so an in-world / AFK / Alt+X-idle player is never disconnected. Reads after
  the handshake are untimed. The watchdog **closes the socket** (unblocking the pending read) rather than
  relying on `NetworkStream`'s unreliable read-cancellation.
- **Unframed-buffer bounds** — the shared reader also drops a connection, before and after the handshake,
  once its unframed buffer exceeds one maximum legal frame (`3 + 0xFFFF` = 65,538 B) or its head byte is not
  `0xAA`, because neither state can ever produce a frame and §2's checksum-free, trailer-free framing gives
  nothing to re-sync against. A third state goes the same way: a `0xAA` head whose **length field is under
  2**. The field counts opcode + increment + body, so 2 — a body-less frame, `AA 00 02 op inc` — is the
  smallest value §2's `3 + length` can describe; `AA 00 00` and `AA 00 01` are arithmetic no further byte can
  rescue, and they used to throw out of the read loop instead of dropping the peer.
- **Non-blocking writes (the tick-stall fix — most important).** `Session.Send` no longer does a synchronous
  `_stream.Write` on the shared 600 ms `World.TickLoop` thread. It enqueues onto a **bounded per-session
  channel** (cap 2048) drained by one dedicated writer task that owns the only socket writes. A client whose
  TCP receive buffer is full (slow, or deliberately not reading) used to **block that write and freeze mob
  AI for everyone on the map**; now the tick thread does an O(1) `TryWrite` and moves on, and a client whose
  queue overflows is dropped — the world never stalls. The single-reader channel also guarantees frames
  never interleave mid-packet (what the old per-session send lock did). The LOGIN channel runs the SAME type
  (`Shared/TcpOutbound.cs`, one outbound for both processes) with its own numbers: cap 64 frames plus a 10 s
  bound on any single write, `P1998_LOGIN_WRITE_MS`. So a peer that stops reading is dropped rather than
  holding that session's read loop, and the redirect is drained before the socket closes. Both channels log
  `SLOW SEND` (`P1998_SLOW_SEND_MS`, 250 ms) long before anything is dropped.

---

## 5. The "enter world" trigger — the crux

**This is the single most important fact in this document, and the reason naïve ports of 6.x/7.x
servers fail with a silent black-screen hang.**

The client has two receive-dispatch objects:

- A **connection-state object** (vtable `0x4cbd8c`) that handles **only opcode `0x02`**.
- A **game-world object** (vtable `0x4cc3b8`) whose dispatcher `0x44b9c0` handles opcodes `0x03`–`0x68`
  (via `remap[opcode-3]` at `0x44bc80` → jump table `0x44bbd4` → real handler).

**Right after `0x10`, the game-world object does not exist yet.** It is created *only* when the client
receives **opcode `0x02` with first payload byte `0x00`** (handler `0x444de0` → `0x4406c0` → world
ctor `0x44a090`). Until then, every world packet (`0x15`, `0x04`, `0x33`, …) is silently dropped
because there is no dispatcher to receive it.

The 6.x/7.x reference servers never send `0x02`+`00`, which is exactly why porting them yields a
client that connects, goes quiet, and never renders. **The server must send `0x02` `[00]` FIRST**,
before any other world packet.

You can confirm it worked: Frida shows the world ctor `0x44a090` fire ("game-world object
CONSTRUCTED"), and the client begins loading the map and sending game packets back.

---

## 6. World-entry packet sequence (the working burst)

After the client's `0x10`, the server sends this sequence (all game-channel, all NexonInc-encrypted, no
trailer), in this order (`Session.HandleArrival`, `Server/Session.cs:1239-1269`; corrected 2026-10-24, this
table used to stop at row 7 and gave the time as a constant `10 32`):

| # | Opcode | Body (decrypted) | Purpose |
|---|---|---|---|
| 1 | `0x02` | `00` | **Enter-world trigger** — builds the world object (§5). Must be first. |
| 2 | `0x1E` | `06 00 00` | Ack / handshake. |
| 3 | `0x20` | `hour(u8) year(u8)` | Time-of-day (shows the day/night icon), read from the world clock (`World.Clock.Time`, `Server/Session.cs:1248`; `SendTime`, `Server/Session.Media.cs:243-244`), the same calendar as the hourly `0x20` broadcast (§11n). |
| 4 | `0x05` | `entityId(u32BE) 00 00 00 02 00 00 00 00` | **Your own entity id.** Binds camera/input to the self. Without it the client stays black and won't move even after the map loads. |
| 5 | `0x15` | enter-map (see §7) | Opens the map's cache file `Maps\TK<mapId>.map` (§10.8). |
| 6 | `0x04` | coords (see §7) | Sets camera scroll. |
| 7 | `0x33` | self appearance (see §7, §8) | Spawns & renders the player (an `0x07` self entry instead while a morph restored at login is active). |
| 8 | `0x06` | the terrain window (27×25, clamped to the map) | Fills the cache from the stream (`PrimeViewport`, §10.7); skipped when `P1998_V495_PUSHMAP=0`. |
| 9 | `0x08` | stats (§9) | The HUD; body byte 46 sets the client's fast-move flag before the first step (§10.1). |
| 10 | `0x1F` | weather (§7.1) | The map's current weather, `0` when the player's Weather setting is off. |
| 11 | `0x19` | sound 412 | The login sound. The map's music (`0x19` too) is only armed here; it goes out on the client's first packet after the arrival (`Server/Session.Media.cs:346-362`). |
| 12 | `0x33` / `0x07` | per entity in view | The world join: other players in view (`0x33`), then mobs and floor items in view (`0x07`), each through the viewport gate (§11b, §11b.1). |
| 13 | `0x0F` / `0x37` | per bag and gear slot | Fills the bag and the equipment window (§11c). |
| 14 | `0x17` | per learned spell | Fills the spell book (§11d). |

Last, a status line goes out only when the player's marriage or engagement is one the partner no longer
names, which ends it there (`SettleVowHalves`).

Ordering notes (what the client is known to depend on; the rest is the server's order, not a known
client requirement):
- `0x02` first: until it arrives the world dispatcher does not exist and every other packet is dropped (§5).
- `0x05` (entity id) **must** precede `0x33`, and the id in `0x33` **must equal** the id in `0x05`,
  or the client won't recognize the entity as "self".
- `0x04` sets the camera scroll and **must** be sent before `0x33` so the spawn's placement check
  passes (see the viewport gotcha in §8/§14).
- `0x06` after `0x15`: the client has mapped the cache file by the time it parses the cell writes (§10.7).

---

## 7. Packet reference

Bodies below are **decrypted** payloads (what you build before encrypting). `u16`/`u32` = big-endian.

### 7.1 Client → server

| Opcode | Name | Body | Notes |
|---|---|---|---|
| `0x62` | Signature | `"baram\0"` | Login channel, first. |
| `0x00` | Version | (plaintext) | Login channel. |
| `0x02` | NameCheck | `nameLen name pwLen pw 00 00 00` | Creation step 1. **Carries name AND password.** |
| `0x04` | CreateAppearance | 5 bytes (see §9) | Creation step 2. |
| `0x03` | Login | `nameLen name pwLen pw 00` | Login channel. Also answered on the game channel: a `0x03` there is re-authenticated and handed back to the same game port (`Session.HandleReLogin`, `Server/Session.cs:696`; §4.2 item 3). |
| `0x26` | Change password | `nameLen name oldLen old newLen new` | Login channel; **no trailing NUL** unlike `0x03`. The dialog is MODAL until the `0x02` status reply — `0x00` changed / `0x03` credentials / `0x05` weak new password, and `0x05` too for a body that does not parse (`LoginServer/LoginSession.cs:368-372`). Counts against the per-IP failed-login budget. See §4.1 item 5. |
| `0x10` | Arrival | `09 "NexonInc." nameLen name <token>` | Game channel, client speaks first. The **body is plaintext**, but the frame keeps the standard envelope with its increment byte, `AA len 10 inc` then this body (§2, §4.2 item 1). |
| `0x0b` | **Exit to select screen** (Alt+X) | `00` (constant) | Game channel. "I left the world." Answer with the `0x03` redirect struct (§4.1) pointed at the **login** port, or the select screen stays bound to the game socket and character creation hangs. RTK `case 0x0B → clif_closeit`. See §4.2. (server→client `0x0b` is a client-side no-op — §13.) |
| `0x32` | Walk step | `dir(u8) stepCounter(u8) X(u16) Y(u16) pad` | Self-walk request (see §10). |
| `0x05` | **Map-data request** (view rect) | `x0(u16BE) y0(u16BE) w(u8) h(u8)` + 4 junk bytes | **The terrain stream request — 4.95 sends this too** (corrected 2026-08-07; this table previously said 4.95 never sent `0x05`). Reply with an `0x06` cell block covering the rect (§13). See §10.7. |
| `0x06` | Walk (long form) | `dir(u8) step(u8) X(u16BE) Y(u16BE)` + the same 4 junk bytes | Sent every few steps instead of `0x32`. Handle identically for movement. **Both** client packets are 10 bytes = a 6-byte payload + 4 trailing bytes; `0x32` is the same walk without them (7 bytes). **Those 4 bytes are UNINITIALIZED STACK, not a checksum** — ignore them (see §10.7). |
| `0x0E` | Chat | `chatType(u8) msgLen(u8) msg` | (see §11). |
| `0x13` | Attack | `13 00` (bare trigger) | Spacebar. (see §11). |
| `0x1d` | Emote | `idx(u8) 00` | The `:` emote wheel. Reply with a `0x1A` action, `type = idx + 11` **in byte arithmetic — two of the sixteen keys wrap**. The client's key handler `0x491560` maps `a`..`l` → 11..22, `m`/`n` → **9**/**10**, `o`/`p` → 23/24, and its sender `0x491810` transmits `action - 11` in one unsigned byte. So `m`/`n` arrive as **`0xFE`/`0xFF`** and only wraparound recovers Respect/Triumph. Do not clamp or range-check `idx`. It is action-budget gated: it pays into the shared per-second action budget and is dropped once that is spent (`Server/Session.cs:739`, `ActionAllowed` `:550-555`). (see §11). |
| `0x43` | Click/inspect entity | `01 entityId(u32) 00` | **Left-click only** — live-confirmed 2026-07-26: right-click in this client is pure client-local walk-to-click (never reaches the server as anything but movement/`0x69` obstruction; see §10). If the id is an **NPC** → open its dialog (`0x30`, §11e); id 0/self → own click-profile `0x34` (§9.5); a real **mob** → name-only mini-text reply (deliberate divergence from stock RTK's GM-only `onLook`, see §11e); another connected **player** → that player's `0x34` (`Server/Session.Dialog.cs:352-353`). Two sentinel ids are not entities: `0xFFFFFFFE` (F2) toggles subpath chat (§11i) and `0xFFFFFFFF` opens the F1 Central Functions menu (§11k), both checked before any lookup (`:317-318`). |
| `0x3a` | NPC dialog reply | `kind(u8) … step(u8@8) menu/len(u8@10) [text@11]` | Answer to a `0x30` we sent. `kind`: `01` text next/close · `02` menu pick (`@10` = 1-based index) · `04` input (`step@8`==2 = submit, `len@10`, text `@11`). See §11e. |
| `0x2d` | Profile key | `2d 00` (byte 0 = self) | Pressing the profile key. Reply with the self-profile `0x39` (see §9.5). |
| `0x4f` | Change profile | `picSize(u16BE) pic[] blurbLen(u8) blurb[] 00` | Player saved their profile edit, **or** answered a `0x49` (§13a) — byte-identical either way. Persist the picture + blurb; reply with a `0x02` message. `picSize = 0` is the normal case and does **not** mean the packet or the server is broken: the picture is a file the PLAYER supplies, and every client-side failure still replies with 0 (see §9.5a). |
| `0x18` | User-list request | *(empty body)* | "Send me the user list." Preceded by a `0x66` sub-1 town-table request the first time. Reply `0x36` — §13c. The server answers every request with the `0x59` sub-1 town table first, then the `0x36` (`Server/Session.UserList.cs:145-149`). |
| `0x11` | Turn / face | `side(u8) pad` | First press in a new direction turns in place (no step). Echo `Be32(id), side, 00` so the client turns (see §10.4). |
| `0x1b` | Setting toggle | `subCmd(u8) 00` | The whole Options menu — one sub-command per toggle, table below. **Two body bytes** (the fast-move capture `1b 09 00` below; the send primitive in "Edge-trigger" builds `1b <sub> 00`); only the Ride key sends three, `00 01 00`. The server reads `body[0]`, and `body[1]` only for sub `0x00` (Ride or F10), and ignores anything after (`Server/Session.Movement.cs:715`, `:768`). **No state** — but it is *not* a plain flip notification: it is **edge-triggered against a client-stored byte** the server seeds via `0x23`. See "Edge-trigger" below: the server re-seeds after the weather, magic and advice toggles, and deliberately not after fast-move. |
| `0x38` | Hard refresh | `38 00` | Ctrl+R. Grays the screen; reply with the in-place refresh burst `0x15`+`0x04`+`0x33`, the `0x06` terrain window (`PrimeViewport`, unless `P1998_V495_PUSHMAP=0`), then the entities (recenters; `Server/Session.Movement.cs:893-897`). See §10.6. |
| `0x66` | **Examine item** (right-click a bag slot) | `00 cursorY(u8) 00 01 01 SLOT(u8) 01 00 00 00` | **`body[0] == 0`.** The item is in **body[5]**, 1-based, the same slot id a left-click sends as `0x1C`. body[1] is the click's pane-local cursor Y — echo it back as the `0x59` tooltip's Y anchor (§11c.1). |
| `0x66` | **Town-table request** | `01 00 01 01 00 01 01 00` | **`body[0] == 1`** — the same opcode, split on the first byte. Fixed body, sent from `0x449ed0` only when the client's own town table is empty. Reply `0x59` sub-1 — §13c. |

#### `0x1b` — the Options menu

The bit is always `1 << (subCmd - 1)` (RTK `enum settingFLAGS`, `common/mmo.h`); the announce string is
always `"<label>"` padded with spaces to **column 17** then `":ON"`/`":OFF"`, sent as an `0x0A` minitext of
type 3 (`Session.SendMiniText`; corrected 2026-10-22, this line used to say a `0x02` message). The padding is
confirmed by the live capture `02 0f 15 "Fast Move        :OFF"`, taken while the toggles were still announced
as `0x02`: those are the bytes `Session.SendMessage` builds (`0f`, then the length `0x15` = 21). All the toggles
moved to minitext on 2026-08-19 (§11g, the `SendMessage`→`SendMiniText` audit).

| sub | bit | label / behaviour |
|---|---|---|
| `0x00` | — | **'r' Ride key** — but sub-`0x00` is *overloaded*: opening the Options menu (F10) also sends it. Told apart by `dec[1]` (live-captured 2026-08-16): ride = `00 01 00` (`dec[1]=0x01`), F10 options-open = bare `00 00` (`dec[1]=0x00`). Only the ride shape drives `Session.TryRideHorse` (RTK `clif_findmount`: mounts by despawning a nearby "horse" world mob, dismounts by spawning one back in front, §8); the bare `00 00` does not ride: it is when the server seeds the four synced boxes with `0x23` (`Session.SendOptions`, "Edge-trigger" below). Routing F10 into the ride path used to dismount anyone who opened options while mounted. No flag bit. |
| `0x01` | 1 | `Listen to whisper` |
| `0x02` | 2 | `Join a group` — Shift+G. A persisted **profile** status cell (§9.5), so it lives in `Character.Grouped`, not the flag word. Switching it OFF while grouped **leaves the group** — the only leave gesture (§11k) |
| `0x03` | 4 | `Listen to shout` |
| `0x04` | 8 | `Listen to advice` |
| `0x05` | 16 | `Believe in magic` |
| `0x06` | 32 | `Weather change` — also re-sends `0x1F` immediately, and flips the mapinfo render byte (below) |
| `0x07` | 64 | `Realm-centered` — F4 camera lock (§10.5). Session/camera state |
| `0x08` | 128 | `Exchange` — persisted profile status cell (§9.5), `Character.Exchange` |
| `0x09` | 256 | `Fast Move` — the movement model (§10.1). The one synced box the server does not re-seed after a toggle ("Edge-trigger" below) |
| `0x0a` | — | `Clan whisper`. RTK keeps this one *out* of the flag word (`status.clan_chat`) |
| `0x0d` | 4096 | `Hear sounds` |
| `0x0e` | 8192 | `Show Helmet` |
| `0x0f` | 16384 | `Show Necklace` (RTK's own string is misaligned to column 19 — a typo there, not a format) |

> RTK reads the sub-command at `RFIFOB(fd, 6)`; **4.95 puts it at `body[0]`** — confirmed by the capture
> `1b 09 00` for fast-move. Don't copy RTK's offset.

##### Edge-trigger: the four synced boxes and the `0x23` seed

Four Options rows round-trip to the server: **Weather (`0x06`), Magic Effect (`0x05`), Wisdom/advice (`0x04`),
Fast Move (`0x09`)**. These are **radio pairs (ON | OFF)**, and they are *not* symmetric toggles. Disassembly of
`NexusTK_local.exe` (ImageBase `0x400000`, no ASLR):

- **Inbound seed** — the client's options window has a **second packet dispatcher** at `0x4650d0` (sub-opcodes
  `0x21`/`0x23`/`0x25`, told apart by `body[0]`; this is the same "there's another table" lesson as the mail
  button). `0x23` → handler `0x465200`: parses **4 bytes** into a stored array at `[window+0x278..0x27b]` and
  sets the 4 radio widgets (child indices **5, 4, 7, 8**) checked via `0x415160(widget, checked)` where
  **checked = (storedByte == 0)** — and that "checked" flag selects the **OFF (right) radio**, so
  `storedByte == 0` shows **OFF**, `storedByte != 0` shows **ON**. (Live-capture confirmed: a byte-0 seed with the
  feature ON showed the box OFF and still played the effect — the byte tracks the OFF radio, not ON.)
- **Outbound** — send primitive `0x4651a0(sub)` builds exactly **`1b <sub> 00`** and sends **2 bytes** — no
  state. Each row's click handler (jump table in the window command handler `0x464c00`) reads its widget's live
  checked flag `[widget+0x114]`, compares it to its stored byte, and sends **only if they differ**. A click
  **never updates the stored byte** — only an inbound `0x23` does.

| widget idx | stored byte | subcommand sent | option |
|---|---|---|---|
| 5 | `+0x278` | `0x06` | Weather |
| 4 | `+0x279` | `0x05` | Magic Effect |
| 7 | `+0x27a` | `0x04` | Wisdom/advice |
| 8 | `+0x27b` | `0x09` | Fast Move |

**Server contract:** seed all four with `0x23` (`Session.SendOptions`, body `[Box(0x06), Box(0x05), Box(0x04),
Box(0x09)]`, **`Box(sub) = HasSetting(sub) ? 1 : 0`** — 1 = feature ON, because the byte tracks the OFF radio)
when F10 opens the menu (sub-`0x00` with `dec[1]=0x00`, above), **and re-seed after each weather, magic and
advice toggle**. The seed byte must track the server bit, because the client compares
each click against that stored byte alone AND uses it to pick which radio to fill. Two things this gets you:
(1) the box shows the right side, and (2) the server bit stays **in phase** with the on-screen radio, so the bare
`1b <sub>` toggle — which carries no state, the server just XORs its bit — lands the way the user intended. Get the
polarity wrong and every toggle inverts (server ends on the opposite of the clicked radio); get the re-seed wrong
and the stored byte goes stale after the first flip (the return click matches the stale value, the client sends
nothing, the box desyncs — and each row *looks* like it only sends one direction, because only the direction *away
from the last seed* is ever a change). `Session.HandleSetting` re-seeds via `SendOptions()` after the weather,
magic and advice toggles only (`Server/Session.Movement.cs:840`). `0x1b` is **not** action-budget gated, so rapid
clicks aren't dropped server-side; the residual rapid-toggle race is client-side (the async re-seed also rewrites
the widget) and is inherent.

**Fast Move is the exception: no re-seed after `0x09`** (corrected 2026-10-22 to what the server does; this
section used to say the fast-move branch re-seeds too). `HandleSetting`'s `0x09` branch
(`Server/Session.Movement.cs:737-761`) flips `_fastMove`, persists it as `SettingFlags` bit 9, saves, answers with
the minitext and sends no `0x23`, so the Fast Move box is seeded only when F10 opens the menu. On 4.95 the new
value reaches the client in byte 46 of the next `0x08` stats packet (§10.1), not in a `0x23`. Under the model
above, the client's stored byte for that row (`+0x27b`) keeps its value from the last F10 seed; what a second
click on that row does before the menu is reopened has not been checked.

Why the server skips it, and whether a `0x23` seed touches the client's runtime fast-move flag `[state+0x451]` at
all, has two readings. **Unconfirmed:** neither has been re-checked against the binary, and this correction read
no binary.

1. **The code comment** (`Server/Session.Movement.cs:746-750`): the Fast Move box and the runtime flag are the
   same byte, and the `0x23` seed handler writes it with inverted polarity, so a re-seed right after toggling ON
   was seen live to drive `[state+0x451]` back to 0 within about a second. That observation (2026-08-19) came
   before `SendStats` set stats byte 46.
2. **[`Fast-Move.md`](Fast-Move.md)** (written later the same day, with the stats-packet fix): the `0x23` handler
   `0x465200` only paints the checkbox and never writes `[state+0x451]`; what reset the flag was the stats
   packet, whose byte 46 was then always 0.

A third wording, at `Server/Session.cs:1567-1568`, has the re-seed nudge the flag through the options window's
apply path.

#### `0x1F` — weather ⚠ *RTK's byte does not work here*

Server→client, one byte. Handler `0x450f40` does **not** take the value as a state — it **bands** it first:

| `body[0]` | state |
|---|---|
| `< 0x0b` | 0 (clear) |
| `0x0b … 0x63` | 1 |
| `0x64` (100) | *falls through with the raw 100 still in the slot — a client bug. Avoid this value.* |
| `>= 0x65` | 2 |

then applies `0x44d340(state, [world+0x401])` — new state plus the previous one, so it can cross-fade.

**RTK's `WRAIN=1` / `WSNOW=2` are both `< 0x0b`, so sending its byte verbatim pins the client to CLEAR and
weather can never render.** We map 0/1/2 → `0 / 0x32 / 0x96`. RTK also flips the mapinfo render byte
(`clif.c:4600`) from 5 to **4** while the weather toggle is on, which arms the map for drawing — the `0x1F`
state alone is not enough.

### 7.2 Server → client

**`0x05` — self entity id**
```
entityId(u32) 00 00 00 02 00 00 00 00
```

**`0x15` — enter-map**
```
mapId(u16) width(u16) height(u16) flagA flagB nameLen name light(u16, e.g. 232=0x00E8)
```
`flagA` is the render byte: **4 when the player's Weather setting is on, 5 when it is off**
(`Server/Session.Dialog.cs:2225`), and a new character has Weather on (`Shared/Character.cs:210-212`, bit 6),
so most players get 4 (the `0x1F` weather block in §7.1 says why). `flagB` is the realm-centre byte, 0 or 1
(`:2226`; §10.5). Corrected 2026-10-24: this used to give `flagA(=05) flagB(=00)`.

Opens the map's cache file `Maps\TK<mapId>.map`, which the `0x06` stream fills (§10.7, §10.8). Example
working values: mapId 32, 33×33, name "Nexus", light 232.

**`0x04` — coords / camera** (call it `SendXy`)
```
X(u16) Y(u16) scrollAnchorX(u16) scrollAnchorY(u16) 00
```
Handler `0x44faf0`: sets **camera scroll = (X − anchorX, Y − anchorY)** and commits the self's logical
tile. Keep the anchor constant (= the spawn tile) so the camera follows the player and the self stays
put on screen. See §10 and the viewport gotcha in §14.

**`0x33` — create/look (self or any entity)** — full layout, decoded from handler `0x44fef0`:
```
X(u16) Y(u16) dir(u8) entityId(u32) type(u8) <appearance> renderKind(u8) nameLen name
```
- `type = 0` → the **7-byte player appearance** form (parser `0x436120`). This is what players use.
- `type = 1` → a `u16 spriteId + u8` form (parser `0x4361b0`) — the **monster/NPC direct-sprite**
  form, *not* a rich hair/face form.
- `renderKind` (byte right after appearance): **must be 1, 2, or 3**, else the handler bails before
  allocating the sprite (→ invisible). `1` = player sprite. This byte tripped us up badly (see §14).
- The 7 appearance bytes are fully decoded in §8.

**`0x0C` — move/animate an entity**
```
entityId(u32) X(u16) Y(u16) dir(u8)
```
Handler `0x4502c0` → `0x462320`: starts the walk animation toward (X,Y) facing dir. **Animation only** —
does not advance the client's logical tile by itself (see §10). **Overshoot:** the walk ends one tile *past*
(X,Y) in `dir` and commits there, so for a peer/mob (no `0x04` self-commit) you must send the **SOURCE** tile,
not the destination — see the boxed note in §11b.

**`0x0D` — over-head speech**
```
chatType(u8) entityId(u32) msgLen(u8) msg
```
Handler `0x450170`: shows `msg` in a bubble over the entity.

**`0x1A` — action** (attack swing, sit, etc.)
```
entityId(u32) type(u8) time(u16) param(u8)
```
Handler `0x4503a0`: plays the action (client scales `time` ×10; `param` @+8 is the 3rd arg to the
entity's action vtable method `[vtbl+0x78]`). `type`: 0=stand, 1=attack, 2=throw, 3=shot,
4=sit/**pickup** (RTK `clif_parsegetitem` reuses 4 for the pick-up crouch), 5=**drop** (`clif_parsedropitem`),
6=magic, 8=eat. **Emotes are actions too** — `type` 9=respect, 10=triumph, 11=laughter, 12=grief,
13=shame, 14=affection, 15=boredom, 16=sleepiness, 17=surprise, 18=rage, 19=sarcasm, 20=shrug,
21=annoyed, **22=dance**, 23=strange, 24=kiss, 27=charge, 28=attack-after-charge. (See §11 for `0x1d`.)

**`0x13` — combat damage / over-head HP bar** (SOLVED 2026-07-25, decoded from handler `0x4508f0`).
```
entityId(u32BE) critical(u8) percent(u8) hitSound(u8)
```
One packet does three things on the target entity:
- **HP bar** — `percent` (0–100) sets the over-head health bar fill (via `0x44e870`). The client **skips the
  bar if `percent > 100`**, so clamp; `0` = empty (dead). This is the "remaining HP bar above a monster's head."
- **Hit animation** — plays overlay animation `0x8f − critical` (SIGNED) over the entity (queued for `0x78`
  ticks via `0x4622d0` → `0x41b5d0`), i.e. `critical` selects a hit spark. RTK uses `33` (normal) / `255` (crit).
- **Hit sound** — `hitSound` is played through the sound manager if nonzero (RTK appends a `u32` damage tail
  here; the 4.95 client reads only its **high byte** = `0` normally, and ignores everything past `body[7]`).

RTK's `clif_send_mob_health` / `clif_send_pc_healthscript` build the same shape. `critical` is calibratable
live via `P1998_HIT_CRIT`; `@hit <pct> [crit]` auditions the bar + hit anim over the faced mob.

**A heal is a NEGATIVE hit on this same packet.** `clif_send_pc_healthscript` takes a signed `damage` and
does `if (damage < 0) currentvita -= damage`, then builds the identical `0x13`; `addHealthExtend` reaches it
as `clif_send_pc_healthscript(sd, -damage, 0)` (and the mob twin as `clif_send_mob_healthscript(mob, -damage,
0)`). So healing draws the over-head bar exactly like damage does, with **`critical = 0`** — that byte still
selects an overlay animation (`0x8f − 0`), and 0 is simply what the reference server passes. Ours is
`P1998_HEAL_CRIT` if the 4.95 client turns out to draw something unwanted for that id.

**Death beat:**
4.95 monsters have **no** death frame-set (`monsfrm.tbl` defines only walk/attack), so a "death animation" is:
send `percent = 0` (empty bar + final hit spark), then delay the `0x0E` despawn (`P1998_DEATH_DELAY_MS`, default
600 ms) so the corpse doesn't pop out instantly. Players die to **ghost form** (appearance `[1]=1`, §8) instead.

**`0x19` — background music.**
```
type(u8) pad(u8=0) bgm(u16BE) volume(u8)
```
Handler `0x450ad0` (dispatch-table stub `0x44bb06` → real handler): `body[1] = type` selects the audio
backend — **2 = MIDI** (the stock `1.mid`..`12.mid` in `NexusTK.snd`, played via the single-instance MIDI
player `[0x4fd3ac]`), **1 = MP3**, **0 = a positional wav/sfx**. `bgm 0` stops the music.

**The two channels need DIFFERENT bodies** — they leave the handler at different points, and getting this
wrong is silent, not an error (live-verified 2026-08-13):

| type | exits at | body |
|---|---|---|
| 2 (midi) | inline `0x450b1b`, **returns `0x450bab` before the tail** | the 6-byte header above is complete |
| 1 (mp3) | falls **through** to the TLV tail `0x450c48` | needs the full TLV, exactly like type 0 (§7.3) |
| 0 (sfx) | falls **through** to the TLV tail `0x450c48` | full TLV |

Send type 1 the short 6-byte body and the tail reads past the end of the buffer, builds a sound object with
a garbage **mode**, and the play wrapper drops it — no sound, no log, no error. The working type-1 wire is
the type-0 layout with `body[1]=1`:
```
19 | 01(type=mp3) | 03(P0) | bgm(u16BE) | 64(vol) | 03 00 01 | 00 00 00 00
```
(`tagA=3, B0=0, C=1` → object mode 1 → play wrapper `0x463ab0` → `0x463ae8` → play fn
`0x4798c0(bgm, type, gain, 0)`; `type==1` there is the MP3 branch at `0x479c29`.)

**MP3 (type 1) is the EXPANDABLE channel.** `0x479c29` does `sprintf(buf, L"%03d.MP3", bgm)` — the **wide**
string at `0x4f3cc0`, which is why an ASCII strings scan of the exe finds nothing — then opens it through
the **XAudio** MPEG-1/2 layer I/II/III decoder that 4.95 links statically (`XA_MSG_*`,
`"MPEG %d, %s, %s, %d kbps, %d hz"`): `0x478e20` → XAudio cmd 8 `INPUT_OPEN` + cmd 1 `PLAY`. The file is a
**loose `NNN.MP3` in the client directory**, not an archive entry; `%03d` is a minimum width, so ids above
999 simply print more digits. There is **no id cap** — the only guard is `bgm > 0`.

By contrast **MIDI (type 2) is hard-capped at ids 1..12** (`cmp si, 0xd / jge bail`, `0x4588b4`), which is
why the stock game has exactly 12 songs and why you cannot add a 13th without patching the exe.

Caveat: the mode-1 branch passes a hardcoded **0 as the loop flag**, so an mp3 sent this way plays once and
stops. Other modes reach branches that push 1 (`0x463b49`, `0x463bc8`, gated on the object's `[+0x154]`), so
looping is a packet-body change rather than a client patch — not yet calibrated.

`re/extract_mus.py` lifts the 25 tracks out of the **5.33** client's `Mus000.dat` (same container as
`NexusTK.snd`: `u32 count`, then `count × {u32 offset, char name[13]}`) and renumbers its `%08d` names onto
this `%03d` scheme — every id in it happens to be ≤ 999, so they map with no collisions. 5.33 additionally
supports `%08d.LST`/`%08d.LSR` playlists; **4.95 has neither string** and can only be told one track at a time.
That asymmetry, plus the fact that stock 4.95 ships none of the files, is why the second soundtrack is
offered to 5.33 sessions only (`@music new`, `Character.NewMusic`). Note the two clients do not even share
an audio engine — 4.95 is DirectSound + its statically linked XAudio decoder, where the loop flag above is
literally "loop or not", while 5.33 is **Miles (`mss32.dll`)**, where the same field is a loop *count* and
**0 means repeat forever**. Its type-1 body is a different shape too, and its playlists advance themselves
on a window message: **docs/5.x/Wire-Divergences.md §6.8**.

**Install gotcha.** The 4.95 client is **file-virtualized** by Windows: its own writes (`Maps\TK*.map`,
`ddraw.ini`, `users\`) go to `%LOCALAPPDATA%\VirtualStore\Program Files (x86)\Nexon\NextAeon\`. Drop the
`.MP3` files in **both** that directory and the real install dir.
`volume` is a raw byte the client **log-scales**: the handler computes `dB = 2000·log10(vol/100)` (so
`vol=100` = 0 dB = nominal full, `vol>0` audible, `vol=0` silent), and the MIDI path then compresses it
further against a base at `[snd+0x270]` — so the audible range is narrow and `>100` (up to 255) is the knob
to push louder. The client dedups (`cmp bgm,[midi+8]`) so re-sending the playing track is a no-op. Songs are
numbered `N.mid`; some tracks may reference instrument samples a given install lacks, but that only manifests
at high volume.

There is no original map→track table in the client files, so the assignments are the server's own, and they
are made **per area, not per map** (`Content.BgmFor` over `MapBgm.csv`; the song *names* — `mist`, `tiger`,
`dragon`… — are ours too, in `MusicTracks.csv`). Only the areas are listed: every other map inherits its
nearest listed map's track through the `Warps.csv` graph, so a shop or a cave plays its area's theme and the
track simply doesn't change on the way in. The few maps with no warp path to any area keep whatever is
playing.

**Timing gotcha (login).** A MIDI sent inside the world-entry burst is **dropped**: at that point the client
has only just built its world object (the `0x02.00` trigger) and hasn't opened `Maps\TK<n>.map` yet. The
`0x19` *type-0* login sfx sent two packets later in the same burst **is** audible, so this is the MIDI
channel waking late, not the opcode being rejected. Hold the track and send it on the first packet the client
sends back — its own `0x05` view request, ~125 ms after entry in the wire logs (`Session.ArmEntryMusic` /
`StartEntryMusicIfArmed`).

**`0x19` type 0 — spell sound-effect (SOLVED 2026-07-25, live-verified).** The type-0 path is **not** a flat
"play sound N" — it's a positional-sound TLV parser, and RTK's `clif_playsound` (a later-client `type 3`
layout) mis-parses on 4.95 into a garbage sound object (mode 4, random id) → silent. The real 4.95 wire, cracked
by RE (`re/frida_sound.py`; hooks the `0x19` handler → TLV tail `0x450c48` → spatial builder `0x44e6c0` → ctor
`0x463950` → play wrapper `0x463ab0` → play fn `0x4798c0`):
```
19 | 00(type=sfx) | 03(P0) | soundId(u16BE) | 64(vol) | 03 00 01 | 00 00 00 00
```
`type 0` reads `soundId@body[3..4]` + `volume@body[5]`, then runs the TLV tail starting at `body[P0]+3` (P0=3
puts it just past the 5-byte header). The tail's parsed **`C` field → the sound object's MODE** (`[obj+0x148]`);
the play wrapper dispatches modes 0–4 and **only mode 1 reaches the audio player** — so the tail bytes
`03(tagA) 00(B0) 01(C=mode 1)` with all skip bytes `0` are what make it play. `soundId`/`type`/`gain` reach the
play fn via the "type-group" (`[ebp-0x1c/18/14]`, only populated by the type-0/1/2 prologue — `type 3` leaves
them garbage, the other reason RTK's format failed). `volume` is log-scaled: `dB = 2000·log10(vol·0.01)` (const
`@0x4cc408 = 0.0`, so `vol>0` audible; `100` = 0 dB). `Session.SendSound`/`SoundAt`; `BroadcastFx` sends the
cast's effect (`0x29`) + this sound (`0x19`) to the whole map. **NOT** the `0x1A` action 4th byte: the client
picks an action's sound from a fixed *action-type → sound* table (emote 22→311, sit 4→406), and **magic type 6
→ sound 0 → silent** no matter what byte we send (proven: `byte8=0` even on the audible emotes). Effect
animation and sound are **separate** — the `0x29` effect ctor chain makes no play call, so sound rides its own
`0x19`. Per-spell sound + graphic come from the spell's own `spell_effects.csv` row (`Content.EffectSound` /
`EffectAnim` are now plain column reads — see **Spell FX are data, not a ladder** below). Sound files are `NexusTK.snd` (a PAK of `NNN.wav` + `N.mid`, non-contiguous, ids up to ~720);
`re/extract_snd.py` dumps them. **Best-effort only** — RTK's 6.x/7.x sound numbering doesn't perfectly match
4.95's, and fan sites carry no sound data. Debug: `@snd <id>` auditions raw client sound ids live.

**Calibrated sound ids (by ear, live 2026-08-04).** RTK's numbers live in a *later client's* sound space, so
every id below was identified in-game against the real 4.95 `NexusTK.snd` and now overrides whatever RTK said.
Everything not listed still runs on the ported RTK number and is a candidate for the same treatment.

| Event | Id | Where |
| --- | --- | --- |
| Unarmed swing (hit or miss) | 009 | `Session._fistSfx` |
| Weapon swing — blades | 331 | `Session.EquippedWeaponSound` |
| Weapon swing — staves/wands | 335 | `Session.EquippedWeaponSound` |
| Player swing that **lands** | 349 | `Session._hitSfx` → `PlayHitSfx`, layered on the swing sfx |
| Mob swing (hit or miss) | 009 | `Session.MobSwingSfx`, fired from `World.Tick` |
| Mob swing that **lands** | 001 | `Session.MobHitSfx`, fired from `Session.ApplyMobHit` |
| Equip armor onto a bare body | 410 | `Session.GearSfx` (wire slot 2) |
| Swap armor / take armor off | 411 | `Session.GearSfx` |
| Draw or swap a weapon | 411 | `Session.GearSfx` (wire slot 1) |
| Put a weapon away | 410 | `Session.GearSfx` |
| Helms, rings | *silent* | `Session.GearSfx` — confirmed to make no sound |
| Eat / use a consumable | 403 **+** 006 together | `Session.PlayEatSfx`, from `ItemEatAnim` |
| Thunder Bolt / Spark / Singe (RTK sound 55) | 701 | `spell_effects.csv` `sound` column |
| Soothe, Gateway | 708 | `spell_effects.csv` / `Session.LuaGateway` |
| Successful login | 412 | `Session.cs` arrival burst |

The swing/hit split is two separate sends: the swing sfx goes out on **every** attempt, the hit sfx **only**
when the swing connects, and they stack. Note the player hit sound rides its own `0x19` rather than the `0x13`
`hitSound` byte — that byte is real but 8 bits wide, and 349 doesn't fit.

**Spell FX are data, not a ladder (2026-08-05).** Every spell's graphic + sound now come from the `animation`
and `sound` columns of its own `spell_effects.csv` row. `Content.EffectAnim`/`EffectSound` are one-line column
reads; there is no `pcalign` switch left in C#. To retune a spell: edit its cell, `@reload`, done.

RTK stored this as `pcalign` — the 5th argument each spell passes to `global_zap`/`global_attack`/`global_heal`,
flattening (visual family × alignment) into one int, with a `+100`/`+200` shift for Warrior/Rogue. Only the
Damage and Heal archetypes used it; the other eight already carried explicit columns. `re/fill_spell_fx.py`
resolved it once into those columns, so `pcalign` is now provenance only and never read at runtime.

Resolving it surfaced that **all three available sources of a spell's alignment contain errors**, so the
script cross-checks them and reports every disagreement rather than silently picking one:

| Source | Errors found |
| --- | --- |
| `pcalign` (RTK Lua) | 8 — the whole poet `vital_spark` family passes `0`; `call_lightning`'s mingken+ohaeng members pass `0`; `lethal_strike`/`whirlwind` pass an aligned constant despite being unaligned |
| `SplAlignment` (`Spells.csv`) | 2 — the `recover_rogue` family is tagged `0,1,1,2` (duplicate kwisin, no ohaeng) |
| **position within the Lua file** | 0 — RTK writes a family as N tables in one file, ordered unaligned, kwisin, mingken, ohaeng |

Position-in-file wins, being the only structural signal rather than a hand-typed value; it was corroborated by
at least one other source in all 10 disagreements, and by the official manual's four parallel `… secrets`
spell lists. The pass also deduped 4 rows that a scratch copy of `rogue/singe.lua` had produced, normalized
Singe so it renders the same for rogue as for mage, and filled two cells the extractor had missed because the
Lua passed a local variable (`slash_warrior` sound 60, and the `spellFX` spells). Four spells
(`rogue/drain.lua`) are left with a blank sound on purpose — that file contains no `playSound` at all.

**The animation id space, decoded from the client (2026-08-05).** `Effect.epf`/`.tbl`/`.frm`/`.pal` extract out
of `NexusTK.dat` (`re/pak_extract.py`), and `re/render_effects.py` renders every animation to a contact sheet
or gif. The table declares **`NumEffects 128` — ids 0–127**, and holds 128 `ID` lines, ids 0–127 in order
(re-counted from the 4.95 install 2026-10-24; this used to say `NumEffects 121`, ids 0–120). One of the 128
has no frames.

| File | Layout |
| --- | --- |
| `Effect.tbl` | text; per effect an `ID n, NumFrontFrames a, …, NumBackFrames b` line then `a+b` `Frame# N, Delay ms, …` lines. **`Frame#` is the absolute `Effect.epf` frame index** — no cumulative sum. The first `a` draw over the sprite, the last `b` behind it. |
| `Effect.frm` | `u32` count (1611 = epf frame count) then one `u32` **per frame** = its palette index (0–10) |
| `Effect.pal` | `u32` count (13) then 13 `DLPalette` blocks. A block is not a fixed 1056 bytes: its header is `32 + 2·C` bytes, where `C`, the animation-entry count, is the **u32 at +24**, and the 256 RGBA entries follow the header. Twelve of Effect.pal's blocks have `C = 0` (entries at +32); one has `C = 1` (a 34-byte header, entries at +34). |
| `Effect.epf` | `u16` frameCount, `u16` w, `u16` h, `u16` pad, `u32` tocOff. **TOC lives at `12+tocOff`** (relative to the header, not absolute). Each 16-byte entry is `top,left,bottom,right (i16×4)` then `pixOff,stencilOff (u32×2)` — 4 shorts then 2 ints. Verified: all 1611 frames satisfy `stencilOff-pixOff == w*h` (1,611 re-counted from the install 2026-10-24; this table used to say 1432). Box coords are signed and usually negative — they're offsets from the cast tile, so the effect centres on its target. |

**`.pal` files come in two forms.** Twelve of the install's 36 `.pal` files (Effect.pal among them) are a u32
block count followed by that many blocks; 23 are a single `DLPalette` block with **no** count prefix (the
remaining one holds no `DLPalette` block at all). Across all 188 blocks the header length is `32 + 2·(u32@+24)`
without exception, and it takes the values 32, 34, 36, 38 and 40.

Two decoding traps here, both of which produce plausible-but-wrong output rather than an error: the colour
table follows the header (**+32** for a block with no animation entries), not the fixed `+38` that
`re/render_items.py` uses (on an Effect.pal block at +38 every channel shears and everything renders
magenta/blue), and the TOC field is **relative to the 12-byte header**.

**Wire id = table index + 1.** The client loads `Effect.tbl` 1-based, so `0x29` byte *N* draws table entry
*N−1* (`Session.SendEffect`, `EfxWireOffset = 0`, live-proven). So the client accepts an `animation` value of
1–128; wire 73 (table entry 72) is the one with no frames. (The history table below speaks of a "1–121 range";
it records the 2026-08-05 pass as it ran, under the old count.)

**Animations identified against nexusatlas (2026-08-05).** The 2002-03 nexusatlas spell pages carry a capture
of each spell's animation as a GIF; `re/scrape_atlas_spellfx.py` pulls them and `re/match_spell_fx.py` +
`re/template_match_fx.py` match them to client effects. The GIFs are 1:1 captures of the client's own sprites,
so **template-matching at native scale** (slide the client frame over the atlas frame, take the best
normalised correlation) settles them exactly — many hit 1.000. The bbox-normalising scorer alone is not
enough: it stretches each animation to its own bounding box and goes flat on small ones. The result table is
`re/fx/animation_map.csv` (gif → effect id → wire id) and `re/fx/spell_animation_join.csv` (per spell).

This confirmed the bulk of the existing `animation` values, including the one the code had explicitly flagged
as unverified — `Session.SendEffect` warned the aligned heals "may not line up" with RTK's numbering. They do:
`ohaengheal`→#62→63, `mingkenheal`→#63→64, `kwisinheal`→#64→65, matching the CSV exactly.

**Every conflict was then rendered side by side and settled by eye** (`re/triage_anim.py`,
`re/write_spell_anim.py`, sheets in `re/fx/review/`). 144 edits across four passes. The atlas won nearly every conflict,
and several were systematic rather than one-offs:

| Fix | Rows |
| --- | --- |
| Shapeshift/summon family was `12` (a dart); the real animation is `3` | 23 |
| The melee-skill ids were ROTATED: Berserk 9→6, Desperate Attack 6→7, Whirlwind 7→9 | 3 |
| `59` and `98` were SWAPPED: Augmentation/Magic Shield→98, Elemental Armor→59 | 8 |
| Inner Blessing/Temper used Might's `11`; they are the "new might" `117` | 4 |
| The drain family (Absorb/Drink of Souls/Parasite) used Heal's `5`; correct is `84` | 3 |
| `dark_veil` 136 and `winters_shadow` 123 were outside the client's 1–121 range | 2 |
| `lockup` pointed at `120`, one of the client's EMPTY effect slots | 1 |
| Spells the atlas marks `none.gif`, now recorded as `-1` = confirmed silent | 67 |

Three cases went the other way, which is why this is a decision table and not a blanket overwrite: Spark/Singe
keep `28` (the atlas illustrates Thunder Bolt, Spark and Singe with a single Thunder Bolt capture that matches
`27` exactly, and `thunder_bolt` already is 27 — so Spark/Singe use a genuinely different sibling bolt), and
Fissure/Lava Surge keep `41`/`42` (the atlas tags both with `confuse.gif`, a tiny figure, but 41/42 are lava
eruptions — an atlas mislabel). A similarity heuristic auto-kept three more as "sibling variants" that turned
out to be wrong on inspection: it is only good for shortlisting, never for deciding.

**The trap ladder comes from tswolf, not nexusatlas.** nexusatlas collapses Set Trap's whole ladder into two
rungs, but [tswolf's 2001 rogue page](https://web.archive.org/web/20010724080903/http://www.tswolf.com/spells/rogue.shtml)
has a `Graphic / Trap / Level / Mana Cost` table listing every rung with its own image, each `<img>` immediately
followed by its own label. tswolf serves the *same image files* — all seven md5s are byte-identical to the
nexusatlas copies — so the gif→effect mapping carries straight over:

| Trap | Lvl | gif | id |   | Trap | Lvl | gif | id |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Dart trap | 26 | `dart` | 12 | | Spear Trap | 70 | `berserk` | 6 |
| Flash trap | 35 | `might` | 11 | | Poison dart | 77 | `vex` | 1 |
| Repeating dart | 44 | `dart` | 12 | | Death Trap | 88 | `deathtrap` | 44 |
| Snare | 63 | `vex` | 1 | | Sleep Trap | 98 | `sanc` | 2 |

Bladestorm trap reuses Lethal Strike's animation (9). Its other class pages carry no ladder tables, so they add
nothing beyond nexusatlas.

Coverage now: **498 spells carry a real id, 67 are confirmed to have NO animation (`-1`), 58 remain unknown** —
of which 19 are GM/internal commands. The rest are poet pet-summons, rogue utility skills and three late trap
variants (`cutting_edge`, `swords_dance`, `tigers_ambush`) that neither fan site documents.

Note the column split: `-1` in `animation` means "no graphic", and says nothing about `sound` — the two are
independent, and a spell with no animation can still play a sound.

Caveats worth keeping: the atlas reuses **one GIF per visual family** (10 mage spells share `ignite.gif`), so
it resolves a spell's family, not always its exact id — where the client holds near-identical siblings (the
bolts #26/#27/#29) the atlas cannot say which a given spell uses. `ls.gif` and `ww.gif` are byte-identical, so
Lethal Strike and Whirlwind are indistinguishable from this source. `kwisinarmor.gif` was never archived. The
rank spell pages (`spells/ilsan.php`/`eesan.php`/`samsan.php`) 404 in their 2002-12-30 and early-2003 captures
(`samsan` until 2004-04-23), but not in every capture: Wayback holds them answering 200 from 2003-06-09
(`ilsan`), 2003-08-22 (`eesan`) and 2004-08-09 (`samsan`). Nothing in this repo has read those later captures
yet, so the rank spells have no atlas animation.
**Use the 2002-03 captures, not the 2018 ones** — the later site serves `/photo/spells60/`, a 6.0-era art set
that would silently poison a 4.95 match.

**Missing-entity policy (2026-07-25): no fallbacks.** When an effect/sound id doesn't exist in the 4.95 client
(`Effect.tbl` ids run 0–127, i.e. wire 1–128, so RTK anims above that like `dark_veil`'s 136 are absent; a few RTK sounds
have no matching `.wav`), we send it as-is and let the client show/play **nothing**, rather than substituting a
stand-in — so a broken entry is visibly empty and easy to spot. `@efx <id>` auditions raw `Effect.tbl` ids
(0–127) live to help identify the real 4.95 effect for a spell whose RTK id doesn't line up (a version-gap
asset task deferred to when the correct 4.95 effect assets are sourced).

**`0x33` type-1 form** (parser `0x4361b0`): `… type(=01) app0 app1 spriteId(u16) extra(u8) renderKind nameLen name`
(5 appearance bytes; `app0`/`app1` are clobbered by the `u16` at +2). **⚠ This does NOT draw creatures.**
The `0x33` sprite ctor (`0x463380`, *any* renderKind 1/2/3) always builds from the **player sprite archive
`0x4f2a84`** — so `0x33` can only render players and human-looking NPCs. Sweeping type-1 sprite ids
`1..140` all rendered *invisible* (a broken player compose). `0x33` is **not** a monster path either —
the real monster spawn is **`0x07`** (see above / §11a).

**`0x16` — ground ITEM / object spawn** (handler `0x450a00` → `0x44dbc0` → ctor `0x463020` → `0x462ec0`).
> **⚠ NOT a monster.** Originally mislabeled "creature spawn" — live RE proved otherwise (see §11a). The
> object it creates draws its sprite from category **`"I"` = Item.epf** (via `0x435ab0`, id+`0x4000`), has no
> collision and no AI. For real monsters (drawn from **`"M"` = Monster.epf**) use **`0x07`** instead (above).

Layout (offsets from opcode byte; multi-byte big-endian), verified from the handler + ctor:
```
+1 owner(u32=0)  +5 GRAPHIC(u16)  +7 entityId(u32)  +0xb X(u16) +0xd Y(u16)
+0xf X'(u16) +0x11 Y'(u16)  +0x13 flags(u32=0)  +0x17 dir(u8)
```
The `u16` at **+5 is the item graphic id** (a frame index into Item.epf; the client adds `0x4000` and looks
it up in category `"I"`), stored at sprite+`0x130` by `0x462ec0`; `+7` is the entity id (find/despawn key).
`(X,Y)` = the resting tile (entity+`0x10c/+0x110`); `(X',Y')` = the "walked-from" tile. **`(X',Y')` MUST
differ from `(X,Y)`** — the ctor computes `[obj+0x148] = |X-X'|+|Y-Y'|` and the per-frame position code
`idiv`s by it, so `(X',Y')==(X,Y)` → divide-by-zero → **client crash** (send the from-tile 1 step away).
No name field, no viewport gate (`0x44dbc0` skips the `0x424310` check), so the object can be placed anywhere.

**`0x07` — creature / monster list** ✅ **THE REAL MONSTER SPAWN** (handler `0x44fdb0`). Confirmed live:
draws real animated creatures from **`"M"` = Monster.epf**. Layout (offsets from opcode byte; big-endian):
```
+1 count(u16)   then `count` × 12-byte entries:
  +0 X(u16)  +2 Y(u16)  +4 entityId(u32)  +8 look(u16)  +0xa color(u8)  +0xb dir(u8)
```
The handler loops `count`, building each entity with the **same factory `0x44d7d0` that `0x33` (players)
uses** — the difference is the `look` field. `0x44d7d0` classifies the look descriptor:
- **`look ∈ [0x8000, 0xbfff]` → descriptor type 1 → direct creature sprite.** Entity ctor `0x461a50`
  (entity vtable `0x4cd098`), whose draw `0x461c70` sees `[ent+0x178]!=0` and routes to the **monster
  resolver `0x434020`/`0x4342e0`** → pushes `MONSTER.EPF` (`0x4f1d18`) → resolves the frame via `0x433d00`
  (Monster.tbl). **So `look = 0x8000 | monsterId`, where `monsterId` is the Monster.tbl index (0..326).**
- `look < 0x8000` or `> 0xbfff` → descriptor type 2 → `0x462ec0` (vtable `0x4cd118`) = the **static item/object
  base** (tick `0x4601a0` = no-op). Don't use for monsters — but this **is** the correct path for
  **ground items at rest** (it renders statically from Item.epf and never despawns, unlike the `0x16` walk
  projectile; see §11c). `IconWire(N)` yields a `look` in this range (`0xc000+`).

`color` = palette (→ resolver), `dir` = facing/state (→ ent+`0x18d`). **There IS a viewport gate** here
(`0x424310`, unlike `0x16`): entries outside the camera rect are silently skipped, so spawn inside view.
Verified live: `look 0x8000`→Monster.tbl frame 6, `0x8001`→26, `0x8002`→46 … i.e. **frame = 6 + 20·monsterId**
(the idle "Starting" frame per Monster.tbl). Combat (melee/spell → `0x13` HP bar + hit anim → death beat →
`0x0E` despawn, see §7.2 `0x13`) works against these because they're real entities with collision. This is the `0x33`-monster path from Mithia 7.x
(`clif_cmoblook_sub`: `look + 0x8000`) — it moved to `0x07` in 4.95 (see §17). Builder: `Session.SendCreatureList`.

**`0x0E` — despawn list** (handler `0x450440`):
```
count(u8) entityId0(u32) entityId1(u32) …
```
Destroys each entity by id (`0x44d9f0`). The loop **stops early on a `0` id**, so never send id `0`.
(Note: client→server `0x0E` is *chat*; the same opcode means despawn only in the server→client direction.)

**`0x29` — effect animation over an entity** (handler `0x4504b0` → `0x44e0a0`). **NOT a floating damage number**
— NexusTK has no floating combat numbers, and the disassembly proves it:
```
entityId(u32) effectId(u8, 1-based) A(u16) B(u16) C(u16)
```
The `u8` is a **1-based index into the client's effect table** (`Effect.tbl`, 128 effects, drawn from `Effect.epf`).
Trace: `0x4504b0` reads the fields, resolves the entity, calls `0x44e0a0`; that calls constructor `0x4354b0`,
which does `idx = u8 − 1; if (idx < 0 || idx ≥ count) bail; entry = table[idx]` and copies the 36-byte effect
template (9 dwords). A number renderer would `itoa` the value — this indexes a table, so it's a graphic. `u8 = 0`
⇒ no effect. `A` is scaled ×1000 = the vertical pop offset; `B`/`C` are style. Send `A=B=C=0` to center it.
**The wire `u8` maps directly to RTK's `sendAnimation(N)` id** (`P1998_EFX_WIRE_OFFSET = 0`) — proven live: Ion
(`pcalign 0` → anim 4 = unaligned zap) sent with a `+1` offset drew the anim-5 graphic (unaligned heal), so
`u8 = N` draws the anim-N effect (the handler's internal `−1` is cancelled by the table being loaded 1-based).
The effect-id + sound-id per spell come from RTK's `global_zap`/`global_heal` `pcalign` ladder (ported to
`Content.ZapEffect`/`HealEffect`; e.g. spark = 28, unaligned zap = 4, unaligned heal = 5, kwi-sin heal = 65).
Most RTK `sendAnimation(N)` ids fall in 0–127 and line up with `Effect.tbl` (128 effects), and low ids are
confirmed identity (unaligned heal 5, spark 28). But the id spaces are **not** a perfect match: RTK's 6.x/7.x
client has more/renumbered effects, so some ids land on the wrong effect (aligned-heal range) or are out of
range entirely (`dark_veil` 136, plus a handful of GM spells) → the 4.95 client draws nothing. Per the
no-fallback policy above, these stay empty until the correct 4.95 effect assets are sourced. `@efx <id>`
auditions raw ids to calibrate.

> **History:** this opcode was previously documented (and used) as a "floating number" — sending the damage
> value as the `u8`, which the client read as effect `#(dmg−1)`, an unintended graphic. Corrected 2026-07-25 by
> disassembling the handler after the absence of in-game damage numbers was questioned. The old melee `SendNumber`
> hack is now **gone**: hits send the `0x13` combat packet (over-head HP bar + hit spark, §7.2); spell casts send
> real `Effect.tbl` ids here for the cast graphic (§11d).

**`0x1E` / `0x20`** — acks / time, as in §6.

---

## 8. The appearance system

The **`0x33` type-0 appearance is 7 bytes.** Their meaning was decoded empirically with an in-game
"look-lab" (spawn dummies with controlled appearance bytes and observe). **This is the definitive
layout:**

| Byte | Meaning | Notes |
|---|---|---|
| `[0]` | **Body / sex** | `0` = male, `1+` = female. |
| `[1]` | **Form / state** | `0`/`4` = normal human, `1` = ghost/dead, `3` = **mounted (horse)**, `5` = invisible-spell (faded), `2` and `6`+ = **no sprite (blank)** (`0x432364`). Driven by `Character.Mounted` via `Session.MountForm()`; toggled with `@ride`/`@mount`, re-drawn on self (`SendSelfLook`) **and** peers (`ShowPlayer` carries `Mounted` in `PlayerSnapshot`). There is no separate horse sprite: the mounted poses of the worn body art (`Body.epf` pose numbers 80–91) are horse and rider in one frame, with the head drawn on top (§8.2). |
| `[2]` | **Face / head** | **Exactly 90 heads, ids `0..89`** — read off the client's own asset table, not guessed: `NexusTK.dat` → `Head.tbl` is plain text beginning `NumFaces 90`, then `ID n, Palette 0, Starting n*100` for every `n` in 0..89, and `Head.epf` holds the matching 9000 frames (100 per head, one per pose number of §8.2; pose 6 is the front view). All 90 were rendered and eyeballed — every one is a complete player head, hairstyle included. **Anything ≥ 90 draws no head at all** (headless character, no error). Creation byte `[0]` feeds this directly and every observed sample (`00`,`12`,`23`,`29`,`32`,`34`,`3d`,`55`) sits in range. `Session.FaceLook()` clamps on send so stale out-of-range saved data can't leave a character headless. |
| `[3]` | **Armor / coat** | Class armors (rogue/mage/warrior…). |
| `[4]` | **Body-layer colour (ramp shift)** | Recolours the worn armor/coat. **Not a palette index — a ramp selector:** the tinted blit does `if (pixel >= 0x30) pixel += colour * 8` before the palette lookup, so garment colours live in 8-entry ramps from index `0x30` and this byte picks the ramp (skin/outline indices below `0x30` are untouched). See the ⚠ below. Driven by `Session.ArmorDye()` = the worn armor's `ItmLookColor` **when that is a real ramp (0..9)**, overridden by `Character.ArmorColor` (Arena Master war paint, §11e) when set. See the second ⚠ below. |
| `[5]` | **Weapon** | **FAMILY-RANGED, not a flat id — see the ⚠ below.** `0x00..0x7F` = Sword.epf art, `0x80..0xBF` = Spear.epf art−0x80, `0xC0..0xDF` = Bow.epf (no 4.95 art — empty hand), `0xE0..0xFE` = Fan.epf art−0xE0. **`0` is a REAL weapon sprite — "no weapon" is `0xFF` (`-1`).** |
| `[6]` | **Shield** | Direct Shield.epf art id, 13 arts (`0..12`). **`0` is a REAL shield — "no shield" is `0xFF` (`-1`).** |

> **⚠ Byte `[4]` is a RAMP SHIFT on the body layer, and it must carry the armor's own `ItmLookColor`
> (RE'd end-to-end 2026-08-07).** The real player draw is **`0x432320`** (reached from the entity draw at
> `0x462999`/`0x462a2e` → `call 0x432320`). It resolves the four layer frames via `0x433040` (which layers,
> in what order, and how each frame is computed: §8.2), fetches each
> layer's palette from its table record (`rec+4`) through `0x467470(kind, palIdx)`, and **remembers which
> loop index is the body layer in `[ebp+0x28]`**. At blit time:
> ```
> 0x004328de  cmp  ecx, eax            ; this layer == the body layer?
> 0x004328e0  jne  0x432907            ;   no  -> plain blit, colour 0   ([obj+0x94])
> 0x004328e2  mov  eax, [ebp + 0x19]   ; appearance[4]
> 0x004328e5  test al, al
> 0x004328e7  je   0x432907            ;   zero -> plain blit
> 0x004328ff  call dword ptr [ebx+0x98]  ; TINTED blit, colour passed through
> ```
> The tinted blit is `0x428c10` (16-bit) / `0x42cb40`, and the recolour is one instruction:
> ```
> 0x00428d56  movsx eax, byte [ebp+0x18] ; colour
> 0x00428d5a  shl   eax, 3               ; colour * 8
> ...
> 0x00428e1a  mov  al, byte [esi]        ; source pixel = palette index
> 0x00428e1c  cmp  al, 0x30
> 0x00428e1e  jb   0x428e23              ; < 0x30 (skin/outline) -> untouched
> 0x00428e20  add  al, byte [ebp-0x14]   ; index += colour * 8
> 0x00428e2f  mov  ax, word [ebx+eax*2+0x2c]   ; palette lookup
> ```
> So each garment's colours are an 8-entry ramp based at `0x30` and the byte selects the ramp — which is why
> all 67 `Body.epf` sprites look green: **green is merely the base ramp**, and every seasonal colour is the
> same art shifted. Rendering body 4 with the shift at `ItmLookColor` 0..9 reproduces the ten armor icons
> exactly (green / teal / blue / cream / salmon / gold / pale-pink=earth / magenta / pale-blue / copper).
>
> **The bug this caused:** the server only ever put `Character.ArmorColor` (the war-paint dye) in this byte,
> so undyed armor always rendered at ramp 0 — the doll was "spring" no matter what the bag icon showed,
> while the icon looked right because RTK gives those rows their own `ItmIcon`. Fixed by `Session.ArmorDye()`
> (worn armor's `ItmLookColor`, war paint overriding). Note `0x4329b0` is a **different, simplified** draw
> path with no palette/tint step — do not use it to reason about colour; the earlier revision of this doc did
> exactly that and wrongly concluded the byte was dead.

> **⚠ The ramp is resolved against THE BODY SPRITE'S OWN PALETTE, so one number is different colours on
> different armor (RE'd 2026-08-09).** `Body.tbl` assigns every body a palette and **only bodies `0..35` use
> Palette 0** — the seasonal one the `ItmLookColor` 0..9 convention indexes. Bodies `36..43` (the wind armors)
> are Palette 1, `44..56` (ice and the other late gear) Palette 2, `57..62`/`63..65`/`66` Palettes 3/4/5.
> **Ramp 10 is the grayscale/black ramp on Palette 0 and a BROWN ramp on Palette 1**, so the Hyun moo (black
> team) war paint came out brown on wind armor, and Ju jak came out olive.
>
> Palette 1 shares only ramps **`24`/`28`/`29`/`30`/`31`** with Palette 0 (the system rows), and all 24 of its
> *own* ramps run **light→dark** where Palette 0's run dark→light — the wind sprites are authored against an
> inverted palette, so only those five preserve shading. Palette 2's ramps `0..31` are byte-identical to
> Palette 0, so ice needs no remap.
>
> Fix: **`game-data/ArmorDyeRamps.csv`** (`BodyLookLo,BodyLookHi,Dye,Ramp`) → `Content.DyeRampFor`,
> applied by `Session.ArmorDye()` to **both** the war paint and the item's `ItmLookColor`. Everything else in
> the server keeps speaking canonical (Palette-0) numbers; only the wire byte is converted. Wind mapping:
> Hyun moo `10→28`, Baekho `11→29`, River `17→3`, Fire `20→16`, Chung ryong `24→24`, Ash `28→28`, Snow
> `29→29`, Ju jak `31→31`. Derivation and proof sheet: `re/wind_dye_final.png`.
>
> **Two team colours deviate from RTK on purpose (§11e).** RTK gives Ju jak `21` and Fire `31`. On this
> client's palette, ramp 21 (index 216) is a dark RUST, mean `(117,52,22)` — so the Vermilion Bird rendered
> brown on *every* armor, not just wind. The palette holds exactly one strong red, ramp 31 (index 40), mean
> `(173,45,0)`, with over double the red-dominance of any other ramp — and Fire had it. Ju jak therefore takes
> `31` and Fire moves to `20` (index 208, mean `(213,134,65)`, orange), which keeps all eight teams
> distinguishable. Palette 1 has no orange at ramp 20 (that row is khaki), hence the `20→16` row.
>
> One residual, by design: wind armor's own `ItmLookColor` is `24` and RTK's **Chung ryong war paint is also
> `24`** (§11e) — both mean the same azure ramp, so that single dye cannot change a wind armor, because it is
> already exactly what an undyed one renders. Row `36,43,24,12` points it at a distinct periwinkle if a team
> battle needs to tell them apart.

> **⚠ Weapon byte `[5]` is FAMILY-RANGED, and `ItmLook` speaks a DIFFERENT space (RE'd 2026-08-19 —
> the "Frozen spear / Might spear draw as random swords when equipped" bug).** The player-draw fn
> `0x432320` pushes the weapon byte through the classifier **`0x432fe0`**:
> ```
> 0x00..0x7F -> 1    0x80..0xBF -> 2    0xC0..0xDF -> 3    0xE0..0xFE -> 4
> ```
> and its switch pushes the matching archive name (UTF-16 in the exe, which is why an ASCII string search
> misses them): 1 → `SWORD.EPF` (`0x4f1b90`), 2 → `SPEAR.EPF` (`0x4f1ba4`), 4 → `FAN.EPF` (`0x4f1bb8`);
> case 3 (bow) falls through and pushes **no archive at all** — and indeed `Bow.tbl` says `NumWeapons 0`,
> so 4.95 cannot draw a bow, period (an equipped bow shows an empty hand, authentically). Art counts from
> the client's own `NexusTK.dat` tables: **Sword 95 (`0..94`), Spear 31 (`0..30`), Bow 0, Fan 4 (`0..3`)**;
> `Shield.tbl` has 13. `Items.csv`'s `ItmLook` is RTK's flat space — family = `look/10000` (0 sword,
> 1 spear/2H, 2 bow, 3 fan), art = `look%10000` — sized for a LATER client with far more art. The server
> converts via `Session.WeaponWireLook` / `ShieldWireLook` / `ArmorWireLook` (armor: `Body.tbl` has 67
> bodies, later-era looks like `210`/`10008` clamp to the base body instead of truncating to a *wrong*
> body). The old code sent `(byte)ItmLook`, so Frozen spear (10011) truncated to sword 27 while its **icon**
> (a separate Item.epf id space, §11c) stayed correct — exactly the reported symptom. A 2026-08-19 data
> sweep also re-pointed 65 Items.csv rows whose per-family art index only exists in later clients (Might
> spear `10087→10015`, the out-of-range staves/blades/charms, etc.) at era-plausible 4.95 art — contact
> sheets to retune any of them: `re/render_weapons.py` → `re/weapons_{sword,spear,fan,shield}.png`.
> (Same script documents the variable-length `DLPalette` header: color data at `+32 + 2*count(u32@+24)`.
> The count is 0 in 12 of Effect.pal's 13 blocks, which is why render_effects.py's flat `+32` mostly worked;
> the thirteenth has count 1, so a flat `+32` shears that block by two bytes. Re-counted from the install
> 2026-10-24; this used to say the count is 0 in Effect.pal.)
>
> **⚠ Weapon/shield "empty" = `0xFF`, not `0` (proven live 2026-07-25 + RTK `clif.c`).** A row-sweep of `[5]`/`[6]`
> showed every value `0..15` renders a distinct blade/shield for **both** sexes; only `-1` (byte `0xFF`) is bare
> hands. RTK sends `0xFFFF` for weapon/shield look when `!pc_isequip(slot)`. `SendSelfLook`/`ShowPlayer`/click-
> profile therefore emit the worn item's `Look` when a weapon (`Type 3`)/shield (`Type 5`) is actually equipped,
> else `0xFF` — keyed on slot occupancy (a worn weapon with `Look == 0`, e.g. Novice sword, still shows sprite 0).

**There is no separate hair slot** in this form, and creation byte `[4]` ("hair") has nowhere to go —
but that does *not* mean 4.95 characters have no hair. `Head.epf` heads are **head + hairstyle baked
together**, so the single face byte `[2]` picks both at once (see the render sheet: the 90 heads differ
mostly by hair). An independent hair id can't be sent; a different hairstyle means a different face id.
This is a hard limit of the packet, not a server bug.

**The 'r' Ride key vs. `@ride`/`@mount` (fixed 2026-07-26).** These now do different things. `@ride`/
`@mount [0|1]` (`Session.ToggleMount`) is a plain GM/debug toggle — flips `Character.Mounted` unconditionally,
no world state involved. The **'r' key** (`0x1b` setting `0x00` → `Session.TryRideHorse`) is the real RTK
mechanic (`clif_findmount`): mounting requires an actual `Mob` with `MobDef.Key == "horse"` (the plain
wandering "Horse" — id 8 — spawned in Buya/Horse Valley by `Content.AreaSpawns`; combat mobs that merely
share the word, e.g. `wild_horse`/`horse_guardsman`, don't count) standing on the **single tile the player
is facing** (`FrontTile()`, checked via `World.MobNear(..., radius: 0)` — cardinal only, same reach as the
player's own melee attack; diagonal doesn't count, corrected same day after the user caught it working
diagonally) — and **despawns that mob** (`World.DespawnMob`: no loot/exp, and if it was a spawn-point mob
the point is freed to respawn normally, like a kill). With no horse faced, 'r' just replies "There is no
horse to ride here." and does nothing. **Dismounting spawns a fresh "horse" mob back down beside the
player, facing them** (`SummonWorldMob`, same path as `@summon`) — so the horse you rode away is physically
set back down when you get off, instead of just vanishing.

**Where the dismounted horse lands (fixed 2026-08-06).** `Session.DismountTile()` checks the **4 cardinal
neighbours only**, **clockwise from the tile the rider faces** (`dir`, `dir+1`, `dir+2`, `dir+3` — the
0=N 1=E 2=S 3=W encoding is already clockwise, so that's faced/right/behind/left), and takes the first free
one; if all four are taken it **stacks the horse on the rider's own tile** rather than dropping it. **No
diagonals** — nothing in this game is diagonally adjacent (movement, melee reach, and the mount check
itself are all cardinal), so a corner tile is never a valid slot. Free = in bounds + not
`MapData.BlockedMove` (ground pass flag AND the `SObj` object-wall, the same two-layer test the walk uses)
+ no mob (`TileHasMob`) + no player (`World.PeerAt`). The horse always faces back toward the rider. Before
this, dismount just `Math.Clamp`ed the faced tile to the map bounds and spawned there, so horses appeared
inside walls, in water, and on top of whatever already stood in front of you.

**What being mounted BLOCKS (2026-08-06).** `Character.Mounted` is a state gate, the same way RTK's
`pc_equipitem` gates on it.

**The refusals that answer.** One shared refusal, `BlockedByMount` (`Server/Session.CharacterApi.cs:116-121`),
answers with the minitext "You can't do that while riding a mount." (`:119`). These handlers call it:

| Action | Handler |
|---|---|
| drop | `HandleDropItem` (`Server/Session.Items.cs:210`) |
| throw | `HandleThrow` (`Server/Session.Items.cs:262`) |
| use and eat | `HandleUseItem` (`Server/Session.Items.cs:331`) |
| equip | `EquipFromSlot` (`Server/Session.Items.cs:1336`) |
| unequip | `HandleUnequip` (`Server/Session.Items.cs:1420`) |
| drop gold | `HandleDropGold` (`Server/Session.Items.cs:1677`) |
| hand an item | `HandleHandItem` (`Server/Session.Social.cs:440`) |
| hand gold | `HandleHandGold` (`Server/Session.Social.cs:471`) |
| **casting** | `HandleCast` (`Server/Session.Spells.cs:266`) |

Casting is checked before the spell slot is even resolved, so no spell is exempt, not even Hyun Moo Revival.

**Melee is silent.** `HandleAttack` refuses a mounted swing with no message at all (`Server/Session.Combat.cs:146`),
matching the live client and matching how an over-rate swing is dropped. The melee gate sits before the swing-rate
gate, so a mounted attack key doesn't burn the swing interval. The shared action-budget bump for `0x13` still happens
at dispatch, as in RTK.

**Emote and pickup are blocked too (2026-10-22), both silently, as in RTK.** Neither goes through
`BlockedByMount`, so neither says anything: no `0x1A`, no sound, no line.
- **Emote.** A mounted player's `0x1d` is dropped at the top of `HandleEmotion`, and so is a dead, invisible or
  transformed player's (`Server/Session.Combat.cs:467`). RTK's `clif_parseemotion` plays only in state 0
  (`clif.c:6477-6482`).
- **Pickup.** A mounted or dead player's `0x07` is dropped at the top of `HandlePickup`, before the crouch and
  before the invisibility break (`Server/Session.Items.cs:168`). RTK's `clif_parsegetitem` returns for states 1 and
  3 first (`clif.c:9923-9924`). So an invisible rider stays invisible.
- **Budget.** As with melee, both opcodes still pay the shared action budget at dispatch.

**Minimum visible self:** `appearance = [sex, 0, face, 0, 0, 0, 0]`, `renderKind = 1`. Any nonzero
value in `[1]` (the form byte) risks blanking the whole sprite — that was the root cause of the
"invisible character" saga (§14).

The parser `0x436120` stores the 7 bytes into an entity sub-struct at offsets +4,+5,+7,+8,+9,+A,+B
(with a special case: if byte[3]==0 it defaults to `byte[0]!=0`). The core entity/body is built by
`0x44d7d0` (create-entity) directly (ctor `0x462ec0`/reposition `0x44c660` for self — see below); the
appearance pointer is stashed at `[entity+0x108]`. The byte→sprite-layer resolution, once decoded only by
observation, is now read from the binary in §8.2.

### 8.1 The floating nameplate marker — leaked every appearance refresh, patched out (2026-07-27)

**Not classic content.** The always-on inverted-triangle marker above every player (turns into their
name on hover) does not appear in real 4.x screenshots — confirmed by the user. It comes from a
**separate decoration/marker sprite** the client builds on top of the entity, distinct from the body:
after `0x44d7d0` returns, the SAME `0x33` handler (`0x44fef0`) — for **any** player packet
(`renderKind=1`) — unconditionally does `0x43fd80` (alloc `0x17c` bytes) → **`0x463380`** (ctor, player
archive `0x4f2a84`) → **`0x462050`** (attach the new sprite to the entity) → `0x45c830` (register into the
id-keyed entity hashmap also used for lookup by `0x45cb80`/removal by `0x45c8f0`). Crucially: this
**never frees whatever marker was already attached from the entity's previous `0x33`.** Every appearance
refresh (equip/unequip weapon or armor/shield, `Type` 3/4/5 in `Content.ItemById`; mount/dismount) leaks
one marker object, self or peer alike — confirmed live via Frida (two consecutive self-refreshes
registered two different sprite pointers, the first never freed). A real despawn (`0x0E` → `0x44d9f0`,
the same path `SyncMobs`/`World.LeaveMap` already use) destructs the entity **and** its currently-attached
marker as a pair, which is why walking out of view and back (or the peer-side despawn-before-reshow now
sent by `Session.RefreshAppearance`) clears accumulated litter — but a bare appearance resend never
destructs anything.

No server-side disable flag exists: appearance byte `[4]` (see the color-index note above) was
live-swept across its full range during real weapon toggles hunting for a hide-nameplate bit — it only
ever affects armor dye, the marker never changed.

**Fix shipped as a permanent on-disk patch** (not a server workaround — there's no packet-level lever).
Live-verified first via Frida (forcing `0x463380`'s return value to `NULL`, so the caller's existing
"ctor failed → skip attach/register" fallback fires — the same path already taken outside renderKind
1/2/3): characters render **fully normally** with the marker ctor suppressed, proving it's purely
decorative. `re/patch_no_nametag.py` overwrites the first 5 bytes of `0x463380` from
`55 8B EC 6A FF` (`push ebp; mov ebp,esp; push -1`) to `33 C0 C2 14 00` (`xor eax,eax; ret 0x14` — the
`ret 0x14` matches the real function's own epilogue, so the 5-dword call-site stack stays balanced).
File offset == RVA == `0x63380` for this exe. Requires an elevated terminal (`Program Files` write);
backs up the original exe first (`re/*.prenametagpatch.bak`, gitignored — never commit a full client
binary). `--check`/`--revert` also supported. See `memory/nexustk-495-nametag-litter.md` (session memory)
for the full investigation trail and the Frida tracer (`re/frida_nametag.py`) used to find it.

### 8.2 Drawing a player: the four layers, their order and their frames

*(Read statically from the 4.95 binary and its own `NexusTK.dat` and `Inter.dat`, 2026-10-21. Composites
built from these rules alone with `re/render_player.py` (every facing standing and walking; the attack with
sword, spear and fan, with and without a shield; the mount; the other actions; the emotes; the ghost; both
sexes, armored and not) all draw as whole characters, weapon in hand and shield on the arm.)*

**Inputs.** Peers and the self draw through one method, `0x462950` (slot `+0x70` of vtable `0x4cd098`; the
self entity's vtable holds the same pointer at `0x4cf05c`). When `[entity+0x178] == 0` it calls `0x432320`
(`this` = the player-sprite tables at `[0x501cf8]`) with:

- the 12-byte look at `[entity+0x178]`, laid out as `0x436120` parses it: `+4` sex `[0]`, `+5` form `[1]`,
  `+7` face `[2]`, `+8` armor `[3]`, `+9` colour `[4]`, `+0xA` weapon `[5]`, `+0xB` shield `[6]`. **The parser
  rewrites one byte: an armor `[3]` of 0 becomes 1 when sex `[0]` is not 0** (`0x436160`–`0x43616c`: `test al,al`,
  `jne` at `0x436162`, then `setne` on the sex byte into the armor byte). Body art 0 is the male base body and
  art 1 the female one, and the server sends armor 0 for every unarmored character (`Session.ApplyAppearance`
  on unequip, and `ArmorWireLook` for a look with no 4.95 body), so a client that skips this rule draws every
  unarmored female on the male body. Nothing later in the draw reads the sex byte: `0x433040` never reads
  look `+4`;
- the facing **`d` = `[entity+0x18d]`: 0 = N (back view), 1 = E (faces right), 2 = S (front view), 3 = W
  (faces left)**, the walk encoding of §10 and the byte the `0x11` turn stores (`0x462410`);
- the walk phase `ph` = `[entity+0x18e]`: 0 standing, then `(ph + 1) & 3` per walk tick (`0x48eef4` for the
  self, `0x4627c1` for peers);
- the running action at `[entity+0x188]` (null when idle). The `0x1A` handler `0x4503a0` builds it through
  `[vtbl+0x78]` = `0x462460` → `0x4612f0`, which keeps the action **type `t`** at `+0x108` and the action
  **frame `f`** at `+0x10f`. The tick `0x461800` steps `f` from 0 to `n − 1`, one step per `time × 10`, and ends
  the action when `f` reaches `n`; `n` (`+0x110`, set at `0x46133f`–`0x461361`) is 2 for `t` 1 (attack) and
  2 (throw), 4 for 22 (dance), 1 for 3–21, 23 and 24, and 0 above 24.

**The layers.** `0x433040` turns those inputs into four frame numbers (`0xFFFF` = none) and a four-letter
draw order; `0x432320` loads each frame (`0x430de0`) from the archive below (names in UTF-16 in the exe):

| Letter | Layer | Archive | Palette: the table record's `Palette`, through `0x467470` |
|---|---|---|---|
| `b` | body (armor `[3]` after the parser's rule: 0 with a non-zero sex is 1) | `BODY.EPF` (`0x4f1bf4`) | `Body.tbl[armor]`; the only layer the `[4]` ramp shift touches (⚠ above) |
| `h` | head (face `[2]`) | `HEAD.EPF` (`0x4f1be0`); **`EMOTION.EPF`** (`0x4f1c08`) while `t` is 11–24 (`0x4323f4`); **`GHOST.EPF`** (`0x4f1c20`) for form 1 | `Head.tbl[face]`; the ghost takes `GHOST.PAL` by name (`0x432541`) |
| `w` | weapon `[5]` | `SWORD.EPF` / `SPEAR.EPF` / `FAN.EPF` by the `0x432fe0` class (weapon ⚠ above); a bow loads nothing | `Sword` / `Spear` / `Fan.tbl[art]` |
| `s` | shield `[6]` | `SHIELD.EPF` (`0x4f1bc8`) | `Shield.tbl[shield]` |

**The order.** The draw loop (`0x432821`) takes the letters left to right, so **the first letter is the
bottom layer**. `0` marks an empty place, and a layer whose letter is absent is not drawn even when its frame
exists. The string comes from **`DrwOrder.tbl`, an entry of `Inter.dat`, not `NexusTK.dat`** (the resource
lookup `0x416f00` searches Inter.dat's directory first): 100 records of four letters and a `;` (502 bytes with a
trailing CRLF; `0x431a62` counts `size / 5`), indexed by the pose number `p` below (`0x432f90`; a `p` outside
0–99 gives `bh00`). The emotes `t` 11–19, 21 and 23 use the literal `bhws` instead (`0x43389d`, `0x4338fe`).

**The pose number and the frames.** One pose number `p` picks the head and the body frame together: **head =
`Head.Starting + p`, body = `Body.Starting + p`** (16-bit adds). A head art is a block of 100 frames
(`Starting = 100 × id`, 90 heads) and a body art a block of 104 (`Body.tbl`: 67 bodies, `Starting = 104 × id`).
Weapon and shield arts are 20-frame blocks (`Starting = 20 × art`) with their own index `q`. With the
sub-frame `sub = {0, 1, 0, 2}[ph]` (`0x4cb508`) and the weapon class `c` from `0x432fe0` (0 = none `0xFF`,
1 sword, 2 spear, 3 bow, 4 fan):

| What is playing (branch) | `p` | weapon and shield `q` |
|---|---|---|
| nothing: standing or walking (`0x433164`) | `{0, 12, 32, 52, 12}[c] + 3d + sub` (`0x4cb510`) | `3d + sub` |
| nothing, form 3 mounted (`0x4330cb`) | `80 + 3d + sub` | none |
| `t` 1 attack (`0x43336b`) | `{24, 24, 44, 64, 24}[c] + 2d + f` (`0x4cb524`) | `12 + 2d + f` |
| `t` 2 throw, 3 shot | `24 + 2d + f`, `64 + 2d + f` | none |
| `t` 4 pick up, 5 drop | `76 + d` | none |
| `t` 6 magic | `72 + d` | none |
| `t` 7 drink or smoke and 8 eat, 9 respect, 10 triumph | `92`, `96 + d`, `93` | none |
| `t` 11–19, 21, 23 (emotes) | body `3d` with order `bhws`; head = `EMOTION.EPF` frame `20 × face + (t − 11)` (for `t` 23, `+ 15`) | none |
| `t` 20 shrug, 22 dance, 24 kiss | `95`, `100 + f`, `96 + d`; head = `EMOTION.EPF` frame `20 × face + 9`, `+ 11 + f`, `+ 16 + d` | none |
| `t` above 24 | all four frames `0xFFFF`: nothing is drawn | none |

The branch for each `t` is the jump table behind `0x433350`. As a frame map: a weapon or shield block holds
the idle and walk frames at 0–11 (three per facing) and two swing frames per facing at 12–19 (the odd ones are
the arcs); a head or body block holds unarmed 0–11, one-handed (sword, fan) 12–23, the swing 24–31, spear
32–43, the spear thrust 44–51, bow 52–63, the bow shot 64–71, magic 72–75, pick-up and drop 76–79, mounted
80–91 and single poses 92–99, and a body block also the dance at 100–103. `EMOTION.EPF` holds 20 frames per
face (1800 = 90 × 20).

**Which case wins** (`0x433040`, in this order): form 2 draws nothing (`0x432320` already returns for form 2
and 6 and up, `0x432364`); form 1 draws **the ghost alone**, order `h`, `GHOST.EPF` frame = face (90 frames, one
per face, no facing) and no body, weapon or shield (`0x43308b`, `0x4323db`); then **any action, ahead of the
mount** (`0x4330b8`), so a mounted rider is drawn dismounted while an action or emote plays; then form 3; then
forms 0, 4 and 5 alike (5 changes only the blit, below).

**The orders by facing.** `DrwOrder.tbl` read at those `p`, bottom layer first (a walk shows the sub-frames
0, 1, 0, 2):

| Pose | N (`d` 0) | E (1) | S (2) | W (3) |
|---|---|---|---|---|
| unarmed, sub-frame 0 / 1 / 2 | `wbhs` / `wbhs` / `sbhw` | `sbhw` | `bhsw` / `sbhw` / `wbhs` | `wbhs` |
| sword or fan, sub-frame 0 / 1 / 2 | `wbhs` / `wbhs` / `swbh` | `sbhw` | `bhsw` / `sbhw` / `bhsw` | `wbhs` |
| attack (none, sword, fan), `f` 0 / 1 | `sbhw` / `wbhs` | `sbhw` / `wsbh` | `wbhs` / `sbhw` | `wbhs` / `sbwh` |
| spear: standing, walking, thrusting | `wbh0` | `bhw0` | `bhw0` | `wbh0` |

Every other pose draws the body under the head (`bhw0`, `wbh0` or `bh00`), except magic facing E and W (`p` 73
and 75, `whb0`), where the body frame's raised hands cover the face. In those poses the `w` and `s` draw nothing:
mounted and actions 2–24 set both frames to `0xFFFF`, and the bow poses 52–71 have no weapon art and no `s`.
**No spear or bow pose has an `s`, so a shield is never drawn with a spear or a bow** even though its frame is
computed and `SHIELD.EPF` is loaded for it; nor is one drawn mounted or during actions 2–24.

**What is skipped.** A layer draws only if its letter is in the order and its frame loaded (the `[obj+4]`
tests at `0x432522`, `0x432584`, `0x4325d2` and `0x432690`):

- weapon `0xFF` and shield `0xFF` give frame `0xFFFF` (`0x43324b` and `0x4331fc` idle, `0x43344c` and
  `0x433405` attack); a bow (`0xC0`–`0xDF`) loads no archive but still selects the bow body poses;
- **a face of 90 or more**: the head table's default record has `Starting = 0xFFFFFF01` (`0x431dad`), so the
  frame is `0xFF01 + p`, past `Head.epf`'s 9000: no head. Such a face has no `EMOTION.EPF` or `GHOST.EPF`
  frame either;
- an armor, weapon art or shield art past its table's count is **not** skipped: those tables' default record
  has `Starting 0` and `Palette −1` (loaders `0x431f60` and `0x432140`), so art 0's frames draw, in whatever
  palette `0x467470` falls back to (`0x467460`). The server never sends one (`ArmorWireLook`,
  `WeaponWireLook`, `ShieldWireLook`).

**Placing and blitting.** The layers share one space: every EPF frame box (top, left, bottom, right) is signed
from the same origin, so a composite is each frame drawn at its own box. `0x432320` takes the union of the four
boxes (`0x4240c0`, `0x4241d0`) and offsets each layer by its box within it. Every form but 5 blits through
`[renderer+0x94]`, and the body through the tinted `[renderer+0x98]` when `[4]` is not 0; form 5 blits every
layer through `[renderer+0xac]` with a 0.7 factor (`0x3f333333`), keeping the body's tint. The ramp shift
reaches every body pixel at index `0x30` or above, so on a mounted body it also shifts the horse's pixels
at those indices.

**Not settled here.** A non-zero `[entity+0x1a8]` sends every layer through `[renderer+0x9c]` instead, and a
pre-pass gated by `0x44f600` draws the same order through `[renderer+0xa0]` first; neither is decoded. None of
this section was compared with a running client: a live look (or a Frida read of `0x433040`'s outputs) would
confirm it, in particular the rider drawn dismounted during an action and what the `Palette −1` fallback looks
like.

---

## 9. Character creation & the creation packet

Creation is two login-channel packets:

1. **`0x02` NameCheck** — `nameLen name pwLen pw 00 00 00` (name + password). Server replies with the
   standard `0x02` frame §2 describes: increment `0x01` and a body of **four zero bytes** — code `0x00`
   ("available / OK"), text length 0, the text's `00` terminator, and one more `00`. The literal frame is
   `AA 00 06 02 01 4F 64 79 6E` (`LoginServer/LoginSession.cs:175`), which deciphers to `00 00 00 00` with
   increment `0x01`. A reader takes the code from the first byte and ignores what trails. (Corrected
   2026-10-24: this used to say the payload is a single `00`.)

   **Refusal.** No "name taken" reply form has been observed from a real server, so this server refuses by
   sending ONLY the message box (`0x02` wrapping a `0x0F`, the same form `"Incorrect password."` uses on
   this screen) and withholding the availability OK — the client then can't advance to appearance
   selection. Verified over the wire: the client receives and displays the message. The real boundary is
   `0x04`, which re-runs the same gate, because a hand-rolled client can send `0x04` without ever asking
   `0x02`. (Before this, the name check answered "available" unconditionally and `0x04` did a
   load-then-overwrite, so re-"creating" an existing name reset that character's password.)
2. **`0x04` CreateAppearance** — **5 bytes**: `[0]=face [1]=sex [2]=nation [3]=totem [4]=hair`.
   **It carries no name.** The character is created under the name `0x02` checked, and `0x04` re-runs the
   name gate on that name before anything is written. With no `0x02` first the name is empty, and the
   gate's message box reads "Please enter a name." Until 2026-09-27 the server read `[0]` as a name
   length, so a face of 1 to 4 became a 1-to-4-byte name and the gate refused it. Nothing on record
   shows the 5.33 client sending a name here either, and both login ports run the one handler
   (`LoginSession.HandleCreate`).
   Field ORDER confirmed against the real RTK char-server source (`RTK-Server/rtk/src/char/logif.c`,
   `logif_parse_newchar`): its call `char_db_newchar(name, pass, totem=RFIFOB(39), sex=RFIFOB(37)%2,
   country=RFIFOB(38), face=RFIFOB(36), hair=RFIFOB(40), faceColor=RFIFOB(42), hairColor=RFIFOB(41))`
   shows the appearance tail (right after that server's fixed-width name+pass block) is laid out
   face, sex, nation, totem, hair, hairColor, faceColor. Our 4.95 blob is the same 5-byte prefix of
   that order, minus the two color bytes. (An earlier pass mis-read `[2]` as "near-constant misc" and
   `[3]`/`[4]` as an undecoded nation/totem pair — that guess never had a sample where nation was
   deliberately varied; re-reading with the corrected order lines up perfectly, e.g. a character
   created with Totem=JuJak + Nation=Buya logs `... 02 00 00` → `[2]=2 (Buya) [3]=0 (JuJak)`.)

   | Byte | Meaning | Evidence |
   |---|---|---|
   | `[0]` | **Face** | chars "faceone/two/three" gave `[0]` = `00/23/34` → three distinct correct faces. |
   | `[1]` | **Sex** | `0` = male, `1` = female. Char "male" → `[1]=00`; every female → `[1]=01`. |
   | `[2]` | **Nation** (`Character.Nations` index) | live report + RTK `logif.c` field order. |
   | `[3]` | **Totem** (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None) | same; matches RTK's `Player.getTotemName`. |
   | `[4]` | **Hair** | never observed non-zero in these samples; persisted, no 4.95 render slot yet. |

   Sample blobs (re-decoded — name, sex, **nation**, **totem**):
   ```
   male:      55 00 02 02 00   M, Buya,    HyunMoo
   female:    12 01 02 01 00   F, Buya,    Baekho
   faceone:   00 00 02 02 00   M, Buya,    HyunMoo
   facetwo:   23 00 01 02 00   M, Koguryo, HyunMoo
   facethree: 34 00 01 02 00   M, Koguryo, HyunMoo
   newbie:    29 00 02 00 00   M, Buya,    JuJak      (live pick: "JuJak + Buya")
   newbiea:   32 00 02 03 00   M, Buya,    ChungRyong
   newbieb:   3d 00 02 00 00   M, Buya,    JuJak      (live pick: "JuJak + Buya")
   ```

**Creation → render mapping** (server-side, `CharacterFactory.ApplyAppearance`): render `appearance[0]` (sex) =
creation `[1]`; render `appearance[2]` (face) = creation `[0]`. `Character.Nation`/`Character.Totem`
are set directly from creation `[2]`/`[3]` (validated against range). Hair (`[4]`) is persisted but has no
4.95 render slot. All five are set once, at creation or when a legacy per-file record is imported, never at
a login: the nation, face, sex and totem change in play (the town criers, Rotah and `@nation`; the
rogue-guild shaman; the totem shrines and `@totem`) and a login must keep them. At a login only a saved face,
totem or nation that no client can use falls back to the creation pick (`CharacterFactory.RestoreUnusableFromCreation`).
`PlaceNewCharacter` (run AFTER `ApplyAppearance` so it sees the real nation) then routes a brand-new character
to their home city — see §11f.

Real RTK note: in the *Lua* gameplay layer (`totem_npc.lua`), totem is normally re-assigned later by
worshipping a totem-animal shrine in the Wilderness, not fixed forever at creation — this server
currently only implements the creation-time pick, not the worship system.

> **Critical caveat:** the creation-packet byte space and the render-packet byte space are **different
> id spaces** with different field *orders* (creation `[0]`=face but render `[2]`=face; creation
> `[1]`=gender and render `[0]`=body). Do not assume they line up — map field-by-field.

### Stats packet — `0x08` (server → client)

Populates the always-on HUD (level, might, will, grace, HP/MP bars + numbers, experience, coins,
nation, totem). Send it in the world-entry burst after `0x33`. Framing is the normal
`AA | len | 08 | inc | body` with the NexonInc cipher. `body[0]` is a **flags** byte selecting the
form; `0x78` = the "full stats" form used below. Multi-byte stat fields are **big-endian `u32`**.

```
body offset  field            type
[0]          flags = 0x78     u8    (full-stats form)
[1]          nation           u8    (0=Neutral 1=Koguryo 2=Buya 3=Nagnang 4=Shilla 5=Jinhan 6=Paekjae 7=Kaya)
[2]          totem            u8    (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None)
[4]          level            u8
[5..8]       maxHP            u32BE (offset confirmed via @hp: HP=100/max=1000 -> bar ~10% full)
[9..12]      maxMP            u32BE (confirmed)
[13]         might            u8
[14]         will             u8
[17]         grace            u8
[24..27]     HP (current)     u32BE
[28..31]     MP (current)     u32BE
[32..35]     experience       u32BE
[36..39]     coins            u32BE
```
Body length ~58 bytes (unused offsets zero-filled). See `Session.SendStats()`.

**Mail/parcel HUD notification byte (in the `0x08` tail).** The real 4.x client shows a small **arrow**
(bottom-left) when you have unread **n-mail**, and an **arrow + bag** when you have a **parcel** waiting —
the cue to visit the postmaster NPC. RTK's `clif_sendstatus` (`rtk/src/map/clif.c`) proves the driver:
its 6.x/7.x `0x08` is a *composite* packet (form byte at offset 5 is an `SFLAG_*` bitmask —
`0x40`FULLSTATS `0x20`HPMP `0x10`XPMONEY `0x08`ALWAYSON; `0x78` = all of them), and its **always-on tail**
carries `sd->flags` documented verbatim as **`1 = New parcel, 16 = New Message (n-mail), 17 = both`**
(a bitmask: bit0 parcel, bit4 mail). That tail rides the SAME `0x78` "full" form we already send.

**✅ CONFIRMED LIVE (2026-07-28): the 4.95 flag byte is `body[45]`.** Sending our full-stats `0x08` with
`body[45]=0x11` (via `@mailflag 45 11`, `Session.MailFlagProbe`) made the real client draw **both** the
mail arrow and the parcel bag in the bottom-left HUD cell; the decrypted wire packet showed `body[45]=0x11`
exactly. Bit semantics match RTK: **`0x10` = n-mail arrow, `0x01` = parcel bag, `0x11` = both, `0x00` =
clear.** So the 4.95 client IS network-wired for the notification (the render widget = vtable `0x4ce440`,
ctor `0x47a230`, draw `0x469480`, loading `MAIL.EPF`/`PARCEL.EPF` from `Inter.dat`; state bytes
`[+0x105]`=parcel-count, `[+0x106]`=hasMail). NOTE: our `SendStats` zero-fills `body[45]`, so any clean
stats resend (movement/action) CLEARS the arrow — real wiring must set `body[45]` in `SendStats` from a
persisted mail/parcel state, not one-shot. Retrieval is a plain `0x30` dialog: RTK's postmaster
`MessengerNpc` (`rtklua/.../messenger.lua`) offers *Mailbox → Send/Receive Parcel* — already supported by
our NPC dialog system. (Client-side `m` hotkey to open mail is a **dead key** in both 4.83 and 4.95 — see
the client-versions memory — so the NPC + this HUD flag are the authentic access path.)

**How this was found (methodology worth reusing):** static dispatch analysis wrongly "proved" `0x08`
was unhandled — it's a no-op in the world dispatcher remap *and* the conn-state object `0x444de0`, yet
the client processes it anyway via a pre-dispatch path. It was cracked by **replaying a real 6.x server
capture** (jeedee/TkServer `game_server.rb` has commented-out captured `send_data` packets; opcode `0x08`
= stats, decrypt with the shared NexonInc cipher — see `re/decode6x.py`). The exact 4.95 field offsets
were then pinned with a **self-describing gradient packet** (`body[i]=i`, in-game `@stg`): every HUD
number equals its own field offset, and u32 values (e.g. `0x18191A1B` at offset 24) reveal size and
big-endian order in one read. **Lesson: a "no-op in the dispatch table" is not proof an opcode is
unhandled, and a close-version reference server (6.x here) beats guessing.**

Server reply to `0x04` — **the create-ack (SOLVED 2026-08-24).** Every login-channel server→client
status is one frame shape, `0x02` wrapping `[code][len][text][00]`, and the **code byte is the
client's sub-dispatch**: `0x00` = success/advance, `0x0F` = message box (text renders, screen stays).
On create success send **code `0x00` with the text riding along** — the 4.95 client shows the message
AND dismisses the creation screen back to the main menu (confirmed live; with the old `0x0F` reply the
screen stuck and players re-submitted, hitting the duplicate-name refusal). This is exactly RTK's
`clif_message` (`rtk/src/login/clif.c`, codes `0x00`/`0x03`/`0x05`; create success at
`rtk/src/login/intif.c` `intif_parse_2002` → `clif_message(fd, 0x00, LGN_NEWCHAR)`) — RTK supplied the
structure, the live client supplied the proof. The name-availability OK (item 1 above) is this same frame
with code `0x00` and an empty string, plus one trailing `00`: four body bytes, all zero, at increment `0x01`
(§2).

---

## 9.5 The profile system (self-profile, click-profile, editing)

There are **two distinct profile windows**, plus an edit path. Both windows are UI `0x198`; the client
opens each on a different request opcode, and the server replies with a different packet. All
multi-byte ints below are **big-endian**. See `Session.SendSelfProfile` / `SendClickProfile` /
`HandleChangeProfile`.

**Self-profile — request `0x2d` → reply `0x39` ("Mind's Eye").** Pressing the profile key sends
`2d 00` (`body[0]==0` = self). Reply with `0x39`, the stats/legend summary. **This layout was
reverse-engineered from the client's OWN parser `0x4732a0`** (the mode-0 self widget picked by the shared
profile dispatcher `0x424820`; vtable `0x4cdf88`, method +0x5c). Body:
```
[AC u8][dam u8][hit u8]
[clan : u8 len + bytes]           (len 0 = clanless)
[clanTitle : u8 len + bytes]
[title : u8 len + bytes]
[PARTY box : u8 len + text]       ← PAGE 2, above the stats. CR(0x0d)-separated. NOT "spouse" — see below
[group u8][TNL u32BE]             ← group u8 = the "sociable/group" status cell (Shift+G, 0x1b/0x02)
[className : u8 len + bytes]
[helmIcon u16BE][leftRingIcon u16BE][rightRingIcon u16BE]   ← the 3 equip-icon cells beside the doll
[BUFF box : u8 len + text]        ← PAGE 1; CR-separated, one line per active buff (TAB also accepted here)
[exchange u8]                     ← the "exchange/trade" status cell (0x1b/0x08); client field +0x935
[legendCount u8]                  ← a single u8 (NOT u16)
legendCount × { icon u8, color u8, textLen u8, text }
```
**⚠ Do NOT reuse a 6.x/RTK profile capture for 4.95.** Those forks have MORE item slots, so their `0x39`
carries a ~116-byte equipment region between `className` and the legends. 4.95's parser has no such region —
it reads only the 3 icon cells + buff box + one flag byte, then a **u8** legend count. Feeding it the 6.x
shape pushes the legend count into the padding (read as 0 → no legends) and spills icons into the wrong
fields (gear in wrong cells). Decoding a real 6.x capture with the grammar above proves it: the bytes align
perfectly up to the legend count, then the 6.x equip block is left unconsumed. The self-view doll BODY is the
LIVE on-map sprite (armor/weapon/shield only) — helm/rings have no sprite layer and show ONLY in the 3 icon
cells. The **buff box** is the self-view analog of the click-profile's gear list (issue: self=buffs,
other=gear); it holds `Name Ns` per active buff (no parentheses), grouped by spell (`Session.BuffBoxText`),
TAB-separated (see the separator note below — 5.33 parses this field, 4.95 only prints it).

**The profile pane has THREE pages, and `0x39` feeds all three.** The parser ends by pushing text into
three separate controls on the widget — this is what the page arrows switch between:

| Control | Fed by | Page |
|---|---|---|
| `+0x104` | the 6th string (after the TAB→CR pass at `0x47359b`) | **1** — active buffs |
| `+0x108` | the **4th** string | **2** — the group roster, the box above VITA/MANA/AC/DAM/HIT |
| `+0x10c` | the legend array | **3** — legend marks |

**⚠ The 4th string is the party box, NOT "spouse"** (corrected 2026-08-13). It was named by analogy with
7.x `clif_mystaytus`, and the mistake survived because an unmarried character sends an empty string —
which is exactly what a groupless player's roster box looks like too. The parser settles it: `0x4732a0`
copies that string to the widget's `+0x938` buffer and then **adds it to the text control at `+0x108`** —
the same store-then-add shape used for the buff box (`+0xb38` → control `+0x104`). A spouse name would be
a label, not a scrolling list. **4.95 has no spouse field because it doesn't need one:** marriage shows
as a **legend mark** — `Married to <name> (<date>)`, key `"married"`, icon 6 colour 1, stamped on both
sides by `Session.RunMarriageCeremony` — so it renders on page 3 with every other legend. `Character.Spouse`
is the server-side state behind it (divorce clears both the legend and the field).

`Session.PartyBoxText` builds it: one member per line, sorted alphabetically, leader marked `*Name`,
joined with **CR**.

**Page 2 uses CR; page 1 uses TAB.** `0x480b20`, the control's copy loop, is what turns a character into
a line break, and its allow-list for sub-`0x20` characters is exactly `0x0d`/`0x0a`:

```
00480b57  cmp  ax, 0xd
00480b5b  je   0x480b63      ; allowed through
00480b5d  cmp  ax, 0xa
00480b61  jne  0x480bb6      ; anything else below 0x20 is DROPPED
```

TAB (`0x09`) is not on that list, so in the **party box** it would be silently dropped and the roster would
run together — `Session.PartyBoxText` joins with CR. The **buff box** is the exception: the parser rewrites
TAB→CR there first (the loop at `0x47359b`), a pre-pass that exists for that one field and nothing else.

That pre-pass is the original server telling us what it sent, and `Session.BuffBoxText` now joins with **TAB**
(`Session.BuffBoxSep`) because of it. On 4.95 the choice is cosmetic — TAB→CR lands on the same bytes CR
would have — but **5.33 rewrites the same field to LF instead and then PARSES it into a live countdown
list**, and its reader breaks on LF only. Sending CR there collapsed the whole box into a single timer entry:
every buff still rendered, but only the last line's seconds counted down. See
[5.x/Wire-Divergences § `0x39` buff box](../5.x/Wire-Divergences.md#4b-0x39-buff-box--a-live-countdown-list-on-533-solved-2026-08-22).
`GearListText` also uses TAB, because `0x34` is a different parser (`0x48b6a0`) with its own conversion.
`0x480b20` also truncates at the first CR when the control is single-line (`+0x16e == 1`) — neither of
these is, or the page-1 box would already stop after its first buff.

**It refreshes on reopen, not live.** `0x39` is only sent in answer to a `0x2d` request, and pushing one
unsolicited would risk opening the window under the player. Someone joining or leaving while you have the
pane open doesn't redraw it — close and reopen. (**5.33 differs on page 1 only:** it drives its own
one-second countdown off the buff box, so those numbers stay live between refreshes and a buff that runs
out disappears from the list on its own. Everything else on the pane is still reopen-only there too.)

**Click-profile — request `0x43` → reply `0x34`.** Clicking a character sends `43 01 id(u32) 00`. Reply
with `0x34`, the public two-page view (portrait + gear on page 1; nation + picture + writable blurb on
page 2). **This layout was reverse-engineered from the client's OWN parser `0x48b6a0`** (profile-page
widget, vtable `0x4cee5c`, method +0x5c) — the 7.x `clif_clickonplayer` is a *different, larger* shape
and does not fit 4.95. Body:
```
5 header strings (u8 len + bytes): title, clan, clanTitle, class, name   (order confirmed live)
appearance: tag u8 (=0) + 7 look bytes                (same 7-byte form as 0x33 type-0 → correct sprite)
[helmIcon u16BE][leftRingIcon u16BE][rightRingIcon u16BE]  ← 3 equip-icon cells (SAME as 0x39; NOT portraits)
gear/item list (u8 len + text)                        PAGE 1; TAB-separated (client → CR), one line per FILLED
                                                      slot (empty slots omitted), in slot order:
                                                      `w:Weapon:<n>` `a:Armor:<n>` `s:Shield:<n>` `h:Helmet:<n>`
                                                      `l:LHand:<n>` `r:RHand:<n>`
targetId (u32BE)                                      THIS profile's entity id -- the Exchange button echoes it
group (u8), exchange (u8)                             the two status cells; 0/1 = off/on, 0xff = blank WHITE box
nation (u8)                                           PAGE 2; drawn via NATION_E.EPF
picture (u16BE len + bitmap bytes)                    PAGE 2; empty = 00 00
writable blurb (u8 len + text)                        PAGE 2; the free-text box
legendCount (u8)                                      (NOTE: u8 here, unlike 0x39's u16)
legendCount × { icon u8, color u8, textLen u8, text }
```
Gotchas that cost real debugging time:
- The **gear text** (page 1) and the **writable blurb** (page 2) are two *separate* length-prefixed
  strings at different points in the packet — do not merge them. **Omitting the blurb field desyncs
  the legend count** (the parser eats the legend-count byte as the blurb length) → empty legend.
- Legend **text color `0x80`** (from the real capture); color `0` renders the text invisible (icon
  still shows).
- **Dated legend text** (fixed 2026-07-26): several RTK legends stamp the in-game date, e.g.
  `player:addLegend("Aided Chu Rua (" .. curT() .. ")", ...)` (`chu_rua.lua`) and the "born" legend
  (`login_map_tile.lua`: `"Born in " .. curT()`, `curT()` = `"Yuri <year>, <season>"`). `Character.GameDate`
  is now **live** (2026-08-12): a static property reading `GameCalendar.Stamp` (§"Day-night clock"), so it
  produces a real `"Yuri <year>, <season>"` exactly like `curT()`. Every legend that stamps "the current
  date" reads it — the seeded born mark, engagement/marriage (`Session.Spells.cs`), `ChuRuaAbility`
  (`NpcAbility.cs`, which grants `$"Aided Chu Rua ({Character.GameDate})"` instead of the undated string it
  shipped with), and the Lua `gameDate` binding (`NpcScript.cs`).
  - The born mark is stamped **at construction**, in `Character.Legends`' field initializer, because that is
    the moment the character is born; it must not re-read the clock later or every birthday would slide
    forward with the world. Deserialization replaces the seeded list outright (System.Text.Json assigns a
    new `List<Legend>` to a settable member rather than appending to the initializer's), so loading an
    existing character does **not** add a second born mark — verified against the live DB.
  - It used to be the fixed constant `"Hyul 31, Winter"` (there was no server-side clock when the legends
    were written), matching the live-captured self-profile reference above (§9.5's "Born in Hyul 31,
    Winter"). The live 4.95 text says **"Hyul"** where RTK's `curT()` says **"Yuri"** — the same king under
    two names. We use RTK's word server-wide so every date the server writes agrees; characters created before
    2026-08-12 keep the `"Hyul 31, Winter"` already persisted in their legend list.
- 4.95's click popup has **no totem slot** (`TOTEM.EPF` is unreferenced in the client) — totem only
  appears on the HUD/self-profile, not here.
- The **3 icon cells** (helm / left ring / right ring) are shared by BOTH views as three `u16BE` fields.
  4.95 has no character-sprite layer for these slots, so the profile shows them as ground-icon boxes:
  each = `IconWire(item.Icon)` (same encoding as the `0x37` equip window), `0` = empty box. They were
  briefly mistaken for "portrait graphics"; a live test (an old bug rendered the *weapon* icon in the
  helm box) proved they take an `IconWire` value. Order confirmed live: helm, left ring, right ring.
  Wire slots come from the client's own `0x1F` unequip captures: **helm=4, left ring=7, right ring=8**.
  Fed by `Session.ProfileCellIcon(wireSlot)`.
- **The `u32BE` before the two status cells is the TARGET'S ENTITY ID, not a spare scalar** (RE'd
  2026-08-21; it was shipped as a hardcoded `0` for a month). The window keeps it (4.95 parser `0x48b6a0`
  reads it with the `u32BE` helper `0x475ce0` into `[window+0xb24]`; 5.33's `0x4d19c0` does the same via
  `0x4a1250` into `+0xa88`) and the **"Exchange" button hands it straight back** in `0x4a` — 4.95
  `0x48c7c7` is literally `mov eax,[edi+0xb24]` → the packet builder `0x48cd00`, 5.33 `0x4d2cb2` the same
  from `+0xa88`. The button does **not** reuse the id from the `0x43` click that opened the window, so a
  zero here makes exchange silently dead: the client sends `4a 00 00 00 00 00 00` and the server's
  `Online.ById(0)` matches nobody. RTK agrees — `clif_clickonplayer` writes `SWAP32(bl->id)` in exactly
  this slot, immediately before the `FLAG_GROUP` / `FLAG_EXCHANGE` bytes.
- **Group / exchange status cells.** Both views show a **group** (sociable) and **exchange** (trade)
  indicator. In `0x34` they're the two `u8` cells after the scalar (were briefly guessed as "look-selectors");
  in `0x39` they're the `group` byte (after `spouse`) and the `exchange` byte (the trailing flag before the
  legend count). **`0xff` renders a blank WHITE box** — send a real `0`/`1` (off/on). The two views MUST read
  the same source or they disagree (self showed OFF while other showed ON when only `0x34` was wired). Toggled
  by the `0x1b` setting opcode — **`0x02` = group (Shift+G), `0x08` = exchange** — and persisted on the
  character (`Character.Grouped` / `Character.Exchange`) so they survive reopening the profile and a relog.
  NOTE this is only the joinable/tradeable *flag* + its display; the actual party/trade **request** flows
  are built now — see §11l. The client's `0x2e` / `0x4a` packets seen in earlier captures are RTK's
  party-invite and exchange-initiate; **both are decoded and both are the profile window's own buttons**
  (`0x2e` from the Group button at `0x48c7ae`, `0x4a` from the Exchange button at `0x48c7c7`, dispatched
  through the hit-test jump table at `0x48c84c`). Note what actually **enables** the two buttons: 4.95
  stores the *group* byte at `[window+0xb28]` and BOTH handlers test only `cmp byte [edi+0xb28], 0xff` —
  `0xff` kills both buttons, any other value enables both. So these two `u8`s are indicators, not
  per-button enables; refusal is server-side (`TryStartTrade`, as in RTK's `clif_startexchange`).

**Editing — client `0x4f`.** Saving the profile edit sends
`4f | picSize(u16BE) | pic[] | blurbLen(u8) | blurb[] | 00`. Parse both (mirrors the client's own
`clif_changeprofile`: `picSize = u16BE(body[0]); text at body[2+picSize]`), persist to the character,
and reply with a `0x02` "Your profile has been saved." message. A later `0x43` click then shows the
player's own words + drawing.

### 9.5a Profile PICTURES — where they actually come from ✅ *(RE'd 2026-08-08)*

**The server never supplies the picture, and can't.** It is a file the *player* drops next to the client;
the client reads it off disk and attaches it to the `0x4f` it was going to send anyway. A capture that
shows `picSize = 0` therefore says nothing about the server:

```
FILE  CreateFileW("...\NextAeon/users/Zaleroo.epf") -> handle=0xffffffff  <<< OPEN FAILED >>>
ENCR  op=0x4f len=9: 4f 00 00 04 61 73 64 66 00        <- picSize 0, blurb "asdf"
```

That is the no-file path, and **every** client-side failure takes it — the `0x4f` always goes out, just
with a zero-length picture. Both directions are already wired here (`HandleChangeProfile` stores it,
`SendClickProfile` field #15 serves it back to viewers), so the only thing missing in that capture was
the file.

**What the client demands** (validation reversed from `0x44edc0`; each check is a hard `jne` bail):

| | |
|---|---|
| path | `<client cwd>/users/<CharacterName>.epf`, retried as `.face` |
| size | **exactly `0xb1c` = 2844 bytes** |
| `dword @ +8` | **`0xaf0` = 2800** — the EPF TOC offset (12-byte header + 2800 bytes of pixel area) |
| geometry | `toc[1].top − toc[0].bottom == 0x38` and `toc[1].left − toc[0].right == 0x30`, with `toc` taken at `+2800` |

Read canonically (the table at `12 + u32@8` = 2812, each entry `top,left,bottom,right` then
`pixOff,stencilOff`, as in the `Effect.epf` row of §7.2 and `Item.epf` in §11c), the file holds two entries and
the picture is **frame 0: 48 × 56, 8bpp**. The geometry row's two subtractions are frame 0's own
`bottom − top` and `right − left`: a `toc` taken at `+2800` starts 12 bytes before the table, which splits
each box across two entries. In the file `re/make_profile_epf.py` writes, frame 1 is all zeros. (Corrected
2026-10-24: this paragraph used to call frame 0 a pure origin marker and the picture frame 1, which is that
12-byte-early reading; the bytes are the same, only the frame numbers differ.) Those numbers are fixed — any
other geometry is rejected outright, not merely drawn wrong.

`re/make_profile_epf.py <image> <out.epf>` builds a conforming file (it asserts all four checks before
writing). Server side, **`0x49`** (`@askpic`) makes a client re-read the file and answer on `0x4f` without
the player reopening the profile editor. It is safe for the client, since the failure path still replies,
but **the server stores the answer** like any profile save: `HandleChangeProfile` replaces the stored picture,
clears it when `picSize = 0`, replaces the blurb with the one the answer carries
(`Server/Session.Dialog.cs:1650-1667`) and answers with the profile-save message. So an `@askpic` on a player
with no valid file clears the picture the server held for them.

Which palette the client draws the picture with is **not** pinned; the generator defaults to `Item.pal`
block 0. If colours come out wrong, sweep the block index — the client accepted the file either way.

---

## 10. Movement model

**The movement model is chosen by the client's FAST-MOVE setting — this is the master switch.** This was
established by RE + live Frida probing and matches RTK's `clif_parsewalk` (which gates its walk response
on `FLAG_FASTMOVE`). Getting this right is what makes 4.95 self-walk smooth.

Per step the client sends **`0x32`** (or **`0x06`** every few steps — handle both identically):
```
dir(u8) stepCounter(u8) X(u16) Y(u16) pad
```
Direction map: **0 = North (y−1), 1 = East (x+1), 2 = South (y+1), 3 = West (x−1).** `X,Y` is the client's
believed **current** tile (where it is walking *from*) — step from it (client-authoritative resync) so
collision runs on the cell the client is really on.

### 10.0 Refusing a step — the server CAN immobilise a player

Client-side prediction is not client authority. The client guesses a step and **`0x04` overrules it**: reply
with the authoritative `(X,Y)` and the prediction is cancelled. That is the whole of our wall collision, and
it works under fast-move too (the "client already moved" case still gets corrected on a block).

RTK's `clif_parsewalk` refuses with a three-packet sequence, and the same one covers *both* a blocked tile
and a status hold — `if (sd->canmove || sd->paralyzed || sd->sleep != 1.0f || sd->snare)`:
```
clif_blockmovement(sd, 0);   // 0x51, flag 0
clif_sendxy(sd);             // 0x04, authoritative x/y + view anchor
clif_blockmovement(sd, 1);   // 0x51, flag 1
```
**`0x51` has no 4.95 handler** — it maps to the no-op in the world dispatcher (`remap[0x51−3] = 0x2a`), and
the player-state dispatcher rejects it before the table is even consulted (`0x48eb51: cmp esi, 0x4a; ja
<no-op>` on `opcode − 4`, so it only accepts `0x04`–`0x4E`). It isn't the part that does the work: the
`clif_sendxy` in the middle is, and that is a plain `0x04`.

Two things matter when refusing:
* **Send something.** The client has already predicted the step and blocks awaiting the ack. Returning
  silently freezes it permanently, not for the duration of whatever you were enforcing.
* **For a hold, snap to the SERVER's `(X,Y)`, not the client's reported tile.** A held player (asleep or
  paralyzed) gets `0x04` at the server's own tile (`Server/Session.Movement.cs:139-151`): a normal step trusts
  the client's claim (above), but a hold must not, or a client that keeps reporting a tile further along
  creeps one step per packet. Every other refusal (a blocked step, a refused warp) snaps to the client's
  reported from-tile when it lies on the map, the tile a good step starts from (§10.2 steps 2 and 4;
  `:174-180`, `:262`, `:334`). Corrected 2026-10-24: this bullet used to say every refusal snaps to the
  server's tile.

RTK gates four things on `paralyzed || sleep`: `clif_parsewalk`, `clif_noparsewalk` (its click-to-move),
`clif_parseattack` (silent `return 0`), and `case 0x0F` magic (silently skipped). Turning is folded into its
walk parser; on 4.95 it is a separate `0x11`, and refusing it needs **no** snap-back — a turn is
fire-and-forget with no ack pending, so simply not broadcasting it is enough.

### 10.1 Choosing the mode: the session's fast-move flag, not a per-walk bit

**Corrected 2026-10-21 to what the server does** (`Server/Session.Movement.cs` `HandleWalk`; the client side
is in [`Fast-Move.md`](Fast-Move.md)). This section used to say to read the mode from the high bit of each
walk's step counter (`dec[1] & 0x80`) and not to track the toggle. The server does the opposite, because that
bit is never set on the wire: reading it per walk sent almost every step down the slow path (`HandleWalk`
quotes the production logs: 32,924 of 33,219 commits). In the binary the bit belongs to a client walk handler,
`0x497290`, which requires `0x80` on its direction byte and strips it, and only when the fast-move flag
`[state+0x451]` is 1 (`0x4972e9`–`0x4972ff`). That the client never puts it in the network walk packet rests on
the server's logs; it is not settled from the binary (unconfirmed).

- **The mode is one session flag, `Session._fastMove`, kept in lockstep with the client's runtime flag
  `[state+0x451]`:**
  - at world entry it is restored from the character's `SettingFlags` bit 9 (`Session.HandleArrival`);
  - every `0x08` stats packet carries it as body byte 46 (`Session.SendStats`), which the client copies into
    `[state+0x451]` (`0x48fd3a`), so the entry burst sets the client before its first step and every later
    stats packet re-asserts it;
  - the client's `0x1b` setting `0x09` (sent after the client flips its own flag) flips `_fastMove`, persists
    bit 9 and answers with the minitext `Fast Move        :ON` or `:OFF` (`Session.HandleSetting`).
- **`HandleWalk` reads that flag:** `clientFast = FastMoveTrustToggle ? _fastMove : (dec[1] & 0x80) != 0`.
  `FastMoveTrustToggle` is the server knob `P1998_V495_FASTMOVE_TRUST_TOGGLE`, **on by default**. At 0 the
  server reads the per-walk bit again and sends stats byte 46 as 0, so both sides fall back to the slow,
  server-authoritative mode together.

The client half of the old text stands: the client boots its flag OFF and never reports its state on connect,
which is why the server carries the persisted value and sets the client from it. A new character's
`SettingFlags` include bit 9 (`Shared/Character.cs`), so a new player starts in the fast mode. The same bit
seeds the Options menu's Fast Move box (§7.1, the Options menu).

### 10.2 One step as the server handles it: source tile, the two acknowledgements, corrections, pacing ✅

**Corrected 2026-10-21** (`HandleWalk`). This section used to say to send the walker nothing on a good fast
step. Half of that stands: in fast mode the client draws the step itself, legs and camera. But its own
key-driven step leaves the walk gate latched until a `0x04` arrives, so with no answer it stalls (the comment at
`HandleWalk`'s fast branch puts the stall at `frameCtr` 2, the "quarter-step" symptom). Per walk (`0x32` or
`0x06`; `dir` at `dec[0]`, the client's believed tile at `dec[2..5]`), in this order:

1. **Holds.** Asleep or paralyzed: a `0x04` at the server's own tile (`SendXy`) and nothing else (§10.0).
2. **Source and destination.** The source is the client's reported tile when it lies on the map, otherwise the
   server's position; the destination is one tile from the source in `dir` (0 N, 1 E, 2 S, 3 W). The facing
   is set from `dir`.
3. **Warps and scripted doorways** on the destination win over collision (`EnterMap` sends the
   `0x15`/`0x04`/`0x33` entry trio). A warp whose entry requirement fails is refused like a block, below,
   with a minitext.
4. **The step** (`World.TryMovePlayer`). Off the map, terrain (the ground pass flag, or the `SObj.tbl` wall for
   this heading, §12), a tile a Human Barrier has closed to this player (`BlockReason.Barrier`), or an occupant
   (a mob or another player, under the ghost rules in `TryMovePlayer`) refuses it. **A refused step is the
   correction, in either mode:** the server sets its position to the source tile and sends `0x04` there with
   the normal view anchor (`SendXy`), which cancels the client's prediction.
5. **A good step.** The server's position becomes the destination. Peers on the map get `0x0C` naming the
   **source** tile (`MoveEntity`, because the 4.95 `0x0C` walk ends one tile past the tile it names). The
   viewport sync (peers, mobs, ground items) and the terrain push (`StreamViewport`, §10.7) run. Then the
   walker gets one acknowledgement:

| Mode | Acknowledgement | Its coordinates |
|---|---|---|
| fast (`clientFast`) | **`0x04` with both view anchors `0xFFFF`** (`Session.SendXyCommitNoScroll`) | the **destination** |
| slow | **`0x26` self-walk** (`Session.SendSelfWalk`, §10.3) | the **source** tile, with the view anchor for the new position (`ViewAnchor`) |

The fast `0x04` releases the step without touching the camera the client has already scrolled: the `0x04`
handler `0x44faf0` runs the camera function `0x44c660` and then the walk completion `0x44b140`; an anchor
outside the viewport fails the bounds gate `0x44c8f0`, so the camera code does nothing, while `0x44b140` moves
the self's logical tile to the destination and clears the walk-active gate (the comment on
`SendXyCommitNoScroll`, `Server/Session.Entity.cs`). The client draws the legs and the scroll itself, and
realm-center (§10.5) is unaffected.

**Pacing.** On these default paths the server adds no delay and no step-rate limit; the acknowledgement goes
out at once and the client, which waits for it (§10.0), sets the pace. The delays `P1998_V495_ACK_MS` (360) and
`P1998_V495_WALK_MS` (200) belong only to the legacy drive modes, `P1998_V495_SELF_MOVE` other than its default
7 and `P1998_V495_SLOW_MOVE` other than its default 5. (A 5.33 client always gets the `0x26`.)

### 10.3 Fast-move OFF = server-authoritative — answer with `0x26` self-walk ✅

> **Game-channel `0x26` only.** This self-walk `0x26` is unrelated to the **login-channel** `0x26`
> (password change, §4.1 item 5) — different connection, different dispatcher, same byte.

The client will **not** move until the server assigns the tile. Answer each walk with **one `0x26`
self-walk packet** — the *same primitive 5.33 uses* — sent from the tile being confirmed:
```
0x26  dir(u8)  X(u16BE)  Y(u16BE)  viewX(u16BE)  viewY(u16BE)  flag(u8)
```
This gives smooth legs, continuous movement, and a correct camera (realm-center honored) — matching
fast-move ON. Server code: `SendSelfWalk(dir, fromX, fromY)`.

> **`0x26` is NOT dead on 4.95 — the "no-op" belief was wrong.** Its *main dispatch-table* handler
> `0x44fb80` really is `mov al,1; ret` (a no-op), but that is not the whole story — exactly like the `0x08`
> stats opcode. The client **pre-dispatches** `0x26` through the self-entity **vtable** (slot `+0x38` at
> `0x4cf038` → self-move dispatch `0x48eb40`) to **`handlerB` `0x4903d0`**, which:
> 1. **move-commits** the pending step (`0x48f160`) to `(X,Y)` — completing it **without** the camera
>    scroll a `0x04` forces; move-commit stores the packet's `viewX/viewY` as the camera anchor, so
>    realm-center is respected exactly like the fast-move-ON local path; and
> 2. starts the next step's leg cycle (`selfWalkAnim 0x48f2c0`) and sets **`entity+0x65f3 = 1`** — the
>    "complete locally, don't wait" flag — so the animation runs `0→3→0` and finishes on its own.
>
> This is why `0x26` works where `0x04` fights the client. The self-walk state machine (`0x48eef4`)
> advances `frameCtr` each tick; **at `frameCtr == 2`, if `entity+0x65f3 == 0` it sets the "waiting"
> flag `entity+0x65f4 = 1` and freezes** until the server unblocks it — that freeze is what made every
> `0x04`-based approach stall or jerk. `0x26` sets `0x65f3 = 1`, so it never waits. Confirmed live
> (`handlerB(0x26)` + `moveCommit` fire per step; the same dispatch also handles the client's *own* local
> walk commands as sub-op `0x0b` → `handlerA` — so **`0x26` is literally the server-side sibling of the
> client's local walk command**).

> **Do NOT use `0x04` or `0x0C` to *drive* self-walk on 4.95.**
> * `0x04` is the **re-anchor / teleport** primitive (§10.6): its handler *always* runs the camera-scroll
>   (`0x44c660` writes a decaying settle-offset even when the origin is unchanged = a jerk) and it cannot
>   complete a step without that scroll. Reserve it for corrections and hard refresh.
> * `0x0C` (`0x462320` start-walk → `0x44b140` walk-render) renders
>   `screen = logical(dest) + forward_step*(frameCtr/4)` — a guaranteed forward OVERSHOOT for the self. It
>   is for animating *other* entities. Normal walk speed constant is 80.
>
> (Legacy `0x0C`/`0x04` walk attempts survive only behind `P1998_V495_SLOW_MOVE` (0–4) for comparison;
> the default `5` is the `0x26` path above.)

### 10.4 Turn (`0x11`) — first press turns, second press walks

In NexusTK the **first** key press in a new direction turns you in place (no step); only the **second**
press walks. The client sends **`0x11`** (`side(u8) pad`) for the turn. Echo it back with the entity id so
the client turns: the recv handler `0x450350` reads `id(u32)@+1, side(u8)@+5`, looks the entity up, and
calls its turn method `0x462410`. Build `Be32(id), side, 00` (same as `SendSide`). Dropping `0x11` leaves
facing unconfirmed until the next walk ("press a new direction, first step goes the OLD way").

Facing is **persisted**, in `Character.Dir` — `Session._facing` is a view over it, so every walk/turn/warp
that assigns facing rides the character blob exactly the way `X`/`Y` do, and the entry sequence's `0x33`
draws the player looking the way they logged out. It used to be a session field, which is why every login
snapped everyone back to north.

### 10.5 Realm-center camera lock (F4 / `0x1b` sub-cmd `0x07`)

A client-side camera mode. The client sends **`0x1b`** with body `07 00 00` (F4). The server signals the
state via a byte in the `0x15` mapinfo packet (our `SendMapInfo` body[7]; the client's `0x15` handler
`0x44f8b0` reads it and feeds `(realm==0)` to the view rebuild `0x44c570`, storing it at `view+0x400`). A
second gate `entity+0x1dd` (from a settings bitfield) must also be set for the camera-clamp block to run.
Works together with fast-move ON. `0x1b` also carries other toggles: sub-cmd `0x09` = fast-move.

### 10.6 Hard refresh (`0x38`, Ctrl+R) — this is what `0x04` is *for*

Ctrl+R sends **`0x38`** (`38 00`) and grays the screen while the client waits for the server to re-assert
authoritative state. Mirror RTK's `clif_refresh`: re-send **`0x15` mapinfo** (on 4.95 this triggers a
client **map reload**, which clears the gray mask) + **`0x04` xy** + **`0x33` self** + re-draw nearby
entities. Server code: `HandleRefresh` (same in-place refresh the F4 toggle performs).

`0x04` is the **re-anchor primitive**: `X, Y, anchorX, anchorY, 00` → authoritative position + a fresh
camera origin `(X−anchorX, Y−anchorY)`. RTK's `clif_sendxy` always computes a **centered / edge-aware**
anchor (`x<8 → x`, `x ≥ xs−8 → x−xs+17`, else `8`; y analogous), with **no** realm-center handling — so a
refresh **recenters** the player even if realm-center had them parked off-center. We match this: if realm is
on, `HandleRefresh` re-locks the freeze at the new centered origin so it recenters *and* stays locked. RTK
also emits a trailing `0x22 03` terminator, but `0x22` is the default no-op on 4.95 (remap slot `0x2a`), so
it is not needed to end the refresh.

### 10.7 Terrain streaming (`0x05` → `0x06`) — 4.95 streams its map too ✅

**Corrected 2026-08-07.** This document previously asserted that the 4.95 client draws terrain *only* from
its local `Maps\TK<id>.map` files and that `0x05` was unused on 4.95. Both were wrong. The client sends a
**map-data request** exactly like 5.33's, and one session log contains **2,161 of them**, every one dropped
by the server with `?? 0x05 with no V495 handler`:

```
dec : 00 00 00 00 0c 0c 00 63 c2 00     x0=0    y0=0    w=12 h=12   (at connect)
dec : 00 6d 00 7e 13 11 00 d1 1c 00     x0=109  y0=126  w=19 h=17   (viewport + pad)
```

The local `.map` files are a **disk cache the client fills from this stream** (§10.8), not its only source,
which is why some 4.x client distributions ship with an empty `Maps` directory.

**The 4 trailing bytes are NOT a checksum — they are uninitialized stack.** This was assumed to be a view
checksum (a plausible cache-validation hook: compare it server-side and skip streaming when the client's
copy is already right). A Frida capture of 30 packets alongside the client's exact cell array
(`re/frida_mapchk.py`) disproved it outright:

* the *same* cell array produced `0x0000`, `0x82e7` and `0xda28` on consecutive steps;
* two *different* arrays both produced `0x2638`;
* map 3800 produced `0xbbbb`, a repeated fill byte, and `0x0000` recurs across unrelated states.

They are deterministic only in the sense that the same preceding code path leaves the same residue on the
stack — which is what makes them look content-derived if you only sample a little. **There is no
cache-validation mechanism in this protocol**; the server cannot tell what a client already has cached, so
it streams what the viewport needs and accepts the redundancy.

The **request** is identical on both clients. The **reply** differs only in cell width, because the two
clients pack passability differently:

| | reply body |
|---|---|
| 5.33 | `x0(u16BE) y0(u16BE) w(u8) h(u8)` + `{ tile(u16BE) pass(u16BE) obj(u16BE) } × w·h` |
| 4.95 | `x0(u16BE) y0(u16BE) w(u8) h(u8)` + `{ ground(u16BE) object(u16BE) } × w·h` |

4.95's cell is the same shape the door cell-patch already sends. For a cell the server has not overridden, its
`ground` is the `.map` file's own ground word, sent unchanged (`MapData.GroundWord` → `TileTranslation.Ground`,
which returns a 4.95 word as is; `MapCell.Write` packs it), so the streamed cell is byte-identical to what the
client would have read from its own copy. **That word is a two-sheet tagged union, not a tile with flags on
top** (§12; `Server/TileTranslation.cs`): `0` draws nothing, below `0xC000` it is `TileA[v − 1]`, from `0xC000`
up it is `TileB[v − 0xC000]`, and the top two bits double as the passability flag, because every blocked cell is
on sheet 2. Masking the word to 14 bits before using it as a tile was the server's own rendering bug: it turned
every sheet-2 cell into an unrelated sheet-1 tile.

**An overridden cell can change sheet on the wire.** The server keeps each cell as a 14-bit index and the 2-bit
pass flag and re-packs them as `(index & 0x3FFF) | (pass << 14)` (`MapData.GroundWord`, `MapCell.Write`), so a
cell whose pass flag the server has changed goes out on the other sheet: pass 0 on a sheet-2 cell sends a
sheet-1 word, pass 3 on a sheet-1 cell a sheet-2 word. That happens for an authored `game-data/MapCells.csv` row
that sets `Pass` (six cells today), a `Doors.csv` ForceOpen tile (none today), a persisted runtime cell whose
pass changed, and, while a GM has `@clip` on, every blocked cell (`Session.SendMapRect` streams pass as 0, so
each sheet-2 cell goes out as sheet 1 until clip is turned off and the window is re-sent).

No leading flag byte on either client. Receive handler `0x44fb90` writes
each cell into the client's **live map array** and then redraws the patched rect, so the write itself is not
viewport-gated and `height > 1` is fine — the door path only ever sends one row because that's all a door
needs. Server code: `Session.HandleMapRequest`, one branch per `_ver`. Requests arriving before `0x15`
enter-map (the client fires two `(0,0) 12×12` at connect) are ignored; it re-asks once in-world.

**A reply is never more than one frame.** The frame's u16 length counts op + inc + body (§2), and the body is
6 + 4·w·h bytes on 4.95 or 6 + 6·w·h on 5.33, so past 16,381 cells (4.95) or 10,921 (5.33) the length would
wrap. `Session.SendMapRect` clamps such a reply by rows: `x0`, `y0` and `w` stay as asked, `h` becomes the most
rows that fit (at least 64 on 4.95 and 42 on 5.33, since `w` is a byte), the header names exactly the cells
sent, the stream's coverage tracker records what was sent, and the log line says `CLAMPED to one frame`. A rect
that fits goes out unchanged. Only a hand-built request is that big (the client asks about 18×16, the server's
own window is 27×25), and only six maps are big enough to answer one (seven on 5.33, where Islets' 80×200 is
too); it gets fewer rows than it asked for.

**The client re-requests as it scrolls — roughly every 5 steps.** Measured over one 61 MB server log:
2162 requests / 10598 walks (0.204) in sessions where the server never replied, and 139 / 810 (0.172) after
it started replying — the same rate either way, so this is scroll-driven, not retry-on-silence. Each request
is sent TWICE (identical rect and body); consecutive distinct rects step by ±1/±2 tiles. An earlier version
of this section claimed the client only ever requested on map entry; that was wrong.

**But it does not always re-request, and the gap is what causes black walls.** On map 1000 (18x25) a player
entered at (8,23), got one rect (0,9) 18x16, then walked 11 tiles north to y=6 — into rows 0-8, which were
never sent — with **no** further request. Map 41 (60x60) re-requested normally over the same kind of walk.
The distinguishing feature is unconfirmed; the obvious candidate is that map 1000 is exactly as wide as the
viewport, so its `x0` is pinned at 0 and only `y0` could change. Because of that gap the server also PUSHES
terrain as the player moves (`Session.StreamViewport`): after each committed step it emits only the newly
exposed strip (~21 cells, ~84 bytes) rather than the whole viewport (~1.3 KB). The tracker holds the LAST
window rather than a running union of everything sent — a union is a bounding box, and a bounding box claims
coverage it never sent (walk far north, then east: the new eastern columns only went out for the current
rows, yet the box marks them covered for the whole accumulated height).

**This push overlaps what the client asks for**: 755 replies were sent against 139 requests in the measured
window, so most of the traffic is server-initiated and redundant on maps where the client re-requests
properly. That redundancy was once gated behind an 8-step "the client hasn't asked recently" grace —
**and the grace was wrong** (removed 2026-08-09, user-reported "black boxes I can almost walk to").
Deferring to the client cannot work, because the client's own request is only **18×16** around itself while
it *draws* **19×17** (viewport builder `0x44c950` extends one tile past the 17×15 view on every side; see
§11b.1): its request buys about one tile of margin, and it re-asks only every ~5 steps,
so two or three steps in a straight line puts the drawn edge on cells nobody sent. The push now runs on
**every** committed step, with the window widened to **27×25** (≈4 tiles of lookahead past the drawn edge).
The cost is per-strip, not per-window: one step exposes one 25-cell column ≈ 100 bytes.

**Map ENTRY is the worst case, and used to have no push at all** (`Session.PrimeViewport`, added 2026-08-09).
`EnterMap` reset the coverage tracker and then relied entirely on the client's own `0x05`. On a map the
client has never visited its cache file is freshly zero-filled, so you arrived on a small island of real
tiles with black in every direction, and the walk-driven push only repaired it one strip at a time as you
walked into it. The full window is now sent immediately after the `0x15`/`0x04`/`0x33` entry trio on login,
on warp, and on `Ctrl+R` refresh — ~2.7 KB, once per map entry. (Not on the F4 realm-center refresh: no map
change, and §10.8's memory-mapped cache means the streamed cells survive the `0x15` reload.) Ordering is
safe because the client parses sequentially: the `0x15` has finished mapping the file before the `0x06`
cell writes are parsed.

`NoteStreamed` also refuses to *shrink* the tracked rect — a rect that falls entirely inside the tracked one
adds no coverage, so the larger one is kept. Without that, every client `0x05` (18×16) would clobber the
tracker and the next step would re-send the whole margin back out to 27×25. This is not the union the
paragraph above rejects: the tracker still only ever holds a rectangle that was sent *whole*.
`P1998_V495_PUSHGRACE` restores the old deferral; `P1998_V495_PUSHMAP=0` disables the push entirely.

### 10.8 The client's map cache is a memory-mapped file ✅

`Maps\TK<id>.map` is not a read-only asset — the client opens it **read/write** and maps it:

```
CreateFileW("Maps\TK%d.map", GENERIC_READ|GENERIC_WRITE, 0, NULL, OPEN_ALWAYS, FILE_FLAG_RANDOM_ACCESS, NULL)
      -> [map+0x3e4]      ; OPEN_ALWAYS creates it when missing; CreateDirectoryW("Maps") runs first
   ... WriteFile zero-fill to extend, or SetFilePointer+SetEndOfFile to truncate, to the exact map size
CreateFileMappingW(hFile, NULL, PAGE_READWRITE, 0, 0, NULL)    -> [map+0x3e8]
MapViewOfFile(hMapping, FILE_MAP_ALL_ACCESS, 0, 0, 0)          -> [map+0x3ec]
```

`+0x3ec` is the live cell array the `0x06` receive handler writes into, so **every streamed cell is
persisted to disk by the OS** — there is no explicit write in the network path. Open path `0x44a780`
(called from the enter-map path `0x44c5e0` with dims already stored at `+0x3e0/+0x3e2`); close path
unmaps and closes with **no** `DeleteFile`, so the cache is never cleaned up.

Map-object layout: `+0x3de` id · `+0x3e0` width · `+0x3e2` height · `+0x3e4` file handle · `+0x3e8` mapping
· `+0x3ec` mapped view. Cell accessor `GetCell(out,x,y)` at `0x44cbc0` (bounds-checks then `[cells+i*4]`).

**Gotcha — UAC VirtualStore.** The path is relative and the client is a 2001-era 32-bit exe with no
manifest, so on modern Windows a non-elevated client writing into `C:\Program Files (x86)\...` is silently
redirected to `%LOCALAPPDATA%\VirtualStore\Program Files (x86)\Nexon\NextAeon\Maps\`. Reads are redirected
too, and the virtualized copy WINS. Deleting the real `Maps` directory therefore does **not** give you a
clean test — stale caches there will still be drawn. Clear the VirtualStore copy as well.

---

## 11. Speech & actions

**Speech:** client sends `0x0E` = `chatType(u8) msgLen(u8) msg`. Echo it back as `0x0D` =
`chatType(u8) entityId(u32) msgLen(u8) msg` attributed to the speaker's entity id → bubble appears
over their head.

**Name-prefixed text + shout — LIVE-CONFIRMED 2026-07-27.** `Session.HandleChat` prefixes the broadcast
text server-side before it goes out over `0x0D`, since the client renders exactly the bytes it's given in
both the bubble and the chat-log line (there's no separate "speaker name" field for the client to prepend
itself): `"Name: message"` for ordinary speech, `"Name! message"` for shout. The client's own keybind help
(extracted `Str_Eng.res`, `re/str_eng.res:102/105`) documents dedicated single-key hotkeys — **`'`** = Say,
**`!`** = Shout — confirming shout is a real, distinct native input mode and not a private-server invention.
`chatType` (the `0x0E` byte the client sends) is forwarded to `0x0D` **unchanged**: the hypothesis was that
whatever value the client uses internally to pick the Say/Shout hotkey is also what it uses to pick the
bubble/chat-log color on playback, so echoing it back would render correctly without the server inventing
its own color scheme — **confirmed live**: ordinary speech renders as plain "Name: message", and shout
renders red in the chat box with a yellow over-head bubble, using nothing but the passthrough `chatType`
byte + the server-side text prefix. The code treats any nonzero `chatType` as shout (`bool shout = chatType
!= 0`) rather than hardcoding a specific value, since that's what was tested and works. Whisper (`"`,
Shift+`'`) is a fully separate native opcode (`0x19`, see below) — not a `chatType` value on `0x0E`.

**Attack:** client sends `0x13` (bare `13 00`) on spacebar. The swing is a **`0x1A` action, type=1**. The
server→client `0x13` is a *different* packet — the combat **damage / over-head HP bar** (§7.2) — sent at the
hit resolution, not as the swing. (Historical trap: a *bare/zero* `0x13` gives `critical=0` → animation
`0x8f − 0 = 0x8f`, a "death flash"; that scared us off replying `0x13` at all until the handler was fully
decoded — the fix is simply to send a real `percent`/`critical`, which is what combat now does.)

**Emotes (`:` wheel).** Client sends `0x1d` = `idx(u8) 00`. The emote plays as an action,
`type = idx + 11`, sent as `0x1A` (RTK `clif_parseemotion`: `sendaction(&bl, RFIFOB(5)+11, 0x4E, 0)`).
- **To the emoter** (`Server/Session.Combat.cs:472`).
- **To every peer in RTK's SAMEAREA box around the emoter, not the whole map** (`Server/Session.Combat.cs:473`). The box is ±9 tiles across
  and ±8 down, and it is edge-shifted: against a map edge it slides inward rather than being clipped. It is sent
  with `World.BroadcastSameArea` (`Server/World.cs:1648-1649`).

So `:` `l` = `idx 0x0b → type 22 = dance`.

**Only an alive, visible, unmounted and untransformed player emotes.** For a dead, invisible, mounted or
transformed player, the server drops the `0x1d` silently (`Server/Session.Combat.cs:467`):
- no `0x1A` to the emoter or to any peer;
- no line;
- invisibility is not broken.

RTK plays the emote only when `status.state == 0` (`clif.c:6477-6482`).

The index sits at `dec[0]` — **not** `dec[1]`; 4.95 has no ordinal byte after the opcode, so
RTK's 6.x `RFIFOB(5)` offset is one byte later than ours (the same shift seen on every 4.95 handler).
The dance/emote sound rides along with the client's action sprite; no separate sound packet is sent.

**Weapon.** `Character.Weapon` renders in the player's `0x33` type-0 appearance slot `[5]` (the sword/blade
slot — see §8), persists in the store, and drives the melee damage bonus. This works (it draws on the
player, which renders). With no weapon the space-bar attack plays the empty-handed *throw* animation/sound.

**Combat (server-authoritative).** The server owns mob HP (`Shared/Mob.cs` + `World`). On client `0x13`: send
the player swing (`0x1A`), then resolve melee against the mob on the tile *in front* of the player (facing
tracked from the last walk `0x32`); apply `might + weapon bonus`, broadcast the **`0x13` over-head HP bar + hit
spark** to the whole map (`ShowDamageResult`), and on death send `percent=0` (empty bar + final spark) then a
**delayed `0x0E`** despawn (the "death beat" — 4.95 has no monster death frames, see §7.2) + exp via a fresh
`0x08`. Spell damage (`CastDamage`/`ApplyCastGeneric`) shows the same HP bar on top of the cast effect. **This
targets real `0x07` monsters (§7.2, §11a) — visible, with collision, killable.** Verified end-to-end live:
spawn → melee/spell → HP bar drains → death beat → despawn → "You defeated" + exp.

## 11a. Monster rendering — the sprite category system ✅ SOLVED (`0x07`)

The client's sprite manager (`[0x4fd2f8]`) groups sprites into **categories**, each backed by an EPF archive
from `NexusTK.dat`, all loaded at startup (registry table `0x47b4c6`). The category name is a **wide (UTF-16)
string** — e.g. `"ITEM"` (`0x4f1fe8`), `"MONSTER.EPF"` (`0x4f1d18`), plus BODY/HEAD/FACE/tiles/shields/swords.
A sprite is resolved by `(categoryName, id)`; the item resolver `0x435ab0` does `id + 0x4000` in `"ITEM"`
(`0x431020`), while the **monster resolvers `0x434020` / `0x4342e0`** push `"MONSTER.EPF"` and resolve the
frame through `0x433d00` (Monster.tbl lookup — no fixed offset; it's a table map).

**The answer: opcode `0x07`** (handler `0x44fdb0`) is the monster/creature-list spawn (full layout in §7.2).
It builds each entity with the **same factory `0x44d7d0` that `0x33` uses**, but the look descriptor decides
the archive: **`look ∈ [0x8000, 0xbfff]` ⇒ descriptor type 1 ⇒ creature entity (vtable `0x4cd098`)**, whose
draw `0x461c70` (`[ent+0x178]!=0`) routes to the monster resolver → Monster.epf. So `look = 0x8000 | monsterId`
(`monsterId` = the Monster.tbl index 0..326). How it was found: enumerated every call site of the generic
sprite lookup `0x431020`/`0x430de0` → located the two that push `"MONSTER.EPF"` (`0x434020`/`0x4342e0`) →
walked callers up to the draw methods in vtable `0x4cd098` → to its ctor `0x461a50` → to the factory `0x44d7d0`
→ to its only two callers: the `0x33` handler (`0x44fef0`, players) and the **`0x07` handler (`0x44fdb0`)**.

**Why not `0x33` or `0x16`:** all three `0x33` renderKinds (1/2/3) call `0x463380` with the *player* archive
`0x4f2a84`, so `0x33` can never draw a monster in 4.95 (a gap from 7.x — see §17). `0x16` builds the item/object
class (vtable `0x4cd18c`, category `"ITEM"`), invisible for monster ids.

Monster frame layout is in `Monster.tbl` (plain text: `NumMonsters 327`, per-id `Palette/Starting/Walk/Attack…`;
`Starting` = idle frame). Live sweep confirmed frame = `6 + 20·monsterId` for the early monsters (the idle
"Starting" frame + a walk-cycle offset). Parse `NexusTK.dat` with the Nexon PAK format: `u32 count` then 17-byte
entries `{u32 offset, char name[13]}` (first offset == header size).

**Commands** in `Session.HandleChat`: `@cre <lookId> [hp] [color]` (one real monster in front, killable;
`color` = the `0x07` colour/recolor byte, see §11a.1), `@crecol <lookId> [loColor] [hiColor] [step]` (sweep
the SAME look id across colour-byte values as a **grid**, 12/row, default `0..23`; the colour byte visibly
wraps mod-24 with only 0-19 real), `@crow <lo> <hi> [step]` (row sweep of the Monster.tbl look space),
`@spawn [lookId] [hp]` (a pack), `@kill`, `@ride`/`@mount [0|1]` (get on/off a horse — form
byte 3, §8). `@mob <look> [hp] [color]` spawns a **shared world** monster (`SummonWorldMob`, stationary —
see below); the raw-sprite `0x16` probes (`@mobraw`, `@mobrow`) are kept for item/object discovery.

**Navigation & content commands** (data-driven, backed by the `Content` registry — §17.3): `@warp <name|id>
[x y]` (fuzzy-match a map by name or id, optionally with coords, and enter it), `@go <x> <y>` (jump to a tile
on the map you are already on — the Tester-tier half of `@warp`, and the one teleport that reaches nowhere you
couldn't have walked to; anything that isn't two in-bounds integers — a missing argument, a word, a coordinate
off this map's edge — lands you on `(0,0)` rather than refusing, and the reply says which happened. It runs
through the same `EnterMap` same-map jump the Rogue leap spells use, because the `0x15`/`0x04`/`0x33` entry
trio is the only proven way to relocate the self entity: a bare `0x04` is a one-tile snap-back, not a
teleport), `@maps [query]` /
`@mobs [query]` (fuzzy list maps / mobs), `@summon <name|id>` (spawn a mob from the registry by name/id, into
the shared world with wander/respawn-less one-off AI), `@rabbit` (one wandering, killable rabbit in front of
you). The persistent, auto-populated spawns (with respawns + drops) are separate — see §11b.1.
All of these print their output as **over-head speech (`0x0D`) from the player's own entity** — the
`SendLog()` helper — because `0x02`/`0x0F` are single-line message *boxes* that can't stack for a list;
`0x0D` is the in-world chat-log channel (handler `0x450170`, 3000 ms bubble). **Door/portal warps** fire
automatically in `HandleWalk`: a step onto a `(map,x,y)` that has a registry warp calls `EnterMap` and
overrides collision (checked *before* the blocked-tile test).

**Exhaustive client-data audit (2026-07-24):** confirmed monster/item **names and stats are NOT stored
client-side anywhere** in the 4.x install — parsed every archive with the Nexon PAK format directly.
`NexusTK.dat` (64 entries): `Monster.tbl`/`Item.tbl` are rendering metadata only (`Palette/Starting/Walk/
Attack/Delay/Shadow` and `Palette/Alpha/Light` respectively — no name field on either, 327 monsters / 1310
items). `Str_Eng.res` is generic `%s`-templated UI prompt text, not a name lookup table. `Inter.dat` (165
entries) is 100% UI chrome (dialogs/buttons/fonts/login art) + localized menu strings + 8 nation `.des`
blurbs — nothing entity-related. `.map` files (1750) are pure tile grids, `NexusTK.snd` is audio (same PAK
header, 197 entries). So names/stats are server-supplied text only — confirms the Nexus Atlas scrape +
matcher tool (`re/monster-matcher/`) is the only path to a name/stat table, not a shortcut we're missing.
PAK tools: `re/pak_list.py <path.dat>` (directory listing), `re/pak_extract.py <path.dat> <EntryName>
[outFile]` (extract one entry).

### 11a.1 The `0x07` color byte = a pure palette-swap (recolor), applied in a deferred blit ✅

The 12-byte `0x07` entry ends with a **`color` byte** (offset +10) then **`dir`** (+11). Live testing
(`@crecol 27 0 4` → 5 visibly different-coloured bulls, frame identical) proves **`color` is a real,
per-spawn recolor** — same sprite, different palette. This is confirmed independently three ways:

- **Field layout (static arg-trace `0x44fdb0 → 0x44d7d0(factory) → 0x461a50(ctor)`):** `look` and `color`
  travel as **one packed dword** `{u16 look, u8 color, u8 pad}` stored at `entity+0x17c`; so the colour
  byte lives at `entity+0x17e`, and `dir` at `entity+0x18d`.
- **Colour is NOT consumed in the resolve path** (so it is purely a palette op, never a frame/geometry op):
  a full-binary xref of displacement `0x17e` = **zero real reads**; all five `entity+0x17c` dword readers
  pass the whole packed value into the sprite resolvers, which use only the low 16 bits (look). All three
  resolvers (`0x433d00` frame = `Starting + dir*Walk`; `0x4342e0`; `0x434020`) call
  `catlookup(MONSTER.EPF, frame, out)` with no colour.
- **Live hook on `0x433d00` dumping its output descriptor:** byte-identical across colours 0-4 —
  `[id, Palette=0, Starting, Walk, Attack, Delay, Shadow]` = the `Monster.tbl` row, whose `Palette`
  column is the *default per-monster* palette, independent of the spawn colour byte.

⟹ The colour→palette-block selection happens in a **deferred blit stage** (the vtable `[esi+0x9c]`/`[esi+0xa0]`
draw calls at the tail of the resolvers) that was not pinned down at the instruction level — but the
*behaviour* is fully known and matches the reference server (below), so it did not need finishing.

**Range:** `@crecol` shows the client cycling through **24** distinct results before repeating (colour 24 ==
colour 0). Byte-scanning `Monster.pal` (count the `DLPalette` ASCII tags, don't divide file size) confirms
the file holds **exactly 20** palette blocks. So the client applies a hardcoded `% 24` clamp unrelated to
the real palette count; **only colours 0-19 are real recolors**; 20-23 read past the 20-slot array into
adjacent memory (stable but undefined garbage — not legitimate variants).

**Confirmed by the RTK reference server (§17.1):** RTK's creature-spawn packet is byte-identical
(`look = 0x8000|monsterId`, then a `look_color` byte), and it stores `look_color` as a first-class
per-monster DB field — recolors are the *same look with a different `look_color`*. Exactly our model.

**Frida hooks used** (`re/frida_probe.py`): `monfr` (0x433d00; correct arg map `args[1]`=packed look+color,
`args[3]`=dir, `args[7]`=out descriptor) and `monctor` (0x461a50). A page-protection software watchpoint on
the entity is also in there but is NOISY (neighbour heap objects share the 4 KB page); it also revealed two
Frida quirks on this target — `context.eflags` reads `undefined` (no single-step re-arm), and NativePointer
objects captured outside the `setExceptionHandler` callback read back as `0x0` inside it (store plain ints).

### 11a.2 Monster names / stats / colours — the data source ✅

Names/stats are **not** in the client (audit above) — they come from the **RTK reference server DB** (§17.1)
and the **Nexus Atlas** scrape (pre-6.5 exp values). Extract these **locally** with the tools in `re/` — the
data itself is **kept out of this repo** (logic-only server; the generated CSVs land in `game-data/`,
which is gitignored). RTK look-ids
validate against our own EPF shape-matching (rat=91, mouse=120, bull=27, rabbit=21, fox=22, wolf=23, bear=24,
squirrel=25). Colours ≤19 map to our `Monster.pal`; RTK colours >19 are 7.x-only and must be re-picked for
4.95 via `@crecol`.

---

## 11b. Shared world (multiplayer) — players see each other + the same mobs ✅

`Server/World.cs` is a single instance (created in `TkListener`, injected into every `Session`) that holds
**all connected players and all live mobs, grouped by map**. It turns the previously per-connection server
into one shared world: players on the same map see each other move/turn/speak, and everyone fights the
**same** server-authoritative mobs.

**Entity ids.** Every player is assigned a **unique** id from the world at arrival (`World.AllocatePlayerId`,
`1+`). Before this, every character defaulted to `Id = 1`, which collided on the broadcast key — the fix
that unblocked multiplayer. Shared mobs draw from a disjoint pool (`100000+`); the session-local *debug*
dummies keep their own `5000+` pool (only ever visible to their own client, so cross-session collisions
there don't matter). The self id still binds the client camera via `0x05` (`SendId`).

**Join / leave.** On world entry AND on every warp (`Session.EnterMap`): the newcomer draws everyone
already on the map (`0x33` per peer, `0x07` per mob) and `World.EnterMap` broadcasts the newcomer to them.
On disconnect/warp-out (`World.LeaveMap`) the player is despawned (`0x0E`) for the peers left behind.

**Broadcasts** (to all same-map players, usually excluding the actor): move `0x0C`, turn `0x11`, speech
`0x0D` (real chat only — `!`-commands stay self-only via `SendLog`), attack swing `0x1A`, combat damage /
HP bar `0x13`, cast effect `0x29`, spawn `0x07`, despawn `0x0E`. The moving player's OWN client is driven by
the self-walk modes (§10), so it's excluded from the `0x0C` broadcast.

> **`0x0C` for peers/mobs sends the SOURCE tile, not the destination.** The 4.95 client's `0x0C` walk always
> ends **one tile past** the packet tile in the walk direction (the forward-slide overshoot of §7.2/§10.3 —
> `logical = packet_tile`, then the render slides `+1` and commits there). For the self, `0x04` corrects it;
> for a peer/mob with no commit it sticks, so the entity renders a tile ahead of where the server has it —
> proven by live trace (server `(61,79)`, client drew `(62,79)`). Anchoring the packet on the **source** tile
> makes `client_final = source + forward(dir) = the true destination`. Both `MoveEntity` (peers, in
> `HandleWalk`) and `MoveMob` (mobs, in `World.Tick`) pass the pre-move tile for this reason.

**Shared mobs + combat.** `@summon` / `@rabbit` / `@mob` spawn into the world (`SummonWorldMob`) — `@mob`
with `wander: false`, so a calibration dummy holds its tile (it still turns, chases and swings back once you
hit it: the `!mob.Wander` gate sits *after* the chase/retaliate blocks in `World.Tick`). The rest of the debug
lab (`@cre`/`@mobraw`/`@mobrow`/`@crow`/look-lab) stays session-local. `HandleAttack` hits world mobs first: `World.TryDamage`
applies damage **under the world lock** so two players can't double-kill; the number + death despawn are
broadcast to all, **loot is rolled onto the floor**, the spawn point is scheduled to respawn, and **exp
(the mob's real `Exp`)** goes to the killer. Beyond the commands, the world is populated automatically — see
the spawn/AI system in **§11b.1**.

**Threading.** All of `World`'s collections are guarded by one lock; socket writes happen **outside** the
lock (recipient list snapshotted under it) and every cross-session send is exception-guarded, so a peer
whose socket just closed can't break a broadcast. Cross-session sends still go out through the target
session's own locked `Send()`, so bytes never interleave mid-packet.

**Gotcha — mapinfo refresh drops foreign entities.** Re-sending `0x15` in place (e.g. the realm-center
`0x1b 07` refresh) makes the client rebuild the map and **drop every foreign entity** — the self survives
via the trailing `0x33`, but peers/mobs vanish. So any in-place `0x15` resend must be followed by
`Session.RedrawWorld()` (re-asserts co-located peers + mobs via `World.View`). Since the 4.95 client sends
`0x1b 07` frequently, this refresh is also a source of walk jitter — throttling it to real state changes is
a movement-side TODO.

Mobs persist on a map after everyone leaves (they belong to the map, not a session); `@kill` clears the
current map's world mobs for everyone. **Players are viewport-streamed like mobs** (corrected 2026-10-24; this
used to say only mobs were): `Session.SyncPeers` (`Server/Session.WorldApi.cs:487`) runs the same show / despawn /
re-show decision mobs get, so a peer is drawn with `0x33` as it comes into view and despawned as it leaves. It
runs at world entry (`Server/Session.cs:1265`), after each of our own steps (`Server/Session.Movement.cs:370`),
on a warp, and on every world tick (`World.ReconcileViews`, `Server/World.cs:3539`). See §11b.1 for the gate.

---

## 11b.1 Persistent spawns, mob AI, collision & viewport streaming ✅

The world is populated automatically from **two** RTK spawn sources, not just by commands.

**Static spawn table (`Content.Spawns` ← `game-data/Spawns.csv`).** Each `SpawnDef` is `(mobId, map, x, y)`
— one live mob per point. This is only **1175 points across 19 maps** (Kugnae 526, Buya 408, a few specials): it
covers the towns and little else. RTK's `Spawns0` SQL table genuinely has nothing for the hunting maps.

**Excluded as "not classic" (`Content.ExcludedSpawnMobIds`).** A handful of `Spawns0` points exist only to host a
scripted storyline for a subsystem we haven't built, so spawning them is worse than not — a mute, purposeless mob
standing in for content that can never trigger. Currently: **729 `spy_hwan` "Hwan"** (Buya 330 @38,99) — a captive
NPC for the Spy subpath's interrogation questline (`NPCs/subpaths/spy/hwan.lua`); the player-subpath system doesn't
exist yet, so he's filtered out of `LoadSpawns` rather than left to wander aimlessly. Revisit if subpaths are ever
ported; add future finds of the same shape to that set rather than editing `Spawns.csv` directly.

**Excluded map range: "Buya Scorpion Cave" (`Content.ExcludedMapRanges`, maps 410-419).** RTK-authored reskin of
the classic **Kugnae Spider Cave** (maps 90-96), not original NexusTK content — same level-42 gate, same shared
generic mob pool (`carrion_raven` 99 / `pale_scorpion` 104 / `massive_scorpion` 105), with the spider-flavoured
ids swapped for scorpion ones (`giant_spider`→`vile_scorpion`, `radiant_spider`→`radiant_scorpion`, plus an extra
`scorpion_lurker`/`crimson_scorpion` boss room). Entrance was Buya `(68,93)/(69,93)`, a 10-room linear chain
(Sand Glen → Sand Edge → Scorpion Tail → Sting → Venom Den → Stream Sands → Sand Den → Desert Claw → South Sand
Den → Green's End) looping back out to Buya. Cut at the **warp-graph level**: `LoadWarps` drops any warp whose
source or destination map falls in an excluded range, so the entrance, every internal room-to-room link, and the
exit are all gone in one place — the maps/mobs/warps stay in the source CSVs untouched (lazy materialization means
nothing for them even loads), so trimming `ExcludedMapRanges` fully restores the cave. Same "flag, don't delete"
pattern as the Hwan exclusion above; add further RTK-only reskin dungeons here if found.

**Area spawns (`Content.AreaSpawns` ← `game-data/AreaSpawns.csv`).** *This is where every cave/dungeon gets its
mobs.* RTK spawns hunting-map populations from a Lua "spawner NPC" (`mobSpawnHandler.lua`), not the SQL table, via
`handleSpawn(npc, map, {mobIds}, {counts}, timer [,minX,minY,maxX,maxY])`. `re/extract_lua_spawns.py` parses those
617 calls into `AreaSpawnDef (mobId, map, count, box)` — **2371 rows, ~21.5k mobs across 767 maps** (Mythic Nexus
41, the zodiac caves — Ox Fury 176, Pig Path 181, Horse Valley 242 — wilderness, etc.). A zero box means "anywhere
walkable on the map"; otherwise the mob's home is a random walkable tile inside the box (`World.PickAreaHome`).
RTK's per-mob respawn `timer` is dropped in favour of the server's own cadence. *(Symptom this fixed: caves and the
Buya haunted-house/animal batch were empty because only `Spawns0` was loaded.)*

**Lazy materialization.** With ~21.5k mobs, `World.PopulateSpawns` no longer instantiates anything at boot — it just
builds a cheap per-map `Spawn` point list (static tiles known; area tiles chosen later). The first time a player
enters a map, `EnsureMaterialized(mapId)` (called under lock from `EnterMap`, before the room's mob list is read)
instantiates that map's roster and loads its map file once. A cave nobody visits costs nothing. `Materialize` still
places each mob via **`FreeSpawnTile`** — the home tile if open, else the nearest unoccupied non-solid tile within 2
— because several RTK points share a tile and a respawn can land where another mob wandered; without this they'd
stack. An area spawn's home tile is fixed on first materialize (respawns hug the same patch, like RTK sentries).

**Respawn.** On death `World.TryDamage` clears the spawn's `Live`, sets `RespawnTick = _tick + RespawnTicks`
(~18 s), and rolls loot. `World.Tick` refills any due point (only on maps with a player watching) via
`Materialize`, minting a fresh mob id.

**Drops (`Content.RollDrops`).** RTK's real per-mob drop tables, extracted from the server-side Lua
(`RTK-Server/rtklua/Accepted/Mobs/MobDrops.lua`) by `re/extract_mob_drops.py` into `game-data/MobDrops.csv`
and loaded into `Content.MobDrops` (382 mobs; a mob absent from the table drops nothing, matching RTK). Each
mob has independent `loot` lines (its own item/amount-range/percent, several can hit on one kill) and at most
one `rareLoot` line (rolled in listed order, only the first hit drops). A `GOLD` item key means a gold pile
(dropped as a `GroundItem` with `ItemId = -1`, same convention as a player's own `!` gold drop) rather than a
real item. Rolled under the world lock, added to the map's floor-item list, and broadcast as `0x07` ground
items (§11c) so anyone can pick them up.

**AI pacing (RTK-faithful).** `World.Tick` runs every 600 ms but a mob only *acts* when its own
**`MobMoveTime`** (ms, from the mob DB — rabbit/squirrel 3000, cat/fox/rat 2000) has accumulated in
`Mob.MoveTimer` (seeded random so they don't all move in lock-step). Even then it mirrors RTK `mob_ai_normal`:
`checkmove = rand(0..10)`; on `>=4` (~64 %) it picks a random facing and **only steps if that equals its current
facing, else just turns in place** (broadcast as `0x11`); on `<4` (~36 %) it steps straight ahead. Net: a rabbit
ambles a hop every few seconds and turns far more often than it moves — not a sprint every heartbeat.

**Collision (matches the player).** A step is rejected unless the target tile is in-bounds, within
`WanderRadius` (Chebyshev **2**) of `Home`, not on a player tile, **not on another mob**, and not solid.
Solidity is **`MapData.BlockedMove(x,y,dir)` = ground pass flag (`Solid`) OR the `SObj.tbl` directional
object-wall for that heading** — the SAME two-layer test `Session.HandleWalk` uses, so mobs respect the same
walls/water/cliffs/buildings the player and the client do (see §12). Passing the step direction matters:
object walls are directional (`UP/DOWN/RIGHT/LEFT`), and it's this layer — not the ground pass flag — that
stops a mob walking through a building's thin side wall (fixed 2026-07-26, "rabbits through Jadespear's hut
wall"; user-confirmed). Mob-vs-mob uses a per-tick `mobTiles` set kept current as each mob moves, so two mobs
can't share a tile or swap through each other in one tick. Players are also blocked from stepping onto a live
mob (`_world.MobAt` in `HandleWalk`; warp tiles still take precedence).

**Viewport streaming (`Session.SyncMobs`).** The `0x07` spawn is viewport-gated (§14): an off-screen spawn is
silently dropped, and the client culls entities that move off-screen. So mobs are streamed per player:
`_shownMobs` tracks which mob ids are drawn on that client; each tick (and on the player's own walk) `SyncMobs`
sends `0x07` for mobs that entered the camera rect and `0x0E` for ones that left.

**`ShowPad = 0`, `HidePad = 1`** — show at the strict 17×15 edge, hide one tile further out (corrected
2026-08-09; both were 0, which made mobs "pop out one tile too soon"). The two rects are genuinely different
sizes and conflating them was the bug: the **spawn gate** `0x424310` is a containment test against the 17×15
viewport, so `ShowPad` must stay 0 or a spawn is silently dropped and the mob is marked shown but never
appears. The **drawn rect** is 19×17 — viewport builder `0x44c950` clamps to `originX-1 .. originX+ViewW+1`
on all four sides — so despawning at 17×15 yanks a mob off a tile that is still on screen.

The dead zone this note used to warn about (we think it's drawn, the client already culled it, we never
re-send) is closed by `_edgeMobs`: any shown mob sitting in the overdraw band is flagged, and re-entering the
strict rect re-sends its `0x07` (an in-place update on a live id). That is strictly *cheaper* than the old
`HidePad = 0`, which sent a `0x0E` + `0x07` pair on every boundary crossing.

`World.Tick` reconciles views **before** broadcasting moves, and `MoveMob`/`SideMob` are
no-ops for players who don't have the mob shown — bounding on-wire traffic to on-screen mobs even on a 400-mob
map. This is what lets the full spawn roster render without blanket-sending hundreds of entities.

**Colours.** The `0x07` colour byte uses RTK's **`MobLookColor`**, and its semantics are now known: the client
treats the byte as a **ramp shift** over the mob's own base palette block — sprite indices ≥ 0x30 read
`palette[(i + 8·colour) & 0xFF]`; indices below 0x30 (outline/skin) stay put (RE'd from the 5.33 exe: draw
`0x447975`, palette manager `0x48ab40`, blitter `0x4392a0`; Sources.csv `binary-re-533`). That resolves the old
"every mob green" experiment: `Monster.tbl`'s Palette is the mob's base **block** id (the same values as the 5.33
`MONSTER.DNA` paletteIndex field), a different axis than the ramp byte, so sending it as a colour recoloured
wrongly — that experiment's table (`MobLookPalettes.csv` / `Content.PaletteFor`) has been deleted. RTK's colours
are era-correct as-is on 4.x, colour bytes ≥ 32 included: the 4.x client has no SUPER palettes and the 8-bit add
wraps, so e.g. horse colour 35 ≡ ramp 3 (brown) — consistent with live 4.95 and the 2005 atlas art across all
356 reference GIFs. The 5.33 client is the odd one out: it swaps in `SUPER{(colour>>5)-1}.PAL` for bytes ≥ 32,
so `Mob5xPalettes.csv` remaps those per (Look, Colour) — see that file's header.

**`rabbit` (MobId 1) had the wrong Look/Color — fixed live 2026-07-26.** The extracted `mobs.csv` row
for the base overworld `rabbit` mob (used by 242 fixed spawn points + 22 area spawns — the everyday wild
critter, not a debug spawn) carried `look 21, color 3`, which shares its sprite shape with the "Hare" family
(`look 21`, e.g. `hare` id 116 `color 37`) and visibly rendered like a hare, not a rabbit. Cross-referencing
RTK's own Lua spawn scripts (`rtklua/Accepted/NPCs/mobSpawnHandler.lua`) turned up a themed "Rabbit Warren"
dungeon (maps `4201-4208`: *Golden Warren*, *Rabbit Hole*, *Hare Depression*, *Mythic Owsla* — a Watership
Down homage) whose 3 progressive tiers are all built from **look 125** (`golden_rabbit`, `mad_hare`,
`giant_rabbit`, `hop`/`thump`/`fluff`, …) — confirming 125 is RTK's real "rabbit family" sprite, distinct
from 21's hare. Visually confirmed live via `re/monster-matcher/monster_matcher.py` (the sprite-matcher
tool, `http://localhost:8777`) plus in-game `@crecol 125 0 40` (spawns one of every color 0-40, each
labeled `col<N>` so `;`/look can read the exact number back — see the `HandleLookAt` fix below): the correct
plain-rabbit palette is **color 11**, not any of the named dungeon-tier colors. `rabbit`'s CSV row is now
`look 125, color 11`; since `Session.SpawnRabbit` (`@rabbit`) and every persistent spawn both read this one
registry row, the single data edit fixed the debug command and the whole overworld population at once — no
code change needed. **Lesson:** a `mobs.csv` row's Look/Color can be individually wrong even when the
extraction is right for the rest of the table; when a sprite looks off, verify visually (matcher tool or
`@crecol`) rather than trusting the row.

**Look-key gap found + fixed alongside this:** `HandleLookAt` (`;` key, §11) only checked `_world.MobAt`
(shared-world mobs), never the session-local debug-dummy list (`Session.MobAt`) that `@cre`/`@mobraw`/`@crow`/
`@crecol`/the look-lab spawn into — so pressing `;` at a `@crecol` row silently did nothing. Now checks both.

**Player HP/MP regen (`Session.RegenTick`).** The same heartbeat also heals **every connected player** — a
separate `World.Tick` pass, *not* gated on mobs or viewport like the steps above. This ports RTK's
`Player.regen` (`rtklua/Accepted/player.lua`, fired every 25 s from `pc_timer.lua`): while alive, restore
**2 % of max HP** and **2 % of max MP**, then push a `0x08` stats packet. RTK scales the vita portion by its
derived `healing` stat; we don't carry that, so **HP regen scales with Grace and MP regen with Will**
(`ceil(max * 0.02 * (1 + stat/100))`), keeping RTK's 2 % base and 25 s cadence. The 25 s clock is the
**world's**, not each player's (`World._regenClockMs`, advanced by one 333 ms beat immediately before the regen
pass): everyone below full heals on the *same* beat — the first beat at or after each 25,000 ms boundary — and
taking damage restarts nothing, which is RTK's one global `timerTick%50` rather than a per-player timer. The dead
(HP 0) and the already-full are skipped, and a packet is emitted only on an actual change. Effective max is
base + gear (`Totals()`). (Threading: this writes `_char.Hp/Mp` from the tick thread while the session's
read-loop also writes them on damage/heal — lock-free, consistent with the codebase's `_char` posture; a
collision at worst drops one small increment and self-corrects next tick.)

---

## 11c. Items — bag, gear, ground, and combat ⏳ (built; awaiting live 4.95 verification)

The full item system: an item registry, a per-character bag + worn gear (persisted), floor items, and the
pickup/drop/throw/use/equip handlers. Opcodes + wire layouts were translated from RTK 7.x `clif.c`
(`clif_sendadditem` / `clif_senddelitem` / `clif_equipit` / `clif_unequipit` and the `parse*` recv path).
Builders/handlers live in `Session.cs` (`SendAddItem`/`SendDelItem`/`SendEquip`/`SendUnequip`,
`HandlePickup`/`HandleDropItem`/`HandleThrow`/`HandleUseItem`/`HandleUnequip`/`HandleDropGold`); floor
items live in `World.cs` (`DropItem`/`PickUp`/`ItemsOn`, id pool `500000+`); the registry is `Content.Items`
(`ItemDef`) loaded from the gitignored `game-data/Items.csv` (2545 items — id, name, type, icon, look,
stat lines), same logic-only pattern as maps/mobs.

**Confidence.** The **recv** opcodes are trustworthy — 4.95's walk/turn/chat/attack/setting opcodes already
match this same RTK recv table exactly, so the item recv numbers align too. The **send** opcodes work on
the real 4.x client too (verified live: the bag populates + eat works), **but the 4.95 window layouts differ
from 5.x by one byte** (below).

**⚠ 4.95 `0x0F`/`0x37` drop the `iconColor` byte (proven live 2026-07-25).** 5.x (V533) carries an
`iconColor(u8)` byte right after the `icon(u16)`; **4.95 (V495) does NOT** — the name length follows the
icon directly. Sending the 5.x layout to 4.95 made the client read the name one byte early: **Apple**
(`iconColor=0`) → length 0 → empty name (`"You ate ."`); **Poison apple** (`iconColor=12`) → length 12 → a
12-char garble `"⊥Poison appl"` (the `⊥` is the real length byte 0x0C, then 11 of "Poison apple"). The wrong
*count* was the same off-by-one — everything after the name shifted by one. `SendAddItem`/`SendEquip`
branch on `_ver`; the tables below show the **V533** shape (V495 = same minus the `iconColor` byte).

**⚠ Because 4.95 has no `iconColor` byte, the color is part of the ICON ID — fold it in (fixed 2026-08-07).**
Dropping the byte was right; leaving the icon at the bare `ItmIcon` was not. 4.95 cannot recolor an item
graphic *anywhere*: the bag/equip icon draw is `0x435ab0(frame, dest)` → `0x431020("ITEM.EPF", frame+0x4000,
dest)`, and the floor-object path resolves the same way — a frame index and nothing else, palette from
`Item.tbl`. Color variants are therefore **separate consecutive `Item.epf` frames**, and RTK's
`(ItmIcon, ItmIconColor)` pair is just the later client's encoding of `ItmIcon + ItmIconColor`:

| RTK base icon | client ids | set |
|---|---|---|
| 89 / 99 / 120 | 89–98 / 99–108 / 120–128 | waistcoat / garb / scale mail |
| 149 / 159 / 180 | 149–158 / 159–168 / 180–188 | dress / blouse / mail dress |
| 265 / 450 | 265–274 / 450–459 | **helm** / gown |

Sending the bare icon drew the color-0 member for all ten — the "every helmet is a spring helmet" bug.
`Content.ResolveIconColors` folds the color in for those eight runs (`ItemDef.ClientIcon`), and
`Session.IconOf` is the single place the V495/V533 choice is made (bag, equip window, profile helm/ring
cells, floor items, item dialog portraits all go through it). It is an **allow-list on purpose**: most
non-zero `ItmIconColor` values in `Items.csv` belong to later-client content where the palette index is not
a frame offset, and adding it blindly lands on an unrelated sprite. A second guard refuses any target
another item already claims as its own `ItmIcon` (catches rows where RTK gave the variants real icons and
left a stale color — `star_armor_dress` 172+2 is `sun_armor_dress`'s icon).

**`Item.epf` is read like every other EPF: the table at `12 + tocOffset`** (corrected 2026-10-24 against the
4.95 install). Each 16-byte entry is `top,left,bottom,right` (i16×4) then `pixOff,stencilOff` (u32×2), the
`Effect.epf` layout of §7.2. Read that way, all **1310** frames satisfy `stencilOff − pixOff == w·h`, frame 0
included (a 24×24 box), and **frame `N` is client item id `N`**, as "Icons" below says: id 265 draws the green
helm at frame 265.

This passage used to say the opposite, following `re/render_items.py` and `re/verify_color_frames.py`: read the
entries at `tocOffset + i*16` as `top,left,pixOff,stencilOff,bottom,right`, take the box across two entries
(`w = left[i] − right[i−1]`, `h = top[i] − bottom[i−1]`), and conclude that frame `N+1` is item `N` and frame 0
is unusable. That table base is 12 bytes early. Its "frame *i*" is the canonical frame *i*−1, byte for byte,
for all 1309 *i* ≥ 1 (the install count), so its "frame 266" is frame 265 and both readings name the same
sprite; only the numbering is off by one.

**✅ Binary note — CORRECTED 2026-08-07. There is no "different build"; there are SIX dispatchers.**
This section used to say that because `NexusTK_local.exe`'s world dispatcher (`0x44b9c0`) no-ops
`0x0A/0x0F/0x10/0x37/0x38` while the live client clearly renders them, the running client must be a
different build. It isn't — that dispatcher is only *one* of several. **A received packet is offered to a
chain of window/widget objects, each via vtable slot +`0x5c`, until one returns TRUE** (`al=1` = handled,
`al=0` = pass it on; you can see the chaining explicitly at `0x474575`, `call [edx+0x5c]`). Every dispatcher
has the same shape: opcode = `[[arg+0xc]]`, a byte remap table, then a jump table.

| Dispatcher | Owner | Opcodes it claims |
|---|---|---|
| `0x44b9c0` | world/map | `03 04 06 07 0b 0c 0d 0e 11 12 13 15 16 19 1a 1b 1d 1f 20 21 26 29 2e 2f 30 31 33 34 35 36 39 3b 42 44 46 49 4a 4b 59 66 67 68` |
| `0x48eb40` | player state (owns `[0x4fd400]`) | `04 05 08 0a 0b 0f 10 12 15 17 18 24 26 4e` |
| `0x474550` | equipment / profile | `08 1d 33 37 38 39` |
| `0x43c110` | inventory pane | `0f 10 59` |
| `0x47bb90` | — | `0a 0f 10 17` |
| `0x47a6b0` | spellbook pane | `17 18` |
| `0x413970` / `0x496640` | — | `04 0b 15 26` / `04 08 0b 26` |
| `0x40ebc0`, `0x453210`, `0x46c490` | — | `0a 0d` / `2f 30` / `2f 30` |

So the disassembly **is** authoritative — you just have to look in more than one place. The player-state
class is where the item/spell arrays live: **inventory** at `state+0x13e`, 52 slots of **164 bytes**
(`0x48fd50` is the `0x0F` additem handler, `0x48f070` its setter); **spellbook** at `state+0x21ec`, stride
**328**; a third array at `state+0x2294`, same stride.

**Icons — SOLVED (encoding, not a mapping).** The client's item-sprite resolver (`0x435ab0`) does
`spriteId = iconField + 0x4000`, then the frame indexer (`0x431450`) bounds-checks the **low 16 bits**
against the Item.epf frame count (**1310**). Sending a frame `N` raw makes `N+0x4000 ≥ 1310` → out of range →
blank icon (the `descriptor[0xc 0xc 0xc 0xc]` in Frida is the caller's `0x424280(out,0xc,0xc)` default written
*after* the failed lookup, not the frame). **Fix:** the packet icon field must be `(N - 0x4000) & 0xFFFF`,
which wraps back to `N` after the client's `+0x4000` (`Session.IconWire`). Item.epf has exactly 1310 frames ==
`Item.tbl`'s 1310 items, so **frame index == client item id**, and — confirmed live (frame 10 renders as an
apple, RTK apple `ItmIcon=10`) — **RTK's `ItmIcon` already equals the client frame**, so `IconWire(def.Icon)`
is all that's needed; no mapping table. `SendAddItem`/`SendEquip`/ground items all encode through `IconWire`.
(RTK icons ≥ 1310 are 7.x-only items and render blank — acceptable.) Debug: `@icons [start]` fills the bag
with frames `start..start+26`.

**Ground items at rest — SOLVED (2026-07-25): use `0x07`, not `0x16`.** A floor item must *persist* where it
lands, but `0x16` is a **walk projectile** (walk ctor `0x463020` → vtable `0x4cd18c`, walk tick `0x463270`):
it animates toward its rest tile then drops off the moving list / self-destructs → **invisible at rest** (and
it plays a throw sound on spawn). The fix is the **`0x07` static base-object** descriptor (§7.2): send an entry
whose `look` is **outside `0x8000..0xbfff`**, which the factory `0x44d7d0` classifies as **descriptor type 2**
→ base ctor `0x462ec0` (vtable `0x4cd118`, tick `0x4601a0` = `xor al,al;ret`, a **no-op**). It renders its
sprite statically from **Item.epf** every frame via the shared draw slot and never moves or despawns — exactly
what a resting item needs, with **no divide-by-zero risk** (that hazard is `0x16`-only; see §7.2). `IconWire(N)`
already lands in the type-2 range: frames `0..1310` → `0xc000..0xc51e`, all `> 0xbfff`. So `ShowGroundItem`
just calls `SendCreatureList(new[]{ (id, IconWire(gfx), x, y, (byte)0, (byte)0) })` — same builder as monsters,
different `look` class. Switching off `0x16` also silenced the spurious throw sound on plain drops.

**Server → client (draw the bag / gear):** all multi-byte big-endian; body offsets are from the first body
byte (= raw frame `+5`).
| Op | Meaning | Body |
|---|---|---|
| `0x0F` | add item to bag slot | `slot(u8=idx+1) icon(u16) iconColor(u8) [dispName u8len+txt] [baseName u8len+txt] amount(u32) [stack/0(u8) dura(u32) protected(u8)] [owner u8len+txt] 00 00 00` |
| `0x10` | remove from bag slot | `slot(u8=idx+1) reason(u8) 00 00` — reason `0`=Remove `1`=Drop `2`=Eat `4`=Throw `6`=Used … |

**⚠ Reason `1` is Drop-only, not a generic "removed" (found live 2026-07-26).** The `0x10` body carries no item
name — the client renders its status line ("You dropped your `X`") from whatever it already has drawn in
that slot, keyed purely off `reason`. Several removal paths were reusing reason `1` for non-drop cases —
`TakeItem` (quest turn-ins), `DlgSell`/`SellItemToNpcByName` (selling), `DepositItemToBank`/`BankDepositItem`
(banking) — so selling or turning in an item falsely announced "You dropped your `X`". Fixed: all of those now
send reason `0` (Remove, silent/generic), matching RTK's own `pc_dropitemmap` (`clif.c`), which is the ONLY
call site that ever passes reason `1`.

**⚠ Reason `0` is NOT silent either (found live 2026-07-26) — it renders "`X` removed."** `EquipFromSlot` was
sending reason `0` when moving an item from bag to gear, which showed the misleading "removed" line for an
item that wasn't actually removed from the character. RTK's real `pc_equipscript` (`pc.c:1668`,
`pc_delitem(sd, sd->invslot, 1, 6)`) uses reason `6` for equipping — the same code `ITM_USE` consumption uses
(`pc.c:2085`) — not reason `0`. Fixed to send reason `6` on equip.

**The real 4.95 reason table (found 2026-08-06) — and it is NOT RTK's numbering.** The messages aren't
compiled into `NexusTK_local.exe` at all: they're plain CRLF-separated lines in **`Inter.dat`** (lines 85-96,
byte `0xdbb148`), which the client indexes by reason and `sprintf`s the slot's drawn item into. Dumping that
run gives the mapping outright — and the tail of it disagrees with the table RTK's `clif_senddelitem`
(`clif.c:7026`) documents for its own later client, the same "RTK ids live in a later client's space" pattern
as the sound ids (§7.3):

| reason | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **4.95** | dropped | ate | smoked | threw | shot | used | posted | decayed | **sold** | **gave** | **broken** | **removed** |
| RTK says | Drop | Eat | Smoke | Throw | Shot | Used | Posted | Decayed | *Gave* | *Sold* | *Removed* | *\*Item name\** |

Consequences: **every** reason 1-12 narrates, so there is no "silent remove" among them — and reason `0`,
which has no line of its own (line 84 is `"To: "`), rendering the *last* line is the signature of a
`default:` branch.

> **⚠ WRONG FROM 9 UP — settled against the client binary. The table above is the 2026-08-06 *inference*.**
> The live `@delreason` sweep the next day (2026-08-07) read back **`9` gave, `10` sold, `11`
> "&lt;item&gt; removed.", `12` SILENT, `13` "&lt;item&gt; broken."**, `14+` falling back to "removed", and
> that has since been confirmed statically from the 4.95 binary (2026-09-06):
>
> **The narration handler is `0x47c800`, not `0x48fe10`.** `0x48fe10` — the one this section's reasoning was
> built around — range-checks the slot, calls `0x48f0b0` to clear the bag cell, and **never reads `body[1]`
> at all**. The reason byte is consumed by `0x47c800`, reached from dispatcher `0x47bb90`, which does
> `ecx = body[1] - 1; cmp ecx, 0xC; ja default;` and then `jmp [0x47c958 + ecx*4]` — a **13-entry jump
> table**, not a linear index into the message run. Each arm pushes an explicit string index, and the arms
> for 9 and 10 push **58 and 57** — i.e. the switch deliberately reverses the two relative to their order in
> `Inter.dat`. That single fact explains the whole disagreement: reading the message run in file order, as
> the 2026-08-06 dump did, necessarily transposes `gave`/`sold` and slides everything after them. Reason
> `11` and reason `0`/`14+` share one arm (`0x47c904`, "&lt;item&gt; removed."), and reason `12` has an arm
> that jumps straight to the epilogue — **no string index, no status line**, which is the silent code this
> section says does not exist.
>
> So the correct reading is a `switch`, and the parts of §11c that survive are reasons 1-8 (which both
> readings agree on) and the observation that out-of-range values default rather than indexing raw. The rows
> above are kept as the record of the inference and of the `Inter.dat` line run, which is real evidence and
> correctly dumped — it was the assumption that the client indexed it linearly that was wrong. Two later
> findings in this document already matched the corrected table before it was confirmed: the give-to-mob
> path (RE'd 2026-08-18) uses **`9`** for `You gave <item>.`, and the NPC refusal drops an item with the
> **silent `12`**.
>
> The server follows the corrected table (`Shared/WireValues.cs` `DelReason`, and `Content.EquipDelReason`,
> whose shipped default is `12` — not the `-1` claimed further down §11c). `Tests/ActionAndDeleteWireTests.cs`
> pins it, so revisiting this means changing that test first and seeing every callsite whose wording moves.

**⚠ OUT-OF-RANGE IS NOT SILENT — but reason `12` IS. (Probe 2026-08-07; corrected by the `0x47c800` jump
table, above.)** The open question — whether an out-of-range reason is silent or falls into the same default
— was probed with `SilentDelReason` 15 on the equip path: it renders "`X` removed.", the *same* line reason
`0` gives, so the handler **defaults** rather than indexing raw (a raw index would have shown line 97,
"Your profile has been saved.", for 13). That part stands. The conclusion drawn from it — "there is
therefore no silent code to pick" — does **not**: it generalised from one out-of-range probe to the whole
range, and reason `12`'s arm in the jump table carries no string index and prints nothing. So a path that
must say nothing sends `12`; it does **not** have to omit the `0x10`, which matters because omitting it
leaves an uncleared bag cell. `SilentDelReason` is gone with the theory that named it — every path that used
it now names a real reason, and the equip path sends reason `12` (below).

Which reason each removal path should use, per the live game (user-confirmed 2026-08-06, banking and selling
revised 2026-08-07; the reason NUMBERS here follow the corrected table above): a **bank deposit** is `9`
"You gave `X`." — you handed the item over — and a **shop sale** is `10` "You sold `X`.", the vendor's own
line. (An earlier revision had both at `10` and called `10` "You gave", which is the same 9/10 transposition
the jump table settles.) Both send it ONLY when the
whole entry leaves the pack; selling or storing part of a stack redraws the stack with `0x0F`, sends no
delitem, and is therefore silent by construction. A **drop** is `1` and a **full pack** is our own NPC line, both
already correct; handing an item over in a **trade** is `9` "You gave `X`."; a **parcel** is `7`
"You posted `X`." (it was `1`/`4`, announcing a posted parcel as dropped or thrown). Still using reason `0`
(→ "`X` removed."), unreviewed: quest turn-ins (`TakeItem`) and the GM bag wipe.

**Consuming and wearing (user-specified 2026-08-07, `Session.HandleUseItem` / `EquipFromSlot`).** Three
distinct behaviours, one rule each:
- **Wearing gear — SILENT, and by reason `12`, not by silence on the wire.** RTK's `pc_equipscript` passes
  reason `6`, which on this client reads "You used `X`." — a consumable's line; the out-of-range 15 was tried
  next and reads "`X` removed." Omitting the `0x10` entirely was tried after that and is **wrong**: it leaves
  a ghost bag row that can't be dropped, equipped or used, because the bag and the equip window are separate
  client structures and only the `0x10` handler clears a bag cell. The `@delreason` sweep then found the code
  that says nothing, so `EquipFromSlot` sends reason **`12`** and gets both halves. The tunable
  `EquipDelReason` (**shipped default `12`**; `-1` suppresses the packet) is the knob, and
  `Content.EquipDelReason` carries the full swept table. *(This bullet previously described the `-1` default
  and the "no reason is silent" conclusion; both predate the sweep.)*
- **Consumables — silent until gone, then exactly one line, from the client.** No line while any of the stack
  (or any charge) remains; the use that removes the last of it sends the delitem, and its reason picks the
  wording: **food (ITM_EAT) → `2`** "You ate `X`.", **everything else** (wine/liquor charges, herbs, powders,
  ITM_USE) **→ `6`** "You used `X`." Keyed on the item TYPE, not the eat-vs-use keypress, so `0x1C` on food
  still says "ate".
| `0x37` | equip-window entry | `equipType(u8) icon(u16) iconColor(u8) [name u8len+txt] [baseName u8len+txt] dura(u32) 00 00` |
| `0x38` | unequip-window | `spot(u8) 00` |
| `0x07` | ground item (§7.2) | floor items go through the **`0x07` static base-object** path (below), NOT `0x16` — graphic = item's `Icon` (Item.epf frame), encoded via `IconWire` |

`equipType`/`spot` wire bytes (client `clif_getequiptype`): WEAP=1 ARMOR=2 SHIELD=3 HELM=4 NECKLACE=6
LEFT=7 RIGHT=8 BOOTS=13 MANTLE=14 COAT=16 SUBLEFT=20 SUBRIGHT=21 FACEACC=22 CROWN=23. Item `Type` (ITM_*)
maps to a gear slot for `Type ∈ 3..16` (EQ index = `Type-3`).

**⚠ Dual ring slots.** ALL rings/gauntlets are `Type 7` (156 items → wire slot **7**); `Type 8` carries
**zero** items in the data, so the right-ring box (wire **8**) is only ever reached by fall-through:
`EquipFromSlot` puts a ring in slot 7, and if 7 is already worn and 8 is free, wears it in **8** instead of
replacing. Only when both are full does a new ring replace the left. (Subleft/subright are distinct item
types with their own slots, so they need no such handling.)

**Client → server (handled in `Session.Handle`):**
| Op | Action | Body |
|---|---|---|
| `0x07` | pick up | `pickuptype(u8)` — `,` = 0 (grab the top item on my tile), `<`/Shift+`,` = 1 (grab **everything** stacked on the tile) |
| `0x08` | drop | `slot(u8=idx+1) all(u8)` — `all`: `0` = one, `1` = whole stack |
| `0x17` | throw | `confirm(u8) slot(u8=idx+1)` |
| `0x1A` | eat | `slot(u8=idx+1)` (ITM_EAT only) |
| `0x1C` | use / equip | `slot(u8=idx+1)` — equipment → wear, else consume |
| `0x1F` | unequip | `wireSlot(u8)` |
| `0x24` | drop gold | `amount(u32)` |
| `0x30` | rearrange a pane (Shift+`c`) | `pane(u8: 0=bag, 1=spellbook) startSlot(u8=idx+1) stopSlot(u8=idx+1)` — swap two slots. RTK `clif_parsechangepos`/`clif_parsechangespell` → `pc_changeitem`. Live capture `30 01 01 02 00` = spell pane, slots 1↔2. `HandleChangePos`/`SwapBagSlots`/`SwapSpellSlots` |
| `0x29` | hand / give item | `slot(u8=idx+1) handgive(u8: 0=hand one, 1=give whole stack)` — to the faced entity. RTK `clif_handitem`. See §Trade |
| `0x2A` | hand gold | `amount(u32 BE)` — to the faced player. RTK `clif_handgold`. See §Trade |
| `0x09` | look (`;` key) | *(no body — always the facing tile; RTK `clif_parselookat_2`)* |
| `0x19` | whisper (Shift+`'`) | `dstlen(u8) dst_name[dstlen] msglen(u8) msg[msglen] 00` — LIVE-confirmed 2026-07-26 |

**Look (`0x09`, added 2026-07-26).** The client sends this with an empty body whenever `;` is pressed — no
coordinates, always the tile immediately in front of the player (facing direction), matching RTK's
`clif_parselookat_2` (the client→server `0x09` here is unrelated to the server→client `0x0A` status opcode
below). `HandleLookAt` checks that tile in RTK's PC → mob/NPC → item order (`clif_parselookat_sub`'s per-type
branches) and echoes the name to the **status / mini-text box below the inventory** via `SendMiniText`
(server→client `0x0A`, see below) — a floor item gets `"name (amount)"` when stacked, same shape as RTK's
commented reference formatter. This matches RTK exactly: its look-at ends in `clif_sendminitext`, NOT a chat
bubble (the earlier `SendLog`/`0x0D` routing spoke the name out loud over the player's head — wrong channel,
fixed 2026-07-27). NPCs are stationary mobs (`IsNpc`) in the same shared per-map list, so the mob check
already covers them; an empty tile gets no reply at all, matching RTK (no `clif_sendminitext` call when
nothing's found).

**Status / mini-text box — server→client `0x0A` (`SendMiniText`).** The scrolling log pane beneath the
inventory (look-at names, item pickup/drop, "experience gained", map-entry rejections, whisper text). A
distinct channel from both the `0x0D` chat bubble (`SendLog`) and the `0x02` login message box
(`SendMessage`).

**⚠ Layout corrected 2026-08-07: the body is `type(u8) · len(u16BE) · text[len]`**, not
`type(u16 LE) · len(u8)`. Every `0x0A` handler in the client reads it that way — a `u8` at `body[0]`, then
`0x475ca0` (the *big-endian* u16 reader) at `body[1]`: see `0x47c520`, `0x490930`, `0x40eeb0`. The old
reading produced identical bytes only because our type high byte is always 0 and our text was always under
256 chars; it would have silently truncated the moment either stopped being true. **The real cap is the
client's own widen buffer — `0x8000` characters**, not 255.

`type`: **0** = wisp (blue), **3** = mini/status text (the default, `clif_sendminitext`), **5** = system,
**11** = group, **12** = clan — and **2 / 3 / 8 additionally reach the bordered word-wrap overlay** (§11c.1).

**⚠ Swept live on 5.33, 2026-08-23 — see `docs/5.x/Wire-Divergences.md` §6.11 for the full table.** The list
above is reasoned from RTK's 7.x `clif.c` and was confirmed live only for `0`. On 5.33 the observed panes
are: 0 blue chat · 2 and 3 status box · **4 RED chat (the Sage world channel — undocumented here, and the
value the ladder needed)** · 5 light blue chat (our `SystemAnnounce`) · 8 a modal OK pop-up · 11 blue ·
12 GREEN, which does not obviously match the "clan" label. So only **8** reaches the overlay there, not
2/3/8 — consistent with the handler-chain caveat below. **4.95 has NOT been swept**; use `@text` (bare, to
compare panes side by side) on a 4.95 client to settle whether these hold on both.
Which widget wins is decided by the handler chain, since several claim `0x0A` (see the Binary note: a packet
walks widgets via vtable slot +`0x5c` until one returns TRUE).

**State guards on item actions (added 2026-07-26).** RTK gates every one of drop/throw/drop-gold/equip on
player state before doing anything else — dead (`Hp==0`, "Spirit") or mounted can't perform them. Ours now
matches: `HandleDropItem`/`HandleThrow`/`HandleDropGold` reply `"Spirits can't do that."` / `"You cannot do
that while riding a mount."`; `EquipFromSlot` uses RTK's (differently-worded, verbatim-preserved) equip-specific
pair `"Spirit's can't do that."` / `"You can't do that while riding a mount."`. `HandleThrow` also now rejects
`NoDrop` items (`"You can't throw this item."`) — previously throw had no restriction at all, unlike drop.

**Semantics.** Equipping a **weapon** (`Type 3`), **armor** (`Type 4`) or **shield** (`Type 5`) changes the
4.95 look — the item's `Look` goes into the 7-byte type-0 form (slot [5]=weapon, [3]=armor, [6]=shield) and
re-draws self + peers; other gear slots have no 4.95 appearance. Weapon/shield use the `0xFF`-when-bare rule
(see §8). Floor items broadcast to everyone on the map
and survive until picked up; gold drops as a sentinel ground pile (`ItemId = -1`) that refills the purse on
pickup.

**Equip stat bonuses + wear requirements — SOLVED (2026-07-25).** Worn gear now feeds the HUD/profile and
combat. The character's `_char.*` stats stay the **base**; the effective values are `base + Σ(worn-gear
lines)`: `Session.EquipTotals()` sums the gear at each gear change into one published totals snapshot that
every send reads (PR #314) — nothing is ever baked into the base, so a
relog (which reloads `Equipment` and redraws it) can't drift or double-count. Mapping: `Vita→maxHP`,
`Mana→maxMP`, `Might/Will/Grace→` those stats (`0x08`), and `Armor→AC`, `Hit`, `Dam→` the profile (`0x39`).
**AC is signed and lower is better**, so armor **subtracts**. `EquipFromSlot`/`HandleUnequip` push a fresh
`0x08` immediately; current HP/MP are clamped to the (possibly reduced) effective cap after an unequip. Melee
(`HandleAttack`) uses effective Might + gear `Dam` + the flat weapon bonus. **Wear requirements** (checked in
`EquipFromSlot`, from `ItemDef`): sex-lock (`ItmSex` — **`0`=male-only, `1`=female-only, `2`=unisex**; the
unisex `2` is the common case at 1944/2545 items, so the gate only fires when `ItmSex < 2` and mismatches
`Character.Sex`, which uses the same `0`=M/`1`=F encoding), minimum **level** (`ItmLevel`), and minimum
**might** (`ItmMightRequired`, tested against *effective* might so already-worn +might gear counts). The
**path/class** restriction (`ItmPthId`) is enforced too, but one level up the call chain in `CanUsePath` (see
§11c) so it also covers the consumables that carry one. **It has no GM bypass any more** (2026-08-07): it
used to skip GMs, which — with the GM account being the one anybody actually plays — read as the gate simply
not working (a Mage wearing a Rogue's waistcoat), while the sex/level/might gates beside it refused that same
GM all along. GM setters `@lvl <n>` / `@might <n>` adjust the base stats to exercise the gates; `@class
<name>` switches path to exercise this one.

**Cursed/malus gear gate (added 2026-07-26, RTK `pc_canequipstats`).** 14/19 items in the registry carry a
*negative* `ItmVita`/`ItmMana` line (a stat penalty, not a bonus). RTK blocks equipping one if the penalty's
magnitude would exceed your current effective max — it'd zero the pool out entirely — replying `"You lack the
health required to wield that."` (Vita) or `"You lack the wisdom required to wield that."` (Mana, RTK's own
wording). Checked against `EffMaxHp`/`EffMaxMp` (before this item's own effect), same as the might gate above.

**Item action animations (0x1A) — each item verb plays its bend-down pose + sound, on self AND peers.**
Every item handler broadcasts a `0x1A` action (§13, builder `Session.SendAction` / peer `ActionOver`) so the
character visibly stoops and the client plays the baked-in sound. The `(type, time)` pairs are RTK's
(`clif.c`): **pickup = `(4, 40)`** (RTK `clif_parsegetitem`), **drop = `(5, 20)`** (`clif_parsedropitem` — a
*distinct* pose from pickup), **throw = `(2, 20)`**, **eat = `(8, 40)`**. `sound` is 0 (the action sprite
carries its own sound; a non-zero 4th arg would be a separate sound id).

**Ordering matches RTK.** Pickup plays the action **before it looks at the floor**, even on an empty tile: the
crouch fires on the keypress.
- **Not at all for a dead or mounted player.** Their `0x07` is dropped silently, before the crouch and before the
  invisibility break: no `0x1A`, no sound, no line, no item. The check is `Server/Session.Items.cs:168`, ahead of
  the crouch at `:170-171`. RTK's `clif_parsegetitem` returns for states 1 and 3 first (`clif.c:9923-9924`).
- **An invisible player on foot** still crouches and picks up, and the attempt breaks the invisibility.
- **A transformed player** picks up as usual.

Drop plays it only **after** the `NoDrop`/valid-slot guard passes. `<` (pick-up-all) plays the action once, then
loops the tile until empty.

**Throw collision — SOLVED (2026-07-25; object-wall aware 2026-07-26).** `HandleThrow` walks the item
**tile-by-tile** from the player in the faced direction (0=N 1=E 2=S 3=W, capped at 3 tiles) and stops at the
last *passable* tile, so items never come to rest on a wall or an unreachable tile. Passability is the **same
two-layer test the walk uses** (§12): the **ground pass flag** (`Blocked` = `map.Pass(x,y) != 0`; top 2 bits
of the ground `u16`, `3` = solid, `0` = walkable, `1`/`2` never occur) **OR** the **`SObj.tbl` directional
object-wall** for the throw heading (`ObjectFlags.Blocks`) — so a thrown item halts at a building's side
wall, not only at water/cliffs. Enforcement is `P1998_PASS` (default on; set `0` to disable); the walk
(`HandleWalk`) applies the identical two-layer check.

**Doors ('o' key / `0x20`) — WORKING (2026-07-25).** A door is an object drawn over the map. Pressing 'o'
facing a door **toggles its open/closed graphic in place** — RTK `openDoors` (`open.lua`) does
`setObject(m,x,y, closed↔open)`, e.g. Buya door `342 ↔ 364` (some doors span 3 tiles: x, x+1, x+2; the city
gates span 4). It is shared world state. (An earlier version of this paragraph said "cosmetic only,
passability doesn't change" — that predates the `SObj.tbl` object-wall layer, §collision. It is wrong for any
door whose closed graphic is flagged solid there, which includes the city gates.) Entering a building is done by
**walking** onto its warp tile (§warps), not by 'o'. Not every door leads anywhere — many are **decorative
facades** (a passable gap in a wall sprite with no interior); RTK's authoritative `Warps` table confirms which
doors are real entrances. `Session.HandleOpen`: reads the faced object, looks it up in the door toggle table
(transcribed from `open.lua`), mutates the shared `MapData` object layer (so the next 'o' toggles it back), and
sends the **`0x06` cell-patch** (see §13) to every client on the map to redraw. The client re-renders the
object layer over the patched rectangle regardless of whether the ground word changed. Full door toggle table:
memory `nexustk-495-doors`.

**City gates + the late-arrival replay (2026-08-06).** RTK's 4-tile-wide gate run `17670-17673 ↔ 17680-17683`
is a *later* client's object space (4.x object ids are a 14-bit field, max 16383); its 4.95 counterpart is
**`5-8 ↔ 15-18`** — same width, same `+10` pairing, and every gate in the game data sits between wall pieces
`1-4` and `9-12`. Which half is open comes from **`SObj.tbl`, not the id order**: `5-8` are `0x0f` (solid all
four sides) = **closed**, `15-18` are `0x00` = **open**. There are 22 such gates; 19 ship open, 3 ship shut
(Kugnae south, City Limits `4717`, map `4951`), which is why 'o' looked broken in *opposite* ways at Kugnae's
two gates — neither id was in the toggle table, so the south one was an unopenable wall and the north one an
uncloseable hole. Closed ids now carry `defaultOpen=1` in `DoorObjects.csv`, applied per cell in
`MapData.Load`. Because the 4.95 client draws its **own local** `.map`, a server-side object change is invisible
until patched: `MapData` therefore tracks every cell that differs from the file, and `Session.SyncMapDoors`
replays them as `0x06` runs on map entry. That also fixes a pre-existing bug — a door opened by one player was
never shown to anyone who arrived afterwards, so their first 'o' appeared to do nothing (it was toggling a door
they were being drawn as already-shut).

**Scripted-tile warps — Mythic Nexus zodiac caves (map 41) — WORKING (2026-07-25).** Not every warp lives in
the SQL `Warps` table. The 12 zodiac cave entrances on Mythic Nexus (map 41) are RTK **Lua tile-scripts**
(`onScriptedTiles/onScriptedTilesMythic.lua` → `NPCs/mythic/mythic_cave_selector.lua`), so a step onto one is
intercepted in `HandleWalk` **before** the collision test (like a warp), by `Session.TryMythicCaveEntrance`.
Each animal has a **two-tile footprint** on map 41 (e.g. Rabbit `(49,12)`/`(50,12)`, Dragon `(29,19)`/`(30,19)`)
and a cave-1 destination map (Rabbit→201, Ox→170, Pig→181, Horse→246, Dragon→257, …). The cave has **three
depth tiers**: cave 1 = base map, cave 2 = `base+3000`, cave 3 = `base+4000` (all renderable — e.g. `170` Red
Bull 1 / `3170` Red Bull 2 / `4170` Red Bull 3). Entry is **level/vitals-gated** (`Scripts/mythicCaveReqCheck.lua`):
per animal, tier *t* needs `level ≥ Lₜ` **and** (`maxHP ≥ Hₜ` **or** `maxMP ≥ Mₜ`) — tier-1 has no HP/MP floor,
so level alone unlocks it (Rabbit L25, Monkey L32, Dog L39, … +7 per animal, Dragon L99). RTK's picker menu is
GM/Config-only; with it off (our default) it **auto-warps to the deepest tier the player qualifies for**, which
`TryMythicCaveEntrance` reproduces. Under-levelled → refused with a flavour line (8+ levels short = *"Nightmarish
visions of your own death repel you."*, 4–7 = *"You are not yet ready…"*, ≤3 = *"You almost understand…"*) and
the step is cancelled. Test with `@lvl <N>` (the bring-up character is L1, so every cave refuses until levelled).

**More scripted tiles: fall-rooms & foraging — WORKING (2026-07-25).** RTK's `onScriptedTile` runs a stack of
per-walk handlers (`onScriptedTiles/`); `Session.OnScriptedTileStep` (fired at the END of a completed step, when
the player stands on the new tile) ports the two that are self-contained **and** live entirely on renderable 4.x
maps:
- **Mythic fall-rooms** (`onScriptedTilesMythicFallRooms.lua`) — inside a zodiac cave, every step has a **1/500
  chance to drop through the floor** to a fixed landing tile in a lower sub-room (e.g. Ox `177/178 → 180 (22,7)`,
  Monkey `167/168 → 169 (23,3)`; +3000/+4000 for cave tiers 2/3; plus the Iron lab `1302-1306 → 1307`). All ~90
  source and destination maps render. `TryMythicFallRoom` (table `FallRooms`, built from `FallGroups`) warps via
  `EnterMap`; it no-ops if the destination isn't renderable, so it can never strand a player.
- **Bush/tree foraging** (`onScriptedTilesBushTree.lua`) — standing adjacent to an **apple tree** (object ids
  `860-864`) or a **rose bush** (`876-889`), each step has a **1/50 chance** to pick an *apple* (`10001`) / *rose*
  (`21001`). `TryForage` scans the 3×3 object layer around the player (`MapData.Obj`) — the `.map` object word IS
  RTK's object id, verified: apple trees appear on 37 renderable maps (Kugnae 685×, Vale 905×, Mythic Nexus 161×),
  rose bushes on 24 (Kugnae 555×, Buya 214×, Wilderness 562×), so it fires against real map data.

**Class path-hall doorways — WORKING (2026-07-25).** Same SQL-vs-Lua split, caught live: inside a class hall the
north (path-leader) and south (guild) doors did nothing because only the *map-exit* warp is in `Warps.csv` — the
two interior doors are scripted (`onScriptedTilesPathHalls.lua`). Each of the **8 halls** (Kugnae Warrior Tebaek
`11` · Rogue Maro `15` · Mage Haedu `13` · Poet Jinsun `17`; Buya Warrior Yebaek `341` · Rogue Maso `343` · Mage
Eldritch `342` · Poet Song `344`) has:
- **South edge** `(x 1-2, y 23)` → that class's **guild hall** (Kugnae `3701-3704` / Buya `3705-3708`), arriving
  `(x+6, 3)`. **Class-gated**: only members of the hall's base class pass (`CharClassId == BaseClass`); RTK also
  admits a `player.tutor` (a staff role we don't model), so wrong-class = minitext *"You are not the right class
  to enter here."* + `SendXy` hold.
- **North edge** `(x 8-9, y 1)` → the player's **alignment sanctum** (path-leader room), indexed by
  `Character.Alignment` 0-3 (Unaligned / Kwisin / Mingken / Ohaeng), arriving `(x-3, 18)`. Mage Eldritch, e.g.,
  maps to `{367, 309, 310, 311}`.

`Session.TryPathHallWarp(x,y)` + static `PathHalls` table (`record PathHall{BaseClass, Hall, Sanctum[4]}`), called
in `HandleWalk` right after the Mythic check and **before** the collision test (these doors sit on edge tiles that
read as solid). Every one of the 24 destinations is renderable; `WarpHall` no-ops (holds) if a dest map is missing,
so it can never strand a player. Live-confirmed working 2026-07-25.

Other `onScriptedTiles/` handlers were surveyed and **skipped**: subpath-trial entrances (most destination maps
absent from the 4.95 client — only diviner `3540` / druid `3632` render), Tutor's Haven / Elixir Hall / Mount
Baekdu / Nagnang-shield / generic quest halls (host maps not renderable = 7.x content), and the Tower Arena /
Carnage Hall minigames (maps render but need a full event-manager). See memory `nexustk-495-scripted-tile-warps`.

**GM commands:** `@items [filter]` (browse registry), `@item <name/id> [amount]` (summon into bag),
`@clearinv` (reset bag + gear). The examine reply below has no command any more — it fires from the
client's own right-click, and its renderer is fixed at the `tooltip` default in `Session.Dialog.cs`.

### 11c.1 `0x66` — "examine item" (right-click a bag slot) ✅ *(both directions RE'd from the 4.95 binary, 2026-08-07)*

Right-clicking an item in the inventory pane asks the server what it is. Before this was answered, the client
sent the request and **retried it 5–8×** before giving up.

**Request (client → server), 10 body bytes.** Built by the inventory pane's mouse handler `0x43bf40`
(mouse-message kind **4**; kind 2 is the left-click that sends `0x1C` *use item* via builder `0x43c160`)
through packet builder `0x43c290`, which writes the body byte for byte:

```
00 cursorY(u8) 00 01 01 SLOT(u8) 01 00 00 00
```

The pane shows **15 cells** with a page byte at *widget*+`0x104`, so a click resolves to
`cell + 15*page + 1` (`0x43bf94`) and then through `0x43c5a0` ("the array index of the Nth occupied slot")
to the id in **body[5]**. Because a left-click hands that same id to `0x1C`, the slot convention is
identical to `HandleUseItem`'s — **1-based**.

> **Gotcha.** body[1] is the click's raw **pane-local cursor Y**, not an item reference. An early note had
> the two swapped, which made every logged decode read 196–225 and report "no bag item at that slot". The
> two bytes track each other only because both come from the same click — that ratio is the 13px cell
> pitch (cell rects at `0x43c420`: `top=13N+0x24, bottom=13N+0x31`, `N`=0..14, so Y spans 36..231 while a
> pane-local X can never pass the hit-test above `0xa0`). The byte exists to be **echoed back as the `0x59`
> tooltip's Y anchor** so the box centers on the mouse — see §11c.1.

**Reply (server → client), same opcode — and it is a "GO TO THIS URL" packet.** Client handler `0x4511b0`,
keyed on `body[0]`. Every kind carries a URL; they differ only in *which browser opens it*:

| kind | body after the kind byte | What happens |
|---|---|---|
| `0` | `u16BE len` + **URL** | The client's **own embedded Internet Explorer control** navigates to it, in a near-fullscreen window at (10,10)–(630,470). |
| `1` / `2` | `u16BE len1` + **URL**, `u16BE len2` + **text** | A centred `DLGBBS01.EPF` + **OK** popup showing *text* (≤999 chars); **OK** `ShellExecute`s the URL in the *external* browser. `1` additionally **quits the game**. |

All lengths are **u16 big-endian** (client reader `0x475ca0` is `hi<<8 | lo`).

**Proof for kind 0.** The window class built at `0x406250` hosts an ActiveX browser: vtable slot +`0x8c`
(`0x406680`) calls `CoCreateInstance(CLSID_WebBrowser@0x4d00a8, …)` and queries `IID_IWebBrowser2@0x4d0098`
(the binary imports `ole32!CoCreateInstance`). Our string reaches `0x4053c0`, which does
`SysAllocString(str)` and then `call [vtbl+0x2c]` with five arguments — **`IWebBrowser2::Navigate(BSTR url,
flags, targetFrame, postData, headers)`**.

**Proof for kinds 1/2.** The popup's OK handler is vtable slot +`0x70` = `0x488480`, two actions:

```
ShellExecuteA(0, 0, url, 0, 0, SW_SHOWNORMAL)                        // url verbatim from window+0x27c
if (flag at window+0x280) { SetEvent(app+0x818); app+0x815 = 1 }     // 0x403030 — shutdown signal
```

**RTK 7.x confirms both independently.** `clif_sendurl(sd, type, url)` (`rtk/src/map/clif.c:265`) builds this
exact packet and comments the type byte:

```c
WFIFOB(sd->fd, 5) = type; // 0 = ingame browser, 1 = popup open browser then close client, 2 = popup
```

> ### So right-clicking an item asks the server for a WEB PAGE
>
> That is the whole feature. Original NexusTK served item pages off `nexustk.com` and rendered them in the
> embedded browser — which is exactly why **no window class in the client draws an item-detail panel**, and
> why `Item.tbl` carries no text (it is `ID n, Palette p, Alpha a, Light l` — sprite metadata only).
>
> Getting here cost two live incidents worth remembering. **Kind 1 killed the client** (it is kind 2 plus the
> quit flag). Then **kind 2 with an *empty* URL opened the client's own install directory in Explorer** —
> `ShellExecute("")` resolves to the working directory. The popup fallback therefore sends the literal
> `<none>`: `<` and `>` are illegal in filenames and it carries no scheme, so the shell can only fail it to
> `SE_ERR_FNF`, which is what makes OK a plain dismiss. A bare word would be worse — `ShellExecute` resolves
> those against `App Paths`/`PATH` and could *launch a program*.
>
> Ruled out along the way (all now identified, and worth keeping): `0x44` = no-op (`mov al,1; ret`), `0x67` =
> a seconds countdown, `0x68` = a `0x75` challenge/response ping, `0x4a` = a `GetModuleFileName` anti-cheat
> check, `0x49` = a profile-picture upload request answering on `0x4F`, `0x4b` = a buffer the client echoes
> straight back as its own next packet, `0x31` = the board ACK, `0x35` = the mail/parcel reader (`MAIL.EPF`/`PARCEL.EPF`), `0x36` = the user
> list (`USERLIST.EPF`), `0x42` = the trade window (`DLGEXC1.EPF`), `0x46` = the "Power" window, `0x2f` = the
> shop grid (`DLGMERC1.EPF`). `#INFOSHOP:` turned out to be a `PHONE.CFG` item-mall marker. The 16 sites that
> draw an item sprite belong only to: the inventory pane (`ITEMINV.EPF`), ground items, trade, mix/craft
> (`MIXITEM.EPF`), the shop, and the profile paperdolls — there is no seventh.

### ✅ The reply: `0x59` sub-kind 0 — the inventory pane's own tooltip

A period screenshot settles the shape: item info is a **multi-line box hanging off the item list**, with the
name, durability, small/large damage, a combined armour/hit/dam line, owner and the class-level requirement.
Wrapped, no window chrome, **no close button**. Not a dialog, not a web page.

It is not a separate window class at all — **the inventory pane draws it itself**. That pane's packet handler
(`0x43c110`) claims three opcodes: `0x0F`, `0x10` and **`0x59`**. Sub-kind 0 lands in `0x43c1b0`:

```
body[0] = 0              sub-kind
body[1] = Y anchor (u8)  the box's vertical CENTER in pane-local pixels — echo back the request's body[1]
                         (the click's pane-local Y) and the box centers on the mouse
body[2..3] = u16BE len   must be 1..0x3FF; outside that the handler bails silently
body[4..]  = text
```

The handler asks the pane for its own rectangle (`0x425380`), computes the pane's **horizontal** midpoint,
and builds a `0x108`-byte overlay object (`0x42f450`) from the text, both coordinates, and a **`0x2710`
(10000) lifetime**. The ctor sizes the box from the wrapped text and places it at `(midX − width/2,
anchorY − height/2)` (`0x42f570`–`0x42f598`), clamps it inside the pane and the screen, then shifts it by
the pane origin — X self-centers on the item list, Y follows the anchor, and the box times out on its own,
which is why the screenshots have no close button. `0x42f450`'s own scan (`0x42f4ed`) treats **CR (`0x0d`),
LF (`0x0a`) and TAB (`0x09`) all as line breaks**, so the separator needs no calibrating.

> **The anchor is a coordinate, not a row index (fixed 2026-08-27).** An earlier read had `body[1]` as
> "which item row to hang off" and the server echoed the *slot number* (1..52) — which made
> `anchorY − height/2` negative for every slot, so the clamp parked the tooltip at the top of the screen on
> every examine. The request's `body[1]` (formerly misread as "cursor X") is the click's pane-local Y: the
> pane's cell hit-test (`0x43c540`) tests the pair from the mouse message against cell rects built at
> `0x43c420` as `(left=0x1e, top=13N+0x24, right=0xa0, bottom=13N+0x31)` for cell `N` — a 13px row pitch —
> and the coordinate the request builder copies into `body[1]` (`0x43bfab`) is the one tested against the
> `13N` row range. It exists solely to be echoed back here.

**Sub-kind 1 is a different feature and cannot collide**: RTK's `clif_sendtowns` sends `0x59` with
`body[0]=64`, and the world dispatcher's `0x59` trampoline (`0x44b9e9`) gates on `body[0]==1`, while the
inventory pane gates on `body[0]==0`.

**Rejected alternatives, live-tested.** `0x0A` types 2/3 land in the status pane (the client's *other*
bordered word-wrap box, class `0x47b400`–`0x47c700`, nine-slice `MSGBORD.EPF`, wrap pass at `0x47c310`) —
right mechanism, wrong pane. `0x0A` type 8 and `0x66` kind 2 both raise the parchment **OK dialog**, which
the game already uses for the Rogue Judge/Spy spells. `0x66` kind 0 crashes.

**Server side.** `Session.HandleItemInfoRequest` → `ItemInfoText` → `SendItemInfo`, which supports both:

- **`mode tooltip` (default)** — `SendItemTooltip`, the `0x59` inventory-pane overlay above. This is the one.
- **`mode overlay`** — `SendMiniText(text, type)`, riding a `0x0A` type (status pane).
- **`mode browser`** — 0x66 kind 0. **⚠ This CRASHES this build.** Live test with a real URL: the window's
  ctor asks for sprite category `"X"` (`XBUTTON.EPF`, its close button), the load throws an uncaught
  allocation failure (`_CxxThrowException` via `0x430c7a` → `0x406cb6`, the `XBUTTON.EPF` site) and the
  client dies on a null deref at `0x470ff4`. The asset isn't in this install's archives, so the browser
  chrome can't build. Kept only for completeness.
- **`mode popup`** — 0x66 kind 2, the same text in the OK dialog. Renders, but that dialog is what the game
  uses for the Rogue Judge/Spy spells, so it's the wrong frame here.

**The text** (`ItemInfoText`) reproduces the original box's layout, taken from period screenshots (Ice garb,
Sunrise fan, Titanium blade, Heavy/Military polearm, Molten blade, Warded robes, …): name, `Durability: cur
/ max`, the `Damage: Small: / Large:` pair (`Large` indented 8 spaces to sit under `Small`), one combined
`Armor: n  Hit: n  Dam: n` line (two spaces between fields), the `<stat> increase: n` lines (Vitality / Mana
/ Might / Will / Grace / Healing / Wisdom), `Protection: n`, `Strength: n`, `Owner:`, then the class/rank
requirement line. **Values follow the label with a single space — the box does NOT column-align them** (Ice
garb shows `Grace increase: 1` and `Strength: 10` at different depths). Only lines an item earns are printed.
`ItmHealing` and `ItmWisdom` were parsed nowhere until this needed them.

**`Strength: n`** is `ItmMightRequired` — the STR the wearer needs to wield the item. The real box labels it
**Strength**, not "Might required", and the shop's own blurb agrees ("Strength of 35 req"); it sits directly
after `Protection`. Screenshot values: Titanium blade 100, Heavy polearm 130, Ice garb 10, Midnight cutlass
0 (line omitted). It's a description, not enforcement — the wear gate stays in `CanWear`.

**The requirement line is ONE line**, not a level row plus a mark row:
- `Mark > 0` → the path's own rank title alone, **no level**: `Content.PathTitle(pathId, mark)` = `Il san (P)`
  (Warded robes). The Peasant path names every rank "Peasant", so Molten blade (path 0 / mark 2 / level 99)
  reads just `Peasant`.
- else with a level → `<Path> Level n`: `Mage Level 99`, `Peasant Level 50`.
- else (equip, level 0) → the bare path name `Peasant` (Heavy/Military polearm) — never `Peasant Level 0`.

**`Owner:` is the BOUND owner**, not whoever is holding it — which is why it appears on some items in the
original and not on others with an otherwise identical layout. It is per-STACK state — `InvItem.Owner`,
persisted with the rest of the character JSON, so no schema change — because the same registry row can be
bound in one player's bag and unbound in another's. The line is emitted only when that field is set.

Binding is a property of **the grant, not the item**. RTK's `player:addItem(key, n, dura, ownerId)` takes an
owner argument, and only the scripts that forge something FOR you pass `player.ID`. Nothing in `Items.csv`
records it, so the set comes from the Nexus Atlas's per-item **Special Info** field — which uses exactly this
three-flag vocabulary ("bonded, break on death, unrepairable and much more", says its own armor index) — mined
by `re/atlas_special_info.py` and matched to the registry by display name. Of the 521 rows that match an Atlas
entry, the Atlas and the CSV columns agree on break-on-death for 502 and on repairability for 505; that
calibration is what earns the field the casting vote on the one flag the CSV does not carry.
`ItemDef.BondedItemIds` is the result (120 ids), and `GivePlaced` stamps the holder as owner on any
acquisition path that doesn't already carry one.

Three properties, three independent sources — do not derive any of them from another:

| property | source | example |
| --- | --- | --- |
| bonded | Atlas *Special Info* (`re/atlas_special_info.py`), RTK grant sites where it is silent | Nimble blade — bound, not BoD |
| break on death | `ItmBoD` column, 77 rows | Spike — BoD, binds to nobody (a boss drop, and Gan sells it) |
| unrepairable | `ItmRepairable` column (**positive**: 1 = repairable) | Frost sabre — bound *and* repairable |

**They overlap, unevenly — 14 ids are both.** The subpath weapon families show the shape of it: the base tier
(Spike, Blood, Surge, Charm) reads `Break on Death` and nothing more, while the Enchanted and san tiers an NPC
upgrades for you read `Bonded / Break on Death`. Enchanted charm is the one the Atlas marks `Non-Bonded`.

**Some bonds are per-INSTANCE, not per-item.** The Giasomo stick, Frozen spear and Student cap rows are plain
break-on-death drops; their bonded copies are a different instance of the same id — *"Bonded, non-break on
death ones can be bought for 400,000 coins at the Arctic Smith by saying Laptev"*. RTK produces that by
stamping an owner at the grant (`smith.lua`'s Laptev branch, `museum_caretaker.lua`); neither NPC is wired
here yet, and when they are they pass `owner:` to `GivePlaced` rather than joining the id list.

A rule that swept the whole `49xxx` weapon block into the bonded set once made every break-on-death subpath
weapon look bound; `ContentSmokeTests.BondedBreakOnDeathAndUnrepairableAreIndependent` guards it.

**`Break on death`** is a warning, not a field: emitted only when the item actually has `ItmBoD`, and always
the **last** line, so it reads as the closing note on the item rather than another stat row.

The damage range separator renders as a lowercase `m` in every surviving screenshot ("90m140", French
"55m65"); whether that's a literal `m` or the client's glyph for a range character is unknown, so
`DamRangeSep` reproduces what is on screen and is one character to change.

**No sex line** (removed 2026-08-07): `ItmSex` is a wear gate, not a description, and the original box never
printed one — `EquipFromSlot` simply refuses the item.

The client fires the reply itself on right-click; the renderer and line-break character are compile-time
defaults now (`_itemInfoMode` / `_itemInfoSep`), the tooltip accepting all three separators anyway.

---

## 11d. Spells & skills ⏳ (built; casting live-confirmed via Gateway — effects/formulas still awaiting broad 4.95 verification)

The spellbook/skillbook is **server-authoritative**: the client ships spell *icons* (`SPELLINV.epf`,
`Icon.epf` in `Inter.dat`) but **no spell-definition table** — the server sends each spell's name, type and
prompt in the add-spell packet and the client just renders it. So the spell *roster* (names, class, level,
prompt) is external data, taken from the RTK `Spells` table (real NexusTK-lineage content — Kugnae/nation
geography matches, unlike Mithia). 906 rows → `Content.Spells` (841 after dropping section-header + inactive
rows). Each `SpellDef` = `Id, Key(SplIdentifier), Name(SplDescription), Type, PathId, Level, Question`.

- **`PathId`** = the class that learns it: **0 Peasant · 1 Warrior · 2 Rogue · 3 Mage · 4 Poet**, 5+ subpaths,
  99 = system/common. From the RTK `Paths` table (`Content.Paths`, `PthMark0` = class name; `PthMark1..5` are
  the per-rank titles). `Character.ClassName` (set with `@class`) resolves to a path id via `PathIdForClass`.
  > **Playable paths** (`Content.PlayablePaths`) are the five base rows plus the four **NPC subpaths** —
  > 6 Chung ryong (Warrior) · 7 Baekho (Rogue) · 8 Ju jak (Mage) · 9 Hyun moo (Poet). Paths.csv's other rows
  > are RTK's GM branch (5 Dreamweaver, 22 Archon) and twelve **PC subpaths** (10-21 Barbarian … Muse); none
  > is modelled here, and `@class` refuses them rather than minting a character with a rank ladder and no
  > spells behind it.
  >
  > An NPC subpath **is its base class plus a little**. `SpellsForClass` resolves `PathBaseOf` (RTK
  > `classdb_path`, the same PthType the gear gate already uses) and runs the whole base-class list through
  > it, then adds the subpath's own signature spell (`chung_ryongs_rage` / `baekhos_cunning` /
  > `ju_jak_evocation` / `hyun_moo_revival` — one row each, `SplLevel` 0, pinned to 99 since you subpath at
  > the cap). It does **not** add the base class's two **Dog spells** (below) — those never pass through
  > `SpellsForClass`, because it is also the tutor menu. Level-up growth and the exp table also go
  > through the base path, since `PathGrowth.csv` and `LevelExp.csv` stop at 4 — without that a Chung ryong
  > would silently level on the Peasant curve.
- **Rank titles are the same axis as `SplMark`.** Paths.csv gives a Warrior "Il san (W) … Oh san (W)" and a
  Ju jak "Force · Inferno · Pandemonium · Catastrophe"; the warrior mark-2 spell is literally named
  **Assault** and Chung ryong's mark-2 title is **Assault**, which is what pins the two together. So an NPC
  subpath's ranks draw the same `SplMark` spells its base class's ranks do. `Content.PathTitle(path, mark)`
  is what a character is CALLED, and every player-visible class line goes through `Session.ClassTitle` —
  the self-profile (`0x39`), the click-profile (`0x34`), the subpath-chat speaker label and Divine's read-out.
  `Character.ClassName` deliberately stays the BASE name so the path id, the subpath chat *channel* and the
  gear gate all keep resolving off one stable string. `@class` accepts a rank title as input for the same
  reason it displays one: `@class Inferno` = Ju jak at mark 2.
- **`Level`** = the character level required. Most advanced-class spells in this dump are level 0 (gated by
  path/subpath, not base level); Peasant has a 0/1/25 spread. The character rebuild (`@lvl`) grants every
  class spell with `Level ≤ Character.Level` — **plus the path-0 spells that are TRULY universal** (Soothe,
  Gateway, Mentor, Propose), which every class keeps after subpathing. `Content.SpellsForClass` unions
  `PathId == yourClass` with `PathId == 0`, so a Warrior/Rogue/Mage/Poet still gets those (Soothe & Gateway
  are level 1, so `@lvl ≥ 1`).
- **`Mark`** (`SplMark`) = the **subpath RANK** required: **0** the base 1-99 list · **1** Il san · **2** Ee
  san · **3** Sam san. 121 of the 906 rows carry one, and **every one of them has `SplLevel` 0** — so until
  2026-08-08 the column was read by nothing and those rows looked like level-1 spells. Every level-99
  character was handed the Il san *and* Ee san secrets of ranks it had never earned, which is also what
  overflowed the 52-slot book (a level-99 Ee san mage wanted 57 entries). `LoadSpells` now floors a mark
  row's level to `Content.MarkSpellLevel` (99: a rank sits *on top of* the cap, it is not an alternative to
  it) and `SpellsForClass` filters `Mark ≤ Character.Mark`. Ranks are cumulative — an Ee san keeps what Il
  san taught it.
  > **The rank ladder stops at Sam san** (`Content.MaxMark` = 3), and `@mark`/`@class` refuse anything above
  > it rather than clamping. Paths.csv names five ranks and Items.csv carries 34 Sa san (mark 4) items, but
  > **Spells.csv has 46 / 57 / 18 / 0 / 0 rows for marks 1-5**. A rank 4 would be a title and one level of
  > stat growth over nothing.
  > *Era:* the mark-3 rows do not date Sam san, and the dated pages put it after our 2001-07-09. tswolf's San
  > quests page lists only the marks "Il San, Ee San" (tswolf.com/quests/sanquests/, Wayback 2001-01-29 and
  > 2001-06-27), its spell pages link Il San and Ee San and no Sam San through 2001-08-21, and Wayback holds
  > no tswolf.com/spells/samsan.shtml; Nexus Atlas first documents it on its "Sam San, 3rd Mark" quest page
  > (nexusatlas.com/quests/samsan.php, first capture 2002-10-05). Sam san is kept as today anyway, with no era
  > gate (Caleb, 2026-10-24), and its promotion is to be built after Il san's and Ee san's. Sa san is later
  > still: its Atlas quest page is first captured 2008-10-15.
  > *Known consequence:* those 34 Sa san items stay unwearable, since `ItmMark` reads the same field. That is
  > right for a rank nobody can hold, and it reverses the moment `MaxMark` moves.
  > **Return/Approach/Summon are NOT part of that peasant-commons union (corrected 2026-07-27)** — the CSV's
  > `SplPthId=0` makes them look identical to Soothe/Gateway/Mentor/Propose, and none of the four scripts
  > (`return.lua`/`approach.lua`/`summon.lua`) contain a class check either, so this was genuinely
  > undecidable from the extracted data or the Lua source alone. Confirmed against the user's own play
  > knowledge: all three are **Rogue/Mage/Poet only, never Warrior**, each at a *different* level per class
  > (impossible to express in the single CSV row's one `Level` field). `Content.RestrictedCommonsLevel`
  > carries this as a `key -> {pathId -> level}` table that `SpellsForClass` consults before falling back to
  > the universal-PathId-0 rule.
  >
  > **Return's levels were corrected again the same day (2026-07-27)** after cross-checking
  > the scraped archive (`P1998_ARCHIVE`; tswolf.com + boards.nexustk.com tutor posts), which the
  > user ranks above the RTK Lua for real game facts: **Return is Mage 13 / Rogue 45 / Poet 32**, not 32 for
  > all three as first believed — tswolf's `mage.shtml` and a Mage tutor-board post independently agree on
  > 13. Approach stayed **Mage 20 / Poet 29 / Rogue 35**; Summon stayed **Mage 30 / Poet 38 / Rogue 53** —
  > both fully confirmed by the same archive. See the `nexustk-495-restricted-commons-spells` memory for
  > the full reconciliation (including the Soothe/Propose conflicts that came up and were NOT folded into
  > this table, since they resolved differently — see the learn-cost note below).
  >
  > **Real per-class learn COSTS (not just levels) are now enforced** for these 6 spells (Propose
  > deliberately excluded — see below) via `Content.LearnCosts` (`Dictionary<string, Dictionary<int,
  > LearnCost>>`), checked/charged in `ClassTrainerAbility.LearnSecret` (`NpcAbility.cs`) — NOT inside
  > `Session.LearnSpellFromNpc` itself, so the staff `@lvl` rebuild (which never calls that method — it
  > mutates `_char.Spells` directly) stays free, matching prior behavior:
  > - Gateway (all): 10 acorn + 10 rabbit meat, free
  > - Return: Mage 30 acorn + 50g · Rogue 100 acorn + 100g · Poet 1 yellow_scroll
  > - Approach: Mage 50 acorn + 20 snake_meat · Rogue 100 acorn + 10 fox_fur + 100g · Poet 1 gold_acorn + 100g
  > - Summon: Mage 80 acorn + 10 snake_meat + 50g · Rogue 100 acorn + 500g · Poet 70 acorn + 100g
  > - Mentor (all): 1 class item (Warrior maxcaliber / Rogue moonblade / Mage deaths_head / Poet
  >   wicked_staff) + 1000g
  > - Soothe: intentionally NOT in `LearnCosts` — tswolf confirms it's a free Newbie Quest reward (5 acorn +
  >   5 rabbit meat, no gold), matching the Lua exactly; conflicting tutor-board posts giving Mage-6/
  >   Warrior-8 gold costs are presumably a later-era trainer fallback, not the base mechanic.
  > - Propose: intentionally NOT in `LearnCosts` either — its `SplPthId=99` never matches any class's teach
  >   filter, so it was never learnable via a trainer at all. Its real "cost" is the `engagement_ring`'s
  >   shop price, already charged in the pre-existing `ChapelAbility.BuyRing` (§ marriage), which grants the
  >   spell directly via `ctx.LearnSpell(propose)`.
- **`Alignment`** (`SplAlignment`) = the sub-alignment: **-1** universal · **0** base/unaligned · **1** Kwisin ·
  **2** Mingken · **3** Ohaeng. Each class's spells split roughly evenly across 0/1/2/3 (e.g. Warrior ~21 each),
  and the four variants often **share a display name** (Rogue's "Maro's Remedy" ×4) — so teaching all of them
  produced literal duplicates *and* handed a character three alignments they can't use. `Character.Alignment`
  (0 unaligned / 1 Kwisin / 2 Mingken / 3 Ohaeng, set with `@align`) gates the teach: `SpellsForClass` keeps
  only universal (-1) + the character's own alignment, then dedupes by display name (preferring the exact
  alignment over universal). Result: **one** alignment set, no duplicates — Warrior 86→23, Mage 163→46 (also
  keeps every class under the 52-slot cap). Default alignment is 0 (Unaligned) = the base set.
- **`Type`** = the client's book discriminator (also the cast-shape selector): **1** = prompt spell (client
  asks `Question`, e.g. Gateway "Which Gate(N,E,S,W)?"), **2** = targeted (client sends a target entity id),
  **5** = self / no-target. type 1/2 render in the **Spell** book, type 5 in the **Skill** book — one packet,
  keyed on type.

**Add-spell — server→client `0x17`** (from RTK 7.x `clif_sendmagic`; body decrypted, big-endian):
```
slot(u8 = idx+1)  type(u8)  nameLen(u8) name  questionLen(u8) question
```
`0x17` is a **no-op in the main world dispatcher** (`remap[0x17-3] = 0x2a`, the default) — **exactly** like the
item opcodes `0x0F/0x10/0x37/0x38`, which are no-ops there too yet work live via the client's *secondary*
dispatcher. That shared property is the evidence the add-spell path is handled the same way (needs one live
confirm, same as items did). Learned ids persist in `Character.Spells` (book order) and are re-sent on world
entry by `RefreshSpells`. Remove-spell = **`0x18`** (`slot(u8=pos+1)`), used by `Session.ClearSpellbook` at the start of every rebuild.

**Cast — client→server `0x0F`** (RTK `clif_parsemagic`): `body[0] = book slot+1`, then by the learned spell's
type: **type 1** → the typed answer string (a **NUL-terminated ASCII** string right after the slot byte — RTK
`strcpy`s it from offset 6, *not* length-prefixed, unlike the server→client `0x17` strings); **type 2** → target
entity id (u32BE); **type 5** → nothing.
**Live-confirmed** (2026-07-25): casting sends `0f | slot+1 00` — the client casts with **just the slot** (no
target attached even for combat spells), e.g. `0f 1a 00` = cast book slot 26. So the server can't rely on a
packet-supplied target.

**`0x0F` is gated on the MAP before the slot is even read.** RTK wraps the whole opcode in
`if (map[sd->bl.m].spell || sd->status.gm_level) clif_parsemagic(sd); else clif_sendminitext(sd, "That doesn't
work here.");` (clif.c:11427) — a blanket per-map no-casting flag, not a per-spell one, and the reason magic
does not work inside a tavern, a shop, the three Gathering halls or a class trainer's building. The flag is the
`Maps` table's **`MapSpells`** column (`Content.SpellsAllowed`; 0 = blocked). It is **not** `MapIndoor`, which
is also set on every cave and dungeon in the game (Bat Cave, the Mythic caves) where casting must work —
"indoors" is the flavour of the rule, `MapSpells` is the rule. RTK's own dump is inconsistent about the class
trainers' buildings (Nagnang's and the later-era set are blocked, Kugnae's and Buya's — the same rooms one
era earlier — are not), so those 40 rows are corrected in `Maps.csv`. GMs bypass the gate, as in RTK.

`HandleCast` plays the cast animation (`0x1A` **type 6 = magic**) for the caster + peers, then applies the
spell's effect via a **data-driven magic engine** (`Session.ApplyCast`). The effect data is extracted straight
from RTK's Lua spell scripts by `re/extract_spell_formulas.py` → `game-data/spell_effects.csv` (gitignored),
loaded into `Content.SpellFx` and joined to each spell by identifier (the Lua table name == `SplIdentifier`).
Each row carries an **archetype** + the spell's **real RTK numbers**: the damage/heal *formula string*, the true
mana cost, buff stat+amount+duration, debuff kind+duration+chance, cooldown ("aether"). 621 of the ~841 spells
have a row (100 % of the four caster classes' teachable sets); the rest fall back to the keyword classifier.

`ApplyCast` dispatches on the archetype:
- **Damage** → evaluates the spell's damage formula (e.g. Spark = `50 + level/2`, the live probe's base since #200;
  RTK's own `15 + floor(level/2) + floor((will+3)/4)` is weight 0), unrounded, via `Content.Formula` — a tiny
  arithmetic evaluator over the caster's effective stats (`player.level/will/…`, `target.baseHealth`) supporting
  `+ - * /`, parens, decimals and `math.floor/ceil/random`. Spends the real mana cost, hits the packet target id
  if present else **the mob on the tile the caster faces** (like melee), reusing the world damage/despawn/exp path
  so a spell kill rewards exp/loot exactly like a melee kill.
- **Heal** → evaluates the real heal amount and restores the caster's HP (clamped to effective max).
- **Buff** → applies the spell's timed stat modifier(s) (might/hit/dam/…) for its RTK duration as an
  `ActiveBuff`, folded live into the HUD/profile/melee through `Session.Totals()` (gear + buffs) and persisted
  across a relog (§ timed-effect persistence). A buff belongs to an **exclusivity slot** (RTK `checkIfCast` /
  `spellTables.lua`: `mights`, `blessings`, `potency`, `shadowFigures`), and while that slot is running the
  cast is **refused** — *"You already cast that spell."* for your own, *"Another spell of this type is in
  effect."* for anyone's — rather than refreshed. Same slot machinery as curses, wards and holds, which is the
  point: Might used to be the one status family with no guard at all, so it was spammable *and* Might + Spirit
  Strength (one slot in RTK, two keys here) stacked into +6 might. The slot comes from
  `spell_verbs.lua`'s `BUFF_CATEGORY`, falling back to `spell_effects.csv`'s `cureCat` (which the extractor
  filled from RTK's `removeDuras(<table>)` calls; it could not see the `checkIfCast(<table>)` half, hence the
  explicit table).
- **TargetBuff** → the same thing cast on someone else (a poet's Valor, a harden on your pet) — **and the same
  code**. `arch_buff` and `arch_targetbuff` are two names for one `apply_buff` body; they differ only in who
  the buff lands on (`ctx:buffTarget("self"/"target")`, resolved once, exactly as `verbs.ward` has always done
  for its self-cast and ally-cast halves) and in the duration a row with no `durationMs` falls back to (60s vs
  300s). Deduction — Sanctuary's incoming-damage multiplier, which lives in its own scalar slot rather than as
  a stat delta — is the one branch, and is players-only. Keeping these as two bodies is precisely how the slot
  guard came to be enforced on one and not the other.
- **Debuff** → paralyze/sleep: freezes the target mob's wandering (`Mob.FrozenUntil`, honoured by `World.Tick`)
  for the RTK duration, subject to the spell's hit-chance roll.
- **ManaBattery** (Invoke / Spirit's Power / …) → verbatim RTK: HP cost = 40 % of *max* mana (HP floored at 100),
  refill mana to full, 22 s cooldown.
- **Gateway** (`Session.CastGateway`, base key `gateway`) → **live-confirmed on 4.95 (2026-07-25)** — warped
  correctly, which also proves the type-1 answer wire format above. Intercepted before the archetype dispatch (a
  teleport has no damage/heal row). Ports `Accepted/Spells/common/gateway.lua`: warp to one of the four **city gates** of the
  caster's kingdom, the gate chosen by the type-1 N/E/S/W answer, the kingdom by the caster's **region**
  (`Content.RegionOf`, from the RTK `Maps` table's `MapRegion` — 0 Kugnae · 1 Buya · 2 Mythic · 3 Nagnang; each
  region's city map + four gate spawn-boxes are the verbatim RTK coords). Lands on a random tile inside the gate's
  box (RTK's `math.random` spread) via the normal `EnterMap` redraw. Guards match RTK: the dead can't cast, a
  `MapWarpout == 0` map says *"It doesn't work here"*, a non-kingdom map *"Cannot find any gates!"*. **No mana cost**
  (RTK's gateway only calls `canCast`, a state check — no debit). Other regions (Baekdu/instances) TBD.
- **Cure / Utility / Summon / other Teleport / Dialog** → spend the real mana + acknowledge (bespoke behaviour TBD).

The formulas + costs are now **RTK-authoritative** (ported from the Lua), not server guesses. What stays a design
choice is the *targeting/routing* the 4.95 client doesn't pin down (self-heal vs. ally, front-tile vs. packet
target) and the effects we don't yet model (real summons, teleports, PvP).

**Spell effect graphics — wired via `0x29`.** Each cast plays its real `Effect.tbl` animation over the target
(Damage/Debuff) or the caster (Heal/Buff/Invoke): `Content.EffectAnim(fx, pathId)` returns the id, `Session.
SendEffect` emits the `0x29` packet (see §7.3 for the opcode). The id comes from the spell's own `sendAnimation`
call when it has one (buffs, Invoke → 11), else from the `pcalign` ladder in the shared helper — ported verbatim
to `Content.ZapEffect` / `HealEffect` (spark → 28, thunder bolt → 27, kwi-sin zap → 17, unaligned heal → 5,
kwi-sin heal → 65, …). Spells with no export row get a generic zap (4) / heal (5).

**Sound — wired via `0x19` type 0.** RTK pairs each `pcalign` with a **sound id** (zap = 56, heal = 4, fire = 88,
kwi-sin heal = 98); `Content.EffectSound` returns it and `Session.SendSound` emits a one-shot SFX using the same
`0x19` packet as background music with `type = 0` (the wav/sfx channel — see §7.3). Each cast broadcasts effect +
sound together (`Session.BroadcastFx`). The caster's `0x1A` magic pose still plays too.

**Icons:** the `0x17` add-spell carries **no icon field** (neither does RTK's `clif_sendmagic`) — the client
resolves the book icon internally, so no icon-id mapping pass is needed on the server side (unlike items/mobs).

**Slot cap:** the client's book array size is unconfirmed for 4.95; RTK 7.x uses 52 (`MAX_SPELLS`). The grant
caps at 52 (env `P1998_SPELLBOOK_CAP`) so an over-long teach can't overrun the client array — raise once a live
test confirms the real limit. The cap no longer bites: the mark gate and the ladder collapse below put every
playable path between **1 and 51** entries at every reachable rank and alignment (1 is Peasant, which owns
only Soothe). Worst case is an unaligned Hyun moo at Sam san — "Guardian" — at **51 of 52**, one slot of
headroom. Anything added to the Poet list, or a `Content.MaxMark` raised to Sa san, needs this re-measured.

**Ability ladders (`Content.LadderRungs` → `RespecSpellSet`).** Mage learns nine single-target zaps that differ
only in magnitude (Thunder Bolt → Spark → Singe → Ignite → Ion → Impact → Call Lightning → Stormstrike →
Hellfire), five 4-way ones, and nine heals across two ladders. Once you have Hellfire, Thunder Bolt is not a
spell you would ever cast, and holding all nine is what filled the book. The character rebuild therefore keeps
**only the top rung of each ladder you qualify for**. Ladders are per class and by SHAPE — single-target zap,
4-way zap, self-only heal, targeted heal, 4-way heal — so a class always keeps one of each. Everything not on
a ladder (buffs, curses, cures, traps, summons, the mark secrets) is one-of-a-kind and always survives.
Tutor NPCs are **unaffected**: `SpellsForClass` still offers the whole ladder, because climbing it one rung at
a time is the point of it.
> Only the **base (unaligned) key** of each rung is declared. `Content.BuildAlignFamilies` expands it to the
> Kwisin/Mingken/Ohaeng reskins by walking SplId order within each `(SplPthId, SplType)` block: the four
> variants of an ability are stored as one consecutive run with ascending alignment, and that adjacency is the
> *only* thing in the data linking them (they share no name, key stem or level). Validated by reconstructing
> `AreaZapMana`'s 20 keys and `AreaHealSpells`'s 16 from their 5 and 4 base keys — both hand-curated from the
> RTK Lua years earlier, both come back exact.

**Dog spells (`Content.DogSpellTiers` / `Content.DogSpellsFor`).** Two per base class, taught by that class's **Dog** rather
than the guild master — "the guildmaster is not involved in these spells": you kill something, come back, and
hand over items. Warrior **Greater Blessing 70 / Spirit Fury 99**, Rogue **Spot Traps 70 / Serpent's Fury
99**, Mage **Fissure 70 / Lava Surge 99**, Poet **Survive 70 / Fascinate 99**. Source: the archived nexusatlas
*Dog Spells* page (`re/fx/atlas_html/class_dog.html`, capture **2002-12-30**, after our 2001-07-09;
the eight spells are in era by tswolf's dog page, Wayback 2001-02-24). They sit in Spells.csv under the `===DOG SPELLS===` divider with `SplPthId 99` and `SplLevel 0`, so no
class filter could ever match them — **dead data until 2026-08-08**. Eligible paths are the **four base
classes and the four NPC subpaths**, never a PC subpath (`Content.CanLearnDogSpells`), which is the page's own
rule: "people in PC subpaths cannot learn these spells". Where the fan tutor-board posts disagree on the level (Greater Blessing
60-vs-70, Spirit Fury 91-vs-99, `re/archive_warrior_spells.md`), **the official listing wins** — the same
tie-break already used for Siege's aether count. They are deliberately **not** on any ladder: Fissure → Lava
Surge is a genuine tier pair, but collapsing it would erase half of the only thing a subpath grants outright.

> **The rebuild grants them too, gated on the linguist flag.** The pairing and the two levels live in
> `Content.DogSpellTiers`; `npc_dialog.lua`'s `DOG_SPELLS` owns the other half of each tier — the kills, the
> goods, and the atlas's *"must have 20,000 Vita or 10,000 Mana"* gate on the level-99 one — because that data
> *is* the dialog. `RespecSpellSet` takes a `dogFlag`, which `SyncSpellbook` passes as
> `HasDogFlag && CanLearnDogSpells(path)`, and merges the qualifying tiers in level-stamped. So `@dog 1` then
> `@lvl 99` produces the book a level-99 linguist of that class really holds, and `@dog 0` then a rebuild
> takes them back. **The flag alone still grants nothing** — set it and walk to the Dog and you start exactly
> where a finished linguist starts, which is what makes the teach flow testable. Before this the rebuild had
> no idea the Dog set existed and silently forgot every Dog spell, *including* ones earned at the Dog, since
> `@lvl`/`@class`/`@mark`/`@align` replace the book rather than top it up. The Dog's price is deliberately not
> re-checked by the rebuild, the same way it hands over tutor spells without charging the tutor's fee.

**Staff commands.** `@lvl <1-99>`, `@class <class or rank name>`, `@mark <0-3>` and
`@align <Unaligned|Kwisin|Mingken|Ohaeng>` all funnel into **one rebuild** (`Session.RespecTo`): reset to the
level-1 baseline, re-apply real `LevelUp`s on the current path, then **replace** the book with exactly
`RespecSpellSet(class, level, alignment, mark, dogFlag)`. It is a rebuild, not a top-up — the book can shrink,
and nothing from a previous class, level or rank survives.
- `@mark n` forces level 99 first, then runs `n` further `LevelUp`s with the counter reading 100, 101, … so a
  rank keeps growing on the same curve and its AC keeps falling past 1 (`100 − effective level`). The *stored*
  level goes back to 99, which is what the character sheet and the exp table understand. Nothing in the live
  game's data says what a rank is worth in stats, so "one more level per rank" is our model, not a ported
  number.
- `@lvl` **clears the mark** — a rank can't survive a move to level 40, and at 99 it would make `@lvl 99` and
  `@mark 0` mean different things for no reason.
- `@class` takes a class name (keeps the current rank) **or a rank title** (`@class Inferno` = Ju jak at
  mark 2). An NPC subpath forces level 99, for the same reason `@mark` does.
- **The stored level is always ≤ 99.** Ranks are levels past the cap only for the purpose of rolling growth;
  `_char.Level` goes back to 99 immediately afterwards, so the HUD, the character sheet, the exp table and
  every level gate see 99 and nothing else. Levels 100+ never exist as a value anything can read.
- `@stats` still overrides the curve outright, and by design a later rebuild discards it.
- `@dog [0|1]` is **not** a rebuild — it only sets or clears the Dog Linguist flag (`Content.DogFlagReg`) and
  its legend. But the flag is an input to the rebuild, so the next `@lvl`/`@class`/`@mark`/`@align` is what
  actually hands the class's Dog spells over or takes them back (see *Dog spells* above).
- **`@spells` and `@forgetspells` are gone** — both were additive-only, so between them the book could only
  ever grow, and a character that had been three classes carried all three books around. The rebuild
  subsumes them, and stays the main grant path.
- **`@spell <name|id>` is the one additive exception** (Tester tier, `Session.TeachSpellCmd`): teach a single
  named ability out of band — a lower ladder rung, another class's ability, a mark secret you haven't
  earned — matched by display name, then identifier, then raw row id via `Content.FindSpell`, and appended to
  the book instead of rebuilding it. It is capped by `SpellBookCap`, refuses a duplicate, and refuses the 8
  era-gated split-trap spells outright rather than handing over a spell that `RefreshSpells` would silently
  prune at the next login. Because the rebuild is authoritative, **any `@lvl` / `@class` / `@mark` / `@align`
  wipes what `@spell` taught** — teach after the rebuild, not before.

---

## 11e. NPCs & dialog ✅ (live-confirmed on 4.95)

NPCs are **stationary "mobs that don't fight."** They're placed from RTK `NPCs.csv` (`Content.Npcs` →
`World.PopulateNpcs`) as `Mob`s with `IsNpc = true`, so they reuse the entire creature pipeline for free:
`0x07` render (portrait/look = `0x8000|look`, §11a), viewport streaming (`SyncMobs`), and tile collision.
Differences from a real mob: `World.TryDamage` rejects them (indestructible), they never respawn, and a
click opens a dialog instead of a profile. NPCs with an RTK `NpcMoveTime`+`NpcReturnDistance` pace, leashed
to `Mob.Leash` (others stand still).

**Click → dialog.** The click (`0x43`, §7.1) resolves the entity via `World.MobById`; if it's an NPC, the
server runs its behaviour instead of the click-profile.

**Left-click vs. right-click (live-confirmed 2026-07-26).** `0x43` is a **left-click-only** packet. Right-
click in this client never reaches the server as an entity click at all — it's pure client-local walk-to-
click pathing (§10.3's self-walk primitive), and the "bumping into the mob" visual when you right-click
onto its tile is the client obstructing its own path locally (`0x69`, a documented no-op even in real RTK's
`clif.c` — commented out except for a GM debug branch). The server has **no way to intercept or suppress**
that walk animation; the only server-controllable feedback for "what IS that" is the left-click `0x43` path
below.

**Click a real (non-NPC) mob → name-only mini-text (changed 2026-07-26, user request).** RTK's own handler
(`clif.c` `clif_handle_clickgetinfo`, `BL_MOB` case) runs an `onLook` script whose player-facing branch is
gated on `player.gmLevel > 0` — a GM gets a minitext readout (name/id/level/HP/AC), everyone else gets no
packet at all (this was matched as-is from 2026-07-25 to 2026-07-26). We now deliberately diverge from that:
`HandleClickInfo` sends the bare `{mob.Name}` via mini-text for any regular player who left-clicks a real
mob, since right-click-to-walk can't be intercepted (above) and this is the only server-side lever available.
The name goes out on its own — no sentence around it (2026-08-07: it was `"It's a/an {name}."`; the pane is a
readout, and the other look-at lines there are bare too, so the article helper is gone).
Previously (2026-07-25 fix) it used to fall through to `SendClickProfile`, which unconditionally serializes
**your own** `_char`, so clicking a monster rendered your own profile mislabeled with the mob's id.

**Click another real player → their real profile (fixed 2026-07-26).** RTK's `clif_clickonplayer` — same
`0x34` opcode, populated from the TARGET's own data, not the clicker's. `SendClickProfile` now takes a
target `Session` (found via `World.Online.ById`) instead of implicitly using `_char`; its helpers
(`WeaponLook`/`ShieldLook`/`ProfileCellIcon`/`GearListText`) are called ON that target session, which works
because they're private instance methods of the same `Session` class — legal to call cross-instance. This
is more than cosmetic: the group/exchange status cells in that packet (§9.5) are what the client reads to
decide whether the "Group"/"Exchange" buttons on the window are enabled, which is the REAL way a player
starts a party or trade (§11l) — not a chat command. An id matching nobody at all is still a no-op.

### `0x30` server → client — the dialog packet (three sub-kinds)

Same frame + graphic-head as everything else (RTK `WFIFO(fd,N)` ↔ this server's `body[N-5]`; ported from
RTK `clif_scriptmes` / `clif_scriptmenuseq` / `clif_inputseq`). The head is shared; the **kind bytes**
`body[0..1]` and the tail differ:

| Sub-kind | `body[0..1]` | Tail after the prompt | Purpose |
|---|---|---|---|
| Text | `00 01` | — (a close/next box) | plain message |
| Menu | `02 02` | `count(u8)` then each item = `len(u8)+ASCII` | button list |
| Input | `04 04` | `0`(dialog2 len) `*`(0x2a sep) `0`(dialog3 len) `00 00` pad | free-text entry |

Shared head/prompt layout (all three): `body[2..5]` npc id(u32BE) · `[6]` head kind · **`[7]` portrait tag +
its payload (VARIABLE length, see below)** · a fixed 4-byte trailing descriptor `1, gfx(u16BE), color` ·
then `=1`(u32) · prev-button · next-button · prompt len(u16BE) · prompt (ASCII). With the ordinary 4-byte
sprite portrait that puts the trailing descriptor at `[11..14]`, the `=1` at `[15..18]`, prev/next at
`[19]`/`[20]`, len at `[21..22]` and the prompt at `[23..]`. **Portrait gfx = `0x8000|look`** (creature
sprite from Monster.epf, same encoding as the on-map spawn — RTK `clif.c:3190`); `0` → no portrait.

**The portrait is a discriminated union — `body[7]` is the tag** (RE'd from the 4.95 client 2026-08-06;
`0x44f530` → `0x46c050` switches `body[0]` across **9** dialog kinds, and all three we use run the same
head code: `lea edx,[edi+6]` → `call 0x4360e0` → `lea esi,[eax+0xa]`). `0x4360e0` dispatches on the tag and
returns the descriptor's total size, so **every field after the head shifts by it**:

| `body[7]` tag | Parser | Payload | Total |
|---|---|---|---|
| `0` | `0x436120` | **the 7-byte player appearance — byte-for-byte the same parser the `0x33` player look uses (§8)** | 8 |
| `1` | `0x436200` | `gfx(u16BE)` + `color` — an NPC/creature sprite | 4 |
| `2` | `0x436240` | item icon | — |
| other | — | no head at all | 0 |

So **a 4.95 NPC dialog can show a full player paperdoll as its portrait**, not just a creature sprite —
`Session.WriteHead` emits either form and `DialogPortrait.Doll` selects it. `body[6]` (head kind) is *not*
the selector: the client only inspects it for the value `2`, which force-overwrites the tag with `2`.
`AppearanceAbility`'s Change Face uses this to preview a candidate head on the player's own doll without
touching the character (§11e) — the same thing RTK does with a throwaway `clone` NPC and `dialogtype=2`.

### `0x3a` client → server — the reply

`body[0]` kind (`01` text next/close · `02` menu · `04` input) · `[8]` step · `[10]` menu index (**1-based**;
0 = cancel) **or** input length · `[11..]` input text. For input, RTK requires `step==2` to count as a real
submit (else it's a cancel). `HandleNpcDialog` just completes the awaited `TaskCompletionSource` — see below.

### Server-side design — async dialog + composable abilities

There are no Lua coroutines, so the flow is **async/await**: each `Dlg*` primitive sends a `0x30` and awaits
a `DialogReply`; the `0x3a` handler completes that task, resuming the behaviour **inline on the read thread**
(no cross-thread state races). Behaviours therefore read as linear script — `var c = await ctx.Menu(...)`,
a `while` loop for a shop that stays open — instead of callback trees.

An NPC is a **composition of reusable abilities** (`INpcAbility` in `Server/NpcAbility.cs`): `ShopAbility`,
`BankAbility`, `TransportAbility`, `TimeAbility`, `RepairAbility`, `AppearanceAbility`, `WarPaintAbility`,
`ClassTrainerAbility`, `ShadowStatsAbility`, `ChapelAbility`, … plus `InlineAbility` for one-off options.
`Server/NpcScripts.cs` maps NPC identifier → its abilities; unregistered NPCs derive abilities from their
data flags (so a plain stocked shop is zero-config). Each NPC declares only what's unique to it.

- **Shop** (`ShopAbility` → `DlgBuy`/`DlgSell`): Buy/Sell menus; catalogue in `Server/Shops.cs` keyed by NPC
  id (smith, inn), prices from `Items.csv` (`BuyPrice`/`SellPrice`). Uses menus, **not** the `0x2f` grid.
- **Bank** (`BankAbility` → `DlgBank`): deposit/withdraw coin (via the input box, capped 100M) and items;
  stored on `Character.BankMoney`/`BankItems`, persisted in the character JSON. Joint accounts out of scope.
- **Change Face / Change Gender** (`AppearanceAbility`, RTK `general_npc_funcs.changeFace`/`changeGender`;
  RTK's third option "Eyes" isn't ported). Browsing is a **try-on that mutates nothing**: each step draws the
  player's own paperdoll wearing the candidate head in the dialog's *portrait* slot (`DlgMenuFace` → the
  tag-0 player head above), so backing out needs no undo, an abandoned browse can't strand a wrong face in
  the save file, and the room doesn't watch you flicker through 90 heads. Only the paid pick writes anything
  (`CommitFace` → `RefreshAppearance` + save, which is also what finally shows peers the new head). RTK
  achieves the same isolation with a throwaway `clone` NPC + `player.gfxFace`. **The candidate list is
  `0..89`, the 4.95 client's real head range (§8) — NOT RTK's `200..216`, which is a later client's id space
  and rendered every browsed face headless** (fixed 2026-08-06). Because 90 entries is a lot of dialog
  round-trips, the loop starts on the face you're already wearing, shows "n of 90", wraps at both ends
  (RTK clamped — it only had 17), and offers a coarse "Skip ahead 10".
- **War paint** (`WarPaintAbility`, RTK `arena_master.lua` → `general_npc_funcs.warPaint`): the Arena Master's
  (`ArenaMasterNpc` — "Mountain"/"Tower") *only* service, an armor dye. Bleach back to base (10g) when dyed;
  else pick 1 of 8 team-battle colors (20g); level-99 characters are also offered special dyes (Brown/Wasabi/
  Super Wasabi, gated on base Vita/Mana) before the team menu. Writes `Character.ArmorColor` → appearance `[4]`
  (§8), redrawn on self + peers. Color values are RTK's palette indices — the 4.95 index→color map isn't
  calibrated yet (see the `@dye` note in §8), so team colors may need adjusting once swept.

**Spoken shortcuts** (`ShopAbility`/`BankAbility` also implement `INpcSayHandler`, so these skip the menu
entirely — real NexusTK commands, not an RTK-Lua invention; RTK's own NPC scripts have no buy/sell/deposit
speech grammar at all, click-menu only, which is exactly why these live in this server's own ability code
rather than being ported from a `*.lua` file):
- `"buy [my] all <item>"` — sell every one of a fuzzy-matched item to a nearby shop NPC
  (`SellItemToNpcByName`, amount = whole stack). `"all"` always needs an item after it — bare `"buy my all"`
  isn't a command and falls through as ordinary chat.
- `"buy [my] N <item>"` — sell exactly `N` of the item.
- `"buy [my] <item>"` (no quantifier) — sell one.
- Item-name matching tries the spoken word as typed, then singularized (`Content.FindItem` matches on the
  registry's singular names, e.g. `"acorn"`, while players usually say `"acorns"`). Independent of this NPC's
  own Buy catalogue — any shop-flagged NPC buys anything sellable, same set `DlgSell`'s menu offers.
- `"take my [all] <item|coin> [N]"` — **deposit** into the vault (`DepositItemToBank`); the bank "takes"
  from you. `"give my [all] <item|coin> [N]"` — **withdraw** (`WithdrawItemFromBank`); the bank "gives" to
  you. `"coin"`/`"coins"` targets money instead of an item. Note the word order is the *opposite* of the
  shop command above: `"all"` is always a prefix (`"take my all coin"`), but a specific count is a *suffix*
  after the item (`"give my coin 500"`, not `"give my 500 coin"`) — confirmed live, not a typo. No
  quantifier at all (`"take my acorn"`) moves exactly one.
- Confirmation reply is a **`0x0D` over-head bubble** (`Session.NpcBubble`), same channel a PC's own speech
  uses — **not** a `0x30` dialog box. Speech triggered these without opening a dialog, so the response
  shouldn't pop one open either; `SellItemToNpcByName`/`DepositItemToBank`/`WithdrawItemFromBank` all reply
  via `NpcBubble`, unlike the click-menu `DlgBuy`/`DlgSell`/`BankDeposit*`/`BankWithdraw*` paths, which
  correctly keep replying inside the `0x30` box the player already has open.

(An earlier draft of this also added a waypoint fast-travel network ported from RTK's `Waypoint.lua`,
reachable by saying `"transport"` or a destination keyword. That was reverted — it's an RTK-only addition
with no evidence of existing in original 4.x/5.x NexusTK, so `TransportAbility` is back to being a stub.)

---

## 11f. Monster combat AI, death/revive, and the home-city spawn

**Combat AI** (`World.Tick`, `Mob.TargetId`/`Level`/`AttackTime`) mirrors RTK's actual `mob_ai_normal.lua`
targeting for the *fights-back* half — a mob only chases once hit. (A single `TargetId`, not a threat score
per attacker: RTK's `AI/threat.lua` table is later-server content, see §"RTK's threat table".) `World.TryDamage` takes
an `attackerId` and, on a non-lethal hit, sets `mob.TargetId` to the attacker. Each tick, a targeted mob

**Unprovoked aggro** (fixed 2026-07-26 — "monster aggro is not working as expected, some monsters should
default to attacking the player, but they aren't"): the fights-back model above is only HALF of RTK's mob
AI, and it's the Lua half. RTK's C engine (`mob.c`) gates a mob's per-tick `move`/`attack` script call on
`mob->data->type` — `0=Normal, 1=Aggressive, 2=Stationary` (RTK SQL column `MobBehavior`) — and for
`type==1` it calls `mob_find_target` (a full-screen-ish area scan for the nearest `BL_PC`) UNPROVOKED,
*before* `mob_ai_normal.lua` ever runs. `mob_ai_normal.lua` itself never attacks first — so reviewing only
the Lua scripts (as the original port did) makes every mob look purely reactive, when most are actually
engine-level aggressive. Real data backs this up: of 713 mobs in the CTK reference dump, 603 have
`MobBehavior=1` (aggressive) and only 109 are `0` (passive — mostly herd/prey critters: rabbit, deer,
squirrel, doe, …). `MobDef.Aggressive` (`Content.cs`, parsed from `mobs.csv`'s merged-in `MobBehavior`
column) and `Mob.Aggressive` carry this through to a live mob. `World.Tick` now scans, for every mob with
`TargetId==0` and `Aggressive==true`, for the nearest living player within `AggroRadius` (8 tiles,
Chebyshev, from the mob's current tile — roughly the player's own 17x15 viewport) and locks onto them
exactly as if they'd just landed a hit; the existing chase/attack branch immediately takes over. A targeted mob
abandons wandering to path toward that player (greedy step, respecting walls/other entities) and, once
**cardinally adjacent** (fixed 2026-07-26 — was `Math.Max(|dx|,|dy|)<=1`, Chebyshev, which let a mob swing
at a player standing diagonally; user: "The horse can also attack me (melee) diagonally... not sure if
that's related" — it was the same bug as the horse-mount range below, both traced to Chebyshev distance.
Now `(dx==0 && |dy|==1) || (dy==0 && |dx|==1)` only, matching the player's own melee, which only ever
checks its single `FrontTile()` — a diagonal target falls through to the chase step instead, which moves
one axis at a time and closes to cardinal within a tick), swings on a cooldown (`AttackTime`, default 2s)
for `World.MobSwingDamage(mob.MinDam, mob.MaxDam)` damage (see below). It gives up and resumes wandering if
the target dies, disconnects, or strays more than `ChaseLeash` (8) tiles from the
mob's home tile. Both melee (`Session.HandleAttack`) and spell damage (`CastDamage`/the `Damage` case in
`ApplyCast`) pass `_char.Id` as the attacker, so either can provoke a fight.

##### The chase step is RTK's `FindCoords`, and it is meant to be stupid (2026-08-06)

`World.StepMobToward` is a port of **`rtklua/Accepted/Mobs/mob.lua:299 FindCoords`** — the real 4.95 chase
step, reached from `mob_ai_normal.move`/`.attack`. Every chase in the world goes through it (provoked mobs,
both pet movers, pet retaliation), so obstacle handling can't diverge between them.

RTK's algorithm in full: roll `checkmove = math.random(0, 2)`; if ≥1 try vertical-then-horizontal, else
horizontal-then-vertical; within that order try **only** the one or two directions that close the gap
(`mob.y < player.y -> side 2`, and so on), taking the first that isn't blocked. **That coin flip is the
entire cleverness of 4.95 mob pathing.** No A*, no map search, no lookahead, no memory.

What it produces — verified by simulating the port before shipping it:

| Situation | Behaviour |
|---|---|
| Open ground | closes normally (19 ticks over 10 tiles diagonal) |
| Single rock / corner, target off-axis | rounds it every time — the *other* axis is still "toward" |
| Short wall (5 wide), target straight behind | gets round it 200/200 — the shuffle reaches the end |
| Endless wall, target straight behind | **never** gets through |
| Pit, target on the near side, stairs on the far side | **never** escapes — paces at the bottom forever |
| Lateral spread while pinned | offsets −5…+5, peaked at 0/±1, tails rare |
| Stepping directly away from the target | **never** |

One deliberate departure from `FindCoords`, in its "nothing worked" branch: RTK flails at up to **11 fully
random sides**, which lets a stuck mob walk eleven tiles straight away from you — that does not happen in the
real game. Since 2026-08-24 the blocked-chaser fallback takes **one random step per tick** instead: sideways
or straight away from the target with equal odds, up to 11 draws until a free tile turns up, and it carries
no state between ticks — nothing remembers a run length or a chosen side, so a still-blocked mob re-rolls
independently on the next tick (`Server/World.MobMovement.cs`, the comment on `StepMobToward`'s fallback).

RTK's other fallback — re-rolling `mob.target` to a random nearby player when it can't reach the current one
— **is** ported, gated on `Mob.Aggressive` (a creature fighting only because you provoked it should keep
pacing after *you*, not go find someone else). It's usually a no-op, since there's usually only one player in
range. Landing a hit always outranks it: `World.TryDamage` re-points the mob at whoever hit it and clears any
in-progress shuffle, so **zapping something always drags its aggro onto you, wall or no wall.**

**Do not make this smarter.** A mob that solves a pit is wrong for this game. The bug being fixed here was
narrower: the step was previously a single greedy move that gave up when blocked, so a mob with a wall, a
tree, or another creature between you would stand on one tile facing you and never even try sideways.

**Mob damage was massively under-tuned (fixed 2026-07-26 — user: "Going into Dragon 1 as a newbie with
250HP, I should not be surviving even a single hit... 50 damage or something. REALLY off"):** the original
swing formula was `Level/2 + 0-2` — a level-99 dragon hit for ~50, when it should one-shot a newbie. Root
cause: `Content.LoadMobs` parsed `mobs.csv`'s `MinDmg`/`MaxDmg` columns into... nothing — `MobDef`
never carried them, so the REAL per-mob damage data (e.g. `dragon_hatchling` = 2500-3500, `old_dragon` =
146250-183750) was silently discarded in favor of a synthetic Level-based stand-in with no basis in RTK.
This turned out to be the first of several "the CSV column was right there, just never parsed" gaps — see
the full combat pass below, done in the same sitting once the user asked to "check spells, my melee, mob
melee, backstab, flank, etc — let's get it right."

**The full combat pass (2026-07-26).** Everything here is ported from RTK's REAL, LIVE combat engine —
`rtklua/Accepted/Scripts/hitCritChance.lua` (crit/hit rolls) and `swingDamage.lua` (the actual damage
pipeline) — NOT the commented-out/dead damage math sitting in the C engine's `mob.c` (which looks like the
"real" formula at first glance but is entirely inert; the live path is 100% Lua). Shared, stateless math
lives in `Server/Combat.cs` so both attack directions use one verified implementation.

- **Mob→player** (`World.MobSwingDamage` + `Session.ApplyMobHit`): raw swing = 3 independent uniform draws
  over `[MinDam/3, MaxDam/3]`, summed and floored +1 (RTK `_getMobSwingDamage`; concentrates near the
  midpoint, not a flat roll edge-to-edge). Then `Combat.ApplyArmor` — `floor(raw * (1 + max(armor,-80)/100))`
  against the player's effective AC (`_char.Ac - Totals().armor`; AC is signed/lower-is-better, armor
  SUBTRACTS from it) — floored at -80, so armor can reduce a hit to as little as 20% but never fully negate
  it. THEN the positional 2x (below). Mobs also roll a crit chance (`Combat.RollCritChance`, hit/5 once past
  the base hitchance window) purely for the 0x13 visual byte (255 vs the calibratable normal byte) — real
  RTK never multiplies MOB damage by its own crit, only a player's (see below), so this doesn't scale `dmg`.

- **Player→mob** (`Session.PlayerSwingDamage`, replacing the old flat `EffMight`-based stand-in): another
  "CSV column parsed nowhere" bug — `Items.csv` has always carried `ItmMinimumSDamage/MaximumSDamage/
  MinimumLDamage/MaximumLDamage` (a weapon's REAL swing range) but `ItemDef` never read them, so player
  melee had no damage-range component at all. Now: `s = random(weapon's Small range)`, or its Large range
  if the target is a boss mob (`MobDef.IsBoss`, merged from CTK `MobIsBoss` — RTK's actual Large-damage
  condition) and the weapon carries one. `swing = (s/2 + dam*2.5 + might/8 + classFactor) * critical`
  (`dam`/`might` floored at 1; `classFactor` is RTK's flat per-class bonus, 9 Warrior / 7.5 Rogue / 0
  everyone else; `critical` = 3 on a genuine player crit, via `Combat.RollCritChance`'s player-attacker
  branch — 55+(grace+level)*0.75+hit-(targetGrace+targetLevel)*0.5 hitchance, flat 3% crit-within-window).
  Then `Combat.ApplyArmor` against the mob's `Ac` (RTK SQL `MobArmor`, merged in — floor -95 for a mob
  target), then the positional 2x. `rage`/`invisible` multipliers are hardcoded to 1 — no rage buff or
  stealth state exists in this server yet, matching RTK's own unbuffed default.

- **DAMAGE SPELLS TAKE THE TARGET'S AC TOO (2026-08-21).** They did not until this date: `Combat.ApplyArmor`
  was reached only by melee, mob-on-player hits and the sacrifice strikes, so every nuke in the game dealt
  face value to any mob no matter how armored. The live Spark probe refutes that outright — it solves
  `Spark = floor((50 + level/2) × (1 + ac/100))` across **19 readings with zero residual**, spanning 9 mobs at
  AC 40–100 *and* 10 self-casts with the caster's own AC varied by swapping gear. So the same armor term
  governs magic and melee alike, and the old `ReceiveSpellDamage` comment claiming *"physical AC does not
  apply to magic (the caster's deflect roll already gates a spell)"* was simply wrong.
  - Mob side: the armor term at each damage site — `LuaDamageTarget`, `LuaMagicDamage` (the `Damage`
    archetype), `LuaAreaZap` (4-way) and `LuaTargetAreaZap` (the dog 5-way). It lives at the call sites
    because `World.TryDamage` deliberately knows nothing about armor. Each site hands `TryDamage` the product
    unfloored (`Session.SpellRaw`), and `TryDamage` floors it once with a slept or dozed creature's amplifier
    in it (#363); with none armed that is `Session.SpellNet`'s figure, the same floor as before.
  - Player side: centralised **inside `ReceiveSpellDamage`**, so every PvP spell path nets exactly once and
    none can forget. Callers pass RAW; `LuaSacApply` and `ApplyOverflow` used to pre-net and no longer do.
  - Scale of the change: positive AC amplifies, so a rabbit (+100) now takes **double** from a nuke, while a
    marsh ogre (−65) takes **35%**. Every damage number in the game moved.
  - NOT extended to traps, pet mob-on-mob hits or harvest nodes — no measurement exists for any of them, and
    guessing would move balance with nothing behind it. Ambush and melee already net attacker-side.
  - **Base settled 2026-10-21 (#200):** the same probe says Spark scales off `level/2` and rules will out ("a +1
    will item changed nothing"), and Caleb took that: the seven Spark-family rows (Spark, Glimpse of the Void and
    Nature's Ire for mage; all four for poet) read `50 + level/2`, no longer `50 + floor((level + will)/4)`. The
    half is kept to the armor step: `Session.CastArch` no longer rounds the evaluated amount, and `ctx:magicDamage`,
    `LuaMagicDamage`, `LuaDamageTarget`, `SpellNet`, `HitPlayerWithSpell`, `ReceiveSpellDamage` and
    `DamageIntake.Raw` carry a double to the armor step: `SpellNet`'s `Combat.ApplyArmor` against a creature, and
    `Combat.ArmorFactor` and `Combat.FloorOnce` in `Session.TakeDamage` against a player. So a level-15 self-cast at
    AC 85 lands floor(57.5 × 1.85) = 106 as read live (rounded first, it was 107). **Mage Bolt joined that row on
    2026-10-22** (Caleb, decision sheet item 3; #200): `bolt_mage` reads `50 + level/2` for 20 mana from its own
    spell_effects.csv row, as the other seven do. `game-data/SpellParams.csv` still binds it to `verbs.magic_damage`,
    which keeps Bolt's refusals and now takes the row's amount and mana (`ctx.spellAmount`, `ctx.spellMana`, the
    same evaluation `CastArch` hands the archetype); its old 2 + 1.5 × Will for 4 mana had no source.
    `Tests/SparkFamilyBaseTests.cs` casts each row through the real frame.

- **`ApplyArmor` forms its deduction as `(100 + armor)/100.0`, not `1.0 + armor/100.0`** — equal in real
  arithmetic, not in IEEE754. The latter evaluates to 0.19999999999999996 at the human floor, so a maximally
  armored player took 19% of a hit instead of 20%, a full point of error at exactly the value the clamp makes
  most common. Fixed 2026-08-21 when the formula became load-bearing for spells as well as swings; all 19
  Spark readings are unchanged by it. `Tests/CombatArmorTests.cs` pins both.

- **The positional "attacked from behind" 2x** (`Combat.IsBehindTarget`) applies to BOTH directions,
  UNCONDITIONALLY: RTK `swingDamage.lua`'s base `side==target.side` rule (attacker and target facing the
  SAME direction, attacker on the target's blind side) — every swing gets this check regardless of class.
  Dir 0=N/1=E/2=S/3=W already matched RTK's own numbering with no translation needed.

- **Backstab/Flank (fixed 2026-07-26, corrected after initial mis-scoping — user: "Flank and Backstab come
  from the warrior spells that provide those abilities. I think only warriors ever get them"):** these were
  first assumed to be rare legendary-weapon procs (several `Items/Weapons/*.lua` scripts DO read
  `player.backstab`/`player.flank` to gate a bonus proc) and descoped as "no data to drive it." Wrong —
  they're real Warrior-only skills (`Spells.csv`: `backstab_warrior`/`flank_warrior` + 3 aligned variants
  each, all `SplPthId=1`, level 15/20 per their Lua `requirements()`), timed self-cast stances (625s) whose
  `recast`/`uncast` hooks just flip `player.backstab`/`player.flank` — this IS the data the weapon scripts
  read, not a separate system. `Content.LoadSpellFx` now parses `cureCat` (previously dropped entirely,
  despite tagging all 4 aliases of each skill "backstabs"/"flanks" — a clean, data-driven way to catch every
  alignment variant without hardcoding 8 identifiers); `Session.ApplyCast` intercepts that `CureCat` before
  the generic archetype dispatch (the old path fell into `CastBuff`, which no-ops on an empty `BuffStat` —
  it was spending mana and saying "You cast Backstab" with zero effect) and routes to `Session.CastStance`,
  which just arms `_backstabUntil`/`_flankUntil`. `Session.PlayerSwingDamage` then applies
  `Combat.IsBackstabAngle`/`Combat.IsFlankAngle` (RTK's literal position tables — OPPOSITE facings for
  backstab, PERPENDICULAR for flank, each with their own per-angle position condition) as additional
  independent 2x multipliers on top of the base positional bonus, exactly mirroring `swingDamage.lua`'s own
  three sequential (and in principle stackable) if-blocks. Mob attackers never have either stance (RTK mobs
  don't cast Warrior skills), so both checks are player-attacker only.

- **`Session.RollDeflect`** (magic resist) now folds in `target.Protection` (RTK SQL `MobProtection`, a
  DIFFERENT stat from `MobArmor`/`Ac` above — RTK melee and magic defense don't share a stat) — previously
  hardcoded to 0 for lack of a source column, now `prot = target.Protection + (willDiff/10 + 0.5)`, matching
  RTK clif.c's real deflect roll exactly.

- **Data merged in from the CTK reference SQL dump** (`RTK-Server/database/references/CTK - closing -
  08-31-2019.sql`, same source as `MobBehavior` — see §11f's aggro fix above) into `mobs.csv`:
  `MobIsBoss`, `MobProtection`, `MobArmor`, `MobHit`. Plus `Grace`, which was already a plain column in the
  CSV but — like `MinDmg`/`MaxDmg` before it — simply never parsed into `MobDef` until this pass.

- **Rage tiers and Rogue stealth (fixed 2026-07-26 — user: "Did we also implement warrior/rogue damage
  multiplier spells? they get `rage` spells until 99 I think (rogue invis also has a damage multiplier)"):**
  `swingDamage.lua`'s `rage`/`invisible` terms (previously hardcoded to 1, i.e. no-ops) are the two BIGGEST
  multipliers in the whole formula and were completely unwired, same silent-no-op failure mode as
  Backstab/Flank (a `CastBuff` call with no numeric `BuffStat` to apply). Real spells, found in
  `RTK-Server/rtklua/Accepted/Spells/{warrior,rogue}/`:
  - **Rage progression** (`player.rage`, a flat multiplier on the ENTIRE swing): Wolf's Fury (rage 2 — Warrior
    lvl 7 / Rogue lvl 34) → Tiger's Fury (rage 3 — Warrior lvl 24 / Rogue lvl 56) → Dragon's Fury (rage 4 —
    Warrior lvl 45 only) → Baekho's Rage (rage 5 — Rogue lvl 99 only, the endgame tier the user recalled).
    `Content.RageAmountFor`/`Session.CastRage` (625s duration, 90-150 mana per tier). Real RTK REJECTS
    re-casting any fury while one is already active rather than letting a stronger tier overwrite a weaker
    one — ported faithfully (`EffRage > 1` guard), not "fixed" into a friendlier overwrite.
  - **Rogue stealth** (`player.state==2` → `invisible=9`, Rogue-only, lvl 30): Invisible + 3 same-mechanic
    aliases (Spirit's Form/Life's Cloak/Glass Form). A one-shot 9x sneak-attack burst — landing the boosted
    hit immediately strips the stealth (RTK `removeDuras(invis)` after a nonzero hit), ported in
    `Session.PlayerSwingDamage` (reads `Stealthed` once, applies the 9x, then clears `_stealthUntil` — so
    only the NEXT swing gets the bonus, not every swing for the whole duration). `Content.IsStealthSpell`/
    `Session.CastStealth`. **Damage multiplier only** — RTK's `PC_INVIS` state also hides the player's
    sprite from other clients (`clif.c`), which would need viewport/`ShowPlayer` changes this pass doesn't
    touch; casting Invisible here does NOT make you invisible to other players, only boosts your next hit.
  - Both families had `SplLevel=0` in the CSV export (their real level lives in each spell's Lua
    `requirements()` function, which the extractor never captures for Type-5 skills) — overridden with the
    real values above in `Content.SpellLevelOverrides`, scoped to just these 7 identifiers. Every OTHER
    Type-5 skill likely has the same `SplLevel=0` problem; this pass does not fix that generally.

- **Full spell-functionality audit (2026-07-26 — user: "Have we not implemented all spell functionality?
  ...rogues and mages have 'shapeshifting' spells. They don't seem to work. I want all spell functionality
  to work as expected"):** cross-referencing every spell archetype our CSV export tags against
  `Session.ApplyCast`'s dispatch found the same class of gap repeated across ~120 spells: anything tagged
  `Utility`/`Summon`/`Teleport` (no numeric `BuffStat`/damage `AmountExpr` the generic archetypes can
  express) falls into `CastMisc`, a pure stub that spends mana and prints "You cast X." with **no real
  effect** — the same silent-no-op failure mode as the earlier Backstab/Flank/Rage/Stealth fixes, just at
  much greater scale (29 Rogue + 24 Warrior "skill" spells, 46 Poet spells including a whole pet-summon
  family, and ~29 Rogue/Mage/Druid/Merchant/Chongun shapeshift ("morph") identifiers). Rather than fix all
  ~120 at once, tackled in priority chunks (user chose "cheap wins first, then work up"); chunks 1-3 (below)
  fixed 138 of ~145 real candidates. Still pending: `magis_bane`'s curse dependencies (1 + 4 unmapped). The
  gold-drop gambling stance (`chance_rogue` + 3 aliases) was explicitly descoped at the user's request (high
  complexity, low value).

  **Shapeshifting — RESOLVED as a peer-visible illusion (chunk 3, 2026-07-26), overturning the chunk-2
  "confirmed blocked" verdict.** RTK groups `feral_rogue`/`rodent_rogue`/`gangrel_rogue`/`beast_rogue` (+
  their Kwisin/Ming-Ken/Ohaeng alignment aliases, Mage's `beast_mage`/`gangrel_mage`, and standalone costume
  morphs like `dagger_uniform`/`marketer_guise`/`wilderness_guise`) in a Lua table literally named `morphs` —
  29 real castable identifiers total (not ~14 — the earlier estimate missed the mage/Druid/Merchant/Chongun/
  Barbarian side of the table). Casting one sets `player.disguise` to an animal/NPC look id + `player.state=4`
  for a fixed duration; purely cosmetic in RTK's own Lua, no stat/combat effect anywhere.
  - **CORRECTED 2026-07-26 — the "caster's own screen can't render it" verdict was wrong; it only proved
    `0x33` specifically can't.** `0x33`'s handler (`0x44fef0`) really does hit a hard wall: it calls the shared
    entity factory `0x44d7d0`, which for a PEER id correctly builds a real creature entity (`0x461a50`, vtable
    `0x4cd098`, `[ent+0x178]`/`[+0x17c]` set from the packet's own look value), but then EVERY renderKind branch
    (1/2/3: `0x4500ba`/`0x4500e8`/`0x4500a7`) makes one more, LATER, unconditional call —
    `push 0x4f2a84 (literal); call 0x463380` — a hardcoded player-archive constant, never read from the
    packet, for every entity `0x33` ever builds. That part still stands: `0x33` cannot draw a monster sprite,
    full stop. Where the earlier pass went wrong was assuming this was the ONLY path into the self entity.
    Deeper tracing of `0x44d7d0` found a THIRD branch, taken whenever the incoming entity id equals the
    client's own self id (`mov ecx,[ebx+0x40c]; cmp edi,[ecx+0x108]`, `ebx+0x40c` = pointer to the self-id
    holder): it skips BOTH the peer ctor and `0x33`'s hardcoded-archive draw call entirely, instead calling
    `0x44c660` (camera recenter to the packet's x/y — a no-op when it's already your own position) followed by
    `0x461e30`, which writes the look descriptor straight into the SELF entity's own struct fields
    (`self+0x178/0x17c/0x180`) — the identical fields a real monster entity carries. Critically, this self-id
    branch is reachable from `0x07` too (`0x44fdb0` passes discriminator 1 for any look in the monster range
    `0x8000..0xBFFF`, which lands in the very same `0x44d7d0` branch `0x33`'s kind 0/1 uses) — so sending the
    CASTER their own `0x07` creature-spawn, addressed to their own id, drives the self entity down this
    self-only path and updates its look descriptor for real, with no player-archive override anywhere in
    the way. Matches actual RTK behavior (the caster does see themselves transform).
  - **Fix:** `Session.ShowPlayer` — the single choke point every peer re-sync path already funnels through
    (join, map-change, equip/mount refresh) — reroutes a morphed player's entry to the real `0x07` Monster.epf
    creature-spawn builder (the SAME one `ShowMob` uses for actual mobs, `0x8000|look`), for BOTH peers' views
    AND the caster's own (`CastMorph`/`RevertMorph` now call `ShowPlayer(this)` directly on top of the
    `except:this` peer broadcast, rather than skipping self). The target id is still the caster's own
    persistent player id — never added to `World`'s mob list — so `HandleClickInfo` (which checks `MobById`
    before `Online.ById`) keeps resolving clicks to the real player profile/party/trade flow unchanged.
    `Content.MorphSpells`/`MorphDispatchSpells`, `Session.CastMorph`/`RevertMorph`, `PlayerSnapshot.MorphLook`,
    `Session.ShowPlayer`'s branch, `World.Tick`'s expiry sweep. Also added: a synthetic zero-stat entry in
    `_buffs` for the active morph, so the self-profile's buff/duration box (`BuffBoxText`, issue #6) actually
    shows the remaining time — it was silently missing before, since morphs never touched `_buffs` at all.
    Deliberately accepted tradeoffs: a `0x07` entity carries no name field (§7.2), so a morphed player shows
    no floating nametag to others. Mutual exclusion simplified to one slot (any morph replaces any other;
    re-casting the same one toggles it off) rather than RTK's per-identifier duration quirk (casting a second
    morph while one is active leaves both timers ticking in real RTK — a bug with no gameplay purpose, not
    reproduced).

  **Chunk 1 ("cheap wins") — fixed:**
  - **Self-sacrifice strike family** (Rogue Lethal Strike/Afterlife's Embrace/Ming-Ken's Judgement/
    Calculating Blow; Desperate Attack/The Void's Measure/Beastly Frenzy/Tilting the Balance; Warrior
    Berserk/No Fear/Tiger's Pounce/Wind's Blast; Whirlwind/Death's Angel/Nature's Own/Bladedance — 16 spells,
    4 base mechanics × 4 alignment aliases each). Ported verbatim from `rogue/lethal_strike.lua`,
    `rogue/desperate_attack.lua`, `warrior/berserk.lua`, `warrior/whirlwind.lua` (+ `rogue/backflow.lua`,
    `warrior/overflow.lua`): a facing-tile physical attack (not a targeted cast) computed from the CASTER's
    OWN pre-cast HP/MP, armor-netted the same way melee is (`Combat.ApplyArmor`) to find any overkill. The
    Rogue pair "backflows" overkill — up to half refunds to the caster as HP+MP, each capped at half their
    pre-cast values. Landing the hit ALWAYS costs the caster a big chunk of their own HP (halved/thirded/
    nearly wiped, family-dependent) regardless of overkill. Whirlwind's damage factor AND post-hit HP cost
    differ by the caster's own `_char.Alignment` (RTK reads the caster's real alignment stat directly, not
    which of the 4 aliases was cast — matches since a player would only ever be granted the alias matching
    their own alignment). Chin-Baek-Ho-Ryung (the Black Potion's 10s ward — *not* `baekhos_rage_rogue`, which
    is a rogue fury and could never fire on these two) adds a further 1.5x to Berserk/Whirlwind. All of the
    above now lives in `verbs.sacrifice` (`game-data/spell_verbs.lua`), hot-reloadable; the engine side is
    `Content.SacrificeFamilyFor` + the `LuaSacFrontMob`/`LuaSacApply`/`ApplyBackflow` primitives. On a PvP
    map the faced tile may hold a **player**, who takes the hit through the normal PvP path
    (`HitPlayerWithSpell`: deflect roll, Deduction, death penalty); off a PvP map peers are skipped.
  - **What happens to the OVERKILL is ERA-GATED, and off by default.** Both mechanisms that spend the
    leftover damage were added to the live game *years* after our 2001-07-09 target, so at the shipped
    `EraDate` all four strikes are a plain one-tile hit. The strikes themselves are era-appropriate and are
    never gated — only the disposal of the overkill is. See `docs/common/Era-Gating.md`.

    | Mechanic | Class | Introduced | Era key | Gate |
    |---|---|---|---|---|
    | Overflow — splash onto adjacent tiles | Warrior | 2007-04-10 | `warrior_overflow` | `Session.LuaOverflow` |
    | Overkill ("backflow") — refund to caster | Rogue | 2008-09-18 | `rogue_overkill` | `Session.LuaBackflow` |

    Two keys, not one, because the seventeen-month gap between them is itself a playable era — the window in
    which warriors had overflow and rogues had no answer, which is the whole subject of Nexus Atlas's
    2008-09-19 "Rogue balance" editorial. Gated in C# rather than in `verbs.sacrifice` so a script edit alone
    cannot lift them.

    **Overflow's formula is disputed and we implement the later one.** The Warrior tutor board's *Overflow
    Damage FAQ v1.6* (Tynan) gives `(done − needed) × 0.2` to each target, **not** divided. Yttribium/Ixeus's
    *Revised overflow formula*, posted explicitly to correct it and the only one of the two reporting
    measurements, gives `((done − needed) ÷ targetCount) × 1.05` — split, with a 5% bonus. We implement the
    revision, and RTK's `warrior/overflow.lua` independently lands on the same shape, which is corroboration
    from a source that never read either post. Both agree on the rest: an "Inferno cross" of the four
    orthogonal tiles **around the target**, computed at 0 AC then modified by each recipient's AC, and kills
    award full exp even on a mob that was never touched directly.

    **One hop only.** RTK's Lua recurses — each splashed kill re-splashes from its own tile — and we ported
    that. The FAQ is explicit that *"a target killed by overflow damage will not cause more overflow
    damage"*, so the recursion was removed; do not reintroduce it from the RTK Lua. Because one of the four
    cells is always the caster's own, a ground-level splash reaches **at most three** mobs.

    **Rogue overkill's ordering is load-bearing.** The strike's own HP/MP cost is charged *before* the
    refund, because that is the only order in which the tutor board's arithmetic works: Lethal Strike takes
    50% vita and the refund restores up to 50%, so *"a person has the potential to gain all of their current
    stats back"*; Desperate Attack zeroes mana and the refund restores *"50% mana, nothing more"*. We had it
    the other way round, which halved LS's refund and destroyed DA's mana refund entirely. Two distinct
    caps, both enforced: the refund is at most 50% of the pre-cast pool, and it can never carry a pool above
    what it held at cast time (so a wounded rogue cannot use a vita strike as a heal).

    **Both work in PvP, against RTK.** *"Does overflow damage work in PK? Yes, it does"* (FAQ VI), and the
    rogue doc's central worked example is itself a PK kill — a Desperate Attack on a player, ending *"890k
    is split between vita/mana; 445k healed to both stats"*. RTK does neither: its `overflow.lua` is
    mob-only, and `berserk.lua` calls `Overflow.Cast` from its mob branch but not its PC branch. The archive
    describes the live game and outranks the reimplementation, so:

    - `LuaSacApply` returns real overkill from a player kill instead of a flat 0, which is what feeds both
      mechanisms. `ReceiveSpellDamage` now returns the damage it applied *uncapped by remaining HP*, so a
      killing blow can report how far past zero it went; a deflect reports 0 and yields no overkill.
    - `ApplyOverflow` splashes peers on a PvP map, sharing one pool with the adjacent mobs rather than
      taking a second helping. **The caster is excluded explicitly** — the struck tile is adjacent to us by
      construction, so we are always standing on one of the four cells (the FAQ draws exactly that picture),
      and without the test every overflow would splash its own caster.

    **The target's physical AC applies**, on both sides. The archive computes these strikes against armor —
    *"XRogueX DA's iPoeti for 1800k damage to 0 AC. AC modifier -50, damage done is 900k"*, i.e. our own
    `dmg × (1 + ac/100)`. Mob targets net in `LuaSacApply`; player targets net inside `ReceiveSpellDamage`,
    which now armor-nets **every** PvP spell path (see the section above on spells and AC), so neither the
    strike nor the splash pre-nets — doing so would apply it twice. Load-bearing for the rogue side in
    particular, since the refund is computed from post-armor overkill.

    **Slash and Feral Berserk overflow too, via a second hook.** The FAQ lists Slash among the warrior
    triggers and Ixeus's revision names Feral Berserk directly (*"the usual formulae like 0.75 x V for old
    Zerk, 0.85 x V for Feral Zerk"*); Nexus Atlas agrees the list is broad (*"overflow works on all of the
    warrior attacks, including their Sam san attack"*). Neither is in `SacrificeAliases` — they are ordinary
    `Damage`-archetype spells whose RTK scripts export cleanly — so they reach the splash through
    `Session.TryArchetypeOverflow` on the `LuaMagicDamage` path instead, gated on the same
    `Era.WarriorOverflow` key. RTK gave neither of them overflow (`warrior/slash.lua` calls
    `removeHealthExtend` and stops, with no `Overflow.Cast` anywhere in it), so this is the archive
    overriding the reimplementation again. **Assault is deliberately excluded** — a vita-funded warrior
    strike like these two, and an easy fifth entry, but no source lists it, and absence of evidence must not
    add content any more than it removes it.

    > **Units.** The FAQ derives overkill from the *post-armor* hit (its example nets a Whirlwind to 787,500
    > against −50 AC and calls 287,500 of that excess), and `TryArchetypeOverflow` is handed a netted figure
    > to match. That was impossible while the `Damage` archetype applied no armor at all; it does now, via
    > `Session.SpellNet`.

    Separately, the FAQ flags rogue **Focused Blade** overflow as a bug and a later Atlas post records it
    *"fixed to remove the overflow or splash damage"* — so its presence in `SacrificeAliases` is
    period-correct for a window, not an error.

    **No group exemption**, and the evidence for adding one is thinner than it first looks. RTK's
    `slash.lua` does skip same-group players — but not in any splash. Its `getAliveObjectsInCell(m, x, y,
    BL_PC)` reads **one cell**, the faced tile, and returns everyone standing on it; players may stack on a
    tile in NexusTK, so that list can hold several, and the script loops them. (Note the asymmetry in the
    same script: the mob branch takes only `d[1]`, the first mob, while the PC branch hits all of them.) So
    the group check is "don't hit the party member standing on the enemy you're attacking", a different
    situation from a cross-tile splash, and it says nothing directly about overflow. Our PvP layer has no
    group concept anywhere, so overflow doesn't either — flagged because an arena splash currently hits your
    own party.

    > Related pre-existing divergence: `World.PeerAt` is a `FirstOrDefault`, so we hit exactly **one** player
    > per tile — on the primary strike as well as the splash. RTK's stacked-tile multi-hit is not reproduced.

    **Feral Berserk is unrelated** and was never multi-tile: it runs the ordinary `Damage` archetype through
    `ResolveDamageTarget`. None of this is client-dependent either — it is server-authoritative damage end
    to end, unaffected by whether a 4.95 or 5.33 client is connected.
  - **Poet mana-transfer families** (8 spells): Draw Energy/Harness Power/Combine Focus/Inspiration drain a
    **group member's** entire current mana into the caster (`Content.IsManaStealSpell`/`CastManaSteal`, party
    check via the existing `Party` object); Inspire/Share Energy/Bestow Power/Release Focus top off **any
    other player's** mana from the caster's own, capped by whichever pool is smaller, gated on the caster
    holding ≥30 mana to attempt it (`Content.IsManaGiftSpell`/`CastManaGift`). Neither reuses the pre-existing
    `CastManaBattery` (Invoke/Spirit's Power/Life Force/Gather Magic) — that's a different, self-only
    HP↔MP-conversion mechanic; these are direct two-player MP transfers with no HP cost at all.
  - **Poet cleanse family** (Dispell/Remove Magic/Return Natural/Restore Balance, 4 spells): a chance-based
    FULL buff/debuff wipe (RTK `flushDuration`) on a targeted player (self-castable too). Success chance is
    RTK's literal formula — target's effective armor (clamped [-60,70]) minus a will-scaled protection term,
    folded into `(120+armor)/2`, floored at 10%. Fixed 200 mana, no cooldown. `Content.IsCleanseSpell`/
    `Session.CastCleanse`/`FlushDurations` (clears `_buffs`, rage, stealth, AND the Backstab/Flank stances —
    RTK's wipe is total). Player `Protection` isn't modeled (only mobs carry it), so that term is 0 for a PC
    target — a known, minor simplification.
  - **Poet revive family** (Resurrect/Return Spirit/Ming-Ken Blessing/Death Undone, 4 spells): heals a dead/
    ghost player back to full in place, reusing the existing `ReviveAt` (Silver Thread no longer shares this
    path — as of 2026-08-06 it only warps; see §11k).
    Fixed 3000 mana, 8s cooldown. `Content.IsReviveSpell`/`Session.CastRevive`. RTK also blocks reviving a
    currently-hostile PvP target (`player:canPK`) — not modeled since this server has no PvP/hostility-flag
    system yet, so that guard is simply absent, not faked.
  - **Rogue short-leap family** (Race/Spiritual Jump/Leap of Faith/Transport, 4 independently-authored
    copies of the identical mechanic in RTK's own Lua — not alias-delegated like the others): jump up to 3
    tiles in the faced direction, stopping at the last passable tile (reuses `MapData.BlockedMove`, the same
    collision test normal movement uses). 1 mana, 80s cooldown. `Content.IsLeapSpell`/`Session.CastLeap`,
    re-anchors the viewport via the same `EnterMap` call `@warp` already uses for same-map jumps.
  - All of the above had `SplLevel=0` in the CSV export (same known gap as the rage/stealth pass) —
    overridden with their real Lua-sourced levels in `Content.SpellLevelOverrides` (63/45/40/63 for the four
    strike families, 35/45/88/99/99 for the Poet and leap families).

  **Chunk 2 — fixed 2026-07-26:**
  - **Enchant weapon-multiplier family** (16 ids, 7 tiers 1.2x/1.5x/2x/2.25x/3x/4x/6x by level 28-99): a
    toggle STANCE (`player.enchant`) — RTK's shared `enchants` mutual-exclusion group means casting any one
    while another (or itself) is already active just re-prints "This spell is already active.", never
    stacks/upgrades. Unlike rage (which multiplies the WHOLE swing), `swingDamage.lua` multiplies ONLY the
    raw weapon-swing term (s/2) by `enchant`. `Content.EnchantFor`/`Session.CastEnchant`/`EffEnchant`, folded
    into `PlayerSwingDamage` as `s/2.0*EffEnchant`. Mana/level hardcoded from each spell's own Lua (not
    trusted from the CSV, same Type-5-skill gap as rage/stealth) since one tier (`tigers_fortitude_rogue`)
    is genuinely mana-FREE (item components only).
  - **Directional ground-loot family** (Filch/Spirit's Hand/Quick Fingers/Light Touch, 4 ids, level 65): grabs
    coins/an item stack from the ONE tile directly ahead — despite the in-game description's "up to 4 tiles"
    claim, the Lua's own loop only ever runs `i=1`. Skipped entirely (silently, no message) if a player is
    standing on that tile; RTK's "someone's deathpile" protection isn't modeled since this server has no
    per-item ownership at all, so every other ground stack is fair game. `Content.IsGroundLootSpell`/
    `Session.CastGroundLoot`, reusing `World.PeerAt`/`PickUp`/`DropItem` (the same primitives `HandlePickup`
    already used for the native '0x07' walk-over pickup).
  - **Divination popup family** (Judge/Spiritual Advisor/Natural Talent/Appraise + Spy/Spiritual Guide/
    Nature's Handiwork/Judgement Day, 8 ids, level 17/28): a text popup of a target player's class/name/
    level/title/might/will/grace; the spy half also lists their full inventory. Sent via the pre-existing
    0x30 NPC-dialog sender (`SendScriptMessageP`, portrait-less) rather than the 0x34 click-profile packet —
    0x34 has no numeric level/might/will/grace fields at all (see the profile-packet entry above), so it
    can't express this. The judge family requires the target STRICTLY lower level (`target.level >=
    player.level` fails in the Lua); the spy family allows an EQUAL level too (`target.level > player.level`
    fails) — a genuine, deliberate difference in the source, not a typo. `Content.IsDivinationSpell`/
    `Session.CastDivination`, target resolved via the pre-existing `ResolvePcCastTarget` (same helper the
    mana-transfer/cleanse/revive families use).
  - **Trap-placement subsystem** (9 real castable ids — `set_trap`, the dispatcher with `SplQuestion` "What
    trap? >", plus the 8 `set_X_trap` spells Dart/Snare/RepeatingDart/Flash/Spear/Poison/Death/Sleep,
    level-gated 26/33/44/55/66/77/88/99). **ERA NOTE:** only the *dispatcher* is 4.95 content. The 8
    individual trap spells were added by the **2003-07-01** reset, two years after this client shipped —
    archive `nexus_news.md` that day carries the Dream Weaver board post relayed by Growl ("the ability to
    split your trap spells into several spells"), Rachel's mechanics writeup ("Set traps spell still exists,
    however you can also learn each individual trap spell ... so that you don't have to type in the name"),
    and Conro confirming the launch bug that Spot traps couldn't see them (fixed 2003-10-31). The NexusAtlas
    pages dated 2003-11-04 are Rachel's *site*-maintenance list, not the patch. They are therefore gated OFF
    by default (`Content.IsOutOfEraSplitTrap`): dropped from `SpellsForClass` (tutor menus + the `@lvl` rebuild),
    pruned from an existing book on world entry, and refused at `HandleCast`. The mechanics below are
    untouched — the dispatcher resolves the same `set_X_trap` defs internally, so every trap kind still
    works, you just have to type its name. Re-enable with `SplitTrapSpells,1` in `ServerTuning.csv` +
    `@reload`. The subsystem itself: an entirely NEW hidden-hazard-entity layer, `World.Trap`
    (id/x/y/kind/ownerId) held per-map alongside `GroundItem`s but NEVER broadcast/drawn (invisible until
    triggered). `World.PlaceTrap`/`TrapsNear` are the public API; triggering is wired into `World.Tick`'s
    EXISTING mob-movement loop (both the chase-toward-target branch and the normal wander-step branch) — the
    instant a mob's new tile matches a live trap, the trap is removed and `TriggerTrapLocked` fires its
    effect. **PC-triggering was deliberately left out** (mobs only) — same "no PvP damage path" precedent as
    every debuff/sacrifice-strike spell earlier in this audit; flagged here rather than silently narrowed.
    Effects: Dart/RepeatingDart/Spear/Death (500/500/3500/11650 flat damage — RepeatingDart is, despite the
    name, byte-for-byte the same single-hit script as Dart in the real Lua) go through a new deferred
    `trapDamage` queue processed after `Tick`'s lock releases (`World.ApplyTrapDamage`, mirroring the
    pre-existing mob-swing `hits` queue — a kill still credits the trap's owner with exp via the existing
    `Session.AwardExp`). Snare/Sleep/Flash are simplified to a `Mob.FrozenUntil` hold (75s/38s/10s) — RTK's
    real snare is a +20 armor debuff and flash is a `blind.cast`, neither of which this server models as a
    distinct mechanic, a deliberate simplification rather than an oversight. Poison is a REAL
    damage-over-time (`Mob.PoisonUntil`/`PoisonNextTick`/`PoisonTickDam`/`PoisonOwnerId`, new fields on
    `Shared.Mob`), ticking every 1500ms inside `World.Tick` for 1% of max HP (capped [1,1000]) — and, matching
    RTK exactly, a tick is skipped rather than allowed to land the kill (poison alone can never finish a
    mob off). **`spot_traps` (the trap-reveal spell) turned out to be a DOG/companion-pet spell**
    (`Spells.csv` `SplPthId=99`, `rtklua/Accepted/Spells/dog/spot_traps.lua`) that a player character never
    learns directly — out of scope here, not silently dropped; revisit only if pets ever cast their own
    spells.
  - **Poet "Call of the Wild" pet-summon system** (28 of ~29 ids — 7 tiers `companion`/`assistant`/
    `protector`/`fighter`/`warrior`/`champion`/`avatar` x 4 alignment reskins each, level 68-99): spawns a
    real, correctly-statted shared-world `Mob` (via the pre-existing `Session.SummonWorldMob` — the same
    helper `@summon` uses, so it already copies the full combat block) on the first free tile of a **clockwise
    sweep starting at the direction the caster faces** — front, right, behind, left (facing dirs are 0=N 1=E
    2=S 3=W, so clockwise is `+1`) — and, with all four cardinal neighbours taken, ON the caster's own tile,
    where summons simply stack. Each pet arrives facing back at the poet, computed from its OWN placement
    direction. *(Changed 2026-08-13. RTK `cotw_SpawnSetThreat` is front-tile-or-stack, and ported straight
    that meant four summons in a row put one in front and piled the other three on the poet — building the
    ring that makes pets a barricade took four deliberate turns. The sweep still prefers the front tile, so a
    single summon lands exactly where it always did.)* Tagged `Mob.OwnerId` + `Mob.PetExpiresAt` (300s after cast, then a
    plain `World.DespawnMob` — no kill/loot/exp, same as riding a mob away), and capped at 4 concurrently
    alive pets (6 at level 90+, 8 at level 99 — `Content.PetCapFor`, RTK's `cotw_spawnCheck`), counted PER MAP
    via the new `World.PetCountFor` (matching RTK's own `getObjectsInMap` scope). The two level-99 wind tiers,
    Wind dancer with the three Champions and Wind warrior with the three Avatars, cost no mana and have an
    8-minute (480000 ms) cooldown, through the pre-existing `OnCooldown`/`SetCooldown` plumbing; every lower
    tier costs a flat 10 mana with no cooldown (`game-data/Pets.csv`). The source is Nexus Atlas's poet page
    (`Sources.csv` `atlas-poet-spells`). RTK agrees on the cooldown (`cotw_wind_dancer.lua` line 6,
    `cotw_wind_warrior.lua` line 5) but charges the dancer tier 10 mana (`cotw_wind_dancer.lua` line 3); Caleb
    chose 0 on 2026-09-29. The gold in RTK's `requirements()` is the trainer's learn price, which every tier
    has, not a cast cost. *(Changed 2026-10-05. Wind dancer and the Champions used to cost 10 mana, with no
    cooldown.)* **NOT ported:** `cotw_controller_poet`, neither its threat-transfer nor
    its dismiss-all — both are later-server behaviour (4.95 pets leave play only by dying or timing out, and
    RTK's threat table isn't 4.95; see §"Call of the Wild: the controller and the Giasomo bird"). Pets heel
    and assist — see §"Pet AI" for the rules (that was added 2026-08-06; before it, an owned mob ran the plain
    wander/aggro AI, so a `MobBehavior 0` pet just drifted off and never fought anything).

    **A PET IS A LEGITIMATE TARGET — including its owner's** *(2026-08-13)*. `Session.ResolveSwing` used to
    open with `if (wmob.OwnerId == _char.Id) return;`, silently eating any swing at something you owned. Two
    things that broke: (1) **a Charm weapon could not kill anything** — its 4% `on_swing` proc endears the
    faced mob for 40-45s (`WeaponProcs.csv` → the `endear` verb), and for all of that time every swing at it
    returned right there, with re-procs refreshing the hold, so the weapon read as "melee does nothing";
    (2) a badly-placed summon was a wall its own poet had to wait out. Other players could always hit your
    pets — `World.TryDamage` never had an owner guard — so this only ever penalised the owner. Consequences
    wired alongside it: the pet-assist foe filter in `World.Tick` dropped its `o.OwnerId == 0` clause (now
    `o.Id != mob.Id`), so **hitting one of your own summons turns the rest of them on it** — the threat test
    is the whole gate, so pets still ignore each other, and other poets' pets, until someone starts
    something. The creature being beaten on does NOT retaliate, which is the pre-existing "a pet does not
    fight back when something hits IT and only it" rule, unchanged. And a **conjured** victim (`Mob.Summoned`
    — a CotW pet, a Giasomo bird) pays **no exp and no quest tally** on death, in both `ResolveSwing` and
    `World.ApplyMobOnMobHit`, or summon-and-kill would be an exp loop. An *endeared* creature is a real world
    mob that was always standing there, so it still pays normally. The 29th
    id, `cotw_giasomo_bird_poet`, asks for mob **807**, which exists nowhere — RTK's own SQL and our
    `mobs.csv` both put `giasomo_bird` at **600** and every other cotw id matches the SQL exactly, so it is an
    isolated typo (RTK's Lua flags itself: "I know this doesn't belong here, but the COTW structure is so
    terrible already"). It is now wired to 600 via `Pets.csv`, plus a synthetic `Spells.csv` row (50052)
    without which `Content.SpellByKey` returned null and the Giasomo stick's proc never fired at all.

  **Chunk 3 — fixed 2026-07-26** (user: "we need to fix shape shifting and add the position-directed
  attacks"):
  - **Shapeshift/morph family** (29 real castable identifiers — see the "Shapeshifting" writeup earlier in
    this section for the full disassembly trace that overturned chunk 2's "confirmed blocked" verdict): a
    peer-visible-only illusion. `Content.MorphSpells` (21 fixed-look reskins) + `Content.MorphDispatchFor` (8
    question-dispatched: `feral_rogue`/`gangrel_rogue`/`gangrel_mage`/`rodent_rogue`/`beast_rogue`/
    `beast_mage`/`druids_rodent`/`wilderness_guise` — `wilderness_guise`'s real RTK menu chains into 5
    separate `recast`-only sub-spells with no `cast()` of their own and no independent SplId path reachable by
    a player; folded directly into `wilderness_guise`'s own answer table instead of modeling that
    indirection). `Session.CastMorph` sets `_morphLook`/`_morphUntil` and immediately broadcasts
    `ShowPlayer` to current peers; `Session.ShowPlayer` checks `PlayerSnapshot.MorphLook` and, when set, calls
    the SAME `SendCreatureList` builder `ShowMob` uses instead of the normal `SendLook` — so every future
    re-sync (join, map-change, equip refresh) keeps rendering the correct form with zero extra plumbing.
    `World.Tick` gained a `expiredMorphs` deferred-revert sweep (same shape as `expiredPets`/`trapDamage`) so
    the disguise reverts on its own once the duration lapses, not just on a toggle-off recast.
  - **`bladestorm_trap` family** (4 real ids, not 8 as first estimated — `set_bladestorm_trap`/
    `set_swords_dance_trap`/`set_tigers_ambush_trap`/`set_cutting_edge_trap` are 4 byte-for-byte-identical
    alignment reskins of ONE spell, confirmed by reading `bladestorm_trap.lua` directly; there's no separate
    Warrior variant). Despite the similar name, a completely different mechanic from the `set_X_trap` hazard
    family: a VISIBLE, step-triggered decoy that detonates a facing-cone AoE off the TRIGGER's own facing
    (RTK `block.side` → a 4-tile fan, `World.BladestormFan`), dealing ONE shared HP-percent damage number
    (not flat) to both the trigger and every mob the cone catches. The first spell in this whole audit where
    a PLAYER stepping on a trap also triggers it, not just a mob — wired into `Session.HandleWalk` right
    after a step commits its new tile (`World.CheckPlayerTrapTrigger`), alongside the existing mob-only path
    in `World.Tick` (`TriggerTrapLocked`'s new `"bladestorm"` case). RTK's cone-AoE only hits OTHER players if
    `block.pvp > 0` (a toggle this server doesn't model) — simplified to "cone hits mobs only", the
    established no-PvP-damage-path precedent from every debuff/sacrifice-strike spell earlier in this audit.
    The trigger's OWN self-damage is kept when the trigger is a player (tripping a trap isn't "PvP" the way
    hitting another player would be) via `Session.ApplyBladestormSelfDamage` — `floor(health*0.5) +
    calculateDamage(35000)`, where RTK's `calculateDamage` armor-deduction formula turned out to be
    IDENTICAL to this codebase's existing `Combat.ApplyArmor` (both `1 + max(armor,floor)/100`), so it's
    reused verbatim rather than reimplemented — capped to leave at least 1 HP (a trap tripped mid-walk has no
    death-flow of its own to hook, same "self-cost, never actually lethal" precedent as the sacrifice strikes'
    own HP cost, which `verbs.sacrifice` sets through `ctx:setHp`, and that never takes a living caster below
    1). Level 99, 1520 mana, 125s cooldown, the decoy auto-expires 21s after placement if
    never triggered (`Trap.ExpiresAt`, swept silently in `World.Tick` — traps have no ground graphic, so no
    broadcast is needed either way). **NOT ported:** the Lua's NPC heartbeat implies a 5000-mana/tick
    owner-upkeep drain while the decoy is alive — the exact drain/early-deletion formula wasn't in the
    captured source, so this is a documented gap (flat 1520 upfront cost only), not a guess.

- **Still NOT ported, explicitly out of scope:** RTK's equipment "Miss" stat (a rare cursed-item flat-miss
  chance — no Miss column in our item data, so every swing here always lands for at least 1 damage, matching
  RTK's non-cursed common case).

- **Debug/GM summon commands carried none of this** (a separate, pre-existing gap found while testing the
  fix): `@rabbit`, `@summon <mob>`, and the ridden-horse re-spawn all resolve a real `MobDef` but only ever
  forwarded Look/Name/Hp/Color/Exp/MoveTime into the spawned `Mob` — Level/Will/MinDam/MaxDam/Ac/Grace/Hit/
  IsBoss/Aggressive were silently dropped, so summoning a named mob for testing would never show its real
  numbers. `Session.SummonWorldMob` now takes an optional `MobDef? def` and copies the full combat block
  when given one. (The unrelated sprite-only debug lab — `@cre`/`@crecol`/`@crow`/look-lab, which spawn by
  raw look id with no mob name at all — has no `MobDef` to pull from and is untouched, working as designed.)

**Taking a hit** (`Session.ApplyMobHit`): docks HP, pushes `SendStats()`, and broadcasts the same over-head
hit/HP-bar packet (`DamageOver`) a mob shows when hit, aimed at the player's own entity id. Hp reaching 0
calls `Die()`: the player is redrawn as a **ghost** (appearance form `1` — `MountForm()` returns
`_char.Hp==0 ? 1 : (Mounted?3:0)`; `PlayerSnapshot`/`ShowPlayer` carry a `Dead` flag so peers see it too),
attacking/casting is blocked ("Spirits cannot attack/cast spells"), and **stays that way** — RTK has no
auto-revive timer at all. **Changed 2026-07-25** (was a fixed 3s timer + auto-warp to the home city, a
deliberate simplification made before the F1 menu was understood): a ghost now revives only by pressing
**F1** and choosing **"Silver Thread"** — RTK's real answer, ported in §11k — which offers a Shaman by
nation and revives (`ReviveAt`: full heal + warp) on arrival.

**Home city** (`Session.HomeCityFor`/`PlaceNewCharacter`): a fresh character spawns, and a defeated one
revives, **just inside their nation's home**, near the real RTK door-arrival tile (`game-data/
Warps.csv`) rather than GmWarp's outdoor GM-teleport spot:
- Nation `2` (Buya): map **351** (Jadespear's Home) at `(3,6)`.
- every other nation: map **36** (Ironheart's Home) at `(5,10)` — the door tile from Kugnae's
  `0:(87/88,146)`.

Jadespear's tile took two corrections before landing on `(3,6)`:
- `(7,12)` — Warps.csv's raw door-arrival Y (from Buya's `330:(55/56,121)`), but map 351 is only 12 rows
  tall (valid `0..11`) — `y=12` is one past the edge. The 4.95 client's self-placement check (`0x424310`)
  silently bails on an out-of-bounds tile: the game-world object gets constructed (`0x02` handshake all
  succeeds) but the self entity is never placed, so the screen stays **black and movement keys do
  nothing** (the GUI still works — it doesn't depend on the world entity). Live symptom trace: `>>33
  0x33 handler ENTER ... place/validate 0x424310 -> al=0 <<< BAIL (placement failed) >>>`.
- `(7,11)` — in bounds, but that row is `TK351.map`'s bottom wall/threshold strip in the OBJECT layer
  (ids 636-643). At the time, passability (`Solid`) was pass-flag only, so this tile was never *blocked* —
  just visually "standing in a wall." (Since the 2026-07-26 §12 object-wall fix, movement now also honors
  `SObj.tbl` object flags, so those wall ids would additionally impede stepping off — reinforcing that an
  open interior tile, not a wall-strip tile, was the correct choice.) `TK351.map`/`TK36.map` were both checked against
  the real client install: entirely open PASSABLE floor (no solid tiles at all), but Jadespear's object
  layer draws a walled room, so a merely in-bounds tile isn't enough — it also needs to dodge every
  nonzero object id. `(3,6)` sits in the empty interior clear of all of them.

`PlaceNewCharacter` MUST run after `ApplyAppearance` has decoded the real creation-time nation (§9) — it
used to run first, silently always landing new characters at Ironheart regardless of what they picked.

**Real RTK note:** in the Lua gameplay layer, totem is normally *re-worshipped* later at one of four
totem-animal shrine NPCs in the Wilderness (`totem_npc.lua`: `JuJakNpc`/`BaekhoNpc`/`HyunMooNpc`/
`ChungRyongNpc`), and those same NPCs are also the resurrection point for a dead player (`_resurrect`,
gated on `player.state==1`). This server doesn't have that shrine/worship system yet. As of §11k, revival
itself IS the real RTK flow (F1 → Silver Thread → pick a Shaman) — the one remaining simplification is
that `ReviveAt` heals on arrival at the Shaman's map instead of requiring a second click on a standalone
Shaman NPC actor once there (we don't have those placed as real map NPCs).

**The live Buya↔Jadespear's-Home door** (`Content.Warps`/`TryWarp`, `game-data/Warps.csv` ids 56-59) is
a *separate* code path from the hardcoded home-city spawn above — walking onto Buya's door tile
(`330:(55/56,121)`) drives the normal warp system (`Session.HandleWalk` → `EnterMap`).

**Fixed (2026-07-26).** The raw Warps.csv data had two bugs, reported by the user as "warps in jadespears
home don't work: can't go further in, and going back outside works but nothing warps you back":
- The entry destination (`351:(7/8,12)`) was one row past map 351's bottom edge (12-row map, valid
  `0..11`) — `EnterMap`'s bounds clamp silently pulled it up to `y=11`, which is `TK351.map`'s bottom
  wall/threshold strip in the OBJECT layer (every column solid-looking except columns 5-6, the actual door
  gap — confirmed by dumping the row). Landing there technically "worked" (object-layer isn't a collision
  source, see §12) but put the player standing ON the door's own trigger tile.
- The **return trip's source tile** (`351:(7/8,13)`) was flat-out off-map (row 13 doesn't exist on a
  12-row map), so stepping "back outside" could never fire — that's the warp the user found dead.

Both are now explicit, in-bounds coordinates instead of relying on the clamp: entry lands one row *above*
the door at **`351:(5/6,10)`**, and the door-gap tiles **`351:(5/6,11)`** are the return-trip source,
matching the real gap in the object-layer wall. The return destination was also corrected to land directly
below the Buya door instead of shifted 2 tiles west (`330:(53/54,122)` → **`330:(55/56,122)`**, user:
"exiting jadespears home should be 55 122 and 56 122") — same columns as the entry trigger `(55/56,121)`,
one row south. Round trip: Buya `330:(55/56,121)` → Jadespear's `(5/6,10)` → walk south onto `(5/6,11)` →
back to Buya `330:(55/56,122)`.

---

## 11g. Durability, warp gating, whisper, and spell resist (added 2026-07-25)

Four RTK subsystems ported in one pass, each cross-checked against `RTK-Server/rtk/src/map/clif.c`.

**Item durability & breakage** (RTK `clif_deductweapon`/`clif_deductarmor`/`clif_checkdura`, clif.c:6646-6844).
`InvItem.Repair` (0-5) tracks which threshold warnings have already fired, mirroring RTK's
`sd->status.equip[x].repair`. On a landed melee hit, the weapon (EQ slot 1) has a ~49% chance
(`rnd(100) > 50`) to lose 1 durability (`Session.HandleAttack` → `DeductDura`); on TAKING a hit
(`Session.ApplyMobHit`), every worn slot rolls independently (RTK's `clif_deductarmor` checks the weapon
slot too — preserved, not "fixed"). Warnings fire once each at 50/25/10/5/1% ("Your X is at 50%.", …);
at 0 the item is destroyed ("Your X was destroyed!"), unequipped, and stats recalculated. Indestructible
items (`ItmIndestructible`) and durability-less items never decay; disabled entirely on `MapPvP` maps
(`Content.IsPvpMap`). RTK's BoD "protected" restore-instead-of-break branch isn't modelled — no item in
the live registry currently sets `ItmProtected`, so it would never fire anyway.

**Warp level/mark/path gating** (RTK clif.c:5187-5203). `Content.MapMetaInfo` now also carries
`ReqLvl/ReqPath/ReqMark/ReqVita/ReqMana/LvlMax/VitaMax/ManaMax/RejectMsg`, loaded from the (previously
ignored) `Maps.csv` columns — 1107/9850 maps set a real req, and vita/mana caps are stored as unsigned
32-bit with `4294967295` as the "no cap" sentinel (parsed as `long`, not `int`, or the sentinel overflows
to 0 and silently locks every map). `Session.TryWarpGate`, called only from `HandleWalk`'s step-onto-warp
path (NOT from the internal `Warp()` used by quests/Gateway/GM teleports — RTK's check lives in the walk
handler, not in `pc_warp` itself), reproduces RTK's denial-message cascade verbatim, including its dead
branches: because almost every gated map sets `ReqLvl`, the level-difference messages already cover every
diff value, so the mark/path-specific text is only reachable when `ReqLvl` equals the player's level
exactly and a mark/path check also fails. `CharMark` reads the character's mark (`_char.Mark`,
`Session.CharacterApi.cs`), so a `ReqMark > 0` map's mark check passes for anyone whose mark meets it. No NPC
sets the mark yet; only `@mark` (or a rank title given to `@class`) does, so in play only a character marked
that way passes it.

**Reject = snap-back + status text, fixed 2026-07-27.** When the gate denies entry, RTK does two things
in order (clif.c:5188-5204): **(1)** `clif_pushback(sd)` (clif.c:15617) — a `pc_warp` that shoves the
player 2 tiles opposite their facing, re-asserting position so the client isn't left standing on the warp
tile; **(2)** `clif_sendminitext(sd, msg)` — the denial to the status box. Our reject path was missing
BOTH: it did `SendLog(denyMsg); return;`, which (a) spoke the line as a `0x0D` chat bubble and (b) sent no
`0x04`. In 4.95 self-walk is client-local — the client had already stepped onto the warp tile and was
**blocked awaiting a `0x04` ack** to release its next step; the bare `return` never cleared that gate, so
the player **froze and could not move or turn** (live-reported on the "Nightmarish visions…" entrance). Fix
mirrors RTK with our proven 4.95 primitives, identical to what `HandleWalk`'s normal `blocked` branch
already does: hold `_char.X/Y` at the from-tile, `SendXy()` (the `0x04` that both snaps the client off the
warp tile AND unblocks the next step), then `SendMiniText(denyMsg)` (status box, RTK `clif_sendminitext`).
The sibling scripted-tile rejection `TryMythicCaveEntrance` already snapped back with `SendXy()` but was
using `SendMessage` (the `0x02` login box); switched to `SendMiniText` so both rejection paths land in the
same status pane. User-confirmed working 2026-07-27.

**Arrival on an occupied tile = stack, settled 2026-09-06 (#99 part 2).** When a warp, scripted-tile
entrance, world-map hop, Gateway or GM teleport puts a player on a tile another player already stands on,
the original game stacked them — it neither refused the warp nor moved the arriver to a neighbouring tile
(Caleb, from play; `game-data/Sources.csv` `live-2026-09-06-arrival-stack`, a recollection rather than a
dated capture, so weight 2 rather than 3; no contrary source is recorded in this tree or the research
archive). Structurally consistent with RTK at weight 0: `pc_warp` applies no
occupancy test (§11m), and the `slash.lua` note earlier in this document has `getAliveObjectsInCell(m, x, y,
BL_PC)` returning *everyone* on one cell, which only makes sense if several can be there. Server side this
is `ArrivalPolicy.Clamp`, the default every arrival passes to `World.PlacePlayer` except the three
free-neighbour GM commands (`@approach`/`@bring`/`@npc` use `AdjacentFreeElseStack`, a GM convenience and a
house choice, not a game fact). PR #102 put the resolve and the position write under one `World._lock`
acquisition; part 2 changed no behaviour, only the record and the tests' names. A walk STEP is a different
rule and untouched: `World.TryMovePlayer` (#30) checks occupancy for steps.

**Full `SendMessage`→`SendMiniText` audit, 2026-07-26.** The class-gate path-hall doorway (§ above,
`TryPathHallWarp`) turned out to be one instance of a systemic mislabeling: `SendMessage` is specifically
RTK's `0x02` **login-box** packet (`TkCrypt.LoginKey`-encrypted), reserved for the pre-world / re-login
flow — not a general in-world chat channel. Cross-referenced against RTK's actual sources (`clif.c:6484`
`clif_sendmsg(sd, type, buf)` — one function, one `0x0A` packet, dispatched by `type`: `0`=wisp, `3`=mini/
status **(RTK's `clif_sendminitext` is just `clif_sendmsg(sd, 3, ...)`)**, `5`=system, `11`=group, `12`=clan
— plus a wide sample of Lua spell/quest/NPC scripts: mana checks, cooldowns, "no target", cast confirmations
("You cast X."), and target-side cast notices ("X casts Y on you.") are *all* `player:sendMinitext(...)` in
every RTK script checked (`ExpSeller.lua`, `harden_armor.lua` family, `spark.lua`/`global_zap.lua`/
`global_heal.lua`, `dart_trap.lua`, `onScriptedTilesBushTree.lua`, `onScriptedTilesPathHalls.lua`, RTK's own
`player.lua:3717` "Spirits can't do that."). Our port had defaulted most of these to `SendMessage` instead,
so ~90 call sites across `Session.cs`'s spell-casting, quest, mount, trap, and status-toggle code were
silently landing on the wrong (or no-op-looking) channel. Converted all of them to `SendMiniText`, **except**:
the four `clif_changestatus` status-line toggles (Realm-centered/Fast-Move/Sociable/Exchange — a different
RTK function, verbatim chat text) — *an exception since found to be wrong: `clif_changestatus`'s announces
are themselves `clif_sendminitext` calls (clif.c:14240ff). 2026-08-19 ALL toggle announces moved to
`SendMiniText` type 3: Sociable ("Join a group") and Exchange with user-specified tab formatting
(`Join a group\t:ON|OFF`, `EXCHANGE\t\t:ON|OFF`), and Fast Move, Realm-centered, Clan whisper, the
`SettingLabels` set and `@setting` with RTK's verbatim space-padded column-17 lines* — the re-login
"Incorrect password." and profile-save confirmation (both
documented, intentional `0x02` uses — see §9.5/§11k above), and our own invented combat-kill flavor lines
("Your Fireball destroys Rat! (+50 exp)") which have no RTK equivalent at all (real RTK conveys a kill purely
via the HP-bar/hit-animation, no text) and were kept as a deliberate enhancement. Durability warnings
(`clif_checkdura`/`clif_deductduraequip`) are a special case: RTK tags them `type 5` ("System") rather than
the default `type 3` — same `0x0A` minitext packet, different client-rendered style — so those became
`SendMiniText(text, type: 5)` rather than the default-typed calls used everywhere else.

**Whisper / tell** (RTK `clif_parsewisp`, clif.c:7644-7790). Native input: **Shift+'** opens the whisper
prompt, then a target name + Enter, then the message + Enter. LIVE-CONFIRMED 2026-07-26 by real capture —
op `0x19`, body `dstlen(u8) dst_name[dstlen] msglen(u8) msg[msglen] 00`, e.g. `07 'destine' 01 'd' 00` —
matching RTK's wire layout exactly (`clif.c:7644`: `dstlen = RFIFOB(fd,5); msglen = RFIFOB(fd,6+dstlen)`).
Dispatched to `Session.HandleWhisperPacket`, which is now the ONLY entry point — the `@whisper` / `@w`
chat fallbacks were removed once this opcode was confirmed real. `Content.CanTalk`
(RTK `cantalk`, 2/9850 maps) and the not-found message (`"<name> is nowhere to be found."`) are RTK's exact
wording. `World.Online.FindPlayer(name)` is the case-insensitive online-lookup this needed. **Error channels fixed
2026-08-19** (they had drifted onto `SendLog` = `0x0D` self-speech, so the client spoke the failure aloud
as the player's own words): the not-found and can't-hear lines are **blue** (`SendBlueMessage` =
`SendMiniText` type 0 — RTK `clif_sendbluemessage`, which writes an `0x0A` with type 0), and the cantalk
line is plain minitext type 3 (RTK `clif_parsewisp` uses `clif_sendminitext` for that one). The ignore-block
wording is RTK's literal `"They cannot hear you right now."` (was "can't"). Same date, the `!!`/`!` channel
errors ("You are not in a group", "You are not in a clan", "Clan chat is off.") moved to blue too — all
four are `clif_sendbluemessage` in RTK's `clif_parsewisp`, and the first two carry no period there.

**Delivery, fixed + LIVE-CONFIRMED 2026-07-27 — was a same-head self-bubble, now rides the `0x0A` mini-text
channel.** Originally delivery rode `SendLog` (`0x0D`, chatType 0, attributed to the *recipient's own*
entity), because RTK's real delivery packet (`clif_sendmsg` type 0, "Wisp/blue text" — clif.c:6484) is a
dedicated non-entity system-message opcode with no proven 4.95 equivalent at the time. Live user testing
found this looked wrong: it showed as a bubble over the recipient's own head reading `"SenderName" ()
message"`, not a chat-log line attributed to the sender. Root cause: `0x0D` always draws a 3-second head
bubble (handler `0x450170`) — there's no way to suppress it, and attributing the packet to the *sender's*
entity id instead would silently vanish whenever sender and recipient aren't on the same map (the common
case, since whisper has no range limit). Fixed by switching delivery to **`Session.SendMiniText`** (`0x0A`,
the client's mini-text/status channel below the inventory, RTK `clif_sendmsg`/`clif_sendminitext` — already
proven live via the look-at-name and item-pickup-name fixes earlier this session) with **`type = 0`**,
matching RTK's own `clif_sendwisp`/`clif_retrwisp` type value for "Wisp" text. **Line formats fixed
2026-08-19 to RTK's real shapes** (both had been a uniform `"SenderName: message"`): the sender's own echo
(`DoWhisper`) is **`Target> message`** — RTK `clif_retrwisp`'s verbatim `"%s> %s"` with the TARGET's
proper-cased name, so you see who you whispered — and the recipient's copy (`ReceiveWhisper`) is
**`Sender" message`** — RTK `clif_sendwisp`'s double-quote separator. RTK additionally inserts the sender's
class in parens (`Sender" (Class) message`, clif.c:6539); that half is deliberately dropped
(user-specified 2026-08-19).
**Live-confirmed:** with `type = 0`, the line lands in the main chat window in blue with no head bubble,
exactly matching RTK's "Wisp" intent — so `0x0A`'s `type` field really does route to a different pane/color
per value (`type = 3`, used elsewhere for look-at/item names, renders in the mini-status box instead). Not
modelled: per-player whisper-on/off, silence/mute, ignore lists — none of those systems exist yet.

**Spell resist / deflect** (RTK `clif_parsemagic`'s deflect roll, clif.c:8910-8934). Only spells flagged
`SplCanFail` (317/905) roll it. `Session.RollDeflect`: `willDiff = max(0, target.Will - caster.Will)`,
`prot = round(willDiff / 10)`, `failChance = 100 - 0.9^prot * 100` — an exponential curve, not a flat
percent. `Mob.Will` is now populated from `MobDef.Will` (the RTK `Will` column, real but previously
unwired — mobs had no Will at all before this). RTK's mob struct also carries a separate per-mob
`protection` stat; our mob registry has no source column for it, so it's treated as 0 for every mob — a
real but incomplete port, not an invented number, and it means our mobs resist somewhat less than genuine
RTK ones with nonzero protection would. Wired into `CastDamage` and `CastDebuff` (the only two PvE-target
archetypes we have — there is still no PvP cast path at all) right after target resolution and BEFORE the
mana debit, matching RTK: a deflected cast spends no mana (RTK returns before ever calling into the Lua
"cast" script that would debit it). Message is RTK's exact line, caster-facing only: "The magic has been
deflected." — RTK sends nothing to the target on a resist.

---

## 11h. Bulletin boards + native nmail (reply shapes LIVE-VERIFIED 2026-07-28)

**Request: LIVE-confirmed.** Pressing **b** sends op `0x3B` — matches RTK's dispatch exactly
(`clif.c:11613`, `case 0x3B: clif_handle_boards(sd);`). Body `dec[0]` is a sub-command, then u16-BE
board/post ids: `1`=show board list, `2 board`=show a board's posts, `3 board post`=read one post,
`4 board topicLen topic bodyLen body`=make a post, `5 board post`=delete a post,
`6 toLen to topicLen topic msgLen(u16BE) msg sendCopy`=**send nmail from the native compose window**
(RTK `nmail_write`, level-10 gated), `9`=open the mailbox (== sub-2 with board 0). `Session.HandleBoard`
decodes 1–6 + 9; 7/8 (GM postcolor, scripted special-write) aren't modelled.

**Board 0 == the player's nmail mailbox** (RTK `boards_showposts`: "Board(0) == NMail"). The client's
board window is MODE-SWITCHED by the reply's first byte (`flags2`, below): mode 4 turns it into the
mailbox whose **Write button opens the recipient-field compose window** (emits sub-6); mode 2 is a
normal board (Write emits sub-4, no recipient). All live-verified on the real 4.95 client.

**Write/delete ACK (`SendBoardAck` — the reply the compose/board window BLOCKS on).** After any sub-4/
5/6 action the client waits for a dedicated `0x31` ack (RTK `nmail_sendmessage`, map.c:164):
`other(u8: 6=write ack, 7=delete ack) type(u8: 1=success — releases/closes the window, 0=failure —
keeps it open) msgLen(u8) message[...] trailer(u8=7)`. Replying with only a 0x0D text line hangs the
window ("Your post didn't go through due to an error"). RTK's canonical texts: "Your message has been
posted." / "Your message has been sent." / "User does not exist." / "The message has been deleted." /
"You can only delete your own messages.".

**Reply: NOT live-confirmed.** RTK's real board storage lives in a *separate char-server process*
talking its own SQL database (`Boards` table: `BrdBnmId/BrdPosition/BrdChaName/BrdTopic/BrdMonth/BrdDay`,
confirmed by reading `rtk/src/char/mapif.c`'s `mapif_parse_showposts`/`boardpost`/`deletepost`), which then
replies to the map-server (`rtk/src/map/intif.c`), which THEN builds the actual client packet. Three hops
of reference material were needed to reconstruct the final client-facing `0x31` reply shapes (list /
show-posts / read-post) below — but none of them have been captured live off the 4.95 client the way the
board-open *request* was, so treat these as "best-effort from source, awaiting confirmation," the same
status this doc gives other built-but-unverified systems (§11c, §11d). If the board window doesn't render
right in-game, this is the first place to check — paste a capture and the offsets get corrected.

Evidence byte 4 (this server's `inc`) isn't client-meaningful for op `0x31`: RTK's own
`intif_parse_readpost` never writes it at all (`//WFIFOB(sd->fd,4)=0x03;` is commented out), so these
replies use the normal `SendMap(0x31, _gameInc++, data)` convention like every other opcode in this
codebase, rather than copying RTK's literal (and inconsistent) byte-4 values.

- **Show board list** (`SendBoardList`): `1 titlelen title[...] boardCount` then per board
  `id(u16BE) nameLen name[...]`. This server appends **Mailbox** (board 0) as the LAST entry — the only
  reachable way into the mailbox, since the 'm' hotkey is dead (below).
- **Show a board's posts** (`SendBoardPosts`): `flags2(u8) flags1(u8) board(u16BE) boardNameLen boardName[...]
  postCount` then per post, newest first, `color(=0) postId(u16BE) authorLen author[...] month day topicLen
  topic[...]`. **`flags2` is the window-mode byte: 2 = normal board, 4 = NMAIL MAILBOX** (RTK
  char/mapif.c `mapif_parse_showposts`: `if (a.board == 0) flags2 = 4; else 2`) — mode 4 is what makes
  Write compose WITH a recipient field (sub-6). `flags1` = rights: 1=read-only, 3=write+del, special
  6="write sends a packet" for scripted boards (not modelled).
- **Read one post** (`SendBoardReadPost`): `type(u8: 3=board post, 5=nmail letter) buttons(u8=3)
  nmailFlag(u8: 1 when board 0) postId(u16BE) authorLen author[...] month day topicLen topic[...]
  bodyLen(u16BE) body[...]` — per RTK `intif_parse_readpost`/`mapif_parse_readpost`.

**The 'm' hotkey — CONDITIONALLY LIVE (corrected 2026-07-30; the dispatch-table read below is right but
is not the whole input path).** Live capture: pressing `'m'` **with the mail arrow up** sends `3b 01 00`
and opens the board window — the same request as `'b'`. The mail-arrow HUD widget (ctor `0x47a230`,
`0x469654 → 0x406e80(1)`) grabs the key first and is armed only while the arrow is drawn, which is why
every earlier static test — all run with no mail — saw nothing happen. The original reading follows.

**(SUPERSEDED) The 'm' hotkey — dead at the dispatch table (client RE, 2026-07-28).** The
in-world letter hotkeys are dispatched by a char switch @`0x48e625` (index = `char-0x0D`, byte-index
table @`0x48eab0`, 50-case jump table @`0x48e9e8`). Full map decoded: 'b'=case 22 (board window ctor
`0x406e80(1)` → sub-1 request via `0x407100`), 'i'=28, 's'=32, 'c'=23, 'o'=29, etc. **'m'/'M' sit in
case 49 = the default do-nothing bucket** (alongside x/z/q/n) — so the "m = mailbox" help line is a
stale NexusTK.dat string and the binding never shipped. This is the *same table* that dispatches every
working hotkey, so it settles the question that the earlier VK-only search could not.

The **mail-arrow click does exactly what 'b' does — LIVE-CONFIRMED 2026-07-28**: clicking the lit arrow
puts `0x3B` sub-1 (board list) on the wire, nothing else. Its handler `0x469654` checks hasMail
(`[widget+0x106]`) and calls the same board-window ctor `0x406e80(1)`. So the arrow needs no separate
server support, and the client's own mail affordance lands in the board list — which is some evidence
that a "Mailbox" entry there is the original design, not a workaround. The arrow's PARCEL branch
(`0x469760`) instead sends **op `0x41`, empty body** = RTK `clif_parseparcel` — a "go see the
messenger" minitext there, and the same here.

> **Negative result (tested live 2026-07-28): an unsolicited mailbox `0x31` opens NO window.** Sending
> the mode-4 posts view when the player hasn't opened the board window does nothing at all — the client
> only renders a `0x31` into an ALREADY-OPEN board window.
>
> **⚠️ That does NOT mean the mailbox is unreachable — corrected 2026-08-07.** Two things the note above
> over-generalised. First, `'m'` is not dead: **the mail-arrow HUD widget intercepts it before the
> char-dispatch table**, so `'m'` works whenever the arrow is up and sends the same `3b 01 00` as `'b'`
> (live capture, 2026-07-30 — see the correction in §11f-key below). Second, the board-window ctor
> `0x406e80` runs on the **keypress**, before the request goes out, so by the time our reply to that
> `0x3B` sub-1 lands **the window IS open** — which is the case the negative test didn't cover.
>
> So answering an unread inbox with the mailbox posts view instead of the board list makes `'m'` land
> straight in the mailbox with no client patch (`Content.MailFirstOnBoard`). The cost is that
> `'b'` does the same while mail is unread, since the two keys are indistinguishable on the wire.
>
> **⚠️ That shortcut HARD-FREEZES the client — measured 2026-08-08, `MailFirstOnBoard` now defaults OFF.**
> Repro (`data/server.log`): `<~ aa 00 04 3b 04 …` → `dec 01 00` → `-> boardposts(0x31) mailbox n=1: 38B`,
> and from that instant the client sends **nothing** — no walk, no turn, no keypress — for 35 s until the
> socket is torn down, while the server keeps happily pushing mob `0x0C`/`0x11` at it. So the window being
> open is **necessary but not sufficient**: the ctor `0x406e80(1)` evidently arms a **list-shaped parse**,
> and a posts body (`flags2=4`, byte 0 = 4 where the list wants type = 1) walks it off the end and spins.
> The *identical* posts bytes render fine when they answer **sub-2**, which is what pins the fault on the
> request context rather than the payload. **Sub-1 must be answered with the board list**; the Mailbox is
> reachable as that list's last entry (the proven path). Re-enabling the shortcut needs `0x406e80` RE'd
> first.
> `Session.Movement.cs` also pops a board open unsolicited with `flags1=2` (board-signs), so `flags1` is
> worth suspecting if an unsolicited push is ever wanted for the mailbox too.
> `re/patches/patch_495_mail_key.py` implements the optional one-byte fix (byteTable['m'] 49→22, making
> 'm' a second 'b') — written, verified against the build, **deliberately NOT applied**: patching the
> client wasn't wanted, and it could only duplicate 'b' anyway, not open the mailbox directly.

**Board list content.** RTK's actual board list (`db/board_db.txt`) is server-instance config not present
in the reference tree — there's no real seed data to port, unlike every other feature this session (items,
mobs, maps, spells all had real CSVs), so `Boards.All` (`Server/Boards.cs`) is this server's own roster,
in wire-id order (which is also the order the client draws):

| Id | Board | | Id | Board |
|---|---|---|---|---|
| 1 | Community        | | 6 | Law |
| 2 | Market (Buy)     | | 7 | Guide |
| 3 | Market (Sell)    | | 8 | Poetry |
| 4 | Hunting          | | 9 | Bugs |
| 5 | Community Events | | 0 | **Mailbox** (appended by `SendBoardList`, not in `Boards.All`) |

Every board is open to read + post by any player — RTK's own default for a board with no gating `check`
script; its gated boards (`bugs_board`/`devs_board` GM-only, `pathBoards.lua` per-class boards gating on
"tutor" status, `subpath_public_boards.lua`) need concepts this server doesn't model. Changing the roster
is an edit to `Boards.All`, but the ids are the wire `BrdBnmId` and are referenced by `board_posts.board_id`
and `game-data/BoardLocations.csv` — renumbering an existing board re-homes its posts and any
board-sign pointing at it, so add with new ids rather than reshuffling. (The one board-sign registered so
far, the Buya arena schedule board at `330,127,79`, points at **5 Community Events**; it pointed at the
previous roster's id 4 "Carnage Schedule".)

**Storage.** RTK's posts live in a separate char-server's SQL database; this server is single-process, so
posts collapse into one server-wide JSON file (`state/boards.json`, `Server/Boards.cs`) instead — same
"RTK's shape, our storage" choice already made for characters (`Shared/CharacterStore.cs`). Delete is
author-only (RTK's broader GM/tutor `CAN_DEL` grant isn't modelled).

---

## 11i. F2 / Subpath chat (added 2026-07-25 — awaiting live confirmation)

**F2 is not a menu — it's a chat-channel toggle.** Pressing it was producing a garbage click-profile
(`0x34`) reply with a nonsense target id, because the client routes F2 through the *same* click-info
packet as a real entity click (`0x43 01 entityId(u32BE) 00`), just with a magic sentinel id instead of a
real one. RTK's `clif_handle_clickgetinfo` (`clif.c:11010`) checks for this **before** the normal
`map_id2bl` entity lookup:

```c
if (SWAP32(RFIFOL(sd->fd, 6)) == 0xFFFFFFFE) {   // subpath chat
    sd->status.subpath_chat = !sd->status.subpath_chat;
    clif_sendminitext(sd, sd->status.subpath_chat ? "Subpath Chat: ON" : "Subpath Chat: OFF");
    return 0;
}
```

Confirmed as the real F2 binding (not a guess) by `rtklua/Accepted/Scripts/welcomeNmail.lua`: *"F2 - Turn
'Subpath Chat' On/Off!"*. `Session.HandleClickInfo` now special-cases `id == 0xFFFFFFFE`
(`SubpathChatSentinel`) and calls `ToggleSubpathChat()` — flips `Character.SubpathChat` and confirms via
the same `0x0A` mini-text channel whisper uses (§11g), **before** falling through to the NPC/click-profile
paths. **Not yet live-tested against the 4.95 client** — the packet shape (id `0xFFFFFFFE` on `0x43`) is
taken on faith from the RTK 7.x source; if F2 still misbehaves, capture the raw `0x43` body to confirm
this 4.95 client sends the same sentinel.

**Delivery — `/subpathchat <msg>` (alias `/sp`).** RTK's `clif_sendsubpathmessage` (`clif.c:7402`) is a
**server-wide, not map-scoped** channel: every *other* online player whose `class` matches the sender's
AND who also has subpath chat on receives `<@Name> (ClassName) message`. `Session.DoSubpathChat` ports
this: gated on the sender's own `SubpathChat` flag and the map's `cantalk` flag (same `Content.CanTalk`
gate whisper uses), it iterates `World.Online.All()` (new — a server-wide roster, unlike the map-scoped
`World.Broadcast`) and compares `ClassName` (our single string stands in for RTK's finer-grained numeric
`class`/mark). Rendered via `SendMiniText` at the default `type=3` (proven-live mini/status pane) rather
than RTK's literal `type=11` (its "group" channel, reused for subpath) — **unconfirmed** whether 4.95
renders `type=11` distinctly from `type=3`; if it turns out to matter, this is a one-line change.

---

## 11j. Experience & leveling (added 2026-07-25 — awaiting live confirmation)

**There was no leveling system at all before this.** Exp was already being added to `Character.Exp` on
every mob kill (melee, spell) and shown on the HUD bar, but nothing ever read it back — `Character.Level`
never changed and `Character.Tnl` (experience-to-next-level, sent in the self-profile `0x39`, §9.5) sat at
0 forever. Ported wholesale from RTK's real level-up path: `pc.c`'s `pc_givexp`/`pc_checklevel`
(exp-gain + level-up-loop driver), `class_db.c`'s `classdb_level` (per-path cumulative exp table, backed
by `rtk/db/level_db.txt`), and `rtklua/Accepted/Scripts/onLevel.lua` (the actual stat/HP/MP gain formulas
run on every level).

**Exp table.** `rtk/db/level_db.txt` is `path,cumExpLvl1,cumExpLvl2,…,cumExpLvl98` — one row per path,
`classdb_level(path, level)` = total exp needed to leave `level` (i.e. reach `level+1`). Paths 0-4 map
1:1 onto this server's existing `Content.PathIdForClass`/`Paths.csv` scheme (`PthType` column: 0 Peasant /
1 Warrior / 2 Rogue / 3 Mage / 4 Poet — RTK's own `Paths` table, already in use for spell-book gating).
Extracted (awk one-liner over the RTK file) into a long-format `game-data/LevelExp.csv`
(`Path,Level,CumExp`), loaded by `Content.LoadLevelExp` into `Content.ExpToNext(pathId, level)`.

**`Session.AwardExp(amount)`** is now the single funnel every exp source goes through (quest rewards,
melee kills, spell kills — the three raw `_char.Exp += reward` sites were replaced with calls to this):
add the exp, then loop `while (Level < 99) { need = ExpToNext(path, Level); if not enough, stop; LevelUp(); }`
so one big reward (e.g. a quest turn-in) can carry a low-level character through several levels in one
call — matches RTK's `pc_checklevel` loop shape exactly. Recomputes `Character.Tnl` afterward so the
self-profile window shows the right remaining amount.

**Peasant level-5 wall.** `onLevel.lua` blocks Peasants (path 0) from leveling past 5 until they choose a
real path: `if player.class == 0 and player.level >= 5 then sendMinitext(...); return end`. Ported
verbatim — exp keeps banking up past the level-5 threshold but `AwardExp` stops calling `LevelUp` and
sends the same message, matching the existing path-hall warp gate (§"Class path-hall interior warps",
`Session.cs` ~line 2365) that already required leaving Peasant to progress further.

**`Session.LevelUp(path)`** ports `onLevel.lua`'s stat-gain formula exactly: a `secondary`/`tertiary`
bonus-point flag pair rolled off `(level+1) % 2` / `% 3` (both trip together every 6th level) for the four
real paths, or a different `% 2` / `% 3` / `% 5` combo for Peasants (who have no primary stat yet — their
roll instead decides whether the level's point goes to Might or to Grace+Will). Per-path Might/Grace/Will
assignment and HP/MP gain ranges (`Random.Shared.Next`, inclusive both ends) are RTK's literal numbers:

| Path | Primary stat | HP gain | MP gain |
|---|---|---|---|
| 0 Peasant | Might (conditional) | 45-55 | 32-36 |
| 1 Warrior | Might (always +1) | 72-81 | 8-9 |
| 2 Rogue | Grace (always +1) | 56-63 | 24-27 |
| 3 Mage | Will (always +1) | 40-45 | 40-45 |
| 4 Poet | Will (always +1) | 48-54 | 32-36 |

Also matches RTK's `Ac -= 1` per level (AC is signed/lower-is-better in this server, §9.5 — RTK's
`baseArmor` decrement is the same direction) and the level-99 special case (`Ac` snaps to `1`), and does
a full heal (`Hp`/`Mp` set to the post-gain max, gear/buffs included) exactly like `onLevel.lua`'s
`health = maxHealth; magic = maxMagic`. Confirmed via `SendMiniText` ("You have gained new insight.") —
RTK's `onLevel.lua` also plays a sound (`playSound(123)`) and a `sendAnimation(2, 0)` visual flash that
aren't ported (no calibrated opcode for a generic non-spell animation/sound pair yet; low priority next
step if the level-up feels too quiet in-game).

**Not yet live-tested.** The exp math, the level-up stat/HP/MP gains, and the Peasant wall are all
ported from RTK source with no live 4.95 combat session behind them yet — kill a mob and watch the level
counter / minitext to confirm before trusting the numbers for real balance decisions.

---

## 11k. F1 / Central Functions menu & Silver Thread revival (added 2026-07-25 — awaiting live confirmation)

**F1 is the sentinel right next to F2's.** RTK's `map.h` defines `#define F1_NPC 4294967295`
(`0xFFFFFFFF`) — one less than F2's `0xFFFFFFFE` (§11i). Clicking it fires the same `0x43` click-info
packet as any entity click, and RTK's `clif_parseclick` (`clif.c:11061`) special-cases it: the proximity
check that normally gates an NPC click is skipped outright when `nd->bl.m == 0` — the F1 NPC has no real
map, so it's reachable from anywhere. It opens **`F1Npc.click`**
(`rtklua/Accepted/NPCs/CentralFunctions/f1npc.lua`) — a normal NPC dialog (menu strings, `player:warp`,
etc.), just triggered by a hotkey instead of walking up to a placed sprite. `Session.HandleClickInfo` now
matches `id == 0xFFFFFFFF` (`F1MenuSentinel`) → `OpenF1Menu()`, reusing the SAME async dialog machinery
(`DlgMenu`/`DlgSay`, §11e) real NPCs use, fed a synthetic `Mob` (`F1VirtualNpc`, sprite 0 → no portrait,
never spawned/looked-up — it only exists to fill the `0x30` packet header).

**Real RTK's menu has ~15 entries**, most gated on systems this server doesn't model: a GM-only submenu,
Kan (cash-shop currency) donations toward "Wisdom Star," tutor appointment, minigame win/loss stats, an
RTK-hosted character webpage's visibility toggles, "Faerie Light," "AFK Message." **Trimmed to three real
entries:**

- **Silver Thread** — **always listed** (changed 2026-08-07); the `IsDead` gate lives inside the branch, not
  on the menu entry, which is RTK's own shape: picking it alive just echoes RTK's line — *"This is for the
  dead of the land to find a path to the shaman. You are not dead, so you have no path with me."* — and does
  nothing (no warp, no state change). Listing it unconditionally is how a living player finds out that F1 is
  the way back from death, the same reasoning as "Recover Death Pile" below. RTK branches the Shaman choice on `player.country` (0 Wilderness / 1 Kugnae / 2 Buya); this
  server only has two home nations modeled, so it collapses to `_char.Nation`: Buya (`2`) offers **Felis**
  (map 338) / **Storm** (339), everyone else **Dusk** (map 8) / **Dawn** (map 9) — all four are real RTK
  map ids, confirmed present as literal 10×10 "\* Shaman" rooms in `game-data/map_index.csv`.
  **Picking one is PASSAGE ONLY (corrected 2026-08-06)** — it warps the ghost to that Shaman's hut and
  leaves it a ghost; the Shaman NPC there is what revives you (see `ReviveAbility` below). That is RTK's own
  split: `f1npc.lua`'s Silver Thread branch ends in a bare `player:warp(...)` and never touches
  `state`/`health`, and `shaman.lua`'s `click` is what does `state = 0; health = maxHealth`. The warp
  coordinates are f1npc.lua's literal ones. It previously called `ReviveAt` (heal + warp) on the stated
  premise that "no standalone Shaman NPCs are placed" — **that premise was wrong**: `ShamanNpc` rows for
  Dusk/Dawn/Felis/Storm/Di jin have been in `NPCs.csv` all along (ids 64–67, 239), they simply had no
  behaviour wired, so a warp alone would have stranded the player as a permanent ghost.
- **Toggles** — RTK's submenu covers Clan Chat + Subpath Chat; only Subpath Chat exists here (§11i), so
  the submenu is a single toggle line for now. Same flag/toggle as F2 — this is just RTK's menu exposing
  the same switch a second way.
- **Choose a Path** — RTK's `F1Npc.level5popupDialog`, offered once a Peasant (path 0) reaches level 5
  (the same threshold `AwardExp`'s Peasant wall enforces, §11j). Warps to the guild-entrance map for the
  chosen class at `(8,7)` — RTK's own coordinate — reusing `PathHalls`' existing outer map ids (§11f's
  neighbor, "Class path-hall interior warps"). Only warps; a Guildmaster NPC inside is what actually calls
  `SetCharClass` (`NpcAbility`'s path-choice ability) — matches RTK, whose `level5popupDialog` also only
  warps.

**Shaman / Priest revival — `ReviveAbility` (added 2026-08-06).** The other half of the loop, and the thing
that makes a Shaman worth walking to. Ported from RTK `NPCs/Common/shaman.lua`'s `click` and the identical
`_resurrect` helper in `NPCs/Common/totem_npc.lua`; wired in `NpcAbilities.csv` as the `revive` ability on
**`ShamanNpc`** (Dusk, Dawn, Felis, Storm, Di jin, Aura) and the **four Wilderness totem priests**
(`BaekhoNpc`, `HyunMooNpc`, `ChungRyongNpc`, `JuJakNpc` — RTK routes all four through the same helper, and
they are the `country == 0` Silver Thread destinations). **Not** on `RogueGuildShamanNpc`: despite the name,
`rogue_guild_shaman.lua` is a face/gender/eyes changer (the `appearance` ability) with no revival branch.

The ability contributes **nothing** to a living player's menu — RTK wraps its whole body in
`if player.state == 1` with no else — so it is offered only to a ghost. Because a Shaman has no other
service, that leaves exactly one menu entry, and `RunNpcAsync` dives straight into a single entry, so a
ghost clicking a Shaman lands immediately on RTK's question: *"Ah, another of the fallen come for my aid.
Are you ready to return to the world of the living?"* → Yes → `Session.ReviveInPlace` (full effective HP/MP,
`RefreshAppearance` drops the ghost form, stats pushed, persisted) → *"So shall it be! Keep yourself safe,
and free from harm."* No warp: the ghost walked here under its own power.

**Not yet live-tested.** The `0xFFFFFFFF` sentinel, the trimmed menu, and the Silver Thread shaman list are
all ported from RTK source with no live 4.95 client session behind them — confirm F1 actually opens the
menu (and that ghosts can no longer wake up on their own) before relying on it.

**Known gap:** RTK's `country == 0` (Wilderness) Silver Thread branch offers the four totem priests
(Hyun Moo 1416, Ju Jak 1411, Baekho 1406, Chung ryong 1401, all at `(11,5)`) — every one of those NPCs and
maps exists here, but `SilverThread` still collapses nation 0 into the Kugnae list. The priests now revive
if you reach them on foot; they just aren't offered as Silver Thread destinations yet.

---

## 11l. Party & trade (added 2026-07-26 — awaiting live confirmation)

Neither existed before this: the `0x1b` sub-`0x02`/`0x08` toggles (§9.5) only ever set a *willingness*
flag (`Character.Grouped`/`Exchange`) with no request/accept logic behind them, AND clicking another player
showed nothing at all (§9.5/§11e — `SendClickProfile` could only ever serialize your own character). Both
gaps are fixed together here, since the real trigger for party/trade turns out to be buttons on that same
profile window: rules are ported from RTK (`rtk/src/map/clif.c`) faithfully, presentation (the trade window
specifically) deliberately NOT — see below.

### Party (RTK "group" — `clif_addgroup`/`clif_leavegroup`/`clif_updategroup`, clif.c:13993-14148)

A `Party` (`Server/Party.cs`) is a plain in-memory leader+members list, never persisted — matches RTK's own
`groups[MAX_GROUPS][MAX_GROUP_MEMBERS]`, which is session-table state, not a DB row. The leader is always
`Members[0]`; leaving/kicking removes from the list, which naturally promotes the next member (RTK:
`group_leader = groups[groupid][0]` after a removal). **Cap is 6**, NexusTK's real historical party size —
RTK's own `MAX_GROUP_MEMBERS` (256) is just an oversized array bound for its static table, not a gameplay
rule, so it isn't copied.

Ported rules (`Session.TryPartyInvite`, RTK's literal minitext wording where it has one):

1. Target not found → *"X is nowhere to be found."* (RTK's `clif_addgroup` just `nullpo_ret`s silently on a
   bad name; this server gives feedback here, matching how whisper already handles the same case — §11 —
   including the channel: blue wisp text, `SendBlueMessage`, since 2026-08-19.)
2. Inviting yourself → *"You can't group yourself..."*
3. **Special case:** the leader "inviting" someone already in their OWN party **kicks** them — RTK's own
   self-referential branch (`tsd->group_leader == sd->group_leader && sd->group_leader == sd->bl.id` →
   `clif_leavegroup(tsd)`).
4. Your party already at the 6-member cap → *"Your group is already full."*
5. Target is dead → *"They are unable to join this group."*
6. Target's "Join a group" flag (`Character.Grouped`, `0x1b` sub-`0x02`) is off → *"They refuse to join
   this group."*
7. Target already in a group (even a different one) → the SAME *"They refuse to join this group."* line.
   One wording for both, as RTK does: a distinct message would turn the refusal into a probe for who is
   already grouped.
8. Otherwise: join, then broadcast *"X is joining the group."* on RTK's dedicated **type=11 "group"**
   minitext channel (`Session.NotifyGroup` — see the `SendMiniText` type table, §9.5). The broadcast goes
   to the WHOLE party including the person it names, so you see your own join. When the invite CREATES the
   party, both founders are announced (inviter first, then invitee) — the inviter wasn't in a group a
   moment ago either.

**Not modelled:** RTK's per-map `canGroup` gate (no such per-map concept exists here) and RTK's explicit
allowance for a *dead* player to invite others (a ghost isn't specifically blocked from grouping here
either — it just isn't specifically un-blocked, since nothing in `TryPartyInvite` checks the inviter's own
state at all, matching RTK, which also only checks the TARGET's state).

**Leaving: turn your own "Join a group" toggle OFF** (`s` → the Options menu → the group row, `0x1b`
sub-`0x02`). `HandleSetting` calls `RemoveFromParty` when that flag goes on→off and you're in a party.
This is the only way out — the profile Group button can only ADD, and its kick branch belongs to the
leader alone — and it reads as what it is: you stop being someone who groups, so you stop being grouped.
The flag is persisted, so leaving this way also means nobody can invite you again until you switch it
back on.

`RemoveFromParty` sends the exact same *"You have left the group."* text whether you left
voluntarily or were kicked — RTK's kick branch literally calls `clif_leavegroup(tsd)`, so there is no
separate "you were removed" wording to port. Dropping to one member disbands the party (*"Your group has
disbanded."* to the straggler). Disconnecting mid-party runs the same cleanup (`Session`'s read-loop
`finally` block).

**Real trigger: the "Group" button on another player's profile window.** Clicking a player now shows
*their* real click-profile (`0x34`, fixed alongside this — see §9.5/§11e), whose group/exchange status
cells are exactly what the client reads to enable those two buttons. Clicking "Group" fires **`0x2e`**
(`HandlePartyInvite`) — RTK's real `clif_addgroup` wire shape, `nameLen(u8) name[nameLen]` (identical shape
to `0x19` whisper, §11; the client already has the target's name from the profile it's showing). This is a
CONFIRMED-real 4.95 opcode (seen in an earlier capture, unlike the untested `0x29`/`0x2A`), so it's wired as
the primary path, not a defensive guess — and since `@party` was removed, the only way INTO a group.

> ⚠ **No roster read-out.** `@party` with no argument used to list the group; nothing replaced it, and no
> native window shows party membership. You know who's in your group only from the join/leave lines as they
> scroll past. Group exp still works regardless (§12b) — this is a visibility gap, not a mechanical one.

### Trade (RTK "exchange" — `clif_handitem`/`clif_handgold`/`clif_parse_exchange`, clif.c:14548-15250)

**The real binary window — BUILT 2026-08-21 (`Server/Session.Exchange.cs`).** Trade runs on the client's own
purpose-built exchange window, not on a dialog stand-in. Outgoing it is opcode **`0x42`** (RTK
`clif_startexchange` / `clif_exchange_additem` / `clif_exchange_money` / `clif_exchange_message` /
`clif_exchange_sendok`); incoming it is **`0x4a`** (`clif_parse_exchange`). Both halves were read out of the
two client binaries rather than copied from RTK, because RTK is a 7.x server and this packet family diverges
from it exactly the way the bag and profile packets do (see sub-type 2 below).

**Why it looked like a dead protocol.** §13a lists `0x42`'s handler as `0x451120` and notes it is *"the only
one with a guard: `body[0]` must be `0`"*. That is true and it is the whole trap: `0x451120` only ever
**creates** the window (a `0x288` object, ctor `0x420e80`) and returns `al=0` for every other sub-type, which
falls through the dispatcher chain to an object that does not exist until sub-type 0 has been sent. The rest
of the conversation is handled by the WINDOW's own packet handler, `0x4216a0` (vtable `0x4caf14` slot
`+0x4c`): it re-checks the opcode is `0x42` and jumps `body[0]` 1..5 through the table at `0x421704` —
1 → `0x421830`, 2 → `0x4218a0`, 3 → `0x421980`, 4 → `0x421a00`, 5 → `0x421b00`. **5.33 is the same design:**
trampoline `0x463971` (window `0x264`, ctor `0x42b300`), window handler `0x42be60`, table `0x42bfb4`.

**Outgoing `0x42` bodies** (`body[0]` = sub-type; RTK's `WFIFOB(fd,5)` is `body[0]`):

| sub | body | notes |
|---|---|---|
| 0 | `00 targetId(u32BE) nameLen(u8) name[] level(u16BE)` | opens the window. Sent to BOTH sides, each naming the OTHER as `"<name>(<class>)"`. The ctor reads the id and the name and stops — **neither client reads the level**; it is sent because RTK does |
| 1 | `01 slot(u8, 1-based)` | pops the client's own quantity prompt for that bag slot; it answers with incoming type 2 |
| 2 | `02 side(u8) rowKey(u8) icon(u16BE) [colour(u8) — 5.33 ONLY] nameLen(u8) name[]` | adds/replaces one row. `side` 0 = the recipient's OWN list (control 5), non-0 = the other party's (control 8) |
| 3 | `03 side(u8) gold(u32BE)` | one side's gold cell (controls 6 / 9) |
| 4 | `04 extra(u8) len(u8) text[]` | pops an OK box and **closes the window** — the cancel/refusal path, not a status line |
| 5 | `05 extra(u8) len(u8) text[]` | the confirm latch — see below |

**Sub-type 2 is where 4.95 and 5.33 disagree, and it is the same one-byte divergence as everywhere else.**
4.95's parser `0x4218a0` reads the name length at `body[5]`, straight after the icon; 5.33's `0x42c240` reads
an icon-**colour** byte there first. Send 4.95 the extra byte and it takes the colour index as the name
length — the identical failure already lived through on `0x0F` (§11c) and on `0x39`/`0x34` (§9.5). Pinned by
`Tests/ExchangeWireTests.cs`. The icon itself goes through the client's ordinary item-sprite resolver
(`0x435ab0`, the `+0x4000` one), so it takes an `IconWire`-encoded frame like every other grid.

**`rowKey` is a KEY, not a position.** `0x421d60` looks it up with `0x421dd0` — a linear scan comparing each
row's stored first byte — and REPLACES the matching row, appending only when there is none. We key rows by
the offerer's **bag slot**, so re-offering a slot rewrites its row instead of stacking up duplicates.

> ⚠ **The slot on the wire is 1-BASED** (the same number `WireSlot` puts in the `0x0F` that drew the item;
> RTK converts it with `id = RFIFOB(fd,10) - 1` before indexing its own array), while `InvItem.Slot` is
> 0-based. Handing the raw wire byte to `InvAt` offers the item ONE ROW DOWN from the one that was clicked —
> caught live 2026-08-22, exactly the same off-by-one the `0x29` hand gesture already accounts for.

**Sub-type 5 is a two-flag latch, and the order matters.** `extra != 0` sets the window's "them" flag,
`extra == 0` sets its "me" flag, and the box pops (and the window closes) only once BOTH are set. So the
FIRST confirm must go out as `extra=1` **to both sides** and the finalizing one as `extra=0` to both —
exactly what RTK's `clif_exchange_sendok` does. Get that backwards and the trade completes server-side with
two windows still sitting open.

**Incoming `0x4a` bodies** — every one carries the target id, so a stale window can be told from the live
one: `00 id(u32BE) 00` initiate · `01 id(u32BE) slot(u8) 00` offer a bag slot · `02 id(u32BE) slot(u8)
amount(u8) 00` offer N of it (the amount is a **u8**; 5.33 clamps it to 255 at `0x42d489`) · `03 id(u32BE)
gold(u32BE) 00` · `04 id(u32BE) 00` cancel · `05 id(u32BE) 00` confirm. Client builders: 4.95 `0x422300` /
`0x4229b0` / `0x4217c0` / `0x421770` / `0x421720`.

**The client does more than it looks.** The window carries its own bag list (control 3) and gold edit box, so
picking what to offer never touches the server — you get a `0x4a` type 1 with the slot already chosen, and
the gold field posts a type 3 on focus-out. It also disables your own side's controls once you press OK
(`0x421460` reads the confirm flags at `[win+0x27c..0x27e]`), and flashes a 10-second warning when the other
party's gold offer **decreases** (`0x421600`) — the stock anti-scam cue, entirely client-side.

`Server/Trade.cs` holds the plain data (`Trade`: two `Session`s + two `TradeOffer`s of items/gold/confirmed;
`TradeOffer`), and `Server/Session.Exchange.cs` is the protocol. An earlier implementation drove the whole
negotiation through `DlgMenu`/`DlgSay`/`DlgInput` because this layout had not been read yet; none of it
remains.

Ported rules (`Session.TryStartTrade` / `Session.Exchange.cs`):

- The target must have the "exchange" flag on (`Character.Exchange`, `0x1b` sub-`0x08`), be alive, on the
  same map, and not already in another trade — any failure replies with the client's own literal *"That
  person refuses to exchange with you."* Exchanging with yourself gets RTK's line for it, *"You move your
  items from one hand to another, but quickly get bored."*
- Offering an item or gold **un-confirms both sides** — so a stale confirm can't sneak a changed offer
  through. RTK gets this for free from escrow (an escrowed offer can only grow) rather than resetting
  anything explicitly. There is no "un-confirm" packet, so a window that already latched its indicator keeps
  showing it; that is cosmetic, because completion only ever happens on the sub-5 `extra=0` the server
  chooses to send.
- **Deliberate simplification:** RTK escrows an item out of your bag the instant you offer it
  (`pc_delitem`, restored by `clif_exchange_close`). This server does **not** — offering records a snapshot
  (bag slot / item id / durability / custom name / amount); finalize (`TransferItems`) re-checks you still
  actually hold that slot, and can only transfer LESS than promised (skipped silently) if you spent it
  elsewhere mid-negotiation, never more. That keeps an interrupted trade from eating anything, at the cost
  of one authentic behavior: an offered item is still usable until the trade closes.
- Finalizing (both sides confirm) moves gold and items in one `FinalizeTrade` call and closes both windows
  with the sub-5 `extra=0` half of the latch, carrying RTK's literal closing line, *"You exchanged, and gave
  away ownership of the items."*
- Cancelling (either side) or disconnecting mid-trade closes both windows with a sub-4 box carrying RTK's
  *"Exchange cancelled."*, and nothing moves (nothing was escrowed, so there is nothing to roll back).
- RTK also XORs `FLAG_EXCHANGE` off on both players in `clif_startexchange` and never restores it
  (`clif_exchange_close`/`_cleanup` both leave it flipped), silently corrupting a setting the profile window
  displays. That is **not** ported — the "already trading" checks do the same job without touching the flag.

**Real trigger: the "Exchange" button on another player's profile window — LIVE-CONFIRMED 2026-08-21.**
Same click-profile window as party above; clicking "Exchange" fires **`0x4a`** type 0. The `@trade <name>`
chat fallback was removed, so the profile button and the hand gesture below are the only triggers.

The captured body is **six bytes**, `00 targetId(u32BE) 00`: the builder (4.95 `0x48cd00`, 5.33
`0x4d3280`) writes the opcode, the type byte `0`, the id big-endian, a trailing `0`, and sends body length
6. **Where the id comes from is the whole game.** The button does not remember the id from the `0x43`
click that opened the window — it re-reads it out of the field the `0x34` reply parsed into
(`[window+0xb24]` on 4.95, `+0xa88` on 5.33), i.e. the `u32BE` this server puts in the click-profile
packet. That field shipped as a hardcoded `0` (documented as an unknown "scalar", §9.5) until 2026-08-21,
so **every** exchange attempt on **both** clients arrived as `4a 00 00 00 00 00 00` and died in
`Online.ById(0)` — the button was firing correctly the whole time. `SendClickProfile` sends the real
`Character.Id` now.

**Native hand-item / hand-gold — NOW WIRED (2026-08-17).** RTK's real hand gesture (select a bag item, face
a tile, press `h` to hand ONE / `H`/Shift+h to give the WHOLE stack → **`0x29`**; the gold gesture →
**`0x2A`**) is now handled — both were seen live unhandled (a `0x29` body `03 00 00` = slot 3, "hand one",
sat in the logs). Wire shapes match RTK `clif_handitem`/`clif_handgold` (`clif.c:14452`/`14548`): `0x29` =
`slot(u8, 1-based) handgive(u8: 0=hand one, 1=give stack)`, `0x2A` = `gold(u32 BE)`. Both resolve the tile
you're facing (the shared `FrontTile()` melee uses) and branch on what's there (`Session.HandleHandItem`/
`HandleHandGold` in `Session.Social.cs`):
- **a PLAYER** → this is an EXCHANGE, not a give: open/continue the SAME native trade window the `0x4a` button
  drives, with the item/gold **pre-offered** (`OpenOrContinueTradeWith` + `RecordTradeItemOffer`/`OfferGold`).
  If they aren't accepting exchanges the attempt is refused with the client's line *"That person refuses to
  exchange with you."* (`TryStartTrade`). RTK opens its binary exchange window with `clif_exchange_additem`; we
  fold it into the same window `0x4a` opens, so both triggers reach one negotiation.
- **a real MOB** (creature) → mobs don't exchange; the creature TAKES the item and **carries it** (`Mob.Handed`),
  dropping it back on the floor when it is KILLED (`World.TryDamage`) — a sword handed to a cat is recoverable by
  killing the cat. A no-kill `DespawnMob` (ridden away, quest release) does NOT drop them. **No server confirm** —
  the 4.95 client already ran the give inline before sending `0x29`, and 4.95 has no give-confirm string (see
  below). `GiveItemToMob` clears the slot with **del-reason 9**, so the client prints its OWN native
  `You gave <item>.` (a partial hand-ONE of a stack leaves the slot occupied → 0x0F redraw, no line). A **NoDrop**
  item (a mount) can't ride a creature (it can't hit the ground) → silent no-op; a bonded-but-droppable item
  (totem helm) rides along and drops still bound to its owner. **Quest creatures get first crack** via
  `TryQuestHandToMob` (RTK's BL_MOB collection path): handing the **leviathan talisman** to a *captured leviathan*
  frees it, exactly as stepping onto the tile beside it does (same one-time legend gate). Mobs do **not** accept
  money — `0x2A` to any mob/NPC is a **silent no-op** (RTK `clif_handgold` has no BL_MOB/BL_NPC branch).
- **an NPC** (stationary, unkillable mob) → the `INpcHandItemHandler.OnHandItem` hook (a quest turn-in) gets
  first refusal; if none want it the NPC refuses **out loud** (an over-head `NpcBubble`, not a status line):
  *"What are you trying to do? Keep your junky &lt;item&gt; with you!"* — and shoves it back: the item is
  deducted and **dropped on the ground on the player's own tile** (`_world.DropItem` at `_char.X/Y`; silent del
  reason 12 since the NPC already spoke). A NoDrop item can't hit the ground, so for that the NPC just speaks
  and the item stays. (This corrects the earlier "only NoDrop items reach an NPC / droppable ones are ignored"
  behavior — per the user, droppable junk handed to an NPC is refused-and-dropped-at-your-feet.)

**The `(Y/N)` give-confirm and `(count)` are NOT 4.95 (RE'd 2026-08-18).** The user recalled the give showing
a follow-up *"Are you sure you want to give this item, and no longer own it? (Y/N)"* box and a *"You gave X
(30)"* count. Neither is in the 4.95 client. `NexusTK.dat` (the string table; the exe references these by id,
so they don't appear as raw exe strings) contains the give INPUT prompt `What do you wish to give, and no
longer own? [%s\?]` (and terse `Give what? [%s?]`) and the success line `You gave %s.` — **name only, the whole
del-reason family is `%s` with no `(%d)` count variant anywhere.** There is **no** give-confirmation string and
**no** `S/He can't take it.` string. `(Y/N)` confirms exist only for *leave*, *save profile*, and **throw**
(`Do you really want to throw it, and loose ownership? (Y/N)`) — and note `0x17` throw even carries a *confirmed*
byte (`dec[0]`), infrastructure the give packet `0x29` (slot + handgive, no confirm byte) simply doesn't have.
So: the 4.95 give is a **one-step** inline gesture (the client shows the input prompt, you type the slot letter,
it sends `0x29`), the success text is the client's own `You gave <item>.` via **del-reason 9**, and the whole
give-confirm/count (plus the *"S/He can't take it."* failure line) are **later-client** behavior. We do NOT send
any of them: handing money to a mob/NPC is simply a silent no-op (the user couldn't reproduce where the 4.95
client shows that status text, so it was dropped rather than faked as server text).

**Trigger confirmed, negotiation still untested (2026-08-21).** The open question in this section used
to be whether "click a button on the profile window" was really the trigger for `0x2e`/`0x4a`. It is: the
window's hit-test jump table (`0x48c84c`) routes button 0 → the `0x4a` exchange send at `0x48c7c7` and
button 1 → the `0x2e` party send at `0x48c7ae`, and a live 4.95 client was captured sending `0x4a` from
that click. Note both handlers gate on the SAME byte — `cmp byte [edi+0xb28], 0xff`, which is the *group*
indicator cell — so `0xff` there disables both buttons and any other value enables both; the two `u8`
cells are display state, not per-button enables, and the real refusal is server-side (`TryStartTrade`,
matching RTK's `clif_startexchange`).

Still to confirm live: that the dialog negotiation that follows (offer / confirm / finalize) behaves, and
the party side end-to-end. Leaving a group is safe either way — it hangs off the `0x1b` group toggle, an
opcode this server already handles live.

---

## 11m. Inter-continent travel (world map) (added 2026-07-26 — native 0x2e screen WORKS; earlier "broken client" conclusion was our own one-byte framing bug)

Going from Mythic Nexus (the Vale) to Buya, Kugnae, and the other towns isn't a normal tile-to-tile warp —
in real NexusTK it's a special "world map" screen you reach by walking to a town's edge.

**RTK source (`RTK-Server/rtklua/Accepted/onScriptedTiles/onScriptedTilesMap.lua` +
`Scripts/sendWorldMap.lua`, `RTK-Server/rtk/src/map/clif.c:15402 clif_mapselect`):** every walk step
re-checks the player's current map name + x/y against a hardcoded per-town edge region
(`onScriptedTile`, fired unconditionally after every completed move — the same hook RTK also uses for
foraging/fall-rooms, ported here as `OnScriptedTileStep`). Stepping onto one of those tiles opens a
full-screen destination picker (`clif_mapselect`, outgoing opcode `0x2e`) listing every other continent,
built from a hardcoded table in `sendWorldMap.lua`. Clicking a destination just makes the client echo the
ordinary map-change packet (`clif.c` `case 0x3F`) with the `(map,x,y)` triple it was handed —
**`pc_warp` applies zero validation and zero level/quest/req gate** to that request; RTK's only "gate" is
that Mount Baekdu is omitted from the list entirely unless `quest["instance"] == 8`. This is a trusted,
cosmetic-only gate, not a real anti-cheat boundary — consistent with this codebase's existing trust model
for map-change requests.

**Outgoing `0x2e` — native screen, wire format re-derived from the CLIENT's own receive parser (2026-07-26).**
The format is taken from the 4.95 client itself (the only authoritative spec — **not** RTK 7.x, whose
`clif_mapselect` has a different shape). The real receive dispatch is a two-level jump table at `0x44b9c0`:
`sel = byte[0x44bc80 + (opcode-3)]`, then `jmp dword[0x44bbd4 + sel*4]`; opcode `0x2e` -> sel 22 -> stub
`0x44bac4` -> `call 0x450580` (the world-map handler). Disassembling `0x450580` (`re/disx.py 0x450580`)
gives the EXACT field-by-field parse. Body (bytes AFTER the opcode byte):

```
bgNameLen   u8        <- payload[0] IS the length. There is NO leading "kind" byte (an earlier version of
                          this doc invented one; the client reads payload[0] straight into the string length).
bgName      bgNameLen bytes
destCount   u8
originIndex u8        -- read by the parser (stored, [ebp-0x20]) but not referenced further on 4.95; sent as
                          0. NOT a mystery byte: 5.33's screen reads the same field as the index of the node
                          you are standing at, and 4.95 simply has nowhere to show it (its camera cannot
                          scroll a 640x480 background and it has no node graph). See docs/5.x §6.7.
-- destCount times:
  x0        u16BE     -- dot position on the background art (hand-placed per destination; NOT a warp coord)
  y0        u16BE
  nameLen   u8
  name      nameLen bytes    -- the destination's display label (converted to wide via MultiByteToWideChar)
  mapId     u32BE     -- the actual destination map (client reads it across two u16 slots)
  x1        u16BE     -- the actual landing tile
  y1        u16BE
```

`Session.WorldMapBody(V495, ...)` builds exactly this. **5.33 does NOT take this body** — it appends a
per-entry link list and replies with a narrower `0x3F`; sending the 4.95 shape there crashes the client
outright. See `docs/5.x/Wire-Divergences.md` §6.7. **The background is `field10` = "Map of the Kingdom":**
`Inter.dat` holds `field10.epf`..`field18.epf` (640x480 single-frame backgrounds — `field10` is the whole-
kingdom overview, `field11`-`field18` are the per-region maps Koguryo/Buya/Kaya/Jinhan/Nagnang/Paekjae/Shilla/
Sonhi), identified by rendering them and reading each image's baked-in title banner. `NATION_E` is only a 20KB
flag icon — too small for a 640x480 background, which is why it rendered black. Default `bgName = "field10"`.
**`x0,y0` is the CENTRE of the destination's label button, in `field10`'s own 640x480 pixel space** — not its
top-left corner, and not a dot with the text hung off it. Proven in the client: the world-map window's draw
loop (`0x423500`) calls `0x423600` once per entry, which computes

```
w    = textWidth(name) + 0x0c          ; 0x426ce0, the client's own bitmap-font measurer
h    = fontHeight * 2                  ; 0x426d10  (measured live: h = 20, so fontHeight = 10)
top  = y0 - h/2 ;  bottom = top  + h
left = x0 - w/2 ;  right  = left + w
```

so the button grows symmetrically around the coordinate and its width depends on how long the name is.

**Do NOT derive these from RTK.** An earlier version of this section claimed RTK's `sendWorldMap.lua` values
were "real hand-placed" coordinates on a 1024x768 canvas that just needed scaling by 0.625 (640/1024). That
is wrong on both counts: RTK's numbers are pixels in a **different background image** (its own `WMkru`, whose
geography is framed differently — plotting RTK's nine coordinates onto the modern client's `WM.EPF` puts
Kugnae, Hausson, Arctic Land and Hamgyong Nam-Do all out in the open sea), and no single scale factor maps
that space onto `field10`. Scaling made every dot land *on-screen*, which is why it passed at the time, but
it did not make any of them land in the *right place* — that is the bug this replaced.

The direct method is to pick coordinates off the real artwork. `re/worldmap_plot.py` extracts `field10.epf`
from the client's own `Inter.dat` (palette: the shared `Baram.pal` in `NexusTK.dat` — `Inter.dat` ships no
`field10.pal`), renders it, and draws every `WorldMapDests.csv` row with the exact box geometry above,
flagging any button that would land on the wooden frame or under the baked-in "Map of the Kingdom" banner.
`--grid` overlays a 20px ruler; `--move Name:x,y` / `--add Name:x,y` try a change without touching the CSV.
Its output has been verified pixel-for-pixel against a live in-client screenshot of the same five buttons.

**The crashes were OUR one-byte framing bug, not a client bug (corrected 2026-07-26).** For a while this
was written up as a confirmed "client memory-lifetime / dangling-pointer bug, dead end" — complete with a
fabricated `"File not found .EPF"` log line that never appeared in any capture, and two other inferred
theories ("wrong archive", "heap corruption"). All of that was wrong. The retail 4.95 client is not buggy;
this screen worked for millions of real sessions. The bug was entirely on our side.

**What was actually happening:** `Session.SendWorldMap` prepended a spurious leading `kind = 0` byte before
the length-prefixed background name. But the client's parser (`0x450580`, above) reads `payload[0]` **as the
bgName length**. So the client read our `00` as `bgNameLen = 0` -> empty name -> a `%s.epf` path builder
produced `"."` -> `catlookup2(".")`, and — critically — **every subsequent field was then shifted one byte**,
so `destCount` and the per-entry offsets became garbage, the handler computed a bogus huge allocation, the
raw allocator (`0x4adc38`) returned NULL, and the uncaught `_CxxThrowException` (`0x4abfa0`) killed the
process. The Frida capture that had looked like proof of a client bug —

```
DECR  op=0x2e ... 2e 00 04 49 74 65 6d 07 ...        <- our body: [00]=kind, [04 "Item"]=len+name
>>SPR catlookup2(".")                                 <- client read [00] as bgNameLen=0 -> "" -> "."
>>SPR *** _CxxThrowException reached ***
```

— is exactly what a one-byte-too-long packet looks like: `"Item"` never "failed to survive," it was simply
never where the client was told to look, because our first byte lied about the name length. **Fix: remove
the leading byte.** `SendWorldMap` now starts the body directly with `AddLenStr(d, bgName)`.

**How it was found:** by disassembling the client's OWN receive dispatch (`0x44b9c0` two-level jump table)
to confirm `0x2e -> 0x450580`, then reading `0x450580`'s parse field-by-field — which immediately showed
`payload[0]` being consumed as the string length with no preceding byte. This is the lesson: when a
shipped, retail client crashes on our packet, derive the format from the client's own parser and assume the
framing error is ours; do not trust a different-version reference (RTK 7.x) or a `@wmtest` test path as
ground truth, and never write an inferred narrative into the docs as observed fact.

**Response: the native screen is RE-ENABLED at the real trigger tiles.** `TryWorldMapTravel` now calls
`SendWorldMap("field10")` directly. `@travel` (`RunWorldMapMenuAsync`) is kept as a dialog fallback in case
the native screen ever regresses live; `@wmtest [name]` remains for trying alternate background graphics.
**Still to verify live:** that the corrected packet renders end-to-end and that destination clicks warp
(the `0x3F` reply shape below is still a guess).

**Incoming `0x3F` — click AND ESC reply, LIVE-CONFIRMED (2026-07-26).** Captured body (after the opcode):
`mapId(u32BE) x(u16BE) y(u16BE) 00` — e.g. clicking Kugnae sent `00 00 03 f3 00 12 00 0e 00` (`0x3f3`=1011,
x=18, y=14). This is exactly RTK's `case 0x3F` map-change (`pc_warp` with the client-supplied map/x/y,
clif.c:11619). **Note `mapId` is a u32, not the u16 an earlier guess assumed** — reading it as u16 gave 0 and
matched nothing, so clicks silently did nothing until this was fixed. **There is no separate cancel opcode:**
opening the map makes the client "leave the world", and BOTH a destination click and **ESC** send this same
`0x3F` — ESC just carries the player's ORIGINAL map/x/y. So `Session.HandleWorldMapSelect` warps to the
destination when the triple matches a `WorldDest`, and otherwise (ESC, or any unrecognized coords) returns
the player to the origin it stored when the map opened (`_worldMapReturnMap/X/Y`) — the player can never be
stranded on the map screen or mis-warped to arbitrary client-chosen coordinates. A `_worldMapPending` flag
(cleared on any map change) guards against a stray `0x3F` outside a world-map session.

**`@travel` — dialog fallback.** Opens the same destination list through the async dialog primitives
(`DlgMenu`, live-confirmed `0x30`/`0x3a`, §11e). No longer the primary path (the native screen is now
wired to the trigger tiles), but kept as a manual fallback independent of the `0x2e`/`0x3F` native path in
case that ever regresses live.

**`@wmtest [name]` — native screen with an explicit background name.** Calls `SendWorldMap(name)`, defaulting
to `field10` when no name is given, for trying alternate backgrounds (`field1`, `title`, other `fieldNN`).
The framing bug that used to crash this is fixed.

**Destinations** (`game-data/WorldMapDests.csv` -> `Content.WorldDests`) — 7 of RTK's 9. Dot pixels are
`field10` centres picked with `re/worldmap_plot.py` (see above), *not* scaled RTK values:

| Destination | Map | Landing (x,y) | Dot (x,y) |
|---|---|---|---|
| Kugnae | 1011 | (18,14) | (291,297) |
| Buya | 1012 | (1,11) | (335,202) |
| Mythic Nexus | 41 | (30,4) | (118,169) |
| Arctic Land | 1013 | (9,9) | (426,75) |
| KaMing's Encampment | 3800 | (31,3) | (89,269) |
| Hamgyong Nam-Do | 114 | (13,1) | (419,334) |
| Nagnang | 2520 | (8,8) | (249,379) |

These seven are the project's own placement, hand-drawn on the rendered artwork and measured back off the
annotated image — they deliberately do **not** follow the baked-in region labels (e.g. Buya's button sits
well south of the italic "Buya"). Treat them as authored content, not something to re-derive.

**Hamgyong Nam-Do is retargeted, not copied.** RTK sends it to map **99** ("North Hamgyong Valley"), which
has no map data here — which is why it used to be dropped from the list entirely. Map **114** *is*
renderable (30x30) and is the map literally titled "Hamgyong Nam-Do", so that is the target, landing on
`(13,1)` — just inside the map's north gate, so the arrival tile sits directly below the return trigger the
way every other town's does. RTK's trigger for it sits on map 99 (`y=0, x∈7..9`), which doesn't exist here;
the 114-side trigger is its **north edge, `y=0, x∈12..15`**.

Those four tiles are also `Warps.csv` 283-286 (114 -> map 99), and the two do **not** collide: `HandleWalk`
only takes a warp when `Content.TryMap(dest.m)` succeeds, and 99 has no map data, so the warp silently
doesn't fire, the step completes, and `OnScriptedTileStep` -> `TryWorldMapTravel` gets the tile. Because 114
is both a destination row and a trigger map, ESC-cancel works from it like every other town.

**Nagnang keeps RTK's numbers.** Unlike Hamgyong, nothing had to be retargeted: RTK's `sendWorldMap.lua`
sends it to map **2520** ("Nagnang Gathering", 18x20), which is renderable, landing on `(8,8)`; its trigger
is that map's `y=5, x∈7..9`, the top row of the walkable corridor (`x∈6..10` at `y=5`, everything above is
`pass=3`), with no `Warps.csv` row competing for those tiles. Its button was hand-placed on the coastal
peninsula west of the baked-in italic "Nagnang", i.e. *not* on the region label.

**Mount Baekdu** (map 4259, RTK's one quest-gated entry) is still omitted: no renderable map data.
**Hausson** (1025) is renderable but simply isn't listed — adding it is a two-line change (one
`WorldMapDests.csv` row + one `WorldMapTriggers.csv` row).

**Trigger tiles** (`game-data/WorldMapTriggers.csv` -> `Content.WorldMapTriggers`, keyed by the town map the
player is standing in): Kugnae Gathering `x=19, y∈{12,13}`; Buya Gathering `x=0, y∈8..12`; Mythic Nexus
`y=3, x∈28..32`; Haeng Tavern `x=10, y∈{7,8}`; KaMing's Encampment `y∈{0,1}, x∈30..34`; Hamgyong Nam-Do
`y=0, x∈12..15`; Nagnang Gathering `y=5, x∈7..9`. All seven fit inside their map's real dimensions per
`map_index.csv`. Every trigger map is
also a destination row, which is what makes ESC-cancel work (`SendWorldMap` puts the origin continent first,
landing on the exact origin tile).

**Live status (2026-07-26).** The trigger tiles are confirmed live. The framing bug that made the native
`0x2e` screen crash (a spurious leading byte — see above) is fixed and the corrected packet builds clean,
but the fixed native screen has **not yet been re-tested live** (the earlier "3 crashes / two different
bugs" writeup was all downstream of that same one-byte error, now understood, not a real client fault). The
native screen is re-enabled at the trigger tiles (`SendWorldMap("field10")`); `@travel` remains as a dialog
fallback. Still open once tested live: whether the picker renders correctly, and the `0x3F` click-reply
shape (still a guess, validated against `WorldDests` so a wrong guess is inert).

---

## 11n. Mail/parcels, friend & ignore lists, weather, day-night, ambush & spot-traps (added 2026-07-26 — closing the RTK feature-gap audit)

Six independent additions from a single pass, each ported at a different confidence level — see each
sub-entry for what's grounded vs. original.

**Mail (`Server/Mail.cs`, `Session.HandleBoard` sub-9).** RTK's nmail reuses the SAME
`boards_showposts`/`boards_readpost` machinery as a normal bulletin board (§11h), just addressed at board id
0 (a player's own mailbox). Confirmed the real `nmail_write`/`boards_post`/`boards_showposts`/
`boards_readpost`/`boards_delete` implementations don't survive anywhere in the reference tree (checked
`rtk/src/map/board_db.c` and `clif.c` both — only the `clif_handle_boards` dispatcher does), so there's no
wire evidence at all for how mail gets COMPOSED (no recipient field visible in the dispatcher). Reading an
existing mailbox reuses the same (already-`§11h`-unverified) `0x31` sub-2/sub-3 reply builders, now sourced
from a new `mail_posts` SQLite table when `boardId==0` instead of `board_posts`; composing goes through the
client's own compose window (`HandleNmailSend`, `0x3B` sub-6). RTK gates nmail at level 10
(`clif_handle_boards` case 6's exact wording, kept verbatim); claiming an attached item is one-shot
(`Mail.ClaimItem`), via `Session.ReadMail` on the native `0x31` sub-3 read.

> ⚠ **Players can no longer mail a PARCEL.** That lived only in `@mail sendItem`, removed with the rest of
> the chat fallbacks; native compose always posts `itemId -1`. `Mail.Send` still takes the item arguments
> and `ReadMail` still claims an attachment, so a script or quest can deliver one — only the player-facing
> send path is gone.

**Friend & ignore lists (`Character.Friends`/`IgnoreList`, `Session.HandleFriendCommand`/`HandleIgnoreCommand`).**
Ignore is real RTK (`map.h sd_ignorelist`, `clif.c` `ignorelist_add`/`ignorelist_remove`/`clif_isignore`) —
ported behaviorally (mutual: a whisper is blocked if EITHER side has the other listed, RTK's exact
`canwhisper` failure wording "They cannot hear you right now." kept verbatim in `DoWhisper`) but via chat
command (`@ignore add|remove <name>`) rather than the raw client packet (`clif_parseignore`, opcode `0x0D`
sub-dispatch) — that packet is itself a sub-switch of some other outer receive opcode never pinned down for
4.95, so wiring it blind would risk misrouting a real 4.95 client packet. **Friends has no RTK source at
all** — no such struct/feature exists anywhere in the C engine — so `@friend add|remove|list` is a pure
original addition: a saved name list plus a live online check on `@friend` (list), nothing more (no
login/logout push notification).

**Weather (`World` `MapState.Weather`/`Weather.Get`/`Weather.Set`, `Session.SendWeather`, opcode `0x1F`).**
RTK's `clif_sendweather` (`clif.c:4565`) is a real, complete wire format — a single byte, 0=clear/1=WRAIN/
2=WSNOW (`map.h`) — but it's gated by `sd->status.settingFlags & FLAG_WEATHER`, a per-player options toggle
with no evidence either way that the 4.95 client's older UI even has it. Ported at face value (best real
number on record, same precedent as this file's still-uncalibrated sound ids); **UNVERIFIED against a live
4.95 client** — `@weather <0-2>` lets it be audited directly. No automatic RTK scheduler exists to port for
WHEN weather changes (`setWeatherM`/`getWeatherM` are pure admin/quest-script levers, never called on a
timer anywhere in the C engine) — `World.Tick` rolls a 20% chance per populated map every ~15 real minutes
as our own substitute, clearly not an RTK-sourced cadence.

**Day-night clock (`World.Time`/`World.Epoch`, `Session.SendTime`, opcode `0x20`).** Fully grounded: RTK's
`clif_sendtime` (`clif.c:4524`, `hour(u8 0..23) year(u8)`) and `change_time_char`'s real timer (`map.c:1661`,
`timer_insert(450000, 450000, ...)` — one in-game hour per 7.5 real minutes, broadcast server-wide to every
connected session on each tick) are both complete C-engine code. This server had sent a hardcoded placeholder
here for a long time (`0x10`/`0x32` at one entry path, `0x00`/`0x00` at another) before the clock was wired
up live.

**The calendar is anchored to a real-world epoch, not counted (2026-08-12).** `Shared/GameCalendar.cs` —
`Epoch` = `2026-08-12T00:00:00-07:00`, at which the world reads **Yuri 1, Spring, day 1, hour 0**; the
current date is then a pure function of wall-clock time (`World.Clock.Sync` re-reads it every tick and
broadcasts `0x20` on each in-game hour rollover). It lives in `Shared`, not `Server`, because the LOGIN
server — a separate process with no `World` — stamps a new character's "Born in ..." legend with it (§9.5
"Dated legend text"). This is the one deliberate divergence from RTK, which increments a counter and
reloads `cur_time`/`cur_day`/`cur_season`/`cur_year` from its `Time` table at boot — meaning RTK's calendar
stops during downtime and slides by however long each restart took. Ours can't drift, needs no persistence
(the old `world_state` `clock.*` keys are dead; `Shared/WorldState.cs` is now an empty table awaiting its
next use), and gives the same answer to anyone who works it out from a calendar.

The wire packet itself only ever carries `hour`+`year`, but `year`'s *cadence* depends on RTK's day/season
rollover, so the derivation tracks day(1..91)/season(1..4) too, matching `change_time_char`'s constants: hour
rolls to `cur_day++`, `cur_day==92` rolls to `cur_season++`, and only `cur_season==5` rolls `cur_year++` —
i.e. **91 in-game days per season, 364 per year**, not one year per in-game day. An earlier version of this
port incremented the year on every 24-hour rollover instead (a bug, not an RTK deviation — fixed 2026-07-28),
which would have made in-game years pass ~364x too fast. 364 in-game days × 3 real hours = **45.5 real days
per Yuri**, which lands on the community "Time Chart" tutor post (`WiKiDWiND`, Poets board) exactly: Nexus
runs at 8× real time, so a 365-day year takes 365/8 = 45 5/8 real days. Season 1 = Spring per rtklua
`sys.lua`'s `getCurSeason`, the mapping behind RTK's own `curT()` = `"Yuri <year>, <season>"` timemark
(`scripts.lua`'s Mithia-lore `{Winter, Spring, Summer, Autumn}` table is a different game's ordering — don't
use it). Season never goes on the wire, and with `@time` removed nothing reports it to a player directly —
it reaches them only through legend text (`GameCalendar.Stamp`) and whatever scripts read it.

**Ambush (`Content.IsAmbushSpell`, `Session.CastAmbush`).** RTK `rogue/ambush.lua` ("Leap over your enemy to
face their back while attacking") has no mana cost in the Lua at all. Ported as a real reposition: teleports
the caster to the tile directly behind the FACED mob (relative to the target's own facing, re-facing the
caster to match) — which lines up exactly with `Combat.IsBehindTarget`'s existing unconditional positional-
backstab bonus, so the follow-up swing gets its x2 "for free" the same way a genuine sneak-up would. Only
targets world mobs (no PvP melee path exists in this server — §"no PvP damage path" elsewhere). RTK paces
reuse with `player.ambushTimer` (attack-speed-derived, not modeled here) — substituted with a flat 3s
cooldown.

**Watchful Eye / Spot Traps (`Content.IsSpotTrapsSpell`, `Session.CastSpotTraps`, `Session.RevealableTrapsNear`,
`World.TrapsNear`).** RTK's `seeSpotTraps()` (`Scripts/spotTraps.lua`) reveals nearby hidden trap NPCs by
dropping a marker item (id 99) at each trap's tile, tagged so only the caster sees it
(`addTrapSpotters`/`getTrapSpotters`). Ported via `Session.ShowGroundItem` called directly (not
`World.DropItem`) so the marker never broadcasts to the map — matching that same caster-only visibility
without needing a new per-player-visibility concept. The warrior family (`watchful_eye_warrior` + 3 reskins)
has a real RTK cooldown (`player:setAether(key, 25000)`) that never made it into the CSV export — 25000ms is
hardcoded to match the Lua; the Rogue-side `spot_traps` already had a correct exported `aether` (6000ms) and
mana (100), used as-is. **The reveal is CLASS-BRANCHED, matching RTK's `seeSpotTraps`: base-class Warrior
(`CharBasePathId == 1`) reveals hidden `ambush` tiles (RTK's `MobSpawnNpc` — see below); everyone else
(Rogue's `spot_traps`) reveals the rogue combat-trap family (dart/snare/repeating/flash/spear/poison/death/
sleep). Neither reveals the cosmetic `shiver` fall-echo, the tiger warp-traps, or a `bladestorm` decoy.**

**Ambush traps (`Content.AmbushMapDef`/`AmbushBursts`, `World.FireAmbushLocked`/`RefillAmbushLocked`;
`game-data/AmbushConfig.csv` + `AmbushBursts.csv`).** RTK's mythic caves populate via hidden `MobSpawnNpc`
tiles scattered on the floor (`NPCs/trap/mob_spawn.lua` + `rabbitTrap.lua` + `tigerTrap.lua` +
`npcSpawnHandler.lua`): stepping on one spawns a burst of cave mobs and the trap relocates. Ported as a real
`ambush` trap kind (this server DID gain trap tiles here, superseding the old AreaSpawnsTrap persistent
approximation for the regular/sentry/big-mob/spider/scorpion/ogre layers). Each configured cave map holds up
to `Count` hidden `ambush` traps, refilled on every entry and after each trigger but only while live mobs stay
under `MobCap` (RTK's population governor). A player step (`World.CheckPlayerTrapTrigger`) fires the map's
burst around them — the exact weighted variant tables (`rabbit_mob1/2/3`, `tiger_mob1/2/3`, `*_big*`,
`*_sents*`) are extractor-generated from the Lua (`re/extract_ambush_tables.py`), so they can't drift by
hand-transcription; the per-map branch (message, sentry y-split for Hare Summit/Guardroom, 1/10 big-mob roll
for Dark Pen, spider/scorpion singles, ogre 4-tile bursts) is hand-authored config. **Bosses are deliberately
NOT rolled by the ambush tiles** — they stay on the rare spawn-point system (the `RespawnSec=1500` rows still
in `AreaSpawnsTrap.csv`), which already reproduces their 1/10 + death-cooldown surprise. Only warrior Watchful
Eye reveals these tiles (see above).

---

## 12. Maps

The client keeps each map as `Maps\TK<mapId>.map` (e.g. `Maps\TK32.map`), a cache it fills from the server's
`0x06` stream (§10.7, §10.8); the server's own copies are under `game-data/maps`. The files are:

- **Headerless.** `width × height` cells, **4 bytes per cell**, row-major.
- Each cell = `ground(u16, little-endian) object(u16, little-endian)`.
- Example: `TK32` = 33×33 = 4356 bytes.
- **The ground word `v` is a tagged union over two tile sheets** (corrected 2026-10-21; `Server/TileTranslation.cs`),
  decoded by the 4.95 ground blitter `0x431820`:

  | `v` | draws | decided at |
  |---|---|---|
  | `0` | nothing | `0x43184a` |
  | `1` … `0xBFFF` | `TileA[v − 1]` (sheet 1) | `0x431853` (`cmp ax, 0xc000`), `dec eax` at `0x4318dd` |
  | `0xC000` … | `TileB[v − 0xC000]` (sheet 2) | `sub eax, 0xc000` at `0x431861` |

  The two bases are the `base` field (u16 after `count` and `palCount`) of `TileA.tbl` (`0x0001`) and
  `TileB.tbl` (`0xC000`) in `NexusTK.dat`. TileA has 9,922 frames, so a sheet-1 word never reaches `0x4000`;
  TileB has 8,938. It is **not** a tile in the low 14 bits with passability on top: masking with `0x3FFF`
  turns every sheet-2 cell into an unrelated sheet-1 tile, and sheet 2 is 31.5% of the 1,926,803 cells in the
  2,031 maps under `game-data/maps` (no word there lies in `0x4000`–`0xBFFF`).

  **The tile tables** (checked against the 4.95 install 2026-10-24). Each is exactly **`11 + 2·count`
  bytes**: `count` (u32), `palCount` (u32), `base` (u16), then byte 10, the sheet number (1 in TileA, 2 in
  TileB, 3 in TileC), then two bytes per frame: the frame's palette index (always below `palCount`) and a byte
  that is 0 in every frame of all three. `TileC.tbl` has the same layout, with base `0x0001`. Counts: TileA
  9,922 frames and 24 palettes, TileB 8,938 and 24, TileC 16,409 and 41; each `count` equals its `.epf`'s
  frame count and each `palCount` its `.pal`'s block count.

The server's `0x15` (enter-map) tells the client which map id + dimensions to open; the cells then come
from the server, which pushes the window on entry and the newly exposed strip after every step
(`PrimeViewport`, `Server/Session.cs:1254`; `StreamViewport`, `:1491-1516`), and the client writes them into
the memory-mapped `.map` (§10.7, §10.8). (Corrected 2026-10-24: this used to say the client reads the `.map`
file itself.) For a test, pick a map whose server copy has content: `TK27` is a uniform "void" tile (renders
black); `TK32` (33×33, ~180 distinct tiles, ~270 objects) renders a real area.

**Passability — ground pass flag PLUS `SObj.tbl` directional object-walls (corrected 2026-07-26).** Two
layers combine:

1. **Ground pass flag = sheet 2.** The **top 2 bits of the ground word** double as the passability flag:
   **`3` = solid**, `0` = walkable, and `1`/`2` never occur. Since sheet-1 words stay below `0x4000` and sheet-2
   words start at `0xC000`, "solid" is exactly "drawn from sheet 2": **every blocked cell is on sheet 2, and
   the server treats every sheet-2 cell as blocked.** The server splits the word into this flag and a 14-bit
   index (`MapData.Load`), collides on the flag (`MapData.Solid`) and joins the two again for the wire
   (`MapData.GroundWord`). That gives back the file's word exactly for every cell the server has not
   overridden; a cell whose pass flag it has changed goes out on the other sheet (§10.7). Water, cliffs,
   out-of-bounds, and *some* wall footprints are baked here.
2. **`SObj.tbl` directional object flags.** The object `u16` indexes `SObj.tbl`; each object has a 1-byte
   **directional wall flag** — `UP=1 DOWN=2 RIGHT=4 LEFT=8` (RTK `map.h`). A move that *enters* a cell while
   heading a given way is blocked if the destination object has that side's bit (`clif_object_canmove`:
   N→UP, E→RIGHT, S→DOWN, W→LEFT). A solid wall piece = `0x0F` (all four sides ⇒ impassable). Decorative
   objects (shadows, rugs, ground decor) have flag `0x00` ⇒ never block. This is the layer that stops you
   walking through a **building's thin side wall**, where the ground pass flag is `0` (only the door graphic
   itself gets `pass=3`).

**Why the earlier "objects are purely visual" note was wrong.** A prior pass concluded collision was
pass-only after mis-reading `SObj.tbl` (it thought wall objects `1519`-`1522` were `0x00` "walls with no
flag"). Re-derived correctly, `1519`-`1522` are *ground decor* (genuinely `0x00`), while the actual Buya
Jadespear-hut wall footprints are objects `505`-`508` = `0x0F` (solid) and the doorway is `372`/`373` =
`0x00` (open). Decisive proof the client uses these flags: a player **cannot** walk through the hut wall
even though its ground `pass=0` — only the object flag can be blocking it. The old `Obj != 0` collision was
still wrong (it blocked flag-`0` decor → "**stuck on shadows**"); the fix is to block on the object's
**directional flag**, not on `Obj != 0`.

**Server implementation.** `Server/ObjectFlags.cs` parses `SObj.tbl` (format below) into a per-object flag
byte; `MapData.BlockedMove(x,y,dir) = Solid(x,y) || ObjectFlags.Blocks(obj,dir)`. Both the **mob AI**
(`World.Tick` chase + wander steps) and the **player walk** (`Session.HandleWalk`) now use it, so mobs
respect the same walls the client draws (fixes "rabbits walked through the wall near Jadespear's hut, 2026-07-26").
Note this **diverges from RTK's own mob AI**, which is pass-only (`map_canmove` has `if(obj) return 1;`
commented out) — RTK mobs clip these walls too; we match the *client's* collision instead, which is what the
player sees. RTK's player path (`clif_canmove`→`clif_object_canmove`) does use the directional flags, but
`object_flag_init()` leaves `objectFlags[z]=flag;` commented out, so even RTK's own server never populates
the table — the live client is the only thing enforcing it there. (Doors are separate — the **'o' key
(`0x20`)** toggles a door's graphic; see "Doors" below.)

**`SObj.tbl` format** (the record walk yields exactly the header object count, then validated tile-by-tile
against the hut geometry): `u32 count` header, **1 lead byte**, then `count` records each = `u8 tileCount` ·
`tileCount × u16` frame ids · **5 bytes** · `u8 flag`, and **one more byte, a 0, after the last flag**.
`flag[objId]` (1-based) is indexed directly by the map's object id; id `0` = no object = flag `0`.
Corrected 2026-10-24 against the 4.95 install (7,608 records): the five bytes are **data, not a separator**.
They read `FF FF FF FF 00` in all but 28 records; in those 28 the first four bytes are something else, and in 2
of them the fifth byte is non-zero too. The last record's flag is stored, and the walk ends one byte short of
the end of the file, not on its last byte. The server's loader skips the five bytes unchecked and reads the
shipped table correctly (`Server/ObjectFlags.cs:112-119`).

The **client's** table (`NexusTK.dat` → `SObj.tbl`, 7608 objects) is authoritative for
the object-id space the `.map` files use; RTK's copy (`RTK-Server/rtk/SObj.tbl`, 18954 objects) is a superset
that agrees on every in-range id. Extract the client copy with
`python re/pak_extract.py "<install>/NexusTK.dat" SObj.tbl game-data/SObj.tbl`.

The **spawn coordinate must lie inside the camera viewport** at the moment `0x33` is processed, or the
placement check bails and the character never renders (§14). With scroll `(0,0)` the initial viewport
is roughly map tiles `0..14`, so spawn small (e.g. `(5,5)`), or set the scroll via `0x04` first.

---

## 13. Full opcode → client-handler table

Receive-side dispatch (server → client): connection-state object handles only `0x02`; everything else
goes through world dispatcher `0x44b9c0` = `remap[opcode-3]` (`0x44bc80`) → jump table `0x44bbd4` →
handler. Opcodes outside `0x03..0x68`, or whose remap = the default `0x44bbcd`, are **no-ops**.

`✓` = decoded/used by the working server.

| Op | Handler | Meaning |
|---|---|---|
| `0x03` | `0x44f0e0` | download/URL (reads HKLM registry) |
| `0x04` | `0x44faf0` | ✓ coords / camera scroll + commit walk |
| `0x05` | — | ✓ self entity id (server→client) |
| `0x06` | `0x44fb90` | ✓ **map CELL-PATCH** (server→client): `startX(u16BE) startY(u16BE) width(u8) height(u8)` then `width*height` cells row-major, each = `ground(u16BE) object(u16BE)`. Writes each cell into the client's live map array and redraws the object layer over the patched rect (tail `0x44df30`, independent of ground change). This is the door open/close primitive (`SendObjRow`/`HandleOpen`) — and the 5.x terrain-stream reply. (client→server `0x06` = walk+view variant, unrelated.) |
| `0x07` | `0x44fdb0` | ✓ **creature/monster list** (server→client): `count(u16)` + 12B entries `X Y id look color dir`; `look=0x8000\|monsterId` → Monster.epf. §7.2/§11a |
| `0x0b` | `0x44fb70` | **no-op** (client→server `0x0b` = **exit to select screen**, and it needs an answer — §4.2) |
| `0x0c` | `0x4502c0` | ✓ move / animate entity |
| `0x0d` | `0x450170` | ✓ over-head speech |
| `0x0e` | `0x450440` | ✓ **despawn list** (server→client): `count(u8)` + `id(u32)`× (client→server = chat) |
| `0x0f` | — | ⏳ **add item to bag slot** (server→client) — §11c. **client→server = cast spell** (`clif_parsemagic`: slot+1, then per type: answer / target id / none) — §11d |
| `0x17` | — | ⏳ **add spell to book** (server→client): `slot(u8) type(u8) name(u8len) question(u8len)` — §11d. (client→server = throw item) · `0x18` = remove spell |
| `0x10` | — | ⏳ **remove item from bag slot** (server→client) — §11c |
| `0x11` | `0x450350` | entity + 1 byte |
| `0x12` | `0x4509a0` | ✳ **per-entity virtual call**: `id(u32BE) unused(u8) sel(u8)`. `body[4]` is read and discarded; `sel` must be `< 0xa0` or the packet is dropped, then the entity is looked up (`0x44b120`) and its **vtable +0x74** is called with `sel`. Meaning depends on what `+0x74` is for the creature class — not yet chased |
| `0x13` | `0x4508f0` | ✓ **combat damage / over-head HP bar** (server→client): `id(u32BE) crit(u8) percent(u8) hitSnd(u8)` → HP bar + hit anim `0x8f−crit` + sfx — §7.2 |
| `0x15` | `0x44f8b0` | ✓ enter-map |
| `0x16` | `0x450a00` | **ground ITEM/object spawn** (draws from Item.epf `"I"`): `+5 gfx(u16) +7 id(u32) +0xb X/Y …`. **NOT a monster** — §7.2, §11a. **Walk projectile → invisible at rest; the server uses `0x07` static objects for floor items instead — §11c.** |
| `0x1a` | `0x4503a0` | ✓ action (attack/sit/…) |
| `0x1b` | `0x450830` | **writable paper popup** (RTK `clif_paperpopupwrite`): `invSlot(u8) width(u8) height(u8) 00 len(u16BE) text[]` — the `0x35` page plus an inventory slot |
| `0x1d` | `0x450db0` | ✳ **change an entity's LOOK in place**: `id(u32BE) kind(u8) look[7]`. `kind` 0 -> the standard 7-byte player look (reader `0x436120`, the same one `0x33`/`0x34` use), 1 -> the same shape tagged 1 (`0x4361b0`). Applied through `[window+0x418]`. This is the respawn-free way to push a gear/dye/morph change — worth trying against the nameplate litter, which comes from re-spawning the entity |
| `0x1e` | — | ✓ ack |
| `0x1f` | `0x450f40` | ✓ **weather** — the value is BANDED, not a state (0x0b/0x63/0x65 → `[world+0x401]`). RTK's 1/2 both read as clear; see the `0x1F` block in §recv |
| `0x20` | `0x44f820` | ✓ time-of-day (server→client). **client→server = the 'o'/Open key** (RTK `clif_parse` case `0x20` "Clicked 'O'" → `clif_cancelafk` + `clif_open_sub` → `openDoors`): in NexusTK this **toggles the faced door object's open/closed graphic in place** (cosmetic; passability untouched) — it does NOT warp. Body is a bare `00` (no target), so the server resolves the faced tile from its own pos + facing. Confirmed live 2026-07-25: sent **only** on the 'o' keypress (deliberate, not a heartbeat — RTK's handler clears AFK). `HandleOpen` toggles the faced door via the door table and the `0x06` cell-patch (above). Doors: §12 |
| `0x21` | `0x450f90` | ✳ **body-less UI window** — allocates a `0x174` object and calls `0x451700` (a `0x198`-wide widget at `(0xc, 0x8f)`, vtable `0x4cc410`) **without ever reading the packet**. Reported live as a transient blue banner where the text input sits. Nothing to encode: sending the bare opcode is the whole protocol |
| `0x26` | `0x44fb80` (main) → **`0x4903d0`** (pre-dispatch) | **Self-walk — WORKS.** Main-table handler is a no-op, but `0x26` is pre-dispatched via the self-entity vtable (`+0x38` @ `0x4cf038` → `0x48eb40` → `handlerB`). This is the 4.95 self-walk primitive (§10.3). |
| `0x29` | `0x4504b0` | ✓ **effect animation** over entity: `id(u32) effectId(u8, 1-based) A/B/C(u16)`; u8 indexes `Effect.tbl` (128 fx). **Not a damage number** — §11d |
| `0x2e` | `0x450580` | list: name + looped u16 items (skills?) |
| `0x2f` | `0x44f490` | **buy/shop grid window** (RTK `clif_buydialog`) — builds a temp stack object (vtable `0x4cc3cc`), parse `0x452c50` (`DLGMERC1.EPF`). Identified, not implemented (shops use `0x30` menus — §11e) |
| `0x30` | `0x44f530` | ✓ **NPC dialog** (server→client): text box / menu / input, `body[0..1]` kind = `00 01`/`02 02`/`04 04`. Live-confirmed on 4.95. Reply = `0x3a`. See §11e |
| `0x31` | `0x451080` | ✓ board write ACK (§11f) |
| `0x33` | `0x44fef0` | ✓ self/entity spawn (appearance) |
| `0x34` | `0x450270` | ✓ **click-profile** window (UI `0x198`); body parsed by widget `0x48b6a0`. See §9.5 |
| `0x35` | `0x450890` | **paper popup** (RTK `clif_paperpopup`): `00 width(u8) height(u8) 00 len(u16BE) text[]`. A parchment page, NOT the mail reader. §13a |
| `0x36` | `0x4515d0` | ✓ **user list window** — `0x294` object, parse `0x48a0c0`, category `"U"`. Fully decoded + implemented; requested by `0x18`. §13c |
| `0x37` | — | ⏳ **equip-window entry** (server→client) — §11c |
| `0x38` | — | ⏳ **unequip-window** (server→client) — §11c |
| `0x39` | `0x4510f0` | ✓ **self-profile** ("Mind's Eye", UI `0x198`): AC/clan/title/class/legend. See §9.5 |
| `0x3b` | `0x450fe0` | ✳ **a u16BE split into its two bytes**: reads `u16BE @body[0]`, runs the LOW byte and the HIGH byte separately through `0x44cb90(x, 0)` and hands both results to `0x44ed00`. So it is one packed pair, not a scalar — not the heartbeat companion this row used to guess |
| `0x42` | `0x451120` | **trade/exchange window** — ✓ WIRED (§11l). This trampoline is gated on `body[0]==0` (`00 targetId(u32BE) nameLen(u8) name[] level(u16BE)`, RTK `clif_startexchange`) and only opens the window; subtypes 1-5 go to the window’s own handler `0x4216a0`. §13a |
| `0x44` | `0x4511a0` | no-op (`mov al,1; ret`) |
| `0x49` | `0x44edc0` | **"resend your profile picture"** — empty body (the trampoline passes no packet pointer). Client reads `<cwd>/users/<name>.epf` (fallback `.face`) and answers on `0x4f`. §13a |
| `0x46` | `0x451020` | **"Power" board** (RTK `clif_sendpowerboard`): `01 count(u16BE)` then `count` × `id(u32BE) path(u8) power(u32BE) dye(u8) nameLen(u8) name[]`. §13a |
| `0x4a` | `0x4514d0` | ⚠ **remote file-tamper primitive**: behind a hardcoded magic it overwrites the first byte of `<System32>\Mscfg.dll` and truncates it. Never send it — §13d |
| `0x4b` | `0x451630` | **remote-send**: `len(u16BE) bytes[len]` — the client sends `bytes` back **verbatim** as its next packet (`bytes[0]` is the opcode). `0x451d10` is a plain `memcpy`, so the payload is plaintext. Buffer is `0x2714`. §13a |
| `0x59` | `0x43c1b0` (inventory pane) / `0x449f60` (world) | ✓ **item tooltip** when `body[0]==0`: `00 anchorY(u8) u16BE len text` — the box that hangs off the item list; anchorY = the box's vertical center in pane-local px (echo the request's cursor Y), 10s lifetime, X self-centering. ✓ **`body[0]==1` = the town/nation table** (§13c) — a prerequisite for the user list, not an optional extra. See §11c.1 |
| `0x66` | `0x4511b0` | ✓ **"open this URL"**: `kind(u8)` then 1 or 2 `u16BE`-length strings, string 1 = the URL. kind 0 = embedded IE (**crashes this build**, missing `XBUTTON.EPF`); kind 1/2 = the parchment OK dialog, and **kind 1 exits the game**. Not the item window. See §11c.1 |
| `0x67` | `0x4513e0` | **countdown timer** at (0x85, 0x0a): `kind(u8) seconds(u32BE)`. kind 0 updates an existing timer only, 1/2 create-or-update with a different mode, 3 closes it. `seconds >= 0xe10` (3600) picks the wider display format. §13a |
| `0x68` | `0x4516a0` | **challenge ping**: `token(u32BE)`. Client replies `0x75` = `token(u32BE) [player+0x70](u32BE)`, 9-byte body. §13a |

---

### 13a. The nine late-table handlers

All identified 2026-08-07 by walking the dispatch table directly: opcode `n` indexes the byte table at
`0x44bc80 + (n-3)`, and that byte indexes the jump table at `0x44bbd4`. Anything landing on `0x44bbcd` is a
no-op. Four of the nine are fully decoded; five open a window whose body layout is still unread.

**Decoded.**

* **`0x67` — countdown timer.** `kind(u8) seconds(u32BE)`. The widget is a `0x120`-byte object built at
  screen (`0x85`, `0x0a`) and cached in the global `[0x501d58]`, so a second `0x67` reuses it. `kind` 1 and 2
  create-or-update (two display modes), `kind` 0 only updates one that already exists — with no timer live it
  does nothing at all — and `kind` 3 closes it (`0x467c90`). The setter `0x486530` stores `now + seconds` as an
  absolute deadline, and `seconds >= 3600` selects the wider format.
* **`0x68` — challenge ping.** `token(u32BE)`. The client immediately answers with opcode `0x75`, body
  `token(u32BE)` echoed plus a second `u32BE` read from `[[0x4fd3b0]+0x70]`. Nine bytes, no window, no user
  interaction — a liveness/anti-idle probe.
* **`0x4b` — remote send.** `len(u16BE) bytes[len]`. The client copies `bytes` into a `0x2714` stack buffer and
  puts it **straight back on the wire as its own next packet**, so `bytes[0]` is the opcode it will send. The
  copy helper `0x451d10` turned out to be a plain `rep movsd` **memcpy** — there is no obfuscation on the
  payload. This makes the server able to force the client to emit any packet it likes.
* **`0x49` — resend your profile picture.** Empty body; its dispatch trampoline doesn't even pass the packet
  pointer. The client does `GetCurrentDirectory()` + `/users/` + `<name>` + `.epf` (retrying `.face`), requires
  the file to be **exactly `0xb1c` = 2844 bytes** with an internal dword at `+8` equal to `0xaf0` and two `u16`
  pairs differing by `0x38` and `0x30`, then answers on **`0x4f`** with
  `picSize(u16BE) pic[] blurbLen(u8) blurb[] 00` — byte-for-byte the packet it already sends when you save the
  profile editor, which `HandleChangeProfile` parses today. Any failure along the way still replies, with
  `picSize = 0`, so it is safe for the client; but the server stores the answer as a profile save, so a
  `picSize = 0` answer clears the stored picture (§9.5a; `Server/Session.Dialog.cs:1650-1667`).

**`0x35` is the PAPER POPUP, not a mail reader.** Earlier notes had this wrong. Parse `0x468550` reads
`00 width(u8) height(u8) 00 len(u16BE) text[len]` — four flag bytes then a length-prefixed message, which it
widens with `MultiByteToWideChar` into a `0x1f40` buffer before drawing. RTK's `clif_paperpopup` builds
exactly that, and its own comments name `body[1]`/`body[2]` "width of paper" / "height of paper". The
`0x4683a0(pkt, mode)` entry has two modes but the `0x35` handler **hardcodes mode 1**, so this is the only
shape `0x35` can take. (`0x1b` is the sibling: RTK `clif_paperpopupwrite`, same layout plus an inventory slot
at `body[0]` — a *writable* paper page.)

**Window opens, body from RTK's builders.** Cross-referenced against `RTK-Server/rtk/src/map/clif.c`, whose
offsets map to ours as `WFIFOB(fd, 5)` = `body[0]` (offset 4 is our `inc`).

| op | handler | object | parse | body |
|---|---|---|---|---|
| `0x2f` | `0x44f490` | temp stack obj, vtable `0x4cc3cc` | `0x452c50` | **sub-kind dispatcher** — `body[0]` selects one of 11 windows. Decoded below |
| `0x36` | `0x4515d0` | `0x294` | `0x48a0c0` | ✓ **user list** — DECODED 2026-08-08, §13c. (This row used to read "No RTK builder exists — still undecoded". Both halves were wrong: the builder is `clif_user_list` + `intif_parse_userlist`, and the format is now pinned.) |
| `0x42` | `0x451120` | `0x288` | `0x420e80` | ✓ **trade window** (`DLGEXC1.EPF`) — WIRED 2026-08-21, §11l. Sub-0 `00 targetId(u32BE) nameLen(u8) name[] level(u16BE)` opens it; sub-types 1-5 are handled by the WINDOW (`0x4216a0`), not by this trampoline |
| `0x46` | `0x451020` | `0x2f0` | `0x46ac00` | "Power" board, category `"P"`. `01 count(u16BE)` then `count` × `id(u32BE) path(u8) power(u32BE) dye(u8) nameLen(u8) name[]` |

`0x42` is the only one with a guard: `body[0]` must be `0` or the handler returns without building anything —
which is also RTK's "initiation" subtype. **Read that guard carefully:** it does not mean the other subtypes
are unsupported, it means they are somebody else's. `0x451120` returns `al=0` for them, the packet falls
through the dispatcher chain, and the WINDOW it built for subtype 0 picks them up on its own handler
`0x4216a0` (jump table `0x421704`, subtypes 1..5). Believing the guard was the whole story is what left
trade running on dialogs for a month — see §11l for the full decode.

#### `0x2f` — the merchant/menu family

`0x452c50` switches on `body[0]` over `0..0x0a` and hands each sub-handler `&body[1]`. Sub-kinds **7** and **9**
are no-ops; the rest each build their own window.

| `body[0]` | parse | object | what |
|---|---|---|---|
| 0 | `0x453290` | `0x494` | **menu** — RTK `clif.c:9268`, which also sets `body[1] = 1` |
| 1 | `0x452dc0` → | | |
| 2 | `0x452e30` → | | |
| 3 | `0x454240` | `0x48c` | **input amount** (buy quantity prompt) — RTK `clif.c:13000`, `body[1] = 3` |
| 4 | `0x4548f0` | `0x284` | **shop grid** — RTK `clif_buydialog` (`clif.c:12387`), `body[1] = 2` |
| 5 | `0x452f90` -> `0x4553e0` | `0x284` | ✓ **sell grid** — rows come from the CLIENT's bag; we send `scalar(u16BE) count(u8)` + wire slot bytes |
| 6 | `0x452ff0` -> `0x455c50` | `0x284` | ✓ **text-list grid** — `scalar(u16BE) count(u16BE)` then `count x [id(u32BE) len(u8) text[]]`. **The 4-byte id is SKIPPED, never stored** (`add esi, 4`, 0x455da8 — the only grid that does this) and the reply carries the TEXT, so the id is inert in 4.95: match on the string, and keep labels unique |
| 8 | `0x453050` -> `0x4564b0` | `0x284` | ✳ **SPELLBOOK grid** — carries NO rows. After the prompt it reads one `u16BE` (a ctor arg, not a count) and fills itself from `[state + 0x21ec + i*0x148]`, i = 1..0x34. This is the class-master **"forget a spell"** picker |
| 10 | `0x4530b0` -> `0x456c40` | `0x284` | ✓ **two-string grid** — `scalar(u16BE) count(u16BE)` then `count x [label(u8) description(u8)]`. LIVE-CONFIRMED: clicking a row answers `0x39` with the **description** string |

**Sub-kinds 1 and 2 are not new windows.** 1 dispatches to `0x453290` and 2 to `0x454240` — the *same*
parse functions and object sizes as sub-kinds 0 and 3. They are variants of the menu and the amount
prompt, differing only in whatever the sub-kind byte itself selects.

**Menu body** (sub 0 / 1, `0x453290`): `tag(u8) npcId(u32BE) look prompt(u16BE len) subtitle(u8 len)
count(u8)` then `count x [ optionText(u8 len) optionId(u16BE) ]`. **This server does not use it** — NPC
menus go through `0x30` dialogs instead, so the whole `0x494` merchant-skin menu is unused.

**Amount body** (sub 3 / 2, `0x454240`): `tag(u8) npcId(u32BE) look prompt(u16BE len) itemName(u8 len)
max(u16BE)`.

> **Sub-kind 6 hides a 4-byte per-row field.** The loop body opens with `add esi, 4` (`0x455da8`) *before*
> it reads the length byte — no other grid does (sub-kind 5's and 10's loops go straight to the length).
> Miss it and the parser takes a byte from the middle of the first string as the length, which is what
> made two probes look like a broken reply:
>
> * `05 "Alpha" 05 "Bravo" 07 "Charlie"` → row 0 ate `05 41 6c 70` as the id, read len = `0x68` (`'h'`) =
>   104, and its text ran from offset 5 to the end of the buffer — byte-for-byte the 18-byte `0x39` reply
>   that came back (`61 05 42 72 61 76 6f …`).
> * Three 24-byte single-letter rows → row 0 ate `18 41 41 41` as the id, read len = `0x41` (`'A'`) = 65,
>   and drew "AAAA…(20 more) + 0x18 + BBBB…" as ONE line. Row 2 was the overrun; row 3 fell off the end,
>   so only two rows drew.
>
> The reply was never malformed — it was faithfully echoing a string we had mis-lengthed. Row counter is
> a `u8` compared against the `u16` count (`0x455e31`), so >255 rows will wrap.
>
> LIVE-CONFIRMED with the corrected format: three rows drew cleanly and clicking row 0 answered
> `0x39` with `08 "AAAAAAAA"` — the row **text**. The `u32` id is skipped on parse and absent from the
> reply, so it is dead space on this client; like the buy grid, this window is matched by NAME, which
> means duplicate labels are ambiguous (cf. the unique-label map the bank withdraw flow builds).

---

### 13c. `0x36` — the user list ✅ *(decoded + implemented 2026-08-08)*

**Correcting §13a.** It said "`0x36` … no RTK builder exists to cross-reference, so it needs the
padding-sweep approach." There is a builder, in two halves — the map server forwards to the char server
(`clif_user_list`, `clif.c:1133` → `0x300B`) and the reply is formatted in `intif_parse_userlist`
(`map/intif.c:560`) off the query in `mapif_parse_userlist` (`char/mapif.c:458`). No sweep was needed: the
client's own parser `0x48a0c0` agrees with it field for field, **except for one extra u32 4.95 has and RTK
7.x does not** — so RTK's row cannot be ported verbatim.

**The round trip.** Three legs, all read out of the client:

1. **Open** — modifier bit 2 + `'W'` (`0x48e5cf: cmp al,0x77`), or entry **3** of the 8-entry UI action
   table at `0x430914`. Both land on `0x48e3d0`.
2. **`0x66` out (client→server), body `01 00 01 01 00 01 01 00`** — "send me the town/nation table",
   emitted by `0x449ed0` but only when the client's own table is empty (`0x449ec0` short-circuits on
   `[table+8] > 0`). Shares the opcode with the examine-item request, which is `body[0] == 0`; this is
   `body[0] == 1`, so the two split cleanly.
3. **`0x18` out, EMPTY body** (`0x490e00` stages the single byte and sends length 1) — "send me the user
   list". Same opcode RTK dispatches to `clif_user_list`.

**`0x59` sub-kind 1 — the town/nation table.** The user-list window needs this *first*: it labels each
column from it and resolves the viewer's own nation through it, so an empty table means an unlabelled,
empty window. Trampoline `0x44b9e9` gates on `body[0] == 1` and hands `&body[2]` to `0x449f60`:

| field | |
|---|---|
| `body[0]` | `1` — sub-kind. (Sub-kind 0 is the item tooltip, §11c.1.) |
| `body[1]` | unread |
| `body[2..3]` | `u16BE` guard — the handler bails on `<= 0` (signed). RTK writes 34; any positive number works |
| `body[4]` | `u8` count |
| rows | `id(u8) nameLen(u8) name[nameLen]` — ASCII, widened, max 0x20 wide chars |

> ⚠ **RTK's `clif_sendtowns` writes `body[0] = 64`, which 4.95 rejects outright** (its gate is `== 1`).
> That is the one field to change when porting; everything else in `clif_sendtowns` matches.

**`0x36` body.** The pointer the client walks is `packet - 1`, so its `[+1]` is our `body[0]`.

| field | |
|---|---|
| `[0..1]` | `u16BE` headline total. Drawn as **(total − hiddenRows)**, *not* the row count |
| `[2..3]` | `u16BE` row count — the loop bound |
| `[4]` | `u8` initial sort: **0** = by rank (comparator `0x48b340`), **1** = by name (`0x48b370`, `wcscmp`). RTK sends 1 |
| rows × count | `(nation<<4 \| path&7)` `(hidden<<4 \| icon&0xF)` **`nameColour(u8)`** `rank(u32BE)` `(mark<<4 \| nameLen&0xF)` `name[nameLen]` |

* **`rank(u32BE)` is 4.95-only.** RTK's row goes straight from the hunter byte to a colour byte and then
  the name length. Porting it verbatim shears every row from the 4th byte on.
* **The row above is 4.95's. 5.33's is FOUR bytes + name** — same five-byte header, no rank, no hidden
  nibble, and the mark moved into byte `+1`. See `docs/5.x/Wire-Divergences.md` §6.10; sending this row
  to a 5.x client leaves only the nation, the column and the subpath badge intact.
* **Name length is 4 bits** — it shares a byte with the tier nibble, so **15 characters is a hard wire
  limit**, not a style choice.
* **Sorting is client-side after the first paint.** The window's own toggle re-runs `0x48b390`/`0x48b3b0`
  on the rows it already holds (`0x48ae85`/`0x48aec5`) without asking us again — which is why the `0x18`
  request carries no mode, and why a post-99 stat tiebreak would have to be folded into the same `rank`
  u32 to survive. The rank comparator is *(tier ascending, then rank descending)*; that pair is exactly
  RTK's `ORDER BY ChaMark DESC, ChaLevel DESC, SUM(ChaBaseMana*2 + ChaBaseVita) DESC`.

**Two client-side filters decide whether a row appears at all** — get these wrong and the window is empty
while the packet looks perfect:

* **`hidden` nibble != 0 drops the row entirely** (and counts it into the subtrahend above) unless the
  *viewer's* state byte `[0x4fd400 + 0x1dc] == 1`. Send 0.
* **`nation` nibble must equal the viewer's own nation** for the row to land in one of the five path
  columns. Every visible row still goes into the window's master list at `+0x290`, so cross-nation players
  are carried but only your own nation fills the columns. That single nibble *is* the "compare across
  nations" behaviour.

Columns come from the path bits: `1→col0, 2→col1, 3→col2, 4→col3, 0→col4` — five `0x6e`-wide columns from
x=`0x13`, i.e. Warrior/Rogue/Mage/Poet + Peasant, matching our `PathId` exactly.

**LIVE-CONFIRMED 2026-08-08 (`@users nations` / `paths` / `tiers`):**

* **The nation nibble and the column mapping work as decoded.** Five rows, one per path, each landed in
  its own column: `[WARRIOR | ROGUE | MAGE | POET | PEASANT]` = path `1,2,3,4,0`.
* **The "tier" nibble is the MARK badge**, and its values are the subpath ranks:
  `0` none · `1` Il-San · `2` Ee-San · `3` Sam-San · `4` Sam-San's successor Sa-San. (5-15 draw nothing.)
  Pinned by sweeping tier 0-15 under sort mode 1 and reading the badges back in the order they landed.
* **Sort mode 1 really is `wcscmp` on the name** — the 16 sweep rows came back in exact lexicographic
  order (`tier0, tier1, tier10, …, tier15, tier2, tier3, tier4`), which incidentally **proves the client
  parses our name strings correctly into row+0xE**. So any "the name doesn't show" symptom is a PAINT
  problem, not a parse one.
* **Row byte +2 is the NAME's TEXT COLOUR, not RTK's "hunter".** Measured by sweeping it 0..15: it is a
  palette index, and 0..15 is the standard 16-colour palette —
  `0` black · `1` dark blue · `2` dark green · `3` teal · `4` dark red · `5` magenta · `6` brown ·
  `7` light gray · `8` dark gray · `9` light blue · `10` light green · `11` light cyan · `12` red ·
  `13` pink · `14` yellow · `15` white. Above 15 it reaches into the client's 256-entry palette and is
  sparse (32 tan, 176 pale green, 192 off-white, 208 light orange, 224 blue-green) — the space RTK's own
  143 white / 63 same-clan green / 47 GM red live in.
  > **`0` paints the name BLACK ON BLACK.** This is what made every name invisible while rows, badges and
  > marks all drew correctly — it read as a broken name field for four rounds of probing. **RTK's row is
  > not aligned with 4.95's here**: RTK has `hunter` at +2 and `colour` at +3; 4.95 has the colour at +2
  > and no hunter at all. Copying RTK's field names is what planted the wrong label.
* **The icon nibble is the SUBPATH BADGE, drawn relative to the COLUMN.** Each column listbox loads its own
  sixteen sprites from category `"I"` (`0x48af92`, `0x24`-byte slots at listbox+`0x1dc`), so one index is a
  different sprite per class. Swept across all five columns and it reproduces **Paths.csv `PthIcon`** exactly:

  | icon | Warrior | Rogue | Mage | Poet |
  |---|---|---|---|---|
  | 0 | *(base class, no badge)* | | | |
  | 1 | Barbarian | Merchant | Diviner | Druid |
  | 2 | Chongun | Ranger *(draws nothing)* | Geomancer | Monk |
  | 3 | Do | Spy | Shaman | Muse |
  | 4 | Chung ryong | Baekho | Ju jak | Hyun moo |

  **Indices 5-15 draw nothing** — the bank is effectively 5 wide, which matches `PthIcon` never exceeding
  4 in the data. So the nibble has 11 spare values and no hidden badges to find.

  So a character's whole user-list identity is **one `PthId`**: `PthType` picks the column, `PthIcon` the
  badge. Using the raw `PthId` for either is wrong — Barbarian is id 10, which as column bits (`&7` = 2)
  files a warrior under ROGUE. Four more sprites from category `"S"` at +`0x14c` are the column headers.
* `listbox+0x41c` / `+0x420` are **not** row data: they are the array of the five sibling columns and its
  count, used by `0x48b2f0` to clear the selection on the other four.

**Still unpinned:** what the client *draws* for `classIcon` (indexes one of sixteen `0x24`-byte sprite
slots at listbox+`0x1dc`), `hunter`, and `rank`. `@users sweep` sends a synthetic 16-row roster where row
*i* carries `icon=i, hunter=i, rank=i, tier=i` under the name `i<n>`, so the mapping gets read off the
screen instead of guessed — the same method `@nat` used for the nation crest.

All of them share a common prefix, read by every parse function in the same order:

```
body[0]   sub-kind
body[1]   u8    -> obj+0x280 / +0x284, passed on to the window ctor
body[2:6] u32BE entity id (the NPC), echoed back in the client's reply
body[6]   u8    NOT READ by the 4.95 client (RTK writes its descriptor-kind guess here)
body[7:]  LOOK DESCRIPTOR, read by 0x4360e0 -- kind(u8) then:
            kind 0 -> the 7-byte player look (0x436120), same one 0x30 and 0x33 use
            kind 1 -> graphic(u16BE) colour(u8)      (0x436200)
            kind 2 -> graphic(u16BE) + 1 skipped     (0x436240)
          kinds 1 and 2 both advance 4 bytes total; anything else advances 0
+4 bytes  NOT READ (RTK writes a second copy of the descriptor here)
u16BE     dialog length
          dialog text
```

**The reply — inbound `0x39`.** Every `0x2f` window answers on opcode `0x39` (RTK `case 0x39` ->
`clif_handle_menuinput`), which dispatches on `body[0]` = the tag the server wrote at the request's `body[1]`.
That is what RTK's `//For parsing purposes` byte is for. Nothing to do with `0x39` OUT (the self-profile).

| tag | window | reply payload |
|---|---|---|
| 0 | menu cancelled | — |
| 1 | menu | — |
| 2 | buy grid | `body[7]` = nameLen, `body[8..]` = the item NAME (RTK `clif_parsebuy`). Empty name = closed |
| 3 | input amount | quantity |
| 4 | sell grid | `body[7]` = the bag slot (RTK `clif_parsesell`) |

The buy grid identifying its row **by name rather than index** is the awkward part: two rows with the same
name are indistinguishable. RTK leans on it too (its bank appends `" - BONDED"` to disambiguate).

**Shop grid (`body[0] = 4`)** continues:

```
u16BE   unknown -- fed straight into the grid widget ctor 0x454fa0. RTK fills it with
        strlen(dialog), unswapped, which cannot be meaningful; treat the value as unknown
u16BE   item count
count x  icon(u16BE) price(u32BE) nameLen(u8) name[] textLen(u8) text[]
```

The per-row `text` is the buy blurb — RTK falls back to `"<path> level <n>"` when the item has no `ItmBuyText`,
which is where our `ItemDef.BuyText` column comes from.

**Sell grid (`body[0] = 5`, tag 4)** is much smaller — no names, icons or prices, because the client already
has them for your own bag. After the shared prefix it is `u16BE unknown`, then a **`u8`** count (not the buy
grid's `u16BE`), then that many wire slots.

> **These are the same ONE-based slots `0x0F` uses.** The row loop at `0x455541` reads the byte, computes
> `edx = 41 * byte`, and indexes `[invBase + edx*4 + 0x13e]` — which is byte-for-byte the address the `0x0F`
> add-item store computes (`0x48f070`), and the `0x0F` handler range-checks its slot as `1..0x34`. One scale,
> one array. RTK's `item[i] + 1` agrees.
>
> Send anything lower and every row draws further down the bag, and the loop's `test cl,cl; je` empty-entry
> check renders the overshoot as a *blank row* rather than skipping it — so the symptom is a list that looks
> **shifted** rather than misindexed, and the click still resolves correctly if the server makes the matching
> mistake on the way back. That masked the bug twice here. The reply echoes the byte unchanged, so compare it
> against the value sent rather than adjusting it.
>
> The trap is that `InvItem.Slot` on our side is **zero**-based and `SendAddItem` has always added 1 — so
> "use the slot" is wrong and "subtract one" is wronger. `Session.Items.cs` `WireSlot` is now the single
> place that conversion happens.

**Amount prompt (`body[0] = 3`, tag 3)** is the native "how many?" box. After the shared prefix: the dialog
text, then the item name as `u8 len + text`, then a `u16BE` stored at `+0x286` (`0x4543af`) which reads as the
maximum enterable amount — RTK hardcodes it to 76, which would cap any larger stack, so send the real stack
size. It answers with **two** strings (RTK `clif_parseinput` reads `body[7]` then a second length/text
immediately after); which one carries the typed number isn't settled, so take whichever parses as an integer.

**Two encodings that must not be guessed.** Both were wrong on the first attempt and both already had a
correct implementation elsewhere in the server:
* the NPC portrait is a creature look in the **`0x8000 | sprite`** space, exactly as `Session.Dialog.cs`
  `NpcPortrait` builds it for the `0x30` head — and in fact `body[6..]` IS the `0x30` head layout, so
  `WriteHead` is reused verbatim;
* a row icon is **`IconWire(frame)` = `(frame - 0x4000) & 0xFFFF`**, i.e. `frame + 0xC000`, the same encoding
  `Session.Items.cs` sends on `0x0F` for the bag. RTK only adds 49152 on its *custom-icon* branch and sends a
  raw `ItmIcon` otherwise; for 4.95 the +49152 form is right for **every** row and a raw frame draws nothing.

**Banking uses these same two windows.** RTK has no bank packet — `rtklua/.../click/bank.lua` calls
`player:buy(...)` (the buy grid) to list stored items and reads the picked item's *name* back. So "deposit"
is the sell grid and "withdraw" is the buy grid; there is no third window to find.

> **4.95 vs RTK divergence, one byte per row.** RTK writes `icon(u16BE) iconColour(u8) price(u32BE)`. The 4.95
> client reads the `u32BE` at descriptor+2 — **there is no colour byte in a shop row here**. Follow the client;
> RTK is a 7.x server and this is exactly the kind of field later clients grew.
>
> **And it grew it by 5.33** (2026-08-21). The 5.x client wants RTK's shape, colour byte and all. Sending it
> the 4.95 row shifts the price one byte late, which then swallows the name-length byte and leaves the name's
> *first letter* serving as the length — the grocer drew `pple…` at 2565 for a 10-gold Apple. The row builder
> is `Session.BuyGridRowBody`, version-split like `0x0F`/`0x37`; see `docs/5.x/Wire-Divergences.md` §6.9. This
> paragraph's prediction was right, which is the reason to keep writing them down.


**Testing these.** `@pkt <hexop> [tokens]` puts an arbitrary server→client packet on the wire — raw hex bytes,
`#n` for u16BE, `%n` for u32BE, `:text` for bare ASCII, `$text` for ASCII behind a u16BE length. Inside
`:`/`$` an underscore is a space, so a string stays one token and fields can follow it (several of these put a
length or a level *after* the text). Watch the client's own outgoing side with `re/frida_probe.py`, whose
`ENCR` lines are plaintext-before-encryption — that is how `0x68`'s `0x75` answer and `0x4b`'s echo are seen.

**RTK is the shortcut here.** Four of these nine (`0x67`, `0x68`, `0x49`, `0x35`) were decoded from the client
and then found verbatim in `RTK-Server/rtk/src/map/clif.c` — `clif_send_timer`, the ping, `clif_retrieveprofile`,
`clif_paperpopup`. `0x46` still only has an RTK builder (`0x42` is decoded and wired — §11l). Grep `WFIFOB(sd->fd, 3) = 0xNN` there
*before* disassembling a window's parse function.



---

### 13d. `0x4a` — a remote file-tamper primitive ⚠ *never wire this*

The handler table row used to read "`GetModuleFileName` anti-cheat check". Wrong API, and badly
understating what it does. Handler `0x4514d0`:

```
body[0] must be 0                      else bail
body[1] = one byte, kept
body[2..5] = u32BE, must be 0x7D3AFF99  else bail      <- hardcoded magic
  GetSystemDirectoryW(buf, 0x104)
  sprintf(path, L"%s\Mscfg.dll", buf)
  CreateFileA(path, GENERIC_READ|GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, 0)
  WriteFile(h, &body[1], 1, ...)        <- writes the packet's byte at offset 0
  SetFilePointer(h, 0xdb72, NULL, FILE_BEGIN)
  SetEndOfFile(h)                       <- truncates the file to 56178 bytes
  CloseHandle(h)
```

So a server can make any connected client **overwrite the first byte of `System32\Mscfg.dll` and truncate
it**. In period this was near-certainly Nexon's remote kill switch for a specific cheat DLL; the magic
constant is the only thing gating it. `OPEN_EXISTING` means it is a no-op on a machine that doesn't have
that file, which is every modern machine — but that is luck, not a safeguard.

**This server does not send `0x4a` and should not.** It is documented so nobody re-derives it and wires it
by accident, and so the magic is recognisable if it ever shows up in a capture from somewhere else.

### 13e. `0x3b` — CRC-16 challenge (server -> client)

Not the "client heartbeat companion" the old row guessed. `0x450fe0`:

```
token = u16BE(body[0])
crc   = crc16(token & 0xFF, 0)          ; 0x44cb90: crc = table[crc>>8] ^ byte ^ (crc<<8)
crc   = crc16(token >> 8,  crc)
-> send 0x45, body = u16BE(crc), length 3
```

The table at `0x4f27e0` is plain **CRC-16/CCITT (poly 0x1021)** — verified against a generated table, and
RTK ships the same one (`clif.c:1039`). Note the byte order: the LOW byte of the token is folded in first.
A sibling of `0x68`/`0x75` (`token(u32BE)` → echo) — both are liveness/tamper probes, neither is wired.

---

### 13b. Opcode implementation status (server -> client)

Generated from the client's own dispatch tables (the six chained dispatchers, §13) against every opcode this
server actually puts on the wire. 53 opcodes are accepted by a dispatcher; three more (`0x02`, `0x1e`, `0x22`)
we send successfully, so they belong to a dispatcher not yet enumerated.

| op | what | status | notes |
|---|---|---|---|
| `0x02` | enter-world trigger | ✅ done | mandatory first packet; builds the world object |
| `0x04` | coords / camera scroll | ✅ done |  |
| `0x05` | self entity id | ✅ done |  |
| `0x06` | map cell-patch | ✅ done |  |
| `0x07` | creature/monster list | ✅ done | also the floor-item path |
| `0x08` | stat/HUD block | ✅ done | partial — mail flag, nation, totem probed |
| `0x0a` | minitext / system line | ✅ done | type(u8) len(u16BE) text |
| `0x0c` | move / animate entity | ✅ done |  |
| `0x0d` | over-head speech | ✅ done |  |
| `0x0e` | despawn list | ✅ done |  |
| `0x0f` | add item to bag slot | ✅ done |  |
| `0x10` | remove from bag slot | ✅ done | reason byte picks the client's line; 12 is silent |
| `0x11` | turn / face | ✅ done |  |
| `0x13` | damage / over-head HP bar | ✅ done |  |
| `0x15` | enter-map | ✅ done |  |
| `0x16` | ground item spawn (walk projectile) | ✅ done | used for thrown items only; floor items use 0x07 |
| `0x17` | add spell to book | ✅ done |  |
| `0x18` | remove spell from book | ✅ done |  |
| `0x19` | music | ✅ done |  |
| `0x1a` | action (attack/sit/emote) | ✅ done |  |
| `0x1e` | ack | ✅ done |  |
| `0x1f` | weather | ✅ done | banded, not a raw state — RTK's byte reads as CLEAR |
| `0x20` | time of day | ✅ done |  |
| `0x22` | (sent during entry) | ✅ done |  |
| `0x26` | self-walk pace | ✅ done |  |
| `0x29` | effect animation | ✅ done |  |
| `0x2e` | world map / skill list | ✅ done |  |
| `0x2f` | merchant grid family | ✅ done | sub-kinds 3/4/5 wired; 0/1/2/6/8/10 not |
| `0x30` | NPC dialog | ✅ done |  |
| `0x31` | board write ACK | ✅ done |  |
| `0x33` | self/entity spawn | ✅ done |  |
| `0x34` | click-profile window | ✅ done |  |
| `0x37` | equip-window entry | ✅ done |  |
| `0x38` | unequip-window | ✅ done |  |
| `0x39` | self-profile | ✅ done |  |
| `0x59` | item tooltip / town list | ✅ done | both sub-kinds wired (0 tooltip, 1 town table) |
| `0x66` | open URL / parchment OK | ✅ done | kind 1 EXITS the game; kind 0 crashes this build |
| `0x1b` | writable paper popup | 🔧 decoded, not wired | invSlot(u8) width(u8) height(u8) 00 len(u16BE) text[] |
| `0x35` | paper popup | 🔧 decoded, not wired | 00 width(u8) height(u8) 00 len(u16BE) text[] |
| `0x42` | trade window | ✅ done | 6 sub-types; sub-0 opens (`00 targetId(u32BE) nameLen(u8) name[] level(u16BE)`), 1-5 drive the open window. §11l |
| `0x46` | "Power" board | 🔧 decoded, not wired | 01 count(u16BE), then id(u32BE) path(u8) power(u32BE) dye(u8) nameLen(u8) name[] |
| `0x49` | resend profile picture | ✅ done | `@askpic`. Client answers on 0x4f; picSize 0 = it couldn't read a valid file, §9.5a. The server stores the answer as a profile save |
| `0x4b` | remote send | 🔧 decoded, not wired | len(u16BE) bytes[] — client re-emits them verbatim as its own packet |
| `0x67` | countdown timer | 🔧 decoded, not wired | kind(u8) seconds(u32BE); kind 3 closes, 12 kinds 0-3 |
| `0x68` | challenge ping | 🔧 decoded, not wired | token(u32BE); client answers 0x75, which we do not handle |
| `0x03` | download / URL | ❓ shape unread | reads HKLM registry (0x44f0e0) |
| `0x12` | per-entity virtual call | 🔧 shape decoded, target unknown | `id(u32BE) unused(u8) sel(u8)`, `sel < 0xa0`, calls entity vtable +0x74 |
| `0x1d` | look-update in place | ✅ done | `id(u32BE) kind(u8) look[7]`; replaces despawn+respawn, `LookUpdateInPlace` |
| `0x21` | body-less UI banner | 🔧 decoded | never reads the packet — the bare opcode IS the protocol |
| `0x24` | (player-state dispatcher) | ❓ shape unread | world dispatcher maps it to the NO-OP; only the player-state dispatcher accepts it |
| `0x36` | user list window | ✅ done | §13c. Requested by `0x18`; needs `0x59` sub-1 (town table) first |
| `0x3b` | CRC-16 challenge | 🔧 decoded, not wired | `token(u16BE)`; client answers `0x45` with CRC-16/CCITT(token), low byte first |
| `0x4a` | ⚠ REMOTE FILE TAMPER | 🚫 decoded, deliberately NOT wired | truncates `<System32>\Mscfg.dll`. See §13d — do not send |
| `0x4e` | (player-state dispatcher) | ❓ shape unread | same as 0x24 — world dispatcher no-ops it |
| `0x0b` | no-op | ⊘ client no-op | handler returns immediately |
| `0x44` | no-op | ⊘ client no-op | mov al,1; ret |

**40 implemented · 7 decoded but not wired · 1 decoded and deliberately not wired · 6 still unread · 2 client no-ops** (of 56).

---

## 14. Learnings, gotchas, things tried & failed

A running list of hard-won lessons. Read this before spending days on something we already burned time
on.

**Enter-world**
- **The world object doesn't exist until you send `0x02`+`00`.** Every "silent hang / black screen"
  is this. Send it first. (§5)
- **`0x05` (self id) is mandatory** and must precede/match the id in `0x33`, or input/camera never
  bind — map loads but the client stays black and won't move.

**Rendering the self**
- **`renderKind` (the byte after the 7 appearance bytes) must be 1/2/3, not 0.** With 0 the handler
  bails before allocating the sprite → invisible character, world never goes fully live. Use 1.
- **Spawn must be inside the camera viewport.** `0x33`'s placement check (`0x424310`) is a rect
  containment test against the viewport (built from scroll `[world+0x3f0/3f4]` + view size, clamped to
  map dims). With scroll `(0,0)` the viewport is ~tiles `0..14`; spawning at `(16,16)` fails silently
  (placement returns 0, entity never created). Send `0x04` to set scroll first, and/or spawn small.
- **Appearance byte `[1]` is a FORM/STATE byte, not hair.** Putting a hair value (e.g. 0x58=88) there
  selects "form 88" = no sprite = invisible. We chased "invisible character" for a long time because
  we assumed the 7 bytes were `[sex][hair][face]…`. They are not (§8). Decode by observation, not
  assumption.
- The 4.95 player look is **only** the 7-byte type-0 form. There is **no** rich equipment/hair
  appearance packet. (`0x33` type=1 is a direct-sprite form but still draws from the *player* archive,
  not monsters — don't mistake it for a creature path; see §7.2/§11a.)

**Monsters (SOLVED — `0x07`)**
- **The real monster spawn is `0x07`, not `0x33` or `0x16`.** `look = 0x8000 | monsterId` draws from
  Monster.epf; full layout + how it was traced in §7.2/§11a. Verified live: visible, collidable, killable.
- **`0x33` can never draw a monster in 4.95** — all renderKinds use the player archive `0x4f2a84`. Don't
  retry the "type-1 sprite id" sweeps; they're a dead end (a 7.x-vs-4.95 gap — 7.x monsters ARE `0x33`).
- **`0x16` is the ground-ITEM spawn, not the monster spawn.** Cost ~8 test cycles: it creates a
  monster-*class* object (vtable `0x4cd18c`, its own tick `0x463270`) but that object draws its sprite from
  category **`"I"` = Item.epf**, so it's invisible (item id doesn't exist), has no collision, and never does
  an `"M"`(Monster) lookup. The opcode-map's "creature/**obj** spawn" note meant *object/item*. Verified via
  a Frida hook that logged which sprite category each render requested. Find the real monster path via the
  `"M"` category draw — §11a. (Later: `0x16` is also wrong for **floor items** — as a walk projectile it
  despawns at rest. Resting items use the `0x07` static base-object instead — §11c.)
- **`0x16` divide-by-zero crash:** its position interpolation `idiv`s by `|X-X'|+|Y-Y'|`, so a "stationary"
  spawn with from-tile == to-tile crashes the client (`arithmetic` exception at `0x4631f7`). Always send the
  from-tile ≥1 away.

**Movement**
- **One `0x0C` only animates; it does not advance the tile.** You must also send `0x04` to commit the
  step, or the character animates once and freezes. (§10)
- **`0x0C` overshoots by one tile — send the SOURCE tile for peers/mobs.** The walk ends one tile *past* the
  packet tile in `dir`. The self gets corrected by `0x04`; a peer/mob doesn't, so `0x0C(dest)` leaves it
  rendered a tile ahead of the server (a single-stepping mob visibly sits on the wrong tile — looked like
  mobs "walking through each other"). Fix: broadcast the **source** tile (`client_final = source + forward`).
  Cost ~an afternoon of "is collision broken?" before a client-side position trace showed `client = server+1`.
  (§7.2, §11b)
- **Use `0x26` for self-walk in 4.95** — its main-table handler is a no-op, but it is pre-dispatched to the real self-walk handler (§10.3). A no-op in the dispatch table is NOT proof an opcode is unhandled (cf. `0x08`).

**Combat**
- **The swing and the damage are two different packets.** The attack *swing* is a `0x1A` action (type=1). The
  server→client `0x13` is the *combat damage* packet — over-head HP bar + hit spark (§7.2) — sent at hit
  resolution. Do send it, with a real `percent`/`critical`; a **bare/zero** `0x13` gives `crit=0` → anim
  `0x8f` (a "death flash"), which is the historical trap that made us wrongly avoid `0x13` entirely.

**Creation / appearance mapping**
- **Creation-packet field order ≠ render-packet field order, and they use different id spaces.**
  Creation `[0]`=face, `[1]`=gender; render `[0]`=body, `[2]`=face. We shipped two wrong mappings
  (invisible chars, then everyone-female) by assuming correspondence. Map each field explicitly.
- **Decode ambiguous fields with *controlled* experiments, not opportunistic samples.** We couldn't
  tell gender from nation/totem while the tester varied several attributes at once. The moment we
  created characters literally named "male"/"female" and "faceone/two/three" (one attribute varied),
  the bytes fell out immediately. This is the single biggest methodology lesson.

**Reverse engineering the send side**
- **The creation-packet *builder* is dispatched via a C++ vtable callback** (through generic dispatch
  `0x4ae65c` / `0x485b5a`), so a Frida backtrace from the encrypt routine lands in generic message-pump
  frames, not a clean "build creation packet" function. Static RE of the builder was a dead end;
  controlled experiments settled the field layout instead.

**Crypto**
- Don't port the 7.x cipher (name-derived table + trailer bytes). 4.95 is the simple NexonInc XOR on
  both channels, no trailer. Using 7.x crypto/framing desyncs the client. (§3)

**Persistence architecture**
- Login and game channels are **separate connections**. A character created on login (port 2000) must
  round-trip through a store (file/DB) before the game channel (port 2005) can read it — you cannot
  share it in memory across the two sessions.

---

## 15. Reverse-engineering toolkit & methodology

The workflow that cracked this, for whoever continues:

- **Static disassembly** — Python + `capstone` + `pefile`. Helper script `re/disx.py`:
  `python re/disx.py <va> [len]` disassembles at an absolute VA; `xref <addr>` finds references;
  `str <text>` searches strings. Because there's no ASLR, VAs are stable across runs.
- **Live instrumentation** — **Frida 17.16.4**, script `re/frida_probe.py`, launched elevated via
  `re/probe.bat` (Run as administrator — the client's `WINXPSP2` app-compat shim forces elevation, so
  Frida spawn fails with `0x2e4` unless the launching terminal is elevated). Hooks: WSOCK recv/send/
  connect, decrypt (`0x478680`) / encrypt (`0x478760`) — the encrypt hook prints the client's
  **plaintext sends** — plus the world ctor, map load, `CreateFile`, and a `0x33`/placement trace.
  It can also capture `Thread.backtrace` on chosen opcodes.
  - **Frida 17 API note:** `Module.findBaseAddress` and 2-arg `Module.findExportByName` were removed;
    use `Process.findModuleByName(name).base`, `m.findExportByName`, `Module.findGlobalExportByName`.
- **The "look-lab"** (the highest-leverage technique for the appearance work): an in-game chat command
  in the server that spawns test dummies via `0x33` with arbitrary appearance bytes, so you read the
  sprite id-space **off the screen** instead of static-RE-ing the sprite archives.
  - `@look b0 b1 b2 b3 b4 b5 b6` — spawn one dummy (named by its bytes) with those 7 appearance bytes.
  - `@row i lo hi` — spawn a west→east row sweeping `appearance[i]` from `lo..hi` (all other bytes at a
    safe baseline). One screenshot maps an entire byte's meaning.
  - This also incidentally proved that **spawning other/non-self entities via `0x33` works** (dozens of
    dummies rendered at once) — so NPCs / other players are largely a solved rendering problem.

**Meta-lesson:** when static RE descends into deep call trees (sprite composition, vtable dispatch),
switch to **observation** — controlled inputs + watching the client — which is often an order of
magnitude faster for "what does this byte mean" questions.

---

## 16. Open problems / where to continue

- **Stats / HUD — SOLVED (2026-07-24).** The self-stats opcode is **`0x08`** — see the §"Stats packet"
  reference. (Static dispatch analysis was misleading: `0x08` is a no-op in *both* the world dispatcher
  remap and the conn-state object `0x444de0`, yet the client still processes it via a pre-dispatch path,
  so it was only found by *replaying a real 6.x server capture*. Do not trust "world remap = no-op" as
  proof an opcode is unhandled.) Layout was pinned with a self-describing gradient packet (`body[i]=i`,
  read each value off the HUD). Level/might/will/grace/HP/MP/exp/coins now populate the always-on HUD and
  round-trip through the character store. `maxHP`/`maxMP` offsets (`[5]`/`[9]`) and the `nation` id table
  (0=Neutral 1=Koguryo 2=Buya 3=Nagnang 4=Shilla 5=Jinhan 6=Paekjae 7=Kaya) are confirmed empirically (`@hp`, `@nat`).
  The `totem` id table (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None) is confirmed the same way (a live
  `@totem 0`-`@totem 3` sweep) and matches RTK's own `Player.getTotemName` — both fields are now decoded
  straight from the creation packet (§9) rather than compiled-in defaults.
- **Hair** is not renderable via `0x33` in 4.95 (no slot in the 7-byte form). Likely requires a
  different mechanism (stylist NPC / equipment), if at all.
- **Creation screen auto-close — SOLVED (2026-08-24).** The create-ack is the login-channel `0x02`
  status frame with **code byte `0x00`** (success/advance) instead of `0x0F` (message box): the client
  dismisses the creation UI and shows the text. Structure from RTK's `clif_message`
  (`rtk/src/login/intif.c` `intif_parse_2002`), confirmed live against the 4.95 client. Full form in §9.
  (The login-channel `0x02` state object itself is still un-RE'd — codes other than
  `0x00`/`0x0F` and RTK's `0x03`/`0x05` remain unmapped — but the create flow no longer needs it.)
- **Monsters — SOLVED (2026-07-24).** The real monster spawn is **`0x07`**: `look = 0x8000 | monsterId`
  (Monster.tbl index) draws a live, animated, collidable, killable creature from Monster.epf. Full layout
  and the trace that found it are in §7.2/§11a. The combat pipeline (`Mob`, melee/spell, `0x13` HP bar + hit
  spark, death beat + delayed `0x0E` despawn, exp) runs against real monsters end-to-end, and the world now **auto-populates
  both spawn sources — the static `Spawns0` towns AND the ~21.5k dynamic hunting-map mobs from RTK's Lua spawner
  (every Mythic cave/dungeon), materialized lazily per-map — with wander AI (`0x0C`), collision, respawns, drops, and
  per-player viewport streaming** — see §11b.1.
  **Remaining monster work:** map the Monster.tbl look ids to names (the matcher exists but its mapping is
  empty — §11a.2); make aggressive mobs fight back (`0x1A`/`0x13` toward the player + HP bars); a proper RTK
  colour → client-palette mapping (§11b.1).
- **Other players / NPCs.** Rendering + movement broadcast are proven and shared-world is live (§11b). Peers
  are viewport-streamed the way mobs are (`Session.SyncPeers`, §11b; corrected 2026-10-24, this item used to
  say only mobs were and to add it).
- **NPCs — built (§11e).** Stationary NPCs from RTK `NPCs0` render + stream like mobs, pace when RTK gives
  them a move timer, and open dialogs on click. Dialog (`0x30` text/menu/input + `0x3a` reply) is live on
  4.95; NPCs are composed from reusable abilities (Shop, Bank, Transport [stub — see below], Time, Repair).
  Buy/Sell and the bank's take/give also have spoken shortcuts (real NexusTK commands, not RTK-ported — see
  §11e's spoken shortcuts note). **Remaining:** the authentic buy/sell grid window (`0x2f`, currently menu-based); per-NPC
  quest/crafting scripts (RTK Lua); joint bank accounts; and the flat item-price data isn't tracked
  (`game-data/` ignored). **Transport is deliberately a stub** — RTK's `Waypoint.lua` fast-travel network
  has no evidence of existing in original 4.x/5.x NexusTK, so it isn't ported.
- **Undecoded handlers** worth probing when needed: `0x1b, 0x2f, 0x31, 0x35, 0x36, 0x39, 0x3b,
  0x42, 0x44, 0x46, 0x4a, 0x4b, 0x67, 0x68`. (`0x66` is decoded — §11c.1.)
- **A client packet you don't answer looks like a client bug.** `0x66` arrived in bursts of 5–8 identical
  packets and read as spam until the builder was traced: it's one right-click, **retried** because nothing
  came back. When a request repeats with an unchanging body, suspect a missing reply before a misparse.
- **To find who BUILDS a client packet, don't grep for the opcode as an immediate** — this client never
  stores one with `mov byte [buf], op`. It calls a write-byte helper `0x475bf0(value, dst)`, so the opcode
  is a `push` operand: search for `6a <op>` (push imm8) followed by `call 0x475bf0`, and the whole body
  falls out in order at the matching stack offsets. `0x475c10` is the matching u16 writer (**big-endian**),
  `0x475c90`/`0x475ca0`/`0x475ce0` the u8/u16/u32 readers.
- **Handler arg0 points at the OPCODE, not the body.** Every `0x4xxxxx` handler reads its first body byte at
  `[esi+1]`. Calibrated against `0x29`, whose layout is known (`0x4504b0`: `esi+1` = the u32 entity id).
- **One dispatcher is never the whole dispatch.** "Opcode X is a no-op in the jump table, yet the client
  clearly handles it" meant *there is another dispatcher*, not *this is a different build* — the packet walks
  a chain of widgets via vtable slot `+0x5c` until one returns TRUE. Find the rest by locating a handler you
  can prove exists (e.g. via the array it writes) and walking back to its caller. See the corrected Binary
  note in §11c.
- **Check the client's IMPORTS before assuming a window is hand-drawn.** `ole32!CoCreateInstance` +
  `CLSID_WebBrowser` + `IID_IWebBrowser2` meant a whole feature — item info — was a *web page*, not a
  packet-driven panel. Weeks of hunting for a nonexistent item-detail window ended with one import-table
  lookup.
- **Read a dialog's BUTTON handler before you send it, not just its parser.** Parsing `0x66`'s handler told
  us the field layout and nothing about consequences; its OK handler (`0x488480`) turned out to
  `ShellExecute` the first string and, on a flag, signal the app to exit — which is what a live client did.
  The parser says what the packet *means*; the vtable says what the packet *does*. Check both, and resolve
  the IAT slots (`ShellExecuteA` at `0x4c9378`, `SetEvent` at `0x4c9214`) — an unnamed `call [0x4c93xx]` is
  one lookup away from being obvious.

---

## 17. Reference servers & version gaps

Useful open-source NexusTK servers — **but verify every format against the 4.95 binary**, because
opcodes and layouts drift between versions:

- **github.com/jeedee/TkServer** — Ruby, **6.x**. `libs/TkGame.rb` `welcome_packet` is a readable
  self-spawn (`0x04` + `0x33`) reference. (It does **not** send the `0x02` enter-world trigger — that's
  why a direct port hangs.)
- **github.com/darkalucard/StarterTK** — C (eAthena-derived), **Mithia 7.x**. `mithia/src/map/clif.c`
  has every packet builder: `clif_parsewalk`, `clif_sendstatus`, `clif_parseattack`, `clif_sendxy`,
  `clif_sendaction`, etc. Great for *concepts*, but 7.x specifics (cipher, `0x26` walk, `0x08` stats)
  do **not** apply to 4.95.

### 17.1 github.com/unkmc/RTK-Server — Mithia/7.x, **content goldmine** ⭐ (2026-07-24)

C core (`rtk/src/`, eAthena-style) + Lua content, and — crucially — a **full production MySQL dump**:
`database/2020-09-02-21-55-01_RTK.sql.bak` (2 MB, 54 tables). **The creature-spawn packet is byte-identical
to our `0x07`** (`rtk/src/map/clif.c`): `WFIFOW(fd,+16)=SWAP16(32768+mob->look)` then
`WFIFOB(fd,+18)=mob->look_color`. So it validates our whole model: `look`=sprite, `look_color`=palette byte,
recolors = same look/different colour (`struct mobdb_data{int look, look_color}` in `map.h`; loaded from
SQL `MobLook`/`MobLookColor`; settable in Lua as `mob.lookColor`).

**Content available in the dump** (regenerate the CSVs locally with the tools below — **not committed**,
this repo is logic-only). Row counts and how much survives to 4.95 (cross-referenced against the client's
1750 `TK<N>.map` files and look-id range 0-326):

| Table | Rows | Usable in 4.95 | Gives |
|---|---|---|---|
| Mobs | 716 | 102 look-ids | name, look, `look_color`, HP(vita), exp, level, might/grace/will, min/max dmg |
| Maps | 9850 | **1387** match `TK<N>.map` | **real map names** (MapId ↔ our `0x15` mapId; `MapId 0 = Kugnae = TK000000.map ↔ TK0.map`), BGM, indoor, light, PvP, warpout flags |
| Warps | 4476 | **3060** (both ends exist) | portals `(srcMap,x,y)→(dstMap,x,y)` = world navigation |
| Spawns0 | 1175 | **1175** | *static* monster placement `(mobId, mapId, x, y)` — towns only (19 maps) |
| AreaSpawns² | 2371 | **2371** | *dynamic* hunting-map spawns `(mobId, map, count, box)` from `mobSpawnHandler.lua` — ~21.5k mobs / 767 maps (all caves/dungeons) |
| NPCs0 | 385 | **283** | NPC placement + look/`look_color` (same recolor mechanism) |
| Items | 2545 | names solid¹ | item name, type, look, icon, damage, armor, stat bonuses, prices |
| Spells | 906 | names/structure | spell & skill definitions |
| Paths | 23 | direct | class names + rank titles (Peasant→Warrior "Il san", Rogue, Mage, Poet…) |

**Ignore** (their live players' state, not content): `Accounts, Character, Inventory, Equipment, Banks,
Clans, Friends, Legends, Mail, Kills, SpellBook, Registry, Auctions, Boards, Parcels`.
**Caveats:** RTK is 7.x — ¹item look/icon ids and colours >19 reference 7.x archives (names reliable, sprite
ids/colours need 4.95 validation); stats are 7.x-balanced (structure correct, numbers a design choice); the
non-overlapping maps/warps/NPCs are 7.x-added content and are filtered out of the extracts.
**Reproduce:** clone `RTK-Server` (gitignored), then `python re/rtk_extract.py` writes the CSVs to
`game-data/` (also gitignored) + prints the client-overlap report; `re/rtk_analyze.py` lists all 54
tables with row counts. ²`AreaSpawns.csv` isn't an SQL table — it's generated from the Lua spawner by
`python re/extract_lua_spawns.py` (parses `mobSpawnHandler.lua`'s `handleSpawn(...)` calls). **None of
this data is committed** — this repo is logic-only; the CSVs are generated locally and kept out of git.

**Confirmed 7.x ≠ 4.95 gaps:**
- **Cipher:** 7.x = name-derived table cipher + 3 trailer bytes; 4.95 = simple NexonInc XOR, no trailer.
- **Self-walk:** 7.x = `0x26`; 4.95 = **also `0x26`** (fast-move OFF) — pre-dispatched past the no-op main-table handler to the real self-walk handler (§10.3). Fast-move ON: send nothing.
- **Stats:** 7.x = `0x08` (`OUT_STATUS`); 6.x = `0x08`; **4.95 = `0x08` too** (SOLVED — see the Stats
  packet reference). The field *layout* differs from 6.x/7.x, so decode offsets per-version.
- **Appearance:** 7.x `create_user` sends a rich equipment block (face, hairstyle, hair_color,
  face_color, coat…); 4.95's `0x33` type-0 is the 7-byte body/form/face/armor/weapon/shield form with
  **no hair**.

### 17.2 External data resources (kept OUTSIDE this repo)

This repo is **logic-only**. All game *data* lives outside it and is regenerated locally with the tools in
`re/`. Pointers so the data can always be re-fetched:

| Resource | What it provides | Get it / tool |
|---|---|---|
| **Client PAK** `NexusTK.dat` / `Inter.dat` (in the client install) | `Monster.epf/.tbl/.pal`, `Item.epf/.tbl`, sprites, `Str_*.res` | `re/pak_list.py`, `re/pak_extract.py` |
| **unkmc/RTK-Server** (`database/*.sql.bak`) | names, stats, `look`/`look_color`, maps, warps, spawns, NPCs, items, spells (§17.1) | clone repo → `re/rtk_extract.py` / `re/rtk_analyze.py` |
| **Nexus Atlas** via Wayback (pre-6.5, ~2005-10) | monster names, exp, type; monster art GIFs | `re/monster-matcher/` scrapers (Wayback CDX + `im_` raw fetch) |
| **DizzyThermal/TKViewer** | later-client DAT/DNA format docs (e.g. `MONSTER.DNA` struct) | GitHub (reference only) |
| **jeedee/TkServer** (6.x), **darkalucard/StarterTK** (7.x) | packet-builder concepts (§17) | GitHub (reference only) |
| **Client `Maps/TK<id>.map`** (in the client install) | authoritative *map existence* + cell count (headerless 4-byte cells) | `re/build_map_index.py` → `game-data/map_index.csv` |

### 17.3 Runtime content registry (`Server/Content.cs`) — how the data is consumed

The generated CSVs above are loaded once at startup by the static, load-once, read-only-after-load
`Content` registry (`Content.Load()` in `Program.cs`; `--selftest` exercises it offline without opening
ports). It powers the navigation commands (§11): fuzzy `FindMap`/`FindMob`/`SearchMaps`/`SearchMobs`
(score: exact < prefix < substring < subsequence), `TryWarp((map,x,y)→(map,x,y))`, and `TryMap(id)`.
Every one of these files (`map_index.csv`, `mobs.csv`, `Warps.csv`, …) lives under the game-data
directory, which is `P1998_GAME_DATA`.

**Map dims are client-authoritative** (`re/build_map_index.py`): every one of the client's ~1750
`TK<id>.map` files is emitted — a map the client ships is warpable, period. The `.map` is headerless, so
the only unknown is how to split `cells = filesize/4` into `(xs, ys)`. Any factor pair with the right
**product** is safe — the client reads exactly the file bytes, so a wrong split only skews row-stride (the
map looks sheared: tiles land in the wrong screen position, but nothing overruns or crashes). RTK dims
never gate existence; a 7.x-resized map (e.g. JadeSpear's Home, RTK 17×15 vs client 12×12) is kept, not
dropped. `Warps` are additionally filtered to destinations that exist in the client map set (no warping to
a map the client can't render). See §17.4 for how the split is actually chosen and why it matters — a wrong
guess here isn't cosmetic, it's the single most impactful data bug found in this repo to date.

### 17.4 Map-dimension mis-guessing — a real, high-impact bug, found & fixed (2026-07-27)

**The bug reports that led here:** a user played through the Mythic Rabbit dungeon and reported, room by
room: floating "black box" tiles in the middle of Mythic Owsla's corridor; Rabbit Leap "insanely broken"
with the same black-box pattern repeated ~14 times; a lava field in Hare Summit speckled with disconnected
patches of ordinary grass tile, looking like "tiles stacking with an offset"; and (separately) monster
sprites intermittently rendering *behind* terrain objects. Rabbit Hole, right next door in the same
dungeon, looked "perfect."

**First theory (wrong):** individual bad tile-id typos baked into the original client `.map` files —
i.e. real content corruption in `TK207.map` etc. This was investigated at length: RE'd the `.map` cell
format (already documented above — 4 bytes/cell, `[ground u16][object u16]`, top 2 ground bits =
passability), found specific cells whose ground-tile id broke an otherwise-perfectly-repeating decorative
motif (e.g. a fence-post pair that sits on tile `2125` fourteen times across the room, but on `2077` — the
room's own border-void filler tile — at exactly the two cells the user flagged as "black boxes"). This
looked like solid, self-consistent proof of a data typo, and does explain *individual anomalous cells* —
but it couldn't explain why entire *rooms* varied from "perfect" to "insanely broken," since a random
typo-rate wouldn't cluster that way room-by-room.

**Real root cause: wrong map dimensions sent to the client, not bad tile data.** The `.map` file is
headerless — nothing in it says how many columns wide it is — so `re/build_map_index.py` has to *guess*
`(xs, ys)` by factoring `cells = filesize/4` and picking the pair whose aspect ratio best matches RTK's
reference dimension for that map id (RTK is a **different, 7.x game version** with many rooms resized, so
its dims are only ever a hint, not authoritative). Our server then sends that guessed `(xs, ys)` straight
to the client in the `0x15` map-info packet (`Session.SendMapInfo(_char.Map, _char.MapXs, _char.MapYs,
...)`, sourced from `Content.Maps[id]` ⟵ `map_index.csv`). **The client's own `.map` file was never
corrupted at all** — we were just telling it to slice the correct bytes into the wrong number of columns,
which shears every row against the next by the wrong offset. A cell that's part of row *N*'s correct
sequence gets displayed as if it belonged to a different row — which looks *exactly* like "the same real
tile, just in the wrong place," because that's precisely what it is. Confirmed by checking RTK's dimension
hint against the real cell count for the 8 Mythic Rabbit rooms: **7 of 8 had no exact match** (only the
entrance, map 201, did) — RTK's own version had completely redesigned those rooms into different cell
counts, so the aspect-ratio fallback had nothing reliable to anchor to and silently produced a plausible-
looking but wrong guess for most of the dungeon. This one root cause explains every symptom reported: the
"black boxes" (a real tile read at a sheared offset), the lava-field speckling (grass-tile bytes from a
different logical row bleeding into what should be a solid lava block), and is the leading explanation for
the monster-behind-terrain reports too (the client's draw-order logic likely keys off row/column
adjacency, which a shear also breaks).

**How the true dimensions were found (`re/build_map_index.py`, wall-connectivity scoring):** for each
factor pair `(w, h)` of the cell count, read the ground-passability bit per cell and measure what fraction
of solid ("wall") cells have **zero** orthogonal solid neighbor (`isolation_fraction`). A correctly-strided
hand-built room draws walls as continuous lines/blobs — almost no isolated single wall pixels. A wrong
stride statistically isolates a large fraction of them, because it's effectively splicing together pieces
of unrelated rows. This needs no external reference data at all. Two extra guards were needed after the
naive version produced false positives on sparse/open maps:
- **Aspect-ratio cap (≤3.2:1) and a wall-density floor (≥5% of cells solid).** Very thin candidates
  (e.g. `280×10`) can spuriously score *better* than the true shape simply because there are too few rows
  for a wall cell to have a chance at a vertical neighbor — a metric artifact, not evidence.
- **A required improvement margin over the prior guess (`IMPROVE_EPS`), not just "passes a minimum bar."**
  An earlier, ungated version of this check flipped 440 of the 1750 maps — including dozens of *visually
  unrelated* zones (Coal Cells, Undergrowth, Tiger Stretch, Bull's Song, Northern Hordes, …) all converging
  on the same handful of dimension pairs (`50×18`, `25×36`, `18×50`). That convergence across unrelated
  content is itself the signature of the connectivity metric being fooled by geometry rather than genuinely
  reading room design — sparse/low-signal maps don't carry enough real evidence either way. Gating on a
  clear win over the existing guess (not just a technically-lower score) discards those and keeps the
  clean, high-confidence hits: e.g. the correct 20×20 render of Mythic Owsla is a strikingly symmetric,
  obviously hand-built room (nested rectangle, mirrored bracket walls) with **zero** isolated wall cells,
  vs. a messy, merely-plausible-looking 16×25 render under the old guess.
- **`Warps.csv` coordinate lower-bounds.** A real warp destination like `(x=15, y=53)` proves the room must
  be at least `16×54` — this resolves orientation ties (e.g. `20×60` vs `60×20`) that connectivity scoring
  alone can't distinguish, since it's symmetric under transpose. If the *only* candidates satisfying every
  warp on record is empty, the filter is dropped rather than vetoing every candidate outright — a single
  bad `Warps.csv` row (see the Owsla `warp 1953 → (29,27)` case below) shouldn't be able to block the fix.

**Result:** 8-of-8 Mythic Rabbit rooms resolved. 5 needed correction (all **live-verified in-game** after a
`@reload`, user-confirmed "perfffeeect!!!"):

| Room | Was sending | Corrected to |
|---|---|---|
| 201 Mythic Waters | 24×24 | *(already correct)* |
| 202 Golden Warren | 25×25 | *(already correct)* |
| 203 Rabbit Hole | 26×26 | *(already correct — matches the "looks perfect" report)* |
| **204 Rabbit Leap** | 24×50 | **20×60** |
| **205 Foraged Fields** | 26×45 | **30×39** |
| **206 Hare Depression** | 25×36 | **30×30** |
| **207 Mythic Owsla** | 16×25 | **20×20** |
| **208 Hare Summit** | 25×32 | **20×40** |

A full-client sweep (`re/build_map_index.py`, now using wall-connectivity as the *primary* method, RTK's
exact-cell-count match second, aspect-ratio guess as the last resort) found **~90 more maps** across the
whole game with the same class of bug — most memorably both city marketplaces (`Buya Marketplace` and
`Kugnae Marketplace`, `44×48 → 48×44`, an striking, obviously-correct symmetric plaza layout once fixed)
and several small named rooms (`Mhul's Chambers`, `Imperial Promenade`). These five plus the two
marketplaces are the highest-confidence tier — individually visually inspected, unambiguous. The remaining
~85 passed the same statistical gates (connectivity win + aspect/density/warp-bound filters) but were
**not** each individually eyeballed — a couple of spot-checks in that tier (`Undergrowth`, `Lilac Walk`)
looked *statistically* favored but visually ambiguous rather than a slam-dunk, so treat that broader batch
as "probably right, worth an in-game glance if a specific room still looks off" rather than fully proven
the way the dungeon rooms are.

**Also found and set aside, not fixed:** `Warps.csv` row **1953** (`SourceMapId=205, x=6, y=6 →
DestinationMapId=207, x=29, y=27`) has a destination that is *mathematically impossible* for any
rectangular interpretation of Owsla's 400-cell file (no factor pair of 400 has both dimensions `>29`× `>27`
simultaneously — the room simply isn't big enough in any orientation). This predates and is independent of
the dimension bug above; it's bad data inherited verbatim from the RTK-Server SQL dump this table was
extracted from (RTK's own `warps.txt` config doesn't even have a `205→207` connection at all, and the door
has no matching reverse warp either — likely an orphaned/stale row). Left unfixed pending a decision on
whether to redirect it or remove it outright (see `memory/nexustk-495-mythic-rabbit-dims.md`).

---

## 18. Key binary addresses (quick reference)

`NexusTK_local.exe`, ImageBase `0x400000`, no ASLR.

**Crypto**
| Addr | What |
|---|---|
| `0x478680` | decrypt |
| `0x478760` | encrypt (hook here to see plaintext sends) |
| `0x478850` | XOR primitive |
| `0x50211c` | key buffer (built from "NexonInc.") |
| `0x4f3358` | identity table |

**Dispatch & field readers**
| Addr | What |
|---|---|
| `0x444de0` | connection-state `0x02` handler (enter-world) |
| `0x44a090` | game-world object ctor |
| `0x44a640`(ish)–`0x44a744` | game-world object **teardown** — releases the cached globals, conditionally sends `0x0B`, then builds the replacement screen (§4.2) |
| `0x44ed50` | **`0x0B` sender** (exit to select screen): `push 0xb` → `0x475bf0`, body `00`, len 1 → send. One caller: `0x44a6e2` |
| `0x4759f0` | get the global connection object (`[0x50214c]`) — **not** a packet builder |
| `0x475ab0` | send the staged packet |
| `0x475bf0` / `0x475c10` | write u8 / write u16BE into a send buffer (the opcode is a `push` operand — see §16) |
| `0x475bd0` | write the connection-object flag `[+0x3aa52]` (raised across the world→login screen swap; the receive-mode byte is `+0x3aa4c`) |
| `0x44b9c0` | world dispatcher (opcodes 0x03–0x68) |
| `0x44bc80` | remap table (`remap[opcode-3]`) |
| `0x44bbd4` | jump table |
| `0x475c90` / `0x475ca0` / `0x475ce0` | read u8 / u16BE / u32BE |
| `0x44b120`, `0x45cb80` | find-entity-by-id |

**Key handlers**
| Addr | Opcode / role |
|---|---|
| `0x44faf0` | `0x04` coords/camera |
| `0x44f8b0` | `0x15` enter-map |
| `0x44fef0` | `0x33` self/entity spawn |
| `0x436120` | 7-byte appearance parser (`0x33` type 0) |
| `0x4361b0` | direct-sprite appearance parser (`0x33` type 1; still player archive) |
| `0x450a00` → `0x44dbc0` → `0x463020` → `0x462ec0` | `0x16` item/object spawn + ctor |
| `0x44fdb0` → `0x44d7d0` → `0x461a50` | **`0x07` monster-list handler → entity factory → creature ctor (vtable `0x4cd098`)** |
| `0x461c70` / `0x462950` | creature-entity draw methods (route to monster resolver when `[ent+0x178]!=0`) |
| `0x434020` / `0x4342e0` | **monster sprite resolvers** (push `"MONSTER.EPF"` `0x4f1d18` → `0x433d00` Monster.tbl → catlookup) |
| `0x435ab0` | item sprite resolver (`id+0x4000` in category `"ITEM"`) |
| `0x431020` / `0x430de0` | generic sprite lookup by `(categoryName, id)` |
| `0x430c30` | load/create a sprite category into the manager `[0x4fd2f8]` |
| `0x47b4c6` | A–Z sprite-category registry table |
| `0x4502c0` | `0x0C` move/animate |
| `0x450170` | `0x0D` speech |
| `0x4503a0` | `0x1A` action |
| `0x4508f0` | `0x13` combat damage / over-head HP bar (+ hit anim `0x8f−crit`) |
| `0x424310` | spawn placement / viewport containment check |
| `0x463380` | **nameplate/marker decoration ctor (player archive) — PATCHED to `xor eax,eax; ret 0x14` (see §8.1), always leaked one per appearance refresh** |
| `0x462050` | attach a built decoration/sprite to an entity |
| `0x425350` | conditional decoration-detach on entity destroy (gated on `[entity+0x104]!=5`, called from `0x44da40`) |
| `0x44da40` | full entity destroy: spatial-unlink + hashmap-unlink (`0x45c8f0`) + `0x425350` + dtor (peer/mob `0x33`+`0x07` path) |
| `0x44d9f0` | despawn-by-id destroy (real `0x0E` path, `0x450440`) — no self/peer distinction, destroys whatever the id hashmap resolves to |
| `0x45c830` / `0x45c8f0` / `0x45cb80` | id-keyed entity hashmap: register / remove / lookup |
| `0x44c660` | self camera/viewport refresh (called from `0x44d7d0`'s self-id branch; repositions the persistent self entity in place, no realloc) |
| `0x44d7d0` → `0x43fd80` → `0x463380` → `0x460760` | sprite build path |

---

*Maintained as facts are learned. When you discover something new — an opcode, a byte meaning, a
gotcha — add it here so the next person doesn't re-derive it.*
