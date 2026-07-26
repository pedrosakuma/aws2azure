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

    public static async Task PublishAsync(
        long completedIterations,
        IReadOnlyCollection<LoadOperationOutcome> operations,
        IReadOnlyCollection<string> requiredScenarioIds,
        IReadOnlyCollection<string> completedScenarioIds,
        int rollbackProofCount,
        string diagnostics,
        Func<Task> publish)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(requiredScenarioIds);
        ArgumentNullException.ThrowIfNull(completedScenarioIds);
        ArgumentNullException.ThrowIfNull(publish);
        Validate(completedIterations, operations, diagnostics);
        ValidateScenarioCoverage(
            requiredScenarioIds,
            completedScenarioIds,
            rollbackProofCount);
        await publish().ConfigureAwait(false);
    }

    public static void ValidateTransactionQualificationContext(
        string? profile,
        string? runtimeMode,
        string? storedProcedureMode,
        bool sealedCandidateConfigured,
        bool sealedRollbackConfigured,
        string? candidateDigest,
        string? priorDigest)
    {
        if (!string.Equals(
                profile,
                "dynamodb-single-partition-transactions",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Transaction load evidence requires the dynamodb-single-partition-transactions profile.");
        }
        if (!string.Equals(runtimeMode, "rollback", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Transaction load evidence requires sealed rollback mode.");
        }
        if (!string.Equals(storedProcedureMode, "Preferred", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Transaction load evidence requires stored procedures Preferred.");
        }
        if (!sealedCandidateConfigured || !sealedRollbackConfigured)
        {
            throw new InvalidDataException(
                "Transaction load evidence requires exact sealed candidate and prior runtimes.");
        }
        if (string.IsNullOrWhiteSpace(candidateDigest)
            || string.IsNullOrWhiteSpace(priorDigest)
            || string.Equals(candidateDigest, priorDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Transaction load evidence requires distinct candidate and prior runtimes.");
        }
    }

    private static void Validate(
        long completedIterations,
        IReadOnlyCollection<LoadOperationOutcome> operations,
        string diagnostics)
    {
        if (completedIterations <= 0)
        {
            throw new InvalidDataException(
                "The production-shaped load completed no full workload iterations.");
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

    private static void ValidateScenarioCoverage(
        IReadOnlyCollection<string> requiredScenarioIds,
        IReadOnlyCollection<string> completedScenarioIds,
        int rollbackProofCount)
    {
        var required = new HashSet<string>(requiredScenarioIds, StringComparer.Ordinal);
        if (required.Count != requiredScenarioIds.Count)
        {
            throw new InvalidDataException(
                "Required load-evidence scenario ids must be unique.");
        }

        var completed = new HashSet<string>(completedScenarioIds, StringComparer.Ordinal);
        if (completed.Count != completedScenarioIds.Count
            || !completed.SetEquals(required))
        {
            throw new InvalidDataException(
                "Load evidence must complete every required scenario exactly once.");
        }
        if (rollbackProofCount != 1)
        {
            throw new InvalidDataException(
                "Load evidence must contain exactly one genuine rollback proof.");
        }
    }
}
