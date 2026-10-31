namespace Shared;

/// <summary>
/// Minimal fabricated character for world-entry bring-up. Real characters will come from
/// Persistence later; for now this is a hardcoded spawn so we can drive the entry sequence.
/// </summary>
public sealed class Character
{
    /// <summary>The JSON blob schema this build writes. Older blobs are upgraded before deserialization.</summary>
    public const int CurrentSchemaVersion = 1;

    // Zero means a pre-versioning blob. New characters must be stamped explicitly at their creation
    // boundary; otherwise the field initializer would make a schema-0 blob look upgraded even when the
    // raw-JSON upgrader had never run.
    public int SchemaVersion = 0;
    public uint   Id     = 1;
    public string Name   = "snuggle";

    // location — defaults to just inside Ironheart's Home in Kugnae (map 36), the home-city coordinates a
    // brand new character actually starts at (see Session.PlaceNewCharacter/HomeCityFor — the door-arrival
    // tile from RTK's Warps.csv, map 0 (87/88,146) -> map 36 (5/6,10)). This compiled-in default only
    // matters if a Character is ever built without going through PlaceNewCharacter; both real entry points
    // (account creation, and world-entry with no saved character) call it explicitly, and a Buya-nation
    // character is placed at Jadespear (map 351) instead.
    public ushort Map    = 36;
    public ushort X      = 5;
    public ushort Y      = 10;
    public ushort MapXs  = 12;
    public ushort MapYs  = 12;

    // Which way the character is looking (0=N 1=E 2=S 3=W — the same convention as Mob.Dir and the 0x11
    // "side" packet). Persisted alongside X/Y for the same reason: the entry sequence draws us with a
    // facing (SendSelfLook/PlayerSnapshot), and a session-only value meant every login snapped everyone
    // back to north regardless of where they'd walked in from. Session._facing IS this field.
    public byte Dir      = 0;

    // appearance
    public ushort Sex    = 1;
    public ushort Face   = 0;
    public ushort Hair   = 0;   // hair STYLE (creation blob[4]); no render slot on 4.95, appearance[?] on later clients

    // Hair COLOUR — the 5.33 appearance[3] palette byte (4.95 has no hair-colour slot, so this is inert there).
    // 0 = the base/undyed colour. Set by the rogue-hall shaman's hair-dye service (AppearanceAbility, adapted
    // from RTK salon.lua / general_npc_funcs.hairdye). Persisted so a dye survives relog and re-broadcast to
    // peers via PlayerSnapshot, exactly like the armor-dye ArmorColor byte below.
    public byte HairColor = 0;

    // War-paint dye applied to the worn armor/coat — renders as the 0x33 type-0 appearance[4] palette byte
    // (0 = undyed base color). Set by the Arena Master (WarPaintAbility, RTK arena_master.lua's "War paint").
    // Persisted so the dye survives a relog, and re-broadcast to peers via PlayerSnapshot so they see it too.
    public byte   ArmorColor = 0;

    // Mounted on a horse — renders as the 0x33 type-0 appearance[1] form byte = 3 (the client swaps the
    // human sprite for the horse+rider composite, SPR ids 344/345). Persisted, as every public field here is
    // (CharacterStore serializes fields), so a rider who logs out mounted logs back in mounted.
    public bool   Mounted = false;

    // The creature the rider sits on (its mobs.csv key), which the dismount sets down again: RTK keeps the ridden
    // creature's id in the saved registry (registry["mountMobId"], rtklua/Accepted/Scripts/on_event.lua), so a stag
    // ridden across a relog comes back a stag (the project owner, 2026-10-31, "As RTK"). Written by the ride key,
    // cleared wherever Mounted is cleared (the dismount, death, the GM toggle). Empty is a horse: the GM toggle's
    // mount, and an older row that has no such field.
    public string MountKey = "";

