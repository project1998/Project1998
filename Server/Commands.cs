using Shared;

namespace Server;

/// <summary>
/// The chat-command surface: the prefix, the name -> handler table, and the dispatcher. Everything about
/// "what is a command" lives here; <see cref="Session.HandleChat"/> just asks this to try the message first.
///
/// WHY A TABLE. This used to be ~70 <c>text.StartsWith(...)</c> lines in HandleChat, each embedding the
/// prefix as a literal (as did every usage string and comment that named a command — some 500 mentions in
/// all, which is what made changing the prefix a research project). Worse, ORDER was load-bearing: the
/// plural item lister had to be tested before the singular item granter, the mail-flag probe before the
/// mailbox, the spellbook before the one-letter status probe — otherwise the shorter name swallowed the
/// longer one, and adding a command whose name was a prefix of another silently broke that other one.
/// Exact-name lookup removes that whole class of bug, and <see cref="Prefix"/> makes the prefix itself a
/// one-character change.
///
/// CONTRACT: a handler receives the ARGUMENT TAIL — the message with the prefix, the command name and the
/// separating space already removed, then trimmed. "@item Fine Sword 3" reaches GiveItemCmd as
/// "Fine Sword 3". Handlers must not re-parse the command name out of it.
/// </summary>
public sealed partial class Session
{
    /// <summary>The one definition of the chat-command prefix. Everything user-visible builds its text from
    /// this, so it can be changed here alone.</summary>
    internal const char Prefix = '@';

    /// <param name="Names">Canonical name first, then aliases. Lower-case; matching is case-insensitive.</param>
    /// <param name="Min">The lowest <see cref="AccessLevel"/> that may run it, compared against
    /// <see cref="Access"/>. Someone below it gets the same answer as for a name that doesn't exist, so the
    /// tooling stays invisible rather than merely locked.</param>
    /// <param name="Run">Receives the argument tail (see the class doc).</param>
    /// <param name="Args">Argument shape for @help, e.g. "&lt;name|id&gt; [amount]"; "" for none.</param>
    private sealed record Command(string[] Names, AccessLevel Min, Action<Session, CommandArgs> Run, string Args, string Help);

