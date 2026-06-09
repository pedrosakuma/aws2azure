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
/// Coverage for <see cref="ScanHandler"/> against a scripted Cosmos
/// REST surface. Pins:
/// <list type="bullet">
///   <item>Cross-partition Cosmos query header
///   (<c>x-ms-documentdb-query-enablecrosspartition</c>) is set and
///   no partition-key header is emitted.</item>
///   <item>FilterExpression evaluated in-process — Count vs
///   ScannedCount divergence.</item>
///   <item>ProjectionExpression on top-level attributes.</item>
///   <item>Limit caps scanned (pre-filter) items.</item>
///   <item>ExclusiveStartKey / LastEvaluatedKey round-trip via the
///   <c>__a2a_continuation</c> sentinel.</item>
///   <item>Loud rejection of IndexName, parallel-scan parameters,
///   and legacy ScanFilter / AttributesToGet.</item>
/// </list>
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class ScanHandlerTests
{
    public ScanHandlerTests()
    {
        // Clear metadata cache at test start to ensure isolation
        CosmosOpsShared.MetadataCache.Clear();
    }

    private const string Table = "orders";

    private static readonly string Metadata =
        "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\","
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}],"
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

    // SELECT VALUE COUNT(1) yields a bare number per partition page.
    private static string CountEnvelope(params long[] partials)
    {
        var sb = new StringBuilder("{\"_rid\":\"x\",\"Documents\":[");
        for (int i = 0; i < partials.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(partials[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append("],\"_count\":").Append(partials.Length).Append('}');
        return sb.ToString();
    }

    [Fact]
    public async Task Scan_returns_all_items_with_cross_partition_header()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"}}"),
                    DocWithItem("b", "b", "{\"pk\":{\"S\":\"b\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\"}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        var qr = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, qr.Method);
        Assert.Equal("true", qr.Headers["x-ms-documentdb-isquery"]);
        Assert.Equal("true", qr.Headers["x-ms-documentdb-query-enablecrosspartition"]);
        Assert.False(qr.Headers.ContainsKey("x-ms-documentdb-partitionkey"));
        Assert.Contains("c._a2a = 'item'", qr.Body);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(2, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.Equal(2, resp.RootElement.GetProperty("ScannedCount").GetInt32());
    }

    [Fact]
    public async Task Scan_filter_expression_diverges_count_and_scanned()
    {
        // Uses a non-pushable predicate (size() is always residual)
        // so the in-process evaluator runs against the docs the stub
        // Cosmos returns.
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"x\"}}"),
                    DocWithItem("b", "b", "{\"pk\":{\"S\":\"b\"},\"v\":{\"S\":\"longer\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"FilterExpression\":\"size(v) > :min\","
                  + "\"ExpressionAttributeValues\":{\":min\":{\"N\":\"3\"}}}";

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.Equal(2, resp.RootElement.GetProperty("ScannedCount").GetInt32());
    }

    [Fact]
    public async Task Scan_filter_expression_pushdown_appends_clause_to_cosmos_sql()
    {
        // A pushable predicate (v = :v over a string) must arrive at
        // Cosmos as part of the WHERE clause, not as in-process
        // filtering. ScannedCount/Count then converge to whatever
        // Cosmos returned (the stub returns all docs verbatim).
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"FilterExpression\":\"v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"S\":\"x\"}}}";

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        var queryReq = handler.Requests[1];
        // Body is JSON-encoded so `"` is escaped as `\"`.
        Assert.Contains("c[\\\"v\\\"] = @fp0", queryReq.Body);
    }

    [Fact]
    public async Task Scan_pushed_filter_recovers_scanned_count_via_aggregate()
    {
        // With the filter enforced server-side, Cosmos streams back only the
        // 2 matching docs. A faithful DynamoDB ScannedCount (pre-filter) must
        // still report every examined item — recovered via a SELECT VALUE
        // COUNT(1) over the same scope minus the pushed filter.
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"x\"}}"),
                    DocWithItem("b", "b", "{\"pk\":{\"S\":\"b\"},\"v\":{\"S\":\"x\"}}"))),
                CosmosOk(CountEnvelope(5)),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"FilterExpression\":\"v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"S\":\"x\"}}}";

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(2, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.Equal(5, resp.RootElement.GetProperty("ScannedCount").GetInt32());

        Assert.Equal(3, handler.Requests.Count);
        var countReq = handler.Requests[2];
        Assert.Contains("SELECT VALUE COUNT(1)", countReq.Body);
        // The aggregate must NOT carry the pushed filter, and must fan out
        // cross-partition (no partition-key scoping for a table-wide Scan).
        Assert.DoesNotContain("@fp0", countReq.Body);
        Assert.Equal("true", countReq.Headers["x-ms-documentdb-query-enablecrosspartition"]);
        Assert.False(countReq.Headers.ContainsKey("x-ms-documentdb-partitionkey"));
    }

    [Fact]
    public async Task Scan_pushed_filter_with_limit_skips_aggregate()
    {
        // A Limit makes the pre-filter ScannedCount page-bounded; under
        // server-side filtering the page boundary differs from DynamoDB's,
        // so we do NOT issue the aggregate (documented divergence) — no
        // third request, ScannedCount stays the streamed value.
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"x\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Limit\":10,"
                  + "\"FilterExpression\":\"v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"S\":\"x\"}}}";

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("ScannedCount").GetInt32());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Scan_pushed_filter_falls_back_when_aggregate_fails()
    {
        // Best-effort: if the count aggregate cannot be read, fall back to
        // the streamed counter rather than failing the request. (The stub
        // returns 500 for the unscripted third request.)
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"S\":\"x\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"FilterExpression\":\"v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"S\":\"x\"}}}";

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(1, resp.RootElement.GetProperty("ScannedCount").GetInt32());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Scan_projection_expression_drops_other_attributes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"},\"extra\":{\"S\":\"x\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\","
                  + "\"ProjectionExpression\":\"pk, #v\","
                  + "\"ExpressionAttributeNames\":{\"#v\":\"v\"}}";

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        var item = resp.RootElement.GetProperty("Items")[0];
        Assert.True(item.TryGetProperty("pk", out _));
        Assert.True(item.TryGetProperty("v", out _));
        Assert.False(item.TryGetProperty("extra", out _));
    }

    [Fact]
    public async Task Scan_select_count_omits_items_array()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Select\":\"COUNT\"}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(1, resp.RootElement.GetProperty("Count").GetInt32());
        Assert.False(resp.RootElement.TryGetProperty("Items", out _));
    }

    [Fact]
    public async Task Scan_consistent_read_sets_strong_consistency_header()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"ConsistentRead\":true}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal("Strong", handler.Requests[1].Headers["x-ms-consistency-level"]);
    }

    [Fact]
    public async Task Scan_limit_caps_scanned_count_and_returns_last_evaluated_key()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(
                    DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"}}"),
                    DocWithItem("b", "b", "{\"pk\":{\"S\":\"b\"}}")),
                    continuation: "PAGE2"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Limit\":2}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("2", handler.Requests[1].Headers["x-ms-max-item-count"]);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(2, resp.RootElement.GetProperty("ScannedCount").GetInt32());
        var sentinel = resp.RootElement.GetProperty("LastEvaluatedKey")
            .GetProperty("__a2a_continuation").GetProperty("S").GetString()!;
        Assert.Equal("PAGE2", Encoding.UTF8.GetString(Convert.FromBase64String(sentinel)));
    }

    [Fact]
    public async Task Scan_exclusive_start_key_round_trips_continuation()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("RESUME-Z"));
        var req = "{\"TableName\":\"orders\","
                  + "\"ExclusiveStartKey\":{\"__a2a_continuation\":{\"S\":\"" + b64 + "\"}}}";

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal("RESUME-Z", handler.Requests[1].Headers["x-ms-continuation"]);
    }

    [Fact]
    public async Task Scan_index_name_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        var req = "{\"TableName\":\"orders\",\"IndexName\":\"gsi1\"}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("secondary indexes", ReadResponse(body));
    }

    [Fact]
    public async Task Scan_parallel_scan_parameters_are_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        var req = "{\"TableName\":\"orders\",\"Segment\":0,\"TotalSegments\":4}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Parallel scan", ReadResponse(body));
    }

    [Fact]
    public async Task Scan_legacy_scan_filter_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        var req = "{\"TableName\":\"orders\","
                  + "\"ScanFilter\":{\"pk\":{\"AttributeValueList\":[{\"S\":\"a\"}],\"ComparisonOperator\":\"EQ\"}}}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Legacy", ReadResponse(body));
    }

    [Fact]
    public async Task Scan_attributes_to_get_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        var req = "{\"TableName\":\"orders\",\"AttributesToGet\":[\"pk\"]}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Scan_missing_table_returns_resource_not_found()
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

        var req = "{\"TableName\":\"missing\"}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task Scan_invalid_table_name_is_rejected_before_cosmos()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"a/b\"}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Scan_malformed_json_returns_serialization_exception()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes("{"), cosmos, logger: null, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("SerializationException", ReadResponse(body));
    }

    [Fact]
    public async Task Scan_continues_across_pages_until_no_continuation()
    {
        // No Limit: should walk both pages.
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"}}")),
                    continuation: "P2"),
                CosmosOk(QueryEnvelope(DocWithItem("b", "b", "{\"pk\":{\"S\":\"b\"}}"))),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\"}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger: null, default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("P2", handler.Requests[2].Headers["x-ms-continuation"]);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal(2, resp.RootElement.GetProperty("ScannedCount").GetInt32());
        Assert.False(resp.RootElement.TryGetProperty("LastEvaluatedKey", out _));
    }

    [Fact]
    public async Task Scan_emits_cost_warning_for_each_request()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(Metadata),
                CosmosOk(QueryEnvelope()),
            },
        };
        var cosmos = BuildClient(handler);
        var logger = new RecordingLogger();

        var req = "{\"TableName\":\"orders\"}";
        await ScanHandler.HandleScanAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, logger, default);

        var warn = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warn.Level);
        Assert.Contains("orders", warn.Message);
        Assert.Contains("Scan", warn.Message);
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

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