    // Wielded weapon — renders as the 0x33 type-0 appearance[5] slot (look-lab: 0=unarmed, then
    // Honor sword / Flame blade / Electra / Steelthorn / Blood / Primogen …). Also drives the melee
    // damage bonus in Session.HandleAttack. Persisted so the weapon survives a relog.
    public byte   Weapon = 0;

    // vitals / stats
    public byte Nation   = 1;   // kingdom id -> HUD crest (NATION_E.EPF). See NationName for the table.
    public byte Totem    = 4;   // 4 = none

    /// <summary>Nation id -> name, confirmed empirically on the 4.95 HUD (see Session `@nat`). The
    /// names live in a client data file (no strings in the exe), so this table is the source of truth.</summary>
    public static readonly string[] Nations =
        { "Neutral", "Koguryo", "Buya", "Nagnang", "Shilla", "Jinhan", "Paekjae", "Kaya" };
    public static string NationName(byte id) => id < Nations.Length ? Nations[id] : $"nation#{id}";
    // MaxHp/MaxMp compiled-in fallback only matters if a Character is ever built without going through
    // CharacterFactory.PlaceNewCharacter (see there): real RTK's Player.reset rolls baseHealth =
    // random(45,55) and baseMagic = random(32,36) per new character, not a fixed 100/50 — PlaceNewCharacter
    // does that same roll for genuinely new characters. These constants are just the midpoint of that range.
    public byte Level    = 1;
    public uint MaxHp    = 50;
    public uint MaxMp    = 34;
    public uint Hp       = 50;
    public uint Mp       = 34;
    public byte Might    = 3;
    public byte Will     = 3;
    public byte Grace    = 3;
    public uint Exp      = 0;
    public uint Coins    = 0;
    public byte Armor    = 0;
    public byte MaxInv   = 27;

    // Karma (RTK player.karma). FRACTIONAL on purpose — RTK hands out quarter- and tenth-points as well as
    // whole ones (reveal_fate 0.25, MinorQuest 0.1, fishing -0.001), and the named ladder in Server/Karma.cs
    // reads bands rather than integers, so rounding here would quietly collapse those. Starts at 0 = "Cat".
    // Persists with the rest of the character JSON blob; no schema change needed.
    public double Karma  = 0;

    // Bag + worn gear. Inventory slots are 0-based (0..MaxInv-1); Equipment entries carry the EQ index
    // (Item.EquipSlot's source) in their Slot field. Both persist in the character JSON so a relog keeps
    // the bag. Resolved against Content.Items (Server) by id — Shared holds only the per-character state.
    public List<InvItem> Inventory = new();
    public List<InvItem> Equipment = new();

    // Bank (vault) storage, filled at an inn/bank NPC. BankMoney is coin held on account; BankItems is the
    // stored stacks (their Slot is meaningless in the vault — a fresh bag slot is assigned on withdrawal).
    // Both persist in the character JSON, so the vault survives a relog.
    public uint          BankMoney = 0;
    public List<InvItem> BankItems = new();

    // Learned spells/skills, in spellbook order. Each entry is a Content.Spells id; the book slot the client
    // uses (the 0x17 "pos" and the 0x0F cast "pos") is the index into this list. Persisted so the book
    // survives a relog; re-sent on world entry. Filled by SyncSpellbook, which the @lvl / @class / @mark /
    // @align rebuild drives — there is no per-spell grant command.
    public List<int> Spells = new();

    // Quest progress. Key = a quest id ("trial_of_iron"); value = its stage (0 = not started, 1 = active,
    // 2 = done — quests define their own meaning). Objective counters live under composite keys
    // ("trial_of_iron.kills"), so one flat map holds both stage machine and progress tallies. Persisted in
    // the character JSON, so an accepted quest and its progress survive a relog. Read and written through
    // Server/QuestState.cs, which names each key by quest and slot and maps it to the key saved here
    // (Tests/QuestStateTests.cs fails on any other access, here or to QuestStrings below).
    public Dictionary<string, int> Quests = new();

