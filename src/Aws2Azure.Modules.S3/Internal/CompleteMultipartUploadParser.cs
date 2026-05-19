using System.IO;
using System.Xml;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Parses the S3 <c>CompleteMultipartUpload</c> request body:
/// <code>
/// &lt;CompleteMultipartUpload&gt;
///   &lt;Part&gt;&lt;PartNumber&gt;1&lt;/PartNumber&gt;&lt;ETag&gt;"…"&lt;/ETag&gt;&lt;/Part&gt;
///   …
/// &lt;/CompleteMultipartUpload&gt;
/// </code>
/// AOT-safe: hand-written <see cref="XmlReader"/> with
/// <see cref="DtdProcessing.Prohibit"/> (no XXE) and a hard part-count cap
/// matching the S3 limit of 10,000 parts per upload.
/// </summary>
internal static class CompleteMultipartUploadParser
{
    public const int MaxParts = 10_000;

    public readonly record struct PartRef(int PartNumber, string? ETag);

    public readonly record struct ParseResult(
        bool Success,
        IReadOnlyList<PartRef> Parts,
        string? Error);

    public static ParseResult Parse(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        var parts = new List<PartRef>();
        try
        {
            using var reader = XmlReader.Create(xml, settings);
            if (!reader.ReadToFollowing("CompleteMultipartUpload"))
            {
                return Fail("Missing <CompleteMultipartUpload> root element.");
            }

            int lastPart = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement &&
                    string.Equals(reader.LocalName, "CompleteMultipartUpload", StringComparison.Ordinal))
                {
                    break;
                }
                if (reader.NodeType != XmlNodeType.Element ||
                    !string.Equals(reader.LocalName, "Part", StringComparison.Ordinal))
                {
                    continue;
                }

                if (parts.Count >= MaxParts)
                {
                    return Fail($"More than {MaxParts} <Part> entries.");
                }

                if (!ReadPart(reader, out var part, out var err))
                {
                    return Fail(err!);
                }

                // S3 requires parts in ascending PartNumber order and no
                // duplicates; CompleteMultipartUpload rejects out-of-order
                // input with InvalidPartOrder.
                if (part.PartNumber <= lastPart)
                {
                    return Fail("Parts must be supplied in ascending PartNumber order without duplicates.");
                }
                lastPart = part.PartNumber;
                parts.Add(part);
            }
        }
        catch (XmlException ex)
        {
            return Fail("Malformed XML: " + ex.Message);
        }

        if (parts.Count == 0)
        {
            return Fail("At least one <Part> entry is required.");
        }

        return new ParseResult(true, parts, null);
    }

    private static bool ReadPart(XmlReader reader, out PartRef part, out string? error)
    {
        part = default;
        error = null;
        int? partNumber = null;
        string? etag = null;

        if (reader.IsEmptyElement)
        {
            error = "<Part> is empty.";
            return false;
        }

        // Advance into the <Part> body so the first iteration sees the
        // first child element. ReadElementContentAsString below leaves the
        // reader positioned on the *next* sibling, so we must not call
        // reader.Read() again after consuming a leaf — otherwise we'd
        // silently skip over the following child (e.g. <ETag>).
        if (!reader.Read())
        {
            error = "<Part> ended unexpectedly.";
            return false;
        }

        while (true)
        {
            if (reader.NodeType == XmlNodeType.EndElement &&
                string.Equals(reader.LocalName, "Part", StringComparison.Ordinal))
            {
                break;
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                var value = reader.ReadElementContentAsString();
                switch (name)
                {
                    case "PartNumber":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out var pn) ||
                            pn is < 1 or > 10000)
                        {
                            error = "PartNumber must be an integer in [1, 10000].";
                            return false;
                        }
                        partNumber = pn;
                        break;
                    case "ETag":
                        etag = value;
                        break;
                    default:
                        // Ignore unknown elements (forwards-compat).
                        break;
                }
                // ReadElementContentAsString already advanced past the
                // closing tag of the leaf; skip the unconditional Read below.
                continue;
            }
            if (!reader.Read())
            {
                error = "<Part> ended unexpectedly.";
                return false;
            }
        }

        if (partNumber is null)
        {
            error = "<Part> is missing <PartNumber>.";
            return false;
        }
        part = new PartRef(partNumber.Value, etag);
        return true;
    }

    private static ParseResult Fail(string message) =>
        new(false, Array.Empty<PartRef>(), message);
}
