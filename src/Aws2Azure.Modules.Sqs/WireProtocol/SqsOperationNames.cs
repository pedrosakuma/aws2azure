using System;
using System.Collections.Generic;

namespace Aws2Azure.Modules.Sqs.WireProtocol;

/// <summary>
/// Maps SQS action names (case-sensitive, matches AWS docs) to the
/// internal <see cref="SqsOperation"/> enum. Lookup is done at request
/// time; the table is intentionally a static readonly dictionary so the
/// compiler/runtime can keep it on the heap once.
/// </summary>
public static class SqsOperationNames
{
    private static readonly Dictionary<string, SqsOperation> _byName = new(StringComparer.Ordinal)
    {
        ["CreateQueue"] = SqsOperation.CreateQueue,
        ["DeleteQueue"] = SqsOperation.DeleteQueue,
        ["ListQueues"] = SqsOperation.ListQueues,
        ["GetQueueUrl"] = SqsOperation.GetQueueUrl,
        ["GetQueueAttributes"] = SqsOperation.GetQueueAttributes,
        ["SetQueueAttributes"] = SqsOperation.SetQueueAttributes,
        ["PurgeQueue"] = SqsOperation.PurgeQueue,
        ["SendMessage"] = SqsOperation.SendMessage,
        ["SendMessageBatch"] = SqsOperation.SendMessageBatch,
        ["ReceiveMessage"] = SqsOperation.ReceiveMessage,
        ["DeleteMessage"] = SqsOperation.DeleteMessage,
        ["DeleteMessageBatch"] = SqsOperation.DeleteMessageBatch,
        ["ChangeMessageVisibility"] = SqsOperation.ChangeMessageVisibility,
        ["ChangeMessageVisibilityBatch"] = SqsOperation.ChangeMessageVisibilityBatch,
        ["ListQueueTags"] = SqsOperation.ListQueueTags,
        ["TagQueue"] = SqsOperation.TagQueue,
        ["UntagQueue"] = SqsOperation.UntagQueue,
        ["AddPermission"] = SqsOperation.AddPermission,
        ["RemovePermission"] = SqsOperation.RemovePermission,
        ["ListDeadLetterSourceQueues"] = SqsOperation.ListDeadLetterSourceQueues,
    };

    public static SqsOperation Resolve(string? actionName)
    {
        if (string.IsNullOrEmpty(actionName)) return SqsOperation.Unknown;
        return _byName.TryGetValue(actionName, out var op) ? op : SqsOperation.Unknown;
    }

    public static string ToName(SqsOperation op) => op.ToString();

    /// <summary>
    /// All recognised SQS action names (the parse-table keys). The module's
    /// <c>KnownOperations</c> metrics allowlist is derived from this so the two
    /// can never drift: every parseable action is labelled by name (including
    /// ones that are accepted by the parser but not yet handled), and any
    /// unrecognised action still collapses to <c>"unknown"</c>.
    /// </summary>
    public static IReadOnlyCollection<string> Names => _byName.Keys;
}
