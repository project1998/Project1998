using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Joining and leaving the four NPC subpaths (Server/NpcSubpathQuest.cs, and the Dog's cleanse in
/// game-data/npc_dialog.lua), and the four NPC-subpath spells' learn prices with the offer guard in
/// <see cref="Content.SpellsForClass"/>.
///
/// <para>Each would fail quietly. A blessing that never sets RTK's flag, or sets it below Ox karma, leaves Baek
/// refusing ("You have not been blessed.") or joining a player who never earned it, with nothing in the log. A
/// shortfall Baek misreads takes a player's favor and pelts and joins them anyway, or joins them for free. A learn row
/// keyed to the base class without the guard offers Baekho's cunning to every level-99 Rogue at every rogue trainer;
/// the same row keyed to the subpath reads as FREE. These pin each case from the outside: what the player is shown,
/// what is taken, the path, the flag and the legend.</para>
///
/// <para>The players are socket-free sessions that are not placed on a map (only the broadcast's listener is), so
/// nothing here moves the shared world. A dialog runs on a thread of its own, started inside the player's monitor as
/// the read loop starts one, and each "next" is the real <c>0x3A</c>, which resumes it inline on that thread.</para>
/// </summary>
[Collection("world")]
public sealed class NpcSubpathQuestTests
{
    private const int BaekNpcId = 119;
    private const int OxKarma = 6, DogKarma = 3;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static int _serial;

    private readonly SessionFixture _fx;

    public NpcSubpathQuestTests(SessionFixture fx) => _fx = fx;

    // ---- the Watcher's blessing ---------------------------------------------------------------------------------

    /// <summary>RTK's box, on the only map titled "Buya", and nowhere else.</summary>
    [Fact]
    public void TheWatchersFieldIsRtksBoxOnBuya()
    {
        Assert.Equal("Buya", Content.Maps[NpcSubpathQuest.WatcherMap].Name);
        Assert.Single(Content.Maps.Values, m => m.Name == "Buya");

        Assert.True(NpcSubpathQuest.InWatcherField(330, 6, 4));
        Assert.True(NpcSubpathQuest.InWatcherField(330, 28, 14));
        Assert.False(NpcSubpathQuest.InWatcherField(330, 5, 10));
        Assert.False(NpcSubpathQuest.InWatcherField(330, 29, 10));
        Assert.False(NpcSubpathQuest.InWatcherField(330, 10, 3));
        Assert.False(NpcSubpathQuest.InWatcherField(330, 10, 15));
        Assert.False(NpcSubpathQuest.InWatcherField(331, 10, 10));
    }

    public enum Walker
    {
        /// <summary>Ox karma, unblessed, on the field, the roll 1: blessed, with RTK's smiling line.</summary>
        OxOnTheField,
        /// <summary>The same at level 1: RTK checks no level, so the rabbit blesses a level-1 player too.</summary>
        OxAtLevelOne,
        /// <summary>Dog karma: the rabbit only bounds away, and the flag stays 0.</summary>
        BelowOx,
        /// <summary>Any other roll: nothing.</summary>
        OtherRoll,
        /// <summary>One tile outside the box: nothing.</summary>
        OffTheField,
        /// <summary>Already blessed: nothing, and the flag stays 1.</summary>
        AlreadyBlessed,
    }

    [Theory]
    [InlineData(Walker.OxOnTheField)]
    [InlineData(Walker.OxAtLevelOne)]
    [InlineData(Walker.BelowOx)]
    [InlineData(Walker.OtherRoll)]
    [InlineData(Walker.OffTheField)]
    [InlineData(Walker.AlreadyBlessed)]
    public void TheWatcherBlessesOnlyOxKarmaOnItsField(Walker walker)
    {
        var (s, o, c) = Unplaced("Watcher", ch =>
        {
            ch.Level = walker == Walker.OxAtLevelOne ? (byte)1 : (byte)99;
            ch.Karma = walker == Walker.BelowOx ? DogKarma : OxKarma;
            ch.Map = NpcSubpathQuest.WatcherMap;
            ch.X = walker == Walker.OffTheField ? (ushort)(NpcSubpathQuest.FieldX1 + 1) : (ushort)12;
            ch.Y = 8;
            if (walker == Walker.AlreadyBlessed) QuestState.Over(ch, QuestState.Registry).Set(NpcSubpathQuest.BlessedReg, 1);
        });

        s.WithState(() => s.WatcherStep(walker == Walker.OtherRoll ? 2 : 1));

        int flag = QuestState.Over(c, QuestState.Registry).Get(NpcSubpathQuest.BlessedReg);
        var boxes = Boxes(o);
        switch (walker)
        {
            case Walker.OxOnTheField:
            case Walker.OxAtLevelOne:
                Assert.Equal(1, flag);
                Assert.Equal(new[] { NpcSubpathQuest.WatcherSmiles }, boxes.Select(b => b.Text));
                Assert.Equal((1, 0x8000 | NpcSubpathQuest.WatcherLook, NpcSubpathQuest.WatcherColor),
                             (boxes[0].Head, boxes[0].Gfx, boxes[0].Color));
                break;
            case Walker.BelowOx:
                Assert.Equal(0, flag);
                Assert.Equal(new[] { NpcSubpathQuest.WatcherBoundsAway }, boxes.Select(b => b.Text));
                break;
            case Walker.AlreadyBlessed:
                Assert.Equal(1, flag);
                Assert.Empty(boxes);
                break;
            default:
                Assert.Equal(0, flag);
                Assert.Empty(boxes);
                break;
        }
    }

    /// <summary>Steps at most a walker takes on the field before the fact gives up: the chance that the server's
    /// own one-in-fifty roll misses every one of them is (49/50)^3000, about 4e-27.</summary>
    private const int FieldSteps = 3000;

    /// <summary>
    /// <b>Walking the field reaches the Watcher.</b> The fact above calls <c>WatcherStep</c> with a chosen roll, so
    /// it pins the rabbit's rules and not that walking ever asks it: with the call in
    /// <c>Session.OnScriptedTileStep</c> deleted, all of them stay green and no player can ever be blessed (PR #394's
    /// review, F1). Here an Ox-karma player stands in the box on Buya and sends real <c>0x06</c> walk packets through
    /// <c>Session.Receive</c>, the read loop's entry, so each accepted step runs <c>HandleWalk</c> to its end and the
    /// after-step hooks with it. The roll is the server's own (<c>QuestRandom</c>, not seamed); the walker goes back and
    /// forth between two walkable tiles of the box until the rabbit has smiled, at most <see cref="FieldSteps"/>
    /// steps. It finds the pair by walking east from each tile of the box in turn, reporting that tile as the one it
    /// stands on, which the handler honours (its client-authoritative resync); a refused step leaves it there.
    /// <para>Red with <c>TryWatcherBlessing();</c> removed from <c>OnScriptedTileStep</c>, Debug and Release: no flag
    /// and no box after all the steps.</para>
    /// </summary>
    [Fact]
    public void WalkingTheFieldReachesTheWatcher()
    {
        var info = Content.Maps[NpcSubpathQuest.WatcherMap];
        var (s, o, c) = _fx.PlayerWith(Name("FieldWalker"), ch =>
        {
            ch.Level = 99;
            ch.Karma = OxKarma;
            ch.MapXs = info.Xs;                       // HandleWalk bounds the step by the character's own copy
            ch.MapYs = info.Ys;
        }, NpcSubpathQuest.WatcherMap, 12, 8);
        try
        {
            bool Blessed() => QuestState.Over(c, QuestState.Registry).Get(NpcSubpathQuest.BlessedReg) == 1;
            int steps = 0, accepted = 0;
            bool StepTo(byte dir, ushort fromX, ushort fromY, int toX, int toY)
            {
                s.Receive(Walk(dir, fromX, fromY));
                steps++;
                bool ok = s.PlayerX == toX && s.PlayerY == toY;
                if (ok) accepted++;
                return ok;
            }

            // Two tiles of the box a player can walk between both ways: from each tile, east and back west.
            (ushort X, ushort Y)? west = null;
            for (int y = NpcSubpathQuest.FieldY0; y <= NpcSubpathQuest.FieldY1 && west is null && !Blessed(); y++)
                for (int x = NpcSubpathQuest.FieldX0; x < NpcSubpathQuest.FieldX1 && west is null && !Blessed(); x++)
                    if (StepTo(1, (ushort)x, (ushort)y, x + 1, y) && StepTo(3, (ushort)(x + 1), (ushort)y, x, y))
                        west = ((ushort)x, (ushort)y);
            Assert.True(west is not null || Blessed(), "no two tiles of the Watcher's box can be walked between");

            // Back and forth between them, each step reporting the tile the server has us on (a normal step).
            while (!Blessed() && steps < FieldSteps)
            {
                var (wx, wy) = west!.Value;
                bool atWest = s.PlayerX == wx;
                Assert.True(atWest ? StepTo(1, wx, wy, wx + 1, wy) : StepTo(3, (ushort)(wx + 1), wy, wx, wy),
                            $"step {steps} between ({wx},{wy}) and ({wx + 1},{wy}) was refused");
            }

            Assert.True(Blessed(), $"{steps} steps ({accepted} accepted) on the field and the Watcher never looked");
            var boxes = Boxes(o);
            Assert.Equal(new[] { NpcSubpathQuest.WatcherSmiles }, boxes.Select(b => b.Text));
            Assert.Equal((1, 0x8000 | NpcSubpathQuest.WatcherLook, NpcSubpathQuest.WatcherColor),
                         (boxes[0].Head, boxes[0].Gfx, boxes[0].Color));
        }
        finally { _fx.World.LeaveMap(s, s.CharMap); }
    }

    /// <summary>A real <c>0x06</c> walk packet: the direction, the step counter, and the tile the client says it is on.</summary>
    private static byte[] Walk(byte dir, ushort x, ushort y) =>
        SessionFixture.Frame(ClientOp.Walk, new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y });

    // ---- Baek ---------------------------------------------------------------------------------------------------

    /// <summary>Baek is still a shop (Buy / Sell, in that order, the stock untouched), and the subpath words are
    /// his: the composition is the shop, the joining, and the universal answers.</summary>
    [Fact]
    public void BaekKeepsHisShopAndHearsTheSubpathWords()
    {
        var def = Content.NpcById(BaekNpcId)!;
        Assert.Equal("BaekNpc", def.Key);
        Assert.Equal(new[] { typeof(ShopAbility), typeof(NpcSubpathAbility), typeof(InfoAbility) },
                     NpcScripts.For(def).Select(a => a.GetType()));

        var (s, _, _) = Unplaced("BaekMenu", ch => Joiner(ch, "Rogue"));
        var ctx = new NpcContext(s, BaekMob(), def);
        Assert.Equal("Buy / Sell", string.Join(" / ", NpcScripts.For(def).SelectMany(a => a.Entries(ctx)).Select(e => e.label)));
    }

    /// <summary>Each word is its subpath's own Paths.csv name, and that path is an NPC subpath of a different base
    /// class. A word mapped to the wrong id would make a Mage a Baekho.</summary>
    [Fact]
    public void EachWordIsItsNpcSubpathsOwnName()
    {
        Assert.Equal(4, NpcSubpathQuest.SubpathForWord.Count);
        foreach (var (word, path) in NpcSubpathQuest.SubpathForWord)
        {
            Assert.True(Content.IsNpcSubpath(path), $"'{word}' -> {path} is not an NPC subpath");
            Assert.Equal(word, Content.PathName(path).ToLowerInvariant());
        }
        Assert.Equal(new[] { 1, 2, 3, 4 },
                     NpcSubpathQuest.SubpathForWord.Values.Select(Content.PathBaseOf).OrderBy(b => b));
    }

    /// <summary>The favors Baek looks for are the twelve the Mythic alliances give, and every one is an item.</summary>
    [Fact]
    public void BaekLooksForTheTwelveMythicFavors()
    {
        Assert.All(NpcSubpathQuest.Favors, f => Assert.NotNull(Content.ItemByKey(f)));
        Assert.Equal(Content.MythicAlliances.Select(a => a.Favor).OrderBy(f => f, StringComparer.Ordinal),
                     NpcSubpathQuest.Favors.OrderBy(f => f, StringComparer.Ordinal));
        Assert.NotNull(Content.ItemByKey(NpcSubpathQuest.Pelt));
        Assert.NotNull(Content.ItemByKey(NpcSubpathQuest.StudyItem));
    }

    public enum Refusal
    {
        TooYoung, PcSubpath, KarmaBelowOx, NotBlessed, NoDogLegend, AlreadyCommitted, AnotherClassesWord,
    }

    /// <summary>Before the offer, every refusal is RTK's line (or RTK's silence), and nothing is taken: favor, pelts
    /// and experience stay, the path stays, no legend is added.</summary>
    [Theory]
    [InlineData(Refusal.TooYoung)]
    [InlineData(Refusal.PcSubpath)]
    [InlineData(Refusal.KarmaBelowOx)]
    [InlineData(Refusal.NotBlessed)]
    [InlineData(Refusal.NoDogLegend)]
    [InlineData(Refusal.AlreadyCommitted)]
    [InlineData(Refusal.AnotherClassesWord)]
    public void BaekRefusesBeforeTheOfferAndTakesNothing(Refusal refusal)
    {
        string cls = refusal switch
        {
            Refusal.PcSubpath => "Merchant",
            Refusal.AlreadyCommitted => "Baekho",
            Refusal.AnotherClassesWord => "Warrior",
            _ => "Rogue",
        };
        var (s, o, c) = Unplaced("BaekNo", ch =>
        {
            Joiner(ch, cls);
            if (refusal == Refusal.TooYoung) ch.Level = 98;
            if (refusal == Refusal.KarmaBelowOx) ch.Karma = DogKarma;
            if (refusal == Refusal.NotBlessed) QuestState.Over(ch, QuestState.Registry).Set(NpcSubpathQuest.BlessedReg, 0);
            if (refusal == Refusal.NoDogLegend) ch.Legends.RemoveAll(l => l.Name == NpcSubpathQuest.DogLegend);
        });
        Assert.Equal(cls, c.ClassName);
        var before = Holdings(c);

        Assert.True(Say(s, "baekho"), "Baek's own word must be consumed");
        string? line = refusal switch
        {
            Refusal.TooYoung => NpcSubpathQuest.TooYoung,
            Refusal.PcSubpath => NpcSubpathQuest.InPcSubpath,
            Refusal.KarmaBelowOx => NpcSubpathQuest.KarmaTooLow,
            Refusal.NotBlessed => NpcSubpathQuest.NotBlessed,
            Refusal.AlreadyCommitted => NpcSubpathQuest.AlreadyCommitted,
            _ => null,
        };
        Assert.Equal(line is null ? Array.Empty<string>() : new[] { line }, Boxes(o).Select(b => b.Text));
        Assert.Equal(before, Holdings(c));
        Assert.Equal(cls, c.ClassName);
        Assert.DoesNotContain(c.Legends, l => l.Name == NpcSubpathQuest.Legend);
    }

    public enum Shortfall { NoFavor, NoPelts, NoExp }

    /// <summary>After the offer, Baek takes each part as he checks it, as RTK does: no favor costs nothing; no pelts
    /// costs the favor; too little experience costs the favor and the pelts. Each is refused with RTK's line, and
    /// the path, the experience and the legends stay.</summary>
    [Theory]
    [InlineData(Shortfall.NoFavor)]
    [InlineData(Shortfall.NoPelts)]
    [InlineData(Shortfall.NoExp)]
    public void BaekTakesEachPartAsHeChecksIt(Shortfall shortfall)
    {
        var (s, o, c) = Unplaced("BaekShort", ch =>
        {
            Joiner(ch, "Rogue");
            if (shortfall == Shortfall.NoFavor) ch.Inventory.RemoveAll(i => i.ItemId == Item("tigers_favor").Id);
            if (shortfall == Shortfall.NoPelts) Bag(ch, NpcSubpathQuest.Pelt).Amount = NpcSubpathQuest.PeltCount - 1;
            if (shortfall == Shortfall.NoExp) ch.Exp = NpcSubpathQuest.JoinExp - 1;
        });
        uint exp = c.Exp;

        Assert.True(Say(s, "baekho"));
        string refusal = shortfall switch
        {
            Shortfall.NoFavor => NpcSubpathQuest.NoFavor,
            Shortfall.NoPelts => NpcSubpathQuest.NoPelts,
            _ => NpcSubpathQuest.NoExp,
        };
        Assert.Equal(NpcSubpathQuest.Offer.Append(refusal), Boxes(o).Select(b => b.Text));
        Assert.Equal(0, Count(c, "tigers_favor"));          // never held (NoFavor), or taken
        Assert.Equal(shortfall == Shortfall.NoPelts ? NpcSubpathQuest.PeltCount - 1
                   : shortfall == Shortfall.NoExp ? 0 : NpcSubpathQuest.PeltCount,
                     Count(c, NpcSubpathQuest.Pelt));
        Assert.Equal(exp, c.Exp);
        Assert.Equal("Rogue", c.ClassName);
        Assert.DoesNotContain(c.Legends, l => l.Name == NpcSubpathQuest.Legend);
    }

    /// <summary>With everything, each class's word makes it that class's NPC subpath, keeping its rank: one favor
    /// (the LAST of RTK's list the player holds), ten pelts and 1,000,000,000 experience are taken; "Attained Totem
    /// Mastery" is added; another player online is sent RTK's "[SUBPATH]" line, naming the new title; and the
    /// player reads the study page over the Book's icon, then Baek's last line.</summary>
    [Theory]
    [InlineData("Warrior", "chung ryong", 6)]
    [InlineData("Rogue", "baekho", 7)]
    [InlineData("Mage", "ju jak", 8)]
    [InlineData("Poet", "hyun moo", 9)]
    public void BaekJoinsWithEverythingAndKeepsTheRank(string cls, string word, int subpath)
    {
        var (listener, heard) = _fx.Player(Name("Listener"));
        try
        {
            var (s, o, c) = Unplaced("BaekYes", ch =>
            {
                Joiner(ch, cls);
                ch.Mark = 1;
                ch.Inventory.Add(new InvItem((byte)ch.Inventory.Count, Item("dragons_favor").Id, 1));
            });
            uint exp = c.Exp;

            Assert.True(Say(s, word));
            Assert.Equal(Content.PathName(subpath), c.ClassName);
            Assert.Equal(1, c.Mark);
            Assert.Equal(exp - NpcSubpathQuest.JoinExp, c.Exp);
            Assert.Equal(0, Count(c, "tigers_favor"));     // tigers_favor is after dragons_favor in RTK's list
            Assert.Equal(1, Count(c, "dragons_favor"));
            Assert.Equal(0, Count(c, NpcSubpathQuest.Pelt));
            Assert.Single(c.Legends, l => l.Name == NpcSubpathQuest.Legend
                                          && l.Text == NpcSubpathQuest.LegendText()
                                          && l.Icon == NpcSubpathQuest.LegendIcon && l.Color == NpcSubpathQuest.LegendColor);

            var boxes = Boxes(o);
            Assert.Equal(NpcSubpathQuest.Offer.Append(NpcSubpathQuest.Studied).Append(NpcSubpathQuest.Ready),
                         boxes.Select(b => b.Text));
            var book = Item(NpcSubpathQuest.StudyItem);
            Assert.Equal((2, (int)book.ClientIcon, 0), (boxes[^2].Head, boxes[^2].Gfx, boxes[^2].Color));

            string line = $"[SUBPATH]: Congratulations to our newest {Content.PathTitle(subpath, 1)} {c.Name}!";
            Assert.Contains((5, line), SystemLines(heard));
        }
        finally { _fx.World.LeaveMap(listener, listener.CharMap); }
    }

    // ---- leaving, at the Dog ------------------------------------------------------------------------------------

    /// <summary>The cleanse at your path's Dog returns an NPC subpath to its base class, rank kept, and takes its
    /// spell (RTK updatePath(player.baseClass, player.mark)). A base class that cleanses stays what it is.</summary>
    [Theory]
    [InlineData("Baekho", "Rogue")]
    [InlineData("Rogue", "Rogue")]
    public void TheDogsCleanseReturnsAnNpcSubpathToItsBase(string cls, string after)
    {
        var dogDef = Content.Npcs.Single(n => n.Key == "DogLinguistNpc" && n.Name == "Jindo dog");   // the Rogues' dog
        var dog = _fx.World.NpcsNear(dogDef.Map, dogDef.X, dogDef.Y, 0).First(m => m.NpcDefId == dogDef.Id);
        var cunning = Content.SpellByKey("baekhos_cunning")!;
        var (s, o, c) = Unplaced("Cleanse", ch =>
        {
            Joiner(ch, cls);
            ch.Mark = 2;
        });
        if (cls == "Baekho") s.WithState(() => Assert.True(s.LearnSpellFromNpc(cunning)));

        Assert.True(OnOwnThread(s, () => NpcScript.RunSayAsync(new NpcContext(s, dog, dogDef), "DogLinguistNpc", "cleanse"),
                                menuPick: 1));
        Assert.Contains("It is done.", Boxes(o).Select(b => b.Text));
        Assert.Equal(after, c.ClassName);
        Assert.Equal(2, c.Mark);
        Assert.False(s.KnowsSpellId(cunning.Id));
    }

    // ---- the four spells' prices and who is offered them --------------------------------------------------------

    /// <summary>tswolf's NPC spell page (Wayback 2001-02-24): each spell costs its subpath's key, two items and
    /// 50,000 coins, read with the BASE path, as the trainer reads it (<c>NpcContext.BasePathId</c>).</summary>
    [Theory]
    [InlineData("chung_ryongs_rage", "spike", "chung_ryong_key", "electra")]
    [InlineData("baekhos_cunning", "baekho_key", "blood", "lucky_silver_coin")]
    [InlineData("ju_jak_evocation", "surge", "ju_jak_key", "star_staff")]
    [InlineData("hyun_moo_revival", "purified_water", "hyun_moo_key", "titanium_lance")]
    public void EachNpcSubpathSpellCostsTswolfsPrice(string key, string item1, string item2, string item3)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.True(Content.IsNpcSubpath(sp.PathId));
        var cost = Content.LearnCostFor(sp, Content.PathBaseOf(sp.PathId));
        Assert.NotNull(cost);
        Assert.Equal(50_000, cost!.Gold);
        Assert.Equal(new[] { (item1, 1), (item2, 1), (item3, 1) }, cost.Items.Select(i => (i.Item, i.Amount)));
    }

    /// <summary>Each NPC-subpath spell is offered to its own subpath and to no other path, at any level, alignment
    /// or rank, although its learn row is keyed to the base class. Without the guard the base class is offered it.</summary>
    [Fact]
    public void EachNpcSubpathSpellIsOfferedOnlyToItsSubpath()
    {
        var spells = Content.Spells.Where(s => Content.IsNpcSubpath(s.PathId)).ToList();
        Assert.Equal(4, spells.Count);
        var wrong = new List<string>();
        for (int path = 0; path <= 22; path++)
        {
            if (Content.PathIdForClass(Content.PathName(path)) != path) continue;
            for (int align = 0; align <= 3; align++)
            for (int mark = 0; mark <= 3; mark++)
            foreach (int level in new[] { 98, 99 })
            {
                var offered = Content.SpellsForClass(path, level, align, mark).Select(s => s.Key).ToHashSet();
                foreach (var sp in spells)
                {
                    bool want = path == sp.PathId && level >= Content.MarkSpellLevel;
                    if (offered.Contains(sp.Key) != want)
                        wrong.Add($"{sp.Key} to path {path} align {align} mark {mark} level {level}: offered {!want}");
                }
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    /// <summary>A level-99 player of <paramref name="cls"/> with everything Baek asks for: Ox karma, the blessing, the
    /// Dog-linguist legend, a Tiger's favor, ten Splendid tiger pelts and two billion experience.</summary>
    private static void Joiner(Character c, string cls)
    {
        c.Level = 99;
        c.ClassName = cls;
        c.Karma = OxKarma;
        c.Exp = 2_000_000_000;
        QuestState.Over(c, QuestState.Registry).Set(NpcSubpathQuest.BlessedReg, 1);
        c.Legends.Add(new Legend(3, 128, "Dog linguist", NpcSubpathQuest.DogLegend));
        c.Inventory.Add(new InvItem(0, Item("tigers_favor").Id, 1));
        c.Inventory.Add(new InvItem(1, Item(NpcSubpathQuest.Pelt).Id, NpcSubpathQuest.PeltCount));
    }

    private static ItemDef Item(string key) => Content.ItemByKey(key) ?? throw new InvalidOperationException($"no item '{key}'");
    private static InvItem Bag(Character c, string key) => c.Inventory.Single(i => i.ItemId == Item(key).Id);
    private static int Count(Character c, string key) => c.Inventory.Where(i => i.ItemId == Item(key).Id).Sum(i => i.Amount);

    private static (int Favors, int Pelts, uint Exp, int Legends) Holdings(Character c) =>
        (NpcSubpathQuest.Favors.Sum(f => Count(c, f)), Count(c, NpcSubpathQuest.Pelt), c.Exp, c.Legends.Count);

    private static string Name(string stem) => $"{stem}{Interlocked.Increment(ref _serial)}";

    /// <summary>A socket-free session for a new character, built but not placed on a map.</summary>
    private (Session Session, RecordingOutbound Out, Character Character) Unplaced(string stem, Action<Character> configure)
    {
        var def = Content.NpcById(BaekNpcId)!;
        var c = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion,
            Id = _fx.World.AllocatePlayerId(),
            Name = Name(stem),
            Map = (ushort)def.Map, X = (ushort)def.X, Y = (ushort)(def.Y + 1),
        };
        c.SettingFlags &= ~Character.SettingBit(4);   // no advice lines (SessionFixture.PlayerWith says why)
        configure(c);
        var o = new RecordingOutbound($"recorder:{c.Name}");
        return (new Session(o, 2005, _fx.Store, _fx.World, c), o, c);
    }

    private Mob BaekMob()
    {
        var def = Content.NpcById(BaekNpcId)!;
        var mob = _fx.World.NpcsNear((ushort)def.Map, def.X, def.Y, 0).FirstOrDefault(m => m.NpcDefId == BaekNpcId);
        Assert.True(mob is not null, "Baek is not placed in the world");
        return mob!;
    }

    /// <summary>Say <paramref name="word"/> to Baek and answer every box with "next" until he is done. True if he
    /// consumed the word.</summary>
    private bool Say(Session s, string word)
    {
        var def = Content.NpcById(BaekNpcId)!;
        var ctx = new NpcContext(s, BaekMob(), def);
        return OnOwnThread(s, () => NpcSubpathAbility.Instance.OnSay(ctx, word));
    }

    /// <summary>Start a dialog inside the player's monitor on a thread of its own, then answer each box: the first
    /// menu with <paramref name="menuPick"/>, every text box with "next", until the dialog ends. Returns what the
    /// dialog returned (for a speech handler, whether it consumed the words).</summary>
    private static bool OnOwnThread(Session s, Func<Task<bool>> start, int menuPick = 0)
    {
        Task<bool>? flow = null;
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(() =>
        {
            s.WithState(() => { flow = start(); });
            for (int i = 0; i < 40 && !flow!.IsCompleted; i++)
                s.Receive(menuPick > 0 && i == 0 ? MenuFrame((byte)menuPick) : NextFrame());
        })) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(Bound), "the dialog thread never finished");
        Assert.Null(fault);
        Assert.True(flow!.IsCompletedSuccessfully, "the dialog was still waiting after forty answers, or failed");
        return flow.Result;
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

    private readonly record struct Box(int Head, int Gfx, int Color, string Text);

    /// <summary>Every text box (0x30 type 1) the player was shown, in order: the portrait (head kind, graphic,
    /// colour; Session.WriteHead) and the message (u16BE length at [21], ASCII from [23]). Menus are left out.</summary>
    private static List<Box> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog)
         .Where(b => b.Length >= 23 && b[0] == 0 && b[1] == 1 && b[7] == 1)
         .Select(b => new Box(b[6], (b[8] << 8) | b[9], b[10],
                              Encoding.ASCII.GetString(b, 23, Math.Min((b[21] << 8) | b[22], b.Length - 23))))
         .ToList();

    /// <summary>Every 0x0A line as (type, text): the type byte, the u16BE length, the ASCII.</summary>
    private static List<(int Type, string Text)> SystemLines(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.MiniText)
         .Where(b => b.Length >= 3)
         .Select(b => ((int)b[0], Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3))))
         .ToList();
}
