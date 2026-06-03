using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Validates DynamoDB Query/Scan pagination (LastEvaluatedKey ↔ Cosmos
/// x-ms-continuation) against the real Cosmos page machinery.
///
/// The Cosmos vnext-preview emulator runs a single physical partition and
/// returns small result sets in one page, so the continuation / multi-page
/// path is never exercised there. Real Azure Cosmos DB paginates a query
/// page once it reaches the response-size limit (~4 MB), regardless of
/// x-ms-max-item-count. These tests pile enough large items into a single
/// partition to cross that boundary, forcing real multi-page reads, and
/// assert that every item is returned exactly once — both when the proxy
/// aggregates pages internally (no client Limit) and when it surfaces a
/// LastEvaluatedKey the client must resume from (with a Limit).
///
/// They skip on the emulator-backed CI run (which cannot reproduce this) and
/// execute only against real Azure (AWS2AZURE_REAL_COSMOS_ENDPOINT / _KEY). See
/// issue #205.
/// </summary>
[Collection(DynamoDbSprocCollection.Name)]
public class DynamoDbRealCosmosPaginationTests
{
    private readonly DynamoDbSprocFixture _fx;
    private readonly ITestOutputHelper _output;

    // ~50 KB of payload per item; 150 items ≈ 7.5 MB in one logical partition,
    // which comfortably exceeds the Cosmos query response page limit (~4 MB) and
    // forces the no-Limit query to span multiple real continuation pages.
    private const int ItemCount = 150;
    private const int PayloadBytes = 50 * 1024;

    public DynamoDbRealCosmosPaginationTests(DynamoDbSprocFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    [SkippableFact]
    public async Task Query_aggregates_all_items_across_real_continuation_pages()
    {
        Skip.IfNot(_fx.IsRealCosmos,
            "Requires real Azure Cosmos DB (AWS2AZURE_REAL_COSMOS_ENDPOINT / _KEY): the emulator runs one partition, ignores x-ms-max-item-count, and cannot reproduce 2 MB / continuation pagination.");

        var table = "qpage" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);
        try
        {
            var expected = await SeedPartitionAsync(table, "p");

            // No Limit → the proxy must walk every Cosmos continuation page and
            // aggregate. On real Cosmos this is multiple 2 MB pages.
            var (count, keys, lek) = await QueryPartitionAsync(table, "p", limit: null, startKey: null);

            Assert.Null(lek);
            Assert.Equal(ItemCount, count);
            AssertExactlyOnce(expected, keys);
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task Query_with_limit_round_trips_LastEvaluatedKey_over_real_pages()
    {
        Skip.IfNot(_fx.IsRealCosmos,
            "Requires real Azure Cosmos DB (AWS2AZURE_REAL_COSMOS_ENDPOINT / _KEY): the emulator runs one partition, ignores x-ms-max-item-count, and cannot reproduce 2 MB / continuation pagination.");

        var table = "qlek" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);
        try
        {
            var expected = await SeedPartitionAsync(table, "p");

            var seen = new List<string>();
            JsonElement? startKey = null;
            int pages = 0;
            do
            {
                var (_, keys, lek) = await QueryPartitionAsync(table, "p", limit: 10, startKey: startKey);
                seen.AddRange(keys);
                startKey = lek;
                pages++;
                Assert.True(pages <= ItemCount + 5, "pagination did not terminate");
            }
            while (startKey is not null);

            _output.WriteLine($"Query+Limit paged in {pages} request(s) over {seen.Count} item(s).");
            Assert.True(pages > 1, "expected more than one client page under a Limit");
            AssertExactlyOnce(expected, seen);
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task Scan_round_trips_LastEvaluatedKey_over_real_pages()
    {
        Skip.IfNot(_fx.IsRealCosmos,
            "Requires real Azure Cosmos DB (AWS2AZURE_REAL_COSMOS_ENDPOINT / _KEY): the emulator runs one partition, ignores x-ms-max-item-count, and cannot reproduce 2 MB / continuation pagination.");

        var table = "spage" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);
        try
        {
            var expected = await SeedPartitionAsync(table, "p");

            var seen = new List<string>();
            JsonElement? startKey = null;
            int pages = 0;
            do
            {
                var (keys, lek) = await ScanAsync(table, limit: 10, startKey: startKey);
                seen.AddRange(keys);
                startKey = lek;
                pages++;
                Assert.True(pages <= ItemCount + 5, "scan pagination did not terminate");
            }
            while (startKey is not null);

            _output.WriteLine($"Scan paged in {pages} request(s) over {seen.Count} item(s).");
            AssertExactlyOnce(expected, seen);
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    // ---- helpers ---------------------------------------------------------

    private async Task<HashSet<string>> SeedPartitionAsync(string table, string pk)
    {
        var payload = new string('x', PayloadBytes);
        var expected = new HashSet<string>();
        for (int i = 0; i < ItemCount; i++)
        {
            var sk = $"s{i:D4}";
            expected.Add(sk);
            var body = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":   { "S": "{{pk}}" },
                "sk":   { "S": "{{sk}}" },
                "blob": { "S": "{{payload}}" }
              }
            }
            """;
            var (status, respBody) = await ExecuteAsync("PutItem", body);
            Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created,
                $"seed PutItem {sk} → {(int)status} {respBody}");
        }
        return expected;
    }

    private async Task<(int count, List<string> keys, JsonElement? lek)> QueryPartitionAsync(
        string table, string pk, int? limit, JsonElement? startKey)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"TableName\":\"{table}\",");
        sb.Append("\"KeyConditionExpression\":\"pk = :p\",");
        sb.Append($"\"ExpressionAttributeValues\":{{\":p\":{{\"S\":\"{pk}\"}}}},");
        sb.Append("\"ConsistentRead\":true");
        if (limit is not null) sb.Append($",\"Limit\":{limit.Value}");
        if (startKey is not null) sb.Append($",\"ExclusiveStartKey\":{startKey.Value.GetRawText()}");
        sb.Append('}');

        var (status, body) = await ExecuteAsync("Query", sb.ToString());
        Assert.Equal(HttpStatusCode.OK, status);
        return ParsePage(body);
    }

    private async Task<(List<string> keys, JsonElement? lek)> ScanAsync(
        string table, int? limit, JsonElement? startKey)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"TableName\":\"{table}\"");
        if (limit is not null) sb.Append($",\"Limit\":{limit.Value}");
        if (startKey is not null) sb.Append($",\"ExclusiveStartKey\":{startKey.Value.GetRawText()}");
        sb.Append('}');

        var (status, body) = await ExecuteAsync("Scan", sb.ToString());
        Assert.Equal(HttpStatusCode.OK, status);
        var (_, keys, lek) = ParsePage(body);
        return (keys, lek);
    }

    private static (int count, List<string> keys, JsonElement? lek) ParsePage(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var keys = new List<string>();
        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                keys.Add(it.GetProperty("sk").GetProperty("S").GetString()!);
            }
        }
        int count = root.TryGetProperty("Count", out var c) ? c.GetInt32() : keys.Count;
        JsonElement? lek = null;
        if (root.TryGetProperty("LastEvaluatedKey", out var k) && k.ValueKind == JsonValueKind.Object)
        {
            lek = k.Clone();
        }
        return (count, keys, lek);
    }

    private void AssertExactlyOnce(HashSet<string> expected, List<string> seen)
    {
        var seenSet = new HashSet<string>();
        foreach (var s in seen)
        {
            Assert.True(seenSet.Add(s), $"item '{s}' returned more than once across pages");
        }
        Assert.True(expected.SetEquals(seenSet),
            $"expected {expected.Count} unique items, saw {seenSet.Count}");
    }

    private async Task<(HttpStatusCode status, string body)> ExecuteAsync(string operation, string body)
    {
        using var req = DynamoDbRequestBuilder.Build(
            operation, body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private async Task CreateHashRangeTableAsync(string table)
    {
        var body = $$"""
        {
          "TableName": "{{table}}",
          "AttributeDefinitions": [
            { "AttributeName": "pk", "AttributeType": "S" },
            { "AttributeName": "sk", "AttributeType": "S" }
          ],
          "KeySchema": [
            { "AttributeName": "pk", "KeyType": "HASH" },
            { "AttributeName": "sk", "KeyType": "RANGE" }
          ],
          "BillingMode": "PAY_PER_REQUEST"
        }
        """;
        var (status, respBody) = await ExecuteAsync("CreateTable", body);
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created,
            $"CreateTable → {(int)status} {respBody}");
    }

    private async Task DeleteTableAsync(string table)
    {
        try { await ExecuteAsync("DeleteTable", $"{{\"TableName\":\"{table}\"}}"); }
        catch { /* best-effort cleanup */ }
    }
}
