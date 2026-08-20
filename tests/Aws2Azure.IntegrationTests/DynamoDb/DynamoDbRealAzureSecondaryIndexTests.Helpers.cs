using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public sealed partial class DynamoDbRealAzureSecondaryIndexTests
{
    private static string NewTableName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static AttributeValue Str(string s) => new() { S = s };
    private static AttributeValue Num(int n) => new() { N = n.ToString(CultureInfo.InvariantCulture) };
    private static string Hex(string value) =>
        Convert.ToHexStringLower(Encoding.UTF8.GetBytes(value));

    private static Dictionary<string, AttributeValue> BaseItem(
        string pk, string sk, Dictionary<string, AttributeValue> extra)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = Str(pk),
            ["sk"] = Str(sk),
        };
        foreach (var kv in extra) item[kv.Key] = kv.Value;
        return item;
    }

    private static Task PutAsync(IAmazonDynamoDB client, string table, Dictionary<string, AttributeValue> item) =>
        client.PutItemAsync(new PutItemRequest { TableName = table, Item = item });

    /// <summary>
    /// Creates the indexed table, waits for it to be ACTIVE, runs the test body,
    /// then deletes the table (best-effort) regardless of outcome.
    /// </summary>
    private async Task WithTableAsync(IAmazonDynamoDB client, string table, Func<Task> body)
    {
        var created = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                    new AttributeDefinition("sk", ScalarAttributeType.S),
                    new AttributeDefinition("score", ScalarAttributeType.N),
                    new AttributeDefinition("customer", ScalarAttributeType.S),
                    new AttributeDefinition("category", ScalarAttributeType.S),
                    new AttributeDefinition("createdAt", ScalarAttributeType.S),
                    new AttributeDefinition("seq", ScalarAttributeType.N),
                ],
                KeySchema =
                [
                    new KeySchemaElement("pk", KeyType.HASH),
                    new KeySchemaElement("sk", KeyType.RANGE),
                ],
                LocalSecondaryIndexes =
                [
                    new LocalSecondaryIndex
                    {
                        IndexName = "byScore",
                        KeySchema =
                        [
                            new KeySchemaElement("pk", KeyType.HASH),
                            new KeySchemaElement("score", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ],
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCustomer",
                        KeySchema = [new KeySchemaElement("customer", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCustomerKeysOnly",
                        KeySchema = [new KeySchemaElement("customer", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCategory",
                        KeySchema =
                        [
                            new KeySchemaElement("category", KeyType.HASH),
                            new KeySchemaElement("createdAt", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCategoryNum",
                        KeySchema =
                        [
                            new KeySchemaElement("category", KeyType.HASH),
                            new KeySchemaElement("seq", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);
            created = true;

            await WaitForTableActiveAsync(client, table).ConfigureAwait(false);
            await body().ConfigureAwait(false);
        }
        finally
        {
            if (created)
            {
                try { await client.DeleteTableAsync(new DeleteTableRequest { TableName = table }).ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Runs a Query, aggregating LastEvaluatedKey pages, retrying until the
    /// result set reaches <paramref name="expectedCount"/> or the convergence
    /// deadline elapses — absorbing Cosmos asynchronous-indexing lag.
    /// </summary>
    private async Task<List<Dictionary<string, AttributeValue>>> QueryUntilAsync(
        IAmazonDynamoDB client, QueryRequest request, int expectedCount)
    {
        var deadline = DateTime.UtcNow + ConvergenceTimeout;
        List<Dictionary<string, AttributeValue>> last = new();
        while (true)
        {
            last = await DrainQueryAsync(client, request).ConfigureAwait(false);
            if (last.Count >= expectedCount || DateTime.UtcNow >= deadline)
            {
                _output.WriteLine($"Query {request.IndexName}: converged {last.Count}/{expectedCount}.");
                return last;
            }
            await Task.Delay(750).ConfigureAwait(false);
        }
    }

    private static async Task<List<Dictionary<string, AttributeValue>>> DrainQueryAsync(
        IAmazonDynamoDB client, QueryRequest request)
    {
        var all = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var page = await client.QueryAsync(new QueryRequest
            {
                TableName = request.TableName,
                IndexName = request.IndexName,
                KeyConditionExpression = request.KeyConditionExpression,
                FilterExpression = request.FilterExpression,
                ProjectionExpression = request.ProjectionExpression,
                ExpressionAttributeNames = request.ExpressionAttributeNames,
                ExpressionAttributeValues = request.ExpressionAttributeValues,
                ScanIndexForward = request.ScanIndexForward,
                ExclusiveStartKey = startKey,
            }).ConfigureAwait(false);
            all.AddRange(page.Items);
            startKey = page.LastEvaluatedKey is { Count: > 0 } lek ? lek : null;
        }
        while (startKey is not null);
        return all;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ScanUntilAsync(
        IAmazonDynamoDB client, ScanRequest request, int expectedCount)
    {
        var deadline = DateTime.UtcNow + ConvergenceTimeout;
        while (true)
        {
            var all = await DrainScanAsync(client, request).ConfigureAwait(false);
            if (all.Count >= expectedCount || DateTime.UtcNow >= deadline)
            {
                _output.WriteLine($"Scan {request.IndexName}: converged {all.Count}/{expectedCount}.");
                return all;
            }
            await Task.Delay(750).ConfigureAwait(false);
        }
    }

    private static async Task<List<Dictionary<string, AttributeValue>>> DrainScanAsync(
        IAmazonDynamoDB client, ScanRequest request)
    {
        var all = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var page = await client.ScanAsync(new ScanRequest
            {
                TableName = request.TableName,
                IndexName = request.IndexName,
                FilterExpression = request.FilterExpression,
                ExpressionAttributeValues = request.ExpressionAttributeValues,
                Limit = request.Limit,
                ExclusiveStartKey = startKey,
            }).ConfigureAwait(false);
            all.AddRange(page.Items);
            startKey = page.LastEvaluatedKey is { Count: > 0 } lek ? lek : null;
        }
        while (startKey is not null);
        return all;
    }

    private static async Task WaitForTableActiveAsync(IAmazonDynamoDB client, string table)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var desc = await client.DescribeTableAsync(table).ConfigureAwait(false);
                if (desc.Table.TableStatus == TableStatus.ACTIVE) return;
            }
            catch (ResourceNotFoundException)
            {
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }
}
