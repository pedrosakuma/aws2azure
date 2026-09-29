using System.Diagnostics;
using System.Text.Json;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.DynamoDb;

[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbControlledCrossover")]
[Collection(DynamoDbRealAzureLoadCollection.Name)]
public sealed class DynamoDbRealAzureCrossoverTests(DynamoDbRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Exact_sealed_ABBA_and_AA_share_one_runner_and_backend()
    {
        var output = Environment.GetEnvironmentVariable("AWS2AZURE_CROSSOVER_REPORT_PATH");
        Skip.If(string.IsNullOrWhiteSpace(output), "No controlled crossover requested.");
        Assert.True(fixture.CosmosConfigured && fixture.SealedRollbackConfigured,
            "Crossover requires a real Cosmos backend and both verified sealed runtimes.");
        Assert.Equal(RequiredEnvironment("AWS2AZURE_CROSSOVER_CANDIDATE_DIGEST"),
            fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest);
        Assert.Equal(RequiredEnvironment("AWS2AZURE_CROSSOVER_PRIOR_DIGEST"),
            fixture.PriorRuntimeIdentity.Runtime.AggregateDigest);
        var path = ResolveOutputPath(output!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            throw new InvalidDataException("Crossover output already exists; never overwrite a diagnostic attempt.");
        var report = new DynamoDbCrossoverReport
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        await DynamoDbCrossover.RunAsync(report,
            async (role, token) =>
            {
                token.ThrowIfCancellationRequested();
                await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                await fixture.StartRuntimeAsync(role).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            },
            async (index, role, warmup, duration, token) =>
            {
                using var client = fixture.CreateDynamoDbClient();
                var tracker = new RealAzureWorkloadLoadTracker(
                    "dynamodb", DynamoDbRealAzureLoadQualificationTests.Operations);
                var iterations = new CompletedIterationCounter();
                var clock = Stopwatch.StartNew();
                var timing = new OperationTimingDiagnostics(
                    DynamoDbRealAzureLoadQualificationTests.Operations, clock, DateTimeOffset.UtcNow,
                    duration, DynamoDbCrossover.Concurrency,
                    role == SealedRuntimeRole.Candidate ? "candidate" : "stable",
                    role == SealedRuntimeRole.Candidate ? report.CandidateDigest : report.PriorDigest,
                    service: "dynamodb", workload: report.Profile,
                    operationSchedule: DynamoDbRealAzureLoadQualificationTests.LifecycleOperationSchedule);
                tracker.TimingDiagnostics = timing;
                var completed = false;
                using var phaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                phaseTimeout.CancelAfter(duration + TimeSpan.FromMinutes(1));
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, DynamoDbCrossover.Concurrency)
                        .Select(worker => DynamoDbRealAzureLoadQualificationTests.RunWorkerAsync(
                            client, tracker, iterations, worker, duration, clock,
                            phaseTimeout.Token, strictObservation: true))).ConfigureAwait(false);
                    completed = true;
                }
                finally
                {
                    clock.Stop();
                    var phase = warmup ? "warmup" : "measurement";
                    var snapshot = timing.Snapshot(clock.Elapsed.TotalSeconds, completed);
                    snapshot.Warmup = warmup ? "this_is_warmup" : "separate_30_second_phase_completed";
                    await timing.PublishAsync(
                        Path.Combine(Path.GetDirectoryName(path)!, $"slot-{index:D2}-{phase}.json"),
                        completed).ConfigureAwait(false);
                }
                var result = timing.Snapshot(clock.Elapsed.TotalSeconds, completed);
                if (result.Operations.Any(row => row.Phase == "lifecycle" && row.Errors > 0)
                    || tracker.Snapshot("GetItem").Completions == 0)
                    throw new InvalidDataException("Crossover phase has failures or no representative samples.");
                return result;
            },
            token => UnauthenticatedCosmosConnectivityProbe.MeasureHeaderLatenciesAsync(
                new Uri(new Uri(fixture.CosmosEndpoint), "/"), 12, token),
            ReadBinding,
            async value =>
            {
                await File.WriteAllTextAsync(path + ".pending", JsonSerializer.Serialize(
                    value, DynamoDbCrossoverJsonContext.Default.DynamoDbCrossoverReport))
                    .ConfigureAwait(false);
                File.Move(path + ".pending", path, overwrite: true);
            },
            deadline.Token).ConfigureAwait(false);

        RcCohortBinding ReadBinding()
        {
            fixture.VerifyConfigurationUnchanged();
            if (!fixture.IsProxyRunning)
                throw new InvalidDataException("The sealed crossover proxy is not running.");
            return new(fixture.BackendIdentityDigest, fixture.ProxyConfigDigest, fixture.AwsBindingDigest);
        }
    }
}
