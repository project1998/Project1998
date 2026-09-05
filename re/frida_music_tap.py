#!/usr/bin/env python
"""
LIVE map -> background-music capture for the 7.5.2.0 KRU client.

Answers the one question no offline source can: which track/playlist does the REAL server
send for each map? ClassicTK's Maps table sets playlist 902 on 9799 of 9850 maps (see
NexusServer docs), which looks like a global fallback rather than the shipped assignment --
24 of the 26 playlists in the client's Mus000.dat are referenced by nothing. Walking the live
world with this tap settles it from the authority.

HOW IT WORKS -- no cipher work needed. The live client is packed and session-keyed, but its
universal decrypt routine at NexusTK.exe+0x178b20 hands us PLAINTEXT for every inbound packet
(decrypt(this, src, len, out) -> plaintext_len, out[0] = opcode). Same hook frida_decode_live.py
uses. We watch two opcodes:

  0x19  music.  Layout is UNCHANGED from 4.95 and corroborated by RTK's own sender
                (rtk/src/map/clif.c ~4650, which writes this exact packet):
                    body[1] = bgmtype  (1 = mp3/lsr playlist, 2 = midi)
                    body[3] = bgm id   (u16 BE)   <- repeated at body[5] on 7.x
                    body[7] = volume   (0x64 = 100)
  0x15  enter-map. Fires on every map change, so it brackets each 0x19 and tells us WHICH map
                the following music belongs to. Its body is logged raw -- the map-id field is
                identified offline against map_index.csv rather than guessed here.

The id space tells you what you captured: 1xx = a single MP3 track, 8xx = a sequential .lst
playlist, 9xx = a shuffle .lsr playlist (the client picks the loader by range; see the
%08d.MP3 / %08d.LST / %08d.LSR format strings in the 5.33 exe).

This tap is PASSIVE: read-only, one Interceptor on a decrypt function, no injection and no
automation. It does not scan memory -- JS byte-walks freeze this client.

Usage:
    python re/frida_music_tap.py --attach            # game already running (recommended)
    python re/frida_music_tap.py --pid 16612
Walk between areas (Buya, Kugnae, the Arctic, a dungeon...). Ctrl-C when done.

Output: re/music_capture.jsonl, one row per 0x15/0x19, plus a printed running timeline.
"""
import sys, os, json, frida

MOD = "NexusTK.exe"
KRU_DIR = r"C:\Program Files (x86)\KRU\NexusTK"   # the LIVE client; see the guard in main()
DEC_RVA = 0x178b20                       # universal decrypt (same hook as frida_decode_live.py)
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "music_capture.jsonl")

JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
const dec = MAIN.base.add(__RVA__);
const WANT = {0x19: 1, 0x15: 1};
Interceptor.attach(dec, {
  onEnter(args){ this.out = args[2]; },
  onLeave(ret){
    try{
      let n = ret.toInt32(); if (n <= 0) return; if (n > 512) n = 512;
      const b = new Uint8Array(this.out.readByteArray(n));
      if (!WANT[b[0]]) return;
      const hex = Array.from(b).map(x => ('0' + x.toString(16)).slice(-2)).join(' ');
      send({t:'pkt', ts: Date.now(), op: b[0], n: n, hex: hex});
    }catch(e){}
  }
});
send({t:'info', m:'music tap installed @ +0x__RVAHEX__ (watching 0x15 enter-map, 0x19 music)'});
""".replace("__MOD__", MOD).replace("__RVA__", hex(DEC_RVA)).replace("__RVAHEX__", format(DEC_RVA, "x"))


def parse_map(b):
    """0x15 enter-map body, confirmed against 8 live captures (Mythic Nexus -> 41, KaMing's -> 3800,
    both matching Maps.csv):
        [0]=0x15  [1:3]=map id u16BE  [3:5]=width u16BE  [5:7]=height u16BE
        [7]=flag (4 or 5, meaning unknown)  [8]=0  [9]=name length  [10:]=name (ASCII)
    """
    if len(b) < 10:
        return {"mapid": None, "w": 0, "h": 0, "flag": 0, "name": ""}
    nlen = b[9]
    name = bytes(b[10:10 + nlen]).decode("latin1", "replace") if nlen else ""
    return {"mapid": (b[1] << 8) | b[2], "w": (b[3] << 8) | b[4], "h": (b[5] << 8) | b[6],
            "flag": b[7], "name": name}


def kind(bgm):
    """The id ranges the client uses to choose its loader (single track vs .lst vs .lsr)."""
    if bgm == 0:            return "silence"
    if 900 <= bgm <= 999:   return "shuffle playlist (.lsr)"
    if 800 <= bgm <= 899:   return "sequential playlist (.lst)"
    return "single track (.mp3)"


def main():
    dev = frida.get_local_device()
    outf = open(OUT, "w", encoding="utf-8", buffering=1)
    seen, last_map = [], None

    def on_message(msg, data):
        nonlocal last_map
        if msg["type"] == "error":
            print("[frida-error]", msg.get("description", str(msg))); return
        p = msg["payload"]
        if p.get("t") == "info":
            print("[i]", p["m"]); return
        if p.get("t") != "pkt":
            return
        b = [int(x, 16) for x in p["hex"].split()]
        rec = {"ts": p["ts"], "op": p["op"], "hex": p["hex"]}

        if p["op"] == 0x15:                       # enter-map
            m = parse_map(b)
            last_map = m
            rec.update(what="enter-map", **m)
            print(f"\n[map ] {m['mapid']:<6} {m['name']!r} {m['w']}x{m['h']}")
        elif p["op"] == 0x19 and len(b) >= 5:      # music
            btype = b[1]
            bgm = (b[3] << 8) | b[4]
            rec.update(what="music", bgmtype=btype, bgm=bgm,
                       chan={1: "mp3/playlist", 2: "midi"}.get(btype, f"type{btype}"),
                       mapid=(last_map or {}).get("mapid"),
                       mapname=(last_map or {}).get("name"))
            seen.append(((last_map or {}).get("mapid"), (last_map or {}).get("name"), btype, bgm))
            where = f"{rec['mapname']!r} ({rec['mapid']})" if last_map else "(map unknown)"
            print(f"[MUS ] bgm={bgm:<5} type={btype} -> {kind(bgm)}   for {where}")
        outf.write(json.dumps(rec) + "\n")

    def instrument(pid, label=""):
        session = dev.attach(pid)
        script = session.create_script(JS)
        script.on("message", on_message)
        script.load()
        print(f"[+] instrumented pid {pid} {label}")
        return session

    pid = None
    for i, a in enumerate(sys.argv[1:]):
        if a.startswith("--pid"):
            pid = int(a.split("=")[1]) if "=" in a else int(sys.argv[i + 2])

    if pid:
        instrument(pid, "(--pid)")
    else:
        # DANGER: a private-server client (Project1998 etc.) is ALSO named NexusTK.exe, and the
        # decrypt RVA below is specific to the KRU 7.5.2.0 build. Hooking that address inside a
        # 4.95/5.33-based client points Interceptor at unrelated code and can crash it. So match
        # on the executable PATH, never on the process name alone.
        procs = []
        for q in dev.enumerate_processes(scope="full"):
            if q.name.lower() != MOD.lower():
                continue
            path = (q.parameters or {}).get("path", "") or ""
            if KRU_DIR.lower() in path.lower():
                procs.append((q, path))
            elif path:
                print(f"  [skip] pid {q.pid} is not the KRU client ({path})")
        if not procs:
            print(f"no running KRU client found under {KRU_DIR}.\n"
                  f"launch it and log in, or pass --pid <N> explicitly.")
            return
        print(f"found {len(procs)} KRU client process(es): {[q.pid for q, _ in procs]}")
        for q, path in procs:
            try: instrument(q.pid, f"({path})")
            except Exception as e: print(f"  attach {q.pid} failed:", e)

    print(f"\nappending to {OUT}")
    print("Walk between areas — Buya, Kugnae, the Arctic, a dungeon. Ctrl-C when done.\n")
    try:
        sys.stdin.read()
    except KeyboardInterrupt:
        pass

    print(f"\nstopped. {len(seen)} music packet(s) captured -> {OUT}")
    if seen:
        vals = sorted({(t, g) for _, t, g in seen})
        print("distinct (type, bgm) seen: " + ", ".join(f"type{t}/{g}" for t, g in vals))
        if len({g for _, g in vals}) > 1:
            print("  -> the live server DOES vary music by area (ClassicTK's flat 902 is not the shipped data)")
        else:
            print("  -> only one value seen; walk through more distinct areas before concluding")
    outf.close()


if __name__ == "__main__":
    main()
