using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class AmqpReceiveParameters
{
    // --- Parameter parsing helpers -------------------------------------

    internal static void ParseAttributeNameSets(
        SqsParseResult parsed,
        out HashSet<string>? attributeNames,
        out HashSet<string>? messageAttributeNames) =>
        SqsParameterHelpers.ParseAttributeNameSets(parsed, out attributeNames, out messageAttributeNames);

    internal static HashSet<string>? ParseAttributeNames(SqsParseResult parsed, string prefix)
        => SqsParameterHelpers.ParseAttributeNames(parsed, prefix);

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

}
