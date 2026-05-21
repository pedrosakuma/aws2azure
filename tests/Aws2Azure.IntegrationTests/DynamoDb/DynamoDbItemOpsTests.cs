using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Phase-3 Slice 2 + Slice 3 smoke: PutItem, GetItem (with strong consistency),
/// UpdateItem (SET + REMOVE via UpdateExpression), DeleteItem, plus a
/// missing-key GetItem to confirm 200 + no Item.
/// </summary>
[Collection(DynamoDbIntegrationCollection.Name)]
public class DynamoDbItemOpsTests
{
    private readonly DynamoDbIntegrationFixture _fx;
    public DynamoDbItemOpsTests(DynamoDbIntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Put_Get_Update_Delete_Roundtrip()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];
        await CreateHashTableAsync(table);

        // PutItem
        var putBody = $$"""
        {
          "TableName": "{{table}}",
          "Item": {
            "pk":   { "S": "order-1" },
            "name": { "S": "widget" },
            "qty":  { "N": "3" }
          }
        }
        """;
        using (var req = DynamoDbRequestBuilder.Build("PutItem", putBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"PutItem → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }

        // GetItem (strong consistency)
        var getBody = $$"""
        {
          "TableName": "{{table}}",
          "Key": { "pk": { "S": "order-1" } },
          "ConsistentRead": true
        }
        """;
        using (var req = DynamoDbRequestBuilder.Build("GetItem", getBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"GetItem → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var item = doc.RootElement.GetProperty("Item");
            Assert.Equal("widget", item.GetProperty("name").GetProperty("S").GetString());
            Assert.Equal("3", item.GetProperty("qty").GetProperty("N").GetString());
        }

        // UpdateItem: SET qty = qty + :inc, REMOVE name
        var updBody = $$"""
        {
          "TableName": "{{table}}",
          "Key": { "pk": { "S": "order-1" } },
          "UpdateExpression": "SET qty = qty + :inc REMOVE #n",
          "ExpressionAttributeNames":  { "#n": "name" },
          "ExpressionAttributeValues": { ":inc": { "N": "2" } },
          "ReturnValues": "ALL_NEW"
        }
        """;
        using (var req = DynamoDbRequestBuilder.Build("UpdateItem", updBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"UpdateItem → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var attrs = doc.RootElement.GetProperty("Attributes");
            Assert.Equal("5", attrs.GetProperty("qty").GetProperty("N").GetString());
            Assert.False(attrs.TryGetProperty("name", out _));
        }

        // DeleteItem
        var delBody = $$"""
        {
          "TableName": "{{table}}",
          "Key": { "pk": { "S": "order-1" } }
        }
        """;
        using (var req = DynamoDbRequestBuilder.Build("DeleteItem", delBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"DeleteItem → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }

        // GetItem after delete: 200 with no Item
        using (var req = DynamoDbRequestBuilder.Build("GetItem", getBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            Assert.False(doc.RootElement.TryGetProperty("Item", out _),
                "Expected no Item after DeleteItem; got: " + text);
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
        // best-effort cleanup; don't fail the test if teardown errors out
    }
}
