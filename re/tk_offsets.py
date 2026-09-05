#!/usr/bin/env python
"""
tk_offsets.py -- ONE source of truth for the live client's memory map.

Provenance matters here, so every constant is tagged:

  VERIFIED  - we have independently confirmed it on the live 7.x client.
  AHK       - taken from the `ALL BOTS - 8.10.26` AutoHotkey corpus (a 2020-era
              NexusTK.exe bot suite). NOT yet confirmed on our client.
  DERIVED   - computed at runtime from a VERIFIED anchor.

The reason to trust the AHK table at all: its `STAT_OFFSET := 0x29B4E4` is byte-identical
to the self-struct pointer we independently found and have been using for months, with the
same sub-offsets (x 0xFC, y 0x100, curhp 0x104, ...). One exact match on a 32-bit address
is not luck, so the rest of that table is a strong lead -- but a lead is not a measurement,
which is what `verify_offsets.py` is for.

PHASE 0 RESULT (2026-08-10, live client pid 24852, char "Zalerooooo" in Mignok's home):
**36/38 checks passed and the static table is confirmed.** `verify_offsets.py --all`
reproduces it. Four things were settled by that run and are recorded below:

  1. SLOT_DELTA = 8, proven (not assumed) -- see below.
  2. AHK 0x180 is NOT our uid. Observed uid 12024 where that field read 294926. Our
     uid@+0xF8 and look@+0x178 stand; the AHK's UID field is something else on 7.x.
  3. SELF+0x118 is NOT level on this build. It read 73 on a character with 107 max HP
     and 521 total exp (i.e. about level 3). The `0x08` statblock packet stays the
     authoritative source of level; the memory field is marked suspect below.
  4. The vtable scan OVER-REPORTS. It found 21 "entities" in a room containing 2 mobs,
     while the bucket walk found exactly 2. Freed pool slots keep the vtable, which is
     the mechanism behind the phantom-entity bug that once dragged the bot across a map
     chasing nothing. The linked-list walk is exact and needs no range guessing, so it
     should replace the scan as the primary enumeration.

THE CONFLICT THAT IS NOW RESOLVED. The AHK reads mob fields relative to a *slot* address
from a bucket array; we read them relative to the *object* address that holds the entity
vtable. Those bases differ by a header. Two hints disagreed about its size:

    AHK mob x @ slot+0x104   vs   our x @ obj+0xFC   =>  SLOT_DELTA = 8
    AHK mob UID @ slot+0x180 vs   our uid @ obj+0xF8 =>  SLOT_DELTA = 0x88 (absurd)

The first was right. `verify_offsets.py` swept candidate deltas, found the entity vtable
at slot+8, and then cross-checked: the bucket walk and the vtable scan agreed on (x, y)
for every entity they both saw, 2 agree / 0 disagree. The second hint was simply a
different field. So every AHK mob offset converts to ours by subtracting 8.
"""

# ---------------------------------------------------------------- module base
NX_BASE = 0x400000          # VERIFIED - client has no ASLR, base is constant

# ------------------------------------------------------- self struct (VERIFIED)
# [NX_BASE + SELF_PTR] -> R, then R + offset. In the display/wire coordinate frame.
SELF_PTR = 0x29B4E4
SELF = {
    "x":       0xFC,
    "y":       0x100,
    "curhp":   0x104,
    "maxhp":   0x108,
    "curmana": 0x10C,
    "maxmana": 0x110,
    "exp":     0x114,
    "level":   0x118,   # u16 -- SUSPECT, see docstring item 3. Read 73 on a ~level-3
                        # character. Use the 0x08 statblock (level=[6]) instead.
    "gold":    0x11C,
}
LEVEL_OFF_IS_SUSPECT = True

