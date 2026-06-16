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
