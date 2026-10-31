using MoonSharp.Interpreter;
using Shared;

namespace Server;

/// <summary>
/// The Lua-facing facade a spell verb (<c>game-data/spell_verbs.lua</c>) uses to act on the game — the
/// spell-cast analogue of <see cref="NpcContext"/>. Created per cast, bound to one caster + target. It exposes
/// a small, safe set of engine primitives (spend mana, damage the target, heal, speak) plus read-only caster
/// stats, so a verb reads as linear script:
/// <code>if ctx:spendMana(row.mana) then ctx:damage(row.base + ctx.will * row.coeff) end</code>
/// Member names are deliberately lower-cased to read naturally from Lua (<c>ctx.will</c>, <c>ctx:damage(n)</c>).
/// Every primitive delegates to an existing <see cref="Session"/> path (e.g. <c>_world.TryDamage</c>), so the
/// Lua route shares the exact same combat/heal plumbing as the C# <c>CastX</c> methods — no divergent logic.
/// </summary>
[MoonSharpUserData]
public sealed class SpellContext
{
    private readonly Session _s;
    private readonly SpellDef _sp;
    private readonly uint? _targetId;
    private readonly SpellFx? _fx;   // the spell's export row (buff stats / duration / chance) for archetype verbs

    /// <summary>What this cast strikes, resolved ONCE: the damage resolver's answer (<c>Session.ResolveDamageTarget</c>
    /// — an aimed id directly, else the faced tile, creature first), taken the first time a verb asks and kept for
    /// the rest of the cast. <see cref="hasTarget"/>, <see cref="targetHp"/>, <see cref="fxTarget"/> and
    /// <see cref="damage"/> all read it, so a verb that reads its victim and then strikes it names one victim, or
    /// none.
    /// <para>They used to resolve separately, and the resolvers disagree on an id (#343's review, F2). Drain read its
    /// creature's HP through the cast-target resolver, which falls back to the faced creature for an id that names no
    /// creature, then struck through this one, which does not: aimed at an id nobody has, it healed its caster off
    /// the creature it was facing and left that creature untouched, for 60 mana, as often as you liked. Kamikaze
    /// asked the same cast-target resolver whether it had a target, then blasted nothing and left its caster on
    /// 10 HP. The damage resolver is the one whose answer the blow always used, so it is the one kept.</para>
    /// <para>A victim the blow cannot hurt is none: an NPC resolves, but <c>World.TryDamage</c> refuses it, so
    /// <c>Session.LuaDamageVictim</c> answers no victim for one (#348's review, F1). Drain and Kamikaze then refuse it
    /// as they refuse no creature, before they spend anything.</para>
    /// <para>So do the other verbs that strike it (#349's review, F1). <see cref="hasDamageTarget"/> (the
    /// <c>generic</c> verb's gate) and <see cref="magicDamage"/> (<c>arch_damage</c>) read this victim too, so each
    /// refuses an NPC as it refuses a cast that finds nothing. <c>magic_damage</c> spends before it strikes, so it asks
    /// <see cref="victimIsNpc"/>, <see cref="castAtNothing"/> and then <see cref="canStrike"/> before it spends.</para>
    /// <para>Per cast because a <c>SpellContext</c> is: <c>Session.ApplyCast</c> builds a new one for every cast,
    /// a queued or proc cast included.</para></summary>
    private (Mob? mob, Session? pc) Victim
    {
        get
        {
            if (_victim is null) { _victim = _s.LuaDamageVictim(_targetId, out bool npc); _victimIsNpc = npc; }
            return _victim.Value;
        }
    }
    private (Mob? mob, Session? pc)? _victim;
    private bool _victimIsNpc;

    /// <summary>What this cast's curse or hold lands on, resolved ONCE, the way <see cref="Victim"/> is: the hostile
    /// status resolver's answer (<c>Session.LuaStatusTarget</c>: an aimed id on the caster's map, else the faced tile,
    /// player first, else on a PvP map the caster), taken the first time a verb asks and kept for the rest of the cast.
    /// <see cref="targetKind"/>, <see cref="canCurse"/>, <see cref="hasStatus"/>, <see cref="alreadyCast"/>,
    /// <see cref="applyCurse"/>, <see cref="canHold"/>, <see cref="holdTarget"/> and <see cref="amplify"/> all read it,
    /// so the target whose slot and map the verb's questions checked is the one its write lands on, or nobody.
    /// <para>They used to resolve separately (#343's review, F1). A target that changed between two of them was
    /// replaced by what the later one found: the faced tile, a player who had stepped onto it, or the caster. Each
    /// read now passes through <c>Session.LuaStatusTargetHere</c>, so a target that has gone since (a player off the
    /// caster's map or logged off, a creature dead or taken off the map) is nobody, never a substitute.</para></summary>
    private Session.StatusTarget StatusTarget => _s.LuaStatusTargetHere(_statusTarget ??= _s.LuaStatusTarget(_targetId));
    private Session.StatusTarget? _statusTarget;

    /// <summary>Who this cast's heal lands on, resolved ONCE, as <see cref="StatusTarget"/> is for a curse: the heal
    /// resolver's answer (<c>Session.LuaResolveHealTarget</c>: the caster for a self-skill, else the aimed id or the
    /// faced tile, else the caster), taken the first time a verb asks and kept for the rest of the cast.
    /// <see cref="healReach"/>, <see cref="healIsGhost"/> and <see cref="healTarget"/> all read it, so Shaman's solace
    /// checks the reach and the ghost of the one target it heals.</summary>
    private Session.HealTarget HealTarget => _healTarget ??= _s.LuaResolveHealTarget(_sp, _targetId);
    private Session.HealTarget? _healTarget;

    internal SpellContext(Session s, SpellDef sp, uint? targetId, string? answer)
    {
        _s = s; _sp = sp; _targetId = targetId; this.answer = answer ?? "";   // set the PROPERTY, not the param (was leaving ctx.answer null)
    }

    /// <summary>A periodic tick's context (<c>Session.TickSpellTicks</c>): the victim is the creature the tick runs on,
    /// on the map it was struck on, given here and never resolved (no faced tile, no fallback), so a creature that has
    /// died is no stand-in for the one the tick was started on. The verb reads it through the members that read
    /// <see cref="Victim"/>; the target-resolving ones (<see cref="healTarget"/>, the curse and buff sets) mean nothing
    /// in a tick.</summary>
    internal SpellContext(Session s, SpellDef sp, Mob tickVictim, ushort tickMap)
        : this(s, sp, null, null)
    {
        _victim = (tickVictim, null);
        _victimMap = tickMap;
    }

    /// <summary>A run's beat on another player (Song of the Goddess; <c>Session.RunDueSpellTicks</c>): the victim is that
    /// player, wherever they stand, given here and never resolved, and <paramref name="tickGen"/> is the count of their
    /// ended durations the run began at, which <see cref="healRunVictim"/> hands on so a run their death or a Dispell has
    /// ended heals no more.</summary>
    internal SpellContext(Session s, SpellDef sp, Session tickPlayer, long tickGen)
        : this(s, sp, null, null)
    {
        _victim = (null, tickPlayer);
        _tickGen = tickGen;
    }
    private readonly long _tickGen;

    /// <summary>The map this cast's victim stands on: a tick's own map, else the caster's (every resolver here finds a
    /// victim on the caster's map only).</summary>
    private ushort VictimMap => _victimMap ?? _s.CharMap;
    private readonly ushort? _victimMap;

    /// <summary>Archetype-path ctor: the engine has already resolved this spell's mana cost and evaluated its
    /// per-spell formula (spell_effects.csv <c>amountExpr</c>), so an archetype verb (arch_damage/arch_heal/…) can
    /// stay pure logic — <c>ctx.amount</c> / <c>ctx.mana</c> carry the numbers, no formula duplicated into Lua.
    /// <paramref name="fx"/> is the spell's export row so buff/debuff verbs can read buffStat/buffAmt/duration/chance.</summary>
    internal SpellContext(Session s, SpellDef sp, uint? targetId, string? answer, double amount, double mana, SpellFx? fx = null)
        : this(s, sp, targetId, answer)
    {
        this.amount = amount; this.mana = mana; _fx = fx;
    }

    /// <summary>The engine's own refusal notice, for the case the verb could not speak for itself: it raised.
    /// Internal, not part of the Lua surface — a verb that wants to decline says so with <c>ctx:say</c> and
    /// returns false. See <see cref="SpellScript.Run"/>'s Errored path, the only caller.</summary>
    internal void Refuse(string msg) => _s.LuaSay(msg);

    /// <summary>Engine-evaluated effect amount for the archetype path (the spell's real formula result).</summary>
    public double amount { get; }
    /// <summary>Engine-resolved mana cost for the archetype path.</summary>
    public double mana   { get; }

    // ---- archetype-row fields (Lua: ctx.buffStat, ctx.durationMs, …) for the Buff/TargetBuff/Debuff verbs ----
    /// <summary>The spell's display name, for the notices a verb does send ("&lt;name&gt; isn't ready yet.").</summary>
    public string spellName  => _sp.Name;
    /// <summary>The spell's stable key (for keying its own cooldown/duration, e.g. a ward's aether).</summary>
    public string spellKey   => _sp.Key;
    /// <summary>Buff stat key(s), '|'-separated for multi-stat buffs (e.g. "might" or "might|hit"). "" if none.</summary>
    public string buffStat   => _fx?.BuffStat ?? "";
    /// <summary>Buff amount(s), '|'-separated, parallel to <see cref="buffStat"/> (deduction uses a fractional
    /// multiplier here, e.g. "0.5"). Split/parse in Lua so the per-stat loop is scriptable.</summary>
    public string buffAmt    => _fx?.BuffAmt ?? "";
    /// <summary>Buff/debuff duration in ms from the export (0 if unset — the verb supplies its archetype default).</summary>
    public int    durationMs => _fx?.DurationMs ?? 0;
    /// <summary>Debuff take-hold chance as a percent, evaluated against the resolved target's stats (100 if the
    /// export has no chance formula). Read-only; re-resolves the target each access (deterministic within a cast).</summary>
    public double chance     => _s.LuaDebuffChance(_sp, _targetId);

