using System.Text.Json.Serialization;
#if USE_AMQP
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.ServiceBus;
#endif
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.Observability;
using Aws2Azure.Core.SigV4;
#if MOD_S3
using Aws2Azure.Modules.S3;
#endif
#if MOD_SQS
using Aws2Azure.Modules.Sqs;
#endif
#if MOD_DYNAMODB
using Aws2Azure.Modules.DynamoDb;
#endif
#if MOD_KINESIS
using Aws2Azure.Modules.Kinesis;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.ShardIterators;
#endif
#if MOD_SNS
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Management;
#endif
#if MOD_SECRETSMANAGER
using Aws2Azure.Modules.SecretsManager;
#endif
using Aws2Azure.Proxy;
using Microsoft.AspNetCore.Http;

// Handle --health-check flag for Docker HEALTHCHECK instruction
if (args.Contains("--health-check"))
{
    var healthUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';').FirstOrDefault() 
        ?? "http://localhost:8080";
    healthUrl = healthUrl.Replace("+", "localhost").Replace("*", "localhost");
    
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        var response = await http.GetAsync($"{healthUrl}/_aws2azure/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

// Disable HttpClient's DiagnosticsHandler activity propagation: the proxy
// does not subscribe to System.Net.Http ActivitySource/DiagnosticListener
// events anywhere, so the Activity allocation + traceparent injection on
// every outbound request is pure overhead. Profiling showed ~18% of S3
// GetObject CPU and ~12 MB / 30s of allocations attributable to this path.
// Must run before any HttpClient / SocketsHttpHandler is constructed.
AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);

// Anchor the content root to the assembly directory rather than the current
// working directory. WebApplication discovers appsettings*.json relative to
// the content root, and when the proxy is launched from a working directory
// other than its binaries — the perf harness runs `dotnet run` from the repo
// root, and some container layouts differ — cwd-relative discovery misses the
// bundled appsettings.json and the logging floor it sets. The result is that
// ASP.NET Core's per-request "Request starting"/"Request finished" Info logs
// fire and allocate a formatted String on every request (a #192 hot spot).
// Setting ContentRootPath here keeps the standard configuration precedence
// (JSON < environment variables < command line) intact, unlike re-adding the
// JSON providers after the builder defaults.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ProxyJsonContext.Default);
});

var configFile = Environment.GetEnvironmentVariable("AWS2AZURE_CONFIG_FILE")
    ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");

