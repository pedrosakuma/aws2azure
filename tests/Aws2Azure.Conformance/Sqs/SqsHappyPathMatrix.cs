using System.Net.Http.Headers;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.Sqs;

/// <summary>
/// Seed SQS happy-path matrix for issue #708. Like SNS, SQS has no meaningful
/// conditional success operation on the proxy's exposed surface, so the third
/// case focuses on batch semantics instead. Tier 1 still skips execution because
/// the fixture is offline and configured with dummy Service Bus credentials.
///
/// <para>Expanded for Tier-3 completeness: tagging (TagQueue/ListQueueTags/
/// UntagQueue), queue metadata (GetQueueUrl/GetQueueAttributes/
/// SetQueueAttributes), single and batch visibility-timeout management
/// (ChangeMessageVisibility[Batch]), PurgeQueue, and the dead-letter-source
/// reverse lookup (ListDeadLetterSourceQueues). <c>AddPermission</c>/
/// <c>RemovePermission</c> are intentionally not covered: both are documented
/// <c>status: stub</c> no-ops (docs/gaps/sqs/AddPermission.yaml,
/// docs/gaps/sqs/RemovePermission.yaml) — the proxy validates queue existence
/// and otherwise silently drops the permission payload, so there is nothing a
/// single-account real-AWS/real-Azure round-trip could meaningfully assert
/// beyond "200 OK", and SQS resource policies only become observable with a
/// second AWS account for cross-account grants.</para>
/// </summary>
public static class SqsHappyPathMatrix
{
    private static readonly Uri DefaultBaseAddress = new("http://sqs.us-east-1.amazonaws.com/");

    private const string Tier1SkipReason =
        "Tier-1 SQS happy-path replay is deferred by issue #708: SqsConformanceFixture " +
        "uses dummy Service Bus credentials and cannot complete a real queue/message round-trip offline.";

    public static IReadOnlyList<IConformanceCase> Cases { get; } =
    [
        CreateRoundTripCase(),
        CreatePaginationCase(),
        CreateBatchCase(),
        CreateQueueTaggingRoundTripCase(),
        CreateQueueAttributesAndUrlRoundTripCase(),
        CreateChangeMessageVisibilityRoundTripCase(),
        CreateChangeMessageVisibilityBatchRoundTripCase(),
        CreatePurgeQueueRoundTripCase(),
        CreateDeadLetterSourceQueuesRoundTripCase(),
    ];

