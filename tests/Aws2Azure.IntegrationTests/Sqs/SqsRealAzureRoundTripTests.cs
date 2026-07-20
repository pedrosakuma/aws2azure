using System;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

/// <summary>
/// Real-Azure nightly smoke for the SQS module (issue #153): a full
/// CreateQueue → SendMessage → ReceiveMessage → DeleteMessage → DeleteQueue
/// cycle against a live Azure Service Bus namespace. Driven by the existing
/// <c>AZURE_SB_CONNSTR</c> secret, so this runs for real on the nightly job.
/// Skips when the connection string is absent.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SqsRealAzureSmokeTests
{
    private readonly RealAzureProxyFixture _fx;

    public SqsRealAzureSmokeTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task Message_lifecycle_round_trips_against_real_service_bus()
    {
        Skip.IfNot(_fx.ServiceBusConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SQS smoke.");

        var queueName = "aws2azure-it-" + Guid.NewGuid().ToString("N")[..12];
        const string body = "aws2azure real-Azure SQS smoke payload";

        using var client = _fx.CreateSqsClient();
        string? queueUrl = null;

        try
        {
            var created = await client.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName }).ConfigureAwait(false);
            queueUrl = created.QueueUrl;
            Assert.False(string.IsNullOrWhiteSpace(queueUrl));

            var discovered = await client.GetQueueUrlAsync(queueName).ConfigureAwait(false);
            Assert.Equal(queueUrl, discovered.QueueUrl);

            await client.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = body,
            }).ConfigureAwait(false);

            ReceiveMessageResponse received = new();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                received = await client.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 5,
                }).ConfigureAwait(false);

                if (received.Messages is { Count: > 0 })
                {
                    break;
                }
            }

            Assert.True(received.Messages is { Count: > 0 }, "No message received from real Service Bus within timeout.");
            var message = received.Messages[0];
            Assert.Equal(body, message.Body);

            await client.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = message.ReceiptHandle,
            }).ConfigureAwait(false);
        }
        finally
        {
            if (queueUrl is not null)
            {
                try
                {
                    await client.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl }).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup for the real-Azure smoke path.
                }
            }
        }
    }
}