ProxyConfig proxyConfig;
try
{
    proxyConfig = ProxyConfigLoader.Load(configFile);
    ProxyConfigValidator.Validate(proxyConfig);
}
catch (ProxyConfigException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

builder.Services.AddSingleton(proxyConfig);
var credentialResolver = new StaticCredentialResolver(proxyConfig);
builder.Services.AddSingleton<ICredentialResolver>(credentialResolver);

var sigV4Validator = new SigV4Validator(
    credentialResolver,
    presignedTrustedSigningHosts: proxyConfig.S3.PresignedTrustedSigningHosts);
builder.Services.AddSingleton(sigV4Validator);

// Observability: ProxyMetrics collects counters/histograms, PrometheusExporter
// exposes them in Prometheus text format at /_aws2azure/metrics.
builder.Services.AddSingleton<ProxyMetrics>();
builder.Services.AddSingleton<PrometheusExporter>();

// Single shared Azure HTTP client per process — pooled SocketsHttpHandler
// inside. Disposed on host shutdown via the DI container.
//
// Test-only knob: AWS2AZURE_INSECURE_TLS=1 disables remote-cert validation on
// outbound Azure calls. Required by the Cosmos DB Linux emulator (vNext
// preview), which serves its 8081 endpoint with a self-signed certificate.
// MUST stay off in production — the value is read once at host start and
// logged at warning level so accidental enablement is obvious.
var azureHttpClient = BuildAzureHttpClient();
builder.Services.AddSingleton(azureHttpClient);

#if USE_ENTRAID
builder.Services.AddSingleton(new EntraIdTokenProvider(azureHttpClient));
#endif
#if USE_EVENTHUBS
builder.Services.AddSingleton<IEventHubsAuthenticator>(sp =>
    new EventHubsAuthenticator(sp.GetRequiredService<EntraIdTokenProvider>()));
builder.Services.AddSingleton<IEventHubsManagementClient>(sp =>
    new EventHubsManagementClient(
        azureHttpClient,
        sp.GetRequiredService<IEventHubsAuthenticator>(),
        sp.GetRequiredService<ILogger<EventHubsManagementClient>>()));
builder.Services.AddSingleton<IEventHubMetadataCache>(sp =>
    new EventHubMetadataCache(sp.GetRequiredService<IEventHubsManagementClient>()));
builder.Services.AddSingleton<ListShardsCursorCodecFactory>(sp =>
    new ListShardsCursorCodecFactory(sp.GetRequiredService<ILogger<ListShardsCursorCodecFactory>>()));
builder.Services.AddSingleton<ShardIteratorTokenCodecFactory>(sp =>
    new ShardIteratorTokenCodecFactory(sp.GetRequiredService<ILogger<ShardIteratorTokenCodecFactory>>()));
#endif
#if USE_SBTOPICS
builder.Services.AddSingleton<IServiceBusTopicsAuthenticator>(sp =>
    new ServiceBusTopicsAuthenticator(sp.GetRequiredService<EntraIdTokenProvider>()));
builder.Services.AddSingleton<IServiceBusTopicsManagementClient>(sp =>
    new ServiceBusTopicsManagementClient(
        azureHttpClient,
        sp.GetRequiredService<IServiceBusTopicsAuthenticator>(),
        sp.GetRequiredService<ILogger<ServiceBusTopicsManagementClient>>()));
#endif

static AzureHttpClient BuildAzureHttpClient()
{
    if (string.Equals(
            Environment.GetEnvironmentVariable("AWS2AZURE_INSECURE_TLS"),
            "1",
            StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "WARNING: AWS2AZURE_INSECURE_TLS=1 — outbound TLS validation is DISABLED. " +
            "This is for local emulator testing only; do not use in production.");
        var insecure = new System.Net.Http.SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = AzureHttpClient.ResolveMaxConnectionsPerServer(),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            EnableMultipleHttp2Connections = true,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        return new AzureHttpClient(insecure, ownsHandler: true);
    }
    return new AzureHttpClient();
}

#if USE_AMQP
// Resolves the FIFO session-receiver idle-TTL for the AMQP pool (#262).
// AWS2AZURE_SB_SESSION_IDLE_SECONDS overrides the default; a value <= 0
// disables the background sweeper (session receivers then live until
// link failure or shutdown, the pre-#262 behaviour).
static TimeSpan? ResolveSessionReceiverIdleTimeout()
{
    var raw = Environment.GetEnvironmentVariable("AWS2AZURE_SB_SESSION_IDLE_SECONDS");
    if (string.IsNullOrWhiteSpace(raw))
        return ServiceBusAmqpPool.DefaultSessionReceiverIdleTimeout;
    if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds))
    {
        Console.Error.WriteLine(
            $"WARNING: AWS2AZURE_SB_SESSION_IDLE_SECONDS='{raw}' is not an integer; " +
            "using the default FIFO session-receiver idle timeout.");
        return ServiceBusAmqpPool.DefaultSessionReceiverIdleTimeout;
    }
    return seconds <= 0 ? null : TimeSpan.FromSeconds(seconds);
}

// Service Bus AMQP pool — shared across all credential entries. Lazy:
// connections / receivers materialise on first AMQP queue use, so a
// REST-only deployment pays zero cost. The pool is registered as a
// singleton so the host's DI lifecycle (DisposeAsync on shutdown)
// tears down every cached connection cleanly.
//
// ContainerId is the AMQP "client identity"; SB uses it for diagnostics
// and to disambiguate clients sharing a SAS key. We tag it with the
// process so a fleet of proxies under the same key is distinguishable
// in the broker's audit logs.
var amqpConnectionSettings = new AmqpConnectionSettings
{
    ContainerId = $"aws2azure-{Environment.MachineName}-{Environment.ProcessId}",
};
var amqpFactory = new ServiceBusAmqpConnectionFactory(amqpConnectionSettings);
// Idle-TTL eviction of FIFO session-receiver links: FIFO requests trigger
// an opportunistic sweep (no timer/background thread), returning idle broker
// sessions for another consumer and freeing AMQP links. The pool also has a
// hard per-connection session-link cap. AWS2AZURE_SB_SESSION_IDLE_SECONDS <= 0
// disables idle eviction while retaining the hard cap.
var sessionIdleTimeout = ResolveSessionReceiverIdleTimeout();
var amqpPool = new ServiceBusAmqpPool(amqpFactory, sessionIdleTimeout);
builder.Services.AddSingleton(amqpPool);
#if USE_EVENTHUBS
builder.Services.AddSingleton(sp =>
    new EventHubsAmqpConnectionPool(
        sp.GetRequiredService<EntraIdTokenProvider>(),
        amqpConnectionSettings));
