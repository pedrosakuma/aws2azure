using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.IntegrationTests.S3;
using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class S3Crossover
{
    internal const int Concurrency = 8;
    internal static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MeasurementDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan InventoryTimeout = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan QuietInterval = TimeSpan.FromSeconds(30);
    internal static readonly string[] OperationSchedule =
    [
        "CreateBucket", "PutObject", "HeadObject", "HeadObject",
        "GetObject", "GetObject", "GetObject", "ListObjectsV2",
        "DeleteObject", "DeleteObject", "DeleteBucket",
    ];
    private static readonly SealedRuntimeRole[] Roles =
    [
        SealedRuntimeRole.Prior, SealedRuntimeRole.Candidate,
        SealedRuntimeRole.Candidate, SealedRuntimeRole.Prior,
        SealedRuntimeRole.Prior, SealedRuntimeRole.Prior,
    ];

    internal static async Task RunAsync(
        S3CrossoverReport report,
        Func<SealedRuntimeRole, CancellationToken, Task> restart,
        Func<SealedRuntimeRole, bool, S3CrossoverPhase, CancellationToken, Task> measure,
        Func<CancellationToken, Task<long>> inventory,
        Func<RcCohortBinding> binding,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<S3CrossoverReport, Task> publish,
        CancellationToken token,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        Exception? failure = null;
        try
        {
            report.Stage = "inputs";
            if (!IsDigest(report.CandidateDigest) || !IsDigest(report.PriorDigest)
                || report.CandidateDigest == report.PriorDigest || report.Slots.Count != 0)
                throw new InvalidDataException("Crossover requires two distinct exact sealed digests and a new report.");
            report.Stage = "initial_binding";
            RequireBinding();
            for (var index = 0; index < Roles.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var role = Roles[index];
                var slot = new S3CrossoverSlot
                {
                    Index = index,
                    Role = role == SealedRuntimeRole.Candidate ? "candidate" : "prior",
                    RuntimeDigest = role == SealedRuntimeRole.Candidate
                        ? report.CandidateDigest : report.PriorDigest,
                };
                report.Slots.Add(slot);
                report.Stage = "runtime_restart";
                await restart(role, token).ConfigureAwait(false);
                report.Stage = "runtime_binding";
                RequireBinding();
                await PhaseAsync(slot.Warmup, true).ConfigureAwait(false);
                await PhaseAsync(slot.Measurement, false).ConfigureAwait(false);
                slot.Completed = true;
                report.Stage = "publish";
                await publish(report).ConfigureAwait(false);

                async Task PhaseAsync(S3CrossoverPhase phase, bool warmup)
                {
                    var prefix = warmup ? "warmup" : "measurement";
                    report.Stage = prefix + ".inventory_before";
                    phase.Before = await ReadInventoryAsync(token).ConfigureAwait(false);
                    RequireEmpty(phase.Before.Value);
                    report.Stage = prefix + ".quiet";
                    var quiet = clock.GetTimestamp();
                    await delay(QuietInterval, token).ConfigureAwait(false);
                    phase.QuietSeconds = clock.GetElapsedTime(quiet).TotalSeconds;
                    report.Stage = prefix + ".inventory_after_quiet";
                    phase.AfterQuiet = await ReadInventoryAsync(token).ConfigureAwait(false);
                    RequireEmpty(phase.AfterQuiet.Value);
                    report.Stage = prefix + ".binding_before";
                    RequireBinding();
                    report.Stage = prefix + ".workload";
                    await measure(role, warmup, phase, token).ConfigureAwait(false);
                    report.Stage = prefix + ".validation";
                    ValidatePhase(phase, slot.RuntimeDigest, slot.Role,
                        warmup ? WarmupDuration : MeasurementDuration);
                    report.Stage = prefix + ".binding_after";
                    RequireBinding();
                    var started = clock.GetTimestamp();
                    try
                    {
                        // A separate token from the measured phase's drain deadline.
                        report.Stage = prefix + ".inventory_after";
                        phase.After = await ReadInventoryAsync(token).ConfigureAwait(false);
                        RequireEmpty(phase.After.Value);
                    }
                    finally
                    {
                        phase.InventorySeconds = clock.GetElapsedTime(started).TotalSeconds;
                    }
                    report.Stage = prefix + ".binding_final";
                    RequireBinding();
                    phase.Completed = true;
                    report.Stage = "publish";
                    await publish(report).ConfigureAwait(false);
                }
            }
            report.Stage = "comparison";
            report.Comparison = Compare(report.Slots);
            report.Completed = true;
            report.Stage = "completed";
        }
        catch (Exception exception)
        {
            failure = exception;
            report.Completed = false;
            report.Comparison = null;
            report.Failure = RealAzureWorkloadFirstFailure.FromException(exception, false);
            throw;
        }
        finally
        {
            report.EndedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                await publish(report).ConfigureAwait(false);
            }
            catch (Exception publishing) when (failure is not null)
            {
                throw new AggregateException("Crossover and publication failed.", failure, publishing);
            }
        }

        void RequireBinding()
        {
            token.ThrowIfCancellationRequested();
            if (binding() != report.Binding)
                throw new InvalidDataException("Crossover backend, configuration or AWS binding changed.");
        }

        async Task<long> ReadInventoryAsync(CancellationToken cancellation)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(InventoryTimeout);
            var count = await inventory(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return count;
        }
    }

    private static void RequireEmpty(long containers)
    {
        if (containers != 0)
            throw new InvalidDataException("Crossover requires an empty dedicated storage account.");
    }

    private static bool IsDigest(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static void ValidatePhase(S3CrossoverPhase phase, string digest, string role, TimeSpan duration)
    {
        var timing = phase.Timing ?? throw new InvalidDataException("Missing crossover timing.");
        if (!timing.WorkersCompleted || timing.Promotable || timing.Concurrency != Concurrency
            || timing.RuntimeDigest != digest || timing.Role != role || timing.Service != "s3"
            || timing.Workload != "s3-basic-object-crud"
            || !timing.OperationSchedule.SequenceEqual(OperationSchedule)
            || timing.RequestedDurationSeconds != duration.TotalSeconds
            || !double.IsFinite(timing.ElapsedSeconds) || timing.ElapsedSeconds < duration.TotalSeconds
            || timing.ElapsedSeconds > duration.TotalSeconds + DrainTimeout.TotalSeconds
            || phase.CompletedIterations <= 0 || phase.StartedIterations != phase.CompletedIterations)
            throw new InvalidDataException("Incomplete, mismatched or timed-out crossover phase.");
        var rows = timing.Operations.Where(row => row.Phase == "lifecycle").ToArray();
        if (rows.Length != S3RealAzureRcObservationTests.Operations.Length
            || timing.Operations.Any(row => row.Errors != 0 || row.Throttles != 0
                || (row.Phase == "cleanup" && row.Attempts != 0)))
            throw new InvalidDataException("Crossover contains errors, throttles or repair cleanup.");
        foreach (var operation in S3RealAzureRcObservationTests.Operations)
        {
            var expected = operation switch
            {
                "CreateBucket" or "DeleteBucket" => Concurrency,
                "HeadObject" or "DeleteObject" => checked(2 * phase.CompletedIterations),
                "GetObject" => checked(3 * phase.CompletedIterations),
                _ => phase.CompletedIterations,
            };
            var matches = rows.Where(row => row.Operation == operation).ToArray();
            if (matches.Length != 1 || matches[0].Successes != expected || matches[0].Attempts != expected)
                throw new InvalidDataException("Crossover lifecycle counts are inconsistent.");
        }
        phase.GetObjectPerSecond = 3.0 * phase.CompletedIterations / timing.ElapsedSeconds;
    }

    internal static S3CrossoverComparison Compare(IReadOnlyList<S3CrossoverSlot> slots)
    {
        var prior = Rate(0, 3);
        var candidate = Rate(1, 2);
        return new()
        {
            PriorRate = prior,
            CandidateRate = candidate,
            ForwardRatio = slots[1].Measurement.GetObjectPerSecond / slots[0].Measurement.GetObjectPerSecond,
            ReverseRatio = slots[2].Measurement.GetObjectPerSecond / slots[3].Measurement.GetObjectPerSecond,
            PooledRatio = candidate / prior,
            ControlRatio = slots[5].Measurement.GetObjectPerSecond / slots[4].Measurement.GetObjectPerSecond,
        };

        double Rate(int first, int second) =>
            3.0 * (slots[first].Measurement.CompletedIterations + slots[second].Measurement.CompletedIterations)
            / (slots[first].Measurement.Timing!.ElapsedSeconds + slots[second].Measurement.Timing!.ElapsedSeconds);
    }
}

