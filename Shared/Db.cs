using Microsoft.Data.Sqlite;

namespace Shared;

/// <summary>
/// The single SQLite database shared by the login and game processes (accounts, characters, handoff
/// tokens, board posts). One file at &lt;root&gt;/state/project1998.db in WAL mode so both processes can access it
/// concurrently: WAL allows many readers plus one writer across processes, and a per-connection
/// busy_timeout absorbs the brief lock waits when both write at once.
///
/// The bound on a contended write is <see cref="BusyTimeoutMs"/>: after about five seconds of waiting for
/// another writer's lock, the statement fails with "database is locked" rather than blocking the calling
/// thread any longer. Two separate timeouts have to agree for that to be true —
/// <c>PRAGMA busy_timeout</c> is how long SQLite itself retries inside one step, and
/// <c>SqliteConnection.DefaultTimeout</c> (30 seconds if nothing sets it) is how long
/// Microsoft.Data.Sqlite keeps re-running a statement that came back SQLITE_BUSY. They compound, so a
/// connection with only the pragma set waits the full thirty seconds. <see cref="Open()"/> therefore sets
/// both from the same constant.
///
/// A failed save is not a lost one. <c>CharacterStore.SaveJson</c>/<c>SaveManyJson</c>/<c>SaveWith</c>
/// catch the exception, log it and return false; <c>Session.CaptureAndWrite</c> puts the dirty flag back
/// so the next flush or autosave sweep retries, and the trade path re-dirties both sides. Failing in five
/// seconds rather than thirty means the session thread is released twenty-five seconds sooner and the
/// retry happens on the next sweep instead.
///
/// Content (items/mobs/warps/…) stays in flat files — this DB is only for MUTABLE state that must be
/// crash-safe and shared between the two processes.
/// </summary>
public static class Db
{
    /// <summary>How long a contended write waits before it gives up, in milliseconds. Feeds BOTH
    /// <c>PRAGMA busy_timeout</c> and <see cref="SqliteConnection.DefaultTimeout"/> so the two cannot
    /// disagree — see the class summary for why having only one of them set is not the same bound.</summary>
    internal const int BusyTimeoutMs = 5000;

    /// <summary><see cref="SqliteConnection.DefaultTimeout"/> is in whole seconds, so this is
    /// <see cref="BusyTimeoutMs"/> rounded UP: a sub-second remainder must not round the provider's
    /// retry window down below SQLite's own, which would make the pragma unreachable.</summary>
    private const int BusyTimeoutSeconds = (BusyTimeoutMs + 999) / 1000;

    /// <summary>One schema step: its SQL, and the test for a database that already has it. The test is what
    /// lets a step meet a database whose <c>CREATE TABLE</c> already declared the change (a fresh one, or a
    /// deployment older than the <c>user_version</c> stamp) without applying it twice.</summary>
    private sealed record Migration(string Sql, Func<SqliteConnection, SqliteTransaction, bool> AlreadyApplied);

    private static Migration AddColumn(string table, string column, string sql) =>
        new(sql, (cn, tx) => ColumnExists(cn, tx, table, column));

    private static readonly Migration[] Migrations =
    {
        AddColumn("handoff_tokens", "ip", "ALTER TABLE handoff_tokens ADD COLUMN ip TEXT NOT NULL DEFAULT '';"),
        AddColumn("parcels", "item_owner", "ALTER TABLE parcels ADD COLUMN item_owner TEXT NOT NULL DEFAULT '';"),
        AddColumn("characters", "unreadable_since", "ALTER TABLE characters ADD COLUMN unreadable_since INTEGER;"),
        new(RekeyHandoffTokens, (cn, tx) => PrimaryKeyIs(cn, tx, "handoff_tokens", "username", "nonce_hash")),
    };

