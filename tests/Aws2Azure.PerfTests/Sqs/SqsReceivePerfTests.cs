using Amazon.SQS;
using Amazon.SQS.Model;
using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Receive-side perf for SQS. Pre-fills the queue and then closed-loops
/// <c>ReceiveMessage</c>+<c>DeleteMessage</c> to capture the realistic
/// drain lifecycle (single message per call, short poll).
/// </summary>
[Collection(SqsPerfCollection.Name)]
public sealed class SqsReceivePerfTests(SqsPerfFixture fixture)
{
    // Sized to comfortably survive a 3 s warm-up + 20 s measurement window
    // at the current ~80–200 cps observed on the Service Bus emulator.
    // Kept below the emulator's bulk-ingest backpressure threshold (~1700
    // before AMQP starts rejecting); larger counts would force throttling
    // on the prefill loop and dwarf the test wall-time.
    private const int PrefillCount = 1_500;
    private const int Concurrency = 16;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(3);

    [SkippableFact]
    public async Task ReceiveMessage_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();
        await DrainAsync(client, fixture.QueueUrl).ConfigureAwait(false);
        await PrefillAsync(client, fixture.QueueUrl, PrefillCount).ConfigureAwait(false);

        var result = await PerfRunner.RunAsync(
            scenario: "sqs.ReceiveMessage+Delete (1)",
            concurrency: Concurrency,
            duration: Duration,
            warmup: Warmup,
            action: async (_, ct) =>
            {
                var resp = await client.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = fixture.QueueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 0,
                }, ct).ConfigureAwait(false);

                if (resp.Messages is { Count: > 0 })
                {
                    await client.DeleteMessageAsync(new DeleteMessageRequest
                    {
                        QueueUrl = fixture.QueueUrl,
                        ReceiptHandle = resp.Messages[0].ReceiptHandle,
                    }, ct).ConfigureAwait(false);
                }
            });

        PerfReport.Append(result, notes: "SQS→ServiceBus(AMQP) emulator — receive+delete; empty receives count as no-op calls");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    private static async Task PrefillAsync(IAmazonSQS client, string queueUrl, int total)
    {
        // SendMessageBatch caps at 10 entries / 256 KiB per call. The SB
        // emulator backpressures (AMQP Rejected → InvalidParameterValue)
        // around ~1700 in-flight messages, so retry-with-backoff on the
        // failure-per-entry list rather than failing the whole prefill.
        const int BatchSize = 10;
        const int MaxRetries = 5;
        var produced = 0;
        var batchIndex = 0;
        while (produced < total)
        {
            var thisBatch = Math.Min(BatchSize, total - produced);
            var req = new SendMessageBatchRequest { QueueUrl = queueUrl, Entries = new List<SendMessageBatchRequestEntry>(thisBatch) };
            for (var i = 0; i < thisBatch; i++)
            {
                req.Entries.Add(new SendMessageBatchRequestEntry($"e{i}", $"prefill-{batchIndex}-{i}"));
            }

            var attempt = 0;
            while (true)
            {
                SendMessageBatchResponse resp;
                try
                {
                    resp = await client.SendMessageBatchAsync(req).ConfigureAwait(false);
                }
                catch (Amazon.SQS.AmazonSQSException ex) when (attempt < MaxRetries && IsTransient(ex))
                {
                    await Task.Delay(BackoffMs(attempt)).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                if (resp.Failed is { Count: > 0 } && attempt < MaxRetries)
                {
                    await Task.Delay(BackoffMs(attempt)).ConfigureAwait(false);
                    attempt++;
                    // Resend only the failed entries so we don't get duplicate-id rejects.
                    req = new SendMessageBatchRequest
                    {
                        QueueUrl = queueUrl,
                        Entries = resp.Failed.Select(f => new SendMessageBatchRequestEntry(f.Id, $"prefill-{batchIndex}-{f.Id}")).ToList(),
                    };
                    continue;
                }
                if (resp.Failed is { Count: > 0 })
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Prefill failed at batch {batchIndex} after {attempt} retries: {resp.Failed[0].Code} {resp.Failed[0].Message}");
                }
                break;
            }
            produced += thisBatch;
            batchIndex++;
        }
    }

    private static bool IsTransient(Amazon.SQS.AmazonSQSException ex) =>
        ex.ErrorCode is "InvalidParameterValue" or "ServiceUnavailable" or "Throttling"
        || ex.Message.Contains("Rejected", StringComparison.OrdinalIgnoreCase);

    private static int BackoffMs(int attempt) => (int)Math.Min(2000, 50 * Math.Pow(2, attempt));

    private static async Task DrainAsync(IAmazonSQS client, string queueUrl)
    {
        // Best-effort drain — bounded by a wall-clock budget so we don't
        // spin forever against a backend that keeps re-surfacing messages.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var consecutiveEmpty = 0;
        while (DateTime.UtcNow < deadline && consecutiveEmpty < 3)
        {
            var resp = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0,
            }).ConfigureAwait(false);

            if (resp.Messages is null || resp.Messages.Count == 0)
            {
                consecutiveEmpty++;
                continue;
            }
            consecutiveEmpty = 0;

            var deleteReq = new DeleteMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = new List<DeleteMessageBatchRequestEntry>(resp.Messages.Count),
            };
            for (var i = 0; i < resp.Messages.Count; i++)
            {
                deleteReq.Entries.Add(new DeleteMessageBatchRequestEntry($"d{i}", resp.Messages[i].ReceiptHandle));
            }
            await client.DeleteMessageBatchAsync(deleteReq).ConfigureAwait(false);
        }
    }
}
