using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// The spell half of the data-driven verb/row model: <c>game-data/spell_verbs.lua</c> defines the verbs,
/// <c>SpellParams.csv</c> supplies each spell's row, and <see cref="SpellContext"/> is the facade a verb acts
/// through. A thin static wrapper over a shared <see cref="LuaVerbHost"/> (the actual MoonSharp engine); both
/// the CSV and this script hot-reload on <c>@reload</c> (see <see cref="Content.Load"/>). See
/// <see cref="Session.ApplyCast"/> for the additive dispatch: a spell with no row and no verb falls through to
/// the C# <c>CastX</c> path unchanged. A verb that ERRORS does not fall through — see <see cref="Run"/>.
/// </summary>
public static class SpellScript
{
    private static readonly LuaVerbHost _host = new("spell_verbs.lua");

    static SpellScript() => UserData.RegisterType<SpellContext>();

    public static bool Load(string? path) => _host.Load(path);

    internal static (bool Live, LuaVerbHost.PreparedReload? Prepared) PrepareReload(string? path) =>
        _host.PrepareReload(path);

    internal static void CommitReload(LuaVerbHost.PreparedReload prepared) => _host.CommitReload(prepared);

    public static bool HasVerb(string verb) => _host.HasVerb(verb);

    /// <summary><see cref="HasVerb"/> without the Lua gate (<see cref="LuaVerbHost.HasVerbUngated"/>).</summary>
    public static bool HasVerbUngated(string verb) => _host.HasVerbUngated(verb);

    /// <summary>The empty row an archetype-style call runs against: those verbs take their numbers off
    /// <c>ctx</c> rather than a CSV row. A single stable instance keeps LuaVerbHost's per-row Table cache warm
    /// (it is keyed by row identity).</summary>
    public static readonly IReadOnlyDictionary<string, string> NoRow = new Dictionary<string, string>();

    /// <summary>Run a spell verb. ONE entry point for both bindings — a verb reached by its SpellParams row
    /// (<paramref name="row"/> carries the numbers) and one reached from a C# dispatch site (pass
    /// <see cref="NoRow"/>; the verb reads <c>ctx</c> instead). The verbs themselves already straddle both, e.g.
    /// <c>local cost = row.mana or ctx.spellMana</c>, so the split only ever lived in these wrappers.
    ///
    /// <para>Tri-state result:</para>
    /// <list type="bullet">
    /// <item><b>null</b> — no such verb. Lua isn't handling this spell; the caller falls through to its C#
    ///   dispatch. This is the ordinary case for the ~600 spells with no row.</item>
    /// <item><b>true</b> — the cast succeeded. (A verb that returns no boolean at all counts as success, so a
    ///   verb needn't bother with <c>return true</c>.)</item>
    /// <item><b>false</b> — the verb ran and DECLINED (no mana / no target / blocked) and has already sent its
    ///   own notice. The caller must return this straight up: no central "You cast X.", and NO fallthrough.</item>
    /// </list>
    ///
    /// <para><b>A Lua error counts as false, never null.</b> The two wrappers this replaces disagreed about
    /// exactly that — the archetype one returned false ("failed cast, don't re-run it") while the row one
    /// returned null, which quietly re-ran the spell through a C# handler whose behaviour has since drifted
    /// (filch barks over-head from Lua and into the status box from C#). Silently substituting a different
    /// implementation is the worst of the three options: the player sees the spell misbehave, the log says
    /// nothing about a fallback, and a verb that errored *after* spending mana gets its effect applied twice.
    /// A failed cast that says so is strictly better, and the error is already logged by
    /// <see cref="LuaVerbHost.Invoke"/>.</para>
    ///
    /// <para>The host reports a four-way <see cref="VerbResult"/>. The tri-state return stays — the dispatch
    /// sites in Session.Spells.cs act on exactly it, and for a spell both Declined and Errored mean "ran, do
    /// not re-run through C#" — but the two are NOT interchangeable on the way out, and treating them as
    /// though they were is what this fixes. A DECLINED verb has already told the player why (that is its
    /// contract). An ERRORED verb has told them nothing: it raised, possibly before reaching any of its own
    /// notices. Folding both to false therefore left a broken spell completely silent — no effect, no
    /// message, nothing in the client at all — which reads to a player as the keypress being dropped. So the
    /// refusal is sent HERE, on the Errored path only, before the fold.</para>
    ///
    /// <para>Mana: the engine charges none on this path, and there is nothing to refund. A verb spends mana
    /// by calling <c>ctx:spendMana</c> itself, so a verb that raised BEFORE that call has cost nothing, and
    /// one that raised after has already spent it — the same half-applied state an errored item verb leaves
    /// (see <see cref="ItemScript.Apply"/>), and not something this layer can unwind.</para></summary>
    public static bool? Run(string verb, SpellContext ctx, IReadOnlyDictionary<string, string>? row = null)
    {
        switch (_host.Invoke(verb, ctx, row ?? NoRow))
        {
            case VerbResult.Missing: return null;    // not a Lua spell -> C# dispatch
            case VerbResult.Ok:      return true;
            case VerbResult.Declined: return false;  // the verb ran and said its own piece
            default:
                // Errored: already logged with the Lua location and the stack by LuaVerbHost.Invoke.
                ctx.Refuse($"{ctx.spellName} isn't working right now.");
                return false;                        // failed cast — no fallthrough, no central "You cast X."
        }
    }
}
