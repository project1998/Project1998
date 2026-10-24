using Shared;

namespace Server;

public static partial class Content
{
    // A loader writes only to its thread-local builder. Runtime readers continue to see the last fully
    // published snapshot until the single Volatile.Write at the end of Load. Dependencies within Load are
    // explicit locals and parameters; the thread-local facade view remains only as a safety net whose test
    // counter proves the loader path does not use it.
    // Partial-file field initializer order is unspecified, so a facade reached by another partial's static
    // initializer must have a usable fallback even before this field receives its first published snapshot.
    private static ContentSnapshot? _snapshot;

    [ThreadStatic]
    private static ContentSnapshotBuilder? _snapshotBuilderState;

    [ThreadStatic]
    private static int _loadingThreadFacadeReads;

    // Every snapshot-backed facade already takes this branch. Keeping the counter here makes the proof cover
    // all current and future facades without adding work to a serving thread beyond its existing null check.
    // Every read of this property is a counted event on the loading thread; code that does not mean to count
    // must inspect the raw _snapshotBuilderState field instead.
    private static ContentSnapshotBuilder? _snapshotBuilder
    {
        get
        {
            var builder = _snapshotBuilderState;
            if (builder is not null) _loadingThreadFacadeReads++;
            return builder;
        }
    }

    private static ContentSnapshot Snapshot => Volatile.Read(ref _snapshot) ?? ContentSnapshot.Empty;

    private static ContentSnapshotBuilder Builder => _snapshotBuilderState
        ?? throw new InvalidOperationException("Programming error: content registries can only be assigned while Content.Load is building a snapshot.");

    private static ContentSnapshotBuilder BeginSnapshotBuild()
    {
        if (_snapshotBuilderState is not null)
            throw new InvalidOperationException("Programming error: Content.Load cannot be nested on the same thread.");
        _loadingThreadFacadeReads = 0;
        return _snapshotBuilderState = new ContentSnapshotBuilder();
    }

    private static void PublishSnapshot(ContentSnapshotBuilder builder)
    {
        LoadStepForTests?.Invoke("BeforePublish");
        var snapshot = builder.Build();

        // Keep every MoonSharp invocation outside the publication window. Once the registry snapshot is live,
        // install the matching verb hosts, then the remaining external content state. These commit seams only
        // replace prepared references: every operation that can reject content happened before BeforePublish.
        using (Session.EnterScriptGate())
        {
            Volatile.Write(ref _snapshot, snapshot);
            if (builder.SpellScript is { } spellScript) SpellScript.CommitReload(spellScript);
            if (builder.ItemScript is { } itemScript) ItemScript.CommitReload(itemScript);
            if (builder.NpcScript is { } npcScript) NpcScript.CommitReload(npcScript);
            if (builder.MobScript is { } mobScript) MobScript.CommitReload(mobScript);
            if (builder.Doors is { } doors) Doors.CommitConfig(doors);
            if (builder.EraCalendar is { } eraCalendar) Shared.EraCalendar.CommitReload(eraCalendar);
            if (builder.ObjectFlagOverrides is { } objectFlagOverrides)
                ObjectFlags.CommitOverrides(objectFlagOverrides);
            if (builder.TileTranslations is { } tileTranslations) TileTranslation.CommitReload(tileTranslations);
        }
    }

    private static void EndSnapshotBuild(ContentSnapshotBuilder builder)
    {
        if (ReferenceEquals(_snapshotBuilderState, builder)) _snapshotBuilderState = null;
    }

    internal static Action<string>? LoadStepForTests { get; set; }
    internal static int LoadingThreadFacadeReadsForTests => _loadingThreadFacadeReads;
    internal static object SnapshotIdentityForTests => Snapshot;
    internal static int SnapshotMemberCountForTests => typeof(ContentSnapshot)
        .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Length;

    private sealed class ContentSnapshotBuilder
    {
        // Prepared content owned by other facades. These are deliberately not ContentSnapshot members: their
        // serving APIs predate Content, but their commits belong to the same publication boundary.
        internal Shared.EraCalendar.PreparedReload? EraCalendar { get; set; }
        internal Dictionary<(ushort map, ushort x, ushort y), Doors.DoorConfig>? Doors { get; set; }
        internal ObjectFlags.PreparedOverrides? ObjectFlagOverrides { get; set; }
        internal TileTranslation.PreparedReload? TileTranslations { get; set; }
        internal LuaVerbHost.PreparedReload? SpellScript { get; set; }
        internal LuaVerbHost.PreparedReload? ItemScript { get; set; }
        internal NpcScript.PreparedReload? NpcScript { get; set; }
        internal MobScript.PreparedReload? MobScript { get; set; }

