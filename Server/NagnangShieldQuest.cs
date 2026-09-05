using Shared;

namespace Server;

/// <summary>
/// The Warrior's Nagnang Shield quest — Nagnang's level-10 trial of restraint, and the only way into the
/// Gauntlet (maps 2545-2569). Ported from RTK <c>NPCs/Common/warrior_trainer.lua</c> (its "Strangers" /
/// "Shield" branches), <c>NPCs/quest/nagnangWarriorShieldTotem.lua</c> (the statue at the end) and the
/// Nagnang + "Objective" blocks of <c>onScriptedTiles/onScriptedTilesQuest.lua</c> — see
/// <c>Session.TryGauntletEntrance</c> and <c>Session.TryGauntletAltar</c> in Session.Navigation.cs.
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item>Click <b>Sword</b>, the Nagnang warrior guildmaster (NPCs.csv 91), at level 10+ on the Warrior
/// path. He wants <b>a green squirrel pelt</b> — the squirrels are through Nagnang's southern warp at
/// (134,155) into Southern Path, and drop the pelt at 5% (MobDrops.csv).</item>
/// <item>He sends you into the Gauntlet: <b>kill nothing red or blue</b>. The cave mouth is the alcove in
/// Nagnang's west wall (<see cref="MouthX"/>, <see cref="MouthY"/>), and which of the five parallel copies
/// you get is read off your level (<see cref="Tiers"/>).</item>
/// <item>Walk the five rooms to <b>Objective</b> and touch the <b>statue of Chung Ryong</b>. Kill nothing
/// forbidden on the way and it hands you the <b>Nagnang shield</b> and the legend; kill one and it calls you
/// a killer and throws you out, and the run has to be walked again from the mouth.</item>
/// <item>Optional: say <b>"shield"</b> to <b>Chul</b> the Nagnang smith (NPCs.csv 108, map 2518), who forges
/// the bonded <b>Tall shield</b> for 10 Ginko wood and a Metal — see
/// <see cref="NagnangTallShieldAbility"/>.</item>
/// </list>
///
/// <para><b>Sources.</b> nexusatlas.com/quests/warriorsnagnangshield.php for the shape and the gates
/// ("Level Required: 10", "Warrior Path", "Karma Needed: Not applicable", "Items Lost to Sacrifice: None",
/// rewards "Nagnang Shield" + "New legend mark", and the closing pointer to "Chul Smith in Nagnang for
/// information about making a Tall Shield (bonded item)"). Every line of dialog is RTK's, which is the only
/// surviving transcript of it. The GEOMETRY is ours, not RTK's — see <see cref="MouthY"/>.</para>
///
/// <para><b>Sword had to be moved, and stood nowhere until he was.</b> NPCs.csv put him on map 3820
/// ("Sword", the guildmaster's inner chamber), which has NO terrain in game-data/maps — the 4.95 map set
/// does not contain it, and RTK's own copy is 7.x-tiled and would render as garbage here. An NPC on a map
/// the client cannot render is dropped at load (Content.LoadNpcs), so he existed in the CSV and nowhere
/// else, which is exactly the silent failure this project is full of. He now stands in <b>map 2510,
/// "Warrior Sword"</b> — Nagnang's warrior guild hall, region 3, the room the chamber opens off (Warps.csv
/// 1665-1668), which our world DOES render and which was otherwise empty of NPCs. That is the room the
/// Atlas means by "the Warrior Guild in Nagnang", and 4.95 shipped one guild room per class per city — the
/// outer-hall/inner-sanctum split is RTK's later client. He stands at (8,3), the north end in front of the
/// dais, matching where Staff was put in 2516 for the same reason. Dagger (138, map 3824) is still dark.
/// The inner chamber stays unbuilt; if it is ever authored, moving him back is one CSV field.</para>
///
/// <para><b>The two shields are deliberately different.</b> The trial's Nagnang shield (51001) is NOT in
/// <c>Content.BondedItemIds</c> and the Tall shield (51002) is — exactly what the Atlas says of each — so
/// both arrive correctly through the ordinary grant path with nothing special done here.</para>
/// </summary>
public static class NagnangShieldQuest
{
    /// <summary>RTK <c>quest["nagnang_warrior_trial"]</c>: 0 = not started, 1 = pelt paid and sent into the
    /// Gauntlet. Cleared back to 0 on completion, exactly as RTK does — the legend is what marks it done.</summary>
    public const string StageReg = "nagnang_warrior_trial";

