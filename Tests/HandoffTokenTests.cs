using Microsoft.Data.Sqlite;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The login→game handoff token, mint through consume. These run against a real SQLite database in a
/// throwaway state directory, because the guarantees under test are single-use and expiry — both enforced
/// by the SQL, so a mock would only prove the mock works.
///
/// The regression that motivated this file: a freshly created 8-character account could log in but was then
/// bounced at world entry with "invalid/expired handoff token". The client shares ONE fixed 13-byte field
/// between the username and the nonce, so a name longer than 7 characters truncates the nonce — and the
/// game server was comparing a fixed 4 bytes it would never receive.
/// </summary>
[Collection("db")]
public class HandoffTokenTests : IDisposable
{
    private const string Ip = "203.0.113.7";           // TEST-NET-3; never a real client
    private readonly List<string> _minted = new();

    /// <summary>A throwaway username of EXACTLY <paramref name="length"/> characters — the length is the
    /// whole point of these tests — and unique, so two names in one test are genuinely different accounts.</summary>
    private string Name(int length)
    {
        var n = ("zz" + Guid.NewGuid().ToString("N"))[..length];
        _minted.Add(n);
        return n;
    }

    public void Dispose()
    {
        try
        {
            using var cn = Db.Open();
            foreach (var n in _minted)
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DELETE FROM handoff_tokens WHERE username=$u;";
                cmd.Parameters.AddWithValue("$u", Auth.Key(n));
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* best effort cleanup */ }
    }

    /// <summary>The client's handoff field as these tests model it: 13 bytes, the last forced to NUL. Written
    /// out here rather than read from <see cref="HandoffTokens"/>, so the server's constants have an
    /// independent number to disagree with.</summary>
    private const int ClientField = 13;

    /// <summary>Simulate the client's copy of our 0x03 reply tail: it strncpy's
    /// &lt;ulen&gt;&lt;username&gt;&lt;nonce&gt; into a 13-byte field (12 bytes + a forced NUL) and echoes
    /// the field back in 0x10. This is the exact transform that broke long names.</summary>
    private static byte[] ClientEcho(string user, byte[] nonce)
    {
        var blob = new List<byte> { (byte)user.Length };
        blob.AddRange(System.Text.Encoding.ASCII.GetBytes(user));
        blob.AddRange(nonce);
        while (blob.Count < ClientField) blob.Add(0);   // strncpy pads the remainder with NULs
        blob = blob.GetRange(0, ClientField);
        blob[ClientField - 1] = 0;                      // ...and the field is always NUL-terminated
        return blob.GetRange(1 + user.Length, ClientField - 1 - user.Length).ToArray();   // the token slot
    }

    // 7 chars was the only length ever probed, which is how the fixed-4-bytes assumption survived. The 8-
    // to 10-char cases are the bug: each one used to be rejected at world entry.
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void TruncatedTokenStillConsumes(int nameLength)
    {
        var user = Name(nameLength);
        var nonce = HandoffTokens.Mint(user, Ip);
        Assert.Equal(5, nonce.Length);   // the wire slot is always 5 bytes, however much survives

        Assert.True(HandoffTokens.Consume(ClientEcho(user, nonce), user, Ip),
            $"a {nameLength}-character name must survive the client's truncation");
    }

    [Fact]
    public void TokenIsSingleUse()
    {
        var user = Name(8);
        var nonce = HandoffTokens.Mint(user, Ip);
        var echo = ClientEcho(user, nonce);

        Assert.True(HandoffTokens.Consume(echo, user, Ip));
        Assert.False(HandoffTokens.Consume(echo, user, Ip));   // replayed 0x10
    }

    [Fact]
    public void TokenIsBoundToTheLoginAddress()
    {
        var user = Name(8);
        var echo = ClientEcho(user, HandoffTokens.Mint(user, Ip));
        Assert.False(HandoffTokens.Consume(echo, user, "198.51.100.9"));
    }

