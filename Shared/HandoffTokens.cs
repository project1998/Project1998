using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Shared;

/// <summary>
/// The login→game handoff token. When login succeeds the login server MINTS a random 5-byte nonce (the
/// exact size of the 4.95 client's handoff-token slot, verified by wire probe — the client echoes those 5
/// bytes verbatim in its 0x10 arrival) and records it in the shared DB. The game server CONSUMES it on
/// arrival: the token must exist, be unexpired, be unconsumed, and be bound to the same username the
/// client claims. This closes the impersonation hole where the game port trusted whatever username the
/// client sent.
///
/// Security model: the nonce is a server-minted secret (not derivable by the client), single-use, and
/// short-lived (TTL seconds), and it is bound to the SOURCE ADDRESS the login came from. Only the
/// SHA-256 of the significant bytes is stored, so a DB leak doesn't reveal live tokens.
///
/// THE TRUNCATION RULE (this is the whole reason this class is more than a hash lookup). The 4.95 client
/// does not keep the token in a slot of its own: it copies the TAIL of our 0x03 handoff reply —
/// <c>&lt;ulen&gt;&lt;username&gt;&lt;nonce&gt;</c> — into one fixed 13-byte NUL-terminated field, i.e. 12
/// bytes plus a forced terminator, and echoes that field back verbatim in its 0x10 arrival (which is why
/// every 0x10 body is exactly 23 bytes regardless of name length). So the username and the nonce SHARE
/// one budget, and the number of nonce bytes that survive is <c>11 - username.Length</c>:
///
///   7-char name -> 4 bytes survive (32 bits)   ..the case the original "the client keeps 4 and zeroes
///   8-char name -> 3 bytes survive (24 bits)     the 5th" note was probed on, and mistook for a fixed slot
///   9-char name -> 2 bytes                     10-char name -> 1 byte    11 -> nothing at all
///  12-char name -> the NAME is cut: the field holds the length byte and only its first 11 letters
///
/// Validating a fixed 4 bytes therefore rejected every name longer than 7 characters outright — a
/// freshly created 8-character account could log in but never enter the world. Both sides now derive the
/// surviving length from the username, so they agree by construction.
///
/// The same field caps the name itself. At 11 letters the length byte and the name fill all 12 bytes and
/// nothing of the nonce is left; at 12 the name loses its last letter, so the 0x10 arrival claims a
/// username the token was never minted for and <see cref="Consume(byte[], string, string)"/> refuses it. Such an account is
/// created and then can never enter the world (#299), which is why account creation refuses any name
/// longer than <see cref="MaxNameLength"/>.
///
/// Because that leaves as little as one byte (or none), the nonce alone is NOT the security boundary for
/// long names — the address binding is. An attacker must both be at the login's source address and guess
/// whatever entropy survived, inside the TTL, once.
///
/// THE KEY. The same shortage is why a row is keyed by the account as well as the hash (Shared/Db.cs,
/// <c>handoff_tokens</c>). Keyed by the hash alone, every 11-letter account shared one key (the hash of no
/// bytes) and every 10-letter account 255, and nothing deleted a row, so the second 11-letter login ever
/// was refused at the game door, 10-letter logins began to be refused within a few dozen, and after 255 all
/// of them were. Every mint now deletes the rows no consume can accept any more, and a token only ever
/// meets its own account's rows.
///
/// The stored hash is still the SHA-256 of the surviving bytes and nothing else, on purpose. Deploys
/// restart the login and game servers minutes apart, and in between one runs this code and the other the
/// code before it; with the hash unchanged each side consumes the tokens the other mints.
/// </summary>
public static class HandoffTokens
{
    private const int TtlSeconds = 60;   // generous: the client opens the game port within ~1s of the 0x03 reply
    private const int SigBytes = 4;      // nonce bytes we mint; how many SURVIVE depends on the name (see above)

    /// <summary>The usable bytes of the client's handoff field: the 13-byte field of the truncation rule
    /// above, less the NUL terminator the client forces. The length byte, the username and whatever of
    /// the nonce survives all share these bytes.</summary>
    public const int HandoffFieldBytes = 12;

    /// <summary>The longest name that reaches the game server whole: the field less its length byte. One
    /// letter more and the arrival carries a cut name that <see cref="Consume(byte[], string, string)"/> can never match, so
    /// account creation (<see cref="NameRules"/>) refuses anything longer. Nothing of the nonce survives
    /// at this length; the address binding carries the check (see above).</summary>
    public const int MaxNameLength = HandoffFieldBytes - 1;

    /// <summary>How many nonce bytes the client will still be carrying when it re-sends them in its 0x10
    /// arrival, given the username it shares the field with. Both minting and consuming key off this, so
    /// the two sides can never disagree about which prefix is being compared.</summary>
    public static int SurvivingBytes(string username) =>
        Math.Clamp(11 - (username ?? "").Length, 0, SigBytes);