    /// <summary>Forbidden kills counted at the moment the player enters the cave. The trial is a DELTA
    /// against this, which is how a lifetime <see cref="Session.KillCount"/> answers "on this run" — RTK
    /// instead flushes the six counters, which we have no equivalent of and do not need.</summary>
    public const string KillSnapshotReg = "nagnang_trial_kills";

    /// <summary>Legend name. Same string as <see cref="StageReg"/> (RTK reuses it); different namespace.</summary>
    public const string Legend = "nagnang_warrior_trial";

    public const string Pelt   = "green_squirrel_pelt";
    public const string Shield = "nagnang_shield";

    public const int MinLevel    = 10;    // Atlas: "Level Required: 10"
    public const int WarriorPath = 1;     // Atlas: "Prerequisite: Warrior Path"
    public const int SwordNpcId  = 91;    // Sword, the Nagnang warrior guildmaster (map 2510)
    public const int ChulNpcId   = 108;   // Chul, the Nagnang smith (map 2518, "Chul Smith")

    /// <summary>The creatures the trial forbids. The Gauntlet's middle rooms hold red, blue, green and orange
    /// deer, doe and rabbit (AreaSpawns.csv 2546-2548 and their four copies); only the first two colours are
    /// off limits, and the first and last room of every tier are empty.</summary>
    public static readonly string[] Forbidden =
        { "red_deer", "red_doe", "red_rabbit", "blue_deer", "blue_doe", "blue_rabbit" };

    // ---- cave mouth (Nagnang) ---------------------------------------------------------------------
    public const ushort NagnangMap = 2500;

    /// <summary>The two-tile alcove in Nagnang's west wall — walled on three sides by solid SObj pieces (the
    /// 0x0F run of objects 1637-1642 at y 46-48) with the cave-mouth art (790/791) across its mouth.
    /// <b>This is not RTK's coordinate.</b> RTK triggers at (9|10,49) on flat open ground and shoves a
    /// refusal two tiles south, because RTK re-tiled the map to suit its own script; on the client's map the
    /// corresponding feature is this alcove, so the trigger is its dead end and the shove puts you back under
    /// the arch. Read off game-data/maps/TK2500.map, not assumed.</summary>
    public static readonly int[] MouthX = { 9, 10 };
    public const int MouthY       = 46;
    public const int MouthPushToY = 48;

    /// <summary>Where the Gauntlet's own exit warps (Warps.csv) put you back — one step clear of the alcove,
    /// so walking out does not immediately walk you back in.</summary>
    public const int MouthExitY = 49;

    /// <summary>Which of the five parallel copies of the Gauntlet a character gets, by level: RTK's bands from
    /// <c>onScriptedTilesQuest.lua</c> (10-24, 25-39, 40-74, 75-98, 99+), whose floor is the same level 10 the
    /// Atlas states. Each entry is that band's ENTRANCE room; the four rooms behind it are the next four map
    /// ids, wired as ordinary Warps.csv rows. Matched last-qualifying-wins, so it stays a ladder.
    ///
    /// <para>Deliberately NOT EventCaveTiers.csv: that file is one ladder shared by every event cave and says
    /// so in its header, and these bands are a different ladder. One entrance does not earn a second
    /// table.</para></summary>
    public static readonly (int MinLevel, ushort Map)[] Tiers =
        { (10, 2545), (25, 2550), (40, 2555), (75, 2560), (99, 2565) };

    /// <summary>The entrance room for a level, or 0 when the level is below the ladder's floor.</summary>
    public static ushort EntranceFor(int level)
    {
        ushort map = 0;
        foreach (var (min, m) in Tiers) if (level >= min) map = m;
        return map;
    }

    /// <summary>Landing tiles just inside the entrance room (RTK <c>math.random(5, 6), 10</c>).</summary>
    public const int LandX0 = 5, LandX1 = 6, LandY = 10;

