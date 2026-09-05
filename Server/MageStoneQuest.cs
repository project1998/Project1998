using Shared;

namespace Server;

/// <summary>
/// The <b>Mage's Spirit Stone</b> — Nagnang's level-55 mage quest, and the only source of the Spirit stone
/// (Items.csv 25047). Ported from RTK <c>NPCs/nagnang/mage_stone_prophets.lua</c>, the "Ward" branch of
/// <c>NPCs/Common/mage_trainer.lua</c>, and the "Oh-mudum crypt" block of
/// <c>onScriptedTiles/onScriptedTilesQuest.lua</c>.
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item>Click <b>Wand</b> in the Nagnang Mage Guild (NPCs.csv 133, map 3828) and pick
/// "<b>Mage Stone</b>". He sends you to three prophets.</item>
/// <item>Nagnang (132, 23) leads up into <b>Prophets</b> (map 2570), a room with three doorways. Above each
/// doorway, walled off behind the north face of the room, sits an immortal mouse: the white
/// <b>Yin mouse</b> above the west door, the grey <b>Yang</b> and <b>Void</b> mice above the middle and east
/// ones (all three already in Spawns.csv). <b>Vex it, then zap it</b> — the hit only counts while the curse
/// is on (game-data/mob_ai.lua <c>squeak</c>), and it sets <c>zapped_&lt;mouse&gt;</c>.</item>
/// <item>Through the doorway, the prophet behind it now talks (npc_dialog.lua
/// <c>MageStoneProphetsNpc</c>): <b>Yin</b> asks for a Rose, <b>Yang</b> for Ore [high], <b>Void</b> sends
/// you to the Cemetery south of Kugnae.</item>
/// <item>Walk the crypts until a spirit speaks (<see cref="CryptMap"/>; <c>Session.TryMageStoneSpirit</c>).</item>
/// <item>Click Wand, pick "Mage Stone" again. He takes the Rose and gives the stone and the mark.</item>
/// </list>
///
/// <para><b>Sources.</b> Primary is nexusatlas.com/quests/magesspiritstone.php, which is where the level gate
/// (55), the path gate, the two components, the sacrifice list and the reward pair come from, and which names
/// the mark on its own header: "<b>Family to the Nangen Mages</b>". Structure, geometry and every line of
/// dialog are RTK's, because nothing of this quest's script survives in the archive.</para>
///
/// <para><b>Three deliberate divergences from RTK:</b></para>
/// <list type="bullet">
/// <item><b>Level 55, not 10.</b> RTK opens its version of this at level 10. Atlas states 55, and Atlas is
/// the only source that speaks to this quest as the 4.95 game ran it.</item>
/// <item><b>The Ore is shown, not spent.</b> RTK removes BOTH components on turn-in. Atlas's structured
/// "Items Lost to Sacrifice" field lists the Rose alone — and that field does itemize everything a quest eats
/// elsewhere on the site, so its silence about the Ore is evidence rather than an omission. Yang's own words
/// are "keep it with you until you complete all the other's quests and return to Wand", which is a
/// carry-it-here errand, not an offering. So the Ore must be in the pack and stays there.</item>
/// <item><b>The reward is the stone, not a ward.</b> RTK's fork pays out <c>magicians_ward</c> and renames the
/// whole quest around it; the item this quest gives is the Spirit stone, so the completion lines are RTK's
/// ward text with the ward swapped back out. RTK's own transcription typos are corrected throughout
/// ("prohets", "poweer", "throuogh", "thee others") — they are the doubled/dropped-letter kind that litters
/// its scripts, not anything a 2001 client printed.</item>
/// </list>
///
/// <para>State is four registry ints (three <see cref="ZapReg"/> flags written by the mob AI, plus
/// <see cref="GhostReg"/>) and one stage, all cleared on turn-in, plus the legend. Nothing new is persisted.
/// RTK's own crypt trigger is broken here and the fix is kept: it TESTS <c>mage_stone_met_ghost</c> and SETS
/// <c>mage_ward_met_ghost</c>, so its spirit re-fires on every step forever. One name, tested and set.</para>
/// </summary>
public static class MageStoneQuest
{
    /// <summary>Prefix of the per-mouse flag the mob AI sets (<c>zapped_yin_mouse</c>, …). Written by
    /// game-data/mob_ai.lua when the mouse is struck WHILE CURSED; read here and by the prophets' script.</summary>
    public const string ZapReg = "zapped_";
    public static readonly string[] Mice = { "yin_mouse", "yang_mouse", "void_mouse" };

