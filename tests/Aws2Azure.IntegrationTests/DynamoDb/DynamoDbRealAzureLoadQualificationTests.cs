using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Production-shaped real-Azure load qualification for
/// <c>dynamodb-basic-crud</c> (issue #627): the exact seven-operation CRUD
/// mix (CreateTable, DescribeTable, PutItem, GetItem, UpdateItem, DeleteItem,
/// DeleteTable) driven through the official AWS SDK for DynamoDB against an
/// isolated ephemeral Cosmos DB (Strong consistency, serverless) topology,
/// plus the deterministic and real-Azure failure/rollback scenarios required
/// by the workload manifest. Mirrors
/// <c>S3RealAzureLoadQualificationTests</c>'s shape so the immutable
/// evidence, qualification policy, and workflow wiring stay uniform across
/// profiles.
/// </summary>
[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbLoadQualification")]
[Collection(DynamoDbRealAzureLoadCollection.Name)]
public sealed partial class DynamoDbRealAzureLoadQualificationTests(DynamoDbRealAzureProxyFixture fixture)
{
    private const string Service = "dynamodb";
    private static readonly string[] Operations =
    [
        "CreateTable",
        "DescribeTable",
        "PutItem",
        "GetItem",
        "UpdateItem",
        "DeleteItem",
        "DeleteTable",
    ];

    [SkippableFact]
    public async Task Production_shaped_item_crud_writes_immutable_load_evidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("AWS2AZURE_LOAD_EVIDENCE_PATH");
        Skip.If(string.IsNullOrWhiteSpace(outputPath),
            "AWS2AZURE_LOAD_EVIDENCE_PATH is not set.");
        Skip.IfNot(fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure DynamoDB load.");

        var fullOutputPath = ResolveOutputPath(outputPath!);
        File.Delete(fullOutputPath);
        File.Delete($"{fullOutputPath}.pending");
        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 8);
        var requestedDuration = TimeSpan.FromSeconds(
            ReadPositiveInt("AWS2AZURE_LOAD_DURATION_SECONDS", 300));
        var tracker = new RealAzureWorkloadLoadTracker(Service, Operations);
        var completedIterations = new CompletedIterationCounter();
        var cosmosEndpoint = RequiredEnvironment("AZURE_COSMOS_ENDPOINT");
        var networkTarget = new Uri(new Uri(cosmosEndpoint), "/");
        var windowStart = DateTimeOffset.UtcNow;
        var networkBefore = await UnauthenticatedCosmosConnectivityProbe.MeasureHeaderLatenciesAsync(
            networkTarget,
            12).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        using var client = fixture.CreateDynamoDbClient();
        using var timeout = new CancellationTokenSource(requestedDuration + TimeSpan.FromMinutes(10));

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
        var networkAfter = await UnauthenticatedCosmosConnectivityProbe.MeasureHeaderLatenciesAsync(
            networkTarget,
            12).ConfigureAwait(false);
        var loadWindowEnd = DateTimeOffset.UtcNow;
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
        var representative = tracker.Snapshot("GetItem");
        var representativeAttempts = representative.Completions + representative.Failures;
        var networkLatencies = networkBefore.Concat(networkAfter).ToArray();
        var scenarios = new List<RealAzureWorkloadLoadScenario>
        {
            Scenario(
                "representative-load",
                Service,
                "GetItem",
                "real_azure",
                representative.Completions,
                representative.Failures,
                0,
                stopwatch.Elapsed.TotalSeconds,
                loadEnd),
        };
        scenarios.Add(await VerifyScenarioAsync(
            "conditional-write-concurrency",
            "PutItem",
            "real_azure",
            () => VerifyConditionalWriteConcurrencyAsync(client, timeout.Token))
            .ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            "read-after-write",
            "GetItem",
            "real_azure",
            () => VerifyReadAfterWriteAsync(client, timeout.Token))
            .ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId,
            "GetItem",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
                DeterministicFailureQualification.ThrottlingScenarioId)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId,
            "GetItem",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
                DeterministicFailureQualification.TimeoutScenarioId)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId,
            "GetItem",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
                DeterministicFailureQualification.ServiceUnavailableScenarioId))
            .ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            "restart",
            "PutItem",
            "real_azure",
            () => RealAzureRestartQualification.VerifyDynamoDbAsync(fixture)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.RetryExhaustionScenarioId,
            "PutItem",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyDynamoDbScenarioAsync(
                DeterministicFailureQualification.RetryExhaustionScenarioId))
            .ConfigureAwait(false));

        RealAzureRollbackResult? rollback = null;
        if (fixture.SealedRollbackConfigured)
        {
            rollback = await RealAzureRollbackQualification.VerifyDynamoDbAsync(
                fixture,
                timeout.Token).ConfigureAwait(false);
            scenarios.Add(Scenario(
                "rollback",
                Service,
                "GetItem",
                "real_azure",
                1,
                0,
                0,
                rollback.DurationSeconds,
                rollback.CapturedAtUtc));
        }
        else
        {
            scenarios.Add(Scenario(
                "rollback",
                Service,
                "GetItem",
                "real_azure",
                0,
                0,
                1,
                0,
                DateTimeOffset.UtcNow));
        }
        var windowEnd = DateTimeOffset.UtcNow;
        var signals = BuildRepresentativeLoadSignals(
            operationMix,
            completedIterationCount,
            startedIterationCount,
            totalCompletions,
            totalAttempts,
            stopwatch.Elapsed.TotalSeconds,
            networkLatencies,
            representativeAttempts,
            tracker.Throttles("GetItem"),
            loadEnd,
            loadWindowEnd);

        var evidence = new RealAzureWorkloadLoadEvidence
        {
            SchemaVersion = 1,
            Profile = new RealAzureWorkloadLoadProfile
            {
                Id = "dynamodb-basic-crud",
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
