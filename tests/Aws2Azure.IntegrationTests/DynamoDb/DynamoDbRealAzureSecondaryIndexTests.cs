using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
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
public sealed partial class DynamoDbRealAzureSecondaryIndexTests
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
                    ["state"] = Str(i % 2 == 0 ? "active" : "inactive"),
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

            // (d) A selective sort-key range plus residual user filter and
            // projection preserves the ordered subset without leaking the large
            // non-projected payload.
            var selective = await QueryUntilAsync(client, new QueryRequest
            {
                TableName = table,
                IndexName = "byCategory",
                KeyConditionExpression = "category = :c AND createdAt BETWEEN :lo AND :hi",
                FilterExpression = "#state <> :inactive",
                ProjectionExpression = "pk, sk, category, createdAt, #state",
                ExpressionAttributeNames = new() { ["#state"] = "state" },
                ExpressionAttributeValues = new()
                {
                    [":c"] = Str("evt"),
                    [":lo"] = Str("00020"),
                    [":hi"] = Str("00039"),
                    [":inactive"] = Str("inactive"),
                },
            }, expectedCount: 10);

            Assert.Equal(
                expected.Skip(20).Take(20).Where(value =>
                    int.Parse(value, CultureInfo.InvariantCulture) % 2 == 0),
                selective.Select(item => item["createdAt"].S));
            Assert.All(selective, item =>
            {
                Assert.Equal("active", item["state"].S);
                Assert.False(item.ContainsKey("payload"));
            });
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

}
