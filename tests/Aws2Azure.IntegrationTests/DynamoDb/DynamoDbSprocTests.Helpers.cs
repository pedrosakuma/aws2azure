using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public partial class DynamoDbSprocTests
{
    private async Task<bool> ItemExistsAsync(string table, string pk, string sk)
        => (await GetItemAsync(table, pk, sk)) is not null;

    private async Task<JsonElement?> GetItemAsync(string table, string pk, string sk)
    {
        var getBody = $$"""
        {
          "TableName": "{{table}}",
          "Key": { "pk": { "S": "{{pk}}" }, "sk": { "S": "{{sk}}" } },
          "ConsistentRead": true
        }
        """;
        var (_, respBody, _) = await ExecuteWithTimingAsync("GetItem", getBody);
        using var doc = JsonDocument.Parse(respBody);
        if (!doc.RootElement.TryGetProperty("Item", out var item))
        {
            return null;
        }
        return item.Clone();
    }

    private async Task<(HttpStatusCode status, string body, TimeSpan elapsed)> ExecuteWithTimingAsync(
        string operation, string body)
    {
        var sw = Stopwatch.StartNew();
        using var req = DynamoDbRequestBuilder.Build(operation, body, _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        sw.Stop();
        var respBody = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, respBody, sw.Elapsed);
    }

    private async Task ExecuteAndAssertAsync(string operation, string body, string context)
    {
        var (status, respBody, _) = await ExecuteWithTimingAsync(operation, body);
        Assert.True(status == HttpStatusCode.OK || status == HttpStatusCode.Created,
            $"{context}: {operation} → {(int)status} {respBody}");
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
        await ExecuteAndAssertAsync("CreateTable", body, "setup CreateTable");
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
        await ExecuteAndAssertAsync("CreateTable", body, "setup CreateTable (pk+sk)");
    }

    private async Task DeleteTableAsync(string table)
    {
        using var req = DynamoDbRequestBuilder.Build("DeleteTable",
            $"{{\"TableName\":\"{table}\"}}", _fx.AccessKeyId, _fx.Secret, _fx.Client.BaseAddress!);
        using var resp = await _fx.Client.SendAsync(req);
        // best-effort cleanup
    }

    private static double Average(double[] values)
    {
        double sum = 0;
        foreach (var v in values) sum += v;
        return sum / values.Length;
    }

    private static double Percentile(double[] values, int percentile)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Max(0, index)];
    }
}
