using System.Reflection;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class PerfIsolationTests
{
    [Fact]
    public void Assembly_disables_parallel_test_collections()
    {
        var behavior = typeof(PerfIsolationTests).Assembly.GetCustomAttribute<CollectionBehaviorAttribute>();

        Assert.NotNull(behavior);
        Assert.True(behavior.DisableTestParallelization);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task Isolated_scenario_preserves_all_concurrent_workers(int concurrency)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var workerMask = 0;
        var scenario = "test.isolated-worker-concurrency";

        var result = await PerfRunner.RunAsync(
            scenario: scenario,
            concurrency: concurrency,
            duration: TimeSpan.FromSeconds(30),
            maxAttempts: concurrency,
            action: async (worker, token) =>
            {
                Interlocked.Or(ref workerMask, 1 << worker);
                if (Interlocked.Increment(ref entered) == concurrency)
                    allEntered.TrySetResult();
                await allEntered.Task.WaitAsync(token);
            },
            cancellationToken: timeout.Token);

        Assert.Equal(concurrency, entered);
        Assert.Equal((1 << concurrency) - 1, workerMask);
        Assert.Equal(concurrency, result.Completed);
        Assert.Equal(0, result.Failures);
        Assert.Equal(concurrency, result.Concurrency);
    }
}
