using System.Diagnostics;

namespace Server;

// The "who is logged in" half of World (#37, section 4). World.cs keeps the lock, the maps and the per-map
// Players lists; this file keeps the account-keyed online slot table behind the duplicate-login guard and the
// three lookups that walk those Players lists on behalf of everyone outside World (by name, by entity id, all
// of them, how many). Nested in World, like SpawnDirector and WorldClock, so it reaches _lock, _maps and
// HoldsWorldLock as they are without widening any of them — and so the slot table stays private to the one
// type that reads it.
public sealed partial class World
{
    /// <summary>
    /// The server-wide roster: the online-account slots the duplicate-login guard turns on, and the lookups
    /// that answer "which session is that?" for code outside <see cref="World"/>. One per <see cref="World"/>,
    /// built by the constructor, called at exactly the moments the code was called when it lived in World.cs.
    ///
    /// <para><b>Lock discipline, unchanged by the move.</b> <see cref="Register"/>, <see cref="Unregister"/>,
    /// <see cref="FindPlayer"/>, <see cref="ById"/>, <see cref="All"/> and <see cref="Count"/> take
    /// <c>World._lock</c> themselves, exactly as they did on <c>World</c>. <see cref="ByIdLocked"/> takes none
    /// and asserts <see cref="HoldsWorldLock"/>, which is the same <c>Monitor.IsEntered(_lock)</c> its old
    /// assert made — it is for callers already inside the lock. <see cref="All"/> is a SNAPSHOT taken under
    /// the lock and iterated outside it; the autosave sweep and the tick both depend on that, because a sweep
    /// that held <c>_lock</c> while flushing to SQLite would freeze the world.</para>
    /// </summary>
    internal sealed class OnlineRegistry
    {
        private const string LockNote = "OnlineRegistry.ByIdLocked runs under World._lock and nowhere else";

        private readonly World world;

        internal OnlineRegistry(World world) => this.world = world;

        // Server-wide online-account registry (independent of the per-map Players lists in World.cs, which a
        // session only joins AFTER its own arrival/load logic runs). Keyed by CharacterStore.Key(username).
        // Exists solely for the duplicate-login guard: Register lets HandleArrival atomically detect + evict a
        // stale session for the same account BEFORE loading, so a slow-to-unwind old session can never clobber
        // the new one's fresher save (SQLite's persistence is blind last-write-wins). Guarded by the same
        // _lock as everything else here — registration/eviction is rare (once per login), so sharing the lock
        // costs nothing measurable against the map operations.
        private readonly Dictionary<string, Session> _online = new();

        /// <summary>The connected player with this character name (case-insensitive, any map), or null if
        /// they're offline. Used by whisper/tell (RTK clif_parsewisp's target lookup).</summary>
        internal Session? FindPlayer(string name)
        {
            // CharName, not Snapshot().Name: Snapshot takes the session's state monitor under _lock (#29, #87).
            // !IsReplaced (#183): a second login leaves the replaced session on its map until its read loop unwinds,
            // and a hit on it loses the whisper or command. Only REPLACED is skipped: a kicked or dropped session is
            // found until its teardown, as before. A volatile read, not a monitor (rule 1); ByIdLocked does the same.
            lock (world._lock)
                return world._maps.Values.SelectMany(m => m.Players)
                                  .FirstOrDefault(p => string.Equals(p.CharName, name, StringComparison.OrdinalIgnoreCase) && !p.IsReplaced);
        }

        /// <summary>The connected player with this entity id (any map), or null. Used by click-profile's "view
        /// another player" path (RTK <c>clif_clickonplayer</c>, §9.5/§11l) and the exchange-initiate opcode
        /// <c>0x4A</c> (RTK <c>clif_parse_exchange</c> type 0), both of which address a player by id — the
        /// client already knows it from the entity it rendered — rather than by name.</summary>
        internal Session? ById(uint id)
        {
            lock (world._lock)
                return ByIdLocked(id);
        }

        /// <summary>Same lookup for callers that already hold <c>_lock</c>. The monitor is re-entrant so taking it
        /// twice would work, but saying which methods expect it is how this file stays readable.</summary>
        internal Session? ByIdLocked(uint id)
        {
            Debug.Assert(world.HoldsWorldLock, LockNote);
            return world._maps.Values.SelectMany(m => m.Players).FirstOrDefault(p => p.PlayerId == id && !p.IsReplaced);
        }

        /// <summary>Every connected player, across every map — a server-wide (not map-scoped) roster snapshot.
        /// Used by channels that reach beyond one map, like subpath chat (RTK clif_sendsubpathmessage loops
        /// every session, not just one map's block list).</summary>
        internal List<Session> All()
        {
            lock (world._lock)
                return world._maps.Values.SelectMany(m => m.Players).ToList();
        }

