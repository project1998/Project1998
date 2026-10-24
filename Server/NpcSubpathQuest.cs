using Shared;

namespace Server;

/// <summary>
/// The way into the four NPC subpaths (Chung ryong, Baekho, Ju jak, Hyun moo), ported from RTK: the Watcher's
/// blessing, a step trigger on Buya's north-west field (the "Blessed by the Watcher" block of
/// <c>onScriptedTiles/onScriptedTilesQuest.lua</c>; see <c>Session.TryWatcherBlessing</c>), and the joining at
/// Baek in Kugnae (the subpath branch of <c>NPCs/kugnae/baek.lua</c>'s <c>onSayClick</c>, here
/// <see cref="NpcSubpathAbility"/>). The way out is the Dog's cleanse in <c>game-data/npc_dialog.lua</c>
/// (<see cref="NpcContext.LeaveNpcSubpath"/>).
///
/// <para>The chain, as a player walks it:</para>
/// <list type="number">
/// <item>With Ox karma, walk Buya's north-west field (map 330, x 6-28, y 4-14). One step in fifty a rabbit
/// watches you; with Ox karma it smiles, and you are blessed (<see cref="BlessedReg"/>, RTK's own key). Without
/// it the rabbit only bounds away. RTK checks no level here.</item>
/// <item>At level 99, in your base class, holding the Dog-linguist legend, say your class's subpath to Baek:
/// "chung ryong" (Warrior), "baekho" (Rogue), "ju jak" (Mage), "hyun moo" (Poet).</item>
/// <item>He takes a Mythic favor, then 10 Splendid tiger pelts, then 1,000,000,000 experience, in that order and
/// as he checks each one, so a player short of the pelts has already given the favor and one short of the
/// experience has given both. RTK's lines say so ("As punishment for failing to follow my instructions, you must
/// now bring me another Favor as well"), and tswolf 2001 warns of it: "Be sure you have all items at once
/// otherwise you could lose yer items".</item>
/// <item>The path becomes the class's NPC subpath, the rank (mark) kept; the legend "Attained Totem Mastery" and
/// a "[SUBPATH]" line to every player online follow, as RTK.</item>
/// </list>
///
/// <para><b>Sources.</b> RTK for the lines, the field, the odds and the order (RTK is identity and structure
/// here, not balance). tswolf's NPC subpath quest page (Wayback 2001-06-27, twelve days before our era date)
/// gives the same steps and the same price: level 99, the Watcher's blessing with Ox karma, outside Buya's north
/// gate as far west as you can go, then 1 billion experience, a Mythic favor and 10 Splendid tiger pelts at Baek.
/// RTK also asks for the Dog-linguist legend, which tswolf does not list; it is kept, as RTK (the project
/// owner's decision of 2026-10-24). The legend and the broadcast are RTK's; tswolf mentions neither.</para>
///
/// <para>State is RTK's registry key, the path and a legend, so nothing new is persisted.</para>
/// </summary>
public static class NpcSubpathQuest
{
    // ---- the Watcher's blessing --------------------------------------------------------------------------

    /// <summary>RTK <c>player.quest["blessed_by_watcher"]</c>: 1 once the rabbit has smiled. Baek reads it; nothing
    /// clears it, so a player who leaves at the Dog and comes back is still blessed (RTK the same).</summary>
    public const string BlessedReg = "blessed_by_watcher";

    /// <summary>RTK <c>player.mapTitle == "Buya"</c>: Maps.csv 330 is the only map with that title.</summary>
    public const ushort WatcherMap = 330;

    /// <summary>RTK's box: <c>x &gt;= 6 and x &lt;= 28 and y &gt;= 4 and y &lt;= 14</c>.</summary>
    public const int FieldX0 = 6, FieldX1 = 28, FieldY0 = 4, FieldY1 = 14;

    /// <summary>RTK <c>math.random(1, 50) == 1</c>: one step in fifty.</summary>
    public const int WatcherOddsOneIn = 50;

    /// <summary>RTK <c>player:karmaCheck("ox")</c>, the only requirement the rabbit has.</summary>
    public const string BlessingKarma = "ox";

    /// <summary>The rabbit's portrait: RTK <c>convertGraphic(21, "monster")</c>, colour 3.</summary>
    public const int WatcherLook = 21, WatcherColor = 3;

