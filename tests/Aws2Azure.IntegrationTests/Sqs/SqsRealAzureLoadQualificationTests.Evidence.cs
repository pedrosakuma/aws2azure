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
    private static async Task<RealAzureWorkloadLoadScenario> VerifyScenarioAsync(
        string id,
        string operation,
        string evidenceSource,
        Func<Task> verification)
    {
        var started = Stopwatch.GetTimestamp();
        await verification().ConfigureAwait(false);
        return Scenario(
            id,
            Service,
            operation,
            evidenceSource,
            1,
            0,
            0,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Representative-load worker: each of the <c>concurrency</c> workers
    /// owns a private AMQP-default queue (the namespace default transport)
    /// and repeatedly exercises every profile operation — CreateQueue once,
    /// then SendMessage → ReceiveMessage (long polling, <c>WaitTimeSeconds
    /// = 5</c>) → GetQueueUrl → ListQueues → DeleteMessage in a loop, and
    /// DeleteQueue at the end — so the operation mix matches the full
    /// seven-operation profile rather than only its read/write hot path.
    /// </summary>
    private static async Task RunWorkerAsync(
        IAmazonSQS client,
        RealAzureWorkloadLoadTracker tracker,
        CompletedIterationCounter completedIterations,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var queueName = $"a2a-load-{worker:x2}-{Guid.NewGuid():N}"[..40];
        var queueCreated = false;
        var iteration = 0;
        string? queueUrl = null;
        try
        {
            await MeasureAsync(tracker, "CreateQueue", async () =>
            {
                queueUrl = (await client.CreateQueueAsync(
                    new CreateQueueRequest { QueueName = queueName },
                    cancellationToken).ConfigureAwait(false)).QueueUrl;
            }, IsThrottle).ConfigureAwait(false);
            queueCreated = true;

            while (stopwatch.Elapsed < duration)
            {
                completedIterations.RecordStarted();
                var body = $"aws2azure production-shaped SQS load worker-{worker:D2} " +
                    $"item-{iteration++:D8} {new string('x', 512)}";
                try
                {
                    await MeasureAsync(tracker, "SendMessage", async () =>
                    {
                        await client.SendMessageAsync(
                            new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body },
                            cancellationToken).ConfigureAwait(false);
                    }, IsThrottle).ConfigureAwait(false);

                    string? receiptHandle = null;
                    await MeasureAsync(tracker, "ReceiveMessage", async () =>
                    {
                        var response = await client.ReceiveMessageAsync(
                            new ReceiveMessageRequest
                            {
                                QueueUrl = queueUrl,
                                MaxNumberOfMessages = 1,
                                WaitTimeSeconds = 5,
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (response.Messages is not { Count: > 0 } messages
                            || !string.Equals(messages[0].Body, body, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "ReceiveMessage did not return the loaded message.");
                        }
                        receiptHandle = messages[0].ReceiptHandle;
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "GetQueueUrl", async () =>
                    {
                        var response = await client.GetQueueUrlAsync(
                            new GetQueueUrlRequest { QueueName = queueName },
                            cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(response.QueueUrl, queueUrl, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("GetQueueUrl returned an unexpected queue.");
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await MeasureAsync(tracker, "ListQueues", async () =>
                    {
                        // ListQueues is documented by AWS as potentially
                        // eventually consistent shortly after a queue is
                        // created/deleted, and Azure Service Bus's management
                        // listing endpoint can lag briefly behind a queue's
                        // own data-plane availability. Tolerate a short,
                        // bounded propagation delay rather than treating a
                        // transient miss as a hard load-run failure.
                        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                        while (true)
                        {
                            var response = await client.ListQueuesAsync(
                                new ListQueuesRequest { QueueNamePrefix = queueName, MaxResults = 2 },
                                cancellationToken).ConfigureAwait(false);
                            if (response.QueueUrls is not null && response.QueueUrls.Contains(queueUrl))
                            {
                                return;
                            }
                            if (DateTimeOffset.UtcNow >= deadline)
                            {
                                throw new InvalidDataException(
                                    "ListQueues did not return the worker's queue.");
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }, IsThrottle).ConfigureAwait(false);

                    await completedIterations.CompleteAfterAsync(() => MeasureAsync(
                        tracker,
                        "DeleteMessage",
                        async () =>
                        {
                            await client.DeleteMessageAsync(
                                new DeleteMessageRequest
                                {
                                    QueueUrl = queueUrl,
                                    ReceiptHandle = receiptHandle,
                                },
                                cancellationToken).ConfigureAwait(false);
                        },
                        IsThrottle)).ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            await MeasureAsync(tracker, "DeleteQueue", async () =>
            {
                await client.DeleteQueueAsync(
                    new DeleteQueueRequest { QueueUrl = queueUrl },
                    cancellationToken).ConfigureAwait(false);
            }, IsThrottle).ConfigureAwait(false);
            queueCreated = false;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (queueCreated && queueUrl is not null)
            {
                try
                {
                    await client.DeleteQueueAsync(
                        new DeleteQueueRequest { QueueUrl = queueUrl },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Forces immediate redelivery via <c>ChangeMessageVisibility(0)</c> —
    /// the SQS "nack now" idiom, which the AMQP path maps to an immediate
    /// Service Bus Abandon (see <c>docs/gaps/sqs/ChangeMessageVisibility.yaml</c>)
    /// — and asserts the redelivered message keeps its body and increments
    /// <c>ApproximateReceiveCount</c>, before settling it.
    /// </summary>
}
