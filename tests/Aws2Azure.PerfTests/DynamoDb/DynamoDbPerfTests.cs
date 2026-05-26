using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.DynamoDb;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class DynamoDbPerfFixture : IAsyncLifetime
{
    private const string ContainerImage =
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
    private const string EmulatorMasterKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private IContainer? _container;
    private readonly PerfProxyProcess _proxy = new();

    public bool Ready { get; private set; }
    public string? SkipReason { get; private set; }
    public string ServiceUrl => _proxy.ServiceUrlForHost("dynamodb");
    public string ProxyOutput => _proxy.Output;
    public string TableName { get; } = "perftbl" + Guid.NewGuid().ToString("N")[..8];
    public string AccessKeyId => "AKIA-PERF-DDB";
    public string Secret => "perf-ddb-secret";
    public string DatabaseName => "aws2azure-perf";

    /// <summary>Cosmos DB emulator endpoint exposed for SDK-baseline scenarios.</summary>
    public string CosmosEndpoint { get; private set; } = string.Empty;
    /// <summary>Well-known Cosmos DB emulator master key (public).</summary>
    public string CosmosMasterKey => EmulatorMasterKey;

    public AmazonDynamoDBClient CreateClient() => new(
        AccessKeyId,
        Secret,
        new AmazonDynamoDBConfig
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = "us-east-1",
            UseHttp = true,
        });

    public async Task InitializeAsync()
    {
        if (!PerfGate.Enabled)
        {
            SkipReason = "AWS2AZURE_PERF=1 not set.";
            return;
        }

        try
        {
            _container = new ContainerBuilder()
                .WithImage(ContainerImage)
                .WithName("aws2azure-perf-cosmos-" + Guid.NewGuid().ToString("N")[..8])
                .WithPortBinding(8081, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilMessageIsLogged("System is now fully ready to accept requests"))
                .Build();
            await _container.StartAsync().ConfigureAwait(false);
            var port = _container.GetMappedPublicPort(8081);
            var cosmosEndpoint = $"http://{_container.Hostname}:{port}/";
            CosmosEndpoint = cosmosEndpoint;

            using (var bootstrap = new HttpClient())
            {
                await CosmosRestBootstrap.EnsureDatabaseAsync(
                    bootstrap, cosmosEndpoint, EmulatorMasterKey, DatabaseName).ConfigureAwait(false);
            }

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
                          "endpoint":     "{{cosmosEndpoint}}",
                          "primaryKey":   "{{EmulatorMasterKey}}",
                          "databaseName": "{{DatabaseName}}"
                        }
                      }
                    }
                  ]
                }
                """;
            await _proxy.StartAsync(config, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

            using var ddb = CreateClient();
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = TableName,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);

            // Wait until table is ACTIVE.
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            while (DateTime.UtcNow < deadline)
            {
                var d = await ddb.DescribeTableAsync(TableName).ConfigureAwait(false);
                if (d.Table.TableStatus == TableStatus.ACTIVE) break;
                await Task.Delay(500).ConfigureAwait(false);
            }
            Ready = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"Fixture init failed: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync().ConfigureAwait(false);
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DynamoDbPerfCollection : ICollectionFixture<DynamoDbPerfFixture>
{
    public const string Name = "dynamodb-perf";
}

[Collection(DynamoDbPerfCollection.Name)]
public sealed class DynamoDbPerfTests(DynamoDbPerfFixture fixture)
{
    [SkippableFact]
    public async Task PutItem_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.PutItem (small)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var key = $"perf-w{workerId:D2}-{Guid.NewGuid():N}";
                await client.PutItemAsync(new PutItemRequest
                {
                    TableName = fixture.TableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new AttributeValue { S = key },
                        ["payload"] = new AttributeValue { S = "perf-payload-256-bytes" },
                    },
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "DynamoDB→Cosmos (REST) emulator");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
