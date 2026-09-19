using System.Diagnostics;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.PerfTests.SecretsManager;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SecretsCapacityCollection
{
    public const string Name = "SecretsManager isolated real-Azure capacity";
}

[Collection(SecretsCapacityCollection.Name)]
[Trait("Category", "SecretsManagerCapacity")]
[Trait("Category", "RealAzure")]
public sealed class SecretsManagerCapacityTests
{
    private const string Value = "capacity-not-a-secret";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [SkippableFact]
    public async Task Selected_operation_runs_bounded_isolated_sweep()
    {
        Skip.If(!SecretsCapacityPlan.Enabled(Environment.GetEnvironmentVariable("AWS2AZURE_SECRETS_CAPACITY")),
            "Dedicated SecretsManager capacity opt-in is absent; no fixture or Azure requests started.");
        var scenario = SecretsCapacityPlan.Select(Environment.GetEnvironmentVariable("AWS2AZURE_SECRETS_CAPACITY_SCENARIO"));
        if (SecretsCapacityBackend.Required("AWS2AZURE_SECRETS_CAPACITY_EXCLUSIVE_VAULT") != "1")
            throw new InvalidOperationException("An exclusive disposable vault lease must be explicitly acknowledged.");
        var topology = SecretsCapacityBackend.Required("AWS2AZURE_SECRETS_CAPACITY_TOPOLOGY");
        var source = SecretsCapacityBackend.Required("AWS2AZURE_LOAD_GIT_SHA");
        if (Environment.GetEnvironmentVariable("AWS2AZURE_SEALED_RUNTIME_MODE") != "candidate"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS2AZURE_RC_OBSERVATION_COHORT_ROLE")))
            throw new InvalidOperationException("Use sealed candidate mode without an RC cohort override; the measured binary must match the recorded candidate.");
        _ = SealedRuntimeSelection.Load("secretsmanager-basic-lifecycle", 1);
        var directory = Path.GetFullPath(SecretsCapacityBackend.Required("AWS2AZURE_SECRETS_CAPACITY_OUTPUT"));
        Directory.CreateDirectory(directory);
        var run = Guid.NewGuid().ToString("N");
        var results = new List<PerfResult>();
        foreach (var concurrency in SecretsCapacityPlan.Concurrency)
        {
            results.Add(await RunLevelAsync(scenario, concurrency, directory, run, topology, source));
        }
        // Use the existing sweep analysis, but not its shared warmup/action:
        // fresh state, per-level warmup, and verified cleanup are required here.
        var knee = PerfSweep.DetectKnee(results.Select(r => (r.Concurrency, r.ThroughputPerSec, r.P99Us / 1000.0)).ToArray());
        await WriteNewAsync(Path.Combine(directory, $"{run}.sweep.json"), new
        {
            scenario = scenario.Name, reportOnly = true, promotable = false,
            interpretation = "Exploratory finite-window client-action curve; not proof of CPU saturation or a calibrated gate.",
            knee, levels = results.Select(r => new
            {
                r.Concurrency, r.Completed, r.Failures, r.Throttled, r.ElapsedSeconds,
                r.ThroughputPerSec, r.P50Us, r.P95Us, r.P99Us,
            }),
        });
    }

    private static async Task<PerfResult> RunLevelAsync(
        SecretsCapacityScenario scenario, int concurrency, string directory, string run, string topology, string source)
    {
        var stem = Path.Combine(directory, $"{run}.{scenario.Id}.c{concurrency}");
        var inventory = new SecretsCapacityInventory(scenario, concurrency, $"cap-{run[..12]}-c{concurrency}");
        // Persist ownership before the first possible mutation, including ambiguous timeouts.
        await WriteNewAsync(stem + ".ownership.json", new { names = inventory.Names, scenario = scenario.Name, concurrency });
        var fixture = new SecretsManagerRealAzureProxyFixture();
        PerfResult? result = null;
        OperationTimingReport? timingReport = null;
        var versions = new string[inventory.Names.Length];
        var setupRequests = 0;
        var warmupRequests = 0;
        var cleanupComplete = false;
        var resourcesMayExist = false;
        var workersCompleted = false;
        var measurementSeconds = 0.0;
        var cleanupSeconds = 0.0;
        var quietSeconds = 0.0;
        string? runtimeDigest = null;
        string? configDigest = null;
        string? backendDigest = null;
        Exception? failure = null;
        using var setupDeadline = new CancellationTokenSource(SecretsCapacityPlan.PhaseTimeout);
        using var backend = await SecretsCapacityBackend.ConnectAsync(setupDeadline.Token);
        await backend.AssertEmptyAsync(setupDeadline.Token);
        try
        {
            await fixture.InitializeAsync();
            if (!fixture.Configured)
                throw new InvalidOperationException(fixture.SkipReason);
            if (!fixture.SealedCandidateConfigured)
                throw new InvalidOperationException("Capacity evidence requires a verified sealed runtime; source fallback is not accepted.");
            runtimeDigest = fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest;
            configDigest = fixture.ProxyConfigDigest;
            backendDigest = fixture.BackendIdentityDigest;
            using var client = fixture.CreateSecretsManagerClient(maxErrorRetry: 0);

            if (scenario.Operation != "CreateSecret")
            {
                resourcesMayExist = true;
                for (var i = 0; i < inventory.Names.Length; i++)
                {
                    setupRequests++;
                    var created = await client.CreateSecretAsync(new CreateSecretRequest
                    {
                        Name = inventory.Names[i], SecretString = Value,
                        ClientRequestToken = Guid.NewGuid().ToString("N"),
                    }, setupDeadline.Token);
                    versions[i] = created.VersionId;
                }
            }
            string? nextToken = null;
            if (scenario.Operation == "ListSecrets")
            {
                setupRequests++;
                var page = await client.ListSecretsAsync(new ListSecretsRequest { MaxResults = 16 }, setupDeadline.Token);
                nextToken = page.NextToken;
                if (page.SecretList.Count != 16 || string.IsNullOrEmpty(nextToken))
                    throw new InvalidOperationException("LIST preflight requires 32 visible secrets and two fixed 16-entry pages.");
                setupRequests++;
                var second = await client.ListSecretsAsync(new ListSecretsRequest { MaxResults = 16, NextToken = nextToken }, setupDeadline.Token);
                if (second.SecretList.Count != 16 || !string.IsNullOrEmpty(second.NextToken)
                    || !page.SecretList.Concat(second.SecretList).Select(s => s.Name).Order()
                        .SequenceEqual(inventory.Names.Order()))
                    throw new InvalidOperationException("LIST inventory or pagination differs from the controlled 32-secret fixture.");
            }

            async Task Action(int worker, CancellationToken token)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(SecretsCapacityPlan.RequestTimeout);
                await InvokeAsync(client, scenario, inventory, versions, nextToken, worker, deadline.Token);
            }

            // Fixed successful actions, not PerfRunner's time-based, failure-swallowing warmup.
            resourcesMayExist = true;
            for (var i = 0; i < SecretsCapacityPlan.WarmupAttempts; i++)
            {
                warmupRequests++;
                await Action(i % concurrency, setupDeadline.Token);
            }
            if (scenario.Operation == "DeleteSecret")
            {
                await Task.Delay(SecretsCapacityPlan.PurgeQuietInterval, setupDeadline.Token);
                quietSeconds += SecretsCapacityPlan.PurgeQuietInterval.TotalSeconds;
            }

            using var measurementDeadline = new CancellationTokenSource(
                SecretsCapacityPlan.MeasurementDuration + SecretsCapacityPlan.RequestTimeout + TimeSpan.FromSeconds(5));
            var clock = Stopwatch.StartNew();
            var diagnostics = new OperationTimingDiagnostics([scenario.Operation], clock, DateTimeOffset.UtcNow,
                SecretsCapacityPlan.MeasurementDuration, concurrency, "isolated-capacity", runtimeDigest);
            try
            {
                result = await PerfRunner.RunAsync(scenario.Name, concurrency, SecretsCapacityPlan.MeasurementDuration,
                    async (worker, token) =>
                    {
                        var start = Stopwatch.GetTimestamp();
                        try
                        {
                            await Action(worker, token);
                            diagnostics.Record(scenario.Operation, Stopwatch.GetElapsedTime(start).TotalMilliseconds, false);
                        }
                        catch (Exception exception)
                        {
                            diagnostics.Record(scenario.Operation, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                                true, PerfThrottle.IsThrottle(exception), exception);
                            throw;
                        }
                    }, cancellationToken: measurementDeadline.Token, maxAttempts: SecretsCapacityPlan.MeasurementAttempts);
                workersCompleted = true;
            }
            finally
            {
                clock.Stop();
                measurementSeconds = clock.Elapsed.TotalSeconds;
                timingReport = diagnostics.Snapshot(measurementSeconds, workersCompleted);
                timingReport.Workload = scenario.Name;
                timingReport.OperationSchedule = [scenario.Operation];
                timingReport.Warmup = "8 successful serial actions on disjoint single-use inventory where applicable; excluded";
                timingReport.Attribution = "one AWS SDK action; SDK retries=0; proxy/backend internal retries and asynchronous work not excluded";
                timingReport.CleanupDenominator = "cleanup excluded; see separate phase wall times in capacity evidence";
                timingReport.HarnessSourceSha = source;
            }
            SecretsCapacityPlan.AssertUsable(result, SecretsCapacityPlan.MeasurementDuration, SecretsCapacityPlan.MeasurementAttempts);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // Let bounded detached purge tasks finish before terminating their owner.
            // Termination then prevents lingering work from contaminating another rung.
            if (scenario.Operation == "DeleteSecret" && fixture.HasDefaultInstance)
            {
                await Task.Delay(SecretsCapacityPlan.PurgeQuietInterval);
                quietSeconds += SecretsCapacityPlan.PurgeQuietInterval.TotalSeconds;
            }
            try { await fixture.DisposeAsync(); }
            catch (Exception exception) { failure = Combine(failure, exception); }
            var cleanupClock = Stopwatch.StartNew();
            try
            {
                using var cleanupDeadline = new CancellationTokenSource(SecretsCapacityPlan.PhaseTimeout);
                if (resourcesMayExist)
                    await backend.CleanupAsync(inventory.Names, cleanupDeadline.Token);
                cleanupComplete = true;
            }
            catch (Exception exception) { failure = Combine(failure, exception); }
            cleanupSeconds = cleanupClock.Elapsed.TotalSeconds;
            if (timingReport is not null)
            {
                await using var timingFile = new FileStream(stem + ".operation-timings.json", FileMode.CreateNew);
                await JsonSerializer.SerializeAsync(timingFile, timingReport, OperationTimingJsonContext.Default.OperationTimingReport);
            }
            await WriteNewAsync(stem + ".capacity.json", new
            {
                schemaVersion = 1, artifactKind = "isolated_capacity_experiment", promotable = false,
                scenario = scenario.Name, operation = scenario.Operation, concurrency,
                backend = "real Azure Key Vault; not an emulator or proxy-only overhead measurement",
                runtimeDigest, configDigest, backendDigest, harnessSourceSha = source, topology,
                load = "closed-loop; fresh process and inventory per rung; serial 1/2/5/8 ladder",
                sdkRetries = 0, proxyRetries = "sealed runtime policy, unchanged; included in action latency",
                requestedMeasurementSeconds = SecretsCapacityPlan.MeasurementDuration.TotalSeconds,
                attemptBudget = SecretsCapacityPlan.MeasurementAttempts, setupRequests, warmupRequests,
                inventorySize = inventory.Names.Length, initialVersionsPerSecret = scenario.Operation == "CreateSecret" ? 0 : 1,
                maxVersionsPerSecret = scenario.Operation is "PutSecretValue" or "UpdateSecret" ? 2 : 1,
                state = scenario.SingleUse ? "each attempted action consumes a unique name; never reused or replenished" : "fixed read-only inventory",
                measurementSeconds, quietSeconds, cleanupSeconds, controlRequests = backend.Requests,
                asynchronousDelete = "force-delete response can precede purge; overlap WITHIN measurement remains included; 35s quiet then process stop and direct cleanup before next rung",
                cleanupComplete, workersCompleted,
                outcome = failure is null ? "report-only" : "invalid",
                failureType = failure?.GetType().Name,
                result = result is null ? null : new
                {
                    result.Completed, result.Failures, result.Throttled, result.ElapsedSeconds,
                    result.ThroughputPerSec, result.P50Us, result.P95Us, result.P99Us,
                },
            });
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    internal static async Task InvokeAsync(IAmazonSecretsManager client, SecretsCapacityScenario scenario,
        SecretsCapacityInventory inventory, string[] versions, string? nextToken, int worker, CancellationToken token)
    {
        var index = inventory.Claim(worker);
        var name = inventory.Names[index];
        switch (scenario.Operation)
        {
            case "CreateSecret":
                await client.CreateSecretAsync(new CreateSecretRequest
                { Name = name, SecretString = Value, ClientRequestToken = Guid.NewGuid().ToString("N") }, token);
                break;
            case "DescribeSecret":
                var description = await client.DescribeSecretAsync(new DescribeSecretRequest { SecretId = name }, token);
                if (description.Name != name) throw new InvalidOperationException("Unexpected DescribeSecret identity.");
                break;
            case "GetSecretValue":
                var value = await client.GetSecretValueAsync(new GetSecretValueRequest
                { SecretId = name, VersionId = scenario.Id == "get-version-worker-local" ? versions[index] : null }, token);
                if (value.SecretString != Value || value.VersionId != versions[index])
                    throw new InvalidOperationException("Unexpected seeded value or version; no polling is allowed in measurement.");
                break;
            case "PutSecretValue":
                await client.PutSecretValueAsync(new PutSecretValueRequest
                { SecretId = name, SecretString = Value + "-updated", ClientRequestToken = Guid.NewGuid().ToString("N") }, token);
                break;
            case "UpdateSecret":
                await client.UpdateSecretAsync(new UpdateSecretRequest
                { SecretId = name, SecretString = Value + "-updated", ClientRequestToken = Guid.NewGuid().ToString("N") }, token);
                break;
            case "ListSecrets":
                var secondPage = scenario.Id == "list-second-page-32";
                var page = await client.ListSecretsAsync(new ListSecretsRequest
                { MaxResults = 16, NextToken = secondPage ? nextToken : null }, token);
                if (page.SecretList.Count != 16 || secondPage != string.IsNullOrEmpty(page.NextToken)
                    || page.SecretList.Any(s => !inventory.Names.Contains(s.Name, StringComparer.Ordinal)))
                    throw new InvalidOperationException("Controlled LIST page changed during measurement.");
                break;
            case "DeleteSecret":
                await client.DeleteSecretAsync(new DeleteSecretRequest { SecretId = name, ForceDeleteWithoutRecovery = true }, token);
                break;
            default:
                throw new InvalidOperationException("Unknown capacity operation.");
        }
    }

    private static Exception Combine(Exception? previous, Exception current) =>
        previous is null ? current : new AggregateException(previous, current);

    private static async Task WriteNewAsync<T>(string path, T value)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }
}
