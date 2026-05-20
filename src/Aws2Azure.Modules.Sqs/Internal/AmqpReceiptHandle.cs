using System;
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
/// up the right receiver in the connection pool.
///
/// <para>The encoding is base64(UTF-8) of a version-prefixed, length-prefixed
/// payload:
/// <code>
///   "2" | queueName | lockTokenGuid("D" format) | lockedUntilUtc(ISO-8601, optional)
/// </code>
/// Version <c>"2"</c> intentionally differs from the REST-path
/// <see cref="ReceiptHandle"/> format (<c>"1"</c>) so the dispatcher can
/// route a delete request back to the right transport when a queue's
/// transport setting flips between REST and AMQP.</para>
///
/// <para>Length-prefixing (rather than separator-delimited) protects
/// against queue names or future fields containing arbitrary user
/// bytes — the field boundary cannot shift on decode.</para>
/// </summary>
internal static class AmqpReceiptHandle
{
    private const string Version = "2";

    public static string Encode(string queueName, Guid lockToken, DateTimeOffset lockedUntilUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        var when = lockedUntilUtc == default
            ? string.Empty
            : lockedUntilUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        // Version marker is emitted raw (not length-prefixed) so the
        // base64 representation starts with the literal byte sequence
        // "2:" → base64 prefix "Mjo". The REST v1 codec length-prefixes
        // its version, so its base64 starts with "MTo"; the two formats
        // are therefore prefix-distinguishable for fast routing.
        var sb = new StringBuilder();
        sb.Append(Version).Append(':');
        AppendField(sb, queueName);
        AppendField(sb, lockToken.ToString("D", CultureInfo.InvariantCulture));
        AppendField(sb, when);
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
            if (text.Length < 2 || text[0] != Version[0] || text[1] != ':') return false;
            var span = text.AsSpan(2);
            if (!TryReadField(ref span, out var queueName)) return false;
            if (!TryReadField(ref span, out var lockTokenRaw)) return false;
            if (!TryReadField(ref span, out var when)) return false;
            if (!span.IsEmpty) return false;
            if (!Guid.TryParseExact(lockTokenRaw, "D", out var lockToken)) return false;

            DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var lockedUntil);
            decoded = new Decoded(queueName, lockToken, lockedUntil);
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
    /// character <c>'2'</c> (i.e. starts with the byte sequence
    /// <c>0x32 0x3A</c> = <c>"2:"</c>, which base64-encodes to the
    /// 3-char prefix <c>"Mjo"</c>). Used by the dispatcher to route
    /// AMQP-issued handles back to the AMQP handler without touching
    /// the REST decoder.
    /// </summary>
    public static bool LooksLikeAmqpHandle(string handle) =>
        !string.IsNullOrEmpty(handle) && handle.StartsWith("Mjo", StringComparison.Ordinal);

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
        var bytes = Encoding.UTF8.GetBytes(rest.ToString());
        if (byteLen > bytes.Length) return false;
        value = Encoding.UTF8.GetString(bytes, 0, byteLen);
        var consumedChars = Encoding.UTF8.GetCharCount(bytes, 0, byteLen);
        span = rest[consumedChars..];
        return true;
    }

    public readonly record struct Decoded(string QueueName, Guid LockToken, DateTimeOffset LockedUntilUtc);
}