    // String-valued quest registry (RTK's registryString): e.g. the active minor-quest key. Kept separate
    // from the int map above so a quest can hold both a numeric stage and a string selection. Persisted.
    public Dictionary<string, string> QuestStrings = new();

    // Lifetime kills per mob key ("squirrel" -> 12), tallied on every world-mob kill. Quests read a delta of
    // this (RTK's player:killCount) — kill-since-accept — so a fresh kill after accepting counts. Persisted.
    public Dictionary<string, int> Kills = new();

    // The KILL TRACK: the last 8 kind of creature killed, most-recent-first, each with how many of it have
    // been killed since it (re-)entered the list. This is NOT a second lifetime tally — it is the array the
    // real Nexus kept and the mythic alliances read, and its whole point is that entries FALL OFF: kill a
    // ninth kind of creature and the least-recently-killed one is pushed out, taking its count with it.
    // Period primary is the Alliance Tips page (Moraghul, via Nexus Atlas and the poet-tutor board): "The
    // Kill Track saves ONLY the last 8 types of creatures you have killed... the database will record up to
    // 255 kills of a single creature." Every documented alliance quirk falls out of those two sentences —
    // the six free creature types, the four-simultaneous-lesser-alliances ceiling, and the trick of killing
    // one more of each boss to push a mistake back off the end. See Server/MythicAlliance.cs.
    public List<KillTrackEntry> KillTrack = new();

    // Sub-alignment: 0 = Unaligned (base), 1 = Kwisin, 2 = Mingken, 3 = Ohaeng. Gates which spell set @spells
    // teaches — a character learns only universal spells + their own alignment's set, never the other
    // sub-alignments' parallel spells. Set with @align.
    public byte Alignment = 0;

    /// <summary>Sub-alignment id -> name (index = the Alignment value). Kwisin/Mingken/Ohaeng are NexusTK's
    /// three sub-paths; 0 is the base "unaligned" set.</summary>
    public static readonly string[] Alignments = { "Unaligned", "Kwisin", "Mingken", "Ohaeng" };
    public static string AlignmentName(byte id) => id < Alignments.Length ? Alignments[id] : $"align#{id}";

    // profile / "Mind's Eye" self-profile (0x39 = clif_mystaytus). These populate the profile window
    // the client opens on the profile key (client sends 0x2D, byte 0). AC is signed in TK (lower is
    // better); Dam/Hit are the melee bonus lines. Tnl = experience "to next level".
    // Ac is the NAKED base AC, held as a cache of the real NexusTK rule "base AC (naked) = 100 - level"
    // (Warrior Tutor Yttribium, confirmed live). Default 99 = 100 - 1 for a fresh level-1 character. It is
    // recomputed from level on world entry and on every level-up (Session.cs), never decremented in place,
    // and reaches 1 at level 99 for every class. Gear/buffs modify it at display/combat time, where the
    // -80 (human) / -95 (mob) damage-mitigation caps apply.
    public sbyte  Ac        = 99;
    public byte   Dam       = 0;
    public byte   Hit       = 0;
    public uint   Tnl       = 0;
    public string Title     = "";          // grade/honorific shown above the name (e.g. "Peasant")
    public string ClassName = "Peasant";   // path/class BASE name; what a player SEES is base + rank (Session.ClassTitle)
    // Subpath RANK within the path (RTK ChaMark / status.mark): 0 = base class, 1 Il san … 5 Oh san. The
    // field holds all six for the sake of the data that references them, but a character may only REACH
    // Content.MaxMark (3, Sam san) — Spells.csv has no mark-4/5 rows, so the higher ranks have no content
    // behind them. Nothing in the world ADVANCES this yet — the subpath-promotion NPCs aren't ported — so it
    // stays 0 for ordinary play and is set by the "@mark" command. It is a real gate all the same: map entry
    // (MapReqMark), unmarked-only doors, minor-quest eligibility and mark-restricted gear (ItmMark) read it.
    // The rank NAMES are per path (Paths.csv PthMark1..5): a Warrior's rank 1 is "Il san (W)", a Ju jak's is
    // "Force" — and the rank axis is shared with SplMark, so a rank's title and its spells line up.
    public byte   Mark      = 0;
    public string ClanName  = "";          // guild/clan name ("" = clanless)
    public string ClanTitle = "";          // rank within the clan
    public string Spouse    = "";           // marriage line ("" = none)

