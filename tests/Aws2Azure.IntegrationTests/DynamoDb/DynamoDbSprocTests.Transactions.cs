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
            Skip.If(IsSprocUnsupported(respBody), SprocUnsupportedReason);
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
            Skip.If(IsSprocUnsupported(respBody), SprocUnsupportedReason);
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
            Skip.If(IsSprocUnsupported(respBody), SprocUnsupportedReason);
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
            Skip.If(IsSprocUnsupported(respBody), SprocUnsupportedReason);
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
    public async Task TransactGetItems_SinglePartitionSnapshot_IsAlignedAndProjected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "tgi" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);
        try
        {
            await ExecuteAndAssertAsync(
                "PutItem",
                $$"""
                {
                  "TableName": "{{table}}",
                  "Item": {
                    "pk": { "S": "order-1" },
                    "sk": { "S": "left" },
                    "version": { "S": "7" },
                    "hidden": { "S": "x" }
                  }
                }
                """,
                "seed snapshot item");

            var (status, response, _) = await ExecuteWithTimingAsync(
                "TransactGetItems",
                $$"""
                {
                  "TransactItems": [
                    { "Get": {
                        "TableName": "{{table}}",
                        "Key": { "pk": { "S": "order-1" }, "sk": { "S": "left" } },
                        "ProjectionExpression": "pk, sk, version"
                    } },
                    { "Get": {
                        "TableName": "{{table}}",
                        "Key": { "pk": { "S": "order-1" }, "sk": { "S": "missing" } }
                    } }
                  ]
                }
                """);
            Skip.If(IsSprocUnsupported(response), SprocUnsupportedReason);
            Assert.Equal(HttpStatusCode.OK, status);
            using var document = JsonDocument.Parse(response);
            var responses = document.RootElement.GetProperty("Responses");
            Assert.Equal(2, responses.GetArrayLength());
            var item = responses[0].GetProperty("Item");
            Assert.Equal(
                "7",
                item.GetProperty("version").GetProperty("S").GetString());
            Assert.False(item.TryGetProperty("hidden", out _));
            Assert.False(responses[1].TryGetProperty("Item", out _));
        }
        finally
        {
            await DeleteTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task TransactWriteItems_MissingAttributeNotEqual_RollsBack()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping DynamoDB sproc test.");

        var table = "twi" + Guid.NewGuid().ToString("N")[..8];
        await CreateHashRangeTableAsync(table);
        try
        {
            var (status, response, _) = await ExecuteWithTimingAsync(
                "TransactWriteItems",
                $$"""
                {
                  "TransactItems": [
                    { "Put": {
                        "TableName": "{{table}}",
                        "Item": {
                          "pk": { "S": "order-1" },
                          "sk": { "S": "conditional" }
                        },
                        "ConditionExpression": "missing <> :value",
                        "ExpressionAttributeValues": { ":value": { "S": "x" } }
                    } },
                    { "Put": {
                        "TableName": "{{table}}",
                        "Item": {
                          "pk": { "S": "order-1" },
                          "sk": { "S": "peer" }
                        }
                    } }
                  ]
                }
                """);
            Skip.If(IsSprocUnsupported(response), SprocUnsupportedReason);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("ConditionalCheckFailed", response);
            Assert.False(await ItemExistsAsync(table, "order-1", "conditional"));
            Assert.False(await ItemExistsAsync(table, "order-1", "peer"));
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

}
