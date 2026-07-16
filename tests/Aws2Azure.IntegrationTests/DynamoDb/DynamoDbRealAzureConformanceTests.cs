using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Batch_operations_round_trip_against_real_cosmos()
    {
        Skip.IfNot(fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB conformance.");

        var table = "tbatch" + Guid.NewGuid().ToString("N")[..12];
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var tableCreated = false;

        try
        {
            await CreateTableAsync(client, table, timeout.Token).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, timeout.Token).ConfigureAwait(false);

            var write = await client.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    [table] = Enumerable.Range(0, 3).Select(i => new WriteRequest
                    {
                        PutRequest = new PutRequest
                        {
                            Item = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new() { S = $"item-{i}" },
                                ["payload"] = new() { S = $"value-{i}" },
                            },
                        },
                    }).ToList(),
                },
            }, timeout.Token).ConfigureAwait(false);
            Assert.Empty(write.UnprocessedItems);

            var read = await client.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new Dictionary<string, KeysAndAttributes>
                {
                    [table] = new()
                    {
                        ConsistentRead = true,
                        Keys = Enumerable.Range(0, 3)
                            .Select(i => new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new() { S = $"item-{i}" },
                            })
                            .ToList(),
                    },
                },
            }, timeout.Token).ConfigureAwait(false);

            Assert.Empty(read.UnprocessedKeys);
            Assert.Equal(
                new[] { "item-0", "item-1", "item-2" },
                read.Responses[table].Select(item => item["pk"].S).Order(StringComparer.Ordinal).ToArray());

            var mutate = await client.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    [table] =
                    [
                        new WriteRequest
                        {
                            DeleteRequest = new DeleteRequest
                            {
                                Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = "item-0" } },
                            },
                        },
                        new WriteRequest
                        {
                            DeleteRequest = new DeleteRequest
                            {
                                Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = "item-1" } },
                            },
                        },
                        new WriteRequest
                        {
                            PutRequest = new PutRequest
                            {
                                Item = new Dictionary<string, AttributeValue>
                                {
                                    ["pk"] = new() { S = "item-3" },
                                    ["payload"] = new() { S = "value-3" },
                                },
                            },
                        },
                    ],
                },
            }, timeout.Token).ConfigureAwait(false);
            Assert.Empty(mutate.UnprocessedItems);

            var verify = await client.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new Dictionary<string, KeysAndAttributes>
                {
                    [table] = new()
                    {
                        ConsistentRead = true,
                        Keys = Enumerable.Range(0, 4)
                            .Select(i => new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new() { S = $"item-{i}" },
                            })
                            .ToList(),
                    },
                },
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(
                new[] { "item-2", "item-3" },
                verify.Responses[table].Select(item => item["pk"].S).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            if (tableCreated)
            {
                try { await client.DeleteTableAsync(table).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task Concurrent_conditional_updates_admit_one_winner()
    {
        Skip.IfNot(fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB conformance.");

        var table = "tconcur" + Guid.NewGuid().ToString("N")[..12];
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var tableCreated = false;

        try
        {
            await CreateTableAsync(client, table, timeout.Token).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, timeout.Token).ConfigureAwait(false);
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = "race" },
                    ["state"] = new() { S = "open" },
                },
            }, timeout.Token).ConfigureAwait(false);

            var contenders = Enumerable.Range(0, 6)
                .Select(i => TryConditionalUpdateAsync(client, table, $"contender-{i}", timeout.Token))
                .ToArray();
            var outcomes = await Task.WhenAll(contenders).ConfigureAwait(false);
            Assert.Single(outcomes.Where(won => won));

            var item = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                ConsistentRead = true,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = "race" } },
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal("closed", item.Item["state"].S);
            Assert.StartsWith("contender-", item.Item["winner"].S, StringComparison.Ordinal);
        }
        finally
        {
            if (tableCreated)
            {
                try { await client.DeleteTableAsync(table).ConfigureAwait(false); } catch { }
            }
        }
    }

    private static Task<CreateTableResponse> CreateTableAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
        => client.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }, cancellationToken);

    private static async Task<bool> TryConditionalUpdateAsync(
        IAmazonDynamoDB client,
        string table,
        string contender,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = "race" } },
                UpdateExpression = "SET #state = :closed, winner = :winner",
                ConditionExpression = "#state = :open",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#state"] = "state" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":open"] = new() { S = "open" },
                    [":closed"] = new() { S = "closed" },
                    [":winner"] = new() { S = contender },
                },
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private static async Task WaitForTableActiveAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await client.DescribeTableAsync(table, cancellationToken).ConfigureAwait(false);
                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Table '{table}' did not become active.");
    }
}
