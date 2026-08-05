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
                var topicName = "conf-happy-topic-" + Guid.NewGuid().ToString("N")[..12];
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
                var topicName = "conf-happy-topic-" + Guid.NewGuid().ToString("N")[..12];
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
            extraSignedHeaders: ["content-type"]);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