    /// <summary>
    /// Migration 4: <c>handoff_tokens</c> keyed by <c>(username, nonce_hash)</c> instead of <c>nonce_hash</c>
    /// alone (the schema comment above says why). SQLite cannot change a primary key in place, so this is its
    /// documented rebuild: create the new table, copy, drop the old one, rename.
    ///
    /// <para>Only rows a consume could still accept are copied: unconsumed and unexpired. Every other row is
    /// one <c>HandoffTokens.Consume</c> refuses already, and under the old key those rows are exactly what
    /// blocked later mints, so they are not carried into the new table. A token minted moments before the
    /// migration is live, is copied, and still enters.</para>
    ///
    /// <para>The hash column is copied as it is: the stored hash is the same SHA-256 of the same surviving
    /// bytes before and after, which is what lets a login server and a game server on either side of this
    /// migration consume each other's tokens during a deploy (Shared/HandoffTokens, the class summary).</para>
    ///
    /// <para>Both processes: this runs inside <see cref="ApplyMigrations"/>' write reservation like every
    /// step. The first process to start takes the reservation and rebuilds; the other waits on the busy
    /// timeout, then reads the new version and does nothing. A process that is already running (the one not
    /// yet restarted) keeps its connections; its next statement on the table re-prepares against the new
    /// schema, and its statements are the same ones this code uses on the same columns.</para>
    /// </summary>
    private const string RekeyHandoffTokens = @"
CREATE TABLE handoff_tokens_rekeyed (
  nonce_hash  TEXT NOT NULL,
  username    TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed    INTEGER NOT NULL DEFAULT 0,
  ip          TEXT NOT NULL DEFAULT '',
  PRIMARY KEY (username, nonce_hash)
);
INSERT INTO handoff_tokens_rekeyed(nonce_hash, username, expires_utc, consumed, ip)
  SELECT nonce_hash, username, expires_utc, consumed, ip FROM handoff_tokens
  WHERE consumed = 0 AND expires_utc > CAST(strftime('%s', 'now') AS INTEGER) AND nonce_hash IS NOT NULL;
DROP TABLE handoff_tokens;
ALTER TABLE handoff_tokens_rekeyed RENAME TO handoff_tokens;";

    internal static int CurrentSchemaVersion => Migrations.Length;

    private static readonly object InitGate = new();
    private static bool _initialized;
    private static string? _path;

    /// <summary>Absolute path of the database file (&lt;root&gt;/state/project1998.db).</summary>
    public static string Path => _path ??= RepoPaths.DbPath();

    /// <summary>
    /// Refuse to start on a deployment that still has the pre-rename <c>nexus.db</c> beside an absent
    /// <c>project1998.db</c>.
    ///
    /// SQLite creates a missing database silently, so without this the server would come up, listen, accept
    /// logins, and drop every player into a world with no accounts and no characters — with nothing in the
    /// log that reads as an error. The data is still on disk and perfectly intact; it just is not the file
    /// being opened any more. That is the single most alarming failure this rename can produce, so it fails
    /// LOUD and early instead.
    ///
    /// Deliberately not an automatic rename. Moving a live WAL database by file is how you corrupt one: the
    /// -wal sidecar holds committed pages that are not yet in the main file, so anything that moves the .db
    /// without the -wal loses them. The safe sequence needs the server stopped and a checkpoint, which is an
    /// operator action with a decision in it, not something to do behind their back at startup.
    /// </summary>
    private static void GuardAgainstPreRenameDatabase()
    {
        var current = Path;
        if (System.IO.File.Exists(current)) return;

        var legacy = System.IO.Path.Combine(RepoPaths.StateDir(), "nexus.db");
        if (!System.IO.File.Exists(legacy)) return;   // fresh deployment: nothing to migrate

        throw new InvalidOperationException(
            $"Found the pre-rename database '{legacy}' but no '{current}'.\n" +
            "Starting now would silently create an EMPTY world and leave your accounts and characters behind.\n\n" +
            "Stop both servers, then rename all three files together (the -wal holds committed pages the\n" +
            "main file does not yet have, so moving the .db alone loses them):\n" +
            "  mv state/nexus.db      state/project1998.db\n" +
            "  mv state/nexus.db-wal  state/project1998.db-wal\n" +
            "  mv state/nexus.db-shm  state/project1998.db-shm\n\n" +
            "(-wal and -shm may not exist if the server was stopped cleanly; that is fine.)");
    }

    /// <summary>Open a ready-to-use connection: the schema is guaranteed to exist, and both halves of the
    /// contention bound are set from <see cref="BusyTimeoutMs"/>, so a write blocked by another writer
    /// fails after about five seconds instead of thirty.</summary>
    public static SqliteConnection Open()
    {
        EnsureInitialized();
        return Open(Path);
    }

