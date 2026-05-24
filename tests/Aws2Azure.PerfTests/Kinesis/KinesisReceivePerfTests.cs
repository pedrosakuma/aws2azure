using System.Text;
using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Receive-side perf for Kinesis. Pre-fills one shard with N records,
/// opens a TRIM_HORIZON iterator and closed-loops <c>GetRecords</c>
/// chasing <c>NextShardIterator</c>. Each "operation" is one GetRecords
/// RPC — empty responses (post-drain) still count as completed RPCs so
/// the harness measures call-rate, not records-per-second.
/// </summary>
[Collection(KinesisPerfCollection.Name)]
public sealed class KinesisReceivePerfTests(KinesisPerfFixture fixture)
{
    // Sized to comfortably survive a 30 s measurement window. Kinesis
    // receives at ~100 cps × ~50 records/call ≫ 5 000 — so the iterator
    // will drain mid-window and the harness will measure a mix of full
    // and empty calls. That's intentional: empty-call latency is also a
    // useful regression signal.
    private const int PrefillCount = 5_000;

    [SkippableFact]
    public async Task GetRecords_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.Inner.CreateClient();
        var payload = Encoding.UTF8.GetBytes(new string('x', 256));

        // Pick the first shard deterministically and pre-fill it so the
        // measurement window has data to drain. PutRecord lets us pin the
        // partition (via PartitionKey) so the records land on the shard we
        // measure against.
        var listResp = await client.ListShardsAsync(new ListShardsRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
        }).ConfigureAwait(false);
        var shardId = listResp.Shards[0].ShardId;
        var partitionKey = $"perf-recv-{Guid.NewGuid():N}";

        await PrefillAsync(client, partitionKey, payload, PrefillCount).ConfigureAwait(false);

        var iter = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            ShardId = shardId,
            ShardIteratorType = "TRIM_HORIZON",
        }).ConfigureAwait(false);

        // Single worker — Kinesis ordering is per-shard, and parallel
        // GetRecords against one iterator is unsafe (the cursor advances
        // per call). Matches the existing send-side test shape.
        var currentIterator = iter.ShardIterator;
        var lock_ = new object();

        var result = await PerfRunner.RunAsync(
            scenario: "kinesis.GetRecords (256 B records)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(30),
            warmup: TimeSpan.Zero,
            action: async (_, ct) =>
            {
                string? toFetch;
                lock (lock_)
                {
                    toFetch = currentIterator;
                }
                if (string.IsNullOrEmpty(toFetch))
                {
                    return;
                }
                var resp = await client.GetRecordsAsync(new GetRecordsRequest
                {
                    ShardIterator = toFetch,
                    Limit = 100,
                }, ct).ConfigureAwait(false);
                lock (lock_)
                {
                    currentIterator = resp.NextShardIterator;
                }
            });

        PerfReport.Append(result, notes: "Kinesis←EventHubs(AMQP) emulator — GetRecords drain (limit=100); calls/s metric");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
    }

    private static async Task PrefillAsync(
        Amazon.Kinesis.IAmazonKinesis client,
        string partitionKey,
        byte[] payload,
        int total)
    {
        // PutRecords batches up to 500 entries / 5 MiB. EH emulator
        // backpressures bulk ingest the same way Service Bus does — retry
        // failed entries with exponential backoff rather than aborting.
        const int BatchSize = 100;
        const int MaxRetries = 5;
        var produced = 0;
        while (produced < total)
        {
            var thisBatch = Math.Min(BatchSize, total - produced);
            var entries = new List<PutRecordsRequestEntry>(thisBatch);
            for (var i = 0; i < thisBatch; i++)
            {
                entries.Add(new PutRecordsRequestEntry
                {
                    PartitionKey = partitionKey,
                    Data = new MemoryStream(payload, writable: false),
                });
            }

            var attempt = 0;
            while (true)
            {
                PutRecordsResponse resp;
                try
                {
                    resp = await client.PutRecordsAsync(new PutRecordsRequest
                    {
                        StreamName = KinesisEmulatorProxyFixture.StreamName,
                        Records = entries,
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
                {
                    await Task.Delay(BackoffMs(attempt)).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                if (resp.FailedRecordCount > 0)
                {
                    if (attempt >= MaxRetries)
                    {
                        var firstErr = resp.Records.FirstOrDefault(r => !string.IsNullOrEmpty(r.ErrorCode));
                        throw new Xunit.Sdk.XunitException(
                            $"Prefill failed after {attempt} retries: {firstErr?.ErrorCode} {firstErr?.ErrorMessage}");
                    }
                    // Rebuild the entry list for just the failed records.
                    var failed = new List<PutRecordsRequestEntry>();
                    for (var i = 0; i < resp.Records.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(resp.Records[i].ErrorCode))
                        {
                            failed.Add(new PutRecordsRequestEntry
                            {
                                PartitionKey = partitionKey,
                                Data = new MemoryStream(payload, writable: false),
                            });
                        }
                    }
                    entries = failed;
                    await Task.Delay(BackoffMs(attempt)).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                break;
            }
            produced += thisBatch;
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is Amazon.Kinesis.Model.ProvisionedThroughputExceededException
        || (ex.Message?.Contains("AMQP send failed", StringComparison.OrdinalIgnoreCase) ?? false)
        || (ex.Message?.Contains("InternalFailure", StringComparison.OrdinalIgnoreCase) ?? false);

    private static int BackoffMs(int attempt) => (int)Math.Min(2000, 50 * Math.Pow(2, attempt));
}
