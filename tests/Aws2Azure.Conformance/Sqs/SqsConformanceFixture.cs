using System.IO;
using Aws2Azure.Conformance.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aws2Azure.Conformance.Sqs;

/// <summary>
/// Boots the proxy in-process (WebApplicationFactory) with the SQS module
/// enabled and a dummy Service Bus credential. The SQS error matrix only
/// exercises rejections that fire in the SigV4 stage or the wire-protocol
/// <em>parser</em> (invalid action, …) — all <em>before</em> any Service Bus
/// call — so no Service Bus emulator is needed and this fixture is fully offline
/// and runs on every PR.
/// </summary>
public sealed class SqsConformanceFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configFile;
    private readonly string? _previousConfigFile;

    public const string AccessKeyId = "AKIACONFORMANCE0001";
    public const string Secret = "conformanceSecretKey0123456789abcdefABCDEF";

    public HttpClient Client { get; }

    public SqsConformanceFixture()
    {
        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-conformance-sqs-" + Guid.NewGuid().ToString("N") + ".json");
        var config = $$"""
        {
          "services": { "sqs": { "enabled": true } },
          "credentials": [
            {
              "awsAccessKeyId": "{{AccessKeyId}}",
              "awsSecretAccessKey": "{{Secret}}",
              "azure": {
                "serviceBus": {
                  "namespace":  "conformancedummy",
                  "sasKeyName": "RootManageSharedAccessKey",
                  "sasKey":     "ZHVtbXlrZXlmb3Jjb25mb3JtYW5jZXRlc3Rpbmc="
                }
              }
            }
          ]
        }
        """;
        File.WriteAllText(_configFile, config);
        _previousConfigFile = Environment.GetEnvironmentVariable("AWS2AZURE_CONFIG_FILE");
        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _configFile);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://sqs.us-east-1.amazonaws.com/"),
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Client.Dispose();
        try { File.Delete(_configFile); } catch { /* best-effort */ }
        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _previousConfigFile);
    }
}
