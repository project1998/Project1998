using Shared;

namespace Server;

// The old hard-coded item -> effect table (ItemUseEffect record + Content.ItemEffects dictionary) has moved
// out of C# into the data-driven verb/row Lua system, exactly like spells: game-data/ItemParams.csv is
// the "row" (each consumable's verb + numeric params), game-data/item_verbs.lua is the "verb" (the
// logic), and Session.ApplyItemEffect runs them through ItemContext (see Server/ItemScript.cs). Both files
// hot-reload via @reload, so a food's heal amount or a potion's ward duration is a CSV edit, not a rebuild.

/// <summary>
/// A spell/skill definition from the RTK <c>Spells</c> table. <c>Name</c> is the display name
/// (SplDescription, e.g. "Bolt"); <c>Key</c> is the internal identifier (SplIdentifier, e.g. "bolt_mage").
/// <c>PathId</c> is the class that learns it (0=Peasant 1=Warrior 2=Rogue 3=Mage 4=Poet, 5+=subpaths,
/// 99=system/common). <c>Level</c> is the character level required to learn it. <c>Alignment</c> is the
/// sub-alignment the spell belongs to (<b>-1</b> = universal/any, <b>0</b> = base/unaligned, <b>1</b> = Kwisin,
/// <b>2</b> = Mingken, <b>3</b> = Ohaeng); a character learns only universal + their own alignment's set, so
/// the other alignments' parallel spells (which often share a display name) aren't taught as duplicates.
/// <c>Type</c> is the client's spellbook type byte (the 0x17 add-spell / 0x0F cast discriminator): <b>1</b> =
/// prompt spell (the client asks <c>Question</c> and sends the typed answer), <b>2</b> = targeted (the client
/// sends a target entity id), <b>5</b> = self / no-target. The client renders type 1/2 in the Spell book and
/// type 5 in the Skill book (both populate through the same 0x17 packet, keyed on this type).
/// <c>Mark</c> is the SUBPATH RANK required (SplMark: 0 = the base 1-99 class list, 1 = Il san, 2 = Ee san,
/// 3 = Sam san) — 121 of the 906 rows carry one and they all have <c>SplLevel</c> 0, which used to make them
/// look like level-1 spells and land in every level-99 character's book alongside the base list. See
/// <see cref="Content.MarkSpellLevel"/>.
/// </summary>
public sealed record SpellDef(int Id, string Key, string Name, byte Type, int PathId, int Level, int Alignment, string Question, bool CanFail = false, int Mark = 0)
{
    public bool NeedsTarget => Type == 2;   // client sends a target entity id (u32) when casting
    public bool NeedsPrompt => Type == 1;   // client sends the typed answer string when casting
    public bool IsSpell     => Type is 1 or 2;   // magic — goes in the Spell book
    public bool IsSkill     => Type == 5;        // physical ability — goes in the Skill book
}

/// <summary>The runtime EFFECT of a spell, extracted from RTK's Lua scripts (re/extract_spell_formulas.py →
/// spell_effects.csv). Keyed by the same identifier as <see cref="SpellDef.Key"/> (the Lua table name ==
/// SplIdentifier). <c>Archetype</c> is one of Damage / Heal / Buff / Debuff / ManaBattery / Cure / Utility /
/// Summon / Teleport / Dialog. <c>AmountExpr</c> is the spell's real damage/heal formula as a Lua arithmetic
/// string over player/target stats (evaluated by <see cref="Formula"/>); <c>Mana</c> is the true mana cost;
/// buff/debuff/cure carry their own params. Session.ApplyCast dispatches on this. Missing fx ⇒ the keyword
/// classifier is the fallback. <c>CureCat</c> is RTK's duration-category tag (<c>player:removeDuras(cat)</c>)
/// — most Buff/Debuff spells carry one (morphs/venoms/paras/curses/…); Session.ApplyCast special-cases
/// "backstabs"/"flanks" (the Warrior Backstab/Flank skills — a boolean combat STANCE, not a numeric
/// BuffStat/BuffAmt our generic buff loop can express) before the normal archetype dispatch.</summary>
public sealed record SpellFx(
    string Key, string Archetype, int Mana, string AmountExpr, string BuffStat, string BuffAmt,
    int DurationMs, string Debuff, string Chance, string HealthCost, int Animation, int Sound, int Aether,
    int PcAlign, string CureCat = "", string Class = "", int Action = 0);

/// <summary>Tiny arithmetic evaluator for the Lua damage/heal formulas RTK spells use, e.g.
/// <c>"25 + math.floor(player.level / 2) + math.floor((player.will + 3) / 4)"</c> or
/// <c>"math.ceil(player.magic * 2.15)"</c>. Supports + - * /, unary sign, parens, decimal literals, dotted
/// variables (player.level, target.baseHealth — resolved against a supplied var map), and the functions
/// math.floor / math.ceil / math.abs / math.random. Unknown names resolve to 0 and a malformed expression
/// yields 0 (never throws) — a missing formula degrades to "no effect", not a crash.</summary>
public static class Formula
{
    private static readonly Random Rng = new();

    public static double Eval(string? expr, IReadOnlyDictionary<string, double> vars)
    {
        if (string.IsNullOrWhiteSpace(expr)) return 0;
        try { return new Parser(expr, vars).ParseAll(); }
        catch (Exception e) { Log.Warn($"formula '{expr}' failed to evaluate — treated as 0", e); return 0; }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly IReadOnlyDictionary<string, double> _v;
        private int _i;
        public Parser(string s, IReadOnlyDictionary<string, double> v) { _s = s; _v = v; }

        private void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private char Cur => _i < _s.Length ? _s[_i] : '\0';

        public double ParseAll() { double v = Expr(); return v; }

        private double Expr()
        {
            double v = Term();
            while (true)
            {
                Ws();
                if (Cur == '+') { _i++; v += Term(); }
                else if (Cur == '-') { _i++; v -= Term(); }
                else return v;
            }
        }
        private double Term()
        {
            double v = Factor();
            while (true)
            {
                Ws();
                if (Cur == '*') { _i++; v *= Factor(); }
                else if (Cur == '/') { _i++; double d = Factor(); v = d == 0 ? 0 : v / d; }
                else return v;
            }
        }
        private double Factor()
        {
            Ws();
            if (Cur == '-') { _i++; return -Factor(); }
            if (Cur == '+') { _i++; return Factor(); }
            return Primary();
        }
        private double Primary()
        {
            Ws();
            if (Cur == '(') { _i++; double v = Expr(); Ws(); if (Cur == ')') _i++; return v; }
            if (char.IsDigit(Cur) || Cur == '.') return Number();

            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
            string name = _s.Substring(start, _i - start);
            Ws();
            if (Cur == '(')   // function call
            {
                _i++;
                var args = new List<double>();
                Ws();
                if (Cur != ')')
                {
                    args.Add(Expr());
                    while (true) { Ws(); if (Cur == ',') { _i++; args.Add(Expr()); } else break; }
                }
                Ws(); if (Cur == ')') _i++;
                return Call(name.ToLowerInvariant(), args);
            }
            return Var(name);
        }
        private double Number()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            double.TryParse(_s.Substring(start, _i - start),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v);
            return v;
        }
        private static double Call(string name, List<double> a)
        {
            double A0 = a.Count > 0 ? a[0] : 0;
            switch (name)
            {
                case "math.floor": return Math.Floor(A0);
                case "math.ceil":  return Math.Ceiling(A0);
                case "math.abs":   return Math.Abs(A0);
                case "math.max":   return a.Count >= 2 ? Math.Max(a[0], a[1]) : A0;
                case "math.min":   return a.Count >= 2 ? Math.Min(a[0], a[1]) : A0;
                case "math.random":
                    if (a.Count >= 2) return Rng.Next((int)a[0], (int)a[1] + 1);
                    if (a.Count == 1) return Rng.Next(1, (int)a[0] + 1);
                    return Rng.NextDouble();
                default: return 0;
            }
        }
        private double Var(string name)
        {
            if (_v.TryGetValue(name, out var v)) return v;
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && _v.TryGetValue(name[(dot + 1)..], out v)) return v;
            return 0;
        }
    }
}

public static partial class Content
{

    // All learnable spells/skills (RTK Spells table, section-headers + inactive rows filtered out), and the
    // class/path id -> display name table (RTK Paths table). Read-only after Load, shared lock-free.
    public static IReadOnlyList<SpellDef> Spells
    {
        get => _snapshotBuilder?.Spells ?? Snapshot.Spells;
        private set => Builder.Spells = value;
    }
    public static IReadOnlyDictionary<int, string> Paths
    {
        get => _snapshotBuilder?.Paths ?? Snapshot.Paths;
        private set => Builder.Paths = value;
    }

    // Per-spell runtime effect (archetype + real RTK formulas), keyed by spell identifier. Drives the magic
    // engine in Session.ApplyCast. Extracted from RTK's Lua by re/extract_spell_formulas.py; empty ⇒ every
    // cast falls back to the keyword classifier. Read-only after Load, shared lock-free.
    public static IReadOnlyDictionary<string, SpellFx> SpellFx
    {
        get => _snapshotBuilder?.SpellFx ?? Snapshot.SpellFx;
        private set => Builder.SpellFx = value;
    }

    // Per-spell TARGET flavor line (game-data/SpellText.csv), CANONICAL from LIVE NexusTK — supersedes RTK.
    // The caster always just sees "You cast <name>." (Session.HandleCast); the TARGET of a spell additionally
    // sees this line when present. On a self-cast you are both, so you see the flavor THEN the cast line.
    public static IReadOnlyDictionary<string, (string Target, string Fade)> SpellTexts
    {
        get => _snapshotBuilder?.SpellTexts ?? Snapshot.SpellTexts;
        private set => Builder.SpellTexts = value;
    }
    /// <summary>The live flavor shown to the TARGET when a spell is applied, or "" if none is recorded.</summary>
    public static string TargetTextFor(string key) => SpellTexts.TryGetValue(key, out var t) ? t.Target : "";
    /// <summary>The live flavor shown when a timed buff FADES (RTK uncast), or "" if none is recorded.</summary>
    public static string FadeTextFor(string key) => SpellTexts.TryGetValue(key, out var t) ? t.Fade : "";
    private static IReadOnlyDictionary<int, SpellDef> SpellByIdIndex
    {
        get => _snapshotBuilder?.SpellById ?? Snapshot.SpellById;
        set => Builder.SpellById = value;
    }
    private static IReadOnlyDictionary<string, SpellDef> SpellByKeyIndex
    {
        get => _snapshotBuilder?.SpellByKey ?? Snapshot.SpellByKey;
        set => Builder.SpellByKey = value;
    }
    private static IReadOnlyDictionary<string, int> PathIdByNameIndex
    {
        get => _snapshotBuilder?.PathIdByName ?? Snapshot.PathIdByName;
        set => Builder.PathIdByName = value;
    }

    // Data-driven spell params (game-data/SpellParams.csv): per spell key, the raw CSV row its Lua verb
    // reads (the `verb` column + numeric params like coeff/mana/amount). The "row" half of the verb/row spell
    // model — the "verb" logic lives in spell_verbs.lua (see Server/SpellScript.cs + Session.ApplyCast). Sparse:
    // only migrated spells have a row; everything else uses the C# CastX dispatch. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SpellParams
    {
        get => _snapshotBuilder?.SpellParams ?? Snapshot.SpellParams;
        private set => Builder.SpellParams = value;
    }

    // ---- spells / classes (used by the @lvl/@class/@mark/@align book rebuild + casting) -------

    /// <summary>The display name of a class/path id (e.g. 1 -> "Warrior"); "path&lt;id&gt;" if unknown.</summary>
    public static string PathName(int pathId) =>
        Paths.TryGetValue(pathId, out var n) && !string.IsNullOrEmpty(n) ? n : $"path{pathId}";

    /// <summary>Resolve a class/path NAME (as stored on the character, e.g. "Warrior") to its path id, or
    /// -1 if it matches no known class. Case-insensitive against the base class name (Paths.PthMark0).</summary>
    public static int PathIdForClass(string? className)
    {
        var name = (className ?? "").Trim();
        return name.Length != 0 && PathIdByNameIndex.TryGetValue(name, out var id) ? id : -1;
    }

    /// <summary>Real per-class level + item/gold cost to LEARN a spell from a trainer. <c>Items</c> is
    /// checked and consumed alongside <c>Gold</c>, all-or-nothing.</summary>
    public sealed record LearnCost(int Level, int Gold, (string Item, int Amount)[] Items);

    /// <summary>Per-spell, per-class real learn data — key → {pathId → cost}. Generated 2026-07-27 by
    /// <c>re/merge_spell_costs.py</c> from two sources, per the user's explicit ranking (archive beats Lua):
    /// <list type="bullet">
    /// <item>Archive-sourced (149 rows): cross-checked against the tswolf.com + boards.nexustk.com scrape
    /// (tswolf.com class spell-list pages, Wayback-dated 2001, + boards.nexustk.com tutor-board posts) —
    /// covers the base 1-99 spell list for all 4 classes plus the "peasant commons" spells (which turned out
    /// to NOT be flat-universal: Return/Approach/Summon are Rogue/Mage/Poet only at different levels each,
    /// see <c>nexustk-495-restricted-commons-spells</c> memory for the full reconciliation and the 9
    /// within-archive conflicts resolved directly with the user).</item>
    /// <item>Lua-fallback (424 rows): <c>re/extract_spell_requirements.py</c>'s static parse of every RTK
    /// spell script's own <c>requirements()</c> function, used only where no archive data exists.</item>
    /// </list>
    /// Deliberately NOT covered: Propose (its only real cost is the <c>engagement_ring</c>'s shop price,
    /// already charged in <c>ChapelAbility.BuyRing</c> before it grants the spell directly), subpath-only
    /// spells (PathId 5+, unreachable via <see cref="SpellsForClass"/> regardless since only base classes
    /// 0-4 are modeled), and the Il/Ee/Sam/Sa-san alignment-tier spells (gated by a rank-progression system
    /// this server doesn't implement, not a character level — would need a fake level to force into this
    /// table, which was deliberately avoided rather than guessed). Any spell with no entry here still teaches
    /// free at its CSV <c>SplLevel</c>/<c>PathId</c>, the pre-existing behavior.</summary>
    public static IReadOnlyDictionary<string, Dictionary<int, LearnCost>> SpellCosts
    {
        get => _snapshotBuilder?.SpellCosts ?? Snapshot.SpellCosts;
        private set => Builder.SpellCosts = value;
    }

    /// <summary>The real cost to learn <paramref name="sp"/> as class <paramref name="pathId"/>, or null if
    /// this spell has no entry in <see cref="SpellCosts"/> (learned free at its CSV level, as before).</summary>
    public static LearnCost? LearnCostFor(SpellDef sp, int pathId) =>
        SpellCosts.TryGetValue(sp.Key, out var perClass) && perClass.TryGetValue(pathId, out var cost) ? cost : null;

    /// <summary>How long the caster holds the magic pose — the 0x1A action <c>time</c> field, in 1/60s frames
    /// (35 = ~583ms). ONE value for every spell, deliberately.
    /// <para>RTK varies it per script (35 ×117, 20 ×45, 25 ×9, 30 ×8 across 179 spells) and the groups are
    /// coherent families — 30 is the Poet songs, 25 is the mount/companion set — but that is the RTK author's
    /// styling, not evidence about real 4.95, and RTK coverage has burned us before. The only thing actually
    /// established is that our old hardcoded 8 matches NOTHING in RTK and is too short to survive a held key.
    /// 35 is RTK's modal value and what every attack/heal spell uses. If real per-spell values ever turn up,
    /// this becomes a lookup then — not before.</para></summary>
    public const ushort CastAnimFrames = 35;

    // Genuinely universal base spells — the Nexon manual's "base secrets for every path", learned free in the
    // newbie quest (currently just Soothe). Taught by @spells to EVERY class at their base SplLevel, and NOT
    // gated by SpellCosts even though they carry per-class rows there — for these, those rows are relearn-cost
    // data for the NPC relearn flow only, never a teachable gate. This explicit marker is what separates them
    // from RESTRICTED commons (Return/Approach/Summon): those are ALSO PathId 0 + in SpellCosts, but there the
    // rows ARE the gate — a class with no row (e.g. Warrior for Return) is correctly excluded. PathId alone
    // can't tell the two apart, hence this allowlist.
    private static readonly HashSet<string> UniversalBaseSpells = new(StringComparer.OrdinalIgnoreCase) { "soothe" };
    public static bool IsUniversalBaseSpell(SpellDef sp) => UniversalBaseSpells.Contains(sp.Key);

