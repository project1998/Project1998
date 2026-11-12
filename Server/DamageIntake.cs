using Shared;

namespace Server;

/// <summary>Which of the five ways a player can take damage this blow is. The enum exists so the shape of an
/// intake is a property of its SOURCE rather than a bag of booleans re-chosen at each call site, and so a
/// test can enumerate the sources and assert something of every one of them (#28).</summary>
public enum DamageKind
{
    /// <summary>A creature's landed swing — <c>Session.ApplyMobHit</c>, driven by World.Tick.</summary>
    MobMelee,
    /// <summary>Another player's spell, or a self-cast — <c>Session.ReceiveSpellDamage</c>.</summary>
    PlayerSpell,
    /// <summary>Another player's weapon swing (PvP) — <c>Session.ReceiveMeleeDamage</c>.</summary>
    PlayerMelee,
    /// <summary>A creature's spell — <c>Session.ReceiveMobSpell</c>.</summary>
    MobSpell,
    /// <summary>The room itself: Sute's cold tiles — <c>Session.ReceiveEnvironmentDamage</c>.</summary>
    Environment,
}

/// <summary>
/// One blow arriving at one player, described rather than executed. <see cref="Session.TakeDamage"/> runs it.
///
/// <para>The per-blow facts live here (how much, from whom, what to say, what to log); the per-KIND facts —
/// which terms of the sequence run and in what order — live in <see cref="Terms"/>, keyed off
/// <see cref="Kind"/>, because they are properties of the damage source and not of the individual hit.</para>
/// </summary>
public sealed class DamageIntake
{
    public DamageIntake(DamageKind kind, double raw)
    {
        Kind = kind;
        Raw  = raw;
    }

    public DamageKind Kind { get; }

    /// <summary>Damage before any intake-side term. What "before" means differs by kind — a PvP melee figure
    /// has already had the victim's armor applied attacker-side (its rear x2 comes over as
    /// <see cref="DoubleForRear"/>), a mob swing has not.
    /// <para>A double, not an int (#200): a player's Spark-family spell arrives as 50 + level/2, and the live probe's
    /// self-casts read that half at the armour step (level 15 at AC 85: floor(57.5 x 1.85) = 106). An int here
    /// rounded it away first.</para></summary>
    public double Raw { get; }

