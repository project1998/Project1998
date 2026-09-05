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
        ["transport"] = TransportAbility.Instance,
        ["time"] = TimeAbility.Instance,
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
        ["tiger_mail"] = TigerMailAbility.Instance,
        ["armor_quest"] = ArmorQuestAbility.Instance,
        ["poet_whip"] = PoetWhipQuestAbility.Instance,
        ["stars_hint"] = StarHintAbility.Instance,
        ["totem_worship"] = TotemWorshipAbility.Instance,
        ["mythic_alliance"] = MythicAllianceAbility.Instance,
        ["alignment"] = AlignmentAbility.Instance,
        ["summit"] = SummitAbility.Instance,
    };

    /// <summary>The abilities that make up an NPC: its explicit composition (NpcAbilities.csv via
    /// Content.NpcCompositions) if listed, else derived from its data flags (so simple shops/banks work with no
    /// row). Unknown ability names in the CSV are skipped (logged once at load).</summary>
    public static INpcAbility[] For(NpcDef def)
    {
        var list = new List<INpcAbility>();
        // Any NPC that gives quests gets the quest menu first — including data-driven NPCs (like the two
        // MainTutorialNpc givers, which share an identifier but differ by id) that have no composition row.
        if (Quests.ForNpc(def.Id).Count > 0) list.Add(QuestAbility.Instance);

        if (Content.NpcCompositions.TryGetValue(def.Key, out var names))
        {
            foreach (var n in names)
                if (AbilityByName.TryGetValue(n, out var a)) list.Add(a);
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
}
