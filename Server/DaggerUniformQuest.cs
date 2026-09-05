using Shared;

namespace Server;

/// <summary>
/// The Rogue's <b>Dagger Uniform</b> quest — the initiation into <b>Master Dagger</b>'s guild in Nagnang,
/// and the gate the Rogue Shield quests sit behind. Ported from RTK
/// <c>NPCs/Common/rogue_trainer.lua</c> (its <c>Dagger Strangers</c> / <c>Blue Rooster</c> branches and its
/// Maso <c>receiveItem</c> branch), <c>NPCs/kugnae/blackbird.lua</c>, <c>Quests/map_entry_quest_checker.lua</c>
/// and <c>Scripts/on_event.lua</c>'s <c>onLook</c>, with the shape and the reward list taken from
/// nexusatlas.com/quests/roguesdaggeruniform.php.
///
/// <para>The chain, in the order a player walks it:</para>
/// <list type="number">
/// <item>Enter the Nagnang Rogue Guild — Nagnang (map 2500) at 019-020/140, into <b>Rogue Dagger</b>
/// (<see cref="GuildMap"/>) — and tap <b>Master Dagger</b> (NPCs.csv <see cref="DaggerNpcId"/>) on
/// the shoulder. He warns you off twice; on the third tap he sets three <b>Dagger assassins</b> on you.
/// They are not meant to be fought — run, and they vanish (<see cref="AssassinSeconds"/>).</item>
/// <item>Come back once they are gone. Coming back at all is the test; he tells you to watch for a
/// <b>Blue Rooster</b>.</item>
/// <item>Find the Blue Rooster wandering southern Buya (Spawns.csv 983, map 330 at 67/140) and LOOK at it —
/// either way of looking counts, see <see cref="NoticeObserved"/>. Report back.</item>
/// <item>Pick <b>Maro</b>'s pocket at the Kugnae Rogue Guild (NPCs.csv <see cref="MaroNpcId"/>, Maro Sanctum
/// map 16) and walk out. Stepping into Kugnae's sunlight, a crow takes the acorn and flies east.</item>
/// <item>Follow it to <b>Dae Shore</b> (map 1004): the crow is an enchanted boy who needs "something bright
/// enough" — a <b>Stardrop</b> — to be freed. Trade it and he gives the acorn back.</item>
/// <item>Take the acorn to Dagger. He swaps it for a scroll to plant on <b>Maso</b>, the Buya Rogue Master
/// (NPCs.csv <see cref="MasoNpcId"/>, Maso Sanctum map 368) — hand it to him with the 'h' key.</item>
/// <item>Return to Dagger for the <b>Dagger Uniform</b> secret and the guild legend.</item>
/// </list>
///
/// <para><b>Nothing here is new content.</b> Every item, creature, NPC and spell this quest needs was
/// already in the data and unreachable: silvery_acorn 29017 / maso_scroll 29018 / silvered_acorn 29019 /
/// stardrop 29005, mobs blue_rooster 7 (already spawned in southern Buya) and dagger_assassin 304, NPCs
/// Dagger 138 / Maro 37 / Maso 42 / Blackbird 139, and Spells.csv 2124 <c>dagger_uniform</c> (a Morphs.csv
/// disguise, so the spell itself already works). What was missing was the code that joins them, plus the two
/// NpcAbilities.csv rows — and Blackbird had no row at all, which per this repo's rule means he was
/// silently inert.</para>
///
/// <para><b>Four deliberate calls.</b></para>
/// <list type="bullet">
/// <item><b>The reward is the spell and the legend, not RTK's shield.</b> RTK hands over a
/// <c>round_buckler</c> and says "Take this shield"; Atlas's Rewards field lists "Dagger Uniform Spell",
/// "Daggers Guild legend mark" and "Access to Rogue Shield quests" and no shield. That field itemizes what
/// it knows (it lists exp elsewhere), so its silence is evidence — and a buckler handed out HERE would be
/// the reward of the very quests this one is supposed to unlock. The two closing lines that name the shield
/// are replaced by one that names the uniform; the first and last lines are RTK's verbatim.</item>
/// <item><b>No level gate.</b> RTK requires level 10; Atlas's Level Required reads "(Not specified)", and
/// the quest's own difficulty is the assassins, who are survivable only by walking away — which any level
/// can do. RTK's OTHER requirement, the Rogue path, is kept: the reward is a rogue-only disguise and Atlas
/// files the page under the Rogue quests.</item>
/// <item><b>One stage key, not RTK's six.</b> RTK spreads this over <c>dagger_clicked</c>,
/// <c>dagger_blue_rooster</c>, <c>seen_blue_rooster</c>, <c>crow_took_silvery_acorn</c>,
/// <c>crow_took_silvery_acorn2</c> and <c>handed_maso_scroll</c>, which is six chances for two of them to
/// disagree. The chain is strictly linear, so it is one counter (<see cref="Key"/>).</item>
/// <item><b>The assassins vanish on a flat timer.</b> RTK's <c>dagger_assassin.lua</c> counts move ticks
/// with no target within 3 tiles and suicides on the fifth. That needs a per-mob AI script hook this server
/// does not have; a lifespan does the same job from the player's side ("they will disappear after a time").
/// They are marked conjured, so killing one pays nothing — which is right either way: the assassins are a
/// beating, not a hunt.</item>
/// </list>
///
/// <para><b>Not ported:</b> the Rogue Shield quests this unlocks. The legend is their prerequisite and is
/// granted here; nothing reads it yet. See docs/common/Deferred-Work.md.</para>
/// </summary>
public static class DaggerUniformQuest
{
    /// <summary>The one registry key this quest keeps, holding <see cref="Stage"/>.</summary>
    public const string Key = "dagger_uniform";

