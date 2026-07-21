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
public sealed class SqsRealAzureLoadQualificationTests(RealAzureProxyFixture fixture)
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
    /// Representative-load worker: each of the <c>concurrency</c> workers
    /// owns a private AMQP-default queue (the namespace default transport)
    /// and repeatedly exercises every profile operation — CreateQueue once,
    /// then SendMessage → ReceiveMessage (long polling, <c>WaitTimeSeconds
    /// = 5</c>) → GetQueueUrl → ListQueues → DeleteMessage in a loop, and
    /// DeleteQueue at the end — so the operation mix matches the full
    /// seven-operation profile rather than only its read/write hot path.
    /// </summary>
    private static async Task RunWorkerAsync(
        IAmazonSQS client,
        RealAzureWorkloadLoadTracker tracker,
        CompletedIterationCounter completedIterations,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var queueName = $"a2a-load-{worker:x2}-{Guid.NewGuid():N}"[..40];
        var queueCreated = false;
        var iteration = 0;
        string? queueUrl = null;
        try
        {
            await MeasureAsync(tracker, "CreateQueue", async () =>
            {
                queueUrl = (await client.CreateQueueAsync(
                    new CreateQueueRequest { QueueName = queueName },
                    cancellationToken).ConfigureAwait(false)).QueueUrl;
            }, IsThrottle).ConfigureAwait(false);
            queueCreated = true;

            while (stopwatch.Elapsed < duration)
            {
                completedIterations.RecordStarted();
                var body = $"aws2azure production-shaped SQS load worker-{worker:D2} " +
                    $"item-{iteration++:D8} {new string('x', 512)}";
                try
                {
                    await MeasureAsync(tracker, "SendMessage", async () =>
                    {
                        await client.SendMessageAsync(
                            new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body },
                            cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);

                    string? receiptHandle = null;
                    await MeasureAsync(tracker, "ReceiveMessage", async () =>
                    {
                        var response = await client.ReceiveMessageAsync(
                            new ReceiveMessageRequest
                            {
                                QueueUrl = queueUrl,
                                MaxNumberOfMessages = 1,
                                WaitTimeSeconds = 5,
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (response.Messages is not { Count: > 0 } messages
                            || !string.Equals(messages[0].Body, body, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "ReceiveMessage did not return the loaded message.");
                        }
                        receiptHandle = messages[0].ReceiptHandle;
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "GetQueueUrl", async () =>
                    {
                        var response = await client.GetQueueUrlAsync(
                            new GetQueueUrlRequest { QueueName = queueName },
                            cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(response.QueueUrl, queueUrl, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("GetQueueUrl returned an unexpected queue.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "ListQueues", async () =>
                    {
                        // ListQueues is documented by AWS as potentially
                        // eventually consistent shortly after a queue is
                        // created/deleted, and Azure Service Bus's management
                        // listing endpoint can lag briefly behind a queue's
                        // own data-plane availability. Tolerate a short,
                        // bounded propagation delay rather than treating a
                        // transient miss as a hard load-run failure.
                        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                        while (true)
                        {
                            var response = await client.ListQueuesAsync(
                                new ListQueuesRequest { QueueNamePrefix = queueName, MaxResults = 2 },
                                cancellationToken).ConfigureAwait(false);
                            if (response.QueueUrls is not null && response.QueueUrls.Contains(queueUrl))
                            {
                                return;
                            }
                            if (DateTimeOffset.UtcNow >= deadline)
                            {
                                throw new InvalidDataException(
                                    "ListQueues did not return the worker's queue.");
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await completedIterations.CompleteAfterAsync(() => MeasureAsync(
                        tracker,
                        "DeleteMessage",
                        async () =>
                        {
                            await client.DeleteMessageAsync(
                                new DeleteMessageRequest
                                {
                                    QueueUrl = queueUrl,
                                    ReceiptHandle = receiptHandle,
                                },
                                cancellationToken).ConfigureAwait(false);
                        },
                        IsThrottle)).ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            await MeasureAsync(tracker, "DeleteQueue", async () =>
            {
                await client.DeleteQueueAsync(
                    new DeleteQueueRequest { QueueUrl = queueUrl },
                    cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            queueCreated = false;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (queueCreated && queueUrl is not null)
            {
                try
                {
                    await client.DeleteQueueAsync(
                        new DeleteQueueRequest { QueueUrl = queueUrl },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Forces immediate redelivery via <c>ChangeMessageVisibility(0)</c> —
    /// the SQS "nack now" idiom, which the AMQP path maps to an immediate
    /// Service Bus Abandon (see <c>docs/gaps/sqs/ChangeMessageVisibility.yaml</c>)
    /// — and asserts the redelivered message keeps its body and increments
    /// <c>ApproximateReceiveCount</c>, before settling it.
    /// </summary>
    private static async Task VerifyRedeliveryAsync(
        RealAzureProxyFixture fixture,
        CancellationToken cancellationToken)
    {
        var queueName = "a2a-redelivery-" + Guid.NewGuid().ToString("N")[..16];
        const string body = "aws2azure redelivery canary";
        using var client = fixture.CreateSqsClient();
        var queueUrl = (await client.CreateQueueAsync(
            new CreateQueueRequest { QueueName = queueName },
            cancellationToken).ConfigureAwait(false)).QueueUrl;
        try
        {
            await client.SendMessageAsync(
                new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body },
                cancellationToken).ConfigureAwait(false);

            var first = await ReceiveWithReceiveCountAsync(client, queueUrl, body, cancellationToken)
                .ConfigureAwait(false);

            await client.ChangeMessageVisibilityAsync(
                new ChangeMessageVisibilityRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = first.ReceiptHandle,
                    VisibilityTimeout = 0,
                },
                cancellationToken).ConfigureAwait(false);

            var second = await ReceiveWithReceiveCountAsync(client, queueUrl, body, cancellationToken)
                .ConfigureAwait(false);
            if (second.ReceiveCount <= first.ReceiveCount)
            {
                throw new InvalidDataException(
                    "ApproximateReceiveCount did not increase after forced redelivery.");
            }

            await client.DeleteMessageAsync(
                new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = second.ReceiptHandle,
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await client.DeleteQueueAsync(
                    new DeleteQueueRequest { QueueUrl = queueUrl },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task<(string ReceiptHandle, int ReceiveCount)> ReceiveWithReceiveCountAsync(
        IAmazonSQS client,
        string queueUrl,
        string expectedBody,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var response = await client.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 5,
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Messages is { Count: > 0 } messages)
            {
                var message = messages[0];
                if (!string.Equals(message.Body, expectedBody, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Redelivery scenario received the wrong body.");
                }
                var receiveCount = message.Attributes is { } attributes
                    && attributes.TryGetValue("ApproximateReceiveCount", out var raw)
                    && int.TryParse(raw, out var parsed)
                    ? parsed
                    : throw new InvalidDataException(
                        "ReceiveMessage did not return ApproximateReceiveCount.");
                return (message.ReceiptHandle, receiveCount);
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "No message received for the redelivery scenario within the deadline.");
            }
        }
    }

    /// <summary>
    /// A fixed batch of uniquely-tagged messages is produced onto one
    /// shared queue, then <paramref name="consumers"/> parallel consumers
    /// race to receive and settle them. Asserts every message is consumed
    /// exactly once — no message is lost and no two consumers observe the
    /// same delivery — proving multi-consumer safety on a shared queue,
    /// distinct from representative-load's per-worker private queues.
    /// </summary>
    private static async Task<(long Completions, long Failures, double DurationSeconds)>
        VerifyConcurrencyAsync(
            RealAzureProxyFixture fixture,
            int consumers,
            int messageCount,
            CancellationToken cancellationToken)
    {
        var queueName = "a2a-concurrency-" + Guid.NewGuid().ToString("N")[..16];
        using var client = fixture.CreateSqsClient();
        var queueUrl = (await client.CreateQueueAsync(
            new CreateQueueRequest { QueueName = queueName },
            cancellationToken).ConfigureAwait(false)).QueueUrl;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var ids = Enumerable.Range(0, messageCount)
                .Select(_ => Guid.NewGuid().ToString("N"))
                .ToArray();
            await Task.WhenAll(ids.Select(id => client.SendMessageAsync(
                new SendMessageRequest { QueueUrl = queueUrl, MessageBody = id },
                cancellationToken))).ConfigureAwait(false);

            var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var duplicates = 0L;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));

            var consumerTasks = Enumerable.Range(0, consumers).Select(async _ =>
            {
                while (seen.Count < ids.Length && !deadline.IsCancellationRequested)
                {
                    ReceiveMessageResponse response;
                    try
                    {
                        response = await client.ReceiveMessageAsync(
                            new ReceiveMessageRequest
                            {
                                QueueUrl = queueUrl,
                                MaxNumberOfMessages = 10,
                                WaitTimeSeconds = 2,
                            },
                            deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        return;
                    }
                    if (response.Messages is not { Count: > 0 } messages)
                    {
                        continue;
                    }
                    foreach (var message in messages)
                    {
                        if (!seen.TryAdd(message.Body, 0))
                        {
                            Interlocked.Increment(ref duplicates);
                        }
                        await client.DeleteMessageAsync(
                            new DeleteMessageRequest
                            {
                                QueueUrl = queueUrl,
                                ReceiptHandle = message.ReceiptHandle,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }).ToArray();
            await Task.WhenAll(consumerTasks).ConfigureAwait(false);
            stopwatch.Stop();

            if (seen.Count != ids.Length)
            {
                throw new InvalidDataException(
                    $"Concurrency scenario consumed {seen.Count} of {ids.Length} messages " +
                    "before the deadline.");
            }
            if (duplicates > 0)
            {
                throw new InvalidDataException(
                    $"Concurrency scenario observed {duplicates} duplicate deliveries across " +
                    $"{consumers} consumers.");
            }
            return (seen.Count, 0, stopwatch.Elapsed.TotalSeconds);
        }
        finally
        {
            try
            {
                await client.DeleteQueueAsync(
                    new DeleteQueueRequest { QueueUrl = queueUrl },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Lightweight REST-transport counterpart of representative-load,
    /// against the fixed <see cref="RealAzureProxyFixture.SqsRestLaneQueueName"/>
    /// override. Kept short and separate — non-required, report-only
    /// evidence that the REST path functions independently of the AMQP
    /// default (issue #626). Purges any stale message left behind by a
    /// prior run (e.g. an undeliverable rollback-rest canary) before
    /// measuring, and never throws: a mismatch or timeout is recorded as a
    /// failure rather than aborting the whole load run, so this
    /// supplementary scenario can never take down the required evidence
    /// produced earlier in the same test.
    /// </summary>
    private static async Task<(long Completions, long Failures, double DurationSeconds)>
        VerifyRestRepresentativeAsync(
            RealAzureProxyFixture fixture,
            int iterations,
            CancellationToken cancellationToken)
    {
        using var client = fixture.CreateSqsClient();
        // The REST-lane queue is shared and intentionally never deleted
        // within a run (see RealAzureRollbackQualification, which purges
        // rather than deletes it) — creating it again here when the
        // rollback scenario already created it earlier in the same run is
        // an ordinary idempotent re-create, not a conflict. Fall back to
        // resolving the existing queue's URL rather than failing the whole
        // load run if Service Bus ever reports a transient attribute
        // mismatch on that idempotent re-create.
        string queueUrl;
        try
        {
            queueUrl = (await client.CreateQueueAsync(
                new CreateQueueRequest { QueueName = RealAzureProxyFixture.SqsRestLaneQueueName },
                cancellationToken).ConfigureAwait(false)).QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            queueUrl = (await client.GetQueueUrlAsync(
                new GetQueueUrlRequest { QueueName = RealAzureProxyFixture.SqsRestLaneQueueName },
                cancellationToken).ConfigureAwait(false)).QueueUrl;
        }
        await PurgeRestLaneQueueAsync(client, queueUrl, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var completions = 0L;
        var failures = 0L;
        for (var i = 0; i < iterations; i++)
        {
            var body = "aws2azure rest-lane load " + Guid.NewGuid().ToString("N");
            try
            {
                await client.SendMessageAsync(
                    new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body },
                    cancellationToken).ConfigureAwait(false);

                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
                ReceiveMessageResponse? received = null;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var response = await client.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = queueUrl,
                            MaxNumberOfMessages = 1,
                            WaitTimeSeconds = 5,
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (response.Messages is { Count: > 0 })
                    {
                        received = response;
                        break;
                    }
                }
                if (received?.Messages is not { Count: > 0 } receivedMessages
                    || !string.Equals(receivedMessages[0].Body, body, StringComparison.Ordinal))
                {
                    failures++;
                    continue;
                }

                await client.DeleteMessageAsync(
                    new DeleteMessageRequest
                    {
                        QueueUrl = queueUrl,
                        ReceiptHandle = receivedMessages[0].ReceiptHandle,
                    },
                    cancellationToken).ConfigureAwait(false);
                completions++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures++;
            }
        }
        stopwatch.Stop();
        return (completions, failures, stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Drains any message left in the shared REST-lane queue by a prior run
    /// (e.g. an undeliverable rollback-rest canary — see
    /// <see cref="RealAzureRollbackQualification.VerifySqsAsync"/>) before
    /// this scenario starts, so a stale delivery can never be mistaken for
    /// the message this scenario just sent.
    /// </summary>
    private static async Task PurgeRestLaneQueueAsync(
        IAmazonSQS client,
        string queueUrl,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await client.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1,
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Messages is not { Count: > 0 } messages)
            {
                return;
            }
            foreach (var message in messages)
            {
                try
                {
                    await client.DeleteMessageAsync(
                        new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = message.ReceiptHandle,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static List<RealAzureWorkloadLoadSignal> BuildRepresentativeLoadSignals(
        IReadOnlyList<RealAzureWorkloadLoadOperationMeasurement> operationMix,
        long completedIterations,
        long startedIterations,
        long totalCompletions,
        long totalAttempts,
        double durationSeconds,
        long representativeAttempts,
        long representativeThrottles,
        DateTimeOffset loadEnd)
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
            "CreateQueue" => "representative-load-create-queue",
            "GetQueueUrl" => "representative-load-get-queue-url",
            "ListQueues" => "representative-load-list-queues",
            "SendMessage" => "representative-load-send-message",
            "ReceiveMessage" => "representative-load",
            "DeleteMessage" => "representative-load-delete-message",
            "DeleteQueue" => "representative-load-delete-queue",
            _ => throw new InvalidDataException(
                $"No stable diagnostic signal prefix is defined for '{operation}'."),
        };
    }

    private static bool IsThrottle(Exception exception)
    {
        return exception is AmazonSQSException aws
               && (aws.StatusCode == HttpStatusCode.TooManyRequests
                   || string.Equals(aws.ErrorCode, "ServiceUnavailable", StringComparison.Ordinal));
    }
}