    // ---- read-only caster stats (Lua: ctx.will, ctx.level, ...) ----
    public double level     => _s.LuaLevel;
    public double will      => _s.LuaWill;
    public double grace     => _s.LuaGrace;
    public double might     => _s.LuaMight;
    public double hp        => _s.LuaHp;
    public double maxHp     => _s.LuaMaxHp;
    public double mp        => _s.LuaMp;
    /// <summary>Is this cast's victim (<see cref="Victim"/>) a creature? Mobs only: an id naming a player is a
    /// victim, but not one this answers yes to. An NPC is no victim, so this answers no for one.</summary>
    public bool   hasTarget => _s.LuaHasTarget(Victim);
    /// <summary>Is there anything a DAMAGE spell could land on — a mob, a peer, or (PvP map, unaimed) you?
    /// Use this and not <see cref="hasTarget"/> in front of a <c>ctx:damage</c>, which resolves mobs only.
    /// It reads this cast's victim (<see cref="Victim"/>), the one the blow strikes, so an NPC is no target.</summary>
    public bool   hasDamageTarget => _s.LuaHasDamageTarget(Victim);
    /// <summary>Did this cast's victim (<see cref="Victim"/>) resolve to an NPC, which no blow can hurt and which the
    /// victim therefore answers as none? <c>magic_damage</c> refuses one first, before it looks at its mana, as #349's
    /// follow-up made it.</summary>
    public bool   victimIsNpc { get { _ = Victim; return _victimIsNpc; } }
    /// <summary>The player's typed answer to the spell's question prompt (empty if none).</summary>
    public string answer    { get; }

    // ---- primitives (Lua: ctx:spendMana(n), ctx:damage(n), ...) ----
    /// <summary>Check + debit the caster's mana; false (with a "not enough mana" notice) if short.</summary>
    public bool spendMana(double amt)   => _s.LuaSpendMana((int)amt, _sp);
    /// <summary>Damage this cast's victim (<see cref="Victim"/>), a mob or a player (handles deflect/HP-bar/death/XP,
    /// and the PvP gate for a player). False if there is none. The amount goes over unrounded, to the one floor
    /// at the armour step (#78): mage Bolt's 50 + level/2 (<see cref="spellAmount"/>) keeps its half at an odd level,
    /// as the rest of the Spark family's does through <see cref="magicDamage"/>.</summary>
    public bool damage(double amt)      => _s.LuaDamageTarget(amt, _sp, Victim);
    /// <summary>May <see cref="damage"/> strike this cast's victim (<see cref="Victim"/>) here? A verb that spends
    /// before it strikes (<c>magic_damage</c>) asks it first, so a blow that would strike nothing costs nothing. Off a
    /// PvP map a cast that finds nothing is refused, silently, and a player with "You can't attack that target.". On a
    /// PvP map it answers yes to everything <see cref="damage"/> took there before, a cast that finds nothing included
    /// (see <c>Session.LuaCanStrike</c>).</summary>
    public bool canStrike()             => _s.LuaCanStrike(_sp, Victim);
    /// <summary>Is this cast aimed at nothing: no victim (<see cref="Victim"/>), or, on a PvP map, the caster only as
    /// the resolver's fallback for a cast aimed at nothing? <c>magic_damage</c> asks it first, before its mana, and
    /// refuses silently and for free (owner, 2026-10-21; RTK's zap returns there first, global_zap.lua:8-10). An id
    /// aimed at the caster is not nothing. See <c>Session.LuaCastAtNothing</c>.</summary>
    public bool castAtNothing           => _s.LuaCastAtNothing(_sp, Victim, _targetId);
    /// <summary>How many tiles this cast's victim (<see cref="Victim"/>) stands from the caster on the larger of the two
    /// axes (RTK's <c>distanceSquare</c>), or -1 when there is none. Earth strike and Nature's smite read it for their
    /// 10-tile reach (RTK <c>Spells/Subpaths/Druid/earth_strike.lua:10-14</c>).</summary>
    public double victimDistance        => _s.LuaVictimDistance(Victim);
    /// <summary>Full magic-attack sequence matching the C# Damage archetype exactly: mana check → this cast's victim
    /// (<see cref="Victim"/>; an NPC is none) → spend mana → deflect roll → apply. Returns false if the cast couldn't
    /// happen (no mana, with a notice; no victim, silently), true otherwise (including a deflect). Used by
    /// arch_damage. The amount goes over unrounded: the Spark family's half (50 + level/2) is netted at the armour
    /// step, where the live probe reads it (#200).</summary>
    public bool magicDamage(double amt, double manaCost) =>
        _s.LuaMagicDamage(amt, (int)manaCost, _sp, Victim);
    /// <summary>Heal the caster's own HP (capped at max), with the spell's sparkle fx.</summary>
    public void heal(double amt)        => _s.LuaHeal((int)System.Math.Round(amt), _sp);
    /// <summary>Heal whoever this cast is AIMED at — another player, a mob (your pet), or yourself — with the
    /// spell's fx drawn over them. Type-5 self-skills always heal the caster; only Type-2 "Which target? &gt;"
    /// spells redirect. Falls back to the caster when nothing is targeted. It heals this cast's one heal target
    /// (<see cref="HealTarget"/>), the one <see cref="healReach"/> and <see cref="healIsGhost"/> read.</summary>
    public void healTarget(double amt)  => _s.LuaHealTarget((int)System.Math.Round(amt), _sp, HealTarget);
    /// <summary>How many tiles this cast's heal target (<see cref="HealTarget"/>) stands from the caster, on the
    /// larger axis (RTK's <c>distanceSquare</c>); 0 for the caster, -1 for an NPC (RTK runs no script for one).
    /// Shaman's solace refuses beyond its 10 tiles, silently and for free.</summary>
    public double healReach             => _s.LuaHealReach(HealTarget);
    /// <summary>Is this cast's heal target (<see cref="HealTarget"/>) a ghost? Shaman's solace answers one "You need
    /// a different type of cure." (RTK <c>shamans_solace.lua:23-26</c>).</summary>
    public bool   healIsGhost           => _s.LuaHealIsGhost(HealTarget);
    /// <summary>Current HP of this cast's victim (<see cref="Victim"/>), 0 if it isn't a living mob the blow can hurt
    /// (an NPC is no victim) — Drain's "is it weak enough" test and the amount of life it yields.</summary>
    public double targetHp              => _s.LuaTargetMobHp(Victim);
    /// <summary>Play this spell's own anim/sound over this cast's victim (<see cref="Victim"/>) instead of the caster.</summary>
    public void fxTarget()              => _s.LuaFxTarget(_sp, Victim);
    /// <summary>Restore the caster's own mana (capped at max).</summary>
    public void restoreMana(double amt) => _s.LuaRestoreMana((int)System.Math.Round(amt));
    /// <summary>Apply a timed stat buff (e.g. "might"/"dam"/"hit") of <paramref name="amount"/> for
    /// <paramref name="durationMs"/> ms; re-casting the same spell refreshes it.</summary>
    public void buff(string stat, double amount, double durationMs) =>
        _s.LuaBuff(stat, (int)System.Math.Round(amount), (int)System.Math.Round(durationMs), _sp);
    /// <summary>Status-box text (RTK sendminitext).</summary>
    public void say(string msg)         => _s.LuaSay(msg);
    /// <summary>Chat-log message.</summary>
    public void message(string msg)     => _s.LuaMessage(msg);

    // ---- primitives for COMPOSED / stateful verbs (Baekho's Cunning) ----
    /// <summary>Read a per-caster integer registry value (RTK <c>player.registry</c>,
    /// <see cref="QuestState.Registry"/>; 0 if unset). Persisted: see the registry note in Session.Spells.cs.</summary>
    public int  reg(string key)                       => _s.Quest(QuestState.Registry).Get(key);
    /// <summary>Set a per-caster integer registry value (persists).</summary>
    public void setReg(string key, double v)          => _s.Quest(QuestState.Registry).Set(key, (int)v);
    /// <summary>Is a named duration (RTK setDuration) still running? One namespace shared with the item
    /// verbs, so a spell sees a potion's ward — which is how the five warrior strikes read the Black Potion's
    /// <c>chin_baek_ho_ryung</c>, exactly as RTK's scripts do.</summary>
    public bool hasDuration(string key)               => _s.LuaHasDuration(key);
    /// <summary>Start/refresh a named duration for <paramref name="ms"/> milliseconds.</summary>
    public void setDuration(string key, double ms)    => _s.LuaSetDuration(key, (int)ms);
    /// <summary>Milliseconds left on a named duration (0 if not running) — for a tiered spell re-arming its
    /// effects onto the run it is already inside instead of opening a new window.</summary>
    public double durationLeft(string key)            => _s.LuaDurationLeft(key);
    /// <summary>The caster's effective armour, clamped to RTK's [-80, 70] — Harden Body's success roll
    /// scales with it (better armour is MORE negative, hence better odds).</summary>
    public double armor                               => _s.ItemArmor;
    /// <summary>Is the caster currently untouchable (any Harden Body ward, spell or scroll)?</summary>
    public bool immune                                => _s.DamageImmune;
    /// <summary>The self-only cast pose (RTK action 6).</summary>
    public void castPose()                            => _s.ItemCastPose();
    /// <summary>Is a named cooldown (RTK aether) still ticking?</summary>
    public bool onCooldown(string key)                => _s.LuaOnCooldown(key);
    /// <summary>Start a named cooldown for <paramref name="ms"/> milliseconds.</summary>
    public void setCooldown(string key, double ms)    => _s.LuaSetCooldown(key, (int)ms);
    /// <summary>Arm the melee rage multiplier (whole-swing ×amount) for <paramref name="durMs"/> ms.</summary>
    public void rage(double amount, double durMs)     => _s.LuaSetRage((int)amount, (int)durMs, _sp.Name);
    /// <summary>Arm Baekho's Cunning damage-reduction (its own slot, independent of the Sanctuary line — Sanctuary
    /// overrides it while up, then it re-asserts). <paramref name="mult"/> is the incoming-damage multiplier.</summary>
    public void deduction(double mult, double durMs)  => _s.ApplyCunningDeduction(mult, (int)durMs);
    /// <summary>Arm (on=true) or clear a positional stance ("backstab"/"flank") for <paramref name="durMs"/> ms.</summary>
    public void stance(string name, bool on, double durMs) => _s.LuaStance(name, on, (int)durMs);
    /// <summary>Play a cast animation + sound on the caster (Effect.tbl anim id, sound id).</summary>
    public void fx(double anim, double sound)         => _s.LuaFx((int)anim, (int)sound);

