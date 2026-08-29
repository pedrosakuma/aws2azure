using System;
using System.Collections.Generic;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsParameterParsing
{
    internal static bool TryGetRequiredNonEmptyParameter(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out string value,
        out string? error,
        bool rejectWhitespace = false)
    {
        if (!parameters.TryGetValue(name, out value!)
            || (rejectWhitespace ? string.IsNullOrWhiteSpace(value) : string.IsNullOrEmpty(value)))
        {
            value = string.Empty;
            error = $"Parameter '{name}' is required and must not be empty.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Reads the topic ARN for Publish, accepting the legacy TargetArn
    /// parameter as a fallback alias for TopicArn. Real AWS SNS's Publish
    /// API has accepted TargetArn as a backward-compatible alias for
    /// publishing to a topic since before TopicArn existed (TargetArn
    /// predates TopicArn and is still what Airflow's SnsPublishOperator,
    /// and other older SNS clients, send). aws2azure only supports the
    /// topic-publish use case (not mobile push endpoints), so any non-empty
    /// TargetArn here is treated as a topic ARN.
    /// </summary>
    internal static bool TryGetTopicArnParameter(
        IReadOnlyDictionary<string, string> parameters,
        out string value,
        out string? error)
    {
        if (TryGetRequiredNonEmptyParameter(parameters, "TopicArn", out value, out error))
        {
            return true;
        }

        if (TryGetRequiredNonEmptyParameter(parameters, "TargetArn", out value, out error))
        {
            return true;
        }

        error = "Parameter 'TopicArn' (or 'TargetArn') is required and must not be empty.";
        return false;
    }


    internal static bool TryExtractEntryIndex(string key, string prefix, out int index)
    {
        index = 0;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var remaining = key.AsSpan(prefix.Length);
        var separator = remaining.IndexOf('.');
        if (separator <= 0)
            return false;

        return int.TryParse(remaining[..separator], out index);
    }
}
