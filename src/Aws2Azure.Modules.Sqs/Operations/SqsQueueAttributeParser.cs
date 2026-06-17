using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Aws2Azure.Modules.Sqs.WireProtocol;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsQueueAttributeParser
{
    internal static IReadOnlyDictionary<string, string> ExtractAttributes(
        SqsParseResult parsed,
        string prefix,
        bool includeJsonPrimitiveValues = true,
        bool contiguousQueryIndexes = false,
        bool jsonAttributesWin = false)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (jsonAttributesWin)
        {
            // CreateQueue historically read the JSON "Attributes" object first and,
            // when it yielded at least one attribute, returned exclusively those —
            // ignoring any (malformed) Attribute.<n> query pairs, including ones the
            // wire parser projects from dotted top-level JSON properties.
            AddJsonAttributes(parsed, attributes, includeJsonPrimitiveValues);
            if (attributes.Count > 0)
                return attributes;
            AddQueryAttributes(parsed, prefix, attributes, contiguousQueryIndexes);
            return attributes;
        }

        AddQueryAttributes(parsed, prefix, attributes, contiguousQueryIndexes);
        AddJsonAttributes(parsed, attributes, includeJsonPrimitiveValues);
        return attributes;
    }

    private static void AddQueryAttributes(
        SqsParseResult parsed,
        string prefix,
        Dictionary<string, string> attributes,
        bool contiguousQueryIndexes)
    {
        if (contiguousQueryIndexes)
        {
            // CreateQueue historically consumed only contiguous Attribute.<i>
            // indexes starting at 1, stopping at the first missing Name. Preserve
            // that semantic so sparse indexes (e.g. Attribute.2 with no
            // Attribute.1) are not silently honoured.
            var i = 1;
            while (parsed.Parameters.TryGetValue($"{prefix}.{i}.Name", out var name))
            {
                if (parsed.Parameters.TryGetValue($"{prefix}.{i}.Value", out var value))
                    attributes[name] = value;
                i++;
            }
            return;
        }

        var dotPrefix = prefix + ".";
        var nameByIndex = new SortedDictionary<int, string>();
        var valueByIndex = new SortedDictionary<int, string>();
        foreach (var kv in parsed.Parameters)
        {
            if (!kv.Key.StartsWith(dotPrefix, StringComparison.Ordinal)) continue;
            var rest = kv.Key.AsSpan(dotPrefix.Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                continue;
            var sub = rest[(dot + 1)..];
            if (sub.SequenceEqual("Name")) nameByIndex[idx] = kv.Value;
            else if (sub.SequenceEqual("Value")) valueByIndex[idx] = kv.Value;
        }

        foreach (var (idx, name) in nameByIndex)
        {
            if (valueByIndex.TryGetValue(idx, out var value))
                attributes[name] = value;
        }
    }

    private static void AddJsonAttributes(
        SqsParseResult parsed,
        Dictionary<string, string> attributes,
        bool includePrimitiveValues)
    {
        if (parsed.Protocol != SqsWireProtocol.AwsJson || string.IsNullOrEmpty(parsed.JsonBody))
            return;

        try
        {
            using var doc = JsonDocument.Parse(parsed.JsonBody);
            if (!doc.RootElement.TryGetProperty("Attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                return;

            foreach (var p in attrs.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    attributes[p.Name] = p.Value.GetString() ?? string.Empty;
                else if (includePrimitiveValues && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
                    attributes[p.Name] = p.Value.GetRawText();
            }
        }
        catch (JsonException) { /* protocol parser already validated body */ }
    }
}
