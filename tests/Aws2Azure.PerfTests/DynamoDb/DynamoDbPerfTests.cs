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

    /// <summary>
    /// Table seeded with LARGE items (many attributes, multi-KB) so the
    /// text-vs-binary A/B can exercise the regime where decode CPU is a
    /// meaningful fraction of per-request work — point reads / read-many of
    /// small (~256 B) items are transport-dominated and hide the translation
    /// cost (see brazilsouth-realazure-results.md). pk+sk schema mirrors
    /// QueryTableName so it supports GetItem (point), Query (by pk) and
    /// BatchGetItem (by keys).
    /// </summary>
    public string LargeTableName { get; } = "perflrg" + Guid.NewGuid().ToString("N")[..8];
    public int LargeSeededPartitions => 5;
    public int LargeSeededItemsPerPartition => 10;
    /// <summary>String attributes per large item (each ~40 B value).</summary>
    public int LargeItemStringAttrs => 400;
    /// <summary>Number attributes per large item.</summary>
    public int LargeItemNumberAttrs => 800;
    public IReadOnlyList<string> SeededBuckets { get; } = new[] { "A", "B", "C" };
    public string AccessKeyId => "AKIA-PERF-DDB";
    public string Secret => "perf-ddb-secret";

    /// <summary>
    /// Table with a numeric Local Secondary Index (byScore on a high-precision
    /// N sort key) for the #504 opt-in numeric-ordering A/B. Seeded with 21-digit
    /// integers (10^20 + i) that exceed IEEE-754 double precision, so they are
    /// stored in the {"_a2a:N":…} envelope — the regime where flag-off orders
    /// structurally + over-fetches (residual re-check in-process) and flag-on
    /// orders by / range-filters exactly against the encoded `_a2a$ord$score`
    /// field. The producer emits that field on write regardless of the flag, so
    /// the seeded data is identical between the two runs; only the query SQL
    /// differs.
    /// </summary>
    public string LsiNumericTableName { get; } = "perflsinum" + Guid.NewGuid().ToString("N")[..8];
    public int LsiNumericPartitions => 5;
    public int LsiNumericItemsPerPartition => 100;
    /// <summary>Big-integer base (10^20) added to a per-item index to build the
    /// high-precision N sort values.</summary>
    public static System.Numerics.BigInteger LsiNumericBase => System.Numerics.BigInteger.Pow(10, 20);

    /// <summary>
    /// Whether the proxy was configured with the #504 opt-in LSI numeric
    /// ordering flag (<c>AWS2AZURE_PERF_LSI_NUMERIC_ORDER=1</c>). Read on both
    /// emulator and real-Azure paths (unlike CosmosBinary, which is real-Azure
    /// only). Drives the query path A/B and the PerfReport row label.
    /// </summary>
    public bool LsiNumericOrderingEnabled { get; private set; }

    /// <summary>Row label for the #504 A/B: which LSI ordering path is active.</summary>
    public string LsiOrderingLabel =>
        LsiNumericOrderingEnabled ? "encoded-order ON" : "raw-order OFF";

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

    /// <summary>
    /// Backend without the text/binary mode suffix — for the SDK baseline rows,
    /// which are mode-agnostic (the native Cosmos SDK has no CosmosBinary toggle).
    /// </summary>
    public string BackendName => IsRealAzure ? "real-Azure" : "emulator";

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

            // CosmosBinary read/write paths and the #504 LSI numeric-ordering
            // flag are top-level "dynamodb" block (DynamoDbSettings) props, NOT
            // services.dynamodb (which only carries "enabled"). Mirrors
            // RealAzureProxyFixture. The block is emitted only when at least one
            // opt-in prop is active so the default emulator path is unchanged.
            var ddbProps = new List<string>();
            if (CosmosBinaryEnabled)
            {
                ddbProps.Add("\"cosmosBinaryResponses\": true");
                ddbProps.Add("\"cosmosBinaryRequests\": true");
            }
            if (LsiNumericOrderingEnabled)
            {
                ddbProps.Add("\"enableLocalSecondaryIndexNumericOrdering\": true");
            }
            var dynamoDbBlock = ddbProps.Count > 0
                ? $", {string.Join(", ", ddbProps)}"
                : string.Empty;

            var config = $$"""
                {
                  "services": {
                    "s3":       { "enabled": false },
                    "sqs":      { "enabled": false },
                    "dynamodb": { "enabled": true{{dynamoDbBlock}} }
                  },
                  "bindings": [
                    {
                      "aws": {
                        "accessKeyId": "{{AccessKeyId}}",
                        "secretAccessKey": "{{Secret}}"
                      },
                      "azure": {
                        "dynamodb": {
                          "kind": "cosmos",
                          "target": {
                            "endpoint": "{{cosmosEndpoint}}",
                            "databaseName": "{{DatabaseName}}"
                          },
                          "auth": {
                            "mode": "sharedKey",
                            "key": "{{cosmosKey}}"
                          }
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

            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = LargeTableName,
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

            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = LsiNumericTableName,
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                    new AttributeDefinition("sk", ScalarAttributeType.S),
                    new AttributeDefinition("score", ScalarAttributeType.N),
                ],
                KeySchema =
                [
                    new KeySchemaElement("pk", KeyType.HASH),
                    new KeySchemaElement("sk", KeyType.RANGE),
                ],
                LocalSecondaryIndexes =
                [
                    new LocalSecondaryIndex
                    {
                        IndexName = "byScore",
                        KeySchema =
                        [
                            new KeySchemaElement("pk", KeyType.HASH),
                            new KeySchemaElement("score", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);

            // Wait until all tables are ACTIVE.
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            foreach (var name in new[] { TableName, QueryTableName, LargeTableName, LsiNumericTableName })
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
            // Seed LargeTableName: LargeSeededPartitions × LargeSeededItemsPerPartition LARGE items.
            await SeedLargeTableAsync(ddb).ConfigureAwait(false);
            // Seed LsiNumericTableName: high-precision N sort values for the #504 A/B.
            await SeedLsiNumericTableAsync(ddb).ConfigureAwait(false);
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
                foreach (var name in new[] { TableName, QueryTableName, LargeTableName, LsiNumericTableName })
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

        // The #504 LSI numeric-ordering flag is meaningful on both backends (the
        // query-path A/B is proxy-side, independent of emulator vs real Azure).
        LsiNumericOrderingEnabled = Env("AWS2AZURE_PERF_LSI_NUMERIC_ORDER") == "1";

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
                    await FlushBatchAsync(ddb, QueryTableName, pending).ConfigureAwait(false);
                    pending.Clear();
                }
            }
            if (pending.Count > 0)
            {
                await FlushBatchAsync(ddb, QueryTableName, pending).ConfigureAwait(false);
            }
        }
    }

    private static async Task FlushBatchAsync(AmazonDynamoDBClient ddb, string table, List<WriteRequest> batch)
    {
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>
            {
                [table] = batch,
            },
        }).ConfigureAwait(false);
        // Retry unprocessed once (emulator can throttle); fail loudly otherwise.
        if (resp.UnprocessedItems is { Count: > 0 } unp && unp.TryGetValue(table, out var leftovers) && leftovers.Count > 0)
        {
            await Task.Delay(100).ConfigureAwait(false);
            var retry = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>> { [table] = leftovers },
            }).ConfigureAwait(false);
            if (retry.UnprocessedItems is { Count: > 0 } left2 && left2.TryGetValue(table, out var still) && still.Count > 0)
            {
                throw new InvalidOperationException($"Seed: {still.Count} items still unprocessed after retry.");
            }
        }
    }

    /// <summary>
    /// Builds one large DynamoDB item: pk + sk plus
    /// <see cref="LargeItemStringAttrs"/> string attributes (~40 B values) and
    /// <see cref="LargeItemNumberAttrs"/> number attributes. Mirrors the
    /// large-item benchmark corpus (≈26 KB, hundreds of attributes) so the
    /// proxy's per-attribute decode/encode walk — where CosmosBinary can win —
    /// is a meaningful share of per-request CPU.
    /// </summary>
    public Dictionary<string, AttributeValue> BuildLargeItem(string pk, string sk, Random rand)
    {
        var item = new Dictionary<string, AttributeValue>(LargeItemStringAttrs + LargeItemNumberAttrs + 2)
        {
            ["pk"] = new() { S = pk },
            ["sk"] = new() { S = sk },
        };
        for (var i = 0; i < LargeItemStringAttrs; i++)
        {
            item[$"s{i:D4}"] = new AttributeValue { S = "v-" + new string((char)('a' + (i % 26)), 38) };
        }
        for (var i = 0; i < LargeItemNumberAttrs; i++)
        {
            item[$"n{i:D4}"] = new AttributeValue { N = rand.Next(0, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
        return item;
    }

    private async Task SeedLsiNumericTableAsync(AmazonDynamoDBClient ddb)
    {
        // High-precision N sort keys (10^20 + si): 21-digit integers that exceed
        // IEEE-754 double precision, so the storage layer keeps them in the
        // {"_a2a:N":…} envelope. Small items → BatchWriteItem (25/call).
        var baseValue = LsiNumericBase;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        for (var pi = 0; pi < LsiNumericPartitions; pi++)
        {
            var pk = $"p{pi:D2}";
            var pending = new List<WriteRequest>(25);
            for (var si = 0; si < LsiNumericItemsPerPartition; si++)
            {
                var score = (baseValue + si).ToString(culture);
                pending.Add(new WriteRequest(new PutRequest(new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = pk },
                    ["sk"] = new() { S = $"s{si:D4}" },
                    ["score"] = new() { N = score },
                    ["payload"] = new() { S = "seed-256-bytes-of-padding-" + new string('x', 200) },
                })));
                if (pending.Count == 25)
                {
                    await FlushBatchAsync(ddb, LsiNumericTableName, pending).ConfigureAwait(false);
                    pending.Clear();
                }
            }
            if (pending.Count > 0)
            {
                await FlushBatchAsync(ddb, LsiNumericTableName, pending).ConfigureAwait(false);
            }
        }
    }

    private async Task SeedLargeTableAsync(AmazonDynamoDBClient ddb)
    {
        var rand = new Random(20260618);
        for (var pi = 0; pi < LargeSeededPartitions; pi++)
        {
            var pk = $"p{pi:D2}";
            for (var si = 0; si < LargeSeededItemsPerPartition; si++)
            {
                // Large items exceed BatchWriteItem's 16 MB/400 KB envelope when
                // bundled, so seed them one at a time with PutItem.
                await ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = LargeTableName,
                    Item = BuildLargeItem(pk, $"s{si:D4}", rand),
                }).ConfigureAwait(false);
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
            concurrency: PerfConcurrency.Scale(16),
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
            concurrency: PerfConcurrency.Scale(8),
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
            concurrency: PerfConcurrency.Scale(4),
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

    // #504 A/B: run the suite twice, toggling AWS2AZURE_PERF_LSI_NUMERIC_ORDER,
    // and diff the two baseline-latest.json files. The seeded data is identical
    // between runs (the producer writes `_a2a$ord$score` unconditionally); only
    // the LSI query SQL differs — flag-off orders by / filters the raw envelope
    // attribute (structural order + in-process residual over-fetch for
    // high-precision N), flag-on orders by / range-filters the encoded field
    // exactly. The row label carries the active path (LsiOrderingLabel).

    [SkippableFact]
    public async Task Query_lsi_numeric_ordered_throughput()
    {
        // Pure ORDER BY: returns the whole partition ordered by the high-precision
        // N sort key. Isolates the SELECT * overhead (the encoded field rides back
        // Cosmos→proxy even when stripped before the AWS response) — the case
        // where flag-on is expected to cost marginally MORE data, not less.
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.Query LSI numeric (ordered)",
            concurrency: PerfConcurrency.Scale(8),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                var pk = $"p{workerId % fixture.LsiNumericPartitions:D2}";
                var resp = await client.QueryAsync(new QueryRequest
                {
                    TableName = fixture.LsiNumericTableName,
                    IndexName = "byScore",
                    KeyConditionExpression = "pk = :p",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":p"] = new() { S = pk },
                    },
                    ScanIndexForward = true,
                }, ct).ConfigureAwait(false);
                if (resp.Items.Count == 0)
                {
                    throw new InvalidOperationException("LSI ordered query returned no items — seed data missing.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos LSI Query ordered by high-precision N sort key — {fixture.LsiOrderingLabel} [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task Query_lsi_numeric_selective_throughput()
    {
        // Selective predicate (score >= 10^20 + half): returns ~half the
        // partition. Flag-off cannot push the high-precision envelope comparison
        // exactly (residual re-check in-process → over-fetch); flag-on pushes it
        // exactly against the encoded field. This is the case where flag-on is
        // expected to transfer FEWER docs and do LESS proxy-side work.
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var bound = (DynamoDbPerfFixture.LsiNumericBase + fixture.LsiNumericItemsPerPartition / 2)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.Query LSI numeric (selective)",
            concurrency: PerfConcurrency.Scale(8),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                var pk = $"p{workerId % fixture.LsiNumericPartitions:D2}";
                var resp = await client.QueryAsync(new QueryRequest
                {
                    TableName = fixture.LsiNumericTableName,
                    IndexName = "byScore",
                    KeyConditionExpression = "pk = :p AND score >= :b",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":p"] = new() { S = pk },
                        [":b"] = new() { N = bound },
                    },
                    ScanIndexForward = true,
                }, ct).ConfigureAwait(false);
                if (resp.Items.Count == 0)
                {
                    throw new InvalidOperationException("LSI selective query returned no items — seed data missing.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos LSI Query high-precision N range predicate (score >= 10^20+half) — {fixture.LsiOrderingLabel} [{fixture.BackendLabel}]");
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
            concurrency: PerfConcurrency.Scale(16),
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
            concurrency: PerfConcurrency.Scale(8),
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
            concurrency: PerfConcurrency.Scale(8),
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

                // Drain UnprocessedItems with exponential backoff, modelling a
                // real DynamoDB client. Under Cosmos RU throttling BatchWriteItem
                // legitimately returns UnprocessedItems (the documented backpressure
                // contract) and the AWS SDK retries them with backoff. Counting the
                // whole batch as ONE op once drained measures the true SUSTAINABLE
                // write throughput on real Azure instead of mislabelling throttle
                // as a hard failure (the emulator rarely throttles, so this only
                // bites on the real backend — issue #420 Tier 2).
                const int maxRetries = 8;
                var delayMs = 25;
                for (var attempt = 0; HasUnprocessed(resp, fixture.TableName, out var leftovers); attempt++)
                {
                    if (attempt >= maxRetries)
                    {
                        throw new InvalidOperationException(
                            $"BatchWriteItem: {leftovers.Count} items still unprocessed after {maxRetries} backoff retries.");
                    }
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    delayMs = Math.Min(delayMs * 2, 1000);
                    resp = await client.BatchWriteItemAsync(new BatchWriteItemRequest
                    {
                        RequestItems = new Dictionary<string, List<WriteRequest>>
                        {
                            [fixture.TableName] = leftovers,
                        },
                    }, ct).ConfigureAwait(false);
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos BatchWriteItem — 25 PutRequest/call, UnprocessedItems retried with backoff [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    private static bool HasUnprocessed(BatchWriteItemResponse resp, string table, out List<WriteRequest> leftovers)
    {
        if (resp.UnprocessedItems is { Count: > 0 } unp
            && unp.TryGetValue(table, out var left) && left.Count > 0)
        {
            leftovers = left;
            return true;
        }
        leftovers = [];
        return false;
    }

    [SkippableFact]
    public async Task UpdateItem_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.UpdateItem (SET expression)",
            concurrency: PerfConcurrency.Scale(16),
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
            concurrency: PerfConcurrency.Scale(16),
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

    // ----------------------------------------------------------------------
    // LARGE-payload scenarios (issue #420 A/B). These read/write multi-KB
    // items with hundreds of attributes, the regime where the proxy's
    // per-attribute decode/encode walk is a meaningful fraction of per-request
    // CPU — and therefore where CosmosBinary (text vs binary) can actually
    // move latency/throughput rather than being lost in transport noise.
    // ----------------------------------------------------------------------

    [SkippableFact]
    public async Task GetItem_large_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.GetItem (large)",
            concurrency: PerfConcurrency.Scale(8),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                var pk = $"p{(workerId < 0 ? 0 : workerId % fixture.LargeSeededPartitions):D2}";
                var sk = $"s{Random.Shared.Next(0, fixture.LargeSeededItemsPerPartition):D4}";
                var resp = await client.GetItemAsync(new GetItemRequest
                {
                    TableName = fixture.LargeTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = pk },
                        ["sk"] = new() { S = sk },
                    },
                    ConsistentRead = false,
                }, ct).ConfigureAwait(false);
                if (resp.Item is null || resp.Item.Count == 0)
                {
                    throw new InvalidOperationException($"GetItem(large) returned no item for pk={pk}, sk={sk} — seed data missing.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos GetItem — LARGE item (~{fixture.LargeItemStringAttrs}s+{fixture.LargeItemNumberAttrs}n attrs) point read [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task PutItem_large_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.PutItem (large)",
            concurrency: PerfConcurrency.Scale(8),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                // Unique pk per write so we exercise the standalone-document
                // write encode path (the CosmosBinaryRequests target) rather
                // than a sproc-conditional upsert. Per-worker partition + GUID
                // sk avoids cross-worker contention.
                var rand = new Random(unchecked(workerId * 2654435761u).GetHashCode());
                var item = fixture.BuildLargeItem(
                    $"lput-w{(workerId < 0 ? 0 : workerId):D2}-{Guid.NewGuid():N}",
                    "s0000",
                    rand);
                await client.PutItemAsync(new PutItemRequest
                {
                    TableName = fixture.LargeTableName,
                    Item = item,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos PutItem — LARGE item (~{fixture.LargeItemStringAttrs}s+{fixture.LargeItemNumberAttrs}n attrs) standalone write [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task Query_large_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.Query (large items)",
            concurrency: PerfConcurrency.Scale(4),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                // Query by pk returns all LargeSeededItemsPerPartition large
                // items in that partition — a heavy multi-item read where the
                // decode walk is amplified by item count × attribute count.
                var pk = $"p{(workerId < 0 ? 0 : workerId % fixture.LargeSeededPartitions):D2}";
                var resp = await client.QueryAsync(new QueryRequest
                {
                    TableName = fixture.LargeTableName,
                    KeyConditionExpression = "pk = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new() { S = pk },
                    },
                    ConsistentRead = false,
                }, ct).ConfigureAwait(false);
                if (resp.Items.Count == 0)
                {
                    throw new InvalidOperationException($"Query(large) returned no items for pk={pk} — seed data missing.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos Query — LARGE items (pk → {fixture.LargeSeededItemsPerPartition} items × ~{fixture.LargeItemStringAttrs}s+{fixture.LargeItemNumberAttrs}n attrs) [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task BatchGetItem_large_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "dynamodb.BatchGetItem (large items)",
            concurrency: PerfConcurrency.Scale(4),
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (workerId, ct) =>
            {
                // Read all large items in one partition by explicit keys —
                // read-many of multi-KB documents.
                var pk = $"p{(workerId < 0 ? 0 : workerId % fixture.LargeSeededPartitions):D2}";
                var keys = new List<Dictionary<string, AttributeValue>>(fixture.LargeSeededItemsPerPartition);
                for (var i = 0; i < fixture.LargeSeededItemsPerPartition; i++)
                {
                    keys.Add(new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new() { S = pk },
                        ["sk"] = new() { S = $"s{i:D4}" },
                    });
                }
                var resp = await client.BatchGetItemAsync(new BatchGetItemRequest
                {
                    RequestItems = new Dictionary<string, KeysAndAttributes>
                    {
                        [fixture.LargeTableName] = new() { Keys = keys, ConsistentRead = false },
                    },
                }, ct).ConfigureAwait(false);
                if (!resp.Responses.TryGetValue(fixture.LargeTableName, out var items)
                    || items.Count != fixture.LargeSeededItemsPerPartition)
                {
                    var actual = items is null ? 0 : items.Count;
                    throw new InvalidOperationException($"BatchGetItem(large) returned {actual}/{fixture.LargeSeededItemsPerPartition} items — partial response invalidates perf measurement.");
                }
            });

        PerfReport.Append(result, notes: $"DynamoDB→Cosmos BatchGetItem — {fixture.LargeSeededItemsPerPartition} LARGE items, single partition [{fixture.BackendLabel}]");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    /// <summary>
    /// Concurrency-saturation sweep for GetItem (issue #420 Tier 2). Opt-in via
    /// <c>AWS2AZURE_PERF_SWEEP=1</c> so the emulator nightly (fixed-concurrency
    /// baseline) is unchanged; the real-Azure A/B job enables it to compare
    /// <b>throughput-at-knee</b> and <b>p99-at-knee</b> between the text and binary
    /// arms under a CPU-constrained proxy — the regime where a CPU/alloc win must
    /// translate to throughput or be rejected. The ladder and per-level duration
    /// come from <c>AWS2AZURE_PERF_SWEEP_LEVELS</c> (csv) and
    /// <c>AWS2AZURE_PERF_SWEEP_SECONDS</c>. Per-level rows plus one
    /// <c>(sweep knee)</c> row are recorded so the two A/B passes are diffable.
    /// </summary>
    [SkippableFact]
    public async Task GetItem_saturation_sweep()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_PERF_SWEEP"), "1", StringComparison.Ordinal),
            "saturation sweep is opt-in (set AWS2AZURE_PERF_SWEEP=1) — Tier 2 only.");

        var levels = ParseSweepLevels(
            Environment.GetEnvironmentVariable("AWS2AZURE_PERF_SWEEP_LEVELS"),
            fallback: new[] { 8, 16, 32, 64, 128 });
        var perLevelSeconds = ParseSweepSeconds(
            Environment.GetEnvironmentVariable("AWS2AZURE_PERF_SWEEP_SECONDS"),
            fallback: 8);

        using var client = fixture.CreateClient();

        var sweep = await PerfSweep.RunSweepAsync(
            scenario: "dynamodb.GetItem",
            levels: levels,
            perLevelDuration: TimeSpan.FromSeconds(perLevelSeconds),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbeFactory: fixture.CreateMemoryProbe,
            action: async (workerId, ct) =>
            {
                var pk = $"p{(workerId < 0 ? 0 : workerId) % fixture.SeededPartitions:D2}";
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
            }).ConfigureAwait(false);

        Console.WriteLine(sweep.Describe());

        // Record the full curve (one row per level) plus a single comparable knee
        // row, both labelled with the backend so the text/binary A/B is traceable.
        foreach (var level in sweep.Levels)
        {
            PerfReport.Append(level, notes: $"DynamoDB→Cosmos GetItem sweep [{fixture.BackendLabel}]");
        }
        var kneeRow = sweep.KneeLevel with { Scenario = "dynamodb.GetItem (sweep knee)" };
        var saturationNote = sweep.Knee.ReachedSaturation
            ? $"knee c={sweep.Knee.KneeConcurrency}, max {sweep.Knee.MaxThroughput:0.0}/s @ c={sweep.Knee.MaxThroughputConcurrency}"
            : $"NOT SATURATED (ladder ended at the knee c={sweep.Knee.KneeConcurrency}) — widen AWS2AZURE_PERF_SWEEP_LEVELS";
        PerfReport.Append(kneeRow, notes: $"DynamoDB→Cosmos GetItem — {saturationNote} [{fixture.BackendLabel}]");

        // Health on EVERY rung (not just the knee): PerfRunner records action
        // exceptions as Failures without throwing, so a rung that overloads the
        // backend would post depressed throughput and could masquerade as a
        // plateau. Asserting each level keeps the knee from being an artifact of a
        // failing rung — if a rung blows the failure budget the ladder is too
        // aggressive (shrink AWS2AZURE_PERF_SWEEP_LEVELS) and the run fails loudly.
        // No AssertNoRegression: the sweep scenarios are deliberately absent from
        // baseline-reference.json (the knee is regime-dependent); the A/B verdict
        // comes from diffing the two passes' knee rows in the artifacts.
        foreach (var level in sweep.Levels)
        {
            level.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        }
    }

    private static int[] ParseSweepLevels(string? raw, int[] fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        var parsed = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Where(v => v > 0)
            .ToArray();
        return parsed.Length > 0 ? parsed : fallback;
    }

    private static int ParseSweepSeconds(string? raw, int fallback) =>
        int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}
