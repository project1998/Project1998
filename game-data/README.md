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
| File | Rows read | Rows kept | Header |
|---|---:|---:|---|
| `ObjectFlagOverrides.csv` | 1 | 1 | supplied (`Obj`, `Flag`, `Note`) |
| `Obj533Fix.csv` | 128 | 128 | supplied (`Legacy`, `Action`, `Replacement`, `FiveId`, `Flag495`, `Flag533`, `Scope`) |
| `Tile533Map.csv` | 1,200 | 1,200 | supplied (`StartLegacy`, `Count`, `Start533`) |
| `map_index.csv` | 2,031 | 2,031 | from file |
| `MobFlees.csv` | 2 | 2 | from file |
| `MobStationary.csv` | 14 | 14 | from file |
| `mobs.csv` | 716 | 716 | from file |
| `Items.csv` | 2,544 | 2,544 | from file |
| `Warps.csv` | 4,824 | 4,347 | from file |
| `Spawns.csv` | 1,175 | 1,174 | from file |
| `AreaSpawns.csv` | 2,588 | 2,588 | from file |
| `AreaSpawnsTrap.csv` | 20 | 20 | from file |
| `AreaSpawnsCrafting.csv` | 8 | 8 | from file |
| `ServerTuning.csv` | 20 | 20 | from file |
| `EraFeatures.csv` | 10 | 10 | from file |
| `NPCs.csv` | 380 | 303 | from file |
| `MinorQuests.csv` | 101 | 101 | from file |
| `ShopStock.csv` | 38 | 38 | from file |
| `ShopBuysFrom.csv` | 46 | 46 | from file |
| `Paths.csv` | 23 | 23 | from file |
| `LevelExp.csv` | 491 | 491 | from file |
| `SpellLevels.csv` | 143 | 143 | from file |
| `Spells.csv` | 957 | 892 | from file |
| `spell_effects.csv` | 670 | 670 | from file |
| `SpellText.csv` | 4 | 4 | from file |
| `SpellLearnCosts.csv` | 621 | 621 | from file |
| `Mob5xPalettes.csv` | 16 | 16 | from file |
| `ArmorDyeRamps.csv` | 11 | 11 | from file |
| `Maps.csv` | 9,850 | 9,850 | from file |
| `MobDrops.csv` | 377 | 377 | from file |
| `CraftingToggles.csv` | 14 | 14 | from file |
| `WarpQuestLocks.csv` | 4 | 4 | from file |
| `ArmorQuests.csv` | 12 | 12 | from file |
| `MythicCaves.csv` | 12 | 12 | from file |
| `MythicAlliances.csv` | 12 | 12 | from file |
| `ArenaDoors.csv` | 5 | 5 | from file |
| `EventCaveTiers.csv` | 9 | 9 | from file |
| `EventCaves.csv` | 1 | 1 | from file |
| `MusicTracks.csv` | 89 | 89 | from file |
| `MapBgm.csv` | 7 | 7 | from file |
| `Inns.csv` | 14 | 14 | from file |
| `ForageAreas.csv` | 2 | 2 | from file |
| `HarvestNodes.csv` | 6 | 6 | from file |
| `MobSpells.csv` | 294 | 294 | from file |
| `MobChatter.csv` | 21 | 21 | from file |
| `MobSpawnRules.csv` | 67 | 67 | from file |
| `MobBosses.csv` | 72 | 72 | from file |
| `PathHalls.csv` | 12 | 12 | from file |
| `GatewayGates.csv` | 16 | 16 | from file |
| `WorldMapDests.csv` | 7 | 7 | from file |
| `WorldMapTriggers.csv` | 7 | 7 | from file |
| `FallRooms.csv` | 12 | 12 | from file |
| `AmbushBursts.csv` | 37 | 37 | from file |
| `AmbushConfig.csv` | 21 | 21 | from file |
| `BoardLocations.csv` | 1 | 1 | from file |
| `ShopCatalogues.csv` | 11 | 11 | from file |
| `SpellParams.csv` | 105 | 105 | from file |
| `ItemParams.csv` | 60 | 60 | from file |
| `Pets.csv` | 29 | 29 | from file |
| `WeaponProcs.csv` | 25 | 25 | from file |
| `Traps.csv` | 8 | 8 | from file |
| `Morphs.csv` | 29 | 29 | from file |
| `SpellMods.csv` | 42 | 42 | from file |
| `NpcAbilities.csv` | 32 | 32 | from file |
| `PathGrowth.csv` | 5 | 5 | from file |
| `DoorObjects.csv` | 50 | 50 | from file |
| `Doors.csv` | 8 | 8 | from file |
| `MapCells.csv` | 29 | 29 | from file |
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
