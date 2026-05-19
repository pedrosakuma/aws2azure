using System.Xml;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Parses an S3 <c>Delete</c> request body for the multi-object delete
/// (POST /{bucket}?delete) operation. Reflection-free / AOT-safe; uses
/// <see cref="XmlReader"/> directly so no XmlSerializer ever spins up.
/// </summary>
/// <remarks>
/// Wire shape:
/// <code>
/// &lt;Delete&gt;
///   &lt;Object&gt;&lt;Key&gt;…&lt;/Key&gt;&lt;/Object&gt;
///   &lt;Quiet&gt;true|false&lt;/Quiet&gt;
/// &lt;/Delete&gt;
/// </code>
/// <c>VersionId</c> sub-elements are rejected (no versioning support in
/// this phase), as is any payload exceeding S3's 1 000 object batch cap.
/// </remarks>
internal static class DeleteRequestParser
{
    public const int MaxObjects = 1000;

    public readonly record struct Entry(string Key);

    public readonly record struct ParseResult(
        bool Success,
        IReadOnlyList<Entry> Objects,
        bool Quiet,
        string? Error);

    public static ParseResult Parse(Stream body)
    {
        var entries = new List<Entry>(16);
        var quiet = false;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true,
            CloseInput = false,
        };

        try
        {
            using var reader = XmlReader.Create(body, settings);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Delete")
            {
                return Fail("MalformedXML: expected root <Delete> element.");
            }

            if (reader.IsEmptyElement)
            {
                return Fail("MalformedXML: <Delete> must contain at least one <Object>.");
            }

            // Advance into <Delete> children.
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                if (reader.LocalName == "Object")
                {
                    var keyResult = ReadObject(reader);
                    if (keyResult.Error is not null)
                    {
                        return Fail(keyResult.Error);
                    }
                    if (entries.Count >= MaxObjects)
                    {
                        return Fail($"MalformedXML: too many keys; limit is {MaxObjects} per request.");
                    }
                    entries.Add(new Entry(keyResult.Key!));
                }
                else if (reader.LocalName == "Quiet")
                {
                    var raw = reader.ReadElementContentAsString();
                    quiet = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                else
                {
                    // Skip unknown siblings (forward-compat).
                    reader.Skip();
                    continue;
                }
            }
        }
        catch (XmlException ex)
        {
            return Fail("MalformedXML: " + ex.Message);
        }

        if (entries.Count == 0)
        {
            return Fail("MalformedXML: <Delete> must contain at least one <Object>.");
        }

        return new ParseResult(true, entries, quiet, null);
    }

    private readonly record struct ObjectResult(string? Key, string? Error);

    private static ObjectResult ReadObject(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return new ObjectResult(null, "MalformedXML: <Object> must contain a <Key> element.");
        }

        string? key = null;

        // Consume <Object> child elements via ReadSubtree so the outer
        // reader is positioned cleanly on the EndElement when we return.
        using (var sub = reader.ReadSubtree())
        {
            sub.Read(); // position at <Object>
            sub.Read(); // first child or EndElement
            while (sub.NodeType != XmlNodeType.EndElement)
            {
                if (sub.NodeType != XmlNodeType.Element)
                {
                    sub.Read();
                    continue;
                }
                switch (sub.LocalName)
                {
                    case "Key":
                        key = sub.ReadElementContentAsString();
                        break;
                    case "VersionId":
                        return new ObjectResult(null,
                            "NotImplemented: aws2azure does not support versionId in DeleteObjects.");
                    default:
                        sub.Skip();
                        break;
                }
            }
        }

        // Skip past the EndElement on the outer reader.
        reader.Read();

        if (string.IsNullOrEmpty(key))
        {
            return new ObjectResult(null, "MalformedXML: <Object> requires a non-empty <Key>.");
        }
        return new ObjectResult(key, null);
    }

    private static ParseResult Fail(string message) =>
        new(false, Array.Empty<Entry>(), false, message);
}
