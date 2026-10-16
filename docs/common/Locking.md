# Locking

Every lock in the game process, what it guards, and the order they may be taken in.

This file exists because the order was never written down. #29 made `Session` an actor and asserted a
two-lock order — session monitor, then `World._lock` — in a process that has **six** lock families. Two
deadlocks followed immediately, both with locks the design had not considered, and both found by review
rather than by running the server (they need two players and a specific interleave, so no amount of
playing would reliably have found them). The lesson is the file: a new lock is a change to this table.

---

## The order

Outermost first. A thread may hold locks from several rows, but **only ever in this order**.

| # | Lock | Scope | Guards |
|---|---|---|---|
| 1 | **Lua gate** — `Session.EnterScriptGate()` | one, process-wide | every MoonSharp `Script`: `LuaVerbHost` (spell + item verbs), `NpcScript`, `MobScript`; the `Content.PublishSnapshot` publication boundary |
| 2 | **Session state monitor** — `Session.EnterState()` / `WithState` | one per player | `_buffs`, `_statusFlags`, `_char` and everything hanging off it (inventory, equipment, quests, legends) |
| 3 | **World lock** — `World._lock` | one, process-wide | the maps: player lists, mob lists, ground items, spawn rosters, traps, weather |
| 3 | **Session viewport lock** — `Session.EnterView()` | one per player | `_shownMobs`, `_shownPeers`, `_shownItems`, `_edge*`, the trap/warp markers |
| 4 | **Session write gate** — `Session._writeGate` | one per player | the character row's DB write, and its ordering |

Rows 3 are siblings: nothing takes both, and neither may be held while entering row 1 or 2.

Two consequences of the gate's slow path, both easy to miss. A **contended** cast is two critical sections
with a gap, not one — the monitor is dropped while waiting — so `Handle`'s "a packet is atomic" holds for
everything except that case. And a **paired** body (`WithStatePair`: the trade finalizer, the wedding) must
never enter the gate, because the gap it would open is exactly the tear the pair form exists to prevent.
The GM `@reload` path also enters the gate from its session read-loop while holding that session's monitor;
when contended, it takes this same slow path and drops the monitor while it waits.

Between two session monitors (row 2), the order is **ascending `Session.StateRank`**. A handler runs under
its own monitor and can reach a peer — A casting at B while B casts at A is the standard cycle, and on a PvP
map two players can produce it by accident in the same instant. A total order makes it unrepresentable.

## What enforces it

Not comments. Each rule has an assert, and each assert has a test in `Tests/SessionActorTests.cs`.

