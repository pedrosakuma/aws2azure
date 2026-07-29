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
/// Coverage for the DynamoDB TTL control plane (#465): UpdateTimeToLive arms the
/// Cosmos container (<c>defaultTtl = -1</c>) and records the attribute name in
/// the metadata sidecar; DescribeTimeToLive reflects that state. The fake Cosmos
/// handler models the container GET/replace and the metadata GET/POST/PUT.
/// </summary>
[Collection(DynamoDbTestCollection.Name)]
public class TimeToLiveHandlersTests
{
    public TimeToLiveHandlersTests()
    {
        CosmosOpsShared.MetadataCache.Clear();
    }

    [Fact]
    public async Task UpdateTimeToLive_enable_arms_container_and_persists_config()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: null));
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":true,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var resp = JsonDocument.Parse(ReadResponse(body));
        var echo = resp.RootElement.GetProperty("TimeToLiveSpecification");
        Assert.True(echo.GetProperty("Enabled").GetBoolean());
        Assert.Equal("expiresAt", echo.GetProperty("AttributeName").GetString());

        // Container was armed with defaultTtl = -1, preserving other props.
        Assert.NotNull(handler.ContainerJson);
        using var coll = JsonDocument.Parse(handler.ContainerJson!);
        Assert.Equal(-1, coll.RootElement.GetProperty("defaultTtl").GetInt32());
        Assert.Equal("orders", coll.RootElement.GetProperty("id").GetString());
        Assert.True(coll.RootElement.TryGetProperty("partitionKey", out _));
        Assert.True(coll.RootElement.TryGetProperty("indexingPolicy", out _));
        Assert.False(coll.RootElement.TryGetProperty("_rid", out _));

        // Metadata sidecar carries the TTL config.
        using var meta = JsonDocument.Parse(handler.MetadataJson!);
        var persisted = meta.RootElement.GetProperty("timeToLive");
        Assert.True(persisted.GetProperty("enabled").GetBoolean());
        Assert.Equal("expiresAt", persisted.GetProperty("attributeName").GetString());
    }

    [Fact]
    public async Task UpdateTimeToLive_disable_removes_default_ttl()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: ("expiresAt", true)));
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":false,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var coll = JsonDocument.Parse(handler.ContainerJson!);
        Assert.False(coll.RootElement.TryGetProperty("defaultTtl", out _));

        using var meta = JsonDocument.Parse(handler.MetadataJson!);
        Assert.False(meta.RootElement.GetProperty("timeToLive").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task UpdateTimeToLive_disable_rolls_back_container_when_metadata_write_fails()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: ("expiresAt", true)))
        {
            FailMetadataWrite = true,
            ContainerDefinitionJson =
                "{\"id\":\"orders\",\"_rid\":\"abc==\",\"_self\":\"dbs/x/colls/y/\",\"_etag\":\"\\\"42\\\"\",\"_ts\":1700000000,"
                + "\"_docs\":\"docs/\",\"_sprocs\":\"sprocs/\","
                + "\"partitionKey\":{\"paths\":[\"/_a2a_pk\"],\"kind\":\"Hash\"},"
                + "\"indexingPolicy\":{\"indexingMode\":\"consistent\",\"automatic\":true},"
                + "\"defaultTtl\":-1}",
        };
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":false,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("metadata", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.ContainerReplaceCount);
        using var coll = JsonDocument.Parse(handler.ContainerJson!);
        Assert.Equal(-1, coll.RootElement.GetProperty("defaultTtl").GetInt32());
        using var meta = JsonDocument.Parse(handler.MetadataJson!);
        Assert.True(meta.RootElement.GetProperty("timeToLive").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task UpdateTimeToLive_enable_rolls_back_container_when_metadata_write_fails()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: null))
        {
            FailMetadataWrite = true,
        };
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":true,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("metadata", ReadResponse(body), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.ContainerReplaceCount);
        using var coll = JsonDocument.Parse(handler.ContainerJson!);
        Assert.False(coll.RootElement.TryGetProperty("defaultTtl", out _));
        using var meta = JsonDocument.Parse(handler.MetadataJson!);
        Assert.False(meta.RootElement.TryGetProperty("timeToLive", out _));
    }

    [Fact]
    public async Task UpdateTimeToLive_preserves_unknown_ttl_extension_fields()
    {
        var metadata = MetadataJson(ttl: ("expiresAt", true))
            .Replace(
                "\"attributeName\":\"expiresAt\"}",
                "\"attributeName\":\"expiresAt\",\"futureTtlMode\":\"absolute-epoch\"}",
                StringComparison.Ordinal);
        var handler = new TtlCosmosHandler(metadata);
        var cosmos = BuildClient(handler);
        var (ctx, _) = NewCtx();

        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(
            ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":false,\"AttributeName\":\"expiresAt\"}}"),
            cosmos,
            default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var persisted = JsonDocument.Parse(handler.MetadataJson!);
        Assert.Equal(
            "absolute-epoch",
            persisted.RootElement.GetProperty("timeToLive")
                .GetProperty("futureTtlMode").GetString());
    }

    [Fact]
    public async Task DescribeTimeToLive_reports_enabled_state()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: ("expiresAt", true)));
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleDescribeTimeToLiveAsync(ctx, Bytes("{\"TableName\":\"orders\"}"), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var desc = doc.RootElement.GetProperty("TimeToLiveDescription");
        Assert.Equal("ENABLED", desc.GetProperty("TimeToLiveStatus").GetString());
        Assert.Equal("expiresAt", desc.GetProperty("AttributeName").GetString());
    }

    [Fact]
    public async Task DescribeTimeToLive_reports_disabled_when_unset()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: null));
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleDescribeTimeToLiveAsync(ctx, Bytes("{\"TableName\":\"orders\"}"), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var desc = doc.RootElement.GetProperty("TimeToLiveDescription");
        Assert.Equal("DISABLED", desc.GetProperty("TimeToLiveStatus").GetString());
        Assert.False(desc.TryGetProperty("AttributeName", out _));
    }

    [Fact]
    public async Task DescribeTimeToLive_reports_disabled_when_metadata_is_missing_but_table_exists()
    {
        var handler = new TtlCosmosHandler(metadataJson: null) { ContainerExists = true };
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleDescribeTimeToLiveAsync(ctx, Bytes("{\"TableName\":\"orders\"}"), cosmos, default);

        Assert.Equal(200, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadResponse(body));
        var desc = doc.RootElement.GetProperty("TimeToLiveDescription");
        Assert.Equal("DISABLED", desc.GetProperty("TimeToLiveStatus").GetString());
        Assert.False(desc.TryGetProperty("AttributeName", out _));
    }

    [Fact]
    public async Task DescribeTimeToLive_bypasses_warm_cache_when_metadata_becomes_malformed()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: ("expiresAt", true)));
        var cosmos = BuildClient(handler);

        var (warmCtx, _) = NewCtx();
        await TimeToLiveHandlers.HandleDescribeTimeToLiveAsync(warmCtx, Bytes("{\"TableName\":\"orders\"}"), cosmos, default);
        Assert.Equal(200, warmCtx.Response.StatusCode);

        handler.MetadataJson = "{}";

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleDescribeTimeToLiveAsync(ctx, Bytes("{\"TableName\":\"orders\"}"), cosmos, default);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("Malformed table metadata", ReadResponse(body));
    }

    [Fact]
    public async Task UpdateTimeToLive_missing_attribute_name_is_rejected()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: null));
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":true}}"), cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("AttributeName", ReadResponse(body));
        Assert.Null(handler.ContainerJson); // never touched Cosmos
    }

    [Fact]
    public async Task UpdateTimeToLive_unknown_table_is_resource_not_found()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: null)) { ContainerExists = false };
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":true,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadResponse(body));
    }

    [Fact]
    public async Task UpdateTimeToLive_missing_metadata_returns_internal_server_error_without_mutating_container()
    {
        var handler = new TtlCosmosHandler(metadataJson: null) { ContainerExists = true };
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":true,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("Missing table metadata", ReadResponse(body));
        Assert.Null(handler.ContainerJson);
    }

    [Fact]
    public async Task UpdateTimeToLive_empty_metadata_document_returns_internal_server_error()
    {
        var handler = new TtlCosmosHandler("{}");
        var cosmos = BuildClient(handler);

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":true,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("Malformed table metadata", ReadResponse(body));
        Assert.Null(handler.ContainerJson);
    }

    [Fact]
    public async Task UpdateTimeToLive_bypasses_warm_cache_when_metadata_becomes_malformed()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: null));
        var cosmos = BuildClient(handler);

        var (warmCtx, _) = NewCtx();
        await TimeToLiveHandlers.HandleDescribeTimeToLiveAsync(warmCtx, Bytes("{\"TableName\":\"orders\"}"), cosmos, default);
        Assert.Equal(200, warmCtx.Response.StatusCode);

        handler.MetadataJson = "{}";

        var (ctx, body) = NewCtx();
        await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(ctx,
            Bytes("{\"TableName\":\"orders\",\"TimeToLiveSpecification\":{\"Enabled\":true,\"AttributeName\":\"expiresAt\"}}"),
            cosmos, default);

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Contains("Malformed table metadata", ReadResponse(body));
        Assert.Null(handler.ContainerJson);
    }

    [Fact]
    public async Task Fresh_malformed_metadata_read_invalidates_stale_cache_for_subsequent_callers()
    {
        var handler = new TtlCosmosHandler(MetadataJson(ttl: null));
        var cosmos = BuildClient(handler);

        using (var warm = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, "orders", default))
        {
            Assert.Equal(CosmosOpsShared.TableMetadataReadStatus.Found, warm.Status);
        }

        handler.MetadataJson = "{}";

        using (var fresh = await CosmosOpsShared.TryReadTableMetadataAsync(
            cosmos, "orders", default, bypassCache: true))
        {
            Assert.Equal(CosmosOpsShared.TableMetadataReadStatus.Malformed, fresh.Status);
        }

        using var afterInvalidation = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, "orders", default);
        Assert.Equal(CosmosOpsShared.TableMetadataReadStatus.Malformed, afterInvalidation.Status);
    }

    private static string MetadataJson((string Attr, bool Enabled)? ttl)
    {
        var ttlJson = ttl is { } t
            ? ",\"timeToLive\":{\"enabled\":" + (t.Enabled ? "true" : "false") + ",\"attributeName\":\"" + t.Attr + "\"}"
            : string.Empty;
        return "{\"id\":\"__aws2azure_table_meta__\",\"_a2a_pk\":\"__aws2azure_table_meta__\",\"_meta\":\"table\","
            + "\"tableName\":\"orders\","
            + "\"attributeDefinitions\":[{\"name\":\"pk\",\"type\":\"S\"}],"
            + "\"keySchema\":[{\"name\":\"pk\",\"keyType\":\"HASH\"}]" + ttlJson + "}";
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

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

    private sealed class TtlCosmosHandler(string? metadataJson) : HttpMessageHandler
    {
        public bool ContainerExists { get; set; } = true;
        public string? MetadataJson { get; set; } = metadataJson;
        public bool FailMetadataWrite { get; set; }
        public string ContainerDefinitionJson { get; set; } = ContainerGetJson;

        /// <summary>The body the proxy PUT back to replace the container (null = never replaced).</summary>
        public string? ContainerJson { get; private set; }
        public int ContainerReplaceCount { get; private set; }

        private const string ContainerGetJson =
            "{\"id\":\"orders\",\"_rid\":\"abc==\",\"_self\":\"dbs/x/colls/y/\",\"_etag\":\"\\\"42\\\"\",\"_ts\":1700000000,"
            + "\"_docs\":\"docs/\",\"_sprocs\":\"sprocs/\","
            + "\"partitionKey\":{\"paths\":[\"/_a2a_pk\"],\"kind\":\"Hash\"},"
            + "\"indexingPolicy\":{\"indexingMode\":\"consistent\",\"automatic\":true}}";

        private const string ContainerGetJsonWithDefaultTtl =
            "{\"id\":\"orders\",\"_rid\":\"abc==\",\"_self\":\"dbs/x/colls/y/\",\"_etag\":\"\\\"42\\\"\",\"_ts\":1700000000,"
            + "\"_docs\":\"docs/\",\"_sprocs\":\"sprocs/\","
            + "\"partitionKey\":{\"paths\":[\"/_a2a_pk\"],\"kind\":\"Hash\"},"
            + "\"indexingPolicy\":{\"indexingMode\":\"consistent\",\"automatic\":true},"
            + "\"defaultTtl\":-1}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            if (request.Method == HttpMethod.Get && path.EndsWith("/colls/orders", StringComparison.Ordinal))
            {
                return ContainerExists ? JsonOk(ContainerDefinitionJson) : NotFound();
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/colls/orders", StringComparison.Ordinal))
            {
                ContainerJson = body;
                ContainerReplaceCount++;
                return JsonOk(body ?? "{}");
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/docs/__aws2azure_table_meta__", StringComparison.Ordinal))
            {
                return MetadataJson is null ? NotFound() : JsonOk(MetadataJson);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/colls/orders/docs", StringComparison.Ordinal))
            {
                MetadataJson = body;
                return JsonOk("{\"id\":\"__aws2azure_table_meta__\"}");
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/docs/__aws2azure_table_meta__", StringComparison.Ordinal))
            {
                if (FailMetadataWrite)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{\"message\":\"metadata write failed\"}", Encoding.UTF8, "application/json"),
                    };
                }
                MetadataJson = body;
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
}
