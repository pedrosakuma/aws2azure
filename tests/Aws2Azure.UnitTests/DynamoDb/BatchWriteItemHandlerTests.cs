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
/// Coverage for <see cref="BatchWriteItemHandler"/>. Pins:
/// <list type="bullet">
///   <item>Puts upsert via POST + <c>x-ms-documentdb-is-upsert: true</c>;
///   Deletes route via DELETE on the right id/pk.</item>
///   <item>25-item cap enforced before any Cosmos call.</item>
///   <item>Mixed Put + Delete on the same primary key is rejected.</item>
///   <item>Cosmos <c>429</c> on any individual write surfaces the
///   original entry in <c>UnprocessedItems</c>; the rest of the batch
///   still returns 200.</item>
///   <item>Missing table → ResourceNotFoundException (per-table
///   metadata read).</item>
///   <item>An entry with neither PutRequest nor DeleteRequest (or
///   both) is rejected.</item>
/// </list>
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class BatchWriteItemHandlerTests
{
    public BatchWriteItemHandlerTests()
    {
        // Clear metadata cache at test start to ensure isolation
        CosmosOpsShared.MetadataCache.Clear();
    }

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

    private static HttpResponseMessage CosmosCreated()
        => new(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CosmosNoContent()
        => new(HttpStatusCode.NoContent);

    private static HttpResponseMessage CosmosStatus(HttpStatusCode code, string body = "{}")
        => new(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task BatchWrite_puts_via_upsert_post()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosCreated(),
                CosmosCreated(),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"RequestItems\":{\"orders\":["
                  + "{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"}}}},"
                  + "{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"b\"},\"v\":{\"N\":\"2\"}}}}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        // 1 metadata + 2 upserts
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests.GetRange(1, 2), r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("true", r.Headers["x-ms-documentdb-is-upsert"]);
        });
        var resp = ReadResponse(body);
        using var doc = JsonDocument.Parse(resp);
        Assert.False(doc.RootElement.TryGetProperty("UnprocessedItems", out var u) && u.ValueKind == JsonValueKind.Object && u.EnumerateObject().MoveNext());
    }

    [Fact]
    public async Task BatchWrite_deletes_via_delete_method()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosNoContent(),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"RequestItems\":{\"orders\":["
                  + "{\"DeleteRequest\":{\"Key\":{\"pk\":{\"S\":\"a\"}}}}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.EndsWith("/docs/a", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task BatchWrite_throttled_items_go_to_unprocessed_items()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosCreated(),
                CosmosStatus((HttpStatusCode)429, "{\"code\":\"TooManyRequests\"}"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"RequestItems\":{\"orders\":["
                  + "{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"a\"}}}},"
                  + "{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"b\"}}}}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        var u = resp.RootElement.GetProperty("UnprocessedItems").GetProperty("orders");
        Assert.Equal(1, u.GetArrayLength());
        Assert.True(u[0].TryGetProperty("PutRequest", out _));
    }

    [Fact]
    public async Task BatchWrite_over_25_items_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        var sb = new StringBuilder("{\"RequestItems\":{\"orders\":[");
        for (int i = 0; i < 26; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"k").Append(i).Append("\"}}}}");
        }
        sb.Append("]}}");

        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(sb.ToString()), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("25", ReadResponse(body));
    }

    [Fact]
    public async Task BatchWrite_duplicate_key_in_single_batch_is_rejected()
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

        var req = "{\"RequestItems\":{\"orders\":["
                  + "{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"a\"}}}},"
                  + "{\"DeleteRequest\":{\"Key\":{\"pk\":{\"S\":\"a\"}}}}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("duplicate", ReadResponse(body));
        // Only metadata read happened — no per-item writes.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BatchWrite_entry_with_both_put_and_delete_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaHashOnly) } };
        var cosmos = BuildClient(handler);

        var req = "{\"RequestItems\":{\"orders\":["
                  + "{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"a\"}}},\"DeleteRequest\":{\"Key\":{\"pk\":{\"S\":\"a\"}}}}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("exactly one", ReadResponse(body));
    }

    [Fact]
    public async Task BatchWrite_entry_with_neither_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaHashOnly) } };
        var cosmos = BuildClient(handler);

        var req = "{\"RequestItems\":{\"orders\":[{}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task BatchWrite_missing_table_returns_resource_not_found()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.NotFound),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"missing\":[{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"a\"}}}}]}}";

        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task BatchWrite_put_missing_key_attribute_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaHashOnly) } };
        var cosmos = BuildClient(handler);

        var req = "{\"RequestItems\":{\"orders\":[{\"PutRequest\":{\"Item\":{\"v\":{\"N\":\"1\"}}}}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task BatchWrite_put_with_malformed_attribute_value_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaHashOnly) } };
        var cosmos = BuildClient(handler);

        // 'bad' attribute value is a bare string, not a typed {"S":...} envelope.
        var req = "{\"RequestItems\":{\"orders\":[{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"k1\"},\"bad\":\"not-an-attribute-value\"}}}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("bad", ReadResponse(body));
        // No Cosmos write — only the metadata GET happened.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BatchWrite_entry_with_put_and_null_delete_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaHashOnly) } };
        var cosmos = BuildClient(handler);

        // Both keys present — DeleteRequest:null still counts as "both specified".
        var req = "{\"RequestItems\":{\"orders\":[{\"PutRequest\":{\"Item\":{\"pk\":{\"S\":\"k1\"}}},\"DeleteRequest\":null}]}}";
        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("exactly one", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchWrite_empty_request_items_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes("{\"RequestItems\":{}}"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task BatchWrite_malformed_json_returns_serialization_exception()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes("{"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("SerializationException", ReadResponse(body));
    }

    [Fact]
    public async Task BatchWrite_delete_of_missing_item_is_success()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosStatus(HttpStatusCode.NotFound), // no x-ms-substatus → just missing item
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"RequestItems\":{\"orders\":[{\"DeleteRequest\":{\"Key\":{\"pk\":{\"S\":\"ghost\"}}}}]}}";

        await BatchWriteItemHandler.HandleBatchWriteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        Assert.False(doc.RootElement.TryGetProperty("UnprocessedItems", out var u) && u.ValueKind == JsonValueKind.Object && u.EnumerateObject().MoveNext());
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
