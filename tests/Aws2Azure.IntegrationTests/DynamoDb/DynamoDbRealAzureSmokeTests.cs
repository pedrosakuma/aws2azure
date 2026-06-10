using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Azure nightly smoke for the DynamoDB module (issue #153): a full
/// CreateTable → PutItem → GetItem → DeleteItem → DeleteTable cycle against
/// live Azure Cosmos DB. The Cosmos database must already exist
/// (<c>AZURE_COSMOS_DATABASE</c>) because the module provisions containers but
/// not the database; the test owns the table (container) lifecycle. Skips when
/// the <c>AZURE_COSMOS_*</c> secrets are absent.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureSmokeTests
{
    private readonly RealAzureProxyFixture _fx;

    public DynamoDbRealAzureSmokeTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task Item_lifecycle_round_trips_against_real_cosmos_db()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB smoke.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..12];
        using var client = _fx.CreateDynamoDbClient();
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
                    ["payload"] = new AttributeValue { S = "real-azure" },
                },
            }).ConfigureAwait(false);

            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            Assert.True(got.IsItemSet);
            Assert.Equal("real-azure", got.Item["payload"].S);

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
