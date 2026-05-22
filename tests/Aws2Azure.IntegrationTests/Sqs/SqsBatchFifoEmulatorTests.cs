using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

/// <summary>
/// Phase 2.7 Slice 6 — batch + FIFO lifecycle smokes for the SQS module
/// over native AMQP against the Service Bus emulator. Complements
/// <see cref="SqsLifecycleEmulatorTests"/> by exercising
/// <see cref="Aws2Azure.Modules.Sqs.Operations.AmqpSendMessageBatchHandlers"/>
/// and the FIFO interlock in
/// <see cref="Aws2Azure.Modules.Sqs.Operations.AmqpSendMessageHandlers"/> +
/// <see cref="Aws2Azure.Modules.Sqs.Operations.AmqpReceiveMessageHandlers"/>.
///
/// <para>Skipped — not failed — when Docker isn't reachable so fork PRs
/// and sandboxes without Docker self-skip cleanly.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqsEmulatorProxyCollection.Name)]
public sealed class SqsBatchFifoEmulatorTests
{
    private readonly SqsEmulatorProxyFixture _fixture;

    public SqsBatchFifoEmulatorTests(SqsEmulatorProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task SendMessageBatch_then_DeleteMessageBatch_roundtrips_on_standard_queue()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        var queueName = ServiceBusEmulatorFixture.StandardQueue;
        var run = Guid.NewGuid().ToString("N");
        var bodies = new[]
        {
            "batch-" + run + "-0",
            "batch-" + run + "-1",
            "batch-" + run + "-2",
        };

        using var client = _fixture.CreateSqsClient();

        // SendMessageBatch — 3 entries.
        using (var sendResp = await PostAsync(client, "SendMessageBatch", new
        {
            QueueUrl = QueueUrl(queueName),
            Entries = new[]
            {
                new { Id = "m0", MessageBody = bodies[0] },
                new { Id = "m1", MessageBody = bodies[1] },
                new { Id = "m2", MessageBody = bodies[2] },
            },
        }).ConfigureAwait(false))
        {
            var debug = await sendResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(sendResp.StatusCode == HttpStatusCode.OK,
                $"SendMessageBatch status={(int)sendResp.StatusCode}, body={debug}");
            using var doc = JsonDocument.Parse(debug);
            var succ = doc.RootElement.GetProperty("Successful");
            Assert.Equal(3, succ.GetArrayLength());
            var ids = succ.EnumerateArray().Select(e => e.GetProperty("Id").GetString()).ToHashSet();
            Assert.Contains("m0", ids);
            Assert.Contains("m1", ids);
            Assert.Contains("m2", ids);
            // Failed should be empty or missing.
            if (doc.RootElement.TryGetProperty("Failed", out var failed) &&
                failed.ValueKind == JsonValueKind.Array)
            {
                Assert.Equal(0, failed.GetArrayLength());
            }
        }

        // Receive up to 10 — collect until we have all 3 of *our* bodies
        // (the shared collection-level fixture means residue from earlier
        // tests is possible; we filter to bodies tagged with this run id).
        var received = new Dictionary<string, string>(StringComparer.Ordinal);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (received.Count < bodies.Length && DateTime.UtcNow < deadline)
        {
            using var recvResp = await PostAsync(client, "ReceiveMessage", new
            {
                QueueUrl = QueueUrl(queueName),
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 3,
            }).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, recvResp.StatusCode);
            using var doc = JsonDocument.Parse(
                await recvResp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("Messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var m in messages.EnumerateArray())
            {
                var body = m.GetProperty("Body").GetString()!;
                var receipt = m.GetProperty("ReceiptHandle").GetString()!;
                if (Array.IndexOf(bodies, body) >= 0 && !received.ContainsKey(body))
                {
                    received[body] = receipt;
                }
                else
                {
                    // Drop unrelated residue so the queue isn't left dirty
                    // for the next test in the collection.
                    using var drop = await PostAsync(client, "DeleteMessage", new
                    {
                        QueueUrl = QueueUrl(queueName),
                        ReceiptHandle = receipt,
                    }).ConfigureAwait(false);
                }
            }
        }
        Assert.Equal(bodies.Length, received.Count);

        // DeleteMessageBatch — settle all 3 in one call.
        var deleteEntries = received
            .Select((kv, idx) => new { Id = "d" + idx, ReceiptHandle = kv.Value })
            .ToArray();
        using (var delResp = await PostAsync(client, "DeleteMessageBatch", new
        {
            QueueUrl = QueueUrl(queueName),
            Entries = deleteEntries,
        }).ConfigureAwait(false))
        {
            var debug = await delResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(delResp.StatusCode == HttpStatusCode.OK,
                $"DeleteMessageBatch status={(int)delResp.StatusCode}, body={debug}");
            using var doc = JsonDocument.Parse(debug);
            var succ = doc.RootElement.GetProperty("Successful");
            Assert.Equal(deleteEntries.Length, succ.GetArrayLength());
            if (doc.RootElement.TryGetProperty("Failed", out var failed) &&
                failed.ValueKind == JsonValueKind.Array)
            {
                Assert.Equal(0, failed.GetArrayLength());
            }
        }
    }

    [SkippableFact]
    public async Task SendMessage_on_FIFO_queue_then_ReceiveMessage_returns_MessageGroupId()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");
        // The Service Bus emulator does NOT echo the broker-assigned
        // session-id back in the receiver attach response's
        // com.microsoft:session-filter when the client requests "any"
        // session (sessionId=null), so the proxy can't resolve which
        // MessageGroupId it just bound. Real Service Bus echoes the
        // assigned id as required by AMQP, so the production path works
        // end-to-end. Covered by the real-Azure nightly smoke; tracked
        // in docs/gaps/sqs/ReceiveMessage.yaml.
        Skip.If(true, "Emulator does not echo broker-assigned session-id; FIFO receive covered by real-Azure smoke.");

        var queueName = ServiceBusEmulatorFixture.FifoQueue;
        var run = Guid.NewGuid().ToString("N");
        var groupId = "g-" + run;
        var dedupId = "d-" + run;
        var body = "fifo-single-" + run;

        using var client = _fixture.CreateSqsClient();

        using (var sendResp = await PostAsync(client, "SendMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MessageBody = body,
            MessageGroupId = groupId,
            MessageDeduplicationId = dedupId,
        }).ConfigureAwait(false))
        {
            var debug = await sendResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(sendResp.StatusCode == HttpStatusCode.OK,
                $"SendMessage(FIFO) status={(int)sendResp.StatusCode}, body={debug}");
        }

