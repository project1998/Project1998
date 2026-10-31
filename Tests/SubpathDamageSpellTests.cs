using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The eight PC-subpath damage spells with an RTK script this server can run land RTK's numbers (owner, 2026-10-22: "the
/// nine subpath spells get their RTK scripts"; 2026-10-24 decision sheet item 18). Each is bound by its
/// <c>game-data/SpellParams.csv</c> row to a verb in <c>game-data/spell_verbs.lua</c> that follows its script under
/// <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/</c>. Before, none had a spell_effects archetype, so all of them fell to
/// the <c>generic</c> verb: 5 mana for 1 + 4×Will + 3×Grace at a creature, and at a player off a PvP map the 5 mana, the
/// pose, "You can't attack that target." and "You cast X." (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had all
/// nine on <c>verb=generic</c>). Control flames, the ninth, needed an acorn-drop hook; the Druid slice brought it
/// (<c>Tests/DruidSpellTests.cs</c>), and with it Energy bloom, Earth strike's body under its own name, whose facts are
/// Earth strike's here.
///
/// <para>What each fact pins, against RTK's scripts:</para>
/// <list type="bullet">
/// <item>the numbers at a creature: damage, mana and wait (Assassinate 4,000 and 50 s on its caster's vita and mana; Ki
/// strike 250 and 7 s; Hailstorm and Ice bombardment 190; Phoenix flames 880 and 1 s; Earth strike and Nature's smite 600
/// within 10 tiles; Lightning storm 1,200 and 87 s, 3,600 to each creature within 10 tiles on a coin flip, the 10-tile
/// edge included);</item>
/// <item>Assassinate and Phoenix flames never deflected, as RTK's engine never deflects a self-cast spell;</item>
/// <item>Ki strike and Ice bombardment deflected at their target, a creature or a player on a PvP map, as RTK's engine
/// deflects them, keeping their mana and Ki strike's wait (the server's rule for every deflect) and striking nothing; and
/// Hailstorm, the third spell on their verb, never;</item>
/// <item>the free refusal at a player the caster may not attack (the #351/#356 rule), and the spell landing on one on a
/// PvP map;</item>
/// <item>RTK's slips fixed: Phoenix flames' wait under its own key (the script arms <c>phoenix_flame</c>) and its
/// victim's line naming it (the script's says Hailstorm);</item>
/// <item>the learn levels as RTK's <c>requirements</c> give them, and Nature's smite without the mark our row carried.</item>
/// </list>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c> (but the two edge facts, which call the sweep
/// directly at a certain roll, or <c>Session.ApplyCast</c> many times over), by a fresh level-99 caster with Will 140
/// (so the floor in RTK's <c>floor((will + 1) / 2 * n)</c> shows), at creatures with 0 AC unless a fact says otherwise. The
/// content-free maps have no Maps.csv row, so PvP is off on them, as in every town; the two arenas are real PvP maps
/// with no spawns and no NPCs. No other class uses any of them.</para>
/// </summary>
[Collection("world")]
public sealed class SubpathDamageSpellTests
{
    /// <summary>Content-free maps, and Kugnae Legends and Kugnae Ancients (Maps.csv MapPvP 1, MapSpells 1; 33x33, no
    /// spawns, no NPCs, their only warps at (15,2) and (16,2)).</summary>
    private const ushort NumbersMap = 62901, ReachMap = 62902, StormMap = 62903, RefuseMap = 62904, PassMap = 62905,
                         NothingArena = 3007, PvpArena = 3008;
    private const ushort CX = 16, CY = 16;     // every caster stands here
    private const int Creature = 10_000_000;
    private const uint Will = 140, Hp = 9_001, Mana = 20_001;
    private const uint TargetHp = 1_000_000;   // a player target lives through any one of these, a critical included

    /// <summary>Slack on an aether read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private readonly SessionFixture _fx;
    private static int _serial;

    /// <summary><c>Session.ApplyCast</c>, the dispatch a 0x0F cast reaches after the action budget, as
    /// <c>SpellDispatchRouteTests</c> drives it: for a fact that casts the same spell many times in a row.</summary>
    private static readonly MethodInfo ApplyCastMi =
        typeof(Session).GetMethod("ApplyCast", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public SubpathDamageSpellTests(SessionFixture fx) => _fx = fx;

    // RTK's damage line, base + 99*coeff + floor((140 + 1) / 2 * willcoeff), worked out by hand.
    private const int KiStrike = 250 + 198 + 211;         // 659: floor(70.5 * 3) = 211
    private const int Hailstorm = 1000 + 297 + 211;       // 1508
    private const int PhoenixFlames = 1030 + 297 + 211;   // 1538
    private const int EarthStrike = 50 + 198 + 105;       // 353: floor(70.5 * 1.5) = 105

    // ===== the numbers at a creature ==========================================================================

    /// <summary>Ki strike, Hailstorm and Ice bombardment and what each costs and waits.</summary>
    public static TheoryData<string, int, uint, long> SplashCases => new()
    {
        { "ki_strike",       KiStrike,  250, 7_000 },
        { "hailstorm",       Hailstorm, 190, 0 },
        { "ice_bombardment", Hailstorm, 190, 0 },
    };

    /// <summary>Aimed at a creature four tiles off, the spell strikes it and the creatures on its four sides for RTK's
    /// amount each, leaves a creature two tiles from it untouched, takes RTK's mana and arms RTK's wait under the spell's
    /// own key. Red on 821914e: 5 mana and the generic 570 (1 + 4×140 + 3×3) to the target alone, no wait.</summary>
    [Theory]
    [MemberData(nameof(SplashCases))]
    public void ASplashStrikesTheTargetAndItsFourSides(string key, int amount, uint mana, long waitMs)
    {
        var sp = Spell(key);
        var target = AddCreature(NumbersMap, CX + 4, CY);
        var sides = new[] { AddCreature(NumbersMap, CX + 5, CY), AddCreature(NumbersMap, CX + 4, CY - 1),
                            AddCreature(NumbersMap, CX + 4, CY + 1) };
        var far = AddCreature(NumbersMap, CX + 6, CY);
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, target.Id));

