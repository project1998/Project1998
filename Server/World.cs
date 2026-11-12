using System.Buffers;
using System.Diagnostics;
using Shared;

namespace Server;

/// <summary>A read-only snapshot of a player entity, so a peer can draw it without touching that
/// session's mutable state. Built under no lock (fields are only written by the owning session's
/// read-loop; a torn read at worst mis-places a peer by one tile until its next move packet).</summary>
public readonly record struct PlayerSnapshot(
    uint Id, ushort X, ushort Y, byte Dir, byte Sex, byte Face, byte Armor, byte Weapon, byte Shield, bool Mounted, bool Dead, string Name,
    byte ArmorColor = 0, ushort MorphLook = 0, byte MorphColor = 0, bool Faded = false, byte HairColor = 0);

/// <summary>A stack of an item lying on the map floor, drawn to every client on that map via 0x16
/// (Item.epf frame = <see cref="Graphic"/>). <see cref="Id"/> is the entity id (find/despawn key). Carries
/// enough to reconstruct an <see cref="Shared.InvItem"/> when a player picks it up.</summary>
public sealed class GroundItem
{
    public uint   Id;
    public int    ItemId;
    public ushort X, Y;
    public int    Amount = 1;
    public ushort Dura;
    public ushort Graphic;       // Item.epf frame (item's Icon) — the 0x16 graphic id
    public string CustomName = "";
    // Bound owner carried WITH the stack while it sits on the ground, so a bonded item (a totem helm, a subpath
    // weapon) that its owner drops stays bound to them: whoever picks it up gets it still owned by the dropper
    // and can't equip it. Empty for ordinary loot. See ItemDef.Bonded / Session.GivePlaced.
    public string Owner = "";

    // LOOTER LOCK (RTK flooritem_data.looters[] + .timer, gated by player.lua's canLoot/isYours). 0 = ordinary
    // free-for-all floor loot, which is almost everything. Non-zero means this stack was torn off a corpse and
    // belongs to that player id until LockedUntil passes — nobody else can pick it up, filch it, or grab it out
    // from under them, and the owner can pull it back from two tiles away via F1 "Recover Death Pile" even with
    // a would-be thief parked on top of it. RTK stores the drop time and adds 300s at every read; an absolute
    // Environment.TickCount64 deadline is the same rule with the arithmetic done once.
    public uint LooterId;
    public long LockedUntil;

    /// <summary>True while this stack is still reserved for someone other than <paramref name="pickerId"/>.</summary>
    public bool LockedAgainst(uint pickerId)
        => LooterId != 0 && LooterId != pickerId && Environment.TickCount64 < LockedUntil;

    /// <summary>True if this stack is <paramref name="pickerId"/>'s own death pile and the lock is still live —
    /// the only thing "Recover Death Pile" will pick up (RTK <c>isYours</c>, which tests <c>looters[1]</c>).</summary>
    public bool BelongsTo(uint pickerId)
        => LooterId != 0 && LooterId == pickerId && Environment.TickCount64 < LockedUntil;
}

/// <summary>A hidden hazard placed by a Rogue trap spell (RTK NPCs/trap/rogue_traps/*): invisible — no
/// ground graphic is ever drawn for it (unlike <see cref="GroundItem"/>) — until a mob steps onto its
/// tile, at which point its effect fires once and it's removed. See <see cref="World.PlaceTrap"/>/
/// <c>TrapAt</c> and Session.CastTrap/CastSpotTraps.</summary>
public sealed class Trap
{
    public uint   Id;
    public ushort X, Y;
    public string Kind = "";   // "dart"/"snare"/"repeating"/"flash"/"spear"/"poison"/"death"/"sleep"/"bladestorm", Spy
                               // kit's "toxic_spray"/"tripwire"/"smoke_screen" (World.IsSpyTrap), or Ranger's "pit_trap"
                               // (World.PitTrap; the four are World.IsSubpathTrap)
    public uint   OwnerId;     // caster's player id — credited with any exp from a trap kill
    public long   ExpiresAt;   // 0 = never (the 8-kind hazard family); "bladestorm" auto-clears if untriggered
}

/// <summary>Where a player was standing when an arrival began — the tile it still holds while the move is
/// in flight. Only <see cref="ArrivalPolicy.AdjacentFreeElseStack"/> consults it; see
/// <see cref="World.PlacePlayer"/>'s <c>from</c> parameter for why it has to.</summary>
public readonly record struct FromTile(ushort Map, ushort X, ushort Y);

/// <summary>How <see cref="World.PlacePlayer"/> turns a requested arrival tile into the one the player
/// actually lands on.
///
/// <para><b>Every arrival in the tree uses one of these two, and the default is the original game's own
/// behaviour.</b> The source check #99 waited on is answered: when a warp put a player on a tile another
/// player already stood on, the game stacked them — it neither refused the warp nor moved the arriver to a
/// neighbouring tile (the project owner, from play, 2026-09-06; <c>game-data/Sources.csv</c>
/// <c>live-2026-09-06-arrival-stack</c>; structurally consistent with RTK's <c>pc_warp</c>, which applies
/// no occupancy test, and with its per-cell player list holding several — docs/4.x/Protocol.md §11g). So
/// <see cref="Clamp"/> is the game-faithful default, there is deliberately no Refuse and no "step aside on
/// a warp", and <see cref="AdjacentFreeElseStack"/> is a GM-tool convenience — a house choice for
/// <c>@approach</c>/<c>@bring</c>/<c>@npc</c>, not a game fact.</para>
/// </summary>
public enum ArrivalPolicy
{
    /// <summary>Take the requested tile, clamped to the map, occupied or not. What every warp, scripted-tile
    /// entrance, world-map hop, Gateway and GM teleport has always done — a bounds clamp was the whole of the
    /// validation. Two players through one door land on the same tile, as they always have — and as they did
    /// in the original game (#99).</summary>
    Clamp,

    /// <summary>The first free cardinal neighbour of the requested tile (N/E/S/W in that order), else the
    /// requested tile itself. Free = in bounds, not blocked by ground or an object wall, and holding neither
    /// a mob nor a player. <c>@approach</c> and <c>@bring</c> only, which is where this search used to live
    /// as <c>Session.ApproachTile</c> — reading through <c>World.PeerAt</c> and <c>World.MobAt</c>, each its
    /// own acquisition, with the write happening later under none of them.</summary>
    AdjacentFreeElseStack,
}

/// <summary>A peer and the tile it was standing on when <c>_lock</c> was last held — what the viewport
/// reconcile gates on. It exists because the reconcile CANNOT read <c>Session.PlayerX</c>/<c>PlayerY</c>
/// itself: those are two separate <c>ushort</c> reads of another session's character, and every writer of
/// them holds <c>_lock</c>, so a reader outside it can see one tile's X against the previous tile's Y. The
/// player list was already snapshotted under the lock and used outside it; this carries the coordinates in
/// the same snapshot, so the gate now sees the map exactly as the lock saw it rather than a mixture.
///
/// <para><c>Id</c> rides along for the same reason the tile does, plus one of its own. The reason: it is
/// another session's field, and the snapshot under <c>_lock</c> is where this struct reads another session's
/// fields — nowhere else. The one of its own is cost, and the cost is small: reading
/// <c>peer.Session.PlayerId</c> dereferenced somebody else's <c>Session</c> and then its <c>Character</c>,
/// and an in-process A/B of the two capture passes put that at <b>0 ns per viewer per beat on a compact
/// heap and about 450 ns in Debug (90 ns in Release) on a scattered one</b>. It is NOT the 10.2 µs / 29.5%
/// of <c>(3) viewports</c> the viewport profile's ablation arm reports: that arm deletes the whole capture
/// pass — the interface enumeration, the struct build and the array store — of which this read is a few
/// percent (<c>briefs/reports/peertile-id-opus.md</c>). That pass is gone now, and this struct is why:
/// because a <c>PeerTile</c> carries everything the decision needs, <c>SyncPeers</c> takes the snapshot
/// array itself and decides straight off it under its one acquisition, with nothing copied out first.
/// <c>Session.PlayerId</c> is <c>_char.Id</c>, assigned
/// once in <c>Session.HandleArrival</c> before the session joins any map and never written again, so the id
/// this snapshot takes is the id the sweep would have read.</para>
///
/// <para>The three-argument form is the cold-path and test shape: it fills <c>Id</c> from the session it is
/// given. Every hot construction site — the two in <c>EnterMap</c>, the one in <c>View</c>, the one in
/// <c>ReconcileViews</c> — names the id explicitly, under the same <c>_lock</c> acquisition that reads
/// <c>PlayerX</c>/<c>PlayerY</c>.</para></summary>
public readonly record struct PeerTile(Session Session, uint Id, ushort X, ushort Y)
{
    /// <summary>The peer's own id, read off the session. Cold paths and tests only: a caller inside
    /// <c>World._lock</c> should pass the id it already has.</summary>
    public PeerTile(Session session, ushort x, ushort y) : this(session, session.PlayerId, x, y) { }
}

/// <summary>A BORROWED view snapshot: the peers and mobs <see cref="World.ViewPooled"/> copied out of the
/// world lock, in two <see cref="System.Buffers.ArrayPool{T}"/> buffers that belong to the pool and not to
/// the caller. <see cref="Peers"/> and <see cref="Mobs"/> are at LEAST as long as the fill, so every reader
/// walks <see cref="PeerCount"/> / <see cref="MobCount"/> and never <c>Length</c>; the tail is the previous
/// tenant's and is not ours to read.
///
/// <para>It is a loan with one rule: hand it back with <see cref="World.ReturnView"/> in a <c>finally</c>,
/// and let nothing outlive that — no field, no closure, no deferred send, no slice kept past the call. A
/// read after the return is silent, because the buffer is by then legitimately somebody else's, which is
/// why this type exists rather than a bare pair of arrays.</para></summary>
public readonly struct ViewSnapshot
{
    /// <summary>The peers, excluding the viewer, in the map roster's order. Valid for <see cref="PeerCount"/>
    /// entries.</summary>
    public readonly PeerTile[] Peers;
    /// <summary>How many of <see cref="Peers"/> the fill wrote.</summary>
    public readonly int PeerCount;
    /// <summary>The map's mobs, in the map roster's order. Valid for <see cref="MobCount"/> entries.</summary>
    public readonly Mob[] Mobs;
    /// <summary>How many of <see cref="Mobs"/> the fill wrote.</summary>
    public readonly int MobCount;

    internal ViewSnapshot(PeerTile[] peers, int peerCount, Mob[] mobs, int mobCount)
    {
        Peers = peers; PeerCount = peerCount; Mobs = mobs; MobCount = mobCount;
    }
}

/// <summary>Why <see cref="World.TryMovePlayer"/> refused a step. Flags, not a single value, because the
/// walk log prints " mob" and " player" INDEPENDENTLY and always has: a mob and a player can share a tile
/// (a warp lands players on object tiles, and nothing keeps a summon off an occupied one), so collapsing
/// the two would quietly change a log line the ticket counts as behaviour.</summary>
[Flags]
public enum BlockReason
{
    None = 0,
    /// <summary>A living mob stands there — NPCs included, exactly as <see cref="World.MobAt"/> counts them.</summary>
    Mob = 1,
    /// <summary>A living OTHER player stands there. The mover is living too; the dead never block the living.</summary>
    Player = 2,
    /// <summary>Another GHOST stands there, and the mover is a PvP ghost. Logged as " player", like the living
    /// case — the log has never distinguished them.</summary>
    Ghost = 4,
    /// <summary>A running Human Barrier closes the tile to every player but its caster (#334). Logged as
    /// " barrier".</summary>
    Barrier = 8,
}

/// <summary>One Barrier or Human Barrier cast (#334): the four tiles beside (<see cref="X"/>, <see cref="Y"/>),
/// where the Poet stood when it was cast, are closed until <see cref="Until"/> (a <c>TickCount64</c>). They
/// stay where they were cast: the Poet may walk away, and the tiles stay closed. <see cref="Players"/> false is
/// Barrier, which closes them to creatures; true is Human Barrier, which closes them to every player but
/// <see cref="CasterId"/>. Kept in <c>World.MapState.Barriers</c>, under <c>World._lock</c>.</summary>
internal readonly record struct BarrierZone(ushort X, ushort Y, long Until, bool Players, uint CasterId)
{
    /// <summary>Is (<paramref name="x"/>, <paramref name="y"/>) one of the four tiles beside the cast tile? The
    /// cast tile itself is not: the barrier surrounds the Poet.</summary>
    public bool Closes(int x, int y) => Math.Abs(x - X) + Math.Abs(y - Y) == 1;
}

/// <summary>
/// The single shared game world: every connected player and every live mob, grouped by map. One
/// instance is created in <see cref="TkListener"/> and handed to every <see cref="Session"/>, so all
/// clients observe the SAME entities — players see each other, and everyone fights the same mobs.
///
/// This replaces the old per-Session mob ownership for GAMEPLAY mobs (@summon / @rabbit). The debug
/// lab (look-lab dummies, monster/colour sweeps) stays session-local — those are single-screen
/// diagnostics that shouldn't broadcast to the whole map.
///
/// Threading: all collections are guarded by <c>_lock</c>. Socket writes NEVER happen while holding
/// the lock — broadcasts snapshot the recipient list under the lock, then send outside it, and each
/// send is exception-guarded so a peer whose socket just closed can't break a broadcast.
///
/// <c>_lock</c> is the INNER lock of the two the server has: the order is a session's state monitor
/// first, then this one (#29, Server/Session.State.cs). Nothing may enter a session while holding
/// <c>_lock</c> — the tick obeys that by queueing every session-facing call and applying it after the
/// lock is released, and <see cref="HoldsWorldLock"/> is what lets the session side assert it.
/// </summary>
public sealed partial class World
{
    private readonly object _lock = new();

    /// <summary>Whether the calling thread is inside <c>_lock</c>. Exists for the lock-order assert on the
    /// session side (Session.EnterState): the pair of locks has one legal order and this is the only way the
    /// outer one can tell it is being taken second.</summary>
    internal bool HoldsWorldLock => Monitor.IsEntered(_lock);

    /// <summary>Run <paramref name="body"/> holding <c>_lock</c> — for the test that pins the lock-order
    /// assert. Production code never needs this: every path that wants the world lock is already inside
    /// this class. Kept next to the lock it takes rather than hidden in the test project, because a reader
    /// of <c>_lock</c> should be able to see everything that acquires it.</summary>
    internal void UnderWorldLockForTest(Action body) { lock (_lock) body(); }

    // internal, not private: MobTickContext (World.MobAiTick.cs) carries one, and a field on an internal type
    // cannot be of a private one. Still a World-only type by convention; nothing outside World constructs it.
    internal sealed class MapState
    {
        public readonly List<Session> Players = new();
        public readonly List<Mob> Mobs = new();
        public readonly List<GroundItem> Items = new();
        public readonly List<Trap> Traps = new();
        /// <summary>The Barrier and Human Barrier casts on this map (#334), written by <see cref="RaiseBarrier"/>
        /// and read by the tick's collision index (<c>MobTickContext</c>) and <see cref="TryMovePlayer"/>, all under
        /// <c>_lock</c>. Lapsed entries are pruned by the next cast here, and every reader skips them; nothing is
        /// drawn for one, so no <see cref="ViewGen"/> bump.</summary>
        public readonly List<BarrierZone> Barriers = new();
        /// <summary>How many player steps this map has ACCEPTED since the process started — a running total,
        /// never reset, published as <c>steps</c> in <c>run/viewport.json</c> so a reader differencing two
        /// documents gets the map's step RATE beside its in-view fraction. The two numbers together are what
        /// prices a per-map spatial index: the fraction says how much of the tick's sweep an index would
        /// skip, and the step rate says how many walk reconciles it would skip as well, which the holds on
        /// record (briefs/reports/hold-spread-2x2-opus.md) showed is the larger of the two once players move.
        ///
        /// <para>Written by <see cref="TryMovePlayer"/> on the branch that commits the position, inside the
        /// <c>_lock</c> acquisition that step already holds — so this is one add next to a write that was
        /// happening anyway, with no new lock, no new acquisition and no lock-order change. Read by
        /// <see cref="OnlineRegistry.PositionSurvey"/> under the same lock. Nothing on the tick path reads or
        /// writes it and no player can see it.</para></summary>
        public long Steps;

        /// <summary>How many times something a viewport sweep of this map READS has changed — the map's
        /// change generation, and the whole of what lets <see cref="ReconcileViews"/> skip a viewer.
        ///
        /// <para><b>What the sweeps read, which is what this counts.</b> <c>SyncPeers</c> reads the
        /// <see cref="Players"/> roster and each player's id and tile; <c>SyncMobs</c> reads the
        /// <see cref="Mobs"/> roster and each mob's tile; <c>SyncGroundItems</c> reads the
        /// <see cref="Items"/> roster and each item's tile. Every write of any of those bumps this, under the
        /// <c>_lock</c> acquisition that write already holds — the roster edits here in <c>World</c>, the
        /// mob step in <c>MobAiTick.StepMobTo</c>, and every player tile write through the one seam
        /// they all go through, <c>Session.SetPositionUnderWorldLock</c>. There is no new lock, no new
        /// acquisition and no lock-order change: the full inventory is in
        /// <c>briefs/reports/tick-sweep-skip-opus.md</c>.</para>
        ///
        /// <para><b>What it deliberately does NOT count</b> is anything PER-VIEWER: a viewer's own drawn-state
        /// store, its trap and warp markers, and a draw another session made on it directly. Those cannot be
        /// a property of the map, and they un-skip their own viewer through <c>Session.MarkSweepPending</c>
        /// instead.</para>
        ///
        /// <para><b>Which of these bumps is load-bearing, measured rather than assumed.</b> Every ADDITION
        /// and every MOVE is: nothing else tells a standing viewer that a mob spawned, an item dropped or
        /// anything moved, and deleting any one of those bumps turns a fact red. Every REMOVAL is a second
        /// guard: a player leaving, a mob dying and an item being picked up all broadcast
        /// <c>Session.DespawnEntity</c>, which flags each recipient by itself, so those bumps can be deleted
        /// with the suite still green. They stay because the roster is what moved, and a reader of this
        /// counter should not have to know which callers happen to broadcast afterwards. The falsification
        /// table in <c>briefs/reports/tick-sweep-skip-opus.md</c> has the case-by-case result.</para>
        ///
        /// <para>A <c>long</c>, read and written only under <c>_lock</c>, and compared, never interpreted:
        /// one beat's captured value against what the viewer recorded last beat. It cannot wrap in any
        /// lifetime this process has.</para></summary>
        public long ViewGen;
        // The weather state (0 clear / 1 rain / 2 snow) last BROADCAST to players on this map. Not the source
        // of truth — that is the deterministic WeatherModel (+ any zone override) — this is the cached
        // last-sent value the tick compares against on a period rollover to decide whether to re-broadcast.
        public byte Weather;
    }
    private readonly Dictionary<ushort, MapState> _maps = new();

    /// <summary>Bump <see cref="MapState.ViewGen"/> for <paramref name="mapId"/> — "something a viewport
    /// sweep of this map reads has changed". For the change sites that do NOT already have the
    /// <see cref="MapState"/> in hand; the ones that do increment the field directly.
    ///
    /// <para><b>The caller already holds <c>_lock</c>, and that is the point.</b> Every change source this
    /// counts is a write the world lock already serialises, so the bump rides in an acquisition that exists —
    /// no new lock, no wider hold, no order change. The assert pins it, the same way
    /// <c>Session.SetPositionUnderWorldLock</c>'s does for the write it sits next to.</para>
    ///
    /// <para>A map with no <see cref="MapState"/> has no roster, so no sweep reads it and there is nothing to
    /// bump — the same "an unknown map blocks nothing" case <see cref="TryMovePlayer"/> has.</para></summary>
    internal void BumpViewGenUnderWorldLock(ushort mapId)
    {
        Debug.Assert(Monitor.IsEntered(_lock),
            "map view generation bumped outside World._lock — it is read with the roster in one acquisition " +
            "by ReconcileViews, so a bump outside the lock is a viewer skipping a change it never saw.");
        if (_maps.TryGetValue(mapId, out var m)) m.ViewGen++;
    }

    /// <summary>A map's current <see cref="MapState.ViewGen"/>, for the facts that have to arrange two maps
    /// at the SAME generation so that only the map id can tell them apart. No production caller.</summary>
    internal long ViewGenForTest(ushort mapId) { lock (_lock) return _maps.TryGetValue(mapId, out var m) ? m.ViewGen : 0; }

    /// <summary><see cref="BumpViewGenUnderWorldLock"/> with its own acquisition, for the facts that arrange
    /// a generation. No production caller: production always holds the lock already.</summary>
    internal void BumpViewGenForTest(ushort mapId) { lock (_lock) BumpViewGenUnderWorldLock(mapId); }

    // The two spawn systems — the POINT roster (Spawn) and the GROUP roster (SpawnGroup) — and everything
    // that builds, materialises and refills them live in World.SpawnDirector.cs (#37). Constructed before
    // PopulateSpawns, which is the first call into it.
    private readonly SpawnDirector _spawnDirector;
    private long _tick;                                                  // heartbeat counter (TickMs each)
    private long _slowTicks;                                             // beats the watchdog reported (see SlowTicks)
    private long _sweepViewers;                                          // (viewer, beat) pairs considered (see SweepViewers)
    private long _sweepsRun;                                             // of those, the ones that swept (see SweepsRun)

    /// <summary>The world's ONE natural-regeneration clock, in accumulated tick milliseconds since start.
    /// Advanced by <see cref="TickMs"/> once a beat in <see cref="FlushTick"/> and wound back by
    /// <see cref="RegenIntervalMs"/> on the beat it crosses it, so the regen beat lands on the first beat at
    /// or after each 25,000 ms boundary — the 76th beat of 333 ms, then every 75th beat after that.
    ///
    /// <para><b>Why it is the world's and not each player's.</b> The original game ran one global clock:
    /// RTK's <c>Player.regen</c> fires on <c>timerTick%50</c> at 0.5 s a tick, i.e. every 25 s, for
    /// everyone at less than full at once. Damage does not restart it and two players in a fight always tick
    /// together. Ours had drifted to a per-player accumulator that restarted whenever the player dropped
    /// below full, which made the first tick after damage always exactly 25 s later and meant a player
    /// chipped every 20 s never regenerated at all. This is the return to the original behaviour, decided by
    /// the project owner on 2026-09-19, and it is the reason <c>RegenTick</c> can decide from thread-local state whether
    /// it has anything to do this beat.</para>
    ///
    /// <para>Tick-thread-owned: written and read only inside <see cref="FlushTick"/>.</para></summary>
    private long _regenClockMs;

    /// <summary>RTK's regen period (<c>timerTick%50</c> at 0.5 s a tick). It lived on <c>Session</c> while
    /// every player carried their own accumulator; the clock is the world's now, so this is too, and there
    /// is one definition of it.</summary>
    private const int RegenIntervalMs = 25_000;

    /// <summary>Test-only view of <see cref="_regenClockMs"/>, so a fact can start the clock from a known
    /// position rather than from wherever an earlier test in the collection left it. Nothing in the server
    /// writes it outside <see cref="FlushTick"/>.</summary>
    internal long RegenClockMsForTest
    {
        get => _regenClockMs;
        set => _regenClockMs = value;
    }

    /// <summary>Beats run since start, and beats the slow-tick watchdog reported, for <c>run/status.json</c>.
    ///
    /// <para>Both load reports state their headline — the share of beats that were slow — as a wall-clock
    /// span divided by the 333ms period, because the server published no tick total. These two make it a
    /// count: <c>slowTicks / ticks</c> over a span is measured rather than derived, and the derivation can
    /// finally be checked against it.</para>
    ///
    /// <para>Written by the tick thread only, read on the status writer's thread every 10s.
    /// <c>Volatile.Read</c> rather than a plain read, so the status thread cannot be handed a hoisted value
    /// indefinitely; a 64-bit read is already atomic on every runtime this targets, so there is no tearing to
    /// guard against and nothing to interlock on this side. Not under <c>_lock</c> on purpose — the status
    /// writer must never queue behind the world.</para></summary>
    internal long Ticks     => Volatile.Read(ref _tick);
    internal long SlowTicks => Volatile.Read(ref _slowTicks);

    /// <summary>How many (viewer, beat) pairs the tick has CONSIDERED, and how many of those actually SWEPT,
    /// since process start, for <c>run/status.json</c>.
    ///
    /// <para><b>Why they exist.</b> With <c>P1998_TICK_SWEEP_SKIP</c> on, a beat's <c>(3) viewports</c> cost
    /// is proportional to the viewers that were DIRTY on that beat, and nothing the server published said how
    /// many that was. Two Release hold assignments (<c>briefs/reports/hold-sweep-skip-opus.md</c> and
    /// <c>hold-sweep-skip-2-opus.md</c>) found the phase repeating to 3-4% run to run with the skip OFF and
    /// 22% (standing clump) to 61% (walking spread) apart with it ON, and could only label the explanation a
    /// hypothesis: the load script does not hold the dirty count fixed. These two counters make it a reading.
    /// Over a span, the share of considered viewers the skip saved is
    /// <c>1 - Δ sweepsRun / Δ sweepViewers</c>.</para>
    ///
    /// <para><b>What each counts.</b> <see cref="SweepViewers"/> is incremented once per populated map per
    /// beat by that map's player count, before the per-player loop, so it is "one per player of every map the
    /// snapshot carried, every beat" whether or not that player was then skipped. <see cref="SweepsRun"/> is
    /// incremented at the END of a viewer's three sweeps, from <c>Session.EndTickSweep</c> — the same point
    /// as the per-session <c>_tickSweeps</c>, and for the reason that field's doc gives: a counter at
    /// <c>BeginTickSweep</c>'s DECISION can read zero while every sweep runs. With the switch off the two are
    /// equal on every beat; with it on, a still map grows the first and not the second.</para>
    ///
    /// <para>Written by the tick thread only and by nothing else — <c>ReconcileViews</c> and
    /// <c>EndTickSweep</c> are both on it — so these are plain increments with no lock and no
    /// <c>Interlocked</c> on a path that runs once per viewer per beat. Read on the status writer's thread
    /// with <c>Volatile.Read</c>, exactly as <see cref="Ticks"/> and <see cref="SlowTicks"/> are and for the
    /// same reason.</para></summary>
    internal long SweepViewers => Volatile.Read(ref _sweepViewers);
    internal long SweepsRun    => Volatile.Read(ref _sweepsRun);

    /// <summary>Count one viewer whose three tick sweeps have just RUN. Called by
    /// <c>Session.EndTickSweep</c>, on the tick thread, through the <c>World</c> the session already holds.
    ///
    /// <para><b>Why here and not in <see cref="ReconcileViews"/>'s lambda.</b> That lambda is <c>static</c>
    /// and takes its arguments as a tuple precisely so the loop allocates neither a display class nor a
    /// delegate per viewer per beat (PR #245's follow-up 3, 104 B measured). Reaching the world from inside
    /// it would mean either capturing <c>this</c> — which un-statics the lambda and restores the allocation —
    /// or a seventh field on a tuple that is copied for every viewer of every map on every beat. The session
    /// already has <c>_world</c> in a field, so routing the increment through it adds nothing to either.</para></summary>
    internal void CountSweepRunOnTickThread() => _sweepsRun++;

    /// <summary>World heartbeat period, and the unit every mob timer accumulates in — so it is also the
    /// FLOOR on how often any creature can act. Override with <c>P1998_TICK_MS</c>.
    ///
    /// <para><b>Was 600ms; now 333.</b> Lowering it does not make anything faster: every timer is
    /// <c>timer += TickMs</c> compared against a per-mob interval in real milliseconds, and the leftover is
    /// carried (<c>timer -= interval</c>) rather than reset, so a 2000ms creature still moves every 2000ms
    /// on average either way. What changes is GRANULARITY — the smallest action interval the world can
    /// express at all. At 600 the fastest possible creature managed 1.7 actions/sec, which could not
    /// represent Sute, who was observed moving and striking twice a second in a 333/333/rest rhythm (see
    /// Server/SuteAi.cs). 333 divides that rhythm exactly.</para>
    ///
    /// <para>Cost: the tick body runs ~1.8x as often. It only walks maps that have players on them, and the
    /// slow-tick watchdog (<see cref="SlowTickMs"/>, which scales off this) has never fired in this repo's
    /// logs, so the headroom was there. If it starts firing, raise this back — nothing but Sute's cadence
    /// depends on the smaller value.</para></summary>
    private static readonly int TickMs = ServerConfig.Current.TickMs;