    /// <summary>
    /// Whether this source walks past the Harden Body ward. Required — never defaulted — because the five
    /// sources do NOT all answer the same way, and the difference has to be a stated position rather than an
    /// omission. It was an omission: <c>ReceiveMobSpell</c> was written three days before
    /// <c>Session.DamageImmune</c> existed and never had the check retrofitted, and
    /// <c>ReceiveEnvironmentDamage</c> was copied from it a week later (#28).
    ///
    /// <para><b>RTK, read out of the reference server (rtklua/Accepted).</b> A player's HP comes off through
    /// one of three functions and only one of them carries the ward check:
    /// <c>Player.removeHealthExtend</c> (player.lua:164) opens by RETURNING OUTRIGHT on any of
    /// harden_body_poet / deaths_guard_poet / lifes_protection_poet / body_of_alignment_poet — no
    /// net-damage calc, no HP change, the blow does not land. <c>Player.removeHealthWithoutDamageNumbers</c>
    /// (player.lua:111) and the engine's plain <c>removeHealth</c> both subtract unconditionally. So "does
    /// the ward stop this?" is answered, per source, by which of the three that source calls.</para>
    ///
    /// <para><b>Creature spells: BLOCKED, unanimously.</b> Every creature spell in RTK that takes HP off a
    /// player ends in <c>target:removeHealthExtend(...)</c>, so the ward stops all of them. The instantaneous
    /// ones pass every term on (<c>amount, 1, 1, 1, 1, 0</c>): Spells/NPCs/ion.lua:9, call_lightning.lua:11,
    /// thunder_touch.lua:9 — the lightning line mob_ai_mythic.lua fires — plus freeze.lua:29 and
    /// stormstrike.lua:9. The over-time ones pass every term OFF (<c>damage, 0, 0, 0, 0, 0</c>) and floor the
    /// victim at 1 HP instead of killing, because they run per tick out of <c>while_cast</c>: burn.lua:51 and
    /// :59, venom.lua:61 and :74. The mythic boss's own abilities are the same call again —
    /// Instances/instance_boss.lua:288 (Rockslide) and :574 (the Baekdu Guardian's gust). A ward stops a
    /// boss's Thunder touch in RTK, so <see cref="DamageKind.MobSpell"/> honours it here. That is the one
    /// behaviour #28 changed, and it is the finding rather than a side effect of it.</para>
    ///
    /// <para><b>Room hazards: NOT blocked.</b> RTK's trap scripts split, and they split on exactly the axis
    /// that matters. The five you STEP ON — NPCs/trap/rogue_traps/{dart,death,spear,repeating_dart}_trap.lua
    /// and Ranger/pit_trap.lua — all call the plain <c>block:removeHealth(damage)</c>. The two that reach out
    /// and pick a target (bladestorm_trap.lua, Spy/toxic_spray.lua) use <c>removeHealthExtend</c> and are
    /// blocked. Sute's cold tiles are a stepped-on hazard, so <see cref="DamageKind.Environment"/> keeps this
    /// flag set.</para>
    ///
    /// <para>That plain <c>removeHealth</c> really is unguarded, in the ENGINE and not merely in the Lua
    /// that calls it: it binds to <c>pcl_removehealth</c> (rtk/src/map/sl.c:7848-7885), which sets the
    /// damage/crit fields and hands off to <c>clif_send_pc_healthscript</c> (clif.c:1229) — and neither
    /// function contains a ward test of any kind. So the stepped-on traps bypass Harden Body by direct
    /// evidence, not by inference from which Lua helper they happened to pick.</para>
    ///
    /// <para>The caveat that does remain is a different one, and it stands: Sute's cave is later content with
    /// no RTK script of its own (see docs/common/Deferred-Work.md, "Sute's combat kit is built from ONE
    /// eyewitness report"), so the stepped-on trap family is the nearest ANALOGUE RTK offers for a cold tile,
    /// not a reading of the cold tile itself. The mechanism is now sourced to the engine; the decision that a
    /// cold tile belongs in that family is still ours.</para>
    ///
    /// <para><i>Checked and rejected as a counter-example:</i> Instances/instance_boss.lua's rockdamage
    /// (:288) and gustdamage (:574) do go through <c>removeHealthExtend</c>, but they are not room damage —
    /// <c>instance_boss</c> is a mob AI table, the blows are attributed to <c>mob.ID</c> and announced as
    /// "&lt;mob&gt; casts Rockslide on you." and "[Baekdu Guardian]: Haha, pitiful fool!". They are creature
    /// abilities, and they belong to the MobSpell row above, which they support. Nothing in RTK damages a
    /// player from the ROOM except the traps.</para>
    ///
    /// <para><b>What the ward suppresses here that RTK still prints.</b> ion.lua sends its
    /// "&lt;mob&gt; attacks you with &lt;spell&gt; spell." line BEFORE removeHealthExtend, so an RTK player
    /// under a ward sees the line and takes nothing. Ours carries the line on the intake, so a blocked mob
    /// spell prints nothing (the caster's shout, animation and sound still go out from ApplyMobSpell, which
    /// runs first). Left as-is deliberately: our player-spell intake already swallows its own "X hits you
    /// with Y." the same way, and matching RTK on the mob side alone would make the two disagree inside our
    /// own codebase. Recorded rather than silently accepted.</para>
    ///
    /// <para><b>Two divergences this reading turned up and did NOT fix</b>, per the #21 ground rule that a
    /// divergence found while refactoring gets its own issue instead of a silent edit — they are
    /// <b>#77</b> and <b>#78</b>. (#77) RTK nets the player's armor on every damage path a player meets in
    /// one piece: the instantaneous creature spells and every PvP spell pass <c>ac = 1</c> to
    /// <c>Player.calculateNetDamage</c> (player.lua:228-244), and even the stepped-on traps run
    /// <c>calculateDamage</c> (scripts.lua:1183-1204) first. Ours skips AC on
    /// <see cref="DamageKind.MobSpell"/> and <see cref="DamageKind.Environment"/>, on the "magic ignores
    /// physical AC" rule — and <c>SuteAi.IceRayObservedDamage</c>'s 405-at-AC--22 reading was taken as the
    /// RAW figure precisely because of that assumption, so turning AC on means re-deriving Sute's whole kit.
    /// (#78) RTK's three damage helpers all run amplifier, deduction, THEN armor; every intake here applied
    /// armor before the deduction, and the mob swing also amplified on the wrong side of it. That one is
    /// settled now (owner, 2026-10-21: "multiply everything, round down once, minimum 1"): the terms are one
    /// product with one floor, so their order no longer matters. See <see cref="Terms"/>.</para>
    ///
    /// <para><b>Two damage paths this pipeline does not reach</b>, both in files owned by #26 while it is in
    /// flight, so they are named here rather than converted. (<b>#79</b>) <c>Session.NpcCastStormstrike</c>
    /// (Session.Spells.cs:2049) borrows <see cref="DamageKind.Environment"/> for a NAMED caster's spell,
    /// purely because that intake is the one that lets the caller supply the whole chat line — so a mythic's
    /// Stormstrike still walks through a ward, where RTK's own stormstrike.lua:9 is
    /// <c>removeHealthExtend</c> and is stopped by one. It is the last creature spell that ignores Harden
    /// Body, and the fix is small: the two kinds' term rows below are identical, so it wants
    /// <see cref="DamageKind.MobSpell"/> with a caller-supplied line — one sibling method in
    /// Session.MobSpells.cs, one line at a call site this branch must not touch. Note that the enum theory in
    /// Tests/DamageIntakeTests.cs cannot catch this class of bug: it proves each KIND behaves, not that each
    /// caller picked the right one. (<b>#82</b>) <c>Session.TickPoison</c> (Session.Spells.cs:1825) is a
    /// sixth damage path that never went through any of the five and so was outside #28's scope — it
    /// hand-rolls this very epilogue (HP, WakeUp, SendStats, mini-text, the over-head bar, the foe mark),
    /// which makes #28's "five copies" really six. RTK's equivalent ticks (burn/venom <c>while_cast</c>) are
    /// ward-gated like everything else, and since #82 (owner, 2026-10-21) so is ours: <c>TickPoison</c> asks
    /// <see cref="Session.DamageImmune"/> itself, on the heartbeat, and a warded tick takes no HP, the low-HP
    /// drop to 1 included (RTK's own branch for that skips its ward; ours does not, on the project owner's word). It still
    /// does not come through here, deliberately: the tick has no blow, no attacker and no terms, and this
    /// pipeline's foe mark enters another session's monitor.</para>
    /// </summary>
    public required bool IgnoresHardenBody { get; init; }

