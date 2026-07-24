using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb;

public sealed class SprocContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"success\":\"true\"}")]
    [InlineData("{\"success\":true,\"conditionFailed\":true}")]
    public async Task Single_write_malformed_2xx_never_reports_commit(string body)
    {
        using var response = CosmosOk(body);

        var result = await SprocResponseParser.ParseSingleWriteAsync(
            response,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.ConditionFailed);
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"success\":false}")]
    [InlineData("{\"success\":\"true\",\"reasons\":[]}")]
    [InlineData("{\"success\":true,\"reasons\":[]}")]
    public async Task Transaction_malformed_2xx_never_reports_commit(string body)
    {
        using var response = CosmosOk(body);

        var result = await SprocResponseParser.ParseTransactAsync(
            response,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.ConditionFailed);
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"success\":true}")]
    [InlineData("{\"success\":false,\"items\":[]}")]
    public async Task Snapshot_malformed_2xx_never_reports_success(string body)
    {
        using var response = CosmosOk(body);

        var result = await SprocResponseParser.ParseTransactGetAsync(
            response,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Fact]
    public async Task Existing_versioned_sproc_is_accepted_only_after_body_verification()
    {
        var existing = JsonSerializer.Serialize(new
        {
            id = SprocManager.TransactSprocId,
            body = SprocManager.TransactSprocBody,
        });
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.Conflict, "{}"),
                CosmosOk(existing),
            },
        };
        var manager = new SprocManager(NullLogger<SprocManager>.Instance);

        var available = await manager.EnsureTransactSprocAsync(
            BuildClient(handler),
            "orders",
            CancellationToken.None);

        Assert.True(available);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal(HttpMethod.Post, request.Method),
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.EndsWith(
                    "/sprocs/" + SprocManager.TransactSprocId,
                    request.Uri.AbsolutePath,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Conflicting_sproc_body_is_rejected()
    {
        var existing = JsonSerializer.Serialize(new
        {
            id = SprocManager.TransactSprocId,
            body = "function different() {}",
        });
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.Conflict, "{}"),
                CosmosOk(existing),
            },
        };
        var manager = new SprocManager(NullLogger<SprocManager>.Instance);

        var available = await manager.EnsureTransactSprocAsync(
            BuildClient(handler),
            "orders",
            CancellationToken.None);

        Assert.False(available);
    }

    [Fact]
    public void Transaction_v3_script_fails_closed_for_missing_and_unknown_operands()
    {
        Assert.Contains(
            "if (!left.exists || !right.exists",
            SprocManager.TransactSprocBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "return (ast.op === '=' || ast.op === 'EQ') ? equal : !equal;",
            SprocManager.TransactSprocBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "throw new Error('Unsupported condition AST node:",
            SprocManager.TransactSprocBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Transaction_v3_script_compares_strings_by_utf8_bytes()
    {
        Assert.Contains(
            "function compareUtf8(left, right)",
            SprocManager.TransactSprocBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "return compareUtf8(left, right);",
            SprocManager.TransactSprocBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "code = 0x10000 + ((code - 0xD800) << 10)",
            SprocManager.TransactSprocBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transaction_execution_disables_automatic_http_retries()
    {
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.ServiceUnavailable, "{}"),
                CosmosOk("{\"success\":true}"),
            },
        };
        var manager = new SprocManager(NullLogger<SprocManager>.Instance);

        var result = await manager.ExecuteTransactAsync(
            BuildClient(handler, maxAttempts: 3),
            "orders",
            "partition",
            Encoding.UTF8.GetBytes("[[]]"),
            CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, result.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.True(request.NoRetry);
    }

    [Fact]
    public async Task Sproc_provisioning_retains_automatic_http_retries()
    {
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.ServiceUnavailable, "{}"),
                CosmosStatus(HttpStatusCode.Created, "{}"),
            },
        };
        var manager = new SprocManager(NullLogger<SprocManager>.Instance);

        var available = await manager.EnsureTransactSprocAsync(
            BuildClient(handler, maxAttempts: 2),
            "orders",
            CancellationToken.None);

        Assert.True(available);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.False(request.NoRetry));
    }

    [Fact]
    public async Task Sproc_cache_is_isolated_by_cosmos_account()
    {
        var firstHandler = new ScriptedHandler
        {
            Responses = { CosmosStatus(HttpStatusCode.Created, "{}") },
        };
        var secondHandler = new ScriptedHandler
        {
            Responses = { CosmosStatus(HttpStatusCode.Created, "{}") },
        };
        var manager = new SprocManager(NullLogger<SprocManager>.Instance);

        Assert.True(await manager.EnsureTransactSprocAsync(
            BuildClient(firstHandler, endpoint: "https://first.documents.azure.com/"),
            "orders",
            CancellationToken.None));
        Assert.True(await manager.EnsureTransactSprocAsync(
            BuildClient(secondHandler, endpoint: "https://second.documents.azure.com/"),
            "orders",
            CancellationToken.None));

        Assert.Single(firstHandler.Requests);
        Assert.Single(secondHandler.Requests);
    }

    [Fact]
    public async Task Cached_sproc_not_found_is_evicted_reprovisioned_and_executed_once()
    {
        var handler = new ScriptedHandler
        {
            Responses =
            {
                CosmosStatus(HttpStatusCode.Created, "{}"),
                CosmosStatus(HttpStatusCode.NotFound, "{\"code\":\"NotFound\"}"),
                CosmosStatus(HttpStatusCode.Created, "{}"),
                CosmosOk("{\"success\":true}"),
            },
        };
        var manager = new SprocManager(NullLogger<SprocManager>.Instance);
        var client = BuildClient(handler, maxAttempts: 3);

        Assert.True(await manager.EnsureTransactSprocAsync(
            client,
            "orders",
            CancellationToken.None));

        var result = await manager.ExecuteTransactAsync(
            client,
            "orders",
            "partition",
            Encoding.UTF8.GetBytes("[[]]"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.EndsWith("/sprocs", request.Uri.AbsolutePath, StringComparison.Ordinal);
                Assert.False(request.NoRetry);
            },
            request =>
            {
                Assert.EndsWith(
                    "/sprocs/" + SprocManager.TransactSprocId,
                    request.Uri.AbsolutePath,
                    StringComparison.Ordinal);
                Assert.True(request.NoRetry);
            },
            request =>
            {
                Assert.EndsWith("/sprocs", request.Uri.AbsolutePath, StringComparison.Ordinal);
                Assert.False(request.NoRetry);
            },
            request =>
            {
                Assert.EndsWith(
                    "/sprocs/" + SprocManager.TransactSprocId,
                    request.Uri.AbsolutePath,
                    StringComparison.Ordinal);
                Assert.True(request.NoRetry);
            });
    }

    private static CosmosClient BuildClient(
        ScriptedHandler handler,
        int maxAttempts = 1,
        string endpoint = "https://example.documents.azure.com/")
    {
        var http = new AzureHttpClient(
            handler,
            ownsHandler: false,
            new AzureHttpClientOptions
            {
                MaxAttempts = maxAttempts,
                BaseRetryDelay = TimeSpan.FromMilliseconds(1),
                MaxRetryDelay = TimeSpan.FromMilliseconds(2),
            });
        var credentials = new CosmosCredentials
        {
            Endpoint = endpoint,
            PrimaryKey =
                "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };
        return new CosmosClient(
            http,
            credentials,
            new MasterKeyCosmosAuthenticator(credentials.PrimaryKey));
    }

    private static HttpResponseMessage CosmosOk(string body)
        => CosmosStatus(HttpStatusCode.OK, body);

    private static HttpResponseMessage CosmosStatus(
        HttpStatusCode status,
        string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = [];
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Options.TryGetValue(
                    AzureHttpClient.NoRetryOption,
                    out var noRetry)
                && noRetry));
            if (Responses.Count == 0)
            {
                return Task.FromResult(
                    CosmosStatus(HttpStatusCode.InternalServerError, "{}"));
            }

            var response = Responses[0];
            Responses.RemoveAt(0);
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        bool NoRetry);
}
