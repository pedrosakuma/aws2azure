using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

// Separate from qualification evidence: these approximate distributions never feed a gate.
internal sealed class OperationTimingDiagnostics
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock;
    private readonly OperationTimingReport _report;
    private readonly Dictionary<string, Dictionary<string, Accumulator>> _operations;
    private readonly double _windowSeconds;

    public OperationTimingDiagnostics(
        IEnumerable<string> operations,
        Stopwatch clock,
        DateTimeOffset startedAtUtc,
        TimeSpan requestedDuration,
        int concurrency,
        string role,
        string runtimeDigest,
        DateTimeOffset? scheduledStartUtc = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestedDuration.TotalSeconds, 0);
        _clock = clock;
        _windowSeconds = Math.Max(60, requestedDuration.TotalSeconds / 60);
        _report = new OperationTimingReport
        {
            StartedAtUtc = startedAtUtc,
            ScheduledStartUtc = scheduledStartUtc,
            RequestedDurationSeconds = requestedDuration.TotalSeconds,
            WindowSeconds = _windowSeconds,
            Concurrency = concurrency,
            Role = role,
            RuntimeDigest = runtimeDigest,
            RunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? string.Empty,
            RunAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? string.Empty,
            HarnessSourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? string.Empty,
        };
        _operations = operations.ToDictionary(
            operation => operation,
            _ => new Dictionary<string, Accumulator>(StringComparer.Ordinal)
            {
                ["lifecycle"] = new(),
                ["measurement"] = new(),
                ["drain"] = new(),
                ["cleanup"] = new(),
            },
            StringComparer.Ordinal);
    }

    public void Record(
        string operation,
        double milliseconds,
        bool failed,
        bool throttled = false,
        Exception? exception = null,
        bool cleanup = false) =>
        RecordAt(operation, milliseconds, failed, throttled, exception, cleanup, _clock.Elapsed.TotalSeconds);

    internal void RecordAt(
        string operation,
        double milliseconds,
        bool failed,
        bool throttled,
        Exception? exception,
        bool cleanup,
        double completedSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        if (!double.IsFinite(milliseconds) || !double.IsFinite(completedSeconds) || completedSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }
        lock (_sync)
        {
            var summaries = _operations[operation];
            var phase = cleanup ? "cleanup"
                : completedSeconds < _report.RequestedDurationSeconds ? "measurement" : "drain";
            var failure = failed
                ? new OperationTimingFailure
                {
                    CompletedAtUtc = _report.StartedAtUtc.AddSeconds(completedSeconds),
                    WindowOffsetSeconds = completedSeconds,
                    Detail = exception is null ? null
                        : RealAzureWorkloadFirstFailure.FromException(exception, throttled),
                }
                : null;
            summaries[phase].Record(milliseconds, failed, throttled, failure);
            if (cleanup)
            {
                return;
            }
            summaries["lifecycle"].Record(milliseconds, failed, throttled, failure);
            if (phase == "measurement")
            {
                var index = Math.Min(59, (int)(completedSeconds / _windowSeconds));
                var key = $"window-{index}";
                if (!summaries.TryGetValue(key, out var window))
                {
                    summaries[key] = window = new Accumulator();
                }
                window.Record(milliseconds, failed, throttled, failure);
            }
        }
    }

    public async Task MeasureCleanupAsync(string operation, Func<Task> action, Func<Exception, bool> isThrottle)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action().ConfigureAwait(false);
            Record(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds, false, cleanup: true);
        }
        catch (Exception exception)
        {
            Record(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds, true,
                isThrottle(exception), exception, cleanup: true);
            throw;
        }
    }

    internal OperationTimingReport Snapshot(double elapsedSeconds, bool workersCompleted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedSeconds);
        if (!double.IsFinite(elapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }
        lock (_sync)
        {
            _report.ElapsedSeconds = elapsedSeconds;
            _report.EndedAtUtc = _report.StartedAtUtc.AddSeconds(elapsedSeconds);
            _report.WorkersCompleted = workersCompleted;
            _report.Operations = [];
            foreach (var (operation, summaries) in _operations.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                foreach (var (phase, accumulator) in summaries)
                {
                    var start = phase == "drain" ? Math.Min(elapsedSeconds, _report.RequestedDurationSeconds) : 0;
                    var end = phase == "measurement"
                        ? Math.Min(elapsedSeconds, _report.RequestedDurationSeconds)
                        : elapsedSeconds;
                    if (phase.StartsWith("window-", StringComparison.Ordinal))
                    {
                        var index = int.Parse(phase.AsSpan(7), System.Globalization.CultureInfo.InvariantCulture);
                        start = index * _windowSeconds;
                        end = Math.Min(elapsedSeconds, Math.Min(start + _windowSeconds, _report.RequestedDurationSeconds));
                    }
                    _report.Operations.Add(accumulator.Snapshot(operation, phase, start, end));
                }
            }
            return _report;
        }
    }

    public async Task PublishAsync(string evidencePath, bool workersCompleted)
    {
        // Reporting failures must not change the pre-existing qualification/observation verdict.
        try
        {
            var path = RealAzureWorkloadLoad.ResolveOutputPath(
                evidencePath + "." + _report.Role + ".operation-timings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var report = Snapshot(_clock.Elapsed.TotalSeconds, workersCompleted);
            report.EvidenceFile = Path.GetFileName(evidencePath);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
                report, OperationTimingJsonContext.Default.OperationTimingReport)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Operation timing report unavailable: {exception.GetType().Name}.");
        }
    }

    private sealed class Accumulator
    {
        // 512 fixed buckets: <=1ms, then powers of 1.05. No request/secret data retained.
        private readonly long[] _histogram = new long[512];
        private long _successes;
        private long _failures;
        private long _throttles;
        private double _total;
        private double _maximum;
        private OperationTimingFailure? _firstFailure;

        public void Record(double milliseconds, bool failed, bool throttled, OperationTimingFailure? failure)
        {
            if (failed) _failures++; else _successes++;
            if (throttled) _throttles++;
            _total += milliseconds;
            _maximum = Math.Max(_maximum, milliseconds);
            var bucket = milliseconds <= 1 ? 0
                : Math.Min(511, (int)Math.Ceiling(Math.Log(milliseconds) / Math.Log(1.05)));
            _histogram[bucket]++;
            if (failure is not null && (_firstFailure is null
                || failure.WindowOffsetSeconds < _firstFailure.WindowOffsetSeconds))
            {
                _firstFailure = failure;
            }
        }

        public OperationTimingSummary Snapshot(string operation, string phase, double start, double end)
        {
            var attempts = _successes + _failures;
            var duration = Math.Max(0, end - start);
            return new OperationTimingSummary
            {
                Operation = operation,
                Phase = phase,
                StartOffsetSeconds = start,
                EndOffsetSeconds = end,
                DurationSeconds = duration,
                Successes = _successes,
                Attempts = attempts,
                Errors = _failures,
                Throttles = _throttles,
                SuccessesPerSecond = duration > 0 ? _successes / duration : null,
                AttemptsPerSecond = duration > 0 ? attempts / duration : null,
                ErrorRate = attempts > 0 ? (double)_failures / attempts : null,
                CumulativeMilliseconds = _total,
                MeanMilliseconds = attempts > 0 ? _total / attempts : null,
                P50Milliseconds = Percentile(attempts, 0.50),
                P95Milliseconds = Percentile(attempts, 0.95),
                P99Milliseconds = Percentile(attempts, 0.99),
                FirstFailure = _firstFailure,
            };
        }

        private double? Percentile(long count, double percentile)
        {
            if (count == 0) return null;
            var rank = (long)Math.Ceiling(count * percentile);
            long total = 0;
            for (var bucket = 0; bucket < _histogram.Length; bucket++)
            {
                total += _histogram[bucket];
                if (total >= rank)
                {
                    return bucket == _histogram.Length - 1
                        ? _maximum : Math.Min(_maximum, Math.Pow(1.05, bucket));
                }
            }
            return _maximum;
        }
    }
}

