-- Spell verbs: the LOGIC half of the data-driven spell system (the "verb"; SpellParams.csv is the "row").
-- Each verb is a function(ctx, row):
--   ctx = the SpellContext facade -> engine primitives (ctx:spendMana / ctx:damage / ctx:heal / ...) plus
--         read-only caster stats (ctx.will, ctx.level, ctx.might, ctx.hp, ctx.hasTarget, ...).
--   row = this spell's SpellParams.csv row, numbers already parsed (row.coeff, row.mana, row.amount, ...);
--         empty CSV cells are nil, so `row.base or 0` gives a safe default.
-- Bind a verb to a spell by adding a SpellParams.csv row whose `verb` column names it.
-- Edit THIS file or the CSV and run !reload to see changes live -- no server restart.

verbs = {}

-- =========================================================================================================
-- ARCHETYPE verbs (arch_*): the DEFAULT behaviour for a whole spell archetype, run for every spell of that
-- archetype (see Session.ApplyCast). The engine has already evaluated the spell's real formula and mana cost
-- from spell_effects.csv, so these read ctx.amount / ctx.mana rather than per-spell row params. A missing
-- arch_* verb makes that archetype fall back to its C# handler, so migration is one archetype at a time.
-- =========================================================================================================

-- Damage archetype: the standard direct magic attack. ctx:magicDamage does the whole sequence (mana check ->
-- resolve target -> spend mana -> deflect roll -> apply + XP). A DEFLECT still costs the mana: the spell was
-- cast, the target resisted it. Returns false only if the cast couldn't happen at all (no mana / no target),
-- so the cast animation is suppressed for those and those only. The target is the cast's one victim, and an NPC
-- is none (no blow can hurt it), so a cast at one is refused like a cast at nothing, silently and free.
function verbs.arch_damage(ctx, row)
  return ctx:magicDamage(ctx.amount, ctx.mana)
end

-- Heal archetype: restore the caster's own HP by the engine-evaluated amount (spell_effects.csv formula).
-- Mana is checked+spent first (return false on short mana so the cast animation is suppressed, matching C#);
-- ctx:heal caps at max HP and plays the sparkle/message.
function verbs.arch_heal(ctx, row)
  if not ctx:spendMana(ctx.mana) then return false end
  ctx:healTarget(ctx.amount)   -- lands on the AIMED target (player/mob/self); type-5 self-skills heal the caster
  return true
end

-- The refusal line for a blocked exclusivity slot, shared by EVERY verb that has one (arch_buff,
-- arch_targetbuff, arch_debuff, curse, ward). `already` = the slot is held by THIS VERY SPELL, still running.
-- RTK words that case differently from somebody else's spell being in the way -- paralyze.lua answers "You
-- already cast that spell." off its own `target.paralyzed`, static.lua answers "A more powerful spell is in
-- effect." off the very same flag -- and the only reason it had to hand-write the choice per script is that a
-- mob stored one boolean per mechanic, never the identity of whatever set it. Our slots carry the spell key,
-- so the choice is made once, here. A protection keeps its own more specific line: that isn't a same-type
-- collision, it's the target being immune, and it can never be the spell you just cast.
-- (Declared up here, not next to its first curse/ward caller, because a `local` further down the file would be
-- invisible to arch_buff/arch_debuff above it.)
local function blockMsg(cat, already)
  if cat == "protections" then return "The target is already protected." end
  if already then return "You already cast that spell." end
  return "Another spell of this type is in effect."
end

-- BUFF EXCLUSIVITY SLOTS -- RTK Spells/spellTables.lua, verbatim. Buffs were the ONE family that skipped the
-- checkIfCast guard every other status obeys: arch_buff only cleared its own key and re-applied, so Might was
-- spammable and, worse, Might + Spirit Strength (same slot in RTK, different keys) STACKED into +6 might. RTK
-- itself is inconsistent about which half of that it fixes -- rogue/might.lua refuses on checkIfCast(mights)
-- while mage/might.lua calls removeDuras(mights) and refreshes -- so the refusal wins for both, matching the
-- rest of this file and the player-visible rule "one spell of a type at a time".
--
-- Only the four slots whose members actually reach arch_buff / arch_targetbuff are listed. The rest of RTK's
-- tables (furies, invis, morphs, backstabs, flanks, enchants) belong to spells that Session.ApplyCast
-- intercepts BEFORE the archetype dispatch and that already own a dedicated timer slot with its own guard.
-- Cross-slot blocking (BLOCKS, further down) doesn't apply here: RTK gives none of these a cross-guard, so a
-- Might and a Bless deliberately still stack -- they're different slots.
--
-- The export ALREADY carries this for 17 of them, in spell_effects.csv's `cureCat` column: the extractor read
-- the list argument of RTK's `removeDuras(mights)` and wrote it there, and on a Buff row (which has nothing to
-- cure) that column can only mean the exclusivity slot -- so buff_slot() falls back to it. What the extractor
-- could NOT see is `checkIfCast(mights)`, the refusal half, which is why the whole rogue Might family and the
-- mage/poet Valor family come out blank; that gap is exactly this table.
local BUFF_CATEGORY = {}
local function set_slot(cat, keys) for _, k in ipairs(keys) do BUFF_CATEGORY[k] = cat end end
set_slot("mights", {
  "inspire_valor", "valor", "tigers_might",
  "might_rogue", "spirit_strength_rogue", "inner_blessing_rogue", "temper_rogue",
  "might_mage", "spirit_strength_mage", "inner_blessing_mage", "temper_mage",
  -- the mage/poet half is cast ON a target (arch_targetbuff), same slot
  "valor_poet", "strengthen_poet", "bless_muscles_poet", "power_burst_poet",
  "valor_mage", "strengthen_mage", "bless_muscles_mage", "power_burst_mage",
})
set_slot("blessings", {
  "greater_blessing", "bless_warrior", "sanctification_warrior",
  "tribal_gathering_warrior", "strength_of_purpose_warrior",
})
set_slot("potency", { "potence_warrior", "spirit_arm_warrior", "touch_of_the_bear_warrior", "sharpen_warrior" })
set_slot("shadowFigures", {
  "shadow_figure_rogue", "spirit_warrior_rogue", "natural_defense_rogue", "ohaengs_armor_rogue",
})
-- This spell's exclusivity slot, or nil for a buff that has none (and so keeps the old refresh behaviour).
local function buff_slot(ctx)
  local cat = BUFF_CATEGORY[ctx.spellKey]
  if cat then return cat end
  local cc = ctx.cureCat
  if cc ~= nil and cc ~= "" then return cc end
  return nil
end

-- Buff + TargetBuff: ONE body. The two archetypes differ only in WHO the buff lands on -- the caster (Buff has
-- no target arg on the wire at all) or whatever the cast is aimed at -- and in the duration a row with no
-- `durationMs` falls back to. Everything else is identical: the exclusivity slot, the mana ordering, the
-- deduction routing, the fx + flavor line. Running them as two parallel bodies is exactly how the slot guard
-- came to be enforced on one and not the other -- RTK has the same split, rogue/might.lua refusing where
-- mage/might.lua refreshes -- so they share a body and can no longer drift.
--
-- Mirrors verbs.ward, which has always covered its own self-cast (protections) and ally-cast (bolster/harden)
-- halves through one resolved-target primitive set: resolve once with ctx:buffTarget(mode), then every step
-- reads that resolution.
--
-- Mana is CHECKED up front but DEBITED only once the cast commits, so neither a no-target abort nor an
-- occupied slot spends anything.
local function apply_buff(ctx, mode, fallbackDur)
  if not ctx:enoughMana(ctx.mana) then return false end
  if not ctx:buffTarget(mode) then return false end   -- silent: a cast that finds nothing says nothing
  local cat = buff_slot(ctx)
  -- Slot check before the debit: a re-cast into an occupied slot is a plain "no", not a refresh you paid for.
  if cat and ctx:buffHasStatus(cat) then ctx:say(blockMsg(cat, ctx:buffAlreadyCast())); return false end
  local dur = ctx.durationMs > 0 and ctx.durationMs or fallbackDur
  -- Deduction is an incoming-damage MULTIPLIER in its own scalar slot, not a stat delta, and lands on players
  -- only; applyDeduction refuses a mob without spending anything, so the debit comes after it commits.
  if ctx.buffStat == "deduction" then
    if not ctx:applyDeduction(tonumber(ctx.buffAmt) or 1.0, dur) then
      ctx:say(ctx.spellName .. " has no effect on that."); return false
    end
    ctx:debitMana(ctx.mana)
    return true
  end
  ctx:debitMana(ctx.mana)
  -- Raw '|'-separated row fields: a multi-stat buff is split engine-side, so one call covers refresh-then-add
  -- for every stat plus the single fx + flavor line (e.g. Might -> "Your muscles develop." then HandleCast's
  -- central "You cast Might.").
  ctx:applyBuff(ctx.buffStat, ctx.buffAmt, dur, cat or "")
  return true
end

-- BARRIERS (#334). Two buff slots raise a barrier on the four tiles beside the caster once the run is applied:
-- Barrier and its twins (spell_effects.csv cureCat `barriers`) close them to creatures, Human Barrier and its
-- twins (`humanBarriers`) to every player but the caster. What stands there at the cast is held until the
-- barrier wears off, and the tiles stay where they were cast. The Nexus Atlas Poet page (2002-12-30): "4 Way
-- Invisible Blockade surrounds the caster, disabling any animals [players] from walking next to them. If an
-- animal [a player] is in one of those spaces, they are paralyzed until the barrier wears off." The engine
-- half is Server/Session.Barrier.cs.
local BARRIER_KIND = { barriers = "creatures", humanBarriers = "players" }

-- The two archetype names both still have to exist: Session.ApplyCast dispatches on them by name, and
-- SpellScript.HasVerb is what decides whether the archetype falls back to its C# handler. The fallback
-- durations are the only per-archetype DATA, so they're arguments rather than a branch.
function verbs.arch_buff(ctx, row)
  if not apply_buff(ctx, "self", 60000) then return false end
  local barrier = BARRIER_KIND[buff_slot(ctx)]
  if barrier then ctx:raiseBarrier(barrier, ctx.durationMs) end
  return true
end
function verbs.arch_targetbuff(ctx, row) return apply_buff(ctx, "target", 300000) end

-- Debuff archetype: the hostile crowd-control family. The export row's `debuff` column says WHICH kind this
-- spell is, and the four behave differently in RTK -- before this they all ran one generic freeze, so Blind
-- froze a mob solid instead of blinding it and Doze was indistinguishable from Paralyze.
--
--   kind      category   holds?  blinds?  repeats its animation?   RTK source
--   blind     blinds       no      yes      no                     mage/blind.lua (+ dark_veil/winters_shadow/ice_glare)
--   paralyze  paras        yes     no       no                     mage/paralyze.lua (+ spirit_leash/cold_binds/lockup), static.lua
--   sleep     sleeps       yes     no       YES, every 1s          mage/doze.lua + sleep.lua (while_cast -> sendAnimation(2))
--   slow      slows        yes     no       no                     the leftover Debuff rows with no verb of their own
--
-- The category is an EXCLUSIVITY slot (RTK checkIfCast): while one runs, the same kind cannot be re-applied --
-- no stacking, no refreshing, no chain-casting something into a permanent hold. It has to run its course (or
-- be cured) first. Different kinds are different slots, so blind+paralyze deliberately still stack.
-- `amp` is the DAMAGE AMPLIFIER the hold leaves on its victim: NexusAtlas gives both sleep spells one --
-- Doze "The next attack upon the target will do 1.3x the normal damage", Sleep 1.5x -- and that is what RTK's
-- `target.sleep = 1.3` and its `sd->sleep != 1.0f` guards have been all along: a float multiplier whose
-- default is 1.0, doubling as the "is held" flag. Read as a bool it looks like a no-op, which is how it got
-- missed. 1.0 = no amplification.
local DEBUFF = {
  blind    = { cat = "blinds", hold = false, blind = true,  repeatMs = 0,    fallback = 60000, amp = 1.0 },
  paralyze = { cat = "paras",  hold = true,  blind = false, repeatMs = 0,    fallback = 20000, amp = 1.0 },
  sleep    = { cat = "sleeps", hold = true,  blind = false, repeatMs = 1000, fallback = 10000, amp = 1.3 },
  slow     = { cat = "slows",  hold = true,  blind = false, repeatMs = 0,    fallback = 20000, amp = 1.0 },
}
-- Sleep (lvl 70) amplifies harder than Doze (lvl 82) per the atlas -- 1.5x against 1.3x -- so the per-kind
-- default above is overridden per spell here. RTK writes 1.3 in both scripts; the atlas distinguishes them.
local AMP_BY_SPELL = { sleep_mage = 1.5, sweet_musings_mage = 1.5, essence_of_poppies_mage = 1.5, stillness_mage = 1.5 }

-- Take-hold chance where the SPELL DATA doesn't state one. RTK gives a failure roll to exactly one member of
-- this family -- paralyze, at `70 + will*0.2307`, which the export captured in spell_effects.csv's `chance`
-- column and ctx.chance evaluates per cast. Everything else in RTK lands 100% of the time, which is why blind
-- could be held on a creature indefinitely and a curse never missed.
--
-- STATIC is the counter-example, and RTK has it backwards. static.lua carries no roll at all, yet the real
-- client table flags it SplCanFail=1 while paralyze_mage is SplCanFail=0 (Spells.csv) -- exactly inverted
-- from the scripts, so the original game failed statics and RTK dropped that and invented paralyze's roll
-- instead. Live measurement (2026-08-22), three samples: 19/96 at Will 18, 22/123 at Will 19, 19/111 at
-- Will 20 -- pooled 60/330 = 18.2% [95% CI 14.4-22.7%]. That kills RTK's 70+will*.2307 for this spell (it
-- predicts 74%, ~12 sigma out) and it kills the other candidate too: an 18% rate under the SplCanFail
-- RollDeflect staircase needs prot ~16, but every level<=25 mob sits at Protection 0-1 and Will <= 25, so
-- deflect gives them 100% (165 of 180), 90% (10) or 81% (1). The fail roll is CASTER-side.
--
-- WHAT THE DATA DOES NOT SHOW IS THE WILL SLOPE, and the column below is honest fiction on that axis. The
-- three samples span two Will points; they are homogeneous (chi2 0.26 on 2 df) and the fitted logistic slope
-- is -0.089 +/- 0.180 (z -0.49, pointing the WRONG way). That is not evidence against Will-dependence -- a
-- 1%/point slope moves the rate 2.0pp across Will 18-20 against a 5.4pp standard error, so this experiment
-- could never have seen it (z 0.37; it would take ~3000 casts per arm). `player.will` is therefore the
-- WORKING MODEL, chosen because it hits 18.2% dead-on at the measured Wills; flat 18% and 10+will/2 fit
-- equally well and disagree wildly at endgame (100% vs 18% vs 60% at Will 100). ONE sample at Will 40 --
-- ~40 casts -- separates them: `will` predicts 40%, flat predicts 18%, a 22pp gap. Until that exists this
-- cell is a fit to three points, not a law.
--
-- These numbers are OURS, not archive values: nothing in the scraped data or the Lua pins a rate for the
-- others. They are the balance surface for "an offensive status should sometimes just fail" -- edit and
-- @reload, no rebuild. A per-spell `chance` formula in spell_effects.csv always wins over the table.
local HOLD_CHANCE = { blind = 75, sleep = 80, paralyze = 100, slow = 90 }

function verbs.arch_debuff(ctx, row)
  local kind = ctx.debuffKind
  local d = DEBUFF[kind] or DEBUFF.slow
  if not ctx:enoughMana(ctx.mana) then return false end

  -- targetKind (not hasTarget) because Doze can land on a PLAYER: hasTarget resolves MOBS only, so a doze
  -- aimed at someone would have answered "finds no target" before ever reaching holdTarget's player branch.
  if ctx.targetKind == "none" then return false end   -- silent: a cast that finds nothing says nothing

  -- Refuse an occupied slot BEFORE anything is charged, so a re-cast at something already held is a plain
  -- "no" rather than a roll you can lose and pay for. (holdTarget re-checks and is the real authority; this
  -- is the cheap early out.)
  if ctx:hasStatus(d.cat) then ctx:say(blockMsg(d.cat, ctx:alreadyCast(d.cat))); return false end

  -- Then the target itself, before the deflect and the roll below can charge, as curse asks canCurse before its
  -- roll: holdTarget's own refusals (a player for anything but the Doze family, a player off a PvP map, an NPC),
  -- each free. The roll used to come first, so a Doze at a player in a town lost it one time in five and paid for
  -- a target it could never have held: "Something went wrong.", the pose, 30 mana and Doze's aether.
  if not ctx:canHold(d.cat) then return false end

  -- A DEFLECT STILL COSTS THE MANA: the spell was cast and the power left you, and the target resisting it
  -- is their achievement, not a refund. (RTK returns before its debit; a free deflect would mean a resistant
  -- target costs nothing to keep hammering.)
  if ctx:deflected() then ctx:debitMana(ctx.mana); ctx:say("Your magic has been deflected."); return true end

  local dur = ctx.durationMs > 0 and ctx.durationMs or d.fallback
  -- A FIZZLE COSTS THE MANA for the same reason: re-casting until it lands would otherwise be free, which
  -- drains the failure rate of any meaning.
  local ch = ctx.chance
  if ch >= 100 then ch = HOLD_CHANCE[kind] or 100 end
  if ch < 100 and not ctx:roll(ch) then ctx:debitMana(ctx.mana); ctx:say("Something went wrong."); return true end

  -- holdTarget owns the boss cap, and asks canHold's questions again of the same target, in case its slot filled
  -- or it left since (a target that has left is nobody). It returns false without spending anything.
  if not ctx:holdTarget(d.cat, dur, d.hold, d.blind, d.repeatMs) then return false end
  local amp = AMP_BY_SPELL[ctx.spellKey] or d.amp
  if amp > 1.0 then ctx:amplify(amp, dur) end   -- the next hit on them lands harder, then the hold breaks
  ctx:debitMana(ctx.mana)
  return true
end

