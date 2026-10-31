using System.Runtime.CompilerServices;
using MoonSharp.Interpreter;
using Shared;

namespace Server;

/// <summary>
/// What happened when a verb was asked to run. Shared by both wrappers (<see cref="ItemScript"/>,
/// <see cref="SpellScript"/>) so that four distinct outcomes cannot collapse into two on the way to a caller.
///
/// <para>That collapse is the bug this replaces. <see cref="LuaVerbHost.Invoke"/> returned <c>DynValue?</c>, and
/// <c>null</c> meant BOTH "no such verb" and "the verb raised". ItemScript passed the null up as "not handled",
/// <c>Session.ApplyItemEffect</c> read "not handled" as "use the item DB's Vita/Mana instead" and returned true,
/// and <c>HandleUseItem</c> consumed the item. One typo in a hot-reloaded <c>item_verbs.lua</c> therefore
/// destroyed consumables with no player-visible sign and a single Info line in the log (upstream #25).</para>
/// </summary>
public enum VerbResult
{
    /// <summary>The verb ran to completion and returned anything but an explicit <c>false</c> — a verb needn't
    /// bother with <c>return true</c>.</summary>
    Ok,
    /// <summary>The verb ran and returned <c>false</c>: a gate refused (ward already up, no mana, no target). The
    /// verb has already said whatever it wants to say to the player. The caller must not apply its own effect
    /// and must not fall through to another implementation.</summary>
    Declined,
    /// <summary>No script is loaded, or it defines no function by that name. The caller decides whether it has a
    /// C# path to fall back to; for spells that is the ordinary case (~600 spells have no row).</summary>
    Missing,
    /// <summary>The verb raised. It may have applied part of its effect before it did. Already logged with the Lua
    /// location and the stack; the caller must refuse the action, tell the player, and consume nothing.</summary>
    Errored,
}

/// <summary>
/// A reusable embedded-MoonSharp (pure-C# Lua) "verb" host: it owns ONE <c>.lua</c> file that defines a global
/// <c>verbs</c> table (<c>verbName -&gt; function(ctx, row)</c>) and runs those verbs against a C# facade object
/// (<c>ctx</c>) plus a data row (the "row" half of the verb/row model). Both spells (<see cref="SpellScript"/>)
/// and item use-effects (<see cref="ItemScript"/>) are just instances of this over different verb files and
/// facade types — one engine, no duplicated MoonSharp plumbing.
///
/// Safety: the script runs under MoonSharp's hard sandbox (no io/os/file access), and every verb call is wrapped
/// so a Lua error is logged — with the script location and the stack — and reported as
/// <see cref="VerbResult.Errored"/>, distinct from <see cref="VerbResult.Missing"/>, rather than crashing the
/// caller. A MoonSharp <see cref="Script"/> is not thread-safe, so every call is serialized under the shared
/// Lua gate (<c>Session.EnterScriptGate</c>) — one gate for all three script hosts, ranked outside the session
/// monitors so a verb that reaches a peer cannot deadlock against that peer's own read loop (#29). Fine either
/// way: casts/item-uses are low-frequency vs movement/IO.
///
/// The facade type(s) a host will pass as <c>ctx</c> must be registered with MoonSharp
/// (<c>UserData.RegisterType&lt;T&gt;()</c>) before the first <see cref="Invoke"/>; the static wrapper classes do
/// that in their static constructors.
/// </summary>
public sealed class LuaVerbHost
{
    internal sealed record PreparedReload(Script Script, Table Verbs);

    private readonly string _name;      // the verb file's name, for log messages
    private Script? _script;
    private Table? _verbs;
    // Cache the parsed Lua Table per row object, so a hot cast/use doesn't rebuild it + re-parse every CSV cell
    // each call (matters once hundreds of spells route through here). Keyed by the row's IDENTITY: Content
    // rebuilds every row object on @reload, so old entries fall out of this weak table naturally, and Load()
    // clears it anyway (the cached Tables belong to the OLD Script and can't be reused by the new one). Verbs
    // treat `row` as read-only, so sharing one Table across calls is safe.
    private readonly ConditionalWeakTable<object, Table> _rowCache = new();

    public LuaVerbHost(string name) => _name = name;

    /// <summary>(Re)load the verb script — ATOMICALLY. The new script is parsed into locals and only swapped in
    /// once it has compiled AND produced a `verbs` table; a broken file leaves the previously-loaded verbs
    /// running untouched and logs. Returns true if this host is live afterwards.
    /// <para><b>Why atomic matters:</b> this used to null the host first and parse second, so ONE typo in a
    /// hot-fixed .lua silently disabled EVERY verb in that file until the next good reload — the caster would
    /// keep casting, but through the C# fallback, whose behaviour has drifted (filch barks over-head from Lua
    /// and into the status box from C#). A hot-fix loop is exactly where typos happen, so the failure mode has
    /// to be "your edit didn't take" — visible in the log, nothing else changes — rather than "the whole file
    /// reverted to a different implementation".</para>
    /// <para>The row cache is keyed to the live <see cref="Script"/>, so it is dropped only when the script is
    /// actually replaced; a failed reload must keep it, since the old Tables still belong to the old Script.</para></summary>
    public bool Load(string? path)
    {
        using (Session.EnterScriptGate())
        {
            var (ok, prepared) = PrepareReload(path);
            if (prepared is not null) CommitReload(prepared);
            return ok;
        }
    }

