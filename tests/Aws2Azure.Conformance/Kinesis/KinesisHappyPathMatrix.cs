using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.Kinesis;

/// <summary>
/// Seed Kinesis happy-path matrix for issue #708. Kinesis differs from the CRUD-
/// centric services here: stream lifecycle is provisioned outside the proxy, so
/// the meaningful success paths revolve around writing records, reading them
/// back, and paginating shard metadata. The current Tier-1 fixture has no Event
/// Hubs oracle, so the cases are presently deferred after plan validation.
/// </summary>
public static class KinesisHappyPathMatrix
{
    private static readonly Uri DefaultBaseAddress = new("http://kinesis.us-east-1.amazonaws.com/");

    private const string Tier1SkipReason =
        "Tier-1 Kinesis happy-path replay is deferred by issue #708: KinesisConformanceFixture " +
        "uses dummy Event Hubs credentials, and a real success round-trip needs a provisioned stream/backend.";

    public static IReadOnlyList<IConformanceCase> Cases { get; } =
    [
        CreateRoundTripCase(),
        CreatePaginationCase(),
        CreateBatchCase(),
    ];

    private static PlannedConformanceCase CreateRoundTripCase()
        => new(
            "put-record-then-get-records-roundtrip",
            "kinesis:PutRecord/GetShardIterator/GetRecords",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("SequenceNumber", "Present on PutRecord success.")]),
                new(200, RequiredBodyAssertions: [new("ShardIterator", "Present on GetShardIterator success.")]),
                new(200, RequiredBodyAssertions: [new("Records", "Contains the record written by PutRecord.")]),
            ],
            semanticAssertion:
            "The record read back from GetRecords must preserve the bytes and partition key written by PutRecord."),
            static (context, _) =>
            {
                var stream = context.GetProperty("streamName") ?? "conformance-stream";
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("aws2azure kinesis roundtrip"));
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("put-record", _ => BuildRequest(context, "PutRecord",
                        $$"""{"StreamName":"{{stream}}","PartitionKey":"pk-1","Data":"{{payload}}"}""")),
                    new ConformanceRequestStep("get-shard-iterator", state =>
                    {
                        var shardId = state.RequireJsonString("put-record", "ShardId");
                        var sequenceNumber = state.RequireJsonString("put-record", "SequenceNumber");
                        return BuildRequest(context, "GetShardIterator",
                            $$"""{"StreamName":"{{stream}}","ShardId":"{{shardId}}","ShardIteratorType":"AT_SEQUENCE_NUMBER","StartingSequenceNumber":"{{sequenceNumber}}"}""");
                    }),
                    new ConformanceRequestStep("get-records", state =>
                    {
                        var iterator = state.RequireJsonString("get-shard-iterator", "ShardIterator");
                        return BuildRequest(context, "GetRecords",
                            $$"""{"ShardIterator":"{{iterator}}","Limit":10}""");
                    }),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePaginationCase()
        => new(
            "list-shards-pagination",
            "kinesis:ListShards",
            ConformanceCaseExpectation.Success(
            [
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("NextToken", "Present when MaxResults truncates the first shard page."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("Shards", "Returns the remaining shard metadata on the follow-up page."),
                    ]),
            ],
            semanticAssertion:
            "Across both pages the harness should observe the full shard set exactly once."),
            static (context, _) =>
            {
                var stream = context.GetProperty("streamName") ?? "conformance-stream";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("list-shards-page-1", _ => BuildRequest(context, "ListShards",
                        $$"""{"StreamName":"{{stream}}","MaxResults":1}""")),
                    new ConformanceRequestStep("list-shards-page-2", state =>
                    {
                        var token = state.RequireJsonString("list-shards-page-1", "NextToken");
                        return BuildRequest(context, "ListShards",
                            $$"""{"NextToken":"{{token}}","MaxResults":1}""");
                    }),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateBatchCase()
        => new(
            "put-records-batch-success",
            "kinesis:PutRecords",
            ConformanceCaseExpectation.Success(
            [
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("FailedRecordCount", "Equals zero when every entry succeeds."),
                        new("Records[0].SequenceNumber", "Present for the first record."),
                        new("Records[1].SequenceNumber", "Present for the second record."),
                    ]),
            ],
            semanticAssertion:
            "Every PutRecords entry should succeed and return its own sequence number/shard assignment."),
            static (context, _) =>
            {
                var stream = context.GetProperty("streamName") ?? "conformance-stream";
                var first = Convert.ToBase64String(Encoding.UTF8.GetBytes("first batch payload"));
                var second = Convert.ToBase64String(Encoding.UTF8.GetBytes("second batch payload"));
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("put-records", _ => BuildRequest(context, "PutRecords",
                        $$"""{"StreamName":"{{stream}}","Records":[{"Data":"{{first}}","PartitionKey":"pk-a"},{"Data":"{{second}}","PartitionKey":"pk-b"}]}""")),
                ], Tier1SkipReason));
            });

    private static HttpRequestMessage BuildRequest(
        ConformanceCaseContext context,
        string operation,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ResolveBaseAddress(context), "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-amz-json-1.1");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", "Kinesis_20131202." + operation);
        ConformanceSigV4Signer.SignHeader(
            request,
            bytes,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            service: "kinesis",
            extraSignedHeaders: ["x-amz-target"],
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
