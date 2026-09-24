using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Aws2Azure.TestSupport.OperationalQualification;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed record RcCohortBinding(string Backend, string Config, string AwsBinding);

internal sealed record RcCohortRuntime(
    string CandidateIdentity, string CandidateDigest, string PriorIdentity, string PriorDigest,
    string Endpoint, Func<bool> IsRunning, Func<RcCohortBinding> Binding,
    Func<CancellationToken, Task> RestorePrior);

internal sealed record RcCrudCohortResult(RcObservationCohortCapture Capture, Exception? Failure)
{
    public void ThrowIfFailed()
    {
        if (Failure is not null)
            ExceptionDispatchInfo.Capture(Failure).Throw();
    }
}

internal static class RcCrudCohort
{
    public static async Task RunFromEnvironmentAsync(
        string profile, string service, string backend, string representative, string[] operations,
        RcCohortRuntime runtime, Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> verifyRestored, Func<CancellationToken, Task> cleanup,
        Func<int, RealAzureWorkloadLoadTracker, TimeSpan, Stopwatch, CancellationToken, Task> worker,
        IReadOnlyList<string>? operationSchedule = null)
    {
        var role = RcObservationCaptureWriter.ReadObservationCohortRole()
            ?? throw new InvalidDataException("A split RC cohort role is required.");
        var minutes = RcObservationCaptureWriter.ReadWindowMinutes();
        if (RcObservationCaptureWriter.ReadConcurrency("candidate") != 8
            || RcObservationCaptureWriter.ReadConcurrency("stable") != 8)
            throw new InvalidDataException("CRUD observation requires the reviewed 8/8 workload.");
        var mix = RcObservationCaptureWriter.OperationMixIdentity(profile, operationSchedule ?? operations);
        if (mix != RequiredEnvironment("AWS2AZURE_RC_OBSERVATION_OPERATION_MIX_IDENTITY"))
            throw new InvalidDataException("The observation operation-mix identity differs from policy.");
        var directory = RequiredEnvironment("AWS2AZURE_RC_OBSERVATION_READINESS_DIR");
        var duration = TimeSpan.FromMinutes(minutes);
        using var timeout = new CancellationTokenSource(
            duration + RcObservationReadiness.MaximumWait + TimeSpan.FromMinutes(16));
        var result = await RunAsync(
            profile, service, backend, representative, operations, role,
            RequiredEnvironment("AZURE_LOCATION"), duration, 8, mix, runtime,
            prepare, verifyRestored, cleanup, worker,
            async token =>
            {
                var release = await RcObservationReadiness.WaitAsync(
                    directory, role,
                    role == "candidate" ? runtime.CandidateIdentity : runtime.PriorIdentity,
                    role == "candidate" ? runtime.CandidateDigest : runtime.PriorDigest,
                    runtime.IsRunning, token).ConfigureAwait(false);
                timeout.CancelAfter(duration + TimeSpan.FromMinutes(15));
                RcObservationReadiness.RecordMeasurementStart(
                    directory, role, release, DateTimeOffset.UtcNow);
                return release.ScheduledAtUtc;
            },
            workerIndex => RcObservationCaptureWriter.MemberDigest(profile, role, workerIndex, runtime.Endpoint),
            RcObservationCaptureWriter.CohortId(role),
            timeout.Token).ConfigureAwait(false);

        var path = RequiredEnvironment("AWS2AZURE_RC_OBSERVATION_COHORT_CAPTURE_PATH");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path + ".diagnostics.json", JsonSerializer.Serialize(
            result.Capture, RcObservationCaptureJsonContext.Default.RcObservationCohortCapture))
            .ConfigureAwait(false);
        // Incomplete attempts remain raw diagnostics, never promotable captures.
        if (result.Capture.Metrics.All(metric => metric.Samples > 0)
            && result.Capture.Observation.EndedAtUtc != default
            && (result.Failure is null || result.Capture.Cohort.OperationDiagnostics.Any(row => row.Failures > 0)))
            await RcObservationCaptureWriter.PublishCohortAsync(result.Capture).ConfigureAwait(false);
        result.ThrowIfFailed();
    }

    internal static async Task<RcCrudCohortResult> RunAsync(
        string profile, string service, string backend, string representative, string[] operations,
        string role, string region, TimeSpan duration, int concurrency, string mix,
        RcCohortRuntime runtime, Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> verifyRestored, Func<CancellationToken, Task> cleanup,
        Func<int, RealAzureWorkloadLoadTracker, TimeSpan, Stopwatch, CancellationToken, Task> worker,
        Func<CancellationToken, Task<DateTimeOffset>> ready,
        Func<int, string> memberDigest, string cohortId, CancellationToken token)
    {
        if (role is not ("candidate" or "stable") || concurrency <= 0)
            throw new InvalidDataException("Invalid RC cohort role or concurrency.");
        if (runtime.CandidateDigest == runtime.PriorDigest)
            throw new InvalidDataException("Candidate and prior runtimes must be distinct.");
        var binding = runtime.Binding();
        var tracker = new RealAzureWorkloadLoadTracker(service, operations);
        var started = default(DateTimeOffset);
        var measuredUntil = default(DateTimeOffset);
        var ended = default(DateTimeOffset);
        var stopwatch = new Stopwatch();
        RcObservationCaptureRestoration? restoration = null;
        Exception? failure = null;
        try
        {
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
            preparation.CancelAfter(TimeSpan.FromMinutes(2));
            await prepare(preparation.Token).ConfigureAwait(false);
            RequireBinding();
            started = await ready(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            stopwatch.Start();
            try
            {
                await Task.WhenAll(Enumerable.Range(0, concurrency)
                    .Select(index => worker(index, tracker, duration, stopwatch, token)))
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error;
            }
            stopwatch.Stop();
            measuredUntil = DateTimeOffset.UtcNow;
            token.ThrowIfCancellationRequested();
            RequireBinding();
            if (role == "candidate")
            {
                var restorationStarted = DateTimeOffset.UtcNow;
                await runtime.RestorePrior(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                RequireBinding();
                await verifyRestored(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                RequireBinding();
                restoration = new RcObservationCaptureRestoration
                {
                    Verified = true,
                    RuntimeIdentityDigest = runtime.PriorIdentity,
                    RuntimeDigest = runtime.PriorDigest,
                    BackendIdentityDigest = binding.Backend,
                    ConfigDigest = binding.Config,
                    AwsBindingDigest = binding.AwsBinding,
                    StartedAtUtc = restorationStarted,
                    VerifiedAtUtc = DateTimeOffset.UtcNow,
                };
            }
            ended = DateTimeOffset.UtcNow;
        }
        catch (Exception error)
        {
            failure = failure is null ? error : new AggregateException(failure, error);
        }
        finally
        {
            stopwatch.Stop();
            // Azure resource-group teardown remains the backstop after cancellation.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await cleanup(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var cleanupError = new InvalidDataException("RC canary cleanup failed.", error);
                failure = failure is null ? cleanupError : new AggregateException(failure, cleanupError);
            }
        }
        var measurement = tracker.Snapshot(representative);
        var capture = new RcObservationCohortCapture
        {
            Profile = new() { Id = profile, Version = 1 },
            Azure = new()
            {
                BackendKind = backend, Region = region, BackendIdentityDigest = binding.Backend,
                ConfigDigest = binding.Config, AwsBindingDigest = binding.AwsBinding,
            },
            Observation = new()
            {
                StartedAtUtc = started, MeasurementEndedAtUtc = measuredUntil,
                EndedAtUtc = ended, RequestedWindowMinutes = (int)duration.TotalMinutes,
            },
            LoadShape = new()
            {
                CandidateConcurrency = concurrency, StableConcurrency = concurrency,
                OperationMixIdentity = mix,
            },
            Cohort = new()
            {
                Id = cohortId, Role = role,
                RuntimeIdentityDigest = role == "candidate" ? runtime.CandidateIdentity : runtime.PriorIdentity,
                RuntimeDigest = role == "candidate" ? runtime.CandidateDigest : runtime.PriorDigest,
                BackendKind = backend, Region = region, BackendIdentityDigest = binding.Backend,
                ConfigDigest = binding.Config, AwsBindingDigest = binding.AwsBinding,
                ObservedFromUtc = started, ObservedUntilUtc = ended,
                MemberDigests = Enumerable.Range(0, concurrency).Select(memberDigest).ToList(),
                OperationDiagnostics = RcObservationCaptureWriter.OperationDiagnostics(tracker),
            },
            Metrics =
            [
                new()
                {
                    Id = "representative-load-throughput", Unit = "throughput_per_sec",
                    Value = stopwatch.Elapsed.TotalSeconds > 0
                        ? measurement.Completions / stopwatch.Elapsed.TotalSeconds : 0,
                    Samples = measurement.Completions + measurement.Failures, CapturedAtUtc = measuredUntil,
                },
                new()
                {
                    Id = "operation-failure-rate", Unit = "ratio",
                    Value = RcObservationCaptureWriter.FailureRate(tracker),
                    Samples = RcObservationCaptureWriter.TotalAttempts(tracker), CapturedAtUtc = measuredUntil,
                },
            ],
            Restoration = restoration,
        };
        return new(capture, failure);

        void RequireBinding()
        {
            if (!runtime.IsRunning() || runtime.Binding() != binding)
                throw new InvalidDataException("The sealed process or backend/config/AWS binding changed.");
        }
    }
}
