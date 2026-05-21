using System;
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
        // "R" is RFC 1123 with the literal "GMT" suffix Cosmos requires.
        return now.UtcDateTime.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
    }
}