    // ---- Tier-2 combat-stance primitives (rage / enchant / stealth verbs) ----
    /// <summary>Is a fury/rage tier already active? (RTK blocks casting another while one runs.)</summary>
    public bool rageActive                            => _s.LuaRageActive;
    /// <summary>Is an enchant tier already active? (RTK blocks re-casting while one runs.)</summary>
    public bool enchantActive                         => _s.LuaEnchantActive;
    /// <summary>Arm the Rogue stealth burst (next swing ×9, then strips) for <paramref name="durMs"/> ms.</summary>
    public void armStealth(double durMs)              => _s.LuaSetStealth((int)durMs);
    /// <summary>Arm an enchant tier (multiplies only the raw weapon-swing term). Runs until the weapon comes
    /// off or the character logs out — there is no duration. <paramref name="durMs"/> is accepted and IGNORED
    /// purely so an older hot-reloaded spell_verbs.lua still calling the two-arg form keeps working.</summary>
    public void armEnchant(double amount, double durMs = 0) => _s.LuaSetEnchant(amount);

    /// <summary>Apply a mob-only venom DoT (MaxHp×1% per 1.5s, per-tick clamped to <paramref name="tickCap"/>,
    /// for 1 + random(<paramref name="lowMs"/>, <paramref name="highMs"/>) ms). False if no mob target or it's
    /// already venomed (a notice was sent) — the verb then spends no mana. <paramref name="flatTick"/> &gt; 0
    /// substitutes a FIXED per-tick amount for the percentage (Burn's hardcoded 1000).</summary>
    public bool applyVenom(double tickCap, double lowMs, double highMs, double flatTick = 0,
                          double pcDps = 1000, double pcDurMs = 0, double pcPerTick = 0) =>
        _s.LuaApplyVenom((int)tickCap, (int)lowMs, (int)highMs, _sp, _targetId,
                         (int)flatTick, (int)pcDps, (int)pcDurMs, (int)pcPerTick);

    /// <summary>Which hold this Debuff spell is, from its export row: "blind" · "paralyze" · "sleep" · "slow".</summary>
    public string debuffKind => _s.LuaDebuffKind(_sp);

    /// <summary>Apply a hostile categorised hold to the faced/targeted MOB. <paramref name="category"/> is the
    /// exclusivity slot ("blinds"/"paras"/"sleeps"/"slows") — a second cast is REFUSED while it runs, which is
    /// what stops a hold being chain-cast; <paramref name="hold"/> freezes it, <paramref name="blind"/> takes
    /// its sight; <paramref name="repeatFxMs"/> &gt; 0 re-draws the spell's animation on that cadence for the
    /// whole duration (RTK <c>while_cast</c>). Mob-only — a PC gets "It doesn't work.", per RTK.
    /// False (no mana spent) on no target or an occupied slot. It reads this cast's one target
    /// (<see cref="StatusTarget"/>), the one <see cref="canHold"/> asked about, or nobody if that target has gone.</summary>
    public bool holdTarget(string category, double durMs, bool hold, bool blind, double repeatFxMs) =>
        _s.LuaHoldTarget(category, (int)durMs, hold, blind, (int)repeatFxMs, _sp, StatusTarget);

    /// <summary>May <see cref="holdTarget"/> land on what this cast resolves to? Its own refusals, asked without
    /// holding anything: a player for anything but the Doze family ("It doesn't work."), a player off a PvP map
    /// ("You can't attack that target."), an NPC ("It doesn't work."), nothing at all (silent), and an occupied
    /// <paramref name="category"/> slot. Each says its line and spends nothing. Ask it before anything that can
    /// charge — the deflect, the take-hold roll — as <c>curse</c> asks <see cref="canCurse"/> before its roll.
    /// Reads this cast's one target (<see cref="StatusTarget"/>).</summary>
    public bool canHold(string category) => _s.LuaCanHoldTarget(category, _sp, StatusTarget);

    /// <summary>Arm the sleep-family damage amplifier on whatever this cast just held: the NEXT attack on
    /// them is multiplied by <paramref name="mult"/> (Doze 1.3x, Sleep 1.5x per NexusAtlas), then it's
    /// spent. Works on a player or a creature. Reads this cast's one target (<see cref="StatusTarget"/>), the one
    /// the hold landed on, or nobody if it has gone since.</summary>
    public void amplify(double mult, double durMs) => _s.LuaAmplify(mult, (int)durMs, StatusTarget);

    /// <summary>Mind-control the faced/targeted mob for <paramref name="durMs"/> ms (RTK endear): it becomes
    /// yours via the same ownership the pet system uses, then reverts to a normal world mob when the timer
    /// lapses. False (with the RTK notice) for a boss, an already-owned mob, or no target.</summary>
    public bool charmTarget(double durMs) => _s.LuaCharmTarget((int)durMs, _sp, _targetId);

    /// <summary>Make the target mob forget YOU for <paramref name="durMs"/> (a boss for
    /// <paramref name="bossDurMs"/>): your threat on it is wiped and it stops picking you. RTK amnesia.
    /// <paramref name="chance"/> is the take-hold percent (a miss still spends mana — returns true — and just
    /// prints that the creature shook it off; it applies no status, so re-casting is always allowed).</summary>
    public bool amnesiaTarget(double durMs, double bossDurMs, double chance) =>
        _s.LuaAmnesia((int)durMs, (int)bossDurMs, (int)chance, _sp, _targetId);

    /// <summary>Confuse the target mob: a <paramref name="chance"/>% aggro RESET (NOT a status). On success its
    /// whole threat table is wiped and it forgets everyone; a creature on an adjacent tile is turned on instead,
    /// so two mobs side by side (blind them first) can be spammed into fighting each other. A miss still spends
    /// mana (returns true) and just says the creature resisted. False only when there is no legal target.</summary>
    public bool confuseTarget(double chance) => _s.LuaConfuse((int)chance, _sp, _targetId);

    /// <summary>Shout as the caster — an over-head chat bubble everyone on the map sees (RTK player:talk).</summary>
    public void talk(string msg) => _s.LuaTalk(msg);

    /// <summary>Damage everything on the four cardinally-adjacent cells (the mage 4-way zap ladder). Mobs
    /// always, other players only on a PvP map, never the caster. Returns how many were hit — casting at empty
    /// air legitimately returns 0 and is still a successful cast.</summary>
    public double areaZap(double amt) => _s.LuaAreaZap((int)System.Math.Round(amt), _sp);

    /// <summary>The dog 5-way fire (Fissure / Lava Surge): the target's own tile plus its four sides, full
    /// damage on each, centred on the TARGET rather than on the caster. Returns how many were hit; 0 also
    /// covers a range miss, which is this spell's only failure mode.</summary>
    public double targetAreaZap(double amt) => _s.LuaTargetAreaZap((int)System.Math.Round(amt), _sp, _targetId);
    /// <summary>Lightning storm's sweep: every creature (an NPC is none), and every other player on a PvP map, within
    /// <paramref name="range"/> tiles of the caster on the larger axis takes <paramref name="amt"/>, each on its own
    /// <paramref name="chance"/>% roll, netted against its own armour, with this spell's graphic over it. Returns how
    /// many were struck; 0 is still a cast.</summary>
    public double rangeZap(double amt, double range, double chance) =>
        _s.LuaRangeZap((int)System.Math.Round(amt), (int)range, chance, _sp);
    /// <summary>Heal every PLAYER on the four cardinally-adjacent cells (the poet 4-way heal ladder). Not the
    /// caster, not pets — RTK scans BL_PC only. Returns how many were healed.</summary>
    public double areaHeal(double amt) => _s.LuaAreaHeal((int)System.Math.Round(amt), _sp);
    /// <summary>Broadcast a line to EVERY player on the server as "[name]: text" (the Sage ladder's world
    /// channel, RTK <c>broadcast(-1, …)</c>). False if the text was empty.</summary>
    public bool worldShout(string text) => _s.LuaWorldShout(text);
    /// <summary>Where the caster is standing, as the Sage ladder measures it: <c>"sage"</c> in one of the
    /// "4.0 designated areas" (the Mythic/Wilderness/KaMing's region, or a carnage/event map), <c>"home"</c>
    /// inside the caster's own kingdom, or <c>""</c> anywhere else. `sage_shout` owns the per-rung policy —
    /// rungs 1-2 need "sage", rungs 3-4 take either, rung 5 ignores this entirely — and falls back to the
    /// Mentor spell when the rung does not reach. A neutral caster never gets "home" (Content.IsOwnKingdom),
    /// which is what makes their rungs 3-4 behave as rung 2, exactly as the tutor board describes.</summary>
    public string sageReach() =>
        Content.IsSageArea(_s.CharMap) ? "sage"
      : Content.IsOwnKingdom(_s.CharMap, _s.CharNation) ? "home" : "";

