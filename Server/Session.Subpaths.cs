using Shared;

namespace Server;

/// <summary>
/// The PC-subpath foundation: who may join one of the twelve (Barbarian … Muse), who admits them, leaving, and
/// the subpath's elders. One player-tier command, <c>@subpath</c>, because two of its three kinds of user are
/// ordinary players: a member leaving, and an elder admitting.
///
/// <para><b>Why a grant and not an NPC.</b> In 2001 these were "Player Run Subpaths" (tswolf.com/spells/pc.shtml,
/// Wayback capture 2001-07-24): members ran the trials and admitted the newcomers themselves. RTK scripts an NPC
/// trial quest for only one of the twelve (Barbarian's Ghengis Khan), so eleven would have had to be written
/// with no source at all. The 2026-10-24 decision sheet settled it (item 12): a GM, or an elder the GM has
/// appointed, admits a member with a command, and the trials are played out by people.</para>
///
/// <para><b>What joining is.</b> RTK's leaders end every joining with <c>updatePath(path, player.mark)</c>
/// (ghengis_khan.lua, baek.lua), which is <see cref="SetCharClass"/>: the path changes, the rank stays. The
/// title, the user-list badge, the chat label and the gear gate all follow from <c>Character.ClassName</c> and
/// Paths.csv, so nothing else has to be told. The subpath's own spells do not come with it: no trainer teaches
/// them yet (<see cref="Content.IsPcSubpathSpell"/>), and testers use <c>@spell</c>.</para>
///
/// <para><b>What leaving is.</b> RTK's one way back to a base class, the Dog's cleanse (dog_linguist.lua), takes
/// the subpath's spells out of the book and then calls <c>updatePath(player.baseClass, player.mark)</c>. Leaving
/// does the same with the PC subpath's own spells. Worn subpath gear stays on, as RTK's <c>updatePath</c> leaves
/// it (sl.c pcl_updatePath touches only the class and the mark).</para>
///
/// <para><b>Elders.</b> Kept in the flat quest registry under <see cref="SubpathElderReg"/>, which the character
/// already saves (no new column), holding the PATH id the character is an elder of. It counts only while that is
/// still the character's path, so a stale value can never make someone an elder of a subpath they are not in;
/// joining and leaving both clear it.</para>
///
/// <para><b>Locks.</b> Nothing new. Every write to ANOTHER player — the admission, the expulsion, the elder
/// mark — happens inside that player's own monitor (<see cref="WithState(Action)"/>, #29 rule 2), the shape
/// <c>@carnage</c> and <c>@bring</c> already use, and the operator's own reply goes out after it.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>Registry key: the path id of the PC subpath this character is an elder of; 0 (or absent) for
    /// none. Written only by <c>@subpath elder</c>, and cleared by joining and leaving.</summary>
    internal const string SubpathElderReg = "subpath_elder";

    /// <summary>Is this character an elder of the PC subpath they are in now?</summary>
    internal bool IsPcSubpathElder
    {
        get
        {
            int path = CharClassId;
            return Content.IsPcSubpath(path) && Quest(QuestState.Registry).Get(SubpathElderReg) == path;
        }
    }

    /// <summary>"@subpath [admit &lt;name&gt; [subpath] | leave | expel &lt;name&gt; | elder &lt;name&gt; [0|1]]".
    /// Bare, it reads back the caller's own standing.</summary>
    private void SubpathCmd(CommandArgs a)
    {
        if (a.None)           { SubpathStanding(a); return; }
        if (a.Is(0, "admit")) { SubpathAdmit(a);    return; }
        if (a.Is(0, "leave")) { SubpathLeave(a);    return; }
        if (a.Is(0, "expel")) { SubpathExpel(a);    return; }
        if (a.Is(0, "elder")) { SubpathElder(a);    return; }
        Refuse(a.Usage());
    }

    private void SubpathStanding(CommandArgs a)
    {
        int path = CharClassId;
        Reply(Content.IsPcSubpath(path)
            ? $"{ClassTitle}: member of the {Content.PathName(path)} subpath." +
              (IsPcSubpathElder ? " You are one of its elders." : "")
            : $"{ClassTitle}: not in a PC subpath.");
        Reply(a.Usage());
    }

    // "@subpath admit <name> [subpath]". A GM names the subpath; an elder admits to their own, and may name it
    // only if it IS their own. The subpath, when given, is the LAST word, so a name may contain spaces; with a
    // single word after "admit", that word is always the name.
    private void SubpathAdmit(CommandArgs a)
    {
        int path = 0, nameEnd = a.Count;
        if (a.Count >= 3 && Content.PathIdForClass(a.Word(a.Count - 1)) is var named && Content.IsPcSubpath(named))
        {
            path = named;
            nameEnd = a.Count - 1;
        }
        string name = a.Rest(1, nameEnd);
        if (name.Length == 0) { Refuse(a.Usage()); return; }

        if (!IsGm)
        {
            int own = CharClassId;
            if (!IsPcSubpathElder) { Refuse("Only a GM or one of a subpath's elders may admit a member."); return; }
            if (path != 0 && path != own) { Refuse($"You may admit members to the {Content.PathName(own)} subpath only."); return; }
            path = own;
        }
        else if (path == 0)
        {
            Refuse($"Name the subpath: {Prefix}subpath admit {name} <subpath>.");
            return;
        }

        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;

        // Read, check, write and tell: one critical section on the TARGET (#29 rule 2). Their class, level and
        // book are their state, read from our thread; the reply to us goes out after the section.
        string? refusal = null;
        string them = "";
        target.WithState(() =>
        {
            them = target._char.Name;
            refusal = target.PcSubpathAdmitRefusal(path);
            if (refusal is not null) return;
            target.JoinPcSubpath(path);
            // RTK's own line for a path change made by someone else (Tools/Gm_Click/click/gm_click.lua:
            // `target:sendMinitext("Path changed to " .. target.classNameMark .. "")`). Addressed to the TARGET,
            // so it is not a Reply.
            target.SendMiniText($"Path changed to {target.ClassTitle}");
        });
        if (refusal is not null) { Refuse(refusal); return; }

        Reply($"{them} is now a member of the {Content.PathName(path)} subpath.");
        Log.Info($"   -> @subpath admit '{_char.Name}'{(IsGm ? " (GM)" : " (elder)")} -> '{them}' into {Content.PathName(path)}");
    }

    /// <summary>Why this character may not be admitted to PC subpath <paramref name="path"/>, or null when they
    /// may. Called inside this character's own monitor.
    /// <list type="bullet">
    /// <item>The right base class, and ON it: a subpath is joined from its base class, never from another
    /// subpath (an NPC subpath's member goes back to the base class first, as RTK's Dog offers).</item>
    /// <item><see cref="Content.PcSubpathJoinLevel"/> or higher.</item>
    /// <item>No Dog secrets in the book: "These spells are for people not in PC Subpaths" (tswolf, the Dog
    /// spells page, 2001), which is <see cref="Content.CanLearnDogSpells"/>; RTK sends a linguist who wants a
    /// player subpath to their Dog to give them back first ("I wish to join a player subpath.",
    /// dog_linguist.lua), and our Dog's "cleanse" is that undo.</item>
    /// </list></summary>
    private string? PcSubpathAdmitRefusal(int path)
    {
        int current = CharClassId;
        int basePath = Content.PathBaseOf(path);
        string name = _char.Name, subpath = Content.PathName(path);

        if (current == path) return $"{name} is already a member of the {subpath} subpath.";
        if (current < 0 || Content.PathBaseOf(current) != basePath)
            return $"The {subpath} subpath admits {Content.PathName(basePath)}s only; {name} is on the " +
                   $"{(current < 0 ? _char.ClassName : Content.PathName(current))} path.";
        if (current != basePath)
            return $"{name} is on the {Content.PathName(current)} path, and must leave it first.";
        if (_char.Level < Content.PcSubpathJoinLevel)
            return $"{name} is level {_char.Level}; the {subpath} subpath admits from level {Content.PcSubpathJoinLevel}.";
        if (Content.DogSpellsFor(basePath, Content.MarkSpellLevel).Any(sp => KnowsSpellId(sp.Id)))
            return $"{name} holds Dog secrets, which no PC subpath may keep; their Dog must take them back first.";
        return null;
    }

    /// <summary>Join PC subpath <paramref name="path"/>: RTK <c>updatePath(path, mark)</c>, and a fresh member is
    /// nobody's elder. Called inside this character's own monitor.</summary>
    private void JoinPcSubpath(int path)
    {
        ClearSubpathElder();
        SetCharClass(path);
    }

    /// <summary>Leave the PC subpath this character is in: its own spells out of the book, then back to the base
    /// path with the rank kept — RTK's leave (the Dog's cleanse, dog_linguist.lua: <c>removeSpell</c> for each,
    /// then <c>updatePath(player.baseClass, player.mark)</c>). Returns how many spells were taken. Called inside
    /// this character's own monitor; the caller has checked that they ARE in a PC subpath.</summary>
    private int LeavePcSubpath()
    {
        int path = CharClassId;
        int old = _char.Spells.Count;
        int taken = _char.Spells.RemoveAll(id => Content.SpellById(id) is { } sp && sp.PathId == path);
        if (taken > 0) RedrawSpellbook(old);
        ClearSubpathElder();
        SetCharClass(Content.PathBaseOf(path));   // saves, the book with it
        return taken;
    }

    // Written only when set, so a character who was never an elder does not gain a zero-valued key.
    private void ClearSubpathElder()
    {
        var registry = Quest(QuestState.Registry);
        if (registry.Get(SubpathElderReg) != 0) registry.Set(SubpathElderReg, 0);
    }

    // "@subpath leave" — the caller leaves their own PC subpath.
    private void SubpathLeave(CommandArgs a)
    {
        if (a.Count > 1) { Refuse(a.Usage()); return; }
        int path = CharClassId;
        if (!Content.IsPcSubpath(path)) { Refuse("You are not in a PC subpath."); return; }

        int taken = LeavePcSubpath();
        Reply($"You have left the {Content.PathName(path)} subpath and are on the {Content.PathName(CharClassId)} " +
              $"path again" + (taken > 0 ? $"; {taken} of its secrets are forgotten." : "."));
        Log.Info($"   -> @subpath leave '{_char.Name}' from {Content.PathName(path)} ({taken} spell(s) taken)");
    }

    // "@subpath expel <name>" — a GM removes any PC subpath's member; an elder, a member of their own.
    private void SubpathExpel(CommandArgs a)
    {
        string name = a.Rest(1);
        if (name.Length == 0) { Refuse(a.Usage()); return; }
        bool gm = IsGm;
        int own = CharClassId;
        if (!gm && !IsPcSubpathElder) { Refuse("Only a GM or one of a subpath's elders may expel a member."); return; }

        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;

        string? refusal = null;
        string them = "";
        int path = 0, taken = 0;
        target.WithState(() =>
        {
            them = target._char.Name;
            path = target.CharClassId;
            if (!Content.IsPcSubpath(path)) { refusal = $"{them} is not in a PC subpath."; return; }
            if (!gm && path != own) { refusal = $"{them} is not of the {Content.PathName(own)} subpath."; return; }
            taken = target.LeavePcSubpath();
            target.SendMiniText($"Path changed to {target.ClassTitle}");   // RTK's line, as on admission
        });
        if (refusal is not null) { Refuse(refusal); return; }

        Reply($"{them} is no longer a member of the {Content.PathName(path)} subpath" +
              (taken > 0 ? $"; {taken} of its secrets were taken." : "."));
        Log.Info($"   -> @subpath expel '{_char.Name}'{(gm ? " (GM)" : " (elder)")} -> '{them}' from " +
                 $"{Content.PathName(path)} ({taken} spell(s) taken)");
    }

    // "@subpath elder <name> [0|1]" — a GM appoints (1, the default) or removes (0) an elder of the PC subpath
    // the named member is in. There may be several; nothing limits the count.
    private void SubpathElder(CommandArgs a)
    {
        if (!IsGm) { Refuse("Only a GM may appoint or remove an elder."); return; }

        bool appoint = true;
        string name = a.Rest(1);
        if (a.Count >= 3 && a.Int(a.Count - 1, out var flag) && flag is 0 or 1)
        {
            appoint = flag == 1;
            name = a.Rest(1, a.Count - 1);
        }
        if (name.Length == 0) { Refuse(a.Usage()); return; }

        var target = ResolveOnlinePlayer(name, $"'{name}' isn't online.", RefuseChannel.CommandReply);
        if (target is null) return;

        string? refusal = null;
        string them = "";
        int path = 0;
        target.WithState(() =>
        {
            them = target._char.Name;
            path = target.CharClassId;
            if (!Content.IsPcSubpath(path)) { refusal = $"{them} is not in a PC subpath."; return; }
            if (appoint) target.Quest(QuestState.Registry).Set(SubpathElderReg, path);
            else target.ClearSubpathElder();
        });
        if (refusal is not null) { Refuse(refusal); return; }

        Reply(appoint
            ? $"{them} is now an elder of the {Content.PathName(path)} subpath."
            : $"{them} is no longer an elder of the {Content.PathName(path)} subpath.");
        Log.Info($"   -> @subpath elder '{_char.Name}' -> '{them}' {(appoint ? "appointed" : "removed")} " +
                 $"({Content.PathName(path)})");
    }
}
