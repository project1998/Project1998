using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #168 item 2b: a session a newer login has REPLACED writes its character row no more, and the kick that
/// replaces it writes that row one last time, unconditionally, before it says so.
///
/// <para><b>The window.</b> <c>HandleArrival</c> runs <c>Session.KickForReplacement</c> on the old session (O)
/// and then loads the row. O stays on its map, in its party and in the autosave sweep's roster until its own
/// read loop unwinds into the teardown. Before this change the kick's latch (<c>_replaced</c>) stopped only the
/// teardown's own save: a party share, a death, the sweep and the shutdown flush all still wrote O's row after
/// the new session (N) had loaded it. N's next write repairs the row, but a crash, a shutdown with N clean or a
/// second replacement before that leaves the row rolled back to O.</para>
///
/// <para><b>The change.</b> One check at the single-session write chokepoint (<c>CaptureAndWrite</c>), which
/// every one of those writers goes through, and a reordered kick: write unconditionally, then latch. The facts
/// below drive each writer on a session kicked through the real <c>KickForReplacement</c> and compare the
/// stored row, as JSON, with the row the kick left.</para>
///
/// <para>The trade finalizer's pair write does not go through the chokepoint; its refusal is pinned in
/// <c>ReplacedTradeTests</c>.</para>
/// </summary>
[Collection("world")]
public class ReplacedSessionWriteTests
{
    /// <summary>Content-free map ids in the instance band (59000-65000), so a death here charges exp only and
    /// drops nothing on the floor. One per fact; no other class stands here.</summary>
    private const ushort WritersMap = 61700, KickWriteMap = 61701;

