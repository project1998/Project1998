using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Poet's whip chain (see <see cref="PoetWhipQuest"/>) is five steps stitched together out of things that
/// already existed separately — four Items.csv keys, two mobs.csv keys, one CSV composition row, a shop stock
/// list, a courtyard tile on Nagnang and the eight Warps.csv rows that make up Path of Choice. Every one of
/// those joins fails SILENTLY: a renamed item key reads as "the player doesn't have it", a dropped ability row
/// reads as "Staff has nothing to say", a broken warp reads as a dead end, and none of them breaks a build or
/// logs a thing. These pin the joins, and the two places where the port deliberately departs from RTK.
/// </summary>
public class PoetWhipQuestTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            TestProcessState.LoadContent();
            _loaded = true;
        }
    }

    /// <summary>The composition row IS the quest's reachability: without it Staff still stands there with the
    /// ordinary trainer menu and "Welcome Stranger" simply never appears, with nothing anywhere to say why.
    /// Nine NPCs share the PoetTrainerNpc identifier the row is keyed by, so the in-code narrowing to Staff's
    /// own id is load-bearing too — this pins both halves.
    ///
    /// <para>It also pins that Staff EXISTS, which he did not: on his old map (3832, RTK-only, no client
    /// terrain) <c>Content.LoadNpcs</c> dropped him without a word and this assert is the thing that caught
    /// it. See <see cref="PoetWhipQuest.StaffNpcId"/>.</para></summary>
    [Fact]
    public void StaffCarriesTheQuestAndTheOtherPoetTrainersDoNot()
    {
        EnsureLoaded();

        var staff = Content.Npcs.FirstOrDefault(n => n.Id == PoetWhipQuest.StaffNpcId);
        Assert.NotNull(staff);
        Assert.Equal("PoetTrainerNpc", staff!.Key);
        Assert.Equal("Staff", staff.Name);
        Assert.Equal(PoetWhipQuest.StaffMap, staff.Map);
        Assert.Contains(NpcScripts.For(staff), a => a is PoetWhipQuestAbility);

        // Every other poet trainer carries the ABILITY (the row is per identifier) but the entry is gated on
        // the npc id, so only Staff can ever offer it. If a second NPC takes id 137 this stops being true.
        Assert.Single(Content.Npcs, n => n.Id == PoetWhipQuest.StaffNpcId);
        Assert.True(Content.Npcs.Count(n => n.Key == "PoetTrainerNpc") > 1,
                    "the identifier is no longer shared — the in-code Staff gate may now be dead weight");
    }

    /// <summary>The four items the chain moves between hands. A rename anywhere here is invisible: the pipe
    /// stops being takeable, the branch stops being findable, the water stops being droppable, the whip stops
    /// being granted — and every one of those looks like "the player just doesn't have it".</summary>
    [Fact]
    public void EveryQuestItemStillExists()
    {
        EnsureLoaded();

        foreach (string key in new[] { PoetWhipQuest.Pipe, PoetWhipQuest.Branch, PoetWhipQuest.Water, PoetWhipQuest.Whip })
            Assert.True(Content.ItemByKey(key) is not null, $"Items.csv has no '{key}' — that step of the chain is dead");

        // The reward's own gate has to agree with the quest's, or a level-50 Poet is handed a whip he cannot
        // wield (nexusatlas/quests/poetswhip.php: "Level 50 minimum", "Poet Path").
        var whip = Content.ItemByKey(PoetWhipQuest.Whip)!;
        Assert.Equal(PoetWhipQuest.MinLevel, whip.Level);
        Assert.Equal(PoetWhipQuest.PoetPath, whip.PathId);
    }

    /// <summary>The vial is one-shot for 24 hours, so it must not be losable to a stray 'd'. Items.csv 29015
    /// carries our edit (<c>ItmDroppable</c> 0→1 = NoDrop) and <c>Session.TrySacredWaterDrop</c> runs BEFORE
    /// the NoDrop refusal, which is what makes the rite the only drop that works. Flip the flag back and the
    /// vial silently hits the floor of the Subvoid instead of destroying anything.</summary>
    [Fact]
    public void TheSacredWaterCannotBeDroppedAnywhereButTheRite()
    {
        EnsureLoaded();
        Assert.True(Content.ItemByKey(PoetWhipQuest.Water)!.NoDrop,
                    "sacred_water is droppable again — the one-shot vial can now be lost on the floor");
    }

    /// <summary>You cannot start without a pipe, and Sying is the only place named as selling one. If the
    /// stock list loses it the quest has no entrance at all and Staff answers "Hmmm, what? Oh hello,
    /// Stranger" forever.</summary>
    [Fact]
    public void TheSonhiPipeIsStillBuyable()
    {
        EnsureLoaded();

        var sying = Content.Npcs.FirstOrDefault(n => n.Name == "Sying");
        Assert.NotNull(sying);

        var stock = Shops.For(sying!.Key);
        Assert.NotNull(stock);
        Assert.Contains(stock!.SelectMany(c => c.Keys), k => k == PoetWhipQuest.Pipe);
    }

    /// <summary>The two creatures the chain reads. The rabbit key is what the "do not kill" gate counts (a
    /// rename makes the gate permanently pass — the worst kind of silent, because the quest still completes);
    /// The Infected is what the drop rite looks for on the faced tile (a rename makes the rite permanently
    /// refuse). Both must also actually SPAWN, or the errand is unwinnable however well the dialog reads.</summary>
    [Fact]
    public void TheRabbitsAndTheInfectedExistAndSpawnWhereTheQuestExpects()
    {
        EnsureLoaded();

        var rabbit = Content.MobByKey(PoetWhipQuest.RabbitMob);
        var infected = Content.MobByKey(PoetWhipQuest.InfectedMob);
        Assert.NotNull(rabbit);
        Assert.NotNull(infected);

        // The portraits Staff hangs his warnings on are these mobs' own look/colour — a drifted mobs.csv row
        // would show the player the wrong creature entirely.
        Assert.Equal(PoetWhipQuest.RabbitLook, rabbit!.Look);
        Assert.Equal(PoetWhipQuest.InfectedLook, infected!.Look);

        Assert.Contains(Content.AreaSpawns, s => s.Map == PoetWhipQuest.SubvoidMap && s.MobId == infected.Id);
        Assert.Contains(Content.AreaSpawns, s => s.Map == PoetWhipQuest.PathOfChoiceMap && s.MobId == rabbit.Id);
    }

    /// <summary>The pagoda. There is no Warps.csv row into Path of Choice — this tile IS the door — so the
    /// pairing is pinned from the other side: the rows that carry you HOME land on (99|100,115), one tile
    /// south of the trigger, which is what identifies these two tiles as the pagoda's mouth on the 4.95 map
    /// rather than RTK's re-tiled one. Both tiles must be walkable, and so must the shove-back landing, or the
    /// refusal strands a non-acolyte inside a wall.</summary>
    [Fact]
    public void ThePagodaTilesAreTheMouthOfPathOfChoice()
    {
        EnsureLoaded();

        Assert.True(Content.TryMap(PoetWhipQuest.PagodaMap, out var nagnang));
        var ground = MapData.For(PoetWhipQuest.PagodaMap, nagnang.Xs, nagnang.Ys);
        Assert.NotNull(ground);

        foreach (int x in PoetWhipQuest.PagodaX)
        {
            Assert.False(ground!.Solid(x, PoetWhipQuest.PagodaY),
                         $"pagoda tile ({x},{PoetWhipQuest.PagodaY}) is solid — the door can never be stepped on");
            Assert.False(ground.Solid(x, PoetWhipQuest.PagodaPushY),
                         $"the shove-back landing ({x},{PoetWhipQuest.PagodaPushY}) is solid — a refused entry strands the player");
        }

        // The way home, which is what pins the tiles: every exit from Path of Choice lands just south of them.
        var home = Content.Warps.Where(w => w.Key.m == PoetWhipQuest.PathOfChoiceMap
                                         && w.Value.m == PoetWhipQuest.PagodaMap).ToList();
        Assert.NotEmpty(home);
        Assert.All(home, w =>
        {
            Assert.Contains(w.Value.x, PoetWhipQuest.PagodaX.Select(v => (ushort)v));
            Assert.Equal(PoetWhipQuest.PagodaY + 1, w.Value.y);
        });
    }

    /// <summary>Where the pagoda drops you has to be standable AND out of the return warps' way — land on
    /// y=39 and you bounce straight back to Nagnang, which reads as "the pagoda does nothing".</summary>
    [Fact]
    public void ThePathOfChoiceEntryRowIsStandableAndNotItselfAWarp()
    {
        EnsureLoaded();

        Assert.True(Content.TryMap(PoetWhipQuest.PathOfChoiceMap, out var path));
        var ground = MapData.For(PoetWhipQuest.PathOfChoiceMap, path.Xs, path.Ys);
        Assert.NotNull(ground);

        for (int x = PoetWhipQuest.PathEntryMinX; x <= PoetWhipQuest.PathEntryMaxX; x++)
        {
            Assert.False(ground!.Solid(x, PoetWhipQuest.PathEntryY),
                         $"entry tile ({x},{PoetWhipQuest.PathEntryY}) is solid — the pagoda lands you in a wall");
            Assert.False(Content.TryWarp(PoetWhipQuest.PathOfChoiceMap, (ushort)x, PoetWhipQuest.PathEntryY, out _),
                         $"entry tile ({x},{PoetWhipQuest.PathEntryY}) is a warp — you would be bounced straight back out");
        }
    }

    /// <summary>The centre route, "Path of the Arrow", which nexusatlas calls the fast way: Path of Choice →
    /// 2525 → Oblivion, both hops as ordinary warps. A missing hop leaves the acolyte walking a maze with no
    /// exit and nothing to say so.</summary>
    [Fact]
    public void TheCentreRouteReachesOblivion()
    {
        EnsureLoaded();

        const ushort arrow = 2525;
        Assert.Contains(Content.Warps, w => w.Key.m == PoetWhipQuest.PathOfChoiceMap && w.Value.m == arrow);
        Assert.Contains(Content.Warps, w => w.Key.m == arrow && w.Value.m == PoetWhipQuest.OblivionMap);
    }

    /// <summary>The fall, and where it lands. The Subvoid is a 17x17 pocket with no warp out (RTK's own warp
    /// table has none either) — the way home is Return, so the landing tile only has to be inside the map and
    /// standable. Out of bounds here would throw on the first fall; solid would wedge the player.</summary>
    [Fact]
    public void TheSubvoidLandingIsInsideTheMapAndStandable()
    {
        EnsureLoaded();

        Assert.True(Content.TryMap(PoetWhipQuest.SubvoidMap, out var sub));
        Assert.True(PoetWhipQuest.SubvoidX < sub.Xs && PoetWhipQuest.SubvoidY < sub.Ys,
                    $"the Subvoid landing ({PoetWhipQuest.SubvoidX},{PoetWhipQuest.SubvoidY}) is off a {sub.Xs}x{sub.Ys} map");

        var ground = MapData.For(PoetWhipQuest.SubvoidMap, sub.Xs, sub.Ys);
        Assert.NotNull(ground);
        Assert.False(ground!.Solid(PoetWhipQuest.SubvoidX, PoetWhipQuest.SubvoidY));

        // And the fall box has to be inside Oblivion, or the roll can never fire on the tiles it names.
        Assert.True(Content.TryMap(PoetWhipQuest.OblivionMap, out var obl));
        Assert.True(PoetWhipQuest.OblivionMaxX < obl.Xs && PoetWhipQuest.OblivionMaxY < obl.Ys);
    }

    /// <summary>The Forever-branch box has to be inside the Wilderness, and it has to contain the tree the
    /// walkthrough names ("walking around coordinates (019, 091)") — a box that drifted off it would leave the
    /// player searching ground that never yields, with the quest looking simply broken.</summary>
    [Fact]
    public void TheForeverBranchGroundSurroundsTheTree()
    {
        EnsureLoaded();

        Assert.True(Content.TryMap(PoetWhipQuest.TreeMap, out var wild));
        Assert.True(PoetWhipQuest.TreeMaxX < wild.Xs && PoetWhipQuest.TreeMaxY < wild.Ys);
        Assert.True(PoetWhipQuest.InTreeGround(PoetWhipQuest.TreeMap, 19, 91),
                    "the search box no longer contains the Forever Tree at (19,91)");
        Assert.False(PoetWhipQuest.InTreeGround(PoetWhipQuest.PagodaMap, 19, 91), "the box is not map-scoped");
    }

    /// <summary>And he has to be REACHABLE, which is the failure this whole port tripped over first: a quest
    /// giver on a map with no client terrain is dropped at load along with the warp to him, and the only
    /// symptom is that clicking where he should stand does nothing. Pins that his room is renderable, that
    /// Nagnang has a door into it, and that he is standing on floor rather than inside the scenery.</summary>
    [Fact]
    public void StaffIsStandingSomewhereAPlayerCanWalkTo()
    {
        EnsureLoaded();

        Assert.True(Content.TryMap(PoetWhipQuest.StaffMap, out var hall),
                    $"map {PoetWhipQuest.StaffMap} is not renderable — Staff is dropped at load and the quest has no giver");

        // Reachable from the street. (The hall also keeps the way BACK from RTK's 3832 sanctum — that warp
        // survives load because its DESTINATION is renderable — but nobody can ever be standing on 3832 to
        // use it, so the Nagnang door is the only one that matters.)
        Assert.Contains(Content.Warps, w => w.Value.m == PoetWhipQuest.StaffMap && w.Key.m == PoetWhipQuest.PagodaMap);

        var staff = Content.Npcs.Single(n => n.Id == PoetWhipQuest.StaffNpcId);
        var ground = MapData.For(PoetWhipQuest.StaffMap, hall.Xs, hall.Ys);
        Assert.NotNull(ground);
        Assert.False(ground!.Solid(staff.X, staff.Y),
                     $"Staff stands on a solid tile ({staff.X},{staff.Y}) — he cannot be clicked from beside him");
    }
}
