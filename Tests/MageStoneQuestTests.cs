using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Mage's Spirit Stone (see <see cref="MageStoneQuest"/>) is four data files and two scripts joined at
/// six places, and every one of those joins fails the quiet way: a dropped composition row leaves Wand with
/// his ordinary trainer menu, a renamed mouse leaves the AI hook unbound so a Vexed-and-zapped mouse simply
/// squeaks nothing, a missing Lua script leaves the prophets standing there saying "Greetings, traveller",
/// and a moved spawn leaves the mice out of the room entirely. None of that fails a build or logs a word.
///
/// <para>These pin the joins, plus the level gate — the one place the port departs from RTK that is a bare
/// number and so the easiest to "correct" back.</para>
/// </summary>
public class MageStoneQuestTests
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

    /// <summary>Wand carries the ability, and — because nine NPCs share <c>MageTrainerNpc</c> — he is the only
    /// one of them who runs the quest. Without the in-code gate the other eight would each offer their own
    /// "Mage Stone" entry, in the wrong kingdom, pointing at a cave above Nagnang.</summary>
    [Fact]
    public void OnlyWandRunsTheQuest()
    {
        EnsureLoaded();

        var wand = Content.Npcs.FirstOrDefault(n => n.Id == MageStoneQuest.WandNpcId);
        Assert.NotNull(wand);
        Assert.Equal("MageTrainerNpc", wand!.Key);
        Assert.Equal("Wand", wand.Name);

        var trainers = Content.Npcs.Where(n => n.Key == "MageTrainerNpc").ToList();
        Assert.True(trainers.Count > 1, "the identifier is shared — the gate below is why");

        foreach (var t in trainers)
            Assert.Contains(NpcScripts.For(t), a => a is MageStoneAbility);

        Assert.True(MageStoneAbility.AnswersFor(wand));
        foreach (var t in trainers.Where(n => n.Id != MageStoneQuest.WandNpcId))
            Assert.False(MageStoneAbility.AnswersFor(t), $"{t.Name} (npc {t.Id}) offers the Mage Stone but should not");
    }

    /// <summary>The three prophets are Lua, not C#, so their whole conversation hangs off one identifier
    /// having a script. With the script gone they fall through to the C# path, find no ability composed onto
    /// <c>MageStoneProphetsNpc</c>, and greet you as strangers — which is exactly how they behaved before this
    /// quest existed, and is indistinguishable from a player who has not made the offering.</summary>
    [Fact]
    public void TheProphetsHaveADialogScript()
    {
        EnsureLoaded();

        Assert.True(NpcScript.Has("MageStoneProphetsNpc"), "npc_dialog.lua no longer defines the prophets");

        var prophets = Content.Npcs.Where(n => n.Key == "MageStoneProphetsNpc").ToList();
        Assert.Equal(3, prophets.Count);

        // The script branches on the NPC's NAME, and there is a cell map per prophet. Both have to hold.
        Assert.Equal(new[] { "Void", "Yang", "Yin" }, prophets.Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal(MageStoneQuest.ProphetCells.OrderBy(m => m),
                     prophets.Select(p => p.Map).OrderBy(m => m));
    }

    /// <summary>The offering. Each mouse must exist, must be bound to the <c>on_attacked</c> hook that writes
    /// the flag, and must be effectively immortal — a killable mouse would let a player clear the room and
    /// lock themselves out until it respawned.</summary>
    [Fact]
    public void EachMouseIsImmortalAndCarriesTheHook()
    {
        EnsureLoaded();

        foreach (var key in MageStoneQuest.Mice)
        {
            var mob = Content.MobByKey(key);
            Assert.NotNull(mob);
            Assert.True(mob!.Hp > 100_000_000, $"{key} is killable ({mob.Hp} vita) — the offering must not die");
            Assert.True(MobScript.Has(key, "on_attacked"), $"mob_ai.lua no longer binds on_attacked for {key}");
        }
    }

    /// <summary>The mice have to BE in the prophets' room, one cluster above each doorway. Atlas locates them
    /// by door ("a grey rat above the middle door"), and the doorway warps are how a cluster is identified —
    /// so this pins each mouse to the door it belongs to rather than to a tile that could drift.</summary>
    [Fact]
    public void EachMouseSitsAboveItsOwnDoorway()
    {
        EnsureLoaded();

        // The three doorways out of Prophets, grouped by the cell they lead to, west to east.
        var doors = Content.Warps
            .Where(w => w.Key.m == MageStoneQuest.ProphetsMap && MageStoneQuest.ProphetCells.Contains(w.Value.m))
            .GroupBy(w => w.Value.m)
            .OrderBy(g => g.Min(w => w.Key.x))
            .Select(g => (Cell: g.Key, Xs: g.Select(w => (int)w.Key.x).ToArray(), Y: g.Min(w => (int)w.Key.y)))
            .ToList();
        Assert.Equal(3, doors.Count);

        for (int i = 0; i < 3; i++)
        {
            var mob = Content.MobByKey(MageStoneQuest.Mice[i]);
            Assert.NotNull(mob);

            var points = Content.Spawns.Where(s => s.MobId == mob!.Id).ToList();
            Assert.NotEmpty(points);
            Assert.All(points, s => Assert.Equal(MageStoneQuest.ProphetsMap, s.Map));

            // North of the door row (lower y — the strip is walled off from the room, which is why you reach
            // the mouse with a spell and not with a sword) and horizontally over its own doorway.
            var (cell, xs, doorY) = doors[i];
            Assert.All(points, s => Assert.True(s.Y < doorY,
                $"{MageStoneQuest.Mice[i]} at ({s.X},{s.Y}) is not above the doorway into map {cell} (y {doorY})"));
            Assert.Contains(points, s => xs.Contains(s.X));
        }
    }

    /// <summary>Void's task. The tomb has to exist and be reachable from the Cemetery, or the flag the crypt
    /// spirit sets can never be earned and Wand's turn-in refuses forever with no hint why.</summary>
    [Fact]
    public void TheCryptIsReachableFromTheCemetery()
    {
        EnsureLoaded();

        Assert.True(Content.Maps.ContainsKey(MageStoneQuest.CryptMap));
        Assert.Equal("Oh-mudum crypt", Content.Maps[MageStoneQuest.CryptMap].Name);
        Assert.Contains(Content.Warps, w => w.Key.m == 2200 && w.Value.m == MageStoneQuest.CryptMap);
    }

    /// <summary>The two components and the reward, by key. A renamed key reads as "the player hasn't got it",
    /// so a turn-in would refuse a player who is holding exactly what was asked for.</summary>
    [Fact]
    public void ComponentsAndRewardExist()
    {
        EnsureLoaded();

        Assert.NotNull(Content.ItemByKey(MageStoneQuest.RoseItem));
        Assert.NotNull(Content.ItemByKey(MageStoneQuest.OreItem));

        var stone = Content.ItemByKey(MageStoneQuest.Reward);
        Assert.NotNull(stone);
        Assert.Equal(25047, stone!.Id);          // the existing Spirit stone — this quest must not mint a new one
    }

    /// <summary>Atlas's level gate, pinned so a later "port it faithfully to RTK" pass has to argue with a
    /// test rather than silently drop it back to RTK's 10.</summary>
    [Fact]
    public void TheLevelGateIsAtlasNotRtk()
    {
        Assert.Equal(55, MageStoneQuest.MinLevel);
    }
}
