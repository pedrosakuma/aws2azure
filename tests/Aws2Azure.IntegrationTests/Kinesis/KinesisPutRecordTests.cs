using System.Text;
using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "Integration")]
[Trait("Category", "Kinesis")]
[Collection(KinesisEmulatorProxyCollection.Name)]
public sealed class KinesisPutRecordTests
{
    private readonly KinesisEmulatorProxyFixture _fixture;

    public KinesisPutRecordTests(KinesisEmulatorProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task PutRecord_then_TrimHorizon_GetRecords_roundtrips_through_proxy()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason ?? "Docker not available.");

        using var client = _fixture.CreateClient();
        var partitionKey = "putrecord-" + KinesisTestHelpers.RandomSuffix();
        var payload = "payload-" + KinesisTestHelpers.RandomSuffix();

        var put = await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            PartitionKey = partitionKey,
            Data = new MemoryStream(Encoding.UTF8.GetBytes(payload)),
        }).ConfigureAwait(false);

        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
            ShardId = put.ShardId,
            ShardIteratorType = "TRIM_HORIZON",
        }).ConfigureAwait(false);

        var seen = await KinesisTestHelpers.ReadUntilAsync(
            client,
            iterator.ShardIterator,
            record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == payload,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        Assert.Contains(seen, record => record.PartitionKey == partitionKey && KinesisTestHelpers.Utf8(record) == payload);
    }
}
