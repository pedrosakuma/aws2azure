using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;

namespace Aws2Azure.IntegrationTests.Sqs;

public sealed partial class SqsRealAzureLoadQualificationTests
{
    private static async Task PurgeRestLaneQueueAsync(
        IAmazonSQS client,
        string queueUrl,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await client.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1,
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Messages is not { Count: > 0 } messages)
            {
                return;
            }
            foreach (var message in messages)
            {
                try
                {
                    await client.DeleteMessageAsync(
                        new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = message.ReceiptHandle,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static List<RealAzureWorkloadLoadSignal> BuildRepresentativeLoadSignals(
        IReadOnlyList<RealAzureWorkloadLoadOperationMeasurement> operationMix,
        long completedIterations,
        long startedIterations,
        long totalCompletions,
        long totalAttempts,
        double durationSeconds,
        long representativeAttempts,
        long representativeThrottles,
        DateTimeOffset loadEnd)
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
            "CreateQueue" => "representative-load-create-queue",
            "GetQueueUrl" => "representative-load-get-queue-url",
            "ListQueues" => "representative-load-list-queues",
            "SendMessage" => "representative-load-send-message",
            "ReceiveMessage" => "representative-load",
            "DeleteMessage" => "representative-load-delete-message",
            "DeleteQueue" => "representative-load-delete-queue",
            _ => throw new InvalidDataException(
                $"No stable diagnostic signal prefix is defined for '{operation}'."),
        };
    }

    private static bool IsThrottle(Exception exception)
    {
        return exception is AmazonSQSException aws
               && (aws.StatusCode == HttpStatusCode.TooManyRequests
                   || string.Equals(aws.ErrorCode, "ServiceUnavailable", StringComparison.Ordinal));
    }
}

