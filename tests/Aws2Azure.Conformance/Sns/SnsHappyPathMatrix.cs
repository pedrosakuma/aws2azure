using System.Net.Http.Headers;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.Sns;

/// <summary>
/// Seed SNS happy-path matrix for issue #708. SNS has no meaningful conditional
/// success operation on the AWS Query surface the proxy exposes, so the third
/// seed case uses <c>PublishBatch</c> instead. Tier 1 still skips execution
/// because the current fixture is deliberately offline and cannot reach Service
/// Bus/Event Grid.
/// </summary>
public static class SnsHappyPathMatrix
{
    private static readonly Uri DefaultBaseAddress = new("http://sns.us-east-1.amazonaws.com/");

    private const string Tier1SkipReason =
        "Tier-1 SNS happy-path replay is deferred by issue #708: SnsConformanceFixture " +
        "uses dummy Service Bus credentials and cannot complete a real topic/publish flow offline.";

    public static IReadOnlyList<IConformanceCase> Cases { get; } =
    [
        CreateRoundTripCase(),
        CreatePaginationCase(),
        CreateBatchCase(),
        CreateTopicAttributesRoundTripCase(),
        CreateSubscribeConfirmUnsubscribeRoundTripCase(),
        CreateListSubscriptionsRoundTripCase(),
    ];