    // Marriage in progress (RTK Spells/common/propose.lua + NPCs/Common/chapel_npc.lua). Fiance holds the
    // OTHER party's name while engaged ("" = not engaged); Spouse above takes over once actually married.
    // IsProposee marks which side ACCEPTED the proposal — RTK gates "only the proposee may start the
    // ceremony" on registry["partner2"]==self.ID, which this mirrors without needing a partner1/partner2
    // pair. MarriageTimer is the unix-seconds "not yet" gate on starting the ceremony (RTK's 3-day cool-down
    // after getting engaged); RingCooldown is the separate 24h gate on buying ANOTHER engagement ring.
    public string Fiance        = "";
    public bool   IsProposee    = false;
    public long   MarriageTimer = 0;
    public long   RingCooldown  = 0;

    // Ignore list (RTK map.h sd_ignorelist / clif.c ignorelist_add/remove/clif_isignore): case-insensitive
    // names. clif_isignore blocks a whisper if EITHER side has the other listed — see Session.DoWhisper.
    // RTK never exposes an in-game "friend list" (no such struct/feature exists in the C engine at all);
    // Friends is our own addition, purely a saved name list with an online/offline check on login — no RTK
    // source to port from, so there's no wire packet or server behavior beyond that to replicate.
    public List<string> IgnoreList = new();
    public List<string> Friends    = new();

    // Profile status toggles shown as the group/exchange indicator cells (self 0x39 + other-view 0x34).
    // Grouped = "sociable" (Shift+G, 0x1b/0x02); Exchange = trade allowed (0x1b/0x08). Persisted so they
    // survive reopening the profile and a relog. Group defaults off, exchange on.
    public bool   Grouped   = false;
    public bool   Exchange  = true;

    /// <summary>The rest of the Options-menu toggles, as RTK's <c>settingFlags</c> bitfield
    /// (common/mmo.h <c>enum settingFLAGS</c>). Every one of them is a <c>0x1b</c> sub-command, and the bit
    /// is always <c>1 &lt;&lt; (subCmd - 1)</c> — see <see cref="SettingBit"/>. Group/exchange/realm/fast-move
    /// also live in this space but are kept in their own fields above (they feed the profile packets and the
    /// movement model), so their bits here are unused.
    ///
    /// Defaults match a fresh RTK character: the four listen/believe channels, weather, sound and both
    /// show-gear toggles ON. The client only ever tells us a toggle FLIPPED, never its state, so these
    /// defaults are also our only sync point — same deal as fast-move.</summary>
    public int SettingFlags = SettingBit(1) | SettingBit(3) | SettingBit(4) | SettingBit(5)    // whisper/shout/advice/magic
                            | SettingBit(6) | SettingBit(9)                                     // weather/fast-move
                            | SettingBit(13) | SettingBit(14) | SettingBit(15);                 // sound/helm/necklace

    /// <summary>Setting bit for a <c>0x1b</c> sub-command (RTK: FLAG_WHISPER=1 for sub 1, FLAG_GROUP=2 for
    /// sub 2, … FLAG_NECKLACE=16384 for sub 15).</summary>
    public static int SettingBit(int subCmd) => 1 << (subCmd - 1);

    public bool HasSetting(int subCmd) => (SettingFlags & SettingBit(subCmd)) != 0;

    /// <summary>Flip a setting and return its new state.</summary>
    public bool ToggleSetting(int subCmd) { SettingFlags ^= SettingBit(subCmd); return HasSetting(subCmd); }

