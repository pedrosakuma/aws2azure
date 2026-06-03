using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Tests for DynamoDB stored procedure path (atomic conditional writes).
/// Uses a separate fixture with sprocs enabled (Preferred mode).
/// Compares latency of sproc path vs optimistic GET→PUT path.
/// </summary>
[Collection(DynamoDbSprocCollection.Name)]
public class DynamoDbSprocTests
{
    private readonly DynamoDbSprocFixture _fx;
    private readonly ITestOutputHelper _output;

    // The Cosmos linux emulator (vnext-preview) rejects server-side scripts with
    // "Server-side scripts are not supported in this emulator", so the proxy
    // cannot provision the atomicTransactWrite sproc. TransactWriteItems has no
    // non-atomic fallback, so its server-side JS can only be validated against
    // real Azure Cosmos DB. These tests skip cleanly when provisioning fails.
    private const string SprocUnsupportedReason =
        "Cosmos emulator lacks server-side script support; validate TransactWriteItems against real Azure Cosmos DB.";

    public DynamoDbSprocTests(DynamoDbSprocFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    [SkippableFact]
    public async Task ConditionalPutItem_SprocPath()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "sproc" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashTableAsync(table);

        try
        {
            // First PutItem - no condition (baseline)
            var putBody = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":      { "S": "item-1" },
                "version": { "N": "1" },
                "data":    { "S": "initial" }
              }
            }
            """;
            await ExecuteAndAssertAsync("PutItem", putBody, "baseline PutItem");

            // Conditional PutItem with attribute_not_exists - should fail (item exists)
            var condPut1 = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":      { "S": "item-1" },
                "version": { "N": "2" },
                "data":    { "S": "should-fail" }
              },
              "ConditionExpression": "attribute_not_exists(pk)"
            }
            """;
            var (status1, body1, elapsed1) = await ExecuteWithTimingAsync("PutItem", condPut1);
            _output.WriteLine($"ConditionalPutItem (expect fail): {status1} in {elapsed1.TotalMilliseconds:F1}ms");
            Assert.Equal(HttpStatusCode.BadRequest, status1);
            Assert.Contains("ConditionalCheckFailed", body1);

