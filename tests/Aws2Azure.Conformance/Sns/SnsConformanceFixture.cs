using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aws2Azure.Conformance.Sns;

/// <summary>
/// Boots the proxy in-process (WebApplicationFactory) with the SNS module
/// enabled and a dummy Service Bus Topics credential. The SNS error matrix only
/// exercises rejections that fire in the SigV4 stage or the wire-protocol
/// <em>parser</em> (invalid action) — all <em>before</em> any Service Bus / Event
/// Grid call — so no emulator is needed and this fixture is fully offline and runs
/// on every PR.
/// </summary>
public sealed class SnsConformanceFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configFile;
    private readonly string? _previousConfigFile;

    public const string AccessKeyId = "AKIACONFORMANCE0001";
    public const string Secret = "conformanceSecretKey0123456789abcdefABCDEF";

    public HttpClient Client { get; }

    public SnsConformanceFixture()
    {
        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-conformance-sns-" + Guid.NewGuid().ToString("N") + ".json");
        var config = $$"""
        {
          "services": { "sns": { "enabled": true } },
          "credentials": [
            {
              "awsAccessKeyId": "{{AccessKeyId}}",
              "awsSecretAccessKey": "{{Secret}}",
              "azure": {
                "serviceBusTopics": {
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
            BaseAddress = new Uri("http://sns.us-east-1.amazonaws.com/"),
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
