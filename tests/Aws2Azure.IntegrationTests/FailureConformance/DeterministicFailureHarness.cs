using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon.Runtime;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.DynamoDb;
using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.SecretsManager;
using Aws2Azure.Modules.Sqs;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.IntegrationTests.FailureConformance;

internal sealed class DeterministicFailureHarness : IDisposable
{
    public const string AccessKey = "AKIA-FAILURE-CONFORMANCE";
    public const string SecretKey = "failure-conformance-secret";
    public const string Region = "us-east-1";

    private readonly AzureHttpClient _azureHttpClient;
    private readonly RegistryHttpMessageHandler _proxyHandler;

    private DeterministicFailureHarness(
        string service,
        AzureHttpClient azureHttpClient,
        DeterministicBackendFailureHandler backend,
        ServiceModuleRegistry registry)
    {
        Service = service;
        Backend = backend;
        _azureHttpClient = azureHttpClient;
        _proxyHandler = new RegistryHttpMessageHandler(registry);
        RawClient = new HttpClient(_proxyHandler, disposeHandler: false)
        {
            BaseAddress = new Uri($"http://{service}.failure.test/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        AwsHttpClientFactory = new RegistryHttpClientFactory(_proxyHandler);
    }

    public string Service { get; }
    public DeterministicBackendFailureHandler Backend { get; }
    public HttpClient RawClient { get; }
    public HttpClientFactory AwsHttpClientFactory { get; }
    public ProxyExchangeSnapshot? LastProxyExchange => _proxyHandler.LastExchange;

    public static DeterministicFailureHarness Create(string service)
    {
        var backend = new DeterministicBackendFailureHandler();
        var azure = new AzureHttpClient(
            backend,
            ownsHandler: false,
            new AzureHttpClientOptions
            {
                MaxAttempts = 1,
                RequestTimeout = TimeSpan.FromSeconds(10),
                CircuitBreaker = { Enabled = false },
            });

        var config = CreateConfig();
        var resolver = new StaticCredentialResolver(config);
        var capabilities = new CapabilityMatrix(service, []);
        IServiceModule module = service switch
        {
            "s3" => new S3ServiceModule(azure, resolver, capabilities),
            "dynamodb" => new DynamoDbServiceModule(azure, resolver, capabilities),
            "sqs" => new SqsServiceModule(azure, resolver, capabilities),
            "secretsmanager" => new SecretsManagerServiceModule(azure, resolver, capabilities),
            _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Unsupported deterministic HTTP service."),
        };
        var sigV4 = new SigV4Validator(resolver);
        return new DeterministicFailureHarness(
            service,
            azure,
            backend,
            new ServiceModuleRegistry([module], sigV4));
    }

    public void Dispose()
    {
        RawClient.Dispose();
        _proxyHandler.Dispose();
        _azureHttpClient.Dispose();
        Backend.Dispose();
    }

    private static ProxyConfig CreateConfig()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        return new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = AccessKey,
                    AwsSecretAccessKey = SecretKey,
                    Azure = new AzureCredentials
                    {
                        Blob = new BlobCredentials
                        {
                            AccountName = "failureaccount",
                            AccountKey = key,
                        },
                        Cosmos = new CosmosCredentials
                        {
                            Endpoint = "https://failureaccount.documents.azure.com",
                            DatabaseName = "failuredb",
                            PrimaryKey = key,
                        },
                        ServiceBus = new ServiceBusCredentials
                        {
                            Namespace = "failurebus",
                            SasKeyName = "RootManageSharedAccessKey",
                            SasKey = key,
                            Transport = SqsTransport.Rest,
                        },
                        KeyVault = new KeyVaultCredentials
                        {
                            VaultUrl = "https://failurevault.vault.azure.net",
                            AuthMode = AzureAuthMode.ClientSecret,
                            TenantId = "failure-tenant",
                            ClientId = "failure-client",
                            ClientSecret = "failure-client-secret",
                        },
                    },
                },
            },
        };
    }
}

internal sealed class DeterministicBackendFailureHandler : HttpMessageHandler
{
    private FailurePlan _plan = FailurePlan.Status(HttpStatusCode.ServiceUnavailable, null);
    private int _backendRequestCount;
    private int _tokenRequestCount;
    private HttpStatusCode? _tokenFailureStatus;
    private TaskCompletionSource _requestObserved = NewSignal();
    private TaskCompletionSource _cancellationObserved = NewSignal();

    public int BackendRequestCount => Volatile.Read(ref _backendRequestCount);
    public int TokenRequestCount => Volatile.Read(ref _tokenRequestCount);
    public Task RequestObserved => _requestObserved.Task;
    public Task CancellationObserved => _cancellationObserved.Task;

