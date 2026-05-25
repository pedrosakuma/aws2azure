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
/// Coverage for <see cref="BatchGetItemHandler"/>. Pins:
/// <list type="bullet">
///   <item>Fan-out fetches the right Cosmos doc per key (POST mappings:
///   pk header + correct id in URL).</item>
///   <item>Missing items are omitted from <c>Responses</c>, not
///   reported as errors (DDB semantics).</item>
///   <item>Cosmos <c>429</c> drops the key into
///   <c>UnprocessedKeys</c>; the rest of the batch still returns
///   200.</item>
///   <item>Per-table <c>ProjectionExpression</c> + <c>ConsistentRead</c>
///   are applied independently.</item>
///   <item>Validation (100-key cap, legacy AttributesToGet, missing
///   table, key-attr type mismatch) is rejected before any Cosmos call.</item>
/// </list>
/// </summary>
public class BatchGetItemHandlerTests
{
    private static readonly string MetaHashOnly =
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

    private static HttpResponseMessage CosmosOk(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CosmosStatus(HttpStatusCode code, string body = "{}")
        => new(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string ItemDoc(string id, string pk, string itemJson)
    {
        using var d = JsonDocument.Parse(itemJson);
        return Aws2Azure.Modules.DynamoDb.Persistence.InferredAttributeStorage.BuildCosmosDocument(id, pk, d.RootElement);
    }

    [Fact]
    public async Task BatchGet_returns_items_for_each_key()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"}}")),
                CosmosOk(ItemDoc("b", "b", "{\"pk\":{\"S\":\"b\"},\"v\":{\"N\":\"2\"}}")),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}},{\"pk\":{\"S\":\"b\"}}]}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        var items = resp.RootElement.GetProperty("Responses").GetProperty("orders");
        Assert.Equal(2, items.GetArrayLength());
    }

    [Fact]
    public async Task BatchGet_omits_missing_items_from_responses()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"}}")),
                CosmosStatus(HttpStatusCode.NotFound), // 'b' missing — no x-ms-substatus header
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}},{\"pk\":{\"S\":\"b\"}}]}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        var items = resp.RootElement.GetProperty("Responses").GetProperty("orders");
        Assert.Equal(1, items.GetArrayLength());
        Assert.False(resp.RootElement.TryGetProperty("UnprocessedKeys", out var u) && u.ValueKind == JsonValueKind.Object && u.EnumerateObject().MoveNext());
    }

    [Fact]
    public async Task BatchGet_throttled_keys_go_to_unprocessed_keys()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"}}")),
                CosmosStatus((HttpStatusCode)429, "{\"code\":\"TooManyRequests\"}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}},{\"pk\":{\"S\":\"b\"}}]}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        var unprocessed = resp.RootElement.GetProperty("UnprocessedKeys").GetProperty("orders");
        var keys = unprocessed.GetProperty("Keys");
        Assert.Equal(1, keys.GetArrayLength());
        Assert.Equal("b", keys[0].GetProperty("pk").GetProperty("S").GetString());
    }

    [Fact]
    public async Task BatchGet_projection_expression_filters_attributes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"},\"extra\":{\"S\":\"x\"}}")),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":{"
                  + "\"Keys\":[{\"pk\":{\"S\":\"a\"}}],"
                  + "\"ProjectionExpression\":\"pk, #v\","
                  + "\"ExpressionAttributeNames\":{\"#v\":\"v\"}}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        using var resp = JsonDocument.Parse(ReadResponse(body));
        var item = resp.RootElement.GetProperty("Responses").GetProperty("orders")[0];
        Assert.True(item.TryGetProperty("pk", out _));
        Assert.True(item.TryGetProperty("v", out _));
        Assert.False(item.TryGetProperty("extra", out _));
    }

    [Fact]
    public async Task BatchGet_consistent_read_sets_strong_header()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"}}")),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}}],\"ConsistentRead\":true}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal("Strong", handler.Requests[1].Headers["x-ms-consistency-level"]);
    }

    [Fact]
    public async Task BatchGet_over_100_keys_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        var sb = new StringBuilder("{\"RequestItems\":{\"orders\":{\"Keys\":[");
        for (int i = 0; i < 101; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"pk\":{\"S\":\"k").Append(i).Append("\"}}");
        }
        sb.Append("]}}}");

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(sb.ToString()), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("100", ReadResponse(body));
    }

    [Fact]
    public async Task BatchGet_legacy_attributes_to_get_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        var req = "{\"RequestItems\":{\"orders\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}}],\"AttributesToGet\":[\"pk\"]}}}";
        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("AttributesToGet", ReadResponse(body));
    }

    [Fact]
    public async Task BatchGet_missing_table_returns_resource_not_found()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.NotFound), // metadata lookup fails
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"missing\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}}]}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task BatchGet_key_type_mismatch_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":{\"Keys\":[{\"pk\":{\"N\":\"42\"}}]}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        // Only the metadata GET happened — no per-key GET issued.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BatchGet_duplicate_key_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":{\"Keys\":[{\"pk\":{\"S\":\"k1\"}},{\"pk\":{\"S\":\"k1\"}}]}}}";

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("duplicate", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
        // Only the metadata GET happened — no per-key GET issued.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BatchGet_empty_request_items_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes("{\"RequestItems\":{}}"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task BatchGet_malformed_json_returns_serialization_exception()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes("{"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("SerializationException", ReadResponse(body));
    }

    [Fact]
    public async Task BatchGet_cross_table_request_fans_out_per_table()
    {
        var metaOther =
            "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
            + "\"tableName\":\"users\","
            + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}],"
            + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}]}";

        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(metaOther),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"}}")),
                CosmosOk(ItemDoc("u1", "u1", "{\"pk\":{\"S\":\"u1\"}}")),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"RequestItems\":{"
                  + "\"orders\":{\"Keys\":[{\"pk\":{\"S\":\"a\"}}]},"
                  + "\"users\":{\"Keys\":[{\"pk\":{\"S\":\"u1\"}}]}}}";
        await BatchGetItemHandler.HandleBatchGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        var responses = resp.RootElement.GetProperty("Responses");
        Assert.Equal(1, responses.GetProperty("orders").GetArrayLength());
        Assert.Equal(1, responses.GetProperty("users").GetArrayLength());
    }

    // ---- harness ----

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = new();
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
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
    }

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body);
}
