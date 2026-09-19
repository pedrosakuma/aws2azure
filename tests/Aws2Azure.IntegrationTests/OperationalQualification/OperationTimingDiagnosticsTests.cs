using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

public sealed class OperationTimingDiagnosticsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Phase_denominators_counts_and_client_time_are_explicit()
    {
        var diagnostics = Create();
        diagnostics.RecordAt("GetSecretValue", 100, false, false, null, false, 1);
        diagnostics.RecordAt("GetSecretValue", 300, true, true,
            new HttpRequestException("private payload", null, HttpStatusCode.TooManyRequests), false, 2);
        // An action spanning the cutoff is wholly attributed to its completion phase.
        diagnostics.RecordAt("GetSecretValue", 2000, false, false, null, false, 60);
        diagnostics.RecordAt("DeleteSecret", 50, false, false, null, true, 61);
        var report = diagnostics.Snapshot(65, true);
        var lifecycle = Find(report, "GetSecretValue", "lifecycle");
        Assert.Equal(3, lifecycle.Attempts);
        Assert.Equal(2, lifecycle.Successes);
        Assert.Equal(1, lifecycle.Errors);
        Assert.Equal(1, lifecycle.Throttles);
        Assert.Equal(2400, lifecycle.CumulativeMilliseconds);
        Assert.Equal(800, lifecycle.MeanMilliseconds);
        Assert.Equal(2.0 / 65, lifecycle.SuccessesPerSecond);
        Assert.Equal(3.0 / 65, lifecycle.AttemptsPerSecond);
        Assert.Equal(1.0 / 3, lifecycle.ErrorRate);
        var measurement = Find(report, "GetSecretValue", "measurement");
        Assert.Equal(60, measurement.DurationSeconds);
        Assert.Equal(2, measurement.Attempts);
        Assert.Equal(1.0 / 60, measurement.SuccessesPerSecond);
        var drain = Find(report, "GetSecretValue", "drain");
        Assert.Equal(5, drain.DurationSeconds);
        Assert.Equal(1, drain.Attempts);
        Assert.Equal(0.2, drain.SuccessesPerSecond);
        Assert.Equal(0, Find(report, "DeleteSecret", "lifecycle").Attempts);
        Assert.Equal(1, Find(report, "DeleteSecret", "cleanup").Attempts);
        Assert.Equal(65, Find(report, "DeleteSecret", "cleanup").DurationSeconds);
        Assert.Equal(Start.AddSeconds(65), report.EndedAtUtc);
        Assert.False(report.Promotable);
        Assert.Equal("not_performed", report.Warmup);
    }

    [Fact]
    public void Empty_and_shortened_measurements_do_not_invent_samples_or_requested_duration()
    {
        var diagnostics = Create();
        diagnostics.RecordAt("GetSecretValue", 0, false, false, null, false, 0.5);
        var report = diagnostics.Snapshot(2, false);
        Assert.False(report.WorkersCompleted);
        Assert.Equal(2, Find(report, "GetSecretValue", "measurement").DurationSeconds);
        Assert.Equal(0.5, Find(report, "GetSecretValue", "measurement").SuccessesPerSecond);
        Assert.Equal(0, Find(report, "GetSecretValue", "measurement").P99Milliseconds);
        var empty = Find(report, "DeleteSecret", "drain");
        Assert.Equal(0, empty.DurationSeconds);
        Assert.Null(empty.SuccessesPerSecond);
        Assert.Null(empty.MeanMilliseconds);
        Assert.Null(empty.P50Milliseconds);
        Assert.Null(empty.ErrorRate);
    }

    [Fact]
    public void Percentiles_are_bounded_histogram_estimates_and_totals_are_exact()
    {
        var diagnostics = Create();
        for (var sample = 1; sample <= 100; sample++)
        {
            diagnostics.RecordAt("GetSecretValue", sample, false, false, null, false, 1);
        }
        var result = Find(diagnostics.Snapshot(60, true), "GetSecretValue", "measurement");
        Assert.Equal(5050, result.CumulativeMilliseconds);
        Assert.Equal(50.5, result.MeanMilliseconds);
        Assert.InRange(result.P50Milliseconds!.Value, 50, 52.5);
        Assert.InRange(result.P95Milliseconds!.Value, 95, 99.75);
        Assert.InRange(result.P99Milliseconds!.Value, 99, 100);
    }

    [Fact]
    public void Time_windows_are_bounded_even_for_long_high_volume_runs()
    {
        var diagnostics = Create(TimeSpan.FromHours(3));
        Parallel.For(0, 100_000, sample =>
            diagnostics.RecordAt("GetSecretValue", 20, false, false, null, false, sample % 10800));
        var report = diagnostics.Snapshot(10800, true);
        var windows = report.Operations.Where(item => item.Operation == "GetSecretValue"
            && item.Phase.StartsWith("window-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(60, windows.Length);
        Assert.Equal(180, report.WindowSeconds);
        Assert.All(windows, item => Assert.Equal(180, item.DurationSeconds));
        Assert.Equal(100_000, windows.Sum(item => item.Attempts));
        Assert.Equal(100_000, Find(report, "GetSecretValue", "lifecycle").Attempts);
        Assert.Equal(68, report.Operations.Count);
    }

    [Fact]
    public void Last_window_uses_actual_partial_duration()
    {
        var diagnostics = Create(TimeSpan.FromSeconds(65));
        diagnostics.RecordAt("GetSecretValue", 5, false, false, null, false, 64);
        var result = Find(diagnostics.Snapshot(70, true), "GetSecretValue", "window-1");
        Assert.Equal(60, result.StartOffsetSeconds);
        Assert.Equal(65, result.EndOffsetSeconds);
        Assert.Equal(5, result.DurationSeconds);
        Assert.Equal(0.2, result.SuccessesPerSecond);
    }

    [Fact]
    public void Earliest_failure_timestamp_and_safe_metadata_survive_strict_json_roundtrip()
    {
        var diagnostics = Create();
        var exception = new HttpRequestException(
            "secret-name https://vault.example/secrets/name?token=abc", null, HttpStatusCode.Forbidden);
        diagnostics.RecordAt("GetSecretValue", 10, true, false, exception, false, 4);
        diagnostics.RecordAt("GetSecretValue", 20, true, false, exception, false, 2);
        var json = JsonSerializer.Serialize(diagnostics.Snapshot(60, true),
            OperationTimingJsonContext.Default.OperationTimingReport);
        Assert.DoesNotContain("vault.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", json, StringComparison.Ordinal);
        var report = JsonSerializer.Deserialize(json, OperationTimingJsonContext.Default.OperationTimingReport)!;
        var failure = Find(report, "GetSecretValue", "lifecycle").FirstFailure!;
        Assert.Equal(Start.AddSeconds(2), failure.CompletedAtUtc);
        Assert.Equal(2, failure.WindowOffsetSeconds);
        Assert.Equal(403, failure.Detail!.StatusCode);
        Assert.Equal("HttpRequestException", failure.Detail.ErrorCode);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            json.Replace("\"schema_version\": 1", "\"unexpected\": 1", StringComparison.Ordinal),
            OperationTimingJsonContext.Default.OperationTimingReport));
    }

    [Fact]
    public async Task Tracker_and_cleanup_preserve_legacy_gate_counts_and_failure_propagation()
    {
        var diagnostics = Create();
        var tracker = new RealAzureWorkloadLoadTracker("secretsmanager", ["GetSecretValue", "DeleteSecret"])
        {
            TimingDiagnostics = diagnostics,
        };
        await RealAzureWorkloadLoad.MeasureAsync(tracker, "GetSecretValue", () => Task.CompletedTask, _ => false);
        var exception = new TimeoutException("private");
        Assert.Same(exception, await Assert.ThrowsAsync<TimeoutException>(() =>
            RealAzureWorkloadLoad.MeasureAsync(tracker, "GetSecretValue", () => Task.FromException(exception), _ => false)));
        Assert.Same(exception, await Assert.ThrowsAsync<TimeoutException>(() =>
            diagnostics.MeasureCleanupAsync("DeleteSecret", () => Task.FromException(exception), _ => false)));
        Assert.Equal(1, tracker.Snapshot("GetSecretValue").Completions);
        Assert.Equal(1, tracker.Snapshot("GetSecretValue").Failures);
        Assert.Equal(0, tracker.Snapshot("DeleteSecret").Failures);
        Assert.Equal(2, tracker.Latencies("GetSecretValue").Length);
        var report = diagnostics.Snapshot(60, true);
        Assert.Equal(2, Find(report, "GetSecretValue", "lifecycle").Attempts);
        Assert.Equal(1, Find(report, "DeleteSecret", "cleanup").Errors);
        var legacy = JsonSerializer.Serialize(new RcObservationCaptureCohort
        {
            OperationDiagnostics = RcObservationCaptureWriter.OperationDiagnostics(tracker),
        }, RcObservationCaptureJsonContext.Default.RcObservationCaptureCohort);
        Assert.DoesNotContain("duration_seconds", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("p50", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_report_is_persisted_separately_without_requiring_authoritative_evidence()
    {
        var directory = Path.Combine("artifacts", "operation-timing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.GetFullPath(Path.Combine(directory, "capture.json"));
            await Create().PublishAsync(path, workersCompleted: false);
            Assert.False(File.Exists(path));
            var json = await File.ReadAllTextAsync(path + ".candidate.operation-timings.json");
            var report = JsonSerializer.Deserialize(json, OperationTimingJsonContext.Default.OperationTimingReport)!;
            Assert.Equal("capture.json", report.EvidenceFile);
            Assert.False(report.WorkersCompleted);
            Assert.Equal(1, report.SchemaVersion);
            Assert.Equal("operation_timing_diagnostics", report.ArtifactKind);
            // A file where a directory is expected is a deterministic I/O failure, not a new gate.
            await Create().PublishAsync(path + ".candidate.operation-timings.json/blocked.json", false);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Non_io_reporting_failure_does_not_mask_worker_failure()
    {
        var failure = new TimeoutException("worker failure");
        var actual = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            try
            {
                await Task.FromException(failure);
            }
            finally
            {
                await Create().PublishAsync("invalid\0path", workersCompleted: false);
            }
        });
        Assert.Same(failure, actual);
    }

    private static OperationTimingDiagnostics Create(TimeSpan? duration = null) =>
        new(["GetSecretValue", "DeleteSecret"], new Stopwatch(), Start,
            duration ?? TimeSpan.FromSeconds(60), 5, "candidate", "sha256:diagnostic-test");

    private static OperationTimingSummary Find(OperationTimingReport report, string operation, string phase) =>
        Assert.Single(report.Operations, item => item.Operation == operation && item.Phase == phase);
}
