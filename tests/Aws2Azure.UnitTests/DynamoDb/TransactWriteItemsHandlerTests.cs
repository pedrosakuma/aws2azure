using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Coverage for <see cref="TransactWriteItemsHandler"/>:
/// <list type="bullet">
///   <item>Validation: 100-item cap, exactly-one-of Put/Delete/ConditionCheck,
///   Update rejected, ConditionCheck requires a ConditionExpression.</item>
///   <item>Single-table / single-partition / duplicate-target rejection.</item>
///   <item>Stored-procedures-disabled rejection.</item>
///   <item>Sproc request shape, success (200), condition failure
///   (TransactionCanceledException + positional reasons), and error mapping.</item>
/// </list>
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class TransactWriteItemsHandlerTests
{
    public TransactWriteItemsHandlerTests()
    {
        CosmosOpsShared.MetadataCache.Clear();
    }

    // pk (HASH) + sk (RANGE) so multiple items can share a partition.
    private static readonly string MetaPkSk =
        "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\","
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"},{\"name\":\"sk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"},{\"name\":\"sk\",\"keyType\":\"RANGE\"}],"
        + "\"billingMode\":\"PAY_PER_REQUEST\"}";

    private static string MetaPkSkWithLsi(
        string projectionType,
        string? nonKeyAttributes = null)
    {
        var projected = nonKeyAttributes is null
            ? string.Empty
            : ",\"nonKeyAttributes\":[\"" + nonKeyAttributes + "\"]";
        return
            "{\"id\":\"__aws2azure_table_meta__\"," +
            "\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\"," +
            "\"tableName\":\"orders\"," +
            "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}," +
            "{\"name\":\"sk\",\"type\":\"S\"},{\"name\":\"ix\",\"type\":\"S\"}]," +
            "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}," +
            "{\"name\":\"sk\",\"keyType\":\"RANGE\"}]," +
            "\"localSecondaryIndexes\":[{\"indexName\":\"byIx\"," +
            "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}," +
            "{\"name\":\"ix\",\"keyType\":\"RANGE\"}]," +
            "\"projectionType\":\"" + projectionType + "\"" + projected + "}]," +
            "\"billingMode\":\"PAY_PER_REQUEST\"}";
    }

    private static readonly string MetaPkSkWithSecondaryIndexes =
        "{\"id\":\"__aws2azure_table_meta__\"," +
        "\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\"," +
        "\"tableName\":\"orders\"," +
        "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}," +
        "{\"name\":\"sk\",\"type\":\"S\"},{\"name\":\"gpk\",\"type\":\"S\"}," +
        "{\"name\":\"gsk\",\"type\":\"B\"},{\"name\":\"lsk\",\"type\":\"S\"}]," +
        "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}," +
        "{\"name\":\"sk\",\"keyType\":\"RANGE\"}]," +
        "\"globalSecondaryIndexes\":[{\"indexName\":\"byGlobal\"," +
        "\"keySchema\":[{\"name\":\"gpk\",\"keyType\":\"HASH\"}," +
        "{\"name\":\"gsk\",\"keyType\":\"RANGE\"}]," +
        "\"projectionType\":\"ALL\"}]," +
        "\"localSecondaryIndexes\":[{\"indexName\":\"byLocal\"," +
        "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}," +
        "{\"name\":\"lsk\",\"keyType\":\"RANGE\"}]," +
        "\"projectionType\":\"ALL\"}]," +
        "\"billingMode\":\"PAY_PER_REQUEST\"}";

    private static CosmosClient BuildClient(
        ScriptedHandler handler,
        string endpoint = "https://example.documents.azure.com/",
        IReadOnlyList<string>? preferredRegions = null)
    {
        var http = new AzureHttpClient(handler, ownsHandler: false,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        var creds = new CosmosCredentials
        {
            Endpoint = endpoint,
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
            PreferredRegions = preferredRegions is null
                ? null
                : new List<string>(preferredRegions),
        };
        return new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));
    }

    private static SprocContext EnabledSproc()
        => new(StoredProcedureMode.Preferred, new SprocManager(NullLogger<SprocManager>.Instance));

    private static (DefaultHttpContext ctx, MemoryStream body) NewCtx()
    {
        var ctx = new DefaultHttpContext();
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        return (ctx, ms);
    }

    private static string ReadResponse(MemoryStream body)
    {
        body.Position = 0;
        return new StreamReader(body).ReadToEnd();
    }

    private static HttpResponseMessage CosmosOk(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage CosmosCreated(string body = "{}")
        => new(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage CosmosStatus(HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage CosmosWriteRegionRejected()
    {
        var response = CosmosStatus(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("x-ms-substatus", "3");
        return response;
    }

    private static Task Run(DefaultHttpContext ctx, CosmosClient cosmos, SprocContext? sproc, string req)
        => TransactWriteItemsHandler.HandleTransactWriteItemsAsync(
            ctx, Encoding.UTF8.GetBytes(req), cosmos, sproc, CancellationToken.None);

    private static Task Run(
        DefaultHttpContext ctx,
        CosmosClient cosmos,
        SprocContext? sproc,
        byte[] request)
        => TransactWriteItemsHandler.HandleTransactWriteItemsAsync(
            ctx,
            request,
            cosmos,
            sproc,
            CancellationToken.None);

    private static string PutOp(string sk, string? condition = null)
    {
        var cond = condition is null
            ? string.Empty
            : ",\"ConditionExpression\":\"" + condition + "\"";
        return "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\""
            + sk + "\"},\"v\":{\"N\":\"1\"}}" + cond + "}}";
    }

    private static string PutOpWithPayload(int payloadBytes)
        => "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"}," +
           "\"sk\":{\"S\":\"1\"},\"payload\":{\"S\":\"" +
           new string('x', payloadBytes) + "\"}}}}";

    private static string PutOpWithIndexedPayload(int payloadBytes)
        => "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"}," +
           "\"sk\":{\"S\":\"1\"},\"ix\":{\"S\":\"z\"},\"payload\":{\"S\":\"" +
           new string('x', payloadBytes) + "\"}}}}";

    private static string BuildConditionalOperation(
        string action,
        string expression)
    {
        var keyProperty = action == "Put"
            ? "\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}"
            : "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}";
        return
            "{\"TransactItems\":[{\"" + action +
            "\":{\"TableName\":\"orders\"," + keyProperty +
            ",\"ConditionExpression\":\"" + expression +
            "\",\"ExpressionAttributeValues\":{\":v\":{\"S\":\"x\"}}}}]}";
    }

    private static string MultiWriteAccountJson =>
        """
        {
          "enableMultipleWriteLocations": true,
          "readableLocations": [
            { "name": "West US", "databaseAccountEndpoint": "https://txn-write-west.documents.azure.com/" },
            { "name": "East US", "databaseAccountEndpoint": "https://txn-write-east.documents.azure.com/" }
          ],
          "writableLocations": [
            { "name": "West US", "databaseAccountEndpoint": "https://txn-write-west.documents.azure.com/" },
            { "name": "East US", "databaseAccountEndpoint": "https://txn-write-east.documents.azure.com/" }
          ]
        }
        """;

    private static string MultiWriteLaterOnlyAccountJson =>
        """
        {
          "enableMultipleWriteLocations": true,
          "readableLocations": [
            { "name": "East US", "databaseAccountEndpoint": "https://txn-write-east.documents.azure.com/" }
          ],
          "writableLocations": [
            { "name": "East US", "databaseAccountEndpoint": "https://txn-write-east.documents.azure.com/" }
          ]
        }
        """;

    [Fact]
    public void Item_size_uses_utf8_attribute_names_and_decoded_binary_bytes()
    {
        using var document = JsonDocument.Parse(
            """{"é":{"B":"AQID"}}""");

        Assert.True(DynamoDbItemSize.TryCalculate(
            document.RootElement,
            out var size,
            out var error),
            error);
        Assert.Equal(5, size);
    }

    [Fact]
    public async Task Put_item_just_under_400_kib_is_accepted()
    {
        const int fixedItemBytes = 13;
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" +
            PutOpWithPayload(DynamoDbItemSize.MaximumBytes - fixedItemBytes - 1) +
            "]}");

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Put_item_over_400_kib_is_rejected_before_cosmos_io()
    {
        const int fixedItemBytes = 13;
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" +
            PutOpWithPayload(DynamoDbItemSize.MaximumBytes - fixedItemBytes + 1) +
            "]}");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("400 KiB", ReadResponse(body), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("KEYS_ONLY", null, 29)]
    [InlineData("INCLUDE", "payload", 40)]
    [InlineData("ALL", null, 40)]
    public void Local_secondary_index_size_uses_projection_and_unique_keys(
        string projectionType,
        string? includedAttribute,
        long expectedCombinedSize)
    {
        using var itemDocument = JsonDocument.Parse(
            """{"pk":{"S":"a"},"sk":{"S":"1"},"ix":{"S":"z"},"payload":{"S":"data"}}""");
        var metadata = new TableMetadata
        {
            KeySchema =
            [
                new TableKeySchemaElement { Name = "pk", KeyType = "HASH" },
                new TableKeySchemaElement { Name = "sk", KeyType = "RANGE" },
            ],
            LocalSecondaryIndexes =
            [
                new TableIndexDefinition
                {
                    IndexName = "byIx",
                    KeySchema =
                    [
                        new TableKeySchemaElement
                        {
                            Name = "pk",
                            KeyType = "HASH",
                        },
                        new TableKeySchemaElement
                        {
                            Name = "ix",
                            KeyType = "RANGE",
                        },
                    ],
                    ProjectionType = projectionType,
                    NonKeyAttributes = includedAttribute is null
                        ? null
                        : [includedAttribute, "pk", "ix"],
                },
            ],
        };

        Assert.True(
            DynamoDbItemSize.TryCalculate(
                itemDocument.RootElement,
                out var baseSize,
                out var baseError),
            baseError);
        Assert.Equal(20, baseSize);
        Assert.True(
            DynamoDbItemSize.TryCalculateWithLocalSecondaryIndexes(
                itemDocument.RootElement,
                metadata,
                baseSize,
                out var combinedSize,
                out var error),
            error);

        Assert.Equal(expectedCombinedSize, combinedSize);
    }

    [Fact]
    public void Sparse_local_secondary_index_adds_no_entry_size()
    {
        using var itemDocument = JsonDocument.Parse(
            """{"pk":{"S":"a"},"sk":{"S":"1"},"payload":{"S":"data"}}""");
        var metadata = new TableMetadata
        {
            KeySchema =
            [
                new TableKeySchemaElement { Name = "pk", KeyType = "HASH" },
                new TableKeySchemaElement { Name = "sk", KeyType = "RANGE" },
            ],
            LocalSecondaryIndexes =
            [
                new TableIndexDefinition
                {
                    IndexName = "byIx",
                    KeySchema =
                    [
                        new TableKeySchemaElement
                        {
                            Name = "pk",
                            KeyType = "HASH",
                        },
                        new TableKeySchemaElement
                        {
                            Name = "ix",
                            KeyType = "RANGE",
                        },
                    ],
                    ProjectionType = "ALL",
                },
            ],
        };

        Assert.True(
            DynamoDbItemSize.TryCalculate(
                itemDocument.RootElement,
                out var baseSize,
                out var baseError),
            baseError);
        Assert.True(
            DynamoDbItemSize.TryCalculateWithLocalSecondaryIndexes(
                itemDocument.RootElement,
                metadata,
                baseSize,
                out var combinedSize,
                out var error),
            error);

        Assert.Equal(baseSize, combinedSize);
    }

    [Fact]
    public void Local_secondary_index_size_sums_every_corresponding_entry()
    {
        using var itemDocument = JsonDocument.Parse(
            """{"pk":{"S":"a"},"sk":{"S":"1"},"ix":{"S":"z"},"iy":{"S":"q"},"payload":{"S":"data"}}""");
        var metadata = new TableMetadata
        {
            KeySchema =
            [
                new TableKeySchemaElement { Name = "pk", KeyType = "HASH" },
                new TableKeySchemaElement { Name = "sk", KeyType = "RANGE" },
            ],
            LocalSecondaryIndexes =
            [
                new TableIndexDefinition
                {
                    IndexName = "byIx",
                    KeySchema =
                    [
                        new TableKeySchemaElement
                        {
                            Name = "pk",
                            KeyType = "HASH",
                        },
                        new TableKeySchemaElement
                        {
                            Name = "ix",
                            KeyType = "RANGE",
                        },
                    ],
                    ProjectionType = "KEYS_ONLY",
                },
                new TableIndexDefinition
                {
                    IndexName = "byIy",
                    KeySchema =
                    [
                        new TableKeySchemaElement
                        {
                            Name = "pk",
                            KeyType = "HASH",
                        },
                        new TableKeySchemaElement
                        {
                            Name = "iy",
                            KeyType = "RANGE",
                        },
                    ],
                    ProjectionType = "ALL",
                },
            ],
        };

        Assert.True(
            DynamoDbItemSize.TryCalculate(
                itemDocument.RootElement,
                out var baseSize,
                out var baseError),
            baseError);
        Assert.Equal(23, baseSize);
        Assert.True(
            DynamoDbItemSize.TryCalculateWithLocalSecondaryIndexes(
                itemDocument.RootElement,
                metadata,
                baseSize,
                out var combinedSize,
                out var error),
            error);

        Assert.Equal(55, combinedSize);
    }

    [Theory]
    [InlineData("KEYS_ONLY", null, DynamoDbItemSize.MaximumBytes - 25)]
    [InlineData("INCLUDE", "payload", 204784)]
    [InlineData("ALL", null, 204784)]
    public async Task Local_secondary_index_combined_size_accepts_exact_boundary(
        string projectionType,
        string? includedAttribute,
        int payloadBytes)
    {
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSkWithLsi(
                    projectionType,
                    includedAttribute)),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" +
            PutOpWithIndexedPayload(payloadBytes) + "]}");

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("KEYS_ONLY", null, DynamoDbItemSize.MaximumBytes - 24)]
    [InlineData("INCLUDE", "payload", 204785)]
    [InlineData("ALL", null, 204785)]
    public async Task Local_secondary_index_combined_size_rejects_before_sproc(
        string projectionType,
        string? includedAttribute,
        int payloadBytes)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSkWithLsi(
                    projectionType,
                    includedAttribute)),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" +
            PutOpWithIndexedPayload(payloadBytes) + "]}");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains(
            "local secondary index entries",
            ReadResponse(body),
            StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Uri.AbsolutePath.Contains(
                "/sprocs",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_sprocs_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var disabled = new SprocContext(StoredProcedureMode.Disabled, null);

        await Run(ctx, cosmos, disabled, "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
        Assert.Contains("stored procedures", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_transact_items_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await Run(ctx, cosmos, EnabledSproc(), "{\"TransactItems\":[]}");

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
    }

    [Fact]
    public async Task Update_operation_rejected_as_gap()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var req = "{\"TransactItems\":[{\"Update\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"UpdateExpression\":\"SET v = :x\",\"ExpressionAttributeValues\":{\":x\":{\"N\":\"5\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("Update", resp);
    }

    [Fact]
    public async Task Item_with_two_operations_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var req = "{\"TransactItems\":[{" +
            "\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}," +
            "\"Delete\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("exactly one", ReadResponse(body));
    }

    [Theory]
    [InlineData("""{"TransactItems":[{"Put":null}]}""", "Put must be an object")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}}},"Delete":"invalid"}]}""",
        "Delete must be an object")]
    [InlineData("""{"TransactItems":[{"Update":null}]}""", "Update is not supported")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}}},"Unknown":{}}]}""",
        "unsupported action")]
    public async Task Present_non_object_actions_are_rejected(
        string request,
        string expected)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains(expected, ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cross_table_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," +
            "{\"Put\":{\"TableName\":\"others\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"2\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("same table", ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cross_partition_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," +
            "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"b\"},\"sk\":{\"S\":\"2\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("partition-key", ReadResponse(body));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Duplicate_target_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("multiple operations on one item", ReadResponse(body));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConditionCheck_requires_expression()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ConditionExpression is required", ReadResponse(body));
    }

    [Fact]
    public async Task Condition_on_reserved_attribute_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        // "ttl" is shadow-encoded / injected as Cosmos' native TTL, so a
        // transaction condition on it cannot be faithfully evaluated server-side.
        var req = "{\"TransactItems\":[" + PutOp("1", condition: "attribute_not_exists(ttl)") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("ttl", resp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123456789012345678901234567")]
    public async Task Invalid_client_request_token_length_is_rejected_before_io(
        string token)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);
        var req = "{\"ClientRequestToken\":\"" + token
            + "\",\"TransactItems\":["
            + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("ClientRequestToken", response);
        Assert.Contains("1 and 36", response);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Client_request_token_executes_with_durable_descriptor()
    {
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"ClientRequestToken\":\"token-1\",\"TransactItems\":["
            + PutOp("1") + "]}");

        Assert.Equal(200, ctx.Response.StatusCode);
        var execution = handler.Requests[^1];
        using var parameters = JsonDocument.Parse(execution.Body!);
        var idempotency = parameters.RootElement[1];
        Assert.StartsWith(
            DynamoDbPersistedFormatContract.TransactionIdempotencyRecordIdPrefix,
            idempotency.GetProperty("id").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("61", idempotency.GetProperty("pk").GetString());
        Assert.Equal(
            64,
            idempotency.GetProperty("fingerprint").GetString()!.Length);
        Assert.Equal(600_000, idempotency.GetProperty("windowMs").GetInt32());
        Assert.Equal(
            660,
            idempotency.GetProperty("cleanupTtlSeconds").GetInt32());
    }

    [Fact]
    public async Task Semantic_equivalent_requests_produce_same_fingerprint()
    {
        var first =
            """
            {
              "ClientRequestToken": "stable-token",
              "TransactItems": [{
                "Put": {
                  "TableName": "orders",
                  "Item": {
                    "pk": { "S": "a" },
                    "sk": { "S": "1" },
                    "n": { "N": "1.0" },
                    "labels": { "SS": ["beta", "alpha"] },
                    "map": { "M": { "z": { "S": "last" }, "a": { "N": "2.00" } } }
                  },
                  "ConditionExpression": "#n = :one",
                  "ExpressionAttributeNames": { "#n": "n" },
                  "ExpressionAttributeValues": { ":one": { "N": "1.00" } }
                }
              }]
            }
            """;
        var second =
            """
            {
              "TransactItems": [{
                "Put": {
                  "ExpressionAttributeValues": { ":value": { "N": "1" } },
                  "ConditionExpression": "#number=:value",
                  "Item": {
                    "map": { "M": { "a": { "N": "2" }, "z": { "S": "last" } } },
                    "labels": { "SS": ["alpha", "beta"] },
                    "n": { "N": "1" },
                    "sk": { "S": "1" },
                    "pk": { "S": "a" }
                  },
                  "ExpressionAttributeNames": { "#number": "n" },
                  "TableName": "orders"
                }
              }],
              "ClientRequestToken": "stable-token",
              "ReturnConsumedCapacity": "NONE"
            }
            """;
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
                CosmosOk("{\"success\":true,\"replayed\":true}"),
            },
        };
        var cosmos = BuildClient(handler);
        var sproc = EnabledSproc();

        var (firstContext, _) = NewCtx();
        await Run(firstContext, cosmos, sproc, first);
        var (secondContext, _) = NewCtx();
        await Run(secondContext, cosmos, sproc, second);

        Assert.Equal(200, firstContext.Response.StatusCode);
        Assert.Equal(200, secondContext.Response.StatusCode);
        var executions = handler.Requests
            .Where(request => request.Uri.AbsolutePath.Contains(
                "/sprocs/" + SprocManager.TransactSprocId,
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, executions.Length);
        static string Fingerprint(CapturedRequest request)
        {
            using var body = JsonDocument.Parse(request.Body!);
            return body.RootElement[1].GetProperty("fingerprint").GetString()!;
        }
        var fingerprint = Fingerprint(executions[0]);
        Assert.Equal(
            "aa7a21a707d061dd1b5acd0d0d9b8c9f9823d7837a5e7dfc3980ceea667e3c45",
            fingerprint);
        Assert.Equal(fingerprint, Fingerprint(executions[1]));
    }

    [Fact]
    public async Task Idempotency_mismatch_maps_to_native_dynamodb_error()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(
                    "{\"success\":false,\"idempotencyMismatch\":true}"),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"ClientRequestToken\":\"token-1\",\"TransactItems\":["
            + PutOp("1") + "]}");

        Assert.Equal(400, ctx.Response.StatusCode);
        using var response = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(
            "com.amazonaws.dynamodb.v20120810#IdempotentParameterMismatchException",
            response.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task Token_request_retries_one_cosmos_write_conflict_safely()
    {
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosStatus(HttpStatusCode.Conflict),
                CosmosOk("{\"success\":true,\"replayed\":true}"),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"ClientRequestToken\":\"token-1\",\"TransactItems\":["
            + PutOp("1") + "]}");

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(
            2,
            handler.Requests.Count(request =>
                request.Uri.AbsolutePath.Contains(
                    "/sprocs/" + SprocManager.TransactSprocId,
                    StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"Expected":{"v":{"Exists":false}}}}]}""",
        "Expected")]
    [InlineData(
        """{"TransactItems":[{"Delete":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionalOperator":"AND"}}]}""",
        "ConditionalOperator")]
    [InlineData(
        """{"TransactItems":[{"ConditionCheck":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"attribute_exists(pk)","ReturnValuesOnConditionCheckFailure":"ALL_OLD"}}]}""",
        "ReturnValuesOnConditionCheckFailure")]
    public async Task Legacy_and_unsupported_condition_members_are_rejected_before_cosmos(
        string request,
        string expected)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains(expected, ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"bad":{"SS":[]}}}}]}""",
        "must not be empty")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"bad":{"B":"not-base64!"}}}}]}""",
        "valid base64")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"bad":{"N":"1e126"}}}}]}""",
        "invalid Number")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"bad":{"NS":["1","not-a-number"]}}}}]}""",
        "Number set member")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"bad":{"SS":["same","same"]}}}}]}""",
        "duplicate")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"bad":{"NS":["1","1.0"]}}}}]}""",
        "duplicate")]
    [InlineData(
        """{"TransactItems":[{"Delete":{"TableName":"orders","Key":{"pk":{"B":"not-base64!"},"sk":{"S":"1"}}}}]}""",
        "valid base64")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"v = :bad","ExpressionAttributeValues":{":bad":{"N":"1e-131"}}}}]}""",
        "invalid Number")]
    public async Task Invalid_attribute_values_are_rejected_before_metadata_io(
        string request,
        string expected)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains(expected, ReadResponse(body), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("Put")]
    [InlineData("Delete")]
    [InlineData("ConditionCheck")]
    public async Task Condition_expression_over_encoded_limit_is_rejected_before_io(
        string action)
    {
        const string prefix = "v = :v";
        var expression =
            prefix
            + new string(
                ' ',
                ConditionExpressionParser.MaxExpressionUtf8Bytes
                - Encoding.UTF8.GetByteCount(prefix)
                + 1);
        var handler = new ScriptedHandler();
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            BuildConditionalOperation(action, expression));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("4 KiB", ReadResponse(body), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fifty_thousand_or_expression_is_rejected_without_io_or_crash()
    {
        var expression = string.Join(
            " OR ",
            Enumerable.Repeat("attribute_exists(pk)", 50_001));
        var request =
            "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"ConditionExpression\":\"" + expression + "\"}}]}";
        var handler = new ScriptedHandler();
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("4 KiB", ReadResponse(body), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Condition_expression_encoded_length_limit_is_inclusive()
    {
        const string prefix = "é = :v";
        var expression =
            prefix
            + new string(
                ' ',
                ConditionExpressionParser.MaxExpressionUtf8Bytes
                - Encoding.UTF8.GetByteCount(prefix));
        using var value = JsonDocument.Parse("""{"S":"x"}""");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [":v"] = value.RootElement.Clone(),
        };

        var parsed = ConditionExpressionParser.Parse(
            expression,
            expressionAttributeNames: null,
            values);

        Assert.IsType<CompareCondition>(parsed);
        var overLimit = expression + " ";
        var exception = Assert.Throws<ExpressionSyntaxException>(
            () => ConditionExpressionParser.Parse(
                overLimit,
                expressionAttributeNames: null,
                values));
        Assert.Contains("4 KiB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Condition_operator_limit_is_inclusive_and_precedes_parsing()
    {
        var atLimit =
            "NOT "
            + string.Join(
                " OR ",
                Enumerable.Repeat("attribute_exists(v)", 150));
        var parsed = ConditionExpressionParser.Parse(
            atLimit,
            expressionAttributeNames: null,
            expressionAttributeValues: null);

        Assert.NotNull(parsed);
        var overLimit = string.Join(
            " OR ",
            Enumerable.Repeat("attribute_exists(v)", 151));
        var exception = Assert.Throws<ExpressionSyntaxException>(
            () => ConditionExpressionParser.Parse(
                overLimit,
                expressionAttributeNames: null,
                expressionAttributeValues: null));
        Assert.Contains(
            ConditionExpressionParser.MaxOperators.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excessive_condition_nesting_is_rejected_before_io()
    {
        var expression =
            new string(
                '(',
                ConditionExpressionParser.MaxParserNestingDepth + 1)
            + "attribute_exists(v)"
            + new string(
                ')',
                ConditionExpressionParser.MaxParserNestingDepth + 1);
        var handler = new ScriptedHandler();
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"ConditionExpression\":\"" + expression + "\"}}]}");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains(
            "nesting depth",
            ReadResponse(body),
            StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Condition_ast_depth_boundary_serializes_without_stack_overflow()
    {
        var expression =
            string.Concat(
                Enumerable.Repeat(
                    "NOT ",
                    ConditionExpressionParser.MaxAstDepth - 1))
            + "attribute_exists(v)";
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };
        var (context, _) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"ConditionExpression\":\"" + expression + "\"}}]}");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public void Condition_depth_bounds_accept_operator_budget_and_reject_deeper_groups()
    {
        var atAstLimit =
            string.Concat(
                Enumerable.Repeat(
                    "NOT ",
                    ConditionExpressionParser.MaxAstDepth - 1))
            + "attribute_exists(v)";
        Assert.NotNull(
            ConditionExpressionParser.Parse(
                atAstLimit,
                expressionAttributeNames: null,
                expressionAttributeValues: null));

        var grouped =
            new string(
                '(',
                ConditionExpressionParser.MaxParserNestingDepth)
            + "attribute_exists(v)"
            + new string(
                ')',
                ConditionExpressionParser.MaxParserNestingDepth);
        Assert.NotNull(
            ConditionExpressionParser.Parse(
                grouped,
                expressionAttributeNames: null,
                expressionAttributeValues: null));

        var tooDeep =
            '(' + grouped + ')';
        var exception = Assert.Throws<ExpressionSyntaxException>(
            () => ConditionExpressionParser.Parse(
                tooDeep,
                expressionAttributeNames: null,
                expressionAttributeValues: null));
        Assert.Contains(
            "nesting depth",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ExpressionAttributeNames", "#")]
    [InlineData("ExpressionAttributeValues", ":")]
    public async Task Oversized_expression_placeholder_is_rejected_before_io(
        string member,
        string sigil)
    {
        var placeholder =
            sigil
            + new string(
                'p',
                ConditionExpressionParser.MaxPlaceholderUtf8Bytes);
        var expression = sigil == "#"
            ? $"attribute_exists({placeholder})"
            : $"v = {placeholder}";
        var placeholderBody = member == "ExpressionAttributeNames"
            ? "\"" + placeholder + "\":\"v\""
            : "\"" + placeholder + "\":{\"S\":\"x\"}";
        var request =
            "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"ConditionExpression\":\"" + expression + "\",\"" + member +
            "\":{" + placeholderBody + "}}}]}";
        var handler = new ScriptedHandler();
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("255 bytes", ReadResponse(body), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_invalid_utf8_is_a_serialization_error_before_io(
        bool invalidPropertyName)
    {
        var prefix = Encoding.UTF8.GetBytes(
            invalidPropertyName
                ? "{\"TransactItems\":[{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"},\""
                : "{\"TransactItems\":[{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"},\"bad\":{\"S\":\"");
        var suffix = Encoding.UTF8.GetBytes(
            invalidPropertyName
                ? "\":{\"S\":\"x\"}}}}]}"
                : "\"}}}}]}");
        var request = new byte[prefix.Length + 3 + suffix.Length];
        prefix.CopyTo(request, 0);
        request[prefix.Length] = 0xED;
        request[prefix.Length + 1] = 0xA0;
        request[prefix.Length + 2] = 0x80;
        suffix.CopyTo(request, prefix.Length + 3);
        var handler = new ScriptedHandler();
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains(
            "SerializationException",
            response,
            StringComparison.Ordinal);
        Assert.Contains("valid Unicode", response, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"bad":{"S":"\uD800"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"\uD800":{"S":"x"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"ConditionCheck":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"attribute_exists(#n)","ExpressionAttributeNames":{"#n":"\uD800"}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"ConditionCheck":{"TableName":"orders","Key":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"v = :v","ExpressionAttributeValues":{":v":{"S":"\uD800"}}}}]}""")]
    public async Task Lone_surrogate_is_a_serialization_error_before_io(
        string request)
    {
        var handler = new ScriptedHandler();
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains(
            "SerializationException",
            response,
            StringComparison.Ordinal);
        Assert.Contains("valid Unicode", response, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"gpk":{"N":"1"}}""", "gpk", "declares S")]
    [InlineData("""{"gsk":{"B":""}}""", "gsk", "must not be empty")]
    [InlineData("""{"lsk":{"N":"1"}}""", "lsk", "declares S")]
    [InlineData("""{"lsk":{"S":""}}""", "lsk", "must not be empty")]
    public async Task Invalid_present_secondary_index_key_is_rejected_before_sproc(
        string extraAttribute,
        string attributeName,
        string expected)
    {
        var request =
            "{\"TransactItems\":[{\"Put\":{\"TableName\":\"orders\",\"Item\":" +
            "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}," +
            extraAttribute[1..^1] + "}}}]}";
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSkWithSecondaryIndexes) },
        };
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains(attributeName, response, StringComparison.Ordinal);
        Assert.Contains(expected, response, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Uri.AbsolutePath.Contains(
                "/sprocs/",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gpk", ItemHandlers.MaximumPartitionKeyBytes + 1, "2048")]
    [InlineData("lsk", ItemHandlers.MaximumSortKeyBytes + 1, "1024")]
    public async Task Oversized_present_secondary_index_key_is_rejected_before_sproc(
        string attributeName,
        int byteCount,
        string expectedLimit)
    {
        var request =
            "{\"TransactItems\":[{\"Put\":{\"TableName\":\"orders\",\"Item\":" +
            "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"},\"" +
            attributeName + "\":{\"S\":\"" + new string('x', byteCount) +
            "\"}}}}]}";
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSkWithSecondaryIndexes) },
        };
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains(attributeName, response, StringComparison.Ordinal);
        Assert.Contains(expectedLimit, response, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Uri.AbsolutePath.Contains(
                "/sprocs/",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sparse_secondary_indexes_allow_all_index_keys_to_be_absent()
    {
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSkWithSecondaryIndexes),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };
        var (context, _) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"emoji":{"S":"\uD83D\uDE00"}}}}]}""");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Token_fingerprint_validation_failure_stays_a_400()
    {
        var (context, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            """
            {
              "ClientRequestToken": "token-1",
              "TransactItems": [{
                "Put": {
                  "TableName": "orders",
                  "Item": {
                    "pk": { "S": "a" },
                    "sk": { "S": "1" },
                    "bad": { "B": "not-base64!" }
                  }
                }
              }]
            }
            """);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains(
            "valid base64",
            ReadResponse(body),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"attribute_not_exists(#pk)","ExpressionAttributeNames":{"#pk":"pk","#unused":"unused"}}}]}""",
        "#unused")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"attribute_not_exists(pk)","ExpressionAttributeValues":{":unused":{"S":"value"}}}}]}""",
        ":unused")]
    public async Task Unused_expression_placeholders_are_rejected_before_metadata_io(
        string request,
        string expected)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("unused", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expected, response, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task In_with_more_than_100_operands_is_rejected_before_metadata_io()
    {
        var expression = new StringBuilder("v IN (");
        var values = new StringBuilder();
        for (var index = 0; index <= ConditionExpressionParser.MaxInOperands; index++)
        {
            if (index > 0)
            {
                expression.Append(',');
                values.Append(',');
            }
            expression.Append(":v").Append(index);
            values.Append("\":v").Append(index).Append("\":{\"S\":\"x\"}");
        }
        expression.Append(')');
        var request =
            "{\"TransactItems\":[{\"Put\":{\"TableName\":\"orders\",\"Item\":"
            + "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}},"
            + "\"ConditionExpression\":\"" + expression + "\","
            + "\"ExpressionAttributeValues\":{" + values + "}}}]}";
        var handler = new ScriptedHandler();
        var (context, body) = NewCtx();

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Contains("at most 100", ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"m = :v","ExpressionAttributeValues":{":v":{"M":{"x":{"S":"y"}}}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"xs = :v","ExpressionAttributeValues":{":v":{"L":[{"S":"y"}]}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"blob = :v","ExpressionAttributeValues":{":v":{"B":"AQID"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"tags = :v","ExpressionAttributeValues":{":v":{"SS":["x"]}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"n = :v","ExpressionAttributeValues":{":v":{"N":"9007199254740993"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"m.x = :v","ExpressionAttributeValues":{":v":{"S":"y"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"xs[0] = :v","ExpressionAttributeValues":{":v":{"S":"y"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"contains(xs, :v)","ExpressionAttributeValues":{":v":{"S":"y"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"size(xs) > :v","ExpressionAttributeValues":{":v":{"N":"0"}}}}]}""")]
    [InlineData(
        """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"v = other"}}]}""")]
    public async Task Unsupported_transaction_condition_shapes_fail_before_sproc(
        string request)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("supported transaction subset", response);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Over_100_items_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var sb = new StringBuilder("{\"TransactItems\":[");
        for (int i = 0; i < 101; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(PutOp(i.ToString()));
        }
        sb.Append("]}");

        await Run(ctx, cosmos, EnabledSproc(), sb.ToString());

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("at most 100", ReadResponse(body));
    }

    [Fact]
    public async Task Malformed_numeric_condition_value_rejected_with_validation_error()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaPkSk) } };
        var cosmos = BuildClient(handler);
        // {"N":"not-a-number"} parses as a condition operand but serializes to a
        // raw, invalid JSON token — the handler must surface a ValidationException
        // (400), not throw / 500, when the embedded condition is re-validated.
        var req = "{\"TransactItems\":[{\"Put\":{\"TableName\":\"orders\",\"Item\":" +
            "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"},\"v\":{\"N\":\"1\"}}," +
            "\"ConditionExpression\":\"v = :bad\"," +
            "\"ExpressionAttributeValues\":{\":bad\":{\"N\":\"not-a-number\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("invalid Number", resp);
    }

    [Fact]
    public async Task Whitespace_condition_expression_is_rejected_not_dropped()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };
        var request =
            """
            {
              "TransactItems": [
                {
                  "Put": {
                    "TableName": "orders",
                    "Item": {
                      "pk": { "S": "a" },
                      "sk": { "S": "1" }
                    },
                    "ConditionExpression": "   "
                  }
                }
              ]
            }
            """;

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("must not be empty", ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Empty_string_member_in_nonempty_string_set_is_accepted()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };
        var request =
            """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"},"tags":{"SS":[""]}}}}]}""";

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Success_returns_empty_200_and_sends_sproc()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," + PutOp("2") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("{}", ReadResponse(body));

        // Last request must be the sproc execution, carrying both PUT ops.
        var exec = handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, exec.Method);
        Assert.Contains("/sprocs/" + SprocManager.TransactSprocId, exec.Uri.AbsolutePath);
        Assert.NotNull(exec.Body);
        Assert.Contains("\"type\":\"PUT\"", exec.Body!);
        // Params array wraps the operations array plus an optional token descriptor.
        Assert.StartsWith("[[", exec.Body!.Replace(" ", string.Empty));
        using var parameters = JsonDocument.Parse(exec.Body);
        Assert.Equal(JsonValueKind.Null, parameters.RootElement[1].ValueKind);
    }

    [Fact]
    public async Task Condition_failure_returns_transaction_cancelled_with_positional_reasons()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":false,\"reasons\":[{\"code\":\"None\"},{\"code\":\"ConditionalCheckFailed\"}]}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "," +
            PutOp("2", "attribute_not_exists(pk)") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        Assert.Contains("TransactionCanceledException", doc.RootElement.GetProperty("__type").GetString());
        var reasons = doc.RootElement.GetProperty("CancellationReasons");
        Assert.Equal(2, reasons.GetArrayLength());
        Assert.Equal("None", reasons[0].GetProperty("Code").GetString());
        Assert.Equal("ConditionalCheckFailed", reasons[1].GetProperty("Code").GetString());
    }

    [Fact]
    public async Task Sproc_validation_error_maps_to_validation_exception()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(
                    "{\"success\":false,\"validationError\":{\"code\":\"ValidationException\",\"message\":\"TransactItems[0] condition validation failed: Incorrect operand type for begins_with.\"}}"),
            },
        };
        var request =
            """{"TransactItems":[{"Put":{"TableName":"orders","Item":{"pk":{"S":"a"},"sk":{"S":"1"}},"ConditionExpression":"NOT begins_with(v, :prefix)","ExpressionAttributeValues":{":prefix":{"S":"x"}}}}]}""";

        await Run(ctx, BuildClient(handler), EnabledSproc(), request);

        Assert.Equal(400, ctx.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("ValidationException", response);
        Assert.Contains("begins_with", response);
    }

    [Fact]
    public async Task Oversized_serialized_sproc_request_is_rejected_before_provisioning()
    {
        var payload = new string('x', 21_000);
        var request = new StringBuilder(
            "{\"ClientRequestToken\":\"bounded-many-put-token\"," +
            "\"TransactItems\":[");
        for (var index = 0; index < 100; index++)
        {
            if (index > 0)
            {
                request.Append(',');
            }
            request.Append(
                "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"")
                .Append(index)
                .Append("\"},\"payload\":{\"S\":\"")
                .Append(payload)
                .Append("\"}}}}");
        }
        request.Append("]}");

        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };
        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            request.ToString());

        Assert.Equal(400, ctx.Response.StatusCode);
        var response = ReadResponse(body);
        Assert.Contains("2097152", response, StringComparison.Ordinal);
        Assert.Contains("4 MiB", response, StringComparison.Ordinal);
        Assert.Single(handler.Requests);

        CosmosOpsShared.MetadataCache.Clear();
        var (retryContext, retryBody) = NewCtx();
        var retryHandler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetaPkSk) },
        };
        await Run(
            retryContext,
            BuildClient(retryHandler),
            EnabledSproc(),
            request.ToString());

        Assert.Equal(400, retryContext.Response.StatusCode);
        Assert.Equal(response, ReadResponse(retryBody));
        Assert.Single(retryHandler.Requests);
    }

    [Fact]
    public async Task Repeated_large_condition_value_references_fail_bounded_and_deterministically()
    {
        const int referenceCount = 100;
        var expression = string.Join(
            " OR ",
            Enumerable.Repeat("#value = :value", referenceCount));
        var largeValue = new string('x', 100 * 1024);
        var request =
            "{\"ClientRequestToken\":\"bounded-condition-token\"," +
            "\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"ConditionExpression\":\"" + expression + "\"," +
            "\"ExpressionAttributeNames\":{\"#value\":\"value\"}," +
            "\"ExpressionAttributeValues\":{\":value\":{\"S\":\"" +
            largeValue + "\"}}}}]}";

        static async Task<(string Response, ScriptedHandler Handler)> ExecuteAsync(
            string body)
        {
            CosmosOpsShared.MetadataCache.Clear();
            var (context, responseBody) = NewCtx();
            var handler = new ScriptedHandler
            {
                Responses = { CosmosOk(MetaPkSk) },
            };
            await Run(
                context,
                BuildClient(handler),
                EnabledSproc(),
                body);
            Assert.Equal(400, context.Response.StatusCode);
            return (ReadResponse(responseBody), handler);
        }

        var first = await ExecuteAsync(request);
        var second = await ExecuteAsync(request);

        Assert.Equal(first.Response, second.Response);
        Assert.Contains("ValidationException", first.Response);
        Assert.Contains("2097152", first.Response, StringComparison.Ordinal);
        Assert.Contains("4 MiB", first.Response, StringComparison.Ordinal);
        Assert.Single(first.Handler.Requests);
        Assert.Single(second.Handler.Requests);
        Assert.All(
            first.Handler.Requests.Concat(second.Handler.Requests),
            captured => Assert.DoesNotContain(
                "/sprocs/",
                captured.Uri.AbsolutePath,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Large_valid_condition_uses_exact_utf8_space_without_false_overflow()
    {
        var value = new string('x', 909 * 1024);
        var request =
            "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}," +
            "\"ConditionExpression\":\"#value = :value\"," +
            "\"ExpressionAttributeNames\":{\"#value\":\"value\"}," +
            "\"ExpressionAttributeValues\":{\":value\":{\"S\":\"" +
            value + "\"}}}}]}";
        var (context, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };

        await Run(
            context,
            BuildClient(handler),
            EnabledSproc(),
            request);

        Assert.Equal(200, context.Response.StatusCode);
        var execution = handler.Requests[^1];
        using var parameters = JsonDocument.Parse(execution.Body!);
        var condition = parameters.RootElement[0][0].GetProperty("condition");
        Assert.Equal(
            value.Length,
            condition.GetProperty("value").GetString()!.Length);
        Assert.True(
            Encoding.UTF8.GetByteCount(execution.Body!)
            < TransactWriteItemsHandler.MaxSprocRequestBodyBytes);
    }

    [Fact]
    public void Serialized_sproc_request_limit_is_inclusive_at_exact_boundary()
    {
        var emptyDoc = Encoding.UTF8.GetBytes("{\"payload\":\"\"}");
        var emptyOp = new TransactWriteItemsHandler.PreparedOp(
            TransactWriteItemsHandler.OpKind.Put,
            "1",
            emptyDoc,
            null);
        using var baseline =
            TransactWriteItemsHandler.BuildTransactParamsBody([emptyOp]);
        var fillerLength =
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes
            - baseline.WrittenMemory.Length;
        Assert.True(fillerLength > 0);

        var exactOp = new TransactWriteItemsHandler.PreparedOp(
            TransactWriteItemsHandler.OpKind.Put,
            "1",
            Encoding.UTF8.GetBytes(
                "{\"payload\":\"" + new string('x', fillerLength) + "\"}"),
            null);
        using var exact =
            TransactWriteItemsHandler.BuildTransactParamsBody([exactOp]);
        Assert.Equal(
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes,
            exact.WrittenMemory.Length);
        Assert.True(
            TransactWriteItemsHandler.IsWithinTransactRequestBodyLimit(
                exact.WrittenMemory.Length));

        var overOp = exactOp with
        {
            DocBytes = Encoding.UTF8.GetBytes(
                "{\"payload\":\"" + new string('x', fillerLength + 1) + "\"}"),
        };
        var exception = Assert.Throws<BoundedBufferWriterLimitException>(
            () => TransactWriteItemsHandler.BuildTransactParamsBody([overOp]));
        Assert.Equal(
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes,
            exception.Limit);
    }

    [Fact]
    public void Bounded_writer_honors_limit_without_rejecting_overestimated_hint()
    {
        using var writer = new BoundedPooledByteBufferWriter(
            maximumCapacity: 16,
            initialCapacity: 8,
            maximumScratchSizeHint: 32);

        var scratch = writer.GetSpan(17);
        scratch[..16].Fill((byte)'x');
        writer.Advance(16);

        Assert.Equal(16, writer.WrittenMemory.Length);
        Assert.Equal(16, writer.MaximumCapacity);
        writer.GetSpan(1)[0] = (byte)'y';
        Assert.Throws<BoundedBufferWriterLimitException>(
            () => writer.Advance(1));

        using var overflow = new BoundedPooledByteBufferWriter(
            maximumCapacity: 16,
            initialCapacity: 8,
            maximumScratchSizeHint: 32);
        overflow.GetSpan(17).Fill((byte)'x');
        var exception = Assert.Throws<BoundedBufferWriterLimitException>(
            () => overflow.Advance(17));
        Assert.Equal(16, exception.Limit);
        Assert.Equal(0, exception.WrittenBytes);
        Assert.Equal(17, exception.RequestedBytes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"reasons\":[]}")]
    [InlineData("{\"success\":\"true\"}")]
    public async Task Malformed_2xx_sproc_response_fails_closed(string response)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(response),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("malformed", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"success\":false,\"reasons\":[]}")]
    [InlineData("{\"success\":false,\"reasons\":[{\"code\":\"Unknown\"}]}")]
    [InlineData("{\"success\":false,\"reasons\":[{\"code\":\"ConditionalCheckFailed\"}]}")]
    public async Task Malformed_or_unjustified_cancellation_reasons_fail_closed(
        string response)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk(response),
            },
        };

        await Run(
            ctx,
            BuildClient(handler),
            EnabledSproc(),
            "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("cancellation reasons", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sproc_execution_error_maps_to_internal_error()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosStatus(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("InternalServerError", ReadResponse(body));
    }

    [Fact]
    public async Task Idempotent_transaction_stays_on_authoritative_write_region_after_failure()
    {
        var handler = new ScriptedHandler
        {
            AccountTopologyJson = MultiWriteAccountJson,
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosWriteRegionRejected(),
            },
        };
        var cosmos = BuildClient(
            handler,
            endpoint: "https://txn-write-global.documents.azure.com/",
            preferredRegions: ["West US", "East US"]);
        var sproc = EnabledSproc();
        const string request =
            "{\"ClientRequestToken\":\"pinned-token\",\"TransactItems\":[" +
            "{\"Put\":{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"a\"}," +
            "\"sk\":{\"S\":\"1\"},\"v\":{\"N\":\"1\"}}}}]}";

        var (firstContext, firstBody) = NewCtx();
        await Run(firstContext, cosmos, sproc, request);
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            firstContext.Response.StatusCode);
        Assert.Contains(
            "InternalServerError",
            ReadResponse(firstBody),
            StringComparison.Ordinal);

        var requestsAfterFailure = handler.Requests.Count;
        var (retryContext, _) = NewCtx();
        await Run(retryContext, cosmos, sproc, request);

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            retryContext.Response.StatusCode);
        Assert.Equal(requestsAfterFailure, handler.Requests.Count);
        Assert.DoesNotContain(
            handler.Requests,
            captured => captured.Uri.Host
                == "txn-write-east.documents.azure.com");
        Assert.All(
            handler.Requests.Where(requestItem =>
                requestItem.Uri.AbsolutePath.Contains(
                    "/sprocs",
                    StringComparison.Ordinal)),
            requestItem => Assert.Equal(
                "txn-write-west.documents.azure.com",
                requestItem.Uri.Host));
    }

    [Fact]
    public async Task Multi_write_transaction_without_matching_preference_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            AccountTopologyJson = MultiWriteAccountJson,
            Responses = { CosmosOk(MetaPkSk) },
        };

        await Run(
            ctx,
            BuildClient(
                handler,
                endpoint: "https://txn-write-unpinned.documents.azure.com/"),
            EnabledSproc(),
            "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains(
            "preferredRegions",
            ReadResponse(body),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            handler.Requests,
            requestItem => requestItem.Uri.AbsolutePath.Contains(
                "/sprocs",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multi_write_transaction_never_uses_later_preferred_region()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            AccountTopologyJson = MultiWriteLaterOnlyAccountJson,
            Responses = { CosmosOk(MetaPkSk) },
        };

        await Run(
            ctx,
            BuildClient(
                handler,
                endpoint:
                    "https://txn-write-authority-absent.documents.azure.com/",
                preferredRegions: ["West US", "East US"]),
            EnabledSproc(),
            "{\"ClientRequestToken\":\"stable-authority\",\"TransactItems\":[" +
            PutOp("1") + "]}");

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            ctx.Response.StatusCode);
        Assert.Contains(
            "West US",
            ReadResponse(body),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            handler.Requests,
            requestItem => requestItem.Uri.AbsolutePath.Contains(
                "/sprocs",
                StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            requestItem => requestItem.Uri.Host
                == "txn-write-east.documents.azure.com");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AccessDeniedException")]
    [InlineData(
        HttpStatusCode.TooManyRequests,
        "ProvisionedThroughputExceededException")]
    public async Task Topology_discovery_preserves_cosmos_error_mapping(
        HttpStatusCode topologyStatus,
        string expectedCode)
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            AccountTopologyStatus = topologyStatus,
            Responses = { CosmosOk(MetaPkSk) },
        };

        await Run(
            ctx,
            BuildClient(
                handler,
                endpoint:
                    $"https://txn-topology-{(int)topologyStatus}.documents.azure.com/"),
            EnabledSproc(),
            "{\"TransactItems\":[" + PutOp("1") + "]}");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains(expectedCode, ReadResponse(body), StringComparison.Ordinal);
        Assert.DoesNotContain(
            handler.Requests,
            requestItem => requestItem.Uri.AbsolutePath.Contains(
                "/sprocs",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_operation_builds_delete_op()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaPkSk),
                CosmosCreated(),
                CosmosOk("{\"success\":true}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[{\"Delete\":{\"TableName\":\"orders\"," +
            "\"Key\":{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"1\"}}}}]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(200, ctx.Response.StatusCode);
        var exec = handler.Requests[^1];
        Assert.Contains("\"type\":\"DELETE\"", exec.Body!);
    }

    [Fact]
    public async Task Missing_table_returns_resource_not_found()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosStatus(HttpStatusCode.NotFound, "{\"code\":\"NotFound\"}") },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" + PutOp("1") + "]}";

        await Run(ctx, cosmos, EnabledSproc(), req);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = new();
        public List<CapturedRequest> Requests { get; } = new();
        public string? AccountTopologyJson { get; init; }
        public HttpStatusCode? AccountTopologyStatus { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath == "/")
            {
                if (AccountTopologyStatus is { } status)
                {
                    return CosmosStatus(status);
                }
                return CosmosOk(
                    AccountTopologyJson
                    ?? SingleWriteAccountJson(request.RequestUri));
            }

            string? bodyText = null;
            if (request.Content is not null)
            {
                bodyText = await request.Content.ReadAsStringAsync(ct);
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            if (request.Content is not null)
            {
                foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            }
            lock (Requests)
            {
                Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, bodyText));
            }
            HttpResponseMessage? next;
            lock (Responses)
            {
                if (Responses.Count == 0)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                next = Responses[0];
                Responses.RemoveAt(0);
            }
            return next;
        }

        private static string SingleWriteAccountJson(Uri endpoint)
        {
            var accountEndpoint = endpoint.GetLeftPart(UriPartial.Authority) + "/";
            return $$"""
                {
                  "enableMultipleWriteLocations": false,
                  "readableLocations": [
                    { "name": "East US", "databaseAccountEndpoint": "{{accountEndpoint}}" }
                  ],
                  "writableLocations": [
                    { "name": "East US", "databaseAccountEndpoint": "{{accountEndpoint}}" }
                  ]
                }
                """;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body);
}