# OUR OWN FACING (VERIFIED 2026-08-11). It is NOT in the self struct above and it is NOT at
# the entity `facing` offset -- both read a constant 0. It lives under the AHK's
# `direction_root`: [NX_BASE + 0x27A748] + 0x1C5, u8, 0 N / 1 E / 2 S / 3 W.
# Found by turning N/E/S/W and diffing 16 KB of that block: exactly one byte tracked the
# sequence, and a second run in a different order matched 7/7.
#
# Why this matters more than it looks: NexusTK is FACE-THEN-STEP, so without a readable
# facing every turn is a guess. The old code tapped a direction TWICE ("turn, then step")
# and relied on the mob blocking the second tap -- when the mob had moved, that second tap
# STEPPED us, which is exactly the "jousting" the bot did next to a rat. With this offset a
# turn is one verified tap and a step is one unambiguous tap.
SELF_DIR = (0x27A748, 0x1C5)
DIR_NAME = {0: "up", 1: "right", 2: "down", 3: "left"}
DIR_NUM = {v: k for k, v in DIR_NAME.items()}

# ---------------------------------------------------- entity pool (VERIFIED)
ENT_VTABLE  = 0x622F58      # every mob entity object starts with this
SELF_VTABLE = 0x630CB4      # self is a subclass with the same field layout
ENT_STRIDE  = 0x20C         # fixed-stride pool; also the AHK's MOB_SIZE
ENT = {                     # offsets from the OBJECT base (vtable at +0)
    "uid":  0xF8,
    "type": 0xF4,           # 3 = creature, 0 = ground item
    "x":    0xFC,
    "y":    0x100,
    "look": 0x178,          # u16, mask & 0x7FFF
}

# ------------------------------------------------ static table (ALL AHK, unverified)
# Base-relative. Each is a POINTER unless noted; the second value is the offset to apply
# after dereferencing. `None` means the address is used directly.
AHK_STATIC = {
    # name                 base-rel     deref-offset   type      note
    "spell_status":       (0x18E378,    None,          "utf16",  "active buffs, plain string, no pointer chain"),
    "direction_root":     (0x27A748,    None,          "ptr",    "second coord set + facing live here"),
    "group_size":         (0x27A748,    0x3CB0,        "u32",    "0..25"),
    "talk_root":          (0x27A764,    None,          "ptr",    "map id lives under this"),
    "map_id":             (0x27A764,    0x3F2,         "u16",    "numeric map id"),
    "status_1":           (0x27A874,    None,          "ptr",    "ac/dam/hit/subpath/partner"),
    "status_2":           (0x29AE0C,    None,          "ptr",    "might/will/grace bytes"),
    "map_name":           (0x29B4B4,    0xF8,          "utf16",  "room name via pointer"),
    "mob_list":           (0x29B89C,    None,          "ptr",    "head of the bucket linked list"),
    "ground_list":        (0x29B9B4,    None,          "ptr",    "ground items, stride 0x12C, 8/bucket"),
    "self_name":          (0x29BEE0,    None,          "utf16",  "our character name"),
    "spell_target":       (0x29BF20,    None,          "i32",    "WRITE a uid here to target"),
    "v_target":           (0x29BF28,    None,          "i32",    "the AHK spell lab used this one"),
    "tab_target":         (0x29BF2C,    None,          "i32",    ""),
    "group_list":         (0x29BF3C,    None,          "ptr",    "stride 0x12C"),
    "spell_list":         (0x29BF40,    None,          "ptr",    "spells stride 0x148; inventory at -0xAA80"),
}