    // ---- primitives for the Buff / TargetBuff / Debuff / Cure archetype verbs -------------------------------
    /// <summary>Does the caster have at least <paramref name="amt"/> mana? Sends "You do not have enough mana."
    /// and returns false if not (a check only — <see cref="debitMana"/> does the actual debit later, matching the
    /// C# handlers which check up front but debit only once the cast is committed).</summary>
    public bool enoughMana(double amt)  => _s.LuaEnoughMana((int)amt);
    /// <summary>Debit the caster's mana with no re-check and no message (guarded by an earlier <see cref="enoughMana"/>).</summary>
    public void debitMana(double amt)   => _s.LuaDebitMana((int)amt);
    // ---- Buff / TargetBuff: ONE resolved-target primitive set, shaped like the ward one below --------------
    /// <summary>Resolve who this buff lands on: <c>"self"</c> (the Buff archetype — no target arg exists on the
    /// wire) or <c>"target"</c> (the TargetBuff archetype — the aimed id, else the faced tile: a player, incl.
    /// yourself, or a mob/pet). False when nothing resolves; say nothing, a cast that finds nothing is silent.
    /// Every other buff primitive reads this resolution, so call it first.</summary>
    public bool buffTarget(string mode) => _s.LuaBuffTarget(mode, _targetId);
    /// <summary>Does the resolved buff target already carry a status in this exclusivity category (RTK
    /// checkIfCast)? Player targets only — mobs carry no categories, same rule as the curse/ward side.</summary>
    public bool buffHasStatus(string category) => _s.LuaBuffHasStatus(category);
    /// <summary>Is the occupied slot held by THIS very spell? Picks "You already cast that spell." over
    /// "Another spell of this type is in effect."</summary>
    public bool buffAlreadyCast()       => _s.LuaBuffAlreadyCast(_sp);
    /// <summary>Apply the buff to the resolved target, then play the fx and its flavor line once.
    /// <paramref name="stats"/>/<paramref name="amounts"/> are the export row's raw <c>'|'</c>-separated fields
    /// (pass <see cref="buffStat"/>/<see cref="buffAmt"/> straight through — a multi-stat row is split engine-side).
    /// <paramref name="category"/> is the exclusivity slot: pass it and <see cref="buffHasStatus"/> will see the
    /// buff, omit it and the buff blocks nothing.</summary>
    public void applyBuff(string stats, string amounts, double durMs, string category = "") =>
        _s.LuaApplyBuff(stats, amounts, (int)durMs, _sp, category);
    /// <summary>Apply a damage-reduction multiplier (Sanctuary &amp;c) to the resolved target — its own slot, not
    /// a stat delta, and PLAYERS ONLY. <paramref name="mult"/> is the incoming-damage multiplier (0.5 = take
    /// half). False (nothing applied, nothing spent) if the cast resolved to a mob.</summary>
    public bool applyDeduction(double mult, double durMs) => _s.LuaApplyDeduction(mult, (int)durMs, _sp);

    /// <summary>Play this spell's cast anim/sound on the caster.</summary>
    public void fxSelf()                => _s.LuaFxSelf(_sp);
    /// <summary>Raise a barrier on the four tiles beside the caster for <paramref name="durMs"/> (#334):
    /// <c>"creatures"</c> (Barrier) or <c>"players"</c> (Human Barrier). What stands there is held until it ends,
    /// and nothing else may step onto them. See Session.Barrier.cs.</summary>
    public void raiseBarrier(string kind, double durMs) => _s.LuaRaiseBarrier(_sp, kind, (int)durMs);
    // (flavorSelf is gone: applyBuff/applyWard show the flavor line for whoever the cast resolved to, via the
    // one TellTarget that also words an ally's "<caster> casts X on you." A second way to print it is how the
    // self and target halves drifted apart in the first place.)

    /// <summary>What a targeted cast is aimed at, resolved from the client target id or the faced tile:
    /// "player" (incl. a self-cast), "mob" (a mob/NPC/pet), or "none". Read by <c>arch_debuff</c>, which needs
    /// the distinction (Doze can land on a player, so <see cref="hasTarget"/> — mobs only — would refuse it).
    /// It is <c>arch_debuff</c>'s first question, so it resolves this cast's one target (<see cref="StatusTarget"/>);
    /// a cast aimed at nothing that falls back to the caster on a PvP map is "none" here, as it always was. The buff
    /// verbs use <see cref="buffTarget"/> instead, which resolves AND remembers.</summary>
    public string targetKind            => _s.LuaTargetBuffKind(StatusTarget);

    /// <summary>Roll the RTK magic-deflect check (only if the spell can fail) against what the cast is aimed at: a
    /// creature by its protection and Will, another player by their Will, never the caster (see
    /// <c>Session.LuaDeflected</c>). True if the cast was deflected; the verb then strikes nothing and returns true,
    /// and decides what the deflect costs (every verb here keeps the cost).</summary>
    public bool deflected()             => _s.LuaDeflected(_sp, _targetId);
    /// <summary>Uniform percent roll: true with probability <paramref name="pct"/>% (RTK Random.Next(100) &lt; pct).</summary>
    public bool roll(double pct)        => _s.LuaRoll(pct);
    /// <summary>Uniform integer in [<paramref name="lo"/>, <paramref name="hi"/>] inclusive, off the same
    /// stream as <see cref="roll"/>. For weighted picks (e.g. Endear's variable control duration).</summary>
    public double rollRange(double lo, double hi) => _s.LuaRollRange((int)lo, (int)hi);
    // (`freezeTarget` is gone. It set Mob.FrozenUntil directly, with no exclusivity slot — which is what let a
    // hold be re-cast on top of itself indefinitely. Use `holdTarget` above: same freeze, plus the checkIfCast
    // refusal, the boss cap and the repeating animation.)

    // ---- curse / categorized-status primitives (the `curse` verb + arch_cure) ------------------------------
    /// <summary>The category this Cure removes (SpellFx.CureCat: "curses"/"venoms"/"minorcurses"). "" if none.</summary>
    public string cureCat               => _fx?.CureCat ?? "";
    /// <summary>Is the resolved curse target a legal one (a PC only in a PvP map — incl. yourself — or a mob)?
    /// False otherwise — silently when nothing is there, with "You can't attack that target." when it is
    /// someone you may not curse here. An NPC is no curse target, silently. Every curse primitive here reads this
    /// cast's one target (<see cref="StatusTarget"/>), so the target this asked about is the one the curse lands on, or
    /// nobody.</summary>
    public bool canCurse()              => _s.LuaCanCurseTarget(_sp, StatusTarget);
    /// <summary>Does the resolved curse target already carry a status of <paramref name="category"/>? (The
    /// checkIfCast guard — a same-category curse is then blocked, which is what makes self-pestilence a defense.)</summary>
    public bool hasStatus(string category) => _s.LuaCurseHasCategory(category, StatusTarget);
    /// <summary>Is the status occupying <paramref name="category"/> on the resolved target THIS spell's own? Pass
    /// the category that actually blocked the cast (see blockedBy) — the answer picks the refusal wording:
    /// "You already cast that spell." for your own running spell, "Another spell of this type…" for anyone
    /// else's. False when the blocker is a broader category you didn't put there (a protection, say).</summary>
    public bool alreadyCast(string category) => _s.LuaAlreadyCastOnTarget(category, _sp, StatusTarget);
    /// <summary>Apply a categorized status to the resolved curse target: <paramref name="category"/> occupies the
    /// exclusivity slot; <paramref name="stat"/>/<paramref name="amount"/> is the effect (e.g. armor -5 → take more
    /// damage). Plays fx + the target's flavor line.</summary>
    public void applyCurse(string category, string stat, double amount, double durMs) =>
        _s.LuaApplyCurse(category, stat, (int)System.Math.Round(amount), (int)durMs, _sp, StatusTarget);
    /// <summary>Remove every active status of <paramref name="category"/> from the caster (RTK cure-by-category).</summary>
    public void cureCategory(string category) => _s.LuaCureCategory(category);

    // ---- ward primitives (the `ward` verb: bolster / harden / protections — the beneficial categorized status) ----
    /// <summary>Resolve + validate the ward target: "self" (protections) or "ally" (self/ally PC, or a mob for
    /// harden-on-a-pet). No PvP gate. False (with "It doesn't work.") if nothing is found.</summary>
    public bool wardTarget(string mode)      => _s.LuaWardTarget(mode, _targetId);
    /// <summary>Does the resolved ward target already carry a status of <paramref name="category"/>? (PC only.)</summary>
    public bool wardHasStatus(string category) => _s.LuaWardHasStatus(category);
    /// <summary>The ward-target twin of <see cref="alreadyCast"/>: is the blocking status this same ward?</summary>
    public bool wardAlreadyCast()              => _s.LuaWardAlreadyCast(_sp);
    /// <summary>Apply the ward to the resolved target: a PC gets the categorized status (shares curse storage,
    /// folds into AC); a mob gets just the stat buff. <paramref name="amount"/> may be 0 (a protection slot).</summary>
    public void applyWard(string category, string stat, double amount, double durMs) =>
        _s.LuaApplyWard(category, stat, (int)System.Math.Round(amount), (int)durMs, _sp);

    // ---- Tier-3 utility/target primitives (mana_steal/mana_gift/cleanse/revive/leap/mana_battery) ----
    /// <summary>The caster's effective max mana.</summary>
    public double maxMp           => _s.LuaMaxMp;
    /// <summary>Set the caster's HP (clamped to [1, maxHp]). For a raw set (e.g. mana_battery's HP cost).
    /// It cannot kill: a living caster set to 0 or below is left on 1 hp. A verb that means to kill must deal
    /// damage, which runs the real death. A caster who is already dead is clamped to [0, maxHp] as before.</summary>
    public void setHp(double n)   => _s.LuaSetHp((int)System.Math.Round(n));
    /// <summary>Set the caster's mana (clamped to [0, maxMp]).</summary>
    public void setMana(double n) => _s.LuaSetMana((int)System.Math.Round(n));

    /// <summary>Revive the CASTER where they stand; true if they were actually dead. Use this and not
    /// <see cref="setHp"/> to bring someone back — ghost form is a redraw, not just a number.</summary>
    public bool reviveSelf()      => _s.LuaReviveSelf();

    /// <summary>Keyword-classifier verdict for a spell with no spell_effects row: "heal"/"damage"/"buff"/"other".</summary>
    public string effectKind      => _s.LuaEffectKind(_sp);
    /// <summary>Play a raw anim/sound over the resolved target mob (the generic fallback's zap).</summary>
    public void fxRawTarget(double anim, double sound) => _s.LuaFxRawTarget((int)anim, (int)sound, _targetId);

    /// <summary>The recorded Chung Ryong rage tier (0 = none). Check <see cref="rageActive"/> too: the tier
    /// deliberately outlives the fury so the wear-out drain knows what to charge.</summary>
    public double crRageTier      => _s.LuaCrRageTier;
    /// <summary>Record a Chung Ryong rage tier and arm its multiplier, duration and (keyed) AC buff.</summary>
    public void setCrRage(double tier, double mult, double ac, double durMs)
        => _s.LuaSetCrRage((int)tier, (int)mult, (int)ac, (int)durMs, spellName);

