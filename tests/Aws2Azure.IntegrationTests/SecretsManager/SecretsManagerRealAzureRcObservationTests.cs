using System.Diagnostics;
using System.Net;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.SecretsManager;

[Trait("Category", "RealAzure")]
[Trait("Category", "SecretsManagerRcObservation")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRealAzureRcObservationTests(
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
    private static readonly string[] LifecycleOperationSchedule =
    [
        "CreateSecret",
        "DescribeSecret",
        "GetSecretValue",
        "PutSecretValue",
        "GetSecretValue",
        "UpdateSecret",
        "GetSecretValue",
        "ListSecrets",
        "DeleteSecret",
    ];

    [SkippableFact]
    public async Task Candidate_and_stable_cohorts_capture_lifecycle_and_exact_prior_restore()
    {
        var observationCapturePath = Environment.GetEnvironmentVariable(
            "AWS2AZURE_RC_OBSERVATION_CAPTURE_PATH");
        var calibrationReportPath = Environment.GetEnvironmentVariable(
            "AWS2AZURE_RC_CALIBRATION_REPORT_PATH");
        var calibrationMode = !string.IsNullOrWhiteSpace(calibrationReportPath);
        Skip.If(string.IsNullOrWhiteSpace(observationCapturePath)
                && string.IsNullOrWhiteSpace(calibrationReportPath),
            "RC observation capture or calibration report path is not set.");
        Skip.IfNot(fixture.Configured,
            fixture.SkipReason ?? "Real Azure Key Vault is not configured.");
        Assert.True(fixture.SealedRollbackConfigured,
            "RC observation requires exact candidate and prior sealed runtimes.");

        var minutes = calibrationMode
            ? RcObservationCaptureWriter.ReadCalibrationDurationMinutes()
            : RcObservationCaptureWriter.ReadWindowMinutes();
        var candidateConcurrency = calibrationMode
            ? RcObservationCaptureWriter.ReadCalibrationConcurrency("candidate")
            : RcObservationCaptureWriter.ReadConcurrency("candidate");
        var stableConcurrency = calibrationMode
            ? RcObservationCaptureWriter.ReadCalibrationConcurrency("stable")
            : RcObservationCaptureWriter.ReadConcurrency("stable");
        var operationMixIdentity = RcObservationCaptureWriter.OperationMixIdentity(
            "secretsmanager-basic-lifecycle",
            LifecycleOperationSchedule);
        if (!calibrationMode)
        {
            Assert.Equal(
                RequiredEnvironment("AWS2AZURE_RC_OBSERVATION_OPERATION_MIX_IDENTITY"),
                operationMixIdentity);
        }
        var duration = TimeSpan.FromMinutes(minutes);
        using var timeout = new CancellationTokenSource(duration + TimeSpan.FromMinutes(20));
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var refreshTask = RefreshAssertionLoopAsync(
            RequiredEnvironment("AZURE_FEDERATED_TOKEN_FILE"),
            refresh.Token);
        var candidateTracker = new RealAzureWorkloadLoadTracker(
            "secretsmanager",
            Operations);
        var stableTracker = new RealAzureWorkloadLoadTracker(
            "secretsmanager",
            Operations);
        var stable = await fixture.StartAdditionalRuntimeAsync(SealedRuntimeRole.Prior)
            .ConfigureAwait(false);
        Assert.NotEqual(fixture.ProxyServiceUrl, stable.ServiceUrl);
        using var candidateClient = fixture.CreateSecretsManagerClient();
        using var stableClient = fixture.CreateSecretsManagerClient(stable.ServiceUrl);
        var canaryName = "a2a-rc-canary-" + Guid.NewGuid().ToString("N");
        var canaryValue = "rc-observation-" + Guid.NewGuid().ToString("N");
        var canaryExists = false;
        var restoredPrior = false;

        try
        {
            await candidateClient.CreateSecretAsync(
                new CreateSecretRequest
                {
                    Name = canaryName,
                    SecretString = canaryValue,
                    Description = "aws2azure RC observation restoration canary",
                },
                timeout.Token).ConfigureAwait(false);
            canaryExists = true;
            await AssertValueAsync(
                candidateClient,
                canaryName,
                canaryValue,
                timeout.Token).ConfigureAwait(false);

            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var workers = new List<Task>(candidateConcurrency + stableConcurrency);
            for (var worker = 0; worker < candidateConcurrency; worker++)
            {
                workers.Add(RunWorkerAsync(
                    candidateClient,
                    candidateTracker,
                    "candidate",
                    worker,
                    duration,
                    stopwatch,
                    timeout.Token));
            }
            for (var worker = 0; worker < stableConcurrency; worker++)
            {
                workers.Add(RunWorkerAsync(
                    stableClient,
                    stableTracker,
                    "stable",
                    worker,
                    duration,
                    stopwatch,
                    timeout.Token));
            }
            await Task.WhenAll(workers).ConfigureAwait(false);
            stopwatch.Stop();
            var measurementEndedAt = DateTimeOffset.UtcNow;

            await SecretsManagerCredentialRotationQualification.RefreshGitHubOidcTokenAsync(
                RequiredEnvironment("AZURE_FEDERATED_TOKEN_FILE"),
                timeout.Token).ConfigureAwait(false);
            var restorationStartedAt = DateTimeOffset.UtcNow;
            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            using var restoredClient = fixture.CreateSecretsManagerClient();
            await AssertValueAsync(
                restoredClient,
                canaryName,
                canaryValue,
                timeout.Token).ConfigureAwait(false);
            await restoredClient.DeleteSecretAsync(
                new DeleteSecretRequest
                {
                    SecretId = canaryName,
                    ForceDeleteWithoutRecovery = true,
                },
                timeout.Token).ConfigureAwait(false);
            await AssertAbsentAsync(restoredClient, canaryName, timeout.Token)
                .ConfigureAwait(false);
            canaryExists = false;
            var restorationVerifiedAt = DateTimeOffset.UtcNow;
            restoredPrior = true;

            var candidateGet = candidateTracker.Snapshot("GetSecretValue");
            var stableGet = stableTracker.Snapshot("GetSecretValue");
            var candidateAttempts = RcObservationCaptureWriter.TotalAttempts(
                candidateTracker);
            var stableAttempts = RcObservationCaptureWriter.TotalAttempts(stableTracker);
            if (calibrationMode)
            {
                var report = new RcCalibrationReport
                {
                    Profile = new RcObservationCaptureProfile
                    {
                        Id = "secretsmanager-basic-lifecycle",
                        Version = 1,
                    },
                    ReleaseCandidate = new RcCalibrationReleaseCandidate
                    {
                        Id = RequiredEnvironment(
                            "AWS2AZURE_RC_CALIBRATION_RELEASE_CANDIDATE_ID"),
                        ManifestDigest = RequiredEnvironment(
                            "AWS2AZURE_RC_CALIBRATION_MANIFEST_DIGEST"),
                        SourceSha = RequiredEnvironment(
                            "AWS2AZURE_RC_CALIBRATION_CANDIDATE_SOURCE_SHA"),
                        ArchiveInputsDigest = RequiredEnvironment(
                            "AWS2AZURE_RC_CALIBRATION_ARCHIVE_INPUTS_DIGEST"),
                        GhcrInputsDigest = RequiredEnvironment(
                            "AWS2AZURE_RC_CALIBRATION_GHCR_INPUTS_DIGEST"),
                    },
                    Candidate = new RcObservationCaptureCohortIdentity
                    {
                        RuntimeIdentityDigest = fixture.CandidateRuntimeIdentityDigest,
                        RuntimeDigest =
                            fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
                        SourceSha = fixture.CandidateRuntimeIdentity.Source.Sha,
                    },
                    Prior = new RcObservationCaptureCohortIdentity
                    {
                        RuntimeIdentityDigest = fixture.PriorRuntimeIdentityDigest,
                        RuntimeDigest = fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
                        SourceSha = fixture.PriorRuntimeIdentity.Source.Sha,
                    },
                    Azure = new RcObservationCaptureAzure
                    {
                        BackendKind = "keyVault",
                        Region = RequiredEnvironment("AZURE_LOCATION"),
                        BackendIdentityDigest = fixture.BackendIdentityDigest,
                        ConfigDigest = fixture.ProxyConfigDigest,
                        AwsBindingDigest = fixture.AwsBindingDigest,
                    },
                    Calibration = new RcCalibrationWindow
                    {
                        StartedAtUtc = startedAt,
                        MeasurementEndedAtUtc = measurementEndedAt,
                        EndedAtUtc = restorationVerifiedAt,
                        RequestedDurationMinutes = minutes,
                    },
                    PerCohortConcurrency = new RcCalibrationConcurrency
                    {
                        Candidate = candidateConcurrency,
                        Stable = stableConcurrency,
                    },
                    TotalConcurrency = candidateConcurrency + stableConcurrency,
                    OperationMixIdentity =
                        RcObservationCaptureWriter.OperationMixIdentity(
                            "secretsmanager-basic-lifecycle",
                            LifecycleOperationSchedule),
                    Cohorts =
                    [
                        CalibrationCohort(
                            "candidate",
                            candidateConcurrency,
                            candidateGet,
                            measurementEndedAt - startedAt,
                            candidateTracker),
                        CalibrationCohort(
                            "stable",
                            stableConcurrency,
                            stableGet,
                            measurementEndedAt - startedAt,
                            stableTracker),
                    ],
                    Restoration = Restoration(
                        fixture,
                        restorationStartedAt,
                        restorationVerifiedAt),
                };
                await RcObservationCaptureWriter.PublishCalibrationAsync(report)
                    .ConfigureAwait(false);
                return;
            }

            var evidence = new RcObservationCaptureEvidence
            {
                Profile = new RcObservationCaptureProfile
                {
                    Id = "secretsmanager-basic-lifecycle",
                    Version = 1,
                },
                Azure = new RcObservationCaptureAzure
                {
                    BackendKind = "keyVault",
                    Region = RequiredEnvironment("AZURE_LOCATION"),
                    BackendIdentityDigest = fixture.BackendIdentityDigest,
                    ConfigDigest = fixture.ProxyConfigDigest,
                    AwsBindingDigest = fixture.AwsBindingDigest,
                },
                Observation = new RcObservationCaptureWindow
                {
                    StartedAtUtc = startedAt,
                    MeasurementEndedAtUtc = measurementEndedAt,
                    EndedAtUtc = restorationVerifiedAt,
                    RequestedWindowMinutes = minutes,
                },
                LoadShape = new RcObservationCaptureLoadShape
                {
                    CandidateConcurrency = candidateConcurrency,
                    StableConcurrency = stableConcurrency,
                    OperationMixIdentity = operationMixIdentity,
                },
                Cohorts =
                [
                    Cohort(
                        "candidate",
                        fixture.CandidateRuntimeIdentityDigest,
                        fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
                        startedAt,
                        restorationStartedAt,
                        candidateConcurrency,
                        fixture.BackendIdentityDigest,
                        fixture.ProxyConfigDigest,
                        fixture.AwsBindingDigest,
                        fixture.ProxyServiceUrl,
                        candidateTracker),
                    Cohort(
                        "stable",
                        fixture.PriorRuntimeIdentityDigest,
                        fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
                        startedAt,
                        restorationVerifiedAt,
                        stableConcurrency,
                        fixture.BackendIdentityDigest,
                        fixture.ProxyConfigDigest,
                        fixture.AwsBindingDigest,
                        stable.ServiceUrl,
                        stableTracker),
                ],
                Metrics =
                [
                    new RcObservationCaptureMetric
                    {
                        Id = "representative-load-throughput",
                        Unit = "throughput_per_sec",
                        CandidateValue =
                            candidateGet.Completions / stopwatch.Elapsed.TotalSeconds,
                        StableValue =
                            stableGet.Completions / stopwatch.Elapsed.TotalSeconds,
                        CandidateSamples =
                            candidateGet.Completions + candidateGet.Failures,
                        StableSamples = stableGet.Completions + stableGet.Failures,
                        CapturedAtUtc = measurementEndedAt,
                    },
                    new RcObservationCaptureMetric
                    {
                        Id = "operation-failure-rate",
                        Unit = "ratio",
                        CandidateValue =
                            RcObservationCaptureWriter.FailureRate(candidateTracker),
                        StableValue =
                            RcObservationCaptureWriter.FailureRate(stableTracker),
                        CandidateSamples = candidateAttempts,
                        StableSamples = stableAttempts,
                        CapturedAtUtc = measurementEndedAt,
                    },
                ],
                Restoration = Restoration(
                    fixture,
                    restorationStartedAt,
                    restorationVerifiedAt),
            };
            await RcObservationCaptureWriter.PublishAsync(evidence).ConfigureAwait(false);
        }
        finally
        {
            if (!restoredPrior && fixture.Configured && fixture.SealedRollbackConfigured)
            {
                try
                {
                    await SecretsManagerCredentialRotationQualification
                        .RefreshGitHubOidcTokenAsync(
                            RequiredEnvironment("AZURE_FEDERATED_TOKEN_FILE"),
                            CancellationToken.None).ConfigureAwait(false);
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                    await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
            refresh.Cancel();
            try
            {
                await refreshTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            await fixture.StopProxyInstanceAsync(stable).ConfigureAwait(false);
            if (canaryExists)
            {
                try
                {
                    using var cleanup =
                        fixture.CreateSecretsManagerClient(maxErrorRetry: 0);
                    await cleanup.DeleteSecretAsync(new DeleteSecretRequest
                    {
                        SecretId = canaryName,
                        ForceDeleteWithoutRecovery = true,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static RcObservationCaptureCohort Cohort(
        string role,
        string identityDigest,
        string runtimeDigest,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        int concurrency,
        string backendIdentityDigest,
        string configDigest,
        string awsBindingDigest,
        string endpoint,
        RealAzureWorkloadLoadTracker tracker) => new()
    {
        Id = RcObservationCaptureWriter.CohortId(role),
        Role = role,
        RuntimeIdentityDigest = identityDigest,
        RuntimeDigest = runtimeDigest,
        BackendKind = "keyVault",
        Region = RequiredEnvironment("AZURE_LOCATION"),
        BackendIdentityDigest = backendIdentityDigest,
        ConfigDigest = configDigest,
        AwsBindingDigest = awsBindingDigest,
        ObservedFromUtc = startedAt,
        ObservedUntilUtc = endedAt,
        MemberDigests = Enumerable.Range(0, concurrency)
            .Select(worker => RcObservationCaptureWriter.MemberDigest(
                "secretsmanager-basic-lifecycle",
                role,
                worker,
                endpoint))
            .ToList(),
        OperationDiagnostics = RcObservationCaptureWriter.OperationDiagnostics(tracker),
    };

    private static RcCalibrationCohort CalibrationCohort(
        string role,
        int concurrency,
        RealAzureWorkloadLoadOperationMeasurement getSecretValue,
        TimeSpan elapsed,
        RealAzureWorkloadLoadTracker tracker) => new()
    {
        Role = role,
        Concurrency = concurrency,
        GetSecretValueThroughputPerSecond =
            elapsed.TotalSeconds <= 0 ? 0 : getSecretValue.Completions / elapsed.TotalSeconds,
        GetSecretValueSamples = getSecretValue.Completions + getSecretValue.Failures,
        OperationDiagnostics = RcObservationCaptureWriter.OperationDiagnostics(tracker),
    };

    private static RcObservationCaptureRestoration Restoration(
        SecretsManagerRealAzureProxyFixture fixture,
        DateTimeOffset startedAt,
        DateTimeOffset verifiedAt) => new()
    {
        Verified = true,
        RuntimeIdentityDigest = fixture.PriorRuntimeIdentityDigest,
        RuntimeDigest = fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
        BackendIdentityDigest = fixture.BackendIdentityDigest,
        ConfigDigest = fixture.ProxyConfigDigest,
        AwsBindingDigest = fixture.AwsBindingDigest,
        StartedAtUtc = startedAt,
        VerifiedAtUtc = verifiedAt,
    };

    private static async Task RunWorkerAsync(
        IAmazonSecretsManager client,
        RealAzureWorkloadLoadTracker tracker,
        string role,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var iteration = 0;
        while (stopwatch.Elapsed < duration)
        {
            var name = $"a2a-rc-{role[..1]}-{worker:x2}-{iteration++:x6}-" +
                       Guid.NewGuid().ToString("N");
            name = name[..Math.Min(name.Length, 48)];
            var created = false;
            try
            {
                await MeasureAsync(
                    tracker,
                    "CreateSecret",
                    () => client.CreateSecretAsync(new CreateSecretRequest
                    {
                        Name = name,
                        SecretString = "value-1",
                        Description = "aws2azure RC observation cohort",
                    }, cancellationToken),
                    IsThrottle).ConfigureAwait(false);
                created = true;
                await MeasureAsync(
                    tracker,
                    "DescribeSecret",
                    async () =>
                    {
                        var response = await client.DescribeSecretAsync(
                            new DescribeSecretRequest { SecretId = name },
                            cancellationToken).ConfigureAwait(false);
                        if (response.Name != name)
                        {
                            throw new InvalidDataException(
                                "DescribeSecret returned the wrong cohort secret.");
                        }
                    },
                    IsThrottle).ConfigureAwait(false);
                await MeasureAsync(
                    tracker,
                    "GetSecretValue",
                    () => AssertValueAsync(client, name, "value-1", cancellationToken),
                    IsThrottle).ConfigureAwait(false);
                await MeasureAsync(
                    tracker,
                    "PutSecretValue",
                    async () =>
                    {
                        var response = await client.PutSecretValueAsync(
                            new PutSecretValueRequest
                            {
                                SecretId = name,
                                SecretString = "value-2",
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(response.VersionId))
                        {
                            throw new InvalidDataException(
                                "PutSecretValue returned no version id.");
                        }
                    },
                    IsThrottle).ConfigureAwait(false);
                await MeasureAsync(
                    tracker,
                    "GetSecretValue",
                    () => AssertValueAsync(client, name, "value-2", cancellationToken),
                    IsThrottle).ConfigureAwait(false);
                await MeasureAsync(
                    tracker,
                    "UpdateSecret",
                    async () =>
                    {
                        var response = await client.UpdateSecretAsync(
                            new UpdateSecretRequest
                            {
                                SecretId = name,
                                SecretString = "value-3",
                                Description = "aws2azure RC observation cohort updated",
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(response.VersionId))
                        {
                            throw new InvalidDataException(
                                "UpdateSecret returned no version id.");
                        }
                    },
                    IsThrottle).ConfigureAwait(false);
                await MeasureAsync(
                    tracker,
                    "GetSecretValue",
                    () => AssertValueAsync(client, name, "value-3", cancellationToken),
                    IsThrottle).ConfigureAwait(false);
                await MeasureAsync(
                    tracker,
                    "ListSecrets",
                    () => client.ListSecretsAsync(
                        new ListSecretsRequest(),
                        cancellationToken),
                    IsThrottle).ConfigureAwait(false);
                await MeasureAsync(
                    tracker,
                    "DeleteSecret",
                    () => client.DeleteSecretAsync(new DeleteSecretRequest
                    {
                        SecretId = name,
                        ForceDeleteWithoutRecovery = true,
                    }, cancellationToken),
                    IsThrottle).ConfigureAwait(false);
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

    private static async Task AssertValueAsync(
        IAmazonSecretsManager client,
        string secretId,
        string expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            var response = await client.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = secretId },
                cancellationToken).ConfigureAwait(false);
            if (response.SecretString == expected)
            {
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "GetSecretValue did not observe the expected cohort value.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task AssertAbsentAsync(
        IAmazonSecretsManager client,
        string secretId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            try
            {
                await client.GetSecretValueAsync(
                    new GetSecretValueRequest { SecretId = secretId },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "Exact-prior restoration did not remove the Secrets Manager canary.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task RefreshAssertionLoopAsync(
        string tokenFile,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(4), cancellationToken)
                .ConfigureAwait(false);
            await SecretsManagerCredentialRotationQualification.RefreshGitHubOidcTokenAsync(
                tokenFile,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsThrottle(Exception exception) =>
        exception is AmazonSecretsManagerException aws
        && (aws.StatusCode == HttpStatusCode.TooManyRequests
            || aws.ErrorCode == "ThrottlingException");

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{name} is required.")
            : value;
    }
}