    /// <summary>
    /// A command's ARGUMENT TAIL, plus the table row it belongs to.
    ///
    /// <para>WHY. Every handler used to parse its own tail, in three house styles that do not agree: across
    /// Session.GmCommands.cs and Session.Media.cs there were 22 <c>ParseInts</c> calls, 26 raw
    /// <c>.Split</c>s and 39 bare <c>TryParse</c>s. ParseInts SKIPPED non-numeric words, so "@stats 1 2 x"
    /// reached the three-argument form as two integers, while a positional reader sees three words and
    /// refuses. (It is gone now — this type replaced its last caller.) Worse, 33 of those handlers also wrote their own "usage:" line, restating the row's
    /// <c>Args</c> column from memory — so the table said one thing and the refusal another the moment
    /// either drifted, which @hit's row ("[dmg]", against a handler wanting a percent and a crit byte) had
    /// already been doing.</para>
    ///
    /// <para>This is positional and literal: <see cref="Word"/> 0 is the first word of the tail, and a word
    /// that isn't a number simply isn't one. <see cref="Usage"/> renders from the row, so a command's
    /// argument shape is written down exactly once — in the table — and @help and every refusal read the
    /// same copy.</para>
    ///
    /// <para>It converts implicitly to the raw tail so the table's rows did not all have to change at once:
    /// a handler that has not been converted yet still takes a <c>string</c> and still gets the same string
    /// it always got. That conversion is the migration seam, not an invitation — a new handler should take
    /// the CommandArgs.</para>
    /// </summary>
    private readonly struct CommandArgs
    {
        private readonly Command _row;
        private readonly string[] _words;

        internal CommandArgs(Command row, string raw)
        {
            _row = row;
            Raw = raw;
            _words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>The tail exactly as typed, trimmed at the ends (see <see cref="SplitCommand"/>). Interior
        /// spacing is preserved here and nowhere else — <see cref="Rest(int)"/> normalizes it.</summary>
        public string Raw { get; }

        /// <summary>How many words were given.</summary>
        public int Count => _words.Length;

        /// <summary>No arguments at all — the "bare @command" case, which is a readout for most commands.
        /// </summary>
        public bool None => _words.Length == 0;

        /// <summary>Word <paramref name="i"/>, or "" past the end. Never null, so a caller can compare
        /// without a length check first.</summary>
        public string Word(int i) => i >= 0 && i < _words.Length ? _words[i] : "";

        /// <summary>Word <paramref name="i"/> is <paramref name="literal"/>, case-insensitively — the
        /// keyword sub-forms ("@clock real", "@take … all", "@showwarps look").</summary>
        public bool Is(int i, string literal) => Word(i).Equals(literal, StringComparison.OrdinalIgnoreCase);

        /// <summary>Word <paramref name="i"/> as an integer. False if it isn't there or isn't one.</summary>
        public bool Int(int i, out int value) => int.TryParse(Word(i), out value);

        /// <summary>Word <paramref name="i"/> as an integer, or <paramref name="fallback"/> — the shape most
        /// of the sprite and packet probes want, where every argument has a sensible default.</summary>
        public int Int(int i, int fallback) => Int(i, out var v) ? v : fallback;

        /// <summary>Word <paramref name="i"/> as a HEX byte, the form every opcode argument takes ("@s 08",
        /// "@pkt send 1a"). Decimal is deliberately not accepted: an opcode is written in hex everywhere
        /// else in this codebase and in every capture, so reading "10" as sixteen is the useful answer.
        /// </summary>
        public bool Hex(int i, out byte value)
            => byte.TryParse(Word(i), System.Globalization.NumberStyles.HexNumber, null, out value);

        /// <summary>A "[0|1]" argument: 0 or 1 sets the flag, anything else (including nothing) FLIPS
        /// <paramref name="current"/>. Every session toggle takes this shape — @clip, @peace, @anywarp,
        /// @showwarps, @ride, @dog — and each used to spell it out itself.</summary>
        public bool Toggle(int i, bool current) => Int(i, out var v) ? v != 0 : !current;

        /// <summary>Word <paramref name="i"/> and everything after it, rejoined with single spaces — free
        /// text that runs to the end of the line (a reason, a legend body, a quest value).</summary>
        public string Rest(int i) => Rest(i, _words.Length);

        /// <summary>Words <paramref name="from"/> up to but not including <paramref name="to"/>, rejoined
        /// with single spaces. The bounded form is for a tail that ends in a keyword rather than at the end
        /// of the line ("@take Rice Cake all").</summary>
        public string Rest(int from, int to)
        {
            from = Math.Max(from, 0);
            to = Math.Min(to, _words.Length);
            return from < to ? string.Join(' ', _words[from..to]) : "";
        }

        /// <summary>Words <paramref name="from"/> onward as an array, for the one place that hands a whole
        /// token run to a different parser — @pkt's packet-token reader, which also reads token runs out of
        /// a file and so cannot take a CommandArgs.</summary>
        public string[] Words(int from)
            => from >= 0 && from < _words.Length ? _words[from..] : Array.Empty<string>();

        /// <summary>The "&lt;name with spaces&gt; &lt;n&gt;" shape that @item, @take and @dura all take, and
        /// each used to re-implement: an item name is words, so the COUNT has to be recognised as a trailing
        /// integer rather than as part of the name.
        ///
        /// <para>True when there was a trailing integer to take, leaving <paramref name="name"/> as the
        /// words before it. False leaves <paramref name="name"/> as the whole tail verbatim — "@item Fine
        /// Sword" is an item called "Fine Sword", not an error.</para></summary>
        public bool NameThenTrailingInt(out string name, out int n)
        {
            if (_words.Length >= 2 && int.TryParse(_words[^1], out n))
            {
                name = string.Join(' ', _words[..^1]);
                return true;
            }
            name = Raw;
            n = 0;
            return false;
        }

        /// <summary>"usage: @name/@alias &lt;args&gt;" — the command's argument shape, rendered from its
        /// table row. The ONLY place a usage line comes from: there is no longer a hand-written one anywhere,
        /// so the shape @help prints and the shape a refusal prints cannot disagree.
        ///
        /// <para>Deliberately just the shape. What an argument MEANS lives in the row's help text, which
        /// "@help &lt;name&gt;" prints in full — repeating it in every refusal is what made these lines long
        /// enough to be worth rewriting from memory in the first place.</para></summary>
        public string Usage() => "usage: " + HelpShape(_row);

        /// <summary>The tail as a plain string, for the handlers that have not been converted yet.</summary>
        public static implicit operator string(CommandArgs a) => a.Raw;
    }

    private static Command P(string names, Action<Session, CommandArgs> run, string args, string help)
        => new(names.Split('|'), AccessLevel.Player, run, args, help);
    private static Command T(string names, Action<Session, CommandArgs> run, string args, string help)
        => new(names.Split('|'), AccessLevel.Tester, run, args, help);
    private static Command G(string names, Action<Session, CommandArgs> run, string args, string help)
        => new(names.Split('|'), AccessLevel.Gm, run, args, help);

    private static readonly Command[] CommandTable =
    {
        // ---- help / discovery -----------------------------------------------------------------------
        // The compact companion to @help: @help pages the DESCRIBED list (a screenful at a time), this shows
        // every name you can run at once. Player-tier on purpose — a plain player sees only their own handful.
        P("commands|cmds", (s, a) => s.ShowCommandIndex(), "", "compact index of every command you can use (names only; @help <word> for details)"),

        // ---- world / navigation ---------------------------------------------------------------------
        // A command earns its place here only when the 4.95 client has no native way to reach the feature:
        // whisper (0x19), mail (the 0x3B board window), party (0x2E) and trade (0x4A) all do, so their
        // chat-command fallbacks (and the old @friend/@ignore/@travel list-management fallbacks) were removed
        // rather than kept as a second, non-authentic UI. @music is the one player-tier survivor and is listed
        // down in the media block, with the rest of the 0x19 tooling.
        T("warp",    (s, a) => s.Warp(a),          "<map name|id> [x y]", "teleport"),
        T("go",      (s, a) => s.GoCmd(a),         "<x> <y>",             "jump to a tile on the map you're already on (bad/out-of-range coords -> 0 0)"),
        T("rez",     (s, a) => s.RezCmd(a),        "[username]",          "revive a player (or yourself) to full HP/MP (a full heal if already alive)"),
        T("approach",(s, a) => s.ApproachCmd(a),   "<username>",          "teleport to an online player"),
        G("where",   (s, a) => s.WhereCmd(a),      "[username]",          "where a player is, without going there (bare = everyone online, with locations)"),
        G("bring",   (s, a) => s.BringCmd(a),      "<username>",          "pull an online player to your side (the inverse of @approach)"),
        T("die",     (s, a) => s.DieCmd(),         "",                    "kill yourself (ghost form + real death penalties; @rez to get back up)"),
        T("clip",    (s, a) => s.ClipCmd(a),       "[0|1]",               "no-clip: walk through walls, mobs and players (this session only; warps still work)"),
        T("peace",   (s, a) => s.PeaceCmd(a),      "[0|1]",               "unprovoked mobs don't notice you (this session only; anything you attack still fights back)"),
        T("anywarp", (s, a) => s.AnyWarpCmd(a),    "[0|1]",               "use any warp despite level/mark/path/quest requirements (this session only; echoes the denial it waived)"),
        T("showwarps", (s, a) => s.ShowWarpsCmd(a), "[0|1] | look [warpFrame] [doorFrame]", "mark every warp + scripted doorway on the map (you only; follows across maps; lists destinations)"),
        // The readout for the four toggles above plus every other staff override on the session (the melee
        // sfx slots, the world-wide mob swing pose, the @wmpos dots) and the two world pins @clock/@weather.
        // See Server/GmOverrides.cs, which is the class it enumerates.
        G("toggles", (s, a) => s.TogglesCmd(),     "",                    "every staff override you are under, in one readout"),
        T("maps",    (s, a) => s.ListMaps(a),      "[filter]",            "list/fuzzy-search maps"),
        G("mobs",    (s, a) => s.ListMobs(a),      "[filter]",            "list/fuzzy-search the mob registry"),
        G("summon",  (s, a) => s.Summon(a),        "<mob name|id>",       "spawn a registry mob in front of you"),
        G("reload",  (s, a) => s.ReloadContent(),  "",                    "hot-reload file-backed content"),
        G("restart", (s, a) => s.RestartCmd(a),    "[minutes 0-1440] [reason] | cancel|off", "schedule a server restart, warning everyone as it nears (minutes may be fractional; bare @restart reports)"),
        G("announce",(s, a) => s.AnnounceCmd(a),   "<message>",           "system line to every player online (the restart-countdown channel)"),
        G("rabbit",  (s, a) => s.SpawnRabbit(),    "",                    "one wandering, killable rabbit"),
        G("kill",    (s, a) => s.KillMobs(),       "",                    "despawn every creature except NPCs on this map"),

        // ---- character ------------------------------------------------------------------------------
        // These REBUILD the character (see Session.RespecTo): stats and the spellbook always come out exactly
        // right for the resulting class/level/mark/alignment. That's why there is no longer a "@spells" —
        // there is nothing left for it to do.
        T("lvl",     (s, a) => s.RespecLevel(a.Int(0, s._char.Level)),
                                                   "[1-99]",              "rebuild as level n: accurate stats + the matching spellbook (bare @lvl rebuilds at the level you are)"),
        // @lvl's complement, not its rival: @lvl REBUILDS at a level, @exp earns one — it's the only way to
        // exercise the real leveling path (the curve, multi-level carries, the Peasant wall, LevelUp gains),
        // and with `kill` the only way to exercise its GROUP half without a mob to kill (#155).
        T("exp",     (s, a) => s.ExpCmd(a),        "<n> [kill]",          "gain experience through the real leveling path (kill = as a kill would: split across your group in range, group totem rule; no quest credit)"),
        T("mark",    (s, a) => s.SetMark(a),       "<0-3>",               "subpath rank at level 99 (Il san…Sam san): its spells; a rank adds no stats"),
        T("class",   (s, a) => s.SetClass(a),      "<Warrior | Rogue | Mage | Poet | Peasant>", "set the class/path and rebuild for it"),
        T("dog",     (s, a) => s.SetDogFlag(a),    "[0|1]",               "the Dog-quest flag: unlocks Dog spells for a base class or NPC subpath"),
        // The Sage ladder has no other staff route: its five spells are locked to one NPC, so no rebuild
        // grants them, and buying it honestly is 500,000 gold across 360 real days of upgrade waits.
        T("sage",    (s, a) => s.SetSageRung(a),   "[0-5]",               "Share Wisdom rung: sets the spell AND clears the 90-day upgrade wait (bare @sage reports)"),
        // The generic quest-state pair (docs/common/Quest-Registry.md is the key catalogue). Most chains gate
        // on the LEGEND, not the stage, so re-testing one usually takes both commands.
        T("quest",   (s, a) => s.QuestCmd(a),      "[key] [stage]",       "read/set the raw quest registry (bare = dump your keys; stage 0 clears; non-numeric sets the string registry)"),
        T("questreset", (s, a) => s.QuestResetCmd(a), "",           "clear EVERY quest key and keyed legend mark, so all chains replay from the start (kills untouched)"),
        T("legend",  (s, a) => s.LegendCmd(a),     "[key] [0 | <icon> <color> <text...>]", "list legend marks with their internal keys; remove one, or (re)create one by key (colour 128 is the usual white; 0 renders invisible)"),
        // 0x0A's `type` decides which pane/colour a line lands in, and a wrong one is INVISIBLE from the
        // server side — the packet sends, the log says so, the client draws nothing. See TextChannelCmd.
        // A probe filed under character for want of a better home: handler in Server/Session.Probes.cs.
        T("text",    (s, a) => s.TextChannelCmd(a), "[0-255] [message]",   "send yourself one 0x0A line on a channel; bare @text sweeps them to compare panes/colours"),
        T("align",   (s, a) => s.SetAlignment(a),  "<Unaligned | Kwisin | Mingken | Ohaeng | 0-3>", "set sub-alignment and rebuild the book"),
        T("stats",   (s, a) => s.SetStatsCmd(a),   "<vita> <mana> <all> | <vita> <mana> <might> <grace> <will>",
                                                                          "set vitals and stats directly, overriding the curve — e.g. @stats 50000 50000 130"),
        T("might",   (s, a) => s.SetBaseStat("might", a), "<n>",          "set base might"),
        T("will",    (s, a) => s.SetBaseStat("will", a),  "<n>",          "set base will"),
        T("grace",   (s, a) => s.SetBaseStat("grace", a), "<n>",          "set base grace"),
        T("hp",      (s, a) => s.SetMaxPool(hp: true, a), "<n>",          "set max HP (vita) and refill"),
        T("mp",      (s, a) => s.SetMaxPool(hp: false, a),"<n>",          "set max MP (mana) and refill"),
        T("nation",  (s, a) => s.SetNationCmd(a),   "<0-7>",              "set your nation crest (persists)"),
        T("totem",   (s, a) => s.SetTotemCmd(a),    "<0-3>",              "set your totem crest — 0 JuJak, 1 Baekho, 2 HyunMoo, 3 ChungRyong (persists)"),
        T("karma",   (s, a) => s.SetKarmaCmd(a),    "<value|tier>",       "set karma outright: a number, or a tier (cat, dog, angel, …)"),
        T("dispel",  (s, a) => s.DispelCmd(),       "",                   "strip every buff and debuff on you"),
        T("killtrack", (s, a) => s.KillTrackCmd(a), "[clear]",         "the 8-slot kill track the mythic alliances count (clear = what accepting one does)"),
        T("coins|gold", (s, a) => s.GiveCoinsCmd(a), "[n]",               "add coins to the purse (bare = +10,000; a negative n removes, floored at 0)"),
        T("ride|mount", (s, a) => s.ToggleMount(a), "[0|1]",              "get on/off the horse"),

        // ---- PC subpaths ----------------------------------------------------------------------------
        // PLAYER-tier, because two of its users are ordinary players: an elder admitting a member, and a member
        // leaving. The GM-only parts (naming any subpath, appointing elders) are checked inside, in
        // Server/Session.Subpaths.cs, which also says why membership is a grant rather than an NPC.
        P("subpath", (s, a) => s.SubpathCmd(a),    "admit <name> [subpath] | leave | expel <name> | elder <name> [0|1]",
                                                   "PC subpaths (bare = your standing): a GM or one of the subpath's elders admits a level-50 character or expels a member; leave your own; a GM appoints an elder (0 removes one)"),

        // ---- items ----------------------------------------------------------------------------------
        T("items",    (s, a) => s.ListItems(a),     "[filter]",           "list/fuzzy-search the item registry"),
        T("item",     (s, a) => s.GiveItemCmd(a),   "<name|id> [amount]", "summon an item into the bag (browse the registry with @items)"),
        T("take",     (s, a) => s.TakeItemCmd(a),   "<name|id> [amount|all]", "remove an item from the bag (worn gear untouched; browse with @items)"),
        T("dura",     (s, a) => s.DuraCmd(a),       "<name|id> <n>",      "set an item's durability, bag first then worn (repair/breakage testing)"),
        T("clearinv", (s, a) => s.ClearInventory(), "",                   "empty the bag and gear"),
        // A probe: handler in Server/Session.Probes.cs, with @delreason, which shares its raw-0x0F helper.
        G("icons",    (s, a) => s.IconSweep(a),     "[start]",            "fill the bag with client Item.epf frames"),

        // ---- spells ---------------------------------------------------------------------------------
        // @lvl / @class / @mark / @align each resync the WHOLE book to what the character is entitled to, and
        // that stays the main path. @spell is the one additive exception — a single named ability, off-class
        // and off-level, for testing one thing. The bulk grants (@spells, @forgetspells) are still gone: they
        // could only ever grow the book, so a character who had been three classes carried all three.
        T("spell",   (s, a) => s.TeachSpellCmd(a),  "<name|id>",           "learn one ability outright, any class or level (a rebuild forgets it)"),

        // ---- moderation -----------------------------------------------------------------------------
        // GM-only, all of them. See Session.Moderation.cs: no duration means PERMANENT, and anything applied
        // to an online player takes effect immediately rather than at their next login.
        //
        // NOT here yet: GM invisibility (@hide), the observe-unseen half of moderation. Deliberately deferred
        // while everyone online is staff — there is no one to observe. Wanted once real players arrive.
        // Feasibility note for then: entity visibility is entirely server-side (keep the hidden GM out of the
        // entity broadcasts), but that touches every broadcast/PeerAt path, so it's a change, not a table row.
        G("ban",    (s, a) => s.BanCmd(a),    "<name> [minutes] [reason]", "ban an account (no duration = permanent); kicks them if online"),
        G("unban",  (s, a) => s.UnbanCmd(a),  "<name>",                    "lift an account ban"),
        G("mute",   (s, a) => s.MuteCmd(a),   "<name> [minutes] [reason]", "silence speech/whisper/subpath chat; '@' commands still work"),
        G("unmute", (s, a) => s.UnmuteCmd(a), "<name>",                    "lift a mute"),
        G("kick",   (s, a) => s.KickCmd(a),   "<name> [reason]",           "disconnect an online player (saves them first)"),
        G("banip",  (s, a) => s.BanIpCmd(a),  "<ip> [minutes] [reason] | remove <ip>", "ban a source address"),
        G("bans",   (s, a) => s.BansCmd(a),   "",                          "list everyone currently banned or muted"),
        G("modlog", (s, a) => s.ModLogCmd(a), "[n]",                       "recent moderation actions, newest first"),

        // ---- events ---------------------------------------------------------------------------------
        // Carnage is a HOSTED event, so its result is something a GM records rather than something the
        // server derives. Warrior Sun armor's first step reads the tally. See ArmorQuest.CarnageWinsReg.
        G("carnage", (s, a) => s.CarnageWinCmd(a), "<name> [n] | <name> = <n>", "record carnage victories (n<0 removes)"),

        // ---- config read-outs -----------------------------------------------------------------------
        // @npc is tester-tier since gaining its finder half: the warp-to-NPC is @warp-grade tooling, and the
        // bare readout it kept is harmless config info.
        T("npc",   (s, a) => s.NpcCmd(a),         "[name|id]", "bare: which NPCs are switched off; with a name: find that NPC and jump beside it"),
        G("craft", (s, a) => s.CraftToggleCmd(a), "", "crafting era-gate status (config + @reload to change)"),
        G("era",   (s, a) => s.EraCmd(a),         "", "target date + which dated content it includes"),

        // ---- sprite / appearance lab ----------------------------------------------------------------
        // Every handler in this block is an RE probe and lives in Server/Session.Probes.cs, except @mob
        // and @spawn, which spawn real shared monsters and stayed with the entity code.
        G("look",   (s, a) => s.LookOne(a),          "b0..b6",          "spawn a dummy with those appearance bytes"),
        G("row",    (s, a) => s.LookRow(a),          "<i> <lo> <hi>",   "sweep appearance byte i"),
        G("cre",    (s, a) => s.CreatureOne(a),      "[look] [hp] [color]", "spawn one real monster (0x07)"),
        G("crow",   (s, a) => s.CreatureRow(a),      "<lo> <hi> [step]", "sweep monster look ids across a row"),
        G("crecol", (s, a) => s.CreatureColorRow(a), "<look> [lo] [hi] [step]", "sweep the 0x07 colour byte"),
        G("mob",    (s, a) => s.MobOne(a),           "<look> [hp] [color]", "spawn one SHARED world monster everyone sees"),
        G("mobraw", (s, a) => s.MobRaw(a),           "<hi> <lo> [hp]",  "raw-sprite 0x16 probe (self-only)"),
        G("mobrow", (s, a) => s.MobRow(a),           "<lo> <hi> [step]", "sweep graphic ids (0x16, self-only)"),
        G("spawn",  (s, a) => s.SpawnCritters(a),    "[look] [hp]",     "a small pack around you"),
        G("dye",    (s, a) => s.DyeProbe(a),         "<n>",             "war-paint dye: set appearance[4]"),

        // ---- media ----------------------------------------------------------------------------------
        // @music is the one PLAYER-tier command in this block: it's a personal jukebox, not a probe. Every
        // 0x19 it sends goes to the caller's own session, so the loudest a player can be is loud at himself,
        // and the client's own Options menu has no way to pick a track. The rest of the block stays GM-only.
        P("music",    (s, a) => s.PlayMusicCmd(a),  "[name|id] [vol] [mp3|midi] | old|new | stop",
                                                    "play a music track, or pick the soundtrack (vol 0-255, default 100; no argument lists them)"),
        // @snd, @mobact, @efx, @mtx and @hit are RE probes and live in Server/Session.Probes.cs; the rest
        // of this block (@music, the three @*snd slots, @weather, @clock, @setting, @doze) are operator
        // tools and stayed where they were.
        G("snd",      (s, a) => s.SoundProbe(a),    "<id> [id2 ...]", "play raw client sound ids, up to 8 at once (NexusTK.snd holds 001..197.wav)"),
        // One handler, three slots (Session.Media.SetSfx): these differed only in the field they wrote.
        G("swingsnd", (s, a) => s.SetSfx(a, ref s._gm.SwingSfx, "swing"),      "<id>", "set + audition the melee swing sfx (0 mutes it)"),
        G("fistsnd",  (s, a) => s.SetSfx(a, ref s._gm.FistSfx,  "fist swing"), "<id>", "set + audition the unarmed swing sfx (0 mutes it)"),
        G("hitsnd",   (s, a) => s.SetSfx(a, ref s._gm.HitSfx,   "hit"),        "<id>", "set + audition the on-connect impact sfx (0 mutes it)"),
        G("mobact",   (s, a) => s.MobActionProbe(a), "<type> [time]", "set + preview the mob attack-pose action (0x1A) on the faced mob"),
        G("efx",      (s, a) => s.EffectProbe(a),   "<id> [id2 ...]", "play raw Effect.tbl animations over yourself, ids 0-127, up to 8 at once"),
        G("mtx",      (s, a) => s.MiniTextProbe(a), "<type> [text...]", "audition a raw SendMiniText channel (0 wisp, 3 mini/status, 5 system, 11 group, 12 clan)"),
        G("weather",  (s, a) => s.WeatherProbe(a),  "clear|rain|snow|0-2 | auto | raw <0-255>", "pin this map's zone weather, by name or by index (auto releases it back to the season)"),
        G("clock",    (s, a) => s.ClockCmd(a),      "[0-23 | real]", "read or pin the shared in-game hour (totem-time windows follow it; real = release)"),
        G("setting",  (s, a) => s.SettingCmd(a),    "[name] [on|off]", "read/set any 0x1b Options toggle (omit on|off to toggle; bare @setting lists them all)"),
        G("doze",     (s, a) => s.DozeSelfCmd(a),   "[secs|off]", "put YOURSELF to sleep (Doze can't be self-targeted on the wire)"),

        // ---- protocol probes ------------------------------------------------------------------------
        // Handlers in Server/Session.Probes.cs, except the four with production callers or a native
        // client gesture behind them: @users (Session.UserList.cs), @askpic, @self and @click.
        G("hit",      (s, a) => s.HitProbe(a),          "<pct 0-100> [crit 0-255]", "0x13 over-head HP bar + hit animation on the faced mob"),
        G("hpprobe",  (s, a) => s.StatHpTest(a),        "<cur> <max>", "diag: pin the maxHP/maxMP offsets (@hp is the setter)"),
        G("s",        (s, a) => s.StatProbe(a),         "<hexop> [hexflags]", "fire a sentinel status packet"),
        G("stg",      (s, a) => s.StatGradient(a),      "",        "self-describing gradient stats packet"),
        G("r6",       (s, a) => s.StatReplay6x(a),      "[hexop]", "replay a captured 6.x stats packet"),
        G("batch",    (s, a) => s.StatBatch(a),         "",        "sentinel probe over a curated opcode set"),
        G("sweep",    (s, a) => s.StatSweep(a),         "",        "disabled (crashes the client)"),
        G("mailflag", (s, a) => s.MailFlagProbe(a),     "<off 0-79> [valHex]", "sweep the 0x08 mail/parcel notify byte (val defaults to 0x11 = mail+parcel; try offsets 40-57)"),
        G("nat",      (s, a) => s.StatNation(a),        "<id>",    "sweep nation id -> HUD name"),
        G("users",    (s, a) => s.UserListCmd(a),       "[sort|sweep]", "0x36 user list (sweep = label every cell)"),
        G("askpic",   (s, a) => s.SendResendProfilePic(), "",      "0x49 - make the client re-upload users/<name>.epf"),
        G("totemsweep",(s, a) => s.StatTotem(a),        "<id>",    "diag: sweep totem id -> HUD name (@totem is the setter)"),
        G("self",     (s, a) => s.SendSelfProfile(),    "",        "native 0x39 self-profile"),
        G("leg",      (s, a) => s.SendProfileReplay6x(), "",       "exact 6.x 0x39 replay"),
        G("ckm",      (s, a) => s.SendClickMarker(),    "",        "0x34 with marker strings"),
        G("click",    (s, a) => s.ClickProfileCmd(a),   "[name]",  "native 0x34 click-profile"),
        G("boardobj", (s, a) => s.BoardObjProbe(),      "",        "board-sign calibration for the faced tile"),
        G("wmpos",    (s, a) => s.WorldMapPosCmd(a),    "<i> <x> <y>", "live-tune a world-map destination dot"),
        G("wmtest",   (s, a) => s.WorldMapTestCmd(a),   "[bg]",    "native world-map screen with a given background"),
        G("pkt",      (s, a) => s.RawPacketCmd(a),      "<hexop> [tokens] | add | send | show | clear | file <name>",
                                                        "send a raw server->client packet; tokens are xx hex byte, #n u16BE, %n u32BE, :text raw ASCII, $text ASCII behind a u16BE length, '_' for a space inside :/$, ';' starts a comment"),
        G("delreason", (s, a) => s.DelReasonSweep(a),   "[lo] [hi]", "sweep the 0x10 reason byte for a silent one"),
        G("look533",  (s, a) => s.Look533Cmd(a),        "[i v | clear]", "5.33: override one of the 11 appearance bytes and redraw"),
    };

    /// <summary>name -> command, aliases included. Built once; a duplicate name is a programming error and
    /// throws at startup rather than silently shadowing.</summary>
    private static readonly Dictionary<string, Command> CommandsByName = BuildCommandIndex();

    private static Dictionary<string, Command> BuildCommandIndex()
    {
        if (FirstDuplicateName(CommandTable.SelectMany(c => c.Names)) is { } dup)
            throw new InvalidOperationException($"duplicate chat command '{Prefix}{dup}' in Server/Commands.cs");

        var map = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CommandTable)
            foreach (var n in c.Names)
                map[n] = c;
        return map;
    }

