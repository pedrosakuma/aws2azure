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

    public readonly record struct BlobEntry(
        string Name,
        DateTimeOffset LastModified,
        long ContentLength,
        string? ETag);

    /// <summary>
    /// One page of an Azure blob listing: the blob entries in the segment,
    /// the BlobPrefix values (Azure's equivalent of S3 CommonPrefixes,
    /// emitted when the listing request supplied a delimiter), and the
    /// NextMarker for continuation (or null when the listing is complete).
    /// </summary>
    public readonly record struct BlobListPage(
        IReadOnlyList<BlobEntry> Blobs,
        IReadOnlyList<string> BlobPrefixes,
        string? NextMarker);

    /// <summary>
    /// Parses an EnumerationResults document returned by
    /// <c>GET https://{account}.blob.core.windows.net/{container}?restype=container&amp;comp=list</c>.
    /// Extracts blob entries (Name, Last-Modified, Content-Length, ETag),
    /// BlobPrefix entries (when delimiter was set), and NextMarker. Pre-sizes
    /// the blob list to <paramref name="expectedCount"/> to avoid repeated
    /// List resizes for large listings.
    /// </summary>
    public static BlobListPage ParseBlobListPage(string xml, int expectedCount = 64)
    {
        var blobs = new List<BlobEntry>(expectedCount);
        var prefixes = new List<string>();
        string? nextMarker = null;

        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        });

        // Single-pass walk without ReadSubtree() allocations. Track whether
        // we're inside a <Blob> or <BlobPrefix> element and collect leaf
        // values accordingly. ReadElementContentAsString positions the reader
        // on the next node after the end-tag, so we use shouldAdvance=false
        // to avoid double-advancing.
        var inBlob = false;
        var inBlobPrefix = false;
        string? name = null;
        DateTimeOffset? lastModified = null;
        long contentLength = 0;
        string? etag = null;

        if (!reader.Read()) return new BlobListPage(blobs, prefixes, nextMarker);

        while (!reader.EOF)
        {
            var shouldAdvance = true;

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "Blob":
                        inBlob = true;
                        name = null;
                        lastModified = null;
                        contentLength = 0;
                        etag = null;
                        break;
                    case "BlobPrefix":
                        inBlobPrefix = true;
                        name = null;
                        break;
                    case "Name" when inBlob || inBlobPrefix:
                        name = reader.ReadElementContentAsString();
                        shouldAdvance = false;
                        break;
                    case "Last-Modified" when inBlob:
                        var rawLm = reader.ReadElementContentAsString();
                        if (DateTimeOffset.TryParse(rawLm, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedLm))
                        {
                            lastModified = parsedLm;
                        }
                        shouldAdvance = false;
                        break;
                    case "Content-Length" when inBlob:
                        var rawCl = reader.ReadElementContentAsString();
                        long.TryParse(rawCl, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                        shouldAdvance = false;
                        break;
                    case "Etag" when inBlob:
                        etag = reader.ReadElementContentAsString();
                        shouldAdvance = false;
                        break;
                    case "NextMarker" when !inBlob && !inBlobPrefix:
                        var marker = reader.ReadElementContentAsString();
                        if (!string.IsNullOrEmpty(marker))
                        {
                            nextMarker = marker;
                        }
                        shouldAdvance = false;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "Blob" && inBlob)
                {
                    blobs.Add(new BlobEntry(
                        name ?? string.Empty,
                        lastModified ?? DateTimeOffset.UnixEpoch,
                        contentLength,
                        etag));
                    inBlob = false;
                }
                else if (reader.LocalName == "BlobPrefix" && inBlobPrefix)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        prefixes.Add(name!);
                    }
                    inBlobPrefix = false;
                }
            }

            if (shouldAdvance)
            {
                reader.Read();
            }
        }

        return new BlobListPage(blobs, prefixes, nextMarker);
    }

    /// <summary>
    /// Parses a tag set XML document. Accepts both the AWS S3 wire shape
    /// (<c>&lt;Tagging&gt;&lt;TagSet&gt;…</c>, optionally with the S3
    /// namespace) and the Azure Blob shape (<c>&lt;Tags&gt;&lt;TagSet&gt;…</c>).
    /// Returns null when the document is well-formed XML but does not contain
    /// a recognised <c>&lt;TagSet&gt;</c>; throws <see cref="XmlException"/>
    /// on malformed XML.
    /// </summary>
    public static IReadOnlyList<Xml.S3XmlWriter.Tag>? ParseTagSet(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<Xml.S3XmlWriter.Tag>();
        }

        var tags = new List<Xml.S3XmlWriter.Tag>();
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        });

        var insideTagSet = false;
        var sawTagSet = false;
        string? key = null;
        string? value = null;

        // Manual cursor: ReadElementContentAsString already advances past the
        // closing tag, so calling reader.Read() at the top of the loop would
        // skip the sibling element (e.g. <Value> after <Key>). Instead we
        // advance explicitly inside each branch.
        if (!reader.Read()) return Array.Empty<Xml.S3XmlWriter.Tag>();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "TagSet":
                        insideTagSet = true;
                        sawTagSet = true;
                        reader.Read();
                        continue;
                    case "Tag" when insideTagSet:
                        key = null;
                        value = null;
                        reader.Read();
                        continue;
                    case "Key" when insideTagSet:
                        key = reader.ReadElementContentAsString();
                        continue;
                    case "Value" when insideTagSet:
                        value = reader.ReadElementContentAsString();
                        continue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "Tag" && insideTagSet && key is not null)
                {
                    tags.Add(new Xml.S3XmlWriter.Tag(key, value ?? string.Empty));
                }
                else if (reader.LocalName == "TagSet")
                {
                    insideTagSet = false;
                }
            }
            reader.Read();
        }

        return sawTagSet ? tags : null;
    }
}