    /// <summary>Master Dagger (NPCs.csv 138). He is a <c>RogueTrainerNpc</c> like nine other NPCs, so the
    /// ability is composed onto that identifier and narrowed by id here.</summary>
    public const int DaggerNpcId = 138;

    /// <summary><b>Rogue Dagger</b> (2514, 18x24) — the Nagnang Rogue Guild, one door north of Nagnang at
    /// 019-020/140, which is the "(020, 141)" Atlas tells the player to walk to.
    ///
    /// <para><b>Dagger was moved here, and that is why this quest was unreachable.</b> His NPCs.csv row put
    /// him on map <b>3824 "Dagger"</b>, a sanctum off the north end of this hall — and 3824 is one of the
    /// 8,188 rows in Maps.csv (RTK's whole 7.x table) with no terrain in the 4.95 client's own map set. The
    /// NPC loader drops any NPC whose map the client cannot render, so Master Dagger was silently not in the
    /// world at all: his row was there, his warps were there, and clicking where he should have stood did
    /// nothing. He now stands in the guild hall itself, at the head of the entrance corridor.</para>
    ///
    /// <para>The two warp pairs between this hall and 3824 are left alone: they already refuse (Warp checks
    /// the map is renderable), and deleting a warp because its destination is missing is a different change
    /// from placing an NPC where the client can see him.</para></summary>
    public const ushort GuildMap = 2514;
    /// <summary>Maro, the Kugnae Rogue Master (NPCs.csv 37, Maro Sanctum map 16) — the mark.</summary>
    public const int MaroNpcId = 37;
    /// <summary>Maso, the Buya Rogue Master (NPCs.csv 42, Maso Sanctum map 368) — the frame-up. His row
    /// already carries <c>NpcCanReceiveItem</c>; this is what finally gives him something to receive.</summary>
    public const int MasoNpcId = 42;
    /// <summary>The crow on Dae Shore's north-east shore (NPCs.csv 139, map 1004 at 72/4). Its identifier
    /// <c>BlackbirdNpc</c> is its alone.</summary>
    public const int CrowNpcId = 139;

    /// <summary>Kugnae town. The only way out of the Kugnae Rogue Guild (map 15 exits nowhere else), which
    /// is why RTK watches this map for the theft and why the crow's line can say "into the sunlight".</summary>
    public const ushort KugnaeMap = 0;

    /// <summary>Rogue. RTK <c>player.baseClass == 2</c>; base path, so the rogue subpaths qualify.</summary>
    public const int RoguePathId = 2;

    public const string Legend = "dagger_guild_member";
    public const byte LegendIcon = 9, LegendColor = 128;   // RTK addLegend(..., 9, 128)

    public const string RewardSpell = "dagger_uniform";
    public const string AssassinMob = "dagger_assassin";
    public const string RoosterMob  = "blue_rooster";

    /// <summary>Maro's acorn, as stolen. A different item from what comes back — RTK's own two keys, and
    /// Atlas names them apart too ("a Silver acorn" going in, "Silvered acorn" coming back).</summary>
    public const string StolenAcorn = "silvery_acorn";
    public const string ReturnedAcorn = "silvered_acorn";
    public const string Stardrop = "stardrop";
    public const string Scroll = "maso_scroll";

