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
public sealed class DynamoDbRealAzureTransactionLoadQualificationTests(
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

    private void ValidateRuntimeIdentities()
    {
        var candidate = fixture.CandidateRuntimeIdentity;
        var prior = fixture.PriorRuntimeIdentity;
        if (candidate.Role != "candidate" || candidate.Status != "candidate"
            || prior.Role != "prior" || prior.Status != "bootstrap"
            || !prior.Eligibility.RollbackBaselineEligible
            || prior.Eligibility.PromotionEligible
            || prior.Runtime.AggregateDigest != BootstrapRuntimeDigest
            || candidate.Runtime.AggregateDigest == prior.Runtime.AggregateDigest)
        {
            throw new InvalidDataException(
                "Transaction load qualification requires the distinct committed rollback-only bootstrap.");
        }
    }

    private RealAzureWorkloadLoadEvidence BuildEvidence(
        int concurrency,
        TimeSpan requestedDuration,
        List<RealAzureWorkloadLoadOperationMeasurement> operationMix,
        List<RealAzureWorkloadLoadScenario> scenarios,
        List<RealAzureWorkloadLoadSignal> signals,
        RealAzureRollbackResult rollback,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd) => new()
    {
        SchemaVersion = 1,
        Profile = new RealAzureWorkloadLoadProfile
        {
            Id = ProfileId,
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
            QualificationMode = "sealed",
            Runtime = fixture.CandidateRuntimeIdentity,
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
            BackendDescription = RequiredEnvironment(
                "AWS2AZURE_LOAD_BACKEND_DESCRIPTION"),
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
        RollbackProofs = [rollback.Proof],
    };

    private static async Task PublishAsync(
        RealAzureWorkloadLoadEvidence evidence,
        string fullOutputPath,
        CancellationToken cancellationToken)
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
                cancellationToken).ConfigureAwait(false);
            File.Move(pendingOutputPath, fullOutputPath, true);
        }
        finally
        {
            File.Delete(pendingOutputPath);
        }
    }

    private static async Task RunWorkerAsync(
        IAmazonDynamoDB client,
        string table,
        RealAzureWorkloadLoadTracker tracker,
        CompletedIterationCounter completedIterations,
        int worker,
        TimeSpan iterationInterval,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var iteration = 0;
        var writeSorts = Enumerable.Range(0, 5)
            .Select(index => $"load-{worker:D2}-write-{index:D2}")
            .ToArray();
        var readSorts = writeSorts
            .Concat(Enumerable.Range(0, 5)
                .Select(index => $"load-{worker:D2}-seed-{index:D2}"))
            .ToArray();
        while (stopwatch.Elapsed < duration)
        {
            completedIterations.RecordStarted();
            var version = $"{worker:D2}-{iteration++:D8}";
            try
            {
                await MeasureAsync(
                    tracker,
                    "TransactWriteItems",
                    () => client.TransactWriteItemsAsync(
                        new TransactWriteItemsRequest
                        {
                            TransactItems = writeSorts
                                .Select(sort => Put(table, Partition, sort, version))
                                .ToList(),
                        },
                        cancellationToken),
                    IsThrottle).ConfigureAwait(false);
                await completedIterations.CompleteAfterAsync(() =>
                    MeasureAsync(
                        tracker,
                        "TransactGetItems",
                        async () =>
                        {
                            var response = await client.TransactGetItemsAsync(
                                new TransactGetItemsRequest
                                {
                                    TransactItems = readSorts
                                        .Select(sort => Get(table, Partition, sort))
                                        .ToList(),
                                },
                                cancellationToken).ConfigureAwait(false);
                            if (response.Responses.Count != 10)
                            {
                                throw new InvalidDataException(
                                    "Transaction load read did not return ten items.");
                            }
                            for (var index = 0; index < response.Responses.Count; index++)
                            {
                                var expected = index < 5 ? version : "seed";
                                if (response.Responses[index].Item["version"].S != expected)
                                {
                                    throw new InvalidDataException(
                                        "Transaction load read returned an unexpected version.");
                                }
                            }
                        },
                        IsThrottle)).ConfigureAwait(false);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (stopwatch.Elapsed < duration)
                {
                    await Task.Delay(iterationInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private async Task VerifyStoredProcedureBodyAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        const string conflictingBody =
            "function atomicTransactWrite(operations) { "
            + "getContext().getResponse().setBody({success:true,conflictingBody:true}); }";
        using var http = new HttpClient();
        await CosmosRestBootstrap.CreateStoredProcedureAsync(
            http,
            fixture.CosmosEndpoint,
            fixture.CosmosKey,
            fixture.CosmosDatabase,
            table,
            SprocManager.TransactSprocId,
            conflictingBody).ConfigureAwait(false);
        var conflictPresent = true;
        try
        {
            var failure = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(
                () => client.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest
                    {
                        TransactItems =
                        [
                            Put(table, Partition, "sproc-conflict", "must-not-commit"),
                        ],
                    },
                    cancellationToken));
            Assert.Equal("InternalServerError", failure.ErrorCode);
            Assert.False(await ExistsAsync(
                client,
                table,
                Partition,
                "sproc-conflict",
                cancellationToken).ConfigureAwait(false));
            await CosmosRestBootstrap.DeleteStoredProcedureAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                fixture.CosmosDatabase,
                table,
                SprocManager.TransactSprocId).ConfigureAwait(false);
            conflictPresent = false;
            await fixture.RestartAsync().ConfigureAwait(false);
            await client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        Put(table, Partition, "sproc-conflict", "restored"),
                    ],
                },
                cancellationToken).ConfigureAwait(false);
            using var restored = JsonDocument.Parse(
                await CosmosRestBootstrap.ReadStoredProcedureAsync(
                    http,
                    fixture.CosmosEndpoint,
                    fixture.CosmosKey,
                    fixture.CosmosDatabase,
                    table,
                    SprocManager.TransactSprocId).ConfigureAwait(false));
            Assert.Equal(
                SprocManager.TransactSprocBody,
                restored.RootElement.GetProperty("body").GetString());
        }
        finally
        {
            if (conflictPresent)
            {
                await CosmosRestBootstrap.DeleteStoredProcedureAsync(
                    http,
                    fixture.CosmosEndpoint,
                    fixture.CosmosKey,
                    fixture.CosmosDatabase,
                    table,
                    SprocManager.TransactSprocId).ConfigureAwait(false);
            }
        }
    }

    private static async Task VerifyReadAfterWriteAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        const int sampleCount = 12;
        var sampleInterval = TimeSpan.FromMilliseconds(200);
        var sortKeys = Enumerable.Range(0, 72)
            .Select(index => $"snapshot-{index:D2}")
            .ToArray();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        for (var version = 1; version <= sampleCount; version++)
        {
            var expectedVersion = version.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            await WriteVersionAsync(
                client,
                table,
                sortKeys,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            var response = await client.TransactGetItemsAsync(
                new TransactGetItemsRequest
                {
                    TransactItems = sortKeys
                        .Select(sort => Get(table, Partition, sort))
                        .ToList(),
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Responses.Count != sortKeys.Length)
            {
                throw new InvalidDataException(
                    "Transactional read returned the wrong item count.");
            }
            var snapshotVersion = response.Responses[0].Item["version"].S;
            if (response.Responses.Any(item =>
                    item.Item["version"].S != snapshotVersion))
            {
                throw new InvalidDataException(
                    "Transactional read observed mixed committed versions.");
            }
            if (!string.Equals(
                    snapshotVersion,
                    expectedVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Transactional read returned version '{snapshotVersion}' after committing '{expectedVersion}'.");
            }
            observed.Add(snapshotVersion);
            await Task.Delay(sampleInterval, cancellationToken).ConfigureAwait(false);
        }

        if (observed.Count != sampleCount)
        {
            throw new InvalidDataException(
                $"Transactional reads observed {observed.Count} of {sampleCount} committed versions.");
        }
    }

    private static async Task VerifyPreflightContractAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var oversized = new string('x', 400 * 1024);
        var failure = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem
                        {
                            Put = new Put
                            {
                                TableName = table,
                                Item = new()
                                {
                                    ["pk"] = S(Partition),
                                    ["sk"] = S("oversized"),
                                    ["payload"] = S(oversized),
                                },
                            },
                        },
                    ],
                },
                cancellationToken));
        Assert.Equal("ValidationException", failure.ErrorCode);
        Assert.False(await ExistsAsync(
            client,
            table,
            Partition,
            "oversized",
            cancellationToken).ConfigureAwait(false));
    }

    private static async Task VerifyAtomicityAndCancellationAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        await client.PutItemAsync(
            new PutItemRequest
            {
                TableName = table,
                Item = new()
                {
                    ["pk"] = S(Partition),
                    ["sk"] = S("atomic-gate"),
                    ["state"] = S("closed"),
                },
            },
            cancellationToken).ConfigureAwait(false);
        var canceled = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem
                        {
                            ConditionCheck = new ConditionCheck
                            {
                                TableName = table,
                                Key = Key(Partition, "atomic-gate"),
                                ConditionExpression = "#state = :open",
                                ExpressionAttributeNames = new() { ["#state"] = "state" },
                                ExpressionAttributeValues = new() { [":open"] = S("open") },
                            },
                        },
                        Put(table, Partition, "atomic-one", "must-not-commit"),
                        Put(table, Partition, "atomic-two", "must-not-commit"),
                    ],
                },
                cancellationToken));
        Assert.Equal(
            new[] { "ConditionalCheckFailed", "None", "None" },
            canceled.CancellationReasons.Select(reason => reason.Code).ToArray());
        Assert.False(await ExistsAsync(
            client, table, Partition, "atomic-one", cancellationToken).ConfigureAwait(false));
        Assert.False(await ExistsAsync(
            client, table, Partition, "atomic-two", cancellationToken).ConfigureAwait(false));

        await client.TransactWriteItemsAsync(
            new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = table,
                            Key = Key(Partition, "atomic-gate"),
                            ConditionExpression = "#state = :closed",
                            ExpressionAttributeNames = new() { ["#state"] = "state" },
                            ExpressionAttributeValues = new() { [":closed"] = S("closed") },
                        },
                    },
                    Put(table, Partition, "atomic-one", "committed"),
                    Put(table, Partition, "atomic-two", "committed"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        Assert.True(await ExistsAsync(
            client, table, Partition, "atomic-one", cancellationToken).ConfigureAwait(false));
        Assert.True(await ExistsAsync(
            client, table, Partition, "atomic-two", cancellationToken).ConfigureAwait(false));
    }

    private static async Task VerifyScopeRejectionAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var failure = await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        Put(table, Partition, "scope-a", "must-not-commit"),
                        Put(table, "other-partition", "scope-b", "must-not-commit"),
                    ],
                },
                cancellationToken));
        Assert.Equal("ValidationException", failure.ErrorCode);
        Assert.False(await ExistsAsync(
            client, table, Partition, "scope-a", cancellationToken).ConfigureAwait(false));
        Assert.False(await ExistsAsync(
            client, table, "other-partition", "scope-b", cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task VerifyIdempotencyAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var token = "load-" + Guid.NewGuid().ToString("N")[..20];
        var request = new TransactWriteItemsRequest
        {
            ClientRequestToken = token,
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = new()
                        {
                            ["pk"] = S(Partition),
                            ["sk"] = S("idempotency"),
                            ["version"] = S("one"),
                            ["marker"] = S("committed-once"),
                        },
                        ConditionExpression = "attribute_not_exists(#marker)",
                        ExpressionAttributeNames = new() { ["#marker"] = "marker" },
                    },
                },
            ],
        };
        await client.TransactWriteItemsAsync(request, cancellationToken).ConfigureAwait(false);
        await client.TransactWriteItemsAsync(request, cancellationToken).ConfigureAwait(false);
        var mismatch = await Assert.ThrowsAsync<IdempotentParameterMismatchException>(
            () => client.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    ClientRequestToken = token,
                    TransactItems =
                    [
                        Put(table, Partition, "idempotency", "two"),
                    ],
                },
                cancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var item = await ReadAsync(
            client, table, Partition, "idempotency", cancellationToken).ConfigureAwait(false);
        Assert.Equal("one", item["version"].S);
    }

    private static async Task VerifyContentionAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        await client.PutItemAsync(
            new PutItemRequest
            {
                TableName = table,
                Item = new()
                {
                    ["pk"] = S(Partition),
                    ["sk"] = S("contention-gate"),
                    ["state"] = S("open"),
                },
            },
            cancellationToken).ConfigureAwait(false);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async contender =>
        {
            try
            {
                await client.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest
                    {
                        TransactItems =
                        [
                            new TransactWriteItem
                            {
                                Put = new Put
                                {
                                    TableName = table,
                                    Item = new()
                                    {
                                        ["pk"] = S(Partition),
                                        ["sk"] = S("contention-gate"),
                                        ["state"] = S("closed"),
                                        ["winner"] = S($"winner-{contender}"),
                                    },
                                    ConditionExpression = "#state = :open",
                                    ExpressionAttributeNames = new() { ["#state"] = "state" },
                                    ExpressionAttributeValues = new() { [":open"] = S("open") },
                                },
                            },
                            Put(
                                table,
                                Partition,
                                $"contention-audit-{contender}",
                                $"winner-{contender}"),
                        ],
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TransactionCanceledException)
            {
                return false;
            }
        })).ConfigureAwait(false);
        Assert.Single(outcomes.Where(outcome => outcome));
        var query = await client.QueryAsync(
            new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
                ExpressionAttributeValues = new()
                {
                    [":pk"] = S(Partition),
                    [":prefix"] = S("contention-audit-"),
                },
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);
        Assert.Single(query.Items);
    }

    private async Task VerifyStableAuthorityAndRestartVersioningAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var accountBefore = CosmosAccountInfoParser.Parse(
            await CosmosRestBootstrap.ReadAccountAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                cancellationToken).ConfigureAwait(false),
            new Uri(fixture.CosmosEndpoint));
        var selectionBefore = CosmosRegionRouting.SelectTransactionEndpoint(
            accountBefore,
            [],
            out var authorityBefore);
        if (accountBefore.EnableMultipleWriteLocations
            || accountBefore.WritableLocations.Length != 1
            || selectionBefore != CosmosTransactionEndpointSelectionStatus.Ready
            || authorityBefore != accountBefore.WritableLocations[0].Endpoint)
        {
            throw new InvalidDataException(
                "Transaction load requires one discovered writable Cosmos authority.");
        }

        var token = "restart-" + Guid.NewGuid().ToString("N")[..18];
        var request = new TransactWriteItemsRequest
        {
            ClientRequestToken = token,
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = table,
                        Item = new()
                        {
                            ["pk"] = S(Partition),
                            ["sk"] = S("restart-a"),
                            ["version"] = S("before"),
                            ["marker"] = S("committed-once"),
                        },
                        ConditionExpression = "attribute_not_exists(#marker)",
                        ExpressionAttributeNames = new() { ["#marker"] = "marker" },
                    },
                },
                Put(table, Partition, "restart-b", "before"),
            ],
        };
        await client.TransactWriteItemsAsync(request, cancellationToken).ConfigureAwait(false);
        var routeOutputOffset = fixture.ProxyOutput.Length;
        await fixture.RestartWithTransactionRouteCaptureAsync().ConfigureAwait(false);
        using var restartedClient = fixture.CreateDynamoDbClient(maxErrorRetry: 0);
        var committedSnapshot = await restartedClient.TransactGetItemsAsync(
            new TransactGetItemsRequest
            {
                TransactItems =
                [
                    GetWithMarker(table, Partition, "restart-a"),
                    Get(table, Partition, "restart-b"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, committedSnapshot.Responses.Count);
        Assert.All(
            committedSnapshot.Responses,
            response => Assert.Equal("before", response.Item["version"].S));
        Assert.Equal(
            "committed-once",
            committedSnapshot.Responses[0].Item["marker"].S);

        await restartedClient.TransactWriteItemsAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = await restartedClient.TransactGetItemsAsync(
            new TransactGetItemsRequest
            {
                TransactItems =
                [
                    GetWithMarker(table, Partition, "restart-a"),
                    Get(table, Partition, "restart-b"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, snapshot.Responses.Count);
        Assert.All(
            snapshot.Responses,
            response => Assert.Equal("before", response.Item["version"].S));
        Assert.Equal("committed-once", snapshot.Responses[0].Item["marker"].S);
        await restartedClient.TransactWriteItemsAsync(
            new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    Put(table, Partition, "restart-a", "after"),
                    Put(table, Partition, "restart-b", "after"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        var accountAfter = CosmosAccountInfoParser.Parse(
            await CosmosRestBootstrap.ReadAccountAsync(
                http,
                fixture.CosmosEndpoint,
                fixture.CosmosKey,
                cancellationToken).ConfigureAwait(false),
            new Uri(fixture.CosmosEndpoint));
        var selectionAfter = CosmosRegionRouting.SelectTransactionEndpoint(
            accountAfter,
            [],
            out var authorityAfter);
        if (selectionAfter != CosmosTransactionEndpointSelectionStatus.Ready
            || authorityAfter != authorityBefore
            || accountAfter.AccountIdentity != accountBefore.AccountIdentity)
        {
            throw new InvalidDataException(
                "Transaction restart did not retain one stable Cosmos authority.");
        }
        await WaitForTransactionRoutesAsync(
            routeOutputOffset,
            authorityBefore.AbsoluteUri,
            expectedCount: 4,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForTransactionRoutesAsync(
        int outputOffset,
        string expectedAuthority,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var output = fixture.ProxyOutput;
            var captured = outputOffset < output.Length
                ? output[outputOffset..]
                : string.Empty;
            var routes = captured
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(
                    "Selected Cosmos transaction endpoint ",
                    StringComparison.Ordinal))
                .ToArray();
            if (routes.Length >= expectedCount)
            {
                if (routes.Any(line => !line.Contains(
                        expectedAuthority,
                        StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "The candidate routed a transaction outside the authoritative Cosmos endpoint.");
                }
                return;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidDataException(
            "The candidate did not emit the required transaction-route telemetry.");
    }

    private static async Task SeedLoadItemsAsync(
        IAmazonDynamoDB client,
        string table,
        int concurrency,
        CancellationToken cancellationToken)
    {
        for (var worker = 0; worker < concurrency; worker++)
        {
            for (var index = 0; index < 5; index++)
            {
                await client.PutItemAsync(
                    new PutItemRequest
                    {
                        TableName = table,
                        Item = new()
                        {
                            ["pk"] = S(Partition),
                            ["sk"] = S($"load-{worker:D2}-seed-{index:D2}"),
                            ["version"] = S("seed"),
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task CreateTableAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        await client.CreateTableAsync(
            new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions =
                [
                    new("pk", ScalarAttributeType.S),
                    new("sk", ScalarAttributeType.S),
                ],
                KeySchema =
                [
                    new("pk", KeyType.HASH),
                    new("sk", KeyType.RANGE),
                ],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            },
            cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await client.DescribeTableAsync(table, cancellationToken)
                    .ConfigureAwait(false);
                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (DynamoDbResourceNotFoundException)
            {
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Table '{table}' did not become active.");
    }

    private static async Task WriteVersionAsync(
        IAmazonDynamoDB client,
        string table,
        IReadOnlyList<string> sortKeys,
        string version,
        CancellationToken cancellationToken)
        => await client.TransactWriteItemsAsync(
            new TransactWriteItemsRequest
            {
                TransactItems = sortKeys
                    .Select(sort => Put(table, Partition, sort, version))
                    .ToList(),
            },
            cancellationToken).ConfigureAwait(false);

    private static async Task<bool> ExistsAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        string sort,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(
            new GetItemRequest
            {
                TableName = table,
                Key = Key(partition, sort),
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);
        return response.Item is { Count: > 0 };
    }

    private static async Task<Dictionary<string, AttributeValue>> ReadAsync(
        IAmazonDynamoDB client,
        string table,
        string partition,
        string sort,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(
            new GetItemRequest
            {
                TableName = table,
                Key = Key(partition, sort),
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);
        Assert.NotEmpty(response.Item);
        return response.Item;
    }

    private static TransactWriteItem Put(
        string table,
        string partition,
        string sort,
        string version) => new()
    {
        Put = new Put
        {
            TableName = table,
            Item = new()
            {
                ["pk"] = S(partition),
                ["sk"] = S(sort),
                ["version"] = S(version),
                ["payload"] = S("aws2azure production-shaped transaction load"),
            },
        },
    };

    private static TransactGetItem Get(
        string table,
        string partition,
        string sort) => new()
    {
        Get = new Get
        {
            TableName = table,
            Key = Key(partition, sort),
            ProjectionExpression = "pk, sk, version",
        },
    };

    private static TransactGetItem GetWithMarker(
        string table,
        string partition,
        string sort) => new()
    {
        Get = new Get
        {
            TableName = table,
            Key = Key(partition, sort),
            ProjectionExpression = "pk, sk, version, marker",
        },
    };

    private static Dictionary<string, AttributeValue> Key(
        string partition,
        string sort) => new()
    {
        ["pk"] = S(partition),
        ["sk"] = S(sort),
    };

    private static AttributeValue S(string value) => new() { S = value };

    private static async Task<RealAzureWorkloadLoadScenario> VerifyScenarioAsync(
        string id,
        string operation,
        string evidenceSource,
        Func<Task> verification)
    {
        var started = Stopwatch.GetTimestamp();
        await verification().ConfigureAwait(false);
        return Scenario(
            id,
            Service,
            operation,
            evidenceSource,
            1,
            0,
            0,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            DateTimeOffset.UtcNow);
    }

    private static List<RealAzureWorkloadLoadSignal> BuildSignals(
        RealAzureWorkloadLoadOperationMeasurement representative,
        double durationSeconds,
        IReadOnlyCollection<double> networkLatencies,
        long representativeAttempts,
        long representativeThrottles,
        DateTimeOffset loadEnd,
        DateTimeOffset loadWindowEnd) =>
    [
        Signal(
            "representative-load-throughput",
            "representative-load",
            "throughput_per_sec",
            representative.Completions / durationSeconds,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-p95",
            "representative-load",
            "p95_ms",
            representative.P95Milliseconds,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-p99",
            "representative-load",
            "p99_ms",
            representative.P99Milliseconds,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-throttle-rate",
            "representative-load",
            "throttle_rate",
            representativeAttempts == 0
                ? 0
                : (double)representativeThrottles / representativeAttempts,
            representativeAttempts,
            loadEnd),
        Signal(
            "representative-load-unauthenticated-connectivity-header-p95",
            "representative-load",
            "p95_ms",
            Percentile(networkLatencies, 0.95),
            networkLatencies.Count,
            loadWindowEnd),
    ];

    private static bool IsThrottle(Exception exception) =>
        exception is ProvisionedThroughputExceededException;
}