-- Cure archetype: RTK removes a whole CATEGORY of durations from the target (the cure's CureCat, e.g. a
-- 'curses' cure like Atone clears pestilence; a 'venoms' cure clears poisons). Now that curses are real
-- categorized statuses (see the `curse` verb), this actually dispels them from the caster, not just spends mana.
function verbs.arch_cure(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:cureCategory(ctx.cureCat)
  return true
end

-- =========================================================================================================
-- PER-SPELL verbs: bound to one spell by a SpellParams.csv row whose `verb` column names it (these take
-- precedence over the archetype path). Their numbers come from the row (row.coeff, row.base, ...).
-- =========================================================================================================

-- Direct magic damage to the current target. A row with numbers of its own strikes for base + Will*coeff and costs
-- `mana` (Freeze, the Poet whip's proc: a flat 112 for 800). A row that only binds the verb takes both from the
-- spell's spell_effects.csv row instead, ctx.spellAmount and ctx.spellMana, read as the Damage archetype reads them:
-- mage Bolt, on the Spark family's row since #200 (owner, 2026-10-22), 50 + level/2 for 20 mana, where its own row
-- used to say 2 + 1.5 x Will for 4, which no source gives. Bolt keeps this verb and not the archetype's because the
-- refusals below are Bolt's, by the project owner's decisions; the archetype answers several of them differently.
-- A row with a mana but no base or coeff mixes the two: its own mana, and the export row's amount (ctx.spellAmount).
-- No such row exists today. For a spell with no spell_effects.csv row, ctx.spellAmount has no amount to give and
-- raises, before the spend, so the cast fails free with "<spell> isn't working right now." (SpellScript.Run).
-- The amount goes to ctx:damage unrounded, to the one floor at the armour step (#78, #361).
-- (Target resolution, deflect, the HP-bar packet, death + XP are all handled inside ctx:damage.)
--
-- An NPC is refused first, silently and free: no blow can hurt it, so ctx:damage strikes nothing there, and this
-- verb used to charge for that (Bolt 4 mana, the Poet whip's Freeze proc 800). Silence is the answer the Damage
-- archetype and the generic damage spells give a cast that finds nothing.
--
-- A cast at nothing is refused the same way, on every map and whatever the caster's mana: no mana, no line, no pose.
-- RTK's zap returns at a missing target before anything else (Spells/common/global_zap.lua:8-10). Off a PvP map that
-- was the project owner's 2026-10-21 decision sheet item 9a, and it used to cost 4 or 800 mana, the pose and "You cast X." for a
-- blow that struck nothing. On a PvP map (owner, 2026-10-21) the unaimed cast used to land on its caster, by the
-- resolver's fallback, and a cast aimed at an id that found nobody paid for nothing; both are free and silent now.
-- With too little mana (the project owner: "silent like RTK") it used to say "Not enough mana to cast Bolt."; it says nothing.
-- An id aimed at the caster is not nothing, and still strikes them where PvP is on. ctx.castAtNothing asks it.
--
-- A player off a PvP map is refused before the spend too (item 9b): "You can't attack that target.", the line the
-- blow's own PvP gate sends, which it used to send after the debit, so the refusal cost the mana, the pose and
-- "You cast X." as well (the Poet whip's Freeze proc 800 at any player it faced in a town). RTK refuses there before
-- any cost (global_zap.lua:26-30).
--
-- At a real target that question is asked only when the cast can be paid for, so a caster short of mana hears
-- spendMana's own line, as RTK's zap orders it (mana at global_zap.lua:14-17, before the PvP refusal).
function verbs.magic_damage(ctx, row)
  if ctx.victimIsNpc then return false end                    -- silent, free: an NPC is no victim
  if ctx.castAtNothing then return false end                  -- silent, free, whatever the mana: nothing to strike
  local own = row.base or row.coeff                           -- numbers of its own, or only the binding (Bolt)
  local mana = row.mana or (own and 0 or ctx.spellMana)
  if ctx.mp >= mana and not ctx:canStrike() then return false end   -- a player it may not strike here: free, with its line
  local amount = own and (row.base or 0) + ctx.will * (row.coeff or 1.0) or ctx.spellAmount   -- read before the spend
  if not ctx:spendMana(mana) then return false end            -- declined (no mana) -> no "You cast X."
  ctx:damage(amount)
  return true
end

-- =========================================================================================================
-- Baekho's Cunning (RTK Spells/Subpaths/Baekho/baekhos_cunning.lua) — the showcase for COMPOSED verbs: a
-- single spell that is simultaneously a rage MULTIPLIER, a damage-REDUCTION (deduction), and positional
-- STANCE grants, plus a stateful tier machine. It fits NO single archetype; it just composes primitives.
-- Recast to climb Cunning 1->6, each tier stronger and far pricier; each cast supersedes the lesser furies
-- (ctx:rage overwrites the single rage slot). The tier TABLE is an RTK constant so it lives here in the verb.
-- CLASSIC (4.95-era) Cunning chart, from the Rogue Tutor Melalye "Baekho's Cunning" board post in the scraped
-- archive (canonical, > RTK per user). Per tier: mana, damage multiplier (rage), deduction (incoming-damage
-- mult), and cumulative special grants (Cun2 = Backstab, Cun3 adds Flank). ded goes 0/15/30/55/85% reduction
-- -> mult 1.00/0.85/0.70/0.45/0.15; at Cun4-5 this EXCEEDS Sanctuary's 50%, so casting Sanctuary DOWNGRADES a
-- high-Cunning rogue (Sanctuary overrides Cunning's slot; see ApplyCunningDeduction). The 6th cunning (~262k
-- mana) is "no better than the 5th" in the archive, so 5 is the real cap.
--
-- THE 55%/85% AT CUN4-5 CONFLICTS WITH NEXUS ATLAS, WHICH SAYS 45%/60%, and the board post wins. Both sources
-- are the SAME ERA -- identical mana and identical 4x-8x multipliers, and the 2004-05-05 archive news chart
-- still lists those multipliers, so nothing about this spell was rebalanced between them (the 6x/7x/9x/10x/12x
-- + 8/16/24/32/40% numbers in RTK and in the late Atlas mirror are the LATER rebalance, post-2004). Within one
-- era the tiebreak is method, not date: Melalye's post is a tutor's in-depth retest that opens by saying "many
-- previous charts were wrong in parts", and it states the reduction TWICE -- once as a chart and once as a
-- measured damage table (Cun1-5 = 100k/85k/70k/45k/15k taken against a fixed attacker), which is 0/15/30/55/85
-- read straight off the numbers, with named testers. The Atlas figure is a fan-site table with no method shown,
-- and is exactly the kind of "previous chart, wrong in parts" the post describes -- right in every column but
-- this one. Change it only if a DATED pre-2003 source turns up showing 45/60.
--
-- ONE 938s RUN, ARMED ONCE — same shape as Chung Ryong's Rage, and for the same reason: RTK's
-- baekhos_cunning.lua calls setDuration("baekhos_cunning", 938000) in its first-cast branch ONLY, and every
-- tier-up branch resets the AETHER and nothing else. A climb therefore inherits what REMAINS of the run.
--
-- THIS TABLE USED TO CARRY A PER-TIER `dur` (938000/788000/638000/488000/338000) taken from the archive chart,
-- re-armed fresh on every tier-up. Those five numbers are 938000 - 150000*(n-1), and 150000 is the aether:
-- the board post recorded the time REMAINING when each tier was reached in one fixed 938s run, climbing as
-- fast as the cooldown allows. It was a reading of RTK's model, not a contradiction of it. Re-arming them made
-- the run extendable without limit — climb slower than the aether and each tier opened a fresh window (cast at
-- t=0, climb at t=900 -> the run ends at 1688s instead of 938s, and so on up).
--
-- `msg` is again the CLIENT'S OWN text (Atlas npcsubpath 2003-08-22, and RTK's Lua agrees word for word):
-- the first cast reads differently and every climb after it repeats the same line. What was here before --
-- "Baekho sharpens your instincts (x5 damage)" -- was invented, and it printed ON TOP of the central
-- "You cast Baekho's cunning." because the verb never marked the cast narrated.
--
-- THE `four` COLUMN IS THE TIER-4 GRANT, and it was missing until Poet Tutor SkaDemon's "Rage and Cunning"
-- board post turned up naming what each tier actually GIVES you: "Fury / Backstab / Flank / 4 Way Attack /
-- Super Sanctuary". Cunning 4 opens the swing onto all four adjacent tiles at once, with no side roll.
-- Dalsichvedin's 2004-05-05 damage chart agrees from the other direction, listing targets per tier as
-- 1/2/3/4/4 -- and that 3 at Cunning 3 is only reachable if Flank is ONE side, which is exactly how
-- Session.SwingTargets rolls it. Tier 5's "Super Sanctuary" is the 85% deduction, not a targeting change,
-- so `four` simply stays on. (SkaDemon's mana column is rounded poet-facing guidance -- 4100/16000/42000/
-- 130000, "be ready to spire accordingly" -- so Melalye's measured minima below are kept.)
local CUNNING = {
  [1] = { mana = 3000,   rage = 4, ded = 1.00, back = false, flank = false, four = false,  --  0% reduction
          msg = "You feel your fighting skills improve." },
  [2] = { mana = 4200,   rage = 5, ded = 0.85, back = true,  flank = false, four = false,  -- 15%, +Backstab
          msg = "Baekho increases your awareness and skill." },
  [3] = { mana = 15634,  rage = 6, ded = 0.70, back = true,  flank = true,  four = false,  -- 30%, +Flank
          msg = "Baekho increases your awareness and skill." },
  [4] = { mana = 46658,  rage = 7, ded = 0.45, back = true,  flank = true,  four = true,   -- 55%, +4-way
          msg = "Baekho increases your awareness and skill." },
  [5] = { mana = 117667, rage = 8, ded = 0.15, back = true,  flank = true,  four = true,   -- 85% (max)
          msg = "Baekho increases your awareness and skill." },
}
local CUNNING_MAX      = 5
local CUNNING_AETHER   = 150000   -- ~2.5 min cooldown between tier-ups (RTK/archive setAether)
local CUNNING_DURATION = 938000   -- RTK `setDuration("baekhos_cunning", 938000)`, first cast only

function verbs.baekhos_cunning(ctx, row)
  if ctx:onCooldown("baekhos_cunning") then
    ctx:say("Baekho's power is not yet ready to grow.")
    return false
  end
  -- Only continue the ladder if the stance is still active; if it lapsed, hasDuration is false -> start fresh.
  local active = ctx:hasDuration("baekhos_cunning")
  local tier = active and ctx:reg("baekhos_cunning") or 0
  if tier < 0 or tier > CUNNING_MAX then tier = 0 end
  if active and tier >= CUNNING_MAX then
    ctx:say("You have reached your max potential.")
    return false
  end

  local nt = tier + 1
  local c = CUNNING[nt]
  if not ctx:spendMana(c.mana) then return false end        -- huge, tier-scaled cost

  -- A climb rides out the run already burning; only a fresh cast opens one. Every effect below is armed on
  -- that one deadline so the whole stance lapses together, exactly as RTK's single named duration does.
  local left = active and ctx:durationLeft("baekhos_cunning") or 0
  local dur  = left > 0 and left or CUNNING_DURATION
  if not active then ctx:setDuration("baekhos_cunning", dur) end
  ctx:setReg("baekhos_cunning", nt)
  ctx:rage(c.rage, dur)                                      -- whole-swing xN (supersedes lesser furies)
  ctx:deduction(c.ded, dur)                                  -- take less damage (own slot; Sanc overrides it)
  ctx:stance("backstab", c.back,  dur)                       -- extra reachable tile: behind you
  ctx:stance("flank",    c.flank, dur)                       -- extra reachable tile: ONE side, rolled blind
  ctx:stance("fourway",  c.four,  dur)                       -- Cun4+: every adjacent tile, no roll
  ctx:setCooldown("baekhos_cunning", ctx.spellAether > 0 and ctx.spellAether or CUNNING_AETHER)
  ctx:fxSelf()                                               -- graphic + sound from the spell_effects row, as in
                                                             -- chungryong_rage. animation 35 is the WIRE value,
                                                             -- so it draws Effect.tbl INDEX 34 -- the white
                                                             -- tiger. Baekho. Verified against the art.
  ctx:say("[Cunning " .. nt .. "] " .. c.msg)
  ctx:narrated()                                           -- this line REPLACES the central "You cast X."
  return true
end

-- Heal the AIMED target: flat `amount` plus optional Will scaling (`willcoeff`), costing `mana`. Both spells
-- that use this verb (heal_mage, mend_wounds_mage) are SplType 2 "Which target? >" spells, so the heal must
-- land on whoever the cast is aimed at (player/pet/self), not always the caster. healTarget routes by SplType:
-- a Type-5 self-skill would still heal the caster, a Type-2 lands on the target (self when nothing is aimed).
-- (Using ctx:heal here made both spells self-cast unconditionally, ignoring the selected target.)
function verbs.heal(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return false end   -- declined (no mana) -> no "You cast X."
  ctx:healTarget((row.amount or 0) + ctx.will * (row.willcoeff or 0))
  return true
end

-- Restore the caster's own mana by a flat `amount` (no mana cost, obviously).
function verbs.restore_mana(ctx, row)
  ctx:restoreMana(row.amount or 0)
  return true
end

-- Timed self-buff: spend `mana`, then raise `stat` by `amount` for `duration` ms (default 60s). `stat` is one
-- of the Totals() keys: might/will/grace/hp/mp/armor/hit/dam. Re-casting the same spell refreshes, not stacks.
-- `armor` is the odd one out: it is an AC DELTA, and MORE AC = MORE DAMAGE TAKEN, so a ward is NEGATIVE
-- (bolster -4) and a curse POSITIVE (pestilence +5). Same units as items and mobs.
function verbs.buff(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return false end   -- declined (no mana) -> no "You cast X."
  ctx:buff(row.stat, row.amount or 0, row.duration or 60000)
  return true
end

-- =========================================================================================================
-- TIER-2 COMBAT STANCES (rage / enchant / stealth / backstab / flank): each just ARMS a timed melee modifier
-- on the caster, so they share the CastArch-style dispatch (Session.CastStanceArch): the C# classifier
-- (Content.RageAmountFor / EnchantFor / IsStealthSpell / fx.CureCat) still identifies the spell and pre-computes
-- the RTK numbers into ctx.amount / ctx.mana / ctx.durationMs; the LOGIC (guard, debit, arm, fx) lives here.
-- No SpellParams row needed — these run for every spell the classifier recognises, falling back to the C#
-- CastRage / CastEnchant / CastStealth / CastStance handler if the verb is absent (migrate-one-at-a-time).
-- =========================================================================================================

-- Rage tier: a whole-swing damage MULTIPLIER (Wolf's/Tiger's/Dragon's Fury, Baekho's Rage). RTK blocks casting
-- ANY fury while one is already active — you wait it out, no overwrite to a stronger tier. amount = the tier's xN.
function verbs.stance_rage(ctx, row)
  if ctx.rageActive then ctx:say("You are already benefiting from a fury."); return false end
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:rage(math.floor(ctx.amount), ctx.durationMs > 0 and ctx.durationMs or 60000)
  ctx:fxSelf()
  return true
end

-- Enchant tier: multiplies ONLY the raw weapon-swing term (not the whole swing). Also blocked while one is
-- active. NO DURATION -- it lasts until the weapon is taken off or swapped, or the character logs off (the
-- tutor spell list says exactly that for all five tiers, and RTK's ingress.lua has an uncast hook rather than
-- a timer). This used to pass `ctx.durationMs > 0 and ctx.durationMs or 60000`, and since every enchant row
-- in spell_effects.csv has a BLANK durationMs, that fallback capped Ingress at 60s -- against a fury's 625s.
function verbs.stance_enchant(ctx, row)
  if ctx.enchantActive then ctx:say("This spell is already active."); return false end
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:armEnchant(ctx.amount)
  ctx:fxSelf()
  return true
end

-- Stealth (Rogue Invisible/Spirit's Form/…): arms a one-shot 9x sneak-attack burst; landing the next swing
-- strips it (handled engine-side in PlayerSwingDamage). Here we just spend mana and arm the timer.
function verbs.stance_stealth(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:armStealth(ctx.durationMs > 0 and ctx.durationMs or 60000)
  ctx:fxSelf()
  return true
end

-- Backstab / Flank (Warrior positional stances): arm the free positional-crit angle for the duration.
local function arm_positional(ctx, which)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:stance(which, true, ctx.durationMs > 0 and ctx.durationMs or 60000)
  ctx:fxSelf()
  return true
end
function verbs.stance_backstab(ctx, row) return arm_positional(ctx, "backstab") end
function verbs.stance_flank(ctx, row)    return arm_positional(ctx, "flank")    end

-- Venom (RTK mage/venom.lua & kin): a damage-over-time poison, on a CREATURE OR A PLAYER.
--
-- TWO SOURCES, and they agree once you do the arithmetic. Listed oldest first, which is also most-in-era:
--   tswolf.com, 2001-02-23 (4.83/4.95 era -- the closest source we have):
--     "Poisons Target for a Random amount of time. A Poisoned Person recieves a 160 second poison that will
--      bring them down to low health, but not kill them."
--   NexusAtlas, 2002-12 (later, but the only one that quotes a damage number):
--     "Poisons monster targets for a random amount of time. Does 1000 damage a second, disregarding armor
--      class, on other players. Does more damage per second to animals. Poison will not kill a target but
--      rather bring them to the lowest possible health."
--
-- So a CREATURE gets the random window; a PERSON gets a fixed 160s. 160s x 1000/s = 160,000 total against a
-- never-kill clamp, which means the rate only sets HOW FAST you reach the floor and tswolf's "brought down to
-- low health" is simply the steady state of sitting there. It is also self-scaling: anyone whose pool exceeds
-- ~160k rides the whole window out without ever flooring, so venom decides fights early and merely hurts at
-- cap -- no special case needed for level 99.
--
-- Neither target can ever take the killing blow: the tick is clamped to leave 1 vita and keeps flashing.
-- The creature half reuses the SAME poison engine the Rogue poison-dart trap drives (World.PoisonMob).
--
--   row.amount  per-tick cap, creature ("more damage per second to animals" -- MaxHp*1% per 1.5s tick)
--   row.base    random window lower bound, ms (creature)
--   row.duration        upper bound, ms -- RTK's usual 30000; Burn's window is a FIXED 75s (base = duration)
--   row.flat    > 0 swaps the creature's MaxHp*1% for a fixed per-tick amount (Burn alone works that way)
--   row.pcDps     PLAYER damage per SECOND -- atlas 1000 read as a rate => 1500/tick. UNVERIFIED.
--   row.pcPerTick PLAYER damage per TICK, overrides pcDps when set. The rival reading of the same
--                 sentence: 1000 PER TICK (=667/s), which is the shape RTK's own code uses (MaxHp*1%
--                 per 1500ms tick, capped 2000/tick -- and a typical 1% lands near 1000, so the
--                 description may just be someone eyeballing the per-tick number). Set it to 1000 to
--                 switch readings. Only the never-kill floor moves: ~160k max HP vs ~107k.
--   row.pcDurMs   PLAYER window, ms       -- tswolf 160000, fixed, overrides the random roll
--
-- Both player numbers are one @reload away from being retuned once someone measures them on a live server.
-- Blocked if the target is already venomed (checkIfCast(venoms)), and PvP-gated for another player.
function verbs.venom(ctx, row)
  local mana = row.mana or 60
  if not ctx:enoughMana(mana) then return false end
  if not ctx:applyVenom(row.amount or 1000, row.base or 1500, row.duration or 30000, row.flat or 0,
                        row.pcDps or 1000, row.pcDurMs or 160000, row.pcPerTick or 0) then return false end
  ctx:debitMana(mana)
  return true
end

-- Blind (RTK Spells/NPCs/blind.lua): the creature stops being able to find anything -- it drops whoever it
-- was fighting, never acquires anyone new, and stands still rather than wandering, though it will still swing
-- at somebody who walks into arm's reach. Shares the `blinds` slot (and so the no-re-cast rule) with the mage
-- blind family, which reaches the same primitive through arch_debuff. row.mana, row.duration, row.chance.
function verbs.blind(ctx, row)
  local mana = row.mana or 300
  if not ctx:enoughMana(mana) then return false end
  if not ctx:canHold("blinds") then return false end   -- the target first, free, before the roll (see arch_debuff)
  local ch = row.chance or 75
  if ch < 100 and not ctx:roll(ch) then ctx:debitMana(mana); ctx:say("Something went wrong."); return true end
  if not ctx:holdTarget("blinds", row.duration or 10000, false, true, 0) then return false end
  ctx:debitMana(mana)
  return true
end

-- =========================================================================================================
-- AREA (4-way) spells: the two ladders that hit the four tiles around you instead of a target.
--   mage  zap  -- Erupt / Ion Charge / Explode / Electrocute / Tempest (+ 3 alignment reskins each)
--   poet  heal -- Vital Spark / Anoint / Remedy / Heaven's Kiss (+ 3 each)
-- Classified in C# (Content.AreaSpellFor, which also carries the real per-family mana the formula export
-- couldn't see), so ctx.amount is the spell's own evaluated damage/heal and ctx.mana its cost.
--
-- The ORDER here is RTK's and it matters: the mana goes first, the sweep second, and the "You cast X." line
-- (printed by HandleCast on a true return) last and unconditionally. Casting into an empty room is a real
-- cast that costs full price -- which is the behaviour being asked for, and the opposite of the single-target
-- path's "finds no target" refusal that these were wrongly falling into.
-- =========================================================================================================

function verbs.area_zap(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:areaZap(ctx.amount)          -- 0 targets is fine; each victim gets its own animation + HP bar
  return true
end

-- The dog (subpath) 5-way fire: Fissure (lvl 70, 120 mana) and Lava Surge (lvl 99, 210 mana).
-- Head Tutor Nussan's board entry is the spec: "Ranged, targetable 5 way attack. Cast on yourself or
-- monsters. Misses sometimes if you're too far away. When cast on the target, anything on the 4 sides gets
-- hit as well, full damage. Can be cast extremely fast."
-- Same order as the 4-way ladder -- mana first, sweep second, "You cast X." last and unconditional -- so a
-- cast that range-misses still costs full price. "Extremely fast" is honoured in C# by these two carrying no
-- cast delay (Content.CastDelayMs), which leaves them on the ordinary 3/sec action budget while every other
-- zap is held to 1/sec.
function verbs.target_area_zap(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:targetAreaZap(ctx.amount)    -- 0 hits is fine: an empty room, or a miss at range
  return true
end

function verbs.area_heal(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:areaHeal(ctx.amount)         -- players on the four adjacent tiles; never you, never pets (RTK BL_PC)
  return true
end

-- Sage ladder (RTK Spells/common/sage.lua: share_wisdom -> mentors -> apprentices -> adepts -> sages): the
-- game's only world-wide chat channel. You type a line at the spell's ">" prompt and every player on the
-- server sees "[<your name>]: <line>". The five tiers differ ONLY in what they charge and how long they make
-- you wait (row.mana / row.duration), so one verb covers all of them.
-- Casting with nothing typed is a no-op that costs nothing -- RTK guards the same way, and the client shows
-- its own prompt, so there is nothing useful to say about it.
-- The Sage ladder's world channel. Bought a rung at a time from npcs.SageNpc (npc_dialog.lua), which owns
-- the price, the wait and the replacement rule; this owns what the spell actually DOES.
--
-- REACH IS THE WHOLE PRODUCT. Only the top rung sages everywhere. Below it the spell works in the "4.0
-- designated areas" (Mythic, Wilderness, KaMing's Encampment, carnage and events -- ctx:sageReach() == "sage"),
-- rungs 3-4 add the caster's own kingdom ("home"), and ANYWHERE ELSE THE CAST DOES NOT FAIL: it becomes the
-- Mentor spell. That is not an inference. The Dream Weaver that introduced the rule says so outright --
-- "In the towns, your Sage spells will no longer broadcast your thoughts to the Kingdom. However, you will
-- now be able to use these spells to allow your wisdom to flow to those new to the Nexus. Indeed, the spells
-- Champion, Prince Charming, Sage, and Hierophant will operate identically to the spell Mentor when you are
-- in one of the towns of the Nexus." (Eldridge, "Sage in towns") -- and tswolf repeats it per spell, "Has
-- same effect as ""Mentor"" when casted in a NON Sage Area".
--
-- THE AETHER BURNS EITHER WAY, deliberately: a player asked for exactly that to be changed and was refused --
-- "block sage and mentor spell usage in non-saging areas instead of casting Aethers (Won't be changed. Was
-- written to be like that)". So the mana and the cooldown are charged on the Mentor branch too. Do not
-- "fix" this into a free cast; it is the documented behaviour and it is why rung 5 is worth 500,000 gold.
--
-- The client prompts for the wisdom text before the server ever sees the cast (Spells.csv SplQuestion ">"),
-- so on the Mentor branch the player types their line and THEN gets Mentor's own "Who would you like to
-- mentor?" prompt. Slightly odd, and faithful: the typed text is simply not used.
--
-- See docs/common/Deferred-Work.md and Sources.csv `atlas-2002-12-25-sage`. Still not modelled: rungs 3-4
-- are really four per-nation spells each, and carnage/event areas halve the aether.
local SAGE_RUNGS = {
  share_wisdom       = 1,
  mentors_wisdom     = 2,
  apprentices_wisdom = 3,
  adepts_wisdom      = 4,
  sages_wisdom       = 5,
}

-- Does this rung reach the kingdom from where the caster stands? Rung 5 always; rungs 3-4 from a sage area
-- or their own kingdom; rungs 1-2 from a sage area only. An unknown key reads as rung 1, the strictest.
local function sage_reaches(ctx)
  local rung = SAGE_RUNGS[ctx.spellKey] or 1
  if rung >= 5 then return true end
  local where = ctx:sageReach()
  return where == "sage" or (rung >= 3 and where == "home")
end

function verbs.sage_shout(ctx, row)
  local mana = row.mana or 600
  local cd   = row.duration or 900000
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if not ctx:enoughMana(mana) then return false end

  if sage_reaches(ctx) then
    if not ctx:worldShout(ctx.answer) then return false end   -- empty text: nothing said, nothing charged
    ctx:narrated()               -- the shout IS the output; "You cast Share Wisdom." on top reads as noise
  else
    ctx:mentor()                 -- out of reach: the spell IS Mentor here, and still costs the full aether
  end

  ctx:debitMana(mana)
  ctx:setCooldown(ctx.spellKey, cd)
  return true
end

-- Endear / mind control (RTK poet/endear.lua + possess_soul/charm_life/align_follower, and NPCs/endear.lua
-- that the Charm weapons proc): the faced mob becomes YOURS for row.duration, counting against your pet cap.
-- When it lapses the creature reverts to a normal world mob rather than despawning (RTK's uncast just clears
-- owner/target). Bosses and already-owned mobs refuse. row.duration is the control time, row.base the cooldown
-- (RTK setAether 6000) so you can't perma-chain it.
--
-- The control time is a ROLL, not a flat timer: uniform in [row.duration, row.durationMax] ms. All three charm
-- spells share this verb and differ only by their row -- Endear 15-20s, Fascinate 30-35s, Enthrall (the Charm
-- weapon proc, key `endear`) 40-45s. These bands are PROVISIONAL tuning pending live measurement, not archive
-- values: the archive only pins Endear's floor (RTK's flat 15000) and NexusAtlas 4.x's "15-30 Seconds" range.
-- A row with no durationMax (or one not above duration) keeps the old flat behaviour.
local function charm_duration(ctx, row)
  local lo = tonumber(row.duration) or 15000
  local hi = tonumber(row.durationMax) or lo
  if hi <= lo then return lo end
  return ctx:rollRange(lo, hi)
end

function verbs.endear(ctx, row)
  local mana = row.mana or 300
  if ctx:onCooldown(ctx.spellKey) then ctx:say("Your will is too weak."); return false end
  if not ctx:enoughMana(mana) then ctx:say("Your will is too weak."); return false end
  if not ctx:charmTarget(charm_duration(ctx, row)) then return false end
  ctx:debitMana(mana)
  if (row.base or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.base) end
  return true
end

-- NO cotw_controller verb here on purpose. RTK's cotw_controller_poet is a threat-redirect + dismiss-all
-- toggle, and BOTH halves are later-server behaviour: its threat side rides AI/threat.lua (an aggro table
-- whose callers are Druid/Monk/Spy subpath spells, GM spells and a TESTING/ file), and 4.95 Call of the Wild
-- creatures only ever leave play by being KILLED or by their own timer -- there is no dismiss. RTK itself
-- ships the spell disabled (the only cotw row with SplActive=0). Don't re-add either half.

-- Kamikaze (RTK Spells/NPCs/kamikaze.lua): detonate yourself. The blast is ceil(your CURRENT hp * 1.75), so it
-- hits hardest at full health, and you are left on exactly 10 hp whether or not it killed anything. RTK shouts
-- "Kamikaze~!" over your head first. Deliberately NOT gated on hp — dropping to 10 is the cost, not a failure.
function verbs.kamikaze(ctx, row)
  local mana = row.mana or 120
  if not ctx:enoughMana(mana) then ctx:say("You do not have enough mana.") return false end
  if not ctx.hasTarget then return false end          -- silent, free: no creature to strike (an NPC is none)
  ctx:debitMana(mana)
  ctx:talk("Kamikaze~!")
  ctx:damage(math.ceil(ctx.hp * (row.coeff or 1.75)))
  ctx:setHp(row.amount or 10)
  return true
end

-- =========================================================================================================
-- TIER-3 UTILITY / TARGET verbs (mana transfer, cleanse, revive, leap, mana battery). Classified in C#
-- (Content.Is*Spell), so they run against an EMPTY row — RTK constants are the `row.x or <default>` fallbacks
-- (add a SpellParams row later to tune). The target.* primitives all act on the PC resolved by ctx:pcTarget(),
-- which takes an aimed id only to a player on the caster's own map (Session.ResolvePcCastTarget).
-- =========================================================================================================

-- Mana Steal (RTK poet inspiration): drain a GROUP member's entire mana into your own pool (capped at your max).
-- No cast cost — the "cost" is taking their mana. Target must be in your party and not a ghost.
-- Never yourself (owner, 2026-10-11: "Refuse self"). You are in your own group whenever you have one, so the group
-- test alone let a cast at your own id through: setMana(mp + targetMana) then setTargetMana(0) on the same player
-- left you at 0 with "You cast X." (#341's review, probe P6). Mana Gift's refusal, word for word and at the same
-- point: "It doesn't work.", nothing spent (RTK inspire.lua:23-26). RTK's inspiration.lua has no self test.
function verbs.mana_steal(ctx, row)
  if not ctx:pcTarget() then return false end
  if ctx.targetIsSelf then ctx:say("It doesn't work."); return false end
  if ctx.targetIsDead then ctx:say("That cannot save them now."); return false end
  if not ctx.targetInGroup then ctx:say("They must be in your group."); return false end
  -- One step inside the target's own monitor takes all they hold and sets them to 0 (Session.ReceiveTakeAllMana);
  -- the caster gets what it took. A read here and a set to 0 after it wiped a regen beat that landed between the
  -- two, and credited the caster with mana the target lost there (#345, follow-up 4).
  local taken = ctx:takeTargetMana()
  ctx:setMana(ctx.mp + taken)              -- setMana clamps to the caster's max
  ctx:tellTarget()
  ctx:fx(6, 22)
  return true
end

-- Mana Gift (RTK poet inspire): top off ANOTHER player's mana from your own (drains you). Needs >= 30 mana to
-- attempt; gives up to their missing mana, capped by whatever you actually have.
function verbs.mana_gift(ctx, row)
  local cost = row.mana or 30
  if not ctx:pcTarget() then return false end
  if ctx.targetIsSelf then ctx:say("It doesn't work."); return false end
  if ctx.targetIsDead then ctx:say("That cannot save them now."); return false end
  if ctx.mp < cost then ctx:say("Not enough mana."); return false end
  local give = math.min(ctx.mp, ctx.targetMaxMana - ctx.targetMana)
  ctx:setMana(ctx.mp - give)
  -- An add, not a set: the target's new mana is decided inside the target's own monitor, so a regen beat that
  -- lands after the read above is kept rather than overwritten (PR #341's review, probe P4).
  ctx:addTargetMana(give)
  ctx:tellTarget()
  ctx:fx(6, 22)
  return true
end

-- Cleanse (RTK poet dispell): chance-based FULL buff/debuff wipe on a targeted player (self-castable). Success =
-- (120 + clamp(targetAC,-60,70) - floor((targetWill-casterWill)/10)) / 2, floored at 10%. 200 mana, no cooldown.
-- It keeps three rules the project owner set on 2026-10-10 for a cast at another player, on the source survey
-- (briefs/reports/dispell-sources-survey-2026-10-10.md in the coordination workspace). On 2026-10-11 he made the
-- second and third hold on yourself too ("Same rules on self"): RTK's dispell.lua and its three twins treat
-- yourself as any target, but for the target's own line (dispell.lua:53-55).
--   * Staff are exempt. A cast at a GM fizzles: "Fizzle.", nothing spent, nothing cleared. The staff test is
--     Approach and Summon's (ctx.targetIsGm, the GM roster), but a GM caster is refused too: the exemption is about
--     the target. @ban and @mute refuse a GM target whoever asks; Approach and Summon's GM pass is RTK's rule for
--     moving staff (approach.lua, summon.lua), and RTK's dispell.lua has no staff rule at all. Another player
--     only: a GM's Dispell on themselves still lands.
--   * A won roll also ends the two holds that outlive their slot entries: a Doze's sleep (with the harder next hit
--     it armed) and a venom. tswolf and the Atlas say Dispell removes all spells on the target, and RTK's
--     flushDuration ends both. flushTarget(true) does it in the same section of the target's monitor; on yourself
--     that is the monitor the cast already holds.
--   * A lost roll is RTK's (dispell.lua:37-40 and its three twins): "Something went wrong." and nothing else, no
--     cast pose and no mana. So the roll comes before the spend, and the false return keeps HandleCast's "You cast
--     X." and pose back. No era source says a Dispell could fail at all; RTK is the only one for the line and cost.
-- Only the target's own line, "<caster> casts X on you.", is for another player.
function verbs.cleanse(ctx, row)
  local cost = row.mana or 200
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  if not ctx:pcTarget() then return false end
  local other = not ctx.targetIsSelf
  if other and ctx.targetIsGm then ctx:say("Fizzle."); return false end
  local armor = math.max(-60, math.min(70, ctx.targetArmor))
  local prot  = math.floor((ctx.targetWill - ctx.will) / 10)
  local rate  = math.max(10, math.ceil((120 + armor - prot) / 2))
  local won   = ctx:roll(rate)
  if not won then ctx:say("Something went wrong."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:flushTarget(true)
  if other then ctx:tellTarget() end
  ctx:fx(6, 34)
  return true
end

-- Revive (RTK poet resurrect): bring a dead/ghost player back to full health in place. 3000 mana, 8s cooldown.
function verbs.revive(ctx, row)
  local cost = row.mana or 3000
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < cost then ctx:say("Your will is too weak."); return false end
  if not ctx:pcTarget() then return false end
  if not ctx.targetIsDead then ctx:say(ctx.spellName .. " has no effect on the living."); return true end
  ctx:setMana(ctx.mp - cost)
  ctx:setCooldown(ctx.spellKey, 8000)
  ctx:reviveTarget()
  ctx:fx(6, 20)
  return true
end

-- Leap (RTK rogue race): jump up to 3 tiles in the faced direction, stopping at the last passable tile. 1 mana,
-- 80s cooldown. ctx:leap does the collision walk + the actual move, returning tiles moved (0 = nowhere to go).
function verbs.leap(ctx, row)
  local cost = row.mana or 1
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  if ctx:leap(row.amount or 3) == 0 then ctx:say("There's nowhere to go."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:setCooldown(ctx.spellKey, 80000)
  return true
end

-- Mana Battery (RTK invoke): trade HP for a full mana refill — costs 40% of max mana as HP (floored at 100 HP),
-- refills mana to full. Needs >= 30 mana to invoke; 22s cooldown.
-- Harden Body (RTK poet/harden_body.lua + its Death's Guard / Life's Protection / Body of Alignment reskins):
-- "Grants temporary immortality." While it runs, Session.DamageImmune makes every blow simply not land —
-- RTK's Player.removeHealthExtend returns before it computes anything. That is not a stat delta, so the
-- generic arch_buff had no BuffStat/BuffAmt to apply and these four silently did nothing but cost mana.
-- Order is RTK's and it matters: the mana goes FIRST, then the roll, so a failure still costs you the cast.
-- Success scales with armour (better armour is more negative, so -armor/3 rewards it), and the Scroll of
-- Immortality grants the same ward through item_verbs.lua's `hardenbody`.
function verbs.harden_body(ctx, row)
  if ctx.immune then ctx:say("You already cast that spell."); return false end
  if not ctx:spendMana(row.mana or 300) then return false end
  ctx:castPose()
  if not ctx:roll(50 + math.floor(-ctx.armor / 3)) then ctx:say("Something went wrong."); return false end
  ctx:setDuration(ctx.spellKey, row.duration or 12000)
  ctx:fxSelf()
  ctx:say("You cast " .. ctx.spellName .. ".")
  return true
end

function verbs.mana_battery(ctx, row)
  local minMana = row.mana or 30
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < minMana then ctx:say("Not enough mana."); return false end
  ctx:setHp(math.max(100, ctx.hp - math.floor(ctx.maxMp * 0.4)))
  ctx:setMana(ctx.maxMp)
  ctx:setCooldown(ctx.spellKey, 22000)
  ctx:fxSelf()
  return true
end

-- =========================================================================================================
-- CATEGORIZED STATUS layer (curses + wards). RTK's checkIfCast() cross-guards live in spellTables.lua: a cast is
-- refused if the target already carries a status in its category's BLOCK list. These relationships are RTK
-- CONSTANTS (not per-spell data), so they live here keyed by the casting status's category:
--   curses/minorcurses : own family + protections  (a protection makes you immune to curses)
--   disheartens        : own + bolsters + protections   (bolster blocks dishearten and vice-versa)
--   bolsters           : own + disheartens
--   hardarmors         : own only (independent of bolster — you CAN stack harden + bolster)
--   protections        : NONE — RTK hoche/immunity/... have no checkIfCast guard, so re-cast just refreshes
-- minorcurses collapses into curses inside ctx:hasStatus (see Session.CatFamily), so listing "curses" catches it.
local BLOCKS = {
  curses      = { "curses", "protections" },
  minorcurses = { "curses", "protections" },
  disheartens = { "disheartens", "bolsters", "protections" },
  bolsters    = { "bolsters", "disheartens" },
  hardarmors  = { "hardarmors" },
  protections = {},
}
-- Return the FIRST blocking category present on the target (via `hasFn`), or nil. Unknown categories default to
-- own-category exclusivity. `hasFn` is the caller's status check (curses check the curse target; wards their own).
local function blockedBy(hasFn, category)
  local list = BLOCKS[category]
  if list == nil then list = { category } end
  for _, cat in ipairs(list) do
    if hasFn(cat) then return cat end
  end
  return nil
end
-- (blockMsg lives up by arch_buff, its first caller -- a local declared here would be invisible there.)

-- Curse (RTK Spells/*/pestilence.lua & kin): apply a MUTUALLY-EXCLUSIVE categorized status to a curse target.
-- Blocked if the target already carries a status in this category's BLOCK list (checkIfCast) — which is what
-- makes self-pestilence a real defense (occupy your own 'curses' slot with a mild curse; row.amount is the stat
-- effect, e.g. armor +5 -> raises effective AC -> take MORE damage) AND what makes a protection curse-immune.
-- Row: mana, category (curses/disheartens/...), stat, amount, duration. Removed later by a Cure of that category.
-- canCurse, hasStatus, alreadyCast and applyCurse all read the cast's one target, resolved at canCurse (#343's
-- review, F1), so the slot checked is the slot written; a target that has left since is nobody.
-- Take-hold chance per curse category. Like HOLD_CHANCE above these are OURS: RTK's curse scripts have no
-- failure roll at all, so a curse always stuck. A per-spell `chance` column in SpellParams.csv overrides.
-- Note what this interacts with: because a landed curse OCCUPIES its category slot and blocks every other
-- curse until it lapses, a miss is the only way a second attempt is even possible -- so the rate is really
-- "how often does the first cast get to decide the slot", not a damage-style dps knob.
local CURSE_CHANCE = { curses = 75, minorcurses = 85, disheartens = 80 }

function verbs.curse(ctx, row)
  local mana = row.mana or 0
  if not ctx:enoughMana(mana) then return false end
  if not ctx:canCurse() then return false end                  -- PvP-legal PC (incl self) or a creature; not an NPC
  local by = blockedBy(function(c) return ctx:hasStatus(c) end, row.category)
  if by then ctx:say(blockMsg(by, ctx:alreadyCast(by))); return false end
  local ch = row.chance or CURSE_CHANCE[row.category] or 100
  if ch < 100 and not ctx:roll(ch) then ctx:debitMana(mana); ctx:say("Something went wrong."); return true end
  ctx:debitMana(mana)
  ctx:applyCurse(row.category, row.stat or "", math.floor(tonumber(row.amount) or 0), row.duration or 200000)
  return true
end

-- Ward (RTK bolster/harden_armor/hoche & kin): the BENEFICIAL twin of curse — a mutually-exclusive categorized
-- status cast on yourself or an ally (never PvP-gated; a bolster raises AC via positive `amount` in our inverted
-- convention). Protections (hoche family) carry NO stat (amount 0) — they exist only to occupy the 'protections'
-- slot so curses bounce off. Same _buffs storage + category exclusivity as curse, so a warded ally shows up when
-- an enemy's curse checks hasStatus. category -> self vs ally target and any cooldown are RTK constants (below).
local WARD_SELF     = { protections = true }        -- hoche/immunity/... are self-cast (no target arg)
local WARD_COOLDOWN = { protections = 180000 }      -- RTK setAether("hoche_warrior", 180000)
function verbs.ward(ctx, row)
  local mana = row.mana or 0
  local cd = WARD_COOLDOWN[row.category]
  if cd and ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if not ctx:enoughMana(mana) then return false end
  if not ctx:wardTarget(WARD_SELF[row.category] and "self" or "ally") then return false end
  local by = blockedBy(function(c) return ctx:wardHasStatus(c) end, row.category)
  if by then ctx:say(blockMsg(by, ctx:wardAlreadyCast())); return false end
  ctx:debitMana(mana)
  ctx:applyWard(row.category, row.stat or "", math.floor(tonumber(row.amount) or 0), row.duration or 60000)
  if cd then ctx:setCooldown(ctx.spellKey, cd) end
  return true
end

-- =========================================================================================================
-- TIER 4 — WORLD-EFFECTING layer. These spells reach outside the caster/target stat model to touch the map,
-- world entities, or persistent state, so each leans on a `world`/`tile` facade primitive (ctx:gateway,
-- ctx:summonPet, ctx:placeTrap, ctx:warp-home, ...) whose heavy engine op stays in C#. The VERB owns the
-- guards, mana/cooldown, and player-facing messages (the moddable/hot-reloadable part); the primitive does
-- the irreducible engine work. These spells are classified in C# (no SpellParams row), so they run against an
-- empty row — data-bound constants (per-kind trap mana, pet caps, gate boxes) come from the primitives via
-- ctx.spellMana / ctx.petMana / etc., not row.*. Each still falls back to its C# CastX handler if unloaded.
-- =========================================================================================================

-- Gateway (RTK common/gateway.lua): teleport to a random tile in the answered N/E/S/W gate box of the caster's
-- kingdom city. No mana (RTK only state-checks). ctx:gateway does the region/gate lookup, landing + narration.
function verbs.gateway(ctx, row)
  if ctx.isDead then ctx:say("Spirits cannot use Gateway."); return false end
  if not ctx.canWarpOut then ctx:say("It doesn't work here."); return false end
  return ctx:gateway()                                   -- reads ctx.answer; self-narrates its own arrival line
end

-- Return (RTK common/return.lua): warp home to a random tavern in your nation. 30 mana; blocked on warp-locked maps.
function verbs.return_home(ctx, row)
  local cost = row.mana or 30
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  if not ctx.canWarpOut then ctx:say("That does not work here."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:returnHome()
  return true
end

-- Approach and Summon (#313; RTK common/approach.lua + summon.lua): the Mage/Rogue/Poet travel spells. The
-- player TYPES a name into the prompt ("Approach who?>" / "Summon who?>", ctx.answer). Approach takes you to that
-- person; Summon brings them to you. Two era sources agree on what they did and what they asked:
--   tswolf.com/spells/{mage,poet,rogue}.shtml (Wayback 2001-02-23 to 2001-08-21): "Approach a Person" / "Brings
--     Someone to You", 30 Mana; "Person Must be In Group and Same Server. Must also be From Same Kingdom as
--     Person."
--   nexusatlas.com/spells/{mage,poet,rogue}.php (Wayback 2002-12-30): "Brings you to a person" / "Brings a
--     person to you", Mana Cost 30, Aethers 0, Target "Type in Name"; the person must be "in an area that allows
--     approaching, citizenship in the same kingdom, and be in your group ... or else a "Fizzle" message is seen."
-- The kingdom rule here is "the same nation", two Neutrals (0) included. Read literally, the lines above leave two
-- Neutrals out; the project owner chose to allow them (#325, 2026-10-06). His ruling, not a sourced fact (same_group_and_kingdom).
-- RTK agrees on the group, the 30 mana and "Fizzle.", and adds the map checks below. Its ORDER is kept: it decides
-- which line a player reads when more than one applies ("That does not work here." comes before the later fizzles).
-- The cost, animation and sound are the spell_effects.csv rows (ctx.spellMana, ctx:fxSelf()).
-- The "area that allows approaching" is Maps.csv MapCanSummon (RTK map[m].summon, which its scripts read as
-- canSummon), through ctx:mapCanSummon: 0 is "Fizzle." at no cost. Which map counts is the project owner's ruling
-- (2026-10-06, the follow-up to #325):
--   * Approach reads the person's map, where the caster would land: RTK approach.lua checks target.canSummon,
--     and the Atlas line asks that the PERSON be in such an area.
--   * Summon reads both maps: the caster's, where the person would land (RTK summon.lua checks
--     player.canSummon), and the person's own (the Atlas line).
-- All three sit where RTK checks the flag: after the self check, before the indoor check, and so before the
-- warp-out check too. That decides the line: #325's "That does not work here." becomes "Fizzle." in three groups
-- of casts (PR #336 review, F1):
--   * Summon of a person on a map that is both no-summon and warp-locked: Guol (1111-1116), the newbie tutorial
--     fields (4711-4718), the event halls and arenas 3001-3010, the Mythic zodiac rooms (3020-3031), Judge's
--     Chambers (3051) and the halls 3683-3694. Only this group rests on where the person's-map check sits;
--     placed after the warp-out check, it would keep #325's line.
--   * Summon cast from an outdoor no-summon area, of a person on a warp-locked map. RTK's own order: summon.lua
--     checks player.canSummon (L42) before the person's warpOut (L74).
--   * Approach cast from a warp-locked map, to a person in an outdoor no-summon area. RTK's own order:
--     approach.lua checks target.canSummon (L37) before the caster's warpOut (L65).
-- A map with no Maps.csv row, or a blank cell, allows.
-- Not checked, and why:
--   * "Same Server": this is one process, so everyone is on it.
--   * RTK's hardcoded map ids (jail 666, 1228, 3010/3011/33, 3042, 3034-3039, 4259, the two Kan Shops, the 59000+
--     instances): each is a map the 4.95 client cannot render, or an indoor map the indoor check already refuses.
--   * the PK-status and PK-grudge halves of RTK's canPK: not modelled. The PvP-map half is checked.
--   * Approach's dead-caster check: HandleCast refuses a ghost's cast before any verb runs.
-- The mover lands on @approach's and @bring's tile: the first free one north, east, south or west of the other
-- person, else that person's own tile. RTK searches in facing order instead; the project owner kept this one at #325's merge
-- (2026-10-06).
-- Neither verb tells the moved person anything: RTK's scripts send them no line. One exception, Summon's: a person
-- summoned mid-exchange has the exchange cancelled, and both traders see the client's "Exchange cancelled." box,
-- as for any move (#57). The project owner kept this at #325's merge (2026-10-06), rather than have Summon fizzle on a trader.

local function travel_fizzle(ctx) ctx:say("Fizzle."); return false end

-- The two people's own standing, checked last, where RTK checks the group. The kingdom half is "the same
-- nation", Neutral (0) included. Read literally, "citizenship in the same kingdom" excludes two Neutrals, since a
-- Neutral holds no citizenship; the project owner chose to allow them (2026-10-06). A Neutral and a citizen of a kingdom, or
-- citizens of two kingdoms, still fizzle.
local function same_group_and_kingdom(ctx)
  if not ctx.targetInGroup then return false end
  return ctx.nation == ctx.targetNation
end

function verbs.approach(ctx, row)
  local mana = row.mana or ctx.spellMana
  if not ctx:enoughMana(mana) then return false end                -- "You do not have enough mana."
  if not ctx:pcTargetNamed(ctx.answer) then return travel_fizzle(ctx) end
  if ctx.targetIsSelf then return travel_fizzle(ctx) end
  -- Where the caster would land: the person's map, its size and their tile, read once and together inside their
  -- monitor, and kept for the move. Every check below is about this map, and approachTarget moves the caster to this
  -- place, not to wherever the person stands by then (Session.LuaReadApproachPlace; #345, follow-up 2).
  local there = ctx:readTargetPlace()
  if not ctx:mapCanSummon(there) then return travel_fizzle(ctx) end  -- the person's area (RTK target.canSummon)
  if ctx:mapIndoor(there) then return travel_fizzle(ctx) end
  if not ctx:mapAdmits(there, "caster") then return travel_fizzle(ctx) end
  if not ctx.canWarpOut then ctx:say("That does not work here."); return false end
  if ctx.targetIsGm and not ctx.isGm then return travel_fizzle(ctx) end
  if ctx:mapPvp(there) or ctx:mapPvp(ctx.map) then return travel_fizzle(ctx) end
  if not same_group_and_kingdom(ctx) then return travel_fizzle(ctx) end
  ctx:debitMana(mana)
  ctx:fxSelf()
  ctx:approachTarget()
  return true
end

function verbs.summon(ctx, row)
  local mana = row.mana or ctx.spellMana
  if not ctx:enoughMana(mana) then return false end                -- "You do not have enough mana."
  if not ctx:pcTargetNamed(ctx.answer) then return travel_fizzle(ctx) end
  if ctx.targetIsSelf then return travel_fizzle(ctx) end
  local here = ctx.map                                             -- where the target would land
  if not ctx:mapCanSummon(here) then return travel_fizzle(ctx) end           -- RTK player.canSummon
  if not ctx:mapCanSummon(ctx.targetMap) then return travel_fizzle(ctx) end  -- the person's area (Atlas)
  if ctx:mapIndoor(here) then return travel_fizzle(ctx) end
  if ctx.targetIsDead then return travel_fizzle(ctx) end
  if not ctx:mapAdmits(here, "target") then return travel_fizzle(ctx) end
  if not ctx:mapWarpOut(ctx.targetMap) then ctx:say("That does not work here."); return false end
  if ctx.targetIsGm and not ctx.isGm then return travel_fizzle(ctx) end
  if ctx:mapPvp(ctx.targetMap) or ctx:mapPvp(here) then return travel_fizzle(ctx) end
  if not same_group_and_kingdom(ctx) then return travel_fizzle(ctx) end
  -- The move first, then the cost: unlike every check above, it can still be refused, by a target who logged out
  -- since the lookup or who keeps opening exchanges (Session.LuaSummonTarget). A refused cast costs nothing, so
  -- the mana and the effect wait until somebody has actually moved (PR #325 review, F3). RTK debits before its
  -- warp, but RTK has no logout to race.
  if not ctx:summonTarget() then return travel_fizzle(ctx) end
  ctx:debitMana(mana)
  ctx:fxSelf()
  return true
end

-- Divination (RTK rogue/judge.lua + spy.lua): inspect a lower-level player's class/name/level/stats (spy also
-- lists their inventory). 30 mana. Judge needs a STRICTLY lower target; spy allows equal level too.
function verbs.divine(ctx, row)
  local cost = row.mana or 30
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  if not ctx:pcTarget() then return false end
  local ok
  if ctx.spyMode then ok = ctx.targetLevel <= ctx.level else ok = ctx.targetLevel < ctx.level end
  if not ok then ctx:say("Target player must be lower level than you for you to use this spell."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:divine(ctx.spyMode)                                -- builds + sends the inspect popup (self-narrates)
  return true
end

-- Spot Traps (RTK warrior/watchful_eye.lua + dog/spot_traps.lua): reveal every hidden trap within 15 tiles as a
-- caster-only marker sprite. The sense-result line IS the caster narration.
function verbs.spot_traps(ctx, row)
  local cost = row.mana or ctx.spellMana
  local cd = row.duration or (ctx.spellAether > 0 and ctx.spellAether or 25000)
  if ctx.spellAether > 0 and ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:setCooldown(ctx.spellKey, cd)
  ctx:fxSelf()
  local n = ctx:revealTraps()
  if n > 0 then ctx:say("You sense " .. n .. " hidden trap" .. (n == 1 and "" or "s") .. " nearby.")
  else ctx:say("You sense nothing nearby.") end
  ctx:narrated()
  return true
end

-- Filch (RTK rogue/filch.lua family): grab the item on the faced tile into your pack (coins -> purse). The bark
-- + fx play unconditionally, before looking at the floor — same as RTK.
function verbs.filch(ctx, row)
  local cost = row.mana or ctx.spellMana
  if ctx.mp < cost then ctx:say("Your will is too weak."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:fxSelf()
  ctx:talk("I'll take that")          -- RTK player:talk(2, ...) -- an over-head bubble, not a status line
  ctx:filch()
  return true
end

-- Drain (RTK rogue/drain.lua + drink_of_souls/parasite/absorb): finish off a WEAK creature and take whatever
-- life it had left. Only works on a mob at or under row.amount HP (RTK 1000) -- "drains all animals/summons
-- less than 1000 vita and gives you their remaining life". Nothing scales off the caster, so a level-99 mage
-- and a level-80 one drain identically; the yield is entirely the victim's remaining HP.
function verbs.drain(ctx, row)
  local mana = row.mana or 60
  if not ctx:enoughMana(mana) then ctx:say("Your will is too weak."); return false end
  -- targetHp, fxTarget and damage all read ONE victim, resolved here once (SpellContext.Victim), so the life
  -- you take and the creature you kill are the same one. No creature you could strike reads 0: an id that names
  -- none, a player, or an NPC (which no blow can hurt, so it is no victim).
  local hp = ctx.targetHp
  if hp <= 0 or hp > (row.amount or 1000) then ctx:say("It doesn't work."); return false end
  ctx:debitMana(mana)
  ctx:fxTarget()                      -- the spell's own anim over the VICTIM (unaligned 1 / aligned 84)
  ctx:heal(hp)                        -- their remaining life becomes yours...
  ctx:damage(hp)                      -- ...and it kills them (normal death/loot/exp path)
  return true
end

-- Set Trap (RTK rogue/set_X_trap.lua + set_trap.lua dispatcher): place a hidden hazard on your own tile. Which
-- kind/level/mana is data-bound (per trap), so the primitive resolves it from the spell (or the typed answer for
-- the dispatcher) and owns the mana debit; the verb is just the entry point + fallback boundary.
function verbs.set_trap(ctx, row)
  return ctx:placeTrap()                                 -- resolves kind+mana, checks level/mana, places + fx
end

-- Bladestorm Trap (RTK rogue/bladestorm_trap.lua): place a visible decoy that detonates a facing-cone AoE when
-- anything steps on it. 1520 mana, 125s cooldown, 21s lifetime (RTK constants; tunable via a row later).
function verbs.bladestorm(ctx, row)
  local cost = row.mana or 1520
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:setCooldown(ctx.spellKey, row.duration or 125000)
  ctx:placeBladestorm(row.amount or 21000)
  ctx:fxSelf()
  return true
end

-- Pet Summon (RTK Poet "Call of the Wild"): spawn a real owned world mob one tile ahead (or on your tile if
-- blocked), expiring in 300s. Mana/cooldown/mob are data-bound (per pet spell) so come via ctx.pet*.
function verbs.pet_summon(ctx, row)
  if ctx.petCooldown > 0 and ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.petMana > 0 and ctx.mp < ctx.petMana then ctx:say("Not enough mana."); return false end
  if ctx.petCount >= ctx.petCap then ctx:say("You cannot summon any more creatures right now."); return false end
  if not ctx:summonPet() then ctx:say("Something went wrong."); return false end
  if ctx.petMana > 0 then ctx:setMana(ctx.mp - ctx.petMana) end
  if ctx.petCooldown > 0 then ctx:setCooldown(ctx.spellKey, ctx.petCooldown) end
  return true
end

-- Morph (RTK disguise family): reskin the caster to an animal look for a duration, visible to self + peers. The
-- look/female-look/mana/duration are resolved in C# (answer-dispatched forms) and staged before the verb runs;
-- ctx.morph* read that staged plan.
function verbs.morph(ctx, row)
  if ctx:morphActive() then ctx:say("You already cast that spell."); return false end
  if ctx.mp < ctx.morphMana then ctx:say("You do not have enough mana."); return false end
  ctx:setMana(ctx.mp - ctx.morphMana)
  ctx:applyMorph()
  return true
end

-- Propose (RTK common/propose.lua): a marriage proposal — kicks off a scripted async dialog (ask beloved's name,
-- then prompt them). No mana; the real outcome resolves asynchronously. Blocked if already engaged/married.
-- A 20-minute wait from the cast, which RTK's script intends and this port enforces (owner, 2026-10-21, and again
-- for #358: "Keep the saved 20-minute limit as built"). propose.lua:11-12 sets setDuration and setAether, both
-- 1200000, at the cast, before the name is even asked. RTK never applies it: line 12 is a dot call
-- (player.setAether), so RTK's binding gets no player and does nothing (sl.c, typel_boundfunc), and its engine's
-- cast refusal reads only aethers (clif.c, "Wait %d second(s) for aethers to settle."), not line 11's duration.
-- Here the spell stays in the book until the beloved says "yes" (Session.PromptProposal), and this wait is what
-- keeps a proposer from asking again at once, or asking two people at the same moment. Checked first, where RTK's
-- engine would check an aether; the line is this file's own for a verb's aether. Set at the cast, so a name never
-- typed, or a refused one, costs the wait, as the script's order has it.
-- Two halves, so the wait survives a relog (PR #358 review, F1): this file's aether lives in the session and a relog
-- clears it; setDuration here is the persisted one (a status flag the row captures and the arrival restores), as
-- Baekho's Cunning and Harden Body set it and the warrior strikes read the Black Potion's. A cast is refused while
-- either is running. A death or @dispel clears the duration (they clear every status flag) and the aether half holds
-- for the rest of that session, so a death and a relog together end the wait early.
local PROPOSE_AETHER = 1200000
function verbs.propose(ctx, row)
  if ctx:onCooldown(ctx.spellKey) or ctx:hasDuration(ctx.spellKey) then
    ctx:say(ctx.spellName .. " isn't ready yet."); return false
  end
  if ctx:hasLegend("engaged") or ctx:hasLegend("married") then
    ctx:forgetSpell()
    ctx:say("You are already committed to someone else!")
    return false
  end
  ctx:setDuration(ctx.spellKey, PROPOSE_AETHER)
  ctx:setCooldown(ctx.spellKey, PROPOSE_AETHER)
  ctx:propose()                                          -- fires RunProposeAsync; the cast anim plays regardless
  return true
end

-- Mentor (RTK common/mentor.lua): offer mentorship to a nearby low-level player, or culminate one already
-- running. Like propose, the whole exchange is a scripted async dialog and the outcome lands later; the
-- level windows and the "already mentored" guards live on the C# side because both parties must be tested.
function verbs.mentor(ctx, row)
  ctx:mentor()
  return true
end

-- =========================================================================================================
-- COMBAT STRAYS — physical facing-tile strikes (not archetype casts). Both hit whatever mob is directly in
-- front of the caster; the engine ops (swing damage, armor-net, overkill splash, teleport) stay C# primitives.
-- =========================================================================================================

-- Sacrifice strikes (RTK rogue/lethal_strike + desperate_attack, warrior/berserk + whirlwind & reskins): trade
-- the caster's OWN pre-cast HP/MP for one oversized facing-tile hit. Damage, mana, cooldown, and post-hit HP
-- cost all differ by family (ctx.sacrificeFamily); Baekho's Rage adds x1.5 to the warrior pair.
--
-- OVERKILL — the damage the killing blow did not need — is spent differently by class, and BOTH mechanisms
-- are ERA-GATED OFF at our 2001-07-09 target date, because both were added to the live game years later:
--   * ctx:overflow  warrior, 2007-04-10 — splashes it onto the tiles around the TARGET (era warrior_overflow)
--   * ctx:backflow  rogue,   2008-09-18 — refunds it to the caster as vita+mana  (era rogue_overkill)
-- Both primitives self-gate in C# (Session.LuaOverflow / LuaBackflow), so calling them unconditionally here
-- is correct: at an era that has them they fire, otherwise they are no-ops and the strike is a plain one-tile
-- hit. Do NOT add an era check here — the gate is deliberately not script-liftable. See Server/Era.cs.
--
-- ORDER: the strike's own HP/MP cost is charged BEFORE the refund. The rogue-board doc's arithmetic only
-- works this way round (LS takes 50% vita then overkill refills 50%; DA zeroes mana then overkill refills to
-- half). Refunding first — which this used to do — halved LS's refund and wiped DA's mana refund entirely.
--
-- NOTE: casting at empty air still spends the mana + arms the cooldown (RTK), but costs no HP (nothing landed).
-- An NPC in front is empty air too (ctx:sacFrontMob answers false for one): no blow can hurt it, and RTK's strikes
-- look for a BL_MOB only. So it costs the mana and aether, the swing and the shout, and strikes nothing: no HP cost,
-- and no overkill to splash or refund at a later era (owner, 2026-10-19).
--
-- FOCUSED BLOW and SIEGE (#328; owner, 2026-10-21, decision sheet item 6). RTK has no script for either, so their
-- numbers are the Nexus Atlas pages': Focused Blow "Takes 2/3 of current Vita in a Strong Attack. The attack does 2
-- times current vitality in damage at 0 AC", Mana Cost 3,000, Aethers 38 Seconds (live rogue page); Siege "leaves the
-- caster with 25% vita left. Damage to target is 1.875 times current vitality plus 0.5 current mana at 0 AC", Mana
-- Cost 8,000, Aethers 43 Seconds (live warrior page, fetched 2026-10-21). Siege's three alignment names run the same
-- branch, and so do Focused Blow's (Burial dance, Flowing energy, Freeform; owner, 2026-10-24, decision sheet item 7).
-- Before this both fell to the `else` arms: 60 mana, a 60 s wait, 0 damage (raised to 1) and no vita cost.
-- Their overkill still takes the ctx:overflow arm below, unchanged. That is off at our date; whether Focused Blow, a
-- rogue strike, refunded or splashed at a later era no source says, so it is left for that era's own decision.
function verbs.sacrifice(ctx, row)
  local fam = ctx.sacrificeFamily
  local mana, aether
  if     fam == "LethalStrike"    then mana, aether = 120, 23000
  elseif fam == "DesperateAttack" then mana, aether = 60, 11000
  elseif fam == "Berserk"         then mana, aether = 60, 12000
  elseif fam == "Whirlwind"       then mana, aether = 120, (ctx.alignment == 0 and 30000 or 25000)
  elseif fam == "FocusedBlow"     then mana, aether = 3000, 38000
  elseif fam == "Siege"           then mana, aether = 8000, 43000
  else                                 mana, aether = 60, 60000 end

  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < mana then ctx:say("You do not have enough mana."); return false end

  local preHp, preMp = ctx.hp, ctx.mp
  -- Chin-Baek-Ho-Ryung: the Black Potion's 10s ward, x1.5 on Berserk and Whirlwind (RTK berserk.lua:45,
  -- whirlwind.lua:38). This used to read ctx.baekhoRage -- Baekho's RAGE, which is baekhos_rage_rogue, a
  -- ROGUE fury, on two warrior-only strikes. The bonus could therefore never fire. Different Baekho.
  local baekho = ctx:hasDuration("chin_baek_ho_ryung")
  local damage
  if     fam == "LethalStrike"    then damage = math.ceil(preHp / 2) + math.ceil(preMp * 2.5)
  elseif fam == "DesperateAttack" then damage = preHp + preMp
  elseif fam == "Berserk"         then damage = math.ceil(preHp * 0.75)
  elseif fam == "Whirlwind"       then damage = math.ceil(preHp * (ctx.alignment >= 2 and 1.525 or 1.75))
  elseif fam == "FocusedBlow"     then damage = preHp * 2
  elseif fam == "Siege"           then damage = math.ceil(preHp * 1.875) + math.ceil(preMp * 0.5)
  else                                 damage = 0 end
  if (fam == "Berserk" or fam == "Whirlwind") and baekho then damage = math.ceil(damage * 1.5) end

  ctx:setMana(ctx.mp - mana)                             -- spent even if nothing is in front (RTK)
  ctx:setCooldown(ctx.spellKey, aether)

  if ctx:sacFrontMob() then
    local overkill = ctx:sacApply(damage)

    -- 1. the strike's own cost. Charged FIRST -- see the ORDER note in the header.
    local newHp
    if     fam == "LethalStrike"    then newHp = math.ceil(ctx.hp / 2)
    elseif fam == "DesperateAttack" then newHp = math.ceil(ctx.hp / 2)
    elseif fam == "Berserk"         then newHp = math.ceil(ctx.hp / 3)
    elseif fam == "Whirlwind"       then newHp = (ctx.alignment >= 2 and math.ceil(ctx.hp * 0.10) or 10)
    elseif fam == "FocusedBlow"     then newHp = math.ceil(ctx.hp / 3)      -- takes 2/3, keeps a third
    elseif fam == "Siege"           then newHp = math.ceil(ctx.hp * 0.25)   -- leaves 25%
    else                                 newHp = ctx.hp end
    ctx:setHp(newHp)
    if fam == "DesperateAttack" then ctx:setMana(0) end

    -- 2. then spend the overkill. Both branches no-op outside their era (see header).
    if overkill > 0 then
      if fam == "LethalStrike" or fam == "DesperateAttack" then ctx:backflow(overkill, preHp, preMp)
      else ctx:overflow(overkill) end
    end
  end   -- nothing in front: the mana and cooldown are still spent (RTK), and nothing is said
  return true
end

-- Ambush (RTK rogue/ambush.lua + reskins): leap to the far side of the faced mob (its back if it faces you,
-- else a flank) and strike. No mana, no cooldown. ctx:ambushLeap does the tile pick + teleport; if the mob's
-- back and both flanks are occupied it can't land ("finds no opening"). The strike gets the free positional
-- backstab when it lands on the blind side (Combat.IsBehindTarget).
function verbs.ambush(ctx, row)
  if not ctx:ambushMob()  then return false end       -- silent
  if not ctx:ambushLeap() then ctx:say(ctx.spellName .. " finds no opening."); return false end
  ctx:ambushStrike()
  return true
end

-- Misc / catch-all (the Utility, Summon, Teleport and Dialog archetypes — 137 of the 640 exported spells).
-- These have no numeric effect the engine can express: RTK's own scripts for them are dialog or flavour, and
-- what the server owes the player is the mana debit plus the central "You cast X." line, which HandleCast
-- prints. Spending the mana in Lua rather than C# is the whole point — it makes the cost of every one of those
-- 137 spells tunable from spell_effects.csv/SpellParams.csv without a rebuild.
function verbs.misc(ctx, row)
  local cost = row.mana or ctx.mana
  if not ctx:spendMana(cost) then return false end       -- spendMana already sent "Not enough mana to cast X."
  return true
end

-- Not castable: a spell a character can learn but whose effect no source describes, so there is nothing true to
-- run. Bound per spell by a SpellParams.csv row (its notes say why). The cast is refused silently and for free: a
-- false return before anything is spent, so ApplyCast charges no mana, arms no aether and summons nothing, and
-- HandleCast prints no "You cast X." line and plays no pose. Today only Wind master (cotw_wind_master_poet): the
-- live Nexus Atlas poet page says "Summons a ??? vita creature", RTK has no script for it and mobs.csv no
-- creature, so it routed to `misc` and charged 10 mana and an 8-minute wait for nothing (owner, 2026-10-21,
-- decision sheet item 3: not castable until a source names its creature). Its three alignment names on that page
-- (Kwi-Sin master, Ming-Ken master, Ohaeng master) are bound here the same way (decision sheet item 7, 2026-10-24).
function verbs.not_castable(ctx, row)
  return false
end

-- Ju Jak's Evocation (RTK subpath guardian spell): refills the mana pool outright and converts what you were
-- ALREADY holding into vitality, capped at a third of your maximum mana. Cast on a full pool it is a big heal;
-- cast on an empty one it is only a refill, which is why the flavour line has two forms.
-- Order matters: the vita is computed from the mana you held BEFORE the refill, or it would always pay the cap.
function verbs.jujak_evocation(ctx, row)
  local preMp = ctx.mp
  local vita  = math.min(preMp, math.floor(ctx.maxMp / 3))
  ctx:setMana(ctx.maxMp)
  if vita > 0 then ctx:setHp(math.min(ctx.maxHp, ctx.hp + vita)) end
  ctx:fxSelf()
  if vita > 0 then ctx:say("Ju Jak's fire restores your magic and " .. vita .. " vitality.")
  else               ctx:say("Ju Jak's fire restores your magic.") end
  ctx:narrated()
  return true
end

-- Hyun Moo's Revival (RTK subpath guardian spell): a SELF revive that leaves you where you fell — unlike
-- Silver Thread or the poet Resurrect family, both of which relocate you to a Shaman. Restores full vita and
-- then leaves you holding everything except the spell's cost, which is why it reads "all mana EXCEPT for
-- 10,000" in the source. Castable alive too, as a full heal; the flavour line is the only difference.
-- ctx:reviveSelf (not ctx:setHp) is what actually drops ghost form — see its doc.
function verbs.hyunmoo_revival(ctx, row)
  local cost = row.mana or 10000
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  local wasDead = ctx:reviveSelf()
  ctx:setMana(math.max(0, ctx.maxMp - cost))
  ctx:fxSelf()
  ctx:say(wasDead and "Hyun Moo returns your life." or "Hyun Moo restores you.")
  ctx:narrated()
  return true
end

-- Mend Equipment (RTK "Luster return"/"Spirit Salvation" reskins): repairs whatever sits in the FIRST pack
-- slot back to full durability. The first-slot rule is RTK's, not a simplification — the spell has no target
-- wire arg, so the slot IS the selection. Every refusal is its own line because the player otherwise has no
-- way to tell "wrong slot" from "already fine".
function verbs.mend_equipment(ctx, row)
  local slot  = row.amount or 0                          -- 0-based; tunable in case a reskin ever reads another slot
  local cost  = row.mana or (ctx.spellMana > 5 and ctx.spellMana or 50000)
  local state = ctx:packSlotState(slot)
  local name  = ctx:packSlotName(slot)

  if state == "empty"    then ctx:say("You have nothing in the first pack slot.");      return false end
  if state == "notgear"  then ctx:say(name .. " cannot be repaired.");                  return false end
  if state == "perfect"  then ctx:say("Your " .. name .. " is already in perfect repair."); return false end
  if not ctx:spendMana(cost) then return false end

  ctx:repairPackSlot(slot)
  ctx:say("Your " .. name .. " is restored to perfect condition.")
  ctx:narrated()
  return true
end

-- Chung Ryong's Rage: the one fury that CLIMBS. Recasting inside its window steps you 1 -> 6, each tier
-- costing more mana, multiplying the swing harder and hardening your armour (`ac` is an AC DELTA, so it is
-- NEGATIVE — more AC means more damage taken); letting it lapse charges a vita price
-- (applied by the engine's regen tick, which is why the TIER is recorded in C# rather than kept here).
--
-- THE WINDOW IS FIXED AND IT IS SET ONCE. RTK's chung_ryongs_rage.lua arms `setDuration(..., 938000)` in its
-- first-cast branch ONLY; every tier-up branch resets the AETHER (`setAether`, the 120s recast gate) and
-- nothing else. So one run is 938s no matter how you climb it: 120s per step means tier 6 lands at t=600s and
-- you hold it for the remaining ~5.6 minutes, then it wears out and takes its vita. Renewing does NOT extend
-- the run — the fury always ends in the drain, which is the whole shape of the spell. That is why the climb
-- passes durMs = 0 below (the engine reads it as "keep the deadline you already have").
--
-- The 938000 could not come from spell_effects.csv: the RTK export only ever captured durations that lived in
-- a TABLE, and this one is a `local duration` inside the script — the same class of drop as the in-script
-- spell costs. It sat at a made-up 135000 (barely longer than the aether, so the climb was nearly impossible)
-- until this was checked against RTK directly. The tier numbers below are NOT RTK's — they are Warrior Tutor
-- SoulHunter's board post (6/9/12/18/27/81, era-matched 2001-02); RTK's 8/14/20/26/36/81 is a later rebalance.
-- This table is the whole balance surface — edit it and !reload, no rebuild.
--
-- The `msg` strings are the CLIENT'S OWN, transcribed from the Nexus Atlas npcsubpath page (snapshot
-- 2003-08-22, the one whose multipliers are still the era-correct 6/9/12/18/27/81 -- later snapshots carry
-- the 8/14/... rebalance and are the wrong era for us). Note "grows IN you", which is what the archive and
-- the player both report; RTK's Lua says "within you" and is the paraphrase. The bracketed "[Rage N]" is
-- RTK's, kept because the buff box shows only the spell name -- this line is now the only tier readout.
local CHUNG_RYONG_RAGE = {
  { mult = 6,  mana =   2000, ac =   0, msg = "You cast Chung Ryong's rage." },
  { mult = 9,  mana =   7200, ac =   0, msg = "Chung Ryong's power grows in you." },
  { mult = 12, mana =  16200, ac =  -5, msg = "Great rage inspires you." },
  { mult = 18, mana =  28800, ac = -15, msg = "Your body trembles with incredible strength." },
  { mult = 27, mana =  64800, ac = -30, msg = "You enter a mindless frenzy." },
  -- tier 6 wear-out leaves you at 1 vita/mana
  { mult = 81, mana = 145800, ac = -50, msg = "Your body is torn apart with Chung Ryong's power." },
}
local CR_RAGE_DURATION_MS = 938000                        -- RTK `local duration = 938000`, first cast only

-- THE CLIMBING FURIES' LADDERS (the PC-subpath plan's V-rage). RTK's Crossing swords (Do), Sonhi rage (Barbarian) and
-- Spirit of war (Chongun) are copies of RTK's Chung ryong rage, each its own script with its own lines and graphic and
-- all three with the same numbers (RTK Spells/Subpaths/Do/crossing_swords.lua, Barbarian/sonhi_rage.lua,
-- Chongun/spirit_of_war.lua). A SpellParams.csv row binds each to chungryong_rage and names its ladder in `category`;
-- a row with none (Chung Ryong's own) climbs CHUNG_RYONG_RAGE above exactly as it always has. The row's `duration` is
-- the run, armed by the first cast only; the wait is the spell's spell_effects.csv aether, as Chung Ryong's is.
--   * tiers: mult (the whole-swing rage), mana, and ac (an AC delta, RTK's sign: more AC is more damage taken, as
--     Session.Items.cs's ARMOR SIGN note keeps it). rtk_rage's climb costs defence: the scripts' recast adds armor 5 a
--     tier from tier 2 (crossing_swords.lua's `player.armor + armor`), RTK multiplies a blow by 1 + armor / 100
--     (Scripts/magicDamage.lua), and its calcStat runs recast every time it recomputes (rtk/src/map/pc.c), so a
--     tier-6 caster takes 25 AC more damage, as the script has it (#407's review, F1). Chung Ryong's own table keeps
--     its tutor-sourced hardening (negative), which predates this ladder.
--   * gated: a cast inside the wait is refused ("<name> isn't ready yet."), as RTK's engine refuses one inside its
--     aethers before the script runs. Both ladders are gated. Chung Ryong's was not until the project owner's word of 2026-10-26
--     (Q16, "As RTK"): it armed its 120 s wait and nothing checked it, so a climb could run 1 to 6 back to back.
--   * weak: the line for too little mana. Chung Ryong's is ctx:enoughMana's "You do not have enough mana.".
--   * top: what a cast at the top tier does. "hold": nothing, silently and for free (the scripts have no branch for
--     it). Chung Ryong's ladder casts its top tier again, as it always has.
--   * A ladder's tiers may carry their own msg (Chung Ryong's do); otherwise each spell's own six lines are
--     RAGE_LINES[its key], shown after "[Rage N] ".
--   * A climb continues only the fury it climbed: on a row ladder, the running fury must be this spell
--     (ctx.rageName), else the cast starts at tier 1, as the scripts' hasDuration(<own key>) reads it.
-- What a lapsed climb costs in vita is the engine's, looked up by the same ladder name (Session.ChungRyongRageWearOff).
local RAGE_LADDERS = {
  chung_ryongs_rage = { tiers = CHUNG_RYONG_RAGE, gated = true, top = "again" },
  rtk_rage = {
    gated = true, weak = "Your will is too weak.", top = "hold",
    tiers = {
      { mult = 6,  mana =   3000, ac =   0 },
      { mult = 9,  mana =  10000, ac =   5 },
      { mult = 12, mana =  22000, ac =  10 },
      { mult = 18, mana =  38000, ac =  15 },
      { mult = 27, mana =  78000, ac =  20 },
      { mult = 50, mana = 150000, ac =  25 },
    },
  },
}

-- Each row-ladder fury's own lines, tier 1 to 6, as its script words them. Crossing swords' first line names Chung
-- Ryong's rage: the script's own line, copied with the body (Sonhi rage's has the same slip), kept as RTK has it.
local RAGE_LINES = {
  crossing_swords = {
    "You cast Chung Ryong's rage.",
    "Crossing swords grows within you.",
    "Crossing swords inspires you.",
    "Your body trembles with incredible strength.",
    "You enter a mindless frenzy.",
    "Your body is torn apart with Crossing swords.",
  },
  -- Sonhi rage (Barbarian; RTK Subpaths/Barbarian/sonhi_rage.lua). Its first line names Chung Ryong's rage too, as the
  -- script words it; kept as RTK has it (the project owner, 2026-10-30, Q4: "Yes, as RTK").
  sonhi_rage = {
    "You cast Chung Ryong's rage.",
    "Sonhi's rage grows within you.",
    "Sonhi's rage inspires you.",
    "Your body trembles with incredible strength.",
    "You enter a mindless frenzy.",
    "Your body is torn apart with Sonhi's rage.",
  },
}

function verbs.chungryong_rage(ctx, row)
  local name   = row.category or "chung_ryongs_rage"
  local ladder = RAGE_LADDERS[name]
  if not ladder then error("no rage ladder named '" .. tostring(name) .. "' for " .. ctx.spellKey) end
  local tiers  = ladder.tiers
  if ladder.gated and ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  -- Climb from the live tier; if the fury has already lapsed (or never ran) start fresh at 1.
  local climbing = ctx.rageActive and (row.category == nil or ctx.rageName == ctx.spellName)
  local base = climbing and ctx.crRageTier or 0
  if base >= #tiers and ladder.top == "hold" then return false end   -- silent, free: the top tier holds
  local tier = math.min(base + 1, #tiers)
  local t    = tiers[tier]
  local line = t.msg or (RAGE_LINES[ctx.spellKey] or {})[tier]
  if not line then error("no [Rage " .. tier .. "] line for " .. ctx.spellKey) end

  if ladder.weak then
    if ctx.mp < t.mana then ctx:say(ladder.weak); return false end
  elseif not ctx:enoughMana(t.mana) then return false end  -- sends its own "not enough mana" line
  ctx:debitMana(t.mana)
  -- durMs 0 on a climb: keep the run's existing deadline (RTK never re-arms it). Only a fresh cast starts one.
  ctx:setCrRage(tier, t.mult, t.ac, base > 0 and 0 or (row.duration or CR_RAGE_DURATION_MS))
  ctx:setCooldown(ctx.spellKey, ctx.spellAether > 0 and ctx.spellAether or 120000)
  ctx:fxSelf()
  ctx:say("[Rage " .. tier .. "] " .. line)
  ctx:narrated()                                          -- this line REPLACES the central "You cast X."
  return true
end

-- Generic fallback: the ~266 spells the RTK formula export never covered have no spell_effects row at all, so
-- there is no archetype and no formula — only the keyword classifier's guess at what the spell is FOR
-- (ctx.effectKind). Power is a flat stat read, deliberately crude; this is the "we don't know this spell, do
-- something defensible" path, and it stays cheap on purpose. The 5-mana floor and the unaligned graphics
-- (heal 5/4, zap 4/56) are the engine's long-standing defaults, now editable here.
function verbs.generic(ctx, row)
  local cost  = row.mana or 5
  local power = math.max(1, 1 + ctx.will * 4 + ctx.grace * 3)
  local kind  = ctx.effectKind

  if kind == "damage" then
    -- hasDamageTarget, not hasTarget: the latter resolves MOBS only and would refuse a legal self-zap
    -- (unaimed in a PvP map resolves to you) before the damage path ever saw it. It reads the victim the blow
    -- strikes, so an NPC (which no blow can hurt) is no target here either.
    if not ctx.hasDamageTarget then return false end  -- silent
    if not ctx:spendMana(cost) then return false end
    ctx:fxRawTarget(4, 56)                                -- generic unaligned zap
    ctx:damage(power)                                     -- HP bar, death + exp are the engine's job
    return true
  end

  if not ctx:spendMana(cost) then return false end
  if kind == "heal" then
    ctx:setHp(math.min(ctx.maxHp, ctx.hp + power))
    ctx:fx(5, 4)                                          -- generic unaligned heal
  end
  -- "buff"/"other": mana is spent and HandleCast prints "You cast X." — nothing else is known about them.
  return true
end

-- =========================================================================================================
-- PC-SUBPATH DAMAGE SPELLS (RTK rtklua/Accepted/Spells/Subpaths/): Assassinate (Spy), Ki strike (Do), Hailstorm and
-- Ice bombardment (Geomancer), Phoenix flames (Shaman), Earth strike, Nature's smite and Lightning storm (Druid).
-- The project owner, 2026-10-22 ("the nine subpath spells get their RTK scripts") and 2026-10-24 (decision sheet item 18). They
-- have no spell_effects archetype, so they fell to `generic` above: 5 mana for 1 + 4*Will + 3*Grace at a creature.
-- Each is bound by its SpellParams.csv row, which carries its numbers; each verb below is the shape of one RTK
-- script, in that script's order. The graphic and sound over a victim are the spell's spell_effects.csv row, a
-- Utility row, so the cast takes no cast delay, as it took none before. RTK is weight 0 for balance; these numbers
-- are its scripts' by that decision.
--
-- Where the port parts from the scripts, on purpose:
--   * A cast aimed at a player the caster may not attack (off a PvP map, yourself included) is refused before
--     anything is spent: "You can't attack that target." and nothing else, the #351/#356 rule (sheet item 18).
--     RTK's Earth strike and Nature's smite spend their 600 mana first; that is not ported. Phoenix flames and
--     Lightning storm aim at nobody: like RTK they pass over a player they may not attack and strike the rest.
--   * A wait is checked first, with this file's own line, and armed under the spell's own key. Phoenix flames'
--     script arms `phoenix_flame`, a key no spell has, so its 1 s wait never held; here it is `phoenix_flames`.
--   * A player struck reads the engine's one line for every spell, "<caster> hits you with <spell>.", where the
--     scripts word it each their own way. Phoenix flames' says "casts Hailstorm on you", a copy slip.
--   * RTK waives Assassinate's mana and wait for a GM; no verb here waives anything for staff.
--   * Deflect. RTK's engine rolls it only for a cast with a target, free, before the script (clif_parsemagic), and
--     never for a self-cast (type 5) spell, though its table gives canfail 1 to most of these. So Assassinate's and
--     Phoenix flames' Spells.csv rows carry SplCanFail 0, which every deflect roll here reads first. The aimed spells
--     roll it at the blow, after the spend, so a deflect costs what a cast that lands costs (the server's rule for
--     every spell, kept by the project owner on 2026-10-24): Earth strike and Nature's smite inside ctx:damage after the 600, and
--     Ki strike and Ice bombardment through ctx:deflected() after the mana and Ki strike's wait, then strike nothing,
--     neither the target nor its four sides. Theirs is RTK's roll at whatever they strike: a creature by its protection
--     and its Will over the caster's, a player on a PvP map by their Will over the caster's (RTK adds a player's
--     protection too, a stat players here do not have), never the caster. Hailstorm shares their verb but carries
--     SplCanFail 0, as RTK's table does, so it is never deflected. Lightning storm's sweep rolls none.
-- Control flames (Druid) is not here: its acorn-drop hook is the Druid region's (flame_window, at the end of this
-- file), on the drop hook in the item-drop path (Session.RunDropHooks).
-- =========================================================================================================

-- The reach RTK's Earth strike, Nature's smite and Lightning storm test with distanceSquare(player, target, 10):
-- 10 tiles on the larger axis. The scripts' own note: "maximum 10 tiles away in either direction (verified on NTK)".
local SUBPATH_REACH = 10

-- RTK's damage line for six of the scripts: base + level*coeff + floor((will + 1) / 2 * willcoeff), from the
-- caster's effective Will. Earth strike wraps it in math.ceil, which an integer leaves as it is.
local function subpath_amount(ctx, row)
  return (row.base or 0) + ctx.level * (row.coeff or 0) + math.floor((ctx.will + 1) / 2 * (row.willcoeff or 0))
end

-- True, with the refusal said, while the spell's own wait (row.duration) runs. RTK's engine refuses a cast inside
-- its aethers before the script runs (clif_parsemagic), so this is every verb's first question.
local function subpath_waiting(ctx, row)
  if (row.duration or 0) > 0 and ctx:onCooldown(ctx.spellKey) then
    ctx:say(ctx.spellName .. " isn't ready yet.")
    return true
  end
  return false
end

-- Assassinate (Spy; RTK Subpaths/Spy/assassinate.lua). The faced creature, or the faced player on a PvP map, takes
-- ceil(coeff*vita + amount*mana) of the caster's own vita and mana as they stand before the spend, `flat` times
-- that on a `chance`% critical, and the caster keeps 2/3 of their vita (rounded up) only when it struck. The mana
-- and the wait go whatever stands there: at nothing, or at an NPC (no blow can hurt one, and RTK's strike looks for
-- a BL_MOB only), it is still a cast, as the script's own note says. Row: mana 4000, duration 50000, coeff 0.25,
-- amount 1.25, chance 5, flat 5.
function verbs.assassinate(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  local nothing = ctx.victimIsNpc or not ctx.hasDamageTarget or ctx.castAtNothing
  if not nothing and not ctx:canStrike() then return false end    -- a player it may not strike: free, with its line
  local damage = (row.coeff or 0) * ctx.hp + (row.amount or 0) * ctx.mp   -- before the spend, as the script reads it
  if ctx:roll(row.chance or 0) then damage = damage * (row.flat or 1) end
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  if nothing then return true end
  ctx:setHp(math.ceil(ctx.hp * 2 / 3))
  ctx:damage(math.ceil(damage))
  return true
end

-- Ki strike (Do), Hailstorm and Ice bombardment (Geomancer) (RTK Subpaths/Do/ki_strike.lua,
-- Subpaths/Geomancer/hailstorm.lua and ice_bombardment.lua, one body): the target and whatever stands on its four
-- sides take subpath_amount each, a player only on a PvP map. An id that names nobody, or an NPC, casts nothing,
-- silently and for free: RTK's engine runs the script only for a target it found (clif_parsemagic). The sweep is
-- the 5-way primitive the dog fire and the Inferno ladder share (ctx:targetAreaZap), with its 10-tile reach. The
-- sweep rolls no deflect, so the verb rolls RTK's (ctx:deflected(), SplCanFail rows only: Ki strike and Ice
-- bombardment) once, at the target, creature or player, after the spend: deflected, the cast keeps its cost and
-- strikes nothing. Row: mana, base, coeff, willcoeff, and duration for Ki strike's 7 s wait (the Geomancer pair has
-- none).
function verbs.splash_strike(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.victimIsNpc or ctx.castAtNothing then return false end   -- silent, free: nothing to strike
  local mana = row.mana or 0
  if not ctx:enoughMana(mana) then return false end                -- "You do not have enough mana."
  if not ctx:canStrike() then return false end                     -- a player it may not strike: free, with its line
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  if ctx:deflected() then ctx:say("Your magic has been deflected."); return true end
  ctx:targetAreaZap(subpath_amount(ctx, row))                      -- 0 struck is still a cast
  return true
end

-- Phoenix flames (Shaman; RTK Subpaths/Shaman/phoenix_flames.lua): whatever stands on the caster's four sides takes
-- subpath_amount each, a player only on a PvP map, through the 4-way primitive the mage zap ladder uses
-- (ctx:areaZap). Mana first, then the sweep, so a cast into an empty room still costs, as the script orders it.
-- Row: mana 880, base 1030, coeff 3, willcoeff 3, duration 1000 (its wait, under its own key).
function verbs.ring_strike(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if not ctx:enoughMana(mana) then return false end                -- "You do not have enough mana."
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:areaZap(subpath_amount(ctx, row))
  return true
end

-- Earth strike and Nature's smite (Druid; RTK Subpaths/Druid/earth_strike.lua and natures_smite.lua, one body but
-- the name): the target takes subpath_amount, a player only on a PvP map. Beyond SUBPATH_REACH, or at nothing or an
-- NPC, the cast does nothing, silently and for free, as the script's distanceSquare return does. Row: mana 600,
-- base 50, coeff 2, willcoeff 1.5.
function verbs.ranged_strike(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.victimIsNpc or ctx.castAtNothing then return false end   -- silent, free: nothing to strike
  if ctx.victimDistance > SUBPATH_REACH then return false end      -- silent, free: out of reach
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  if not ctx:canStrike() then return false end                     -- a player it may not strike: free, with its line
  ctx:debitMana(mana)
  ctx:damage(subpath_amount(ctx, row))
  return true
end

-- Lightning storm (Druid; RTK Subpaths/Druid/lightning_storm.lua): every creature within SUBPATH_REACH of the
-- caster, and every other player there on a PvP map, takes a flat `amount` on its own `chance`% roll (the script's
-- "only 50% of targets"). Mana and the wait first, then the sweep, so a cast that strikes nothing still costs.
-- Row: mana 1200, duration 87000, amount 3600, chance 50.
function verbs.storm_strike(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:rangeZap(row.amount or 0, SUBPATH_REACH, row.chance or 100)
  return true
end

-- =========================================================================================================
-- SHAMAN (PC subpath; RTK rtklua/Accepted/Spells/Subpaths/Shaman/, and Subpaths/TESTING/ for Shaman's torment):
-- Shaman's solace, Reincarnate and Shaman's torment. The project owner, 2026-10-26: each spell as its RTK script (P5), and
-- torment built from RTK's unfinished TESTING script and sold like the others (Q7). Phoenix flames, the fourth, is
-- ring_strike above. Each is bound by its SpellParams.csv row, which carries its numbers, and each verb is the shape
-- of its script, in that script's order. The graphic and sound over a target are the spell's spell_effects.csv row, a
-- Utility row (torment has none: its one graphic is drawn for its caster alone). RTK is weight 0 for balance; these
-- numbers are its scripts' by those decisions.
--
-- Where the port parts from the scripts, on purpose, as the damage spells above part:
--   * Reincarnate's wait is checked first, with this file's own line, and armed under its own key (subpath_waiting).
--   * The cast pose is the engine's, on a cast that lands. The scripts send their own: torment's before its mana
--     check, so RTK poses on that refusal too.
--   * A player healed or revived reads the engine's one line, "<caster> casts <spell> on you."; solace's script says
--     "cast". On yourself solace says nothing more than "You cast Shaman's solace.".
--   * A solace aimed at nothing heals its caster, as every "Which target?" heal here does (the 4.95 client casts with
--     the slot alone, and you never face yourself). RTK's engine runs no script without a target.
--   * RTK waives Reincarnate's wait for a GM; no verb here waives anything for staff.
--   * Torment's run is the script's roll of N seconds as RTK's creature timer counts it, N x 1.5 s, so 7.5-30 s
--     (TORMENT_SECOND_MS, at verbs.torment). RTK's own run ends on its timer's step 20 (rtk/src/map/mob.c:653-655),
--     so it is that long for a cast just after that step and up to 1.45 s shorter for the rest. RTK beats on step 30
--     (mob.c:650-652), 0.05-1.5 s after the cast first, and N - 1 times in two casts of three, N in the third (a cast
--     in the half second from step 20 to step 30). The beats here come 1.5 s apart from the cast, and a fresh run's
--     count is RTK's (the project owner's choice, 2026-10-30): N - 1, strictly before the run's end, and in one fresh
--     run in three an Nth at N x 1.5 s, the run then lasting 1 s past it (TORMENT_EARLY_STEPS, at verbs.torment); 4-20
--     across rolls. A recast at a running run keeps its beat and rolls nothing, so from the recast on it strikes N
--     times, every time, where RTK gives N in one recast of three and N - 1 otherwise. A creature counts toward
--     another run's siphon count until its own run ends; each run here keeps its own beat, so another run's blow is
--     divided by one more at about one beat more than the creature itself took, where RTK's beats all fall on one
--     step. A creature's threat from a beat is the blow it took, as from every blow here; the script adds its figure
--     before armour. A ghost casts nothing here (the engine's rule); the script's canCast(0, ...) lets a ghost
--     torment.
-- =========================================================================================================

-- Shaman's solace (RTK Subpaths/Shaman/shamans_solace.lua): its target, a player or a creature, takes `amount` vita,
-- within SUBPATH_REACH tiles. Beyond that, or at an NPC, the cast does nothing, silently and for free, as the script's
-- distanceSquare return does. A ghost answers "You need a different type of cure.", for free. Row: mana 600,
-- amount 2200.
function verbs.ranged_heal(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local reach = ctx.healReach
  if reach < 0 or reach > SUBPATH_REACH then return false end      -- silent, free: out of reach, or an NPC
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  if ctx.healIsGhost then ctx:say("You need a different type of cure."); return false end
  ctx:debitMana(mana)
  ctx:healTarget(row.amount or 0)
  return true
end

-- Reincarnate (RTK Subpaths/Shaman/reincarnate.lua): every dead player on the caster's four sides comes back where
-- they lie with `amount` of their max vita (the script's math.floor(maxHealth * 0.10)) and the mana they had; the
-- living are left alone. The mana and the wait go first, so a cast with nobody dead beside you still costs both, as
-- the script orders it. Row: mana 10000, amount 0.1, duration 7500000 (its 7,500 s wait).
function verbs.revive_around(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:reviveAround(row.amount or 0)
  return true
end

-- Shaman's torment (RTK Subpaths/TESTING/shamans_torment.lua, an unfinished testing script, ported as written). The
-- cast takes mana + coeff x max mana once max mana x amount is there (it can take more than that, down to 0), counts
-- a creature into the caster's siphon count, and then: at a player, "This target's soul is too pure for this magic.";
-- at a creature, a run of rollRange(duration, durationMax) whole seconds, each as long as RTK's creature timer makes it
-- (TORMENT_SECOND_MS), whose beat (torment_tick) strikes it every TORMENT_TICK_MS; one fresh run in three lands one
-- beat more, at its rolled end (TORMENT_EARLY_STEPS).
-- At nothing, or at an NPC, it casts nothing, silently and for free: RTK's engine runs no script without a target.
-- The siphon count is RTK's player.registry "totalsiphon", kept here for the login only and never shown: the script's
-- debug line "total siphon: N", sent on every cast, is dropped on the project owner's decision (2026-10-26). The beat
-- recounts it and divides its blow by it. Row: mana 30, coeff 0.01, amount 0.025, duration 5000, durationMax 20000.
local TORMENT_TICK_MS = 1500            -- the script's while_cast_1500
local TORMENT_ANIM    = 84              -- its target:selfAnimation(player.ID, 84, 1), for the caster alone
local TORMENT_SIPHON  = "totalsiphon"   -- its player.registry key
-- One second of the script's duration as RTK's creature timer counts it, the project owner's choice (2026-10-26,
-- "Match RTK"): mob_timer_spawns runs every 50 ms (rtk/src/map/map.c:1863) through a 30-step cycle
-- (rtk/src/map/mob.c:633-689), and mob_duratimer, on the cycle's step 20, takes 1,000 off a creature's duration
-- (mob.c:463-514). So 1,000 lasts 1.5 s, and the script's 5-20 s roll runs 7.5-30 s.
local TORMENT_SECOND_MS = 1500
-- RTK's beat count, the project owner's choice (2026-10-30, "Match RTK's count"). A cast falls somewhere in the
-- timer's 30-step cycle. In the 10 steps from step 20 to step 30 (mob.c:650-655) it meets a beat before its first
-- countdown, so a roll of N beats N times; anywhere else, N - 1. Here the beat comes 1.5 s apart from the cast and
-- strictly before the run's end, so a run of N x 1.5 s beats N - 1 times. One fresh run in three is made long enough
-- for the beat at N x 1.5 s to land: TORMENT_TAIL_MS past it, the 1 s from RTK's step 30 to its next step 20, which is
-- how long every RTK run lasts after its last beat. That beat then lands inside the run, so it counts its own
-- creature, as every beat does (the world heartbeat runs a beat up to one of its beats late). A recast that sets a
-- running run's length again keeps that run's beat (ctx:startTick), so it rolls nothing here: the kept beat is less
-- than 1.5 s off, so the run strikes N times from the recast on, every time. RTK sets a recast's duration against its
-- one timer, so it gives N in one recast of three and N - 1 otherwise; this port does not match RTK's odds on a
-- recast.
local TORMENT_CYCLE_STEPS = 30
local TORMENT_EARLY_STEPS = 10
local TORMENT_TAIL_MS     = 1000

function verbs.torment(ctx, row)
  if ctx.victimIsNpc or ctx.castAtNothing then return false end    -- silent, free: nothing to torment
  local maxmp = ctx.maxMp
  local magic = (row.mana or 0) + maxmp * (row.coeff or 0)
  if ctx.mp < maxmp * (row.amount or 0) then ctx:say("You do not have enough mana."); return false end
  ctx:setMana(math.max(0, math.floor(ctx.mp - magic)))             -- RTK's assignment truncates; never below 0 here
  local creature = ctx.hasTarget
  if creature then
    local siphon = ctx:tempReg(TORMENT_SIPHON)
    if not ctx:tickRunning() then siphon = siphon + 1 end
    if siphon < 0 then siphon = 0 end
    ctx:setTempReg(TORMENT_SIPHON, siphon)
  end
  if creature then
    local secs = ctx:rollRange((row.duration or 0) / 1000, (row.durationMax or 0) / 1000)
    local ms = secs * TORMENT_SECOND_MS
    if not ctx:tickRunning() and ctx:rollRange(1, TORMENT_CYCLE_STEPS) <= TORMENT_EARLY_STEPS then
      ms = ms + TORMENT_TAIL_MS                                    -- the beat at secs x 1.5 s lands
    end
    ctx:startTick("torment_tick", TORMENT_TICK_MS, ms)
    ctx:selfAnimation(TORMENT_ANIM)
  else
    ctx:say("This target's soul is too pure for this magic.")
  end
  ctx:narrated()                                                   -- the script sends no "You cast" line
  return true
end

-- The torment's beat (the script's while_cast_1500), on each creature it holds. If the caster has left the creature's
-- map the run ends, and this beat still lands, as the script orders it. The siphon count becomes how many creatures on
-- that map this caster's torment holds; the blow is max mana x 0.1 x 2 / (1 + e^(-0.95 x (n - 1))) / n, rounded up,
-- plus 1000, with n that count kept between 1 and 20, then netted against the creature's armour and never under 1000.
function verbs.torment_tick(ctx, row)
  if not ctx.victimHere then ctx:endTick() end
  ctx:selfAnimation(TORMENT_ANIM)
  local total = ctx:tickCount()
  ctx:setTempReg(TORMENT_SIPHON, total)
  local amount = total
  if amount > 20 then amount = 20 end
  if amount < 1 then amount = 1 end
  local damage = ctx.maxMp * 0.1
  local factor = 2 / (1 + 2.71828 ^ (-1 * (0.95 * (amount - 1))))
  damage = math.ceil(damage * factor / amount) + 1000
  ctx:strikeVictim(damage, 1000)
  return true
end

-- =========================================================================================================
-- BARBARIAN (PC subpath; RTK rtklua/Accepted/Spells/Subpaths/Barbarian/). The project owner, 2026-10-26 and
-- 2026-10-30: each spell as its RTK script (P5); Push and Break jaw land on any player on any map with no PK check,
-- and Sonhi rage climbs RTK's tiers with its armour sign (Q4, "Yes, as RTK"). The verbs here are Barbarian cry, Rend,
-- Mount, Wilderness guise, Push, Break jaw, Insult and Dishonor. The others ride families that already exist: Sonhi
-- rage climbs chungryong_rage's rtk_rage ladder (its lines are RAGE_LINES.sonhi_rage), Dash is Do's dash verb (the
-- script is Do's body under another table name), Envigorate is a Heal row and Tiger's might a Buff row in
-- spell_effects.csv, Barbarian rage a rage row and Wilderness weapon and Nomadic blade enchant rows in SpellMods.csv.
-- Survival challenge has no script of its own (the file is Dishonor's body) and stays as it was. Each verb is bound by
-- its SpellParams.csv row, which carries its numbers, and is the shape of its script, in that script's order. The
-- graphic and sound over a target are the spell's spell_effects.csv row. RTK is weight 0 for balance; these numbers
-- are its scripts' by those decisions.
--
-- Where the port parts from the scripts, on purpose, as the subpath verbs above part:
--   * Each wait is checked first, with this file's own line, and armed under the spell's own key (subpath_waiting).
--     RTK waives the waits for a GM; no verb here waives anything for staff.
--   * The cast pose is the engine's, on a cast that lands, and so is the central "You cast X." line, unless a verb
--     says its script's own lines and marks the cast narrated.
--   * A ghost casts nothing and a mounted caster nothing (the engine's gates), where some scripts let a ghost cast.
--   * A player struck reads the engine's one line, "<caster> hits you with <spell>.".
-- =========================================================================================================

local CRY_SHOUT  = "TOWNIE~~!"   -- barbarian_cry.lua: player:talk(2, "TOWNIE~~!")
local REND_SHOUT = "Rraah~!"     -- rend.lua: player:talk(2, "Rraah~!")

-- Barbarian cry (RTK Subpaths/Barbarian/barbarian_cry.lua). The caster pays `mana` and keeps `coeff` of their vita
-- (math.ceil), shouts, and the creature on the faced tile and the player there, when the caster may attack them (a
-- PvP map), each take `amount` x the caster's vita as it stood before (math.ceil): RTK's two getTargetFacing calls and
-- global_zap, netted against the target's armour with no deflect roll (a self-cast spell). The cost is paid whatever
-- stands there. Short of mana, "You do not have enough mana.", for free. The script's three lines, its copy slip
-- included, replace the central one. Row: mana 3100, amount 3, coeff 0.33, duration 1200000 (its wait).
function verbs.cry_strike(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("You do not have enough mana."); return false end
  local damage = math.ceil(ctx.hp * (row.amount or 0))
  ctx:setHp(math.ceil(ctx.hp * (row.coeff or 1)))
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:shout(CRY_SHOUT)
  ctx:strikeFaced(damage, "each")
  ctx:say("You cast Townie.")
  ctx:say("You can not touch the spirit world.")
  ctx:say("You cast " .. ctx.spellName .. ".")
  ctx:narrated()
  return true
end

-- Rend (RTK Subpaths/Barbarian/rend.lua). Nothing is checked but the wait: the script names 3100 mana and never asks
-- for it. The blow is `amount` x the caster's vita plus `amount` x their mana (each math.ceil), and the caster is left
-- with `coeff` of their max vita (truncated; never under 1 here) and no mana, whatever stands there. The first thing
-- on the faced tile takes it: a player there (when the caster may attack them, else nobody), else the creature (an NPC,
-- nobody), as RTK's getTargetFacing(BL_ALL) lists a cell's players before its creatures. Netted against armour, no
-- deflect roll. The script's two lines replace the central one. Row: amount 2, coeff 0.05, duration 87000.
function verbs.rend_strike(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local times = row.amount or 0
  local damage = math.ceil(ctx.hp * times) + math.ceil(ctx.mp * times)
  ctx:setHp(math.floor(ctx.maxHp * (row.coeff or 1)))
  ctx:setMana(0)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:shout(REND_SHOUT)
  ctx:strikeFaced(damage, "first")
  ctx:say("You cast " .. ctx.spellName .. ".")
  ctx:say("You can not touch the spirit world.")
  ctx:narrated()
  return true
end

-- Mount (RTK Subpaths/Barbarian/mount.lua): the creature `category` names (a mobs.csv identifier) appears on the faced
-- tile when nothing is there (no map object, a passable tile, no warp, nobody and nothing on the floor, inside RTK's
-- bounds), else on the caster's own tile; nobody owns it, and the ride key ('r') rides it as it rides any horse. The
-- mana is checked and never taken, as the script: short of it, "You do not have enough mana.", for free. Row: mana
-- 600, category horse, duration 187000 (its wait).
function verbs.spawn_mount(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.mp < (row.mana or 0) then ctx:say("You do not have enough mana."); return false end
  ctx:spawnFaced(row.category or "")
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  return true
end

-- Wilderness guise (RTK Subpaths/Barbarian/wilderness_guise.lua): RTK's menu of five guises. The cast holds a 1 s lock
-- (the script's setDuration(key, 1000)), within which another cast says "Spell already cast."; the mana is checked and
-- never taken ("Your will is too weak." short of it, free). The menu's answer runs guise_pick, which takes on the
-- chosen guise's look for its time, the spell's Morphs.csv row (Wolf 23, Rabbit 21, Deer 89, Sheep 173, Thirsty ogre
-- 185; 300 s), ending any other form, as the script's removeDuras(morphs). A closed box changes nothing. Row: mana 30.
local GUISE_LOCK_MS = 1000
local GUISE_PROMPT  = "Which guise would you like to use?"
local GUISES        = "Wolf|Rabbit|Deer|Sheep|Thirsty ogre"

function verbs.guise_menu(ctx, row)
  if ctx:hasDuration(ctx.spellKey) then ctx:say("Spell already cast."); return false end
  if ctx.mp < (row.mana or 0) then ctx:say("Your will is too weak."); return false end
  ctx:setDuration(ctx.spellKey, GUISE_LOCK_MS)
  ctx:menu(GUISE_PROMPT, GUISES, "guise_pick")
  return true
end

function verbs.guise_pick(ctx, row)
  ctx:morphTo(ctx.answer)
  return true
end

-- Push (RTK Subpaths/Barbarian/push.lua): the player on the faced tile, on any map and with no PK check (the project
-- owner, 2026-10-30, Q4), goes one tile on in the caster's facing, or two when the first is taken, when that tile is
-- free (no map object, passable, no warp, nobody and nothing on the floor, inside RTK's bounds). Nobody faced: nothing
-- happens and nothing is spent. Short of `mana`, "Your will is too weak.", for free. With a target the mana is spent
-- even when neither tile is free ("You can't push it."). The pushed player reads "<caster> casts Push spell on you.".
-- Row: mana 600 (no wait: the script arms none).
function verbs.push(ctx, row)
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  if not ctx:facedPlayer() then return false end
  ctx:debitMana(mana)
  if ctx:pushTarget() then
    ctx:tellTargetLine(ctx.name .. " casts Push spell on you.")
    return true
  end
  ctx:say("You can't push it.")
  ctx:narrated()
  return true
end

-- Break jaw (RTK Subpaths/Barbarian/break_jaw.lua): the wait is armed and the cast lands whatever stands there (the
-- script names 600 mana and never asks for it). The player on the faced tile, on any map and with no PK check (Q4),
-- whose jaw is not already broken, speaks garbled for `durationMax` ms (speech.lua: each letter a 1-in-5 chance of
-- '*'; a GM speaks plainly) and reads "<caster> lands a swift blow to your jaw."; at its end "Your jaw feels better."
-- (its SpellText.csv fade line). Row: duration 150000 (its wait), durationMax 60000.
function verbs.break_jaw(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  if ctx:facedPlayer() and ctx:speechStatus("garbled", row.durationMax or 0) then
    ctx:tellTargetLine(ctx.name .. " lands a swift blow to your jaw.")
  end
  return true
end

-- Insult and Dishonor (RTK Subpaths/Barbarian/insult.lua and dishonor.lua, one body but the number): for `mana`, a
-- curse of `stat` `amount` for `duration` ms on whatever stands on the caster's four sides, north, east, south, west,
-- as RTK's getTargetsAround walks them: each creature, and each player the caster may attack (a PvP map), that carries
-- no curse or protection already (the category's BLOCKS), with the spell's graphic and, over a creature, the scripts'
-- while_cast graphic 34 each second. A player reads "<caster> attacks you with <spell> spell." and at the end the
-- spell's SpellText.csv fade line. A dead player met on the walk ends it there, as the scripts' `return` does. The
-- mana is spent with nobody around. No wait: the scripts name 150 s and never arm it. Row: mana 200, category curses,
-- stat armor, amount 30 (Insult) or 50 (Dishonor), duration 425000.
local CURSE_AROUND_BEAT_ANIM = 34     -- while_cast: block:sendAnimation(34, 0)
local CURSE_AROUND_BEAT_MS   = 1000   -- RTK runs while_cast once a second

function verbs.curse_around(ctx, row)
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  local category = row.category or "curses"
  local blocked = table.concat(BLOCKS[category] or { category }, "|")
  ctx:curseAround(category, row.stat or "", math.floor(tonumber(row.amount) or 0), row.duration or 0, blocked,
                  ctx.name .. " attacks you with " .. ctx.spellName .. " spell.",
                  CURSE_AROUND_BEAT_ANIM, CURSE_AROUND_BEAT_MS)
  return true
end

-- =========================================================================================================
-- DO (PC subpath; RTK rtklua/Accepted/Spells/Subpaths/Do/): Dash, Blend, Hear footstep and Assess opponent. The project owner,
-- 2026-10-26: each spell as its RTK script (P5), Spirit blade x10 as RTK's (Q8), Blend as its script rather than as the
-- rogue's Invisible (Q15). The other five Do spells ride families that already exist: Ki strike is splash_strike above;
-- Spirit blade, Balance weapon and Master weapon are enchants (x10, x15, x20 in game-data/SpellMods.csv, the
-- stance_enchant verb); Crossing swords climbs chungryong_rage's rtk_rage ladder. Each verb here is bound by its
-- SpellParams.csv row, which carries its numbers, and is the shape of its script, in that script's order. RTK is weight
-- 0 for balance; these numbers are its scripts' by those decisions.
--
-- Where the port parts from the scripts, on purpose, as the subpath verbs above part:
--   * Each wait is checked first, with this file's own line, and armed under the spell's own key (subpath_waiting).
--   * The cast pose is the engine's, on a cast that lands, and so is the central "You cast X." line; Dash's script
--     sends none. Assess opponent's readout replaces it, as an inspect's result does.
--   * RTK waives Dash's wait for a GM; no verb here waives anything for staff.
-- =========================================================================================================

-- Dash (RTK Subpaths/Do/dash.lua; Barbarian's dash.lua is the same body, so a Barbarian row binds here too): up to
-- `amount` tiles in the faced direction, stopping before the first blocked one (ctx:leap, the walk the rogue's Race
-- takes; RTK's checkSpecificTile), with a `duration` ms wait. Short of `mana`, "Your will is too weak.", for free. With
-- it, nothing is spent: the script checks the mana and never takes it. A dash with nowhere to go still arms its wait,
-- as the script arms it before its warp. Row: mana 250, amount 3, duration 150000.
function verbs.dash(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.mp < (row.mana or 0) then ctx:say("Your will is too weak."); return false end
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:leap(row.amount or 3)
  return true
end

-- Blend (RTK Subpaths/Do/blend.lua; Spy's incognito.lua and Diviner's vanish.lua are its shape with other numbers and
-- no lines, so their rows can bind here): the caster turns invisible (RTK state 2) for `durationMax` ms with the next
-- landed swing x`amount` (the script's invis = 9), for `mana`, with a `duration` ms wait, on the stealth state the
-- rogue's Invisible uses (the faded look; a landed hit or a pickup ends it; Hear footstep and Second Sight see it).
-- "You cast Blend." is the engine's central line, which the script sends too. A cast while already invisible casts
-- again, as the script does (it has no check): the mana, the wait and the line again, but the run is not lengthened,
-- since RTK's setDuration sets a running duration again only to a shorter time (ctx:hide). Refused: inside the wait
-- (this file's line); in a creature's form, "You can't do that while transformed." (the script's canCast(1, 1, 1));
-- short of mana, "Your will is too weak.", for free. When it ends it says its SpellText.csv fade line, "You are no
-- longer invisible.", as the script's uncast does: at its time, a landed hit, a pickup or a won Dispell; not at death,
-- where RTK's uncast finds the player already dead (Session.RevertStealth). Row: mana 4000, amount 9, duration 30000,
-- durationMax 120000.
function verbs.hide(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.morphed then ctx:say("You can't do that while transformed."); return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  ctx:hide(row.durationMax or 0, row.amount or 1)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:fxSelf()
  return true
end

-- Hear footstep (RTK Subpaths/Do/hear_footstep.lua; Spy's and Diviner's are the same body): for `durationMax` ms the
-- caster is told, every HEAR_FOOTSTEP_BEAT_MS, each other player standing invisible within `amount` tiles on both axes,
-- one line a name ("<name> is hidden in the area."). The run is the periodic tick on the caster themselves
-- (ctx:startSelfTick), whose beat is hear_footstep_tick. The cast takes `mana` (0), draws its spell_effects.csv graphic
-- over the caster and arms the `duration` ms wait. Row: mana 0, amount 4, duration 60000, durationMax 32000.
local HEAR_FOOTSTEP_BEAT_MS = 5000   -- the script's while_cast speaks when os.time() % 5 == 0: every 5 s

function verbs.hear_footstep(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  ctx:fxSelf()
  ctx:startSelfTick("hear_footstep_tick", HEAR_FOOTSTEP_BEAT_MS, row.durationMax or 0)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  return true
end

-- Hear footstep's beat (the script's while_cast), on the caster: RTK's distanceSquare(player, pc, 4) and state == 2,
-- the caster left out. Nobody hidden, nothing said.
function verbs.hear_footstep_tick(ctx, row)
  for name in string.gmatch(ctx:hiddenNear(row.amount or 0), "[^\n]+") do
    ctx:say(name .. " is hidden in the area.")
  end
  return true
end

-- Thousands commas, as RTK's Tools.formatNumber writes them ("12,345,678"). The scripts call Tools.formatValue, which
-- RTK defines nowhere (its Tools.lua has formatNumber), so RTK's own readout stops with a Lua error after a player's
-- first line; these are the two lines the script plainly means, written with the formatter RTK has.
local function commas(n)
  local s = tostring(math.floor(n))
  local count
  repeat s, count = string.gsub(s, "^(-?%d+)(%d%d%d)", "%1,%2") until count == 0
  return s
end

-- Assess opponent (RTK Subpaths/Do/assess_opponent.lua; Chongun's evaluate_opponent.lua and Spy's assess_opponent.lua
-- are the same body): a readout of the target. A creature: "[<name>] VIT: <max vita>". A player: "[<title> <name>]",
-- "VIT: <vita>" and "MANA: <mana>", with commas. At nothing, or an NPC, it casts nothing, silently and for free: RTK's
-- engine runs the script only for a target it found. The engine's deflect comes first, as RTK's clif_parsemagic rolls
-- it before the script for a spell whose row can fail (this one's can): deflected, nothing is read, armed or spent. Short
-- of `mana`, "Your will is too weak.", for free; with it, nothing is spent, as the script checks the mana and never
-- takes it. Row: mana 550, duration 25000.
function verbs.assess(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.victimIsNpc or ctx.castAtNothing then return false end    -- silent, free: nothing to judge
  if ctx:victimDeflects() then ctx:say("Your magic has been deflected."); return false end
  if ctx.mp < (row.mana or 0) then ctx:say("Your will is too weak."); return false end
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  if ctx.victimKind == "creature" then
    ctx:say("[" .. ctx.victimName .. "] VIT: " .. math.floor(ctx.victimMaxHp))
  else
    ctx:say("[" .. ctx.victimTitle .. " " .. ctx.victimName .. "]")
    ctx:say("VIT: " .. commas(ctx.victimHp))
    ctx:say("MANA: " .. commas(ctx.victimMp))
  end
  ctx:narrated()                                                   -- the readout replaces "You cast Assess opponent."
  return true
end

-- Amnesia (RTK Spells/rogue/amnesia.lua): the target mob FORGETS you. Your threat on it is wiped and it will
-- not choose you again for row.duration, though it keeps fighting anyone else -- so this is a peel, not a
-- mez: the creature stays dangerous, just not to you. Hitting it again reminds it instantly (the engine
-- clears the amnesia on any damage you deal). Bosses shake it off in row.durationMax instead, which is RTK's
-- own `if target.isBoss == 1 then duration = 5000`.
function verbs.amnesia(ctx, row)
  local mana = row.mana or 30
  if not ctx:enoughMana(mana) then ctx:say("Your will is too weak."); return false end
  -- row.chance is the take-hold rate. A miss returns true (mana spent), the creature just shrugs it off; the
  -- effect is never a status, so there is no re-cast lock and no "already cast" — cast it again.
  if not ctx:amnesiaTarget(row.duration or 900000, row.durationMax or 5000, row.chance or 75) then return false end
  ctx:debitMana(mana)
  return true
end

-- Confuse (RTK Spells/mage/confuse.lua): NOT a debuff and NOT Amnesia's per-caster peel. It is a chance-based
-- aggro RESET — on success the target mob's whole threat table is wiped and it forgets everyone; if a creature
-- is standing right beside it, the confused mob turns on THAT creature (blind two mobs side by side and spam
-- Confuse and they fight each other). No status is applied, so it never says "already cast" and can be cast as
-- fast as you like. A miss still spends the mana (so re-casting until it lands isn't free) and just says the
-- creature resisted. row.mana, row.chance.
function verbs.confuse(ctx, row)
  local mana = row.mana or 30
  if not ctx:enoughMana(mana) then ctx:say("Your will is too weak."); return false end
  if not ctx:confuseTarget(row.chance or 65) then return false end
  ctx:debitMana(mana)
  return true
end

-- =========================================================================================================
-- DIVINER (PC subpath; RTK rtklua/Accepted/Spells/Subpaths/Diviner/): Read karma and Divine time. The project owner's
-- word, 2026-10-26 and again 2026-10-30: each spell as its RTK script (P5); Divine time kept, though a 2001 page calls
-- it gone from the game (decision sheet item 17, the era rule's fail-open). Diviner's other two working spells bind to
-- the Do verbs by their SpellParams.csv rows: Hear footstep to hear_footstep (its script is Do's under another key),
-- Vanish to hide (its script is Blend's shape: invisible for 1,000 s, the next landed swing x9, no mana, no wait, and
-- no line when it ends, so it has no SpellText.csv fade line). Each verb is the shape of its script, in that script's
-- order; RTK is weight 0 for balance, and these numbers are its scripts'.
--
-- Read karma is three steps, because RTK's script waits twice for its player (an async script: inputSeq, then
-- dialogSeq) and a verb here is one call: each wait names the verb that runs once the reply comes (ctx:ask, then
-- ctx:dialogThenTarget; the note above Session.LuaAsk). Monk's and Geomancer's read_karma.lua are this script under
-- their own keys, so their rows can bind here too: the wait is armed under ctx.spellKey.
--
-- Where the port parts from the scripts, on purpose, as the subpath verbs above part:
--   * A wait is checked first, with this file's own line (subpath_waiting): RTK's engine refuses a cast inside its
--     aethers before the script runs.
--   * The cast pose is the engine's, on a cast that lands, as for every spell; neither script sends one. Their boxes
--     and lines replace the central "You cast X." line, as Assess opponent's readout does.
--   * A ghost or a mounted caster casts nothing here, the engine's rule for every spell; Read karma's
--     canCast(0, 1, 0) lets a ghost cast it.
--   * The input box carries the script's question alone. Its two lines either side of the entry field ("The one
--     known as", "shall be examined.") are not sent: no input box here sends them (Session.SendInputBox), and the
--     4.95 client's drawing of them is unread.
--   * A typed name is looked up as every typed name here is (ctx:pcTargetNamed: any map, any case, trimmed); RTK's
--     Player(name) matches any case and does not trim.
-- =========================================================================================================

-- Read karma's ladder (RTK read_karma.lua:45-115), best first: the band a karma of at least `from` falls in, the
-- portrait both boxes carry (a creature look in its palette, or the Angel's tear's item icon), what the caster reads
-- after the target's name, and what the target reads. Below the cat the two open bands: the rat above -3, the snake at
-- -3 and below. The script's own words, its doubled "and and" (the bear) included.
local KARMA_LADDER = {
  { from = 30, look = 49,  color = 5,  them = " is one with the Gods, and their soul is worthy to dwell among them.",
    you = "You are one with the Gods, and your soul is worthy to dwell among them." },
  { from = 24, item = "angels_tear",
    them = "'s journey has been long; they have achieved inner peace and tranquility; they speak with the Gods.",
    you = "Your journey has been long; you have achieved inner peace and tranquility; you speak with the Gods." },
  { from = 19, look = 167, color = 24, them = " has reached the threshold of immortality; they put aside mortal possessions and passions.",
    you = "You have reached the threshold of immortality; you put aside mortal possessions and passions." },
  { from = 14, look = 108, color = 2,  them = " is at the pinnacle of mortal grace; their soul is noble, beautiful and fearsome to behold.",
    you = "You are at the pinnacle of mortal grace; your soul is noble, beautiful and fearsome to behold." },
  { from = 11, look = 29,  color = 9,  them = " has entered the nobility of souls -- fierce, wise and just.",
    you = "You have entered the nobility of souls -- fierce, wise and just." },
  { from = 8,  look = 24,  color = 9,  them = " holds quiet power and and great wisdom, confidence and fortitude.",
    you = "You hold quiet power and and great wisdom, confidence and fortitude." },
  { from = 6,  look = 27,  color = 9,  them = " is stalwart and honest, worthy of trust, strong of heart.",
    you = "You are stalwart and honest, worthy of trust, strong of heart." },
  { from = 4,  look = 126, color = 5,  them = " is clever and good-hearted; the Gods smile upon them.",
    you = "You are clever and good-hearted; the Gods smile upon you." },
  { from = 3,  look = 18,  color = 12, them = " is faithful to the teachings of the Gods.",
    you = "You are faithful to the teachings of the Gods." },
  { from = 2,  look = 21,  color = 3,  them = "'s soul moves ever quicker toward its goal.",
    you = "Your soul moves ever quicker toward its goal." },
  { from = 1,  look = 25,  color = 9,  them = "'s education progresses; they store knowledge for the ordeals to come.",
    you = "Your education progresses; you store knowledge for the ordeals to come." },
  { from = 0,  look = 182, color = 30, them = " has begun the journey to perfection.",
    you = "You have begun the journey to perfection." },
}
local KARMA_RAT = { look = 91, color = 10,
  them = " is out of favor with the Gods; they must purify their soul and cleanse their spirit.",
  you = "You are out of favor with the Gods; you must purify your soul and cleanse your spirit." }
local KARMA_SNAKE = { look = 184, color = 25,
  them = " is among the cursed of the earth; they crawl alone in shame.",
  you = "You are among the cursed of the earth; you crawl alone in shame." }

local function karma_band(karma)
  if karma <= -3 then return KARMA_SNAKE end
  for _, band in ipairs(KARMA_LADDER) do
    if karma >= band.from then return band end
  end
  return KARMA_RAT
end

-- Read karma (RTK Subpaths/Diviner/read_karma.lua): asks a name and shows that player's karma. The cast: its wait;
-- short of `mana`, "You do not have enough mana.", for free; then the input box, "Who's karma shall you read?", and
-- nothing spent yet. The answer runs read_karma_named.
function verbs.read_karma(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.mp < (row.mana or 0) then ctx:say("You do not have enough mana."); return false end
  ctx:ask("Who's karma shall you read?", "read_karma_named")
  ctx:narrated()
  return true
end

-- Read karma's answer (the script after its inputSeq). Anything but letters, spaces and an apostrophe: "You can only
-- enter letters and spaces." (RTK inputLetterCheck's box; its script would then say the next line too, once that box
-- was answered, and a box with no button gets no answer from this client). Nobody online by that name: "Player is not
-- online or unavailable.". Neither costs anything. Otherwise, yourself included (the script's own check is commented
-- out): the `mana` is taken (what there is, down to 0, as every debit here), the `duration` ms wait is armed, and the
-- caster reads "<name>" and the band's words in a box with the band's portrait; once they answer it, the player read
-- gets their own box, "You ..." (ctx:dialogThenTarget). The script checks the mana before its question and takes it
-- after, with no second check.
function verbs.read_karma_named(ctx, row)
  if string.find(ctx.answer, "[^%a%s']") then ctx:dialog("You can only enter letters and spaces."); return false end
  if not ctx:pcTargetNamed(ctx.answer) then ctx:dialog("Player is not online or unavailable."); return false end
  ctx:debitMana(row.mana or 0)
  local band = karma_band(ctx.targetKarma)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:dialogThenTarget(ctx.targetName .. band.them, band.you, band.look or 0, band.color or 0, band.item or "")
  return true
end

-- RTK's ordinal ending for a number, as general_npc_funcs.time writes it: 1st, 2nd, 3rd, and "th" for every number
-- from 4 up (so the 21st day of the season reads "21th", as the script writes it).
local function rtk_ordinal(n)
  if n == 1 then return "st" elseif n == 2 then return "nd" elseif n == 3 then return "rd" elseif n >= 4 then return "th" end
  return ""
end

local DIVINE_DAY_NAMES = { "Mon", "Tue", "Wed", "Thurs", "Fri", "Sat", "Sun" }

-- Divine time (RTK Subpaths/Diviner/divine_time.lua, which calls the inn keeper's general_npc_funcs.time): tells the
-- game's time in two lines, "Yuri <year>, <month><ending> Moon, <day><ending>" and "<weekday> <hour> <a.m./p.m.>". The
-- year, the day of the season (1-91) and the hour are the world clock's (RTK curYear, curDay, curTime); the "Moon" is
-- the real calendar's month, as the script reads os.date("%m"); the weekday is the day of the season mod 7 into Mon to
-- Sun; hours 0-12 are a.m. as they stand, 13-23 p.m. less 12 (so noon is "12 a.m." and midnight "0 a.m.", as the
-- script writes them). Short of `mana`, "Your will is too weak.", for free. Row: mana 5.
function verbs.divine_time(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  local month, day, hour = ctx.realMonth, ctx.gameDay, ctx.gameHour
  local half = "a.m."
  if hour > 12 then half = "p.m."; hour = hour - 12 end
  ctx:say("Yuri " .. ctx.gameYear .. ", " .. month .. rtk_ordinal(month) .. " Moon, " .. day .. rtk_ordinal(day))
  ctx:say(DIVINE_DAY_NAMES[(day % 7) + 1] .. " " .. hour .. " " .. half)
  ctx:narrated()                                                   -- the two lines replace "You cast Divine time."
  return true
end

-- =========================================================================================================
-- MUSE (PC subpath; RTK rtklua/Accepted/Spells/Subpaths/Muse/): Sacred verse, Tranquil symphony and Costume, and Song of
-- the Goddess from RTK's TESTING copy (Subpaths/TESTING/muse_song_of_the_goddess.lua; Muse's own folder has none). The
-- project owner's P5 (each spell as its RTK script) and P3 (on sale at its script's level and items), and for the Song
-- the project owner's 2026-10-30 answer, "Port RTK's testing copy", sold at its row's level with no items. WarMusic (no
-- script) stays as it is, and RTK's Lullaby has no row. Each verb is bound by its SpellParams.csv row, which carries
-- its numbers, and is the shape of its script, in that script's order. The graphic and sound are the spell's
-- spell_effects.csv row, a Utility row, so there is no cast delay. RTK is weight 0 for balance; these numbers are its
-- scripts' by those decisions.
--
-- Where the port parts from the scripts, on purpose, as the subpath verbs above part:
--   * The cast pose is the engine's, on a cast that lands, and so is the central "You cast X." line. Sacred verse's
--     and Tranquil symphony's scripts send both themselves; Costume's sends neither, and the Song's only the pose.
--   * A player the Song reaches reads the engine's one line, "<caster> casts Song of the Goddess on you."; the script
--     says "cast", and sends it to a caster who sings to themselves too. A Song aimed at nothing sings to its caster,
--     the rule every "Which target?" heal here has; RTK's engine runs the script with a nil target, which stops after
--     its mana check (the ghost check reads the missing target).
--   * The Song's run is held by its caster: it ends when either player logs out (RTK saves the duration on the player
--     and resumes it after their relog; a logged-out caster's beat lands nothing in RTK either), and the healed
--     player's buff box does not list it.
--   * "Already in a sanctuary" is the Sanctuary line's one slot being held, so a player under Tranquil symphony is in
--     one. RTK's `sanctuaries` table lists `sacred_symphony`, a name no spell has, and not `tranquil_symphony`, so RTK
--     lets either song land again on a Tranquil symphony holder, and its two halves then sum to a deduction of 0,
--     which RTK's damage code reads as no reduction at all while both run. One slot here cannot hold two.
--   * A player you may attack is one on a PvP map, this server's canPK (RTK also counts a PK flag and a kill list).
-- =========================================================================================================

-- Sacred verse and Tranquil symphony (RTK Subpaths/Muse/sacred_verse.lua and tranquil_symphony.lua, one body but the
-- name and the times): every player on the caster's map, the caster included, who is not already in a sanctuary takes
-- `amount` of every blow (the scripts' deduction - .5) for `durationMax` ms, or `pcDurMs` ms where the caster may attack
-- them; each but the caster reads "You have found Sanctuary." and "<caster> casts <spell> on you.", with the spell's
-- graphic over every one it reaches and its sound at the caster (ctx:mapSanctuary). Short of `mana`, "Your will is too
-- weak.", for free. No wait. Rows: mana 60, amount 0.5, durationMax 120000 / 300000, pcDurMs 60000 / 185000.
function verbs.map_sanctuary(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  local dur = row.durationMax or 0
  if ctx:mapPvp(ctx.map) then dur = row.pcDurMs or dur end         -- the scripts' player:canPK(pcs[i])
  ctx:mapSanctuary(row.amount or 1, dur)                            -- 0 reached is still a cast
  return true
end

-- Costume (RTK Subpaths/Muse/costume.lua; Spy's mimic.lua and Ranger's forest_cover.lua are its body with other
-- numbers, so their rows can bind here): for `mana` the caster takes the look of what stands on the faced tile for
-- `durationMax` ms (ctx:disguise): a creature's form and colour, or a player's gear, face and colours under the caster's
-- own name (a player in a creature's form gives that form). Then the spell's graphic over the caster and the `duration`
-- ms wait. At an NPC there is no look, but the rest, as the script's two branches both miss one. When it ends, at its
-- time, death, @dispel or a won Dispell, the spell's graphic plays over the caster again (its uncast). Short of `mana`,
-- "Your will is too weak.", for free. Facing nothing living, the script takes the mana and then stops with a Lua error
-- (it indexes the nil target), so the mana is gone and nothing else happens: no graphic, no wait, no line, no pose.
-- Row: mana 1600, duration 225000 (the wait), durationMax 150000 (the run).
function verbs.disguise(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  if ctx:disguise(row.durationMax or 0) == "none" then return false end   -- RTK's script errors here, the mana spent
  ctx:fxSelf()
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  return true
end

-- Song of the Goddess (RTK Subpaths/TESTING/muse_song_of_the_goddess.lua, ported as written): its "Which target?" heal
-- target, a player or a creature, gets a `durationMax` ms run (24 s) whose beat (heal_run_tick) heals it, one beat for
-- each 1,000 ms of the run, as RTK's duration timers take 1,000 off a beat: every second on a player (24 heals), every
-- 1.5 s on a creature (RTK's creature timer, so 24 heals over 36 s). The mana and the heal go by the caster's rank, the
-- script's two if-chains, which name ranks 1 and 2 only: `mana` and `amount` (1,000 and 5,000) at any other rank. At
-- an NPC it casts nothing, silently and for free (RTK's engine runs no script for one). Short of the mana, "Your will
-- is too weak.", for free; at a ghost, "You need a different type of cure.", for free. The cast plays the spell's sound
-- over the target (its playSound(4)); each beat its graphic and sound (sendAnimation(66), playSound(4)). The heal
-- reads the caster's rank on each beat, as the script's while_cast does. A recast on the same target takes the run's
-- length again from now. Row: mana 1000, amount 5000, durationMax 24000.
local SONG_BY_MARK = {
  [1] = { mana = 3000, heal = 10000 },   -- the script's player.mark == 1
  [2] = { mana = 6000, heal = 20000 },   -- the script's player.mark == 2
}

function verbs.heal_run(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.healReach < 0 then return false end                        -- an NPC: silent, free
  local byMark = SONG_BY_MARK[ctx.mark] or {}
  local mana = byMark.mana or row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  if ctx.healIsGhost then ctx:say("You need a different type of cure."); return false end
  if not ctx:startHealRun("heal_run_tick", row.durationMax or 0) then return false end
  ctx:debitMana(mana)
  return true
end

-- The Song's beat (the script's while_cast), on the player or creature it holds.
function verbs.heal_run_tick(ctx, row)
  local byMark = SONG_BY_MARK[ctx.mark] or {}
  ctx:healRunVictim(byMark.heal or row.amount or 0)
  return true
end

-- =========================================================================================================
-- DRUID (PC subpath; RTK rtklua/Accepted/Spells/Subpaths/Druid/). The project owner, 2026-10-26 (P5) and again on
-- 2026-10-30: each spell as its RTK script, its smaller calls as RTK's. So Sea breeze heals creatures too, Energy bloom
-- keeps its rank mark, and Steed's stag is ridden as RTK's onMount rides one (Session.TryRideHorse). The verbs here are
-- Control flames and Nature's bloom (a cast that opens a window, and a drop hook while it runs), Druid's rodent, Steed
-- and Sea breeze. Lightning storm, Earth strike and Nature's smite are storm_strike and ranged_strike above (#390), and
-- Energy bloom binds ranged_strike too: its script is Earth strike's body under its own name. Efflorescence has no RTK
-- script and stays as it was. Each verb is bound by its SpellParams.csv row, which carries its numbers, and is the shape
-- of its script, in that script's order. The graphic and sound are the spell's spell_effects.csv row. RTK is weight 0
-- for balance; these numbers are its scripts' by those decisions.
--
-- THE DROP HOOK (RTK clif_parsedropitem, which runs the on_drop_while_aether of every spell whose aether still runs
-- before the item leaves the bag). While a spell's own wait runs, an item its caster drops is first handed to the verb
-- named after the spell row's verb with "_drop" on the end, with ctx.dropped the item's key. Returning true takes the
-- drop over (RTK's fakeDrop = 1): nothing lands but what the verb puts down. Returning false lets the item drop as it
-- always does. See Session.RunDropHooks.
--
-- Where the port parts from the scripts, on purpose, as the subpath verbs above part:
--   * Each wait is checked first, with this file's own line, and armed under the spell's own key (subpath_waiting).
--     RTK waives Steed's wait for a GM; no verb here waives anything for staff.
--   * The cast pose is the engine's, on a cast that lands, and so is the central "You cast X." line, said where the
--     scripts say nothing (Control flames, Druid's rodent, Steed, Nature's bloom).
--   * A wait is the session's, as every wait here is, so a relog ends it and the drop window with it (RTK keeps an
--     aether across a relog).
-- =========================================================================================================

-- Control flames and Nature's bloom open their window the same way (RTK Subpaths/Druid/control_flames.lua and
-- natures_bloom.lua, one cast): for `mana`, the spell's `duration` ms wait, inside which its caster's drops go to its
-- hook. Short of mana, "Your will is too weak.", for free. Nothing else happens at the cast.
local function open_window(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  return true
end

-- Control flames (RTK Subpaths/Druid/control_flames.lua). Inside its window an acorn dropped is spent, one of it, and
-- nothing lands. On a `chance`% roll (the script's math.random(1, 4) == 1), with `flat` mana there, every creature on
-- the caster's four sides takes `amount`, netted against its armour, with the spell's graphic over it, and when any
-- stood there the spell's sound plays, the `flat` mana goes and the script's own line is said (ctx:strikeAround). Short
-- of `flat` on that roll, "Your will is too weak." and nothing struck. Any other item drops as always. With no acorn
-- left to spend, or the caster going away (an exchange or a kick in the Lua gate's gap, Session.LuaTakeDropped),
-- nothing happens and nothing lands. Row: mana 1000, duration 62000 (the window), amount 1200, chance 25, flat 800
-- (the drop's own mana).
local FLAME_ITEM = "acorn"                      -- control_flames.lua: item.yname == "acorn"
local FLAME_LINE = "You cast Wall of flames."   -- its line when the flames rise

function verbs.flame_window(ctx, row)
  return open_window(ctx, row)
end

function verbs.flame_window_drop(ctx, row)
  if ctx.dropped ~= FLAME_ITEM then return false end
  if not ctx:takeDropped() then return true end              -- nothing to spend: nothing happens
  if not ctx:roll(row.chance or 0) then return true end
  local mana = row.flat or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return true end
  if ctx:strikeAround(row.amount or 0) > 0 then
    ctx:debitMana(mana)
    ctx:say(FLAME_LINE)
  end
  return true
end

-- Druid's rodent (RTK Subpaths/Druid/druids_rodent.lua). In any creature's form already, the cast ends that form and
-- does nothing else, silently and for free (the script's checkIfCast(morphs) and removeDuras(morphs)). Short of `mana`,
-- "Your will is too weak.". Answered "rabbit" or "rat", the caster takes that look from the spell's Morphs.csv row
-- (Rabbit 21, Rat 91) for its 155 s, with the spell's graphic and sound; any other answer does nothing, silently. Nothing
-- is spent: the script checks its 30 and never takes it. Row: mana 30.
function verbs.rodent_morph(ctx, row)
  if ctx.morphed then ctx:endMorph(); return false end
  if ctx.mp < (row.mana or 0) then ctx:say("Your will is too weak."); return false end
  return ctx:morphTo(ctx.answer)
end

-- Steed (RTK Subpaths/Druid/steed.lua): Mount's spawn with the creature `category` names (Arawn's stag), on the faced
-- tile when RTK's test finds it free, else on the caster's own tile; nobody owns it, and the ride key rides it as RTK's
-- onMount rides a stag. Short of `mana`, "Your will is too weak.", for free; with it nothing is spent, as the script
-- checks its 2000 and never takes it. The wait is armed before the spawn, as the script arms it. Row: mana 2000,
-- category arawns_stag, duration 110000.
function verbs.spawn_steed(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  if ctx.mp < (row.mana or 0) then ctx:say("Your will is too weak."); return false end
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:spawnFaced(row.category or "")
  return true
end

-- Sea breeze (RTK Subpaths/Druid/sea_breeze.lua): for `mana`, with a `duration` ms wait, "You cast Sea breeze.", then
-- every player and every creature within SUBPATH_REACH tiles of the caster on both axes, the caster among them and an
-- NPC not, gets the spell's graphic and `amount` vita (a ghost the graphic and no vita), a player reading "<caster> cast
-- Sea breeze on you." first (the script's line, the caster too), and the spell's sound plays at the caster
-- (ctx:healRange). No PK test: a heal lands on anyone. Short of mana, "Your will is too weak.", for free. Row: mana
-- 1500, amount 600, duration 75000.
function verbs.breeze_heal(ctx, row)
  if subpath_waiting(ctx, row) then return false end
  local mana = row.mana or 0
  if ctx.mp < mana then ctx:say("Your will is too weak."); return false end
  ctx:debitMana(mana)
  if (row.duration or 0) > 0 then ctx:setCooldown(ctx.spellKey, row.duration) end
  ctx:say("You cast " .. ctx.spellName .. ".")
  ctx:healRange(row.amount or 0, SUBPATH_REACH, ctx.name .. " cast " .. ctx.spellName .. " on you.")
  ctx:narrated()                                                   -- the script's line comes first, then the sweep's
  return true
end

-- Nature's bloom (RTK Subpaths/Druid/natures_bloom.lua). Inside its window a flower dropped, one of the script's
-- sixteen, is spent, one of it, and nothing lands but what the spell scatters: one of that flower on the caster's tile,
-- then over every tile within `amount` of them on both axes one more on a `chance`% roll, where the tile carries no map
-- object, is passable and is no warp (ctx:scatter, with the script's clamp at the map's edge). No line, no graphic. Any
-- other item drops as always. With no flower left to spend, or the caster going away (Session.LuaTakeDropped), nothing
-- is scattered and nothing lands. Row: mana 200, duration 625000 (the window), amount 4, chance 50.
local BLOOM_FLOWERS = {
  daffodil = true, dawn_tulip = true, dusk_blossom = true, fallen_star = true, golden_sunset = true,
  greater_tulip = true, heavens_bell = true, lemon_flower = true, mango_floret = true, midnights_reign = true,
  old_poppy = true, orange_blossom = true, pink_mugunghwa = true, rose = true, sunflower = true, winedrop = true,
}

function verbs.bloom_window(ctx, row)
  return open_window(ctx, row)
end

function verbs.bloom_window_drop(ctx, row)
  local flower = ctx.dropped
  if not BLOOM_FLOWERS[flower] then return false end
  if not ctx:takeDropped() then return true end              -- nothing to spend: nothing scattered
  ctx:scatter(flower, row.amount or 0, row.chance or 0)
  return true
end
