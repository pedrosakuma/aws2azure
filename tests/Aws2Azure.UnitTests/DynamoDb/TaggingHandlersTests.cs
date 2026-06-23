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

[Collection(DynamoDbTestCollection.Name)]
public class TaggingHandlersTests
{
    public TaggingHandlersTests()
    {
        CosmosOpsShared.MetadataCache.Clear();
    }

    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("arn:aws:dynamodb:us-east-1:123456789012:table/orders", "orders")]
    [InlineData("arn:aws:dynamodb:::table/orders", "orders")]
    public void TryParseTableName_accepts_table_arns_and_bare_names(string resource, string expected)
    {
        Assert.True(TaggingHandlers.TryParseTableName(resource, out var tableName, out var error));
        Assert.Equal(expected, tableName);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("arn:aws:s3:::bucket")]
    [InlineData("arn:aws:dynamodb:us-east-1:123456789012:table/")]
    [InlineData("arn:aws:dynamodb:us-east-1:123456789012:index/orders")]
    [InlineData("arn:aws:dynamodb:us-east-1:123456789012:table/orders/index/byStatus")]
    [InlineData("ab")]
    public void TryParseTableName_rejects_malformed_arns(string resource)
    {
        Assert.False(TaggingHandlers.TryParseTableName(resource, out var tableName, out var error));
        Assert.Empty(tableName);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task Tag_list_untag_round_trips_metadata_tags()
    {
        var handler = new MetadataHandler(tableExists: true, MetadataJson());
        var cosmos = BuildClient(handler);

        await InvokeTagAsync(cosmos,
            "{\"ResourceArn\":\"arn:aws:dynamodb:us-east-1:123456789012:table/orders\","
            + "\"Tags\":[{\"Key\":\"env\",\"Value\":\"prod\"},{\"Key\":\"owner\",\"Value\":\"team-a\"}]}");
        await InvokeTagAsync(cosmos,
            "{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"dev\"}]}");

        var listed = await InvokeListAsync(cosmos, "{\"ResourceArn\":\"orders\"}");
        AssertTag(listed, "env", "dev");
        AssertTag(listed, "owner", "team-a");

        await InvokeUntagAsync(cosmos, "{\"ResourceArn\":\"orders\",\"TagKeys\":[\"owner\"]}");

        listed = await InvokeListAsync(cosmos, "{\"ResourceArn\":\"orders\"}");
        AssertTag(listed, "env", "dev");
        Assert.DoesNotContain(listed, t => t.Key == "owner");
        Assert.Equal(3, handler.UpsertCount);
    }

    [Fact]
    public async Task TagResource_seeds_missing_metadata_when_container_exists()
    {
        var handler = new MetadataHandler(tableExists: true, metadataJson: null);
        var cosmos = BuildClient(handler);

        await InvokeTagAsync(cosmos,
            "{\"ResourceArn\":\"arn:aws:dynamodb:::table/orders\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"prod\"}]}");

        Assert.NotNull(handler.MetadataJson);
        using var doc = JsonDocument.Parse(handler.MetadataJson);
        Assert.Equal("orders", doc.RootElement.GetProperty("tableName").GetString());
        Assert.Equal("env", doc.RootElement.GetProperty("tags")[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task TagResource_conditional_replace_preserves_schema_fields()
    {
        var handler = new MetadataHandler(tableExists: true,
            "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
            + "\"tableName\":\"orders\",\"creationDateTime\":123,\"billingMode\":\"PAY_PER_REQUEST\","
            + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"},{\"name\":\"sk\",\"type\":\"N\"}],"
            + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"},{\"name\":\"sk\",\"keyType\":\"RANGE\"}],"
            + "\"tags\":[{\"key\":\"owner\",\"value\":\"team-a\"}]}");
        var cosmos = BuildClient(handler);

        await InvokeTagAsync(cosmos,
            "{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"prod\"}]}");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("\"1\"", handler.Requests[1].Headers["If-Match"]);

        using var doc = JsonDocument.Parse(handler.MetadataJson!);
        var root = doc.RootElement;
        Assert.Equal(123, root.GetProperty("creationDateTime").GetInt64());
        Assert.Equal("PAY_PER_REQUEST", root.GetProperty("billingMode").GetString());
        Assert.Equal(2, root.GetProperty("attributeDefinitions").GetArrayLength());
        Assert.Equal("sk", root.GetProperty("keySchema")[1].GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task TagResource_retries_conditional_replace_after_precondition_failure()
    {
        var handler = new MetadataHandler(tableExists: true, MetadataJson())
        {
            PreconditionFailuresBeforeSuccess = 1,
        };
        var cosmos = BuildClient(handler);

        await InvokeTagAsync(cosmos,
            "{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"prod\"}]}");

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[3].Method);
        Assert.Equal("\"1\"", handler.Requests[1].Headers["If-Match"]);
        Assert.Equal("\"1\"", handler.Requests[3].Headers["If-Match"]);

        using var doc = JsonDocument.Parse(handler.MetadataJson!);
        Assert.Equal("env", doc.RootElement.GetProperty("tags")[0].GetProperty("key").GetString());
    }


    [Fact]
    public async Task ListTagsOfResource_missing_table_returns_resource_not_found()
    {
        var handler = new MetadataHandler(tableExists: false, metadataJson: null);
        var cosmos = BuildClient(handler);
        var (ctx, body) = NewCtx();

        await TaggingHandlers.HandleListTagsOfResourceAsync(
            ctx, Encoding.UTF8.GetBytes("{\"ResourceArn\":\"arn:aws:dynamodb:::table/orders\"}"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Theory]
    [InlineData("{\"ResourceArn\":\"arn:aws:s3:::bucket\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"prod\"}]}", "ResourceArn")]
    [InlineData("{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"\",\"Value\":\"prod\"}]}", "Tag keys")]
    [InlineData("{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"aws:reserved\",\"Value\":\"prod\"}]}", "Tag keys")]
    [InlineData("{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"bad*key\",\"Value\":\"prod\"}]}", "Unicode letters")]
    [InlineData("{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"env\",\"Value\":\"", "Tag values")]
    public async Task TagResource_validation_errors_return_400(string requestPrefix, string expected)
    {
        var request = requestPrefix == "{\"ResourceArn\":\"orders\",\"Tags\":[{\"Key\":\"env\",\"Value\":\""
            ? requestPrefix + new string('x', 257) + "\"}]}"
            : requestPrefix;
        var handler = new MetadataHandler(tableExists: true, MetadataJson());
        var cosmos = BuildClient(handler);
        var (ctx, body) = NewCtx();

        await TaggingHandlers.HandleTagResourceAsync(ctx, Encoding.UTF8.GetBytes(request), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains(expected, ReadResponse(body));
    }

    [Fact]
    public async Task TagResource_rejects_more_than_50_tags()
    {
        var sb = new StringBuilder("{\"ResourceArn\":\"orders\",\"Tags\":[");
        for (int i = 0; i < 51; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"Key\":\"k").Append(i).Append("\",\"Value\":\"v\"}");
        }
        sb.Append("]}");
        var handler = new MetadataHandler(tableExists: true, MetadataJson());
        var cosmos = BuildClient(handler);
        var (ctx, body) = NewCtx();

        await TaggingHandlers.HandleTagResourceAsync(ctx, Encoding.UTF8.GetBytes(sb.ToString()), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("50", ReadResponse(body));
    }

    private static async Task InvokeTagAsync(CosmosClient cosmos, string request)
    {
        var (ctx, body) = NewCtx();
        await TaggingHandlers.HandleTagResourceAsync(ctx, Encoding.UTF8.GetBytes(request), cosmos, default);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    private static async Task InvokeUntagAsync(CosmosClient cosmos, string request)
    {
        var (ctx, body) = NewCtx();
        await TaggingHandlers.HandleUntagResourceAsync(ctx, Encoding.UTF8.GetBytes(request), cosmos, default);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    private static async Task<List<(string Key, string Value)>> InvokeListAsync(CosmosClient cosmos, string request)
    {
        var (ctx, body) = NewCtx();
        await TaggingHandlers.HandleListTagsOfResourceAsync(ctx, Encoding.UTF8.GetBytes(request), cosmos, default);
        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var tags = new List<(string Key, string Value)>();
        foreach (var tag in doc.RootElement.GetProperty("Tags").EnumerateArray())
        {
            tags.Add((tag.GetProperty("Key").GetString()!, tag.GetProperty("Value").GetString()!));
        }
        return tags;
    }

    private static void AssertTag(List<(string Key, string Value)> tags, string key, string value)
    {
        Assert.Contains(tags, t => t.Key == key && t.Value == value);
    }

    private static CosmosClient BuildClient(HttpMessageHandler handler)
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
        return new StreamReader(body, Encoding.UTF8).ReadToEnd();
    }

    private static string MetadataJson()
        => "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
            + "\"tableName\":\"orders\","
            + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}],"
            + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}]}";

    private sealed class MetadataHandler(bool tableExists, string? metadataJson) : HttpMessageHandler
    {
        public string? MetadataJson { get; private set; } = metadataJson;
        public int UpsertCount { get; private set; }
        public int PreconditionFailuresBeforeSuccess { get; set; }
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(ct);
            }
            Requests.Add(new CapturedRequest(request.Method, path, headers, body));

            if (request.Method == HttpMethod.Get && path.EndsWith("/docs/__aws2azure_table_meta__", StringComparison.Ordinal))
            {
                if (MetadataJson is null) return NotFound();
                return JsonOk(MetadataJson);
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/colls/orders", StringComparison.Ordinal))
            {
                return tableExists ? JsonOk("{\"id\":\"orders\"}") : NotFound();
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/colls/orders/docs", StringComparison.Ordinal))
            {
                MetadataJson = body;
                UpsertCount++;
                return JsonOk("{\"id\":\"__aws2azure_table_meta__\"}");
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/colls/orders/docs/__aws2azure_table_meta__", StringComparison.Ordinal))
            {
                if (PreconditionFailuresBeforeSuccess > 0)
                {
                    PreconditionFailuresBeforeSuccess--;
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    };
                }
                MetadataJson = body;
                UpsertCount++;
                return JsonOk("{\"id\":\"__aws2azure_table_meta__\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("unexpected " + request.Method + " " + path, Encoding.UTF8, "text/plain"),
            };
        }

        private static HttpResponseMessage JsonOk(string body)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"1\"");
            return resp;
        }

        private static HttpResponseMessage NotFound()
            => new(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        Dictionary<string, string> Headers,
        string? Body);
}