    /// <summary>Poison/venom damage cadence, RTK's <c>while_cast_1500</c>. Shared by the mob DoT, the Rogue
    /// poison trap and the player-side venom, so the rate NexusAtlas quotes ("1000 damage a second") converts
    /// against one number in one place — see <see cref="Session.ReceivePoison"/>.</summary>
    public const int PoisonTickMs = 1500;
    // Fallback respawn delay for a spawn POINT whose creature somehow carries no SpawnTime (~18s, derived
    // from TickMs so it stays 18s whatever the heartbeat is). Every mob in the table does carry one, so this
    // is a floor, not the cadence: a point's
    // real delay is its own MobDef.SpawnTime — see SpawnDirector.SpawnTicksFor.
    private static readonly int RespawnTicks = Math.Max(1, 18_000 / TickMs);
    // How often the batch-refill sweep runs, in ticks. RTK's spawner NPC is an actiontime-driven NPC firing
    // about once a second; its timers are whole seconds and the shortest in the table is 2s, so sampling at
    // ~1.2s costs nothing and keeps the sweep off the per-beat hot path.
    private static readonly int BatchSweepTicks = Math.Max(1, 1_200 / TickMs);
    // Placement attempts per mob when filling a group, as a multiple of the group's cap. RTK gives up the
    // same way (`if fail >= maxMobs[z] * 4 then` treat the mob as done), which is what stops a boxed spawn
    // from spinning forever when the box is mostly wall.
    private const int PlacementTriesPerMob = 4;
    // How far (Chebyshev) FreeSpawnTile looks past a spawn tile for an open one before it accepts the spawn tile
    // itself. SpawnDirector's off-map room rule (#333) reads the same reach: a boss room with no tile of its map
    // this close is one FreeSpawnTile would build outside the map.
    private const int SpawnTileRadius = 2;
    // How far a mob may wander from its spawn tile (Chebyshev). Kept small so town critters hug their
    // spawn points instead of clustering into a dense knot that constantly overlaps on screen.
    private const int WanderRadius = 2;
    // Farthest (Chebyshev, from its home tile) a provoked mob will chase an attacker before giving up and
    // resuming normal wandering — bigger than WanderRadius so a fight can range beyond the idle-hop leash,
    // but still bounded so a player can outrun pursuit rather than being chased across the whole map.
    //
    // RTK doesn't home-leash an ACTIVE chase at all: mob_ai_basic.move forces `mob.returning = false` the
    // moment `mob.target ~= 0`, so its `retDist` governs only idle wandering, and a mob with a target chases
    // it via FindCoords with no distance cap (target loss is by threat/area, not range). We keep a bound
    // deliberately, but it MUST exceed the notice box below (NoticeX/Y) — otherwise an aggressive mob that
    // spots a player at the screen edge would acquire and, on the very same tick, drop the target for being
    // past the leash, jittering in place instead of pursuing. Sized to a full screen so an off-screen
    // aggressive mob gives a real chase (user, 2026-08-24: "just off screen should pursue"); leading it this
    // far from its den still shakes it.
    private const int ChaseLeash = 16;
    // Mythic boss animations RTK hardcodes rather than carrying per-boss: Last Stand flashes 11
    // (Spells/last_stand.lua `sendAnimation(11)`), a curse shrug flashes 10 and plays no sound
    // (mob_ai_mythic.move). The heal animation/sound pair IS per-boss and lives in MobBosses.csv.
    private const int LastStandAnim  = 11;
    private const int CurseShrugAnim = 10;
    // The Ice Beast questline (Northeast Koguryo, map 3040). RTK Mobs/ice_beast.lua ends its `move` hook by
    // checking the lava row at 29-30 x 14-16 and, if it is standing on it, removing its own full health — so
    // you defeat the beast by luring it onto the lava, not by out-trading its 300k one-shot. It is UNLEASHED for
    // that (see the chase-leash test in Tick), because its spawn (29,3) is farther than ChaseLeash from the
    // lava and an ordinary mob would give up the pursuit long before reaching it. IceBeastMeltAnim is the
    // burst it flashes as it melts (RTK's sendAnimation on the same tiles).
    private const ushort IceBeastMap      = 3040;
    private const string IceBeastKey      = "ice_beast";
    private const int    IceBeastMeltAnim = 5;
    private static bool IsIceBeastLava(int x, int y) => (x == 29 || x == 30) && y >= 14 && y <= 16;
    // How far (Chebyshev, from the mob's CURRENT tile) an aggressive mob (MobDef.Aggressive, RTK MobBehavior==1)
    // scans for an unprovoked target each move tick — RTK's mob_find_target runs over a full-screen-ish area;
    // this is scoped to roughly what the player can see on their own screen (17x15 viewport, Session.InView).
    // Kept for the PET foe scan (a summon fighting what's around it), which is about reach, not screen edges.
    private const int AggroRadius = 8;
    // The box (half-extents from the mob's tile) in which a mob NOTICES a player — for unprovoked aggro, for
    // re-picking a reachable target when walled off, and for keeping threat on someone at the screen edge.
    // The drawn viewport is 19x17 (x±9, y±8 — Session.SayHalfW/H, the same rect the client renders), so a
    // plain square AggroRadius=8 fell two tiles short horizontally: a mob you could plainly see at the left or
    // right edge sat inert until you stepped closer, and one just off-screen never pursued. This is the
    // viewport PLUS one tile on every side, so behaviour reaches at least a tile past what's visible (user,
    // 2026-08-24: "extend outside the viewport slightly … at least 1 tile"; "just off screen should pursue").
    private const int NoticeX = Session.SayHalfW + 1;   // 10 — a tile past the horizontal draw edge
    private const int NoticeY = Session.SayHalfH + 1;   // 9  — a tile past the vertical draw edge
    /// <summary>Is (px,py) inside a mob at (mx,my)'s notice box — the drawn viewport plus a one-tile margin?</summary>
    private static bool Notices(int mx, int my, int px, int py) =>
        Math.Abs(px - mx) <= NoticeX && Math.Abs(py - my) <= NoticeY;
    // The mirror of AggroRadius for PREY (MobDef.Flees — rabbit, blue rooster): how close (Chebyshev) a player
    // gets before the creature starts backing away. Deliberately much shorter than the aggro scan — a rabbit
    // notices you when you're nearly on top of it, not from across the screen, otherwise a town full of them
    // would evaporate the moment anyone walked in. Doubled while the creature is panicking (see PanicMs), so a
    // swing sends it running from further off than a stroll past does.
    private const int FleeRadius = 2;
    // ---- flee DART sizes (see Dart) -------------------------------------------------------------
    // Tiles covered in ONE move turn by a fleeing creature. RTK expresses running away as several
    // `mob:move()` calls in a single script invocation rather than as a shorter timer, so distance per turn
    // — not milliseconds per tile — is the dial, and it is the same dial for all three fleers.
    //
    // PREY (rabbit, blue rooster): two tiles, from the user's own observation of the real game (2026-08-22) —
    // a rabbit you walk up to or swing at hops two spaces at once. No RTK reference exists for this: RTK
    // gives a rabbit a wolf's AI, and the Flees flag is ours (Content.LoadMobFlees).
    //   * A rabbit (MoveTime 3000) covers exactly the same ground per second as the shortened-timer version
    //     this replaced (2 tiles / 3000ms == 1 tile / 1500ms), so it is no harder to catch — only the motion
    //     changes, from a hurried walk to the hop it should always have been.
    //   * A blue rooster (MoveTime 500, already under the 600ms heartbeat) genuinely does get quicker. Its
    //     flee used to be expressible only as direction, because one tile per tick was the hard ceiling on
    //     pace; distance per turn has no such ceiling.
    public const int PreyDartTiles = 2;
    // THE WOUNDED ROUT (nine-tailed fox, Maletic, Citelam): three, straight off RTK's own
    // `mob:move() mob:move() mob:move()`. Previously approximated by stepping every heartbeat instead.
    public const int RoutDartTiles = 3;
    // How long a prey creature stays spooked after a player swings at it or damages it (World.Spook /
    // TryDamage), refreshed by each further hit. Panic doesn't lengthen the dart — it widens the
    // notice radius and keeps the creature running after you stop chasing, so a swing sends it properly away
    // instead of it settling down the moment you step back.
    private const int PanicMs = 4000;

    // Ground-item forage spawns (RTK itemspawner.lua): keep up to Max stacks of a gatherable item scattered on
    // passable tiles within a box, topped up periodically. Chestnuts fill the Kugnae farm (map 0) and a Buya
    // patch (map 330) — the tutorial's stage-3 gather. A stack is MinQty..MaxQty items on one tile.
    // Forage spawn boxes are data-driven (game-data/ForageAreas.csv -> Content.ForageAreas); hot-reloads
    // via @reload. See TopUpForageLocked.
    private static readonly int ForageTicks = Math.Max(1, 18_000 / TickMs);   // top up ~every 18s, like RTK's periodic itemspawner

    // The world calendar (opcode 0x20) — the cached in-game date, the hour-rollover watch phase (1.6) drives,
    // and the @clock pin — lives in World.WorldClock.cs (#37). Constructed before the constructor's first
    // Sync, which is the first call into it. Sessions read it through World.Clock.

    // The weather (opcode 0x1F / RTK clif_sendweather) — the admin overrides, the per-map resolution and the
    // period-rollover sweep phase (1.7) drives — lives in World.WeatherService.cs (#37). World keeps the
    // per-map last-sent cache (MapState.Weather) and the flush that puts 0x1F on the wire; sessions read it
    // through World.Weather.
    private static readonly int AdviceTicks = Math.Max(1, 900_000 / TickMs);   // ~15 minutes — the "Listen to advice" hint cadence (RTK pc_timer, NOT weather)

    // Effects raised from inside the lock (a boss shrugging off a killing blow, say) and flushed by the next
    // Tick — TryDamage can't broadcast where it stands, and its callers only know how to draw the damage.
    private readonly List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> _deferredFx = new();
    // Sprung traps whose spot-traps MARKER still has to be rubbed out on every client that revealed it (RTK
    // removeTrapItem, called by every trap NPC right before it deletes itself). Filled from inside the tick's
    // _lock by TriggerTrapLocked, flushed with _deferredFx once the lock is released — the clear is socket I/O.
    private readonly List<(ushort map, uint trapId)> _deferredTrapClears = new();
    // Pit trap strikes sprung by a creature: for a beat's trap-damage list, the index of each strike in it and the strike's
    // figure as it sprang (the owner's half max vita plus half max mana, the creature's armour and sleep amplifier), before
    // the last-vita rule. Filled from inside the tick's _lock by SpringPitTrapLocked, which queues the strike at its own
    // place in that list; read by FlushTick, which lands that entry through ApplyPitStrike, so the rule reads the vita the
    // creature has when the strike lands (#423's review, F2; its re-check, R1 and R2). Weak on the list: a list nothing
    // flushes (a test's) takes its entry with it.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<(ushort map, Mob mob, int dmg, uint ownerId)>,
                                                                          Dictionary<int, double>> _pitStrikeFigures = new();

    // Lua mob hooks raised from inside the lock, run by the next Tick OUTSIDE it. This queue is the whole
    // reason MobScript is safe: a hook is free to speak, heal, vanish or touch a player's quest registry,
    // all of which re-enter the world — doing that while still holding _lock would deadlock it.
    private readonly List<(string key, string hook, ushort map, Mob mob, Session? actor)> _hooks = new();

    /// <summary>Queue a Lua AI hook for the creature, if it defines that hook. Cheap (one hash lookup) for
    /// the overwhelming majority of mobs, which define none. Safe to call under <c>_lock</c>.</summary>
    private void QueueHook(string hook, ushort map, Mob mob, Session? actor)
    {
        if (MobScript.Has(mob.Key, hook)) _hooks.Add((mob.Key, hook, map, mob, actor));
    }

    /// <summary>Point a creature at whoever has earned it (RTK <c>threat.calcHighestThreat</c>). Two rules,
    /// and the order is the point:
    /// <list type="number">
    /// <item><b>Cornered.</b> If the mob is boxed in by players and the one it is fighting isn't within
    /// reach, it turns on the highest-threat player it CAN reach. This is what stops a mob standing in a
    /// crowd swinging uselessly at someone behind a wall.</item>
    /// <item><b>Otherwise</b> it fights whoever has hurt it most, anywhere in sight.</item>
    /// </list>
    /// Threat only ever accrues from damage, so this can never make a mob attack an innocent bystander —
    /// the worst it does is move aggro between people already in the fight, which is the intent (a group
    /// CAN peel a mob off whoever pulled it, by out-damaging them).
    /// <para>Callers hold <c>_lock</c>. Players who have left the map simply aren't considered; their threat
    /// stays banked in case they come back, exactly as RTK's per-mob table does.</para>
    /// <para>A session a second login has replaced (<see cref="Session.IsReplaced"/>) is treated as already
    /// gone, at all three reads below (#168 item 5): it is not the current target, it does not corner the
    /// mob, and it is never the pick. All three are needed together. Skipping it at the current-target read
    /// alone would let it still corner the mob, which confines the pick to arm's reach and can keep the mob
    /// off a live player it would otherwise have turned to. Each is one volatile read with no session
    /// monitor, made only after the existing cheaper tests have passed.</para></summary>
    private void RetargetByThreat(MapState m, Mob mob)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        bool Adjacent(Session p) =>
            (p.PlayerX == mob.X && Math.Abs(p.PlayerY - mob.Y) == 1) ||
            (p.PlayerY == mob.Y && Math.Abs(p.PlayerX - mob.X) == 1);

        // Rule 1's precondition: someone is in arm's reach and the current target is not.
        bool cornered = false;
        if (mob.TargetId != 0)
        {
            var current = m.Players.FirstOrDefault(p => p.PlayerId == mob.TargetId && !p.IsReplaced);
            if (current is null || !Adjacent(current))
                cornered = m.Players.Any(p => !p.IsDead && Adjacent(p) && !p.IsReplaced);
        }

        long now = Environment.TickCount64;
        Session? best = null;
        long bestThreat = 0;
        foreach (var p in m.Players)
        {
            if (p.IsDead) continue;
            // RTK's non-cornered scan is `mob:getObjectsInArea(BL_PC)` — what the creature can see, not the
            // whole map. Without the bound a mob would swap onto someone who hurt it once and then walked to
            // the far side of the level, and chase a player it has no way of knowing is there.
            if (!Notices(mob.X, mob.Y, p.PlayerX, p.PlayerY)) continue;
            if (cornered && !Adjacent(p)) continue;
            if (mob.HasForgotten(p.PlayerId, now)) continue;   // Amnesia: this one isn't here as far as it knows
            long t = mob.ThreatOf(p.PlayerId);
            if (t > bestThreat && !p.IsReplaced) { bestThreat = t; best = p; }
        }

