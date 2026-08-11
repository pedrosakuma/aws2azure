using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.Sqs;

/// <summary>
/// Sealed production-shaped real-Azure load runner for the
/// <c>sqs-standard-messaging</c> profile (issue #626). Exercises all seven
/// profile operations plus long polling, visibility/redelivery, receipt
/// settlement, concurrency, restart, and rollback. AMQP is the
/// namespace-wide default transport (matching production config) and is
/// the graded/required evidence source for every required real-Azure
/// scenario; REST evidence is captured separately (never blended into the
/// AMQP numbers) via the fixed <see cref="RealAzureProxyFixture.SqsRestLaneQueueName"/>
/// per-queue transport override, as supplementary, non-required scenario
/// rows. FIFO is out of scope for this profile and is never exercised here.
/// </summary>
[Trait("Category", "RealAzure")]
[Trait("Category", "SqsLoadQualification")]
[Collection(RealAzureCollection.Name)]

public sealed partial class SqsRealAzureLoadQualificationTests(RealAzureProxyFixture fixture)
{
    private const string Service = "sqs";
    private static readonly string[] Operations =
    [
        "CreateQueue",
        "GetQueueUrl",
        "ListQueues",
        "SendMessage",
        "ReceiveMessage",
        "DeleteMessage",
        "DeleteQueue",
    ];

    [SkippableFact]
    public async Task Production_shaped_queue_messaging_writes_immutable_load_evidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("AWS2AZURE_LOAD_EVIDENCE_PATH");
        Skip.If(string.IsNullOrWhiteSpace(outputPath),
            "AWS2AZURE_LOAD_EVIDENCE_PATH is not set.");
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS load.");

