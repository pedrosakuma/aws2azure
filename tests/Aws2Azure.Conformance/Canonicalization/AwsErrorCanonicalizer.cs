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
        var looksXml = baseType is "application/xml" or "text/xml"
            || body.TrimStart().StartsWith('<');
        if (!looksXml)
        {
            return (CanonicalResponse.BodyKindOpaque,
                new List<CanonicalField> { new("(raw)", body.Trim()) });
        }

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

        // Note: ReadElementContentAsString advances the reader past the element's
        // end tag to the next node, so the loop must NOT call Read() again after a
        // successful read — doing so would skip every other sibling element.
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

            var name = reader.LocalName;
            string rawValue;
            try
            {
                rawValue = reader.ReadElementContentAsString();
            }
            catch (InvalidOperationException)
            {
                // Element has nested element children (rare for error envelopes):
                // record presence with a structural marker and skip its subtree.
                rawValue = "<nested>";
                reader.Skip();
            }

            var value = policy.VolatileBodyElements.Contains(name)
                        || policy.NonContractualBodyElements.Contains(name)
                ? CanonicalResponse.Masked
                : rawValue.Trim();
            fields.Add(new CanonicalField(name, value));
        }

        return fields;
    }

    private static string NormalizeHeaderValue(string name, string value)
    {
        var trimmed = value.Trim();
        if (name == "content-type")
        {
            // "application/xml; charset=utf-8" -> "application/xml; charset=utf-8"
            // (lowercased, single space after each ';'). Charset differences are
            // preserved deliberately — they are a real, if minor, divergence.
            var parts = trimmed.Split(';');
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim().ToLowerInvariant();
            }
            return string.Join("; ", parts);
        }
        return trimmed;
    }

    private static int CompareFields(CanonicalField a, CanonicalField b)
    {
        var n = string.CompareOrdinal(a.Name, b.Name);
        return n != 0 ? n : string.CompareOrdinal(a.Value, b.Value);
    }
}
