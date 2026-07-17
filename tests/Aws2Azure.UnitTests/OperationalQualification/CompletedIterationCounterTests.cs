using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.UnitTests.OperationalQualification;

public sealed class CompletedIterationCounterTests
{
    [Fact]
    public async Task CompleteAfterAsync_counts_only_after_the_final_required_operation()
    {
        var counter = new CompletedIterationCounter();
        counter.RecordStarted();

        await Assert.ThrowsAsync<InvalidOperationException>(() => counter.CompleteAfterAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("partial iteration");
        }));

        Assert.Equal(0, counter.Count);
        Assert.Equal(1, counter.StartedCount);

        counter.RecordStarted();
        await counter.CompleteAfterAsync(static () => Task.CompletedTask);

        Assert.Equal(1, counter.Count);
        Assert.Equal(2, counter.StartedCount);
    }
}
