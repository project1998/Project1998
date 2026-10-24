using System.Diagnostics;
using Server;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>Regression guards for serializing reload writers and publishing every content table atomically.</summary>
[Collection("tile-translation")]
public class ContentReloadTests
{
    [Fact]
    public void LoadDoesNotReadFacadesOnTheLoadingThread()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();

            Assert.Equal(0, Content.LoadingThreadFacadeReadsForTests);
        }
    }

    [Fact]
    public async Task ConcurrentReloadReportsThatReloadIsAlreadyInProgress()
    {
        TestProcessState.LoadContent();
        var field = typeof(World).GetField("ReloadGate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var reloadGate = Assert.IsType<SemaphoreSlim>(field?.GetValue(null));
        // The constructor starts the live world's background machinery on this branch. The contended path
        // returns before touching instance state, so an uninitialized instance isolates the gate response.
        var world = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
        using var started = new ManualResetEventSlim();
        Task<(bool ok, string report)>? waiting = null;
        bool gateHeld = false;
        try
        {
            reloadGate.Wait();
            gateHeld = true;
            waiting = Task.Run(() => { started.Set(); return world.ReloadFromDisk(); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the concurrent reload task did not start");

            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.ok);
            Assert.Equal("reload already in progress", result.report);
        }
        finally
        {
            // If an assertion failed against an unbounded Wait, release it first; either way the task must be
            // finished before LoadContent restores process-wide registries for every later test.
            if (waiting is not null && !waiting.IsCompleted)
            {
                reloadGate.Release();
                gateHeld = false;
            }
            try { if (waiting is not null) await waiting.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                if (gateHeld) reloadGate.Release();
                TestProcessState.LoadContent();
            }
        }
    }

    [Fact]
    public void MalformedMobDropRowsAreSkippedWithoutAbortingTheLoad()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-mob-drops-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "MobDrops.csv");
            File.WriteAllText(path,
                "MobKey,Loot,RareLoot\n" +
                "valid_drop_row,GOLD:2:50,\n" +
                "bad_number,GOLD:not-an-integer:25,\n" +
                "bad_two_parts,apple:5,\n" +
                "bad_one_part,apple,\n" +
                "bad_four_parts,apple:1:2:3,\n" +
                "bad_negative_amount,apple:-5:50,\n" +
                "bad_negative_rate,apple:1:-50,\n" +
                "bad_infinite_rate,apple:1:Infinity,\n" +
                "bad_nan_rate,apple:1:NaN,\n" +
                "bad_rare_rate,,amber:Infinity\n" +
                "after_bad_row,,amber:12.5\n");

            var original = Content.OverridePathForTests(Content.TableId.MobDrops, path);
            try
            {
                TestProcessState.LoadContent();

                var valid = Assert.IsType<MobDropDef>(Content.MobDrops["valid_drop_row"]);
                Assert.Equal(2, Assert.Single(valid.Loot).MaxAmount);
                foreach (string badKey in new[]
                {
                    "bad_number", "bad_two_parts", "bad_one_part", "bad_four_parts",
                    "bad_negative_amount", "bad_negative_rate", "bad_infinite_rate", "bad_nan_rate",
                    "bad_rare_rate",
                })
                    Assert.False(Content.MobDrops.ContainsKey(badKey), $"{badKey} should have been skipped");
                Assert.Equal(12.5, Assert.Single(Content.MobDrops["after_bad_row"].Rare).RatePercent);
            }
            finally
            {
                Content.ReplaceSpecForTests(Content.TableId.MobDrops, original);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    /// <summary>#128: ParseSheet2 used to fill its dictionary in a for loop straight off an unvalidated
    /// <c>Count</c>, so one authored row with a huge Count was a denial-of-service shape (PR #124's review).
    /// A bogus <c>Count</c> of <c>int.MaxValue</c> must now be rejected before the loop ever starts — the
    /// whole content load (72 tables, not just this one) has to come back in ordinary time, keep nothing
    /// from the bad row, keep the valid rows on either side of it, and count the row as skipped.</summary>
    [Fact]
    public void BogusSheet2CountIsRejectedBeforeExpandingAndReturnsPromptly()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-sheet2-bogus-count-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "Tile533Map.csv");
            File.WriteAllText(path,
                "1,1,101\n" +               // valid, kept
                "5,2147483647,200\n" +      // bogus Count: would run 2^31 iterations unbounded
                "10,1,110\n");              // valid, kept — proves the loader carries on past the bad row

            var original = Content.OverridePathForTests(Content.TableId.Tile533Map, path);
            try
            {
                var clock = Stopwatch.StartNew();
                TestProcessState.LoadContent();
                clock.Stop();

                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
                    $"content load took {clock.Elapsed.TotalSeconds:F1}s — the bogus Count row was not bounded");

                Assert.Equal((ushort)101, TileTranslation.Sheet2ForTests[1]);
                Assert.Equal((ushort)110, TileTranslation.Sheet2ForTests[10]);
                // Nothing from the bogus row's run may have landed — spot-check the row's own start index and
                // a handful of indices the unbounded loop would have reached first.
                foreach (ushort probe in new ushort[] { 5, 6, 7, 100, 1000, ushort.MaxValue })
                    Assert.False(TileTranslation.Sheet2ForTests.ContainsKey(probe),
                        $"sheet-2 table kept an entry ({probe}) from the rejected row");

                var report = Content.LoadReport["Tile533Map.csv"];
                Assert.NotNull(report);
                Assert.Equal(3, report!.Read);
                Assert.Equal(2, report.Kept);
                Assert.Equal(1, report.Skipped);
            }
            finally
            {
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map, original);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    /// <summary>#128: the run's bound is the ushort domain itself (0..65535), checked with the arithmetic
    /// widened to long so it cannot silently wrap the way the ushort casts in the expansion loop used to.
    /// A run that lands exactly on the boundary is kept; one that overshoots it by a single index — the
    /// off-by-one this bug actually produced — is skipped whole, not truncated.</summary>
    [Fact]
    public void Sheet2RunOneOverTheUshortDomainIsSkippedButTheBoundaryRunIsKept()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-sheet2-wrap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "Tile533Map.csv");
            File.WriteAllText(path,
                "65534,2,0\n" +   // StartLegacy+Count-1 == 65535: exactly on the boundary, must be kept
                "65535,2,300\n"); // StartLegacy+Count-1 == 65536: one past the boundary, must be skipped whole

            var original = Content.OverridePathForTests(Content.TableId.Tile533Map, path);
            try
            {
                TestProcessState.LoadContent();

                Assert.Equal((ushort)0, TileTranslation.Sheet2ForTests[65534]);
                Assert.Equal((ushort)1, TileTranslation.Sheet2ForTests[65535]);
                // The rejected row must not have landed even partially — index 65535 keeps the boundary row's
                // value (1), not the wrapped row's (300), and the wrapped-to-zero index must be untouched.
                Assert.False(TileTranslation.Sheet2ForTests.ContainsKey(0));

                var report = Content.LoadReport["Tile533Map.csv"];
                Assert.NotNull(report);
                Assert.Equal(2, report!.Read);
                Assert.Equal(1, report.Kept);
                Assert.Equal(1, report.Skipped);
            }
            finally
            {
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map, original);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    // The duplicate tile throws after the era prepare but before Doors and Lua prepare, so this test's unique
    // job is guarding the era commit against a real mid-load data failure.
    [Fact]
    public void RealMidLoadFailureKeepsEraDoorsLuaAndSnapshot()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            object beforeSnapshot = Content.SnapshotIdentityForTests;
            int beforeEra = Shared.EraCalendar.RawDate;
            var beforeDoor = Doors.For(64000, 1, 1);
            bool beforeHook = MobScript.Has("content_reload_probe", MobScript.OnSpawn);

            string dir = Path.Combine(Path.GetTempPath(), "project1998-reload-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string caves = Path.Combine(dir, "MythicCaves.csv");
            File.WriteAllText(caves,
                "Animal,EntranceMap,EntranceTiles,DestMap,DestX,DestY,T1Level,T1Vita,T1Mana,T2Level,T2Vita,T2Mana,T3Level,T3Vita,T3Mana,Sources\n" +
                "Broken,41,1:1;1:1,201,1,1,1,0,0,1,0,0,1,0,0,test\n");
            string tuning = Path.Combine(dir, "ServerTuning.csv");
            File.WriteAllText(tuning, "key,value\nEraDate,19990102\n");
            string doors = Path.Combine(dir, "Doors.csv");
            File.WriteAllText(doors,
                "Map,X,Y,Locked,Key,ConsumeKey,ForceOpen,StartDx,ClosedObj,OpenObj,DefaultClosed,Sources\n" +
                "64000,1,1,1,probe_key,1,1,0,,,0,review-probe\n");
            string mobAi = Path.Combine(dir, "mob_ai.lua");
            File.WriteAllText(mobAi,
                "mobs = { content_reload_probe = { on_spawn = function(ctx) end } }\n");

            var previousCaves = Content.OverridePathForTests(Content.TableId.MythicCaves, caves);
            var previousTuning = Content.OverridePathForTests(Content.TableId.ServerTuning, tuning);
            var previousDoors = Content.OverridePathForTests(Content.TableId.Doors, doors);
            var previousMobAi = Content.OverridePathForTests(Content.TableId.MobAi, mobAi);
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => Content.Reload());

                Assert.Contains("Reload failed (previous content kept).", error.Message);
                Assert.Same(beforeSnapshot, Content.SnapshotIdentityForTests);
                Assert.Equal(beforeEra, Shared.EraCalendar.RawDate);
                Assert.Same(beforeDoor, Doors.For(64000, 1, 1));
                Assert.Equal(beforeHook, MobScript.Has("content_reload_probe", MobScript.OnSpawn));
            }
            finally
            {
                Content.ReplaceSpecForTests(Content.TableId.MythicCaves, previousCaves);
                Content.ReplaceSpecForTests(Content.TableId.ServerTuning, previousTuning);
                Content.ReplaceSpecForTests(Content.TableId.Doors, previousDoors);
                Content.ReplaceSpecForTests(Content.TableId.MobAi, previousMobAi);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void RealMidLoadFailureKeepsObjectFlagsAndTileTranslations()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-external-content-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string oldOverrides = Path.Combine(dir, "old-overrides.csv");
            string newOverrides = Path.Combine(dir, "new-overrides.csv");
            string oldObj533 = Path.Combine(dir, "old-obj533.csv");
            string newObj533 = Path.Combine(dir, "new-obj533.csv");
            string oldSheet2 = Path.Combine(dir, "old-sheet2.csv");
            string newSheet2 = Path.Combine(dir, "new-sheet2.csv");
            string caves = Path.Combine(dir, "MythicCaves.csv");
            File.WriteAllText(oldOverrides, "60000,0x01,old\n");
            File.WriteAllText(newOverrides, "60001,0x02,new\n");
            File.WriteAllText(oldObj533, "60000,suppress,0,0,0,0,structural\n");
            File.WriteAllText(newObj533, "60001,suppress,0,0,0,0,structural\n");
            File.WriteAllText(oldSheet2, "60000,1,61000\n");
            File.WriteAllText(newSheet2, "60001,1,61001\n");
            File.WriteAllText(caves,
                "Animal,EntranceMap,EntranceTiles,DestMap,DestX,DestY,T1Level,T1Vita,T1Mana,T2Level,T2Vita,T2Mana,T3Level,T3Vita,T3Mana,Sources\n" +
                "Broken,41,1:1;1:1,201,1,1,1,0,0,1,0,0,1,0,0,test\n");

            var previousOverrides = Content.Spec(Content.TableId.ObjectFlagOverrides);
            var previousObj533 = Content.Spec(Content.TableId.Obj533Fix);
            var previousSheet2 = Content.Spec(Content.TableId.Tile533Map);
            var previousCaves = Content.Spec(Content.TableId.MythicCaves);
            try
            {
                Content.ReplaceSpecForTests(Content.TableId.ObjectFlagOverrides,
                    previousOverrides with { PathOverride = oldOverrides });
                Content.ReplaceSpecForTests(Content.TableId.Obj533Fix,
                    previousObj533 with { PathOverride = oldObj533 });
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map,
                    previousSheet2 with { PathOverride = oldSheet2 });
                TestProcessState.LoadContent();

                Assert.Equal((60000, (byte)1), Assert.Single(ObjectFlags.OverridesForTests));
                Assert.Equal((ushort)60000, Assert.Single(TileTranslation.Obj533ForTests).Legacy);
                Assert.Equal((ushort)61000, TileTranslation.Sheet2ForTests[60000]);

                Content.ReplaceSpecForTests(Content.TableId.ObjectFlagOverrides,
                    previousOverrides with { PathOverride = newOverrides });
                Content.ReplaceSpecForTests(Content.TableId.Obj533Fix,
                    previousObj533 with { PathOverride = newObj533 });
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map,
                    previousSheet2 with { PathOverride = newSheet2 });
                Content.ReplaceSpecForTests(Content.TableId.MythicCaves,
                    previousCaves with { PathOverride = caves });

                var error = Assert.Throws<InvalidOperationException>(() => Content.Reload());

                Assert.Contains("Reload failed (previous content kept).", error.Message);
                Assert.Equal((60000, (byte)1), Assert.Single(ObjectFlags.OverridesForTests));
                Assert.Equal((ushort)60000, Assert.Single(TileTranslation.Obj533ForTests).Legacy);
                Assert.Equal((ushort)61000, TileTranslation.Sheet2ForTests[60000]);
                Assert.False(TileTranslation.Sheet2ForTests.ContainsKey(60001));
            }
            finally
            {
                Content.ReplaceSpecForTests(Content.TableId.ObjectFlagOverrides, previousOverrides);
                Content.ReplaceSpecForTests(Content.TableId.Obj533Fix, previousObj533);
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map, previousSheet2);
                Content.ReplaceSpecForTests(Content.TableId.MythicCaves, previousCaves);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void SuccessfulReloadPublishesObjectFlagsAndTileTranslations()
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-external-content-success-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string oldOverrides = Path.Combine(dir, "old-overrides.csv");
            string newOverrides = Path.Combine(dir, "new-overrides.csv");
            string oldObj533 = Path.Combine(dir, "old-obj533.csv");
            string newObj533 = Path.Combine(dir, "new-obj533.csv");
            string oldSheet2 = Path.Combine(dir, "old-sheet2.csv");
            string newSheet2 = Path.Combine(dir, "new-sheet2.csv");
            File.WriteAllText(oldOverrides, "60000,0x01,old\n");
            File.WriteAllText(newOverrides, "60001,0x02,new\n");
            File.WriteAllText(oldObj533, "60000,suppress,0,0,0,0,structural\n");
            File.WriteAllText(newObj533, "60001,suppress,0,0,0,0,structural\n");
            File.WriteAllText(oldSheet2, "60000,1,61000\n");
            File.WriteAllText(newSheet2, "60001,1,61001\n");

            var previousOverrides = Content.Spec(Content.TableId.ObjectFlagOverrides);
            var previousObj533 = Content.Spec(Content.TableId.Obj533Fix);
            var previousSheet2 = Content.Spec(Content.TableId.Tile533Map);
            try
            {
                Content.ReplaceSpecForTests(Content.TableId.ObjectFlagOverrides,
                    previousOverrides with { PathOverride = oldOverrides });
                Content.ReplaceSpecForTests(Content.TableId.Obj533Fix,
                    previousObj533 with { PathOverride = oldObj533 });
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map,
                    previousSheet2 with { PathOverride = oldSheet2 });
                TestProcessState.LoadContent();

                Content.ReplaceSpecForTests(Content.TableId.ObjectFlagOverrides,
                    previousOverrides with { PathOverride = newOverrides });
                Content.ReplaceSpecForTests(Content.TableId.Obj533Fix,
                    previousObj533 with { PathOverride = newObj533 });
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map,
                    previousSheet2 with { PathOverride = newSheet2 });
                Content.Reload();

                Assert.Equal((60001, (byte)2), Assert.Single(ObjectFlags.OverridesForTests));
                Assert.Equal((ushort)60001, Assert.Single(TileTranslation.Obj533ForTests).Legacy);
                Assert.Equal((ushort)61001, TileTranslation.Sheet2ForTests[60001]);
                Assert.False(TileTranslation.Sheet2ForTests.ContainsKey(60000));
            }
            finally
            {
                Content.ReplaceSpecForTests(Content.TableId.ObjectFlagOverrides, previousOverrides);
                Content.ReplaceSpecForTests(Content.TableId.Obj533Fix, previousObj533);
                Content.ReplaceSpecForTests(Content.TableId.Tile533Map, previousSheet2);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    [Fact]
    public void ReaderNeverSeesItemsWithoutMatchingIndexDuringReload()
    {
        const int reloadCount = 10;
        const int readsPerReload = 10_000;

        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            var firstItems = Content.Items;
            using var itemsLoaded = new SemaphoreSlim(0);
            using var resumeLoad = new SemaphoreSlim(0);
            Thread? reloadThread = null;
            Exception? reloadFailure = null;
            var progress = new StallWatch.RoundCounter();
            int mismatches = 0;

            try
            {
                Content.LoadStepForTests = step =>
                {
                    if (step != "ItemsLoaded") return;
                    itemsLoaded.Release();
                    if (!resumeLoad.Wait(TimeSpan.FromSeconds(15)))
                        throw new TimeoutException("reader did not release the paused content load");
                };

                reloadThread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < reloadCount; i++)
                        {
                            Content.Reload();
                            progress.Bump();
                        }
                    }
                    catch (Exception e)
                    {
                        reloadFailure = e;
                    }
                });
                reloadThread.Start();

                for (int reload = 0; reload < reloadCount; reload++)
                {
                    Assert.True(itemsLoaded.Wait(TimeSpan.FromSeconds(15)), $"reload {reload + 1} did not reach ItemsLoaded");
                    for (int read = 0; read < readsPerReload; read++)
                    {
                        var items = Content.Items;
                        var known = items[0];
                        if (!ReferenceEquals(known, Content.ItemById(known.Id))) mismatches++;
                    }
                    resumeLoad.Release();
                }

                StallWatch.RunUntilDoneOrStalled(new[] { reloadThread }, () => progress.Rounds,
                    StallWatch.StallQuiet, StallWatch.StallCap, "the reload thread");
                Assert.Null(reloadFailure);
                Assert.Equal(0, mismatches);
                Assert.NotSame(firstItems, Content.Items); // proves the final snapshot write was not removed
            }
            finally
            {
                Content.LoadStepForTests = null;
                for (int i = 0; i < reloadCount; i++) resumeLoad.Release();
                if (reloadThread is { IsAlive: true })
                    StallWatch.RunUntilDoneOrStalled(new[] { reloadThread }, () => progress.Rounds,
                        StallWatch.StallQuiet, StallWatch.StallCap, "the reload thread during cleanup");
                TestProcessState.LoadContent();
            }
        }
    }

    // BeforePublish is the widest failure window, after every candidate is prepared; this is the test that
    // guards the Doors and all four Lua commits as well as the snapshot and era.
    [Fact]
    public void FailedReloadKeepsEveryPublishedFacade()
    {
        lock (TestProcessState.Gate)
        {
            TestProcessState.LoadContent();
            object beforeSnapshot = Content.SnapshotIdentityForTests;
            var before = SnapshotBackedFacades();
            Assert.Equal(89, before.Count);
            Assert.Equal(64, before.Values.Count(entry => entry.Property.GetMethod!.IsPublic));
            int beforeEra = Shared.EraCalendar.RawDate;
            var beforeDoor = Doors.For(64000, 1, 1);
            bool beforeHook = MobScript.Has("content_reload_probe", MobScript.OnSpawn);

            string dir = Path.Combine(Path.GetTempPath(), "project1998-before-publish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string tuning = Path.Combine(dir, "ServerTuning.csv");
            File.WriteAllText(tuning, "key,value\nEraDate,19990103\n");
            string doors = Path.Combine(dir, "Doors.csv");
            File.WriteAllText(doors,
                "Map,X,Y,Locked,Key,ConsumeKey,ForceOpen,StartDx,ClosedObj,OpenObj,DefaultClosed,Sources\n" +
                "64000,1,1,1,probe_key,1,1,0,,,0,review-probe\n");
            string mobAi = Path.Combine(dir, "mob_ai.lua");
            File.WriteAllText(mobAi,
                "mobs = { content_reload_probe = { on_spawn = function(ctx) end } }\n");

            var previousTuning = Content.Spec(Content.TableId.ServerTuning);
            var previousDoors = Content.Spec(Content.TableId.Doors);
            var previousMobAi = Content.Spec(Content.TableId.MobAi);

            try
            {
                Content.ReplaceSpecForTests(Content.TableId.ServerTuning,
                    previousTuning with { PathOverride = tuning });
                Content.ReplaceSpecForTests(Content.TableId.Doors,
                    previousDoors with { PathOverride = doors });
                Content.ReplaceSpecForTests(Content.TableId.MobAi,
                    previousMobAi with { PathOverride = mobAi });
                Content.LoadStepForTests = step =>
                {
                    if (step == "BeforePublish") throw new InvalidOperationException("injected loader failure");
                };

                var error = Assert.Throws<InvalidOperationException>(() => Content.Reload());

                Assert.Contains("Reload failed (previous content kept).", error.Message);
                Assert.Same(beforeSnapshot, Content.SnapshotIdentityForTests);
                Assert.Equal(89, Content.SnapshotMemberCountForTests);
                Assert.Equal(beforeEra, Shared.EraCalendar.RawDate);
                Assert.Same(beforeDoor, Doors.For(64000, 1, 1));
                Assert.Equal(beforeHook, MobScript.Has("content_reload_probe", MobScript.OnSpawn));

                var after = SnapshotBackedFacades();
                Assert.Equal(before.Keys, after.Keys);
                foreach (string name in before.Keys)
                {
                    if (before[name].Property.PropertyType.IsValueType)
                        Assert.Equal(before[name].Value, after[name].Value);
                    else
                    {
                        Assert.True(ReferenceEquals(before[name].Value, after[name].Value),
                            $"ContentSnapshot member '{name}' facade '{before[name].Property.Name}' changed identity.");
                        Assert.Same(before[name].Value, after[name].Value);
                    }
                }
            }
            finally
            {
                Content.LoadStepForTests = null;
                Content.ReplaceSpecForTests(Content.TableId.ServerTuning, previousTuning);
                Content.ReplaceSpecForTests(Content.TableId.Doors, previousDoors);
                Content.ReplaceSpecForTests(Content.TableId.MobAi, previousMobAi);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a test fixture */ }
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string> SnapshotFacadeNames =
        new Dictionary<string, string>
        {
            ["ItemById"] = "ItemByIdIndex",
            ["ItemByKey"] = "ItemByKeyIndex",
            ["MobById"] = "MobByIdIndex",
            ["MobByKey"] = "MobByKeyIndex",
            ["NpcById"] = "NpcByIdIndex",
            ["PathIdByName"] = "PathIdByNameIndex",
            ["PathRankByName"] = "PathRankByNameIndex",
            ["SpellById"] = "SpellByIdIndex",
            ["SpellByKey"] = "SpellByKeyIndex",
        };

    private static SortedDictionary<string, (System.Reflection.PropertyInfo Property, object? Value)>
        SnapshotBackedFacades()
    {
        const System.Reflection.BindingFlags snapshotFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        const System.Reflection.BindingFlags facadeFlags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static;

        var snapshotMembers = Content.SnapshotIdentityForTests.GetType().GetProperties(snapshotFlags);
        var snapshotMemberNames = snapshotMembers.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var facades = typeof(Content).GetProperties(facadeFlags)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var (memberName, facadeName) in SnapshotFacadeNames)
        {
            Assert.True(snapshotMemberNames.Contains(memberName),
                $"Facade '{facadeName}' has no ContentSnapshot member '{memberName}'.");
            Assert.True(facades.ContainsKey(facadeName),
                $"ContentSnapshot member '{memberName}' has no facade '{facadeName}'.");
        }

        var facadeMembers = SnapshotFacadeNames.ToDictionary(pair => pair.Value, pair => pair.Key,
            StringComparer.Ordinal);
        foreach (var facade in facades.Values.Where(p =>
                     p.GetMethod!.IsPublic && p.GetSetMethod(nonPublic: true)?.IsPrivate == true))
        {
            string memberName = facadeMembers.GetValueOrDefault(facade.Name, facade.Name);
            Assert.True(snapshotMemberNames.Contains(memberName),
                $"Facade '{facade.Name}' has no ContentSnapshot member '{memberName}'.");
        }

        var result = new SortedDictionary<string, (System.Reflection.PropertyInfo, object?)>(StringComparer.Ordinal);
        foreach (var member in snapshotMembers)
        {
            string facadeName = SnapshotFacadeNames.GetValueOrDefault(member.Name, member.Name);
            Assert.True(facades.TryGetValue(facadeName, out var facade),
                $"ContentSnapshot member '{member.Name}' has no facade '{facadeName}'.");
            result.Add(member.Name, (facade, facade.GetValue(null)));
        }

        return result;
    }
}
