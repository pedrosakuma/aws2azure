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
/// Boots Cosmos DB Linux emulator (vNext preview) + the proxy
/// (in-process WebApplicationFactory) configured with CosmosCredentials
/// pointing at the running container. Owns its own emulator + proxy so the
/// suite is self-contained.
///
/// Test scope: Phase-3 smoke for Slices 1, 2, 5 and 6 (table lifecycle,
/// item basics, query, scan). Batch and transactional ops stay out of this
/// fixture until Phase-3 retro.
/// </summary>
public sealed class DynamoDbIntegrationFixture : IAsyncLifetime
{
    private const string ContainerImage =
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

    private IContainer? _container;
    private WebApplicationFactory<Program>? _factory;
    private string? _configFile;

    public bool DockerAvailable { get; private set; }
    public string CosmosEndpoint { get; private set; } = string.Empty;
    public HttpClient Client { get; private set; } = default!;

    public string AccessKeyId => "AKIA-IT-DDB";
    public string Secret { get; } = "test-secret-" + Guid.NewGuid().ToString("N");
    public string CosmosKey => Fixtures.CosmosEmulatorFixture.EmulatorMasterKey;
    public string DatabaseName => "aws2azure-it";

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage(ContainerImage)
                .WithName("aws2azure-ddb-it-" + Guid.NewGuid().ToString("N")[..8])
                .WithPortBinding(8081, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilMessageIsLogged("System is now fully ready to accept requests"))
                .Build();

            await _container.StartAsync();
            var port = _container.GetMappedPublicPort(8081);
            CosmosEndpoint = $"http://{_container.Hostname}:{port}/";
            DockerAvailable = true;
        }
        catch
        {
            DockerAvailable = false;
            return;
        }

        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-ddb-it-" + Guid.NewGuid().ToString("N") + ".json");
        var config = $$"""
        {
          "services": {
            "s3":       { "enabled": false },
            "sqs":      { "enabled": false },
            "dynamodb": { "enabled": true }
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

        // The proxy expects the Cosmos database to exist; bootstrap it directly
        // via the Cosmos REST API before any test runs.
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
public sealed class DynamoDbIntegrationCollection : ICollectionFixture<DynamoDbIntegrationFixture>
{
    public const string Name = "dynamodb-integration";
}