    /// <summary>The first name in <paramref name="names"/> that repeats one already seen, or null. Matching
    /// is case-insensitive because the LOOKUP is: "@Warp" and "@warp" are one command, so declaring both is
    /// the same collision as declaring "warp" twice.
    ///
    /// <para>Split out of <see cref="BuildCommandIndex"/> so the check is reachable with a deliberately
    /// broken list. The real table must never contain a duplicate — that is the whole point — so there is
    /// otherwise no way to run this code and find out whether it works.</para></summary>
    internal static string? FirstDuplicateName(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
            if (!seen.Add(n)) return n;
        return null;
    }

    /// <summary>Build the name index NOW, at startup, instead of at the first '@' someone types.
    ///
    /// <para>A duplicate name is a programming error, and until this existed it surfaced as an exception
    /// inside whichever session first ran a command — on a background connection thread, hours after the
    /// deploy, taking that one player's session down while the server carried on looking healthy. Called
    /// from Program so the process refuses to start instead.</para>
    ///
    /// <para>The row and name counts go in the log for the same reason the map counts do: a table that
    /// silently lost half its rows to a bad merge is otherwise invisible until someone misses a command.
    /// </para></summary>
    internal static void WarmCommandTable() =>
        Log.Info($"=== chat commands: {CommandTable.Length} command(s), {CommandsByName.Count} name(s) " +
                 $"including aliases; prefix '{Prefix}'");