        /// <summary>One map's live positions, copied out of the world under <c>World._lock</c> so that
        /// everything a reader wants to DO with them — rect tests, fractions, JSON — happens outside it.
        /// Plain arrays and value tuples, no sessions and no mobs: a reference here would let a reader touch
        /// live state off the lock, which is exactly what this type exists to prevent.</summary>
        /// <param name="Steps">The map's running total of ACCEPTED player steps (<c>World.MapState.Steps</c>),
        /// read in the same acquisition as the tiles. Defaulted so the computation seam can be driven by a
        /// test that has nothing to say about walking.</param>
        internal sealed record MapPositions(ushort Map,
                                            (uint Id, ushort X, ushort Y)[] Players,
                                            (ushort X, ushort Y)[] Mobs,
                                            long Steps = 0);

        /// <summary>Every map with at least one player on it, with that map's players' (id, tile) and its
        /// ALIVE mobs' tiles copied into plain arrays. The survey document's one read of the world
        /// (<see cref="ViewportSurvey"/>).
        ///
        /// <para><b>Copies and nothing else.</b> Under <c>_lock</c> this does two array fills per map and
        /// returns; every rect test and every byte of JSON is computed by the caller outside the lock. That
        /// is the same shape <see cref="All"/> and <see cref="Count"/> already use for the status thread, and
        /// it is the reason this is safe to run beside the tick: the hold is proportional to the number of
        /// entities, not to the work the survey does with them.</para>
        ///
        /// <para><b>No session monitor is taken</b>, deliberately. <c>PlayerX</c>/<c>PlayerY</c> are read the
        /// way <c>World.EntityPos</c> and <c>World.EnterMap</c> read them — bare, under <c>_lock</c>, which is
        /// the lock their only writer (<c>Session.SetPositionUnderWorldLock</c>) holds. Taking a session's
        /// state monitor from here would be the wrong order anyway (#29 is session state THEN <c>_lock</c>,
        /// and we are inside <c>_lock</c>), so it is not an option. The residual risk is a torn (x, y) pair
        /// on a player mid-step, which in a positional survey is one player one tile off.</para></summary>
        internal MapPositions[] PositionSurvey()
        {
            lock (world._lock)
            {
                var maps = new List<MapPositions>();
                foreach (var (id, m) in world._maps)
                {
                    int pc = m.Players.Count;
                    if (pc == 0) continue;                       // maps with no viewer have no fraction to take

                    var players = new (uint, ushort, ushort)[pc];
                    for (int i = 0; i < pc; i++)
                    {
                        var p = m.Players[i];
                        players[i] = (p.PlayerId, p.PlayerX, p.PlayerY);
                    }

                    // Two passes so the array is exactly sized: the alive count is stable under this lock,
                    // which is the same lock every writer of Mob.Hp takes.
                    int alive = 0;
                    foreach (var mo in m.Mobs) if (mo.Alive) alive++;
                    var mobs = new (ushort, ushort)[alive];
                    int k = 0;
                    foreach (var mo in m.Mobs) if (mo.Alive) mobs[k++] = (mo.X, mo.Y);

                    maps.Add(new MapPositions(id, players, mobs, m.Steps));
                }
                return maps.ToArray();
            }
        }

        /// <summary>How many players are in the world right now. Separate from <see cref="All"/> because
        /// the status publisher wants only the number, and materialising every session into a list on a timer to
        /// read <c>.Count</c> off it is pure garbage.</summary>
        internal int Count
        {
            get
            {
                lock (world._lock)
                {
                    var n = 0;
                    foreach (var m in world._maps.Values) n += m.Players.Count;
                    return n;
                }
            }
        }

        // #168: accounts whose session tore down moments ago, keyed like _online, with the TickCount64 of the
        // teardown. A session parks here instead of simply dropping its slot (Depart), so the next login for
        // the account is handed it (Register) and fences it with the same KickForReplacement a live duplicate
        // gets: the kick enters the departed session's monitor, which waits out a late group share or a late
        // death still writing under it, writes the row, and latches _replaced so nothing later can. Kept apart
        // from _online so the live-slot contract (Unregister's compare-and-remove, HoldsSlotForTest) is exactly
        // what it was. Pruned by the autosave sweep once an entry is one AutoSaveMs old (AllForSweep), so it
        // holds at most one session per account for between one and two sweep intervals. Guarded by _lock.
        private readonly Dictionary<string, (Session Session, long AtMs)> _departed = new();

