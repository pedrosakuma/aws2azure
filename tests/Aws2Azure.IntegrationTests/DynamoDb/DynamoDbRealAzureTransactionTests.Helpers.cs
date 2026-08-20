using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using TransactionHandler =
    Aws2Azure.Modules.DynamoDb.Operations.TransactWriteItemsHandler;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public sealed partial class DynamoDbRealAzureTransactionTests
{
    private async Task<RawResponse> SendRawAsync(
        string operation,
        string body,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = DynamoDbRequestBuilder.Build(
            operation,
            body,
            RealAzureProxyFixture.AwsAccessKey,
            RealAzureProxyFixture.AwsSecret,
            new Uri(fixture.GetServiceUrl("dynamodb")));
        using var response = await http.SendAsync(
            request,
            cancellationToken);
        return new RawResponse(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task SendRawAndDiscardResponseAsync(
        string operation,
        string body,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = DynamoDbRequestBuilder.Build(
            operation,
            body,
            RealAzureProxyFixture.AwsAccessKey,
            RealAzureProxyFixture.AwsSecret,
            new Uri(fixture.GetServiceUrl("dynamodb")));
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private static string BuildRawIdempotentPut(
        string table,
        string partition,
        string sort,
        string token,
        string number,
        bool reordered)
    {
        if (!reordered)
        {
            return $$"""
                {
                  "ClientRequestToken": "{{token}}",
                  "TransactItems": [{
                    "Put": {
                      "TableName": "{{table}}",
                      "Item": {
                        "pk": { "S": "{{partition}}" },
                        "sk": { "S": "{{sort}}" },
                        "value": { "N": "{{number}}" },
                        "marker": { "S": "committed" }
                      },
                      "ConditionExpression": "attribute_not_exists(#marker)",
                      "ExpressionAttributeNames": { "#marker": "marker" }
                    }
                  }]
                }
                """;
        }

        return $$"""
            {
              "ReturnConsumedCapacity": "NONE",
              "TransactItems": [{
                "Put": {
                  "ExpressionAttributeNames": { "#m": "marker" },
                  "ConditionExpression": "attribute_not_exists(#m)",
                  "Item": {
                    "marker": { "S": "committed" },
                    "value": { "N": "{{number}}" },
                    "sk": { "S": "{{sort}}" },
                    "pk": { "S": "{{partition}}" }
                  },
                  "TableName": "{{table}}"
                }
              }],
              "ClientRequestToken": "{{token}}"
            }
            """;
    }

    private void SkipUnlessConfigured()
        => Skip.IfNot(
            fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB transaction certification.");

    private static async Task WithTableAsync(
        IAmazonDynamoDB client,
        Func<string, Task> action,
        CancellationToken cancellationToken)
    {
        var table = "ttxn" + Guid.NewGuid().ToString("N")[..12];
        var created = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new("pk", ScalarAttributeType.S),
                    new("sk", ScalarAttributeType.S),
                ],
                KeySchema =
                [
                    new("pk", KeyType.HASH),
                    new("sk", KeyType.RANGE),
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }, cancellationToken);
            created = true;
            await WaitForActiveAsync(client, table, cancellationToken);
            await action(table);
        }
        finally
        {
            if (created)
            {
                try
                {
                    await client.DeleteTableAsync(
                        new DeleteTableRequest { TableName = table },
                        CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task WaitForActiveAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await client.DescribeTableAsync(
                    table,
                    cancellationToken);
                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"Table '{table}' did not become active.");
    }

    private static async Task WriteVersionAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        IReadOnlyList<string> sortKeys,
        string version,
        CancellationToken cancellationToken)
        => await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = sortKeys
                .Select(sort => Put(
                    table,
                    partition,
                    sort,
                    version,
                    "version"))
                .ToList(),
        }, cancellationToken);

    private static async Task<bool> TryWinAsync(
        IAmazonDynamoDB client,
        string table,
        int contender,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = new()
                            {
                                ["pk"] = S("race"),
                                ["sk"] = S("gate"),
                                ["state"] = S("closed"),
                                ["winner"] = S($"winner-{contender}"),
                            },
                            ConditionExpression = "#state = :open",
                            ExpressionAttributeNames = new()
                            {
                                ["#state"] = "state",
                            },
                            ExpressionAttributeValues = new()
                            {
                                [":open"] = S("open"),
                            },
                        },
                    },
                    Put(
                        table,
                        "race",
                        $"audit-{contender}",
                        $"winner-{contender}"),
                ],
            }, cancellationToken);
            return true;
        }
        catch (TransactionCanceledException exception)
        {
            Assert.Equal(2, exception.CancellationReasons.Count);
            Assert.Equal(
                "ConditionalCheckFailed",
                exception.CancellationReasons[0].Code);
            Assert.Equal("None", exception.CancellationReasons[1].Code);
            return false;
        }
    }

    private static TransactWriteItem Check(
        string table,
        string partition,
        string sort,
        string condition,
        Dictionary<string, AttributeValue> values,
        Dictionary<string, string>? names = null)
        => new()
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = table,
                Key = Key(partition, sort),
                ConditionExpression = condition,
                ExpressionAttributeValues = values,
                ExpressionAttributeNames = names,
            },
        };

    private static TransactWriteItem Put(
        string table,
        string partition,
        string sort,
        string value,
        string valueName = "value")
        => new()
        {
            Put = new Put
            {
                TableName = table,
                Item = Item(partition, sort, value, valueName),
            },
        };

    private static TransactGetItem Get(
        string table,
        string partition,
        string sort)
        => new()
        {
            Get = new Get
            {
                TableName = table,
                Key = Key(partition, sort),
                ProjectionExpression = "pk, sk, version, #v",
                ExpressionAttributeNames = new()
                {
                    ["#v"] = "value",
                },
            },
        };

    private static Dictionary<string, AttributeValue> Item(
        string partition,
        string sort,
        string value,
        string valueName = "value")
        => new()
        {
            ["pk"] = S(partition),
            ["sk"] = S(sort),
            [valueName] = S(value),
            ["payload"] = S("projection-must-remove"),
        };

    private static Dictionary<string, AttributeValue> Key(
        string partition,
        string sort)
        => new()
        {
            ["pk"] = S(partition),
            ["sk"] = S(sort),
        };

    private static async Task PutAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        string sort,
        Dictionary<string, AttributeValue> attributes,
        CancellationToken cancellationToken)
    {
        attributes["pk"] = S(partition);
        attributes["sk"] = S(sort);
        await client.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = attributes,
        }, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        string sort,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(partition, sort),
            ConsistentRead = true,
        }, cancellationToken);
        return response.Item is { Count: > 0 };
    }

    private static async Task<Dictionary<string, AttributeValue>> ReadItemAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        string sort,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(partition, sort),
            ConsistentRead = true,
        }, cancellationToken);
        Assert.NotEmpty(response.Item);
        return response.Item;
    }

    private static AttributeValue S(string value) => new() { S = value };

    private readonly record struct RawResponse(
        HttpStatusCode Status,
        string Body);
}

