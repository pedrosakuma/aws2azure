using System.Text;
using Amazon.Kinesis.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class KinesisRealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task GetRecords_reads_from_real_event_hubs()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var payload = "read-" + Guid.NewGuid().ToString("N");
        var put = await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = fixture.EventHubStream,
            PartitionKey = "read-" + Guid.NewGuid().ToString("N"),
            Data = new MemoryStream(Encoding.UTF8.GetBytes(payload)),
        }, timeout.Token).ConfigureAwait(false);

        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = put.ShardId,
            ShardIteratorType = "AT_SEQUENCE_NUMBER",
            StartingSequenceNumber = put.SequenceNumber,
        }, timeout.Token).ConfigureAwait(false);

        var records = await KinesisTestHelpers.ReadUntilAsync(
            client,
            iterator.ShardIterator,
            record => KinesisTestHelpers.Utf8(record) == payload,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        Assert.Contains(records, record => KinesisTestHelpers.Utf8(record) == payload);
    }

    [SkippableFact]
    public async Task ListShards_paginates_against_real_event_hubs()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var shardIds = new List<string>();
        string? nextToken = null;
        var pages = 0;
        do
        {
            var response = await client.ListShardsAsync(new ListShardsRequest
            {
                StreamName = nextToken is null ? fixture.EventHubStream : null,
                MaxResults = 1,
                NextToken = nextToken,
            }, timeout.Token).ConfigureAwait(false);
            pages++;
            Assert.InRange(pages, 1, 8);
            Assert.Single(response.Shards);
            shardIds.Add(response.Shards[0].ShardId);
            nextToken = response.NextToken;
        } while (!string.IsNullOrWhiteSpace(nextToken));

        Assert.True(pages > 1,
            "The real-Azure Event Hub needs at least two provisioned partitions to exercise ListShards continuation.");
        Assert.Equal(shardIds.Count, shardIds.Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task PutRecords_reports_real_event_hubs_results()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var run = Guid.NewGuid().ToString("N");
        var entries = Enumerable.Range(0, 4)
            .Select(i => new PutRecordsRequestEntry
            {
                PartitionKey = $"batch-{i % 2}",
                Data = new MemoryStream(Encoding.UTF8.GetBytes($"batch-{run}-{i}")),
            })
            .ToList();
        entries.Add(new PutRecordsRequestEntry
        {
            PartitionKey = "invalid-without-data",
        });

        try
        {
            var response = await client.PutRecordsAsync(new PutRecordsRequest
            {
                StreamName = fixture.EventHubStream,
                Records = entries,
            }, timeout.Token).ConfigureAwait(false);

            Assert.Equal(1, response.FailedRecordCount);
            Assert.Equal(entries.Count, response.Records.Count);
            Assert.All(response.Records.Take(4), result =>
            {
                Assert.False(string.IsNullOrWhiteSpace(result.ShardId));
                Assert.False(string.IsNullOrWhiteSpace(result.SequenceNumber));
                Assert.True(string.IsNullOrWhiteSpace(result.ErrorCode));
            });
            Assert.False(string.IsNullOrWhiteSpace(response.Records[4].ErrorCode));
            Assert.False(string.IsNullOrWhiteSpace(response.Records[4].ErrorMessage));
        }
        finally
        {
            foreach (var entry in entries)
            {
                entry.Data?.Dispose();
            }
        }
    }

    [SkippableFact]
    public async Task Concurrent_consumers_maintain_iterator_progress()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var payload = "concurrent-" + Guid.NewGuid().ToString("N");
        var put = await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = fixture.EventHubStream,
            PartitionKey = "concurrent-" + Guid.NewGuid().ToString("N"),
            Data = new MemoryStream(Encoding.UTF8.GetBytes(payload)),
        }, timeout.Token).ConfigureAwait(false);

        async Task<string> CreateIteratorAsync()
        {
            var response = await client.GetShardIteratorAsync(new GetShardIteratorRequest
            {
                StreamName = fixture.EventHubStream,
                ShardId = put.ShardId,
                ShardIteratorType = "AT_SEQUENCE_NUMBER",
                StartingSequenceNumber = put.SequenceNumber,
            }, timeout.Token).ConfigureAwait(false);
            return response.ShardIterator;
        }

        var firstIterator = await CreateIteratorAsync().ConfigureAwait(false);
        var secondIterator = await CreateIteratorAsync().ConfigureAwait(false);
        var reads = await Task.WhenAll(
            KinesisTestHelpers.ReadUntilAsync(
                client, firstIterator, record => KinesisTestHelpers.Utf8(record) == payload, TimeSpan.FromSeconds(45)),
            KinesisTestHelpers.ReadUntilAsync(
                client, secondIterator, record => KinesisTestHelpers.Utf8(record) == payload, TimeSpan.FromSeconds(45)))
            .ConfigureAwait(false);

        Assert.All(reads, records =>
            Assert.Contains(records, record => KinesisTestHelpers.Utf8(record) == payload));
    }
}