    /// <summary>Compile a candidate without changing the live host. The answer has two halves, and they are
    /// different facts. <c>Prepared</c> is the candidate; null means this file did not take, because it is
    /// missing, does not compile, or defines no <c>verbs</c> table. <c>Live</c> is whether the host is live
    /// afterwards: a rejected or missing file keeps the current host, matching <see cref="Load"/>'s
    /// established fallback, so on a reload it is true whether or not the file took. A caller reporting
    /// rejections reads <c>Prepared</c>; reading <c>Live</c> is how a rejected reload went unreported (#113).</summary>
    internal (bool Live, PreparedReload? Prepared) PrepareReload(string? path)
    {
        using (Session.EnterScriptGate())
        {
            if (path is null || !File.Exists(path))
            {
                Log.Warn($"{_name}: no verb file at '{path ?? "(null)"}' — keeping {(_verbs is null ? "the Lua path disabled" : "the previously-loaded verbs")}");
                return (_verbs is not null, null);
            }
            try
            {
                var s = new Script(CoreModules.Preset_HardSandbox);
                s.DoString(File.ReadAllText(path), null, _name);
                var v = s.Globals.Get("verbs");
                if (v.Type != DataType.Table)
                {
                    Log.Warn($"{_name} defines no global `verbs` table — reload REJECTED, keeping the previous verbs");
                    return (_verbs is not null, null);
                }
                return (true, new PreparedReload(s, v.Table));
            }
            catch (Exception e)
            {
                // Warn, not Error: a content author's syntax error, handled (the old verbs keep running). The
                // exception still rides along — a SyntaxErrorException's DecoratedMessage is the file:line.
                Log.Warn($"{_name} load failed: {Describe(e)} — reload REJECTED, keeping the previous verbs", e);
                return (_verbs is not null, null);
            }
        }
    }

    /// <summary>Install a compiled candidate. The caller holds the shared Lua gate across all four host
    /// commits, so no invocation can observe only part of the content publication.</summary>
    internal void CommitReload(PreparedReload prepared)
    {
        _script = prepared.Script;
        _verbs = prepared.Verbs;
        _rowCache.Clear();   // cached Tables belong to the OLD Script — only safe to drop once it is replaced
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in prepared.Verbs.Pairs)
            if (pair.Key.Type == DataType.String && pair.Value.Type == DataType.Function) names.Add(pair.Key.String);
        _verbNames = names;
    }

    /// <summary>The names of the loaded verbs, published whole at each commit and never changed in place, so
    /// <see cref="HasVerbUngated"/> reads one load's set or the next's without the Lua gate.</summary>
    private volatile IReadOnlySet<string> _verbNames = new HashSet<string>();

    /// <summary>Is <paramref name="verb"/> a function in the loaded verb table, asked WITHOUT the Lua gate: the set the
    /// last commit published. For a caller that must not wait at the gate just to learn there is nothing to run, since a
    /// handler that waits there lets its session's monitor go (<c>Session.EnterScriptGate</c>'s slow path): the item-drop
    /// handler asks it on every drop (<c>Session.RunDropHooks</c>, #417's review, F1). A reload racing the read may answer
    /// for either load; <see cref="Invoke"/>, under the gate, has the last word (a verb gone by then is
    /// <see cref="VerbResult.Missing"/>).</summary>
    public bool HasVerbUngated(string verb) => _verbNames.Contains(verb);

    /// <summary>Is <paramref name="verb"/> a function in the loaded verb table?</summary>
    public bool HasVerb(string verb)
    {
        using (Session.EnterScriptGate())
        {
            return _verbs is not null && verb.Length > 0 && _verbs.Get(verb).Type == DataType.Function;
        }
    }

    /// <summary>Run <c>verb(ctx, row)</c>. See <see cref="VerbResult"/> for the four outcomes; an explicit Lua
    /// <c>return false</c> is the only thing that yields <see cref="VerbResult.Declined"/>. Empty CSV cells become
    /// nil, so a verb's <c>row.x or default</c> works; numeric-looking cells are passed as Lua numbers.</summary>
    public VerbResult Invoke(string verb, object ctx, IReadOnlyDictionary<string, string> row)
    {
        using (Session.EnterScriptGate())
        {
            if (_script is null || _verbs is null) return VerbResult.Missing;
            var fn = _verbs.Get(verb);
            if (fn.Type != DataType.Function) return VerbResult.Missing;

            if (!_rowCache.TryGetValue(row, out var rowTable))
            {
                rowTable = new Table(_script);
                foreach (var (k, val) in row)
                {
                    if (string.IsNullOrEmpty(val)) continue;   // empty cell -> nil, so `row.x or default` works
                    rowTable.Set(k, double.TryParse(val, out var d) ? DynValue.NewNumber(d) : DynValue.NewString(val));
                }
                _rowCache.Add(row, rowTable);
            }

            DynValue ret;
            try { ret = _script.Call(fn, UserData.Create(ctx), DynValue.NewTable(rowTable)); }
            catch (Exception e)
            {
                Log.Error($"{_name} verb '{verb}' raised: {Describe(e)}", e);
                return VerbResult.Errored;
            }
            return ret.Type == DataType.Boolean && !ret.Boolean ? VerbResult.Declined : VerbResult.Ok;
        }
    }

    /// <summary>MoonSharp's <c>DecoratedMessage</c> carries the chunk name and position —
    /// <c>item_verbs.lua:(212,8-31): attempt to index a nil value</c> — which is what the person fixing the
    /// script needs and what <c>e.Message</c> alone drops. Falls back to the plain message for anything that is
    /// not an interpreter exception (a facade method throwing from C#, say). Internal so the other two Lua
    /// hosts (MobScript, NpcScript) and the NPC dialog catches describe their errors the same way.</summary>
    internal static string Describe(Exception e) =>
        e is InterpreterException { DecoratedMessage.Length: > 0 } ie ? ie.DecoratedMessage : e.Message;
}
