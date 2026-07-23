using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SqsAdvancedBoundariesRealAzureTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    [Trait("WorkloadProfile", "sqs-fifo-amqp")]
    public async Task Fifo_batch_dedup_order_settlement_and_restart_are_connection_affine()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS FIFO boundaries.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var client = fixture.CreateSqsClient(maxErrorRetry: 0);
        var queueName = "aws2azure-fifo-" + Guid.NewGuid().ToString("N")[..10] + ".fifo";
        string? queueUrl = null;

        try
        {
            queueUrl = (await client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>
                {
                    ["FifoQueue"] = "true",
                    ["ContentBasedDeduplication"] = "false",
                    ["VisibilityTimeout"] = "5",
                },
            }, timeout.Token).ConfigureAwait(false)).QueueUrl;

            var run = Guid.NewGuid().ToString("N");
            var bodies = Enumerable.Range(0, 3).Select(i => $"fifo-{run}-{i}").ToArray();
            var entries = bodies.Select((body, i) => new SendMessageBatchRequestEntry
            {
                Id = $"send-{i}",
                MessageBody = body,
                MessageGroupId = "group-" + run,
                MessageDeduplicationId = $"dedup-{run}-{i}",
            }).ToList();

            var sent = await client.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = entries,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, sent.Successful.Count);
            Assert.Empty(sent.Failed);

            var duplicate = await client.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = entries,
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(3, duplicate.Successful.Count);
            Assert.Empty(duplicate.Failed);

            for (var i = 0; i < bodies.Length; i++)
            {
                var message = Assert.Single(
                    await ReceiveAtLeastAsync(client, queueUrl, 1, timeout.Token).ConfigureAwait(false));
                Assert.Equal(bodies[i], message.Body);
                Assert.Equal("group-" + run, message.Attributes["MessageGroupId"]);
                if (i == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token).ConfigureAwait(false);
                    await client.ChangeMessageVisibilityAsync(
                        queueUrl, message.ReceiptHandle, 10, timeout.Token).ConfigureAwait(false);
                    var blockedSameGroup = await client.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 1,
                        WaitTimeSeconds = 2,
                        MessageSystemAttributeNames = new List<string> { "All" },
                    }, timeout.Token).ConfigureAwait(false);
                    Assert.Empty(blockedSameGroup.Messages);
                }
                await client.DeleteMessageAsync(queueUrl, message.ReceiptHandle, timeout.Token)
                    .ConfigureAwait(false);
            }

            var duplicateProbe = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                MessageSystemAttributeNames = new List<string> { "All" },
            }, timeout.Token).ConfigureAwait(false);
            Assert.Empty(duplicateProbe.Messages);

            var batchSent = await client.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = Enumerable.Range(0, 2).Select(i => new SendMessageBatchRequestEntry
                {
                    Id = $"batch-delete-{i}",
                    MessageBody = $"batch-delete-{run}-{i}",
                    MessageGroupId = "batch-delete-group-" + run,
                    MessageDeduplicationId = $"batch-delete-dedup-{run}-{i}",
                }).ToList(),
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(2, batchSent.Successful.Count);
            Assert.Empty(batchSent.Failed);
            var batchReceived = await ReceiveAtLeastAsync(client, queueUrl, 2, timeout.Token).ConfigureAwait(false);
            var deleted = await client.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = batchReceived.Select((message, i) => new DeleteMessageBatchRequestEntry
                {
                    Id = $"delete-{i}",
                    ReceiptHandle = message.ReceiptHandle,
                }).ToList(),
            }, timeout.Token).ConfigureAwait(false);
            Assert.Equal(2, deleted.Successful.Count);
            Assert.Empty(deleted.Failed);

            await client.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "restart-" + run,
                MessageGroupId = "restart-group-" + run,
                MessageDeduplicationId = "restart-dedup-" + run,
            }, timeout.Token).ConfigureAwait(false);
            var beforeRestart = Assert.Single(
                await ReceiveAtLeastAsync(client, queueUrl, 1, timeout.Token).ConfigureAwait(false));

            await fixture.RestartAsync().ConfigureAwait(false);
            using var restartedClient = fixture.CreateSqsClient(maxErrorRetry: 0);
            await Assert.ThrowsAsync<AmazonSQSException>(() =>
                restartedClient.DeleteMessageAsync(queueUrl, beforeRestart.ReceiptHandle, timeout.Token));

            var redelivered = Assert.Single(
                await ReceiveAtLeastAsync(restartedClient, queueUrl, 1, timeout.Token).ConfigureAwait(false));
            Assert.Equal(beforeRestart.Body, redelivered.Body);
            await restartedClient.DeleteMessageAsync(queueUrl, redelivered.ReceiptHandle, timeout.Token)
                .ConfigureAwait(false);
            var deleteQueue = await restartedClient.DeleteQueueAsync(queueUrl, timeout.Token)
                .ConfigureAwait(false);
            Assert.Equal(System.Net.HttpStatusCode.OK, deleteQueue.HttpStatusCode);
            queueUrl = null;
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
    [Trait("WorkloadProfile", "sqs-dlq-redrive")]
    public async Task Redrive_policy_attribution_and_source_pagination_survive_restart()
    {
        Skip.IfNot(fixture.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS DLQ boundaries.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var client = fixture.CreateSqsClient(maxErrorRetry: 0);
        var run = Guid.NewGuid().ToString("N")[..10];
        var targetName = "aws2azure-dlq-" + run;
        var sourceNames = Enumerable.Range(0, 3)
            .Select(i => $"aws2azure-src-{run}-{i}")
            .ToArray();
        string? targetUrl = null;
        var sourceUrls = new List<string>();

        try
        {
            targetUrl = (await client.CreateQueueAsync(targetName, timeout.Token).ConfigureAwait(false)).QueueUrl;
            var targetArn = $"arn:aws:sqs:us-east-1:000000000000:{targetName}";
            var redrivePolicy = JsonSerializer.Serialize(new
            {
                deadLetterTargetArn = targetArn,
                maxReceiveCount = 1,
            });

            foreach (var sourceName in sourceNames)
            {
                var created = await client.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = sourceName,
                    Attributes = new Dictionary<string, string>
                    {
                        ["VisibilityTimeout"] = "5",
                    },
                }, timeout.Token).ConfigureAwait(false);
                sourceUrls.Add(created.QueueUrl);
                await client.SetQueueAttributesAsync(new SetQueueAttributesRequest
                {
                    QueueUrl = created.QueueUrl,
                    Attributes = new Dictionary<string, string>
                    {
                        ["RedrivePolicy"] = redrivePolicy,
                    },
                }, timeout.Token).ConfigureAwait(false);
            }

            var attrs = await client.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = sourceUrls[0],
                AttributeNames = new List<string> { "RedrivePolicy" },
            }, timeout.Token).ConfigureAwait(false);
            using (var policy = JsonDocument.Parse(attrs.Attributes["RedrivePolicy"]))
            {
                Assert.Equal(targetArn,
                    policy.RootElement.GetProperty("deadLetterTargetArn").GetString());
                Assert.Equal(1,
                    policy.RootElement.GetProperty("maxReceiveCount").GetInt32());
            }

            var first = await client.ListDeadLetterSourceQueuesAsync(
                new ListDeadLetterSourceQueuesRequest
                {
                    QueueUrl = targetUrl,
                    MaxResults = 2,
                },
                timeout.Token).ConfigureAwait(false);
            Assert.Equal(2, first.QueueUrls.Count);
            Assert.False(string.IsNullOrWhiteSpace(first.NextToken));

            await fixture.RestartAsync().ConfigureAwait(false);
            using var restartedClient = fixture.CreateSqsClient(maxErrorRetry: 0);
            var second = await restartedClient.ListDeadLetterSourceQueuesAsync(
                new ListDeadLetterSourceQueuesRequest
                {
                    QueueUrl = targetUrl,
                    MaxResults = 2,
                    NextToken = first.NextToken,
                },
                timeout.Token).ConfigureAwait(false);
            Assert.Single(second.QueueUrls);
            Assert.True(string.IsNullOrWhiteSpace(second.NextToken));
            Assert.Equal(
                sourceUrls.Order(StringComparer.Ordinal).ToArray(),
                first.QueueUrls.Concat(second.QueueUrls).Order(StringComparer.Ordinal).ToArray());

            var body = "redrive-" + Guid.NewGuid().ToString("N");
            await restartedClient.SendMessageAsync(sourceUrls[0], body, timeout.Token).ConfigureAwait(false);
            var sourceMessage = Assert.Single(
                await ReceiveAtLeastAsync(restartedClient, sourceUrls[0], 1, timeout.Token)
                    .ConfigureAwait(false));
            await restartedClient.ChangeMessageVisibilityAsync(
                sourceUrls[0], sourceMessage.ReceiptHandle, 0, timeout.Token).ConfigureAwait(false);

            // Service Bus evaluates MaxDeliveryCount when the source entity is
            // asked for the next delivery. This poll triggers the broker-side
            // dead-letter transition; the over-limit message must not escape
            // back to the SQS source queue.
            var sourceAfterLimit = await restartedClient.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = sourceUrls[0],
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 2,
                    MessageSystemAttributeNames = new List<string> { "All" },
                },
                timeout.Token).ConfigureAwait(false);
            Assert.Empty(sourceAfterLimit.Messages);

            var forwarded = Assert.Single(
                await ReceiveAtLeastAsync(restartedClient, targetUrl, 1, timeout.Token)
                    .ConfigureAwait(false));
            Assert.Equal(body, forwarded.Body);
            Assert.Equal(
                $"arn:aws:sqs:us-east-1:000000000000:{sourceNames[0]}",
                forwarded.Attributes["DeadLetterQueueSourceArn"]);
            await restartedClient.DeleteMessageAsync(targetUrl, forwarded.ReceiptHandle, timeout.Token)
                .ConfigureAwait(false);

            foreach (var sourceUrl in sourceUrls)
            {
                var deleteSource = await restartedClient.DeleteQueueAsync(sourceUrl, timeout.Token)
                    .ConfigureAwait(false);
                Assert.Equal(System.Net.HttpStatusCode.OK, deleteSource.HttpStatusCode);
            }
            sourceUrls.Clear();
            var deleteTarget = await restartedClient.DeleteQueueAsync(targetUrl, timeout.Token)
                .ConfigureAwait(false);
            Assert.Equal(System.Net.HttpStatusCode.OK, deleteTarget.HttpStatusCode);
            targetUrl = null;
        }
        finally
        {
            foreach (var sourceUrl in sourceUrls)
            {
                try { await client.DeleteQueueAsync(sourceUrl).ConfigureAwait(false); } catch { }
            }
            if (targetUrl is not null)
            {
                try { await client.DeleteQueueAsync(targetUrl).ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task<List<Message>> ReceiveAtLeastAsync(
        IAmazonSQS client,
        string queueUrl,
        int count,
        CancellationToken cancellationToken)
    {
        var messages = new List<Message>(count);
        while (messages.Count < count)
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = Math.Min(10, count - messages.Count),
                WaitTimeSeconds = 5,
                MessageSystemAttributeNames = new List<string> { "All" },
            }, cancellationToken).ConfigureAwait(false);
            messages.AddRange(response.Messages);
        }
        return messages;
    }
}
