using System.Net;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Deterministic tests for <see cref="PerfThrottle.IsThrottle(Exception)"/> — the
/// predicate that separates backend backpressure (HTTP 429) from genuine
/// proxy/transport defects so the perf harness doesn't red an A/B run on expected
/// serverless throttling (issue #456). The classification must be precise: real
/// failures (5xx, translation faults) must NOT be swallowed as throttling.
/// </summary>
public class PerfThrottleTests
{
    [Fact]
    public void Http_429_status_is_throttle()
    {
        var ex = new AmazonServiceException(
            "rate too large", null, ErrorType.Sender, "Whatever", "req-1", HttpStatusCode.TooManyRequests);

        Assert.True(PerfThrottle.IsThrottle(ex));
    }

    [Fact]
    public void Throttle_error_code_is_throttle_even_without_429_status()
    {
        var ex = new AmazonServiceException(
            "slow down", null, ErrorType.Sender, "ThrottlingException", "req-2", HttpStatusCode.BadRequest);

        Assert.True(PerfThrottle.IsThrottle(ex));
    }

    [Fact]
    public void DynamoDb_provisioned_throughput_exceeded_is_throttle()
    {
        // The exact type the AWS SDK surfaces when the proxy relays a Cosmos 429.
        var ex = new ProvisionedThroughputExceededException("Request rate is large");

        Assert.True(PerfThrottle.IsThrottle(ex));
    }

    [Fact]
    public void Cosmos_request_rate_message_on_plain_exception_is_throttle()
    {
        // Wrapped / non-AWS exception that only carries the textual throttle marker.
        var ex = new InvalidOperationException(
            "Message: {\"Errors\":[\"Request rate is large. More Request Units may be needed...\"]}");

        Assert.True(PerfThrottle.IsThrottle(ex));
    }

    [Fact]
    public void S3_slow_down_is_throttle()
    {
        var ex = new AmazonServiceException(
            "Reduce your request rate.", null, ErrorType.Sender, "SlowDown", "req-s3",
            HttpStatusCode.ServiceUnavailable);

        Assert.True(PerfThrottle.IsThrottle(ex));
    }

    [Fact]
    public void Server_error_5xx_is_not_throttle()
    {
        var ex = new AmazonServiceException(
            "internal error", null, ErrorType.Receiver, "InternalServerError", "req-3", HttpStatusCode.InternalServerError);

        Assert.False(PerfThrottle.IsThrottle(ex));
    }

    [Fact]
    public void Generic_failure_is_not_throttle()
    {
        Assert.False(PerfThrottle.IsThrottle(new InvalidOperationException("boom — translation fault")));
    }

    [Fact]
    public void Throttle_nested_in_inner_exception_is_detected()
    {
        var throttle = new AmazonServiceException(
            "rate", null, ErrorType.Sender, "x", "req-4", HttpStatusCode.TooManyRequests);
        var wrapped = new InvalidOperationException("wrapper", throttle);

        Assert.True(PerfThrottle.IsThrottle(wrapped));
    }

    [Fact]
    public void Throttle_inside_aggregate_is_detected()
    {
        var throttle = new ProvisionedThroughputExceededException("Request rate is large");
        var aggregate = new AggregateException(new Exception("benign"), throttle);

        Assert.True(PerfThrottle.IsThrottle(aggregate));
    }

    [Fact]
    public void Aggregate_of_only_genuine_failures_is_not_throttle()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("a"), new TimeoutException("b"));

        Assert.False(PerfThrottle.IsThrottle(aggregate));
    }

    [Fact]
    public void Null_is_not_throttle()
    {
        Assert.False(PerfThrottle.IsThrottle((Exception?)null));
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(200, false)]
    public void Status_code_overload_only_flags_429(int status, bool expected)
    {
        Assert.Equal(expected, PerfThrottle.IsThrottle((HttpStatusCode)status));
    }
}
