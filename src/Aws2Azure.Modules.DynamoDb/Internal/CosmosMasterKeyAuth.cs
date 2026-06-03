using System;
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Generates the <c>Authorization</c> header value Cosmos DB Core (SQL)
/// REST endpoints expect when using master-key authentication. Format:
/// <c>type=master&amp;ver=1.0&amp;sig={base64(HMAC-SHA256(stringToSign, decode(masterKey)))}</c>
/// where <c>stringToSign = lower(verb) + "\n" + lower(resourceType) + "\n" + resourceLink + "\n" + lower(date) + "\n\n"</c>.
///
/// <para>The result must be URL-encoded by the caller because Cosmos
/// rejects raw <c>+</c> / <c>/</c> characters in the header value; the
/// helper returns the already-encoded form to keep callers from
/// forgetting.</para>
///
/// <para>The HTTP date used here MUST match the <c>x-ms-date</c> header
/// the caller sends; signature verification is byte-exact.</para>
///
/// <para>Spec reference: <see href="https://learn.microsoft.com/azure/cosmos-db/nosql/security/access-control-overview#authorization-header"/>.</para>
/// </summary>
internal static class CosmosMasterKeyAuth
{
    /// <summary>
    /// Builds the URL-encoded <c>Authorization</c> header for a Cosmos
    /// REST call. <paramref name="resourceType"/> is the singular form
    /// the API uses (e.g. <c>dbs</c>, <c>colls</c>, <c>docs</c>).
    /// <paramref name="resourceLink"/> is the resource path **without**
    /// a leading slash (e.g. <c>dbs/myDb/colls/myColl</c>) or the
    /// empty string for top-level operations on the resource type.
    /// </summary>
    /// <param name="verb">HTTP verb, e.g. <c>GET</c>, <c>POST</c>.</param>
    /// <param name="resourceType">Cosmos resource type singular: <c>dbs</c>, <c>colls</c>, <c>docs</c>, <c>sprocs</c>, etc.</param>
    /// <param name="resourceLink">Resource link without leading slash, or empty for root-level ops.</param>
    /// <param name="utcNowHttpDate">Current time formatted as an RFC 1123 lower-case HTTP date — must equal what the caller sends as <c>x-ms-date</c>.</param>
    /// <param name="base64MasterKey">Cosmos master key (primary or secondary) as the base64 string Azure returns.</param>
    public static string Build(
        string verb,
        string resourceType,
        string resourceLink,
        string utcNowHttpDate,
        string base64MasterKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(verb);
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceLink);
        ArgumentException.ThrowIfNullOrEmpty(utcNowHttpDate);
        ArgumentException.ThrowIfNullOrEmpty(base64MasterKey);

        // String-to-sign is case-normalised verb/resourceType/date but the
        // resource link is taken verbatim — case is significant there since
        // Cosmos treats names as case-sensitive identifiers.
        var stringToSign = string.Concat(
            verb.ToLowerInvariant(), "\n",
            resourceType.ToLowerInvariant(), "\n",
            resourceLink, "\n",
            utcNowHttpDate.ToLowerInvariant(), "\n",
            "\n");

