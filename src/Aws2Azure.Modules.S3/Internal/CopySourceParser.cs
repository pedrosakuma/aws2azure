using Aws2Azure.Modules.S3.Errors;
using Microsoft.AspNetCore.Http;

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
/// Optional trailing <c>?versionId=…</c> is rejected in this slice — we
/// have no versioning story yet and silently ignoring the qualifier would
/// land the wrong object.
/// </summary>
internal static class CopySourceParser
{
    public readonly record struct ParseResult(bool Success, string? Bucket, string? Key, string? Error);

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
        var qmark = s.IndexOf('?', StringComparison.Ordinal);
        if (qmark >= 0)
        {
            var qs = s[(qmark + 1)..];
            // versionId qualifies a specific historical object — without a
            // versioning implementation we must reject rather than copy the
            // current version under a different identity.
            if (qs.Contains("versionId=", StringComparison.Ordinal))
            {
                return Fail("aws2azure: x-amz-copy-source versionId is not supported.");
            }
            s = s[..qmark];
        }

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

        return new ParseResult(true, bucket, decodedKey, null);
    }

    public readonly record struct ValidatedSource(bool Success, string? Bucket, string? Key, S3ErrorMapping.Mapping Error);

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

        return new ValidatedSource(true, bucket, key, default);
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

    private static ParseResult Fail(string message) =>
        new(false, null, null, message);

    private static ValidatedSource Invalid(S3ErrorMapping.Mapping error) =>
        new(false, null, null, error);
}