    [Fact]
    public void TokenIsBoundToTheUsername()
    {
        var user = Name(8);
        var other = Name(8);
        var echo = ClientEcho(user, HandoffTokens.Mint(user, Ip));
        Assert.False(HandoffTokens.Consume(echo, other, Ip));
    }

    /// <summary>A wrong nonce must still fail — the truncation fix must not have degraded into accepting
    /// anything of the right length.</summary>
    [Fact]
    public void WrongNonceIsRejected()
    {
        var user = Name(8);
        HandoffTokens.Mint(user, Ip);
        Assert.False(HandoffTokens.Consume(new byte[] { 1, 2, 3, 0 }, user, Ip));
    }

    /// <summary>The truncation budget the two sides derive independently. If this table ever changes, the
    /// login and game servers must change together or every login breaks.</summary>
    [Theory]
    [InlineData(3, 4)]
    [InlineData(7, 4)]
    [InlineData(8, 3)]
    [InlineData(9, 2)]
    [InlineData(10, 1)]
    [InlineData(11, 0)]
    [InlineData(12, 0)]
    public void SurvivingBytesMatchesTheClientField(int nameLength, int expected) =>
        Assert.Equal(expected, HandoffTokens.SurvivingBytes(new string('x', nameLength)));

    /// <summary>#299: the name cap is the client's field, not a number of its own. The field's usable bytes
    /// carry the length byte and then the name, so the longest name that arrives whole is one less than
    /// the field, and at that length nothing of the nonce is left. At 12 letters the name itself was cut,
    /// and the account was created but could never enter the world.</summary>
    [Fact]
    public void MaxNameLengthIsTheClientFieldLessItsLengthByte()
    {
        Assert.Equal(ClientField - 1, HandoffTokens.HandoffFieldBytes);
        Assert.Equal(ClientField - 1, 1 + HandoffTokens.MaxNameLength);
        Assert.Equal(0, HandoffTokens.SurvivingBytes(new string('x', HandoffTokens.MaxNameLength)));
    }

    /// <summary>The longest name creation allows still enters the world: no nonce byte survives, so its token
    /// consumes on the username and the address binding alone.</summary>
    [Fact]
    public void LongestAllowedNameStillConsumes()
    {
        var user = Name(HandoffTokens.MaxNameLength);
        var nonce = HandoffTokens.Mint(user, Ip);
        Assert.True(HandoffTokens.Consume(ClientEcho(user, nonce), user, Ip),
            $"a {HandoffTokens.MaxNameLength}-character name must survive the client's truncation");
    }


    // ---- the table's key -------------------------------------------------------------------------------
    // Every fact below runs on a database file of its own (IsolatedDatabase, or a file it builds by hand),
    // with an explicit clock, so what it counts and what it collides with is only what it wrote.

    /// <summary>A fixed clock for the isolated facts, in unix seconds.</summary>
    private const long T0 = 1_800_000_000;

    private const string OtherIp = "198.51.100.9";   // TEST-NET-2

    /// <summary>A throwaway username of exactly <paramref name="length"/> characters for a fact on its own
    /// database file: nothing to clean up in the process database, so it is not recorded for Dispose.</summary>
    private static string Fresh(int length) => ("zz" + Guid.NewGuid().ToString("N"))[..length];

    private static byte[] MintAt(Func<SqliteConnection> open, string user, long now, string ip = Ip) =>
        ClientEcho(user, HandoffTokens.Mint(user, ip, open, now));

    private static bool ConsumeAt(Func<SqliteConnection> open, byte[] echo, string user, long now, string ip = Ip) =>
        HandoffTokens.Consume(echo, user, ip, open, now);

    /// <summary>One login as the two servers perform it: the mint the login server makes, the client's
    /// truncating echo, and the consume the game server makes on arrival.</summary>
    private static bool Login(Func<SqliteConnection> open, string user, long now, string ip = Ip) =>
        ConsumeAt(open, MintAt(open, user, now, ip), user, now, ip);