    /// <summary>
    /// The same connection against an EXPLICIT file. Internal and not reachable from any configuration: the
    /// server has one database and <see cref="Path"/> names it, so the only caller is a test that needs a
    /// database-wide lock of its own.
    ///
    /// <para>Why that needs a seam at all: <c>BEGIN IMMEDIATE</c> locks the whole FILE, and the test process
    /// has a single one (<c>TestProcessState</c> points P1998_STATE at one temp directory). A fact that holds
    /// the write lock to prove a failed save rolls back therefore locks out every other test running beside
    /// it. Pointing such a fact at its own file is the fix; swapping <see cref="Path"/> for the process would
    /// be the opposite of one, because it would move other collections' writes to the wrong file
    /// mid-run.</para>
    ///
    /// <para>Unlike <see cref="Open()"/> this does NOT initialize anything — there is no per-file
    /// <c>_initialized</c> latch and a lock-holding caller must not pay a schema build inside its window. The
    /// caller runs <see cref="InitializeDatabase"/> on the path once first; a path that has never been
    /// initialized opens as an empty database rather than failing, which is SQLite's behaviour, not
    /// ours.</para></summary>
    internal static SqliteConnection Open(string path)
    {
        var cn = new SqliteConnection($"Data Source={path}");
        // The provider's retry window, which bounds the whole statement. Without this it is 30s and the
        // busy_timeout below bounds nothing that a caller can observe.
        cn.DefaultTimeout = BusyTimeoutSeconds;
        cn.Open();
        using var pragma = cn.CreateCommand();
        // synchronous=NORMAL is per-connection (unlike journal_mode=WAL, which is a persistent DB-file
        // setting) — reapply it on every connection, else this connection silently runs at SQLite's
        // default FULL.
        pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return cn;
    }

