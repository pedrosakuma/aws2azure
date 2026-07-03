using System.IO;
using Aws2Azure.Conformance.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aws2Azure.Conformance.DynamoDb;

/// <summary>
/// Boots the proxy in-process (WebApplicationFactory) with the DynamoDB module
/// enabled and a dummy Cosmos DB credential. The DynamoDB error matrix only
/// exercises rejections that fire in the SigV4 stage or the wire-protocol
/// <em>parser</em> (unknown operation, malformed body, …) — all
/// <em>before</em> any Cosmos call — so no Cosmos emulator is needed and this
/// fixture is fully offline and runs on every PR.
/// </summary>
public sealed class DynamoDbConformanceFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configFile;
    private readonly string? _previousConfigFile;

    public const string AccessKeyId = "AKIACONFORMANCE0001";
    public const string Secret = "conformanceSecretKey0123456789abcdefABCDEF";

    public HttpClient Client { get; }

    public DynamoDbConformanceFixture()
    {
        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-conformance-ddb-" + Guid.NewGuid().ToString("N") + ".json");
        var config = $$"""
        {
          "services": { "dynamodb": { "enabled": true } },
          "bindings": [
            {
              "aws": {
                "accessKeyId": "{{AccessKeyId}}",
                "secretAccessKey": "{{Secret}}"
              },
              "azure": {
                "dynamodb": {
                  "kind": "cosmos",
                  "target": {
                    "endpoint":     "https://conformancedummy.documents.azure.com:443/",
                    "databaseName": "conformance"
                  },
                  "auth": {
                    "mode": "sharedKey",
                    "key":  "ZHVtbXlrZXlmb3Jjb25mb3JtYW5jZXRlc3Rpbmc="
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
            BaseAddress = new Uri("http://dynamodb.us-east-1.amazonaws.com/"),
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
