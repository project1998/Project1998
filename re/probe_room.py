#!/usr/bin/env python
"""One-shot: what does the client ACTUALLY say the current room is?

Reads the room name via the fixed pointer chain (not the ambiguous string harvest)
and the numeric map id, so we can tell a stale heap string from the truth.
"""
import sys, time
import nexus_bot as NB
import nexus_agent as NA
from tk_offsets import NX_BASE

PID = int(sys.argv[sys.argv.index("--pid") + 1]) if "--pid" in sys.argv else None

agent = NA.Agent()
world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent), pid=PID)
ex = sc.exports_sync
try:
    time.sleep(1.0)

    def ptr_chain(base_rel, off):
        p = ex.ru32(hex(NX_BASE + base_rel))
        if not p:
            return None, None
        return p, p + off

    # map_name : [0x29B4B4] + 0xF8, utf16
    p, a = ptr_chain(0x29B4B4, 0xF8)
    name = ex.rutf16(hex(a), 40) if a else None
    # map_id : [0x27A764] + 0x3F2, u16
    mp, ma = ptr_chain(0x27A764, 0x3F2)
    mid = ex.ru16(hex(ma)) if ma else None
    # self name (static utf16, used directly)
    sname = ex.rutf16(hex(NX_BASE + 0x29BEE0), 40)
    # self pos via the self-struct pointer
    r = ex.ru32(hex(NX_BASE + 0x29B4E4))
    x = ex.ru32(hex(r + 0xFC)) if r else None
    y = ex.ru32(hex(r + 0x100)) if r else None

    print(f"self_name       = {sname!r}")
    print(f"self_pos        = ({x},{y})")
    print(f"map_name(PTR)   = {name!r}     <- fixed pointer chain, authoritative")
    print(f"map_id(PTR)     = {mid}        <- 41=Mythic Nexus, 44=Mythic Gateway, 201=Waters1, 220=Kugnae")

    # And what the ambiguous harvest picks, for comparison:
    try:
        got = ex.utf16strings(4, 400000, 0, 0)
        from tk_offsets import NX_BASE as _b  # noqa
        names = NB.load_map_names()
        matches = [(addr, sv) for addr, sv in got if sv in names]
        print(f"harvest matches = {len(matches)} known-map strings in memory:")
        for addr, sv in matches[:12]:
            print(f"    {addr} = {sv!r}")
    except Exception as e:
        print(f"harvest failed: {e!r}")
finally:
    try:
        sc.unload()
    except Exception:
        pass
    try:
        s.detach()
    except Exception:
        pass
