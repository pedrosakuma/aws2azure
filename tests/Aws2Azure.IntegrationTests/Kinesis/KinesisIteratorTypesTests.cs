using System.Text;
using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "Integration")]
[Trait("Category", "Kinesis")]
[Collection(KinesisEmulatorProxyCollection.Name)]
public sealed class KinesisIteratorTypesTests
{
    private readonly KinesisEmulatorProxyFixture _fixture;

    public KinesisIteratorTypesTests(KinesisEmulatorProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Latest_iterator_only_returns_records_produced_after_iterator_creation()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason ?? "Docker not available.");

        using var client = _fixture.CreateClient();
        var partitionKey = "latest-" + KinesisTestHelpers.RandomSuffix();
        var beforePayload = "before-" + KinesisTestHelpers.RandomSuffix();
        var afterPayload = "after-" + KinesisTestHelpers.RandomSuffix();

        var before = await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            PartitionKey = partitionKey,
            Data = new MemoryStream(Encoding.UTF8.GetBytes(beforePayload)),
        }).ConfigureAwait(false);

        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            ShardId = before.ShardId,
            ShardIteratorType = "LATEST",
        }).ConfigureAwait(false);

        await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            PartitionKey = partitionKey,
            Data = new MemoryStream(Encoding.UTF8.GetBytes(afterPayload)),
        }).ConfigureAwait(false);

        var seen = await KinesisTestHelpers.ReadUntilAsync(
            client,
            iterator.ShardIterator,
            record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == afterPayload,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        Assert.DoesNotContain(seen, record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == beforePayload);
        Assert.Contains(seen, record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == afterPayload);
    }

    [SkippableFact]
    public async Task AtTimestamp_iterator_returns_records_from_requested_time_forward()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason ?? "Docker not available.");

        using var client = _fixture.CreateClient();
        var partitionKey = "timestamp-" + KinesisTestHelpers.RandomSuffix();
        var beforePayload = "before-ts-" + KinesisTestHelpers.RandomSuffix();
        var afterPayload = "after-ts-" + KinesisTestHelpers.RandomSuffix();

        await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            PartitionKey = partitionKey,
            Data = new MemoryStream(Encoding.UTF8.GetBytes(beforePayload)),
        }).ConfigureAwait(false);

        await Task.Delay(1100).ConfigureAwait(false);
        var boundary = DateTimeOffset.UtcNow;
        await Task.Delay(100).ConfigureAwait(false);

        var after = await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            PartitionKey = partitionKey,
            Data = new MemoryStream(Encoding.UTF8.GetBytes(afterPayload)),
        }).ConfigureAwait(false);

        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            ShardId = after.ShardId,
            ShardIteratorType = "AT_TIMESTAMP",
            Timestamp = KinesisTestHelpers.ToSdkTimestamp(boundary),
        }).ConfigureAwait(false);

        var seen = await KinesisTestHelpers.ReadUntilAsync(
            client,
            iterator.ShardIterator,
            record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == afterPayload,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        Assert.DoesNotContain(seen, record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == beforePayload);
        Assert.Contains(seen, record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == afterPayload);
    }
}
