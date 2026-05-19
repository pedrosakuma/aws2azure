namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Parses the <c>x-amz-copy-source</c> header. S3 accepts three shapes
/// (all percent-encoded):
/// <list type="bullet">
///   <item><c>/{bucket}/{key}</c> (modern, leading slash)</item>
///   <item><c>{bucket}/{key}</c> (legacy)</item>
///   <item><c>arn:aws:s3:::…</c> (S3-on-Outposts — out of scope)</item>
/// </list>
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

        var slash = s.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash >= s.Length - 1)
        {
            return Fail("x-amz-copy-source must be of the form '/{bucket}/{key}'.");
        }

        var bucket = s[..slash];
        var encodedKey = s[(slash + 1)..];

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
}