    public void PlanStatus(HttpStatusCode statusCode, string? azureErrorCode = null)
    {
        _plan = FailurePlan.Status(statusCode, azureErrorCode);
        _tokenFailureStatus = null;
        ResetObservation();
    }

    public void PlanTokenFailure(HttpStatusCode statusCode)
    {
        _plan = FailurePlan.Status(HttpStatusCode.OK, null);
        _tokenFailureStatus = statusCode;
        ResetObservation();
    }

    public void PlanCancellation()
    {
        _plan = FailurePlan.Cancellation();
        _tokenFailureStatus = null;
        ResetObservation();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            Interlocked.Increment(ref _tokenRequestCount);
            var statusCode = _tokenFailureStatus ?? HttpStatusCode.OK;
            return new HttpResponseMessage(statusCode)
            {
                Content = statusCode == HttpStatusCode.OK
                    ? new StringContent(
                        """{"access_token":"deterministic-test-token","expires_in":3600}""",
                        Encoding.UTF8,
                        "application/json")
                    : new StringContent(
                        """{"error":"invalid_client"}""",
                        Encoding.UTF8,
                        "application/json"),
            };
        }

        Interlocked.Increment(ref _backendRequestCount);
        _requestObserved.TrySetResult();
        var plan = _plan;
        if (plan.WaitForCancellation)
        {
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _cancellationObserved);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
        }

        var response = new HttpResponseMessage(plan.StatusCode)
        {
            Content = new StringContent(
                """{"error":{"code":"DeterministicInjectedFailure"}}""",
                Encoding.UTF8,
                "application/json"),
        };
        if (!string.IsNullOrEmpty(plan.AzureErrorCode))
        {
            response.Headers.TryAddWithoutValidation("x-ms-error-code", plan.AzureErrorCode);
        }
        return response;
    }

    private void ResetObservation()
    {
        Interlocked.Exchange(ref _backendRequestCount, 0);
        Interlocked.Exchange(ref _tokenRequestCount, 0);
        _requestObserved = NewSignal();
        _cancellationObserved = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct FailurePlan(
        HttpStatusCode StatusCode,
        string? AzureErrorCode,
        bool WaitForCancellation)
    {
        public static FailurePlan Status(HttpStatusCode statusCode, string? azureErrorCode) =>
            new(statusCode, azureErrorCode, false);

        public static FailurePlan Cancellation() =>
            new(HttpStatusCode.ServiceUnavailable, null, true);
    }
}

internal sealed class RegistryHttpClientFactory(RegistryHttpMessageHandler handler) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
        new(handler, disposeHandler: false);

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
}

internal sealed class RegistryHttpMessageHandler(ServiceModuleRegistry registry) : HttpMessageHandler
{
    public ProxyExchangeSnapshot? LastExchange { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.RequestUri);

        var context = new DefaultHttpContext();
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Request.Method = request.Method.Method;
        context.Request.Scheme = request.RequestUri.Scheme;
        context.Request.Host = new HostString(
            request.Headers.Host ?? request.RequestUri.Authority);
        context.Request.Path = request.RequestUri.AbsolutePath;
        context.Request.QueryString = new QueryString(request.RequestUri.Query);
        context.RequestAborted = cancellationToken;

        CopyRequestHeaders(request.Headers, context);
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            context.Request.Body = new MemoryStream(bytes, writable: false);
            context.Request.ContentLength = bytes.LongLength;
            CopyRequestHeaders(request.Content.Headers, context);
        }
        else
        {
            context.Request.Body = Stream.Null;
            context.Request.ContentLength = 0;
        }

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        try
        {
            await registry.DispatchAsync(context).ConfigureAwait(false);
        }
        catch
        {
            LastExchange = Snapshot(context, responseBody);
            throw;
        }

        LastExchange = Snapshot(context, responseBody);
        var response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(responseBody.ToArray()),
        };
        foreach (var (name, values) in context.Response.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, values.ToArray()))
            {
                response.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }
        return response;
    }

    private static void CopyRequestHeaders(
        HttpHeaders headers,
        HttpContext context)
    {
        foreach (var (name, values) in headers)
        {
            context.Request.Headers[name] = values.ToArray();
        }
    }

    private static ProxyExchangeSnapshot Snapshot(
        HttpContext context,
        MemoryStream body) =>
        new(
            context.Response.StatusCode,
            context.Response.HasStarted,
            body.Length);
}

internal sealed record ProxyExchangeSnapshot(
    int StatusCode,
    bool ResponseStarted,
    long ResponseBodyLength);
