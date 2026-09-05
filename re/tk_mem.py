#!/usr/bin/env python
"""
tk_mem.py -- the verified memory reader. Built on `tk_offsets.py`, proven by
`verify_offsets.py` (36/38 on the live client, 2026-08-10).

Two things here are load-bearing:

1. `entities()` DOES THE WHOLE BUCKET WALK IN ONE RPC CALL. Reading 32 slots x 9 fields
   from Python would be ~290 frida round trips per tick, which is far past the control
   loop's budget. The JS walks the linked list, filters to live slots and returns the
   whole roster in a single reply.

2. It uses the LINKED LIST, not the vtable scan. Phase 0 measured a room holding 2 mobs
   where the scan reported 21 "entities" -- freed pool slots keep their vtable, which is
   the phantom-entity bug that once had the bot chasing nothing across a map. The bucket
   walk returns exactly the live set and needs no memory-range guessing.

NPC WARNING, found the hard way in Mignok's home: `type == 3` at +0xF4 does NOT separate
mobs from NPCs -- two NPCs both read 3, and both had empty names, so neither the type
field nor the name field discriminates. The only authoritative source is the `0x07` spawn
packet's class byte ([4]: 2 = player, 5/6 = mob, 12 = NPC), which pool enumeration never
sees. `record_core.py` therefore learns class per LOOK from spawn packets and caches it;
until a look is classified, treat it as UNKNOWN rather than assuming mob.
"""
import time

import frida

import tk_offsets as T

MOD = "NexusTK.exe"

