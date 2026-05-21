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
/// Exercises PutItem / GetItem / DeleteItem against a scripted Cosmos
/// REST surface. The handlers read sidecar metadata first then route
/// item docs via formatted pk/id; tests cover happy paths, validation,
/// idempotent delete, missing-table propagation, and the
/// preserve-original-wire-form contract that lets reads round-trip
/// without type erosion.
/// </summary>
public class ItemHandlersTests
{
    private const string TableName = "orders";

    private static readonly string MetadataDoc =
        "{\"id\":\"__aws2azure_table_meta__\",\"pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\",\"creationDateTime\":0,"
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"},{\"name\":\"sk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"},{\"name\":\"sk\",\"keyType\":\"RANGE\"}],"
        + "\"billingMode\":\"PAY_PER_REQUEST\"}";

    private static readonly string MetadataDocHashOnly =
        "{\"id\":\"__aws2azure_table_meta__\",\"pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
        + "\"tableName\":\"orders\",\"creationDateTime\":0,"
        + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}],"
        + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}],"
        + "\"billingMode\":\"PAY_PER_REQUEST\"}";

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
        using var r = new StreamReader(body);
        return r.ReadToEnd();
    }

    private static HttpResponseMessage CosmosOk(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task PutItem_writes_doc_with_routing_fields_and_envelope()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDoc),
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") },
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{"
                  + "\"pk\":{\"S\":\"customer-1\"},"
                  + "\"sk\":{\"S\":\"order-42\"},"
                  + "\"total\":{\"N\":\"99.95\"},"
                  + "\"tags\":{\"SS\":[\"vip\",\"new\"]}}}";

        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("{}", ReadResponse(body));

        // Two Cosmos calls: metadata read + doc upsert.
        Assert.Equal(2, handler.Requests.Count);
        var upsert = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, upsert.Method);
        Assert.Equal("[\"customer-1\"]", upsert.Headers["x-ms-documentdb-partitionkey"]);
        Assert.Equal("true", upsert.Headers["x-ms-documentdb-is-upsert"]);

        // The doc carries id, pk, _a2a, item; the item envelope preserves
        // the exact wire form of every attribute (including numeric precision).
        using var doc = JsonDocument.Parse(upsert.Body!);
        Assert.Equal("order-42", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("customer-1", doc.RootElement.GetProperty("pk").GetString());
        Assert.Equal("item", doc.RootElement.GetProperty("_a2a").GetString());
        var item = doc.RootElement.GetProperty("item");
        Assert.Equal("99.95", item.GetProperty("total").GetProperty("N").GetString());
        Assert.Equal("vip", item.GetProperty("tags").GetProperty("SS")[0].GetString());
    }

    [Fact]
    public async Task PutItem_hash_only_uses_hash_value_as_id_and_pk()
    {
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDocHashOnly),
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") },
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"only-key\"},\"v\":{\"N\":\"1\"}}}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("only-key", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("only-key", doc.RootElement.GetProperty("pk").GetString());
    }

    [Fact]
    public async Task PutItem_rejects_missing_required_key_attribute()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetadataDoc) } };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"x\"},\"data\":{\"S\":\"y\"}}}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("sk", resp);
    }

    [Fact]
    public async Task PutItem_rejects_key_attribute_with_wrong_type_tag()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetadataDoc) } };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{"
                  + "\"pk\":{\"N\":\"123\"},"
                  + "\"sk\":{\"S\":\"x\"}}}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        var resp = ReadResponse(body);
        Assert.Contains("ValidationException", resp);
        Assert.Contains("type", resp);
    }

    [Fact]
    public async Task PutItem_rejects_malformed_attribute_value()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetadataDoc) } };
        var cosmos = BuildClient(handler);

        // 'data' attribute uses two type tags — illegal.
        var req = "{\"TableName\":\"orders\",\"Item\":{"
                  + "\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"},"
                  + "\"data\":{\"S\":\"v\",\"N\":\"1\"}}}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("data", ReadResponse(body));
    }

    [Fact]
    public async Task PutItem_rejects_condition_expression()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}},"
                  + "\"ConditionExpression\":\"attribute_not_exists(pk)\"}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Conditional writes", ReadResponse(body));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PutItem_rejects_return_values_other_than_none()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}},"
                  + "\"ReturnValues\":\"ALL_OLD\"}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ReturnValues", ReadResponse(body));
    }

    [Fact]
    public async Task PutItem_returns_resource_not_found_when_table_missing()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") } },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"missing\",\"Item\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}}}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task GetItem_returns_item_envelope_verbatim()
    {
        var (ctx, body) = NewCtx();
        var docJson = "{\"id\":\"order-42\",\"pk\":\"customer-1\",\"_a2a\":\"item\","
                      + "\"item\":{\"pk\":{\"S\":\"customer-1\"},\"sk\":{\"S\":\"order-42\"},"
                      + "\"total\":{\"N\":\"99.95\"},\"flag\":{\"BOOL\":true}}}";
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetadataDoc), CosmosOk(docJson) },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"customer-1\"},\"sk\":{\"S\":\"order-42\"}}}";
        await ItemHandlers.HandleGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var item = doc.RootElement.GetProperty("Item");
        Assert.Equal("99.95", item.GetProperty("total").GetProperty("N").GetString());
        Assert.True(item.GetProperty("flag").GetProperty("BOOL").GetBoolean());

        var getReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, getReq.Method);
        Assert.EndsWith("/docs/order-42", getReq.Uri.AbsolutePath);
        Assert.Equal("[\"customer-1\"]", getReq.Headers["x-ms-documentdb-partitionkey"]);
        Assert.False(getReq.Headers.ContainsKey("x-ms-consistency-level"));
    }

    [Fact]
    public async Task GetItem_consistent_read_adds_strong_consistency_header()
    {
        var (ctx, _) = NewCtx();
        var docJson = "{\"id\":\"a\",\"pk\":\"a\",\"_a2a\":\"item\",\"item\":{\"pk\":{\"S\":\"a\"}}}";
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetadataDocHashOnly), CosmosOk(docJson) } };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"a\"}},\"ConsistentRead\":true}";
        await ItemHandlers.HandleGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal("Strong", handler.Requests[1].Headers["x-ms-consistency-level"]);
    }

    [Fact]
    public async Task GetItem_returns_empty_when_doc_missing()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetadataDoc), new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") } },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}}}";
        await ItemHandlers.HandleGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        Assert.False(doc.RootElement.TryGetProperty("Item", out _));
    }

    [Fact]
    public async Task GetItem_rejects_projection_expression()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}},"
                  + "\"ProjectionExpression\":\"#a,#b\"}";
        await ItemHandlers.HandleGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Projection", ReadResponse(body));
    }

    [Fact]
    public async Task GetItem_rejects_extra_key_attribute()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetadataDoc) } };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"},\"extra\":{\"S\":\"z\"}}}";
        await ItemHandlers.HandleGetItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("extra", ReadResponse(body));
    }

    [Fact]
    public async Task DeleteItem_returns_success_when_doc_missing()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetadataDoc), new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") } },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}}}";
        await ItemHandlers.HandleDeleteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("{}", ReadResponse(body));
    }

    [Fact]
    public async Task DeleteItem_sends_delete_with_partition_key()
    {
        var (ctx, _) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses = { CosmosOk(MetadataDoc), new HttpResponseMessage(HttpStatusCode.NoContent) },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"customer-1\"},\"sk\":{\"S\":\"order-42\"}}}";
        await ItemHandlers.HandleDeleteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(200, ctx.Response.StatusCode);
        var delReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Delete, delReq.Method);
        Assert.EndsWith("/docs/order-42", delReq.Uri.AbsolutePath);
        Assert.Equal("[\"customer-1\"]", delReq.Headers["x-ms-documentdb-partitionkey"]);
    }

    [Fact]
    public async Task DeleteItem_rejects_condition_expression()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler();
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Key\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}},"
                  + "\"ConditionExpression\":\"attribute_exists(pk)\"}";
        await ItemHandlers.HandleDeleteItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("Conditional", ReadResponse(body));
    }

    [Fact]
    public async Task PutItem_rejects_metadata_collision_in_hash_only_table()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler { Responses = { CosmosOk(MetadataDocHashOnly) } };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"__aws2azure_table_meta__\"}}}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("reserved", ReadResponse(body));
    }

    [Fact]
    public async Task PutItem_cosmos_429_maps_to_provisioned_throughput_exceeded()
    {
        var (ctx, body) = NewCtx();
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosOk(MetadataDoc),
                new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("RU throttled") },
            },
        };
        var cosmos = BuildClient(handler);

        var req = "{\"TableName\":\"orders\",\"Item\":{\"pk\":{\"S\":\"x\"},\"sk\":{\"S\":\"y\"}}}";
        await ItemHandlers.HandlePutItemAsync(ctx, Encoding.UTF8.GetBytes(req), cosmos, CancellationToken.None);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ProvisionedThroughputExceededException", ReadResponse(body));
    }

    [Fact]
    public void BuildItemDocument_preserves_numeric_precision()
    {
        using var src = JsonDocument.Parse("{\"v\":{\"N\":\"3.141592653589793238462643383279\"}}");
        var doc = ItemHandlers.BuildItemDocument("k", "k", src.RootElement);
        Assert.Contains("3.141592653589793238462643383279", doc);
    }

    [Fact]
    public void ExtractItemFromCosmosDoc_returns_null_when_envelope_missing()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"x\",\"pk\":\"x\"}"));
        Assert.Null(ItemHandlers.ExtractItemFromCosmosDoc(ms));
    }

    [Fact]
    public void ParsedAttributeValue_rejects_empty_object_and_multi_property()
    {
        using var empty = JsonDocument.Parse("{}");
        Assert.False(ParsedAttributeValue.TryParse(empty.RootElement, out _));

        using var multi = JsonDocument.Parse("{\"S\":\"a\",\"N\":\"1\"}");
        Assert.False(ParsedAttributeValue.TryParse(multi.RootElement, out _));

        using var unknown = JsonDocument.Parse("{\"X\":\"a\"}");
        Assert.False(ParsedAttributeValue.TryParse(unknown.RootElement, out _));

        using var ok = JsonDocument.Parse("{\"S\":\"hello\"}");
        Assert.True(ParsedAttributeValue.TryParse(ok.RootElement, out var p));
        Assert.Equal("S", p.TypeTag);
        Assert.Equal("hello", p.Value.GetString());
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = new();
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
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
