using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Druid spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", its smaller calls as RTK's;
/// P1 and P3 "on sale once it works, at its script's price"; approved again 2026-10-30). Before, Control flames, Druid's
/// rodent, Steed, Sea breeze, Nature's bloom and Energy bloom fell to the <c>generic</c> verb, 5 mana and "You cast X."
/// and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on <c>verb=generic</c>); Efflorescence,
/// which has no RTK script, still does. Energy bloom's facts are Earth strike's, in <c>SubpathDamageSpellTests</c>.
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Druid/</c>, one file each, bound by their
/// <c>game-data/SpellParams.csv</c> rows. Control flames and Nature's bloom act on a drop: while their wait runs, an acorn
/// or a flower their caster drops goes to the spell (RTK's <c>on_drop_while_aether</c>, <c>Session.RunDropHooks</c>).
/// Steed's stag is ridden by the ride key, as RTK's <c>onMount</c> rides one.</para>
///
/// <para>Every cast is the real 0x0F frame and every drop the real 0x08 frame through <c>Session.Receive</c>, by a fresh
/// level-99 Druid facing east. The scatter's object and blocked-ground fact needs terrain, so it runs on the Druid Guide
/// map (3631: no NPC stands on it and no other class uses it); the rest on content-free maps (no Maps.csv row, so PvP is
/// off, as in a town, and no terrain), one a fact, claimed by no other class.</para>
/// </summary>
[Collection("world")]
public sealed class DruidSpellTests
{
    private const ushort GuideMap = 3631;
    private const ushort FlameMap = 63270, FlameItemsMap = 63271, FlameShortMap = 63272, RodentMap = 63273,
                         SteedMap = 63274, BreezeMap = 63275, BloomMap = 63276, EdgeMap = 63277, WindowMap = 63278;
    // Fix round 1 (#417's review): content-free too, claimed by no other class.
    private const ushort KickMap = 63279, TradeMap = 63280, KickBloomMap = 63281, RelogMap = 63282;
    // Fix round 2 (#417's re-check): content-free too.
    private const ushort PartialMap = 63283;
    private const byte ExchangeIn = 0x4a, ExcOpen = 0, ExcItem = 1, ExcItemAmount = 2, ExcConfirm = 5;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const ushort CX = 30, CY = 30;
    private const uint MaxMana = 200_000, Mana = 200_000, Hp = 10_000;
    private const int Creature = 1_000_000;

