using Amazon.SQS;
using Amazon.SQS.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SqsRealAzureConformanceTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task ListQueues_paginates_against_real_service_bus()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var prefix = "aws2azure-list-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var queueUrls = new List<string>();

        try
        {
            for (var i = 0; i < 3; i++)
            {
                var created = await client.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = $"{prefix}-{i}",
                }, timeout.Token).ConfigureAwait(false);
                queueUrls.Add(created.QueueUrl);
            }

            var first = await client.ListQueuesAsync(new ListQueuesRequest
            {
                QueueNamePrefix = prefix,
                MaxResults = 2,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(2, first.QueueUrls.Count);
            Assert.False(string.IsNullOrWhiteSpace(first.NextToken));

            var second = await client.ListQueuesAsync(new ListQueuesRequest
            {
                QueueNamePrefix = prefix,
                MaxResults = 2,
                NextToken = first.NextToken,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Single(second.QueueUrls);
            Assert.True(string.IsNullOrWhiteSpace(second.NextToken));
            Assert.Equal(
                queueUrls.Order(StringComparer.Ordinal).ToArray(),
                first.QueueUrls.Concat(second.QueueUrls).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            foreach (var queueUrl in queueUrls)
            {
                try { await client.DeleteQueueAsync(queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public async Task Message_batches_report_real_service_bus_results()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS conformance.");

        var queueName = "aws2azure-batch-" + Guid.NewGuid().ToString("N")[..10];
        using var client = fixture.CreateSqsClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string? queueUrl = null;

        try
        {
            queueUrl = (await client.CreateQueueAsync(queueName, timeout.Token).ConfigureAwait(false)).QueueUrl;
            var bodies = Enumerable.Range(0, 3)
                .Select(i => $"batch-{Guid.NewGuid():N}-{i}")
                .ToArray();
            var sent = await client.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = bodies.Select((body, i) => new SendMessageBatchRequestEntry
                {
                    Id = $"send-{i}",
                    MessageBody = body,
                }).ToList(),
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, sent.Successful.Count);
            Assert.Empty(sent.Failed);

            var received = await ReceiveBodiesAsync(client, queueUrl, bodies, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, received.Count);

            var deleteEntries = received.Select((item, i) => new DeleteMessageBatchRequestEntry
            {
                Id = $"delete-{i}",
                ReceiptHandle = item.Value,
            }).ToList();
            deleteEntries.Add(new DeleteMessageBatchRequestEntry
            {
                Id = "invalid",
                ReceiptHandle = "not-a-real-service-bus-lock-token",
            });

            var deleted = await client.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = deleteEntries,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, deleted.Successful.Count);
            var failure = Assert.Single(deleted.Failed);
            Assert.Equal("invalid", failure.Id);
            Assert.True(failure.SenderFault);
        }
        finally
        {
            if (queueUrl is not null)
            {
                try { await client.DeleteQueueAsync(queueUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    [SkippableFact]
    public void Fifo_session_receive_is_not_yet_conformant_on_real_azure()
        => Skip.If(
            true,
            "Real Azure run 29472731381 timed out AcceptNextSession even for one ready FIFO group; the earlier multi-group runs 29471929057 and 29471188679 failed on the same acquisition path.");

    private static async Task<Dictionary<string, string>> ReceiveBodiesAsync(
        IAmazonSQS client,
        string queueUrl,
        IReadOnlyCollection<string> expectedBodies,
        CancellationToken cancellationToken)
    {
        var expected = expectedBodies.ToHashSet(StringComparer.Ordinal);
        var received = new Dictionary<string, string>(StringComparer.Ordinal);
        while (received.Count < expected.Count && !cancellationToken.IsCancellationRequested)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                if (expected.Contains(message.Body))
                {
                    received[message.Body] = message.ReceiptHandle;
                }
            }
        }

        return received;
    }
}
