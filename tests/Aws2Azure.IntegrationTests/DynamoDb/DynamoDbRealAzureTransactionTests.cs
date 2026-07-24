using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Cosmos certification source for the single-table, single-partition
/// transaction profile. These tests define evidence rows but do not stamp
/// operation or sub-feature seals until a workflow actually executes them.
/// </summary>
[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbTransactions")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureTransactionTests(
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
                "missing-ne-peer",
                timeout.Token));
        }, timeout.Token);
    }

    [SkippableFact]
    public async Task Snapshot_reads_never_observe_mixed_transaction_versions()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            await WriteVersionAsync(client, table, "snapshot", "0", timeout.Token);

            var writer = Task.Run(async () =>
            {
                for (var version = 1; version <= 60; version++)
                {
                    await WriteVersionAsync(
                        client,
                        table,
                        "snapshot",
                        version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        timeout.Token);
                }
            }, timeout.Token);

            for (var sample = 0; sample < 120; sample++)
            {
                var response = await client.TransactGetItemsAsync(
                    new TransactGetItemsRequest
                    {
                        TransactItems =
                        [
                            Get(table, "snapshot", "left"),
                            Get(table, "snapshot", "right"),
                        ],
                    },
                    timeout.Token);
                Assert.Equal(2, response.Responses.Count);
                var left = response.Responses[0].Item;
                var right = response.Responses[1].Item;
                Assert.Equal(left["version"].S, right["version"].S);
                Assert.Equal("left", left["sk"].S);
                Assert.Equal("right", right["sk"].S);
                Assert.DoesNotContain("payload", left.Keys);
                Assert.DoesNotContain("payload", right.Keys);
            }

            await writer;
        }, timeout.Token);
    }

    [SkippableFact]
    public async Task Contending_transactions_admit_one_winner_and_one_audit()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            await PutAsync(client, table, "race", "gate", new()
            {
                ["state"] = S("open"),
            }, timeout.Token);

            var contenders = Enumerable.Range(0, 8)
                .Select(index => TryWinAsync(
                    client,
                    table,
                    index,
                    timeout.Token))
                .ToArray();
            var outcomes = await Task.WhenAll(contenders);

            Assert.Single(outcomes.Where(outcome => outcome));
            var gate = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                ConsistentRead = true,
                Key = Key("race", "gate"),
            }, timeout.Token);
            Assert.Equal("closed", gate.Item["state"].S);
            Assert.StartsWith(
                "winner-",
                gate.Item["winner"].S,
                StringComparison.Ordinal);

            var query = await client.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = new()
                {
                    [":pk"] = S("race"),
                },
                ConsistentRead = true,
            }, timeout.Token);
            Assert.Equal(
                1,
                query.Items.Count(item => item["sk"].S.StartsWith(
                    "audit-",
                    StringComparison.Ordinal)));
        }, timeout.Token);
    }

    [SkippableFact]
    public async Task Scope_token_rejection_and_versioned_sprocs_survive_restart()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            var tokenRequest = new TransactWriteItemsRequest
            {
                ClientRequestToken = "explicit-token",
                TransactItems =
                [
                    Put(table, "restart", "token", "must-not-commit"),
                ],
            };
            var tokenError = await Assert.ThrowsAsync<AmazonDynamoDBException>(
                () => client.TransactWriteItemsAsync(
                    tokenRequest,
                    timeout.Token));
            Assert.Equal("ValidationException", tokenError.ErrorCode);
            Assert.Contains("ClientRequestToken", tokenError.Message);

            var scopeRequest = new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Put(table, "partition-a", "one", "must-not-commit"),
                    Put(table, "partition-b", "two", "must-not-commit"),
                ],
            };
            var scopeError = await Assert.ThrowsAsync<AmazonDynamoDBException>(
                () => client.TransactWriteItemsAsync(
                    scopeRequest,
                    timeout.Token));
            Assert.Equal("ValidationException", scopeError.ErrorCode);
            Assert.False(await ExistsAsync(
                client,
                table,
                "partition-a",
                "one",
                timeout.Token));
            Assert.False(await ExistsAsync(
                client,
                table,
                "partition-b",
                "two",
                timeout.Token));

            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Put(table, "restart", "one", "before"),
                    Put(table, "restart", "two", "before"),
                ],
            }, timeout.Token);
            var beforeRestart = await client.TransactGetItemsAsync(
                new TransactGetItemsRequest
                {
                    TransactItems =
                    [
                        Get(table, "restart", "one"),
                        Get(table, "restart", "two"),
                    ],
                },
                timeout.Token);
            Assert.All(
                beforeRestart.Responses,
                response => Assert.Equal("before", response.Item["value"].S));

            await fixture.RestartAsync();

            var snapshot = await client.TransactGetItemsAsync(
                new TransactGetItemsRequest
                {
                    TransactItems =
                    [
                        Get(table, "restart", "one"),
                        Get(table, "restart", "two"),
                    ],
                },
                timeout.Token);
            Assert.All(
                snapshot.Responses,
                response => Assert.Equal("before", response.Item["value"].S));

            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Put(table, "restart", "one", "after"),
                    Put(table, "restart", "two", "after"),
                ],
            }, timeout.Token);

            var restartedTokenError =
                await Assert.ThrowsAsync<AmazonDynamoDBException>(
                () => client.TransactWriteItemsAsync(
                    tokenRequest,
                    timeout.Token));
            Assert.Equal(
                "ValidationException",
                restartedTokenError.ErrorCode);
            var restartedScopeError =
                await Assert.ThrowsAsync<AmazonDynamoDBException>(
                    () => client.TransactWriteItemsAsync(
                        scopeRequest,
                        timeout.Token));
            Assert.Equal(
                "ValidationException",
                restartedScopeError.ErrorCode);
            Assert.False(await ExistsAsync(
                client,
                table,
                "partition-a",
                "one",
                timeout.Token));
            Assert.False(await ExistsAsync(
                client,
                table,
                "partition-b",
                "two",
                timeout.Token));
        }, timeout.Token);
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
        string version,
        CancellationToken cancellationToken)
        => await client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                Put(table, partition, "left", version, "version"),
                Put(table, partition, "right", version, "version"),
            ],
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
        return response.Item.Count > 0;
    }

    private static AttributeValue S(string value) => new() { S = value };
}
