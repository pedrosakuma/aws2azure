using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "RealAzure")]
[Trait("Category", "KinesisLoadQualification")]
[Collection(RealAzureCollection.Name)]
public sealed class KinesisRealAzureLoadQualificationTests(RealAzureProxyFixture fixture)
{
    private const string Service = "kinesis";
    private static readonly string[] Operations =
    [
        "DescribeStream",
        "DescribeStreamSummary",
        "ListShards",
        "GetShardIterator",
        "GetRecords",
    ];

    [SkippableFact]
    public async Task Production_shaped_single_consumer_writes_immutable_load_evidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("AWS2AZURE_LOAD_EVIDENCE_PATH");
        Skip.If(string.IsNullOrWhiteSpace(outputPath),
            "AWS2AZURE_LOAD_EVIDENCE_PATH is not set.");
        Skip.IfNot(fixture.EventHubsConfigured,
            "AZURE_EVENTHUBS_* / AZURE_EVENTHUBS_STREAM not set — skipping real-Azure Kinesis load.");

        var fullOutputPath = ResolveOutputPath(outputPath!);
        File.Delete(fullOutputPath);
        File.Delete($"{fullOutputPath}.pending");
        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 1);
        if (concurrency != 1)
        {
            throw new InvalidDataException(
                "The single-consumer-per-shard qualification profile requires concurrency=1.");
        }
        var requestedDuration = TimeSpan.FromSeconds(
            ReadPositiveInt("AWS2AZURE_LOAD_DURATION_SECONDS", 300));
        var tracker = new RealAzureWorkloadLoadTracker(Service, Operations);
        var completedIterations = new CompletedIterationCounter();
        var windowStart = DateTimeOffset.UtcNow;
        using var client = fixture.CreateKinesisClient();
        using var timeout = new CancellationTokenSource(requestedDuration + TimeSpan.FromMinutes(15));

        var target = await KinesisTestHelpers.ResolvePartitionTargetAsync(
            client, fixture.EventHubStream, timeout.Token).ConfigureAwait(false);
        await MeasureAsync(tracker, "DescribeStream", async () =>
        {
            var response = await client.DescribeStreamAsync(new DescribeStreamRequest
            {
                StreamName = fixture.EventHubStream,
            }, timeout.Token).ConfigureAwait(false);
            if (!response.StreamDescription.Shards.Any(shard => shard.ShardId == target.ShardId))
            {
                throw new InvalidDataException("DescribeStream omitted the selected shard.");
            }
        }, IsThrottle).ConfigureAwait(false);
        await MeasureAsync(tracker, "DescribeStreamSummary", async () =>
        {
            var response = await client.DescribeStreamSummaryAsync(
                new DescribeStreamSummaryRequest { StreamName = fixture.EventHubStream },
                timeout.Token).ConfigureAwait(false);
            if (response.StreamDescriptionSummary.OpenShardCount <= 0)
            {
                throw new InvalidDataException(
                    "DescribeStreamSummary reported no open shards.");
            }
        }, IsThrottle).ConfigureAwait(false);
        await MeasureAsync(tracker, "ListShards", async () =>
        {
            var response = await client.ListShardsAsync(new ListShardsRequest
            {
                StreamName = fixture.EventHubStream,
                MaxResults = 100,
            }, timeout.Token).ConfigureAwait(false);
            if (!response.Shards.Any(shard => shard.ShardId == target.ShardId))
            {
                throw new InvalidDataException("ListShards omitted the selected shard.");
            }
        }, IsThrottle).ConfigureAwait(false);

        string currentIterator = string.Empty;
        await MeasureAsync(tracker, "GetShardIterator", async () =>
        {
            var response = await client.GetShardIteratorAsync(new GetShardIteratorRequest
            {
                StreamName = fixture.EventHubStream,
                ShardId = target.ShardId,
                ShardIteratorType = "LATEST",
            }, timeout.Token).ConfigureAwait(false);
            currentIterator = response.ShardIterator;
        }, IsThrottle).ConfigureAwait(false);
        await MeasureAsync(tracker, "GetRecords", async () =>
        {
            var response = await client.GetRecordsAsync(new GetRecordsRequest
            {
                ShardIterator = currentIterator,
                Limit = 100,
            }, timeout.Token).ConfigureAwait(false);
            currentIterator = response.NextShardIterator;
        }, IsThrottle).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var iteration = 0;
        while (stopwatch.Elapsed < requestedDuration)
        {
            completedIterations.RecordStarted();
            var payload = $"consumer-load-{iteration++:D8}-{Guid.NewGuid():N}";
            using (var data = new MemoryStream(Encoding.UTF8.GetBytes(payload)))
            {
                await client.PutRecordAsync(new PutRecordRequest
                {
                    StreamName = fixture.EventHubStream,
                    PartitionKey = target.PartitionKey,
                    Data = data,
                }, timeout.Token).ConfigureAwait(false);
            }

            var found = false;
            while (!found && stopwatch.Elapsed < requestedDuration)
            {
                await MeasureAsync(tracker, "GetRecords", async () =>
                {
                    var response = await client.GetRecordsAsync(new GetRecordsRequest
                    {
                        ShardIterator = currentIterator,
                        Limit = 100,
                    }, timeout.Token).ConfigureAwait(false);
                    currentIterator = response.NextShardIterator;
                    found = response.Records.Any(
                        record => KinesisTestHelpers.Utf8(record) == payload);
                }, IsThrottle).ConfigureAwait(false);
            }

            if (found)
            {
                await completedIterations.CompleteAfterAsync(
                    static () => Task.CompletedTask).ConfigureAwait(false);
            }
        }
        stopwatch.Stop();
        var loadEnd = DateTimeOffset.UtcNow;

        var operationMix = tracker.Snapshot();
        var operationOutcomes = operationMix
            .Select(item => new LoadOperationOutcome(
                item.Operation,
                item.Completions,
                item.Failures,
                tracker.FirstFailure(item.Operation)))
            .ToArray();
        var representative = tracker.Snapshot("GetRecords");
        var scenarios = new List<RealAzureWorkloadLoadScenario>
        {
            Scenario(
                "representative-load",
                Service,
                "GetRecords",
                "real_azure",
                representative.Completions,
                representative.Failures,
                0,
                stopwatch.Elapsed.TotalSeconds,
                loadEnd),
            await VerifyScenarioAsync(
                "topology",
                "DescribeStreamSummary",
                "real_azure",
                () => VerifyTopologyAsync(client, timeout.Token)).ConfigureAwait(false),
            await VerifyScenarioAsync(
                "pagination",
                "ListShards",
                "real_azure",
                () => VerifyPaginationAsync(client, timeout.Token)).ConfigureAwait(false),
            await VerifyScenarioAsync(
                "iterator-types",
                "GetShardIterator",
                "real_azure",
                () => VerifyIteratorTypesAsync(client, target, timeout.Token)).ConfigureAwait(false),
            await VerifyScenarioAsync(
                "empty-reads",
                "GetRecords",
                "real_azure",
                () => VerifyEmptyReadAsync(client, target, timeout.Token)).ConfigureAwait(false),
            await VerifyScenarioAsync(
                "progression",
                "GetRecords",
                "real_azure",
                () => VerifyProgressionAsync(client, target, timeout.Token)).ConfigureAwait(false),
            await VerifyScenarioAsync(
                "iterator-expiry",
                "GetRecords",
                "deterministic",
                KinesisConsumerDeterministicQualification.VerifyIteratorExpiryAsync)
                .ConfigureAwait(false),
            await VerifyScenarioAsync(
                "cancellation",
                "GetRecords",
                "deterministic",
                KinesisConsumerDeterministicQualification.VerifyCancellationAsync)
                .ConfigureAwait(false),
            await VerifyScenarioAsync(
                "restart",
                "GetRecords",
                "real_azure",
                () => RealAzureRestartQualification.VerifyKinesisAsync(fixture))
                .ConfigureAwait(false),
            await VerifyScenarioAsync(
                "retry-boundaries",
                "GetRecords",
                "deterministic",
                KinesisConsumerDeterministicQualification.VerifyRetryBoundariesAsync)
                .ConfigureAwait(false),
        };

        RealAzureRollbackResult? rollback = null;
        if (fixture.SealedRollbackConfigured)
        {
            rollback = await RealAzureRollbackQualification.VerifyKinesisAsync(
                fixture,
                timeout.Token).ConfigureAwait(false);
            scenarios.Add(Scenario(
                "rollback",
                Service,
                "GetRecords",
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
                "GetRecords",
                "real_azure",
                0,
                0,
                1,
                0,
                DateTimeOffset.UtcNow));
        }

        var windowEnd = DateTimeOffset.UtcNow;
        var latencies = tracker.Latencies("GetRecords");
        var signals = new List<RealAzureWorkloadLoadSignal>
        {
            Signal(
                "representative-load-throughput",
                "representative-load",
                "throughput_per_sec",
                representative.Completions / stopwatch.Elapsed.TotalSeconds,
                representative.Completions,
                loadEnd),
            Signal(
                "representative-load-p95",
                "representative-load",
                "p95_ms",
                Percentile(latencies, 0.95),
                latencies.Length,
                loadEnd),
            Signal(
                "representative-load-p99",
                "representative-load",
                "p99_ms",
                Percentile(latencies, 0.99),
                latencies.Length,
                loadEnd),
        };

        var evidence = new RealAzureWorkloadLoadEvidence
        {
            SchemaVersion = 1,
            Profile = new RealAzureWorkloadLoadProfile
            {
                Id = "kinesis-single-consumer-per-shard",
                Version = 1,
                Services =
                [
                    new RealAzureWorkloadLoadProfileService
                    {
                        Service = Service,
                        Operations = Operations.ToList(),
                    },
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
            RollbackProofs = rollback is null ? [] : [rollback.Proof],
        };

        await LoadEvidenceProducerGuard.PublishAsync(
            completedIterations.Count,
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

    private async Task VerifyTopologyAsync(
        IAmazonKinesis client,
        CancellationToken cancellationToken)
    {
        var describe = await client.DescribeStreamAsync(
            new DescribeStreamRequest { StreamName = fixture.EventHubStream },
            cancellationToken).ConfigureAwait(false);
        var summary = await client.DescribeStreamSummaryAsync(
            new DescribeStreamSummaryRequest { StreamName = fixture.EventHubStream },
            cancellationToken).ConfigureAwait(false);
        if (describe.StreamDescription.Shards.Count == 0
            || describe.StreamDescription.Shards.Count
            != summary.StreamDescriptionSummary.OpenShardCount)
        {
            throw new InvalidDataException(
                "Kinesis topology operations disagree on the Event Hubs partition count.");
        }
    }

    private async Task VerifyPaginationAsync(
        IAmazonKinesis client,
        CancellationToken cancellationToken)
    {
        var shardIds = new HashSet<string>(StringComparer.Ordinal);
        string? nextToken = null;
        var pages = 0;
        do
        {
            var response = await client.ListShardsAsync(new ListShardsRequest
            {
                StreamName = nextToken is null ? fixture.EventHubStream : null,
                NextToken = nextToken,
                MaxResults = 1,
            }, cancellationToken).ConfigureAwait(false);
            pages++;
            foreach (var shard in response.Shards)
            {
                if (!shardIds.Add(shard.ShardId))
                {
                    throw new InvalidDataException(
                        "ListShards pagination returned a duplicate shard.");
                }
            }
            nextToken = response.NextToken;
        } while (!string.IsNullOrWhiteSpace(nextToken));

        if (pages < 2)
        {
            throw new InvalidDataException(
                "The qualification Event Hub needs at least two partitions.");
        }
    }

    private async Task VerifyIteratorTypesAsync(
        IAmazonKinesis client,
        (string ShardId, string PartitionKey) target,
        CancellationToken cancellationToken)
    {
        foreach (var type in new[] { "TRIM_HORIZON", "LATEST" })
        {
            var response = await client.GetShardIteratorAsync(new GetShardIteratorRequest
            {
                StreamName = fixture.EventHubStream,
                ShardId = target.ShardId,
                ShardIteratorType = type,
            }, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ShardIterator))
            {
                throw new InvalidDataException($"{type} returned no shard iterator.");
            }
        }

        var boundary = DateTimeOffset.UtcNow;
        var timestamp = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "AT_TIMESTAMP",
            Timestamp = KinesisTestHelpers.ToSdkTimestamp(boundary),
        }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(timestamp.ShardIterator))
        {
            throw new InvalidDataException("AT_TIMESTAMP returned no shard iterator.");
        }
    }

    private async Task VerifyEmptyReadAsync(
        IAmazonKinesis client,
        (string ShardId, string PartitionKey) target,
        CancellationToken cancellationToken)
    {
        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "LATEST",
        }, cancellationToken).ConfigureAwait(false);
        var response = await client.GetRecordsAsync(new GetRecordsRequest
        {
            ShardIterator = iterator.ShardIterator,
            Limit = 1,
        }, cancellationToken).ConfigureAwait(false);
        if (response.Records.Count != 0
            || string.IsNullOrWhiteSpace(response.NextShardIterator))
        {
            throw new InvalidDataException(
                "A primed LATEST iterator did not produce an empty read with a continuation.");
        }
    }

    private async Task VerifyProgressionAsync(
        IAmazonKinesis client,
        (string ShardId, string PartitionKey) target,
        CancellationToken cancellationToken)
    {
        var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
        {
            StreamName = fixture.EventHubStream,
            ShardId = target.ShardId,
            ShardIteratorType = "LATEST",
        }, cancellationToken).ConfigureAwait(false);
        var current = await KinesisTestHelpers.PrimeIteratorAsync(
            client, iterator.ShardIterator, cancellationToken).ConfigureAwait(false);
        var payloads = new[]
        {
            "progress-a-" + Guid.NewGuid().ToString("N"),
            "progress-b-" + Guid.NewGuid().ToString("N"),
        };
        foreach (var payload in payloads)
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            await client.PutRecordAsync(new PutRecordRequest
            {
                StreamName = fixture.EventHubStream,
                PartitionKey = target.PartitionKey,
                Data = data,
            }, cancellationToken).ConfigureAwait(false);
        }

        var seen = new List<string>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTimeOffset.UtcNow < deadline && seen.Count < payloads.Length)
        {
            var response = await client.GetRecordsAsync(new GetRecordsRequest
            {
                ShardIterator = current,
                Limit = 100,
            }, cancellationToken).ConfigureAwait(false);
            current = response.NextShardIterator;
            foreach (var record in response.Records)
            {
                var value = KinesisTestHelpers.Utf8(record);
                if (payloads.Contains(value, StringComparer.Ordinal))
                {
                    seen.Add(value);
                }
            }
        }

        if (!seen.SequenceEqual(payloads, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "GetRecords did not preserve independent iterator progression and record order.");
        }
    }

    private static bool IsThrottle(Exception exception)
        => exception is ProvisionedThroughputExceededException;
}
