# Quest & Legend Registry

The reference sheet for `@quest` and `@legend`: every persistent quest key a character can carry, what
its stages mean, and every legend mark with the internal key it answers to. Compiled from the server
code and `game-data/npc_dialog.lua` (2026-08-28); when this sheet and the code disagree, the code wins —
grep the key string.

## How quest state is stored

A character carries four stores (all persisted in the character JSON, all survive a relog):

| Store | Shape | What lives there |
|---|---|---|
| `Character.Quests` | `string → int` | Stage machines, flags, counters, kill-count snapshots and unix-second timers — **one flat map**, read and written only through `Server/QuestState.cs` (see [Names in code](#names-in-code-queststate)). The keys below are the SAVED keys, which is what `@quest` shows and takes. |
| `Character.QuestStrings` | `string → string` | The rare string-valued selections (active minor quest, mentor's name) — the string registry, also read and written only through `Server/QuestState.cs`, as **text slots**. |
| `Character.Legends` | list of `{Icon, Color, Text, Name}` | Profile-window marks. The client only ever sees `Text`; `Name` is the hidden key quests gate on (`HasLegend`), and adding is replace-by-key. |
| `Character.Kills` / `KillTrack` | tallies | Lifetime kills per mob key (quests read a *delta* against a snapshot), and the 8-slot kill track the mythic alliances count. |

### Names in code (`QuestState`)

C# never spells a saved key. A quest reads and writes through `ctx.Quest(<namespace>)` (NPC scripts) or
`Session.Quest(<namespace>)`: the namespace is the quest's stage key, and every other value is a **slot** in
it — `flag.<name>`, `kills.<mob>` (a kill-count snapshot), `timer.<name>` (unix seconds), `count.<name>`,
and `text.<name>` for a value in the string registry (read with `GetText`/`SetText`; asking either map's
pair for the other's slot throws). An explicit alias table in `Server/QuestState.cs` maps each slot to the
key characters were already saved under, so nothing was migrated (#51, #309). A slot the table does not list
throws rather than reading 0. The flat namespace, `QuestState.Registry`, is RTK's `player.registry`: the name
IS the saved key, in either map. The Lua `stage`/`setStage`/`reg`/`setReg` verbs (npc_dialog.lua), the spell
verbs' `reg`/`setReg`, `mob_ai.lua`'s `actorQuest`, `@quest`'s raw keys, and the registry globals several
features share (`home`, `sage_rung`, `sage_timer`, `dog_flag`, `carnage_wins`, `mentored`, `craft_*`,
`damage_shotgun`, `baekhos_cunning`) all live there.

| Namespace (= stage key) | Slot | Saved key |
|---|---|---|
| `tutorial_quest` | `flag.gave_gold` · `flag.gave_meat` · `flag.gave_sword` | `tutorial_quest1_gave_gold` · `tutorial_quest2_gave_meat` · `tutorial_quest8_gave_sword` |
| `tutorial_quest` | `flag.learned_to_fish` · `flag.talked_to_tutor` · `flag.helped_haguru` · `flag.visited_yon_and_weaved` | `learned_to_fish` · `talked_to_tutor` · `helped_haguru` · `visited_yon_and_weaved` |
| `tiger_armor` | `flag.met_claw` · `level.nudged` | `tiger_essence_met_claw` · `tiger_essence_nudged_level` |
| `novice_quest` | `kills.rabbit` · `kills.squirrel` · `flag.gave_garb` · `flag.asked_soothe` | `novice_quest1_rabbit_snapshot` · `novice_quest2_squirrel_snapshot` · `novice_quest2_gave_garb` · `novice_quest3_asked_soothe` |
| `mage_stone` | `flag.met_ghost` · `flag.zapped.{mouse}` | `mage_stone_met_ghost` · `zapped_{mouse}` |
| `minor_quest` | `tier` · `timer.cooldown` · `count.completed` · `kills.{mob}` · `text.target` | `minor_quest_tier` · `minor_quest_timer` · `minor_quests_completed` · `minor_quest_kill_count_{mob}` · `minor_quest` (string registry) |
| `mentorship` | `text.mentor` | `mentor` (string registry) |
| `nagnang_warrior_trial` | `kills.forbidden` | `nagnang_trial_kills` |
| `nangen_acolyte` | `flag.gave_pipe` · `timer.water` · `flag.destroyed_infected` · `kills.magic_rabbit` | `gave_sonhi_pipe` · `sacred_water_timer` · `destroyed_infected` · `nangen_rabbit_base` |
| `sute_quest` | `flag.dye` · `timer.recoat` | `sute_quest_dye` · `sute_quest_timer` |
| `totem_worship` | `timer.daily` · `count.karma_misses` · `count.worships` | `totem_worship_daily_timer` · `totem_worship_karma_force` · `totem_total_worships` |
| `sun_armor` | `count.totems` | `sun_armor_totem` |
| `white_moon_axe` | `kills.pale_scorpion` · `kills.skeleton_ju` | `wma_scorpion_base` · `wma_ju_base` |
| `{tier}_armor` | `step.{n}.opened` · `step.{n}.total_kills` · `step.{n}.kills.{mob}` | `aq_{tier}_{n}_!` · `aq_{tier}_{n}_@` · `aq_{tier}_{n}_{mob}` |

Stage-only namespaces need no row: `leviathan`, `dagger_uniform`, `forgotten_path`, `lesser_alliance_{animal}`,
`newbie_area_quest`, and the stage of every namespace above. A new slot is added to the table mapped to its
own dotted name (`leviathan.flag.x`); no saved key contains a dot, so a new key can never collide with an old
one. `Tests/QuestStateTests.cs` pins every row against the constants each quest spelled before it was a slot,
round-trips a character blob carrying all of them (`Tests/Fixtures/character-quests-v1.json`), and walks every
armor chain's steps and every `MinorQuests.csv` mob through the `{tier}_armor` and `kills.{mob}` families, so a
data row they cannot resolve (a mob key with a dot in it) fails the test run instead of a dialog.

**Most chains gate on the legend, not the stage.** Clearing only the stage usually re-tests nothing —
the door/NPC checks the mark. A full quest reset is generally: clear the legend(s), zero the stage
key(s), remove any leftover quest item.

## Tester commands

- `@quest` — dump every key you carry (both maps). `@quest <key>` reads one; `@quest <key> <n>` sets it
  (0 removes the entry); a non-numeric value writes the string registry.
- `@legend` — list your marks as `key: "text" (icon, color)`. `@legend <key> 0` removes one;
  `@legend <key> <icon> <color> <text...>` (re)creates one, replace-by-key. The seeded "Born in …" mark
  has no key and can't be addressed (deliberately).
- Purpose-built shortcuts that manage several of these at once: `@dog [0|1]`, `@sage [0-5]`,
  `@carnage <name> [n]`, `@killtrack [clear]`.

### Recipe: re-run the Leviathan quest

```
@legend leviathan_freed 0
@quest leviathan 0
```

Dae-Whan now treats you as new and hands out a fresh talisman (remove a leftover one first if you kept
it — it can't be dropped, so bank it or ask a GM). To jump straight to the *finished* state instead:
`@quest leviathan 3` and `@legend leviathan_freed 7 128 Freed Leviathan (Yuri 1, Winter)`.

## Quest keys (`Character.Quests`)

Kinds: **stage** = a stage machine (0 = untouched), **flag** = 0/1, **counter** = accumulates,
**snapshot** = a kill count captured at accept-time (completion checks the delta against
`Character.Kills`), **timer** = unix seconds.

### Story chains

| Key | Kind | Values | Feature / notes |
|---|---|---|---|
| `leviathan` | stage | 1 asked (talisman in hand) · 2 captive freed · 3 said "dae-whan" to the Hermit (shop open forever) | `LeviathanQuest`. Stage ≥1 opens the Border patrol's pelt bribe; the Hermit door and second-free block gate on the **legend** `leviathan_freed`, not the stage. |
| `tutorial_quest` | stage | 0-14: 1 buy armor · 2 sell meat · 3 rose+chestnuts · 4 fishing · 5 exploration · 6 ogre cider · 7 Chu Rua · 8 group hunt · 9 spelunking · 10 riding · 11 missing brother · 12 better weapon · 13 student cap · 14 done | Peasant tutor chain (`TutorialQuest`). Helpers below. |
| `tutorial_quest1_gave_gold`, `tutorial_quest2_gave_meat`, `tutorial_quest8_gave_sword` | flag | 1 = that stage's handout was given (first two reset on stage clear) | tutorial handout guards |
| `learned_to_fish`, `talked_to_tutor`, `helped_haguru` | flag | 1 = sub-task done (`talked_to_tutor` / `helped_haguru` are consumed back to 0 on turn-in) | tutorial stages 4 / 5 / 11 |
| `visited_yon_and_weaved` | flag | read at stage 13 but **nothing ever writes it** — the wool→Yon→cloth chain is unported | dead branch, documented |
| `tiger_essence_nudged_level` | level-stamp | the level at which the "your tiger armor looks outdated" nudge last fired | Tiger Essence tutor branch |
| `novice_quest` | stage | 0 saber given → 1 five rabbits → 2 garb + five squirrels → 3 learn Soothe → 4 done | Tutor first-steps chain (era-gated). Helpers: `novice_quest1_rabbit_snapshot`, `novice_quest2_squirrel_snapshot` (snapshots), `novice_quest2_gave_garb`, `novice_quest3_asked_soothe` (flags). |
| `newbie_area_quest` | stage | 0-10 across the newbie maps (4712-4718); stages also unlock four warps (`WarpQuestLocks.csv`) | Newbie area. Helpers: `newbie_rabbit_snapshot`, `newbie_squirrel_snapshot`, `newbie_mignok_told`, `newbie_coords_learned` (the 50-exp tile on map 4714). |
| `tiger_armor` | stage (level-valued) | holds the rung you are ON: 0·10·20·30·40·50·60·70 = done; "i lost my tiger mail" resets to 0 | Claw / Tiger mail ladder |
| `tiger_essence_met_claw` | flag | 1 = Claw has engaged you (also set by the level-up push); releases the tutor's repeated briefing | Tiger Essence |
| `forgotten_path` | stage | 1 clicked "Forgotten past" · 2 blossoms · 3 sent to Buya · 4-6 the Storm Shaman · 7 Gruff · 8-9 Thane · 10 carrying the (invisible) strange metal · 11 Rotah told · 12 examination passed; **reset to 0 on completion** | Geomancers' orbs (`ForgottenPastQuest`). The one-time gate is the **legend** `forged_orb`, never the stage. |

### Armor chains (Star / Moon / Sun)

| Key | Kind | Values | Notes |
|---|---|---|---|
| `star_armor`, `moon_armor`, `sun_armor` | stage | index of the current step in that chain; reset to 0 on completion (the **legend** is the durable record) | 12 chains: 4 paths × 3 tiers |
| `aq_{tier}_{step}_!` | flag | 1 = step opened, snapshots taken; cleared on completion | |
| `aq_{tier}_{step}_@` | snapshot | total kills at step open — enforces "kill nothing else" steps | |
| `aq_{tier}_{step}_{mobKey}` | snapshot | per-mob kills at step open | |
| `mentored` | counter | completed mentorships (Poet Moon needs ≥3). **Also a legend name** — see below | `Mentorship` |
| `carnage_wins` | counter | Carnage victories; only `@carnage` writes it (Warrior Sun needs ≥2) | |
| `sun_armor_totem` | counter 0-4 | progress through the Chung ryong→Baekho→Ju Jak→Hyun moo worship order (Poet Sun needs ≥4; cleared on pay) | `TotemWorship` |
| `craft_tailoring`, `craft_metalworking`, `craft_woodworking` | counter | manufacturing skill points (Adept = 3910/2040/2250). **Nothing writes them** — Poet Sun step 5 is a documented hard stop | |
| `white_moon_axe` | stage | 1 show a Whisper bracelet · 2 five Pale scorpions · 3 Skeleton Ju · 4 pay 20,000; **reset to 0 on completion** (repeatable, no legend) | Rogue White Moon Axe at Buya's Maso (`WhiteMoonAxeAbility`). Axe is handed out UNBONDED; Rogue Moon bonds it. |
| `wma_scorpion_base`, `wma_ju_base` | snapshot | Pale scorpion / Skeleton Ju kills when that step was asked; cleared on completion | |

### Repeatables & services

| Key | Kind | Values | Feature |
|---|---|---|---|
| `minor_quest_tier` | tier | 1 Minor · 2 Major · 3 Epic (cleared on finish) | Minor quests. Active target lives in the **string** registry, below. |
| `minor_quest_timer` | timer | cooldown end (abandon or complete) | |
| `minor_quests_completed` | counter | lifetime completions. **Also a legend name** | |
| `minor_quest_kill_count_{mobKey}` | snapshot | per-target snapshot at accept | |
| `lesser_alliance_{animal}` | stage/flag | 1 = accepted; reset to 0 on completion (the legend of the same name replaces it) | Mythic alliances — 12 animals (rat, horse, dragon, dog, rabbit, rooster, snake, pig, sheep, ox, tiger, monkey) |
| `totem_worship_daily_timer` | timer | next worship allowed (21 h) | Totem worship |
| `totem_worship_karma_force` | counter | pity counter — forced grant at 5 missed rolls | |
| `totem_total_worships` | counter | lifetime worships (no consumer yet) | |
| `sute_quest_dye` | flag | 1 = powder applied, unspent; spent by the cave mouth (maps 441-447) | Sute's cave |
| `sute_quest_timer` | timer | 1 h re-coating block; zeroed on turn-in | |
| `home` | enum | 0 nation taverns · 10 Sanhae · 11 Hausson; set by the two mayors, force-cleared on nation change | Return / yellow scroll destination |
| `sage_rung` | counter | which Sage rung is PAID for (survives book rebuilds); `@sage` writes it | Sage wisdom ladder |
| `sage_timer` | timer | 90-day upgrade wait; `@sage` zeroes it | |

### Dog Linguist & dog spells

| Key | Kind | Values | Notes |
|---|---|---|---|
| `dog_linguist` | stage | 1 Mutt → 2 Jindo → 3 Hunting → 4 Spotted = done. **Also the legend key** — `@dog` manages both plus `dog_flag` | |
| `dog_flag` | flag | 1 = chain finished; gates saying "secret" to your class's dog | |
| `dog_linguist_echo_{step}` | counter | barks heard at this step (3 needed before echoing advances) | |
| `dog_task_{spellKey}` | flag | 1 = that dog set you this spell's task ("never asked = never done"); `cleanse` zeroes them | keys: `greater_blessing`, `spirit_fury`, `spot_traps`, `serpents_fury`, `fissure`, `lava_surge`, `survive`, `fascinate` |
| `dog_kill_{mobKey}` | snapshot | kills at task-assignment | |
| `poet_restore` | stage/flag | 1 = hunting Storm; reset 0 on completion (legend `avenged_treachery_against_the_dogs` is the record) | Old Dog. Helpers: `dog_kill_tiger_storm`, `dog_kill_any` (total-kill snapshot — killing anything else re-arms the hunt). |

### PC subpaths

| Key | Kind | Values | Notes |
|---|---|---|---|
| `subpath_elder` | enum | the path id (10-21) of the PC subpath this character is an elder of; 0 or absent = none. Counts only while it is still the character's path | Written by `@subpath elder` (GM); cleared by joining and by leaving (`@subpath admit` / `leave` / `expel`), and by `@class` to another path. See `Server/Session.Subpaths.cs` |

### One-off oddities

| Key | Kind | Notes |
|---|---|---|
| `chu_rua_rabbit_greeted`, `chu_rua_tiger_gone` | flag | Chu Rua: greet the rabbit, lure the tiger ("Forest"), then the ginseng tile on map 1116 works |
| `myung_suck_threshold` / `_tries` / `_spoken` | misc | the talking rock: a rolled 3-5 threshold, greetings so far, and "it has spoken once" |
| `paid_gold_for_frost_sabre` | flag | paid Blood 100 gold, owes an ice heart; reset when the sabre is forged |
| `damage_shotgun` | counter | per-swing damage tally for the weapon proc (persisted incidentally) |
| `baekhos_cunning` | counter | the spell's climb tier |

### String registry (`Character.QuestStrings`)

| Key | Values | Feature |
|---|---|---|
| `minor_quest` | the active target's key from `MinorQuests.csv` (`squirrel`, `rabbit`, `deer`, …); `""` = none | Minor quests (slot `minor_quest` / `text.target`) |
| `mentor` | on the protégé: the mentor's character name; `""` = free | Mentorship (slot `mentorship` / `text.mentor`) |

## Legend marks (`Character.Legends`)

`@legend <key> <icon> <color> <text...>` recreates any of these; color 128 is the usual white, color 0
renders invisible. "Never removed" means no code path removes it — `@legend <key> 0` always can.

| Key | Text template | Icon | Color | Granted by | Gates / effect | Removed by |
|---|---|---|---|---|---|---|
| *(no key)* | `Born in {date}` | 0 | 128 | character creation | profile filler | nothing (keyless by design) |
| `leviathan_freed` | `Freed Leviathan ({date})` | 7 | 128 | Dae-Whan at stage 2 | opens the Hermit's hut door; blocks freeing a second captive; suppresses re-asking | never |
| `leviathan_sworn_enemy` | `Sworn enemy of the Leviathans ({date})` | 7 | 4 | refusing Dae-Whan, **or killing any leviathan while on the quest** (`mob_ai.lua`) | blocks all Dae-Whan dialog; makes the talisman inert | pay him 1,000,000 coins |
| `blessed_by_the_stars` | `Was blessed by the stars ({date})` | 3 | 128 | drop a white amber in the Mythic Nexus centre (map 41), level ≥60 | prerequisite for every Star chain | never |
| `mastered_the_stars` | `Mastered the stars ({date})` | 5 | 128 | finishing any Star chain | prereq for Moon and Sun; guildmaster refuses "star" again | never |
| `understood_the_moon` | `Understood the moon ({date})` | 5 | 128 | finishing any Moon chain | prereq for Sun | never |
| `survived_the_sun` | `Survived the sun ({date})` | 5 | 128 | finishing any Sun chain | also short-circuits Moon as "already done" | never |
| `slew_mighty_sute` | `Slew the mighty Sute ({date})` | 5 | 16 | hand Eldritch `sutes_key` | stops the turn-in re-firing (a repeat run keeps the key) | never |
| `forged_orb` | `Forged an orb of {Element} ({date})` | 6 | 128 | Rotah, on paying an orb's materials | **one orb per character, ever** — every branch of the chain goes silent once it is held | never |
| `minor_quest_info` | `On a quest to slay the {target}` | 5 | 128 | accepting a minor quest | informational | complete or abandon |
| `minor_quests_completed` | `Completed {n} minor quests` | 5 | 128 | each completion (rewritten in place) | tally display | never |
| `mentored` | `Mentored {n} new player(s)` | 3 | 1 | mentorship culmination | cosmetic (the gate is the registry counter) | never |
| `mentored_by` | `Mentored by {name} ({date})` | 3 | 1 | culmination, on the protégé | **one per life** — disqualifies ever being mentored again | never |
| `being_mentored_by` | `Being mentored by {name}` | 3 | 1 | protégé accepts | in-progress marker | replaced by `mentored_by` |
| `lesser_alliance_{animal}` ×12 | `Lesser alliance with the {Animal} ({date})` | 5 | 128 | completing that alliance | that mythic casts Rebirth on you forever; your **enemy's** mythic Stormstrikes + banishes you | never |
| `greater_alliance_{animal}` ×12 | — | — | — | **nothing grants these yet** — the enemy-ally check already honours them | n/a |
| `engaged` | `Engaged to {name} ({date})` | 6 | 1 | proposal accepted (both parties) | blocks a second proposal; 3-day timer | marriage, or breaking it off |
| `married` | `Married to {name} ({date})` | 6 | 1 | the ceremony (both parties) | blocks proposals and remarriage | divorce (also takes the `love` item) |
| `{align}_{class}_since` ×12 | `{Kwi-Sin\|Ming-Ken\|Ohaeng} {Class} since ({date})` | path id 1-4 | 128 | alignment swap (shrines, summit, `@align`) | devotion record | the next swap strips all twelve first |
| `dog_linguist` | `Dog linguist ({date})` | 3 | 128 | the Spotted dog (or `@dog 1`) | unlocks `secret`/`cleanse` at every dog; prereq for the Old Dog | `@dog 0` |
| `avenged_treachery_against_the_dogs` | `Avenged treachery against the dogs ({date})` | 5 | 128 | Old Dog turn-in | done-marker; Restore re-taught free if forgotten | never |
| `aided_chu_rua` | `Aided Chu Rua ({date})` | 5 | 128 | hand the turtle young ginseng | completes tutorial stage 7; stops re-offering | never |
| `defeated_ice_beast` | `Defeated the Ice beast ({date})` | 5 | 128 | Blood forges the Frost sabre | completes tutorial stage 12 | never |
| `specialized_in_weaving` | `Specialized in Weaving` | 7 | 128 | Laptev/Yon, level ≥25, 500 gold | the specialisation itself — only one may be held | abandon (two confirmations) |
| `recently_specialized_weaver` | `Recently specialized weaver` | 64 | 128 | paired with the above | cosmetic companion | abandon |
| `specialized_in_smelting` / `_gemcutting` (+ `recently_*`) | — | — | — | **no granter ported yet** — the weaver already refuses to double-specialise over them | abandon path exists |

## Footnotes

1. **Double-duty strings.** `dog_linguist`, `mentored` and `minor_quests_completed` are each both a
   registry key and a legend name. They live in different stores, so there's no collision — but when
   resetting, remember to hit both (`@quest dog_linguist 0` *and* `@legend dog_linguist 0`).
2. **Read-but-never-written** (safe to ignore, listed so nobody hunts for a granter):
   `visited_yon_and_weaved`, the three `craft_*` skill counters, the `greater_alliance_*` and
   smelting/gemcutting-specialisation legends.
3. **Not character quest state, despite the look of it:** `Era.*` names (`druid_bouquet_quest`, …) are
   server-wide era flags from `EraFeatures.csv`; `chin_baek_ho_ryung` / `chung_ryongs_rage_ac` are
   timed-effect status keys; `fox_charm` is an item. The lifetime-total kill counter is
   `Character.Kills[" total"]` (leading space, so no mob key can collide).
4. **The armor-chain legends are code-defined** (`ArmorQuest.cs`), not CSV columns —
   `ArmorQuests.csv` holds only the Path/Tier/Level/Karma gates.
