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

            Assert.Single(outcomes, outcome => outcome);
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
    public async Task Official_sdk_auto_generated_token_reaches_success()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            var request = new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = Item(
                                "sdk-auto-token",
                                "target",
                                "committed"),
                            ConditionExpression =
                                "attribute_not_exists(#value)",
                            ExpressionAttributeNames = new()
                            {
                                ["#value"] = "value",
                            },
                        },
                    },
                ],
            };

            await client.TransactWriteItemsAsync(request, timeout.Token);

            Assert.True(await ExistsAsync(
                client,
                table,
                "sdk-auto-token",
                "target",
                timeout.Token));
        }, timeout.Token);
    }

    [SkippableFact]
    public async Task Explicit_token_replay_mismatch_concurrency_cancellation_and_restart_are_durable()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await WithTableAsync(client, async table =>
        {
            const string partition = "idempotency";
            var replayToken = "raw-replay-" + Guid.NewGuid().ToString("N")[..20];
            var firstBody = BuildRawIdempotentPut(
                table,
                partition,
                "replay",
                replayToken,
                "1.0",
                reordered: false);
            var equivalentBody = BuildRawIdempotentPut(
                table,
                partition,
                "replay",
                replayToken,
                "1",
                reordered: true);

            var first = await SendRawAsync(
                "TransactWriteItems",
                firstBody,
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, first.Status);
            var replay = await SendRawAsync(
                "TransactWriteItems",
                equivalentBody,
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, replay.Status);
            var replayedItem = await ReadItemAsync(
                client,
                table,
                partition,
                "replay",
                timeout.Token);
            Assert.Equal("committed", replayedItem["marker"].S);
            Assert.Equal("1", replayedItem["value"].N);

            var mismatch = await SendRawAsync(
                "TransactWriteItems",
                BuildRawIdempotentPut(
                    table,
                    partition,
                    "replay",
                    replayToken,
                    "2",
                    reordered: false),
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
            using (var mismatchDocument = JsonDocument.Parse(mismatch.Body))
            {
                Assert.Equal(
                    "com.amazonaws.dynamodb.v20120810#IdempotentParameterMismatchException",
                    mismatchDocument.RootElement.GetProperty("__type").GetString());
            }

            var concurrentToken =
                "raw-concurrent-" + Guid.NewGuid().ToString("N")[..17];
            var concurrentBody = BuildRawIdempotentPut(
                table,
                partition,
                "concurrent",
                concurrentToken,
                "1",
                reordered: false);
            var concurrent = await Task.WhenAll(
                SendRawAsync(
                    "TransactWriteItems",
                    concurrentBody,
                    timeout.Token),
                SendRawAsync(
                    "TransactWriteItems",
                    concurrentBody,
                    timeout.Token));
            Assert.All(
                concurrent,
                response => Assert.Equal(HttpStatusCode.OK, response.Status));
            Assert.True(await ExistsAsync(
                client,
                table,
                partition,
                "concurrent",
                timeout.Token));

            await PutAsync(client, table, partition, "gate", new()
            {
                ["state"] = S("closed"),
            }, timeout.Token);
            var cancellationToken =
                "raw-canceled-" + Guid.NewGuid().ToString("N")[..19];
            var cancellationBody = $$"""
                {
                  "ClientRequestToken": "{{cancellationToken}}",
                  "TransactItems": [
                    {
                      "ConditionCheck": {
                        "TableName": "{{table}}",
                        "Key": {
                          "pk": { "S": "{{partition}}" },
                          "sk": { "S": "gate" }
                        },
                        "ConditionExpression": "#state = :open",
                        "ExpressionAttributeNames": { "#state": "state" },
                        "ExpressionAttributeValues": {
                          ":open": { "S": "open" }
                        }
                      }
                    },
                    {
                      "Put": {
                        "TableName": "{{table}}",
                        "Item": {
                          "pk": { "S": "{{partition}}" },
                          "sk": { "S": "canceled-peer" },
                          "marker": { "S": "must-not-commit" }
                        }
                      }
                    }
                  ]
                }
                """;
            var canceled = await SendRawAsync(
                "TransactWriteItems",
                cancellationBody,
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, canceled.Status);
            Assert.Contains(
                "TransactionCanceledException",
                canceled.Body,
                StringComparison.Ordinal);
            await PutAsync(client, table, partition, "gate", new()
            {
                ["state"] = S("open"),
            }, timeout.Token);
            var canceledReplay = await SendRawAsync(
                "TransactWriteItems",
                cancellationBody,
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, canceledReplay.Status);
            Assert.Contains(
                "TransactionCanceledException",
                canceledReplay.Body,
                StringComparison.Ordinal);
            Assert.False(await ExistsAsync(
                client,
                table,
                partition,
                "canceled-peer",
                timeout.Token));

            var restartToken =
                "raw-restart-" + Guid.NewGuid().ToString("N")[..20];
            var restartBody = BuildRawIdempotentPut(
                table,
                partition,
                "restart",
                restartToken,
                "1",
                reordered: false);
            await SendRawAndDiscardResponseAsync(
                "TransactWriteItems",
                restartBody,
                timeout.Token);
            Assert.True(await ExistsAsync(
                client,
                table,
                partition,
                "restart",
                timeout.Token));

            if (!string.IsNullOrWhiteSpace(fixture.CosmosMasterKey))
            {
                using var cosmos = new HttpClient();
                using var tokenRecord = JsonDocument.Parse(
                    await CosmosRestBootstrap.ReadDocumentAsync(
                        cosmos,
                        fixture.CosmosEndpoint,
                        fixture.CosmosMasterKey,
                        fixture.CosmosDatabase,
                        table,
                        TransactionHandler.BuildIdempotencyRecordId(
                            replayToken),
                        Convert.ToHexStringLower(
                            Encoding.UTF8.GetBytes(partition))));
                Assert.Equal(
                    DynamoDbPersistedFormatContract
                        .TransactionIdempotencyRecordDiscriminator,
                    tokenRecord.RootElement.GetProperty("_a2a").GetString());
                Assert.Equal(
                    "success",
                    tokenRecord.RootElement.GetProperty("outcome").GetString());
                Assert.Equal(
                    600_000,
                    tokenRecord.RootElement.GetProperty("expiresAtMs").GetInt64()
                    - tokenRecord.RootElement.GetProperty("createdAtMs").GetInt64());
                Assert.Equal(
                    660,
                    tokenRecord.RootElement.GetProperty("ttl").GetInt32());
            }

            await fixture.RestartAsync();
            var restartedReplay = await SendRawAsync(
                "TransactWriteItems",
                restartBody,
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, restartedReplay.Status);

            var visible = await client.QueryAsync(
                new QueryRequest
                {
                    TableName = table,
                    KeyConditionExpression = "pk = :pk",
                    ExpressionAttributeValues = new()
                    {
                        [":pk"] = S(partition),
                    },
                    ConsistentRead = true,
                },
                timeout.Token);
            Assert.Equal(4, visible.Items.Count);
            Assert.All(
                visible.Items,
                item =>
                {
                    Assert.Contains("pk", item.Keys);
                    Assert.Contains("sk", item.Keys);
                    Assert.DoesNotContain(
                        item.Keys,
                        name => name.StartsWith("_a2a", StringComparison.Ordinal));
                });
        }, timeout.Token);
    }

}
