#!/usr/bin/env python
"""
run_session.py -- the experiment runner. Core telemetry + gear rotation, together.

This is what docs/BOT-DATA-PLAN.md §4 calls Phase 4: not a bot loop but a *protocol
driver*. A session declares its intent, refuses to start if the variable it claims to
sweep cannot actually move, and then records continuously while rotating gear so that
every engagement is stamped with the loadout and stat vector that produced it.

    python re/run_session.py --minutes 30 --sweep ac
    python re/run_session.py --minutes 60 --sweep ac,hit --swap-every 90
    python re/run_session.py --minutes 20 --no-swap        # pure observation (S1/S2)

THREE THINGS IT GETS RIGHT THAT A NAIVE LOOP WOULD NOT:

1. SWAPS ONLY AT ENGAGEMENT BOUNDARIES. A swap mid-fight would put two AC values inside
   one engagement record and make every incoming hit ambiguous. `can_swap` refuses while
   anything is engaged.

2. THE STAT VECTOR IS CACHED, NOT POLLED. ac/dam/hit settle a beat behind a gear change,
   so reading them correctly means waiting for quiet -- which is fine right after a swap
   and unacceptable at the start of every engagement. Gear is the only thing that moves
   them, so we re-read once per swap and stamp the cached vector on every record until
   the next one. (Level-ups also move it, so we refresh on a slow timer as a backstop.)

3. IT REFUSES A SESSION IT CANNOT ANSWER. If you say --sweep hit and nothing in the bag
   grants hit -- or hit only ever moves together with ac -- the run stops before wasting
   an hour producing a dataset in which that coefficient is unfittable. That check is the
   whole reason the rank guard exists.

The loadout label is tracked INCREMENTALLY: one audit at the start establishes ground
truth (worn gear is not in the inventory array, so it has to be discovered by taking each
slot off), and thereafter every swap updates the set from what went on and what came off.
Re-auditing per swap would cost ~25s of takeoff/re-wear churn each time.
"""
import argparse, json, os, sys, time

import tk_mem
import record_core as RC
from gear_rotate import GearRotator, EQUIP_SLOTS, PREDICTORS

P_SESSION = os.path.join(RC.OUT, "sessions.jsonl")


class Loadout:
    """The worn set, tracked incrementally so we never pay for a re-audit."""

    def __init__(self, rot):
        self.rot = rot
        self.worn = set()
        self.slots = {}

    def audit(self, log=print):
        log("auditing loadout (each slot off, measured, back on) ...")
        found = self.rot.audit_loadout(log=log)
        self.slots = {c: v["item"] for c, v in found.items()}
        self.worn = set(self.slots.values())
        return self.worn

    def applied(self, new_item, displaced):
        """Update after a verified swap: `new_item` went on, `displaced` came off."""
        self.worn.add(new_item)
        if displaced and displaced != "(empty)":
            self.worn.discard(displaced)
            for c, it in list(self.slots.items()):
                if it == displaced:
                    self.slots[c] = new_item
        return self.label()

    def label(self):
        return "|".join(sorted(self.worn)) or "(none)"


