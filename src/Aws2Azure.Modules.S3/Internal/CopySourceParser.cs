using Aws2Azure.Modules.S3.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Parses the <c>x-amz-copy-source</c> header. S3 accepts three shapes
/// (all percent-encoded):
/// <list type="bullet">
///   <item><c>/{bucket}/{key}</c> (modern, leading slash)</item>
///   <item><c>{bucket}/{key}</c> (legacy)</item>
///   <item><c>arn:aws:s3:::…</c> (S3-on-Outposts — out of scope)</item>
/// </list>
/// The bucket/key separator may be a literal <c>/</c> or a percent-encoded
/// <c>%2F</c>: the official AWS SDKs fully percent-encode the value
/// (including the separator) when marshalling <c>CopyObjectRequest</c>, so
/// the wire form is <c>{bucket}%2F{key}</c>. Both forms are accepted.
/// Optional trailing <c>?versionId=…</c> is parsed and returned separately so
/// copy flows can resolve a specific Azure blob version. The query delimiter
/// must remain literal; an encoded <c>%3FversionId=…</c> sequence is treated
/// as part of the object key so keys containing that text still round-trip.
/// </summary>
internal static class CopySourceParser
{
    public readonly record struct ParseResult(bool Success, string? Bucket, string? Key, string? VersionId, string? Error);

    public static ParseResult Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Fail("x-amz-copy-source header is required for CopyObject.");
        }

        if (raw.StartsWith("arn:", StringComparison.Ordinal))
        {
            return Fail("aws2azure: ARN copy-sources (S3-on-Outposts) are not supported.");
        }

        var s = raw;

        // Strip a single leading '/'.
        if (s.Length > 0 && s[0] == '/')
        {
            s = s[1..];
        }

        // Locate the bucket/key boundary. The AWS SDKs percent-encode the
        // separator as %2F (CopyObjectRequest marshalling), while hand-built
        // and legacy callers use a literal '/'. Accept whichever appears
        // first. Bucket names cannot contain '/' or '%', so the first
        // separator is unambiguous and everything before it is the bucket.
        var (sepIndex, sepLen) = FindSeparator(s);
        if (sepIndex <= 0 || sepIndex + sepLen >= s.Length)
        {
            return Fail("x-amz-copy-source must be of the form '/{bucket}/{key}'.");
        }

        var bucket = s[..sepIndex];
        var encodedKey = s[(sepIndex + sepLen)..];
        SplitVersionQualifier(ref encodedKey, out var versionId);

        if (!IsWellFormedPercentEncoding(encodedKey))
        {
            return Fail("x-amz-copy-source contains an invalid percent-encoding.");
        }

        // Per S3 docs the value is URL-encoded; decode before handing to the
        // backend so we work with the same key bytes a GET on the source
        // would see.
        var decodedKey = Uri.UnescapeDataString(encodedKey);

        if (string.IsNullOrEmpty(decodedKey))
        {
            return Fail("x-amz-copy-source key segment cannot be empty.");
        }

        return new ParseResult(true, bucket, decodedKey, versionId, null);
    }

    public readonly record struct ValidatedSource(bool Success, string? Bucket, string? Key, string? VersionId, S3ErrorMapping.Mapping Error);

    public static ValidatedSource ParseAndValidate(HttpRequest request)
    {
        var raw = HeaderForwarding.ReadFirstHeader(request, "x-amz-copy-source");
        var parsed = Parse(raw);
        if (!parsed.Success)
        {
            return Invalid(S3ErrorMapping.InvalidArgument(parsed.Error!));
        }

        var bucket = parsed.Bucket!;
        var key = parsed.Key!;
        if (!BlobClient.IsValidContainerName(bucket))
        {
            return Invalid(new S3ErrorMapping.Mapping(400, "InvalidBucketName",
                "The specified copy-source bucket is not valid."));
        }

        if (!S3ObjectKey.IsValid(key))
        {
            return Invalid(S3ErrorMapping.InvalidArgument(
                "The specified copy-source object key is not valid."));
        }

        return new ValidatedSource(true, bucket, key, parsed.VersionId, default);
    }

    private static (int Index, int Length) FindSeparator(string s)
    {
        // The separator is either a literal '/' (length 1) or a percent-encoded
        // '%2F'/'%2f' (length 3), whichever comes first.
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '/')
            {
                return (i, 1);
            }
            if (s[i] == '%' && i + 2 < s.Length
                && s[i + 1] == '2' && (s[i + 2] == 'F' || s[i + 2] == 'f'))
            {
                return (i, 3);
            }
        }
        return (-1, 0);
    }

    private static bool IsWellFormedPercentEncoding(string s)
    {
        // Uri.UnescapeDataString is permissive — it silently leaves "%ZZ"
        // intact. Validate up-front so a malformed encoding surfaces as
        // InvalidArgument rather than being copied byte-for-byte into the
        // Azure URL.
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '%')
            {
                continue;
            }
            if (i + 2 >= s.Length || !IsHex(s[i + 1]) || !IsHex(s[i + 2]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static void SplitVersionQualifier(ref string value, out string? versionId)
    {
        versionId = null;

        var literalQmark = value.IndexOf('?', StringComparison.Ordinal);
        if (literalQmark >= 0)
        {
            TryReadVersionId(value[(literalQmark + 1)..], out versionId);
            value = value[..literalQmark];
            return;
        }

    }

    private static void TryReadVersionId(string queryString, out string? versionId)
    {
        versionId = null;
        var parsedQuery = QueryHelpers.ParseQuery("?" + queryString);
        if (!parsedQuery.TryGetValue("versionId", out var versionValues))
        {
            return;
        }

        foreach (var value in versionValues)
        {
            if (!string.IsNullOrEmpty(value))
            {
                versionId = value;
                return;
            }
        }
    }

    private static ParseResult Fail(string message) =>
        new(false, null, null, null, message);

    private static ValidatedSource Invalid(S3ErrorMapping.Mapping error) =>
        new(false, null, null, null, error);
}