    private static PlannedConformanceCase CreateRoundTripCase()
        => new(
            "create-list-delete-topic-roundtrip",
            "sns:CreateTopic/GetTopicAttributes/ListTopics/DeleteTopic",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateTopicResult.TopicArn", "Returned by CreateTopic.")]),
                new(200, RequiredBodyAssertions: [new("GetTopicAttributesResult.Attributes", "Contains attributes for the created topic.")]),
                new(200, RequiredBodyAssertions: [new("ListTopicsResult.Topics.member.TopicArn", "Contains the created topic ARN.")]),
                new(200),
            ],
            semanticAssertion:
            "The topic created in step 1 must be discoverable by both GetTopicAttributes and ListTopics before deletion."),
            static (context, _) =>
            {
                var topicName = context.GetProperty("topicName") ?? ("conf-happy-topic-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-topic", _ => BuildRequest(context, [
                        new("Action", "CreateTopic"),
                        new("Version", "2010-03-31"),
                        new("Name", topicName),
                    ])),
                    new ConformanceRequestStep("get-topic-attributes", state => BuildRequest(context, [
                        new("Action", "GetTopicAttributes"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                    new ConformanceRequestStep("list-topics", _ => BuildRequest(context, [
                        new("Action", "ListTopics"),
                        new("Version", "2010-03-31"),
                    ])),
                    new ConformanceRequestStep("delete-topic", state => BuildRequest(context, [
                        new("Action", "DeleteTopic"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePaginationCase()
        => new(
            "list-topics-pagination",
            "sns:ListTopics",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("ListTopicsResult.NextToken", "Present when the fixture pre-seeds 101+ topics and page 1 truncates at AWS SNS's fixed 100-item page size.")]),
                new(200, RequiredBodyAssertions: [new("ListTopicsResult.Topics.member.TopicArn", "Returns the remaining topic ARNs.")]),
            ],
            semanticAssertion:
            "Across both pages the harness should observe every pre-seeded topic ARN exactly once; the future fixture must provision at least 101 topics because AWS SNS exposes no MaxResults control."),
            static (context, _) =>
            {
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("list-topics-page-1", _ => BuildRequest(context, [
                        new("Action", "ListTopics"),
                        new("Version", "2010-03-31"),
                    ])),
                    new ConformanceRequestStep("list-topics-page-2", state => BuildRequest(context, [
                        new("Action", "ListTopics"),
                        new("Version", "2010-03-31"),
                        new("NextToken", state.RequireXmlValue("list-topics-page-1", "NextToken")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateBatchCase()
        => new(
            "publish-batch-success",
            "sns:CreateTopic/PublishBatch/DeleteTopic",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateTopicResult.TopicArn", "Returned by CreateTopic.")]),
                new(200, RequiredBodyAssertions: [new("PublishBatchResult.Successful.member", "Contains one success record per published entry.")]),
                new(200),
            ],
            semanticAssertion:
            "PublishBatch should report each entry as successful without partial failures."),
            static (context, _) =>
            {
                var topicName = context.GetProperty("topicName") ?? ("conf-happy-topic-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-topic", _ => BuildRequest(context, [
                        new("Action", "CreateTopic"),
                        new("Version", "2010-03-31"),
                        new("Name", topicName),
                    ])),
                    new ConformanceRequestStep("publish-batch", state => BuildRequest(context, [
                        new("Action", "PublishBatch"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                        new("PublishBatchRequestEntries.member.1.Id", "a"),
                        new("PublishBatchRequestEntries.member.1.Message", "hello"),
                        new("PublishBatchRequestEntries.member.2.Id", "b"),
                        new("PublishBatchRequestEntries.member.2.Message", "world"),
                    ])),
                    new ConformanceRequestStep("delete-topic", state => BuildRequest(context, [
                        new("Action", "DeleteTopic"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateTopicAttributesRoundTripCase()
        => new(
            "topic-attributes-roundtrip",
            "sns:CreateTopic/GetTopicAttributes/SetTopicAttributes/GetTopicAttributes/DeleteTopic",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateTopicResult.TopicArn", "Returned by CreateTopic.")]),
                new(200, RequiredBodyAssertions: [new("GetTopicAttributesResult.Attributes", "Contains default attributes (TopicArn, Owner, DisplayName) for the newly created topic.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("GetTopicAttributesResult.Attributes", "Reflects the DisplayName value written by SetTopicAttributes.")]),
                new(200),
            ],
            semanticAssertion:
            "GetTopicAttributes must reflect the DisplayName written by the intervening SetTopicAttributes call before the topic is deleted."),
            static (context, _) =>
            {
                var topicName = context.GetProperty("topicName") ?? ("conf-happy-topic-" + Guid.NewGuid().ToString("N")[..12]);
                var updatedDisplayName = "conf-happy-display-" + Guid.NewGuid().ToString("N")[..8];
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-topic", _ => BuildRequest(context, [
                        new("Action", "CreateTopic"),
                        new("Version", "2010-03-31"),
                        new("Name", topicName),
                    ])),
                    new ConformanceRequestStep("get-topic-attributes-before", state => BuildRequest(context, [
                        new("Action", "GetTopicAttributes"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                    new ConformanceRequestStep("set-topic-attributes", state => BuildRequest(context, [
                        new("Action", "SetTopicAttributes"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                        new("AttributeName", "DisplayName"),
                        new("AttributeValue", updatedDisplayName),
                    ])),
                    new ConformanceRequestStep("get-topic-attributes-after", state => BuildRequest(context, [
                        new("Action", "GetTopicAttributes"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                    new ConformanceRequestStep("delete-topic", state => BuildRequest(context, [
                        new("Action", "DeleteTopic"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateSubscribeConfirmUnsubscribeRoundTripCase()
        => new(
            "subscribe-confirm-unsubscribe-roundtrip",
            "sns:CreateTopic/Subscribe/GetSubscriptionAttributes/SetSubscriptionAttributes/GetSubscriptionAttributes/Unsubscribe/GetSubscriptionAttributes/DeleteTopic",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateTopicResult.TopicArn", "Returned by CreateTopic.")]),
                new(200, RequiredBodyAssertions: [new("SubscribeResult.SubscriptionArn", "sqs-protocol subscriptions are auto-confirmed immediately; no ConfirmSubscription call is required or possible without an out-of-band token (see docs/gaps/sns/Subscribe.yaml).")]),
                new(200, RequiredBodyAssertions: [new("GetSubscriptionAttributesResult.Attributes", "Contains the auto-confirmed subscription's attributes.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("GetSubscriptionAttributesResult.Attributes", "Reflects the RawMessageDelivery value written by SetSubscriptionAttributes.")]),
                new(200),
                new(404, Notes: "Unsubscribe deletes the subscription immediately at the API level; a follow-up GetSubscriptionAttributes correctly rejects with NotFound (confirmed against real AWS - unlike ListSubscriptionsByTopic, which keeps a stale 'Deleted' sentinel entry for up to ~72h and is therefore intentionally not used here as the teardown check)."),
                new(200),
            ],
            semanticAssertion:
            "The sqs-protocol subscription created in step 2 is auto-confirmed (no ConfirmSubscription call needed per docs/gaps/sns/Subscribe.yaml), " +
            "GetSubscriptionAttributes must reflect the RawMessageDelivery toggle written by SetSubscriptionAttributes, and GetSubscriptionAttributes " +
            "must reject with NotFound once Unsubscribe completes (ListSubscriptionsByTopic is intentionally not used for this check because real AWS " +
            "retains a 'Deleted'-sentinel entry there for up to ~72h after Unsubscribe, which would make an immediate-disappearance assertion flaky " +
            "against real AWS)."),
            static (context, _) =>
            {
                var topicName = context.GetProperty("topicName") ?? ("conf-happy-topic-" + Guid.NewGuid().ToString("N")[..12]);
                var endpoint = context.GetProperty("subscriptionEndpoint") ?? BuildStubSqsEndpointArn(context);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-topic", _ => BuildRequest(context, [
                        new("Action", "CreateTopic"),
                        new("Version", "2010-03-31"),
                        new("Name", topicName),
                    ])),
                    new ConformanceRequestStep("subscribe", state => BuildRequest(context, [
                        new("Action", "Subscribe"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                        new("Protocol", "sqs"),
                        new("Endpoint", endpoint),
                    ])),
                    new ConformanceRequestStep("get-subscription-attributes-before", state => BuildRequest(context, [
                        new("Action", "GetSubscriptionAttributes"),
                        new("Version", "2010-03-31"),
                        new("SubscriptionArn", state.RequireXmlValue("subscribe", "SubscriptionArn")),
                    ])),
                    new ConformanceRequestStep("set-subscription-attributes", state => BuildRequest(context, [
                        new("Action", "SetSubscriptionAttributes"),
                        new("Version", "2010-03-31"),
                        new("SubscriptionArn", state.RequireXmlValue("subscribe", "SubscriptionArn")),
                        new("AttributeName", "RawMessageDelivery"),
                        new("AttributeValue", "true"),
                    ])),
                    new ConformanceRequestStep("get-subscription-attributes-after", state => BuildRequest(context, [
                        new("Action", "GetSubscriptionAttributes"),
                        new("Version", "2010-03-31"),
                        new("SubscriptionArn", state.RequireXmlValue("subscribe", "SubscriptionArn")),
                    ])),
                    new ConformanceRequestStep("unsubscribe", state => BuildRequest(context, [
                        new("Action", "Unsubscribe"),
                        new("Version", "2010-03-31"),
                        new("SubscriptionArn", state.RequireXmlValue("subscribe", "SubscriptionArn")),
                    ])),
                    new ConformanceRequestStep("get-subscription-attributes-after-unsubscribe", state => BuildRequest(context, [
                        new("Action", "GetSubscriptionAttributes"),
                        new("Version", "2010-03-31"),
                        new("SubscriptionArn", state.RequireXmlValue("subscribe", "SubscriptionArn")),
                    ])),
                    new ConformanceRequestStep("delete-topic", state => BuildRequest(context, [
                        new("Action", "DeleteTopic"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateListSubscriptionsRoundTripCase()
        => new(
            "list-subscriptions-roundtrip",
            "sns:CreateTopic/Subscribe/ListSubscriptions/ListSubscriptionsByTopic/Unsubscribe/DeleteTopic",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("CreateTopicResult.TopicArn", "Returned by CreateTopic.")]),
                new(200, RequiredBodyAssertions: [new("SubscribeResult.SubscriptionArn", "sqs-protocol subscriptions are auto-confirmed immediately.")]),
                new(200, RequiredBodyAssertions: [new("ListSubscriptionsResult.Subscriptions.member.SubscriptionArn", "The account-wide listing must include the new subscription.")]),
                new(200, RequiredBodyAssertions: [new("ListSubscriptionsByTopicResult.Subscriptions.member.SubscriptionArn", "The topic-scoped listing must include the same subscription.")]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "Both the account-wide ListSubscriptions and the topic-scoped ListSubscriptionsByTopic must report the subscription created in step 2 " +
            "before it is torn down."),
            static (context, _) =>
            {
                var topicName = context.GetProperty("topicName") ?? ("conf-happy-topic-" + Guid.NewGuid().ToString("N")[..12]);
                var endpoint = context.GetProperty("subscriptionEndpoint") ?? BuildStubSqsEndpointArn(context);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-topic", _ => BuildRequest(context, [
                        new("Action", "CreateTopic"),
                        new("Version", "2010-03-31"),
                        new("Name", topicName),
                    ])),
                    new ConformanceRequestStep("subscribe", state => BuildRequest(context, [
                        new("Action", "Subscribe"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                        new("Protocol", "sqs"),
                        new("Endpoint", endpoint),
                    ])),
                    new ConformanceRequestStep("list-subscriptions", _ => BuildRequest(context, [
                        new("Action", "ListSubscriptions"),
                        new("Version", "2010-03-31"),
                    ])),
                    new ConformanceRequestStep("list-subscriptions-by-topic", state => BuildRequest(context, [
                        new("Action", "ListSubscriptionsByTopic"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                    new ConformanceRequestStep("unsubscribe", state => BuildRequest(context, [
                        new("Action", "Unsubscribe"),
                        new("Version", "2010-03-31"),
                        new("SubscriptionArn", state.RequireXmlValue("subscribe", "SubscriptionArn")),
                    ])),
                    new ConformanceRequestStep("delete-topic", state => BuildRequest(context, [
                        new("Action", "DeleteTopic"),
                        new("Version", "2010-03-31"),
                        new("TopicArn", state.RequireXmlValue("create-topic", "TopicArn")),
                    ])),
                ], Tier1SkipReason));
            });

    // Subscribe's sqs protocol only needs a syntactically valid SQS ARN; the
    // proxy stores the endpoint as opaque subscription metadata and never
    // dispatches to it (see docs/gaps/sns/Subscribe.yaml: aws2azure is
    // publish-only and does not implement active delivery).
    private static string BuildStubSqsEndpointArn(ConformanceCaseContext context)
        => $"arn:aws:sqs:{context.Region}:000000000000:conf-happy-queue-" + Guid.NewGuid().ToString("N")[..12];

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
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
        {
            CharSet = "utf-8",
        };
        ConformanceSigV4Signer.SignHeader(
            request,
            payload,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            service: "sns",
            extraSignedHeaders: ["content-type"],
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
