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
    public async Task Projection_alias_and_in_operand_limits_reject_before_transaction_execution()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            await PutAsync(client, table, "validation", "target", new()
            {
                ["value"] = S("present"),
            }, timeout.Token);

            var sdkAliasError = await Assert.ThrowsAsync<AmazonDynamoDBException>(
                () => client.TransactGetItemsAsync(
                    new TransactGetItemsRequest
                    {
                        TransactItems =
                        [
                            new TransactGetItem
                            {
                                Get = new Get
                                {
                                    TableName = table,
                                    Key = Key("validation", "target"),
                                    ProjectionExpression = "#pk",
                                    ExpressionAttributeNames = new()
                                    {
                                        ["#pk"] = "pk",
                                        ["#unused"] = "unused",
                                    },
                                },
                            },
                        ],
                    },
                    timeout.Token));
            Assert.Equal("ValidationException", sdkAliasError.ErrorCode);
            Assert.Contains("#unused", sdkAliasError.Message);

            var rawAlias = await SendRawAsync(
                "TransactGetItems",
                $$"""
                {
                  "TransactItems": [{
                    "Get": {
                      "ExpressionAttributeNames": {
                        "#unused": "unused",
                        "#pk": "pk"
                      },
                      "Key": {
                        "sk": { "S": "target" },
                        "pk": { "S": "validation" }
                      },
                      "ProjectionExpression": "#pk",
                      "TableName": "{{table}}"
                    }
                  }]
                }
                """,
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, rawAlias.Status);
            Assert.Contains("#unused", rawAlias.Body);

            var expression = new StringBuilder("value IN (");
            var values = new StringBuilder();
            for (var index = 0;
                 index <= ConditionExpressionParser.MaxInOperands;
                 index++)
            {
                if (index > 0)
                {
                    expression.Append(',');
                    values.Append(',');
                }
                expression.Append(":v").Append(index);
                values.Append("\":v").Append(index).Append("\":{\"S\":\"x\"}");
            }
            expression.Append(')');
            var inLimit = await SendRawAsync(
                "TransactWriteItems",
                "{\"TransactItems\":[{\"ConditionCheck\":{\"TableName\":\""
                + table + "\",\"Key\":{\"pk\":{\"S\":\"validation\"},"
                + "\"sk\":{\"S\":\"target\"}},\"ConditionExpression\":\""
                + expression + "\",\"ExpressionAttributeValues\":{"
                + values + "}}}]}",
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, inLimit.Status);
            Assert.Contains("at most 100", inLimit.Body);
        }, timeout.Token);
    }

    [SkippableFact]
    public async Task Scope_and_versioned_sprocs_survive_restart()
    {
        SkipUnlessConfigured();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
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

    [SkippableFact]
    public async Task Conflicting_v5_sproc_body_fails_closed_and_is_restored_in_isolated_table()
    {
        SkipUnlessConfigured();
        Skip.If(
            string.IsNullOrWhiteSpace(fixture.CosmosMasterKey),
            "AZURE_COSMOS_KEY is required for the direct Cosmos stored-procedure conflict probe.");
        using var client = fixture.CreateDynamoDbClient();
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WithTableAsync(client, async table =>
        {
            const string conflictingBody =
                "function atomicTransactWrite(operations) { "
                + "getContext().getResponse().setBody({success:true,conflictingBody:true}); }";
            var conflictPresent = false;
            try
            {
                await CosmosRestBootstrap.CreateStoredProcedureAsync(
                    http,
                    fixture.CosmosEndpoint,
                    fixture.CosmosMasterKey,
                    fixture.CosmosDatabase,
                    table,
                    SprocManager.TransactSprocId,
                    conflictingBody);
                conflictPresent = true;

                var failure = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(
                    () => client.TransactWriteItemsAsync(
                        new TransactWriteItemsRequest
                        {
                            TransactItems =
                            [
                                Put(
                                    table,
                                    "body-conflict",
                                    "target",
                                    "must-not-commit"),
                            ],
                        },
                        timeout.Token));
                Assert.Equal("InternalServerError", failure.ErrorCode);
                Assert.False(await ExistsAsync(
                    client,
                    table,
                    "body-conflict",
                    "target",
                    timeout.Token));

                using (var conflicting = JsonDocument.Parse(
                           await CosmosRestBootstrap.ReadStoredProcedureAsync(
                               http,
                               fixture.CosmosEndpoint,
                               fixture.CosmosMasterKey,
                               fixture.CosmosDatabase,
                               table,
                               SprocManager.TransactSprocId)))
                {
                    Assert.Equal(
                        conflictingBody,
                        conflicting.RootElement.GetProperty("body").GetString());
                }

                await CosmosRestBootstrap.DeleteStoredProcedureAsync(
                    http,
                    fixture.CosmosEndpoint,
                    fixture.CosmosMasterKey,
                    fixture.CosmosDatabase,
                    table,
                    SprocManager.TransactSprocId);
                conflictPresent = false;
                await fixture.RestartAsync();

                await client.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest
                    {
                        TransactItems =
                        [
                            Put(
                                table,
                                "body-conflict",
                                "target",
                                "restored"),
                        ],
                    },
                    timeout.Token);
                Assert.True(await ExistsAsync(
                    client,
                    table,
                    "body-conflict",
                    "target",
                    timeout.Token));

                using var restored = JsonDocument.Parse(
                    await CosmosRestBootstrap.ReadStoredProcedureAsync(
                        http,
                        fixture.CosmosEndpoint,
                        fixture.CosmosMasterKey,
                        fixture.CosmosDatabase,
                        table,
                        SprocManager.TransactSprocId));
                Assert.Equal(
                    SprocManager.TransactSprocBody,
                    restored.RootElement.GetProperty("body").GetString());
            }
            finally
            {
                if (conflictPresent)
                {
                    await CosmosRestBootstrap.DeleteStoredProcedureAsync(
                        http,
                        fixture.CosmosEndpoint,
                        fixture.CosmosMasterKey,
                        fixture.CosmosDatabase,
                        table,
                        SprocManager.TransactSprocId);
                }
            }
        }, timeout.Token);
    }

}
