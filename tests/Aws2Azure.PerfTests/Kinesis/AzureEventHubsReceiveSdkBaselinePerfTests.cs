using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;
using Azure.Messaging.EventHubs.Producer;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.PerfTests.Kinesis;

/// <summary>
/// Receive-side baseline that hits the Event Hubs emulator directly with
/// <c>Azure.Messaging.EventHubs</c> — the proxy is left idle. Mirrors
/// <see cref="KinesisReceivePerfTests.GetRecords_throughput"/> shape
/// (c=1, 30s, 256 B records) so the rows in baseline-latest.md are
/// directly comparable and surface the "proxy tax" (issue #132).
///
/// Each "operation" is one <c>PartitionReceiver.ReceiveBatchAsync</c>
/// call (capped at 100 events or MaximumWaitTime). The receiver keeps
/// the cursor across batches — exactly mirroring how the proxy carries
/// <c>NextShardIterator</c> across <c>GetRecords</c> RPCs (gpt-5.5
/// review of PR #135). Empty windows (post-drain) count as completed
/// calls, matching the proxy-side metric.
/// </summary>
[Collection(KinesisPerfCollection.Name)]
public sealed class AzureEventHubsReceiveSdkBaselinePerfTests(KinesisPerfFixture fixture)
{
    private const int PrefillCount = 5_000;

    [SkippableFact]
    public async Task ReceiveBatchAsync_throughput_AzureSdk()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        var connStr = fixture.Inner.EventHubsConnectionString;
        var payload = Encoding.UTF8.GetBytes(new string('x', 256));

        // Pre-fill one partition.
        await using var producer = new EventHubProducerClient(connStr, KinesisEmulatorProxyFixture.EventHubName);
        var partitions = await producer.GetPartitionIdsAsync().ConfigureAwait(false);
        var partitionId = partitions[0];

        for (var sent = 0; sent < PrefillCount;)
        {
            var batch = await producer.CreateBatchAsync(new CreateBatchOptions { PartitionId = partitionId })
                .ConfigureAwait(false);
            while (sent < PrefillCount && batch.TryAdd(new EventData(payload)))
            {
                sent++;
            }
            await producer.SendAsync(batch).ConfigureAwait(false);
        }

        // PartitionReceiver holds cursor state across ReceiveBatchAsync
        // calls — mirrors NextShardIterator chasing on the proxy side.
        await using var receiver = new PartitionReceiver(
            EventHubConsumerClient.DefaultConsumerGroupName,
            partitionId,
            EventPosition.Earliest,
            connStr,
            KinesisEmulatorProxyFixture.EventHubName);

        var records = 0L;
        var dataCalls = 0L;
        var emptyCalls = 0L;
        var waitTime = TimeSpan.FromMilliseconds(500);

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.EventHubs.ReceiveBatchAsync (256 B records)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(30),
            warmup: TimeSpan.Zero,
            action: async (workerId, ct) =>
            {
                _ = workerId;
                var batch = await receiver.ReceiveBatchAsync(
                    maximumEventCount: 100,
                    maximumWaitTime: waitTime,
                    cancellationToken: ct).ConfigureAwait(false);

                var batchCount = 0;
                foreach (var ev in batch)
                {
                    _ = ev;
                    batchCount++;
                }
                Interlocked.Add(ref records, batchCount);
                if (batchCount > 0)
                {
                    Interlocked.Increment(ref dataCalls);
                }
                else
                {
                    Interlocked.Increment(ref emptyCalls);
                }
            });

        var notes =
            $"Azure SDK baseline — direct PartitionReceiver.ReceiveBatchAsync against EH emulator (no proxy); "
            + $"records={records}, dataCalls={dataCalls}, emptyCalls={emptyCalls}; calls/s metric";
        PerfReport.Append(result, notes: notes);
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
