using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.IntegrationTests.SecretsManager;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class SecretsManagerConcurrencyDiagnostic
{
    internal static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MeasurementDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan BarrierTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan QuietInterval = TimeSpan.FromMinutes(1);
    private static readonly int[] Concurrencies = [5, 8, 8, 5, 5, 5];

    internal static async Task RunAsync(
        SecretsConcurrencyReport report,
        Func<CancellationToken, Task> restart,
        Func<int, int, bool, SecretsConcurrencyPhase, CancellationToken, Task> measure,
        Func<CancellationToken, Task<VaultInventory>> inventory,
        Func<CancellationToken, Task<double[]>> probe,
        Func<RcCohortBinding> binding,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<SecretsConcurrencyReport, Task> publish,
        CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            RequireBinding();
            report.InitialInventory = await inventory(token).ConfigureAwait(false);
            RequireEmpty(report.InitialInventory);
            for (var index = 0; index < Concurrencies.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var slot = new SecretsConcurrencySlot { Index = index, Concurrency = Concurrencies[index] };
                report.Slots.Add(slot);
                RequireBinding();
                await restart(token).ConfigureAwait(false);
                RequireBinding();
                slot.NetworkBeforeMilliseconds = await probe(token).ConfigureAwait(false);
                await PhaseAsync(slot.Warmup, true).ConfigureAwait(false);
                await PhaseAsync(slot.Measurement, false).ConfigureAwait(false);
                slot.NetworkAfterMilliseconds = await probe(token).ConfigureAwait(false);
                RequireBinding();
                slot.Completed = true;
                await publish(report).ConfigureAwait(false);

                async Task PhaseAsync(SecretsConcurrencyPhase phase, bool warmup)
                {
                    phase.Before = await inventory(token).ConfigureAwait(false);
                    RequireEmpty(phase.Before);
                    var quiet = Stopwatch.StartNew();
                    await delay(QuietInterval, token).ConfigureAwait(false);
                    phase.QuietSeconds = quiet.Elapsed.TotalSeconds;
                    phase.AfterQuiet = await inventory(token).ConfigureAwait(false);
                    RequireEmpty(phase.AfterQuiet);
                    RequireBinding();
                    await measure(index, slot.Concurrency, warmup, phase, token).ConfigureAwait(false);
                    ValidatePhase(phase, slot.Concurrency, report.CandidateDigest,
                        warmup ? WarmupDuration : MeasurementDuration);
                    RequireBinding();
                    await WaitForEmptyAsync(phase, inventory, delay, token).ConfigureAwait(false);
                    RequireBinding();
                    phase.Completed = true;
                    await publish(report).ConfigureAwait(false);
                }
            }
            report.Comparison = Compare(report.Slots);
            report.Completed = true;
        }
        catch (Exception exception)
        {
            failure = exception;
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
            catch (Exception reportingFailure) when (failure is not null)
            {
                throw new AggregateException("Diagnostic and publication failed.", failure, reportingFailure);
            }
        }

        void RequireBinding()
        {
            token.ThrowIfCancellationRequested();
            if (binding() != report.Binding)
                throw new InvalidDataException("Diagnostic backend, configuration or AWS binding changed.");
        }
    }

    internal static async Task WaitForEmptyAsync(
        SecretsConcurrencyPhase phase,
        Func<CancellationToken, Task<VaultInventory>> inventory,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(BarrierTimeout);
        var clock = Stopwatch.StartNew();
        try
        {
            // The count also bounds a fake-clock/offline execution; the CTS bounds slow HTTP.
            for (var poll = 0; poll <= 24; poll++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                phase.After = await inventory(deadline.Token).ConfigureAwait(false);
                phase.BarrierPolls++;
                if (phase.After.Active == 0 && phase.After.Deleted == 0)
                    return;
                if (poll < 24)
                    await delay(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false);
            }
            throw new TimeoutException("Key Vault did not return to its empty baseline.");
        }
        finally
        {
            phase.BarrierSeconds = clock.Elapsed.TotalSeconds;
        }
    }

    private static void RequireEmpty(VaultInventory inventory)
    {
        if (inventory.Active != 0 || inventory.Deleted != 0)
            throw new InvalidDataException("Diagnostic requires an empty dedicated Key Vault.");
    }

    internal static void ValidatePhase(
        SecretsConcurrencyPhase phase, int concurrency, string digest, TimeSpan duration)
    {
        var timing = phase.Timing
            ?? throw new InvalidDataException("Missing diagnostic phase timing.");
        if (!timing.WorkersCompleted || timing.Concurrency != concurrency || timing.RuntimeDigest != digest
            || timing.RequestedDurationSeconds != duration.TotalSeconds
            || !double.IsFinite(timing.ElapsedSeconds) || timing.ElapsedSeconds < duration.TotalSeconds
            || timing.ElapsedSeconds > duration.TotalSeconds + BarrierTimeout.TotalSeconds)
            throw new InvalidDataException("Incomplete, mismatched or timed-out diagnostic phase.");
        var rows = timing.Operations.Where(row => row.Phase == "lifecycle").ToArray();
        if (rows.Length != SecretsManagerRealAzureRcObservationTests.Operations.Length
            || timing.Operations.Any(row => row.Errors != 0 || row.Throttles != 0
                || (row.Phase == "cleanup" && row.Attempts != 0)))
            throw new InvalidDataException("Diagnostic phase contains errors, throttles or repair cleanup.");
        var deletes = rows.SingleOrDefault(row => row.Operation == "DeleteSecret")?.Successes ?? 0;
        if (deletes <= 0)
            throw new InvalidDataException("Diagnostic completed no full lifecycles.");
        foreach (var operation in SecretsManagerRealAzureRcObservationTests.Operations)
        {
            var row = rows.SingleOrDefault(item => item.Operation == operation);
            var expected = operation == "GetSecretValue" ? checked(3 * deletes) : deletes;
            if (row is null || row.Successes != expected || row.Attempts != expected)
                throw new InvalidDataException("Diagnostic lifecycle operation ratios are inconsistent.");
        }
        phase.CompletedIterations = deletes;
        phase.GetSecretValuePerSecond = 3 * deletes / timing.ElapsedSeconds;
    }

    internal static SecretsConcurrencyComparison Compare(IReadOnlyList<SecretsConcurrencySlot> slots)
    {
        var fiveCount = slots[0].Measurement.CompletedIterations + slots[3].Measurement.CompletedIterations;
        var eightCount = slots[1].Measurement.CompletedIterations + slots[2].Measurement.CompletedIterations;
        var fiveRate = 3.0 * fiveCount /
            (slots[0].Measurement.Timing!.ElapsedSeconds + slots[3].Measurement.Timing!.ElapsedSeconds);
        var eightRate = 3.0 * eightCount /
            (slots[1].Measurement.Timing!.ElapsedSeconds + slots[2].Measurement.Timing!.ElapsedSeconds);
        return new()
        {
            FiveWorkerRate = fiveRate,
            EightWorkerRate = eightRate,
            ForwardRatio = slots[1].Measurement.GetSecretValuePerSecond / slots[0].Measurement.GetSecretValuePerSecond,
            ReverseRatio = slots[2].Measurement.GetSecretValuePerSecond / slots[3].Measurement.GetSecretValuePerSecond,
            PooledRatio = eightRate / fiveRate,
            ScalingEfficiency = eightRate / fiveRate / (8.0 / 5),
            FiveWorkerPerWorkerRate = fiveRate / 5,
            EightWorkerPerWorkerRate = eightRate / 8,
            ControlRatio = slots[5].Measurement.GetSecretValuePerSecond / slots[4].Measurement.GetSecretValuePerSecond,
        };
    }
}

