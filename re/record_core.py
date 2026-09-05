#!/usr/bin/env python
"""
record_core.py -- CORE TELEMETRY. The always-on half of docs/BOT-DATA-PLAN.md §5.

The dividing rule: core is everything collectable without changing what the bot does,
with every varying input stamped so analysis can filter later. Sessions (§5c) are the
protocols where you must NOT fight normally; none of them live here.

Two ways to run it:

    python re/record_core.py --watch 600         # standalone, MEMORY ONLY
    (or)  driven by the bot, which also feeds it packets

Standalone mode sees everything memory exposes -- roster, census, tracks, engagement
geometry, mob HP%, our own vitals -- and is exactly what the passive sessions (S1 sit-and-
watch, S2 approach ladder) need. It CANNOT see exact exp per kill or attribute incoming
damage, because those come off the wire. Full fidelity means running under the bot and
letting it call `on_packet`/`on_swing`.

WHAT THE ENGAGEMENT RECORD IS FOR. Loose swing rows carry contamination that cannot be
cleaned afterwards: fleeing targets logged as misses, damage that cannot be attributed,
mixed facing, pooled levels. An engagement -- one mob, adjacent AND facing us, from pull
to death or disengage -- makes each of those either impossible or an explicit field.

NPC HAZARD. `type == 3` does not separate mobs from NPCs (measured: two NPCs in Mignok's
home both read 3, both with empty names). The only authority is the `0x07` spawn class
byte, which pool enumeration never sees. So we learn class PER LOOK from spawn packets,
cache it in look_class.json, and mark anything unclassified as UNKNOWN. Never assume mob.
"""
import argparse, csv, json, os, sys, time, collections

import tk_offsets as T
import tk_mem

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "auto")
os.makedirs(OUT, exist_ok=True)

P_SPAWNS   = os.path.join(OUT, "mob_spawns.csv")
P_DESPAWNS = os.path.join(OUT, "mob_despawns.csv")
P_TRACKS   = os.path.join(OUT, "mob_tracks.jsonl")
P_CENSUS   = os.path.join(OUT, "room_census.csv")
P_ENGAGE   = os.path.join(OUT, "engagements.jsonl")
P_COVER    = os.path.join(OUT, "coverage.json")
P_CLASS    = os.path.join(OUT, "look_class.json")

SPAWN_COLS   = ["ts", "room", "map_id", "eid", "look", "x", "y", "cls", "how"]
DESPAWN_COLS = ["ts", "room", "map_id", "eid", "look", "x", "y", "reason", "lifetime_s"]
CENSUS_COLS  = ["ts", "room", "map_id", "look", "cls", "count"]

# 0x07 spawn class byte -> label. From STATE-OF-KNOWLEDGE §3a.
CLS = {2: "player", 5: "mob", 6: "mob", 12: "npc"}

# facing -> the tile offset the mob is looking at (0 up, 1 right, 2 down, 3 left)
FACE_DELTA = {0: (0, -1), 1: (1, 0), 2: (0, 1), 3: (-1, 0)}


