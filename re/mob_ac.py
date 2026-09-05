#!/usr/bin/env python
"""
mob_ac.py -- read a mob's AC off a single fixed-damage spell cast.

Spark does no random roll. Its damage is a pure function of the CASTER's level and the
TARGET's AC, so one cast at a mob is a direct measurement of that mob's AC -- the one
defensive number that is otherwise unobservable (melee damage mixes AC with the attack
roll, the damage roll and the mob's HP, none of which we know independently).

    dmg = floor( (50 + level/2) × (1 + ac/100) )

`level/2` is a REAL half, not integer division: at level 15 the base is 57.5, and rounding
it to 57 would predict 105 for AC 85 instead of the observed 106.

Calibration (self-cast across a known AC sweep), TWO caster levels:
    level 15, base 57.5          level 16, base 58.0
    AC 85 -> 106                 AC 85 -> 107
    AC 84 -> 105                 AC 73 -> 100
    AC 75 -> 100                 AC 67 ->  96
    AC 70 ->  97
7/7 exact. Higher AC takes MORE spell damage -- in NexusTK a lower AC is the better one.
The second level is what proves the `level/2` term rather than assuming it: the same AC 85
target moved 106 -> 107 across one level, which is the +0.5 base doing exactly what the
formula says. It also rules out any level term other than a half-point per level.

WHY A SECOND CAST RARELY BREAKS THE TIE. Intersecting two caster levels sounds like it
should pin the AC down, and sometimes it does -- but separating AC 85 from AC 86 requires an
integer to fall between B×1.85 and B×1.86, a window of only 0.01×B (0.58 damage at base 58).
So it works for roughly half of ambiguous pairs and never for the rest. ±1 AC is the honest
resolution of a single fixed-damage spell at these levels; do not pretend otherwise by
averaging the interval and quoting a decimal.

RESOLUTION. Because the result is floored, a damage number maps to an INTERVAL of AC, not
a point: ac ∈ [100(d/B − 1), 100((d+1)/B − 1)). The interval is 100/B wide, so at level 15
(B = 57.5) it spans ~1.7 AC -- typically two candidate integers. It narrows as the caster
levels (B grows), and two casts at different caster levels intersect to a single value.
So we always store the RAW damage and the caster level alongside the derived bounds: if the
law is ever refined, every measurement we ever took can be re-derived from disk.
"""
import csv
import math
import os

D = os.path.dirname(os.path.abspath(__file__))
P_MOB_AC = os.path.join(D, "auto", "mob_ac.csv")

# `color` is NOT decoration -- look alone is not a species. mobs.csv keys every mob
# on MobLook + MobLookColor, and look 90 alone covers Mouse, Rat, Vile rat and
# Killer rat. Two of them stood in the same room during the first live probe.
COLS = ["ts", "spell", "look", "color", "mob", "zone", "caster_level", "dmg",
        "ac_lo", "ac_hi", "ac_mid", "base"]

# Fixed-damage spells and their base at level L. Only Spark is calibrated; Thunder Bolt is
# flat 1 damage regardless of anything, so it measures nothing (it is a pull, not a probe).
SPELLS = {
    "spark": lambda level: 50 + level / 2.0,
}


def base_of(spell, level):
    f = SPELLS.get(spell.lower())
    return f(level) if f else None


def predict(spell, level, ac):
    """Damage this spell would do to a target with `ac`. The forward direction, used to
    check the law against any new observation before trusting an inversion."""
    b = base_of(spell, level)
    if b is None:
        return None
    return int(math.floor(b * (1 + ac / 100.0)))


def infer_ac(spell, level, dmg):
    """(ac_lo, ac_hi) -- the half-open interval of ACs consistent with this damage, or None.

    Half-open on the right: an AC of exactly ac_hi would floor to dmg+1.
    """
    b = base_of(spell, level)
    if not b or dmg is None or dmg <= 0:
        return None
    lo = 100.0 * (dmg / b - 1.0)
    hi = 100.0 * ((dmg + 1) / b - 1.0)
    return (lo, hi)