    /// <summary>How long the ambush lasts. RTK's own condition is "five move ticks with nobody in reach",
    /// i.e. roughly ten seconds after you break away; this is the flat equivalent — long enough that
    /// standing and fighting is a real decision, short enough to match "they will disappear after a time".</summary>
    public const int AssassinSeconds = 60;

    /// <summary>The crow's portrait for the line it speaks with no NPC in front of the player (NPCs.csv 139
    /// look/colour). <c>DaggerUniformQuestTests.CrowPortraitMatchesItsRow</c> pins it against the row.</summary>
    public const int CrowLook = 92, CrowColor = 0;

    /// <summary>The theft's consequence, spoken on the first step taken in Kugnae while carrying the acorn —
    /// see <c>Session.TryCrowSnatch</c>. RTK fires it from its map-entry hook on the same map; Atlas's step 4
    /// ("exit the guild without scrolling") describes the same moment from the player's side. A player who
    /// scrolls out instead loses nothing: the crow is waiting the next time they walk into Kugnae.</summary>
    public static readonly string[] CrowSnatch =
    {
        "As you step out into the sunlight, the glint of the acorn attracts a crow. He snatches it and flies east towards the northern beeches of Dae Shore.",
    };

    /// <summary>What looking at the Blue Rooster tells you. RTK's <c>onLook</c> sets its flag silently, which
    /// leaves a player who has done step 3 with no way to know it — and the step is "look at a bird", so
    /// there is nothing else to go on.</summary>
    public const string RoosterNoticed = "A Blue Rooster. Dagger will want to hear of this.";

    /// <summary>Where the player is along the chain. Linear, so a plain counter — the value is also the
    /// answer to "what is Dagger waiting for".</summary>
    public static class Stage
    {
        public const int Unmet          = 0;    // never tapped him
        public const int TappedOnce     = 1;    // "I shall not speak with you Ever."
        public const int TappedTwice    = 2;    // "Bother me again…"
        public const int Assaulted      = 3;    // three assassins are (or were) on you
        public const int WatchForRooster = 4;   // came back anyway; told to watch for a Blue Rooster
        public const int SeenRooster    = 5;    // looked at the Blue Rooster in southern Buya
        public const int StealAcorn     = 6;    // sent after Maro's acorn (and Maro will part with it)
        public const int CrowTookIt     = 7;    // snatched in Kugnae, flown east to Dae Shore
        public const int CrowSpoke      = 8;    // the boy has asked for a Stardrop
        public const int PlantScroll    = 9;    // carrying maso_scroll to Buya
        public const int ScrollPlanted  = 10;   // Maso has read it; go and collect
        public const int Done           = 11;
    }

}

/// <summary>
/// Every part of the quest that is a conversation. Composed onto <c>RogueTrainerNpc</c> (which is Dagger,
/// Maro and Maso, plus seven trainers with no part in this) and onto <c>BlackbirdNpc</c> (the crow), and
/// narrowed to the four of them by id — see <see cref="Entries"/>.
/// </summary>
public sealed class DaggerUniformAbility : INpcAbility, INpcHandItemHandler
{
    public static readonly DaggerUniformAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        int stage = ctx.Stage(DaggerUniformQuest.Key);

        if (ctx.Def.Id == DaggerUniformQuest.CrowNpcId)
        {
            // The crow is only a crow to anyone else — RTK's blackbird says nothing at all unless you are
            // the one whose acorn it took.
            if (stage is DaggerUniformQuest.Stage.CrowTookIt or DaggerUniformQuest.Stage.CrowSpoke)
                yield return ("Approach the crow", Crow);
            yield break;
        }

        if (ctx.BasePathId != DaggerUniformQuest.RoguePathId) yield break;
        if (stage == DaggerUniformQuest.Stage.Done) yield break;