    private static readonly MethodInfo DieMethod =
        typeof(Session).GetMethod("Die", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo StoreSaveMethod =
        typeof(Session).GetMethod("StoreSave", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;

    public ReplacedSessionWriteTests(SessionFixture fx) => _fx = fx;

    /// <summary>The kick's write lands, whether or not anything is dirty. The coin change below is made
    /// WITHOUT <c>MarkDirty</c>, so only an unconditional write can carry it to the row; that is the shape
    /// the in-flight sweep leaves behind (see <see cref="AnAutosaveInFlightAtTheKickCannotLandAfterIt"/>).
    ///
    /// <para>Falsified twice (see the #168 report): the kick back on the dirty-gated <c>FlushNow()</c> goes red
    /// on the row's coins; the latch moved back above the write goes red the same way, because the chokepoint
    /// then refuses the kick's own write.</para></summary>
    [Fact]
    public void TheKickWritesTheRowEvenWhenNothingIsDirty()
    {
        const string name = "ReplacedKickWrite";
        var (old, _, character) = _fx.PlayerWith(name, _ => { }, KickWriteMap, 5, 5);

        try
        {
            old.WithState(() => character.Coins = 4321);   // no MarkDirty: the session stays clean
            Assert.Contains("dirty False", old.DiagState());

            old.KickForReplacement();

            Assert.True(old.IsReplaced);
            Assert.Equal(4321u, LoadOk(name).Coins);
        }
        finally
        {
            _fx.World.LeaveMap(old, KickWriteMap);
        }
    }

    /// <summary>After the kick, every single-session writer the #168 sheet lists refuses, and the row stays
    /// exactly the row the kick wrote. Each writer is driven the way production reaches it:
    /// <list type="bullet">
    /// <item>a party share: <c>AwardExp</c> under O's monitor, the body of <c>AwardKillExp</c>'s payout;</item>
    /// <item>a death: <c>Die()</c> under O's monitor, which a mob swing or a PvP hit reaches through
    /// <c>TakeDamage</c>, and which ends in <c>SaveChar</c>;</item>
    /// <item>the unconditional <c>StoreSave</c> (spellbook, legend and profile edits);</item>
    /// <item>the autosave sweep's call, <c>AutoSaveLoop.FlushIsolated</c>, on a dirty O;</item>
    /// <item>the shutdown flush, <c>AutoSaveLoop.SaveAll</c>, with O dirty and still on its map.</item>
    /// </list>
    /// Each writer really ran: the in-memory character moves (exp, the death's exp loss) while the row does not.
    ///
    /// <para>Falsified by deleting the <c>_replaced</c> check from <c>CaptureAndWrite</c> (see the #168
    /// report): red on the first writer, the party share, with the row's exp moved.</para></summary>
    [Fact]
    public void AReplacedSessionWritesNothingAfterTheKick()
    {
        const string name = "ReplacedWriters";
        // Level 3 on the Peasant path: below the level-5 wall, so AwardExp pays; and inside the exp table, so
        // the death has a band to charge.
        var (old, _, character) = _fx.PlayerWith(name, c =>
        {
            c.Level = 3;
            c.Totem = 4;
        }, WritersMap, 5, 5);

        try
        {
            old.KickForReplacement();
            string kicked = RowJson(name);

            // The party share: AwardExp ends in SaveChar.
            uint expBefore = character.Exp;
            old.WithState(() => old.AwardExp(50));
            Assert.True(character.Exp > expBefore, "the share must really have been paid in memory");
            Assert.Equal(kicked, RowJson(name));

            // A death: Die() applies the penalties and ends in SaveChar.
            uint expBeforeDeath = character.Exp;
            old.WithState(() => DieMethod.Invoke(old, null));
            Assert.True(character.Exp < expBeforeDeath, "the death must really have charged its exp in memory");
            Assert.Equal(kicked, RowJson(name));

            // The unconditional write.
            bool stored = false;
            old.WithState(() => stored = (bool)StoreSaveMethod.Invoke(old, null)!);
            Assert.True(stored, "a refused write is not a failed one");
            Assert.Equal(kicked, RowJson(name));

            // The autosave sweep's per-player call.
            old.WithState(old.MarkDirty);
            Assert.True(World.AutoSaveLoop.FlushIsolated(old, "autosave"));
            Assert.Equal(kicked, RowJson(name));

            // The shutdown flush walks Online.All(), which still has O on its map.
            old.WithState(old.MarkDirty);
            Assert.Contains(old, _fx.World.Online.All());
            _fx.World.AutoSave.SaveAll();
            Assert.Equal(kicked, RowJson(name));
        }
        finally
        {
            _fx.World.LeaveMap(old, WritersMap);
        }
    }

    /// <summary>R6 of the #168 sheet, the second of its two new late writers: the autosave sweep has CAPTURED O
    /// (which clears the dirty flag) but its write is still waiting for the database when the kick runs. A
    /// dirty-gated kick found nothing to do, the new login loaded the older row, and the sweep's write landed
    /// after it. The unconditional kick takes a newer sequence number and waits its turn at O's write gate, so
    /// the load sees O's latest state.
    ///
    /// <para>Forced, not hoped for. The database's write lock is held by hand (on this fact's own file, so
    /// nothing else is locked out), which parks the sweep's <c>SaveJson</c> inside O's write gate. The dirty
    /// flag going down is the proof the sweep has captured. The kick and the load then run on a thread of their
    /// own, and the lock is let go only once that thread has either finished (the dirty-gated kick, which
    /// waits for nothing) or blocked (the unconditional kick, on O's write gate).</para>
    ///
    /// <para>Falsified by putting the kick back on the dirty-gated <c>FlushNow()</c> (see the #168 report):
    /// red on the load, which carries the row as it was before the sweep.</para></summary>
    [Fact]
    public void AnAutosaveInFlightAtTheKickCannotLandAfterIt()
    {
        using var db = new IsolatedDatabase();
        const string name = "ReplacedInFlight";

        var character = new Character { SchemaVersion = Character.CurrentSchemaVersion, Name = name, Coins = 1 };
        var old = new Session(new RecordingOutbound($"recorder:{name}"), 2005, db.Store, _fx.World, character);
        Assert.True(db.Store.SaveMany(new[] { character }));   // R0: one coin

        old.WithState(() => { character.Coins = 2; old.MarkDirty(); });

        using var locked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var blocker = db.Open();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
            locked.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }) { IsBackground = true };

        Thread? sweep = null, arrival = null;
        try
        {
            holder.Start();
            Assert.True(locked.Wait(TimeSpan.FromSeconds(30)), "the blocking thread never took the write lock");

            bool swept = false;
            sweep = new Thread(() => swept = World.AutoSaveLoop.FlushIsolated(old, "autosave")) { IsBackground = true };
            sweep.Start();
            Assert.True(SpinWait.SpinUntil(() => old.DiagState().Contains("dirty False"), TimeSpan.FromSeconds(30)),
                        "the sweep never captured the session");

            CharacterLoadResult? loaded = null;
            arrival = new Thread(() =>
            {
                old.KickForReplacement();
                loaded = db.Store.Load(name);
            }) { IsBackground = true };
            arrival.Start();
            Assert.True(SpinWait.SpinUntil(() => !arrival.IsAlive || (arrival.ThreadState & ThreadState.WaitSleepJoin) != 0,
                                           TimeSpan.FromSeconds(30)), "the arrival neither finished nor blocked");

            release.Set();
            Assert.True(arrival.Join(TimeSpan.FromSeconds(30)), "the arrival never finished");
            Assert.True(sweep.Join(TimeSpan.FromSeconds(30)), "the sweep never finished");

            Assert.NotNull(loaded);
            Assert.Equal(CharacterLoadStatus.Ok, loaded!.Status);
            Assert.Equal(2u, Assert.IsType<Character>(loaded.Character).Coins);
            Assert.True(swept, "the sweep's own write should have landed too, or been superseded, not failed");
            Assert.Equal(2u, Assert.IsType<Character>(db.Store.Load(name).Character).Coins);
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(30));
            sweep?.Join(TimeSpan.FromSeconds(30));
            arrival?.Join(TimeSpan.FromSeconds(30));
        }
    }

    private Character LoadOk(string name)
    {
        var load = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, load.Status);
        return Assert.IsType<Character>(load.Character);
    }

    /// <summary>The stored row as JSON, re-serialized from what <c>Load</c> read: equal strings mean an equal
    /// row, which is the claim — not merely one field that happened not to move.</summary>
    private string RowJson(string name) => CharacterStore.Serialize(LoadOk(name));
}
