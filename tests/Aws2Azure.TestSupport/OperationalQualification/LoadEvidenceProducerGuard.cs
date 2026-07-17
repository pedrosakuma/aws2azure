namespace Aws2Azure.TestSupport.OperationalQualification;

public sealed record LoadOperationOutcome(
    string Operation,
    long Completions,
    long Failures,
    string? FirstFailure);

public static class LoadEvidenceProducerGuard
{
    public static async Task PublishAsync(
        long completedIterations,
        IReadOnlyCollection<LoadOperationOutcome> operations,
        string diagnostics,
        Func<Task> publish)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(publish);
        Validate(completedIterations, operations, diagnostics);
        await publish().ConfigureAwait(false);
    }

    private static void Validate(
        long completedIterations,
        IReadOnlyCollection<LoadOperationOutcome> operations,
        string diagnostics)
    {
        if (completedIterations <= 0)
        {
            throw new InvalidDataException(
                "The production-shaped load completed no full CRUD iterations.");
        }

        var totalCompletions = operations.Sum(item => item.Completions);
        if (totalCompletions <= 0)
        {
            throw new InvalidDataException(
                "The production-shaped load completed no operations.");
        }

        var totalFailures = operations.Sum(item => item.Failures);
        if (totalFailures > 0)
        {
            var failures = string.Join(
                ", ",
                operations
                    .Where(item => item.Failures > 0)
                    .Select(item =>
                        $"{item.Operation}={item.Failures} ({item.FirstFailure})"));
            throw new InvalidDataException(
                $"{totalFailures} of {totalCompletions + totalFailures} operations failed." +
                $"{Environment.NewLine}{failures}" +
                $"{Environment.NewLine}{diagnostics}");
        }

        var incompleteOperation = operations.FirstOrDefault(item => item.Completions <= 0);
        if (incompleteOperation is not null)
        {
            throw new InvalidDataException(
                $"{incompleteOperation.Operation} completed no requests.");
        }
    }
}
