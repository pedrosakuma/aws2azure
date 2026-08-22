using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;
using DynamoDbResourceNotFoundException =
    Amazon.DynamoDBv2.Model.ResourceNotFoundException;

namespace Aws2Azure.IntegrationTests.DynamoDb;



public sealed partial class DynamoDbRealAzureTransactionLoadQualificationTests
{
    private async Task VerifyStoredProcedureBodyAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        const string conflictingBody =
            "function atomicTransactWrite(operations) { "
            + "getContext().getResponse().setBody({success:true,conflictingBody:true}); }";
        using var http = new HttpClient();
        await CosmosRestBootstrap.CreateStoredProcedureAsync(
            http,
            fixture.CosmosEndpoint,
            fixture.CosmosKey,
            fixture.CosmosDatabase,
            table,
            SprocManager.TransactSprocId,
            conflictingBody).ConfigureAwait(false);
        var conflictPresent = true;
        try
        {
            var failure = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(
                () => client.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest
                    {
                        TransactItems =
                        [
                            Put(table, Partition, "sproc-conflict", "must-not-commit"),
                        ],
                    },
                    cancellationToken));
            Assert.Equal("InternalServerError", failure.ErrorCode);
            Assert.False(await ExistsAsync(
                client,
                table,
                Partition,
                "sproc-conflict",
                cancellationToken).ConfigureAwait(false));
            await CosmosRestBootstrap.DeleteStoredProcedureAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                fixture.CosmosDatabase,
                table,
                SprocManager.TransactSprocId).ConfigureAwait(false);
            conflictPresent = false;
            await fixture.RestartAsync().ConfigureAwait(false);
            await client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        Put(table, Partition, "sproc-conflict", "restored"),
                    ],
                },
                cancellationToken).ConfigureAwait(false);
            using var restored = JsonDocument.Parse(
                await CosmosRestBootstrap.ReadStoredProcedureAsync(
                    http,
                    fixture.CosmosEndpoint,
                    fixture.CosmosKey,
                    fixture.CosmosDatabase,
                    table,
                    SprocManager.TransactSprocId).ConfigureAwait(false));
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
                    fixture.CosmosKey,
                    fixture.CosmosDatabase,
                    table,
                    SprocManager.TransactSprocId).ConfigureAwait(false);
            }
        }
    }

    private static async Task VerifyReadAfterWriteAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        const int sampleCount = 12;
        var sampleInterval = TimeSpan.FromMilliseconds(200);
        var sortKeys = Enumerable.Range(0, 72)
            .Select(index => $"snapshot-{index:D2}")
            .ToArray();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        for (var version = 1; version <= sampleCount; version++)
        {
            var expectedVersion = version.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            await WriteVersionAsync(
                client,
                table,
                sortKeys,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            var response = await client.TransactGetItemsAsync(
                new TransactGetItemsRequest
                {
                    TransactItems = sortKeys
                        .Select(sort => Get(table, Partition, sort))
                        .ToList(),
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Responses.Count != sortKeys.Length)
            {
                throw new InvalidDataException(
                    "Transactional read returned the wrong item count.");
            }
            var snapshotVersion = response.Responses[0].Item["version"].S;
            if (response.Responses.Any(item =>
                    item.Item["version"].S != snapshotVersion))
            {
                throw new InvalidDataException(
                    "Transactional read observed mixed committed versions.");
            }
            if (!string.Equals(
                    snapshotVersion,
                    expectedVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Transactional read returned version '{snapshotVersion}' after committing '{expectedVersion}'.");
            }
            observed.Add(snapshotVersion);
            await Task.Delay(sampleInterval, cancellationToken).ConfigureAwait(false);
        }

        if (observed.Count != sampleCount)
        {
            throw new InvalidDataException(
                $"Transactional reads observed {observed.Count} of {sampleCount} committed versions.");
        }
    }

    private static async Task VerifyPreflightContractAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var oversized = new string('x', 400 * 1024);
        var failure = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
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
                                    ["pk"] = S(Partition),
                                    ["sk"] = S("oversized"),
                                    ["payload"] = S(oversized),
                                },
                            },
                        },
                    ],
                },
                cancellationToken));
        Assert.Equal("ValidationException", failure.ErrorCode);
        Assert.False(await ExistsAsync(
            client,
            table,
            Partition,
            "oversized",
            cancellationToken).ConfigureAwait(false));
    }

    private static async Task VerifyAtomicityAndCancellationAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        await client.PutItemAsync(
            new PutItemRequest
            {
                TableName = table,
                Item = new()
                {
                    ["pk"] = S(Partition),
                    ["sk"] = S("atomic-gate"),
                    ["state"] = S("closed"),
                },
            },
            cancellationToken).ConfigureAwait(false);
        var canceled = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem
                        {
                            ConditionCheck = new ConditionCheck
                            {
                                TableName = table,
                                Key = Key(Partition, "atomic-gate"),
                                ConditionExpression = "#state = :open",
                                ExpressionAttributeNames = new() { ["#state"] = "state" },
                                ExpressionAttributeValues = new() { [":open"] = S("open") },
                            },
                        },
                        Put(table, Partition, "atomic-one", "must-not-commit"),
                        Put(table, Partition, "atomic-two", "must-not-commit"),
                    ],
                },
                cancellationToken));
        Assert.Equal(
            new[] { "ConditionalCheckFailed", "None", "None" },
            canceled.CancellationReasons.Select(reason => reason.Code).ToArray());
        Assert.False(await ExistsAsync(
            client, table, Partition, "atomic-one", cancellationToken).ConfigureAwait(false));
        Assert.False(await ExistsAsync(
            client, table, Partition, "atomic-two", cancellationToken).ConfigureAwait(false));

        await client.TransactWriteItemsAsync(
            new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = table,
                            Key = Key(Partition, "atomic-gate"),
                            ConditionExpression = "#state = :closed",
                            ExpressionAttributeNames = new() { ["#state"] = "state" },
                            ExpressionAttributeValues = new() { [":closed"] = S("closed") },
                        },
                    },
                    Put(table, Partition, "atomic-one", "committed"),
                    Put(table, Partition, "atomic-two", "committed"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        Assert.True(await ExistsAsync(
            client, table, Partition, "atomic-one", cancellationToken).ConfigureAwait(false));
        Assert.True(await ExistsAsync(
            client, table, Partition, "atomic-two", cancellationToken).ConfigureAwait(false));
    }

    private static async Task VerifyScopeRejectionAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var failure = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        Put(table, Partition, "scope-a", "must-not-commit"),
                        Put(table, "other-partition", "scope-b", "must-not-commit"),
                    ],
                },
                cancellationToken));
        Assert.Equal("ValidationException", failure.ErrorCode);
        Assert.False(await ExistsAsync(
            client, table, Partition, "scope-a", cancellationToken).ConfigureAwait(false));
        Assert.False(await ExistsAsync(
            client, table, "other-partition", "scope-b", cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task VerifyIdempotencyAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var token = "load-" + Guid.NewGuid().ToString("N")[..20];
        var request = new TransactWriteItemsRequest
        {
            ClientRequestToken = token,
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = new()
                        {
                            ["pk"] = S(Partition),
                            ["sk"] = S("idempotency"),
                            ["version"] = S("one"),
                            ["marker"] = S("committed-once"),
                        },
                        ConditionExpression = "attribute_not_exists(#marker)",
                        ExpressionAttributeNames = new() { ["#marker"] = "marker" },
                    },
                },
            ],
        };
        await client.TransactWriteItemsAsync(request, cancellationToken).ConfigureAwait(false);
        await client.TransactWriteItemsAsync(request, cancellationToken).ConfigureAwait(false);
        var mismatch = await Assert.ThrowsAsync<IdempotentParameterMismatchException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    ClientRequestToken = token,
                    TransactItems =
                    [
                        Put(table, Partition, "idempotency", "two"),
                    ],
                },
                cancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var item = await ReadAsync(
            client, table, Partition, "idempotency", cancellationToken).ConfigureAwait(false);
        Assert.Equal("one", item["version"].S);
    }

    private static async Task VerifyContentionAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        await client.PutItemAsync(
            new PutItemRequest
            {
                TableName = table,
                Item = new()
                {
                    ["pk"] = S(Partition),
                    ["sk"] = S("contention-gate"),
                    ["state"] = S("open"),
                },
            },
            cancellationToken).ConfigureAwait(false);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async contender =>
        {
            try
            {
                await client.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest
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
                                        ["pk"] = S(Partition),
                                        ["sk"] = S("contention-gate"),
                                        ["state"] = S("closed"),
                                        ["winner"] = S($"winner-{contender}"),
                                    },
                                    ConditionExpression = "#state = :open",
                                    ExpressionAttributeNames = new() { ["#state"] = "state" },
                                    ExpressionAttributeValues = new() { [":open"] = S("open") },
                                },
                            },
                            Put(
                                table,
                                Partition,
                                $"contention-audit-{contender}",
                                $"winner-{contender}"),
                        ],
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TransactionCanceledException)
            {
                return false;
            }
        })).ConfigureAwait(false);
        Assert.Single(outcomes, outcome => outcome);
        var query = await client.QueryAsync(
            new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
                ExpressionAttributeValues = new()
                {
                    [":pk"] = S(Partition),
                    [":prefix"] = S("contention-audit-"),
                },
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);
        Assert.Single(query.Items);
    }

    private async Task VerifyStableAuthorityAndRestartVersioningAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var accountBefore = CosmosAccountInfoParser.Parse(
            await CosmosRestBootstrap.ReadAccountAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                cancellationToken).ConfigureAwait(false),
            new Uri(fixture.CosmosEndpoint));
        var selectionBefore = CosmosRegionRouting.SelectTransactionEndpoint(
            accountBefore,
            [],
            out var authorityBefore);
        if (accountBefore.EnableMultipleWriteLocations
            || accountBefore.WritableLocations.Length != 1
            || selectionBefore != CosmosTransactionEndpointSelectionStatus.Ready
            || authorityBefore != accountBefore.WritableLocations[0].Endpoint)
        {
            throw new InvalidDataException(
                "Transaction load requires one discovered writable Cosmos authority.");
        }

        var token = "restart-" + Guid.NewGuid().ToString("N")[..18];
        var request = new TransactWriteItemsRequest
        {
            ClientRequestToken = token,
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = new()
                        {
                            ["pk"] = S(Partition),
                            ["sk"] = S("restart-a"),
                            ["version"] = S("before"),
                            ["marker"] = S("committed-once"),
                        },
                        ConditionExpression = "attribute_not_exists(#marker)",
                        ExpressionAttributeNames = new() { ["#marker"] = "marker" },
                    },
                },
                Put(table, Partition, "restart-b", "before"),
            ],
        };
        await client.TransactWriteItemsAsync(request, cancellationToken).ConfigureAwait(false);
        var routeOutputOffset = fixture.ProxyOutput.Length;
        await fixture.RestartWithTransactionRouteCaptureAsync().ConfigureAwait(false);
        using var restartedClient = fixture.CreateDynamoDbClient(maxErrorRetry: 0);
        var committedSnapshot = await restartedClient.TransactGetItemsAsync(
            new TransactGetItemsRequest
            {
                TransactItems =
                [
                    GetWithMarker(table, Partition, "restart-a"),
                    Get(table, Partition, "restart-b"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, committedSnapshot.Responses.Count);
        Assert.All(
            committedSnapshot.Responses,
            response => Assert.Equal("before", response.Item["version"].S));
        Assert.Equal(
            "committed-once",
            committedSnapshot.Responses[0].Item["marker"].S);

        await restartedClient.TransactWriteItemsAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = await restartedClient.TransactGetItemsAsync(
            new TransactGetItemsRequest
            {
                TransactItems =
                [
                    GetWithMarker(table, Partition, "restart-a"),
                    Get(table, Partition, "restart-b"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, snapshot.Responses.Count);
        Assert.All(
            snapshot.Responses,
            response => Assert.Equal("before", response.Item["version"].S));
        Assert.Equal("committed-once", snapshot.Responses[0].Item["marker"].S);
        await restartedClient.TransactWriteItemsAsync(
            new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Put(table, Partition, "restart-a", "after"),
                    Put(table, Partition, "restart-b", "after"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        var accountAfter = CosmosAccountInfoParser.Parse(
            await CosmosRestBootstrap.ReadAccountAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                cancellationToken).ConfigureAwait(false),
            new Uri(fixture.CosmosEndpoint));
        var selectionAfter = CosmosRegionRouting.SelectTransactionEndpoint(
            accountAfter,
            [],
            out var authorityAfter);
        if (selectionAfter != CosmosTransactionEndpointSelectionStatus.Ready
            || authorityAfter != authorityBefore
            || accountAfter.AccountIdentity != accountBefore.AccountIdentity)
        {
            throw new InvalidDataException(
                "Transaction restart did not retain one stable Cosmos authority.");
        }
        await WaitForTransactionRoutesAsync(
            routeOutputOffset,
            authorityBefore.AbsoluteUri,
            expectedCount: 4,
            cancellationToken).ConfigureAwait(false);
    }

}
