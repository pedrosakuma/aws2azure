using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.DynamoDb;

public sealed partial class DynamoDbRealAzureLoadQualificationTests
{
    private static async Task WaitForTableActiveAsync(
        IAmazonDynamoDB client,
        string table,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var description = await client.DescribeTableAsync(table, cancellationToken)
                    .ConfigureAwait(false);
                if (description.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<RealAzureWorkloadLoadSignal> BuildRepresentativeLoadSignals(
        IReadOnlyList<RealAzureWorkloadLoadOperationMeasurement> operationMix,
        long completedIterations,
        long startedIterations,
        long totalCompletions,
        long totalAttempts,
        double durationSeconds,
        IReadOnlyCollection<double> networkLatencies,
        long representativeAttempts,
        long representativeThrottles,
        DateTimeOffset loadEnd,
        DateTimeOffset loadWindowEnd)
    {
        var signals = new List<RealAzureWorkloadLoadSignal>
        {
            Signal(
                "crud-iterations-per-sec",
                "representative-load",
                "throughput_per_sec",
                completedIterations / durationSeconds,
                startedIterations,
                loadEnd),
            Signal(
                "aws-operations-per-sec",
                "representative-load",
                "throughput_per_sec",
                totalCompletions / durationSeconds,
                totalAttempts,
                loadEnd),
        };

        foreach (var operation in operationMix)
        {
            var prefix = OperationSignalPrefix(operation.Operation);
            var attempts = operation.Completions + operation.Failures;
            signals.Add(Signal(
                $"{prefix}-throughput",
                "representative-load",
                "throughput_per_sec",
                operation.Completions / durationSeconds,
                attempts,
                loadEnd));
            signals.Add(Signal(
                $"{prefix}-p95",
                "representative-load",
                "p95_ms",
                operation.P95Milliseconds,
                attempts,
                loadEnd));
            signals.Add(Signal(
                $"{prefix}-p99",
                "representative-load",
                "p99_ms",
                operation.P99Milliseconds,
                attempts,
                loadEnd));
        }

        signals.Add(Signal(
            "representative-load-unauthenticated-connectivity-header-p95",
            "representative-load",
            "p95_ms",
            Percentile(networkLatencies, 0.95),
            networkLatencies.Count,
            loadWindowEnd));
        signals.Add(Signal(
            "representative-load-throttle-rate",
            "representative-load",
            "throttle_rate",
            representativeAttempts == 0
                ? 0
                : (double)representativeThrottles / representativeAttempts,
            representativeAttempts,
            loadEnd));
        return signals;
    }

    private static string OperationSignalPrefix(string operation)
    {
        return operation switch
        {
            "CreateTable" => "representative-load-create-table",
            "DescribeTable" => "representative-load-describe-table",
            "PutItem" => "representative-load-put-item",
            "GetItem" => "representative-load",
            "UpdateItem" => "representative-load-update-item",
            "DeleteItem" => "representative-load-delete-item",
            "DeleteTable" => "representative-load-delete-table",
            _ => throw new InvalidDataException(
                $"No stable diagnostic signal prefix is defined for '{operation}'."),
        };
    }

    private static bool IsThrottle(Exception exception)
    {
        return exception is ProvisionedThroughputExceededException;
    }
}