    /// <summary>The creature to record as <c>owner.attacker</c> (RTK's "last thing that actually landed a
    /// blow on you", which a Call of the Wild pet reads). Null for every non-creature source; stamped only
    /// once the blow is past the gates, exactly as before.</summary>
    public Mob? StampMobAttacker { get; init; }

    /// <summary>The positional "attacked from behind while both face the same way" x2 (RTK
    /// swingDamage.lua's <c>side == target.side</c> rule). Decided by the caller, which owns the geometry: the
    /// creature swing, and the PvP swing, whose attacker passes it over with its unfloored figure. Applied after
    /// the one floor (#78; RTK's order).</summary>
    public bool DoubleForRear { get; init; }

    /// <summary>The over-head bar's hit-animation byte (RTK: 33 normal, 255 critical). Ignored when
    /// <see cref="RollCritByte"/> is set.</summary>
    public byte CritByte { get; init; }

    /// <summary>A DEFERRED crit byte, for the one source that has to roll for it. Deferred because
    /// <c>Combat.RollCritChance</c> draws from <c>Random.Shared</c>, and RTK returns on an immune target
    /// before the damage calc runs at all — computing it at the call site would advance the shared stream on
    /// blows that never land. The pipeline invokes it at the original position: after the gates, before the
    /// damage math (and therefore before the durability rolls, which draw from the same stream).</summary>
    public Func<byte>? RollCritByte { get; init; }

    /// <summary>Sound id layered on the victim's own tile once the blow lands (RTK binds the landed-hit
    /// sound to the VICTIM). 0 = silent, which is every source but the creature swing.</summary>
    public int HitSound { get; init; }

    /// <summary>The 0x0A line the victim reads, or null for the sources that show only the HP bar. Built by
    /// the caller because only it knows whether there is anyone to name.</summary>
    public string? MiniText { get; init; }

    /// <summary>The other player in a PvP exchange — BOTH sides get marked, which is what an arena pet reads
    /// to pick someone to go for. Null for a self-cast and for everything a player did not do.</summary>
    public Session? PvpFoe { get; init; }

    /// <summary>The log line, given the damage that actually landed. A delegate rather than a string because
    /// every one of these reads the victim's HP AFTER the deduction.</summary>
    public Func<int, string>? LogLine { get; init; }

