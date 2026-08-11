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


/// <summary>
/// Real-Cosmos certification source for the single-table, single-partition
/// transaction profile. These tests define evidence rows but do not stamp
/// operation or sub-feature seals until a workflow actually executes them.
/// </summary>
[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbTransactions")]
[Collection(RealAzureCollection.Name)]
public sealed partial class DynamoDbRealAzureTransactionTests(
    RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Atomic_write_rollback_conditions_and_cancellation_are_aligned()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            await PutAsync(client, table, "order-1", "gate", new()
            {
                ["state"] = S("open"),
            }, timeout.Token);
            await PutAsync(client, table, "order-1", "survivor", new()
            {
                ["value"] = S("keep"),
            }, timeout.Token);
            await PutAsync(client, table, "order-1", "utf8-order", new()
            {
                ["rank"] = S("\uE000"),
            }, timeout.Token);

            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Check(
                        table,
                        "order-1",
                        "utf8-order",
                        "#rank < :supplementary",
                        new()
                        {
                            [":supplementary"] = S("\U00010000"),
                        },
                        new()
                        {
                            ["#rank"] = "rank",
                        }),
                    Put(
                        table,
                        "order-1",
                        "utf8-order-peer",
                        "committed"),
                ],
            }, timeout.Token);
            Assert.True(await ExistsAsync(
                client,
                table,
                "order-1",
                "utf8-order-peer",
                timeout.Token));

            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Check(table, "order-1", "gate", "#state = :open", new()
                    {
                        [":open"] = S("open"),
                    }, new() { ["#state"] = "state" }),
                    Put(table, "order-1", "created-1", "committed"),
                    Put(table, "order-1", "created-2", "committed"),
                ],
            }, timeout.Token);
            Assert.True(await ExistsAsync(
                client,
                table,
                "order-1",
                "created-1",
                timeout.Token));
            Assert.True(await ExistsAsync(
                client,
                table,
                "order-1",
                "created-2",
                timeout.Token));
            await PutAsync(client, table, "order-1", "typed-ne", new()
            {
                ["flag"] = new AttributeValue { BOOL = true },
            }, timeout.Token);
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = Item(
                                "order-1",
                                "typed-ne",
                                "different-type-ne-committed"),
                            ConditionExpression = "flag <> :string",
                            ExpressionAttributeValues = new()
                            {
                                [":string"] = S("true"),
                            },
                        },
                    },
                    Put(
                        table,
                        "order-1",
                        "typed-ne-peer",
                        "committed"),
                ],
            }, timeout.Token);
            Assert.True(await ExistsAsync(
                client,
                table,
                "order-1",
                "typed-ne-peer",
                timeout.Token));
            var typedNotEqual = await ReadItemAsync(
                client,
                table,
                "order-1",
                "typed-ne",
                timeout.Token);
            Assert.Equal(
                "different-type-ne-committed",
                typedNotEqual["value"].S);

            var cancelled = await Assert.ThrowsAsync<TransactionCanceledException>(
                () => client.TransactWriteItemsAsync(new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        Check(table, "order-1", "gate", "#state = :closed", new()
                        {
                            [":closed"] = S("closed"),
                        }, new() { ["#state"] = "state" }),
                        new TransactWriteItem
                        {
                            Delete = new Delete
                            {
                                TableName = table,
                                Key = Key("order-1", "survivor"),
                            },
                        },
                        Put(table, "order-1", "rolled-back", "must-not-exist"),
                    ],
                }, timeout.Token));

            Assert.Equal(3, cancelled.CancellationReasons.Count);
            Assert.Equal(
                "ConditionalCheckFailed",
                cancelled.CancellationReasons[0].Code);
            Assert.Equal("None", cancelled.CancellationReasons[1].Code);
            Assert.Equal("None", cancelled.CancellationReasons[2].Code);
            Assert.True(await ExistsAsync(
                client,
                table,
                "order-1",
                "survivor",
                timeout.Token));
            Assert.False(await ExistsAsync(
                client,
                table,
                "order-1",
                "rolled-back",
                timeout.Token));
            var gate = await ReadItemAsync(
                client,
                table,
                "order-1",
                "gate",
                timeout.Token);
            Assert.Equal("open", gate["state"].S);

            var missingNotEqual = await Assert.ThrowsAsync<TransactionCanceledException>(
                () => client.TransactWriteItemsAsync(new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem
                        {
                            Put = new Put
                            {
                                TableName = table,
                                Item = Item(
                                    "order-1",
                                    "missing-ne",
                                    "must-not-commit"),
                                ConditionExpression = "missing <> :value",
                                ExpressionAttributeValues = new()
                                {
                                    [":value"] = S("x"),
                                },
                            },
                        },
                        Put(
                            table,
                            "order-1",
                            "missing-ne-peer",
                            "must-not-commit"),
                    ],
                }, timeout.Token));
            Assert.Equal(
                "ConditionalCheckFailed",
                missingNotEqual.CancellationReasons[0].Code);
            Assert.False(await ExistsAsync(
                client,
                table,
                "order-1",
                "missing-ne",
                timeout.Token));
            Assert.False(await ExistsAsync(
                client,
                table,
                "order-1",
                "missing-ne-peer",
                timeout.Token));
        }, timeout.Token);
    }

    [SkippableFact]
    public async Task Supported_condition_subset_and_write_kinds_commit_expected_state()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            const string partition = "conditions";
            await PutAsync(client, table, partition, "source", new()
            {
                ["text"] = S("mango"),
                ["prefix"] = S("prefix-value"),
                ["flag"] = new AttributeValue { BOOL = true },
                ["nil"] = new AttributeValue { NULL = true },
                ["count"] = new AttributeValue { N = "7" },
            }, timeout.Token);
            await PutAsync(client, table, partition, "delete-me", new()
            {
                ["marker"] = S("delete"),
            }, timeout.Token);

            var condition =
                "(#text = :wrong OR #text = :mango) "
                + "AND NOT (#text = :wrong) "
                + "AND #text BETWEEN :low AND :high "
                + "AND #text IN (:pear, :mango) "
                + "AND attribute_exists(#text) "
                + "AND attribute_not_exists(#missing) "
                + "AND begins_with(#prefix, :prefix) "
                + "AND attribute_type(#text, :typeS) "
                + "AND attribute_type(#flag, :typeBool) "
                + "AND attribute_type(#nil, :typeNull) "
                + "AND #flag = :true "
                + "AND #nil = :null "
                + "AND #count = :seven "
                + "AND #count <> :eight "
                + "AND #count IN (:six, :seven) "
                + "AND :low < #text "
                + "AND :seven = #count";
            var names = new Dictionary<string, string>
            {
                ["#text"] = "text",
                ["#missing"] = "missing",
                ["#prefix"] = "prefix",
                ["#flag"] = "flag",
                ["#nil"] = "nil",
                ["#count"] = "count",
            };
            var values = new Dictionary<string, AttributeValue>
            {
                [":wrong"] = S("wrong"),
                [":mango"] = S("mango"),
                [":low"] = S("apple"),
                [":high"] = S("zebra"),
                [":pear"] = S("pear"),
                [":prefix"] = S("prefix-"),
                [":typeS"] = S("S"),
                [":typeBool"] = S("BOOL"),
                [":typeNull"] = S("NULL"),
                [":true"] = new AttributeValue { BOOL = true },
                [":null"] = new AttributeValue { NULL = true },
                [":six"] = new AttributeValue { N = "6" },
                [":seven"] = new AttributeValue { N = "7" },
                [":eight"] = new AttributeValue { N = "8" },
            };

            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Check(
                        table,
                        partition,
                        "source",
                        condition,
                        values,
                        names),
                    new TransactWriteItem
                    {
                        Delete = new Delete
                        {
                            TableName = table,
                            Key = Key(partition, "delete-me"),
                            ConditionExpression = "attribute_exists(#marker)",
                            ExpressionAttributeNames = new()
                            {
                                ["#marker"] = "marker",
                            },
                        },
                    },
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = Item(
                                partition,
                                "created",
                                "committed"),
                            ConditionExpression =
                                "attribute_not_exists(#marker)",
                            ExpressionAttributeNames = new()
                            {
                                ["#marker"] = "marker",
                            },
                        },
                    },
                ],
            }, timeout.Token);

            var source = await ReadItemAsync(
                client,
                table,
                partition,
                "source",
                timeout.Token);
            Assert.Equal("mango", source["text"].S);
            Assert.True(source["flag"].BOOL);
            Assert.True(source["nil"].NULL);
            Assert.Equal("7", source["count"].N);
            Assert.False(await ExistsAsync(
                client,
                table,
                partition,
                "delete-me",
                timeout.Token));
            var created = await ReadItemAsync(
                client,
                table,
                partition,
                "created",
                timeout.Token);
            Assert.Equal("committed", created["value"].S);

            var unsupportedOrdering =
                await Assert.ThrowsAsync<AmazonDynamoDBException>(
                    () => client.TransactWriteItemsAsync(
                        new TransactWriteItemsRequest
                        {
                            TransactItems =
                            [
                                Check(
                                    table,
                                    partition,
                                    "source",
                                    "#count < :eight",
                                    new()
                                    {
                                        [":eight"] =
                                            new AttributeValue { N = "8" },
                                    },
                                    new() { ["#count"] = "count" }),
                                Put(
                                    table,
                                    partition,
                                    "numeric-ordering-peer",
                                    "must-not-commit"),
                            ],
                        },
                        timeout.Token));
            Assert.Equal("ValidationException", unsupportedOrdering.ErrorCode);
            Assert.False(await ExistsAsync(
                client,
                table,
                partition,
                "numeric-ordering-peer",
                timeout.Token));
            var sourceAfterRejection = await ReadItemAsync(
                client,
                table,
                partition,
                "source",
                timeout.Token);
            Assert.Equal("7", sourceAfterRejection["count"].N);

            var invalidNegatedBeginsWith =
                await Assert.ThrowsAsync<AmazonDynamoDBException>(
                    () => client.TransactWriteItemsAsync(
                        new TransactWriteItemsRequest
                        {
                            TransactItems =
                            [
                                Check(
                                    table,
                                    partition,
                                    "source",
                                    "NOT begins_with(#count, :prefix)",
                                    new()
                                    {
                                        [":prefix"] = S("7"),
                                    },
                                    new() { ["#count"] = "count" }),
                                Put(
                                    table,
                                    partition,
                                    "not-begins-with-peer",
                                    "must-not-commit"),
                            ],
                        },
                        timeout.Token));
            Assert.Equal(
                "ValidationException",
                invalidNegatedBeginsWith.ErrorCode);
            Assert.False(await ExistsAsync(
                client,
                table,
                partition,
                "not-begins-with-peer",
                timeout.Token));
            var sourceAfterTypeError = await ReadItemAsync(
                client,
                table,
                partition,
                "source",
                timeout.Token);
            Assert.Equal("7", sourceAfterTypeError["count"].N);
        }, timeout.Token);
    }

    [SkippableFact]
    public async Task Snapshot_reads_never_observe_mixed_transaction_versions()
    {
        SkipUnlessConfigured();
        using var readerClient = fixture.CreateDynamoDbClient();
        using var writerClient = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(readerClient, async table =>
        {
            const int itemCount = 72;
            const int minimumSamples = 12;
            const int maximumSamples = 36;
            var sortKeys = Enumerable.Range(0, itemCount)
                .Select(index => $"item-{index:D2}")
                .ToArray();
            await WriteVersionAsync(
                writerClient,
                table,
                "snapshot",
                sortKeys,
                "0",
                timeout.Token);

            using var overlap = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token);
            var firstCommit = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var committedVersion = 0;
            var observedVersions = new HashSet<string>(StringComparer.Ordinal);

            var writer = Task.Run(async () =>
            {
                try
                {
                    for (var version = 1; ; version++)
                    {
                        await WriteVersionAsync(
                            writerClient,
                            table,
                            "snapshot",
                            sortKeys,
                            version.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            overlap.Token);
                        Volatile.Write(ref committedVersion, version);
                        firstCommit.TrySetResult();
                    }
                }
                catch (OperationCanceledException)
                    when (overlap.IsCancellationRequested)
                {
                    firstCommit.TrySetCanceled(overlap.Token);
                }
                catch (Exception exception)
                {
                    firstCommit.TrySetException(exception);
                    overlap.Cancel();
                    throw;
                }
            }, CancellationToken.None);

            Exception? samplingFailure = null;
            Exception? writerFailure = null;
            try
            {
                await firstCommit.Task.WaitAsync(overlap.Token);
                var commitsAtSamplingStart =
                    Volatile.Read(ref committedVersion);
                var sampleCount = 0;
                while (sampleCount < maximumSamples)
                {
                    var response = await readerClient.TransactGetItemsAsync(
                        new TransactGetItemsRequest
                        {
                            TransactItems = sortKeys
                                .Select(sort => Get(
                                    table,
                                    "snapshot",
                                    sort))
                                .ToList(),
                        },
                        overlap.Token);

                    Assert.Equal(itemCount, response.Responses.Count);
                    var snapshotVersion =
                        response.Responses[0].Item["version"].S;
                    for (var index = 0; index < itemCount; index++)
                    {
                        var item = response.Responses[index].Item;
                        Assert.NotEmpty(item);
                        Assert.Equal("snapshot", item["pk"].S);
                        Assert.Equal(sortKeys[index], item["sk"].S);
                        Assert.Equal(snapshotVersion, item["version"].S);
                        Assert.DoesNotContain("payload", item.Keys);
                    }

                    Assert.True(
                        int.TryParse(
                            snapshotVersion,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out _),
                        $"Snapshot version '{snapshotVersion}' is not numeric.");
                    observedVersions.Add(snapshotVersion);
                    sampleCount++;

                    if (sampleCount >= minimumSamples
                        && Volatile.Read(ref committedVersion)
                           - commitsAtSamplingStart >= 6
                        && observedVersions.Count >= 4)
                    {
                        break;
                    }
                }

                var commitsDuringSampling =
                    Volatile.Read(ref committedVersion)
                    - commitsAtSamplingStart;
                Assert.True(
                    sampleCount >= minimumSamples,
                    $"Snapshot sampling completed only {sampleCount} reads.");
                Assert.True(
                    commitsDuringSampling >= 6,
                    $"Only {commitsDuringSampling} writer commits completed " +
                    "during snapshot sampling.");
                Assert.True(
                    observedVersions.Count >= 4,
                    $"Snapshot sampling observed only " +
                    $"{observedVersions.Count} distinct committed versions.");
            }
            catch (Exception exception)
            {
                samplingFailure = exception;
            }
            finally
            {
                overlap.Cancel();
                try
                {
                    await writer;
                }
                catch (Exception exception)
                {
                    writerFailure = exception;
                }
            }

            if (writerFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(writerFailure)
                    .Throw();
            }
            if (samplingFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(samplingFailure)
                    .Throw();
            }
        }, timeout.Token);
    }

}