    /// <summary>Classify a 0-based pack slot for a repair spell: "empty" · "notgear" · "perfect" · "ok".</summary>
    public string packSlotState(double slot) => _s.LuaPackSlotState((int)slot);
    /// <summary>Display name of whatever is in a 0-based pack slot ("" if empty).</summary>
    public string packSlotName(double slot)  => _s.LuaPackSlotName((int)slot);
    /// <summary>Restore a 0-based pack slot to full durability (no-op unless packSlotState is "ok").</summary>
    public void repairPackSlot(double slot)  => _s.LuaRepairPackSlot((int)slot);

    /// <summary>Resolve the targeted PLAYER (explicit id incl. self, if on the caster's map, else the faced peer) for
    /// this cast; all the target.* members below then act on it. False — and SILENT — if none.</summary>
    public bool   pcTarget()      => _s.LuaResolvePcTarget(_sp, _targetId);
    /// <summary>Resolved target's current mana.</summary>
    public double targetMana      => _s.LuaTargetMana;
    /// <summary>Resolved target's effective max mana.</summary>
    public double targetMaxMana   => _s.LuaTargetMaxMana;
    /// <summary>Is the resolved target a ghost/dead?</summary>
    public bool   targetIsDead    => _s.LuaTargetIsDead;
    /// <summary>Is the resolved target the caster themselves?</summary>
    public bool   targetIsSelf    => _s.LuaTargetIsSelf;
    /// <summary>Is the resolved target in the caster's own party?</summary>
    public bool   targetInGroup   => _s.LuaTargetInGroup;
    /// <summary>Resolved target's effective AC (lower = better; for the cleanse success formula).</summary>
    public double targetArmor     => _s.LuaTargetArmor;
    /// <summary>Resolved target's effective Will.</summary>
    public double targetWill      => _s.LuaTargetWill;
    /// <summary>Take ALL of the resolved target's mana and return how much that was: what they hold, read and set to 0
    /// in one section of their own monitor (<c>Session.ReceiveTakeAllMana</c>), so nothing that lands on their pool
    /// meanwhile is wiped or credited twice. 0 with no target.</summary>
    public double takeTargetMana() => _s.LuaTakeTargetMana();
    /// <summary>Add to the resolved target's mana (clamped to their [0, maxMp]), decided inside the target's own
    /// monitor from the mana they hold then (<c>Session.ReceiveAddMana</c>), so what lands there after the verb's
    /// read of <see cref="targetMana"/> is kept.</summary>
    public void   addTargetMana(double n) => _s.LuaAddTargetMana((int)System.Math.Round(n));
    /// <summary>Send the resolved target this spell's "&lt;caster&gt; casts &lt;name&gt; on you." (or flavor) line.</summary>
    public void   tellTarget()    => _s.LuaTellTarget(_sp);
    /// <summary>Clear the resolved target's buff list and timed stances (RTK flushDuration; not the ward flags,
    /// the Sanctuary and Cunning reductions or the enchant), inside the target's own monitor
    /// (<c>Session.ReceiveFlush</c>). With <paramref name="endHolds"/>, the same section also wakes them from a
    /// Doze (the sleep and the harder next hit it armed) and ends a venom.</summary>
    public void   flushTarget(bool endHolds) => _s.LuaFlushTarget(endHolds);
    /// <summary>Revive the resolved (dead) target in place at full health.</summary>
    public void   reviveTarget()  => _s.LuaReviveTarget(_sp);
    /// <summary>Leap up to <paramref name="maxDist"/> tiles in the faced direction (collision-stopped); returns
    /// the number of tiles actually moved (0 = blocked, nothing happened).</summary>
    public double leap(double maxDist) => _s.LuaLeap((int)maxDist);

    // ---- Tier-4 world-effecting primitives (gateway/return/divine/spot_traps/filch/trap/bladestorm/pet/morph/propose) ----
    /// <summary>Is the caster a ghost/dead? (Gateway/Return can't be cast while dead.)</summary>
    public bool   isDead        => _s.IsDead;
    /// <summary>Does the caster's current map allow warping out? (RTK warpOut flag — false on arenas/instances.)</summary>
    public bool   canWarpOut    => _s.LuaWarpOut;
    /// <summary>The spell's real per-spell mana cost from its export row (or 5 if the export had none).</summary>
    public double spellMana     => _s.LuaSpellMana(_sp);
    /// <summary>The spell's amount from its export row (spell_effects.csv <c>amountExpr</c>) at this cast, unrounded:
    /// the figure the archetype path hands its verb as <see cref="amount"/>, for a verb bound by a SpellParams row that
    /// carries no amount of its own. Mage Bolt's <c>magic_damage</c> reads it, and so lands the Spark family's
    /// 50 + level/2 (#200). Throws for a spell with no export row (<c>Session.LuaSpellAmount</c>).</summary>
    public double spellAmount   => _s.LuaSpellAmount(_sp);
    /// <summary>The spell's cooldown (RTK aether) in ms from its export row (0 if none).</summary>
    public double spellAether    => _s.LuaSpellAether(_sp);
    /// <summary>The resolved target player's level (after <see cref="pcTarget"/>). For Divination's level gate.</summary>
    public double targetLevel   => _s.LuaTargetLevel;
    /// <summary>Is this the spy (inventory-listing, equal-level-allowed) Divination variant, not judge?</summary>
    public bool   spyMode       => _s.LuaIsSpy(_sp);
    /// <summary>Live pet count this caster owns on the current map, and the level-scaled cap.</summary>
    public double petCount      => _s.LuaPetCount;
    public double petCap        => _s.LuaPetCap;
    /// <summary>This pet spell's mana cost / cooldown ms (0 if none) — data-bound per pet spell.</summary>
    public double petMana       => _s.LuaPetMana(_sp);
    public double petCooldown   => _s.LuaPetCooldownMs(_sp);
    /// <summary>Is a morph already active at the look this cast would apply? (Re-cast of the same form is a no-op.)</summary>
    public bool   morphActive() => _s.LuaMorphActive();
    /// <summary>The staged morph's resolved mana cost.</summary>
    public double morphMana     => _s.LuaMorphMana;

    /// <summary>Gateway: warp to the answered gate of the caster's kingdom. Self-narrates arrival; false (with a
    /// notice) if the region has no gates or the answer isn't a direction.</summary>
    public bool gateway()       => _s.LuaGateway(answer);
    /// <summary>Return: warp home to a random tavern in the caster's nation.</summary>
    public void returnHome()    => _s.LuaReturnHome();
    /// <summary>Divination: build + send the inspect popup for the resolved target to the caster (spy variant
    /// appends inventory). Self-narrates.</summary>
    public void divine(bool showInventory) => _s.LuaDivine(_sp, showInventory);
    /// <summary>Reveal every hidden trap within 15 tiles as a caster-only marker; returns how many were found.</summary>
    public double revealTraps() => _s.LuaRevealTraps();
    /// <summary>Grab the item on the faced tile (coins -> purse, else -> pack; put back if the pack is full).</summary>
    public void filch()         => _s.LuaFilch();
    /// <summary>Place a hidden trap on the caster's own tile — resolves kind/level/mana from the spell (or the
    /// typed answer for the set_trap dispatcher) and owns its own mana debit. False (with a notice) on a bad
    /// answer or too-low level / not enough mana.</summary>
    public bool placeTrap()     => _s.LuaPlaceTrap(_sp, answer);
    /// <summary>Place a bladestorm decoy on the caster's tile, auto-expiring in <paramref name="lifetimeMs"/> ms.</summary>
    public void placeBladestorm(double lifetimeMs) => _s.LuaPlaceBladestorm((int)lifetimeMs);
    /// <summary>Summon this spell's pet as a real owned world mob (one tile ahead, else on the caster's tile).
    /// False if the pet/mob couldn't be resolved.</summary>
    public bool summonPet()     => _s.LuaSummonPet(_sp);
    /// <summary>Apply the staged morph plan (look/duration) to the caster + rebroadcast to self and peers.</summary>
    public void applyMorph()    => _s.LuaApplyMorph(_sp);
    /// <summary>Kick off the async marriage-proposal dialog (RTK RunProposeAsync). Returns immediately.</summary>
    public void propose()       => _s.LuaPropose(_sp);
    public void mentor()        => _s.LuaMentor(_sp);
    /// <summary>Does the caster carry a legend mark (e.g. "engaged"/"married")?</summary>
    public bool hasLegend(string mark) => _s.LuaHasLegend(mark);
    /// <summary>Forget this spell from the caster's spellbook (RTK cleanup of a spell you shouldn't still have).</summary>
    public void forgetSpell()   => _s.LuaForgetSpell(_sp);
    /// <summary>Mark this cast as self-narrated so the central "You cast X." line is suppressed.</summary>
    public void narrated()      => _s.LuaMarkNarrated();

    // ---- Approach / Summon (#313): the person is TYPED into the prompt, not aimed at ----------------------
    // The engine half is Session.ApproachSummon.cs; the verbs own every guard and every line. Both moves are the
    // ones @approach and @bring make (EnterMap with ArrivalPolicy.AdjacentFreeElseStack).

