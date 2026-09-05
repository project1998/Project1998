#!/usr/bin/env python
"""
gear_rotate.py -- the rotation driver (docs/BOT-DATA-PLAN.md §5b).

Swapping gear during ordinary fighting makes our AC, hit and grace vary continuously.
That is BETTER experimental design than a blocked sweep, not merely cheaper: blocking
confounds the swept variable with everything that drifts between blocks (level, zone, mob
mix, session), while randomising inside one fight stream removes that confound and lets
the attacker terms be fitted jointly instead of one at a time.

HOW THE SWAP ACTUALLY WORKS -- measured live 2026-08-10, and not what we assumed:

  * `w` + letter WEARS the item in that inventory letter.
  * The previously worn item of that slot is displaced into the **first free bag slot**,
    which is generally NOT the letter you pressed.
  * The whole inventory array then REFLOWS, so letters are not stable identifiers across
    a swap. Tracking anything by letter across two reads is a bug.

Consequences, both of which cost us a wrong result before they were understood:

  1. Verification must be by ITEM NAME over the whole bag (a multiset diff), never by
     watching one letter. The first version watched a letter and reported a successful
     swap as a failure -- then skipped its restore, leaving a ring equipped.
  2. **Every measured delta is a PAIRWISE DIFFERENCE (worn_new - worn_old), not an
     absolute bonus.** Equipping an Axe over a Staff of power measured will -1; that is
     the Axe minus the Staff, not the Axe. Absolutes are recoverable only by chaining
     differences back to a measurement taken with the slot EMPTY. `absolute_vectors()`
     does that and reports which items remain floating.

  3. The stat vector is NOT a swap detector. A Purple ring equipped correctly and moved
     zero stats. Zero bonus is a legitimate measurement; a keypress that never landed is
     the failure, and only the inventory can tell them apart.

FOUR RULES, enforced here rather than documented and hoped for:
  1. Swap only at ENGAGEMENT BOUNDARIES, so one record carries one stat vector.
  2. VERIFY every swap by name-diff, and always attempt the restore.
  3. The set must be FULL RANK over the predictors of interest (`refuse_reason`).
  4. SAFETY FLOOR: never strip below a minimum AC/HP without a healer.

    python re/gear_rotate.py --list
    python re/gear_rotate.py --probe --only "Axe,Novice sword"
    python re/gear_rotate.py --rank
"""
import argparse, json, os, random, re, sys, time
from collections import Counter

import tk_mem

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "auto")
os.makedirs(OUT, exist_ok=True)
P_EDGES = os.path.join(OUT, "gear_edges.jsonl")     # pairwise (new - old) measurements
P_VECTORS = os.path.join(OUT, "item_vectors.json")  # absolutes, where solvable

PREDICTORS = ["ac", "dam", "hit", "might", "grace", "will"]

SWAP_KEY = "w"
SWAP_SETTLE_S = 0.9     # the array reflow + server ack needs longer than a stat read
MIN_AC_FLOOR = -50
MIN_HP_FRAC = 0.55

# NEVER press `w` on these. A quantity filter is not enough: a single Rice wine passes
# "qty == 1" and `w` on a consumable may consume it. Deny-by-default; --auto to widen.
CONSUMABLE_WORDS = (
    "wine", "pipe", "food", "meat", "acorn", "scroll", "potion", "elixir", "herb",
    "tools", "ink", "book", "pelt", "antler", "fish", "bread", "water", "kindred",
    "envelope", "gem", "essence", "seed", "flower", "ore",
)
EMPTY = "(empty)"       # the pseudo-item meaning "that gear slot was bare"

# Equipment slot codes for the `shift+t`, code, Enter takeoff. These are their OWN
# namespace and have nothing to do with inventory letters. Complete map, user-supplied
# and confirmed live (`w` removed a Staff of power; the rest read empty on that mage).
EQUIP_SLOTS = {
    "w": "weapon",
    "a": "armor",
    "s": "shield",
    "h": "helm",
    "l": "left ring",
    "r": "right ring",
}


def looks_consumable(name):
    """Word-boundary match, not substring: substring rejected 'Wooden saber' for
    containing 'wood'."""
    n = (name or "").lower()
    return any(re.search(rf"\b{w}\b", n) for w in CONSUMABLE_WORDS)


