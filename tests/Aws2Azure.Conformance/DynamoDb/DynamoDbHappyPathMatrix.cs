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
        CreateTableLifecycleCase(),
        CreateBatchGetWriteItemCase(),
        CreateTransactGetWriteItemsCase(),
        CreateUpdateItemCase(),
        CreateTagListUntagResourceCase(),
        CreateDescribeUpdateTimeToLiveCase(),
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

    /// <summary>
    /// CreateTable/DescribeTable/DeleteTable round-trip (issue #708 backlog:
    /// expand DynamoDB Tier-3 coverage beyond the item-level operations).
    /// Unlike the item-level cases above, this case provisions and tears down
    /// its <em>own</em> ephemeral table rather than the shared
    /// <c>tableName</c> fixture property, because <c>DeleteTable</c> at the end
    /// of the plan must not remove the table every other case in this matrix
    /// depends on. The proxy's CreateTable is synchronous (the table is
    /// ACTIVE by the time the response returns, see
    /// docs/gaps/dynamodb/CreateTable.yaml), so no out-of-band waiter is
    /// needed before DescribeTable.
    /// </summary>
    private static PlannedConformanceCase CreateTableLifecycleCase()
        => new(
            "create-describe-delete-table-roundtrip",
            "dynamodb:CreateTable/DescribeTable/DeleteTable",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("TableDescription.TableStatus", "ACTIVE immediately; the proxy's CreateTable is synchronous.")]),
                new(200, RequiredBodyAssertions: [new("Table.TableStatus", "Equals ACTIVE, matching the synchronous CreateTable contract.")]),
                new(200, RequiredBodyAssertions: [new("TableDescription.TableStatus", "DELETING, matching the synchronous-transition contract.")]),
            ],
            semanticAssertion:
            "DescribeTable must report TableStatus=ACTIVE immediately after CreateTable returns, with no polling/waiter needed."),
            static (context, _) =>
            {
                var table = context.GetProperty("createTableName") ?? ("conf-happy-create-table-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-table", _ => BuildRequest(context, "CreateTable",
                        $@"{{""TableName"":""{table}"",""AttributeDefinitions"":[{{""AttributeName"":""pk"",""AttributeType"":""S""}}],""KeySchema"":[{{""AttributeName"":""pk"",""KeyType"":""HASH""}}],""BillingMode"":""PAY_PER_REQUEST""}}")),
                    new ConformanceRequestStep("describe-table", _ => BuildRequest(context, "DescribeTable",
                        $@"{{""TableName"":""{table}""}}")),
                    new ConformanceRequestStep("delete-table", _ => BuildRequest(context, "DeleteTable",
                        $@"{{""TableName"":""{table}""}}")),
                ], Tier1SkipReason));
            });

    /// <summary>
    /// BatchWriteItem/BatchGetItem round-trip against the shared per-run
    /// table. Neither operation constrains keys to a single logical
    /// partition (unlike Transact*), so multiple independent items in one
    /// table is representative of the common shape.
    /// </summary>
    private static PlannedConformanceCase CreateBatchGetWriteItemCase()
        => new(
            "batch-get-write-item-roundtrip",
            "dynamodb:BatchWriteItem/BatchGetItem",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("UnprocessedItems", "Absent/empty; both puts succeed against Cosmos.")]),
                new(200, RequiredBodyAssertions:
                [
                    new("Responses", "Contains both seeded items."),
                    new("UnprocessedKeys", "Absent/empty; both keys resolve on the first attempt."),
                ]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "BatchGetItem must return every item written by the preceding BatchWriteItem, with UnprocessedKeys empty/absent. The future Tier-3 fixture must provide an empty table via the conformance context."),
            static (context, _) =>
            {
                var table = context.GetProperty("tableName") ?? "conformance-table";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("batch-write", _ => BuildRequest(context, "BatchWriteItem",
                        $@"{{""RequestItems"":{{""{table}"":[" +
                        $@"{{""PutRequest"":{{""Item"":{{""pk"":{{""S"":""pk-batch-1""}},""payload"":{{""S"":""batch-first""}}}}}}}}," +
                        $@"{{""PutRequest"":{{""Item"":{{""pk"":{{""S"":""pk-batch-2""}},""payload"":{{""S"":""batch-second""}}}}}}}}]}}}}")),
                    new ConformanceRequestStep("batch-get", _ => BuildRequest(context, "BatchGetItem",
                        $@"{{""RequestItems"":{{""{table}"":{{""Keys"":[{{""pk"":{{""S"":""pk-batch-1""}}}},{{""pk"":{{""S"":""pk-batch-2""}}}}],""ConsistentRead"":true}}}}}}")),
                    new ConformanceRequestStep("delete-item-1", _ => BuildRequest(context, "DeleteItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""pk-batch-1""}}}}}}")),
                    new ConformanceRequestStep("delete-item-2", _ => BuildRequest(context, "DeleteItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""pk-batch-2""}}}}}}")),
                ], Tier1SkipReason));
            });

    /// <summary>
    /// TransactWriteItems/TransactGetItems round-trip. Both operations
    /// restrict a request to one table and one logical DynamoDB partition
    /// (see docs/gaps/dynamodb/TransactWriteItems.yaml and
    /// TransactGetItems.yaml), so a hash-only table cannot host more than one
    /// item per partition; this case provisions its own composite-key
    /// (HASH pk + RANGE sk) table so two distinct items can legitimately
    /// share a partition. The transaction pairs a Put with a ConditionCheck
    /// (Update is an unsupported gap inside TransactWriteItems) asserting the
    /// second item does not yet exist, then TransactGetItems reads back both
    /// the written item and the still-absent one positionally.
    /// </summary>
    private static PlannedConformanceCase CreateTransactGetWriteItemsCase()
        => new(
            "transact-get-write-items-roundtrip",
            "dynamodb:CreateTable/TransactWriteItems/TransactGetItems/DeleteTable",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("TableDescription.TableStatus", "ACTIVE immediately; the proxy's CreateTable is synchronous.")]),
                new(200, Notes: "TransactWriteItems returns an empty success body."),
                new(200, RequiredBodyAssertions:
                [
                    new("Responses.0.Item", "The Put-written item, positionally first."),
                    new("Responses.1", "Empty object; the ConditionCheck target was never written."),
                ]),
                new(200),
            ],
            semanticAssertion:
            "TransactGetItems must observe one coherent snapshot: the item written by the Put succeeds and is present, while the ConditionCheck-only key stays absent."),
            static (context, _) =>
            {
                var table = context.GetProperty("transactTableName") ?? ("conf-happy-transact-table-" + Guid.NewGuid().ToString("N")[..12]);
                const string pk = "pk-transact";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-table", _ => BuildRequest(context, "CreateTable",
                        $@"{{""TableName"":""{table}"",""AttributeDefinitions"":[{{""AttributeName"":""pk"",""AttributeType"":""S""}},{{""AttributeName"":""sk"",""AttributeType"":""S""}}],""KeySchema"":[{{""AttributeName"":""pk"",""KeyType"":""HASH""}},{{""AttributeName"":""sk"",""KeyType"":""RANGE""}}],""BillingMode"":""PAY_PER_REQUEST""}}")),
                    new ConformanceRequestStep("transact-write", _ => BuildRequest(context, "TransactWriteItems",
                        $@"{{""TransactItems"":[" +
                        $@"{{""Put"":{{""TableName"":""{table}"",""Item"":{{""pk"":{{""S"":""{pk}""}},""sk"":{{""S"":""1""}},""payload"":{{""S"":""transact-written""}}}}}}}}," +
                        $@"{{""ConditionCheck"":{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{pk}""}},""sk"":{{""S"":""2""}}}},""ConditionExpression"":""attribute_not_exists(pk)""}}}}]}}")),
                    new ConformanceRequestStep("transact-get", _ => BuildRequest(context, "TransactGetItems",
                        $@"{{""TransactItems"":[" +
                        $@"{{""Get"":{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{pk}""}},""sk"":{{""S"":""1""}}}}}}}}," +
                        $@"{{""Get"":{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{pk}""}},""sk"":{{""S"":""2""}}}}}}}}]}}")),
                    new ConformanceRequestStep("delete-table", _ => BuildRequest(context, "DeleteTable",
                        $@"{{""TableName"":""{table}""}}")),
                ], Tier1SkipReason));
            });

    /// <summary>
    /// PutItem/UpdateItem/GetItem round-trip exercising the UpdateExpression
    /// SET/ADD/REMOVE grammar against the shared per-run table.
    /// </summary>
    private static PlannedConformanceCase CreateUpdateItemCase()
        => new(
            "update-item-roundtrip",
            "dynamodb:PutItem/UpdateItem/GetItem",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(200, RequiredBodyAssertions: [new("Attributes", "Absent unless ReturnValues is requested.")]),
                new(200, RequiredBodyAssertions:
                [
                    new("Item.counter.N", "Incremented by the ADD clause (seed 1 + 4 = 5)."),
                    new("Item.payload.S", "Replaced by the SET clause."),
                    new("Item.stale.S", "Absent; removed by the REMOVE clause."),
                ]),
                new(200),
            ],
            semanticAssertion:
            "GetItem after UpdateItem must reflect the SET replacement, the ADD numeric increment, and the REMOVE deletion in one UpdateExpression. The future Tier-3 fixture must provide an empty table via the conformance context."),
            static (context, _) =>
            {
                var table = context.GetProperty("tableName") ?? "conformance-table";
                const string key = "pk-update";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("put-item", _ => BuildRequest(context, "PutItem",
                        $@"{{""TableName"":""{table}"",""Item"":{{""pk"":{{""S"":""{key}""}},""payload"":{{""S"":""before""}},""counter"":{{""N"":""1""}},""stale"":{{""S"":""remove-me""}}}}}}")),
                    new ConformanceRequestStep("update-item", _ => BuildRequest(context, "UpdateItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{key}""}}}},""UpdateExpression"":""SET payload = :p ADD counter :i REMOVE stale"",""ExpressionAttributeValues"":{{"":p"":{{""S"":""after""}},"":i"":{{""N"":""4""}}}}}}")),
                    new ConformanceRequestStep("get-item", _ => BuildRequest(context, "GetItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{key}""}}}},""ConsistentRead"":true}}")),
                    new ConformanceRequestStep("delete-item", _ => BuildRequest(context, "DeleteItem",
                        $@"{{""TableName"":""{table}"",""Key"":{{""pk"":{{""S"":""{key}""}}}}}}")),
                ], Tier1SkipReason));
            });

    /// <summary>
    /// CreateTable/TagResource/ListTagsOfResource/UntagResource/DeleteTable
    /// round-trip. Uses its own ephemeral table (rather than the shared
    /// <c>tableName</c> fixture property) for the same reason as
    /// <see cref="CreateTableLifecycleCase"/>: the trailing DeleteTable must
    /// not remove a table other cases in this matrix depend on. The tagged
    /// resource ARN is taken from CreateTable's own response so this case
    /// exercises the exact ARN shape (<c>DynamoDbNames.BuildTableArn</c>) the
    /// tagging handlers must accept.
    /// </summary>
    private static PlannedConformanceCase CreateTagListUntagResourceCase()
        => new(
            "tag-list-untag-resource-roundtrip",
            "dynamodb:CreateTable/TagResource/ListTagsOfResource/UntagResource/DeleteTable",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("TableDescription.TableArn", "Returned by CreateTable; used as the tagging ResourceArn.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("Tags", "Contains the tag written by TagResource.")]),
                new(200),
                new(200, RequiredBodyAssertions: [new("Tags", "No longer contains the untagged key.")]),
                new(200),
            ],
            semanticAssertion:
            "ListTagsOfResource must reflect TagResource immediately, and no longer include a key after UntagResource removes it."),
            static (context, _) =>
            {
                var table = context.GetProperty("tagTableName") ?? ("conf-happy-tag-table-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-table", _ => BuildRequest(context, "CreateTable",
                        $@"{{""TableName"":""{table}"",""AttributeDefinitions"":[{{""AttributeName"":""pk"",""AttributeType"":""S""}}],""KeySchema"":[{{""AttributeName"":""pk"",""KeyType"":""HASH""}}],""BillingMode"":""PAY_PER_REQUEST""}}")),
                    new ConformanceRequestStep("tag-resource", state =>
                    {
                        var tableArn = state.RequireJsonString("create-table", "TableDescription", "TableArn");
                        return BuildRequest(context, "TagResource",
                            $@"{{""ResourceArn"":""{tableArn}"",""Tags"":[{{""Key"":""env"",""Value"":""conformance""}}]}}");
                    }),
                    new ConformanceRequestStep("list-tags-after-tag", state =>
                    {
                        var tableArn = state.RequireJsonString("create-table", "TableDescription", "TableArn");
                        return BuildRequest(context, "ListTagsOfResource", $@"{{""ResourceArn"":""{tableArn}""}}");
                    }),
                    new ConformanceRequestStep("untag-resource", state =>
                    {
                        var tableArn = state.RequireJsonString("create-table", "TableDescription", "TableArn");
                        return BuildRequest(context, "UntagResource",
                            $@"{{""ResourceArn"":""{tableArn}"",""TagKeys"":[""env""]}}");
                    }),
                    new ConformanceRequestStep("list-tags-after-untag", state =>
                    {
                        var tableArn = state.RequireJsonString("create-table", "TableDescription", "TableArn");
                        return BuildRequest(context, "ListTagsOfResource", $@"{{""ResourceArn"":""{tableArn}""}}");
                    }),
                    new ConformanceRequestStep("delete-table", _ => BuildRequest(context, "DeleteTable",
                        $@"{{""TableName"":""{table}""}}")),
                ], Tier1SkipReason));
            });

    /// <summary>
    /// CreateTable/UpdateTimeToLive/DescribeTimeToLive/DeleteTable
    /// round-trip. Uses its own ephemeral table for the same DeleteTable
    /// isolation reason as the other table-lifecycle cases above.
    /// </summary>
    private static PlannedConformanceCase CreateDescribeUpdateTimeToLiveCase()
        => new(
            "describe-update-time-to-live-roundtrip",
            "dynamodb:CreateTable/UpdateTimeToLive/DescribeTimeToLive/DeleteTable",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(200, RequiredBodyAssertions:
                [
                    new("TimeToLiveSpecification.AttributeName", "Equals the enabled TTL attribute name."),
                    new("TimeToLiveSpecification.Enabled", "True."),
                ]),
                new(200, RequiredBodyAssertions:
                [
                    new("TimeToLiveDescription.TimeToLiveStatus", "ENABLED, matching the synchronous UpdateTimeToLive contract."),
                    new("TimeToLiveDescription.AttributeName", "Equals the enabled TTL attribute name."),
                ]),
                new(200),
            ],
            semanticAssertion:
            "DescribeTimeToLive must report ENABLED with the same AttributeName immediately after UpdateTimeToLive returns, with no polling/waiter needed."),
            static (context, _) =>
            {
                var table = context.GetProperty("ttlTableName") ?? ("conf-happy-ttl-table-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-table", _ => BuildRequest(context, "CreateTable",
                        $@"{{""TableName"":""{table}"",""AttributeDefinitions"":[{{""AttributeName"":""pk"",""AttributeType"":""S""}}],""KeySchema"":[{{""AttributeName"":""pk"",""KeyType"":""HASH""}}],""BillingMode"":""PAY_PER_REQUEST""}}")),
                    new ConformanceRequestStep("update-ttl", _ => BuildRequest(context, "UpdateTimeToLive",
                        $@"{{""TableName"":""{table}"",""TimeToLiveSpecification"":{{""AttributeName"":""expiresAt"",""Enabled"":true}}}}")),
                    new ConformanceRequestStep("describe-ttl", _ => BuildRequest(context, "DescribeTimeToLive",
                        $@"{{""TableName"":""{table}""}}")),
                    new ConformanceRequestStep("delete-table", _ => BuildRequest(context, "DeleteTable",
                        $@"{{""TableName"":""{table}""}}")),
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