    /// <summary>Resolve the online player whose name was typed (<paramref name="name"/>, normally
    /// <see cref="answer"/>; any map, case-insensitive, like whisper) as this cast's player target: the typed
    /// twin of <see cref="pcTarget"/>, so <see cref="targetIsSelf"/>, <see cref="targetIsDead"/>,
    /// <see cref="targetInGroup"/> and the members below read them. False, and SILENT, on a blank name or when
    /// nobody online has it; the verb says why.</summary>
    public bool   pcTargetNamed(string name) => _s.LuaResolveNamedPcTarget(_sp, name);
    /// <summary>The resolved player target's kingdom (<c>Character.Nations</c>: 0 Neutral, 1 Koguryo, 2 Buya, …);
    /// -1 with no target.</summary>
    public double targetNation => _s.LuaPcTarget?.CharNation ?? -1;
    /// <summary>The map the resolved player target stands on (0 with no target): a bare read of the map alone, which
    /// Summon's checks make. Approach reads the whole place once through <see cref="readTargetPlace"/>.</summary>
    public double targetMap    => _s.LuaPcTarget?.CharMap ?? 0;
    /// <summary>Approach's one read of where the resolved player target stands: their map, its size and their tile,
    /// read together inside their monitor and kept for <see cref="approachTarget"/> (<c>Session.LuaReadApproachPlace</c>).
    /// Returns the map, for the verb's checks; 0 with no target.</summary>
    public double readTargetPlace() => _s.LuaReadApproachPlace();
    /// <summary>Is the resolved player target staff (the GM roster)?</summary>
    public bool   targetIsGm   => _s.LuaPcTarget?.LuaIsGm ?? false;
    /// <summary>The caster's kingdom, numbered as <see cref="targetNation"/>.</summary>
    public double nation       => _s.CharNation;
    /// <summary>The map the caster stands on.</summary>
    public double map          => _s.CharMap;
    /// <summary>Is the caster staff?</summary>
    public bool   isGm         => _s.LuaIsGm;
    /// <summary>Is <paramref name="mapId"/> indoors (Maps.csv <c>MapIndoor</c>)? False for a map with no row.</summary>
    public bool   mapIndoor(double mapId)  => Content.IsIndoor((ushort)mapId);
    /// <summary>Is <paramref name="mapId"/> a PvP map (Maps.csv <c>MapPvP</c>)? False for a map with no row.</summary>
    public bool   mapPvp(double mapId)     => Content.IsPvpMap((ushort)mapId);
    /// <summary>May a player warp out of <paramref name="mapId"/> (Maps.csv <c>MapWarpout</c>)? True unless the
    /// row says 0. <see cref="canWarpOut"/> asks the same of the caster's own map.</summary>
    public bool   mapWarpOut(double mapId) => Content.WarpOut((ushort)mapId);
    /// <summary>May Approach and Summon reach <paramref name="mapId"/> (Maps.csv <c>MapCanSummon</c>, RTK
    /// <c>canSummon</c>)? True unless the row says 0: a map with no row, or a blank cell, allows.</summary>
    public bool   mapCanSummon(double mapId) => Content.CanSummon((ushort)mapId);

    /// <summary>Do <paramref name="mapId"/>'s level, vita and mana bands admit <paramref name="who"/>
    /// (<c>"caster"</c>, or <c>"target"</c> for the resolved player target)? The four comparisons RTK's
    /// <c>Spells/common/approach.lua</c> and <c>summon.lua</c> make against the destination, on its Maps.csv
    /// row: level at least <c>MapReqLvl</c> and at most <c>MapLvlMax</c>; base vita at least
    /// <c>MapReqVita</c> OR base mana at least <c>MapReqMana</c>; base vita at most <c>MapVitaMax</c> AND base
    /// mana at most <c>MapManaMax</c>. A map with no row has no bands. Base, not with gear, as RTK's
    /// <c>baseHealth</c>/<c>baseMagic</c> and the walk-in warp gate read them.
    /// <para>Not the walk-in gate (<c>Session.TryWarpGate</c>), on purpose: that is RTK's clif.c cascade, which also
    /// asks for mark and path and caps vita and mana with AND rather than OR. These are the spell scripts'
    /// own comparisons.</para></summary>
    public bool mapAdmits(double mapId, string who)
    {
        Session? mover = who switch { "caster" => _s, "target" => _s.LuaPcTarget, _ => null };
        if (mover is null) return false;
        if (!Content.MapMeta.TryGetValue((ushort)mapId, out var band)) return true;
        long level = mover.CharLevel, vita = mover.CharMaxHp, mana = mover.CharMaxMp;
        if (level < band.ReqLvl || level > band.LvlMax) return false;
        if (vita < band.ReqVita && mana < band.ReqMana) return false;
        return vita <= band.VitaMax && mana <= band.ManaMax;
    }

    /// <summary>Approach's move: the caster to the first free tile beside the place <see cref="readTargetPlace"/>
    /// read (N, E, S, W), else onto that tile: the move <c>@approach</c> makes, to the place the checks were made on
    /// rather than to a second read. False, and nobody moves, with no target or no place read since it resolved.</summary>
    public bool   approachTarget() => _s.LuaApproachTarget(_sp);
    /// <summary>Summon's move: the resolved player target to the first free tile beside the caster, else onto
    /// the caster's tile. Exactly the move <c>@bring</c> makes, under the target's own monitor, after ending any
    /// exchange the target has open. False, and nobody moves, with no target, when they logged out between the
    /// lookup and the move, or when their exchanges kept re-opening (<c>Session.LuaSummonTarget</c>).</summary>
    public bool   summonTarget()   => _s.LuaSummonTarget(_sp);

    // ---- combat-stray primitives (sacrifice strikes + ambush) ----------------------------------------------
    /// <summary>Which self-sacrifice strike family this spell is ("LethalStrike"/"DesperateAttack"/"Berserk"/
    /// "Whirlwind"), driving its per-family damage/mana/cooldown/HP-cost formulas.</summary>
    public string sacrificeFamily => _s.LuaSacrificeFamily(_sp);
    /// <summary>The caster's alignment stat (Whirlwind's damage factor + HP cost + cooldown branch on it).</summary>
    public double alignment       => _s.LuaAlignment;
    /// <summary>Resolve + stash the mob on the faced tile for a sacrifice strike (alive). False if none — the
    /// strike lands nothing (no HP cost), though the mana/cooldown were already spent. An NPC is none: no blow can
    /// hurt it, so an NPC in front is nothing in front (RTK's reading).</summary>
    public bool   sacFrontMob()   => _s.LuaSacFrontMob();
    /// <summary>Apply <paramref name="damage"/> (armor-netted) to the stashed sacrifice target; plays the family
    /// fx, awards kill XP. Returns the overkill (net damage minus the mob's pre-hit HP; may be negative).</summary>
    public double sacApply(double damage) => _s.LuaSacApply(_sp, (int)System.Math.Round(damage));
    /// <summary>Rogue overkill refund: up to half the overkill returns as HP and MP, each capped at half the
    /// caster's pre-cast HP/MP.</summary>
    public void   backflow(double overkill, double preHp, double preMp) => _s.LuaBackflow((int)overkill, (int)preHp, (int)preMp);
    /// <summary>Warrior overkill splash: the overkill cleaves onto adjacent-tile mobs, recursively re-splashing.</summary>
    public void   overflow(double overkill) => _s.LuaOverflow(_sp, (int)overkill);
    /// <summary>Resolve + stash the mob on the faced tile for an ambush. False (silently) if none.</summary>
    public bool   ambushMob()     => _s.LuaAmbushMob();
    /// <summary>Teleport to the far side of the stashed mob (its back, else a flank), facing it. False if the
    /// back and both flanks are all occupied ("finds no opening").</summary>
    public bool   ambushLeap()    => _s.LuaAmbushLeap();
    /// <summary>Swing on the stashed ambush target (gets the free positional backstab if landed on its blind side).</summary>
    public void   ambushStrike()  => _s.LuaAmbushStrike(_sp);

    // ---- revive around (Reincarnate) -------------------------------------------------------------------------------
    /// <summary>Revive every dead player on the four cells beside the caster, where they lie, with
    /// <paramref name="fraction"/> of their max vita (floored, at least 1) and the mana they had; the living are left
    /// alone. Each gets the spell's graphic and "&lt;caster&gt; casts &lt;spell&gt; on you."; the spell's sound plays at the
    /// caster when anyone stands beside them (RTK <c>Spells/Subpaths/Shaman/reincarnate.lua:27-43</c>). Returns how many
    /// came back; 0 is still a cast. Each revive is made inside the revived player's own monitor
    /// (<c>Session.ReceiveRevive</c>).</summary>
    public double reviveAround(double fraction) => _s.LuaReviveAround(fraction, _sp);

    // ---- periodic ticks (RTK while_cast_<ms>) ----------------------------------------------------------------------
    /// <summary>Start, or refresh, this caster's tick of this spell on this cast's victim, which must be a creature:
    /// the verb named <paramref name="verb"/> runs every <paramref name="everyMs"/> for <paramref name="durationMs"/>,
    /// against that creature (RTK <c>setDuration(key, ms, player.ID, 1)</c> and the script's
    /// <c>while_cast_&lt;ms&gt;</c>). A tick already running there takes the new length and keeps its beat. False, starting
    /// nothing, at a player, an NPC or nothing. See <c>Session.TickSpellTicks</c>.</summary>
    public bool startTick(string verb, double everyMs, double durationMs) =>
        _s.LuaStartTick(_sp, Victim.mob, VictimMap, verb, (int)everyMs, (int)durationMs);
    /// <summary>Does this caster's tick of this spell run on this cast's victim? RTK <c>hasDurationID(key, player.ID)</c>.</summary>
    public bool tickRunning()           => _s.LuaTickRunning(_sp, Victim.mob, VictimMap);
    /// <summary>End this caster's tick of this spell on this cast's victim now (RTK <c>setDuration(key, 0, player.ID)</c>);
    /// a beat already running goes on to its end.</summary>
    public void endTick()               => _s.LuaEndTick(_sp, Victim.mob, VictimMap);
    /// <summary>How many living creatures on this cast's victim's map carry this caster's tick of this spell.</summary>
    public double tickCount()           => _s.LuaTickCount(_sp, VictimMap);
    /// <summary>Is the caster on the map this cast's victim stands on? Always, in a cast; a tick's creature may be on
    /// a map its caster has left.</summary>
    public bool   victimHere            => VictimMap == _s.CharMap;
    /// <summary>Strike this cast's victim, a creature, for <paramref name="amount"/>, netted against its armour, never
    /// for less than <paramref name="minimum"/> once netted: no deflect roll and no graphic (RTK <c>calculateDamage</c>,
    /// a floor, <c>removeHealth</c>). The over-head bar, and a kill's experience. False when it is not a living
    /// creature.</summary>
    public bool   strikeVictim(double amount, double minimum) =>
        _s.LuaStrikeVictim(_sp, Victim.mob, VictimMap, amount, (int)minimum);
    /// <summary>Draw effect <paramref name="anim"/> over this cast's victim for the caster alone (RTK
    /// <c>target:selfAnimation(player.ID, anim, 1)</c>).</summary>
    public void   selfAnimation(double anim) =>
        _s.LuaSelfAnimation(Victim.mob?.Id ?? Victim.pc?.PlayerId, VictimMap, (int)anim);
    /// <summary>Start, or refresh, this caster's tick of this spell on the caster themselves, with no creature: the verb
    /// named <paramref name="verb"/> runs every <paramref name="everyMs"/> for <paramref name="durationMs"/> (RTK
    /// <c>player:setDuration(key, ms)</c> and the script's <c>while_cast</c>; Hear footstep). A run already going takes
    /// the new length and keeps its beat. Death, <c>@dispel</c> and a won Dispell end it. See
    /// <c>Session.LuaStartSelfTick</c>.</summary>
    public bool startSelfTick(string verb, double everyMs, double durationMs) =>
        _s.LuaStartSelfTick(_sp, verb, (int)everyMs, (int)durationMs);
    /// <summary>The names of the other players standing invisible within <paramref name="range"/> tiles of the caster on
    /// both axes, on the caster's map, one a line ("\n" between them; "" for nobody): RTK's
    /// <c>getObjectsInSameMap(BL_PC)</c> filtered by <c>state == 2</c> and <c>distanceSquare</c>, the caster left out
    /// (Hear footstep's beat). Nobody once the caster is logging out or this spell's run on them has ended.</summary>
    public string hiddenNear(double range) => _s.LuaHiddenNear(_sp, (int)range);
    /// <summary>Turn the caster invisible for <paramref name="durationMs"/> with the next landed swing
    /// x<paramref name="times"/>, under this spell's name and with its SpellText.csv fade line when it ends (the
    /// <c>hide</c> verb; Blend). A cast while already invisible never lengthens the run. See <c>Session.LuaHide</c>.</summary>
    public void hide(double durationMs, double times) => _s.LuaHide(_sp, (int)durationMs, (int)times);
    /// <summary>Is the caster in a creature's form (a morph)?</summary>
    public bool morphed => _s.LuaMorphed;

