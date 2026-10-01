using System.Diagnostics;
using System.Text.Json;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.S3;

[Trait("Category", "RealAzure")]
[Trait("Category", "S3ControlledCrossover")]
[Collection(RealAzureCollection.Name)]
public sealed class S3RealAzureCrossoverTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Exact_sealed_ABBA_and_AA_share_one_runner_and_backend()
    {
        var output = Environment.GetEnvironmentVariable("AWS2AZURE_S3_CROSSOVER_REPORT_PATH");
        Skip.If(string.IsNullOrWhiteSpace(output), "No controlled S3 crossover requested.");
        Assert.True(fixture.BlobConfigured && fixture.SealedRollbackConfigured,
            "Crossover requires a dedicated Blob backend and both verified sealed runtimes.");
        Assert.Equal(RequiredEnvironment("AWS2AZURE_S3_CROSSOVER_CANDIDATE_DIGEST"),
            fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest);
        Assert.Equal(RequiredEnvironment("AWS2AZURE_S3_CROSSOVER_PRIOR_DIGEST"),
            fixture.PriorRuntimeIdentity.Runtime.AggregateDigest);
        var path = ResolveOutputPath(output!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            throw new InvalidDataException("Crossover output already exists.");
        var report = new S3CrossoverReport
        {
            RunId = RequiredEnvironment("GITHUB_RUN_ID"),
            RunAttempt = RequiredEnvironment("GITHUB_RUN_ATTEMPT"),
            HarnessSourceSha = RequiredEnvironment("GITHUB_SHA"),
            BackendRegion = RequiredEnvironment("AZURE_LOCATION"),
            CandidateDigest = fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
            PriorDigest = fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
            CandidateIdentityDigest = fixture.CandidateRuntimeIdentityDigest,
            PriorIdentityDigest = fixture.PriorRuntimeIdentityDigest,
            Binding = ReadBinding(),
        };
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        var inventory = new DiagnosticBlobInventory(http, new Uri(RequiredEnvironment("AZURE_BLOB_ENDPOINT")),
            fixture.StorageAccountName, fixture.StorageAccountKey);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        await S3Crossover.RunAsync(report,
            async (role, token) =>
            {
                await inventory.VerifySettingsAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                await fixture.StartRuntimeAsync(role).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            },
            async (role, warmup, phase, token) =>
            {
                using var client = fixture.CreateS3Client(maxErrorRetry: 2);
                var duration = warmup ? S3Crossover.WarmupDuration : S3Crossover.MeasurementDuration;
                var tracker = new RealAzureWorkloadLoadTracker("s3", S3RealAzureRcObservationTests.Operations);
                var iterations = new CompletedIterationCounter();
                var clock = Stopwatch.StartNew();
                var timing = new OperationTimingDiagnostics(
                    S3RealAzureRcObservationTests.Operations, clock, DateTimeOffset.UtcNow,
                    duration, S3Crossover.Concurrency, role == SealedRuntimeRole.Candidate ? "candidate" : "prior",
                    role == SealedRuntimeRole.Candidate ? report.CandidateDigest : report.PriorDigest,
                    service: "s3", workload: report.Profile, operationSchedule: S3Crossover.OperationSchedule);
                tracker.TimingDiagnostics = timing;
                using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                phaseDeadline.CancelAfter(duration + S3Crossover.DrainTimeout);
                var completed = false;
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, S3Crossover.Concurrency).Select(async worker =>
                    {
                        try
                        {
                            // The payload label is identical for both runtime roles.
                            await S3RealAzureRcObservationTests.RunWorkerAsync(client, tracker, "diagnostic",
                                worker, duration, clock, phaseDeadline.Token,
                                strictDiagnostic: true, completedIterations: iterations).ConfigureAwait(false);
                        }
                        catch
                        {
                            await phaseDeadline.CancelAsync().ConfigureAwait(false);
                            throw;
                        }
                    })).ConfigureAwait(false);
                    completed = true;
                }
                finally
                {
                    clock.Stop();
                    phase.StartedIterations = iterations.StartedCount;
                    phase.CompletedIterations = iterations.Count;
                    phase.Timing = timing.Snapshot(clock.Elapsed.TotalSeconds, completed);
                    phase.Timing.Warmup = warmup ? "this_is_warmup" : "separate_30_second_phase_completed";
                }
            },
            inventory.CountAsync, ReadBinding, Task.Delay, PublishAsync, deadline.Token).ConfigureAwait(false);

        RcCohortBinding ReadBinding()
        {
            fixture.VerifyConfigurationUnchanged();
            if (!fixture.IsProxyRunning)
                throw new InvalidDataException("The sealed crossover proxy is not running.");
            return new(fixture.BackendIdentityDigest, fixture.ProxyConfigDigest, fixture.AwsBindingDigest);
        }

        async Task PublishAsync(S3CrossoverReport value)
        {
            await File.WriteAllTextAsync(path + ".pending",
                JsonSerializer.Serialize(value, S3CrossoverJsonContext.Default.S3CrossoverReport)).ConfigureAwait(false);
            File.Move(path + ".pending", path, overwrite: true);
        }
    }
}