    // ---- the Objective room -----------------------------------------------------------------------
    /// <summary>The last room of each tier. Empty of creatures — the trial is over by the time you reach it.</summary>
    public static bool IsObjective(int map) => map is 2549 or 2554 or 2559 or 2564 or 2569;

    /// <summary>The ring of tiles around the statue. It stands on (7,3), (8,3) and (9,3) — three solid SObj
    /// pieces (3239-3241), identical in all five copies — so this is the one-tile perimeter you can actually
    /// stand on and "touch the Altar" from. (RTK's own box, x 5-11 by y 2-6, is the same idea drawn around
    /// its own copy of the room.)</summary>
    public static bool AtAltar(int x, int y) => x >= 6 && x <= 10 && y >= 2 && y <= 4;

    /// <summary>The portrait the statue speaks with: creature look 165, the Chung ryong statue sprite
    /// (mobs.csv 69 <c>museum_chungryong</c>) — and Sword's own words are "a statue of Chung Ryong".</summary>
    public const int StatueLook = 165, StatueColor = 0;

    /// <summary>RTK's legend icon/colour. Its text reads "Completed Nangen Warrior Trial"; the kingdom is
    /// spelled Nagnang everywhere in this project's data and no period screenshot of this mark survives, so
    /// the misspelling is not carried over.</summary>
    public const byte LegendIcon = 9, LegendColor = 128;

    /// <summary>Sword's briefing — the same four pages whether he is setting the task or repeating it.</summary>
    public static readonly string[] Briefing =
    {
        "Anyone who dedicates their lives to the weapon should learn how to use a shield.",
        "First, though, I will ask you to prove this to me. To the West and North of here, there is a cave. This is the training caves for our Warriors.",
        "In it, you will find many different dyed creatures. You may not kill the red and blue ones, you must avoid them.",
        "At the end of the caves, there is a statue of Chung Ryong. If you reach it without killing any of the Blue or Red animals, you will be rewarded with a shield.",
    };

    /// <summary>What the statue says when the trial was kept.</summary>
    public static readonly string[] StatueReward =
    {
        "As you touch the mighty statue, it seems to come to life!",
        "Ah, Mortal, you dared to enter my cave to face me? You are a brave and worthy warrior.",
        "You held your word and harmed none of the red and blue creatures. You have shown honor and skill.",
        "I shall reward you. Take this shield, and may it protect you in your upcoming battles. This is the only one I shall ever give you.",
        "The statue turns back to stone, and you see a shield on the steps, you go to pick it up.",
    };

    /// <summary>What it says when it was not (RTK sendMinitext, not a dialog — the statue never wakes).</summary>
    public static readonly string[] StatueRefusal =
    {
        "You touch the statue several times, but nothing seems to happen. You hear a faint voice calling you a killer...",
        "Perhaps you shouldn't have killed the red and blue animals in the cave.",
    };
}

/// <summary>
/// Sword's half of the quest: take the pelt, send them in, and repeat the briefing while they are on it.
///
/// <para>Composed onto <c>WarriorTrainerNpc</c> (NpcAbilities.csv) because that identifier is the only handle
/// the composition table offers, but it answers for exactly one NPC — RTK gates the same branch on
/// <c>npc.mapTitle == "Sword"</c>, and every other warrior trainer shares the identifier. Same narrowing as
/// <see cref="SuteQuestAbility"/>.</para>
/// </summary>
public sealed class NagnangShieldAbility : INpcAbility
{
    public static readonly NagnangShieldAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (ctx.Def.Id != NagnangShieldQuest.SwordNpcId) yield break;
        if (ctx.BaseClass != NagnangShieldQuest.WarriorPath) yield break;
        if (ctx.Level < NagnangShieldQuest.MinLevel) yield break;
        if (ctx.HasLegend(NagnangShieldQuest.Legend)) yield break;
        // RTK's own two labels: the offer, then the reminder once you are on it.
        yield return (ctx.Stage(NagnangShieldQuest.StageReg) == 0 ? "Strangers" : "Shield", Talk);
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Stage(NagnangShieldQuest.StageReg) == 0)
        {
            if (!ctx.HasItem(NagnangShieldQuest.Pelt))
            {
                // Status box, not a dialog — RTK sendMinitext, and he does not open his mouth for you.
                ctx.Notify("Eh? Please don't bother me.");
                ctx.Notify("You probably couldn't even kill one of the Green squirrels to the south.");
                return;
            }
            ctx.TakeItem(NagnangShieldQuest.Pelt, 1);
            ctx.SetStage(NagnangShieldQuest.StageReg, 1);
        }
        await ctx.Say(NagnangShieldQuest.Briefing);
    }
}

