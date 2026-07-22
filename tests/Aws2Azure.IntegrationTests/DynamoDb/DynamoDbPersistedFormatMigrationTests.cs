using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Source scenario for issue #636. Evidence publication and workflow selection
/// are owned separately; this test only defines the executable adjacent-runtime
/// contract against one unchanged Cosmos container.
/// </summary>
[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbPersistedFormatMigration")]
[Collection(DynamoDbRealAzureLoadCollection.Name)]
public sealed class DynamoDbPersistedFormatMigrationTests(
    DynamoDbRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Adjacent_runtime_reads_rewrites_and_continuations_are_bidirectional()
    {
        Skip.If(
            Environment.GetEnvironmentVariable(
                "AWS2AZURE_DDB_PERSISTED_FORMAT_QUALIFICATION") != "1",
            "AWS2AZURE_DDB_PERSISTED_FORMAT_QUALIFICATION is not enabled.");
        Skip.IfNot(fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE are not configured.");
        Skip.IfNot(fixture.SealedRollbackConfigured,
            "Exact candidate and previous sealed runtimes are required.");

        var table = "a2a-format-" + Guid.NewGuid().ToString("N")[..20];
        var candidateStopped = false;
        var candidateRestored = false;
        AmazonDynamoDBClient? previous = null;
        using var candidate = fixture.CreateDynamoDbClient(maxErrorRetry: 0);

        try
        {
            await candidate.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new("pk", ScalarAttributeType.S),
                    new("sk", ScalarAttributeType.S),
                    new("gpk", ScalarAttributeType.S),
                    new("gsk", ScalarAttributeType.N),
                ],
                KeySchema =
                [
                    new("pk", KeyType.HASH),
                    new("sk", KeyType.RANGE),
                ],
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byAmount",
                        KeySchema =
                        [
                            new("gpk", KeyType.HASH),
                            new("gsk", KeyType.RANGE),
                        ],
                        Projection = new Projection
                        {
                            ProjectionType = ProjectionType.ALL,
                        },
                    },
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);
            await WaitForActiveAsync(candidate, table).ConfigureAwait(false);
            await candidate.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = table,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    AttributeName = "ttl",
                    Enabled = true,
                },
            }).ConfigureAwait(false);

            var expiry = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            await PutFixtureItemAsync(candidate, table, "001", expiry, "candidate")
                .ConfigureAwait(false);
            await PutFixtureItemAsync(candidate, table, "002", expiry, "candidate")
                .ConfigureAwait(false);
            var candidatePage = await QueryPageAsync(candidate, table, null)
                .ConfigureAwait(false);
            Assert.NotEmpty(candidatePage.LastEvaluatedKey);
            var candidateFirstKey = ItemSortKey(candidatePage.Items);
            var candidateScanPage = await ScanPageAsync(candidate, table, null)
                .ConfigureAwait(false);
            Assert.NotEmpty(candidateScanPage.LastEvaluatedKey);
            var candidateFirstScanKey = ItemSortKey(candidateScanPage.Items);
            var candidateOrderedPage = await OrderedQueryPageAsync(candidate, table, null)
                .ConfigureAwait(false);
            Assert.NotEmpty(candidateOrderedPage.LastEvaluatedKey);
            var candidateFirstOrderedKey = ItemSortKey(candidateOrderedPage.Items);

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            candidateStopped = true;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            previous = fixture.CreateDynamoDbClient(maxErrorRetry: 0);

            await AssertFixtureItemAsync(previous, table, "001", "candidate")
                .ConfigureAwait(false);
            var described = await previous.DescribeTableAsync(
                new DescribeTableRequest { TableName = table }).ConfigureAwait(false);
            Assert.Contains(
                described.Table.GlobalSecondaryIndexes,
                index => index.IndexName == "byAmount");
            var ttl = await previous.DescribeTimeToLiveAsync(
                new DescribeTimeToLiveRequest { TableName = table }).ConfigureAwait(false);
            Assert.Equal(
                TimeToLiveStatus.ENABLED,
                ttl.TimeToLiveDescription.TimeToLiveStatus);
            await previous.TagResourceAsync(new TagResourceRequest
            {
                ResourceArn = described.Table.TableArn,
                Tags = [new Tag { Key = "format-writer", Value = "previous" }],
            }).ConfigureAwait(false);
            await previous.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = table,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    AttributeName = "ttl",
                    Enabled = false,
                },
            }).ConfigureAwait(false);
            await previous.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = table,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    AttributeName = "ttl",
                    Enabled = true,
                },
            }).ConfigureAwait(false);
            var previousPage = await QueryPageAsync(
                previous,
                table,
                candidatePage.LastEvaluatedKey).ConfigureAwait(false);
            Assert.Single(previousPage.Items);
            Assert.NotEqual(candidateFirstKey, ItemSortKey(previousPage.Items));
            var previousScanPage = await ScanPageAsync(
                previous,
                table,
                candidateScanPage.LastEvaluatedKey).ConfigureAwait(false);
            Assert.Single(previousScanPage.Items);
            Assert.NotEqual(candidateFirstScanKey, ItemSortKey(previousScanPage.Items));
            var previousOrderedPage = await OrderedQueryPageAsync(
                previous,
                table,
                candidateOrderedPage.LastEvaluatedKey).ConfigureAwait(false);
            Assert.Single(previousOrderedPage.Items);
            Assert.NotEqual(
                candidateFirstOrderedKey,
                ItemSortKey(previousOrderedPage.Items));

            await previous.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key("001"),
                UpdateExpression = "SET #writer = :writer",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#writer"] = "writer",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":writer"] = new() { S = "previous" },
                },
            }).ConfigureAwait(false);
            await PutFixtureItemAsync(previous, table, "003", expiry, "previous")
                .ConfigureAwait(false);
            var priorFirstPage = await QueryPageAsync(previous, table, null)
                .ConfigureAwait(false);
            Assert.NotEmpty(priorFirstPage.LastEvaluatedKey);
            var priorFirstKey = ItemSortKey(priorFirstPage.Items);
            var priorFirstScanPage = await ScanPageAsync(previous, table, null)
                .ConfigureAwait(false);
            Assert.NotEmpty(priorFirstScanPage.LastEvaluatedKey);
            var priorFirstScanKey = ItemSortKey(priorFirstScanPage.Items);
            var priorFirstOrderedPage = await OrderedQueryPageAsync(previous, table, null)
                .ConfigureAwait(false);
            Assert.NotEmpty(priorFirstOrderedPage.LastEvaluatedKey);
            var priorFirstOrderedKey = ItemSortKey(priorFirstOrderedPage.Items);

            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            previous.Dispose();
            previous = null;
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate).ConfigureAwait(false);
            candidateStopped = false;
            candidateRestored = true;

            await AssertFixtureItemAsync(candidate, table, "001", "previous")
                .ConfigureAwait(false);
            await AssertFixtureItemAsync(candidate, table, "003", "previous")
                .ConfigureAwait(false);
            var candidateDescription = await candidate.DescribeTableAsync(
                new DescribeTableRequest { TableName = table }).ConfigureAwait(false);
            Assert.Contains(
                candidateDescription.Table.GlobalSecondaryIndexes,
                index => index.IndexName == "byAmount");
            var candidateTtl = await candidate.DescribeTimeToLiveAsync(
                new DescribeTimeToLiveRequest { TableName = table }).ConfigureAwait(false);
            Assert.Equal(
                TimeToLiveStatus.ENABLED,
                candidateTtl.TimeToLiveDescription.TimeToLiveStatus);
            var candidateTags = await candidate.ListTagsOfResourceAsync(
                new ListTagsOfResourceRequest
                {
                    ResourceArn = candidateDescription.Table.TableArn,
                }).ConfigureAwait(false);
            Assert.Contains(
                candidateTags.Tags,
                tag => tag.Key == "format-writer" && tag.Value == "previous");
            var resumed = await QueryPageAsync(
                candidate,
                table,
                priorFirstPage.LastEvaluatedKey).ConfigureAwait(false);
            Assert.NotEmpty(resumed.Items);
            Assert.NotEqual(priorFirstKey, ItemSortKey(resumed.Items));
            var resumedScan = await ScanPageAsync(
                candidate,
                table,
                priorFirstScanPage.LastEvaluatedKey).ConfigureAwait(false);
            Assert.NotEmpty(resumedScan.Items);
            Assert.NotEqual(priorFirstScanKey, ItemSortKey(resumedScan.Items));
            var resumedOrdered = await OrderedQueryPageAsync(
                candidate,
                table,
                priorFirstOrderedPage.LastEvaluatedKey).ConfigureAwait(false);
            Assert.NotEmpty(resumedOrdered.Items);
            Assert.NotEqual(priorFirstOrderedKey, ItemSortKey(resumedOrdered.Items));
        }
        finally
        {
            previous?.Dispose();
            if (candidateStopped && !candidateRestored)
            {
                if (fixture.ProxyStarted)
                {
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                }
                await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate)
                    .ConfigureAwait(false);
            }

            try
            {
                await candidate.DeleteTableAsync(
                    new DeleteTableRequest { TableName = table }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task PutFixtureItemAsync(
        AmazonDynamoDBClient client,
        string table,
        string sortKey,
        string expiry,
        string writer)
    {
        await client.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = "partition-1" },
                ["sk"] = new() { S = sortKey },
                ["gpk"] = new() { S = "amounts" },
                ["gsk"] = new() { N = "99999999999999999999999999999999999" + sortKey },
                ["id"] = new() { S = "user-id-" + sortKey },
                ["ttl"] = new() { N = expiry },
                ["blob"] = new() { B = new MemoryStream([1, 2, 3, 4]) },
                ["labels"] = new() { SS = ["alpha", "beta"] },
                ["writer"] = new() { S = writer },
            },
        }).ConfigureAwait(false);
    }

    private static async Task AssertFixtureItemAsync(
        AmazonDynamoDBClient client,
        string table,
        string sortKey,
        string writer)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(sortKey),
            ConsistentRead = true,
        }).ConfigureAwait(false);

        Assert.Equal(writer, response.Item["writer"].S);
        Assert.Equal("user-id-" + sortKey, response.Item["id"].S);
        Assert.Equal(
            "99999999999999999999999999999999999" + sortKey,
            response.Item["gsk"].N);
        Assert.Equal(4, response.Item["blob"].B.Length);
        Assert.Equal(["alpha", "beta"], response.Item["labels"].SS);
    }

    private static Dictionary<string, AttributeValue> Key(string sortKey) => new()
    {
        ["pk"] = new() { S = "partition-1" },
        ["sk"] = new() { S = sortKey },
    };

    private static string ItemSortKey(List<Dictionary<string, AttributeValue>> items)
    {
        Assert.Single(items);
        return items[0]["sk"].S;
    }

    private static Task<QueryResponse> QueryPageAsync(
        AmazonDynamoDBClient client,
        string table,
        Dictionary<string, AttributeValue>? exclusiveStartKey) =>
        client.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pk"] = new() { S = "partition-1" },
            },
            ExclusiveStartKey = exclusiveStartKey,
            Limit = 1,
        });

    private static Task<ScanResponse> ScanPageAsync(
        AmazonDynamoDBClient client,
        string table,
        Dictionary<string, AttributeValue>? exclusiveStartKey) =>
        client.ScanAsync(new ScanRequest
        {
            TableName = table,
            ExclusiveStartKey = exclusiveStartKey,
            Limit = 1,
        });

    private static Task<QueryResponse> OrderedQueryPageAsync(
        AmazonDynamoDBClient client,
        string table,
        Dictionary<string, AttributeValue>? exclusiveStartKey) =>
        client.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "byAmount",
            KeyConditionExpression = "gpk = :gpk",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":gpk"] = new() { S = "amounts" },
            },
            ExclusiveStartKey = exclusiveStartKey,
            Limit = 1,
            ScanIndexForward = true,
        });

    private static async Task WaitForActiveAsync(
        AmazonDynamoDBClient client,
        string table)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var response = await client.DescribeTableAsync(
                new DescribeTableRequest { TableName = table }).ConfigureAwait(false);
            if (response.Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        throw new TimeoutException($"Table {table} did not become ACTIVE.");
    }
}
