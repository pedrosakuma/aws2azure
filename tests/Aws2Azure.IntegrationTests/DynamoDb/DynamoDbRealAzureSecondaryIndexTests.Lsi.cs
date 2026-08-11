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

public sealed partial class DynamoDbRealAzureSecondaryIndexTests
{
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
                Limit = 2,
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
                Limit = 2,
            }, expectedCount: 3);

            Assert.Equal(3, items.Count);
            Assert.All(items, it => Assert.True(it.ContainsKey("score")));
            Assert.DoesNotContain(items, it => it["sk"].S == "sn");
        });
    }

    [SkippableFact]
    public async Task DescribeTable_reports_certified_secondary_index_shape()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("idxdesc");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            var response = await client.DescribeTableAsync(table).ConfigureAwait(false);

            Assert.Equal(4, response.Table.GlobalSecondaryIndexes.Count);
            var byCustomer = Assert.Single(
                response.Table.GlobalSecondaryIndexes,
                index => index.IndexName == "byCustomer");
            Assert.Equal(IndexStatus.ACTIVE, byCustomer.IndexStatus);
            Assert.Equal(ProjectionType.ALL, byCustomer.Projection.ProjectionType);
            Assert.Collection(
                byCustomer.KeySchema,
                key =>
                {
                    Assert.Equal("customer", key.AttributeName);
                    Assert.Equal(KeyType.HASH, key.KeyType);
                });

            var byCustomerKeysOnly = Assert.Single(
                response.Table.GlobalSecondaryIndexes,
                index => index.IndexName == "byCustomerKeysOnly");
            Assert.Equal(IndexStatus.ACTIVE, byCustomerKeysOnly.IndexStatus);
            Assert.Equal(ProjectionType.KEYS_ONLY, byCustomerKeysOnly.Projection.ProjectionType);
            Assert.Collection(
                byCustomerKeysOnly.KeySchema,
                key =>
                {
                    Assert.Equal("customer", key.AttributeName);
                    Assert.Equal(KeyType.HASH, key.KeyType);
                });

            var byCategory = Assert.Single(
                response.Table.GlobalSecondaryIndexes,
                index => index.IndexName == "byCategory");
            Assert.Equal(IndexStatus.ACTIVE, byCategory.IndexStatus);
            Assert.Equal(ProjectionType.ALL, byCategory.Projection.ProjectionType);
            Assert.Collection(
                byCategory.KeySchema,
                key =>
                {
                    Assert.Equal("category", key.AttributeName);
                    Assert.Equal(KeyType.HASH, key.KeyType);
                },
                key =>
                {
                    Assert.Equal("createdAt", key.AttributeName);
                    Assert.Equal(KeyType.RANGE, key.KeyType);
                });

            var byCategoryNum = Assert.Single(
                response.Table.GlobalSecondaryIndexes,
                index => index.IndexName == "byCategoryNum");
            Assert.Equal(IndexStatus.ACTIVE, byCategoryNum.IndexStatus);
            Assert.Equal(ProjectionType.ALL, byCategoryNum.Projection.ProjectionType);
            Assert.Collection(
                byCategoryNum.KeySchema,
                key =>
                {
                    Assert.Equal("category", key.AttributeName);
                    Assert.Equal(KeyType.HASH, key.KeyType);
                },
                key =>
                {
                    Assert.Equal("seq", key.AttributeName);
                    Assert.Equal(KeyType.RANGE, key.KeyType);
                });

            foreach (var index in response.Table.GlobalSecondaryIndexes)
            {
                Assert.NotEmpty(index.IndexArn);
            }

            var lsi = Assert.Single(response.Table.LocalSecondaryIndexes);
            Assert.Equal("byScore", lsi.IndexName);
            Assert.NotEmpty(lsi.IndexArn);
            Assert.Equal(ProjectionType.ALL, lsi.Projection.ProjectionType);
            Assert.Collection(
                lsi.KeySchema,
                key =>
                {
                    Assert.Equal("pk", key.AttributeName);
                    Assert.Equal(KeyType.HASH, key.KeyType);
                },
                key =>
                {
                    Assert.Equal("score", key.AttributeName);
                    Assert.Equal(KeyType.RANGE, key.KeyType);
                });
            Assert.True(response.Table.ItemCount is null or 0);
            Assert.True(response.Table.TableSizeBytes is null or 0);
            Assert.True(byCustomer.ItemCount is null or 0);
            Assert.True(byCustomer.IndexSizeBytes is null or 0);
            Assert.True(lsi.ItemCount is null or 0);
            Assert.True(lsi.IndexSizeBytes is null or 0);
        });
    }

    [SkippableFact]
    public async Task Numeric_lsi_backfill_rewrite_restores_legacy_item_membership()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure GSI/LSI validation.");

        var table = NewTableName("lsibf");
        using var client = _fx.CreateDynamoDbClient();
        await WithTableAsync(client, table, async () =>
        {
            const string pk = "p1";
            const string sk = "legacy";
            const string score = "100000000000000000001";
            var encodedPk = Hex(pk);
            var legacyDocument =
                "{\"id\":\"" + Hex(sk)
                + "\",\"_a2a_pk\":\"" + encodedPk
                + "\",\"_a2a\":\"item\",\"pk\":\"" + pk
                + "\",\"sk\":\"" + sk
                + "\",\"score\":{\"_a2a:N\":\"" + score + "\"}}";

            using var http = new HttpClient();
            await CosmosRestBootstrap.CreateDocumentAsync(
                http,
                _fx.CosmosEndpoint,
                _fx.CosmosMasterKey,
                _fx.CosmosDatabase,
                table,
                encodedPk,
                legacyDocument).ConfigureAwait(false);

            var baseRequest = new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = new() { [":p"] = Str(pk) },
            };
            var visibleBaseItems = await QueryUntilAsync(client, baseRequest, expectedCount: 1);
            Assert.Single(visibleBaseItems);

            var request = new QueryRequest
            {
                TableName = table,
                IndexName = "byScore",
                KeyConditionExpression = "pk = :p",
                ExpressionAttributeValues = new() { [":p"] = Str(pk) },
            };
            var before = await client.QueryAsync(request).ConfigureAwait(false);
            Assert.Empty(before.Items);

            await PutAsync(client, table, BaseItem(pk, sk, new()
            {
                ["score"] = new AttributeValue { N = score },
            })).ConfigureAwait(false);

            var after = await QueryUntilAsync(client, request, expectedCount: 1);
            var item = Assert.Single(after);
            Assert.Equal(score, Canonical(item["score"].N));
        });
    }

    // ---- helpers ---------------------------------------------------------

}
