using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// AMQP-path receipt handle. SQS <c>DeleteMessage</c> /
/// <c>ChangeMessageVisibility</c> arrive as stateless HTTP requests
/// carrying only this opaque string; the AMQP path's
/// <see cref="Aws2Azure.Amqp.ServiceBus.ServiceBusReceiver"/> needs the
/// Service Bus <b>lock-token GUID</b> (the 16-byte AMQP delivery-tag)
/// to settle the in-flight delivery, plus the <b>queue name</b> to look
/// up the right receiver in the connection pool. FIFO/session-bound
/// receives additionally carry the bound <b>session-id</b> so settle
/// requests can route back to the same session receiver via the pool's
/// <see cref="Aws2Azure.Amqp.ServiceBus.ServiceBusAmqpPool.GetSessionReceiverAsync"/>.
///
/// <para>Two on-the-wire formats are supported:</para>
/// <list type="bullet">
///   <item><c>v2</c> (non-session, base64 prefix <c>"Mjo"</c>):
///   <c>"2:" | queueName | lockTokenGuid("D") | lockedUntilUtc(ISO-8601, optional)</c></item>
///   <item><c>v3</c> (session, base64 prefix <c>"Mzo"</c>):
///   <c>"3:" | queueName | lockTokenGuid("D") | lockedUntilUtc(ISO-8601, optional) | sessionId</c></item>
/// </list>
/// <para>The version bump (rather than appending an optional field
/// to v2) lets <see cref="LooksLikeAmqpHandle"/> stay a tight 3-char
/// prefix match and lets the decoder fail-fast on malformed payloads
/// without having to guess whether a missing trailing field means
/// "no session" or "truncated".</para>
///
/// <para>Both versions intentionally differ from the REST-path
/// <see cref="ReceiptHandle"/> format (<c>"1"</c>, base64 prefix
/// <c>"MTo"</c>) so the dispatcher can route a delete request back
/// to the right transport when a queue's transport setting flips
/// between REST and AMQP.</para>
///
/// <para>Length-prefixing (rather than separator-delimited) protects
/// against queue names, session-ids, or future fields containing
/// arbitrary user bytes — the field boundary cannot shift on decode.</para>
/// </summary>
internal static class AmqpReceiptHandle
{
    private const string VersionV2 = "2";
    private const string VersionV3 = "3";

    /// <summary>
    /// Encodes a non-session (v2) handle. Used by the non-FIFO AMQP
    /// receive path.
    /// </summary>
    public static string Encode(string queueName, Guid lockToken, DateTimeOffset lockedUntilUtc)
        => EncodeCore(queueName, lockToken, lockedUntilUtc, sessionId: null);

    /// <summary>
    /// Encodes a session-bound (v3) handle when <paramref name="sessionId"/>
    /// is non-null/empty, otherwise falls through to the v2 encoder.
    /// Used by the FIFO AMQP receive path (slice 7c.3c).
    /// </summary>
    public static string Encode(string queueName, Guid lockToken, DateTimeOffset lockedUntilUtc, string? sessionId)
        => EncodeCore(queueName, lockToken, lockedUntilUtc, sessionId);

    private static string EncodeCore(string queueName, Guid lockToken, DateTimeOffset lockedUntilUtc, string? sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        var when = lockedUntilUtc == default
            ? string.Empty
            : lockedUntilUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        var withSession = !string.IsNullOrEmpty(sessionId);
        var version = withSession ? VersionV3 : VersionV2;

        // Version marker is emitted raw (not length-prefixed) so the
        // base64 representation starts with a stable 3-char prefix
        // ("Mjo" for v2, "Mzo" for v3, distinct from the REST v1
        // prefix "MTo"). LooksLikeAmqpHandle matches both AMQP prefixes.
        var sb = new StringBuilder();
        sb.Append(version).Append(':');
        AppendField(sb, queueName);
        AppendField(sb, lockToken.ToString("D", CultureInfo.InvariantCulture));
        AppendField(sb, when);
        if (withSession)
            AppendField(sb, sessionId!);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public static bool TryDecode(string handle, out Decoded decoded)
    {
        decoded = default;
        if (string.IsNullOrEmpty(handle)) return false;
        try
        {
            var bytes = Convert.FromBase64String(handle);
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length < 2 || text[1] != ':') return false;
            var version = text[0];
            if (version != VersionV2[0] && version != VersionV3[0]) return false;

            var span = text.AsSpan(2);
            if (!TryReadField(ref span, out var queueName)) return false;
            if (!TryReadField(ref span, out var lockTokenRaw)) return false;
            if (!TryReadField(ref span, out var when)) return false;
            string? sessionId = null;
            if (version == VersionV3[0])
            {
                if (!TryReadField(ref span, out var sid)) return false;
                sessionId = sid;
            }
            if (!span.IsEmpty) return false;
            if (!Guid.TryParseExact(lockTokenRaw, "D", out var lockToken)) return false;

            DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var lockedUntil);
            decoded = new Decoded(queueName, lockToken, lockedUntil, sessionId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fast check that doesn't allocate or decode. Returns <c>true</c>
    /// when the handle's first base64 byte is the literal version
    /// character <c>'2'</c> or <c>'3'</c> (base64 prefixes <c>"Mjo"</c>
    /// / <c>"Mzo"</c>). Used by the dispatcher to route AMQP-issued
    /// handles back to the AMQP handler without touching the REST
    /// decoder.
    /// </summary>
    public static bool LooksLikeAmqpHandle(string handle) =>
        !string.IsNullOrEmpty(handle)
        && (handle.StartsWith("Mjo", StringComparison.Ordinal)
            || handle.StartsWith("Mzo", StringComparison.Ordinal));

    private static void AppendField(StringBuilder sb, string value)
    {
        var byteLen = Encoding.UTF8.GetByteCount(value);
        sb.Append(byteLen.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static bool TryReadField(ref ReadOnlySpan<char> span, out string value)
    {
        value = string.Empty;
        var colon = span.IndexOf(':');
        if (colon < 0) return false;
        if (!int.TryParse(span[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var byteLen))
            return false;
        if (byteLen < 0) return false;
        var rest = span[(colon + 1)..];
        var maxByteLen = Encoding.UTF8.GetMaxByteCount(rest.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxByteLen);
        try
        {
            var written = Encoding.UTF8.GetBytes(rest, rented);
            if (byteLen > written) return false;
            value = Encoding.UTF8.GetString(rented, 0, byteLen);
            var consumedChars = Encoding.UTF8.GetCharCount(rented, 0, byteLen);
            span = rest[consumedChars..];
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public readonly record struct Decoded(
        string QueueName,
        Guid LockToken,
        DateTimeOffset LockedUntilUtc,
        string? SessionId = null);
}
