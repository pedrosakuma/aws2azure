namespace Aws2Azure.PerfTests;

internal static class S3PerfSetup
{
    private const int DefaultMaxAttempts = 8;

    public static async Task ExecuteWithThrottleRetryAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        delay ??= static (duration, token) => Task.Delay(duration, token);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                PerfThrottle.IsThrottle(exception))
            {
                var delayMilliseconds = Math.Min(2_000, 100 << (attempt - 1));
                await delay(
                    TimeSpan.FromMilliseconds(delayMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
