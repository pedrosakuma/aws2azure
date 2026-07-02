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

    /// <summary>
    /// A3 (#470): nested <c>ProjectionExpression</c> paths against live Cosmos.
    /// Projects a nested map member and a single list index; verifies the map is
    /// pruned to the referenced member and the list is compacted to the
    /// referenced element (positions not preserved), and unreferenced top-level
    /// attributes are dropped.
    /// </summary>
    [SkippableFact]
    public async Task Nested_projection_expression_prunes_map_and_list_against_real_cosmos_db()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB nested-projection smoke.");

        var table = "twp" + Guid.NewGuid().ToString("N")[..12];
        using var client = _fx.CreateDynamoDbClient();
        var tableCreated = false;

        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
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
                    ["m"] = new AttributeValue
                    {
                        M = new Dictionary<string, AttributeValue>
                        {
                            ["keep"] = new AttributeValue { S = "y" },
                            ["drop"] = new AttributeValue { S = "n" },
                        },
                    },
                    ["l"] = new AttributeValue
                    {
                        L =
                        [
                            new AttributeValue { S = "z0" },
                            new AttributeValue { S = "z1" },
                            new AttributeValue { S = "z2" },
                        ],
                    },
                },
            }).ConfigureAwait(false);

            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            var query = await client.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ProjectionExpression = "m.keep, l[2]",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":p"] = new AttributeValue { S = "item-1" },
                },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            Assert.True(got.IsItemSet);
            var item = Assert.Single(query.Items);

            // Unreferenced top-level attribute dropped.
            Assert.False(item.ContainsKey("pk"));

            // Map keeps only the referenced member.
            Assert.True(item.TryGetValue("m", out var m));
            Assert.True(m.M.ContainsKey("keep"));
            Assert.False(m.M.ContainsKey("drop"));
            Assert.Equal("y", m.M["keep"].S);

            // List is compacted to the single referenced index.
            Assert.True(item.TryGetValue("l", out var l));
            Assert.Single(l.L);
            Assert.Equal("z2", l.L[0].S);
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
}
