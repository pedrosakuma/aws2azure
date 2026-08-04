using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public sealed class RestChangeMessageVisibilityTests
{
    private static readonly ServiceBusCredentials Credentials = new()
    {
        Namespace = "test-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
    };

    [Fact]
    public async Task Visibility_zero_unlocks_message_with_put()
    {
        var handler = new RecordingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await ReceiveMessageHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", QueueUrl),
                ("ReceiptHandle", Receipt("message-1", "lock-1")),
                ("VisibilityTimeout", "0")),
            serviceBus,
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.EndsWith(
            "/queue/messages/message-1/lock-1?api-version=2021-05",
            request.RequestUri!.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.False(context.Response.Headers.ContainsKey("Aws2Azure-VisibilityClamped"));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Visibility_zero_maps_missing_lock_to_invalid_receipt_handle()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await ReceiveMessageHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", QueueUrl),
                ("ReceiptHandle", Receipt("message-1", "lock-1")),
                ("VisibilityTimeout", "0")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Visibility_zero_maps_missing_queue_to_nonexistent_queue()
    {
        var handler = new RecordingHandler(HttpStatusCode.Gone);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await ReceiveMessageHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", QueueUrl),
                ("ReceiptHandle", Receipt("message-1", "lock-1")),
                ("VisibilityTimeout", "0")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("NonExistentQueue", ReadBody(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Positive_visibility_renews_lock_with_post()
    {
        var handler = new RecordingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await ReceiveMessageHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", QueueUrl),
                ("ReceiptHandle", Receipt("message-2", "lock-2")),
                ("VisibilityTimeout", "30")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
        Assert.Equal("30", context.Response.Headers["Aws2Azure-VisibilityClamped"].ToString());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Positive_visibility_maps_missing_queue_to_nonexistent_queue()
    {
        var handler = new RecordingHandler(HttpStatusCode.Gone);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await ReceiveMessageHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", QueueUrl),
                ("ReceiptHandle", Receipt("message-2", "lock-2")),
                ("VisibilityTimeout", "30")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("NonExistentQueue", ReadBody(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_uses_unlock_for_zero_and_renew_for_positive_visibility()
    {
        var handler = new RecordingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await BatchAdminHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ChangeMessageVisibilityBatch,
                ("QueueUrl", QueueUrl),
                ("ChangeMessageVisibilityBatchRequestEntry.1.Id", "zero"),
                ("ChangeMessageVisibilityBatchRequestEntry.1.ReceiptHandle", Receipt("message-1", "lock-1")),
                ("ChangeMessageVisibilityBatchRequestEntry.1.VisibilityTimeout", "0"),
                ("ChangeMessageVisibilityBatchRequestEntry.2.Id", "positive"),
                ("ChangeMessageVisibilityBatchRequestEntry.2.ReceiptHandle", Receipt("message-2", "lock-2")),
                ("ChangeMessageVisibilityBatchRequestEntry.2.VisibilityTimeout", "30")),
            serviceBus,
            CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal("true", context.Response.Headers["Aws2Azure-VisibilityClampedBatch"].ToString());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Batch_visibility_zero_maps_missing_queue_per_entry()
    {
        var handler = new RecordingHandler(HttpStatusCode.Gone);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await BatchAdminHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ChangeMessageVisibilityBatch,
                ("QueueUrl", QueueUrl),
                ("ChangeMessageVisibilityBatchRequestEntry.1.Id", "zero"),
                ("ChangeMessageVisibilityBatchRequestEntry.1.ReceiptHandle", Receipt("message-1", "lock-1")),
                ("ChangeMessageVisibilityBatchRequestEntry.1.VisibilityTimeout", "0")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
        var xml = XDocument.Parse(ReadBody(context));
        var code = Assert.Single(xml.Descendants(), element => element.Name.LocalName == "Code").Value;
        Assert.Equal("AWS.SimpleQueueService.NonExistentQueue", code);
        Assert.False(context.Response.Headers.ContainsKey("Aws2Azure-VisibilityClampedBatch"));
    }

    private const string QueueUrl =
        "https://sqs.us-east-1.amazonaws.com/000000000000/queue";

    private static string Receipt(string messageId, string lockToken) =>
        ReceiptHandle.Encode(messageId, lockToken, "1", DateTimeOffset.UtcNow.AddMinutes(1));

    private static HttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("sqs.us-east-1.amazonaws.com");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static SqsParseResult QueryParsed(
        SqsOperation operation,
        params (string Name, string Value)[] values)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            parameters[name] = value;
        }
        return new SqsParseResult(
            SqsWireProtocol.Query,
            operation,
            parameters,
            JsonBody: null,
            Error: null);
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _statusCode = statusCode;
        }

        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
