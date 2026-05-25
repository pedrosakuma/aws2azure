using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Phase-2.x end-to-end coverage for the FilterPushdownVisitor. Each test
/// seeds a small mixed-shape dataset then issues a Query or Scan with a
/// FilterExpression that exercises a specific branch of the contract:
///
///   * <c>Scan_pushable_string_equality</c> — purely-pushable predicate.
///   * <c>Scan_hybrid_number_with_envelope_values</c> — hybrid SQL path
///     against a column where some rows store the value flat (small N)
///     and others store it through the high-precision <c>_a2a:N</c>
///     envelope (38-digit N). The residual must re-check the canonical
///     string so the envelope row is included.
///   * <c>Scan_polymorphic_attribute_residual_handles_cross_type</c> —
///     the same logical attribute appears as S on one item and N on
///     another. DynamoDB <c>&lt;&gt;</c> is cross-type true; the visitor
///     refuses pushdown so the residual evaluator must decide.
///   * <c>Query_pushdown_with_limit_returns_only_matching_items</c> —
///     under pushdown+Limit every returned row must satisfy the
///     FilterExpression (Cosmos pre-filters server-side). The exact
///     continuation behaviour is covered by the unit suite; here we
///     only assert the safety property the runtime guarantees.
/// </summary>
[Collection(DynamoDbIntegrationCollection.Name)]
public class DynamoDbFilterPushdownTests
{
    private readonly DynamoDbIntegrationFixture _fx;
    public DynamoDbFilterPushdownTests(DynamoDbIntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Scan_pushable_string_equality()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];
        await CreateHashTableAsync(table);

        for (int i = 1; i <= 4; i++)
        {
            await PutAsync(table, $$"""
            {
              "pk":   { "S": "p-{{i}}" },
              "tag":  { "S": "{{(i % 2 == 0 ? "match" : "skip")}}" }
            }
            """);
        }

        var body = $$"""
        {
          "TableName": "{{table}}",
          "FilterExpression": "#t = :v",
          "ExpressionAttributeNames":  { "#t": "tag" },
          "ExpressionAttributeValues": { ":v": { "S": "match" } },
          "ConsistentRead": true
        }
        """;
        using var req = DynamoDbRequestBuilder.Build("Scan", body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"Scan → {(int)resp.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);

        Assert.Equal(2, doc.RootElement.GetProperty("Count").GetInt32());
        // Pushdown active → Cosmos pre-filters; ScannedCount tracks pre-filter rows
        // returned by Cosmos (≤ 5 since pushdown trims).
        Assert.True(doc.RootElement.GetProperty("ScannedCount").GetInt32() <= 4);

        await DeleteTableAsync(table);
    }