builder.Services.AddSingleton<IEventHubsAmqpSender>(sp =>
    new EventHubsAmqpSender(sp.GetRequiredService<EventHubsAmqpConnectionPool>()));
builder.Services.AddSingleton<IEventHubsAmqpReceiver>(sp =>
    new EventHubsAmqpReceiver(sp.GetRequiredService<EventHubsAmqpConnectionPool>()));
#endif
#if MOD_SNS
builder.Services.AddSingleton<ISnsAmqpSender>(sp =>
    new SnsAmqpSender(
        sp.GetRequiredService<EntraIdTokenProvider>(),
        amqpConnectionSettings));
#endif
#endif // USE_AMQP
#if USE_EVENTGRID
builder.Services.AddSingleton<IEventGridPublisher>(sp =>
    new EventGridPublisher(
        azureHttpClient,
        sp.GetRequiredService<EntraIdTokenProvider>(),
        sp.GetRequiredService<ILogger<EventGridPublisher>>()));
#endif // USE_EVENTGRID

// Manual, reflection-free module registration. Capability matrices come from
// the generated registry (single source of truth: docs/gaps/**/*.yaml). The
// S3 module is real as of Phase-1 slice 1 (bucket lifecycle); the rest stay
// stubbed until their respective phases.
//
// Which modules are compiled in is fixed at build time by the `Modules`
// MSBuild property (#273); the #if guards below drop the unselected modules'
// construction so the AOT trimmer removes their code entirely.
//
// Registered via factory so modules that need the host's ILoggerFactory
// (e.g. DynamoDB Scan cost-warning telemetry) can resolve it from DI
// without us duplicating a separate factory.
builder.Services.AddSingleton<ServiceModuleRegistry>(sp =>
{
#if MOD_DYNAMODB || MOD_KINESIS || MOD_SNS || MOD_SECRETSMANAGER
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var tokenProvider = sp.GetRequiredService<EntraIdTokenProvider>();
#endif
    var modules = new List<IServiceModule>();
#if MOD_S3
    modules.Add(new S3ServiceModule(azureHttpClient, credentialResolver, CapabilityRegistry.S3));
#endif
#if MOD_SQS
    modules.Add(new SqsServiceModule(azureHttpClient, credentialResolver, CapabilityRegistry.Sqs, amqpPool));
#endif
#if MOD_DYNAMODB
    modules.Add(new DynamoDbServiceModule(azureHttpClient, credentialResolver, CapabilityRegistry.DynamoDb,
        settings: proxyConfig.DynamoDb,
        loggerFactory: loggerFactory,
        tokenProvider: tokenProvider));
#endif
#if MOD_KINESIS
    modules.Add(new KinesisServiceModule(
        credentialResolver,
        sp.GetRequiredService<IEventHubsManagementClient>(),
        sp.GetRequiredService<IEventHubMetadataCache>(),
        sp.GetRequiredService<IEventHubsAmqpSender>(),
        sp.GetRequiredService<IEventHubsAmqpReceiver>(),
        sp.GetRequiredService<ListShardsCursorCodecFactory>(),
        sp.GetRequiredService<ShardIteratorTokenCodecFactory>(),
        CapabilityRegistry.Kinesis));
#endif
#if MOD_SNS
    modules.Add(new SnsServiceModule(
        credentialResolver,
        proxyConfig.Sns,
        sp.GetRequiredService<IServiceBusTopicsManagementClient>(),
        sp.GetRequiredService<ISnsAmqpSender>(),
        sp.GetRequiredService<IEventGridPublisher>(),
        sp.GetRequiredService<ILogger<SnsServiceModule>>(),
        CapabilityRegistry.Sns));
#endif
#if MOD_SECRETSMANAGER
    modules.Add(new SecretsManagerServiceModule(
        azureHttpClient,
        credentialResolver,
        CapabilityRegistry.SecretsManager,
        tokenProvider));
#endif
    return new ServiceModuleRegistry(modules.ToArray(), sigV4Validator, sp.GetRequiredService<ProxyMetrics>());
});

var app = builder.Build();

var registry = app.Services.GetRequiredService<ServiceModuleRegistry>();
var prometheusExporter = app.Services.GetRequiredService<PrometheusExporter>();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Aws2Azure.Proxy");
ProxyLog.HostStarting(startupLogger, registry.Modules.Count, proxyConfig.Credentials.Count);