    /// <summary>The table's rows as plain data — names, the <c>Args</c> shape, the help line. For tests: the
    /// table is the single source of truth for what a command is CALLED and what it TAKES, and nothing else
    /// can assert that a row is well formed without being able to read it.</summary>
    internal static IEnumerable<(string[] Names, string Args, string Help)> CommandRows()
        => CommandTable.Select(c => (c.Names, c.Args, c.Help));

    /// <summary>Split a chat message into command name and ARGUMENT TAIL. False when it isn't a command
    /// (no prefix, or prefix alone), in which case the message is ordinary speech. Pure and static so the
    /// dispatch split can be executed in a test rather than only read — the args-tail contract is exactly
    /// where the move to the command table went wrong once already.</summary>
    internal static bool SplitCommand(string text, out string name, out string args)
    {
        name = args = "";
        if (text.Length < 2 || text[0] != Prefix) return false;
        int sp = text.IndexOf(' ');
        name = sp < 0 ? text[1..] : text[1..sp];
        args = sp < 0 ? "" : text[(sp + 1)..].Trim();
        return true;
    }

    // ---- where a command answers -------------------------------------------------------------------
    //
    // Three unrelated client widgets can carry a line, and which one a command used to reach for was
    // whatever its author happened to type: 121 SendLog, 16 SendMiniText and 11 SendMessage calls in
    // Session.GmCommands.cs alone, so @lvl popped a modal, @item made the character speak, and @clip
    // printed in the status pane. They are not interchangeable:
    //
    //   0x02  SendMessage   the LOGIN message box. It does not stack: a second line REPLACES the first, so
    //                       multi-line output silently becomes one line. It is the pre-world channel (see
    //                       Session.Dialog.cs) and no command answers on it.
    //   0x0D  SendLog       self-speech. A bubble over your own character plus a chat-pane line — loud, and
    //                       clamped at 250 characters (@dog's reply was being cut mid-word).
    //   0x0A  SendMiniText  the status / mini-text pane under the inventory: where the game already puts
    //                       "experience gained", look-at names and pickup lines, and where RTK's own scripts
    //                       put cast confirmations, cooldowns and refusals alike (clif_sendminitext — see
    //                       docs/4.x/Protocol.md §11f). Scrolls, and holds 0x8000 characters.
    //
    // THE RULE, by what the LINE is rather than by which handler produced it:
    //
    //   readout       a report you asked for: a listing, a dump, a value read back,
    //                 or the help a bare command prints.                                Reply  -> status pane
    //   confirmation  a line stating what the command changed.                          Reply  -> status pane
    //   refusal       the command's action did NOT happen: bad arguments, no such       Refuse -> speech bubble
    //                 target, a state or a shape that forbids it.
    //
    // Readouts and confirmations take the status pane because it is where the rest of this server already
    // answers, and because it is the only one of the three that survives a twenty-line listing intact.
    // Refusals take the bubble precisely BECAUSE it is loud and in the way: a refusal that scrolls quietly
    // past in the status pane is indistinguishable from a command that ran and did nothing, which is the
    // confusion this tooling exists to prevent.
    //
    // A search that matched nothing is a readout, not a refusal — the listing ran, the answer was empty.
    // The test is whether the command's ACTION happened: "no items match" from @items is a result, the same
    // words from @item are a refusal, because @item granted nothing.
    //
    // What does NOT come through here, and calls the Send* methods directly instead: a line addressed to
    // ANOTHER player (@bring, @carnage), a broadcast (@announce -> SystemAnnounce), and the two probes whose
    // channel is the thing under test (@text and @mtx audition a raw 0x0A type by number).