def session_header(args, mem, rot, loadout):
    return {
        "ts": time.time(),
        "character": mem.self_name(),
        "room": mem.map_name(),
        "map_id": mem.map_id(),
        # THE INTENT. Analysis should never have to guess why a block of rows exists.
        "sweep": args.sweep.split(",") if args.sweep else [],
        "swap_every_s": args.swap_every,
        "swapping": not args.no_swap,
        "loadout_at_start": sorted(loadout.worn),
        "stats_at_start": rot.stable_stats(),
        "rank_report": rot.rank_report(),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid", type=int)
    ap.add_argument("--minutes", type=float, default=30)
    ap.add_argument("--hz", type=float, default=10.0)
    ap.add_argument("--sweep", default="",
                    help="predictors this session claims to move, e.g. 'ac' or 'ac,hit'. "
                         "The run REFUSES to start if they cannot actually vary.")
    ap.add_argument("--swap-every", type=float, default=120.0,
                    help="seconds between gear swaps (only fires between engagements)")
    ap.add_argument("--no-swap", action="store_true", help="pure observation")
    ap.add_argument("--skip-audit", action="store_true",
                    help="trust the cached loadout instead of re-discovering it")
    ap.add_argument("--force", action="store_true",
                    help="run even if the sweep is unfittable (produces known-degenerate data)")
    a = ap.parse_args()

    mem = tk_mem.open_client(a.pid)
    rot = GearRotator(mem)
    if not a.no_swap:
        try:
            from bot_input_test import find_windows
            wins = [w for w in find_windows() if w[2] == mem.pid]
            if not wins:
                raise RuntimeError(f"no visible window owned by pid {mem.pid}")
            mem.attach_window(wins[0][0])
        except Exception as e:
            print(f"cannot bind a window ({e}) -- falling back to observation only")
            a.no_swap = True

    print(f"{mem.self_name()!r} in {mem.map_name()!r}")

    loadout = Loadout(rot)
    if not a.no_swap and not a.skip_audit:
        loadout.audit()

    # --- refuse a session that cannot answer its own question -------------------
    want = [s.strip() for s in a.sweep.split(",") if s.strip()]
    if want and not a.no_swap:
        bad = [w for w in want if w not in PREDICTORS]
        if bad:
            print(f"unknown predictor(s): {bad}; known: {PREDICTORS}")
            return 2
        why = rot.refuse_reason(want=tuple(want))
        if why:
            print(f"\nREFUSING TO START: {why}")
            print("An hour of rows in which the target coefficient is unfittable is worse")
            print("than no rows -- it looks like evidence. Fix the gear set, or --force.")
            if not a.force:
                return 3
            print("--force given: continuing with known-degenerate data.\n")

    rec = RC.CoreRecorder(mem)
    rec.gear_label = loadout.label()
    rec.stat_vector = rot.stable_stats() if not a.no_swap else rot.stats()

    hdr = session_header(a, mem, rot, loadout)
    with open(P_SESSION, "a", encoding="utf-8") as f:
        f.write(json.dumps(hdr) + "\n")
    print(f"session intent: sweep={hdr['sweep'] or '(none)'} "
          f"loadout={rec.gear_label}\nstats={rec.stat_vector}")

    end = time.time() + a.minutes * 60
    period = 1.0 / a.hz
    last_swap = time.time()
    last_stat_refresh = time.time()
    last_flush = time.time()
    swaps = 0

    try:
        while time.time() < end:
            rec.poll()

            engaged = bool(rec.engagements)
            now = time.time()

            if (not a.no_swap and not engaged
                    and now - last_swap >= a.swap_every):
                item, note = rot.step(engaged=engaged)
                last_swap = now
                if item:
                    swaps += 1
                    # rot.step recorded the edge; mirror the change into the labels the
                    # recorder stamps on every subsequent engagement.
                    disp = rot.edges[-1]["old"] if rot.edges else None
                    rec.gear_label = loadout.applied(item, disp)
                    rec.stat_vector = rot.stable_stats()
                    last_stat_refresh = now
                    print(f"  [swap {swaps}] {note}\n           loadout={rec.gear_label}"
                          f"\n           stats={rec.stat_vector}")
                else:
                    print(f"  [swap skipped] {note}")

            # Backstop: gear is the main mover of the stat vector, but a level-up moves
            # it too and would otherwise go unnoticed until the next swap.
            if now - last_stat_refresh > 120 and not engaged:
                v = rot.stats()
                if v != rec.stat_vector:
                    print(f"  [stats changed without a swap] {rec.stat_vector} -> {v}")
                    rec.stat_vector = v
                last_stat_refresh = now

            if now - last_flush > 10:
                rec.flush()
                last_flush = now
            time.sleep(period)
    except KeyboardInterrupt:
        print("\ninterrupted")
    finally:
        rec.close()
        n_eng = (sum(1 for _ in open(RC.P_ENGAGE, encoding="utf-8"))
                 if os.path.exists(RC.P_ENGAGE) else 0)
        print(f"\nswaps: {swaps}   engagements on disk: {n_eng}")
        print(f"coverage: {len(rec.cov.d)} looks")
        for look, e in rec.cov.gaps()[:6]:
            print(f"  look {look:<6} swings={e['swings']:<5} incoming={e['incoming']:<5} "
                  f"exp={e['exp']} hp=[{e['hp_lo']},{e['hp_hi']}]")
        mem.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
