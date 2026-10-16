using Shared;

namespace Server;

/// <summary>
/// Mentorship — the <b>Mentor</b> ability (Spells.csv 106, taught by every path's trainer at level 40) and
/// the tally the <b>Poet Moon armor</b> chain counts. Ported from RTK <c>Spells/common/mentor.lua</c>.
///
/// <para><b>Two casts, one relationship.</b> Cast it near a player between the 3rd and 8th insight who has
/// never been mentored and has no mentor, and they get an accept/decline prompt; accept and they are your
/// protégé. Cast it near that same protégé once they reach the 15th insight and the mentorship culminates:
/// your tally rises by one, they keep a permanent "Mentored by …" mark, and both of you are free again.</para>
///
/// <para><b>Why it is repaired rather than transcribed.</b> RTK's own script is broken here: its
/// culmination branch sits INSIDE <c>if target.level &lt; 3 or target.level &gt; 8</c> and then tests
/// <c>target.level &gt;= 15</c>, so the two arms of its own conditional contradict each other and the
/// offer path is unreachable for exactly the levels it advertises. The dialogue it speaks states the rule
/// plainly — <i>"as long as they have reached the 3rd insight and have not exceeded the 8th … you may
/// culminate the mentorship when the mentoree has reached the 15th"</i> — and per this project's standing
/// rule, when RTK's strings and RTK's logic disagree the strings are the better witness. The strings are
/// implemented.</para>
///
/// <para><b>The mentor is remembered by NAME</b>, not by the transient session id RTK stores, so a
/// mentorship survives both parties logging out — it has to, since it spans a dozen levels of the
/// protégé's growth.</para>
///
/// <para><b>No karma penalty.</b> nexusatlas warns that the Poet Moon step "could bring you to dramatically
/// low karma (Snake!) if you are not careful", but nothing in it or in RTK says the cast itself docks
/// karma, and the likeliest reading is the obvious one — shepherding a level-3 through twelve insights
/// means hunting far below your own, which is where the karma goes. Inventing a debit here would be
/// guessing, so there is none.</para>
/// </summary>
public static class Mentorship
{
    /// <summary>Completed mentorships (the mentor's side), a flat registry key
    /// (<see cref="QuestState.Registry"/>). Poet Moon reads this — see <see cref="ArmorQuest.MentoredReg"/>, the
    /// same key.</summary>
    public const string MentoredReg = ArmorQuest.MentoredReg;

    /// <summary>The namespace of the mentorship's own state (<see cref="QuestState"/>).</summary>
    public const string Key = "mentorship";

    /// <summary>On the PROTÉGÉ: the name of the character currently mentoring them ("" = none). A text slot of
    /// <see cref="Key"/>, saved in the string registry as <c>mentor</c>.</summary>
    public const string MentorSlot = "text.mentor";

    /// <summary>Permanent mark on someone who has completed a mentorship — one per life, which is what
    /// stops a pair of friends farming the tally between them.</summary>
    public const string MentoredByLegend = "mentored_by";
    /// <summary>In-progress mark on a protégé; replaced by <see cref="MentoredByLegend"/> at culmination.</summary>
    public const string BeingMentoredLegend = "being_mentored_by";
    /// <summary>The mentor's own running mark, rewritten with the new total each time.</summary>
    public const string MentorLegend = "mentored";

    public const int MinProtegeLevel = 3;    // "have reached the 3rd insight"
    public const int MaxProtegeLevel = 8;    // "and have not exceeded the 8th insight"
    public const int CulminateLevel  = 15;   // "when the mentoree has reached the 15th insight"

    public const byte LegendIcon = 3, LegendColor = 1;
}

public sealed partial class Session
{
    /// <summary>Lua primitive behind <c>verbs.mentor</c>: fires the ask-a-name flow. The cast animation plays
    /// regardless, exactly as propose does — the outcome resolves asynchronously.</summary>
    internal bool LuaMentor(SpellDef sp) { _ = RunMentorAsync(sp); return true; }

