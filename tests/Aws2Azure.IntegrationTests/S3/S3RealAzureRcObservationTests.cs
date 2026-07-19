using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.S3;

[Trait("Category", "RealAzure")]
[Trait("Category", "S3RcObservation")]
[Collection(RealAzureCollection.Name)]
public sealed class S3RealAzureRcObservationTests(RealAzureProxyFixture fixture)
{
    private static readonly string[] Operations =
    [
        "CreateBucket",
        "PutObject",
        "HeadObject",
        "GetObject",
        "ListObjectsV2",
        "DeleteObject",
        "DeleteBucket",
    ];

    [SkippableFact]
    public async Task Candidate_and_stable_cohorts_capture_object_crud_and_exact_prior_restore()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            "AWS2AZURE_RC_OBSERVATION_CAPTURE_PATH")),
            "AWS2AZURE_RC_OBSERVATION_CAPTURE_PATH is not set.");
        Skip.IfNot(fixture.BlobConfigured,
            "Real Azure Blob Storage is not configured.");
        Assert.True(fixture.SealedRollbackConfigured,
            "RC observation requires exact candidate and prior sealed runtimes.");

        var minutes = RcObservationCaptureWriter.ReadWindowMinutes();
        var concurrency = RcObservationCaptureWriter.ReadConcurrency();
        var duration = TimeSpan.FromMinutes(minutes);
        using var timeout = new CancellationTokenSource(duration + TimeSpan.FromMinutes(15));
        var candidateTracker = new RealAzureWorkloadLoadTracker("s3", Operations);
        var stableTracker = new RealAzureWorkloadLoadTracker("s3", Operations);
        var stable = await fixture.StartAdditionalRuntimeAsync(SealedRuntimeRole.Prior)
            .ConfigureAwait(false);
        Assert.NotEqual(fixture.S3ServiceUrl, stable.ServiceUrl);
        using var candidateClient = fixture.CreateS3Client();
        using var stableClient = fixture.CreateS3Client(stable.ServiceUrl);
        var canaryBucket = "a2a-rc-canary-" + Guid.NewGuid().ToString("N")[..24];
        const string canaryKey = "state.txt";
        var canaryValue = "rc-observation-" + Guid.NewGuid().ToString("N");
        var canaryExists = false;

        try
        {
            await candidateClient.PutBucketAsync(
                new PutBucketRequest { BucketName = canaryBucket },
                timeout.Token).ConfigureAwait(false);
            await candidateClient.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = canaryBucket,
                    Key = canaryKey,
                    ContentBody = canaryValue,
                    ContentType = "text/plain",
                },
                timeout.Token).ConfigureAwait(false);
            canaryExists = true;
            await AssertValueAsync(
                candidateClient,
                canaryBucket,
                canaryKey,
                canaryValue,
                timeout.Token).ConfigureAwait(false);

            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var workers = new List<Task>(concurrency * 2);
            for (var worker = 0; worker < concurrency; worker++)
            {
                workers.Add(RunWorkerAsync(
                    candidateClient,
                    candidateTracker,
                    "candidate",
                    worker,
                    duration,
                    stopwatch,
                    timeout.Token));
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

            var restorationStartedAt = DateTimeOffset.UtcNow;
            await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
            await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
            using var restoredClient = fixture.CreateS3Client();
            await AssertValueAsync(
                restoredClient,
                canaryBucket,
                canaryKey,
                canaryValue,
                timeout.Token).ConfigureAwait(false);
            await restoredClient.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = canaryBucket,
                    Key = canaryKey,
                },
                timeout.Token).ConfigureAwait(false);
            await restoredClient.DeleteBucketAsync(
                new DeleteBucketRequest { BucketName = canaryBucket },
                timeout.Token).ConfigureAwait(false);
            await AssertBucketAbsentAsync(
                restoredClient,
                canaryBucket,
                timeout.Token).ConfigureAwait(false);
            canaryExists = false;
            var restorationVerifiedAt = DateTimeOffset.UtcNow;

            var candidateGet = candidateTracker.Snapshot("GetObject");
            var stableGet = stableTracker.Snapshot("GetObject");
            var candidateAttempts = RcObservationCaptureWriter.TotalAttempts(
                candidateTracker);
            var stableAttempts = RcObservationCaptureWriter.TotalAttempts(stableTracker);
            var evidence = new RcObservationCaptureEvidence
            {
                Profile = new RcObservationCaptureProfile
                {
                    Id = "s3-basic-object-crud",
                    Version = 1,
                },
                Azure = new RcObservationCaptureAzure
                {
                    BackendKind = "blob",
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
                Cohorts =
                [
                    Cohort(
                        "candidate",
                        fixture.CandidateRuntimeIdentityDigest,
                        fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
                        startedAt,
                        restorationStartedAt,
                        concurrency,
                        fixture.BackendIdentityDigest,
                        fixture.ProxyConfigDigest,
                        fixture.AwsBindingDigest,
                        fixture.S3ServiceUrl,
                        candidateTracker),
                    Cohort(
                        "stable",
                        fixture.PriorRuntimeIdentityDigest,
                        fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
                        startedAt,
                        restorationVerifiedAt,
                        concurrency,
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
                Restoration = new RcObservationCaptureRestoration
                {
                    Verified = true,
                    RuntimeIdentityDigest = fixture.PriorRuntimeIdentityDigest,
                    RuntimeDigest = fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
                    BackendIdentityDigest = fixture.BackendIdentityDigest,
                    ConfigDigest = fixture.ProxyConfigDigest,
                    AwsBindingDigest = fixture.AwsBindingDigest,
                    StartedAtUtc = restorationStartedAt,
                    VerifiedAtUtc = restorationVerifiedAt,
                },
            };
            await RcObservationCaptureWriter.PublishAsync(evidence).ConfigureAwait(false);
        }
        finally
        {
            await fixture.StopAdditionalRuntimeAsync(stable).ConfigureAwait(false);
            if (canaryExists)
            {
                try
                {
                    using var cleanup = fixture.CreateS3Client(maxErrorRetry: 0);
                    await cleanup.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = canaryBucket,
                        Key = canaryKey,
                    }, CancellationToken.None).ConfigureAwait(false);
                    await cleanup.DeleteBucketAsync(new DeleteBucketRequest
                    {
                        BucketName = canaryBucket,
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
        BackendKind = "blob",
        Region = RequiredEnvironment("AZURE_LOCATION"),
        BackendIdentityDigest = backendIdentityDigest,
        ConfigDigest = configDigest,
        AwsBindingDigest = awsBindingDigest,
        ObservedFromUtc = startedAt,
        ObservedUntilUtc = endedAt,
        MemberDigests = Enumerable.Range(0, concurrency)
            .Select(worker => RcObservationCaptureWriter.MemberDigest(
                "s3-basic-object-crud",
                role,
                worker,
                endpoint))
            .ToList(),
        OperationDiagnostics = RcObservationCaptureWriter.OperationDiagnostics(tracker),
    };

    private static async Task RunWorkerAsync(
        IAmazonS3 client,
        RealAzureWorkloadLoadTracker tracker,
        string role,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var bucket = $"a2a-rc-{role[..1]}-{worker:x2}-{Guid.NewGuid():N}"[..40];
        var bucketCreated = false;
        var iteration = 0;
        try
        {
            await MeasureAsync(
                tracker,
                "CreateBucket",
                () => client.PutBucketAsync(
                    new PutBucketRequest { BucketName = bucket },
                    cancellationToken),
                IsThrottle).ConfigureAwait(false);
            bucketCreated = true;

            while (stopwatch.Elapsed < duration)
            {
                var key = $"objects/{worker:D2}/{iteration++:D8}.txt";
                var payload =
                    $"aws2azure RC {role} S3 observation {key} {new string('x', 65_536)}";
                var objectCreated = false;
                try
                {
                    var etag = string.Empty;
                    await MeasureAsync(
                        tracker,
                        "PutObject",
                        async () =>
                        {
                            var request = new PutObjectRequest
                            {
                                BucketName = bucket,
                                Key = key,
                                ContentBody = payload,
                                ContentType = "text/plain",
                            };
                            request.Metadata.Add(
                                "x-amz-meta-observationmember",
                                $"{role}-{worker:D2}");
                            var response = await client.PutObjectAsync(
                                request,
                                cancellationToken).ConfigureAwait(false);
                            etag = response.ETag;
                            if (string.IsNullOrWhiteSpace(etag))
                            {
                                throw new InvalidDataException(
                                    "PutObject returned no ETag.");
                            }
                        },
                        IsThrottle).ConfigureAwait(false);
                    objectCreated = true;
                    await MeasureAsync(
                        tracker,
                        "HeadObject",
                        async () =>
                        {
                            var response = await client.GetObjectMetadataAsync(
                                new GetObjectMetadataRequest
                                {
                                    BucketName = bucket,
                                    Key = key,
                                },
                                cancellationToken).ConfigureAwait(false);
                            if (response.ContentLength != payload.Length
                                || !string.Equals(
                                    response.ETag,
                                    etag,
                                    StringComparison.Ordinal)
                                || !string.Equals(
                                    response.Metadata["x-amz-meta-observationmember"],
                                    $"{role}-{worker:D2}",
                                    StringComparison.Ordinal))
                            {
                                throw new InvalidDataException(
                                    "HeadObject returned an unexpected object shape.");
                            }
                        },
                        IsThrottle).ConfigureAwait(false);
                    await MeasureAsync(
                        tracker,
                        "HeadObject",
                        async () =>
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
                                throw new InvalidDataException(
                                    "Conditional HeadObject returned the wrong ETag.");
                            }
                        },
                        IsThrottle).ConfigureAwait(false);
                    await MeasureAsync(
                        tracker,
                        "GetObject",
                        () => AssertValueAsync(
                            client,
                            bucket,
                            key,
                            payload,
                            cancellationToken),
                        IsThrottle).ConfigureAwait(false);
                    await MeasureAsync(
                        tracker,
                        "GetObject",
                        async () =>
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
                            if (!string.Equals(
                                    ranged,
                                    payload[17..81],
                                    StringComparison.Ordinal))
                            {
                                throw new InvalidDataException(
                                    "Ranged GetObject returned the wrong payload.");
                            }
                        },
                        IsThrottle).ConfigureAwait(false);
                    await MeasureAsync(
                        tracker,
                        "GetObject",
                        async () =>
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
                        },
                        IsThrottle).ConfigureAwait(false);
                    await MeasureAsync(
                        tracker,
                        "ListObjectsV2",
                        async () =>
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
                                || response.S3Objects[0].Key != key)
                            {
                                throw new InvalidDataException(
                                    "ListObjectsV2 did not return the cohort object.");
                            }
                        },
                        IsThrottle).ConfigureAwait(false);
                    await MeasureAsync(
                        tracker,
                        "DeleteObject",
                        () => client.DeleteObjectAsync(
                            new DeleteObjectRequest
                            {
                                BucketName = bucket,
                                Key = key,
                            },
                            cancellationToken),
                        IsThrottle).ConfigureAwait(false);
                    objectCreated = false;
                    await MeasureAsync(
                        tracker,
                        "DeleteObject",
                        () => client.DeleteObjectAsync(
                            new DeleteObjectRequest
                            {
                                BucketName = bucket,
                                Key = key,
                            },
                            cancellationToken),
                        IsThrottle).ConfigureAwait(false);
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
                                new DeleteObjectRequest
                                {
                                    BucketName = bucket,
                                    Key = key,
                                },
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            await MeasureAsync(
                tracker,
                "DeleteBucket",
                () => client.DeleteBucketAsync(
                    new DeleteBucketRequest { BucketName = bucket },
                    cancellationToken),
                IsThrottle).ConfigureAwait(false);
            bucketCreated = false;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (bucketCreated)
            {
                try
                {
                    await client.DeleteBucketAsync(
                        new DeleteBucketRequest { BucketName = bucket },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task AssertValueAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        string expected,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(
            new GetObjectRequest { BucketName = bucket, Key = key },
            cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(response.ResponseStream);
        var actual = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (actual != expected)
        {
            throw new InvalidDataException("GetObject returned the wrong cohort value.");
        }
    }

    private static async Task AssertBucketAbsentAsync(
        IAmazonS3 client,
        string bucket,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            try
            {
                await client.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception exception)
                when (exception.StatusCode == HttpStatusCode.NotFound
                      || exception.ErrorCode == "NoSuchBucket")
            {
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "Exact-prior restoration did not remove the S3 canary.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsThrottle(Exception exception) =>
        exception is AmazonS3Exception aws
        && (aws.StatusCode == HttpStatusCode.TooManyRequests
            || aws.ErrorCode is "SlowDown" or "Throttling");

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{name} is required.")
            : value;
    }
}
