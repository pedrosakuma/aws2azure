using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.DynamoDb;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class DynamoDbPerfFixture : IAsyncLifetime
{
    private const string ContainerImage =
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
    private const string EmulatorMasterKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    private const string EmulatorDatabaseName = "aws2azure-perf";

    private IContainer? _container;
    private bool _proxyStarted;
    private readonly PerfProxyProcess _proxy = new();

    public bool Ready { get; private set; }
    public string? SkipReason { get; private set; }
    public string ServiceUrl => _proxy.ServiceUrlForHost("dynamodb");
    public string ProxyOutput => _proxy.Output;
    public ProxyMemoryProbe CreateMemoryProbe() => _proxy.CreateMemoryProbe();
    public string TableName { get; } = "perftbl" + Guid.NewGuid().ToString("N")[..8];
    public string QueryTableName { get; } = "perfqry" + Guid.NewGuid().ToString("N")[..8];
    public int SeededPartitions => 10;
    public int SeededItemsPerPartition => 50;
    public IReadOnlyList<string> SeededBuckets { get; } = new[] { "A", "B", "C" };
    public string AccessKeyId => "AKIA-PERF-DDB";
    public string Secret => "perf-ddb-secret";

    /// <summary>
    /// Cosmos database name. The emulator path bootstraps
    /// <see cref="EmulatorDatabaseName"/>; the real-Azure path uses
    /// <c>AZURE_COSMOS_DATABASE</c> (which must already exist — the module
    /// provisions containers, not the database).
    /// </summary>
    public string DatabaseName { get; private set; } = EmulatorDatabaseName;

    /// <summary>Cosmos DB account endpoint exposed for SDK-baseline scenarios.</summary>
    public string CosmosEndpoint { get; private set; } = string.Empty;
    /// <summary>Cosmos DB account master key (emulator well-known key, or
    /// <c>AZURE_COSMOS_KEY</c> for the real-Azure path).</summary>
    public string CosmosMasterKey { get; private set; } = EmulatorMasterKey;

    /// <summary>
    /// True when the fixture is driving a live Azure Cosmos DB account
    /// (<c>AZURE_COSMOS_ENDPOINT/KEY/DATABASE</c> all set) instead of the
    /// emulator container — the Tier 2 backend (issue #420). Emulators neither
    /// emit nor reliably accept CosmosBinary, so the binary A/B is only
    /// meaningful here.
    /// </summary>
    public bool IsRealAzure { get; private set; }

    /// <summary>
    /// Whether the proxy was configured with CosmosBinary read/write paths on
    /// (<c>AWS2AZURE_PERF_COSMOS_BINARY=1</c>). Exposed so scenarios can label
    /// their <see cref="PerfReport"/> notes for the binary-vs-text A/B. Off by
    /// default so the emulator path is behaviorally unchanged.
    /// </summary>
    public bool CosmosBinaryEnabled { get; private set; }

    /// <summary>
    /// Human-readable backend label for <see cref="PerfReport"/> notes so the
    /// binary-vs-text A/B is traceable: <c>emulator</c>, <c>real-Azure (text)</c>,
    /// or <c>real-Azure (binary)</c>.
    /// </summary>
    public string BackendLabel => IsRealAzure
        ? (CosmosBinaryEnabled ? "real-Azure (binary)" : "real-Azure (text)")
        : "emulator";

    public AmazonDynamoDBClient CreateClient() => new(
        AccessKeyId,
        Secret,
        new AmazonDynamoDBConfig
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = "us-east-1",
            UseHttp = true,
        });

    public async Task InitializeAsync()
    {
        if (!PerfGate.Enabled)
        {
            SkipReason = "AWS2AZURE_PERF=1 not set.";
            return;
        }

        try
        {
            ResolveBackend();

            string cosmosEndpoint;
            string cosmosKey;
            if (IsRealAzure)
            {
                // Tier 2 (#420): drive a live Cosmos DB account. The database
                // must already exist; we only own the table (container)
                // lifecycle, mirroring DynamoDbRealAzureSmokeTests.
                cosmosEndpoint = CosmosEndpoint;
                cosmosKey = CosmosMasterKey;
            }
            else
            {
                _container = new ContainerBuilder()
                    .WithImage(ContainerImage)
                    .WithName("aws2azure-perf-cosmos-" + Guid.NewGuid().ToString("N")[..8])
                    .WithPortBinding(8081, true)
                    .WithWaitStrategy(Wait.ForUnixContainer()
                        .UntilMessageIsLogged("System is now fully ready to accept requests"))
                    .Build();
                await _container.StartAsync().ConfigureAwait(false);
                var port = _container.GetMappedPublicPort(8081);
                cosmosEndpoint = $"http://{_container.Hostname}:{port}/";
                cosmosKey = EmulatorMasterKey;
                CosmosEndpoint = cosmosEndpoint;
                CosmosMasterKey = cosmosKey;

                using var bootstrap = new HttpClient();
                await CosmosRestBootstrap.EnsureDatabaseAsync(
                    bootstrap, cosmosEndpoint, cosmosKey, DatabaseName).ConfigureAwait(false);
            }

            // CosmosBinary read/write paths are a top-level "dynamodb" block
            // (DynamoDbSettings), NOT services.dynamodb (which only carries
            // "enabled"). Mirrors RealAzureProxyFixture; emitted only for the
            // real-Azure binary A/B.
            var dynamoDbBlock = CosmosBinaryEnabled
                ? "  \"dynamodb\": { \"cosmosBinaryResponses\": true, \"cosmosBinaryRequests\": true },\n"
                : string.Empty;

            var config = $$"""
                {
                  "services": {
                    "s3":       { "enabled": false },
                    "sqs":      { "enabled": false },
                    "dynamodb": { "enabled": true }
                  },
                {{dynamoDbBlock}}  "credentials": [
                    {
                      "awsAccessKeyId": "{{AccessKeyId}}",
                      "awsSecretAccessKey": "{{Secret}}",
                      "azure": {
                        "cosmos": {
                          "endpoint":     "{{cosmosEndpoint}}",
                          "primaryKey":   "{{cosmosKey}}",
                          "databaseName": "{{DatabaseName}}"
                        }
                      }
                    }
                  ]
                }
                """;
            await _proxy.StartAsync(config, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            // Proxy is alive; any CreateTable below may create live Cosmos
            // containers, so arm teardown cleanup before issuing them.
            _proxyStarted = true;

            using var ddb = CreateClient();
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = TableName,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);

            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = QueryTableName,
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                    new AttributeDefinition("sk", ScalarAttributeType.S),
                ],
                KeySchema =
                [
                    new KeySchemaElement("pk", KeyType.HASH),
                    new KeySchemaElement("sk", KeyType.RANGE),
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);

            // Wait until both tables are ACTIVE.
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            foreach (var name in new[] { TableName, QueryTableName })
            {
                while (DateTime.UtcNow < deadline)
                {
                    var d = await ddb.DescribeTableAsync(name).ConfigureAwait(false);
                    if (d.Table.TableStatus == TableStatus.ACTIVE) break;
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }

            // Seed QueryTableName: SeededPartitions × SeededItemsPerPartition items.
            await SeedQueryTableAsync(ddb).ConfigureAwait(false);
            Ready = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"Fixture init failed: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        // Real Azure: drop the containers we may have created so live accounts
        // don't accumulate perf tables (the emulator path is torn down with the
        // container, so it needs no explicit cleanup). Gated on _proxyStarted
        // (not Ready) so tables created before a mid-init failure are still
        // removed. Best-effort — deletion goes through the proxy, which must
        // still be alive here, and DeleteTable on a never-created table no-ops.
        if (IsRealAzure && _proxyStarted)
        {
            try
            {
                using var ddb = CreateClient();
                foreach (var name in new[] { TableName, QueryTableName })
                {
                    try
                    {
                        await ddb.DeleteTableAsync(name).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore — leftover tables are a billing nuisance, not a
                        // test failure.
                    }
                }
            }
            catch
            {
                // Ignore client construction failures during teardown.
            }
        }

        await _proxy.DisposeAsync().ConfigureAwait(false);
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the Cosmos backend from the environment. When
    /// <c>AZURE_COSMOS_ENDPOINT/KEY/DATABASE</c> are all present the fixture
    /// targets a live Azure account (Tier 2, issue #420) and skips the emulator
    /// container; otherwise it falls back to the emulator default. The
    /// CosmosBinary read/write paths are enabled only when
    /// <c>AWS2AZURE_PERF_COSMOS_BINARY=1</c> (and never against the emulator,
    /// which does not emit binary bodies).
    /// </summary>
    private void ResolveBackend()
    {
        var endpoint = Env("AZURE_COSMOS_ENDPOINT");
        var key = Env("AZURE_COSMOS_KEY");
        var database = Env("AZURE_COSMOS_DATABASE");

        IsRealAzure = endpoint is not null && key is not null && database is not null;
        if (IsRealAzure)
        {
            CosmosEndpoint = endpoint!;
            CosmosMasterKey = key!;
            DatabaseName = database!;
            CosmosBinaryEnabled = Env("AWS2AZURE_PERF_COSMOS_BINARY") == "1";
        }

        static string? Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    private async Task SeedQueryTableAsync(AmazonDynamoDBClient ddb)
    {
        // Spread N items over SeededPartitions partition keys; each item has
        // bucket ∈ {A,B,C} (cycled so each bucket is roughly 1/3 of rows) and
        // a numeric score in [0,100). Query/Scan perf scenarios filter on
        // these attributes via FilterExpression so the FilterPushdownVisitor
        // path is exercised.
        var rand = new Random(20260526);
        for (var pi = 0; pi < SeededPartitions; pi++)
        {
            var pk = $"p{pi:D2}";
            // BatchWriteItem holds at most 25 items per call.
            var pending = new List<WriteRequest>(25);
            for (var si = 0; si < SeededItemsPerPartition; si++)
            {
                var bucket = SeededBuckets[(pi * SeededItemsPerPartition + si) % SeededBuckets.Count];
                pending.Add(new WriteRequest(new PutRequest(new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = pk },
                    ["sk"] = new() { S = $"s{si:D4}" },
                    ["bucket"] = new() { S = bucket },
                    ["score"] = new() { N = rand.Next(0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    ["payload"] = new() { S = "seed-256-bytes-of-padding-" + new string('x', 200) },
                })));
                if (pending.Count == 25)
                {
                    await FlushBatchAsync(ddb, pending).ConfigureAwait(false);
                    pending.Clear();
                }
            }
            if (pending.Count > 0)
            {
                await FlushBatchAsync(ddb, pending).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushBatchAsync(AmazonDynamoDBClient ddb, List<WriteRequest> batch)
    {
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>
            {
                [QueryTableName] = batch,
            },
        }).ConfigureAwait(false);
        // Retry unprocessed once (emulator can throttle); fail loudly otherwise.
        if (resp.UnprocessedItems is { Count: > 0 } unp && unp.TryGetValue(QueryTableName, out var leftovers) && leftovers.Count > 0)
        {
            await Task.Delay(100).ConfigureAwait(false);
            var retry = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>> { [QueryTableName] = leftovers },
            }).ConfigureAwait(false);
            if (retry.UnprocessedItems is { Count: > 0 } left2 && left2.TryGetValue(QueryTableName, out var still) && still.Count > 0)
            {
                throw new InvalidOperationException($"Seed: {still.Count} items still unprocessed after retry.");
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DynamoDbPerfCollection : ICollectionFixture<DynamoDbPerfFixture>
{
    public const string Name = "dynamodb-perf";
}

[Collection(DynamoDbPerfCollection.Name)]
public sealed class DynamoDbPerfTests(DynamoDbPerfFixture fixture)
{
    [SkippableFact]
    public async Task PutItem_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.PutItem (small)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                var key = $"perf-w{workerId:D2}-{Guid.NewGuid():N}";
                await client.PutItemAsync(new PutItemRequest
                {
                    TableName = fixture.TableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new AttributeValue { S = key },
                        ["payload"] = new AttributeValue { S = "perf-payload-256-bytes" },
                    },
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos (REST) [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task Query_with_pushable_filter_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.Query (pushable filter)",
            concurrency: 8,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                var pk = $"p{workerId % fixture.SeededPartitions:D2}";
                // bucket = "A" pushes down to SQL via FilterPushdownVisitor.
                var resp = await client.QueryAsync(new QueryRequest
                {
                    TableName = fixture.QueryTableName,
                    KeyConditionExpression = "pk = :pk",
                    FilterExpression = "bucket = :b",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new() { S = pk },
                        [":b"] = new() { S = "A" },
                    },
                }, ct).ConfigureAwait(false);
                if (resp.Items.Count == 0)
                {
                    throw new InvalidOperationException("Query returned no items — seed data missing.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos Query — FilterPushdownVisitor (pushable eq on bucket) [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task Scan_with_pushable_filter_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.Scan (pushable filter)",
            concurrency: 4,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                // Ordered numeric BETWEEN — pushable; envelope branch uses IS_DEFINED.
                var resp = await client.ScanAsync(new ScanRequest
                {
                    TableName = fixture.QueryTableName,
                    FilterExpression = "score BETWEEN :lo AND :hi",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":lo"] = new() { N = "20" },
                        [":hi"] = new() { N = "60" },
                    },
                    Limit = 100,
                }, ct).ConfigureAwait(false);
                _ = resp.ScannedCount;
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos Scan — FilterPushdownVisitor (BETWEEN on score, Limit=100) [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task GetItem_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.GetItem (small)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                // Round-robin over the 10×50 seeded items in QueryTableName so
                // every worker hits a different partition + sort key on each
                // call and we exercise the point-read fast-path
                // (DynamoDb→Cosmos ReadItemAsync via REST).
                var pk = $"p{workerId % fixture.SeededPartitions:D2}";
                var sk = $"s{Random.Shared.Next(0, fixture.SeededItemsPerPartition):D4}";
                var resp = await client.GetItemAsync(new GetItemRequest
                {
                    TableName = fixture.QueryTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = pk },
                        ["sk"] = new() { S = sk },
                    },
                    ConsistentRead = false,
                }, ct).ConfigureAwait(false);
                if (resp.Item is null || resp.Item.Count == 0)
                {
                    throw new InvalidOperationException($"GetItem returned no item for pk={pk}, sk={sk} — seed data missing.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos GetItem — point read against seeded QueryTable [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task BatchGetItem_25_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.BatchGetItem (25 items)",
            concurrency: 8,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                // Pin to one partition per call (Cosmos read-many is most
                // efficient within a single partition) and pick 25 distinct
                // sort keys from the seed range.
                var pk = $"p{workerId % fixture.SeededPartitions:D2}";
                var keys = new List<Dictionary<string, AttributeValue>>(25);
                var seen = new HashSet<int>();
                while (keys.Count < 25)
                {
                    var idx = Random.Shared.Next(0, fixture.SeededItemsPerPartition);
                    if (!seen.Add(idx)) continue;
                    keys.Add(new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = pk },
                        ["sk"] = new() { S = $"s{idx:D4}" },
                    });
                }
                var resp = await client.BatchGetItemAsync(new BatchGetItemRequest
                {
                    RequestItems = new Dictionary<string, KeysAndAttributes>
                    {
                        [fixture.QueryTableName] = new() { Keys = keys, ConsistentRead = false },
                    },
                }, ct).ConfigureAwait(false);
                if (!resp.Responses.TryGetValue(fixture.QueryTableName, out var items) || items.Count != 25)
                {
                    var actual = items is null ? 0 : items.Count;
                    throw new InvalidOperationException($"BatchGetItem returned {actual}/25 items — partial response invalidates perf measurement (seed gap or proxy regression).");
                }
                if (resp.UnprocessedKeys is { Count: > 0 } unp
                    && unp.TryGetValue(fixture.QueryTableName, out var left) && left.Keys.Count > 0)
                {
                    throw new InvalidOperationException($"BatchGetItem: {left.Keys.Count} unprocessed keys.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos BatchGetItem — 25 keys, single partition [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task BatchWriteItem_25_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.BatchWriteItem (25 items)",
            concurrency: 8,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                var batch = new List<WriteRequest>(25);
                for (var i = 0; i < 25; i++)
                {
                    batch.Add(new WriteRequest(new PutRequest(new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = $"bwi-w{workerId:D2}-{Guid.NewGuid():N}" },
                        ["payload"] = new() { S = "bwi-256B" },
                    })));
                }
                var resp = await client.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        [fixture.TableName] = batch,
                    },
                }, ct).ConfigureAwait(false);
                if (resp.UnprocessedItems is { Count: > 0 } unp
                    && unp.TryGetValue(fixture.TableName, out var left) && left.Count > 0)
                {
                    // Emulator throttling — treat as a soft failure surfaced via failure count.
                    throw new InvalidOperationException($"BatchWriteItem: {left.Count} unprocessed.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos BatchWriteItem — 25 PutRequest/call [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task UpdateItem_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.UpdateItem (SET expression)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                // Round-robin over the 10×50 seeded items so each call rewrites
                // an existing item — exercises the UpdateExpression parse +
                // translation path (DynamoDb→Cosmos PATCH/replace) rather than
                // an upsert of a brand-new document. Normalize the warmup
                // worker id (-1) to a seeded partition so warmup doesn't upsert
                // junk rows into the shared QueryTable.
                var pk = $"p{(workerId < 0 ? 0 : workerId % fixture.SeededPartitions):D2}";
                var sk = $"s{Random.Shared.Next(0, fixture.SeededItemsPerPartition):D4}";
                await client.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = fixture.QueryTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = pk },
                        ["sk"] = new() { S = sk },
                    },
                    UpdateExpression = "SET payload = :p, score = :s",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":p"] = new() { S = "upd-256-bytes-of-padding-" + new string('y', 200) },
                        [":s"] = new() { N = Random.Shared.Next(0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    },
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos UpdateItem — SET expression on seeded QueryTable items [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task DeleteItem_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.DeleteItem (idempotent)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                // Delete unique keys that were never written. DynamoDB DeleteItem
                // is idempotent — a missing item is a 200 success — and the
                // proxy's translation cost (table-metadata read + Cosmos DELETE)
                // is independent of item existence. This avoids seed-pool
                // exhaustion over a 20 s run while faithfully measuring the
                // DeleteItem hot path.
                var key = $"del-w{workerId:D2}-{Guid.NewGuid():N}";
                await client.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = fixture.TableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = key },
                    },
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos DeleteItem — idempotent delete of unique non-existent keys (200 no-op; cost independent of existence) [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