    /// <summary>Set by the crypt spirit. RTK's intended name — see the class doc for the bug it fixes.</summary>
    public const string GhostReg = "mage_stone_met_ghost";
    /// <summary>0 = Wand has not sent you; 1 = the three tasks are open (RTK <c>registry["mage_ward"]</c>).</summary>
    public const string StageReg = "mage_stone";

    public const string Legend = "family_nangen_mages";
    // "Family to the Nangen Mages (Yuri 33, Summer)" — the title is Atlas's page header, the date format is
    // Character.GameDate. Icon/colour are RTK's; no source names the glyph.
    public const byte LegendIcon = 3, LegendColor = 128;

    public const string Reward   = "spirit_stone";   // Items.csv 25047
    public const string RoseItem = "rose";           // sacrificed
    public const string OreItem  = "ore_high";       // shown, not sacrificed — see the class doc

    public const int MinLevel  = 55;                 // Atlas: "Level Required: 55"
    public const int MagePath  = 3;                  // NpcContext.BaseClass
    /// <summary>Wand, Nagnang's guild master (NPCs.csv 133, map 3828). Nine NPCs share the
    /// <c>MageTrainerNpc</c> identifier the ability is composed onto, and only this one runs the quest:
    /// Atlas sends the player to "Wand at the Mage Guild in Nagnang" by name, and the prophets are up the
    /// stairs from that guild.</summary>
    public const int WandNpcId = 133;

    /// <summary>The prophets' room and the three cells off it (NPCs.csv 134/135/136).</summary>
    public const ushort ProphetsMap = 2570;
    public static readonly ushort[] ProphetCells = { 2571, 2572, 2573 };

    /// <summary>Oh-mudum crypt — the fifth of the nine tombs under the Cemetery (2200), and the one RTK's
    /// tile script puts the spirit in. Atlas only says "walk through the tombs until a spirit speaks to you",
    /// which is what that looks like from the outside.</summary>
    public const ushort CryptMap = 2205;
    /// <summary>The spirit's portrait (RTK <c>convertGraphic(167, "monster")</c>, colour 11).</summary>
    public const int SpiritLook = 167, SpiritColor = 11;

    public static readonly string[] SpiritPages =
    {
        "Who walks these halls? One of the living? Why?",
        "Ah. I see that you are pondering the Void of the Void. Well, the void is not a place but a state of being. Even in complete nothingness, there is always something.",
        "However, within one's own mind, one may be totally alone. Imagine and feel the emptiness. Only in your mind can the Void be experienced.",
        "Heed these words. There may be a time when you need to remember them. Dwell upon them and take solace in the fact you are not ever truly alone.",
    };

    /// <summary>Has this character done everything the three prophets asked? The two components have to be in
    /// hand at the same time, which is what makes the Ore a fetch and not a kill count.</summary>
    public static bool TasksComplete(NpcContext ctx) =>
        Mice.All(m => ctx.Reg(ZapReg + m) == 1)
        && ctx.Reg(GhostReg) == 1
        && ctx.HasItem(RoseItem)
        && ctx.HasItem(OreItem);
}

/// <summary>
/// Wand's half of the quest: the "Mage Stone" menu entry that opens it and closes it.
///
/// <para>Composed onto <c>MageTrainerNpc</c> (game-data/NpcAbilities.csv) because that is the only handle the
/// composition table offers, and narrowed to <see cref="MageStoneQuest.WandNpcId"/> here — the same shape
/// <see cref="SuteQuestAbility"/> uses on the same identifier, and for the same reason: eight other NPCs
/// share it and none of them is Wand.</para>
/// </summary>
public sealed class MageStoneAbility : INpcAbility
{
    public static readonly MageStoneAbility Instance = new();

