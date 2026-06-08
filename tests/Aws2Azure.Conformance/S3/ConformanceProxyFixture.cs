using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Boots the proxy in-process (WebApplicationFactory) with a dummy Blob
/// credential. The auth/validation error matrix short-circuits in the SigV4
/// stage <em>before</em> any Azure call, so no Azurite/LocalStack container is
/// needed — this fixture is fully offline and runs on every PR.
/// </summary>
public sealed class ConformanceProxyFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configFile;
    private readonly string? _previousConfigFile;

    public const string AccessKeyId = "AKIACONFORMANCE0001";
    public const string Secret = "conformanceSecretKey0123456789abcdefABCDEF";

    public HttpClient Client { get; }

    public ConformanceProxyFixture()
    {
        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-conformance-" + Guid.NewGuid().ToString("N") + ".json");
        var config = $$"""
        {
          "services": { "s3": { "enabled": true } },
          "credentials": [
            {
              "awsAccessKeyId": "{{AccessKeyId}}",
              "awsSecretAccessKey": "{{Secret}}",
              "azure": {
                "blob": {
                  "accountName": "conformancedummy",
                  "accountKey":  "ZHVtbXlrZXlmb3Jjb25mb3JtYW5jZXRlc3Rpbmc="
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
            BaseAddress = new Uri("http://s3.us-east-1.amazonaws.com/"),
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