    /// <summary>RTK's line when the rabbit blesses you (Ox karma).</summary>
    public const string WatcherSmiles =
        "Out of the corner of your eye, you spot a rabbit watching you. It seems to smile slightly at you, then bounds away.";

    /// <summary>RTK's line when it does not (karma below Ox).</summary>
    public const string WatcherBoundsAway =
        "Out of the corner of your eye, you spot a rabbit watching you. As you turn your head, it bounds away.";

    public static bool InWatcherField(ushort map, int x, int y) =>
        map == WatcherMap && x >= FieldX0 && x <= FieldX1 && y >= FieldY0 && y <= FieldY1;

    // ---- joining at Baek ---------------------------------------------------------------------------------

    /// <summary>The word Baek answers, and the path it makes you (RTK <c>updatePath(6..9, player.mark)</c> by base
    /// class). Each is the subpath's own Paths.csv name, lower-cased, as RTK compares the lowered speech.</summary>
    public static readonly IReadOnlyDictionary<string, int> SubpathForWord = new Dictionary<string, int>
    {
        ["chung ryong"] = 6,
        ["baekho"]      = 7,
        ["ju jak"]      = 8,
        ["hyun moo"]    = 9,
    };

    public const int JoinLevel = 99;

    /// <summary>RTK <c>player.class &gt;= 10</c>: a PC subpath (Paths.csv 10-21, and the GM row 22 above them).</summary>
    public const int FirstPcSubpath = 10;

    /// <summary>RTK <c>player.class &gt; 4</c>: anything past the four base classes has "already committed".</summary>
    public const int LastBaseClass = 4;

    public const uint JoinExp = 1_000_000_000;
    public const string Pelt = "splendid_tiger_pelt";
    public const int PeltCount = 10;

    /// <summary>The twelve Mythic favors in RTK's order. RTK walks the whole list and keeps the LAST one the player
    /// holds, so that is the one taken.</summary>
    public static readonly string[] Favors =
    {
        "dragons_favor", "roosters_favor", "dogs_favor", "horses_favor", "monkeys_favor", "oxs_favor",
        "pigs_favor", "rabbits_favor", "rats_favor", "sheeps_favor", "snakes_favor", "tigers_favor",
    };

    /// <summary>RTK <c>player:hasLegend("dog_linguist")</c>; the legend the Spotted dog gives (npc_dialog.lua DOG_LEGEND).</summary>
    public const string DogLegend = "dog_linguist";

    /// <summary>RTK <c>addLegend("Attained Totem Mastery (" .. curT() .. ")", "attained_totem_mastery", 3, 128)</c>.</summary>
    public const string Legend = "attained_totem_mastery";
    public const byte LegendIcon = 3, LegendColor = 128;
    public static string LegendText() => $"Attained Totem Mastery ({Character.GameDate})";

    /// <summary>RTK <c>broadcast(-1, "[SUBPATH]: Congratulations to our newest " .. player.classNameMark .. " " ..
    /// player.name .. "!")</c>, read after the path has changed.</summary>
    public static string Broadcast(string classTitle, string name) =>
        $"[SUBPATH]: Congratulations to our newest {classTitle} {name}!";

    /// <summary>The book RTK shows the study page with (<c>convertGraphic(20, "item")</c>, colour 0): Items.csv
    /// <c>book</c> is icon 20, colour 0.</summary>
    public const string StudyItem = "book";

    // RTK's lines, verbatim, in the order baek.lua says them.
    public const string TooYoung =
        "You are too young to be changing your mind on such matters. Return later, and we can talk again.";
    public const string InPcSubpath = "You cannot join an NPC subpath while you belong to a PC subpath.";
    public const string KarmaTooLow = "Come back when your karma is higher.";
    public const string NotBlessed = "You have not been blessed.";
    public const string AlreadyCommitted = "You have already committed to a subpath.";

    public static readonly string[] Offer =
    {
        "Hmm... yes, I have studied the ways of the four totem animals. Do you wish to attain knowledge and power few mortals will know?",
        "You must be committed to serve the totem animals. Should you choose this arduous path, you will NEVER be able to join a subpath.",
        "To prove your devotion, you must make many sacrifices. First, you must bring to me a Favor from one of the Mythic animals.",
        "Then you must bring me ten hides from the most dreaded of caves, where the exits are not easily found and the enemies hide in shadows.",
        "Finally, you must sacrifice 1,000,000,000 experience to complete the most difficult studying involved.",
    };

