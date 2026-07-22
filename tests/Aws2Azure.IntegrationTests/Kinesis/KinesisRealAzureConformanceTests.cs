using Amazon.Kinesis;
using System.Text;
using Amazon.Kinesis.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class KinesisRealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Describe_operations_and_ListShards_reflect_real_event_hubs_topology()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var describe = await client.DescribeStreamAsync(new DescribeStreamRequest
        {
            StreamName = fixture.EventHubStream,
        }, timeout.Token).ConfigureAwait(false);
        var summary = await client.DescribeStreamSummaryAsync(new DescribeStreamSummaryRequest
        {
            StreamName = fixture.EventHubStream,
        }, timeout.Token).ConfigureAwait(false);
        var shards = await client.ListShardsAsync(new ListShardsRequest
        {
            StreamName = fixture.EventHubStream,
            MaxResults = 100,
        }, timeout.Token).ConfigureAwait(false);

        Assert.NotEmpty(describe.StreamDescription.Shards);
        Assert.Equal(describe.StreamDescription.Shards.Count, summary.StreamDescriptionSummary.OpenShardCount);
        Assert.Equal(
            describe.StreamDescription.Shards.Select(item => item.ShardId),
            shards.Shards.Select(item => item.ShardId));
    }

    [SkippableFact]
    public async Task GetRecords_reads_from_real_event_hubs()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var target = await KinesisTestHelpers.ResolvePartitionTargetAsync(client, fixture.EventHubStream, timeout.Token).ConfigureAwait(false);
        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "LATEST",
        }, timeout.Token).ConfigureAwait(false);
        var primedIterator = await KinesisTestHelpers.PrimeIteratorAsync(client, iterator.ShardIterator, timeout.Token).ConfigureAwait(false);

        var payload = "read-" + Guid.NewGuid().ToString("N");
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = fixture.EventHubStream,
            PartitionKey = target.PartitionKey,
            Data = data,
        }, timeout.Token).ConfigureAwait(false);

        var records = await KinesisTestHelpers.ReadUntilAsync(
            client,
            primedIterator,
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
        var target = await KinesisTestHelpers.ResolvePartitionTargetAsync(client, fixture.EventHubStream, timeout.Token).ConfigureAwait(false);
        async Task<string> CreateIteratorAsync()
        {
            var response = await client.GetShardIteratorAsync(new GetShardIteratorRequest
            {
                StreamName = fixture.EventHubStream,
                ShardId = target.ShardId,
                ShardIteratorType = "LATEST",
            }, timeout.Token).ConfigureAwait(false);
            return response.ShardIterator;
        }

        var firstIterator = await CreateIteratorAsync().ConfigureAwait(false);
        var secondIterator = await CreateIteratorAsync().ConfigureAwait(false);
        Assert.NotEqual(firstIterator, secondIterator);
        var primedIterators = await Task.WhenAll(
            KinesisTestHelpers.PrimeIteratorAsync(client, firstIterator, timeout.Token),
            KinesisTestHelpers.PrimeIteratorAsync(client, secondIterator, timeout.Token)).ConfigureAwait(false);

        var payload = "concurrent-" + Guid.NewGuid().ToString("N");
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = fixture.EventHubStream,
            PartitionKey = target.PartitionKey,
            Data = data,
        }, timeout.Token).ConfigureAwait(false);

        var reads = await Task.WhenAll(
            KinesisTestHelpers.ReadUntilAsync(
                client, primedIterators[0], record => KinesisTestHelpers.Utf8(record) == payload, TimeSpan.FromSeconds(45)),
            KinesisTestHelpers.ReadUntilAsync(
                client, primedIterators[1], record => KinesisTestHelpers.Utf8(record) == payload, TimeSpan.FromSeconds(45)))
            .ConfigureAwait(false);

        Assert.All(reads, records =>
            Assert.Contains(records, record => KinesisTestHelpers.Utf8(record) == payload));
    }

    [SkippableFact]
    public async Task Iterator_types_empty_reads_and_continuation_progress_are_stable()
    {
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis conformance.");

        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var target = await KinesisTestHelpers.ResolvePartitionTargetAsync(
            client, fixture.EventHubStream, timeout.Token).ConfigureAwait(false);

        var trim = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "TRIM_HORIZON",
        }, timeout.Token).ConfigureAwait(false);
        Assert.False(string.IsNullOrWhiteSpace(trim.ShardIterator));

        var latest = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "LATEST",
        }, timeout.Token).ConfigureAwait(false);
        var empty = await client.GetRecordsAsync(new GetRecordsRequest
        {
            ShardIterator = latest.ShardIterator,
            Limit = 10,
        }, timeout.Token).ConfigureAwait(false);
        Assert.Empty(empty.Records);
        Assert.False(string.IsNullOrWhiteSpace(empty.NextShardIterator));

        var boundary = DateTimeOffset.UtcNow;
        await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        var timestampPayload = "timestamp-" + Guid.NewGuid().ToString("N");
        PutRecordResponse timestampPut;
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes(timestampPayload)))
        {
            timestampPut = await client.PutRecordAsync(new PutRecordRequest
            {
                StreamName = fixture.EventHubStream,
                PartitionKey = target.PartitionKey,
                Data = data,
            }, timeout.Token).ConfigureAwait(false);
        }

        var atTimestamp = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "AT_TIMESTAMP",
            Timestamp = KinesisTestHelpers.ToSdkTimestamp(boundary),
        }, timeout.Token).ConfigureAwait(false);
        var timestampRecords = await KinesisTestHelpers.ReadUntilAsync(
            client,
            atTimestamp.ShardIterator,
            record => KinesisTestHelpers.Utf8(record) == timestampPayload,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        Assert.Single(
            timestampRecords,
            item => KinesisTestHelpers.Utf8(item) == timestampPayload);

        var atSequence = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "AT_SEQUENCE_NUMBER",
            StartingSequenceNumber = timestampPut.SequenceNumber,
        }, timeout.Token).ConfigureAwait(false);
        var atSequenceRecords = await KinesisTestHelpers.ReadUntilAsync(
            client,
            atSequence.ShardIterator,
            item => KinesisTestHelpers.Utf8(item) == timestampPayload,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        Assert.Contains(
            atSequenceRecords,
            item => KinesisTestHelpers.Utf8(item) == timestampPayload);

        var afterSequence = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "AFTER_SEQUENCE_NUMBER",
            StartingSequenceNumber = timestampPut.SequenceNumber,
        }, timeout.Token).ConfigureAwait(false);
        await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        var afterPayload = "after-sequence-" + Guid.NewGuid().ToString("N");
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes(afterPayload)))
        {
            await client.PutRecordAsync(new PutRecordRequest
            {
                StreamName = fixture.EventHubStream,
                PartitionKey = target.PartitionKey,
                Data = data,
            }, timeout.Token).ConfigureAwait(false);
        }
        var afterSequenceRecords = await KinesisTestHelpers.ReadUntilAsync(
            client,
            afterSequence.ShardIterator,
            item => KinesisTestHelpers.Utf8(item) == afterPayload,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        Assert.Contains(
            afterSequenceRecords,
            item => KinesisTestHelpers.Utf8(item) == afterPayload);
    }
}
