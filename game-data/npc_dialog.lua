-- Data-driven NPC dialog (the async cousin of spell_verbs.lua / item_verbs.lua). Each migrated NPC is a
-- COROUTINE: npcs.<Identifier> = function(ctx) ... end. The ctx methods below are thin `coroutine.yield`s —
-- the C# driver (Server/NpcScript.cs) awaits the matching NpcContext primitive and resumes the coroutine with
-- the reply, so a script reads as linear code even though every prompt suspends on the player's network reply:
--   local c = ctx:menu("Pick one", {"A","B"});  if c == 1 then ... end
--   local name = ctx:input("Your name?");        if name then ... end
-- Suspending ops (wait for the player): say / sayItem / sayLook / menu (returns 1-based pick, 0 = cancel) /
--   input (returns the typed string, or nil if cancelled).
-- Immediate ops (no wait): giveItem/takeItem/hasItem/countItem/itemName, learnSpell/hasSpell/forgetSpell,
--   awardExp/awardGold, stage/setStage, reg/setReg, hasLegend/addLegend/removeLegend, warp,
--   level/maxHp/maxMp/sex/nation/setNation/map/coins/spendGold, classId/basePathId/canLearnDogSpells,
--   npcName, killCount/totalKills/mounted, eraHas, bubble/notify, gameDate, now,
--   karma/karmaLevel/karmaCheck/addKarma/removeKarma/karmaTooLow.
-- To expose a new primitive: add a stub here AND a case in Server/NpcScript.cs Dispatch. Edit this file and run
-- !reload to see changes live -- no server restart. Any NPC with a script here takes precedence over its C#
-- ability; an NPC with no entry (or a broken file) uses the C# abilities unchanged.