        if (best is null || best.PlayerId == mob.TargetId) return;
        mob.TargetId = best.PlayerId;
        mob.TargetMobId = 0;
    }

    // Facing (0=N 1=E 2=S 3=W) toward a delta, preferring the larger axis — used to turn a mob to face
    // whatever it's about to melee.
    private static byte FaceDelta(int dx, int dy) =>
        Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? (byte)1 : (byte)3) : (dy >= 0 ? (byte)2 : (byte)0);

    // Disjoint entity-id pools so a player id can never collide with a shared-mob id.
    //   players:     1 ..            (bound to each client's camera via 0x05)
    //   world mobs:  100000 ..       (session-local debug dummies use their own 5000+ pool, invisible
    //                                 to other clients, so those ranges never need to be globally unique)
    //   ground items: 500000 ..    (disjoint from players + mobs so a floor-item id never collides)
    private uint _nextPlayerId = 1;
    private uint _nextMobId = 100_000;
    private uint _nextNpcId = 300_000;    // NPCs get their own id band (disjoint from mobs) so a click can tell them apart

    // The NpcDef each placed NPC was built from, so @reload can tell an edited row from an unchanged one
    // (see ReconcileNpcToggles). Guarded by _lock, same as the maps it mirrors.
    private readonly Dictionary<int, NpcDef> _npcPlaced = new();
    private uint _nextItemId = 500_000;

    /// <summary>The scheduled-restart clock (@restart, or the run/restart_at file a deploy writes). Kept on
    /// the World because a restart warning is a server-wide broadcast and the online roster lives here.</summary>
    public RestartSchedule Restarts { get; }

    /// <summary>Builds the world's in-memory state and NOTHING that runs on its own: no tick thread, no
    /// autosave sweep, no watchdog, no restart scheduler, no status writer. Everything with a heartbeat is
    /// started by the process entry point (<c>TkListener.StartWorld</c>, Net.cs) and a test does not start it
    /// — that split is what lets a test hold a real World without the server's background machinery attached
    /// to it.</summary>
    public World()
    {
        RegisterForScriptGateAssert(_lock);   // Debug only: lets Session.EnterScriptGate assert #90 (World.ScriptGateRegistry.cs)
        _spawnDirector = new SpawnDirector(this);
        Online = new OnlineRegistry(this);
        AutoSave = new AutoSaveLoop(this);
        Clock = new WorldClock(this);
        Weather = new WeatherService(this);
        PopulateSpawns();                 // build the persistent roster from Content.Spawns (needs Content.Load first)
        PopulateNpcs();                   // place the stationary NPCs (Content.Npcs) as non-fighting mobs
        // Derive the in-game calendar from the fixed real-world epoch. Under _lock like the two Populate
        // calls above it: nothing else can be holding it here (the world is still being constructed and no
        // thread can see it yet), and it is what lets WorldClock.Sync assert its caller holds the lock.
        lock (_lock) Clock.Sync();
        var (hour, day, year) = Clock.ClockNow;
        Log.Info($"=== clock: Yuri {year}, {Clock.SeasonName}, day {day}, hour {hour}:00");
        Restarts = new RestartSchedule(this);
    }

    private int _started;   // 0 until the host has started this world; makes a second start a no-op instead of a second tick thread

    /// <summary>Whether the host has started this world — i.e. whether this world has threads attached to the
    /// process. Public so a test can assert the guarantee the constructor makes, rather than trusting it.</summary>
    public bool IsStarted => Volatile.Read(ref _started) != 0;

    /// <summary>Claim the one-shot right to attach the background machinery to this world: true on the first
    /// call, false on every later one. The threads themselves belong to the process host
    /// (<c>TkListener.StartWorld</c>, Net.cs), but the guard stays here with the state it guards, so a second
    /// start is a no-op rather than a duplicate set of threads and <see cref="IsStarted"/> keeps answering
    /// "does this world have threads attached".</summary>
    internal bool MarkStarted() => Interlocked.Exchange(ref _started, 1) == 0;

    // ---- persistent spawn roster --------------------------------------------------------------

    /// <summary>Build the persistent spawn-point roster from the static table (<see cref="Content.Spawns"/>,
    /// fixed tiles) and the Lua area spawns (<see cref="Content.AreaSpawns"/>, a count of mobs per map/box).
    /// Runs once at startup (Content is already loaded). This only builds cheap point objects — no mob is
    /// instantiated and no map file is read until the first player enters that map (<see cref="SpawnDirector.EnsureMaterialized"/>),
    /// so the ~21k hunting-map mobs don't flood memory or stall boot. Dead points refill via <see cref="Tick"/>.</summary>
    private void PopulateSpawns()
    {
        int points, skipped, groups, capped, roomPoints, roomMaps;
        lock (_lock)
        {
            (points, skipped, groups, capped) = _spawnDirector.Build();
            (roomPoints, roomMaps) = (_spawnDirector.RoomPointCount, _spawnDirector.RoomMapCount);
        }
        Log.Info($"spawns: {points} spawn points (materialized lazily) across {_spawnDirector.PointMapCount} map(s)" +
                 $", {roomPoints} of them filed under their bosses' rooms on {roomMaps} map(s)" +
                 (skipped > 0 ? $" ({skipped} skipped — unknown map/mob)" : ""));
        Log.Info($"spawns: {groups} batch groups capping {capped} mobs across {_spawnDirector.GroupMapCount} map(s)");
    }

    /// <summary>Every tile on a map that something is standing on. Built once per refill sweep and updated as
    /// mobs are placed: the placement test runs up to four times the group's cap, and the big woodcutting maps
    /// cap at 500, so re-scanning the map's entity lists per attempt would be a quarter-million comparisons
    /// under the world lock. Caller holds <c>_lock</c>.
    ///
    /// <para><paramref name="arriving"/> is the tile of a player who is entering the map and is not on its
    /// player list yet, and null on every other path. <see cref="EnterMap"/> fills the room before it adds the
    /// newcomer to that list, and keeps that order on purpose (#122): the newcomer's tile is handed in here
    /// instead, so a creature, an ambush trap or a cold tile is never placed on the tile they arrive on.</para></summary>
    private HashSet<(int, int)> OccupiedTiles(ushort mapId, (int X, int Y)? arriving)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var map = Map(mapId);
        var taken = new HashSet<(int, int)>(map.Mobs.Count + map.Players.Count + 1);
        foreach (var m in map.Mobs) if (m.Alive) taken.Add((m.X, m.Y));
        foreach (var p in map.Players) taken.Add((p.PlayerX, p.PlayerY));
        if (arriving is { } a) taken.Add(a);
        return taken;
    }

    /// <summary>Place every stationary NPC (Content.Npcs) into the world as a non-fighting mob. NPCs ride
    /// the exact same 0x07 creature render + viewport streaming as a real mob (see Session.ShowMob/SyncMobs),
    /// so they render + stream for free; they simply never wander, never respawn, and can't be damaged
    /// (World.TryDamage rejects <see cref="Mob.IsNpc"/>). Clicking one opens its dialog (Session.HandleClickInfo).
    /// Runs once at startup after Content.Load; the NPC's home tile is its spawn tile and it holds position.</summary>
    private void PopulateNpcs()
    {
        int placed = 0;
        lock (_lock)
        {
            foreach (var n in Content.Npcs)
            {
                if (!n.Enabled) continue;   // switched off in NPCs.csv (Enabled=0)
                PlaceNpc(n);
                placed++;
            }
        }
        Log.Info($"npcs: {placed} stationary NPC(s) placed");
    }

    /// <summary>Instantiate one NPC def as a stationary (or pacing) non-fighting mob and add it to its map.
    /// Shared by startup placement and the live <see cref="EnableNpc"/> toggle. Caller holds <c>_lock</c>.</summary>
    private void PlaceNpc(NpcDef n)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        // Don't stack on a mob spawn sharing the tile, but DO stand where NPCs.csv says, wall or not.
        var (nx, ny) = FreeSpawnTile(n.Map, n.X, n.Y, avoidSolid: false);
        _npcPlaced[n.Id] = n;   // the def this instance was built from — see ReconcileNpcToggles
        // RTK gives some NPCs (animals, town dogs, roaming merchants) a MoveTime + ReturnDistance so they
        // pace; the rest stand still. A leash of 0 means "don't stray", i.e. stationary.
        bool paces = n.MoveTime > 0 && n.ReturnDistance > 0;
        var npc = new Mob(_nextNpcId++, n.Look, nx, ny, n.Name, hp: 1)
        {
            IsNpc = true, NpcDefId = n.Id, Color = n.Color, Dir = n.Dir,
            Wander = paces, MoveTime = paces ? n.MoveTime : 2500, Leash = n.ReturnDistance,
        };
        var nm = Map(n.Map);
        nm.Mobs.Add(npc);
        nm.ViewGen++;                       // a mob roster change — the mob sweep reads this list
    }

    /// <summary>Remove every placed instance of NPC def <paramref name="npcId"/> from the world and despawn
    /// it (0x0E) for everyone watching. Returns how many instances were removed. Called by
    /// <see cref="ReconcileNpcToggles"/> on <c>@reload</c> — toggling is config (the Enabled column of
    /// NPCs.csv), not a live GM action, so this has no separate persistence step of its own.</summary>
    public int DisableNpc(int npcId)
    {
        var removed = new List<(ushort map, uint id)>();
        lock (_lock)
        {
            foreach (var (mapId, m) in _maps)
            {
                var gone = m.Mobs.Where(x => x.IsNpc && x.NpcDefId == npcId).ToList();
                foreach (var g in gone) { m.Mobs.Remove(g); removed.Add((mapId, g.Id)); }
                if (gone.Count > 0) m.ViewGen++;   // a mob roster change
            }
            _npcPlaced.Remove(npcId);
        }
        foreach (var (map, id) in removed)
            Broadcast(map, p => p.DespawnEntity(id));   // socket I/O — outside the lock
        return removed.Count;
    }

    /// <summary>Place NPC def <paramref name="npcId"/> back into the world (idempotent — a no-op if it's
    /// already placed). The periodic viewport sync streams it to anyone in range. Returns true if it was
    /// placed. See <see cref="DisableNpc"/>.</summary>
    public bool EnableNpc(int npcId)
    {
        lock (_lock)
        {
            foreach (var (_, m) in _maps)
                if (m.Mobs.Any(x => x.IsNpc && x.NpcDefId == npcId)) return false;   // already present
            var def = Content.Npcs.FirstOrDefault(n => n.Id == npcId);
            if (def is null) return false;
            PlaceNpc(def);
        }
        return true;
    }

    /// <summary>Hot-reload hook (the <c>@reload</c> command, after <see cref="Content.Reload"/> re-reads
    /// <c>NPCs.csv</c>): re-sync stationary-NPC placement against the just-reloaded defs — spawns any NPC newly
    /// enabled, despawns any newly disabled, and re-places any whose def CHANGED. That last case used to be
    /// missed entirely: <see cref="EnableNpc"/> returns false as soon as the NPC is placed at all, so an edited
    /// tile (or look, or colour) sat in the CSV doing nothing until the next full restart, and the world quietly
    /// disagreed with the file. <see cref="NpcDef"/> is a record, so one structural compare covers every column.
    /// Returns how many NPCs' placement changed.</summary>
    public int ReconcileNpcToggles()
    {
        int changed = 0;
        foreach (var n in Content.Npcs)
        {
            if (!n.Enabled) { if (DisableNpc(n.Id) > 0) changed++; continue; }

            bool moved;
            lock (_lock) moved = _npcPlaced.TryGetValue(n.Id, out var prev) && !prev.Equals(n);
            if (moved) DisableNpc(n.Id);          // drop the stale instance, then fall through and re-place it
            if (EnableNpc(n.Id)) changed++;
        }
        return changed;
    }

    /// <summary>Build one live creature, add it to the map and fire its spawn hook. Shared by both spawn
    /// systems so a batch-spawned cave mob is identical in every respect to a point-spawned town one — the
    /// only difference between the two is what decides WHEN and WHERE. Home defaults to the spawn tile.
    /// Caller holds <c>_lock</c>.</summary>
    private Mob BuildMob(ushort mapId, MobDef d, ushort x, ushort y, ushort? homeX = null, ushort? homeY = null)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var mob = new Mob(_nextMobId++, d.Look, x, y, d.Name, d.Hp)
        {
            // Color byte = RTK's MobLookColor. (The client Monster.tbl palette turned out wrong here — it
            // rendered every mob green — so we use RTK's per-mob colour, which matches for most creatures.)
            Key = d.Key, DefId = d.Id,   // identifier for quest kill-matching, id for spawn-group counting
            Color = d.Color, Exp = d.Exp, Level = d.Level, Will = d.Will, Aggressive = d.Aggressive, Flees = d.Flees,
            MinDam = d.MinDam, MaxDam = d.MaxDam, Hit = d.Hit, IsBoss = d.IsBoss, Protection = d.Protection, Ac = d.Ac, Grace = d.Grace,
            // Wander is the default; MobStationary.csv opts a creature out (a penned captive whose RTK AI
            // script only ever turns on the spot — see Content.LoadMobStationary).
            Dir = 2, HomeX = homeX ?? x, HomeY = homeY ?? y, Wander = !d.Stationary, Leash = WanderRadius,
            MoveTime = d.MoveTime, MoveTimer = Random.Shared.Next(d.MoveTime),   // stagger so they don't all step at once
            WorldSpawned = true,           // placed by the world, so it drops loot (see Mob.WorldSpawned)
        };

        // Spawn HP jitter (RTK AI/mob_on_spawn.lua — the DEFAULT on_spawn every creature without its own
        // gets): max HP moves by up to +/-(minDam + maxDam) * 2, so two of the same creature are never
        // quite the same fight. Floored at 1 — RTK's own version can drive a small, hard-hitting mob to
        // zero HP, which would spawn it already dead.
        if (Content.MobHpJitter)
        {
            int swing = Math.Max(1, (d.MinDam + d.MaxDam) * 2);
            // The jitter is scaled by DAMAGE, which assumes damage and HP share a rough scale — true of an
            // ordinary creature. A hard-hitter whose swing dwarfs its own HP (the Ice Beast one-shots for
            // 300k but has only 10k HP) would otherwise have its HP scrambled to anywhere from 1 to ~1.2M,
            // half the time spawning AT 1 HP — which for the Ice Beast would let a player one-hit it and
            // skip the lava-lure entirely. Only jitter when the swing fits inside the mob's own health.
            if (swing < mob.MaxHp)
            {
                int delta = Random.Shared.Next(1, swing + 1) * (Random.Shared.Next(2) == 0 ? 1 : -1);
                mob.MaxHp = Math.Max(1, mob.MaxHp + delta);
                mob.Hp = mob.MaxHp;
            }
        }
        var bm = Map(mapId);
        bm.Mobs.Add(mob);
        bm.ViewGen++;                       // a mob roster change
        QueueHook(MobScript.OnSpawn, mapId, mob, null);
        return mob;
    }

    /// <summary>The spawn tile if it's open, else the nearest tile (within 2) that's in-bounds, not already
    /// occupied by a live mob, and — for a real creature — not solid, so two spawns on one tile (or a respawn
    /// onto a wanderer) don't stack. Falls back to the spawn tile if everything nearby is taken.
    ///
    /// <paramref name="avoidSolid"/> is false for NPCs: standing on solid ground is NORMAL for them and has to
    /// be honoured, not corrected. RTK's own authored placements do it (Mignok 4716(4,9) and Tominaru 4716(13,8)
    /// are both wall tiles), it's how an NPC stands behind a counter or on a shrine block, and nothing about an
    /// NPC needs walkable ground: it renders through the same 0x07 creature path as any mob and
    /// <see cref="Session.HandleClickInfo"/> opens its dialog by entity id with no adjacency check. Bumping them
    /// silently moved every such NPC a few tiles off its authored spot. Caller holds <c>_lock</c>.</summary>
    private (ushort x, ushort y) FreeSpawnTile(ushort mapId, ushort x, ushort y, bool avoidSolid = true)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var m = Map(mapId);
        var dims = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        var terrain = dims.Item1 > 0 ? MapData.For(mapId, dims.Item1, dims.Item2) : null;

        // A map with no registry row has no upper bound to test — dims are 0 and terrain is null, and the old
        // inline Free() skipped its `tx >= xs` test in exactly that case rather than reading 0 as the width.
        // int.MaxValue says the same thing to the shared walk without giving 0 a second meaning.
        int xs = dims.Item1 > 0 ? dims.Item1 : int.MaxValue;
        int ys = dims.Item1 > 0 ? dims.Item2 : int.MaxValue;

        // The spawn tile first, then the rings at radius 1 and 2 — that order and the bounds test are
        // MapData.FreeNeighbour's now, shared with the dismount path (#57 finding 31). The ground test, the
        // mob test and the fallback stay this caller's: they are not the dismount's and must not become them.
        //
        // This runs under _lock on every respawn, so it must cost what the inline loop cost: the walk is a
        // struct enumerator and the two tests are a struct, so there is no iterator, no closure and no
        // delegate — nothing on the heap per call at all.
        var free = MapData.FreeNeighbour(
            MapData.SelfThenRingWalk(x, y, maxRadius: SpawnTileRadius), xs, ys,
            new SpawnTileTest(m, terrain, avoidSolid));

        return free is { } t ? ((ushort)t.x, (ushort)t.y)
                             : (x, y);   // everything nearby is taken — accept the overlap rather than drop the mob
    }

    /// <summary>The spawn fallback's own two tests, exactly as the inline <c>Free()</c> wrote them: ground pass
    /// only (<see cref="MapData.Solid"/>, and not even that for an NPC — see the remarks on
    /// <see cref="FreeSpawnTile"/>), and a LIVE mob standing there. A readonly struct rather than a pair of
    /// lambdas because <see cref="MapData.FreeNeighbour{TWalk, TTest}"/> takes the tests by generic type and so
    /// allocates nothing per call, which this path — under <c>_lock</c>, on every respawn — needs.</summary>
    private readonly struct SpawnTileTest(MapState m, MapData? terrain, bool avoidSolid) : MapData.ITileTest
    {
        public bool Blocked(int x, int y, int side) => avoidSolid && terrain is not null && terrain.Solid(x, y);

        public bool Occupied(int x, int y)
        {
            foreach (var mo in m.Mobs) if (mo.Alive && mo.X == x && mo.Y == y) return true;
            return false;
        }
    }

    // Refill every forage box to its target stack count on random passable tiles (RTK itemspawner.lua:
    // count existing stacks of the item in the box, drop the shortfall). Runs under _lock; returns the new
    // drops (with their map) so the caller can broadcast them once the lock is released.
    private List<(ushort map, GroundItem gi)>? TopUpForageLocked()
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        List<(ushort, GroundItem)>? drops = null;
        foreach (var area in Content.ForageAreas)
        {
            if (!_maps.TryGetValue(area.Map, out var m) || m.Players.Count == 0) continue;   // no one watching
            var def = Content.ItemByKey(area.ItemKey);
            if (def is null) continue;

            int have = m.Items.Count(gi => gi.ItemId == def.Id &&
                                           gi.X >= area.MinX && gi.X <= area.MaxX &&
                                           gi.Y >= area.MinY && gi.Y <= area.MaxY);
            int need = area.Max - have;
            if (need <= 0) continue;

            var (xs, ys) = Content.Maps.TryGetValue(area.Map, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
            var terrain = xs > 0 ? MapData.For(area.Map, xs, ys) : null;
            for (int i = 0; i < need; i++)
            {
                int tx = Random.Shared.Next(area.MinX, area.MaxX + 1);
                int ty = Random.Shared.Next(area.MinY, area.MaxY + 1);
                if (terrain is not null && terrain.Solid(tx, ty)) continue;   // passable tiles only (getPass==0)
                var gi = new GroundItem
                {
                    Id = _nextItemId++, ItemId = def.Id, X = (ushort)tx, Y = (ushort)ty,
                    Amount = Random.Shared.Next(area.MinQty, area.MaxQty + 1), Graphic = def.Icon,
                };
                m.Items.Add(gi);
                m.ViewGen++;                // a floor-item roster change — the item sweep reads this list
                (drops ??= new()).Add((area.Map, gi));
            }
        }
        return drops;
    }

    public uint AllocatePlayerId() { lock (_lock) return _nextPlayerId++; }
    public uint AllocateMobId()    { lock (_lock) return _nextMobId++; }
    public uint AllocateItemId()   { lock (_lock) return _nextItemId++; }

    private uint _nextTrapId = 1;

    /// <summary>Place a hidden trap (see <see cref="Trap"/>). Never broadcast — traps have no ground
    /// graphic; only <see cref="TrapsNear"/> (spot_traps) ever reveals one, and only to its caster.</summary>
    public Trap PlaceTrap(ushort mapId, ushort x, ushort y, string kind, uint ownerId, long expiresAt = 0)
    {
        var t = new Trap { Id = _nextTrapId++, X = x, Y = y, Kind = kind, OwnerId = ownerId, ExpiresAt = expiresAt };
        lock (_lock) Map(mapId).Traps.Add(t);
        return t;
    }

    // RTK bladestorm_trap.lua's block.side -> {x[],y[]} table: 4 tiles fanned out AHEAD of the TRIGGER's own
    // facing (0=N/1=E/2=S/3=W, this codebase's usual Dir convention) — not the caster's facing at cast time.
    private static readonly (int dx, int dy)[][] BladestormFan =
    {
        new[] { (0,-1), (-1,-2), (0,-2), (1,-2) },   // dir 0 = north
        new[] { (1,0), (2,-1), (2,0), (2,1) },       // dir 1 = east
        new[] { (0,1), (-1,2), (0,2), (1,2) },       // dir 2 = south
        new[] { (-1,0), (-2,1), (-2,0), (-2,-1) },   // dir 3 = west
    };

    /// <summary>Bladestorm's PC-trigger path (see Content.IsBladestormTrap) — the only trap kind a PLAYER can
    /// set off; the hazard family (dart/snare/…) stays mob-only. Called from Session.HandleWalk right after a
    /// successful step commits the new tile — HandleWalk holds no lock of its own, so the resulting damage
    /// can be applied directly here with no deferred queue (unlike the mob-trigger case in
    /// TriggerTrapLocked, which fires from inside World.Tick's own lock).</summary>
    public void CheckPlayerTrapTrigger(Session player, ushort mapId, ushort x, ushort y, byte facing)
    {
        Trap? trap;
        Session? pitOwner = null;
        var coneTargets = new List<Mob>();
        string? ambushMsg = null;
        // Spy kit's traps and Ranger's Pit trap spring on a player only where the trap may touch them, and never on a
        // ghost: RTK's click returns first for `block.state == 1` and for `not block:canPK(block)` (this server's canPK, the
        // PvP map), leaving the trap where it is. Read on the stepper's own thread, before the lock.
        bool spySprings = !player.IsDead && Content.IsPvpMap(mapId);
        lock (_lock)
        {
            var m = Map(mapId);
            // The trap kinds a PLAYER sets off: bladestorm (an AoE decoy), shiver (a cosmetic fall echo — see
            // Session.TryMythicFallRoom), ambush (a cave mob-spawn tile — see Content.AmbushMapDef), and Spy kit's three
            // and the Pit trap where they may touch the stepper. The hazard family (dart/snare/…) stays mob-only.
            trap = m.Traps.FirstOrDefault(t => (t.Kind == "bladestorm" || t.Kind == "shiver" || t.Kind == "ambush"
                                                || t.Kind == SuteAi.FrigidTrapKind || (spySprings && IsSubpathTrap(t.Kind)))
                                               && t.X == x && t.Y == y);
            if (trap is null) return;
            // A Pit trap whose owner is gone does nothing and stays (RTK pit_trap.lua's click opens on Player(npc.owner),
            // which is nil then, and stops with a Lua error at owner.maxHealth before it touches anything).
            if (trap.Kind == PitTrap && (pitOwner = Online.ByIdLocked(trap.OwnerId)) is null) return;
            m.Traps.Remove(trap);
            if (trap.Kind == SuteAi.FrigidTrapKind) RefillFrigidLocked(mapId);   // relocate the sprung tile
            if (trap.Kind == "bladestorm")
                foreach (var (dx, dy) in BladestormFan[facing & 3])
                {
                    var t = m.Mobs.FirstOrDefault(o => o.Alive && o.X == x + dx && o.Y == y + dy);
                    if (t is not null) coneTargets.Add(t);
                }
            else if (trap.Kind == "ambush")
                ambushMsg = FireAmbushLocked(mapId, x, y);   // spawns the burst + relocates a replacement trap
        }
        // The trap is gone, so its revealed marker goes with it (RTK removeTrapItem before npc:delete) — on
        // every client that spotted it, not just the stepper's. Runs for all three PC-triggerable kinds; only
        // ambush is revealable today, so for the other two it's a no-op on every session.
        Broadcast(mapId, p => p.ClearTrapMarker(trap.Id));
        // Spy kit's trap: its spell on the stepper, written on their own thread inside their own monitor.
        if (IsSpyTrap(trap.Kind)) { player.SpringSpyTrap(trap.Kind, trap.OwnerId); return; }
        // The Pit trap: its strike on the stepper, from its owner's max vita and mana as they stand now (a peer read of
        // the owner, no monitor of theirs taken), written on the stepper's own thread inside their own monitor.
        if (trap.Kind == PitTrap)
        {
            var (vita, mana) = pitOwner!.PeerMaxVitaMana;
            player.SpringPitTrap(trap.OwnerId, vita, mana);
            return;
        }
        // Shiver is pure flavor: sense the sprung trap, nothing else (RTK WarpTrapShiverNpc).
        if (trap.Kind == "shiver") { player.FeelShiver(); return; }
        // Sute's Cave cold tile: a flat hit and its own line. Its replacement was already placed under the
        // lock above, so the room does not get safer as it is walked.
        if (trap.Kind == SuteAi.FrigidTrapKind) { player.TakeFrigidBlast(); return; }
        // Ambush: the burst mobs are already on the map (built under the lock above); the caller's post-step
        // SyncMobs renders them for the stepper. Just deliver the "Rabbits ambush you!" style status line.
        if (trap.Kind == "ambush") { if (ambushMsg is not null) player.ShowAmbushText(ambushMsg); return; }
        // Bladestorm: ONE damage number, computed from the trigger (RTK applies it uniformly, not per-target) —
        // Session owns the armor/HP math for a player trigger and caps its OWN loss to leave 1 HP; the cone
        // targets it catches take the same (uncapped) value via the existing trap-damage pipeline.
        int dmg = player.ApplyBladestormSelfDamage();
        foreach (var t in coneTargets) Try(() => ApplyTrapDamage(mapId, t, dmg, player.PlayerId), "ApplyTrapDamage (bladestorm cone)");
    }

    // ---- Ambush traps (Content.Ambushes / AmbushBursts; RTK mob_spawn.lua + rabbitTrap.lua + tigerTrap.lua) ----
    // A hidden "ambush" trap tile a player steps on spawns a burst of cave mobs around them, then a replacement
    // trap is placed elsewhere (while live mobs stay under the map's cap). Caller holds _lock; the burst mobs
    // are built here (BuildMob needs the lock) and the stepper's own post-step SyncMobs renders them. Returns
    // the ambush status message to show the stepper. Bosses are NOT rolled here — they stay on the rare
    // spawn-point system (AreaSpawnsTrap), which already reproduces their 1/10 + cooldown surprise.
    private string? FireAmbushLocked(ushort mapId, ushort x, ushort y)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        if (!Content.Ambushes.TryGetValue(mapId, out var cfg)) return null;
        bool onStepper = cfg.PrimaryKind == "single";   // RTK block:spawn(id, block.x, block.y) — see AmbushBurstTile
        int slot = 0;
        foreach (var mobId in SelectAmbushBurst(cfg, y))
        {
            var def = Content.MobById(mobId);
            int i = slot++;                              // consumed even if the id doesn't resolve — RTK indexes the LIST
            if (def is null) continue;
            var (sx, sy) = onStepper ? (x, y) : AmbushBurstTile(mapId, x, y, i);
            BuildMob(mapId, def, sx, sy);
        }
        RefillAmbushLocked(mapId);   // relocate the sprung trap (and top up) while under the mob cap
        return cfg.Message;
    }

    /// <summary>Where the <paramref name="index"/>'th member of a burst lands: RTK spreads them over the four
    /// tiles AROUND the stepper in a fixed order — east, west, north, south — and only drops one on the
    /// stepper's OWN tile when that neighbour is unusable (wall, occupied, off-map or a warp). Members past
    /// the fourth (a 5-strong sentry pack) land on the stepper too, exactly as the Lua's trailing <c>else</c>
    /// does. See rabbitTrap.lua <c>spawnRabbitMobN</c> / mob_spawn.lua <c>spawnMob</c>: each z-index has its
    /// own hard-coded neighbour and its own <c>getPass</c>/<c>getObjectsInCell</c>/<c>getWarp</c> guard.
    ///
    /// <para>Deliberately NOT <see cref="FreeSpawnTile"/>. That helper treats the trigger tile as free when no
    /// MOB is on it — it never looks at players — so the first member of every burst spawned directly under
    /// the stepper's feet before the rest ringed outward: the reported "mobs spawn around me, but one is
    /// stacked on top of me". The single-creature caves (Kugnae trapdoor spider, Buya scorpion lurker) still
    /// land on the stepper, because RTK's own <c>block:spawn(102, block.x, block.y, 1)</c> does — one creature
    /// dropping onto you is that trap, not this bug.</para>
    ///
    /// Caller holds <c>_lock</c>.</summary>
    private (ushort x, ushort y) AmbushBurstTile(ushort mapId, ushort x, ushort y, int index)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var (dx, dy) = index switch
        {
            0 => (1, 0),    // east
            1 => (-1, 0),   // west
            2 => (0, -1),   // north
            3 => (0, 1),    // south
            _ => (0, 0),    // 5th and beyond: on the stepper (RTK's `else block:spawn(mob, block.x, block.y)`)
        };
        if (dx == 0 && dy == 0) return (x, y);

        int nx = x + dx, ny = y + dy;
        var (xs, ys) = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        if (nx < 0 || ny < 0 || (xs > 0 && (nx >= xs || ny >= ys))) return (x, y);
        var terrain = xs > 0 ? MapData.For(mapId, xs, ys) : null;
        if (terrain is not null && terrain.Solid(nx, ny)) return (x, y);
        if (Content.TryWarp(mapId, (ushort)nx, (ushort)ny, out _)) return (x, y);
        var m = Map(mapId);
        foreach (var mo in m.Mobs) if (mo.Alive && mo.X == nx && mo.Y == ny) return (x, y);
        foreach (var p in m.Players) if (p.PlayerX == nx && p.PlayerY == ny) return (x, y);
        return ((ushort)nx, (ushort)ny);
    }

    // Pick the mob-id list this trap fires: the sentry burst when the stepper is in the top half
    // (y <= SentryTopY — RTK's Hare Summit split / the guardroom's always-on sentries), else a 1-in-BigChance
    // "big mob" burst (tiger Dark Pen), else the primary — a random burst variant, a single creature
    // (spider/scorpion), or an ogre 4-tile burst (RTK spawnMob).
    private int[] SelectAmbushBurst(AmbushMapDef cfg, ushort y)
    {
        if (cfg.SentryTable.Length > 0 && y <= cfg.SentryTopY) return PickVariant(cfg.SentryTable);
        if (cfg.BigTable.Length > 0 && cfg.BigChance > 0 && Random.Shared.Next(cfg.BigChance) == 0) return PickVariant(cfg.BigTable);
        return cfg.PrimaryKind switch
        {
            "burst"  => PickVariant(cfg.PrimaryTable),
            "single" => new[] { cfg.PrimaryMob },
            "ogre"   => Enumerable.Repeat(
                            cfg.OgreAltChance > 0 && Random.Shared.Next(cfg.OgreAltChance) == 0 ? cfg.OgreAltMob : cfg.PrimaryMob, 4)
                        .ToArray(),
            _        => Array.Empty<int>(),
        };
    }

    private int[] PickVariant(string table) =>
        Content.AmbushBursts.TryGetValue(table, out var vs) && vs.Count > 0 ? vs[Random.Shared.Next(vs.Count)] : Array.Empty<int>();

    // Top a configured map's hidden ambush traps back up to its target count, but only while live mobs stay
    // under the map's cap — RTK's population governor (its trap refiller stops adding while the map is already
    // full of mobs). Caller holds _lock. Called on every map entry (EnsureMaterialized, which passes the
    // entering player's tile as `arriving`: they are not on the map's player list yet, #122) and after a trap
    // fires (the stepper is on the list, so no `arriving`).
    private void RefillAmbushLocked(ushort mapId, (int X, int Y)? arriving = null)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        if (!Content.Ambushes.TryGetValue(mapId, out var cfg)) return;
        var m = Map(mapId);
        int traps = 0; foreach (var t in m.Traps) if (t.Kind == "ambush") traps++;
        if (traps >= cfg.Count) return;
        int mobs = 0; foreach (var mob in m.Mobs) if (mob.Alive && !mob.IsNpc) mobs++;
        if (mobs >= cfg.MobCap) return;

        var taken = OccupiedTiles(mapId, arriving);
        foreach (var t in m.Traps) taken.Add((t.X, t.Y));   // don't stack two traps on one tile
        int budget = cfg.Count * PlacementTriesPerMob;
        while (traps < cfg.Count && budget-- > 0)
        {
            if (!TryPickMapTile(mapId, taken, out var tx, out var ty)) continue;
            m.Traps.Add(new Trap { Id = _nextTrapId++, X = tx, Y = ty, Kind = "ambush", OwnerId = 0 });
            taken.Add((tx, ty));
            traps++;
        }
    }

    // ---- Sute's Cave cold tiles (Server/SuteAi.cs) ----------------------------------------------
    // The "A blast of frigid cold hits you." hazard: hidden trap tiles scattered through all seven rooms,
    // topped back up to SuteAi.FrigidTrapsPerMap on every entry and after each one springs. Deliberately the
    // same machinery as the cave ambush traps above rather than a per-step dice roll, so they can be SPOTTED
    // (spot_traps reveals any trap kind) and so a sprung one relocates instead of thinning the room out.
    // Caller holds _lock. `arriving` as in RefillAmbushLocked: the entering player's tile on map entry, null
    // after a cold tile springs under someone already on the map.
    private void RefillFrigidLocked(ushort mapId, (int X, int Y)? arriving = null)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        if (Array.IndexOf(SuteAi.CaveMaps, mapId) < 0) return;
        var m = Map(mapId);
        int traps = 0; foreach (var t in m.Traps) if (t.Kind == SuteAi.FrigidTrapKind) traps++;
        if (traps >= SuteAi.FrigidTrapsPerMap) return;

        var taken = OccupiedTiles(mapId, arriving);
        foreach (var t in m.Traps) taken.Add((t.X, t.Y));   // don't stack two traps on one tile
        int budget = SuteAi.FrigidTrapsPerMap * PlacementTriesPerMob;
        while (traps < SuteAi.FrigidTrapsPerMap && budget-- > 0)
        {
            if (!TryPickMapTile(mapId, taken, out var tx, out var ty)) continue;
            m.Traps.Add(new Trap { Id = _nextTrapId++, X = tx, Y = ty, Kind = SuteAi.FrigidTrapKind, OwnerId = 0 });
            taken.Add((tx, ty));
            traps++;
        }
    }

    // A random passable, object-free, non-warp, unoccupied tile anywhere on a map (the whole-map analogue of
    // SpawnDirector.TryPickGroupTile). Caller holds _lock.
    private bool TryPickMapTile(ushort mapId, HashSet<(int, int)> taken, out ushort x, out ushort y)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        x = y = 0;
        var (xs, ys) = Content.Maps.TryGetValue(mapId, out var mi) ? (mi.Xs, mi.Ys) : ((ushort)0, (ushort)0);
        if (xs == 0 || ys == 0) return false;
        int tx = Random.Shared.Next(1, xs), ty = Random.Shared.Next(1, ys);
        if (taken.Contains((tx, ty))) return false;
        var terrain = MapData.For(mapId, xs, ys);
        if (terrain is not null && terrain.Solid(tx, ty)) return false;
        if (Content.TryWarp(mapId, (ushort)tx, (ushort)ty, out _)) return false;
        x = (ushort)tx; y = (ushort)ty;
        return true;
    }

    /// <summary>Every trap within <paramref name="radius"/> tiles (Chebyshev) of a point — spot_traps'
    /// reveal, or a debug listing. Doesn't consume/remove anything.</summary>
    public Trap[] TrapsNear(ushort mapId, int x, int y, int radius)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return Array.Empty<Trap>();
            return m.Traps.Where(t => Math.Max(Math.Abs(t.X - x), Math.Abs(t.Y - y)) <= radius).ToArray();
        }
    }

    // Flat damage for the four "instant hit" trap kinds (RTK NPCs/trap/rogue_traps/*.lua — dart/repeating
    // are byte-for-byte the same script despite the name difference; only their spell-side level gate and
    // mana cost differ).
    private static readonly Dictionary<string, int> TrapDamage = new()
        { ["dart"] = 500, ["repeating"] = 500, ["spear"] = 3500, ["death"] = 11650 };

    /// <summary>Trap kinds only a PLAYER springs — a wandering mob walks over one and leaves it alone. RTK
    /// gates both inside the trap NPC's own click hook: <c>MobSpawnNpc</c> (our "ambush") and the tiger
    /// caves' <c>WarpTrapShiverNpc</c> (our "shiver") each bail out unless <c>block.blType == BL_PC</c>. The
    /// rogue combat family is the opposite — those exist to catch mobs — so it stays triggerable by both.
    ///
    /// <para>Without this, cave fauna quietly ATE the hidden ambush tiles as they wandered (the mob-step
    /// lookup removed the trap, then <see cref="TriggerTrapLocked"/> had no case for it and did nothing), so
    /// a cave's spawn triggers kept silently vanishing and reappearing elsewhere on the next refill — the
    /// "the spawn triggers seem to despawn and move around" report.</para></summary>
    private static bool IsPcOnlyTrap(string kind) => kind is "shiver" or "ambush";

    // Caller holds _lock (called mid-movement-loop, mob has just stepped onto the trap's tile). Damage
    // kinds are queued for World.Tick's deferred pass (needs Session-facing broadcasts, which mustn't run
    // under the lock); status kinds (snare/sleep/flash — all simplified to the same "can't act" mechanic as
    // a cast Debuff, since this server has no separate armor-debuff/blind stat) and poison (a real DOT,
    // ticked every 1500ms by the poison check above) mutate the mob directly since that's lock-only state.
    private void TriggerTrapLocked(ushort mapId, Mob mob, Trap trap, List<(ushort map, Mob mob, int dmg, uint ownerId)> damageQueue)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        long now = Environment.TickCount64;
        // A Pit trap whose owner is gone does nothing and stays where it was: RTK pit_trap.lua's click opens on
        // Player(npc.owner), which is nil then, and stops with a Lua error at owner.maxHealth before it touches anything
        // (the error is caught and the engine goes on, rtk/src/map/sl.c, sl_doscript). The step took it off the list
        // (MobMovement.StepMobTo), so it goes back at the head, which keeps it the first trap on its tile.
        if (trap.Kind == PitTrap && Online.ByIdLocked(trap.OwnerId) is null) { Map(mapId).Traps.Insert(0, trap); return; }
        // RTK's every-trap epilogue: removeTrapItem(npc) before npc:delete(). A trap that has gone off takes
        // its revealed marker with it, so a Spot Traps / Watchful Eye sword never outlives the trap it marks.
        _deferredTrapClears.Add((mapId, trap.Id));
        switch (trap.Kind)
        {
            case "dart" or "repeating" or "spear" or "death":
                damageQueue.Add((mapId, mob, TrapDamage[trap.Kind], trap.OwnerId));
                break;
            case "snare": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 75000); break;   // RTK: armor+20 debuff, simplified to a hold
            case "sleep": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 38000); break;
            case "flash": mob.FrozenUntil = Math.Max(mob.FrozenUntil, now + 10000); break;    // RTK: blind.cast, simplified to a hold
            case "poison":
                mob.PoisonUntil = now + 1 + Random.Shared.Next(1500, 30001);   // RTK: 1 + random(1500,30000) for a MOB target
                mob.PoisonNextTick = now + 1500;
                mob.PoisonTickDam = Math.Clamp((int)(mob.MaxHp * 0.01), 1, 1000);
                mob.PoisonOwnerId = trap.OwnerId;
                break;
            case "bladestorm":
            {
                // ONE HP-percent damage number computed from the trigger (RTK block.health*0.75, or *0.05 on
                // instance/high maps ids >= 60000), applied uniformly to the trigger itself AND every mob the
                // facing cone catches — see Content.IsBladestormTrap. The PC-trigger case (World.
                // CheckPlayerTrapTrigger) mirrors this but nets against the trigger's OWN armor instead.
                var mm = Map(mapId);
                int dmg = Math.Max(1, (int)(mob.Hp * (mapId < 60000 ? 0.75 : 0.05)));
                foreach (var (dx, dy) in BladestormFan[mob.Dir & 3])
                {
                    var t = mm.Mobs.FirstOrDefault(o => o.Alive && !ReferenceEquals(o, mob) && o.X == mob.X + dx && o.Y == mob.Y + dy);
                    if (t is not null) damageQueue.Add((mapId, t, dmg, trap.OwnerId));
                }
                damageQueue.Add((mapId, mob, dmg, mob.Id));   // the trigger takes it too (RTK block.health -= damage)
                break;
            }
            case ToxicSprayTrap or TripwireTrap or SmokeScreenTrap:
                SpringSpyTrapLocked(mapId, mob, trap);
                break;
            case PitTrap:
                SpringPitTrapLocked(mapId, mob, trap, damageQueue);
                break;
        }
    }

    // ---- Ranger's Pit trap (RTK NPCs/trap/rogue_traps/Ranger/pit_trap.lua, set by Spells/Subpaths/Ranger/pit_trap.lua) ----
    // RTK's trap NPC, as Spy kit's are: never drawn, never shown by Spot traps or Watchful Eye (Session.RevealableTrapsNear),
    // gone after its 20 s if nothing has sprung it (the expiry sweep in Tick). A creature that steps onto it always springs
    // it (TriggerTrapLocked); a player only where the trap may touch them, alive (CheckPlayerTrapTrigger,
    // Session.SpringPitTrap). Its click reads its owner first (Player(npc.owner)): with the owner gone (logged out) the
    // script stops there with a Lua error, so the trap does nothing and stays, for anyone, until its time. Else the strike:
    // half the owner's max vita plus half their max mana, as they stand when it springs, through RTK's calculateDamage
    // (Scripts/scripts.lua:1163-1227: the sleep amplifier, the deduction, armour clamped to the victim's floor and to 100),
    // then never the last vita (PitTrapDamage), "CHOMP!" to a player, graphic 44, credited to the owner. The script sets no
    // duration on its victim, so no creature timer applies. Its plain removeHealth tests no ward (DamageIntake's note on
    // IgnoresHardenBody names this file), so no ward stops it. RTK reads the victim's vita when the trap fires, so the
    // last-vita rule reads the vita the victim has when the strike lands: on a player at once, inside their monitor; on a
    // creature at the strike's own place in the beat's trap-damage list, after whatever landed on it before that place (a
    // pet's swing, a poison tick queued before its step) and before whatever is queued after it (#423's review, F2, and its
    // re-check, R1). The figure itself, with the creature's armour and sleep amplifier, is worked out when it springs, as
    // the slice first had it (R2).
    internal const string PitTrap = "pit_trap";
    internal const int PitTrapAnim = 44;                        // block:sendAnimation(44), "big teeth"
    private const int PitTrapArmorCap = 100;                    // calculateDamage: armoramount > 100 -> 100

    /// <summary>Is <paramref name="kind"/> a PC subpath's trap: Spy kit's three or Ranger's Pit trap?</summary>
    internal static bool IsSubpathTrap(string kind) => IsSpyTrap(kind) || kind == PitTrap;

    /// <summary>The Pit trap's strike before its last line, from RTK pit_trap.lua:5-25: <paramref name="raw"/> (the owner's
    /// max vita and max mana, half each) times the victim's armour factor (its armour clamped to its floor and to
    /// <see cref="PitTrapArmorCap"/>), its sleep amplifier and its deduction, then, where the victim has more than 1 vita and
    /// fewer than the figure, its vita less 1 (RTK's strict <c>block.health &lt; damage</c>, so a figure exactly equal to
    /// the vita left still takes it all), at 1 vita nothing, else the figure floored once (at least 1).</summary>
    internal static int PitTrapDamage(double raw, double armorFactor, double amp, double deduction, int hp)
    {
        double figure = raw * armorFactor * (amp > 1.0 ? amp : 1.0) * deduction;
        if (hp > 1 && hp < figure) return hp - 1;
        if (hp == 1) return 0;
        return Combat.FloorOnce(figure);
    }

    /// <summary>The Pit trap's armour factor for a victim of armour <paramref name="armor"/> whose floor is
    /// <paramref name="floor"/> (-95 a creature, -80 a player): RTK calculateDamage's clamp and <c>1 + armour / 100</c>.</summary>
    internal static double PitTrapArmorFactor(int armor, int floor) => Combat.ArmorFactor(Math.Min(armor, PitTrapArmorCap), floor);

    /// <summary>A creature springs the Pit trap (caller holds <c>_lock</c>, mid-step; its owner is online, checked above):
    /// the strike is queued with the tick's other trap damage, at its place in <paramref name="damageQueue"/>, credited to
    /// the owner, and graphic 44 goes with the tick's deferred effects. The owner's max vita and mana are a peer read
    /// (<see cref="Session.PeerMaxVitaMana"/>: one published snapshot, no monitor of theirs taken, which would be the wrong
    /// order inside <c>_lock</c>). The figure, with the creature's armour and its sleep amplifier (spent here, so the hit
    /// that lands it finds none), is kept for that entry (<see cref="_pitStrikeFigures"/>), and <see cref="FlushTick"/>
    /// lands it through <see cref="ApplyPitStrike"/>, capped against the vita left then. The queued figure is the strike
    /// capped against the vita now, which is what a list not flushed by FlushTick lands. At 1 vita the strike is 0 and
    /// nothing is queued (RTK's removeHealth(0) takes nothing).</summary>
    private void SpringPitTrapLocked(ushort mapId, Mob mob, Trap trap, List<(ushort map, Mob mob, int dmg, uint ownerId)> damageQueue)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        if (Online.ByIdLocked(trap.OwnerId) is not { } owner) return;
        var (vita, mana) = owner.PeerMaxVitaMana;
        int hp = mob.Hp;
        if (hp != 1)
        {
            double raw = vita * 0.5 + mana * 0.5, armor = PitTrapArmorFactor(mob.Ac, -95);
            double amp = mob.TakeDamageAmp(Environment.TickCount64);
            int dmg = PitTrapDamage(raw, armor, amp, 1.0, hp);
            if (dmg > 0)
            {
                // PitTrapDamage's own product, so the cap at landing (every factor 1) compares the very same figure.
                _pitStrikeFigures.GetOrCreateValue(damageQueue)[damageQueue.Count] = raw * armor * (amp > 1.0 ? amp : 1.0) * 1.0;
                damageQueue.Add((mapId, mob, dmg, trap.OwnerId));
            }
        }
        _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, PitTrapAnim, 0));
    }

    /// <summary>A creature's Pit trap strike lands (the tick's trap-damage pass, at the strike's own place in it; #423's
    /// review, F2, and its re-check, R1). <paramref name="figure"/> is the strike as it sprang, armour and sleep amplifier
    /// in (<see cref="SpringPitTrapLocked"/>). Under <c>_lock</c>, against the vita the creature has now
    /// (<see cref="PitTrapDamage"/>), so the strike never takes the last vita whatever landed on it first in the beat. An
    /// amplifier armed on the creature since the spring (a Sleep or Doze cast between the tick's step and this pass) is the
    /// one <see cref="TryDamage"/> would multiply the hit by: it is spent here, into the figure the cap reads, so the cap
    /// holds against the blow that really lands and <see cref="TryDamage"/> finds none (#423's second re-check, S1). A
    /// strike that leaves the creature standing lands inside that same hold of the lock, so no other blow can come between
    /// the cap and the hit; with nothing left to multiply it, that hit cannot kill and sends nothing from inside the lock (no
    /// drops). Then the over-head bar. Only a strike exactly equal to the vita left kills (RTK's strict test); it lands
    /// outside the lock through <see cref="ApplyTrapDamage"/>, as every killing trap hit does, since a kill sends its drops,
    /// and is credited to the owner.</summary>
    private void ApplyPitStrike(ushort mapId, Mob mob, double figure, uint ownerId)
    {
        int dmg;
        bool landed = false, died = false;
        lock (_lock)
        {
            if (!mob.Alive || mob.IsNpc) return;
            int hp = mob.Hp;
            dmg = hp == 1 ? 0 : PitTrapDamage(figure, 1.0, mob.TakeDamageAmp(Environment.TickCount64), 1.0, hp);
            if (dmg <= 0) return;
            if (dmg < hp)
            {
                if (!TryDamage(mapId, mob, dmg, out died, ownerId)) return;
                Debug.Assert(!died, "a Pit strike capped below the vita left killed inside the world lock");
                landed = true;
            }
        }
        if (!landed) { ApplyTrapDamage(mapId, mob, dmg, ownerId); return; }
        ShowTrapHit(mapId, mob, died, ownerId);
    }

    // ---- Spy kit's traps (RTK NPCs/trap/rogue_traps/Spy/, set by Spells/Subpaths/Spy/spy_kit.lua) -------------------
    // Each is RTK's trap NPC: never drawn (an NPC with a subtype is passed over by RTK's object look), never shown by Spot
    // traps or Watchful Eye (RTK's seeSpotTraps names only the rogue's eight; Session.RevealableTrapsNear), gone after its
    // 20 s if nothing has sprung it (the expiry sweep in Tick). A creature that steps onto one always springs it
    // (TriggerTrapLocked); a player only where the trap may touch them, alive (CheckPlayerTrapTrigger,
    // Session.SpringSpyTrap). A sprung trap is used up even when its effect is then refused, as RTK's click deletes the
    // NPC before it casts. Each effect is the trap's spell as RTK's script has it, through the statuses that exist:
    //   * toxic_spray: refused while any poison holds (checkIfCast(venoms)); else the poison, 1% of base vita a beat (at
    //     least 1, at most 1,000, never the last vita), graphic 1 at the cast and each beat, credited to the trap's owner
    //     for its whole time, whatever the victim does (the project owner, 2026-11-09, "Tick its full time": RTK's beat
    //     takes its vita through the victim's recorded attacker, which its engine clears at each of their casts).
    //   * tripwire: refused while a curse or a protection holds; else armour +30 (more damage taken) in the curses slot,
    //     graphic 1, and over a creature graphic 34 each beat (the script's while_cast).
    //   * smoke_screen: refused while a blind holds; else a creature's blind, a player's blinds slot.
    // A creature's figure is RTK's setDuration count, which its creature timer takes 1,000 off every 1.5 s
    // (rtk/src/map/mob.c, mob_timer_spawns and mob_duratimer), so it runs 1.5 times as long as on a player, whose
    // durations beat every second: the reading Awe and the Song of the Goddess took.
    internal const string ToxicSprayTrap = "toxic_spray", TripwireTrap = "tripwire", SmokeScreenTrap = "smoke_screen";
    /// <summary>Is <paramref name="kind"/> one of Spy kit's three traps?</summary>
    internal static bool IsSpyTrap(string kind) => kind is ToxicSprayTrap or TripwireTrap or SmokeScreenTrap;
    // The scripts' setDuration figures (toxic_spray.lua:27-31, tripwire.lua:28, smoke_screen.lua:27).
    internal const int ToxicSprayPcMs = 40_000, ToxicSprayMobFigure = 185_000;
    internal const int TripwireFigure = 37_000, SmokeScreenFigure = 25_000;
    internal const int ToxicSprayTickCap = 1000;                // toxic_spray.lua:53-58
    internal const int TripwireArmor = 30;                      // tripwire.lua:46 and its recast
    internal const int SpyTrapCastAnim = 1, TripwireBeatAnim = 34;
    private const int CreatureBeatMs = 1500;                    // RTK's creature duration timer, one 1,000 a beat

    /// <summary>How long RTK's creature timer holds a setDuration <paramref name="figure"/>: 1,000 off every 1.5 s.</summary>
    internal static int CreatureTimerMs(int figure) => figure / 1000 * CreatureBeatMs;

    /// <summary>Toxic spray's beat: 1% of <paramref name="baseVita"/>, rounded up as RTK's damage helper rounds
    /// (math.ceil), at least 1 and at most 1,000 (toxic_spray.lua:51-58).</summary>
    internal static int ToxicSprayTick(long baseVita) =>
        (int)Math.Clamp((long)Math.Ceiling(baseVita * 0.01), 1, ToxicSprayTickCap);

    /// <summary>A creature springs a Spy kit trap (caller holds <c>_lock</c>, mid-step; the status writes take it again,
    /// re-entrantly, as World's own methods). The cast graphic is queued with the tick's other deferred effects.</summary>
    private void SpringSpyTrapLocked(ushort mapId, Mob mob, Trap trap)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        switch (trap.Kind)
        {
            case ToxicSprayTrap:
            {
                int ms = CreatureTimerMs(ToxicSprayMobFigure);
                if (!PoisonMob(mob, ToxicSprayTickCap, ms - 1, ms - 1, trap.OwnerId, flatTick: ToxicSprayTick(mob.MaxHp),
                               fxAnim: SpyTrapCastAnim, spellKey: ToxicSprayTrap)) return;
                _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, SpyTrapCastAnim, 0));
                return;
            }
            case TripwireTrap:
            {
                if (MobHasStatus(mob, "curses") || MobHasStatus(mob, "minorcurses") || MobHasStatus(mob, "protections")) return;
                int ms = CreatureTimerMs(TripwireFigure);
                if (!ApplyMobStatus(mob, "curses", ms, hold: false, blind: false, fxAnim: TripwireBeatAnim, fxSound: 0,
                                    fxEveryMs: CreatureBeatMs, spellKey: TripwireTrap)) return;
                ApplyMobBuff(mob, "armor", TripwireArmor, ms, TripwireTrap);
                _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, SpyTrapCastAnim, 0));
                return;
            }
            case SmokeScreenTrap:
                ApplyMobStatus(mob, "blinds", CreatureTimerMs(SmokeScreenFigure), hold: false, blind: true,
                               spellKey: SmokeScreenTrap);
                return;
        }
    }

    /// <summary>Apply a trap hit / poison tick's damage (deferred out of the movement lock — see
    /// <see cref="TriggerTrapLocked"/>): mutate HP via the normal <see cref="TryDamage"/> path, broadcast the
    /// over-head damage number (and death despawn), and credit the trap owner with exp on a kill.</summary>
    private void ApplyTrapDamage(ushort mapId, Mob mob, int dmg, uint ownerId)
    {
        if (!TryDamage(mapId, mob, dmg, out bool died, ownerId)) return;
        ShowTrapHit(mapId, mob, died, ownerId);
    }

    /// <summary>What a trap hit that landed shows, outside <c>_lock</c>: the over-head bar, and on a kill the delayed corpse
    /// despawn and the trap owner's exp. <see cref="ApplyTrapDamage"/>'s own steps, shared with
    /// <see cref="ApplyPitStrike"/>, which no longer drops its hit's <paramref name="died"/>.</summary>
    private void ShowTrapHit(ushort mapId, Mob mob, bool died, uint ownerId)
    {
        byte pct = died ? (byte)0 : (byte)Math.Clamp(mob.Hp * 100 / Math.Max(1, mob.MaxHp), 1, 100);
        BroadcastWideArea(mapId, mob.X, mob.Y, p => p.DamageOver(mob.Id, pct, 33));
        if (died)
        {
            uint mobId = mob.Id;
            _ = Task.Run(async () => { try { await Task.Delay(600); Broadcast(mapId, p => p.DespawnEntity(mobId)); } catch (Exception e) { Log.Error($"delayed despawn of mob {mobId} threw", e); } });
            uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
            Online.ById(ownerId)?.AwardKillExp(reward, mapId, mob.X, mob.Y, mob.Key);
        }
    }

    /// <summary>A pet's swing landing on another mob. Same shape as <see cref="ApplyTrapDamage"/> — damage,
    /// over-head bar, delayed corpse despawn, exp to the human who owns the attacker — plus the melee
    /// swing/impact sfx a mob-on-mob hit should make, and retaliation.
    /// <para><b>attackerId stays 0 into TryDamage on purpose.</b> That parameter marks a PLAYER as the
    /// victim's new target, and routing the pet's owner in there would make every pet swing pull the mob
    /// straight onto its owner — the opposite of what a pet is for. Retaliation is handled below instead,
    /// and only when the victim isn't already busy with a player, so a pet can't soak a fight that was never
    /// aimed at it.</para></summary>
    private void ApplyMobOnMobHit(ushort mapId, Mob attacker, Mob victim, int dmg)
    {
        // Either side can have died to an earlier entry in this same batch — don't play a dead pet's swing.
        if (!attacker.Alive || !victim.Alive) return;
        BroadcastSameArea(mapId, attacker.X, attacker.Y, p => p.ActionOver(attacker.Id, GmOverrides.MobSwingActionType, GmOverrides.MobSwingActionTime, 0));  // visibly swing
        BroadcastSameArea(mapId, attacker.X, attacker.Y, p => p.SoundAt(Session.MobSwingSfx, attacker.Id));   // 009.wav on the swing itself, SAMEAREA like RTK's clif_playsound
        if (!TryDamage(mapId, victim, dmg, out bool died)) return;
        // The ONLY 0x13 that carries a hitSound byte, so this one is range-gated: 001.wav would otherwise ring
        // map-wide every time a pet landed a swing. RTK sends 0x13 over AREA everywhere (clif.c ~1305), but the
        // sound half belongs in the tighter SAMEAREA box, so the two go out separately.
        byte vpct = died ? (byte)0 : (byte)Math.Clamp(victim.Hp * 100 / Math.Max(1, victim.MaxHp), 1, 100);
        BroadcastWideArea(mapId, victim.X, victim.Y, p => p.DamageOver(victim.Id, vpct, 0));
        BroadcastSameArea(mapId, victim.X, victim.Y, p => p.SoundAt(Session.MobHitSfx, victim.Id));
        if (died)
        {
            uint victimId = victim.Id;
            _ = Task.Run(async () => { try { await Task.Delay(600); Broadcast(mapId, p => p.DespawnEntity(victimId)); } catch (Exception e) { Log.Error($"delayed despawn of mob {victimId} threw", e); } });
            // The owner gets the kill: RTK credits a mob's damage to map_id2sd(mob->owner) the same way
            // (clif.c's `tmob->owner < MOB_START_NUM` lookup), so a pet kill counts as yours.
            // …but NOT for a conjured victim. Now that a poet's pets will turn on a sibling he has attacked,
            // paying exp here would be the same summon-and-kill loop Session.ResolveSwing refuses, just
            // routed through a second pet. Same rule, same reason.
            if (!victim.Summoned)
            {
                uint reward = (uint)(victim.Exp > 0 ? victim.Exp : victim.MaxHp);
                Online.ById(attacker.OwnerId)?.AwardKillExp(reward, mapId, victim.X, victim.Y, victim.Key);
            }
            return;
        }
        lock (_lock) if (victim.Alive && victim.TargetId == 0) victim.TargetMobId = attacker.Id;
    }

    /// <summary>Confuse (RTK mage/confuse.lua) SUCCESS effect: wipe the mob's ENTIRE threat table and drop its
    /// current target so it forgets everyone. Confuse is a full aggro RESET, not the per-caster peel that
    /// Amnesia is. If a creature is standing on a cardinally-adjacent tile, the confused mob is turned on THAT
    /// creature (<see cref="Mob.TargetMobId"/>) — which is how two mobs side by side, Blinded first so they
    /// don't simply re-aggro you, can be spammed with Confuse into fighting each other; the victim's own
    /// retaliation (<see cref="ApplyMobOnMobHit"/>) then closes the loop. No status and no timer: this just
    /// re-points the AI once, so a still-sighted aggressive mob is free to re-acquire the nearest player next
    /// tick (blind it first if you want the redirect to stick). Caller need not hold the lock.</summary>
    public void ConfuseMob(ushort mapId, Mob mob)
    {
        lock (_lock)
        {
            mob.Threat?.Clear();
            mob.TargetId = 0;
            mob.AmnesiaBy = 0; mob.AmnesiaUntil = 0;   // a full reset supersedes any earlier Amnesia peel
            mob.AttackTimer = 0;
            mob.TargetMobId = 0;
            if (!_maps.TryGetValue(mapId, out var m)) return;
            var foes = m.Mobs.Where(o => o.Alive && !o.IsNpc && o.Id != mob.Id
                            && ((o.X == mob.X && Math.Abs(o.Y - mob.Y) == 1) || (o.Y == mob.Y && Math.Abs(o.X - mob.X) == 1)))
                         .ToList();
            if (foes.Count > 0) mob.TargetMobId = foes[Random.Shared.Next(foes.Count)].Id;
        }
    }

    // ---- mob movement (World.MobMovement.cs) --------------------------------------------------
    // The step primitives — StepMobToward, StepMobAway, StepMobStraight, StepMobTo, Dart and the MobBlocked
    // gate — are World.MobMovement (#37, section 2). No movement state is left in this file: the
    // sideways-shuffle bookkeeping a blocked chaser used to keep went with #149, having been replaced by
    // RTK's stateless random walk on 2026-08-24 and written but never read since.

    /// <summary>Remaining-HP percent for a mob's over-head bar — 1..100 while alive so a living creature's
    /// bar never reads empty. Mirrors Session's own private HpPercent(Mob); the two must agree or a healed
    /// bar would jump to a different scale than a damaged one.</summary>
    private static byte MobHpPercent(Mob m)
    {
        int max = Math.Max(1, m.MaxHp);
        int cur = Math.Clamp(m.Hp, 0, max);
        if (cur <= 0) return 0;
        return (byte)Math.Max(1, (int)((long)cur * 100 / max));
    }

    /// <summary>A player swung at <paramref name="mob"/> — hit OR miss. A prey creature (<see cref="Mob.Flees"/>)
    /// bolts: it stays spooked for <see cref="PanicMs"/>, refreshed by each further swing, which WIDENS the
    /// distance at which it notices you (<see cref="FleeRadius"/>) rather than changing how far its dart
    /// carries — see <see cref="MobMovement.Dart"/> and <see cref="PreyDartTiles"/>. No effect on anything
    /// else — an ordinary mob is provoked by <see cref="TryDamage"/>, which needs damage to have landed.</summary>
    public void Spook(Mob mob)
    {
        if (!mob.Flees) return;
        lock (_lock) mob.PanicUntil = Environment.TickCount64 + PanicMs;
    }

    private MapState Map(ushort id)
    {
        if (!_maps.TryGetValue(id, out var m)) { m = new MapState(); _maps[id] = m; }
        return m;
    }

    // ---- players joining / leaving a map ------------------------------------------------------

    /// <summary>Register <paramref name="s"/> on <paramref name="mapId"/>, broadcast it to everyone
    /// already there, and return the peers + mobs the caller should draw for the newcomer. A spawn row that
    /// throws while the room is filled costs only itself: the entry goes ahead, and the fault is logged once,
    /// after the lock (<see cref="SpawnDirector.EnsureMaterialized"/>).</summary>
    public (PeerTile[] peers, Mob[] mobs) EnterMap(Session s, ushort mapId)
    {
        // BEFORE the lock: first entry to a map runs EnsureMaterialized, whose spawn placement reads the
        // terrain (FreeSpawnTile -> MapData.For) — a disk read, a full cell decode and a SQLite query. Held
        // under _lock that froze every player on every OTHER map too, for as long as the load took. Warming
        // the cache out here makes the locked section a pure in-memory hit. See MapData.Prewarm. A boss filed
        // under this map as one of its rooms' maps can be built on another of them by this entry (#333), so
        // those maps are warmed too.
        MapData.Prewarm(mapId);
        SpawnDirector.PrewarmRoomMaps(new ReadOnlySpan<ushort>(in mapId));

        PeerTile[] peers; Mob[] mobs; PeerTile newcomer;
        List<EntryFault>? faults = null;   // spawn rows that threw on this entry; null on an ordinary one
        lock (_lock)
        {
            // The room is filled BEFORE the newcomer joins the player list below, and that order stays. The
            // tile they are arriving on is handed to the fill instead, as taken, so no creature, ambush trap or
            // cold tile is placed on it (#122). Read under the lock, like the newcomer snapshot further down.
            faults = _spawnDirector.EnsureMaterialized(mapId, (s.PlayerX, s.PlayerY));   // instantiate this map's spawns on first entry
            var m = Map(mapId);
            if (!m.Players.Contains(s)) m.Players.Add(s);
            m.ViewGen++;                    // a player roster change — the peer sweep reads this list
            // Seed the weather cache to what the newcomer is about to be shown (Session sends it on entry via
            // Weather.Get), so the tick's period-rollover diff compares against the on-screen state and never
            // skips a real change as a no-op — otherwise a player who entered mid-period could stay stuck on
            // stale weather when the period rolls to a value that happens to match the default-0 cache.
            m.Weather = Weather.For(mapId);
            peers = m.Players.Where(p => p != s).Select(p => new PeerTile(p, p.PlayerId, p.PlayerX, p.PlayerY)).ToArray();
            mobs = m.Mobs.ToArray();
            // The newcomer's own tile is snapshotted here too: the loop below draws THEM on every peer's
            // client, so it is their coordinates the peers' viewport gates read.
            newcomer = new PeerTile(s, s.PlayerId, s.PlayerX, s.PlayerY);
        }
        // Static lambda over a (p, newcomer) tuple, not Try(() => …) — the per-peer shape Broadcast uses, for
        // the same reason: this loop is once per peer per map entry, and a capture here is a display class
        // plus a delegate on each of them.
        foreach (var p in peers) Try((p, newcomer), static t => t.p.Session.SyncPeer(t.newcomer), "SyncPeer (EnterMap)");   // tell the room about the newcomer (view-gated + tracked)
        // A spawn row that threw while the room was filled was skipped and the entry went ahead. Its fault is
        // written now, by this thread, with the lock released (World.MobFaults.cs), never under it (#109).
        if (faults is not null) LogEntryFaults(faults);
        return (peers, mobs);
    }

    /// <summary>Read-only: the peers + mobs on <paramref name="mapId"/> (excluding <paramref name="s"/>),
    /// WITHOUT registering or broadcasting. Used to re-assert the view after a client-side map rebuild
    /// (e.g. an in-place 0x15 refresh) drops all foreign entities.</summary>
    public (PeerTile[] peers, Mob[] mobs) View(Session s, ushort mapId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return (Array.Empty<PeerTile>(), Array.Empty<Mob>());
            return (m.Players.Where(p => p != s).Select(p => new PeerTile(p, p.PlayerId, p.PlayerX, p.PlayerY)).ToArray(),
                    m.Mobs.ToArray());
        }
    }

    /// <summary>The pooled twin of <see cref="View"/>, for the one caller that can give the buffers straight
    /// back: an accepted walk step (<c>Session.HandleWalk</c>), which runs about 1,200 times a second on a
    /// walking population and paid 16.6 KB a step for the two arrays <see cref="View"/> builds under the
    /// lock — a <c>Where(…).Select(…).ToArray()</c> over every peer plus a <c>ToArray()</c> over every mob,
    /// measured at 11.1 µs Debug / 6.6 µs Release per step on a 400-player / 386-mob fixture.
    ///
    /// <para>Same treatment, same reasoning and the same hazards as <see cref="Broadcast"/>: the buffers are
    /// rented from <see cref="ArrayPool{T}"/> and filled by a plain loop with the SAME filter
    /// (<c>p != s</c>) and the SAME order, under <c>_lock</c> exactly where the LINQ ran and for less time.
    /// A single reusable buffer pair on <see cref="World"/> would be wrong for the reason it is wrong there:
    /// the tick thread and every session thread reach this concurrently, so each call needs its own.</para>
    ///
    /// <para>A rented array is at LEAST as long as the fill, so the counts come back beside it and the
    /// caller must walk <see cref="ViewSnapshot.PeerCount"/> / <see cref="ViewSnapshot.MobCount"/> entries
    /// and not <c>Length</c>. THE SNAPSHOT MUST NOT OUTLIVE THE CALL: give it back with
    /// <see cref="ReturnView"/> in a <c>finally</c>, and never store it, a slice of it, or a reference into
    /// it in a field, a closure or a deferred send — a use after return is silent and lands in whichever
    /// caller rents that buffer next. Callers that cannot promise that (<c>ResyncPeers</c>,
    /// <c>RedrawWorld</c>, the tests) keep using <see cref="View"/>, which is unchanged.</para></summary>
    public ViewSnapshot ViewPooled(Session s, ushort mapId)
    {
        PeerTile[] peers; Mob[] mobs; int peerCount, mobCount;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m))
                return new ViewSnapshot(Array.Empty<PeerTile>(), 0, Array.Empty<Mob>(), 0);
            peers = ArrayPool<PeerTile>.Shared.Rent(m.Players.Count);
            peerCount = 0;
            foreach (var p in m.Players) if (p != s) peers[peerCount++] = new PeerTile(p, p.PlayerId, p.PlayerX, p.PlayerY);
            mobs = ArrayPool<Mob>.Shared.Rent(m.Mobs.Count);
            mobCount = 0;
            foreach (var mo in m.Mobs) mobs[mobCount++] = mo;
        }
        return new ViewSnapshot(peers, peerCount, mobs, mobCount);
    }

    /// <summary>Give a <see cref="ViewPooled"/> snapshot's buffers back, with the slots the fill used wiped
    /// first. The wipe is the same leak fix <see cref="ReturnPeers"/> documents, twice over: a pooled array
    /// is a live GC root for as long as the pool holds it, and here BOTH buffers carry references — every
    /// <see cref="PeerTile"/> holds a <see cref="Session"/>, and the mob buffer holds the <see cref="Mob"/>
    /// objects themselves. Only the filled slots are ours to clear; anything past them belongs to whoever
    /// rented the buffer last. The empty snapshot an unknown map returns owns nothing, so it is skipped —
    /// <see cref="Array.Empty{T}()"/> is not the pool's to take back.</summary>
    public static void ReturnView(in ViewSnapshot v)
    {
        if (v.Peers.Length > 0) { Array.Clear(v.Peers, 0, v.PeerCount); ArrayPool<PeerTile>.Shared.Return(v.Peers); }
        if (v.Mobs.Length > 0) { Array.Clear(v.Mobs, 0, v.MobCount); ArrayPool<Mob>.Shared.Return(v.Mobs); }
    }

    /// <summary>Remove <paramref name="s"/> from <paramref name="mapId"/> and despawn it for the rest.</summary>
    public void LeaveMap(Session s, ushort mapId)
    {
        Session[] peers;
        uint id = s.PlayerId;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            m.Players.Remove(s);
            // A player roster change. Redundant today — the despawn broadcast below flags every remaining
            // viewer by itself, and deleting this line breaks no fact (the falsification table in
            // briefs/reports/tick-sweep-skip-opus.md, case 2) — and kept because the roster is what moved: a
            // reader of ViewGen is entitled to hear that without also having to know that this particular
            // caller happens to broadcast a despawn afterwards.
            m.ViewGen++;
            peers = m.Players.ToArray();
        }
        foreach (var p in peers) Try((p, id), static t => t.p.DespawnEntity(t.id), "DespawnEntity (LeaveMap)");
    }

    // ---- broadcasts ---------------------------------------------------------------------------

    /// <summary>Run <paramref name="send"/> for every player on <paramref name="mapId"/> (except
    /// <paramref name="except"/>), outside the lock and exception-guarded.
    ///
    /// <para>The per-peer step goes through <see cref="Try{T}(T, Action{T}, string)"/> with a STATIC lambda
    /// over a <c>(p, send)</c> tuple rather than <c>Try(() =&gt; send(p), …)</c>, for the reason that overload
    /// exists: a capturing lambda here is a display class plus a delegate PER PEER PER CALL — measured on this
    /// repo, Debug and Release alike, at 96 B a peer, which is 38,408 B of the 45,361 B one call costs at 400
    /// players. This is the helper the tick uses once per queued mob move and once per turn, and that every
    /// player action reaches from its own session thread. The static lambda captures nothing, so one delegate
    /// is cached per call site for the life of the process and the loop hands it a different tuple each time
    /// — the tuple is a struct and stays on the stack. The isolation, the catch, the log line, the peer order
    /// and the <paramref name="except"/> filter are all unchanged. <c>Tests/BroadcastIsolationTests.cs</c>
    /// pins the per-peer zero and the isolation.</para>
    ///
    /// <para>With the closures gone the SNAPSHOT was all that was left — 6,953 B a call at 400 players, the
    /// <c>Where(…).ToArray()</c> iterator, its growth copies and the final array — so it is rented from
    /// <see cref="ArrayPool{T}"/> and filled by a plain loop with the same filter and the same order instead.
    /// That takes the helper to nothing at all, and is also 2.4x faster than the LINQ it replaces (2,503 ns
    /// against 5,963 ns for one 400-peer call, Release). A single reusable buffer on <see cref="World"/>
    /// would be wrong here: the tick thread and every session thread call this concurrently, so each call
    /// needs its own. The rent happens under the lock with the fill, and the buffer is wiped and returned in
    /// a <c>finally</c> — see <see cref="ReturnPeers"/> for why the wipe is not optional.</para></summary>
    public void Broadcast(ushort mapId, Action<Session> send, Session? except = null)
    {
        Session[] peers; int count;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            peers = ArrayPool<Session>.Shared.Rent(m.Players.Count);
            count = 0;
            foreach (var p in m.Players) if (p != except) peers[count++] = p;
        }
        try
        {
            for (int i = 0; i < count; i++) Try((p: peers[i], send), static t => t.send(t.p), "Broadcast");
        }
        finally { ReturnPeers(peers, count); }
    }

    /// <summary>Like <see cref="Broadcast"/>, but only to players inside a box of ±<paramref name="halfW"/> ×
    /// ±<paramref name="halfH"/> around (<paramref name="cx"/>, <paramref name="cy"/>) — RTK's SAMEAREA
    /// proximity range, used for normal speech (clif_sendscriptsay's clif_send(..., SAMEAREA), the x±9/y±8 box)
    /// and for every sound (clif_playsound). Shout is NOT one of these: RTK's engine sends it SAMEMAP and we
    /// follow speech.lua's distance 16 instead, so it passes a bigger box and turns the shift off.
    /// <para><paramref name="edgeShift"/> reproduces RTK's SAMEAREA edge behaviour — see
    /// <see cref="ShiftedBox"/>. Leave it on for a SAMEAREA box; turn it off for anything else.</para></summary>
    public void BroadcastArea(ushort mapId, int cx, int cy, int halfW, int halfH, Action<Session> send,
                              Session? except = null, bool edgeShift = true)
    {
        var (x0, y0, x1, y1) = edgeShift
            ? ShiftedBox(mapId, cx, cy, halfW, halfH)
            : (cx - halfW, cy - halfH, cx + halfW, cy + halfH);
        Session[] peers; int count;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            peers = ArrayPool<Session>.Shared.Rent(m.Players.Count);
            count = 0;
            foreach (var p in m.Players)
                if (p != except
                    && p.PlayerX >= x0 && p.PlayerX <= x1
                    && p.PlayerY >= y0 && p.PlayerY <= y1) peers[count++] = p;
        }
        try
        {
            // Static lambda over a (p, send) tuple, not Try(() => send(p), …) — see Broadcast above for why.
            for (int i = 0; i < count; i++) Try((p: peers[i], send), static t => t.send(t.p), "BroadcastArea");
        }
        finally { ReturnPeers(peers, count); }
    }

    /// <summary>Give a rented peers buffer back, with the slots the caller filled wiped first.
    ///
    /// <para>The wipe is not hygiene, it is a leak fix: a pooled array is a live GC root for as long as the
    /// pool holds it, so a buffer returned with 400 <see cref="Session"/> references still in it would keep
    /// 400 logged-out players — and everything they reference — alive until that exact buffer happened to be
    /// rented again. Only the first <paramref name="count"/> slots are ours to clear; anything past them
    /// belongs to whoever rented this buffer last (<see cref="ArrayPool{T}"/> hands back an array that is at
    /// LEAST the requested length, and a bigger one carries the previous tenant's tail).</para></summary>
    private static void ReturnPeers(Session[] peers, int count)
    {
        Array.Clear(peers, 0, count);
        ArrayPool<Session>.Shared.Return(peers);
    }

    /// <summary>The SAMEAREA box for a map id — <see cref="ShiftBox"/> against that map's dims. A map the
    /// registry doesn't know has no edges to slide against, so it falls back to a plain centred box.</summary>
    private static (int x0, int y0, int x1, int y1) ShiftedBox(ushort mapId, int cx, int cy, int halfW, int halfH)
        => Content.Maps.TryGetValue(mapId, out var mi) && mi.Xs > 0 && mi.Ys > 0
            ? ShiftBox(cx, cy, halfW, halfH, mi.Xs, mi.Ys)
            : (cx - halfW, cy - halfH, cx + halfW, cy + halfH);

    /// <summary>RTK's SAMEAREA box (map.c <c>map_foreachinarea</c>): a ±<paramref name="halfW"/>/±<paramref
    /// name="halfH"/> rect around the source that is SLID back inside the map rather than clipped when it would
    /// hang off an edge. Standing against the west wall therefore reaches twice as far east — the box keeps its
    /// full size, it just stops being centred on you. Per axis, and SAMEAREA only: RTK's AREA case passes a
    /// plain centred box that <c>map_foreachinblockva</c> merely clamps, which is why
    /// <see cref="BroadcastWideArea"/> opts out.
    /// <para>A map SMALLER than the box collapses to the whole map, exactly as RTK's does — both shifts fire
    /// and cancel. Bounds are INCLUSIVE on both ends, matching map_foreachinblockva's <c>&gt;= x0 &amp;&amp;
    /// &lt;= x1</c>.</para></summary>
    public static (int x0, int y0, int x1, int y1) ShiftBox(int cx, int cy, int halfW, int halfH, int xs, int ys)
    {
        int x0 = cx - halfW, y0 = cy - halfH, x1 = cx + halfW, y1 = cy + halfH;
        // Order matters and mirrors RTK exactly: push off the low edge first, then pull back off the high one.
        if (x0 < 0)   { x1 += -x0; x0 = 0; if (x1 >= xs) x1 = xs - 1; }
        if (y0 < 0)   { y1 += -y0; y0 = 0; if (y1 >= ys) y1 = ys - 1; }
        if (x1 >= xs) { x0 -= x1 - xs + 1; x1 = xs - 1; if (x0 < 0) x0 = 0; }
        if (y1 >= ys) { y0 -= y1 - ys + 1; y1 = ys - 1; if (y0 < 0) y0 = 0; }
        return (x0, y0, x1, y1);
    }

    // ---- hearing / seeing ranges (RTK map.c map_foreachinarea) -----------------------------------
    //
    // RTK never sends a sound or a spell graphic to a whole map: clif_playsound ends in
    // clif_send(..., SAMEAREA) and clif_sendanimation is always driven by map_foreachinarea(..., AREA),
    // and BOTH resolve to a box around the SOURCE entity, not the map:
    //
    //   SAMEAREA (sound)     x +/- 9,  y +/- 8    -- map.c's x0/y0/x1/y1, i.e. one screen
    //   AREA     (animation) x +/- 19, y +/- 17   -- AREAX_SIZE+1 / AREAY_SIZE+1, about two screens
    //
    // The sound box is deliberately the tighter one: it is roughly the 17x15 viewport, so the rule is
    // "you hear what you can see". The animation box is looser because a 0x29 over an entity the client
    // never drew is a no-op anyway, so RTK does not bother trimming it to the screen.
    //
    // The sound box also carries RTK's edge SHIFT (see ShiftedBox): against a wall it slides inward instead
    // of being clipped, so hugging the west edge lets you hear twice as far east. That is SAMEAREA-only in
    // RTK -- the AREA case passes a plain centred box -- so BroadcastWideArea below opts out of it.
    public const int SoundHalfW = 9,  SoundHalfH = 8;
    public const int FxHalfW    = 19, FxHalfH    = 17;

    /// <summary>RTK's <c>SAMEAREA</c> box (+/-9/+/-8, edge-shifted) around the tile the thing happens ON.
    /// Carries <c>clif_playsound</c> (every sfx) and <c>clif_sendaction</c> (the 0x1A pose). Centre it on the
    /// entity the packet is BOUND to, not on whoever caused it — RTK binds a landed hit to the VICTIM
    /// (<c>clif_playsound(&amp;mob-&gt;bl, itemdb_soundhit(...))</c>).</summary>
    public void BroadcastSameArea(ushort mapId, int cx, int cy, Action<Session> send, Session? except = null)
        => BroadcastArea(mapId, cx, cy, SoundHalfW, SoundHalfH, send, except);

    /// <summary>RTK's looser <c>AREA</c> box (+/-19/+/-17, plain and centred — RTK does not edge-shift this
    /// one). Carries <c>clif_sendanimation</c> (0x29) and <c>clif_damage</c> (0x13, the over-head HP bar).
    /// Strictly contains the client's drawn rect (19x17 tiles = +/-9/+/-8), so it can never cut something a
    /// viewer could actually have seen.</summary>
    public void BroadcastWideArea(ushort mapId, int cx, int cy, Action<Session> send, Session? except = null)
        => BroadcastArea(mapId, cx, cy, FxHalfW, FxHalfH, send, except, edgeShift: false);

    /// <summary>The tile an entity id currently occupies on <paramref name="mapId"/> -- a live mob first,
    /// then a connected player -- or null when it is already gone (a mob that died between queueing an
    /// effect and flushing it). Callers use it to centre a sound/effect box on the thing making the noise.</summary>
    public (ushort x, ushort y)? EntityPos(ushort mapId, uint id)
    {
        lock (_lock)
        {
            if (_maps.TryGetValue(mapId, out var m))
            {
                var mob = m.Mobs.FirstOrDefault(mo => mo.Alive && mo.Id == id);
                if (mob is not null) return (mob.X, mob.Y);
            }
            var pc = Online.ByIdLocked(id);
            return pc is null ? null : ((ushort, ushort)?)(pc.PlayerX, pc.PlayerY);
        }
    }

    // ---- mobs ---------------------------------------------------------------------------------

    /// <summary>Full population hot-reload (the <c>@reload</c> path, after <see cref="Content.Reload"/> swapped
    /// the spawn/NPC registries): tear down every spawn-backed mob AND stationary NPC, rebuild the spawn roster
    /// + NPC placement from the fresh <see cref="Content"/>, and re-materialize the maps that currently have
    /// players (the rest build lazily on next entry). Unlike the old in-place re-stat, this picks up
    /// ADDED / REMOVED / REPOSITIONED spawn rows and NPCs — editing <c>AreaSpawns.csv</c>/<c>Spawns.csv</c> or
    /// an NPC's tile now takes effect on <c>@reload</c> without a restart. Cost is bounded: only populated maps
    /// re-materialize; the ~23k lazy points elsewhere are cheap point objects again. Players on a populated map
    /// briefly see mobs blink (despawn now, re-stream on the next <see cref="Tick"/>) — acceptable for an admin
    /// reload; a wounded mob also resets to full, since area spawns have no stable identity to preserve.
    /// A boss filed under its rooms' maps (#333) is re-built once if any of those maps has a player, in whichever
    /// room its pick lands on; <see cref="ReloadFromDisk"/> warms those rooms' maps before this takes the lock.
    /// Returns (mobs torn down, NPCs placed, maps re-materialized).</summary>
    public (int mobs, int npcs, int maps) RebuildPopulation()
    {
        var despawn = new List<(ushort map, uint id)>();
        var populated = new List<ushort>();
        int npcs = 0;
        lock (_lock)
        {
            // 1. tear down every shared mob (spawn-backed AND stationary NPC) on every map, remembering which
            //    maps have players so we know what to re-materialize. (Session-local debug dummies aren't in
            //    _maps, so they're untouched.)
            foreach (var (mapId, m) in _maps)
            {
                if (m.Players.Count > 0) populated.Add(mapId);
                foreach (var g in m.Mobs) despawn.Add((mapId, g.Id));
                m.Mobs.Clear();
                m.ViewGen++;                // a mob roster change
            }
            // 2. rebuild the spawn roster + NPC placement from the just-reloaded Content (fresh defs, positions,
            //    and any added/removed rows). NPCs are placed on every map (cheap, ~340); mobs stay lazy.
            _spawnDirector.Clear();
            _spawnDirector.Build();
            foreach (var n in Content.Npcs) if (n.Enabled) { PlaceNpc(n); npcs++; }
            // 3. re-materialize only the maps that currently have players; the rest build lazily on next entry.
            foreach (var mapId in populated) _spawnDirector.EnsureMaterialized(mapId);
        }
        // Despawn the torn-down entities on clients (socket I/O outside the lock). The freshly placed NPCs +
        // materialized mobs stream back to players via the next Tick's viewport sync (~one tick later).
        foreach (var (map, id) in despawn) Broadcast(map, p => p.DespawnEntity(id));
        return (despawn.Count, npcs, populated.Count);
    }

    /// <summary>Map ids that currently have at least one player — used by the @reload path to pre-warm the
    /// terrain cache OUTSIDE this lock before <see cref="RebuildPopulation"/> re-materializes them (so the
    /// .map re-reads don't happen under the world lock, per the reload-stall fix).</summary>
    public List<ushort> PopulatedMapIds()
    {
        lock (_lock) return _maps.Where(kv => kv.Value.Players.Count > 0).Select(kv => kv.Key).ToList();
    }

    /// <summary>Add a shared mob to a map and stream it to everyone whose viewport it falls in (players
    /// out of range receive it later, as they approach, via <see cref="Tick"/>'s per-player sync).</summary>
    public void AddMob(ushort mapId, Mob mob)
    {
        lock (_lock) { var m = Map(mapId); m.Mobs.Add(mob); m.ViewGen++; }   // a mob roster change
        var one = new[] { mob };
        Broadcast(mapId, p => p.SyncMobs(one));
    }

    /// <summary>Give a mob to a player for a fixed duration, if nobody owns it already.</summary>
    public bool CharmMob(Mob mob, uint ownerId, int durMs)
    {
        lock (_lock)
        {
            if (mob.OwnerId != 0) return false;
            mob.OwnerId = ownerId;
            mob.PetExpiresAt = Environment.TickCount64 + durMs;
            mob.TargetId = 0;
            return true;
        }
    }

    /// <summary>Give a newly created summon to its owner; unlike Endear, this cannot be refused.</summary>
    public void OwnSummonedMob(Mob mob, uint ownerId, int durMs)
    {
        lock (_lock)
        {
            Debug.Assert(mob.OwnerId == 0, "a newly created summon must not already have an owner");
            mob.OwnerId = ownerId;
            mob.PetExpiresAt = Environment.TickCount64 + durMs;
            mob.Summoned = true;
            mob.TargetId = 0;
        }
    }

    /// <summary>Give a conjured creature a LIFESPAN but no owner — a scripted ambush (Master Dagger's
    /// assassins) rather than a pet. It hunts like any wild mob, it pays nothing when killed (that is what
    /// <see cref="Mob.Summoned"/> already means: made out of nothing seconds ago), and <see cref="Tick"/>
    /// despawns it when the timer lapses, on the same path a lapsed summon takes.</summary>
    public void ExpireUnowned(Mob mob, int durMs)
    {
        lock (_lock)
        {
            mob.PetExpiresAt = Environment.TickCount64 + durMs;
            mob.Summoned = true;
        }
    }

    /// <summary>Make a mob forget one player's threat for a fixed duration.</summary>
    public void ForgetPlayer(Mob mob, uint playerId, int durMs)
    {
        lock (_lock)
        {
            mob.AmnesiaBy = playerId;
            mob.AmnesiaUntil = Environment.TickCount64 + durMs;
            mob.ClearThreat(playerId);
            if (mob.TargetId == playerId) mob.TargetId = 0;
        }
    }

    /// <summary>Arm the one-hit damage multiplier left by a sleep-family hold.</summary>
    public void ArmDamageAmp(Mob mob, double mult, int durMs)
    {
        lock (_lock)
        {
            mob.DamageAmp = mult;
            mob.DamageAmpUntil = Environment.TickCount64 + durMs;
        }
    }

    /// <summary>Heal a living mob, capped at its maximum HP.</summary>
    public bool HealMob(Mob mob, int amount)
    {
        lock (_lock)
        {
            if (amount <= 0 || !mob.Alive) return false;
            mob.Hp = Math.Min(mob.MaxHp, mob.Hp + amount);
            return true;
        }
    }

    /// <summary>Give a world mob an item to carry (RTK's hand-off: the creature drops it back when killed —
    /// see <see cref="TryDamage"/>). Under <c>_lock</c> because <c>Mob.Handed</c> is a plain <c>List</c> that
    /// the death path ENUMERATES under this lock while the player's read loop was adding to it: not a stale
    /// read but an "InvalidOperationException: Collection was modified" thrown inside the world lock, or a
    /// list lost outright when two hands race the <c>??=</c>. Same shape as <see cref="HealMobFromScript"/>:
    /// the caller decides what to hand over, the world owns the write.</summary>
    internal void HandItemToMob(Mob mob, InvItem item)
    {
        lock (_lock) (mob.Handed ??= new()).Add(item);
    }

    /// <summary>Settle a harvest node's claim and take it if it is free: the lapse check, the lazy heal that
    /// follows it, the owner test and the re-stamp, all in ONE acquisition. Returns false if somebody else
    /// holds it, and the caller says so.
    ///
    /// <para><b>Why one method and not the three the issue sketched.</b> Reset-then-check-then-claim as
    /// separate lock-owning calls would put each write under the lock and leave the DECISION spanning three
    /// of them — two players swinging at the same node in the same instant could both see it unclaimed and
    /// both stamp their own id, which is the exact check-then-act shape #26 exists to remove. The semantics
    /// are unchanged: a claim lapses on time, a lapsed node heals to full, a live claim belonging to someone
    /// else refuses, and your own claim is refreshed.</para>
    ///
    /// <para>The node's HP damage is not here: it already goes through <see cref="TryDamage"/>, which takes
    /// this lock itself.</para>
    ///
    /// <para><b>The clock is read here, inside the acquisition, and not by the caller before it.</b> The
    /// first cut took <c>now</c> as a parameter, so the lapse decision and the new expiry both used a reading
    /// from before the wait for the lock: a claim stamped fractionally short, and a claim that lapsed DURING
    /// the wait not seen. The magnitude is one lock hold against a two-minute claim, so nothing a player
    /// could feel — but the code this replaced took no lock at all, so the window was new, and "the semantics
    /// are exactly what they were" is only literally true with the read on this side of it.</para>
    ///
    /// <para><paramref name="clock"/> is a test seam and nothing else. Production passes nothing and the
    /// ternary reads <see cref="Environment.TickCount64"/> directly, so no caller-supplied delegate runs
    /// inside <c>_lock</c> on any real path; a test's clock has to stay pure, because reaching back into the
    /// world from inside this acquisition is the deadlock the #90 rule is about.</para></summary>
    internal bool TryClaimHarvestNode(Mob node, uint claimant, long claimMs, Func<long>? clock = null)
    {
        lock (_lock)
        {
            long now = clock is null ? Environment.TickCount64 : clock();

            // A node nobody is touching has nothing to observe it, so a lapsed claim is settled lazily on the
            // next swing rather than on a tick — indistinguishable from RTK's timer, and no per-tick work.
            if (node.HarvestClaimUntil != 0 && now > node.HarvestClaimUntil)
            {
                node.Hp = node.MaxHp;
                node.HarvestClaimBy = 0;
                node.HarvestClaimUntil = 0;
            }
            if (node.HarvestClaimBy != 0 && node.HarvestClaimBy != claimant) return false;

            node.HarvestClaimBy = claimant;
            node.HarvestClaimUntil = now + claimMs;
            return true;
        }
    }

    /// <summary>The Lua hook's heal (<c>MobContext.heal</c>), under <c>_lock</c> — the write it used to do
    /// straight to <c>mob.Hp</c> from inside a script, with no lock held at all.
    ///
    /// <para><b>Why this is not just <see cref="HealMob"/>.</b> That method refuses a non-positive amount
    /// and refuses a dead mob; this one does neither, because a script can already do both today and
    /// adopting those guards would change what a Lua heal DOES rather than only when its write lands.
    /// <c>heal(-5)</c> currently takes HP off a creature, and a <c>heal</c> from <c>after_death</c>
    /// currently revives one (<see cref="Mob.Alive"/> is <c>Hp &gt; 0</c>). Neither looks intended, and
    /// neither is this PR's to decide — see the follow-up note on <c>MobContext.heal</c>.</para>
    ///
    /// <para>Called from inside the Lua gate, which is the legal direction: the rule (#90) is that
    /// <c>_lock</c> must not be held when ENTERING the gate, not that the gate may not call into the world.
    /// <c>MobContext.vanish</c> (<see cref="DespawnMob"/>) and <c>MobContext.say</c>
    /// (<see cref="BroadcastArea"/>) have taken this lock from inside a script since the host was
    /// written.</para></summary>
    internal void HealMobFromScript(Mob mob, int amount)
    {
        lock (_lock) mob.Hp = Math.Min(mob.MaxHp, mob.Hp + amount);
    }

    /// <summary>The Lua hook's status test (<c>MobContext.hasStatus</c>), under <c>_lock</c> — the read it
    /// used to do straight off the mob from inside a script, with no lock held.
    ///
    /// <para>The same shape and the same reason as <see cref="HealMobFromScript"/>, and the same direction of
    /// call (#90: <c>_lock</c> must not be held ENTERING the Lua gate; the gate calling into the world is
    /// fine). This one is a READ, which is why the #100 review had to point it out — the PR that fixed the
    /// heal said the remaining <c>MobContext</c> reads were simple field reads and out of scope, and that was
    /// true of every member except this one. <c>Mob.HasStatus</c> walks <c>Mob.Statuses</c>, a plain
    /// <c>Dictionary</c> that <c>World.SetStatus</c>/<c>ClearStatus</c> write under the lock, and an
    /// unsynchronised dictionary read against a concurrent write is not a stale answer — it can loop or throw
    /// inside the lookup. It is also the one <c>MobContext</c> member a shipped script actually calls
    /// (<c>game-data/mob_ai.lua</c>), so it is live, not theoretical.</para></summary>
    internal bool MobHasStatusFromScript(Mob mob, string category)
    {
        lock (_lock) return mob.HasStatus(category, Environment.TickCount64);
    }

    /// <summary>Hold a mob for a fixed duration unless it is already held.</summary>
    public bool HoldMob(Mob mob, int durMs)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.FrozenUntil > now) return false;
            mob.FrozenUntil = now + durMs;
            return true;
        }
    }

    /// <summary>Apply a player's targeted timed stat buff (Session.CastTargetBuff — e.g. Valor/Harden Armor on a
    /// pet) to a mob, refresh-not-stack by spell key. Taken under <c>_lock</c> so the mob.Buffs list mutation
    /// can't race the Tick's expiry-revert pass (which runs the same list under the lock).</summary>
    public void ApplyMobBuff(Mob mob, string stat, int amount, int durMs, string key)
    {
        if (string.IsNullOrEmpty(stat) || amount == 0 || durMs <= 0) return;
        lock (_lock)
        {
            mob.Buffs ??= new();
            for (int i = mob.Buffs.Count - 1; i >= 0; i--)   // refresh: revert + drop any prior cast of THIS spell
                if (mob.Buffs[i].Key == key) { mob.AdjustBuffField(mob.Buffs[i].Stat, mob.Buffs[i].Amount, -1); mob.Buffs.RemoveAt(i); }
            mob.AdjustBuffField(stat, amount, +1);
            mob.Buffs.Add(new Mob.TimedBuff { Stat = stat, Amount = amount, ExpiresAt = Environment.TickCount64 + durMs, Key = key });
        }
    }

    /// <summary>Apply a hostile categorised status to a mob (RTK <c>checkIfCast</c> + <c>setDuration</c>): the
    /// exclusivity slot, whichever AI field the status actually drives, and — for the ones RTK re-draws in
    /// <c>while_cast</c> — the repeating over-head animation.
    /// <para>Returns <b>false</b> if a status of <paramref name="category"/> is already running, which is the
    /// whole point: an offensive hold cannot be stacked or refreshed on top of itself, so the victim gets the
    /// hold's full window back before it can be re-applied.</para>
    /// <paramref name="hold"/> freezes movement (paralyze/sleep/slow), <paramref name="blind"/> takes its
    /// sight. Both false = a pure stat curse, which only occupies the slot.</summary>
    public bool ApplyMobStatus(Mob mob, string category, int durMs, bool hold, bool blind,
                               int fxAnim = 0, int fxSound = 0, int fxEveryMs = 0, string spellKey = "")
    {
        if (durMs <= 0) return false;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.HasStatus(category, now)) return false;          // RTK checkIfCast — no stacking, no refresh
            long until = now + durMs;
            mob.SetStatus(category, until, spellKey);
            // Take the LATER of any running hold and this one, so a short paralyze can't cut a long sleep
            // short — the two are different categories and are allowed to overlap.
            if (hold)  mob.FrozenUntil = Math.Max(mob.FrozenUntil, until);
            if (blind) { mob.BlindUntil = Math.Max(mob.BlindUntil, until); mob.TargetId = 0; }
            if (fxAnim > 0 && fxEveryMs > 0) mob.SetFxRepeat(fxAnim, fxSound, fxEveryMs, until, now);
            return true;
        }
    }

    /// <summary>Does this mob already carry a status of <paramref name="category"/>? (The read-only half of
    /// <see cref="ApplyMobStatus"/>, for a verb that needs to check before it commits to anything.)</summary>
    public bool MobHasStatus(Mob mob, string category)
    {
        lock (_lock) return mob.HasStatus(category, Environment.TickCount64);
    }

    /// <summary>The spell key holding <paramref name="category"/>'s slot on this mob right now, or "" if it is
    /// free. Lets a blocked cast say whether it bounced off ITS OWN running spell or somebody else's.</summary>
    public string MobStatusKey(Mob mob, string category)
    {
        lock (_lock) return mob.StatusKey(category, Environment.TickCount64);
    }

    /// <summary>Apply a venom/poison damage-over-time to a mob (RTK mage venom.lua family — the SAME engine the
    /// Rogue poison-dart trap drives, see <see cref="TriggerTrapLocked"/>'s "poison" case): ticks MaxHp*1% every
    /// 1500ms for a random window (1 + random(<paramref name="lowMs"/>, <paramref name="highMs"/>)), the per-tick
    /// damage clamped to [1, <paramref name="tickCap"/>] so it can never itself land the killing blow (World.Tick
    /// only ticks while Hp > the tick amount). Returns false if the mob is already venomed (checkIfCast(venoms)).
    /// <para><paramref name="flatTick"/> &gt; 0 replaces the proportional MaxHp*1% with a FLAT per-tick amount —
    /// RTK's Burn (Spells/NPCs/burn.lua) is the one member of this family whose while_cast deals a hardcoded
    /// 1000 rather than a percentage, and clamping a flat 1000 through <paramref name="tickCap"/> would silently
    /// weaken it against anything under 100k HP.</para></summary>
    /// <param name="fxAnim">Effect id to re-draw over the victim on every 1500ms tick, mirroring RTK's
    /// <c>while_cast_1500</c>, which calls <c>target:sendAnimation(1)</c> each time it deals damage — the
    /// poison is meant to keep flashing for its whole window, not once at cast. 0 = no repeat (the trap
    /// path, whose RTK script draws nothing per tick).</param>
    public bool PoisonMob(Mob mob, int tickCap, int lowMs, int highMs, uint ownerId, int flatTick = 0,
                          int fxAnim = 0, int fxSound = 0, string spellKey = "")
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (mob.PoisonUntil > now) return false;                        // already venomed — RTK checkIfCast(venoms)
            mob.PoisonUntil     = now + 1 + Random.Shared.Next(lowMs, highMs + 1);
            mob.PoisonNextTick  = now + PoisonTickMs;
            mob.PoisonTickDam   = flatTick > 0 ? flatTick : Math.Clamp((int)(mob.MaxHp * 0.01), 1, tickCap);
            mob.PoisonOwnerId   = ownerId;
            mob.SetStatus("venoms", mob.PoisonUntil, spellKey);
            // Same 1500ms cadence as the damage tick, so the flash and the hit land together.
            if (fxAnim > 0) mob.SetFxRepeat(fxAnim, fxSound, PoisonTickMs, mob.PoisonUntil, now);
            return true;
        }
    }

    /// <summary>How many of this owner's pets (RTK Poet "Call of the Wild" summons) are currently alive on
    /// this map — the spawn cap in Content.PetCapFor is checked against this (RTK cotw_spawnCheck: same-map
    /// only, matching <c>player:getObjectsInMap</c>).</summary>
    public int PetCountFor(ushort mapId, uint ownerId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return 0;
            return m.Mobs.Count(mo => mo.Alive && mo.OwnerId == ownerId);
        }
    }

    /// <summary>The first living mob on (x,y) of <paramref name="mapId"/>, or null.</summary>
    public Mob? MobAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.FirstOrDefault(mo => mo.Alive && mo.X == x && mo.Y == y);
        }
    }

    /// <summary>
    /// A player's step: decide whether (<paramref name="nx"/>,<paramref name="ny"/>) is occupied and, if the
    /// step stands, write the mover's position — both inside ONE acquisition of <c>_lock</c> (#30).
    ///
    /// <para><b>Why it has to be one.</b> <c>HandleWalk</c> used to ask <see cref="MobAt"/>, then a
    /// <c>PlayerAt</c>/<c>PvpGhostAt</c> pair — three acquisitions, each taken and released — and then commit
    /// <c>_char.X/Y</c> under none of them. Two sessions stepping onto the same empty tile in the same instant
    /// both passed the check and both committed, and the tick's <c>occupied</c> snapshot was stale for any walk
    /// that committed mid-beat. A check whose answer is acted on after the lock is dropped is not a check. The
    /// two former helpers are gone with the race: their predicates live here, where the write can share them.</para>
    ///
    /// <para><b>What stays outside.</b> Everything that is not occupancy: the map edge, the ground pass flag,
    /// object walls, warps and scripted tiles. That is immutable content — no other thread can change it under
    /// the caller — so it does not belong in a critical section. The caller folds its own verdict in through
    /// <paramref name="otherwiseBlocked"/> so the commit is still suppressed, and passes
    /// <paramref name="enforceOccupancy"/> false for a no-clipper (<c>@clip</c>), whose reasons are still
    /// COMPUTED but do not hold it. They are computed rather than skipped so the caller's log line is
    /// byte-identical to the pre-#30 one in the case that prints them — a REFUSED step. A no-clipper's step
    /// is not refused, so it logs no suffix, exactly as before.</para>
    ///
    /// <para><b>Both predicates run even once one has fired.</b> The walk log prints " mob" and " player"
    /// independently, and the code it replaces computed both eagerly; short-circuiting would change it.</para>
    ///
    /// <para><b>Lock order (#29).</b> The caller is <c>HandleWalk</c>, already inside the mover's own state
    /// monitor, and this takes <c>_lock</c> second — session-state THEN world, the one legal order. Nothing
    /// here enters another session or Lua: the peer reads are the same plain <c>PlayerX</c>/<c>PlayerY</c>
    /// field reads the tick does under this lock, and the only write is to the mover, whose monitor the
    /// calling thread already holds.</para>
    /// </summary>
    /// <param name="ghostMover">The mover is a PvP ghost (Session.PvpGhostHidden), so it is blocked by other
    /// GHOSTS and no-clips through the living, instead of the other way round.</param>
    /// <returns>True if the position was written; false if the step was refused, and the caller snaps back.</returns>
    public bool TryMovePlayer(Session mover, ushort mapId, int nx, int ny,
                              bool ghostMover, bool enforceOccupancy, bool otherwiseBlocked,
                              out BlockReason why)
    {
        lock (_lock)
        {
            why = BlockReason.None;
            if (_maps.TryGetValue(mapId, out var m))
            {
                // A living mob occupies its tile, NPCs included — the same predicate MobAt uses.
                if (m.Mobs.Any(mo => mo.Alive && mo.X == nx && mo.Y == ny))
                    why |= BlockReason.Mob;
                // A PvP ghost is blocked ONLY by another ghost; everyone else only by a living player. The
                // dead never block the living either way, which is why the living branch tests !IsDead.
                if (ghostMover)
                {
                    if (m.Players.Any(p => !ReferenceEquals(p, mover) && p.IsDead && p.PlayerX == nx && p.PlayerY == ny))
                        why |= BlockReason.Ghost;
                }
                else if (m.Players.Any(p => !ReferenceEquals(p, mover) && !p.IsDead && p.PlayerX == nx && p.PlayerY == ny))
                {
                    why |= BlockReason.Player;
                }
                // A running Human Barrier closes the four tiles beside where it was cast to every player but its
                // caster (#334: the Atlas, "disabling any players from walking next to them"). Like the two
                // checks above it is occupancy, so @clip waives it.
                if (m.Barriers.Count > 0 && ClosedToPlayer(m, mover.PlayerId, nx, ny))
                    why |= BlockReason.Barrier;
            }
            // An unknown map blocks nothing and commits, which is what the three helpers did between them.
            if (otherwiseBlocked || (enforceOccupancy && why != BlockReason.None)) return false;

            mover.SetPositionUnderWorldLock((ushort)nx, (ushort)ny);
            // The accepted-step counter (MapState.Steps), incremented here because here is where the step is
            // accepted and here is the lock it already holds. `m` is null only for a map with no MapState,
            // which is the "unknown map blocks nothing and commits" case above — nothing to count it on.
            if (m is not null) m.Steps++;
            return true;
        }
    }

    /// <summary>
    /// An ARRIVAL: resolve the tile a player is being put on and write it, both inside one acquisition of
    /// <c>_lock</c> (#99 part 1). <see cref="TryMovePlayer"/> is the same idea for a step; this is every
    /// other way a player's position changes — warps, scripted-tile entrances, world-map travel, the
    /// Gateway, GM teleports.
    ///
    /// <para><b>What this replaces.</b> <c>Session.EnterMap</c> clamped the requested tile to the map's
    /// bounds and assigned <c>_char.X/Y</c> with no lock held, and <c>@approach</c>/<c>@bring</c> chose their
    /// tile beforehand through <c>World.PeerAt</c> then <c>World.MobAt</c> — two more acquisitions, both
    /// released before the write. The tile a search picked could therefore be taken by the time it was
    /// written to.</para>
    ///
    /// <para><b>This is a lock-scope change and nothing else.</b> <see cref="ArrivalPolicy.Clamp"/> is the
    /// default and is what all 21 non-GM-adjacency callers pass, and it does exactly what the old inline
    /// clamp did, occupancy included: it does not test it. Two players through one door land on one tile,
    /// which is what the original game did too — #99's source check settled it as stacking, so the default
    /// is the game's behaviour and not merely the code's history; see <see cref="ArrivalPolicy"/>.</para>
    ///
    /// <para><b>Lock order (#29)</b> is the same as <see cref="TryMovePlayer"/>'s: the caller is
    /// <c>Session.EnterMap</c>, already inside the mover's own state monitor, and this takes <c>_lock</c>
    /// second. The prewarm is outside the lock for the reason <see cref="EnterMap"/> documents — a cold
    /// <c>MapData.For</c> is a disk read, a full cell decode and a SQLite query, and none of that belongs in
    /// a critical section every player on every map is waiting behind.</para>
    /// </summary>
    /// <param name="from">Where the mover is coming from. <c>Session.EnterMap</c> has already called
    /// <see cref="LeaveMap"/> by the time it reaches here, so the mover is in no map's player list — and a
    /// search that cannot see them would offer them the tile they are standing on, which is not what
    /// <c>@approach</c>/<c>@bring</c> did before. Consulted only by
    /// <see cref="ArrivalPolicy.AdjacentFreeElseStack"/>, and only when its map matches the destination.</param>
    public void PlacePlayer(Session mover, ushort mapId, ushort xs, ushort ys, int x, int y,
                            ArrivalPolicy policy, FromTile from,
                            out ushort placedX, out ushort placedY)
    {
        // BEFORE the lock, and MapData.For rather than Prewarm: Prewarm returns early for a map with no
        // Maps.csv row, which would leave AdjacentFreeElseStack's own MapData.For to do the cold load —
        // a disk read, a full cell decode and a SQLite query — INSIDE the critical section every player on
        // every map is waiting behind. Reachable through @approach/@bring on an unregistered map, and the
        // path every policy test takes. The result is discarded; the point is the cache entry. Only the
        // searching policy needs it — Clamp never looks at terrain, so it does no terrain work at all, which
        // is also what the old inline clamp in Session.EnterMap did.
        if (policy == ArrivalPolicy.AdjacentFreeElseStack) _ = MapData.For(mapId, xs, ys);
        lock (_lock)
        {
            var (tx, ty) = policy switch
            {
                ArrivalPolicy.AdjacentFreeElseStack => AdjacentFreeLocked(mapId, xs, ys, x, y, from),
                _ => (x, y),
            };
            // The clamp itself is unchanged, and still last: the old code clamped whatever tile it was handed,
            // and a search's fallback tile has to go through it for the same reason.
            placedX = (ushort)Math.Clamp(tx, 0, Math.Max(0, xs - 1));
            placedY = (ushort)Math.Clamp(ty, 0, Math.Max(0, ys - 1));
            mover.SetPositionUnderWorldLock(placedX, placedY);
        }
    }

    /// <summary>The first free cardinal neighbour of (<paramref name="tx"/>,<paramref name="ty"/>), else that
    /// tile itself — <c>Session.ApproachTile</c>'s search, moved here so it shares the acquisition that writes
    /// its answer. The predicates are that method's, unchanged: in bounds, not ground- or object-wall-blocked
    /// for the direction stepped, and holding neither a player (any player, living or dead — the old
    /// <c>PeerAt</c> filtered neither) nor a living mob.</summary>
    private (int x, int y) AdjacentFreeLocked(ushort mapId, ushort xs, ushort ys, int tx, int ty, FromTile from)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var terrain = MapData.For(mapId, xs, ys);
        _maps.TryGetValue(mapId, out var m);

        for (int dir = 0; dir < 4; dir++)
        {
            int nx = tx + (dir == 1 ? 1 : dir == 3 ? -1 : 0);   // 0=N 1=E 2=S 3=W, as everywhere else
            int ny = ty + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            if (nx < 0 || ny < 0 || nx >= xs || ny >= ys) continue;
            if (terrain is not null && terrain.BlockedMove(nx, ny, dir)) continue;
            // The mover still holds the tile it is leaving; see the `from` parameter on PlacePlayer.
            if (from.Map == mapId && from.X == nx && from.Y == ny) continue;
            if (m is not null && m.Players.Any(p => p.PlayerX == nx && p.PlayerY == ny)) continue;
            if (m is not null && m.Mobs.Any(mo => mo.Alive && mo.X == nx && mo.Y == ny)) continue;
            return (nx, ny);
        }
        return (tx, ty);
    }

    /// <summary>Write a player's position with no occupancy test — the walk handler's snap-back.
    /// <see cref="TryMovePlayer"/> is the move that ACQUIRES a tile and therefore has to check; this is the
    /// one that gives a tile up, or re-asserts the one the client says the mover is already on, so there is
    /// nothing to check. It still goes through <c>_lock</c>: X and Y are two writes, and every reader of them
    /// (this class's tile scans, the tick's <c>occupied</c> snapshot) reads both under this lock, so a
    /// snap-back outside it is a torn (new X, old Y) waiting to be read.</summary>
    public void SetPlayerPosition(Session mover, int x, int y)
    {
        lock (_lock) mover.SetPositionUnderWorldLock((ushort)x, (ushort)y);
    }

    /// <summary>The nearest living, non-NPC mob within <paramref name="radius"/> tiles (Chebyshev) of a
    /// point matching <paramref name="match"/>, or null. Used by the 'r' ride key (RTK clif_findmount) to
    /// locate a rideable "horse" mob — called with <c>radius 0</c> at the player's <c>FrontTile()</c> so it
    /// only matches the exact tile faced (cardinal only, matching the player's own melee reach).</summary>
    public Mob? MobNear(ushort mapId, int x, int y, int radius, Func<Mob, bool> match)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.Where(mo => mo.Alive && !mo.IsNpc && match(mo)
                                       && Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)) <= radius)
                          .OrderBy(mo => Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)))
                          .FirstOrDefault();
        }
    }

    /// <summary>Remove a live mob from the map WITHOUT a kill (no loot roll, no exp) — used when a player
    /// rides it away ('r' key). If it's a spawn point's mob, the point is freed to respawn like a normal
    /// death; an ad-hoc mob (e.g. one summoned by a dismount) is just dropped. Broadcasts the despawn.</summary>
    public bool DespawnMob(ushort mapId, Mob mob)
    {
        lock (_lock)
        {
            if (!mob.Alive || mob.IsNpc) return false;
            if (!_maps.TryGetValue(mapId, out var m)) return false;
            m.Mobs.Remove(mob);
            m.ViewGen++;                    // a mob roster change
            _spawnDirector.ReleasePoint(mob);
        }
        Broadcast(mapId, p => p.DespawnEntity(mob.Id));
        return true;
    }

    /// <summary>The player standing on (x,y) of <paramref name="mapId"/>, or null. Used by the ';' look key
    /// (RTK clif_parselookat checks PC before mob/item/NPC).
    /// <para>A session a second login has replaced (<see cref="Session.IsReplaced"/>) is skipped: it stays on
    /// its map until its read loop unwinds into its teardown, and in that window nothing may swing at it, cast
    /// at it or trade with it (#168 item 5). The read is volatile and takes no session monitor, which is what
    /// makes it legal under <c>_lock</c>; it runs only for a player already on the tile.</para></summary>
    public Session? PeerAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Players.FirstOrDefault(p => p.PlayerX == x && p.PlayerY == y && !p.IsReplaced);
        }
    }

    /// <summary>Every player standing on (x,y) of <paramref name="mapId"/>, in the map's player order, a replaced
    /// session skipped as <see cref="PeerAt"/> skips it; empty for none. For a spell that reads the whole of a tile as
    /// RTK's <c>getObjectsInCell</c> does (the Barbarian slice: the faced living player, the strikes, the area curse), so
    /// a living player standing on a ghost's tile is not hidden behind the ghost (#414's review, F2). A snapshot taken
    /// under <c>_lock</c> and returned with it released, so the caller reads each player (alive or dead) outside it.</summary>
    public List<Session> PlayersAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return new();
            return m.Players.Where(p => p.PlayerX == x && p.PlayerY == y && !p.IsReplaced).ToList();
        }
    }

    /// <summary>Every living mob (an NPC included) on (x,y) of <paramref name="mapId"/>, in the map's order; empty for
    /// none. <see cref="MobAt"/>'s test for the whole tile, as <see cref="PlayersAt"/> is <see cref="PeerAt"/>'s.</summary>
    public List<Mob> MobsAt(ushort mapId, int x, int y)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return new();
            return m.Mobs.Where(mo => mo.Alive && mo.X == x && mo.Y == y).ToList();
        }
    }

    /// <summary>The names of the players on <paramref name="mapId"/> who are invisible and stand within
    /// <paramref name="range"/> tiles of (<paramref name="x"/>, <paramref name="y"/>) on both axes, in the map's
    /// player order: Second Sight's scan (<c>Session.SecondSightScan</c>; RTK poet/second_sight.lua reads
    /// <c>distanceSquare(player, pc, 9)</c> and <c>state == 2</c>). Under <c>_lock</c>, the lock every writer of a
    /// player's tile holds. <see cref="Session.IsInvisible"/> is the unsynchronised scalar read the tick makes
    /// here of <c>IsStealthExpired</c>, and the name is one reference read of a string set at arrival. The
    /// caller holds its own state monitor, which comes before this lock (docs/common/Locking.md).
    /// <para><paramref name="except"/> is left out of the answer: Hear footstep's beat names the others only (RTK
    /// Spells/Subpaths/Do/hear_footstep.lua's <c>pcs[i].ID ~= player.ID</c>). Second Sight passes none.</para></summary>
    public List<string> HiddenPlayersNear(ushort mapId, int x, int y, int range, Session? except = null)
    {
        var names = new List<string>();
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return names;
            foreach (var p in m.Players)
                if (!p.IsReplaced && p.IsInvisible && !ReferenceEquals(p, except)
                    && Math.Abs(p.PlayerX - x) <= range && Math.Abs(p.PlayerY - y) <= range)
                    names.Add(p.CharName);
        }
        return names;
    }

    /// <summary>The exclusivity slot a Barrier's hold takes on a creature: RTK's own (<c>snares</c>, which
    /// barrier.lua checks with <c>checkIfCast</c> before it holds anything, Spells/spellTables.lua:287), apart from
    /// Paralyze's <c>paras</c>, so a creature already paralyzed is still held until the barrier ends.</summary>
    internal const string BarrierHoldSlot = "snares";

    /// <summary>Raise a Barrier (<paramref name="players"/> false) or a Human Barrier (true) on the four tiles
    /// beside (<paramref name="x"/>, <paramref name="y"/>) for <paramref name="durMs"/> (#334). One acquisition of
    /// <c>_lock</c> prunes the map's lapsed barriers, adds this one, and finds what stands on its tiles.
    /// <list type="bullet">
    /// <item>Barrier holds every living creature there (NPCs aside, which have no AI to stop) through
    /// <see cref="ApplyMobStatus"/>, the path every paralyze takes: <c>FrozenUntil</c>, which stops a creature
    /// moving and attacking, until the barrier ends; one another barrier already holds is held to the later end.
    /// <paramref name="creaturesHeld"/> counts them.</item>
    /// <item>Human Barrier returns the living players there instead, and holds nobody itself: holding a player writes
    /// that player's state under that player's monitor, which may not be taken under this lock
    /// (docs/common/Locking.md, rows 2 and 3). The caller does it after this returns.</item>
    /// </list>
    /// The caller holds its own state monitor, which comes before this lock.</summary>
    internal List<Session> RaiseBarrier(ushort mapId, ushort x, ushort y, int durMs, bool players, uint casterId,
                                        string spellKey, out int creaturesHeld)
    {
        var standing = new List<Session>();
        creaturesHeld = 0;
        if (durMs <= 0) return standing;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            var m = Map(mapId);
            m.Barriers.RemoveAll(b => b.Until <= now);
            var zone = new BarrierZone(x, y, now + durMs, players, casterId);
            m.Barriers.Add(zone);
            if (players)
            {
                // A ghost is not held (PR #338 review, F4): the dead never block the living here either
                // (TryMovePlayer), and a hold would keep a ghost from its way back to life for 22 s. It is still
                // kept off the tiles, like any player.
                foreach (var p in m.Players)
                    if (!p.IsReplaced && !p.IsDead && zone.Closes(p.PlayerX, p.PlayerY)) standing.Add(p);
            }
            else
            {
                long until = now + durMs;
                foreach (var mob in m.Mobs)
                {
                    if (!mob.Alive || mob.IsNpc || !zone.Closes(mob.X, mob.Y)) continue;
                    // Already held by another barrier (only barriers fill this slot): held to the later of the two
                    // ends, because it is "paralyzed until the barrier wears off" and this one has not. Not
                    // ApplyMobStatus's refusal, which keeps an offensive hold from being chained on its victim;
                    // a barrier's hold belongs to the tile (PR #338 review, F2).
                    if (mob.HasStatus(BarrierHoldSlot, now))
                    {
                        if (mob.Statuses![BarrierHoldSlot].Until < until) mob.SetStatus(BarrierHoldSlot, until, spellKey);
                        mob.FrozenUntil = Math.Max(mob.FrozenUntil, until);
                        creaturesHeld++;
                    }
                    else if (ApplyMobStatus(mob, BarrierHoldSlot, durMs, hold: true, blind: false, spellKey: spellKey))
                        creaturesHeld++;
                }
            }
        }
        return standing;
    }

    /// <summary>Is (<paramref name="x"/>, <paramref name="y"/>) closed to the player <paramref name="moverId"/> by
    /// a running Human Barrier on <paramref name="m"/>? Its own caster passes. Caller holds <c>_lock</c>.</summary>
    private static bool ClosedToPlayer(MapState m, uint moverId, int x, int y)
    {
        long now = Environment.TickCount64;
        foreach (var b in m.Barriers)
            if (b.Players && b.Until > now && b.CasterId != moverId && b.Closes(x, y)) return true;
        return false;
    }

    /// <summary>Add the tiles every running Barrier on <paramref name="m"/> closes to creatures to
    /// <paramref name="closed"/>: the tick's collision index, which every creature step reads
    /// (<c>MobTickContext.Occupied</c>). Caller holds <c>_lock</c>.</summary>
    internal static void AddBarrierTiles(MapState m, HashSet<(ushort, ushort)> closed)
    {
        if (m.Barriers.Count == 0) return;
        long now = Environment.TickCount64;
        foreach (var b in m.Barriers)
        {
            if (b.Players || b.Until <= now) continue;
            closed.Add((b.X, (ushort)(b.Y + 1)));
            closed.Add(((ushort)(b.X + 1), b.Y));
            if (b.Y > 0) closed.Add((b.X, (ushort)(b.Y - 1)));
            if (b.X > 0) closed.Add(((ushort)(b.X - 1), b.Y));
        }
    }

    /// <summary>Test seam: the barriers <paramref name="mapId"/> holds, lapsed ones included.</summary>
    internal BarrierZone[] BarriersForTest(ushort mapId)
    {
        lock (_lock) return _maps.TryGetValue(mapId, out var m) ? m.Barriers.ToArray() : Array.Empty<BarrierZone>();
    }

    /// <summary>Test seam: end every barrier on <paramref name="mapId"/> now, as its 22 s would, by moving its
    /// deadline one millisecond into the past.</summary>
    internal void EndBarriersForTest(ushort mapId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return;
            long past = Environment.TickCount64 - 1;
            for (int i = 0; i < m.Barriers.Count; i++) m.Barriers[i] = m.Barriers[i] with { Until = past };
        }
    }

    // One gate covers the entire disk-to-live sequence below, not just Content.Load: cache invalidation,
    // staff reload, terrain pre-warm and population rebuild must observe the same content generation. A GM
    // invokes this on the session read loop, so contention is bounded rather than stalling packet handling.
    private static readonly SemaphoreSlim ReloadGate = new(1, 1);
    private static readonly TimeSpan ReloadWaitTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Hot-reload every file-backed registry and rebuild the world population from it — the work behind the
    /// <c>@reload</c> GM command, lifted out of <see cref="Session"/> because the OTHER caller has no session:
    /// a content-only deploy drops a <c>run/reload_now</c> sentinel and <see cref="RestartSchedule.Loop"/>
    /// calls this. That is the whole point of the content lane — a CSV or Lua fix ships without kicking anyone.
    ///
    /// <para>Returns (ok, report). A load error keeps the previous content publication and comes back
    /// <c>ok: false</c> rather than taking down a running world.</para>
    /// </summary>
    public (bool ok, string report) ReloadFromDisk()
    {
        if (!ReloadGate.Wait(ReloadWaitTimeout)) return (false, "reload already in progress");
        try
        {
            string summary;
            try { summary = Content.Reload(); }
            catch (Exception e)
            {
                Log.Error("content reload failed — the previous content publication remains live", e);
                return (false, e.Message);
            }
            ObjectFlags.Invalidate();   // Overrides arrive in Content.Load's commit; this only drops the SObj.tbl cache
            MapData.Invalidate();
            StaffAccounts.Load();   // the staff rosters are file-backed config too — promote/demote without a restart
            // Pre-warm the terrain cache for populated maps OUTSIDE _lock, so RebuildPopulation's re-materialization
            // (FreeSpawnTile/SpawnDirector.PickAreaHome -> MapData.For) hits a warm cache instead of reading .map files from disk
            // while holding the world lock (the old reload-stall). With them, the maps a boss filed under one of
            // them may be built on (#333): its rooms' maps (SpawnDirector.PrewarmRoomMaps).
            var populated = PopulatedMapIds();
            foreach (var mapId in populated)
                if (Content.Maps.TryGetValue(mapId, out var mi)) MapData.For(mapId, mi.Xs, mi.Ys);
            SpawnDirector.PrewarmRoomMaps(populated.ToArray());
            var (mobs, npcs, maps) = RebuildPopulation();
            return (true, $"{summary}. Rebuilt population: {mobs} mob(s) torn down, {npcs} NPC(s) placed, " +
                          $"{maps} live map(s) re-materialized; map cache cleared.");
        }
        finally { ReloadGate.Release(); }
    }

    /// <summary>Make every mob everywhere forget <paramref name="playerId"/> — target and threat table both.
    /// The @peace toggle calls this on switch-on so a mob already mid-chase lets go; staying ignored from
    /// then on is the scan-side exclusions in Tick (a peace player is skipped by unprovoked aggro and the
    /// stuck-mob retarget). Damage still re-acquires: TryDamage points a mob at whoever hit it, peace or not.</summary>
    public void PacifyPlayer(uint playerId)
    {
        lock (_lock)
            foreach (var (_, pm) in _maps)
                foreach (var mob in pm.Mobs)
                {
                    mob.ClearThreat(playerId);
                    if (mob.TargetId == playerId) { mob.TargetId = 0; mob.AttackTimer = 0; }
                }
    }

    /// <summary>NPCs (stationary, IsNpc) within <paramref name="radius"/> tiles (Chebyshev) of a point, nearest
    /// first. Used to route a player's speech to a nearby NPC's say-handler (RTK onSayClick).</summary>
    public List<Mob> NpcsNear(ushort mapId, int x, int y, int radius)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return new();
            return m.Mobs.Where(mo => mo.IsNpc &&
                                      Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)) <= radius)
                         .OrderBy(mo => Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y)))
                         .ToList();
        }
    }

    /// <summary>The nearest living mob with this identifier within <paramref name="radius"/> tiles (Chebyshev)
    /// of a point, or null. One pass under the world lock — the alternative, probing <see cref="MobAt"/> over
    /// a box, takes the lock once per tile and gets expensive fast for anything wider than a couple of steps.</summary>
    public Mob? NearestMobByKey(ushort mapId, int x, int y, int radius, string key)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            Mob? best = null; int bestDist = int.MaxValue;
            foreach (var mo in m.Mobs)
            {
                if (!mo.Alive || !string.Equals(mo.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                int d = Math.Max(Math.Abs(mo.X - x), Math.Abs(mo.Y - y));
                if (d <= radius && d < bestDist) { bestDist = d; best = mo; }
            }
            return best;
        }
    }

    /// <summary>The live world mob with this entity id on the map, or null (used by targeted spell casts,
    /// where the client sends the target's entity id rather than a tile).</summary>
    public Mob? MobById(ushort mapId, uint id)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            return m.Mobs.FirstOrDefault(mo => mo.Alive && mo.Id == id);
        }
    }

    /// <summary>Apply damage under the lock (so concurrent attackers can't double-kill). Returns
    /// false if the mob was already dead; otherwise sets <paramref name="died"/> and, on death,
    /// removes it from the map, schedules its spawn point to respawn, and rolls floor loot (which this
    /// method drops + broadcasts). The caller still broadcasts the damage number / corpse despawn.
    /// <paramref name="attackerId"/> (0 = none, e.g. a session-local debug hit) marks the mob as targeting
    /// that player — <see cref="Tick"/> then has it chase and fight back instead of just wandering.
    /// <para><paramref name="raw"/> is the hit's figure UNFLOORED: the product of every term the caller owns (the
    /// creature's armor, a Backstab/Flank reach), not yet rounded. This method multiplies in the creature's own
    /// sleep-family amplifier, floors the product once (at least 1) and then doubles it when
    /// <paramref name="fromBehind"/> (#363). A whole-number figure (a trap, a poison tick, a pet's swing, a harvest
    /// swing) passes as it is.</para>
    /// <para><paramref name="minDamage"/> raises the floored figure to at least that much, after the amplifier and
    /// before the rear x2: Shaman's torment's beat, whose script floors its netted blow at 1,000 (RTK
    /// shamans_torment.lua:71-74, after calculateDamage). 0, the default, changes nothing.</para></summary>
    public bool TryDamage(ushort mapId, Mob mob, double raw, out bool died, uint attackerId = 0, bool fromBehind = false,
                          int minDamage = 0)
    {
        died = false;
        List<GroundItem>? drops = null;
        lock (_lock)
        {
            if (!mob.Alive || mob.IsNpc) return false;   // NPCs are indestructible (a click talks to them, not fights)
            // Sleep-family amplifier: "the NEXT attack upon the target" lands harder (Doze 1.3x, Sleep 1.5x).
            // Consumed here, atomically with the HP change below, so it applies to exactly one hit, and applied
            // before the HP subtraction so the over-head bar and any kill it causes both reflect the amplified number.
            //
            // ONE PRODUCT, ONE FLOOR, THEN THE REAR x2 (#78, #363). The amplifier multiplies the caller's unfloored
            // figure, the product is floored once and raised to 1 (Combat.FloorOnce), and a hit from behind doubles
            // the whole number, as RTK's swingDamage.lua orders it: target.sleep at :92, the one floor at :108, the
            // x2 at :147-150. It used to arrive already floored and doubled, and was multiplied and rounded half to
            // even here, a second rounding that left the first hit on a slept or dozed creature a point or two off the
            // one rule (more from behind, where the x2 came before it). With no amplifier armed this is the caller's
            // own floor and double, so every other hit lands exactly what it did. A Backstab/Flank reach (0.5) stays inside the
            // floor, before the x2, as today: it is in the caller's figure (Session.PlayerSwingRaw), and #363's
            // ruling kept it there. Arithmetic only under the lock: no allocation, no I/O, no other lock.
            double amp = mob.TakeDamageAmp(Environment.TickCount64);
            if (amp > 1.0) raw *= amp;
            int dmg = Combat.FloorOnce(raw);
            if (dmg < minDamage) dmg = minDamage;
            if (fromBehind) dmg *= 2;
            // A mythic boss does not simply die (RTK mob_ai_mythic.on_attacked). Its lethal-blow ladder, in
            // RTK's order, because the order is the mechanic:
            //
            //   1. FIRST lethal blow of its life -> Last Stand (RTK gates this on `mob.magic == 100`, and the
            //      spell spends all 100 with nothing to give it back, so it is once per life).
            //   2. Otherwise OVERKILL: a blow big enough to punch through its HP *and* the heal it would get
            //      (`attacker.damage >= mob.health + healAmount`) kills it outright, no roll. This is the
            //      whole reason a boss is beatable — you out-damage the save rather than out-waiting it.
            //      Note RTK skips this test on the Last Stand branch, so the first brink is always survivable.
            //   3. Then the save roll — 1/2, 2/3 or 3/4 by tier. Fail it and the blow lands normally.
            //
            // Runs BEFORE the subtraction, so the heal is what the blow lands on.
            if (dmg >= mob.Hp && Content.MobBosses.TryGetValue(mob.Key, out var boss) && boss.HealAmount > 0)
            {
                bool lastStand = !mob.SecondWindUsed && boss.LastStandMs > 0;
                // The save roll runs on the Last Stand branch too: RTK casts the spell and *then* rolls, so a
                // boss really can go into its last stand and drop dead on the same blow.
                bool overkill  = !lastStand && dmg >= mob.Hp + boss.HealAmount;
                bool saved     = !overkill && Random.Shared.Next(Math.Max(2, boss.HealChance)) != 0;

                if (lastStand)
                {
                    // RTK Spells/last_stand.lua: scrub own curses, animation 11, 8s duration, and PARALYSE
                    // ITSELF for it. The boss stands frozen and heals every tick while the window runs — it
                    // is a window to burst it down or back off, not an untouchable enrage.
                    mob.SecondWindUsed = true;
                    mob.LastStandUntil = Environment.TickCount64 + boss.LastStandMs;
                    mob.FrozenUntil = Math.Max(mob.FrozenUntil, mob.LastStandUntil);
                    mob.ClearStatus("curses"); mob.ClearStatus("minorcurses");
                    _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, LastStandAnim, boss.Sound));
                }

                if (saved)
                {
                    mob.Hp = Math.Min(mob.MaxHp, mob.Hp + boss.HealAmount);
                    if (!lastStand) _deferredFx.Add((mapId, mob.Id, mob.X, mob.Y, boss.Anim, boss.Sound));
                }
            }

            mob.Hp -= dmg;
            died = !mob.Alive;
            // Threat accrues with the damage (RTK swing.lua `player:addThreat(mob.ID, damage)`), whether or
            // not this hit takes the target — Tick's retarget then reads it. Counted BEFORE the death check
            // so the killing blow still counts, which matters for a pet deciding what to assist against.
            mob.AddThreat(attackerId, dmg);
            // Hitting something that has forgotten you reminds it (RTK amnesia.lua on_takedamage_while_cast).
            // Only the forgotten player breaks it — anyone else can keep hitting it without giving you away.
            if (mob.AmnesiaBy != 0 && mob.AmnesiaBy == attackerId) { mob.AmnesiaBy = 0; mob.AmnesiaUntil = 0; }

            // Lua AI hooks for this creature, if it has any (queued — see QueueHook).
            var actor = attackerId == 0 ? null : Online.ByIdLocked(attackerId);
            QueueHook(MobScript.OnAttacked, mapId, mob, actor);
            if (died) QueueHook(MobScript.AfterDeath, mapId, mob, actor);
            // Provoked -> fight back (mob_ai_normal on_attacked). Getting hit ALWAYS wins: it drops whatever
            // mob it was scrapping with (a pet) and re-points it at the player, and it overrides the
            // stuck-mob retarget in Tick — so zapping something always drags its aggro onto you, wall or no
            // wall, however unreachable you are.
            if (!died && attackerId != 0) { mob.TargetId = attackerId; mob.TargetMobId = 0; }
            // Being hit wakes a sleeping creature (RTK sleep.lua on_takedamage_while_cast). Paralyze
            // deliberately does NOT clear here — a paralyzed mob stays held while you beat on it.
            if (!died && mob.HasStatus("sleeps", Environment.TickCount64))
            {
                mob.ClearStatus("sleeps"); mob.FrozenUntil = 0; mob.FxRepeatUntil = 0;
            }
            // …unless it's PREY, which has no fight in it: being hurt by anything (a spell, a trap, a swing)
            // panics it instead. Tick clears the TargetId set just above before it can ever be acted on; this
            // is what makes a spell as alarming as a sword. A pure MISS never reaches here — Session.ResolveSwing
            // calls Spook directly for that case.
            if (!died && mob.Flees) mob.PanicUntil = Environment.TickCount64 + PanicMs;
            // Sute, below the red bar, buys the person hitting him one answering swing before he breaks off
            // again — two if he is cornered and cannot run at all. See SuteAi.OnDamaged.
            if (!died && mob.Key == SuteAi.MobKey) SuteAi.OnDamaged(mob, Environment.TickCount64);
            if (died && _maps.TryGetValue(mapId, out var m))
            {
                m.Mobs.Remove(mob);
                // One bump for this whole acquisition: the mob leaving the roster, the drops RollDropsLocked
                // adds to m.Items below, and the handed items after it are all one change as far as a viewer
                // is concerned — the generation counts CHANGES, and any increment makes every viewer of this
                // map re-sweep the beat that follows.
                m.ViewGen++;
                _spawnDirector.RecordDeath(mapId, mob);   // the boss death registry, and its spawn point freed
                // Loot is a property of the CREATURE, not of which system placed it. Rolling it inside the
                // spawn-point branch above meant a mob without a point dropped nothing at all — which, once
                // the hunting maps moved to batch groups, would have been every mob in them. Gated on
                // WorldSpawned so a conjured pet still can't be farmed for its wild counterpart's drops.
                if (mob.WorldSpawned && Content.MobByKey(mob.Key) is { } dropDef)
                    drops = RollDropsLocked(m, mob, dropDef);  // adds to m.Items under the lock
                // Anything a player HANDED this creature falls back to the floor when it dies — regardless of
                // whether the creature is a loot-dropper (the sword handed to a cat). A no-kill DespawnMob never
                // reaches here, so a ridden-away / quest-released creature keeps carrying them (they're lost,
                // which is the intended "you gave it away" for those paths). Each keeps its Dura/name/owner.
                if (mob.Handed is { Count: > 0 })
                {
                    drops ??= new List<GroundItem>();
                    foreach (var h in mob.Handed)
                    {
                        var hdef = Content.ItemById(h.ItemId);
                        if (hdef is null) continue;
                        var gi = new GroundItem { Id = _nextItemId++, ItemId = h.ItemId, X = mob.X, Y = mob.Y,
                            Amount = h.Amount, Dura = h.Dura, Graphic = hdef.Icon, CustomName = h.CustomName, Owner = h.Owner };
                        m.Items.Add(gi);
                        drops.Add(gi);
                    }
                }
            }
        }
        if (drops is not null)
            foreach (var gi in drops) Broadcast(mapId, p => p.ShowGroundItem(gi));
        return true;
    }

    /// <summary>Roll a slain mob's loot, add each stack to the floor, and return them for broadcasting.
    /// Caller holds <c>_lock</c>.</summary>
    private List<GroundItem> RollDropsLocked(MapState m, Mob mob, MobDef def)
    {
        Debug.Assert(Monitor.IsEntered(_lock));
        var drops = new List<GroundItem>();
        foreach (var roll in Content.RollDrops(def, Random.Shared))
        {
            GroundItem gi;
            if (roll.Gold)
            {
                // Mirrors Session.HandleDropGold's icon tiering (coins_1 / _2_99 / _100_999).
                ushort gfx = roll.Amount < 2 ? (ushort)22 : roll.Amount < 100 ? (ushort)73 : (ushort)72;
                gi = new GroundItem { Id = _nextItemId++, ItemId = -1, X = mob.X, Y = mob.Y, Amount = roll.Amount, Graphic = gfx };
            }
            else
            {
                gi = new GroundItem { Id = _nextItemId++, ItemId = roll.Item!.Id, X = mob.X, Y = mob.Y,
                    Amount = roll.Amount, Graphic = roll.Item.Icon, Dura = roll.Item.Durability };
            }
            m.Items.Add(gi);
            drops.Add(gi);
        }
        return drops;
    }

    // ---- ground items (dropped/thrown stacks lying on the floor) -------------------------------

    /// <summary>Drop <paramref name="gi"/> onto <paramref name="mapId"/> and draw it for everyone there.</summary>
    public void DropItem(ushort mapId, GroundItem gi)
    {
        lock (_lock) { var m = Map(mapId); m.Items.Add(gi); m.ViewGen++; }   // a floor-item roster change
        Broadcast(mapId, p => p.ShowGroundItem(gi));
    }

    /// <summary>Read-only snapshot of the floor items on a map (for drawing to a newcomer / on redraw).</summary>
    public GroundItem[] ItemsOn(ushort mapId)
    {
        lock (_lock)
            return _maps.TryGetValue(mapId, out var m) ? m.Items.ToArray() : Array.Empty<GroundItem>();
    }

    /// <summary>Remove the topmost (last-dropped) floor item on (x,y) under the lock — so two players
    /// grabbing the same tile can't both win — and despawn it for everyone. Null if the tile is empty.
    /// <para><paramref name="pickerId"/> is who is grabbing (0 = an anonymous/system grab, which ignores locks).
    /// Death-pile stacks reserved for someone else are SKIPPED rather than taken, and
    /// <c>blocked</c> comes back true so the caller can say why nothing happened — RTK
    /// <c>canLoot</c>'s "That item does not belong to you." Set <paramref name="ownOnly"/> to take ONLY the
    /// picker's own still-locked pile and pass over everything else (RTK <c>isYours</c>, the F1 recovery).</para></summary>
    public GroundItem? PickUp(ushort mapId, int x, int y, uint pickerId = 0, bool ownOnly = false)
        => PickUp(mapId, x, y, pickerId, ownOnly, out _);

    /// <inheritdoc cref="PickUp(ushort, int, int, uint, bool)"/>
    public GroundItem? PickUp(ushort mapId, int x, int y, uint pickerId, bool ownOnly, out bool blocked)
    {
        GroundItem? gi = null;
        blocked = false;
        lock (_lock)
        {
            if (_maps.TryGetValue(mapId, out var m))
            {
                // last match = most recently dropped (drawn on top)
                for (int i = m.Items.Count - 1; i >= 0; i--)
                {
                    var it = m.Items[i];
                    if (it.X != x || it.Y != y) continue;
                    if (ownOnly) { if (!it.BelongsTo(pickerId)) continue; }
                    else if (pickerId != 0 && it.LockedAgainst(pickerId)) { blocked = true; continue; }
                    gi = it; m.Items.RemoveAt(i); m.ViewGen++; break;   // a floor-item roster change
                }
            }
        }
        if (gi is not null) { blocked = false; Broadcast(mapId, p => p.DespawnEntity(gi.Id)); }
        return gi;
    }

    /// <summary>Despawn every creature on a map except its NPCs, for all its players (the shared @kill). Returns
    /// how many were taken.
    ///
    /// <para><b>NPCs stay.</b> No despawn path takes one: <see cref="DespawnMob"/> refuses them, and an NPC leaves
    /// the world only through its NPCs.csv toggle (<see cref="DisableNpc"/>) or a population rebuild. When this
    /// cleared the whole list, a GM's @kill on a town map removed the townsfolk until the next @reload or
    /// restart.</para>
    ///
    /// <para><b>Each creature taken gets a despawn's bookkeeping.</b> A spawn point's creature releases its
    /// point, as <see cref="DespawnMob"/> does, so the point refills on its creature's own timer. Without the
    /// release the point kept the removed creature as its live one. The respawn pass skips a point that has
    /// one, so the point stayed empty until a @reload or restart. A batch group's creature has no point: its
    /// group counts what is left on the map and tops up on its own clock, with or without this.</para>
    ///
    /// <para><b>No <c>Alive</c> test, unlike <see cref="DespawnMob"/>.</b> There it guards a caller holding a
    /// stale reference: a creature killed after the caller found it has already left the list and released its
    /// point. This reads the list itself, under the lock, so every entry is current. A kill takes its creature
    /// off the list in the same locked section, and content floors a creature's HP at 1. The one other source
    /// of an entry with no hit points was a GM's <c>@mob &lt;look&gt; 0</c>, and <c>@mob</c> now refuses an HP
    /// below 1. Should one reach the list anyway, no client draws it, nothing can damage it, and DespawnMob
    /// refuses it, so @kill is the only way to clear it, as it always has been.</para>
    ///
    /// <para>One locked section. <see cref="SpawnDirector.ReleasePoint"/> runs under the lock this already
    /// holds and takes none of its own. The despawns go out after the lock, one per creature taken and for no
    /// other id.</para></summary>
    public int ClearMap(ushort mapId)
    {
        static bool Takes(Mob mo) => !mo.IsNpc;

        uint[] ids;
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return 0;
            var taken = m.Mobs.FindAll(Takes);
            if (taken.Count == 0) return 0;
            m.Mobs.RemoveAll(Takes);
            m.ViewGen++;                    // a mob roster change
            foreach (var mo in taken) _spawnDirector.ReleasePoint(mo);   // a spawn point's creature frees its point
            ids = taken.Select(mo => mo.Id).ToArray();
        }
        foreach (var id in ids) Broadcast(mapId, p => p.DespawnEntity(id));
        return ids.Length;
    }

    // ---- shared mob AI (one heartbeat drives every wandering mob on every map) -----------------

    /// <summary>A tick this slow (work OR scheduling delay, ms) gets a diagnostic line. 150ms is a quarter of
    /// the heartbeat — well clear of normal jitter, low enough to catch a stall long before a player would
    /// call it lag. <c>P1998_SLOW_TICK_MS</c> tunes it; 0 disables the watchdog.</summary>
    private static readonly int SlowTickMs = ServerConfig.Current.SlowTickMs ?? TickMs / 4;

    private long _lockWaitMs;   // how long the last Tick() waited to acquire _lock (watchdog attribution)

    // ---- phase attribution for the slow-tick watchdog ------------------------------------------
    //
    // `work` is one number. The first bot load run (400 players on one map: work p50 132ms, max 297ms against
    // a 333ms period, lock-wait 0ms in every line) could therefore say only that the tick BODY was too big
    // for the population — not which part of it. These buckets answer that: one raw-timestamp delta per
    // labelled phase of Tick/FlushTick, taken on every beat (nothing can know in advance which beat will be
    // slow) and turned into text only inside the branch that has already decided to log.
    //
    // MarkPhase(X) CLOSES bucket X where it stands, so a bucket covers everything since the previous mark and
    // every statement of the beat falls in exactly one of them. That is what makes `other` a real remainder —
    // the beat minus what was named — rather than a number chosen to make the line add up.
    //
    // Cost on a healthy beat: one Stopwatch.GetTimestamp (QueryPerformanceCounter) and one array store per
    // phase, plus an Array.Clear of PhCount longs at the top of the beat. No list, no string, no dictionary,
    // no LINQ, nothing allocated — the allocation is in PhaseBreakdown, which only runs on a logged beat.
    private const int PhWarm = 0, PhLockWait = 1, PhRespawns = 2, PhRefills = 3, PhMorphs = 4, PhDecoys = 5,
                      PhForage = 6, PhClock = 7, PhWeather = 8, PhWander = 9, PhViewSnapshot = 10,
                      PhViewports = 11, PhMoves = 12, PhFx = 13, PhHooks = 14, PhStatus = 15, PhAdvice = 16,
                      PhSwings = 17, PhCasts = 18, PhPetTrap = 19, PhExpiries = 20, PhRegen = 21, PhTime = 22,
                      PhCount = 23, PhOther = PhCount;

    /// <summary>One name per <c>Ph*</c> constant above, in that order, with <c>other</c> last. The explicit
    /// length is the check that the two lists stay in step: a constant added without a name (or the other way
    /// round) is a compile error, not a mislabelled log line.</summary>
    private static readonly string[] PhaseNames = new string[PhCount + 1]
    {
        "(0) warm", "lock-wait", "(1) respawns", "(1.1) refills", "(1.2) morphs", "(1.3) decoys",
        "(1.5) forage", "(1.6) clock", "(1.7) weather", "(2) wander", "(3.0) view snapshot", "(3) viewports",
        "(4) moves", "(4.1) fx", "(4.2) hooks", "(4.3) status", "(4.4) advice/forage", "(4.5) swings",
        "(4.6) casts", "(4.7) pet/trap", "(4.8) expiries", "(5) regen", "(6) time", "other",
    };

    private readonly long[] _phaseTicks = new long[PhCount];   // this beat, per phase, in raw Stopwatch ticks
    private long _phaseT0;                                     // when the phase now running started
    private long _phaseStart;                                  // when this beat started
    private long _phaseTotal;                                  // the whole beat — what the parts are measured against

    // The same buckets again, but never cleared: every beat's cost added on, so a phase's cost is a RATE
    // rather than a floor. The per-beat array above only becomes text on a beat the watchdog already
    // decided to log, so every "milliseconds per second" figure in a load report so far is a sum over the
    // slow beats divided by the whole span — which understates a phase that is expensive on every beat and
    // says nothing at all about a phase on a server that logged nothing. Two samples of run/status.json give
    // delta(phase ms) / delta(elapsedMs) exactly, for every phase, slow beats and healthy ones alike.
    //
    // Raw Stopwatch ticks, converted to milliseconds ONCE at render. Summing milliseconds instead would
    // truncate every beat to zero for any phase under a millisecond — which is most of them on a healthy
    // beat — and the totals would report 0ms forever for exactly the phases nobody has measured yet.
    //
    // What is accumulated is the NAMED phases plus the unmarked TAIL of each beat, never the beat total
    // itself — so the whole is the sum of its parts by construction rather than by two clocks agreeing.
    // A beat's marks chain (`_phaseT0 - _phaseStart` IS everything named on it), so the tail is the only
    // piece left over, and `beatMs` is computed from both at render. The alternative — accumulating the
    // beat total on one side and the phases on the other — makes the parts exceed the whole the moment
    // anything runs a beat without closing it, which the `…ForTest` seams in World.MobAiTick.cs do.
    private readonly long[] _phaseTotals = new long[PhCount];  // since start, per phase, raw Stopwatch ticks
    private long _phaseTailTicks;                              // since start, each beat after its last mark

    /// <summary>When this world's phase clock came up, as a raw Stopwatch timestamp. The origin for
    /// <see cref="ElapsedMs"/>, so a reader of two status samples can divide by a span the server measured
    /// rather than by its own wall clock, which a poll's jitter and a clock adjustment both spoil.
    ///
    /// <para>Construction, not the first beat: <c>Program</c> starts <see cref="TickLoop"/> immediately
    /// after building the world, so the difference is milliseconds, and taking it here costs one
    /// <c>QueryPerformanceCounter</c> for the life of the process instead of a branch on every beat.</para></summary>
    private readonly long _phaseEpoch = Stopwatch.GetTimestamp();

    /// <summary>Open this beat's phase clock. The first statement of <see cref="Tick"/>, so every caller of
    /// it leaves the buckets consistent; <see cref="EndPhases"/> is the caller's, because only the caller
    /// knows that a beat which threw still has to be reported.</summary>
    private void BeginPhases()
    {
        Array.Clear(_phaseTicks);
        _phaseStart = _phaseT0 = Stopwatch.GetTimestamp();
    }

    /// <summary>Close the beat: the total the parts are measured against. Called OUTSIDE the tick's
    /// try/catch, so a beat that threw part-way through still reports the part it ran and carries the rest
    /// in <c>other</c>. Also where the running totals take this beat's unmarked tail — the same remainder,
    /// for the same reason: a beat that threw still cost what it cost, and the part it did not reach has to
    /// show somewhere rather than vanish out of the rate.</summary>
    private void EndPhases()
    {
        _phaseTotal = Stopwatch.GetTimestamp() - _phaseStart;
        _phaseTailTicks += _phaseTotal - (_phaseT0 - _phaseStart);   // the beat minus everything it marked
    }

    /// <summary>Close phase <paramref name="phase"/> and open the next one.</summary>
    private void MarkPhase(int phase) => MarkPhaseAt(phase, Stopwatch.GetTimestamp());

    /// <summary>The same, at a timestamp the caller has already taken — so the lock-wait bucket and
    /// <c>_lockWaitMs</c> are one measurement rather than two reads of the clock that could disagree.</summary>
    private void MarkPhaseAt(int phase, long now)
    {
        long d = now - _phaseT0;
        _phaseTicks[phase] = d;
        _phaseTotals[phase] += d;   // one add per phase per beat: the whole cost of the running totals
        _phaseT0 = now;
    }

    /// <summary>Milliseconds since this world's phase clock came up — the denominator for a rate computed
    /// from two <c>run/status.json</c> samples. Read on the status writer's thread; nothing but the
    /// constructor ever writes <c>_phaseEpoch</c>.</summary>
    internal long ElapsedMs => (Stopwatch.GetTimestamp() - _phaseEpoch) * 1000 / Stopwatch.Frequency;

    /// <summary>Every phase's total since the world came up, in milliseconds, in <see cref="PhaseNames"/>
    /// order with <c>other</c> last, plus the total of every beat — the whole the parts are measured
    /// against, exactly as the <c>SLOW TICK PHASES</c> line measures one beat.
    ///
    /// <para><paramref name="beatMs"/> is the sum of the named phases and the accumulated tail, and
    /// <c>other</c> is then that whole minus every named phase's PRINTED figure — the same definition, and
    /// the same arithmetic, <see cref="PhaseBreakdown"/> uses for one beat, so the document and the log line
    /// cannot drift. Two things follow, and both are deliberate. The parts sum to the whole EXACTLY: every
    /// phase truncates toward zero independently and the remainder carries that residue the way it carries
    /// the unmarked tail, instead of leaving up to a millisecond per phase unaccounted for. And the whole
    /// can never be smaller than its named parts, whatever seam drove the beat — which an independently
    /// accumulated beat total could not promise.</para>
    ///
    /// <para>The price, stated because a reader will otherwise trip over it: <c>other</c> is the one figure
    /// here that can go DOWN between two samples, by up to a millisecond per phase, when a phase's total
    /// crosses a millisecond boundary and takes its truncation residue out of the remainder with it. Every
    /// NAMED phase only ever rises.</para>
    ///
    /// <para><b>A sample may straddle a beat, and that is said out loud rather than hidden.</b> The tick
    /// thread writes these slots one at a time as the beat runs, and the status thread reads them without a
    /// lock, so a document can carry an early phase from the beat now running and a late phase from the
    /// previous one — up to one beat of skew per phase, 333ms, against samples ten seconds apart. For a
    /// rate over a span of minutes that is noise; for anyone who wants an exact beat-aligned reading, this
    /// is the wrong instrument.</para>
    ///
    /// <para><c>Volatile.Read</c> per slot for the reason <see cref="Ticks"/> gives: a 64-bit read is atomic
    /// on every runtime this targets, so nothing can tear, but a plain read leaves the JIT free to hoist it
    /// out of the status loop and publish the same number forever. No lock and no <c>Interlocked</c> — one
    /// writer, and the status writer must never queue behind the world. Allocates one small array per
    /// call; it is called once every <c>P1998_STATUS_MS</c> (10s by default) on another thread.</para></summary>
    /// <param name="beatMs">Every beat this world has run, added up — the whole the parts belong to. An
    /// <c>out</c> rather than a second property so that the whole and its parts come from ONE read of the
    /// instrument: two calls could land either side of a beat and publish a document whose parts and whole
    /// disagree by a beat for no reason the reader could see.</param>
    internal (string Name, long Ms)[] PhaseTotalsMs(out long beatMs)
    {
        long freq = Stopwatch.Frequency;
        var result = new (string, long)[PhCount + 1];
        long wholeTicks = Volatile.Read(ref _phaseTailTicks);
        long namedMs = 0;
        for (int i = 0; i < PhCount; i++)
        {
            long ticks = Volatile.Read(ref _phaseTotals[i]);
            wholeTicks += ticks;
            long ms = ticks * 1000 / freq;
            namedMs += ms;
            result[i] = (PhaseNames[i], ms);
        }
        // floor(a) + floor(b) <= floor(a + b), and every named phase is inside the whole, so the remainder
        // cannot go negative from the truncation above and needs no clamp to be honest.
        beatMs = wholeTicks * 1000 / freq;
        result[PhOther] = (PhaseNames[PhOther], beatMs - namedMs);
        return result;
    }

    /// <summary>A test seam, null in every process that is not the test host: an action run INSIDE one named
    /// phase of the beat — <c>(4.3) status</c>, immediately before that bucket closes.
    ///
    /// <para>Why the production path carries it: <c>Tests/TickPhaseTimingTests.cs</c> has to assert that the
    /// phase line NAMES a phase, and <see cref="PhaseBreakdown"/> folds anything under a millisecond into
    /// <c>other</c>. Its old premise was "4,000 wandering creatures cost whole milliseconds on any machine",
    /// which is a statement about the machine, and it failed once on a shared CI runner (PR #245) — the beat
    /// came in fast enough that every bucket truncated to zero and the line carried only <c>other</c>. A
    /// tolerance cannot fix a threshold; a phase whose cost the test SETS can. With this the fact is green
    /// on a machine of any speed, with no creatures seeded at all.</para>
    ///
    /// <para>Its cost on the healthy path is one static null check per beat — not per player, not per mob —
    /// and it is deliberately placed where a null check is already lost in the noise of a per-session
    /// loop.</para></summary>
    internal static Action? PhaseProbeForTest;

    /// <summary>This beat's phases, most expensive first, as the line that follows <c>SLOW TICK</c>.
    ///
    /// <para>Two rules keep the arithmetic honest. A phase under 1ms is not named — it is folded into
    /// <c>other</c> rather than dropped — and <c>other</c> is then the whole beat MINUS everything named, so
    /// the printed numbers always sum to the beat. Anything the phases failed to account for (a gap between
    /// two marks, the tail of a beat that threw) lands in <c>other</c> and is visible there; it is never
    /// silently absorbed into a neighbouring phase.</para>
    ///
    /// <para>Allocates (a StringBuilder and the two small arrays), which is why nothing calls it except the
    /// watchdog branch that has already decided this beat is worth a log line.</para></summary>
    private string PhaseBreakdown()
    {
        long freq = Stopwatch.Frequency;
        long[] ms = new long[PhCount + 1];
        long named = 0;
        for (int i = 0; i < PhCount; i++)
        {
            long v = _phaseTicks[i] * 1000 / freq;
            if (v < 1) continue;             // sub-millisecond: folded into `other` below, not dropped
            ms[i] = v;
            named += v;
        }
        // Every part truncates toward zero and so does the total, and floor(a)+floor(b) <= floor(a+b), so
        // this remainder cannot go negative from rounding alone.
        ms[PhOther] = _phaseTotal * 1000 / freq - named;

        const string head = "SLOW TICK PHASES:";
        var sb = new System.Text.StringBuilder(head);
        for (int printed = 0; ; printed++)
        {
            int best = -1;
            for (int i = 0; i <= PhOther; i++)
                if (ms[i] > 0 && (best < 0 || ms[i] > ms[best])) best = i;
            if (best < 0) break;
            sb.Append(printed == 0 ? " " : ", ").Append(PhaseNames[best]).Append(' ').Append(ms[best]).Append("ms");
            ms[best] = 0;
        }
        // A beat with nothing to attribute: the watchdog fired on `late` (a GC pause, or the OS not
        // scheduling us) while the tick body itself was under a millisecond. Say so rather than print a
        // bare header.
        if (sb.Length == head.Length) sb.Append(" other 0ms");
        return sb.ToString();
    }

    // Fixed-cadence heartbeat on its own thread. Schedules against an absolute deadline rather than sleeping
    // TickMs between iterations, so the tick's own work doesn't accumulate into drift (the old
    // `await Task.Delay(600)` loop actually ran at ~612ms). If we fall a whole period behind we resync to
    // now instead of trying to catch up — the world would rather skip a beat than run several back-to-back.
    internal void TickLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long next = TickMs;
        while (true)
        {
            int wait = (int)(next - clock.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);

            // Measured BEFORE the tick body: this is time the thread was not running at all (GC pause, OS
            // preemption, the machine swapping) as opposed to time the tick spent working.
            long late = clock.ElapsedMilliseconds - next;
            next += TickMs;
            if (next <= clock.ElapsedMilliseconds) next = clock.ElapsedMilliseconds + TickMs;

            long t0 = clock.ElapsedMilliseconds;
            var gc0 = GC.GetTotalPauseDuration();
            _lockWaitMs = 0;
            // What still reaches this catch: the pre-sweep phases (1)-(1.7) and the spawn rows inside (1)/(1.1)
            // catch their own throws (#106), and so do the sweep's per-map and per-creature guards (#109). Left
            // here are FlushTick, the sweep's outer walk over the maps, the terrain pre-warm, and the rest of
            // Tick outside those guards.
            try { Tick(); }
            catch (Exception e) { Log.Error("world tick threw outside its phase and sweep guards (FlushTick, the sweep's map walk, the terrain pre-warm) — the rest of this beat is abandoned, the next runs on schedule", e); }
            EndPhases();

            if (SlowTickMs <= 0) continue;
            long work = clock.ElapsedMilliseconds - t0;
            if (work < SlowTickMs && late < SlowTickMs) continue;
            long gcMs = (long)(GC.GetTotalPauseDuration() - gc0).TotalMilliseconds;
            LogSlowTick(work, late, gcMs);
        }
    }

    /// <summary>The watchdog's report on one beat, once something has decided the beat was slow: the counts
    /// line, then the phase breakdown that attributes its <c>work</c> number.
    ///
    /// <para>The counts line's format is FIXED — the load run's analysis parses it and the baseline report's
    /// numbers have to stay comparable against future runs — so the phase attribution is a second line after
    /// it, never a change to it. (Note for whoever greps a log next: <c>SLOW TICK</c> now matches two lines
    /// per slow beat. <c>SLOW TICK: work</c> still matches exactly one.)</para>
    ///
    /// <para>Its own method only so that <see cref="TickOnceWatchedForTest"/> can drive the real emission
    /// path; <see cref="TickLoop"/> is the production caller and owns the threshold.</para></summary>
    private void LogSlowTick(long work, long late, long gcMs)
    {
        // Counted HERE rather than at TickLoop's threshold test, because this method is the one place a slow
        // beat is reported from: TickLoop and TickOnceWatchedForTest each own their own gate and both arrive
        // here exactly once when that gate opens. So `slowTicks` can never disagree with the number of
        // `SLOW TICK: work` lines in the log — which is the whole point of publishing it, since the load run
        // counts those lines and has until now had to divide them by a wall-clock estimate. Interlocked
        // because a test seam can drive this from a thread other than the tick's; it is off the healthy path
        // entirely, once per logged beat.
        Interlocked.Increment(ref _slowTicks);

        // Read this line as: LATE with gc ~= late  -> a GC pause. LATE with gc ~0 -> the OS didn't
        // schedule us (machine-wide contention). WORK with lock ~= work -> a session thread was holding
        // _lock (something slow ran inside a critical section). WORK with lock ~0 -> the tick body
        // itself is genuinely too big for the population it's driving — and the line below says which part.
        Log.Warn($"SLOW TICK: work {work}ms (lock-wait {_lockWaitMs}ms), late {late}ms, gc {gcMs}ms — " +
                 $"{PlayerCount} player(s), {MobCount} mob(s) on {ActiveMapCount} active map(s)");
        Log.Warn(PhaseBreakdown());
    }

    /// <summary>One beat and the watchdog that reports it, exactly as <see cref="TickLoop"/> runs them, with
    /// the slow-tick threshold passed in rather than read from <see cref="SlowTickMs"/> — which is a static
    /// readonly off an environment variable, fixed for the life of the process the moment this type is
    /// initialised. Without this a test could only see the watchdog's output by running the real 333ms
    /// scheduler on a machine slow enough to trip it. Everything past the gate IS the production path: the
    /// same <see cref="LogSlowTick"/>, the same two lines.</summary>
    internal void TickOnceWatchedForTest(int slowMs, long late = 0)
    {
        var clock = Stopwatch.StartNew();
        var gc0 = GC.GetTotalPauseDuration();
        _lockWaitMs = 0;
        try { Tick(); }
        catch (Exception e) { Log.Error("world tick threw outside its phase and sweep guards (FlushTick, the sweep's map walk, the terrain pre-warm) — the rest of this beat is abandoned, the next runs on schedule", e); }
        EndPhases();

        if (slowMs <= 0) return;
        long work = clock.ElapsedMilliseconds;
        if (work < slowMs && late < slowMs) return;
        long gcMs = (long)(GC.GetTotalPauseDuration() - gc0).TotalMilliseconds;
        LogSlowTick(work, late, gcMs);
    }

    /// <summary>The BACK half of a beat — <see cref="FlushTick"/> alone — under the same watchdog, with the
    /// phase clock opened for it.
    ///
    /// <para>Only <c>(3.0) view snapshot</c> needs this, and only because of how <c>Monitor</c> behaves.
    /// That bucket measures <see cref="ReconcileViews"/>'s own <c>_lock</c> acquisition, so a test that wants
    /// to see time in it has to be holding <c>_lock</c> when the tick thread gets there. Through
    /// <see cref="TickOnceWatchedForTest"/> that is not arrangeable: <see cref="Tick"/> takes and RELEASES
    /// <c>_lock</c> a few instructions earlier, and a thread that has just released a monitor and re-enters
    /// it wins the race against a blocked waiter essentially every time — so the hold would land in
    /// <c>lock-wait</c> and never in <c>(3.0)</c>. Entering at <see cref="FlushTick"/> makes
    /// <c>ReconcileViews</c>' acquisition the first one this thread takes, which a holder can actually
    /// contend.</para>
    ///
    /// <para>Everything past the threshold gate is the production path — the same <see cref="LogSlowTick"/>,
    /// the same two lines. What is missing is the beat's front half, which is exactly what
    /// <see cref="FlushTickForTest"/> already leaves out.</para></summary>
    internal void FlushTickWatchedForTest(int slowMs)
    {
        var clock = Stopwatch.StartNew();
        BeginPhases();
        try { FlushTick(new TickQueues()); }
        catch (Exception e) { Log.Error("world tick threw outside its phase and sweep guards (FlushTick, the sweep's map walk, the terrain pre-warm) — the rest of this beat is abandoned, the next runs on schedule", e); }
        EndPhases();

        if (slowMs <= 0) return;
        long work = clock.ElapsedMilliseconds;
        if (work < slowMs) return;
        LogSlowTick(work, late: 0, gcMs: 0);
    }

    /// <summary>Counts for the slow-tick diagnostic. Cheap, and only read on the watchdog path.</summary>
    private int PlayerCount    { get { lock (_lock) return _maps.Sum(kv => kv.Value.Players.Count); } }
    private int MobCount       { get { lock (_lock) return _maps.Sum(kv => kv.Value.Mobs.Count); } }
    private int ActiveMapCount { get { lock (_lock) return _maps.Count(kv => kv.Value.Players.Count > 0); } }

    // One heartbeat: (1) refill dead spawn points that are due, (2) wander every live mob OR, if provoked,
    // chase and swing at its target instead (queuing any landed swings), (3) reconcile each player's
    // viewport (mobs that moved in/out of view, plus this tick's respawns, appear/disappear), (4) stream
    // moves/turns to observers, (4.5) apply this tick's queued mob swings, (5) regen every connected
    // player, (6) broadcast the day/night hour and any map's changed weather. All map mutation happens
    // under the lock; no socket I/O does. Only maps with at least one player are processed — an empty map's
    // roster stays put. (1)-(2) are this method; (3)-(6) are FlushTick, after the lock is released.
    private void Tick()
    {
        BeginPhases();
        _tick++;
        // This beat's outbound work, in one object: the locked phases below fill it, FlushTick drains
        // it once the lock is released (World.MobAiTick.cs).
        var q = new TickQueues();
        List<PhaseFault>? phaseFaults = null;   // phases (1)-(1.7)'s caught throws, if any; see (1)
        List<MobFault>? faults = null;          // the mob-AI sweep's caught throws, if any; see (2)

        // (0) Warm every active map's terrain BEFORE taking the lock. Both the respawn refill (Materialize ->
        // FreeSpawnTile) and the wander loop below call MapData.For, which on a miss reads the .map off disk,
        // decodes every cell and runs a SQLite query. Under _lock that stalled the whole world; out here it
        // costs nothing on the overwhelmingly common cache-hit path. See MapData.Prewarm. A boss filed under its
        // rooms' maps (#333) is refilled while any of them is active and built on any of them, so the maps
        // beside an active one are warmed too (SpawnDirector.PrewarmRoomMaps).
        ushort[] active;
        lock (_lock) active = _maps.Where(kv => kv.Value.Players.Count > 0).Select(kv => kv.Key).ToArray();
        foreach (var id in active) MapData.Prewarm(id);
        SpawnDirector.PrewarmRoomMaps(active);

        long lockT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        MarkPhaseAt(PhWarm, lockT0);
        lock (_lock)
        {
            // Time spent BLOCKED here means another thread was inside a _lock critical section. The
            // slow-tick watchdog prints it, which is what distinguishes "someone else stalled us" from
            // "this tick body is too slow".
            long acquired = System.Diagnostics.Stopwatch.GetTimestamp();
            _lockWaitMs = (acquired - lockT0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            MarkPhaseAt(PhLockWait, acquired);

            // Phases (1) to (1.7) each have their own exception boundary (#106), the same log-and-skip shape
            // as the (2) sweep's guards below. A phase that throws loses the rest of ITSELF this beat and
            // nothing else: the phases after it, the mob sweep and FlushTick still run. Before these guards a
            // throw here reached TickLoop's catch and the whole beat was lost, every queue with it.
            //
            // Drain what there is. Whatever a phase did or queued before it threw stands and is sent: a point
            // that materialised before the bad one is drawn by (3), a morph already queued is reverted. Nothing
            // is unwound, and what the phase did not reach waits for the next beat. (1) and (1.1) go one level
            // finer: a spawn point, or a group member, that throws is caught by its own row guard inside the
            // SpawnDirector and costs only itself, so the rows after it still spawn in the same beat. A throw
            // part-way through a phase can leave its work half done: forage items already added to the map
            // when TopUpForageLocked throws are on the ground but not broadcast this beat, because its drop
            // list is never returned. That is the same torn state the per-mob guard documents at (2), and the
            // same state the old shape left before it lost the rest of the beat on top of it.
            //
            // MarkPhase runs after each guard, thrown or not, so the phase clock still closes every bucket.
            // Nothing is logged under the lock (#109): a throw costs one PhaseFault in `phaseFaults`, and
            // LogPhaseFaults writes it after the lock is released. Plain try/catch, no closure or delegate per
            // phase: a beat with no throw allocates nothing, and `phaseFaults` stays null. PreSweepProbeForTest
            // is null outside the test host (World.MobFaults.cs).

            // (1) respawns: refill any due spawn point on a map someone is watching. Points only — the
            // hunting maps refill in batches at (1.1), not one mob at a time as they die. A point that throws
            // is a row fault in `phaseFaults` and the walk goes on (SpawnDirector.RespawnDuePoints).
            try
            {
                PreSweepProbeForTest?.Invoke(PhRespawns);
                _spawnDirector.RespawnDuePoints(_tick, ref phaseFaults);
            }
            catch (Exception e) { (phaseFaults ??= new()).Add(new PhaseFault(PhRespawns, e)); }
            MarkPhase(PhRespawns);

            // (1.1) batch refills: every due spawn group on a map someone is hunting (RTK's spawner NPC,
            // whose own `#pc > 0` test this mirrors). A map nobody is on is skipped here and caught by
            // EnsureMaterialized when someone walks in, so the room is full before their viewport is built
            // rather than filling in around them. Sampled every BatchSweepTicks — these clocks are in whole
            // seconds and the shortest is 2s, so there is nothing to gain from looking every 600ms. A group
            // member that throws is a row fault in `phaseFaults` and the batch goes on (RefillGroups).
            try
            {
                PreSweepProbeForTest?.Invoke(PhRefills);
                _spawnDirector.RefillDueGroups(_tick, ref phaseFaults);
            }
            catch (Exception e) { (phaseFaults ??= new()).Add(new PhaseFault(PhRefills, e)); }
            MarkPhase(PhRefills);

            // (1.2) morph expiry (Session.CastMorph/RevertMorph): purely cosmetic per-player visual state
            // with no server-side entity of its own — the revert broadcast is socket I/O, so it's deferred
            // outside the lock same as trapDamage/expiredPets below.
            try
            {
                PreSweepProbeForTest?.Invoke(PhMorphs);
                foreach (var (_, pm) in _maps)
                    foreach (var p in pm.Players)
                        if (p.IsMorphExpired || p.IsDisguiseExpired) q.ExpiredMorphs.Add(p);   // a lapsed morph, or a Costume run
                foreach (var (_, pm) in _maps)
                    foreach (var p in pm.Players)
                        if (p.IsStealthExpired) q.ExpiredStealth.Add(p);   // faded (invisible-spell) look lapsed with no hit — revert
            }
            catch (Exception e) { (phaseFaults ??= new()).Add(new PhaseFault(PhMorphs, e)); }
            MarkPhase(PhMorphs);

            // (1.3) bladestorm auto-expiry: an untriggered decoy despawns silently after its 21s lifetime —
            // traps have no ground graphic (same precedent as the hazard family), so this is a plain in-lock
            // removal, no broadcast/deferral needed.
            try
            {
                PreSweepProbeForTest?.Invoke(PhDecoys);
                foreach (var (_, pm) in _maps)
                    pm.Traps.RemoveAll(t => t.ExpiresAt != 0 && Environment.TickCount64 >= t.ExpiresAt);
            }
            catch (Exception e) { (phaseFaults ??= new()).Add(new PhaseFault(PhDecoys, e)); }
            MarkPhase(PhDecoys);

            // (1.5) forage top-up: on a slow cadence, refill each forage box (chestnuts &c.) to its target count.
            try
            {
                PreSweepProbeForTest?.Invoke(PhForage);
                if (_tick % ForageTicks == 0) q.Forage = TopUpForageLocked();
            }
            catch (Exception e) { (phaseFaults ??= new()).Add(new PhaseFault(PhForage, e)); }
            MarkPhase(PhForage);

            // (1.6) day/night clock (see the Epoch doc): re-derive the shared calendar from wall-clock time
            // and, on an in-game hour rollover, flag every connected session for a fresh 0x20 broadcast.
            // Checked every tick rather than every 750th, so the broadcast lands within 600ms of the true
            // rollover instead of drifting by however far into an hour the process happened to start.
            try
            {
                PreSweepProbeForTest?.Invoke(PhClock);
                if (Clock.Sync()) q.TimeChanged = true;
            }
            catch (Exception e) { (phaseFaults ??= new()).Add(new PhaseFault(PhClock, e)); }
            MarkPhase(PhClock);

            // (1.7) weather: when the deterministic weather PERIOD rolls over (WeatherModel.PeriodHours, ~15
            // real min), recompute each active map's weather and broadcast to any whose sky actually changed.
            // A season change lands on a period boundary too, so this pass catches those as well. Cheap: the
            // period only advances a couple of times an hour. Overrides are broadcast eagerly elsewhere.
            try
            {
                PreSweepProbeForTest?.Invoke(PhWeather);
                Weather.SweepPeriod(q);
            }
            catch (Exception e) { (phaseFaults ??= new()).Add(new PhaseFault(PhWeather, e)); }
            MarkPhase(PhWeather);

            // (2) wander: each mob acts only when its own MoveTime has elapsed (RTK MobMoveTime), and even
            // then usually just turns instead of stepping — mirroring RTK mob_ai_normal (checkmove: pick a
            // random side, only step when it matches the current facing, else 4-in-11 step straight ahead).
            // This paces a rabbit (3000ms) to a hop every few seconds, not every 600ms heartbeat.
            //
            // Two exception boundaries, one per mob and one per map, so a creature that throws costs ITSELF the
            // beat and nothing else. Before them the only catch was TickLoop's, which abandoned the whole tick:
            // every list queued above and below was dropped unsent while the mobs already stepped stayed
            // stepped. A skipped mob is left exactly where the throw found it: nothing here unwinds. Its
            // tile-set entry, its timers and any queue entry it added before throwing all stand, and a throw
            // part-way through a step can leave work half done in ways a completed step never does — a
            // creature standing on a trap it stepped onto but never sprang (the move is queued before the
            // trigger runs), a queued swing whose cooldown was never advanced. That is the same torn state the
            // old shape left, when the whole beat was lost on top of it; it is not new, and it is not repaired.
            // Plain try/catch rather than Try(): this is the hot loop, and a closure per mob per beat is an
            // allocation the sweep does not need.
            //
            // Neither guard logs (#109). Under the lock a throw costs one MobFault appended to `faults` — the
            // facts, unformatted — and LogMobFaults writes them after the lock is released, one stack per new
            // fault and one count line per beat for the repeats. It used to be a full Log.Error per throw, the
            // whole stack formatted in here: 200 creatures of one bad row held the world for 8-15 ms a beat and
            // wrote ~330 KB/s of identical stacks. `faults` stays null on a beat where nothing throws.
            foreach (var (mapId, m) in _maps)
            {
                if (m.Mobs.Count == 0 || m.Players.Count == 0) continue;   // no observers -> don't bother
                try
                {
                    // The map's collision index and this tick's queues, packaged for MobAiTick.Step (World.MobAiTick.cs).
                    var ctx = new MobTickContext(this, mapId, m, q);
                    SweepProbeForTest?.Invoke(mapId);   // null outside the test host; see World.MobFaults.cs

                    foreach (var mob in m.Mobs)
                    {
                        if (!mob.Alive) continue;
                        try { MobAiTick.Step(ctx, mob); }
                        catch (Exception e) { (faults ??= new()).Add(new MobFault(mapId, mob.Id, mob.Key, e, WholeMap: false)); }
                    }
                }
                catch (Exception e) { (faults ??= new()).Add(new MobFault(mapId, 0, null, e, WholeMap: true)); }
            }
            MarkPhase(PhWander);
        }

        // (3)-(6): everything queued above, sent now the lock is released. The phase faults and the sweep's
        // faults are written after it, in the order they happened, in a finally so a flush that throws
        // cannot lose them; their cost lands in the phase clock's `other`, the beat's unmarked tail, and on a
        // beat with no fault it is two null tests.
        try { FlushTick(q); }
        finally
        {
            if (phaseFaults is not null) LogPhaseFaults(phaseFaults, _tick);
            if (faults is not null) LogMobFaults(faults, _tick);
        }
    }

    /// <summary>
    /// The back half of one heartbeat: everything <see cref="Tick"/> queued under <c>_lock</c>, sent now that
    /// it is released. Phases (3) to (6), in the order they are written — viewports first (so a mob that left
    /// the screen is despawned rather than sent an off-screen move), then moves and turns, then the deferred
    /// visuals, then this beat's swings, then regen, then the clock and the weather.
    ///
    /// <para>Three of those boundaries are BEHAVIOUR rather than layout, and
    /// <c>Tests/MobAiTickTests.cs</c> pins exactly those three on the wire: viewports before moves, moves
    /// before turns, turns before swings. The rest of the (3)-(6) sequence — the deferred visuals, the Lua
    /// hooks, regen, the clock, the weather — is NOT pinned by a test; it is preserved because the body was
    /// moved here verbatim from <c>Tick</c> (135 lines, 0 differing), and reordering any of it would be a
    /// change no test would catch. Treat it as load-bearing until someone proves otherwise.</para>
    ///
    /// <para>Runs OUTSIDE <c>_lock</c> and must keep doing so — it sends. It takes the lock only where the
    /// body always did (the <c>_deferredFx</c> / <c>_hooks</c> / <c>_deferredTrapClears</c> drain and the
    /// player snapshot), and <see cref="ReconcileViews"/>, its first statement, takes its own.</para>
    /// </summary>
    private void FlushTick(TickQueues q)
    {
        // (3) Reconcile viewports FIRST, using the mobs' NEW positions: spawn any that stepped into view,
        // despawn any that stepped out. Doing this before the moves means a mob that just left the screen is
        // despawned (0x0E) rather than sent an off-screen 0x0C the client would cull — the desync that made
        // mobs vanish for good.
        // ReconcileViews closes `(3.0) view snapshot` at the end of its own `lock (_lock)` block, so this
        // mark closes the lockless per-player sweep alone.
        ReconcileViews();
        MarkPhase(PhViewports);

        // (4) Now stream moves/turns, but only to players who still have that mob in view (MoveMob/SideMob
        // are no-ops otherwise) — bounding on-wire traffic to on-screen mobs even on a 400-spawn map.
        foreach (var mv in q.Moves)
            Broadcast(mv.map, p => p.MoveMob(mv.id, mv.x, mv.y, mv.dir));
        foreach (var tn in q.Turns)
            Broadcast(tn.map, p => p.SideMob(tn.id, tn.dir));
        MarkPhase(PhMoves);

        // Repeating status effects queued above (venom's per-tick zap, doze/sleep's drowse) — the same 0x29 +
        // 0x19 pair a cast plays, re-sent over the afflicted creature for as long as the status holds.
        // Bars for health that changed without a hit (the self-heal). Same 0x13 the damage path uses, so the
        // bar animates up exactly the way it animates down.
        foreach (var hs in q.HealthShows)
        {
            byte pct = MobHpPercent(hs.mob);
            BroadcastWideArea(hs.map, hs.mob.X, hs.mob.Y, p => p.DamageOver(hs.mob.Id, pct, 0));
        }

        foreach (var fr in q.FxRepeats)
        {
            BroadcastWideArea(fr.map, fr.x, fr.y, p => p.EffectOver(fr.id, fr.anim));
            if (fr.sound > 0) BroadcastSameArea(fr.map, fr.x, fr.y, p => p.SoundAt(fr.sound, fr.id));
        }

        // …and anything raised from inside TryDamage, which has no way to send where it stands.
        List<(ushort map, uint id, ushort x, ushort y, int anim, int sound)> deferred;
        List<(string key, string hook, ushort map, Mob mob, Session? actor)> hooks;
        List<(ushort map, uint trapId)> trapClears;
        Dictionary<int, double>? pitFigures = null;
        lock (_lock)
        {
            deferred = new List<(ushort, uint, ushort, ushort, int, int)>(_deferredFx); _deferredFx.Clear();
            hooks = new List<(string, string, ushort, Mob, Session?)>(_hooks); _hooks.Clear();
            trapClears = new List<(ushort, uint)>(_deferredTrapClears); _deferredTrapClears.Clear();
            if (_pitStrikeFigures.TryGetValue(q.TrapDamage, out var figures))
            {
                pitFigures = figures;
                _pitStrikeFigures.Remove(q.TrapDamage);
            }
        }
        // Traps that went off this tick: rub out their revealed marker on everyone who had spotted them.
        foreach (var tc in trapClears) Broadcast(tc.map, p => p.ClearTrapMarker(tc.trapId));
        foreach (var fx in deferred)
        {
            // The tile was captured when the effect was queued, not looked up now — the mob that raised it may
            // already be dead and off the map by the time this flushes, and a death effect still has to be seen
            // and heard from where the body fell.
            if (fx.anim  > 0) BroadcastWideArea(fx.map, fx.x, fx.y, p => p.EffectOver(fx.id, fx.anim));
            if (fx.sound > 0) BroadcastSameArea(fx.map, fx.x, fx.y, p => p.SoundAt(fx.sound, fx.id));
        }
        MarkPhase(PhFx);

        // Lua AI hooks, run here and only here — outside the lock (see _hooks).
        foreach (var h in hooks)
            Try(() => MobScript.Fire(h.key, h.hook, new MobContext(this, h.map, h.mob, h.actor)), $"mob hook {h.key}.{h.hook}");
        MarkPhase(PhHooks);

        // The PLAYER half of the same thing: a dozed player's drowse redraws and their hold lapses. Kept out
        // here with the other broadcasts rather than in the mob loop — it is per-session, not per-mob, and it
        // sends. Only sleepers do any work; TickSleep returns immediately for everyone else. A caster's periodic spell
        // ticks (Shaman's torment) beat here too, outside the lock, as a cast would run them (Session.TickSpellTicks).
        foreach (var s in Online.All())
        {
            Try(s, static x => x.TickSleep(), "TickSleep");
            Try(s, static x => x.TickPoison(), "TickPoison");
            Try(s, static x => x.TickSpellTicks(), "TickSpellTicks");
        }
        PhaseProbeForTest?.Invoke();   // null except under test — see World.PhaseProbeForTest
        MarkPhase(PhStatus);

        // Wisdom / "Listen to advice" (0x1b sub-4): a gameplay hint into the chat channel every ~15 minutes for
        // players who left the option on. RTK runs this per-player from login; we fire it server-wide on the
        // same cadence as the weather roll. SendAdvice is a no-op for anyone with the option off.
        if (_tick % AdviceTicks == 0)
            foreach (var s in Online.All()) Try(s, static x => x.SendAdvice(), "SendAdvice");

        // Newly-foraged ground items (chestnuts &c.): draw them for everyone on that map (0x16).
        if (q.Forage is not null)
            foreach (var (map, gi) in q.Forage)
                Broadcast(map, p => p.ShowGroundItem(gi));
        MarkPhase(PhAdvice);

        // (4.5) Resolve this tick's mob swings (queued above while still under the lock) — applying player
        // damage runs Session-side (HUD update + broadcast + possible death), so it happens out here like
        // every other socket-touching step.
        foreach (var h in q.Hits)
        {
            // On the SWING itself, hit or miss — this is the point where the mob commits to the attack. The
            // 0x1A action makes the mob visibly swing (matching the player's own swing anim in HandleAttack) and
            // 009.wav is the swing sfx; the landed-hit sound (001.wav) is layered on separately by ApplyMobHit.
            BroadcastSameArea(h.map, h.mob.X, h.mob.Y, p => p.ActionOver(h.mob.Id, GmOverrides.MobSwingActionType, GmOverrides.MobSwingActionTime, 0));
            BroadcastSameArea(h.map, h.mob.X, h.mob.Y, p => p.SoundAt(Session.MobSwingSfx, h.mob.Id));
            int dmg = MobSwingDamage(h.mob.MinDam, h.mob.MaxDam);
            Try(() => h.target.ApplyMobHit(h.mob, dmg), $"ApplyMobHit {h.mob.Name} -> {h.target.Remote}");
        }
        MarkPhase(PhSwings);

        // Creature spells + idle flavour queued above — both broadcast, and a spell can kill, so neither can
        // run under the lock.
        foreach (var c in q.MobCasts) Try(() => c.target.ApplyMobSpell(c.mob, c.spell), $"ApplyMobSpell {c.mob.Name} -> {c.target.Remote}");
        foreach (var ch in q.Chatter)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(
                ch.channel == 0 ? $"{ch.mob.Name}: {ch.line}" : ch.line);   // RTK talk(0) attributes, talk(2) doesn't
            // Proximity-gated around the creature, matching RTK bll_talk's map_foreachinarea(..., AREA, ...).
            BroadcastArea(ch.map, ch.mob.X, ch.mob.Y, Session.SayHalfW, Session.SayHalfH,
                p => p.SpeakEntity(ch.channel, ch.mob.Id, bytes));
        }
        MarkPhase(PhCasts);

        // Pet swings queued above: same damage roll as any other mob swing, but landing on a mob.
        foreach (var ph in q.MobHits)
            Try(() => ApplyMobOnMobHit(ph.map, ph.attacker, ph.victim, MobSwingDamage(ph.attacker.MinDam, ph.attacker.MaxDam)), "ApplyMobOnMobHit");

        // Trap hits + poison ticks queued above (same reasoning as the mob-swing pass: Session-facing
        // broadcasts/exp can't run under the lock).
        // A Pit trap's strike lands at its own place in the list, capped against the vita its creature has then.
        for (int i = 0; i < q.TrapDamage.Count; i++)
        {
            var td = q.TrapDamage[i];
            if (pitFigures is not null && pitFigures.TryGetValue(i, out double figure))
                Try(() => ApplyPitStrike(td.map, td.mob, figure, td.ownerId), "ApplyPitStrike (tick)");
            else
                Try(() => ApplyTrapDamage(td.map, td.mob, td.dmg, td.ownerId), "ApplyTrapDamage (tick)");
        }
        MarkPhase(PhPetTrap);

        // Expired pets queued above — plain despawn, no kill/loot.
        foreach (var ep in q.ExpiredPets)
            Try(() => DespawnMob(ep.map, ep.mob), "DespawnMob (expired pet)");

        // Expired morphs and disguise runs queued above — revert the peer-visible disguise back to our real human look.
        foreach (var mp in q.ExpiredMorphs)
            Try(() => mp.RevertLapsedLooks(), "RevertLapsedLooks");

        // Expired stealth queued above — restore the normal look once the invisible-spell timer lapses w/o a hit.
        foreach (var sp in q.ExpiredStealth)
            Try(() => sp.RevertStealth(), "RevertStealth");
        MarkPhase(PhExpiries);

        // (5) natural HP/MP regen for EVERY connected player (not gated on mobs/viewport, unlike the
        // steps above), plus the per-player buff expiry, fury wear-off and mail backstop that ride the same
        // call — see Session.RegenTick, which only emits a status packet on a real change.
        //
        // The 25s regen beat is the WORLD's, not each player's (_regenClockMs): everyone below full
        // regenerates in the same moment, as the original game did. Advanced here, immediately before the
        // loop that reads it, so the clock moves exactly once a beat whatever else the flush does.
        // Snapshot the player list under the lock, tick outside it.
        _regenClockMs += TickMs;
        bool regenDue = _regenClockMs >= RegenIntervalMs;
        if (regenDue) _regenClockMs -= RegenIntervalMs;
        Session[] players2;
        lock (_lock) players2 = _maps.Values.SelectMany(m => m.Players).ToArray();
        foreach (var p in players2) Try((p, regenDue), static t => t.p.RegenTick(TickMs, t.regenDue), "RegenTick");
        MarkPhase(PhRegen);

        // (6) day/night + weather broadcasts queued above — every connected session hears the new hour
        // (RTK broadcasts clif_sendtime server-wide, not per-map), each affected map hears its own weather.
        if (q.TimeChanged)
        {
            var (h, y) = Clock.Time;
            foreach (var p in players2) Try((p, h, y), static t => t.p.SendTime(t.h, t.y), "SendTime");
            // Nothing to persist: the calendar is derived from the epoch, so a restart resumes it exactly.
        }
        if (q.WeatherChanges is not null)
            foreach (var (map, w) in q.WeatherChanges)
                Broadcast(map, p => p.SendWeather(w));
        MarkPhase(PhTime);
    }

    // Snapshot each populated map's (players, mobs, items) and its change generation under the lock, then
    // reconcile outside it the viewport of every player whose map or own view state has changed since that
    // player last swept. No allocation on the hot path beyond the snapshot arrays.
    private void ReconcileViews()
    {
        // Floor items ride along with the mobs: a forage top-up or another player's drop lands on the map
        // while we're standing still, and its own broadcast is viewport-gated like every other 0x07 — so the
        // tick is what eventually draws it for whoever is close enough. Hence `|| m.Items.Count > 0`: a map
        // with items but no mobs still needs reconciling. And `|| m.Players.Count > 1`: a peer walking toward
        // us is viewport-gated the same way (0x33), so a mob-less, item-less map with two players still needs
        // the tick to draw each into the other's view as they close the distance.
        // The coordinates come out WITH the player list, in the same acquisition. They used to be read back
        // off each Session inside ReconcilePeer, out here with no lock held — two ushort reads of a character
        // whose owner writes both under _lock, so the gate could test one tile's X against the previous
        // tile's Y (and its two InView calls could each see a different pair). See PeerTile. The peer's ID
        // comes out here too, in the same acquisition and for the same reason — it is another session's
        // field — which is what lets SyncPeers' capture pass decide without dereferencing any peer at all.
        //
        // THE MAP ID AND ITS CHANGE GENERATION COME OUT IN THE SAME ACQUISITION, for the reason the tiles do.
        // MapState.ViewGen counts every write of anything the three sweeps read on this map, and every one of
        // those writes happens under this same lock; captured here, WITH the roster, the pair is consistent —
        // this roster is what the map looked like at this generation. Compared outside the lock against the
        // CAPTURED value, never a live read, so a change that lands while the sweeps run shows up as a
        // difference on the next beat instead of being silently recorded as already swept.
        (ushort mapId, long gen, PeerTile[] players, Mob[] mobs, GroundItem[] items)[] snapshot;
        lock (_lock)
        {
            snapshot = _maps
                .Where(kv => kv.Value.Players.Count > 0 && (kv.Value.Mobs.Count > 0 || kv.Value.Items.Count > 0 || kv.Value.Players.Count > 1))
                .Select(kv => (kv.Key, kv.Value.ViewGen,
                               kv.Value.Players.Select(p => new PeerTile(p, p.PlayerId, p.PlayerX, p.PlayerY)).ToArray(),
                               kv.Value.Mobs.ToArray(), kv.Value.Items.ToArray()))
                .ToArray();
        }
        // (3.0) The snapshot closes here, and the sweep below is then `(3) viewports` on its own. Load-run-2
        // is why the bucket was split: `(3) viewports` led 37 of the 38 slow beats at 400 players on one map,
        // at p50 111ms, while the whole-sweep bench models ~11ms of that — and the phase spanned two things
        // with nothing in common. This block is the acquisition of `_lock` against 400 session read-loop
        // threads plus the LINQ copy of every populated map's roster; everything after it is lockless
        // per-player work. The `lock-wait` figure on the counts line cannot see this one: it measures only
        // `Tick`'s own acquisition, taken and released before FlushTick starts. The mark is AFTER the block
        // because MarkPhase CLOSES the bucket it names.
        MarkPhase(PhViewSnapshot);
        // The three arrays are passed, not captured: a lambda that closed over them would be a display class
        // plus a delegate per player per beat (104 B measured, PR #245's follow-up 3), where a tuple of three
        // references is a struct that stays on the stack and lets the compiler cache one delegate for this
        // site. Same three calls, same order, same isolation, same log line.
        //
        // THE SKIP (P1998_TICK_SWEEP_SKIP, default on). BeginTickSweep answers "has anything this viewer's
        // three sweeps read changed since this viewer last swept this map" — the map's captured generation
        // against the one it recorded, plus its own per-viewer pending flag — and false means the three calls
        // are not made at all this beat. It is sound because a sweep is idempotent over unchanged inputs:
        // the argument is written out at Session.BeginTickSweep, and the frames are pinned against the
        // switched-off shape by Tests/TickSweepSkipTests.cs. Nothing else about this loop changes: the same
        // three calls in the same order, inside the same Try with the same log line, and the generation is
        // recorded only AFTER they return, so a throw a Try swallows leaves the viewer un-skipped next beat.
        foreach (var (mapId, gen, players, mobs, items) in snapshot)
        {
            // Every player of this map is a viewer this beat CONSIDERED, skipped or not — one add per map per
            // beat on the tick thread, outside the lock, before anything can decide otherwise. Its partner is
            // counted at the far end, in Session.EndTickSweep; see SweepViewers for what the pair is worth.
            _sweepViewers += players.Length;
            foreach (var p in players)
                Try((p.Session, mapId, gen, players, mobs, items),
                    static t =>
                    {
                        if (!t.Session.BeginTickSweep(t.mapId, t.gen)) return;
                        t.Session.SyncPeers(t.players); t.Session.SyncMobs(t.mobs); t.Session.SyncGroundItems(t.items);
                        t.Session.EndTickSweep(t.mapId, t.gen);
                    },
                    "ReconcileViews");
        }
    }

    /// <summary>Run one per-player / per-mob step in isolation: a throw in one player's RegenTick, one
    /// mob's AI hook or one peer's broadcast delivery must not abort the rest of the sweep — and on the tick
    /// thread it must not reach <see cref="TickLoop"/>'s outer catch, where it would cost EVERY later step
    /// of this beat.
    ///
    /// <para>This used to be <c>catch { }</c> on the theory that the only thing that could throw here was a
    /// send to a dead socket. That has not been true since the outbound channel: <c>Session.Send</c> is a
    /// non-blocking <c>TryWrite</c> that closes the session on a full queue and never throws. So anything
    /// caught here is a real bug in the wrapped code — and nineteen sites, including <c>ApplyMobHit</c>,
    /// <c>ApplyMobSpell</c> and every Lua AI hook, were swallowing those with no trace at all.
    /// <paramref name="what"/> names the site so the log says which of the nineteen it was.</para></summary>
    private static void Try(Action a, string what)
    {
        try { a(); }
        catch (Exception e) { Log.Error($"isolated step '{what}' threw — skipped, the sweep continues", e); }
    }

    /// <summary>The same isolation, for a step whose argument can be passed instead of captured — which is
    /// every PER-PLAYER site in <see cref="FlushTick"/>.
    ///
    /// <para><b>Why it exists.</b> The overload above can only be reached with a delegate, and at a
    /// per-player site there is no delegate the compiler can cache: <c>Try(s.TickSleep, …)</c> binds a fresh
    /// one to <c>s</c> on every call, and <c>Try(() =&gt; p.RegenTick(TickMs), …)</c> allocates a display
    /// class for <c>p</c> as well. Measured on this repo, Debug and Release alike, at 400 players: 64 B per
    /// method group, 88 B per one-capture closure and 96 B per multi-capture closure, per player per beat —
    /// 320 B a player a beat across the three sites that run every beat, 128 KB a beat, 385 KB a second.
    /// Called with a STATIC lambda (<c>static s =&gt; s.TickSleep()</c>) this overload allocates nothing at
    /// all: the lambda captures nothing, so the compiler caches one delegate per site for the life of the
    /// process and the loop hands it a different argument each time. <c>Tests/TickStepIsolationTests.cs</c>
    /// pins the zero.</para>
    ///
    /// <para>For a step that needs more than one value, <typeparamref name="T"/> is a tuple of them
    /// (<c>Try((p, h, y), static t =&gt; t.Item1.SendTime(t.Item2, t.Item3), "SendTime")</c>) — a struct
    /// passed by value, so it stays on the stack and nothing is captured. The isolation itself, the catch and
    /// the log line are the overload above's, word for word; nothing about which throws are swallowed, which
    /// are logged or what the line says changes with the shape.</para></summary>
    private static void Try<T>(T arg, Action<T> step, string what)
    {
        try { step(arg); }
        catch (Exception e) { Log.Error($"isolated step '{what}' threw — skipped, the sweep continues", e); }
    }

    /// <summary>The generic helper, reachable from the suite. <c>Tests/TickStepIsolationTests.cs</c> measures
    /// its allocation across thousands of calls, which is a claim about the REAL helper and cannot be made
    /// against a copy of it in the test project.</summary>
    internal static void TryForTest<T>(T arg, Action<T> step, string what) => Try(arg, step, what);

    /// <summary>A mob's raw melee swing (RTK <c>swingDamage.lua</c> <c>_getMobSwingDamage</c>): three
    /// independent uniform draws over the range split into thirds, summed and floored, +1. This is NOT a
    /// flat roll across [MinDam,MaxDam] — three thirded draws concentrate the result near the midpoint
    /// (Irwin-Hall-ish), matching RTK's actual distribution. The target's armor is applied separately, by
    /// the target itself (<see cref="Session.ApplyMobHit"/>), since AC/gear/buffs are session-local state.</summary>
    private static int MobSwingDamage(int minDam, int maxDam)
    {
        double lo = minDam / 3.0, hi = maxDam / 3.0;
        double sum = 0;
        for (int i = 0; i < 3; i++) sum += lo + Random.Shared.NextDouble() * (hi - lo);
        return 1 + (int)Math.Floor(sum);
    }
}