def load_json(p, d):
    try:
        with open(p, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return d


def rank_of(rows, cols):
    """Rank of the design matrix by Gaussian elimination -- no numpy dependency. Rank <
    len(cols) means some linear combination of predictors is unmoved by the entire set,
    and any coefficient along it is unfittable no matter how many samples we take."""
    m = [[float(r.get(c, 0) or 0) for c in cols] for r in rows]
    rank, piv_row = 0, 0
    for c in range(len(cols)):
        piv = next((r for r in range(piv_row, len(m)) if abs(m[r][c]) > 1e-9), None)
        if piv is None:
            continue
        m[piv_row], m[piv] = m[piv], m[piv_row]
        pv = m[piv_row][c]
        for r in range(len(m)):
            if r != piv_row and abs(m[r][c]) > 1e-9:
                f = m[r][c] / pv
                for k in range(c, len(cols)):
                    m[r][k] -= f * m[piv_row][k]
        rank += 1
        piv_row += 1
    return rank


class GearRotator:
    def __init__(self, mem, log=print):
        self.mem = mem
        self.log = log
        self.edges = [json.loads(l) for l in open(P_EDGES, encoding="utf-8")] \
            if os.path.exists(P_EDGES) else []
        self.allow_all = False
        self.last_swap = 0.0

    # ------------------------------------------------------------------ state
    def stats(self):
        return {k: v for k, v in self.mem.combat_stats().items() if k in PREDICTORS}

    # ---- settle discipline -------------------------------------------------
    # ac/dam/hit do not update instantly on a gear change; they lag behind might/grace/
    # will. Reading a fixed sleep after the keypress is a race, and losing it is SILENT:
    # the first loadout audit reported Summer garb as {} when it is actually -9 AC,
    # because each slot's "before" was still settling from the previous slot's re-wear.
    # So: never sample a raw stat vector around an action. Wait for quiet before, and
    # wait for movement (or a real timeout) after.

    def stable_stats(self, quiet=0.7, timeout=4.0):
        """Poll until the vector holds still for `quiet` seconds."""
        t0 = time.time()
        last, since = self.stats(), time.time()
        while time.time() - t0 < timeout:
            time.sleep(0.15)
            cur = self.stats()
            if cur != last:
                last, since = cur, time.time()
            elif time.time() - since >= quiet:
                return cur
        return self.stats()

    def changed_stats(self, before, settle=0.7, timeout=6.0):
        """Wait for the vector to move, then for it to hold still. Returns (vector,
        moved). `moved=False` after the full timeout is a genuine zero-bonus reading --
        which is a real measurement, not a failure."""
        t0 = time.time()
        while time.time() - t0 < timeout:
            if self.stats() != before:
                return self.stable_stats(quiet=settle, timeout=timeout), True
            time.sleep(0.15)
        return self.stats(), False

    def bag(self):
        """item name -> current letter. Read FRESH every time; letters reflow."""
        out = {}
        for i in self.mem.inventory():
            if i["item"] and i["item"] not in out:
                out[i["item"]] = i["slot"]
        return out

    def bag_counts(self):
        return Counter(i["item"] for i in self.mem.inventory() if i["item"])

    def swappable(self):
        out = []
        for it in self.mem.inventory():
            if (it["qty"] or 0) > 1 or not it["item"]:
                continue
            if looks_consumable(it["item"]) and not self.allow_all:
                continue
            out.append(it)
        return out

    # ------------------------------------------------------------------ gating
    def can_swap(self, engaged, healer_present=False):
        if engaged:
            return False, "mid-engagement: a swap here would straddle one record"
        me = self.mem.self_all()
        if not me or not me["maxhp"]:
            return False, "cannot read vitals"
        if me["curhp"] / me["maxhp"] < MIN_HP_FRAC and not healer_present:
            return False, f"hp {me['curhp']}/{me['maxhp']} below floor, no healer"
        st = self.stats()
        if st.get("ac") is not None and st["ac"] < MIN_AC_FLOOR and not healer_present:
            return False, f"ac {st['ac']} at floor, no healer"
        return True, ""

    # ------------------------------------------------------------------- swap
    def wear(self, item_name):
        """Wear `item_name` from the bag. Returns (ok, delta, info).

        `delta` is (new_worn - old_worn) for that gear slot -- a DIFFERENCE, not an
        absolute bonus. `info['displaced']` names what came off (or EMPTY if the slot
        was bare, which is the anchor that makes absolutes solvable).
        """
        before_counts = self.bag_counts()
        letter = self.bag().get(item_name)
        if not letter:
            return False, {}, {"note": f"{item_name!r} is not in the bag"}
        before = self.stable_stats()

        self.mem.press(SWAP_KEY)
        time.sleep(0.12)
        self.mem.press(letter)
        time.sleep(SWAP_SETTLE_S)

        after_counts = self.bag_counts()
        after, _ = self.changed_stats(before)
        delta = {k: (after.get(k) or 0) - (before.get(k) or 0) for k in PREDICTORS}

        if after_counts.get(item_name, 0) >= before_counts.get(item_name, 0):
            return False, delta, {"note": "item still in bag -- keypress did not land"}

        gained = [n for n in after_counts if after_counts[n] > before_counts.get(n, 0)]
        displaced = gained[0] if gained else EMPTY
        self.last_swap = time.time()
        return True, delta, {
            "displaced": displaced,
            "note": "zero stat difference (still a valid arm)" if not any(delta.values()) else "",
            # Restoring means wearing back whatever came off. If nothing came off the
            # slot was empty and `w` cannot undo it -- that arm is ONE-WAY.
            "restorable": displaced != EMPTY,
        }

    def takeoff(self, slot_code):
        """Remove the item worn in equipment slot `slot_code` via `shift+t`, code, Enter.

        THIS IS THE ANCHOR. `wear` only ever yields (new - old) differences, so a set of
        items swapped among themselves is solvable only up to an additive constant. A
        takeoff measures (empty - X) = -vec[X] against a genuinely empty slot, which pins
        that constant and turns every connected item's vector absolute. It is also the
        only way to reverse a swap that went into a bare slot -- `w` wears, it cannot
        unequip.

        Slot codes are in EQUIP_SLOTS -- their own namespace, unrelated to inventory
        letters. A code that returns nothing means the slot is EMPTY, not that the code
        is wrong (a/s/h all read empty on a mage wearing only a weapon).
        """
        before_counts = self.bag_counts()
        before = self.stable_stats()
        self.mem.press("t", shift=True)
        time.sleep(0.15)
        self.mem.press(slot_code)
        time.sleep(0.15)
        self.mem.press("enter")
        time.sleep(SWAP_SETTLE_S)
        # The takeoff dialog stays up and SWALLOWS THE NEXT KEYPRESS -- measured: a wear
        # issued straight after a takeoff silently did nothing, leaving the character
        # unarmed. Dismiss it before returning so the next action lands.
        self.mem.press("esc")
        time.sleep(0.25)
        self.mem.press("esc")
        time.sleep(0.25)

        after_counts = self.bag_counts()
        after, _ = self.changed_stats(before)
        delta = {k: (after.get(k) or 0) - (before.get(k) or 0) for k in PREDICTORS}
        gained = [n for n in after_counts if after_counts[n] > before_counts.get(n, 0)]
        if not gained:
            return False, {}, {"note": f"nothing arrived in the bag -- slot {slot_code!r} "
                                       f"may be empty or the code may be wrong"}
        item = gained[0]
        # delta here is (empty - item), so the item's own vector is its negation.
        return True, delta, {"item": item,
                             "vector": {k: -v for k, v in delta.items()}}

    def audit_loadout(self, slots=None, log=print):
        """Take off every equipment slot in turn, measure the absolute vector of whatever
        comes off, and put it straight back.

        Two things fall out that we could not get any other way:

          * THE WORN SET, enumerated from memory alone. Worn gear is not in the inventory
            array, so until now identifying it needed a `0x39` profile request (which only
            fires when the profile is opened). Taking an item off makes it appear in the
            bag, where we can read it.
          * An ABSOLUTE vector per worn item, because each removal is measured against a
            genuinely empty slot rather than against another item.

        Each slot is restored immediately, so the loadout is unchanged at the end -- but
        a failed re-wear is reported loudly rather than swallowed, because leaving the
        character stripped is exactly how this went wrong before.
        """
        found = {}
        for code in (slots or EQUIP_SLOTS):
            name = EQUIP_SLOTS.get(code, code)
            ok, delta, info = self.takeoff(code)
            if not ok:
                log(f"  {code} {name:<11} (empty)")
                continue
            item, vec = info["item"], info["vector"]
            self.record(EMPTY, item, delta)
            found[code] = {"item": item, "vector": {k: v for k, v in vec.items() if v}}
            back_ok, _, back_info = self.wear(item)
            log(f"  {code} {name:<11} {item:<22} "
                f"{found[code]['vector'] or '{}'}"
                f"{'' if back_ok else '  *** RE-WEAR FAILED: ' + str(back_info.get('note'))}")
        return found

    def record(self, new_item, old_item, delta):
        """One pairwise edge: wearing `new_item` in place of `old_item` moved stats by
        `delta`. Edges compose; absolutes fall out only if the graph reaches EMPTY."""
        e = {"ts": time.time(), "new": new_item, "old": old_item, "delta": delta}
        self.edges.append(e)
        with open(P_EDGES, "a", encoding="utf-8") as f:
            f.write(json.dumps(e) + "\n")

    # -------------------------------------------------- absolutes from the chain
    def absolute_vectors(self):
        """Solve item bonuses from the pairwise edges.

        Each edge says vec[new] - vec[old] = delta, with vec[EMPTY] = 0. That is a graph
        where every component containing EMPTY is fully determined and every other
        component is known only up to an additive constant. We report both, because a
        floating component is still usable for *relative* work and pretending otherwise
        would invent numbers.
        """
        adj = {}
        for e in self.edges:
            adj.setdefault(e["new"], []).append((e["old"], {k: -v for k, v in e["delta"].items()}))
            adj.setdefault(e["old"], []).append((e["new"], e["delta"]))
        vec, floating, seen = {}, [], set()
        for root in [EMPTY] + sorted(adj):
            if root in seen or root not in adj and root != EMPTY:
                continue
            if root not in adj:
                continue
            anchored = root == EMPTY
            stack, comp = [(root, {k: 0 for k in PREDICTORS})], {}
            while stack:
                node, v = stack.pop()
                if node in comp:
                    continue
                comp[node] = v
                seen.add(node)
                for nxt, d in adj.get(node, []):
                    if nxt not in comp:
                        stack.append((nxt, {k: v[k] + (d.get(k) or 0) for k in PREDICTORS}))
            comp.pop(EMPTY, None)
            if anchored:
                vec.update(comp)
            else:
                floating.append(sorted(comp))
        return vec, floating

    # -------------------------------------------------------------- rule 3
    def rank_report(self):
        rows = [e["delta"] for e in self.edges]
        moved = [c for c in PREDICTORS if any(abs(r.get(c, 0) or 0) > 0 for r in rows)]
        r = rank_of(rows, moved) if rows and moved else 0
        return {"edges": len(rows), "predictors_moved": moved, "rank": r,
                "full_rank": bool(moved) and r == len(moved)}

    def entangled_with(self, p):
        """Predictors whose column is an exact scalar multiple of `p`'s across every
        measured swap -- i.e. perfectly collinear with it, and therefore impossible to
        tell apart no matter how many samples are collected."""
        rows = [e["delta"] for e in self.edges]
        col = [float(r.get(p, 0) or 0) for r in rows]
        if not any(col):
            return []
        out = []
        for q in PREDICTORS:
            if q == p:
                continue
            oc = [float(r.get(q, 0) or 0) for r in rows]
            if not any(oc):
                continue
            k = next((b / a for a, b in zip(col, oc) if a), None)
            if k is not None and all(abs(b - k * a) < 1e-9 for a, b in zip(col, oc)):
                out.append(q)
        return out

    def refuse_reason(self, want=("ac", "hit")):
        rep = self.rank_report()
        if not rep["edges"]:
            return "no swaps measured yet -- run --probe over your gear first"
        missing = [w for w in want if w not in rep["predictors_moved"]]
        if missing:
            return (f"nothing in the set moves {', '.join(missing)} -- that coefficient "
                    f"is unfittable, find gear that grants it")
        for w in want:
            ent = self.entangled_with(w)
            if ent:
                return (f"{w!r} is perfectly collinear with {', '.join(ent)} across every "
                        f"measured swap -- their coefficients cannot be told apart. Add an "
                        f"item that moves {w} WITHOUT {ent[0]} (or the reverse).")
        if not rep["full_rank"]:
            return (f"set is RANK DEFICIENT ({rep['rank']} of "
                    f"{len(rep['predictors_moved'])}): some predictors only ever move "
                    f"together. The sweep targets are separable, but a joint fit over all "
                    f"of {rep['predictors_moved']} is not -- restrict the model or add gear.")
        return ""

    # ---------------------------------------------------------------- driving
    def pick(self):
        """Prefer items never yet worn -- that is the bootstrap -- then randomise. A
        fixed cycle would correlate the arm with time, reintroducing the very confound
        rotation exists to remove."""
        cands = self.swappable()
        if not cands:
            return None
        known = {e["new"] for e in self.edges}
        unknown = [c for c in cands if c["item"] not in known]
        return random.choice(unknown or cands)

    def step(self, engaged=False, healer_present=False):
        ok, why = self.can_swap(engaged, healer_present)
        if not ok:
            return None, why
        it = self.pick()
        if not it:
            return None, "nothing swappable in the bag"
        ok, delta, info = self.wear(it["item"])
        if not ok:
            return None, f"{it['item']}: {info.get('note')}"
        self.record(it["item"], info["displaced"], delta)
        return it["item"], (f"wore {it['item']} over {info['displaced']} {delta} "
                            f"{info.get('note', '')}").strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid", type=int)
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--rank", action="store_true")
    ap.add_argument("--probe", action="store_true")
    ap.add_argument("--only", default="", help="comma-separated item names (safest)")
    ap.add_argument("--takeoff", default="",
                    help="equipment slot code(s) to unequip, e.g. 'l' or 'l,r'. "
                         "w=weapon a=armor s=shield h=helm l=left ring r=right ring. "
                         "Each yields an ABSOLUTE vector.")
    ap.add_argument("--audit", action="store_true",
                    help="take off every slot, measure its absolute vector, re-wear it. "
                         "Enumerates the worn loadout without a 0x39 profile request.")
    ap.add_argument("--auto", action="store_true",
                    help="lift the consumable deny-list -- can DESTROY items")
    a = ap.parse_args()

    mem = tk_mem.open_client(a.pid)
    try:
        from bot_input_test import find_windows
        # (hwnd, title, pid, image) -- match on pid. Binding the wrong window would drive
        # input into the OTHER client while reading this one's memory.
        wins = [w for w in find_windows() if w[2] == mem.pid]
        if not wins:
            raise RuntimeError(f"no visible window owned by pid {mem.pid}")
        mem.attach_window(wins[0][0])
        print(f"window {wins[0][0]} bound to pid {mem.pid}")
    except Exception as e:
        print(f"(no window bound: {e}) -- --probe disabled, reads still work")

    rot = GearRotator(mem)
    rot.allow_all = a.auto
    only = [s.strip() for s in a.only.split(",") if s.strip()]
    print(f"{mem.self_name()!r}  stats={rot.stats()}")

    if a.list or not (a.rank or a.probe):
        print("\nswappable (letters reflow after every swap -- these are a snapshot):")
        for it in rot.swappable():
            print(f"  [{it['slot']}] {it['item']}")

    if a.audit:
        print("\nloadout audit (each slot removed, measured, and put back):")
        rot.audit_loadout()

    for code in [c.strip() for c in a.takeoff.split(",") if c.strip()]:
        print(f"  taking off slot {code!r} ...")
        ok, delta, info = rot.takeoff(code)
        if not ok:
            print(f"    FAILED: {info.get('note')}")
            continue
        # Recorded as an edge against EMPTY, which is what anchors the whole graph.
        rot.record(EMPTY, info["item"], delta)
        print(f"    removed {info['item']!r} -> absolute vector "
              f"{({p: n for p, n in info['vector'].items() if n}) or '{} (no stat bonus)'}")

    if a.probe:
        worn_chain = []
        for name in (only or [it["item"] for it in rot.swappable()]):
            ok, why = rot.can_swap(engaged=False)
            if not ok:
                print(f"  refusing: {why}")
                break
            print(f"  wearing {name!r} ...")
            ok, delta, info = rot.wear(name)
            if not ok:
                print(f"    FAILED: {info.get('note')}")
                continue
            rot.record(name, info["displaced"], delta)
            print(f"    over {info['displaced']!r}: {delta}  {info.get('note','')}".rstrip())
            worn_chain.append((name, info["displaced"], info["restorable"]))

        # Always try to put the original loadout back, newest swap first.
        for name, displaced, restorable in reversed(worn_chain):
            if not restorable:
                print(f"    NOTE: {name!r} went into an empty slot -- `w` cannot remove it. "
                      f"Unequip in game if you want it off.")
                continue
            ok, _, info = rot.wear(displaced)
            print(f"    restored {displaced!r}" if ok
                  else f"    WARNING: restore of {displaced!r} failed: {info.get('note')}")

    rep = rot.rank_report()
    print(f"\nrank: {rep['rank']} over {rep['predictors_moved']} ({rep['edges']} swaps)"
          f"{'  FULL RANK' if rep['full_rank'] else ''}")
    vec, floating = rot.absolute_vectors()
    if vec:
        print("absolute vectors (anchored to an empty slot):")
        for k, v in sorted(vec.items()):
            print(f"  {k:<24} {({p: n for p, n in v.items() if n}) or '{}'}")
        with open(P_VECTORS, "w", encoding="utf-8") as f:
            json.dump(vec, f, indent=1, sort_keys=True)
    for comp in floating:
        print(f"relative-only group (no empty-slot anchor yet): {comp}")
    print(f"verdict: {rot.refuse_reason() or 'set can separate ac and hit -- good to rotate'}")
    mem.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
