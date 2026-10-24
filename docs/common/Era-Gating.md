# Era gating — targeting a point in NexusTK's history

NexusTK ran for years against a slowly-changing client. "Which client do we target" therefore does not
answer "what content should exist": client 4.95 shipped 2001-07-09, but a player logging in the week it
shipped met a different tutorial than one logging in eleven months earlier. Quests were added, moved
between givers, and occasionally taken away.

So the server carries a **target date**, and dated content declares the window it existed in.

## Configuring it

| Where | What |
|---|---|
| `game-data/ServerTuning.csv` | `EraDate,20010709` — the date the world is pretending it is, as `yyyymmdd`. `0` switches gating off entirely. |
| `game-data/EraFeatures.csv` | One row per dated feature: `Feature,Introduced,Retired,Source,Notes`. |
| `Server/Era.cs` | `Era.Has("feature")`, plus the feature-key constants our code actually asks about. |
| `@era` | Read-only GM readout of the date and what it includes. |

Both files hot-reload with `@reload`; no restart.

The default `20010709` is the day client 4.95 shipped — the build this server targets — so out of the box
everything documented below is switched **on**. Move the date back to play an earlier world.

## The two rules that matter

**Sparse and fail-open.** A feature with no row is always present, and a missing or malformed file gates
nothing. Only add a row you can point at a source for. Absence of evidence must never remove content —
the failure direction has to be "a player sees something they shouldn't", not "content silently vanishes".

**`Retired` is exclusive.** `Retired=2000-10-06` means the feature is gone *on* that day. That's the
natural reading when one thing replaces another, because the replacement's `Introduced` is the same date
and exactly one of the pair must be live.

## The three tutorial eras

