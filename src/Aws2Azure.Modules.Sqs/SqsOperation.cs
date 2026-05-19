namespace Aws2Azure.Modules.Sqs;

/// <summary>
/// Operations recognised by the SQS module. New ops are appended as later
/// Phase-2 slices implement them. Slice 0 only wires routing + protocol
/// detection: every value is currently dispatched as <see cref="NotImplemented"/>.
/// </summary>
public enum SqsOperation
{
    Unknown,
    Unsupported,

    // Queue lifecycle (Slice 1)
    CreateQueue,
    DeleteQueue,
    ListQueues,
    GetQueueUrl,
    GetQueueAttributes,
    SetQueueAttributes,
    PurgeQueue,

    // Send path (Slice 2)
    SendMessage,
    SendMessageBatch,

    // Receive / visibility (Slice 3-4)
    ReceiveMessage,
    DeleteMessage,
    DeleteMessageBatch,
    ChangeMessageVisibility,
    ChangeMessageVisibilityBatch,

    // Tail (Slice 5)
    ListQueueTags,
    TagQueue,
    UntagQueue,
    AddPermission,
    RemovePermission,
    ListDeadLetterSourceQueues,
}
