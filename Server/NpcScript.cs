using MoonSharp.Interpreter;
using Shared;

namespace Server;

/// <summary>
/// Hosts data-driven NPC dialog scripts in Lua (<c>game-data/npc_dialog.lua</c>) — the async cousin of the
/// spell/item verb system. Where a spell verb is a one-shot function, an NPC conversation SUSPENDS on every
/// prompt (menu / say / input) waiting for the player's 0x3A reply, so a script can't be a plain call. Instead
/// each migrated NPC is a Lua <b>coroutine</b>: the script calls <c>ctx:say(...)</c> / <c>ctx:menu(...)</c>,
/// which <c>coroutine.yield</c> a request table; this C# driver awaits the matching <see cref="NpcContext"/>
/// dialog Task (the SAME primitive the C# abilities use) and <c>Resume</c>s the coroutine with the reply. So a
/// script reads as linear code — <c>local c = ctx:menu(...); if c == 1 then ... end</c> — exactly like the C#
/// abilities, but hot-reloadable.
///
/// The Lua file defines a global <c>npcs</c> table (<c>npcKey -&gt; function(ctx)</c>) plus a <c>__make_ctx</c>
/// factory (the ctx API — every method is a thin yield; see the file). A missing/broken file just disables the
/// Lua NPC path (<see cref="Has"/> returns false), so <see cref="Session.RunNpcAsync"/> falls through to the C#
/// abilities — a broken script can never take NPCs offline.
///
/// Concurrency: a MoonSharp <see cref="Script"/> isn't thread-safe and a conversation spans many awaits, so we
/// can't hold a lock across one. Instead each individual <c>Resume</c>/<c>Call</c> takes the shared Lua gate
/// (<c>Session.EnterScriptGate</c>, the same one the verb hosts use — see #29 for why it is one gate and why it
/// ranks outside the session monitors); they're fast and synchronous, and the awaits happen outside it. Two
/// players' conversations therefore interleave their coroutine steps safely — never resumed simultaneously —
/// which is how a single Lua VM is meant to be driven.
/// </summary>
public static class NpcScript
{
    internal sealed record PreparedReload(Script Script, Table Npcs, Table? NpcsSay);

    private static Script? _script;
    private static Table? _npcs;       // npcs[key]      = function(ctx)         -- click dialog
    private static Table? _npcsSay;    // npcs_say[key]  = function(ctx, speech)  -- spoken trigger, returns consumed?

    /// <summary>(Re)load the NPC dialog script — ATOMICALLY, for the same reason as
    /// <see cref="LuaVerbHost.Load"/>: the new script only replaces the running one once it has compiled and
    /// produced both `npcs` and `__make_ctx`. A broken edit leaves the previous dialogs working and logs.
    /// <para>This matters MORE here than for the verb hosts, because Lua NPC dialog has no C# fallback at all —
    /// nulling first meant one typo in npc_dialog.lua struck every scripted NPC in the world mute until the
    /// next good reload. Returns true if the Lua NPC path is live afterwards.</para></summary>
    public static bool Load(string? path)
    {
        using (Session.EnterScriptGate())
        {
            var (ok, prepared) = PrepareReload(path);
            if (prepared is not null) CommitReload(prepared);
            return ok;
        }
    }

