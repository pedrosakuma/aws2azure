using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.PerfTests.Kinesis;

/// <summary>
/// Baseline perf scenarios that bypass the aws2azure proxy entirely and
/// hit the Event Hubs emulator with the official Azure SDK
/// (<c>Azure.Messaging.EventHubs</c>). Purpose: isolate whether the
/// ~1.7 ops/s ceiling observed in <see cref="KinesisPerfTests"/> is an
/// emulator-side limit or a proxy-side limit (issue #129).
///
/// Reuses <see cref="KinesisPerfFixture"/> so the EH emulator container
/// and Azurite are shared; the proxy is left idle.
/// </summary>
[Collection(KinesisPerfCollection.Name)]
public sealed class AzureEventHubsSdkBaselinePerfTests(KinesisPerfFixture fixture)
{
    [SkippableFact]
    public async Task SendAsync_throughput_AzureSdk_singlePartition()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        var connStr = fixture.Inner.EventHubsConnectionString;
        await using var producer = new EventHubProducerClient(
            connStr,
            KinesisEmulatorProxyFixture.EventHubName);

        var payload = Encoding.UTF8.GetBytes(new string('x', 256));

        // Same shape as KinesisPerfTests.PutRecord_throughput: c=1, 60s,
        // no warm-up window in the runner (we do one priming send below
        // so the link ATTACH cost doesn't get charged to the first
        // measured iteration).
        await producer.SendAsync(
            new[] { new EventData(payload) },
            new SendEventOptions { PartitionKey = "perf-warmup" });

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.EventHubs.SendAsync (256 B, c=1)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(60),
            warmup: TimeSpan.Zero,
            action: async (workerId, ct) =>
            {
                await producer.SendAsync(
                    new[] { new EventData(payload) },
                    new SendEventOptions { PartitionKey = "perf-w" + workerId },
                    ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "Azure SDK baseline — direct EventHubProducerClient against EH emulator (no proxy)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
    }
}
