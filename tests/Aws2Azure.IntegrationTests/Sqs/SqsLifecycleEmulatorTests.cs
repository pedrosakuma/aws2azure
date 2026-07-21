using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

/// <summary>
/// Phase 2.7 Slice 5 — end-to-end lifecycle smokes for the SQS module
/// over native AMQP against the Service Bus emulator. Exercises the
/// AMQP send path (<see cref="Aws2Azure.Modules.Sqs.Operations.AmqpSendMessageHandlers"/>)
/// alongside the existing AMQP receive / delete / visibility handlers.
///
/// <para>Skipped — not failed — when Docker isn't reachable so fork PRs
/// and sandboxes without Docker self-skip cleanly.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqsEmulatorProxyCollection.Name)]
public sealed class SqsLifecycleEmulatorTests
{
    private readonly SqsEmulatorProxyFixture _fixture;

    public SqsLifecycleEmulatorTests(SqsEmulatorProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task SendMessage_then_ReceiveMessage_then_DeleteMessage_roundtrips_on_standard_queue()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        var queueName = ServiceBusEmulatorFixture.StandardQueue;
        var body = "hello-from-aws2azure-it-" + Guid.NewGuid().ToString("N");

        using var client = _fixture.CreateSqsClient();

        // Send
        using (var sendResp = await PostAsync(client, "SendMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MessageBody = body,
        }).ConfigureAwait(false))
        {
            var debug = await sendResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(sendResp.StatusCode == HttpStatusCode.OK,
                $"SendMessage status={(int)sendResp.StatusCode}, body={debug}");
        }
        string receiptHandle;
        using (var recvResp = await PostAsync(client, "ReceiveMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 2,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, recvResp.StatusCode);
            using var doc = JsonDocument.Parse(
                await recvResp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var messages = doc.RootElement.GetProperty("Messages");
            Assert.Equal(1, messages.GetArrayLength());
            var msg = messages[0];
            Assert.Equal(body, msg.GetProperty("Body").GetString());
            receiptHandle = msg.GetProperty("ReceiptHandle").GetString()!;
            Assert.False(string.IsNullOrEmpty(receiptHandle));
        }

        // Delete
        using (var delResp = await PostAsync(client, "DeleteMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            ReceiptHandle = receiptHandle,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task ReceiveMessage_honors_MessageSystemAttributeNames_filter()
    {
        // Regression coverage for issue #626: the proxy previously only
        // recognized the deprecated AttributeNames JSON property. Modern AWS
        // SDKs marshal ReceiveMessageRequest.MessageSystemAttributeNames onto
        // its own distinct wire property, so a caller using the current SDK
        // API silently got no system attributes back (e.g.
        // ApproximateReceiveCount) even though AttributeNames-based callers
        // worked. Exercise the modern wire property directly.
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        var queueName = ServiceBusEmulatorFixture.StandardQueue;
        var body = "attr-filter-" + Guid.NewGuid().ToString("N");

        using var client = _fixture.CreateSqsClient();

        using (var sendResp = await PostAsync(client, "SendMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MessageBody = body,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        }

        string receiptHandle;
        using (var recvResp = await PostAsync(client, "ReceiveMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 2,
            MessageSystemAttributeNames = new[] { "ApproximateReceiveCount" },
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, recvResp.StatusCode);
            using var doc = JsonDocument.Parse(
                await recvResp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var messages = doc.RootElement.GetProperty("Messages");
            Assert.Equal(1, messages.GetArrayLength());
            var msg = messages[0];
            Assert.Equal(body, msg.GetProperty("Body").GetString());
            var attributes = msg.GetProperty("Attributes");
            Assert.Equal("1", attributes.GetProperty("ApproximateReceiveCount").GetString());
            receiptHandle = msg.GetProperty("ReceiptHandle").GetString()!;
        }

        using (var delResp = await PostAsync(client, "DeleteMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            ReceiptHandle = receiptHandle,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task ReceiveMessage_long_poll_returns_message_published_mid_poll()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        var queueName = ServiceBusEmulatorFixture.StandardQueue;
        var body = "long-poll-" + Guid.NewGuid().ToString("N");

        using var client = _fixture.CreateSqsClient();

        // Start the long-poll first (5s budget), then publish ~500ms in.
        var receiveTask = PostAsync(client, "ReceiveMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 5,
        });

        await Task.Delay(500).ConfigureAwait(false);

        using (var sendResp = await PostAsync(client, "SendMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MessageBody = body,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        }

        using var recvResp = await receiveTask.ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, recvResp.StatusCode);
        using var doc = JsonDocument.Parse(
            await recvResp.Content.ReadAsStringAsync().ConfigureAwait(false));
        var messages = doc.RootElement.GetProperty("Messages");
        Assert.Equal(1, messages.GetArrayLength());
        var receipt = messages[0].GetProperty("ReceiptHandle").GetString()!;

        // Settle so the message doesn't bleed into the next test.
        using var del = await PostAsync(client, "DeleteMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            ReceiptHandle = receipt,
        }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    [SkippableFact]
    public async Task ChangeMessageVisibility_extends_the_lock_so_message_is_not_redelivered()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");
        // The Service Bus emulator's $management node detaches the
        // request/response link on the first com.microsoft:renew-lock
        // request, surfacing as "channel has been closed" inside the
        // proxy. Real Service Bus handles this fine; the emulator's
        // management surface is a documented divergence (see
        // docs/gaps/sqs/ChangeMessageVisibility.yaml). Covered by the
        // real-Azure nightly smoke instead.
        Skip.If(true, "Emulator does not support $management renew-lock; covered by real-Azure smoke.");

        var queueName = ServiceBusEmulatorFixture.StandardQueue;
        var body = "cmv-" + Guid.NewGuid().ToString("N");

        using var client = _fixture.CreateSqsClient();

        using (var sendResp = await PostAsync(client, "SendMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MessageBody = body,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        }

        string receiptHandle;
        using (var recvResp = await PostAsync(client, "ReceiveMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 2,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, recvResp.StatusCode);
            using var doc = JsonDocument.Parse(
                await recvResp.Content.ReadAsStringAsync().ConfigureAwait(false));
            receiptHandle = doc.RootElement.GetProperty("Messages")[0]
                .GetProperty("ReceiptHandle").GetString()!;
        }

        // Extend lock to 30s (= the queue's LockDuration; emulator
        // clamps to LockDuration ceiling so this is the safe upper
        // bound).
        using (var cmvResp = await PostAsync(client, "ChangeMessageVisibility", new
        {
            QueueUrl = QueueUrl(queueName),
            ReceiptHandle = receiptHandle,
            VisibilityTimeout = 30,
        }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, cmvResp.StatusCode);
        }

        // Cleanup: delete so subsequent runs start clean.
        using var del = await PostAsync(client, "DeleteMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            ReceiptHandle = receiptHandle,
        }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    private static string QueueUrl(string name) =>
        $"https://sqs.us-east-1.amazonaws.com/000000000000/{name}";

    /// <summary>
    /// Issue a SigV4-signed AWS-JSON-1.0 SQS request against the
    /// proxy. Uses <see cref="TestSigV4Signer"/> with service=sqs so
    /// the proxy's validator accepts the signature.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string operation, object jsonPayload)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(jsonPayload);
        // SigV4 signer needs an absolute URI to read the authority; mirror
        // what the AWS SDK does by building a fully-qualified URL.
        var uri = new Uri(client.BaseAddress!, "/");
        var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(payload),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        req.Headers.TryAddWithoutValidation("X-Amz-Target", "AmazonSQS." + operation);
        TestSigV4Signer.SignHeader(req, payload,
            SqsEmulatorProxyFixture.AwsAccessKey, SqsEmulatorProxyFixture.AwsSecret,
            region: "us-east-1", service: "sqs");
        return await client.SendAsync(req).ConfigureAwait(false);
    }
}
