using System.Text.Json;
using Xunit;

namespace Aws2Azure.PerfTests.DynamoDb;

public sealed class BatchWriteExperimentTelemetryTests
{
    [Fact]
    public async Task Resource_samples_are_bounded_ordered_and_report_observed_not_lifetime_peaks()
    {
        await using var window = new BatchWriteResourceWindow(Environment.ProcessId);
        var process = new BatchWriteProcessSample(DateTimeOffset.UnixEpoch, 0, 123, 9999, 1, null, null, null, null);
        window.Add(new(0, process, process));
        Assert.Throws<ArgumentException>(() => window.Add(new(0, process, process)));
        Assert.Throws<ArgumentException>(() => window.Add(new(double.NaN, process, process)));
        for (var i = 1; i < BatchWriteResourceWindow.MaxSamples; i++)
            window.Add(new(i * .25, process with { WorkingSetBytes = 456 }, process));
        window.Add(new(40, process, process));
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(window.Snapshot()));
        Assert.Equal(160, report.RootElement.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("droppedSamples").GetInt32());
        Assert.Equal(456, report.RootElement.GetProperty("maxObservedProxyWorkingSetBytes").GetInt64());
        Assert.Equal(123, report.RootElement.GetProperty("maxObservedDriverWorkingSetBytes").GetInt64());
    }

    [Fact]
    public async Task Empty_resource_window_reports_missing_not_zero()
    {
        await using var window = new BatchWriteResourceWindow(Environment.ProcessId);
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(window.Snapshot()));
        Assert.Equal(0, report.RootElement.GetProperty("sampleCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("maxObservedProxyWorkingSetBytes").ValueKind);
    }

    [Fact]
    public void Stage_deltas_are_counts_and_sums_not_subtracted_percentiles()
    {
        var before = BatchWriteStageTelemetry.Parse("""
            aws2azure_batch_write_stage_seconds_sum{stage="semaphore_wait"} 0.5
            aws2azure_batch_write_stage_seconds_count{stage="semaphore_wait"} 5
            """);
        var after = BatchWriteStageTelemetry.Parse("""
            aws2azure_batch_write_stage_seconds_sum{stage="semaphore_wait"} 2.5
            aws2azure_batch_write_stage_seconds_count{stage="semaphore_wait"} 15
            aws2azure_batch_write_stage_seconds_sum{stage="resource-key"} 9999
            aws2azure_batch_write_stage_seconds_sum{stage="document_encode"} NaN
            """);
        Assert.Equal(2, after.Count);
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(BatchWriteStageTelemetry.Delta(before, after)));
        var wait = report.RootElement.EnumerateArray().Single(x => x.GetProperty("stage").GetString() == "semaphore_wait");
        Assert.Equal(10, wait.GetProperty("count").GetDouble());
        Assert.Equal(2, wait.GetProperty("seconds").GetDouble());
        Assert.Equal(.2, wait.GetProperty("meanSeconds").GetDouble());
        var missing = report.RootElement.EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("seconds").ValueKind);
    }

    [Fact]
    public void Missing_baseline_and_reset_counters_are_not_zero()
    {
        var values = BatchWriteStageTelemetry.Parse("""
            aws2azure_batch_write_stage_seconds_sum{stage="envelope_parse"} 1
            aws2azure_batch_write_stage_seconds_count{stage="envelope_parse"} 5
            """);
        foreach (var result in new[]
        {
            BatchWriteStageTelemetry.Delta(null, values),
            BatchWriteStageTelemetry.Delta([], values),
            BatchWriteStageTelemetry.Delta(values, []),
        })
        {
            using var report = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.All(report.RootElement.EnumerateArray(), stage =>
                Assert.Equal(JsonValueKind.Null, stage.GetProperty("seconds").ValueKind));
        }
    }

    [Fact]
    public void Runtime_manifest_rejects_changed_missing_and_additional_dependencies()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "batch-runtime-test-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(directory, "app");
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(app, "Aws2Azure.Proxy"), "executable");
            File.WriteAllText(Path.Combine(app, "dependency.dll"), "dependency");
            var manifest = new BatchWriteRuntime(new string('a', 40), "Aws2Azure.Proxy", BatchWriteRuntime.HashFiles(app));
            File.WriteAllText(Path.Combine(directory, "runtime-identity.json"), JsonSerializer.Serialize(manifest));
            Assert.Equal(manifest.Commit, BatchWriteRuntime.Verify(directory).Commit);
            File.WriteAllText(Path.Combine(app, "dependency.dll"), "changed");
            Assert.Throws<InvalidOperationException>(() => BatchWriteRuntime.Verify(directory));
            File.WriteAllText(Path.Combine(app, "dependency.dll"), "dependency");
            File.WriteAllText(Path.Combine(app, "extra.dll"), "extra");
            Assert.Throws<InvalidOperationException>(() => BatchWriteRuntime.Verify(directory));
            File.Delete(Path.Combine(app, "extra.dll"));
            File.Delete(Path.Combine(app, "dependency.dll"));
            Assert.Throws<InvalidOperationException>(() => BatchWriteRuntime.Verify(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Item_acknowledgements_are_bounded_and_have_separate_distribution()
    {
        var accounting = new BatchWriteExperimentAccounting();
        accounting.Acknowledged(3, 10);
        accounting.Acknowledged(2, 50);
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(accounting.Snapshot(1)));
        var items = report.RootElement.GetProperty("itemAcknowledgementLatencyMs");
        Assert.Equal(5, items.GetProperty("count").GetInt32());
        Assert.Equal(10, items.GetProperty("p50").GetDouble());
        Assert.Equal(50, items.GetProperty("p99").GetDouble());
        accounting.Acknowledged(3195, 1);
        Assert.Throws<InvalidOperationException>(() => accounting.Acknowledged(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => accounting.Acknowledged(1, double.NaN));
    }
}
