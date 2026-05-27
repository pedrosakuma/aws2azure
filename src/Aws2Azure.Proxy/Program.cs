using System.Text.Json.Serialization;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.Observability;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.DynamoDb;
using Aws2Azure.Modules.Kinesis;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Proxy;
using Microsoft.AspNetCore.Http;

// Disable HttpClient's DiagnosticsHandler activity propagation: the proxy
// does not subscribe to System.Net.Http ActivitySource/DiagnosticListener
// events anywhere, so the Activity allocation + traceparent injection on
// every outbound request is pure overhead. Profiling showed ~18% of S3
// GetObject CPU and ~12 MB / 30s of allocations attributable to this path.
// Must run before any HttpClient / SocketsHttpHandler is constructed.
AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);

var builder = WebApplication.CreateSlimBuilder(args);

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

var sigV4Validator = new SigV4Validator(credentialResolver);
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

builder.Services.AddSingleton(new EntraIdTokenProvider(azureHttpClient));
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
builder.Services.AddSingleton<IServiceBusTopicsAuthenticator>(sp =>
    new ServiceBusTopicsAuthenticator(sp.GetRequiredService<EntraIdTokenProvider>()));
builder.Services.AddSingleton<IServiceBusTopicsManagementClient>(sp =>
    new ServiceBusTopicsManagementClient(
        azureHttpClient,
        sp.GetRequiredService<IServiceBusTopicsAuthenticator>(),
        sp.GetRequiredService<ILogger<ServiceBusTopicsManagementClient>>()));

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
            MaxConnectionsPerServer = 64,
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
var amqpPool = new ServiceBusAmqpPool(amqpFactory);
builder.Services.AddSingleton(amqpPool);
builder.Services.AddSingleton<IEventHubsAmqpSender>(sp =>
    new EventHubsAmqpSender(
        sp.GetRequiredService<EntraIdTokenProvider>(),
        amqpConnectionSettings));
builder.Services.AddSingleton<IEventHubsAmqpReceiver>(sp =>
    new EventHubsAmqpReceiver(
        sp.GetRequiredService<EntraIdTokenProvider>(),
        amqpConnectionSettings));
builder.Services.AddSingleton<ISnsAmqpSender>(sp =>
    new SnsAmqpSender(
        sp.GetRequiredService<EntraIdTokenProvider>(),
        amqpConnectionSettings));
builder.Services.AddSingleton<IEventGridPublisher>(sp =>
    new EventGridPublisher(
        azureHttpClient,
        sp.GetRequiredService<EntraIdTokenProvider>(),
        sp.GetRequiredService<ILogger<EventGridPublisher>>()));

// Manual, reflection-free module registration. Capability matrices come from
// the generated registry (single source of truth: docs/gaps/**/*.yaml). The
// S3 module is real as of Phase-1 slice 1 (bucket lifecycle); the rest stay
// stubbed until their respective phases.
//
// Registered via factory so modules that need the host's ILoggerFactory
// (e.g. DynamoDB Scan cost-warning telemetry) can resolve it from DI
// without us duplicating a separate factory.
builder.Services.AddSingleton<ServiceModuleRegistry>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var tokenProvider = sp.GetRequiredService<EntraIdTokenProvider>();
    IServiceModule[] modules =
    [
        new S3ServiceModule(azureHttpClient, credentialResolver, CapabilityRegistry.S3),
        new SqsServiceModule(azureHttpClient, credentialResolver, CapabilityRegistry.Sqs, amqpPool),
        new DynamoDbServiceModule(azureHttpClient, credentialResolver, CapabilityRegistry.Dynamodb,
            settings: proxyConfig.DynamoDb,
            loggerFactory: loggerFactory,
            tokenProvider: tokenProvider),
        new KinesisServiceModule(
            credentialResolver,
            sp.GetRequiredService<IEventHubsManagementClient>(),
            sp.GetRequiredService<IEventHubMetadataCache>(),
            sp.GetRequiredService<IEventHubsAmqpSender>(),
            sp.GetRequiredService<IEventHubsAmqpReceiver>(),
            sp.GetRequiredService<ListShardsCursorCodecFactory>(),
            sp.GetRequiredService<ShardIteratorTokenCodecFactory>(),
            CapabilityRegistry.Kinesis),
        new SnsServiceModule(
            credentialResolver,
            proxyConfig.Sns,
            sp.GetRequiredService<IServiceBusTopicsManagementClient>(),
            sp.GetRequiredService<ISnsAmqpSender>(),
            sp.GetRequiredService<IEventGridPublisher>(),
            sp.GetRequiredService<ILogger<SnsServiceModule>>(),
            CapabilityRegistry.Sns),
    ];
    return new ServiceModuleRegistry(modules, sigV4Validator, sp.GetRequiredService<ProxyMetrics>());
});

var app = builder.Build();

var registry = app.Services.GetRequiredService<ServiceModuleRegistry>();
var prometheusExporter = app.Services.GetRequiredService<PrometheusExporter>();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Aws2Azure.Proxy");
ProxyLog.HostStarting(startupLogger, registry.Modules.Count, proxyConfig.Credentials.Count);

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
    internal sealed record HealthResponse(string Status, int Modules);

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
