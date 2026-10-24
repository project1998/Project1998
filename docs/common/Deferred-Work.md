# Deferred work — known gaps, deliberately not built yet

Things we found, understood well enough to build, and chose not to build at the time. This is **not** a bug
list and **not** a wishlist: every entry is something whose absence we have already confirmed, with the
source that documents what the real behaviour was, so that picking it up later is implementation rather
than research.

Rules for this file:

* **Only things we actually investigated.** If we never looked, it does not belong here.
* **Cite the source.** RTK path + line, an archive URL, or a walkthrough screenshot. The point of the entry
  is that the next person does not repeat the digging.
* **Say why it was deferred**, so a stale reason is visible as stale. "Out of scope" ages badly on its own.
* **Delete entries when they land.** A done item's record is the code and the commit, not this file.

Related: `docs/common/Era-Gating.md` (whether a thing should exist *at our date* — a different question from whether
it is built), `docs/common/Crafting-Values.md`.

---

## Karma

The karma system itself is built (`Server/Karma.cs`, `Tests/KarmaTests.cs`) — fractional score, the named
ladder, `Meets` gates, `AddKarma`/`RemoveKarma`, and RTK's `Tools.checkKarma` scum floor, with Lua bindings.
What is missing is almost everything that *uses* it.

### Nothing removes karma

The only call site in our content is Chu Rua's `addKarma(1)`. Karma is therefore monotonically increasing:
the Rat and Snake bands and the `<= -3` scum floor are unreachable, and `KarmaTooLow()` is wired into the
Chu Rua speech NPCs but can never fire. RTK's drains, none of which are ported:

| Source | Amount | RTK path |
|---|---|---|
| Class trainers, failed oath | -3 (mage/rogue/warrior), -5 (poet) | `NPCs/Common/*_trainer.lua` |
| Dog linguist | -4 | `NPCs/Common/dog_linguist.lua:433` |
| Monk `forgive` | variable penalty | `Spells/Subpaths/Monk/forgive.lua:50` |
| Faerie light | -25 to -30 | `Scripts/faerieLight.lua:19` |
| Fishing (level 15+ branch) | -0.001 | `NPCs/Common/fishnpc.lua:78` |

### Star / Moon / Sun armour chains

RTK's four class trainers each run a three-tier armour questline, and **every numbered step re-checks
karma** before advancing (`"Your soul is too impure to master the stars. Improve your karma and return."`).
None of it is ported — our trainers have no armour chain at all. The gates, from
`NPCs/Common/{mage,poet,rogue,warrior}_trainer.lua`:

| Tier | Warrior | Rogue | Mage | Poet |
|---|---|---|---|---|
| Star (3 steps) | Rabbit (2) | Rabbit (2) | Rabbit (2) | Rabbit (2) |
| Moon (4–5 steps) | Dog (3) | Dog (3) | Ox (6) | Ox (6) |
| Sun (5–8 steps) | Tiger (11) | Bear (8) | Tiger (11) | Bear (8) |

The check sits *inside* each step, after the intro dialog and before the objective is given or completed, so
dropping below the tier mid-chain stalls you where you stand rather than locking the door.

### Three other karma gates

* **Exp Seller, `"kawlana"`** — Spirit (19), part of the wind-armour chain (`ExpSeller.lua:395`). Note it is
  a *hidden* gate: failing it gives `"I really have no idea what you are talking about."`, the same line as
  missing the prerequisite quests. Do not "improve" that into a helpful message; the opacity is the design.
* **Monk `forgive`** — Dragon (14) (`Monk/forgive.lua:21`). The spell that restores someone else's karma
  costs high karma to cast.
* **Faerie light** — Angel (30) (`faerieLight.lua:7`), the top of the ladder, and the biggest sink in the
  game: casting it drops you roughly Angel → Bear.

That last pair is the shape of the system — karma is a currency you spend, not only a badge.

### Karma is not displayed

The 4.95 `0x39` self-profile grammar has no karma field and the client cannot render one (see
`Session.SendSelfProfile`). Feedback is the minitext + sparkle on change, as in RTK. `Karma.LevelName()`
exists for dialog and GM inspection. If we ever surface it, note that RTK's smallest penalty (0.001) is
3,000× too weak to move a real tier yet still flips the displayed name Cat → Rat instantly — a small dead
zone around zero would be worth adding at that point, not before.

---

## Fishing

