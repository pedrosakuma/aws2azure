using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

/// <summary>
/// Real-Azure nightly smoke for the Kinesis module (issue #153): a
/// <c>PutRecord</c> against a live Azure Event Hubs entity. The module does not
/// implement <c>CreateStream</c>, so the Event Hub must already exist
/// (<c>AZURE_EVENTHUBS_STREAM</c>) — the test only writes a record and asserts
/// the proxy returns a shard id + sequence number. Skips when the
/// <c>AZURE_EVENTHUBS_*</c> secrets are absent.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class KinesisRealAzureSmokeTests
{
    private readonly RealAzureProxyFixture _fx;

    public KinesisRealAzureSmokeTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task PutRecord_writes_to_real_event_hubs()
    {
        Skip.IfNot(_fx.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis smoke.");

        using var client = _fx.CreateKinesisClient();

        var payload = "aws2azure real-Azure Kinesis smoke " + Guid.NewGuid().ToString("N");
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var response = await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = _fx.EventHubStream,
            PartitionKey = "smoke-partition",
            Data = data,
        }).ConfigureAwait(false);

        Assert.False(string.IsNullOrWhiteSpace(response.ShardId));
        Assert.False(string.IsNullOrWhiteSpace(response.SequenceNumber));
    }
}