    /// <summary>Compile candidate dialogs without replacing the live Lua state. <c>Prepared</c> is null when this
    /// file did not take; <c>Live</c> is whether the Lua NPC path is live afterwards, which stays true on a
    /// rejected reload because the previous dialogs keep running. See <see cref="LuaVerbHost.PrepareReload"/>.</summary>
    internal static (bool Live, PreparedReload? Prepared) PrepareReload(string? path)
    {
        using (Session.EnterScriptGate())
        {
            if (path is null || !File.Exists(path))
            {
                Log.Warn($"npc_dialog.lua: no file at '{path ?? "(null)"}' — keeping {(_npcs is null ? "the Lua NPC path disabled" : "the previously-loaded dialogs")}");
                return (_npcs is not null, null);
            }
            try
            {
                // Hard sandbox (no io/os) PLUS the coroutine module — the whole dialog model is Lua coroutines
                // (ctx:say/menu/input yield); Preset_HardSandbox omits `coroutine`, so add it back explicitly.
                var s = new Script(CoreModules.Preset_HardSandbox | CoreModules.Coroutine);
                s.DoString(File.ReadAllText(path), null, "npc_dialog");
                var n = s.Globals.Get("npcs");
                if (n.Type == DataType.Table && s.Globals.Get("__make_ctx").Type == DataType.Function)
                {
                    var sy = s.Globals.Get("npcs_say");            // optional — speech-trigger handlers
                    return (true, new PreparedReload(s, n.Table, sy.Type == DataType.Table ? sy.Table : null));
                }
                Log.Warn("npc_dialog.lua missing global `npcs` table or `__make_ctx` — reload REJECTED, keeping the previous dialogs");
                return (_npcs is not null, null);
            }
            catch (Exception e)
            {
                Log.Warn($"npc_dialog.lua load failed: {LuaVerbHost.Describe(e)} — reload REJECTED, keeping the previous dialogs", e);
                return (_npcs is not null, null);
            }
        }
    }

    /// <summary>Install candidate dialogs while the caller holds the shared Lua gate.</summary>
    internal static void CommitReload(PreparedReload prepared)
    {
        _script = prepared.Script;
        _npcs = prepared.Npcs;
        _npcsSay = prepared.NpcsSay;
    }

    /// <summary>Is there a Lua CLICK dialog script for this NPC identifier?</summary>
    public static bool Has(string npcKey)
    {
        using (Session.EnterScriptGate())
        {
            return _npcs is not null && npcKey.Length > 0 && _npcs.Get(npcKey).Type == DataType.Function;
        }
    }

    /// <summary>Is there a Lua SPEECH-trigger script (npcs_say) for this NPC identifier?</summary>
    public static bool HasSay(string npcKey)
    {
        using (Session.EnterScriptGate())
        {
            return _npcsSay is not null && npcKey.Length > 0 && _npcsSay.Get(npcKey).Type == DataType.Function;
        }
    }

    /// <summary>Drive one Lua NPC conversation to completion. Creates the coroutine, then loops: resume it
    /// (locked), await the yielded dialog op via <paramref name="ctx"/> (unlocked), resume with the reply. A Lua
    /// error aborts just this conversation (logged); it can't crash the session (the caller wraps this in
    /// try/catch too).</summary>
    public static Task RunAsync(NpcContext ctx, string npcKey)
    {
        DynValue coro, ctxTable;
        using (Session.EnterScriptGate())
        {
            if (_script is null || _npcs is null || _npcs.Get(npcKey).Type != DataType.Function) return Task.CompletedTask;
            ctxTable = _script.Call(_script.Globals.Get("__make_ctx"));
            coro = _script.CreateCoroutine(_npcs.Get(npcKey));
        }
        return Drive(ctx, coro, new[] { ctxTable });
    }

    /// <summary>Run a Lua SPEECH handler <c>npcs_say[key](ctx, speech)</c> to completion. Returns true if the
    /// script CONSUMED the speech (explicit Lua <c>return true</c>) — the caller then stops dispatching; false
    /// (or no return) lets other NPCs / the C# say-handlers have a go.</summary>
    public static async Task<bool> RunSayAsync(NpcContext ctx, string npcKey, string speech)
    {
        DynValue coro, ctxTable;
        using (Session.EnterScriptGate())
        {
            if (_script is null || _npcsSay is null || _npcsSay.Get(npcKey).Type != DataType.Function) return false;
            ctxTable = _script.Call(_script.Globals.Get("__make_ctx"));
            coro = _script.CreateCoroutine(_npcsSay.Get(npcKey));
        }
        var ret = await Drive(ctx, coro, new[] { ctxTable, DynValue.NewString(speech) });
        return ret.Type == DataType.Boolean && ret.Boolean;
    }

