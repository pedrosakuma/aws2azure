using System.Text;
using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "Integration")]
[Trait("Category", "Kinesis")]
[Collection(KinesisEmulatorProxyCollection.Name)]
public sealed class KinesisPutRecordsTests
{
    private readonly KinesisEmulatorProxyFixture _fixture;

    public KinesisPutRecordsTests(KinesisEmulatorProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task PutRecords_batch_roundtrips_records_from_two_partitions()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason ?? "Docker not available.");

        using var client = _fixture.CreateClient();
        var keysByPartition = KinesisTestHelpers.PickPartitionKeys(partitionCount: 4, totalRecords: 3, requiredPartitions: 2);
        Assert.Equal(2, keysByPartition.Count);

        var payloads = new List<(string PartitionKey, string Payload)>();
        foreach (var (partition, keys) in keysByPartition.OrderBy(kvp => kvp.Key))
        {
            var recordCount = partition == keysByPartition.Keys.Min() ? 3 : 2;
            for (var i = 0; i < recordCount; i++)
            {
                payloads.Add((keys[0], $"p{partition}-record-{i}-{KinesisTestHelpers.RandomSuffix()}"));
            }
        }

        var put = await client.PutRecordsAsync(new PutRecordsRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            Records = payloads.Select(entry => new PutRecordsRequestEntry
            {
                PartitionKey = entry.PartitionKey,
                Data = new MemoryStream(Encoding.UTF8.GetBytes(entry.Payload)),
            }).ToList(),
        }).ConfigureAwait(false);

        Assert.Equal(0, put.FailedRecordCount);
        var expectedByShard = payloads.Zip(put.Records, (payload, response) => (payload, response))
            .GroupBy(item => item.response.ShardId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.payload).ToList());
        Assert.Equal(2, expectedByShard.Count);

        foreach (var (shardId, expectedRecords) in expectedByShard)
        {
            var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
            {
                StreamName = KinesisEmulatorProxyFixture.StreamName,
                ShardId = shardId,
                ShardIteratorType = "TRIM_HORIZON",
            }).ConfigureAwait(false);

            var seen = await KinesisTestHelpers.ReadUntilAsync(
                client,
                iterator.ShardIterator,
                record => expectedRecords.Any(expected => expected.PartitionKey == record.PartitionKey && expected.Payload == KinesisTestHelpers.Utf8(record)),
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            foreach (var expected in expectedRecords)
            {
                Assert.Contains(seen, record => record.PartitionKey == expected.PartitionKey && KinesisTestHelpers.Utf8(record) == expected.Payload);
            }
        }
    }
}
