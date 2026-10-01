using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.SecretsManager;

[Trait("Category", "RealAzure")]
[Trait("Category", "SecretsManagerConcurrencyDiagnostic")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRealAzureConcurrencyTests(SecretsManagerRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Exact_sealed_candidate_compares_five_and_eight_workers_on_one_backend()
    {
        var output = Environment.GetEnvironmentVariable("AWS2AZURE_SECRETS_CONCURRENCY_REPORT_PATH");
        Skip.If(string.IsNullOrWhiteSpace(output), "No controlled concurrency diagnostic requested.");
        Assert.True(fixture.Configured && fixture.SealedCandidateConfigured && !fixture.SealedRollbackConfigured,
            "Diagnostic requires one verified sealed candidate and a dedicated real Key Vault.");
        Assert.Equal(RequiredEnvironment("AWS2AZURE_SECRETS_CONCURRENCY_DIGEST"),
            fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest);
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_KEYVAULT_URL_STABLE")),
            "A second backend is forbidden in the concurrency diagnostic.");
        var path = ResolveOutputPath(output!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            throw new InvalidDataException("Diagnostic output already exists.");
        var report = new SecretsConcurrencyReport
        {
            RunId = RequiredEnvironment("GITHUB_RUN_ID"),
            RunAttempt = RequiredEnvironment("GITHUB_RUN_ATTEMPT"),
            HarnessSourceSha = RequiredEnvironment("GITHUB_SHA"),
            BackendRegion = RequiredEnvironment("AZURE_LOCATION"),
            CandidateDigest = fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
            CandidateIdentityDigest = fixture.CandidateRuntimeIdentityDigest,
            Binding = ReadBinding(),
        };
        using var identityHttp = new AzureHttpClient();
        var identity = WorkloadIdentityTokenSource.FromEnvironment(identityHttp);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        var inventory = new DiagnosticKeyVaultInventory(http, new Uri(RequiredEnvironment("AZURE_KEYVAULT_URL")),
            token => identity.GetTokenAsync("https://vault.azure.net/.default", token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(80));
        var refreshes = new ConcurrentQueue<DateTimeOffset>();
        var authorizationCaptures = new List<(SecretsConcurrencySlot Slot, SecretsAuthorizationCapture Capture)>();
        Exception? failure = null;
        try
        {
            await RunWithRefreshAsync(
                token => SecretsManagerConcurrencyDiagnostic.RunAsync(report,
                    async cancellation =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                        var capture = new SecretsAuthorizationCapture();
                        fixture.AuthorizationCapture = capture;
                        authorizationCaptures.Add((report.Slots[^1], capture));
                        await fixture.StartRuntimeAsync(SealedRuntimeRole.Candidate).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                    },
                    MeasureAsync, inventory.ReadAsync, inventory.ProbeAsync,
                    ReadBinding, Task.Delay, PublishAsync, token),
                async token =>
                {
                    using var refreshDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    refreshDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    await SecretsManagerCredentialRotationQualification.RefreshGitHubOidcTokenAsync(
                        RequiredEnvironment("AZURE_FEDERATED_TOKEN_FILE"), refreshDeadline.Token).ConfigureAwait(false);
                    refreshes.Enqueue(DateTimeOffset.UtcNow);
                },
                Task.Delay, deadline.Token).ConfigureAwait(false);
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
                // Stop the final slot before the final snapshot so redirected
                // output readers can deliver the authorization failure and EOF.
                try
                {
                    if (fixture.HasDefaultInstance)
                        await fixture.StopProxyInstanceAsync(fixture.DefaultInstance).ConfigureAwait(false);
                }
                catch (Exception shutdownFailure)
                {
                    report.Completed = false;
                    report.Comparison = null;
                    report.Failure ??= RealAzureWorkloadFirstFailure.FromException(shutdownFailure, false);
                    throw;
                }
                finally
                {
                    fixture.AuthorizationCapture = null;
                    await PublishAsync(report).ConfigureAwait(false);
                }
            }
            catch (Exception publishing) when (failure is not null)
            {
                throw new AggregateException("Diagnostic and publication failed.", failure, publishing);
            }
        }

        async Task MeasureAsync(int index, int concurrency, bool warmup,
            SecretsConcurrencyPhase phase, CancellationToken token)
        {
            using var client = fixture.CreateSecretsManagerClient(maxErrorRetry: 2);
            var duration = warmup ? SecretsManagerConcurrencyDiagnostic.WarmupDuration
                : SecretsManagerConcurrencyDiagnostic.MeasurementDuration;
            var tracker = new RealAzureWorkloadLoadTracker("secretsmanager",
                SecretsManagerRealAzureRcObservationTests.Operations);
            var clock = Stopwatch.StartNew();
            var timing = new OperationTimingDiagnostics(
                SecretsManagerRealAzureRcObservationTests.Operations, clock, DateTimeOffset.UtcNow,
                duration, concurrency, "candidate", report.CandidateDigest,
                operationSchedule: SecretsManagerRealAzureRcObservationTests.LifecycleOperationSchedule);
            tracker.TimingDiagnostics = timing;
            using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            phaseDeadline.CancelAfter(duration + SecretsManagerConcurrencyDiagnostic.BarrierTimeout);
            var completed = false;
            try
            {
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async worker =>
                {
                    try
                    {
                        await SecretsManagerRealAzureRcObservationTests.RunWorkerAsync(
                            client, tracker, "candidate", worker, duration, clock,
                            phaseDeadline.Token, strictDiagnostic: true).ConfigureAwait(false);
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
                phase.Timing = timing.Snapshot(clock.Elapsed.TotalSeconds, completed);
                phase.Timing.Warmup = warmup ? "this_is_warmup" : "separate_30_second_phase_completed";
            }
        }

        RcCohortBinding ReadBinding()
        {
            fixture.VerifyConfigurationUnchanged();
            if (!fixture.IsProxyRunning
                || fixture.DefaultInstance.RuntimeRole != SealedRuntimeRole.Candidate)
                throw new InvalidDataException("The sealed candidate is not running.");
            return new(fixture.BackendIdentityDigest, fixture.ProxyConfigDigest, fixture.AwsBindingDigest);
        }

        async Task PublishAsync(SecretsConcurrencyReport value)
        {
            foreach (var (slot, capture) in authorizationCaptures)
                slot.AuthorizationEvidence = capture.Snapshot();
            value.OidcRefreshesAtUtc = refreshes.ToList();
            await File.WriteAllTextAsync(path + ".pending",
                JsonSerializer.Serialize(value, SecretsConcurrencyJsonContext.Default.SecretsConcurrencyReport))
                .ConfigureAwait(false);
            File.Move(path + ".pending", path, overwrite: true);
        }
    }

    internal static async Task RunWithRefreshAsync(
        Func<CancellationToken, Task> work, Func<CancellationToken, Task> refresh,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? refreshFailure = null;
        Exception? workFailure = null;
        var refreshTask = RefreshAsync();
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            await work(lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            workFailure = exception;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await refreshTask.ConfigureAwait(false);
        }
        if (refreshFailure is not null && workFailure is not null and not OperationCanceledException)
            throw new AggregateException("Diagnostic workload and OIDC refresh failed.", workFailure, refreshFailure);
        if (refreshFailure is not null)
            throw new InvalidOperationException("Diagnostic OIDC refresh failed.", refreshFailure);
        if (workFailure is not null)
            ExceptionDispatchInfo.Capture(workFailure).Throw();

        async Task RefreshAsync()
        {
            try
            {
                while (true)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    await refresh(lifetime.Token).ConfigureAwait(false);
                    await delay(TimeSpan.FromMinutes(4), lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception)
            {
                refreshFailure = exception;
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
        }
    }
}
