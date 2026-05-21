using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Phase-3 Slice 1 (table lifecycle) smoke against the Cosmos DB Linux
/// emulator: CreateTable → DescribeTable → ListTables → DeleteTable.
/// </summary>
[Collection(DynamoDbIntegrationCollection.Name)]
public class DynamoDbTableLifecycleTests
{
    private readonly DynamoDbIntegrationFixture _fx;
    public DynamoDbTableLifecycleTests(DynamoDbIntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Create_Describe_List_Delete_Roundtrip()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB integration test.");

        var table = "it" + Guid.NewGuid().ToString("N")[..12];

        var createBody = $$"""
        {
          "TableName": "{{table}}",
          "AttributeDefinitions": [ { "AttributeName": "pk", "AttributeType": "S" } ],
          "KeySchema": [ { "AttributeName": "pk", "KeyType": "HASH" } ],
          "BillingMode": "PAY_PER_REQUEST"
        }
        """;

        using (var req = DynamoDbRequestBuilder.Build("CreateTable", createBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"CreateTable → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var desc = doc.RootElement.GetProperty("TableDescription");
            Assert.Equal(table, desc.GetProperty("TableName").GetString());
        }

        var describeBody = $"{{\"TableName\":\"{table}\"}}";
        using (var req = DynamoDbRequestBuilder.Build("DescribeTable", describeBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"DescribeTable → {(int)resp.StatusCode} {text}");
            using var doc = JsonDocument.Parse(text);
            var status = doc.RootElement.GetProperty("Table").GetProperty("TableStatus").GetString();
            Assert.Equal("ACTIVE", status);
        }

        using (var req = DynamoDbRequestBuilder.Build("ListTables", "{}", _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"ListTables → {(int)resp.StatusCode} {text}");
            Assert.Contains(table, text);
        }

        using (var req = DynamoDbRequestBuilder.Build("DeleteTable", describeBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"DeleteTable → {(int)resp.StatusCode} {text}");
        }

        // After delete, DescribeTable should surface ResourceNotFoundException.
        using (var req = DynamoDbRequestBuilder.Build("DescribeTable", describeBody, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!))
        using (var resp = await _fx.Client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("ResourceNotFoundException", text);
        }
    }
}
