using System.Linq;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The Dagger Uniform quest (see <see cref="DaggerUniformQuest"/>) adds no content of its own — it is four
/// NPCs, four items, two creatures and one spell that were all already in the data, joined by one ability and
/// two Session hooks. Every one of those joins is by KEY or by ID, and every one of them fails silently: a
/// renamed item reads as "you don't have it", a dropped composition row reads as "he has nothing to say", a
/// missing spawn reads as "there is no rooster in Buya". None of it breaks a build.
///
/// <para>These pin the joins in walk order, plus the two calls this port makes that RTK does not: the reward
/// being the spell rather than the buckler, and the acorn being stolen in a place whose only exit is the map
/// the crow watches.</para>
/// </summary>
public class DaggerUniformQuestTests
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

    /// <summary>The three rogue masters and the crow all have to CARRY the ability, or their part of the
    /// chain is simply missing with nothing anywhere to say why. Blackbird is the sharp end: he had no
    /// NpcAbilities.csv row at all before this quest, which in this codebase means a silently inert NPC.</summary>
    [Fact]
    public void TheFourSpeakersCarryTheAbility()
    {
        EnsureLoaded();

        foreach (int id in new[] { DaggerUniformQuest.DaggerNpcId, DaggerUniformQuest.MaroNpcId,
                                   DaggerUniformQuest.MasoNpcId, DaggerUniformQuest.CrowNpcId })
        {
            var npc = Content.NpcById(id);
            Assert.NotNull(npc);
            Assert.Contains(NpcScripts.For(npc!), a => a is DaggerUniformAbility);
        }

        Assert.Equal("RogueTrainerNpc", Content.NpcById(DaggerUniformQuest.DaggerNpcId)!.Key);
        Assert.Equal("BlackbirdNpc",    Content.NpcById(DaggerUniformQuest.CrowNpcId)!.Key);

        // Maso is reached with the 'h' gesture, which only ever consults INpcHandItemHandler.
        Assert.Contains(NpcScripts.For(Content.NpcById(DaggerUniformQuest.MasoNpcId)!).OfType<INpcHandItemHandler>(),
                        h => h is DaggerUniformAbility);
    }

    /// <summary>Ten NPCs share <c>RogueTrainerNpc</c>, so composing the ability onto that identifier hands it
    /// to all ten — including the six alignment Maros and Masos, who have no part in this. The in-ability id
    /// gate is what keeps Dagger's initiation in Dagger's mouth; this pins that the identifier really is
    /// shared, which is the reason the gate has to exist at all.</summary>
    [Fact]
    public void TheRogueTrainerIdentifierIsShared()
    {
        EnsureLoaded();

        var trainers = Content.Npcs.Where(n => n.Key == "RogueTrainerNpc").ToList();
        Assert.True(trainers.Count > 3, "expected the identifier to be shared — the id gates below are why");
        foreach (int id in new[] { DaggerUniformQuest.DaggerNpcId, DaggerUniformQuest.MaroNpcId, DaggerUniformQuest.MasoNpcId })
            Assert.Contains(trainers, n => n.Id == id);

        // Blackbird's identifier, by contrast, is his alone — no gate needed there beyond the id check.
        Assert.Single(Content.Npcs, n => n.Key == "BlackbirdNpc");
    }

    /// <summary>Every item and spell the chain moves, by key. Two of the four are a Silvery/Silvered pair one
    /// letter apart, which is exactly the sort of key a well-meaning edit collapses into one.</summary>
    [Fact]
    public void EveryItemAndTheRewardSpellExist()
    {
        EnsureLoaded();

        foreach (string key in new[] { DaggerUniformQuest.StolenAcorn, DaggerUniformQuest.ReturnedAcorn,
                                       DaggerUniformQuest.Stardrop, DaggerUniformQuest.Scroll })
            Assert.NotNull(Content.ItemByKey(key));

        Assert.NotEqual(DaggerUniformQuest.StolenAcorn, DaggerUniformQuest.ReturnedAcorn);

        var uniform = Content.SpellByKey(DaggerUniformQuest.RewardSpell);
        Assert.NotNull(uniform);
        Assert.Equal(2124, uniform!.Id);
        // It is a disguise, and the Morphs.csv row is the whole of what it DOES — without it the reward is a
        // spellbook entry that casts nothing.
        Assert.True(Content.MorphFor(uniform) is not null, "dagger_uniform has no Morphs.csv row — the reward would cast nothing");
    }

    /// <summary>The two creatures. The rooster is the one the quest asks you to LOOK at, so it has to exist
    /// AND be standing somewhere in southern Buya — a bird nobody can find is an unfinishable step 3.</summary>
    [Fact]
    public void TheRoosterIsSpawnedInSouthernBuya()
    {
        EnsureLoaded();

        var rooster = Content.MobByKey(DaggerUniformQuest.RoosterMob);
        Assert.NotNull(rooster);

        const ushort buya = 330;
        var inBuya = Content.Spawns.Where(s => s.MobId == rooster!.Id && s.Map == buya).ToList();
        Assert.NotEmpty(inBuya);
        // "southern Buya" — the far half of a 195-row map. A rooster that migrated north still passes the
        // spawn check above and still makes the Atlas walkthrough wrong.
        Assert.All(inBuya, s => Assert.True(s.Y > Content.Maps[buya].Ys / 2,
                                            $"the Blue Rooster at ({s.X},{s.Y}) is no longer in southern Buya"));

        Assert.NotNull(Content.MobByKey(DaggerUniformQuest.AssassinMob));
    }

    /// <summary>The theft's geometry, which is the whole reason RTK's crow trigger can be a single map check:
    /// the Kugnae Rogue Guild has exactly one way back out and it lands in Kugnae. If a second exit is ever
    /// added, a player can walk the acorn out past the crow and dead-end the quest holding an item nothing
    /// will take.</summary>
    [Fact]
    public void TheOnlyWayOutOfMarosGuildIsKugnae()
    {
        EnsureLoaded();

        var maro = Content.NpcById(DaggerUniformQuest.MaroNpcId);
        Assert.NotNull(maro);
        Assert.Equal((ushort)16, maro!.Map);                                  // Maro Sanctum

        // Sanctum -> hall, hall -> Kugnae, and nowhere else from either.
        var fromSanctum = Content.Warps.Where(w => w.Key.m == 16).Select(w => w.Value.m).Distinct().ToList();
        Assert.Equal(new[] { (ushort)15 }, fromSanctum);

        var fromHall = Content.Warps.Where(w => w.Key.m == 15).Select(w => w.Value.m).Distinct().ToList();
        Assert.Equal(new[] { DaggerUniformQuest.KugnaeMap }, fromHall);
    }

    /// <summary>Dagger's own reachability: Atlas sends the player to Nagnang (020, 141), and that door has to
    /// actually lead to the map he is standing on. Two hops — Nagnang into the guild, guild into his sanctum.</summary>
    [Fact]
    public void DaggerIsReachableFromNagnang()
    {
        EnsureLoaded();

        var dagger = Content.NpcById(DaggerUniformQuest.DaggerNpcId);
        Assert.NotNull(dagger);

        // He must be on a map the 4.95 client can render, or the NPC loader drops him and the whole quest is
        // silently absent — which is exactly what his old map 3824 did (see DaggerUniformQuest.GuildMap).
        Assert.Equal(DaggerUniformQuest.GuildMap, dagger!.Map);
        Assert.True(Content.Maps.ContainsKey(DaggerUniformQuest.GuildMap));
        var hall = MapData.For(DaggerUniformQuest.GuildMap);
        Assert.NotNull(hall);
        Assert.False(hall!.Solid(dagger.X, dagger.Y), $"Dagger stands on solid ground at ({dagger.X},{dagger.Y})");

        const ushort nagnang = 2500;
        var intoGuild = Content.Warps.Where(w => w.Key.m == nagnang && w.Value.m == DaggerUniformQuest.GuildMap).ToList();
        Assert.NotEmpty(intoGuild);
        // The Atlas coordinate is the tile you stand on to step north into the doorway row.
        Assert.All(intoGuild, w => Assert.Equal((ushort)140, w.Key.y));
    }

    /// <summary>The crow speaks one line with no NPC in front of the player, so its portrait is passed as a
    /// bare look/colour pair rather than derived from the mob. Drift here draws the wrong bird.</summary>
    [Fact]
    public void CrowPortraitMatchesItsRow()
    {
        EnsureLoaded();

        var crow = Content.NpcById(DaggerUniformQuest.CrowNpcId);
        Assert.NotNull(crow);
        Assert.Equal((ushort)DaggerUniformQuest.CrowLook, crow!.Look);
        Assert.Equal((byte)DaggerUniformQuest.CrowColor, crow.Color);
        Assert.Equal((ushort)1004, crow.Map);                                 // Dae Shore
    }

    /// <summary>The stage ladder has to stay strictly ordered and gap-free: the ability branches on
    /// <c>&lt;</c> and <c>==</c> against these, and the two Session hooks advance one to the next by name. A
    /// renumbering that leaves a hole strands a player on a value nothing matches.</summary>
    [Fact]
    public void StagesAreConsecutive()
    {
        int[] ladder =
        {
            DaggerUniformQuest.Stage.Unmet, DaggerUniformQuest.Stage.TappedOnce, DaggerUniformQuest.Stage.TappedTwice,
            DaggerUniformQuest.Stage.Assaulted, DaggerUniformQuest.Stage.WatchForRooster, DaggerUniformQuest.Stage.SeenRooster,
            DaggerUniformQuest.Stage.StealAcorn, DaggerUniformQuest.Stage.CrowTookIt, DaggerUniformQuest.Stage.CrowSpoke,
            DaggerUniformQuest.Stage.PlantScroll, DaggerUniformQuest.Stage.ScrollPlanted, DaggerUniformQuest.Stage.Done,
        };

        Assert.Equal(0, ladder[0]);
        for (int i = 1; i < ladder.Length; i++) Assert.Equal(ladder[i - 1] + 1, ladder[i]);

        // "Dagger Strangers" is offered below WatchForRooster and "Blue Rooster" at or above it, so the
        // handover point must sit exactly where the assault ends.
        Assert.Equal(DaggerUniformQuest.Stage.Assaulted + 1, DaggerUniformQuest.Stage.WatchForRooster);
    }
}
