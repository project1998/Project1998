using Shared;

namespace Server;

/// <summary>
/// The <b>Poet's whip</b> quest — Nagnang's Poet chain, and the only source of the Poet whip (Items.csv
/// 46009). Ported from RTK <c>NPCs/Common/poet_trainer.lua</c> (its "Poet Welcome" branch),
/// <c>Items/Quest/sacred_water.lua</c> and the three tile triggers in
/// <c>onScriptedTiles/onScriptedTilesQuest.lua</c> — see <c>Session.TryForeverBranch</c>,
/// <c>Session.TryNangenPagoda</c> and <c>Session.TryOblivionFall</c> in Session.Navigation.cs, and
/// <c>Session.TrySacredWaterDrop</c> in Session.Items.cs.
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item>Buy a <b>Sonhi pipe</b> from Sying (Sying's Shop, Kaming's Encampment — MessengerNpc stock) and give
/// it to <b>Staff</b>, the Poet guildmaster of Nagnang (NPCs.csv 137, map 2516 — see
/// <see cref="PoetWhipQuest.StaffNpcId"/>, which had to be moved before any of this was reachable).
/// "Welcome Stranger" without a pipe in the bag gets you nothing but "Hmmm, what? Oh hello, Stranger". The
/// pipe is the quest's one sacrifice.</item>
/// <item>He asks for "a shard of wood that will last forever". Walk the ground around the Forever Tree in the
/// Wilderness (map 1002, near 19,91) until you find a <b>Forever branch</b> — 1 step in 100
/// (<see cref="PoetWhipQuest.BranchRate"/>). Bring it back for the legend <b>Became Nangen Acolyte</b>.</item>
/// <item>He tells the story of the evil the Poets banished and hands you a vial of <b>Sacred water</b>. That
/// legend also unlocks the pagoda south of the guild (<see cref="PoetWhipQuest.PagodaMap"/>) into <b>Path of
/// Choice</b> — three routes down, of which the centre one (Path of the Arrow, map 2525) is the short
/// way.</item>
/// <item>In <b>Oblivion</b> (map 2528) every step has a 1-in-100 chance to drop you through into the
/// <b>Subvoid</b> (map 2530) where <b>The Infected</b> waits. Stand next to it, face it, and DROP the water:
/// it dies and the water is spent. See <c>Session.TrySacredWaterDrop</c> for why the drop is the mechanic.</item>
/// <item>Return to Staff for the <b>Poet whip</b> and the legend <b>Destroyed Nagnang Evil</b>.</item>
/// </list>
///
/// <para><b>Do not kill the rabbits.</b> The magic rabbits scattered through the Path maps are what hold the
/// evil in check; killing one after the water is issued makes Staff refuse the turn-in. RTK enforces this with
/// <c>killCount</c>/<c>flushKills</c>, a resettable per-quest counter we do not have — so this measures a
/// DELTA against a baseline snapshotted when the water is handed over
/// (<see cref="PoetWhipQuest.RabbitBaselineReg"/>), the same idiom the armor chains use for their kill steps.
/// Kills banked before the water do not count against you, and taking a fresh vial re-snapshots, which is also
/// the only way to clear the debt: neither period source describes the totem-forgiveness rite well enough to
/// build it (nexusatlas: "seek forgiveness from the Shamans at the Wilderness Totem Shrines"), and it is the
/// same gap already logged against <see cref="AncientLeviathanAbility"/>.</para>
///
/// <para><b>Where the sources disagree.</b> The level gate is <b>50</b> per nexusatlas/quests/poetswhip.php
/// ("Level 50 minimum"); RTK asks 10, and per the project's source ladder the archive outranks RTK's Lua.
/// The <b>reward</b> is the Poet whip per the same page; RTK hands out an <c>essence_charm</c> instead, which
/// is its own custom shield and is not ported (its "protective charm" line is re-pointed at the whip, one word
/// changed). The menu label is Atlas's <b>"Welcome Stranger"</b>, not RTK's internal "Poet Welcome". What RTK
/// IS used for here is <b>prose</b> — Staff's lines survive nowhere else, and RTK's strings are ported dialogue
/// rather than invented; its own typos are kept ("stil", "my enter"), the same call the Sute tale made for the
/// client's.</para>
///
/// <para>Atlas also warns "you cannot obtain another one, you will only get one chance to get this correct."
/// RTK re-issues the water on a <b>24-hour</b> timer (<see cref="PoetWhipQuest.WaterCooldown"/>) and that is
/// what is built: a genuinely one-shot vial bricks the character permanently on a stray keypress, and RTK's
/// timer is the only implementation evidence either way. The Items.csv row is hardened to match the Leviathan
/// talisman (<c>ItmDroppable</c> 0→1 = NoDrop) so the only drop that works is the rite itself.</para>
/// </summary>
public static class PoetWhipQuest
{
    /// <summary>RTK <c>player.quest["nangen_acolyte"]</c> — 0 not started (or finished), 1 owes the branch,
    /// 2 acolyte, water errand live. Same name as the legend below, in a different store; that is RTK's own
    /// collision and is kept so imported characters land on the right step.</summary>
    public const string Key = "nangen_acolyte";
    public const int StageBranch = 1;   // accepted the service, owes a Forever branch
    public const int StageWater  = 2;   // acolyte: the sacred-water errand is live