    /// <summary>Mint a fresh nonce for <paramref name="username"/> arriving from <paramref name="ip"/>,
    /// store the hash of the prefix that will survive the client's truncation, and return the raw bytes
    /// for the 0x03 handoff reply's 5-byte token slot. The significant bytes are kept non-zero so the
    /// client's NUL-terminated copy can't end early; the trailing 5th byte is the terminator.</summary>
    public static byte[] Mint(string username, string ip) =>
        Mint(username, ip, Db.Open, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// <see cref="Mint(string, string)"/> against a connection source and a clock the caller names: the
    /// public overload passes the shared database and the current time, and the tests pass a database file
    /// of their own and a fixed clock.
    ///
    /// <para>Two statements, each a single autocommit statement taking SQLite's one write lock the way every
    /// write from either process does; neither depends on the other's result, so no transaction spans them.
    /// First the purge: it deletes only rows <see cref="Consume(byte[], string, string)"/> refuses already
    /// (consumed, or expired at <paramref name="now"/>), so it cannot take a live token from a login in flight
    /// in either process, and the table holds no more than the tokens minted within the TTL. Then the
    /// insert. The key is <c>(username, nonce_hash)</c> (Shared/Db.cs), so it can only meet a row of this
    /// same account with the same surviving bytes; for an 11-letter name that is every earlier token of the
    /// account. Such a row is either spent (the purge has just removed it) or a live token from a login of
    /// this account that has not arrived yet, which the new token replaces: the two are identical to
    /// <see cref="Consume(byte[], string, string)"/> apart from the address and the expiry, and the newer
    /// login's are the ones to keep.</para>
    ///
    /// <para>A failure of either statement is logged at Warn with its exception. The insert's is the one that
    /// matters: the reply still goes out (the caller sends it regardless), and that login's arrival will be
    /// refused at the game door.</para>
    /// </summary>
    internal static byte[] Mint(string username, string ip, Func<SqliteConnection> open, long now)
    {
        var sig = new byte[SigBytes];
        RandomNumberGenerator.Fill(sig);
        for (int i = 0; i < SigBytes; i++) if (sig[i] == 0) sig[i] = 1;   // keep all significant bytes non-zero
        string user = Auth.Key(username);
        try
        {
            using var cn = open();
            Purge(cn, now);
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"INSERT INTO handoff_tokens(nonce_hash, username, expires_utc, consumed, ip)
                                VALUES($h, $u, $e, 0, $ip)
                                ON CONFLICT(username, nonce_hash) DO UPDATE
                                  SET expires_utc=excluded.expires_utc, consumed=0, ip=excluded.ip;";
            cmd.Parameters.AddWithValue("$h", HashHex(sig[..SurvivingBytes(username)]));
            cmd.Parameters.AddWithValue("$u", user);
            cmd.Parameters.AddWithValue("$e", now + TtlSeconds);
            cmd.Parameters.AddWithValue("$ip", ip ?? "");
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Log.Warn($"handoff token for '{user}' could not be recorded — that login's arrival will be refused", e);
        }
        return new byte[] { sig[0], sig[1], sig[2], sig[3], 0 };   // 5-byte wire token; the client keeps a prefix
    }

    /// <summary>Delete the rows no consume can accept any more: consumed, or expired at
    /// <paramref name="now"/>. Run by every mint, so spent rows never accumulate. A failure is logged and
    /// does not stop the mint: the insert that follows does not need it.</summary>
    private static void Purge(SqliteConnection cn, long now)
    {
        try
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "DELETE FROM handoff_tokens WHERE consumed=1 OR expires_utc<=$now;";
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Log.Warn("handoff token purge failed — spent tokens stay in the table until a later mint's purge", e);
        }
    }

    /// <summary>Validate + single-use-consume a token for the claimed username and source address. Compares
    /// exactly the prefix the client was able to carry (<see cref="SurvivingBytes"/>) — a caller cannot
    /// shorten the comparison by claiming a longer name, because the length is derived from the SAME name
    /// the row is keyed on. Returns true only if the row exists, matches username + address, is unexpired,
    /// and was not already consumed: one atomic UPDATE, so a replayed 0x10 fails the second time.</summary>
    public static bool Consume(byte[] token, string expectedUser, string ip) =>
        Consume(token, expectedUser, ip, Db.Open, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary><see cref="Consume(byte[], string, string)"/> against a connection source and a clock the
    /// caller names (see the <see cref="Mint(string, string, Func{SqliteConnection}, long)"/> overload). One
    /// autocommit UPDATE: SQLite runs it under the write lock both processes share, so of two arrivals
    /// presenting the same token only one can change the row.</summary>
    internal static bool Consume(byte[] token, string expectedUser, string ip, Func<SqliteConnection> open, long now)
    {
        int need = SurvivingBytes(expectedUser);
        if (token is null || token.Length < need) return false;
        var sig = token[..need];
        try
        {
            using var cn = open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = @"UPDATE handoff_tokens SET consumed=1
                                WHERE nonce_hash=$h AND username=$u AND ip=$ip
                                  AND consumed=0 AND expires_utc>$now;";
            cmd.Parameters.AddWithValue("$h", HashHex(sig));
            cmd.Parameters.AddWithValue("$u", Auth.Key(expectedUser));
            cmd.Parameters.AddWithValue("$ip", ip ?? "");
            cmd.Parameters.AddWithValue("$now", now);
            return cmd.ExecuteNonQuery() == 1;   // exactly one row updated -> valid & now consumed
        }
        catch { return false; }
    }

    private static string HashHex(byte[] sig) => Convert.ToHexString(SHA256.HashData(sig));
}
