using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "Integration")]
[Trait("Category", "Kinesis")]
[Collection(KinesisEmulatorProxyCollection.Name)]
public sealed class KinesisDescribeStreamTests
{
    private readonly KinesisEmulatorProxyFixture _fixture;

    public KinesisDescribeStreamTests(KinesisEmulatorProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task DescribeStream_ListShards_and_DescribeStreamSummary_reflect_emulator_topology()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason ?? "Docker not available.");

        using var client = _fixture.CreateClient();

        var describe = await client.DescribeStreamAsync(new DescribeStreamRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
        }).ConfigureAwait(false);
        Assert.Equal(4, describe.StreamDescription.Shards.Count);
        string[] expectedShardIds = ["shardId-000000000000", "shardId-000000000001", "shardId-000000000002", "shardId-000000000003"];
        Assert.Equal(expectedShardIds, describe.StreamDescription.Shards.Select(s => s.ShardId).ToArray());

        var page1 = await client.ListShardsAsync(new ListShardsRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            MaxResults = 2,
        }).ConfigureAwait(false);
        Assert.Equal(2, page1.Shards.Count);
        Assert.False(string.IsNullOrWhiteSpace(page1.NextToken));

        var page2 = await client.ListShardsAsync(new ListShardsRequest
        {
            NextToken = page1.NextToken,
        }).ConfigureAwait(false);
        Assert.Equal(2, page2.Shards.Count);
        Assert.True(string.IsNullOrWhiteSpace(page2.NextToken));
        Assert.Equal(expectedShardIds, page1.Shards.Concat(page2.Shards).Select(s => s.ShardId).ToArray());

        var summary = await client.DescribeStreamSummaryAsync(new DescribeStreamSummaryRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
        }).ConfigureAwait(false);
        Assert.Equal(4, summary.StreamDescriptionSummary.OpenShardCount);
    }

    [SkippableFact]
    public async Task Trim_horizon_iterators_progress_independently_on_the_emulator()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason ?? "Docker not available.");

        using var client = _fixture.CreateClient();
        var partitionKey = "independent-" + KinesisTestHelpers.RandomSuffix();
        var firstPayload = "first-" + KinesisTestHelpers.RandomSuffix();
        var secondPayload = "second-" + KinesisTestHelpers.RandomSuffix();
        var first = await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            PartitionKey = partitionKey,
            Data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(firstPayload)),
        }).ConfigureAwait(false);
        await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            PartitionKey = partitionKey,
            Data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(secondPayload)),
        }).ConfigureAwait(false);

        async Task<string> NewIteratorAsync()
        {
            var response = await client.GetShardIteratorAsync(new GetShardIteratorRequest
            {
                StreamName = KinesisEmulatorProxyFixture.StreamName,
                ShardId = first.ShardId,
                ShardIteratorType = "TRIM_HORIZON",
            }).ConfigureAwait(false);
            return response.ShardIterator;
        }

        var iterators = await Task.WhenAll(NewIteratorAsync(), NewIteratorAsync()).ConfigureAwait(false);
        var reads = await Task.WhenAll(
            KinesisTestHelpers.ReadUntilAsync(
                client,
                iterators[0],
                record => KinesisTestHelpers.Utf8(record) == secondPayload,
                TimeSpan.FromSeconds(30)),
            KinesisTestHelpers.ReadUntilAsync(
                client,
                iterators[1],
                record => KinesisTestHelpers.Utf8(record) == secondPayload,
                TimeSpan.FromSeconds(30))).ConfigureAwait(false);

        Assert.All(reads, records =>
        {
            var matching = records
                .Where(record => record.PartitionKey == partitionKey)
                .Select(KinesisTestHelpers.Utf8)
                .ToArray();
            Assert.Contains(firstPayload, matching);
            Assert.Contains(secondPayload, matching);
            Assert.True(
                Array.IndexOf(matching, firstPayload) < Array.IndexOf(matching, secondPayload),
                "Each iterator must preserve per-shard record order.");
        });
    }
}
