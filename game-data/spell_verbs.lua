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
-- so the cast animation is suppressed for those and those only.
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

  -- holdTarget owns the boss cap, the PvP gate on the Doze family, and the "It doesn't work." for everything
  -- else aimed at a player. It returns false without spending anything.
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

-- Direct magic damage to the current target: base + Will*coeff, costing `mana`.
-- (Target resolution, deflect, the HP-bar packet, death + XP are all handled inside ctx:damage.)
function verbs.magic_damage(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return false end   -- declined (no mana) -> no "You cast X."
  ctx:damage((row.base or 0) + ctx.will * (row.coeff or 1.0))
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
  if not ctx.hasTarget then return false end          -- silent
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
function verbs.mana_steal(ctx, row)
  if not ctx:pcTarget() then return false end
  if ctx.targetIsDead then ctx:say("That cannot save them now."); return false end
  if not ctx.targetInGroup then ctx:say("They must be in your group."); return false end
  ctx:setMana(ctx.mp + ctx.targetMana)     -- setMana clamps to the caster's max
  ctx:setTargetMana(0)
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
-- It keeps three rules Caleb set on 2026-10-10 for a cast at another player, on the source survey
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
-- Take-hold chance per curse category. Like HOLD_CHANCE above these are OURS: RTK's curse scripts have no
-- failure roll at all, so a curse always stuck. A per-spell `chance` column in SpellParams.csv overrides.
-- Note what this interacts with: because a landed curse OCCUPIES its category slot and blocks every other
-- curse until it lapses, a miss is the only way a second attempt is even possible -- so the rate is really
-- "how often does the first cast get to decide the slot", not a damage-style dps knob.
local CURSE_CHANCE = { curses = 75, minorcurses = 85, disheartens = 80 }

function verbs.curse(ctx, row)
  local mana = row.mana or 0
  if not ctx:enoughMana(mana) then return false end
  if not ctx:canCurse() then return false end                                    -- PvP-legal PC (incl self) or a mob
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
-- Neutrals out; Caleb chose to allow them (#325, 2026-10-06). His ruling, not a sourced fact (same_group_and_kingdom).
-- RTK agrees on the group, the 30 mana and "Fizzle.", and adds the map checks below. Its ORDER is kept: it decides
-- which line a player reads when more than one applies ("That does not work here." comes before the later fizzles).
-- The cost, animation and sound are the spell_effects.csv rows (ctx.spellMana, ctx:fxSelf()).
-- The "area that allows approaching" is Maps.csv MapCanSummon (RTK map[m].summon, which its scripts read as
-- canSummon), through ctx:mapCanSummon: 0 is "Fizzle." at no cost. Which map counts is Caleb's ruling
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
-- person, else that person's own tile. RTK searches in facing order instead; Caleb kept this one at #325's merge
-- (2026-10-06).
-- Neither verb tells the moved person anything: RTK's scripts send them no line. One exception, Summon's: a person
-- summoned mid-exchange has the exchange cancelled, and both traders see the client's "Exchange cancelled." box,
-- as for any move (#57). Caleb kept this at #325's merge (2026-10-06), rather than have Summon fizzle on a trader.

local function travel_fizzle(ctx) ctx:say("Fizzle."); return false end

-- The two people's own standing, checked last, where RTK checks the group. The kingdom half is "the same
-- nation", Neutral (0) included. Read literally, "citizenship in the same kingdom" excludes two Neutrals, since a
-- Neutral holds no citizenship; Caleb chose to allow them (2026-10-06). A Neutral and a citizen of a kingdom, or
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
  local there = ctx.targetMap                                      -- where the caster would land
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
function verbs.propose(ctx, row)
  if ctx:hasLegend("engaged") or ctx:hasLegend("married") then
    ctx:forgetSpell()
    ctx:say("You are already committed to someone else!")
    return false
  end
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
function verbs.sacrifice(ctx, row)
  local fam = ctx.sacrificeFamily
  local mana, aether
  if     fam == "LethalStrike"    then mana, aether = 120, 23000
  elseif fam == "DesperateAttack" then mana, aether = 60, 11000
  elseif fam == "Berserk"         then mana, aether = 60, 12000
  elseif fam == "Whirlwind"       then mana, aether = 120, (ctx.alignment == 0 and 30000 or 25000)
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

function verbs.chungryong_rage(ctx, row)
  -- Climb from the live tier; if the fury has already lapsed (or never ran) start fresh at 1.
  local base = ctx.rageActive and ctx.crRageTier or 0
  local tier = math.min(base + 1, #CHUNG_RYONG_RAGE)
  local t    = CHUNG_RYONG_RAGE[tier]

  if not ctx:enoughMana(t.mana) then return false end     -- sends its own "not enough mana" line
  ctx:debitMana(t.mana)
  -- durMs 0 on a climb: keep the run's existing deadline (RTK never re-arms it). Only a fresh cast starts one.
  ctx:setCrRage(tier, t.mult, t.ac, base > 0 and 0 or (row.duration or CR_RAGE_DURATION_MS))
  ctx:setCooldown(ctx.spellKey, ctx.spellAether > 0 and ctx.spellAether or 120000)
  ctx:fxSelf()
  ctx:say("[Rage " .. tier .. "] " .. t.msg)
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
    -- (unaimed in a PvP map resolves to you) before the damage path ever saw it.
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
