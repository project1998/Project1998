using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// A cast aimed by id at a player on another map no longer reaches them through the buff or the damage resolver.
/// Caleb, 2026-10-11 ("Fix as a slice"), on #342's review (F2): <c>Session.ResolveTargetBuff</c> and
/// <c>Session.ResolveDamageTarget</c> took an id to the player with that id on ANY map. From a PvP map that let a
/// damage spell, a curse, a venom or a Doze land on a player standing in a town, because every one of those gates
/// reads the caster's map; and a heal, a buff or a ward reached anyone anywhere.
///
/// <para>Both resolvers now follow #342's rule, the one <c>ResolvePcCastTarget</c> already keeps: an id resolves
/// to a player only when that player is on the caster's map. A player elsewhere is answered by the faced tile, as
/// a cast with no id is; with nothing faced, the cast gets the answer an id that names nobody gets (the damage
/// resolver: no target; the buff resolver: the caster, for a hostile status on a PvP map, else no target). Each
/// caller's answer to that is its own and unchanged.</para>
///
/// <para>Only a client that sends a target id reaches this. The stock 4.95 client casts with the slot alone
/// (docs/4.x/Protocol.md, "Live-confirmed"), which takes the faced tile and never reaches the id branch.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh caster, so neither the action
/// budget nor a damage spell's cast delay decides a fact. The arenas are real PvP maps with no spawns and no NPCs;
/// the other maps are content-free, so PvP is off on them. No other class uses any of them.</para>
/// </summary>
[Collection("world")]
public sealed class ResolverTargetMapTests
{
    /// <summary>Arctic Arena and Bringer Arena (Maps.csv MapPvP 1, MapSpells 1).</summary>
    private const ushort Arena = 3009, FarArena = 3011;
    /// <summary>Content-free: no Maps.csv row, so <c>Content.IsPvpMap</c> is false, as it is in every town.</summary>
    private const ushort Town = 62461, Here = 62462, There = 62463, FacedHere = 62464, FacedThere = 62465;

    private const ushort CX = 10, CY = 10;      // where every caster stands
    private const ushort NearX = 14;             // a player on the caster's map, aimed at by id, not faced
    private const uint NobodyId = 0x7FFF_FFF0;   // an id nobody online has
    private const uint Hp = 5_000, MaxHp = 10_000, Mana = 50_000;

    private readonly SessionFixture _fx;
    private static int _serial;

    public ResolverTargetMapTests(SessionFixture fx) => _fx = fx;

    /// <summary>A damage spell for each damage-resolver caller, a venom, a curse and a Doze: cast from a PvP map.</summary>
    private static readonly string[] Hostile =
    {
        "ignite_mage",   // arch_damage -> ctx:magicDamage -> LuaMagicDamage
        "bolt_mage",     // magic_damage -> ctx:damage -> LuaDamageTarget
        "hailstorm",     // generic (no export row, a damage word) -> ctx.hasDamageTarget + ctx:damage
        "venom_mage",    // venom -> LuaApplyVenom
        "vex_mage",      // curse -> canCurse, hasStatus, alreadyCast, applyCurse
        "doze_mage",     // arch_debuff (sleep) -> targetKind, hasStatus, holdTarget, amplify
    };

    /// <summary>A heal, a target buff and a ward: friendly, so any two maps show it.</summary>
    private static readonly string[] Friendly =
    {
        "heal_mage",          // heal -> LuaHealTarget
        "valor_mage",         // arch_targetbuff -> LuaBuffTarget
        "harden_armor_mage",  // ward (ally) -> LuaWardTarget
    };

    public static TheoryData<string, ushort, ushort> NothingFacedCases
    {
        get
        {
            var data = new TheoryData<string, ushort, ushort>();
            foreach (var key in Hostile)
            {
                data.Add(key, Arena, Town);       // the PvP gate's case: from an arena into a town
                data.Add(key, Arena, FarArena);   // and from one arena into another
            }
            foreach (var key in Friendly) data.Add(key, Here, There);
            return data;
        }
    }

