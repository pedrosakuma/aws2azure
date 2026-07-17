namespace Aws2Azure.TestSupport.OperationalQualification;

public sealed class CompletedIterationCounter
{
    private long _count;
    private long _startedCount;

    public long Count => Interlocked.Read(ref _count);
    public long StartedCount => Interlocked.Read(ref _startedCount);

    public void RecordStarted()
    {
        Interlocked.Increment(ref _startedCount);
    }

    public async Task CompleteAfterAsync(Func<Task> finalRequiredOperation)
    {
        ArgumentNullException.ThrowIfNull(finalRequiredOperation);
        await finalRequiredOperation().ConfigureAwait(false);
        Interlocked.Increment(ref _count);
    }
}
