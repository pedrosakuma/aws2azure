using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.SecretsManager;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.UnitTests.SecretsManager;

public sealed class SecretsManagerAuthDiagnosticsTests
{
    private const string RequestId = "0HNABCDEFG123:00000001";
    private const string UpstreamId = "fedcba98-7654-4321-abcd-0123456789ab";
    private const string Sentinel = "SENSITIVE_SENTINEL";

    [Theory]
    [InlineData(400, 403, "AccessDeniedException", 1)]
    [InlineData(401, 403, "AccessDeniedException", 4)]
    [InlineData(403, 403, "AccessDeniedException", 1)]
    [InlineData(429, 429, "ThrottlingException", 1)]
    [InlineData(500, 503, "InternalServiceError", 1)]
    [InlineData(503, 503, "InternalServiceError", 1)]
    public async Task Token_failure_preserves_status_mapping_body_and_attempts(
        int upstreamStatus, int awsStatus, string awsCode, int attempts)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler((_, _) => Task.FromResult(Response(upstreamStatus, Sentinel)));
        using var http = CreateHttp(handler);
        var context = Context("ListSecrets");

        await Module(http, logger).HandleAsync(context);

        Assert.Equal(awsStatus, context.Response.StatusCode);
        Assert.Equal(AwsErrorResponse.BuildJson(awsCode, $"Entra ID token request failed with HTTP {upstreamStatus}."), Body(context));
        Assert.Equal(attempts, handler.TokenCalls);
        Assert.Equal(0, handler.VaultCalls);
        var entry = Assert.Single(logger.Entries);
        AssertEvent(entry, 5, "ListSecrets", RequestId);
        Assert.Equal(upstreamStatus, entry.Fields["TokenStatus"]);
        Assert.Contains("UpstreamRequestId=unavailable", entry.Message);
        Assert.DoesNotContain("UpstreamStatus", entry.Fields.Keys);
        AssertSanitized(logger);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Vault_auth_failure_preserves_wire_response_and_does_not_read_body(int status)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler((request, _) =>
        {
            if (IsToken(request))
            {
                return Task.FromResult(Token());
            }

            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new UnreadableContent() };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", UpstreamId);
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", Sentinel);
            return Task.FromResult(response);
        });
        using var http = CreateHttp(handler);
        var context = Context("ListSecrets");

        await Module(http, logger).HandleAsync(context);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(AwsErrorResponse.BuildJson("AccessDeniedException", "Key Vault request failed."), Body(context));
        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(1, handler.VaultCalls);
        var entry = Assert.Single(logger.Entries);
        AssertEvent(entry, 6, "ListSecrets", RequestId);
        Assert.Equal(status, entry.Fields["UpstreamStatus"]);
        Assert.Equal(UpstreamId, entry.Fields["UpstreamRequestId"]);
        Assert.DoesNotContain("TokenStatus", entry.Fields.Keys);
        AssertSanitized(logger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SENSITIVE_SENTINEL")]
    [InlineData("fedcba98-7654-4321-abcd-0123456789ab\r\nSENSITIVE_SENTINEL")]
    [InlineData("fedcba98-7654-4321-abcd-0123456789ab,SENSITIVE_SENTINEL")]
    [InlineData("oversized")]
    [InlineData("multiple")]
    public async Task Unsafe_or_missing_metadata_is_explicitly_unavailable(string? metadata)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler((request, _) =>
        {
            var response = IsToken(request) ? Token() : Response(401, Sentinel);
            if (!IsToken(request) && metadata is not null)
            {
                response.Headers.TryAddWithoutValidation("x-ms-request-id", metadata switch
                {
                    "oversized" => [new string('a', 8192) + Sentinel],
                    "multiple" => [UpstreamId, UpstreamId],
                    _ => [metadata],
                });
            }

            return Task.FromResult(response);
        });
        using var http = CreateHttp(handler);
        var context = Context("ListSecrets");
        context.TraceIdentifier = metadata == "oversized" ? new string('a', 8192) + Sentinel : metadata ?? "";

        await Module(http, logger).HandleAsync(context);

        var entry = Assert.Single(logger.Entries);
        AssertEvent(entry, 6, "ListSecrets", "unavailable");
        Assert.Equal("unavailable", entry.Fields["UpstreamRequestId"]);
        AssertSanitized(logger);
        Assert.True(entry.Message.Length < 250);
    }

    [Theory]
    [InlineData(RequestId)]
    [InlineData(UpstreamId)]
    [InlineData("fedcba9876544321abcd0123456789ab")]
    public void Safe_request_ids_are_retained(string requestId)
        => Assert.Equal(requestId, SecretsManagerLog.SafeRequestId(requestId));

    [Fact]
    public async Task Concurrent_requests_on_cached_binding_keep_operation_and_request_identity()
    {
        var logger = new RecordingLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ScriptedHandler(async (request, ct) =>
        {
            if (IsToken(request))
            {
                return Token();
            }

            if (request.RequestUri!.AbsolutePath == "/secrets")
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                return Response(401, Sentinel);
            }

            return Response(403, Sentinel);
        });
        using var http = CreateHttp(handler);
        var module = Module(http, logger);
        var first = Context("ListSecrets");
        var second = Context("DescribeSecret", UpstreamId);
        var firstTask = module.HandleAsync(first).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await module.HandleAsync(second);
        }
        finally
        {
            release.SetResult();
        }

        await firstTask;
        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(2, handler.VaultCalls);
        Assert.Equal(403, first.Response.StatusCode);
        Assert.Equal(403, second.Response.StatusCode);
        var entries = logger.Entries.ToArray();
        Assert.Equal(2, entries.Length);
        AssertEvent(entries[0], 6, "DescribeSecret", UpstreamId);
        Assert.Equal(403, entries[0].Fields["UpstreamStatus"]);
        AssertEvent(entries[1], 6, "ListSecrets", RequestId);
        Assert.Equal(401, entries[1].Fields["UpstreamStatus"]);
        AssertSanitized(logger);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caller_cancellation_propagates_without_auth_event(bool duringTokenAcquisition)
    {
        var logger = new RecordingLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ScriptedHandler(async (request, ct) =>
        {
            if (IsToken(request) == duringTokenAcquisition)
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }

            return IsToken(request) ? Token() : Response(200, "{\"value\":[]}");
        });
        using var http = CreateHttp(handler);
        using var cancellation = new CancellationTokenSource();
        var context = Context("ListSecrets");
        context.RequestAborted = cancellation.Token;
        var task = Module(http, logger).HandleAsync(context).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Empty(logger.Entries);
        Assert.Equal("", Body(context));
        Assert.Equal(duringTokenAcquisition ? 0 : 1, handler.VaultCalls);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Success_and_non_auth_vault_failures_emit_no_auth_event(int status)
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler((request, _) =>
            Task.FromResult(IsToken(request) ? Token() : Response(status, "{\"value\":[]}")));
        using var http = CreateHttp(handler);
        var context = Context("ListSecrets");

        await Module(http, logger).HandleAsync(context);

        Assert.Empty(logger.Entries);
        Assert.Equal(1, handler.VaultCalls);
        Assert.Equal(status == 500 ? 503 : status, context.Response.StatusCode);
        if (status == 200)
        {
            Assert.Equal("{\"SecretList\":[]}", Body(context));
        }
    }

    [Fact]
    public async Task Token_transport_failure_has_reported_synthetic_status_without_exception_details()
    {
        var logger = new RecordingLogger();
        using var handler = new ScriptedHandler((_, _) => throw new HttpRequestException(Sentinel));
        using var http = CreateHttp(handler);
        var context = Context("ListSecrets");

        await Module(http, logger).HandleAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        var entry = Assert.Single(logger.Entries);
        AssertEvent(entry, 5, "ListSecrets", RequestId);
        Assert.Equal(503, entry.Fields["TokenStatus"]);
        AssertSanitized(logger);
    }

    [Fact]
    public async Task Transient_token_401_followed_by_success_emits_no_failure_event()
    {
        var logger = new RecordingLogger();
        var attempts = 0;
        using var handler = new ScriptedHandler((request, _) => Task.FromResult(
            IsToken(request)
                ? Interlocked.Increment(ref attempts) == 1 ? Response(401, Sentinel) : Token()
                : Response(200, "{\"value\":[]}")));
        using var http = CreateHttp(handler);
        var context = Context("ListSecrets");

        await Module(http, logger).HandleAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(2, handler.TokenCalls);
        Assert.Equal(1, handler.VaultCalls);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Later_backend_failure_keeps_incoming_operation_and_wire_response(bool loggingEnabled)
    {
        var logger = new RecordingLogger { Enabled = loggingEnabled };
        using var handler = new ScriptedHandler((request, _) => Task.FromResult(
            IsToken(request) ? Token() : request.Method == HttpMethod.Get ? Response(404, "{}") : Response(401, Sentinel)));
        using var http = CreateHttp(handler);
        var context = Context("CreateSecret");

        await Module(http, logger).HandleAsync(context);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(AwsErrorResponse.BuildJson("AccessDeniedException", "Key Vault request failed."), Body(context));
        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(2, handler.VaultCalls);
        if (loggingEnabled)
        {
            AssertEvent(Assert.Single(logger.Entries), 6, "CreateSecret", RequestId);
        }
        else
        {
            Assert.Empty(logger.Entries);
        }

        AssertSanitized(logger);
    }

    private static void AssertEvent(Entry entry, int id, string operation, string requestId)
    {
        Assert.Equal(id, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(operation, entry.Fields["Operation"]);
        Assert.Equal(requestId, entry.Fields["RequestId"]);
        Assert.Null(entry.Exception);
    }

    private static void AssertSanitized(RecordingLogger logger)
    {
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(Sentinel, entry.Message);
            Assert.DoesNotContain("https://", entry.Message);
            Assert.DoesNotContain("Authorization", entry.Message);
            Assert.All(entry.Fields.Values, value => Assert.DoesNotContain(Sentinel, value?.ToString() ?? ""));
            Assert.Null(entry.Exception);
        }
    }

    private static AzureHttpClient CreateHttp(HttpMessageHandler handler)
        => new(handler, ownsHandler: false, new AzureHttpClientOptions { MaxAttempts = 1 });

    private static SecretsManagerServiceModule Module(AzureHttpClient http, RecordingLogger logger)
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = Sentinel + "-access",
                    AwsSecretAccessKey = Sentinel + "-signing",
                    Azure = new AzureCredentials
                    {
                        KeyVault = new KeyVaultCredentials
                        {
                            VaultUrl = "https://sensitive-sentinel.vault.azure.net/",
                            TenantId = Sentinel + "-tenant",
                            ClientId = Sentinel + "-client",
                            ClientSecret = Sentinel + "-credential",
                        },
                    },
                },
            },
        };
        return new SecretsManagerServiceModule(http, new StaticCredentialResolver(config),
            new CapabilityMatrix("secretsmanager", []), logger: logger);
    }

    private static DefaultHttpContext Context(string operation, string requestId = RequestId)
    {
        var context = new DefaultHttpContext { TraceIdentifier = requestId };
        context.Request.Headers["X-Amz-Target"] = "SecretsManager." + operation;
        context.Request.Headers.Authorization = Sentinel;
        context.Request.Headers["x-ms-request-id"] = Sentinel;
        context.Items["aws2azure.accessKeyId"] = Sentinel + "-access";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"Name":"{{Sentinel}}-name","SecretId":"{{Sentinel}}-name","SecretString":"{{Sentinel}}-value","NextToken":"{{Sentinel}}-continuation"}"""));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static bool IsToken(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal);

    private static HttpResponseMessage Token()
        => Response(200, $$"""{"access_token":"{{Sentinel}}-bearer","expires_in":3600}""");

    private static HttpResponseMessage Response(int status, string body)
        => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int TokenCalls;
        public int VaultCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (IsToken(request))
            {
                Interlocked.Increment(ref TokenCalls);
            }
            else
            {
                Interlocked.Increment(ref VaultCalls);
            }

            return responder(request, cancellationToken);
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("Diagnostic code must not read the response body.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed record Entry(LogLevel Level, EventId EventId, string Message, Dictionary<string, object?> Fields, Exception? Exception);

    private sealed class RecordingLogger : ILogger<SecretsManagerServiceModule>
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public bool Enabled { get; init; } = true;
        public bool IsEnabled(LogLevel logLevel) => Enabled;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new(logLevel, eventId, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value), exception));
    }
}