def candidates(spell, level, dmg):
    """The integer ACs consistent with this damage -- usually one or two."""
    iv = infer_ac(spell, level, dmg)
    if iv is None:
        return []
    lo, hi = iv
    return [a for a in range(int(math.floor(lo)) - 1, int(math.ceil(hi)) + 2)
            if predict(spell, level, a) == dmg]


def record(ts, spell, look, color, mob, zone, level, dmg):
    """Append one measurement. Returns the row, or None if the damage is unusable."""
    iv = infer_ac(spell, level, dmg)
    if iv is None:
        return None
    lo, hi = iv
    row = {"ts": int(ts), "spell": spell, "look": look if look is not None else "",
           "color": color if color is not None else "",
           "mob": mob or "", "zone": zone or "", "caster_level": level, "dmg": dmg,
           "ac_lo": round(lo, 2), "ac_hi": round(hi, 2), "ac_mid": round((lo + hi) / 2, 2),
           "base": base_of(spell, level)}
    new = not os.path.exists(P_MOB_AC)
    os.makedirs(os.path.dirname(P_MOB_AC), exist_ok=True)
    with open(P_MOB_AC, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=COLS, extrasaction="ignore")
        if new:
            w.writeheader()
        w.writerow(row)
    return row


def solve():
    """Intersect every measurement per look. Casts at DIFFERENT caster levels have different
    interval widths and offsets, so their intersection pins the AC down far tighter than any
    single cast -- which is the whole reason for storing raw damage per level."""
    if not os.path.exists(P_MOB_AC):
        return {}
    per = {}
    for r in csv.DictReader(open(P_MOB_AC, encoding="utf-8")):
        try:
            lvl, dmg = int(r["caster_level"]), int(r["dmg"])
        except (ValueError, KeyError):
            continue
        key = (r.get("look", ""), r.get("color", ""))
        if not key[0]:
            continue
        cs = set(candidates(r["spell"], lvl, dmg))
        e = per.setdefault(key, {"look": r["look"], "color": r.get("color", ""),
                                 "mob": r["mob"], "n": 0, "acs": None,
                                 "levels": set()})
        e["n"] += 1
        e["levels"].add(lvl)
        e["acs"] = cs if e["acs"] is None else (e["acs"] & cs)
    return per


def _selftest():
    """The calibration points are the test: if a change to the law breaks them, it is wrong."""
    pts = [(15, 85, 106), (15, 84, 105), (15, 75, 100), (15, 70, 97),
           (16, 85, 107), (16, 73, 100), (16, 67, 96)]
    for lvl, ac, want in pts:
        got = predict("spark", lvl, ac)
        assert got == want, f"spark L{lvl} AC {ac}: predicted {got}, measured {want}"
        assert ac in candidates("spark", lvl, want), f"AC {ac} not recovered from {want}"
    print(f"law check: {len(pts)}/{len(pts)} calibration points exact across "
          f"levels {sorted({p[0] for p in pts})}, all invertible")


if __name__ == "__main__":
    _selftest()
    print(f"\nlevel-15 Spark resolution: {100 / base_of('spark', 15):.2f} AC per damage point")
    for d in (97, 100, 105, 106):
        print(f"  dmg {d:4d} -> AC {candidates('spark', 15, d)}")
    res = solve()
    if res:
        print(f"\n{'look':>6}{'col':>5} {'mob':<18}{'n':>3} {'levels':<10} AC")
        for k, e in sorted(res.items()):
            acs = sorted(e["acs"] or [])
            print(f"{e['look'] or '?':>6}{e['color'] or '?':>5} {e['mob'][:17]:<18}{e['n']:>3} "
                  f"{','.join(map(str, sorted(e['levels']))):<10} "
                  f"{acs if len(acs) <= 4 else f'{acs[0]}..{acs[-1]}'}")
    else:
        print("\nno measurements yet")
