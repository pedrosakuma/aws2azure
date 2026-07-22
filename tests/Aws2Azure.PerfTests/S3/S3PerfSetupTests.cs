using System.Net;
using Amazon.Runtime;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class S3PerfSetupTests
{
    [Fact]
    public async Task Retries_throttles_with_bounded_backoff()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        await S3PerfSetup.ExecuteWithThrottleRetryAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw SlowDown();
                }

                return Task.CompletedTask;
            },
            maxAttempts: 4,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        Assert.Equal(3, attempts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)],
            delays);
    }

    [Fact]
    public async Task Does_not_retry_non_throttle_failures()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            S3PerfSetup.ExecuteWithThrottleRetryAsync(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("translation failure");
                },
                delay: static (_, _) => Task.CompletedTask));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Throws_final_throttle_after_retry_limit()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<AmazonServiceException>(() =>
            S3PerfSetup.ExecuteWithThrottleRetryAsync(
                _ =>
                {
                    attempts++;
                    throw SlowDown();
                },
                maxAttempts: 3,
                delay: static (_, _) => Task.CompletedTask));

        Assert.Equal(3, attempts);
    }

    private static AmazonServiceException SlowDown() =>
        new(
            "Reduce your request rate.",
            null,
            ErrorType.Receiver,
            "SlowDown",
            "request-id",
            HttpStatusCode.ServiceUnavailable);
}
