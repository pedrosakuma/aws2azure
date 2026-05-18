using System.Globalization;
using System.Text;
using System.Xml;

namespace Aws2Azure.Modules.S3.Xml;

/// <summary>
/// Manual XmlReader helpers for Azure Blob storage responses consumed by the
/// S3 module. AOT-safe (no XmlSerializer, no reflection).
/// </summary>
internal static class AzureBlobXmlReader
{
    public readonly record struct ContainerEntry(string Name, DateTimeOffset LastModified);

    /// <summary>
    /// One page of an Azure container listing: the containers in this segment
    /// plus the <c>NextMarker</c> needed to fetch the following page (or null
    /// when the listing is complete).
    /// </summary>
    public readonly record struct ContainerListPage(IReadOnlyList<ContainerEntry> Containers, string? NextMarker);

    /// <summary>
    /// Parses an EnumerationResults document and returns just the container
    /// entries. Convenience wrapper around <see cref="ParseContainerListPage"/>.
    /// </summary>
    public static IReadOnlyList<ContainerEntry> ParseContainerList(string xml) =>
        ParseContainerListPage(xml).Containers;

    /// <summary>
    /// Parses an EnumerationResults document returned by
    /// <c>GET https://{account}.blob.core.windows.net/?comp=list</c>.
    /// Extracts the container entries plus the <c>NextMarker</c> needed for
    /// continuation. Slice 1 only needs Name + Last-Modified.
    /// </summary>
    public static ContainerListPage ParseContainerListPage(string xml)
    {
        var list = new List<ContainerEntry>();
        string? nextMarker = null;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        });

        string? name = null;
        DateTimeOffset? lastModified = null;
        var insideContainer = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "Container":
                        insideContainer = true;
                        name = null;
                        lastModified = null;
                        break;
                    case "Name" when insideContainer:
                        name = reader.ReadElementContentAsString();
                        break;
                    case "Last-Modified" when insideContainer:
                        var raw = reader.ReadElementContentAsString();
                        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                        {
                            lastModified = parsed;
                        }
                        break;
                    case "NextMarker" when !insideContainer:
                        var marker = reader.ReadElementContentAsString();
                        if (!string.IsNullOrEmpty(marker))
                        {
                            nextMarker = marker;
                        }
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Container")
            {
                if (!string.IsNullOrEmpty(name))
                {
                    list.Add(new ContainerEntry(name, lastModified ?? DateTimeOffset.UnixEpoch));
                }
                insideContainer = false;
            }
        }

        return new ContainerListPage(list, nextMarker);
    }
}
