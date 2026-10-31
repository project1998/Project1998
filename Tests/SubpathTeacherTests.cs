using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The first PC-subpath slice (owner, 2026-10-26, P1-P5): a teacher on each of the twelve Circles
/// (<c>Server/SubpathTeacher.cs</c>), selling a member only their own subpath's working spells at RTK's script prices
/// (<c>game-data/SpellLearnCosts.csv</c>, the nine rows keyed to paths 13, 16, 18, 19 and 20); a member's Qui hyang
/// offering RTK's "Subpath Circle" (<c>item_verbs.lua</c> <c>quihyang</c>); and the spellbook rebuild keeping the
/// subpath spells the book holds (<see cref="Content.RespecSpellSet"/>).
///
/// <para>Each fails quietly without its piece. A teacher on the wrong map, or not composed, stands there saying
/// "Greetings, traveller."; a missing price row makes a spell vanish from the list, a wrong one sells it for the wrong
/// items; a rebuild without the keep takes a paid spell at the shrine with nothing said; a scroll without the menu
/// sends a member to a tavern as before. Dialogs run on a thread of their own, started inside the player's monitor as
/// the read loop starts one, and each answer is the real <c>0x3A</c>; the scroll goes through the real <c>0x1C</c>.</para>
/// </summary>
[Collection("world")]
public sealed class SubpathTeacherTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static int _serial;

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public SubpathTeacherTests(SessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    // ---- the twelve Circles and their teachers ------------------------------------------------------------------

    /// <summary>The Circle table names the twelve PC subpaths once each, each on the map Maps.csv calls
    /// "&lt;Subpath&gt; Circle", and every arrival box has walkable tiles, all inside the box, none the teacher's.</summary>
    [Fact]
    public void TheCirclesAreTheTwelveSubpathsOwnMaps()
    {
        Assert.Equal(Enumerable.Range(10, 12), SubpathCircles.All.Select(c => c.Subpath).OrderBy(p => p));
        foreach (var c in SubpathCircles.All)
        {
            Assert.True(Content.IsPcSubpath(c.Subpath), $"path {c.Subpath} is not a PC subpath");
            Assert.Equal($"{Content.PathName(c.Subpath)} Circle", Content.Maps[(ushort)c.Map].Name);

            var tiles = SubpathCircles.ArrivalTiles(c);
            var md = MapData.For((ushort)c.Map)!;
            var teacher = Content.Npcs.Single(n => n.Key == "SubpathTeacherNpc" && n.Map == c.Map);
            Assert.True(tiles.Count > 0, $"{Content.PathName(c.Subpath)} Circle has no walkable arrival tile");
            Assert.All(tiles, t =>
            {
                Assert.InRange((int)t.X, c.X1, c.X2);
                Assert.InRange((int)t.Y, c.Y1, c.Y2);
                Assert.False(md.Solid(t.X, t.Y));
                Assert.NotEqual((teacher.X, teacher.Y), (t.X, t.Y));
            });
        }
    }

    /// <summary>Each Circle has exactly one teacher, "&lt;Subpath&gt; Teacher", placed in the world on a free tile
    /// (open ground, no object, four walkable neighbours), composed of the teacher ability and the universal answers.
    /// The look is the subpath's own where Morphs.csv gives the subpath one (the Chongun uniform, the Marketer guise),
    /// else the look of the base class's own trainers. Red without the NPC rows (no teacher on the map) or without the
    /// composition row or registration (the ability list is the universal answers alone).</summary>
    [Theory]
    [InlineData("Barbarian", 39, 6)]
    [InlineData("Chongun",   37, 0)]
    [InlineData("Do",        39, 6)]
    [InlineData("Merchant",  52, 0)]
    [InlineData("Ranger",    38, 0)]
    [InlineData("Spy",       38, 0)]
    [InlineData("Diviner",   40, 3)]
    [InlineData("Geomancer", 40, 3)]
    [InlineData("Shaman",    40, 3)]
    [InlineData("Druid",     87, 7)]
    [InlineData("Monk",      87, 7)]
    [InlineData("Muse",      87, 7)]
    public void EachCircleHasItsTeacherOnAFreeTile(string subpath, int look, int color)
    {
        int path = Content.PathIdForClass(subpath);
        var circle = SubpathCircles.For(path)!.Value;
        var def = Assert.Single(Content.Npcs, n => n.Map == circle.Map && n.Key == "SubpathTeacherNpc");
        Assert.Equal($"{subpath} Teacher", def.Name);
        Assert.True(def.Enabled);
        Assert.Equal((look, color), ((int)def.Look, (int)def.Color));

        var md = MapData.For((ushort)circle.Map)!;
        Assert.False(md.Solid(def.X, def.Y), "the teacher stands on blocked ground");
        Assert.Equal(0, md.Obj(def.X, def.Y));
        foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            Assert.False(md.Solid(def.X + dx, def.Y + dy), $"({def.X + dx},{def.Y + dy}) next to the teacher is blocked");

        Assert.Equal(new[] { typeof(SubpathTeacherAbility), typeof(InfoAbility) },
                     NpcScripts.For(def).Select(a => a.GetType()));
        var mob = TeacherMob(def);
        Assert.Equal((def.X, def.Y, def.Look), (mob.X, mob.Y, (ushort)mob.Sprite));
    }

    // ---- what each teacher sells --------------------------------------------------------------------------------

    /// <summary>The nine working spells at their RTK scripts' <c>requirements()</c> (the facts report's table 2.3),
    /// and nothing at all for the other seven. Each item named is one Items.csv has. Red without the nine rows: every
    /// list is empty. The Shaman spells slice then put Shaman's solace, Reincarnate and Shaman's torment on sale beside
    /// Phoenix flames, at their scripts' prices; torment's TESTING script has no <c>requirements()</c>, so it sells at its
    /// row's level, 0, for nothing (the project owner's P3). The Do spells slice then put Do's other seven on sale beside Ki strike
    /// and Spirit blade, each at its script's price, the borrowed ones as written (Hear footstep asks Blend's, Crossing
    /// swords Chung ryong rage's). The Diviner spells slice then put Read karma, Hear footstep, Divine time and Vanish on
    /// sale, each at its script's price (Read karma's coins are its list's 0 item, 50; the other three ask Blend's, as
    /// their scripts copy it).
    /// The Muse spells slice then put Sacred verse and Tranquil symphony on sale at their
    /// scripts' level 30 for a Scroll and an Ink, and Costume and Song of the Goddess (from RTK's TESTING copy), whose
    /// scripts have no <c>requirements()</c>, at their rows' level, 0, for nothing (the project owner's P3).</summary>
    [Theory]
    // The Barbarian spells slice: fifteen of the sixteen at their scripts' prices; Push, whose script has no
    // requirements(), at its row's level, 0, for nothing; Survival challenge, with no script of its own, off sale.
    [InlineData("Barbarian", "barbarian_cry 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]; " +
                             "barbarian_rage 99 20000 [1 dragons_liver, 1 electra, 1 spike]; " +
                             "break_jaw 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]; " +
                             "dash_barbarian 99 25000 [1 blood, 1 splendid_tiger_pelt]; " +
                             "dishonor 99 0 [1 forsaken_staff, 2 forsaken_blade]; " +
                             "envigorate 9 100 [1 aged_wine]; " +
                             "insult 70 1000 [2 amber, 80 acorn]; " +
                             "mount 99 0 []; " +
                             "nomadic_blade 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]; " +
                             "push 0 0 []; " +
                             "rend 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]; " +
                             "sonhi_rage 99 50000 [1 chung_ryong_key, 1 dragons_liver, 1 spike]; " +
                             "tigers_might 99 5000 [10 bears_liver, 10 tiger_meat, 5 antler]; " +
                             "wilderness_guise 99 50000 [1 ju_jak_key, 1 surge]; " +
                             "wilderness_weapon 99 40000 [1 il_san_spike, 2 dragons_liver, 2 electra]")]
    [InlineData("Chongun",   "")]
    [InlineData("Do",        "assess_opponent_do 99 5000 [3 fox_blade, 5 fine_rabbit_meat, 80 red_fox_fur]; " +
                             "balance_weapon 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]; " +
                             "blend 99 0 [1 fox_tail, 1 topaz]; " +
                             "crossing_swords 99 50000 [1 chung_ryong_key, 1 dragons_liver, 1 spike]; " +
                             "dash_do 99 25000 [1 blood, 1 splendid_tiger_pelt]; " +
                             "hear_footstep_do 99 0 [1 fox_tail, 1 topaz]; " +
                             "ki_strike 99 500 [1 amber, 70 acorn]; " +
                             "master_weapon 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]; " +
                             "spirit_blade 99 60000 [1 ee_san_spike, 4 dragons_liver, 4 electra]")]
    [InlineData("Merchant",  "")]
    [InlineData("Ranger",    "")]
    [InlineData("Spy",       "assassinate 99 10000 [1 angels_tear, 1 electra, 20 dark_amber]")]
    [InlineData("Diviner",   "divine_time 99 0 [1 fox_tail, 1 topaz]; " +
                             "hear_footstep_diviner 99 0 [1 fox_tail, 1 topaz]; " +
                             "read_karma_diviner 60 50 [20 light_fox_fur, 70 acorn]; " +
                             "vanish 99 0 [1 fox_tail, 1 topaz]")]
    [InlineData("Geomancer", "hailstorm 64 500 [1 amber, 70 acorn]; ice_bombardment 99 500 [1 amber, 70 acorn]")]
    [InlineData("Shaman",    "phoenix_flames 64 500 [1 amber, 70 acorn]; reincarnate 64 500 [1 amber, 70 acorn]; shamans_solace 99 10000 [1 mountain_ginseng]; shamans_torment 0 0 []")]
    // The Druid spells slice: Control flames, Druid's rodent, Steed, Sea breeze, Nature's bloom and Energy bloom beside the
    // three, each at its script's price, the copied lists as written (Sea breeze asks Druid's rodent's, Nature's bloom
    // Control flames'); Steed's list is empty; Efflorescence, with no script, off sale.
    [InlineData("Druid",     "control_flames 99 20000 [1 wicked_staff, 20 antler]; " +
                             "druids_rodent 33 0 [20 rabbit_meat, 30 rat_meat]; " +
                             "earth_strike 99 0 [1 gold_acorn, 1 topaz]; " +
                             "energy_bloom 99 0 [1 gold_acorn, 1 topaz]; " +
                             "lightning_storm 99 0 [20 rabbit_meat, 30 rat_meat]; " +
                             "natures_bloom 99 20000 [1 wicked_staff, 20 antler]; " +
                             "natures_smite 99 0 [1 gold_acorn, 1 topaz]; " +
                             "sea_breeze 33 0 [20 rabbit_meat, 30 rat_meat]; " +
                             "steed 99 0 []")]
    [InlineData("Monk",      "")]
    [InlineData("Muse",      "costume 0 0 []; sacred_verse 30 0 [1 ink, 1 scroll]; song_of_the_goddess 0 0 []; tranquil_symphony 30 0 [1 ink, 1 scroll]")]
    public void EachTeacherSellsTheWorkingSpellsAtRtksScriptPrices(string subpath, string expected)
    {
        int path = Content.PathIdForClass(subpath);
        var sold = Content.SubpathTeacherSpells(path, Content.MarkSpellLevel, Content.MaxMark);
        Assert.All(sold, s => Assert.Equal(path, s.PathId));
        string got = string.Join("; ", sold.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s =>
        {
            var cost = Content.LearnCostFor(s, path)!;
            foreach (var (item, _) in cost.Items) Assert.NotNull(Content.ItemByKey(item));
            Assert.Equal(cost.Level, s.Level);
            string items = string.Join(", ", cost.Items.Select(i => $"{i.Amount} {i.Item}").OrderBy(i => i, StringComparer.Ordinal));
            return $"{s.Key} {cost.Level} {cost.Gold} [{items}]";
        }));
        Assert.Equal(expected, got);
    }

    /// <summary>A price row keyed to a subpath sells to that subpath's teacher alone: no trainer offers the nine to
    /// any path (the rows are keyed to paths 13-20, which no trainer reads), and a subpath's teacher never offers
    /// another subpath's spell.</summary>
    [Fact]
    public void NoTrainerOrOtherTeacherOffersThem()
    {
        foreach (int path in Content.PlayablePaths)
            for (int alignment = 0; alignment <= 3; alignment++)
                Assert.DoesNotContain(Content.SpellsForClass(path, Content.MarkSpellLevel, alignment, Content.MaxMark),
                                      Content.IsPcSubpathSpell);
        foreach (var c in SubpathCircles.All)
            Assert.All(Content.SubpathTeacherSpells(c.Subpath, Content.MarkSpellLevel, Content.MaxMark),
                       s => Assert.Equal(c.Subpath, s.PathId));
        Assert.Empty(Content.SubpathTeacherSpells(4, Content.MarkSpellLevel, Content.MaxMark));   // a base class has no teacher list
    }

    /// <summary>A marked row is offered only from its rank, as at every trainer. Written before any marked row was sold,
    /// this prices Balance weapon (Do, mark 1) through the list's price seam: not at mark 0, offered at mark 1. Red
    /// with the mark gate taken out of <see cref="Content.SubpathTeacherSpells"/>. Its real price row is the Do spells
    /// slice's (<c>DoSpellTests.TheDoTeacherSellsByRank</c>).</summary>
    [Fact]
    public void AMarkedRowNeedsItsRank()
    {
        var balance = Content.SpellByKey("balance_weapon")!;
        Assert.Equal((18, 1), (balance.PathId, balance.Mark));
        Content.LearnCost? Price(SpellDef s) =>
            s.Key == balance.Key ? new Content.LearnCost(99, 60_000, new[] { ("ee_san_spike", 1) }) : Content.LearnCostFor(s, 18);

        Assert.DoesNotContain(Content.SubpathTeacherSpells(18, 99, 0, Price), s => s.Key == balance.Key);
        Assert.Contains(Content.SubpathTeacherSpells(18, 99, 1, Price), s => s.Key == balance.Key);
    }

    /// <summary>The Learn Secret list is the subpath's sold rows at or below the member's level, minus what the book
    /// holds: a level-63 Geomancer is told to grow (the trainer's line), at 64 is offered Hailstorm, at 99 Hailstorm and
    /// Ice bombardment, and holding Hailstorm only Ice bombardment.</summary>
    [Fact]
    public void TheListIsTheRowsAtTheMembersLevelMinusKnown()
    {
        var hail = Content.SpellByKey("hailstorm")!;
        Assert.Equal(new[] { SecretLessons.NothingMoreForNow }, LearnSecretBoxes("Geomancer", 63));
        Assert.Equal(new[] { "Hailstorm" }, LearnSecretOptions("Geomancer", 64));
        Assert.Equal(new[] { "Hailstorm", "Ice bombardment" }, LearnSecretOptions("Geomancer", 99));
        Assert.Equal(new[] { "Ice bombardment" }, LearnSecretOptions("Geomancer", 99, c => c.Spells.Add(hail.Id)));
    }

    // ---- a sale, and the refusals -------------------------------------------------------------------------------

    /// <summary>A Do buys Ki strike: the trainer's lines word for word, the fee priced on the Do's own path, and the
    /// items and coins taken. Red without the price row (the fee reads FREE and nothing is taken, or the spell is not
    /// offered at all). The list is the Do's unmarked seven at level 99, by name (the Do spells slice); Ki strike is the
    /// sixth.</summary>
    [Fact]
    public void ASaleTakesTheItemsAndCoins()
    {
        var (s, o, c) = Member("Do", 99, ch =>
        {
            ch.Coins = 1_000;
            Give(ch, "acorn", 70);
            Give(ch, "amber", 1);
        });
        var def = Teacher("Do");
        Assert.Equal(new[] { "Learn Secret", "Forget Secret" }, Labels(s, def));

        Converse(s, def, "Learn Secret", Pick(6), Pick(1), Pick(1));

        Assert.Equal(new[]
        {
            "Which secret shall I teach you?|Assess opponent|Blend|Crossing swords|Dash|Hear footstep|Ki strike|Spirit blade",
            "You are ready to learn Ki strike. Do you swear you will use this secret only for good causes?|Yes|No",
            "The fee to learn Ki strike is: Acorn (70), Amber (1), 500 gold, All must be in good condition.|Yes|No",
        }, Menus(o));
        Assert.Equal(new[] { "You have learned Ki strike." }, Boxes(o));
        Assert.Contains(Content.SpellByKey("ki_strike")!.Id, c.Spells);
        Assert.Equal((0, 0, 500u), (Count(c, "acorn"), Count(c, "amber"), c.Coins));
    }

    /// <summary>A Druid one Gold acorn short is refused with the trainer's devotion line and keeps the Topaz and the
    /// book as they were.</summary>
    [Fact]
    public void AMissingItemIsRefusedAndNothingIsTaken()
    {
        var (s, o, c) = Member("Druid", 99, ch => Give(ch, "topaz", 1));
        int before = c.Spells.Count;

        Converse(s, Teacher("Druid"), "Learn Secret", Pick(4), Pick(1), Pick(1));

        Assert.Equal("Which secret shall I teach you?|Druid's rodent|Sea breeze|Control flames|Earth strike|Lightning storm|" +
                     "Nature's bloom|Nature's smite|Steed", Menus(o)[0]);
        Assert.Equal(new[] { "Paying for what you want is a sign of devotion. Return when you have what is required for this." }, Boxes(o));
        Assert.Equal((1, before), (Count(c, "topaz"), c.Spells.Count));
    }

    /// <summary>A full book (52) is refused with the trainer's line and the spell is not learned. The fee is taken
    /// first, as at every trainer here (the shared <c>SecretLessons.Teach</c> checks the items and coins, takes them,
    /// then learns) and as RTK's <c>learnSpecificSpells</c> does; pinned so that a change to that order is seen.</summary>
    [Fact]
    public void AFullBookIsRefused()
    {
        var smite = Content.SpellByKey("natures_smite")!;
        var (s, o, c) = Member("Druid", 99, ch =>
        {
            ch.Spells.AddRange(Content.Spells.Where(sp => sp.Key.Length > 0 && sp.PathId == 4)
                                      .Select(sp => sp.Id).Distinct().Take(Content.SpellBookCap));
            Give(ch, "gold_acorn", 1);
            Give(ch, "topaz", 1);
        });
        Assert.Equal(Content.SpellBookCap, c.Spells.Count);

        Converse(s, Teacher("Druid"), "Learn Secret", Pick(7), Pick(1), Pick(1));

        Assert.StartsWith("The fee to learn Nature's smite is:", Menus(o)[2]);
        Assert.Equal(new[] { "Your mind cannot hold any more secrets right now." }, Boxes(o));
        Assert.DoesNotContain(smite.Id, c.Spells);
        Assert.Equal(Content.SpellBookCap, c.Spells.Count);
        Assert.Equal((0, 0), (Count(c, "gold_acorn"), Count(c, "topaz")));   // taken, as every trainer takes it
    }

    /// <summary>Anyone but a member of the teacher's own subpath gets one entry, which says one line and nothing more:
    /// the Druid teacher's base class (a Poet), another Poet subpath (a Monk) and an unrelated class (a Warrior).</summary>
    [Theory]
    [InlineData("Poet")]
    [InlineData("Monk")]
    [InlineData("Warrior")]
    public void ANonMemberGetsOneLine(string cls)
    {
        var (s, o, _) = Member(cls, 99, _ => { });
        var def = Teacher("Druid");
        Assert.Equal(new[] { "Speak" }, Labels(s, def));

        Converse(s, def, "Speak");

        Assert.Equal(new[] { "You are not a Druid, I can not help you." }, Boxes(o));
        Assert.Empty(Menus(o));
    }

    /// <summary>The teacher's list is its Circle's, not the player's class's, so who is learning is read again: a
    /// Druid who leaves the subpath (<c>@subpath leave</c>) with the click menu up, or after accepting the oath, is
    /// refused with the non-member line, pays nothing and learns nothing. Red without the two re-reads: the second
    /// buys Earth strike as a Poet, after leaving took the subpath's spells away.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMemberWhoLeavesMidLessonBuysNothing(bool afterTheOath)
    {
        var strike = Content.SpellByKey("earth_strike")!;
        var (s, o, c) = Member("Druid", 99, ch =>
        {
            Give(ch, "gold_acorn", 1);
            Give(ch, "topaz", 1);
        });
        var def = Teacher("Druid");
        Assert.Equal(new[] { "Learn Secret", "Forget Secret" }, Labels(s, def));

        if (afterTheOath) Converse(s, def, "Learn Secret", Pick(1), Pick(1), "@subpath leave", Pick(1));
        else Converse(s, def, "Learn Secret", "@subpath leave");   // the leave lands before the pick runs: see Converse

        Assert.Equal("Poet", c.ClassName);
        Assert.Equal(new[] { "You are not a Druid, I can not help you." }, Boxes(o));
        Assert.DoesNotContain(strike.Id, c.Spells);
        Assert.Equal((1, 1), (Count(c, "gold_acorn"), Count(c, "topaz")));
    }

    /// <summary>A teacher with nothing priced yet says so to its member (Merchant, one of those still unpriced; it was
    /// Barbarian's until the Barbarian spells slice priced Barbarian's).</summary>
    [Fact]
    public void ATeacherWithNothingToSellSaysSo()
    {
        var (s, o, _) = Member("Merchant", 99, _ => { });
        Converse(s, Teacher("Merchant"), "Learn Secret");
        Assert.Equal(new[] { SubpathTeacherAbility.NothingYet }, Boxes(o));
    }

    /// <summary>The base trainers still teach with the same lines through the shared code: a level-5 Warrior buys
    /// Gateway from Tabaek for its archive price. A guard on the extraction (green before it too).</summary>
    [Fact]
    public void ATrainerStillTeachesWithTheSameLines()
    {
        var gateway = Content.SpellByKey("gateway")!;
        var (s, o, c) = Member("Warrior", 5, ch =>
        {
            Give(ch, "acorn", 10);
            Give(ch, "rabbit_meat", 10);
        });
        var tabaek = Content.NpcById(36)!;
        var ctx = new NpcContext(s, TeacherMob(tabaek), tabaek);
        var list = ctx.LearnableSpells();
        int pick = list.FindIndex(sp => sp.Id == gateway.Id) + 1;
        Assert.True(pick > 0, "Tabaek does not offer Gateway at level 5");

        Converse(s, tabaek, "Learn Secret", Pick(pick), Pick(1), Pick(1));

        Assert.Equal(new[]
        {
            "Which secret shall I teach you?|" + string.Join("|", list.Select(sp => sp.Name)),
            $"You are ready to learn {gateway.Name}. Do you swear you will use this secret only for good causes?|Yes|No",
            $"The fee to learn {gateway.Name} is: Acorn (10), Rabbit meat (10), All must be in good condition.|Yes|No",
        }, Menus(o));
        Assert.Equal(new[] { $"You have learned {gateway.Name}." }, Boxes(o));
        Assert.Contains(gateway.Id, c.Spells);
        Assert.Equal((0, 0), (Count(c, "acorn"), Count(c, "rabbit_meat")));
    }

    // ---- the way in: a member's Qui hyang -----------------------------------------------------------------------

    /// <summary>A member's Qui hyang asks RTK's question under the scroll's own icon, with RTK's two choices, and spends
    /// nothing yet. "Subpath Circle" then lands on a walkable tile of RTK's box on their Circle, not the teacher's, and
    /// spends one. Red without the verb (no menu: straight to a tavern).</summary>
    [Fact]
    public void AMembersQuiHyangGoesToTheirCircle()
    {
        var circle = SubpathCircles.For(Content.PathIdForClass("Druid"))!.Value;
        var (s, o, c) = Scroller("Druid");
        try
        {
            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));

            var menu = MenuBodies(o).Single();
            var scroll = Content.ItemByKey("qui_hyang")!;
            Assert.Equal((2, (int)scroll.ClientIcon), (menu[6], (menu[8] << 8) | menu[9]));
            Assert.Equal(new[] { "Where do you wish to go?|Subpath Circle|Main Inn" }, Menus(o));
            Assert.Equal((2, (ushort)SessionFixture.HomeMap), (Count(c, "qui_hyang"), c.Map));

            s.Receive(MenuFrame(1));

            var tiles = SubpathCircles.ArrivalTiles(circle);
            Assert.Equal(circle.Map, c.Map);
            Assert.Contains((c.X, c.Y), tiles);
            Assert.Equal(1, Count(c, "qui_hyang"));
        }
        finally { _fx.World.LeaveMap(s, c.Map); }
    }

    /// <summary>"Main Inn" is the scroll's old destination, a tavern, and spends one; a reply that is not a pick (a
    /// "next") moves nobody and spends nothing. That step is not a close: closing the box sends no reply at all, and
    /// the menu's await stays pending.</summary>
    [Fact]
    public void AMembersMainInnIsTheTavernAndANonPickSpendsNothing()
    {
        var (s, _, c) = Scroller("Spy");
        try
        {
            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));
            s.Receive(NextFrame());   // a "next", not a pick
            Assert.Equal((2, (ushort)SessionFixture.HomeMap), (Count(c, "qui_hyang"), c.Map));

            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));
            s.Receive(MenuFrame(2));
            Assert.Contains(c.Map, TavernMaps());
            Assert.Equal(1, Count(c, "qui_hyang"));
        }
        finally { _fx.World.LeaveMap(s, c.Map); }
    }

    /// <summary>Everyone else's Qui hyang is as before: no menu, a tavern, one spent. So is a member's Yellow scroll.
    /// A guard: green before this slice too.</summary>
    [Theory]
    [InlineData("Poet", "qui_hyang")]
    [InlineData("Baekho", "qui_hyang")]
    [InlineData("Druid", "yellow_scroll")]
    public void EveryoneElsesScrollIsUnchanged(string cls, string item)
    {
        var (s, o, c) = Scroller(cls, item);
        try
        {
            s.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));
            Assert.Empty(o.BodiesOf(ServerOp.NpcDialog));
            Assert.Contains(c.Map, TavernMaps());
            Assert.Equal(1, Count(c, item));
        }
        finally { _fx.World.LeaveMap(s, c.Map); }
    }

    // ---- the rebuild keeps paid subpath spells ------------------------------------------------------------------

    /// <summary>Devoting at a shrine rebuilds the book: a Druid keeps the Earth strike it bought, and gains the new
    /// alignment's list. Forgotten, it stays forgotten through the next rebuild (renouncing, as the summit does). Red
    /// without the keep: the shrine takes it.</summary>
    [Fact]
    public void AShrineRebuildKeepsAPaidSubpathSpell()
    {
        var strike = Content.SpellByKey("earth_strike")!;
        var (s, _, c) = Member("Druid", 99, ch => ch.Spells.Add(strike.Id));
        var shrineDef = Content.NpcById(188)!;   // the Kwi-Sin shrine's shaman
        var ctx = new NpcContext(s, TeacherMob(shrineDef), shrineDef);

        s.WithState(() => ctx.Devote(1));
        Assert.Equal(1, c.Alignment);
        Assert.Contains(strike.Id, c.Spells);
        Assert.Equal(new[] { strike.Id }, c.Spells.Where(id => Content.IsPcSubpathSpell(Content.SpellById(id)!)));
        Assert.True(c.Spells.Count > 1, "the rebuild gave the Druid none of the Poet list");

        s.WithState(() => { ctx.ForgetSpell(strike.Id); ctx.Devote(0); });
        Assert.Equal(0, c.Alignment);
        Assert.DoesNotContain(strike.Id, c.Spells);
    }

    /// <summary>The keep is for the member's own current subpath: a rebuild into the base class (<c>@class Poet</c>)
    /// or into another subpath drops them, and a book entry of another subpath is never kept.</summary>
    [Fact]
    public void ARebuildIntoAnotherPathDropsThem()
    {
        var strike = Content.SpellByKey("earth_strike")!;
        var hail = Content.SpellByKey("hailstorm")!;
        var held = new[] { strike, hail };
        bool Keeps(int path, SpellDef sp) =>
            Content.RespecSpellSet(path, 99, 0, 0, held: held).Any(s => s.Id == sp.Id);

        Assert.True(Keeps(13, strike));    // Druid keeps its own
        Assert.False(Keeps(13, hail));     // ...never a Geomancer's
        Assert.False(Keeps(4, strike));    // @class Poet
        Assert.False(Keeps(17, strike));   // @class Monk
    }

    /// <summary>The most subpath spells a member can hold today is what their teacher sells (nine, for a Druid), and
    /// every PC subpath's rebuilt book with all of them, at every alignment and rank and every Sage rung, keeps each and
    /// holds everything else it is owed up to the client's 52 slots, no further. A rank-2 or rank-3 Druid is owed 53 or
    /// 57: the base spells their book did not hold give way (the project owner, 2026-10-31, "Subpath spells stay";
    /// <see cref="ARebuildPastTheCapKeepsEveryHeldSpell"/> pins which). Red without the keep (they are not in the book),
    /// and with the rebuild's old order (a Druid's 53 and 57 slots). Each subpath's largest set owed is written to the
    /// output.</summary>
    [Fact]
    public void TheMostAMemberCanHoldTodayFitsTheBook()
    {
        var problems = new List<string>();
        var sizes = new List<(int Count, int Owed, int Path, int Alignment, int Mark, int Held)>();
        foreach (var c in SubpathCircles.All)
        {
            var held = Content.SubpathTeacherSpells(c.Subpath, Content.MarkSpellLevel, Content.MaxMark);
            for (int alignment = 0; alignment <= 3; alignment++)
                for (int mark = 0; mark <= Content.MaxMark; mark++)
                {
                    var book = Content.RespecSpellSet(c.Subpath, Content.MarkSpellLevel, alignment, mark,
                                                      dogFlag: Content.CanLearnDogSpells(c.Subpath),
                                                      sageRung: Content.SageLadder.Length, held: held);
                    int owed = Content.RespecSpellSet(c.Subpath, Content.MarkSpellLevel, alignment, mark,
                                                      dogFlag: Content.CanLearnDogSpells(c.Subpath),
                                                      sageRung: Content.SageLadder.Length).Count + held.Count;
                    sizes.Add((book.Count, owed, c.Subpath, alignment, mark, held.Count));
                    foreach (var sp in held.Where(h => book.All(b => b.Id != h.Id)))
                        problems.Add($"{Content.PathName(c.Subpath)} a{alignment} m{mark}: {sp.Key} not kept");
                    if (book.Count != Math.Min(owed, Content.SpellBookCap))
                        problems.Add($"{Content.PathName(c.Subpath)} a{alignment} m{mark}: {book.Count} slots of {owed} owed");
                }
        }
        foreach (var s in sizes.Where(s => s.Mark == Content.MaxMark && s.Alignment == 0).OrderByDescending(s => s.Owed))
            _out.WriteLine($"{Content.PathName(s.Path)}: {s.Count} of {Content.SpellBookCap}, {s.Owed} owed, with {s.Held} held");
        _out.WriteLine($"largest owed: {sizes.Max(s => s.Owed)}");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The rebuild past the cap (the project owner, 2026-10-31, "Subpath spells stay"): a rank-3 Druid holding
    /// all nine Druid spells, who forgot as many Poet spells as made room for them (and two more, in the second case),
    /// renounces (a shrine's rebuild at the alignment they have). All nine stay, every spell the book held stays, the
    /// book is full at 52, and of the Poet spells they had forgotten only as many come back as there is room for, the
    /// first in the rebuild's order. Red with the rebuild's old order (held subpath spells last, cut at 52): the last
    /// Druid spells go.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ARebuildPastTheCapKeepsEveryHeldSpell(int room)
    {
        int druid = Content.PathIdForClass("Druid");
        var nine = Content.SubpathTeacherSpells(druid, Content.MarkSpellLevel, Content.MaxMark);
        var owed = Content.RespecSpellSet(druid, 99, alignment: 0, mark: 3);
        Assert.Equal(9, nine.Count);
        Assert.True(owed.Count + nine.Count > Content.SpellBookCap,
                    $"a rank-3 Druid is owed {owed.Count} + {nine.Count}, inside the {Content.SpellBookCap}-slot book");
        int forgot = owed.Count + nine.Count - Content.SpellBookCap + room;
        var forgotten = owed.Skip(owed.Count / 2).Take(forgot).ToList();   // from the middle of the Poet list
        var book = owed.Where(sp => !forgotten.Contains(sp)).Concat(nine).Select(sp => sp.Id).ToList();
        var (s, _, c) = Member("Druid", 99, ch => { ch.Mark = 3; ch.Spells.AddRange(book); });
        Assert.Equal(Content.SpellBookCap - room, c.Spells.Count);
        var shrineDef = Content.NpcById(188)!;   // the Kwi-Sin shrine's shaman
        var ctx = new NpcContext(s, TeacherMob(shrineDef), shrineDef);

        s.WithState(() => ctx.Devote(0));

        var lost = book.Where(id => !c.Spells.Contains(id)).Select(id => Content.SpellById(id)!.Key).ToList();
        var back = forgotten.Where(f => c.Spells.Contains(f.Id)).Select(f => f.Key).ToList();
        Assert.True(lost.Count == 0 && c.Spells.Count == Content.SpellBookCap
                    && back.SequenceEqual(forgotten.Take(room).Select(f => f.Key)),
                    $"{c.Spells.Count} slots; lost [{string.Join(", ", lost)}]; back [{string.Join(", ", back)}] of forgotten " +
                    $"[{string.Join(", ", forgotten.Select(f => f.Key))}]");
    }

    /// <summary>A rung of a ladder the book held stays at the cut (#417's review, F3). A rank-3 Druid's book holds all nine
    /// Druid spells and the Poet set owed less as many entries as made room for them, with a lower rung in place of its
    /// ladder's top. The rebuild keeps only the top rung of a ladder, so it hands back the top, which the book did not hold
    /// as a spell; it holds its ladder, so the top counts as held and stays, and the lower rung is the only entry of the
    /// book the rebuild leaves out (it is the top's ladder). Red with a ladder not counted: the cut leaves the top out too,
    /// and the member holds neither rung (the review's theory, ported).</summary>
    [Theory]
    [InlineData("water_of_life_poet", "revitalize_poet")]
    [InlineData("heavens_kiss_poet", "remedy_poet")]
    [InlineData("flare_poet", "earthquake_poet")]
    public void ARebuildPastTheCapKeepsARungOfAHeldLadder(string top, string lower)
    {
        int druid = Content.PathIdForClass("Druid");
        var nine = Content.SubpathTeacherSpells(druid, Content.MarkSpellLevel, Content.MaxMark);
        var owed = Content.RespecSpellSet(druid, 99, alignment: 0, mark: 3);
        var t = owed.FirstOrDefault(sp => sp.Key == top);
        var l = Content.SpellByKey(lower);
        Assert.True(t is not null && l is not null, $"top {top} owed {t is not null}, lower {lower} exists {l is not null}");
        var forgotten = owed.Where(sp => sp.Id != t!.Id).Take(owed.Count + nine.Count - Content.SpellBookCap).ToList();
        var book = owed.Where(sp => sp.Id != t!.Id && !forgotten.Contains(sp)).Append(l!).Concat(nine).ToList();
        var rebuilt = Content.RespecSpellSet(druid, 99, alignment: 0, mark: 3, held: book);
        bool keepsTop = rebuilt.Any(sp => sp.Id == t!.Id);
        var lost = book.Where(b => rebuilt.All(r => r.Id != b.Id)).Select(b => b.Key).ToList();
        Assert.True(keepsTop && lost.SequenceEqual(new[] { lower }) && rebuilt.Count == Content.SpellBookCap,
                    $"book {book.Count}, rebuilt {rebuilt.Count}; top {top} kept {keepsTop}; held and lost [{string.Join(", ", lost)}]");
    }

    /// <summary>A devote or a renounce past the cap keeps every spell the book held, in the new alignment's version
    /// (#417's re-check, F4). The rebuild runs at the new alignment, so a held spell comes back as that alignment's
    /// version, under another id. A rank-3 Druid in alignment <paramref name="from"/> holds all nine Druid spells and that
    /// alignment's Poet set owed, less as many entries as made room for them (52 in all), and devotes at the Kwi-Sin
    /// shrine to <paramref name="to"/> (to 0: a renounce; <c>NpcContext.Devote</c>, <c>Session.SwapAlignment</c>,
    /// <c>SyncSpellbook</c>). Of what the new alignment owes, nothing whose spell the book held (in any version, by the
    /// content's alignment families) is cut; all nine stay; the book is full at 52. From 0 to 1 the pairs are named:
    /// Resurrect becomes Return Spirit (<c>game-data/Spells.csv</c> ids 4138, 4139) and Ensure becomes Spirit Shield
    /// (4162, 4163). Red with a spell counted held by its own id only: the cut takes the new versions of held level-99
    /// spells and hands the forgotten ones back (the re-check's probe, ported). It judges by the content's families,
    /// which cannot see a row they link wrongly (the re-check's F6);
    /// <see cref="ADevoteAtAShrineOrARenounceAtTheSummitKeepsRtksVersionOfEveryHeldSpell"/> judges by pairs it names
    /// itself.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    public void ADevoteOrRenouncePastTheCapKeepsEveryHeldSpellInItsNewAlignment(int from, int to)
    {
        int druid = Content.PathIdForClass("Druid");
        var nine = Content.SubpathTeacherSpells(druid, Content.MarkSpellLevel, Content.MaxMark);
        var owedFrom = Content.RespecSpellSet(druid, 99, alignment: from, mark: 3);
        var owedTo = Content.RespecSpellSet(druid, 99, alignment: to, mark: 3);
        int forgot = owedFrom.Count + nine.Count - Content.SpellBookCap;
        Assert.True(forgot > 0, $"a rank-3 Druid in alignment {from} is owed {owedFrom.Count} + {nine.Count}, inside the book");
        var forgotten = owedFrom.Take(forgot).ToList();
        var book = owedFrom.Where(sp => !forgotten.Contains(sp)).Concat(nine).Select(sp => sp.Id).ToList();
        var (s, _, c) = Member("Druid", 99, ch => { ch.Mark = 3; ch.Alignment = (byte)from; ch.Spells.AddRange(book); });
        Assert.Equal(Content.SpellBookCap, c.Spells.Count);
        var shrineDef = Content.NpcById(188)!;   // the Kwi-Sin shrine's shaman
        var ctx = new NpcContext(s, TeacherMob(shrineDef), shrineDef);

        s.WithState(() => ctx.Devote(to));

        var family = (Dictionary<string, string>)typeof(Content)
            .GetMethod("BuildAlignFamilies", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { Content.Spells })!;
        string F(SpellDef sp) => family.TryGetValue(sp.Key, out var leader) ? leader : sp.Key;
        var bookF = book.Select(id => F(Content.SpellById(id)!)).ToHashSet();
        var nowF = c.Spells.Select(id => F(Content.SpellById(id)!)).ToHashSet();
        var heldCut = owedTo.Where(sp => bookF.Contains(F(sp)) && !nowF.Contains(F(sp))).Select(sp => $"{sp.Key}(lv{sp.Level})").ToList();
        var lostNine = nine.Where(n => !c.Spells.Contains(n.Id)).Select(n => n.Key).ToList();
        var named = from == 0 && to == 1
            ? new[] { "return_spirit_poet", "spirit_shield_poet" }.Where(k => !c.Spells.Contains(Content.SpellByKey(k)!.Id)).ToList()
            : new List<string>();
        Assert.True(c.Alignment == to && heldCut.Count == 0 && lostNine.Count == 0 && named.Count == 0
                    && c.Spells.Count == Content.SpellBookCap,
                    $"{from} to {to}: alignment now {c.Alignment}, {c.Spells.Count} slots; held but cut [{string.Join(", ", heldCut)}]; " +
                    $"Druid spells lost [{string.Join(", ", lostNine)}]; named versions missing [{string.Join(", ", named)}]");
    }

    /// <summary>RTK's four versions of the Poet spells the next fact names, copied from its swap lists here rather than
    /// read from the server's table (<c>rtklua/Accepted/Scripts/swapAlignment.lua</c>, unaligned, Kwi-Sin, Ming-Ken and
    /// Ohaeng; a swap turns a held spell into the same row's spell of the new alignment, lines 531-537). Each row's four
    /// lines are given. The Caterpillar's Ming-Ken version is the one RTK's Caterpillar script defines
    /// (<c>rtklua/Accepted/Spells/poet/cotw_caterpillar.lua</c>:99); the list writes Kwi-Sin's word at line 379.</summary>
    private static readonly string[][] RtkPoetVersions =
    {
        new[] { "purge_poet", "cure_illness_poet", "restore_health_poet", "remove_poison_poet" },              // 92, 209, 326, 443
        new[] { "invoke_poet", "spirits_power_poet", "life_force_poet", "gather_magic_poet" },                  // 97, 214, 331, 448
        new[] { "resurrect_poet", "return_spirit_poet", "mingken_blessing_poet", "death_undone_poet" },        // 139, 256, 373, 490
        new[] { "cotw_caterpillar_poet", "kwisin_companion_poet", "mingken_companion_poet", "ohaeng_companion_poet" },
                                                                                                                // 145, 262, 379, 496
        new[] { "cotw_fluffy_dog_poet", "kwisin_assistant_poet", "mingken_assistant_poet", "ohaeng_assistant_poet" },
                                                                                                                // 146, 263, 380, 497
        new[] { "cotw_panda_bear_poet", "kwisin_protector_poet", "mingken_protector_poet", "ohaeng_protector_poet" },
                                                                                                                // 147, 264, 381, 498
        new[] { "cotw_wild_monkey_poet", "kwisin_fighter_poet", "mingken_fighter_poet", "ohaeng_fighter_poet" }, // 148, 265, 382, 499
        new[] { "cotw_gorilla_poet", "kwisin_warrior_poet", "mingken_warrior_poet", "ohaeng_warrior_poet" },     // 149, 266, 383, 500
        new[] { "cotw_wind_dancer_poet", "kwisin_champion_poet", "mingken_champion_poet", "ohaeng_champion_poet" },
                                                                                                                // 150, 267, 384, 501
        new[] { "cotw_wind_warrior_poet", "kwisin_avatar_poet", "mingken_avatar_poet", "ohaeng_avatar_poet" },   // 151, 268, 385, 502
    };

    /// <summary>A devote at an alignment's shrine, or a renounce at the summit, past the cap keeps the new alignment's
    /// version of every spell the book held (#417's re-check, F6; its N6 probe, ported). A rank-3 Druid in alignment
    /// <paramref name="from"/> holds all nine Druid spells and that alignment's Poet set owed less its first entries, as
    /// many as made room for the nine (52 in all). From Unaligned they answer the catechism of the shrine of
    /// <paramref name="to"/> to its end and dedicate for life (<c>AlignmentAbility</c>); to Unaligned they say "cleanse
    /// curse" to the Tiger Palace summit's shaman and take his help (<c>SummitAbility</c>). Both end in
    /// <c>NpcContext.Devote</c>, which rebuilds the book at the new alignment. The pairs are named here, not read from the
    /// server: RTK's swap lists for the Poet spells (<see cref="RtkPoetVersions"/>) and the live poet page's Sam san
    /// families (<see cref="SamSanAlignedRowTests.Families"/>). Of each named spell the book held, the new alignment's
    /// version is in the book; of the spells the book had forgotten, none comes back (the new alignment owes as many, so
    /// there is no room); all nine Druid spells stay; the book is full at 52. Red with the content's adjacency families
    /// deciding held (ccfa08b): from Unaligned the new versions of the Gorilla, the Wind dancer, the Wind warrior and
    /// Charge of Life are cut and four forgotten spells come back; a renounce cuts the Wild monkey's, the Wind dancer's,
    /// the Wind warrior's and Charge of Life's.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    public void ADevoteAtAShrineOrARenounceAtTheSummitKeepsRtksVersionOfEveryHeldSpell(int from, int to)
    {
        int druid = Content.PathIdForClass("Druid");
        var nine = Content.SubpathTeacherSpells(druid, Content.MarkSpellLevel, Content.MaxMark);
        var owedFrom = Content.RespecSpellSet(druid, 99, alignment: from, mark: 3);
        int forgot = owedFrom.Count + nine.Count - Content.SpellBookCap;
        Assert.True(forgot > 0, $"a rank-3 Druid in alignment {from} is owed {owedFrom.Count} + {nine.Count}, inside the book");
        Assert.Equal(owedFrom.Count, Content.RespecSpellSet(druid, 99, alignment: to, mark: 3).Count);
        var forgotten = owedFrom.Take(forgot).ToList();
        var book = owedFrom.Where(sp => !forgotten.Contains(sp)).Concat(nine).Select(sp => sp.Id).ToList();
        var versions = RtkPoetVersions
            .Concat(SamSanAlignedRowTests.Families.Where(f => f.Base.EndsWith("_poet", StringComparison.Ordinal))
                                                  .Select(f => f.Rows.Select(r => r.Key).Prepend(f.Base).ToArray()))
            .ToList();
        string[]? RowOf(string key) => versions.FirstOrDefault(v => v.Contains(key));
        bool Held(string key) => book.Contains(Content.SpellByKey(key)!.Id);
        // Soothe and Gateway are one row at every alignment; every other forgotten spell is named, so "back" is judged;
        // and every named spell not forgotten is held, so "kept" is judged for each.
        foreach (var f in forgotten)
            Assert.True(f.Alignment < 0 || RowOf(f.Key) is not null, $"the forgotten {f.Key} is not named here");
        var heldRows = versions.Where(v => !forgotten.Any(f => v.Contains(f.Key))).ToList();
        Assert.True(heldRows.All(v => Held(v[from])),
                    $"not in the book: {string.Join(", ", heldRows.Where(v => !Held(v[from])).Select(v => v[from]))}");
        var (s, _, c) = Member("Druid", 99, ch =>
        {
            ch.Mark = 3;
            ch.Alignment = (byte)from;
            ch.MaxHp = 30_000; ch.MaxMp = 20_000;     // the summit's floor is 20,000 and 10,000
            ch.Spells.AddRange(book);
        });
        Assert.Equal(Content.SpellBookCap, c.Spells.Count);

        if (to != 0)
        {
            int map = AlignmentAbility.Shrines.Single(kv => kv.Value.Align == to).Key;
            var shrine = Content.Npcs.Single(n => n.Key == "AlignmentNpc" && n.Map == map);
            int aspect = to switch { 1 => 3, 2 => 1, _ => 2 };   // the afterlife, the living, the balance of all
            Converse(s, shrine, "Speak", NextFrame(), NextFrame(), NextFrame(), Pick(3), NextFrame(), Pick(aspect),
                     NextFrame(), Pick(4));
        }
        else
            SayTo<SummitAbility>(s, Content.Npcs.Single(n => n.Key == "SummitNpc"), "cleanse curse",
                                 NextFrame(), NextFrame(), NextFrame(), NextFrame(), Pick(1));

        bool Has(string key) => c.Spells.Contains(Content.SpellByKey(key)!.Id);
        var missing = heldRows.Where(v => !Has(v[to])).Select(v => $"{v[from]}->{v[to]}").ToList();
        var back = forgotten.Select(f => RowOf(f.Key)?[to] ?? f.Key).Where(Has).ToList();
        var lostNine = nine.Where(n => !c.Spells.Contains(n.Id)).Select(n => n.Key).ToList();
        string line = $"{from} to {to}: alignment now {c.Alignment}, {c.Spells.Count} slots; held, new version missing " +
                      $"[{string.Join(", ", missing)}]; forgotten back [{string.Join(", ", back)}]; Druid spells lost " +
                      $"[{string.Join(", ", lostNine)}]; {heldRows.Count} named spells held";
        _out.WriteLine(line);
        Assert.True(c.Alignment == to && c.Spells.Count == Content.SpellBookCap && missing.Count == 0 && back.Count == 0
                    && lostNine.Count == 0, line);
    }

    /// <summary>Every aligned row of the content is in the declared pairing that decides "held" at a devote or a renounce
    /// past the cap (<c>Content.AlignmentPairOf</c>; #417's re-check, F6), under a key that is a loaded unaligned row. A
    /// row the pairing does not name is held only by its own id, so the cut can take its new version from a member who
    /// held it; the message names each such row, for the pairing's table. Red with Siege's row taken out of the
    /// table.</summary>
    [Fact]
    public void EveryAlignedRowIsInTheDeclaredPairing()
    {
        var problems = new List<string>();
        foreach (var sp in Content.Spells.Where(sp => sp.Alignment is >= 1 and <= 3))
        {
            string pair = Content.AlignmentPairKey(sp.Key);
            if (pair == sp.Key) problems.Add($"{sp.Key} (alignment {sp.Alignment}): not in the pairing");
            else if (Content.SpellByKey(pair) is not { Alignment: 0 })
                problems.Add($"{sp.Key}: paired under {pair}, which is not a loaded unaligned row");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ===== plumbing ==============================================================================================

    private static NpcDef Teacher(string subpath)
    {
        var circle = SubpathCircles.For(Content.PathIdForClass(subpath))!.Value;
        return Content.Npcs.Single(n => n.Key == "SubpathTeacherNpc" && n.Map == circle.Map);
    }

    private Mob TeacherMob(NpcDef def)
    {
        var mob = _fx.World.NpcsNear(def.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == def.Id);
        Assert.True(mob is not null, $"{def.Name} is not placed in the world");
        return mob!;
    }

    /// <summary>A socket-free session for a new character of <paramref name="cls"/>, built but not placed on a map,
    /// so a conversation moves nothing in the shared world.</summary>
    private (Session Session, RecordingOutbound Out, Character Character) Member(string cls, int level, Action<Character> configure)
    {
        var c = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = $"teach{Interlocked.Increment(ref _serial)}",
            Map = SessionFixture.HomeMap, X = 5, Y = 10,
            ClassName = cls, Level = (byte)level,
        };
        c.SettingFlags &= ~Character.SettingBit(4);   // no advice lines (SessionFixture.PlayerWith says why)
        configure(c);
        var o = new RecordingOutbound($"recorder:{c.Name}");
        return (new Session(o, 2005, _fx.Store, _fx.World, c), o, c);
    }

    /// <summary>A placed level-60 player of <paramref name="cls"/>, alive, holding two of <paramref name="item"/> in
    /// bag slot 1.</summary>
    private (Session Session, RecordingOutbound Out, Character Character) Scroller(string cls, string item = "qui_hyang")
    {
        var def = Content.ItemByKey(item)!;
        return _fx.PlayerWith($"scroll{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.ClassName = cls;
            ch.Level = 60;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.Inventory.Add(new InvItem { Slot = 0, ItemId = def.Id, Amount = 2 });
        });
    }

    private static HashSet<ushort> TavernMaps() =>
        Content.Inns.Values.SelectMany(v => v).Select(i => (ushort)i.Map).ToHashSet();

    private static void Give(Character c, string key, int amount) =>
        c.Inventory.Add(new InvItem { Slot = (byte)c.Inventory.Count, ItemId = Content.ItemByKey(key)!.Id, Amount = amount });

    private static int Count(Character c, string key) =>
        c.Inventory.Where(i => i.ItemId == Content.ItemByKey(key)!.Id).Sum(i => i.Amount);

    private List<string> Labels(Session s, NpcDef def)
    {
        var ctx = new NpcContext(s, TeacherMob(def), def);
        return NpcScripts.For(def).SelectMany(a => a.Entries(ctx)).Select(e => e.label).ToList();
    }

    /// <summary>The options a level-<paramref name="level"/> member is offered by their teacher's Learn Secret.</summary>
    private List<string> LearnSecretOptions(string subpath, int level, Action<Character>? configure = null)
    {
        var (s, o, _) = Member(subpath, level, configure ?? (_ => { }));
        Converse(s, Teacher(subpath), "Learn Secret", NextFrame());   // a "next", not a pick: the picker ends
        return Menus(o).Single().Split('|').Skip(1).ToList();
    }

    private List<string> LearnSecretBoxes(string subpath, int level)
    {
        var (s, o, _) = Member(subpath, level, _ => { });
        Converse(s, Teacher(subpath), "Learn Secret");
        Assert.Empty(Menus(o));
        return Boxes(o);
    }

    private static byte[] Pick(int index) => MenuFrame((byte)index);

    /// <summary>Run <paramref name="label"/>'s entry of <paramref name="def"/>'s menu for this player, inside their
    /// monitor on a thread of its own, then take <paramref name="steps"/> in order, then "next" until the conversation
    /// ends. A step is a <c>0x3A</c> answer frame, or a typed '@' command (a string), sent as the real chat packet. The
    /// entry is picked from the menu as it stands before any step; leading commands run after that and before the
    /// entry does, as a command typed with the click menu still up would.</summary>
    private void Converse(Session s, NpcDef def, string label, params object[] steps)
    {
        var ctx = new NpcContext(s, TeacherMob(def), def);
        var entry = NpcScripts.For(def).SelectMany(a => a.Entries(ctx)).Single(e => e.label == label);
        Run(s, () => entry.run(ctx), steps);
    }

    /// <summary>Say <paramref name="speech"/> to <paramref name="def"/>, which answers it with its
    /// <typeparamref name="T"/> (the handler its composition gives it), then take <paramref name="steps"/> as
    /// <see cref="Converse"/> does.</summary>
    private void SayTo<T>(Session s, NpcDef def, string speech, params object[] steps) where T : INpcSayHandler
    {
        var ctx = new NpcContext(s, TeacherMob(def), def);
        var handler = NpcScripts.For(def).OfType<T>().Single();
        Run(s, () => handler.OnSay(ctx, speech), steps);
    }

    private static void Run(Session s, Func<Task> start, object[] steps)
    {
        Task? flow = null;
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(() =>
        {
            int k = 0;
            for (; k < steps.Length && steps[k] is string first; k++) s.Receive(ChatFrame(first));
            s.WithState(() => { flow = start(); });
            for (; k < steps.Length && !flow!.IsCompleted; k++)
                s.Receive(steps[k] is string command ? ChatFrame(command) : (byte[])steps[k]);
            for (int i = 0; i < 20 && !flow!.IsCompleted; i++) s.Receive(NextFrame());
        })) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(Bound), "the dialog thread never finished");
        Assert.Null(fault);
        Assert.True(flow!.IsCompletedSuccessfully, "the dialog was still waiting, or failed");
    }

    /// <summary>A typed line (<c>0x0E</c>): <c>00 len text</c>, the layout PcSubpathTests sends its commands in.</summary>
    private static byte[] ChatFrame(string line)
    {
        var text = Encoding.ASCII.GetBytes(line);
        var body = new byte[2 + text.Length];
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        return SessionFixture.Frame(ClientOp.Chat, body);
    }

    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    private static List<byte[]> MenuBodies(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[0] == 2 && b[1] == 2 && b[7] == 1).ToList();

    /// <summary>Every menu (0x30 kind 2) the player was shown, as "prompt|option|option...": the prompt is the u16BE
    /// length at [21] and ASCII from [23], then a count byte and each option as a length byte and ASCII.</summary>
    private static List<string> Menus(RecordingOutbound o)
    {
        var menus = new List<string>();
        foreach (var b in MenuBodies(o))
        {
            int len = (b[21] << 8) | b[22];
            var parts = new List<string> { Encoding.ASCII.GetString(b, 23, len) };
            int at = 23 + len, n = b[at++];
            for (int i = 0; i < n; i++)
            {
                int ol = b[at++];
                parts.Add(Encoding.ASCII.GetString(b, at, ol));
                at += ol;
            }
            menus.Add(string.Join("|", parts));
        }
        return menus;
    }

    /// <summary>Every text box (0x30 type 1) the player was shown, its message (u16BE length at [21], ASCII from [23]).</summary>
    private static List<string> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog)
         .Where(b => b.Length >= 23 && b[0] == 0 && b[1] == 1 && b[7] == 1)
         .Select(b => Encoding.ASCII.GetString(b, 23, Math.Min((b[21] << 8) | b[22], b.Length - 23)))
         .ToList();
}
