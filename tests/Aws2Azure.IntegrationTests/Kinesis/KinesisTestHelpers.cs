using System.Security.Cryptography;
using System.Text;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;

namespace Aws2Azure.IntegrationTests.Kinesis;

internal static class KinesisTestHelpers
{
    public static string RandomSuffix() => Guid.NewGuid().ToString("N")[..12];

    public static string Utf8(Record record)
    {
        record.Data.Position = 0;
        using var reader = new StreamReader(record.Data, Encoding.UTF8, leaveOpen: true);
        var text = reader.ReadToEnd();
        record.Data.Position = 0;
        return text;
    }

    public static async Task<IReadOnlyList<Record>> ReadUntilAsync(
        AmazonKinesisClient client,
        string shardIterator,
        Func<Record, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var seen = new List<Record>();
        var currentIterator = shardIterator;
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetRecordsAsync(new GetRecordsRequest
            {
                ShardIterator = currentIterator,
                Limit = 1000,
            }).ConfigureAwait(false);

            seen.AddRange(response.Records);
            if (seen.Any(predicate))
            {
                return seen;
            }

            currentIterator = response.NextShardIterator;
            await Task.Delay(250).ConfigureAwait(false);
        }

        return seen;
    }

    public static Dictionary<int, List<string>> PickPartitionKeys(int partitionCount, int totalRecords, int requiredPartitions)
    {
        var byPartition = new Dictionary<int, List<string>>();
        foreach (var candidate in new[]
                 {
                     "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet",
                     "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango",
                 })
        {
            var partition = ComputePartitionIndex(candidate, partitionCount);
            if (!byPartition.TryGetValue(partition, out var keys))
            {
                keys = [];
                byPartition.Add(partition, keys);
            }

            keys.Add(candidate);
        }

        return byPartition
            .Where(kvp => kvp.Value.Count > 0)
            .OrderBy(kvp => kvp.Key)
            .Take(requiredPartitions)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Take(totalRecords).ToList());
    }

    private static int ComputePartitionIndex(string partitionKey, int partitionCount)
    {
        var bytes = Encoding.UTF8.GetBytes(partitionKey);
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(bytes, hash);
        var remainder = 0;
        for (var i = 0; i < hash.Length; i++)
        {
            remainder = ((remainder << 8) + hash[i]) % partitionCount;
        }

        return remainder;
    }

    public static DateTime ToSdkTimestamp(DateTimeOffset value)
        => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Utc);
}