    /// <summary>RTK <c>gave_sonhi_pipe</c> — the pipe is taken once, before the offer, so refusing and coming
    /// back does not cost a second pipe.</summary>
    public const string PipeGivenReg = "gave_sonhi_pipe";
    /// <summary>RTK <c>sacred_water_timer</c> — unix seconds; a new vial is refused until then.</summary>
    public const string WaterTimerReg = "sacred_water_timer";
    /// <summary>RTK <c>destroyed_infected</c> — set by the drop rite, read by the turn-in.</summary>
    public const string InfectedReg = "destroyed_infected";
    /// <summary>Ours, not RTK's: lifetime magic-rabbit kills at the moment the water was handed over. See the
    /// class doc on why this replaces RTK's <c>flushKills</c>.</summary>
    public const string RabbitBaselineReg = "nangen_rabbit_base";

    public const string LegendAcolyte   = "nangen_acolyte";
    public const string LegendDestroyed = "destroyed_nagnang_evil";

    /// <summary>Icon/colour for the two legends. RTK's values, and the only witness for them — neither period
    /// page records a glyph index. Cosmetic either way (the same call ArmorQuest made).</summary>
    public const byte AcolyteIcon = 4, DestroyedIcon = 7, LegendColor = 128;

    /// <summary>Staff, Poet guildmaster of Nagnang. Nine NPCs share the PoetTrainerNpc identifier the
    /// composition row is keyed by, so this is what keeps the chain in his mouth — RTK narrows the same way,
    /// on <c>npc.mapTitle == "Staff"</c>.
    ///
    /// <para><b>He was not in the world at all.</b> NPCs.csv had him on map <b>3832</b>, an RTK-only inner
    /// sanctum: no <c>TK3832.map</c> ships with the 4.95 client, so the map-registry gate in
    /// <c>Content.LoadNpcs</c> ("map the 4.95 client can't render") silently dropped both him and the warp
    /// that reaches him, and clicking where he should stand did nothing. He is now on <b>2516</b>, the last
    /// renderable room on that path — reached from Nagnang (96|97,101), and named "<b>Poet Staff</b>" by the
    /// client's own map table, which is the evidence: 4.95 shipped ONE guild room per class per city and RTK's
    /// later client split it into an outer hall plus a 38xx sanctum with the master moved inside. He stands at
    /// (8,3), the north end of the hall in front of its dais, facing the door you come in by.</para>
    ///
    /// <para>The same gate still hides <b>Sword</b> (NPC 91, map 3820) and <b>Dagger</b> (NPC 138, map 3824),
    /// Nagnang's Warrior and Rogue masters, whose 2510/2514 halls are named for them in exactly the same way.
    /// Not moved here — that is a separate content call and no quest in this change needs them — but it is the
    /// same bug with the same fix. <b>Wand</b> (NPC 133, map 3828) happens to be renderable and is
    /// unaffected.</para></summary>
    public const int StaffNpcId = 137;
    /// <summary>The hall he stands in, and the reason the move was needed — see <see cref="StaffNpcId"/>.</summary>
    public const ushort StaffMap = 2516;