    /// <summary>Slack on a wait or a run read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo MorphLookField =
        typeof(Session).GetField("_morphLook", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MorphUntilField =
        typeof(Session).GetField("_morphUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo AetherField =
        typeof(Session).GetField("_aether", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public DruidSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Control flames ======================================================================================

    /// <summary>The cast opens the window: 1,000 mana, a 62 s wait, "You cast Control flames."; inside the wait, "Control
    /// flames isn't ready yet." for free; short of 1,000, "Your will is too weak." for free. Red on f94a4cf: generic's 5
    /// mana and no wait.</summary>
    [Fact]
    public void ControlFlamesOpensItsWindowForItsMana()
    {
        var (caster, cOut, cc) = Caster("control_flames", FlameMap, CX, CY - 20);
        var (poor, pOut, pc) = Caster("control_flames", FlameMap, CX, CY - 16, mana: 999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "control_flames");
            Assert.True(cc.Mp == Mana - 1_000 && Lines(cOut) == "You cast Control flames." && wait > 62_000 - SlackMs && wait <= 62_000,
                        $"spent {Mana - cc.Mp}, lines [{Lines(cOut)}], wait {wait}");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "Control flames isn't ready yet." && cc.Mp == Mana - 1_000
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 999 && SpellCastSupport.AetherLeft(poor, "control_flames") == 0,
                        $"again [{Lines(cOut)}], spent {Mana - cc.Mp}; poor [{Lines(pOut)}]");
        }
        finally { Leave(FlameMap, caster, poor); }
    }

    /// <summary>Inside the window each acorn dropped is spent, one at a time, and none lands; one drop in four (the
    /// script's <c>math.random(1, 4) == 1</c>) burns every creature on the caster's four sides for 1,200, two on one side
    /// both, with graphic 45 over each, takes the drop's own 800 mana and says "You cast Wall of flames.", with sound 708
    /// at the caster. A creature two tiles off and an NPC beside the caster are never burned. Sixty drops: the flames
    /// rise at least once (all sixty missing is 0.75^60) and not every time. Red on f94a4cf: the sixty acorns lie on the
    /// floor and nothing burns.</summary>
    [Fact]
    public void AnAcornDroppedInTheWindowIsSpentAndSometimesBurnsEveryCreatureBeside()
    {
        var (caster, cOut, cc) = Caster("control_flames", FlameMap, CX, CY,
                                        configure: ch => Give(ch, 0, "acorn", 60));
        var beside = new[] { AddCreature(FlameMap, CX, CY - 1), AddCreature(FlameMap, CX + 1, CY), AddCreature(FlameMap, CX + 1, CY),
                             AddCreature(FlameMap, CX, CY + 1), AddCreature(FlameMap, CX - 1, CY) };
        var far = AddCreature(FlameMap, CX + 2, CY);
        var npc = AddCreature(FlameMap, CX - 1, CY, npc: true);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            for (int i = 0; i < 60; i++) caster.Receive(DropFrame(0));
            uint spent = Mana - 1_000 - cc.Mp;
            int rose = (int)(spent / 800);
            var lines = SpellCastSupport.MiniTexts(cOut);
            string what = $"spent {spent} after the cast ({rose} flames), lost [{string.Join(",", beside.Select(Lost))}], far lost " +
                          $"{Lost(far)}, NPC lost {Lost(npc)}, acorns left {Count(cc, "acorn")}, on the floor " +
                          $"{Floor(FlameMap, "acorn")}, lines [{string.Join(" | ", lines.Distinct())}]";
            Assert.True(spent % 800 == 0 && rose is > 0 and < 60 && beside.All(m => Lost(m) == 1_200 * rose) && Lost(far) == 0
                        && Lost(npc) == 0 && Count(cc, "acorn") == 0 && Floor(FlameMap, "acorn") == 0
                        && lines.Count(l => l == "You cast Wall of flames.") == rose
                        && lines.All(l => l is "You cast Wall of flames."), what);
            Assert.True(beside.All(m => Effects(cOut, m.Id).Count(e => e == 45) == rose)
                        && Sounds(cOut).Count(s => s == 708) == rose, $"graphic 45 over each [{string.Join(",",
                        beside.Select(m => Effects(cOut, m.Id).Count(e => e == 45)))}], sound 708 x{Sounds(cOut).Count(s => s == 708)}");
        }
        finally { Leave(FlameMap, caster); foreach (var m in beside.Append(far).Append(npc)) _fx.World.DespawnMob(FlameMap, m); }
    }

    /// <summary>Before the window an acorn drops as it always does; inside it any item but an acorn does too, a Rose
    /// landing on the floor; and once the window has run out an acorn drops again. Green on f94a4cf (nothing hooks a drop
    /// there); it pins that the hook leaves every other drop alone. Red with the window's end left unread.</summary>
    [Fact]
    public void OutsideTheWindowAnAcornDropsAndInsideItAnyOtherItemDrops()
    {
        var (caster, _, cc) = Caster("control_flames", FlameItemsMap, CX, CY,
                                     configure: ch => { Give(ch, 0, "acorn", 5); Give(ch, 1, "rose", 2); });
        try
        {
            caster.Receive(DropFrame(0));
            int before = Floor(FlameItemsMap, "acorn");
            caster.Receive(SpellCastSupport.CastFrame(0));
            caster.Receive(DropFrame(1));
            Assert.True(before == 1 && Count(cc, "acorn") == 4 && Floor(FlameItemsMap, "rose") == 1 && Count(cc, "rose") == 1,
                        $"acorn on the floor {before}, in the bag {Count(cc, "acorn")}; rose on the floor " +
                        $"{Floor(FlameItemsMap, "rose")}, in the bag {Count(cc, "rose")}");
            caster.WithState(() => ((Dictionary<string, long>)AetherField.GetValue(caster)!)["control_flames"] = Environment.TickCount64 - 1);
            caster.Receive(DropFrame(0));
            Assert.True(Floor(FlameItemsMap, "acorn") == 2 && Count(cc, "acorn") == 3,
                        $"after the window: acorns on the floor {Floor(FlameItemsMap, "acorn")}, in the bag {Count(cc, "acorn")}");
        }
        finally { Leave(FlameItemsMap, caster); }
    }

    /// <summary>Short of the drop's own 800 mana when the flames would rise, "Your will is too weak." and nothing burns; the
    /// acorn is spent all the same. With nobody beside the caster the flames take nothing and say nothing. Red on
    /// f94a4cf: the acorns land and no line is said.</summary>
    [Fact]
    public void ShortOfTheFlamesManaOrWithNobodyBesideNothingBurnsAndTheAcornIsSpent()
    {
        var (weak, wOut, wc) = Caster("control_flames", FlameShortMap, CX, CY, configure: ch => Give(ch, 0, "acorn", 40));
        var mob = AddCreature(FlameShortMap, CX + 1, CY);
        var (alone, aOut, ac) = Caster("control_flames", FlameShortMap, CX, CY + 10, configure: ch => Give(ch, 0, "acorn", 40));
        try
        {
            weak.Receive(SpellCastSupport.CastFrame(0));
            alone.Receive(SpellCastSupport.CastFrame(0));
            weak.WithState(() => wc.Mp = 799);
            wOut.Clear();
            aOut.Clear();
            for (int i = 0; i < 40; i++) { weak.Receive(DropFrame(0)); alone.Receive(DropFrame(0)); }
            var weakLines = SpellCastSupport.MiniTexts(wOut);
            Assert.True(weakLines.Count > 0 && weakLines.All(l => l == "Your will is too weak.") && wc.Mp == 799 && Lost(mob) == 0
                        && Count(wc, "acorn") == 0,
                        $"short: lines [{string.Join(" | ", weakLines)}], mana {wc.Mp}, lost {Lost(mob)}, acorns {Count(wc, "acorn")}");
            Assert.True(SpellCastSupport.MiniTexts(aOut).Count == 0 && ac.Mp == Mana - 1_000 && Count(ac, "acorn") == 0
                        && Floor(FlameShortMap, "acorn") == 0,
                        $"alone: lines [{Lines(aOut)}], spent {Mana - ac.Mp}, acorns {Count(ac, "acorn")}, floor {Floor(FlameShortMap, "acorn")}");
        }
        finally { Leave(FlameShortMap, weak, alone); _fx.World.DespawnMob(FlameShortMap, mob); }
    }

    // ===== Druid's rodent ======================================================================================

    /// <summary>Answered "rabbit" or "rat" (any case), the caster takes that look from the spell's Morphs.csv row, 21 or
    /// 91, for 155 s, with graphic 3 and sound 703 over them and "You cast Druid's rodent."; nothing is spent (the script
    /// checks its 30 and never takes it). Red on f94a4cf: generic's 5 mana and no form.</summary>
    [Theory]
    [InlineData("rabbit", 21)]
    [InlineData("Rat", 91)]
    public void DruidsRodentTakesTheAnsweredLook(string answer, int look)
    {
        var (caster, cOut, cc) = Caster("druids_rodent", RodentMap, CX, look == 21 ? CY : CY + 4);
        try
        {
            caster.Receive(AnswerFrame(0, answer));
            var (got, left) = Morph(caster);
            Assert.True(got == look && left > 155_000 - SlackMs && left <= 155_000 && cc.Mp == Mana
                        && Lines(cOut) == "You cast Druid's rodent." && Effects(cOut, cc.Id).Contains(3) && Sounds(cOut).Contains(703),
                        $"look {got} (want {look}), {left} ms, spent {Mana - cc.Mp}, lines [{Lines(cOut)}], graphics " +
                        $"[{string.Join(",", Effects(cOut, cc.Id))}], sounds [{string.Join(",", Sounds(cOut))}]");
        }
        finally { Leave(RodentMap, caster); }
    }

    /// <summary>A cast in a creature's form ends that form and does nothing else, silently and for free (the script's
    /// <c>checkIfCast(morphs)</c> and <c>removeDuras(morphs)</c>), whatever is answered; any answer but rabbit or rat does
    /// nothing, silently; short of 30, "Your will is too weak.". Red on f94a4cf: generic's "You cast" and 5 mana each
    /// time.</summary>
    [Fact]
    public void DruidsRodentEndsAFormAndOtherwiseAsksItsAnswer()
    {
        var (caster, cOut, cc) = Caster("druids_rodent", RodentMap, CX + 10, CY);
        var (poor, pOut, pc) = Caster("druids_rodent", RodentMap, CX + 10, CY + 4, mana: 29);
        try
        {
            caster.Receive(AnswerFrame(0, "rabbit"));
            bool was = caster.IsMorphed;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(AnswerFrame(0, "rat"));
            bool after = caster.IsMorphed;
            string ended = Lines(cOut);
            FreeSlot(caster);
            caster.Receive(AnswerFrame(0, "cat"));
            poor.Receive(AnswerFrame(0, "rat"));
            Assert.True(was && !after && ended == "" && !caster.IsMorphed && Lines(cOut) == "" && cc.Mp == Mana
                        && Lines(pOut) == "Your will is too weak." && !poor.IsMorphed && pc.Mp == 29,
                        $"morphed {was}, after the recast {after}, lines [{ended}]; 'cat' [{Lines(cOut)}] morphed {caster.IsMorphed}; " +
                        $"spent {Mana - cc.Mp}; poor [{Lines(pOut)}]");
        }
        finally { Leave(RodentMap, caster, poor); }
    }

    // ===== Steed ===============================================================================================

    /// <summary>Arawn's stag (mobs.csv <c>arawns_stag</c>) appears on the faced tile, a 110 s wait, "You cast Steed.",
    /// nothing spent; the ride key rides it away, as RTK's <c>onMount</c> rides a stag, and the ride key again sets a stag
    /// down, as RTK's <c>onDismount</c> sets down the creature ridden. Red on f94a4cf: 5 mana and no stag; and without the
    /// ride key taking a stag, "Good try, but there is nothing here that you can ride.".</summary>
    [Fact]
    public void SteedPutsArawnsStagOnTheFacedTileThatTheRideKeyRidesAndSetsDown()
    {
        var (caster, cOut, cc) = Caster("steed", SteedMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var stag = _fx.World.MobAt(SteedMap, CX + 1, CY);
            long wait = SpellCastSupport.AetherLeft(caster, "steed");
            Assert.True(stag is { Key: "arawns_stag" } && cc.Mp == Mana && Lines(cOut) == "You cast Steed."
                        && wait > 110_000 - SlackMs && wait <= 110_000,
                        $"faced: {stag?.Key ?? "nothing"}, spent {Mana - cc.Mp}, lines [{Lines(cOut)}], wait {wait}");
            cOut.Clear();
            caster.Receive(RideFrame());
            Assert.True(cc.Mounted && Lines(cOut) == "The powerful steed takes you where you want to go."
                        && _fx.World.MobAt(SteedMap, CX + 1, CY) is null, $"mounted {cc.Mounted}, lines [{Lines(cOut)}]");
            cOut.Clear();
            caster.Receive(RideFrame());
            var down = _fx.World.MobAt(SteedMap, CX + 1, CY);
            Assert.True(!cc.Mounted && Lines(cOut) == "You precariously step again onto the ground." && down is { Key: "arawns_stag" },
                        $"mounted {cc.Mounted}, lines [{Lines(cOut)}], set down: {down?.Key ?? "nothing"}");
            _fx.World.DespawnMob(SteedMap, down!);
        }
        finally { Leave(SteedMap, caster); }
    }

    /// <summary>With the faced tile taken, the stag comes on the caster's own tile (the script's else branch); short of
    /// 2,000, "Your will is too weak." and no stag. A horse ridden is set down a horse, as before. Red on f94a4cf: no stag
    /// either way.</summary>
    [Fact]
    public void SteedFallsBackToTheCastersTileAndRefusesShortOfManaAndAHorseStaysAHorse()
    {
        var (caster, _, _) = Caster("steed", SteedMap, CX + 10, CY);
        var block = AddCreature(SteedMap, CX + 11, CY);
        var (poor, pOut, pc) = Caster("steed", SteedMap, CX + 10, CY + 4, mana: 1_999);
        var (rider, rOut, rc) = Caster("steed", SteedMap, CX + 10, CY + 8);
        var horse = AddCreature(SteedMap, CX + 11, CY + 8, key: "horse");
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var own = _fx.World.MobAt(SteedMap, CX + 10, CY);
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(own is { Key: "arawns_stag" } && Lines(pOut) == "Your will is too weak." && pc.Mp == 1_999
                        && _fx.World.MobAt(SteedMap, CX + 11, CY + 4) is null && _fx.World.MobAt(SteedMap, CX + 10, CY + 4) is null,
                        $"own tile: {own?.Key ?? "nothing"}; poor [{Lines(pOut)}]");
            _fx.World.DespawnMob(SteedMap, own!);
            rider.Receive(RideFrame());
            rider.Receive(RideFrame());
            var down = _fx.World.MobAt(SteedMap, CX + 11, CY + 8);
            Assert.True(!rc.Mounted && down is { Key: "horse" }, $"lines [{Lines(rOut)}], set down: {down?.Key ?? "nothing"}");
            _fx.World.DespawnMob(SteedMap, down!);
        }
        finally { Leave(SteedMap, caster, poor, rider); _fx.World.DespawnMob(SteedMap, block); _fx.World.DespawnMob(SteedMap, horse); }
    }

    // ===== Sea breeze ==========================================================================================

    /// <summary>For 1,500 mana and a 75 s wait, "You cast Sea breeze.", then every player and every creature within 10 tiles
    /// of the caster on both axes, the caster too, gets graphic 15 and 600 vita, capped at the maximum, a player reading
    /// "&lt;caster&gt; cast Sea breeze on you." (the caster too, after its own line); a ghost reads the line and gets the
    /// graphic and no vita; sound 708 plays at the caster. A player and a creature 11 tiles off, and an NPC beside the
    /// caster, are left alone. On a map where nobody may attack anybody. Red on f94a4cf: generic's 5 mana and no heal.</summary>
    [Fact]
    public void SeaBreezeHealsEveryPlayerAndCreatureWithinTenTiles()
    {
        var (caster, cOut, cc) = Caster("sea_breeze", BreezeMap, CX, CY, configure: ch => ch.Hp = 1_000);
        var (near, nOut, nc) = Player(BreezeMap, CX + 10, CY - 10, ch => ch.Hp = 1_000);
        var (farP, fOut, fc) = Player(BreezeMap, CX + 11, CY, ch => ch.Hp = 1_000);
        var (ghost, gOut, gc) = Player(BreezeMap, CX - 3, CY, ch => ch.Hp = 0);
        var hurt = AddCreature(BreezeMap, CX, CY + 10, hp: 1_000, maxHp: 10_000);
        var almost = AddCreature(BreezeMap, CX - 5, CY - 5, hp: 9_800, maxHp: 10_000);
        var beyond = AddCreature(BreezeMap, CX - 11, CY, hp: 1_000, maxHp: 10_000);
        var npc = AddCreature(BreezeMap, CX + 1, CY, hp: 1_000, maxHp: 10_000, npc: true);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string N(RecordingOutbound o) => Lines(o).Replace(cc.Name, "<caster>", StringComparison.Ordinal);
            long wait = SpellCastSupport.AetherLeft(caster, "sea_breeze");
            Assert.True(N(cOut) == "You cast Sea breeze. | <caster> cast Sea breeze on you." && cc.Hp == 1_600 && cc.Mp == Mana - 1_500
                        && wait > 75_000 - SlackMs && wait <= 75_000,
                        $"caster: lines [{N(cOut)}], vita {cc.Hp}, spent {Mana - cc.Mp}, wait {wait}");
            Assert.True(N(nOut) == "<caster> cast Sea breeze on you." && nc.Hp == 1_600 && N(fOut) == "" && fc.Hp == 1_000
                        && N(gOut) == "<caster> cast Sea breeze on you." && gc.Hp == 0,
                        $"10 off: [{N(nOut)}] {nc.Hp}; 11 off: [{N(fOut)}] {fc.Hp}; ghost: [{N(gOut)}] {gc.Hp}");
            Assert.True(hurt.Hp == 1_600 && almost.Hp == 10_000 && beyond.Hp == 1_000 && npc.Hp == 1_000,
                        $"creatures: 10 off {hurt.Hp}, near full {almost.Hp}, 11 off {beyond.Hp}, NPC {npc.Hp}");
            Assert.True(new[] { cc.Id, nc.Id, gc.Id, hurt.Id, almost.Id }.All(id => Effects(cOut, id).Contains(15))
                        && !Effects(cOut, fc.Id).Contains(15) && !Effects(cOut, npc.Id).Contains(15) && Sounds(cOut).Contains(708),
                        "graphic 15 over each healed and none over the two left alone, and sound 708");
        }
        finally
        {
            Leave(BreezeMap, caster, near, farP, ghost);
            foreach (var m in new[] { hurt, almost, beyond, npc }) _fx.World.DespawnMob(BreezeMap, m);
        }
    }

    /// <summary>Inside its wait, "Sea breeze isn't ready yet." for free; short of 1,500, "Your will is too weak." for free.
    /// Red on f94a4cf: generic's "You cast" and 5 mana.</summary>
    [Fact]
    public void SeaBreezeRefusesInsideItsWaitAndShortOfMana()
    {
        var (caster, cOut, cc) = Caster("sea_breeze", BreezeMap, CX + 40, CY + 40);
        var (poor, pOut, pc) = Caster("sea_breeze", BreezeMap, CX + 40, CY - 20, mana: 1_499);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "Sea breeze isn't ready yet." && cc.Mp == Mana - 1_500 && Lines(pOut) == "Your will is too weak."
                        && pc.Mp == 1_499, $"again [{Lines(cOut)}], spent {Mana - cc.Mp}; poor [{Lines(pOut)}]");
        }
        finally { Leave(BreezeMap, caster, poor); }
    }

    // ===== Nature's bloom ======================================================================================

    /// <summary>The cast opens the window: 200 mana, a 625 s wait, "You cast Nature's bloom."; inside it a non-flower (an
    /// acorn) drops as always. Red on f94a4cf: generic's 5 mana and no wait.</summary>
    [Fact]
    public void NaturesBloomOpensItsWindowForItsMana()
    {
        var (caster, cOut, cc) = Caster("natures_bloom", WindowMap, CX, CY, configure: ch => Give(ch, 0, "acorn", 2));
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "natures_bloom");
            caster.Receive(DropFrame(0));
            Assert.True(cc.Mp == Mana - 200 && Lines(cOut) == "You cast Nature's bloom." && wait > 625_000 - SlackMs && wait <= 625_000
                        && Floor(WindowMap, "acorn") == 1 && Count(cc, "acorn") == 1,
                        $"spent {Mana - cc.Mp}, lines [{Lines(cOut)}], wait {wait}, acorn on the floor {Floor(WindowMap, "acorn")}");
        }
        finally { Leave(WindowMap, caster); }
    }

    /// <summary>Inside the window a Rose dropped is spent, one of it, and what lands is Roses only: one on the caster's
    /// tile, and more within 4 tiles on both axes (each of the 81 a coin flip, so none at all is 2^-81), nothing further
    /// off; no line. Red on f94a4cf: one Rose lands on the caster's tile and nothing else.</summary>
    [Fact]
    public void AFlowerDroppedInTheWindowIsSpentAndScattersThatFlower()
    {
        var (caster, cOut, cc) = Caster("natures_bloom", BloomMap, CX, CY, configure: ch => Give(ch, 0, "rose", 3));
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            caster.Receive(DropFrame(0));
            var items = _fx.World.ItemsOn(BloomMap);
            int rose = Item("rose").Id;
            string what = $"{items.Length} on the floor, {items.Count(i => i.ItemId == rose)} roses, on the caster's tile " +
                          $"{items.Count(i => i.X == CX && i.Y == CY)}, furthest {items.Select(i => Math.Max(Math.Abs(i.X - CX), Math.Abs(i.Y - CY))).DefaultIfEmpty(0).Max()}, " +
                          $"roses left {Count(cc, "rose")}, lines [{Lines(cOut)}]";
            Assert.True(items.All(i => i.ItemId == rose && i.Amount == 1 && i.Owner == "") && items.Length > 1
                        && items.Any(i => i.X == CX && i.Y == CY) && items.All(i => Math.Abs(i.X - CX) <= 4 && Math.Abs(i.Y - CY) <= 4)
                        && Count(cc, "rose") == 2 && Lines(cOut) == "", what);
        }
        finally { Leave(BloomMap, caster); }
    }

    /// <summary>The scatter passes over a tile that carries a map object or is blocked, as the script's
    /// <c>getObject</c> and <c>getPass</c> tests do: on the Druid Guide map, standing where the 9x9 around the caster holds
    /// such tiles, six flowers dropped put none on one. Red with the object read taken out of
    /// <c>Session.RtkTileOpen</c>.</summary>
    [Fact]
    public void TheScatterPassesOverObjectsAndBlockedGround()
    {
        var md = MapData.For(GuideMap)!;
        var (x, y, objects, blocked) = BloomSpot(md);
        var (caster, _, _) = Caster("natures_bloom", GuideMap, x, y, configure: ch => Give(ch, 0, "sunflower", 6));
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            for (int i = 0; i < 6; i++) caster.Receive(DropFrame(0));
            int flower = Item("sunflower").Id;
            var down = _fx.World.ItemsOn(GuideMap).Where(i => i.ItemId == flower).ToList();
            var wrong = down.Where(i => md.Obj(i.X, i.Y) != 0 || md.Pass(i.X, i.Y) != 0
                                        || Content.TryWarp(GuideMap, i.X, i.Y, out _)).ToList();
            Assert.True(down.Count > 6 && wrong.Count == 0,
                        $"at ({x},{y}), {objects} object and {blocked} blocked tiles around: {down.Count} down, on a closed tile " +
                        string.Join(" ", wrong.Select(i => $"({i.X},{i.Y})")));
            foreach (var i in down) _fx.World.PickUp(GuideMap, i.X, i.Y);
        }
        finally { Leave(GuideMap, caster); }
    }

    /// <summary>Near the map's edge a tile past it is read as the edge tile, as the script clamps x and y to 0: at (1,1)
    /// the corner (0,0) takes sixteen rolls and each other edge tile within reach four, so one flower dropped puts two or
    /// more on some tile other than the caster's (none doing so is about 10^-9). Red with tiles past the edge skipped
    /// instead.</summary>
    [Fact]
    public void NearTheEdgeTheScatterReadsTheEdgeTile()
    {
        var (caster, _, _) = Caster("natures_bloom", EdgeMap, 1, 1, configure: ch => Give(ch, 0, "daffodil", 1));
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            caster.Receive(DropFrame(0));
            var piles = _fx.World.ItemsOn(EdgeMap).GroupBy(i => (i.X, i.Y)).ToDictionary(g => g.Key, g => g.Count());
            Assert.True(piles.Where(p => p.Key != (1, 1)).Any(p => p.Value >= 2) && piles.Keys.All(k => k.X <= 5 && k.Y <= 5),
                        string.Join(" ", piles.OrderBy(p => p.Key).Select(p => $"({p.Key.X},{p.Key.Y})x{p.Value}")));
        }
        finally { Leave(EdgeMap, caster); }
    }

    // ===== fix round 1: the drop in the Lua gate's gap (#417's review, F1) =======================================

    /// <summary>A drop that waits at the Lua gate lets the dropper's monitor go while it waits (the gate's slow path), and
    /// a second login's kick can run in that gap: it writes the row with the item still in the bag and closes the session.
    /// A Druid in Control flames' window drops a Rose (the hook declines it) while another thread holds the gate; the kick
    /// runs in the gap; the gate is let go. One Rose stays one: in the saved row, and not on the floor. Red without the
    /// going-away check after the hooks: the drop lands the Rose it read before the gap, one in the row and one on the
    /// floor (the review's probe, ported).</summary>
    [Fact]
    public void ADropRacingARelogInTheGatesGapKeepsOneRose()
    {
        string name = $"DruidKick{Interlocked.Increment(ref _serial)}";
        var (caster, _, _) = CasterNamed(name, "control_flames", KickMap, CX, CY, ch => Give(ch, 1, "rose", 1));
        var gap = new GateGap();
        Thread? kick = null;
        Exception? kickFault = null;
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(SpellCastSupport.AetherLeft(caster, "control_flames") > 0, "no window");
            bool waited = gap.DropWhileHeld(caster, DropFrame(1));
            kick = new Thread(() => { try { caster.KickForReplacement(); } catch (Exception e) { kickFault = e; } })
                   { IsBackground = true, Name = "kick" };
            kick.Start();
            bool inGap = kick.Join(TimeSpan.FromSeconds(2)) && gap.DropAlive;
            gap.Release();
            Assert.True(kick.Join(Bound), "the kick never finished");
            Assert.Null(kickFault);

            int rose = Item("rose").Id;
            int inRow = _fx.Store.Load(name).Character!.Inventory.Where(i => i.ItemId == rose).Sum(i => i.Amount);
            int onFloor = Floor(KickMap, "rose");
            Assert.True(waited && inGap && inRow + onFloor == 1 && onFloor == 0,
                        $"drop waited at the gate {waited}, kick ran in the gap {inGap}; roses: in the saved row {inRow}, on the floor {onFloor}");
        }
        finally
        {
            gap.Close();
            kick?.Join(Bound);
            foreach (var i in _fx.World.ItemsOn(KickMap)) _fx.World.PickUp(KickMap, i.X, i.Y);
            Leave(KickMap, caster);
        }
    }

    /// <summary>The same gap with an exchange: the Druid has offered the Rose and confirmed; the drop waits at the gate; the
    /// partner's confirm finishes the exchange in the gap (the pair takes the Druid's monitor) and the Rose goes to the
    /// partner. One Rose stays one: the partner's, none on the floor. Red without the slot read again after the hooks: the
    /// drop lands the stack the exchange had moved (the review's probe, ported).</summary>
    [Fact]
    public void ADropRacingAnExchangeInTheGatesGapKeepsOneRose()
    {
        var (o, _, oc) = CasterNamed($"DruidTradeO{Interlocked.Increment(ref _serial)}", "control_flames", TradeMap, 5, 5,
                                     ch => Give(ch, 1, "rose", 1));
        var (p, _, pc) = Player(TradeMap, 6, 5);
        var gap = new GateGap();
        Thread? confirm = null;
        Exception? confirmFault = null;
        try
        {
            o.Receive(SpellCastSupport.CastFrame(0));
            o.Receive(ExchangeRequest(ExcOpen, p.PlayerId));
            o.Receive(ExchangeRequest(ExcItem, p.PlayerId, new byte[] { 2 }));     // wire slot 2 = bag slot 1, the Rose
            o.Receive(ExchangeRequest(ExcConfirm, p.PlayerId));
            bool waited = gap.DropWhileHeld(o, DropFrame(1));
            confirm = new Thread(() => { try { p.Receive(ExchangeRequest(ExcConfirm, o.PlayerId)); } catch (Exception e) { confirmFault = e; } })
                      { IsBackground = true, Name = "partner confirm" };
            confirm.Start();
            bool inGap = confirm.Join(TimeSpan.FromSeconds(2)) && gap.DropAlive;
            gap.Release();
            Assert.True(confirm.Join(Bound), "the confirm never finished");
            Assert.Null(confirmFault);

            int partner = Count(pc, "rose"), druid = Count(oc, "rose"), onFloor = Floor(TradeMap, "rose");
            Assert.True(waited && inGap && partner + druid + onFloor == 1 && partner == 1,
                        $"drop waited at the gate {waited}, exchange finished in the gap {inGap}; roses: partner {partner}, " +
                        $"Druid {druid}, on the floor {onFloor}");
        }
        finally
        {
            gap.Close();
            confirm?.Join(Bound);
            foreach (var i in _fx.World.ItemsOn(TradeMap)) _fx.World.PickUp(TradeMap, i.X, i.Y);
            Leave(TradeMap, o, p);
        }
    }

    /// <summary>A hook that runs after the gap on a session a kick has closed spends nothing and puts nothing down: a Druid
    /// in Nature's bloom's window drops a Rose, the kick runs while the drop waits at the gate, and then the hook runs. The
    /// saved row keeps the Rose and no Rose lies on the floor. Red without the going-away check in
    /// <c>Session.LuaTakeDropped</c>: the hook spends the Rose the row still holds and scatters Roses.</summary>
    [Fact]
    public void AKickedSessionScattersNothing()
    {
        string name = $"DruidKickBloom{Interlocked.Increment(ref _serial)}";
        var (caster, _, _) = CasterNamed(name, "natures_bloom", KickBloomMap, CX, CY, ch => Give(ch, 1, "rose", 1));
        var gap = new GateGap();
        Thread? kick = null;
        Exception? kickFault = null;
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            bool waited = gap.DropWhileHeld(caster, DropFrame(1));
            kick = new Thread(() => { try { caster.KickForReplacement(); } catch (Exception e) { kickFault = e; } })
                   { IsBackground = true, Name = "kick" };
            kick.Start();
            bool inGap = kick.Join(TimeSpan.FromSeconds(2)) && gap.DropAlive;
            gap.Release();
            Assert.True(kick.Join(Bound), "the kick never finished");
            Assert.Null(kickFault);

            int rose = Item("rose").Id;
            int inRow = _fx.Store.Load(name).Character!.Inventory.Where(i => i.ItemId == rose).Sum(i => i.Amount);
            int onFloor = Floor(KickBloomMap, "rose");
            Assert.True(waited && inGap && inRow == 1 && onFloor == 0,
                        $"drop waited at the gate {waited}, kick ran in the gap {inGap}; roses: in the saved row {inRow}, on the floor {onFloor}");
        }
        finally
        {
            gap.Close();
            kick?.Join(Bound);
            foreach (var i in _fx.World.ItemsOn(KickBloomMap)) _fx.World.PickUp(KickBloomMap, i.X, i.Y);
            Leave(KickBloomMap, caster);
        }
    }

    /// <summary>A drop-all decided after the gap drops what the stack holds then (#417's re-check, F5). A Druid in Control
    /// flames' window holds five Roses in one stack, offers three of them and confirms, then drops the whole stack (D)
    /// while another thread holds the Lua gate (the flames hook declines a Rose). The partner's confirm finishes the
    /// exchange in the gap: three Roses leave that same stack, in place, and two stay. The drop then lands the two. Five
    /// Roses stay five: the partner 3, the Druid none, the floor 2. Red with the count read before the gap: five land, and
    /// five Roses become eight (the re-check's probe N2, ported).</summary>
    [Fact]
    public void ADropAllAfterAnExchangeTookPartOfTheStackInTheGapLandsWhatIsLeft()
    {
        var (o, _, oc) = CasterNamed($"DruidPartO{Interlocked.Increment(ref _serial)}", "control_flames", PartialMap, 5, 5,
                                     ch => Give(ch, 1, "rose", 5));
        var (p, _, pc) = Player(PartialMap, 6, 5);
        var gap = new GateGap();
        Thread? confirm = null;
        Exception? confirmFault = null;
        try
        {
            o.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(SpellCastSupport.AetherLeft(o, "control_flames") > 0, "no window");
            o.Receive(ExchangeRequest(ExcOpen, p.PlayerId));
            o.Receive(ExchangeRequest(ExcItemAmount, p.PlayerId, new byte[] { 2, 3 }));   // wire slot 2 = bag slot 1, three of five
            o.Receive(ExchangeRequest(ExcConfirm, p.PlayerId));
            bool waited = gap.DropWhileHeld(o, DropAllFrame(1));
            confirm = new Thread(() => { try { p.Receive(ExchangeRequest(ExcConfirm, o.PlayerId)); } catch (Exception e) { confirmFault = e; } })
                      { IsBackground = true, Name = "partner confirm" };
            confirm.Start();
            bool inGap = confirm.Join(TimeSpan.FromSeconds(2)) && gap.DropAlive;
            gap.Release();
            Assert.True(confirm.Join(Bound), "the confirm never finished");
            Assert.Null(confirmFault);

            int partner = Count(pc, "rose"), druid = Count(oc, "rose"), onFloor = Floor(PartialMap, "rose");
            Assert.True(waited && inGap && partner == 3 && druid == 0 && onFloor == 2,
                        $"drop waited at the gate {waited}, exchange finished in the gap {inGap}; roses: partner {partner}, " +
                        $"Druid {druid}, on the floor {onFloor} (of 5)");
        }
        finally
        {
            gap.Close();
            confirm?.Join(Bound);
            foreach (var i in _fx.World.ItemsOn(PartialMap)) _fx.World.PickUp(PartialMap, i.X, i.Y);
            Leave(PartialMap, o, p);
        }
    }

    // ===== fix round 1: the stag across a relog (#417's review, F2; the project owner's Q-G, "As RTK") ===========

    /// <summary>A Druid rides Arawn's stag and logs in again: the row keeps the creature ridden (Character.MountKey, as
    /// RTK keeps it in the saved registry), so the next login, mounted, sets a stag down. An older row with no such field
    /// loads as a horse. Red with the field left out of the row: the stag comes back a horse (the review's probe,
    /// ported).</summary>
    [Fact]
    public void AStagRiddenAcrossARelogIsSetDownAStag()
    {
        string name = $"DruidRelog{Interlocked.Increment(ref _serial)}";
        var (caster, _, cc) = CasterNamed(name, "steed", RelogMap, CX, CY);
        Session? again = null;
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.Equal("arawns_stag", _fx.World.MobAt(RelogMap, CX + 1, CY)?.Key);
            caster.Receive(RideFrame());
            Assert.True(cc.Mounted, "not mounted");
            caster.KickForReplacement();
            Leave(RelogMap, caster);

            var row = _fx.Store.Load(name).Character!;
            (again, _, _) = _fx.PlayerWith(name + "b", ch =>
            {
                ch.Mounted = row.Mounted;
                ch.MountKey = row.MountKey;
                ch.ClassName = "Druid"; ch.Level = 99; ch.Dir = 1; Size(ch, RelogMap);
            }, RelogMap, CX, CY);
            again.Receive(RideFrame());
            var down = _fx.World.MobAt(RelogMap, CX + 1, CY);

            string json = CharacterStore.Serialize(row);
            var old = CharacterStore.Deserialize(json.Replace(",\"MountKey\":\"arawns_stag\"", ""));
            Assert.True(row.Mounted && row.MountKey == "arawns_stag" && down?.Key == "arawns_stag" && old.Mounted && old.MountKey == "",
                        $"row Mounted {row.Mounted}, MountKey '{row.MountKey}'; after the relog's ride key: set down {down?.Key ?? "nothing"}; " +
                        $"a row without the field: Mounted {old.Mounted}, MountKey '{old.MountKey}'");
        }
        finally
        {
            foreach (var m in new[] { _fx.World.MobAt(RelogMap, CX + 1, CY), _fx.World.MobAt(RelogMap, CX, CY) })
                if (m is not null) _fx.World.DespawnMob(RelogMap, m);
            if (again is not null) Leave(RelogMap, again);
        }
    }

    // ===== the teacher =========================================================================================

    /// <summary>The Druid teacher's list by rank, each at its script's level: eight from mark 0 (Druid's rodent and Sea
    /// breeze at 33, the rest at 99) and Energy bloom from mark 1, its row's rank mark. Efflorescence, which has no script,
    /// is not on sale. Red without the price rows: Earth strike, Lightning storm and Nature's smite only.</summary>
    [Theory]
    [InlineData(0, "control_flames druids_rodent earth_strike lightning_storm natures_bloom natures_smite sea_breeze steed")]
    [InlineData(1, "control_flames druids_rodent earth_strike energy_bloom lightning_storm natures_bloom natures_smite sea_breeze steed")]
    public void TheDruidTeacherSellsByRank(int mark, string expected)
    {
        var sold = Content.SubpathTeacherSpells(Content.PathIdForClass("Druid"), 99, mark);
        Assert.Equal(expected, string.Join(" ", sold.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
        Assert.All(sold, s => Assert.Equal(s.Key is "druids_rodent" or "sea_breeze" ? 33 : 99, s.Level));
    }

    // ===== helpers =============================================================================================

    /// <summary>A tile on <paramref name="md"/> the caster may stand on (open, no warp) at least 4 from every edge, whose
    /// 9x9 holds the most tiles with a map object and blocked tiles: both kinds there.</summary>
    private static (int X, int Y, int Objects, int Blocked) BloomSpot(MapData md)
    {
        (int, int, int, int) best = default;
        int bestScore = -1;
        for (int y = 4; y < md.Ys - 4; y++)
            for (int x = 4; x < md.Xs - 4; x++)
            {
                if (md.Obj(x, y) != 0 || md.Pass(x, y) != 0 || Content.TryWarp(GuideMap, (ushort)x, (ushort)y, out _)) continue;
                int objects = 0, blocked = 0;
                for (int ty = y - 4; ty <= y + 4; ty++)
                    for (int tx = x - 4; tx <= x + 4; tx++)
                    {
                        if (md.Obj(tx, ty) != 0) objects++;
                        if (md.Pass(tx, ty) != 0) blocked++;
                    }
                int score = Math.Min(objects, blocked);
                if (score > bestScore) { bestScore = score; best = (x, y, objects, blocked); }
            }
        Assert.True(bestScore >= 2, $"no tile on map {GuideMap} has both kinds around it");
        return best;
    }

    private static byte[] DropFrame(int slot) => SessionFixture.Frame(ClientOp.DropItem, new byte[] { (byte)(slot + 1), 0, 0 });

    /// <summary>The drop-all (D) frame: the all flag set.</summary>
    private static byte[] DropAllFrame(int slot) => SessionFixture.Frame(ClientOp.DropItem, new byte[] { (byte)(slot + 1), 1, 0 });

    private static byte[] RideFrame() => SessionFixture.Frame(ClientOp.Setting, new byte[] { 0x00, 0x01, 0x00 });

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>An exchange request frame (0x4a): the sub-command, the other player's id, then its tail.</summary>
    private static byte[] ExchangeRequest(byte sub, uint targetId, byte[]? tail = null) =>
        SessionFixture.Frame(ExchangeIn, new byte[] { sub }.Concat(Be32(targetId)).Concat(tail ?? new byte[] { 0 }).ToArray());

    /// <summary>The Lua gate held by a thread of its own, and a drop sent on another while it is held, so the drop's hook
    /// waits at the gate with the dropper's monitor let go (the gate's slow path): the gap #417's review found.</summary>
    private sealed class GateGap
    {
        private readonly ManualResetEventSlim _held = new(), _release = new();
        private Thread? _holder, _drop;
        private Exception? _dropFault;

        public bool DropAlive => _drop?.IsAlive ?? false;

        /// <summary>Take the gate, send <paramref name="frame"/> through <paramref name="s"/> on its own thread and answer
        /// whether that drop is now waiting (blocked and still running).</summary>
        public bool DropWhileHeld(Session s, byte[] frame)
        {
            _holder = new Thread(() => { using (Session.EnterScriptGate()) { _held.Set(); _release.Wait(Bound); } })
                      { IsBackground = true, Name = "gate holder" };
            _holder.Start();
            Assert.True(_held.Wait(Bound), "the gate was never taken");
            _drop = new Thread(() => { try { s.Receive(frame); } catch (Exception e) { _dropFault = e; } })
                    { IsBackground = true, Name = "drop" };
            _drop.Start();
            return SpinWait.SpinUntil(() => (_drop.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0 || !_drop.IsAlive, Bound)
                   && _drop.IsAlive;
        }

        /// <summary>Let the gate go and wait for the drop to finish, which must end without a fault.</summary>
        public void Release()
        {
            Close();
            if (_drop is not null) Assert.False(_drop.IsAlive, "the drop never finished");
            Assert.Null(_dropFault);
        }

        /// <summary>Let the gate go and wait for both threads, asserting nothing (for a <c>finally</c>).</summary>
        public void Close()
        {
            _release.Set();
            _holder?.Join(Bound);
            _drop?.Join(Bound);
        }
    }

    /// <summary>The 0x0F cast frame for a "Become what?" spell: the slot byte, then the typed answer, NUL-terminated
    /// (RTK clif_parsemagic, <c>Session.HandleCast</c>).</summary>
    private static byte[] AnswerFrame(int slot, string answer)
    {
        var body = new List<byte> { (byte)(slot + 1) };
        body.AddRange(Encoding.ASCII.GetBytes(answer));
        body.Add(0);
        return SessionFixture.Frame(ClientOp.Cast, body.ToArray());
    }

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    private static uint Id(byte[] body) => (uint)((body[0] << 24) | (body[1] << 16) | (body[2] << 8) | body[3]);

    /// <summary>The spell graphics (0x29) <paramref name="o"/> was sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound o, uint id) =>
        o.BodiesOf(ServerOp.Effect).Where(b => b.Length >= 5 && Id(b) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    /// <summary>The sound ids <paramref name="o"/> was sent: the 0x19 body is type, 3, then the id as a u16 big-endian
    /// (<c>Session.SendSound</c>).</summary>
    private static List<int> Sounds(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.Audio).Where(b => b.Length >= 4 && b[0] == 0).Select(b => (b[2] << 8) | b[3]).ToList();

    private static (int Look, long LeftMs) Morph(Session s)
    {
        (int, long) got = default;
        s.WithState(() => got = ((ushort)MorphLookField.GetValue(s)!, (long)MorphUntilField.GetValue(s)! - Environment.TickCount64));
        return got;
    }

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    private static ItemDef Item(string key)
    {
        var def = Content.ItemByKey(key);
        Assert.True(def is not null, $"no item '{key}'");
        return def!;
    }

    private static void Give(Character c, byte slot, string key, int amount) =>
        c.Inventory.Add(new InvItem { Slot = slot, ItemId = Item(key).Id, Amount = amount, Dura = Item(key).Durability });

    private static int Count(Character c, string key) => c.Inventory.Where(i => i.ItemId == Item(key).Id).Sum(i => i.Amount);

    private int Floor(ushort map, string key) => _fx.World.ItemsOn(map).Where(i => i.ItemId == Item(key).Id).Sum(i => i.Amount);

    private static int Lost(Mob m) => m.MaxHp - m.Hp;

    private Mob AddCreature(ushort map, int x, int y, int hp = Creature, int maxHp = Creature, bool npc = false, string key = "")
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Druid target {Interlocked.Increment(ref _serial)}", maxHp)
        {
            IsNpc = npc,
            Key = key,
        };
        mob.Hp = hp;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Druid at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/> in
    /// slot 0 of the book. On a map with terrain, the map's own size; elsewhere a wide one.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, Action<Character>? configure = null) =>
        CasterNamed($"Druid{Interlocked.Increment(ref _serial)}", key, map, x, y, configure, mana);

    /// <summary><see cref="Caster"/> under a name the fact chooses, for a fact that reads the saved row back.</summary>
    private (Session session, RecordingOutbound outbound, Character c) CasterNamed(string name, string key, ushort map, int x, int y,
                                                                                 Action<Character>? configure = null, uint mana = Mana) =>
        _fx.PlayerWith(name, ch =>
        {
            ch.ClassName = "Druid";
            ch.Level = 99;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = MaxMana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, Action<Character>? configure = null) =>
        _fx.PlayerWith($"DruidOther{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = Hp; ch.Hp = Hp;
            Size(ch, map);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private static void Size(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }
}
