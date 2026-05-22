using System.Collections.Generic;

namespace Aws2Azure.Modules.Sns.WireProtocol;

internal static class SnsOperationNames
{
    private static readonly Dictionary<string, SnsOperation> Map = new(System.StringComparer.Ordinal)
    {
        ["CreateTopic"] = SnsOperation.CreateTopic,
        ["DeleteTopic"] = SnsOperation.DeleteTopic,
        ["ListTopics"] = SnsOperation.ListTopics,
        ["Publish"] = SnsOperation.Publish,
        ["PublishBatch"] = SnsOperation.PublishBatch,
        ["Subscribe"] = SnsOperation.Subscribe,
        ["Unsubscribe"] = SnsOperation.Unsubscribe,
        ["ListSubscriptions"] = SnsOperation.ListSubscriptions,
        ["ListSubscriptionsByTopic"] = SnsOperation.ListSubscriptionsByTopic,
        ["GetTopicAttributes"] = SnsOperation.GetTopicAttributes,
        ["SetTopicAttributes"] = SnsOperation.SetTopicAttributes,
    };

    public static SnsOperation Resolve(string? action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return SnsOperation.Unknown;
        }

        return Map.TryGetValue(action, out var operation)
            ? operation
            : SnsOperation.Unknown;
    }

    public static string ToShortName(SnsOperation operation) => operation.ToString();
}