    public const string NoFavor = "If you ally with a Mythic animal, they will give you Favor.";
    public const string NoPelts =
        "You have forgotten the hides! As punishment for failing to follow my instructions, you must now bring me another Favor as well.";
    public const string NoExp =
        "You must have forgotten the experience needed! As punishment for failing to follow my instructions, you must now bring me another Favor as well as more hides.";
    public const string Studied = "You study for many days under the tutelage of Baek. You learn many rituals and legends.";
    public const string Ready = "You are ready. You will now be able to learn new rituals from your Guildmaster. Go well, my friend!";
}

/// <summary>
/// Baek's half of <see cref="NpcSubpathQuest"/>: the four subpath words, spoken near him in his Kugnae shop.
/// Speech only; his Buy and Sell stay with <see cref="ShopAbility"/>, listed first in his NpcAbilities.csv row.
/// Every check is RTK's, in RTK's order, and a check RTK fails in silence (no Dog-linguist legend, another class's
/// word) is silent here too.
/// </summary>
public sealed class NpcSubpathAbility : INpcAbility, INpcSayHandler
{
    public static readonly NpcSubpathAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (!NpcSubpathQuest.SubpathForWord.TryGetValue(speech, out int subpath)) return false;
        if (ctx.KarmaTooLow()) return true;                                          // RTK Tools.checkKarma

        if (ctx.Level < NpcSubpathQuest.JoinLevel) { await ctx.Say(NpcSubpathQuest.TooYoung); return true; }
        if (ctx.ClassId >= NpcSubpathQuest.FirstPcSubpath) { await ctx.Say(NpcSubpathQuest.InPcSubpath); return true; }
        if (!ctx.KarmaCheck(NpcSubpathQuest.BlessingKarma)) { await ctx.Say(NpcSubpathQuest.KarmaTooLow); return true; }
        if (ctx.Quest(QuestState.Registry).Get(NpcSubpathQuest.BlessedReg) == 0)
        { await ctx.Say(NpcSubpathQuest.NotBlessed); return true; }
        if (!ctx.HasLegend(NpcSubpathQuest.DogLegend)) return true;                  // RTK returns without a word
        if (ctx.ClassId > NpcSubpathQuest.LastBaseClass) { await ctx.Say(NpcSubpathQuest.AlreadyCommitted); return true; }
        if (ctx.ClassId != Content.PathBaseOf(subpath)) return true;                 // another class's word: RTK is silent

        await ctx.Say(NpcSubpathQuest.Offer);

        // Each part is taken as it is checked, as RTK does: a later shortfall does not give back an earlier part.
        string? favor = NpcSubpathQuest.Favors.LastOrDefault(f => ctx.HasItem(f));
        if (favor is null) { await ctx.Say(NpcSubpathQuest.NoFavor); return true; }
        ctx.TakeItem(favor, 1);

        if (!ctx.HasItem(NpcSubpathQuest.Pelt, NpcSubpathQuest.PeltCount)) { await ctx.Say(NpcSubpathQuest.NoPelts); return true; }
        ctx.TakeItem(NpcSubpathQuest.Pelt, NpcSubpathQuest.PeltCount);

        if (!ctx.SpendExp(NpcSubpathQuest.JoinExp)) { await ctx.Say(NpcSubpathQuest.NoExp); return true; }

        if (!ctx.HasLegend(NpcSubpathQuest.Legend))
            ctx.AddLegend(NpcSubpathQuest.LegendText(), NpcSubpathQuest.Legend, NpcSubpathQuest.LegendIcon, NpcSubpathQuest.LegendColor);
        ctx.SetClass(subpath);                                                       // RTK updatePath(subpath, player.mark): the mark is untouched
        Log.Info($"   -> NPC SUBPATH: {ctx.PlayerName} joined {Content.PathName(subpath)} at {ctx.Def.Name} (favor {favor})");
        ctx.AnnounceToWorld(NpcSubpathQuest.Broadcast(ctx.ClassTitle, ctx.PlayerName));

        await ctx.SayItem(NpcSubpathQuest.StudyItem, NpcSubpathQuest.Studied);
        await ctx.Say(NpcSubpathQuest.Ready);
        return true;
    }
}
