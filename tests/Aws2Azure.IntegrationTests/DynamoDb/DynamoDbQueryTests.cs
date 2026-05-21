using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Phase-3 Slice 5 smoke (Query): seeds a small composite-key dataset
/// (HASH pk + RANGE sk) and runs Query with KeyConditionExpression covering
/// the equality + BETWEEN flavours, plus a FilterExpression case.
/// </summary>
[Collection(DynamoDbIntegrationCollection.Name)]
public class DynamoDbQueryTests
{
    private readonly DynamoDbIntegrationFixture _fx;
    public DynamoDbQueryTests(DynamoDbIntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Query_with_KeyConditionExpression()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];
        await CreateCompositeTableAsync(table);

        // Seed three items under pk=customer-1 with sk = order-{1,2,3}
        for (int i = 1; i <= 3; i++)
        {
            var putBody = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":   { "S": "customer-1" },
                "sk":   { "S": "order-{{i}}" },
                "amt":  { "N": "{{i * 10}}" }
              }
            }
            """;
            using var req = DynamoDbRequestBuilder.Build("PutItem", putBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
            using var resp = await _fx.Client.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode,
                $"seed PutItem #{i} → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }
        // And one foreign-pk item that must not appear.
        {
            var putBody = $$"""
            {
              "TableName": "{{table}}",
              "Item": { "pk": { "S": "customer-2" }, "sk": { "S": "order-1" }, "amt": { "N": "99" } }
            }
            """;
            using var req = DynamoDbRequestBuilder.Build("PutItem", putBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
            using var resp = await _fx.Client.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode);
        }

        // Equality KeyCondition: pk = :p AND sk BETWEEN :a AND :b → expect order-1 & order-2.
        var queryBody = $$"""
        {
          "TableName": "{{table}}",
          "KeyConditionExpression": "pk = :p AND sk BETWEEN :a AND :b",
          "ExpressionAttributeValues": {
            ":p": { "S": "customer-1" },
            ":a": { "S": "order-1" },
            ":b": { "S": "order-2" }
          },
          "ConsistentRead": true
        }
        """;
        using (var req = DynamoDbRequestBuilder.Build("Query", queryBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"Query → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var items = doc.RootElement.GetProperty("Items");
            Assert.Equal(2, items.GetArrayLength());
            // None of the matches should be from the foreign pk
            Assert.DoesNotContain("customer-2", text);
        }

        // With FilterExpression: amt > 15 → expect only order-2 (amt=20).
        var queryFilterBody = $$"""
        {
          "TableName": "{{table}}",
          "KeyConditionExpression": "pk = :p",
          "FilterExpression": "amt > :min",
          "ExpressionAttributeValues": {
            ":p":   { "S": "customer-1" },
            ":min": { "N": "15" }
          },
          "ConsistentRead": true
        }
        """;
        using (var req = DynamoDbRequestBuilder.Build("Query", queryFilterBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"Query+Filter → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var items = doc.RootElement.GetProperty("Items");
            Assert.Equal(2, items.GetArrayLength()); // amt 20 + amt 30
        }

        await DeleteTableAsync(table);
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
