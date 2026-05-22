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
}
