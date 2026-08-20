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
    private static async Task VerifyRedeliveryAsync(
        RealAzureProxyFixture fixture,
        CancellationToken cancellationToken)
    {
        var queueName = "a2a-redelivery-" + Guid.NewGuid().ToString("N")[..16];
        const string body = "aws2azure redelivery canary";
        using var client = fixture.CreateSqsClient();
        var queueUrl = (await client.CreateQueueAsync(
            new CreateQueueRequest { QueueName = queueName },
            cancellationToken).ConfigureAwait(false)).QueueUrl;
        try
        {
            await client.SendMessageAsync(
                new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body },
                cancellationToken).ConfigureAwait(false);

            var first = await ReceiveWithReceiveCountAsync(client, queueUrl, body, cancellationToken)
                .ConfigureAwait(false);

            await client.ChangeMessageVisibilityAsync(
                new ChangeMessageVisibilityRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = first.ReceiptHandle,
                    VisibilityTimeout = 0,
                },
                cancellationToken).ConfigureAwait(false);

            var second = await ReceiveWithReceiveCountAsync(client, queueUrl, body, cancellationToken)
                .ConfigureAwait(false);
            if (second.ReceiveCount <= first.ReceiveCount)
            {
                throw new InvalidDataException(
                    "ApproximateReceiveCount did not increase after forced redelivery.");
            }

            await client.DeleteMessageAsync(
                new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = second.ReceiptHandle,
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
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

    private static async Task<(string ReceiptHandle, int ReceiveCount)> ReceiveWithReceiveCountAsync(
        IAmazonSQS client,
        string queueUrl,
        string expectedBody,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var response = await client.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 5,
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                },
                cancellationToken).ConfigureAwait(false);
            if (response.Messages is { Count: > 0 } messages)
            {
                var message = messages[0];
                if (!string.Equals(message.Body, expectedBody, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Redelivery scenario received the wrong body.");
                }
                var receiveCount = message.Attributes is { } attributes
                    && attributes.TryGetValue("ApproximateReceiveCount", out var raw)
                    && int.TryParse(raw, out var parsed)
                    ? parsed
                    : throw new InvalidDataException(
                        "ReceiveMessage did not return ApproximateReceiveCount.");
                return (message.ReceiptHandle, receiveCount);
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidDataException(
                    "No message received for the redelivery scenario within the deadline.");
            }
        }
    }

    /// <summary>
    /// A fixed batch of uniquely-tagged messages is produced onto one
    /// shared queue, then <paramref name="consumers"/> parallel consumers
    /// race to receive and settle them. Asserts every message is consumed
    /// exactly once — no message is lost and no two consumers observe the
    /// same delivery — proving multi-consumer safety on a shared queue,
    /// distinct from representative-load's per-worker private queues.
    /// </summary>
    private static async Task<(long Completions, long Failures, double DurationSeconds)>
        VerifyConcurrencyAsync(
            RealAzureProxyFixture fixture,
            int consumers,
            int messageCount,
            CancellationToken cancellationToken)
    {
        var queueName = "a2a-concurrency-" + Guid.NewGuid().ToString("N")[..16];
        using var client = fixture.CreateSqsClient();
        var queueUrl = (await client.CreateQueueAsync(
            new CreateQueueRequest { QueueName = queueName },
            cancellationToken).ConfigureAwait(false)).QueueUrl;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var ids = Enumerable.Range(0, messageCount)
                .Select(_ => Guid.NewGuid().ToString("N"))
                .ToArray();
            await Task.WhenAll(ids.Select(id => client.SendMessageAsync(
                new SendMessageRequest { QueueUrl = queueUrl, MessageBody = id },
                cancellationToken))).ConfigureAwait(false);

            var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var duplicates = 0L;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));

            var consumerTasks = Enumerable.Range(0, consumers).Select(async _ =>
            {
                while (seen.Count < ids.Length && !deadline.IsCancellationRequested)
                {
                    ReceiveMessageResponse response;
                    try
                    {
                        response = await client.ReceiveMessageAsync(
                            new ReceiveMessageRequest
                            {
                                QueueUrl = queueUrl,
                                MaxNumberOfMessages = 10,
                                WaitTimeSeconds = 2,
                            },
                            deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        return;
                    }
                    if (response.Messages is not { Count: > 0 } messages)
                    {
                        continue;
                    }
                    foreach (var message in messages)
                    {
                        if (!seen.TryAdd(message.Body, 0))
                        {
                            Interlocked.Increment(ref duplicates);
                        }
                        await client.DeleteMessageAsync(
                            new DeleteMessageRequest
                            {
                                QueueUrl = queueUrl,
                                ReceiptHandle = message.ReceiptHandle,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }).ToArray();
            await Task.WhenAll(consumerTasks).ConfigureAwait(false);
            stopwatch.Stop();

            if (seen.Count != ids.Length)
            {
                throw new InvalidDataException(
                    $"Concurrency scenario consumed {seen.Count} of {ids.Length} messages " +
                    "before the deadline.");
            }
            if (duplicates > 0)
            {
                throw new InvalidDataException(
                    $"Concurrency scenario observed {duplicates} duplicate deliveries across " +
                    $"{consumers} consumers.");
            }
            return (seen.Count, 0, stopwatch.Elapsed.TotalSeconds);
        }
        finally
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

    /// <summary>
    /// Lightweight REST-transport counterpart of representative-load,
    /// against the fixed <see cref="RealAzureProxyFixture.SqsRestLaneQueueName"/>
    /// override. Kept short and separate — non-required, report-only
    /// evidence that the REST path functions independently of the AMQP
    /// default (issue #626). Purges any stale message left behind by a
    /// prior run (e.g. an undeliverable rollback-rest canary) before
    /// measuring, and never throws: a mismatch or timeout is recorded as a
    /// failure rather than aborting the whole load run, so this
    /// supplementary scenario can never take down the required evidence
    /// produced earlier in the same test.
    /// </summary>
    private static async Task<(long Completions, long Failures, double DurationSeconds)>
        VerifyRestRepresentativeAsync(
            RealAzureProxyFixture fixture,
            int iterations,
            CancellationToken cancellationToken)
    {
        using var client = fixture.CreateSqsClient();
        // The REST-lane queue is shared and intentionally never deleted
        // within a run (see RealAzureRollbackQualification, which purges
        // rather than deletes it) — creating it again here when the
        // rollback scenario already created it earlier in the same run is
        // an ordinary idempotent re-create, not a conflict. Fall back to
        // resolving the existing queue's URL rather than failing the whole
        // load run if Service Bus ever reports a transient attribute
        // mismatch on that idempotent re-create.
        string queueUrl;
        try
        {
            queueUrl = (await client.CreateQueueAsync(
                new CreateQueueRequest { QueueName = RealAzureProxyFixture.SqsRestLaneQueueName },
                cancellationToken).ConfigureAwait(false)).QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            queueUrl = (await client.GetQueueUrlAsync(
                new GetQueueUrlRequest { QueueName = RealAzureProxyFixture.SqsRestLaneQueueName },
                cancellationToken).ConfigureAwait(false)).QueueUrl;
        }
        await PurgeRestLaneQueueAsync(client, queueUrl, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var completions = 0L;
        var failures = 0L;
        for (var i = 0; i < iterations; i++)
        {
            var body = "aws2azure rest-lane load " + Guid.NewGuid().ToString("N");
            try
            {
                await client.SendMessageAsync(
                    new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body },
                    cancellationToken).ConfigureAwait(false);

                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
                ReceiveMessageResponse? received = null;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var response = await client.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = queueUrl,
                            MaxNumberOfMessages = 1,
                            WaitTimeSeconds = 5,
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (response.Messages is { Count: > 0 })
                    {
                        received = response;
                        break;
                    }
                }
                if (received?.Messages is not { Count: > 0 } receivedMessages
                    || !string.Equals(receivedMessages[0].Body, body, StringComparison.Ordinal))
                {
                    failures++;
                    continue;
                }

                await client.DeleteMessageAsync(
                    new DeleteMessageRequest
                    {
                        QueueUrl = queueUrl,
                        ReceiptHandle = receivedMessages[0].ReceiptHandle,
                    },
                    cancellationToken).ConfigureAwait(false);
                completions++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures++;
            }
        }
        stopwatch.Stop();
        return (completions, failures, stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Drains any message left in the shared REST-lane queue by a prior run
    /// (e.g. an undeliverable rollback-rest canary — see
    /// <see cref="RealAzureRollbackQualification.VerifySqsAsync"/>) before
    /// this scenario starts, so a stale delivery can never be mistaken for
    /// the message this scenario just sent.
    /// </summary>
}