    /// <summary>Clan whisper is a plain on/off in RTK (status.clan_chat), not part of the flag word.</summary>
    public bool   ClanChat  = false;

    /// <summary>Which background soundtrack this character hears: false = the 12 stock midis (both clients
    /// ship them), true = the 5.x mp3 playlists out of the 5.33 client's Mus000.dat. Set by "@music new" /
    /// "@music old" and honoured only on a 5.33 session — the 4.95 client has the mp3 engine but none of the
    /// files, so a 4.95 player left on `true` (by playing on both) simply keeps hearing the midis rather than
    /// going silent. Not a 0x1b Options bit: the client's own menu has no such checkbox.</summary>
    public bool   NewMusic = false;

    // Subpath chat (F2 toggle, RTK clif_handle_clickgetinfo's 0xFFFFFFFE sentinel + clif_sendsubpathmessage):
    // a server-wide chat channel reaching every OTHER online player who shares this character's ClassName and
    // also has it on. Off by default, like RTK. See Session.ToggleSubpathChat / DoSubpathChat.
    public bool   SubpathChat = false;

    // The writable "profile" page shown when someone clicks you (0x34): a free-text blurb the player
    // writes about their character, plus an optional drawn portrait bitmap. ProfilePic is the raw
    // bitmap bytes WITHOUT the size prefix (null/empty = no picture). Edited via the client's
    // change-profile packet (0x2F) and persisted.
    public string ProfileText = "This character has not written a profile yet.";
    public byte[]? ProfilePic = null;

    // The live in-game date, as RTK's Scripts/scripts.lua curT() writes it ("Yuri <year>, <season>") — used
    // by every legend that stamps "the current date", and by the Lua `gameDate` binding. Reads the world
    // calendar, so it is a real date now rather than the fixed "Hyul 31, Winter" constant this used to be
    // (there was no server-side clock when the legends were written). A live 4.95 self-profile capture says
    // "Hyul" where RTK says "Yuri" — same king, two names; we use RTK's word server-wide so every date the
    // server writes agrees. Characters created before 2026-08-12 keep whatever their born legend already says.
    public static string GameDate => GameCalendar.Stamp;

    // Legend marks: the scrollable list at the bottom of the profile. Each = icon + color + text.
    // Seeded with a "born" entry (mirrors the real 6.x capture) so the window has visible content. The date
    // is stamped HERE, at construction — that is the moment the character is born, and it must not re-read
    // the clock later or everyone's birthday would slide forward with the world.
    public List<Legend> Legends = new()
    {
        // color 0x80 matches the real 6.x capture (`01 00 80 17 "Born…"`); color 0 renders invisible.
        new Legend(icon: 0, color: 0x80, text: $"Born in {GameDate}"),
    };

    // Raw body of the creation packet (0x04), kept verbatim until its appearance-byte layout is
    // decoded from the logged dump and mapped onto the fields above. Null for the default spawn.
    public byte[]? CreationBlob = null;

    // Timed effects that were still running at the last save — see TimedEffects. Snapshot-only: the live
    // state is the session's own fields, and Session.CaptureTimedEffects refreshes this right before every
    // write, so the field is meaningless (and ignored) except at load time.
    public TimedEffects Effects = new();
}

/// <summary>
/// The character's running timed effects, persisted so they survive a relog AND a server crash. Every
/// deadline is stored as ABSOLUTE unix milliseconds, not as a remaining duration: a buff is a wall-clock
/// timer, so 40 seconds of Might left is still 40 seconds after a 10-second reconnect and is GONE after an
/// hour offline. Storing "remaining" instead would let a player bank a fury by logging out, and would hand
/// back free duration after every crash restart.
///
/// Mirrors the session's live fields (Server/Session.Spells.cs, Session.Items.cs), which stay on
/// Environment.TickCount64 — a monotonic clock that can't be dragged around by an NTP step mid-session.
/// Session.CaptureTimedEffects / RestoreTimedEffects convert between the two.
/// </summary>
public sealed class TimedEffects
{
    /// <summary>Stat buffs, and curse/venom statuses (the ones carrying a mutually-exclusive Category).</summary>
    public List<SavedBuff> Buffs = new();

