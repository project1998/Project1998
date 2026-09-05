#!/usr/bin/env python
"""
tk_map.py -- exact terrain for a NexusTK map, from the shipped `.map` files.

Until now the bot learned walls by BUMPING INTO THEM: every failed step was evidence, three
failures carved a wall, and A* routed on that slowly-accumulating guess. That is why it
orbited mobs it could have walked straight to, and why a swallowed keypress could invent a
wall out of open floor and strand it.

None of that is necessary. The terrain is a file.

FORMAT (headerless, 4 bytes per cell, row-major, y*xs + x):

    [ground u16 LE][object u16 LE]

  ground低14 bits = tile graphic; ground >> 14 = the 2-bit PASSABILITY flag.
  Cell is walkable iff pass == 0. (0 and 3 are the only values that occur on real maps.)

THE OBJECT LAYER IS NOT COLLISION. It looks like it should be -- SObj.tbl even carries
per-object direction flags -- but it is height/draw-order, not passability. Map authors bake
every wall footprint into the ground pass flag instead (verified server-side: objects
1519-1522, the heaviest wall pieces with ~2000 placements each, sit on pass=3 ground 100% of
the time, while plenty of 0x0F-flagged objects sit on fully walkable ground). Treating obj!=0
as solid is what made a character "stuck on shadows" -- shadows and rugs are objects on good
ground. RTK's own `map_canmove()` agrees: its object test is commented out.

Dimensions come from data/game-data/map_index.csv; the file itself carries none, and a wrong
xs silently SHEARS the map (every row offset by one), so the size check below is load-bearing.

Doors are the one moving part: a closed door is pass=3 in the file and opens at runtime. So
the file is the BASELINE, and anything the bot learns by bumping stays as an overlay on top.
"""
import os
import struct

_HERE = os.path.dirname(os.path.abspath(__file__))

# The .map files live with the archival server. Prefer a local copy if one is ever made, so
# this tree can stand alone, but do not duplicate 9.7 MB / 1751 files just to read two of them.
MAP_DIRS = [
    os.path.join(_HERE, "..", "data", "game-data", "maps"),
    os.path.join(os.path.expanduser("~"), "Desktop", "NexusServer", "game-data", "maps"),
]

_cache = {}


def map_path(map_id):
    """Locate TK<id>.map. Both naming conventions occur: TK370.map and TK000370.map."""
    names = (f"TK{int(map_id)}.map", f"TK{int(map_id):06d}.map")
    for d in MAP_DIRS:
        for n in names:
            p = os.path.join(d, n)
            if os.path.isfile(p):
                return p
    return None


class TkMap:
    """Terrain for one map. `walkable(x, y)` is the whole point."""

    def __init__(self, map_id, xs, ys, ground, obj):
        self.id, self.xs, self.ys = int(map_id), int(xs), int(ys)
        self.ground, self.obj = ground, obj

    def pass_flag(self, x, y):
        return self.ground[y * self.xs + x] >> 14

    def tile(self, x, y):
        return self.ground[y * self.xs + x] & 0x3FFF

    def object_at(self, x, y):
        return self.obj[y * self.xs + x]

    def in_bounds(self, x, y):
        return 0 <= x < self.xs and 0 <= y < self.ys

    def walkable(self, x, y):
        if not self.in_bounds(x, y):
            return False
        return (self.ground[y * self.xs + x] >> 14) == 0

    def grid(self):
        """{(x, y): 1 walkable / 0 wall} for the whole map -- the World grid's baseline."""
        g = {}
        for y in range(self.ys):
            row = y * self.xs
            for x in range(self.xs):
                g[(x, y)] = 1 if (self.ground[row + x] >> 14) == 0 else 0
        return g

    def stats(self):
        walk = sum(1 for v in self.ground if (v >> 14) == 0)
        return {"id": self.id, "xs": self.xs, "ys": self.ys,
                "cells": self.xs * self.ys, "walkable": walk,
                "blocked": self.xs * self.ys - walk}


def load(map_id, xs, ys):
    """Load TK<map_id>.map, or None if the file is missing or the wrong size for xs*ys.

    The size check is not paranoia: the format is headerless, so a mismatched xs would load
    happily and hand back a map sheared by one tile per row -- worse than no map at all,
    because the bot would trust it.
    """
    key = (int(map_id), int(xs), int(ys))
    if key in _cache:
        return _cache[key]
    p = map_path(map_id)
    if p is None:
        _cache[key] = None
        return None
    try:
        with open(p, "rb") as f:
            raw = f.read()
    except OSError:
        _cache[key] = None
        return None
    need = int(xs) * int(ys) * 4
    if len(raw) != need:
        _cache[key] = None
        return None
    n = int(xs) * int(ys)
    words = struct.unpack("<%dH" % (n * 2), raw)
    ground = words[0::2]
    obj = words[1::2]
    m = TkMap(map_id, xs, ys, ground, obj)
    _cache[key] = m
    return m


if __name__ == "__main__":
    import sys
    import csv
    idx = os.path.join(_HERE, "..", "data", "game-data", "map_index.csv")
    want = [int(a) for a in sys.argv[1:]] or None
    with open(idx, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            mid = int(r["id"])
            if want and mid not in want:
                continue
            m = load(mid, int(r["xs"]), int(r["ys"]))
            if m is None:
                print(f"{mid:6d} {r['name'][:24]:24s}  -- no usable .map "
                      f"({r['xs']}x{r['ys']})")
                continue
            s = m.stats()
            print(f"{mid:6d} {r['name'][:24]:24s}  {s['xs']:3d}x{s['ys']:<3d} "
                  f"walkable {s['walkable']:6d}/{s['cells']:<6d} "
                  f"({s['walkable'] / s['cells']:5.1%})")