    // ---- the Share Wisdom ladder ---------------------------------------------------------------------
    //
    // The five rungs in order, which is the ONE definition: npc_dialog.lua's SAGE_LADDER (what the Sage
    // sells), spell_verbs.lua's SAGE_RUNGS (how far each reaches), the NpcGrantedSpells gate below, the
    // rebuild's re-grant and @sage all read the same order from here. A smoke test pins the Lua copies
    // against this array, because a rename on one side only is otherwise silent.
    public static readonly string[] SageLadder =
        { "share_wisdom", "mentors_wisdom", "apprentices_wisdom", "adepts_wisdom", "sages_wisdom" };

    /// <summary>The level the Sage teaches at — "available to all paths for people over the level 90".
    /// The ROOM is deliberately more generous (Maps.csv keeps map 1230 at 50) so a player can find him
    /// early and be told what it takes; this is the gate that actually bites.</summary>
    public const int SageLevel = 90;

    /// <summary>Registry key: which rung the character has paid for (0 = none), written by the Sage's own
    /// dialog and by <c>@sage</c>. The spell in the book is the visible half; this is the half that
    /// SURVIVES a character rebuild, exactly as <see cref="DogFlagReg"/> does for the Dog spells — without
    /// it, one <c>@lvl</c> would confiscate a 500,000-gold ladder with no way to get it back.</summary>
    public const string SageRungReg = "sage_rung";

    /// <summary>Registry key: absolute unix-SECOND deadline before the next rung may be bought. Written by
    /// the Sage (now + 90 days) and cleared by <c>@sage</c> so a tester is not stuck behind it.</summary>
    public const string SageTimerReg = "sage_timer";

    /// <summary>The spell key for a 1-based rung, or null if the rung is out of range (0 = holds none).</summary>
    public static string? SageSpellForRung(int rung) =>
        rung >= 1 && rung <= SageLadder.Length ? SageLadder[rung - 1] : null;

    /// <summary>The rung a spell key is, or 0 if it is not one of them.</summary>
    public static int SageRungOf(string key) =>
        System.Array.FindIndex(SageLadder, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) + 1;

    /// <summary>The rung a character rebuild should hand back: what they paid for, but only once they are
    /// high enough to have bought it. Level-gated for the same reason the Dog spells are — drop a character
    /// to level 5 and the entitlement stops applying until they are 90 again, at which point it returns.
    /// Returns null when there is nothing to grant.</summary>
    public static SpellDef? SageSpellFor(int rung, int maxLevel)
    {
        if (maxLevel < SageLevel) return null;
        return SageSpellForRung(rung) is { } key && SpellByKey(key) is { } sp
            ? sp with { Level = SageLevel }
            : null;
    }

    // Spells granted by ONE specific NPC flow and by nothing else — never teachable at a path trainer, never
    // handed out by an @spells rebuild. Propose comes with the engagement ring you buy at the chapel
    // (ChapelAbility.BuyRing), which is its real cost and its real gate; SpellCosts' doc has always said so,
    // but the archive merge nonetheless wrote propose rows for Mage and Poet (level 11), and a SpellCosts row
    // is exactly what makes SpellsForClass offer a spell — so path leaders were teaching it. Those rows stay
    // in the CSV as the relearn-cost record they were extracted as; this is the gate.
    // The five Sage rungs join it for the same reason. The Sage in the wilderness is their only teacher
    // ("The Share wisdom spells can be learned from 'The Sage', an old man who lives in the wilderness at
    // 0126 0007" — tutor board), and npc_dialog.lua's SageNpc is that flow; but the archive merge wrote
    // share_wisdom rows for Warrior/Mage/Poet at level 90, so path leaders were selling rung 1 out from
    // under him. The upper four are already unreachable (SplPthId 99 matches no class), and are listed
    // anyway because SpellLearnCosts.csv is GENERATED — re/merge_spell_costs.py can hand any of them a row
    // on the next merge, and this gate has to hold when it does. The set is BUILT from SageLadder rather
    // than restating it, so adding a rung cannot leave the gate one key behind.
    private static readonly HashSet<string> NpcGrantedSpells =
        new(SageLadder.Append("propose"), StringComparer.OrdinalIgnoreCase);
    public static bool IsNpcGrantedOnly(SpellDef sp) => NpcGrantedSpells.Contains(sp.Key);

