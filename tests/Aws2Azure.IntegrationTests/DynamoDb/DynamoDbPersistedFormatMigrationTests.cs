using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Aws2Azure.TestSupport.OperationalQualification;
using System.Text.Json;
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
    public async Task Frozen_v1_export_import_uses_isolated_container_and_preserves_rollback_source()
    {
        Skip.IfNot(fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE are not configured.");

        var suffix = Guid.NewGuid().ToString("N")[..16];
        var sourceContainer = "a2a-v1-source-" + suffix;
        var targetTable = "a2a-v2-target-" + suffix;
        var sourceCreated = false;
        var targetCreated = false;
        using var http = new HttpClient();
        using var candidate = fixture.CreateDynamoDbClient(maxErrorRetry: 0);

        try
        {
            await CosmosRestBootstrap.CreateContainerAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                fixture.CosmosDatabase,
                sourceContainer,
                "/pk").ConfigureAwait(false);
            sourceCreated = true;

            var fixturePath = Path.Combine(
                AppContext.BaseDirectory,
                "DynamoDb",
                "Persistence",
                "Fixtures",
                "v1",
                "item-envelope.json");
            using var frozen = JsonDocument.Parse(
                await File.ReadAllTextAsync(fixturePath).ConfigureAwait(false));
            var futureExpiry = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var legacyDocument = BuildLegacyDocument(frozen.RootElement, futureExpiry);
            await CosmosRestBootstrap.CreateDocumentAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                fixture.CosmosDatabase,
                sourceContainer,
                "partition-1",
                legacyDocument).ConfigureAwait(false);

            var sourceBefore = await CosmosRestBootstrap.ReadDocumentAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                fixture.CosmosDatabase,
                sourceContainer,
                "sort-1",
                "partition-1").ConfigureAwait(false);
            using var sourceDocument = JsonDocument.Parse(sourceBefore);
            var migrated = InferredAttributeStorage.ExtractItem(sourceDocument.RootElement);
            Assert.NotNull(migrated);

            await candidate.CreateTableAsync(new CreateTableRequest
            {
                TableName = targetTable,
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
            }).ConfigureAwait(false);
            targetCreated = true;
            await WaitForActiveAsync(candidate, targetTable).ConfigureAwait(false);
            var targetContainer = await CosmosRestBootstrap.ReadContainerAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                fixture.CosmosDatabase,
                targetTable).ConfigureAwait(false);
            using (var targetDefinition = JsonDocument.Parse(targetContainer))
            {
                var paths = targetDefinition.RootElement
                    .GetProperty("partitionKey")
                    .GetProperty("paths");
                Assert.Equal(1, paths.GetArrayLength());
                Assert.Equal("/_a2a_pk", paths[0].GetString());
            }
            await candidate.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = targetTable,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    AttributeName = "ttl",
                    Enabled = true,
                },
            }).ConfigureAwait(false);

            await candidate.PutItemAsync(new PutItemRequest
            {
                TableName = targetTable,
                Item = ToAttributeMap(migrated!),
            }).ConfigureAwait(false);

            var read = await candidate.GetItemAsync(new GetItemRequest
            {
                TableName = targetTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = "partition-1" },
                    ["sk"] = new() { S = "sort-1" },
                },
                ConsistentRead = true,
            }).ConfigureAwait(false);
            Assert.Equal("Alice", read.Item["name"].S);
            Assert.Equal("99999999999999999999999999999999999999", read.Item["big"].N);
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(read.Item["blob"].B.ToArray()));
            Assert.Equal(futureExpiry, read.Item["ttl"].N);

            var query = await candidate.QueryAsync(new QueryRequest
            {
                TableName = targetTable,
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new() { S = "partition-1" },
                },
            }).ConfigureAwait(false);
            Assert.Single(query.Items);
            var scan = await candidate.ScanAsync(
                new ScanRequest { TableName = targetTable }).ConfigureAwait(false);
            Assert.Single(scan.Items);

            var sourceAfter = await CosmosRestBootstrap.ReadDocumentAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                fixture.CosmosDatabase,
                sourceContainer,
                "sort-1",
                "partition-1").ConfigureAwait(false);
            AssertLegacyPayloadEqual(sourceBefore, sourceAfter);
        }
        finally
        {
            if (targetCreated)
            {
                await candidate.DeleteTableAsync(
                    new DeleteTableRequest { TableName = targetTable }).ConfigureAwait(false);
            }
            if (sourceCreated)
            {
                await CosmosRestBootstrap.DeleteContainerAsync(
                    http,
                    fixture.CosmosEndpoint,
                    fixture.CosmosKey,
                    fixture.CosmosDatabase,
                    sourceContainer).ConfigureAwait(false);
            }
        }
    }

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

    private static string BuildLegacyDocument(JsonElement frozen, string futureExpiry)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in frozen.EnumerateObject())
            {
                if (property.Name is "_rid" or "_etag")
                {
                    continue;
                }
                writer.WritePropertyName(property.Name);
                if (property.Name == "item")
                {
                    writer.WriteStartObject();
                    foreach (var attribute in property.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(attribute.Name);
                        if (attribute.Name == "ttl")
                        {
                            writer.WriteStartObject();
                            writer.WriteString("N", futureExpiry);
                            writer.WriteEndObject();
                        }
                        else
                        {
                            attribute.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Dictionary<string, AttributeValue> ToAttributeMap(
        Dictionary<string, JsonElement> values)
    {
        var result = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var typed = pair.Value;
            if (typed.TryGetProperty("S", out var text))
            {
                result[pair.Key] = new AttributeValue { S = text.GetString() };
            }
            else if (typed.TryGetProperty("N", out var number))
            {
                result[pair.Key] = new AttributeValue { N = number.GetString() };
            }
            else if (typed.TryGetProperty("B", out var binary))
            {
                result[pair.Key] = new AttributeValue
                {
                    B = new MemoryStream(Convert.FromBase64String(binary.GetString()!)),
                };
            }
            else
            {
                throw new InvalidOperationException(
                    $"Migration fixture attribute {pair.Key} has an unsupported type.");
            }
        }
        return result;
    }

    private static void AssertLegacyPayloadEqual(string before, string after)
    {
        using var beforeDocument = JsonDocument.Parse(before);
        using var afterDocument = JsonDocument.Parse(after);
        Assert.Equal(
            beforeDocument.RootElement.GetProperty("pk").GetString(),
            afterDocument.RootElement.GetProperty("pk").GetString());
        Assert.Equal(
            beforeDocument.RootElement.GetProperty("item").GetRawText(),
            afterDocument.RootElement.GetProperty("item").GetRawText());
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
