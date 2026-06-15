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
/// Coverage for <see cref="QueryHandler"/> against a scripted Cosmos
/// REST surface. Pins:
/// <list type="bullet">
///   <item>HASH-only / composite KCE translation to partition-scoped
///   Cosmos SQL.</item>
///   <item>FilterExpression evaluated in-process (Count vs ScannedCount
///   divergence).</item>
///   <item>ProjectionExpression on top-level attributes.</item>
///   <item>ScanIndexForward / ConsistentRead header plumbing.</item>
///   <item>Limit + pagination round-trip via the
///   <c>__a2a_continuation</c> sentinel.</item>
///   <item>Loud rejection of IndexName and legacy KeyConditions /
///   QueryFilter.</item>
/// </list>
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class QueryHandlerTests
{
    public QueryHandlerTests()
    {
        // Clear metadata cache at test start to ensure isolation
        CosmosOpsShared.MetadataCache.Clear();
    }

    private const string Table = "orders";

    private static readonly string MetadataHashOnly =
        "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\","
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}],"
        + "\"billingMode\":\"PAY_PER_REQUEST\"}";

    private static readonly string MetadataComposite =
        "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\","
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"},{\"name\":\"sk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"},{\"name\":\"sk\",\"keyType\":\"RANGE\"}],"
        + "\"billingMode\":\"PAY_PER_REQUEST\"}";

    private static CosmosClient BuildClient(ScriptedHandler handler)
    {
        var http = new AzureHttpClient(handler, ownsHandler: false,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        var creds = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com/",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };
        return new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));
    }

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

    private static HttpResponseMessage CosmosOk(string body, string? continuation = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (continuation is not null)
            r.Headers.TryAddWithoutValidation("x-ms-continuation", continuation);
        return r;
    }

    private static HttpResponseMessage CosmosOkBytes(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string DocWithItem(string pk, string id, string itemJson)
    {
        using var d = JsonDocument.Parse(itemJson);
        return Aws2Azure.Modules.DynamoDb.Persistence.InferredAttributeStorage.BuildCosmosDocument(id, pk, d.RootElement);
    }

    private static string QueryEnvelope(params string[] docs)
    {
        var sb = new StringBuilder("{\"_rid\":\"x\",\"Documents\":[");
        for (int i = 0; i < docs.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(docs[i]);
        }
        sb.Append("],\"_count\":").Append(docs.Length).Append('}');
        return sb.ToString();
    }

    private static string CountEnvelope(long n)
        => "{\"_rid\":\"x\",\"Documents\":[" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "],\"_count\":1}";

    // Materialized oracle: rebuild the QueryResponse exactly as the
    // legacy (non-fused) path would and serialize it via the same
    // source-gen context. The fused handler output must be byte-identical.
    private static byte[] MaterializedQuery(string? continuation, params string[] cosmosDocs)
    {
        var items = new List<Dictionary<string, JsonElement>>();
        foreach (var d in cosmosDocs)
        {
            using var doc = JsonDocument.Parse(d);
            items.Add(Aws2Azure.Modules.DynamoDb.Persistence.InferredAttributeStorage.ExtractItem(doc.RootElement)!);
        }
        var resp = new QueryResponse
        {
            Items = items,
            Count = items.Count,
            ScannedCount = items.Count,
        };
        if (continuation is not null)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(continuation));
            using var keyDoc = JsonDocument.Parse("{\"__a2a_continuation\":{\"S\":" + JsonSerializer.Serialize(b64) + "}}");
            var key = new Dictionary<string, JsonElement>();
            foreach (var p in keyDoc.RootElement.EnumerateObject())
                key[p.Name] = p.Value.Clone();
            resp.LastEvaluatedKey = key;
        }
        return JsonSerializer.SerializeToUtf8Bytes(resp, QueryJsonContext.Default.QueryResponse);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("page-token-/+=")]
    public async Task Query_fused_is_byte_identical_to_materialized(string? continuation)
    {
        // Rich corpus over a single partition (pk = "a"): escaping
        // surface, number, binary, sets, nested map/list, bool, null.
        string d1 = DocWithItem("a", "a",
            "{\"pk\":{\"S\":\"a\"},\"name\":{\"S\":\"\\\"héllo\\\" <b>&</b> \\uD83D\\uDE00\\tend\"}}");
        string d2 = DocWithItem("a", "b",
            "{\"pk\":{\"S\":\"a\"},\"n\":{\"N\":\"123.45\"},\"bin\":{\"B\":\"AQID\"},"
            + "\"ss\":{\"SS\":[\"x\",\"y\"]},\"ns\":{\"NS\":[\"1\",\"2\"]},\"flag\":{\"BOOL\":true}}");
        string d3 = DocWithItem("a", "c",
            "{\"pk\":{\"S\":\"a\"},\"nested\":{\"M\":{\"inner\":{\"L\":[{\"S\":\"z\"},{\"NULL\":true}]}}}}");

        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(d1, d2, d3), continuation),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}},"
                  + "\"Limit\":3}";
        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("application/x-amz-json-1.0", ctx.Response.ContentType);
        body.Position = 0;
        var actual = body.ToArray();
        var expected = MaterializedQuery(continuation, d1, d2, d3);
        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public async Task Query_pushed_filter_recovers_scanned_count_via_partition_scoped_aggregate()
    {
        // Filter pushed into Cosmos → streamed docs are pre-filtered, so the
        // faithful pre-filter ScannedCount is recovered with a SELECT VALUE
        // COUNT(1) over the same single-partition key scope, minus the filter.
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"9\"}}"),
                    DocWithItem("a", "b", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"8\"}}"))),
                CosmosOk(CountEnvelope(5)),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"FilterExpression\":\"v > :min\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":min\":{\"N\":\"3\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(2, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.Equal(5, resp.RootElement.GetProperty("ScannedCount").GetInt32());

        Assert.Equal(3, handler.Requests.Count);
        var countReq = handler.Requests[2];
        Assert.Contains("SELECT VALUE COUNT(1)", countReq.Body);
        Assert.DoesNotContain("@fp0", countReq.Body);
        Assert.DoesNotContain("ORDER BY", countReq.Body);
        // The aggregate stays scoped to the queried partition (single-partition).
        var pkHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes("a"));
        Assert.Equal($"[\"{pkHex}\"]", countReq.Headers["x-ms-documentdb-partitionkey"]);
        Assert.False(countReq.Headers.ContainsKey("x-ms-documentdb-query-enablecrosspartition"));
    }

    [Fact]
    public async Task Query_accepts_cosmos_binary_query_pages()
    {
        var (ctx, body) = NewCtx();
        string page = QueryEnvelope(
            DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"9\"}}"));
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOkBytes(CosmosBinaryTestEncoder.Encode(page)),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.Equal("a", resp.RootElement.GetProperty("Items")[0].GetProperty("pk").GetProperty("S").GetString());
        Assert.Equal("9", resp.RootElement.GetProperty("Items")[0].GetProperty("v").GetProperty("N").GetString());
    }

    [Fact]
    public async Task Query_hash_only_returns_matching_item()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        var queryReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, queryReq.Method);
        var pkHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes("a"));
        Assert.Equal($"[\"{pkHex}\"]", queryReq.Headers["x-ms-documentdb-partitionkey"]);
        Assert.Equal("true", queryReq.Headers["x-ms-documentdb-isquery"]);
        Assert.Contains("c._a2a = 'item'", queryReq.Body);
        Assert.DoesNotContain("ORDER BY", queryReq.Body);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        var root = resp.RootElement;
        Assert.Equal(1, root.GetProperty("Count").GetInt32());
        Assert.Equal(1, root.GetProperty("ScannedCount").GetInt32());
        Assert.Equal("a", root.GetProperty("Items")[0].GetProperty("pk").GetProperty("S").GetString());
        Assert.False(root.TryGetProperty("LastEvaluatedKey", out _));
    }

    [Fact]
    public async Task Query_composite_with_sk_between_translates_to_sql()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataComposite),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "b", "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"b\"}}"),
                    DocWithItem("a", "c", "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"c\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p AND sk BETWEEN :lo AND :hi\","
                  + "\"ExpressionAttributeValues\":{"
                  + "\":p\":{\"S\":\"a\"},\":lo\":{\"S\":\"b\"},\":hi\":{\"S\":\"c\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        var queryReq = handler.Requests[1];
        Assert.Contains("c.id >= @skLo", queryReq.Body);
        Assert.Contains("c.id <= @skHi", queryReq.Body);
        Assert.Contains("ORDER BY c.id ASC", queryReq.Body);
        using var qbody = JsonDocument.Parse(queryReq.Body!);
        var parameters = qbody.RootElement.GetProperty("parameters");
        Assert.Equal(2, parameters.GetArrayLength());

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(2, resp.RootElement.GetProperty("Count").GetInt32());
    }

    [Fact]
    public async Task Query_begins_with_sort_key()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataComposite),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "ord#1", "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"ord#1\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p AND begins_with(sk, :pre)\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":pre\":{\"S\":\"ord#\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        var queryReq = handler.Requests[1];
        Assert.Contains("STARTSWITH(c.id, @sk0)", queryReq.Body);
    }

    [Fact]
    public async Task Query_filter_expression_applies_in_process()
    {
        // Uses a non-pushable predicate (size() always stays residual)
        // so the in-process evaluator is exercised against the docs
        // the stub Cosmos returns verbatim.
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"x\"}}"),
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"longer\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"FilterExpression\":\"size(v) > :min\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":min\":{\"N\":\"3\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.Equal(2, resp.RootElement.GetProperty("ScannedCount").GetInt32());
    }

    [Fact]
    public async Task Query_filter_expression_pushdown_appends_clause_to_cosmos_sql()
    {
        // A pushable predicate (v > :min over a number) must arrive at
        // Cosmos as a hybrid IS_NUMBER / _a2a:N branch in the WHERE
        // clause, not as in-process post-filtering.
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"FilterExpression\":\"v > :min\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":min\":{\"N\":\"2\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        var queryReq = handler.Requests[1];
        // Body is JSON-encoded so `"` is escaped as `\"`.
        Assert.Contains("IS_NUMBER(c[\\\"v\\\"])", queryReq.Body);
        Assert.Contains("_a2a:N", queryReq.Body);
        Assert.Contains("@fp0", queryReq.Body);
    }

    [Fact]
    public async Task Query_projection_expression_drops_other_attributes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"},\"extra\":{\"S\":\"hidden\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ProjectionExpression\":\"pk, #v\","
                  + "\"ExpressionAttributeNames\":{\"#v\":\"v\"},"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        var item = resp.RootElement.GetProperty("Items")[0];
        Assert.True(item.TryGetProperty("pk", out _));
        Assert.True(item.TryGetProperty("v", out _));
        Assert.False(item.TryGetProperty("extra", out _));
    }

    [Fact]
    public async Task Query_select_count_omits_items_array()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"Select\":\"COUNT\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.False(resp.RootElement.TryGetProperty("Items", out _));
    }

    [Fact]
    public async Task Query_consistent_read_sets_strong_consistency_header()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ConsistentRead\":true,"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("Strong", handler.Requests[1].Headers["x-ms-consistency-level"]);
    }

    [Fact]
    public async Task Query_scan_index_forward_false_emits_desc_order()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataComposite),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ScanIndexForward\":false,"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Contains("ORDER BY c.id DESC", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Query_returns_last_evaluated_key_on_limit_hit()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataComposite),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "b", "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"b\"}}"),
                    DocWithItem("a", "c", "{\"pk\":{\"S\":\"a\"},\"sk\":{\"S\":\"c\"}}")),
                    continuation: "TOKEN-XYZ"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"Limit\":2,"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.True(resp.RootElement.TryGetProperty("LastEvaluatedKey", out var lek));
        var sentinel = lek.GetProperty("__a2a_continuation").GetProperty("S").GetString()!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(sentinel));
        Assert.Equal("TOKEN-XYZ", decoded);
    }

    [Fact]
    public async Task Query_exclusive_start_key_round_trips_continuation()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("RESUME-1"));
        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ExclusiveStartKey\":{\"__a2a_continuation\":{\"S\":\"" + b64 + "\"}},"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("RESUME-1", handler.Requests[1].Headers["x-ms-continuation"]);
    }

    [Fact]
    public async Task Query_index_name_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"IndexName\":\"gsi1\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("secondary indexes", ReadResponse(body));
    }

    [Fact]
    public async Task Query_legacy_key_conditions_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"KeyConditions\":{\"pk\":{\"AttributeValueList\":[{\"S\":\"a\"}],\"ComparisonOperator\":\"EQ\"}},"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Legacy KeyConditions", ReadResponse(body));
    }

    [Fact]
    public async Task Query_missing_table_returns_resource_not_found()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"missing\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task Query_keycondition_without_hash_equality_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetadataHashOnly) },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk > :p\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
    }

    [Fact]
    public async Task Query_sort_key_predicate_against_hash_only_table_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetadataHashOnly) },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p AND sk = :s\","
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":s\":{\"S\":\"b\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Query_filter_expression_with_limit_caps_scanned_not_matched()
    {
        // DynamoDB Limit caps *evaluated* (pre-filter) items. With
        // Limit=2 and a page that returns 2 items where the filter
        // keeps only 1, we stop after page 1 (scanned == Limit) and
        // return whatever continuation the page came with — we do NOT
        // fetch a second page to top up matches. Uses a residual (size)
        // predicate so the filter executes in-process against both
        // returned docs (stub Cosmos ignores SQL).
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"x\"}}"),
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"longer\"}}")),
                    continuation: "P2"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"FilterExpression\":\"size(v) > :min\","
                  + "\"Limit\":2,"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":min\":{\"N\":\"3\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        // Exactly one Cosmos query call (no second-page fetch).
        Assert.Equal(2, handler.Requests.Count);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.Equal(2, resp.RootElement.GetProperty("ScannedCount").GetInt32());
        Assert.True(resp.RootElement.TryGetProperty("LastEvaluatedKey", out _));
    }

    [Fact]
    public async Task Query_with_pushdown_and_limit_stops_after_first_non_empty_page()
    {
        // When the filter is pushed into the Cosmos SQL, our `scanned`
        // counter is post-prefilter and cannot model DDB's evaluated-
        // items cap. Continuing across continuation pages to top up
        // matches would silently move the page boundary forward. So
        // we stop after the first non-empty page and surface its
        // continuation as LastEvaluatedKey. Limit=5 + page1 returns
        // 1 row + has continuation → exactly one Cosmos call, 1 item
        // returned, LastEvaluatedKey present.
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataHashOnly),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"10\"}}")),
                    continuation: "P2"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"KeyConditionExpression\":\"pk = :p\","
                  + "\"FilterExpression\":\"v > :min\","
                  + "\"Limit\":5,"
                  + "\"ExpressionAttributeValues\":{\":p\":{\"S\":\"a\"},\":min\":{\"N\":\"2\"}}}";

        await QueryHandler.HandleQueryAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(2, handler.Requests.Count);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.True(resp.RootElement.TryGetProperty("LastEvaluatedKey", out _));
    }

    // ---- harness ----

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = new();
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(ct);
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            if (request.Content is not null)
            {
                foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            }
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, body));

            if (Responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var next = Responses[0];
            Responses.RemoveAt(0);
            return next;
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body);
}
