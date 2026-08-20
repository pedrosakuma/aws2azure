using System.Diagnostics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public sealed partial class DynamoDbRealAzureLoadQualificationTests
{
    private static async Task<RealAzureWorkloadLoadScenario> VerifyScenarioAsync(
        string id,
        string operation,
        string evidenceSource,
        Func<Task> verification)
    {
        var started = Stopwatch.GetTimestamp();
        await verification().ConfigureAwait(false);
        return Scenario(
            id,
            Service,
            operation,
            evidenceSource,
            1,
            0,
            0,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Concurrent conditional writes must admit exactly one winner: N workers
    /// race a <c>PutItem</c> with <c>attribute_not_exists(pk)</c> against the
    /// same key; exactly one succeeds and the rest fail with
    /// <c>ConditionalCheckFailedException</c>.
    /// </summary>
    private static async Task VerifyConditionalWriteConcurrencyAsync(
        IAmazonDynamoDB client,
        CancellationToken cancellationToken)
    {
        var table = "a2a-cwc-" + Guid.NewGuid().ToString("N")[..20];
        const string key = "contested";
        const int racers = 8;
        var tableCreated = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }, cancellationToken).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, cancellationToken).ConfigureAwait(false);

            var results = await Task.WhenAll(Enumerable.Range(0, racers).Select(async racer =>
            {
                try
                {
                    await client.PutItemAsync(new PutItemRequest
                    {
                        TableName = table,
                        Item = new Dictionary<string, AttributeValue>
                        {
                            ["pk"] = new AttributeValue { S = key },
                            ["winner"] = new AttributeValue { N = racer.ToString() },
                        },
                        ConditionExpression = "attribute_not_exists(pk)",
                    }, cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (ConditionalCheckFailedException)
                {
                    return false;
                }
            })).ConfigureAwait(false);

            var winners = results.Count(won => won);
            if (winners != 1)
            {
                throw new InvalidDataException(
                    $"Conditional-write concurrency admitted {winners} winners; expected exactly 1.");
            }

            var final = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = key } },
                ConsistentRead = true,
            }, cancellationToken).ConfigureAwait(false);
            if (!final.IsItemSet)
            {
                throw new InvalidDataException(
                    "Conditional-write concurrency winner did not persist an item.");
            }
        }
        finally
        {
            if (tableCreated)
            {
                try
                {
                    await client.DeleteTableAsync(
                        new DeleteTableRequest { TableName = table },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Strong-consistency read-after-write: a <c>PutItem</c> immediately
    /// followed by a <c>ConsistentRead=true</c> <c>GetItem</c> must observe
    /// the write with no propagation delay (issue #627's Strong-consistency
    /// requirement for the CRUD mix).
    /// </summary>
    private static async Task VerifyReadAfterWriteAsync(
        IAmazonDynamoDB client,
        CancellationToken cancellationToken)
    {
        var table = "a2a-raw-" + Guid.NewGuid().ToString("N")[..20];
        const string key = "item";
        var value = "read-after-write-" + Guid.NewGuid().ToString("N");
        var tableCreated = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }, cancellationToken).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, cancellationToken).ConfigureAwait(false);

            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = key },
                    ["payload"] = new AttributeValue { S = value },
                },
            }, cancellationToken).ConfigureAwait(false);

            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = key } },
                ConsistentRead = true,
            }, cancellationToken).ConfigureAwait(false);
            if (!got.IsItemSet || got.Item["payload"].S != value)
            {
                throw new InvalidDataException(
                    "Strong-consistency read-after-write did not observe the immediately preceding write.");
            }
        }
        finally
        {
            if (tableCreated)
            {
                try
                {
                    await client.DeleteTableAsync(
                        new DeleteTableRequest { TableName = table },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

}