Only the under-15 branch of `NPCs/Common/fishnpc.lua` is ported (`FishAbility`, the "You're still a
youngin'!" path with a flat 25% catch). The **level 15+** branch is absent entirely: borrowing gear, the
four-pole / four-bait purchase menus (1/10/100/1000 and 0/5/50/500 gold), the 10-base-health minimum, and
the two "laziness" penalty rolls (1-in-10 lose 1 base health; else 1-in-40 lose 0.001 karma).

---

## Chu Rua / By the Sea

The quest is complete and era-accurate as of the tswolf (Jan 2001) and nexusatlas passes. Two known gaps:

* **The tiger stays visible after being scared off.** RTK warps the player to map 1117, a tiger-free copy of
  the Tiger Pass; `TK1117.map` does not exist in `game-data/maps/`, so `Session.TryGinseng` gates on the
  `chu_rua_tiger_gone` flag instead and leaves the player on 1116. Consequence: you pick the root while the
  tiger you just sent south is still standing there, and because the flag is permanent the "too dangerous"
  gate never re-arms on a later visit. Fix needs either the map or per-player mob despawn.
* **The Lost Legend mermaid song** (`chu_rua.lua:121-194`, the `"humm dee do dum do hee"` branch) is not
  ported. This is Chu Rua's *second*, unrelated role — a link in the Lost Legend chain that ends in the
  Legend of the Winds and Kawlana, nothing to do with By the Sea. It gates on `quest["wind_armor"]` and
  `quest["min_song_asked"]`, and failing that gate gives the same deliberately opaque `"I really have no
  idea what you're talking about."` the Exp Seller uses — do not "improve" it into a helpful message.

  The chain: a treasure chest in Buya hums a wordless tune (`"Humm dee do dum do hee"`) → say it to Min in
  Hausson, then to Chu Rua → he sings five lines of lyrics → sing them back to the chest one line at a
  time, each advancing `chu_rua_song_stanza`, and the fifth grants the `lost_legend` legend
  (`NPCs/buya/lost_legend_chest.lua:61-110`).

  **Port the randomisation with it, or the puzzle is pointless.** `chu_rua_song` is rolled 1 or 2 on first
  asking and *persisted*, and the two variants of line three differ by one word — "a story **been**
  retold" vs "a story **is** retold". The chest rebuilds its expected array from the player's own saved
  variant, so a borrowed transcript fails on line three. It is an anti-sharing measure disguised as a typo,
  and the period tutor guide knew the symptom without the cause: *"Copy down what the turtle sings. (Be
  sure to have every letter the same!)"* (`boards_tutors/by_category/quests.md`, step 23).

  Chu Rua's own part is ~30 lines; the blocker is everything around it — Min, the chest, Gloth, Pond, the
  Sunset Weaver and the wind-armour chain, none of which exist here.

---

## Level caps on quests — UNVERIFIED, single source

nexusatlas lists By the Sea as `Level Cap: 15 and lower`. Nothing else we have supports it:

* the Jan 2001 tswolf page — the era-correct one — gives only the level 3 minimum and never mentions a cap;
* RTK's `chu_rua.lua` has no level check at all;
* the only `<= 15` anywhere in RTK is `fishnpc.lua:24`, the fishing NPC's under-15 branch, which is a
  different thing that happens to share the number;
* the scraped board archive has no "level cap" phrasing and no By the Sea walkthrough at all.

**This was first written up here as "a later-era addition". That was an overstatement and is why the entry
now leads with UNVERIFIED.** The argument does not hold in either direction: RTK's Lua is a fan server's
*reimplementation*, not a dump of the original server, so its silence is weak evidence that the rule never
existed — and equally weak evidence that it was added late and dropped. Three readings still fit:

1. a real later addition that RTK simply never implemented;
2. nexusatlas describing the practical newbie-questline context ("you'd only ever do this below 15") as
   though it were a hard gate;
3. a real rule enforced somewhere no surviving source captures.

Deferred because it is **unverified and single-source**, not because it is dated. Left out on the principle
that we do not invent gates the era-correct source does not describe.

What would settle it: the jeedee 6.x or Mithia 7.x server sources (see `[[nexustk-reference-servers]]` —
not currently on disk), or any period board post describing being refused the quest for being too high.

If per-quest level caps are ever wanted, note they are a general rule and an era-gated one, not a Chu Rua
special case — see `docs/common/Era-Gating.md`.

---

## Sute — what is built, and on what evidence

The quest is built (`Server/SuteQuest.cs`): Eldritch's tale, the 200-gold powder, the cave-mouth tile, the
key, the legend. So is Sute's combat kit (`Server/SuteAi.cs`): two ranged zaps, a wounded self-heal,
hit-and-run, a cornered rout, and the cave's cold-blast tiles. The first rests on period sources; the
second rests on one player's account of one fight, which is why it is written up here rather than only in
code. What is genuinely still missing is the two armour chains that consume this quest's outputs.

### Sute's combat kit is built from ONE eyewitness report

Built 2026-08-22 (`Server/SuteAi.cs`, `Tests/SuteAiTests.cs`, two `MobSpells.csv` rows). Recorded here not
as a gap but as a **provenance warning**: unlike everything else in the Sute quest, none of it comes from a
period source. RTK has no AI script for him at all, and the only thing the archive says is Nexus Atlas
quoting the Gods' launch notice — the cave "offers a spellcasting enemy, magical traps, and makes the old
'Super Wasabi' dye color available to many". Everything concrete came from the user fighting him once and
reporting what happened. Anyone re-tuning this should know they are re-tuning a single observation:

