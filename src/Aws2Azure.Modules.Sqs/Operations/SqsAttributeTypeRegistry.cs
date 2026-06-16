using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sqs.Internal;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsAttributeTypeRegistry
{
    internal const string HeaderName = "Aws2Azure-AttrTypes";

    internal static string Build(IReadOnlyDictionary<string, SqsMessageAttribute> attributes)
    {
        if (attributes.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in attributes)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(kv.Key).Append('=').Append(kv.Value.DataType);
        }
        return sb.ToString();
    }

    internal static IReadOnlyDictionary<string, string> Parse(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(HeaderName, out var values))
            return new Dictionary<string, string>(0);

        return Parse(string.Join(",", values));
    }

    internal static IReadOnlyDictionary<string, string> Parse(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq];
            var type = pair[(eq + 1)..];
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(type))
                dict[name] = type;
        }
        return dict;
    }
}
