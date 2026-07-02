using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Azure validation for DynamoDB secondary-index access (issue #461),
/// exercised against live Azure Cosmos DB through the proxy. The proxy serves
/// Global Secondary Indexes with the Option A strategy — a single base
/// container, cross-partition Cosmos queries filtered on the raw index
/// attribute, with <c>ORDER BY</c> for composite indexes — so the behaviours
/// that only manifest on real Cosmos (cross-partition merge-sort ORDER BY,
/// multi-page continuation pagination, and Cosmos asynchronous indexing lag)
/// cannot be reproduced on the single-partition CI emulator. The nightly proxy
/// enables <c>EnableGlobalSecondaryIndexQueries</c> (see
/// <see cref="RealAzureProxyFixture"/>); these tests skip when the
/// <c>AZURE_COSMOS_*</c> secrets are absent.
///
/// Cosmos indexes documents asynchronously, so a freshly written item may not
/// be visible to a query for a short window. Every query/scan assertion polls
/// until the expected member set converges (or a deadline elapses) before
/// asserting ordering / membership, which both tolerates and validates that
/// convergence — the closest analogue to DynamoDB GSI eventual consistency in
/// the Option A model, where the proxy reads the live base document and there
/// is no separate index replica to lag.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureSecondaryIndexTests
{
    private readonly RealAzureProxyFixture _fx;
    private readonly ITestOutputHelper _output;

    // ~45 KB per item; 100 items sharing one GSI hash value ≈ 4.5 MB, which
    // exceeds the Cosmos cross-partition query response page limit (~4 MB) and
    // forces the ordered GSI query to span multiple real continuation pages.
    private const int LargeItemCount = 100;
    private const int PayloadBytes = 45 * 1024;

    // Spread items across many base partitions so the GSI query is genuinely
    // cross-partition (Cosmos must merge-sort ORDER BY across partitions).
    private const int PartitionSpread = 16;

    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(45);

    public DynamoDbRealAzureSecondaryIndexTests(RealAzureProxyFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    [SkippableFact]
    public async Task Gsi_hash_only_query_returns_members_across_partitions()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("gsih");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            // Five items share customer="acme" across five distinct base
            // partitions; two non-members carry no customer attribute at all.
            for (int i = 0; i < 5; i++)
            {
                await PutAsync(client, table, BaseItem($"p{i}", $"s{i}", new()
                {
                    ["customer"] = Str("acme"),
                    ["note"] = Str($"n{i}"),
                }));
            }
            await PutAsync(client, table, BaseItem("pX", "sX", new() { ["note"] = Str("no-customer") }));

            var items = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byCustomer",
                KeyConditionExpression = "customer = :c",
                ExpressionAttributeValues = new() { [":c"] = Str("acme") },
            }, expectedCount: 5);

            Assert.Equal(5, items.Count);
            Assert.All(items, it => Assert.Equal("acme", it["customer"].S));
            // The non-member (no customer attribute) is excluded by the GSI
            // membership guard.
            Assert.DoesNotContain(items, it => it["sk"].S == "sX");
        });
    }

    [SkippableFact]
    public async Task Gsi_composite_query_orders_by_sort_key_across_real_pages()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("gsiord");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            // Seed LargeItemCount members under one GSI hash (category="evt"),
            // each with a distinct zero-padded createdAt so the cross-partition
            // ORDER BY has a total order, spread across PartitionSpread base
            // partitions and large enough to cross the Cosmos page boundary.
            var expected = new List<string>(LargeItemCount);
            for (int i = 0; i < LargeItemCount; i++)
            {
                var created = i.ToString("D5", CultureInfo.InvariantCulture);
                expected.Add(created);
                await PutAsync(client, table, BaseItem($"p{i % PartitionSpread}", $"s{i:D5}", new()
                {
                    ["category"] = Str("evt"),
                    ["createdAt"] = Str(created),
                    ["payload"] = Str(new string('x', PayloadBytes)),
                }));
            }

            // (a) No Limit: the proxy must merge-sort across every Cosmos
            // continuation page and aggregate. Ascending order.
            var asc = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byCategory",
                KeyConditionExpression = "category = :c",
                ExpressionAttributeValues = new() { [":c"] = Str("evt") },
                ScanIndexForward = true,
            }, expectedCount: LargeItemCount);

            var ascCreated = asc.Select(it => it["createdAt"].S).ToList();
            Assert.Equal(expected, ascCreated);

            // (b) Descending order across the same cross-partition merge.
            var desc = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byCategory",
                KeyConditionExpression = "category = :c",
                ExpressionAttributeValues = new() { [":c"] = Str("evt") },
                ScanIndexForward = false,
            }, expectedCount: LargeItemCount);

            var descCreated = desc.Select(it => it["createdAt"].S).ToList();
            var expectedDesc = new List<string>(expected);
            expectedDesc.Reverse();
            Assert.Equal(expectedDesc, descCreated);

            // (c) Limit + LastEvaluatedKey resume must reconstruct the same
            // ordered set exactly once across real continuation pages.
            var paged = new List<string>(LargeItemCount);
            Dictionary<string, AttributeValue>? startKey = null;
            int guard = 0;
            do
            {
                var page = await client.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = "byCategory",
                    KeyConditionExpression = "category = :c",
                    ExpressionAttributeValues = new() { [":c"] = Str("evt") },
                    ScanIndexForward = true,
                    Limit = 17,
                    ExclusiveStartKey = startKey,
                }).ConfigureAwait(false);

                paged.AddRange(page.Items.Select(it => it["createdAt"].S));
                startKey = page.LastEvaluatedKey is { Count: > 0 } lek ? lek : null;
                Assert.True(++guard < 256, "pagination did not terminate.");
            }
            while (startKey is not null);

            Assert.Equal(expected, paged);
        });
    }

    [SkippableFact]
    public async Task Gsi_composite_query_orders_by_numeric_sort_key_across_real_pages()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("gsinum");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            // High-precision numeric GSI sort keys: 21-digit integers (10^20 + i)
            // exceed IEEE-754 double precision, so the storage layer keeps them in
            // the {"_a2a:N":…} envelope. Without the Option-B synthetic order key
            // Cosmos would sort these envelope objects structurally (not
            // numerically) and mis-order the result. This asserts the
            // `_a2a$ord$seq` encoded field restores true numeric order across the
            // real cross-partition merge + continuation pagination.
            var baseValue = System.Numerics.BigInteger.Pow(10, 20);
            var expected = new List<string>(LargeItemCount);
            for (int i = 0; i < LargeItemCount; i++)
            {
                var seq = (baseValue + i).ToString(CultureInfo.InvariantCulture);
                expected.Add(seq);
                await PutAsync(client, table, BaseItem($"p{i % PartitionSpread}", $"s{i:D5}", new()
                {
                    ["category"] = Str("num"),
                    ["seq"] = new AttributeValue { N = seq },
                    ["payload"] = Str(new string('x', PayloadBytes)),
                }));
            }

            // Ascending: merge-sort across every cross-partition continuation page.
            var asc = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byCategoryNum",
                KeyConditionExpression = "category = :c",
                ExpressionAttributeValues = new() { [":c"] = Str("num") },
                ScanIndexForward = true,
            }, expectedCount: LargeItemCount);

            Assert.Equal(expected, asc.Select(it => Canonical(it["seq"].N)).ToList());

            // Descending.
            var desc = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byCategoryNum",
                KeyConditionExpression = "category = :c",
                ExpressionAttributeValues = new() { [":c"] = Str("num") },
                ScanIndexForward = false,
            }, expectedCount: LargeItemCount);

            var expectedDesc = new List<string>(expected);
            expectedDesc.Reverse();
            Assert.Equal(expectedDesc, desc.Select(it => Canonical(it["seq"].N)).ToList());

            // Limit + LastEvaluatedKey resume reconstructs the ordered set exactly
            // once across real continuation pages (encoded-boundary continuation).
            var paged = new List<string>(LargeItemCount);
            Dictionary<string, AttributeValue>? startKey = null;
            int guard = 0;
            do
            {
                var page = await client.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = "byCategoryNum",
                    KeyConditionExpression = "category = :c",
                    ExpressionAttributeValues = new() { [":c"] = Str("num") },
                    ScanIndexForward = true,
                    Limit = 17,
                    ExclusiveStartKey = startKey,
                }).ConfigureAwait(false);

                paged.AddRange(page.Items.Select(it => Canonical(it["seq"].N)));
                startKey = page.LastEvaluatedKey is { Count: > 0 } lek ? lek : null;
                Assert.True(++guard < 256, "pagination did not terminate.");
            }
            while (startKey is not null);

            Assert.Equal(expected, paged);
        });
    }

    // Normalises a returned Number to its canonical big-integer form so the
    // comparison is robust to any DynamoDB/Cosmos numeric formatting.
    private static string Canonical(string n) =>
        System.Numerics.BigInteger.Parse(n, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);

    [SkippableFact]
    public async Task Lsi_query_orders_by_index_sort_key_within_partition()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("lsiq");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            // Items under one base partition with out-of-order LSI sort values.
            var scores = new[] { 30, 10, 20, 50, 40 };
            foreach (var sc in scores)
            {
                await PutAsync(client, table, BaseItem("p1", $"s{sc}", new()
                {
                    ["score"] = Num(sc),
                }));
            }
            // A non-member in the same partition (no score attribute).
            await PutAsync(client, table, BaseItem("p1", "s-nomember", new() { ["note"] = Str("x") }));

            var items = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byScore",
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = new() { [":p"] = Str("p1") },
                ScanIndexForward = true,
            }, expectedCount: scores.Length);

            var ordered = items.Select(it => int.Parse(it["score"].N, CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(new[] { 10, 20, 30, 40, 50 }, ordered);
            Assert.DoesNotContain(items, it => it["sk"].S == "s-nomember");
        });
    }

    [SkippableFact]
    public async Task Lsi_query_orders_by_numeric_sort_key()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("lsinum");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            // High-precision numeric LSI sort keys: 21-digit integers (10^20 + i)
            // exceed IEEE-754 double precision, so the storage layer keeps them in
            // the {"_a2a:N":…} envelope. Without the Option-B synthetic order key
            // Cosmos would sort these envelope objects structurally (not
            // numerically) and mis-order the result. With the opt-in
            // EnableLocalSecondaryIndexNumericOrdering flag (set by the fixture)
            // the query orders by the `_a2a$ord$score` encoded field, restoring
            // true numeric order. LSI is partition-scoped, so all items share one
            // base partition and the values are inserted out of order.
            var baseValue = System.Numerics.BigInteger.Pow(10, 20);
            const int count = 25;
            var order = new[] { 7, 0, 23, 11, 4, 19, 2, 15, 9, 21, 1, 13, 6, 24, 3, 17, 10, 20, 5, 22, 8, 16, 12, 18, 14 };
            var expected = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                expected.Add((baseValue + i).ToString(CultureInfo.InvariantCulture));
            }
            foreach (var i in order)
            {
                await PutAsync(client, table, BaseItem("p1", $"s{i:D5}", new()
                {
                    ["score"] = new AttributeValue { N = (baseValue + i).ToString(CultureInfo.InvariantCulture) },
                }));
            }

            var asc = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byScore",
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = new() { [":p"] = Str("p1") },
                ScanIndexForward = true,
            }, expectedCount: count);

            Assert.Equal(expected, asc.Select(it => Canonical(it["score"].N)).ToList());

            var desc = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byScore",
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = new() { [":p"] = Str("p1") },
                ScanIndexForward = false,
            }, expectedCount: count);

            var expectedDesc = new List<string>(expected);
            expectedDesc.Reverse();
            Assert.Equal(expectedDesc, desc.Select(it => Canonical(it["score"].N)).ToList());

            // A numeric range predicate on the encoded field filters exactly.
            var bound = (baseValue + 15).ToString(CultureInfo.InvariantCulture);
            var ranged = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byScore",
                KeyConditionExpression = "pk = :p AND score >= :b",
                ExpressionAttributeValues = new()
                {
                    [":p"] = Str("p1"),
                    [":b"] = new AttributeValue { N = bound },
                },
                ScanIndexForward = true,
            }, expectedCount: count - 15);

            Assert.Equal(expected.Skip(15).ToList(), ranged.Select(it => Canonical(it["score"].N)).ToList());
        });
    }

    [SkippableFact]
    public async Task Gsi_scan_returns_only_index_members_with_projection()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("gsiscan");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                await PutAsync(client, table, BaseItem($"p{i}", $"s{i}", new()
                {
                    ["customer"] = Str($"c{i}"),
                    ["secret"] = Str("base-only"),
                }));
            }
            // Two non-members lacking the GSI hash attribute.
            await PutAsync(client, table, BaseItem("pn1", "sn1", new() { ["note"] = Str("x") }));
            await PutAsync(client, table, BaseItem("pn2", "sn2", new() { ["note"] = Str("y") }));

            // byCustomerKeysOnly projects KEYS_ONLY: only base keys + the GSI key
            // attribute survive; "secret" must be dropped.
            var items = await ScanUntilAsync(client, new ScanRequest
            {
                TableName = table,
                IndexName = "byCustomerKeysOnly",
            }, expectedCount: 4);

            Assert.Equal(4, items.Count);
            Assert.All(items, it =>
            {
                Assert.True(it.ContainsKey("pk"));
                Assert.True(it.ContainsKey("sk"));
                Assert.True(it.ContainsKey("customer"));
                Assert.False(it.ContainsKey("secret"));
            });
        });
    }

    [SkippableFact]
    public async Task Lsi_scan_returns_only_index_members()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("lsiscan");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            // Members define the LSI sort attribute (score); non-members don't.
            for (int i = 0; i < 3; i++)
            {
                await PutAsync(client, table, BaseItem($"p{i}", $"s{i}", new() { ["score"] = Num(i * 10) }));
            }
            await PutAsync(client, table, BaseItem("pn", "sn", new() { ["note"] = Str("no-score") }));

            var items = await ScanUntilAsync(client, new ScanRequest
            {
                TableName = table,
                IndexName = "byScore",
            }, expectedCount: 3);

            Assert.Equal(3, items.Count);
            Assert.All(items, it => Assert.True(it.ContainsKey("score")));
            Assert.DoesNotContain(items, it => it["sk"].S == "sn");
        });
    }

    // ---- helpers ---------------------------------------------------------

    private static string NewTableName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static AttributeValue Str(string s) => new() { S = s };
    private static AttributeValue Num(int n) => new() { N = n.ToString(CultureInfo.InvariantCulture) };

    private static Dictionary<string, AttributeValue> BaseItem(
        string pk, string sk, Dictionary<string, AttributeValue> extra)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = Str(pk),
            ["sk"] = Str(sk),
        };
        foreach (var kv in extra) item[kv.Key] = kv.Value;
        return item;
    }

    private static Task PutAsync(IAmazonDynamoDB client, string table, Dictionary<string, AttributeValue> item) =>
        client.PutItemAsync(new PutItemRequest { TableName = table, Item = item });

    /// <summary>
    /// Creates the indexed table, waits for it to be ACTIVE, runs the test body,
    /// then deletes the table (best-effort) regardless of outcome.
    /// </summary>
    private async Task WithTableAsync(IAmazonDynamoDB client, string table, Func<Task> body)
    {
        var created = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                    new AttributeDefinition("sk", ScalarAttributeType.S),
                    new AttributeDefinition("score", ScalarAttributeType.N),
                    new AttributeDefinition("customer", ScalarAttributeType.S),
                    new AttributeDefinition("category", ScalarAttributeType.S),
                    new AttributeDefinition("createdAt", ScalarAttributeType.S),
                    new AttributeDefinition("seq", ScalarAttributeType.N),
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
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCustomer",
                        KeySchema = [new KeySchemaElement("customer", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCustomerKeysOnly",
                        KeySchema = [new KeySchemaElement("customer", KeyType.HASH)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCategory",
                        KeySchema =
                        [
                            new KeySchemaElement("category", KeyType.HASH),
                            new KeySchemaElement("createdAt", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "byCategoryNum",
                        KeySchema =
                        [
                            new KeySchemaElement("category", KeyType.HASH),
                            new KeySchemaElement("seq", KeyType.RANGE),
                        ],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);
            created = true;

            await WaitForTableActiveAsync(client, table).ConfigureAwait(false);
            await body().ConfigureAwait(false);
        }
        finally
        {
            if (created)
            {
                try { await client.DeleteTableAsync(new DeleteTableRequest { TableName = table }).ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Runs a Query, aggregating LastEvaluatedKey pages, retrying until the
    /// result set reaches <paramref name="expectedCount"/> or the convergence
    /// deadline elapses — absorbing Cosmos asynchronous-indexing lag.
    /// </summary>
    private async Task<List<Dictionary<string, AttributeValue>>> QueryUntilAsync(
        IAmazonDynamoDB client, QueryRequest request, int expectedCount)
    {
        var deadline = DateTime.UtcNow + ConvergenceTimeout;
        List<Dictionary<string, AttributeValue>> last = new();
        while (true)
        {
            last = await DrainQueryAsync(client, request).ConfigureAwait(false);
            if (last.Count >= expectedCount || DateTime.UtcNow >= deadline)
            {
                _output.WriteLine($"Query {request.IndexName}: converged {last.Count}/{expectedCount}.");
                return last;
            }
            await Task.Delay(750).ConfigureAwait(false);
        }
    }

    private static async Task<List<Dictionary<string, AttributeValue>>> DrainQueryAsync(
        IAmazonDynamoDB client, QueryRequest request)
    {
        var all = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var page = await client.QueryAsync(new QueryRequest
            {
                TableName = request.TableName,
                IndexName = request.IndexName,
                KeyConditionExpression = request.KeyConditionExpression,
                ExpressionAttributeValues = request.ExpressionAttributeValues,
                ScanIndexForward = request.ScanIndexForward,
                ExclusiveStartKey = startKey,
            }).ConfigureAwait(false);
            all.AddRange(page.Items);
            startKey = page.LastEvaluatedKey is { Count: > 0 } lek ? lek : null;
        }
        while (startKey is not null);
        return all;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> ScanUntilAsync(
        IAmazonDynamoDB client, ScanRequest request, int expectedCount)
    {
        var deadline = DateTime.UtcNow + ConvergenceTimeout;
        while (true)
        {
            var all = await DrainScanAsync(client, request).ConfigureAwait(false);
            if (all.Count >= expectedCount || DateTime.UtcNow >= deadline)
            {
                _output.WriteLine($"Scan {request.IndexName}: converged {all.Count}/{expectedCount}.");
                return all;
            }
            await Task.Delay(750).ConfigureAwait(false);
        }
    }

    private static async Task<List<Dictionary<string, AttributeValue>>> DrainScanAsync(
        IAmazonDynamoDB client, ScanRequest request)
    {
        var all = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var page = await client.ScanAsync(new ScanRequest
            {
                TableName = request.TableName,
                IndexName = request.IndexName,
                FilterExpression = request.FilterExpression,
                ExpressionAttributeValues = request.ExpressionAttributeValues,
                ExclusiveStartKey = startKey,
            }).ConfigureAwait(false);
            all.AddRange(page.Items);
            startKey = page.LastEvaluatedKey is { Count: > 0 } lek ? lek : null;
        }
        while (startKey is not null);
        return all;
    }

    private static async Task WaitForTableActiveAsync(IAmazonDynamoDB client, string table)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var desc = await client.DescribeTableAsync(table).ConfigureAwait(false);
                if (desc.Table.TableStatus == TableStatus.ACTIVE) return;
            }
            catch (ResourceNotFoundException)
            {
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }
}
