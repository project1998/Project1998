using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Mana Steal refuses a cast aimed at yourself, as Mana Gift does. Caleb, 2026-10-11 ("Refuse self"), on #341's
/// review (probe P6, "Pre-existing"): the <c>mana_steal</c> verb asks only whether the target is in your group, and
/// you are in your own group whenever you have one. So Draw Energy aimed at yourself in a party set your mana to
/// your mana plus "the target's", then set "the target's" to zero, on the same player: your pool went to 0 and you
/// read "You cast Draw Energy.".
///
/// <para>Mana Gift has always refused yourself with "It doesn't work." and no cost (game-data/spell_verbs.lua,
/// <c>mana_gift</c>; RTK's inspire.lua and its three twins, the same line). Mana Steal now gives the same answer at
/// the same point, straight after the target resolves.</para>
///
/// <para>Only a client that sends its own id can aim at itself. The stock 4.95 client casts with the slot alone, and
/// you are never on your own faced tile.</para>
/// </summary>
[Collection("world")]
public sealed class ManaStealSelfTests
{
    /// <summary>Content-free; no other class uses it.</summary>
    private const ushort SelfMap = 62466;

    private const uint MaxMana = 10_000, Mana = 4_000, MemberMana = 500;

    private readonly SessionFixture _fx;
    private static int _serial;

    public ManaStealSelfTests(SessionFixture fx) => _fx = fx;

    /// <summary>Each of the four, cast through the real frame at your own id while you are in a party: "It doesn't
    /// work." and nothing else, no cast pose, and your mana kept. Then the control: the same caster's cast at the
    /// group member, by id, still drains them into the caster's pool.
    /// <para>Red on df98c3a for all four: "You cast X.", the pose, and the caster's 4000 mana goes to 0.</para></summary>
    [Theory]
    [InlineData("inspiration_poet")]
    [InlineData("draw_energy_poet")]
    [InlineData("harness_power_poet")]
    [InlineData("combine_focus_poet")]
    public void ManaStealAimedAtYourselfInAPartyIsRefusedAsManaGiftIs(string key)
    {
        var sp = Content.SpellByKey(key)!;
        Assert.True(Content.IsManaStealSpell(sp), $"{key} is not a Mana Steal spell");
        int n = Interlocked.Increment(ref _serial);
        var (member, _, memberc) = _fx.PlayerWith($"StealMember{n}", ch =>
        {
            ch.Level = 50;
            ch.MaxMp = MaxMana; ch.Mp = MemberMana;
            ch.Grouped = true;
            Wide(ch);
        }, SelfMap, 30, 20);
        var (caster, cOut, cc) = _fx.PlayerWith($"StealCaster{n}", ch =>
        {
            ch.Level = 99;
            ch.MaxMp = MaxMana; ch.Mp = Mana;
            ch.Grouped = true;
            Wide(ch);
            ch.Spells.Add(sp.Id);
        }, SelfMap, 20, 20);
        try
        {
            SessionFixture.FormParty(caster, member);
            // The cause: you count as a member of your own group.
            Assert.True(caster.WithState(() => caster.LuaResolvePcTarget(sp, caster.PlayerId) && caster.LuaTargetInGroup),
                        "the caster does not count as in their own group");
            cOut.Clear();

            caster.Receive(SpellCastSupport.CastFrame(0, caster.PlayerId));

            Assert.Equal(new[] { "It doesn't work." }, SpellCastSupport.MiniTexts(cOut));
            Assert.Empty(cOut.BodiesOf(ServerOp.Action));
            Assert.Equal(Mana, cc.Mp);

            // The control: the group member, by id.
            cOut.Clear();
            caster.Receive(SpellCastSupport.CastFrame(0, member.PlayerId));

            Assert.Equal(new[] { $"You cast {sp.Name}." }, SpellCastSupport.MiniTexts(cOut));
            Assert.Equal(0u, memberc.Mp);
            Assert.Equal(Mana + MemberMana, cc.Mp);
        }
        finally
        {
            _fx.World.LeaveMap(caster, SelfMap);
            _fx.World.LeaveMap(member, SelfMap);
        }
    }

    /// <summary>Content-free maps have no size of their own; the cast handler needs one.</summary>
    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }
}
