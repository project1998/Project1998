#!/usr/bin/env python
"""
analyze_hit.py -- what can actually be identified from attempts.csv, and what cannot.

The point of this script is NOT to print a formula. It is to answer the prior question:
does the data we collected separate the variables we care about? A logistic fit will
happily return coefficients for a design matrix of rank 3 pretending to be rank 6, and
those coefficients are arbitrary -- any of infinitely many splits of the same fitted
values. So we check conditioning FIRST and only report contrasts that survive.

A contrast is reported only when it varies ONE factor with everything else held fixed:
same mob, same level, same weapon, same everything but the factor under test. That is a
controlled experiment; a marginal average across mixed conditions is not.
"""
import csv
import os
import sys
import math
import collections
import itertools

D = os.path.dirname(os.path.abspath(__file__))
P = os.path.join(D, "auto", "attempts.csv")

# The factors a swing's outcome could plausibly depend on. `gear` stands in for the weapon
# and the whole worn vector; it is the label we actually trust (see ATTEMPT_COLS).
FACTORS = ["level", "might", "grace", "will", "hit_stat", "ac", "dam", "gear", "mob"]


def wilson(k, n, z=1.96):
    """Wilson score interval -- honest at small n and at rates near 0 or 1, unlike the
    normal approximation, which happily produces negative lower bounds on 3/3."""
    if n == 0:
        return (0.0, 0.0, 0.0)
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (p, max(0.0, c - h), min(1.0, c + h))


def two_prop(k1, n1, k2, n2):
    """z for the difference of two proportions, and whether it clears 95%."""
    if not n1 or not n2:
        return 0.0, False
    p1, p2 = k1 / n1, k2 / n2
    p = (k1 + k2) / (n1 + n2)
    se = math.sqrt(p * (1 - p) * (1 / n1 + 1 / n2))
    if se == 0:
        return 0.0, False
    z = (p1 - p2) / se
    return z, abs(z) >= 1.96


def load():
    if not os.path.exists(P):
        sys.exit(f"no {P}")
    rows = []
    for r in csv.DictReader(open(P, encoding="utf-8")):
        if r.get("hit") not in ("0", "1"):
            continue
        if not r.get("mob"):
            continue                       # unidentified mob -> cannot be a controlled cell
        rows.append(r)
    return rows


def report_conditioning(rows):
    """How many DISTINCT combinations of each factor pair exist? If two factors move
    together in every row, no amount of data separates them."""
    print("=" * 78)
    print("CONDITIONING -- can these factors be told apart at all?")
    print("=" * 78)
    for a, b in itertools.combinations(FACTORS, 2):
        va = {r[a] for r in rows if r[a] != ""}
        vb = {r[b] for r in rows if r[b] != ""}
        if len(va) < 2 or len(vb) < 2:
            continue
        pairs = {(r[a], r[b]) for r in rows if r[a] != "" and r[b] != ""}
        # If #pairs == max(#a, #b) the two are locked together (a bijection or a fan-out),
        # i.e. knowing one tells you the other -> their effects are not separable.
        locked = len(pairs) <= max(len(va), len(vb))
        if locked:
            print(f"  CONFOUNDED: {a}({len(va)}) x {b}({len(vb)}) -> only {len(pairs)} "
                  f"combos seen; their effects cannot be separated")
    print()


def controlled_contrasts(rows, factor):
    """Every pair of cells that differ ONLY in `factor`. Everything else is held fixed."""
    others = [f for f in FACTORS if f != factor]
    cells = collections.defaultdict(lambda: [0, 0])          # key -> [hits, n]
    for r in rows:
        key = (tuple(r[o] for o in others), r[factor])
        cells[key][0] += int(r["hit"])
        cells[key][1] += 1
    by_ctx = collections.defaultdict(dict)
    for (ctx, val), (k, n) in cells.items():
        by_ctx[ctx][val] = (k, n)
    out = []
    for ctx, vals in by_ctx.items():
        if len(vals) < 2:
            continue
        for v1, v2 in itertools.combinations(sorted(vals), 2):
            k1, n1 = vals[v1]
            k2, n2 = vals[v2]
            if n1 < 25 or n2 < 25:                # too thin to say anything
                continue
            z, sig = two_prop(k1, n1, k2, n2)
            out.append((factor, ctx, others, v1, k1, n1, v2, k2, n2, z, sig))
    return out


def main():
    rows = load()
    print(f"{len(rows)} usable attempts "
          f"({sum(int(r['hit']) for r in rows)} hits, "
          f"{sum(int(r['hit']) for r in rows) / max(1, len(rows)):.1%})\n")

    report_conditioning(rows)

    print("=" * 78)
    print("CONTROLLED CONTRASTS -- one factor varied, everything else identical")
    print("=" * 78)
    any_found = False
    for f in ("hit_stat", "ac", "level", "dam", "gear", "mob"):
        cs = controlled_contrasts(rows, f)
        if not cs:
            continue
        print(f"\n--- {f} ---")
        for (_f, ctx, others, v1, k1, n1, v2, k2, n2, z, sig) in sorted(
                cs, key=lambda c: -min(c[5], c[8]))[:8]:
            any_found = True
            p1, lo1, hi1 = wilson(k1, n1)
            p2, lo2, hi2 = wilson(k2, n2)
            held = ", ".join(f"{o}={ctx[i]}" for i, o in enumerate(others)
                             if o in ("mob", "level", "gear") and ctx[i])
            print(f"  {f}={v1:<6} {k1:4d}/{n1:<5d} {p1:6.1%} [{lo1:.1%},{hi1:.1%}]   vs   "
                  f"{f}={v2:<6} {k2:4d}/{n2:<5d} {p2:6.1%} [{lo2:.1%},{hi2:.1%}]")
            print(f"      delta {p2 - p1:+.1%}  z={z:+.2f}  "
                  f"{'SIGNIFICANT' if sig else 'not significant'}")
            print(f"      held: {held}")
    if not any_found:
        print("\n  NONE. Every factor moves together with the others in this dataset --")
        print("  there is no controlled comparison anywhere in it.")

    print()
    print("=" * 78)
    print("PER-MOB BASELINE (the one thing a single condition CAN measure)")
    print("=" * 78)
    per = collections.defaultdict(lambda: [0, 0])
    for r in rows:
        per[(r["mob"], r["level"], r["hit_stat"], r["gear"].split("|")[0])][0] += int(r["hit"])
        per[(r["mob"], r["level"], r["hit_stat"], r["gear"].split("|")[0])][1] += 1
    print(f"{'mob':<16}{'lvl':>4}{'hit':>5}  {'weapon':<16}{'n':>6}  hit rate [95% CI]")
    for (mob, lvl, hs, wep), (k, n) in sorted(per.items(), key=lambda kv: -kv[1][1]):
        if n < 30:
            continue
        p, lo, hi = wilson(k, n)
        print(f"{mob[:15]:<16}{lvl:>4}{hs:>5}  {wep[:15]:<16}{n:>6}  "
              f"{p:6.1%} [{lo:.1%}, {hi:.1%}]")


if __name__ == "__main__":
    main()