        var keyBytes = Convert.FromBase64String(base64MasterKey);
        Span<byte> hash = stackalloc byte[32];
        if (!HMACSHA256.TryHashData(keyBytes, Encoding.UTF8.GetBytes(stringToSign), hash, out var written)
            || written != 32)
        {
            // Fallback: shouldn't happen with a 32-byte SHA-256 output.
            hash = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(stringToSign));
        }
        var signature = Convert.ToBase64String(hash);

        // Cosmos requires URL-encoding so '+' and '/' don't get reinterpreted
        // by intermediate proxies as query separators.
        var raw = string.Concat("type=master&ver=1.0&sig=", signature);
        return Uri.EscapeDataString(raw);
    }

    /// <summary>
    /// Returns the current UTC time formatted as an RFC 1123
    /// lower-case HTTP date — Cosmos requires this exact form on the
    /// <c>x-ms-date</c> header. Centralised so callers and the auth
    /// helper agree on the byte representation.
    /// </summary>
    public static string GetHttpUtcDate(DateTimeOffset now)
    {
        // "R" is RFC 1123 with the literal "GMT" suffix Cosmos requires. The
        // format is fixed-width ASCII (29 chars, e.g. "Thu, 27 Apr 2017
        // 00:51:12 GMT"), so format into a stack buffer and ASCII-fold to
        // lowercase in place — one String allocation instead of the two the
        // ToString("R").ToLowerInvariant() pair produced per request.
        Span<char> buf = stackalloc char[32];
        if (!now.UtcDateTime.TryFormat(buf, out var written, "R", CultureInfo.InvariantCulture))
        {
            return now.UtcDateTime.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        }

        var slice = buf[..written];
        AsciiLowerInPlace(slice);
        return new string(slice);
    }

    /// <summary>
    /// Allocation-light hot-path equivalent of <see cref="Build"/>. Produces
    /// the byte-identical URL-encoded <c>Authorization</c> header value while
    /// avoiding the per-call <c>FromBase64String</c> of the master key
    /// (hoisted to the caller via <paramref name="masterKeyBytes"/>), the
    /// <c>ToLowerInvariant</c> Strings, the <c>string.Concat</c> /
    /// <c>Encoding.UTF8.GetBytes</c> pair, and the
    /// <c>Convert.ToBase64String</c> + <c>Uri.EscapeDataString</c>
    /// allocations. Only the returned header <see cref="string"/> is
    /// allocated (HTTP headers require a String).
    ///
    /// <para><paramref name="verb"/>, <paramref name="resourceType"/> and
    /// <paramref name="utcNowHttpDate"/> are ASCII by construction (HTTP
    /// method, fixed Cosmos resource-type literal, RFC 1123 date) so the
    /// case-fold is a plain ASCII fold — byte-identical to the oracle's
    /// <c>ToLowerInvariant</c> for that domain. <paramref name="resourceLink"/>
    /// is UTF-8 encoded verbatim (case-significant).</para>
    /// </summary>
    public static string BuildAuthHeader(
        string verb,
        string resourceType,
        string resourceLink,
        string utcNowHttpDate,
        ReadOnlySpan<byte> masterKeyBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(verb);
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceLink);
        ArgumentException.ThrowIfNullOrEmpty(utcNowHttpDate);
        if (masterKeyBytes.IsEmpty)
        {
            throw new ArgumentException("Master key bytes must be non-empty.", nameof(masterKeyBytes));
        }

        // Upper bound for the UTF-8 string-to-sign: the three ASCII fields are
        // 1 byte/char, resourceLink may carry multi-byte UTF-8, plus 5 '\n'.
        var stsMaxBytes = verb.Length + resourceType.Length + utcNowHttpDate.Length
            + Encoding.UTF8.GetMaxByteCount(resourceLink.Length) + 5;

        const int StackThreshold = 256;
        byte[]? rented = null;
        Span<byte> sts = stsMaxBytes <= StackThreshold
            ? stackalloc byte[StackThreshold]
            : (rented = ArrayPool<byte>.Shared.Rent(stsMaxBytes));

        try
        {
            var pos = 0;
            AppendAsciiLower(sts, ref pos, verb);
            sts[pos++] = (byte)'\n';
            AppendAsciiLower(sts, ref pos, resourceType);
            sts[pos++] = (byte)'\n';
            pos += Encoding.UTF8.GetBytes(resourceLink, sts[pos..]);
            sts[pos++] = (byte)'\n';
            AppendAsciiLower(sts, ref pos, utcNowHttpDate);
            sts[pos++] = (byte)'\n';
            sts[pos++] = (byte)'\n';

            Span<byte> hash = stackalloc byte[32];
            if (!HMACSHA256.TryHashData(masterKeyBytes, sts[..pos], hash, out var written) || written != 32)
            {
                hash = HMACSHA256.HashData(masterKeyBytes, sts[..pos].ToArray());
            }

            // base64(32 bytes) is always 44 chars (single '=' pad).
            Span<char> b64 = stackalloc char[44];
            Convert.TryToBase64Chars(hash, b64, out var b64Len);

            return BuildEncodedHeader(b64[..b64Len]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // "type=master&ver=1.0&sig=" after Uri.EscapeDataString — every '=' and
    // '&' is percent-encoded, the rest are unreserved. Constant per build.
    private const string EncodedPrefix = "type%3Dmaster%26ver%3D1.0%26sig%3D";

    /// <summary>
    /// Emits <c>EscapeDataString("type=master&amp;ver=1.0&amp;sig=" + base64)</c>
    /// without intermediate Strings. Within the base64 alphabet only
    /// <c>+</c>, <c>/</c> and <c>=</c> are reserved, so they expand to
    /// <c>%2B</c> / <c>%2F</c> / <c>%3D</c> and every other char is copied
    /// verbatim — byte-identical to <see cref="Uri.EscapeDataString"/>.
    /// </summary>
    private static string BuildEncodedHeader(ReadOnlySpan<char> base64)
    {
        // Worst case every base64 char expands to 3 chars.
        Span<char> dest = stackalloc char[EncodedPrefix.Length + (44 * 3)];
        EncodedPrefix.AsSpan().CopyTo(dest);
        var pos = EncodedPrefix.Length;

        foreach (var c in base64)
        {
            switch (c)
            {
                case '+':
                    dest[pos++] = '%'; dest[pos++] = '2'; dest[pos++] = 'B';
                    break;
                case '/':
                    dest[pos++] = '%'; dest[pos++] = '2'; dest[pos++] = 'F';
                    break;
                case '=':
                    dest[pos++] = '%'; dest[pos++] = '3'; dest[pos++] = 'D';
                    break;
                default:
                    dest[pos++] = c;
                    break;
            }
        }

        return new string(dest[..pos]);
    }

    private static void AppendAsciiLower(Span<byte> dest, ref int pos, ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            dest[pos++] = (byte)(c is >= 'A' and <= 'Z' ? c + 32 : c);
        }
    }

    private static void AsciiLowerInPlace(Span<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'A' and <= 'Z')
            {
                value[i] = (char)(c + 32);
            }
        }
    }
}
