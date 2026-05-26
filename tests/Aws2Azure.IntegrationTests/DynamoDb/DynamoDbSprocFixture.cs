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
    public string CosmosKey => EmulatorMasterKey;
    public string DatabaseName => "aws2azure-sproc";

    public async Task InitializeAsync()
    {
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

        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-sproc-it-" + Guid.NewGuid().ToString("N") + ".json");
        
        // Config with storedProcedures: Preferred
        var config = $$"""
        {
          "services": {
            "s3":       { "enabled": false },
            "sqs":      { "enabled": false },
            "dynamodb": { "enabled": true }
          },
          "dynamoDb": {
            "storedProcedures": "Preferred"
          },
          "credentials": [
            {
              "awsAccessKeyId": "{{AccessKeyId}}",
              "awsSecretAccessKey": "{{Secret}}",
              "azure": {
                "cosmos": {
                  "endpoint":     "{{CosmosEndpoint}}",
                  "primaryKey":   "{{CosmosKey}}",
                  "databaseName": "{{DatabaseName}}"
                }
              }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(_configFile, config);

        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _configFile);

        using (var bootstrap = new HttpClient())
        {
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

[CollectionDefinition(Name)]
public sealed class DynamoDbSprocCollection : ICollectionFixture<DynamoDbSprocFixture>
{
    public const string Name = "dynamodb-sproc";
}