        if (ctx.Def.Id == DaggerUniformQuest.DaggerNpcId)
        {
            if (stage < DaggerUniformQuest.Stage.WatchForRooster) yield return ("Dagger Strangers", Strangers);
            else                                                  yield return ("Blue Rooster", BlueRooster);
        }
        // Maro's pocket, offered only while Dagger is waiting on it and the acorn is not already yours.
        else if (ctx.Def.Id == DaggerUniformQuest.MaroNpcId
                 && stage == DaggerUniformQuest.Stage.StealAcorn
                 && !ctx.HasItem(DaggerUniformQuest.StolenAcorn))
        {
            yield return ("Pick his pocket", PickPocket);
        }
    }

    // ---- Dagger, before the assault: three taps on the shoulder ----------------------------------
    // RTK's three minitexts, one per click, then the ambush. Its double space in the third line is dropped;
    // this is RTK's own text, not a client screenshot, so its typography carries no weight.
    private static async Task Strangers(NpcContext ctx)
    {
        if (ctx.KarmaTooLow()) return;

        switch (ctx.Stage(DaggerUniformQuest.Key))
        {
            case DaggerUniformQuest.Stage.Unmet:
                ctx.Notify("I shall not speak with you Ever.");
                ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.TappedOnce);
                return;

            case DaggerUniformQuest.Stage.TappedOnce:
                ctx.Notify("Bother me again, and you shall die seeing what hides in the shadows.");
                ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.TappedTwice);
                return;

            case DaggerUniformQuest.Stage.TappedTwice:
                ctx.Notify("This is what you get for your annoyance. Attack!");
                ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.Assaulted);
                ctx.SpawnAmbush(DaggerUniformQuest.AssassinMob, DaggerUniformQuest.AssassinSeconds);
                return;

            default:   // Assaulted — and you came back
                ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.WatchForRooster);
                await ctx.Say("So, you still return to me even after the assault. You have a glimmer of promise... or stupidity. Return when you see a Blue Rooster.");
                return;
        }
    }

    // ---- Dagger, after the assault: the rest of the chain ----------------------------------------
    private static async Task BlueRooster(NpcContext ctx)
    {
        if (ctx.KarmaTooLow()) return;

        switch (ctx.Stage(DaggerUniformQuest.Key))
        {
            case DaggerUniformQuest.Stage.WatchForRooster:
                await ctx.Say("Return to me when you see a Blue Rooster.");
                return;

            case DaggerUniformQuest.Stage.SeenRooster:
                ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.StealAcorn);
                await ctx.Say(
                    "Ah, seen the Blue Rooster have you? That is good that you came at my summoning.",
                    "I have decided that many of you Strangers may make very good additions to my little clan. Perhaps the ways of the Night will not be lost.",
                    "But first, I need to see if you have what it takes. The other so called Rogue masters are merely pretenders. First, go to that pretender Maro in Kugnae.",
                    "He keeps in his pocket a Silver acorn for good luck. Snatch it for me to show that even your prowness is better than his.");
                return;

            case DaggerUniformQuest.Stage.ScrollPlanted:
                await Reward(ctx);
                return;

            case DaggerUniformQuest.Stage.PlantScroll:
                await ctx.Say("I am still waiting for you to finish your last task.");
                return;

            // StealAcorn / CrowTookIt / CrowSpoke — he wants the acorn, and only the acorn ends this branch.
            default:
                if (!ctx.HasItem(DaggerUniformQuest.ReturnedAcorn))
                {
                    await ctx.Say(
                        "First, go to that pretender Maro in Kugnae.",
                        "He keeps in his pocket a Silver acorn for good luck. Snatch it for me to show that even your prowness is better than his.");
                    return;
                }

                ctx.TakeItem(DaggerUniformQuest.ReturnedAcorn, 1);
                ctx.GiveItem(DaggerUniformQuest.Scroll, 1);   // the acorn's slot just freed, so this fits
                ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.PlantScroll);
                await ctx.Say(
                    "So you have managed to capture the acorn from that fool, Maro, eh? Good for you. Now it is time for you to pull the wool over Maso's eyes in Buya.",
                    "I'll take that acorn and place it somewhere safe. Take this scroll and slide it into Maso's pocket. It is easy to be a pickpocket, a bit harder to be a put-pocket.",
                    "If you finish with that, and return to me with the ability to learn a spell, then you will be worthy of wearing the uniform of the Daggers.");
                return;
        }
    }

    /// <summary>The end of it. "Return to me with the ability to learn a spell" is Dagger's own warning that
    /// this needs a free spellbook slot, so a full book is refused in his words rather than silently eating
    /// the turn-in.</summary>
    private static async Task Reward(NpcContext ctx)
    {
        var uniform = Content.SpellByKey(DaggerUniformQuest.RewardSpell);
        if (uniform is null || !ctx.LearnSpell(uniform))
        {
            await ctx.Say("You came back without the ability to learn a spell. Make room for what I have to teach you.");
            return;
        }

        ctx.AddLegend($"Member of Dagger's guild ({Character.GameDate})", DaggerUniformQuest.Legend,
                      DaggerUniformQuest.LegendIcon, DaggerUniformQuest.LegendColor);
        ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.Done);

        // RTK's opening and closing lines, verbatim. Its middle two name the round buckler it hands over,
        // which is not this quest's reward (see the class doc) — the line between them is authored, and says
        // what he actually gives.
        await ctx.Say(
            "Very good! You have shown yourself to be worthy of the Daggers' protection.",
            "Wear the uniform of the Daggers, then, and walk among us unremarked.",
            "May it aid you in your future missions. This is the only one I shall ever give you.");
    }

    // ---- Maro's pocket ---------------------------------------------------------------------------
    // No source for this beat: RTK never built it (its map-entry hook waits on an acorn nothing ever gives
    // out), and Atlas only says "successfully steal the item". The theft itself is what the quest is
    // testing, and there is no pickpocketing mechanic in 4.95 to hang a roll on, so it succeeds — the
    // consequence, and the actual test, is the walk out through Kugnae.
    private static async Task PickPocket(NpcContext ctx)
    {
        if (!ctx.GiveItem(DaggerUniformQuest.StolenAcorn, 1))
        {
            await ctx.Say("Your pack is too full to take anything of mine.");
            return;
        }
        ctx.Notify($"You lift a {ctx.ItemName(DaggerUniformQuest.StolenAcorn)} from Maro's pocket.");
        await ctx.Say("Maro looks straight through you, and goes on with his business.");
    }

    // ---- the crow on Dae Shore -------------------------------------------------------------------
    // blackbird.lua verbatim, typos and all ("furtune"): it is the only record of these lines, and the
    // corrections a reader would make are guesses about what the 2001 client printed.
    private static async Task Crow(NpcContext ctx)
    {
        if (ctx.Stage(DaggerUniformQuest.Key) == DaggerUniformQuest.Stage.CrowTookIt)
        {
            ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.CrowSpoke);
            await ctx.Say(
                "The crow sits there watching you approach. When you get close enough it squawks, \"Please don't harm me!\"",
                "\"I am actually a small boy who once thought he was smarter than a Tiger,\" the crow says. \"But the Tiger turned out to be an evil spirit.\"",
                "\"'So you think you are smarter than me?'\" said the Tiger and he changed into this bird. \"'There! Now you'll remain this dull bird until you find something bright enough to free you!'\"",
                "\"I have been searching for something bright enough and hoped that the acorn would work. But alas, I am still a Crow. I do not know what I need!\"",
                "\"I had asked an old furtune teller if he knew what I needed but all he said was that the answer would drop from the stars. If you could help me, I would return the acorn to you.\"");
            return;
        }

        if (!ctx.HasItem(DaggerUniformQuest.Stardrop))
        {
            await ctx.Say("\"I had asked an old furtune teller if he knew what I needed but all he said was that the answer would drop from the stars. If you could help me, I would return the acorn to you.\"");
            return;
        }

        ctx.TakeItem(DaggerUniformQuest.Stardrop, 1);
        ctx.GiveItem(DaggerUniformQuest.ReturnedAcorn, 1);
        await ctx.Say("The crow's eyes widen. \"GIMME THAT!\" he squawks and plucks a Stardrop from you.");
        // The boy behind the bird — RTK swaps the portrait to look 208 c21 for this last line.
        await ctx.SayLook(208, 21,
            "\"Thank you! My mom must be so worried. Here's your Acorn back. Oh, and don't mind the other crow, he's just a good friend.\"");
    }

    /// <summary>Planting the scroll on Maso — the native 'h' gesture, which is how RTK does it too (its
    /// <c>receiveItem</c> branch). Declining anything else lets the generic refusal fire, so a rogue who
    /// hands a trainer their dinner still gets it back.</summary>
    public async Task<bool> OnHandItem(NpcContext ctx, ItemDef item, int amount)
    {
        if (ctx.Def.Id != DaggerUniformQuest.MasoNpcId) return false;
        if (item.Key != DaggerUniformQuest.Scroll) return false;
        if (ctx.Stage(DaggerUniformQuest.Key) != DaggerUniformQuest.Stage.PlantScroll) return false;
        if (!ctx.TakeItem(DaggerUniformQuest.Scroll, 1)) return false;

        ctx.SetStage(DaggerUniformQuest.Key, DaggerUniformQuest.Stage.ScrollPlanted);
        await ctx.Say(
            "\".....Eh? What is this?....\"",
            "\"That arrogant fool! Does Maro really believe that he can destroy me?!!! I will have to take some actions against him...\"");
        return true;
    }
}
