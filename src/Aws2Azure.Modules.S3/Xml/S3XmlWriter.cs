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
}
