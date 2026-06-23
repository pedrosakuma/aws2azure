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
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Exercise the four table-lifecycle handlers end-to-end against a
/// scripted Cosmos REST surface. The handlers own JSON parsing,
/// validation, Cosmos request shape, and DynamoDB-shaped response
/// rendering, so the assertions cover all four boundaries.
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class TableLifecycleHandlersTests
{
    public TableLifecycleHandlersTests()
    {
        // Clear metadata cache at test start to ensure isolation
        CosmosOpsShared.MetadataCache.Clear();
    }

    private static CosmosClient BuildClient(ScriptedHandler handler, string db = "main")
    {
        var http = new AzureHttpClient(handler, ownsHandler: false,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        var creds = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com/",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = db,
        };
        return new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));
    }

    private static (DefaultHttpContext ctx, MemoryStream body) NewCtx()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    private static string ReadResponse(MemoryStream body)
    {
        body.Position = 0;
        return new StreamReader(body, Encoding.UTF8).ReadToEnd();
    }

    [Fact]
    public async Task CreateTable_rejects_short_table_name()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes("{\"TableName\":\"ab\",\"AttributeDefinitions\":[],\"KeySchema\":[]}");
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleCreateTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Empty(handler.Requests); // Validation fails before any Cosmos call.
        Assert.Contains("ValidationException", ReadResponse(body));
    }

    [Fact]
    public async Task CreateTable_rejects_missing_key_schema()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes(
            "{\"TableName\":\"orders\",\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}]}");
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleCreateTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("KeySchema", ReadResponse(body));
    }

    [Fact]
    public async Task CreateTable_rejects_key_attr_not_in_definitions()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes(
            "{\"TableName\":\"orders\",\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}],"
            + "\"KeySchema\":[{\"AttributeName\":\"sk\",\"KeyType\":\"HASH\"}]}");
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleCreateTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateTable_posts_to_colls_then_creates_metadata()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes(
            "{\"TableName\":\"orders\","
            + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"},{\"AttributeName\":\"sk\",\"AttributeType\":\"N\"}],"
            + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"},{\"AttributeName\":\"sk\",\"KeyType\":\"RANGE\"}]}");

        var handler = new ScriptedHandler
        {
            Responses =
            {
                // POST /dbs/main/colls
                new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"id\":\"orders\"}", Encoding.UTF8, "application/json"),
                },
                // POST /dbs/main/colls/orders/docs  (metadata create)
                new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"id\":\"__aws2azure_table_meta__\"}", Encoding.UTF8, "application/json"),
                },
            },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleCreateTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);

        var create = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.EndsWith("/dbs/main/colls", create.Uri.AbsolutePath);
        Assert.Contains("\"id\":\"orders\"", create.Body!);
        Assert.Contains("\"paths\":[\"/_a2a_pk\"]", create.Body!);

        var meta = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, meta.Method);
        Assert.EndsWith("/dbs/main/colls/orders/docs", meta.Uri.AbsolutePath);
        Assert.False(meta.Headers.ContainsKey("x-ms-documentdb-is-upsert"));
        Assert.Equal("[\"__aws2azure_table_meta__\"]", meta.Headers["x-ms-documentdb-partitionkey"]);

        var bodyJson = ReadResponse(body);
        using var doc = JsonDocument.Parse(bodyJson);
        var desc = doc.RootElement.GetProperty("TableDescription");
        Assert.Equal("orders", desc.GetProperty("TableName").GetString());
        Assert.Equal("ACTIVE", desc.GetProperty("TableStatus").GetString());
        Assert.Equal(2, desc.GetProperty("AttributeDefinitions").GetArrayLength());
        Assert.Equal(2, desc.GetProperty("KeySchema").GetArrayLength());
    }

    [Fact]
    public async Task CreateTable_metadata_conflict_preserves_concurrent_tags()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes(
            "{\"TableName\":\"orders\","
            + "\"BillingMode\":\"PAY_PER_REQUEST\","
            + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"},{\"AttributeName\":\"sk\",\"AttributeType\":\"N\"}],"
            + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"},{\"AttributeName\":\"sk\",\"KeyType\":\"RANGE\"}]}");

        var existing = "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
            + "\"tableName\":\"orders\",\"tags\":[{\"key\":\"env\",\"value\":\"prod\"}]}";
        var readExisting = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(existing, Encoding.UTF8, "application/json"),
        };
        readExisting.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"tag-v1\"");

        var handler = new ScriptedHandler
        {
            Responses =
            {
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") },
                new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") },
                readExisting,
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
            },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleCreateTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.False(handler.Requests[1].Headers.ContainsKey("x-ms-documentdb-is-upsert"));
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[3].Method);
        Assert.Equal("\"tag-v1\"", handler.Requests[3].Headers["If-Match"]);

        using var persisted = JsonDocument.Parse(handler.Requests[3].Body!);
        var root = persisted.RootElement;
        Assert.Equal("PAY_PER_REQUEST", root.GetProperty("billingMode").GetString());
        Assert.Equal(2, root.GetProperty("attributeDefinitions").GetArrayLength());
        Assert.Equal(2, root.GetProperty("keySchema").GetArrayLength());
        Assert.Equal("env", root.GetProperty("tags")[0].GetProperty("key").GetString());
        Assert.Equal("prod", root.GetProperty("tags")[0].GetProperty("value").GetString());

        using var responseDoc = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(2, responseDoc.RootElement.GetProperty("TableDescription")
            .GetProperty("KeySchema").GetArrayLength());
    }

    [Fact]
    public async Task CreateTable_409_returns_ResourceInUseException()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes(
            "{\"TableName\":\"orders\","
            + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}],"
            + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}]}");

        var handler = new ScriptedHandler
        {
            Responses = { new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("") } },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleCreateTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceInUseException", ReadResponse(body));
        Assert.Single(handler.Requests); // metadata POST is skipped after the conflict.
    }

    [Fact]
    public async Task CreateTable_metadata_failure_rolls_back_container()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes(
            "{\"TableName\":\"orders\","
            + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}],"
            + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}]}");

        var handler = new ScriptedHandler
        {
            Responses =
            {
                new HttpResponseMessage(HttpStatusCode.Created)  { Content = new StringContent("{}") },
                new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad meta") },
                new HttpResponseMessage(HttpStatusCode.NoContent), // rollback delete
            },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleCreateTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.EndsWith("/dbs/main/colls/orders", handler.Requests[2].Uri.AbsolutePath);
    }

    [Fact]
    public async Task DeleteTable_returns_ResourceNotFound_when_cosmos_404()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes("{\"TableName\":\"orders\"}");

        var handler = new ScriptedHandler
        {
            Responses =
            {
                // Metadata read: 404 → meta = null path is fine.
                new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
                // DELETE colls: 404 too → ResourceNotFoundException.
                new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
            },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleDeleteTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task DeleteTable_returns_description_with_DELETING_status()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes("{\"TableName\":\"orders\"}");

        var metaJson = "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\","
            + "\"tableName\":\"orders\",\"creationDateTime\":1700000000,"
            + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}],"
            + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}]}";
        var handler = new ScriptedHandler
        {
            Responses =
            {
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metaJson, Encoding.UTF8, "application/json") },
                new HttpResponseMessage(HttpStatusCode.NoContent),
            },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleDeleteTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var desc = doc.RootElement.GetProperty("TableDescription");
        Assert.Equal("orders", desc.GetProperty("TableName").GetString());
        Assert.Equal("DELETING", desc.GetProperty("TableStatus").GetString());
    }

    [Fact]
    public async Task DescribeTable_round_trips_attribute_definitions_via_metadata()
    {
        var (ctx, body) = NewCtx();
        var req = Encoding.UTF8.GetBytes("{\"TableName\":\"orders\"}");

        var metaJson = "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\","
            + "\"tableName\":\"orders\",\"creationDateTime\":1700000000,"
            + "\"billingMode\":\"PAY_PER_REQUEST\","
            + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"},{\"name\":\"sk\",\"type\":\"N\"}],"
            + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"},{\"name\":\"sk\",\"keyType\":\"RANGE\"}]}";
        var handler = new ScriptedHandler
        {
            Responses =
            {
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"orders\"}") }, // GET colls
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metaJson, Encoding.UTF8, "application/json") },
            },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleDescribeTableAsync(ctx, req, cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var table = doc.RootElement.GetProperty("Table");
        Assert.Equal("orders", table.GetProperty("TableName").GetString());
        Assert.Equal("ACTIVE", table.GetProperty("TableStatus").GetString());
        Assert.Equal("PAY_PER_REQUEST", table.GetProperty("BillingModeSummary").GetProperty("BillingMode").GetString());
        Assert.Equal(2, table.GetProperty("AttributeDefinitions").GetArrayLength());
        Assert.Equal(2, table.GetProperty("KeySchema").GetArrayLength());
        Assert.StartsWith("arn:aws:dynamodb:azure:", table.GetProperty("TableArn").GetString()!);
    }

    [Fact]
    public async Task DescribeTable_returns_ResourceNotFound_when_container_missing()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") } },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleDescribeTableAsync(
            ctx, Encoding.UTF8.GetBytes("{\"TableName\":\"orders\"}"), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task ListTables_returns_sorted_names_with_pagination()
    {
        var (ctx, body) = NewCtx();
        var listJson = "{\"DocumentCollections\":[{\"id\":\"users\"},{\"id\":\"orders\"},{\"id\":\"events\"},{\"id\":\"audit\"}],\"_count\":4}";

        var handler = new ScriptedHandler
        {
            Responses = { new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson, Encoding.UTF8, "application/json") } },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleListTablesAsync(
            ctx, Encoding.UTF8.GetBytes("{\"Limit\":2}"), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var names = doc.RootElement.GetProperty("TableNames");
        Assert.Equal(2, names.GetArrayLength());
        // Ordinal sort: audit, events, orders, users → first page = audit, events.
        Assert.Equal("audit", names[0].GetString());
        Assert.Equal("events", names[1].GetString());
        Assert.Equal("events", doc.RootElement.GetProperty("LastEvaluatedTableName").GetString());
    }

    [Fact]
    public async Task ListTables_honours_exclusive_start_cursor()
    {
        var (ctx, body) = NewCtx();
        var listJson = "{\"DocumentCollections\":[{\"id\":\"audit\"},{\"id\":\"events\"},{\"id\":\"orders\"},{\"id\":\"users\"}],\"_count\":4}";

        var handler = new ScriptedHandler
        {
            Responses = { new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listJson, Encoding.UTF8, "application/json") } },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleListTablesAsync(
            ctx, Encoding.UTF8.GetBytes("{\"ExclusiveStartTableName\":\"events\",\"Limit\":100}"),
            cosmos, CancellationToken.None);

        using var doc = JsonDocument.Parse(ReadResponse(body));
        var names = doc.RootElement.GetProperty("TableNames");
        Assert.Equal(2, names.GetArrayLength());
        Assert.Equal("orders", names[0].GetString());
        Assert.Equal("users", names[1].GetString());
        Assert.False(doc.RootElement.TryGetProperty("LastEvaluatedTableName", out var _));
    }

    [Fact]
    public async Task ListTables_empty_body_uses_defaults()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"DocumentCollections\":[],\"_count\":0}") } },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleListTablesAsync(ctx, Array.Empty<byte>(), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(0, doc.RootElement.GetProperty("TableNames").GetArrayLength());
    }

    [Fact]
    public async Task ListTables_rejects_limit_over_100()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleListTablesAsync(
            ctx, Encoding.UTF8.GetBytes("{\"Limit\":1000}"), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
    }

    [Fact]
    public async Task Cosmos_429_maps_to_provisioned_throughput_exceeded()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("RU throttled") } },
        };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleDescribeTableAsync(
            ctx, Encoding.UTF8.GetBytes("{\"TableName\":\"orders\"}"), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ProvisionedThroughputExceededException", ReadResponse(body));
    }

    [Fact]
    public void DynamoDbNames_validation_boundaries()
    {
        Assert.False(DynamoDbNames.IsValidTableName(null));
        Assert.False(DynamoDbNames.IsValidTableName(""));
        Assert.False(DynamoDbNames.IsValidTableName("ab"));
        Assert.True(DynamoDbNames.IsValidTableName("abc"));
        Assert.True(DynamoDbNames.IsValidTableName("a-_.123"));
        Assert.False(DynamoDbNames.IsValidTableName("contains space"));
        Assert.False(DynamoDbNames.IsValidTableName("não-ascii-é"));
        Assert.True(DynamoDbNames.IsValidTableName(new string('x', 255)));
        Assert.False(DynamoDbNames.IsValidTableName(new string('x', 256)));
    }

    [Fact]
    public void BuildPartitionKeyHeader_escapes_special_chars()
    {
        Assert.Equal("[\"orders\"]", CosmosOpsShared.BuildPartitionKeyHeader("orders"));
        // JSON-escape of inner quote + backslash to ensure the helper goes
        // through JsonEncodedText rather than naive concat.
        var encoded = CosmosOpsShared.BuildPartitionKeyHeader("a\"b");
        Assert.Contains("\\u0022", encoded);
    }

    [Fact]
    public void ParseContainerNames_handles_empty_array_and_missing_field()
    {
        using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("{\"DocumentCollections\":[],\"_count\":0}"));
        Assert.Empty(TableLifecycleHandlers.ParseContainerNames(s1));

        using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("{\"_count\":0}"));
        Assert.Empty(TableLifecycleHandlers.ParseContainerNames(s2));

        using var s3 = new MemoryStream(Encoding.UTF8.GetBytes("{\"DocumentCollections\":[{\"id\":\"a\"},{\"notid\":\"b\"}]}"));
        var got = TableLifecycleHandlers.ParseContainerNames(s3);
        Assert.Single(got);
        Assert.Equal("a", got[0]);
    }

    [Fact]
    public async Task CreateTable_rejects_global_secondary_indexes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\",\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}],"
                      + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}],"
                      + "\"GlobalSecondaryIndexes\":[{\"IndexName\":\"gsi1\"}]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("GlobalSecondaryIndexes", resp);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateTable_rejects_local_secondary_indexes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\",\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}],"
                      + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}],"
                      + "\"LocalSecondaryIndexes\":[{\"IndexName\":\"lsi1\"}]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("LocalSecondaryIndexes", ReadResponse(body));
    }

    [Fact]
    public async Task CreateTable_accepts_empty_index_arrays()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"id\":\"orders\"}") },
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") },
            },
        };
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\",\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}],"
                      + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}],"
                      + "\"GlobalSecondaryIndexes\":[],\"LocalSecondaryIndexes\":[]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task CreateTable_rejects_invalid_billing_mode()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\",\"BillingMode\":\"SOMETHING\","
                      + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}],"
                      + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("BillingMode", ReadResponse(body));
    }

    [Fact]
    public async Task CreateTable_rejects_range_before_hash()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\","
                      + "\"KeySchema\":[{\"AttributeName\":\"sk\",\"KeyType\":\"RANGE\"},{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}],"
                      + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"},{\"AttributeName\":\"sk\",\"AttributeType\":\"S\"}]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("HASH", ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateTable_rejects_duplicate_key_attribute()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\","
                      + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"},{\"AttributeName\":\"pk\",\"KeyType\":\"RANGE\"}],"
                      + "\"AttributeDefinitions\":[{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"}]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("differ", ReadResponse(body));
    }

    [Fact]
    public async Task CreateTable_rejects_unused_attribute_definition()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\","
                      + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}],"
                      + "\"AttributeDefinitions\":["
                      + "{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"},"
                      + "{\"AttributeName\":\"orphan\",\"AttributeType\":\"S\"}]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("orphan", ReadResponse(body));
    }

    [Fact]
    public async Task CreateTable_rejects_duplicate_attribute_definition()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var reqBody = "{\"TableName\":\"orders\","
                      + "\"KeySchema\":[{\"AttributeName\":\"pk\",\"KeyType\":\"HASH\"}],"
                      + "\"AttributeDefinitions\":["
                      + "{\"AttributeName\":\"pk\",\"AttributeType\":\"S\"},"
                      + "{\"AttributeName\":\"pk\",\"AttributeType\":\"N\"}]}";
        await TableLifecycleHandlers.HandleCreateTableAsync(
            ctx, Encoding.UTF8.GetBytes(reqBody), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("duplicate", ReadResponse(body));
    }

    [Fact]
    public async Task ListTables_follows_cosmos_continuation_token()
    {
        var (ctx, body) = NewCtx();
        var page1 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"DocumentCollections\":[{\"id\":\"users\"},{\"id\":\"orders\"}],\"_count\":2}",
                Encoding.UTF8, "application/json"),
        };
        page1.Headers.TryAddWithoutValidation("x-ms-continuation", "page2token");
        var page2 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"DocumentCollections\":[{\"id\":\"audit\"},{\"id\":\"events\"}],\"_count\":2}",
                Encoding.UTF8, "application/json"),
        };

        var handler = new ScriptedHandler { Responses = { page1, page2 } };
        var cosmos = BuildClient(handler);

        await TableLifecycleHandlers.HandleListTablesAsync(
            ctx, Encoding.UTF8.GetBytes("{\"Limit\":100}"), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var names = doc.RootElement.GetProperty("TableNames");
        Assert.Equal(4, names.GetArrayLength());
        Assert.Equal("audit", names[0].GetString());
        Assert.Equal("users", names[3].GetString());

        // Second request must carry the continuation header from the first.
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(handler.Requests[0].Headers.ContainsKey("x-ms-continuation"));
        Assert.Equal("page2token", handler.Requests[1].Headers["x-ms-continuation"]);
    }


    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = new();
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Capture content + headers eagerly — by the time the test reads them
            // the HttpClient has disposed the original request.
            string? body = null;
            string? contentType = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(ct);
                contentType = request.Content.Headers.ContentType?.ToString();
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, body, contentType));

            if (Responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var next = Responses[0];
            Responses.RemoveAt(0);
            return next;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        Dictionary<string, string> Headers,
        string? Body,
        string? ContentType);
}