            // Conditional PutItem with version check - should succeed
            var condPut2 = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":      { "S": "item-1" },
                "version": { "N": "2" },
                "data":    { "S": "updated-via-sproc" }
              },
              "ConditionExpression": "version = :v",
              "ExpressionAttributeValues": { ":v": { "N": "1" } }
            }
            """;
            var (status2, _, elapsed2) = await ExecuteWithTimingAsync("PutItem", condPut2);
            _output.WriteLine($"ConditionalPutItem (expect success): {status2} in {elapsed2.TotalMilliseconds:F1}ms");
            Assert.Equal(HttpStatusCode.OK, status2);

            // Verify the update
            var getBody = $$"""
            {
              "TableName": "{{table}}",
              "Key": { "pk": { "S": "item-1" } },
              "ConsistentRead": true
            }
            """;
            var (_, getResp, _) = await ExecuteWithTimingAsync("GetItem", getBody);
            using var doc = JsonDocument.Parse(getResp);
            var item = doc.RootElement.GetProperty("Item");
            Assert.Equal("2", item.GetProperty("version").GetProperty("N").GetString());
            Assert.Equal("updated-via-sproc", item.GetProperty("data").GetProperty("S").GetString());
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task ConditionalUpdateItem_SprocPath()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "sproc" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashTableAsync(table);

        try
        {
            // Setup: create item
            var putBody = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":      { "S": "item-1" },
                "counter": { "N": "0" },
                "status":  { "S": "pending" }
              }
            }
            """;
            await ExecuteAndAssertAsync("PutItem", putBody, "setup PutItem");

            // Conditional UpdateItem - should succeed (ReturnValues=NONE uses sproc path)
            var updBody = $$"""
            {
              "TableName": "{{table}}",
              "Key": { "pk": { "S": "item-1" } },
              "UpdateExpression": "SET counter = counter + :inc, #s = :new",
              "ConditionExpression": "#s = :old",
              "ExpressionAttributeNames": { "#s": "status" },
              "ExpressionAttributeValues": {
                ":inc": { "N": "1" },
                ":old": { "S": "pending" },
                ":new": { "S": "processing" }
              }
            }
            """;
            var (status, _, elapsed) = await ExecuteWithTimingAsync("UpdateItem", updBody);
            _output.WriteLine($"ConditionalUpdateItem (NONE): {status} in {elapsed.TotalMilliseconds:F1}ms");
            Assert.Equal(HttpStatusCode.OK, status);

            // Verify
            var getBody = $$"""
            {
              "TableName": "{{table}}",
              "Key": { "pk": { "S": "item-1" } },
              "ConsistentRead": true
            }
            """;
            var (_, getResp, _) = await ExecuteWithTimingAsync("GetItem", getBody);
            using var doc = JsonDocument.Parse(getResp);
            var item = doc.RootElement.GetProperty("Item");
            Assert.Equal("1", item.GetProperty("counter").GetProperty("N").GetString());
            Assert.Equal("processing", item.GetProperty("status").GetProperty("S").GetString());
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task ConditionalDeleteItem_SprocPath()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "sproc" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashTableAsync(table);

        try
        {
            // Setup
            var putBody = $$"""
            {
              "TableName": "{{table}}",
              "Item": {
                "pk":     { "S": "item-1" },
                "status": { "S": "deletable" }
              }
            }
            """;
            await ExecuteAndAssertAsync("PutItem", putBody, "setup PutItem");

            // Conditional DeleteItem - should fail (wrong status)
            var delBody1 = $$"""
            {
              "TableName": "{{table}}",
              "Key": { "pk": { "S": "item-1" } },
              "ConditionExpression": "#s = :v",
              "ExpressionAttributeNames": { "#s": "status" },
              "ExpressionAttributeValues": { ":v": { "S": "wrong-status" } }
            }
            """;
            var (status1, body1, elapsed1) = await ExecuteWithTimingAsync("DeleteItem", delBody1);
            _output.WriteLine($"ConditionalDeleteItem (expect fail): {status1} in {elapsed1.TotalMilliseconds:F1}ms");
            Assert.Equal(HttpStatusCode.BadRequest, status1);
            Assert.Contains("ConditionalCheckFailed", body1);

            // Conditional DeleteItem - should succeed
            var delBody2 = $$"""
            {
              "TableName": "{{table}}",
              "Key": { "pk": { "S": "item-1" } },
              "ConditionExpression": "#s = :v",
              "ExpressionAttributeNames": { "#s": "status" },
              "ExpressionAttributeValues": { ":v": { "S": "deletable" } }
            }
            """;
            var (status2, _, elapsed2) = await ExecuteWithTimingAsync("DeleteItem", delBody2);
            _output.WriteLine($"ConditionalDeleteItem (expect success): {status2} in {elapsed2.TotalMilliseconds:F1}ms");
            Assert.Equal(HttpStatusCode.OK, status2);

            // Verify deleted
            var getBody = $$"""
            {
              "TableName": "{{table}}",
              "Key": { "pk": { "S": "item-1" } },
              "ConsistentRead": true
            }
            """;
            var (_, getResp, _) = await ExecuteWithTimingAsync("GetItem", getBody);
            using var doc = JsonDocument.Parse(getResp);
            Assert.False(doc.RootElement.TryGetProperty("Item", out _));
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task LatencyComparison_SprocVsOptimistic()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "perf" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashTableAsync(table);

        const int iterations = 10;
        var sprocTimes = new double[iterations];
        var optimisticTimes = new double[iterations];

        try
        {
            // Warm up sproc creation
            var warmupPut = $$"""
            {
              "TableName": "{{table}}",
              "Item": { "pk": { "S": "warmup" }, "v": { "N": "1" } },
              "ConditionExpression": "attribute_not_exists(pk)"
            }
            """;
            await ExecuteWithTimingAsync("PutItem", warmupPut);

            for (int i = 0; i < iterations; i++)
            {
                var pk = $"item-{i}";

                // Create item
                var createBody = $$"""
                {
                  "TableName": "{{table}}",
                  "Item": { "pk": { "S": "{{pk}}" }, "v": { "N": "1" } }
                }
                """;
                await ExecuteAndAssertAsync("PutItem", createBody, $"create item {i}");

                // Sproc path: conditional UpdateItem with ReturnValues=NONE
                var sprocUpdate = $$"""
                {
                  "TableName": "{{table}}",
                  "Key": { "pk": { "S": "{{pk}}" } },
                  "UpdateExpression": "SET v = v + :inc",
                  "ConditionExpression": "v = :expected",
                  "ExpressionAttributeValues": { ":inc": { "N": "1" }, ":expected": { "N": "1" } }
                }
                """;
                var (s1, _, e1) = await ExecuteWithTimingAsync("UpdateItem", sprocUpdate);
                Assert.Equal(HttpStatusCode.OK, s1);
                sprocTimes[i] = e1.TotalMilliseconds;

                // Optimistic path: conditional UpdateItem with ReturnValues=ALL_NEW (forces GET→PUT)
                var optimisticUpdate = $$"""
                {
                  "TableName": "{{table}}",
                  "Key": { "pk": { "S": "{{pk}}" } },
                  "UpdateExpression": "SET v = v + :inc",
                  "ConditionExpression": "v = :expected",
                  "ExpressionAttributeValues": { ":inc": { "N": "1" }, ":expected": { "N": "2" } },
                  "ReturnValues": "ALL_NEW"
                }
                """;
                var (s2, _, e2) = await ExecuteWithTimingAsync("UpdateItem", optimisticUpdate);
                Assert.Equal(HttpStatusCode.OK, s2);
                optimisticTimes[i] = e2.TotalMilliseconds;
            }

            // Report results
            var avgSproc = Average(sprocTimes);
            var avgOptimistic = Average(optimisticTimes);
            var p50Sproc = Percentile(sprocTimes, 50);
            var p50Optimistic = Percentile(optimisticTimes, 50);
            var p99Sproc = Percentile(sprocTimes, 99);
            var p99Optimistic = Percentile(optimisticTimes, 99);

            _output.WriteLine("");
            _output.WriteLine("=== Latency Comparison (ms) ===");
            _output.WriteLine($"Sproc path (NONE):       avg={avgSproc:F1}  p50={p50Sproc:F1}  p99={p99Sproc:F1}");
            _output.WriteLine($"Optimistic (ALL_NEW):    avg={avgOptimistic:F1}  p50={p50Optimistic:F1}  p99={p99Optimistic:F1}");
            _output.WriteLine($"Difference:              avg={avgOptimistic - avgSproc:+0.0;-0.0}  ({(avgOptimistic / avgSproc - 1) * 100:+0.0;-0.0}%)");
            _output.WriteLine("");
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task TransactWriteItems_AtomicMultiPut_Commits()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);

        try
        {
            var body = $$"""
            {
              "TransactItems": [
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "a" }, "v": { "N": "1" } } } },
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "b" }, "v": { "N": "2" } } } }
              ]
            }
            """;
            var (status, respBody, _) = await ExecuteWithTimingAsync("TransactWriteItems", body);
            Skip.If(respBody.Contains("could not be provisioned", StringComparison.Ordinal), SprocUnsupportedReason);
            Assert.True(status == HttpStatusCode.OK, $"TransactWriteItems → {(int)status} {respBody}");

            Assert.True(await ItemExistsAsync(table, "order-1", "a"));
            Assert.True(await ItemExistsAsync(table, "order-1", "b"));
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task TransactWriteItems_ConditionFailure_RollsBackAllWrites()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);

        try
        {
            // Pre-create sk=b so the attribute_not_exists condition on it fails.
            var setup = $$"""
            {
              "TableName": "{{table}}",
              "Item": { "pk": { "S": "order-1" }, "sk": { "S": "b" }, "v": { "N": "9" } }
            }
            """;
            await ExecuteAndAssertAsync("PutItem", setup, "setup existing item");

            var body = $$"""
            {
              "TransactItems": [
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "a" }, "v": { "N": "1" } } } },
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "b" }, "v": { "N": "2" } },
                    "ConditionExpression": "attribute_not_exists(pk)" } }
              ]
            }
            """;
            var (status, respBody, _) = await ExecuteWithTimingAsync("TransactWriteItems", body);
            Skip.If(respBody.Contains("could not be provisioned", StringComparison.Ordinal), SprocUnsupportedReason);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("TransactionCanceledException", respBody);
            Assert.Contains("ConditionalCheckFailed", respBody);

            // Rollback: sk=a was never written, sk=b keeps its original value.
            Assert.False(await ItemExistsAsync(table, "order-1", "a"));
            var existing = await GetItemAsync(table, "order-1", "b");
            Assert.Equal("9", existing!.Value.GetProperty("v").GetProperty("N").GetString());
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task TransactWriteItems_ConditionCheckGate_AllowsWrite()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);

        try
        {
            // A gate item that the ConditionCheck inspects.
            var setup = $$"""
            {
              "TableName": "{{table}}",
              "Item": { "pk": { "S": "order-1" }, "sk": { "S": "gate" }, "status": { "S": "open" } }
            }
            """;
            await ExecuteAndAssertAsync("PutItem", setup, "setup gate item");

            var body = $$"""
            {
              "TransactItems": [
                { "ConditionCheck": { "TableName": "{{table}}",
                    "Key": { "pk": { "S": "order-1" }, "sk": { "S": "gate" } },
                    "ConditionExpression": "#s = :open",
                    "ExpressionAttributeNames": { "#s": "status" },
                    "ExpressionAttributeValues": { ":open": { "S": "open" } } } },
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "line-1" }, "qty": { "N": "3" } } } }
              ]
            }
            """;
            var (status, respBody, _) = await ExecuteWithTimingAsync("TransactWriteItems", body);
            Skip.If(respBody.Contains("could not be provisioned", StringComparison.Ordinal), SprocUnsupportedReason);
            Assert.True(status == HttpStatusCode.OK, $"TransactWriteItems → {(int)status} {respBody}");
            Assert.True(await ItemExistsAsync(table, "order-1", "line-1"));

            // Now flip the gate and confirm the ConditionCheck blocks the write.
            var flip = $$"""
            {
              "TableName": "{{table}}",
              "Item": { "pk": { "S": "order-1" }, "sk": { "S": "gate" }, "status": { "S": "closed" } }
            }
            """;
            await ExecuteAndAssertAsync("PutItem", flip, "flip gate");

            var body2 = $$"""
            {
              "TransactItems": [
                { "ConditionCheck": { "TableName": "{{table}}",
                    "Key": { "pk": { "S": "order-1" }, "sk": { "S": "gate" } },
                    "ConditionExpression": "#s = :open",
                    "ExpressionAttributeNames": { "#s": "status" },
                    "ExpressionAttributeValues": { ":open": { "S": "open" } } } },
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "line-2" }, "qty": { "N": "5" } } } }
              ]
            }
            """;
            var (status2, body2Resp, _) = await ExecuteWithTimingAsync("TransactWriteItems", body2);
            Assert.Equal(HttpStatusCode.BadRequest, status2);
            Assert.Contains("ConditionalCheckFailed", body2Resp);
            Assert.False(await ItemExistsAsync(table, "order-1", "line-2"));
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task TransactWriteItems_DeleteWithinTransaction_Removes()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);

        try
        {
            var setup = $$"""
            {
              "TableName": "{{table}}",
              "Item": { "pk": { "S": "order-1" }, "sk": { "S": "old" }, "v": { "N": "1" } }
            }
            """;
            await ExecuteAndAssertAsync("PutItem", setup, "setup item to delete");

            var body = $$"""
            {
              "TransactItems": [
                { "Delete": { "TableName": "{{table}}",
                    "Key": { "pk": { "S": "order-1" }, "sk": { "S": "old" } } } },
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "new" }, "v": { "N": "2" } } } }
              ]
            }
            """;
            var (status, respBody, _) = await ExecuteWithTimingAsync("TransactWriteItems", body);
            Skip.If(respBody.Contains("could not be provisioned", StringComparison.Ordinal), SprocUnsupportedReason);
            Assert.True(status == HttpStatusCode.OK, $"TransactWriteItems → {(int)status} {respBody}");

            Assert.False(await ItemExistsAsync(table, "order-1", "old"));
            Assert.True(await ItemExistsAsync(table, "order-1", "new"));
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task TransactWriteItems_CrossPartition_Rejected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);

        try
        {
            var body = $$"""
            {
              "TransactItems": [
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-1" }, "sk": { "S": "a" } } } },
                { "Put": { "TableName": "{{table}}", "Item": {
                    "pk": { "S": "order-2" }, "sk": { "S": "b" } } } }
              ]
            }
            """;
            var (status, respBody, _) = await ExecuteWithTimingAsync("TransactWriteItems", body);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("ValidationException", respBody);
            Assert.Contains("partition-key", respBody);
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

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
