using System.Buffers;
using System.Text;

namespace Aws2Azure.Core.SigV4;

/// <summary>
/// Builds the SigV4 <em>canonical request</em> string and the corresponding
/// <em>string-to-sign</em>.
/// </summary>
/// <remarks>
/// All inputs MUST already be in their on-the-wire form. The caller is
/// responsible for capturing the raw path and raw query string before any
/// framework normalization (ASP.NET Core preserves these on
/// <c>HttpRequest.Path</c> and <c>HttpRequest.QueryString</c>).
/// </remarks>
public static class CanonicalRequest
{
    /// <summary>
    /// Build the canonical request string per
    /// https://docs.aws.amazon.com/IAM/UserGuide/create-signed-request.html#create-canonical-request.
    /// </summary>
    public static string Build(
        string httpMethod,
        string rawPath,
        string rawQueryString,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string[] signedHeaders,
        string payloadHash,
        bool s3PathStyle)
    {
        var sb = new StringBuilder();
        sb.Append(httpMethod.ToUpperInvariant()).Append('\n');
        sb.Append(CanonicalUri(rawPath, s3PathStyle)).Append('\n');
        sb.Append(CanonicalQuery(rawQueryString)).Append('\n');
        AppendCanonicalHeaders(sb, headers, signedHeaders);
        sb.Append('\n');
        sb.Append(string.Join(';', signedHeaders)).Append('\n');
        sb.Append(payloadHash);
        return sb.ToString();
    }

    /// <summary>
    /// Build the string-to-sign per
    /// https://docs.aws.amazon.com/IAM/UserGuide/create-signed-request.html#create-string-to-sign.
    /// </summary>
    public static string StringToSign(string amzDate, string scope, string canonicalRequest)
    {
        var canonicalHash = SigningKey.Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));
        return $"{SigV4Constants.Algorithm}\n{amzDate}\n{scope}\n{canonicalHash}";
    }

    /// <summary>
    /// Encode the URI path. For S3 the path segments are URI-encoded once;
    /// for every other service they are encoded twice.
    /// </summary>
    public static string CanonicalUri(string rawPath, bool s3PathStyle)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            return "/";
        }

        var firstPass = EncodePathPreserveSlash(rawPath);
        return s3PathStyle ? firstPass : EncodePathPreserveSlash(firstPass);
    }

    /// <summary>
    /// Canonical query string: parameters sorted by key (and by value when
    /// keys collide), each name and value URI-encoded per RFC 3986.
    /// </summary>
    public static string CanonicalQuery(string rawQueryString)
    {
        if (string.IsNullOrEmpty(rawQueryString))
        {
            return string.Empty;
        }

        var qs = rawQueryString[0] == '?' ? rawQueryString[1..] : rawQueryString;
        if (qs.Length == 0)
        {
            return string.Empty;
        }

        var pairs = new List<(string Key, string Value)>();
        foreach (var raw in qs.Split('&'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            string key, value;
            var eq = raw.IndexOf('=');
            if (eq < 0)
            {
                key = raw;
                value = string.Empty;
            }
            else
            {
                key = raw[..eq];
                value = raw[(eq + 1)..];
            }

            // The signature itself MUST be excluded from the canonical query.
            if (key.Equals(SigV4Constants.AmzSignatureQuery, StringComparison.Ordinal))
            {
                continue;
            }

            pairs.Add((UriEncode(UrlDecode(key), true), UriEncode(UrlDecode(value), true)));
        }

        pairs.Sort(static (a, b) =>
        {
            var keyCmp = string.CompareOrdinal(a.Key, b.Key);
            return keyCmp != 0 ? keyCmp : string.CompareOrdinal(a.Value, b.Value);
        });

        var sb = new StringBuilder();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(pairs[i].Key).Append('=').Append(pairs[i].Value);
        }
        return sb.ToString();
    }

    private static void AppendCanonicalHeaders(
        StringBuilder sb,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string[] signedHeaders)
    {
        // Build a lookup keyed by lowercase header name; multi-value headers
        // are comma-joined in their original order.
        var bag = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in headers)
        {
            var name = kv.Key.ToLowerInvariant();
            if (!bag.TryGetValue(name, out var list))
            {
                list = new List<string>();
                bag[name] = list;
            }
            list.Add(TrimAndCollapseWhitespace(kv.Value));
        }

        for (var i = 0; i < signedHeaders.Length; i++)
        {
            var name = signedHeaders[i];
            sb.Append(name).Append(':');
            if (bag.TryGetValue(name, out var values))
            {
                for (var v = 0; v < values.Count; v++)
                {
                    if (v > 0) sb.Append(',');
                    sb.Append(values[v]);
                }
            }
            sb.Append('\n');
        }
    }

    /// <summary>
    /// Trim ASCII whitespace from both ends and collapse runs of internal
    /// whitespace to a single space — but only outside double-quoted regions.
    /// </summary>
    public static string TrimAndCollapseWhitespace(string value)
    {
        var span = value.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(span.Length);
        var inQuotes = false;
        var lastWasSpace = false;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }
            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            sb.Append(c);
            lastWasSpace = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// RFC 3986 percent-encoding. Per AWS spec the unreserved set is
    /// <c>A–Z a–z 0–9 - _ . ~</c>; when <paramref name="encodeSlash"/> is
    /// false, <c>/</c> is also left alone.
    /// </summary>
    public static string UriEncode(string value, bool encodeSlash)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (IsUnreserved((char)b) || (!encodeSlash && b == (byte)'/'))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%');
                sb.Append(HexUpper(b >> 4));
                sb.Append(HexUpper(b & 0xF));
            }
        }
        return sb.ToString();
    }

    private static string EncodePathPreserveSlash(string path)
    {
        if (path.Length == 0)
        {
            return "/";
        }
        return UriEncode(path, encodeSlash: false);
    }

    private static bool IsUnreserved(char c)
    {
        return (c >= 'A' && c <= 'Z')
            || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9')
            ||  c == '-' || c == '_' || c == '.' || c == '~';
    }

    private static char HexUpper(int nibble)
        => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));

    private static string UrlDecode(string value)
    {
        if (value.IndexOf('%') < 0)
        {
            return value;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(value));
        try
        {
            var written = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '%' && i + 2 < value.Length
                    && TryParseHex(value[i + 1], out var hi)
                    && TryParseHex(value[i + 2], out var lo))
                {
                    buffer[written++] = (byte)((hi << 4) | lo);
                    i += 2;
                }
                else
                {
                    // SigV4 query is RFC3986-encoded, NOT form-url-encoded:
                    // a literal '+' must round-trip as '+' (then re-encoded as
                    // %2B by UriEncode), and not decode to a space.
                    var charBytes = Encoding.UTF8.GetBytes(value.AsSpan(i, 1).ToArray());
                    Array.Copy(charBytes, 0, buffer, written, charBytes.Length);
                    written += charBytes.Length;
                }
            }
            return Encoding.UTF8.GetString(buffer, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParseHex(char c, out int value)
    {
        if (c >= '0' && c <= '9') { value = c - '0'; return true; }
        if (c >= 'A' && c <= 'F') { value = 10 + (c - 'A'); return true; }
        if (c >= 'a' && c <= 'f') { value = 10 + (c - 'a'); return true; }
        value = 0;
        return false;
    }
}