/// <summary>
/// Chul the Nagnang smith's follow-up (RTK <c>NPCs/Common/smith.lua</c>, its <c>speech == "shield"</c>
/// branch): once you hold your path's Nagnang shield legend he will forge the BONDED version for 10 Ginko
/// wood and one Metal. Speech-triggered like the rest of that script, and the Atlas's own pointer is
/// "Speak with Chul Smith in Nagnang for information about making a Tall Shield (bonded item)".
///
/// <para>All four paths are listed because that is RTK's own table and it is four lines; only the warrior's
/// legend is obtainable today, so the other three rows simply never pass the gate until their quests are
/// built.</para>
/// </summary>
public sealed class NagnangTallShieldAbility : INpcAbility, INpcSayHandler
{
    public static readonly NagnangTallShieldAbility Instance = new();

    /// <summary>The shield each path is forged, and the legend that unlocks it (RTK's two parallel arrays,
    /// indexed by <c>player.baseClass</c> 1-4).</summary>
    private static readonly (string Legend, string Shield)[] ByPath =
    {
        (NagnangShieldQuest.Legend, "tall_shield"),      // 1 Warrior
        ("dagger_guild_member",     "round_buckler"),    // 2 Rogue
        ("family_nangen_mages",     "magicians_ward"),   // 3 Mage
        ("destroyed_nagnang_evil",  "essence_charm"),    // 4 Poet
    };

    private const string Wood = "ginko_wood", Metal = "metal";
    private const int WoodCost = 10, MetalCost = 1;

    /// <summary>Heard, not clicked — the smith's click menu is still shop + repair.</summary>
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech != "shield") return false;
        if (ctx.Def.Id != NagnangShieldQuest.ChulNpcId) return false;   // not Chul -> falls through to chat
        if (ctx.KarmaTooLow()) return true;                            // RTK Tools.checkKarma

        int path = ctx.BaseClass;
        if (path < 1 || path > ByPath.Length) return true;             // Peasant: RTK returns silently
        var (legend, shield) = ByPath[path - 1];

        if (!ctx.HasLegend(legend))
        {
            await ctx.Say("You know nothing of shields. When you learn, perhaps I will help you.");
            return true;
        }

        await ctx.Say(
            "Greetings, I see you have come to get a new shield.",
            "These shields are special as only the Nagnang people know how to make them.",
            "However, the materials we need to make them are not available in these parts.",
            "If you would bring me a supply of the items I will use some to make a new shield for you.");

        int choice = await ctx.Menu("Will you give me 10 Ginko wood and one Metal?",
                                    new[] { "Yes, I have it right here.", "Sorry, not now." });
        if (choice != 1)
        {
            if (choice == 2) await ctx.Say("Perhaps another time then. Farewell.");
            return true;
        }

        if (ctx.CountItem(Wood) < WoodCost || ctx.CountItem(Metal) < MetalCost)
        {
            await ctx.Say("You do not have the items I need. Oh well.");
            return true;
        }
        // Take nothing unless the shield has somewhere to land — a full pack must not eat the materials.
        if (ctx.FreeSlotCount < 1)
        {
            await ctx.Say("You have no room to carry it. Come back with an emptier pack.");
            return true;
        }
        ctx.TakeItem(Wood, WoodCost);
        ctx.TakeItem(Metal, MetalCost);
        ctx.GiveItem(shield, 1);                 // bonds on grant: every one of these is a Content bonded id
        await ctx.Say("Good luck to you, and thanks for the materials.");
        return true;
    }
}