    /// <summary>Spells only ONE city's trainer teaches, keyed by <see cref="BaseKey"/> → <see cref="RegionOf"/>
    /// (0 Kugnae · 1 Buya · 3 Nagnang). Keying on the base key covers all four alignment reskins of each at once.
    ///
    /// <para>The rogue self-heal Remedies, verbatim from nexusatlas 2003-07-01 "Rogue Changes" (Rachel):
    /// <i>"Maro's Remedy: Learned in Kugnae only … Maso's Remedy: Learned in Buya only … Dagger's Remedy:
    /// Learned in Nagnang only"</i>. Maro, Maso and Dagger ARE the three cities' rogue trainers, so the lock is
    /// really "each trainer's own remedy", and the three make one ladder that climbs by travelling rather than
    /// by grinding a single guild — 1500/1000 vita/mana at level 99, then 3000/2000 at Il san, then 4500/3000
    /// at Ee san. Without this every rogue trainer offered all three the moment the rank was met.</para>
    ///
    /// <para>Explorer's Remedy is deliberately NOT here: the same post files it under <i>"(Rumored) Learned ??
    /// (New wilderness/vale NPC?)"</i>, so there is no city to pin it to.</para></summary>
    private static readonly Dictionary<string, int> CityLockedSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        ["maros_remedy"]   = 0,   // Kugnae  — Maro  (trainer on map 16, Maro Sanctum)
        ["masos_remedy"]   = 1,   // Buya    — Maso  (trainer on map 368, Maso Sanctum)
        ["daggers_remedy"] = 3,   // Nagnang — Dagger
    };

    /// <summary>The one region whose trainer teaches this spell, or -1 if any of them will.</summary>
    public static int CityLockOf(SpellDef sp) =>
        CityLockedSpells.TryGetValue(BaseKey(sp), out var region) ? region : -1;

    /// <summary>May a trainer standing in <paramref name="region"/> teach this spell? True for everything that
    /// isn't city-locked, so callers can filter unconditionally.</summary>
    public static bool TeachableInRegion(SpellDef sp, int region)
    {
        int locked = CityLockOf(sp);
        return locked < 0 || locked == region;
    }

    /// <summary>City name for a <see cref="RegionOf"/> id, for the "you'll have to go there" line the trainer
    /// shows in place of a locked secret. Falls back to the raw id so an unmapped region is still legible.</summary>
    public static string RegionCityName(int region) => region switch
    {
        0 => "Kugnae", 1 => "Buya", 2 => "the Mythic", 3 => "Nagnang", _ => $"region {region}",
    };

    /// <summary>Whether class <paramref name="pathId"/> may (re)learn <paramref name="sp"/> from a tutor NPC —
    /// the gate for the "Learn Secret" menu, distinct from the universal <c>@spells</c> grant. A universal
    /// base spell (Soothe) is granted to every class at the newbie quest, but if FORGOTTEN it can only be
    /// relearned at the Guild by a class that has a per-class <see cref="SpellCosts"/> row for it — which is
    /// how Poet is correctly refused Soothe (Warrior/Rogue/Mage have rows, Poet doesn't), matching the live
    /// game's "cannot be relearned by Poets". Any non-universal spell is unaffected (already gated upstream by
    /// <see cref="SpellsForClass"/>), so it always returns true here.</summary>
    public static bool CanRelearnAtNpc(SpellDef sp, int pathId) =>
        !IsUniversalBaseSpell(sp) || LearnCostFor(sp, pathId) is not null;

    /// <summary>Every spell/skill a class can learn at or below <paramref name="maxLevel"/> for a given
    /// <paramref name="alignment"/> (0 unaligned / 1 Kwisin / 2 Mingken / 3 Ohaeng) — i.e. the teachable set
    /// for "@spells". <see cref="SpellCosts"/> is checked FIRST for a spell's key: if present, the class only
    /// qualifies if its pathId has an entry in that spell's per-class table (which is how Warrior ends up
    /// correctly excluded from Return/Approach/Summon — it simply has no row there), at THAT entry's level;
    /// spells with no <see cref="SpellCosts"/> entry fall back to the old universal rule (own path OR
    /// path-0 "peasant commons", at the CSV's flat <c>SplLevel</c>). A spell qualifies if it is universal
    /// (Alignment -1) OR matches the character's alignment; the other sub-alignments' parallel spells are
    /// excluded, so an unaligned character never gets the Kwisin/Mingken/Ohaeng variants (which often share a
    /// display name → looked like duplicates). Deduped by display name as a safety net, preferring the
    /// exact-alignment version over a universal one. Ordered by level then name so the spellbook fills in a
    /// sensible order. Spells switched off by an era gate (see <see cref="IsOutOfEraSplitTrap"/>) or owned by
    /// a single NPC flow (see <see cref="IsNpcGrantedOnly"/> — Propose) are dropped outright, so they never
    /// reach a tutor menu, the character rebuild, or the Divine Secret preview.
    /// <para><paramref name="mark"/> is the character's subpath rank, and gates the Il/Ee/Sam san secrets:
    /// they carry <c>SplMark</c> 1-3 and are pinned to <see cref="MarkSpellLevel"/>, so a level-99 base
    /// character sees none of them and an Ee san sees ranks 1 and 2 (ranks are cumulative — you keep what Il
    /// san taught you). Before this the column was read by nothing at all, which is how every level-99
    /// character ended up holding secrets belonging to ranks they had never earned.</para>
    /// <para>Dog spells are NOT here and must not be added: "The guildmaster is not involved in these spells"
    /// (nexusatlas Dog Spells listing) — the class's Dog teaches them itself, in exchange for kills and goods.
    /// They carry <c>SplPthId</c> 99, which no class filter matches, so they drop out of this list naturally;
    /// see <see cref="CanLearnDogSpells"/> and the <c>DogLinguistNpc</c> handler in npc_dialog.lua.</para>
    /// <para>An NPC subpath's own spell (Chung ryong's rage, Baekho's cunning, Ju jak evocation, Hyun moo revival;
    /// <c>SplPthId</c> 6-9) is offered to that subpath and to no other path. Its learn row is keyed to the BASE
    /// class, because the trainer reads the fee with the base path (<see cref="NpcContext.BasePathId"/>), and
    /// without this guard that row would also offer the spell to every player of the base class. RTK draws the same
    /// line: an NPC subpath learns its base class's list plus its own spells, everyone else the base list
    /// (<c>Scripts/Spells.lua</c>, <c>Player.learnSpell</c>). PC-subpath spells (<c>SplPthId</c> 10-21) are not
    /// this guard's: none has a learn row, and who teaches them is a separate rule.</para></summary>
    public static List<SpellDef> SpellsForClass(int pathId, int maxLevel, int alignment, int mark = 0)
    {
        // An NPC subpath IS its base class plus a little: a Chung ryong learns the whole Warrior list (the
        // learn-cost table and every SplPthId are keyed to the base four), then its own signature spell on top.
        // PathBaseOf is RTK's classdb_path, the same mapping the gear restriction already uses, so "a Chung
        // ryong may wear warrior gear" and "a Chung ryong learns warrior secrets" now come from one source.
        int basePath = PathBaseOf(pathId);
        bool isSubpath = pathId != basePath;                      // ANY subpath, PC or NPC — used for the
                                                                  // signature-spell rule further down

        return Spells.Where(s => (s.Alignment < 0 || s.Alignment == alignment) && s.Mark <= mark)
              .Select(s =>
                    isSubpath && s.PathId == pathId ? s with { Level = MarkSpellLevel }   // the subpath's own signature spell; you only get there at the cap
                  : IsNpcSubpath(s.PathId) ? null                  // ANOTHER path's NPC-subpath spell: never offered (see the summary)
                  : IsUniversalBaseSpell(s) ? s                    // taught to EVERY class at its base level; SpellCosts rows are relearn-cost only
                  : SpellCosts.TryGetValue(s.Key, out var perClass)
                      ? (perClass.TryGetValue(basePath, out var cost) ? s with { Level = cost.Level } : null)
                      : (s.PathId == basePath || s.PathId == 0 ? s : null))
              .Where(s => s is not null && s.Level <= maxLevel && !IsOutOfEraSplitTrap(s) && !IsNpcGrantedOnly(s))
              .Select(s => s!)
              .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .Select(g => g.OrderByDescending(s => s.Alignment == alignment).ThenBy(s => s.Level).First())
              .OrderBy(s => s.Level).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .ToList();
    }

    // ---- DOG spells -----------------------------------------------------------------------------------
    // Two per base class, taught by the class's Dog and by nobody else ("The guildmaster is not involved in
    // these spells" — you kill something, come back, and hand over goods). They live in Spells.csv under the
    // "===DOG SPELLS===" divider with SplPthId 99 and SplLevel 0, so no class filter can reach them; the
    // whole flow lives in the DogLinguistNpc handler in npc_dialog.lua, which owns the per-class spell,
    // level and requirement table because that data IS the dialog.
    //
    // SOURCES, both era-correct for the 4.95 client (built 2001-06-29):
    //   * tswolf.com/spells/dog.shtml + /quests/dogling/, Wayback captures 2001-02-24 / 2001-03-11
    //   * nexusatlas.com/spells/dog.php, capture 2002-12-30 (re/fx/atlas_html/class_dog.html)
    // They disagree on some numbers; the atlas listing wins, the same tie-break already used for Siege's
    // aether — and our spell_effects rows were sourced from it, so they already agree. Divergences kept on
    // record: tswolf has Survive at 1000 mana (atlas 600), Spot Traps 60 (100), Serpent's Fury level 91 and
    // 500 mana (99 / 800), Spirit Fury 500 mana (1000). RTK's own Lua is a 2019-fork rewrite that swapped
    // both level-99 kill targets for a Golden lobster and Spirit Fury's Ambrosia for a Titanium glove; it is
    // the weakest of the three and is followed only where the other two are silent.
    //
    // ELIGIBILITY: "These spells are for people not in PC Subpaths. NPC Subpaths can get these spells as
    // well" (tswolf), i.e. the four base classes AND the four NPC subpaths, never a PC subpath — which is
    // exactly what CanLearnDogSpells says. (An earlier comment here claimed NPC subpaths ONLY; the code
    // never did that, and the archive confirms the code.)

    /// <summary>An NPC subpath (Chung ryong · Baekho · Ju jak · Hyun moo) as against a PC one (Barbarian,
    /// Monk, Druid, …). Paths.csv separates them in <c>PthIcon</c>: 4 for the four NPC subpaths, 1/2/3 for
    /// the twelve PC ones, 0 for the base classes and Archon.</summary>
    public const int NpcSubpathIcon = 4;
    public static bool IsNpcSubpath(int pathId) => PathIcon.GetValueOrDefault(pathId) == NpcSubpathIcon;

    /// <summary>The four base classes — Warrior · Rogue · Mage · Poet. Peasant (0) is not one.</summary>
    public static bool IsBaseClass(int pathId) => pathId >= 1 && pathId <= 4 && PathBaseOf(pathId) == pathId;

    /// <summary>May this path learn Dog spells AT ALL (before the Dog flag is even considered)?
    /// <para>Base classes and NPC subpaths yes; PC subpaths never — a Barbarian or a Monk cannot learn them,
    /// which matches "These spells are for people not in PC Subpaths. NPC Subpaths can get these spells as
    /// well" (tswolf 2001) and the atlas's "People in PC subpaths cannot learn these spells".</para>
    /// <para>This replaces a test of <c>pathId != basePath</c>, which was exactly inverted: it let all TWELVE
    /// PC subpaths through (Barbarian, Merchant, Diviner, Druid, Chongun, Ranger, Geomancer, Monk, Do, Spy,
    /// Shaman, Muse) while excluding the four base classes that should have them.</para></summary>
    public static bool CanLearnDogSpells(int pathId) => IsBaseClass(pathId) || IsNpcSubpath(pathId);

    /// <summary>Quest-registry key for the Dog flag — set when the Spotted dog finishes the bark/woof/grrowl
    /// chain, and the gate on saying "secret" to your own class's Dog. Lives in the flat registry
    /// (<see cref="QuestState.Registry"/>) like every other quest flag, so no schema change.</summary>
    public const string DogFlagReg = "dog_flag";

    /// <summary>The two Dog spells each BASE class may hold, in teach order, with the level each is pinned
    /// to. Spells.csv cannot say either thing — all eight rows carry <c>SplPthId</c> 99 and <c>SplLevel</c> 0
    /// so that no class filter can reach them (see <see cref="SpellsForClass"/>) — so the pairing and the
    /// levels are declared here, mirroring the <c>DOG_SPELLS</c> table in npc_dialog.lua, which owns the
    /// other half of each tier (the kills, the goods and the atlas's 20,000-vita / 10,000-mana gate on the
    /// level-99 one) because that data IS the dialog. Same source as the Lua: the nexusatlas Dog Spells
    /// listing, capture 2002-12-30. <c>DogSpellTiersAreReal</c> in ContentSmokeTests pins the keys.</summary>
    private static readonly Dictionary<int, (string Key, int Level)[]> DogSpellTiers = new()
    {
        [1] = new[] { ("greater_blessing", 70), ("spirit_fury",   99) },   // Warrior
        [2] = new[] { ("spot_traps",       70), ("serpents_fury", 99) },   // Rogue
        [3] = new[] { ("fissure",          70), ("lava_surge",    99) },   // Mage
        [4] = new[] { ("survive",          70), ("fascinate",     99) },   // Poet
    };

    /// <summary>The Dog spells a character of this path holds at <paramref name="maxLevel"/>, level-stamped
    /// from <see cref="DogSpellTiers"/>. Empty unless the path is eligible (<see cref="CanLearnDogSpells"/>);
    /// an NPC subpath reads its BASE class's pair, exactly as it inherits that class's whole spell list.
    ///
    /// <para>THE DOG FLAG IS THE CALLER'S TEST, not this one's — the only caller is
    /// <see cref="RespecSpellSet"/>, and it passes the flag in. This deliberately does NOT re-check the
    /// kills/goods each tier costs at the Dog: the character rebuild grants what a character of this class
    /// and level WOULD hold, the same way it hands over tutor spells without charging the tutor's fee.</para></summary>
    public static List<SpellDef> DogSpellsFor(int pathId, int maxLevel)
    {
        var result = new List<SpellDef>();
        if (!CanLearnDogSpells(pathId)) return result;
        if (!DogSpellTiers.TryGetValue(PathBaseOf(pathId), out var tiers)) return result;
        foreach (var (key, level) in tiers)
            if (level <= maxLevel && SpellByKey(key) is { } sp)
                result.Add(sp with { Level = level });
        return result;
    }

    /// <summary>The level a mark (subpath-rank) spell is pinned to. Marks sit ON TOP of the level cap — an
    /// Il san is, in the user's words, "level 100" — so every rank spell needs level 99 first and then the
    /// rank. The CSV can't say that (SplLevel is 0 on all 121 of them), so <see cref="LoadSpells"/> floors
    /// them here and <see cref="SpellsForClass"/>'s ordinary <c>Level &lt;= maxLevel</c> test does the rest.</summary>
    public const int MarkSpellLevel = 99;

    /// <summary>The highest subpath rank a character may hold: <b>3, Sam san</b> — the last one that exists
    /// as content. Paths.csv names five ranks per path and Items.csv carries 34 Sa san (mark 4) items, but
    /// <b>Spells.csv stops dead at mark 3</b>: 46 mark-1 rows, 57 mark-2, 18 mark-3, and zero for 4 or 5.
    /// (That asymmetry is exactly what proved Sam san was an RTK implementation gap rather than an
    /// out-of-era feature — see the nexustk-495-subpath-spells note; Sa san is the same gap, one tier up and
    /// not yet closed.) Allowing rank 4 would mint a "Sa san" whose only difference from a Sam san is one
    /// more level of stat growth and a title, so the ladder stops here until those spells are written.
    /// <para>KNOWN CONSEQUENCE: the 34 mark-4 items stay unwearable, since the ItmMark gear gate reads the
    /// same field. That is the correct behaviour for a rank nobody can hold — Sa san gear is precisely what
    /// a Sa san would wear — and it reverses the moment this constant moves.</para></summary>
    public const int MaxMark = 3;

    // ---- ability LADDERS (the same ability, restated stronger, over and over) --------------------------
    // Mage learns nine single-target zaps that differ only in magnitude (Thunder Bolt -> Spark -> Singe ->
    // Ignite -> Ion -> Impact -> Call Lightning -> Stormstrike -> Hellfire), five 4-way ones, and nine heals
    // across two ladders. Learning ALL of them is what filled the book: 57 entries for a level-99 Ee san mage
    // against a 52-slot cap, so the tail was silently dropped. Once you have Hellfire, Thunder Bolt is not a
    // spell you would ever cast — so the character-rebuild grant (RespecSpellSet) keeps ONLY the top rung of
    // each ladder you qualify for. Every class ends up between 20 and 48 entries at every mark.
    //
    // Ladders are per-class and by SHAPE, not by archetype: single-target zap, 4-way zap, self-only heal,
    // targeted heal and 4-way heal are five separate ladders because each does something the others can't,
    // and a class keeps its best of each. Anything not listed here is never trimmed — buffs, curses, cures,
    // traps, summons and the mark secrets are all one-of-a-kind and all survive.
    //
    // Only the BASE (unaligned) key of each rung is listed; BuildAlignFamilies expands each to its Kwisin /
    // Mingken / Ohaeng reskins, which is why a ladder like the mage 4-way one is 5 entries here and covers
    // the same 20 keys AreaZapMana spells out by hand.
    //
    // This is the GM/tester grant ONLY. Tutor NPCs still offer the whole ladder (SpellsForClass is
    // untouched), because a real character climbing it one rung at a time is the entire point of the ladder.
    //
    // A RUNG MUST HAVE NO AETHER. A cooldown is what separates "the attack you press" from "the button you
    // save for one moment", and the two are not tiers of one ability no matter how the damage numbers rank:
    // you cannot fight with a spell you may cast once every 19 seconds, so collapsing the ladder onto one
    // takes the class's basic attack away rather than upgrading it. Two spells used to sit on ladders they do
    // not belong to, and both were the top rung, so both were doing exactly that:
    //   Hellfire     (mage, was the 9th zap rung) — mana 1000 PLUS 70% of the pool, aether 19000, damage
    //                 ceil(magic * 2.15). A mage who ran @lvl came out holding it INSTEAD of Stormstrike.
    //   Retribution  (poet, was the 4th zap rung) — mana 500 and empties the pool, aether 24000, damage
    //                 ceil(magic * .34). Latent: Flare outranked it, so it only bit in the level band where
    //                 Retribution was the highest rung a poet qualified for.
    // Both are still LEARNED — dropping a spell from LadderRungs doesn't remove it, it exempts it from the
    // collapse, which is how every other one-of-a-kind ability (Inferno, Dooms Fire, the traps, the summons)
    // already survives. The mage now ends up with Stormstrike AND Hellfire, which is the real spellbook.
    // BuildSpellLadders enforces this at load, so a rung added back here is dropped with a log line rather
    // than quietly eating a class's attack again.
    private static readonly Dictionary<int, Dictionary<string, string[]>> LadderRungs = new()
    {
        [1] = new()   // Warrior — no zap ladder; its damage skills (Taunt/Slash/Berserk/Whirlwind) are all
        {             // different mechanics, not tiers of one.
            ["heal_self"]   = new[] { "soothe", "relief_warrior", "vigor_warrior" },
            ["heal_target"] = new[] { "fleshspeak_warrior" },
        },
        [2] = new()   // Rogue
        {
            ["zap"]         = new[] { "singe_rogue", "ignite_rogue" },
            ["heal_self"]   = new[] { "soothe", "maros_remedy_rogue" },
            ["heal_target"] = new[] { "fleshspeak_rogue", "mend_wounds_rogue", "recover_rogue", "seal_wounds_rogue" },
            // Drain is Heal-archetype but it is a life-steal ATTACK, so it is not on the heal ladder.
        },
        [3] = new()   // Mage
        {
            ["zap"]         = new[] { "thunder_bolt_mage", "spark_mage", "singe_mage", "ignite_mage", "ion_mage",
                                      "impact_mage", "call_lightning_mage", "stormstrike_mage" },
            ["zap_area"]    = new[] { "erupt_mage", "ion_charge_mage", "explode_mage", "electrocute_mage", "tempest_mage" },
            ["heal_self"]   = new[] { "soothe", "lay_hands_mage", "relief_mage" },
            ["heal_target"] = new[] { "fleshspeak_mage", "mend_wounds_mage", "recover_mage", "heal_mage", "rejuvenate_mage" },
        },
        [4] = new()   // Poet
        {
            ["zap"]         = new[] { "spark_poet", "singe_poet", "ignite_poet", "earthquake_poet", "flare_poet" },
            ["heal_area"]   = new[] { "vital_spark_poet", "anoint_poet", "remedy_poet", "heavens_kiss_poet" },
            ["heal_self"]   = new[] { "lay_hands_poet", "fortify_poet" },
            ["heal_target"] = new[] { "recover_poet", "heal_poet", "revitalize_poet", "water_of_life_poet" },
            // Poet has no Soothe rung: it is the one class the live game refuses to (re)teach it to.
        },
    };

    /// <summary>(pathId, spell key) → (ladder id, rung index), expanded from <see cref="LadderRungs"/> through
    /// the alignment families. Keyed per path because <c>soothe</c> is the bottom rung of three different
    /// classes' self-heal ladders. The rung INDEX is what <see cref="RespecSpellSet"/> ranks by — see there
    /// for why the learn level can't be trusted to order a ladder.</summary>
    private static IReadOnlyDictionary<int, Dictionary<string, (string Ladder, int Rung)>> LadderOf
    {
        get => _snapshotBuilder?.LadderOf ?? Snapshot.LadderOf;
        set => Builder.LadderOf = value;
    }

    private static Dictionary<int, Dictionary<string, (string Ladder, int Rung)>> BuildSpellLadders(
        IReadOnlyList<SpellDef> spells,
        Func<SpellDef, SpellFx?> fxFor)
    {
        var family = BuildAlignFamilies(spells);
        var byLeader = spells.GroupBy(s => family.GetValueOrDefault(s.Key, s.Key), StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, Dictionary<string, (string, int)>>();
        foreach (var (pathId, ladders) in LadderRungs)
        {
            var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ladderId, rungs) in ladders)
                for (int i = 0; i < rungs.Length; i++)
                    if (!byLeader.TryGetValue(rungs[i], out var siblings))
                        Log.Warn($"spell ladder {pathId}/{ladderId}: no spell keyed '{rungs[i]}' — rung ignored");
                    // A spell on a cooldown is a different ability, not a louder version of this one — see the
                    // "A RUNG MUST HAVE NO AETHER" note on LadderRungs. Dropping it here leaves it OFF every
                    // ladder, which means RespecSpellSet keeps it outright instead of letting it displace the
                    // class's actual attack.
                    else if (siblings.Select(fxFor).FirstOrDefault(fx => fx is not null)?.Aether is > 0 and var aether)
                        Log.Warn($"spell ladder {pathId}/{ladderId}: '{rungs[i]}' has a {aether}ms aether — " +
                                 $"not a rung, granted on its own instead");
                    else
                        foreach (var s in siblings) map[s.Key] = (ladderId, i);
            result[pathId] = map;
        }
        return result;
    }

    /// <summary>The EXACT book a character of this class/level/mark/alignment should hold — what @lvl,
    /// @class, @mark and @align rebuild to. <see cref="SpellsForClass"/> (which is also the tutor menu, and
    /// stays complete) narrowed to one rung per ladder: the HIGHEST RUNG of each that the character qualifies
    /// for, ties broken by SplId so the pick is stable across reloads.
    /// <para>Ranked by the rung's position in <see cref="LadderRungs"/>, NOT by its learn level. Level looks
    /// like the same thing and isn't: the alignment reskins carry their own <see cref="SpellCosts"/> rows and
    /// those rows do not agree with the tier order. The mage top zap is the case that proved it — Hellfire is
    /// level 99, but its Kwisin/Mingken/Ohaeng twins (Consume Soul / Flesh Eaters / Hurricane) are level 72,
    /// BELOW the 77 of the rung under them (River of Blood / Natural Disaster / Winds of Disaster). Ranking by
    /// level therefore handed every aligned mage the second-best zap and deleted the best one, while the
    /// unaligned mage — whose levels happen to ascend — got the right answer. The declared order is the
    /// authority on which rung is stronger; the level column only decides whether you have REACHED it (that
    /// gate already ran, in <see cref="SpellsForClass"/>).</para>
    /// <para><paramref name="dogFlag"/> is the character's finished-the-linguist-chain flag
    /// (<see cref="DogFlagReg"/>). When it is set, the class's Dog spells are merged in as well — see
    /// <see cref="DogSpellsFor"/>. They cannot arrive through <see cref="SpellsForClass"/>, which is also the
    /// tutor menu and must never show one, so this is the single place a rebuild can pick them up.</para></summary>
    public static List<SpellDef> RespecSpellSet(int pathId, int maxLevel, int alignment, int mark,
                                               bool dogFlag = false, int sageRung = 0)
    {
        var all = SpellsForClass(pathId, maxLevel, alignment, mark);

        // The Sage rung the character has PAID for (Content.SageRungReg), for the same reason as the Dog
        // spells below: @lvl/@class/@mark/@align rebuild rather than top up, and the ladder is bought a rung
        // at a time for 100,000 gold each with a 90-day wait between them. Without this, one @lvl silently
        // confiscated the whole thing — and unlike a tutor spell there is no way to buy it straight back.
        //
        // The rung comes from the registry rather than from the old book because the book is what is being
        // wiped. SpellsForClass can never supply it (IsNpcGrantedOnly drops all five), so this is the single
        // place a rebuild can pick one up. Level-gated inside SageSpellFor: drop to level 5 and it stops
        // applying, return to 90 and it comes back, exactly as the Dog tiers behave.
        if (SageSpellFor(sageRung, maxLevel) is { } sage) all.Add(sage);
        // The Dog spells, for a character who has finished the chain. Merged in level-stamped and re-sorted
        // into place, which is what makes "@dog 1" followed by "@lvl 70/99" produce the book a linguist of
        // this class really holds. Without this the rebuild silently forgot every Dog spell — including ones
        // earned honestly at the Dog, since @lvl/@class/@mark/@align rebuild rather than top up.
        //
        // The Dog's OWN price (the kills, the goods, and the atlas's 20,000-vita / 10,000-mana gate on the
        // level-99 tier, all in npc_dialog.lua's DOG_SPELLS) is deliberately NOT re-checked here: the rebuild
        // grants what a character of this class and level would hold, exactly as it hands over tutor spells
        // without charging the tutor. Level is the gate, as it is for every other entry in the set.
        //
        // KNOWN INTERACTION: the Dog's "cleanse" forgets the spells but keeps the legend and the flag (RTK
        // resets the teach progress instead), so a rebuild afterwards hands them straight back. That is the
        // rebuild behaving as designed — it restores the whole entitlement set — and it takes a staff command
        // to reach, so a player who cleansed still has the quest to walk again.
        if (dogFlag)
        {
            var dogs = DogSpellsFor(pathId, maxLevel);
            if (dogs.Count > 0)
                all = all.Concat(dogs).OrderBy(s => s.Level)
                         .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        // Ladders are declared per BASE class, and an NPC subpath inherits its base class's whole list, so
        // it inherits the ladders with it. The two Dog spells are deliberately not on any ladder: Fissure ->
        // Lava Surge is a tier pair, but it is two spells from a separate trainer, and collapsing it would
        // erase half of the only reward the subpath grants outright.
        if (!LadderOf.TryGetValue(PathBaseOf(pathId), out var ladders)) return all;

        var top = new Dictionary<string, (SpellDef Spell, int Rung)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
            if (ladders.TryGetValue(s.Key, out var rung)
                && (!top.TryGetValue(rung.Ladder, out var best) || rung.Rung > best.Rung
                                                               || (rung.Rung == best.Rung && s.Id > best.Spell.Id)))
                top[rung.Ladder] = (s, rung.Rung);

        // Soothe is the exception to the collapse: a rebuilt character always keeps it ALONGSIDE the best
        // self-heal it qualifies for, never instead of it. It sits at the BOTTOM rung of the Warrior/Rogue/
        // Mage heal_self ladders, so the normal top-rung filter would drop it the moment a character out-levels
        // it — but Soothe is the first-steps heal every class is expected to still have, so it is exempted here
        // the same way an aether-bearing rung is. (Poet's heal_self ladder has no soothe rung — LadderRungs[4]
        // — so Poet's Soothe, granted as a universal base spell, was already passing through untouched; this
        // clause is a no-op there.)
        return all.Where(s => s.Key.Equals("soothe", StringComparison.OrdinalIgnoreCase)
                              || !ladders.TryGetValue(s.Key, out var rung)
                              || ReferenceEquals(top[rung.Ladder].Spell, s)).ToList();
    }

    public static SpellDef? SpellById(int id) => SpellByIdIndex.TryGetValue(id, out var v) ? v : null;
    public static SpellDef? SpellByKey(string? key) => key is not null && SpellByKeyIndex.TryGetValue(key, out var v) ? v : null;

    /// <summary>The extracted RTK effect for a spell (real formula/archetype), or null if the export has no
    /// row for its identifier (⇒ caller falls back to the keyword classifier).</summary>
    public static SpellFx? FxFor(SpellDef sp) => SpellFx.TryGetValue(sp.Key, out var fx) ? fx : null;

    // Sentinel for "this spell has no pcalign arg" (skills / non-global-helper spells).
    public const int NoPcAlign = int.MinValue;

    // ---- spell effect graphic (client 0x29) + sound (0x19) ------------------------------------
    // The 4.95 client's 0x29 handler plays effect N from Effect.tbl (128 effects) over an entity - it is NOT a
    // floating damage number (proven by disassembly of 0x4504b0 -> 0x44e0a0 -> the index-into-table copy at
    // 0x4354b0). Sound rides its own 0x19 (see Session.BroadcastFx); the two are independent.
    //
    // BOTH now come straight off the spell's own row. There used to be a hardcoded `pcalign` ladder here,
    // ported from rtklua's common/global_{zap,attack,heal}.lua, used ONLY by the Damage and Heal archetypes -
    // every other archetype already carried explicit animation/sound columns. re/fill_spell_fx.py resolved that
    // ladder once into those same columns for Damage/Heal too, so all ten archetypes now work the same way and
    // `pcalign` is provenance only, never read at runtime. Doing it as data also fixed things the ladder could
    // not express (full writeup in that script's docstring):
    //   - 8 spells whose Lua passed the wrong alignment (Rain of Fire and Winds of Disaster are Ohaeng and
    //     Nature's Wounding is Mingken, but all three passed unaligned; the whole poet vital_spark family too),
    //   - 2 whose Spells.csv SplAlignment was wrong (the recover_rogue family is tagged 0,1,1,2),
    //   - Singe rendering differently for rogue than for mage,
    //   - 4 duplicate rows from a scratch copy of rogue/singe.lua.
    // To retune a spell now: edit its animation/sound cell and @reload. No rebuild, no switch statement.

    /// <summary>The Effect.tbl graphic id to play for a cast, or -1 for "no graphic".</summary>
    public static int EffectAnim(SpellFx fx, int pathId = 0) => fx.Animation != 0 ? fx.Animation : -1;

    /// <summary>The NexusTK.snd id to play for a cast, or -1 for "silent".</summary>
    public static int EffectSound(SpellFx fx, int pathId = 0) => fx.Sound != 0 ? fx.Sound : -1;

    public static SpellDef? FindSpell(string query)
    {
        query = query.Trim();
        if (int.TryParse(query, out var id))
        {
            var byId = Spells.FirstOrDefault(s => s.Id == id);
            if (byId is not null) return byId;
        }
        return BestByName(Spells, query, s => s.Name) ?? BestByName(Spells, query, s => s.Key);
    }

    public static List<SpellDef> SearchSpells(string query, int limit) =>
        RankByName(Spells, query, s => s.Name).Take(limit).ToList();

    // A spell's coarse effect category. There is NO per-spell effect data in the export (RTK runs ~900 Lua
    // scripts), so this is a best-guess keyword classifier over the name + identifier. It drives the generic
    // cast effect in Session.HandleCast: Damage spells deal magic damage, Heal spells restore HP, Buff/Utility
    // give feedback. Refine per spell later if bespoke behaviour is wanted.
    public enum SpellEffect { Utility, Damage, Heal, Buff }

    private static readonly string[] HealWords =
        { "heal", "remedy", "cure", "mend", "recover", "regen", "soothe", "bandage", "balm", "reviv",
          "rejuven", "renew", "vitali", "refresh", "restore" };
    private static readonly string[] DamageWords =
        { "bolt", "blast", "flame", "fire", "ice", "frost", "cold", "lightn", "thunder", "zap", "storm",
          "nova", "strike", "smite", "burn", "shock", "blaze", "meteor", "quake", "slash", "wrath", "doom",
          "drain", "wound", "blood", "venom", "poison", "chaos", "shard", "spike", "fang", "claw", "bite",
          "sever", "crush", "pierce", "inferno", "electr", "avalanche", "blizzard", "assault", "ambush",
          "assassin", "attack" };
    private static readonly string[] BuffWords =
        { "bless", "augment", "armor", "shield", "harden", "protect", "guard", "bolster", "fortif", "haste",
          "aegis", "barrier", "ward", "enchant", "empower", "strength" };

    /// <summary>Best-guess effect category from the spell's name/identifier keywords. Heal wins over Damage
    /// on overlap (e.g. "Healer's Revenge").</summary>
    public static SpellEffect EffectOf(SpellDef sp)
    {
        var s = (sp.Name + " " + sp.Key).ToLowerInvariant();
        bool Any(string[] words) { foreach (var k in words) if (s.Contains(k)) return true; return false; }
        if (Any(HealWords))   return SpellEffect.Heal;
        if (Any(DamageWords)) return SpellEffect.Damage;
        if (Any(BuffWords))   return SpellEffect.Buff;
        return SpellEffect.Utility;
    }

    private static readonly string[] AlignPrefixes = { "kwisin_", "mingken_", "ohaeng_" };
    private static readonly string[] ClassSuffixes = { "_peasant", "_warrior", "_rogue", "_mage", "_poet" };

    /// <summary>The spell's identifier with its sub-alignment prefix (kwisin_/mingken_/ohaeng_) and class
    /// suffix (_mage/_poet/…) stripped — so every variant of "Invoke" (invoke_mage, invoke_poet, invoke)
    /// collapses to the base key "invoke". Session.HandleCast switches on this to run bespoke per-spell
    /// effects (which a keyword category can't express, e.g. Invoke = trade HP for MP).</summary>
    public static string BaseKey(SpellDef sp)
    {
        var k = sp.Key.ToLowerInvariant();
        foreach (var pre in AlignPrefixes) if (k.StartsWith(pre)) { k = k[pre.Length..]; break; }
        foreach (var suf in ClassSuffixes) if (k.EndsWith(suf))   { k = k[..^suf.Length]; break; }
        return k;
    }

    // Class/path table: PthId -> base class name (PthMark0), PLUS the whole PthMark0..15 rank ladder into
    // PathRanks — "Warrior · Il san (W) · Ee san (W) · …" for a base class, "Ju jak · Force · Inferno ·
    // Pandemonium · Catastrophe · Ju jak" for an NPC subpath. Those higher columns are what a character is
    // actually CALLED once it has a mark, and the rank names are not decoration: the warrior mark-2 spell is
    // literally "Assault" and Chung ryong's mark-2 title is "Assault", which is the clearest evidence that
    // SplMark and PthMarkN are the same rank axis. PthType is loaded alongside into PathBase (PathBaseOf).
    private const int MaxPathRank = 15;   // Paths.csv goes PthMark0..PthMark15; only 0..5 are ever populated

    private static Dictionary<int, string[]> PathRanks
    {
        get => _snapshotBuilder?.PathRanks ?? Snapshot.PathRanks;
        set => Builder.PathRanks = value;
    }

    /// <summary>What a character of this path and mark is CALLED — Paths.csv <c>PthMark&lt;mark&gt;</c>.
    /// A Ju jak is "Force" at mark 1, "Inferno" at 2, "Pandemonium" at 3, "Catastrophe" at 4; a Warrior is
    /// "Il san (W)" at 1. Falls back to the base name (mark 0) for a blank or out-of-range column, so a rank
    /// nobody named still reads as the class rather than as an empty string.</summary>
    public static string PathTitle(int pathId, int mark)
    {
        if (!PathRanks.TryGetValue(pathId, out var ladder)) return PathName(pathId);
        if (mark > 0 && mark < ladder.Length && ladder[mark].Length > 0) return ladder[mark];
        return ladder[0].Length > 0 ? ladder[0] : PathName(pathId);
    }

    /// <summary>Resolve any class OR rank name to the path and mark it denotes: "Mage" → (3, 0),
    /// "Inferno" → (8, 2), "Il san (W)" → (1, 1). Null if it names nothing. Base names are indexed first, so
    /// a string that is one path's class name and another's rank title always resolves to the class.</summary>
    public static (int PathId, int Mark)? PathRankForName(string? name)
    {
        var n = (name ?? "").Trim();
        return n.Length != 0 && PathRankByNameIndex.TryGetValue(n, out var v) ? v : null;
    }

    private static IReadOnlyDictionary<string, (int PathId, int Mark)> PathRankByNameIndex
    {
        get => _snapshotBuilder?.PathRankByName ?? Snapshot.PathRankByName;
        set => Builder.PathRankByName = value;
    }

    /// <summary>The paths a character may actually BE: the four base classes and Peasant, plus the four NPC
    /// subpaths (Chung ryong / Baekho / Ju jak / Hyun moo). Everything else in Paths.csv is either RTK's GM
    /// branch (5 Dreamweaver, 22 Archon) or a PC subpath (10-21 Barbarian … Muse) that this server does not
    /// model — no spells, no promotion NPC, and in the PC subpaths' case a rank ladder that would silently
    /// hand out the base class's secrets under the wrong titles.</summary>
    public static readonly IReadOnlySet<int> PlayablePaths = new HashSet<int> { 0, 1, 2, 3, 4, 6, 7, 8, 9 };

    public static bool IsPlayablePath(int pathId) => PlayablePaths.Contains(pathId);

    /// <summary>The playable paths in display order, as "&lt;name&gt;" strings for a usage line.</summary>
    public static IEnumerable<string> PlayablePathNames() => PlayablePaths.Select(PathName);

    // PthId -> PthIcon, the subpath BADGE index. Read live off the user-list window 2026-08-08 (@users
    // sweep, all five columns) and it matches this column exactly: the badge is drawn RELATIVE TO THE
    // COLUMN, so one index means a different sprite per class —
    //     icon 0  (none)      base class
    //     icon 1  Barbarian / Merchant  / Diviner   / Druid
    //     icon 2  Chongun   / Ranger    / Geomancer / Monk      (Ranger draws nothing on this build)
    //     icon 3  Do        / Spy       / Shaman    / Muse
    //     icon 4  Chung ryong / Baekho  / Ju jak    / Hyun moo
    // So a character's whole user-list identity is one PthId: PthType picks the column, PthIcon the badge.
    private static Dictionary<int, int> PathIcon
    {
        get => _snapshotBuilder?.PathIcon ?? Snapshot.PathIcon;
        set => Builder.PathIcon = value;
    }

    /// <summary>Subpath badge index for a path id (Paths.csv PthIcon) — see <see cref="PathIcon"/>.</summary>
    public static int PathIconOf(int pathId) => PathIcon.GetValueOrDefault(pathId, 0);

    // PthId -> PthType, the BASE path a (sub)class descends from (RTK class_db.c classdb_path): every subpath
    // collapses onto 1 Warrior / 2 Rogue / 3 Mage / 4 Poet, e.g. Chung ryong (6) and Barbarian (10) are both
    // base 1. 0 = Peasant, 5 = Dreamweaver/Archon (RTK's GM branch, which skips every wear restriction).
    private static Dictionary<int, int> PathBase
    {
        get => _snapshotBuilder?.PathBase ?? Snapshot.PathBase;
        set => Builder.PathBase = value;
    }

    /// <summary>The base path (PthType) a class/path id descends from — RTK <c>classdb_path</c>. Unknown ids
    /// and Peasant both give 0.</summary>
    public static int PathBaseOf(int pathId) => PathBase.GetValueOrDefault(pathId, 0);

    // Rage-tier spells (RTK Scripts/wolfs_fury.lua, tigers_fury.lua, dragons_fury.lua, baekhos_rage.lua —
    // Warrior AND Rogue both progress through some of these, per-class level gates differ) — the flat
    // multiplier `player.rage` swingDamage.lua's _getPlayerSwingDamage multiplies the WHOLE swing by.
    // Any fury is refused while one is already active ("You are already benefiting from a fury."); a stronger
    // tier never replaces a weaker one (the stance_rage verb, game-data/spell_verbs.lua, as RTK's lesserFuries
    // checkIfCast). Values/levels straight from the Lua source, since
    // SplLevel is 0 for these in the export (see SpellLevelOverrides below — the real gate lives in each
    // spell's Lua requirements() function, which the CSV export never captured for Type-5 skills).
    // Loaded from game-data/SpellMods.csv (`rage` column) in Load() — see LoadSpellMods. Every fury needs its own
    // row, alignment twins included (Soul's Rage is Wolf's Fury for a Kwi-Sin character): a key with none is not a
    // fury here, and falls to arch_buff, which takes the mana and arms nothing (#334).
    private static IReadOnlyDictionary<string, int> RageAmount
    {
        get => _snapshotBuilder?.RageAmount ?? Snapshot.RageAmount;
        set => Builder.RageAmount = value;
    }

    /// <summary>The rage multiplier this spell/skill arms, or null if it isn't a rage-tier spell. See
    /// <see cref="RageAmount"/>.</summary>
    public static int? RageAmountFor(SpellDef sp) => RageAmount.TryGetValue(sp.Key, out var r) ? r : null;

    // (IsChungRyongRage lived here: a hardcoded key check that routed Chung Ryong's Rage to its Lua verb.
    // It now binds through a SpellParams.csv row like Baekho's Cunning, the spell it mirrors, so the key
    // test is gone. It is still absent from SpellMods.csv — an INCREMENTAL fury must not reach the flat
    // RageAmountFor path, whose "already benefiting from a fury" block would forbid the tier climb.)

    // RTK warrior/enchant.lua, infuse.lua, ingress.lua, vipers_venom.lua, dragons_flame.lua + rogue/
    // tigers_fortitude.lua, baekhos_blade.lua: a weapon-enchant STANCE (player.enchant). Unlike rage (which
    // swingDamage.lua multiplies the WHOLE swing by), enchant only multiplies the raw weapon-swing term
    // (s/2) — see Session.PlayerSwingRaw. All 16 identifiers share one mutual-exclusion group (RTK
    // "enchants" checkIfCast table, spellTables.lua) — casting any one while another (or itself) is already
    // active just re-prints "This spell is already active.", never stacks/upgrades (Session.CastEnchant).
    // Mana/level are hardcoded straight from each spell's own Lua (not trusted from the CSV export — same
    // Type-5-skill gap as rage/stealth/sacrifice-strikes; tigers_fortitude_rogue genuinely costs 0 mana,
    // just consumes cast components via requirements()).
    // Loaded from game-data/SpellMods.csv (`enchantAmt`/`enchantMana` columns) in Load() — see LoadSpellMods.
    //
    // LADDER RESOLVED 2026-08-04 on an EARLIEST-SOURCE-WINS rule (user decision). Three sources exist and
    // they disagree; dating them shows the values were REBALANCED UPWARD over the years, so the earliest
    // reading is the one closest to our 4.95 client (built 2001-06-29):
    //                        nexusatlas 2003/04   DarkMaverick nmails   Melalye board post (rev. 2011)
    //     Invisible                  -                    8                       9        (tswolf 2001: 5)
    //     Rage 1..6                  -            6/9/12/18/27/81           8/14/20/26/36/81
    //     Cunning 1..5               -                  4..8                     6..12
    //     Dragon's Flame            5                     5                        6
    //     Baekho's Blade           1.5                   1.5                       2
    // Every value that moved, moved UP — so the 2011 post (boards.nexustk.com/Rogues/Melalye%2007210115.html,
    // byline "Rogue Tutor Melalye" but signed Yttribium, "Reviewed 2011, by Deimos") is the LEAST era-correct
    // despite being the most detailed. Do not treat it as authoritative on magnitudes; it IS the best source
    // for STRUCTURE (which subpath rank grants which tier) and for qualitative rules.
    // WHAT WE SHIP (earliest of each):
    //     Enchant 1.5 | Infuse 2 | Ingress 3 | Viper's Venom 4 | Dragon's Flame 5 | Spirit Blade 9
    //     Baekho's Blade (rogue, Ee San) 1.5
    // Melalye's Dragon's Harness (Sam San) 8 and Chung Ryong's Wrath (Sa San) 10 do NOT exist in our 4.95
    // Spells.csv (later-era subpath content) so they are not added. spirit_blade was ADDED here — it existed
    // in Spells.csv with no SpellMods row, i.e. it was silently INERT. The row alone did not wire it: having no
    // spell_effects row either, it reached Session.ApplyCast's no-row fallback first, which now hands an enchant
    // to the same stance verb as the rest of this table.
    // Klanx/Yari (also in the DM PDF) define `Ing` as 1 none | 3 Ingress | 4 "Il san NPC" | 5 "Ee san NPC",
    // agreeing on Ingress 3. Infuse 2 / Ingress 3 / Viper's Venom 4 are unanimous across all sources.
    // NOTE baekhos_blade_rogue 1.5 now EQUALS the free tigers_fortitude_rogue despite costing 6000 mana at
    // level 99. That looks wrong but it is what both early sources say; flagged, not "corrected".
    // STILL UNRESOLVED: art_of_war — DM calls it a x4 weapon enhancer, but RTK's art_of_war.lua implements
    // something ELSE entirely (an 80-mana reveal of a mob's max health). Not wired as an enchant here.
    private static IReadOnlyDictionary<string, (double Amt, int Mana)> EnchantSpells
    {
        get => _snapshotBuilder?.EnchantSpells ?? Snapshot.EnchantSpells;
        set => Builder.EnchantSpells = value;
    }
    public static (double Amt, int Mana)? EnchantFor(SpellDef sp) => EnchantSpells.TryGetValue(sp.Key, out var e) ? e : null;

    // Rogue Invisible (+3 same-mechanic aliases per alignment: Spirit's Form/Life's Cloak/Glass Form):
    // the swing that follows gets a flat 5x damage multiplier (tswolf 8/2001, era-matched to 4.95:
    // "Invisible increases attack by 5 times"; RTK's Lua says 9x but that's a later, non-authoritative
    // rebalance), a sneak-attack bonus that then breaks the stealth —
    // see Session.PlayerSwingRaw). NOTE: this only ports the DAMAGE multiplier, not real invisibility —
    // RTK's PC_INVIS state also hides the player's sprite from other clients (clif.c), which would need
    // viewport/ShowPlayer changes this pass doesn't touch.
    private static readonly HashSet<string> StealthSpells =
        new(StringComparer.OrdinalIgnoreCase) { "invisible_rogue", "spirits_form_rogue", "lifes_cloak_rogue", "glass_form_rogue" };
    public static bool IsStealthSpell(SpellDef sp) => StealthSpells.Contains(sp.Key);

    // RTK rogue/lethal_strike.lua + desperate_attack.lua, warrior/berserk.lua + whirlwind.lua: a facing-tile
    // physical attack computed from the CASTER's OWN current HP/MP that costs the caster a big chunk of
    // their own HP the instant it lands. Each base identifier here is cast by ALL 4 of its alignment aliases
    // (Kwisin/Ming-Ken/Ohaeng flavor names only — same mechanic, same formula). RTK picks the display name, and
    // the key it sets the aether under, from the caster's OWN alignment stat, not from which alias identifier
    // was granted/cast. The `sacrifice` verb (game-data/spell_verbs.lua) does not: the name a cast shows and
    // the cooldown it arms are the cast identifier's own (ctx.spellName, ctx.spellKey). Only Whirlwind's
    // alignment-gated damage factor, HP cost and cooldown length read the caster's alignment (ctx.alignment).
    // FocusedBlow (Rogue Sam San) and Siege (Warrior Sam San) join the same family — both are "spend your own
    // vita for a big facing-tile hit". nexusatlas: Focused Blow "Takes 2/3 of current Vita in a Strong Attack.
    // The attack does 2 times current vitality in damage at 0 AC"; Siege "does a critical strike and leaves the
    // caster with 25% vita left. Damage to target is 1.875 times current vitality plus 0.5 current mana at 0 AC".
    // The sacrifice verb's FocusedBlow and Siege branches carry those numbers, with the Atlas pages' mana and
    // aethers (#328). Siege's three alignment names are below; the live Atlas warrior page lists them
    // (https://www.nexusatlas.com/spells/warrior.php, fetched 2026-10-21). Focused Blow's are not in Spells.csv.
    public enum SacrificeFamily { LethalStrike, DesperateAttack, Berserk, Whirlwind, FocusedBlow, Siege }
    private static readonly Dictionary<string, SacrificeFamily> SacrificeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["focused_blow_rogue"] = SacrificeFamily.FocusedBlow,

        // Siege + its three alignment aliases (user-confirmed): Kwi-Sin "Soul's Freedom", Ming-Ken
        // "Life's End", Ohaeng "Winter Chill". Same mechanic; as for every family here, a cast shows the name
        // of the alias that was cast (the sacrifice verb's ctx.spellName), not one picked by the caster's alignment.
        ["siege_warrior"]        = SacrificeFamily.Siege,
        ["souls_freedom_warrior"] = SacrificeFamily.Siege,
        ["lifes_end_warrior"]     = SacrificeFamily.Siege,
        ["winter_chill_warrior"]  = SacrificeFamily.Siege,

        ["lethal_strike_rogue"] = SacrificeFamily.LethalStrike, ["afterlifes_embrace_rogue"] = SacrificeFamily.LethalStrike,
        ["mingkens_judgement_rogue"] = SacrificeFamily.LethalStrike, ["calculating_blow_rogue"] = SacrificeFamily.LethalStrike,

        ["desperate_attack_rogue"] = SacrificeFamily.DesperateAttack, ["the_voids_measure_rogue"] = SacrificeFamily.DesperateAttack,
        ["beastly_frenzy_rogue"] = SacrificeFamily.DesperateAttack, ["tilting_the_balance_rogue"] = SacrificeFamily.DesperateAttack,

        ["berserk_warrior"] = SacrificeFamily.Berserk, ["no_fear_warrior"] = SacrificeFamily.Berserk,
        ["tigers_pounce_warrior"] = SacrificeFamily.Berserk, ["winds_blast_warrior"] = SacrificeFamily.Berserk,

        ["whirlwind_warrior"] = SacrificeFamily.Whirlwind, ["deaths_angel_warrior"] = SacrificeFamily.Whirlwind,
        ["natures_own_warrior"] = SacrificeFamily.Whirlwind, ["bladedance_warrior"] = SacrificeFamily.Whirlwind,
    };
    public static SacrificeFamily? SacrificeFamilyFor(SpellDef sp) => SacrificeAliases.TryGetValue(sp.Key, out var f) ? f : null;

    // ---- overhead cast shouts --------------------------------------------------------------------------------
    // A handful of warrior/rogue power strikes make the caster SHOUT a short word in blue over their own head
    // as they cast (the live game's own flavor; RTK player:talk(2, "…") without a name prefix). Berserk,
    // Whirlwind, Desperate Attack and Lethal Strike route through the sacrifice verb; Assault runs the generic
    // Damage archetype. Session.ApplyCast emits the shout (LuaShout) once the cast is confirmed. Keyed by the
    // sacrifice family for the four strikes, and by key for the Assault reskins (same DisplayName "Assault").
    private static readonly HashSet<string> AssaultShoutKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "assault_warrior", "deaths_challenge_warrior", "cold_snap_warrior", "volley_warrior", "assault",
    };
    /// <summary>The blue over-head word this spell shouts when cast, or null for the vast majority that shout
    /// nothing. See AssaultShoutKeys / the SacrificeFamily switch.</summary>
    public static string? OverheadShoutFor(SpellDef sp)
    {
        if (AssaultShoutKeys.Contains(sp.Key)) return "Assault~!";
        return SacrificeFamilyFor(sp) switch
        {
            SacrificeFamily.Berserk         => "K'YA~!",
            SacrificeFamily.Whirlwind       => "Sa-AAA~~!",
            SacrificeFamily.DesperateAttack => "Ka~!",
            SacrificeFamily.LethalStrike    => "Ka~~!",
            _                               => null,
        };
    }

    /// <summary>Physical melee power-strikes: the sacrifice family (Berserk / Whirlwind / Desperate Attack /
    /// Lethal Strike / Focused Blow / Siege) plus the vita-funded Chin-Baek warrior strikes (Slash, Assault &amp;
    /// reskins, Feral Berserk). These are swings, not spells, so <see cref="Session.HandleCast"/> plays the
    /// attack pose (0x1A type 1) rather than the magic cast pose (type 6). Everything else casts as normal.</summary>
    public static bool ShowsSwingAnim(SpellDef sp) => SacrificeFamilyFor(sp) is not null || TakesChinBaekHoRyung(sp);

    /// <summary>The 0x1A action type the caster strikes when this spell is cast: a physical strike swings
    /// (<see cref="ActionType.Attack"/>); a spell whose <c>action</c> cell is an emote-range value (9–28)
    /// casts with that body emote — e.g. the furies use 18, <see cref="ActionType.Rage"/> — and everything
    /// else uses the default magic pose. The emote-range cell is passed through as-is rather than matched
    /// against the named set: the range is the validation, exactly as before.
    /// <para><b>The return type is a plain <c>byte</c> and must stay one.</b> This is a public member and
    /// returning <see cref="ActionType"/> instead is a source-breaking change for any existing caller that
    /// assigns the result to a byte — it fails with CS0266, since there is no implicit enum-to-byte
    /// conversion. Naming the values is a readability change and does not license moving the public
    /// contract; the named constants therefore live inside the body, and callers that want the enum cast at
    /// their own end. <c>Tests/ContentApiSurfaceTests.cs</c> pins this.</para></summary>
    public static byte CastActionType(SpellDef sp)
    {
        if (ShowsSwingAnim(sp)) return (byte)ActionType.Attack;
        var fx = FxFor(sp);
        // Passed through on the RANGE alone, exactly as before — an emote-range cell need not be a value the
        // enum happens to name, so this must not become a lookup against ActionType's members.
        if (fx is not null && fx.Action >= 9 && fx.Action <= 28) return (byte)fx.Action;
        return (byte)ActionType.Magic;
    }

    // ---- Wisdom / "Listen to advice" hints -----------------------------------------------------------------
    // The periodic gameplay tips the "Listen to advice" option (0x1b sub-4) streams into the chat channel every
    // ~15 minutes (RTK pc_timer's advice[] via msg type 99 -> 11). Adapted from RTK's list to this server's own
    // systems and wording (Central Functions, board-signs, the group-exp bonus, level-5 path choice).
    private static readonly string[] AdviceHints =
    {
        "Your legend is the history of your character — from birth to every accomplishment since.",
        "Press F1 to open Central Functions and learn more about your character.",
        "Visit a trainer to learn new spells. At level 5, you may choose your path.",
        "Be courteous to your fellow players and obey the laws of the land.",
        "Subpaths are chosen at level 99 by seeking out the leader of the subpath you desire.",
        "Travel to the neighboring towns and cities to learn how to craft finer gear.",
        "Face a board and press it to read the notices there, or to post your own.",
        "Group with others to share the hunt — a party earns more experience together.",
    };
    /// <summary>A random "Listen to advice" hint, or null if the list is empty. See Session.SendAdvice.</summary>
    public static string? RandomAdvice() => AdviceHints.Length == 0 ? null : AdviceHints[Random.Shared.Next(AdviceHints.Length)];

    // Sam San one-offs that fit no existing archetype (nexusatlas 2004; the 2002-10-01 TSWolf announcement
    // names Mend Equipment "Luster return" and Spirit Salvation, i.e. these are alignment aliases whose other
    // identifiers we do not know - only the unaligned key is wired).
    public static bool IsMendEquipment(SpellDef sp) => sp.Key == "mend_equipment_warrior";

    // NPC-subpath guardian spells (their own SplPthId: 8 Ju Jak, 9 Hyun Moo). Both had Spells.csv rows but no
    // archetype, so they spent mana and did nothing. Hyun Moo Revival is the one spell MEANT to be cast while
    // dead, which is why Session.HandleCast exempts it from "Spirits can't cast spells".
    // ---- POST-CAST POOL DRAIN (a FRACTION of the mana you held when you cast) -------------------------------
    // A handful of mage nukes charge a share of the CURRENT pool on top of (or instead of) the flat per-spell
    // `mana` column, and every one of them computes both its damage AND its cost from the mana held BEFORE the
    // cast. Session.ApplyCast subtracts this after a successful cast, from the pre-cast reading (the amount is
    // evaluated first, so the ordering is safe and matches RTK's).
    //
    // <c>GateOnly</c> says what the row's own `mana` number MEANT in the RTK script: false = the shared
    // global_zap helper really did debit it, so the fraction is charged ON TOP; true = it was never spent at
    // all, only tested as a minimum-to-cast, so the archetype's debit is handed back and the fraction is the
    // whole price.
    //
    // 1.0 - "Takes all mana when cast and does that much damage times N" (nexusatlas): Inferno x1.5 (Ee San
    //   mage) and Dooms Fire x2.5 (Sam San mage). Their spell_effects rows carried mana=0 and an amountExpr
    //   reading player.magic, which computes the damage correctly but NEVER SPENT the pool - so before this
    //   they were free, repeatable nukes scaling off a mana bar that never moved. Inferno's three alignment
    //   reskins (Death's Door, Nature's Denial, Steel Storm) take the same entry: RTK mage/inferno.lua gives
    //   all four one body, ending setAether(<key>, 70000) and `player.magic = 0`. (The four are 5-way zaps,
    //   and ApplyCast's 5-way branch used to return ahead of this drain, so even Inferno's entry never applied
    //   until that branch joined the archetype tail.) The same body refuses a cast below 600 mana
    //   (inferno.lua:11-14, "You do not have enough mana."), so the four rows now carry mana=600 and the verb
    //   refuses below it for free. That 600 is a bar in RTK, not a bill, which is GateOnly's case, but the flag
    //   would change nothing here: the verb's 600 comes out of a pool of at least 600 and the drain takes the
    //   whole pre-cast reading from what is left, so the cast ends at 0 either way. They stay false. Dooms
    //   Fire's row takes the same gate at the live Nexus Atlas mage page's "Mana Cost - 15,275" and its
    //   "Aethers - 65 Seconds" (https://www.nexusatlas.com/spells/mage.php, fetched 2026-10-10; Caleb,
    //   2026-10-22, decision sheet item 5): arch_damage refuses below 15,275 for free, and from it the cast
    //   ends at 0 for the same reason, so it stays false too. RTK has no script for it. Retribution and its
    //   three reskins (RTK poet/retribution.lua, `player.magic = 0` after a successful global_zap) are the
    //   same thing one tier down: "deals 34% of current mana to target", and it empties you doing it.
    // 0.7 - Hellfire and its three alignment reskins. RTK Spells/mage/hellfire.lua takes its cost TWICE:
    //   global_zap debits the 1000 it is handed (which is the only number the formula extractor could see, and
    //   so the only one in spell_effects.csv), and then the script itself subtracts a second
    //   `manaTaken = floor(player.magic * .7)` computed from the pre-cast pool. That second debit was dropped
    //   on export, which is why the game's biggest zap - damage = ceil(mana * 2.15) - cost a flat 1000 out of
    //   a five-figure pool and read as consuming no mana at all.
    // 1/3 - Restore (RTK poet/restore.lua). Its own description is "Heals a target for 150% caster mana,
    //   removes 1/3 of caster mana", and the script does exactly that: `magic = 1000` is compared against and
    //   never subtracted, then `player.magic = ceil(player.magic * 2/3)`. So this is the GateOnly case - the
    //   1000 in the row is the bar you must clear to cast, not the bill.
    private readonly record struct ManaDrain(double Fraction, bool GateOnly);
    private static readonly Dictionary<string, ManaDrain> PostCastManaDrain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inferno_mage"] = new(1.0, false), ["dooms_fire_mage"] = new(1.0, false),
        ["deaths_door_mage"] = new(1.0, false), ["natures_denial_mage"] = new(1.0, false),
        ["steel_storm_mage"] = new(1.0, false),

        ["hellfire_mage"] = new(0.7, false),     ["consume_soul_mage"] = new(0.7, false),
        ["flesh_eaters_mage"] = new(0.7, false), ["hurricane_mage"] = new(0.7, false),

        ["retribution_poet"] = new(1.0, false),   ["spirit_puppet_poet"] = new(1.0, false),
        ["palm_of_life_poet"] = new(1.0, false),  ["tornado_poet"] = new(1.0, false),

        ["restore_poet"] = new(1.0 / 3.0, true),
    };
    /// <summary>Fraction of the PRE-CAST mana pool this spell burns after it lands, and whether its
    /// <c>spell_effects.csv</c> mana cost was a minimum-to-cast gate rather than a real debit (in which case
    /// the caller hands that back first). <c>(0, false)</c> for everything not in the table above.</summary>
    public static (double Fraction, bool GateOnly) PostCastManaDrainFor(SpellDef sp) =>
        PostCastManaDrain.TryGetValue(sp.Key, out var d) ? (d.Fraction, d.GateOnly) : (0.0, false);

    // ---- POST-CAST VITA COST (the warrior "pay in blood" strikes) -------------------------------------------
    // The self-sacrifice FAMILY (Lethal Strike / Desperate Attack / Berserk / Whirlwind / Focused Blow / Siege)
    // has always charged its share of vita — that lives in the `sacrifice` verb, which owns the whole strike.
    // These two do NOT: they are ordinary Damage-archetype spells that happen to end their RTK script by
    // assigning `player.health` a fraction of itself, which no column in the export can express. So they were
    // free — all of the damage, none of the price.
    //   Slash  (RTK warrior/slash.lua):  `endvita = math.floor(player.health * 0.90)` -> keep 90%.
    //   Assault + its 3 reskins (warrior/assault.lua): `player.health = damage`, damage = ceil(health / 2)
    //     -> keep 50%. (RTK re-uses the damage variable, so a Chin Baek Ho Ryung buff up at cast time makes it
    //     keep 75% instead — a buff that REDUCES your own cost is plainly a slip in their script, and porting
    //     it is not worth the mechanic it would break, so the flat half is what runs here.)
    //   Feral Berserk (warrior/feral_berserk.lua): `player.health = ceil(player.health * 0.3333)`. It is the
    //     one-of-a-kind upgrade the Berserk family's `on_learn` REPLACES those spells with, which is why it
    //     sits outside SacrificeAliases and missed the vita charge the family's own verb applies.
    // All three are charged only when the strike LANDS, matching RTK (slash inside its `#d > 0` branch,
    // assault and feral berserk behind their `landed == 1` flag) — a swing at empty air costs the mana only.
    private static readonly Dictionary<string, double> PostCastVitaKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slash_warrior"] = 0.90,
        ["assault_warrior"] = 0.50, ["deaths_challenge_warrior"] = 0.50,
        ["cold_snap_warrior"] = 0.50, ["volley_warrior"] = 0.50,
        ["feral_berserk_warrior"] = 0.3333,
    };
    /// <summary>Fraction of current vita left standing after this strike lands, or 1.0 (costs no vita) for
    /// everything outside the table above.</summary>
    public static double PostCastVitaKeepFor(SpellDef sp) => PostCastVitaKeep.GetValueOrDefault(sp.Key, 1.0);

    // ---- Chin-Baek-Ho-Ryung (the Black Potion's 10-second strike buff) --------------------------------------
    // RTK gives exactly five warrior strikes a x1.5 while `chin_baek_ho_ryung` is up, each with the same two
    // lines right after it computes its damage (warrior/{slash,assault,berserk,feral_berserk,whirlwind}.lua):
    //     if (player:hasDuration("chin_baek_ho_ryung")) then damage = math.ceil(damage * 1.5) end
    // The ward comes from ONE source, black_potion.lua, and nothing else in the tree sets it — which is what
    // makes this list exhaustive rather than a sample.
    //
    // Berserk and Whirlwind reach their x1.5 inside the `sacrifice` verb; the other three run the generic
    // Damage archetype, so their multiplier is applied to the evaluated amount in Session.CastArch. Both read
    // the same ward. (Before this they read `ctx.baekhoRage` — Baekho's RAGE, an ordinary fury tier, and a
    // ROGUE spell at that, on strikes only warriors can cast, so the bonus was unreachable. Two different
    // Baekhos; that primitive is gone, see the note where it used to live in Session.Spells.cs.)
    private static readonly HashSet<string> ChinBaekHoRyungStrikes = new(StringComparer.OrdinalIgnoreCase)
    {
        "slash_warrior", "feral_berserk_warrior",
        "assault_warrior", "deaths_challenge_warrior", "cold_snap_warrior", "volley_warrior",
    };
    /// <summary>The status key whose ward multiplies the five warrior strikes by 1.5 — one name, referenced by
    /// the Black Potion's ItemParams row, the strike list here, and the sacrifice verb.</summary>
    public const string ChinBaekHoRyung = "chin_baek_ho_ryung";
    /// <summary>Does this strike take the Chin-Baek-Ho-Ryung x1.5 when the ward is up? (Berserk and Whirlwind
    /// are absent on purpose — the sacrifice verb applies theirs.)</summary>
    public static bool TakesChinBaekHoRyung(SpellDef sp) => ChinBaekHoRyungStrikes.Contains(sp.Key);

    // ---- OVERFLOW from outside the sacrifice family ---------------------------------------------------------
    // The Overflow FAQ's warrior trigger list is "Slash, Berserk, Whirlwind, Siege", and Ixeus's revision adds
    // Feral Berserk by name — "the usual formulae like 0.75 x V for old Zerk, 0.85 x V for Feral Zerk". Nexus
    // Atlas agrees the list is broad: "Warrior overflow works on all of the warrior attacks, including their
    // Sam san attack" (Siege).
    //
    // Berserk / Whirlwind / Siege reach the splash from inside `verbs.sacrifice`. These two cannot: they are
    // ordinary Damage-archetype spells (their RTK scripts export cleanly, which is exactly why they are not in
    // SacrificeAliases — see PostCastVitaKeep), so the splash needs its own hook on that path. RTK gave neither
    // of them overflow — warrior/slash.lua calls removeHealthExtend and stops, with no Overflow.Cast in it at
    // all — so this list is the archive overriding the reimplementation, the same call as the PvP splash.
    //
    // Neither spell has alignment aliases (Spells.csv has exactly one row each), so the set is literal.
    // Era-gated with everything else (Era.WarriorOverflow), hence inert at the default 2001-07-09 date.
    //
    // ASSAULT IS DELIBERATELY ABSENT. It is a vita-funded warrior strike like these two and would be an easy
    // fifth entry, but no source lists it as an overflow trigger — and "absence of evidence never adds
    // content" cuts the same way here as it does for an era row.
    private static readonly HashSet<string> ArchetypeOverflowStrikes = new(StringComparer.OrdinalIgnoreCase)
    {
        "slash_warrior", "feral_berserk_warrior",
    };
    /// <summary>Does this spell splash its overkill even though it runs the generic <c>Damage</c> archetype
    /// rather than <c>verbs.sacrifice</c>? Slash and Feral Berserk only — see the note above.</summary>
    public static bool OverflowsFromDamageArchetype(SpellDef sp) => ArchetypeOverflowStrikes.Contains(sp.Key);

    // ---- flag-shaped wards that the engine ACTUALLY reads --------------------------------------------------
    // A ward set into the setDuration/hasDuration namespace does nothing on its own — it is a name and an
    // expiry. It matters only if something looks it up. Every one of these was, for a long time, written by a
    // potion and read by nobody, so drinking a Sanctuary potion or a Scroll of Immortality was a no-op the
    // player paid for. Wards with a spell twin now avoid this entirely by routing into the spell's own slot
    // (Session.ItemApplyWard); what remains here is the genuinely flag-shaped, and each name below is paired
    // with the code that reads it. ContentSmokeTests asserts no ItemParams ward escapes both lists.
    public static readonly IReadOnlySet<string> ReadStatusWards = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "purple_potion",                                        // Session.RegenTick — +20 to the vita regen term
        ChinBaekHoRyung,                                        // Session.CastArch + the sacrifice verb — x1.5
        "harden_body", "harden_body_poet", "deaths_guard_poet", // Session.DamageImmune — total damage immunity
        "lifes_protection_poet", "body_of_alignment_poet",
        "baekhos_cunning",                                      // spell_verbs.lua stance_cunning — its tier window
    };

    /// <summary>Ward categories <see cref="Session.ItemApplyWard"/> knows how to route into the spell system's
    /// own slots. A ward row naming anything else would silently apply nothing.</summary>
    public static readonly IReadOnlySet<string> ItemWardCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "deduction", "hardarmors", "protections", "bolsters", "curses", "minorcurses", "disheartens" };

    /// <summary>Every spell key carrying a post-cast pool cost, mana or vita. Both tables are keyed by hand,
    /// so a key that stops naming a real spell disables that cost SILENTLY — the spell keeps working, it just
    /// becomes free again. Exposed only so the content smoke test can assert they all still resolve.</summary>
    public static IEnumerable<string> PostCastCostKeys => PostCastManaDrain.Keys.Concat(PostCastVitaKeep.Keys);

    public static bool IsJuJakEvocation(SpellDef sp) => sp.Key == "ju_jak_evocation";
    public static bool IsHyunMooRevival(SpellDef sp) => sp.Key == "hyun_moo_revival";

    // ---- AREA (4-way) spells --------------------------------------------------------------------------------
    // The two ladders that hit the four cardinally-adjacent cells instead of a target: the mage zap line
    // (Erupt -> Ion Charge -> Explode -> Electrocute -> Tempest) and the poet heal line (Vital Spark -> Anoint
    // -> Remedy -> Heaven's Kiss), each with its four alignment reskins. Identified in the RTK Lua by the
    // literal `local x = {-1, 0, 1, 0}` cell walk (Spells/mage/{erupt,ion_charge,explode,electrocute,tempest}
    // .lua and Spells/poet/{vital_spark,anoint,remedy,heavens_kiss}.lua) — the ONLY spells in the whole tree
    // that have it, so the list below is exhaustive rather than a sample.
    //
    // A key set rather than a data column because the formula export can't see this: the extractor reads each
    // script's damage/heal expression and its global_zap/global_heal call, and both of those look identical to
    // a single-target spell. It is also why the export gave every one of them mana=0 — these debit up front,
    // outside the shared helper, so the helper's manacost argument is 0. The real per-family costs live in
    // AreaSpellMana below and are what Session.ApplyCast passes to the verb.
    //
    // Every one of them is SplType 5 (no target argument exists), which is what made the old single-target
    // dispatch answer "<name> finds no target." on every cast, spending nothing.
    private static readonly Dictionary<string, int> AreaZapMana = new(StringComparer.OrdinalIgnoreCase)
    {
        ["erupt_mage"] = 80,        ["soulstorm_mage"] = 80,       ["avalanche_mage"] = 80,        ["deluge_mage"] = 80,
        ["ion_charge_mage"] = 120,  ["crescendo_mage"] = 120,      ["flight_of_arrows_mage"] = 120,["blazing_sands_mage"] = 120,
        ["explode_mage"] = 180,     ["soul_chasm_mage"] = 180,     ["winters_vortex_mage"] = 180,  ["volcano_mage"] = 180,
        ["electrocute_mage"] = 250, ["eater_of_the_dead_mage"] = 250, ["forests_discord_mage"] = 250, ["shatter_storm_mage"] = 250,
        ["tempest_mage"] = 310,     ["dance_macabre_mage"] = 310,  ["wilding_mage"] = 310,         ["chain_lightning_mage"] = 310,
    };
    // The poet ladder is a flat 390 across all four tiers — the tiers differ only in how much they heal
    // (100 / 200 / 500 / 1000, which the export DID capture correctly in amountExpr).
    private static readonly HashSet<string> AreaHealSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "vital_spark_poet", "spirits_kiss_poet", "spark_of_health_poet", "water_of_nature_poet",
        "anoint_poet", "brothers_of_spirit_poet", "gathering_of_power_poet", "natures_family_poet",
        "remedy_poet", "brethren_of_spirits_poet", "gathering_of_the_flock_poet", "gathering_of_majesty_poet",
        "heavens_kiss_poet", "clan_of_souls_poet", "healing_hand_poet", "earths_embrace_poet",
    };
    private const int AreaHealMana = 390;

    // ---- DOG (subpath) 5-way fire: Fissure and Lava Surge ----------------------------------------------------
    // Same shape as the mage 4-way ladder -- pay the mana up front, hand the shared helper manacost 0 so it
    // neither charges again nor fires a pose per hit, then one sendAction at the end -- but with two
    // differences that make them their own family (RTK Spells/dog/fissure.lua, lava_surge.lua):
    //   * the sweep is centred on the TARGET, not the caster (`target.x + x[i]`, not `player.x + x[i]`), and
    //   * the offset list leads with {0,0}, so the target's OWN tile is hit too -- FIVE cells, not four.
    // They were reaching the plain single-target Damage archetype here, i.e. hitting exactly one thing.
    //
    // RTK's own PvP branch has a bug we deliberately do not reproduce: it casts on `hits[1]` but then sends
    // the "<caster> cast X on you." line to `target` -- the ORIGINAL target -- so in a 5-cell sweep the
    // primary victim gets a line per bystander and the bystanders get none (fissure.lua:36-38). We message
    // whoever was actually hit.
    // Identified in the RTK Lua by the literal `local x = {0, -1, 0, 1, 0}` walk, exactly as the 4-way family
    // is identified by `{-1, 0, 1, 0}` — ELEVEN entries, every one of them centred on the target. This is the
    // exhaustive list, not a sample:
    //   dog   fissure · lava_surge                                     (Mage Dog, levels 70 / 99)
    //   mage  volcanic_blast_mage                                      (Il san mark spell)
    //   mage  inferno · deaths_door · natures_denial · steel_storm     (one ladder, 4 alignment reskins)
    //   poet  earthquake · tossing_the_bones · natures_fury · groundstrike  (ditto)
    // All eleven were reaching the plain single-target Damage archetype, i.e. hitting exactly one thing.
    private static readonly HashSet<string> TargetAreaZapSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "fissure", "lava_surge", "volcanic_blast_mage",
        "inferno_mage", "deaths_door_mage", "natures_denial_mage", "steel_storm_mage",
        "earthquake_poet", "tossing_the_bones_poet", "natures_fury_poet", "groundstrike_poet",
    };

    /// <summary>The 5-way fire spells: the target's own tile plus its four sides, centred on the TARGET
    /// rather than on the caster.</summary>
    public static bool IsTargetAreaZap(SpellDef sp) => TargetAreaZapSpells.Contains(sp.Key);

    // The DOG/Il-san fire family — the subset of the eleven that Head Tutor Nussan's board entry actually
    // describes. Structurally interchangeable (volcanic_blast.lua is fissure.lua with a bigger number: same
    // 5-way walk, same 120 mana, no aether), and they are the ones carrying the two documented oddities:
    // "Can be cast extremely fast" (so: no cast delay) and "Misses sometimes if you're too far away".
    //
    // The other eight share the MECHANIC but not those properties, and nothing in the corpus says they should:
    // the Inferno ladder is gated by a 70-SECOND aether instead, and the Earthquake ladder is an ordinary
    // 90-mana poet attack. Both therefore keep the standard 1s cast delay and have no range miss.
    private static readonly HashSet<string> DogFireSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "fissure", "lava_surge", "volcanic_blast_mage",
    };

    /// <summary>The Mage Dog / Il san fire family: castable "extremely fast" (no cast delay) and the only
    /// spells whose failure mode is range rather than a deflect roll.</summary>
    public static bool IsDogFireSpell(SpellDef sp) => DogFireSpells.Contains(sp.Key);

    // ---- CAST DELAY ----------------------------------------------------------------------------------------
    // A spell's CAST DELAY occupies the same slot as a melee swing, so casting one blocks swinging (and vice
    // versa) until it expires. It is NOT the same thing as the 3/sec action budget, which zaps also pay, and
    // it is NOT an aether -- Fissure is documented at "Aethers - 0 Seconds" while still being unspammable in
    // the ordinary sense.
    //
    // THREE INDEPENDENT SOURCES, since RTK models none of this (global_zap.lua sets no aether, canCast is
    // state-only, clif_parsemagic gates only aethers/silence/deflect, and no cast-delay field exists on the
    // user struct at all -- do not go looking for it in RTK):
    //
    //  1. LIVE CAPTURE (re/spell_rate_probe.py, 2026-08-14): a held cast key yields exactly ONE "You cast
    //     <name>." per second for Spark against exactly THREE per second for Soothe -- 36 confirmations in
    //     32 one-second windows vs 86 in 25 windows of three.
    //  2. USER, TESTING LIVE: "I cannot attack and cast Singe in the same 1s window." The delay is SHARED
    //     with the swing timer, which is why it belongs in Session.Combat.cs as _nextActionTick rather
    //     than in a per-spell cooldown table.
    //  3. PLAYER FEATURE REQUEST, official Dreams board (scraped_nexus_data .../Dreams/Mornelithe06071014.html):
    //         "~ Modify Taunt so it no longer has a cast delay and doesn't interfere with swinging"
    //         "~ Modify Invisible to not have a cast delay so it doesn't interfere with swinging"
    //     This is the source that proves the mechanism is GENERAL and named. Note what it names: Taunt is a
    //     global_zap caller (pcalign 13, "zaps w/o animation"), but INVISIBLE IS NOT A ZAP AT ALL. So a cast
    //     delay is a per-spell property that some non-zaps carry too -- hence CastDelayMs rather than an
    //     IsZap predicate. Both of those spells having one is era-correct: the post is asking for them to be
    //     changed BECAUSE they have it.
    //
    // SCOPE (user, from live play): EVERY attack spell, with the Dog fire family the single exception.
    // That includes the vita/mana-funded strikes -- Whirlwind, Lethal Strike, Slash, Siege, Assault -- and
    // the big pool-burners like Hellfire and Retribution, not just the elemental ladders.
    //
    // An earlier version of this tried to separate "real zaps" from "weapon strikes" by whether the export's
    // pcalign column held a number or the string "spellFX". That is deleted, and deliberately not coming
    // back: "spellFX" is not a game concept, it is what OUR extractor emits when a script has no zap
    // alignment, so it keyed combat behaviour off an artifact of our own tooling. It also needed a special
    // case on first contact (Assault is pcalign 0 and Volley is 3 -- inside the zap range, both 5000-mana
    // weapon strikes) and it mislabelled exactly the spells named above, all of which are "spellFX".
    //
    // There is no data signal for "vita-based" to key on either: healthCost is EMPTY on all 115 Damage rows,
    // because the in-script vita costs never survived the export (the extractor read only global_zap's
    // manacost ARGUMENT -- the same gap that lost Hellfire's 70%-of-pool). So archetype is the honest test.
    /// <summary>The cast delay, live-measured at exactly one second.</summary>
    public const int ZapCastDelayMs = 1000;

    // The sacrifice reskins. Rogue: Afterlife's Embrace, Ming-Ken's Judgement and Calculating Blow are Lethal Strike,
    // and The Void's Measure, Beastly Frenzy and Tilting the Balance are Desperate Attack, under the other three
    // alignments. Warrior: No Fear, Tiger's Pounce and Wind's Blast are Berserk, and Death's Angel, Nature's Own and
    // Bladedance are Whirlwind (the 2002 Nexus Atlas warrior page lists each trio as its spell's Kwi-Sin, Ming-Ken
    // and Ohaeng names). RTK makes each a one-line wrapper over its original's cast (rogue/lethal_strike.lua,
    // desperate_attack.lua, warrior/berserk.lua, whirlwind.lua), and the sacrifice verb runs all sixteen alike, but
    // the export gave the twelve reskins bare Utility rows where the originals have Damage rows, so the archetype
    // test in CastDelayMs gave the reskins no delay at all. Caleb: they share the originals' 1s (the Rogue six on
    // 2026-09-29, the Warrior six on 2026-10-21). Named key by key rather than by sacrifice family, so a spell that
    // joins the family later does not take a delay no decision gave it.
    private static readonly HashSet<string> SacrificeReskins = new(StringComparer.OrdinalIgnoreCase)
    {
        "afterlifes_embrace_rogue", "mingkens_judgement_rogue", "calculating_blow_rogue",   // Lethal Strike's
        "the_voids_measure_rogue", "beastly_frenzy_rogue", "tilting_the_balance_rogue",    // Desperate Attack's
        "no_fear_warrior", "tigers_pounce_warrior", "winds_blast_warrior",                 // Berserk's
        "deaths_angel_warrior", "natures_own_warrior", "bladedance_warrior",               // Whirlwind's
    };

    /// <summary>How long this spell occupies the shared cast/swing slot. 0 = no cast delay, so it neither
    /// waits on a swing nor blocks the next one (it still pays the ordinary 3/sec action budget).</summary>
    public static int CastDelayMs(SpellDef sp)
    {
        // The Dog fire family is exempt on the tutor's own wording -- Head Tutor Nussan, Mages board:
        // "Aethers - 0 Seconds ... Can be cast extremely fast." That note appears on NO other attack spell
        // in the whole tutor corpus, which only means anything if the baseline attack is NOT fast -- so it
        // is corroboration for the 1s delay as much as it is an exemption from it.
        if (IsDogFireSpell(sp)) return 0;

        // INVISIBLE and its three alignment variants — the ONE non-attack family with a cast delay, and the
        // only one we have evidence for. The Dreams board post names it alongside Taunt: "Modify Invisible to
        // not have a cast delay so it doesn't interfere with swinging". Taunt needs no special case (it is a
        // Damage row), Invisible does: it is archetype Buff.
        //
        // Their export rows carry aether = 1000 -- EXACTLY the delay measured live on Spark -- and no mana at
        // all, which reads as the extractor having captured the cast delay itself into the aether column
        // rather than these having a genuinely separate 1s recast cooldown. Either way the observable
        // behaviour is the same 1s, so the slot claim is not double-charging: it just makes the wait SILENT
        // and shared with swinging, instead of the aether path's "Invisible isn't ready yet (0s)." -- a
        // message whose "(0s)" was always a sub-second remainder being floored, i.e. this same 1s.
        if (IsStealthSpell(sp)) return ZapCastDelayMs;

        // The twelve sacrifice reskins (SacrificeReskins, above) take their originals' 1s.
        if (SacrificeReskins.Contains(sp.Key)) return ZapCastDelayMs;

        var fx = FxFor(sp);
        return fx is not null && fx.Archetype == "Damage" ? ZapCastDelayMs : 0;
    }

    /// <summary>Is this one of the 4-way area spells, and if so which verb runs it and what does it cost?
    /// Null for everything else. <c>("area_zap", mana)</c> or <c>("area_heal", 390)</c>.</summary>
    public static (string Verb, int Mana)? AreaSpellFor(SpellDef sp) =>
        AreaZapMana.TryGetValue(sp.Key, out var m) ? ("area_zap", m)
        : AreaHealSpells.Contains(sp.Key) ? ("area_heal", AreaHealMana)
        : null;

    // ---- The one hold that reaches PLAYERS -------------------------------------------------------------------
    // Doze (lvl 82) and its three alignment reskins are the ONLY members of the blind/paralyze/sleep family
    // whose RTK script has a `BL_PC` branch:
    //     elseif (target.blType == BL_PC and player:canPK(target)) then …
    // Every other one — paralyze, static, blind, and Sleep (lvl 70, the cheaper-to-learn cousin that is
    // strictly stronger on monsters) — answers a player with "It doesn't work." / "Something went wrong."
    // So this is per-SPELL, not per-kind: Doze and Sleep share `debuff = sleep` and differ only here.
    // `canPK` is our IsPvpMap gate, so it lands in an arena and nowhere else.
    private static readonly HashSet<string> PlayerHoldSpells = new(StringComparer.OrdinalIgnoreCase)
        { "doze_mage", "voids_touch_mage", "still_ethers_mage", "still_waters_mage" };
    /// <summary>May this hold be cast on another PLAYER (in a PvP map)? True only for the Doze family.</summary>
    public static bool HoldHitsPlayers(SpellDef sp) => PlayerHoldSpells.Contains(sp.Key);

    // (The Sage ladder — Share Wisdom and its four upgrades, RTK Spells/common/sage.lua — needs no classifier
    // here: it is bound to the `sage_shout` verb by SpellParams rows, since its per-tier mana and cooldown are
    // the only things that differ between the five and both are plain numbers a row can carry.)

    // RTK poet/inspiration.lua family (Draw Energy/Harness Power/Combine Focus/Inspiration — 4 reskins, one
    // mechanic): drains a GROUP MEMBER's entire current mana into the caster's own pool.
    private static readonly HashSet<string> ManaStealSpells = new(StringComparer.OrdinalIgnoreCase)
        { "draw_energy_poet", "harness_power_poet", "combine_focus_poet", "inspiration_poet" };
    public static bool IsManaStealSpell(SpellDef sp) => ManaStealSpells.Contains(sp.Key);

    // RTK poet/inspire.lua family (Inspire/Share Energy/Bestow Power/Release Focus — 4 reskins): tops off
    // ANY other player's mana using the caster's own, no group requirement.
    private static readonly HashSet<string> ManaGiftSpells = new(StringComparer.OrdinalIgnoreCase)
        { "inspire_poet", "share_energy_poet", "bestow_power_poet", "release_focus_poet" };
    public static bool IsManaGiftSpell(SpellDef sp) => ManaGiftSpells.Contains(sp.Key);

    // RTK poet/dispell.lua family (Dispell/Remove Magic/Return Natural/Restore Balance — 4 reskins): a
    // chance-based FULL buff/debuff wipe ("flushDuration") on a targeted player.
    private static readonly HashSet<string> CleanseSpells = new(StringComparer.OrdinalIgnoreCase)
        { "dispell_poet", "remove_magic_poet", "return_natural_poet", "restore_balance_poet" };
    public static bool IsCleanseSpell(SpellDef sp) => CleanseSpells.Contains(sp.Key);

    // RTK poet/resurrect.lua family (Resurrect/Return Spirit/Ming-Ken Blessing/Death Undone — 4 reskins):
    // revives a dead/ghost player to full health.
    private static readonly HashSet<string> ReviveSpells = new(StringComparer.OrdinalIgnoreCase)
        { "resurrect_poet", "return_spirit_poet", "mingken_blessing_poet", "death_undone_poet" };
    public static bool IsReviveSpell(SpellDef sp) => ReviveSpells.Contains(sp.Key);

    // RTK rogue/race.lua family (Race/Spiritual Jump/Leap of Faith/Transport — 4 independently-authored
    // copies of the same mechanic, not alias-delegated like the others above): jump up to 3 tiles in the
    // faced direction, stopping at the last passable tile.
    private static readonly HashSet<string> LeapSpells = new(StringComparer.OrdinalIgnoreCase)
        { "race_rogue", "spiritual_jump_rogue", "leap_of_faith_rogue", "transport_rogue" };
    public static bool IsLeapSpell(SpellDef sp) => LeapSpells.Contains(sp.Key);

    // (Filch/Spirit's Hand/Quick Fingers/Light Touch — RTK rogue/filch.lua — no longer needs a key table here:
    // the four spells are bound to the `filch` verb by their SpellParams rows. The mechanic grabs whatever is on
    // the SINGLE tile in front of the caster, despite the description's "up to 4 tiles" claim — the Lua's own
    // loop only ever runs i=1 — and skips a tile a player is standing on, or one holding someone else's
    // looter-locked death pile. See Session.LuaFilch + verbs.filch.)

    // RTK rogue/ambush.lua (+ displacement_rogue/waylay_rogue/reflect_rogue, alias-delegated reskins):
    // "Leap over your enemy to face their back while attacking." No mana cost in the Lua at all — only
    // player:canCast(1,1,0) gates it, and repeat-use is paced by player.ambushTimer (attackSpeed-derived,
    // not a fixed RTK "aether"/cooldown).
    //
    // WE ADD NO COOLDOWN OF OUR OWN, and must not: per the user, Ambush runs on the ordinary 3-casts-per-
    // second action budget — three fire essentially instantaneously, a fourth in the same second does not.
    // That falls out for free, from two things worth not breaking: these rows are archetype Utility, so
    // Content.CastDelayMs gives them no cast delay; and Session.LuaAmbushStrike calls PlayerSwingRaw
    // directly rather than going through HandleAttack, so the leap-strike never claims the shared cast/swing
    // slot the way a real swing would (which would space them 333ms apart instead of letting all three land
    // together). An earlier comment here claimed a "short fixed cooldown" — there isn't one.
    private static readonly HashSet<string> AmbushSpells = new(StringComparer.OrdinalIgnoreCase)
        { "ambush_rogue", "displacement_rogue", "waylay_rogue", "reflect_rogue" };
    public static bool IsAmbushSpell(SpellDef sp) => AmbushSpells.Contains(sp.Key);

    // RTK warrior/watchful_eye.lua family (+ spirits_whisper/creatures_guidance/spot_unbalance, alias-
    // delegated reskins, 125 mana/25s cooldown) and dog/spot_traps.lua (Rogue's own lower-level reskin of
    // the same seeSpotTraps() mechanic, 100 mana/6s cooldown — its own export row already carries a real
    // aether value, the warrior family's doesn't: RTK's setAether(key, 25000) never made it into the CSV).
    // Reveals nearby hidden rogue-trap NPCs (dart/snare/repeating/flash/spear/poison/death/sleep) via a
    // caster-only marker item — see World.TrapsNear. Session.LuaRevealTraps.
    // (No key table: the five spells are bound to the `spot_traps` verb by their SpellParams rows.)

    // RTK rogue/judge.lua (Judge/Spiritual Advisor/Natural Talent/Appraise — 4 reskins) + rogue/spy.lua
    // (Spy/Spiritual Guide/Nature's Handiwork/Judgement Day — 4 reskins, same popup PLUS the target's
    // inventory list): a text popup of the target's class/name/level/title/might/will/grace. The judge
    // family requires the target STRICTLY lower level than the caster (`target.level >= player.level` fails);
    // the spy family allows an EQUAL level too (`target.level > player.level` fails) — a genuine, deliberate
    // difference in the Lua source, not a typo. Session.LuaDivine.
    // All eight are bound to the `divine` verb by their SpellParams rows, so no dispatch table is needed.
    // The judge/spy SPLIT still is: it is not a binding, it's a rule the verb reads through ctx.spyMode -
    // judge needs the target STRICTLY lower level, spy allows equal. See Session.LuaIsSpy.
    private static readonly HashSet<string> DivinationSpySpells = new(StringComparer.OrdinalIgnoreCase)
        { "spy_rogue", "spiritual_guide_rogue", "natures_handiwork_rogue", "judgement_day_rogue" };
    public static bool IsDivinationSpySpell(SpellDef sp) => DivinationSpySpells.Contains(sp.Key);

    // RTK rogue/set_trap.lua (dispatcher, "What trap? >" SplQuestion — Spells.csv row 2701) + the 8
    // individual set_X_trap spells (dart_trap.lua/snare_trap.lua/repeating_dart_trap.lua/flash_trap.lua/
    // spear_trap.lua/poison_dart_trap.lua/death_trap.lua/sleep_trap.lua, Spells.csv rows 2702-2709): places
    // a hidden hazard on the caster's own tile (World.PlaceTrap) that fires once a MOB steps onto it
    // (World.Tick's movement pass — see Trap/TriggerTrapLocked), then despawns. The dispatcher itself
    // spends no mana (set_trap.lua never touches player.magic) — it just re-runs the SAME level gate +
    // mana cost as casting the specific trap directly, keyed off the typed answer. Real per-kind level/mana
    // straight from each spell's own Lua, not trusted from the CSV (SplLevel is 0 for all 9 — the familiar
    // Type-5-skill export gap). NOTE: spot_traps (Spells.csv SplPthId=99) is actually a DOG/companion-pet
    // spell (rtklua/Accepted/Spells/dog/spot_traps.lua), not one a player character ever learns directly —
    // out of scope here, revisit alongside the Poet pet-summon system if pets ever cast their own spells.
    public enum TrapKind { Dart, Snare, RepeatingDart, Flash, Spear, Poison, Death, Sleep }
    // Loaded from game-data/Traps.csv (spell-side cast cost; kind = TrapKind enum name) in Load() — see
    // LoadTrapSpells. The trigger-side effect (damage/durations) stays in World.TriggerTrapLocked.
    private static IReadOnlyDictionary<string, (TrapKind Kind, int Level, int Mana)> TrapSpells
    {
        get => _snapshotBuilder?.TrapSpells ?? Snapshot.TrapSpells;
        set => Builder.TrapSpells = value;
    }
    public static (TrapKind Kind, int Level, int Mana)? TrapSpellFor(SpellDef sp) => TrapSpells.TryGetValue(sp.Key, out var t) ? t : null;
    public static bool IsTrapDispatcher(SpellDef sp) => sp.Key.Equals("set_trap", StringComparison.OrdinalIgnoreCase);

    // ERA GATE — the 8 individual set_X_trap spells above did NOT exist in 4.95. They were added by the
    // 2003-07-01 reset, two years after this client shipped (built 2001-06-29). Three archive posts that day
    // (nexus_news.md): Growl 10:48 relaying the in-character Dream Weaver board post from Eldridge ("the
    // guild masters have also devised some new spells to help rogues ... the ability to split your trap
    // spells into several spells", tagged with the standard OOC "currently under review" patch marker);
    // Rachel 16:07 with the mechanics ("Set traps spell still exists, however you can also learn each
    // individual trap spell such as 'Sleep trap' 'Dart trap' so that you don't have to type in the name");
    // Conro 18:15 confirming the launch bug that Spot traps couldn't see them (fixed 2003-10-31). The
    // NexusAtlas pages for these spells are dated 2003-11-04, but that post is Rachel's SITE-maintenance
    // list, not a patch — her 2003-10-27 corrections say the data "will be added soon". Corroborated by the
    // rogue tutor spell list itself, where every split entry is worded "Seperate form of <X>" and carries no
    // ingredient cost (`-`), i.e. written after the split, describing derivatives of the original.
    //
    // So in-era there is exactly ONE way to set a trap: cast Set Trap (row 2701) and TYPE the trap's name at
    // its "What trap? >" prompt. Nothing about the trap MECHANICS changes here and the rows stay in
    // Spells.csv/Traps.csv — the dispatcher resolves the same set_X_trap SpellDefs internally, so every trap
    // kind still works exactly as before. This gate only removes them as spells a rogue can learn from a
    // tutor (SpellsForClass -> Learn Secret / Divine Secret / @spells) and cast directly from the book.
    //
    // Off by default; a deployment that wants the post-2003 behavior sets SplitTrapSpells=1 in
    // game-data/ServerTuning.csv and runs @reload. Bladestorm/Sword's Dance/Tiger's Ambush/Cutting Edge
    // (rows 2710-2713) are NOT covered — they are a different, subpath-only mechanic (the `bladestorm` verb / set_bladestorm_trap family).
    public static bool SplitTrapSpellsEnabled => Tune("SplitTrapSpells", 0) != 0;
    private static readonly HashSet<string> SplitTrapSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "set_dart_trap", "set_flash_trap", "set_repeating_dart_trap", "set_snare_trap",
        "set_spear_trap", "set_poison_dart_trap", "set_death_trap", "set_sleep_trap",
    };
    /// <summary>True when <paramref name="sp"/> is one of the 8 post-2003 individual trap spells and the
    /// era gate is off — i.e. it may not be learned or cast directly (Set Trap still sets that trap).</summary>
    public static bool IsOutOfEraSplitTrap(SpellDef sp) => !SplitTrapSpellsEnabled && SplitTrapSpells.Contains(sp.Key);

    // set_trap.lua's own q-string match ("dart"/"snare"/"repeating"/"flash"/"spear"/"poison"/"death"/"sleep")
    // to the underlying set_X_trap identifier that TrapSpellFor understands.
    //
    // RTK's set_trap.lua PROMPTS with one string and MATCHES another: it prints `traps[i] .. " trap"` — "Snare
    // trap", "Dart trap", … — then compares `q == "snare"`. So typing back exactly what the menu just told you
    // to type falls through to the else-branch and nothing is set. Live 4.95 clients send the full label
    // (log: `dec : 0d 53 6e 61 72 65 20 74 72 61 70 00  |.Snare trap.|`), so matching RTK literally means no
    // trap can ever be set through the dispatcher. Normalise instead: lowercase, drop a trailing "trap", and
    // key off the first remaining word. That accepts "Snare trap", "snare", "SNARE TRAP" and "Repeating dart"
    // alike, and is a superset of RTK's own accepted inputs — nothing that used to work stops working.
    public static string? TrapKeyForAnswer(string answer)
    {
        var a = (answer ?? "").Trim().ToLowerInvariant();
        if (a.EndsWith(" trap", StringComparison.Ordinal)) a = a[..^5].TrimEnd();
        int sp = a.IndexOf(' ');
        if (sp > 0) a = a[..sp];              // "repeating dart" -> "repeating", "poison dart" -> "poison"
        return a switch
        {
            "dart" => "set_dart_trap", "snare" => "set_snare_trap", "repeating" => "set_repeating_dart_trap",
            "flash" => "set_flash_trap", "spear" => "set_spear_trap", "poison" => "set_poison_dart_trap",
            "death" => "set_death_trap", "sleep" => "set_sleep_trap", _ => null,
        };
    }

    // RTK rogue/bladestorm_trap.lua (Spells.csv rows 2710-2713, all 4 SplAlignment reskins byte-for-byte
    // identical Lua) — despite the similar name, a COMPLETELY different mechanic from the set_X_trap hazard
    // family above: not a hidden hit-and-forget hazard but a visible step-triggered decoy that detonates a
    // facing-cone AoE off the TRIGGER's own facing (RTK block.side), dealing ONE shared HP-PERCENT damage
    // value (not flat) to both the trigger and every mob the cone catches. The FIRST spell in this whole
    // audit where a PLAYER stepping on it also triggers it, not just a mob — RTK guards the cone's PC targets
    // with `if block.pvp > 0`, which this server has no toggle for, so that's simplified to "cone hits mobs
    // only" (the established no-PvP-damage-path precedent everywhere else in this audit); the TRIGGER's own
    // self-damage IS kept when the trigger is a player (tripping a trap isn't "PvP" the way hitting another
    // player would be), but capped to leave at least 1 HP — same "self-cost, never actually lethal" precedent
    // as the sacrifice strikes' own HP cost (verbs.sacrifice sets it through ctx:setHp, which never takes a
    // living caster below 1), since a trap tripped mid-walk has no death-flow of its own to hook cleanly.
    // Level 99, 1520 mana, 125s cooldown (RTK aether), the decoy auto-expires 21s after placement if never
    // triggered. NOT ported: the Lua's NPC heartbeat implies a 5000-mana/tick owner-upkeep drain while the
    // decoy is alive — the exact drain/early-deletion formula wasn't in the captured source, so this is a
    // documented gap (flat 1520 upfront cost only), not a guess. See Session.LuaPlaceBladestorm/
    // ApplyBladestormSelfDamage, World.CheckPlayerTrapTrigger, World.TriggerTrapLocked's "bladestorm" case.
    // (No key table: the four are bound to the `bladestorm` verb by their SpellParams rows. The trap they
    // place is still the "bladestorm" wire kind that World.TriggerTrapLocked / CheckPlayerTrapTrigger switch on.)

    /// <summary>The wire string World.Trap/TriggerTrapLocked switches on.</summary>
    public static string TrapWireKind(TrapKind k) => k switch
    {
        TrapKind.Dart => "dart", TrapKind.Snare => "snare", TrapKind.RepeatingDart => "repeating",
        TrapKind.Flash => "flash", TrapKind.Spear => "spear", TrapKind.Poison => "poison",
        TrapKind.Death => "death", TrapKind.Sleep => "sleep", _ => "dart",
    };

    // RTK "morphs" duration group (spellTables.lua) — ~29 real castable identifiers (Rogue/Mage/Druid/
    // Merchant/Chongun/Barbarian) that spend mana to set player.disguise (an ANIMAL/NPC look id drawn from
    // the Monster.epf archive, exactly like a real mob) + player.state=4 for a fixed duration. Purely
    // cosmetic in RTK's own Lua — no stat/combat effect anywhere, just the look swap + an animation.
    //
    // CONFIRMED CLIENT-ENGINE BLOCKED for the caster's OWN screen (re-investigated 2026-07-26 via FRESH
    // static disassembly of NexusTK_local.exe, not just re-reading the old sweep notes): the 0x33 handler
    // (0x44fef0) funnels EVERY renderKind branch (1/2/3) — for BOTH the self-update path (0x461e30) and the
    // peer-create path (0x461a50, which DOES honor a monster look via [ent+0x178]/[+0x17c]!) — through one
    // more, later, unconditional call: `push 0x4f2a84 (literal, the PLAYER archive); call 0x463380`. That
    // constant is hardcoded at the call site, never read from the packet, for every entity 0x33 ever builds
    // — so a 0x33 packet can *never* draw a monster sprite, self or peer, type-0 or type-1. §7.2/§16.
    //
    // BUT that only blocks 0x33. Session.ShowPlayer is the single choke point every peer re-sync path funnels
    // through (join, map-change, equip/mount refresh — Session.cs greps confirm no other path builds a peer's
    // look). Rerouting a morphed player's entry there to the SAME 0x07 Monster.epf creature-spawn already used
    // for real mobs (0x8000|look) works for every OTHER client's view: the click/Online.ById resolution in
    // HandleClickInfo checks `_world.MobById` before `_world.Online.ById`, and a morphed player is never added
    // to the mob list — only their RENDER packet changes — so clicks/party/trade keep resolving to the real
    // player unchanged. Deliberately accepted tradeoffs: the caster's own screen still shows themselves as
    // human (the confirmed wall above); a 0x07 entity carries no name field (§7.2), so a morphed player shows
    // no floating nametag to others. Session.CastMorph/RevertMorph, Session.ShowPlayer.
    //
    // Mutual exclusion: real RTK's `hasDuration(OWN_NAME)` check means casting a DIFFERENT morph while one is
    // active leaves BOTH durations ticking (a genuine RTK quirk/bug — whichever's timer lapses last wins the
    // visual). Simplified here to "any morph cancels any other, consistent state always": casting a morph
    // while a *different* one is active replaces it outright; re-casting the SAME one un-morphs (toggle).
    //
    // (Look, LookFemale, Mana, DurationMs) — LookFemale=0 means every sex uses Look (only the two
    // "mingken_mask" reskins are sex-dependent buck/doe, per gangrel.lua's `if player.sex==1`).
    // Loaded from game-data/Morphs.csv (rows with an empty `answers` column) in Load() — see LoadMorphs.
    public static IReadOnlyDictionary<string, (ushort Look, ushort LookFemale, int Mana, int DurationMs)> MorphSpells
    {
        get => _snapshotBuilder?.MorphSpells ?? Snapshot.MorphSpells;
        private set => Builder.MorphSpells = value;
    }
    public static (ushort Look, ushort LookFemale, int Mana, int DurationMs)? MorphFor(SpellDef sp) =>
        MorphSpells.TryGetValue(sp.Key, out var m) ? m : null;

    // Question-dispatched morphs: SplQuestion asks which animal, the typed answer picks the look (RTK
    // player.question, lowercased). rodent_rogue.lua never actually lowers `player.question` into a local
    // `q` before comparing (a genuine copy-paste bug in that one file vs. every sibling) — ported as
    // OBVIOUSLY intended (rabbit/squirrel), not as the RTK bug, since the bug has no gameplay purpose.
    // rodent_rogue's "rabbit" answer is look 125, NOT the 21 RTK's rodent.lua hardcodes: 21 is the HARE
    // sprite (mobs.csv `hare`/`large_hare`/`red_hare`), while the actual Rabbit — mob id 1, identifier
    // `rabbit`, plus every blue/green/orange/red/magic colour variant — is look 125. Both looks carry a few
    // mob rows named the other animal, so go by mob 1, not by a name scan.
    // wilderness_guise (Barbarian subpath, lvl 99) is RTK's odd one out: real RTK asks a MENU then chains
    // into separate recast()-only sub-spells (wolf_guise/rabbit_guise/deer_guise/sheep_guise/
    // thirsty_ogre_guise) that have no cast() of their own and are never independently castable — folded
    // directly into wilderness_guise's own answer table instead of modeling that indirection.
    // Loaded from game-data/Morphs.csv (rows with a non-empty `answers` column, "ans:look;ans:look") in
    // Load() — see LoadMorphs.
    public static IReadOnlyDictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> MorphDispatchSpells
    {
        get => _snapshotBuilder?.MorphDispatchSpells ?? Snapshot.MorphDispatchSpells;
        private set => Builder.MorphDispatchSpells = value;
    }
    public static (Dictionary<string, ushort> Answers, int Mana, int DurationMs)? MorphDispatchFor(SpellDef sp) =>
        MorphDispatchSpells.TryGetValue(sp.Key, out var m) ? m : null;
    public static bool IsMorphSpell(SpellDef sp) => MorphSpells.ContainsKey(sp.Key) || MorphDispatchSpells.ContainsKey(sp.Key);

    // RTK Poet "Call of the Wild" pet-summon family (rtklua/Accepted/Spells/poet/cotw_*.lua): 7 tiers x 4
    // alignment reskins (28 identifiers) + a 29th, cotw_giasomo_bird_poet. That 29th is NOT part of the
    // learnable ladder: it has no requirements(), no Spells.csv row and no learn cost — it is fired only by
    // the Giasomo stick's on_swing proc (see game-data/WeaponProcs.csv). Its Lua asks for mob 807,
    // which exists nowhere; RTK's OWN SQL and our mobs.csv both put giasomo_bird at 600, and every other
    // cotw id in that file matches the SQL exactly, so 807 is an isolated typo. It is wired to mob 600
    // here. (The Lua flags itself: "@TODO: I know this doesn't belong here, but the COTW structure is so
    // terrible already".) The base cotw_controller_poet is likewise not a summon — it has no cast() at all,
    // only on_takedamage_while_cast (threat redirect) and uncast (dismiss every owned pet), which is why it
    // is learned at 63 while the first actual creature comes at 68. DELIBERATELY NOT PORTED, either half:
    // 4.95 Call of the Wild creatures leave play ONLY by being killed or by their own timer (there is no
    // dismiss), and the threat side rides RTK's AI/threat.lua aggro table, which is later-server content —
    // see the protocol doc's "RTK's threat table is later-server content". RTK ships the spell disabled
    // anyway: it is the only cotw row in Spells.csv with SplActive=0 (all 14 summons are 1), so LoadSpells
    // skips it. Every
    // tier spawns a real MobDef (all 28 DO exist in mobs.csv, correctly statted) owned by the caster,
    // capped by Content.PetCapFor and expiring 300s later (World.Tick). The top two tiers, Wind dancer with
    // the three Champions and Wind warrior with the three Avatars, cost no mana and have an 8-minute
    // (480000 ms) cooldown; every lower tier costs a flat 10 mana with no cooldown. Source: Nexus Atlas's poet
    // page (Sources.csv atlas-poet-spells). RTK agrees on the cooldown (cotw_wind_dancer.lua:6,
    // cotw_wind_warrior.lua:5) but charges the dancer tier 10 mana (cotw_wind_dancer.lua:3); Caleb chose 0 on
    // 2026-09-29. The gold in RTK's requirements() is the trainer's learn price (SpellLearnCosts.csv), which
    // every tier has, not a cast cost.
    // Loaded from game-data/Pets.csv in Load() — see LoadPets.
    private static IReadOnlyDictionary<string, (string MobKey, int Level, int Mana, int CooldownMs)> PetSpells
    {
        get => _snapshotBuilder?.PetSpells ?? Snapshot.PetSpells;
        set => Builder.PetSpells = value;
    }
    public static (string MobKey, int Level, int Mana, int CooldownMs)? PetSpellFor(SpellDef sp) =>
        PetSpells.TryGetValue(sp.Key, out var p) ? p : null;

    /// <summary>RTK cotw_spawnCheck's live-pet cap: 4 normally, 6 at level 90+, 8 at level 99.</summary>
    public static int PetCapFor(int level) => level >= 99 ? 8 : level >= 90 ? 6 : 4;

    // SplLevel is 0 for every rage/stealth/sacrifice-strike/mana-transfer/cleanse/revive/leap spell above
    // (and, going by that, likely many other Type-5 skills) in the export — their real level gate lives in
    // each spell's Lua requirements() function, which re/extract_spell_formulas.py never captured for skills
    // (only spells with a static formula). This overrides just the ones this pass wires up; the general
    // "skills learn at level 0" gap for every OTHER skill is a separate, broader export-completeness issue,
    // not fixed here.
    // Loaded from game-data/SpellLevels.csv in Load() — see LoadSpellLevels. Assigned BEFORE Spells is
    // loaded (LoadSpells reads it to override SplLevel for Type-5 skills whose export level is 0).
    private static IReadOnlyDictionary<string, int> SpellLevelOverrides
    {
        get => _snapshotBuilder?.SpellLevelOverrides ?? Snapshot.SpellLevelOverrides;
        set => Builder.SpellLevelOverrides = value;
    }
}