The tutor (Jadespear #49 / Ironheart #20) is present in **all three**. What changes is how much he still
has to teach — content moved *into* the newbie area, and later new quests were appended to his chain.

| | before 2000-10-06 | 2000-10-06 → 2001-03-17 | 2001-03-18 → |
|---|---|---|---|
| Newbie area (4711-4718) | — | yes | yes |
| Tutor teaches saber/rabbits/squirrels/Soothe | yes | — (moved to the area) | — |
| Novice sword comes from | tutor, stage 8 | Woodland Guard's law quiz | Woodland Guard's law quiz |
| Rest of the tutor chain | yes | yes | yes |
| Du Mountain (stage 11) + student cap (stage 13) | — | — | yes |

The seam is explicit in the area's own last line — the Woodland Angel closes with *"I now leave you in
the hands of the tutor from the town you have picked as home"* and warps you to him. The area never
replaced the tutor; it fed him.

The novice sword is the one reward that crosses the seam without belonging to the moved chain: it hangs
off the tutor's **stage 8** (the deer hunt, the first task that needs a real weapon), not off
`NoviceQuest`, so `tutor_novice_chain` does not cover it. It is gated separately, on
`newbie_tutorial_area` — the era that has the area is the era where the Woodland Guard hands the same
sword out for passing his law quiz, and the tutor must not hand out a second one.

### What is *not* gated

Du Mountain the place, and Haguru standing on it, are **older than the quest** — the release post calls
him "the old guy named Haguru that was stranded on the mountain in early 4.0". With
`tutor_du_mountain_quest` off he is still there and still answers; he just has no wolves to send you
after. Same principle everywhere: gate the quest, keep the world.

## Content from *after* our window

The tutorial gates above all sit near 2001, where the question is which version of a thing was live. The
other direction is simpler and blunter: content that hadn't been invented yet.

**The Druid bouquet quest** (`druid_bouquet_quest`, 2005-05-31) is the first of these — nearly four years
past the target date, in the last months of the 5.x client before 6.x shipped 2005-10-11. Nexus Atlas's
reset post for the day lists *"Druids gain a new bouquet quest in Elder's Hideaway (public Room)"*, and the
next three days are its bug tail (2005-06-01 "recently released", 06-02 a fix attempt plus Sebastian's
Token made non-droppable, 06-03 "the problems with the Druid quest have been fixed").

That gate removes **Yarlof** from Du Mountain, and he is the reason this section exists: he is the quest's
flower-test NPC and has no other role, so there is no earlier Yarlof to leave standing. Every mention of
him in the whole archive scrape is that one Mage-tutor walkthrough — *say Sophie, then Flower, then
Bouquet with 12 flowers and a Fine cloth* — which is what dates the **NPC** and not merely the quest.

**Haguru shares map 1321 and is deliberately untouched.** He is 4.0-era, and `tutor_du_mountain_quest`
gates only his quest. Two NPCs on one mountain, gated by opposite rules, is the clearest illustration of
the next section.

### Overflow and overkill — gating a *mechanic* rather than content

The two gates above hide a quest and an NPC. `warrior_overflow` and `rogue_overkill` are the first that
hide part of how an existing ability *behaves*, and they are the furthest from our window of anything here.

| | `warrior_overflow` | `rogue_overkill` |
|---|---|---|
| What | a killing vita strike splashes its unused damage onto the tiles around the target | a killing vita strike refunds its unused damage to the caster, split vita/mana |
| Who | Warrior — Berserk, Whirlwind (+ Slash, Siege on live) | Rogue — Lethal Strike, Desperate Attack |
| Introduced | **2007-04-10** | **2008-09-18** |
| Gated at | `Session.LuaOverflow` | `Session.LuaBackflow` |

Both are six and seven years past the target date, so both are **off** by default and the four strikes
resolve as an ordinary one-tile hit. This is the fix for a real bug report — *"Berserk is hitting multiple
tiles"* — and the era system is the honest place for it: the behaviour was never wrong, it was just from
the wrong decade. Because they scale off caster vita, overkill happened on nearly every cast, so the
splash was effectively permanent rather than the rare cleave the port assumed.

**They are two keys on purpose.** KRU shipped the rogue refund seventeen months after warrior overflow, as
its explicit counterweight — *"you will now have a special overflow balance for rogues"*. The gap between
them is a real playable era in which warriors had overflow and rogues had nothing, and it is the entire
subject of Nexus Atlas's 2008-09-19 "Rogue balance" editorial. Folding them into one key would erase the
period the sources talk about most.

**What is *not* gated:** the strikes. Berserk, Whirlwind, Lethal Strike and Desperate Attack are all
era-appropriate and fully functional — damage, mana, cooldown and the caster's own HP cost are untouched.
Gate the mechanic, keep the ability; the same principle as gating the quest and keeping the NPC.

**Gated in C#, not in Lua.** Every other spell behaviour in this server is tunable from `spell_verbs.lua`
without a rebuild, and these two deliberately are not: the primitives self-gate, so `verbs.sacrifice` calls
them unconditionally and they no-op outside their era. A date-correctness rule that any script edit could
lift is not a rule. `ctx:eraHas` exists in **NPC dialog scripts** (`npc_dialog.lua`), not in spell verbs,
for the same reason.

> The formulas themselves are disputed between two archive sources, and which one we implement is written
> up where the code is, in `docs/4.x/Protocol.md` under the self-sacrifice strike family.

### Dated after our window, and kept with no gate: Ranger

Not every late arrival gets a gate. **Ranger** (PthId 15, the rogue's second PC subpath) is dated after
2001-07-09 by the pages we have, and is kept as RTK and today have it: no row in `EraFeatures.csv`, no gate
(Caleb, 2026-10-24). This is a source note, not a feature date.

- tswolf's Classes/Subpaths page (tswolf.com/classes/, Wayback 2001-01-26, 2001-06-15 and 2001-08-05) names
  Merchant, **Shadow** and Spy in the Rogue column. Ranger is not there.
- Nexus Atlas's subpath legend page (nexusatlas.com/legends/subpaths.php, first capture 2002-12-25) lists
  Ranger's legend marks, and of Shadow's says they can no longer be received, as *"the Shadow are no longer a
  path"*.
- So Ranger took Shadow's slot between 2001-08-05 and 2002-12-25. The 4.95 client draws no user-list badge for
  that slot (the badge table in `docs/4.x/Protocol.md`), which fits either name.

Why no gate: it would disable a subpath the PC-subpath work will use and leave rogues two PC subpaths, and Shadow
exists nowhere in our data (no `Paths.csv` row, no client badge), so nothing could stand in its place. No one can
hold a PC subpath today anyway (`Content.PlayablePaths`).

**What would settle the date:** a dated page between 2001-08-05 and 2002-12-25 that names the Ranger subpath or
the end of Shadow: the official site's news from September 2001 on, tswolf's own late-2001 news, or a capture
of the Atlas legend page earlier than 2002-12-25 (Wayback lists none).

Sam san is the other content dated after our window and kept with no gate; its dating is written at
`Content.MaxMark` (`Server/Content.Spells.cs`) and in `docs/4.x/Protocol.md`'s rank-ladder note.

## Adding a gate

1. Add a row to `EraFeatures.csv` with a `Source`.
2. Add a `const` to `Server/Era.cs` (bare strings at call sites mean a typo becomes a silently always-on
   gate — the fail-open default makes a misspelling invisible at runtime) and list it in `KnownFeatures`
   so `@era` reports it.
3. Call `Era.Has(...)` at the point that decides. From an **NPC dialog** script, `ctx:eraHas("feature")`
   (`npc_dialog.lua`); spell verbs have no such hook by design — see the overflow section above.
4. Add a case to `EraGatingTests` pinning both edges of the date. `EveryGatedFeatureHasADatedRow` will
   already fail the build if you add the const in step 2 and forget step 1.

### Behaviour or placement?

**Default to gating the behaviour.** A gated quest normally still has its NPC standing there, so the script
is what needs to know, not `NPCs.csv` — gate the quest, keep the world.

**Gate the placement only when the NPC himself postdates the target date** — when there is no earlier
version of him to leave in place. Put the feature key in the `EraFeature` column of `game-data/NPCs.csv`
and write no code: `Content.LoadNpcs` folds the verdict into `Enabled`, so every spawn path already
handles it, including `World.ReconcileNpcToggles` despawning him across an `@reload`. The row survives, so
`NpcById` still resolves.

The distinction is "did this being exist" versus "did he have this to say". An old NPC who gained a new
quest is always the second one.

> `Content.Load` calls `EraCalendar.Reload()` **before** `LoadNpcs` for this reason. Reading the calendar
> after would place NPCs by the *previous* date across an `@reload`, and a wrong era never throws.

`@npc` reports the two kinds of "off" separately — editing the `Enabled` column is useless advice for an
NPC who isn't born yet, whose date lives in `ServerTuning.csv`.

## Sources

- `tswolf-2001-03-18` — TSWolf/NexNet "Du Mountain opened", dating both 2001 quests to the day.
- `tswolf-newbie-guide` — the three-page newbie quest guide the area's dialog is transcribed from.
- `atlas-2005-05-31-bouquet` — the Nexus Atlas reset run dating the Druid bouquet quest, and so Yarlof.
- `atlas-2007-04-10-overflow` — Nexus Atlas "Overflow introduced", plus the two later posts that
  corroborate it (Ixeus still deriving the formula seven months on; Rachel's editorial a year on).
- `atlas-2008-09-18-rogue-overflow` — KRU's own reset notes for the rogue refund, corroborated by the
  Rogue tutor board's "Rogues Overkill ability", which is also where the two refund caps come from.

All five are in `game-data/Sources.csv` with the exact quotes and the surviving/missing screenshot list.

## New-character placement

`CharacterFactory.StartFor` picks the spawn from the calendar: **Welcome (4711) at (3,5)** once
`newbie_tutorial_area` is live, the nation's tutor home before that. `HomeCityFor` is deliberately left
alone — it is also the Silver Thread revive point, and a defeated veteran must not wake up in the newbie
area.

This is the reason `EraCalendar` lives in `Shared` rather than under `Server`. Placement runs in the
**login server**, a separate process that never loads world content; if it couldn't read the calendar it
would place every character by the pre-2000 rule regardless of what the game server believed, and nothing
would report the disagreement. The calendar is read lazily so the login server still starts instantly.

> **The login server caches the calendar for its process lifetime.** It has no `@reload`. After changing
> `EraDate`, restart it — otherwise it keeps placing characters by the old date.

Welcome's way onward is the four-tile doorway along its bottom edge, `(8..11, 15)` → Open Field (4712)
at `(8..11, 2)`, so the player keeps walking south into the next room.

> **Historical note.** Welcome originally shipped with its only exits at `(9,16)` and `(10,16)` on a
> **16×16** map — rows 0–15 — so both source tiles were out of bounds and the room was a sealed box.
> (Two of those three rows even share a source tile with conflicting destinations.) They are left in
> `Warps.csv`: they are unreachable, and they route `4711 → 4714`, which is the *later* room order, so
> they may be wanted by a future post-4.x era config. `ContentSmokeTests.NewbieAreaFirstDoorwayIsReachable`
> now asserts the real doorway so this cannot regress silently.

### Still needed for the area to be traversable end to end

| From | To | Why |
|---|---|---|
| `4712 → 4713` | Forest Path | after the rabbits |
| `4713 → 4714` | Deep Forest | after the squirrels |
| `4717 → 4718` | Angel's Blessing | the "door north" after the quiz; only the reverse exists |

`4711 → 4712`, `4714 → 4715`, `4715 ↔ 4716` and `4715 ↔ 4717` exist. The final exit to the city needs no
warp — the Woodland Angel warps the player home in script when they say "Finish".

Until those three land, a new character can reach Open Field and no further. Running at
**`EraDate=20001005`** (the day before the area) keeps new characters at the tutor instead, with him
teaching the first-steps beats himself, at the cost of the two 2001-03-18 quests.

### Dying inside the area

The area is sealed, and that has to hold for death too. Every Shaman is out in the world, so taking
Silver Thread passage would eject a player from the chain permanently — there is no warp back in — and
the passage never checked whether you were even dead, so a *living* player could leave the same way.

- **Silver Thread is refused on any tutorial map** and answers with what the ability is for instead.
- **While dead, clicking ANY NPC in the area** runs the Shaman's own revival dialog. Deliberately every
  NPC: a beginner shouldn't have to work out which one to click.

Both key off `Content.IsTutorialMap` — a *where you physically are* test, not an era or quest-progress
one. That's on purpose: it holds for a character who was GM-warped in, who abandoned the chain half way,
or whose progress flag disagrees with the map. The ghost branch reuses `ReviveAbility.Resurrect` rather
than copying its strings, so the wording can only drift in one place.

### Mob spawns

Welcome carries **rabbits only** — it is the practice room before the first quest, and the armorer's
squirrels are not introduced until Forest Path. Open Field is rabbits (quest 1), Forest Path squirrels
(quest 2); Deep Forest and Country Farm carry both, which is what lets a player collect the 5 acorns and
5 rabbit meats Mignok asks for.
