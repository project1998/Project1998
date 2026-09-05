#!/usr/bin/env python
"""
verify_offsets.py -- PHASE 0. Confirm the AHK-derived memory map on the live client.

Everything in docs/BOT-DATA-PLAN.md is gated on this. The AHK corpus gave us a static
offset table whose self-struct anchor matches ours exactly, which makes the rest of it a
strong lead -- but a lead that has never been read on our client. This script reads every
one of them and reports pass/fail with the observed value, so the plan proceeds on measured
ground or not at all.

It is READ-ONLY. It attaches with a minimal script (no decrypt hook, no send hook, no
interceptors), so it is safe to run alongside the bot or during ordinary play.

    python re/verify_offsets.py                 # verify the first client found
    python re/verify_offsets.py --pid 23668     # a specific client
    python re/verify_offsets.py --all           # every running client
    python re/verify_offsets.py --json out.json # machine-readable result

THE INTERESTING PART is `resolve_slot_delta`. Two bases exist for the same entity objects:

    slot   = bucket + i*0x20C          (how the AHK walks them)
    object = address holding ENT_VTABLE (how we find them, by scanning)

The AHK's mob field offsets are slot-relative and ours are object-relative, so they differ
by a constant header we have never measured. Rather than assume it, we sweep candidate
deltas, keep the one that puts the entity vtable at slot+delta, and then CROSS-CHECK the
resulting (uid, x, y) against the vtable-scan enumeration we already trust. If the two
enumerations agree on the same entities, the delta is proven and every other AHK mob offset
becomes usable by subtraction.
"""
import argparse, json, os, sys, time

import frida

import tk_offsets as T

MOD = "NexusTK.exe"

# Minimal read-only surface. Deliberately NOT the bot's JS: that one installs an
# Interceptor on the decrypt routine and captures the connection object, which we do
# not want as a side effect of a diagnostic.
JS = r"""
'use strict';
rpc.exports = {
  ru8:    function(a){ try{ return ptr(a).readU8();  }catch(e){ return null; } },
  ru16:   function(a){ try{ return ptr(a).readU16(); }catch(e){ return null; } },
  ru32:   function(a){ try{ return ptr(a).readU32(); }catch(e){ return null; } },
  ri8:    function(a){ try{ return ptr(a).readS8();  }catch(e){ return null; } },
  ri32:   function(a){ try{ return ptr(a).readS32(); }catch(e){ return null; } },
  rutf16: function(a, n){ try{ return ptr(a).readUtf16String(n); }catch(e){ return null; } },
  // Is this address inside a readable mapping? Distinguishes "offset is wrong" from
  // "offset is right but the game has not populated it yet" -- a null pointer at a
  // correct address is a PASS-with-caveat, garbage at a wrong one is a fail.
  readable: function(a){
    try{ const r = Process.findRangeByAddress(ptr(a));
         return r ? r.protection : null; }catch(e){ return null; }
  },
  // The enumeration we already trust: scan for the entity vtable in the pool region.
  enument: function(vt, lo, hi){
    const out = [];
    try{
      const pat = [vt&0xff,(vt>>>8)&0xff,(vt>>>16)&0xff,(vt>>>24)&0xff]
        .map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
      const ms = Memory.scanSync(ptr(lo), hi - lo, pat);
      for (const m of ms){
        const a = m.address;
        if (a.and(3).toInt32() !== 0) continue;
        try{
          const uid = a.add(0xF8).readU32();
          const x = a.add(0xFC).readU32(), y = a.add(0x100).readU32();
          if (uid > 1000 && x > 0 && y > 0 && x < 1000 && y < 1000)
            out.push([a.toString(), uid, x, y]);
        }catch(e){}
      }
    }catch(e){}
    return out;
  },
  // Scan EVERY rw- range for the entity vtable, not just the first one that matches.
  // The first run of this script found 0 pool entities while the bucket walk found 2 --
  // because entity objects were living in a later range than the first hit, and a
  // "first match wins" range probe never looked there. That is a real defect in the
  // vtable-scan enumeration the bot uses today, and it is why the linked-list walk is
  // the better primitive: it is exact and needs no range guessing at all.
  enumall: function(vt){
    const out = [];
    const pat = [vt&0xff,(vt>>>8)&0xff,(vt>>>16)&0xff,(vt>>>24)&0xff]
      .map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    let rs; try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of rs){
      if (r.size > 0x4000000) continue;
      let ms; try{ ms = Memory.scanSync(r.base, r.size, pat); }catch(e){ continue; }
      for (const mm of ms){
        const a = mm.address;
        if (a.and(3).toInt32() !== 0) continue;
        try{
          const uid = a.add(0xF8).readU32();
          const x = a.add(0xFC).readU32(), y = a.add(0x100).readU32();
          if (uid > 1000 && x > 0 && y > 0 && x < 1000 && y < 1000)
            out.push([a.toString(), uid, x, y]);
        }catch(e){}
        if (out.length >= 256) return out;
      }
    }
    return out;
  }
};
"""


