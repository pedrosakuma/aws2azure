using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.DynamoDb;

/// <summary>
/// Seed DynamoDB happy-path matrix for issue #708. The current Tier-1 fixture is
/// intentionally offline and configured with a dummy Cosmos endpoint, so these
/// cases are skipped at execution time today; their value in this PR is that the
/// request sequence, dynamic pagination token flow, and success expectations now
/// live in the same generic case model as the existing error matrix.
///
/// <para>
/// The seeded cases intentionally operate at the <em>item</em> level against a
/// fixture-provisioned table. Real DynamoDB-style table activation is
/// asynchronous, so baking <c>CreateTable → PutItem</c> into the same live plan
/// would be flaky without an out-of-band waiter. Future Tier-3 fixtures should
/// therefore provision an empty table, pass its name via
/// <c>ConformanceCaseContext.Properties["tableName"]</c>, and clean it up around
/// these per-item scenarios.
/// </para>
/// </summary>
public static class DynamoDbHappyPathMatrix
{
    private static readonly Uri DefaultBaseAddress = new("http://dynamodb.us-east-1.amazonaws.com/");

    private const string Tier1SkipReason =
        "Tier-1 DynamoDB happy-path replay is deferred by issue #708: DynamoDbConformanceFixture " +
        "targets a dummy Cosmos endpoint and cannot prove successful round-trips offline.";

    public static IReadOnlyList<IConformanceCase> Cases { get; } =
    [
        CreateRoundTripCase(),
        CreatePaginationCase(),
        CreateConditionalCase(),
    ];

    private static PlannedConformanceCase CreateRoundTripCase()
        => new(
            "put-get-delete-item-roundtrip",
            "dynamodb:PutItem/GetItem/DeleteItem",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(200, RequiredBodyAssertions: [new("Item.pk.S", "Equals the seeded partition key."), new("Item.payload.S", "Equals the seeded payload.")]),
                new(200),
            ],
            semanticAssertion:
            "The item read by GetItem must preserve the attribute values written by PutItem. The future Tier-3 fixture must provide an empty table via the conformance context."),
            static (context, _) =>
            {
                var table = context.GetProperty("tableName") ?? "conformance-table";
                const string key = "pk-1";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("put-item", _ => BuildRequest(context, "PutItem",
                        $@"{{""TableName"":""{table}"",""Item"":{{""pk"":{{""S"":""{key}""}},""payload"":{{""S"":""roundtrip""}}}}}}")),
                    new ConformanceRequestStep("get-item", _ => BuildRequest(context, "GetItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{key}""}}}},""ConsistentRead"":true}}")),
                    new ConformanceRequestStep("delete-item", _ => BuildRequest(context, "DeleteItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{key}""}}}}}}")),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePaginationCase()
        => new(
            "scan-pagination",
            "dynamodb:PutItem/Scan/DeleteItem",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(200),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("LastEvaluatedKey", "Present when Limit=1 truncates the first page."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("Items", "Contains the remaining seeded item(s) on the follow-up page."),
                    ]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "The union of both Scan pages must contain each seeded item exactly once. The future Tier-3 fixture must provide an empty table via the conformance context."),
            static (context, _) =>
            {
                var table = context.GetProperty("tableName") ?? "conformance-table";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("seed-item-1", _ => BuildRequest(context, "PutItem",
                        $@"{{""TableName"":""{table}"",""Item"":{{""pk"":{{""S"":""pk-1""}},""payload"":{{""S"":""first""}}}}}}")),
                    new ConformanceRequestStep("seed-item-2", _ => BuildRequest(context, "PutItem",
                        $@"{{""TableName"":""{table}"",""Item"":{{""pk"":{{""S"":""pk-2""}},""payload"":{{""S"":""second""}}}}}}")),
                    new ConformanceRequestStep("scan-page-1", _ => BuildRequest(context, "Scan",
                        $@"{{""TableName"":""{table}"",""Limit"":1,""ConsistentRead"":true}}")),
                    new ConformanceRequestStep("scan-page-2", state =>
                    {
                        var lastKey = state.RequireJsonRaw("scan-page-1", "LastEvaluatedKey");
                        return BuildRequest(context, "Scan",
                            $@"{{""TableName"":""{table}"",""Limit"":1,""ConsistentRead"":true,""ExclusiveStartKey"":{lastKey}}}");
                    }),
                    new ConformanceRequestStep("delete-item-1", _ => BuildRequest(context, "DeleteItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""pk-1""}}}}}}")),
                    new ConformanceRequestStep("delete-item-2", _ => BuildRequest(context, "DeleteItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""pk-2""}}}}}}")),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateConditionalCase()
        => new(
            "put-item-condition-expression-success",
            "dynamodb:PutItem[ConditionExpression]/GetItem/DeleteItem",
            ConformanceCaseExpectation.Success(
            [
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("Attributes", "Absent unless ReturnValues is requested; the write succeeds because the key was absent."),
                    ]),
                new(200, RequiredBodyAssertions: [new("Item.payload.S", "Equals the conditionally-written payload.")]),
                new(200),
            ],
            semanticAssertion:
            "The conditional PutItem must succeed only because attribute_not_exists(pk) is true for the first write. The future Tier-3 fixture must provide an empty table via the conformance context."),
            static (context, _) =>
            {
                var table = context.GetProperty("tableName") ?? "conformance-table";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("conditional-put", _ => BuildRequest(context, "PutItem",
                        $@"{{""TableName"":""{table}"",""Item"":{{""pk"":{{""S"":""pk-conditional""}},""payload"":{{""S"":""conditional""}}}},""ConditionExpression"":""attribute_not_exists(pk)""}}")),
                    new ConformanceRequestStep("get-item", _ => BuildRequest(context, "GetItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""pk-conditional""}}}},""ConsistentRead"":true}}")),
                    new ConformanceRequestStep("delete-item", _ => BuildRequest(context, "DeleteItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""pk-conditional""}}}}}}")),
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
            new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", "DynamoDB_20120810." + operation);
        ConformanceSigV4Signer.SignHeader(
            request,
            bytes,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            service: "dynamodb",
            extraSignedHeaders: ["x-amz-target"],
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
