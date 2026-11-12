-- Item use-effect verbs: the LOGIC half of the data-driven consumable system (the "verb"; ItemParams.csv is
-- the "row"). Each verb is a function(ctx, row):
--   ctx = the ItemContext facade -> engine primitives (ctx:heal / ctx:restoreMana / ctx:setStatus / ...) plus
--         read-only eater stats (ctx.maxHp, ctx.armor, ...).
--   row = this item's ItemParams.csv row, numbers already parsed (row.amount, row.duration, ...); empty CSV
--         cells are nil, so `row.amount or 0` gives a safe default.
-- Bind a verb to an item by adding an ItemParams.csv row whose `verb` column names it.
-- A verb may `return false` to REFUSE the use (a gate, e.g. a ward already active): the item is NOT consumed.
-- Returning nothing (or true) consumes the item as normal.
-- Edit THIS file or the CSV and run !reload to see changes live -- no server restart.

verbs = {}

-- Eat food or drink a heal potion: restore HP by a flat `amount`, or to full when `full` is set. Plays the
-- shared eat animation first (RTK's own scripts sendAction before applying the heal).
function verbs.heal(ctx, row)
  ctx:animate()
  if row.full then ctx:heal(ctx.maxHp) else ctx:heal(row.amount or 0) end
end

-- poison_apple: RTK removeHealthExtend(999999999) -- an always-lethal joke item.
function verbs.fatal(ctx, row)
  ctx:animate()
  ctx:kill()
end

-- Drinks + smoke: restore mana by `amount` at a small HP cost (`hpcost`) -- RTK's mana-for-HP trade.
-- Uses sipAnim, not animate: RTK gives this whole class its own gesture (sendAction(7, 20)) and its own
-- sound, where food is sendAction(8, 25) and the eat noise. Sharing the eat animation made a pipe sound
-- like a mouthful of food. sipAnim is silent for now -- see Session.ItemSipAnim on why the sound id isn't
-- ported straight from RTK.
function verbs.drink(ctx, row)
  ctx:sipAnim()
  ctx:restoreMana(row.amount or 0)
  if row.hpcost then ctx:loseHp(row.hpcost) end
end

-- Timed ward potion/scroll, in two flavours picked by whether the row names a `category`.
--
-- WITH a category (sanctuary / harden_armor / curse_protection): the effect already exists on the spell side,
-- so apply it into the SAME slot the spell uses. The stat really lands, the two share RTK's checkIfCast
-- exclusivity (you cannot stack a potion Sanctuary on a cast one), and it persists across a relog for free.
-- WITHOUT one (chin_baek_ho_ryung / purple_potion / harden_body): a genuinely flag-shaped ward the engine
-- reads directly — a warrior strike multiplier, a regen bonus, damage immunity. Those set the plain flag.
--
-- Either way a live ward + an `activemsg` refuses WITHOUT consuming (RTK's own early return); a row with no
-- activemsg has no guard and simply re-applies. RTK's potion scripts all sendAction(8, 25) before the effect
-- — purple_potion is the lone exception and gets the pose here anyway, so drinking always looks like drinking.
function verbs.ward(ctx, row)
  local blocked = row.category and ctx:wardBlocked(row.category) or ctx:hasStatus(row.statuskey)
  if row.activemsg and blocked then
    ctx:say(row.activemsg)
    return false
  end
  ctx:animate()
  if row.category then
    ctx:applyWard(row.category, row.stat or "", row.amount or 0, row.duration or 0,
                  row.statuskey, row.wardname or row.statuskey)
  else
    ctx:setStatus(row.statuskey, row.duration or 0)
  end
  return true
end

-- scroll_of_immortality: an armor-scaled success roll before granting the harden-body ward (RTK's own
-- ceil((120 + clampedArmor) / 2)% chance). Refuses (no consume) under any of RTK's hardBodies, its own ward among them
-- (RTK Items/Consumables/scrolls/scroll_of_immortality.lua:7-10: checkIfCast(hardBodies), "You already cast that
-- spell."; the list is Spells/spellTables.lua:252-260: Tree bark, Invincibility, the Harden Body wards), or on a failed
-- roll.
local HARD_BODIES = { "tree_bark", "invincibility", "harden_body", "harden_body_poet", "deaths_guard_poet",
                      "lifes_protection_poet", "body_of_alignment_poet" }

function verbs.hardenbody(ctx, row)
  if ctx:hasStatus(row.statuskey) then ctx:say(row.activemsg); return false end
  for _, ward in ipairs(HARD_BODIES) do
    if ctx:hasStatus(ward) then ctx:say(row.activemsg); return false end
  end
  local rate = math.ceil((120 + ctx.armor) / 2)
  if not ctx:chance(rate) then ctx:say("Something went wrong."); return false end
  ctx:setStatus(row.statuskey, row.duration or 0)
  ctx:castPose()
  ctx:say("You cast Harden Body.")
  return true
end

-- indigo_potion / clear_water_song: no player poison/curse model exists yet to clear -- a faithful no-op consume.
function verbs.cure(ctx, row)
  return true
end

-- yellow_scroll: warp to a random tavern in your nation (RTK returnFunc -> returnToInn). qui_hyang does the same
-- for anyone outside the PC subpaths (quihyang, below).
function verbs.warphome(ctx, row)
  ctx:warpHome()
  return true
end

-- qui_hyang (RTK qui_hyang.lua): a member of one of the twelve PC subpaths is asked "Where do you wish to go?",
-- RTK's "Subpath Circle" (a walkable tile of their subpath's Circle) or "Main Inn" (the tavern warphome picks).
-- The answer spends the scroll and warps; closing the box spends nothing, so the use itself declines here.
-- For everyone else it does what warphome does, unchanged: no menu, a tavern, one scroll spent.
function verbs.quihyang(ctx, row)
  if ctx:askSubpathCircle() then return false end
  ctx:warpHome()
  return true
end