JS = r"""
'use strict';
const __u32 = Process.getModuleByName('user32.dll');
const __PM = new NativeFunction(__u32.getExportByName('PostMessageW'),
                                'int', ['pointer','uint','uint','pointer']);
rpc.exports = {
  ru8:    function(a){ try{ return ptr(a).readU8();  }catch(e){ return null; } },
  ru16:   function(a){ try{ return ptr(a).readU16(); }catch(e){ return null; } },
  ru32:   function(a){ try{ return ptr(a).readU32(); }catch(e){ return null; } },
  ri8:    function(a){ try{ return ptr(a).readS8();  }catch(e){ return null; } },
  ri32:   function(a){ try{ return ptr(a).readS32(); }catch(e){ return null; } },
  wi32:   function(a, v){ try{ ptr(a).writeS32(v); return true; }catch(e){ return false; } },
  rutf16: function(a, n){ try{ return ptr(a).readUtf16String(n); }catch(e){ return null; } },

  // SAME-PROCESS PostMessage. Posting from inside the target sidesteps window focus
  // entirely, which is what lets a swap happen in the background without stealing the
  // desktop. (Externally posted messages proved focus-dependent here.)
  postchar: function(hwnd, vk, ch, shift){
    try{
      const h = ptr(hwnd), up = (1 | (1<<30) | (1<<31)) >>> 0;
      if (shift) __PM(h, 0x0100, 0x10, ptr(1));
      __PM(h, 0x0100, vk, ptr(1));
      if (ch >= 0) __PM(h, 0x0102, ch, ptr(1));
      __PM(h, 0x0101, vk, ptr(up));
      if (shift) __PM(h, 0x0101, 0x10, ptr(up));
      return true;
    }catch(e){ return false; }
  },

  // Self struct: pointer chain + every field in ONE trip.
  selfall: function(pa, o){
    try{
      const r = ptr(pa).readU32(); if (!r || r < 0x100000) return null;
      const p = ptr(r);
      return {x:p.add(o.x).readU32(), y:p.add(o.y).readU32(),
              curhp:p.add(o.curhp).readU32(), maxhp:p.add(o.maxhp).readU32(),
              curmana:p.add(o.curmana).readU32(), maxmana:p.add(o.maxmana).readU32(),
              exp:p.add(o.exp).readU32(), gold:p.add(o.gold).readU32()};
    }catch(e){ return null; }
  },

  // THE HOT PATH: enumerate every live entity in ONE round trip.
  //
  // Two enumerations exist and BOTH are individually wrong:
  //   * the AHK's bucket linked list under-reports -- measured returning 0 slots in a
  //     room where entities demonstrably existed, because live objects are not always
  //     reachable from that head pointer;
  //   * a raw vtable scan over-reports -- freed pool slots keep their vtable, which is
  //     the phantom that once dragged the bot across a map chasing nothing.
  // So: SCAN for existence (reliable), then apply the AHK's own VALIDITY FIELDS for
  // liveness (valid_a non-zero AND valid_b zero, or the player-validity word). That
  // combination is what kills phantoms without losing real entities.
  //
  // `rangeHint` is a LIST of ranges to scan, cached from the last wide pass.
  // IT MUST BE EVERY range that produced a hit, not just the first: entity objects are
  // spread across several heap regions, and caching only the first one silently reduced
  // a 6-entity room to 1 (the one holding our own character) on every cached tick --
  // a roster that looks plausible and is wrong.
  entities: function(vt, delta, off, hpPctOff, rangeHint){
    const out = [];
    const pat = [vt&0xff,(vt>>>8)&0xff,(vt>>>16)&0xff,(vt>>>24)&0xff]
      .map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    let ranges = [];
    try{
      if (rangeHint && rangeHint.length){
        ranges = rangeHint.map(r => ({base: ptr(r[0]), size: r[1]}));
      } else {
        ranges = Process.enumerateRanges('rw-').filter(r => r.size <= 0x4000000);
      }
    }catch(e){ return {ents: out, ranges: []}; }

    const hitRanges = [];
    for (const r of ranges){
      let ms; try{ ms = Memory.scanSync(r.base, r.size, pat); }catch(e){ continue; }
      for (const mm of ms){
        const obj = mm.address;
        if (obj.and(3).toInt32() !== 0) continue;      // vtable pointer is 4-aligned
        const slot = obj.sub(delta);                   // AHK offsets are slot-relative
        try{
          const uid = obj.add(off.uid).readU32();
          const x   = obj.add(off.x).readU32();
          const y   = obj.add(off.y).readU32();
          if (!(uid > 1000 && x > 0 && y > 0 && x < 1000 && y < 1000)) continue;
          // LIVENESS, straight from the AHK's isMobValid: a monster is live when
          // valid_a is set and valid_b is clear; a player uses its own validity word.
          const va = slot.add(off.valid_a).readU32();
          const vb = slot.add(off.valid_b).readU8();
          const vp = slot.add(off.valid_pc).readU32();
          if (!((va && !vb) || vp)) continue;
          // HP BAR IS TRANSIENT. The client only draws a mob health bar as an ON-HIT
          // FLASH, so this pointer is populated for roughly half a second after damage
          // and is null the rest of the time. null therefore means "NO RECENT HIT" --
          // it does NOT mean undamaged, and it does not mean the offset is wrong.
          // Verified: 98 on a mob one tick after a 1-damage Thunder Bolt.
          // Poll at >=10 Hz through a fight or the reading is simply missed.
          let pct = null;
          const bar = slot.add(off.hp_bar).readU32();
          if (bar) { try{ pct = ptr(bar).add(hpPctOff).readU8(); }catch(e){} }
          let nm = ''; try{ nm = slot.add(off.name).readUtf16String(32) || ''; }catch(e){}
          out.push({uid:uid, x:x, y:y,
                    type: obj.add(off.type).readU32(),
                    look: obj.add(off.look).readU16() & 0x7FFF,
                    facing: slot.add(off.facing).readU8(),
                    invis: slot.add(off.invisible).readU8(),
                    targeted: slot.add(off.targeted).readU8(),
                    hp_pct: pct, name: nm, addr: obj.toString()});
          const key = r.base.toString();
          if (!hitRanges.some(h => h[0] === key)) hitRanges.push([key, r.size]);
        }catch(e){}
      }
    }
    return {ents: out, ranges: hitRanges};
  }
};
"""