    /// <summary>Each family, cast through the real frame at the id of a player on another map, with nothing on the
    /// caster's faced tile. The far player is untouched: their HP, their status list, their venom and their sleep,
    /// and they are told nothing. The caster gets exactly what a cast at an id nobody has gets. Then the control: the
    /// same cast, by id, at a player on the caster's own map, lands.
    /// <para>Red on df98c3a for all 15: the cast lands on the other map. From an arena it damages, venoms, curses or
    /// dozes a player standing in a town or in another arena; from anywhere it heals or wards them. Valor reaches
    /// them too: in Release the far player carries it, and in Debug its write trips the <c>_buffs</c> guard
    /// ("Valor isn't working right now.", for 30).</para>
    /// <para>A Vex resolved to the caster meets the caster's own running Vex, so that answer does not ride the curse's
    /// 85% roll: "You already cast that spell.", for nothing. Valor's control aims at a creature: on df98c3a a target
    /// buff on another player has no monitor for its write (#341's slice), and the Debug build refuses it.</para></summary>
    [Theory]
    [MemberData(nameof(NothingFacedCases))]
    public void ACastAimedByIdAtAPlayerOnAnotherMapIsAnsweredAsAnIdNobodyHas(string key, ushort casterMap, ushort farMap)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.Equal(casterMap == Arena, Content.IsPvpMap(casterMap));
        Assert.Equal(farMap == FarArena, Content.IsPvpMap(farMap));

        string offMap = CastWithNothingFaced(sp, casterMap, farMap, aimAtFar: true);
        string nobody = CastWithNothingFaced(sp, casterMap, farMap, aimAtFar: false);
        Assert.Equal(nobody, offMap);