internal sealed record VaultInventory(long Active, long Deleted);

internal sealed class SecretsConcurrencyReport
{
    public int SchemaVersion => 1;
    public string ArtifactKind => "secretsmanager_concurrency_diagnostics";
    public bool Promotable => false;
    public string Profile => "secretsmanager-basic-lifecycle";
    public string MetricScope => "logical_GetSecretValue_calls_including_consistency_polling_not_physical_requests";
    public string NetworkProbeScope => "anonymous_KeyVault_response_headers_including_connection_setup_not_pure_RTT";
    public string RunnerRegionSource => "Actions_setup_log_not_inferred_from_backend";
    public string RetryScope => "SDK_MaxErrorRetry_2_upstream_retry_counts_unknown";
    public string RunId { get; set; } = "";
    public string RunAttempt { get; set; } = "";
    public string HarnessSourceSha { get; set; } = "";
    public string CandidateDigest { get; set; } = "";
    public string CandidateIdentityDigest { get; set; } = "";
    public string BackendRegion { get; set; } = "";
    public RcCohortBinding Binding { get; set; } = new("", "", "");
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public VaultInventory? InitialInventory { get; set; }
    public List<SecretsConcurrencySlot> Slots { get; set; } = [];
    public SecretsConcurrencyComparison? Comparison { get; set; }
    public bool Completed { get; set; }
    public RealAzureWorkloadFirstFailure? Failure { get; set; }
    public List<DateTimeOffset> OidcRefreshesAtUtc { get; set; } = [];
}

internal sealed class SecretsConcurrencySlot
{
    public int Index { get; set; }
    public string Comparison => Index < 4 ? "5_8_8_5" : "5_5_control";
    public int Concurrency { get; set; }
    public bool Completed { get; set; }
    public double[] NetworkBeforeMilliseconds { get; set; } = [];
    public double[] NetworkAfterMilliseconds { get; set; } = [];
    public SecretsConcurrencyPhase Warmup { get; set; } = new();
    public SecretsConcurrencyPhase Measurement { get; set; } = new();
}

internal sealed class SecretsConcurrencyPhase
{
    public VaultInventory? Before { get; set; }
    public VaultInventory? AfterQuiet { get; set; }
    public VaultInventory? After { get; set; }
    public double QuietSeconds { get; set; }
    public int BarrierPolls { get; set; }
    public double BarrierSeconds { get; set; }
    public OperationTimingReport? Timing { get; set; }
    public long CompletedIterations { get; set; }
    public double GetSecretValuePerSecond { get; set; }
    public bool Completed { get; set; }
}

internal sealed class SecretsConcurrencyComparison
{
    public double FiveWorkerRate { get; set; }
    public double EightWorkerRate { get; set; }
    public double ForwardRatio { get; set; }
    public double ReverseRatio { get; set; }
    public double PooledRatio { get; set; }
    public double ScalingEfficiency { get; set; }
    public double FiveWorkerPerWorkerRate { get; set; }
    public double EightWorkerPerWorkerRate { get; set; }
    public double ControlRatio { get; set; }
}

[JsonSerializable(typeof(SecretsConcurrencyReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class SecretsConcurrencyJsonContext : JsonSerializerContext;