    /// <summary>Run last, and only when the blow landed AND the victim survived it — the creature on-hit
    /// spell proc, which must trail neither a blocked blow nor a corpse.</summary>
    public Action? AfterSurvivedHit { get; init; }

    /// <summary>Which terms of the damage sequence this kind runs.</summary>
    public readonly record struct Sequence(bool FloorRawToOne, bool AppliesArmor, bool DeductsDurability);

    /// <summary>
    /// The whole divergence matrix, in one readable table — the thing five copy-pasted intakes never let
    /// anyone see at once.
    ///
    /// <para><b>FloorRawToOne</b> — four sources raise a sub-1 raw figure to 1 before the terms, so a
    /// heavily-reduced hit still stings; the creature swing does not. Every kind's RESULT is raised to 1
    /// anyway (see the single floor in <see cref="Session.TakeDamage"/>), so this differs only for a raw
    /// below 1 under terms that multiply it up.</para>
    /// <para><b>AppliesArmor</b> — physical AC is netted at intake for a creature swing (the attacker side
    /// has no view of our gear) and for a player's spell (centralised there so no PvP spell path can forget
    /// it, and no path can apply it twice). A PvP melee figure arrives already netted, via
    /// <c>SwingTarget.Of(this)</c> on the attacker side, and unrounded. Creature spells and room damage skip
    /// AC entirely — "the AC pass belongs to swings, not spells" (#77, held for a retail reading).</para>
    /// <para><b>No order column.</b> There was one, <c>ArmorBeforeAmp</c>: the creature swing netted armor
    /// and THEN applied the sleep-family amplifier, a player's spell the other way round, and each step was
    /// rounded, so at AC -50 with a 1.5x amplifier a raw 101 landed 75 one way and 76 the other. Neither
    /// order was RTK's, whose three helpers multiply amplifier, deduction and armor UNROUNDED and floor once
    /// (swingDamage.lua:92, :101-102, :105-108; player.lua:210-244). #78, decided by the project owner on 2026-10-21
    /// ("multiply everything, round down once, minimum 1"): every term is one product and is floored once,
    /// so the order of the terms cannot change a result and the column has nothing left to say. The floor
    /// is the live-proven one (the Spark probe, <c>Combat.ApplyArmor</c>), not RTK's spell ceiling. The rear
    /// x2 stays outside it, after the floor, as RTK's swingDamage.lua doubles (:147-150).</para>
    /// <para><b>DeductsDurability</b> — RTK clif_deductarmor rolls every worn slot on a HIT; magic and the
    /// room do not touch gear.</para>
    /// </summary>
    public Sequence Terms => Kind switch
    {
        //                                 floor<1  armor  dura
        DamageKind.MobMelee    => new Sequence(false, true,  true),
        DamageKind.PlayerSpell => new Sequence(true,  true,  false),
        DamageKind.PlayerMelee => new Sequence(true,  false, true),
        DamageKind.MobSpell    => new Sequence(true,  false, false),
        DamageKind.Environment => new Sequence(true,  false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "unhandled damage kind"),
    };
}

