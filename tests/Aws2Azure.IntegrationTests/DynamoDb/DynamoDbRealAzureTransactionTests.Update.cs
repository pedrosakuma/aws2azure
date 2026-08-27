using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public sealed partial class DynamoDbRealAzureTransactionTests
{
    [SkippableFact]
    public async Task Update_actions_commit_and_all_old_condition_failures_return_atomic_snapshot()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            const string partition = "update-rvoccf";
            await PutAsync(client, table, partition, "target", new()
            {
                ["counter"] = new AttributeValue { N = "3" },
                ["state"] = S("pending"),
                ["note"] = S("legacy"),
                ["stale"] = S("remove-me"),
            }, timeout.Token);

            await client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem
                        {
                            Update = new Update
                            {
                                TableName = table,
                                Key = Key(partition, "target"),
                                UpdateExpression =
                                    "SET #count = #count + :inc, #state = :complete, #note = if_not_exists(#note, :fallback) REMOVE #stale",
                                ConditionExpression = "#state = :pending",
                                ExpressionAttributeNames = new()
                                {
                                    ["#count"] = "counter",
                                    ["#state"] = "state",
                                    ["#note"] = "note",
                                    ["#stale"] = "stale",
                                },
                                ExpressionAttributeValues = new()
                                {
                                    [":inc"] = new AttributeValue { N = "2" },
                                    [":complete"] = S("complete"),
                                    [":pending"] = S("pending"),
                                    [":fallback"] = S("fallback"),
                                },
                            },
                        },
                        Put(table, partition, "peer-committed", "yes"),
                    ],
                },
                timeout.Token);

            var committed = await ReadItemAsync(
                client,
                table,
                partition,
                "target",
                timeout.Token);
            Assert.Equal(partition, committed["pk"].S);
            Assert.Equal("target", committed["sk"].S);
            Assert.Equal("5", committed["counter"].N);
            Assert.Equal("complete", committed["state"].S);
            Assert.Equal("legacy", committed["note"].S);
            Assert.False(committed.ContainsKey("stale"));
            Assert.True(await ExistsAsync(
                client,
                table,
                partition,
                "peer-committed",
                timeout.Token));

            var canceled = await Assert.ThrowsAsync<TransactionCanceledException>(
                () => client.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest
                    {
                        TransactItems =
                        [
                            new TransactWriteItem
                            {
                                Update = new Update
                                {
                                    TableName = table,
                                    Key = Key(partition, "target"),
                                    UpdateExpression =
                                        "SET #count = #count + :inc, #state = :rolledBack",
                                    ConditionExpression = "#state = :pending",
                                    ReturnValuesOnConditionCheckFailure =
                                        Amazon.DynamoDBv2.ReturnValuesOnConditionCheckFailure.ALL_OLD,
                                    ExpressionAttributeNames = new()
                                    {
                                        ["#count"] = "counter",
                                        ["#state"] = "state",
                                    },
                                    ExpressionAttributeValues = new()
                                    {
                                        [":inc"] = new AttributeValue { N = "1" },
                                        [":rolledBack"] = S("rolled-back"),
                                        [":pending"] = S("pending"),
                                    },
                                },
                            },
                            Put(table, partition, "peer-rolled-back", "must-not-commit"),
                        ],
                    },
                    timeout.Token));

            Assert.Equal(2, canceled.CancellationReasons.Count);
            var reason = canceled.CancellationReasons[0];
            Assert.Equal("ConditionalCheckFailed", reason.Code);
            var previous = Assert.IsAssignableFrom<Dictionary<string, AttributeValue>>(reason.Item);
            Assert.Equal(partition, previous["pk"].S);
            Assert.Equal("target", previous["sk"].S);
            Assert.Equal("5", previous["counter"].N);
            Assert.Equal("complete", previous["state"].S);
            Assert.Equal("legacy", previous["note"].S);
            Assert.False(previous.ContainsKey("stale"));
            Assert.Equal("None", canceled.CancellationReasons[1].Code);
            Assert.Null(canceled.CancellationReasons[1].Item);
            Assert.False(await ExistsAsync(
                client,
                table,
                partition,
                "peer-rolled-back",
                timeout.Token));

            var afterCancellation = await ReadItemAsync(
                client,
                table,
                partition,
                "target",
                timeout.Token);
            Assert.Equal("5", afterCancellation["counter"].N);
            Assert.Equal("complete", afterCancellation["state"].S);
            Assert.Equal("legacy", afterCancellation["note"].S);
            Assert.False(afterCancellation.ContainsKey("stale"));
        }, timeout.Token);
    }
}
