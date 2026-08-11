using System.Diagnostics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public sealed partial class DynamoDbRealAzureLoadQualificationTests
{
    private static async Task RunWorkerAsync(
        IAmazonDynamoDB client,
        RealAzureWorkloadLoadTracker tracker,
        CompletedIterationCounter completedIterations,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var table = $"a2a-load-{worker:x2}-{Guid.NewGuid():N}"[..40];
        var tableCreated = false;
        var iteration = 0;
        try
        {
            await MeasureAsync(tracker, "CreateTable", async () =>
            {
                await client.CreateTableAsync(new CreateTableRequest
                {
                    TableName = table,
                    AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                }, cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, cancellationToken).ConfigureAwait(false);

            await MeasureAsync(tracker, "DescribeTable", async () =>
            {
                var description = await client.DescribeTableAsync(
                    table,
                    cancellationToken).ConfigureAwait(false);
                if (description.Table.TableStatus != TableStatus.ACTIVE)
                {
                    throw new InvalidDataException("DescribeTable did not report an ACTIVE table.");
                }
            }, IsThrottle).ConfigureAwait(false);

            while (stopwatch.Elapsed < duration)
            {
                completedIterations.RecordStarted();
                var key = $"item-worker-{worker:D2}-{iteration++:D8}";
                var payload = $"aws2azure production-shaped DynamoDB load {key} {new string('x', 4_096)}";
                var itemCreated = false;
                try
                {
                    await MeasureAsync(tracker, "PutItem", async () =>
                    {
                        await client.PutItemAsync(new PutItemRequest
                        {
                            TableName = table,
                            Item = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                                ["payload"] = new AttributeValue { S = payload },
                                ["version"] = new AttributeValue { N = "1" },
                            },
                        }, cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);
                    itemCreated = true;

                    await MeasureAsync(tracker, "GetItem", async () =>
                    {
                        var response = await client.GetItemAsync(new GetItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                            // ConsistentRead=true exercises the Strong-consistency
                            // path this profile requires (issue #627).
                            ConsistentRead = true,
                        }, cancellationToken).ConfigureAwait(false);
                        if (!response.IsItemSet
                            || response.Item["payload"].S != payload)
                        {
                            throw new InvalidDataException("GetItem returned the wrong payload.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "UpdateItem", async () =>
                    {
                        await client.UpdateItemAsync(new UpdateItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                            UpdateExpression = "SET version = version + :one",
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                            {
                                [":one"] = new AttributeValue { N = "1" },
                            },
                        }, cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "GetItem", async () =>
                    {
                        var response = await client.GetItemAsync(new GetItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                            ConsistentRead = true,
                        }, cancellationToken).ConfigureAwait(false);
                        if (!response.IsItemSet || response.Item["version"].N != "2")
                        {
                            throw new InvalidDataException("UpdateItem did not persist the expected version.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "DeleteItem", async () =>
                    {
                        await client.DeleteItemAsync(new DeleteItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                        }, cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);
                    itemCreated = false;

                    // DeleteItem is idempotent (a repeat delete of an absent item
                    // still succeeds), so wrapping a second call in
                    // CompleteAfterAsync marks "iteration complete" strictly after
                    // a genuine measured backend round trip, decoupled from the
                    // main per-op measurement chain above (mirrors the S3 load
                    // producer's DeleteObject completion marker).
                    await completedIterations.CompleteAfterAsync(() => MeasureAsync(
                        tracker,
                        "DeleteItem",
                        async () =>
                        {
                            await client.DeleteItemAsync(new DeleteItemRequest
                            {
                                TableName = table,
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    ["pk"] = new AttributeValue { S = key },
                                },
                            }, cancellationToken).ConfigureAwait(false);
                        },
                        IsThrottle)).ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    if (itemCreated)
                    {
                        try
                        {
                            await client.DeleteItemAsync(new DeleteItemRequest
                            {
                                TableName = table,
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    ["pk"] = new AttributeValue { S = key },
                                },
                            }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            await MeasureAsync(tracker, "DeleteTable", async () =>
            {
                await client.DeleteTableAsync(
                    new DeleteTableRequest { TableName = table },
                    cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            tableCreated = false;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
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
