using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Azure nightly smoke for the DynamoDB → Cosmos DB path authenticating via
/// <b>Workload Identity</b> (issue #307) rather than a shared key. The proxy
/// resolves the Workload-Identity AWS credential to a Cosmos backend configured
/// with <c>authMode: workloadIdentity</c>, exchanges the federated token for an
/// AAD token, and runs the same item lifecycle the shared-key smoke covers. This
/// is the only end-to-end exercise of <c>WorkloadIdentityTokenSource</c> against
/// real Azure — emulators do not validate AAD, and Managed Identity is
/// untestable on GitHub-hosted runners (no IMDS). Skips unless the federated
/// token plus Cosmos endpoint/database are configured.
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
    public async Task Item_lifecycle_round_trips_via_workload_identity()
    {
        Skip.IfNot(_fx.CosmosWorkloadIdentityConfigured,
            "AZURE_FEDERATED_TOKEN_FILE/AZURE_TENANT_ID/AZURE_CLIENT_ID or AZURE_COSMOS_ENDPOINT/DATABASE not set — skipping real-Azure DynamoDB Workload Identity smoke.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..12];
        using var client = _fx.CreateDynamoDbClientWorkloadIdentity();
        var tableCreated = false;

        try
        {
            await client.CreateTableAsync(new CreateTableRequest
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

            await WaitForTableActiveAsync(client, table).ConfigureAwait(false);

            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = "item-1" },
                    ["payload"] = new AttributeValue { S = "real-azure-wi" },
                },
            }).ConfigureAwait(false);

            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            Assert.True(got.IsItemSet);
            Assert.Equal("real-azure-wi", got.Item["payload"].S);

            await client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
            }).ConfigureAwait(false);

            var afterDelete = await client.GetItemAsync(new GetItemRequest
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
                    await client.DeleteTableAsync(new DeleteTableRequest { TableName = table }).ConfigureAwait(false);
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