    private static long Scalar(Func<SqliteConnection> open, string sql)
    {
        using var cn = open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long Rows(Func<SqliteConnection> open) => Scalar(open, "SELECT COUNT(*) FROM handoff_tokens;");

    private static void Exec(Func<SqliteConnection> open, string sql)
    {
        using var cn = open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // -- the reproduction: red before this change, one fact per case the defect names --

    [Fact]
    public void ElevenLetterAccountEntersEveryTime()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(11);
        for (int i = 1; i <= 2; i++)
            Assert.True(Login(db.Open, user, T0 + i), $"login {i} of one 11-letter account was refused");
    }

    [Fact]
    public void TwoElevenLetterAccountsBothEnter()
    {
        using var db = new IsolatedDatabase();
        Assert.True(Login(db.Open, Fresh(11), T0), "the first 11-letter account was refused");
        Assert.True(Login(db.Open, Fresh(11), T0 + 1), "the second 11-letter account was refused");
    }

    [Fact]
    public void TenLetterAccountEntersThreeHundredTimes()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(10);
        for (int i = 1; i <= 300; i++)
            Assert.True(Login(db.Open, user, T0 + i), $"login {i} of one 10-letter account was refused");
    }

    // -- one guard each --

    /// <summary>The key. Two 11-letter logins in flight at once — both minted before either arrives — hash
    /// to the same value (no nonce byte survives), so only a key that includes the account keeps them
    /// apart.</summary>
    [Fact]
    public void TwoElevenLetterAccountsInFlightTogetherBothEnter()
    {
        using var db = new IsolatedDatabase();
        var first = Fresh(11);
        var second = Fresh(11);
        var firstEcho = MintAt(db.Open, first, T0);
        var secondEcho = MintAt(db.Open, second, T0);
        Assert.True(ConsumeAt(db.Open, firstEcho, first, T0), "the first account's token was lost to the second's");
        Assert.True(ConsumeAt(db.Open, secondEcho, second, T0), "the second account's token was never recorded");
    }

