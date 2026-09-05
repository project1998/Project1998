using System.Collections.Generic;
using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Warrior's Nagnang Shield quest (see <see cref="NagnangShieldQuest"/>): two NPC abilities, two tile
/// triggers, and 85 Warps.csv rows that chain five parallel five-room caves. Nothing here fails a build and
/// almost nothing here throws — every way it breaks looks, in game, like a player who "can't get in" or a
/// cave that "goes nowhere".
///
/// <para>The specific regressions these pin, in walk order: Sword or Chul losing their composition row (the
/// menu entry and the spoken word just stop existing); the pelt drop or the pelt item vanishing, which
/// strands the quest at step one; the cave mouth's ground or object layer changing so the trigger tile can't
/// be stood on; a level band that leaves a level with no cave; a warp row landing on solid ground or on the
/// mouth row; the room chain breaking in the middle so the Objective is unreachable; the statue moving out
/// of the ring the altar answers from; the trial's creatures appearing in the first or last room, which
/// would make the run unwinnable; and either shield's bonding flipping.</para>
/// </summary>
public class NagnangShieldQuestTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate) { if (_loaded) return; TestProcessState.LoadContent(); _loaded = true; }
    }

    /// <summary>Every room of every tier: entrance, three fighting rooms, Objective.</summary>
    private static IEnumerable<ushort> Rooms(ushort entrance)
    {
        for (ushort id = entrance; id <= entrance + 4; id++) yield return id;
    }

    /// <summary>Can a player stand here? Solid ground and an all-directions SObj wall both say no, and only
    /// the first of the two is obvious from a map render.</summary>
    private static bool Steppable(MapData m, int x, int y) =>
        !m.Solid(x, y) && !ObjectFlags.Blocks(m.Obj(x, y), 0);

    /// <summary>Both halves of the quest are reachable ONLY through a composition row. Sword's menu entry and
    /// Chul's spoken "shield" have no fallback — drop either row and the quest is silently unplayable.</summary>
    [Fact]
    public void SwordAndChulCarryTheirAbilities()
    {
        EnsureLoaded();

        var sword = Content.Npcs.FirstOrDefault(n => n.Id == NagnangShieldQuest.SwordNpcId);
        Assert.NotNull(sword);
        Assert.Equal("WarriorTrainerNpc", sword!.Key);
        Assert.Equal("Sword", sword.Name);
        Assert.Equal(2510, sword.Map);                       // "Warrior Sword", the Nagnang guild hall
        Assert.Contains(NpcScripts.For(sword), a => a is NagnangShieldAbility);

        var chul = Content.Npcs.FirstOrDefault(n => n.Id == NagnangShieldQuest.ChulNpcId);
        Assert.NotNull(chul);
        Assert.Equal("SmithNpc", chul!.Key);
        Assert.Equal("Chul", chul.Name);
        Assert.Equal(2518, chul.Map);                        // "Chul Smith"
        var chulAbilities = NpcScripts.For(chul);
        Assert.Contains(chulAbilities, a => a is NagnangTallShieldAbility);
        // and reachable by EAR specifically — the say dispatcher only considers INpcSayHandler.
        Assert.Contains(chulAbilities.OfType<INpcSayHandler>(), h => h is NagnangTallShieldAbility);
    }

    /// <summary>The one token that starts the quest. Green squirrels are the only source of the pelt, and
    /// they live behind Nagnang's southern warp — if either the drop or that warp goes, step one is
    /// impossible with nothing to say so.</summary>
    [Fact]
    public void GreenSquirrelsDropThePeltAndAreReachableFromNagnang()
    {
        EnsureLoaded();

        Assert.NotNull(Content.ItemByKey(NagnangShieldQuest.Pelt));

        var squirrel = Content.MobByKey("green_squirrel");
        Assert.NotNull(squirrel);
        Assert.True(Content.MobDrops.TryGetValue("green_squirrel", out var drops), "green_squirrel has no drop table");
        Assert.Contains(drops!.Loot, l => l.ItemKey == NagnangShieldQuest.Pelt);

        // Southern Path (2535) is where they are, and Nagnang's south warp is the way in. Atlas: "(134, 154)".
        Assert.Contains(Content.AreaSpawns, s => s.MobId == squirrel!.Id && s.Map == 2535);
        Assert.Contains(Content.Warps, w => w.Key.m == NagnangShieldQuest.NagnangMap && w.Value.m == 2535);
    }

    /// <summary>The cave mouth has to be STEPPABLE or the trigger never runs and the quest dead-ends without
    /// a word. It is the dead end of a two-tile alcove, so the alcove's walls are load-bearing too: if they
    /// stop blocking, the trigger stops being the only way in.</summary>
    [Fact]
    public void CaveMouthIsWalkableAndWalledIn()
    {
        EnsureLoaded();

        var nagnang = MapData.For(NagnangShieldQuest.NagnangMap);
        Assert.NotNull(nagnang);

        foreach (int x in NagnangShieldQuest.MouthX)
        {
            Assert.True(Steppable(nagnang!, x, NagnangShieldQuest.MouthY),
                        $"cave mouth ({x},{NagnangShieldQuest.MouthY}) cannot be stepped on");
            Assert.True(Steppable(nagnang!, x, NagnangShieldQuest.MouthPushToY),
                        $"refusal push-back tile ({x},{NagnangShieldQuest.MouthPushToY}) cannot be stepped on");
            Assert.True(Steppable(nagnang!, x, NagnangShieldQuest.MouthExitY),
                        $"cave exit tile ({x},{NagnangShieldQuest.MouthExitY}) cannot be stepped on");
        }

        // The alcove is a two-tile gap in a wall run; the flanks are what make the trigger the only door.
        foreach (int x in new[] { NagnangShieldQuest.MouthX[0] - 1, NagnangShieldQuest.MouthX[1] + 1 })
            Assert.False(Steppable(nagnang!, x, NagnangShieldQuest.MouthY),
                         $"the wall beside the cave mouth at ({x},{NagnangShieldQuest.MouthY}) no longer blocks");

        // Walking out must not land on the trigger row, or leaving would bounce you straight back in.
        Assert.NotEqual(NagnangShieldQuest.MouthY, NagnangShieldQuest.MouthExitY);
        Assert.NotEqual(NagnangShieldQuest.MouthY, NagnangShieldQuest.MouthPushToY);
    }

    /// <summary>The level ladder. A gap between two bands is not a load error — it is a level range for which
    /// the mouth silently refuses everyone, and the quest's own floor is the Atlas's stated level 10.</summary>
    [Fact]
    public void EveryLevelFromTenGetsExactlyOneCave()
    {
        EnsureLoaded();

        Assert.Equal(0, NagnangShieldQuest.EntranceFor(NagnangShieldQuest.MinLevel - 1));
        for (int level = NagnangShieldQuest.MinLevel; level <= 99; level++)
        {
            ushort entrance = NagnangShieldQuest.EntranceFor(level);
            Assert.Contains(entrance, NagnangShieldQuest.Tiers.Select(t => t.Map));
        }

        // RTK's bands, at their edges (10-24 / 25-39 / 40-74 / 75-98 / 99+).
        Assert.Equal(2545, NagnangShieldQuest.EntranceFor(10));
        Assert.Equal(2545, NagnangShieldQuest.EntranceFor(24));
        Assert.Equal(2550, NagnangShieldQuest.EntranceFor(25));
        Assert.Equal(2550, NagnangShieldQuest.EntranceFor(39));
        Assert.Equal(2555, NagnangShieldQuest.EntranceFor(40));
        Assert.Equal(2555, NagnangShieldQuest.EntranceFor(74));
        Assert.Equal(2560, NagnangShieldQuest.EntranceFor(75));
        Assert.Equal(2560, NagnangShieldQuest.EntranceFor(98));
        Assert.Equal(2565, NagnangShieldQuest.EntranceFor(99));
    }

    /// <summary>Every tier is five real rooms with terrain, and the landing tile is stand-able. A tier map id
    /// with no terrain behind it strands whoever the ladder sends there, and nothing says so until then.</summary>
    [Fact]
    public void EveryTierIsFiveRoomsWithTerrain()
    {
        EnsureLoaded();

        Assert.Equal(5, NagnangShieldQuest.Tiers.Length);
        foreach (var (_, entrance) in NagnangShieldQuest.Tiers)
        {
            foreach (ushort id in Rooms(entrance))
            {
                Assert.True(Content.Maps.ContainsKey(id), $"Gauntlet room {id} is missing from Maps.csv");
                Assert.NotNull(MapData.For(id));
            }
            Assert.Equal("Gauntlet Entrance", Content.Maps[entrance].Name);
            Assert.Equal("Objective", Content.Maps[(ushort)(entrance + 4)].Name);
            Assert.True(NagnangShieldQuest.IsObjective(entrance + 4));

            var first = MapData.For(entrance)!;
            foreach (int x in new[] { NagnangShieldQuest.LandX0, NagnangShieldQuest.LandX1 })
                Assert.True(Steppable(first, x, NagnangShieldQuest.LandY),
                            $"landing tile ({x},{NagnangShieldQuest.LandY}) on map {entrance} cannot be stepped on");
        }
    }

    /// <summary>The chain itself. 85 warp rows joining five caves is exactly the kind of table where one
    /// transposed number leaves a room that only goes backwards — which reads in game as a dead end, never as
    /// an error. Every endpoint must be stand-able, and the Objective must be reachable from the entrance.</summary>
    [Fact]
    public void EveryTierChainsFromItsEntranceToItsObjective()
    {
        EnsureLoaded();

        foreach (var (_, entrance) in NagnangShieldQuest.Tiers)
        {
            var rooms = Rooms(entrance).ToHashSet();
            var inside = Content.Warps.Where(w => rooms.Contains(w.Key.m)).ToList();
            Assert.NotEmpty(inside);

            foreach (var w in inside)
            {
                var from = MapData.For(w.Key.m)!;
                Assert.True(Steppable(from, w.Key.x, w.Key.y),
                            $"warp source ({w.Key.x},{w.Key.y}) on map {w.Key.m} cannot be stepped on");
                var to = MapData.For(w.Value.m);
                Assert.NotNull(to);
                Assert.True(Steppable(to!, w.Value.x, w.Value.y),
                            $"warp lands on ({w.Value.x},{w.Value.y}) of map {w.Value.m}, which cannot be stood on");
            }

            // Forward reachability, entrance -> Objective, walking only warps that stay inside this tier.
            var seen = new HashSet<ushort> { entrance };
            var queue = new Queue<ushort>(seen);
            while (queue.Count > 0)
            {
                ushort here = queue.Dequeue();
                foreach (var w in inside.Where(w => w.Key.m == here && rooms.Contains(w.Value.m)))
                    if (seen.Add(w.Value.m)) queue.Enqueue(w.Value.m);
            }
            Assert.Equal(rooms, seen);

            // And the way out, back to Nagnang's cave mouth row — not onto the trigger tile itself.
            var outs = inside.Where(w => w.Value.m == NagnangShieldQuest.NagnangMap).ToList();
            Assert.NotEmpty(outs);
            foreach (var w in outs)
            {
                Assert.Equal(entrance, w.Key.m);
                Assert.Equal(NagnangShieldQuest.MouthExitY, w.Value.y);
                Assert.Contains((int)w.Value.x, NagnangShieldQuest.MouthX);
            }
        }
    }

    /// <summary>The statue, and the ring the altar answers from. The trigger is positional with no click, so
    /// if the statue moves the reward becomes unreachable — or, worse, the ring stops containing it and the
    /// altar fires from across the room.</summary>
    [Fact]
    public void TheStatueStandsInsideTheAltarRingInEveryTier()
    {
        EnsureLoaded();

        foreach (var (_, entrance) in NagnangShieldQuest.Tiers)
        {
            ushort id = (ushort)(entrance + 4);
            var objective = MapData.For(id)!;

            // Three solid pieces, identical in all five copies — that is the statue.
            var statue = new[] { (7, 3), (8, 3), (9, 3) };
            foreach (var (x, y) in statue)
            {
                Assert.False(Steppable(objective, x, y), $"statue tile ({x},{y}) on map {id} is no longer solid");
                Assert.True(NagnangShieldQuest.AtAltar(x, y), $"the altar ring no longer contains the statue tile ({x},{y})");
            }

            // The ring has to hold somewhere a player can actually stand, or nothing ever touches it.
            var standable = new List<(int, int)>();
            for (int y = 0; y < objective.Ys; y++)
                for (int x = 0; x < objective.Xs; x++)
                    if (NagnangShieldQuest.AtAltar(x, y) && Steppable(objective, x, y)) standable.Add((x, y));
            Assert.NotEmpty(standable);

            // …and it must stay a RING: nothing far from the statue may answer for it.
            foreach (var (x, y) in standable)
                Assert.True(System.Math.Abs(y - 3) <= 1 && x >= 6 && x <= 10,
                            $"altar tile ({x},{y}) on map {id} is not adjacent to the statue");
        }
    }

    /// <summary>Where the trial's creatures may and may not be. The Atlas is explicit that the first and last
    /// rooms are empty — a forbidden creature spawning in the entrance would fail runs before they start, and
    /// one in the Objective would fail them at the statue.</summary>
    [Fact]
    public void ForbiddenCreaturesLiveInTheMiddleRoomsOnly()
    {
        EnsureLoaded();

        var forbidden = NagnangShieldQuest.Forbidden
            .Select(k => { var m = Content.MobByKey(k); Assert.NotNull(m); return m!.Id; })
            .ToHashSet();

        foreach (var (_, entrance) in NagnangShieldQuest.Tiers)
        {
            foreach (ushort empty in new[] { entrance, (ushort)(entrance + 4) })
                Assert.DoesNotContain(Content.AreaSpawns, s => s.Map == empty && forbidden.Contains(s.MobId));

            for (ushort id = (ushort)(entrance + 1); id <= entrance + 3; id++)
            {
                ushort room = id;
                Assert.Contains(Content.AreaSpawns, s => s.Map == room && forbidden.Contains(s.MobId));
            }
        }
    }

    /// <summary>The two shields, and the one difference between them the Atlas states outright: the trial's
    /// Nagnang shield is non-bonded, Chul's Tall shield is bonded. Bonding is a list in code, not a CSV
    /// column, so a wrong answer here is invisible until a player tries to trade one.</summary>
    [Fact]
    public void TheTrialShieldIsLooseAndTheSmithsIsBound()
    {
        EnsureLoaded();

        var trial = Content.ItemByKey(NagnangShieldQuest.Shield);
        Assert.NotNull(trial);
        Assert.False(trial!.Bonded, "the Nagnang shield is the NON-bonded one (Atlas)");

        var tall = Content.ItemByKey("tall_shield");
        Assert.NotNull(tall);
        Assert.True(tall!.Bonded, "the Tall shield is the bonded one (Atlas)");

        // Chul's materials have to exist, or the forge silently never completes.
        Assert.NotNull(Content.ItemByKey("ginko_wood"));
        Assert.NotNull(Content.ItemByKey("metal"));
    }
}
