using System.Globalization;
using System.Text;
using System.Xml;

namespace Aws2Azure.Modules.S3.Xml;

/// <summary>
/// Manual XmlWriter helpers for the S3 response shapes used in slice 1.
/// XmlWriter is AOT-safe; XmlSerializer (reflection-based) is not, so every
/// payload is hand-written here. The schema follows the public S3 REST API:
/// https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListBuckets.html.
/// </summary>
internal static class S3XmlWriter
{
    private const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private static readonly XmlWriterSettings Settings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        CloseOutput = false,
    };

    public readonly record struct OwnerInfo(string Id, string DisplayName);

    public readonly record struct BucketInfo(string Name, DateTimeOffset CreationDate);

    public static string ListAllMyBucketsResult(OwnerInfo owner, IReadOnlyList<BucketInfo> buckets)
    {
        var sb = new StringBuilder(256);
        using (var writer = XmlWriter.Create(sb, Settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ListAllMyBucketsResult", S3Namespace);

            writer.WriteStartElement("Owner");
            writer.WriteElementString("ID", owner.Id);
            writer.WriteElementString("DisplayName", owner.DisplayName);
            writer.WriteEndElement();

            writer.WriteStartElement("Buckets");
            foreach (var bucket in buckets)
            {
                writer.WriteStartElement("Bucket");
                writer.WriteElementString("Name", bucket.Name);
                writer.WriteElementString("CreationDate",
                    bucket.CreationDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return sb.ToString();
    }

    public readonly record struct ListedObject(
        string Key,
        DateTimeOffset LastModified,
        string? ETag,
        long Size,
        string StorageClass);

    /// <summary>
    /// S3 CopyObject response body. Only LastModified + ETag are emitted;
    /// SDKs surface both. ETag is normalised through the same quoting rule
    /// used for ListObjects so Azure's bare ETag round-trips as S3-shaped.
    /// </summary>
    public static string CopyObjectResult(DateTimeOffset lastModified, string? eTag)
    {
        var sb = new StringBuilder(192);
        using (var writer = XmlWriter.Create(sb, Settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("CopyObjectResult", S3Namespace);
            writer.WriteElementString("LastModified",
                lastModified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            var quoted = NormalizeETag(eTag);
            if (!string.IsNullOrEmpty(quoted))
            {
                writer.WriteElementString("ETag", quoted);
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return sb.ToString();
    }

    public readonly record struct DeletedEntry(string Key);

    public readonly record struct DeleteErrorEntry(string Key, string Code, string Message);

    /// <summary>
    /// S3 multi-object DeleteResult envelope. In <paramref name="quiet"/>
    /// mode (Delete.Quiet=true on the request) successfully deleted keys
    /// are omitted and only errors are emitted, matching the wire spec.
    /// </summary>
    public static string DeleteResult(
        bool quiet,
        IReadOnlyList<DeletedEntry> deleted,
        IReadOnlyList<DeleteErrorEntry> errors)
    {
        var sb = new StringBuilder(256);
        using (var writer = XmlWriter.Create(sb, Settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("DeleteResult", S3Namespace);

            if (!quiet)
            {
                foreach (var d in deleted)
                {
                    writer.WriteStartElement("Deleted");
                    writer.WriteElementString("Key", d.Key);
                    writer.WriteEndElement();
                }
            }

            foreach (var e in errors)
            {
                writer.WriteStartElement("Error");
                writer.WriteElementString("Key", e.Key);
                writer.WriteElementString("Code", e.Code);
                writer.WriteElementString("Message", e.Message);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return sb.ToString();
    }

    /// <summary>
    /// S3 ListObjectsV2 response. <paramref name="continuationToken"/> echoes
    /// the request's token (null if absent). <paramref name="nextContinuationToken"/>
    /// is set only when the listing is truncated. <paramref name="encodeUrl"/>
    /// percent-encodes Key/Prefix/Delimiter/StartAfter when the client asked
    /// for <c>encoding-type=url</c>.
    /// </summary>
    public static string ListObjectsV2Result(
        string bucket,
        string? prefix,
        string? delimiter,
        int maxKeys,
        int keyCount,
        bool isTruncated,
        string? continuationToken,
        string? nextContinuationToken,
        string? startAfter,
        bool encodeUrl,
        IReadOnlyList<ListedObject> contents,
        IReadOnlyList<string> commonPrefixes)
    {
        var sb = new StringBuilder(512);
        using (var writer = XmlWriter.Create(sb, Settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ListBucketResult", S3Namespace);

            writer.WriteElementString("Name", bucket);
            writer.WriteElementString("Prefix", Encode(prefix, encodeUrl));
            if (!string.IsNullOrEmpty(startAfter))
            {
                writer.WriteElementString("StartAfter", Encode(startAfter, encodeUrl));
            }
            if (!string.IsNullOrEmpty(continuationToken))
            {
                writer.WriteElementString("ContinuationToken", continuationToken);
            }
            if (!string.IsNullOrEmpty(nextContinuationToken))
            {
                writer.WriteElementString("NextContinuationToken", nextContinuationToken);
            }
            writer.WriteElementString("KeyCount", keyCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("MaxKeys", maxKeys.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(delimiter))
            {
                writer.WriteElementString("Delimiter", Encode(delimiter, encodeUrl));
            }
            writer.WriteElementString("IsTruncated", isTruncated ? "true" : "false");
            if (encodeUrl)
            {
                writer.WriteElementString("EncodingType", "url");
            }
            WriteContents(writer, contents, encodeUrl);
            WriteCommonPrefixes(writer, commonPrefixes, encodeUrl);

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return sb.ToString();
    }

    /// <summary>
    /// S3 ListObjects (V1) response. V1 uses <c>Marker</c> + <c>NextMarker</c>
    /// instead of continuation tokens; NextMarker is only emitted when the
    /// listing is truncated AND a delimiter was supplied (matches S3 docs).
    /// </summary>
    public static string ListBucketResult(
        string bucket,
        string? prefix,
        string? delimiter,
        int maxKeys,
        bool isTruncated,
        string? marker,
        string? nextMarker,
        bool encodeUrl,
        IReadOnlyList<ListedObject> contents,
        IReadOnlyList<string> commonPrefixes)
    {
        var sb = new StringBuilder(512);
        using (var writer = XmlWriter.Create(sb, Settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ListBucketResult", S3Namespace);

            writer.WriteElementString("Name", bucket);
            writer.WriteElementString("Prefix", Encode(prefix, encodeUrl));
            writer.WriteElementString("Marker", Encode(marker, encodeUrl));
            if (!string.IsNullOrEmpty(nextMarker))
            {
                writer.WriteElementString("NextMarker", Encode(nextMarker, encodeUrl));
            }
            writer.WriteElementString("MaxKeys", maxKeys.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(delimiter))
            {
                writer.WriteElementString("Delimiter", Encode(delimiter, encodeUrl));
            }
            writer.WriteElementString("IsTruncated", isTruncated ? "true" : "false");
            if (encodeUrl)
            {
                writer.WriteElementString("EncodingType", "url");
            }
            WriteContents(writer, contents, encodeUrl);
            WriteCommonPrefixes(writer, commonPrefixes, encodeUrl);

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return sb.ToString();
    }

    private static void WriteContents(XmlWriter writer, IReadOnlyList<ListedObject> contents, bool encodeUrl)
    {
        foreach (var entry in contents)
        {
            writer.WriteStartElement("Contents");
            writer.WriteElementString("Key", Encode(entry.Key, encodeUrl));
            writer.WriteElementString("LastModified",
                entry.LastModified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            var etag = NormalizeETag(entry.ETag);
            if (!string.IsNullOrEmpty(etag))
            {
                writer.WriteElementString("ETag", etag);
            }
            writer.WriteElementString("Size", entry.Size.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("StorageClass", entry.StorageClass);
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// S3 always wraps ETag values in double-quotes; Azure's List Blobs
    /// response surfaces them unquoted (e.g. <c>0x8CBFF45D8A29A19</c>).
    /// Normalize so SDK ETag parsers see the S3-shaped value.
    /// </summary>
    private static string? NormalizeETag(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw;
        }
        return "\"" + raw + "\"";
    }

    private static void WriteCommonPrefixes(XmlWriter writer, IReadOnlyList<string> commonPrefixes, bool encodeUrl)
    {
        foreach (var p in commonPrefixes)
        {
            writer.WriteStartElement("CommonPrefixes");
            writer.WriteElementString("Prefix", Encode(p, encodeUrl));
            writer.WriteEndElement();
        }
    }

    private static string Encode(string? value, bool encodeUrl)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return encodeUrl ? Uri.EscapeDataString(value) : value;
    }
}