    private async Task RunMentorAsync(SpellDef sp)
    {
        string? name = await DlgInput(MarriageVirtualNpc, "Who would you like to mentor?");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, _char.Name, StringComparison.OrdinalIgnoreCase))
        { SendMiniText("You can't mentor yourself."); return; }

        // Same sentence, same channel, one lookup — Session.Resolve.cs also carries the lock-discipline note.
        var target = ResolveOnlinePlayer(name, "Player is not valid or not online.", RefuseChannel.MiniText);
        if (target is null) return;
        if (target.CharMap != CharMap)
        { SendMiniText($"{target.Snapshot().Name} must be near you when you ask to mentor."); return; }

        string them = target.Snapshot().Name;
        // Everything the checks below ask of the protégé, read in one section inside the protégé's monitor (#29):
        // their mentor slot, their level and whether they have been mentored. This runs on the MENTOR's thread (the
        // reply that resumed it, under the mentor's monitor; never World._lock), so the protégé's is taken by
        // StateRank. Before this the reads were bare: the walk of the protégé's legends threw "Collection was
        // modified" when their own handler edited the list, and the conversation died with nothing said.
        var (mentorOf, level, mentored) = target.WithState(() => (
            target.Quest(Mentorship.Key).GetText(Mentorship.MentorSlot),
            target.CharLevel,
            target.HasLegend(Mentorship.MentoredByLegend)));
        bool mine = string.Equals(mentorOf, _char.Name, StringComparison.OrdinalIgnoreCase);

        // ---- culminate: this is already your protégé -------------------------------------------
        if (mine)
        {
            if (level < Mentorship.CulminateLevel)
            { SendMiniText($"You may complete your mentorship of {them} at level {Mentorship.CulminateLevel}."); return; }

            int done = await DlgMenu(MarriageVirtualNpc,
                $"You may complete your mentorship of {them}. Do you wish to continue?",
                new[] { "Yes, that's fine.", "No, absolutely not." });
            if (done != 1) return;

            var registry = Quest(QuestState.Registry);
            int total = registry.Get(Mentorship.MentoredReg) + 1;
            registry.Set(Mentorship.MentoredReg, total);
            AddLegend($"Mentored {total} new player{(total == 1 ? "" : "s")}", Mentorship.MentorLegend,
                      Mentorship.LegendIcon, Mentorship.LegendColor);

            // Three mutations on the PROTÉGÉ's character, run from the MENTOR's thread — one critical
            // section on their side so their own read loop or the autosave sweep cannot catch them
            // half-unbound (#29).
            target.WithState(() =>
            {
                target.Quest(Mentorship.Key).SetText(Mentorship.MentorSlot, "");
                target.RemoveLegend(Mentorship.BeingMentoredLegend);
                target.AddLegend($"Mentored by {_char.Name} ({Character.GameDate})", Mentorship.MentoredByLegend,
                                 Mentorship.LegendIcon, Mentorship.LegendColor);
            });

            SendMiniText($"This culminates your mentorship of {them}. Hopefully they have learned much from your teachings.");
            // The protégé's own line, entered on their monitor like the three mutations above (#29 rule 2,
            // Server/Session.State.cs — entering a peer's state happens under that peer's monitor). Its own
            // section rather than an extra statement inside the mutation block above, so the two lines keep
            // going out in the order they always did: the mentor's first, then the protégé's. Rule 1 holds —
            // this runs on the mentor's 0x3A read-loop thread (HandleNpcDialog completes the prompt's
            // TaskCompletionSource inline, so the continuation resumes under Handle's own WithState) with no
            // world lock, no viewport lock and no Lua gate: LuaMentor fires RunMentorAsync and returns at the
            // first prompt, so everything past that await is outside the gate the cast was under.
            target.WithState(() =>
                target.SendMiniText($"This culminates your mentorship under {_char.Name}. Hopefully you have learned much from their teachings."));
            Log.Info($"   -> MENTOR '{_char.Name}' culminated '{them}' (total {total})");
            return;
        }

        // ---- offer: everything that disqualifies a new protégé ----------------------------------
        if (mentored)
        { SendMiniText($"{them} has already been mentored!"); return; }
        if (mentorOf.Length > 0)
        { SendMiniText($"{them} is already being mentored by someone else!"); return; }
        if (level < Mentorship.MinProtegeLevel || level > Mentorship.MaxProtegeLevel)
        {
            SendMiniText($"{them} must be between the levels of {Mentorship.MinProtegeLevel} and " +
                         $"{Mentorship.MaxProtegeLevel} to accept a mentor.");
            return;
        }

        await DlgSayNpc(MarriageVirtualNpc, new[]
        {
            "Mentoring someone is a great way to show your knowledge of the lands and your support for those new to them.",
            $"You may begin mentoring someone as long as they have reached the {Mentorship.MinProtegeLevel}rd insight and have not exceeded the {Mentorship.MaxProtegeLevel}th insight.",
            "The proposed mentoree must also be free from another's mentorship.",
            $"After you have taught your mentoree much, you may culminate the mentorship when the mentoree has reached the {Mentorship.CulminateLevel}th insight.",
        });

        int offer = await DlgMenu(MarriageVirtualNpc,
            $"Are you sure you would like to offer mentorship to {them}?", new[] { "Yes", "No" });
        if (offer != 1) return;

        _ = target.PromptMentorship(this);
    }

    /// <summary>Runs on the PROTÉGÉ's own session (cross-session, same pattern as the marriage proposal):
    /// the accept/decline prompt, and the bind on accept.</summary>
    internal async Task PromptMentorship(Session mentor)
    {
        string who = mentor.Snapshot().Name;
        int accept = await DlgMenu(MarriageVirtualNpc,
            $"{who} would like to offer you mentorship. Do you accept?",
            new[] { "Yes! I need guidance.", "No, I must decline." });

        string me = _char.Name;
        if (accept != 1)
        {
            mentor.SendMiniText($"{me} regretably must decline.");
            return;
        }

        // Re-check on landing: both parties have been free to act while the prompt was open.
        var mentorship = Quest(Mentorship.Key);
        if (HasLegend(Mentorship.MentoredByLegend) || mentorship.GetText(Mentorship.MentorSlot).Length > 0)
        { mentor.SendMiniText($"{me} is already being mentored."); return; }

        mentorship.SetText(Mentorship.MentorSlot, who);
        AddLegend($"Being mentored by {who}", Mentorship.BeingMentoredLegend,
                  Mentorship.LegendIcon, Mentorship.LegendColor);
        mentor.SendMiniText($"{me} accepts your offer of mentorship! Please guide them until level " +
                            $"{Mentorship.CulminateLevel}, where you will need to cast this again to end the mentorship.");
        SendMiniText($"{who} is now your mentor.");
        Log.Info($"   -> MENTOR '{who}' took on '{me}'");
    }
}
