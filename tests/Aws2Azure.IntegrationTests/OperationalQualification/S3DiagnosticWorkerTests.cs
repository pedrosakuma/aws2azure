using System.Diagnostics;
using System.Net;
using System.Text;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Aws2Azure.IntegrationTests.S3;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class S3DiagnosticWorkerTests
{
    [Fact]
    public async Task Strict_worker_reuses_full_range_conditional_and_double_delete_semantics()
    {
        var clock = Stopwatch.StartNew();
        using var client = new Client(clock);
        var tracker = new RealAzureWorkloadLoadTracker("s3", S3RealAzureRcObservationTests.Operations);
        var iterations = new CompletedIterationCounter();
        await S3RealAzureRcObservationTests.RunWorkerAsync(client, tracker, "diagnostic", 0,
            TimeSpan.FromMilliseconds(100), clock, CancellationToken.None, true, iterations);
        Assert.Equal(S3Crossover.OperationSchedule, client.Calls);
        Assert.Equal(3, tracker.Snapshot("GetObject").Completions);
        Assert.Equal(2, tracker.Snapshot("DeleteObject").Completions);
        Assert.Equal(1, iterations.Count);
        Assert.Equal(iterations.StartedCount, iterations.Count);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("full")]
    [InlineData("range")]
    [InlineData("conditional")]
    [InlineData("list")]
    [InlineData("second-delete")]
    public async Task Strict_failure_propagates_without_repair_or_another_iteration(string failure)
    {
        var clock = Stopwatch.StartNew();
        using var client = new Client(clock, failure);
        var tracker = new RealAzureWorkloadLoadTracker("s3", S3RealAzureRcObservationTests.Operations);
        var iterations = new CompletedIterationCounter();
        await Assert.ThrowsAsync<InvalidDataException>(() => S3RealAzureRcObservationTests.RunWorkerAsync(
            client, tracker, "diagnostic", 0, TimeSpan.FromMilliseconds(100), clock, CancellationToken.None,
            true, iterations));
        Assert.Equal(1, iterations.StartedCount);
        Assert.Equal(0, iterations.Count);
        Assert.DoesNotContain("DeleteBucket", client.Calls);
        Assert.Equal(1, tracker.Snapshot(failure switch
        {
            "head" => "HeadObject", "list" => "ListObjectsV2",
            "second-delete" => "DeleteObject", _ => "GetObject",
        }).Failures);
    }

    [Fact]
    public async Task Legacy_observation_still_records_failures_and_attempts_cleanup_without_throwing()
    {
        var clock = Stopwatch.StartNew();
        using var client = new Client(clock, "head");
        var tracker = new RealAzureWorkloadLoadTracker("s3", S3RealAzureRcObservationTests.Operations);
        await S3RealAzureRcObservationTests.RunWorkerAsync(client, tracker, "candidate", 0,
            TimeSpan.FromMilliseconds(100), clock, CancellationToken.None);
        Assert.True(tracker.Snapshot("HeadObject").Failures > 0);
        Assert.Contains("DeleteObject", client.Calls);
        Assert.Contains("DeleteBucket", client.Calls);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clock = Stopwatch.StartNew();
        using var client = new Client(clock);
        var tracker = new RealAzureWorkloadLoadTracker("s3", S3RealAzureRcObservationTests.Operations);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => S3RealAzureRcObservationTests.RunWorkerAsync(
            client, tracker, "diagnostic", 0, TimeSpan.FromMilliseconds(100), clock, cancellation.Token, true));
        Assert.Empty(client.Calls);
    }

    private sealed class Client(Stopwatch clock, string failure = "") :
        AmazonS3Client("offline", "offline", RegionEndpoint.USEast1)
    {
        internal List<string> Calls { get; } = [];
        private string _payload = "";
        private string _metadata = "";
        private int _deletes;

        public override Task<PutBucketResponse> PutBucketAsync(PutBucketRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("CreateBucket");
            clock.Restart();
            return Task.FromResult(new PutBucketResponse());
        }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("PutObject");
            _payload = request.ContentBody;
            _metadata = request.Metadata["x-amz-meta-observationmember"];
            Assert.Contains("S3 observation", _payload, StringComparison.Ordinal);
            Assert.True(_payload.Length > 65_536);
            return Task.FromResult(new PutObjectResponse { ETag = "\"etag\"" });
        }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("HeadObject");
            var result = new GetObjectMetadataResponse { ETag = "\"etag\"", ContentLength = _payload.Length };
            result.Metadata.Add("x-amz-meta-observationmember", failure == "head" ? "wrong" : _metadata);
            return Task.FromResult(result);
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("GetObject");
            if (request.EtagToNotMatch is not null && failure != "conditional")
                throw new AmazonS3Exception("expected") { StatusCode = HttpStatusCode.NotModified };
            var range = request.ByteRange is not null;
            if (range)
            {
                Assert.Equal(17, request.ByteRange!.Start);
                Assert.Equal(80, request.ByteRange.End);
            }
            var value = failure == (range ? "range" : "full") ? "wrong" : range ? _payload[17..81] : _payload;
            return Task.FromResult(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(value)),
            });
        }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("ListObjectsV2");
            Assert.Equal(2, request.MaxKeys);
            return Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = failure == "list" ? "wrong" : request.Prefix }],
            });
        }

        public override async Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("DeleteObject");
            if (++_deletes == 2)
            {
                if (failure == "second-delete") throw new InvalidDataException("failed idempotent delete");
                await Task.Delay(TimeSpan.FromMilliseconds(110), cancellationToken);
            }
            return new DeleteObjectResponse();
        }

        public override Task<DeleteBucketResponse> DeleteBucketAsync(DeleteBucketRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("DeleteBucket");
            return Task.FromResult(new DeleteBucketResponse());
        }
    }
}
