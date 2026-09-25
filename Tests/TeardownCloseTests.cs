using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #168 item 3c: the read loop's exit still closes the connection, and says the save was lost, when the
/// teardown's final save throws.
///
/// <para><b>Before.</b> <c>RunAsync</c>'s <c>finally</c> ran <c>WithState(TearDownWorldState)</c> and then
/// <c>CloseConnection</c>. The teardown's final <c>FlushNow</c> was not fenced, and a capture that throws (the
/// PR #282 review traced the route: a value the serializer rejects, here NaN karma) left the <c>finally</c>
/// straight away. No <c>CloseConnection</c>, no writer await, no CLOSE line, and the log said "faulted during
/// teardown" rather than that the save was lost. The session had already left its map, so no sweep retried
/// it.</para>
///
/// <para><b>After.</b> The flush is fenced inside the teardown and logs "save LOST", and the close, the writer
/// await and the CLOSE line sit in a <c>finally</c> of their own around the teardown
/// (<c>Session.EndReadLoopAsync</c>, which <c>RunAsync</c>'s <c>finally</c> now awaits), so they run whatever
/// the teardown throws.</para>
/// </summary>
[Collection("world")]
public class TeardownCloseTests
{
    private const ushort CloseMap = 61720;   // content-free; no other class stands here

    private readonly SessionFixture _fx;

    public TeardownCloseTests(SessionFixture fx) => _fx = fx;

    /// <summary>A character that cannot be serialized, torn down through the read loop's real exit.
    ///
    /// <para>Falsified twice (see the #168 report). The fence removed (its <c>catch</c> made unreachable): red
    /// on the no-exception assertion, with the connection still closed and the CLOSE line still logged — the
    /// new <c>finally</c> carrying the close on its own. The fence and the <c>finally</c> both removed: red on
    /// <c>Closed</c>, which is master's behaviour.</para></summary>
    [Fact]
    public async Task AThrowingFinalSaveStillClosesTheConnectionAndLogsTheLoss()
    {
        const string name = "TeardownThrower";
        var (session, outbound, character) = _fx.PlayerWith(name, _ => { }, CloseMap, 5, 5);

        try
        {
            session.WithState(() =>
            {
                character.Karma = double.NaN;   // System.Text.Json cannot write NaN: Serialize throws
                session.MarkDirty();
            });

            Exception? error;
            using (var sink = LogLineSink.Acquire())
            {
                error = await Record.ExceptionAsync(() => session.EndReadLoopAsync(Task.CompletedTask));

                Assert.True(outbound.Closed, "the connection was left open after the teardown");
                sink.LineContaining($"-- CLOSE recorder:{name}");
                Assert.Null(error);
                var lost = sink.EntryContaining($"disconnect save of '{name}' threw — save LOST");
                Assert.Equal(LogLevel.Error, lost.Level);
                Assert.False(sink.Has($"persisted '{name}'"), "the persisted line was logged for a save that threw");
            }

            Assert.NotEqual(CharacterLoadStatus.Ok, _fx.Store.Load(name).Status);   // nothing was written
        }
        finally
        {
            character.Karma = 0;
            _fx.World.LeaveMap(session, CloseMap);
        }
    }
}
