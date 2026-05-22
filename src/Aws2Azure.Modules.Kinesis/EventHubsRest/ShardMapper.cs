using System.Globalization;
using System.Numerics;

namespace Aws2Azure.Modules.Kinesis.EventHubsRest;

public static class ShardMapper
{
    public static IReadOnlyList<MappedShard> MapShards(IReadOnlyList<string> partitionIds)
    {
        ArgumentNullException.ThrowIfNull(partitionIds);
        if (partitionIds.Count == 0)
        {
            return [];
        }

        var orderedIds = partitionIds.ToArray();
        Array.Sort(orderedIds, ComparePartitionIds);

        var totalHashSpace = BigInteger.One << 128;
        var maxHashKey = totalHashSpace - BigInteger.One;
        var step = totalHashSpace / orderedIds.Length;
        var shards = new MappedShard[orderedIds.Length];

        for (var i = 0; i < orderedIds.Length; i++)
        {
            var startingHashKey = step * i;
            var endingHashKey = i == orderedIds.Length - 1
                ? maxHashKey
                : (step * (i + 1)) - BigInteger.One;

            shards[i] = new MappedShard(
                ShardId: "shardId-" + orderedIds[i].PadLeft(12, '0'),
                PartitionId: orderedIds[i],
                StartingHashKey: startingHashKey,
                EndingHashKey: endingHashKey,
                StartingSequenceNumber: "0");
        }

        return shards;
    }

    private static int ComparePartitionIds(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (ulong.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftValue)
            && ulong.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightValue))
        {
            return leftValue.CompareTo(rightValue);
        }

        return string.CompareOrdinal(left, right);
    }
}

public sealed record MappedShard(
    string ShardId,
    string PartitionId,
    BigInteger StartingHashKey,
    BigInteger EndingHashKey,
    string StartingSequenceNumber);
