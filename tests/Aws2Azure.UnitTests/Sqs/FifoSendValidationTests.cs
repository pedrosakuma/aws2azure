using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Slice-5 FIFO send-path validation: <c>.fifo</c> queues must reject
/// SendMessage / SendMessageBatch requests that omit <c>MessageGroupId</c>,
/// while standard queues accept MessageGroupId but still reject FIFO-only
/// MessageDeduplicationId.
/// </summary>
public sealed class FifoSendValidationTests
{
    private static readonly ServiceBusCredentials Creds = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
    };

    [Fact]
    public async Task SendMessage_on_fifo_queue_without_message_group_id_returns_MissingParameter()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/orders.fifo"),
            ("MessageBody", "hi"));

        using var http = new AzureHttpClient(new RecordingHandler(), ownsHandler: true);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("MissingParameter", body);
        Assert.Contains("MessageGroupId", body);
    }

    [Fact]
    public async Task SendMessage_on_standard_queue_with_message_group_id_is_accepted()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/plain"),
            ("MessageBody", "hi"),
            ("MessageGroupId", "g1"),
            ("DelaySeconds", "0"));

        var handler = new RecordingHandler(HttpStatusCode.Created);
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(1, handler.InvocationCount);
    }

    [Fact]
    public async Task SendMessage_on_standard_queue_with_dedup_id_is_rejected()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/plain"),
            ("MessageBody", "hi"),
            ("MessageDeduplicationId", "dedup-1"));

        using var http = new AzureHttpClient(new RecordingHandler(), ownsHandler: true);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("MessageDeduplicationId", body);
    }

    [Fact]
    public async Task SendMessageBatch_on_fifo_queue_rejects_entry_without_group_id()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/orders.fifo"),
            ("SendMessageBatchRequestEntry.1.Id", "e1"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "m1"),
            ("SendMessageBatchRequestEntry.1.MessageGroupId", "g1"),
            ("SendMessageBatchRequestEntry.1.MessageDeduplicationId", "d1"),
            ("SendMessageBatchRequestEntry.2.Id", "e2"),
            ("SendMessageBatchRequestEntry.2.MessageBody", "m2"),
            ("SendMessageBatchRequestEntry.2.MessageDeduplicationId", "d2")); // missing MessageGroupId

        using var http = new AzureHttpClient(new RecordingHandler(), ownsHandler: true);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("MissingParameter", body);
        Assert.Contains("MessageGroupId", body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tenant with space")]
    [InlineData("tenant\twith-tab")]
    public async Task SendMessage_rejects_message_group_id_with_invalid_characters(string groupId)
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/plain"),
            ("MessageBody", "hi"),
            ("MessageGroupId", groupId));

        var handler = new RecordingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageGroupId", ReadBody(ctx));
        Assert.Equal(0, handler.InvocationCount);
    }

    [Fact]
    public async Task SendMessageBatch_rejects_message_group_id_longer_than_128_characters()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/plain"),
            ("SendMessageBatchRequestEntry.1.Id", "e1"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "m1"),
            ("SendMessageBatchRequestEntry.1.MessageGroupId", new string('g', 129)));

        var handler = new RecordingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageGroupId", ReadBody(ctx));
        Assert.Equal(0, handler.InvocationCount);
    }

    // --- helpers ---------------------------------------------------------

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

    /// <summary>
    /// Captures whether the SB client was ever invoked. The validation paths
    /// short-circuit before any SB call, so a single request reaching here
    /// proves we leaked past the validator.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
        {
            _statusCode = statusCode;
        }

        public int InvocationCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
