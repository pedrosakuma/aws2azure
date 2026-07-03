using System;
using System.IO;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// DynamoDB integration fixture with stored procedures enabled (Preferred mode).
/// Uses Cosmos DB Linux emulator + in-process proxy.
/// </summary>
public sealed class DynamoDbSprocFixture : IAsyncLifetime
{
    private const string ContainerImage =
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

    private IContainer? _container;
    private WebApplicationFactory<Program>? _factory;
    private string? _configFile;

    public bool DockerAvailable { get; private set; }
    public string CosmosEndpoint { get; private set; } = string.Empty;
    public HttpClient Client { get; private set; } = default!;

    public const string EmulatorMasterKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public string AccessKeyId => "AKIA-IT-SPROC";
    public string Secret { get; } = "sproc-test-" + Guid.NewGuid().ToString("N");
    public string CosmosKey { get; private set; } = EmulatorMasterKey;
    public string DatabaseName => "aws2azure-sproc";

    // One-off real-Azure validation hook (see repo emulator caveat): when
    // AWS2AZURE_REAL_COSMOS_ENDPOINT / _KEY are set, the fixture targets a real
    // Cosmos DB account instead of the emulator container, so the server-side
    // stored-procedure transaction (which the emulator cannot run) is exercised.
    private static string? RealEndpoint =>
        Environment.GetEnvironmentVariable("AWS2AZURE_REAL_COSMOS_ENDPOINT");
    private static string? RealKey =>
        Environment.GetEnvironmentVariable("AWS2AZURE_REAL_COSMOS_KEY");
    private static bool RealCosmosEnabled =>
        !string.IsNullOrWhiteSpace(RealEndpoint) && !string.IsNullOrWhiteSpace(RealKey);

    /// <summary>
    /// True when the fixture is targeting a real Azure Cosmos DB account
    /// (AWS2AZURE_REAL_COSMOS_ENDPOINT / _KEY set) rather than the emulator.
    /// Tests that depend on behavior the emulator cannot reproduce — server-side
    /// scripts, 2 MB page / x-ms-continuation pagination, RU throttling — gate on
    /// this so they skip cleanly on the emulator-backed CI run.
    /// </summary>
    public bool IsRealCosmos => RealCosmosEnabled;

    public async Task InitializeAsync()
    {
        if (RealCosmosEnabled)
        {
            var ep = RealEndpoint!.Trim();
            CosmosEndpoint = ep.EndsWith('/') ? ep : ep + "/";
            CosmosKey = RealKey!.Trim();
            DockerAvailable = true;
            await StartProxyAsync();
            return;
        }

        try
        {
            _container = new ContainerBuilder()
                .WithImage(ContainerImage)
                .WithName("aws2azure-sproc-it-" + Guid.NewGuid().ToString("N")[..8])
                .WithPortBinding(8081, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilMessageIsLogged("System is now fully ready to accept requests"))
                .Build();

            await _container.StartAsync();
            var port = _container.GetMappedPublicPort(8081);
            CosmosEndpoint = $"http://{_container.Hostname}:{port}/";
            DockerAvailable = true;
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is InvalidOperationException ||
            ex.GetType().FullName?.StartsWith("Docker", StringComparison.Ordinal) == true ||
            ex.Message.Contains("docker", StringComparison.OrdinalIgnoreCase))
        {
            DockerAvailable = false;
            return;
        }

        await StartProxyAsync();
    }

    private async Task StartProxyAsync()
    {
        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-sproc-it-" + Guid.NewGuid().ToString("N") + ".json");
        
        // Config with useStoredProcedures: Preferred
        var config = $$"""
        {
          "services": {
            "s3":       { "enabled": false },
            "sqs":      { "enabled": false },
            "dynamodb": {
              "enabled": true,
              "useStoredProcedures": "Preferred"
            }
          },
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
                    "endpoint":     "{{CosmosEndpoint}}",
                    "databaseName": "{{DatabaseName}}"
                  },
                  "auth": {
                    "mode": "sharedKey",
                    "key":  "{{CosmosKey}}"
                  }
                }
              }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(_configFile, config);

        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _configFile);

        // In real-Azure mode the database is pre-provisioned out of band (az CLI);
        // the test-only REST bootstrap only targets the emulator. The proxy
        // creates the per-test containers itself via its production auth path.
        if (!RealCosmosEnabled)
        {
            using var bootstrap = new HttpClient();
            await CosmosRestBootstrap.EnsureDatabaseAsync(
                bootstrap, CosmosEndpoint, CosmosKey, DatabaseName);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://dynamodb.us-east-1.amazonaws.com/"),
        });
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { /* best-effort */ }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DynamoDbSprocCollection : ICollectionFixture<DynamoDbSprocFixture>
{
    public const string Name = "dynamodb-sproc";
}