def append_csv(path, rows, cols):
    if not rows:
        return
    header = None
    if os.path.exists(path):
        try:
            with open(path, newline="", encoding="utf-8") as f:
                header = next(csv.reader(f), None)
        except OSError:
            header = None
    if header is not None and header != cols:
        try:
            os.replace(path, f"{path}.{time.strftime('%Y%m%d-%H%M%S')}.bak")
        except OSError:
            pass
        header = None
    with open(path, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
        if header is None:
            w.writeheader()
        for r in rows:
            w.writerow({c: r.get(c, "") for c in cols})


def append_jsonl(path, objs):
    if not objs:
        return
    with open(path, "a", encoding="utf-8") as f:
        for o in objs:
            f.write(json.dumps(o, default=str) + "\n")


def load_json(path, default):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default


def adjacent(ax, ay, bx, by):
    return abs(ax - bx) + abs(ay - by) == 1


def faces(mob, sx, sy):
    """Does this mob's facing point at our tile? Engagement is a READ, not an inference
    from damage timing -- adjacent and facing means engaged, turning away means not."""
    d = FACE_DELTA.get(mob.get("facing"))
    if not d:
        return False
    return (mob["x"] + d[0], mob["y"] + d[1]) == (sx, sy)


class Coverage:
    """What is measured, per look. This is what turns a grinder into a survey instrument:
    the bot reads it to prefer under-sampled mobs instead of farming whatever is nearest."""

    def __init__(self):
        self.d = load_json(P_COVER, {})

    def bump(self, look, field, n=1):
        k = str(look)
        e = self.d.setdefault(k, {"seen": 0, "engagements": 0, "swings": 0, "hits": 0,
                                  "incoming": 0, "kills": 0, "exp": None, "ac": None,
                                  "hp_lo": None, "hp_hi": None, "rooms": []})
        e[field] = (e.get(field) or 0) + n

    def note(self, look, **kw):
        k = str(look)
        e = self.d.setdefault(k, {"seen": 0, "engagements": 0, "swings": 0, "hits": 0,
                                  "incoming": 0, "kills": 0, "exp": None, "ac": None,
                                  "hp_lo": None, "hp_hi": None, "rooms": []})
        for kk, vv in kw.items():
            if kk == "room":
                if vv and vv not in e["rooms"]:
                    e["rooms"].append(vv)
            elif vv is not None:
                e[kk] = vv

    def gaps(self):
        """Looks with the least data first -- the bot's targeting preference order."""
        def score(e):
            return (e["swings"] + e["incoming"], 0 if e["ac"] is None else 1,
                    0 if e["exp"] is None else 1)
        return sorted(self.d.items(), key=lambda kv: score(kv[1]))

    def save(self):
        with open(P_COVER, "w", encoding="utf-8") as f:
            json.dump(self.d, f, indent=1, sort_keys=True)


class CoreRecorder:
    def __init__(self, mem, log=print):
        self.mem = mem
        self.log = log
        self.room, self.map_id = "", None
        self.known = {}          # eid -> last seen record
        self.first_seen = {}     # eid -> ts
        self.tracks = {}         # eid -> [[ts,x,y,facing], ...]
        self.engagements = {}    # eid -> open engagement dict
        self.look_class = load_json(P_CLASS, {})     # str(look) -> "mob"/"npc"/"player"
        self.cov = Coverage()
        self.pending = collections.defaultdict(list)
        self.last_census = 0.0
        self.self_prev_hp = None
        self.stat_vector = {}    # filled by the bot from 0x08; stamped on every engagement
        self.gear_label = ""     # current loadout id, set by the rotation driver

    # ------------------------------------------------------------ packet side
    def on_packet(self, op, d, ts):
        """Fed by the bot's decrypt hook. Only the parts core needs."""
        if op == 0x07 and len(d) >= 14:
            eid = int.from_bytes(d[5:9], "big") if len(d) >= 9 else None
            cls = CLS.get(d[4], f"cls{d[4]}")
            look = int.from_bytes(d[9:11], "big") & 0x7FFF if len(d) >= 11 else None
            if look is not None:
                # THE NPC FIX: class is only ever visible here, so cache it by look.
                prev = self.look_class.get(str(look))
                if prev and prev != cls:
                    self.log(f"look {look} class changed {prev} -> {cls}")
                self.look_class[str(look)] = cls
            if eid:
                self.pending["spawn"].append(
                    {"ts": ts, "room": self.room, "map_id": self.map_id, "eid": eid,
                     "look": look, "x": int.from_bytes(d[0:2], "big"),
                     "y": int.from_bytes(d[2:4], "big"), "cls": cls, "how": "packet"})
        elif op == 0x0e and len(d) >= 5:
            eid = int.from_bytes(d[1:5], "big")
            self._despawn(eid, ts, "packet")

    def on_swing(self, eid, hit, dmg, ts):
        """One outgoing attack, already resolved to hit/miss by the bot."""
        e = self.engagements.get(eid)
        if e is not None:
            e["swings"].append({"ts": ts, "hit": int(bool(hit)), "dmg": dmg})
        look = self.known.get(eid, {}).get("look")
        if look is not None:
            self.cov.bump(look, "swings")
            if hit:
                self.cov.bump(look, "hits")

    def on_kill(self, eid, exp, ts):
        e = self.engagements.get(eid)
        look = self.known.get(eid, {}).get("look")
        if e is not None:
            e["killed"] = True
            e["exp"] = exp
        if look is not None:
            self.cov.bump(look, "kills")
            if exp:
                self.cov.note(look, exp=exp)

    # ------------------------------------------------------------ memory side
    def poll(self, ts=None):
        ts = ts or time.time()
        me = self.mem.self_all()
        if not me:
            return
        room = self.mem.map_name()
        mid = self.mem.map_id()
        if room != self.room:
            self._room_change(room, mid, ts)
        self.room, self.map_id = room, mid

        ents = self.mem.entities()
        cur = {}
        for e in ents:
            if e["uid"] == 0:
                continue
            # Our own character object shares the entity layout and passes the validity
            # test, so it turns up in the roster. Tiles are exclusive, so the entity
            # standing exactly where we are IS us -- drop it, or every census counts a
            # phantom mob and the coverage ledger fills with our own look id.
            if (e["x"], e["y"]) == (me["x"], me["y"]):
                self.self_eid = e["uid"]
                continue
            cur[e["uid"]] = e

        self._diff_roster(cur, ts)
        self._update_tracks(cur, ts)
        self._update_engagements(cur, me, ts)
        self._census(cur, ts)
        self.known = cur
        self.self_prev_hp = me["curhp"]

    def _room_change(self, room, mid, ts):
        """A warp. Close every open engagement and track -- entities do not follow us."""
        for eid in list(self.engagements):
            self._close_engagement(eid, ts, "room_change")
        for eid in list(self.tracks):
            self._flush_track(eid)
        for eid, rec in self.known.items():
            self._despawn(eid, ts, "room_change", rec)
        self.known = {}

    def _diff_roster(self, cur, ts):
        """Presence comes from the pool, not the wire: stationary mobs emit no packets
        and despawns are unreliable, so a packet-derived roster accumulates ghosts."""
        for eid, e in cur.items():
            if eid in self.known:
                continue
            self.first_seen[eid] = ts
            cls = self.look_class.get(str(e["look"]), "unknown")
            self.pending["spawn"].append(
                {"ts": ts, "room": self.room, "map_id": self.map_id, "eid": eid,
                 "look": e["look"], "x": e["x"], "y": e["y"], "cls": cls,
                 # a genuine spawn vs something that walked into view are different
                 # events and only the first measures respawn timing
                 "how": "pool"})
            self.cov.bump(e["look"], "seen")
            self.cov.note(e["look"], room=self.room)
        for eid in list(self.known):
            if eid not in cur:
                self._despawn(eid, ts, "gone", self.known[eid])

    def _despawn(self, eid, ts, reason, rec=None):
        rec = rec or self.known.get(eid)
        if not rec:
            return
        born = self.first_seen.pop(eid, None)
        self.pending["despawn"].append(
            {"ts": ts, "room": self.room, "map_id": self.map_id, "eid": eid,
             "look": rec.get("look"), "x": rec.get("x"), "y": rec.get("y"),
             "reason": reason,
             "lifetime_s": round(ts - born, 1) if born else ""})
        self._flush_track(eid)
        if eid in self.engagements:
            self._close_engagement(eid, ts, reason)

    def _update_tracks(self, cur, ts):
        for eid, e in cur.items():
            prev = self.known.get(eid)
            if prev and (prev["x"], prev["y"], prev["facing"]) == (e["x"], e["y"], e["facing"]):
                continue          # only record CHANGES; a stationary mob is implied
            self.tracks.setdefault(eid, []).append([round(ts, 2), e["x"], e["y"], e["facing"]])

    def _flush_track(self, eid):
        s = self.tracks.pop(eid, None)
        if not s or len(s) < 2:
            return
        rec = self.known.get(eid, {})
        append_jsonl(P_TRACKS, [{"eid": eid, "look": rec.get("look"),
                                 "room": self.room, "map_id": self.map_id,
                                 "cls": self.look_class.get(str(rec.get("look")), "unknown"),
                                 "samples": s}])

    # ------------------------------------------------------- §3.0 engagements
    def _update_engagements(self, cur, me, ts):
        sx, sy = me["x"], me["y"]
        adj = [e for e in cur.values() if adjacent(sx, sy, e["x"], e["y"])]
        # The adjacency SET is stamped on every sample, not reduced to a count. Analysis
        # decides validity later: all-one-look is usable (our models are per-look), mixed
        # looks get dropped for incoming damage. Requiring one adjacent mob would throw
        # away most of the game.
        adj_set = [{"eid": a["uid"], "look": a["look"]} for a in adj]
        engaged_now = {a["uid"] for a in adj if faces(a, sx, sy)}

        for a in adj:
            if a["uid"] not in engaged_now or a["uid"] in self.engagements:
                continue
            self.engagements[a["uid"]] = {
                "eid": a["uid"], "look": a["look"],
                "cls": self.look_class.get(str(a["look"]), "unknown"),
                "room": self.room, "map_id": self.map_id,
                "t0": round(ts, 2), "gear": self.gear_label,
                "stats_at_entry": dict(self.stat_vector),
                "self_maxhp": me["maxhp"],
                "samples": [], "swings": [], "incoming": [],
                "killed": False, "exp": None,
            }
            self.cov.bump(a["look"], "engagements")
            # SAY SO. A record is only written when the engagement closes, so without
            # this a live fight produces no output at all and the recorder looks dead
            # while it is in fact working.
            self.log(f"ENGAGED  eid={a['uid']} look={a['look']} "
                     f"cls={self.look_class.get(str(a['look']), 'unknown')} "
                     f"at ({a['x']},{a['y']})")

        drop = self.self_prev_hp is not None and me["curhp"] < self.self_prev_hp
        dmg = (self.self_prev_hp - me["curhp"]) if drop else 0

        for eid, en in list(self.engagements.items()):
            e = cur.get(eid)
            if e is None or not adjacent(sx, sy, e["x"], e["y"]) or not faces(e, sx, sy):
                self._close_engagement(eid, ts, "disengaged")
                continue
            en["samples"].append([round(ts, 2), e["hp_pct"], e["x"], e["y"], e["facing"],
                                  sx, sy, me["curhp"], adj_set])
            if drop:
                # Attribution is by look, not eid: with several same-look mobs adjacent
                # the draw is still valid for that look's damage distribution, and the
                # adjacency count in the sample is what makes cadence recoverable.
                mixed = len({a["look"] for a in adj_set}) > 1
                en["incoming"].append({"ts": round(ts, 2), "dmg": dmg,
                                       "hp_after": me["curhp"], "n_adj": len(adj_set),
                                       "mixed": mixed})
                self.cov.bump(en["look"], "incoming")
                self.log(f"  hit taken {dmg:>4}  hp {me['curhp']}/{me['maxhp']}  "
                         f"from look {en['look']}  n_adj={len(adj_set)}"
                         f"{'  MIXED (excluded)' if mixed else ''}")

    def _close_engagement(self, eid, ts, reason):
        en = self.engagements.pop(eid, None)
        if not en:
            return
        en["t1"] = round(ts, 2)
        en["reason"] = reason
        en["dur_s"] = round(en["t1"] - en["t0"], 2)
        if en["samples"] or en["swings"]:
            self.pending["engage"].append(en)
            self._update_hp_bounds(en)
            hits = sum(1 for s in en["swings"] if s.get("hit"))
            self.log(f"CLOSED   eid={en['eid']} look={en['look']} {en['dur_s']}s "
                     f"({en['reason']})  swings={len(en['swings'])} hits={hits} "
                     f"taken={len(en['incoming'])}"
                     + (f"  HP in [{en['hp_lo']},{en['hp_hi']}]" if en.get("hp_lo") else ""))

    def _update_hp_bounds(self, en):
        """Mob max HP by percent-step intersection (BOT-DATA-PLAN §3.1).

        The bar shows p = floor(100*(H-D)/H) for cumulative damage D, so each reading
        gives H in [100D/(100-p), 100D/(99-p)). Intersecting over a fight collapses fast
        as p falls.

        The bar is an ON-HIT FLASH lasting well under a second, so most samples in a
        fight carry pct=None -- that means "no recent hit", NOT "undamaged", and those
        samples are simply skipped. A reading of exactly 100 is also skipped: it cannot
        distinguish full health from a bar that has only just been populated.
        """
        cum = 0
        lo, hi = 0.0, float("inf")
        by_ts = {s["ts"]: s for s in en["swings"] if s.get("dmg")}
        for smp in en["samples"]:
            ts_s, pct = smp[0], smp[1]
            for t, s in list(by_ts.items()):
                if t <= ts_s:
                    cum += s["dmg"]
                    by_ts.pop(t)
            if pct is None or pct >= 100 or cum <= 0:
                continue
            lo = max(lo, 100.0 * cum / (100 - pct))
            if pct < 99:
                hi = min(hi, 100.0 * cum / (99 - pct))
        if lo > 0 and lo <= hi:
            en["hp_lo"], en["hp_hi"] = round(lo, 1), (None if hi == float("inf") else round(hi, 1))
            cur = self.cov.d.get(str(en["look"]), {})
            best_lo = max(lo, cur.get("hp_lo") or 0)
            best_hi = min(hi, cur.get("hp_hi") or float("inf"))
            self.cov.note(en["look"], hp_lo=round(best_lo, 1),
                          hp_hi=None if best_hi == float("inf") else round(best_hi, 1))

    def _census(self, cur, ts, every=5.0):
        if ts - self.last_census < every:
            return
        self.last_census = ts
        counts = collections.Counter((e["look"]) for e in cur.values())
        for look, n in counts.items():
            self.pending["census"].append(
                {"ts": round(ts, 1), "room": self.room, "map_id": self.map_id,
                 "look": look, "cls": self.look_class.get(str(look), "unknown"),
                 "count": n})

    # ------------------------------------------------------------------ flush
    def flush(self):
        append_csv(P_SPAWNS, self.pending.pop("spawn", []), SPAWN_COLS)
        append_csv(P_DESPAWNS, self.pending.pop("despawn", []), DESPAWN_COLS)
        append_csv(P_CENSUS, self.pending.pop("census", []), CENSUS_COLS)
        append_jsonl(P_ENGAGE, self.pending.pop("engage", []))
        self.pending.clear()
        self.cov.save()
        with open(P_CLASS, "w", encoding="utf-8") as f:
            json.dump(self.look_class, f, indent=1, sort_keys=True)

    def close(self, ts=None):
        ts = ts or time.time()
        for eid in list(self.engagements):
            self._close_engagement(eid, ts, "shutdown")
        for eid in list(self.tracks):
            self._flush_track(eid)
        self.flush()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid", type=int)
    ap.add_argument("--watch", type=float, default=0, help="seconds to record (0 = forever)")
    ap.add_argument("--hz", type=float, default=10.0)
    a = ap.parse_args()

    mem = tk_mem.open_client(a.pid)
    rec = CoreRecorder(mem)
    print(f"recording {mem.self_name()!r} in {mem.map_name()!r} "
          f"(memory-only: no exp/damage attribution -- run under the bot for those)")
    end = time.time() + a.watch if a.watch else None
    period, last_flush = 1.0 / a.hz, time.time()
    try:
        while end is None or time.time() < end:
            rec.poll()
            if time.time() - last_flush > 10:
                rec.flush()
                last_flush = time.time()
            time.sleep(period)
    except KeyboardInterrupt:
        print("\nstopping")
    finally:
        rec.close()
        n = sum(1 for _ in open(P_ENGAGE, encoding="utf-8")) if os.path.exists(P_ENGAGE) else 0
        print(f"engagements on disk: {n}")
        print(f"coverage: {len(rec.cov.d)} looks; unclassified "
              f"{sum(1 for k in rec.cov.d if rec.look_class.get(k) is None)}")
        mem.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