    /// <summary>Roughly how many characters of the status pane's font fit on one line, measured off a real
    /// 4.95 client (2026-09-02, screenshots of @commands and @help).
    ///
    /// <para>APPROXIMATE, and it cannot be otherwise: the pane's font is PROPORTIONAL, so a line of 30 narrow
    /// characters and a line of 30 wide ones are different widths on screen and no character count is exactly
    /// right. This is the count at which a line of ordinary mixed text stopped fitting, so it is a budget
    /// rather than a measurement — the wrapper below aims under it, and a line that overruns slightly still
    /// renders, it just wraps again in the client.</para>
    ///
    /// <para>The pane wraps by itself at CHARACTER boundaries when a line is too long, which is the whole
    /// reason this exists: "@commands @warp @go @rez @" then "approach" on the next line, command names split
    /// down the middle. Every line has to leave here already short enough, or already broken somewhere it
    /// reads.</para></summary>
    /// <para>Internal so a test can pin the wrapper directly, for the same reason
    /// <see cref="SplitCommand"/> is: the interesting cases (a token wider than the whole pane, a line that
    /// only just fits) are awkward to reach through a real command and are exactly the ones that break.
    /// </para>
    internal const int PaneWidth = 30;

    /// <summary>How many dashes the separator rule is made of. Its own number rather than
    /// <see cref="PaneWidth"/> reused, because a dash is one of the NARROWEST glyphs in a proportional font:
    /// 30 dashes draw considerably shorter than 30 characters of ordinary text, so the count that reads as a
    /// full-width rule is not the count that fits a line of prose. Starts equal to PaneWidth and is meant to
    /// be tuned by eye against the client.</summary>
    internal const int PaneRuleDashes = PaneWidth;

