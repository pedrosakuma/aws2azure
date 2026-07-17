using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.S3;

[Trait("Category", "RealAzure")]
[Trait("Category", "S3LoadQualification")]
[Collection(RealAzureCollection.Name)]
public sealed class S3RealAzureLoadQualificationTests(RealAzureProxyFixture fixture)
{
    private const string Service = "s3";
    private static readonly string[] Operations =
    [
        "CreateBucket",
        "PutObject",
        "GetObject",
        "HeadObject",
        "ListObjectsV2",
        "DeleteObject",
        "DeleteBucket",
    ];

    [SkippableFact]
    public async Task Production_shaped_object_crud_writes_immutable_load_evidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("AWS2AZURE_LOAD_EVIDENCE_PATH");
        Skip.If(string.IsNullOrWhiteSpace(outputPath),
            "AWS2AZURE_LOAD_EVIDENCE_PATH is not set.");
        Skip.IfNot(fixture.BlobConfigured,
            "AZURE_BLOB_ACCOUNT/AZURE_BLOB_KEY not set — skipping real-Azure S3 load.");

        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 8);
        var requestedDuration = TimeSpan.FromSeconds(
            ReadPositiveInt("AWS2AZURE_LOAD_DURATION_SECONDS", 300));
        var tracker = new RealAzureWorkloadLoadTracker(Service, Operations);
        var blobEndpoint = RequiredEnvironment("AZURE_BLOB_ENDPOINT");
        var networkTarget = new Uri(new Uri(blobEndpoint), "?comp=list");
        var windowStart = DateTimeOffset.UtcNow;
        var networkBefore = await ProbeNetworkAsync(networkTarget, 12).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        using var client = fixture.CreateS3Client();
        using var timeout = new CancellationTokenSource(requestedDuration + TimeSpan.FromMinutes(10));

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
        var operationMix = tracker.Snapshot();
        var totalCompletions = operationMix.Sum(item => item.Completions);
        var totalFailures = operationMix.Sum(item => item.Failures);
        var representative = tracker.Snapshot("GetObject");
        var representativeAttempts = representative.Completions + representative.Failures;
        var representativeLatencies = tracker.Latencies("GetObject");
        var networkLatencies = networkBefore.Concat(networkAfter).ToArray();
        var scenarios = new List<RealAzureWorkloadLoadScenario>
        {
            Scenario(
                "representative-load",
                Service,
                "GetObject",
                "real_azure",
                representative.Completions,
                representative.Failures,
                0,
                stopwatch.Elapsed.TotalSeconds,
                loadEnd),
        };
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.ThrottlingScenarioId,
            "GetObject",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyS3ScenarioAsync(
                DeterministicFailureQualification.ThrottlingScenarioId)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.TimeoutScenarioId,
            "GetObject",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyS3ScenarioAsync(
                DeterministicFailureQualification.TimeoutScenarioId)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.ServiceUnavailableScenarioId,
            "GetObject",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyS3ScenarioAsync(
                DeterministicFailureQualification.ServiceUnavailableScenarioId))
            .ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.CancellationScenarioId,
            "GetObject",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyS3ScenarioAsync(
                DeterministicFailureQualification.CancellationScenarioId)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            "restart",
            "PutObject",
            "real_azure",
            () => RealAzureRestartQualification.VerifyS3Async(fixture)).ConfigureAwait(false));
        scenarios.Add(await VerifyScenarioAsync(
            DeterministicFailureQualification.RetryExhaustionScenarioId,
            "PutObject",
            "deterministic",
            static () => DeterministicFailureQualification.VerifyS3ScenarioAsync(
                DeterministicFailureQualification.RetryExhaustionScenarioId))
            .ConfigureAwait(false));

        // No sealed previous-artifact deployment target exists to qualify rollback.
        scenarios.Add(Scenario(
            "rollback",
            Service,
            "GetObject",
            "real_azure",
            0,
            0,
            1,
            0,
            DateTimeOffset.UtcNow));
        var windowEnd = DateTimeOffset.UtcNow;

        var evidence = new RealAzureWorkloadLoadEvidence
        {
            SchemaVersion = 1,
            Profile = new RealAzureWorkloadLoadProfile
            {
                Id = "s3-basic-object-crud",
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
            Signals =
            [
                Signal(
                    "representative-load-throughput",
                    "representative-load",
                    "throughput_per_sec",
                    representative.Completions / stopwatch.Elapsed.TotalSeconds,
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-p95",
                    "representative-load",
                    "p95_ms",
                    Percentile(representativeLatencies, 0.95),
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-p99",
                    "representative-load",
                    "p99_ms",
                    Percentile(representativeLatencies, 0.99),
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-network-p95",
                    "representative-load",
                    "p95_ms",
                    Percentile(networkLatencies, 0.95),
                    networkLatencies.LongLength,
                    loadWindowEnd),
                Signal(
                    "representative-load-throttle-rate",
                    "representative-load",
                    "throttle_rate",
                    representativeAttempts == 0
                        ? 0
                        : (double)tracker.Throttles("GetObject") / representativeAttempts,
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

    private static async Task RunWorkerAsync(
        IAmazonS3 client,
        RealAzureWorkloadLoadTracker tracker,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var bucket = $"a2a-load-{worker:x2}-{Guid.NewGuid():N}"[..40];
        var bucketCreated = false;
        var iteration = 0;
        try
        {
            await MeasureAsync(tracker, "CreateBucket", async () =>
            {
                await client.PutBucketAsync(
                    new PutBucketRequest { BucketName = bucket },
                    cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            bucketCreated = true;

            while (stopwatch.Elapsed < duration)
            {
                var key = $"objects/worker-{worker:D2}/item-{iteration++:D8}.txt";
                var payload = $"aws2azure production-shaped S3 load {key} {new string('x', 65_536)}";
                var objectCreated = false;
                try
                {
                    string etag = string.Empty;
                    await MeasureAsync(tracker, "PutObject", async () =>
                    {
                        var request = new PutObjectRequest
                        {
                            BucketName = bucket,
                            Key = key,
                            ContentBody = payload,
                            ContentType = "text/plain",
                        };
                        request.Metadata.Add("x-amz-meta-loadworker", worker.ToString("D2"));
                        var response = await client.PutObjectAsync(
                            request,
                            cancellationToken).ConfigureAwait(false);
                        etag = response.ETag;
                        if (string.IsNullOrWhiteSpace(etag))
                        {
                            throw new InvalidDataException("PutObject returned no ETag.");
                        }
                    }, IsThrottle).ConfigureAwait(false);
                    objectCreated = true;

                    await MeasureAsync(tracker, "HeadObject", async () =>
                    {
                        var response = await client.GetObjectMetadataAsync(
                            new GetObjectMetadataRequest
                            {
                                BucketName = bucket,
                                Key = key,
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (response.ContentLength != payload.Length
                            || !string.Equals(response.ETag, etag, StringComparison.Ordinal)
                            || !string.Equals(
                                response.Metadata["x-amz-meta-loadworker"],
                                worker.ToString("D2"),
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("HeadObject returned an unexpected object shape.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "HeadObject", async () =>
                    {
                        var response = await client.GetObjectMetadataAsync(
                            new GetObjectMetadataRequest
                            {
                                BucketName = bucket,
                                Key = key,
                                EtagToMatch = etag,
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(response.ETag, etag, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("Conditional HeadObject returned the wrong ETag.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "GetObject", async () =>
                    {
                        using var response = await client.GetObjectAsync(
                            new GetObjectRequest { BucketName = bucket, Key = key },
                            cancellationToken).ConfigureAwait(false);
                        using var reader = new StreamReader(response.ResponseStream);
                        var downloaded = await reader.ReadToEndAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.Equals(downloaded, payload, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("GetObject returned the wrong payload.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "GetObject", async () =>
                    {
                        using var response = await client.GetObjectAsync(
                            new GetObjectRequest
                            {
                                BucketName = bucket,
                                Key = key,
                                ByteRange = new ByteRange(17, 80),
                            },
                            cancellationToken).ConfigureAwait(false);
                        using var reader = new StreamReader(response.ResponseStream);
                        var ranged = await reader.ReadToEndAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.Equals(ranged, payload[17..81], StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("Ranged GetObject returned the wrong payload.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "GetObject", async () =>
                    {
                        try
                        {
                            using var response = await client.GetObjectAsync(
                                new GetObjectRequest
                                {
                                    BucketName = bucket,
                                    Key = key,
                                    EtagToNotMatch = etag,
                                },
                                cancellationToken).ConfigureAwait(false);
                            throw new InvalidDataException(
                                "Conditional GetObject unexpectedly returned a body.");
                        }
                        catch (AmazonS3Exception exception)
                            when (exception.StatusCode == HttpStatusCode.NotModified)
                        {
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "ListObjectsV2", async () =>
                    {
                        var response = await client.ListObjectsV2Async(
                            new ListObjectsV2Request
                            {
                                BucketName = bucket,
                                Prefix = key,
                                MaxKeys = 2,
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (response.S3Objects.Count != 1
                            || !string.Equals(response.S3Objects[0].Key, key, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("ListObjectsV2 did not return the loaded object.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "DeleteObject", async () =>
                    {
                        await client.DeleteObjectAsync(
                            new DeleteObjectRequest { BucketName = bucket, Key = key },
                            cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);
                    objectCreated = false;

                    await MeasureAsync(tracker, "DeleteObject", async () =>
                    {
                        await client.DeleteObjectAsync(
                            new DeleteObjectRequest { BucketName = bucket, Key = key },
                            cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    if (objectCreated)
                    {
                        try
                        {
                            await client.DeleteObjectAsync(
                                new DeleteObjectRequest { BucketName = bucket, Key = key },
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            await MeasureAsync(tracker, "DeleteBucket", async () =>
            {
                await client.DeleteBucketAsync(
                    new DeleteBucketRequest { BucketName = bucket },
                    cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            bucketCreated = false;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (bucketCreated)
            {
                await DeleteBucketBestEffortAsync(client, bucket).ConfigureAwait(false);
            }
        }
    }

    private static bool IsThrottle(Exception exception)
    {
        return exception is AmazonS3Exception aws
               && (aws.StatusCode == HttpStatusCode.TooManyRequests
                   || string.Equals(aws.ErrorCode, "SlowDown", StringComparison.Ordinal));
    }

    private static async Task DeleteBucketBestEffortAsync(IAmazonS3 client, string bucket)
    {
        try
        {
            string? continuationToken = null;
            do
            {
                var listed = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    ContinuationToken = continuationToken,
                }).ConfigureAwait(false);
                foreach (var item in listed.S3Objects)
                {
                    try
                    {
                        await client.DeleteObjectAsync(new DeleteObjectRequest
                        {
                            BucketName = bucket,
                            Key = item.Key,
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                continuationToken = listed.IsTruncated == true
                    ? listed.NextContinuationToken
                    : null;
            } while (continuationToken is not null);
        }
        catch
        {
        }

        try
        {
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket })
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