    private static PlannedConformanceCase CreateRoundTripCase()
        => new(
            "create-send-receive-delete-message-roundtrip",
            "sqs:CreateQueue/SendMessage/ReceiveMessage/DeleteMessage/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Returned by CreateQueue.")]),
                new(200, RequiredBodyAssertions: [new("SendMessageResult.MessageId", "Returned by SendMessage.")]),
                new(200, RequiredBodyAssertions: [new("ReceiveMessageResult.Message.ReceiptHandle", "Returned for the enqueued message.")]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "The message received in step 3 must match the body sent in step 2, and its receipt handle must delete successfully."),
            static (context, _) =>
            {
                var queueName = context.GetProperty("queueName") ?? ("conf-happy-queue-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("send-message", state => BuildRequest(context, [
                        new("Action", "SendMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MessageBody", "hello from conformance"),
                    ])),
                    new ConformanceRequestStep("receive-message", state => BuildRequest(context, [
                        new("Action", "ReceiveMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MaxNumberOfMessages", "1"),
                        new("WaitTimeSeconds", "5"),
                    ])),
                    new ConformanceRequestStep("delete-message", state => BuildRequest(context, [
                        new("Action", "DeleteMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("ReceiptHandle", state.RequireXmlValue("receive-message", "ReceiptHandle")),
                    ])),
                    new ConformanceRequestStep("delete-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePaginationCase()
        => new(
            "list-queues-pagination",
            "sqs:CreateQueue/ListQueues/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(200),
                new(200, RequiredBodyAssertions: [new("ListQueuesResult.NextToken", "Present when page 1 is truncated.")]),
                new(200, RequiredBodyAssertions: [new("ListQueuesResult.QueueUrl", "Contains the remaining queue URL(s).")]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "Across both pages the harness should observe every seeded queue URL exactly once."),
            static (context, _) =>
            {
                var first = context.GetProperty("queueName1") ?? ("conf-happy-queue-" + Guid.NewGuid().ToString("N")[..10]);
                var second = context.GetProperty("queueName2") ?? ("conf-happy-queue-" + Guid.NewGuid().ToString("N")[..10]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue-1", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", first),
                    ])),
                    new ConformanceRequestStep("create-queue-2", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", second),
                    ])),
                    new ConformanceRequestStep("list-queues-page-1", _ => BuildRequest(context, [
                        new("Action", "ListQueues"),
                        new("Version", "2012-11-05"),
                        new("MaxResults", "1"),
                    ])),
                    new ConformanceRequestStep("list-queues-page-2", state => BuildRequest(context, [
                        new("Action", "ListQueues"),
                        new("Version", "2012-11-05"),
                        new("MaxResults", "1"),
                        new("NextToken", state.RequireXmlValue("list-queues-page-1", "NextToken")),
                    ])),
                    new ConformanceRequestStep("delete-queue-1", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue-1", "QueueUrl")),
                    ])),
                    new ConformanceRequestStep("delete-queue-2", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue-2", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateBatchCase()
        => new(
            "send-message-batch-success",
            "sqs:CreateQueue/SendMessageBatch/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Returned by CreateQueue.")]),
                new(200, RequiredBodyAssertions: [new("SendMessageBatchResult.SendMessageBatchResultEntry", "Contains one success entry per message.")]),
                new(200),
            ],
            semanticAssertion:
            "SendMessageBatch should report both entries as successful before the queue is deleted."),
            static (context, _) =>
            {
                var queueName = context.GetProperty("queueName3")
                    ?? context.GetProperty("queueName")
                    ?? ("conf-happy-queue-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("send-message-batch", state => BuildRequest(context, [
                        new("Action", "SendMessageBatch"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("SendMessageBatchRequestEntry.1.Id", "a"),
                        new("SendMessageBatchRequestEntry.1.MessageBody", "first"),
                        new("SendMessageBatchRequestEntry.2.Id", "b"),
                        new("SendMessageBatchRequestEntry.2.MessageBody", "second"),
                    ])),
                    new ConformanceRequestStep("delete-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateQueueTaggingRoundTripCase()
        => new(
            "queue-tagging-roundtrip",
            "sqs:CreateQueue/TagQueue/ListQueueTags/UntagQueue/ListQueueTags/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Returned by CreateQueue.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("ListQueueTagsResult.Tag", "Reflects the tags just written by TagQueue.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("ListQueueTagsResult", "Must no longer contain the removed tag key(s) after UntagQueue.")]),
                new(200),
            ],
            semanticAssertion:
            "ListQueueTags must show the tags written by TagQueue, and must no longer show them once UntagQueue removes them."),
            static (context, _) =>
            {
                var queueName = context.GetProperty("queueTaggingQueueName")
                    ?? ("conf-happy-queue-tag-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("tag-queue", state => BuildRequest(context, [
                        new("Action", "TagQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("Tag.1.Key", "conf-owner"),
                        new("Tag.1.Value", "aws2azure-conformance"),
                    ])),
                    new ConformanceRequestStep("list-queue-tags-after-tag", state => BuildRequest(context, [
                        new("Action", "ListQueueTags"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                    new ConformanceRequestStep("untag-queue", state => BuildRequest(context, [
                        new("Action", "UntagQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("TagKey.1", "conf-owner"),
                    ])),
                    new ConformanceRequestStep("list-queue-tags-after-untag", state => BuildRequest(context, [
                        new("Action", "ListQueueTags"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                    new ConformanceRequestStep("delete-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateQueueAttributesAndUrlRoundTripCase()
        => new(
            "queue-attributes-roundtrip",
            "sqs:CreateQueue/GetQueueUrl/GetQueueAttributes/SetQueueAttributes/GetQueueAttributes/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Returned by CreateQueue.")]),
                new(200, RequiredBodyAssertions: [new("GetQueueUrlResult.QueueUrl", "Must resolve to the same queue CreateQueue provisioned.")]),
                new(200, RequiredBodyAssertions: [new("GetQueueAttributesResult.Attribute", "Contains the default VisibilityTimeout/QueueArn.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("GetQueueAttributesResult.Attribute", "VisibilityTimeout reflects the value SetQueueAttributes just wrote.")]),
                new(200),
            ],
            semanticAssertion:
            "GetQueueUrl must resolve the queue created by CreateQueue, and the VisibilityTimeout observed after SetQueueAttributes must match the value that was set (subject to SB LockDuration clamping documented in the ChangeMessageVisibility gap doc)."),
            static (context, _) =>
            {
                var queueName = context.GetProperty("queueAttributesQueueName")
                    ?? ("conf-happy-queue-attr-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("get-queue-url", _ => BuildRequest(context, [
                        new("Action", "GetQueueUrl"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("get-queue-attributes-before", state => BuildRequest(context, [
                        new("Action", "GetQueueAttributes"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("AttributeName.1", "All"),
                    ])),
                    new ConformanceRequestStep("set-queue-attributes", state => BuildRequest(context, [
                        new("Action", "SetQueueAttributes"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("Attribute.1.Name", "VisibilityTimeout"),
                        new("Attribute.1.Value", "60"),
                    ])),
                    new ConformanceRequestStep("get-queue-attributes-after", state => BuildRequest(context, [
                        new("Action", "GetQueueAttributes"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("AttributeName.1", "All"),
                    ])),
                    new ConformanceRequestStep("delete-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateChangeMessageVisibilityRoundTripCase()
        => new(
            "change-message-visibility-roundtrip",
            "sqs:CreateQueue/SendMessage/ReceiveMessage/ChangeMessageVisibility/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Returned by CreateQueue.")]),
                new(200, RequiredBodyAssertions: [new("SendMessageResult.MessageId", "Returned by SendMessage.")]),
                new(200, RequiredBodyAssertions: [new("ReceiveMessageResult.Message.ReceiptHandle", "Returned for the enqueued message.")]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "ChangeMessageVisibility must succeed against the receipt handle returned by ReceiveMessage. Real AWS grants the caller-supplied timeout; the proxy grants Service Bus's queue LockDuration instead (a documented by-design divergence, not asserted here per the ChangeMessageVisibility gap doc's timing caveat)."),
            static (context, _) =>
            {
                var queueName = context.GetProperty("changeVisibilityQueueName")
                    ?? ("conf-happy-queue-cmv-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("send-message", state => BuildRequest(context, [
                        new("Action", "SendMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MessageBody", "conformance-cmv-message"),
                    ])),
                    new ConformanceRequestStep("receive-message", state => BuildRequest(context, [
                        new("Action", "ReceiveMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MaxNumberOfMessages", "1"),
                        new("WaitTimeSeconds", "5"),
                    ])),
                    new ConformanceRequestStep("change-message-visibility", state => BuildRequest(context, [
                        new("Action", "ChangeMessageVisibility"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("ReceiptHandle", state.RequireXmlValue("receive-message", "ReceiptHandle")),
                        new("VisibilityTimeout", "30"),
                    ])),
                    new ConformanceRequestStep("delete-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateChangeMessageVisibilityBatchRoundTripCase()
        => new(
            "change-message-visibility-batch-roundtrip",
            "sqs:CreateQueue/SendMessage/SendMessage/ReceiveMessage/ReceiveMessage/ChangeMessageVisibilityBatch/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Returned by CreateQueue.")]),
                new(200, RequiredBodyAssertions: [new("SendMessageResult.MessageId", "First seeded message.")]),
                new(200, RequiredBodyAssertions: [new("SendMessageResult.MessageId", "Second seeded message.")]),
                new(200, RequiredBodyAssertions: [new("ReceiveMessageResult.Message.ReceiptHandle", "Receipt handle for the first message.")]),
                new(200, RequiredBodyAssertions: [new("ReceiveMessageResult.Message.ReceiptHandle", "Receipt handle for the second message.")]),
                new(200, RequiredBodyAssertions: [new("ChangeMessageVisibilityBatchResult.ChangeMessageVisibilityBatchResultEntry", "Contains one success entry per receipt handle.")]),
                new(200),
            ],
            semanticAssertion:
            "ChangeMessageVisibilityBatch must report both receipt handles as successful entries."),
            static (context, _) =>
            {
                var queueName = context.GetProperty("changeVisibilityBatchQueueName")
                    ?? ("conf-happy-queue-cmvb-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("send-message-1", state => BuildRequest(context, [
                        new("Action", "SendMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MessageBody", "conformance-cmv-batch-message-1"),
                    ])),
                    new ConformanceRequestStep("send-message-2", state => BuildRequest(context, [
                        new("Action", "SendMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MessageBody", "conformance-cmv-batch-message-2"),
                    ])),
                    // Two single-message receives (rather than one MaxNumberOfMessages=2
                    // call) so each step's response contains exactly one ReceiptHandle —
                    // the shared RequireXmlValue helper matches the first element with a
                    // given local name, so a single receive step with multiple messages
                    // would not let this plan address the second handle deterministically.
                    new ConformanceRequestStep("receive-message-1", state => BuildRequest(context, [
                        new("Action", "ReceiveMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MaxNumberOfMessages", "1"),
                        new("WaitTimeSeconds", "5"),
                    ])),
                    new ConformanceRequestStep("receive-message-2", state => BuildRequest(context, [
                        new("Action", "ReceiveMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MaxNumberOfMessages", "1"),
                        new("WaitTimeSeconds", "5"),
                    ])),
                    new ConformanceRequestStep("change-message-visibility-batch", state => BuildRequest(context, [
                        new("Action", "ChangeMessageVisibilityBatch"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("ChangeMessageVisibilityBatchRequestEntry.1.Id", "a"),
                        new("ChangeMessageVisibilityBatchRequestEntry.1.ReceiptHandle", state.RequireXmlValue("receive-message-1", "ReceiptHandle")),
                        new("ChangeMessageVisibilityBatchRequestEntry.1.VisibilityTimeout", "30"),
                        new("ChangeMessageVisibilityBatchRequestEntry.2.Id", "b"),
                        new("ChangeMessageVisibilityBatchRequestEntry.2.ReceiptHandle", state.RequireXmlValue("receive-message-2", "ReceiptHandle")),
                        new("ChangeMessageVisibilityBatchRequestEntry.2.VisibilityTimeout", "30"),
                    ])),
                    new ConformanceRequestStep("delete-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePurgeQueueRoundTripCase()
        => new(
            "purge-queue-roundtrip",
            "sqs:CreateQueue/SendMessage/SendMessage/PurgeQueue/ReceiveMessage/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Returned by CreateQueue.")]),
                new(200, RequiredBodyAssertions: [new("SendMessageResult.MessageId", "First seeded message.")]),
                new(200, RequiredBodyAssertions: [new("SendMessageResult.MessageId", "Second seeded message.")]),
                new(200),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "After PurgeQueue, a follow-up ReceiveMessage must return no messages. Only a single purge is issued per run — AWS enforces a 60s cooldown between purges on the same queue (see PurgeQueue gap doc), so this case must not purge twice."),
            static (context, _) =>
            {
                var queueName = context.GetProperty("purgeQueueName")
                    ?? ("conf-happy-queue-purge-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", queueName),
                    ])),
                    new ConformanceRequestStep("send-message-1", state => BuildRequest(context, [
                        new("Action", "SendMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MessageBody", "conformance-purge-message-1"),
                    ])),
                    new ConformanceRequestStep("send-message-2", state => BuildRequest(context, [
                        new("Action", "SendMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MessageBody", "conformance-purge-message-2"),
                    ])),
                    new ConformanceRequestStep("purge-queue", state => BuildRequest(context, [
                        new("Action", "PurgeQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                    new ConformanceRequestStep("receive-message-after-purge", state => BuildRequest(context, [
                        new("Action", "ReceiveMessage"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                        new("MaxNumberOfMessages", "10"),
                        new("WaitTimeSeconds", "1"),
                    ])),
                    new ConformanceRequestStep("delete-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateDeadLetterSourceQueuesRoundTripCase()
        => new(
            "dead-letter-source-queues-roundtrip",
            "sqs:CreateQueue/CreateQueue/GetQueueAttributes/SetQueueAttributes/ListDeadLetterSourceQueues/DeleteQueue/DeleteQueue",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Dead-letter target queue.")]),
                new(200, RequiredBodyAssertions: [new("CreateQueueResult.QueueUrl", "Source queue that will redrive into the DLQ.")]),
                new(200, RequiredBodyAssertions: [new("GetQueueAttributesResult.Attribute", "Contains the dead-letter queue's QueueArn.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("ListDeadLetterSourceQueuesResult.QueueUrl", "Must contain the source queue's URL.")]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "Once the source queue's RedrivePolicy targets the dead-letter queue, ListDeadLetterSourceQueues called on the dead-letter queue must return the source queue's URL."),
            static (context, _) =>
            {
                var dlqName = context.GetProperty("dlqQueueName")
                    ?? ("conf-happy-queue-dlq-" + Guid.NewGuid().ToString("N")[..10]);
                var sourceName = context.GetProperty("dlqSourceQueueName")
                    ?? ("conf-happy-queue-dlqsrc-" + Guid.NewGuid().ToString("N")[..10]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-dlq-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", dlqName),
                    ])),
                    new ConformanceRequestStep("create-source-queue", _ => BuildRequest(context, [
                        new("Action", "CreateQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueName", sourceName),
                    ])),
                    new ConformanceRequestStep("get-dlq-arn", state => BuildRequest(context, [
                        new("Action", "GetQueueAttributes"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-dlq-queue", "QueueUrl")),
                        new("AttributeName.1", "QueueArn"),
                    ])),
                    new ConformanceRequestStep("set-redrive-policy", state => BuildRequest(context, [
                        new("Action", "SetQueueAttributes"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-source-queue", "QueueUrl")),
                        new("Attribute.1.Name", "RedrivePolicy"),
                        new("Attribute.1.Value",
                            "{\"deadLetterTargetArn\":\"" + state.RequireXmlValue("get-dlq-arn", "Value") + "\",\"maxReceiveCount\":5}"),
                    ])),
                    new ConformanceRequestStep("list-dead-letter-source-queues", state => BuildRequest(context, [
                        new("Action", "ListDeadLetterSourceQueues"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-dlq-queue", "QueueUrl")),
                    ])),
                    new ConformanceRequestStep("delete-source-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-source-queue", "QueueUrl")),
                    ])),
                    new ConformanceRequestStep("delete-dlq-queue", state => BuildRequest(context, [
                        new("Action", "DeleteQueue"),
                        new("Version", "2012-11-05"),
                        new("QueueUrl", state.RequireXmlValue("create-dlq-queue", "QueueUrl")),
                    ])),
                ], Tier1SkipReason));
            });

    private static HttpRequestMessage BuildRequest(
        ConformanceCaseContext context,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        using var form = new FormUrlEncodedContent(parameters);
        var payload = form.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ResolveBaseAddress(context), "/"))
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        ConformanceSigV4Signer.SignHeader(
            request,
            payload,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            service: "sqs",
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
