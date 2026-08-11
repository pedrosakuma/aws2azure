using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Aws2Azure.TestSupport.OperationalQualification;
using System.Text.Json;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public sealed partial class DynamoDbPersistedFormatMigrationTests
{
    private static async Task PutFixtureItemAsync(
        AmazonDynamoDBClient client,
        string table,
        string sortKey,
        string expiry,
        string writer)
    {
        await client.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = "partition-1" },
                ["sk"] = new() { S = sortKey },
                ["gpk"] = new() { S = "amounts" },
                ["gsk"] = new() { N = "99999999999999999999999999999999999" + sortKey },
                ["id"] = new() { S = "user-id-" + sortKey },
                ["ttl"] = new() { N = expiry },
                ["blob"] = new() { B = new MemoryStream([1, 2, 3, 4]) },
                ["labels"] = new() { SS = ["alpha", "beta"] },
                ["writer"] = new() { S = writer },
            },
        }).ConfigureAwait(false);
    }

    private static async Task WriteTransactionPairAsync(
        AmazonDynamoDBClient client,
        string table,
        string writer)
    {
        await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                TransactionPut(table, "txn-left", writer),
                TransactionPut(table, "txn-right", writer),
            ],
        }).ConfigureAwait(false);
    }

    private static async Task AssertTransactionPairAsync(
        AmazonDynamoDBClient client,
        string table,
        string writer)
    {
        var response = await client.TransactGetItemsAsync(
            new TransactGetItemsRequest
            {
                TransactItems =
                [
                    TransactionGet(table, "txn-left"),
                    TransactionGet(table, "txn-right"),
                ],
            }).ConfigureAwait(false);
        Assert.Equal(2, response.Responses.Count);
        Assert.All(
            response.Responses,
            item => Assert.Equal(writer, item.Item["writer"].S));
    }

    private static TransactWriteItem TransactionPut(
        string table,
        string sortKey,
        string writer)
        => new()
        {
            Put = new Put
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = "partition-1" },
                    ["sk"] = new() { S = sortKey },
                    ["writer"] = new() { S = writer },
                },
            },
        };

    private static TransactGetItem TransactionGet(
        string table,
        string sortKey)
        => new()
        {
            Get = new Get
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = "partition-1" },
                    ["sk"] = new() { S = sortKey },
                },
            },
        };

    private static string BuildLegacyDocument(JsonElement frozen, string futureExpiry)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in frozen.EnumerateObject())
            {
                if (property.Name is "_rid" or "_etag")
                {
                    continue;
                }
                writer.WritePropertyName(property.Name);
                if (property.Name == "item")
                {
                    writer.WriteStartObject();
                    foreach (var attribute in property.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(attribute.Name);
                        if (attribute.Name == "ttl")
                        {
                            writer.WriteStartObject();
                            writer.WriteString("N", futureExpiry);
                            writer.WriteEndObject();
                        }
                        else
                        {
                            attribute.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Dictionary<string, AttributeValue> ToAttributeMap(
        Dictionary<string, JsonElement> values)
    {
        var result = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var typed = pair.Value;
            if (typed.TryGetProperty("S", out var text))
            {
                result[pair.Key] = new AttributeValue { S = text.GetString() };
            }
            else if (typed.TryGetProperty("N", out var number))
            {
                result[pair.Key] = new AttributeValue { N = number.GetString() };
            }
            else if (typed.TryGetProperty("B", out var binary))
            {
                result[pair.Key] = new AttributeValue
                {
                    B = new MemoryStream(Convert.FromBase64String(binary.GetString()!)),
                };
            }
            else
            {
                throw new InvalidOperationException(
                    $"Migration fixture attribute {pair.Key} has an unsupported type.");
            }
        }
        return result;
    }

    private static void AssertLegacyPayloadEqual(string before, string after)
    {
        using var beforeDocument = JsonDocument.Parse(before);
        using var afterDocument = JsonDocument.Parse(after);
        Assert.Equal(
            beforeDocument.RootElement.GetProperty("pk").GetString(),
            afterDocument.RootElement.GetProperty("pk").GetString());
        Assert.Equal(
            beforeDocument.RootElement.GetProperty("item").GetRawText(),
            afterDocument.RootElement.GetProperty("item").GetRawText());
    }

    private static async Task AssertFixtureItemAsync(
        AmazonDynamoDBClient client,
        string table,
        string sortKey,
        string writer)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(sortKey),
            ConsistentRead = true,
        }).ConfigureAwait(false);

        Assert.Equal(writer, response.Item["writer"].S);
        Assert.Equal("user-id-" + sortKey, response.Item["id"].S);
        Assert.Equal(
            "99999999999999999999999999999999999" + sortKey,
            response.Item["gsk"].N);
        Assert.Equal(4, response.Item["blob"].B.Length);
        Assert.Equal(["alpha", "beta"], response.Item["labels"].SS);
    }

    private static Dictionary<string, AttributeValue> Key(string sortKey) => new()
    {
        ["pk"] = new() { S = "partition-1" },
        ["sk"] = new() { S = sortKey },
    };

    private static string ItemSortKey(List<Dictionary<string, AttributeValue>> items)
    {
        Assert.Single(items);
        return items[0]["sk"].S;
    }

    private static Task<QueryResponse> QueryPageAsync(
        AmazonDynamoDBClient client,
        string table,
        Dictionary<string, AttributeValue>? exclusiveStartKey) =>
        client.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pk"] = new() { S = "partition-1" },
            },
            ExclusiveStartKey = exclusiveStartKey,
            Limit = 1,
        });

    private static Task<ScanResponse> ScanPageAsync(
        AmazonDynamoDBClient client,
        string table,
        Dictionary<string, AttributeValue>? exclusiveStartKey) =>
        client.ScanAsync(new ScanRequest
        {
            TableName = table,
            ExclusiveStartKey = exclusiveStartKey,
            Limit = 1,
        });

    private static Task<QueryResponse> OrderedQueryPageAsync(
        AmazonDynamoDBClient client,
        string table,
        Dictionary<string, AttributeValue>? exclusiveStartKey) =>
        client.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "byAmount",
            KeyConditionExpression = "gpk = :gpk",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":gpk"] = new() { S = "amounts" },
            },
            ExclusiveStartKey = exclusiveStartKey,
            Limit = 1,
            ScanIndexForward = true,
        });

    private static async Task WaitForActiveAsync(
        AmazonDynamoDBClient client,
        string table)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var response = await client.DescribeTableAsync(
                new DescribeTableRequest { TableName = table }).ConfigureAwait(false);
            if (response.Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        throw new TimeoutException($"Table {table} did not become ACTIVE.");
    }
}
