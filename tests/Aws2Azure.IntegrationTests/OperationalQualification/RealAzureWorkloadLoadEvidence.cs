using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class RealAzureWorkloadLoad
{
    public static async Task MeasureAsync(
        RealAzureWorkloadLoadTracker tracker,
        string operation,
        Func<Task> action,
        Func<Exception, bool> isThrottle)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action().ConfigureAwait(false);
            tracker.RecordSuccess(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            tracker.RecordFailure(
                operation,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                isThrottle(exception),
                exception);
            throw;
        }
    }

    public static async Task<double[]> ProbeNetworkAsync(Uri target, int samples)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        var latencies = new double[samples];
        for (var index = 0; index < samples; index++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(
                target,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return latencies;
    }

    public static RealAzureWorkloadLoadScenario Scenario(
        string id,
        string service,
        string operation,
        string evidenceSource,
        long completions,
        long failures,
        long skipped,
        double durationSeconds,
        DateTimeOffset capturedAtUtc) => new()
    {
        Id = id,
        Service = service,
        Operation = operation,
        EvidenceSource = evidenceSource,
        Completions = completions,
        Failures = failures,
        Skipped = skipped,
        DurationSeconds = durationSeconds,
        CapturedAtUtc = capturedAtUtc,
    };

    public static RealAzureWorkloadLoadSignal Signal(
        string id,
        string scenarioId,
        string metric,
        double measuredValue,
        long samples,
        DateTimeOffset capturedAtUtc) => new()
    {
        Id = id,
        ScenarioId = scenarioId,
        Metric = metric,
        MeasuredValue = measuredValue,
        Samples = samples,
        CapturedAtUtc = capturedAtUtc,
    };

    public static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        var index = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Length) - 1,
            0,
            ordered.Length - 1);
        return ordered[index];
    }

    public static int ReadPositiveInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new InvalidDataException($"{name} must be a positive integer.");
        }
        return parsed;
    }

    public static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{name} is required for load evidence.");
        }
        return value;
    }

    public static string ResolveOutputPath(string outputPath)
    {
        return Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.Combine(FindRepoRoot(), outputPath);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}

internal sealed class RealAzureWorkloadLoadTracker
{
    private readonly string _service;
    private readonly IReadOnlyDictionary<string, OperationTracker> _operations;

    public RealAzureWorkloadLoadTracker(string service, IEnumerable<string> operations)
    {
        _service = service;
        _operations = operations.ToDictionary(
            operation => operation,
            _ => new OperationTracker(),
            StringComparer.Ordinal);
    }

    public void RecordSuccess(string operation, double elapsedMilliseconds)
    {
        _operations[operation].RecordSuccess(elapsedMilliseconds);
    }

    public void RecordFailure(
        string operation,
        double elapsedMilliseconds,
        bool throttled,
        Exception? exception = null)
    {
        _operations[operation].RecordFailure(elapsedMilliseconds, throttled, exception);
    }

    public List<RealAzureWorkloadLoadOperationMeasurement> Snapshot()
    {
        return _operations
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value.Snapshot(_service, item.Key))
            .ToList();
    }

    public RealAzureWorkloadLoadOperationMeasurement Snapshot(string operation)
    {
        return _operations[operation].Snapshot(_service, operation);
    }

    public double[] Latencies(string operation)
    {
        return _operations[operation].Latencies.ToArray();
    }

    public long Throttles(string operation)
    {
        return _operations[operation].Throttles;
    }

    public string? FirstFailure(string operation)
    {
        return _operations[operation].FirstFailure;
    }

    private sealed class OperationTracker
    {
        private readonly ConcurrentQueue<double> _latencies = new();
        private long _completions;
        private long _failures;
        private long _throttles;
        private string? _firstFailure;

        public IEnumerable<double> Latencies => _latencies;
        public long Throttles => Interlocked.Read(ref _throttles);
        public string? FirstFailure => Volatile.Read(ref _firstFailure);

        public void RecordSuccess(double elapsedMilliseconds)
        {
            _latencies.Enqueue(elapsedMilliseconds);
            Interlocked.Increment(ref _completions);
        }

        public void RecordFailure(
            double elapsedMilliseconds,
            bool throttled,
            Exception? exception = null)
        {
            _latencies.Enqueue(elapsedMilliseconds);
            Interlocked.Increment(ref _failures);
            if (exception is not null)
            {
                Interlocked.CompareExchange(
                    ref _firstFailure,
                    $"{exception.GetType().Name}: {exception.Message}",
                    null);
            }
            if (throttled)
            {
                Interlocked.Increment(ref _throttles);
            }
        }

        public RealAzureWorkloadLoadOperationMeasurement Snapshot(
            string service,
            string operation)
        {
            var latencies = _latencies.ToArray();
            return new RealAzureWorkloadLoadOperationMeasurement
            {
                Service = service,
                Operation = operation,
                Completions = Interlocked.Read(ref _completions),
                Failures = Interlocked.Read(ref _failures),
                P95Milliseconds = RealAzureWorkloadLoad.Percentile(latencies, 0.95),
                P99Milliseconds = RealAzureWorkloadLoad.Percentile(latencies, 0.99),
            };
        }
    }
}

internal sealed class RealAzureWorkloadLoadEvidence
{
    public int SchemaVersion { get; set; }
    public RealAzureWorkloadLoadProfile Profile { get; set; } = new();
    public RealAzureWorkloadLoadCandidate Candidate { get; set; } = new();
    public RealAzureWorkloadLoadProvenance Provenance { get; set; } = new();
    public RealAzureWorkloadLoadShape LoadShape { get; set; } = new();
    public List<RealAzureWorkloadLoadOperationMeasurement> OperationMix { get; set; } = new();
    public List<RealAzureWorkloadLoadScenario> Scenarios { get; set; } = new();
    public List<RealAzureWorkloadLoadSignal> Signals { get; set; } = new();
}

internal sealed class RealAzureWorkloadLoadProfile
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public List<RealAzureWorkloadLoadProfileService> Services { get; set; } = new();
}

internal sealed class RealAzureWorkloadLoadProfileService
{
    public string Service { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
}

internal sealed class RealAzureWorkloadLoadCandidate
{
    public string GitSha { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
}

internal sealed class RealAzureWorkloadLoadProvenance
{
    public string RunId { get; set; } = string.Empty;
    public string RunUrl { get; set; } = string.Empty;
    public int RunAttempt { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset WindowStartUtc { get; set; }
    public DateTimeOffset WindowEndUtc { get; set; }
    public string Region { get; set; } = string.Empty;
    public string BackendDescription { get; set; } = string.Empty;
    public string ProducerConfigDigest { get; set; } = string.Empty;
}

internal sealed class RealAzureWorkloadLoadShape
{
    public int Concurrency { get; set; }
    public double RequestedDurationSeconds { get; set; }
}

internal sealed class RealAzureWorkloadLoadOperationMeasurement
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public long Completions { get; set; }
    public long Failures { get; set; }
    public double P95Milliseconds { get; set; }
    public double P99Milliseconds { get; set; }
}

internal sealed class RealAzureWorkloadLoadScenario
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public long Completions { get; set; }
    public long Failures { get; set; }
    public long Skipped { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

internal sealed class RealAzureWorkloadLoadSignal
{
    public string Id { get; set; } = string.Empty;
    public string ScenarioId { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public double MeasuredValue { get; set; }
    public long Samples { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

[JsonSerializable(typeof(RealAzureWorkloadLoadEvidence))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
internal sealed partial class RealAzureWorkloadLoadEvidenceJsonContext : JsonSerializerContext;
