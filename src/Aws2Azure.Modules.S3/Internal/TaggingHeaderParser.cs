using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.WebUtilities;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Parses the <c>x-amz-tagging</c> request header used by <c>PutObject</c>
/// and <c>CreateMultipartUpload</c> to set object tags at write time. The
/// value is a URL-encoded query-string of <c>key=value</c> pairs (e.g.
/// <c>"Project=Blue&amp;Team=Widget"</c>) — the same wire shape AWS SDKs use
/// for a plain query string, so it is parsed with
/// <see cref="QueryHelpers.ParseQuery"/> for consistency with the proxy's
/// other query-string parsing (see <see cref="CopySourceParser"/>).
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

        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parsed;
        try
        {
            parsed = QueryHelpers.ParseQuery(headerValue.StartsWith('?') ? headerValue : "?" + headerValue);
        }
        catch (Exception)
        {
            return (null, S3ErrorMapping.InvalidArgument("The x-amz-tagging header value is malformed."));
        }

        if (parsed.Count > MaxTags)
        {
            return (null, S3ErrorMapping.InvalidArgument(
                $"Object tag count exceeds the allowed maximum of {MaxTags}."));
        }

        var tags = new List<S3XmlWriter.Tag>(parsed.Count);
        foreach (var (key, values) in parsed)
        {
            // A repeated key in the query string (e.g. "a=1&a=2") surfaces
            // as multiple StringValues entries under the same dictionary
            // key — treat that the same as a duplicate <Tag> in the XML
            // TagSet body used by PutObjectTagging.
            if (values.Count > 1)
            {
                return (null, S3ErrorMapping.InvalidArgument(
                    $"Duplicate tag key '{key}' in x-amz-tagging."));
            }

            var value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
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