    // ---- the target readout (RTK's Evaluate opponent and Assess opponent) -----------------------------------------
    // Reads only. A player victim's name, class and rank, vita and mana are bare reads of that player's fields, as
    // targetMana's is: display figures, each one scalar or reference read, never written and never under their monitor.
    /// <summary>What this cast's victim (<see cref="Victim"/>) is: "creature", "player" or "none" (an NPC is none).</summary>
    public string victimKind  => Victim.mob is not null ? "creature" : Victim.pc is not null ? "player" : "none";
    /// <summary>This cast's victim's name: a creature's, or a player's; "" with none.</summary>
    public string victimName  => Victim.mob?.Name ?? Victim.pc?.CharName ?? "";
    /// <summary>A player victim's class and rank as the world calls them (RTK <c>classNameMark</c>,
    /// <c>Session.ClassTitle</c>); "" for a creature or none.</summary>
    public string victimTitle => Victim.pc?.ClassTitle ?? "";
    /// <summary>This cast's victim's vita now: a creature's, or a player's; 0 with none.</summary>
    public double victimHp    => Victim.mob is { } m ? m.Hp : Victim.pc?.LuaHp ?? 0;
    /// <summary>A creature victim's max vita (RTK <c>target.maxHealth</c>); 0 for a player or none.</summary>
    public double victimMaxHp => Victim.mob?.MaxHp ?? 0;
    /// <summary>A player victim's mana now; 0 for a creature or none.</summary>
    public double victimMp    => Victim.pc?.LuaMp ?? 0;
    /// <summary>Roll the engine's magic deflect against this cast's victim, for a spell whose row can fail (RTK's
    /// clif_parsemagic rolls it before the script runs): a creature's protection and Will, a player's Will; never on
    /// the caster. True when deflected; the verb then says so and does nothing else.</summary>
    public bool victimDeflects() => _s.LuaVictimDeflects(_sp, Victim);
    /// <summary>The name of the fury running now (the rage slot's, shown in the buff box); meaningful while
    /// <see cref="rageActive"/>. A climbing fury on a row ladder continues only its own climb (chungryong_rage).</summary>
    public string rageName    => _s.LuaRageName;

    // ---- a typed answer and dialog boxes inside a cast (RTK inputSeq, dialogSeq; Read karma) -----------------------
    // RTK runs these scripts as coroutines that wait for the player's reply. A verb here is one call, so a wait names
    // the verb that runs once the reply comes (Session.LuaAsk; the run's beat names its verb the same way). See the
    // note above Session.LuaAsk for what each box is, and its locks.
    /// <summary>Ask the caster for a line of text (RTK <c>inputSeq</c>): an input box carrying <paramref name="prompt"/>,
    /// with no portrait. A submitted answer runs the verb <paramref name="verb"/> for this caster and spell, against the
    /// spell's own row, with the typed text as <see cref="answer"/>; a box closed or replaced runs nothing.</summary>
    public void ask(string prompt, string verb) => _s.LuaAsk(_sp, prompt, verb);
    /// <summary>A box with no button on the caster (RTK <c>dialogSeq({t, text}, 0)</c>). Its portrait is the Items.csv
    /// item <paramref name="item"/>'s icon when one is named, else creature look <paramref name="look"/> in palette
    /// <paramref name="color"/> (RTK <c>convertGraphic(n, "item")</c> and <c>convertGraphic(n, "monster")</c>); none
    /// for look 0.</summary>
    public void dialog(string text, double look = 0, double color = 0, string item = "") =>
        _s.LuaDialog(text, (int)look, (int)color, item);
    /// <summary>The caster's box with the next arrow, then, once the caster answers it, a box with no button to the
    /// player <see cref="pcTargetNamed"/> resolved, their waiting conversation dropped first (RTK read_karma.lua:
    /// <c>player:dialogSeq({t, text}, 1)</c>, <c>target:freeAsync()</c>, <c>target:dialogSeq({t, text2}, 0)</c>). Both
    /// boxes take the one portrait, as <see cref="dialog"/> reads it. Nothing with no target; nothing to a target who
    /// has logged out by then.</summary>
    public void dialogThenTarget(string text, string targetText, double look = 0, double color = 0, string item = "") =>
        _s.LuaDialogThenTarget(_sp, text, targetText, (int)look, (int)color, item);
    /// <summary>The karma of the player <see cref="pcTargetNamed"/> (or <see cref="pcTarget"/>) resolved (RTK
    /// <c>target.karma</c>); 0 with none. A bare read, never written.</summary>
    public double targetKarma => _s.LuaPcTarget?.LuaKarma ?? 0;
    /// <summary>The name of the player <see cref="pcTargetNamed"/> (or <see cref="pcTarget"/>) resolved (RTK
    /// <c>target.name</c>); "" with none.</summary>
    public string targetName  => _s.LuaPcTarget?.CharName ?? "";

    // ---- the game's time (RTK curYear, curDay, curTime; Divine time) ------------------------------------------------
    /// <summary>The in-game year (RTK <c>curYear()</c>): the world clock's (<c>World.WorldClock.ClockNow</c>).</summary>
    public double gameYear  => _s.LuaGameClock.year;
    /// <summary>The in-game day of the season, 1-91 (RTK <c>curDay()</c>).</summary>
    public double gameDay   => _s.LuaGameClock.day;
    /// <summary>The in-game hour, 0-23 (RTK <c>curTime()</c>), the <c>@clock</c> pin's when one is set.</summary>
    public double gameHour  => _s.LuaGameClock.hour;
    /// <summary>The real calendar's month, 1-12 (RTK's <c>os.date("%m")</c>, which Divine time's script names its
    /// "Moon" by), at the world calendar's own UTC offset (<c>GameCalendar.Epoch</c>), so a host in another zone reads
    /// the same month.</summary>
    public double realMonth => DateTimeOffset.UtcNow.ToOffset(GameCalendar.Epoch.Offset).Month;

    // ---- the disguise (P3; RTK clone.playerToPlayer and player.disguise) --------------------------------------------
    /// <summary>Take the look of what stands on the faced tile for <paramref name="durationMs"/>, and say what it was
    /// (RTK <c>Spells/Subpaths/Muse/costume.lua:21-37</c>; Spy's Mimic and Ranger's Forest cover are the same body):
    /// "creature" (its look and colour, a creature's form: RTK state 4), "player" (their gear, face and colours, the
    /// caster's own name; or, for a player in a creature's form, that form), "npc" (no look, but a run that ends with the
    /// spell's graphic), or "none" (nothing living there, so nothing started). The run ends at its time, at death, at
    /// @dispel and at a won Dispell, with the spell's graphic over the caster. See <c>Session.LuaDisguise</c>.</summary>
    public string disguise(double durationMs) => _s.LuaDisguise(_sp, (int)durationMs);

    // ---- effects on everyone on the caster's map (P11; RTK getObjectsInSameMap(BL_PC)) -----------------------------
    /// <summary>Every player on the caster's map, the caster included, who is not already in a sanctuary takes the
    /// Sanctuary line's incoming-damage multiplier <paramref name="mult"/> for <paramref name="durationMs"/> under this
    /// spell's name, with its graphic over them; the others read "You have found Sanctuary." and "&lt;caster&gt; casts
    /// &lt;spell&gt; on you."; its sound plays at the caster (RTK <c>Spells/Subpaths/Muse/sacred_verse.lua:21-42</c>).
    /// Each is decided inside that player's own monitor. Returns how many it reached; 0 is still a cast. See
    /// <c>Session.LuaMapSanctuary</c>.</summary>
    public double mapSanctuary(double mult, double durationMs) => _s.LuaMapSanctuary(mult, (int)durationMs, _sp);

    // ---- a heal run on the heal target (Song of the Goddess; RTK setDuration on the target and its while_cast) --------
    /// <summary>The caster's rank within their path (RTK <c>player.mark</c>, <c>Character.Mark</c>): 0 for the base
    /// rank, 1 Il san, 2 Ee san and up.</summary>
    public double mark => _s.CharMark;
    /// <summary>Start, or refresh, this caster's run on this cast's heal target (<see cref="HealTarget"/>, the one
    /// <see cref="healReach"/> and <see cref="healIsGhost"/> read): the verb named <paramref name="verb"/> beats
    /// <paramref name="durationMs"/> / 1,000 times, every second on a player and every 1.5 s on a creature (RTK's player
    /// and creature duration timers), the last as the run ends; the spell's sound over the target now, and the engine's
    /// line to a player other than the caster (RTK <c>Spells/Subpaths/TESTING/muse_song_of_the_goddess.lua:26-28</c>).
    /// False at an NPC or nobody. See <c>Session.LuaStartHealRun</c>.</summary>
    public bool startHealRun(string verb, double durationMs) =>
        _s.LuaStartHealRun(_sp, HealTarget, verb, (int)durationMs);
    /// <summary>In a heal run's beat: heal its victim for <paramref name="amount"/>, with the spell's graphic and sound
    /// over them (RTK's while_cast: <c>addHealthExtend</c>, <c>playSound(4)</c>, <c>sendAnimation(66)</c>). A player is
    /// healed inside their own monitor, and a player whose durations have ended since the run began (death, @dispel, a
    /// won Dispell) or who is leaving ends the run instead. False when nothing was healed. See
    /// <c>Session.LuaHealRunVictim</c>.</summary>
    public bool healRunVictim(double amount) =>
        _s.LuaHealRunVictim(_sp, Victim, VictimMap, _tickGen, (int)System.Math.Round(amount));

