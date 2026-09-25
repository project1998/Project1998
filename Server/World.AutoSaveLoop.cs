using System.Diagnostics;
using Shared;

namespace Server;

// The crash-safety half of World (#37, section 4). TkListener.StartWorld STARTS the world-autosave thread
// (thread start-up was section 5); this file is what that thread runs — the periodic sweep, the per-player
// isolation fence both sweeps share, and the shutdown flush TkListener reports. Nested in World, like
// SpawnDirector and WorldClock, so it reaches Online and HoldsWorldLock as they are without widening either.
public sealed partial class World
{
    /// <summary>
    /// The autosave sweep: the periodic flush of every connected player, the fence that keeps one player's
    /// failure off the rest of the sweep, and the last-chance flush at shutdown. One per <see cref="World"/>,
    /// built by the constructor, called at exactly the moments the code was called when it lived in World.cs.
    ///
    /// <para><b>Lock discipline, unchanged by the move.</b> Nothing here takes <c>World._lock</c> and nothing
    /// here may be called under it. <see cref="OnlineRegistry.All"/> takes the lock for the length of one
    /// snapshot and returns; the flushing happens OUTSIDE it, because <see cref="FlushIsolated"/> calls
    /// <c>Session.FlushNow</c>, which enters that session's own state monitor — the inversion rule 1 forbids
    /// (Session.State.cs) — and because a sweep that held <c>_lock</c> across a synchronous SQLite write would
    /// freeze the world for the length of the sweep. <see cref="Tick(long)"/> and <see cref="SaveAll"/> assert
    /// <c>!HoldsWorldLock</c> at the top: that only writes down what was already true, since the assert in
    /// <c>Session.EnterState</c> would fire a moment later anyway, but it names the sweep as the offender
    /// instead of naming whichever player happened to be first in the snapshot.</para>
    /// </summary>
    internal sealed class AutoSaveLoop
    {
        private const string LockNote =
            "the autosave sweep flushes sessions and so runs OUTSIDE World._lock and nowhere else: " +
            "FlushNow enters the session's state monitor, which is the lock-order inversion rule 1 forbids " +
            "(Server/Session.State.cs), and a sweep holding _lock across a SQLite write freezes the world";

        private readonly World world;

        internal AutoSaveLoop(World world) => this.world = world;

        /// <summary>Periodic crash-safety backstop (see <see cref="Run"/>): flush every connected player's pending
        /// mutation, regardless of the per-session AutoSaveMs throttle. Its unique job is an IDLE dirty player
        /// (mutated, then stopped sending packets, so their own read-loop FlushIfDue never gets another
        /// iteration to fire on) — an ACTIVE player is already covered by their own on-thread flush.
        ///
        /// <para>It also prunes the duplicate-login guard's departed table (#168), inside the one acquisition of
        /// <c>_lock</c> the roster snapshot already makes: a teardown older than one interval is no longer
        /// fenced by the next login.</para></summary>
        internal void Tick() => Tick(Environment.TickCount64);

        /// <summary><see cref="Tick()"/> at a given clock reading, so a test can age the departed table without
        /// waiting an interval.</summary>
        internal void Tick(long nowMs)
        {
            Debug.Assert(!world.HoldsWorldLock, LockNote);
            foreach (var s in world.Online.AllForSweep(nowMs)) FlushIsolated(s, "autosave");
        }

        /// <summary>One player's flush, fenced so it can't take the rest of a sweep with it. Before this, one
        /// throw from FlushNow unwound the whole foreach in <see cref="Run"/>'s catch, and every player AFTER the
        /// unlucky one in that snapshot silently missed the interval. Idle dirty players are exactly who the
        /// sweep exists for (see <see cref="Tick()"/>), so a skipped sweep is a real crash-safety hole, not a delay.
        ///
        /// <para>The throw it was written for was a collection mutated under the serializer by that player's own
        /// thread; #29 closed that off — FlushNow now serializes a snapshot taken under the session's state
        /// monitor — so what is left to catch here is a value the serializer rejects (the #282 NaN route) or a
        /// store that throws rather than returning false. The fence stays: "one player's failure must not
        /// cost every later player their interval" is worth keeping whatever the cause.</para>
        ///
        /// <para>Returns whether the flush succeeded, because the two callers face different consequences and
        /// must say different things. The periodic sweep genuinely does retry on its next interval. The
        /// shutdown flush has no next interval: a throw there is the player's last state LOST, and reporting it
        /// as "retried" — or, worse, counting it as saved — is the one thing an operator reading the final
        /// lines of a log must not be told. <paramref name="lastChance"/> picks the wording.</para></summary>
        internal static bool FlushIsolated(Session s, string sweep, bool lastChance = false)
        {
            try { s.FlushNow(); return true; }
            catch (Exception e)
            {
                Log.Error($"{sweep}: flush of '{s.UserKey}' ({s.Remote}) threw — " +
                          (lastChance ? "save LOST — process is exiting, there is no retry"
                                      : "that player's save is retried next sweep, the others continue"), e);
                return false;
            }
        }

        // Own thread (see TkListener.StartWorld): each FlushNow serializes a multi-KB character graph to JSON
        // and does a synchronous SQLite write, so a sweep of a full server is a long block. On the thread pool
        // that was a pool thread held for the duration, competing with the heartbeat.
        internal void Run()
        {
            Log.Info($"autosave sweep running on thread '{Thread.CurrentThread.Name}' every {Session.AutoSaveMs} ms");
            while (true)
            {
                Thread.Sleep(Session.AutoSaveMs);
                // Tick isolates each player's flush; this only sees a throw from Online.All itself.
                try { Tick(); }
                catch (Exception e) { Log.Error("autosave sweep threw — retrying on the next interval", e); }
            }
        }

        /// <summary>Graceful-shutdown flush: force-save every connected player right now, ignoring the dirty
        /// flag entirely is NOT needed here — FlushNow already no-ops a clean session cheaply. Cannot help
        /// against a hard crash/kill — that's what the periodic <see cref="Run"/> sweep + each session's own
        /// on-thread flush bound instead.
        ///
        /// <para>Returns (saved, failed) rather than the population count. It used to return
        /// <c>players.Count</c> whatever happened, so the shutdown hook's "flushed N player(s)" was the number
        /// of players CONNECTED, not the number persisted — a run that lost three characters' last hour logged
        /// exactly what a clean one did. The caller reports both numbers (see TkListener.Shutdown).</para></summary>
        internal (int saved, int failed) SaveAll()
        {
            Debug.Assert(!world.HoldsWorldLock, LockNote);
            var players = world.Online.All();
            int saved = 0, failed = 0;
            foreach (var s in players)
            {
                if (FlushIsolated(s, "shutdown save", lastChance: true)) saved++;
                else failed++;
            }
            return (saved, failed);
        }
    }

    /// <summary>The autosave sweep — the periodic flush the <c>world-autosave</c> thread runs and the
    /// last-chance flush at shutdown. The shutdown hook reaches it directly
    /// (<c>_world.AutoSave.SaveAll()</c>); <c>World</c> keeps no forwarders, the same rule the spawn,
    /// movement and clock extractions followed.</summary>
    internal AutoSaveLoop AutoSave { get; }
}