# --------------------------------------------------------------------- plumbing
class Mem:
    """Thin typed accessor over the frida exports, with pointer-chain support."""

    def __init__(self, ex):
        self.ex = ex

    def u8(self, a):   return self.ex.ru8(hex(a))
    def u16(self, a):  return self.ex.ru16(hex(a))
    def u32(self, a):  return self.ex.ru32(hex(a))
    def i8(self, a):   return self.ex.ri8(hex(a))
    def i32(self, a):  return self.ex.ri32(hex(a))
    def utf16(self, a, n=64):
        s = self.ex.rutf16(hex(a), n)
        return s if s else ""
    def readable(self, a): return self.ex.readable(hex(a))

    def chain(self, base_abs, *offsets):
        """[base] + off0, dereferencing between each offset. Returns None on a null link."""
        cur = self.u32(base_abs)
        if not cur:
            return None
        for i, off in enumerate(offsets):
            if i == len(offsets) - 1:
                return cur + off
            cur = self.u32(cur + off)
            if not cur:
                return None
        return cur


def result(name, ok, value, note=""):
    return {"name": name, "ok": bool(ok), "value": value, "note": note}


# --------------------------------------------------------------- the checks
def check_anchor(m):
    """The one offset we already trust. If THIS fails, the client changed and every
    other result below is meaningless -- so it gates the whole run."""
    out = []
    root = m.u32(T.addr(T.SELF_PTR))
    if not root or root < 0x100000:
        return [result("self_ptr (ANCHOR)", False, root,
                       "self struct pointer is null - is a character logged in?")], None
    out.append(result("self_ptr (ANCHOR)", True, hex(root), ""))

    vals = {k: m.u32(root + o) for k, o in T.SELF.items() if k != "level"}
    vals["level"] = m.u16(root + T.SELF["level"])
    ok_hp = bool(vals["maxhp"]) and 0 < vals["curhp"] <= vals["maxhp"] * 1.5
    ok_lv = vals["level"] and 1 <= vals["level"] <= 99
    ok_xy = all(0 < vals[k] < 1000 for k in ("x", "y"))
    out.append(result("self.x,y", ok_xy, (vals["x"], vals["y"])))
    out.append(result("self.hp", ok_hp, f'{vals["curhp"]}/{vals["maxhp"]}'))
    out.append(result("self.mana", True, f'{vals["curmana"]}/{vals["maxmana"]}'))
    # A range predicate alone is too weak to catch a WRONG offset -- any small integer
    # passes "1..99". Cross-check it against HP, which cannot lie: a level-70 character
    # does not have 107 max HP. This caught a real problem on the first run.
    implausible = ok_lv and vals["level"] > 20 and vals["maxhp"] < 300
    out.append(result("self.level", ok_lv and not implausible, vals["level"],
                      "IMPLAUSIBLE vs maxhp - +0x118 may not be level on this build"
                      if implausible else ""))
    out.append(result("self.exp", vals["exp"] >= 0, vals["exp"]))
    return out, vals