internal sealed class OperationTimingReport
{
    public int SchemaVersion { get; set; } = 1;
    public string ArtifactKind { get; set; } = "operation_timing_diagnostics";
    public bool Promotable { get; set; }
    public string Service { get; set; } = "secretsmanager";
    public string Workload { get; set; } = "secretsmanager-basic-lifecycle";
    public string[] OperationSchedule { get; set; } =
        ["CreateSecret", "DescribeSecret", "GetSecretValue", "PutSecretValue", "GetSecretValue",
         "UpdateSecret", "GetSecretValue", "ListSecrets", "DeleteSecret"];
    public string Warmup { get; set; } = "not_performed";
    public string Attribution { get; set; } = "client_action_including_sdk_retries_and_value_polling";
    public string PhaseAssignment { get; set; } = "completion_time";
    public string PercentileMethod { get; set; } = "histogram_upper_bound_5_percent_or_1_ms";
    public string CleanupDenominator { get; set; } = "overlapping_workload_wall_clock";
    public string RunId { get; set; } = string.Empty;
    public string RunAttempt { get; set; } = string.Empty;
    public string HarnessSourceSha { get; set; } = string.Empty;
    public string EvidenceFile { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string RuntimeDigest { get; set; } = string.Empty;
    public int Concurrency { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? ScheduledStartUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public double RequestedDurationSeconds { get; set; }
    public double WindowSeconds { get; set; }
    public double ElapsedSeconds { get; set; }
    public bool WorkersCompleted { get; set; }
    public List<OperationTimingSummary> Operations { get; set; } = [];
}

internal sealed class OperationTimingSummary
{
    public string Operation { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public double StartOffsetSeconds { get; set; }
    public double EndOffsetSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public long Successes { get; set; }
    public long Attempts { get; set; }
    public long Errors { get; set; }
    public long Throttles { get; set; }
    public double? SuccessesPerSecond { get; set; }
    public double? AttemptsPerSecond { get; set; }
    public double? ErrorRate { get; set; }
    public double CumulativeMilliseconds { get; set; }
    public double? MeanMilliseconds { get; set; }
    public double? P50Milliseconds { get; set; }
    public double? P95Milliseconds { get; set; }
    public double? P99Milliseconds { get; set; }
    public OperationTimingFailure? FirstFailure { get; set; }
}

internal sealed class OperationTimingFailure
{
    public DateTimeOffset CompletedAtUtc { get; set; }
    public double WindowOffsetSeconds { get; set; }
    public RealAzureWorkloadFirstFailure? Detail { get; set; }
}

[JsonSerializable(typeof(OperationTimingReport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class OperationTimingJsonContext : JsonSerializerContext;
