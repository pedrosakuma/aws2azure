using System;
using System.Globalization;
using System.Text;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// SQS receipt handles are opaque tokens the client passes back on
/// DeleteMessage / ChangeMessageVisibility. SQS itself doesn't define a
/// format, so the proxy is free to pick one — we use a URL-safe base64
/// encoding of the four pieces of state Service Bus needs to act on the
/// in-flight message:
///
/// <list type="bullet">
///   <item><c>messageId</c> — SB locator for the broker side.</item>
///   <item><c>lockToken</c> — the GUID SB issued at peek-lock time.</item>
///   <item><c>sequenceNumber</c> — used for diagnostics and to scope
///         <c>ChangeMessageVisibility</c> when SB requires it.</item>
///   <item><c>lockedUntilUtc</c> — the ISO-8601 instant SB said the lock
///         expires; lets us reject obviously-stale handles cheaply.</item>
/// </list>
///
/// <para>The encoding is length-prefixed rather than separator-delimited so
/// arbitrary user-controlled values (e.g. a FIFO <c>MessageDeduplicationId</c>
/// containing <c>'|'</c>) cannot shift the field boundaries on decode.</para>
/// </summary>
internal static class ReceiptHandle
{
    // v1 prefix is part of the payload so we can evolve the encoding while
    // still recognising handles minted by older proxy versions.
    private const string Version = "1";

    public static string Encode(string messageId, string lockToken, string sequenceNumber, DateTimeOffset lockedUntilUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentException.ThrowIfNullOrEmpty(lockToken);
        // sequenceNumber and lockedUntilUtc may be absent in some SB responses;
        // surface them as empty rather than failing — the receive path will
        // mint defaults when needed.
        sequenceNumber ??= string.Empty;
        var when = lockedUntilUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        // Length-prefix each field: <len>:<utf8 bytes>. Lengths are written
        // as decimal characters so the entire payload remains 7-bit-clean
        // and safe to base64-encode without further escaping. This avoids
        // the separator-collision class of bugs that plagued earlier
        // pipe-delimited encodings (e.g. a FIFO MessageDeduplicationId
        // containing '|' silently corrupting downstream Delete / Renew
        // calls).
        var sb = new StringBuilder();
        AppendField(sb, Version);
        AppendField(sb, messageId);
        AppendField(sb, lockToken);
        AppendField(sb, sequenceNumber);
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
            var span = text.AsSpan();
            if (!TryReadField(ref span, out var version)) return false;
            if (version != Version) return false;
            if (!TryReadField(ref span, out var messageId)) return false;
            if (!TryReadField(ref span, out var lockToken)) return false;
            if (!TryReadField(ref span, out var sequenceNumber)) return false;
            if (!TryReadField(ref span, out var when)) return false;
            if (!span.IsEmpty) return false;

            DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var lockedUntil);
            decoded = new Decoded(messageId, lockToken, sequenceNumber, lockedUntil);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void AppendField(StringBuilder sb, string value)
    {
        var byteLen = Encoding.UTF8.GetByteCount(value);
        sb.Append(byteLen.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static bool TryReadField(ref ReadOnlySpan<char> span, out string value)
    {
        value = string.Empty;
        var colon = span.IndexOf(':');
        if (colon <= 0) return false;
        if (!int.TryParse(span[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var byteLen))
            return false;
        if (byteLen < 0) return false;
        // Field bytes follow the colon; the underlying string was UTF-8
        // before base64, so byte length and char length only diverge for
        // multi-byte sequences. Translate the byte length back to a char
        // count by walking from the colon for `byteLen` UTF-8 bytes.
        var rest = span[(colon + 1)..];
        var bytes = Encoding.UTF8.GetBytes(rest.ToString());
        if (byteLen > bytes.Length) return false;
        value = Encoding.UTF8.GetString(bytes, 0, byteLen);
        // Advance the span past the field — convert byte offset to char offset.
        var consumedChars = Encoding.UTF8.GetCharCount(bytes, 0, byteLen);
        span = rest[consumedChars..];
        return true;
    }

    public readonly record struct Decoded(string MessageId, string LockToken, string SequenceNumber, DateTimeOffset LockedUntilUtc);
}