        string receiptHandle;
        using (var recvResp = await PostAsync(client, "ReceiveMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 5,
            AttributeNames = new[] { "All" },
        }).ConfigureAwait(false))
        {
            var debug = await recvResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(recvResp.StatusCode == HttpStatusCode.OK,
                $"ReceiveMessage(FIFO) status={(int)recvResp.StatusCode}, body={debug}");
            using var doc = JsonDocument.Parse(debug);
            var messages = doc.RootElement.GetProperty("Messages");
            Assert.Equal(1, messages.GetArrayLength());
            var msg = messages[0];
            Assert.Equal(body, msg.GetProperty("Body").GetString());

            // MessageGroupId is surfaced via the Attributes map when the
            // caller asks for "All" system attributes.
            var attrs = msg.GetProperty("Attributes");
            Assert.Equal(groupId, attrs.GetProperty("MessageGroupId").GetString());

            receiptHandle = msg.GetProperty("ReceiptHandle").GetString()!;
            Assert.False(string.IsNullOrEmpty(receiptHandle));
        }

        using (var delResp = await PostAsync(client, "DeleteMessage", new
        {
            QueueUrl = QueueUrl(queueName),
            ReceiptHandle = receiptHandle,
        }).ConfigureAwait(false))
        {
            var debug = await delResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(delResp.StatusCode == HttpStatusCode.OK,
                $"DeleteMessage(FIFO) status={(int)delResp.StatusCode}, body={debug}");
        }
    }

    [SkippableFact]
    public async Task SendMessageBatch_on_FIFO_queue_preserves_order_within_a_MessageGroup()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");
        // Same emulator divergence as the single-FIFO send test above:
        // the emulator omits the broker-assigned session-id from the
        // receiver attach response, so the proxy's FIFO receive path
        // fails. Real Azure honours the contract; covered by the
        // nightly real-Azure smoke.
        Skip.If(true, "Emulator does not echo broker-assigned session-id; FIFO receive covered by real-Azure smoke.");

        var queueName = ServiceBusEmulatorFixture.FifoQueue;
        var run = Guid.NewGuid().ToString("N");
        var groupId = "g-" + run;
        var bodies = new[]
        {
            "fifo-batch-" + run + "-0",
            "fifo-batch-" + run + "-1",
            "fifo-batch-" + run + "-2",
        };

        using var client = _fixture.CreateSqsClient();

        using (var sendResp = await PostAsync(client, "SendMessageBatch", new
        {
            QueueUrl = QueueUrl(queueName),
            Entries = new[]
            {
                new { Id = "m0", MessageBody = bodies[0], MessageGroupId = groupId, MessageDeduplicationId = "d-" + run + "-0" },
                new { Id = "m1", MessageBody = bodies[1], MessageGroupId = groupId, MessageDeduplicationId = "d-" + run + "-1" },
                new { Id = "m2", MessageBody = bodies[2], MessageGroupId = groupId, MessageDeduplicationId = "d-" + run + "-2" },
            },
        }).ConfigureAwait(false))
        {
            var debug = await sendResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(sendResp.StatusCode == HttpStatusCode.OK,
                $"SendMessageBatch(FIFO) status={(int)sendResp.StatusCode}, body={debug}");
            using var doc = JsonDocument.Parse(debug);
            Assert.Equal(3, doc.RootElement.GetProperty("Successful").GetArrayLength());
        }

        // Drain up to 3 messages tagged with this run's group; SB sessions
        // guarantee in-order delivery within a single MessageGroupId, so
        // we assert (body, order) matches the send order.
        var ordered = new List<(string Body, string ReceiptHandle)>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (ordered.Count < bodies.Length && DateTime.UtcNow < deadline)
        {
            using var recvResp = await PostAsync(client, "ReceiveMessage", new
            {
                QueueUrl = QueueUrl(queueName),
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 3,
                AttributeNames = new[] { "All" },
            }).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, recvResp.StatusCode);
            using var doc = JsonDocument.Parse(
                await recvResp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("Messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var m in messages.EnumerateArray())
            {
                var body = m.GetProperty("Body").GetString()!;
                var receipt = m.GetProperty("ReceiptHandle").GetString()!;
                if (Array.IndexOf(bodies, body) >= 0)
                {
                    // MessageGroupId must be stamped on every entry.
                    Assert.Equal(groupId, m.GetProperty("Attributes").GetProperty("MessageGroupId").GetString());
                    ordered.Add((body, receipt));
                }
                else
                {
                    using var drop = await PostAsync(client, "DeleteMessage", new
                    {
                        QueueUrl = QueueUrl(queueName),
                        ReceiptHandle = receipt,
                    }).ConfigureAwait(false);
                }
            }
        }

        Assert.Equal(bodies.Length, ordered.Count);
        Assert.Equal(bodies, ordered.Select(o => o.Body).ToArray());

        var deleteEntries = ordered
            .Select((o, idx) => new { Id = "d" + idx, ReceiptHandle = o.ReceiptHandle })
            .ToArray();
        using (var delResp = await PostAsync(client, "DeleteMessageBatch", new
        {
            QueueUrl = QueueUrl(queueName),
            Entries = deleteEntries,
        }).ConfigureAwait(false))
        {
            var debug = await delResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(delResp.StatusCode == HttpStatusCode.OK,
                $"DeleteMessageBatch(FIFO) status={(int)delResp.StatusCode}, body={debug}");
            using var doc = JsonDocument.Parse(debug);
            Assert.Equal(deleteEntries.Length, doc.RootElement.GetProperty("Successful").GetArrayLength());
        }
    }

    private static string QueueUrl(string name) =>
        $"https://sqs.us-east-1.amazonaws.com/000000000000/{name}";

    /// <summary>
    /// Issue a SigV4-signed AWS-JSON-1.0 SQS request against the proxy
    /// (mirrors <see cref="SqsLifecycleEmulatorTests"/>).
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string operation, object jsonPayload)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(jsonPayload);
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