| Thing | Where it came from |
|---|---|
| Ice ray, 405 damage, "Feel my power!" | measured once, at AC -22 |
| Ice storms, "Chilly in here isn't it?" / "You will never get my jewels!" | shouts observed; damage **never measured** — 810 is the user's authorised "let's say 2x Ice ray" |
| "A blast of frigid cold hits you.", 257 damage | measured once, at AC -22 |
| 4-hit burst / back off 2 / pause / return, repeat | described, not timed precisely — see the burst-size note below |
| Ice ray animation (the dart trap's, 12), Ice storms 24, heal = Soothe's (5 / sound 708) | given by the user, by eye, after seeing the wrong ones in game |
| self-heal 200 HP at or below 25% | described |
| flee below 25%, fight back when cornered | described |
| ~1-in-8 magic deflect | **already correct before this work** — his `MobProtection=1` in mobs.csv gives 10% against a Will-25 caster, and nothing was changed |

Two numbers are softer than the rest and are called out at their constants:

* **Sute's cadence forced the world heartbeat down from 600ms to 333.** Worth reading before touching
  `World.TickMs`. He was observed moving two tiles a second and striking twice a second, both in a
  **333 / 333 / 333-rest** rhythm — the same shape a player gets from the 3-actions-per-second budget, using
  two of its three slots. A mob may act at most once per world beat, so at 600ms the fastest creature
  possible managed 1.7 actions/sec and simply could not represent him.

  **Lowering the beat speeds nothing up.** Every timer is `timer += TickMs` against a per-mob interval in
  real milliseconds, carrying the remainder rather than resetting, so a 2000ms creature still moves every
  2000ms. What changes is the granularity — the smallest interval the world can express. The four
  tick-COUNT constants (`RespawnTicks`, `BatchSweepTicks`, `ForageTicks`, `AdviceTicks`) were converted to
  derive from `TickMs`, so their real-world periods are unchanged. Cost is ~1.8x the tick body, which only
  walks maps with players; the slow-tick watchdog has never fired in this repo's logs and now scales off the
  period. `P1998_TICK_MS` overrides it if that turns out to be wrong.

  Movement went through three readings before landing. "Rabbit speed" was first taken as a walking *pace*;
  the user clarified it meant the rabbit's **two-tiles-in-one-turn dart**, which is RTK's own idiom
  (`AI/bosses/nine_tailed_fox.lua` calls `mob:move()` three times in one invocation) — and that unified the
  world's three fleers onto one `World.Dart`. Then Sute turned out not to want it at all: he walks, one tile
  per beat, animated, and it is the beat that is fast. So:

  | Fleer | Tiles per turn | Pace | Source |
  |---|---|---|---|
  | Prey (rabbit, blue rooster) | 2 (a hop) | own `MobMoveTime` | user's observation; RTK has no rabbit AI |
  | Wounded rout (fox, Maletic, Citelam) | 3 (a hop) | own `MobMoveTime` | RTK's literal `mob:move()` x3 |
  | **Sute** | **1 (a walk)** | **333ms, acting 2 beats in 3** | user's observation |

  Side effects of the unification: a routing boss can now trip trap tiles (the old hand-rolled rout moved
  the mob directly and skipped that check), and the blue rooster got quicker — at `MoveTime` 500 it was
  pinned at the one-tile-per-beat ceiling, which has now moved.
* **AC and spell damage.** Both damage figures were measured on a player wearing AC -22, but our engine
  does not run creature-spell damage through AC at all (`Session.ReceiveMobSpell`: magic ignores physical
  AC, the same rule player spells follow). The observed numbers are therefore used raw and reproduce the
  observation exactly *for that player*. If spell damage is ever put through AC these want re-deriving
  (405 / 0.78 ≈ 519).

Still not built: the "magical traps" of the launch notice are modelled ONLY as the cold-blast tiles. If the
original had a second trap kind in the cave, no source describes it.

**What this shipped wrong, kept because each one is a class of bug rather than a typo. Every single
one was found by playing it — none was reachable by reading the code or by any test that existed:**

1. **Animations are invisible to tests.** Ice ray, Ice storms and the self-heal all went out with ids picked
   by theme rather than from a source — the heal in particular used the ICE GLARE effect, so it drew an
   attack over him. Nothing failed; it just looked wrong, and only playing it caught that. The ids are now
   pinned against the rows they come from (`set_dart_trap`, `soothe`) rather than as bare numbers, so a
   retuned source row moves them together.
2. **Every burst after the first was two swings, not four.** The description read "4 hits … then comes
   back for two attacks repeating process", which was implemented as a 4-hit opener and 2-hit follow-ups.
   In play that meant he hit four times once and twice forever after. The user's correction: above half
   health it is **four every time**. There is now one `SuteAi.BurstHits`, and a test that drives three whole
   cycles counting real swings — the old shape was internally self-consistent, so reading the constant back
   would never have caught it.
3. **He never fled — three separate causes, fixed over three rounds.** All the same shape: a rule that is
   correct in isolation but only reachable when some other state permits it.
   * `OnDamaged` re-armed the one owed retaliation on every hit, so a player swinging faster than his
     attack timer kept the debt permanently above zero. Added `SuteAi.RetaliateLockoutMs`.
   * That lockout armed when the debt reached ZERO. Cornered he is owed *two*, so a hit landing between the
     first and second answer topped the debt back to two, zero never arrived, and the lockout never armed.
     It now arms when the debt is CREATED.
   * **`SuteCornered` was latched.** `Decide` read the flag and returned Normal while it was set — but
     returning Normal meant World never entered the branch that recomputes it, so one blocked step pinned
     him for the rest of the fight. That is why a boss on 15% health stood and fought to the death. He now
     asks to run on *every* beat unconditionally, and World falls through to a swing only when the step
     genuinely fails. **Never latch a "can't do X" flag whose only writer is the code path that doing X
     would reach.**
4. **Feedback that isn't damage has no path of its own.** The self-heal changed his HP silently: the
   over-head bar is drawn by `Session.ShowDamageResult`, which only runs on a hit, so the bar sat where the
   last blow left it and the heal was invisible to the player fighting him. World now queues a bar redraw
   alongside the heal's animation.
5. **He wasn't always there at all — a shared rule whose failure only shows at a cap of one.** Reported as
   "Sute isn't spawning in Sute's Nest" (2026-08-23). The batch spawner ported RTK's give-up rule literally
   (`mobSpawnHandler.lua:3003`, `if fail >= maxMobs[z] * 4`): roll a random tile, and abandon the creature
   after `cap * 4` failures. Every `handleSpawn` row extracts to a ZERO box, so each roll is uniform over the
   *whole* map — and Sute's Nest is 52% walkable ground. Yachi (cap 15) gets 60 rolls for 15 tiles and never
   comes up short; Sute gets **four coin flips**, and was measured missing from **6.9% of refills** (2000
   trials against the real map), each miss lasting the full 300s group cycle. The rule reads as a spin guard
   and is fine as one; it was doing a second job — deciding coverage — that it does badly, and badly in
   inverse proportion to how much the creature matters. The roll now falls back to `World.OpenTiles`, which
   enumerates the box, so a cap is a guarantee whenever a free tile exists (0/2000 misses after; the
   last-free-tile case places 200/200). This is a deliberate deviation from RTK, and it brings the batch
   system into line with the point system, which never had the hole — `PickAreaHome` falls back to the box
   centre and `FreeSpawnTile` to the spawn tile, so a point spawn cannot silently vanish.
   **A give-up budget scaled to a population is not a budget at all for a population of one.**

### The two armour chains that need Sute

Sute's key and Sute's corpse are both *ingredients* of chains listed under "Star / Moon / Sun armour chains"
above, which is why `SuteQuestAbility` deliberately stops eating the key once you hold the legend:

* **Mage, Moon armour** — `mage_trainer.lua:595` asks for nine keys at once: "Key to Earth / Fire / Heaven /
  Mountain / Wind / Pond / Thunder / Water / **Sute's Key**". Atlas's quest page says the same ("Mages need
  the key for their Moon armor quest").
* **Poet, Sun armour** — `poet_trainer.lua:826` gates on `killCount("massive_scorpion") >= 1 and
  killCount("sute") >= 1`. Atlas: "Poets must kill Sute as part of their Sun armor quest."

Nothing needs to change in `SuteQuest.cs` when those land; the key already survives repeat runs.

### Nexus Atlas says the cooldown is a day, not an hour

Recorded because it is a genuine three-way disagreement and the next person will re-find it.
`SuteQuest.RecoatSeconds` is **3600**, following the NPC's own words in the tswolf screenshot ("it is
dangeous to apply the powder more than once per hour") and that page's walkthrough ("wait an hour and pay
again for the dye"). RTK's code says 86,400 while RTK's own dialog text says "once per hour". Nexus Atlas,
years later, says "once a day (a Nexus day is 3 hours in real time)". Its parenthetical is exactly right —
`Shared/GameCalendar.cs` has `MsPerHour = 450_000` (7.5 real minutes) and `HoursPerDay = 24`, so a game day
is precisely 3 real hours — which makes the claim a considered one rather than a slip, but it still
contradicts the era-correct dialog.
Reading: the hour is right for 4.95 and Atlas may be describing a later retune. What would settle it: a
period board post about waiting to be re-coated.

---

## Tiger Mail — what is built, and the three source conflicts

The Warrior armour ladder is built (`Server/TigerMailQuest.cs`, `Tests/TigerMailQuestTests.cs`): Claw in
Chonsa Den, all seven rungs, the per-rung experience sacrifice, the reset hatch, and the tutor's Tiger
Essence briefing (`TutorialQuest.TigerEssence`). Recorded here for the two things that are *not* built and
the three places the sources disagree, because all five will be re-found by the next person.

### The dragon / shard branch of `claw.lua` is not ported

`RTK-Server/rtklua/Accepted/NPCs/buya/claw.lua` has a second, unrelated conversation gated on level 99:
saying `"dragon"`, then `"earth dragon"`, then `"shard"` walks `quest["claw_soe"]` 1 → 2 → 3 and ends by
pointing the player at **Baegi** to have an Amethyst hollowed into a Dragon Shard. That is the opening of
the Dragon Shard / Kawlana chain, not the tiger ladder, and it dead-ends without Baegi's ring shop
(`ring_shop.lua`) and the Sonhi Desert — neither of which exists here. When they land, the three keywords
belong on `TigerMailAbility` beside `"chongun"`; the registry key to use is RTK's own, `claw_soe`.

### Claw's ladder stops at Earth, and Star / Moon / Sun tiger mail is NOT its continuation

RTK's `claw.lua` ends with "you have seen what I know for I have only lived on earth — perhaps a **celestial
being elsewhere** would know more", and RTK backs that up with a tiger-mail continuation. **That
continuation is RTK's own invention.** The line is kept as flavour; nothing should ever be built behind it.

What the period sources actually say:

* Star / Moon / Sun **Tiger Mail** and **Tigress** are real items — tswolf's 2001 armour archive
  (`armor/warrior.shtml`) lists all six with sell values, and nexusatlas later files Moon and Sun tiger mail
  and tigress on its **extinct** page ("Armor that no longer exists in the Kingdoms").
* But **nothing grants them.** tswolf's source column for all six reads **"Unknown"** — in the same table
  where Star/Moon/Sun *Scale Mail* reads "Quest" and Star/Moon/Sun *War Platemail* reads "Tailor+Smith". A
  source of "Unknown" next to two families whose sources are named is evidence, not an omission.
* nexusatlas' walkthrough closes the same way: "You cannot get another level of Tiger mail any longer."

The real Warrior Star / Moon / Sun / Wind chain is **Scale Mail / Mail Dress**, and it is a different quest
in a different place — tswolf `quests/armor/warrior.shtml` (Wayback 2001-01-28):

| Tier | Level | Where | Gate | Shape |
|---|---|---|---|---|
| Star | 66 | Kugnae Guildmaster, say `"Star"` | Blessed by the Stars | 18 Agile Monkeys solo → 2 Titanium gloves → 1 Electra → 1 karma + 1 might |
| Moon | 76 | same, say `"Moon"` | Dog karma | 30 Crazed Mongrels → the Pig 1 glowing boss → 20 Grim Ogres → 1 Titanium glove, 3 Electras, 20 Ambers, 2 might, 1 grace → Star armour + 2 karma |
| Sun | 86 | same, say `"Sun"` | Tiger karma | 60 Ice + 60 Frost Ogres → 20 White ambers, 2 Titanium gloves, 2 Corrupted blades, 4 Electras → 200 rabbits → 14 self-killed Gold acorns → both Monkey bosses → Moon armour, 20,000 coins, 3 karma |
| Wind | 96 | Scribe atop Scribe's Mountain, Vale, say `"Wind"` | Spirit karma | undocumented in period ("Nexon has not officially released them") |

Every kill step must be solo, at full experience, and the LAST thing killed before returning. The rewards
are **BONDED**, and each tier's turn-in accepts an unbonded predecessor bought from a tailor. Unported —
it belongs with the "Star / Moon / Sun armour chains" entry above, whose karma table already covers its
gates. `game-data/Items.csv` already carries the whole scale mail / mail dress ladder (35001-35009,
36001-36009), so it is a quest-script gap, not a data gap.

### Three source conflicts, and how each was called

All three are documented at their constants in `TigerMailQuest`; this is the short version.

| Question | RTK `claw.lua` | tswolf (2001) | nexusatlas | Board tutor (KoyaSoto) | Built |
|---|---|---|---|---|---|
| Starting level | 5 | — | **6** ("Level Required : 6") | **6** ("cannot be started till level 6") | **6** |
| Rung 2 catalyst | **gold acorn** | — | mountain ginseng *(2006-09 … 2007-08)*, **gold acorn** *(2007-10 on)* | **gold acorn** | **gold acorn** |
| Female rung names | Jade / Royal / Sky tigress | **Summer / Autumn / Winter** | **Summer / Autumn / Winter** (`warriorarmor-old.php`); quest page says "Autumn tigress" | — (male names only) | **Summer / Autumn / Winter** |

* **Level 6** is also the self-consistent answer: a Peasant is walled at 5, and the quest demands the
  Warrior path, so 6 is the first level at which a Warrior has earned anything.
* **The catalyst** is the one place this port picks a side against the nexusatlas snapshot it was checked
  against. What decided it: the page *changed* between its 2007-08-11 and 2007-10-13 captures, and it
  changed toward what the other two sources already said. A page correcting itself is a better reading than
  a game change nobody else recorded. `mountain_ginseng` (10045) exists, so flipping it back is a one-word
  edit if a period source ever turns up.
* **The female names** were called the wrong way round on the first pass — nexusatlas' "Autumn tigress" was
  read as a slip for RTK's `royal_tigress`, and it is not. Every other female warrior line is seasonal where
  the male is mineral (war dress and mail dress both run spring/summer/autumn/winter), tswolf's 2001 archive
  lists "Summer Tigress 6 / Autumn Tigress 16 / Winter Tigress 26", and nexusatlas' `warriorarmor-old.php`
  pairs them explicitly: Jade tiger mail ↔ Summer tigress, Royal ↔ Autumn, Sky ↔ Winter. RTK named the female
  ladder after the male one. `Items.csv` rows 42014-42016 are renamed to the seasonal names; the KEYS stay
  `jade_/royal_/sky_tigress` so they still line up with the RTK reference tree a porter will read beside this.
  (nexusatlas' *current* `warriorarmor.php` lists BOTH sets, which is what a later rename with the old rows
  retained looks like — its `extinct.php` exists for exactly that.)

### Two RTK behaviours deliberately changed

* **The experience sacrifice is actually charged.** `claw.lua` computes `player.exp - cost` but only writes
  it back inside `if exp < 0`, so the deduction silently vanishes for everyone who can afford it — the
  sacrifice the whole quest is named for never happens. Charged here, clamped at zero, which is what the
  clamp branch was plainly meant to do. The seven costs (664 / 2,556 / 11,200 / 34,784 / 70,344 / 178,032 /
  428,544) are corroborated: they are RTK's constants *and* KoyaSoto's "TNL penalty" column.
* **The tutor's block releases on MEETING Claw, not on quest progress.** RTK's condition is
  `quest["tiger_armor"] == 0`, which only clears when the first rung is actually claimed — so a Warrior who
  walked to Chonsa Den, heard Claw out, and could not yet afford an antler and a war platemail would have
  done everything the briefing asked and still be locked out of their own tutor. `TigerMailQuest.MetClaw`
  (saved as `tiger_essence_met_claw`) is stamped the moment Claw engages, ahead of his own level and ingredient
  checks. **The block itself is
  kept** — the briefing repeats on every click until you have been. The branch also reads
  `TigerMailQuest.MinLevel` rather than the Lua's literal 5: RTK's briefing level was only right because
  RTK's quest also started at 5, and briefing below the quest gate sends the player across Buya to be
  bounced. `TutorBriefsExactlyWhenClawWill` and `TutorBlockReleasesOnMeetingClawNotOnProgress` pin both.

### The briefing arrives on its own, and the tutor is the fallback

nexusatlas: "The Tutor **will eventually give** warriors a quest called Tiger Essence." It is pushed the
moment a Warrior reaches `TigerMailQuest.MinLevel` — no NPC, no click (`Session.PushTigerEssence`, fired from
`AwardExp` after a real level-up, deliberately not from `LevelUp`, which the `@lvl`/`@class` rebuild replays
dozens of times). The dialog goes out on the player's own entity id with the tutor's portrait read off his
NPCs.csv row, since he is typically a city away (`Session.DlgPush`).

The tutor's click branch plays the SAME script (`TigerMailQuest.Briefing`, so they cannot drift) and exists
for everyone the push cannot reach: characters already past the gate when this shipped, characters rebuilt by
`@lvl`, and anyone who dismissed the push. The push fires once; the tutor repeats until you have been to
Claw.

### Harden Armor lands on the Jade and Blood rungs only

nexusatlas says "The tiger will cast Harden armor on you" under steps 2 and 6 — the Jade and Blood rungs —
and under no other step; RTK casts nothing anywhere. An earlier pass cast it on all seven, reasoning that
every rung is the same transaction so the split looked like patchy coverage. It is not: the user confirms
the two-rung split is real. `Rung.Harden` carries it, so it is data rather than a rule
(`TigerMailQuest.HardenSpell` → `Session.NpcCastWard`, which is a general "an NPC casts a ward on you"
primitive and stays available to anything else that wants it).

---

## The Sage — what is built, and the three things left off

The ladder is built, and so is its reach.

`npcs.SageNpc` (`game-data/npc_dialog.lua`) teaches Share Wisdom and its four upgrades, one rung at a time,
100,000 gold each, 90 real days between rungs, level 90, each rung **replacing** the one below it.
`Content.NpcGrantedSpells` keeps the path trainers from teaching rung 1 behind his back. `verbs.sage_shout`
(`game-data/spell_verbs.lua`) then decides where each rung actually reaches: rungs 1-2 sage from the "4.0
designated areas" only, rungs 3-4 also from the caster's own kingdom, rung 5 from anywhere — and outside its
reach the spell becomes the **Mentor** spell rather than failing, with the mana and the full aether charged
either way. `Content.IsSageArea` / `Content.IsOwnKingdom` own the geography;
`Tests/ContentSmokeTests.TheSageIsReachableAndOwnsTheWholeWisdomLadder` and
`SageRungsReachTheMythicAndOnlyTheirOwnKingdom` pin both halves.

Source: `Sources.csv` **`atlas-2002-12-25-sage`** — the whole system on one dated page — plus tswolf's spell
list, the tutor-board post "The sage spells", and the Dream Weaver `Eldridge09270051.html` ("Sage in towns")
that introduced the Mentor fallback. RTK's `NPCs/wilderness/sage.lua` disagreed on every number and lost each
time; its script is still the shape of the dialog and the source of the rules speech.

| rung | spell (ours) | archive name | aether | where it sages |
|---|---|---|---|---|
| 1 | `share_wisdom` | Share Wisdom | 15 min | Mythic Nexus, Wilderness, KaMing's Encampment, Carnage, Events |
| 2 | `mentors_wisdom` | Mentor's Wisdom | 10 min | as rung 1 |
| 3 | `apprentices_wisdom` | Buya / Kugnae / Nagnang / **Neutral** Wisdom | 10 min | as rung 1, **plus the caster's own kingdom** |
| 4 | `adepts_wisdom` | Buya / Kugnae / Nagnang / **Neutral** Sage | 5 min | as rung 3 |
| 5 | `sages_wisdom` | Sage's Wisdom | 5 min | *"virtually anywhere"* |

### Rungs 3 and 4 are really four spells each, named for your nation

The archive names them *"Buya, Kugnae, Nagnang, or Neutral Wisdom"* and *"... or Neutral Sage"* — four
variants apiece against our single `apprentices_wisdom` / `adepts_wisdom`, which carry RTK's invented
Apprentice's/Adept's names. The **behaviour** is already right: `Content.IsOwnKingdom` resolves the caster's
kingdom at cast time, and a neutral caster gets no home kingdom, so their rungs 3-4 behave as rung 2 exactly
as the tutor board says (*"Works just like level 2 for neutral villagers"*). What is missing is only that the
spell in the book does not say which kingdom it is for. Deferred because it is eight new `Spells.csv` rows
plus a nation-keyed grant in the Sage's dialog, to buy a name — and renaming without splitting would be
worse than either, since the kingdom names exist *because* those rungs are kingdom-scoped.

### Carnage and event areas should halve the aether

*"Sage Aethers are cut in half to 2/3 off in Carnage Event Arenas and Special Event Areas."* Not built: the
discount is a property of a carnage **game** being under way, an event state this server does not model at
all. `Content.IsSageArea` already knows which maps the carnage/event set is (see `SageEventMaps`), so this is
a multiplier on `row.duration` in `sage_shout` the day there is an event state to hang it on.

Related and deliberate: the **arenas** are not sage areas here. Sage worked in "carnage games", not in an
empty arena, and a permanently-sagable arena is not what any source describes.

### The rules the Sage reads out are not enforced

"Jailing for ANY crime will result in loss of this spell", and the Atlas is more specific: *"If you are
punished, you lose sage, and cannot relearn for 90 days. After those 90 days, you have to start from the
first spell once again."* There is no jail here — maps 47 and 666 exist, the mechanic does not — so the
speech is era-correct flavour and nothing acts on it. Whoever builds jailing owns this, and it is small:
forget whichever rung they hold, and write `now + 90 days` into the `sage_timer` registry key the Sage
already uses. The restart-at-rung-1 half needs nothing extra — the Sage finds your rung by looking in your
spellbook, so losing the spell *is* the demotion.

### Sage's Wisdom is a Sam san requirement, and there is no trial

*"One of the Sam San Requirements is to have the highest level sage."* We have no trial — marks are set by
`@mark` — so nothing consumes rung 5 yet.

### Deliberately not reproduced: the Sage's own retail bug

TSWolf, 2003-04-13: *"The Sage NPC takes 100k without upgrading yer sage. (I personally lost 100k."* Ours
teaches before it charges and takes nothing on a failed teach. Recorded so the next person to find that post
does not "fix" our version into matching it.

## The Forever Tree / Bon-Hwa — what is built, and the trials that are not

Bon-Hwa is the immortal at the Forever Tree (map 1229). Its two everyday services are **built**
(`Server/BonHwa.cs`, from RTK `NPCs/wilderness/bon_hwa.lua` and the `npcIsBonHwa` branch of
`NPCs/Common/ExpSeller.lua`):

* **Bon-Hwa Immortality → My Weapon** — enchant a class weapon (Spike/Blood/Surge/Charm) straight to your
  mark's tier (Enchanted/Il/Ee/Sam san) for a flat 200M exp, consuming any lower tier and handing back the
  bonded upgrade. Gated at level 99 + the Enchanted rank (80k base vita **or** 40k base mana).
* **Shadow Stats** — the same exp-for-stat trade the ExpSeller vendors run, but with the per-(mark, class)
  Might/Grace/Will caps past 130 and the 100M-per-point cost. The four in-era cap rows (marks 0–3) are
  transcribed from `_bonHwaLimits`.

**What is NOT built: the San-rank trials themselves.** bon_hwa.lua's `"Il San"` and `"Ee San"` menu branches
(and the commented-out Sam/Sa/Oh San) are how you *earn* a mark. We do not have them — marks are `@mark`-set,
and the weapon/stat tiers above simply read whatever mark you hold. Each trial set is a legend-gated checklist
(`passed_first_trial_of_*`) that, once complete, fires `updatePath(class, mark)` and a server broadcast. The
trials, from bon_hwa.lua:

| Rank | Trials (RTK) | Blocked on |
|---|---|---|
| **Il san** (mark 1) | Knowledge (1.2B exp → Tree), Strength (kill Spirit Rat, `killCount`), Wealth (600k coin + 5 Well Crafted White Amber) | Well Crafted White Amber item; the rest is buildable now (exp/coin/kill-count all exist) |
| **Ee san** (mark 2) | Knowledge (1.2B exp), Strength (Spirit+Avenger in 8 mythic caves via `allMythicCaveBosses`), Wealth (2M coin), Skill (6 carnage **or** 12 combined minigame wins), Culture (Skilled in a craft), Spirit (Tiger karma) | carnage/minigame win counters, crafting skill levels — neither modelled |

Knowledge/Wealth/Strength(rat)/Spirit(karma) are wireable today; Skill and Culture are the real blockers, and
they are the same systems the greater alliances and the Sam san Sage gate wait on.

**Sam san is in the game here, though the dated pages put it after 2001-07-09.** `Content.MaxMark = 3` admits
it (`@mark 3`, its 18 spells). tswolf's San quests page lists only the marks "Il San, Ee San" (Wayback
2001-01-29 and 2001-06-27), its spell pages link no Sam San through 2001-08-21, and Nexus Atlas first documents
it on its "Sam San, 3rd Mark" quest page (first capture 2002-10-05). It is kept as today anyway, with no era
gate (Caleb, 2026-10-24), and its promotion is to be built after Il san's and Ee san's; RTK has no script for
it, and the 2002 Atlas trials need greater alliances (below) and event-game wins. **Sa san stays out:** its
Atlas quest page is first captured 2008-10-15, and `MaxMark = 3` keeps mark 4 unreachable.

## Greater alliances with the mythic animals

The lesser alliances are built ([Mythic-Alliances.md](Mythic-Alliances.md)). The greater ones are not, and
they are the natural next piece: the eight-slot kill track they depend on already exists, and so does the
CSV that names every cave's bosses and its enemy.

What a greater alliance is, from Atlas's *Greater Alliance Information* page and Nussan's walkthrough,
which agree on all of it:

| | |
|---|---|
| **Word** | `greater` (or `greater alliance`), said to a mythic you are ALREADY allied to |
| **Rank** | **Ee San** — Atlas lists it as the level requirement, Nussan repeats it |
| **Prerequisite** | **six** completed lesser alliances |
| **Cap** | **three** greater alliances per character, and only one in progress at a time |
| **Ask** | **five** of each of the two leaders in **three** caves — thirty bosses, "and NOTHING else" |
| **Which three caves** | your ally's own enemy, plus the enemies of the next two lesser alliances you hold |
| **Tribute** | none |
| **Rewards** | 4–8 karma (Nussan: 4), a legend that **replaces** the lesser mark, and the same standing Rebirth |

Three things to settle when someone builds it:

1. **Six bosses on an eight-slot track leaves two free kinds, not six.** That is the whole difficulty of a
   greater alliance and it needs no new code — but it does mean the "kill one more of each boss to push the
   mistake off" trick becomes the intended play rather than a curiosity. Atlas devotes half its Alliance
   Tips page to exactly that, worked example included.
2. **Ee San is not gated here yet.** Subpath marks are set with `@mark`; there is no San trial. Gate on
   `ctx.Mark` and accept that it is settable rather than earned, the same compromise every other mark gate
   on this server makes.
3. **Which two extra caves** is order-dependent in RTK — it walks a fixed animal list and takes the first
   two lesser alliances it finds. Nothing in the archive says the player chooses, so RTK's deterministic
   walk is probably right, but it is the one piece of the design with a single witness.

The enemy-ally check in `MythicAllianceAbility` already reads `greater_alliance_<animal>` alongside the
lesser mark, so a champion of the Dog will be turned away by the Dragon the moment something grants it.

---

## The Geomancers' orbs are built, but four of their materials do not exist yet

`Server/ForgottenPastQuest.cs` builds the whole Forgotten Past chain — twelve stages across Rotah, the Storm
Shaman, Gruff and Thane, the five-question examination, and the one-time grant of a player-chosen orb. What
it cannot do is let most players *pay* for one, because four of the materials it asks for have **no source in
the world**:

| Material | Asked for by | Why it is unobtainable |
|---|---|---|
| `ore_high` | Thane's toll (5), and it blocks the chain itself | `game-data/HarvestNodes.csv` ships RTK's **novice** mining table, which rolls poor/med only. RTK's `AI/crafting/ore.lua` first rolls high ore at *apprentice*, and there is no crafting-skill system here. |
| `metal` | Metal orb (10) | Smelting. RTK's `NPCs/Common/smith.lua` "I'm Smelting!" branch turns ore into it; not ported. |
| `hot_coal` | Fire orb (10) | Same smelting branch. |
| `ice_shard` | Water orb (2) | Nothing drops or sells it (`grep -rn ice_shard game-data/` finds only its Items.csv row). |

`ginko_wood`, `ore_poor`, `stardrop` and `yellow_amber` are all obtainable today, so the **Wood** and
**Earth** orbs are fully paid-for the moment the ore toll is passable.

This is the crafting system's gap, not the quest's: the costs are right (a period tutor post, the Atlas's
per-orb captures and RTK all agree on all five), the grant works, and each orb starts working the day its
material does. Nothing here should be "fixed" by lowering a cost or by adding high ore to the novice table —
the Atlas's own walkthrough says "it may take a short while to get high ore if you aren't very skilled in
mining", which is the skill gate, in period, described from the outside.

The unblocking change is the mining-skill ladder, which is a crafting-system feature and is already implied
by the header comment in `game-data/HarvestNodes.csv`.