        var fullOutputPath = ResolveOutputPath(outputPath!);
        File.Delete(fullOutputPath);
        File.Delete($"{fullOutputPath}.pending");
        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 8);
        var requestedDuration = TimeSpan.FromSeconds(
            ReadPositiveInt("AWS2AZURE_LOAD_DURATION_SECONDS", 300));
        var tracker = new RealAzureWorkloadLoadTracker(Service, Operations);
        var completedIterations = new CompletedIterationCounter();
        var windowStart = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(requestedDuration + TimeSpan.FromMinutes(15));

        var workers = Enumerable.Range(0, concurrency)
            .Select(worker => RunWorkerAsync(
                client,
                tracker,
                completedIterations,
                worker,
                requestedDuration,
                stopwatch,
                timeout.Token))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        stopwatch.Stop();
        var loadEnd = DateTimeOffset.UtcNow;
        var operationMix = tracker.Snapshot();
        var totalCompletions = operationMix.Sum(item => item.Completions);
        var totalFailures = operationMix.Sum(item => item.Failures);
        var totalAttempts = totalCompletions + totalFailures;
        var completedIterationCount = completedIterations.Count;
        var startedIterationCount = completedIterations.StartedCount;
        var operationOutcomes = operationMix
            .Select(item => new LoadOperationOutcome(
                item.Operation,
                item.Completions,
                item.Failures,
                tracker.FirstFailure(item.Operation)))
            .ToArray();
        var representative = tracker.Snapshot("ReceiveMessage");
        var representativeAttempts = representative.Completions + representative.Failures;

        var scenarios = new List<RealAzureWorkloadLoadScenario>
        {
            Scenario(
                "representative-load",
                Service,
                "ReceiveMessage",
                "real_azure",
                representative.Completions,
                representative.Failures,
                0,
                stopwatch.Elapsed.TotalSeconds,
                loadEnd),
        };
        scenarios.Add(await VerifyScenarioAsync(
            "redelivery",
            "ReceiveMessage",
            "real_azure",
            () => VerifyRedeliveryAsync(fixture, timeout.Token)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId,
            "SendMessage",
            "deterministic",
            static () => DeterministicFailureQualification.VerifySqsScenarioAsync(
                DeterministicFailureQualification.ThrottlingScenarioId)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId,
            "SendMessage",
            "deterministic",
            static () => DeterministicFailureQualification.VerifySqsScenarioAsync(
                DeterministicFailureQualification.TimeoutScenarioId)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId,
            "SendMessage",
            "deterministic",
            static () => DeterministicFailureQualification.VerifySqsScenarioAsync(
                DeterministicFailureQualification.ServiceUnavailableScenarioId))
            .ConfigureAwait(false));

        var concurrencyResult = await VerifyConcurrencyAsync(
            fixture, concurrency, concurrency * 5, timeout.Token).ConfigureAwait(false);
        scenarios.Add(Scenario(
            "concurrency",
            Service,
            "ReceiveMessage",
            "real_azure",
            concurrencyResult.Completions,
            concurrencyResult.Failures,
            0,
            concurrencyResult.DurationSeconds,
            DateTimeOffset.UtcNow));

        scenarios.Add(await VerifyScenarioAsync(
            "restart",
            "SendMessage",
            "real_azure",
            () => RealAzureRestartQualification.VerifySqsAsync(fixture)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.RetryExhaustionScenarioId,
            "SendMessage",
            "deterministic",
            static () => DeterministicFailureQualification.VerifySqsScenarioAsync(
                DeterministicFailureQualification.RetryExhaustionScenarioId))
            .ConfigureAwait(false));

        RealAzureSqsRollbackResult? rollback = null;
        if (fixture.SealedRollbackConfigured)
        {
            rollback = await RealAzureRollbackQualification.VerifySqsAsync(
                fixture,
                timeout.Token).ConfigureAwait(false);
            scenarios.Add(Scenario(
                "rollback",
                Service,
                "DeleteMessage",
                "real_azure",
                1,
                0,
                0,
                rollback.DurationSeconds,
                rollback.CapturedAtUtc));
            // Supplementary, non-required: the REST-transport counterpart of the
            // same rollback window, kept as its own row so REST and AMQP
            // rollback evidence are never blended (issue #626).
            scenarios.Add(Scenario(
                "rollback-rest",
                Service,
                "DeleteMessage",
                "real_azure",
                rollback.RestReceiptHandleSurvivedRestart ? 1 : 0,
                rollback.RestReceiptHandleSurvivedRestart ? 0 : 1,
                0,
                rollback.RestDurationSeconds,
                rollback.RestCapturedAtUtc));
        }
        else
        {
            scenarios.Add(Scenario(
                "rollback",
                Service,
                "DeleteMessage",
                "real_azure",
                0,
                0,
                1,
                0,
                DateTimeOffset.UtcNow));
        }

        // Supplementary, non-required: REST-transport representative evidence
        // from the fixed REST-lane queue, kept entirely separate from the
        // AMQP-default representative-load numbers above (issue #626).
        var restRepresentative = await VerifyRestRepresentativeAsync(
            fixture, iterations: 20, timeout.Token).ConfigureAwait(false);
        scenarios.Add(Scenario(
            "representative-load-rest",
            Service,
            "ReceiveMessage",
            "real_azure",
            restRepresentative.Completions,
            restRepresentative.Failures,
            0,
            restRepresentative.DurationSeconds,
            DateTimeOffset.UtcNow));

        var windowEnd = DateTimeOffset.UtcNow;
        var signals = BuildRepresentativeLoadSignals(
            operationMix,
            completedIterationCount,
            startedIterationCount,
            totalCompletions,
            totalAttempts,
            stopwatch.Elapsed.TotalSeconds,
            representativeAttempts,
            tracker.Throttles("ReceiveMessage"),
            loadEnd);

        var evidence = new RealAzureWorkloadLoadEvidence
        {
            SchemaVersion = 1,
            Profile = new RealAzureWorkloadLoadProfile
            {
                Id = "sqs-standard-messaging",
                Version = 1,
                Services =
                [
                    new RealAzureWorkloadLoadProfileService
                    {
                        Service = Service,
                        Operations = Operations.ToList(),
                    }
                ],
            },
            Candidate = new RealAzureWorkloadLoadCandidate
            {
                GitSha = RequiredEnvironment("AWS2AZURE_LOAD_GIT_SHA"),
                ArtifactDigest = RequiredEnvironment("AWS2AZURE_LOAD_ARTIFACT_DIGEST"),
                ConfigDigest = RequiredEnvironment("AWS2AZURE_LOAD_CONFIG_DIGEST"),
                QualificationMode = fixture.SealedRollbackConfigured
                    ? "sealed"
                    : "source_validation",
                Runtime = fixture.SealedCandidateConfigured
                    ? fixture.CandidateRuntimeIdentity
                    : null,
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
            Scenarios = scenarios,
            Signals = signals,
            RollbackProofs = rollback is null ? [] : [rollback.Proof],
        };

        await LoadEvidenceProducerGuard.PublishAsync(
            completedIterationCount,
            operationOutcomes,
            fixture.ProxyOutput,
            async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                var pendingOutputPath = $"{fullOutputPath}.pending";
                File.Delete(pendingOutputPath);
                try
                {
                    await File.WriteAllTextAsync(
                        pendingOutputPath,
                        JsonSerializer.Serialize(
                            evidence,
                            RealAzureWorkloadLoadEvidenceJsonContext.Default
                                .RealAzureWorkloadLoadEvidence),
                        timeout.Token).ConfigureAwait(false);
                    File.Move(pendingOutputPath, fullOutputPath, true);
                }
                finally
                {
                    File.Delete(pendingOutputPath);
                }
            }).ConfigureAwait(false);
    }

}