            long wait = SpellCastSupport.AetherLeft(caster, key);
            string what = $"{sp.Name}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], target lost " +
                          $"{Lost(target)}, sides lost {string.Join("/", sides.Select(Lost))}, two off lost {Lost(far)} " +
                          $"(want {amount} each and 0), spent {Mana - c.Mp} (want {mana}), wait {wait} ms (want {waitMs})";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { $"You cast {sp.Name}." })
                        && Lost(target) == amount && sides.All(s => Lost(s) == amount) && Lost(far) == 0
                        && c.Mp == Mana - mana && (waitMs == 0 ? wait == 0 : wait > waitMs - SlackMs && wait <= waitMs), what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            foreach (var m in sides.Append(target).Append(far)) _fx.World.DespawnMob(NumbersMap, m);
        }
    }

    /// <summary>Phoenix flames strikes the creatures on its caster's four sides for RTK's amount each, takes 880 mana and
    /// arms a 1 s wait under its own key, <c>phoenix_flames</c>; the script's <c>phoenix_flame</c> stays unarmed. A
    /// creature two tiles off is untouched. A second cast inside the wait is refused for free with this server's wait
    /// line. Red on 821914e: 5 mana, 570 to the faced creature only, no wait.</summary>
    [Fact]
    public void PhoenixFlamesStrikesTheFourSidesAndWaitsUnderItsOwnKey()
    {
        var sp = Spell("phoenix_flames");
        var sides = new[] { AddCreature(NumbersMap, CX + 1, CY), AddCreature(NumbersMap, CX - 1, CY),
                            AddCreature(NumbersMap, CX, CY - 1), AddCreature(NumbersMap, CX, CY + 1) };
        var far = AddCreature(NumbersMap, CX + 2, CY);
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "phoenix_flames");
            long slip = SpellCastSupport.AetherLeft(caster, "phoenix_flame");
            string first = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], sides lost " +
                           $"{string.Join("/", sides.Select(Lost))}, two off lost {Lost(far)} (want {PhoenixFlames} each and " +
                           $"0), spent {Mana - c.Mp} (want 880), wait {wait} ms under phoenix_flames, {slip} under phoenix_flame";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "You cast Phoenix flames." })
                        && sides.All(s => Lost(s) == PhoenixFlames) && Lost(far) == 0 && c.Mp == Mana - 880
                        && wait > 0 && wait <= 1_000 && slip == 0, first);

            outbound.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0));
            string again = string.Join(" | ", SpellCastSupport.MiniTexts(outbound));
            Assert.True(again == "Phoenix flames isn't ready yet." && c.Mp == Mana - 880
                        && sides.All(s => Lost(s) == PhoenixFlames) && outbound.BodiesOf(ServerOp.Action).Count == 0,
                        $"a second cast inside the wait: [{again}], spent {Mana - c.Mp} in all");
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            foreach (var m in sides.Append(far)) _fx.World.DespawnMob(NumbersMap, m);
        }
    }

    /// <summary>RTK's own check, in the script's comment: "tested with Harue Shaman, 141 will. 3080 dmg against rabbit
    /// AC = 100" (<c>phoenix_flames.lua:2</c>). At level 99 that is 1030 + 297 + floor(71 × 3) = 1,540, doubled by AC
    /// 100. Red on 821914e: the generic 1 + 4×141 + 3×3 = 574, doubled, 1,148.</summary>
    [Fact]
    public void PhoenixFlamesLandsTheScriptsOwnTestedFigure()
    {
        var sp = Spell("phoenix_flames");
        var rabbit = AddCreature(NumbersMap, CX + 1, CY);
        rabbit.Ac = 100;
        var (caster, _, _) = Caster(sp, NumbersMap, will: 141);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lost(rabbit) == 3_080, $"the AC-100 creature lost {Lost(rabbit)} (the script says 3080)");
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            _fx.World.DespawnMob(NumbersMap, rabbit);
        }
    }

    public static TheoryData<string> RangedKeys => new() { "earth_strike", "natures_smite", "energy_bloom" };

    /// <summary>Earth strike and Nature's smite strike a creature exactly 10 tiles off for RTK's amount and 600 mana,
    /// with no wait; at 11 tiles the cast does nothing, silently and for free, as the script's
    /// <c>distanceSquare(player, target, 10)</c> return does. Red on 821914e: the 11-tile cast struck for 570 and cost
    /// 5 mana, and the 10-tile one too. Energy bloom (the Druid slice) is the same body under its own name, and was the
    /// same generic cast before it.</summary>
    [Theory]
    [MemberData(nameof(RangedKeys))]
    public void ARangedStrikeReachesTenTilesAndNoFurther(string key)
    {
        var sp = Spell(key);
        var near = AddCreature(ReachMap, CX + 10, CY + 3);
        var beyond = AddCreature(ReachMap, CX - 11, CY);
        var (caster, outbound, c) = Caster(sp, ReachMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, beyond.Id));
            string far = $"{sp.Name} at 11 tiles: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], " +
                         $"lost {Lost(beyond)}, spent {Mana - c.Mp}, pose {outbound.BodiesOf(ServerOp.Action).Count}";
            Assert.True(SpellCastSupport.MiniTexts(outbound).Count == 0 && Lost(beyond) == 0 && c.Mp == Mana
                        && outbound.BodiesOf(ServerOp.Action).Count == 0, far);

            outbound.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0, near.Id));
            string close = $"{sp.Name} at 10 tiles: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], " +
                           $"lost {Lost(near)} (want {EarthStrike}), spent {Mana - c.Mp} (want 600), wait " +
                           $"{SpellCastSupport.AetherLeft(caster, key)} ms";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { $"You cast {sp.Name}." })
                        && Lost(near) == EarthStrike && c.Mp == Mana - 600 && SpellCastSupport.AetherLeft(caster, key) == 0, close);
        }
        finally
        {
            _fx.World.LeaveMap(caster, ReachMap);
            _fx.World.DespawnMob(ReachMap, near);
            _fx.World.DespawnMob(ReachMap, beyond);
        }
    }

    /// <summary>Assassinate at the faced creature: ceil(0.25 × 9,001 + 1.25 × 20,001) = 27,252 of the caster's own pools
    /// as they stood, or five times that, 136,258, on its 5% critical; the caster keeps ceil(9,001 × 2/3) = 6,001 vita,
    /// and pays 4,000 mana and a 50 s wait. Red on 821914e: 570 damage, 5 mana, vita kept whole, no wait.</summary>
    [Fact]
    public void AssassinateStrikesFromTheCastersOwnPools()
    {
        var sp = Spell("assassinate");
        var mob = AddCreature(NumbersMap, CX + 1, CY);
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "assassinate");
            string what = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], lost {Lost(mob)} (want " +
                          $"27252 or 136258), vita {c.Hp} (want 6001), spent {Mana - c.Mp} (want 4000), wait {wait} ms";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "You cast Assassinate." })
                        && Lost(mob) is 27_252 or 136_258 && c.Hp == 6_001 && c.Mp == Mana - 4_000
                        && wait > 50_000 - SlackMs && wait <= 50_000, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            _fx.World.DespawnMob(NumbersMap, mob);
        }
    }

    /// <summary>Assassinate with nothing faced still costs: the script spends the mana and starts the wait "no matter
    /// what" (<c>assassinate.lua:3</c>). Nothing is struck and the caster keeps all its vita. Off a PvP map and on one,
    /// where an unaimed cast would otherwise land on its caster. Red on 821914e: off a PvP map free and silent; on one the
    /// generic cast struck its own caster for 5 mana.</summary>
    [Theory]
    [InlineData(NumbersMap)]
    [InlineData(NothingArena)]
    public void AssassinateAtNothingStillCostsItsManaAndWait(ushort map)
    {
        var sp = Spell("assassinate");
        var (caster, outbound, c) = Caster(sp, map);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "assassinate");
            string what = $"map {map}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], vita {c.Hp}, " +
                          $"spent {Mana - c.Mp}, wait {wait} ms";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "You cast Assassinate." })
                        && c.Hp == Hp && c.Mp == Mana - 4_000 && wait > 50_000 - SlackMs, what);
        }
        finally { _fx.World.LeaveMap(caster, map); }
    }

    // ===== never deflected (review F1) ======================================================================
    // RTK rolls a spell's deflect in its engine only for a cast that has a target: the canfail test sits inside
    // `if (sd->target)` (clif_parsemagic), and a type-5 spell never sets one. Assassinate and Phoenix flames are type 5,
    // so RTK never deflects either, though its table carries canfail 1 for both. Their rows carry SplCanFail 0 here,
    // which every deflect roll reads first. A creature with Protection 255 deflects every SplCanFail cast
    // (100 - 0.9^255 × 100 rounds to 100).

    /// <summary>Assassinate at a creature that deflects everything still strikes it, and only then takes the caster's
    /// third of vita: the creature lost 27,252 (or the critical's 136,258) and the caster kept 6,001 of 9,001, with no
    /// deflect line. Red on 87d0031: "Your magic has been deflected.", the creature untouched, and the caster's vita
    /// already down to 6,001.</summary>
    [Fact]
    public void AssassinateIsNeverDeflected()
    {
        var sp = Spell("assassinate");
        var mob = AddCreature(NumbersMap, CX + 1, CY, protection: 255);
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string what = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], lost {Lost(mob)} (want " +
                          $"27252 or 136258), vita {c.Hp} (want 6001), spent {Mana - c.Mp} (want 4000)";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "You cast Assassinate." })
                        && Lost(mob) is 27_252 or 136_258 && c.Hp == 6_001 && c.Mp == Mana - 4_000, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            _fx.World.DespawnMob(NumbersMap, mob);
        }
    }

    /// <summary>Phoenix flames at four creatures that deflect everything strikes all four for RTK's amount. Red on
    /// 87d0031: all four lost 0, and the 880 mana was spent.</summary>
    [Fact]
    public void PhoenixFlamesIsNeverDeflected()
    {
        var sp = Spell("phoenix_flames");
        var sides = new[] { AddCreature(NumbersMap, CX + 1, CY, protection: 255), AddCreature(NumbersMap, CX - 1, CY, protection: 255),
                            AddCreature(NumbersMap, CX, CY - 1, protection: 255), AddCreature(NumbersMap, CX, CY + 1, protection: 255) };
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string what = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], sides lost " +
                          $"{string.Join("/", sides.Select(Lost))} (want {PhoenixFlames} each), spent {Mana - c.Mp} (want 880)";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "You cast Phoenix flames." })
                        && sides.All(s => Lost(s) == PhoenixFlames) && c.Mp == Mana - 880, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            foreach (var m in sides) _fx.World.DespawnMob(NumbersMap, m);
        }
    }

    // ===== Ki strike and Ice bombardment deflect ==============================================================
    // RTK rolls their deflect in its engine, at the target, before the script (clif_parsemagic), and its table gives
    // both canfail 1, as ours does; Hailstorm, on the same verb, carries canfail 0 in both. The 5-way sweep rolls no
    // deflect, so the verb rolls it once through ctx:deflected(), at the target, after the spend: a creature by its
    // protection and its Will over the caster's, a player by their Will over the caster's (RTK's formula, the PvP
    // blow's roll). A deflected cast keeps its mana and Ki strike's wait, the server's rule for every deflect (RTK's
    // deflect is free), and strikes nothing.
    // The player roll is 100 - 0.9^p × 100, rounded, with p = round(WillAdvantage / 10): 0 with no advantage, 52 at an
    // advantage of 70 (p 7), and 100 from an advantage of 505 (p 51). A player's Will is a byte, so the facts that need
    // a certain deflect give the target a timed Will buff of 1,000.

    /// <summary>Ki strike and Ice bombardment with their mana and wait, aimed by id from four tiles off or by the slot
    /// alone at the faced creature.</summary>
    public static TheoryData<string, uint, long, bool> DeflectCases
    {
        get
        {
            var data = new TheoryData<string, uint, long, bool>();
            foreach (var byId in new[] { true, false })
            {
                data.Add("ki_strike", 250, 7_000, byId);
                data.Add("ice_bombardment", 190, 0, byId);
            }
            return data;
        }
    }

    /// <summary>At a creature with Protection 255 the cast is deflected: "Your magic has been deflected." then "You cast
    /// X.", the pose, the spell's mana and Ki strike's 7 s wait, and nothing struck, neither the target nor the creatures
    /// on its sides. Red on e67cd9d: never deflected, the target and its sides each lost the spell's amount.</summary>
    [Theory]
    [MemberData(nameof(DeflectCases))]
    public void KiStrikeAndIceBombardmentAreDeflectedAndKeepTheirCost(string key, uint mana, long waitMs, bool byId)
    {
        var sp = Spell(key);
        int tx = CX + (byId ? 4 : 1);
        var target = AddCreature(NumbersMap, tx, CY, protection: 255);
        var sides = new[] { AddCreature(NumbersMap, tx + 1, CY), AddCreature(NumbersMap, tx, CY - 1),
                            AddCreature(NumbersMap, tx, CY + 1) };
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, byId ? target.Id : null));
            long wait = SpellCastSupport.AetherLeft(caster, key);
            int pose = outbound.BodiesOf(ServerOp.Action).Count;
            string what = $"{sp.Name}{(byId ? " by id" : " faced")}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], " +
                          $"target lost {Lost(target)}, sides lost {string.Join("/", sides.Select(Lost))} (want 0 each), spent " +
                          $"{Mana - c.Mp} (want {mana}), wait {wait} ms (want {waitMs}), pose {pose}";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "Your magic has been deflected.", $"You cast {sp.Name}." })
                        && Lost(target) == 0 && sides.All(s => Lost(s) == 0) && c.Mp == Mana - mana
                        && (waitMs == 0 ? wait == 0 : wait > waitMs - SlackMs && wait <= waitMs) && pose == 1, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            foreach (var m in sides.Append(target)) _fx.World.DespawnMob(NumbersMap, m);
        }
    }

    /// <summary>The roll is the target's alone. At a creature that never deflects (Protection 0, Will 0), with a
    /// Protection-255 creature on each of its four sides, the cast lands as it did before: the target and all four
    /// struck for the spell's amount, its mana and only "You cast X.". A guard, green on e67cd9d too; red if the sides
    /// rolled as well.</summary>
    [Theory]
    [InlineData("ki_strike", KiStrike, 250u)]
    [InlineData("ice_bombardment", Hailstorm, 190u)]
    public void WhenTheTargetsRollPassesTheSplashLandsAsBefore(string key, int amount, uint mana)
    {
        var sp = Spell(key);
        var target = AddCreature(NumbersMap, CX + 4, CY);
        var sides = new[] { AddCreature(NumbersMap, CX + 5, CY, protection: 255), AddCreature(NumbersMap, CX + 3, CY, protection: 255),
                            AddCreature(NumbersMap, CX + 4, CY - 1, protection: 255), AddCreature(NumbersMap, CX + 4, CY + 1, protection: 255) };
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, target.Id));
            string what = $"{sp.Name}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], target lost {Lost(target)}, " +
                          $"sides lost {string.Join("/", sides.Select(Lost))} (want {amount} each), spent {Mana - c.Mp} (want {mana})";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { $"You cast {sp.Name}." })
                        && Lost(target) == amount && sides.All(s => Lost(s) == amount) && c.Mp == Mana - mana, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            foreach (var m in sides.Append(target)) _fx.World.DespawnMob(NumbersMap, m);
        }
    }

    /// <summary>Hailstorm, the third spell on the verb, is never deflected: its row carries SplCanFail 0, as RTK's table
    /// does (its script is Ice bombardment's but for the name and level; the table is the difference). At a Protection-255 creature with Protection-255 creatures on its
    /// sides, all five are struck for its amount, for 190 mana and only "You cast Hailstorm.". A guard, green on e67cd9d
    /// too; red if its row carried SplCanFail 1.</summary>
    [Fact]
    public void HailstormIsNeverDeflected()
    {
        var sp = Spell("hailstorm");
        var target = AddCreature(NumbersMap, CX + 4, CY, protection: 255);
        var sides = new[] { AddCreature(NumbersMap, CX + 5, CY, protection: 255), AddCreature(NumbersMap, CX + 3, CY, protection: 255),
                            AddCreature(NumbersMap, CX + 4, CY - 1, protection: 255), AddCreature(NumbersMap, CX + 4, CY + 1, protection: 255) };
        var (caster, outbound, c) = Caster(sp, NumbersMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, target.Id));
            string what = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], target lost {Lost(target)}, sides lost " +
                          $"{string.Join("/", sides.Select(Lost))} (want {Hailstorm} each), spent {Mana - c.Mp} (want 190)";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "You cast Hailstorm." })
                        && Lost(target) == Hailstorm && sides.All(s => Lost(s) == Hailstorm) && c.Mp == Mana - 190, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, NumbersMap);
            foreach (var m in sides.Append(target)) _fx.World.DespawnMob(NumbersMap, m);
        }
    }

    /// <summary>At a player on a PvP map whose Will beats the caster's by far more than 505 (a timed Will buff of 1,000),
    /// every cast is deflected: "Your magic has been deflected." then "You cast X.", the pose, the spell's mana and Ki
    /// strike's 7 s wait; the player keeps their HP and is told nothing, and the creature on the player's side is
    /// untouched. Aimed by id from four tiles off, with a creature that never deflects on the tile the caster faces (the
    /// one an id for a player used to fall back to), or by the slot alone at the faced player. Red on e67cd9d and on
    /// 90310cd: no roll at a player, so the player and the creature beside them were struck.</summary>
    [Theory]
    [MemberData(nameof(DeflectCases))]
    public void AtAPlayerWhoseWillFarBeatsTheCastersEveryCastIsDeflected(string key, uint mana, long waitMs, bool byId)
    {
        var sp = Spell(key);
        int tx = CX + (byId ? 4 : 1);
        var (target, tOut, tc) = Player(PvpArena, (ushort)tx, CY);
        target.WithState(() => target.LuaBuff("will", 1_000, 600_000, sp));
        var side = AddCreature(PvpArena, tx + 1, CY);
        var faced = byId ? AddCreature(PvpArena, CX + 1, CY) : null;
        var (caster, outbound, c) = Caster(sp, PvpArena, dir: 1);
        try
        {
            tOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0, byId ? target.PlayerId : null));
            long wait = SpellCastSupport.AetherLeft(caster, key);
            int pose = outbound.BodiesOf(ServerOp.Action).Count;
            string what = $"{sp.Name}{(byId ? " by id" : " faced")}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], " +
                          $"player lost {TargetHp - tc.Hp} and read {SpellCastSupport.MiniTexts(tOut).Count} line(s), creature beside " +
                          $"lost {Lost(side)}, faced creature lost {(faced is null ? "-" : Lost(faced))}, spent {Mana - c.Mp} (want " +
                          $"{mana}), wait {wait} ms (want {waitMs}), pose {pose}";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "Your magic has been deflected.", $"You cast {sp.Name}." })
                        && tc.Hp == TargetHp && SpellCastSupport.MiniTexts(tOut).Count == 0 && Lost(side) == 0
                        && (faced is null || Lost(faced) == 0) && c.Mp == Mana - mana
                        && (waitMs == 0 ? wait == 0 : wait > waitMs - SlackMs && wait <= waitMs) && pose == 1, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(target, PvpArena);
            _fx.World.DespawnMob(PvpArena, side);
            if (faced is not null) _fx.World.DespawnMob(PvpArena, faced);
        }
    }

    /// <summary>At a player whose Will beats the caster's by 70 (210 against 140), each cast is deflected 52 times in
    /// 100, by RTK's formula: forty casts by id, the wait cleared between them, each either deflected (the deflect line,
    /// the player untouched and told nothing) or landed (no line, the player struck and told "&lt;caster&gt; hits you
    /// with X."), both happened, and every one cost the spell's mana. The chance that forty casts all land alike is
    /// under 1 in 10^11. Red on e67cd9d and on 90310cd: none was deflected.</summary>
    [Theory]
    [InlineData("ki_strike", 250u)]
    [InlineData("ice_bombardment", 190u)]
    public void AtAPlayerWhoseWillBeatsTheCastersSomeCastsAreDeflected(string key, uint mana)
    {
        var sp = Spell(key);
        var (target, tOut, tc) = Player(PvpArena, CX + 4, CY, will: 210);
        var (caster, outbound, c) = Caster(sp, PvpArena, dir: 1);
        try
        {
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            var seen = new List<string>();
            int deflected = 0, landed = 0, odd = 0;
            for (int i = 0; i < 40; i++)
            {
                SpellCastSupport.ClearAether(caster, sp.Key);
                outbound.Clear();
                tOut.Clear();
                uint before = tc.Hp;
                caster.WithState(() => ApplyCastMi.Invoke(caster, new object?[] { sp, target.PlayerId, null }));
                var lines = SpellCastSupport.MiniTexts(outbound);
                string told = string.Join(" | ", SpellCastSupport.MiniTexts(tOut).Select(N));
                if (lines.SequenceEqual(new[] { "Your magic has been deflected." }) && tc.Hp == before && told == "") deflected++;
                else if (lines.Count == 0 && tc.Hp < before && told == $"<caster> hits you with {sp.Name}.") landed++;
                else { odd++; seen.Add($"[{string.Join(" | ", lines)}] lost {before - tc.Hp} told [{told}]"); }
            }
            string what = $"{sp.Name}: 40 casts, {deflected} deflected, {landed} landed, {odd} neither " +
                          $"[{string.Join("; ", seen)}], spent {Mana - c.Mp} (want {40 * mana})";
            Assert.True(odd == 0 && deflected > 0 && landed > 0 && c.Mp == Mana - 40 * mana, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(target, PvpArena);
        }
    }

    /// <summary>A caster whose Will is at least the player's is never deflected, and a player target is rolled for by its
    /// own Will, never by the creature the caster faces. Aimed by id at a player with Will 255, by a caster with Will
    /// 255 facing a Protection-255 creature, the cast lands on the player: "You cast X.", its mana, the player struck and
    /// told "&lt;caster&gt; hits you with X.", and the faced creature, outside the blast, untouched. (A caster with more
    /// Will than the player is the PvP control above, <c>OnAPvpMapTheSameCastLandsOnThePlayer</c>.) Red with
    /// ctx:deflected() as it was at 90310cd, which took the faced creature for an id that named a player; red too if
    /// a player target were deflected whatever the Wills.</summary>
    [Theory]
    [InlineData("ki_strike", 250u)]
    [InlineData("ice_bombardment", 190u)]
    public void ACasterWhoseWillMatchesThePlayersIsNeverDeflected(string key, uint mana)
    {
        var sp = Spell(key);
        var faced = AddCreature(PvpArena, CX + 1, CY, protection: 255);
        var (target, tOut, tc) = Player(PvpArena, CX + 4, CY, will: 255);
        var (caster, outbound, c) = Caster(sp, PvpArena, will: 255, dir: 1);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, target.PlayerId));
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            string told = string.Join(" | ", SpellCastSupport.MiniTexts(tOut).Select(N));
            string what = $"{sp.Name}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], player lost " +
                          $"{TargetHp - tc.Hp} and read [{told}], faced creature lost {Lost(faced)}, spent {Mana - c.Mp} (want {mana})";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { $"You cast {sp.Name}." })
                        && tc.Hp < TargetHp && told == $"<caster> hits you with {sp.Name}." && Lost(faced) == 0
                        && c.Mp == Mana - mana, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(target, PvpArena);
            _fx.World.DespawnMob(PvpArena, faced);
        }
    }

    /// <summary>An id for a player on another map never rolls that player's Will. The caster, on a PvP map, aims by id at
    /// a player on another map whose Will far beats theirs (a Will buff of 1,000, a certain deflect were it rolled), while
    /// facing a creature that never deflects. The damage resolver answers the faced creature and the sweep centres on it,
    /// so the roll is the faced creature's: "You cast X." only, the creature struck for the spell's amount, the far player
    /// untouched and told nothing. PR #397's review, F1 (its probe P1). Red with LuaDeflected's same-map test removed:
    /// the cast is deflected by the absent player and the faced creature is untouched.</summary>
    [Theory]
    [InlineData("ki_strike", KiStrike)]
    [InlineData("ice_bombardment", Hailstorm)]
    public void AnIdForAPlayerOnAnotherMapNeverRollsThatPlayersWill(string key, int amount)
    {
        var sp = Spell(key);
        var (far, farOut, farC) = Player(NumbersMap, CX + 4, CY);
        far.WithState(() => far.LuaBuff("will", 1_000, 600_000, sp));
        var faced = AddCreature(PvpArena, CX + 1, CY);
        var (caster, outbound, _) = Caster(sp, PvpArena, dir: 1);
        try
        {
            farOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0, far.PlayerId));
            string what = $"{sp.Name}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], faced creature lost " +
                          $"{Lost(faced)} (want {amount}), far player lost {TargetHp - farC.Hp} and read " +
                          $"{SpellCastSupport.MiniTexts(farOut).Count} line(s)";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { $"You cast {sp.Name}." })
                        && Lost(faced) == amount && farC.Hp == TargetHp && SpellCastSupport.MiniTexts(farOut).Count == 0, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(far, NumbersMap);
            _fx.World.DespawnMob(PvpArena, faced);
        }
    }

    /// <summary>Lightning storm takes 1,200 mana and an 87 s wait, and gives each creature within 10 tiles (on the
    /// larger axis) a coin flip for 3,600; creatures 11 tiles off are untouched. Thirty creatures in range: each lost
    /// 3,600 or nothing, and both happened (the chance all thirty land the same way is 2 in 2^30). The 10-tile edge
    /// itself is pinned by the two edge facts below, which this one cannot do: a creature on the edge that got no flip
    /// looks like one that lost it. Red on 821914e: with nothing faced the generic cast was refused, so nothing was
    /// struck or spent.</summary>
    [Fact]
    public void LightningStormFlipsACoinForEveryCreatureWithinTenTiles()
    {
        var sp = Spell("lightning_storm");
        var inRange = new List<Mob>();
        for (int i = 0; i < 30; i++)
        {
            int dx = (i % 6) * 4 - 10, dy = (i / 6) * 5 - 10;   // -10..10 on both axes, edges included, never 0,0
            inRange.Add(AddCreature(StormMap, CX + dx, CY + dy));
        }
        var outOfRange = new[] { AddCreature(StormMap, CX + 11, CY), AddCreature(StormMap, CX, CY - 11),
                                 AddCreature(StormMap, CX - 11, CY + 11) };
        var (caster, outbound, c) = Caster(sp, StormMap);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "lightning_storm");
            var lost = inRange.Select(Lost).ToList();
            string what = $"lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], in range lost " +
                          $"[{string.Join(",", lost)}], out of range lost [{string.Join(",", outOfRange.Select(Lost))}], " +
                          $"spent {Mana - c.Mp} (want 1200), wait {wait} ms (want 87000)";
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { "You cast Lightning storm." })
                        && lost.All(l => l is 0 or 3_600) && lost.Contains(0) && lost.Contains(3_600)
                        && outOfRange.All(m => Lost(m) == 0) && c.Mp == Mana - 1_200
                        && wait > 87_000 - SlackMs && wait <= 87_000, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, StormMap);
            foreach (var m in inRange.Concat(outOfRange)) _fx.World.DespawnMob(StormMap, m);
        }
    }

    /// <summary>On a PvP map Lightning storm gives each other player within 10 tiles the same coin flip: twenty players in
    /// range each either lost vita and read "&lt;caster&gt; hits you with Lightning storm." or were untouched and told
    /// nothing, and both happened (2 in 2^20 that they all land alike); a player 11 tiles off and the caster are
    /// untouched. Red on 821914e: with nothing faced the generic cast fell back to its caster and struck only them.</summary>
    [Fact]
    public void LightningStormFlipsACoinForEveryPlayerWithinTenTilesOnAPvpMap()
    {
        var sp = Spell("lightning_storm");
        var inRange = new List<(Session s, RecordingOutbound o, Character c)>();
        for (int i = 0; i < 20; i++)
        {
            int dx = (i % 5) * 5 - 10, dy = (i / 5) * 5 - 8;   // -10..10 across, -8..7 down, never 0,0
            inRange.Add(Player(PvpArena, (ushort)(CX + dx), (ushort)(CY + dy)));
        }
        var (farP, farOut, farC) = Player(PvpArena, CX - 11, CY - 11);
        var (caster, outbound, c) = Caster(sp, PvpArena);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            var seen = inRange.Select(p => (lost: TargetHp - p.c.Hp,
                                            lines: string.Join(" | ", SpellCastSupport.MiniTexts(p.o).Select(N)))).ToList();
            bool Hit((uint lost, string lines) r) => r.lost > 0 && r.lines == "<caster> hits you with Lightning storm.";
            bool Missed((uint lost, string lines) r) => r.lost == 0 && r.lines == "";
            string what = $"in range [{string.Join(", ", seen.Select(r => $"{r.lost}:{r.lines}"))}], 11 off lost " +
                          $"{TargetHp - farC.Hp} and read {SpellCastSupport.MiniTexts(farOut).Count} line(s), caster vita " +
                          $"{c.Hp}, spent {Mana - c.Mp}";
            Assert.True(seen.All(r => Hit(r) || Missed(r)) && seen.Any(Hit) && seen.Any(Missed)
                        && farC.Hp == TargetHp && SpellCastSupport.MiniTexts(farOut).Count == 0
                        && c.Hp == Hp && c.Mp == Mana - 1_200, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(farP, PvpArena);
            foreach (var p in inRange) _fx.World.LeaveMap(p.s, PvpArena);
        }
    }

    // ===== the 10-tile edge (review F2) =====================================================================
    // RTK's distanceSquare(player, target, 10) passes |dx| <= 10 and |dy| <= 10 (Scripts/scripts.lua), so a creature
    // or an attackable player exactly 10 tiles off on either axis is in reach and one 11 off is not. The two facts
    // above cannot tell an edge target that got no flip from one that lost it.

    /// <summary>The sweep itself at a certain roll: <c>Session.LuaRangeZap</c> at 100%, on a PvP map, strikes the
    /// creatures exactly 10 tiles off (east, north, and a corner) and the player 10 off on both axes, and leaves the
    /// creature and the player 11 off untouched. Goes red if the sweep's edge test stops at 9 tiles.</summary>
    [Fact]
    public void TheStormSweepReachesTenTilesAndNotEleven()
    {
        var sp = Spell("lightning_storm");
        var edge = new[] { AddCreature(PvpArena, CX + 10, CY), AddCreature(PvpArena, CX, CY - 10),
                           AddCreature(PvpArena, CX - 10, CY + 10) };
        var beyond = AddCreature(PvpArena, CX + 11, CY - 3);
        var (edgeP, _, edgeC) = Player(PvpArena, CX + 10, CY - 10);
        var (farP, _, farC) = Player(PvpArena, CX - 11, CY);
        var (caster, _, _) = Caster(sp, PvpArena);
        try
        {
            int hit = caster.WithState(() => caster.LuaRangeZap(3_600, 10, 100, sp));
            string what = $"struck {hit}; edge creatures lost {string.Join("/", edge.Select(Lost))}, 11 off lost " +
                          $"{Lost(beyond)}; edge player lost {TargetHp - edgeC.Hp}, 11-off player lost {TargetHp - farC.Hp}";
            Assert.True(edge.All(m => Lost(m) == 3_600) && Lost(beyond) == 0 && edgeC.Hp < TargetHp && farC.Hp == TargetHp
                        && hit == 4, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(edgeP, PvpArena);
            _fx.World.LeaveMap(farP, PvpArena);
            foreach (var m in edge.Append(beyond)) _fx.World.DespawnMob(PvpArena, m);
        }
    }

    /// <summary>The same edge through the spell itself, its SpellParams row and verb included (so through the verb's own
    /// 10-tile reach): Lightning storm is cast until a creature and a player standing exactly 10 tiles off have each been
    /// struck, at most 40 times (its wait cleared between casts), and a creature and a player 11 off are never struck.
    /// The chance that an edge target in reach goes 40 coin flips without one is 2 in 2^40. Goes red if either the verb's
    /// reach or the sweep's edge test stops at 9 tiles.</summary>
    [Fact]
    public void LightningStormReachesItsTenTileEdgeWhenCast()
    {
        var sp = Spell("lightning_storm");
        var edge = AddCreature(PvpArena, CX + 10, CY - 10);
        var beyond = AddCreature(PvpArena, CX - 11, CY);
        var (edgeP, _, edgeC) = Player(PvpArena, CX - 10, CY + 10);
        var (farP, _, farC) = Player(PvpArena, CX, CY + 11);
        var (caster, _, _) = Caster(sp, PvpArena, mana: 100_000);
        try
        {
            int casts = 0;
            while (casts < 40 && (Lost(edge) == 0 || edgeC.Hp == TargetHp))
            {
                SpellCastSupport.ClearAether(caster, sp.Key);
                caster.WithState(() => ApplyCastMi.Invoke(caster, new object?[] { sp, null, null }));
                casts++;
            }
            string what = $"{casts} cast(s): edge creature lost {Lost(edge)}, edge player lost {TargetHp - edgeC.Hp}; " +
                          $"11-off creature lost {Lost(beyond)}, 11-off player lost {TargetHp - farC.Hp}";
            Assert.True(Lost(edge) > 0 && Lost(edge) % 3_600 == 0 && edgeC.Hp < TargetHp
                        && Lost(beyond) == 0 && farC.Hp == TargetHp, what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(edgeP, PvpArena);
            _fx.World.LeaveMap(farP, PvpArena);
            _fx.World.DespawnMob(PvpArena, edge);
            _fx.World.DespawnMob(PvpArena, beyond);
        }
    }

    // ===== at a player ==========================================================================================

    /// <summary>The six that aim at someone, by the slot alone at a faced player (the stock 4.95 client's frame) and,
    /// for the five "Which target?" spells, by the player's id from four tiles off.</summary>
    public static TheoryData<string, bool> AimedCases
    {
        get
        {
            var data = new TheoryData<string, bool> { { "assassinate", false } };
            foreach (var key in new[] { "ki_strike", "hailstorm", "ice_bombardment", "earth_strike", "natures_smite", "energy_bloom" })
            {
                data.Add(key, false);
                data.Add(key, true);
            }
            return data;
        }
    }

    /// <summary>Off a PvP map, a cast at a player is refused before anything is spent, the #351/#356 rule (sheet item
    /// 18): "You can't attack that target." and nothing else, no mana, no pose, no wait, and the player keeps their HP
    /// and is told nothing. RTK's Earth strike and Nature's smite spend their 600 first; that is not ported. Red on
    /// 821914e: the generic verb spent 5 mana before the blow's refusal, then posed and said "You cast X.".</summary>
    [Theory]
    [MemberData(nameof(AimedCases))]
    public void ACastAtAPlayerOffAPvpMapIsRefusedForFree(string key, bool byId)
    {
        var r = CastAtAPlayer(Spell(key), RefuseMap, byId);
        Assert.True(r.Lines == "You can't attack that target." && r.Spent == 0 && r.Pose == 0 && r.Wait == 0
                    && r.TargetHpLost == 0 && r.TargetLines == "", $"{key}{(byId ? " by id" : "")}: {r}");
    }

    /// <summary>The control: on a PvP map the same cast lands on the player. "You cast X.", the spell's mana, the pose,
    /// the player's HP down and their one line, the engine's "&lt;caster&gt; hits you with X.". Green on 821914e too
    /// (the generic blow landed there, for 5 mana); it pins that the refusal above is the map's.</summary>
    [Theory]
    [MemberData(nameof(AimedCases))]
    public void OnAPvpMapTheSameCastLandsOnThePlayer(string key, bool byId)
    {
        var sp = Spell(key);
        var r = CastAtAPlayer(sp, PvpArena, byId);
        Assert.True(r.Lines == $"You cast {sp.Name}." && r.Spent > 0 && r.Pose == 1 && r.TargetHpLost > 0
                    && r.TargetLines == $"<caster> hits you with {sp.Name}.", $"{key}{(byId ? " by id" : "")}: {r}");
    }

    /// <summary>Phoenix flames and Lightning storm aim at nobody: off a PvP map they pass over a player beside the
    /// caster, as RTK's scripts do (they strike a player only where <c>canPK</c> allows), and strike the creature on the
    /// other side. The player keeps their HP and is told nothing; the cast costs its mana. Red on 821914e: the generic
    /// cast faced the player, spent 5 mana and said "You can't attack that target.".</summary>
    [Theory]
    [InlineData("phoenix_flames", 880u)]
    [InlineData("lightning_storm", 1_200u)]
    public void AnUnaimedSpellPassesOverAPlayerItMayNotAttack(string key, uint mana)
    {
        var sp = Spell(key);
        var (player, pOut, pc) = Player(PassMap, CX + 1, CY);
        var mob = AddCreature(PassMap, CX - 1, CY);
        var (caster, outbound, c) = Caster(sp, PassMap, dir: 1);   // facing the player
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string what = $"{sp.Name}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], player lost " +
                          $"{TargetHp - pc.Hp} and read [{string.Join(" | ", SpellCastSupport.MiniTexts(pOut))}], creature lost " +
                          $"{Lost(mob)}, spent {Mana - c.Mp} (want {mana})";
            // Lightning storm's coin flip may pass over the creature too; Phoenix flames always strikes it.
            Assert.True(SpellCastSupport.MiniTexts(outbound).SequenceEqual(new[] { $"You cast {sp.Name}." })
                        && pc.Hp == TargetHp && SpellCastSupport.MiniTexts(pOut).Count == 0 && c.Mp == Mana - mana
                        && (key == "lightning_storm" ? Lost(mob) is 0 or 3_600 : Lost(mob) == PhoenixFlames), what);
        }
        finally
        {
            _fx.World.LeaveMap(caster, PassMap);
            _fx.World.LeaveMap(player, PassMap);
            _fx.World.DespawnMob(PassMap, mob);
        }
    }

    /// <summary>Phoenix flames' victim reads a line that names Phoenix flames. RTK's script tells every victim
    /// "&lt;caster&gt; casts Hailstorm on you." (<c>phoenix_flames.lua:33</c>), a slip copied from Hailstorm; here the
    /// player beside the caster on a PvP map reads the engine's line with the spell's own name, and nothing names
    /// Hailstorm. Red on 821914e on the cost: 5 mana, not 880 (the generic blow said the name too).</summary>
    [Fact]
    public void PhoenixFlamesTellsItsVictimItsOwnName()
    {
        var sp = Spell("phoenix_flames");
        var (player, pOut, pc) = Player(PvpArena, CX, CY + 1);
        var (caster, _, c) = Caster(sp, PvpArena, dir: 2);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var lines = SpellCastSupport.MiniTexts(pOut).Select(l => l.Replace(caster.CharName, "<caster>")).ToList();
            Assert.True(lines.SequenceEqual(new[] { "<caster> hits you with Phoenix flames." })
                        && !lines.Any(l => l.Contains("Hailstorm")) && pc.Hp < TargetHp && c.Mp == Mana - 880,
                        $"the player read [{string.Join(" | ", lines)}] and lost {TargetHp - pc.Hp}; spent {Mana - c.Mp}");
        }
        finally
        {
            _fx.World.LeaveMap(caster, PvpArena);
            _fx.World.LeaveMap(player, PvpArena);
        }
    }

    /// <summary>The five "Which target?" spells, on a PvP map with nothing aimed at and nothing faced, cast nothing,
    /// silently and for free: RTK's engine runs the script only for a target it found (clif_parsemagic). Red on
    /// 821914e: the generic cast fell back to its caster there and struck them for 5 mana.</summary>
    [Theory]
    [InlineData("ki_strike")]
    [InlineData("hailstorm")]
    [InlineData("ice_bombardment")]
    [InlineData("earth_strike")]
    [InlineData("natures_smite")]
    [InlineData("energy_bloom")]
    public void AnAimedSpellAtNothingCastsNothing(string key)
    {
        var sp = Spell(key);
        var (caster, outbound, c) = Caster(sp, NothingArena);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string what = $"{sp.Name}: lines [{string.Join(" | ", SpellCastSupport.MiniTexts(outbound))}], spent " +
                          $"{Mana - c.Mp}, hp lost {Hp - c.Hp}, pose {outbound.BodiesOf(ServerOp.Action).Count}";
            Assert.True(SpellCastSupport.MiniTexts(outbound).Count == 0 && c.Mp == Mana && c.Hp == Hp
                        && outbound.BodiesOf(ServerOp.Action).Count == 0, what);
        }
        finally { _fx.World.LeaveMap(caster, NothingArena); }
    }

    // ===== learn levels ========================================================================================

    /// <summary>The level each script's <c>requirements</c> asks: 64 for Hailstorm and Phoenix flames, 99 for the other
    /// six. Red on 821914e: 0 for all but Nature's smite, which read 99 only through the rank floor its mark 2 gave
    /// it.</summary>
    [Theory]
    [InlineData("assassinate", 99)]
    [InlineData("ki_strike", 99)]
    [InlineData("hailstorm", 64)]
    [InlineData("ice_bombardment", 99)]
    [InlineData("phoenix_flames", 64)]
    [InlineData("earth_strike", 99)]
    [InlineData("natures_smite", 99)]
    [InlineData("lightning_storm", 99)]
    public void EachLearnsAtTheLevelItsScriptAsks(string key, int level) =>
        Assert.Equal(level, Spell(key).Level);

    /// <summary>Nature's smite asks no subpath rank: RTK's script asks level 99 and nothing else
    /// (<c>natures_smite.lua:45-51</c>), and its twin Earth strike carries none either. Red on 821914e: mark 2.</summary>
    [Fact]
    public void NaturesSmiteAsksNoRank()
    {
        Assert.Equal(0, Spell("natures_smite").Mark);
        Assert.Equal(0, Spell("earth_strike").Mark);
    }

    // ===== helpers ===========================================================================================

    private sealed record PlayerCast(string Lines, uint Spent, int Pose, long Wait, uint TargetHpLost, string TargetLines);

    /// <summary>A fresh caster casts <paramref name="sp"/> at a fresh player: faced (east, one tile) by the slot alone,
    /// or by the player's id from four tiles east. Returns what both saw, with the caster's name taken out.</summary>
    private PlayerCast CastAtAPlayer(SpellDef sp, ushort map, bool byId)
    {
        var (target, tOut, tc) = Player(map, (ushort)(CX + (byId ? 4 : 1)), CY);
        var (caster, cOut, cc) = Caster(sp, map, dir: 1);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0, byId ? target.PlayerId : null));
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            return new PlayerCast(string.Join(" | ", SpellCastSupport.MiniTexts(cOut).Select(N)), Mana - cc.Mp,
                                  cOut.BodiesOf(ServerOp.Action).Count, SpellCastSupport.AetherLeft(caster, sp.Key),
                                  TargetHp - tc.Hp, string.Join(" | ", SpellCastSupport.MiniTexts(tOut).Select(N)));
        }
        finally
        {
            _fx.World.LeaveMap(caster, map);
            _fx.World.LeaveMap(target, map);
        }
    }

    private static SpellDef Spell(string key)
    {
        var sp = Content.SpellByKey(key);
        Assert.True(sp is not null, $"no spell '{key}'");
        return sp!;
    }

    /// <summary>A 10,000,000-vita creature with 0 AC on (<paramref name="x"/>, <paramref name="y"/>); Protection 255
    /// deflects every SplCanFail cast.</summary>
    private Mob AddCreature(ushort map, int x, int y, int protection = 0)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Subpath target {Interlocked.Increment(ref _serial)}",
                          Creature);
        mob.Protection = protection;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    private static int Lost(Mob mob) => Creature - mob.Hp;

    /// <summary>A level-99 caster at (CX, CY) with <paramref name="sp"/> in slot 0, Will <paramref name="will"/>,
    /// 9,001 vita and <paramref name="mana"/> mana (20,001 unless a fact asks more), facing <paramref name="dir"/>
    /// (0 north, 1 east, 2 south).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(SpellDef sp, ushort map, uint will = Will,
                                                                            byte dir = 1, uint mana = Mana) =>
        _fx.PlayerWith($"SubpathCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.Will = (byte)will;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = mana; ch.Mp = mana;
            ch.Dir = dir;
            Wide(ch, map);
            ch.Spells.Add(sp.Id);
        }, map, CX, CY);

    /// <summary>A level-50 player with a million vita and nothing running, with Will <paramref name="will"/> when a fact
    /// gives one (the character's default otherwise).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, ushort x, ushort y, byte? will = null) =>
        _fx.PlayerWith($"SubpathTarget{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            if (will is byte w) ch.Will = w;
            ch.MaxHp = TargetHp; ch.Hp = TargetHp;
            ch.MaxMp = 1_000; ch.Mp = 1_000;
            Wide(ch, map);
        }, map, x, y);

    /// <summary>The arenas carry their own size; content-free maps have none, and the cast handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m) && m.Xs > 0 && m.Ys > 0) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
