using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aws2Azure.Conformance.SecretsManager;

/// <summary>
/// Boots the proxy in-process (WebApplicationFactory) with the Secrets Manager
/// module enabled and a dummy Key Vault client-secret credential. The Secrets
/// Manager error matrix only exercises rejections that fire in the SigV4 stage
/// or the wire-protocol parser (unknown target, malformed body, …) — all
/// <em>before</em> any Key Vault call — so no Key Vault emulator is needed and
/// this fixture is fully offline and runs on every PR.
/// </summary>
public sealed class SecretsManagerConformanceFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configFile;
    private readonly string? _previousConfigFile;

    public const string AccessKeyId = "AKIACONFORMANCE0001";
    public const string Secret = "conformanceSecretKey0123456789abcdefABCDEF";

    public HttpClient Client { get; }

    public SecretsManagerConformanceFixture()
    {
        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-conformance-secretsmanager-" + Guid.NewGuid().ToString("N") + ".json");
        var config = $$"""
        {
          "services": { "secretsmanager": { "enabled": true } },
          "bindings": [
            {
              "aws": {
                "accessKeyId": "{{AccessKeyId}}",
                "secretAccessKey": "{{Secret}}"
              },
              "azure": {
                "secretsmanager": {
                  "kind": "keyVault",
                  "target": {
                    "vaultUrl": "https://conformancedummy.vault.azure.net"
                  },
                  "auth": {
                    "mode": "clientSecret",
                    "tenantId": "00000000-0000-0000-0000-000000000000",
                    "clientId": "00000000-0000-0000-0000-000000000001",
                    "clientSecret": "conformance-dummy-client-secret"
                  }
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
            BaseAddress = new Uri("http://secretsmanager.us-east-1.amazonaws.com/"),
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
