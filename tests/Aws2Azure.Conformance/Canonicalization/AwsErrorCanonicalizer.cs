using System.Text;
using System.Text.Json;
using System.Xml;

namespace Aws2Azure.Conformance.Canonicalization;

/// <summary>
/// Reduces a raw AWS error response (HTTP status + headers + body bytes) to a
/// <see cref="CanonicalResponse"/> by applying a <see cref="CanonicalizationPolicy"/>.
/// Pure and side-effect-free so it can be unit-tested offline against
/// hand-crafted AWS-shaped payloads, and reused by both the Tier-1 golden
/// replay and the Tier-2 LocalStack differential.
/// </summary>
public static class AwsErrorCanonicalizer
{
    public static CanonicalResponse Canonicalize(
        int statusCode,
        IEnumerable<KeyValuePair<string, string>> headers,
        string body,
        CanonicalizationPolicy? policy = null)
    {
        policy ??= CanonicalizationPolicy.Default;

        var canonicalHeaders = new List<CanonicalField>();
        string? contentType = null;
        foreach (var (rawName, rawValue) in headers)
        {
            var name = rawName.Trim().ToLowerInvariant();
            if (name == "content-type")
            {
                contentType = rawValue;
            }
            if (!policy.IsSignificantHeader(name))
            {
                continue;
            }
            var value = policy.VolatileHeaderValues.Contains(name)
                ? CanonicalResponse.Masked
                : NormalizeHeaderValue(name, rawValue);
            canonicalHeaders.Add(new CanonicalField(name, value));
        }
        canonicalHeaders.Sort(static (a, b) => CompareFields(a, b));

        var (bodyKind, bodyFields) = CanonicalizeBody(body, contentType, policy);
        bodyFields.Sort(static (a, b) => CompareFields(a, b));

        return new CanonicalResponse(statusCode, canonicalHeaders, bodyKind, bodyFields, body);
    }

    private static (string Kind, List<CanonicalField> Fields) CanonicalizeBody(
        string body, string? contentType, CanonicalizationPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (CanonicalResponse.BodyKindEmpty, new List<CanonicalField>());
        }

        var baseType = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        var trimmed = body.TrimStart();
        var looksXml = baseType is "application/xml" or "text/xml"
            || trimmed.StartsWith('<');
        if (looksXml)
        {
            try
            {
                return (CanonicalResponse.BodyKindXmlError, ParseXmlError(body, policy));
            }
            catch (XmlException)
            {
                // Malformed XML is itself a faithful observation — surface it as an
                // opaque body rather than throwing.
                return (CanonicalResponse.BodyKindOpaque,
                    new List<CanonicalField> { new("(unparseable-xml)", body.Trim()) });
            }
        }

        // AWS JSON-protocol services (DynamoDB, Kinesis, modern SQS, …) return
        // their error as a JSON envelope ({"__type":"…#Code","message":"…"})
        // rather than the S3-style XML <Error>. Canonicalize it onto the same
        // contract surface (a Code field + masked Message) so the Tier-1 replay
        // and Tier-2 differential work uniformly across protocols.
        var looksJson = baseType.Contains("json", StringComparison.Ordinal)
            || trimmed.StartsWith('{');
        if (looksJson)
        {
            try
            {
                var (kind, fields) = ParseJsonError(body, policy);
                return (kind, fields);
            }
            catch (JsonException)
            {
                return (CanonicalResponse.BodyKindOpaque,
                    new List<CanonicalField> { new("(unparseable-json)", body.Trim()) });
            }
        }

