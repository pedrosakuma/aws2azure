using System.Numerics;
using Aws2Azure.Modules.Kinesis.EventHubsRest;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class ShardMapperTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(32)]
    public void MapShards_evenly_distributes_hash_space_for_partition_counts(int partitionCount)
    {
        var partitionIds = Enumerable.Range(0, partitionCount)
            .Select(i => i.ToString())
            .ToArray();

        var shards = ShardMapper.MapShards(partitionIds);

        Assert.Equal(partitionCount, shards.Count);
        var totalHashSpace = BigInteger.One << 128;
        var maxHashKey = totalHashSpace - BigInteger.One;
        var expectedWidth = totalHashSpace / partitionCount;

        for (var i = 0; i < shards.Count; i++)
        {
            var shard = shards[i];
            Assert.Equal("shardId-" + i.ToString().PadLeft(12, '0'), shard.ShardId);
            Assert.Equal(i == 0 ? BigInteger.Zero : shards[i - 1].EndingHashKey + BigInteger.One, shard.StartingHashKey);
            Assert.Equal(i == shards.Count - 1 ? maxHashKey : shard.StartingHashKey + expectedWidth - BigInteger.One, shard.EndingHashKey);
            Assert.Equal(expectedWidth, shard.EndingHashKey - shard.StartingHashKey + BigInteger.One);
        }

        Assert.Equal(BigInteger.Zero, shards[0].StartingHashKey);
        Assert.Equal(maxHashKey, shards[^1].EndingHashKey);
    }

    [Fact]
    public void MapShards_orders_partition_ids_numerically()
    {
        var shards = ShardMapper.MapShards(["10", "2", "1"]);

        Assert.Equal(["1", "2", "10"], shards.Select(s => s.PartitionId).ToArray());
        Assert.Equal(["shardId-000000000001", "shardId-000000000002", "shardId-000000000010"], shards.Select(s => s.ShardId).ToArray());
    }
}