    /// <summary>Create the data directory + schema once per process. Idempotent and thread-safe; safe to
    /// run from both processes (CREATE TABLE IF NOT EXISTS). WAL is a persistent DB setting, so setting it
    /// here once is enough for every later connection from either process.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitGate)
        {
            if (_initialized) return;
            System.IO.Directory.CreateDirectory(RepoPaths.StateDir());
            GuardAgainstPreRenameDatabase();
            InitializeDatabase(Path);
            _initialized = true;
        }
    }

    /// <summary>Build or migrate one database file. Internal so tests can exercise a fresh, isolated file.
    ///
    /// <para>This connection CAN contend: <see cref="ApplyMigrations"/> takes the write reservation
    /// (<c>BEGIN IMMEDIATE</c>) deliberately, and login and game initialize the same shared file
    /// independently, so one of them can be holding it while the other starts. It therefore gets the same
    /// paired timeouts as <see cref="Open()"/>. The consequence of losing the race is different here — a
    /// startup that cannot take the reservation within the window throws out of
    /// <see cref="EnsureInitialized"/> rather than returning false — and that is the intended shape: a
    /// process that cannot confirm the schema should fail loudly at startup, not serve a world.</para></summary>
    internal static void InitializeDatabase(string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.DefaultTimeout = BusyTimeoutSeconds;
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $@"
PRAGMA journal_mode=WAL;
PRAGMA busy_timeout={BusyTimeoutMs};
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS accounts (
  username       TEXT PRIMARY KEY COLLATE NOCASE,
  pass_hash      TEXT,
  created_utc    INTEGER,
  last_login_utc INTEGER
);

CREATE TABLE IF NOT EXISTS characters (
  username         TEXT PRIMARY KEY COLLATE NOCASE,
  json             TEXT NOT NULL,
  updated_utc      INTEGER,
  unreadable_since INTEGER
);

-- Moderation state, keyed by the same normalized username as accounts/characters. SEPARATE from `accounts`
-- so that table stays purely about authentication — and so adding this needed no migration of an existing
-- deployment's schema.
--
-- `*_until` is unix SECONDS: 0 = not banned/muted, otherwise the moment it lapses. A permanent action stores
-- Moderation.Forever (year 9999) rather than a -1 sentinel, so every check is the same `until > now`
-- comparison with no special case to forget.
CREATE TABLE IF NOT EXISTS moderation (
  username    TEXT PRIMARY KEY COLLATE NOCASE,
  ban_until   INTEGER NOT NULL DEFAULT 0,
  ban_reason  TEXT,
  ban_by      TEXT,
  ban_at      INTEGER,
  mute_until  INTEGER NOT NULL DEFAULT 0,
  mute_reason TEXT,
  mute_by     TEXT,
  mute_at     INTEGER
);

-- Account bans are trivially evaded by making a new character, IP bans catch a shared household. RTK keeps
-- both axes (ChaBanned + a BannedIP table) and so do we; a GM picks which one fits.
CREATE TABLE IF NOT EXISTS banned_ips (
  ip        TEXT PRIMARY KEY,
  until     INTEGER NOT NULL DEFAULT 0,
  reason    TEXT,
  banned_by TEXT,
  banned_at INTEGER
);

-- Append-only record of every moderation action, including the ones that UNDO something. A ban with no
-- record of who placed it and why is unreviewable, and who LIFTED a ban is the question that actually gets
-- asked. Never updated, never deleted.
CREATE TABLE IF NOT EXISTS mod_log (
  id     INTEGER PRIMARY KEY AUTOINCREMENT,
  at_utc INTEGER NOT NULL,
  actor  TEXT NOT NULL,
  action TEXT NOT NULL,
  target TEXT,
  detail TEXT
);

-- Small key/value store for state that belongs to the WORLD rather than to any character — currently the
-- in-game clock (RTK keeps the same thing in its `Time` table). Without this the calendar resets to its
-- compiled-in start on every restart, which stopped being harmless the moment deploys began restarting the
-- server on a schedule.
CREATE TABLE IF NOT EXISTS world_state (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- Login->game handoff nonces. `ip` is the address the login connection came from; the game arrival must
-- come from the same one. That binding is what carries the security when the nonce itself is short: the
-- client only echoes back the bytes its fixed-size handoff field has room for after the username, so a
-- long name leaves as little as one significant byte (see Shared/HandoffTokens).
--
-- Keyed by the account AND the hash. The hash covers only the surviving nonce bytes: none at all for an
-- 11-letter name and one byte of 255 values at 10, so a key of the hash alone was one key shared by every
-- 11-letter account (255 for every 10-letter one), and a second account's mint collided with the first's
-- row. Migration 4 rebuilds a table created with that key; the hash itself is unchanged.
CREATE TABLE IF NOT EXISTS handoff_tokens (
  nonce_hash  TEXT NOT NULL,
  username    TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed    INTEGER NOT NULL DEFAULT 0,
  ip          TEXT NOT NULL DEFAULT '',
  PRIMARY KEY (username, nonce_hash)
);

CREATE TABLE IF NOT EXISTS board_posts (
  id       INTEGER PRIMARY KEY AUTOINCREMENT,   -- internal rowid
  board_id INTEGER NOT NULL,
  position INTEGER NOT NULL,                     -- BrdPosition: 1-based within its own board (on the wire)
  author   TEXT,
  topic    TEXT,
  body     TEXT,
  month    INTEGER,
  day      INTEGER
);

-- RTK nmail (clif.c case 9/2/3 reuse boards_showposts/boards_readpost against board id 0 — a player's own
-- mailbox is really just a board only they can see). One row per piece of mail; position is 1-based within
-- the RECIPIENT's own mailbox (mirrors board_posts.position's per-board scoping). item_id<0 = no parcel
-- attached (see Mail.cs).
CREATE TABLE IF NOT EXISTS mail_posts (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  recipient TEXT NOT NULL COLLATE NOCASE,
  position  INTEGER NOT NULL,
  sender    TEXT,
  topic     TEXT,
  body      TEXT,
  month     INTEGER,
  day       INTEGER,
  item_id     INTEGER NOT NULL DEFAULT -1,
  item_amount INTEGER NOT NULL DEFAULT 0,
  item_dura   INTEGER NOT NULL DEFAULT 0,
  claimed     INTEGER NOT NULL DEFAULT 0,
  is_read     INTEGER NOT NULL DEFAULT 0
);

-- Parcels: item/gold sent player-to-player, collected from a MessengerNpc (RTK Parcels table +
-- messenger.lua/Parcel.lua). SEPARATE from mail — RTK keeps them apart, and a gold parcel has no letter.
-- Name-addressed like mail_posts (offline recipients resolve by CharacterStore). item_id<0 = a GOLD
-- parcel (item_amount = the coin amount); item_id>=0 = an item stack (item_amount = count). position is
-- 1-based within the recipient's own queue (FIFO claim). See Server/Parcel.cs.
CREATE TABLE IF NOT EXISTS parcels (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  recipient TEXT NOT NULL COLLATE NOCASE,
  position  INTEGER NOT NULL,
  sender    TEXT,
  item_id     INTEGER NOT NULL DEFAULT -1,
  item_amount INTEGER NOT NULL DEFAULT 0,
  item_dura   INTEGER NOT NULL DEFAULT 0,
  engrave     TEXT,
  item_owner  TEXT NOT NULL DEFAULT '',   -- bound owner carried with a parcelled item so the bond survives the mail
  month     INTEGER,
  day       INTEGER
);

-- Map cells a PLAYER changed: doors opened, GM edits, event scripts. The .map files on disk are never
-- written and authored corrections live in game-data/MapCells.csv, so this table holds only the
-- diff against that authored baseline (MapData.RuntimeCells) — NOT the diff against the file. Baking
-- authored corrections in here would make the DB outrank the CSV, and editing the CSV would silently
-- stop working. One row per changed cell; the row is DELETED when a cell returns to its baseline (a
-- door toggled shut again), so the table stays proportional to what is actually open right now.
CREATE TABLE IF NOT EXISTS map_cells (
  map  INTEGER NOT NULL,
  x    INTEGER NOT NULL,
  y    INTEGER NOT NULL,
  tile INTEGER NOT NULL,
  pass INTEGER NOT NULL,
  obj  INTEGER NOT NULL,
  PRIMARY KEY (map, x, y)
);

-- Locked doors a player has opened (Doors.csv Locked/Key). Separate from map_cells because unlocking is
-- a separate axis from the open/closed GRAPHIC: a door can be unlocked but shut. Without this a key with
-- ConsumeKey=1 was spent and the door relocked on restart — the key gone and the door shut again.
CREATE TABLE IF NOT EXISTS map_unlocks (
  map          INTEGER NOT NULL,
  x            INTEGER NOT NULL,
  y            INTEGER NOT NULL,
  unlocked_utc INTEGER,
  PRIMARY KEY (map, x, y)
);
";
        cmd.ExecuteNonQuery();

        ApplyMigrations(cn);
    }

    private static void ApplyMigrations(SqliteConnection cn)
    {
        while (true)
        {
            // Take the write reservation before reading the version. Login and game initialize the shared
            // file independently; this makes choosing + applying one step atomic across both processes.
            using var tx = cn.BeginTransaction(deferred: false);
            using var readVersion = cn.CreateCommand();
            readVersion.Transaction = tx;
            readVersion.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(readVersion.ExecuteScalar());
            if (version > Migrations.Length)
                throw new InvalidOperationException(
                    $"Database schema {version} is newer than this server supports ({Migrations.Length}).");
            if (version == Migrations.Length)
            {
                tx.Commit();
                return;
            }

            var migration = Migrations[version];
            if (!migration.AlreadyApplied(cn, tx))
            {
                using var alter = cn.CreateCommand();
                alter.Transaction = tx;
                alter.CommandText = migration.Sql;
                alter.ExecuteNonQuery();
            }

            using var stamp = cn.CreateCommand();
            stamp.Transaction = tx;
            stamp.CommandText = $"PRAGMA user_version = {version + 1};";
            stamp.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private static bool ColumnExists(
        SqliteConnection cn,
        SqliteTransaction tx,
        string table,
        string column)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
            if (string.Equals(rows.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="table"/>'s primary key is exactly <paramref name="columns"/>, in that
    /// order (<c>PRAGMA table_info</c>'s <c>pk</c> column numbers the key's columns from 1).</summary>
    internal static bool PrimaryKeyIs(
        SqliteConnection cn,
        SqliteTransaction? tx,
        string table,
        params string[] columns)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info({table});";
        var key = new SortedList<long, string>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
            if (rows.GetInt64(5) > 0) key.Add(rows.GetInt64(5), rows.GetString(1));
        return key.Values.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase);
    }
}
