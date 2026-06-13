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
/// Real-Azure nightly smoke for the Kinesis → Event Hubs path authenticating via
/// <b>Workload Identity</b> (issue #307) rather than a SAS key. The proxy
/// resolves the Workload-Identity AWS credential to an Event Hubs backend
/// configured with <c>authMode: workloadIdentity</c>, exchanges the federated
/// token for an AAD token, and writes a record. The Event Hub must already exist
/// (<c>AZURE_EVENTHUBS_STREAM</c>). Skips unless the federated token plus the
/// Event Hubs namespace/stream are configured.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class KinesisRealAzureWorkloadIdentityTests
{
    private readonly RealAzureProxyFixture _fx;

    public KinesisRealAzureWorkloadIdentityTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task PutRecord_writes_to_real_event_hubs_via_workload_identity()
    {
        Skip.IfNot(_fx.EventHubsWorkloadIdentityConfigured,
            "AZURE_FEDERATED_TOKEN_FILE/AZURE_TENANT_ID/AZURE_CLIENT_ID or AZURE_EVENTHUBS_NAMESPACE/STREAM not set — skipping real-Azure Kinesis Workload Identity smoke.");

        using var client = _fx.CreateKinesisClientWorkloadIdentity();

        var payload = "aws2azure real-Azure Kinesis Workload Identity smoke " + Guid.NewGuid().ToString("N");
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
