using System.Reflection;
using System.Text;
using Server;
using Shared;

namespace Tests.Support;

/// <summary>
/// The pieces the spell-dispatch facts share: the 0x0F cast frame a client sends, the mini-text lines a cast
/// answered with, and what a cast leaves behind that <see cref="Session"/> keeps private (the enchant and rage
/// multipliers, a spell's aether, and the timed-buff entry a self-buff runs on).
///
/// <para>Reflection into Session's non-public state is the pattern <c>EnchantSwapOrderTests</c> and
/// <c>FuryRelogDrainTests</c> already use; every read runs under the session's state monitor, the lock the cast
/// itself wrote them under.</para>
/// </summary>
internal static class SpellCastSupport
{
    private static readonly FieldInfo EnchantField =
        typeof(Session).GetField("_enchantAmount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo AetherField =
        typeof(Session).GetField("_aether", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageAmountField =
        typeof(Session).GetField("_rageAmount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RageUntilField =
        typeof(Session).GetField("_rageUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo BuffsField =
        typeof(Session).GetField("_buffs", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishBuffTotals =
        typeof(Session).GetMethod("PublishBuffTotals", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The 0x0F cast frame for 0-based book <paramref name="slot"/> (RTK clif_parsemagic,
    /// <c>Session.HandleCast</c>): the slot byte (slot + 1), then, for a "Which target? &gt;" spell, the target's
    /// entity id as a u32 big-endian. With no target the client sends the slot alone.</summary>
    public static byte[] CastFrame(int slot, uint? targetId = null)
    {
        var body = new List<byte> { (byte)(slot + 1) };
        if (targetId is uint id)
            body.AddRange(new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id });
        return SessionFixture.Frame(ClientOp.Cast, body.ToArray());
    }

    /// <summary>Every 0x0A mini-text line the recorder was sent. The body is
    /// <c>type(u8) | len(u16 BE) | ascii[len]</c> (<c>Session.SendMiniText</c>).</summary>
    public static List<string> MiniTexts(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.MiniText)
                .Where(b => b.Length >= 3)
                .Select(b => Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3)))
                .ToList();

    /// <summary>The armed enchant multiplier (1 = none), which <c>Session.PlayerSwingDamage</c> applies to the
    /// raw weapon term.</summary>
    public static double Enchant(Session session)
    {
        double value = 0;
        session.WithState(() => value = (double)EnchantField.GetValue(session)!);
        return value;
    }

    /// <summary>Milliseconds left on the aether (RTK's per-spell cooldown) armed under
    /// <paramref name="key"/>, or 0 when none is running. <c>OnCooldown</c>, the gate a recast meets, reads the
    /// same dictionary.</summary>
    public static long AetherLeft(Session session, string key)
    {
        long left = 0;
        session.WithState(() =>
        {
            var aether = (Dictionary<string, long>)AetherField.GetValue(session)!;
            if (aether.TryGetValue(key, out long until)) left = Math.Max(0, until - Environment.TickCount64);
        });
        return left;
    }

    /// <summary>The armed rage multiplier and the milliseconds left on it, as <c>EffRage</c> reads them: a lapsed
    /// or unarmed fury is (1, 0), RTK's baseline. <c>Session.PlayerSwingDamage</c> multiplies the whole swing by
    /// the amount.</summary>
    public static (int Amount, long LeftMs) Rage(Session session)
    {
        int amount = 1;
        long left = 0;
        session.WithState(() =>
        {
            left = Math.Max(0, (long)RageUntilField.GetValue(session)! - Environment.TickCount64);
            if (left > 0) amount = (int)RageAmountField.GetValue(session)!;
        });
        return (amount, left);
    }

    /// <summary>The running <c>_buffs</c> entry a spell put there under its key: the milliseconds left, its
    /// exclusivity slot, and the name the buff box shows. Null when there is none. A slot-only buff (no stat) is
    /// one entry, which is what <c>Session.ReceiveTimedBuff</c> adds for it.</summary>
    public static (long LeftMs, string Category, string Name)? BuffEntry(Session session, string key)
    {
        (long, string, string)? found = null;
        session.WithState(() =>
        {
            foreach (var entry in (System.Collections.IEnumerable)BuffsField.GetValue(session)!)
            {
                if ((string)Field(entry, "Key") != key) continue;
                long left = (long)Field(entry, "Expires") - Environment.TickCount64;
                if (left > 0) found = (left, (string)Field(entry, "Category"), (string)Field(entry, "Name"));
            }
        });
        return found;
    }

    /// <summary>Move the deadline of every <c>_buffs</c> entry under <paramref name="key"/> one millisecond into the
    /// past, which is where the clock would put it at the end of its run, so the next beat's
    /// <c>ExpireBuffs</c> drops it. The entry is changed in place, under the session's monitor, without the list
    /// writers: their hints stay where the cast left them, as they would until that beat.
    /// <para>The published copy of the buffs (the totals snapshot, which another player's slot checks and the totals
    /// read) is republished from the list, so it says what the list says, as a real clock moving past the deadline
    /// would leave both. Production never changes an entry in place; only this seam does, so only this seam has to
    /// keep the copy in step.</para></summary>
    public static void EndBuff(Session session, string key)
    {
        session.WithState(() =>
        {
            foreach (var entry in (System.Collections.IEnumerable)BuffsField.GetValue(session)!)
                if ((string)Field(entry, "Key") == key)
                    entry.GetType().GetField("Expires")!.SetValue(entry, Environment.TickCount64 - 1);
            PublishBuffTotals.Invoke(session, null);
        });
    }

    /// <summary>Drop the aether running under <paramref name="key"/>, as if it had run out. For a fact that has to
    /// cast the same spell again and again faster than its aether allows.</summary>
    public static void ClearAether(Session session, string key) =>
        session.WithState(() => ((Dictionary<string, long>)AetherField.GetValue(session)!).Remove(key));

    /// <summary>Milliseconds until Second Sight's next scan may run (<c>Session._secondSightNextScan</c>), or -1
    /// where the field does not exist (a build without the scan).</summary>
    public static long SecondSightNextScanIn(Session session)
    {
        var field = typeof(Session).GetField("_secondSightNextScan", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) return -1;
        long next = 0;
        session.WithState(() => next = (long)field.GetValue(session)!);
        return next - Environment.TickCount64;
    }

    private static object Field(object entry, string name) => entry.GetType().GetField(name)!.GetValue(entry)!;
}
