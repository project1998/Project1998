# game-data — the content registry

Everything the game *is*, as opposed to how it works: 71 CSVs, 4 Lua scripts, 1,840 `.map` terrain files
and `SObj.tbl`. Read-only to a running server, hot-reloadable with the `@reload` GM command, and replaced
wholesale by a deploy.

**Editing a file here does not need a rebuild.** See [`../docs/common/Modding.md`](../docs/common/Modding.md)
for the full map of which file holds what, and
[`../docs/common/Architecture.md`](../docs/common/Architecture.md) §3 for why content, live state and code
live in three directories that cannot see each other.

> This was its own repository (`Project1998-data`, a submodule) until 2026-08. It is a plain directory now:
> a change to a CSV and a change to the `Content.cs` loader that parses it are one change, and as two repos
> they could never be one commit.

## Where it came from

Most of it is distilled from **[github.com/unkmc/RTK-Server](https://github.com/unkmc/RTK-Server)** — a
Mithia/7.x NexusTK server whose production MySQL dump (`database/2020-09-02-21-55-01_RTK.sql.bak`, 2 MB,
54 tables) is the best available source of monster/item/map/NPC **names + stats + placement**. Names and
stats are *not* in the 4.95 client (see [`../docs/4.x/Protocol.md`](../docs/4.x/Protocol.md) §11a — the
client-data audit), so this is the canonical content source.

The RTK creature-spawn packet is **byte-identical to our 4.95 `0x07`** (`look = 0x8000|monsterId`, then a
`look_color` palette byte), which independently confirms our reverse-engineered recolor model (§11a.1).

**`Sources.csv` is the provenance registry.** Every source carries a `Tier` and a `Weight`, and content
rows cite a `SourceId`; when two disagree, the higher weight wins and the loser goes in the notes. RTK is
**weight 0** — the most-used source here and the least authoritative. Read
[`../docs/research/README.md`](../docs/research/README.md) before adding a value.

## Files

Regenerable via the `re/*.py` extractors — `re/rtk_extract.py`, `re/build_map_index.py`,
`re/extract_mob_drops.py`, `re/extract_shops.py`, `re/extract_lua_spawns.py`,
`re/extract_minor_quests.py`, `re/extract_spell_formulas.py` — each of which writes here. The C# loader
in `Server/Content.cs` names the script that generated each table in its doc comment.

The generated block covers the 68 CSV files the server loads. `MobEquipment.csv`, `NPCEquipment.csv`
and `Sources.csv` are extractor output only and are not loaded by the server.

<!-- generated: tables -->
| File | Environment override | Rows read | Rows kept | Header |
|---|---|---:|---:|---|
| `ObjectFlagOverrides.csv` | `P1998_OBJECT_FLAG_OVERRIDES` | 1 | 1 | supplied (`Obj`, `Flag`, `Note`) |
| `Obj533Fix.csv` | `P1998_OBJ533_FIX` | 128 | 128 | supplied (`Legacy`, `Action`, `Replacement`, `FiveId`, `Flag495`, `Flag533`, `Scope`) |
| `Tile533Map.csv` | `P1998_TILE533_MAP` | 1,190 | 1,190 | supplied (`StartLegacy`, `Count`, `Start533`) |
| `map_index.csv` | `P1998_MAP_INDEX` | 2,025 | 2,025 | from file |
| `MobFlees.csv` | `P1998_MOB_FLEES` | 2 | 2 | from file |
| `MobStationary.csv` | `P1998_MOB_STATIONARY` | 14 | 14 | from file |
| `mobs.csv` | `P1998_MOBS` | 716 | 716 | from file |
| `Items.csv` | `P1998_ITEMS` | 2,544 | 2,544 | from file |
| `Warps.csv` | `P1998_WARPS` | 4,691 | 4,208 | from file |
| `Spawns.csv` | `P1998_SPAWNS` | 1,175 | 1,174 | from file |
| `AreaSpawns.csv` | `P1998_AREASPAWNS` | 2,588 | 2,588 | from file |
| `AreaSpawnsTrap.csv` | `P1998_AREASPAWNS_TRAP` | 20 | 20 | from file |
| `AreaSpawnsCrafting.csv` | `P1998_AREASPAWNS_CRAFT` | 8 | 8 | from file |
| `ServerTuning.csv` | `P1998_SERVER_TUNING` | 16 | 16 | from file |
| `EraFeatures.csv` | `P1998_ERA_FEATURES` | 10 | 10 | from file |
| `NPCs.csv` | `P1998_NPCS` | 368 | 289 | from file |
| `MinorQuests.csv` | `P1998_MINORQUESTS` | 101 | 101 | from file |
| `ShopStock.csv` | `P1998_SHOPSTOCK` | 38 | 38 | from file |
| `ShopBuysFrom.csv` | `P1998_SHOPBUYSFROM` | 46 | 46 | from file |
| `Paths.csv` | `P1998_PATHS` | 23 | 23 | from file |
| `LevelExp.csv` | `P1998_LEVELEXP` | 491 | 491 | from file |
| `SpellLevels.csv` | `P1998_SPELL_LEVELS` | 143 | 143 | from file |
| `Spells.csv` | `P1998_SPELLS` | 927 | 862 | from file |
| `spell_effects.csv` | `P1998_SPELL_FX` | 641 | 641 | from file |
| `SpellText.csv` | `P1998_SPELL_TEXT` | 4 | 4 | from file |
| `SpellLearnCosts.csv` | `P1998_SPELL_COSTS` | 591 | 591 | from file |
| `Mob5xPalettes.csv` | `P1998_MOB_PALETTES_5X` | 16 | 16 | from file |
| `ArmorDyeRamps.csv` | `P1998_ARMOR_DYE_RAMPS` | 11 | 11 | from file |
| `Maps.csv` | `P1998_MAPS_FULL` | 9,850 | 9,850 | from file |
| `MobDrops.csv` | `P1998_MOB_DROPS` | 377 | 377 | from file |
| `CraftingToggles.csv` | `P1998_CRAFTING_TOGGLES` | 14 | 14 | from file |
| `WarpQuestLocks.csv` | `P1998_WARP_QUEST_LOCKS` | 4 | 4 | from file |
| `ArmorQuests.csv` | `P1998_ARMOR_QUESTS` | 12 | 12 | from file |
| `MythicCaves.csv` | `P1998_MYTHIC_CAVES` | 12 | 12 | from file |
| `MythicAlliances.csv` | `P1998_MYTHIC_ALLIANCES` | 12 | 12 | from file |
| `ArenaDoors.csv` | `P1998_ARENA_DOORS` | 5 | 5 | from file |
| `EventCaveTiers.csv` | `P1998_EVENT_CAVE_TIERS` | 9 | 9 | from file |
| `EventCaves.csv` | `P1998_EVENT_CAVES` | 1 | 1 | from file |
| `MusicTracks.csv` | `P1998_MUSIC_TRACKS` | 89 | 89 | from file |
| `MapBgm.csv` | `P1998_MAP_BGM` | 7 | 7 | from file |
| `Inns.csv` | `P1998_INNS` | 14 | 14 | from file |
| `ForageAreas.csv` | `P1998_FORAGE` | 2 | 2 | from file |
| `HarvestNodes.csv` | `P1998_HARVEST` | 6 | 6 | from file |
| `MobSpells.csv` | `P1998_MOB_SPELLS` | 294 | 294 | from file |
| `MobChatter.csv` | `P1998_MOB_CHATTER` | 21 | 21 | from file |
| `MobSpawnRules.csv` | `P1998_MOB_SPAWN_RULES` | 67 | 67 | from file |
| `MobBosses.csv` | `P1998_MOB_BOSSES` | 72 | 72 | from file |
| `PathHalls.csv` | `P1998_PATHHALLS` | 8 | 8 | from file |
| `GatewayGates.csv` | `P1998_GATEWAY` | 16 | 16 | from file |
| `WorldMapDests.csv` | `P1998_WORLDMAP_DESTS` | 7 | 7 | from file |
| `WorldMapTriggers.csv` | `P1998_WORLDMAP_TRIGGERS` | 7 | 7 | from file |
| `FallRooms.csv` | `P1998_FALLROOMS` | 12 | 12 | from file |
| `AmbushBursts.csv` | `P1998_AMBUSH_BURSTS` | 37 | 37 | from file |
| `AmbushConfig.csv` | `P1998_AMBUSH_CONFIG` | 21 | 21 | from file |
| `BoardLocations.csv` | `P1998_BOARD_LOCATIONS` | 1 | 1 | from file |
| `ShopCatalogues.csv` | `P1998_SHOP_CATALOGUES` | 11 | 11 | from file |
| `SpellParams.csv` | `P1998_SPELL_PARAMS` | 96 | 96 | from file |
| `ItemParams.csv` | `P1998_ITEM_PARAMS` | 60 | 60 | from file |
| `Pets.csv` | `P1998_PETS` | 29 | 29 | from file |
| `WeaponProcs.csv` | `P1998_WEAPON_PROCS` | 25 | 25 | from file |
| `Traps.csv` | `P1998_TRAPS` | 8 | 8 | from file |
| `Morphs.csv` | `P1998_MORPHS` | 29 | 29 | from file |
| `SpellMods.csv` | `P1998_SPELL_MODS` | 25 | 25 | from file |
| `NpcAbilities.csv` | `P1998_NPC_ABILITIES` | 29 | 29 | from file |
| `PathGrowth.csv` | `P1998_PATH_GROWTH` | 5 | 5 | from file |
| `DoorObjects.csv` | `P1998_DOOR_OBJECTS` | 50 | 50 | from file |
| `Doors.csv` | `P1998_DOORS` | 8 | 8 | from file |
| `MapCells.csv` | `P1998_MAP_CELLS` | 32 | 29 | from file |
<!-- /generated -->

Key-column guide: `mobs.csv` uses `MobLook`, `MobLookColor`, `Vita` (HP), `Exp`, `Level`,
might/grace/will and minimum/maximum damage; `Maps.csv` uses `MapId` (matching the `0x15` map id and
`maps/TK<MapId>.map`), `MapName`, BGM, indoor, light, PvP and warp-out fields; `Warps.csv` maps source
map/X/Y to destination map/X/Y; `Spawns.csv` uses `SpnMobId`, `SpnMapId` and `SpnX`/`SpnY`; `NPCs.csv`
uses `NpcDescription`, map/X/Y, `NpcLook` and `NpcLookColor`; `Items.csv` uses `ItmDescription`, `ItmType`,
`ItmLook`, damage, armor, stats and buy/sell price; `Spells.csv` holds spell and skill definitions; and
`Paths.csv` holds class names and rank titles.

## Version caveats (RTK is 7.x, our client is 4.95)

- **Look-ids 0–326 overlap** and are validated against our EPF shape-matching (rat=91, mouse=120, bull=27,
  rabbit=21, fox=22, wolf=23, bear=24, squirrel=25).
- **Maps:** 1387 of RTK's `MapId`s have a matching `TK<N>.map` in our client; the rest are 7.x-added.
- **Colours ≤19** map to our 20 `Monster.pal` blocks; **>19 are 7.x-only** and must be re-picked via `!crecol`.
- **Item look/icon ids** reference 7.x `Item.epf` — names reliable, sprite ids need checking.
- **Stats** are 7.x-balanced (structurally correct, numerically a design choice).

The `Description` field is the display name; `Identifier` is the internal snake_case key.
