using Shared;

namespace Server;

public static partial class Content
{

    // ---- CSV loaders ----

    private static Dictionary<ushort, MapInfo> LoadMaps(CsvTable csv)
    {
        var maps = new Dictionary<ushort, MapInfo>();
        foreach (var col in csv)
        {
            if (ushort.TryParse(col.Require("id"), out var id)
                && ushort.TryParse(col.Require("xs"), out var xs)
                && ushort.TryParse(col.Require("ys"), out var ys))
            {
                var name = Clean(col.Require("name", ""));
                maps[id] = new MapInfo(id, string.IsNullOrEmpty(name) ? $"Map {id}" : name, xs, ys);
                col.Keep();
            }
        }
        return maps;
    }

    // id -> MapMetaInfo from the full RTK Maps table. unknown/blank region defaults to -1 (no kingdom),
    // warpOut to true (allow) so only an explicit 0 blocks warp-outs; the req*/max*/rejectmsg columns
    // default to 0/"" (no gate) when absent.
    private static Dictionary<ushort, MapMetaInfo> LoadMapMeta(CsvTable csv)
    {
        var meta = new Dictionary<ushort, MapMetaInfo>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("MapId"), out var id)) continue;
            int Rd(string k) { int.TryParse(col.Require(k, "0"), out var v); return v; }
            // Vita/mana caps are stored as unsigned 32-bit in RTK; "no cap" is the sentinel 4294967295, which
            // overflows int.TryParse (silently yielding 0 -- looks like "no vita/mana at all" instead of
            // "unbounded"). Parse as long so the sentinel round-trips correctly.
            long Rl(string k) { long.TryParse(col.Require(k, "0"), out var v); return v; }
            if (!int.TryParse(col.Require("MapRegion", "-1"), out var region)) region = -1;
            bool warpOut = col.Require("MapWarpout", "1") != "0";
            bool pvp = col.Require("MapPvP", "0") == "1";
            // MapChat is RTK's "cantalk" flag (map.c sscanf column order matches): 1 = talk is BLOCKED on
            // this map (only 2/9850 maps set it), not "chat allowed" despite the name.
            bool canTalk = col.Require("MapChat", "0") != "1";
            // MapSpells is the opposite polarity to MapChat despite sitting two columns away: 1 = casting is
            // ALLOWED here, 0 = "That doesn't work here." (RTK map[m].spell, gated in clif.c's 0x0F case).
            // Unknown/blank defaults to allowed, so only an explicit 0 blocks.
            bool canCast = col.Require("MapSpells", "1") != "0";
            // MapIndoor (RTK map[m].indoor) — set on every town interior, cave and dungeon. Used here only as
            // the weather gate (WeatherModel.For): no rain/snow indoors. Deliberately NOT reused as a casting
            // gate — casting has to work in caves, which is why MapSpells above is the separate no-cast flag.
            bool indoor = col.Require("MapIndoor", "0") == "1";
            // MapCanSummon (RTK map[m].summon, its Lua canSummon): 0 = Approach and Summon fizzle here. Which map
            // each spell asks about is the verbs' business (spell_verbs.lua). Only an explicit 0 refuses: a blank
            // cell allows, as a map with no row does, because RTK's column default is 1.
            bool canSummon = col.Require("MapCanSummon", "1") != "0";
            meta[id] = new MapMetaInfo
            {
                Region = region,
                WarpOut = warpOut,
                Pvp = pvp,
                CanTalk = canTalk,
                CanCast = canCast,
                ReqLvl = Rd("MapReqLvl"),
                ReqPath = Rd("MapReqPath"),
                ReqMark = Rd("MapReqMark"),
                ReqVita = Rl("MapReqVita"),
                ReqMana = Rl("MapReqMana"),
                LvlMax = Rd("MapLvlMax"),
                VitaMax = Rl("MapVitaMax"),
                ManaMax = Rl("MapManaMax"),
                RejectMsg = Clean(col.Require("MapRejectMsg", "")),
                Indoor = indoor,
                CanSummon = canSummon,
            };
            col.Keep();
        }
        return meta;
    }

    /// <summary>game-data/MobFlees.csv (`Identifier,Flees`) — which creatures RUN AWAY rather than fight.
    /// <para>There is nothing to port for this: RTK's engine knows only three MobBehavior values (0 fights back,
    /// 1 attacks on sight, 2+ inert) and mob_ai_basic.lua gives a rabbit the same chase-and-swing routine as a
    /// wolf — the single <c>RunAway()</c> in the whole RTK tree belongs to one instance boss. So the MOVEMENT is
    /// ported from that boss (Mobs/mob.lua <c>RunAway</c>, Instances/mysterious_merchant.lua's
    /// <c>on_attacked</c>), and WHICH creatures use it is this file. Sparse and kept out of mobs.csv so
    /// re-running the mob extractor can't drop it; hot-reloads with @reload.</para></summary>
    private static Dictionary<string, bool> LoadMobFlees(CsvTable csv)
    {
        var flees = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var key = Clean(col.Require("Identifier", ""));
            if (key.Length == 0) continue;
            flees[key] = col.Require("Flees", "0").Trim() != "0";
            col.Keep();
        }
        return flees;
    }

    /// <summary>game-data/MobStationary.csv (`Identifier,Stationary`) — creatures that never take a step.
    /// <para>Our world gives every spawned mob the same idle wander (World.Materialize sets
    /// <c>Wander = true</c>), because RTK's per-mob movement lives in each mob's own AI script rather than in
    /// its DB row: <c>Mobs/captured_leviathan.lua</c>'s <c>move</c> only turns the sprite on the spot, never
    /// calls <c>mob:move()</c>. A caged captive that strolls two tiles out of its pen looks broken AND breaks
    /// the quest tile that has to find it (see Server/LeviathanQuest.cs). Sparse, same shape and reasoning as
    /// <see cref="LoadMobFlees"/>: kept out of mobs.csv so re-running the mob extractor can't drop it, and it
    /// hot-reloads with @reload.</para></summary>
    private static Dictionary<string, bool> LoadMobStationary(CsvTable csv)
    {
        var still = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var key = Clean(col.Require("Identifier", ""));
            if (key.Length == 0) continue;
            still[key] = col.Require("Stationary", "0").Trim() != "0";
            col.Keep();
        }
        return still;
    }

    private static List<MobDef> LoadMobs(
        CsvTable csv,
        IReadOnlyDictionary<string, bool> fleeOverrides,
        IReadOnlyDictionary<string, bool> stationaryOverrides)
    {
        var mobs = new List<MobDef>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("MobLook"), out var look)) continue;
            int.TryParse(col.Require("MobId", "0"), out var id);
            byte.TryParse(col.Require("MobLookColor", "0"), out var color);
            int.TryParse(col.Require("Vita", "0"), out var hp);
            int.TryParse(col.Require("Exp", "0"), out var exp);
            int.TryParse(col.Require("Level", "0"), out var lvl);
            int.TryParse(col.Require("Will", "0"), out var will);
            // MobMoveTime (ms between move attempts). Absent/0 in older exports -> a calm default.
            int move = int.TryParse(col.Require("MobMoveTime", "0"), out var mv) && mv > 0 ? mv : 2500;
            var name = Clean(col.Require("Description", ""));
            var key = Clean(col.Require("Identifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"mob{id}" : key;
            bool aggressive = col.Require("MobBehavior", "0") == "1";
            int.TryParse(col.Require("MinDmg", "1"), out var minDam);
            int.TryParse(col.Require("MaxDmg", "1"), out var maxDam);
            if (minDam <= 0) minDam = 1;
            if (maxDam < minDam) maxDam = minDam;
            bool isBoss = col.Require("MobIsBoss", "0") == "1";
            int.TryParse(col.Require("MobProtection", "0"), out var protection);
            int.TryParse(col.Require("MobHit", "0"), out var hit);
            int.TryParse(col.Require("MobArmor", "0"), out var ac);
            int.TryParse(col.Require("Grace", "0"), out var grace);
            // SpawnTime: blank (a mob the RTK dump didn't carry) falls back to that table's own SQL default
            // rather than to our old cadence. 0 is a REAL value there, not "unset" — two creatures ship with
            // it and RTK revives them on the next AI pass — so an explicit 0 is honoured.
            int spawnTime = int.TryParse(col.Require("SpawnTime", ""), out var st) && st >= 0
                ? st : DefaultSpawnTimeSec;
            mobs.Add(new MobDef
            {
                Id = id,
                Key = key,
                Name = name,
                Look = look,
                Color = color,
                Hp = hp <= 0 ? 1 : hp,
                Exp = exp,
                Level = lvl,
                MoveTime = move,
                Will = will,
                Aggressive = aggressive,
                MinDam = minDam,
                MaxDam = maxDam,
                IsBoss = isBoss,
                Protection = protection,
                Hit = hit,
                Ac = ac,
                Grace = grace,
                Flees = fleeOverrides.GetValueOrDefault(key),
                Stationary = stationaryOverrides.GetValueOrDefault(key),
                SpawnTime = spawnTime,
            });
            col.Keep();
        }
        return mobs;
    }

    private static Dictionary<string, string[]> LoadShopStock(CsvTable csv)
    {
        var stock = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var id = Clean(col.Require("NpcIdentifier", ""));
            if (string.IsNullOrEmpty(id)) continue;
            var keys = Clean(col.Require("ItemKeys", "")).Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length > 0) { stock[id] = keys; col.Keep(); }
        }
        return stock;
    }

    // ShopBuysFrom.csv is ShopStock.csv's shape with one addition: a lone "-" is an EXPLICIT empty list —
    // "this shop buys nothing" (RTK's chapel, with boss-drop sales off, and the druid who won't take your
    // meat). It has to survive as a present-but-empty entry, because an ABSENT key means the opposite:
    // "no list known, so buy anything" (see ShopBuysFrom / Shops.BuysFrom).
    private static Dictionary<string, string[]> LoadShopBuysFrom(CsvTable csv)
    {
        var lists = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var id = Clean(col.Require("NpcIdentifier", ""));
            if (string.IsNullOrEmpty(id)) continue;
            lists[id] = Clean(col.Require("ItemKeys", ""))
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Where(k => k != "-")
                .ToArray();
            col.Keep();
        }
        return lists;
    }

    // MobDrops.csv "Loot"/"RareLoot" cells are pipe-separated "item:amount:rate" / "item:rate" triples/pairs
    // (re/extract_mob_drops.py); item key "GOLD" -> a null ItemKey (gold rather than an item).
    private static Dictionary<string, MobDropDef> LoadMobDrops(CsvTable csv)
    {
        var table = new Dictionary<string, MobDropDef>();
        foreach (var col in csv)
        {
            var key = Clean(col.Require("MobKey", ""));
            if (string.IsNullOrEmpty(key)) continue;

            string lootCell = Clean(col.Require("Loot", ""));
            string rareCell = Clean(col.Require("RareLoot", ""));
            var loot = new List<LootRoll>();
            var rare = new List<RareRoll>();
            string? badEntry = null;

            foreach (string entry in lootCell.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split(':');
                if (p.Length != 3)
                {
                    badEntry = $"Loot entry '{entry}'";
                    break;
                }
                if (!int.TryParse(p[1], out int amount)
                    || amount <= 0
                    || !double.TryParse(p[2], System.Globalization.NumberStyles.Float |
                        System.Globalization.NumberStyles.AllowThousands,
                        System.Globalization.CultureInfo.InvariantCulture, out double rate)
                    || !double.IsFinite(rate)
                    || rate < 0)
                {
                    badEntry = $"Loot entry '{entry}'";
                    break;
                }
                loot.Add(new LootRoll(p[0] == "GOLD" ? null : p[0], amount, rate));
            }

            if (badEntry is null)
            {
                foreach (string entry in rareCell.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = entry.Split(':');
                    if (p.Length != 2)
                    {
                        badEntry = $"RareLoot entry '{entry}'";
                        break;
                    }
                    if (!double.TryParse(p[1], System.Globalization.NumberStyles.Float |
                            System.Globalization.NumberStyles.AllowThousands,
                            System.Globalization.CultureInfo.InvariantCulture, out double rate)
                        || !double.IsFinite(rate)
                        || rate < 0)
                    {
                        badEntry = $"RareLoot entry '{entry}'";
                        break;
                    }
                    rare.Add(new RareRoll(p[0] == "GOLD" ? null : p[0], rate));
                }
            }

            if (badEntry is not null)
            {
                Log.Warn($"MobDrops.csv row MobKey='{key}' skipped: invalid {badEntry}; " +
                         $"row Loot='{lootCell}', RareLoot='{rareCell}'");
                continue;
            }

            if (loot.Count > 0 || rare.Count > 0) { table[key] = new MobDropDef(loot.ToArray(), rare.ToArray()); col.Keep(); }
        }
        return table;
    }

    private static List<MinorQuestDef> LoadMinorQuests(CsvTable csv)
    {
        var quests = new List<MinorQuestDef>();
        foreach (var col in csv)
        {
            var key = Clean(col.Require("Key", ""));
            if (string.IsNullOrEmpty(key)) continue;
            long L(string k) { long.TryParse(col.Require(k, "0"), out var v); return v; }
            int  I(string k) { int.TryParse(col.Require(k, "0"), out var v); return v; }
            var mobs = Clean(col.Require("Mobs", "")).Split('|', StringSplitOptions.RemoveEmptyEntries);
            quests.Add(new MinorQuestDef(
                Clean(col.Require("Tier", "Minor")), key, Clean(col.Require("DisplayName", key)),
                mobs, I("MinLevel"), I("MaxLevel"), L("MinStat"), L("MaxStat"), I("MinMark"), I("MaxMark")));
            col.Keep();
        }
        return quests;
    }

    private static List<ItemDef> LoadItems(CsvTable csv)
    {
        var items = new List<ItemDef>();
        foreach (var col in csv)
        {
            if (!int.TryParse(col.Require("ItmId"), out var id)) continue;
            byte  B(string k)  { byte.TryParse(col.Require(k, "0"), out var v); return v; }
            ushort U(string k) { ushort.TryParse(col.Require(k, "0"), out var v); return v; }
            int  I(string k)   { int.TryParse(col.Require(k, "0"), out var v); return v; }

            var name = Clean(col.Require("ItmDescription", ""));
            var key  = Clean(col.Require("ItmIdentifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"item{id}" : key;

            items.Add(new ItemDef
            {
                Id = id,
                Key = key,
                Name = name,
                Type = B("ItmType"),
                Icon = U("ItmIcon"),
                IconColor = B("ItmIconColor"),
                Look = U("ItmLook"),
                LookColor = B("ItmLookColor"),
                Sex = B("ItmSex"),
                Level = B("ItmLevel"),
                Durability = U("ItmDurability"),
                StackAmount = I("ItmStackAmount"),
                MaxAmount = I("ItmMaximumAmount"),
                Armor = I("ItmArmor"),
                Hit = I("ItmHit"),
                Dam = I("ItmDam"),
                Vita = I("ItmVita"),
                Mana = I("ItmMana"),
                Might = I("ItmMight"),
                Will = I("ItmWill"),
                Grace = I("ItmGrace"),
                NoDrop = I("ItmDroppable") != 0,
                Thrown = I("ItmThrown") != 0,
                BuyPrice = I("ItmBuyPrice"),
                SellPrice = I("ItmSellPrice"),
                MightReq = I("ItmMightRequired"),
                Sound = I("ItmSound"),
                Indestructible = I("ItmIndestructible") != 0,
                MinSDam = I("ItmMinimumSDamage"),
                MaxSDam = I("ItmMaximumSDamage"),
                MinLDam = I("ItmMinimumLDamage"),
                MaxLDam = I("ItmMaximumLDamage"),
                Protection = I("ItmProtection"),
                Healing = I("ItmHealing"),
                Wisdom = I("ItmWisdom"),
                Text = Clean(col.Require("ItmText", "")),
                BuyText = Clean(col.Require("ItmBuyText", "")),
                PathId = I("ItmPthId"),
                Mark = I("ItmMark"),
                BreakOnDeath = I("ItmBoD") != 0,
                Protected = I("ItmProtected") != 0,
                Repairable = I("ItmRepairable") != 0,
                NoTrade = I("ItmExchangeable") != 0,
                NoDeposit = I("ItmDepositable") != 0,
            });
            col.Keep();
        }
        return ResolveIconColors(items);
    }

    /// <summary>Fold <c>ItmIconColor</c> into the icon for the colour runs the 4.95 client has art for.
    /// <para>Why this exists: the 4.95 client cannot recolour an item graphic. The bag/equip draw
    /// (<c>0x435ab0</c>) and the ground-object draw both call <c>0x431020(epfName, frame, dest)</c> — a frame
    /// index and nothing else — and the palette comes from Item.tbl per frame. Sun/moon/star helms are
    /// therefore ten SEPARATE Item.epf frames (265..274), not one frame plus a colour byte, and sending the
    /// bare <c>ItmIcon</c> drew the first one for all ten (the "everything is spring" bug).</para>
    /// <para>The <paramref name="items"/> pass also refuses any target that some other item already claims as
    /// its own <c>ItmIcon</c> — that catches the rows where RTK gave the variants real icons of their own and
    /// left a stale colour behind (star_armor_dress 172+2 is sun_armor_dress's icon; kabuto 265+10 is another
    /// helm), which must keep the base.</para></summary>
    private static List<ItemDef> ResolveIconColors(List<ItemDef> items)
    {
        var claimed = new HashSet<int>();
        foreach (var it in items) claimed.Add(it.Icon);

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.IconColor == 0 || Array.IndexOf(IconColorRuns, it.Icon) < 0) continue;
            int frame = it.Icon + it.IconColor;
            if (frame >= ItemIconCount || claimed.Contains(frame)) continue;
            items[i] = it with { ClientIcon = (ushort)frame };
        }
        return items;
    }
    private static bool IsExcludedMap(ushort map) => Array.Exists(ExcludedMapRanges, r => map >= r.lo && map <= r.hi);

    private static Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)> LoadWarps(
        CsvTable csv,
        IReadOnlyDictionary<ushort, MapInfo> maps)
    {
        var warps = new Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)>();
        foreach (var col in csv)
        {
            if (ushort.TryParse(col.Require("SourceMapId"), out var sm)
                && ushort.TryParse(col.Require("SourceX"), out var sx)
                && ushort.TryParse(col.Require("SourceY"), out var sy)
                && ushort.TryParse(col.Require("DestinationMapId"), out var dm)
                && ushort.TryParse(col.Require("DestinationX"), out var dx)
                && ushort.TryParse(col.Require("DestinationY"), out var dy)
                && maps.ContainsKey(dm)            // don't warp to a map the client can't render
                && !IsExcludedMap(sm) && !IsExcludedMap(dm))
            {
                warps[(sm, sx, sy)] = (dm, dx, dy);   // last write wins on duplicate source tiles
                col.Keep();
            }
        }
        return warps;
    }

    // Board-sign tiles: MapId,X,Y,BoardId. Comment/blank/un-parseable rows are skipped, so the shipped file
    // can carry documentation and be filled in live (calibrate the tile with @boardobj, then @reload).
    private static List<(ushort, ushort, ushort, int)> LoadBoardLocations(CsvTable csv)
    {
        var list = new List<(ushort, ushort, ushort, int)>();
        foreach (var col in csv)
            if (ushort.TryParse(col.Require("MapId"), out var m)
                && ushort.TryParse(col.Require("X"), out var x)
                && ushort.TryParse(col.Require("Y"), out var y)
                && int.TryParse(col.Require("BoardId"), out var bid))
                { list.Add((m, x, y, bid)); col.Keep(); }
        return list;
    }

    // Spawn points: SpnMobId,SpnMapId,SpnX,SpnY (+ RTK bookkeeping columns we ignore). Rows whose mob or
    // map is unknown are still returned; the world filters them against the loaded mob/map registries.
    // Look,Colour,Palette from Mob5xPalettes.csv. (look, era colour byte) -> colour byte to send V533.
    private static Dictionary<(ushort, byte), byte> LoadMob5xPalettes(CsvTable csv)
    {
        var pals = new Dictionary<(ushort, byte), byte>();
        foreach (var col in csv)
            if (ushort.TryParse(col.Require("Look"), out var look)
                && byte.TryParse(col.Require("Colour"), out var colour)
                && byte.TryParse(col.Require("Palette"), out var pal))
                { pals[(look, colour)] = pal; col.Keep(); }
        return pals;
    }

    // Rows are body-look RANGES (a whole Body.tbl palette band is one row per dye) rather than one row per
    // look, so the file stays readable and a new armor on an existing body is covered without a data edit.
    private static Dictionary<(ushort, byte), byte> LoadArmorDyeRamps(CsvTable csv)
    {
        var map = new Dictionary<(ushort, byte), byte>();
        foreach (var col in csv)
            if (ushort.TryParse(col.Require("BodyLookLo"), out var lo)
                && ushort.TryParse(col.Require("BodyLookHi"), out var hi)
                && byte.TryParse(col.Require("Dye"), out var dye)
                && byte.TryParse(col.Require("Ramp"), out var ramp))
                { for (ushort look = lo; look <= hi; look++) map[(look, dye)] = ramp; col.Keep(); }
        return map;
    }

    private static List<SpawnDef> LoadSpawns(CsvTable csv)
    {
        var spawns = new List<SpawnDef>();
        foreach (var col in csv)
        {
            if (int.TryParse(col.Require("SpnMobId"), out var mob)
                && ushort.TryParse(col.Require("SpnMapId"), out var map)
                && ushort.TryParse(col.Require("SpnX"), out var x)
                && ushort.TryParse(col.Require("SpnY"), out var y)
                && !ExcludedSpawnMobIds.Contains(mob))
            {
                spawns.Add(new SpawnDef(mob, map, x, y));
                col.Keep();
            }
        }
        return spawns;
    }

    /// <summary>One loader, three files, and the three do NOT share a header — so which of the two spawn
    /// systems a file belongs to is a parameter rather than something guessed from the row.
    /// <para><paramref name="grouped"/> true is the batch-group model (AreaSpawns.csv and
    /// AreaSpawnsCrafting.csv, both from RTK's handleSpawn NPCs): those carry <c>Timer</c> and
    /// <c>Group</c> and no <c>RespawnSec</c>. False is the per-point trap supplement (AreaSpawnsTrap.csv),
    /// which is the exact reverse. Splitting it this way is what lets every column stay
    /// <see cref="CsvRow.Require">Required</see>: reading all three names out of every file would report a
    /// missing column on every startup, and making them Optional would put the loudest columns in the file
    /// back into silence — rename <c>Timer</c> and all 2,588 rows would read 0, which flips
    /// <c>World.Tick</c>'s <c>ad.Timer &gt; 0</c> and quietly moves every batch hunting map onto the
    /// per-point model. Requiredness belongs to the FILE, not to the row.</para></summary>
    private static List<AreaSpawnDef> LoadAreaSpawns(CsvTable csv, bool grouped)
    {
        var spawns = new List<AreaSpawnDef>();
        foreach (var col in csv)
        {
            if (int.TryParse(col.Require("MobId"), out var mob)
                && ushort.TryParse(col.Require("Map"), out var map)
                && int.TryParse(col.Require("Count"), out var count) && count > 0
                && ushort.TryParse(col.Require("MinX"), out var minX)
                && ushort.TryParse(col.Require("MinY"), out var minY)
                && ushort.TryParse(col.Require("MaxX"), out var maxX)
                && ushort.TryParse(col.Require("MaxY"), out var maxY))
            {
                // The column a file does not have reads 0, which is exactly what it read before: every row
                // of AreaSpawns.csv and AreaSpawnsCrafting.csv carries a non-zero Timer and a Group, and
                // every row of AreaSpawnsTrap.csv carries a RespawnSec, so no value moves.
                int respawnSec = 0, timer = 0, group = 0;
                if (grouped)
                {
                    int.TryParse(col.Require("Timer", "0"), out timer);
                    int.TryParse(col.Require("Group", "0"), out group);
                }
                else
                {
                    int.TryParse(col.Require("RespawnSec", "0"), out respawnSec);
                }
                spawns.Add(new AreaSpawnDef(mob, map, count, minX, minY, maxX, maxY, respawnSec, timer, group));
                col.Keep();
            }
        }
        return spawns;
    }

    // NOTE: NPCs.csv is OUR data now (no re-extraction), so former "override" decisions are baked straight into
    // the rows rather than layered on at load. Historical record, in case a row looks surprising:
    //   * ~24 rows were DELETED — NPCs whose NpcLook exceeds the 4.95 client's Monster.tbl ceiling (327) so they
    //     render nothing (the Abyssal Crystal zodiac puzzle + its questgivers/instance chests/jukeboxes, which
    //     RTK's own team hid via a later migration), PyungPetNpc, and the 3 SalonNpc barbers (Face/Gender stays
    //     Rogue-hall-only per user direction). See [[nexustk-495-broken-npc-assets]].
    //   * A few rows were CORRECTED, e.g. NpcId 51 Bagai (map 363) moved from (2,6) to (2,3).
    //   * The Enabled column carries the on/off toggle (0 = keep the row but don't spawn). Nothing is switched
    //     off today — the inn keeps' assistants (InnNpc2: Ox, Taur) were, and are back, standing in their
    //     taverns with an EMPTY ability composition, so they belong to the scene without a click menu.

    // Stationary NPCs (our game-data/NPCs.csv). We keep only NPCs whose map the client can render and that
    // sit on a real tile (skip the (0,0) placeholders — f1npc, treasure portals — which aren't placed beings).
    // Look is the creature sprite; the world draws them via the same 0x07 path as a mob (see World.PopulateNpcs).
    // The Enabled column (default 1) is the spawn on/off switch — a disabled NPC keeps its row but World skips it.
    private static List<NpcDef> LoadNpcs(
        CsvTable csv,
        IReadOnlyDictionary<ushort, MapInfo> maps)
    {
        var npcs = new List<NpcDef>();
        foreach (var col in csv)
        {
            if (!int.TryParse(col.Require("NpcId"), out var id)) continue;
            ushort.TryParse(col.Require("NpcMapId", "0"), out var map);
            ushort.TryParse(col.Require("NpcX", "0"), out var x);
            ushort.TryParse(col.Require("NpcY", "0"), out var y);
            ushort.TryParse(col.Require("NpcLook", "0"), out var look);
            byte.TryParse(col.Require("NpcLookColor", "0"), out var color);
            int.TryParse(col.Require("NpcMoveTime", "0"), out var move);
            int.TryParse(col.Require("NpcReturnDistance", "0"), out var leash);
            bool Flag(string k) => col.Require(k, "0") == "1";
            if (!maps.ContainsKey(map)) continue;        // map the 4.95 client can't render
            if (x == 0 && y == 0) continue;              // (0,0) = unplaced placeholder / abstract NPC
            var name = Clean(col.Require("NpcDescription", ""));
            var key = Clean(col.Require("NpcIdentifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"npc{id}" : key;
            // Enabled defaults ON: a blank/absent column means the NPC spawns (only an explicit 0 disables).
            bool enabled = col.Require("Enabled", "1").Trim() != "0";
            // ...and an NPC who did not EXIST yet at the target date doesn't spawn either. This is the one era
            // gate that removes a being rather than muting one, and it is deliberately narrow: it is for an NPC
            // whose whole reason to stand there postdates us (Yarlof arrived with the 2005 Druid bouquet quest),
            // NOT for an old NPC who gained a new quest — gate that in his script and leave him standing. Blank
            // is the overwhelming majority and means undated, and an unknown key reads as present, so a typo
            // here can only leave someone in the world, never silently delete him.
            var eraFeature = Clean(col.Require("EraFeature", ""));
            if (eraFeature.Length > 0 && !Era.Has(eraFeature)) enabled = false;
            npcs.Add(new NpcDef
            {
                Id = id,
                Key = key,
                Name = name,
                Map = map,
                X = x,
                Y = y,
                Dir = 2,
                Look = look,
                Color = color,
                IsChar = Flag("NpcIsChar"),
                Shop = Flag("NpcIsShopNpc"),
                Repair = Flag("NpcIsRepairNpc"),
                Bank = Flag("NpcIsBankNpc"),
                MoveTime = move,
                ReturnDistance = leash,
                Enabled = enabled,
                EraFeature = eraFeature,
            });
            col.Keep();
        }
        return npcs;
    }

    private static (
        Dictionary<int, string> Paths,
        Dictionary<int, string[]> Ranks,
        Dictionary<int, int> Bases,
        Dictionary<int, int> Icons,
        Dictionary<int, int> Chats) LoadPaths(CsvTable csv)
    {
        var paths = new Dictionary<int, string>();
        var ranks = new Dictionary<int, string[]>();
        var bases = new Dictionary<int, int>();
        var icons = new Dictionary<int, int>();
        var chats = new Dictionary<int, int>();
        foreach (var col in csv)
            if (int.TryParse(col.Require("PthId"), out var id))
            {
                var ladder = new string[MaxPathRank + 1];
                for (int m = 0; m <= MaxPathRank; m++) ladder[m] = Clean(col.Require($"PthMark{m}", ""));
                paths[id] = ladder[0];
                ranks[id] = ladder;
                bases[id] = int.TryParse(col.Require("PthType"), out var t) ? t : 0;
                icons[id] = int.TryParse(col.Require("PthIcon"), out var ic) ? ic : 0;
                // RTK class_db's `chat` (classdb_chat): non-zero on the twelve PC subpaths and the GM row Archon
                // only, and the gate on RTK's subpath channel — see Content.HasSubpathChat.
                chats[id] = int.TryParse(col.Require("PthChat", ""), out var ch) ? ch : 0;
                col.Keep();
            }
        return (paths, ranks, bases, icons, chats);
    }

    private static Dictionary<(int, string), (int, string)> LoadArmorQuestGates(CsvTable csv)
    {
        var gates = new Dictionary<(int, string), (int, string)>();
        foreach (var col in csv)
        {
            if (!int.TryParse(col.Require("Path"), out var p)) continue;
            var tier = col.Require("Tier", "").Trim().ToLowerInvariant();
            if (tier.Length == 0) continue;
            if (!int.TryParse(col.Require("Level"), out var lvl)) continue;
            var karma = col.Require("Karma", "").Trim();
            if (karma.Length == 0) continue;
            gates[(p, tier)] = (lvl, karma);
            col.Keep();
        }
        return gates;
    }

    // See CraftingToggleOverrides above. Sparse by design — a skill missing from the file (or the file
    // missing entirely) just falls through to CraftingToggles.DefaultDisabled.
    private static Dictionary<string, bool> LoadCraftingToggles(CsvTable csv)
    {
        var overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var skill = col.Require("Skill", "").Trim();
            if (skill.Length == 0) continue;
            if (int.TryParse(col.Require("Enabled"), out var en)) { overrides[skill] = en != 0; col.Keep(); }
        }
        return overrides;
    }

    // See WarpQuestLocks above. A row whose MinStage doesn't parse is skipped rather than defaulted to 0 —
    // a lock that silently became "always open" would look identical to no lock at all, and the whole
    // point of one is that the player isn't carried onward early.
    private static Dictionary<(ushort From, ushort To), WarpQuestLock> LoadWarpQuestLocks(CsvTable csv)
    {
        var bars = new Dictionary<(ushort, ushort), WarpQuestLock>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("FromMap"), out var from)) continue;
            if (!ushort.TryParse(col.Require("ToMap"), out var to)) continue;
            var key = col.Require("QuestKey", "").Trim();
            if (key.Length == 0) continue;
            if (!int.TryParse(col.Require("MinStage"), out var min)) continue;
            var msg = col.Require("Message", "").Trim();
            if (msg.Length == 0) msg = "You are not yet ready to proceed.";
            bars[(from, to)] = new WarpQuestLock(from, to, key, min, msg);
            col.Keep();
        }
        return bars;
    }

    // See MythicCaves above. One row per zodiac animal. EntranceTiles is a ';'-separated list of "x:y" pairs
    // (2 per cave in retail). T{1,2,3}{Level,Vita,Mana} give the cave-1/2/3 gates; a 0 Vita/Mana means that
    // tier is level-only. A malformed/absent file yields an empty registry (entrances then never gate — the
    // player is held out only where a row exists), same fail-soft posture as every other loader here.
    private static List<MythicCaveDef> LoadMythicCaves(CsvTable csv)
    {
        var list = new List<MythicCaveDef>();
        foreach (var col in csv)
        {
            var animal = col.Require("Animal", "").Trim();
            if (animal.Length == 0) continue;
            ushort U(string k) => ushort.TryParse(col.Require(k), out var v) ? v : (ushort)0;
            uint U32(string k) => uint.TryParse(col.Require(k), out var v) ? v : 0u;

            var tiles = new List<(ushort X, ushort Y)>();
            foreach (var pair in col.Require("EntranceTiles").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(':');
                if (xy.Length == 2 && ushort.TryParse(xy[0].Trim(), out var tx) && ushort.TryParse(xy[1].Trim(), out var ty))
                    tiles.Add((tx, ty));
            }

            var tiers = new MythicTier[3];
            for (int t = 1; t <= 3; t++)
                tiers[t - 1] = new MythicTier((byte)U($"T{t}Level"), U32($"T{t}Vita"), U32($"T{t}Mana"));

            list.Add(new MythicCaveDef(animal, U("EntranceMap"), tiles.ToArray(),
                U("DestMap"), U("DestX"), U("DestY"), tiers, col.Require("Sources", "")));
            col.Keep();
        }
        return list;
    }

    // See MythicAlliances above. One row per zodiac animal. KeyBosses/ItemBosses are ';'-separated, cave 1
    // first; a row is dropped unless BOTH name at least one boss and the row names an enemy, because a
    // half-declared alliance would offer a quest that can never be finished and would look to a player
    // exactly like a very hard one.
    private static List<MythicAllianceDef> LoadMythicAlliances(CsvTable csv)
    {
        var list = new List<MythicAllianceDef>();
        foreach (var col in csv)
        {
            var animal = col.Require("Animal", "").Trim();
            var enemy  = col.Require("Enemy", "").Trim();
            if (animal.Length == 0 || enemy.Length == 0) continue;

            static string[] Split(string? v) =>
                (v ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int I(string k, int dflt = 0) => int.TryParse(col.Require(k), out var v) ? v : dflt;

            var keyBosses  = Split(col.Require("KeyBosses"));
            var itemBosses = Split(col.Require("ItemBosses"));
            if (keyBosses.Length == 0 || itemBosses.Length == 0) continue;

            list.Add(new MythicAllianceDef(
                animal, I("NpcId"), enemy, keyBosses, itemBosses,
                col.Require("KeyDrop", "").Trim(),  I("KeyTribute"),
                col.Require("ItemDrop", "").Trim(), I("ItemTribute"),
                col.Require("Favor", "").Trim(),
                uint.TryParse(col.Require("Exp"), out var xp) ? xp : 0u,
                double.TryParse(col.Require("Karma"), out var km) ? km : 0.0,
                col.Require("Sources", "")));
            col.Keep();
        }
        return list;
    }

    // See ArenaDoors above. One row per door (a door is the 2 adjacent tiles the sprite occupies). Tiles is
    // ';'-separated "x:y"; DestX may be a "lo-hi" range. MaxLevel/MaxVita/MaxMana of 0 mean "no cap".
    private static List<ArenaDoorDef> LoadArenaDoors(CsvTable csv)
    {
        var list = new List<ArenaDoorDef>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("Map"), out var map)) continue;
            ushort U(string k) => ushort.TryParse(col.Require(k), out var v) ? v : (ushort)0;
            int I(string k) => int.TryParse(col.Require(k), out var v) ? v : 0;
            uint U32(string k) => uint.TryParse(col.Require(k), out var v) ? v : 0u;

            var tiles = new List<(ushort X, ushort Y)>();
            foreach (var pair in col.Require("Tiles").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(':');
                if (xy.Length == 2 && ushort.TryParse(xy[0].Trim(), out var tx) && ushort.TryParse(xy[1].Trim(), out var ty))
                    tiles.Add((tx, ty));
            }
            if (tiles.Count == 0) continue;

            // DestX is either a single column or a "lo-hi" band the landing tile is rolled from.
            var span = col.Require("DestX").Split('-', 2);
            ushort.TryParse(span[0].Trim(), out var dx);
            var dx2 = span.Length > 1 && ushort.TryParse(span[1].Trim(), out var hi) ? hi : dx;

            list.Add(new ArenaDoorDef(map, tiles.ToArray(), U("DestMap"), dx, dx2, U("DestY"),
                I("MinLevel"), I("MaxLevel"), U32("MaxVita"), U32("MaxMana"),
                I("Unmarked") != 0, col.Require("Label", "").Trim(), col.Require("Sources", "")));
            col.Keep();
        }
        return list;
    }

    // See EventCaveBands above. One row per band of the shared tier ladder, matched in file order, so the
    // FILE's order is the semantics — do not sort it. Blank/absent Mark columns give 0..0, which is what a
    // pure level band wants (a subpath rank only exists at 99). A malformed/absent file yields an empty
    // ladder, which makes every event-cave doorway refuse rather than dumping people into tier 1 blind.
    private static List<EventCaveBand> LoadEventCaveBands(CsvTable csv)
    {
        var list = new List<EventCaveBand>();
        foreach (var col in csv)
        {
            int I(string k, int dflt = 0) => int.TryParse(col.Require(k), out var v) ? v : dflt;
            int tier = I("Tier");
            if (tier <= 0) continue;
            list.Add(new EventCaveBand(tier, I("AltTier"), I("MinLevel"), I("MaxLevel"), I("MinMark"), I("MaxMark"),
                Clean(col.Require("Label", "")), col.Require("Sources", "")));
            col.Keep();
        }
        return list;
    }

    // See EventCaves above. One row per entrance. EntranceTiles is ';'-separated "x:y" (same encoding as
    // MythicCaves/ArenaDoors); TierMaps and Pages are '|'-separated, shallowest page/tier first. A row with
    // no tiles or no destination maps is dropped rather than half-registered — a doorway that intercepts the
    // step and then has nowhere to send anyone is worse than one that stays an ordinary tile.
    private static List<EventCaveDef> LoadEventCaves(CsvTable csv)
    {
        var list = new List<EventCaveDef>();
        foreach (var col in csv)
        {
            var key = Clean(col.Require("Key", ""));
            if (key.Length == 0) continue;
            ushort U(string k) => ushort.TryParse(col.Require(k), out var v) ? v : (ushort)0;
            string S(string k) => Clean(col.Require(k, ""));

            var tiles = new List<(ushort X, ushort Y)>();
            foreach (var pair in col.Require("EntranceTiles").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(':');
                if (xy.Length == 2 && ushort.TryParse(xy[0].Trim(), out var tx) && ushort.TryParse(xy[1].Trim(), out var ty))
                    tiles.Add((tx, ty));
            }
            if (tiles.Count == 0) continue;

            var maps = col.Require("TierMaps").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(m => ushort.TryParse(m.Trim(), out var mv) ? mv : (ushort)0)
                .Where(m => m != 0).ToArray();
            if (maps.Length == 0) continue;

            var pages = S("Pages").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

            list.Add(new EventCaveDef(key, U("EntranceMap"), tiles.ToArray(), maps, U("DestX"), U("DestY"),
                pages, S("Prompt"), S("OptionNear"), S("OptionFar"), S("DenyMsg"),
                col.Require("Sources", "")));
            col.Keep();
        }
        return list;
    }

    // ---- Location / warp geometry loaders (see the Content.* registries near MythicCaves) ----------------

    // MusicTracks.csv: Track,Name,Kind[,Set] — the id<->name table for both soundtracks. `Kind` is what the
    // client will be asked to open, and it implies the rest:
    //   midi    -> 0x19 type 2, MusicSet.Old   (N.mid, ids 1-12 only — both clients hard-cap there)
    //   mp3     -> 0x19 type 1, MusicSet.New   (one song; on 5.33 it repeats forever, see the MusicTrack doc)
    //   list    -> 0x19 type 1, MusicSet.New   (%08d.LST — ten tracks in order, wraps forever. Map music.)
    //   shuffle -> 0x19 type 1, MusicSet.New   (%08d.LSR — the same ten from a random start, but STALLS:
    //                                           audition only, never a map assignment. See MusicTrack.)
    // An explicit `Set` column overrides the Kind's default set; a row with neither reads as an old midi,
    // which is what every row of this file was before the 5.x set existed.
    private static List<MusicTrack> LoadMusicTracks(CsvTable csv)
    {
        var list = new List<MusicTrack>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("Track"), out var id)) continue;
            var name = col.Require("Name", "").Trim();
            var kind = col.Require("Kind", "").Trim().ToLowerInvariant();
            bool playlist = kind is "list" or "shuffle";
            bool shuffle = kind is "shuffle";
            byte type = kind is "mp3" or "list" or "shuffle" ? (byte)1 : (byte)2;
            // Legacy `Type` column still wins if a deployed CSV predates `Kind`.
            if (kind.Length == 0 && byte.TryParse(col.Require("Type"), out var t)) type = t;
            var set = col.Require("Set", "").Trim()
                .Equals("new", StringComparison.OrdinalIgnoreCase) || type == 1 ? MusicSet.New : MusicSet.Old;
            list.Add(new MusicTrack(id, name, type, set, playlist, shuffle));
            col.Keep();
        }
        return list;
    }

    // MapBgm.csv: Zone,Track,Track5x,Maps,Names — one row per AREA. `Track` (the old/midi soundtrack) and
    // `Track5x` (the 5.x one) are each a MusicTracks.csv name or a raw id; `Maps` is a ';'-separated list of
    // ids and lo-hi ranges; `Names` is a ';'-separated list of map-name globs. The row whose Zone is
    // "Default" is pulled out as the fresh-session fallback (DefaultBgm / DefaultBgmNew).
    private static (List<BgmZone>, (ushort, byte)?, (ushort, byte)?) LoadBgmZones(
        CsvTable csv,
        IReadOnlyList<MusicTrack> musicTracks)
    {
        var zones = new List<BgmZone>();
        (ushort, byte)? def = null, defNew = null;

        foreach (var col in csv)
        {
            var zone = col.Require("Zone", "").Trim();
            var track = FindTrack(col.Require("Track", ""), MusicSet.Old, musicTracks);
            if (zone.Length == 0 || track is null) continue;
            // No Track5x -> the zone's midi, which 5.33 plays too (its Snd.dat carries the same 12 files).
            var track5x = FindTrack(col.Require("Track5x", ""), MusicSet.New, musicTracks) ?? track;

            if (zone.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                def = (track.Id, track.Type);
                defNew = (track5x.Id, track5x.Type);
                col.Keep();
                continue;
            }

            var maps = new List<(ushort, ushort)>();
            foreach (var part in col.Require("Maps", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var span = part.Split('-', 2);
                if (ushort.TryParse(span[0].Trim(), out var lo))
                    maps.Add((lo, span.Length > 1 && ushort.TryParse(span[1].Trim(), out var hi) ? hi : lo));
            }
            var names = col.Require("Names", "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            zones.Add(new BgmZone(zone, track.Id, track.Type, track5x.Id, track5x.Type, maps, names));
            col.Keep();
        }
        return (zones, def, defNew);
    }

    private static Dictionary<string, IReadOnlyList<InnDef>> LoadInns(CsvTable csv)
    {
        var acc = new Dictionary<string, List<InnDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var g = col.Require("Group", "").Trim();
            if (g.Length == 0 || !ushort.TryParse(col.Require("Map"), out var m)) continue;
            ushort.TryParse(col.Require("X"), out var x);
            ushort.TryParse(col.Require("Y"), out var y);
            // Blank/unparseable X2,Y2 collapses the box to the single tile X,Y — the normal case.
            if (!ushort.TryParse(col.Require("X2"), out var x2) || x2 < x) x2 = x;
            if (!ushort.TryParse(col.Require("Y2"), out var y2) || y2 < y) y2 = y;
            if (!acc.TryGetValue(g, out var list)) acc[g] = list = new List<InnDef>();
            list.Add(new InnDef(m, x, y, x2, y2));
            col.Keep();
        }
        return acc.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<InnDef>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static List<ForageAreaDef> LoadForageAreas(CsvTable csv)
    {
        var list = new List<ForageAreaDef>();
        foreach (var col in csv)
        {
            var key = col.Require("ItemKey", "").Trim();
            if (key.Length == 0) continue;
            int I(string k) => int.TryParse(col.Require(k), out var v) ? v : 0;
            list.Add(new ForageAreaDef(key, (ushort)I("Map"), I("MinX"), I("MaxX"), I("MinY"), I("MaxY"),
                I("Max"), I("MinQty"), I("MaxQty")));
            col.Keep();
        }
        return list;
    }

    // HarvestNodes.csv. Weighted cells are `key:number` pipe-separated; a cell with no number defaults to
    // weight 1 so a single-item table can be written as just the key.
    private static Dictionary<string, HarvestNodeDef> LoadHarvestNodes(CsvTable csv)
    {
        var d = new Dictionary<string, HarvestNodeDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var node = Clean(col.Require("NodeMob", ""));
            if (node.Length == 0) continue;
            int I(string k) => int.TryParse(col.Require(k), out var v) ? v : 0;
            (string, double)[] Weighted(string k) =>
                Clean(col.Require(k, "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Split(':'))
                    .Select(p => (p[0].Trim(),
                                  p.Length > 1 && double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1))
                    .Where(t => t.Item1.Length > 0).ToArray();

            d[node] = new HarvestNodeDef(node,
                Clean(col.Require("Tools", "")).Split('|', StringSplitOptions.RemoveEmptyEntries),
                Clean(col.Require("Skill", "")),
                Weighted("Yield"), I("Rolls"), Weighted("Bonus"),
                Clean(col.Require("BreakChance", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out var v) ? v : 0).ToArray(),
                Clean(col.Require("Message", "")));
            col.Keep();
        }
        return d;
    }

    // MobSpells.csv — several rows per mob, kept in file order (the roll walks them and takes the first hit).
    private static Dictionary<string, MobSpellDef[]> LoadMobSpells(CsvTable csv)
    {
        var d = new Dictionary<string, List<MobSpellDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var key = Clean(col.Require("MobKey", ""));
            if (key.Length == 0) continue;
            int I(string k, int dflt = 0) => int.TryParse(col.Require(k), out var v) ? v : dflt;
            d.TryAdd(key, new List<MobSpellDef>());
            d[key].Add(new MobSpellDef(key,
                Clean(col.Require("Name", "")), Clean(col.Require("Effect", "")).ToLowerInvariant(),
                I("Chance", 1), I("EveryMs"), I("Range", 1), I("Amount"),
                Clean(col.Require("Stat", "")), Clean(col.Require("Category", "")),
                I("DurationMs"), I("Anim"), I("Sound"), Clean(col.Require("Say", "")),
                I("PerTick"), I("TickMinMs"), I("TickMaxMs"),
                Clean(col.Require("Trigger", "")).ToLowerInvariant()));
            col.Keep();
        }
        return d.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    // MobSpawnRules.csv. The "*" row is the global default rather than a mob (see MobHpJitter).
    private static Dictionary<string, MobSpawnRuleDef> LoadMobSpawnRules(CsvTable csv)
    {
        var d = new Dictionary<string, MobSpawnRuleDef>(StringComparer.OrdinalIgnoreCase);
        MobHpJitter = false;
        foreach (var col in csv)
        {
            var key = Clean(col.Require("MobKey", ""));
            if (key.Length == 0) continue;
            if (key == "*") { MobHpJitter = Clean(col.Require("HpJitter", "")) == "1"; col.Keep(); continue; }

            var rooms = Clean(col.Require("Rooms", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(':'))
                .Where(p => p.Length == 3)
                .Select(p => (ushort.TryParse(p[0], out var mp) ? mp : (ushort)0,
                              ushort.TryParse(p[1], out var x) ? x : (ushort)0,
                              ushort.TryParse(p[2], out var y) ? y : (ushort)0))
                .Where(t => t.Item1 != 0).ToArray();
            int.TryParse(col.Require("MaxAlive"), out var max);
            var capMaps = Clean(col.Require("CapMaps", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => ushort.TryParse(s, out var v) ? v : (ushort)0).Where(v => v != 0).ToArray();
            int.TryParse(col.Require("FleeBelowPct"), out var fleePct);
            int.TryParse(col.Require("SpawnChance"), out var chance);
            int.TryParse(col.Require("DeathCooldownSec"), out var cooldown);
            if (rooms.Length == 0 && max <= 0 && fleePct <= 0 && chance <= 0 && cooldown <= 0) continue;
            d[key] = new MobSpawnRuleDef(key, rooms, max, capMaps, fleePct, chance, cooldown);
            col.Keep();
        }
        return d;
    }

    private static Dictionary<string, MobBossDef> LoadMobBosses(CsvTable csv)
    {
        var d = new Dictionary<string, MobBossDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var key = Clean(col.Require("MobKey", ""));
            if (key.Length == 0) continue;
            int I(string k, int dflt = 0) => int.TryParse(col.Require(k), out var v) ? v : dflt;
            d[key] = new MobBossDef(key, I("HealAmount"), I("HealChance", 2), I("ParaBreakChance", 2),
                                    I("LastStandMs"), I("Anim"), I("Sound"));
            col.Keep();
        }
        return d;
    }

    // MobChatter.csv — Lines is |-separated.
    private static Dictionary<string, MobChatterDef> LoadMobChatter(CsvTable csv)
    {
        var d = new Dictionary<string, MobChatterDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var key = Clean(col.Require("MobKey", ""));
            if (key.Length == 0) continue;
            var lines = Clean(col.Require("Lines", "")).Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;
            int.TryParse(col.Require("Chance"), out var chance);
            byte.TryParse(col.Require("Channel"), out var channel);
            d[key] = new MobChatterDef(key, Math.Max(1, chance), channel, lines);
            col.Keep();
        }
        return d;
    }

    private static Dictionary<ushort, PathHallDef> LoadPathHalls(CsvTable csv)
    {
        var d = new Dictionary<ushort, PathHallDef>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("HallMap"), out var hall)) continue;
            int I(string k) => int.TryParse(col.Require(k), out var v) ? v : 0;
            ushort U(string k) => (ushort)I(k);
            d[hall] = new PathHallDef(I("BaseClass"), U("GuildMap"),
                new[] { U("SanctumUnaligned"), U("SanctumKwisin"), U("SanctumMingken"), U("SanctumOhaeng") });
            col.Keep();
        }
        return d;
    }

    private static Dictionary<int, GatewayDef> LoadGatewayGates(CsvTable csv)
    {
        var acc = new Dictionary<int, (ushort map, string city, Dictionary<char, (int Xlo, int Xhi, int Ylo, int Yhi)> gates)>();
        foreach (var col in csv)
        {
            if (!int.TryParse(col.Require("Region"), out var region)) continue;
            var gate = col.Require("Gate", "").Trim().ToLowerInvariant();
            if (gate.Length == 0) continue;
            ushort.TryParse(col.Require("Map"), out var map);
            var city = col.Require("City", "").Trim();
            int I(string k) => int.TryParse(col.Require(k), out var v) ? v : 0;
            if (!acc.TryGetValue(region, out var r)) acc[region] = r = (map, city, new());
            r.gates[gate[0]] = (I("Xlo"), I("Xhi"), I("Ylo"), I("Yhi"));
            col.Keep();
        }
        return acc.ToDictionary(kv => kv.Key, kv => new GatewayDef(kv.Value.map, kv.Value.city,
            (IReadOnlyDictionary<char, (int Xlo, int Xhi, int Ylo, int Yhi)>)kv.Value.gates));
    }

    private static List<WorldDestDef> LoadWorldDests(CsvTable csv)
    {
        var list = new List<WorldDestDef>();
        foreach (var col in csv)
        {
            var name = col.Require("Name", "").Trim();
            if (name.Length == 0) continue;
            int I(string k) => int.TryParse(col.Require(k), out var v) ? v : 0;
            list.Add(new WorldDestDef(name, (ushort)I("Map"), (ushort)I("X"), (ushort)I("Y"), I("DotX"), I("DotY")));
            col.Keep();
        }
        return list;
    }

    private static Dictionary<ushort, WorldTriggerDef> LoadWorldTriggers(CsvTable csv)
    {
        var d = new Dictionary<ushort, WorldTriggerDef>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("Map"), out var m)) continue;
            var axis = col.Require("FixedAxis", "x").Trim().ToLowerInvariant();
            int I(string k) => int.TryParse(col.Require(k), out var v) ? v : 0;
            d[m] = new WorldTriggerDef(axis.Length > 0 ? axis[0] : 'x', I("FixedLo"), I("FixedHi"), I("RangeLo"), I("RangeHi"));
            col.Keep();
        }
        return d;
    }

    private static Dictionary<ushort, (ushort Map, ushort X, ushort Y)> LoadFallRooms(CsvTable csv)
    {
        var d = new Dictionary<ushort, (ushort, ushort, ushort)>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("DestMap"), out var dest)) continue;
            ushort.TryParse(col.Require("DestX"), out var dx);
            ushort.TryParse(col.Require("DestY"), out var dy);
            bool tiered = col.Require("Tiered", "0") == "1";
            foreach (var s in col.Require("SrcMaps").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ushort.TryParse(s.Trim(), out var src)) continue;
                if (tiered)
                    for (ushort off = 0; off <= 4000; off += 3000)   // 0 = cave 1, +3000 = cave 2, +4000 = cave 3
                        d[(ushort)(src + off)] = ((ushort)(dest + off), dx, dy);
                else
                    d[src] = (dest, dx, dy);
                col.Keep();
            }
        }
        return d;
    }

    // AmbushBursts.csv: burst-table name -> its list of weighted variant mob-id vectors. A trap firing picks
    // one variant at random and spawns every id in it. Extractor-generated (re/extract_ambush_tables.py).
    private static Dictionary<string, IReadOnlyList<int[]>> LoadAmbushBursts(CsvTable csv)
    {
        var acc = new Dictionary<string, List<int[]>>();
        foreach (var col in csv)
        {
            var table = col.Require("Table", "").Trim();
            if (table.Length == 0) continue;
            var ids = col.Require("MobIds").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToArray();
            if (ids.Length == 0) continue;
            if (!acc.TryGetValue(table, out var list)) { list = new(); acc[table] = list; }
            list.Add(ids);
            col.Keep();
        }
        return acc.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int[]>)kv.Value);
    }

    // AmbushConfig.csv: per-map trap trigger config. Maps column is a ';'-list of single ids and "lo-hi"
    // ranges (already the concrete tier maps — no auto-expansion, since each tier points at its own burst
    // table). Primary is "burst:<table>" | "single:<id>" | "ogre:<id>[/<altId>/<altChance>]" | "".
    private static Dictionary<ushort, AmbushMapDef> LoadAmbushConfig(CsvTable csv, IReadOnlyDictionary<string, IReadOnlyList<int[]>> bursts)
    {
        var d = new Dictionary<ushort, AmbushMapDef>();
        foreach (var col in csv)
        {
            var maps = ParseMapList(col.Require("Maps", ""));
            if (maps.Count == 0) continue;
            int I(string k, int dflt) => int.TryParse(col.Require(k), out var v) ? v : dflt;
            var def = new AmbushMapDef
            {
                Count = I("Count", 12), MobCap = I("MobCap", 50),
                Message = col.Require("Message", "You stepped on a trap!"),
                SentryTable = col.Require("SentryTable", "").Trim(), SentryTopY = I("SentryTopY", 0),
                BigTable = col.Require("BigTable", "").Trim(), BigChance = I("BigChance", 0),
            };
            var primary = col.Require("Primary", "").Trim();
            if (primary.StartsWith("burst:")) { def.PrimaryKind = "burst"; def.PrimaryTable = primary["burst:".Length..]; }
            else if (primary.StartsWith("single:")) { def.PrimaryKind = "single"; int.TryParse(primary["single:".Length..], out def.PrimaryMob); }
            else if (primary.StartsWith("ogre:"))
            {
                def.PrimaryKind = "ogre";
                var parts = primary["ogre:".Length..].Split('/');
                int.TryParse(parts[0], out def.PrimaryMob);
                if (parts.Length >= 3) { int.TryParse(parts[1], out def.OgreAltMob); int.TryParse(parts[2], out def.OgreAltChance); }
            }
            // Fail loud on a config that points at a burst table the extractor didn't produce (typo / stale name).
            foreach (var t in new[] { def.PrimaryTable, def.SentryTable, def.BigTable })
                if (t.Length > 0 && !bursts.ContainsKey(t))
                    Log.Info($"WARN AmbushConfig: map(s) '{col.Require("Maps")}' reference unknown burst table '{t}'");
            foreach (var m in maps) d[m] = def;
            col.Keep();
        }
        return d;
    }

    // Parse a ';'-list of map ids where each entry is a single id ("208") or an inclusive "lo-hi" range ("90-96").
    private static List<ushort> ParseMapList(string s)
    {
        var result = new List<ushort>();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            int dash = p.IndexOf('-');
            if (dash > 0 && ushort.TryParse(p[..dash], out var lo) && ushort.TryParse(p[(dash + 1)..], out var hi))
                for (int m = lo; m <= hi; m++) result.Add((ushort)m);
            else if (ushort.TryParse(p, out var one)) result.Add(one);
        }
        return result;
    }

    private static Dictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> LoadPathGrowth(CsvTable csv)
    {
        var d = new Dictionary<int, (int, int, int, int)>();
        foreach (var c in csv)
        {
            if (!int.TryParse(c.Require("path"), out var p)) continue;
            int.TryParse(c.Require("hpMin", "0"), out var a);
            int.TryParse(c.Require("hpMax", "0"), out var b);
            int.TryParse(c.Require("mpMin", "0"), out var e);
            int.TryParse(c.Require("mpMax", "0"), out var f);
            d[p] = (a, b, e, f);
            c.Keep();
        }
        return d;
    }

    // ServerTuning.csv: named scalar config, key -> double (typed accessors above apply per-key defaults).
    private static Dictionary<string, double> LoadTuning(CsvTable csv)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("key", "").Trim();
            if (k.Length == 0) continue;
            if (double.TryParse(c.Require("value", ""), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                { d[k] = v; c.Keep(); }
        }
        return d;
    }

    // DoorObjects.csv: two row kinds. `map` rows are exact faced-object swaps (result = `;`-separated new ids at
    // startDx); `delta` rows are single-tile [lo,hi] ranges whose result is a signed delta added to the faced id.
    // The optional `defaultOpen` column (1 on a `map` row) marks that row's faced id as the CLOSED state of a
    // door that should start open — MapData.Load rewrites those cells as the file is read, per cell, so a
    // multi-tile run needs the flag on every one of its pieces (see DoorDefaultOpen).
    private static (Dictionary<int, (int, ushort[])>, List<(int, int, int)>, Dictionary<int, ushort>)
        LoadDoorObjects(CsvTable csv)
    {
        var swaps = new Dictionary<int, (int, ushort[])>();
        var deltas = new List<(int, int, int)>();
        var open = new Dictionary<int, ushort>();
        foreach (var c in csv)
        {
            var kind = c.Require("kind", "").Trim();
            if (!int.TryParse(c.Require("lo"), out var lo)) continue;
            if (!int.TryParse(c.Require("hi"), out var hi)) continue;
            var result = c.Require("result", "").Trim();
            if (kind == "map")
            {
                int.TryParse(c.Require("startDx", "0"), out var dx);
                var ids = result.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => ushort.TryParse(s, out var u) ? u : (ushort)0).ToArray();
                if (ids.Length == 0) continue;
                swaps[lo] = (dx, ids);   // map rows use lo == hi as the exact faced id
                c.Keep();
                // This piece's own counterpart sits at -startDx in the run (startDx is how far LEFT the run
                // starts from the faced tile), so the substitution stays single-cell and order-independent.
                if (c.Require("defaultOpen", "").Trim() == "1" && -dx >= 0 && -dx < ids.Length)
                    open[lo] = ids[-dx];
            }
            else if (kind == "delta" && int.TryParse(result, out var d))
            {
                deltas.Add((lo, hi, d));
                c.Keep();
            }
        }
        return (swaps, deltas, open);
    }

    // NpcAbilities.csv: NpcKey -> its abilities, in order (names resolved to instances by NpcScripts.AbilityByName).
    // A token is `name` (every NPC of the identifier), `name@id;id` (only those NPC ids) or `name@map:id;id` (only
    // the NPCs of the identifier standing on those maps); the file's header is the reference. A narrowing that
    // names nothing real is logged here, at load, and kept as written: an id with no NPC of this identifier
    // behind it matches nobody at run time too, so the ability stays OFF rather than falling open onto every
    // NPC of the identifier. A malformed id is dropped for the same reason, never read as "no narrowing".
    private static Dictionary<string, NpcAbilityRef[]> LoadNpcCompositions(CsvTable csv, IReadOnlyList<NpcDef> npcs,
        IReadOnlyDictionary<ushort, MapInfo> maps)
    {
        var npcById = IndexFirst(npcs, n => n.Id);
        var d = new Dictionary<string, NpcAbilityRef[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("NpcKey", "").Trim();
            if (k.Length == 0) continue;
            d[k] = c.Require("Abilities", "")
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token => ParseNpcAbility(k, token, npcs, npcById, maps))
                .ToArray();
            c.Keep();
        }
        return d;
    }

    internal static NpcAbilityRef ParseNpcAbility(string key, string token, IReadOnlyList<NpcDef> npcs,
        IReadOnlyDictionary<int, NpcDef> npcById, IReadOnlyDictionary<ushort, MapInfo> maps)
    {
        int at = token.IndexOf('@');
        if (at < 0) return new NpcAbilityRef(token);

        string name = token[..at].Trim();
        string spec = token[(at + 1)..].Trim();
        bool byMap = spec.StartsWith("map:", StringComparison.OrdinalIgnoreCase);
        if (byMap) spec = spec["map:".Length..];
        string where = $"NpcAbilities.csv row NpcKey='{key}', '{token}'";

        var ids = new HashSet<int>();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out int id))
            {
                Log.Warn($"{where}: '{part}' is not {(byMap ? "a map" : "an NPC")} id — ignored");
                continue;
            }
            ids.Add(id);
            if (byMap)
            {
                if (id > ushort.MaxValue || !maps.ContainsKey((ushort)id))
                    Log.Warn($"{where}: map {id} is not in the maps table — the ability reaches nobody through it");
                else if (!npcs.Any(n => n.Map == id && string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase)))
                    Log.Warn($"{where}: no {key} stands on map {id} — the ability reaches nobody through it");
            }
            else if (!npcById.TryGetValue(id, out var npc))
                Log.Warn($"{where}: NPC {id} is not in NPCs.csv (or was dropped at load) — the ability reaches nobody through it");
            else if (!string.Equals(npc.Key, key, StringComparison.OrdinalIgnoreCase))
                Log.Warn($"{where}: NPC {id} ({npc.Name}) is a {npc.Key}, not a {key} — the ability reaches nobody through it");
        }
        if (ids.Count == 0)
            Log.Warn($"{where}: narrowed to no {(byMap ? "map" : "NPC")} at all — the ability is on nobody");

        return byMap ? new NpcAbilityRef(name, MapIds: ids) : new NpcAbilityRef(name, NpcIds: ids);
    }

    // Load a verb/row params CSV into "key -> whole row" — shared by SpellParams and ItemParams (both feed a Lua
    // verb that reads whatever columns it needs). Rows are keyed by the `key` column; the `verb` column names
    // the Lua verb.
    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadKeyedRows(CsvTable csv)
    {
        var d = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var key = col.Require("key", "").Trim();
            if (key.Length == 0) continue;
            d[key] = col.ToDictionary();   // the whole row, verbatim — the Lua verb reads whatever columns it needs
            col.Keep();
        }
        return d;
    }

    private static Dictionary<string, IReadOnlyList<(string Name, string[] Keys)>> LoadShopCatalogues(CsvTable csv)
    {
        var acc = new Dictionary<string, List<(string Name, string[] Keys)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var npc = col.Require("NpcKey", "").Trim();
            var cat = col.Require("Category", "").Trim();
            if (npc.Length == 0 || cat.Length == 0) continue;
            var keys = col.Require("ItemKeys").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (!acc.TryGetValue(npc, out var list)) acc[npc] = list = new();
            list.Add((cat, keys));
            col.Keep();
        }
        return acc.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(string, string[])>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // MapCells.csv -> per-map authored cell overrides. Blank value column = inherit from the .map file, so
    // "Map,X,Y,,0," means "make this tile walkable, leave its graphics alone". Rows for maps that don't exist
    // are kept: the map may simply not be in the registry yet, and MapData only ever asks for its own id.
    private static (Dictionary<ushort, List<CellOverride>>, int) LoadMapCells(CsvTable csv)
    {
        var d = new Dictionary<ushort, List<CellOverride>>();
        int n = 0;
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("Map"), out var m)) continue;
            if (!ushort.TryParse(col.Require("X"), out var x)) continue;
            if (!ushort.TryParse(col.Require("Y"), out var y)) continue;
            ushort? U(string k)
            {
                var v = col.Require(k);
                return string.IsNullOrWhiteSpace(v) || !ushort.TryParse(v.Trim(), out var r) ? null : r;
            }
            var tile = U("Tile"); var pass = U("Pass"); var obj = U("Obj");
            if (tile is null && pass is null && obj is null) continue;   // a row that overrides nothing
            if (!d.TryGetValue(m, out var list)) d[m] = list = new();
            list.Add(new CellOverride(m, x, y, tile, pass, obj));
            n++;
            col.Keep();
        }
        return (d, n);
    }

    private static Dictionary<(ushort, ushort, ushort), Doors.DoorConfig> LoadDoors(CsvTable csv)
    {
        var d = new Dictionary<(ushort, ushort, ushort), Doors.DoorConfig>();
        foreach (var col in csv)
        {
            if (!ushort.TryParse(col.Require("Map"), out var m)) continue;
            ushort.TryParse(col.Require("X"), out var x);
            ushort.TryParse(col.Require("Y"), out var y);
            bool B(string k, bool def) { var v = col.Require(k); return string.IsNullOrEmpty(v) ? def : v.Trim() == "1"; }
            var key = col.Require("Key", "");
            // ClosedObj/OpenObj: ';'-separated object-id runs starting at this tile (same convention as
            // DoorObjects.csv). Both must be present and the same length to be usable — a half-configured
            // pair would give a door that opens and can never close, so drop both and log it.
            ushort[]? Run(string k)
            {
                var v = col.Require(k, "");
                if (string.IsNullOrWhiteSpace(v)) return null;
                var parts = v.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var outp = new List<ushort>();
                foreach (var p in parts) if (ushort.TryParse(p.Trim(), out var o)) outp.Add(o);
                return outp.Count > 0 ? outp.ToArray() : null;
            }
            var closed = Run("ClosedObj");
            var open = Run("OpenObj");
            if (closed is not null && open is not null && closed.Length != open.Length)
            {
                Log.Info($"   !! Doors.csv ({m},{x},{y}): ClosedObj has {closed.Length} id(s) but OpenObj has {open.Length} — ignoring both");
                closed = open = null;
            }
            // StartDx: where the run begins relative to THIS tile, same convention as DoorObjects.csv's own
            // startDx column. A two-tile door registers both of its halves, the right one with -1, so it
            // toggles as a unit whichever half the player happens to be facing.
            int.TryParse(col.Require("StartDx"), out var startDx);
            d[(m, x, y)] = new Doors.DoorConfig(
                Locked: B("Locked", false),
                Key: string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
                ConsumeKey: B("ConsumeKey", true),
                ForceOpen: B("ForceOpen", false),
                ClosedObjs: closed,
                OpenObjs: open,
                DefaultClosed: B("DefaultClosed", false),
                StartDx: startDx);
            col.Keep();
        }
        return d;
    }

    private static Dictionary<int, Dictionary<int, uint>> LoadLevelExp(CsvTable csv)
    {
        var table = new Dictionary<int, Dictionary<int, uint>>();
        foreach (var col in csv)
        {
            if (!int.TryParse(col.Require("Path"), out var p)) continue;
            if (!int.TryParse(col.Require("Level"), out var lvl)) continue;
            if (!uint.TryParse(col.Require("CumExp"), out var exp)) continue;
            if (!table.TryGetValue(p, out var byLevel)) table[p] = byLevel = new Dictionary<int, uint>();
            byLevel[lvl] = exp;
            col.Keep();
        }
        return table;
    }

    // ---- Phase-1 spell-DATA loaders (Content.cs literals -> CSV; see re/extract_spell_tables.py) ----------

    private static Dictionary<string, int> LoadSpellLevels(CsvTable csv)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("key", "").Trim();
            if (k.Length > 0 && int.TryParse(c.Require("level"), out var lvl)) { d[k] = lvl; c.Keep(); }
        }
        return d;
    }

    private static Dictionary<string, (string MobKey, int Level, int Mana, int CooldownMs)> LoadPets(CsvTable csv)
    {
        var d = new Dictionary<string, (string, int, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("key", "").Trim();
            if (k.Length == 0) continue;
            int.TryParse(c.Require("level", "0"), out var lvl);
            int.TryParse(c.Require("mana", "0"), out var mana);
            int.TryParse(c.Require("cooldownMs", "0"), out var cd);
            d[k] = (c.Require("mobKey", "").Trim(), lvl, mana, cd);
            c.Keep();
        }
        return d;
    }

    private static Dictionary<string, WeaponProc> LoadWeaponProcs(CsvTable csv)
    {
        var d = new Dictionary<string, WeaponProc>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var item = c.Require("item", "").Trim();
            var spell = c.Require("spell", "").Trim();
            if (item.Length == 0 || item.StartsWith('#') || spell.Length == 0) continue;
            if (!int.TryParse(c.Require("chancePct", "0"), out var pct) || pct <= 0) continue;
            var target = c.Require("target", "enemy").Trim();
            bool self = target.StartsWith("self", StringComparison.OrdinalIgnoreCase);
            bool needsFacing = !self || target.Equals("self_faced", StringComparison.OrdinalIgnoreCase);
            d[item] = new WeaponProc(item, pct, spell, self, needsFacing);
            c.Keep();
        }
        return d;
    }

    private static Dictionary<string, (TrapKind Kind, int Level, int Mana)> LoadTrapSpells(CsvTable csv)
    {
        var d = new Dictionary<string, (TrapKind, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("key", "").Trim();
            if (k.Length == 0 || !Enum.TryParse<TrapKind>(c.Require("kind", ""), true, out var kind)) continue;
            int.TryParse(c.Require("level", "0"), out var lvl);
            int.TryParse(c.Require("mana", "0"), out var mana);
            d[k] = (kind, lvl, mana);
            c.Keep();
        }
        return d;
    }

    // Morphs.csv holds BOTH fixed morphs (look/lookFemale set, answers empty) and question-dispatch morphs
    // (answers = "ans:look;ans:look", look/lookFemale empty) — split back into the two dicts here.
    private static (Dictionary<string, (ushort Look, ushort LookFemale, int Mana, int DurationMs)> Fixed,
                    Dictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> Dispatch)
        LoadMorphs(CsvTable csv)
    {
        var fx = new Dictionary<string, (ushort, ushort, int, int)>(StringComparer.OrdinalIgnoreCase);
        var dp = new Dictionary<string, (Dictionary<string, ushort>, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("key", "").Trim();
            if (k.Length == 0) continue;
            int.TryParse(c.Require("mana", "0"), out var mana);
            int.TryParse(c.Require("durationMs", "0"), out var dur);
            var answers = c.Require("answers", "").Trim();
            if (answers.Length > 0)
            {
                var ans = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in answers.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split(':', 2);
                    if (kv.Length == 2 && ushort.TryParse(kv[1].Trim(), out var look)) ans[kv[0].Trim()] = look;
                }
                dp[k] = (ans, mana, dur);
                c.Keep();
            }
            else
            {
                ushort.TryParse(c.Require("look", "0"), out var look);
                ushort.TryParse(c.Require("lookFemale", "0"), out var lookF);
                fx[k] = (look, lookF, mana, dur);
                c.Keep();
            }
        }
        return (fx, dp);
    }

    // SpellMods.csv: one row per spell, sparse — a `rage` value OR an `enchantAmt`+`enchantMana` pair.
    private static (Dictionary<string, int> Rage, Dictionary<string, (double Amt, int Mana)> Enchant) LoadSpellMods(CsvTable csv)
    {
        var rage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ench = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("key", "").Trim();
            if (k.Length == 0) continue;
            if (int.TryParse(c.Require("rage", ""), out var r)) { rage[k] = r; c.Keep(); }
            var ea = c.Require("enchantAmt", "").Trim();
            if (ea.Length > 0 && double.TryParse(ea, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amt))
            {
                int.TryParse(c.Require("enchantMana", "0"), out var em);
                ench[k] = (amt, em);
                c.Keep();
            }
        }
        return (rage, ench);
    }

    // Spells/skills. Rows that are section headers (name/ident begins with '=') or inactive (SplActive=0)
    // are skipped — they're book dividers in the RTK data, not castable. SplQuestion "NO" means "no prompt".
    private static List<SpellDef> LoadSpells(
        CsvTable csv,
        IReadOnlyDictionary<string, int> levelOverrides)
    {
        var spells = new List<SpellDef>();
        foreach (var col in csv)
        {
            if (!int.TryParse(col.Require("SplId"), out var id)) continue;
            if (col.Require("SplActive", "1") == "0") continue;
            var name  = Clean(col.Require("SplDescription", ""));
            var key   = Clean(col.Require("SplIdentifier", ""));
            if (string.IsNullOrEmpty(name) || name.StartsWith("=") || key.StartsWith("=")) continue;
            byte.TryParse(col.Require("SplType", "5"), out var type);
            int.TryParse(col.Require("SplPthId", "0"), out var pth);
            int.TryParse(col.Require("SplLevel", "0"), out var lvl);
            if (levelOverrides.TryGetValue(key, out var lvlOverride)) lvl = lvlOverride;
            if (!int.TryParse(col.Require("SplAlignment", "-1"), out var align)) align = -1;
            int.TryParse(col.Require("SplMark", "0"), out var mark);
            // Every mark row carries SplLevel 0 (the rank IS the requirement — there is no level past 99),
            // so without this floor a mark spell reads as "learnable at level 1" and SpellsForClass hands
            // Il san and Ee san secrets to any level-99 base character. See MarkSpellLevel.
            if (mark > 0) lvl = Math.Max(lvl, MarkSpellLevel);
            var q = Clean(col.Require("SplQuestion", ""));
            if (q.Equals("NO", StringComparison.OrdinalIgnoreCase)) q = "";
            bool canFail = col.Require("SplCanFail", "0") == "1";   // RTK magicdb_canfail — gates the deflect roll
            spells.Add(new SpellDef(id, key, name, type, pth, lvl, align, q, canFail, mark));
            col.Keep();
        }
        return spells;
    }

    /// <summary>The alignment-reskin family each spell belongs to: key → the KEY OF ITS BASE (alignment 0 or
    /// universal) sibling. Most abilities exist four times over — <c>spark_mage</c> (unaligned) alongside
    /// <c>glimpse_of_the_void_mage</c> (Kwisin), <c>bolt_mage</c> (Mingken) and <c>natures_ire_mage</c>
    /// (Ohaeng) — and the four are stored as a consecutive run of SplIds within one (SplPthId, SplType)
    /// block, alignments ascending. That adjacency is the only thing in the data that links them: they share
    /// no name, no key stem and no level column. Walking it here means <see cref="BuildSpellLadders"/> can be
    /// declared with ONE base key per tier instead of four, and stays correct for Kwisin/Mingken/Ohaeng
    /// characters for free.
    ///
    /// A new family starts at an alignment 0 row, at any universal (-1) row, at the first row after one, and
    /// wherever the alignment stops ascending. Validated by reconstructing <see cref="AreaZapMana"/>'s 20
    /// keys and <see cref="AreaHealSpells"/>'s 16 from their 5 and 4 base keys — both hand-curated from the
    /// RTK Lua years apart from this, and both come back exact.</summary>
    private static Dictionary<string, string> BuildAlignFamilies(IReadOnlyList<SpellDef> spells)
    {
        var leaderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in spells.OrderBy(s => s.Id).GroupBy(s => (s.PathId, s.Type)))
        {
            string leader = ""; int prev = int.MinValue;
            foreach (var s in block)
            {
                if (leader.Length == 0 || s.Alignment <= 0 || prev < 0 || s.Alignment < prev) leader = s.Key;
                leaderOf[s.Key] = leader;
                prev = s.Alignment;
            }
        }
        return leaderOf;
    }

    // Per-spell effect rows from re/extract_spell_formulas.py (spell_effects.csv). Keyed by identifier so it
    // joins to SpellDef.Key. A missing/short file just yields an empty map (every cast then uses the keyword
    // classifier). Numbers parse leniently — a blank cell is 0.
    // SpellText.csv: key -> (targetText apply-line, fadeText expiry-line), both live-canonical. Only spells with
    // a recorded line have a row; a spell may set just one of the two (e.g. Valor has a known fade but not apply).
    private static Dictionary<string, (string, string)> LoadSpellTexts(CsvTable csv)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in csv)
        {
            var k = c.Require("key", "").Trim();
            if (k.Length == 0) continue;
            var t = c.Require("targetText", "").Trim();
            var f = c.Require("fadeText", "").Trim();
            if (t.Length > 0 || f.Length > 0) { d[k] = (t, f); c.Keep(); }
        }
        return d;
    }

    private static Dictionary<string, SpellFx> LoadSpellFx(CsvTable csv)
    {
        var fx = new Dictionary<string, SpellFx>(StringComparer.OrdinalIgnoreCase);
        static int I(CsvRow c, string k)
            => int.TryParse(c.Require(k).Trim(), out var v) ? v : 0;
        foreach (var col in csv)
        {
            var key = col.Require("key", "").Trim();
            if (string.IsNullOrEmpty(key)) continue;
            fx[key] = new SpellFx(
                Key: key,
                Archetype: col.Require("archetype", "Utility").Trim(),
                Mana: I(col, "mana"),
                AmountExpr: col.Require("amountExpr", "").Trim(),
                BuffStat: col.Require("buffStat", "").Trim(),
                BuffAmt: col.Require("buffAmt", "").Trim(),
                DurationMs: I(col, "durationMs"),
                Debuff: col.Require("debuff", "").Trim(),
                Chance: col.Require("chance", "").Trim(),
                HealthCost: col.Require("healthCost", "").Trim(),
                Animation: I(col, "animation"),
                Sound: I(col, "sound"),
                Aether: I(col, "aether"),
                PcAlign: int.TryParse(col.Require("pcalign", "").Trim(), out var pa) ? pa : NoPcAlign,
                CureCat: col.Require("cureCat", "").Trim(),
                // The `class` column was read and thrown away until the zap cast-rate work needed it:
                // SplPthId can't stand in for it (Ion and Fissure are BOTH SplPthId 99, the shared path),
                // so this is the only per-spell class signal we have. See IsRateLimitedZap.
                Class: col.Require("class", "").Trim(),
                // Cast POSE override (0x1A action type). RTK's sendAction(N) for the cast; normally 6 (magic
                // pose) and left at the default, but an emote-range value (>=9) lets a spell cast with a body
                // emote instead — e.g. the Rogue/Warrior furies use 18 (the 'h' rage emote). See CastActionType.
                Action: I(col, "action"));
            col.Keep();
        }
        return fx;
    }

    // SpellLearnCosts.csv (generated by re/merge_spell_costs.py, see SpellCosts' own doc): one row per
    // (spell key, class pathId) -> level + up to 4 (item,amount) pairs + gold. Multiple rows can share a key
    // (one per class) for the handful of spells whose real level/cost differs by class.
    private static Dictionary<string, Dictionary<int, LearnCost>> LoadSpellCosts(CsvTable csv)
    {
        var costs = new Dictionary<string, Dictionary<int, LearnCost>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in csv)
        {
            var key = col.Require("key", "").Trim();
            if (key.Length == 0) continue;
            if (!int.TryParse(col.Require("pathId", ""), out var pathId)) continue;
            if (!int.TryParse(col.Require("level", ""), out var level)) continue;
            int.TryParse(col.Require("gold", "0"), out var gold);

            var items = new List<(string, int)>();
            for (int i = 1; i <= 4; i++)
            {
                var itemKey = col.Require($"item{i}", "").Trim();
                if (itemKey.Length == 0) continue;
                int.TryParse(col.Require($"amt{i}", "0"), out var amt);
                items.Add((itemKey, amt));
            }

            if (!costs.TryGetValue(key, out var perClass))
                costs[key] = perClass = new Dictionary<int, LearnCost>();
            perClass[pathId] = new LearnCost(level, gold, items.ToArray());
            col.Keep();
        }
        return costs;
    }

    // Undo the SQL-dump backslash escaping the RTK data carries (e.g. "JadeSpear\'s Home" -> "JadeSpear's Home").
    private static string Clean(string s) =>
        s.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
}
