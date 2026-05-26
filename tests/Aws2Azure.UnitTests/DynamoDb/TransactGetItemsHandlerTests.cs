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
/// Coverage for <see cref="TransactGetItemsHandler"/>:
/// <list type="bullet">
///   <item>Fan-out reads with strong consistency and positional Responses alignment.</item>
///   <item>Missing items emit empty <c>{}</c> entries (not errors).</item>
///   <item>Any Cosmos non-2xx,non-404 cancels the transaction with
///   <c>TransactionCanceledException</c> + per-item <c>CancellationReasons</c>.</item>
///   <item>Validation (100-item cap, missing TableName/Key, key-attr type, missing table).</item>
///   <item>ProjectionExpression respected.</item>
/// </list>
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class TransactGetItemsHandlerTests
{
    public TransactGetItemsHandlerTests()
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
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage CosmosStatus(HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ItemDoc(string id, string pk, string itemJson)
    {
        using var d = JsonDocument.Parse(itemJson);
        return Aws2Azure.Modules.DynamoDb.Persistence.InferredAttributeStorage.BuildCosmosDocument(id, pk, d.RootElement);
    }

    [Fact]
    public async Task TransactGet_returns_aligned_responses()
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
        var req = "{\"TransactItems\":[" +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}}}}," +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"b\"}}}}" +
            "]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var responses = doc.RootElement.GetProperty("Responses").EnumerateArray();
        var arr = new List<JsonElement>();
        foreach (var e in responses) arr.Add(e);
        Assert.Equal(2, arr.Count);
        Assert.Equal("1", arr[0].GetProperty("Item").GetProperty("v").GetProperty("N").GetString());
        Assert.Equal("2", arr[1].GetProperty("Item").GetProperty("v").GetProperty("N").GetString());
    }

    [Fact]
    public async Task TransactGet_uses_strong_consistency_on_every_read()
    {
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"}}")),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}}}}]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        // Last request is the per-key GET.
        var gets = handler.Requests.FindAll(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.Contains("/docs/a"));
        Assert.Single(gets);
        Assert.Equal("Strong", gets[0].Headers["x-ms-consistency-level"]);
    }

    [Fact]
    public async Task TransactGet_missing_item_emits_empty_entry()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosStatus(HttpStatusCode.NotFound, "{\"code\":\"NotFound\"}"),
                CosmosOk(ItemDoc("b", "b", "{\"pk\":{\"S\":\"b\"}}")),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}}}}," +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"b\"}}}}" +
            "]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var responses = doc.RootElement.GetProperty("Responses");
        Assert.Equal(2, responses.GetArrayLength());
        Assert.False(responses[0].TryGetProperty("Item", out _));
        Assert.True(responses[1].TryGetProperty("Item", out _));
    }

    [Fact]
    public async Task TransactGet_hard_error_cancels_with_reasons()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosStatus(HttpStatusCode.InternalServerError, "{\"code\":\"Boom\"}"),
                CosmosOk(ItemDoc("b", "b", "{\"pk\":{\"S\":\"b\"}}")),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}}}}," +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"b\"}}}}" +
            "]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        Assert.Contains("TransactionCanceledException", doc.RootElement.GetProperty("__type").GetString());
        var reasons = doc.RootElement.GetProperty("CancellationReasons");
        Assert.Equal(2, reasons.GetArrayLength());
        Assert.Equal("InternalServerError", reasons[0].GetProperty("Code").GetString());
        Assert.Equal("None", reasons[1].GetProperty("Code").GetString());
    }

    [Fact]
    public async Task TransactGet_over_100_items_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var sb = new StringBuilder("{\"TransactItems\":[");
        for (int i = 0; i < 101; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"k").Append(i).Append("\"}}}}");
        }
        sb.Append("]}");

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(sb.ToString()), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("100", ReadResponse(body));
    }

    [Fact]
    public async Task TransactGet_missing_TableName_is_rejected()
    {
        var (ctx, _) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var req = "{\"TransactItems\":[{\"Get\":{\"Key\":{\"pk\":{\"S\":\"a\"}}}}]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task TransactGet_missing_Key_is_rejected()
    {
        var (ctx, _) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());
        var req = "{\"TransactItems\":[{\"Get\":{\"TableName\":\"orders\"}}]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task TransactGet_table_not_found_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.NotFound,
                    "{\"code\":\"NotFound\",\"message\":\"x-ms-substatus: 1003\"}"),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}}}}]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task TransactGet_projection_filters_attributes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetaHashOnly),
                CosmosOk(ItemDoc("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"},\"x\":{\"S\":\"hidden\"}}")),
            },
        };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},\"ProjectionExpression\":\"pk,v\"}}" +
            "]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        var text = ReadResponse(body);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("\"v\"", text);
    }

    [Fact]
    public async Task TransactGet_empty_items_is_rejected()
    {
        var (ctx, _) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes("{\"TransactItems\":[]}"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task TransactGet_malformed_json_is_rejected()
    {
        var (ctx, body) = NewCtx();
        var cosmos = BuildClient(new ScriptedHandler());

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes("{"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("SerializationException", ReadResponse(body));
    }

    [Fact]
    public async Task TransactGet_rejects_non_string_ExpressionAttributeName_value()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaHashOnly) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}}," +
            "\"ProjectionExpression\":\"#a\",\"ExpressionAttributeNames\":{\"#a\":1}}}" +
            "]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        var text = ReadResponse(body);
        Assert.Contains("ValidationException", text);
        Assert.Contains("ExpressionAttributeNames", text);
    }

    [Fact]
    public async Task TransactGet_rejects_non_object_ExpressionAttributeNames()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetaHashOnly) } };
        var cosmos = BuildClient(handler);
        var req = "{\"TransactItems\":[" +
            "{\"Get\":{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}}," +
            "\"ProjectionExpression\":\"#a\",\"ExpressionAttributeNames\":\"oops\"}}" +
            "]}";

        await TransactGetItemsHandler.HandleTransactGetItemsAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ValidationException", ReadResponse(body));
    }

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
