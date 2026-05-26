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
            concurrency: 16,
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

        PerfReport.Append(result, notes: "Azure SDK baseline — direct CosmosClient.UpsertItemAsync against Cosmos emulator (no proxy)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
