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
        bool includeJsonPrimitiveValues = true)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        AddQueryAttributes(parsed, prefix, attributes);
        AddJsonAttributes(parsed, attributes, includeJsonPrimitiveValues);
        return attributes;
    }

    private static void AddQueryAttributes(SqsParseResult parsed, string prefix, Dictionary<string, string> attributes)
    {
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