    public const int MinLevel = 50;   // nexusatlas/quests/poetswhip.php; RTK asks 10
    public const int PoetPath = 4;

    public const string Pipe   = "sonhi_pipe";      // Items.csv 11003 — the sacrifice
    public const string Branch = "forever_branch";  // Items.csv 29014
    public const string Water  = "sacred_water";    // Items.csv 29015 — one-shot, NoDrop but for the rite
    public const string Whip   = "poet_whip";       // Items.csv 46009 — the reward

    public const string RabbitMob   = "magic_rabbit";   // mobs.csv 301 — do NOT kill
    public const string InfectedMob = "the_infected";   // mobs.csv 303 — what the water destroys

    /// <summary>RTK's re-issue window in seconds ("You must wait 24 hours before I give you another sacred
    /// water.").</summary>
    public const int WaterCooldown = 86_400;

    // ---- tile geometry (onScriptedTilesQuest.lua) ------------------------------------------------

    /// <summary>The Forever Tree's ground, where a branch can be found: Wilderness, RTK's box around the tree
    /// at (19,91). The crevasse tile itself is not in play — stepping on it is the Forever Tree entrance
    /// (<c>Session.TryForeverTreeEntrance</c>), which returns before the after-step hooks run.</summary>
    public const ushort TreeMap = 1002;
    public const int TreeMinX = 5, TreeMaxX = 25, TreeMinY = 76, TreeMaxY = 95;
    /// <summary>One step in this many finds the branch (RTK <c>math.random(1,100) == 1</c>).</summary>
    public const int BranchRate = 100;

    /// <summary>The pagoda south of the Poet guild, on Nagnang. Acolytes only; anyone else is shoved two tiles
    /// south (RTK <c>warp(player.m, player.x, player.y + 2)</c>). The 4.95 map agrees with RTK here — both
    /// tiles are open floor in a walled courtyard whose south face is the pagoda's object strip (ids 164-169
    /// at y=115) — and the Warps.csv rows home from Path of Choice already land on (99|100,115), one tile
    /// south of these, which is what pins the pairing.</summary>
    public const ushort PagodaMap = 2500;
    public static readonly int[] PagodaX = { 99, 100 };
    public const int PagodaY = 114;
    public const int PagodaPushY = PagodaY + 2;

    /// <summary>Path of Choice, and the row RTK drops you on (<c>warp(2522, math.random(18,20), 38)</c>) —
    /// one north of the exit warps at y=39 that carry you back to the pagoda.</summary>
    public const ushort PathOfChoiceMap = 2522;
    public const int PathEntryMinX = 18, PathEntryMaxX = 20, PathEntryY = 38;

    /// <summary>Oblivion, and RTK's fall box inside it. Every step in the box has a 1-in-
    /// <see cref="FallRate"/> chance to drop you into the Subvoid.</summary>
    public const ushort OblivionMap = 2528;
    public const int OblivionMinX = 5, OblivionMaxX = 34, OblivionMinY = 6, OblivionMaxY = 36;
    public const int FallRate = 100;

    /// <summary>Where you land, and where The Infected is. There is no warp out — RTK's own warp table has
    /// none either — so the way home is Return/Exit, which this map's <c>MapWarpout</c> allows.</summary>
    public const ushort SubvoidMap = 2530;
    public const int SubvoidX = 5, SubvoidY = 10;

    /// <summary>How near The Infected the water must be dropped (Chebyshev). One: RTK takes the tile you are
    /// FACING and nothing else, and the message says so in capitals.</summary>
    public const int DropRange = 1;

    /// <summary>Portraits RTK hangs its lines on: the infected creature and a magic rabbit, both look+colour
    /// off their mobs.csv rows (193/16 and 125/25).</summary>
    public const int InfectedLook = 193, InfectedColor = 16;
    public const int RabbitLook = 125, RabbitColor = 25;

    /// <summary>Is this tile inside RTK's Forever-Tree branch box?</summary>
    public static bool InTreeGround(int map, int x, int y) =>
        map == TreeMap && x >= TreeMinX && x <= TreeMaxX && y >= TreeMinY && y <= TreeMaxY;

