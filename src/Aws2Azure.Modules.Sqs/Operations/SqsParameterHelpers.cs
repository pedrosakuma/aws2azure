using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsParameterHelpers
{
    internal static void ParseAttributeNameSets(
        SqsParseResult parsed,
        out HashSet<string>? attributeNames,
        out HashSet<string>? messageAttributeNames)
    {
        var system = new HashSet<string>(StringComparer.Ordinal);
        var message = new HashSet<string>(StringComparer.Ordinal);

        AddQueryAttributeNames(parsed, "AttributeName", system);
        AddQueryAttributeNames(parsed, "MessageAttributeName", message);

        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(parsed.JsonBody);
                AddJsonAttributeNames(doc.RootElement, "AttributeNames", system);
                AddJsonAttributeNames(doc.RootElement, "MessageAttributeNames", message);
            }
            catch (JsonException) { /* protocol parser already validated */ }
        }

        attributeNames = system.Count == 0 ? null : system;
        messageAttributeNames = message.Count == 0 ? null : message;
    }

    internal static HashSet<string>? ParseAttributeNames(SqsParseResult parsed, string prefix)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        AddQueryAttributeNames(parsed, prefix, set);
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(parsed.JsonBody);
                AddJsonAttributeNames(doc.RootElement, prefix + "s", set);
            }
            catch (JsonException) { /* protocol parser already validated */ }
        }
        return set.Count == 0 ? null : set;
    }

    internal static bool TryParseBoundedInt(
        SqsParseResult parsed, string name, int min, int max, int defaultValue, out int value)
    {
        if (!parsed.Parameters.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw))
        {
            value = defaultValue;
            return true;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
            value < min || value > max)
        {
            value = defaultValue;
            return false;
        }
        return true;
    }

    internal static string? ExtractQueueName(SqsParseResult parsed) =>
        parsed.Parameters.TryGetValue("QueueUrl", out var url) ? QueueUrlBuilder.ExtractQueueName(url) : null;

    internal static bool TryGetParam(SqsParseResult parsed, string key, out string value)
    {
        if (parsed.Parameters.TryGetValue(key, out var v) && v is not null)
        {
            value = v;
            return true;
        }
        value = string.Empty;
        return false;
    }

    internal static Task WriteErrorAsync(HttpContext context, SqsWireProtocol protocol, SqsErrorMapping.Mapping mapping) =>
        SqsErrorResponse.WriteAsync(context, protocol, mapping.StatusCode, mapping.Code, mapping.Message, mapping.FaultType);

    private static void AddQueryAttributeNames(SqsParseResult parsed, string prefix, HashSet<string> set)
    {
        var dotPrefix = prefix + ".";
        foreach (var kv in parsed.Parameters)
        {
            if (kv.Key.StartsWith(dotPrefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(kv.Value))
                set.Add(kv.Value);
        }
    }

    private static void AddJsonAttributeNames(JsonElement root, string propertyName, HashSet<string> set)
    {
        if (root.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) set.Add(s);
                }
            }
        }
    }
}
