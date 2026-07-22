using Xunit;

namespace Aws2Azure.PerfTests;

public class PerfRunnerWarmupTests
{
    [Fact]
    public async Task Warmup_deadline_waits_for_in_flight_action_before_measurement()
    {
        var timeProvider = new ManualTimeProvider();
        var warmupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmupToken = CancellationToken.None;
        var warmupReturned = false;
        var warmupObservedCancellation = false;
        var measurementStartedBeforeWarmupReturned = false;
        var warmupCalls = 0;
        var scenario = "test.warmup-deadline";

        var runTask = PerfRunner.RunAsync(
            scenario: scenario,
            concurrency: 1,
            duration: TimeSpan.FromSeconds(1),
            warmup: TimeSpan.FromSeconds(1),
            maxOps: 1,
            action: async (workerId, token) =>
            {
                if (workerId == -1)
                {
                    Interlocked.Increment(ref warmupCalls);
                    warmupToken = token;
                    warmupStarted.TrySetResult();
                    try
                    {
                        await releaseWarmup.Task.WaitAsync(token);
                        warmupReturned = true;
                    }
                    catch (OperationCanceledException)
                    {
                        warmupObservedCancellation = true;
                        throw;
                    }
                    return;
                }

                measurementStartedBeforeWarmupReturned = !warmupReturned;
            },
            timeProvider: timeProvider);

        await warmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        Assert.False(warmupToken.IsCancellationRequested);
        Assert.False(runTask.IsCompleted);

        releaseWarmup.TrySetResult();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, warmupCalls);
        Assert.False(warmupObservedCancellation);
        Assert.True(warmupReturned);
        Assert.False(measurementStartedBeforeWarmupReturned);
        Assert.Equal(1, result.Completed);
    }

    [Fact]
    public async Task Caller_cancellation_cancels_in_flight_warmup_action()
    {
        var timeProvider = new ManualTimeProvider();
        var warmupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCts = new CancellationTokenSource();
        var observedWarmupWorkerId = int.MinValue;
        var measurementStarted = false;
        var scenario = "test.warmup-caller-cancellation";

        var runTask = PerfRunner.RunAsync(
            scenario: scenario,
            concurrency: 1,
            duration: TimeSpan.FromSeconds(1),
            warmup: TimeSpan.FromSeconds(1),
            maxOps: 1,
            action: async (workerId, token) =>
            {
                if (workerId != -1)
                {
                    measurementStarted = true;
                    return;
                }

                observedWarmupWorkerId = workerId;
                warmupStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            cancellationToken: callerCts.Token,
            timeProvider: timeProvider);

        await warmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(-1, observedWarmupWorkerId);
        Assert.False(measurementStarted);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_when_final_warmup_action_returns_normally()
    {
        var timeProvider = new ManualTimeProvider();
        using var callerCts = new CancellationTokenSource();
        var measurementStarted = false;
        var scenario = "test.warmup-caller-cancellation-after-return";

        var runTask = PerfRunner.RunAsync(
            scenario: scenario,
            concurrency: 1,
            duration: TimeSpan.FromSeconds(1),
            warmup: TimeSpan.FromSeconds(1),
            maxOps: 1,
            action: (workerId, _) =>
            {
                if (workerId == -1)
                {
                    timeProvider.Advance(TimeSpan.FromSeconds(2));
                    callerCts.Cancel();
                }
                else
                {
                    measurementStarted = true;
                }

                return Task.CompletedTask;
            },
            cancellationToken: callerCts.Token,
            timeProvider: timeProvider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.False(measurementStarted);
    }

    [Fact]
    public async Task Measurement_deadline_waits_for_in_flight_action()
    {
        var timeProvider = new ManualTimeProvider();
        var measurementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMeasurement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measurementObservedCancellation = false;
        var scenario = "test.measurement-cancellation";

        var runTask = PerfRunner.RunAsync(
            scenario: scenario,
            concurrency: 1,
            duration: TimeSpan.FromSeconds(1),
            warmup: TimeSpan.Zero,
            action: async (_, token) =>
            {
                measurementStarted.TrySetResult();
                try
                {
                    await releaseMeasurement.Task.WaitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    measurementObservedCancellation = true;
                    throw;
                }
            },
            timeProvider: timeProvider);

        await measurementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        Assert.False(runTask.IsCompleted);

        releaseMeasurement.TrySetResult();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(measurementObservedCancellation);
        Assert.Equal(1, result.Completed);
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public async Task Caller_cancellation_cancels_in_flight_measurement_action()
    {
        var measurementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCts = new CancellationTokenSource();
        var scenario = "test.measurement-caller-cancellation";

        var runTask = PerfRunner.RunAsync(
            scenario: scenario,
            concurrency: 1,
            duration: TimeSpan.FromMinutes(1),
            warmup: TimeSpan.Zero,
            action: async (_, token) =>
            {
                measurementStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            cancellationToken: callerCts.Token);

        await measurementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) =>
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
