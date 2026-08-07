using System.Net.Http.Headers;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.Sqs;

/// <summary>
/// Seed SQS happy-path matrix for issue #708. Like SNS, SQS has no meaningful
/// conditional success operation on the proxy's exposed surface, so the third
/// case focuses on batch semantics instead. Tier 1 still skips execution because
/// the fixture is offline and configured with dummy Service Bus credentials.
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
