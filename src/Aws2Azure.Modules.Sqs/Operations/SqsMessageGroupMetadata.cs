using System.Collections.Generic;
using System.Net.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsMessageGroupMetadata
{
    internal const string PropertyName = "AWS.Aws2Azure.MessageGroupId";

    internal static string? Read(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(PropertyName, out var values))
            return null;

        foreach (var value in values)
            return value;

        return null;
    }

    internal static string? Read(IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null ||
            !properties.TryGetValue(PropertyName, out var value) ||
            value is not string messageGroupId)
        {
            return null;
        }

        return messageGroupId;
    }
}