    /// <summary>Ward-style presence markers with no stat delta (item_verbs "ward"/"hardenbody"):
    /// key -> absolute unix-ms expiry.</summary>
    public Dictionary<string, long> StatusFlags = new();

    // Rage / fury tier (player.rage): a whole-swing multiplier. CrRageTier is Chung Ryong's Rage's climb
    // level (0 = not that fury): it must ride along with RageUntil because the tier is what prices the
    // wear-out drain, and dropping it on relog turned logging out into a way to keep an 81x fury for free.
    public long   RageUntil;   public int    RageAmount = 1;  public string RageName = "";  public int CrRageTier;
    // Damage-reduction "deduction" — two independent, non-stacking sources (Sanctuary line, Baekho's Cunning).
    public long   SancUntil;   public double SancMult   = 1.0; public string SancName = "";
    public long   CunningUntil; public double CunningMult = 1.0;
    // Combat stances (Backstab / Flank / Baekho's Cunning 4's four-way swing).
    public long   BackstabUntil; public long FlankUntil; public long FourWayUntil;
    // Stealth (Invisible / Spirit's Form / Life's Cloak / Glass Form) — timer + which spell armed it.
    public long   StealthUntil; public string StealthName = "";
    // Morph/disguise: the Monster.tbl look peers see instead of the human sprite.
    public long   MorphUntil;  public ushort MorphLook;  public byte MorphColor; public string MorphKey = "";
}

/// <summary>One persisted entry of the session's live buff list. <see cref="Until"/> is absolute unix ms.</summary>
public sealed class SavedBuff
{
    public string Stat     = "";
    public int    Amount;
    public long   Until;
    public string Key      = "";
    public string Name     = "";
    public string Category = "";
}

/// <summary>
/// One stack of a held item: which slot it sits in, the item-db id, how many, and current durability.
/// The definition (name/icon/stats) is looked up from the item registry by <see cref="ItemId"/>; only
/// the mutable per-character bits live here. CustomName overrides the display name when non-empty
/// (renamed/quest items). Used for both the bag (Slot = inventory index) and worn gear (Slot = EQ index).
/// </summary>
public sealed class InvItem
{
    public byte   Slot       = 0;
    public int    ItemId     = 0;
    public int    Amount     = 1;
    public ushort Dura       = 0;
    public string CustomName = "";

    // Durability warning-threshold stage (RTK sd->status.equip[x].repair): 0 = full/no warning sent yet,
    // 1..5 = the 50/25/10/5/1% warnings already sent, so each only fires once as Dura counts down. Only
    // meaningful for worn equipment (Session.CheckDura); bag items stay at 0.
    public byte Repair = 0;

    // Remaining "save this item from breaking" charges (RTK sd->status.inventory[x].protected): each one is
    // consumed to restore the item to full durability instead of destroying it. Nothing grants a charge yet —
    // the item-level ItmProtected flag is likewise 0 for every row in the registry — so this stays 0 and the
    // protect branch (Session.TryProtectFromBreak) never fires; it is here so the break paths match RTK.
    public byte Protected = 0;

    /// <summary>Bound owner. Empty = unbound (the normal case). Some items bind to the character who
    /// obtained them — NPC-sold subpath weapons, and items produced by a quest upgrade (a Spike becoming an
    /// Enchanted Spike) — after which the examine tooltip shows "Owner: &lt;name&gt;". This is per-STACK
    /// state, not per-item-definition, which is why it lives here and not on ItemDef: the same registry row
    /// can exist bound in one player's bag and unbound in another's. Nothing sets it automatically yet —
    /// the grant sites for those two cases still need wiring, see @bind.</summary>
    public string Owner = "";