public sealed partial class Session
{
    /// <summary>
    /// The one player-damage pipeline (#28). Every blow a player takes runs this sequence: immunity gate,
    /// wake, attacker stamp, armor x amplifier x deduction floored once and then the rear x2 (#78), HP,
    /// durability, stats, over-head bar, sound, chat line, our own PvP foe mark, log, death — and then, last of
    /// all, the foe's half of that mark (#174: it is the one statement here that enters ANOTHER session's monitor,
    /// so it goes where dropping ours costs nothing). The per-kind subset comes from <see cref="DamageIntake.Terms"/>.
    ///
    /// <para>It replaces five hand-copied versions of that sequence. Three checked
    /// <see cref="DamageImmune"/> and two did not, which is the finding this pipeline exists to make
    /// impossible to repeat: a new intake term is now one edit, and a new intake SOURCE has to state its
    /// answer to <see cref="DamageIntake.IgnoresHardenBody"/> out loud — with a citation, since that
    /// property's own note is where the RTK reading lives.</para>
    /// </summary>
    /// <returns>The damage actually applied, AFTER every term but NOT capped by the victim's remaining HP —
    /// so a killing blow reports how far past zero it went (what the vita strikes' overflow is computed
    /// from). 0 when nothing landed: already dead, replaced by a second login, or immune.</returns>
    internal int TakeDamage(DamageIntake intake)
    {
        if (IsDead) return 0;   // already down — don't re-trigger Die() while the revive delay is pending
        // Replaced by a second login (#168 item 5): the new session has already loaded this character, so a
        // death here would spill a pile onto the floor that the new session still carries — a duplication.
        // Nothing lands, which also stops a blow the tick queued before the kick. Volatile, no monitor.
        if (IsReplaced) return 0;
        // ---- Harden Body: total damage immunity -----------------------------------------------------------
        // RTK Player.removeHealthExtend (player.lua:164) opens by RETURNING OUTRIGHT if any of four wards is
        // up: harden_body_poet / deaths_guard_poet / lifes_protection_poet / body_of_alignment_poet — the
        // poet spell and its three alignment reskins. No net-damage calc, no HP change: the blow simply does
        // not land. The Scroll of Immortality grants the same ward (item_verbs.lua `hardenbody`, 16s, behind
        // RTK's armor-scaled success roll), which is what makes the scroll worth its name — and is our one
        // deliberate widening, since RTK's own scroll sets `harden_body` while removeHealthExtend looks only
        // at the four `*_poet` keys, so the RTK scroll grants a ward that stops nothing.
        //
        // Four of the five sources reach this gate; the fifth (room damage) walks past it, sourced. Which
        // ones and why is the note on DamageIntake.IgnoresHardenBody.
        //
        // Chongun's Invincibility and Ranger's Tree bark (Invincibility's body) are the two wards that stop SWINGS only
        // (the project owner, 2026-11-09, "Invincibility swings-only first", as RTK; Tree bark as RTK under P5). RTK's
        // hardBodies list holds both, and RTK reads that list to stop a blow in exactly
        // one place, Scripts/hitCritChance.lua:26, which every swing runs before its damage: a player's melee swing at a
        // player (clif.c clif_pc_damage) or at a creature, a creature's swing (mob.c's attack, Mobs/mob.lua's), and a
        // bow's shot (Items/Weapons/Bows/bow.lua). A spell, a player's or a creature's, lands through removeHealthExtend,
        // which does not test it (Spells/common/global_zap.lua:40; magicDamage.lua tests it but nothing calls that
        // script). Here the swings are the two melee kinds; every other kind asks DamageImmune, the five wards above,
        // exactly as before.
        bool warded = intake.Kind is DamageKind.MobMelee or DamageKind.PlayerMelee ? HardBodyWarded : DamageImmune;
        if (!intake.IgnoresHardenBody && warded) return 0;

        WakeUp(byDamage: true);   // being hit ends a Doze (RTK on_takedamage_while_cast) — see ReceiveSleep

        // RTK's `owner.attacker`: the last creature to actually land a blow on you. A Call of the Wild pet
        // reads this to decide what to defend you from — see World.Tick's pet block. Set on the LANDED hit,
        // not on aggro, which is the whole difference between a pet that holds a corner and one that charges
        // the moment anything looks at you.
        if (intake.StampMobAttacker is { } attackerMob)
        {
            LastMobAttackerId = attackerMob.Id;
            LastMobAttackerAt = Environment.TickCount64;
        }

        // Resolved here, at the position the mob swing's roll used to sit: past the gates (so a blocked blow
        // consumes no randomness) and ahead of the durability rolls (so it keeps its place in the stream).
        byte critByte = intake.RollCritByte is null ? intake.CritByte : intake.RollCritByte();

        var terms = intake.Terms;
        double net = intake.Raw;
        if (terms.FloorRawToOne && net < 1) net = 1;

        // ONE PRODUCT, ONE FLOOR (#78; owner, 2026-10-21: "multiply everything, round down once, minimum 1").
        // Each term below multiplies the unrounded figure, and Combat.FloorOnce floors it once at the end and
        // raises it to 1. Every step used to round on its own (the armor floor, then Math.Round, half to even,
        // after the amplifier and again after the deduction), so the order of the steps changed results by up
        // to 2 HP, and a deduction could round a landed hit down to 0: raw 9 on an AC -80 player under
        // Sanctuary was floor(1.8) = 1, then round(0.5) = 0. It is 1 now. With no amplifier and no deduction
        // this is exactly Combat.ApplyArmor's product and floor, so the common hit is unchanged, from behind too.
        //
        // Armor — RTK swingDamage.lua: finalDamage = floor(finalDamage * (1 + max(armor,-80)/100)). AC is
        // signed and LOWER is better, and gear/buff armor is an AC delta in the same units, so it simply ADDS
        // (a -4 garb takes 4 off your AC; see Session.Items.EquipTotals). A well-armored (very negative
        // effective AC) player takes as little as 20% of the raw swing, while a naked/positive-AC player takes
        // MORE than raw — armor can't fully negate a hit (-80 floor = min 20%).
        if (terms.AppliesArmor) net *= Combat.ArmorFactor(_char.Ac + Totals().armor, floor: -80);

        // Sleep-family amplifier: being dozed/slept makes the NEXT hit on you land harder (Doze 1.3x,
        // Sleep 1.5x). Consumed here, so it applies to one hit only — WakeUp above already broke the hold,
        // and together that is the whole point of the spell: set up one amplified opener.
        double amp = TakeDamageAmp();
        if (amp > 1.0) net *= amp;

        // RTK player.deduction: a flat damage-reduction multiplier from the sanctuary line / Baekho's Cunning
        // (1.0 normally, down to 0.5/0.15 while active).
        if (EffDeduction < 1.0) net *= EffDeduction;

        int dmg = Combat.FloorOnce(net);

        // The positional rear x2 comes AFTER the one floor and its minimum of 1, as RTK's swingDamage.lua doubles
        // its floored figure (:147-150). The project owner, 2026-10-21: "Round down once (minimum 1), then double for a hit
        // from behind. Ordinary hits from behind stay exactly as today." Doubling a whole number rounds nothing.
        if (intake.DoubleForRear) dmg *= 2;

        _char.Hp = (uint)Math.Max(0, (int)_char.Hp - dmg);
        // RTK clif_deductarmor: taking a hit rolls durability loss on every worn slot (not just armor —
        // the reference implementation checks the weapon slot here too).
        if (terms.DeductsDurability) foreach (var worn in _char.Equipment.ToArray()) DeductDura(worn);
        SendStats();

        byte hpPct = PlayerHpPercent();   // same for every peer — compute once, not inside the per-peer lambda
        _world.BroadcastWideArea(_char.Map, _char.X, _char.Y, p => p.DamageOver(_char.Id, hpPct, critByte));
        if (intake.HitSound != 0)
        {
            int sound = intake.HitSound;
            // 001.wav: layered on the 009 swing sfx World.Tick already played (RTK binds a landed hit to the
            // VICTIM, so it rings from OUR tile).
            _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.SoundAt(sound, _char.Id));
        }
        if (intake.MiniText is { } line) SendMiniText(line);
        Session? markFoeAfterwards = null;
        uint ourIdForFoe = 0;
        if (intake.PvpFoe is { } foe)
        {
            // Both sides remember the exchange — that's what a PvP-map pet reads to pick a person to go for.
            // OUR half is our own field under the monitor we already hold (MarkPvpFoe's EnterState is
            // re-entrant here). THEIR half is a different session's monitor, so it is deferred to the bottom
            // of this method — see the note there. #174.
            MarkPvpFoe(foe._char.Id);
            markFoeAfterwards = foe;
            ourIdForFoe = _char.Id;   // captured, so the deferred call reads no state of ours after the drop
        }
        if (intake.LogLine is { } log) Log.Info(log(dmg));

