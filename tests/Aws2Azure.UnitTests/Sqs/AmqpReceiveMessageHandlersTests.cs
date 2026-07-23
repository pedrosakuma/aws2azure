using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.UnitTests.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Transport;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Slice 8b.4c — AMQP-backed dispatcher tests. Spins up an in-process
/// broker simulator behind a real <see cref="ServiceBusReceiver"/> so
/// the handler exercises the full receive → settle path (including the
/// 8b.4b lock-token cache), then asserts the SQS-shaped responses.
/// </summary>
[Collection(SqsAmqpTestCollection.Name)]
public sealed class AmqpReceiveMessageHandlersTests
{
    private const string QueueName = "amqp-q";

    [Fact]
    public async Task ReceiveMessage_returns_messages_with_AMQP_receipt_handles()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray(), EncodeMessage("hello-1")),
            (Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa").ToByteArray(), EncodeMessage("hello-2")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MaxNumberOfMessages", "2"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("hello-1", body);
        Assert.Contains("hello-2", body);
        Assert.Contains("<ReceiptHandle>Mjo", body); // v2 AMQP handle prefix
        Assert.Equal(2, harness.Receiver.InFlightCount);
    }

    [Fact]
    public async Task ReceiveMessage_returns_partial_batch_without_waiting_for_long_poll_deadline()
    {
        await using var harness = await TestHarness.OpenAsync(
            QueueName,
            (Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray(),
                EncodeMessage("available")));
        var ctx = NewCtx();
        var parsed = QueryParsed(
            SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MaxNumberOfMessages", "10"),
            ("WaitTimeSeconds", "2"));

        var started = Stopwatch.GetTimestamp();
        await AmqpReceiveMessageHandlers.HandleAsync(
            ctx, parsed, harness.Provider, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Contains("available", ReadBody(ctx));
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Partial batch took {elapsed}.");
    }

    [Fact]
    public async Task ReceiveMessage_returns_empty_list_when_queue_is_empty()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        // ReceiveMessageResponse with no <Message> children.
        Assert.Contains("<ReceiveMessageResponse", body);
        Assert.DoesNotContain("<Message>", body);
    }

    [Fact]
    public async Task ReceiveMessage_dead_letters_redelivery_beyond_redrive_limit_before_exposing_it()
    {
        var tag = Guid.Parse("20202020-3030-4040-5050-606060606060").ToByteArray();
        await using var harness = await TestHarness.OpenAsync(
            QueueName,
            (tag, EncodeMessage(
                "over-limit",
                groupId: null,
                deadLetterSource: null,
                deadLetterReason: "user-reason",
                deadLetterErrorDescription: "user-description",
                deliveryCount: 1)));

        const string QueueXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<entry xmlns=\"http://www.w3.org/2005/Atom\">" +
              "<title>amqp-q</title>" +
              "<content type=\"application/xml\">" +
                "<QueueDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\">" +
                  "<MaxDeliveryCount>1</MaxDeliveryCount>" +
                  "<ForwardDeadLetteredMessagesTo>target-dlq</ForwardDeadLetteredMessagesTo>" +
                "</QueueDescription>" +
              "</content>" +
            "</entry>";
        var handler = new QueueDescriptionHandler(QueueXml);
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var management = new ServiceBusClient(http, new ServiceBusCredentials
        {
            Namespace = "http://127.0.0.1:5672",
            SasKeyName = "RootManageSharedAccessKey",
            SasKey = Convert.ToBase64String([1, 2, 3]),
        });

        var ctx = NewCtx();
        var parsed = QueryParsed(
            SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"));

        await AmqpReceiveMessageHandlers.HandleAsync(
            ctx, parsed, harness.Provider, management, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.DoesNotContain("<Message>", ReadBody(ctx));
        Assert.Equal(1, handler.CallCount);
        var forwarded = Assert.Single(harness.Provider.ForwardedMessages);
        Assert.Equal("target-dlq", forwarded.QueueName);
        Assert.Equal("over-limit", Encoding.UTF8.GetString(forwarded.Message.Body.Span));
        Assert.Equal(
            QueueName,
            forwarded.Message.ApplicationProperties![AmqpMessageTranslator.DeadLetterSourceProperty]);
        Assert.Equal("user-reason", forwarded.Message.ApplicationProperties["DeadLetterReason"]);
        Assert.Equal("user-description", forwarded.Message.ApplicationProperties["DeadLetterErrorDescription"]);
        Assert.Equal(
            "MaxReceiveCountExceeded",
            forwarded.Message.ApplicationProperties[AmqpMessageTranslator.DeadLetterReasonProperty]);
        await WaitForDispositionAsync(harness.Broker, deliveryId: 0);
        Assert.Equal(
            AmqpDispositionOutcome.Accepted,
            harness.Broker.Dispositions[0].Outcome);
        Assert.Equal(0, harness.Receiver.InFlightCount);
    }

    [Fact]
    public async Task ReceiveMessage_rejects_invalid_MaxNumberOfMessages()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MaxNumberOfMessages", "999"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameterValue", ReadBody(ctx));
    }

    [Fact]
    public async Task DeleteMessage_settles_in_flight_delivery_via_lock_token_cache()
    {
        var tag = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899").ToByteArray();
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (tag, EncodeMessage("to-delete")));

        // 1) Receive — populates the in-flight cache.
        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(receiveCtx));
        Assert.False(string.IsNullOrEmpty(handle));
        Assert.Equal(1, harness.Receiver.InFlightCount);

        // 2) Delete — settles via the cache → returns 200.
        var deleteCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(deleteCtx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);
        Assert.Contains("DeleteMessageResponse", ReadBody(deleteCtx));
        Assert.Equal(0, harness.Receiver.InFlightCount);
    }

    [Fact]
    public async Task DeleteMessage_returns_ReceiptHandleIsInvalid_on_cache_miss()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var stale = AmqpReceiptHandle.Encode(QueueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", stale)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(ctx));
    }

    [Fact]
    public async Task DeleteMessage_rejects_handle_minted_against_a_different_queue()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var crossQueueHandle = AmqpReceiptHandle.Encode("some-other-queue", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", crossQueueHandle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(ctx));
    }

    [Fact]
    public async Task DeleteMessage_rejects_REST_v1_handle()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var v1Handle = ReceiptHandle.Encode("msg", "tok", "1", DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", v1Handle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_with_renewlock_emits_clamp_header_when_granted_differs()
    {
        // Broker grants a ~30s expiry; client requests 300 → divergence
        // surfaces via the Aws2Azure-VisibilityClamped header.
        var lockToken = Guid.Parse("abababab-cdcd-efef-1212-343434343434");
        var grantedExpiry = DateTimeOffset.UtcNow.AddSeconds(30);
        grantedExpiry = DateTimeOffset.FromUnixTimeMilliseconds(grantedExpiry.ToUnixTimeMilliseconds());
        await using var harness = await TestHarness.OpenWithManagementAsync(
            QueueName, renewExpiry: grantedExpiry,
            messages: [(lockToken.ToByteArray(), EncodeMessage("renew-standard"))]);

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(receiveCtx));
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "300")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var clamp = ctx.Response.Headers["Aws2Azure-VisibilityClamped"].ToString();
        Assert.StartsWith("requested=300;granted=", clamp);
        Assert.Contains("ChangeMessageVisibilityResponse", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_maps_lock_lost_to_MessageNotInflight()
    {
        await using var harness = await TestHarness.OpenWithManagementAsync(
            QueueName,
            renewExpiry: DateTimeOffset.UtcNow,
            statusCode: 410,
            statusDescription: "MessageLockLost",
            errorCondition: "com.microsoft:message-lock-lost");

        var handle = AmqpReceiptHandle.Encode(QueueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "30")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageNotInflight", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_zero_calls_Abandon_on_receiver()
    {
        // visibility=0 → no $management round-trip; goes straight to
        // ServiceBusReceiver.AbandonAsync. Receive first so the lock
        // token lives in the receiver's in-flight cache; otherwise
        // Abandon returns false and we'd see MessageNotInflight.
        var tag = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToByteArray();
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (tag, EncodeMessage("to-abandon")));

        var rcv = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(rcv,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(rcv));
        Assert.Equal(1, harness.Receiver.InFlightCount);

        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "0")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.False(ctx.Response.Headers.ContainsKey("Aws2Azure-VisibilityClamped"));
        Assert.Equal(0, harness.Receiver.InFlightCount); // Abandon removed it.
        Assert.Contains("ChangeMessageVisibilityResponse", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_rejects_invalid_VisibilityTimeout()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var handle = AmqpReceiptHandle.Encode(QueueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "99999")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameterValue", ReadBody(ctx));
    }

    // --- FIFO (session-bound) receive ---------------------------------

    private const string FifoQueueName = "orders.fifo";

    [Fact]
    public async Task ReceiveMessage_on_fifo_queue_acquires_broker_assigned_session_and_mints_v3_handle()
    {
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, sessionId: "group-A",
            (Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray(), EncodeMessage("fifo-msg-1", groupId: "group-A")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("AttributeName.1", "All"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(1, harness.Provider.AcquireSessionCount);
        var body = ReadBody(ctx);
        Assert.Contains("fifo-msg-1", body);
        // v3 receipt handle (session-bound) — base64 prefix "Mzo".
        Assert.Contains("<ReceiptHandle>Mzo", body);
        // MessageGroupId surfaced from the message's properties.group-id.
        Assert.Contains("<Name>MessageGroupId</Name><Value>group-A</Value>", body);
    }

    [Fact]
    public async Task ReceiveMessage_on_fifo_queue_falls_back_to_receiver_session_id_when_group_id_absent()
    {
        // Producer that does not stamp properties.group-id explicitly:
        // the bound session-id on the receiver is still the right
        // MessageGroupId for SQS clients (SB couples the two anyway).
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, sessionId: "group-B",
            (Guid.Parse("22222222-3333-4444-5555-666666666666").ToByteArray(), EncodeMessage("fifo-msg-2")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("AttributeName.1", "MessageGroupId"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("<Name>MessageGroupId</Name><Value>group-B</Value>", body);
    }

    [Fact]
    public async Task ReceiveMessage_on_non_fifo_queue_does_not_call_session_acquire()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (Guid.Parse("33333333-4444-5555-6666-777777777777").ToByteArray(), EncodeMessage("std-msg")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(0, harness.Provider.AcquireSessionCount);
        var body = ReadBody(ctx);
        // Non-FIFO keeps emitting the v2 (non-session) receipt handle prefix.
        Assert.Contains("<ReceiptHandle>Mjo", body);
        Assert.DoesNotContain("Mzo", body);
    }

    [Fact]
    public async Task ReceiveMessage_on_empty_fifo_queue_bounds_session_acquisition_by_WaitTimeSeconds()
    {
        await using var harness = await TestHarness.OpenAsync(FifoQueueName);
        harness.Provider.BlockBrokerAssignedAcquire = true;
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("WaitTimeSeconds", "1"));
        var started = Stopwatch.GetTimestamp();

        await AmqpReceiveMessageHandlers.HandleAsync(
            ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.DoesNotContain("<Message>", ReadBody(ctx));
    }

    // --- Dead-letter surfacing ----------------------------------------

    [Fact]
    public async Task ReceiveMessage_surfaces_dead_letter_attributes_when_message_came_from_dlq()
    {
        // Simulate SB delivering a message originally enqueued on
        // "orders" but dead-lettered onto "orders/$DeadLetterQueue":
        // x-opt-deadletter-source carries the source queue name and
        // the application-properties carry the reason / description SB
        // stamps on dead-letter.
        var payload = EncodeMessage("dlq-payload", groupId: null,
            deadLetterSource: "orders/$DeadLetterQueue",
            deadLetterReason: "MaxDeliveryCountExceeded",
            deadLetterErrorDescription: "Message could not be consumed after 10 attempts.");
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (Guid.Parse("12345678-1234-1234-1234-1234567890ab").ToByteArray(), payload));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("AttributeName.1", "All"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("<Name>DeadLetterQueueSourceArn</Name><Value>arn:aws:sqs:us-east-1:000000000000:orders</Value>", body);
        Assert.Contains("<Name>Aws2Azure-DeadLetterReason</Name><Value>MaxDeliveryCountExceeded</Value>", body);
        Assert.Contains("<Name>Aws2Azure-DeadLetterErrorDescription</Name><Value>Message could not be consumed after 10 attempts.</Value>", body);
    }

    [Fact]
    public async Task ReceiveMessage_omits_dead_letter_attributes_for_non_dlq_messages()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444").ToByteArray(), EncodeMessage("live-msg")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("AttributeName.1", "All"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.DoesNotContain("DeadLetterQueueSourceArn", body);
        Assert.DoesNotContain("Aws2Azure-DeadLetter", body);
    }

    [Fact]
    public async Task ReceiveMessage_filters_dead_letter_attributes_by_AttributeNames()
    {
        var payload = EncodeMessage("dlq-payload", groupId: null,
            deadLetterSource: "src-q",
            deadLetterReason: "TestReason",
            deadLetterErrorDescription: "TestDescription");
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (Guid.Parse("bbbbbbbb-2222-3333-4444-555555555555").ToByteArray(), payload));

        var ctx = NewCtx();
        // Request only DeadLetterQueueSourceArn — the two Aws2Azure-*
        // attributes must NOT be emitted (no 'All' shorthand).
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("AttributeName.1", "DeadLetterQueueSourceArn"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        var body = ReadBody(ctx);
        Assert.Contains("DeadLetterQueueSourceArn", body);
        Assert.DoesNotContain("Aws2Azure-DeadLetterReason", body);
        Assert.DoesNotContain("Aws2Azure-DeadLetterErrorDescription", body);
    }

    [Fact]
    public async Task DeleteMessage_on_fifo_queue_routes_via_session_receiver()
    {
        const string SessionId = "group-A";
        var tag = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToByteArray();
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, SessionId,
            (tag, EncodeMessage("fifo-to-delete", groupId: SessionId)));

        // 1) Receive — mints a v3 handle and populates the session
        //    receiver's in-flight cache.
        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(receiveCtx));
        Assert.StartsWith("Mzo", handle);
        Assert.Equal(1, harness.Receiver.InFlightCount);

        // 2) Delete — must route via GetSessionReceiverAsync (the v2
        //    fall-back path would query the non-session receiver and
        //    miss the cache, returning ReceiptHandleIsInvalid).
        var deleteCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(deleteCtx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", handle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);
        Assert.Contains("DeleteMessageResponse", ReadBody(deleteCtx));
        Assert.Equal(0, harness.Receiver.InFlightCount);
        Assert.Equal(1, harness.Provider.InvalidateSessionCount);
    }

    [Fact]
    public async Task ChangeMessageVisibility_zero_on_fifo_queue_abandons_via_session_receiver()
    {
        const string SessionId = "group-A";
        var tag = Guid.Parse("ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb").ToByteArray();
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, SessionId,
            (tag, EncodeMessage("fifo-to-abandon", groupId: SessionId)));

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(receiveCtx));
        Assert.StartsWith("Mzo", handle);
        Assert.Equal(1, harness.Receiver.InFlightCount);

        var cmvCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(cmvCtx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "0")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, cmvCtx.Response.StatusCode);
        Assert.Equal(0, harness.Receiver.InFlightCount);
        Assert.Equal(1, harness.Provider.InvalidateSessionCount);
    }

    [Fact]
    public async Task DeleteMessage_on_fifo_queue_returns_invalid_handle_without_opening_session_on_cache_miss()
    {
        const string SessionId = "expired-session";
        // No session receiver wired: simulates the "session lock is
        // gone" path (proxy restarted, session evicted, lock expired).
        // The pool's TryGetExistingSessionReceiver must return null and
        // the handler must surface ReceiptHandleIsInvalid — crucially
        // without round-tripping to the broker to grab a new session
        // lock that would starve the MessageGroupId.
        await using var harness = await TestHarness.OpenAsync(FifoQueueName);

        var v3 = AmqpReceiptHandle.Encode(FifoQueueName, Guid.NewGuid(), DateTimeOffset.UtcNow, SessionId);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", v3)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(ctx));
    }

    [Fact]
    public async Task DeleteMessage_on_fifo_queue_returns_invalid_handle_when_session_receiver_disposed_under_settle()
    {
        // #262 idle-TTL sweeper race: the cached session receiver can be
        // evicted (disposed) between TryGetExistingSessionReceiver and the
        // CompleteAsync call. CompleteAsync then throws ObjectDisposedException.
        // A swept session's broker lock is gone, so this must surface as a
        // stale receipt handle (ReceiptHandleIsInvalid / 404) — not a generic
        // internal error — to stay wire-faithful with real SQS.
        const string SessionId = "group-A";
        var tag = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToByteArray();
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, SessionId,
            (tag, EncodeMessage("fifo-to-delete", groupId: SessionId)));

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(receiveCtx));

        // Sweeper evicts the idle session link out from under the settle.
        await harness.Receiver.DisposeAsync();

        var deleteCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(deleteCtx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", handle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, deleteCtx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(deleteCtx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_zero_on_fifo_queue_returns_invalid_handle_when_session_receiver_disposed_under_settle()
    {
        // CMV=0 abandons via the session receiver; mirror the DeleteMessage
        // sweeper-race case for the AbandonAsync settle path (#262).
        const string SessionId = "group-A";
        var tag = Guid.Parse("ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb").ToByteArray();
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, SessionId,
            (tag, EncodeMessage("fifo-to-abandon", groupId: SessionId)));

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(receiveCtx));

        await harness.Receiver.DisposeAsync();

        var cmvCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(cmvCtx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "0")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, cmvCtx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(cmvCtx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_positive_on_fifo_queue_renews_session_lock_and_emits_clamp_header()
    {
        const string SessionId = "group-A";
        var lockToken = Guid.Parse("10101010-2020-3030-4040-505050505050");
        var grantedExpiry = DateTimeOffset.UtcNow.AddSeconds(30);
        grantedExpiry = DateTimeOffset.FromUnixTimeMilliseconds(grantedExpiry.ToUnixTimeMilliseconds());
        await using var harness = await TestHarness.OpenWithManagementAsync(
            FifoQueueName, renewExpiry: grantedExpiry, sessionId: SessionId,
            messages: [(lockToken.ToByteArray(), EncodeMessage("renew-me", groupId: SessionId))]);

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}")),
            harness.Provider, CancellationToken.None);
        var v3 = ExtractReceiptHandle(ReadBody(receiveCtx));
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", v3),
                ("VisibilityTimeout", "300")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var clamp = ctx.Response.Headers["Aws2Azure-VisibilityClamped"].ToString();
        Assert.StartsWith("requested=300;granted=", clamp);
        Assert.Contains("ChangeMessageVisibilityResponse", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_positive_on_fifo_queue_registers_session_link_activity_after_renew()
    {
        // #262 Fix B: a successful FIFO renewal goes through $management,
        // which does not register as activity on the cached session-receiver
        // link. The handler must explicitly reset the link's idle clock
        // (TryGetExistingSessionReceiver) so the idle-TTL sweeper does not
        // dispose the very session the client just renewed.
        const string SessionId = "group-A";
        var lockToken = Guid.Parse("60606060-7070-8080-9090-a0a0a0a0a0a0");
        var grantedExpiry = DateTimeOffset.UtcNow.AddSeconds(30);
        grantedExpiry = DateTimeOffset.FromUnixTimeMilliseconds(grantedExpiry.ToUnixTimeMilliseconds());
        await using var harness = await TestHarness.OpenWithManagementAsync(
            FifoQueueName, renewExpiry: grantedExpiry, sessionId: SessionId,
            messages: [(lockToken.ToByteArray(), EncodeMessage("renew-activity", groupId: SessionId))]);

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}")),
            harness.Provider, CancellationToken.None);
        var v3 = ExtractReceiptHandle(ReadBody(receiveCtx));
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", v3),
                ("VisibilityTimeout", "300")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(harness.Provider.TryGetExistingSessionCount >= 1,
            "positive FIFO CMV must touch the session-receiver slot after renew");
    }

    [Fact]
    public async Task ChangeMessageVisibility_positive_on_fifo_queue_maps_session_lock_lost_to_MessageNotInflight()
    {
        const string SessionId = "group-B";
        var lockToken = Guid.Parse("b0b0b0b0-c0c0-d0d0-e0e0-f0f0f0f0f0f0");
        await using var harness = await TestHarness.OpenWithManagementAsync(
            FifoQueueName,
            renewExpiry: DateTimeOffset.UtcNow,
            statusCode: 410,
            statusDescription: "SessionLockLost",
            errorCondition: "com.microsoft:session-lock-lost",
            sessionId: SessionId,
            messages: [(lockToken.ToByteArray(), EncodeMessage("lost-session", groupId: SessionId))]);

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}")),
            harness.Provider, CancellationToken.None);
        var v3 = ExtractReceiptHandle(ReadBody(receiveCtx));
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", v3),
                ("VisibilityTimeout", "30")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageNotInflight", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_positive_on_fifo_queue_rejects_untracked_lock_token()
    {
        const string SessionId = "group-stale";
        await using var harness = await TestHarness.OpenSessionAsync(FifoQueueName, SessionId);
        var staleHandle = AmqpReceiptHandle.Encode(
            FifoQueueName, Guid.NewGuid(), DateTimeOffset.UtcNow, SessionId);
        var ctx = NewCtx();

        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ReceiptHandle", staleHandle),
                ("VisibilityTimeout", "30")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageNotInflight", ReadBody(ctx));
    }

    // --- DeleteMessageBatch / ChangeMessageVisibilityBatch (AMQP) -----

    [Fact]
    public async Task DeleteMessageBatch_settles_multiple_in_flight_deliveries()
    {
        var tag1 = Guid.Parse("aaaa1111-2222-3333-4444-555555555555").ToByteArray();
        var tag2 = Guid.Parse("bbbb1111-2222-3333-4444-555555555555").ToByteArray();
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (tag1, EncodeMessage("batch-1")),
            (tag2, EncodeMessage("batch-2")));

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("MaxNumberOfMessages", "2")),
            harness.Provider, CancellationToken.None);
        Assert.Equal(2, harness.Receiver.InFlightCount);
        var handles = ExtractAllReceiptHandles(ReadBody(receiveCtx));
        Assert.Equal(2, handles.Count);

        var deleteCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(deleteCtx,
            QueryParsed(SqsOperation.DeleteMessageBatch,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("DeleteMessageBatchRequestEntry.1.Id", "d1"),
                ("DeleteMessageBatchRequestEntry.1.ReceiptHandle", handles[0]),
                ("DeleteMessageBatchRequestEntry.2.Id", "d2"),
                ("DeleteMessageBatchRequestEntry.2.ReceiptHandle", handles[1])),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);
        var body = ReadBody(deleteCtx);
        Assert.Contains("<DeleteMessageBatchResponse", body);
        Assert.Contains("<Id>d1</Id>", body);
        Assert.Contains("<Id>d2</Id>", body);
        Assert.DoesNotContain("BatchResultErrorEntry", body);
        Assert.Equal(0, harness.Receiver.InFlightCount);
    }

    [Fact]
    public async Task DeleteMessageBatch_on_fifo_queue_settles_via_session_receiver()
    {
        const string SessionId = "group-A";
        var tag1 = Guid.Parse("11111111-aaaa-bbbb-cccc-111111111111").ToByteArray();
        var tag2 = Guid.Parse("22222222-aaaa-bbbb-cccc-222222222222").ToByteArray();
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, SessionId,
            (tag1, EncodeMessage("fifo-batch-1", groupId: SessionId)),
            (tag2, EncodeMessage("fifo-batch-2", groupId: SessionId)));

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("MaxNumberOfMessages", "2")),
            harness.Provider, CancellationToken.None);
        Assert.Equal(2, harness.Receiver.InFlightCount);
        var handles = ExtractAllReceiptHandles(ReadBody(receiveCtx));
        Assert.Equal(2, handles.Count);
        Assert.All(handles, h => Assert.StartsWith("Mzo", h));  // v3 session-bound

        var deleteCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(deleteCtx,
            QueryParsed(SqsOperation.DeleteMessageBatch,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("DeleteMessageBatchRequestEntry.1.Id", "f1"),
                ("DeleteMessageBatchRequestEntry.1.ReceiptHandle", handles[0]),
                ("DeleteMessageBatchRequestEntry.2.Id", "f2"),
                ("DeleteMessageBatchRequestEntry.2.ReceiptHandle", handles[1])),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);
        var body = ReadBody(deleteCtx);
        Assert.Contains("<Id>f1</Id>", body);
        Assert.Contains("<Id>f2</Id>", body);
        Assert.DoesNotContain("BatchResultErrorEntry", body);
        Assert.Equal(0, harness.Receiver.InFlightCount);
    }

    [Fact]
    public async Task DeleteMessageBatch_aggregates_per_entry_failures_for_invalid_handles()
    {
        var tag = Guid.Parse("cccc1111-2222-3333-4444-555555555555").ToByteArray();
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (tag, EncodeMessage("good-msg")));

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}")),
            harness.Provider, CancellationToken.None);
        var goodHandle = ExtractReceiptHandle(ReadBody(receiveCtx));

        var deleteCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(deleteCtx,
            QueryParsed(SqsOperation.DeleteMessageBatch,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("DeleteMessageBatchRequestEntry.1.Id", "ok"),
                ("DeleteMessageBatchRequestEntry.1.ReceiptHandle", goodHandle),
                ("DeleteMessageBatchRequestEntry.2.Id", "bad"),
                ("DeleteMessageBatchRequestEntry.2.ReceiptHandle", "not-a-real-handle")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);
        var body = ReadBody(deleteCtx);
        Assert.Contains("<Id>ok</Id>", body);
        Assert.Contains("<BatchResultErrorEntry>", body);
        Assert.Contains("<Id>bad</Id>", body);
        Assert.Contains("ReceiptHandleIsInvalid", body);
    }

    [Fact]
    public async Task ChangeMessageVisibilityBatch_zero_on_fifo_queue_abandons_each_via_session_receiver()
    {
        const string SessionId = "group-X";
        var tag1 = Guid.Parse("eeeeeeee-1111-2222-3333-444444444444").ToByteArray();
        var tag2 = Guid.Parse("eeeeeeee-5555-6666-7777-888888888888").ToByteArray();
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, SessionId,
            (tag1, EncodeMessage("fifo-cmv-1", groupId: SessionId)),
            (tag2, EncodeMessage("fifo-cmv-2", groupId: SessionId)));

        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("MaxNumberOfMessages", "2")),
            harness.Provider, CancellationToken.None);
        var handles = ExtractAllReceiptHandles(ReadBody(receiveCtx));
        Assert.Equal(2, handles.Count);
        Assert.Equal(2, harness.Receiver.InFlightCount);

        var cmvCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(cmvCtx,
            QueryParsed(SqsOperation.ChangeMessageVisibilityBatch,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
                ("ChangeMessageVisibilityBatchRequestEntry.1.Id", "c1"),
                ("ChangeMessageVisibilityBatchRequestEntry.1.ReceiptHandle", handles[0]),
                ("ChangeMessageVisibilityBatchRequestEntry.1.VisibilityTimeout", "0"),
                ("ChangeMessageVisibilityBatchRequestEntry.2.Id", "c2"),
                ("ChangeMessageVisibilityBatchRequestEntry.2.ReceiptHandle", handles[1]),
                ("ChangeMessageVisibilityBatchRequestEntry.2.VisibilityTimeout", "0")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, cmvCtx.Response.StatusCode);
        var body = ReadBody(cmvCtx);
        Assert.Contains("<ChangeMessageVisibilityBatchResponse", body);
        Assert.Contains("<Id>c1</Id>", body);
        Assert.Contains("<Id>c2</Id>", body);
        Assert.DoesNotContain("BatchResultErrorEntry", body);
        Assert.Equal(0, harness.Receiver.InFlightCount);
    }

    // --- harness -------------------------------------------------------

    private sealed class TestHarness : IAsyncDisposable
    {
        public required ServiceBusAmqpConnection Connection { get; init; }
        public required ServiceBusReceiver Receiver { get; init; }
        public required FakeAmqpReceiverProvider Provider { get; init; }
        public required ServiceBusBrokerSimulator Broker { get; init; }
        public Aws2Azure.Amqp.Connection.AmqpConnection? MgmtConnection { get; init; }
        public Aws2Azure.Amqp.Connection.AmqpSession? MgmtSession { get; init; }
        public ServiceBusManagementClient? Management { get; init; }
        public Task<Guid[]?>? MgmtBrokerTask { get; init; }
        public Aws2Azure.Amqp.Transport.IAmqpTransport? MgmtServer { get; init; }

        public static async Task<TestHarness> OpenAsync(string queueName,
            params (byte[] tag, byte[] payload)[] messages)
            => await OpenInternalAsync(queueName, sessionId: null, messages);

        /// <summary>
        /// Opens a session-bound receiver against the broker simulator.
        /// The simulator binds the requested session-id verbatim, so the
        /// returned <see cref="ServiceBusReceiver.SessionId"/> equals
        /// <paramref name="sessionId"/>. Use this to exercise the FIFO
        /// receive path (slice 7c.3c).
        /// </summary>
        public static async Task<TestHarness> OpenSessionAsync(string queueName, string sessionId,
            params (byte[] tag, byte[] payload)[] messages)
            => await OpenInternalAsync(queueName, sessionId: sessionId, messages);

        private static async Task<TestHarness> OpenInternalAsync(string queueName, string? sessionId,
            (byte[] tag, byte[] payload)[] messages)
        {
            var (client, server) = PipePairTransport.CreatePair();
            var broker = new ServiceBusBrokerSimulator(server);
            broker.Start();
            var conn = await ServiceBusAmqpConnection
                .OpenAsync(client, new FakeTokenProvider(), new AmqpConnectionSettings
                {
                    ContainerId = "test-client",
                    Hostname = "ns.servicebus.windows.net",
                    IdleTimeout = TimeSpan.Zero,
                })
                .WaitAsync(TimeSpan.FromSeconds(10));
            var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", queueName);

            ServiceBusReceiver receiver;
            if (sessionId is null)
            {
                receiver = await conn.OpenReceiverAsync(queueName, audience, prefetchCredit: 0)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                receiver = await conn.OpenSessionReceiverAsync(queueName, audience, sessionId, prefetchCredit: 0)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }

            var inbox = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>();
            foreach (var (tag, payload) in messages)
                inbox.Enqueue(new ServiceBusBrokerSimulator.DeliveryToSend(tag, payload));
            broker.Inbox[receiver.Link.Name] = inbox;

            return new TestHarness
            {
                Connection = conn,
                Receiver = receiver,
                Broker = broker,
                Provider = new FakeAmqpReceiverProvider(queueName, receiver,
                    brokerAssignedSessionReceiver: sessionId is null ? null : receiver),
            };
        }

        /// <summary>
        /// Opens a queue harness plus a dedicated management-client
        /// fixture (separate pipe-pair + AmqpConnection driven by
        /// <see cref="Aws2Azure.UnitTests.Amqp.ServiceBus.ManagementBrokerSimulator"/>).
        /// </summary>
        public static async Task<TestHarness> OpenWithManagementAsync(
            string queueName,
            DateTimeOffset renewExpiry,
            int statusCode = 200,
            string? statusDescription = "OK",
            string? errorCondition = null,
            string? sessionId = null,
            params (byte[] tag, byte[] payload)[] messages)
        {
            var baseHarness = sessionId is null
                ? await OpenAsync(queueName, messages)
                : await OpenSessionAsync(queueName, sessionId, messages);

            var (mgmtClient, mgmtServer) = PipePairTransport.CreatePair();
            var mgmtBrokerTask = Task.Run(async () =>
                await Aws2Azure.UnitTests.Amqp.ServiceBus.ManagementBrokerSimulator.RunFullAsync(
                    mgmtServer, renewExpiry, statusCode, statusDescription, errorCondition,
                    captureOperation: _ => { }));

            var mgmtConn = new Aws2Azure.Amqp.Connection.AmqpConnection(mgmtClient,
                new AmqpConnectionSettings
                {
                    ContainerId = "test-client-mgmt",
                    Hostname = "ns.servicebus.windows.net",
                    IdleTimeout = TimeSpan.Zero,
                });
            await mgmtConn.OpenAsync();
            var mgmtSession = await mgmtConn.BeginSessionAsync();
            var mgmt = await ServiceBusManagementClient.OpenAsync(
                mgmtSession,
                ServiceBusEndpoint.BuildManagementAddress(QueueName));

            return new TestHarness
            {
                Connection = baseHarness.Connection,
                Receiver = baseHarness.Receiver,
                Broker = baseHarness.Broker,
                Provider = new FakeAmqpReceiverProvider(queueName, baseHarness.Receiver, mgmt,
                    brokerAssignedSessionReceiver: sessionId is null ? null : baseHarness.Receiver),
                MgmtConnection = mgmtConn,
                MgmtSession = mgmtSession,
                Management = mgmt,
                MgmtBrokerTask = mgmtBrokerTask,
                MgmtServer = mgmtServer,
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (Management is not null) await Management.DisposeAsync();
            if (MgmtSession is not null) await MgmtSession.CloseAsync();
            if (MgmtConnection is not null) await MgmtConnection.CloseAsync();
            if (MgmtBrokerTask is not null)
            {
                try { await MgmtBrokerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            if (MgmtServer is not null) await MgmtServer.DisposeAsync();
            await Receiver.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FakeAmqpReceiverProvider : IAmqpReceiverProvider
    {
        private readonly string _expectedQueue;
        private readonly ServiceBusReceiver _receiver;
        private readonly ServiceBusReceiver? _brokerAssignedSessionReceiver;
        private readonly ServiceBusManagementClient? _management;
        public int InvalidateCount { get; private set; }
        public int InvalidateManagementCount { get; private set; }
        public int InvalidateSessionCount { get; private set; }
        public int AcquireSessionCount { get; private set; }
        public int TryGetExistingSessionCount { get; private set; }
        public bool BlockBrokerAssignedAcquire { get; set; }
        public List<(string QueueName, AmqpMessage Message)> ForwardedMessages { get; } = new();

        public FakeAmqpReceiverProvider(string queueName, ServiceBusReceiver receiver,
            ServiceBusManagementClient? management = null,
            ServiceBusReceiver? brokerAssignedSessionReceiver = null)
        {
            _expectedQueue = queueName;
            _receiver = receiver;
            _management = management;
            _brokerAssignedSessionReceiver = brokerAssignedSessionReceiver;
        }

        public Task<ServiceBusReceiver> GetReceiverAsync(string queueName, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedQueue, queueName);
            return Task.FromResult(_receiver);
        }

        public ServiceBusReceiver? TryGetExistingReceiver(string queueName)
        {
            Assert.Equal(_expectedQueue, queueName);
            return _receiver;
        }

        public Task<ServiceBusManagementClient> GetManagementClientAsync(string queueName, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedQueue, queueName);
            if (_management is null)
                throw new InvalidOperationException("Test harness did not wire a management client.");
            return Task.FromResult(_management);
        }

        public Task InvalidateAsync(string queueName, bool closeConnection)
        {
            InvalidateCount++;
            return Task.CompletedTask;
        }

        public Task InvalidateManagementClientAsync(string queueName)
        {
            InvalidateManagementCount++;
            return Task.CompletedTask;
        }

        public Task ForwardAsync(
            string queueName,
            AmqpMessage message,
            CancellationToken cancellationToken)
        {
            ForwardedMessages.Add((queueName, message));
            return Task.CompletedTask;
        }

        public Task InvalidateForwardSenderAsync(string queueName) => Task.CompletedTask;

        public Task<ServiceBusReceiver> GetSessionReceiverAsync(
            string queueName, string sessionId, CancellationToken cancellationToken)
        {
            // Slice 7c.3d switched the settle paths to TryGetExisting,
            // so GetSessionReceiverAsync is not reached for that flow.
            // Kept for future callers that need open-on-demand semantics.
            Assert.Equal(_expectedQueue, queueName);
            if (_brokerAssignedSessionReceiver is null)
                throw new NotSupportedException("Test harness did not wire a session receiver.");
            return Task.FromResult(_brokerAssignedSessionReceiver);
        }

        public ServiceBusReceiver? TryGetExistingSessionReceiver(string queueName, string sessionId)
        {
            Assert.Equal(_expectedQueue, queueName);
            TryGetExistingSessionCount++;
            // The fake tracks a single bound session receiver — once
            // wired (by OpenSessionAsync) it stays cached for the test
            // duration. Returning null lets tests exercise the
            // stale-handle path by constructing a fake with
            // brokerAssignedSessionReceiver: null.
            return _brokerAssignedSessionReceiver;
        }

        public AmqpReceiverLease? TryAcquireExistingSessionReceiver(
            string queueName,
            string sessionId)
        {
            var receiver = TryGetExistingSessionReceiver(queueName, sessionId);
            return receiver is null ? null : new AmqpReceiverLease(receiver);
        }

        public async Task<BrokerAssignedSessionReceiverResult> AcquireBrokerAssignedSessionReceiverAsync(
            string queueName, TimeSpan maxBrokerWait, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedQueue, queueName);
            if (BlockBrokerAssignedAcquire)
            {
                await Task.Delay(maxBrokerWait, cancellationToken);
                return new BrokerAssignedSessionReceiverResult(null, maxBrokerWait);
            }
            if (_brokerAssignedSessionReceiver is null)
                throw new NotSupportedException("Test harness did not wire a broker-assigned session receiver.");
            AcquireSessionCount++;
            return new BrokerAssignedSessionReceiverResult(
                _brokerAssignedSessionReceiver, TimeSpan.Zero);
        }

        public Task InvalidateSessionReceiverAsync(string queueName, string sessionId)
        {
            InvalidateSessionCount++;
            return Task.CompletedTask;
        }
    }

    // --- shared helpers ------------------------------------------------

    private static byte[] EncodeMessage(string body)
        => EncodeMessage(body, groupId: null);

    private static byte[] EncodeMessage(string body, string? groupId, uint? deliveryCount = null)
        => EncodeMessage(body, groupId: groupId, deadLetterSource: null,
            deadLetterReason: null, deadLetterErrorDescription: null, deliveryCount);

    private static byte[] EncodeMessage(string body, string? groupId,
        string? deadLetterSource,
        string? deadLetterReason,
        string? deadLetterErrorDescription,
        uint? deliveryCount = null)
    {
        // Build the optional message-annotations + application-properties
        // sections by hand (AmqpMessage.Write does not author annotations
        // — see its doc-comment) and prefix them onto a properties+body
        // payload that AmqpMessage.Write does emit. The on-wire ordering
        // header → message-annotations → properties → application-properties
        // → body is what real Service Bus produces; the proxy's parser is
        // tolerant to any subset.
        var prefix = new Aws2Azure.UnitTests.Amqp.Framing.SectionWriter();
        if (deliveryCount is { } count)
        {
            prefix.WriteDescribed(Aws2Azure.Amqp.Framing.MessageSectionDescriptor.Header);
            prefix.BeginList8(count: 5);
            prefix.WriteNull();
            prefix.WriteNull();
            prefix.WriteNull();
            prefix.WriteNull();
            prefix.WriteUInt(count);
            prefix.EndList8();
        }
        if (!string.IsNullOrEmpty(deadLetterSource))
        {
            prefix.WriteDescribed(Aws2Azure.Amqp.Framing.MessageSectionDescriptor.MessageAnnotations);
            prefix.BeginMap8(pairCount: 1);
            prefix.WriteSymbol(Aws2Azure.Amqp.Framing.AmqpMessageAnnotations.KeyDeadLetterSource);
            prefix.WriteString(deadLetterSource!);
            prefix.EndMap8();
        }

        var msg = new AmqpMessage { Body = Encoding.UTF8.GetBytes(body) };
        if (!string.IsNullOrEmpty(groupId))
            msg.Properties = msg.Properties with { GroupId = groupId };
        if (!string.IsNullOrEmpty(deadLetterReason) || !string.IsNullOrEmpty(deadLetterErrorDescription))
        {
            var app = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(deadLetterReason)) app["DeadLetterReason"] = deadLetterReason;
            if (!string.IsNullOrEmpty(deadLetterErrorDescription)) app["DeadLetterErrorDescription"] = deadLetterErrorDescription;
            msg.ApplicationProperties = app;
        }
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            msg.Write(rented, out var written);
            if (prefix.Length == 0)
                return rented.AsSpan(0, written).ToArray();
            var head = prefix.ToArray();
            var result = new byte[head.Length + written];
            head.CopyTo(result, 0);
            rented.AsSpan(0, written).CopyTo(result.AsSpan(head.Length));
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task WaitForDispositionAsync(
        ServiceBusBrokerSimulator broker,
        uint deliveryId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (broker.Dispositions.ContainsKey(deliveryId))
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Disposition for delivery-id {deliveryId} did not arrive in time.");
    }

    private sealed class QueueDescriptionHandler(string xml) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/atom+xml"),
            });
        }
    }

    private static HttpContext NewCtx()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("sqs.us-east-1.amazonaws.com");
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static SqsParseResult QueryParsed(SqsOperation op, params (string Name, string Value)[] kv)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in kv) dict[k] = v;
        return new SqsParseResult(SqsWireProtocol.Query, op, dict, JsonBody: null, Error: null);
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
    }

    private static string ExtractReceiptHandle(string xml)
    {
        const string open = "<ReceiptHandle>";
        const string close = "</ReceiptHandle>";
        var i = xml.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var j = xml.IndexOf(close, i, StringComparison.Ordinal);
        if (j < 0) return string.Empty;
        return xml.Substring(i + open.Length, j - i - open.Length);
    }

    private static List<string> ExtractAllReceiptHandles(string xml)
    {
        const string open = "<ReceiptHandle>";
        const string close = "</ReceiptHandle>";
        var list = new List<string>();
        var cursor = 0;
        while (true)
        {
            var i = xml.IndexOf(open, cursor, StringComparison.Ordinal);
            if (i < 0) break;
            var j = xml.IndexOf(close, i, StringComparison.Ordinal);
            if (j < 0) break;
            list.Add(xml.Substring(i + open.Length, j - i - open.Length));
            cursor = j + close.Length;
        }
        return list;
    }
}