class TkMem:
    """Verified reads against one client. Construct with a pid, or pass an existing
    frida script's exports via `from_exports` if you already have the bot attached."""

    def __init__(self, pid, session=None, script=None):
        self.pid = pid
        self._own = session is None
        self.session = session or frida.get_local_device().attach(pid)
        self.script = script or self.session.create_script(JS)
        if script is None:
            self.script.load()
        self.ex = self.script.exports_sync
        # slot-relative offsets handed to JS; keeps the map in tk_offsets.py only
        self._off = {k: v[0] for k, v in T.AHK_MOB_SLOT.items()}
        self._off.update({"uid": T.ENT["uid"], "type": T.ENT["type"],
                          "x": T.ENT["x"], "y": T.ENT["y"], "look": T.ENT["look"]})

    # ---------------------------------------------------------------- primitives
    def u8(self, a):  return self.ex.ru8(hex(a))
    def u16(self, a): return self.ex.ru16(hex(a))
    def u32(self, a): return self.ex.ru32(hex(a))
    def i8(self, a):  return self.ex.ri8(hex(a))
    def i32(self, a): return self.ex.ri32(hex(a))
    def utf16(self, a, n=64): return self.ex.rutf16(hex(a), n) or ""

    def chain(self, base_abs, *offsets):
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

    # ------------------------------------------------------------------- reads
    def self_all(self):
        """x/y/hp/mana/exp/gold in one trip. NOTE: level is deliberately absent --
        SELF+0x118 read 73 on a level-2 Peasant, so it is not level. Take level from
        the 0x08 statblock instead."""
        return self.ex.selfall(hex(T.addr(T.SELF_PTR)), T.SELF)

    WIDE_RESCAN_S = 20.0

    def entities(self, force_wide=False):
        """Every LIVE entity. Vtable scan for existence + the AHK validity fields for
        liveness; see the JS for why neither alone is correct.

        The cached range list makes the warm path ~2 ms against ~1.1 s cold, but a cache
        can only ever shrink the search -- so a periodic WIDE rescan is mandatory, not an
        optimisation. Without it a mob allocated into a fresh region stays invisible
        forever and the roster is quietly incomplete.
        """
        hint = getattr(self, "_range_hint", None)
        now = time.time()
        if force_wide or now - getattr(self, "_wide_ts", 0) > self.WIDE_RESCAN_S:
            hint = None
        r = self.ex.entities(T.ENT_VTABLE, T.SLOT_DELTA, self._off, T.HP_BAR_PCT, hint) or {}
        ents = r.get("ents") or []
        if not ents and hint:
            r = self.ex.entities(T.ENT_VTABLE, T.SLOT_DELTA, self._off,
                                 T.HP_BAR_PCT, None) or {}
            ents = r.get("ents") or []
            hint = None
        if hint is None:
            self._wide_ts = now
        if r.get("ranges"):
            self._range_hint = r["ranges"]
        return ents

    def map_id(self):
        rel, deref = T.AHK_STATIC["map_id"][0], T.AHK_STATIC["map_id"][1]
        p = self.chain(T.addr(rel), deref)
        return self.u16(p) if p else None

    def map_name(self):
        rel, deref = T.AHK_STATIC["map_name"][0], T.AHK_STATIC["map_name"][1]
        p = self.chain(T.addr(rel), deref)
        return self.utf16(p, 64) if p else ""

    def self_name(self):
        return self.utf16(T.addr(T.AHK_STATIC["self_name"][0]), 64)

    def buffs(self):
        """Active buff/cooldown tokens. Empty is a valid reading (nothing up)."""
        s = self.utf16(T.addr(T.AHK_STATIC["spell_status"][0]), 1000)
        return [x.strip() for x in s.replace("\r", "").split("\n") if x.strip()]

    def combat_stats(self):
        """ac/dam/hit and might/grace/will straight from memory. Unlike the 0x08 sub
        0x19 packet -- which fires on its own schedule and goes stale right after a gear
        swap -- this is always current, which is exactly what the rotation driver needs
        to confirm a swap actually landed."""
        out = {}
        for name, (root_key, offs, kind) in T.AHK_SELF_EXTRA.items():
            p = self.chain(T.addr(T.AHK_STATIC[root_key][0]), *offs)
            out[name] = (self.i8(p) if kind == "i8" else self.u8(p)) if p else None
        return out

    def group(self):
        p = self.chain(T.addr(T.AHK_STATIC["group_size"][0]), T.AHK_STATIC["group_size"][1])
        size = self.u32(p) if p else 0
        if not size or size > 25:
            return []
        lst = self.u32(T.addr(T.AHK_STATIC["group_list"][0]))
        if not lst:
            return []
        out = []
        for i in range(size):
            b = lst + T.GROUP_STRIDE * i + 0x220
            out.append({"name": self.utf16(b + T.GROUP_MEMBER["name"], 40),
                        "vita": self.u32(b + T.GROUP_MEMBER["vita"]),
                        "max_vita": self.u32(b + T.GROUP_MEMBER["max_vita"]),
                        "mana": self.u32(b + T.GROUP_MEMBER["mana"]),
                        "max_mana": self.u32(b + T.GROUP_MEMBER["max_mana"])})
        return out

    def inventory(self):
        """slot index == the in-game letter. Quantities included, which is what makes
        stack tracking (and 'do I still have the swap item') possible."""
        lst = self.u32(T.addr(T.AHK_STATIC["spell_list"][0]))
        if not lst:
            return []
        out = []
        for i in range(53):
            b = lst + T.INV_BIAS + T.INV_STRIDE * i
            if not self.u8(b + T.INV_FIELD["in_use"]):
                continue
            out.append({"idx": i, "slot": slot_letter(i),
                        "item": self.utf16(b + T.INV_FIELD["item"], 60),
                        "qty": self.u32(b + T.INV_FIELD["qty"])})
        return out

    # ------------------------------------------------------------------ writes
    def set_target(self, uid, which="spell_target"):
        """Deterministic targeting: write the uid, then read it back. Read-back is not
        optional -- a silently failed write means the next cast hits whatever was
        targeted before, and mislabels the row it produces."""
        a = T.addr(T.AHK_STATIC[which][0])
        self.ex.wi32(hex(a), int(uid))
        return self.i32(a) == int(uid)

    # ------------------------------------------------------------------- input
    def attach_window(self, hwnd):
        self.hwnd = int(hwnd)

    NAMED_KEYS = {"enter": (0x0D, 0x0D), "esc": (0x1B, 0x1B), "space": (0x20, 0x20),
                  "tab": (0x09, 0x09)}

    def press(self, ch, shift=False):
        """Post a single key to the client. `ch` is a literal like 'w'/'a', or one of
        the named keys ('enter', 'esc', ...). Named keys are needed because the takeoff
        sequence is `shift+t`, slot letter, Enter."""
        hwnd = getattr(self, "hwnd", None)
        if not hwnd:
            raise RuntimeError("call attach_window(hwnd) first")
        named = self.NAMED_KEYS.get(str(ch).lower())
        if named:
            vk, code = named
        else:
            vk, code = (ord(ch.upper()) if ch.isalpha() else ord(ch)), ord(ch)
        return self.ex.postchar(hex(hwnd), vk, code, bool(shift))

    def close(self):
        if self._own:
            try:
                self.session.detach()
            except Exception:
                pass


def slot_letter(idx, upper_ascii=39, lower_ascii=97):
    """Inventory index -> the letter shown in game (a..z then A..Z)."""
    return chr(idx + upper_ascii) if idx > 25 else chr(idx + lower_ascii)


def find_clients():
    return [p.pid for p in frida.get_local_device().enumerate_processes()
            if p.name.lower() == MOD.lower()]


def open_client(pid=None):
    pids = find_clients()
    if not pids:
        raise RuntimeError(f"{MOD} is not running")
    if pid is None:
        pid = pids[0]
    elif pid not in pids:
        raise RuntimeError(f"pid {pid} is not a live {MOD}")
    return TkMem(pid)
