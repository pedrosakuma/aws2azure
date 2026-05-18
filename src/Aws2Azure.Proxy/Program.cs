using System.Text.Json.Serialization;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Proxy;
using Microsoft.AspNetCore.Http;

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

// Manual, reflection-free module registration. Capability matrices come from
// the generated registry (single source of truth: docs/gaps/**/*.yaml). Real
// modules will replace these stubs in their respective Phase 1+ issues.
IServiceModule[] modules =
[
    new StubServiceModule(CapabilityRegistry.S3, AwsErrorFormat.Xml, "s3."),
    new StubServiceModule(CapabilityRegistry.Sqs, AwsErrorFormat.Json, "sqs."),
    new StubServiceModule(CapabilityRegistry.Dynamodb, AwsErrorFormat.Json, "dynamodb."),
    new StubServiceModule(CapabilityRegistry.Kinesis, AwsErrorFormat.Json, "kinesis."),
    new StubServiceModule(CapabilityRegistry.Sns, AwsErrorFormat.Json, "sns."),
];

builder.Services.AddSingleton(new ServiceModuleRegistry(modules, sigV4Validator));

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Aws2Azure.Proxy");
ProxyLog.HostStarting(startupLogger, modules.Length, proxyConfig.Credentials.Count);

app.MapGet("/_aws2azure/health", () => Results.Ok(new HealthResponse("ok", modules.Length)));

app.MapGet("/_aws2azure/modules", (ServiceModuleRegistry registry) =>
    Results.Ok(registry.Modules.Select(m => m.ServiceName).ToArray()));

app.MapGet("/_aws2azure/capabilities", (ServiceModuleRegistry registry) =>
    Results.Ok(registry.Modules.Select(m => m.Capabilities).ToArray()));

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