    /// <summary>The separator itself. Sent raw, never wrapped: it is one token, and if it is tuned wider than
    /// the pane the client drawing it as one over-long line is exactly what a rule should look like.</summary>
    internal static readonly string PaneRule = new('-', PaneRuleDashes);

    /// <summary>True when this command invocation has not put a line in the status pane yet, so the next one
    /// owes a separator first.
    ///
    /// <para>Lazy on purpose. The pane is one scrolling column shared with pickups, experience and look-at
    /// names, so consecutive commands ran together — but a rule printed eagerly at dispatch would also head
    /// every command that answers on the BUBBLE (every refusal), spending a pane line to separate output
    /// that never arrived. Set when a message is recognized as a command, spent by the first pane line it
    /// actually produces, so: one rule per invocation however many Reply calls it makes, and no rule at all
    /// for a command that only refuses.</para>
    ///
    /// <para>A command that answers with a RAW pane line — @text and @mtx send a 0x0A on a type given to
    /// them by number, which is the whole point of those two — must pay the rule itself, with
    /// <see cref="PayPaneRule"/>, BEFORE that line. Otherwise the raw line goes out unheaded and the rule
    /// lands in the middle of the invocation, above whatever Reply follows it. The rule always sits at the
    /// top of a command's output; there is no case where it appears anywhere else.</para></summary>
    private bool _paneRuleDue;