    public InvItem() { }
    public InvItem(byte slot, int itemId, int amount, ushort dura = 0)
    { Slot = slot; ItemId = itemId; Amount = amount; Dura = dura; }

    /// <summary>An independent copy. Used to snapshot the bag before a transfer that might have to be rolled
    /// back (see Session.SnapshotBag) — a shallow list copy wouldn't do, because the failure cases mutate
    /// Amount on an EXISTING stack rather than only adding and removing whole entries.</summary>
    public InvItem Clone() => new()
    {
        Slot = Slot, ItemId = ItemId, Amount = Amount, Dura = Dura,
        CustomName = CustomName, Repair = Repair, Protected = Protected, Owner = Owner,
    };
}

/// <summary>One legend-mark line in the profile window: an icon id, a text color, and the text. <see cref="Name"/>
/// is an internal key (RTK's legend name, never sent to the client) so a quest can find/replace/remove its own
/// legend by identity rather than matching on display text.</summary>
public sealed class Legend
{
    public byte   Icon  = 0;
    public byte   Color = 0;
    public string Text  = "";
    public string Name  = "";

    public Legend() { }
    public Legend(byte icon, byte color, string text, string name = "") { Icon = icon; Color = color; Text = text; Name = name; }
}

/// <summary>One row of the <see cref="Character.KillTrack"/>: a creature kind and how many of it have been
/// killed since this row entered the list. <see cref="Count"/> saturates at 255 — the real track's per-type
/// ceiling ("the database will record up to 255 kills of a single creature"), and the reason no alliance ever
/// asks for more than a handful.</summary>
public sealed class KillTrackEntry
{
    public string Mob   = "";
    public int    Count = 0;

    public KillTrackEntry() { }
    public KillTrackEntry(string mob, int count) { Mob = mob; Count = count; }
}

/// <summary>The kill track's two rules, kept here rather than in the server so they can be exercised on
/// their own. Everything the mythic alliances document about "don't kill anything else" is these two
/// methods: an entry moves to the FRONT when its kind is killed, and the ninth kind pushes the oldest one
/// (and its count) off the end.</summary>
public static class KillTrack
{
    /// <summary>How many kinds of creature are remembered. "The Kill Track saves ONLY the last 8 types of
    /// creatures you have killed" (Alliance Tips).</summary>
    public const int Slots = 8;
    /// <summary>Per-kind ceiling: "the database will record up to 255 kills of a single creature".</summary>
    public const int Cap = 255;

    /// <summary>Record one kill. The kind moves to the front whether or not it was already listed, its count
    /// rises (saturating at <see cref="Cap"/>), and anything past <see cref="Slots"/> falls off for good.</summary>
    public static void Push(List<KillTrackEntry> track, string mob)
    {
        if (track is null || string.IsNullOrEmpty(mob)) return;

        int at = track.FindIndex(e => string.Equals(e.Mob, mob, StringComparison.OrdinalIgnoreCase));
        KillTrackEntry entry;
        if (at >= 0) { entry = track[at]; track.RemoveAt(at); }
        else entry = new KillTrackEntry(mob, 0);

        if (entry.Count < Cap) entry.Count++;
        track.Insert(0, entry);
        if (track.Count > Slots) track.RemoveRange(Slots, track.Count - Slots);
    }

    /// <summary>Kills of a kind still on the track. 0 covers both "never killed" and "killed, then pushed
    /// off" — the game draws no distinction between them either.</summary>
    public static int Count(IReadOnlyList<KillTrackEntry> track, string mob)
    {
        if (track is null || string.IsNullOrEmpty(mob)) return 0;
        foreach (var e in track)
            if (string.Equals(e.Mob, mob, StringComparison.OrdinalIgnoreCase)) return e.Count;
        return 0;
    }
}
