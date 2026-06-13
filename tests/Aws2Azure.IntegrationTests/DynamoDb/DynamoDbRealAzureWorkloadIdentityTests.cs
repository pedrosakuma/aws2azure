using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Azure nightly smoke for the DynamoDB → Cosmos DB <b>data path</b>
/// authenticating via <b>Workload Identity</b> (issue #307) rather than a shared
/// key: PutItem → GetItem → DeleteItem against live Cosmos, with the proxy
/// exchanging the federated token for an AAD token. This is the only end-to-end
/// exercise of <c>WorkloadIdentityTokenSource</c> against real Azure — emulators
/// do not validate AAD, and Managed Identity is untestable on GitHub-hosted
/// runners (no IMDS).
///
/// <para><b>Why the table is created with the shared-key client.</b> Cosmos
/// native RBAC authorizes only <i>data-plane</i> actions over an AAD token —
/// creating/deleting containers (what <c>CreateTable</c>/<c>DeleteTable</c> map
/// to) is a <i>control-plane</i> operation and is rejected with
/// <c>Forbidden … cannot be authorized by AAD token in data plane</c>
/// (https://aka.ms/cosmos-native-rbac). Container provisioning is therefore an
/// admin concern handled out of band; the shared-key client owns the table
/// lifecycle here, and the Workload-Identity client drives the item CRUD that an
/// actual migrated workload runs — which is exactly the AAD flow under test.
/// Skips unless both the shared-key Cosmos config and the federated-token
/// environment are present.</para>
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureWorkloadIdentityTests
{
    private readonly RealAzureProxyFixture _fx;

    public DynamoDbRealAzureWorkloadIdentityTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task Item_data_path_round_trips_via_workload_identity()
    {
        Skip.IfNot(_fx.CosmosWorkloadIdentityConfigured && _fx.CosmosConfigured,
            "AZURE_FEDERATED_TOKEN_FILE/AZURE_TENANT_ID/AZURE_CLIENT_ID or AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB Workload Identity smoke.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..12];

        // Shared-key client owns the container (control-plane) lifecycle…
        using var admin = _fx.CreateDynamoDbClient();
        // …the Workload-Identity client exercises the item data path (the AAD
        // flow under test).
        using var wi = _fx.CreateDynamoDbClientWorkloadIdentity();
        var tableCreated = false;

        try
        {
            await admin.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                ],
                KeySchema =
                [
                    new KeySchemaElement("pk", KeyType.HASH),
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);
            tableCreated = true;

            // Wait for readiness through the WI client: DescribeTable performs the
            // first (cold) read of the table-metadata sidecar doc, so the
            // AAD-authenticated metadata path — not just the item writes — is
            // exercised before the item CRUD warms the shared metadata cache.
            await WaitForTableActiveAsync(wi, table).ConfigureAwait(false);

            await wi.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = "item-1" },
                    ["payload"] = new AttributeValue { S = "real-azure-wi" },
                },
            }).ConfigureAwait(false);

            var got = await wi.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            Assert.True(got.IsItemSet);
            Assert.Equal("real-azure-wi", got.Item["payload"].S);

            await wi.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
            }).ConfigureAwait(false);

            var afterDelete = await wi.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            Assert.False(afterDelete.IsItemSet);
        }
        finally
        {
            if (tableCreated)
            {
                try
                {
                    await admin.DeleteTableAsync(new DeleteTableRequest { TableName = table }).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup for the real-Azure smoke path.
                }
            }
        }
    }

    private static async Task WaitForTableActiveAsync(IAmazonDynamoDB client, string table)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var desc = await client.DescribeTableAsync(table).ConfigureAwait(false);
                if (desc.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
    }
}
