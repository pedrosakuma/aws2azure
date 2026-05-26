namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Object-key validation + URL encoding helpers shared by object handlers.
/// AOT-safe (no reflection, no regex).
/// </summary>
/// <remarks>
/// S3 keys are UTF-8 strings of 1..1024 bytes. We additionally reject NUL
/// bytes (Azure rejects them and they break HTTP headers) and any byte below
/// 0x20 except common whitespace that Azure accepts in metadata only.
/// Azure imposes its own blob name rules (1..1024 chars, can contain any
/// Unicode); the intersection that round-trips cleanly is what we accept.
/// </remarks>
internal static class S3ObjectKey
{
    public const int MaxBytes = 1024;

    public static bool IsValid(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        // Byte-length per S3 spec.
        var bytes = System.Text.Encoding.UTF8.GetByteCount(key);
        if (bytes == 0 || bytes > MaxBytes)
        {
            return false;
        }
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            // NUL and other C0 controls are unsafe in HTTP and rejected by Azure.
            if (c == '\0' || (c < 0x20 && c != '\t'))
            {
                return false;
            }
        }
        // Reject "." / ".." segments and leading/double slashes — Uri
        // resolution would otherwise normalize them away and let a crafted
        // key (e.g. "../other/blob") escape its target container.
        var span = key.AsSpan();
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == '/')
            {
                var segLen = i - start;
                if (segLen == 0 && i != span.Length && i != 0)
                {
                    // empty middle segment ("a//b") — also a normalization risk.
                    return false;
                }
                if (segLen == 1 && span[start] == '.')
                {
                    return false;
                }
                if (segLen == 2 && span[start] == '.' && span[start + 1] == '.')
                {
                    return false;
                }
                start = i + 1;
            }
        }
        if (span[0] == '/')
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Percent-encodes the key for inclusion in a blob URL path. Path
    /// separators ('/') are preserved so virtual-folder hierarchies survive.
    /// Encoding follows RFC 3986 unreserved + sub-delims that Azure accepts;
    /// everything else is %HH-escaped from UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Fast-path: when every character is already URL-safe (RFC 3986
    /// unreserved or '/') the input string is returned unchanged with no
    /// allocation. Profiling showed this method on the hot path of every
    /// blob operation, allocating a StringBuilder + a UTF-8 byte buffer
    /// per call; the typical S3 key (UUID, virtual-folder path) is
    /// ASCII-clean and now skips both allocations.
    /// </remarks>
    public static string EncodeForBlobUrl(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }
        if (!NeedsEncoding(key))
        {
            return key;
        }
        return EncodeSlow(key);
    }

    private static bool NeedsEncoding(string key)
    {
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            // Anything outside ASCII unreserved + '/' must go through the
            // percent-encoding path. Non-ASCII (>= 0x80) also triggers the
            // slow path so it can be UTF-8 expanded byte-by-byte.
            if (c >= 0x80 || (!IsUnreserved(c) && c != '/'))
            {
                return true;
            }
        }
        return false;
    }

    private static string EncodeSlow(string key)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(key);
        var buf = new System.Text.StringBuilder(utf8.Length + 8);
        for (var i = 0; i < utf8.Length; i++)
        {
            var b = utf8[i];
            if (IsUnreserved((char)b) || b == (byte)'/')
            {
                buf.Append((char)b);
            }
            else
            {
                buf.Append('%');
                buf.Append(HexUpper(b >> 4));
                buf.Append(HexUpper(b & 0xF));
            }
        }
        return buf.ToString();
    }

    private static bool IsUnreserved(char c) =>
        c is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '0' and <= '9')
            or '-' or '_' or '.' or '~';

    private static char HexUpper(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
}
