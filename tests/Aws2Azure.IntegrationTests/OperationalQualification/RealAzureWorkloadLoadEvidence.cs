using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using Aws2Azure.TestSupport.OperationalQualification;

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
        return _operations[operation].FirstFailure?.ToString();
    }

    public RealAzureWorkloadFirstFailure? FirstFailureDetail(string operation)
    {
        return _operations[operation].FirstFailure;
    }

    private sealed class OperationTracker
    {
        private readonly ConcurrentQueue<double> _latencies = new();
        private long _completions;
        private long _failures;
        private long _throttles;
        private RealAzureWorkloadFirstFailure? _firstFailure;

        public IEnumerable<double> Latencies => _latencies;
        public long Throttles => Interlocked.Read(ref _throttles);
        public RealAzureWorkloadFirstFailure? FirstFailure => Volatile.Read(ref _firstFailure);

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
                    RealAzureWorkloadFirstFailure.FromException(exception, throttled),
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

internal sealed class RealAzureWorkloadFirstFailure
{
    public string Category { get; init; } = string.Empty;
    public int? StatusCode { get; init; }
    public string ErrorCode { get; init; } = string.Empty;

    public static RealAzureWorkloadFirstFailure FromException(
        Exception exception,
        bool throttled)
    {
        var statusCode = exception switch
        {
            AmazonServiceException serviceException
                when serviceException.StatusCode != default => (int?)serviceException.StatusCode,
            HttpRequestException { StatusCode: { } httpStatus } => (int?)httpStatus,
            _ => null,
        };
        var errorCode = exception is AmazonServiceException { ErrorCode.Length: > 0 } service
            ? service.ErrorCode
            : exception.GetType().Name;
        return new RealAzureWorkloadFirstFailure
        {
            Category = throttled ? "throttle" : CategoryFor(exception),
            StatusCode = statusCode,
            ErrorCode = SafeToken(errorCode),
        };
    }

    public override string ToString()
    {
        var status = StatusCode is int statusCode
            ? statusCode.ToString(CultureInfo.InvariantCulture)
            : "none";
        return $"{Category}/status-{status}/code-{ErrorCode}";
    }

    private static string CategoryFor(Exception exception) => exception switch
    {
        AmazonServiceException => "aws_service",
        OperationCanceledException => "canceled",
        TimeoutException => "timeout",
        HttpRequestException => "http_request",
        _ => "exception",
    };

    private static string SafeToken(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 128)];
        var written = 0;
        foreach (var ch in value)
        {
            if (written == buffer.Length)
            {
                break;
            }
            if (ch is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.'
                or '_'
                or '-')
            {
                buffer[written++] = ch;
            }
        }
        return written == 0 ? "unspecified" : new string(buffer[..written]);
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
    public List<RealAzureCredentialRotationProof> CredentialRotationProofs { get; set; } = new();
    public List<RealAzureRollbackProof> RollbackProofs { get; set; } = new();
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
    public string QualificationMode { get; set; } = string.Empty;
    public SealedRuntimeIdentity? Runtime { get; set; }
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

internal sealed class RealAzureCredentialRotationProof
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string RotationKind { get; set; } = string.Empty;
    public string AuthenticationMode { get; set; } = string.Empty;
    public string BackendKind { get; set; } = string.Empty;
    public string IdentityAClientId { get; set; } = string.Empty;
    public string IdentityAObjectId { get; set; } = string.Empty;
    public string IdentityBClientId { get; set; } = string.Empty;
    public string IdentityBObjectId { get; set; } = string.Empty;
    public string RoleAssignmentAId { get; set; } = string.Empty;
    public string RoleAssignmentBId { get; set; } = string.Empty;
    public string RoleDefinitionId { get; set; } = string.Empty;
    public string RoleScopeDigestA { get; set; } = string.Empty;
    public string RoleScopeDigestB { get; set; } = string.Empty;
    public string FederatedIssuerDigest { get; set; } = string.Empty;
    public string FederatedSubjectDigest { get; set; } = string.Empty;
    public string FederatedAudienceDigest { get; set; } = string.Empty;
    public string RuntimeArtifactDigestA { get; set; } = string.Empty;
    public string RuntimeArtifactDigestB { get; set; } = string.Empty;
    public string CandidateConfigDigestA { get; set; } = string.Empty;
    public string CandidateConfigDigestB { get; set; } = string.Empty;
    public string ProxyConfigDigestA { get; set; } = string.Empty;
    public string ProxyConfigDigestB { get; set; } = string.Empty;
    public string AwsBindingDigestA { get; set; } = string.Empty;
    public string AwsBindingDigestB { get; set; } = string.Empty;
    public string BackendTargetDigestA { get; set; } = string.Empty;
    public string BackendTargetDigestB { get; set; } = string.Empty;
    public long SetupPropagationRetries { get; set; }
    public long FederatedCredentialCompletions { get; set; }
    public long RevocationPolls { get; set; }
    public long GreenReadCompletions { get; set; }
    public long OldAccessDeniedCompletions { get; set; }
    public string OldAccessDeniedErrorCode { get; set; } = string.Empty;
    public int OldAccessDeniedHttpStatus { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset RevocationRequestedAtUtc { get; set; }
    public DateTimeOffset OldAccessDeniedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

internal sealed class RealAzureRollbackProof
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string EvidenceRunId { get; set; } = string.Empty;
    public int EvidenceRunAttempt { get; set; }
    public SealedRuntimeIdentity Candidate { get; set; } = new();
    public SealedRuntimeIdentity Prior { get; set; } = new();
    public string CandidateConfigDigest { get; set; } = string.Empty;
    public string PriorConfigDigest { get; set; } = string.Empty;
    public string CandidateBackendIdentityDigest { get; set; } = string.Empty;
    public string PriorBackendIdentityDigest { get; set; } = string.Empty;
    public string CandidateAwsBindingDigest { get; set; } = string.Empty;
    public string PriorAwsBindingDigest { get; set; } = string.Empty;
    public string CanaryDigest { get; set; } = string.Empty;
    public string CleanupSemantics { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CandidateCreateCompletedAtUtc { get; set; }
    public DateTimeOffset CandidateReadCompletedAtUtc { get; set; }
    public DateTimeOffset CandidateStoppedAtUtc { get; set; }
    public DateTimeOffset PriorStartedAtUtc { get; set; }
    public DateTimeOffset PriorReadCompletedAtUtc { get; set; }
    public DateTimeOffset CleanupRequestedAtUtc { get; set; }
    public DateTimeOffset CleanupVerifiedAtUtc { get; set; }
    public DateTimeOffset CandidateRestoredAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

[JsonSerializable(typeof(RealAzureWorkloadLoadEvidence))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
internal sealed partial class RealAzureWorkloadLoadEvidenceJsonContext : JsonSerializerContext;
