using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
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
/// Each "operation" is one <c>ReadEventsFromPartitionAsync</c> batch
/// (capped at 100 events or MaximumWaitTime), matching how
/// <c>kinesis.GetRecords</c> is measured: calls/s metric, not
/// records/s. Empty windows (post-drain) count as completed calls.
/// </summary>
[Collection(KinesisPerfCollection.Name)]
public sealed class AzureEventHubsReceiveSdkBaselinePerfTests(KinesisPerfFixture fixture)
{
    private const int PrefillCount = 5_000;

    [SkippableFact]
    public async Task ReadEventsFromPartition_throughput_AzureSdk()
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

        await using var consumer = new EventHubConsumerClient(
            EventHubConsumerClient.DefaultConsumerGroupName,
            connStr,
            KinesisEmulatorProxyFixture.EventHubName);

        var records = 0L;
        var dataCalls = 0L;
        var emptyCalls = 0L;
        var readOptions = new ReadEventOptions
        {
            MaximumWaitTime = TimeSpan.FromMilliseconds(500),
        };

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.EventHubs.ReadEventsFromPartition (256 B records)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(30),
            warmup: TimeSpan.Zero,
            action: async (_, ct) =>
            {
                // One "operation" = one batch read (up to 100 events or 500ms wait),
                // mirroring how kinesis.GetRecords is measured.
                var batchRecords = 0;
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await foreach (var ev in consumer.ReadEventsFromPartitionAsync(
                    partitionId, EventPosition.Earliest, readOptions, batchCts.Token).ConfigureAwait(false))
                {
                    if (ev.Data is null)
                    {
                        // MaximumWaitTime elapsed without an event — surface as
                        // an empty call so the metric matches the proxy side.
                        break;
                    }
                    batchRecords++;
                    if (batchRecords >= 100)
                    {
                        break;
                    }
                }
                Interlocked.Add(ref records, batchRecords);
                if (batchRecords > 0)
                {
                    Interlocked.Increment(ref dataCalls);
                }
                else
                {
                    Interlocked.Increment(ref emptyCalls);
                }
            });

        var notes =
            $"Azure SDK baseline — direct EventHubConsumerClient.ReadEventsFromPartitionAsync against EH emulator (no proxy); "
            + $"records={records}, dataCalls={dataCalls}, emptyCalls={emptyCalls}; calls/s metric";
        PerfReport.Append(result, notes: notes);
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
    }
}