def check_statics(m, map_ids):
    """Every entry in the AHK static table, with a plausibility predicate per type."""
    out = []
    for name, (rel, deref, kind, note) in sorted(T.AHK_STATIC.items(), key=lambda kv: kv[1][0]):
        a = T.addr(rel)
        if kind == "utf16" and deref is None:
            # An EMPTY buff string is the correct reading for a character with no buffs up,
            # so emptiness cannot be the failure test -- readability is. Distinguishing the
            # two is what stops a legitimately-idle character from looking like a bad offset.
            s = m.utf16(a, 64)
            prot = m.readable(a)
            ok = prot is not None and (s == "" or s.isprintable())
            out.append(result(name, ok, repr(s[:40]) if s else "(empty)",
                              note + ("" if s else " [readable, nothing active]")))
        elif kind == "utf16":
            p = m.chain(a, deref)
            s = m.utf16(p, 64) if p else ""
            out.append(result(name, bool(s), repr(s[:40]), note))
        elif kind == "ptr":
            v = m.u32(a)
            prot = m.readable(v) if v else None
            out.append(result(name, bool(v and prot), hex(v) if v else None,
                              note + (f" [{prot}]" if prot else " [null or unmapped]")))
        elif kind == "u16":
            p = m.chain(a, deref) if deref is not None else a
            v = m.u16(p) if p else None
            ok = v is not None and (not map_ids or v in map_ids)
            out.append(result(name, ok, v,
                              note + ("" if ok else " (not a known map id)")))
        elif kind == "u32":
            p = m.chain(a, deref) if deref is not None else a
            v = m.u32(p) if p else None
            out.append(result(name, v is not None and v <= 25, v, note))
        elif kind == "i32":
            v = m.i32(a)
            out.append(result(name, v is not None, v, note))
    return out


def check_self_extra(m):
    """ac/dam/hit and might/grace/will read through the status pointers."""
    out = []
    for name, (root_key, offs, kind) in T.AHK_SELF_EXTRA.items():
        rel = T.AHK_STATIC[root_key][0]
        p = m.chain(T.addr(rel), *offs)
        if p is None:
            out.append(result(f"self.{name}", False, None, "pointer chain broke"))
            continue
        v = m.i8(p) if kind == "i8" else m.u8(p)
        ok = v is not None and (-128 <= v <= 127 if kind == "i8" else 0 <= v <= 255)
        out.append(result(f"self.{name}", ok, v, f"via {root_key}"))
    return out


def walk_buckets(m, head_rel, stride, per_bucket, max_buckets=24):
    """The AHK's mob/ground enumeration: a linked list of buckets, each holding
    `per_bucket` fixed-stride slots, with the NEXT bucket pointer in the first word."""
    head = m.u32(T.addr(head_rel))
    if not head:
        return [], []
    buckets, seen = [head], {head}
    cur = head
    for _ in range(max_buckets):
        nxt = m.u32(cur)
        if not nxt or nxt in seen:
            break
        buckets.append(nxt)
        seen.add(nxt)
        cur = nxt
    slots = [b + stride * i for b in buckets for i in range(per_bucket)]
    return buckets, slots


def resolve_slot_delta(m, slots, pool):
    """THE question this script exists to answer.

    Sweep candidate header sizes; the right delta is the one that puts the entity vtable
    at slot+delta for many slots at once. Then cross-check against the vtable-scan
    enumeration: if the bucket walk and the scan describe the same entities, the AHK's
    slot-relative offsets are usable on our client by simple subtraction.
    """
    votes = {}
    for d in range(0, 64, 4):
        n = sum(1 for s in slots[:96] if m.u32(s + d) == T.ENT_VTABLE)
        if n:
            votes[d] = n
    if not votes:
        return None, votes, []
    delta = max(votes, key=votes.get)

    pool_by_addr = {int(a, 16): (uid, x, y) for (a, uid, x, y) in pool}
    agree, disagree = [], []
    for s in slots:
        obj = s + delta
        if obj in pool_by_addr:
            uid, x, y = pool_by_addr[obj]
            sx = m.i32(s + T.AHK_MOB_SLOT["x"][0])
            sy = m.i32(s + T.AHK_MOB_SLOT["y"][0])
            (agree if (sx, sy) == (x, y) else disagree).append((obj, (x, y), (sx, sy)))
    return delta, votes, (agree, disagree)