        return (CanonicalResponse.BodyKindOpaque,
            new List<CanonicalField> { new("(raw)", body.Trim()) });
    }

    private static List<CanonicalField> ParseXmlError(string body, CanonicalizationPolicy policy)
    {
        var fields = new List<CanonicalField>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
            CloseInput = true,
        };

        using var reader = XmlReader.Create(new StringReader(body), settings);
        reader.MoveToContent();
        fields.Add(new CanonicalField("(root)", reader.LocalName));

        var rootDepth = reader.Depth;
        if (reader.IsEmptyElement || !reader.Read())
        {
            return fields;
        }

        // ReadElement fully consumes each child element and leaves the reader
        // positioned on the next node, so the loop must NOT call Read() after it.
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == rootDepth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1)
            {
                if (!reader.Read())
                {
                    break;
                }
                continue;
            }

            var (name, rawValue) = ReadElement(reader);
            var value = policy.VolatileBodyElements.Contains(name)
                        || policy.NonContractualBodyElements.Contains(name)
                ? CanonicalResponse.Masked
                : rawValue.Trim();
            fields.Add(new CanonicalField(name, value));
        }

        return fields;
    }

    /// <summary>
    /// Reads a single error-envelope child element and leaves the reader on the
    /// next node. Leaf elements yield their text; an element with nested element
    /// children yields the structural marker <c>&lt;nested&gt;</c> (its presence is
    /// part of the contract surface even when its inner shape is not).
    /// </summary>
    private static (string Name, string Value) ReadElement(XmlReader reader)
    {
        var name = reader.LocalName;
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return (name, string.Empty);
        }

        var startDepth = reader.Depth;
        var text = new StringBuilder();
        var nested = false;
        reader.Read();
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == startDepth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                nested = true;
                reader.Skip(); // advances past the child subtree to the next node
                continue;
            }
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                text.Append(reader.Value);
            }
            if (!reader.Read())
            {
                break;
            }
        }
        reader.Read(); // consume the end tag, positioning on the next sibling
        return (name, nested ? "<nested>" : text.ToString());
    }

    /// <summary>
    /// AWS JSON-protocol error envelope. Top-level properties are projected onto
    /// the same canonical surface as the XML path:
    /// <list type="bullet">
    ///   <item><c>__type</c> → <c>Code</c>, reduced to the short error-code name
    ///   (the namespace prefix before <c>#</c> / after the last <c>.</c> is not
    ///   contracted — SDK clients dispatch on the short name).</item>
    ///   <item><c>message</c>/<c>Message</c> → <c>Message</c> (masked: the wording
    ///   is non-contractual).</item>
    ///   <item>volatile correlation ids (RequestId, …) are masked via policy.</item>
    ///   <item>nested object/array values degrade to the <c>&lt;nested&gt;</c>
    ///   marker, matching the XML path.</item>
    /// </list>
    /// </summary>
    private static (string Kind, List<CanonicalField> Fields) ParseJsonError(
        string body, CanonicalizationPolicy policy)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            // Arrays / scalars are not an AWS error-envelope shape; surface them
            // opaquely rather than masquerading as a structured error.
            return (CanonicalResponse.BodyKindOpaque,
                new List<CanonicalField> { new("(raw)", body.Trim()) });
        }

        var fields = new List<CanonicalField>();
        foreach (var prop in root.EnumerateObject())
        {
            var name = NormalizeJsonFieldName(prop.Name);
            string value;
            if (name == "Code")
            {
                value = ShortErrorCode(prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : JsonValueToString(prop.Value));
            }
            else if (policy.VolatileBodyElements.Contains(name)
                     || policy.NonContractualBodyElements.Contains(name))
            {
                value = CanonicalResponse.Masked;
            }
            else
            {
                value = JsonValueToString(prop.Value);
            }
            fields.Add(new CanonicalField(name, value));
        }

        return (CanonicalResponse.BodyKindJsonError, fields);
    }

    /// <summary>
    /// Maps an AWS JSON error property name onto the canonical field name shared
    /// with the XML envelope: <c>__type</c> → <c>Code</c>, <c>message</c> →
    /// <c>Message</c>. All other names pass through unchanged.
    /// </summary>
    private static string NormalizeJsonFieldName(string name) => name switch
    {
        "__type" => "Code",
        _ when string.Equals(name, "message", StringComparison.OrdinalIgnoreCase) => "Message",
        _ => name,
    };

    /// <summary>
    /// Reduces a JSON <c>__type</c> value to the short error-code name AWS SDK
    /// clients dispatch on, stripping the namespace prefix
    /// (<c>com.amazonaws.dynamodb.v20120810#ResourceNotFoundException</c> →
    /// <c>ResourceNotFoundException</c>).
    /// </summary>
    private static string ShortErrorCode(string type)
    {
        var s = type.Trim();
        var hash = s.LastIndexOf('#');
        if (hash >= 0)
        {
            s = s[(hash + 1)..];
        }
        var dot = s.LastIndexOf('.');
        if (dot >= 0)
        {
            s = s[(dot + 1)..];
        }
        return s.Trim();
    }

    private static string JsonValueToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => (value.GetString() ?? string.Empty).Trim(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        // Nested object/array: surface the structural marker, mirroring the XML
        // path, so the presence of a nested member is part of the contract
        // surface even when its inner shape is not.
        _ => "<nested>",
    };

    private static string NormalizeHeaderValue(string name, string value)
    {
        var trimmed = value.Trim();
        if (name == "content-type")
        {
            // Compare the base media type (and any non-charset parameters). The
            // charset parameter is not part of the AWS error wire contract that
            // SDK clients depend on — they parse the XML error body regardless —
            // so it is normalized out rather than surfaced as a divergence. This
            // keeps a genuine media-type regression (e.g. application/json or
            // text/html on an error) detectable via header-value:content-type
            // without a blunt waiver having to bless every content-type value.
            var parts = trimmed.Split(';');
            var kept = new List<string>(parts.Length);
            foreach (var raw in parts)
            {
                var part = raw.Trim().ToLowerInvariant();
                if (part.Length == 0 || part.StartsWith("charset=", StringComparison.Ordinal))
                {
                    continue;
                }
                kept.Add(part);
            }
            return string.Join("; ", kept);
        }
        return trimmed;
    }

    private static int CompareFields(CanonicalField a, CanonicalField b)
    {
        var n = string.CompareOrdinal(a.Name, b.Name);
        return n != 0 ? n : string.CompareOrdinal(a.Value, b.Value);
    }
}