        Assert.True(LandsByIdOnTheCastersMap(sp, casterMap),
                    $"the control: {sp.Name} by id at a target on the caster's own map did not land");
    }

    public static TheoryData<string, ushort, ushort> FacedCases => new()
    {
        { "ignite_mage", Arena, Town },
        { "bolt_mage", Arena, Town },
        { "venom_mage", Arena, Town },
        { "heal_mage", FacedHere, FacedThere },
        { "harden_armor_mage", FacedHere, FacedThere },
    };

    /// <summary>The fallback is the faced tile (Caleb kept #342's: "Merge as is (fallback)"). A caster faces a player
    /// on their own map and casts twice: with the slot alone (the stock 4.95 client's frame) and with the id of a player
    /// on another map. Both land on the faced player, with the same lines and cost, and the far player is untouched.
    /// <para>Red on df98c3a: the id cast lands on the far player and the faced one is untouched.</para></summary>
    [Theory]
    [MemberData(nameof(FacedCases))]
    public void AnIdOnAnotherMapIsAnsweredByTheFacedTileAsTheSlotAloneIs(string key, ushort casterMap, ushort farMap)
    {
        var sp = Content.SpellByKey(key)!;
        string slot = CastAtAFacedPlayer(sp, casterMap, farMap, aimAtFar: false);
        string offMap = CastAtAFacedPlayer(sp, casterMap, farMap, aimAtFar: true);
        Assert.Contains("faced landed=True", slot);
        Assert.Equal(slot, offMap);
    }

    /// <summary>The same fallback with a creature faced. The callers that refuse a player outright (Endear, Amnesia,
    /// Confuse); Drain, which reads its creature by the cast-target resolver and then hits through the damage
    /// resolver; and Valor, a target buff, which lands on a creature as well as on a player. Aimed at a player on
    /// another map, each now takes the faced creature, as the slot alone does: Endear charms it, Amnesia and Confuse
    /// spend their mana on it (won or lost, the line names it), Drain kills it, and Valor buffs it.
    /// <para>Red on df98c3a: Endear, Amnesia and Confuse resolve the far player, refuse it silently and spend
    /// nothing; Drain's blow goes to the far player, off a PvP map ("You can't attack that target."), and the
    /// creature lives; Valor goes to the far player and the creature has nothing.</para></summary>
    [Theory]
    [InlineData("endear_poet")]
    [InlineData("amnesia_rogue")]
    [InlineData("confuse_mage")]
    [InlineData("drain_rogue")]
    [InlineData("valor_mage")]
    public void AnIdOnAnotherMapWithACreatureFacedTakesTheCreatureAsTheSlotAloneDoes(string key)
    {
        var sp = Content.SpellByKey(key)!;
        var (far, farOut, farc) = Player("RtmFarM", FacedThere, CX, CY);
        try
        {
            int row = 0;
            foreach (bool aimAtFar in new[] { false, true })
            {
                ushort y = (ushort)(CY + 4 * row++);
                var mob = new Mob(_fx.World.AllocateMobId(), 1, CX + 1, y, $"Rtm creature {row}", 500);
                _fx.World.AddMob(FacedHere, mob);
                var (caster, cOut, cc) = Caster(sp, FacedHere, CX, y, dir: 1);   // facing east, at the creature
                try
                {
                    uint mp0 = cc.Mp, hp0 = cc.Hp;
                    caster.Receive(SpellCastSupport.CastFrame(0, aimAtFar ? far.PlayerId : null));
                    var lines = SpellCastSupport.MiniTexts(cOut);
                    string what = aimAtFar ? "the id of a player on another map" : "the slot alone";
                    switch (key)
                    {
                        case "endear_poet":
                            Assert.True(mob.OwnerId == caster.PlayerId, $"{what}: the faced creature was not charmed");
                            break;
                        case "amnesia_rogue":
                        case "confuse_mage":
                            Assert.True(mp0 - cc.Mp == 30 && lines.Any(l => l.Contains(mob.Name, StringComparison.Ordinal)),
                                        $"{what}: the faced creature was not the target ({string.Join(" | ", lines)})");
                            break;
                        case "drain_rogue":
                            Assert.True(!mob.Alive && cc.Hp > hp0 && !lines.Contains("You can't attack that target."),
                                        $"{what}: the faced creature was not drained ({string.Join(" | ", lines)})");
                            break;
                        case "valor_mage":
                            Assert.True(CarriesBuff(mob, key) && mp0 - cc.Mp == 30,
                                        $"{what}: the faced creature was not buffed ({string.Join(" | ", lines)})");
                            break;
                        default:
                            Assert.Fail($"no check for '{key}'");
                            break;
                    }
                    Assert.Empty(SpellCastSupport.MiniTexts(farOut));
                    Assert.Equal(Hp, farc.Hp);
                }
                finally
                {
                    _fx.World.DespawnMob(FacedHere, mob);
                    _fx.World.LeaveMap(caster, FacedHere);
                }
            }
        }
        finally
        {
            _fx.World.LeaveMap(far, FacedThere);
        }
    }

    /// <summary>The dog 5-way centres on its target. Aimed at a player on another map with nothing faced, it now
    /// centres on the caster, as an id nobody has does, instead of on the far player's coordinates on the caster's
    /// own map. A creature stands on the caster's map exactly there, three tiles away: it is untouched. Then the
    /// control: aimed at that creature by id, the blast lands on it.
    /// <para>Red on df98c3a: the blast centres on the far player's tile, here, and the creature takes it.</para></summary>
    [Fact]
    public void TheFiveWayAimedAtAPlayerOnAnotherMapCentresOnTheCaster()
    {
        var sp = Content.SpellByKey("fissure")!;
        const ushort mx = CX + 3;
        var mob = new Mob(_fx.World.AllocateMobId(), 1, mx, CY, "Rtm bystander", 1_000_000);
        _fx.World.AddMob(Here, mob);
        try
        {
            string offMap = CastWithNothingFaced(sp, Here, There, aimAtFar: true, farX: mx);
            Assert.Equal(mob.MaxHp, mob.Hp);
            string nobody = CastWithNothingFaced(sp, Here, There, aimAtFar: false, farX: mx);
            Assert.Equal(nobody, offMap);
            Assert.Equal(mob.MaxHp, mob.Hp);

            var (caster, cOut, _) = Caster(sp, Here, CX, CY);
            try
            {
                caster.Receive(SpellCastSupport.CastFrame(0, mob.Id));
                Assert.True(mob.Hp < mob.MaxHp, "the control: Fissure aimed at the creature by id did not reach it");
                Assert.Contains($"You cast {sp.Name}.", SpellCastSupport.MiniTexts(cOut));
            }
            finally { _fx.World.LeaveMap(caster, Here); }
        }
        finally
        {
            _fx.World.DespawnMob(Here, mob);
        }
    }

    // ===== the casts =====================================================================================

    /// <summary>A fresh caster on <paramref name="casterMap"/>, facing an empty tile, casts <paramref name="sp"/> at
    /// either a fresh player on <paramref name="farMap"/> (by their id) or an id nobody has. Asserts the far player
    /// is untouched, and returns what the caster saw, spent and now carries, with no names in it.</summary>
    private string CastWithNothingFaced(SpellDef sp, ushort casterMap, ushort farMap, bool aimAtFar, ushort farX = CX)
    {
        var (far, farOut, farc) = Player("RtmFar", farMap, farX, CY);
        var (caster, cOut, cc) = Caster(sp, casterMap, CX, CY);   // facing north: (CX, CY - 1) is empty
        try
        {
            if (sp.Key == "vex_mage") caster.ReceiveCurse("armor", 30, 600_000, sp.Key, sp.Name, "minorcurses");
            uint mp0 = cc.Mp, hp0 = cc.Hp;
            cOut.Clear();
            farOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, aimAtFar ? far.PlayerId : NobodyId));

            string what = aimAtFar ? $"{sp.Name} at the id of a player on map {farMap}" : $"{sp.Name} at an id nobody has";
            Assert.True(farc.Hp == Hp, $"{what}: the far player's HP went {Hp} -> {farc.Hp}");
            Assert.True(SpellCastSupport.BuffEntry(far, sp.Key) is null, $"{what}: the far player carries {sp.Name}");
            Assert.False(far.Poisoned, $"{what}: the far player is poisoned");
            Assert.False(far.Asleep, $"{what}: the far player is asleep");
            Assert.True(SpellCastSupport.MiniTexts(farOut).Count == 0,
                        $"{what}: the far player read [{string.Join(" | ", SpellCastSupport.MiniTexts(farOut))}]");
            return $"caster lines=[{string.Join(" | ", SpellCastSupport.MiniTexts(cOut))}] spent={mp0 - cc.Mp} " +
                   $"hp={(int)cc.Hp - (int)hp0:+0;-0;0} pose={cOut.BodiesOf(ServerOp.Action).Count} " +
                   $"carries={SpellCastSupport.BuffEntry(caster, sp.Key) is not null} poisoned={caster.Poisoned} asleep={caster.Asleep}";
        }
        finally
        {
            _fx.World.LeaveMap(caster, casterMap);
            _fx.World.LeaveMap(far, farMap);
        }
    }

    /// <summary>The control. A fresh caster casts <paramref name="sp"/> by id at a fresh player on its own map, out
    /// of reach of the faced tile; true once it lands. A curse and a Doze roll (85% and 80%), so they get up to 20
    /// casters; everything else gets one. Valor aims at a creature instead (see the theory's note).</summary>
    private bool LandsByIdOnTheCastersMap(SpellDef sp, ushort map)
    {
        int tries = sp.Key is "vex_mage" or "doze_mage" ? 20 : 1;
        for (int i = 0; i < tries; i++)
        {
            var (near, _, nearc) = Player("RtmNear", map, NearX, CY);
            var mob = new Mob(_fx.World.AllocateMobId(), 1, NearX, CY + 2, "Rtm pet", 1_000);
            _fx.World.AddMob(map, mob);
            var (caster, _, _) = Caster(sp, map, CX, CY);
            try
            {
                if (sp.Key == "valor_mage")
                {
                    caster.Receive(SpellCastSupport.CastFrame(0, mob.Id));
                    if (CarriesBuff(mob, sp.Key)) return true;
                    continue;
                }
                caster.Receive(SpellCastSupport.CastFrame(0, near.PlayerId));
                if (Landed(sp, near, nearc)) return true;
            }
            finally
            {
                _fx.World.DespawnMob(map, mob);
                _fx.World.LeaveMap(caster, map);
                _fx.World.LeaveMap(near, map);
            }
        }
        return false;
    }

    private static bool CarriesBuff(Mob mob, string key) => mob.Buffs?.Any(b => b.Key == key) == true;

    /// <summary>A fresh caster faces a fresh player on its own map and casts <paramref name="sp"/>, with the slot
    /// alone or at the id of a fresh player on <paramref name="farMap"/>. Asserts the far player is untouched and
    /// returns what the caster and the faced player saw, with the caster's name taken out.</summary>
    private string CastAtAFacedPlayer(SpellDef sp, ushort casterMap, ushort farMap, bool aimAtFar)
    {
        var (far, farOut, farc) = Player("RtmFarF", farMap, CX, CY);
        var (faced, facedOut, facedc) = Player("RtmFaced", casterMap, CX + 1, CY);
        var (caster, cOut, cc) = Caster(sp, casterMap, CX, CY, dir: 1);   // facing east, at the faced player
        try
        {
            uint mp0 = cc.Mp;
            caster.Receive(SpellCastSupport.CastFrame(0, aimAtFar ? far.PlayerId : null));

            Assert.True(farc.Hp == Hp && SpellCastSupport.BuffEntry(far, sp.Key) is null && !far.Poisoned,
                        $"{sp.Name}: the player on map {farMap} was reached");
            Assert.Empty(SpellCastSupport.MiniTexts(farOut));
            string N(string s) => s.Replace(caster.CharName, "<caster>", StringComparison.Ordinal);
            return $"caster lines=[{string.Join(" | ", SpellCastSupport.MiniTexts(cOut).Select(N))}] spent={mp0 - cc.Mp} " +
                   $"pose={cOut.BodiesOf(ServerOp.Action).Count}; faced landed={Landed(sp, faced, facedc)} " +
                   $"lines=[{string.Join(" | ", SpellCastSupport.MiniTexts(facedOut).Select(N))}]";
        }
        finally
        {
            _fx.World.LeaveMap(caster, casterMap);
            _fx.World.LeaveMap(faced, casterMap);
            _fx.World.LeaveMap(far, farMap);
        }
    }

    /// <summary>Did <paramref name="sp"/> land on <paramref name="target"/>, who started at <see cref="Hp"/> with
    /// nothing running?</summary>
    private static bool Landed(SpellDef sp, Session target, Character c) => sp.Key switch
    {
        "ignite_mage" or "bolt_mage" or "hailstorm" => c.Hp < Hp,
        "heal_mage" => c.Hp > Hp,
        "venom_mage" => target.Poisoned,
        "doze_mage" => target.Asleep,
        _ => SpellCastSupport.BuffEntry(target, sp.Key) is not null,   // the curse, the buff, the ward
    };

    // ===== helpers =======================================================================================

    /// <summary>A level-99 caster with <paramref name="sp"/> in slot 0, Will 255 (so a player never deflects it), half
    /// its HP (so a heal on itself shows) and plenty of mana, facing <paramref name="dir"/> (0 north, 1 east).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Caster(SpellDef sp, ushort map, ushort x, ushort y,
                                                                            byte dir = 0) =>
        _fx.PlayerWith($"RtmCaster{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 99;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = Mana; ch.Mp = Mana;
            ch.Will = 255;
            ch.Dir = dir;
            Wide(ch, map);
            ch.Spells.Add(sp.Id);
        }, map, x, y);

    /// <summary>A level-50 player at half HP with nothing running.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Player(string name, ushort map, ushort x, ushort y) =>
        _fx.PlayerWith($"{name}{Interlocked.Increment(ref _serial)}", ch =>
        {
            ch.Level = 50;
            ch.MaxHp = MaxHp; ch.Hp = Hp;
            ch.MaxMp = 1_000; ch.Mp = 1_000;
            Wide(ch, map);
        }, map, x, y);

    /// <summary>The arenas carry their own size; content-free maps have none, and the cast handler needs one.</summary>
    private static void Wide(Character c, ushort map)
    {
        if (Content.Maps.TryGetValue(map, out var m)) { c.MapXs = m.Xs; c.MapYs = m.Ys; }
        else { c.MapXs = 100; c.MapYs = 100; }
    }
}