    // Drive a coroutine to completion: resume (locked), await the yielded dialog op (unlocked), repeat. Returns
    // the coroutine's final return DynValue. Shared by the click and speech paths.
    private static async Task<DynValue> Drive(NpcContext ctx, DynValue coro, DynValue[] firstArgs)
    {
        DynValue yielded;
        using (Session.EnterScriptGate()) { yielded = coro.Coroutine.Resume(firstArgs); }
        while (coro.Coroutine.State == CoroutineState.Suspended)
        {
            var reply = await Dispatch(ctx, yielded);
            using (Session.EnterScriptGate()) { yielded = coro.Coroutine.Resume(reply); }
        }
        return yielded;
    }

    // Map one yielded request table {op=..., ...} to the real NpcContext primitive. Suspending ops (say/menu/
    // input) await the dialog Task; everything else runs immediately and resumes with the result. Add a case
    // here (and its yield stub in __make_ctx) to expose a new primitive to scripts.
    private static async Task<DynValue> Dispatch(NpcContext ctx, DynValue req)
    {
        if (req.Type != DataType.Table) return DynValue.Nil;
        var t = req.Table;
        string op = Str(t, "op");
        switch (op)
        {
            // ---- suspending (await the player's reply) ----
            case "say":     await ctx.Say(Arr(t, "pages"));                       return DynValue.Nil;
            case "sayItem": await ctx.SayItem(Str(t, "item"), Arr(t, "pages"));   return DynValue.Nil;
            case "sayLook": await ctx.SayLook(Int(t, "look"), Int(t, "color"), Arr(t, "pages")); return DynValue.Nil;
            case "menu":    return DynValue.NewNumber(await ctx.Menu(Str(t, "prompt"), Arr(t, "options")));
            case "input":   { var s = await ctx.Input(Str(t, "prompt")); return s is null ? DynValue.Nil : DynValue.NewString(s); }
            // The native shop grids, so a scripted NPC can offer Buy/Sell alongside its own menu entries
            // instead of having to be a plain flag-driven ShopAbility. Both run the SAME flows the C# ability
            // does: buy reads this NPC's catalogue (Shops.For -> ShopStock/ShopCatalogues, so an NPC with no
            // stock politely says it has nothing — which is what Yon/Laptev's verified-empty "Buy" wants),
            // sell offers the player's droppable, sellable pack, independent of the catalogue.
            case "buy":     await ctx.Buy();                                      return DynValue.Nil;
            case "sell":    await ctx.Sell();                                     return DynValue.Nil;

            // ---- immediate (no wait) ----
            case "bubble":     ctx.Bubble(Str(t, "text"));                       return DynValue.Nil;
            case "notify":     ctx.Notify(Str(t, "text"));                       return DynValue.Nil;
            case "giveItem":   return DynValue.NewBoolean(ctx.GiveItem(Str(t, "key"), Int(t, "n", 1)));
            case "takeItem":   return DynValue.NewBoolean(ctx.TakeItem(Str(t, "key"), Int(t, "n", 1)));
            case "hasItem":    return DynValue.NewBoolean(ctx.HasItem(Str(t, "key"), Int(t, "n", 1)));
            case "countItem":  return DynValue.NewNumber(ctx.CountItem(Str(t, "key")));
            case "itemName":   return DynValue.NewString(ctx.ItemName(Str(t, "key")));
            // Teach a spell by Spells.csv key. False on an unknown key OR a full spell book, and the
            // script is expected to check — a silently-unlearned reward reads to the player as the quest
            // having eaten their items.
            case "learnSpell":
            {
                var sp = Content.SpellByKey(Str(t, "key"));
                return DynValue.NewBoolean(sp is not null && ctx.LearnSpell(sp));
            }
            case "hasSpell":    return DynValue.NewBoolean(ctx.KnowsSpell(Str(t, "key")));
            case "forgetSpell": ctx.ForgetSpellByKey(Str(t, "key"));              return DynValue.Nil;
            // The "Forget Secret" picker itself (pick from your spell book, then forget it). Suspending —
            // it runs a menu of its own. Shared with the class trainers' menu entry so the wording and the
            // "you know no secrets" case can only ever live in one place.
            case "forgetSecret": await ForgetSecretAbility.Forget(ctx);           return DynValue.Nil;
            // `totem` opts this grant into the +5% totem-time bonus. Off by default: quest/tutorial rewards
            // normally don't take it (see Session.AwardExp). The Old dog's Restore reward DOES — its atlas
            // page states both figures, "50,000,000 experience (52,500,000 at totem time)".
            case "awardExp":   ctx.AwardExp((uint)Math.Max(0, Int(t, "n")), t.Get("totem").CastToBool()); return DynValue.Nil;
            case "awardGold":  ctx.AwardGold((uint)Math.Max(0, Int(t, "n")));    return DynValue.Nil;
            // The four quest verbs name SAVED keys (RTK's flat registry), so all four read QuestState.Registry:
            // stage and reg are the same map read, as they always were.
            case "stage":      return DynValue.NewNumber(ctx.Quest(QuestState.Registry).Get(Str(t, "key")));
            case "setStage":   ctx.Quest(QuestState.Registry).Set(Str(t, "key"), Int(t, "n")); return DynValue.Nil;
            case "reg":        return DynValue.NewNumber(ctx.Quest(QuestState.Registry).Get(Str(t, "key")));
            case "setReg":     ctx.Quest(QuestState.Registry).Set(Str(t, "key"), Int(t, "n")); return DynValue.Nil;
            // Karma (Server/Karma.cs). addKarma/removeKarma take FRACTIONS — RTK hands out 0.1 and 0.25 as
            // readily as whole points. karmaCheck takes a tier NAME ("rabbit", "ox", "tiger"), which is the
            // only form RTK's gates are ever written in.
            case "karma":       return DynValue.NewNumber(ctx.KarmaValue);
            case "karmaLevel":  return DynValue.NewString(ctx.KarmaLevel());
            case "karmaCheck":  return DynValue.NewBoolean(ctx.KarmaCheck(Str(t, "tier")));
            case "addKarma":    ctx.AddKarma(Num(t, "n"));                       return DynValue.Nil;
            case "removeKarma": ctx.RemoveKarma(Num(t, "n"));                    return DynValue.Nil;
            case "karmaTooLow": return DynValue.NewBoolean(ctx.KarmaTooLow());
            case "hasLegend":  return DynValue.NewBoolean(ctx.HasLegend(Str(t, "name")));
            case "addLegend":  ctx.AddLegend(Str(t, "text"), Str(t, "name"), (byte)Int(t, "icon"), (byte)Int(t, "color")); return DynValue.Nil;
            case "removeLegend": ctx.RemoveLegend(Str(t, "name"));               return DynValue.Nil;
            case "warp":       return DynValue.NewBoolean(ctx.Warp(Int(t, "map"), Int(t, "x"), Int(t, "y")));
            case "level":      return DynValue.NewNumber(ctx.Level);
            case "sex":        return DynValue.NewNumber(ctx.Sex);
            case "nation":     return DynValue.NewNumber(ctx.Nation);
            // Emigration (RTK updateCountry): the town criers and Rotah. Persists + clears the bound home.
            case "setNation":  ctx.SetNation(Int(t, "n"));                        return DynValue.Nil;
            case "map":        return DynValue.NewNumber(ctx.MapId);
            case "killCount":  return DynValue.NewNumber(ctx.KillCount(Str(t, "key")));
            case "totalKills": return DynValue.NewNumber(ctx.TotalKills);
            case "maxHp":      return DynValue.NewNumber(ctx.MaxHp);
            case "maxMp":      return DynValue.NewNumber(ctx.MaxMp);
            // Class identity. `classId` is the exact path (Ju jak = 8), `basePathId` the base four it descends
            // from (Ju jak -> 3 Mage) — which is what per-class content is keyed to. `canLearnDogSpells` is
            // the PC-subpath exclusion (Content.CanLearnDogSpells), a rule the script must not re-derive.
            case "classId":    return DynValue.NewNumber(ctx.ClassId);
            case "basePathId": return DynValue.NewNumber(ctx.BasePathId);
            case "canLearnDogSpells": return DynValue.NewBoolean(ctx.CanLearnDogSpells);
            // The Dog's cleanse leaving an NPC subpath for its base class (RTK updatePath(baseClass, mark)). C# owns
            // which paths it moves (NpcContext.LeaveNpcSubpath), for the same reason as canLearnDogSpells.
            case "leaveNpcSubpath": return DynValue.NewBoolean(ctx.LeaveNpcSubpath());
            // This NPC's display name. Several NPCs share ONE identifier and differ only by name (the four
            // Dogs are all DogLinguistNpc), so a handler needs it to know which of them it is speaking as.
            case "npcName":    return DynValue.NewString(ctx.Def.Name);
            // Era gate (Server/Era.cs): does this dated feature exist at the server's target date? Scripts
            // need it because a gated quest usually still has its NPC standing there — the giver predates
            // the quest — so the script, not the placement, is what has to know.
            case "eraHas":     return DynValue.NewBoolean(Era.Has(Str(t, "feature")));
            case "mounted":    return DynValue.NewBoolean(ctx.Mounted);
            case "coins":      return DynValue.NewNumber(ctx.Coins);
            case "spendGold":  return DynValue.NewBoolean(ctx.SpendGold((uint)Math.Max(0, Int(t, "n"))));
            case "gameDate":   return DynValue.NewString(Character.GameDate);
            // Wall-clock unix SECONDS — the unit every persisted cooldown in this server is written in
            // (MinorQuest's KTimer, SuteQuest's recoat timer, the marriage cool-down), and the unit the
            // registry can hold: reg values are C# ints, so unix ms would overflow and unix seconds fit
            // until 2038. Scripts store `now() + wait` and compare, never a duration that has to tick.
            case "now":        return DynValue.NewNumber(ctx.NowUnix);
            // Inclusive [lo,hi] random int. Backed by the C# RNG on purpose: MoonSharp's math.random is
            // unseeded (deterministic per server start) and the sandbox has no os.time to seed it with.
            case "rand":       { int lo = Int(t, "lo"), hi = Int(t, "hi"); if (hi < lo) (lo, hi) = (hi, lo); return DynValue.NewNumber(Random.Shared.Next(lo, hi + 1)); }

            default:
                Log.Warn($"npc_dialog: unknown op '{op}' — ignored");
                return DynValue.Nil;
        }
    }

    // ---- request-table readers ----
    private static string Str(Table t, string k) => t.Get(k).CastToString() ?? "";
    private static int Int(Table t, string k, int def = 0)
    { var v = t.Get(k); return v.Type == DataType.Number ? (int)v.Number : def; }

    /// <summary>Like <see cref="Int"/> but keeps the fraction — karma awards are quarter- and tenth-points,
    /// so reading them as int would silently zero every partial award.</summary>
    private static double Num(Table t, string k, double def = 0)
    { var v = t.Get(k); return v.Type == DataType.Number ? v.Number : def; }

    private static string[] Arr(Table t, string k)
    {
        var a = t.Get(k);
        if (a.Type != DataType.Table) return Array.Empty<string>();
        int n = a.Table.Length;
        var r = new string[n];
        for (int i = 1; i <= n; i++) r[i - 1] = a.Table.Get(i).CastToString() ?? "";
        return r;
    }
}
