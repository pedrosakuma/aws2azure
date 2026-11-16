using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.SecretsManager;
using Aws2Azure.Modules.SecretsManager.Operations;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.SecretsManager;

public sealed class BatchGetSecretValueTests
{
    [Fact]
    public async Task HandleAsync_returns_mixed_values_and_errors_in_input_order()
    {
        using var http = new AzureHttpClient(
            new BatchKeyVaultHandler(new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal)
            {
                ["missing"] = HttpStatusCode.NotFound,
                ["denied"] = HttpStatusCode.Forbidden,
            }),
            ownsHandler: false);
        var module = CreateModule(http);
        var context = CreateContext(
            """{"SecretIdList":["alpha","missing","denied","omega"]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        var values = document.RootElement.GetProperty("SecretValues");
        Assert.Equal(2, values.GetArrayLength());
        Assert.Equal("alpha", values[0].GetProperty("Name").GetString());
        Assert.Equal("value-alpha", values[0].GetProperty("SecretString").GetString());
        Assert.Equal("omega", values[1].GetProperty("Name").GetString());

        var errors = document.RootElement.GetProperty("Errors");
        Assert.Equal(2, errors.GetArrayLength());
        Assert.Equal("missing", errors[0].GetProperty("SecretId").GetString());
        Assert.Equal("ResourceNotFoundException", errors[0].GetProperty("ErrorCode").GetString());
        Assert.Equal("denied", errors[1].GetProperty("SecretId").GetString());
        Assert.Equal("AccessDeniedException", errors[1].GetProperty("ErrorCode").GetString());
    }

    [Fact]
    public async Task HandleAsync_preserves_duplicate_secret_ids_deterministically()
    {
        var handler = new BatchKeyVaultHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var module = CreateModule(http);
        var context = CreateContext("""{"SecretIdList":["alpha","alpha"]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        var values = document.RootElement.GetProperty("SecretValues");
        Assert.Equal(2, values.GetArrayLength());
        Assert.Equal("alpha", values[0].GetProperty("Name").GetString());
        Assert.Equal("alpha", values[1].GetProperty("Name").GetString());
        Assert.Empty(document.RootElement.GetProperty("Errors").EnumerateArray());
        Assert.Equal(2, handler.VersionListCalls["alpha"]);
    }

    [Fact]
    public async Task HandleAsync_returns_invalid_normalized_secret_id_as_per_secret_error()
    {
        var handler = new BatchKeyVaultHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var module = CreateModule(http);
        var context = CreateContext("""{"SecretIdList":["alpha","/","omega"]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        var values = document.RootElement.GetProperty("SecretValues");
        Assert.Equal(2, values.GetArrayLength());
        Assert.Equal("alpha", values[0].GetProperty("Name").GetString());
        Assert.Equal("omega", values[1].GetProperty("Name").GetString());
        var error = Assert.Single(document.RootElement.GetProperty("Errors").EnumerateArray());
        Assert.Equal("/", error.GetProperty("SecretId").GetString());
        Assert.Equal("InvalidParameterException", error.GetProperty("ErrorCode").GetString());
    }

    [Fact]
    public async Task HandleAsync_supports_single_secret_boundary()
    {
        using var http = new AzureHttpClient(new BatchKeyVaultHandler(), ownsHandler: false);
        var module = CreateModule(http);
        var context = CreateContext("""{"SecretIdList":["alpha"]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Single(document.RootElement.GetProperty("SecretValues").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("Errors").EnumerateArray());
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return ["{}"];
        yield return ["""{"SecretIdList":[]}"""];
        yield return ["""{"SecretIdList":[""]}"""];
        yield return ["""{"SecretIdList":[42]}"""];
        yield return ["""{"SecretIdList":"alpha"}"""];
        yield return ["""{"SecretIdList":["alpha"],"Filters":[]}"""];
        yield return ["""{"SecretIdList":["alpha"],"MaxResults":1}"""];
        yield return ["""{"SecretIdList":["alpha"],"NextToken":"token"}"""];
        yield return [JsonSerializer.Serialize(new
        {
            SecretIdList = Enumerable.Range(0, BatchGetSecretValueHandler.MaxSecretIds + 1)
                .Select(index => $"secret-{index}")
                .ToArray(),
        })];
        yield return [JsonSerializer.Serialize(new
        {
            SecretIdList = new[] { new string('a', 2049) },
        })];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task HandleAsync_rejects_invalid_secret_id_list_without_backend_calls(string body)
    {
        var handler = new BatchKeyVaultHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var module = CreateModule(http);
        var context = CreateContext(body);

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("InvalidParameterException", document.RootElement.GetProperty("__type").GetString());
        Assert.Equal(0, handler.TotalCalls);
    }

    [Fact]
    public async Task HandleAsync_reports_filters_as_documented_not_implemented_without_backend_calls()
    {
        var handler = new BatchKeyVaultHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var module = CreateModule(http);
        var context = CreateContext("""{"Filters":[{"Key":"name","Values":["prod"]}]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("NotImplementedException", document.RootElement.GetProperty("__type").GetString());
        Assert.Equal(0, handler.TotalCalls);
    }

    [Fact]
    public async Task HandleAsync_bounds_physical_key_vault_fan_out()
    {
        var handler = new GatedConcurrencyHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var module = CreateModule(http);
        var ids = Enumerable.Range(0, BatchGetSecretValueHandler.MaxSecretIds)
            .Select(index => $"secret-{index}")
            .ToArray();
        var context = CreateContext(JsonSerializer.Serialize(new { SecretIdList = ids }));

        var handling = module.HandleAsync(context).AsTask();
        await handler.PeakReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BatchGetSecretValueHandler.MaxConcurrentSecrets * 2, handler.MaxObserved);
        handler.Release();
        await handling;

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(handler.MaxObserved <= BatchGetSecretValueHandler.MaxConcurrentSecrets * 2);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal(BatchGetSecretValueHandler.MaxSecretIds, document.RootElement.GetProperty("SecretValues").GetArrayLength());
    }

    [Fact]
    public async Task HandleAsync_cancels_in_flight_key_vault_requests()
    {
        var handler = new CancellationHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var module = CreateModule(http);
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext("""{"SecretIdList":["alpha","beta","gamma","delta"]}""");
        context.RequestAborted = cancellation.Token;

        var handling = module.HandleAsync(context).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handling);
        Assert.True(handler.CancelledRequests > 0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, StatusCodes.Status403Forbidden, "AccessDeniedException")]
    [InlineData(HttpStatusCode.RequestTimeout, StatusCodes.Status503ServiceUnavailable, "InternalServiceError")]
    [InlineData(HttpStatusCode.TooManyRequests, StatusCodes.Status429TooManyRequests, "ThrottlingException")]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status503ServiceUnavailable, "InternalServiceError")]
    public async Task HandleAsync_returns_backend_wide_failures_at_request_level(
        HttpStatusCode backendStatus,
        int expectedStatus,
        string expectedCode)
    {
        using var http = new AzureHttpClient(
            new BatchKeyVaultHandler(new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal)
            {
                ["failed"] = backendStatus,
            }),
            ownsHandler: false,
            NoRetryOptions());
        var module = CreateModule(http);
        var context = CreateContext("""{"SecretIdList":["failed","alpha","omega"]}""");

        await module.HandleAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal(expectedCode, document.RootElement.GetProperty("__type").GetString());
        Assert.False(document.RootElement.TryGetProperty("SecretValues", out _));
    }

    [Fact]
    public async Task HandleAsync_returns_key_vault_timeout_as_request_level_error()
    {
        using var http = new AzureHttpClient(
            new TimeoutHandler(),
            ownsHandler: false,
            NoRetryOptions());
        var module = CreateModule(http);
        var context = CreateContext("""{"SecretIdList":["alpha","beta","gamma","delta"]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("InternalServiceError", document.RootElement.GetProperty("__type").GetString());
        Assert.False(document.RootElement.TryGetProperty("SecretValues", out _));
    }

    [Fact]
    public async Task HandleAsync_returns_transport_failure_as_request_level_error()
    {
        using var http = new AzureHttpClient(
            new TransportFailureHandler(),
            ownsHandler: false,
            NoRetryOptions());
        var module = CreateModule(http);
        var context = CreateContext("""{"SecretIdList":["alpha","beta"]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("InternalServiceError", document.RootElement.GetProperty("__type").GetString());
        Assert.False(document.RootElement.TryGetProperty("SecretValues", out _));
    }

    [Fact]
    public async Task HandleAsync_returns_token_failure_before_starting_key_vault_workers()
    {
        var handler = new TokenFailureHandler();
        using var http = new AzureHttpClient(
            handler,
            ownsHandler: false,
            NoRetryOptions());
        var module = CreateModule(http);
        var context = CreateContext("""{"SecretIdList":["alpha","beta"]}""");

        await module.HandleAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal("AccessDeniedException", document.RootElement.GetProperty("__type").GetString());
        Assert.Equal(0, handler.KeyVaultCalls);
    }

    [Fact]
    public void KnownOperations_includes_batch_get_secret_value()
    {
        using var http = new AzureHttpClient(new BatchKeyVaultHandler(), ownsHandler: false);
        var module = CreateModule(http);

        Assert.Contains("BatchGetSecretValue", module.KnownOperations);
    }

    private static SecretsManagerServiceModule CreateModule(AzureHttpClient http)
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA1",
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials
                    {
                        KeyVault = new KeyVaultCredentials
                        {
                            VaultUrl = "https://example.vault.azure.net/",
                            TenantId = "tenant",
                            ClientId = "client",
                            ClientSecret = "secret",
                        },
                    },
                },
            },
        };

        return new SecretsManagerServiceModule(
            http,
            new StaticCredentialResolver(config),
            new CapabilityMatrix("secretsmanager", []));
    }

    private static AzureHttpClientOptions NoRetryOptions()
        => new()
        {
            MaxAttempts = 1,
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
        };

    private static DefaultHttpContext CreateContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/";
        context.Request.ContentType = "application/x-amz-json-1.1";
        context.Request.Headers["X-Amz-Target"] = "SecretsManager.BatchGetSecretValue";
        context.Items["aws2azure.accessKeyId"] = "AKIA1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class BatchKeyVaultHandler(
        IReadOnlyDictionary<string, HttpStatusCode>? statuses = null) : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, HttpStatusCode> _statuses =
            statuses ?? new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal);
        private readonly object _sync = new();

        public int TotalCalls { get; private set; }
        public Dictionary<string, int> VersionListCalls { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                TotalCalls++;
            }

            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}"""));
            }

            var (name, isVersionList) = ParseSecretRequest(request.RequestUri.AbsolutePath);
            if (isVersionList)
            {
                lock (_sync)
                {
                    VersionListCalls.TryGetValue(name, out var calls);
                    VersionListCalls[name] = calls + 1;
                }
            }

            var status = _statuses.TryGetValue(name, out var configured)
                ? configured
                : HttpStatusCode.OK;
            return Task.FromResult(status == HttpStatusCode.OK
                ? BuildSuccessResponse(name, isVersionList)
                : JsonResponse("""{"error":{"code":"test"}}""", status));
        }
    }

    private sealed class GatedConcurrencyHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _maxObserved;

        public TaskCompletionSource<bool> PeakReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxObserved => Volatile.Read(ref _maxObserved);

        public void Release() => _release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return JsonResponse("""{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""");
            }

            var current = Interlocked.Increment(ref _inFlight);
            UpdateMaximum(current);
            if (current == BatchGetSecretValueHandler.MaxConcurrentSecrets * 2)
            {
                PeakReached.TrySetResult(true);
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                var (name, isVersionList) = ParseSecretRequest(request.RequestUri.AbsolutePath);
                return BuildSuccessResponse(name, isVersionList);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxObserved);
                if (current <= observed
                    || Interlocked.CompareExchange(ref _maxObserved, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        private int _cancelledRequests;

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelledRequests => Volatile.Read(ref _cancelledRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return JsonResponse("""{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""");
            }

            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation gate unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelledRequests);
                throw;
            }
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}"""));
            }

            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("Simulated Key Vault timeout."));
        }
    }

    private sealed class TransportFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}"""));
            }

            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Simulated Key Vault transport failure."));
        }
    }

    private sealed class TokenFailureHandler : HttpMessageHandler
    {
        private int _keyVaultCalls;

        public int KeyVaultCalls => Volatile.Read(ref _keyVaultCalls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """{"error":"invalid_client","error_description":"Invalid client secret."}""",
                    HttpStatusCode.Unauthorized));
            }

            Interlocked.Increment(ref _keyVaultCalls);
            return Task.FromResult(JsonResponse("{}", HttpStatusCode.InternalServerError));
        }
    }

    private static (string Name, bool IsVersionList) ParseSecretRequest(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = Uri.UnescapeDataString(parts[1]);
        return (name, parts.Length == 3 && string.Equals(parts[2], "versions", StringComparison.Ordinal));
    }

    private static HttpResponseMessage BuildSuccessResponse(string name, bool isVersionList)
    {
        var id = $"https://example.vault.azure.net/secrets/{name}/versions/version-{name}";
        var json = isVersionList
            ? $"{{\"value\":[{{\"id\":\"{id}\",\"attributes\":{{\"created\":1710000000}},\"tags\":{{\"aws2azure-version-stages\":\"AWSCURRENT\"}}}}]}}"
            : $"{{\"value\":\"value-{name}\",\"id\":\"{id}\",\"contentType\":\"text/plain\",\"attributes\":{{\"created\":1710000000}},\"tags\":{{\"aws2azure-version-stages\":\"AWSCURRENT\"}}}}";
        return JsonResponse(json);
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