    /// <summary>Break one logical line into pane-width lines at SPACES ONLY.
    ///
    /// <para>A token is never split: if one is longer than the whole pane (a long map name, a hex dump, a
    /// packet's bytes) it goes out alone on its own line and the client wraps it however it likes — half of a
    /// mangled token is no worse than half of a mangled token, but half of a mangled COMMAND NAME is a name
    /// you cannot type back, which is what made the old output unusable.</para>
    ///
    /// <para>A line that already fits comes back untouched, exactly as written. That matters: some lines are
    /// deliberately spaced (RTK's verbatim column-17 toggle lines, "No-clip          :ON") and re-flowing
    /// them would collapse the padding for no gain. Continuation lines keep the original leading indent, so
    /// an indented list entry stays visually inside its list.</para>
    ///
    /// <para>A line that DOES have to be wrapped is rebuilt from its <c>Split(' ')</c>'d words, so any run of
    /// spaces inside it collapses to one on the way out — a caller that wants a run of spaces preserved only
    /// gets it if the whole line is short enough to skip wrapping entirely (see #159).</para></summary>
    internal static IEnumerable<string> WrapForPane(string line)
    {
        if (line.Length <= PaneWidth) { yield return line; yield break; }

        string indent = line[..(line.Length - line.TrimStart(' ').Length)];
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) { yield return line; yield break; }

