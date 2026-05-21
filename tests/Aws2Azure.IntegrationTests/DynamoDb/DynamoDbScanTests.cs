using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Phase-3 Slice 6 smoke (Scan): seeds multiple partitions then runs
/// table-wide Scan, both unfiltered and with a FilterExpression.
/// </summary>
[Collection(DynamoDbIntegrationCollection.Name)]
public class DynamoDbScanTests
{
    private readonly DynamoDbIntegrationFixture _fx;
    public DynamoDbScanTests(DynamoDbIntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Scan_with_and_without_filter()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];
        await CreateHashTableAsync(table);

        for (int i = 1; i <= 5; i++)
        {
            var putBody = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":   { "S": "p-{{i}}" },
                "tier": { "S": "{{(i % 2 == 0 ? "gold" : "silver")}}" },
                "n":    { "N": "{{i}}" }
              }
            }
            """;
            using var req = DynamoDbRequestBuilder.Build("PutItem", putBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
            using var resp = await _fx.Client.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode);
        }

        // Unfiltered: expect all 5
        using (var req = DynamoDbRequestBuilder.Build("Scan",
            $"{{\"TableName\":\"{table}\",\"ConsistentRead\":true}}",
            _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"Scan → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var items = doc.RootElement.GetProperty("Items");
            Assert.Equal(5, items.GetArrayLength());
            Assert.Equal(5, doc.RootElement.GetProperty("Count").GetInt32());
        }

        // Filtered: tier = "gold" → expect i=2, i=4
        var filterBody = $$"""
        {
          "TableName": "{{table}}",
          "FilterExpression": "#t = :tier",
          "ExpressionAttributeNames":  { "#t": "tier" },
          "ExpressionAttributeValues": { ":tier": { "S": "gold" } },
          "ConsistentRead": true
        }
        """;
        using (var req = DynamoDbRequestBuilder.Build("Scan", filterBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"Scan+Filter → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var items = doc.RootElement.GetProperty("Items");
            Assert.Equal(2, items.GetArrayLength());
            // ScannedCount > Count when a filter is applied
            Assert.Equal(2, doc.RootElement.GetProperty("Count").GetInt32());
            Assert.Equal(5, doc.RootElement.GetProperty("ScannedCount").GetInt32());
        }

        await DeleteTableAsync(table);
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

    private async Task DeleteTableAsync(string table)
    {
        using var req = DynamoDbRequestBuilder.Build("DeleteTable",
            $"{{\"TableName\":\"{table}\"}}", _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
    }
}
