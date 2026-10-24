namespace Server;

/// <summary>
/// How each NPC is COMPOSED from reusable <see cref="INpcAbility"/> features. An NPC entry lists only what
/// that NPC <i>is</i> (a smith is a shop + repair; an inn keeper is a shop + bank + transport + clock) —
/// the abilities themselves hold how each feature works, shared across every NPC that has it.
///
/// NPCs whose menu is fully implied by their data flags need no entry here at all: <see cref="For"/> falls
/// back to deriving abilities from the shop/repair/bank flags, so a plain stocked shopkeeper is zero-config.
/// Register an NPC only when its composition differs from that default (extra features, a unique order, or
/// bespoke <see cref="InlineAbility"/> options).
/// </summary>
public static class NpcScripts
{
    // Ability NAME -> the C# singleton that implements it. This is the code half of the composition: the CSV
    // (game-data/NpcAbilities.csv, loaded into Content.NpcCompositions) says WHICH abilities each NPC has;
    // this map turns each name into its shared instance. To expose a new ability to the CSV, register it here.
    // (ClassTrainerAbility has four per-class instances; everything else is a lone singleton.)
    private static readonly Dictionary<string, INpcAbility> AbilityByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shop"] = ShopAbility.Instance,
        ["repair"] = RepairAbility.Instance,
        ["bank"] = BankAbility.Instance,
        ["messenger"] = MessengerAbility.Instance,
        ["fish"] = FishAbility.Instance,
        ["librarian"] = LibrarianAbility.Instance,
        ["minor_quest"] = MinorQuestAbility.Instance,
        ["warrior_trainer"] = ClassTrainerAbility.Warrior,
        ["rogue_trainer"] = ClassTrainerAbility.Rogue,
        ["mage_trainer"] = ClassTrainerAbility.Mage,
        ["poet_trainer"] = ClassTrainerAbility.Poet,
        ["forget_secret"] = ForgetSecretAbility.Instance,
        ["appearance"] = AppearanceAbility.Instance,
        ["war_paint"] = WarPaintAbility.Instance,
        ["shadow_stats"] = ShadowStatsAbility.Instance,
        ["bon_hwa"] = BonHwaAbility.Instance,
        ["chapel"] = ChapelAbility.Instance,
        ["revive"] = ReviveAbility.Instance,
        ["ancient_leviathan"] = AncientLeviathanAbility.Instance,
        ["border_patrol"] = BorderPatrolAbility.Instance,
        ["hermit"] = HermitAbility.Instance,
        ["sute"] = SuteQuestAbility.Instance,
        ["nagnang_shield"] = NagnangShieldAbility.Instance,
        ["nagnang_tall_shield"] = NagnangTallShieldAbility.Instance,
        ["mage_stone"] = MageStoneAbility.Instance,
        ["forgotten_past"] = ForgottenPastAbility.Instance,
        ["dagger_uniform"] = DaggerUniformAbility.Instance,
        ["tiger_mail"] = TigerMailAbility.Instance,
        ["armor_quest"] = ArmorQuestAbility.Instance,
        ["white_moon_axe"] = WhiteMoonAxeAbility.Instance,
        ["poet_whip"] = PoetWhipQuestAbility.Instance,
        ["stars_hint"] = StarHintAbility.Instance,
        ["totem_worship"] = TotemWorshipAbility.Instance,
        ["mythic_alliance"] = MythicAllianceAbility.Instance,
        ["alignment"] = AlignmentAbility.Instance,
        ["summit"] = SummitAbility.Instance,
        ["npc_subpath"] = NpcSubpathAbility.Instance,
        // A single-giver quest is one QuestAbility per QuestDef; its giver lists it first in its row.
        ["tutorial_quest"] = new QuestAbility(TutorialQuest.Def),
    };

    /// <summary>The abilities that make up an NPC: its explicit composition (NpcAbilities.csv via
    /// Content.NpcCompositions) if listed, else derived from its data flags (so simple shops/banks work with no
    /// row). A row's ability that is narrowed to other NPC ids or maps of the same identifier is left off
    /// (<see cref="NpcAbilityRef.AppliesTo"/>), so the ability never sees an NPC it is not for. An ability name
    /// with no registration here is skipped without a word at run time; NpcAbilityNarrowingTests fails on one.</summary>
    public static INpcAbility[] For(NpcDef def)
    {
        var list = new List<INpcAbility>();
        if (Content.NpcCompositions.TryGetValue(def.Key, out var refs))
        {
            foreach (var r in refs)
                if (r.AppliesTo(def) && AbilityByName.TryGetValue(r.Name, out var a)) list.Add(a);
        }
        else
        {
            if (def.Shop)   list.Add(ShopAbility.Instance);   // contributes nothing if we haven't stocked it
            if (def.Repair) list.Add(RepairAbility.Instance);
            if (def.Bank)   list.Add(BankAbility.Instance);
        }

        // Every NPC also answers the universal "Misc" voice questions (name / what do you buy / what do you
        // sell). Last, so a matching shop/bank/quest handler wins; it adds no click-menu entry.
        list.Add(InfoAbility.Instance);
        return list.ToArray();
    }

    /// <summary>Whether an ability name has an implementation — what every name in the CSV must have.</summary>
    internal static bool IsRegistered(string name) => AbilityByName.ContainsKey(name);
}

/// <summary>
/// One ability in an NpcAbilities.csv row, and which of the NPCs sharing the row's identifier it is composed
/// onto. <see cref="NpcIds"/> and <see cref="MapIds"/> are both null for the whole identifier (a plain
/// <c>name</c>); a <c>name@39</c> token fills <see cref="NpcIds"/>, a <c>name@map:324;325</c> token fills
/// <see cref="MapIds"/>. The header of game-data/NpcAbilities.csv is the reference for the syntax.
///
/// <para>This is where an ability that belongs to SOME of the NPCs sharing an identifier says which. Twelve
/// NPCs are <c>MageTrainerNpc</c>, and only Eldritch tells the Sute story. Before the CSV could say so, each
/// such ability was composed onto all twelve and refused the other eleven itself, from a private id table, on
/// every word spoken near any of them.</para>
/// </summary>
public sealed record NpcAbilityRef(string Name, IReadOnlySet<int>? NpcIds = null, IReadOnlySet<int>? MapIds = null)
{
    /// <summary>Is this ability part of <paramref name="def"/>? Always, unless narrowed away from it.</summary>
    public bool AppliesTo(NpcDef def) =>
        (NpcIds is null || NpcIds.Contains(def.Id)) && (MapIds is null || MapIds.Contains(def.Map));
}
