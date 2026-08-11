using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;
using DynamoDbResourceNotFoundException =
    Amazon.DynamoDBv2.Model.ResourceNotFoundException;

namespace Aws2Azure.IntegrationTests.DynamoDb;



[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbTransactionLoadQualification")]
[Collection(DynamoDbRealAzureLoadCollection.Name)]
public sealed partial class DynamoDbRealAzureTransactionLoadQualificationTests(
    DynamoDbRealAzureProxyFixture fixture)
{
    private const string ProfileId = "dynamodb-single-partition-transactions";
    private const string Service = "dynamodb";
    private const string Partition = "qualification";
    private const string BootstrapRuntimeDigest =
        "sha256:8ed5e089baeacb3e703ffae788a148e6de89f355f97c3fd10e3a74536298314b";
    private static readonly string[] Operations =
    [
        "TransactGetItems",
        "TransactWriteItems",
    ];
    private static readonly string[] RequiredScenarioIds =
    [
        "representative-load",
        "transaction-read-after-write",
        "transaction-region-pinning",
        "transaction-preflight-contracts",
        "transaction-atomicity-rollback",
        "transaction-conditions-cancellation",
        "transaction-scope-rejection",
        "transaction-idempotency",
        "transaction-contention",
        "transaction-restart-versioning",
        "transaction-sproc-body-verification",
        "rollback",
    ];

    [SkippableFact]
    public async Task Production_shaped_single_partition_transactions_write_immutable_load_evidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("AWS2AZURE_LOAD_EVIDENCE_PATH");
        Skip.If(string.IsNullOrWhiteSpace(outputPath),
            "AWS2AZURE_LOAD_EVIDENCE_PATH is not set.");
        var profile = Environment.GetEnvironmentVariable("AWS2AZURE_QUALIFICATION_PROFILE");
        Skip.IfNot(string.Equals(profile, ProfileId, StringComparison.Ordinal),
            "Transaction load qualification runs only for its workload profile.");
        var runtimeMode = Environment.GetEnvironmentVariable("AWS2AZURE_SEALED_RUNTIME_MODE");
        Skip.If(string.Equals(runtimeMode, "source_validation", StringComparison.Ordinal),
            "Transaction load evidence is qualification-only and requires an exact adjacent runtime.");
        Skip.IfNot(fixture.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure transaction load.");

        var storedProcedureMode =
            Environment.GetEnvironmentVariable("AWS2AZURE_DDB_STORED_PROCEDURE_MODE");
        LoadEvidenceProducerGuard.ValidateTransactionQualificationContext(
            profile,
            runtimeMode,
            storedProcedureMode,
            fixture.SealedCandidateConfigured,
            fixture.SealedRollbackConfigured,
            fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
            fixture.PriorRuntimeIdentity.Runtime.AggregateDigest);
        ValidateRuntimeIdentities();

        var fullOutputPath = ResolveOutputPath(outputPath!);
        File.Delete(fullOutputPath);
        File.Delete($"{fullOutputPath}.pending");
        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 8);
        var iterationInterval = TimeSpan.FromMilliseconds(
            ReadPositiveInt("AWS2AZURE_LOAD_ITERATION_INTERVAL_MS", 500));
        var requestedDuration = TimeSpan.FromSeconds(
            ReadPositiveInt("AWS2AZURE_LOAD_DURATION_SECONDS", 300));
        var tracker = new RealAzureWorkloadLoadTracker(Service, Operations);
        var completedIterations = new CompletedIterationCounter();
        var cosmosEndpoint = RequiredEnvironment("AZURE_COSMOS_ENDPOINT");
        var networkTarget = new Uri(new Uri(cosmosEndpoint), "/");
        var windowStart = DateTimeOffset.UtcNow;
        var networkBefore = await UnauthenticatedCosmosConnectivityProbe
            .MeasureHeaderLatenciesAsync(networkTarget, 12).ConfigureAwait(false);
        using var client = fixture.CreateDynamoDbClient(maxErrorRetry: 0);
        using var timeout = new CancellationTokenSource(
            requestedDuration + TimeSpan.FromMinutes(15));
        var table = "a2a-txn-load-" + Guid.NewGuid().ToString("N")[..16];
        var tableCreated = false;

        try
        {
            await CreateTableAsync(client, table, timeout.Token).ConfigureAwait(false);
            tableCreated = true;

            var scenarios = new List<RealAzureWorkloadLoadScenario>();
            scenarios.Add(await VerifyScenarioAsync(
                "transaction-sproc-body-verification",
                "TransactWriteItems",
                "real_azure",
                () => VerifyStoredProcedureBodyAsync(client, table, timeout.Token))
                .ConfigureAwait(false));
            await SeedLoadItemsAsync(
                client,
                table,
                concurrency,
                timeout.Token).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var workers = Enumerable.Range(0, concurrency)
                .Select(worker => RunWorkerAsync(
                    client,
                    table,
                    tracker,
                    completedIterations,
                    worker,
                    iterationInterval,
                    requestedDuration,
                    stopwatch,
                    timeout.Token))
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            stopwatch.Stop();
            var loadEnd = DateTimeOffset.UtcNow;
            var networkAfter = await UnauthenticatedCosmosConnectivityProbe
                .MeasureHeaderLatenciesAsync(networkTarget, 12).ConfigureAwait(false);
            var loadWindowEnd = DateTimeOffset.UtcNow;
            var operationMix = tracker.Snapshot();
            var operationOutcomes = operationMix
                .Select(item => new LoadOperationOutcome(
                    item.Operation,
                    item.Completions,
                    item.Failures,
                    tracker.FirstFailure(item.Operation)))
                .ToArray();
            var representative = tracker.Snapshot("TransactWriteItems");
            var representativeAttempts =
                representative.Completions + representative.Failures;
            scenarios.Insert(0, Scenario(
                "representative-load",
                Service,
                "TransactWriteItems",
                "real_azure",
                representative.Completions,
                representative.Failures,
                0,
                stopwatch.Elapsed.TotalSeconds,
                loadEnd));

            scenarios.Add(await VerifyScenarioAsync(
                "transaction-read-after-write",
                "TransactGetItems",
                "real_azure",
                () => VerifyReadAfterWriteAsync(client, table, timeout.Token))
                .ConfigureAwait(false));
            scenarios.Add(await VerifyScenarioAsync(
                "transaction-preflight-contracts",
                "TransactWriteItems",
                "deterministic",
                () => VerifyPreflightContractAsync(client, table, timeout.Token))
                .ConfigureAwait(false));
            var atomicityStarted = Stopwatch.GetTimestamp();
            await VerifyAtomicityAndCancellationAsync(client, table, timeout.Token)
                .ConfigureAwait(false);
            var atomicityDuration =
                Stopwatch.GetElapsedTime(atomicityStarted).TotalSeconds;
            var atomicityCapturedAt = DateTimeOffset.UtcNow;
            scenarios.Add(Scenario(
                "transaction-atomicity-rollback",
                Service,
                "TransactWriteItems",
                "real_azure",
                1,
                0,
                0,
                atomicityDuration,
                atomicityCapturedAt));
            scenarios.Add(Scenario(
                "transaction-conditions-cancellation",
                Service,
                "TransactWriteItems",
                "real_azure",
                1,
                0,
                0,
                atomicityDuration,
                atomicityCapturedAt));
            scenarios.Add(await VerifyScenarioAsync(
                "transaction-scope-rejection",
                "TransactWriteItems",
                "real_azure",
                () => VerifyScopeRejectionAsync(client, table, timeout.Token))
                .ConfigureAwait(false));
            scenarios.Add(await VerifyScenarioAsync(
                "transaction-idempotency",
                "TransactWriteItems",
                "real_azure",
                () => VerifyIdempotencyAsync(client, table, timeout.Token))
                .ConfigureAwait(false));
            scenarios.Add(await VerifyScenarioAsync(
                "transaction-contention",
                "TransactWriteItems",
                "real_azure",
                () => VerifyContentionAsync(client, table, timeout.Token))
                .ConfigureAwait(false));
            var restartStarted = Stopwatch.GetTimestamp();
            await VerifyStableAuthorityAndRestartVersioningAsync(
                client,
                table,
                timeout.Token).ConfigureAwait(false);
            var restartDuration = Stopwatch.GetElapsedTime(restartStarted).TotalSeconds;
            var restartCapturedAt = DateTimeOffset.UtcNow;
            scenarios.Add(Scenario(
                "transaction-region-pinning",
                Service,
                "TransactWriteItems",
                "deterministic",
                1,
                0,
                0,
                restartDuration,
                restartCapturedAt));
            scenarios.Add(Scenario(
                "transaction-restart-versioning",
                Service,
                "TransactWriteItems",
                "real_azure",
                1,
                0,
                0,
                restartDuration,
                restartCapturedAt));

            var rollback = await RealAzureRollbackQualification
                .VerifyDynamoDbTransactionsAsync(fixture, timeout.Token)
                .ConfigureAwait(false);
            scenarios.Add(Scenario(
                "rollback",
                Service,
                "TransactGetItems",
                "real_azure",
                1,
                0,
                0,
                rollback.DurationSeconds,
                rollback.CapturedAtUtc));

            var windowEnd = DateTimeOffset.UtcNow;
            var networkLatencies = networkBefore.Concat(networkAfter).ToArray();
            var signals = BuildSignals(
                representative,
                stopwatch.Elapsed.TotalSeconds,
                networkLatencies,
                representativeAttempts,
                tracker.Throttles("TransactWriteItems"),
                loadEnd,
                loadWindowEnd);
            var evidence = BuildEvidence(
                concurrency,
                requestedDuration,
                operationMix,
                scenarios,
                signals,
                rollback,
                windowStart,
                windowEnd);

            await LoadEvidenceProducerGuard.PublishAsync(
                completedIterations.Count,
                operationOutcomes,
                RequiredScenarioIds,
                scenarios.Select(scenario => scenario.Id).ToArray(),
                evidence.RollbackProofs.Count,
                fixture.ProxyOutput,
                () => PublishAsync(evidence, fullOutputPath, timeout.Token))
                .ConfigureAwait(false);
        }
        finally
        {
            if (tableCreated)
            {
                try
                {
                    await client.DeleteTableAsync(
                        new DeleteTableRequest { TableName = table },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

}
