using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.SecretsManager;

[Trait("Category", "RealAzure")]
[Trait("Category", "SecretsManagerLoadQualification")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRealAzureLoadQualificationTests(
    SecretsManagerRealAzureProxyFixture fixture)
{
    private static readonly string[] Operations =
    [
        "CreateSecret",
        "DescribeSecret",
        "GetSecretValue",
        "PutSecretValue",
        "UpdateSecret",
        "ListSecrets",
        "DeleteSecret",
    ];

    [SkippableFact]
    public async Task Production_shaped_lifecycle_writes_immutable_load_evidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("AWS2AZURE_LOAD_EVIDENCE_PATH");
        Skip.If(string.IsNullOrWhiteSpace(outputPath),
            "AWS2AZURE_LOAD_EVIDENCE_PATH is not set.");
        Skip.If(!fixture.Configured,
            fixture.SkipReason ?? "Real Azure Key Vault is not configured.");

        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 8);
        var requestedDuration = TimeSpan.FromSeconds(
            ReadPositiveInt("AWS2AZURE_LOAD_DURATION_SECONDS", 300));
        var tracker = new RealAzureWorkloadLoadTracker("secretsmanager", Operations);
        var vaultUrl = RequiredEnvironment("AZURE_KEYVAULT_URL");
        var windowStart = DateTimeOffset.UtcNow;
        var networkTarget = new Uri(new Uri(vaultUrl), "secrets?api-version=7.4");
        var networkBefore = await ProbeNetworkAsync(networkTarget, 12).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        using var client = fixture.CreateSecretsManagerClient();
        using var timeout = new CancellationTokenSource(requestedDuration + TimeSpan.FromMinutes(5));

        var workers = Enumerable.Range(0, concurrency)
            .Select(worker => RunWorkerAsync(
                client,
                tracker,
                worker,
                requestedDuration,
                stopwatch,
                timeout.Token))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        stopwatch.Stop();
        var loadEnd = DateTimeOffset.UtcNow;
        var networkAfter = await ProbeNetworkAsync(networkTarget, 12).ConfigureAwait(false);
        var loadWindowEnd = DateTimeOffset.UtcNow;
        var throttling = await VerifyScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId,
            "GetSecretValue",
            "deterministic",
            () => DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
                DeterministicFailureQualification.ThrottlingScenarioId)).ConfigureAwait(false);
        var timeoutScenario = await VerifyScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId,
            "GetSecretValue",
            "deterministic",
            () => DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
                DeterministicFailureQualification.TimeoutScenarioId)).ConfigureAwait(false);
        var serviceUnavailable = await VerifyScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId,
            "GetSecretValue",
            "deterministic",
            () => DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
                DeterministicFailureQualification.ServiceUnavailableScenarioId))
            .ConfigureAwait(false);
        var cancellation = await VerifyScenarioAsync(
            DeterministicFailureQualification.CancellationScenarioId,
            "GetSecretValue",
            "deterministic",
            () => DeterministicFailureQualification.VerifySecretsManagerScenarioAsync(
                DeterministicFailureQualification.CancellationScenarioId)).ConfigureAwait(false);
        var restart = await VerifyScenarioAsync(
            "restart",
            "CreateSecret",
            "real_azure",
            () => RealAzureRestartQualification.VerifySecretsManagerAsync(fixture))
            .ConfigureAwait(false);
        var windowEnd = DateTimeOffset.UtcNow;

        var operationMix = tracker.Snapshot();
        var totalCompletions = operationMix.Sum(item => item.Completions);
        var totalFailures = operationMix.Sum(item => item.Failures);
        var representative = tracker.Snapshot("GetSecretValue");
        var representativeAttempts = representative.Completions + representative.Failures;
        var representativeLatencies = tracker.Latencies("GetSecretValue");
        var networkLatencies = networkBefore.Concat(networkAfter).ToArray();
        var evidence = new RealAzureWorkloadLoadEvidence
        {
            SchemaVersion = 1,
            Profile = new RealAzureWorkloadLoadProfile
            {
                Id = "secretsmanager-basic-lifecycle",
                Version = 1,
                Services =
                [
                    new RealAzureWorkloadLoadProfileService
                    {
                        Service = "secretsmanager",
                        Operations = Operations.ToList(),
                    }
                ],
            },
            Candidate = new RealAzureWorkloadLoadCandidate
            {
                GitSha = RequiredEnvironment("AWS2AZURE_LOAD_GIT_SHA"),
                ArtifactDigest = RequiredEnvironment("AWS2AZURE_LOAD_ARTIFACT_DIGEST"),
                ConfigDigest = RequiredEnvironment("AWS2AZURE_LOAD_CONFIG_DIGEST"),
            },
            Provenance = new RealAzureWorkloadLoadProvenance
            {
                RunId = RequiredEnvironment("GITHUB_RUN_ID"),
                RunUrl = RequiredEnvironment("AWS2AZURE_LOAD_RUN_URL"),
                RunAttempt = ReadPositiveInt("GITHUB_RUN_ATTEMPT", 1),
                GeneratedAtUtc = windowEnd,
                WindowStartUtc = windowStart,
                WindowEndUtc = windowEnd,
                Region = RequiredEnvironment("AZURE_LOCATION"),
                BackendDescription = RequiredEnvironment("AWS2AZURE_LOAD_BACKEND_DESCRIPTION"),
                ProducerConfigDigest = RequiredEnvironment(
                    "AWS2AZURE_LOAD_PRODUCER_CONFIG_DIGEST"),
            },
            LoadShape = new RealAzureWorkloadLoadShape
            {
                Concurrency = concurrency,
                RequestedDurationSeconds = requestedDuration.TotalSeconds,
            },
            OperationMix = operationMix,
            Scenarios =
            [
                Scenario(
                    "representative-load",
                    "GetSecretValue",
                    "real_azure",
                    representative.Completions,
                    representative.Failures,
                    0,
                    stopwatch.Elapsed.TotalSeconds,
                    loadEnd),
                throttling,
                timeoutScenario,
                serviceUnavailable,
                cancellation,
                restart,
                // Backend credential rotation and sealed-artifact rollback require
                // external deployment orchestration that this ephemeral run lacks.
                Scenario("credential-rotation", "GetSecretValue", "real_azure", 0, 0, 1, 0, loadEnd),
                Scenario("rollback", "GetSecretValue", "real_azure", 0, 0, 1, 0, loadEnd),
            ],
            Signals =
            [
                Signal(
                    "representative-load-throughput",
                    "throughput_per_sec",
                    representative.Completions / stopwatch.Elapsed.TotalSeconds,
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-p95",
                    "p95_ms",
                    Percentile(representativeLatencies, 0.95),
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-p99",
                    "p99_ms",
                    Percentile(representativeLatencies, 0.99),
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-network-p95",
                    "p95_ms",
                    Percentile(networkLatencies, 0.95),
                    networkLatencies.LongLength,
                    loadWindowEnd),
                Signal(
                    "representative-load-throttle-rate",
                    "throttle_rate",
                    representativeAttempts == 0
                        ? 0
                        : (double)tracker.Throttles("GetSecretValue") / representativeAttempts,
                    representativeAttempts,
                    loadEnd),
            ],
        };

        var fullOutputPath = ResolveOutputPath(outputPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllTextAsync(
            fullOutputPath,
            JsonSerializer.Serialize(
                evidence,
                RealAzureWorkloadLoadEvidenceJsonContext.Default.RealAzureWorkloadLoadEvidence),
            timeout.Token).ConfigureAwait(false);

        Assert.True(totalCompletions > 0, "The production-shaped load completed no operations.");
        Assert.True(
            totalFailures == 0,
            $"{totalFailures} of {totalCompletions + totalFailures} operations failed." +
            $"{Environment.NewLine}{string.Join(
                ", ",
                operationMix
                    .Where(item => item.Failures > 0)
                    .Select(item =>
                        $"{item.Operation}={item.Failures} ({tracker.FirstFailure(item.Operation)})"))}" +
            $"{Environment.NewLine}{fixture.ProxyOutput}");
        Assert.All(operationMix, item => Assert.True(
            item.Completions > 0,
            $"{item.Operation} completed no requests."));
    }

    private static async Task RunWorkerAsync(
        IAmazonSecretsManager client,
        RealAzureWorkloadLoadTracker tracker,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var iteration = 0;
        while (stopwatch.Elapsed < duration)
        {
            var name = $"a2a-load-{worker:x2}-{iteration++:x6}-{Guid.NewGuid():N}"[..48];
            var created = false;
            try
            {
                await MeasureAsync(tracker, "CreateSecret", async () =>
                {
                    await client.CreateSecretAsync(new CreateSecretRequest
                    {
                        Name = name,
                        SecretString = "value-1",
                        Description = "aws2azure production-shaped load qualification",
                    }, cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
                created = true;

                await MeasureAsync(tracker, "DescribeSecret", async () =>
                {
                    var response = await client.DescribeSecretAsync(
                        new DescribeSecretRequest { SecretId = name },
                        cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(response.Name, name, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("DescribeSecret returned the wrong secret.");
                    }
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "GetSecretValue", async () =>
                {
                    await GetExpectedValueAsync(
                        client,
                        name,
                        "value-1",
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "PutSecretValue", async () =>
                {
                    var response = await client.PutSecretValueAsync(new PutSecretValueRequest
                    {
                        SecretId = name,
                        SecretString = "value-2",
                    }, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(response.VersionId))
                    {
                        throw new InvalidDataException("PutSecretValue returned no version id.");
                    }
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "GetSecretValue", async () =>
                {
                    await GetExpectedValueAsync(
                        client,
                        name,
                        "value-2",
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "UpdateSecret", async () =>
                {
                    var response = await client.UpdateSecretAsync(new UpdateSecretRequest
                    {
                        SecretId = name,
                        SecretString = "value-3",
                        Description = "aws2azure production-shaped load qualification updated",
                    }, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(response.VersionId))
                    {
                        throw new InvalidDataException("UpdateSecret returned no version id.");
                    }
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "GetSecretValue", async () =>
                {
                    await GetExpectedValueAsync(
                        client,
                        name,
                        "value-3",
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "ListSecrets", async () =>
                {
                    await client.ListSecretsAsync(
                        new ListSecretsRequest(),
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "DeleteSecret", async () =>
                {
                    await client.DeleteSecretAsync(new DeleteSecretRequest
                    {
                        SecretId = name,
                        ForceDeleteWithoutRecovery = true,
                    }, cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
                created = false;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (created)
                {
                    try
                    {
                        await client.DeleteSecretAsync(new DeleteSecretRequest
                        {
                            SecretId = name,
                            ForceDeleteWithoutRecovery = true,
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static async Task MeasureAsync(
        RealAzureWorkloadLoadTracker tracker,
        string operation,
        Func<Task> action)
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
                IsThrottle(exception),
                exception);
            throw;
        }
    }

    private static async Task GetExpectedValueAsync(
        IAmazonSecretsManager client,
        string secretId,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var delay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            var response = await client.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = secretId },
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(response.SecretString, expectedValue, StringComparison.Ordinal))
            {
                return;
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    $"GetSecretValue did not observe '{expectedValue}' within 60 seconds.");
            }
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(2))
            {
                delay += delay;
            }
        }
    }

    private static bool IsThrottle(Exception exception)
    {
        return exception is AmazonSecretsManagerException aws
               && (aws.StatusCode == HttpStatusCode.TooManyRequests
                   || string.Equals(aws.ErrorCode, "ThrottlingException", StringComparison.Ordinal));
    }

    private static async Task<RealAzureWorkloadLoadScenario> VerifyScenarioAsync(
        string id,
        string operation,
        string evidenceSource,
        Func<Task> verify)
    {
        var started = Stopwatch.GetTimestamp();
        await verify().ConfigureAwait(false);
        return Scenario(
            id,
            operation,
            evidenceSource,
            1,
            0,
            0,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            DateTimeOffset.UtcNow);
    }

    private static RealAzureWorkloadLoadScenario Scenario(
        string id,
        string operation,
        string evidenceSource,
        long completions,
        long failures,
        long skipped,
        double durationSeconds,
        DateTimeOffset capturedAtUtc) => new()
    {
        Id = id,
        Service = "secretsmanager",
        Operation = operation,
        EvidenceSource = evidenceSource,
        Completions = completions,
        Failures = failures,
        Skipped = skipped,
        DurationSeconds = durationSeconds,
        CapturedAtUtc = capturedAtUtc,
    };

    private static RealAzureWorkloadLoadSignal Signal(
        string id,
        string metric,
        double measuredValue,
        long samples,
        DateTimeOffset capturedAtUtc) => new()
    {
        Id = id,
        ScenarioId = "representative-load",
        Metric = metric,
        MeasuredValue = measuredValue,
        Samples = samples,
        CapturedAtUtc = capturedAtUtc,
    };

}