internal sealed class S3CrossoverReport
{
    public int SchemaVersion => 1;
    public string ArtifactKind => "s3_controlled_crossover_diagnostics";
    public bool Promotable => false;
    public string Profile => "s3-basic-object-crud";
    public int Concurrency => S3Crossover.Concurrency;
    public string MetricScope => "logical_GetObject_full_range_and_expected_304_not_physical_Blob_requests";
    public string RetryScope => "SDK_MaxErrorRetry_2_upstream_retry_counts_unknown";
    public string NetworkProbeScope => "not_measured";
    public string BackendLatencyScope => "not_measured";
    public string RunnerRegionSource => "Actions_setup_log_not_inferred_from_backend";
    public string RunId { get; set; } = "";
    public string RunAttempt { get; set; } = "";
    public string HarnessSourceSha { get; set; } = "";
    public string CandidateDigest { get; set; } = "";
    public string PriorDigest { get; set; } = "";
    public string CandidateIdentityDigest { get; set; } = "";
    public string PriorIdentityDigest { get; set; } = "";
    public string BackendRegion { get; set; } = "";
    public RcCohortBinding Binding { get; set; } = new("", "", "");
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public bool Completed { get; set; }
    public string Stage { get; set; } = "initializing";
    public RealAzureWorkloadFirstFailure? Failure { get; set; }
    public S3CrossoverComparison? Comparison { get; set; }
    public List<S3CrossoverSlot> Slots { get; set; } = [];
}

internal sealed class S3CrossoverSlot
{
    public int Index { get; set; }
    public string Comparison => Index < 4 ? "ABBA" : "AA";
    public string Role { get; set; } = "";
    public string RuntimeDigest { get; set; } = "";
    public bool Completed { get; set; }
    public S3CrossoverPhase Warmup { get; set; } = new();
    public S3CrossoverPhase Measurement { get; set; } = new();
}

internal sealed class S3CrossoverPhase
{
    public long? Before { get; set; }
    public long? AfterQuiet { get; set; }
    public long? After { get; set; }
    public double QuietSeconds { get; set; }
    public double InventorySeconds { get; set; }
    public long StartedIterations { get; set; }
    public long CompletedIterations { get; set; }
    public double GetObjectPerSecond { get; set; }
    public OperationTimingReport? Timing { get; set; }
    public bool Completed { get; set; }
}

internal sealed class S3CrossoverComparison
{
    public double PriorRate { get; set; }
    public double CandidateRate { get; set; }
    public double ForwardRatio { get; set; }
    public double ReverseRatio { get; set; }
    public double PooledRatio { get; set; }
    public double ControlRatio { get; set; }
}

[JsonSerializable(typeof(S3CrossoverReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class S3CrossoverJsonContext : JsonSerializerContext;
