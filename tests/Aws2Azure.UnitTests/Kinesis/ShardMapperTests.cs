using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;

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

    [Fact]
    public void MapShards_hash_key_ranges_do_not_describe_modulo_write_routing()
    {
        var shards = ShardMapper.MapShards(["0", "1"]);
        var partitionKey = "a";
        var routedPartitionIndex = PutRecordHandler.ComputePartitionIndex(partitionKey, shards.Count);
        var hashKey = new BigInteger(MD5.HashData(Encoding.UTF8.GetBytes(partitionKey)), isUnsigned: true, isBigEndian: true);
        var rangeIndex = Array.FindIndex(shards.ToArray(), shard => hashKey >= shard.StartingHashKey && hashKey <= shard.EndingHashKey);

        Assert.Equal(0, rangeIndex);
        Assert.Equal(1, routedPartitionIndex);
    }
}
