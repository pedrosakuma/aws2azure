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
        ["ConfirmSubscription"] = SnsOperation.ConfirmSubscription,
        ["GetTopicAttributes"] = SnsOperation.GetTopicAttributes,
        ["SetTopicAttributes"] = SnsOperation.SetTopicAttributes,
        ["GetSubscriptionAttributes"] = SnsOperation.GetSubscriptionAttributes,
        ["SetSubscriptionAttributes"] = SnsOperation.SetSubscriptionAttributes,
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

    /// <summary>
    /// All recognised SNS action names (the parse-table keys). The module's
    /// <c>KnownOperations</c> metrics allowlist is derived from this so the two
    /// can never drift: every parseable action is labelled by name, and any
    /// unrecognised action still collapses to <c>"unknown"</c>.
    /// </summary>
    public static IReadOnlyCollection<string> Names => Map.Keys;
}