        internal IReadOnlyList<ItemDef> Items { get; set; } = new List<ItemDef>();
        internal IReadOnlyDictionary<int, ItemDef> ItemById { get; set; } = new Dictionary<int, ItemDef>();
        internal IReadOnlyDictionary<string, ItemDef> ItemByKey { get; set; } =
            new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<(ushort Look, byte Dye), byte> ArmorDyeRamps { get; set; } =
            new Dictionary<(ushort, byte), byte>();
        internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams { get; set; } =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, WeaponProc> WeaponProcs { get; set; } =
            new Dictionary<string, WeaponProc>(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyDictionary<ushort, MapInfo> Maps { get; set; } = new Dictionary<ushort, MapInfo>();
        internal IReadOnlyDictionary<(ushort m, ushort x, ushort y), (ushort m, ushort x, ushort y)> Warps { get; set; } =
            new Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)>();
        internal IReadOnlyDictionary<ushort, MapMetaInfo> MapMeta { get; set; } = new Dictionary<ushort, MapMetaInfo>();
        internal IReadOnlyList<MythicCaveDef> MythicCaves { get; set; } = new List<MythicCaveDef>();
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), MythicCaveDef> MythicCaveTiles { get; set; } =
            new Dictionary<(ushort, ushort, ushort), MythicCaveDef>();
        internal IReadOnlyList<EventCaveBand> EventCaveBands { get; set; } = new List<EventCaveBand>();
        internal IReadOnlyList<EventCaveDef> EventCaves { get; set; } = new List<EventCaveDef>();
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), EventCaveDef> EventCaveTiles { get; set; } =
            new Dictionary<(ushort, ushort, ushort), EventCaveDef>();
        internal IReadOnlyList<ArenaDoorDef> ArenaDoors { get; set; } = new List<ArenaDoorDef>();
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), ArenaDoorDef> ArenaDoorTiles { get; set; } =
            new Dictionary<(ushort, ushort, ushort), ArenaDoorDef>();
        internal IReadOnlyList<(ushort Map, ushort X, ushort Y, int BoardId)> BoardLocations { get; set; } =
            new List<(ushort, ushort, ushort, int)>();
        internal IReadOnlyDictionary<string, IReadOnlyList<InnDef>> Inns { get; set; } =
            new Dictionary<string, IReadOnlyList<InnDef>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyList<ForageAreaDef> ForageAreas { get; set; } = new List<ForageAreaDef>();
        internal IReadOnlyDictionary<ushort, PathHallDef> PathHalls { get; set; } = new Dictionary<ushort, PathHallDef>();
        internal IReadOnlyDictionary<int, GatewayDef> GatewayRegions { get; set; } = new Dictionary<int, GatewayDef>();
        internal IReadOnlyList<WorldDestDef> WorldDests { get; set; } = new List<WorldDestDef>();
        internal IReadOnlyDictionary<ushort, WorldTriggerDef> WorldMapTriggers { get; set; } =
            new Dictionary<ushort, WorldTriggerDef>();
        internal IReadOnlyDictionary<ushort, (ushort Map, ushort X, ushort Y)> FallRooms { get; set; } =
            new Dictionary<ushort, (ushort, ushort, ushort)>();
        internal IReadOnlyDictionary<int, (int StartDx, ushort[] Objs)> DoorSwaps { get; set; } =
            new Dictionary<int, (int, ushort[])>();
        internal IReadOnlyList<(int Lo, int Hi, int Delta)> DoorDeltas { get; set; } = new List<(int, int, int)>();
        internal IReadOnlyDictionary<int, ushort> DoorDefaultOpen { get; set; } = new Dictionary<int, ushort>();
        internal IReadOnlyDictionary<ushort, List<CellOverride>> MapCells { get; set; } =
            new Dictionary<ushort, List<CellOverride>>();
        internal int MapCellCount { get; set; }

        internal IReadOnlyList<MobDef> Mobs { get; set; } = new List<MobDef>();
        internal IReadOnlyList<SpawnDef> Spawns { get; set; } = new List<SpawnDef>();
        internal IReadOnlyList<AreaSpawnDef> AreaSpawns { get; set; } = new List<AreaSpawnDef>();
        internal IReadOnlyList<NpcDef> Npcs { get; set; } = new List<NpcDef>();
        internal IReadOnlyDictionary<int, NpcDef> NpcById { get; set; } = new Dictionary<int, NpcDef>();
        internal IReadOnlyDictionary<int, MobDef> MobById { get; set; } = new Dictionary<int, MobDef>();
        internal IReadOnlyDictionary<string, MobDef> MobByKey { get; set; } =
            new Dictionary<string, MobDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, string[]> ShopStock { get; set; } =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, string[]> ShopBuysFrom { get; set; } =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<(ushort Look, byte Colour), byte> Mob5xPalettes { get; set; } =
            new Dictionary<(ushort, byte), byte>();
        internal IReadOnlyDictionary<string, MobDropDef> MobDrops { get; set; } =
            new Dictionary<string, MobDropDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, HarvestNodeDef> HarvestNodes { get; set; } =
            new Dictionary<string, HarvestNodeDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, MobSpellDef[]> MobSpells { get; set; } =
            new Dictionary<string, MobSpellDef[]>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, MobChatterDef> MobChatter { get; set; } =
            new Dictionary<string, MobChatterDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, MobSpawnRuleDef> MobSpawnRules { get; set; } =
            new Dictionary<string, MobSpawnRuleDef>(StringComparer.OrdinalIgnoreCase);
        internal bool MobHpJitter { get; set; }
        internal IReadOnlyDictionary<string, MobBossDef> MobBosses { get; set; } =
            new Dictionary<string, MobBossDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, IReadOnlyList<int[]>> AmbushBursts { get; set; } =
            new Dictionary<string, IReadOnlyList<int[]>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<ushort, AmbushMapDef> Ambushes { get; set; } = new Dictionary<ushort, AmbushMapDef>();
        internal IReadOnlyDictionary<string, IReadOnlyList<(string Name, string[] Keys)>> ShopCatalogues { get; set; } =
            new Dictionary<string, IReadOnlyList<(string, string[])>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, NpcAbilityRef[]> NpcCompositions { get; set; } =
            new Dictionary<string, NpcAbilityRef[]>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, bool> MobFleeOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, bool> MobStationaryOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyList<SpellDef> Spells { get; set; } = new List<SpellDef>();
        internal IReadOnlyDictionary<int, string> Paths { get; set; } = new Dictionary<int, string>();
        internal IReadOnlyDictionary<string, SpellFx> SpellFx { get; set; } =
            new Dictionary<string, SpellFx>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, (string Target, string Fade)> SpellTexts { get; set; } =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<int, SpellDef> SpellById { get; set; } = new Dictionary<int, SpellDef>();
        internal IReadOnlyDictionary<string, SpellDef> SpellByKey { get; set; } =
            new Dictionary<string, SpellDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, int> PathIdByName { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SpellParams { get; set; } =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, Dictionary<int, LearnCost>> SpellCosts { get; set; } =
            new Dictionary<string, Dictionary<int, LearnCost>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<int, Dictionary<string, (string Ladder, int Rung)>> LadderOf { get; set; } =
            new Dictionary<int, Dictionary<string, (string, int)>>();
        internal Dictionary<int, string[]> PathRanks { get; set; } = new();
        internal IReadOnlyDictionary<string, (int PathId, int Mark)> PathRankByName { get; set; } =
            new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<int, int> PathIcon { get; set; } = new();
        internal Dictionary<int, int> PathChat { get; set; } = new();
        internal Dictionary<int, int> PathBase { get; set; } = new();
        internal IReadOnlyDictionary<string, int> RageAmount { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, (double Amt, int Mana)> EnchantSpells { get; set; } =
            new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, (TrapKind Kind, int Level, int Mana)> TrapSpells { get; set; } =
            new Dictionary<string, (TrapKind, int, int)>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, (ushort Look, ushort LookFemale, int Mana, int DurationMs)> MorphSpells { get; set; } =
            new Dictionary<string, (ushort, ushort, int, int)>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> MorphDispatchSpells { get; set; } =
            new Dictionary<string, (Dictionary<string, ushort>, int, int)>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, (string MobKey, int Level, int Mana, int CooldownMs)> PetSpells { get; set; } =
            new Dictionary<string, (string, int, int, int)>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, int> SpellLevelOverrides { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyList<MinorQuestDef> MinorQuests { get; set; } = new List<MinorQuestDef>();
        internal IReadOnlyList<MythicAllianceDef> MythicAlliances { get; set; } = new List<MythicAllianceDef>();
        internal IReadOnlyDictionary<(ushort From, ushort To), WarpQuestLock> WarpQuestLocks { get; set; } =
            new Dictionary<(ushort, ushort), WarpQuestLock>();
        internal IReadOnlyDictionary<(int Path, string Tier), (int Level, string Karma)> ArmorQuestGates { get; set; } =
            new Dictionary<(int, string), (int, string)>();

        internal IReadOnlyList<MusicTrack> MusicTracks { get; set; } = new List<MusicTrack>();
        internal IReadOnlyList<BgmZone> BgmZones { get; set; } = new List<BgmZone>();
        internal Dictionary<ushort, BgmPick> BgmByMap { get; set; } = new();
        internal (ushort bgm, byte type)? DefaultBgm { get; set; }
        internal (ushort bgm, byte type)? DefaultBgmNew { get; set; }

        internal IReadOnlyDictionary<string, bool> CraftingToggleOverrides { get; set; } =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> PathGrowth { get; set; } =
            new Dictionary<int, (int, int, int, int)>();
        internal IReadOnlyDictionary<string, double> Tuning { get; set; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<int, Dictionary<int, uint>> LevelExp { get; set; } = new();
        internal IReadOnlyList<string> RejectedScripts { get; set; } = Array.Empty<string>();
        internal ContentLoadReport LoadReport { get; set; } = ContentLoadReport.Empty;

        internal ContentSnapshot Build() => new(this);
    }

    private sealed class ContentSnapshot
    {
        internal static ContentSnapshot Empty { get; } = new(new ContentSnapshotBuilder());

        internal ContentSnapshot(ContentSnapshotBuilder builder)
        {
            Items = builder.Items;
            ItemById = builder.ItemById;
            ItemByKey = builder.ItemByKey;
            ArmorDyeRamps = builder.ArmorDyeRamps;
            ItemParams = builder.ItemParams;
            WeaponProcs = builder.WeaponProcs;
            Maps = builder.Maps;
            Warps = builder.Warps;
            MapMeta = builder.MapMeta;
            MythicCaves = builder.MythicCaves;
            MythicCaveTiles = builder.MythicCaveTiles;
            EventCaveBands = builder.EventCaveBands;
            EventCaves = builder.EventCaves;
            EventCaveTiles = builder.EventCaveTiles;
            ArenaDoors = builder.ArenaDoors;
            ArenaDoorTiles = builder.ArenaDoorTiles;
            BoardLocations = builder.BoardLocations;
            Inns = builder.Inns;
            ForageAreas = builder.ForageAreas;
            PathHalls = builder.PathHalls;
            GatewayRegions = builder.GatewayRegions;
            WorldDests = builder.WorldDests;
            WorldMapTriggers = builder.WorldMapTriggers;
            FallRooms = builder.FallRooms;
            DoorSwaps = builder.DoorSwaps;
            DoorDeltas = builder.DoorDeltas;
            DoorDefaultOpen = builder.DoorDefaultOpen;
            MapCells = builder.MapCells;
            MapCellCount = builder.MapCellCount;
            Mobs = builder.Mobs;
            Spawns = builder.Spawns;
            AreaSpawns = builder.AreaSpawns;
            Npcs = builder.Npcs;
            NpcById = builder.NpcById;
            MobById = builder.MobById;
            MobByKey = builder.MobByKey;
            ShopStock = builder.ShopStock;
            ShopBuysFrom = builder.ShopBuysFrom;
            Mob5xPalettes = builder.Mob5xPalettes;
            MobDrops = builder.MobDrops;
            HarvestNodes = builder.HarvestNodes;
            MobSpells = builder.MobSpells;
            MobChatter = builder.MobChatter;
            MobSpawnRules = builder.MobSpawnRules;
            MobHpJitter = builder.MobHpJitter;
            MobBosses = builder.MobBosses;
            AmbushBursts = builder.AmbushBursts;
            Ambushes = builder.Ambushes;
            ShopCatalogues = builder.ShopCatalogues;
            NpcCompositions = builder.NpcCompositions;
            MobFleeOverrides = builder.MobFleeOverrides;
            MobStationaryOverrides = builder.MobStationaryOverrides;
            Spells = builder.Spells;
            Paths = builder.Paths;
            SpellFx = builder.SpellFx;
            SpellTexts = builder.SpellTexts;
            SpellById = builder.SpellById;
            SpellByKey = builder.SpellByKey;
            PathIdByName = builder.PathIdByName;
            SpellParams = builder.SpellParams;
            SpellCosts = builder.SpellCosts;
            LadderOf = builder.LadderOf;
            PathRanks = builder.PathRanks;
            PathRankByName = builder.PathRankByName;
            PathIcon = builder.PathIcon;
            PathChat = builder.PathChat;
            PathBase = builder.PathBase;
            RageAmount = builder.RageAmount;
            EnchantSpells = builder.EnchantSpells;
            TrapSpells = builder.TrapSpells;
            MorphSpells = builder.MorphSpells;
            MorphDispatchSpells = builder.MorphDispatchSpells;
            PetSpells = builder.PetSpells;
            SpellLevelOverrides = builder.SpellLevelOverrides;
            MinorQuests = builder.MinorQuests;
            MythicAlliances = builder.MythicAlliances;
            WarpQuestLocks = builder.WarpQuestLocks;
            ArmorQuestGates = builder.ArmorQuestGates;
            MusicTracks = builder.MusicTracks;
            BgmZones = builder.BgmZones;
            BgmByMap = builder.BgmByMap;
            DefaultBgm = builder.DefaultBgm;
            DefaultBgmNew = builder.DefaultBgmNew;
            CraftingToggleOverrides = builder.CraftingToggleOverrides;
            PathGrowth = builder.PathGrowth;
            Tuning = builder.Tuning;
            LevelExp = builder.LevelExp;
            RejectedScripts = builder.RejectedScripts;
            LoadReport = builder.LoadReport;
        }

        internal IReadOnlyList<ItemDef> Items { get; }
        internal IReadOnlyDictionary<int, ItemDef> ItemById { get; }
        internal IReadOnlyDictionary<string, ItemDef> ItemByKey { get; }
        internal IReadOnlyDictionary<(ushort Look, byte Dye), byte> ArmorDyeRamps { get; }
        internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams { get; }
        internal IReadOnlyDictionary<string, WeaponProc> WeaponProcs { get; }
        internal IReadOnlyDictionary<ushort, MapInfo> Maps { get; }
        internal IReadOnlyDictionary<(ushort m, ushort x, ushort y), (ushort m, ushort x, ushort y)> Warps { get; }
        internal IReadOnlyDictionary<ushort, MapMetaInfo> MapMeta { get; }
        internal IReadOnlyList<MythicCaveDef> MythicCaves { get; }
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), MythicCaveDef> MythicCaveTiles { get; }
        internal IReadOnlyList<EventCaveBand> EventCaveBands { get; }
        internal IReadOnlyList<EventCaveDef> EventCaves { get; }
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), EventCaveDef> EventCaveTiles { get; }
        internal IReadOnlyList<ArenaDoorDef> ArenaDoors { get; }
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), ArenaDoorDef> ArenaDoorTiles { get; }
        internal IReadOnlyList<(ushort Map, ushort X, ushort Y, int BoardId)> BoardLocations { get; }
        internal IReadOnlyDictionary<string, IReadOnlyList<InnDef>> Inns { get; }
        internal IReadOnlyList<ForageAreaDef> ForageAreas { get; }
        internal IReadOnlyDictionary<ushort, PathHallDef> PathHalls { get; }
        internal IReadOnlyDictionary<int, GatewayDef> GatewayRegions { get; }
        internal IReadOnlyList<WorldDestDef> WorldDests { get; }
        internal IReadOnlyDictionary<ushort, WorldTriggerDef> WorldMapTriggers { get; }
        internal IReadOnlyDictionary<ushort, (ushort Map, ushort X, ushort Y)> FallRooms { get; }
        internal IReadOnlyDictionary<int, (int StartDx, ushort[] Objs)> DoorSwaps { get; }
        internal IReadOnlyList<(int Lo, int Hi, int Delta)> DoorDeltas { get; }
        internal IReadOnlyDictionary<int, ushort> DoorDefaultOpen { get; }
        internal IReadOnlyDictionary<ushort, List<CellOverride>> MapCells { get; }
        internal int MapCellCount { get; }
        internal IReadOnlyList<MobDef> Mobs { get; }
        internal IReadOnlyList<SpawnDef> Spawns { get; }
        internal IReadOnlyList<AreaSpawnDef> AreaSpawns { get; }
        internal IReadOnlyList<NpcDef> Npcs { get; }
        internal IReadOnlyDictionary<int, NpcDef> NpcById { get; }
        internal IReadOnlyDictionary<int, MobDef> MobById { get; }
        internal IReadOnlyDictionary<string, MobDef> MobByKey { get; }
        internal IReadOnlyDictionary<string, string[]> ShopStock { get; }
        internal IReadOnlyDictionary<string, string[]> ShopBuysFrom { get; }
        internal IReadOnlyDictionary<(ushort Look, byte Colour), byte> Mob5xPalettes { get; }
        internal IReadOnlyDictionary<string, MobDropDef> MobDrops { get; }
        internal IReadOnlyDictionary<string, HarvestNodeDef> HarvestNodes { get; }
        internal IReadOnlyDictionary<string, MobSpellDef[]> MobSpells { get; }
        internal IReadOnlyDictionary<string, MobChatterDef> MobChatter { get; }
        internal IReadOnlyDictionary<string, MobSpawnRuleDef> MobSpawnRules { get; }
        internal bool MobHpJitter { get; }
        internal IReadOnlyDictionary<string, MobBossDef> MobBosses { get; }
        internal IReadOnlyDictionary<string, IReadOnlyList<int[]>> AmbushBursts { get; }
        internal IReadOnlyDictionary<ushort, AmbushMapDef> Ambushes { get; }
        internal IReadOnlyDictionary<string, IReadOnlyList<(string Name, string[] Keys)>> ShopCatalogues { get; }
        internal IReadOnlyDictionary<string, NpcAbilityRef[]> NpcCompositions { get; }
        internal Dictionary<string, bool> MobFleeOverrides { get; }
        internal Dictionary<string, bool> MobStationaryOverrides { get; }
        internal IReadOnlyList<SpellDef> Spells { get; }
        internal IReadOnlyDictionary<int, string> Paths { get; }
        internal IReadOnlyDictionary<string, SpellFx> SpellFx { get; }
        internal IReadOnlyDictionary<string, (string Target, string Fade)> SpellTexts { get; }
        internal IReadOnlyDictionary<int, SpellDef> SpellById { get; }
        internal IReadOnlyDictionary<string, SpellDef> SpellByKey { get; }
        internal IReadOnlyDictionary<string, int> PathIdByName { get; }
        internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SpellParams { get; }
        internal IReadOnlyDictionary<string, Dictionary<int, LearnCost>> SpellCosts { get; }
        internal IReadOnlyDictionary<int, Dictionary<string, (string Ladder, int Rung)>> LadderOf { get; }
        internal Dictionary<int, string[]> PathRanks { get; }
        internal IReadOnlyDictionary<string, (int PathId, int Mark)> PathRankByName { get; }
        internal Dictionary<int, int> PathIcon { get; }
        internal Dictionary<int, int> PathChat { get; }
        internal Dictionary<int, int> PathBase { get; }
        internal IReadOnlyDictionary<string, int> RageAmount { get; }
        internal IReadOnlyDictionary<string, (double Amt, int Mana)> EnchantSpells { get; }
        internal IReadOnlyDictionary<string, (TrapKind Kind, int Level, int Mana)> TrapSpells { get; }
        internal IReadOnlyDictionary<string, (ushort Look, ushort LookFemale, int Mana, int DurationMs)> MorphSpells { get; }
        internal IReadOnlyDictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> MorphDispatchSpells { get; }
        internal IReadOnlyDictionary<string, (string MobKey, int Level, int Mana, int CooldownMs)> PetSpells { get; }
        internal IReadOnlyDictionary<string, int> SpellLevelOverrides { get; }
        internal IReadOnlyList<MinorQuestDef> MinorQuests { get; }
        internal IReadOnlyList<MythicAllianceDef> MythicAlliances { get; }
        internal IReadOnlyDictionary<(ushort From, ushort To), WarpQuestLock> WarpQuestLocks { get; }
        internal IReadOnlyDictionary<(int Path, string Tier), (int Level, string Karma)> ArmorQuestGates { get; }
        internal IReadOnlyList<MusicTrack> MusicTracks { get; }
        internal IReadOnlyList<BgmZone> BgmZones { get; }
        internal Dictionary<ushort, BgmPick> BgmByMap { get; }
        internal (ushort bgm, byte type)? DefaultBgm { get; }
        internal (ushort bgm, byte type)? DefaultBgmNew { get; }
        internal IReadOnlyDictionary<string, bool> CraftingToggleOverrides { get; }
        internal IReadOnlyDictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> PathGrowth { get; }
        internal IReadOnlyDictionary<string, double> Tuning { get; }
        internal Dictionary<int, Dictionary<int, uint>> LevelExp { get; }
        internal IReadOnlyList<string> RejectedScripts { get; }
        internal ContentLoadReport LoadReport { get; }
    }
}
