using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// The thirty aligned Sam san rows (Caleb, 2026-10-24, decision sheet item 7): the Kwi-Sin, Ming-Ken and Ohaeng rows
/// of the ten Sam san families that had only their unaligned row.
///
/// <para>The silent failure. <c>Content.SpellsForClass</c> offers a row only if it is universal or matches the
/// character's alignment, and ten Sam san families existed only at alignment 0: Health's return, Mend equipment,
/// Explorer's remedy, Focused Blow, Power stream, Dooms Fire, Energy flow, Charge of life, Ensure and Wind master. So an
/// aligned Sam san rogue, mage or poet was taught one Sam san spell and a warrior two (the universal rows and Siege's
/// twin), and nothing said so.</para>
///
/// <para>The names are the live Nexus Atlas class pages', each listed in its family's own block under the family's one
/// price: https://www.nexusatlas.com/spells/warrior.php (fetched 2026-10-21) and rogue.php, mage.php and poet.php
/// (fetched 2026-10-10); <c>game-data/Sources.csv</c> <c>atlas-warrior-spells</c>, <c>atlas-rogue-spells</c>,
/// <c>atlas-mage-spells</c>, <c>atlas-poet-spells</c>. Each new row copies its base's Spells.csv row (type, prompt, path, mark
/// 3) with alignment 1, 2 or 3, its base's spell_effects.csv row and SpellParams.csv row, and joins whatever its base
/// is keyed into in code: Focused Blow's <c>SacrificeAliases</c> entry, Dooms Fire's pool drain, Mend equipment's
/// verb. Wind master's three stay not castable, as Wind master is, until a source names its creature.</para>
/// </summary>
[Collection("world")]
public sealed class SamSanAlignedRowTests
{
    private const ushort CastMap = 62640, WindMap = 62641;
    private const ushort CX = 10, CY = 10;      // the caster, facing east at (CX + 1, CY)
    private const int Creature = 10_000_000;
    private const long SlackMs = 5_000;
    private const uint CasterMp = 60_001;

    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;
    private static int _serial;

