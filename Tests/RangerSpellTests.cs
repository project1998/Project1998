using System.Diagnostics;
using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Ranger spells slice (the project owner, 2026-10-26, P5 "each spell as its RTK script", P1 and P3 "on sale once it
/// works, at its script's price"; 2026-11-12, Scouting trip's boxes, "Plain boxes"; the smaller calls as RTK under P5).
/// Before, all seven Ranger rows fell to the <c>generic</c> verb, 5 mana and "You cast X." and nothing else
/// (<c>Tests/Fixtures/spell-dispatch-routes.txt</c> had them on <c>verb=generic</c>).
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Ranger/</c> and the Pit trap's trap,
/// <c>NPCs/trap/rogue_traps/Ranger/pit_trap.lua</c>. Each spell is bound by its <c>game-data/SpellParams.csv</c> row: Skill
/// up to the climbing fury on Spy's ladder, Forest cover to Muse's <c>disguise</c>, Tree bark to Chongun's
/// <c>invincible</c>, Pit trap, Carve arrow, Call companion and Scouting trip to the Ranger region of
/// <c>game-data/spell_verbs.lua</c>; the trap's strike is <c>World</c>'s (the Pit trap block) and
/// <c>Session.SpringPitTrap</c>.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Ranger facing east, on
/// content-free maps 63600-63630 (no Maps.csv row: no terrain, PvP off) and on Seasons Arena (3045), a PvP map no other
/// class's facts use. A player springs a trap by a real walk frame; a creature by the mob tick's committed step. A menu,
/// an input box or a box is answered from a thread of its own with no synchronisation context, as a read loop answers it,
/// so the step resumes inline as on the server.</para>
/// </summary>
[Collection("world")]
public sealed class RangerSpellTests
{
    private const ushort RageMap = 63600, CoverMap = 63601, BarkMap = 63602, BarkWardMap = 63603, BarkKindMap = 63604,
                         BarkEndMap = 63605, PitMap = 63606, PitCreatureMap = 63607, PitGoneMap = 63608, PitOffMap = 63609,
                         PitRevealMap = 63610, CarveMap = 63611, CarveMakeMap = 63612, CarveRefuseMap = 63613,
                         CarveFullMap = 63614, CompanionMap = 63615, CompanionRefuseMap = 63616, ScoutMap = 63617,
                         ScoutUnmarkMap = 63618, ScoutRefuseMap = 63619, ScoutFarMap = 63620, ScoutHeldMap = 63621,
                         ScoutBoxHeldMap = 63622, Arena = 3045;
    // Fix round 1 (#423's review, F1-F3): content-free too, claimed by no other class.
    private const ushort PitBeatMap = 63623, CarveGoneMap = 63624, GoneCasterMap = 63625, ScrollMap = 63626;
    // Fix round 2 (#423's re-check, R1 and R2): the same.
    private const ushort PitOrderMap = 63627, PitAmpMap = 63628;
    // Fix round 3 (#423's second re-check, S1): the same.
    private const ushort PitGapMap = 63629, PitGapKillMap = 63630;
    private const ushort CX = 30, CY = 30;
    private const uint MaxMana = 400_000, Mana = 400_000, Hp = 10_000;
    private const byte Will = 41;

    /// <summary>The Pit trap's strike from the casters here: half of 10,000 max vita plus half of 400,000 max mana.</summary>
    private const int Strike = 205_000;

    /// <summary>Slack on a wait, a run or a status read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StatusFlagsField =
        typeof(Session).GetField("_statusFlags", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo DurationLineDueField =
        typeof(Session).GetField("_durationLineDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RevealMethod =
        typeof(Session).GetMethod("RevealableTrapsNear", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ReplacedField =
        typeof(Session).GetField("_replaced", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LeavingField =
        typeof(Session).GetField("_leaving", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public RangerSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Skill up =============================================================================================

    /// <summary>Ranger's Skill up is Spy's script under its own key: six casts climb rage 6, 7, 9, 10, 12, 14 for 3,000,
    /// 4,711, 17,960, 53,646, 135,327 and 269,420 mana, each the script's line after "[Skill up N] ", no AC, the 150 s wait
    /// under Ranger's own key each time, and one 938 s run the first cast arms; a seventh says "You have reached your max
    /// potential." and spends nothing. Red on 5bd21c7: generic's 5 mana and "You cast Skill up.".</summary>
    [Fact]
    public void SkillUpClimbsItsSixTiersAndHoldsAtTheTop()
    {
        var (caster, cOut, cc) = Caster("skill_up_ranger", RageMap, CX, CY, mana: 600_000, maxMana: 600_000);
        int[] mults = { 6, 7, 9, 10, 12, 14 }, mana = { 3_000, 4_711, 17_960, 53_646, 135_327, 269_420 };
        string[] said =
        {
            "[Skill up 1] Your senses sharpen.", "[Skill up 2] You hone your skills.",
            "[Skill up 3] Agility becomes your strongest suit..", "[Skill up 4] Acquiring cat-like reflex.",
            "[Skill up 5] Incredible skill!", "[Skill up 6] Sagat would be jealous.",
        };
        try
        {
            int baseArmor = caster.ItemArmor;
            long firstEnd = 0;
            for (int tier = 1; tier <= 6; tier++)
            {
                uint before = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                var lines = SpellCastSupport.MiniTexts(cOut);
                var (amount, left) = SpellCastSupport.Rage(caster);
                long wait = SpellCastSupport.AetherLeft(caster, "skill_up_ranger");
                if (tier == 1) firstEnd = Environment.TickCount64 + left;
                long end = Environment.TickCount64 + left;
                Assert.True(lines.SequenceEqual(new[] { said[tier - 1] }) && amount == mults[tier - 1]
                            && before - cc.Mp == mana[tier - 1] && caster.ItemArmor == baseArmor
                            && wait > 150_000 - SlackMs && wait <= 150_000 && SpellCastSupport.AetherLeft(caster, "skill_up_spy") == 0
                            && left > 938_000 - SlackMs * 2 && Math.Abs(end - firstEnd) < SlackMs,
                            $"tier {tier}: [{string.Join(" | ", lines)}], rage {amount}, spent {before - cc.Mp}, armor " +
                            $"{caster.ItemArmor - baseArmor}, wait {wait} ms, run left {left} ms");
                SpellCastSupport.ClearAether(caster, "skill_up_ranger");
            }
            uint top = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You have reached your max potential." && cc.Mp == top && SpellCastSupport.Rage(caster).Amount == 14,
                        $"at the top: [{Lines(cOut)}], spent {top - cc.Mp}, rage {SpellCastSupport.Rage(caster).Amount}");
        }
        finally { Leave(RageMap, caster); }
    }

    /// <summary>Inside its 150 s wait, "Skill up isn't ready yet."; short of tier 1's 3,000, "You do not have enough
    /// mana."; both free. A run that lapses at tier 1 keeps all its vita and says nothing (the uncast only sets rage 1).
    /// Red on 5bd21c7: generic's 5 mana and "You cast Skill up.".</summary>
    [Fact]
    public void SkillUpRefusesInsideItsWaitAndShortOfManaAndLapsesWithItsVita()
    {
        var (caster, cOut, cc) = Caster("skill_up_ranger", RageMap, CX, CY + 4);
        var (poor, pOut, pc) = Caster("skill_up_ranger", RageMap, CX, CY + 8, mana: 2_999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            uint after = cc.Mp;
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "Skill up isn't ready yet." && cc.Mp == after && SpellCastSupport.Rage(caster).Amount == 6
                        && Lines(pOut) == "You do not have enough mana." && pc.Mp == 2_999 && SpellCastSupport.Rage(poor).Amount == 1,
                        $"again [{Lines(cOut)}] spent {after - cc.Mp}; poor [{Lines(pOut)}] mana {pc.Mp}");
            cOut.Clear();
            caster.WithState(() => RageUntilField.SetValue(caster, Environment.TickCount64 - 1));
            caster.RegenTick(0, regenDue: false);
            Assert.True(cc.Hp == Hp && SpellCastSupport.MiniTexts(cOut).Count == 0 && caster.LuaCrRageTier == 0,
                        $"lapsed: vita {cc.Hp}, lines [{Lines(cOut)}], tier left {caster.LuaCrRageTier}");
        }
        finally { Leave(RageMap, caster, poor); }
    }

    // ===== Forest cover =========================================================================================

    /// <summary>Forest cover is Costume's (and Mimic's) script with Ranger's numbers: at a creature the caster takes its
    /// form for 150 s, pays 750, has graphic 16 over them and waits 200 s; short of 750, "Your will is too weak.", free.
    /// Red on 5bd21c7: generic's 5 mana and no form.</summary>
    [Fact]
    public void ForestCoverIsCostumesOnRangersRow()
    {
        var mob = AddCreature(CoverMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster("forest_cover", CoverMap, CX, CY);
        var (weak, wOut, wc) = Caster("forest_cover", CoverMap, CX, CY + 4, mana: 749);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            long wait = SpellCastSupport.AetherLeft(caster, "forest_cover");
            weak.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(caster.IsMorphed && cc.Mp == Mana - 750 && Lines(cOut) == "You cast Forest cover."
                        && Effects(cOut, caster.PlayerId).SequenceEqual(new[] { 16 }) && wait > 200_000 - SlackMs && wait <= 200_000
                        && Lines(wOut) == "Your will is too weak." && wc.Mp == 749 && !weak.IsMorphed,
                        $"morphed {caster.IsMorphed}, spent {Mana - cc.Mp}, lines [{Lines(cOut)}], graphics " +
                        $"[{string.Join(",", Effects(cOut, caster.PlayerId))}], wait {wait} ms; weak [{Lines(wOut)}]");
        }
        finally { Leave(CoverMap, caster, weak); _fx.World.DespawnMob(CoverMap, mob); }
    }

    // ===== Tree bark ============================================================================================

    /// <summary>Tree bark is Invincibility's script with Ranger's numbers. At the best armour (clamped to -80, where the roll
    /// lands 71 times in 100) a cast that lands takes 1,500, says "Harden body spell" and "You cast Tree bark.", wards its
    /// caster for 12 s (a creature's swing takes nothing) and arms a 300 s wait, with graphic 11 and sound 5; a cast that
    /// fails takes 1,500 too, says "Something went wrong." and arms nothing. Short of 1,500, "Your will is too weak.", free.
    /// Red on 5bd21c7 (generic); red without Tree bark in <c>Session.HardBodyWarded</c> (the swing lands).</summary>
    [Fact]
    public void TreeBarkLandsByInvincibilitysRollAndWardsItsCasterForTwelveSeconds()
    {
        var (caster, cOut, cc) = Caster("tree_bark", BarkMap, CX, CY, configure: ch => ch.Ac = -100);
        var (poor, pOut, pc) = Caster("tree_bark", BarkMap, CX, CY + 4, mana: 1_499);
        var mob = AddCreature(BarkMap, CX + 1, CY);
        try
        {
            bool landed = false;
            var problems = new List<string>();
            for (int i = 0; i < 40 && !landed; i++)
            {
                uint before = cc.Mp;
                cOut.Clear();
                FreeSlot(caster);
                caster.Receive(SpellCastSupport.CastFrame(0));
                string lines = Lines(cOut);
                long wait = SpellCastSupport.AetherLeft(caster, "tree_bark");
                if (lines == "Harden body spell | You cast Tree bark.")
                {
                    landed = true;
                    long ward = StatusLeft(caster, "tree_bark");
                    if (before - cc.Mp != 1_500 || wait <= 300_000 - SlackMs || wait > 300_000 || !caster.HardBodyWarded
                        || ward <= 12_000 - SlackMs || ward > 12_000
                        || !Effects(cOut, caster.PlayerId).Contains(11) || !Sounds(cOut).Contains(5))
                        problems.Add($"landed: spent {before - cc.Mp}, wait {wait}, ward {caster.HardBodyWarded} for {ward} ms, " +
                                     $"graphics [{string.Join(",", Effects(cOut, caster.PlayerId))}], sounds [{string.Join(",", Sounds(cOut))}]");
                }
                else if (lines != "Something went wrong." || before - cc.Mp != 1_500 || wait != 0 || caster.HardBodyWarded)
                    problems.Add($"cast {i}: [{lines}], spent {before - cc.Mp}, wait {wait}, ward {caster.HardBodyWarded}");
            }
            Assert.True(landed && problems.Count == 0, $"landed {landed}; " + string.Join("\n", problems));

            uint hp = cc.Hp;
            caster.ApplyMobHit(mob, 500);
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(cc.Hp == hp && Lines(pOut) == "Your will is too weak." && pc.Mp == 1_499,
                        $"after a swing {hp} -> {cc.Hp}; poor [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { _fx.World.DespawnMob(BarkMap, mob); Leave(BarkMap, caster, poor); }
    }

    /// <summary>Tree bark is one of RTK's hardBodies, as Invincibility and the poet's Harden Body are, so under any one of
    /// them casting another says "You already cast that spell." for free: Tree bark under Invincibility or Harden Body, and
    /// Invincibility or Harden Body under Tree bark. Red on 5bd21c7 (generic: Tree bark casts); red without Tree bark in
    /// <c>Session.HardBodyWarded</c> (the two casts under Tree bark go through).</summary>
    [Theory]
    [InlineData("tree_bark", "invincibility")]
    [InlineData("tree_bark", "harden_body_poet")]
    [InlineData("invincibility", "tree_bark")]
    [InlineData("harden_body_poet", "tree_bark")]
    public void TreeBarkIsAHardBodyWard(string cast, string ward)
    {
        int y = CY + 4 * Interlocked.Increment(ref _serial) % 60;
        var (caster, cOut, cc) = Caster(cast, BarkWardMap, CX, y);
        try
        {
            caster.ItemSetStatus(ward, 60_000);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(cOut) == "You already cast that spell." && cc.Mp == Mana,
                        $"{cast} under {ward}: [{Lines(cOut)}], spent {Mana - cc.Mp}");
        }
        finally { Leave(BarkWardMap, caster); }
    }

    /// <summary>A Scroll of Immortality read under any of RTK's hardBodies says "You already cast that spell." and is not
    /// spent, nor rolled, nor its Harden Body set (RTK <c>Items/Consumables/scrolls/scroll_of_immortality.lua:7-10</c>:
    /// <c>checkIfCast(hardBodies)</c>; #423's review, F3): under Tree bark, Invincibility, or the poet's Harden Body. Red
    /// with <c>game-data/item_verbs.lua</c> at 6f3c4d5, which refused under the scroll's own <c>harden_body</c> alone (the
    /// scroll rolls, and is spent when the roll lands).</summary>
    [Theory]
    [InlineData("tree_bark")]
    [InlineData("invincibility")]
    [InlineData("harden_body_poet")]
    public void AScrollOfImmortalityIsRefusedUnspentUnderAHardBody(string ward)
    {
        var scroll = Content.ItemByKey("scroll_of_immortality")!;
        int y = CY + 4 * (Interlocked.Increment(ref _serial) % 12);
        var (reader, rOut, rc) = Caster("tree_bark", ScrollMap, CX, y, configure: ch => ch.Inventory.Add(new InvItem(0, scroll.Id, 1)));
        try
        {
            reader.ItemSetStatus(ward, 60_000);
            reader.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));
            long hardened = StatusLeft(reader, "harden_body");
            Assert.True(Lines(rOut) == "You already cast that spell." && Amount(rc, 0) == 1 && hardened == 0,
                        $"under {ward}: [{Lines(rOut)}], scrolls left {Amount(rc, 0)}, Harden Body {hardened} ms");
        }
        finally { Leave(ScrollMap, reader); }
    }

    /// <summary>Under Tree bark a creature's swing and another player's swing deal nothing, while a creature's spell and
    /// another player's spell land, as under Invincibility (RTK reads hardBodies to stop a blow in its swing script alone,
    /// Scripts/hitCritChance.lua). Each damage kind through its real entry point. Red without Tree bark in
    /// <c>Session.HardBodyWarded</c> (the swings land).</summary>
    [Theory]
    [InlineData(DamageKind.MobMelee, false)]
    [InlineData(DamageKind.PlayerMelee, false)]
    [InlineData(DamageKind.MobSpell, true)]
    [InlineData(DamageKind.PlayerSpell, true)]
    public void UnderTreeBarkSwingsDoNotLandAndSpellsDo(DamageKind kind, bool lands)
    {
        int y = CY + 4 * ((int)kind + 1);
        var (victim, _, c) = Player(BarkKindMap, CX, y, hp: 100_000, maxHp: 100_000);
        var (peer, _, _) = Player(BarkKindMap, CX + 4, y);
        var mob = AddCreature(BarkKindMap, CX + 1, y);
        try
        {
            victim.ItemSetStatus("tree_bark", 60_000);
            Hit(victim, kind, peer, mob);
            Assert.True((c.Hp < 100_000) == lands, $"{kind} under Tree bark: vita {c.Hp} of 100000 (want {(lands ? "less" : "all")})");
        }
        finally { _fx.World.DespawnMob(BarkKindMap, mob); Leave(BarkKindMap, victim, peer); }
    }

    /// <summary>When the 12 s ward has run out, the caster reads "Your skin turns back to flesh." (its uncast) once, on the
    /// heartbeat, which the cast schedules for about 12 s on; nothing before, nothing after. Red without the end line in
    /// the row's verb (Tree bark on generic, 5bd21c7).</summary>
    [Fact]
    public void TreeBarkEndsWithItsLine()
    {
        var (caster, cOut, _) = Caster("tree_bark", BarkEndMap, CX, CY, configure: ch => ch.Ac = -100);
        try
        {
            Assert.True(LandTreeBark(caster, cOut), "no Tree bark cast landed in forty");
            long wake = 0;
            caster.WithState(() => wake = (long)DurationLineDueField.GetValue(caster)! - Environment.TickCount64);
            cOut.Clear();
            caster.RegenTick(0, regenDue: true);
            string before = Lines(cOut);
            caster.WithState(() =>
            {
                ((Dictionary<string, long>)StatusFlagsField.GetValue(caster)!)["tree_bark"] = Environment.TickCount64 - 1;
                DurationLineDueField.SetValue(caster, Environment.TickCount64 - 1);
            });
            caster.RegenTick(0, regenDue: false);
            string ended = Lines(cOut);
            cOut.Clear();
            caster.RegenTick(0, regenDue: true);
            Assert.True(wake > 12_000 - SlackMs && wake <= 12_000 && before == "" && ended == "Your skin turns back to flesh."
                        && Lines(cOut) == "" && !caster.HardBodyWarded,
                        $"wake in {wake} ms (want about 12,000), before [{before}], at its end [{ended}], after [{Lines(cOut)}]");
        }
        finally { Leave(BarkEndMap, caster); }
    }

    // ===== Pit trap: the cast ===================================================================================

    /// <summary>For 1,000 mana a pit trap under the caster, owned by them, gone in 20 s if nothing springs it, and the
    /// engine's "You cast Pit trap."; no wait (the script's aethers is never used), so a second cast goes off at once.
    /// Short of 1,000, "Your will is too weak.", free, no trap. Red on 5bd21c7: generic's 5 mana and no trap.</summary>
    [Fact]
    public void PitTrapSetsItsTrapUnderTheCaster()
    {
        var (caster, cOut, cc) = Caster("set_pit_trap", PitMap, CX, CY);
        var (poor, pOut, pc) = Caster("set_pit_trap", PitMap, CX, CY + 4, mana: 999);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            var trap = _fx.World.TrapsNear(PitMap, CX, CY, 0).SingleOrDefault();
            long life = trap is null ? 0 : trap.ExpiresAt - Environment.TickCount64;
            long wait = SpellCastSupport.AetherLeft(caster, "set_pit_trap");
            string first = Lines(cOut);
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            poor.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(first == "You cast Pit trap." && trap is { Kind: "pit_trap" } && trap.OwnerId == caster.PlayerId
                        && life > 20_000 - SlackMs && life <= 20_000 && wait == 0
                        && Lines(cOut) == "You cast Pit trap." && cc.Mp == Mana - 2_000
                        && _fx.World.TrapsNear(PitMap, CX, CY, 0).Length == 2
                        && Lines(pOut) == "Your will is too weak." && pc.Mp == 999 && _fx.World.TrapsNear(PitMap, CX, CY + 4, 0).Length == 0,
                        $"[{first}], trap {trap?.Kind ?? "none"} owner {trap?.OwnerId} life {life} ms, wait {wait}; again [{Lines(cOut)}] " +
                        $"spent {Mana - cc.Mp}, traps {_fx.World.TrapsNear(PitMap, CX, CY, 0).Length}; poor [{Lines(pOut)}] mana {pc.Mp}");
        }
        finally { ClearTraps(PitMap); Leave(PitMap, caster, poor); }
    }

    // ===== Pit trap on a creature ===============================================================================

    /// <summary>A creature that steps onto a Pit trap springs it, and the trap is gone: the strike is half its owner's max
    /// vita plus half their max mana (205,000 here), times the creature's armour factor with its armour clamped to -95 and
    /// to 100 (RTK's calculateDamage), credited to the owner, with graphic 44 over it. Red on 5bd21c7 (no such trap: the
    /// creature walks over it); red with the armour clamp at 100 taken out.</summary>
    [Theory]
    [InlineData(0, Strike)]
    [InlineData(-50, Strike / 2)]
    [InlineData(-100, Strike / 20)]
    [InlineData(150, Strike * 2)]
    public void ACreatureSpringsThePitTrapForHalfItsOwnersVitaAndMana(int ac, int want)
    {
        int y = CY + 4 * (Interlocked.Increment(ref _serial) % 12);
        var (owner, oOut, _) = Caster("set_pit_trap", PitCreatureMap, CX + 5, y);
        var mob = AddCreature(PitCreatureMap, CX, y, hp: 1_000_000, maxHp: 1_000_000);
        try
        {
            mob.Ac = ac;
            _fx.World.PlaceTrap(PitCreatureMap, CX + 1, (ushort)y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            var q = StepQueued(PitCreatureMap, mob, CX + 1, y, 1);
            bool gone = _fx.World.TrapsNear(PitCreatureMap, CX + 1, y, 0).Length == 0;
            oOut.Clear();
            _fx.World.FlushTickForTest(q);
            long credit = mob.ThreatOf(owner.PlayerId);
            Assert.True(gone && mob.Hp == 1_000_000 - want && credit == want && Effects(oOut, mob.Id).Contains(World.PitTrapAnim),
                        $"AC {ac}: gone {gone}, vita {mob.Hp} (want {1_000_000 - want}), owner's threat {credit}, graphics " +
                        $"[{string.Join(",", Effects(oOut, mob.Id))}]");
        }
        finally { ClearTraps(PitCreatureMap); _fx.World.DespawnMob(PitCreatureMap, mob); Leave(PitCreatureMap, owner); }
    }

    /// <summary>The owner's max vita and max mana are theirs with their gear, as RTK's <c>maxHealth</c> and <c>maxMagic</c>
    /// are: wearing a Sage's band (+1,000 of each) the strike is half of 11,000 plus half of 401,000. Red with the owner's
    /// base figures read alone in <c>Session.PeerMaxVitaMana</c>.</summary>
    [Fact]
    public void APitTrapsStrikeCountsItsOwnersGear()
    {
        var band = Content.ItemByKey("sages_band")!;
        var (owner, _, _) = Caster("set_pit_trap", PitCreatureMap, CX + 45, CY,
                                   configure: ch => ch.Equipment.Add(new InvItem { Slot = band.EquipSlot, ItemId = band.Id, Dura = band.Durability }));
        var mob = AddCreature(PitCreatureMap, CX + 40, CY, hp: 1_000_000, maxHp: 1_000_000);
        try
        {
            _fx.World.PlaceTrap(PitCreatureMap, CX + 41, CY, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            _fx.World.FlushTickForTest(StepQueued(PitCreatureMap, mob, CX + 41, CY, 1));
            int took = 1_000_000 - mob.Hp;
            Assert.True(took == (10_000 + 1_000) / 2 + (400_000 + 1_000) / 2,
                        $"took {took} (want {(10_000 + 1_000) / 2 + (400_000 + 1_000) / 2})");
        }
        finally { ClearTraps(PitCreatureMap); _fx.World.DespawnMob(PitCreatureMap, mob); Leave(PitCreatureMap, owner); }
    }

    /// <summary>The strike never takes a creature's last vita: more than it has left takes all but 1, and at 1 vita it takes
    /// nothing, the trap still used up. RTK's test is strict (<c>block.health &lt; damage</c>), so a strike exactly equal to
    /// what is left takes it all. Red with the cap taken out of <c>World.PitTrapDamage</c>; red, for the exact case, with
    /// the test made inclusive.</summary>
    [Theory]
    [InlineData(1_000, 999)]
    [InlineData(1, 0)]
    [InlineData(Strike - 1, Strike - 2)]
    [InlineData(Strike, Strike)]
    [InlineData(Strike + 1, Strike)]
    public void APitTrapLeavesACreatureItsLastVitaButAnExactStrikeTakesIt(int hp, int want)
    {
        int y = CY + 4 * (Interlocked.Increment(ref _serial) % 12);
        var (owner, _, _) = Caster("set_pit_trap", PitCreatureMap, CX + 25, y);
        var mob = AddCreature(PitCreatureMap, CX + 20, y, hp: hp, maxHp: 1_000_000);
        try
        {
            _fx.World.PlaceTrap(PitCreatureMap, CX + 21, (ushort)y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            var q = StepQueued(PitCreatureMap, mob, CX + 21, y, 1);
            bool gone = _fx.World.TrapsNear(PitCreatureMap, CX + 21, y, 0).Length == 0;
            _fx.World.FlushTickForTest(q);
            int took = hp - Math.Max(0, mob.Hp);
            Assert.True(gone && took == want && mob.Alive == (want < hp),
                        $"vita {hp}: gone {gone}, took {took} (want {want}), alive {mob.Alive}");
        }
        finally { ClearTraps(PitCreatureMap); _fx.World.DespawnMob(PitCreatureMap, mob); Leave(PitCreatureMap, owner); }
    }

    /// <summary>The last-vita rule reads the creature's vita when the strike lands, after the beat's other blows on it
    /// (#423's review, F2, the reviewer's two probes; RTK reads <c>block.health</c> when the trap fires): a creature at
    /// 1,000 whose due poison tick of 100 its own beat queues before it steps onto the trap ends at 1, the strike taking
    /// 899; one at 2 whose tick of 1 lands first is at 1 when the strike lands, which takes nothing; one a pet swings at for
    /// 50 in the same beat ends at 1 too. Red with <c>Server/World.cs</c> at 6f3c4d5, which capped the strike when it was
    /// sprung (the creature dies, credited to the trap's owner), and with the cap at landing taken out of
    /// <c>World.FlushTick</c> (the same).</summary>
    [Theory]
    [InlineData("poison", 1_000, 100)]
    [InlineData("poison", 2, 1)]
    [InlineData("pet", 1_000, 50)]
    public void APitTrapStrikeReadsTheVitaLeftWhenItLands(string first, int hp, int blow)
    {
        int y = CY + 4 * (Interlocked.Increment(ref _serial) % 12);
        var (owner, _, _) = Caster("set_pit_trap", PitBeatMap, CX + 5, y);
        var mob = AddCreature(PitBeatMap, CX, y, hp: hp, maxHp: 1_000_000);
        var pet = first == "pet" ? AddCreature(PitBeatMap, CX, y + 1, hp: 1_000, maxHp: 1_000) : null;
        try
        {
            long now = Environment.TickCount64;
            _fx.World.PlaceTrap(PitBeatMap, CX + 1, (ushort)y, "pit_trap", owner.PlayerId, now + 20_000);
            var q = new World.TickQueues();
            _fx.World.UnderWorldLockForTest(() =>
            {
                var ctx = _fx.World.MobTickContextForTest(PitBeatMap, q);
                if (pet is not null)
                {
                    pet.MinDam = blow; pet.MaxDam = blow;
                    q.MobHits.Add((PitBeatMap, pet, mob));   // a pet's swing, landed by the deferred pass before trap damage
                }
                else
                {
                    mob.PoisonUntil = now + 60_000; mob.PoisonNextTick = now - 1; mob.PoisonTickDam = blow; mob.PoisonOwnerId = 0;
                    mob.FrozenUntil = now + 60_000;            // its beat queues the tick and does not move it; the step does
                    World.MobAiTick.Step(ctx, mob);
                }
                Assert.True(World.MobMovement.StepToForTest(ctx, mob, CX + 1, y, 1));
            });
            _fx.World.FlushTickForTest(q);
            Assert.True(mob.Alive && mob.Hp == 1, $"{first} of {blow} then the strike, from {hp}: vita {mob.Hp}, alive {mob.Alive}");
        }
        finally
        {
            ClearTraps(PitBeatMap);
            _fx.World.DespawnMob(PitBeatMap, mob);
            if (pet is not null) _fx.World.DespawnMob(PitBeatMap, pet);
            Leave(PitBeatMap, owner);
        }
    }

    /// <summary>A trap hit queued after the Pit trap's strike in the same beat lands after it, so it still finishes a
    /// creature the strike left at 1, and its owner is paid (#423's re-check, R1, the re-checker's two probes; RTK fires each
    /// trap as its creature steps). A creature at 5,000 vita steps onto the Pit trap (the strike leaves it 1); then, in the
    /// same beat, another creature steps onto another player's bladestorm trap with the first in its cone (5% of 20,000 on
    /// this content-free map: 1,000), or the first darts on across that player's spear trap (3,500). Red with
    /// <c>Server/World.cs</c> at 562cc69, which landed every Pit strike after the beat's whole trap-damage list: the later
    /// hit landed first, the strike then left 1, and nobody was paid.</summary>
    [Theory]
    [InlineData("bladestorm")]
    [InlineData("spear")]
    public void ATrapHitQueuedAfterThePitStrikeStillFinishesTheCreature(string later)
    {
        int y = later == "bladestorm" ? CY : CY + 8;
        var (owner, _, _) = Caster("set_pit_trap", PitOrderMap, CX + 10, y);
        var (rogue, _, rc) = Caster("set_pit_trap", PitOrderMap, CX + 10, y + 4);   // the other trap's owner
        var a = AddCreature(PitOrderMap, CX, y, hp: 5_000, maxHp: 1_000_000);
        var b = later == "bladestorm" ? AddCreature(PitOrderMap, CX - 2, y, hp: 20_000, maxHp: 20_000) : null;
        a.Exp = 777;
        if (b is not null) b.Dir = East;
        try
        {
            long now = Environment.TickCount64;
            uint before = rc.Exp;
            _fx.World.PlaceTrap(PitOrderMap, CX + 1, (ushort)y, "pit_trap", owner.PlayerId, now + 20_000);
            if (b is not null) _fx.World.PlaceTrap(PitOrderMap, CX - 1, (ushort)y, "bladestorm", rogue.PlayerId, now + 20_000);
            else _fx.World.PlaceTrap(PitOrderMap, CX + 2, (ushort)y, "spear", rogue.PlayerId, now + 20_000);
            var q = new World.TickQueues();
            _fx.World.UnderWorldLockForTest(() =>
            {
                var ctx = _fx.World.MobTickContextForTest(PitOrderMap, q);
                Assert.True(World.MobMovement.StepToForTest(ctx, a, CX + 1, y, East));
                if (b is not null) Assert.True(World.MobMovement.StepToForTest(ctx, b, CX - 1, y, East));   // A in its cone
                else Assert.True(World.MobMovement.StepToForTest(ctx, a, CX + 2, y, East));                // the next hop
            });
            _fx.World.FlushTickForTest(q);
            Assert.True(!a.Alive && rc.Exp > before, $"{later} after the strike: vita {a.Hp}, alive {a.Alive}, rogue exp {before} -> {rc.Exp}");
        }
        finally
        {
            ClearTraps(PitOrderMap);
            _fx.World.DespawnMob(PitOrderMap, a);
            if (b is not null) _fx.World.DespawnMob(PitOrderMap, b);
            Leave(PitOrderMap, owner, rogue);
        }
    }

    /// <summary>A creature's sleep amplifier (x1.5 here) goes to the Pit trap's strike, spent when it springs, as RTK's
    /// calculateDamage spends it in the click (#423's re-check, R2, the re-checker's two probes): the strike takes 307,500
    /// (the owner's credit), and a poison tick its own beat queued before the step lands at its plain 100, a pet's 50 swing in
    /// the same beat at its plain roll. Red with <c>Server/World.cs</c> at 562cc69, which spent it when the strike landed,
    /// last in the beat: the tick or the swing landed first and took it (150, 76) and the strike landed at 205,000.</summary>
    [Theory]
    [InlineData("poison")]
    [InlineData("pet")]
    public void ACreaturesSleepAmplifierGoesToThePitStrike(string other)
    {
        int y = other == "poison" ? CY : CY + 8;
        var (owner, _, _) = Caster("set_pit_trap", PitAmpMap, CX + 5, y);
        var mob = AddCreature(PitAmpMap, CX, y, hp: 1_000_000, maxHp: 1_000_000);
        var pet = other == "pet" ? AddCreature(PitAmpMap, CX, y + 1, hp: 1_000, maxHp: 1_000) : null;
        try
        {
            long now = Environment.TickCount64;
            _fx.World.ArmDamageAmp(mob, 1.5, 60_000);
            _fx.World.PlaceTrap(PitAmpMap, CX + 1, (ushort)y, "pit_trap", owner.PlayerId, now + 20_000);
            var q = new World.TickQueues();
            _fx.World.UnderWorldLockForTest(() =>
            {
                var ctx = _fx.World.MobTickContextForTest(PitAmpMap, q);
                if (pet is not null)
                {
                    pet.MinDam = 50; pet.MaxDam = 50;
                    q.MobHits.Add((PitAmpMap, pet, mob));
                }
                else
                {
                    mob.PoisonUntil = now + 60_000; mob.PoisonNextTick = now - 1; mob.PoisonTickDam = 100; mob.PoisonOwnerId = 0;
                    mob.FrozenUntil = now + 60_000;
                    World.MobAiTick.Step(ctx, mob);
                }
                Assert.True(World.MobMovement.StepToForTest(ctx, mob, CX + 1, y, East));
            });
            _fx.World.FlushTickForTest(q);
            long strike = mob.ThreatOf(owner.PlayerId), rest = 1_000_000 - mob.Hp - strike;
            Assert.True(strike == 307_500 && (other == "poison" ? rest == 100 : rest < 70),
                        $"{other}: the strike {strike} (want 307,500), the {other}'s blow {rest}");
        }
        finally
        {
            ClearTraps(PitAmpMap);
            _fx.World.DespawnMob(PitAmpMap, mob);
            if (pet is not null) _fx.World.DespawnMob(PitAmpMap, pet);
            Leave(PitAmpMap, owner);
        }
    }

    /// <summary>A sleep-family amplifier armed on the creature after it springs the Pit trap and before the beat's trap
    /// pass lands the strike (a Sleep x1.5 or a Doze x1.3 cast in that gap, through <c>World.ArmDamageAmp</c>, the call
    /// <c>Session.LuaAmplify</c> makes) goes into the cap, so the strike still leaves a creature at 5,000 vita its last
    /// vita, credited to the owner (#423's second re-check, S1, the re-checker's probe). Red with <c>Server/World.cs</c> at
    /// 64f43df, whose cap at landing read no amplifier while the hit that landed the strike multiplied by it: the creature
    /// died inside the world lock, the owner unpaid.</summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(1.3)]
    public void AnAmplifierArmedBeforeTheStrikeLandsStillLeavesTheLastVita(double amp)
    {
        int y = amp == 1.5 ? CY : CY + 8;
        var (owner, _, _) = Caster("set_pit_trap", PitGapMap, CX + 5, y);
        var mob = AddCreature(PitGapMap, CX, y, hp: 5_000, maxHp: 1_000_000);
        try
        {
            _fx.World.PlaceTrap(PitGapMap, CX + 1, (ushort)y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            var q = StepQueued(PitGapMap, mob, CX + 1, y, East);
            _fx.World.ArmDamageAmp(mob, amp, 60_000);   // the gap: after the tick's lock, before its flush
            _fx.World.FlushTickForTest(q);
            long credit = mob.ThreatOf(owner.PlayerId);
            Assert.True(mob.Alive && mob.Hp == 1 && credit == 4_999,
                        $"x{amp} armed in the gap: vita {mob.Hp}, alive {mob.Alive}, owner's threat {credit}");
        }
        finally { ClearTraps(PitGapMap); _fx.World.DespawnMob(PitGapMap, mob); Leave(PitGapMap, owner); }
    }

    /// <summary>A strike that the amplifier armed in that gap makes exactly equal to the vita left (205,000 x1.5 on 307,500)
    /// kills, as RTK's strict test lets it, outside the world lock: the owner is paid the creature's exp, and the item it
    /// was handed falls to the floor with its 0x07 sent to a watcher from outside that lock (#423's second re-check, S1, the
    /// re-checker's probe; <c>docs/common/Locking.md</c>). Red with <c>Server/World.cs</c> at 64f43df: the kill landed inside
    /// the lock, the owner unpaid and the drop sent under it.</summary>
    [Fact]
    public void AnExactAmplifiedStrikeKillsOutsideTheWorldLockAndPaysTheOwner()
    {
        var (owner, _, oc) = Caster("set_pit_trap", PitGapKillMap, CX + 5, CY);
        var spy = new LockSpyOutbound(_fx.World);
        var watcherChar = new Character
        {
            SchemaVersion = Character.CurrentSchemaVersion, Id = _fx.World.AllocatePlayerId(), Name = Letters("Watcher"),
            Map = PitGapKillMap, X = CX + 3, Y = CY + 1, MapXs = 100, MapYs = 100, MaxHp = 1_000, Hp = 1_000, Level = 50,
        };
        var watcher = new Session(spy, 2005, _fx.Store, _fx.World, watcherChar);
        _fx.World.EnterMap(watcher, PitGapKillMap);
        var mob = AddCreature(PitGapKillMap, CX, CY, hp: 307_500, maxHp: 1_000_000);
        mob.Exp = 777;
        var wood = Content.ItemByKey("ginko_wood")!;
        mob.Handed = new List<InvItem> { new InvItem { ItemId = wood.Id, Amount = 1, Dura = wood.Durability } };
        try
        {
            _fx.World.PlaceTrap(PitGapKillMap, CX + 1, CY, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            var q = StepQueued(PitGapKillMap, mob, CX + 1, CY, East);
            uint before = oc.Exp;
            lock (spy.Sent) spy.Sent.Clear();
            _fx.World.ArmDamageAmp(mob, 1.5, 60_000);
            _fx.World.FlushTickForTest(q);
            List<(byte op, bool under)> sent;
            lock (spy.Sent) sent = spy.Sent.ToList();
            var under = sent.Where(f => f.under).Select(f => $"0x{f.op:X2}").ToList();
            bool dropSent = sent.Any(f => f.op == 0x07);
            Assert.True(!mob.Alive && oc.Exp > before && dropSent && under.Count == 0,
                        $"alive {mob.Alive}, vita {mob.Hp}, owner exp {before} -> {oc.Exp}; drop sent {dropSent}, " +
                        $"{sent.Count} frames, under the world lock [{string.Join(",", under)}]");
        }
        finally
        {
            ClearTraps(PitGapKillMap);
            foreach (var g in _fx.World.ItemsOn(PitGapKillMap)) _fx.World.PickUp(PitGapKillMap, g.X, g.Y);
            _fx.World.DespawnMob(PitGapKillMap, mob);
            _fx.World.LeaveMap(watcher, PitGapKillMap);
            Leave(PitGapKillMap, owner);
        }
    }

    /// <summary>A Pit trap whose owner has gone (logged out) does nothing and stays, for a creature and for a player on a
    /// PvP map alike: RTK's click opens on <c>Player(npc.owner)</c>, which is nil, and stops with a Lua error before it
    /// touches anything. Red with the owner check taken out of <c>World.TriggerTrapLocked</c> (the creature) or out of
    /// <c>World.CheckPlayerTrapTrigger</c> (the player).</summary>
    [Fact]
    public void AGoneOwnersPitTrapDoesNothingAndStays()
    {
        var (owner, _, _) = Caster("set_pit_trap", PitGoneMap, CX, CY);
        var mob = AddCreature(PitGoneMap, CX + 4, CY + 4);
        var run = OpenRun(Arena, 3, skip: 0);
        var (walker, wOut, wc) = Player(Arena, run.X, run.Y, hp: 1_000_000, maxHp: 1_000_000);
        try
        {
            owner.Receive(SpellCastSupport.CastFrame(0));   // the trap under the owner, at (CX, CY)
            uint ownerId = owner.PlayerId;
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "pit_trap", ownerId, Environment.TickCount64 + 20_000);
            Leave(PitGoneMap, owner);
            Assert.Null(_fx.World.Online.ById(ownerId));
            _fx.World.PlaceTrap(PitGoneMap, CX + 5, CY + 4, "pit_trap", ownerId, Environment.TickCount64 + 20_000);
            var q = StepQueued(PitGoneMap, mob, CX + 5, CY + 4, 1);
            _fx.World.FlushTickForTest(q);
            wOut.Clear();
            Walk(walker, East);
            Assert.True(q.TrapDamage.Count == 0 && mob.Hp == 100_000 && (mob.X, mob.Y) == (CX + 5, CY + 4)
                        && _fx.World.TrapsNear(PitGoneMap, CX + 5, CY + 4, 0).Length == 1
                        && walker.PlayerX == run.X + 1 && wc.Hp == 1_000_000 && Lines(wOut) == ""
                        && _fx.World.TrapsNear(Arena, run.X + 1, run.Y, 0).Length == 1,
                        $"creature: queued {q.TrapDamage.Count}, vita {mob.Hp}, at ({mob.X},{mob.Y}), traps " +
                        $"{_fx.World.TrapsNear(PitGoneMap, CX + 5, CY + 4, 0).Length}; player at {walker.PlayerX}, vita {wc.Hp}, " +
                        $"[{Lines(wOut)}], traps {_fx.World.TrapsNear(Arena, run.X + 1, run.Y, 0).Length}");
        }
        finally { ClearTraps(PitGoneMap); ClearTraps(Arena); _fx.World.DespawnMob(PitGoneMap, mob); Leave(Arena, walker); }
    }

    /// <summary>Spot traps and Watchful Eye show no Pit trap: RTK's rogue list names only the rogue's eight. Red with the Pit
    /// trap not left out of <c>Session.RevealableTrapsNear</c>.</summary>
    [Fact]
    public void SpotTrapsShowsNoPitTrap()
    {
        var (rogue, _, _) = _fx.PlayerWith(Letters("Rogue"), ch => { ch.ClassName = "Rogue"; ch.Level = 99; Size(ch, PitRevealMap); },
                                           PitRevealMap, CX, CY);
        try
        {
            _fx.World.PlaceTrap(PitRevealMap, CX + 1, CY, "pit_trap", rogue.PlayerId, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(PitRevealMap, CX, CY + 1, "dart", 1);
            Trap[] seen = Array.Empty<Trap>();
            rogue.WithState(() => seen = (Trap[])RevealMethod.Invoke(rogue, null)!);
            Assert.Equal("dart", string.Join(",", seen.Select(t => t.Kind)));
        }
        finally { ClearTraps(PitRevealMap); Leave(PitRevealMap, rogue); }
    }

    // ===== Pit trap on a player =================================================================================

    /// <summary>A player who walks onto a Pit trap on a PvP map reads "CHOMP!" and takes the strike through their armour
    /// (AC -50 halves 205,000), graphic 44 over them, the owner marked as their foe, and the trap is gone. No ward stops it,
    /// Harden Body's included (RTK's plain removeHealth). Red on 5bd21c7: nothing springs.</summary>
    [Fact]
    public void APlayerSpringsThePitTrapOnAPvpMap()
    {
        Assert.True(Content.IsPvpMap(Arena));
        var (owner, _, _) = Caster("set_pit_trap", PitMap, CX + 20, CY);
        var run = OpenRun(Arena, 3, skip: 1);
        var wardRun = OpenRun(Arena, 3, skip: 2);
        var (victim, vOut, vc) = Player(Arena, run.X, run.Y, hp: 1_000_000, maxHp: 1_000_000, configure: ch => ch.Ac = -50);
        var (warded, dOut, dc) = Player(Arena, wardRun.X, wardRun.Y, hp: 1_000_000, maxHp: 1_000_000, configure: ch => ch.Ac = -50);
        try
        {
            warded.ItemSetStatus("harden_body_poet", 60_000);
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(Arena, (ushort)(wardRun.X + 1), (ushort)wardRun.Y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            vOut.Clear(); dOut.Clear();
            Walk(victim, East);
            Walk(warded, East);
            Assert.True(Lines(vOut) == "CHOMP!" && vc.Hp == 1_000_000 - Strike / 2 && Effects(vOut, victim.PlayerId).Contains(World.PitTrapAnim)
                        && victim.PvpFoeId == owner.PlayerId && _fx.World.TrapsNear(Arena, run.X + 1, run.Y, 0).Length == 0
                        && Lines(dOut) == "CHOMP!" && dc.Hp == 1_000_000 - Strike / 2,
                        $"[{Lines(vOut)}] vita {vc.Hp} (want {1_000_000 - Strike / 2}), graphics [{string.Join(",", Effects(vOut, victim.PlayerId))}], " +
                        $"foe {victim.PvpFoeId}; under Harden Body [{Lines(dOut)}] vita {dc.Hp}");
        }
        finally { ClearTraps(Arena); Leave(Arena, victim, warded); Leave(PitMap, owner); }
    }

    /// <summary>The strike never takes a player's last vita: at 1,000 vita it leaves 1; at 1 it takes nothing, though the
    /// player still reads "CHOMP!" and the trap is still used up. Red with the cap taken out of <c>World.PitTrapDamage</c>
    /// (the first dies).</summary>
    [Fact]
    public void APitTrapLeavesAPlayerTheirLastVita()
    {
        var (owner, _, _) = Caster("set_pit_trap", PitMap, CX + 20, CY + 4);
        var run = OpenRun(Arena, 3, skip: 3);
        var oneRun = OpenRun(Arena, 3, skip: 4);
        var (low, lOut, lc) = Player(Arena, run.X, run.Y, hp: 1_000, maxHp: 1_000_000);
        var (one, nOut, nc) = Player(Arena, oneRun.X, oneRun.Y, hp: 1, maxHp: 1_000_000);
        try
        {
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(Arena, (ushort)(oneRun.X + 1), (ushort)oneRun.Y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            lOut.Clear(); nOut.Clear();
            Walk(low, East);
            Walk(one, East);
            Assert.True(lc.Hp == 1 && !low.IsDead && Lines(lOut) == "CHOMP!" && nc.Hp == 1 && !one.IsDead && Lines(nOut) == "CHOMP!"
                        && _fx.World.TrapsNear(Arena, oneRun.X + 1, oneRun.Y, 0).Length == 0,
                        $"low: vita {lc.Hp} [{Lines(lOut)}]; at one: vita {nc.Hp} [{Lines(nOut)}]");
        }
        finally { ClearTraps(Arena); Leave(Arena, low, one); Leave(PitMap, owner); }
    }

    /// <summary>Off a PvP map a player who walks onto a Pit trap springs nothing and the trap stays (RTK's click returns for
    /// <c>not block:canPK(block)</c>); a ghost on a PvP map the same (<c>block.state == 1</c>). Red with the player gate taken
    /// out of <c>World.CheckPlayerTrapTrigger</c>.</summary>
    [Fact]
    public void APlayerSpringsNoPitTrapOffAPvpMapOrAsAGhost()
    {
        Assert.False(Content.IsPvpMap(PitOffMap));
        var (owner, _, _) = Caster("set_pit_trap", PitMap, CX + 20, CY + 8);
        var (walker, wOut, wc) = Player(PitOffMap, CX, CY);
        var run = OpenRun(Arena, 3, skip: 5);
        var (ghost, gOut, _) = Player(Arena, run.X, run.Y, hp: 0);
        try
        {
            Assert.True(ghost.IsDead);
            _fx.World.PlaceTrap(PitOffMap, CX + 1, CY, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            _fx.World.PlaceTrap(Arena, (ushort)(run.X + 1), (ushort)run.Y, "pit_trap", owner.PlayerId, Environment.TickCount64 + 20_000);
            wOut.Clear(); gOut.Clear();
            Walk(walker, East);
            Walk(ghost, East);
            Assert.True((walker.PlayerX, ghost.PlayerX) == (CX + 1, run.X + 1) && wc.Hp == Hp && Lines(wOut) == "" && Lines(gOut) == ""
                        && _fx.World.TrapsNear(PitOffMap, CX + 1, CY, 0).Length == 1
                        && _fx.World.TrapsNear(Arena, run.X + 1, run.Y, 0).Length == 1,
                        $"walker at {walker.PlayerX} vita {wc.Hp} [{Lines(wOut)}]; ghost at {ghost.PlayerX} [{Lines(gOut)}]");
        }
        finally { ClearTraps(PitOffMap); ClearTraps(Arena); Leave(PitOffMap, walker); Leave(Arena, ghost); Leave(PitMap, owner); }
    }

    // ===== Carve arrow ==========================================================================================

    /// <summary>The cast: "You cast Carve arrow.", the 10,000 s wait armed before the menu, no mana (the script checks 8,000
    /// and never takes it), and RTK's menu over the five arrows and one more at each of marks 1 to 4 (Sleep, Explosive,
    /// Dark, Deamon). Red on 5bd21c7: generic's 5 mana, no menu.</summary>
    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "|Sleep")]
    [InlineData(2, "|Sleep|Explosive")]
    [InlineData(3, "|Sleep|Explosive|Dark")]
    [InlineData(4, "|Sleep|Explosive|Dark|Deamon")]
    public void CarveArrowOffersItsArrowsByMarkAndSpendsNoMana(int mark, string extra)
    {
        var (caster, cOut, cc) = Caster("carve_arrow", CarveMap, CX, CY + 4 * mark, configure: ch => ch.Mark = (byte)mark);
        try
        {
            string lines = "", menu = "";
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                lines = Lines(cOut);
                menu = string.Join(" / ", Menus(cOut));
            });
            long wait = SpellCastSupport.AetherLeft(caster, "carve_arrow");
            Assert.True(lines == "You cast Carve arrow." && cc.Mp == Mana && wait > 10_000_000 - SlackMs && wait <= 10_000_000
                        && menu == "What do you wish to make, Ranger?|Basic wooden|Split head|Cursed head|Flame head|Steel tip" + extra,
                        $"mark {mark}: [{lines}], spent {Mana - cc.Mp}, wait {wait} ms, menu [{menu}]");
        }
        finally { Leave(CarveMap, caster); }
    }

    /// <summary>Each pick takes 3 Ginko wood and the arrow's reagent and gives one of the arrow at its own durability (30),
    /// then asks again, as the script's loop: the part-full Ginko stack is taken from before the full one, with
    /// "Ginko wood (3)" for a stack left part-full, and an emptied reagent stack goes. Short of the reagent, the box "You
    /// need a Chestnut to make this arrow." ends it, nothing taken. Red on 5bd21c7 (no menu); red with the full stack taken
    /// first.</summary>
    [Fact]
    public void CarveArrowMakesAnArrowAndAsksAgain()
    {
        var wood = Content.ItemByKey("ginko_wood")!;
        var (caster, cOut, cc) = Caster("carve_arrow", CarveMakeMap, CX, CY, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(1, wood.Id, 100));
            ch.Inventory.Add(new InvItem(2, wood.Id, 7));
            ch.Inventory.Add(new InvItem(3, Content.ItemByKey("pebble")!.Id, 1));
            ch.Inventory.Add(new InvItem(4, Content.ItemByKey("chestnut")!.Id, 1));
        });
        try
        {
            string menu = "", firstLines = "", firstMenu = "", secondMenu = "", lastMenu = "";
            List<Box> lastBoxes = new();
            int woodAfterFirst = 0, woodAfterSecond = 0;
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                menu = string.Join(" / ", Menus(cOut));
                cOut.Clear();
                caster.Receive(MenuFrame(4));                 // Flame head: a Pebble
                firstLines = Lines(cOut);
                firstMenu = string.Join(" / ", Menus(cOut));
                woodAfterFirst = Amount(cc, 2);
                cOut.Clear();
                caster.Receive(MenuFrame(1));                 // Basic wooden: a Chestnut
                secondMenu = string.Join(" / ", Menus(cOut));
                woodAfterSecond = Amount(cc, 2);
                cOut.Clear();
                caster.Receive(MenuFrame(2));                 // Split head: a Chestnut, none left
                lastBoxes = Boxes(cOut);
                lastMenu = string.Join(" / ", Menus(cOut));
            });
            var flame = cc.Inventory.SingleOrDefault(i => i.ItemId == Content.ItemByKey("flame_head_arrows")!.Id);
            var basic = cc.Inventory.SingleOrDefault(i => i.ItemId == Content.ItemByKey("ranger_arrows")!.Id);
            Assert.True(firstLines == "Ginko wood (3)" && woodAfterFirst == 4 && Amount(cc, 1) == 100 && firstMenu == menu
                        && secondMenu == menu && woodAfterSecond == 1
                        && flame is { Amount: 1, Dura: 30 } && basic is { Amount: 1, Dura: 30 }
                        && cc.Inventory.All(i => i.ItemId != Content.ItemByKey("pebble")!.Id && i.ItemId != Content.ItemByKey("chestnut")!.Id)
                        && lastBoxes is [{ Kind: 0x0001 } b] && b.Text == "You need a Chestnut to make this arrow." && lastMenu == ""
                        && Amount(cc, 2) == 1 && cc.Mp == Mana,
                        $"first [{firstLines}], wood {woodAfterFirst} + {Amount(cc, 1)}, menu again {firstMenu == menu}; second wood " +
                        $"{woodAfterSecond}, menu again {secondMenu == menu}; flame {flame?.Amount}/{flame?.Dura}, basic " +
                        $"{basic?.Amount}/{basic?.Dura}; last [{string.Join(" / ", lastBoxes)}] menu [{lastMenu}]; spent {Mana - cc.Mp}");
        }
        finally { Leave(CarveMakeMap, caster); }
    }

    /// <summary>Short of 8,000, "Your will is too weak.", free, no wait and no menu; inside the 10,000 s wait, "Carve arrow
    /// isn't ready yet."; short of 3 Ginko wood, the box "You need some ginko wood to make this arrow." (asked before the
    /// reagent); with the wood and no Metal, Steel tip's box "You need a Metal to make this arrow."; each refused pick takes
    /// nothing and ends it. Red on 5bd21c7 (generic).</summary>
    [Fact]
    public void CarveArrowRefusals()
    {
        var wood = Content.ItemByKey("ginko_wood")!;
        var (poor, pOut, pc) = Caster("carve_arrow", CarveRefuseMap, CX, CY, mana: 7_999);
        var (shortWood, sOut, sc) = Caster("carve_arrow", CarveRefuseMap, CX, CY + 4, configure: ch => ch.Inventory.Add(new InvItem(0, wood.Id, 2)));
        var (noMetal, mOut, mc) = Caster("carve_arrow", CarveRefuseMap, CX, CY + 8, configure: ch => ch.Inventory.Add(new InvItem(0, wood.Id, 5)));
        try
        {
            List<Box> woodBox = new(), metalBox = new();
            string woodMenu = "", metalMenu = "";
            OnOwnThread(() =>
            {
                poor.Receive(SpellCastSupport.CastFrame(0));
                shortWood.Receive(SpellCastSupport.CastFrame(0));
                sOut.Clear();
                shortWood.Receive(MenuFrame(1));
                woodBox = Boxes(sOut);
                woodMenu = string.Join(" / ", Menus(sOut));
                noMetal.Receive(SpellCastSupport.CastFrame(0));
                mOut.Clear();
                noMetal.Receive(MenuFrame(5));
                metalBox = Boxes(mOut);
                metalMenu = string.Join(" / ", Menus(mOut));
            });
            sOut.Clear();
            FreeSlot(shortWood);
            shortWood.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(pOut) == "Your will is too weak." && Menus(pOut).Count == 0 && pc.Mp == 7_999
                        && SpellCastSupport.AetherLeft(poor, "carve_arrow") == 0
                        && woodBox is [{ Kind: 0x0001, Next: 0 } w] && w.Text == "You need some ginko wood to make this arrow."
                        && woodMenu == "" && Amount(sc, 0) == 2
                        && metalBox is [{ Kind: 0x0001 } m] && m.Text == "You need a Metal to make this arrow." && metalMenu == ""
                        && Amount(mc, 0) == 5 && Lines(sOut) == "Carve arrow isn't ready yet.",
                        $"poor [{Lines(pOut)}] mana {pc.Mp}; wood [{string.Join(" / ", woodBox)}] menu [{woodMenu}] left {Amount(sc, 0)}; " +
                        $"metal [{string.Join(" / ", metalBox)}] menu [{metalMenu}] left {Amount(mc, 0)}; again [{Lines(sOut)}]");
        }
        finally { Leave(CarveRefuseMap, poor, shortWood, noMetal); }
    }

    /// <summary>An arrow that does not fit in a full bag lies on the floor at the caster's feet, at its durability, with
    /// "You can't have more.", as RTK's addItem drops it (pc_dropitemfull); the materials are still taken and the menu
    /// comes again. Red with the floor drop taken out of <c>Session.LuaGiveItem</c> (the arrow is lost).</summary>
    [Fact]
    public void ACarvedArrowThatDoesNotFitLiesAtTheCastersFeet()
    {
        var wood = Content.ItemByKey("ginko_wood")!;
        var pebble = Content.ItemByKey("pebble")!;
        var filler = Content.ItemByKey("acorn")!;
        var (caster, cOut, cc) = Caster("carve_arrow", CarveFullMap, CX, CY, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(0, wood.Id, 7));
            ch.Inventory.Add(new InvItem(1, pebble.Id, 2));
            for (byte s = 2; s < ch.MaxInv; s++) ch.Inventory.Add(new InvItem(s, filler.Id, 1));
        });
        try
        {
            string lines = "", menuAgain = "";
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                cOut.Clear();
                caster.Receive(MenuFrame(4));                 // Flame head
                lines = Lines(cOut);
                menuAgain = string.Join(" / ", Menus(cOut));
            });
            var arrows = Content.ItemByKey("flame_head_arrows")!;
            var floor = _fx.World.ItemsOn(CarveFullMap).Where(g => g.X == CX && g.Y == CY && g.ItemId == arrows.Id).ToList();
            Assert.True(lines == "Ginko wood (3) | Pebble (1) | You can't have more." && menuAgain.StartsWith("What do you wish to make, Ranger?")
                        && floor is [{ Amount: 1, Dura: 30 }] && cc.Inventory.All(i => i.ItemId != arrows.Id)
                        && Amount(cc, 0) == 4 && Amount(cc, 1) == 1,
                        $"[{lines}], menu again [{menuAgain}], floor [{string.Join(", ", floor.Select(g => $"{g.Amount}/{g.Dura}"))}], " +
                        $"wood {Amount(cc, 0)}, pebble {Amount(cc, 1)}");
        }
        finally
        {
            foreach (var g in _fx.World.ItemsOn(CarveFullMap)) _fx.World.PickUp(CarveFullMap, g.X, g.Y);
            Leave(CarveFullMap, caster);
        }
    }

    /// <summary>A Carve arrow pick that waits at a contended Lua gate while a second login replaces the caster (the gate's
    /// slow path lets the caster's monitor go, so the kick gets in, writes the row and latches the fence) takes nothing and
    /// makes nothing, with a full bag (no arrow on the floor) and with room (none in the bag): the materials in memory and
    /// in the saved row are as they were (#423's review, F1, the reviewer's probe). Red with
    /// <c>Server/Session.Spells.cs</c> at 6f3c4d5: with a full bag an arrow lies on the floor while the saved row still
    /// holds all the wood and pebbles, an arrow out of nothing; with room the take and the arrow are in memory only.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReplacedCastersCarvePickTakesAndMakesNothing(bool fullBag)
    {
        var wood = Content.ItemByKey("ginko_wood")!;
        var pebble = Content.ItemByKey("pebble")!;
        var filler = Content.ItemByKey("acorn")!;
        var arrows = Content.ItemByKey("flame_head_arrows")!;
        int x = fullBag ? CX : CX + 10;
        var (caster, _, cc) = Caster("carve_arrow", CarveGoneMap, x, CY, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(0, wood.Id, 7));
            ch.Inventory.Add(new InvItem(1, pebble.Id, 2));
            if (fullBag) for (byte s = 2; s < ch.MaxInv; s++) ch.Inventory.Add(new InvItem(s, filler.Id, 1));
        });
        string name = cc.Name;
        var gateHeld = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        Thread? holder = null;
        try
        {
            OnOwnThread(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            holder = new Thread(() => { using (Session.EnterScriptGate()) { gateHeld.Set(); release.Wait(); } }) { IsBackground = true };
            holder.Start();
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(30)), "the gate was never taken");
            Exception? pickFault = null;
            var picker = new Thread(() => { try { caster.Receive(MenuFrame(4)); } catch (Exception e) { pickFault = e; } }) { IsBackground = true };
            picker.Start();                                    // Flame head: 3 Ginko wood and a Pebble
            WaitUntilDoneOrBlocked(picker);
            Assert.True(picker.IsAlive, "the pick did not wait at the gate");
            var kicker = new Thread(() => caster.KickForReplacement()) { IsBackground = true };
            kicker.Start();
            Assert.True(kicker.Join(TimeSpan.FromSeconds(10)), "the kick did not land while the pick waited");
            release.Set();
            Assert.True(picker.Join(TimeSpan.FromSeconds(30)), "the pick never finished");
            Assert.Null(pickFault);
            int floor = _fx.World.ItemsOn(CarveGoneMap).Where(g => g.X == x && g.Y == CY && g.ItemId == arrows.Id).Sum(g => g.Amount);
            int Held(IEnumerable<InvItem> bag, ItemDef def) => bag.Where(i => i.ItemId == def.Id).Sum(i => i.Amount);
            var saved = _fx.Store.Load(name).Character!;
            Assert.True(caster.IsReplaced && floor == 0 && Held(cc.Inventory, arrows) == 0
                        && Held(cc.Inventory, wood) == 7 && Held(cc.Inventory, pebble) == 2
                        && Held(saved.Inventory, wood) == 7 && Held(saved.Inventory, pebble) == 2 && Held(saved.Inventory, arrows) == 0,
                        $"full bag {fullBag}: replaced {caster.IsReplaced}, floor arrows {floor}, in memory arrows " +
                        $"{Held(cc.Inventory, arrows)} wood {Held(cc.Inventory, wood)} pebble {Held(cc.Inventory, pebble)}; " +
                        $"saved wood {Held(saved.Inventory, wood)} pebble {Held(saved.Inventory, pebble)} arrows {Held(saved.Inventory, arrows)}");
        }
        finally
        {
            release.Set();
            holder?.Join(TimeSpan.FromSeconds(10));
            foreach (var g in _fx.World.ItemsOn(CarveGoneMap)) _fx.World.PickUp(CarveGoneMap, g.X, g.Y);
            Leave(CarveGoneMap, caster);
        }
    }

    /// <summary>Each primitive this slice adds that writes the caster's saved state or the world refuses a caster who is
    /// leaving or whose session a second login has replaced, asked under the caster's monitor (#423's review, F1):
    /// <c>ctx:takeItem</c> takes nothing, <c>ctx:giveItem</c> gives nothing to the bag or (with the bag full) the floor,
    /// and <c>ctx:setTrap</c> sets no Pit trap. Red with each primitive's <c>CasterGone</c> test taken out of
    /// <c>Server/Session.Spells.cs</c>.</summary>
    [Theory]
    [InlineData("leaving")]
    [InlineData("replaced")]
    public void TheItemAndTrapPrimitivesRefuseAGoneCaster(string why)
    {
        var wood = Content.ItemByKey("ginko_wood")!;
        var filler = Content.ItemByKey("acorn")!;
        var arrows = Content.ItemByKey("flame_head_arrows")!;
        int x = why == "leaving" ? CX : CX + 10;
        var (caster, _, cc) = Caster("set_pit_trap", GoneCasterMap, x, CY, configure: ch =>
        {
            ch.Inventory.Add(new InvItem(0, wood.Id, 7));
            for (byte s = 1; s < ch.MaxInv; s++) ch.Inventory.Add(new InvItem(s, filler.Id, 1));
        });
        var pit = Content.SpellByKey("set_pit_trap")!;
        bool took = true;
        int given = -1;
        try
        {
            caster.WithState(() =>
            {
                if (why == "leaving") LeavingField.SetValue(caster, true); else ReplacedField.SetValue(caster, 1);
                try
                {
                    took = caster.LuaTakeItem("ginko_wood", 3);
                    given = caster.LuaGiveItem("flame_head_arrows", 1);
                    caster.LuaSetSpyTrap(pit, "pit_trap", 20_000);
                }
                finally
                {
                    if (why == "leaving") LeavingField.SetValue(caster, false); else ReplacedField.SetValue(caster, 0);
                }
            });
            int floor = _fx.World.ItemsOn(GoneCasterMap).Count(g => g.ItemId == arrows.Id);
            int traps = _fx.World.TrapsNear(GoneCasterMap, x, CY, 0).Length;
            Assert.True(!took && given == 0 && Amount(cc, 0) == 7 && cc.Inventory.All(i => i.ItemId != arrows.Id) && floor == 0 && traps == 0,
                        $"{why}: took {took}, gave {given}, wood {Amount(cc, 0)}, floor arrows {floor}, traps {traps}");
        }
        finally
        {
            ClearTraps(GoneCasterMap);
            foreach (var g in _fx.World.ItemsOn(GoneCasterMap)) _fx.World.PickUp(GoneCasterMap, g.X, g.Y);
            Leave(GoneCasterMap, caster);
        }
    }

    // ===== Call companion =======================================================================================

    /// <summary>The cast: sound 22, "You cast Call companion.", 2,000 mana and the 420 s wait, all before RTK's menu of seven
    /// creatures. The pick: that creature (mobs.csv call_companion_*) on the faced tile, owned by the caster for 600 s, and
    /// "You summon a &lt;creature&gt;". Red on 5bd21c7: generic's 5 mana and no menu.</summary>
    [Theory]
    [InlineData(1, "call_companion_red_wolf", "Red wolf")]
    [InlineData(7, "call_companion_tiger", "Tiger")]
    public void CallCompanionOffersSevenCreaturesAndCallsThePick(byte pick, string key, string name)
    {
        int y = CY + 4 * pick;
        var (caster, cOut, cc) = Caster("call_companion", CompanionMap, CX, y);
        Mob? called = null;
        try
        {
            string cast = "", menu = "", picked = "";
            List<int> sounds = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                cast = Lines(cOut);
                menu = string.Join(" / ", Menus(cOut));
                sounds = Sounds(cOut);
                cOut.Clear();
                caster.Receive(MenuFrame(pick));
                picked = Lines(cOut);
            });
            long wait = SpellCastSupport.AetherLeft(caster, "call_companion");
            called = _fx.World.MobsAt(CompanionMap, CX + 1, y).SingleOrDefault(m => !m.IsNpc);
            long life = called is null ? 0 : called.PetExpiresAt - Environment.TickCount64;
            Assert.True(cast == "You cast Call companion." && sounds.Contains(22) && cc.Mp == Mana - 2_000 && wait > 420_000 - SlackMs
                        && wait <= 420_000
                        && menu == "Which creature do you wish to call?|Red wolf|Green squirrel|Black lobster|Rabbit|Fox|Bear|Tiger"
                        && called is not null && called.Key == key && called.OwnerId == caster.PlayerId
                        && life > 600_000 - SlackMs && life <= 600_000 && picked == $"You summon a {name}",
                        $"cast [{cast}], sounds [{string.Join(",", sounds)}], spent {Mana - cc.Mp}, wait {wait} ms, menu [{menu}], " +
                        $"called {called?.Key ?? "none"} owner {called?.OwnerId} life {life} ms, [{picked}]");
        }
        finally { if (called is not null) _fx.World.DespawnMob(CompanionMap, called); Leave(CompanionMap, caster); }
    }

    /// <summary>Short of 2,000, "Your will is too weak.", free and no menu; inside the 420 s wait, "Call companion isn't
    /// ready yet."; a menu closed without a pick calls nothing, but its mana and wait are gone (both came before the menu).
    /// Red on 5bd21c7 (generic).</summary>
    [Fact]
    public void CallCompanionRefusalsAndAClosedMenu()
    {
        var (poor, pOut, pc) = Caster("call_companion", CompanionRefuseMap, CX, CY, mana: 1_999);
        var (caster, cOut, cc) = Caster("call_companion", CompanionRefuseMap, CX, CY + 4);
        try
        {
            poor.Receive(SpellCastSupport.CastFrame(0));
            OnOwnThread(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            long wait = SpellCastSupport.AetherLeft(caster, "call_companion");
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(Lines(pOut) == "Your will is too weak." && Menus(pOut).Count == 0 && pc.Mp == 1_999
                        && cc.Mp == Mana - 2_000 && wait > 420_000 - SlackMs && Lines(cOut) == "Call companion isn't ready yet."
                        && _fx.World.MobsAt(CompanionRefuseMap, CX + 1, CY + 4).Count == 0,
                        $"poor [{Lines(pOut)}] mana {pc.Mp}; spent {Mana - cc.Mp}, wait {wait}; again [{Lines(cOut)}]");
        }
        finally { Leave(CompanionRefuseMap, poor, caster); }
    }

    // ===== Scouting trip ========================================================================================

    /// <summary>The cast spends 2,000 and arms the 25,000 s wait before RTK's menu, with no line. "Mark a person" asks the
    /// name ("Who will be granted the mark?"); a name typed in any case, of a player within 10 tiles, counts their saved
    /// registry key scouted_with_rangers up and gives them the legend "Scouted with Rangers 1 times, marked by
    /// &lt;caster&gt; (&lt;date&gt;)", icon 12, colour 1. The caster reads "&lt;player&gt; has been recognized for scouting."
    /// in a plain box with the next arrow; once they answer it the player gets one plain box with no button, "You have been
    /// recognized for scouting with the Rangers.", and the conversation they had waiting is dropped. A second mark counts 2
    /// and the one legend says so. Red on 5bd21c7 (generic: 5 mana, no menu).</summary>
    [Fact]
    public void ScoutingTripMarksANearbyPlayerAndTellsThem()
    {
        var (target, tOut, tc) = Player(ScoutMap, CX + 6, CY + 3, name: Letters("Scout"));
        var (caster, cOut, cc) = Caster("scouting_trip", ScoutMap, CX, CY);
        try
        {
            string castLines = "", menu = "";
            List<Box> asked = new();
            OnOwnThread(() =>
            {
                _ = target.DlgInputPush(0, 0, "a conversation the player has waiting");
                caster.Receive(SpellCastSupport.CastFrame(0));
                castLines = Lines(cOut);
                menu = string.Join(" / ", Menus(cOut));
                cOut.Clear();
                caster.Receive(MenuFrame(1));
                asked = Boxes(cOut);
                cOut.Clear();
                tOut.Clear();
                caster.Receive(InputFrame(target.CharName.ToLowerInvariant()));
            });
            long wait = SpellCastSupport.AetherLeft(caster, "scouting_trip");
            var mine = Boxes(cOut);
            var before = Boxes(tOut);
            bool waiting = target.DialogBusy;
            int count1 = QuestState.Over(tc, QuestState.Registry).Get("scouted_with_rangers");
            var legend1 = tc.Legends.Where(l => l.Name == "scouted_with_rangers").Select(l => (l.Text, l.Icon, l.Color)).ToList();
            string date = Character.GameDate;
            OnOwnThread(() => caster.Receive(NextFrame()));
            var theirs = Boxes(tOut);
            Assert.True(castLines == "" && menu == "What do you wish to do?|Mark a person|Remove a mark" && cc.Mp == Mana - 2_000
                        && wait > 25_000_000 - SlackMs && wait <= 25_000_000
                        && asked is [{ Kind: 0x0404, Head: 0, Gfx: 0 } a] && a.Text == "Who will be granted the mark?"
                        && count1 == 1
                        && legend1.SequenceEqual(new[] { ($"Scouted with Rangers 1 times, marked by {caster.CharName} ({date})", (byte)12, (byte)1) })
                        && mine is [{ Kind: 0x0001, Head: 0, Next: 1 } m] && m.Text == $"{target.CharName} has been recognized for scouting."
                        && before.Count == 0 && waiting
                        && theirs is [{ Kind: 0x0001, Head: 0, Next: 0 } t] && t.Id == target.PlayerId
                        && t.Text == "You have been recognized for scouting with the Rangers." && !target.DialogBusy,
                        $"cast [{castLines}], menu [{menu}], spent {Mana - cc.Mp}, wait {wait}; asked [{string.Join(" / ", asked)}]; " +
                        $"count {count1}, legend [{string.Join(" / ", legend1)}]; caster's [{string.Join(" / ", mine)}]; player's before " +
                        $"[{string.Join(" / ", before)}] (waiting {waiting}), after [{string.Join(" / ", theirs)}] (waiting {target.DialogBusy})");

            SpellCastSupport.ClearAether(caster, "scouting_trip");
            FreeSlot(caster);
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(MenuFrame(1));
                caster.Receive(InputFrame(target.CharName));
            });
            int count2 = QuestState.Over(tc, QuestState.Registry).Get("scouted_with_rangers");
            var legend2 = tc.Legends.Where(l => l.Name == "scouted_with_rangers").Select(l => l.Text).ToList();
            Assert.True(count2 == 2 && legend2.SequenceEqual(new[] { $"Scouted with Rangers 2 times, marked by {caster.CharName} ({date})" }),
                        $"second: count {count2}, legend [{string.Join(" / ", legend2)}]");
        }
        finally { Leave(ScoutMap, target, caster); }
    }

    /// <summary>"Remove a mark" asks "Who will have their mark removed?"; for a marked player within 10 tiles their count
    /// goes to 0 and the legend goes; the caster reads "&lt;player&gt; has had their mark removed." with the next arrow,
    /// and once they answer it the player gets "You have had your mark removed for Scouting with Rangers by
    /// &lt;caster&gt;." in a plain box with no button. Red on 5bd21c7 (generic).</summary>
    [Fact]
    public void ScoutingTripRemovesAMark()
    {
        var (target, tOut, tc) = Player(ScoutUnmarkMap, CX + 1, CY + 10, name: Letters("Marked"), configure: ch =>
        {
            QuestState.Over(ch, QuestState.Registry).Set("scouted_with_rangers", 3);
            ch.Legends.Add(new Legend(12, 1, "Scouted with Rangers 3 times, marked by Someone (Yuri 1, Spring)", "scouted_with_rangers"));
        });
        var (caster, cOut, _) = Caster("scouting_trip", ScoutUnmarkMap, CX, CY);
        try
        {
            List<Box> asked = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                cOut.Clear();
                caster.Receive(MenuFrame(2));
                asked = Boxes(cOut);
                cOut.Clear();
                tOut.Clear();
                caster.Receive(InputFrame(target.CharName));
            });
            var mine = Boxes(cOut);
            int count = QuestState.Over(tc, QuestState.Registry).Get("scouted_with_rangers");
            bool legend = tc.Legends.Any(l => l.Name == "scouted_with_rangers");
            OnOwnThread(() => caster.Receive(NextFrame()));
            var theirs = Boxes(tOut);
            Assert.True(asked is [{ Kind: 0x0404 } a] && a.Text == "Who will have their mark removed?" && count == 0 && !legend
                        && mine is [{ Kind: 0x0001, Next: 1 } m] && m.Text == $"{target.CharName} has had their mark removed."
                        && theirs is [{ Kind: 0x0001, Next: 0 } t]
                        && t.Text == $"You have had your mark removed for Scouting with Rangers by {caster.CharName}.",
                        $"asked [{string.Join(" / ", asked)}], count {count}, legend {legend}; caster's [{string.Join(" / ", mine)}]; " +
                        $"player's [{string.Join(" / ", theirs)}]");
        }
        finally { Leave(ScoutUnmarkMap, target, caster); }
    }

    /// <summary>The typed name's refusals, each a plain box with no button that ends it, in the script's order, the 2,000
    /// and the wait already spent (they came before the menu): anything but letters, "You can only enter letters and
    /// spaces."; nobody online by it, "That player is currently not online."; the caster, "You cannot provide your own
    /// recognition."; a player on another map, or 11 tiles off on one axis, "That person is not near you." (10 tiles off on
    /// both is near, and marked). Short of 2,000, "Your will is too weak.", free and no menu; inside the wait, "Scouting
    /// trip isn't ready yet.". Red on 5bd21c7 (generic); red with the range test taken out of <c>Session.LuaTargetWithin</c>.</summary>
    [Fact]
    public void ScoutingTripRefusals()
    {
        var (far, _, farC) = Player(ScoutFarMap, CX, CY, name: Letters("Far"));
        var (eleven, _, elevenC) = Player(ScoutRefuseMap, CX + 11, CY + 20, name: Letters("Eleven"));
        var (ten, _, tenC) = Player(ScoutRefuseMap, CX + 10, CY + 10, name: Letters("Ten"));
        var (poor, pOut, pc) = Caster("scouting_trip", ScoutRefuseMap, CX, CY + 2, mana: 1_999);
        var casters = Enumerable.Range(0, 6).Select(i => Caster("scouting_trip", ScoutRefuseMap, CX, CY + (i == 4 ? 20 : i == 5 ? 0 : 4 + 2 * i))).ToList();
        try
        {
            string[] typed = { "Bob1", "Nobodyhereatall", casters[2].session.CharName, far.CharName, eleven.CharName, ten.CharName };
            var boxes = new List<List<Box>>();
            OnOwnThread(() =>
            {
                poor.Receive(SpellCastSupport.CastFrame(0));
                for (int i = 0; i < casters.Count; i++)
                {
                    var (s, o, _) = casters[i];
                    s.Receive(SpellCastSupport.CastFrame(0));
                    s.Receive(MenuFrame(1));
                    o.Clear();
                    s.Receive(InputFrame(typed[i]));
                    boxes.Add(Boxes(o));
                }
            });
            string[] want =
            {
                "You can only enter letters and spaces.", "That player is currently not online.",
                "You cannot provide your own recognition.", "That person is not near you.", "That person is not near you.",
                $"{ten.CharName} has been recognized for scouting.",
            };
            var problems = new List<string>();
            for (int i = 0; i < want.Length; i++)
                if (boxes[i] is not [{ Kind: 0x0001 } b] || b.Text != want[i] || b.Next != (i == 5 ? 1 : 0) || casters[i].c.Mp != Mana - 2_000)
                    problems.Add($"{typed[i]}: [{string.Join(" / ", boxes[i])}] spent {Mana - casters[i].c.Mp}");
            int Count(Character ch) => QuestState.Over(ch, QuestState.Registry).Get("scouted_with_rangers");
            var (again, aOut, _) = casters[0];
            aOut.Clear();
            FreeSlot(again);
            again.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(problems.Count == 0 && Count(farC) == 0 && Count(elevenC) == 0 && Count(tenC) == 1
                        && Lines(pOut) == "Your will is too weak." && Menus(pOut).Count == 0 && pc.Mp == 1_999
                        && SpellCastSupport.AetherLeft(poor, "scouting_trip") == 0 && Lines(aOut) == "Scouting trip isn't ready yet.",
                        $"{string.Join("\n", problems)}; counts far {Count(farC)}, eleven {Count(elevenC)}, ten {Count(tenC)}; " +
                        $"poor [{Lines(pOut)}] mana {pc.Mp}; again [{Lines(aOut)}]");
        }
        finally
        {
            Leave(ScoutFarMap, far);
            Leave(ScoutRefuseMap, eleven, ten, poor);
            Leave(ScoutRefuseMap, casters.Select(c => c.session).ToArray());
        }
    }

    /// <summary>The mark is written inside the player's own monitor: while another thread holds it the caster's name step
    /// waits and the player's count and legend are untouched, in both <c>StateRank</c> orders. Red with
    /// <c>Session.ReceiveMarkCount</c>'s monitor entry taken out (Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScoutingTripWritesThePlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        int y = casterRanksFirst ? CY : CY + 6;
        Session caster, target;
        Character tc;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("scouting_trip", ScoutHeldMap, CX, y);
            (target, _, tc) = Player(ScoutHeldMap, CX + 1, y, name: Letters("Held"));
        }
        else
        {
            (target, _, tc) = Player(ScoutHeldMap, CX + 1, y, name: Letters("Held"));
            (caster, _, _) = Caster("scouting_trip", ScoutHeldMap, CX, y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            OnOwnThread(() => { caster.Receive(SpellCastSupport.CastFrame(0)); caster.Receive(MenuFrame(1)); });
            WhileAnotherThreadHolds(run, target, () => caster.Receive(InputFrame(target.CharName)),
                                    untouched: () => QuestState.Over(tc, QuestState.Registry).Get("scouted_with_rangers") == 0
                                                     && tc.Legends.All(l => l.Name != "scouted_with_rangers"));
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the name's step finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the player was marked while another thread held their monitor");
            Assert.True(QuestState.Over(tc, QuestState.Registry).Get("scouted_with_rangers") == 1, "the mark never landed");
        }
        finally { if (run.Settled) Leave(ScoutHeldMap, caster, target); }
    }

    /// <summary>The player's box, and the drop of the conversation they had waiting, are sent inside their own monitor:
    /// while another thread holds it the caster's next arrow waits, and the player has no box and still has their
    /// conversation, in both <c>StateRank</c> orders. Red with <c>Session.SendSpellBoxToTarget</c>'s monitor entry taken
    /// out (Release).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScoutingTripsBoxReachesThePlayerInsideTheirOwnMonitor(bool casterRanksFirst)
    {
        int y = casterRanksFirst ? CY : CY + 6;
        Session caster, target;
        RecordingOutbound tOut;
        if (casterRanksFirst)
        {
            (caster, _, _) = Caster("scouting_trip", ScoutBoxHeldMap, CX, y);
            (target, tOut, _) = Player(ScoutBoxHeldMap, CX + 1, y, name: Letters("Boxed"));
        }
        else
        {
            (target, tOut, _) = Player(ScoutBoxHeldMap, CX + 1, y, name: Letters("Boxed"));
            (caster, _, _) = Caster("scouting_trip", ScoutBoxHeldMap, CX, y);
        }
        Assert.Equal(casterRanksFirst, caster.StateRank < target.StateRank);
        var run = new HeldRun();
        try
        {
            OnOwnThread(() =>
            {
                _ = target.DlgInputPush(0, 0, "a conversation the player has waiting");
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(MenuFrame(1));
                caster.Receive(InputFrame(target.CharName));
            });
            tOut.Clear();
            WhileAnotherThreadHolds(run, target, () => caster.Receive(NextFrame()),
                                    untouched: () => Boxes(tOut).Count == 0 && target.DialogBusy);
            Assert.Null(run.HolderFault);
            Assert.Null(run.ActorFault);
            Assert.False(run.FinishedWhileHeld, "the next arrow's step finished while another thread held the player's monitor");
            Assert.True(run.UntouchedWhileHeld, "the player's box went out while another thread held their monitor");
            Assert.True(Boxes(tOut).Count == 1 && !target.DialogBusy, "the player's box never went out");
        }
        finally { if (run.Settled) Leave(ScoutBoxHeldMap, caster, target); }
    }

    // ===== the teacher ==========================================================================================

    /// <summary>The Ranger teacher sells six, each at its script's level: Skill up, Forest cover and Carve arrow (no
    /// requirements()) from their rows' 0, Tree bark, Pit trap and Scouting trip from 99. Call companion, whose price
    /// names boiled_meat (in no item table), has no price. Red without the price rows: nothing.</summary>
    [Fact]
    public void TheRangerTeacherSellsSix()
    {
        int path = Content.PathIdForClass("Ranger");
        Assert.Equal(15, path);
        var sold = Content.SubpathTeacherSpells(path, 99, Content.MaxMark);
        Assert.Equal("carve_arrow:0 forest_cover:0 scouting_trip:99 set_pit_trap:99 skill_up_ranger:0 tree_bark:99",
                     string.Join(" ", sold.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}:{s.Level}")));
        Assert.All(sold, s => Assert.Equal(path, s.PathId));
        Assert.Null(Content.LearnCostFor(Content.SpellByKey("call_companion")!, path));
        Assert.Equal("carve_arrow forest_cover skill_up_ranger",
                     string.Join(" ", Content.SubpathTeacherSpells(path, 98, Content.MaxMark).Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
    }

    // ===== helpers ==============================================================================================

    private const byte East = 1;

    private static string Lines(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));

    /// <summary>A menu pick (0x3A kind 2, the option's 1-based index at [10]).</summary>
    private static byte[] MenuFrame(byte index)
    {
        var body = new byte[11];
        body[0] = 0x02;
        body[10] = index;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>An input box's answer (0x3A kind 4, a submit: step 2 at [8], the length at [10], the text from [11]).</summary>
    private static byte[] InputFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>The client's "next" on a text box (0x3A kind 1, step 2).</summary>
    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        body[8] = 0x02;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Every menu (0x30 kind 2) the player was shown, as "prompt|option|option...".</summary>
    private static List<string> Menus(RecordingOutbound o)
    {
        var menus = new List<string>();
        foreach (var b in o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[0] == 2 && b[1] == 2))
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

    private sealed record Box(int Kind, uint Id, int Head, int Gfx, int Color, int Prev, int Next, string Text)
    {
        public override string ToString() => $"{Kind:x4} id {Id} head {Head} gfx {Gfx:x4}/{Color} prev {Prev} next {Next} \"{Text}\"";
    }

    /// <summary>Every 0x30 box or input box <paramref name="o"/> was sent, in order (a menu is not one), as
    /// <c>DivinerSpellTests</c> reads them.</summary>
    private static List<Box> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[7] == 1 && !(b[0] == 2 && b[1] == 2)).Select(b =>
        {
            int len = (b[21] << 8) | b[22];
            return new Box((b[0] << 8) | b[1], (uint)((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]), b[6],
                           (b[8] << 8) | b[9], b[10], b[19], b[20], Encoding.ASCII.GetString(b, 23, Math.Min(len, b.Length - 23)));
        }).ToList();

    private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    /// <summary>The spell graphics (0x29) sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => U32(b, 0) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    private static List<int> Sounds(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.Audio).Where(b => b.Length >= 4).Select(b => (b[2] << 8) | b[3]).ToList();

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    /// <summary>How long a status flag (a setDuration) has left on <paramref name="s"/>, 0 for none.</summary>
    private static long StatusLeft(Session s, string key)
    {
        long left = 0;
        s.WithState(() =>
        {
            if (((Dictionary<string, long>)StatusFlagsField.GetValue(s)!).TryGetValue(key, out long until))
                left = until - Environment.TickCount64;
        });
        return left;
    }

    /// <summary>The amount in bag slot <paramref name="slot"/>, 0 for an empty slot.</summary>
    private static int Amount(Character c, byte slot) => c.Inventory.FirstOrDefault(i => i.Slot == slot)?.Amount ?? 0;

    /// <summary>One 500 blow of <paramref name="kind"/> on <paramref name="victim"/> through its production entry point (as
    /// <c>DamageIntakeTests</c> drives each kind).</summary>
    private static void Hit(Session victim, DamageKind kind, Session peer, Mob mob)
    {
        switch (kind)
        {
            case DamageKind.MobMelee: victim.ApplyMobHit(mob, 500); break;
            case DamageKind.PlayerMelee: victim.ReceiveMeleeDamage(500, peer, crit: false); break;
            case DamageKind.MobSpell: victim.ReceiveMobSpell(500, mob, "Ice ray"); break;
            case DamageKind.PlayerSpell: victim.ReceiveSpellDamage(500, peer, "Spark"); break;
            default: throw new NotSupportedException(kind.ToString());
        }
    }

    /// <summary>Cast Tree bark until a cast lands (Invincibility's roll lands 71 in 100 at the best armour), clearing the
    /// wait between; true once one has.</summary>
    private static bool LandTreeBark(Session caster, RecordingOutbound o)
    {
        for (int i = 0; i < 40; i++)
        {
            o.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            if (Lines(o) == "Harden body spell | You cast Tree bark.") return true;
            SpellCastSupport.ClearAether(caster, "tree_bark");
        }
        return false;
    }

    /// <summary>A map's size as Maps.csv has it, or a wide 100 x 100 for a content-free map.</summary>
    private static void Size(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }

    /// <summary>The <paramref name="skip"/>'th east-west run of <paramref name="length"/> open tiles on
    /// <paramref name="map"/> (no wall, no warp), the runs at least four rows apart, so each fact walks its own.</summary>
    private static (int X, int Y) OpenRun(ushort map, int length, int skip)
    {
        ushort xs = 100, ys = 100;
        if (Content.Maps.TryGetValue(map, out var info)) { xs = info.Xs; ys = info.Ys; }
        var terrain = MapData.For(map, xs, ys);
        int found = 0, lastY = -100;
        for (int y = 2; y < ys - 2; y++)
        {
            if (y - lastY < 4) continue;
            for (int x = 2; x + length < xs - 2; x++)
            {
                bool open = Enumerable.Range(0, length).All(i =>
                    (terrain is null || !terrain.Solid(x + i, y)) && !Content.TryWarp(map, (ushort)(x + i), (ushort)y, out _));
                if (!open) continue;
                if (found++ == skip) return (x, y);
                lastY = y;
                break;
            }
        }
        throw new InvalidOperationException($"map {map} has no open run #{skip}");
    }

    /// <summary>A step on the walk frame (0x06), from where the player stands.</summary>
    private static void Walk(Session s, byte dir)
    {
        ushort x = s.PlayerX, y = s.PlayerY;
        s.Receive(SessionFixture.Frame(ClientOp.Walk, new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y }));
    }

    /// <summary>A creature's committed step onto (<paramref name="nx"/>, <paramref name="ny"/>), as the mob tick commits it,
    /// on a beat's queues of its own, which <c>World.FlushTickForTest</c> can then drain (the trap damage, the graphics).</summary>
    private World.TickQueues StepQueued(ushort map, Mob mob, int nx, int ny, byte dir)
    {
        var q = new World.TickQueues();
        _fx.World.UnderWorldLockForTest(() =>
        {
            var ctx = _fx.World.MobTickContextForTest(map, q);
            Assert.True(World.MobMovement.StepToForTest(ctx, mob, nx, ny, dir));
        });
        return q;
    }

    /// <summary>Every trap left on <paramref name="map"/> lapses and the world tick's sweep takes it.</summary>
    private void ClearTraps(ushort map)
    {
        _fx.World.UnderWorldLockForTest(() =>
        {
            foreach (var t in _fx.World.TrapsNear(map, 0, 0, 10_000)) t.ExpiresAt = 1;
        });
        _fx.World.TickOnceForTest();
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own with no synchronisation context, as a read loop runs a
    /// packet: an await inside it (a menu's, an input box's or a box's) then resumes inline on the thread that answers it.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() => fault = Record.Exception(body)) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the packet thread never finished");
        Assert.Null(fault);
    }

    /// <summary>A letters-only name, unique in this process: Scouting trip's typed names take letters, spaces and an
    /// apostrophe only.</summary>
    private static string Letters(string stem)
    {
        int n = Interlocked.Increment(ref _serial);
        var sb = new StringBuilder(stem);
        do { sb.Append((char)('a' + n % 26)); n /= 26; } while (n > 0);
        return sb.ToString();
    }

    private Mob AddCreature(ushort map, int x, int y, int hp = 100_000, int maxHp = 100_000)
    {
        var mob = new Mob(_fx.World.AllocateMobId(), 1, (ushort)x, (ushort)y, $"Ranger target {Interlocked.Increment(ref _serial)}", maxHp);
        mob.Hp = hp;
        _fx.World.AddMob(map, mob);
        return mob;
    }

    /// <summary>A level-99 Ranger at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/> in
    /// slot 0 of the book, Will 41, 10,000 vita, on the map's own size (a wide one for a content-free map). Its gear and
    /// buff totals are published, as a login's first stats push publishes them (<c>Session.Totals</c>), so a peer's read of
    /// them (the Pit trap's owner) sees what it wears.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(string key, ushort map, int x, int y,
                                                                            uint mana = Mana, uint maxMana = MaxMana,
                                                                            Action<Character>? configure = null)
    {
        var made = _fx.PlayerWith(Letters("Ranger"), ch =>
        {
            ch.ClassName = "Ranger";
            ch.Level = 99;
            ch.Ac = 0;
            ch.Will = Will;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = maxMana; ch.Mp = mana;
            ch.Dir = 1;
            Size(ch, map);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);
        made.session.WithState(() => _ = made.session.ItemArmor);
        return made;
    }

    private (Session session, RecordingOutbound outbound, Character c) Player(ushort map, int x, int y, uint hp = Hp,
                                                                            uint maxHp = Hp, string? name = null,
                                                                            Action<Character>? configure = null) =>
        _fx.PlayerWith(name ?? Letters("Other"), ch =>
        {
            ch.Level = 50;
            ch.Ac = 0;
            ch.MaxHp = maxHp; ch.Hp = hp;
            ch.MaxMp = 1_000; ch.Mp = 0;
            ch.Dir = 1;
            Size(ch, map);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    /// <summary>An outbound that notes, for each frame sent, its opcode and whether the sending thread holds the world
    /// lock (the second re-check's probe's).</summary>
    private sealed class LockSpyOutbound : IOutbound
    {
        private readonly World _w;
        public readonly List<(byte op, bool under)> Sent = new();
        public LockSpyOutbound(World w) => _w = w;
        public string Remote => "lockspy";
        public int Capacity => int.MaxValue;
        public int QueueDepth => 0;
        public bool Send(byte[] frame)
        {
            lock (Sent) Sent.Add((frame.Length > 3 ? frame[3] : (byte)0, _w.HoldsWorldLock));
            return true;
        }
        public void Close() { }
    }

    // ---- the held-monitor run (MonkSpellTests' shape) -----------------------------------------------------------------

    private sealed class HeldRun
    {
        public bool Settled = true;
        public bool FinishedWhileHeld;
        public bool UntouchedWhileHeld;
        public Exception? HolderFault, ActorFault;
    }

    /// <summary>A third thread takes <paramref name="target"/>'s monitor; then <paramref name="act"/> runs on a thread of its
    /// own until it finishes or sits blocked for 100 ms; then, still inside the third thread's section,
    /// <paramref name="untouched"/> says whether the target's state is as the third thread left it. Then the third thread
    /// lets go and both are joined.</summary>
    private static void WhileAnotherThreadHolds(HeldRun run, Session target, Action act, Func<bool> untouched)
    {
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            try
            {
                target.WithState(() =>
                {
                    held.Set();
                    letGo.Wait();
                    run.UntouchedWhileHeld = untouched();
                });
            }
            catch (Exception e) { run.HolderFault = e; }
        }) { IsBackground = true };
        var actor = new Thread(() =>
        {
            try { act(); }
            catch (Exception e) { run.ActorFault = e; }
        }) { IsBackground = true };

        run.Settled = false;
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the target's monitor");
            actor.Start();
            WaitUntilDoneOrBlocked(actor);
            run.FinishedWhileHeld = !actor.IsAlive;
        }
        finally
        {
            letGo.Set();
        }
        Assert.True(holder.Join(TimeSpan.FromSeconds(60)), "the holder never let go");
        Assert.True(actor.Join(TimeSpan.FromSeconds(60)), "the action never finished after the monitor was free");
        run.Settled = true;
    }

    /// <summary>Wait until <paramref name="t"/> has finished, or has sat blocked for 100 ms straight, or 10 s have passed.</summary>
    private static void WaitUntilDoneOrBlocked(Thread t)
    {
        var clock = Stopwatch.StartNew();
        long blockedSince = -1;
        while (clock.ElapsedMilliseconds < 10_000 && !t.Join(5))
        {
            if ((t.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) blockedSince = -1;
            else if (blockedSince < 0) blockedSince = clock.ElapsedMilliseconds;
            else if (clock.ElapsedMilliseconds - blockedSince >= 100) return;
        }
    }
}