* `Session.EnterState` asserts `!World.HoldsWorldLock` and `!Session.HoldsAnyViewLock`.
* `Session.EnterScriptGate` asserts `!Session.HoldsAnyViewLock` and `!World.HoldsAnyWorldLock` (#90): see
  the note on the gate and `World._lock` below.
* `World.MobAiTick.Step` (the per-mob half of the tick) asserts `World.HoldsWorldLock`: the AI runs under row 3 and nowhere else. Debug builds only, like the rest of this list; `Tests/MobAiTickTests.cs` (`StepOutsideTheWorldLockAsserts`) pins it firing.
* `World.SpawnDirector` (the spawn points and batch groups, #37) asserts `World.HoldsWorldLock` at the top of every method that touches map state — thirteen of them, the tick's two sweeps, map entry and the death path included — and takes no lock of its own. `Tests/SpawnDirectorTests.cs` (`DirectorMethodsRefuseToRunOutsideTheWorldLock`) pins the five entry points firing.
* `Session.EnterState` sorts by `StateRank`; a descending nested acquisition drops what it holds, retakes in
  order, and restores the caller's holdings.
* The viewport lock is *counted*, not merely locked (`_viewDepth`), because the lock that breaks the rule
  belongs to a **different** session and `Monitor.IsEntered` cannot see it. That is why `lock (_viewLock)`
  is spelled `using (EnterView())` everywhere.

**The gate and `World._lock` (#90).** Nothing may enter the Lua gate while holding `World._lock` (row 3
under row 1). The gate is static and has no `World` to ask, so instead of counting `_lock` the way `_viewLock`
is counted — which would mean routing its ~60 `lock (_lock)` sites through a guard — every `World` registers
its lock object when it is built (`World.ScriptGateRegistry.cs`, weak references, Debug only), and the gate
asserts `Monitor.IsEntered` is false for each. No lock site changes, and the check covers every host at once:
`LuaVerbHost`, `NpcScript`, `MobScript` and the `Content.PublishSnapshot` boundary. It fires for ANY world's
lock, not only one: the gate is process-wide, so holding a second world's lock while waiting for it is the
same cycle. A Release build compiles neither the registration nor the check. `MobScript.Fire` keeps its own
earlier assert on the tick's hook path; `MobScript.Has`, which *is* called under `_lock` by `World.QueueHook`,
is deliberately lock-free so the hot path never reaches the gate. `Tests/SessionActorTests.cs`
(`EnteringTheLuaGateUnderAWorldLockAsserts`) pins it.

## The two shapes that keep coming back

**Decide under the lock, act outside it.** `World.Broadcast` snapshots the recipient list under `_lock` and
sends outside it. `ReconcilePeer` and `SyncGroundItems` decide what to draw under `_viewLock` and build the
packet outside it. `World.Tick` queues every session-facing call (`hits`, `mobCasts`, `trapDamage`,
`expiredMorphs`) and applies them after releasing `_lock`. Human Barrier (#334) finds the players on its tiles
under `_lock` (`World.RaiseBarrier`) and paralyses each after releasing it, inside that player's monitor. Any of
these done the other way round is a cycle, because the thing you call out to takes a lock of its own.

**Never wait for an outer lock while holding an inner one.** The Lua gate is the worked example. Its fast
path is `Monitor.TryEnter` with no timeout: acquiring a lock you never *block* on cannot complete a cycle,
so an ordinary cast keeps its monitor and stays atomic. Only on contention does it take the slow path, which
drops this thread's session monitors *before* waiting. That is the whole invariant — a thread waiting for
the gate holds no monitor, so whoever holds the gate can always finish.

## How long a hold may be

Wait on a contended lock grows with the **square** of each hold's length, not with the total time the lock
is held. A few long holds cost the waiters far more than many short ones adding up to the same total. So on
`World._lock` the thing worth avoiding is a **long** hold, not a frequent one.

A local measurement (2026-09-21, 400 walkers on one synthetic map, one laptop, 700 accepted steps a second)
found the lock's duty cycle at 1.82% (Release) / 2.45% (Debug). The tick is 1.8% of that held time (Debug)
but about 40% of the walkers' measured wait, because its hold is one ~0.28 ms block where a walker's are
~12 µs each. Tenfolding the tick's hold to 2.7 ms multiplied the walkers' wait by 16 (Debug, 0.28 to
4.40 ms/s). The tick's own wait for the lock (`lock-wait`) is 0.0002 ms/s in both builds — it does not wait.

A hold of a few tenths of a millisecond is free at this load; a hold of several milliseconds is not. Before
a change lengthens a hold under `World._lock`, measure that hold's length, not only its frequency.

Two artefacts of measuring this in-process on a laptop have to be controlled for: CPU
idle states (undisturbed step p50 598 µs against 131 µs with a core kept awake) and the Windows 15.6 ms
timer tick, which synchronises otherwise-independent threads (contended fraction 57% against 5%; fixed with
`timeBeginPeriod(1)`).

## Adding a lock

1. Put it in the table above, with a row number.
2. Assert the rule from the side that can see the violation — usually the outer lock's entry point, since
   the inner one is the one already held.
3. Write the two-thread test that hangs without it, and **check that it hangs**: a deadlock test that has
   never deadlocked is proof of nothing. Same for a race test — falsify it against genuinely unguarded code,
   with the Debug asserts stripped as well, or what you have measured is the assert.

## Deliberately outside all of this

* The **outbound channel** (`TcpOutbound`). `Send` is a non-blocking `TryWrite`; the socket write happens on
  the transport's own writer task. Nothing above ever blocks on a client.
* `Watchdog`'s `DiagState()`. A watchdog that can block on the wedged session it is diagnosing is not a
  watchdog, so it reads plain fields and takes nothing.
* The scalar reads the tick makes under `World._lock` — `PlayerX`, `PlayerY`, `IsDead`, `IsMorphExpired` and
  friends. They are unsynchronised on purpose: taking the monitor there would invert row 2 against row 3.
  `IsReplaced` is a volatile read of `_replaced`; its one writer, `KickForReplacement`, latches it under the
  session's own monitor and nothing clears it. `World` reads it under `World._lock` without the session's monitor
  (#183, #297). `IsInvisible` is one more of these: `World.HiddenPlayersNear` reads it for Second Sight's scan
  (#334), which runs on the tick thread inside the caster's own monitor and takes `World._lock` after it.
* A session's **totals snapshot** (`Session._equipTotals`, an immutable `TotalsSnapshot`: the worn-gear sum plus a
  copy of the timed buffs). The owner builds it under its OWN monitor and publishes it with one `Volatile.Write`:
  `InvalidateEquipTotals` at each gear change and the four `_buffs` writers, or its own first `Totals()` read
  (compare-exchange against null). Anyone reads it with one `Volatile.Read` and no monitor. Another session's
  read (`PeerTotals`: the PvP swing's `SwingTarget.Of`, `LuaTargetArmor`, divination; and the slot checks a
  caster makes of its target, `HasStatusCategory` and `HasStatusFromSpell`, which read the snapshot's copy of
  each buff's category and spell key) never walks the owner's equipment or buff lists; it runs on a thread that
  holds its own monitor, and entering the target's there would nest a second session monitor. A peer that finds
  nothing published reads zero totals and logs it; a session on a map cannot be in that state, because arrival's
  first `SendStats` publishes before `EnterMap` (PR #286 review F2). The slot checks read no buffs then, which is
  what the list holds: nothing is published only while no buff was ever added.