function __make_ctx()
  local ctx = {}
  -- suspending (await the player's reply)
  function ctx:say(...)                 return coroutine.yield({op="say", pages={...}}) end
  function ctx:sayItem(item, ...)       return coroutine.yield({op="sayItem", item=item, pages={...}}) end
  function ctx:sayLook(look, color, ...) return coroutine.yield({op="sayLook", look=look, color=color, pages={...}}) end
  function ctx:menu(prompt, options)    return coroutine.yield({op="menu", prompt=prompt, options=options}) end
  function ctx:input(prompt)            return coroutine.yield({op="input", prompt=prompt}) end
  function ctx:buy()                    return coroutine.yield({op="buy"}) end
  function ctx:sell()                   return coroutine.yield({op="sell"}) end
  -- immediate
  function ctx:bubble(text)             return coroutine.yield({op="bubble", text=text}) end
  function ctx:notify(text)             return coroutine.yield({op="notify", text=text}) end
  function ctx:giveItem(key, n)         return coroutine.yield({op="giveItem", key=key, n=n}) end
  function ctx:takeItem(key, n)         return coroutine.yield({op="takeItem", key=key, n=n}) end
  function ctx:hasItem(key, n)          return coroutine.yield({op="hasItem", key=key, n=n}) end
  function ctx:countItem(key)           return coroutine.yield({op="countItem", key=key}) end
  function ctx:itemName(key)            return coroutine.yield({op="itemName", key=key}) end
  function ctx:learnSpell(key)          return coroutine.yield({op="learnSpell", key=key}) end
  function ctx:hasSpell(key)            return coroutine.yield({op="hasSpell", key=key}) end
  function ctx:forgetSpell(key)         return coroutine.yield({op="forgetSpell", key=key}) end
  -- The shared "Forget Secret" picker (C# ForgetSecretAbility.Forget) -- the same flow the class
  -- trainers show, so a scripted NPC can offer it without restating the list-and-confirm dialog.
  function ctx:forgetSecret()           return coroutine.yield({op="forgetSecret"}) end
  function ctx:eraHas(feature)          return coroutine.yield({op="eraHas", feature=feature}) end
  -- `totem` opts into the +5% totem-time bonus; quest rewards normally don't take it. See NpcScript.cs.
  function ctx:awardExp(n, totem)       return coroutine.yield({op="awardExp", n=n, totem=totem}) end
  function ctx:awardGold(n)             return coroutine.yield({op="awardGold", n=n}) end
  function ctx:stage(key)               return coroutine.yield({op="stage", key=key}) end
  function ctx:setStage(key, n)         return coroutine.yield({op="setStage", key=key, n=n}) end
  function ctx:reg(key)                 return coroutine.yield({op="reg", key=key}) end
  function ctx:setReg(key, n)           return coroutine.yield({op="setReg", key=key, n=n}) end
  -- karma (Server/Karma.cs). addKarma/removeKarma accept FRACTIONS (0.1, 0.25); karmaCheck takes a tier
  -- NAME -- "cat"/"squirrel"/"rabbit"/"dog"/"monkey"/"ox"/"bear"/"tiger"/"dragon"/"spirit"/"angel's tear"/
  -- "angel", plus "rat"/"snake" which read as "is this player that bad" rather than as a minimum.
  function ctx:karma()                  return coroutine.yield({op="karma"}) end
  function ctx:karmaLevel()             return coroutine.yield({op="karmaLevel"}) end
  function ctx:karmaCheck(tier)         return coroutine.yield({op="karmaCheck", tier=tier}) end
  function ctx:addKarma(n)              return coroutine.yield({op="addKarma", n=n}) end
  function ctx:removeKarma(n)           return coroutine.yield({op="removeKarma", n=n}) end
  function ctx:karmaTooLow()            return coroutine.yield({op="karmaTooLow"}) end
  function ctx:hasLegend(name)          return coroutine.yield({op="hasLegend", name=name}) end
  function ctx:addLegend(text, name, icon, color) return coroutine.yield({op="addLegend", text=text, name=name, icon=icon, color=color}) end
  function ctx:removeLegend(name)       return coroutine.yield({op="removeLegend", name=name}) end
  function ctx:warp(map, x, y)          return coroutine.yield({op="warp", map=map, x=x, y=y}) end
  function ctx:level()                  return coroutine.yield({op="level"}) end
  function ctx:maxHp()                  return coroutine.yield({op="maxHp"}) end
  function ctx:maxMp()                  return coroutine.yield({op="maxMp"}) end
  -- class identity: classId is the exact path (Ju jak = 8), basePathId the base four it descends from
  -- (Ju jak -> 3 Mage). canLearnDogSpells is the PC-subpath exclusion, owned by C# so scripts can't drift.
  function ctx:classId()                return coroutine.yield({op="classId"}) end
  function ctx:basePathId()             return coroutine.yield({op="basePathId"}) end
  function ctx:canLearnDogSpells()      return coroutine.yield({op="canLearnDogSpells"}) end
  -- this NPC's display name -- needed when several NPCs share one identifier (the four Dogs).
  function ctx:npcName()                return coroutine.yield({op="npcName"}) end
  function ctx:killCount(key)           return coroutine.yield({op="killCount", key=key}) end
  function ctx:totalKills()             return coroutine.yield({op="totalKills"}) end
  function ctx:mounted()                return coroutine.yield({op="mounted"}) end
  function ctx:sex()                    return coroutine.yield({op="sex"}) end
  function ctx:nation()                 return coroutine.yield({op="nation"}) end
  function ctx:setNation(n)             return coroutine.yield({op="setNation", n=n}) end
  function ctx:map()                    return coroutine.yield({op="map"}) end
  function ctx:coins()                  return coroutine.yield({op="coins"}) end
  function ctx:spendGold(n)             return coroutine.yield({op="spendGold", n=n}) end
  function ctx:gameDate()               return coroutine.yield({op="gameDate"}) end
  -- Wall-clock unix SECONDS. For a cooldown that must survive a relog, store `ctx:now() + seconds` in a
  -- registry key and compare against `ctx:now()` on the next visit -- an absolute deadline, never a
  -- countdown (the same rule the C# timed effects follow). See the Sage's SAGE_TIMER.
  function ctx:now()                    return coroutine.yield({op="now"}) end
  -- Inclusive random integer in [lo, hi], backed by the server RNG (MoonSharp's own math.random is
  -- unseeded and so repeats the same sequence every server start -- see Myung-Suck's grumpy gate).
  function ctx:rand(lo, hi)             return coroutine.yield({op="rand", lo=lo, hi=hi}) end
  return ctx
end

npcs = {}

-- RTK: Koguryo (country 1) home = map 36 (7,6); otherwise Buya = map 351 (8,8).
local function warp_home(ctx)
  if ctx:nation() == 1 then ctx:warp(36, 7, 6) else ctx:warp(351, 8, 8) end
end

-- =====================================================================================================
-- WHERE YOU LIVE. Two separate things, both ported from RTK:
--
--   * your NATION (RTK player.country) -- Neutral/wilderness 0, Kugnae 1, Buya 2, Nagnang 3. Changed by
--     the town criers ("Move to <kingdom>") and by Rotah in the wilderness ("Become Neutral"), both of
--     which call move_to_country below (RTK general_npc_funcs.moveToCountry).
--   * a bound HOME in an outlying town (RTK registry["home"]) -- a room in a mayor's tavern that OVERRIDES
--     your nation's taverns as the Return destination. Sanhae's mayor binds 10, Hausson's binds 11.
--
-- Both feed Session.HomeGroup -> game-data/Inns.csv, which is what Return / yellow scroll / qui hyang
-- actually read. Moving kingdom clears a bound home (the room is in the country you just left) -- that is
-- done C#-side in Session.SetNation, so every caller gets it, not just these scripts.
local HOME_REG, HOME_NONE = "home", 0

-- RTK general_npc_funcs.moveToCountry, verbatim in its rules:
--   * level 20 minimum ("still too new to these lands"),
--   * you may only join a kingdom FROM neutral -- kingdom-to-kingdom is refused, so leaving via Rotah is a
--     deliberate, unskippable step with its own warning about what you give up,
--   * joining a kingdom costs 20 gold acorns as tribute; going neutral is free,
--   * every path clears the bound home.
--
-- NOT modelled, because this server has neither: RTK's "you will leave your clan" (clans) and its
-- registry["home"] == 2 subpath hall. The warning line still says it -- it is the original's wording, and
-- what it describes will be true again if clans ever land.
local COUNTRY_NAMES = {[0] = "the wilderness", [1] = "Kugnae", [2] = "Buya", [3] = "Nagnang"}
local MOVE_TRIBUTE_ITEM, MOVE_TRIBUTE_QTY = "gold_acorn", 20
local MOVE_MIN_LEVEL = 20

local function move_to_country(ctx, country)
  if ctx:level() < MOVE_MIN_LEVEL then
    ctx:say("Hello! You are still too new to these lands to consider moving to another kingdom. Perhaps when you are ready.")
    return
  end

  local here = ctx:nation()

  if here ~= 0 and country ~= 0 then
    ctx:say("I cannot allow you to move here while you pledge your loyalty to another kingdom. Only someone who is neutral can join a kingdom.")
    return
  end

  if country == 0 then
    if here == 0 then
      ctx:say("Ah, the free life. Isn't it great?")
      return
    end

    ctx:say(
      "Welcome there city dweller. Isn't it wonderful out here?",
      "Would you like to leave the city behind, and become a member of the wilderness?",
      "Doing so means you will leave all that you have behind, your clan, your loyalties, your home, and your companions.")

    if ctx:menu("Are you still interested in becoming Neutral?",
                {"No, I'd prefer not to.", "Yes, please."}) == 2 then
      ctx:setNation(0)
      ctx:say("Welcome to the wilderness.")
    end
    return
  end

  if here == country then
    -- RTK writes this line out per kingdom; the only difference is the demonym.
    local kin = {[1] = "fellow Koguryian", [2] = "fellow Buyan", [3] = "fellow Nagnang citizen"}
    ctx:say("Greetings, " .. (kin[country] or "friend") .. ".")
    return
  end

  local name = COUNTRY_NAMES[country] or "our city"
  if ctx:menu("Would you like to become a citizen of our lovely city, " .. name .. "?",
              {"No, thank you.", "Yes, very much."}) ~= 2 then
    return
  end

  if not ctx:hasItem(MOVE_TRIBUTE_ITEM, MOVE_TRIBUTE_QTY) then
    ctx:say(name .. " requests " .. MOVE_TRIBUTE_QTY .. " gold acorns as tribute to move, come back when you have that.")
    return
  end

  ctx:takeItem(MOVE_TRIBUTE_ITEM, MOVE_TRIBUTE_QTY)
  ctx:setNation(country)
  ctx:say("Welcome to " .. name .. ".")
end

-- The mayors' "Live in <town>" option (RTK sanhae_mayor.lua / hausson_mayor.lua, which are the same script
-- with two ids). Binding is a toggle: talk to him again to give the room up.
--
-- `nation` is the kingdom the town belongs to -- RTK gates both taverns on it ("only people who are Buyan
-- may live in this town" / "...who are Koguryan..."), so an outlying home is a perk of citizenship rather
-- than a free room for anyone passing through. Pass nil for a town with no such gate.
local DEMONYMS = {[1] = "Koguryan", [2] = "Buyan", [3] = "Nagnang"}

local function live_in_town(ctx, home_id, nation)
  if nation and ctx:nation() ~= nation then
    ctx:say("Greetings, I would love to let you live here, but only people who are " ..
            (DEMONYMS[nation] or "of this kingdom") .. " may live in this town.")
    return
  end

  if ctx:reg(HOME_REG) == home_id then
    ctx:say("You already live in my towns tavern... do you wish to leave already?")
    if ctx:menu("Do you wish to leave?", {"Yes, I do.", "No, I wish to stay."}) == 1 then
      ctx:setReg(HOME_REG, HOME_NONE)
      ctx:say("Well, nothing lasts forever. Good luck in the future.")
    else
      ctx:say("Ah, that is good to hear. I hope you like my service here.")
    end
    return
  end

  ctx:say("So you wish to live in my humble tavern, eh? Well, I can spare you some room. But remember, you will always return here, and not the taverns in the city if you do this.")

  if ctx:menu("Are you sure you want to do this?", {"Yes, I wish to.", "No, I do not."}) == 1 then
    ctx:setReg(HOME_REG, home_id)
    ctx:say("Welcome to my tavern, I hope you enjoy your time here.")
  else
    ctx:say("That is your choice, plenty of room if you wish to come back later.")
  end
end

-- Rotah, the old man in the Wilderness clearing (RTK NPCs/wilderness/rotah.lua) -- the ONLY way out of a
-- kingdom, and so the hinge of the whole system: kingdom-to-kingdom moves are refused, so anyone changing
-- allegiance passes through him and hears what they are giving up.
--
-- His clearing is also where neutrals wake up (Inns.csv "Wilderness", the 4x4 box he stands beside), which
-- is RTK's own joke -- the wilderness has no tavern, so the wilderness IS the tavern.
--
-- Only "Become Neutral" is ported of his eleven options. The rest are systems this server doesn't have
-- (Waypoint transport, clan rings and tribes, shadow stats, mass exchange, world shout, broadcast event,
-- the Forgotten Past orb quest) or duplicates of services he already offers through his shop/repair/bank
-- flags in NPCs.csv.
function npcs.RotahNpc(ctx)
  move_to_country(ctx, 0)
end

-- The town criers (RTK NPCs/Common/town_crier.lua): Yeoni in Kugnae, Honi in Buya, the Palace Concierge in
-- Nagnang. RTK picks the kingdom from `npc.mapTitle`, i.e. from WHERE THE CRIER STANDS -- each one recruits
-- for their own city and nothing else -- so this keys off the map the conversation happens on. A crier
-- placed on any other map has nothing to offer and says so.
--
-- Not ported, same reasoning as Rotah: Broadcast Event, Wisdom clothes, and the three
-- <Kingdom> Honor/Defender titles (all of which are "Prince Mhul must first bestow this on you" stubs in
-- RTK anyway).
local CRIER_COUNTRY = {[0] = 1, [330] = 2, [2500] = 3}   -- Kugnae / Buya / Nagnang city maps

function npcs.TownCrierNpc(ctx)
  local country = CRIER_COUNTRY[ctx:map()]
  if not country then
    ctx:say("Hear ye, hear ye! ... but not here, I am afraid. Seek me in the city itself.")
    return
  end
  move_to_country(ctx, country)
end

-- Chu Rua, the Dragon King's turtle (RTK tutorial/chu_rua.lua). Tutorial stage 7: he asks for a young_ginseng
-- (a scripted-tile pickup on Guol Tiger Pass, map 1116); bring it and he grants the aided_chu_rua legend + a
-- sea ring + experience. The Lost-Legend "mermaid song" branch isn't ported.
--
-- NO WARP HOME, deliberately, against RTK. RTK ends both the turn-in and every later click with "I will
-- return you home now." + warp(36,7,6)/warp(351,8,8). The era says otherwise: the tutor sweeps you TO the
-- shore and that is the only free ride -- you walk back. Neither the Jan-2001 tswolf walkthrough (its four
-- turn-in screens end on the ring line) nor nexusatlas shows that dialog or mentions being returned, and the
-- atlas tells you outright to "go back to your tutor and click him" afterwards. The false "I will return you
-- home now." clause goes with the warp; the "Thank you again for your help!" greeting it was bolted onto is
-- kept as-is.
function npcs.ChuRuaNpc(ctx)
  if ctx:hasLegend("aided_chu_rua") then
    ctx:say("Thank you again for your help!")
    return
  end

  if ctx:hasItem("young_ginseng", 1) then
    -- Turn-in, screen for screen as tswolf captured it in Jan 2001 (complete1-4). Two divergences from RTK,
    -- both from those screenshots: the "Returned safe, I hope." greeting opens it (RTK drops the line), and
    -- "Ginseng. What an odd looking root." is shown against the GINSENG icon, not Chu Rua's own portrait.
    ctx:say("Returned safe, I hope.")
    ctx:sayItem("young_ginseng", "Ginseng. What an odd looking root.")
    ctx:say("The Dragon king shall live. Bless you, kind one.")
    ctx:awardExp(ctx:stage("tutorial_quest") == 7 and 600 or 400)   -- RTK: 400, +200 on the tutorial
    ctx:addKarma(1)                                                 -- RTK chu_rua.lua:50; both walkthroughs list it
    ctx:takeItem("young_ginseng", 1)
    ctx:giveItem("sea_ring", 1)
    ctx:addLegend("Aided Chu Rua (" .. ctx:gameDate() .. ")", "aided_chu_rua", 5, 128)
    ctx:sayItem("sea_ring", "Humbly, I offer one of the finest jewels from the sea.")
    return
  end

  ctx:say(
    "I have swum as hard as I could. Hey! hey you, honorable human. But a moment! I would that you would hear out an earnest request.",
    "The Lord, Dragon King, is dying as we speak, beneath the waves in his palace. The finest physician has come and declared that he must have an item we cannot procure from within the sea.",
    "I entreat you as a humble servant of the Dragon King, and the only servants who know of the land and the sea.",
    "Please, his highness's health depends upon a root of Young ginseng.")
  ctx:sayItem("sea_ring", "Give this to me, and this ring of the Mermaid Princess I would, in return, give to thee.")
  -- The "Hello" hint is LOAD-BEARING and must not be paraphrased away: it is the only place in the game that
  -- tells you to greet things, and with the rabbit gate below it is now a hard requirement, not just a nudge.
  -- What was here before ("The ginseng lies north, in the Tiger Pass — mind the tiger.") was invented, and it
  -- also spoiled the tiger, which is the rock's payoff to deliver.
  --
  -- Last line is the Jan-2001 wording (tswolf king9). Both captures run to exactly nine screens and differ
  -- only on this one, so it is a substitution, not something RTK dropped: the later nexusatlas capture has
  -- "Please get young ginseng for his highness's sake!" here instead, which is also what RTK carries. Swap
  -- the two if you ever want the later reading.
  ctx:say(
    "I... I wish I could point you in the way of the ginseng, but I know not where it grows. There is an old verse,",
    "'Skip north, until rabbits nibbling grass you find, is a path to a king's health and harmony,'",
    "I can tell you, though, that you may greet some of the magic animals of the land. What is you people say, \"Hello\"?",
    "Be sure to greet me again and not to hand the young ginseng until I ask. If you don't click on me first, the ginseng will fall into the sea.")
end

-- The Sanhae Mayor (RTK NPCs/arctic/sanhae_mayor.lua), Sanhae Hall (1127), the only NPC in the room. He is
-- the BREADCRUMB for tutorial stage 11: the tutor sends you here ("Talk to the Mayor there, he may be able
-- to tell you what happened") and the mayor is what turns that into an actual direction -- his "Du Mountain?"
-- topic names the mountain and where to turn off the northern pass. Without him the stage is completable but
-- unfindable, since the tutor's own directions point at the Arctic and Haguru says nothing until you stand
-- in front of him.
--
-- "Live in Sanhae" binds his tavern (the Spruce Inn, 1122) as your Return destination -- see live_in_town
-- and Session.HomeGroup. RTK gates it on being Buyan, which Sanhae belongs to.
--
-- NOT ported from the Lua, deliberately: "Waypoint". RTK's waypoint fast-travel network doesn't exist in
-- 4.x/5.x NexusTK -- the same call already made in Server/NpcAbility.cs's WaypointAbility stub.
--
-- ERA: the stage-11 branch is gated on the quest's own date as well as on the stage, because TutorialQuest
-- deliberately does NOT rewrite a saved stage when a gate is off -- a character stored at 11 with the quest
-- switched off dispatches as 12 at the tutor, and the mayor must not be the one place that still talks about
-- it. Outside that window he is just the mayor of a town, which is what he was before 2001-03-18.
--
-- One topic per click, as in the Lua: RTK asks once and ends the conversation. Clicking him again re-opens
-- the menu, so nothing is unreachable.
function npcs.SanhaeMayorNpc(ctx)
  if ctx:menu("Hello! How can I help you today?", {"Sanhae Mayor", "Live in Sanhae"}) == 2 then
    live_in_town(ctx, 10, 2)   -- registry home 10; Buya-only, Sanhae being a Buyan town
    return
  end

  ctx:say("Hello there. welcome to the town of Sanhae.")

  if ctx:stage("tutorial_quest") ~= 11 or not ctx:eraHas("tutor_du_mountain_quest") then
    return
  end

  ctx:say("You look troubled... And so you should be. There are some dark forces at work here.")

  local choice = ctx:menu("So, what can I help you with today?",
                          {"Missing Brother", "Dark Forces?", "Du Mountain?"})

  if choice == 1 then
    ctx:say(
      "Poor, poor man. He went off with the others to hunt, and is lost with them.",
      "Darn these evil forces, if only somebody brave enough would lift the curse.")
  elseif choice == 2 then
    ctx:say(
      "Recently several of our men have gone missing from this town.",
      "They go off to hunt at Du Mountain, and never return.",
      "I fear it will be the end of our village if something is not done soon.")
  elseif choice == 3 then
    ctx:say(
      "Oh, you are new to these lands. Du Mountain is to the west of our town.",
      "If you go back the way you came, then head to the west side of the northern pass you will find it.",
      "But I beg you not to go there, only evil resides there now.")
  end
end

-- Lanwick, the Hausson Mayor (RTK NPCs/hausson/hausson_mayor.lua), Hausson Hall (1024). RTK's script is the
-- Sanhae mayor's minus the tutorial branch and the waypoint: he has exactly ONE thing to offer, a room in
-- the Haggard Mate Tavern (1027), so there is no menu to open -- clicking him IS asking about the room.
--
-- Koguryo's outlying town, so his gate is country 1 where Sanhae's is 2. That pairing is the whole point of
-- the two of them: each kingdom gets one back-country home, and you can only take the one your own kingdom
-- owns. Moving kingdom therefore has to give up the room, which Session.SetNation does.
function npcs.HaussonMayorNpc(ctx)
  live_in_town(ctx, 11, 1)   -- registry home 11; Koguryo-only
end

-- Haguru, the tutors' missing youngest brother (RTK NPCs/arctic/haguru.lua). He stands on Du Mountain, the
-- first turning west off the Northern Pass, with a pack of mountain wolves above him. Kill 3 and he sets
-- helped_haguru, which clears TutorialQuest stage 11.
--
-- Faithful to the Lua including its ONE deliberate looseness: RTK commented out the `registry
-- ["tutorial_quest"] == 11` half of the gate and ships the kill-count check alone, so the flag can be earned
-- before the tutor ever asks for it. Kept that way — a player who cleared wolves here last week should not be
-- sent back to do it again.
--
-- ERA GATE: the QUEST is dated 2001-03-18, but Haguru is not. TSWolf's release post that day calls him
-- "the old guy named Haguru that was stranded on the mountain in early 4.0" -- he had been standing here
-- for months with nothing to give. So before that date he stays where he is and still answers, he just
-- has no wolves to send you after, and helped_haguru is never set. (His pre-quest lines are RECONSTRUCTED
-- -- no archive records what a stranded Haguru actually said -- and are written to give away nothing the
-- quest later reveals, in particular not his name, which is the quest's own payoff.)
function npcs.HaguruNpc(ctx)
  if not ctx:eraHas("tutor_du_mountain_quest") then
    ctx:say(
      "Away with you. This mountain is no place to go wandering, and I have troubles enough of my own.",
      "My road down washed out in the thaw and the wolves have the high paths. So here I sit.",
      "No, I want nothing from you. Go back the way you came, before the cold finds you too.")
    return
  end

  if ctx:killCount("mountain_wolf") >= 3 then
    ctx:setReg("helped_haguru", 1)
    ctx:say(
      "You were great! Thank you so much! Now I can save my friends up there, once I get them out of hiding.",
      "My name is Haguru, I am the brother of the tutors of the great city.",
      "Why do you look so surprised? Don't tell me he sent you to look for me! You can go back and tell him that I am fine, and not to worry about me.",
      "Oh, and thanks again for all your help here today.")
    return
  end

  ctx:say(
    "You just came from my town I see. Did you come to help me with the dark forces?",
    "Of course you are! What other reason would you have to be in such a place.",
    "Well, you can help me if you want, but it could be dangerous.",
    "A few levels up this mountain you will find a pack of wolves. They have trapped my hunting party up there.",
    "If you can kill a few of them then I can start saving the people. Be very careful, they do a lot of damage to you.")
end

-- =====================================================================================================
-- THE NEWBIE TUTORIAL AREA (maps 4711 Welcome -> 4712 Open Field -> 4713 Forest Path -> 4714 Deep Forest
-- -> 4715 Country Farm + 4716 Mignok's Home -> 4717 City Limits -> 4718 Angel's Blessing).
--
-- Added ~2000-10-06 and era-gated on `newbie_tutorial_area`; before that date the tutor taught these
-- beats himself (Server/NoviceQuest.cs, retired the same day) and new characters spawned at his feet.
-- The tutor is NEVER gated -- all three eras end up at him, the area just changes what he still has to
-- teach. See Server/Era.cs and docs/common/Era-Gating.md.
--
-- The dialog below is TRANSCRIBED, not reconstructed: every line is read off the screenshots archived on
-- tswolf.com/newb/quest.shtml, quest2.shtml and quiz.shtml (Wayback 2001-06-27 / 2001-07-23), which
-- captured each dialog page as a GIF. Where a page failed to archive the gap is marked inline rather
-- than papered over. This supersedes NoviceQuest.cs's rewritten one-tutor wording, which existed only
-- because the area itself wasn't implemented -- here the four speakers really are four NPCs in four
-- rooms, so their "continue down this path" framing is literally true again.
--
-- One counter drives the whole area; each NPC owns a slice and nudges you back if you're behind it.
local NEWB = "newbie_area_quest"
local NEWB_RABBITS, NEWB_SQUIRRELS = 10, 10       -- tswolf: "Slay ten of them", "kill ten Squirrels"
local NEWB_STAGE_EXP = 50                          -- junior to every TutorialQuest stage, as NoviceQuest is
local NEWB_MIGNOK_TOLD = "newbie_mignok_told"      -- set once Mignok has named his price (see MignokNpc)

-- Kills are compared against a snapshot taken when the task is GIVEN, so a player who wandered off and
-- cleared the field before being asked doesn't skip the quest (same pattern as NoviceQuest/MinorQuest).
--
-- The count is never shown back to the player. None of the archived dialog does that -- the NPC just
-- restates the task -- and a running "3 of 10" tally reads as a modern quest-log UI the 4.95 client has
-- no equivalent of.
local function newb_killed(ctx, mob, snapkey)
  return ctx:killCount(mob) - ctx:reg(snapkey)
end

-- Quest 1 -- the traveling weapon smith, 4712 Open Field. Gives the Wooden saber and the 10 rabbits.
function npcs.WoodlandSmithNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage == 0 then
    ctx:say(
      "Welcome young one. What are you doing so far from town? 'Tis a good thing that this area is relatively safe to travel.",
      "Within this kingdom lay many a beast that would kill you with nothing but a glance. In these fields you will find little that would kill you fast, but that is not to say they can't kill you.")
    ctx:giveItem("wooden_saber", 1)
    ctx:sayItem("wooden_saber",
      "I am a traveling weapon smith. I can teach you the basics of combat, and weapons. Here is a Wooden saber, I carve them from the branches I find in the woods nearby.")
    ctx:say(
      "To see what you're carrying press 'i'. To use a weapon or item in your inventory press 'u', then the letter of the weapon.",
      "To attack using your saber press the <space> bar. Wield your new Wooden saber now, then look for rabbits around here. Slay ten of them and return to me.")
    ctx:setReg("newbie_rabbit_snapshot", ctx:killCount("rabbit"))
    ctx:setStage(NEWB, 1)
    return
  end

  if stage == 1 then
    if newb_killed(ctx, "rabbit", "newbie_rabbit_snapshot") < NEWB_RABBITS then
      ctx:say("The rabbits are still out there, young one. Slay ten of them and return to me.")
      return
    end
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 2)
    ctx:say(
      "Congratulations! Your first hunt is a success! When you get to the town make sure to seek out a blacksmith to keep your weapons in top condition, as you use them they can grow dull and may break.",
      "Now continue down this path, and catch up with my brother who I am sure will tell you a little of his trade - Armor!")
    return
  end

  ctx:say("Continue down this path, and catch up with my brother who I am sure will tell you a little of his trade - Armor!")
end

-- Quest 2 -- the smith's brother the armorer, 4713 Forest Path. Gives the Spring garb and the 10 squirrels.
function npcs.WoodlandArmorerNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 2 then
    ctx:say("Back down the path there is my brother, a weapon smith. Speak with him before you come to me — you'll want something to swing before you want something to wear.")
    return
  end

  if stage == 2 then
    ctx:say("Whoa there mighty fighter. Where are you off to in such a hurry? I see you have already armed yourself well for a hunt, but your still in your rags.")
    ctx:giveItem("spring_garb", 1)
    ctx:sayItem("spring_garb",
      "Take this, like all armor in this kingdom it is tailored to fit your gender. It is Spring in quality, and represents the spring of your adventure.")
    ctx:say(
      "As with weapons, you can press 'i' to see it in your inventory. Then type 'u' and the letter next to the armor to wear it.",
      "Armor helps protect you from attacks, and reduces the damage you take. If you look to the bottom right of your screen you will see a red bar, that is your vitality, your health.",
      "Watch it carefully as you hunt, for should it fall to low your body will become but a spirit, and some mana can drop to the floor or even break from the extra damage of death!",
      "This part of the woods is filled with Squirrels. Unlike the rabbits from before these have far sharper teeth. Your new armor will help protect you from their bite.",
      "Go now, kill ten Squirrels. As you kill them you will see that they drop Acorns. Stand above the acorns and press ',' to pick them up.")
    ctx:setReg("newbie_squirrel_snapshot", ctx:killCount("squirrel"))
    ctx:setStage(NEWB, 3)
    return
  end

  if stage == 3 then
    if newb_killed(ctx, "squirrel", "newbie_squirrel_snapshot") < NEWB_SQUIRRELS then
      ctx:say("Ten Squirrels, and mind their teeth.")
      return
    end
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 4)
    ctx:say(
      "Well, that was fast! You are well on your way to being a truly mighty fighter. Remember to keep your Armor well maintained like your weapons.",
      "As you grow stronger, and gain more insight, you will be able to use better armor and weapons. These can be gained from creatures you kill or bought from players and shops.",
      "Continue now on your journey, and I hope you fair well on your adventure.")
    return
  end

  ctx:say("Continue now on your journey, and I hope you fair well on your adventure.")
end

-- Quest 3 -- the coordinate lesson, 4714 Deep Forest. He explains the coordinate readout and sends you to
-- walk to 21,20, where the map's own warp (Warps.csv 4714 (21,20) -> 4715 (3,2)) carries you on.
--
-- GAP: tswolf's quest3congrats/quest3congrats2 ("Congratulations! You have learned to find your way
-- around. If you look at the bottom right of your screen, the numbers will now read 0021 0020." / "Using
-- this system, and the information in the help ((Press F1)) under 'Finding a place', you can find your
-- way around the cities and towns.") fired ON the 21,20 tile, not from an NPC -- the guide says "a text
-- box will pop up". That needs the scripted-tile trigger system, so those two pages are deliberately NOT
-- spoken by anyone here rather than being relocated onto a speaker who couldn't have said them.
--
-- The REWARD, though, does belong on the tile, and now sits there: this stage is the only one in the area
-- whose task is "walk somewhere", so paying it out when he finishes SPEAKING paid for listening, not for
-- doing. Session.TryNewbieCoordinateLesson awards it as the player warps through 21,20 -- see
-- Server/Session.Navigation.cs. He therefore sets the stage but awards nothing here.
function npcs.TutorialNpc1(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 4 then
    ctx:say("Hello there young one. You seem like your in a hurry. But where are you going to in such a rush? Do you even know?",
            "Go back along the path and finish what my brothers were teaching you. They will not keep you long.")
    return
  end

  if stage == 4 then
    ctx:say(
      "Hello there young one. You seem like your in a hurry. But where are you going to in such a rush? Do you even know?",
      "Look to the bottom right of your screen. Those two numbers are where you stand — the first across, the second down. Every place in the kingdoms is named that way.",
      "Learn to read them and you need never be lost again. Walk from here to 0021 0020, and I will know you have understood.")
    ctx:setStage(NEWB, 5)   -- the exp is paid on the 21,20 tile itself, not here (see the note above)
    return
  end

  ctx:say("Walk to 0021 0020, and the way onward will open for you.")
end

-- Quest 4 -- the magic teacher, 4715 Country Farm. Points you at his sister Mignok in the hut to the
-- north east (Warps.csv 4715 (17,4) -> 4716), then explains the spell list when you come back.
function npcs.TutorialNpc2(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 5 then
    ctx:say("Peace, young one. There are others along the path behind you with more to teach before I can be of use.")
    return
  end

  if stage == 5 then
    ctx:say(
      "Welcome child. You seem to be learning much, I can sense your mind expanding in leaps and bounds. But are you ready for the greatest test of mental power?",
      "Magic, and its mastery, is the greatest challenge of the mind. Are you ready to face the challenge? Depending on which path you follow later in your life ((level 5)) you will learn different secrets.",
      "In the hut beside me is my sister, a novice magic teacher. If you go in I am sure she will teach you a secret. She may ask for items in exchange for teaching you.",
      "Return to me after she has taught you the spell.")
    ctx:setStage(NEWB, 6)
    return
  end

  if stage == 6 then
    ctx:say("Return to me after she has taught you the spell. She is in the hut beside me.")
    return
  end

  if stage == 7 then
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 8)
    ctx:say(
      "Ah, you look eager to use your new spell. To see the list of spells you have press the '+' key on the keypad on the right of your keyboard.",
      "When you click on your Path's tutor, they will have a button that says 'Learn Secret' and it is from there that you will be able to see what spells can be learned.",
      "Try your spells now, and when you're ready you should continue with your travels.")
    return
  end

  ctx:say("Try your spells now, and when you're ready you should continue with your travels.")
end

-- Mignok, 4716 Mignok's Home -- teaches Soothe for 5 acorns and 5 rabbit meats.
function npcs.MignokNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 6 then
    ctx:say("Greetings there young one. My brother stands outside — speak with him first, and he will send you to me when you are ready.")
    return
  end

  if stage > 6 then
    ctx:say("Go well, and use the spell kindly.")
    return
  end

  -- Naming the price IS the quest, so it has to be said before it can be met: a player who wandered in
  -- already carrying 5 acorns and 5 rabbit meats (both drop in the rooms before this one) used to skip
  -- straight to the reward and never hear a word of it. Told once, then hand them over on the next click.
  if ctx:reg(NEWB_MIGNOK_TOLD) ~= 0 and ctx:hasItem("acorn", 5) and ctx:hasItem("rabbit_meat", 5) then
    -- TAKE FIRST, and only teach if the take actually succeeded. The old order (teach, then take) meant any
    -- failure to collect -- and takeItem CAN return false -- handed over the spell for free and left the
    -- items sitting in the pack, which is exactly what was reported. Consuming up front makes the trade
    -- atomic in the direction that matters: the reward is now impossible to get without paying for it.
    -- The acorns go first and the meat is only taken if they were, so a half-payment can't happen either.
    if not (ctx:takeItem("acorn", 5) and ctx:takeItem("rabbit_meat", 5)) then
      ctx:say("I need five acorns and five rabbit meats before I can teach you.")
      return
    end
    if not ctx:learnSpell("soothe") then
      -- Only reachable with a full spell book, which a player standing here cannot have -- but taking the
      -- payment first means the one case that could eat it has to hand it back.
      ctx:giveItem("acorn", 5)
      ctx:giveItem("rabbit_meat", 5)
      ctx:say("Your mind cannot hold any more secrets right now.")
      return
    end
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 7)
    ctx:say("Thank you for the items! Now here is your spell, Soothe. Go back and speak with my brother outside as to how to use it.")
    return
  end

  ctx:setReg(NEWB_MIGNOK_TOLD, 1)
  ctx:say(
    "Greetings there young one. So you wish to learn the ways of magic do you? All classes learn various spells. Some are common to all, while others are unique to the path they choose.",
    "Every spell will usually have a price associated with it. For instance, I will be happy to teach you the spell \"Soothe\" - a healing spell - but you will have to bring me 5 acorns and 5 rabbit meats.")
end

-- Quest 5 -- the Woodland Guard, 4717 City Limits. A short lecture on the law, then a yes/no quiz; pass it
-- and he hands over the Novice sword and lets you through.
--
-- The sword is HIS, not the Angel's: quizcongrats2 is the guard saying "here's a sword that will hit a
-- little harder than that stick your carrying" (the wooden saber). quiz.shtml's closing line "She gives
-- you a Novice Sword" contradicts its own screenshots, so the screenshots win.
--
-- All FIVE questions, in the archive's own quiz1..quiz5 order. Per question the archive may hold: quizN
-- (the question), quizNa (the two options, correct one highlighted yellow), quizNb (the guard's reply to a
-- correct answer). quiz1, quiz1a, quiz2, quiz2b and quiz4b have ZERO snapshots in the Wayback CDX index for
-- newb/questpics/ (which lists 50 files) and 404 at every timestamp -- permanently lost, not un-fetched.
--
-- Correct answers were read by MEASURING the highlight colour in quizNa.gif (rgb 239,223,143 vs the
-- 16,16,16 of the unselected row), not by eye: No, No, No, YES, No.
--
-- Each line below is tagged with where it came from: a bare quizN reference means it is transcribed off that
-- screenshot word for word, and anything marked RECONSTRUCTED is ours, written to fill a hole the archive
-- can no longer fill, and deletable without breaking the quiz.
local NEWB_QUIZ = {
  -- Q1 -- question RECONSTRUCTED. quiz1/quiz1a are gone, but quiz1b (the reply) survives word for word and
  -- fixes the subject and the answer beyond doubt, and the guard's own lecture already lists "no profanity".
  { q = "Are you allowed to use bad language in Nexus?",             answer = 2,   -- No. (RECONSTRUCTED)
    reply = "Correct! Bad language is not allowed in Nexus, and will land you in jail if used." },  -- quiz1b
  -- Q2 -- question and reply from brian; the archive kept only quiz2a, whose highlight is the second option,
  -- which agrees with the answer being No. Phrased to match the attested siblings' cadence.
  { q = "Are you allowed to harass other players?",                  answer = 2,   -- No. (quiz2a)
    reply = "Correct! Respect other players and treat them in the same manner you would like to be treated." },
  { q = "Are you allowed to steal other players items?",             answer = 2,   -- No.  quiz3/quiz3a
    reply = "Correct! Treat other people's items in the same way you would like them to respect yours." },  -- quiz3b
  -- Q4 -- reply RECONSTRUCTED (quiz4b is gone); the question and the Yes are both transcribed. Every other
  -- question answers back, so without this one the quiz silently advances mid-run.
  { q = "Is there a justice system within Nexus?",                   answer = 1,   -- Yes. quiz4/quiz4a
    reply = "Correct! Judges and justice officials uphold the law here, and they will act on it." },  -- RECONSTRUCTED
  { q = "Can you use new, or secret, characters to commit crimes?",  answer = 2,   -- No.  quiz5/quiz5a
    reply = "Correct! People using a new character to hide their true identity can be traced." },
}

function npcs.WoodlandGuardNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 8 then
    ctx:say("Who goes there? Show yourself for identification! Ahh.. you are not the person I am looking for. Sorry for the abrupt greeting, I am a city guard.",
            "You are not finished with your training yet. Go back and see it done, then I will let you by.")
    return
  end

  if stage > 8 then
    ctx:say("On your way, and keep on the right side of the law.")
    -- Edge case: passed the quiz but wound up back on this side of the gate anyway (relog, death, a warp
    -- that didn't take). He already agreed to let them through, so he does it again rather than leaving
    -- them stranded -- the gate is scenery and this is the only way in. Stage 9 only: once the Angel has
    -- finished with them (10) they belong in their home city, not back in there.
    if stage == 9 then ctx:warp(4718, 9, 18) end
    return
  end

  -- quest501, [quest502 -- from brian; the one page of the lecture the capture missed, and it lands exactly
  -- between "I am a city guard" and "If your caught"], quest503..quest506. The misspellings in the
  -- transcribed pages ("If your caught", "more sever punishments") are the original's, not typos -- leave them.
  ctx:say(
    "Who goes there? Show yourself for identification! Ahh.. you are not the person I am looking for. Sorry for the abrupt greeting, I am a city guard.",
    "I work for the justice system of Nexus. In our lands we have many rules that people must follow. Breaking the laws here can have severe consequences.",
    "If your caught, and convicted of a crime Judges and justice officials have the ability to jail your character, and if you continue there is more sever punishments!",
    "There are simple rules here, such as no profanity, no stealing, and no harassment. Most of this information is available on the 'Law' boards.",
    "All boards contain information that is important. Be sure to look over 'Law', and 'Guide', and read 'Dream Weaver' regularly as it keeps you updated on important information and events.",
    "Before I let you continue, I want to make sure you have a basic understanding of the laws.")

  for _, item in ipairs(NEWB_QUIZ) do
    local pick = ctx:menu(item.q, {"Yes.", "No."})
    if pick ~= item.answer then
      -- OURS: the archive doesn't record the failure branch. Re-asking from the top is the gentlest
      -- reading of a gate whose whole purpose is to make sure the answer is known.
      ctx:say("That is not the law here. Read the 'Law' board, and speak to me again when you are sure.")
      return
    end
    if item.reply then ctx:say(item.reply) end
  end

  ctx:giveItem("novice_sword", 1)
  ctx:awardExp(NEWB_STAGE_EXP)
  ctx:setStage(NEWB, 9)
  ctx:say("You have done well. I can now permit you to enter the city, go on your way, and keep on the right side of the law.")
  ctx:sayItem("novice_sword",
    "To help you defend against the lawless and some of those tougher critters, here's a sword that will hit a little harder than that stick your carrying.")
  -- He says he's letting you through, so he does. The gate itself (4717 17..20,18) is scenery in a solid
  -- wall band and stays shut -- passing it is HIS act, not a tile you walk over, which is also the only
  -- reason 4718 is reachable at all (there is no walk-in warp into Angel's Blessing).
  ctx:warp(4718, 9, 18)
end

-- Quest 6 -- the Woodland Angel, 4718 Angel's Blessing. Closes the area and hands the player to the town
-- tutor, which is the seam the whole era model turns on: the area never replaced the tutor, it fed him.
-- Saying "Finish" is what ends it (npcs_say.WoodlandAngelNpc below).
--
-- GAP: lastq02 and lastq08 did not survive the Wayback capture. lastq08 sat between the "talk out loud"
-- page and the farewell, and the page before it promises to teach "how to talk, AND WHISPER" while only
-- ever teaching ' -- so the whisper key is what is missing. Written by us (2026-08-11, brian) to close
-- that dangling promise rather than left blank: the mechanic is real and LIVE-confirmed (shift+' opens the
-- prompt, name + enter, message + enter -- see Session.HandleWhisperPacket and docs 11g). Not a
-- transcript; the wording is ours. lastq02 is still an unfilled hole.
function npcs.WoodlandAngelNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 9 then
    ctx:say("Not yet, child. The guard at the city limits has not finished with you.")
    return
  end

  if stage > 9 then
    ctx:say("Good luck, I now leave you in the hands of the tutor from the town you have picked as home. Farewell.")
    return
  end

  ctx:say(
    "There you are at last. This marks the end of your training. The great tutors of the cities will now continue your training.",
    "You can get more help from players, and also the built in help system. To access the help press 'F1' and look under the topic that applies to you.",
    "Before you invest too much time into your character, make sure that you have selected a good name for the setting of Ancient Korea.",
    "Some groups, such as Clans and Subpaths will not accept people with names that reflect modern life or current trends.",
    "This may harm your chances of joining important parts of the Nexus community and block you from a full and diverse adventure in Nexus.",
    "The last thing I have to teach you is how to talk, and whisper to people. Pressing ' allows you to talk out loud, to all the people around you.",
    "To whisper instead, press <shift> and ' together. Type the name of the person you wish to reach and press <enter>, then your message and <enter> again — only they will hear it, wherever in the land they stand.",
    "When you are ready to leave, say \"Finish\".")
end

-- RTK generalNPC.crafting_skills (NPCs/Common/generalNPC.lua) — the shared "tell me about crafting" explainer
-- every crafting merchant in the game offers. Verbatim but for the last line, which names RTK's own server.
-- Loops until the player takes the last option or closes the dialog, same as RTK's tail-recursive original.
-- Purely informational: it describes the skill SYSTEM, and says nothing about the player's own progress, so
-- it reads correctly even though the crafting skills themselves aren't implemented here yet.
local function crafting_skills(ctx)
  while true do
    local choice = ctx:menu(
      "I would be happy to tell you about crafting skills. What would you like to learn about?",
      {"General information on crafting skills.", "Gathering skills.", "Manufacturing skills.",
       "Refining skills.", "Thanks, nothing for now."})

    if choice == 1 then
      ctx:say("There are three types of crafting skills: Gathering, Manufacturing, and Refining. Initially, you have no training in any skills.",
              "As you successfully use a skill, your ability in that skill will gradually increase. You will notice improvements occur faster when your skill level is still low.",
              "As you become better, it takes longer to improve your ability. Becoming a 'Master', or higher, takes a very long time.",
              "As your skill improves, you will fail less often and have positive results more often. Most skills require some tools or materials.",
              "Throughout the land, you will find merchants who know different skills. Each merchant will explain to you the details of how his or her specific skill is performed.")
    elseif choice == 2 then
      ctx:say("Gathering skills are the simplest of all skills to acquire. Even unskilled people can perform these fairly well. They involve getting raw materials to sell or to use for more advanced skills.",
              "Eventually, everyone can become a master at all gathering skills. Gathering skills usually require tools.",
              "You must be at least level 8 to gather materials.")
    elseif choice == 3 then
      ctx:say("Manufacturing skills involve turning raw materials into more valuable forms. You can reach the 'Accomplished' skill level in any manufacturing skill.",
              "You can also specialize in one specific manufacturing skill. With enough work, you can become a 'Master' or higher, in that one skill.",
              "You will find that you still sometimes fail at manufacturing skills in which you possess great experience. Overall, however, you will be making better products and earning more money as you improve.",
              "You must be at least level 25 to perform a manufacturing skill.")
    elseif choice == 4 then
      ctx:say("Refining skills are the most advanced of all skills. You can only learn one refining skill. These skills allow you to create useful items, like weapons and armor.",
              "You must be at least level 50 to learn a refining skill.")
    else
      return
    end
  end
end

-- RTK crafting.checkSpecialization: you may hold only ONE manufacturing specialisation, so before granting a
-- new one the merchant offers to abandon the one you have. Two confirmations deep, because it is destructive.
-- Returns true if `skill` is no longer held afterwards (either it never was, or they abandoned it).
--
-- RTK clears the skill's accumulated POINTS here too (registry[skill] = 0). We have no skill points yet — only
-- the legend mark that records the specialisation — so there is nothing else to clear.
local SPEC_TRADE = {weaving = "weaver", smelting = "smelter", gemcutting = "gemcutter"}

local function abandon_specialization(ctx, skill)
  if not ctx:hasLegend("specialized_in_" .. skill) then return true end

  ctx:say("You have already specialized in " .. skill .. ", another manufacturing skill. If you abandon it, you will lose ALL skill in that craft. Even if you return to it at a later time, you will have to begin anew.")
  if ctx:menu("Are you absolutely certain you want to abandon your " .. skill .. " trade?",
              {"Yes, I'm entirely certain.", "I'm not sure."}) ~= 1 then return false end

  ctx:say("If you continue, you will lose your " .. skill .. " skill.")
  if ctx:menu("This is your last chance to turn back! Do you REALLY want to do this?", {"Yes.", "No."}) ~= 1 then
    return false
  end

  ctx:removeLegend("specialized_in_" .. skill)
  ctx:removeLegend("recently_specialized_" .. SPEC_TRADE[skill])
  ctx:say("It is done.")
  return true
end

-- RTK crafting.addSpecialization, weaving branch: level 25 and 500 gold, recorded as two legend marks.
local function specialize_in_weaving(ctx)
  if ctx:level() < 25 then
    ctx:say("You are not ready to specialize in a craft yet, come back later.")
    return
  end

  ctx:say("You can only specialize in one manufacturing craft at a time. If you change your mind later, you will lose all of the skill you worked for in that craft.",
          "For a mere 500 gold, I will help you specialize in Weaving.")
  if ctx:menu("Are you willing to pay 500 gold?", {"Yes, I wish to become a weaver.", "No thanks."}) ~= 1 then
    return
  end
  if not ctx:spendGold(500) then
    ctx:say("You need to get 500 gold first.")
    return
  end

  ctx:addLegend("Specialized in Weaving", "specialized_in_weaving", 7, 128)
  ctx:addLegend("Recently specialized weaver", "recently_specialized_weaver", 64, 128)
  ctx:say("It is done, welcome to the world of Weaving.")
end

-- Laptev, the Arctic Village weaver (Laptev Crafter, map 3817). Ported from RTK's Yon
-- (NPCs/wilderness/yon.lua), the game's other weaving merchant — same menu, same wording.
--
-- The empty "Buy" is deliberate and is RTK's own note: it records that NexusTK was checked live and the
-- weaver answered "I don't sell anything." She has no ShopStock row, so ctx:buy() finds no catalogue and says
-- so, which is why the option can stay on the menu instead of being hidden.
--
-- NOT ported from Yon: the waypoint entry (no waypoint system here), the "Weave Magical Net" wind_armor quest
-- step, and the `weave`/`twine` speech triggers — those need the crafting skill system, which is still just an
-- era gate (Server/CraftingToggles.cs). "Weaving Specialization" IS ported because RTK records a
-- specialisation as a legend mark, which we have; it is the skill POINTS that don't exist yet.
function npcs.LaptevNpc(ctx)
  local choice = ctx:menu("Hello! How can I help you today?",
    {"Buy", "Sell", "Crafting Skills", "Joy of Weaving", "Weaving Specialization"})

  if choice == 1 then
    ctx:buy()
  elseif choice == 2 then
    ctx:sell()
  elseif choice == 3 then
    crafting_skills(ctx)
  elseif choice == 4 then
    ctx:say("I would be happy to tell you about weaving! Weaving requires three things: steady hands, some wool, and good weaving equipment.",
            "You can get wool from sheep.\nYou'll have to see a woodworker in order to acquire your own weaving tools, but I can loan you the rest of the things you need. As for the steady hands, those come with practice.",
            "Just say 'weave' to me when you're ready to give it a try!")
  elseif choice == 5 then
    if ctx:hasLegend("specialized_in_weaving") then
      ctx:say("You have already specialized in Weaving.")
      return
    end
    -- Weaving/smelting/gemcutting are the three manufacturing specialisations and you may hold one. RTK asks
    -- about each competing one in turn, then falls through to addSpecialization REGARDLESS of the answer —
    -- so declining to abandon smelting still made you a weaver as well. Guarded here: its own dialog promises
    -- "only one manufacturing craft at a time", so granting a second one is plainly not the intent.
    for _, other in ipairs({"smelting", "gemcutting"}) do
      if not abandon_specialization(ctx, other) then
        ctx:say("Then you must remain a " .. SPEC_TRADE[other] .. ". Come back if you change your mind.")
        return
      end
    end
    ctx:say("Weavers can make cloth from wool. Do you want to specialize in weaving? ((You need to be specialized to become better than 'Accomplished.'))")
    specialize_in_weaving(ctx)
  end
end

-- Speech-trigger handlers: npcs_say.<Identifier> = function(ctx, speech). `speech` is already lowercased +
-- trimmed. Return true to CONSUME the speech (stops dispatch); return false/nothing to let other NPCs or the
-- C# say-handlers try it. Same ctx/coroutine model as the click handlers above.
npcs_say = {}

-- Saying "Finish" to the Woodland Angel ends the newbie area and delivers the player to their home city's
-- tutor -- the hand-off the tutor chain then picks up (TutorialQuest stage 0).
function npcs_say.WoodlandAngelNpc(ctx, speech)
  if speech ~= "finish" then return false end
  if ctx:stage(NEWB) < 9 then
    ctx:say("Not yet, child. The guard at the city limits has not finished with you.")
    return true
  end
  ctx:awardExp(NEWB_STAGE_EXP)
  ctx:setStage(NEWB, 10)
  ctx:say("Good luck, I now leave you in the hands of the tutor from the town you have picked as home. Farewell.")
  warp_home(ctx)
  return true
end

-- The talking rabbit of Guol Valley (chu_rua_rabbit.lua) — hints at the ginseng quest, and GATES the tiger.
--
-- Greeting him sets chu_rua_rabbit_greeted; until then the tiger will not take the "rabbit" gambit (see
-- ChuRuaTigerNpc). RTK has no such gate -- neither do the tswolf or nexusatlas walkthroughs, and nothing in
-- the scraped board archive describes one -- so this is a deliberate design fix rather than a port: without
-- it the rabbit and the rock are skippable scenery and the whole dialog chain can be shortcut by anyone who
-- already knows the word. Only "hello" arms it, which is the greeting Chu Rua actually teaches; "tiger" and
-- "ginseng" are follow-ups you would only think to ask AFTER greeting him.
function npcs_say.ChuRuaRabbitNpc(ctx, speech)
  if ctx:karmaTooLow() then return true end   -- RTK Tools.checkKarma at the top of the handler
  if speech == "hello" then
    ctx:setReg("chu_rua_rabbit_greeted", 1)
    ctx:say("Hmmm..", "What is it you want?")
    return true
  elseif speech == "tiger" then
    ctx:bubble("Fool was I to go north for ginseng. He almost ate me!")
    return true
  elseif speech == "ginseng" then
    ctx:say(
      "What a bitter root! It's as bad tasting as the mountains in which it grows.",
      "Some trickster cousin told me I should go up the left path and have some of the delicious root.",
      "Fool was I to go into the awful mountains. I followed this stream up to those horrid mountain's foot, and hopped up a dangerous path.")
    return true
  end
  return false
end

-- The Ancient dolmen of Guol Divide (chu_rua_rock.lua) — say "hello" for the tiger hint.
function npcs_say.ChuRuaRockNpc(ctx, speech)
  if speech ~= "hello" then return false end
  if ctx:karmaTooLow() then return true end   -- RTK Tools.checkKarma
  ctx:say(
    "O, it must be good to have feet.",
    "You've been to the sea I'll bet from the smell of you.",
    "That is where I have lived for so long until now; by the sea.",
    "Thank you for spending a moment with this old soul. Be careful of the tiger to the north.",
    "He only thinks of food, though you might distract him if you allude to one of the rabbits that tricked him")
  return true
end

-- Myung-Suck, the rock at 20,21 in Northeast Koguryo (map 3040), by the Nameless Hermit -- "Myung-Suck
-- means long-lasting rock in Korean" (NexusAtlas). Say "hi" and it grumbles "Quit bugging me will ya?".
-- The Hermit warns you it only talks "if he's in the mood... he's a grumpy fellow": the FIRST time you
-- greet it, it ignores you a hidden 3-to-5 times before it answers. Once it has spoken to you it always
-- does. State is per-character (persisted regs): a rolled threshold, a try-counter, and a spoken flag.
-- (RTK's own rock had none of this -- it answered every time -- so this gate is deliberately new.)
function npcs_say.RockNpc(ctx, speech)
  if speech ~= "hi" then return false end
  if ctx:karmaTooLow() then return true end   -- RTK Tools.checkKarma
  if ctx:reg("myung_suck_spoken") ~= 1 then
    local need = ctx:reg("myung_suck_threshold")
    if need == 0 then need = ctx:rand(3, 5); ctx:setReg("myung_suck_threshold", need) end
    local tries = ctx:reg("myung_suck_tries") + 1
    ctx:setReg("myung_suck_tries", tries)
    if tries < need then return true end      -- ignored you this time: consume the "hi", say nothing
    ctx:setReg("myung_suck_spoken", 1)        -- reached the threshold: it talks now and forever after
  end
  ctx:say("\"Quit bugging me will ya?\"")
  return true
end

-- The tiger guarding the ginseng (chu_rua_tiger.lua). Say "rabbit", pick Forest, and he leaves (sets the
-- chu_rua_tiger_gone flag so TryGinseng lets you take the root on map 1116).
--
-- "hello" answers OUT LOUD (a bubble over his head); "rabbit" opens a real dialog pop-up. That split is
-- deliberate and matches RTK's own npc:talk-vs-dialogSeq split -- do not collapse them into one.
--
-- The "rabbit" branch is GATED on having greeted the rabbit (see ChuRuaRabbitNpc). Ungated, he just threatens
-- to eat you, so the word buys you nothing until you have actually met the animal you are naming.
function npcs_say.ChuRuaTigerNpc(ctx, speech)
  if ctx:karmaTooLow() then return true end   -- RTK Tools.checkKarma
  if speech == "hello" then
    ctx:bubble("Hello, Dinner!")
    return true
  elseif speech == "ginseng" then
    ctx:bubble("I'd rather eat you!")
    return true
  elseif speech == "rabbit" then
    if ctx:reg("chu_rua_rabbit_greeted") ~= 1 then
      ctx:say("Grrr... you look good to EAT! Come here!")
      return true
    end
    ctx:say("What? Rabbit? Was it that foul hopping furre that trapped me in a pit?")
    local choice = ctx:menu("I'd love to rend his neck. Where did you see him?",
      {"Warrior's Guild", "Forest", "Town", "Mage's Guild"})
    if choice == 2 then   -- Forest = correct
      ctx:say("Mmm. Well then I guess I'll return him a favor with grinning teeth.")
      ctx:notify("The tiger leaves to the south.")
      ctx:setReg("chu_rua_tiger_gone", 1)
    elseif choice == 4 then
      ctx:say("What, did someone pull him out of a hat?",
              "So far? Oh well, I guess I'll have a snack beforehand... and you look tasty!")
    else
      ctx:say("So far? Oh well, I guess I'll have a snack beforehand... and you look tasty!")
    end
    return true
  end
  return false
end

-- The Nameless Hermit (RTK NPCs/tutorial/nameless_hermit.lua), the ragged old man in Northeast Koguryo
-- (map 3040, standing at 4,2 on the near side of the lava). He is the "one who has dwelt here a long time"
-- that Blood points you toward after you pay for a Frost sabre -- the ice-beast questline's breadcrumb. He
-- tells you where the Ice Beast is (across the lava, on the far side of the SAME map -- our Spawns.csv puts
-- it at 29,3) and warns you off it. Pure flavour, no state: killing the beast for its Ice heart is the real
-- gate and Blood forges the sabre. See npcs_say.BloodNpc below for the other end of the chain.
--
-- He also runs a small side-trade: hand him an Aged wine and he gives you a pair of Traveling shoes. It
-- opens automatically when you click him while carrying the wine. A completed hand-in ENDS there (we return
-- rather than fall through to the greeting/menu -- a deliberate deviation from RTK, which kept going);
-- declining ("I kind of need it") still drops into the usual greeting so he stays talkable.
function npcs.NamelessHermitNpc(ctx)
  local country = ""
  if ctx:nation() == 1 then country = "Kugnae"
  elseif ctx:nation() == 2 then country = "Buya" end

  if ctx:hasItem("aged_wine", 1) then
    if ctx:menu("Are you willing to part with that Aged wine?",
        {"Of course!", "I kind of need it."}) == 1 then
      ctx:takeItem("aged_wine", 1)
      ctx:giveItem("traveling_shoes", 1)
      ctx:say("\"Why thank you! Here.\" The hermit rummaged through a dusty chest. \"Take these shoes if you'd like.\"")
      return                              -- the hand-in IS the conversation; don't fall through to the greeting/menu
    else
      ctx:say("The hermit sighs. \"That's too bad.\"")
    end
  end

  ctx:say("Well, hello! Not too many visitors out in these parts.",
          "You're from " .. country .. ", aren't you? I could tell by your urban mannerisms.")

  -- Come to him cold and he offers two things to ask about; once Blood has taken your 100 gold and sent
  -- you to find "one who has dwelt here a long time" (paid_gold_for_frost_sabre, or the completed-quest
  -- legend afterward) two more open up: his little wine errand, and the story of the rock -- which is how
  -- he hands you the "say Hi to the rock" tip.
  local opts = {"What do you know about the dreaded Ice Beast?"}
  if ctx:reg("paid_gold_for_frost_sabre") == 1 or ctx:hasLegend("defeated_ice_beast") then
    opts[#opts + 1] = "Is there anything I can do for you?"
    opts[#opts + 1] = "What's the story behind the big rock?"
  end
  opts[#opts + 1] = "I'm just looking around."

  local pick = opts[ctx:menu("So, traveller. What brings you by?", opts)]
  if pick == "What do you know about the dreaded Ice Beast?" then
    ctx:say(
      "You're here for the Ice Beast?!? I hope you're not serious. That Beast has been in this area for as long as I can remember.",
      "They say that it's semi-immortal. It can be defeated, but it will later reform! I don't know if that's true. I'm not sure anyone has defeated it!",
      "\"Thank goodness there's that lava between us. It stays on its side, I stay on mine.\" The haggard man laughs. \"Well, except when I dash over there to hunt rabbits. I'm real quick about it though.\"",
      "When you see how big it is, you'll want to stand clear, too. One good 'SMACK' and you'll be done for, I reckon.",
      "If you want some advice, leave that vile beast alone. The world is better off with you alive.")
  elseif pick == "Is there anything I can do for you?" then
    ctx:say(
      "\"Hmm...well, now that you mention it, I am a bit parched. I could sure use same Aged Wine to wash down my rabbit meat.\"",
      "\"Bring me back some Aged Wine and I'll make it worth your trouble.\"")
  elseif pick == "What's the story behind the big rock?" then
    ctx:say(
      "\"Heh. Curious about the rock, are you? That lump of dirt has been here longer than I have.\"",
      "\"Just say 'Hi' to him and he'll talk to you. If he's in the mood, that is. He's a grumpy fellow.\"")
  else
    ctx:say("Okay. Say, I'd stay on this side of the lava were I you.")
  end
end

-- Blood's CLICK menu (RTK NPCs/kaming/blood.lua `click`), heading verbatim. RTK offers Buy / Sell /
-- Forget Secret plus the blood-oath branches and a warrior-only Learn Spell; of those only Forget Secret
-- is a system this server has, so his menu is that one entry -- but it is still a MENU. Without this
-- handler he fell through to the C# abilities, where a lone entry dives straight into its service
-- (Session.RunNpcAsync), so clicking him opened "Which secret do you wish to forget?" cold -- a
-- destructive picker nobody asked for. His NpcAbilities.csv `forget_secret` row is kept as the fallback
-- for a failed script load; while this handler exists it owns the click and the row is unused.
function npcs.BloodNpc(ctx)
  local choice = ctx:menu("Hello! How can I help you today?", {"Forget Secret"})
  if choice == 1 then ctx:forgetSecret() end
end

-- Blood, the Sonhi trickster in Blood's Home off KaMing's Encampment (RTK NPCs/kaming/blood.lua, the
-- `onSayClick` "ice beast" branch). TutorialQuest stage 12 wants his Frost sabre.
--
-- The flow, verbatim from the Lua: say "ice beast" -> he pitches the sabre and asks 100 gold up front
-- (paid_gold_for_frost_sabre) -> you kill the Ice Beast in Northeast Koguryo, off Dae Shore, for its Ice
-- heart -> bring it back and he forges the sabre, pays 2,300 exp and grants the defeated_ice_beast legend.
-- Level 7 minimum, as RTK has it.
--
-- Only the "ice beast" branch is ported. His shop, spell-teaching and the whole blood-oath ritual are a
-- different system (and the oath belongs with marriage, which lives in C#); the "seal" branch belongs to the
-- wind-armor chain, which isn't here. An unported keyword just falls through to `return false`, so he stays
-- silent about them rather than half-answering.
function npcs_say.BloodNpc(ctx, speech)
  if speech ~= "ice beast" then return false end

  if ctx:level() < 7 then return true end   -- RTK returns silently rather than explaining

  if ctx:hasLegend("defeated_ice_beast") then
    ctx:say("\"I hope you have found your Frost sabre useful!\"")
    return true
  end

  if ctx:reg("paid_gold_for_frost_sabre") == 1 then
    if ctx:hasItem("ice_heart", 1) then
      ctx:say("The Sonhi looks surprised. \"I do not know how one as wimpy as you could defeat an Ice beast, but somehow you have triumphed.\"",
              "\"As promised, I will forge you a Frost sabre.\"")
      ctx:takeItem("ice_heart", 1)
      ctx:giveItem("frost_sabre", 1)
      ctx:awardExp(2300)
      ctx:addLegend("Defeated the Ice beast (" .. ctx:gameDate() .. ")", "defeated_ice_beast", 5, 128)
      ctx:setReg("paid_gold_for_frost_sabre", 0)
      ctx:sayItem("frost_sabre", "\"Wield it well.\"")
      return true
    end

    ctx:say("The Sonhi seems to be suppressing laughter. \"Bring me an Ice heart and I will make you a Frost sabre.\"")
    return true
  end

  ctx:say("The Sonhi grins. \"Ice Beast, eh? You must be after a Frost sabre.\"",
          "\"You have heard of the Frost sabre, haven't you? No? Then let me tell you what you are missing.\"")
  ctx:sayItem("frost_sabre",
          "\"Though only a modest weapon, the Frost sabre has many great powers.\"",
          "\"When you die, it will not leave your side. When it is worn, it can be repaired with ease. In combat, sometimes it chills your foe, making them easier to hit.\"",
          "\"Perhaps most impressive is that only YOU will be able to wield your Frost sabre if it is crafted for you.\nUnfortunately, very, very few know how to make one.\"")
  ctx:say("\"I know what you are thinking. Yes, I know how to craft a Frost sabre. I can see in your eyes that you are eager for one aren't you?\"")

  local choice = ctx:menu("\"For a mere 100 gold, I will forge you one if you bring me the item needed to make one. Will you pay?\"",
    {"Yes, I want a Frost sabre.", "No, I'll keep my money."})

  if choice == 1 then
    if not ctx:spendGold(100) then
      ctx:say("Come back when you have more gold.")
      return true
    end
    ctx:setReg("paid_gold_for_frost_sabre", 1)
    ctx:say("\"To forge it, I will need the Ice heart of a mighty and wicked Ice Beast.\"",
            "\"Where do you find an Ice Beast? I wouldn't know. We Sonhi aren't from this area. Perhaps one who has dwelt here a long time would know.\"",
            "As you are leaving, you hear the Sonhi captain chuckle to himself, \"Even if the fool finds an Ice beast, they'll surely die. An easy 100 gold, heh, heh.\"")
  elseif choice == 2 then
    ctx:say("\"As you wish... I suppose I will make a Frost sabre for another then.\"")
  end
  return true
end

-- =====================================================================================================
-- THE DOG LINGUIST (RTK NPCs/Common/dog_linguist.lua) -- four dogs, one identifier.
--
-- All four are `DogLinguistNpc` and differ only by NAME, so this one handler speaks as whichever of them
-- the player is standing next to (ctx:npcName()). It is speech-only: RTK's dogs have an onSayClick and no
-- click menu, so clicking one still just shows the default greeting.
--
-- PART ONE -- learning to bark. Each dog answers one word with the next one; you repeat its word back to
-- show you understood, and it passes you along. Per the tswolf walkthrough (Wayback 2001-03-11) you must
-- hear each dog's answer THREE times before the echo counts -- RTK's rewrite dropped the repetition and
-- took the first echo, which turns a four-stop chain into four one-word answers. The Spotted dog needs
-- only the single "grrowl" and hands over the legend.
--
-- PART TWO -- "secret" to your OWN class's dog teaches its two spells, at 70 and 99, in that order (the
-- level-99 one is gated on already knowing the level-70 one, which is RTK's ordering too). "The
-- guildmaster is not involved in these spells" -- see Content.SpellsForClass, which deliberately does not
-- carry them.
--
-- PART THREE -- "cleanse" gives the spells back. RTK wraps this in a "commit to NEVER join a subpath"
-- prompt, which does not apply here: PC subpaths are what the exclusion is about and they are not joinable
-- on this server, while NPC subpaths keep their dog spells (Content.CanLearnDogSpells). So the commitment
-- prompt is dropped and only the undo remains. Like RTK -- and like the live game its comments say it was
-- checked against -- it takes the SPELLS and the karma but leaves the legend: you are still a linguist,
-- you have just given the secrets back.

local DOG_LEGEND = "dog_linguist"          -- legend id, also the chain-progress registry key
local DOG_FLAG   = "dog_flag"              -- Content.DogFlagReg -- set once the chain is finished
local DOG_ECHO   = "dog_linguist_echo_"    -- prefix: how many times THIS dog has answered you. Per-dog, so a
                                           -- count left over from another dog (or from a GM @dog reset) can
                                           -- never pay for the next one's echo.
local DOG_KILL   = "dog_kill_"             -- kill-count snapshot prefix
local DOG_TASK   = "dog_task_"             -- "this dog has already set you this task" prefix

-- step = position in the chain; path = the BASE class this dog teaches; hear/answer = the word it responds
-- to and the word you must give back. Keys are NPCs.csv NpcDescription, matched exactly.
local DOGS = {
  ["Mutt"]        = { step = 1, path = 3, hear = "hello",  reply = "Bark!",   answer = "bark"   },
  ["Jindo dog"]   = { step = 2, path = 2, hear = "bark",   reply = "Woof!",   answer = "woof"   },
  ["Hunting dog"] = { step = 3, path = 1, hear = "woof",   reply = "Grrowl!", answer = "grrowl" },
  ["Spotted dog"] = { step = 4, path = 4, hear = "grrowl"                                      },
}

local DOG_ECHOES_NEEDED = 3

-- The eight Dog spells. `mob`/`mobs` + `kills` is the slaying half (snapshotted, so kills banked before the
-- dog asked don't pay for it), `items`/`gold` the goods half; a `keep` tier is SHOWN the item and hands it
-- back rather than sacrificing it. `bigGate` is the atlas's "Must have 20,000 Vita or 10,000 Mana", which
-- every level-99 dog spell carries.
--
-- Numbers are the nexusatlas listing (capture 2002-12-30); where the earlier tswolf capture disagrees the
-- divergence is recorded in Content.cs beside CanLearnDogSpells. RTK's Lua swapped both level-99 kill
-- targets for a Golden lobster and Spirit Fury's Ambrosia for a Titanium glove -- not followed.
local DOG_SPELLS = {
  -- Warrior
  [1] = {
    { key = "greater_blessing", name = "Greater Blessing", level = 70,
      mob = "trapdoor_spider", kills = 3,
      mobName = "three Trapdoor spiders (the Ambushing spiders in the Kugnae Spider Cave)" },
    { key = "spirit_fury", name = "Spirit Fury", level = 99, bigGate = true,
      mob = "spirit_rabbit", kills = 1, mobName = "the Non-Corporeal Bunny, the boss of Rabbit 3",
      items = { { "ambrosia", 1 } }, gold = 10000 },
  },
  -- Rogue
  [2] = {
    { key = "spot_traps", name = "Spot Traps", level = 70,
      mob = "trapdoor_spider", kills = 3,
      mobName = "three Trapdoor spiders (the Ambushing spiders in the Kugnae Spider Cave)" },
    -- tswolf is explicit that the bracelet is SHOWN, "(Not Sacrifice)"; the atlas only says "bring", which
    -- does not contradict it. So this is the one requirement you keep.
    { key = "serpents_fury", name = "Serpent's Fury", level = 99, bigGate = true,
      mobs = { "zinte_ogre", "zangze_ogre" }, kills = 1,
      mobName = "both Zin-te and Zangze, the two Southern Ogre bosses",
      items = { { "whisper_bracelet", 1 } }, keep = true },
  },
  -- Mage
  [3] = {
    { key = "fissure", name = "Fissure", level = 70,
      items = { { "amber", 1 }, { "amethyst", 1 }, { "quartz", 1 }, { "topaz", 1 } } },
    { key = "lava_surge", name = "Lava Surge", level = 99, bigGate = true,
      mob = "ice_panther", kills = 1, mobName = "an Ice panther in the Northern Ogres",
      items = { { "star_staff", 1 }, { "scribes_pen", 1 } } },
  },
  -- Poet
  [4] = {
    { key = "survive", name = "Survive", level = 70,
      items = { { "mountain_ginseng", 10 }, { "pearl_charm", 1 } } },
    { key = "fascinate", name = "Fascinate", level = 99, bigGate = true,
      mob = "ice_panther", kills = 1, mobName = "an Ice panther in the Northern Ogres",
      items = { { "titanium_lance", 1 }, { "purified_water", 1 } } },
  },
}

local BIG_GATE_VITA, BIG_GATE_MANA = 20000, 10000

-- Every dog spell, for "cleanse". RTK also strips the four NPC-subpath signature spells here, and its own
-- comment says that was verified against the live game -- kept.
local ALL_DOG_SPELLS = {
  "greater_blessing", "spirit_fury", "spot_traps", "serpents_fury",
  "fissure", "lava_surge", "survive", "fascinate",
}
local CLEANSE_ALSO = { "hyun_moo_revival", "chung_ryongs_rage", "ju_jak_evocation", "baekhos_cunning" }
local CLEANSE_KARMA = 4.0

-- Speech arrives lowercased and trimmed, but a player may or may not type the exclamation mark the dogs
-- themselves use ("Bark!" vs "bark"), so compare on the bare word.
local function bare_word(s)
  return (string.gsub(s, "[!%.%?%s]+$", ""))
end

-- Kill requirements are DELTAS from the moment the dog SET the task (RTK calls flushKills for the same
-- reason; we have no reset, so we snapshot instead). The task has to be assigned explicitly, in its own
-- registry key, for two reasons: without it the first visit would silently accept a lifetime of old kills,
-- and every later "am I done yet?" visit would re-snapshot and wipe the progress you had made.
local function dog_mobs(tier)
  return tier.mobs or (tier.mob and { tier.mob }) or {}
end

local function assign_kills(ctx, tier)
  for _, mob in ipairs(dog_mobs(tier)) do ctx:setReg(DOG_KILL .. mob, ctx:killCount(mob)) end
  ctx:setReg(DOG_TASK .. tier.key, 1)
end

local function kills_done(ctx, tier)
  if ctx:reg(DOG_TASK .. tier.key) ~= 1 then return false end   -- never asked = never done
  local need = tier.kills or 1
  for _, mob in ipairs(dog_mobs(tier)) do
    if ctx:killCount(mob) - ctx:reg(DOG_KILL .. mob) < need then return false end
  end
  return true
end

local function items_held(ctx, tier)
  for _, it in ipairs(tier.items or {}) do
    if not ctx:hasItem(it[1], it[2]) then return false end
  end
  return true
end

-- "(1) Star-staff\n(1) Scribe's pen" -- the shape RTK's own dogs use for their shopping lists.
local function item_list(ctx, tier)
  local lines = {}
  for _, it in ipairs(tier.items or {}) do
    lines[#lines + 1] = "(" .. it[2] .. ") " .. ctx:itemName(it[1])
  end
  if tier.gold and tier.gold > 0 then lines[#lines + 1] = tier.gold .. " gold" end
  return table.concat(lines, "\n")
end

-- Take the price and teach. Nothing is spent unless the spell actually lands, so a full spellbook cannot
-- silently eat the components.
local function pay_and_teach(ctx, tier)
  if tier.gold and tier.gold > 0 and ctx:coins() < tier.gold then
    ctx:say("You have not brought the " .. tier.gold .. " gold I asked for.")
    return false
  end
  if not ctx:learnSpell(tier.key) then
    ctx:say("Your mind is too full of other secrets to hold this one.")
    return false
  end
  if not tier.keep then
    for _, it in ipairs(tier.items or {}) do ctx:takeItem(it[1], it[2]) end
  end
  if tier.gold and tier.gold > 0 then ctx:spendGold(tier.gold) end
  ctx:notify("Your mind expands as you learn " .. tier.name)
  return true
end

-- The two-stage teach for one spell: slay, then bring.
local function teach_tier(ctx, tier)
  if ctx:level() < tier.level then
    ctx:say("Come back to me when you have gained some insight.")
    return
  end
  if tier.bigGate and ctx:maxHp() < BIG_GATE_VITA and ctx:maxMp() < BIG_GATE_MANA then
    ctx:say("You are not yet strong enough in body or in will to hold a secret of this weight.")
    return
  end

  if #dog_mobs(tier) > 0 and not kills_done(ctx, tier) then
    local asked = ctx:reg(DOG_TASK .. tier.key) == 1
    if not asked then assign_kills(ctx, tier) end
    ctx:say(asked and ("You have not yet slain " .. tier.mobName .. ".")
                   or ("Ah yes, I believe I can help you. First, slay " .. tier.mobName .. "."),
            "Return to me when you have done what I have asked.")
    return
  end

  if not items_held(ctx, tier) then
    ctx:say("Ah yes, I believe I can help you. Bring me the following:\n\n" .. item_list(ctx, tier),
            "Return when you have acquired what I have asked.")
    return
  end

  if tier.keep then
    ctx:say("Show me the " .. ctx:itemName(tier.items[1][1]) ..
            ". ... Good. Keep it -- it was the seeing I needed, not the taking.")
  end
  pay_and_teach(ctx, tier)
end

local function dog_secret(ctx, dog)
  if ctx:karmaTooLow() then return end                      -- RTK Tools.checkKarma

  -- Every dog answers to "secret", but only YOUR class's dog has anything to say. RTK returns in silence
  -- when the pairing is wrong; a line is friendlier and gives away no more than the four dogs' own split.
  if ctx:basePathId() ~= dog.path then
    ctx:say("The dog listens politely, but has nothing to teach your kind.")
    return
  end
  if not ctx:canLearnDogSpells() then
    ctx:say("The dog sniffs at you and turns away. You have given yourself to a path it does not trust.")
    return
  end

  local tiers = DOG_SPELLS[dog.path]
  if not ctx:hasSpell(tiers[1].key) then
    teach_tier(ctx, tiers[1])
  elseif not ctx:hasSpell(tiers[2].key) then
    teach_tier(ctx, tiers[2])
  else
    ctx:say("I have taught you all that I can.")
  end
end

local function dog_cleanse(ctx, dog)
  if ctx:karmaTooLow() then return end
  if ctx:basePathId() ~= dog.path then return end

  local confirm = ctx:menu("Do you wish to clear your mind, giving back what we taught you? It will cost much karma to do so.",
    { "Yes, clear my mind.", "Nevermind." })
  if confirm ~= 1 then return end

  for _, key in ipairs(ALL_DOG_SPELLS) do
    ctx:forgetSpell(key)
    ctx:setReg(DOG_TASK .. key, 0)   -- RTK resets the teach progress too: earning them back is the whole quest again
  end
  for _, key in ipairs(CLEANSE_ALSO) do ctx:forgetSpell(key) end
  ctx:removeKarma(CLEANSE_KARMA)
  ctx:say("It is done.")
end

-- Finishing the chain: the legend, the karma, and the flag that unlocks "secret" at every dog.
local function become_linguist(ctx)
  ctx:setStage(DOG_FLAG, 1)
  ctx:addLegend("Dog linguist (" .. ctx:gameDate() .. ")", DOG_LEGEND, 3, 128)
  ctx:addKarma(1.0)
  ctx:say("You think you could now hold a simple conversation in their language.")
end

function npcs_say.DogLinguistNpc(ctx, speech)
  local dog = DOGS[ctx:npcName()]
  if not dog then return false end
  local word = bare_word(speech)

  if ctx:hasLegend(DOG_LEGEND) then
    if word == "secret"  then dog_secret(ctx, dog);  return true end
    if word == "cleanse" then dog_cleanse(ctx, dog); return true end
    return false                                    -- a linguist has no more chain to walk
  end

  -- The chain. Each dog only listens once the one before it has passed you along.
  if ctx:stage(DOG_LEGEND) ~= dog.step - 1 then return false end
  if word ~= dog.hear and word ~= dog.answer then return false end

  -- The Spotted dog is the end of the chain: one "grrowl" and you are a linguist.
  if not dog.reply then
    ctx:setStage(DOG_LEGEND, dog.step)
    become_linguist(ctx)
    return true
  end

  local echo = DOG_ECHO .. dog.step

  if word == dog.hear then
    ctx:bubble(dog.reply)
    ctx:setReg(echo, ctx:reg(echo) + 1)
    return true
  end

  -- word == dog.answer: the echo only counts once you have heard the word enough times to have learned it.
  if ctx:reg(echo) >= DOG_ECHOES_NEEDED then
    ctx:setStage(DOG_LEGEND, dog.step)
    ctx:setReg(echo, 0)
  end
  return true
end

-- =====================================================================================================
-- THE OLD DOG -- Poet's Restore (nexusatlas /quests/poetsrestore.php; RTK NPCs/wilderness/old_dog.lua).
--
-- RESTORE IS NOT A DOG SPELL. The Dog Linguist legend is only the PREREQUISITE; the spell itself is a
-- poet spell, so the gate is "any poet path" -- Poet, Hyun moo, Druid, Monk, Muse, i.e. basePathId 4 --
-- and NOT Content.CanLearnDogSpells, which exists to keep PC subpaths away from the eight dog spells and
-- would wrongly refuse a Druid or a Muse here. RTK gets this right too (`player.baseClass == 4`).
--
-- Level 99. Click the Old DOG -- the Old man beside him is a decoy and the page says so. He sends you to
-- kill Storm; you come back and click him again to be taught.
--
-- "Do NOT kill anything else along the way" is quoted from the walkthrough and is enforced: the tally is
-- taken when he sets the task, and on your return the ONLY thing you may have killed since is Storm. RTK
-- has no such check. It is made recoverable rather than terminal -- a spoiled hunt re-arms the task
-- instead of ending the quest -- because the page describes a fight players expect to die in.
--
-- Group play is supported and is the intended way to do it (Storm one-shots poets): quest credit follows
-- the experience share, so a grouped poet who is paid for the kill is credited for it. See
-- Session.AwardKillExp. The one thing the page describes that we do NOT reproduce is doing it while DEAD:
-- a dead member draws no exp share here (an existing, deliberate RTK-matching rule), so they get no
-- credit either.

local OLD_DOG_QUEST  = "poet_restore"                            -- 0 = not asked, 1 = hunting Storm
local OLD_DOG_LEGEND = "avenged_treachery_against_the_dogs"
local STORM          = "tiger_storm"
local RESTORE_EXP    = 50000000

function npcs.OldDogNpc(ctx)
  if ctx:hasLegend(OLD_DOG_LEGEND) then
    -- RTK re-grants the spell to anyone who has the legend but not the spell, so forgetting it is not
    -- permanent. Kept: the quest is a once-per-character kill of a boss most poets cannot solo.
    if ctx:basePathId() == 4 and not ctx:hasSpell("restore_poet") then ctx:learnSpell("restore_poet") end
    ctx:say("You have already avenged us.")
    return
  end

  if ctx:basePathId() ~= 4 or ctx:level() < 99 or not ctx:hasLegend(DOG_LEGEND) then
    ctx:say("The dog doesn't seem interested in you.")
    return
  end

  -- Not yet asked: set the task and snapshot BOTH tallies (Storm's, and everything's).
  if ctx:stage(OLD_DOG_QUEST) ~= 1 then
    ctx:setStage(OLD_DOG_QUEST, 1)
    ctx:setReg(DOG_KILL .. STORM, ctx:killCount(STORM))
    ctx:setReg(DOG_KILL .. "any", ctx:totalKills())
    ctx:say("In the Rose palace, you will find Storm. Please slay him and return to me.",
            "Slay nothing else along the way -- come back with his blood on you and no other's.")
    return
  end

  local storm = ctx:killCount(STORM) - ctx:reg(DOG_KILL .. STORM)
  local any   = ctx:totalKills()     - ctx:reg(DOG_KILL .. "any")

  if storm < 1 then
    ctx:say("Return to me after you have slain Storm.")
    return
  end

  if any > storm then
    ctx:setReg(DOG_KILL .. STORM, ctx:killCount(STORM))    -- re-arm rather than end the quest
    ctx:setReg(DOG_KILL .. "any", ctx:totalKills())
    ctx:say("You reek of other blood. The hunt was to be for Storm alone.",
            "Go back. Find him again, and touch nothing else.")
    return
  end

  -- Teach FIRST: learnSpell fails on a full spellbook ("Make sure you have an empty spell slot"), and the
  -- quest must survive that rather than paying out the legend and the fifty million for nothing.
  if not ctx:learnSpell("restore_poet") then
    ctx:say("Your mind is too full to hold what we would give you. Make room, and return.")
    return
  end

  -- "Small Karma increase" (atlas). RTK writes math.random(0.1, 0.3), which in Lua truncates both bounds to
  -- 0 and so awards NOTHING -- a flat mid-range 0.2 is the nearest thing its author clearly meant.
  ctx:addKarma(0.2)
  ctx:awardExp(RESTORE_EXP, true)                    -- takes the totem bonus: 52,500,000 at totem time
  ctx:addLegend("Avenged treachery against the dogs (" .. ctx:gameDate() .. ")", OLD_DOG_LEGEND, 5, 128)
  ctx:setStage(OLD_DOG_QUEST, 0)
  ctx:say("Thank you for avenging us.")
end

-- =====================================================================================================
-- THE SAGE -- the wisdom ladder (RTK NPCs/wilderness/sage.lua, retuned against the archive).
--
-- The old man alone in his room off the Wilderness at 126,7 (map 1230, reached by the warp there). He is
-- the ONLY teacher of Share Wisdom and its four upgrades -- the game's one world chat channel, cast by
-- typing what you want the land to hear. The spells themselves already work (`sage_shout` in
-- spell_verbs.lua, per-tier mana and aether from SpellParams.csv); this is the only way into them, which
-- is why Content.NpcGrantedSpells now keeps the path trainers from teaching Share Wisdom behind his back.
--
-- SOURCE, and where it beats RTK. The dated nexusatlas sage page (2002-12-25, Sources.csv
-- `atlas-2002-12-25-sage`) carries the whole system on one sheet, corroborated by tswolf's spell list and
-- by the tutor-board post "The sage spells". RTK's script disagrees with them on every number:
--
--            archive                                              RTK sage.lua
--   level    90, every path                                       50 to speak, 90 for tiers 2-5
--   price    100,000 a rung, "in total 500k"                      25,000 then 100,000 x4
--   wait     2 yuris (90 days) between rungs                      2/4/6/8 weeks, then none
--   ladder   "only 1 sage level on personal spells list per       addSpell only -- all five pile up in
--            time" -- each rung REPLACES the one below            the book, and its own next-rung search
--                                                                 then finds the lowest and re-sells it
--
-- We follow the archive on all four. The replacement rule is the one that matters most: without it RTK's
-- forward `for i = 1, #sages` search stops at the rung you already outgrew and sells you the same upgrade
-- forever. Searching DOWN from the top instead is what that loop was reaching for.
--
-- ONE SOURCE CONFLICT, called for the dated one. The tutor post exists in two revisions: the earlier says
-- "2 yuris ((90 days))" and the later "(Updated)" one says "1 hyul ((45 days))". The Atlas page is dated
-- 2002-12-25, sits 18 months from our target, and agrees with the EARLIER revision -- and the "(Updated)"
-- revision also talks about Generals and "Fast Sage", which are later-era furniture. 90 days it is.
--
-- WHAT THE RUNGS BUY is not this file's business: `sage_shout` in spell_verbs.lua owns the reach (rungs 1-2
-- sage from the designated areas only, 3-4 also from your own kingdom, 5 from anywhere, and outside its
-- reach the spell becomes Mentor). All this NPC sells is the next rung.
--
-- NOT MODELLED: the punishment rule the rules speech below promises. There is no jail here -- maps 47 and
-- 666 exist, the mechanic does not -- so the speech is era-correct flavour and nothing enforces it. When it
-- is built it lands here: forget the held rung and write now + 90 days into SAGE_TIMER. Also unmodelled:
-- rungs 3-4 are really FOUR per-nation spells each ("Buya, Kugnae, Nagnang, or Neutral Wisdom" / "... or
-- Neutral Sage"); our Spells.csv carries RTK's single Apprentice's/Adept's Wisdom, and only the NAME is
-- missing -- the kingdom behaviour is live. See docs/common/Deferred-Work.md for both.

local SAGE_LADDER = {
  "share_wisdom",        -- 15 min aethers
  "mentors_wisdom",      -- 10
  "apprentices_wisdom",  -- 10  (archive: "Buya, Kugnae, Nagnang, or Neutral Wisdom")
  "adepts_wisdom",       --  5  (archive: "Buya, Kugnae, Nagnang, or Neutral Sage")
  "sages_wisdom",        --  5  -- "virtually anywhere"; the ceiling, and a Sam san requirement
}

local SAGE_LEVEL = 90        -- "It was able to be learned at level 90" / "available to all paths for people
                             -- over the level 90". The atlas ALSO gates the room itself ("Only Level 90+ may
                             -- enter this location"), but Maps.csv keeps map 1230 open from 50 (RTK's own
                             -- figure) BY CHOICE: a player who finds the cave early should meet the Sage and
                             -- be told what it takes, not a wall. This line is that telling -- do not raise
                             -- MapReqLvl to match and make it unreachable.
local SAGE_COST  = 100000    -- flat, every rung: "The cost to learn it is of 100,000 coins and it can be
                             -- upgraded to next level of Sage ... for the same price ... in total 500k"
local SAGE_WAIT  = 90 * 86400   -- two yuris between rungs: "It can be learned every 2 Yuris (90 Days)"
                                -- (Atlas, dated), matching the earlier tutor revision's "every 2 yuris
                                -- ((90 days)) ... 500k and 10 yuris ((450 days))" for the full ladder. A
                                -- REAL-time wait, and the entire cost of the upgrade path beyond the gold
                                -- -- so it is also the one number here most worth retuning by taste.
local SAGE_TIMER = "sage_timer"  -- absolute unix-second deadline (RTK registry "learnSageSpellTimer")
local SAGE_RUNG  = "sage_rung"   -- which rung has been PAID for. The spell in the book is the visible half;
                                 -- this is the half that survives @lvl/@class/@mark/@align, which rebuild
                                 -- the book from scratch and would otherwise confiscate the whole ladder.
                                 -- Read by Content.SageRungReg -> RespecSpellSet, and set by @sage.

-- RTK playerTimerValues, which is what its own "consult with the Sage" line renders: "07 days 03 hours
-- 22 minutes 11 seconds". Kept verbatim in shape -- the tutor post tells players to come and ask him how
-- long is left, so this string IS the documented answer.
local function sage_wait_text(secs)
  return string.format("%02d days %02d hours %02d minutes %02d seconds",
    math.floor(secs / 86400), math.floor(secs % 86400 / 3600),
    math.floor(secs % 3600 / 60), math.floor(secs % 60))
end

-- 100000 -> "100,000". RTK's Tools.formatNumber; the price line is the only place we need it.
local function commas(n)
  local s = tostring(n)
  while true do
    local out, hits = string.gsub(s, "^(-?%d+)(%d%d%d)", "%1,%2")
    s = out
    if hits == 0 then return s end
  end
end

-- The highest rung the player holds (0 = none). Downward, so an old book that somehow holds two rungs
-- reads as the better one rather than re-selling the worse one's upgrade. Deliberately reads the BOOK and
-- not SAGE_RUNG: what you can cast is what you have, so a spell lost to a Forget Secret really is lost and
-- the Sage will sell it again. SAGE_RUNG exists only so a REBUILD can restore what a rebuild wiped.
local function sage_rung(ctx)
  for i = #SAGE_LADDER, 1, -1 do
    if ctx:hasSpell(SAGE_LADDER[i]) then return i end
  end
  return 0
end

function npcs.SageNpc(ctx)
  if ctx:level() < SAGE_LEVEL then
    ctx:say("Come back when you have reached the " .. SAGE_LEVEL .. "th insight.")
    return
  end

  local rung = sage_rung(ctx)
  if rung >= #SAGE_LADDER then
    ctx:say("I have already taught you the highest level of wisdom.")
    return
  end

  -- Checked BEFORE the rules speech, unlike RTK, which reads out all seven pages and only then tells you
  -- to come back in a month. Same rule, asked in the order a person would ask it.
  local left = ctx:reg(SAGE_TIMER) - ctx:now()
  if left > 0 then
    ctx:say("You have your spell for now, and I will not let you upgrade again for " .. sage_wait_text(left) .. ".")
    return
  end

  -- RTK's rules, verbatim but for its own server's name. The player may ask for them again as often as
  -- they like (RTK recurses into the whole script; we just loop the pair).
  while true do
    ctx:say("Read the following rules very carefully, for if you should break one then you will lose this spell for a long time!",
            "Share wisdom is for you to share your wisdom with the community.",
            "Use of the spell in any way to offend or harass anyone in the game will result in its loss.",
            "Repeated spamming of your wisdom to the world can result in the loss of this spell.",
            "Keep personal grievances out of sage.",
            "Jailing for ANY crime will result in loss of this spell.",
            "Breaking any other law in Nexus using this spell will result in loss of the spell.")

    local understood = ctx:menu("Do you understand these rules completely?",
      {"Yes, I understand and accept the rules.", "No, please repeat them to me."})
    if understood == 1 then break end
    if understood ~= 2 then return end          -- closed the box
  end

  if ctx:menu("This spell will cost " .. commas(SAGE_COST) .. " gold to learn.",
              {"Yes, I have the money.", "No, I will not pay."}) ~= 1 then
    return
  end

  if ctx:coins() < SAGE_COST then
    ctx:say("The spell I will teach you cost " .. commas(SAGE_COST) .. " gold. Return to me when you have that.")
    return
  end

  -- Replace, don't stack -- one rung at a time, the new one instead of the one you hold. Teach BEFORE
  -- forgetting so no failure can leave you holding neither: only if the book is full (52 slots) is the old
  -- rung's slot freed and the teach retried, which then fits by definition because it is a swap. If even
  -- that fails the old rung goes straight back, and nothing has been charged either way.
  local next_rung = SAGE_LADDER[rung + 1]
  local taught = ctx:learnSpell(next_rung)
  if not taught and rung > 0 then
    ctx:forgetSpell(SAGE_LADDER[rung])
    taught = ctx:learnSpell(next_rung)
    if not taught then ctx:learnSpell(SAGE_LADDER[rung]) end
  end
  if not taught then
    ctx:say("Your mind is too full of other secrets to hold this one.")
    return
  end
  if rung > 0 then ctx:forgetSpell(SAGE_LADDER[rung]) end   -- no-op if the retry above already did it

  ctx:spendGold(SAGE_COST)
  ctx:setReg(SAGE_RUNG, rung + 1)     -- what a character rebuild hands back; see Content.SageRungReg
  ctx:setReg(SAGE_TIMER, ctx:now() + SAGE_WAIT)
  ctx:say("Use your spell well, abuse will result in its loss, and you will end up having to learn the spells again from the start.")
end

-- =====================================================================================================
-- YIN, YANG AND VOID -- the three prophets of the Mage's Spirit Stone (RTK NPCs/nagnang/
-- mage_stone_prophets.lua). See Server/MageStoneQuest.cs for the whole chain and its sources.
--
-- Three NPCs (NPCs.csv 134/135/136) share the MageStoneProphetsNpc identifier, one to a cell off the
-- Prophets room (2571/2572/2573), so this branches on npcName() exactly as RTK branches on npc.mapTitle.
--
-- The gate is the offering, and nothing else: `zapped_<mouse>` is set by game-data/mob_ai.lua when that
-- prophet's immortal mouse is struck WHILE VEXED. A prophet whose mouse you have not offered will not
-- speak to you at all -- which is the only thing stopping you from walking the three cells and collecting
-- all three tasks without casting anything.
--
-- Wand warns that cursing MORE than one mouse before a door also costs you the audience. That is not
-- enforced: RTK does not enforce it either (its flags are per-mouse, so cursing all three simply sets all
-- three), no archived source describes what the failure actually looked like, and the only way to make it
-- stick would be to invent a reset. The line stays in Wand's mouth as the instruction it is.

local MAGE_STONE_MICE = {
  ["Yin"]  = "yin_mouse",
  ["Yang"] = "yang_mouse",
  ["Void"] = "void_mouse",
}

local MAGE_STONE_WORDS = {
  ["Yin"] = {
    "Ah, someone who shows great wisdom and follows simple directions. That speaks well of you.",
    "You wish to become one of the Nagnang Mages, eh? Well it is more about being a mage at heart than of a town.",
    "Magic is not about killing and destroying. It is also about compassion and beauty. This is the quest that I bestow upon you.",
    "Find a rose. That is the simplicity and beauty of my request. Keep that rose with you and give it to Wand when you have done the other's bidding.",
  },
  ["Yang"] = {
    "I see that you have learned your lesson as to how to greet us in the caves. Good. Good.",
    "So you want the power that comes with the Mages of Nagnang, do you? Well power is what we are about.",
    "Magic has its soft side but it has the strength and power to destroy and conquer. It can be stronger than any weapon or blade.",
    "To show this, get yourself a piece of high Ore, and keep it with you until you complete all the other's quests and return to Wand.",
  },
  ["Void"] = {
    "Ah, you had the clarity of mind to follow that which Wand told you. That is good.",
    "Understanding the void, the absence of everything, is the most important part of being a mage of Nagnang.",
    "The person who understands the Void better than anyone are the dead. Walk through the graves in the Crypts of the Cemetery and glean their knowledge.",
    "Afterwards, return to Wand with whatever the others have you find and you will be rewarded with the stone.",
  },
}

function npcs.MageStoneProphetsNpc(ctx)
  local mouse = MAGE_STONE_MICE[ctx:npcName()]
  if not mouse then return end                    -- a fourth prophet would be a data error, not a silent NPC

  if ctx:reg("zapped_" .. mouse) ~= 1 then
    ctx:say("You have not shown me an offering. I will not chat with you.")
    return
  end

  local w = MAGE_STONE_WORDS[ctx:npcName()]       -- four pages each, indexed rather than unpacked
  ctx:say(w[1], w[2], w[3], w[4])
end