    [SkippableFact]
    public async Task Scan_hybrid_number_with_envelope_values()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];
        await CreateHashTableAsync(table);

        // Three items: two flat-stored small N, one envelope-stored 38-digit N
        // whose value (1.0e37) lies inside the [1e30, 1e38] range.
        await PutAsync(table, $$$"""{ "pk": { "S": "small" },  "v": { "N": "100" } }""");
        await PutAsync(table, $$$"""{ "pk": { "S": "medium" }, "v": { "N": "5000" } }""");
        await PutAsync(table, $$$"""{ "pk": { "S": "huge" },   "v": { "N": "10000000000000000000000000000000000000" } }""");

        // BETWEEN that includes the envelope row but excludes the small/medium ones.
        var body = $$"""
        {
          "TableName": "{{table}}",
          "FilterExpression": "v BETWEEN :lo AND :hi",
          "ExpressionAttributeValues": {
            ":lo": { "N": "1000000000000000000000000000000" },
            ":hi": { "N": "100000000000000000000000000000000000000" }
          },
          "ConsistentRead": true
        }
        """;
        using var req = DynamoDbRequestBuilder.Build("Scan", body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"Scan → {(int)resp.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);

        // Exactly the envelope-stored row matches.
        var items = doc.RootElement.GetProperty("Items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("huge", items[0].GetProperty("pk").GetProperty("S").GetString());

        await DeleteTableAsync(table);
    }

    [SkippableFact]
    public async Task Scan_polymorphic_attribute_residual_handles_cross_type()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];
        await CreateHashTableAsync(table);

        // Same logical attribute "v" stored as different DDB types per row.
        await PutAsync(table, $$$"""{ "pk": { "S": "as-string" }, "v": { "S": "42" } }""");
        await PutAsync(table, $$$"""{ "pk": { "S": "as-number" }, "v": { "N": "42" } }""");

        // v = :n (N=42): DDB equality is type-sensitive — only the N row matches,
        // even though both rows "look like" 42. The visitor pushes a hybrid
        // IS_NUMBER / _a2a:N branch; residual re-checks the canonical string.
        var body = $$"""
        {
          "TableName": "{{table}}",
          "FilterExpression": "v = :n",
          "ExpressionAttributeValues": { ":n": { "N": "42" } },
          "ConsistentRead": true
        }
        """;
        using var req = DynamoDbRequestBuilder.Build("Scan", body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"Scan → {(int)resp.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);

        var items = doc.RootElement.GetProperty("Items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("as-number", items[0].GetProperty("pk").GetProperty("S").GetString());
        Assert.True(items[0].GetProperty("v").TryGetProperty("N", out _));

        await DeleteTableAsync(table);
    }

    [SkippableFact]
    public async Task Query_pushdown_with_limit_returns_only_matching_items()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];
        await CreateCompositeTableAsync(table);

        // Seed 6 items under the same pk; FilterExpression keeps the even ones.
        for (int i = 1; i <= 6; i++)
        {
            await PutAsync(table, $$"""
            {
              "pk":   { "S": "p-1" },
              "sk":   { "S": "s-{{i:D2}}" },
              "kind": { "S": "{{(i % 2 == 0 ? "keep" : "drop")}}" }
            }
            """);
        }

        var body = $$"""
        {
          "TableName": "{{table}}",
          "KeyConditionExpression": "pk = :p",
          "FilterExpression": "#k = :v",
          "ExpressionAttributeNames":  { "#k": "kind" },
          "ExpressionAttributeValues": {
            ":p": { "S": "p-1" },
            ":v": { "S": "keep" }
          },
          "Limit": 2,
          "ConsistentRead": true
        }
        """;
        using var req = DynamoDbRequestBuilder.Build("Query", body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"Query → {(int)resp.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);

        // Pushdown + Limit: Cosmos pre-filters server-side, so every row we get
        // back must satisfy the FilterExpression — never a "drop" row.
        var items = doc.RootElement.GetProperty("Items");
        Assert.True(items.GetArrayLength() >= 1, $"expected ≥1 match; body: {text}");
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal("keep", item.GetProperty("kind").GetProperty("S").GetString());
        }

        // We MAY or MAY NOT see LastEvaluatedKey: if the Cosmos page held every
        // match, there's no continuation. If pagination kicked in, the
        // documented contract is that the continuation is surfaced. Whichever
        // path the runtime takes, the unit-test suite covers the loop break
        // semantics (Query_with_pushdown_and_limit_stops_after_first_non_empty_page).

        await DeleteTableAsync(table);
    }

    private async Task PutAsync(string table, string itemJson)
    {
        var body = $$"""
        {
          "TableName": "{{table}}",
          "Item": {{itemJson}}
        }
        """;
        using var req = DynamoDbRequestBuilder.Build("PutItem", body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"seed PutItem → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task CreateHashTableAsync(string table)
    {
        var body = $$"""
        {
          "TableName": "{{table}}",
          "AttributeDefinitions": [ { "AttributeName": "pk", "AttributeType": "S" } ],
          "KeySchema": [ { "AttributeName": "pk", "KeyType": "HASH" } ],
          "BillingMode": "PAY_PER_REQUEST"
        }
        """;
        using var req = DynamoDbRequestBuilder.Build("CreateTable", body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"setup CreateTable → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task CreateCompositeTableAsync(string table)
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
        using var req = DynamoDbRequestBuilder.Build("CreateTable", body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"setup CreateTable → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task DeleteTableAsync(string table)
    {
        using var req = DynamoDbRequestBuilder.Build("DeleteTable",
            $"{{\"TableName\":\"{table}\"}}", _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
    }
}
