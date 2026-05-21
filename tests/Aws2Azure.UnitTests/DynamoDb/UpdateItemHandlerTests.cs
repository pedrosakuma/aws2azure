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
/// End-to-end coverage for <see cref="UpdateItemHandler"/> against a
/// scripted Cosmos REST surface. Pins the GET → mutate → PUT(If-Match)
/// contract, upsert-on-missing semantics, ReturnValues plumbing, the
/// retry loop under 412, and the rejection of conditional-write
/// fields that are deferred to a later slice.
/// </summary>
public class UpdateItemHandlerTests
{
    private const string Table = "orders";

    private static readonly string MetadataDocHashOnly =
        "{\"id\":\"__aws2azure_table_meta__\",\"pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\",\"creationDateTime\":0,"
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

    private static HttpResponseMessage CosmosOk(string body, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (etag is not null) r.Headers.TryAddWithoutValidation("etag", etag);
        return r;
    }

    private static string DocWithItem(string pk, string id, string itemJson)
        => $"{{\"id\":\"{id}\",\"pk\":\"{pk}\",\"_a2a\":\"item\",\"item\":{itemJson}}}";

    [Fact]
    public async Task UpdateItem_set_against_existing_doc_does_etag_put()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                CosmosOk(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"}}"), etag: "\"etag-1\""),
                CosmosOk("{}"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"42\"}}}";

        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        var put = handler.Requests[2];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("\"etag-1\"", put.Headers["If-Match"]);

        using var doc = JsonDocument.Parse(put.Body!);
        Assert.Equal("42",
            doc.RootElement.GetProperty("item").GetProperty("v").GetProperty("N").GetString());
    }

    [Fact]
    public async Task UpdateItem_on_missing_item_atomic_creates_with_if_none_match()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") },
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"k\"}},"
                  + "\"UpdateExpression\":\"SET name = :n\","
                  + "\"ExpressionAttributeValues\":{\":n\":{\"S\":\"new\"}}}";

        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        var post = handler.Requests[2];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("*", post.Headers["If-None-Match"]);
        // We must NOT fall back to upsert: that's what the original race
        // bug used. Confirm the upsert header is absent.
        Assert.False(post.Headers.ContainsKey("x-ms-documentdb-is-upsert"));
    }

    [Fact]
    public async Task UpdateItem_on_create_conflict_replays_against_winner()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                // Initial GET → not found, attempt atomic create...
                new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
                // ...conflict because another writer beat us to it.
                new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") },
                // Loop body re-reads → the winner's doc is now present.
                CosmosOk(DocWithItem("k", "k", "{\"pk\":{\"S\":\"k\"},\"counter\":{\"N\":\"1\"}}"), etag: "\"w1\""),
                CosmosOk("{}"),
            },
        };
        var cosmos = BuildClient(handler);

        // Two concurrent ADDs must converge to +2 — replay path proves
        // we re-applied the update against the winner's state.
        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"k\"}},"
                  + "\"UpdateExpression\":\"ADD counter :one\","
                  + "\"ExpressionAttributeValues\":{\":one\":{\"N\":\"1\"}}}";

        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(5, handler.Requests.Count);
        using var doc = JsonDocument.Parse(handler.Requests[4].Body!);
        Assert.Equal("2",
            doc.RootElement.GetProperty("item").GetProperty("counter").GetProperty("N").GetString());
    }

    [Fact]
    public async Task UpdateItem_retries_on_precondition_failed_then_succeeds()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                CosmosOk(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"}}"), etag: "\"e1\""),
                new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { Content = new StringContent("{}") },
                CosmosOk(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"2\"}}"), etag: "\"e2\""),
                CosmosOk("{}"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"99\"}}}";

        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        // Two GETs + two PUTs after the metadata read = 5 requests.
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("\"e2\"", handler.Requests[4].Headers["If-Match"]);
    }

    [Fact]
    public async Task UpdateItem_returns_all_new_attributes_when_requested()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                CosmosOk(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"}}"), etag: "\"e\""),
                CosmosOk("{}"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"5\"}},"
                  + "\"ReturnValues\":\"ALL_NEW\"}";

        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var attrs = doc.RootElement.GetProperty("Attributes");
        Assert.Equal("5", attrs.GetProperty("v").GetProperty("N").GetString());
        Assert.Equal("a", attrs.GetProperty("pk").GetProperty("S").GetString());
    }

    [Fact]
    public async Task UpdateItem_returns_updated_new_only_for_touched_attributes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                CosmosOk(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"},\"other\":{\"S\":\"x\"}}"), etag: "\"e\""),
                CosmosOk("{}"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"7\"}},"
                  + "\"ReturnValues\":\"UPDATED_NEW\"}";

        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        using var doc = JsonDocument.Parse(ReadResponse(body));
        var attrs = doc.RootElement.GetProperty("Attributes");
        Assert.True(attrs.TryGetProperty("v", out _));
        Assert.False(attrs.TryGetProperty("other", out _));
    }

    [Fact]
    public async Task UpdateItem_returns_all_old_attributes()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                CosmosOk(DocWithItem("a", "a", "{\"pk\":{\"S\":\"a\"},\"v\":{\"N\":\"1\"}}"), etag: "\"e\""),
                CosmosOk("{}"),
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"9\"}},"
                  + "\"ReturnValues\":\"ALL_OLD\"}";

        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        Assert.Equal("1", doc.RootElement.GetProperty("Attributes").GetProperty("v").GetProperty("N").GetString());
    }

    [Fact]
    public async Task UpdateItem_rejects_condition_expression()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ConditionExpression\":\"v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"1\"}}}";
        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Conditional", ReadResponse(body));
    }

    [Fact]
    public async Task UpdateItem_rejects_both_update_expression_and_attribute_updates()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"AttributeUpdates\":{\"v\":{\"Action\":\"PUT\",\"Value\":{\"N\":\"1\"}}},"
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"1\"}}}";
        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Exactly one", ReadResponse(body));
    }

    [Fact]
    public async Task UpdateItem_legacy_attribute_updates_path()
    {
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") },
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"AttributeUpdates\":{\"name\":{\"Action\":\"PUT\",\"Value\":{\"S\":\"hello\"}}}}";
        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(handler.Requests[2].Body!);
        Assert.Equal("hello",
            doc.RootElement.GetProperty("item").GetProperty("name").GetProperty("S").GetString());
    }

    [Fact]
    public async Task UpdateItem_table_not_found_surfaces_resource_not_found()
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

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"1\"}}}";
        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task UpdateItem_throttle_surfaces_provisioned_throughput()
    {
        var (ctx, body) = NewCtx();
        var throttle = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetadataDocHashOnly), throttle },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},"
                  + "\"UpdateExpression\":\"SET v = :v\","
                  + "\"ExpressionAttributeValues\":{\":v\":{\"N\":\"1\"}}}";
        await UpdateItemHandler.HandleUpdateItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ProvisionedThroughputExceededException", ReadResponse(body));
    }

    // ----- minimal scripted handler ---------------------------------

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