    /// <summary>Whether this NPC runs the quest at all.</summary>
    public static bool AnswersFor(NpcDef def) => def.Id == MageStoneQuest.WandNpcId;

    /// <summary>The entry is Wand's, and only for mages who have not already earned the mark — RTK hides it
    /// the same way. The LEVEL gate is not hidden but spoken, so a young mage who clicks it is told why
    /// rather than left wondering whether the quest exists.</summary>
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (!AnswersFor(ctx.Def)) yield break;
        if (ctx.BaseClass != MageStoneQuest.MagePath) yield break;
        if (ctx.HasLegend(MageStoneQuest.Legend)) yield break;
        yield return ("Mage Stone", Talk);
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.KarmaTooLow()) return;                       // RTK Tools.checkKarma

        if (ctx.Level < MageStoneQuest.MinLevel)
        {
            await ctx.Say("You are still young for this. The prophets do not waste their breath on the unready.");
            return;
        }

        // Already sent: this click is the turn-in (RTK checks the open-quest branch first, same order).
        if (ctx.Reg(MageStoneQuest.StageReg) == 1) { await TurnIn(ctx); return; }

        await ctx.Say("Ah, I see that you have come for the knowledge of the Mages of Nagnang.");

        int choice = await ctx.Menu(
            "Well, I am not the person who has the knowledge, merely the one who tells those who are worthy where to seek out that knowledge. Are you such a worthy person?",
            new[] { "Yes, I am worthy", "No, I am not worthy." });

        if (choice != 1)
        {
            if (choice == 2) ctx.Notify("I admire your honesty.");
            return;
        }

        await ctx.Say(
            "Well then, I will tell you where to find the knowledge you seek. Above this cave, there is another. Inside of it is the home to three prophets.",
            "Each embodies a mystical force. One is for Yin, the other, Yang and the third is the Void. Each of them will evaluate your potential and will then instruct you on what to do.",
            "If you follow their instructions, and prove to me that you are honorable and wise by returning here after completing all of their tasks, I will reward you with the spirit stone.",
            "To visit each prophet, you need to first attack one of the mice with a spell and then curse that same immortal mouse. The creature will die as an offering and you can then enter.",
            "Take care to curse only ONE creature before entering each room. If you curse more, the wise men will not speak with you and you will need to return to me.",
            "I also implore you, listen to ALL of them and all they have to say. If you do not, I will not grant you the stone.");

        ctx.SetReg(MageStoneQuest.StageReg, 1);
        ctx.Notify("Good luck.");
    }

    private static async Task TurnIn(NpcContext ctx)
    {
        if (!MageStoneQuest.TasksComplete(ctx))
        {
            ctx.Notify("You have not completed all of the tasks handed to you.");
            return;
        }

        // Give FIRST: a full pack must not eat the Rose and pay out nothing (the same order the Old dog's
        // spell reward uses). Nothing else has been spent at this point, so a refusal here costs the player
        // only the walk back.
        if (!ctx.GiveItem(MageStoneQuest.Reward))
        {
            ctx.Notify("You have no room to carry it. Return when your pack is lighter.");
            return;
        }

        ctx.TakeItem(MageStoneQuest.RoseItem, 1);            // the Ore stays — see MageStoneQuest's class doc
        ctx.AddLegend($"Family to the Nangen Mages ({Character.GameDate})", MageStoneQuest.Legend,
                      MageStoneQuest.LegendIcon, MageStoneQuest.LegendColor);

        foreach (var m in MageStoneQuest.Mice) ctx.SetReg(MageStoneQuest.ZapReg + m, 0);
        ctx.SetReg(MageStoneQuest.GhostReg, 0);
        ctx.SetReg(MageStoneQuest.StageReg, 0);

        await ctx.Say(
            "You have learned well and earned the regard of the Nangen Mages. Take this stone, cut long ago by the same prophets who instructed you in our ways.",
            "It holds a little of what each of them is, and it will remember you to them. This is the only one I shall ever give you.");
    }
}
