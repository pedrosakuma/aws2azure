using Microsoft.Azure.Cosmos;
using Xunit;

namespace Aws2Azure.PerfTests.DynamoDb;

/// <summary>
/// Baseline that hits the Cosmos DB emulator directly with the Cosmos
/// SDK NoSQL client — the proxy is left idle. Mirrors
/// <see cref="DynamoDbPerfTests.PutItem_throughput"/> shape (c=16, 20s)
/// so the two rows in baseline-latest.md are directly comparable and
/// surface the "proxy tax" (issue #131).
/// </summary>
[Collection(DynamoDbPerfCollection.Name)]
public sealed class AzureCosmosSdkBaselinePerfTests(DynamoDbPerfFixture fixture)
{
    [SkippableFact]
    public async Task UpsertItem_throughput_AzureSdk()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        // Emulator uses a self-signed cert and (vnext-preview) accepts
        // plain HTTP — bypass cert validation + force Gateway mode so
        // the SDK uses the emulator's REST endpoint instead of TCP/Direct.
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            }),
        };

        using var cosmos = new CosmosClient(fixture.CosmosEndpoint, fixture.CosmosMasterKey, options);
        var database = cosmos.GetDatabase(fixture.DatabaseName);
        var containerName = "sdk-baseline-" + Guid.NewGuid().ToString("N")[..8];
        var containerResp = await database.CreateContainerIfNotExistsAsync(
            containerName, partitionKeyPath: "/pk").ConfigureAwait(false);
        var container = containerResp.Container;

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.Cosmos.UpsertItem (small)",
            concurrency: PerfConcurrency.Scale(16),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var pk = $"perf-w{workerId:D2}-{Guid.NewGuid():N}";
                var item = new
                {
                    id = pk,
                    pk,
                    payload = "perf-payload-256-bytes",
                };
                await container.UpsertItemAsync(item, new PartitionKey(pk), cancellationToken: ct)
                    .ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"Azure SDK baseline — direct CosmosClient.UpsertItemAsync, no proxy [{fixture.BackendName}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task ReadItem_throughput_AzureSdk()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var cosmos = CreateBaselineCosmosClient();
        var (container, _) = await SetupBaselineReadContainerAsync(cosmos).ConfigureAwait(false);

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.Cosmos.ReadItem (small)",
            concurrency: PerfConcurrency.Scale(16),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var pk = $"sdk-pk-{workerId % BaselineReadPartitions:D2}";
                var idx = Random.Shared.Next(0, BaselineReadItemsPerPartition);
                var id = $"{pk}-{idx:D4}";
                try
                {
                    _ = await container.ReadItemAsync<BaselineReadDoc>(id, new PartitionKey(pk), cancellationToken: ct)
                        .ConfigureAwait(false);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException($"ReadItem missed for id={id} pk={pk} — seed data missing.", ex);
                }
            });

        PerfReport.Append(result, notes: $"Azure SDK baseline — direct CosmosClient.ReadItemAsync, no proxy [{fixture.BackendName}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task ReadManyItems_25_throughput_AzureSdk()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var cosmos = CreateBaselineCosmosClient();
        var (container, _) = await SetupBaselineReadContainerAsync(cosmos).ConfigureAwait(false);

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.Cosmos.ReadManyItems (25 keys)",
            concurrency: PerfConcurrency.Scale(8),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var pk = $"sdk-pk-{workerId % BaselineReadPartitions:D2}";
                var keys = new List<(string id, PartitionKey pk)>(25);
                var seen = new HashSet<int>();
                while (keys.Count < 25)
                {
                    var idx = Random.Shared.Next(0, BaselineReadItemsPerPartition);
                    if (!seen.Add(idx)) continue;
                    keys.Add(($"{pk}-{idx:D4}", new PartitionKey(pk)));
                }
                var resp = await container.ReadManyItemsAsync<BaselineReadDoc>(keys, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (resp.Count != 25)
                {
                    throw new InvalidOperationException($"ReadManyItems returned {resp.Count}/25 items — partial response invalidates perf measurement (seed gap or SDK regression).");
                }
            });

        PerfReport.Append(result, notes: $"Azure SDK baseline — direct CosmosClient.ReadManyItemsAsync, no proxy [{fixture.BackendName}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    private const int BaselineReadPartitions = 10;
    private const int BaselineReadItemsPerPartition = 50;
    private const string BaselineReadContainerName = "sdk-baseline-reads";

    private CosmosClient CreateBaselineCosmosClient()
    {
        // Same emulator-friendly options as the write baseline above.
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            }),
        };
        return new CosmosClient(fixture.CosmosEndpoint, fixture.CosmosMasterKey, options);
    }

    private async Task<(Container container, bool seeded)> SetupBaselineReadContainerAsync(CosmosClient cosmos)
    {
        // Idempotent: create the container once per fixture and seed only if
        // empty. Concurrent test runs against the same fixture share the
        // same seeded container, so the read scenarios all see the same
        // working set.
        var database = cosmos.GetDatabase(fixture.DatabaseName);
        var containerResp = await database.CreateContainerIfNotExistsAsync(
            BaselineReadContainerName, partitionKeyPath: "/pk").ConfigureAwait(false);
        var container = containerResp.Container;

        // Check for seed marker.
        try
        {
            _ = await container.ReadItemAsync<BaselineReadDoc>(
                "sdk-pk-00-0000", new PartitionKey("sdk-pk-00")).ConfigureAwait(false);
            return (container, false);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Fall through to seed.
        }

        for (var pi = 0; pi < BaselineReadPartitions; pi++)
        {
            var pk = $"sdk-pk-{pi:D2}";
            for (var si = 0; si < BaselineReadItemsPerPartition; si++)
            {
                var id = $"{pk}-{si:D4}";
                await container.UpsertItemAsync(new BaselineReadDoc
                {
                    id = id,
                    pk = pk,
                    payload = "seed-256-bytes-of-padding-" + new string('x', 200),
                }, new PartitionKey(pk)).ConfigureAwait(false);
            }
        }

        return (container, true);
    }

    private sealed class BaselineReadDoc
    {
#pragma warning disable IDE1006 // Naming Styles (Cosmos uses lowercase id)
        public string id { get; set; } = string.Empty;
        public string pk { get; set; } = string.Empty;
        public string payload { get; set; } = string.Empty;
#pragma warning restore IDE1006
    }
}