def check_mob_fields(m, slots, delta, limit=8):
    """Read every AHK mob field on live slots and judge it. `hp_bar` is the one that
    matters most -- it is the whole reason mob HP stopped being interval-censored."""
    rows = []
    for s in slots:
        if m.u32(s + delta) != T.ENT_VTABLE:
            continue
        obj = s + delta
        uid = m.u32(obj + T.ENT["uid"])
        x, y = m.u32(obj + T.ENT["x"]), m.u32(obj + T.ENT["y"])
        if not (uid > 1000 and 0 < x < 1000 and 0 < y < 1000):
            continue
        etype = m.u32(obj + T.ENT["type"])
        look = m.u16(obj + T.ENT["look"])
        look = (look & 0x7FFF) if look is not None else None

        bar_ptr = m.u32(s + T.AHK_MOB_SLOT["hp_bar"][0])
        pct = m.u8(bar_ptr + T.HP_BAR_PCT) if bar_ptr else None
        rows.append({
            "slot": hex(s), "obj": hex(obj), "uid": uid, "xy": (x, y),
            "type": etype, "look": look,
            "name": m.utf16(s + T.AHK_MOB_SLOT["name"][0], 40),
            "facing": m.u8(s + T.AHK_MOB_SLOT["facing"][0]),
            "invisible": m.u8(s + T.AHK_MOB_SLOT["invisible"][0]),
            "targeted": m.u8(s + T.AHK_MOB_SLOT["targeted"][0]),
            "valid_a": m.u32(s + T.AHK_MOB_SLOT["valid_a"][0]),
            "valid_b": m.u8(s + T.AHK_MOB_SLOT["valid_b"][0]),
            "ahk_uid_field": m.i32(s + T.AHK_MOB_SLOT["uid"][0]),
            "hp_bar_ptr": hex(bar_ptr) if bar_ptr else None,
            "hp_pct": pct,
        })
        if len(rows) >= limit:
            break
    return rows


def judge_mob_fields(rows):
    """Turn the raw per-entity reads into pass/fail verdicts on each field."""
    out = []
    if not rows:
        return [result("mob fields", False, None, "no live entities to read - stand near mobs")]

    facings = [r["facing"] for r in rows if r["facing"] is not None]
    out.append(result("mob.facing", bool(facings) and all(0 <= f <= 3 for f in facings),
                      facings, "expect 0..3"))

    bars = [(r["uid"], r["hp_pct"]) for r in rows]
    have = [p for _, p in bars if p is not None]
    ok = bool(have) and all(0 <= p <= 100 for p in have)
    note = "NULL bar = undamaged (expected); a 100 reading is 'not yet allocated', not full HP"
    out.append(result("mob.hp_bar -> pct", ok or not have, bars, note))

    named = [r["name"] for r in rows if r["name"]]
    out.append(result("mob.name", True, named or "(all empty)",
                      "empty == monster is CORRECT; non-empty should only be players/NPCs"))

    tgt = [r["targeted"] for r in rows if r["targeted"] is not None]
    out.append(result("mob.targeted", all(t in (0, 1) for t in tgt), tgt, "expect 0/1"))

    # The documented conflict, settled by observation.
    mism = [(r["uid"], r["ahk_uid_field"]) for r in rows]
    same = all(a == b for a, b in mism)
    out.append(result("mob.uid @ AHK 0x180", same, mism[:4],
                      "matches our uid@+0xF8" if same else
                      "DIFFERENT field on our client - keep using uid@+0xF8 and look@+0x178"))
    return out


def check_group(m):
    p = m.chain(T.addr(T.AHK_STATIC["group_size"][0]), T.AHK_STATIC["group_size"][1])
    size = m.u32(p) if p else None
    if not size:
        return [result("group", True, 0, "not grouped - re-run in a group to verify members")]
    lst = m.u32(T.addr(T.AHK_STATIC["group_list"][0]))
    names = []
    for i in range(min(size, 12)):
        base = lst + T.GROUP_STRIDE * i + 0x220
        names.append((m.utf16(base + T.GROUP_MEMBER["name"], 40),
                      m.u32(base + T.GROUP_MEMBER["vita"]),
                      m.u32(base + T.GROUP_MEMBER["max_vita"])))
    return [result("group.members", all(n for n, _, _ in names), names, f"size={size}")]


def check_inventory(m, limit=6):
    lst = m.u32(T.addr(T.AHK_STATIC["spell_list"][0]))
    if not lst:
        return [result("inventory", False, None, "spell_list pointer is null")]
    items = []
    for i in range(53):
        base = lst + T.INV_BIAS + T.INV_STRIDE * i
        if not m.u8(base + T.INV_FIELD["in_use"]):
            continue
        items.append((i, m.utf16(base + T.INV_FIELD["item"], 60),
                      m.u32(base + T.INV_FIELD["qty"])))
        if len(items) >= limit:
            break
    return [result("inventory", bool(items), items,
                   "index == the in-game slot letter; qty enables stack tracking")]