    /// <summary>Is this tile inside RTK's Oblivion fall box?</summary>
    public static bool InOblivionFallBox(int map, int x, int y) =>
        map == OblivionMap && x >= OblivionMinX && x <= OblivionMaxX && y >= OblivionMinY && y <= OblivionMaxY;
}

/// <summary>
/// Staff's half of the chain. Composed onto every PoetTrainerNpc (game-data/NpcAbilities.csv) and narrowed
/// here to Staff himself, to Poets, and to level <see cref="PoetWhipQuest.MinLevel"/> — RTK hides the option
/// the same way rather than refusing inside it, so a Warrior clicking Staff sees the ordinary trainer menu.
/// </summary>
public sealed class PoetWhipQuestAbility : INpcAbility
{
    public static readonly PoetWhipQuestAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (ctx.Def.Id != PoetWhipQuest.StaffNpcId) yield break;
        if (ctx.BasePathId != PoetWhipQuest.PoetPath) yield break;
        if (ctx.Level < PoetWhipQuest.MinLevel) yield break;
        if (ctx.HasLegend(PoetWhipQuest.LegendDestroyed)) yield break;   // done, and it is once per character
        yield return ("Welcome Stranger", Talk);
    }

    /// <summary>The three steps as RTK writes them — sequential <c>if</c>s, not a switch, so handing in the
    /// branch falls straight through into the water errand in the same conversation.</summary>
    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Stage(PoetWhipQuest.Key) == PoetWhipQuest.StageBranch && !await TurnInBranch(ctx)) return;
        if (ctx.Stage(PoetWhipQuest.Key) == PoetWhipQuest.StageWater) { await Errand(ctx); return; }
        await Begin(ctx);
    }

    // ---- step 1: the pipe, and the offer ---------------------------------------------------------
    private static async Task Begin(NpcContext ctx)
    {
        if (ctx.Reg(PoetWhipQuest.PipeGivenReg) == 0)
        {
            // RTK sendMinitext — the status box, not a dialog. It is deliberately incurious: he has no idea
            // who you are until the pipe is in his hand.
            if (!ctx.HasItem(PoetWhipQuest.Pipe)) { ctx.Notify("Hmmm, what? Oh hello, Stranger"); return; }
            if (!ctx.TakeItem(PoetWhipQuest.Pipe, 1)) return;
            ctx.SetReg(PoetWhipQuest.PipeGivenReg, 1);
        }

        await ctx.Say(
            "Why, thank you for the pipe. I have not seen its kind since we had moved into the town. What a thoughtful gift.",
            "Perhaps not all strangers are as evil as we have thought. Hmm... Perhaps we could even ask them to assist us in our service.");

        int choice = await ctx.Menu(
            "I wonder, would you be willing to help us? It would require you to follow the path of the Staff and of Nagnang, if only for a short while.",
            new[] { "I would be honored to help you and your lovely town.", "I am sorry, but my path lies along another way." });

        if (choice != 1)
        {
            if (choice == 2)
            {
                ctx.Notify("Then I wish you the best of luck on your path.");
                ctx.Notify("Thanks again for the pipe.");
            }
            return;   // the pipe stays spent: come back and he picks up from here
        }

        ctx.SetStage(PoetWhipQuest.Key, PoetWhipQuest.StageBranch);
        await ctx.Say(
            "Well then, you will still need to become an initiate of the Staff before we can allow you to know our secrets. You must quest to find a shard of wood that will last forever.",
            "Bring it back to me as a gift and I will allow you to be an initiate of the Staff. Note - it MUST be you who picks up the branch from the tree.");
    }

    /// <summary>Hand over the Forever branch. True if the conversation may continue into the water errand —
    /// false only when the branch is still owed.</summary>
    private static async Task<bool> TurnInBranch(NpcContext ctx)
    {
        if (!ctx.HasItem(PoetWhipQuest.Branch))
        { await ctx.Say("I am still waiting for you to bring me a branch from the Forever tree."); return false; }

        if (!ctx.TakeItem(PoetWhipQuest.Branch, 1)) return false;
        ctx.SetStage(PoetWhipQuest.Key, PoetWhipQuest.StageWater);

        if (!ctx.HasLegend(PoetWhipQuest.LegendAcolyte))
            ctx.AddLegend($"Became Nangen Acolyte ({Character.GameDate})", PoetWhipQuest.LegendAcolyte,
                          PoetWhipQuest.AcolyteIcon, PoetWhipQuest.LegendColor);

        await ctx.Say("Ah, the wood will be well used in this order of Poets. Thank you.");
        return true;
    }

    // ---- steps 3 and 5: the water errand, and the reward ------------------------------------------
    private static async Task Errand(NpcContext ctx)
    {
        if (ctx.KillCount(PoetWhipQuest.RabbitMob) > ctx.Reg(PoetWhipQuest.RabbitBaselineReg))
        {
            await ctx.SayLook(PoetWhipQuest.RabbitLook, PoetWhipQuest.RabbitColor,
                "You killed one of our rabbits! You must cleanse yourself by asking for forgiveness from all of the Totem Animals.");
            return;
        }

        if (ctx.Reg(PoetWhipQuest.InfectedReg) == 1) { await Reward(ctx); return; }

        await ctx.Say(
            "Now for the story of our service. A long time ago, a great evil presence grew here. It began to affect the townsfolk, turning them into a warlike people.",
            "Nagnang has always been strong with a sword, but these people began to become crazy with power. It was only through the honor and intelligence of our Leader Kija that we managed to drive them out.",
            "But the evil stil existed. The Poets managed to banish this presence to another realm. But it grows upon war and pain and our lands are filled with it.",
            "To keep the evil in check, we created magical rabbits to keep the evil balanced. But now it is out of our control once again.",
            "It has begun to pour all of its energy into one of itself, deep in a hidden pocket of Oblivion. If the power increases too much, a hole will tear into this realm and the evil will be free once again.");

        if (ctx.NowUnix <= ctx.Reg(PoetWhipQuest.WaterTimerReg))
        { await ctx.Say("You must wait 24 hours before I give you another sacred water."); return; }

        ctx.SetReg(PoetWhipQuest.WaterTimerReg, (int)(ctx.NowUnix + PoetWhipQuest.WaterCooldown));

        await ctx.SayItem(PoetWhipQuest.Water,
            "You need to take this sacred water into the realm and drop it next to the ugly green infected creature. The water will destroy it and balance will be restored once again.");
        ctx.GiveItem(PoetWhipQuest.Water, 1);

        // RTK's flushKills("magic_rabbit"): from here on, only rabbits killed with the water in hand count.
        ctx.SetReg(PoetWhipQuest.RabbitBaselineReg, ctx.KillCount(PoetWhipQuest.RabbitMob));

        await ctx.SayLook(PoetWhipQuest.InfectedLook, PoetWhipQuest.InfectedColor,
            "Note! You need to be NEXT to the creature and FACING it in order for the magic water to work! Do not lose or give this water away. That would be disrespectful.");
        await ctx.SayLook(PoetWhipQuest.RabbitLook, PoetWhipQuest.RabbitColor,
            "Do not kill any of the rabbits you see. They are our allegiance and our power against the evil.");
        await ctx.Say(
            "The entrance to this realm is at the pagoda just south of here. Only our order my enter. When you return... if you return... come see me, I will be most grateful for your help.");
    }

    private static async Task Reward(NpcContext ctx)
    {
        await ctx.Say(
            "Congratulations! You have helped us in our need to keep balance in our kingdom by relieving a great pressure of evil from within our land.",
            "Please take this whip. It has been imbued with the essence of the sacred water that you used to destroy the evil presence.",
            "May it shield you from evil in your upcoming battles. This is the only one I shall ever give you. Thank you again.");

        ctx.AddLegend($"Destroyed Nagnang Evil ({Character.GameDate})", PoetWhipQuest.LegendDestroyed,
                      PoetWhipQuest.DestroyedIcon, PoetWhipQuest.LegendColor);
        ctx.GiveItem(PoetWhipQuest.Whip, 1);

        // Back to a clean slate, as RTK does — from here the legend is what makes it once per character.
        ctx.SetStage(PoetWhipQuest.Key, 0);
        ctx.SetReg(PoetWhipQuest.InfectedReg, 0);
        ctx.SetReg(PoetWhipQuest.WaterTimerReg, 0);
        ctx.SetReg(PoetWhipQuest.PipeGivenReg, 0);
    }
}