        var built = new System.Text.StringBuilder(indent);
        bool any = false;
        foreach (var word in words)
        {
            if (any && built.Length + 1 + word.Length > PaneWidth)
            {
                yield return built.ToString();
                built.Clear().Append(indent);
                any = false;
            }
            if (any) built.Append(' ');
            built.Append(word);
            any = true;

            // A single token that does not fit even on a line of its own: send it alone and start over,
            // rather than letting the next token ride along behind it.
            if (built.Length > PaneWidth)
            {
                yield return built.ToString();
                built.Clear().Append(indent);
                any = false;
            }
        }
        if (any) yield return built.ToString();
    }

    /// <summary>Put one already-wrapped line in the status pane, paying the invocation's separator first if
    /// it is still owed. Every pane line a command produces goes through here, which is what makes the rule
    /// land above the FIRST one and nowhere else.</summary>
    private void SendPaneLine(string line)
    {
        if (_paneRuleDue)
        {
            _paneRuleDue = false;
            SendMiniText(PaneRule);
        }
        SendMiniText(line);
    }

    /// <summary>One line of command output — a readout or a confirmation. See the rule above. Wrapped to the
    /// pane, so a caller never has to think about the width; a line that already fits is sent as written.
    /// </summary>
    private void Reply(string line)
    {
        foreach (var pane in WrapForPane(line)) SendPaneLine(pane);
    }

    /// <summary>A multi-line readout, one status-pane line per wrapped line. The pane scrolls, so the whole
    /// listing survives; the login box this used to be routed to would have shown only the last line.
    ///
    /// <para>An EMPTY sequence prints nothing at all, separator included — the rule is owed by the first line
    /// that actually appears, not by the call that might have produced one.</para></summary>
    private void Reply(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            foreach (var pane in WrapForPane(line)) SendPaneLine(pane);
    }

    /// <summary>A readout that is a LIST, under a header naming it and its size.
    ///
    /// <para>The header IS this listing's separator, so it cancels the dashed rule rather than following it:
    /// a list that announced itself twice would spend two of the pane's few lines saying the same thing. It
    /// only cancels a rule that is still owed — a command that already said something before its list (@pkt
    /// prints its usage line first) keeps the rule it has already paid for, at the top where it belongs, and
    /// the header still opens the list underneath.</para>
    ///
    /// <para>There is no closing rule: the next command's separator does that job now, and the pane has too
    /// few lines to spend one on punctuation that something else already provides.</para></summary>
    private void ReplyList(string title, IEnumerable<string> lines)
    {
        _paneRuleDue = false;
        Reply($"= {title} =");
        Reply(lines);
    }

    /// <summary>The command did not do what was asked. Loud on purpose — see the rule above.</summary>
    private void Refuse(string line) => SendLog(line);

    /// <summary>Try to run <paramref name="text"/> as a chat command. Returns false if it isn't one (no
    /// prefix), in which case the caller treats it as ordinary speech.
    ///
    /// <para>Private again as of #29. It was briefly internal so a test could drive a command through the
    /// prefix split and the table without a socket — but a packet is now the atomic unit of work against a
    /// session, and the state monitor wraps <c>Session.Handle</c>, so entering here skipped the monitor and
    /// tripped its assert. Tests drive a framed 0x0E through <c>Session.Receive</c> instead, which is both
    /// the real entry point and a wider one.</para></summary>
    private bool TryRunCommand(string text)
    {
        if (!SplitCommand(text, out string name, out string args)) return false;

        // From here on this IS a command, so its first pane line owes a separator — including @help below
        // and the unknown-command answer, which simply never spends it because they answer on the bubble.
        _paneRuleDue = true;

        // @help — special-cased before the table so it works at every tier. Accepts a page (as a suffix,
        // "@help2", or an argument, "@help 2") or a keyword filter ("@help item"). The full detail list runs
        // past the chat pane, so a bare @help pages it rather than dumping all 107 lines (see ShowCommandHelp).
        if (name.Equals("help", StringComparison.OrdinalIgnoreCase)
            || (name.Length > 4 && name.StartsWith("help", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(name.AsSpan(4), out _)))
        {
            int page = 1; string filter = "";
            if (name.Length > 4) int.TryParse(name.AsSpan(4), out page);   // "@help2"
            else if (int.TryParse(args, out var p)) page = p;             // "@help 2"
            else filter = args;                                          // "@help item"
            ShowCommandHelp(filter, page);
            return true;
        }

        var access = Access;
        bool known = CommandsByName.TryGetValue(name, out var cmd);
        if (!known || access < cmd!.Min)
        {
            // Someone below the tier must not be able to tell a gated command from a typo, so both answers
            // are identical for them. Staff get the more useful message.
            if (known) Log.Info($"   -> denied {cmd!.Min} command from {access} '{_char.Name}': \"{text}\"");
            Refuse(access > AccessLevel.Player ? $"Unknown command '{Prefix}{name}'. Try {Prefix}help." : "Unknown command.");
            return true;
        }

        cmd.Run(this, new CommandArgs(cmd, args));
        return true;
    }

    /// <summary>How many COMMANDS one <c>@help</c> page describes. Not how many pane lines it produces: each
    /// command is now its calling shape on one line plus a description wrapped to <see cref="PaneWidth"/>
    /// below it, so a page of six is more like twenty lines in the pane.
    ///
    /// <para>Was 12, sized for the chat pane when @help still spoke. The status pane is far narrower, so 12
    /// became a wall of ~50 lines. Six is a guess at a screenful and wants the same real-client look the
    /// width got — the pane's HEIGHT has not been measured.</para></summary>
    private const int HelpPageSize = 6;

    /// <summary>"@help [page|filter]" — every command this session may actually run, so the list a player sees
    /// contains no staff tooling at all, and a tester's contains no GM tooling.
    ///
    /// The full detail list (name + args + description, one command per line) is what people want, but it runs
    /// past the chat pane. So a keyword filter shows just its matches in full, and an unfiltered list is PAGED:
    /// each page is a screenful of full-detail lines with a footer pointing at the next page ("@help2"). We
    /// page rather than drop the descriptions — the descriptions are the point of @help.</summary>
    private void ShowCommandHelp(string filter, int page)
    {
        var access = Access;
        var reachable = CommandTable.Where(c => access >= c.Min).ToList();

        // Keyword filter: show every match in full. A keyword narrows 107 commands to a few, so this fits.
        if (filter.Length > 0)
        {
            var matches = reachable
                .Where(c => c.Names.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                         || c.Help.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) { Reply($"No command matches \"{filter}\"."); return; }
            ReplyList($"{matches.Count} matching \"{filter}\"", matches.SelectMany(HelpLines));
            return;
        }

        // No filter: page the whole reachable list, descriptions intact.
        int pages = Math.Max(1, (reachable.Count + HelpPageSize - 1) / HelpPageSize);
        page = Math.Clamp(page, 1, pages);
        int first = (page - 1) * HelpPageSize;
        int last = Math.Min(first + HelpPageSize, reachable.Count);

        ReplyList($"{Prefix}help {first + 1}-{last} of {reachable.Count} (p{page}/{pages})",
                  reachable.Skip(first).Take(HelpPageSize).SelectMany(HelpLines));
        // One short line each rather than one long one: the old single footer wrapped to four pane lines.
        if (page < pages) Reply($"next: {Prefix}help{page + 1}");
        Reply($"search: {Prefix}help <word>");
    }

    /// <summary>One command's @help entry, as the TWO logical lines the pane can hold: the calling shape,
    /// then the description indented under it. They used to be one line — "@bring &lt;username&gt; — pull an
    /// online player to your side (the inverse of @approach)" — which the pane broke into four, starting
    /// mid-word, so the shape you actually wanted to read was buried in prose.</summary>
    private static IEnumerable<string> HelpLines(Command c)
    {
        yield return HelpShape(c);
        yield return " " + c.Help;
    }

    /// <summary>Just the calling shape: "@name/@alias &lt;args&gt;". Shared by @help and by
    /// <see cref="CommandArgs.Usage"/>, so the two cannot describe the same command differently.</summary>
    private static string HelpShape(Command c)
    {
        string names = string.Join('/', c.Names.Select(n => Prefix + n));
        return $"{names}{(c.Args.Length > 0 ? " " + c.Args : "")}";
    }

    /// <summary>"@commands" / "@cmds" — the compact index: just the NAMES of every command this session can
    /// run. The quick companion to the paged, described <see cref="ShowCommandHelp"/>: reach for this to SEE
    /// everything you have, then '@help &lt;word&gt;' to read what one does. Access-filtered like @help, so a
    /// player sees no staff tooling.
    ///
    /// <para>THREE names a line at most, and <see cref="WrapForPane"/> cuts that to two when the names are
    /// long. It used to pack seven, which was fine in the chat pane and unreadable in the status pane: the
    /// client wrapped the overflow at a character boundary, so the roster came out as "@commands @warp @go
    /// @rez @" / "approach" — command names split down the middle, none of them typeable.</para></summary>
    private void ShowCommandIndex()
    {
        var access = Access;
        var names = CommandTable.Where(c => access >= c.Min).Select(c => Prefix + c.Names[0]).ToList();
        ReplyList($"{Prefix}commands ({names.Count})",
                  names.Chunk(3).Select(chunk => string.Join(' ', chunk)));
        Reply($"{Prefix}help <word> for one");
    }
}
