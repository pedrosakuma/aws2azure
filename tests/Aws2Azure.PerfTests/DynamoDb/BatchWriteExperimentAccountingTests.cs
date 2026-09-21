using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Xunit;

namespace Aws2Azure.PerfTests.DynamoDb;

public sealed class BatchWriteExperimentAccountingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    public void Execution_blocks_are_optional_bounded_order_metadata(string? value, int? expected)
        => Assert.Equal(expected, BatchWriteExperimentPlan.ParseExecutionBlock(value));

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("candidate")]
    public void Invalid_execution_blocks_fail_before_infrastructure(string value)
        => Assert.Throws<ArgumentException>(() => BatchWriteExperimentPlan.ParseExecutionBlock(value));

    [Theory]
    [InlineData(256)]
    [InlineData(8192)]
    public void Direct_reference_uses_the_same_encoded_keys_and_document_shape(int payloadBytes)
    {
        var plan = BatchWriteExperimentPlan.Parse($"25:8:put:shared:direct:{payloadBytes}");
        Assert.All(plan.Items(4), fixture => Assert.Equal(payloadBytes, fixture.PayloadBytes));
        var item = new BatchWriteExperimentItem("partition", "item", false, payloadBytes);
        using var document = JsonDocument.Parse(item.CosmosDocument());
        Assert.Equal("706172746974696f6e", item.CosmosPk);
        Assert.Equal("6974656d", item.CosmosId);
        Assert.Equal(item.CosmosId, document.RootElement.GetProperty("id").GetString());
        Assert.Equal(item.CosmosPk, document.RootElement.GetProperty("_a2a_pk").GetString());
        Assert.Equal("partition", document.RootElement.GetProperty("pk").GetString());
        Assert.Equal(payloadBytes, document.RootElement.GetProperty("payload").GetString()!.Length);
        Assert.Equal(payloadBytes, item.Write().PutRequest.Item["payload"].S.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("25:8:put:distinct")]
    [InlineData("26:8:put:distinct:proxy")]
    [InlineData("25:9:put:distinct:proxy")]
    [InlineData("25:8:upsert:distinct:proxy")]
    [InlineData("25:8:put:random:proxy")]
    [InlineData("25:8:put:distinct:azure")]
    [InlineData("25:8:put:distinct:proxy:512")]
    [InlineData("25:8:put:distinct:proxy:8192:extra")]
    public void Invalid_cells_fail_loud(string? value)
        => Assert.Throws<ArgumentException>(() => BatchWriteExperimentPlan.Parse(value));

    [Fact]
    public void Matrix_is_explicit_and_inventory_is_bounded()
    {
        var count = 0;
        foreach (var size in new[] { 1, 5, 10, 25 })
        foreach (var concurrency in new[] { 1, 2, 5, 8 })
        foreach (var kind in new[] { "put", "delete", "mixed" })
        foreach (var partitions in new[] { "shared", "distinct" })
        foreach (var route in new[] { "proxy", "direct" })
        {
            var plan = BatchWriteExperimentPlan.Parse($"{size}:{concurrency}:{kind}:{partitions}:{route}");
            var items = Enumerable.Range(0, 132).SelectMany(plan.Items).ToArray();
            Assert.Equal(132 * size, items.Length);
            Assert.Equal(items.Length, items.Select(x => x.Pk + "/" + x.Id).Distinct().Count());
            Assert.Equal(partitions == "shared" ? 1 : items.Length, items.Select(x => x.Pk).Distinct().Count());
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.Items(132));
            count++;
        }
        Assert.Equal(192, count);
    }

    [Fact]
    public async Task Partial_success_and_resubmission_count_items_once()
    {
        var accounting = new BatchWriteExperimentAccounting();
        var items = BatchWriteExperimentPlan.Parse("5:1:put:shared:proxy").Items(0).Select(x => x.Write()).ToList();
        var calls = 0;
        accounting.Started();
        await BatchWriteExperimentAccounting.DrainAsync(items, (pending, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? pending.Take(2).ToList() : []);
        }, accounting, CancellationToken.None);
        accounting.Settled(50, false);
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(accounting.Snapshot(2)));
        var root = report.RootElement;
        Assert.Equal(5, root.GetProperty("acknowledgedItems").GetInt32());
        Assert.Equal(2, root.GetProperty("submissions").GetInt32());
        Assert.Equal(1, root.GetProperty("initialRequests").GetInt32());
        Assert.Equal(1, root.GetProperty("resubmissionRequests").GetInt32());
        Assert.Equal(0, root.GetProperty("requestsWithoutAcknowledgement").GetInt32());
        Assert.Equal(2, root.GetProperty("resubmittedItems").GetInt32());
        Assert.Equal(2, root.GetProperty("unprocessedItemOccurrences").GetInt32());
        Assert.Equal(2.5, root.GetProperty("acknowledgedItemsPerSecond").GetDouble());
        Assert.Equal(.5, root.GetProperty("batchesPerSecond").GetDouble());
        var latency = root.GetProperty("itemAcknowledgementLatencyMs");
        Assert.Equal(5, latency.GetProperty("count").GetInt32());
        Assert.True(latency.GetProperty("p99").GetDouble() > latency.GetProperty("p50").GetDouble());
    }

    [Fact]
    public async Task Exhausted_retries_preserve_partial_success_and_fail_the_batch()
    {
        var accounting = new BatchWriteExperimentAccounting();
        var items = BatchWriteExperimentPlan.Parse("5:1:put:shared:proxy").Items(0).Select(x => x.Write()).ToList();
        accounting.Started();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BatchWriteExperimentAccounting.DrainAsync(
            items, (pending, _) => Task.FromResult(pending.Take(1).ToList()), accounting, CancellationToken.None));
        accounting.Settled(500, true);
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(accounting.Snapshot(1)));
        Assert.Equal(4, report.RootElement.GetProperty("acknowledgedItems").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("failedBatches").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("completedBatches").GetInt32());
        Assert.Equal(5, report.RootElement.GetProperty("submissions").GetInt32());
        Assert.Equal(4, report.RootElement.GetProperty("itemAcknowledgementLatencyMs").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Unprocessed_keys_cannot_duplicate_or_escape_the_submitted_subset()
    {
        var items = BatchWriteExperimentPlan.Parse("5:1:put:shared:proxy").Items(0).Select(x => x.Write()).ToList();
        foreach (var invalid in new[] { new List<WriteRequest> { items[0], items[0] },
            new List<WriteRequest> { new BatchWriteExperimentItem("other", "other", false).Write() } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => BatchWriteExperimentAccounting.DrainAsync(
                items, (_, _) => Task.FromResult(invalid), new(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_not_a_success_and_zero_duration_is_invalid()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var items = new List<WriteRequest> { new BatchWriteExperimentItem("p", "i", false).Write() };
        var accounting = new BatchWriteExperimentAccounting();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BatchWriteExperimentAccounting.DrainAsync(
            items, (_, ct) => Task.FromCanceled<List<WriteRequest>>(ct), accounting, cancellation.Token));
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(accounting.Snapshot(1)));
        Assert.Equal(1, report.RootElement.GetProperty("initialRequests").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("requestsWithoutAcknowledgement").GetInt32());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchWriteExperimentAccounting().Snapshot(0));
    }

    [Fact]
    public void Concurrent_accounting_remains_bounded()
    {
        var accounting = new BatchWriteExperimentAccounting();
        Parallel.For(0, 128, _ =>
        {
            accounting.Started();
            accounting.Attempt(false);
            accounting.Submission(25, 0, false);
            accounting.Settled(1, false);
        });
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(accounting.Snapshot(2)));
        Assert.Equal(3200, report.RootElement.GetProperty("acknowledgedItems").GetInt32());
        Assert.Equal(128, report.RootElement.GetProperty("batchLatencyMs").GetProperty("count").GetInt32());
        Assert.Throws<InvalidOperationException>(accounting.Started);
    }
}
