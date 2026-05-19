using System.Text;
using System.Xml;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Writes the Azure Blob Storage <c>Put Block List</c> request body:
/// <code>
/// &lt;BlockList&gt;
///   &lt;Latest&gt;BASE64_BLOCK_ID&lt;/Latest&gt;
///   …
/// &lt;/BlockList&gt;
/// </code>
/// We always use <c>&lt;Latest&gt;</c> — the Azure semantics that "commit
/// the most recently uploaded block with this id whether committed or
/// uncommitted" is what S3 CompleteMultipartUpload expects.
/// </summary>
internal static class BlockListXml
{
    public static byte[] Build(IReadOnlyList<string> base64BlockIds)
    {
        var settings = new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("BlockList");
            foreach (var id in base64BlockIds)
            {
                writer.WriteElementString("Latest", id);
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return ms.ToArray();
    }
}