    // ---- login-only registry ---------------------------------------------------------------------------------------
    /// <summary>Read a per-caster integer kept for this login only (0 if unset): RTK <c>player.registry</c> for a value
    /// this server does not save (Shaman's torment's "totalsiphon"). <see cref="reg"/> is the saved one.</summary>
    public int  tempReg(string key)                   => _s.LuaTempReg(key);
    /// <summary>Set a per-caster integer kept for this login only (not saved).</summary>
    public void setTempReg(string key, double v)      => _s.LuaSetTempReg(key, (int)v);

    // ---- the Barbarian spells' primitives (RTK Spells/Subpaths/Barbarian/), built for the later slices too ---------
    // Every write to another player is made inside that player's own monitor, by a Receive* method or EnterMap, the
    // paths Summon, the curses and the blows already take; no new lock. See Session.Spells.cs, "the Barbarian spells".
    /// <summary>The caster's own name, for a line a verb words for its target ("&lt;caster&gt; casts Push spell on you.").</summary>
    public string name => _s.CharName;
    /// <summary>Shout <paramref name="msg"/> over the caster's head, as it stands (RTK <c>player:talk(2, msg)</c>, the shout the
    /// warrior strikes give: Barbarian cry's "TOWNIE~~!"). <see cref="talk"/> puts the caster's name in front.</summary>
    public void shout(string msg) => _s.Shout(msg);
    /// <summary>Strike what stands on the faced tile for <paramref name="amount"/>, netted against its armour, with no
    /// deflect roll, with this spell's graphic over it: "each" strikes the first creature there (never an NPC) and the
    /// first living player there when the caster may attack them (RTK's getTargetFacing(BL_MOB) and (BL_PC), Barbarian
    /// cry); "first" strikes the first living thing, a player before a creature, as RTK's getTargetFacing(BL_ALL) lists a
    /// cell (Rend). A ghost on the tile is passed over to a living player behind it. Returns how many were struck; 0 is
    /// still a cast. See <c>Session.LuaStrikeFaced</c>.</summary>
    public double strikeFaced(double amount, string mode) => _s.LuaStrikeFaced(_sp, amount, mode);
    /// <summary>Spawn the mobs.csv creature <paramref name="mobKey"/>, unowned, on the faced tile when RTK's spawn test finds
    /// it free (no map object, passable, no warp, nothing standing or lying there, inside RTK's bounds), else on the
    /// caster's own tile (RTK <c>player:spawn</c>; Mount's horse). Throws for an unknown creature.</summary>
    public bool spawnFaced(string mobKey) => _s.LuaSpawnFaced(_sp, mobKey);
    /// <summary>Put a menu of <paramref name="options"/> ('|' between them) under <paramref name="prompt"/> to the caster,
    /// with no portrait (RTK <c>player:menuString</c> from a spell), and return at once. A pick runs the verb named
    /// <paramref name="verb"/> for this spell, with this spell's SpellParams.csv row and the picked option as
    /// <see cref="answer"/>, on the caster's own thread and monitor, unless they have died or are leaving by then. A closed
    /// box runs nothing. See <c>Session.LuaSpellMenu</c>.</summary>
    public void menu(string prompt, string options, string verb) => _s.LuaSpellMenu(_sp, prompt, options, verb);
    /// <summary>Take on the form this spell's Morphs.csv row gives <paramref name="answer"/> (its answers column), for its
    /// time, ending any other form: the morph verb's look, graphic and redraw. False, changing nothing, for an answer the
    /// row does not list.</summary>
    public bool morphTo(string answer) => _s.LuaMorphTo(_sp, answer);
    /// <summary>Resolve the first living player on the faced tile, on any map, as this cast's player target (RTK
    /// <c>getTargetFacing(player, BL_PC)</c>, which passes over the dead, so a living player standing on a ghost's tile
    /// is found); no aimed id is read. False, and silent, with nobody living there. <see cref="pushTarget"/>, <see cref="speechStatus"/> and <see cref="tellTargetLine"/> act on it.</summary>
    public bool facedPlayer() => _s.LuaFacedPlayer(_sp);
    /// <summary>Push's move: the resolved player target one tile on in the caster's facing, or two when the first is taken,
    /// when RTK's test finds the tile free, inside the target's own monitor (the landing tile first, then Summon's checks,
    /// so a push that moves nobody leaves an open exchange alone). This spell's graphic over them before and after. False,
    /// and nobody moves, when neither tile is free (or the target is logging out).</summary>
    public bool pushTarget() => _s.LuaPushTarget(_sp);
    /// <summary>Send <paramref name="line"/> to the resolved player target's status box, inside their monitor.</summary>
    public void tellTargetLine(string line) => _s.LuaTellTargetLine(line);
    /// <summary>Give the resolved player target a speech status for <paramref name="ms"/>: "garbled" (each letter they say
    /// may come out '*', a GM excepted; Break jaw) or "mute" (they cannot say or shout; Spy's Dagger to throat), under
    /// this spell's name and key, with this spell's graphic over them and its SpellText.csv fade line at the end. False,
    /// changing nothing, when this spell's status already holds them (RTK <c>hasDuration</c>). See Session.Chat.cs.</summary>
    public bool speechStatus(string kind, double ms) => _s.LuaSpeechStatus(_sp, kind, (int)ms);
    /// <summary>The area curse (Insult, Dishonor): on each of the caster's four sides, north, east, south, west, every
    /// player there the caster may attack and every creature (not an NPC) there take <paramref name="category"/>'s curse of
    /// <paramref name="stat"/> <paramref name="amount"/> for <paramref name="durMs"/>, with this spell's graphic, unless
    /// they already carry a status of a category in <paramref name="blockedBy"/> ('|' between them). A player reads
    /// <paramref name="line"/>; a creature redraws <paramref name="beatAnim"/> every <paramref name="beatMs"/>. A dead
    /// player met on the walk ends it there. Returns how many were cursed.</summary>
    public double curseAround(string category, string stat, double amount, double durMs, string blockedBy, string line,
                              double beatAnim, double beatMs) =>
        _s.LuaCurseAround(_sp, category, stat, (int)System.Math.Round(amount), (int)durMs, blockedBy, line,
                          (int)beatAnim, (int)beatMs);

    // ---- the Druid spells' primitives (RTK Spells/Subpaths/Druid/), the drop hook built for Geomancer's Exhume too ---
    // As the Barbarian half: a heal on another player lands inside that player's own monitor (ReceiveHeal, WithState); a
    // creature and a floor item are written through World's own methods. No new lock. See Session.Spells.cs, "the Druid
    // spells".
    /// <summary>A drop hook's context (<c>Session.RunDropHooks</c>): this spell, whose wait is running, is handed the item
    /// its caster is dropping, <paramref name="dropped"/> (an Items.csv key), before it leaves the bag.</summary>
    internal SpellContext(Session s, SpellDef sp, string dropped)
        : this(s, sp, null, null)
    {
        this.dropped = dropped;
    }
    /// <summary>In a drop hook, the key of the item being dropped (RTK <c>player:getInventoryItem(player.invSlot).yname</c>);
    /// "" in a cast.</summary>
    public string dropped { get; } = "";
    /// <summary>Take one of the item being dropped from the bag, as RTK's <c>player:removeItem(yname, 1, 1)</c> takes it:
    /// from the first stack of it, in slot order, that is not full, else from the first; a stack that empties reads "You
    /// dropped &lt;item&gt;.". False, taking nothing, when there is none to take or the caster is going away (a kick or an
    /// exchange that came while the hook waited for the Lua gate); the hook then does nothing more. See
    /// <c>Session.LuaTakeDropped</c>.</summary>
    public bool takeDropped() => _s.LuaTakeDropped(dropped);
    /// <summary>Control flames' flames: every creature (never an NPC) on each of the caster's four sides, north, east,
    /// south, west, every one on each, takes <paramref name="amount"/>, netted against its armour, with this spell's
    /// graphic over it; when any was struck the spell's sound plays at the caster (RTK <c>getTargetsAround(player,
    /// BL_MOB)</c>). Returns how many were struck.</summary>
    public double strikeAround(double amount) => _s.LuaStrikeAround(_sp, (int)System.Math.Round(amount));
    /// <summary>Nature's bloom's flowers: one <paramref name="itemKey"/> on the caster's tile, then one more on each tile
    /// within <paramref name="radius"/> on both axes, on a <paramref name="chance"/>% roll, where the tile carries no map
    /// object, is passable and is no warp, a tile past the map's edge read as the edge tile (RTK's clamp). Returns how
    /// many were put down. See <c>Session.LuaScatter</c>.</summary>
    public double scatter(string itemKey, double radius, double chance) =>
        _s.LuaScatter(_sp, itemKey, (int)radius, chance);
    /// <summary>Sea breeze: every player and every creature (not an NPC) within <paramref name="range"/> tiles of the
    /// caster on both axes, the caster too, gets this spell's graphic and <paramref name="amount"/> vita (a ghost none), a
    /// player reading <paramref name="line"/> first; the spell's sound plays at the caster. Returns how many.</summary>
    public double healRange(double amount, double range, string line) =>
        _s.LuaHealRange(_sp, (int)System.Math.Round(amount), (int)range, line);
    /// <summary>End the caster's creature form now, as when it runs out (RTK <c>removeDuras(morphs)</c>). False with
    /// none.</summary>
    public bool endMorph() => _s.LuaEndMorph();
}
