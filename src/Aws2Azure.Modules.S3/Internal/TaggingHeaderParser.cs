using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Xml;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Parses the <c>x-amz-tagging</c> request header used by <c>PutObject</c>
/// and <c>CreateMultipartUpload</c> to set object tags at write time. The
/// value is a URL-encoded query-string of <c>key=value</c> pairs (e.g.
/// <c>"Project=Blue&amp;Team=Widget"</c>).
/// </summary>
internal static class TaggingHeaderParser
{
    internal const int MaxTags = 10;
    internal const int MaxTagKeyLength = 128;
    internal const int MaxTagValueLength = 256;

    public static (IReadOnlyList<S3XmlWriter.Tag>? Tags, S3ErrorMapping.Mapping? Error) Parse(string? headerValue)
    {
        if (string.IsNullOrEmpty(headerValue))
        {
            return (Array.Empty<S3XmlWriter.Tag>(), null);
        }

        // Parsed by hand rather than via QueryHelpers.ParseQuery: that helper
        // groups keys with an ordinal-IGNORE-CASE comparer, which would
        // silently merge two distinct, case-sensitive S3 tag keys (e.g.
        // "Env" and "ENV" are different tags in S3) into one duplicate-key
        // false-positive.
        var pairs = headerValue.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length > MaxTags)
        {
            return (null, S3ErrorMapping.InvalidArgument(
                $"Object tag count exceeds the allowed maximum of {MaxTags}."));
        }

        var tags = new List<S3XmlWriter.Tag>(pairs.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            string rawKey, rawValue;
            if (eq < 0)
            {
                rawKey = pair;
                rawValue = string.Empty;
            }
            else
            {
                rawKey = pair[..eq];
                rawValue = pair[(eq + 1)..];
            }

            string key, value;
            try
            {
                // '+' means space in a query-string-encoded value (the same
                // application/x-www-form-urlencoded convention AWS SDKs use
                // when marshalling x-amz-tagging), so decode it explicitly —
                // Uri.UnescapeDataString does not.
                key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            }
            catch (Exception)
            {
                return (null, S3ErrorMapping.InvalidArgument("The x-amz-tagging header value is malformed."));
            }

            if (!seen.Add(key))
            {
                return (null, S3ErrorMapping.InvalidArgument(
                    $"Duplicate tag key '{key}' in x-amz-tagging."));
            }

            if (string.IsNullOrEmpty(key) || key.Length > MaxTagKeyLength || value.Length > MaxTagValueLength)
            {
                return (null, S3ErrorMapping.InvalidArgument(
                    $"Invalid tag key/value (key length 1..{MaxTagKeyLength}, value length 0..{MaxTagValueLength})."));
            }

            tags.Add(new S3XmlWriter.Tag(key, value));
        }

        return (tags, null);
    }
}