# self stats that hang off status_1 / status_2 (AHK)
AHK_SELF_EXTRA = {
    "ac":     ("status_1", (0x4, 0x1F14), "i8"),
    "dam":    ("status_1", (0x4, 0x1F15), "i8"),
    "hit":    ("status_1", (0x4, 0x1F16), "i8"),
    # LEVEL (found 2026-08-11): the byte immediately BEFORE might in the same UI stat block.
    # SELF+0x118 was the long-standing guess and is wrong (see docstring item 3). Cross-check
    # that settled it: this read 15 while total exp was 30155, and RTK's LevelExp table puts
    # level 15 at 28469..33269 cumulative -- the only level that bracket admits.
    "level":  ("status_2", (0x280,),      "u8"),
    "might":  ("status_2", (0x281,),      "u8"),
    "grace":  ("status_2", (0x282,),      "u8"),
    "will":   ("status_2", (0x283,),      "u8"),
    # TNL is here too, but it is a PUSHED value: it refreshes when the server sends an 0x08
    # statblock, not as exp accrues, so it goes stale between level-ups (measured: exp moved
    # +211 while this did not move at all). Use it as a level-boundary hint, never as a live
    # "exp remaining" counter.
    "tnl":    ("status_1", (0x4, 0x1F18), "u32"),
}

# ------------------------------------------------- mob fields, SLOT-relative (AHK)
# Convert to object-relative by subtracting the SLOT_DELTA that verify_offsets.py measures.
AHK_MOB_SLOT = {
    "x":          (0x104, "i32"),
    "y":          (0x108, "i32"),
    "draw_x":     (0x10C, "i32"),
    "draw_y":     (0x110, "i32"),
    "name":       (0x12E, "utf16"),   # EMPTY for monsters -- that is the isPlayer test
    "spell_root": (0x178, "ptr"),     # -> +0xC count, +0x10 array of ptrs -> +0x148 graphic id
    "invisible":  (0x19E, "u8"),      # == 2 means invisible
    "uid":        (0x180, "i32"),     # CONFLICTS with our look@0x178 -- see module docstring
    "facing":     (0x1C9, "u8"),      # 0 up, 1 right, 2 down, 3 left
    "valid_a":    (0x174, "u32"),     # mob is valid if valid_a and not valid_b
    "valid_b":    (0x1D4, "u8"),
    "hp_bar":     (0x1E0, "ptr"),     # NULL => undamaged; else deref + HP_BAR_PCT
    "valid_pc":   (0x1E8, "u32"),     # player validity
    "targeted":   (0x1EC, "u8"),
}
HP_BAR_PCT = 0x12C          # UChar percent, read from the dereferenced hp_bar pointer

# VERIFIED 2026-08-10: the entity vtable sits at slot+8, and the bucket walk agrees with
# the vtable scan on (x, y) for every entity both saw. Object offset = slot offset - 8.
SLOT_DELTA = 8


def mob_obj_off(field):
    """AHK slot-relative mob offset -> our object-relative offset."""
    return T_AHK_MOB_SLOT_LOOKUP[field] - SLOT_DELTA


T_AHK_MOB_SLOT_LOOKUP = {k: v[0] for k, v in AHK_MOB_SLOT.items()}

# --------------------------------------------------------------- other strides
GROUP_STRIDE = 0x12C
GROUP_MEMBER = {            # relative to member base = [group_list] + i*stride + 0x220
    "name":     0x000,      # utf16
    "max_vita": 0x118,
    "vita":     0x11C,
    "max_mana": 0x120,
    "mana":     0x124,
}
INV_STRIDE = 0x1FC
INV_BIAS   = -0xAA80        # inventory array starts this far from [spell_list]
INV_FIELD  = {"in_use": 0x00, "uid": 0x02, "label": 0x06,
              "item": 0xA6, "owner": 0x146, "qty": 0x1E8}
SPELL_STRIDE = 0x148
SPELL_FIELD  = {"name": 0x08, "action": 0xA8}
GROUND_STRIDE = 0x12C
GROUND_PER_BUCKET = 8
MOBS_PER_BUCKET = 32

# ------------------------------------------------------------- server pacing
# Measured by the AHK corpus across two independent bot classes; treat as hard limits.
CAST_MIN_MS = 334           # 3 casts/sec sustained cap
ITEM_MIN_MS = 180


def addr(base_rel):
    """Absolute address of a base-relative offset."""
    return NX_BASE + base_rel