        /// <summary>Duplicate-login guard: atomically register <paramref name="s"/> as the online session for
        /// <paramref name="key"/> (CharacterStore.Key(username)), returning whatever session previously held
        /// that slot via <paramref name="old"/> (null if this is a fresh login). Called from HandleArrival
        /// BEFORE the character is loaded from disk, so a second concurrent arrival for the same account can
        /// never both pass unnoticed — the dictionary write is atomic under _lock. The caller (HandleArrival)
        /// is responsible for kicking <paramref name="old"/> (Session.KickForReplacement) so its state is
        /// flushed before the new session's own Load runs.
        ///
        /// <para>When no live session holds the slot, the session that most recently DEPARTED it (see
        /// <see cref="Depart"/>) is handed back instead, taken out of the departed table, with
        /// <paramref name="departed"/> set (#168). The caller fences it with the same kick.</para></summary>
        internal void RegisterArrival(string key, Session s, out Session? old, out bool departed)
        {
            lock (world._lock)
            {
                departed = false;
                if (!_online.TryGetValue(key, out old) && _departed.Remove(key, out var gone))
                {
                    old = gone.Session;
                    departed = true;
                }
                _online[key] = s;
            }
        }

        /// <summary><see cref="RegisterArrival"/> for a caller that does not care whether the session handed
        /// back was live or departed.</summary>
        internal void Register(string key, Session s, out Session? old) => RegisterArrival(key, s, out old, out _);

        /// <summary>Remove <paramref name="s"/> from the online registry, but ONLY if it still owns that slot —
        /// a compare-and-remove so a session that was already kicked/replaced (Register overwrote its
        /// slot with the newer session) can't accidentally evict the session that replaced it when its own
        /// (now-stale) teardown finally runs. The arrival's own refusals give their slot back through here: a
        /// session that never loaded a character has nothing to fence.</summary>
        internal void Unregister(string key, Session s)
        {
            lock (world._lock)
            {
                if (_online.TryGetValue(key, out var cur) && ReferenceEquals(cur, s))
                    _online.Remove(key);
            }
        }

        /// <summary>The teardown's half of the #168 fence: <see cref="Unregister"/>'s compare-and-remove, and
        /// the removed session parked in the departed table, stamped <paramref name="nowMs"/>. Only a session
        /// that still owns the slot parks: one a newer login replaced has already been fenced by that login's
        /// kick, and parking it would hand a stale session to the NEXT login.</summary>
        internal void Depart(string key, Session s, long nowMs)
        {
            lock (world._lock)
            {
                if (_online.TryGetValue(key, out var cur) && ReferenceEquals(cur, s))
                {
                    _online.Remove(key);
                    _departed[key] = (s, nowMs);
                }
            }
        }

        /// <summary><see cref="All"/> for the autosave sweep, which also prunes the departed table in the same
        /// acquisition of <c>_lock</c>: every entry stamped at or before <c>nowMs - Session.AutoSaveMs</c> is
        /// dropped. A departed session is off its map, so it is never in the roster this returns.</summary>
        internal List<Session> AllForSweep(long nowMs)
        {
            lock (world._lock)
            {
                if (_departed.Count > 0)
                {
                    long cutoff = nowMs - Session.AutoSaveMs;
                    List<string>? stale = null;
                    foreach (var (key, entry) in _departed)
                        if (entry.AtMs <= cutoff) (stale ??= new List<string>()).Add(key);
                    if (stale is not null) foreach (var key in stale) _departed.Remove(key);
                }
                return world._maps.Values.SelectMany(m => m.Players).ToList();
            }
        }

        /// <summary>Whether <paramref name="s"/> is parked in the departed table under <paramref name="key"/>.
        /// Reads the same table under the same lock; nothing in production calls it.</summary>
        internal bool HoldsDepartedForTest(string key, Session s)
        {
            lock (world._lock)
                return _departed.TryGetValue(key, out var entry) && ReferenceEquals(entry.Session, s);
        }

        /// <summary>Whether <paramref name="key"/>'s slot is held by <paramref name="s"/> right now — the
        /// compare half of <see cref="Unregister"/>'s compare-and-remove, exposed so a test can watch the slot
        /// change hands instead of inferring it from who gets kicked. Reads the same table under the same
        /// lock; nothing in production calls it.</summary>
        internal bool HoldsSlotForTest(string key, Session s)
        {
            lock (world._lock)
                return _online.TryGetValue(key, out var cur) && ReferenceEquals(cur, s);
        }
    }

    /// <summary>The online roster — the duplicate-login slot table and the lookups by name, by entity id and
    /// across the whole world. Sessions reach it directly (<c>_world.Online.FindPlayer(name)</c>);
    /// <c>World</c> keeps no forwarders, the same rule the spawn, movement and clock extractions followed.</summary>
    internal OnlineRegistry Online { get; }
}