        if (IsDead) Die();
        else intake.AfterSurvivedHit?.Invoke();

        // ---- LAST, not in the middle: harden the one-critical-section invariant for every caller ----------
        //
        // Marking the FOE enters the ATTACKER's state monitor while we hold our own. All six shipped PvP intake
        // sites reach here on the attacker's handler thread, whose Dispatch is already wrapped in WithState, so
        // that thread already holds the attacker's monitor. MarkPvpFoe's EnterState is therefore re-entrant
        // under #29 rule 3 and never drops the victim's monitor on any shipped path.
        //
        // A descending acquisition, and the gap rule 2 would open in our monitor, is reachable only from a
        // caller that does not already hold the attacker's monitor. None is shipped today; a future queued or
        // timer-driven blow, including #29's channel-drained read loop, could introduce that shape. A revive
        // could then enter the gap between our HP write and death sequence.
        //
        // Keep the mark here as a hardening: TakeDamage through Die() remains one critical section however the
        // blow arrives, and the death sequence (or survived-hit epilogue) finishes before a possible drop.
        // This is the same last-statement remedy as the two calls at the bottom of Die() (#172, #177), one step
        // earlier in the damage path. PvpDeathOrderingTests pins the invariant with a bare-thread caller, which
        // is why its race is observable even though no production caller has that shape.
        markFoeAfterwards?.MarkPvpFoe(ourIdForFoe);
        return dmg;
    }
}