#if MOD_DYNAMODB
// #204: startup probe of each Cosmos account's default consistency level
// (opt-in via DynamoDb.ConsistencyCheck). Under Required, an account that
// cannot honor ConsistentRead fails startup; under Warn it only logs.
if (proxyConfig.DynamoDb.ConsistencyCheck != ConsistencyCheckMode.Disabled)
{
    var dynamoModule = registry.Modules.OfType<DynamoDbServiceModule>().FirstOrDefault();
    if (dynamoModule is not null)
    {
        var cosmosAccounts = proxyConfig.Credentials
            .Select(c => c.Azure.Cosmos)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
        try
        {
            await dynamoModule.ValidateAccountConsistencyAsync(cosmosAccounts, CancellationToken.None);
        }
        catch (CosmosConsistencyValidationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
#endif

// Kubernetes-style health probes (standard paths)
// Only respond if Host is not an AWS service (avoids intercepting proxied requests)
app.MapGet("/health", (HttpContext ctx) =>
{
    var host = ctx.Request.Host.Host;
    if (registry.Modules.Any(m => m.MatchesHost(host)))
    {
        // This is an AWS service request, not a probe - let dispatcher handle it
        return Results.StatusCode(404);
    }
    return Results.Json(new LivenessResponse("healthy"), ProxyJsonContext.Default.LivenessResponse);
});

app.MapGet("/ready", (HttpContext ctx) =>
{
    var host = ctx.Request.Host.Host;
    if (registry.Modules.Any(m => m.MatchesHost(host)))
    {
        // This is an AWS service request, not a probe - let dispatcher handle it
        return Results.StatusCode(404);
    }
    
    var services = new Dictionary<string, ServiceReadiness>();
    foreach (var module in registry.Modules)
    {
        var enabled = proxyConfig.Services.TryGetValue(module.ServiceName, out var svc) && svc.Enabled;
        services[module.ServiceName] = new ServiceReadiness(enabled, enabled);
    }
    
    var hasCredentials = proxyConfig.Credentials.Count > 0;
    var allEnabled = services.Values.Any(s => s.Enabled);
    var isReady = hasCredentials && allEnabled;
    
    var response = new ReadinessResponse(
        isReady ? "ready" : "not_ready",
        services,
        hasCredentials,
        registry.Modules.Count);
    
    // AOT-compatible: use explicit JsonTypeInfo for consistent casing
    return Results.Json(response, ProxyJsonContext.Default.ReadinessResponse, 
        statusCode: isReady ? 200 : 503);
});

// Legacy internal endpoints (keep for backward compatibility)
app.MapGet("/_aws2azure/health", () => Results.Ok(new HealthResponse("ok", registry.Modules.Count)));

app.MapGet("/_aws2azure/modules", (ServiceModuleRegistry registry) =>
    Results.Ok(registry.Modules.Select(m => m.ServiceName).ToArray()));

app.MapGet("/_aws2azure/capabilities", (ServiceModuleRegistry registry) =>
    Results.Ok(registry.Modules.Select(m => m.Capabilities).ToArray()));

// Prometheus metrics endpoint
app.MapGet("/_aws2azure/metrics", (PrometheusExporter exporter) =>
{
    var content = exporter.Export();
    return Results.Text(content, "text/plain; version=0.0.4; charset=utf-8");
});

// Catch-all dispatcher: every other request is routed by Host header.
app.Map("/{**catchAll}", (HttpContext ctx, ServiceModuleRegistry registry) =>
    registry.DispatchAsync(ctx));

app.Run();
return 0;

namespace Aws2Azure.Proxy
{
    // Health check responses
    internal sealed record LivenessResponse(string Status);
    internal sealed record ServiceReadiness(bool Enabled, bool Ready);
    internal sealed record ReadinessResponse(
        string Status,
        Dictionary<string, ServiceReadiness> Services,
        bool HasCredentials,
        int ModuleCount);
    internal sealed record HealthResponse(string Status, int Modules);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(LivenessResponse))]
    [JsonSerializable(typeof(ReadinessResponse))]
    [JsonSerializable(typeof(ServiceReadiness))]
    [JsonSerializable(typeof(Dictionary<string, ServiceReadiness>))]
    [JsonSerializable(typeof(HealthResponse))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(CapabilityMatrix[]))]
    internal sealed partial class ProxyJsonContext : JsonSerializerContext
    {
    }

    internal static partial class ProxyLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "aws2azure host starting with {ModuleCount} service module(s) and {CredentialCount} credential entry/entries")]
        public static partial void HostStarting(ILogger logger, int moduleCount, int credentialCount);
    }
}

// Exposed so tests can use Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>.
public partial class Program;
