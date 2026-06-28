using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Azure validation for DynamoDB TTL (issue #465). Exercises the control
/// plane (UpdateTimeToLive arms the live Cosmos container's <c>defaultTtl</c>,
/// DescribeTimeToLive reflects state) and the per-item write translation
/// (PutItem with a future epoch-seconds attribute round-trips while the item is
/// still live) against real Azure Cosmos DB. The actual background expiry sweep
/// is not asserted — Cosmos' TTL sweep cadence is non-deterministic and waiting
/// for it would make the test flaky/slow. Skips when AZURE_COSMOS_* is absent.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureTimeToLiveTests
{
    private readonly RealAzureProxyFixture _fx;

    public DynamoDbRealAzureTimeToLiveTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task Ttl_control_plane_and_item_translation_against_real_cosmos_db()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB TTL test.");

        const string ttlAttr = "expiresAt";
        var table = "ttl" + Guid.NewGuid().ToString("N")[..12];
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

            // Before enable: DescribeTimeToLive reports DISABLED.
            var before = await client.DescribeTimeToLiveAsync(
                new DescribeTimeToLiveRequest { TableName = table }).ConfigureAwait(false);
            Assert.Equal(TimeToLiveStatus.DISABLED, before.TimeToLiveDescription.TimeToLiveStatus);

            // Enable TTL — arms the live Cosmos container defaultTtl = -1.
            var update = await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = table,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    Enabled = true,
                    AttributeName = ttlAttr,
                },
            }).ConfigureAwait(false);
            Assert.True(update.TimeToLiveSpecification.Enabled);
            Assert.Equal(ttlAttr, update.TimeToLiveSpecification.AttributeName);

            // DescribeTimeToLive now reports ENABLED + the attribute name.
            var afterEnable = await client.DescribeTimeToLiveAsync(
                new DescribeTimeToLiveRequest { TableName = table }).ConfigureAwait(false);
            Assert.Equal(TimeToLiveStatus.ENABLED, afterEnable.TimeToLiveDescription.TimeToLiveStatus);
            Assert.Equal(ttlAttr, afterEnable.TimeToLiveDescription.AttributeName);

            // Per-item translation: a future epoch-seconds expiry is written to
            // the live container as a relative Cosmos ttl; the item stays live
            // (1h out) and round-trips through GetItem with the attribute intact.
            var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = "ttl-item" },
                    ["payload"] = new AttributeValue { S = "real-azure-ttl" },
                    [ttlAttr] = new AttributeValue { N = expiry.ToString() },
                },
            }).ConfigureAwait(false);

            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "ttl-item" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);
            Assert.True(got.IsItemSet);
            Assert.Equal("real-azure-ttl", got.Item["payload"].S);
            Assert.Equal(expiry.ToString(), got.Item[ttlAttr].N);

            // Disable TTL — removes the container defaultTtl; Describe reflects it.
            await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = table,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    Enabled = false,
                    AttributeName = ttlAttr,
                },
            }).ConfigureAwait(false);

            var afterDisable = await client.DescribeTimeToLiveAsync(
                new DescribeTimeToLiveRequest { TableName = table }).ConfigureAwait(false);
            Assert.Equal(TimeToLiveStatus.DISABLED, afterDisable.TimeToLiveDescription.TimeToLiveStatus);
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
                    // Best-effort cleanup for the real-Azure path.
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
