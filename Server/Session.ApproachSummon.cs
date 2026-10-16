using Shared;

namespace Server;

/// <summary>
/// The Session half of the Approach / Summon binding (#313). <see cref="SpellContext"/> is the Lua-facing half;
/// these are the members it needs and cannot reach on its own, because what they wrap is private to
/// <see cref="Session"/>: the world's name lookup, the staff test, the cast's player target and, above all,
/// <see cref="EnterMap"/>, the move <c>@approach</c> and <c>@bring</c> already make.
///
/// <para><b>Nothing here is a new path.</b> Each member wraps one the server already runs, so a spell and the
/// GM command it mirrors cannot drift apart: the lookup is whisper's and the commands' (<c>FindPlayer</c>), the
/// target slot is the one <see cref="LuaResolvePcTarget"/> fills, and both moves are
/// <c>EnterMap(..., ArrivalPolicy.AdjacentFreeElseStack)</c> with the same arguments
/// <c>ApproachCmd</c> and <c>BringCmd</c> pass (Session.GmCommands.cs). The verbs in spell_verbs.lua own every
/// guard and every line the player reads; these do the engine work.</para>
///
/// <para><b>Locks.</b> A verb runs on the caster's packet thread, inside the caster's state monitor
/// (<c>Session.Handle</c> wraps <c>Dispatch</c> in <c>WithState</c>) and inside the Lua gate
/// (<c>SpellScript.Run</c> -&gt; <c>LuaVerbHost.Invoke</c>). Approach moves the CASTER: <c>EnterMap</c>'s own
/// <c>EnterState</c> is re-entrant there, as for Gateway and Return. Summon moves ANOTHER player:
/// <see cref="LuaSummonTarget"/> takes the target's monitor (row 2 of docs/common/Locking.md, a second session
/// monitor, which <c>EnterState</c> orders by <c>StateRank</c>), and <c>EnterMap</c> takes <c>World._lock</c>
/// (row 3) inside it and releases it before returning. That is <c>@bring</c>'s sequence, with the Lua gate
/// outermost, which is also where the poet Resurrect family already moves another player from a spell verb
/// (<see cref="LuaReviveTarget"/> -&gt; <see cref="ReviveAt"/> -&gt; <c>EnterMap</c>). Before the move, Summon
/// ends the target's open exchange itself, with <c>EnterMap</c>'s own <c>EndTrade</c> call (which takes the
/// partner's monitor, a third row-2 monitor, by rank like every pair), and checks again after each end: see
/// <see cref="LuaSummonTarget"/> for why. No new lock and no new cross-session write path.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>Resolve the online player whose name was TYPED into the spell's prompt as this cast's player
    /// target: the typed-answer twin of <see cref="LuaResolvePcTarget"/>, which resolves an aimed id or the
    /// faced peer. Any map, case-insensitive, a replaced session skipped: <c>FindPlayer</c>, the lookup whisper
    /// and the GM commands use. False on a blank name or nobody online by it, and silent then (the verb answers),
    /// with a log line, since "nothing happened" is the hardest report to diagnose.</summary>
    internal bool LuaResolveNamedPcTarget(SpellDef sp, string? name)
    {
        string typed = (name ?? "").Trim();
        _pcSpellTarget = typed.Length == 0 ? null : _world.Online.FindPlayer(typed);
        if (_pcSpellTarget is not null) return true;
        Log.Info($"      -x {sp.Name}: nobody online named '{typed}' — the verb answers");
        return false;
    }

    /// <summary>The player target this cast resolved, for <see cref="SpellContext"/>'s reads of their kingdom,
    /// map, level and base vita/mana. Bare field reads of another session, as <see cref="LuaTargetLevel"/> and
    /// the rest of the target getters already make.</summary>
    internal Session? LuaPcTarget => _pcSpellTarget;

    /// <summary>Is this session staff? The same test the command table's tier gate makes.</summary>
    internal bool LuaIsGm => IsGm;

    /// <summary>Approach's move: this caster to the first free cardinal tile beside the resolved target (N, E, S,
    /// W), else onto the target's own tile. <c>ApproachCmd</c>'s call, argument for argument: the target's map,
    /// its dimensions and tile, read whole by <see cref="PlaceForPeer"/>, as the command reads them.</summary>
    internal bool LuaApproachTarget(SpellDef sp)
    {
        if (_pcSpellTarget is not { } target) return false;
        var (map, xs, ys, tx, ty) = target.PlaceForPeer();
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        var (x, y) = EnterMap(map, xs, ys, tx, ty, mapName, ArrivalPolicy.AdjacentFreeElseStack);
        Log.Info($"      {sp.Name}(lua): '{_char.Name}' -> '{target._char.Name}' at map {map} ({x},{y})");
        return true;
    }

    /// <summary>Where THIS player stands, for another player's move to them (<c>@approach</c> and the Approach
    /// spell): the map, its dimensions and the tile, read in one section inside this player's monitor (#29), taken
    /// by <c>StateRank</c>. The mover's thread holds the mover's monitor (and, for the spell, the Lua gate) and never
    /// <c>World._lock</c>; <c>FindPlayer</c> releases it before it returns.
    /// <para>A warp writes the five as one critical section on this player (<see cref="EnterMap"/>: the map and its
    /// dimensions first, then the tile through <c>World.PlacePlayer</c>), and a walk writes the tile under the same
    /// monitor. Read bare, a mover could take the new map with the old tile, and land on that tile clamped onto the
    /// new map, where this player never stood, or the new tile on the old map. Read here, the mover lands beside where
    /// this player was at one moment, or where they are now.</para></summary>
    internal (ushort Map, ushort Xs, ushort Ys, ushort X, ushort Y) PlaceForPeer()
    {
        using var _ = EnterState();   // #29: cross-thread entry into this session's state
        return (_char.Map, _char.MapXs, _char.MapYs, _char.X, _char.Y);
    }

    /// <summary>Summon's move: the resolved target to the first free cardinal tile beside this caster, else onto
    /// the caster's own tile. <c>BringCmd</c>'s call, argument for argument, made under the target's own
    /// monitor, which <c>EnterMap</c> would take anyway (its own <c>EnterState</c> is then re-entrant).
    ///
    /// <para><b>Nothing may drop that monitor between the last check and the move</b> (PR #325 review, F1). A
    /// target who logged out after the name was looked up must stay off every map: their teardown sets
    /// <c>_leaving</c> under this monitor before it takes them off their map, and a move after that puts a departed
    /// session back on one, found by name and still writing its row. Two things can drop the monitor here:</para>
    /// <list type="bullet">
    /// <item><b>This acquisition.</b> When the target ranks below the caster, <c>EnterState</c> drops the caster's
    ///   monitor while it waits; the target is not held yet, so the first check below sees anything that ran.</item>
    /// <item><b>Ending an open exchange.</b> <c>EnterMap</c> does that first (<c>Session.Navigation.cs</c>, the
    ///   <c>EndTrade</c> before <c>LeaveMap</c>), through <c>WithStatePair</c>, which takes the lower-ranked side
    ///   first: with a partner ranked below the target it drops this monitor, and the caster's, while it waits for
    ///   the partner, and a whole logout fits in that gap. So the exchange is ended HERE, by the same call, and
    ///   both checks run again after every end, until the target is neither leaving nor trading. Then
    ///   <c>EnterMap</c>'s own exchange step has nothing to end, and nothing else in it gives up this monitor
    ///   before the target is on the new map.</item>
    /// </list>
    /// <para>An exchange that keeps re-opening is ended at most <see cref="SummonExchangeEndsMax"/> times; the next
    /// one refuses the move. Every refusal returns false and moves nobody, and the verb answers "Fizzle." at no
    /// cost. The caster's map and tile are read only after the last check, under both monitors again, since the
    /// caster's was dropped too. <c>EnterMap</c> itself still has the gap for <c>@bring</c> and the Resurrect
    /// family; #330 closes it there.</para></summary>
    internal bool LuaSummonTarget(SpellDef sp)
    {
        if (_pcSpellTarget is not { } target) return false;
        using var _ = target.EnterState();
        for (int ended = 0; ; ended++)
        {
            if (target._leaving)
            {
                Log.Info($"      {sp.Name}(lua): '{target._char.Name}' logged out before the move — nobody moved");
                return false;
            }
            if (target._trade is not { } open) break;
            if (ended == SummonExchangeEndsMax)
            {
                Log.Info($"      {sp.Name}(lua): '{target._char.Name}' had an exchange open again after {ended} " +
                         "were ended — nobody moved");
                return false;
            }
            EndTrade(open, "Exchange cancelled.");   // EnterMap's own call; may drop and retake this monitor
            SummonExchangeEndedProbeForTest?.Invoke(target, ended + 1);   // null except under test
        }

        ushort map = _char.Map, xs = _char.MapXs, ys = _char.MapYs;
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        var (x, y) = target.EnterMap(map, xs, ys, _char.X, _char.Y, mapName, ArrivalPolicy.AdjacentFreeElseStack);
        Log.Info($"      {sp.Name}(lua): '{target._char.Name}' -> '{_char.Name}' at map {map} ({x},{y})");
        return true;
    }

    /// <summary>How many of the target's exchanges one Summon ends before it gives up on the move. One is the
    /// ordinary case (the exchange that was open when the cast arrived); each further one is an exchange that
    /// opened while ending the last dropped the target's monitor.</summary>
    internal const int SummonExchangeEndsMax = 3;

    /// <summary>Test seam: called on the caster's thread with the target and the running count each time
    /// <see cref="LuaSummonTarget"/> has ended one of the target's exchanges, with both monitors held again. A fact
    /// re-opens an exchange here to stand in for one that opened while the end had dropped the target's monitor,
    /// which pins the bound at <see cref="SummonExchangeEndsMax"/>. Null outside the test host.</summary>
    internal static Action<Session, int>? SummonExchangeEndedProbeForTest;
}
