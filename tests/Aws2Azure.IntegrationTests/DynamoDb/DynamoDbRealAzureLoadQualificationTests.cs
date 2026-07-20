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
public sealed class DynamoDbRealAzureLoadQualificationTests(DynamoDbRealAzureProxyFixture fixture)
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

    /// <summary>
    /// Concurrent conditional writes must admit exactly one winner: N workers
    /// race a <c>PutItem</c> with <c>attribute_not_exists(pk)</c> against the
    /// same key; exactly one succeeds and the rest fail with
    /// <c>ConditionalCheckFailedException</c>.
    /// </summary>
    private static async Task VerifyConditionalWriteConcurrencyAsync(
        IAmazonDynamoDB client,
        CancellationToken cancellationToken)
    {
        var table = "a2a-cwc-" + Guid.NewGuid().ToString("N")[..20];
        const string key = "contested";
        const int racers = 8;
        var tableCreated = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }, cancellationToken).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, cancellationToken).ConfigureAwait(false);

            var results = await Task.WhenAll(Enumerable.Range(0, racers).Select(async racer =>
            {
                try
                {
                    await client.PutItemAsync(new PutItemRequest
                    {
                        TableName = table,
                        Item = new Dictionary<string, AttributeValue>
                        {
                            ["pk"] = new AttributeValue { S = key },
                            ["winner"] = new AttributeValue { N = racer.ToString() },
                        },
                        ConditionExpression = "attribute_not_exists(pk)",
                    }, cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (ConditionalCheckFailedException)
                {
                    return false;
                }
            })).ConfigureAwait(false);

            var winners = results.Count(won => won);
            if (winners != 1)
            {
                throw new InvalidDataException(
                    $"Conditional-write concurrency admitted {winners} winners; expected exactly 1.");
            }

            var final = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = key } },
                ConsistentRead = true,
            }, cancellationToken).ConfigureAwait(false);
            if (!final.IsItemSet)
            {
                throw new InvalidDataException(
                    "Conditional-write concurrency winner did not persist an item.");
            }
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

    /// <summary>
    /// Strong-consistency read-after-write: a <c>PutItem</c> immediately
    /// followed by a <c>ConsistentRead=true</c> <c>GetItem</c> must observe
    /// the write with no propagation delay (issue #627's Strong-consistency
    /// requirement for the CRUD mix).
    /// </summary>
    private static async Task VerifyReadAfterWriteAsync(
        IAmazonDynamoDB client,
        CancellationToken cancellationToken)
    {
        var table = "a2a-raw-" + Guid.NewGuid().ToString("N")[..20];
        const string key = "item";
        var value = "read-after-write-" + Guid.NewGuid().ToString("N");
        var tableCreated = false;
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }, cancellationToken).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, cancellationToken).ConfigureAwait(false);

            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new AttributeValue { S = key },
                    ["payload"] = new AttributeValue { S = value },
                },
            }, cancellationToken).ConfigureAwait(false);

            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = key } },
                ConsistentRead = true,
            }, cancellationToken).ConfigureAwait(false);
            if (!got.IsItemSet || got.Item["payload"].S != value)
            {
                throw new InvalidDataException(
                    "Strong-consistency read-after-write did not observe the immediately preceding write.");
            }
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

    private static async Task RunWorkerAsync(
        IAmazonDynamoDB client,
        RealAzureWorkloadLoadTracker tracker,
        CompletedIterationCounter completedIterations,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var table = $"a2a-load-{worker:x2}-{Guid.NewGuid():N}"[..40];
        var tableCreated = false;
        var iteration = 0;
        try
        {
            await MeasureAsync(tracker, "CreateTable", async () =>
            {
                await client.CreateTableAsync(new CreateTableRequest
                {
                    TableName = table,
                    AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                }, cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table, cancellationToken).ConfigureAwait(false);

            await MeasureAsync(tracker, "DescribeTable", async () =>
            {
                var description = await client.DescribeTableAsync(
                    table,
                    cancellationToken).ConfigureAwait(false);
                if (description.Table.TableStatus != TableStatus.ACTIVE)
                {
                    throw new InvalidDataException("DescribeTable did not report an ACTIVE table.");
                }
            }, IsThrottle).ConfigureAwait(false);

            while (stopwatch.Elapsed < duration)
            {
                completedIterations.RecordStarted();
                var key = $"item-worker-{worker:D2}-{iteration++:D8}";
                var payload = $"aws2azure production-shaped DynamoDB load {key} {new string('x', 4_096)}";
                var itemCreated = false;
                try
                {
                    await MeasureAsync(tracker, "PutItem", async () =>
                    {
                        await client.PutItemAsync(new PutItemRequest
                        {
                            TableName = table,
                            Item = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                                ["payload"] = new AttributeValue { S = payload },
                                ["version"] = new AttributeValue { N = "1" },
                            },
                        }, cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);
                    itemCreated = true;

                    await MeasureAsync(tracker, "GetItem", async () =>
                    {
                        var response = await client.GetItemAsync(new GetItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                            // ConsistentRead=true exercises the Strong-consistency
                            // path this profile requires (issue #627).
                            ConsistentRead = true,
                        }, cancellationToken).ConfigureAwait(false);
                        if (!response.IsItemSet
                            || response.Item["payload"].S != payload)
                        {
                            throw new InvalidDataException("GetItem returned the wrong payload.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "UpdateItem", async () =>
                    {
                        await client.UpdateItemAsync(new UpdateItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                            UpdateExpression = "SET version = version + :one",
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                            {
                                [":one"] = new AttributeValue { N = "1" },
                            },
                        }, cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "GetItem", async () =>
                    {
                        var response = await client.GetItemAsync(new GetItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                            ConsistentRead = true,
                        }, cancellationToken).ConfigureAwait(false);
                        if (!response.IsItemSet || response.Item["version"].N != "2")
                        {
                            throw new InvalidDataException("UpdateItem did not persist the expected version.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "DeleteItem", async () =>
                    {
                        await client.DeleteItemAsync(new DeleteItemRequest
                        {
                            TableName = table,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["pk"] = new AttributeValue { S = key },
                            },
                        }, cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);
                    itemCreated = false;

                    // DeleteItem is idempotent (a repeat delete of an absent item
                    // still succeeds), so wrapping a second call in
                    // CompleteAfterAsync marks "iteration complete" strictly after
                    // a genuine measured backend round trip, decoupled from the
                    // main per-op measurement chain above (mirrors the S3 load
                    // producer's DeleteObject completion marker).
                    await completedIterations.CompleteAfterAsync(() => MeasureAsync(
                        tracker,
                        "DeleteItem",
                        async () =>
                        {
                            await client.DeleteItemAsync(new DeleteItemRequest
                            {
                                TableName = table,
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    ["pk"] = new AttributeValue { S = key },
                                },
                            }, cancellationToken).ConfigureAwait(false);
                        },
                        IsThrottle)).ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    if (itemCreated)
                    {
                        try
                        {
                            await client.DeleteItemAsync(new DeleteItemRequest
                            {
                                TableName = table,
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    ["pk"] = new AttributeValue { S = key },
                                },
                            }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            await MeasureAsync(tracker, "DeleteTable", async () =>
            {
                await client.DeleteTableAsync(
                    new DeleteTableRequest { TableName = table },
                    cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            tableCreated = false;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
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

    private static async Task WaitForTableActiveAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var description = await client.DescribeTableAsync(table, cancellationToken)
                    .ConfigureAwait(false);
                if (description.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<RealAzureWorkloadLoadSignal> BuildRepresentativeLoadSignals(
        IReadOnlyList<RealAzureWorkloadLoadOperationMeasurement> operationMix,
        long completedIterations,
        long startedIterations,
        long totalCompletions,
        long totalAttempts,
        double durationSeconds,
        IReadOnlyCollection<double> networkLatencies,
        long representativeAttempts,
        long representativeThrottles,
        DateTimeOffset loadEnd,
        DateTimeOffset loadWindowEnd)
    {
        var signals = new List<RealAzureWorkloadLoadSignal>
        {
            Signal(
                "crud-iterations-per-sec",
                "representative-load",
                "throughput_per_sec",
                completedIterations / durationSeconds,
                startedIterations,
                loadEnd),
            Signal(
                "aws-operations-per-sec",
                "representative-load",
                "throughput_per_sec",
                totalCompletions / durationSeconds,
                totalAttempts,
                loadEnd),
        };

        foreach (var operation in operationMix)
        {
            var prefix = OperationSignalPrefix(operation.Operation);
            var attempts = operation.Completions + operation.Failures;
            signals.Add(Signal(
                $"{prefix}-throughput",
                "representative-load",
                "throughput_per_sec",
                operation.Completions / durationSeconds,
                attempts,
                loadEnd));
            signals.Add(Signal(
                $"{prefix}-p95",
                "representative-load",
                "p95_ms",
                operation.P95Milliseconds,
                attempts,
                loadEnd));
            signals.Add(Signal(
                $"{prefix}-p99",
                "representative-load",
                "p99_ms",
                operation.P99Milliseconds,
                attempts,
                loadEnd));
        }

        signals.Add(Signal(
            "representative-load-unauthenticated-connectivity-header-p95",
            "representative-load",
            "p95_ms",
            Percentile(networkLatencies, 0.95),
            networkLatencies.Count,
            loadWindowEnd));
        signals.Add(Signal(
            "representative-load-throttle-rate",
            "representative-load",
            "throttle_rate",
            representativeAttempts == 0
                ? 0
                : (double)representativeThrottles / representativeAttempts,
            representativeAttempts,
            loadEnd));
        return signals;
    }

    private static string OperationSignalPrefix(string operation)
    {
        return operation switch
        {
            "CreateTable" => "representative-load-create-table",
            "DescribeTable" => "representative-load-describe-table",
            "PutItem" => "representative-load-put-item",
            "GetItem" => "representative-load",
            "UpdateItem" => "representative-load-update-item",
            "DeleteItem" => "representative-load-delete-item",
            "DeleteTable" => "representative-load-delete-table",
            _ => throw new InvalidDataException(
                $"No stable diagnostic signal prefix is defined for '{operation}'."),
        };
    }

    private static bool IsThrottle(Exception exception)
    {
        return exception is ProvisionedThroughputExceededException;
    }
}