def load_map_ids():
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                     "..", "data", "game-data", "map_index.csv")
    ids = set()
    try:
        import csv
        with open(p, encoding="utf-8") as f:
            for row in csv.DictReader(f):
                for k in ("id", "map_id", "mapid", "Id"):
                    if k in row and str(row[k]).strip().isdigit():
                        ids.add(int(row[k]))
                        break
    except Exception:
        pass
    return ids


# --------------------------------------------------------------------- driver
def verify(pid, map_ids):
    sess = frida.get_local_device().attach(pid)
    try:
        sc = sess.create_script(JS)
        sc.load()
        ex = sc.exports_sync
        m = Mem(ex)

        report = {"pid": pid, "ts": time.time(), "sections": {}}

        anchor, self_vals = check_anchor(m)
        report["sections"]["anchor"] = anchor
        if not all(r["ok"] for r in anchor[:1]):
            report["fatal"] = "anchor failed - nothing below is meaningful"
            return report

        report["sections"]["static table"] = check_statics(m, map_ids)
        report["sections"]["self extras"] = check_self_extra(m)

        pool = ex.enumall(T.ENT_VTABLE)
        report["pool_entities"] = len(pool)

        buckets, slots = walk_buckets(m, T.AHK_STATIC["mob_list"][0],
                                      T.ENT_STRIDE, T.MOBS_PER_BUCKET)
        delta, votes, cross = resolve_slot_delta(m, slots, pool) if slots else (None, {}, [])
        agree, disagree = cross if cross else ([], [])
        report["sections"]["mob list"] = [
            result("mob_list buckets", bool(buckets), len(buckets),
                   "linked list walked from the head pointer"),
            result("slot delta", delta is not None, delta,
                   f"vtable-at-slot+delta votes={votes}"),
            result("bucket walk vs vtable scan", bool(agree) and not disagree,
                   f"{len(agree)} agree / {len(disagree)} disagree",
                   "both enumerations describing the same entities proves the delta"),
        ]

        if delta is not None:
            rows = check_mob_fields(m, slots, delta)
            report["mob_sample"] = rows
            report["sections"]["mob fields"] = judge_mob_fields(rows)

        report["sections"]["group"] = check_group(m)
        report["sections"]["inventory"] = check_inventory(m)
        return report
    finally:
        try:
            sess.detach()
        except Exception:
            pass


def render(report):
    print(f"\n{'='*74}\n  PID {report['pid']}\n{'='*74}")
    if report.get("fatal"):
        print(f"  FATAL: {report['fatal']}")
    total = passed = 0
    for section, rows in report["sections"].items():
        print(f"\n-- {section} " + "-" * (68 - len(section)))
        for r in rows:
            total += 1
            passed += r["ok"]
            mark = "PASS" if r["ok"] else "FAIL"
            val = r["value"]
            if isinstance(val, (list, tuple)) and len(str(val)) > 46:
                val = str(val)[:43] + "..."
            print(f"  [{mark}] {r['name']:<28} {str(val):<24} {r['note'][:60]}")
    if report.get("mob_sample"):
        print(f"\n-- live entity sample " + "-" * 52)
        for r in report["mob_sample"][:6]:
            print(f"  uid={r['uid']:<9} {str(r['xy']):<12} look={str(r['look']):<6} "
                  f"type={r['type']} face={r['facing']} hp%={r['hp_pct']} "
                  f"name={r['name'][:18]!r}")
    print(f"\n  {passed}/{total} checks passed   "
          f"(pool entities seen: {report.get('pool_entities', 0)})\n")
    return passed, total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid", type=int)
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--json")
    a = ap.parse_args()

    pids = []
    if a.pid:
        pids = [a.pid]
    else:
        for p in frida.get_local_device().enumerate_processes():
            if p.name.lower() == MOD.lower():
                pids.append(p.pid)
        if not pids:
            print(f"no {MOD} process found - start the client and log a character in")
            return 2
        if not a.all:
            pids = pids[:1]

    map_ids = load_map_ids()
    reports = []
    for pid in pids:
        try:
            r = verify(pid, map_ids)
        except Exception as e:
            print(f"pid {pid}: attach/verify failed: {e}")
            continue
        reports.append(r)
        render(r)

    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(reports, f, indent=2, default=str)
        print(f"wrote {a.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