    /// <summary>A user's own live row must not block a new mint: the first login's client never arrived, and
    /// the player logs in again from another address inside the TTL. For an 11-letter name the two tokens
    /// share one key, so the second has to replace the first.</summary>
    [Fact]
    public void RetryFromAnotherAddressBeforeTheFirstLoginArrivesEnters()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(11);
        MintAt(db.Open, user, T0);
        var retry = MintAt(db.Open, user, T0 + 1, OtherIp);
        Assert.True(ConsumeAt(db.Open, retry, user, T0 + 1, OtherIp),
            "the retry's token was blocked by the first login's unconsumed row");
    }

    /// <summary>The purge, for spent tokens: a thousand logins by a thousand accounts of every allowed length,
    /// and the table never holds more than the one row the latest login left behind.</summary>
    [Fact]
    public void SpentTokensDoNotAccumulateAcrossAThousandLogins()
    {
        using var db = new IsolatedDatabase();
        long most = 0;
        for (int i = 0; i < 1000; i++)
        {
            int length = 3 + i % (HandoffTokens.MaxNameLength - 2);   // 3..11
            Assert.True(Login(db.Open, Fresh(length), T0 + i), $"login {i + 1} ({length} letters) was refused");
            most = Math.Max(most, Rows(db.Open));
        }
        Assert.Equal(1, most);
    }

    /// <summary>The purge, for abandoned tokens: a thousand logins whose clients never arrive, one a second.
    /// A row lives out its 60-second TTL and is gone at the next mint after it, so the table holds at most the
    /// logins of the last minute.</summary>
    [Fact]
    public void ExpiredTokensDoNotAccumulateAcrossAThousandLogins()
    {
        using var db = new IsolatedDatabase();
        long most = 0;
        for (int i = 0; i < 1000; i++)
        {
            MintAt(db.Open, Fresh(7), T0 + i);
            most = Math.Max(most, Rows(db.Open));
        }
        Assert.Equal(60, most);
    }

    /// <summary>A mint that cannot record its token says so, at Warn, with the database's reason, and the
    /// login it belongs to is refused at the door rather than let in unchecked.</summary>
    [Fact]
    public void AMintThatCannotRecordItsTokenWarnsWithTheReason()
    {
        using var db = new IsolatedDatabase();
        Exec(db.Open, "CREATE TRIGGER refuse_mint BEFORE INSERT ON handoff_tokens " +
                      "BEGIN SELECT RAISE(ABORT, 'test trigger refuses this insert'); END;");
        var user = Fresh(9);
        byte[] echo;
        LogLineSink.Entry warning;
        using (var sink = LogLineSink.Acquire())
        {
            echo = MintAt(db.Open, user, T0);
            warning = sink.EntryContaining($"handoff token for '{user}' could not be recorded");
        }
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Contains("test trigger refuses this insert", warning.Line);
        Assert.False(ConsumeAt(db.Open, echo, user, T0));
    }

    /// <summary>The purge and the insert are independent: a purge the database refuses is logged at Warn and
    /// the login still enters.</summary>
    [Fact]
    public void APurgeThatFailsWarnsAndTheLoginStillEnters()
    {
        using var db = new IsolatedDatabase();
        Assert.True(Login(db.Open, Fresh(8), T0));   // leaves a consumed row for the next purge to meet
        Exec(db.Open, "CREATE TRIGGER refuse_purge BEFORE DELETE ON handoff_tokens " +
                      "BEGIN SELECT RAISE(ABORT, 'test trigger refuses this delete'); END;");
        var user = Fresh(8);
        bool entered;
        LogLineSink.Entry warning;
        using (var sink = LogLineSink.Acquire())
        {
            entered = Login(db.Open, user, T0 + 1);
            warning = sink.EntryContaining("handoff token purge failed");
        }
        Assert.True(entered, "a failed purge stopped the mint");
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Contains("test trigger refuses this delete", warning.Line);
    }

    // -- the security properties, where the key change could have touched them --

    /// <summary>Two 11-letter names carry no nonce byte at all, so only the username and the address bind a
    /// token. A's token presented as B is refused both when B has no token and when B's token is bound to
    /// another address, and consuming A's leaves B's alone.</summary>
    [Fact]
    public void ElevenLetterTokenIsBoundToItsAccountAndAddress()
    {
        using var db = new IsolatedDatabase();
        var a = Fresh(11);
        var b = Fresh(11);
        var echoA = MintAt(db.Open, a, T0);
        Assert.False(ConsumeAt(db.Open, echoA, b, T0), "A's token entered as B, who has no token");
        var echoB = MintAt(db.Open, b, T0, OtherIp);
        Assert.False(ConsumeAt(db.Open, echoA, b, T0), "A's token entered as B, whose token is bound to another address");
        Assert.True(ConsumeAt(db.Open, echoA, a, T0), "A's own token was refused after B minted");
        Assert.True(ConsumeAt(db.Open, echoB, b, T0, OtherIp), "consuming A's token consumed B's");
        Assert.False(ConsumeAt(db.Open, echoA, a, T0), "A's arrival replayed");
    }

    /// <summary>The TTL is 60 seconds from the mint: stored as such, accepted at 59, refused at 60.</summary>
    [Fact]
    public void TokenExpiresSixtySecondsAfterItsMint()
    {
        using var db = new IsolatedDatabase();
        var user = Fresh(7);
        var late = MintAt(db.Open, user, T0);
        Assert.Equal(T0 + 60, Scalar(db.Open, "SELECT expires_utc FROM handoff_tokens;"));
        Assert.False(ConsumeAt(db.Open, late, user, T0 + 60), "a token was accepted at its expiry");

        var other = Fresh(7);
        var inTime = MintAt(db.Open, other, T0);
        Assert.True(ConsumeAt(db.Open, inTime, other, T0 + 59), "a token was refused a second before its expiry");
    }

    // -- the deploy window: a login server and a game server on either side of this change --

    // The two statements of Shared/HandoffTokens.cs as they were before this change (upstream/master
    // 9c00b98583495bf53684397f48beda773fdbd3ff, Mint and Consume), and the hash they used, computed here
    // rather than borrowed, so a change to the stored hash has something independent to disagree with.
    private const string PreviousMintSql = @"INSERT INTO handoff_tokens(nonce_hash, username, expires_utc, consumed, ip)
                                VALUES($h, $u, $e, 0, $ip);";
    private const string PreviousConsumeSql = @"UPDATE handoff_tokens SET consumed=1
                                WHERE nonce_hash=$h AND username=$u AND ip=$ip
                                  AND consumed=0 AND expires_utc>$now;";

    private static string PreviousHash(string user, byte[] echo) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(echo[..HandoffTokens.SurvivingBytes(user)]));

    /// <summary>A nonce as the previous login server minted one: four non-zero bytes and the terminator.</summary>
    private static readonly byte[] PreviousNonce = { 0x5A, 0x6B, 0x7C, 0x8D, 0 };

    private static int PreviousMint(Func<SqliteConnection> open, string user, byte[] echo, long expires)
    {
        using var cn = open();
        return PreviousMint(cn, user, echo, expires);
    }

    private static int PreviousMint(SqliteConnection cn, string user, byte[] echo, long expires)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = PreviousMintSql;
        cmd.Parameters.AddWithValue("$h", PreviousHash(user, echo));
        cmd.Parameters.AddWithValue("$u", Auth.Key(user));
        cmd.Parameters.AddWithValue("$e", expires);
        cmd.Parameters.AddWithValue("$ip", Ip);
        return cmd.ExecuteNonQuery();
    }

    private static int PreviousConsume(Func<SqliteConnection> open, string user, byte[] echo, long now)
    {
        using var cn = open();
        return PreviousConsume(cn, user, echo, now);
    }

    private static int PreviousConsume(SqliteConnection cn, string user, byte[] echo, long now)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = PreviousConsumeSql;
        cmd.Parameters.AddWithValue("$h", PreviousHash(user, echo));
        cmd.Parameters.AddWithValue("$u", Auth.Key(user));
        cmd.Parameters.AddWithValue("$ip", Ip);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>New login server, old game server, and the reverse. The old game server consumes the tokens
    /// this code mints, this code consumes the tokens the old login server mints, and the old login server's
    /// insert for an 11-letter account no longer meets another account's row.</summary>
    [Fact]
    public void TheServerBeforeThisChangeAndThisOneConsumeEachOthersTokens()
    {
        using var db = new IsolatedDatabase();
        foreach (int length in new[] { 7, 10, 11 })
        {
            var user = Fresh(length);   // this login server mints, the previous game server consumes
            Assert.True(PreviousConsume(db.Open, user, MintAt(db.Open, user, T0), T0) == 1,
                $"the previous game server refused a {length}-letter token this login server minted");

            var other = Fresh(length);  // the previous login server mints, this game server consumes
            var echo = ClientEcho(other, PreviousNonce);
            Assert.Equal(1, PreviousMint(db.Open, other, echo, expires: T0 + 60));
            Assert.True(ConsumeAt(db.Open, echo, other, T0), $"this game server refused a {length}-letter token the previous login server minted");
        }

        // The previous login server mints for an 11-letter account while this code's token for another
        // 11-letter account is in flight: the same hash, which was the same key.
        var ours = Fresh(11);
        var ourEcho = MintAt(db.Open, ours, T0 + 1);
        var theirs = Fresh(11);
        var theirEcho = ClientEcho(theirs, PreviousNonce);
        Assert.Equal(1, PreviousMint(db.Open, theirs, theirEcho, expires: T0 + 61));
        Assert.True(ConsumeAt(db.Open, theirEcho, theirs, T0 + 1), "the previous login server's in-flight token was refused");
        Assert.True(ConsumeAt(db.Open, ourEcho, ours, T0 + 1), "this login server's in-flight token was refused");
    }

    /// <summary>Migration 4 on the database a deployment has today: the old key, with a live token, a
    /// consumed one and an expired one in it. The live one survives and still enters, the spent ones are
    /// gone, the key is the new one, and a second run changes nothing. A connection opened before the
    /// migration — the server that has not restarted yet — runs the previous code's statements on the new
    /// table.</summary>
    [Fact]
    public void RekeyMigrationKeepsLiveTokensAndDropsSpentOnes()
    {
        string dir = System.IO.Path.Combine(TestProcessState.StateDirectory, $"rekey-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "project1998.db");
        Func<SqliteConnection> open = () => Db.Open(path);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // the migration reads SQLite's own clock

        var live = Fresh(11);
        var spent = Fresh(11);
        var expired = Fresh(10);
        var liveEcho = ClientEcho(live, PreviousNonce);
        using (var cn = new SqliteConnection($"Data Source={path}"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            // handoff_tokens exactly as upstream/master 9c00b98 declares it, at that schema's version (3).
            cmd.CommandText = @"
CREATE TABLE handoff_tokens (
  nonce_hash  TEXT PRIMARY KEY,
  username    TEXT NOT NULL,
  expires_utc INTEGER NOT NULL,
  consumed    INTEGER NOT NULL DEFAULT 0,
  ip          TEXT NOT NULL DEFAULT ''
);
PRAGMA journal_mode = WAL;
PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(1, PreviousMint(open, live, liveEcho, expires: now + 60));
        Exec(open, $"INSERT INTO handoff_tokens VALUES('{new string('A', 64)}', '{spent}', {now + 60}, 1, '{Ip}');");
        Exec(open, $"INSERT INTO handoff_tokens VALUES('{new string('B', 64)}', '{expired}', {now - 1}, 0, '{Ip}');");
        Assert.False(PrimaryKeyIsUserThenHash(open), "the legacy table already had the new key");
        using var running = open();   // the process that has not restarted: its connection predates the rebuild

        Db.InitializeDatabase(path);

        Assert.True(PrimaryKeyIsUserThenHash(open), "migration 4 did not rekey the table");
        Assert.Equal(Db.CurrentSchemaVersion, Scalar(open, "PRAGMA user_version;"));
        Assert.Equal(1, Rows(open));
        var another = Fresh(11);   // the same hash as the live row, another account: collided before
        var anotherEcho = MintAt(open, another, now);
        var third = Fresh(11);     // the previous code's insert, on the running connection, for the same hash
        var thirdEcho = ClientEcho(third, PreviousNonce);
        Assert.Equal(1, PreviousMint(running, third, thirdEcho, expires: now + 60));
        Assert.True(ConsumeAt(open, liveEcho, live, now), "a token minted before the migration was lost");
        Assert.True(ConsumeAt(open, anotherEcho, another, now), "a mint after the migration was refused");
        Assert.Equal(1, PreviousConsume(running, third, thirdEcho, now));

        Db.InitializeDatabase(path);
        Assert.True(PrimaryKeyIsUserThenHash(open), "a second run changed the key");
        Assert.Equal(Db.CurrentSchemaVersion, Scalar(open, "PRAGMA user_version;"));
    }

    private static bool PrimaryKeyIsUserThenHash(Func<SqliteConnection> open)
    {
        using var cn = open();
        return Db.PrimaryKeyIs(cn, null, "handoff_tokens", "username", "nonce_hash");
    }
}