    public SamSanAlignedRowTests(SessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>One Sam san family: its base key, its three rows in alignment order with the name the live page
    /// prints (Kwi-Sin, Ming-Ken, Ohaeng), and the family's learn price on that page. Spells.csv writes each name in
    /// title case, as Siege's three (Soul's Freedom, Life's End, Winter Chill) write the page's "Soul's freedom",
    /// "Life's end" and "Winter chill"; the spelling is the page's.</summary>
    public sealed record Family(string Base, (string Key, string Name)[] Rows, int Gold, (string Item, int Amount)[] Items);

    /// <summary>The ten families, each with the page it is listed on (https://www.nexusatlas.com/spells/&lt;class&gt;.php)
    /// and the price that page prints in the family's block.</summary>
    public static readonly Family[] Families =
    {
        // warrior.php, price "5 Mountain ginseng 50,000 Coins"
        new("healths_return_warrior", new[] { ("mending_of_the_soul_warrior", "Mending of the Soul"),
            ("spirits_salvation_warrior", "Spirit's Salvation"), ("gift_of_life_warrior", "Gift of Life") },
            50_000, new[] { ("mountain_ginseng", 5) }),
        // warrior.php, price "10 Fine cloth 10 Fine metal 150,000 Coins"
        new("mend_equipment_warrior", new[] { ("restore_equipment_warrior", "Restore Equipment"),
            ("luster_return_warrior", "Luster Return"), ("reforge_warrior", "Reforge") },
            150_000, new[] { ("fine_metal", 10), ("fine_cloth", 10) }),
        // rogue.php, price "4 Mountain Ginseng 100,000 Coins"
        new("explorers_remedy_rogue", new[] { ("kwisin_explorers_remedy_rogue", "Kwi-Sin Explorers Remedy"),
            ("mingken_explorers_remedy_rogue", "Ming Ken Explorers Remedy"), ("ohaeng_explorers_remedy_rogue", "Ohaeng Explorers Remedy") },
            100_000, new[] { ("mountain_ginseng", 4) }),
        // rogue.php, price "750,000 coins"
        new("focused_blow_rogue", new[] { ("burial_dance_rogue", "Burial Dance"),
            ("flowing_energy_rogue", "Flowing Energy"), ("freeform_rogue", "Freeform") },
            750_000, Array.Empty<(string, int)>()),
        // rogue.php, price "2 Mountain ginseng 100,000 coins"
        new("power_stream_rogue", new[] { ("phantoms_kiss_rogue", "Phantoms Kiss"),
            ("life_herbs_rogue", "Life Herbs"), ("meditation_rogue", "Meditation") },
            100_000, new[] { ("mountain_ginseng", 2) }),
        // mage.php, price "750,000 Coins"
        new("dooms_fire_mage", new[] { ("deaths_awakening_mage", "Deaths Awakening"),
            ("soul_rip_mage", "Soul Rip"), ("final_blow_mage", "Final Blow") },
            750_000, Array.Empty<(string, int)>()),
        // mage.php, price "10 Mountain ginseng 50,000 coins"
        new("energy_flow_mage", new[] { ("spirit_heal_mage", "Spirit Heal"),
            ("natures_milk_mage", "Natures Milk"), ("peaceful_bliss_mage", "Peaceful Bliss") },
            50_000, new[] { ("mountain_ginseng", 10) }),
        // poet.php, price "10 Mountain ginseng 100,000 coins"
        new("charge_of_life_poet", new[] { ("vital_charge_poet", "Vital Charge"),
            ("blessing_of_life_poet", "Blessing of Life"), ("sacred_soil_poet", "Sacred Soil") },
            100_000, new[] { ("mountain_ginseng", 10) }),
        // poet.php, price "10 Purple ring 100,000 Coins"
        new("ensure_poet", new[] { ("spirit_shield_poet", "Spirit Shield"),
            ("soul_guard_poet", "Soul Guard"), ("rock_blessing_poet", "Rock Blessing") },
            100_000, new[] { ("purple_ring", 10) }),
        // poet.php, price "350,000 Coins"
        new("cotw_wind_master_poet", new[] { ("kwisin_master_poet", "Kwi-Sin Master"),
            ("mingken_master_poet", "Ming-Ken Master"), ("ohaeng_master_poet", "Ohaeng Master") },
            350_000, Array.Empty<(string, int)>()),
    };

    /// <summary>The three bases whose learn rows #387 (<c>pr/learn-costs-1024</c>) moves to the live page's price
    /// (decision sheet item 8). Their rows here are at that price already, which until #387 merges is not their
    /// base's hand-written row; every other family's rows are at its base's row too.</summary>
    private static readonly HashSet<string> RepricedBy387 =
        new(StringComparer.Ordinal) { "explorers_remedy_rogue", "power_stream_rogue", "energy_flow_mage" };

    private const string WindMaster = "cotw_wind_master_poet";

    /// <summary>Every row: (base key, row key, alignment).</summary>
    public static TheoryData<string, string, int> AllRows
    {
        get
        {
            var data = new TheoryData<string, string, int>();
            foreach (var f in Families)
                for (int i = 0; i < 3; i++) data.Add(f.Base, f.Rows[i].Key, i + 1);
            return data;
        }
    }

    /// <summary>Wind master's three: (row key, alignment).</summary>
    public static TheoryData<string, int> WindMasterRows
    {
        get
        {
            var data = new TheoryData<string, int>();
            var f = Families.Single(x => x.Base == WindMaster);
            for (int i = 0; i < 3; i++) data.Add(f.Rows[i].Key, i + 1);
            return data;
        }
    }

    /// <summary>Each row is its base's Spells.csv row (path, type, prompt, can-fail, mark 3) under the live page's name
    /// at alignment 1, 2 or 3, with an id of its own. Red on 473789e: none of the thirty keys is loaded.</summary>
    [Fact]
    public void EachRowIsItsBasesRowUnderTheLivePagesName()
    {
        var ids = Content.Spells.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var problems = new List<string>();
        foreach (var f in Families)
        {
            var b = Content.SpellByKey(f.Base);
            Assert.True(b is not null, $"{f.Base} is not loaded");
            for (int i = 0; i < 3; i++)
            {
                var (key, name) = f.Rows[i];
                var t = Content.SpellByKey(key);
                if (t is null) { problems.Add($"{key}: not loaded"); continue; }
                var want = (name, b!.PathId, b.Type, b.Question, b.CanFail, Mark: 3, Alignment: i + 1, b.Level);
                var got = (t.Name, t.PathId, t.Type, t.Question, t.CanFail, t.Mark, t.Alignment, t.Level);
                if (want != got) problems.Add($"{key}: {got} (want {want})");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static readonly MethodInfo BuildAlignFamiliesMi =
        typeof(Content).GetMethod("BuildAlignFamilies", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The family map the ability ladders are built from (<c>Content.BuildAlignFamilies</c>, the server's own
    /// method): spell key to the key of the row leading its run.</summary>
    private static Dictionary<string, string> AlignFamilies(IReadOnlyList<SpellDef> spells) =>
        (Dictionary<string, string>)BuildAlignFamiliesMi.Invoke(null, new object[] { spells })!;

    /// <summary>No set of three joins another family. <c>Content.BuildAlignFamilies</c> links a spell to its base only by
    /// adjacency: within one (path, type) block, ordered by id, a run of ascending alignments after an alignment-0 row is
    /// that row's family, and <c>Content.BuildSpellLadders</c> spreads each ladder rung to its whole family. So each set's
    /// three must file under their own base (when their ids follow it) or under their own Kwi-Sin row (a run of their
    /// own), all three alike, and never under another key; and every row that was there before keeps the leader it has
    /// without the thirty. The bases' ids are consecutive, so only a set whose base ends its block's run can follow it:
    /// Focused Blow's, Power stream's, Ensure's and Wind master's do; the other six lead their own run. Red with the ids
    /// of 9e49491, where Explorer's remedy's three filed under Focused Blow and Charge of life's under Ensure. No base is
    /// a rung today, so the misfiling changed no book; this keeps a later rung from carrying the wrong set.</summary>
    [Fact]
    public void NoRowsIdRunFilesItUnderAnotherFamily()
    {
        var newKeys = Families.SelectMany(f => f.Rows.Select(r => r.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leaderOf = AlignFamilies(Content.Spells);
        var before = AlignFamilies(Content.Spells.Where(s => !newKeys.Contains(s.Key)).ToList());
        var problems = new List<string>();
        foreach (var f in Families)
        {
            var leaders = f.Rows.Select(r => leaderOf.GetValueOrDefault(r.Key, "(none)")).Distinct().ToList();
            if (leaders.Count != 1 || (leaders[0] != f.Base && leaders[0] != f.Rows[0].Key))
                problems.Add($"{f.Base}'s rows file under [{string.Join(", ", leaders)}] (want {f.Base} or {f.Rows[0].Key})");
            else
                _out.WriteLine($"{f.Base}: its three file under {leaders[0]}");
        }
        foreach (var (key, leader) in before)
            if (leaderOf.GetValueOrDefault(key) != leader)
                problems.Add($"{key} filed under {leader} without the thirty rows, now under {leaderOf.GetValueOrDefault(key)}");
        Assert.True(before.Count > 800, $"the family map holds only {before.Count} keys");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>A Sam san character of each playable class and subpath is offered its own alignment's rows of its base
    /// class's families and none of the other alignments' or the unaligned base rows, by the trainers' menu
    /// (<see cref="Content.SpellsForClass"/>) and by the rebuild <c>@mark 3</c> runs
    /// (<see cref="Content.RespecSpellSet"/>); an unaligned one keeps the base rows and gets none of the new ones; and
    /// below Sam san nobody gets either. Red on 473789e: an aligned Sam san character is offered none of the new
    /// rows (they do not exist) and none of the base rows, for every class.</summary>
    [Fact]
    public void AnAlignedSamSanCharacterIsOfferedItsOwnAlignmentsRowsOnly()
    {
        var allRows = Families.SelectMany(f => f.Rows.Select(r => r.Key)).ToHashSet(StringComparer.Ordinal);
        var bases = Families.Select(f => f.Base).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        int checkedPaths = 0;
        foreach (int path in Content.PlayablePaths.Where(p => p != 0).OrderBy(p => p))
        {
            checkedPaths++;
            int basePath = Content.PathBaseOf(path);
            var mine = Families.Where(f => Content.SpellByKey(f.Base)?.PathId == basePath).ToList();
            Assert.NotEmpty(mine);
            for (int alignment = 0; alignment <= 3; alignment++)
            {
                var wantRows = alignment == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : mine.Select(f => f.Rows[alignment - 1].Key).ToHashSet(StringComparer.Ordinal);
                var wantBases = alignment == 0
                    ? mine.Select(f => f.Base).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                foreach (var (what, book) in new[]
                {
                    ("offered", Content.SpellsForClass(path, Content.MarkSpellLevel, alignment, 3)),
                    ("rebuilt", Content.RespecSpellSet(path, Content.MarkSpellLevel, alignment, 3)),
                })
                {
                    var keys = book.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
                    var gotRows = keys.Where(allRows.Contains).ToHashSet(StringComparer.Ordinal);
                    var gotBases = keys.Where(bases.Contains).ToHashSet(StringComparer.Ordinal);
                    if (!gotRows.SetEquals(wantRows) || !gotBases.SetEquals(wantBases))
                        problems.Add($"path {path} alignment {alignment} {what}: rows [{string.Join(", ", gotRows.Order())}] " +
                                     $"(want [{string.Join(", ", wantRows.Order())}]), bases [{string.Join(", ", gotBases.Order())}] " +
                                     $"(want [{string.Join(", ", wantBases.Order())}])");
                }
                var below = Content.SpellsForClass(path, Content.MarkSpellLevel, alignment, 2).Select(s => s.Key);
                if (below.Any(k => allRows.Contains(k) || bases.Contains(k)))
                    problems.Add($"path {path} alignment {alignment}: offered a Sam san row at Ee san");
            }
        }
        Assert.Equal(8, checkedPaths);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Each row casts exactly as its base, driven end to end through <c>Session.Receive</c>: two casters set up
    /// the same way (a wounded level-99 holding 60,001 mana, a 0-AC creature on the faced tile, a worn-down piece of gear
    /// in the first pack slot), one unaligned with the base in its book and one of the row's alignment with the row, each
    /// casting book slot 0. A "Which target? &gt;" spell is aimed at the creature when its base is a damage spell and at
    /// the caster otherwise. Everything each caster is sent must match frame for frame (the row's name read as its
    /// base's; an entity id read as caster, creature or other), and so must what the cast left: the caster's vita and
    /// mana, what the creature lost, the gear's durability, the ward it put up, and the aether it armed. Wind master's
    /// three match it too, in sending nothing. Red on 473789e: none of the thirty keys is loaded.</summary>
    [Theory]
    [MemberData(nameof(AllRows))]
    public void EachRowCastsExactlyAsItsBase(string baseKey, string rowKey, int alignment)
    {
        var b = Content.SpellByKey(baseKey);
        var t = Content.SpellByKey(rowKey);
        Assert.True(b is not null && t is not null, $"{baseKey} or {rowKey} is not loaded");

        var want = Cast(b!, 0, b!);
        var got = Cast(t!, alignment, b!);
        _out.WriteLine($"{baseKey}: vita {want.Hp}, mana {want.Mp}, creature lost {want.CreatureLost}, dura {want.Dura}, " +
                       $"ward '{want.Ward}', aether {want.AetherMs} ms; frames [{string.Join(" | ", want.Frames)}]");
        _out.WriteLine($"{rowKey}: vita {got.Hp}, mana {got.Mp}, creature lost {got.CreatureLost}, dura {got.Dura}, " +
                       $"ward '{got.Ward}', aether {got.AetherMs} ms; frames [{string.Join(" | ", got.Frames)}]");

        if (baseKey != WindMaster)   // the base really went off (every one of them spends mana), so the match means something
            Assert.True(want.Mp < CasterMp, $"{baseKey} was refused: [{string.Join(" | ", want.Frames)}]");
        Assert.Equal(want.Frames, got.Frames);
        Assert.Equal((want.Hp, want.Mp, want.CreatureLost, want.Dura, want.Ward),
                     (got.Hp, got.Mp, got.CreatureLost, got.Dura, got.Ward));
        Assert.True(want.AetherMs == 0 ? got.AetherMs == 0 : Math.Abs(want.AetherMs - got.AetherMs) <= SlackMs,
                    $"{rowKey}: aether {got.AetherMs} ms, its base's {want.AetherMs} ms");
    }

    /// <summary>Wind master's three are refused as Wind master is: silently and for free. A cast from the book sends
    /// nothing back and changes nothing: no line, no pose, no mana, no aether, no cast/swing slot held, no creature on
    /// the map (<see cref="WindMasterUncastableTests"/> pins the same for Wind master). Red on 473789e: the keys are not
    /// loaded; and with their SpellParams.csv rows taken away they fall to the generic <c>misc</c> verb, as Wind master
    /// did before decision sheet item 3: "You cast Kwi-Sin Master.", the pose, 10 mana and the 480 s aether.</summary>
    [Theory]
    [MemberData(nameof(WindMasterRows))]
    public void WindMastersRowsAreRefusedSilentlyAndForFree(string key, int alignment)
    {
        const uint Mana = 10_000;
        var sp = Content.SpellByKey(key);
        Assert.NotNull(sp);
        var (session, outbound, c) = _fx.PlayerWith($"SamSanWind{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.Alignment = (byte)alignment;
            ch.Mark = 3;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Spells.Add(sp!.Id);
        }, WindMap, x: 5, y: 5);
        try
        {
            session.Receive(SpellCastSupport.CastFrame(0));

            string what = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], " +
                          $"poses {outbound.BodiesOf(ServerOp.Action).Count}, mana {c.Mp} (want {Mana}), " +
                          $"aether {SpellCastSupport.AetherLeft(session, key)} ms, slot {session.ActionSlotLeft} ms, " +
                          $"creatures {_fx.World.View(session, WindMap).mobs.Length}";
            Assert.True(SpellCastSupport.MiniTexts(outbound).Count == 0 && outbound.BodiesOf(ServerOp.Action).Count == 0
                        && c.Mp == Mana && SpellCastSupport.AetherLeft(session, key) == 0
                        && session.ActionSlotLeft == 0 && _fx.World.View(session, WindMap).mobs.Length == 0, what);
        }
        finally
        {
            _fx.World.LeaveMap(session, WindMap);
        }
    }

    /// <summary>Each row is learned from its class's trainer at level 99 for its family's price on the live page, which
    /// is the price of its base's row for the seven families #387 leaves alone; the three it moves (Explorer's remedy,
    /// Power stream, Energy flow) are at #387's price, the page's (decision sheet item 8). Red on 473789e: the rows do not
    /// exist. Without their learn rows a trainer would still list them (<c>SpellsForClass</c> falls back to the own-path
    /// rule) and teach them free, showing "FREE"; this is the fact that catches that.</summary>
    [Fact]
    public void EachRowIsLearnedAtItsFamilysPrice()
    {
        var problems = new List<string>();
        foreach (var f in Families)
        {
            int path = Content.SpellByKey(f.Base)!.PathId;
            if (!Content.SpellCosts.TryGetValue(f.Base, out var baseByPath) || !baseByPath.TryGetValue(path, out var baseCost))
            {
                problems.Add($"{f.Base}: no learn row for path {path}");
                continue;
            }
            var baseRow = (baseCost.Level, baseCost.Gold, Items(baseCost.Items));
            var page = (99, f.Gold, Items(f.Items));
            foreach (var (key, _) in f.Rows)
            {
                if (!Content.SpellCosts.TryGetValue(key, out var byPath) || !byPath.TryGetValue(path, out var cost))
                {
                    problems.Add($"{key}: no learn row for path {path}");
                    continue;
                }
                var got = (cost.Level, cost.Gold, Items(cost.Items));
                if (got != page) problems.Add($"{key}: {got} (want the page's {page})");
                if (!RepricedBy387.Contains(f.Base) && got != baseRow) problems.Add($"{key}: {got} (want its base's {baseRow})");
                if (byPath.Count != 1) problems.Add($"{key}: learn rows for {byPath.Count} paths");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>No rebuilt book passes the client's 52 slots (<c>docs/4.x/Protocol.md</c>, "Slot cap"): every playable
    /// path, at every alignment and every rank up to <see cref="Content.MaxMark"/>, at level 99 with the Dog spells
    /// (where its path may hold them) and every Sage rung. Level 99 is each path's largest book: a higher level only adds
    /// rows, and each ladder keeps one rung. A grant past the cap is cut silently (<c>Session.SyncSpellbook</c> takes the
    /// first <see cref="Content.SpellBookCap"/>), so the worst case is written to the test output. A guard: green on
    /// 473789e too, where the worst case was 51.</summary>
    [Fact]
    public void NoRebuiltBookPassesTheClientsSlots()
    {
        var sizes = new List<(int Count, int Path, int Alignment, int Mark)>();
        foreach (int path in Content.PlayablePaths)
            for (int alignment = 0; alignment <= 3; alignment++)
                for (int mark = 0; mark <= Content.MaxMark; mark++)
                    sizes.Add((Content.RespecSpellSet(path, Content.MarkSpellLevel, alignment, mark,
                                                      dogFlag: Content.CanLearnDogSpells(path),
                                                      sageRung: Content.SageLadder.Length).Count, path, alignment, mark));
        int top = sizes.Max(s => s.Count);
        var worst = sizes.Where(s => s.Count == top)
                         .Select(s => $"{Content.PathName(s.Path)} (path {s.Path}) alignment {s.Alignment} mark {s.Mark}").ToList();
        _out.WriteLine($"largest rebuilt book: {top} of {Content.SpellBookCap}: {string.Join("; ", worst)}");
        foreach (var s in sizes.Where(s => s.Mark == 3).OrderByDescending(s => s.Count).ThenBy(s => s.Path).ThenBy(s => s.Alignment))
            _out.WriteLine($"  Sam san {Content.PathName(s.Path)} (path {s.Path}) alignment {s.Alignment}: {s.Count}");
        Assert.True(top <= Content.SpellBookCap, $"a rebuilt book holds {top} of {Content.SpellBookCap} slots: {string.Join("; ", worst)}");
    }

    private static string Items((string Item, int Amount)[] items) =>
        string.Join(",", items.OrderBy(i => i.Item, StringComparer.Ordinal).Select(i => $"{i.Item}x{i.Amount}"));

    /// <summary>What one cast sent and left behind (see <see cref="EachRowCastsExactlyAsItsBase"/>).</summary>
    private sealed record Outcome(List<string> Frames, uint Hp, uint Mp, int CreatureLost, int Dura, string Ward, long AetherMs);

    /// <summary>Cast <paramref name="sp"/> from book slot 0 as a caster of <paramref name="alignment"/>, reading the
    /// frames as if <paramref name="asBase"/> had been cast (its name in place of <paramref name="sp"/>'s).</summary>
    private Outcome Cast(SpellDef sp, int alignment, SpellDef asBase)
    {
        const uint Hp = 9_001, MaxHp = 50_000, Mp = CasterMp;
        var gear = Content.Items.First(i => i.IsEquip && i.Durability > 1 && !i.Unrepairable);
        var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, CY, $"SamSan target {Interlocked.Increment(ref _serial)}", Creature);
        Assert.Equal(0, mob.Ac);
        _fx.World.AddMob(CastMap, mob);
        var (session, outbound, c) = _fx.PlayerWith($"SamSanCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.Alignment = (byte)alignment;
            ch.Mark = 3;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mp; ch.Mp = Mp;
            ch.Dir = 1;
            ch.MapXs = 100; ch.MapYs = 100;
            ch.Inventory.Add(new InvItem { Slot = 0, ItemId = gear.Id, Amount = 1, Dura = 1 });
            ch.Spells.Add(sp.Id);
        }, CastMap, CX, CY);
        try
        {
            uint? aim = !sp.NeedsTarget ? null
                      : Content.FxFor(asBase)?.Archetype == "Damage" ? mob.Id : c.Id;
            session.Receive(SpellCastSupport.CastFrame(0, aim));

            // Every frame in send order, decrypted as RecordingOutbound.BodiesOf decrypts it: a mini-text line as its
            // text, a frame that opens with an entity id (0x29 effect, 0x1A pose, 0x13 HP bar) with the id read as a
            // role, and anything else as its opcode and bytes.
            string Role(uint id) => id == c.Id ? "caster" : id == mob.Id ? "creature" : "other";
            var read = new List<string>();
            foreach (var frame in outbound.Frames)
            {
                if (!Protocol.Tk495.TkPacket.TryParse(frame, out var pkt, out _)) continue;
                byte op = pkt.Opcode;
                var body = Protocol.Tk495.TkCrypt.Crypt(pkt.Body, pkt.Increment, Protocol.Tk495.TkCrypt.LoginKey);
                if (op == ServerOp.MiniText)
                {
                    int len = body.Length >= 3 ? Math.Min((body[1] << 8) | body[2], body.Length - 3) : 0;
                    read.Add(Encoding.ASCII.GetString(body, 3, len).Replace(sp.Name, asBase.Name));
                }
                else if ((op == ServerOp.Effect || op == ServerOp.Action || op == ServerOp.Damage) && body.Length >= 4)
                    read.Add($"{op:x2} {Role((uint)(body[0] << 24 | body[1] << 16 | body[2] << 8 | body[3]))} " +
                             Convert.ToHexString(body, 4, body.Length - 4));
                else
                    read.Add($"{op:x2} {Convert.ToHexString(body)}");
            }

            var ward = SpellCastSupport.BuffEntry(session, sp.Key) is { } w
                ? $"{w.Category} {(w.LeftMs + 999) / 1000}s {w.Name.Replace(sp.Name, asBase.Name)}" : "";
            return new Outcome(read, c.Hp, c.Mp, Creature - mob.Hp, c.Inventory.Single(i => i.Slot == 0).Dura, ward,
                               SpellCastSupport.AetherLeft(session, sp.Key));
        }
        finally
        {
            _fx.World.LeaveMap(session, CastMap);
            _fx.World.DespawnMob(CastMap, mob);
        }
    }
}
