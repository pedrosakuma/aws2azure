using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

public sealed class SqsFairQueueCompatibilityTests
{
    private static readonly ServiceBusCredentials Credentials = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]),
    };

    [Fact]
    public async Task Rest_SendMessage_carries_standard_group_as_user_property_not_session_id()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await SendMessageHandlers.HandleAsync(
            context,
            QueryParsed(
                SqsOperation.SendMessage,
                ("QueueUrl", QueueUrl("standard")),
                ("MessageBody", "hello"),
                ("MessageGroupId", "tenant-a"),
                ("DelaySeconds", "0")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        using var brokerProperties = JsonDocument.Parse(request.BrokerProperties!);
        Assert.False(brokerProperties.RootElement.TryGetProperty("SessionId", out _));
        Assert.Equal("tenant-a", request.Headers[SqsMessageGroupMetadata.PropertyName]);
    }

    [Fact]
    public async Task Rest_SendMessageBatch_carries_each_standard_group_without_session_id()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await SendMessageHandlers.HandleAsync(
            context,
            QueryParsed(
                SqsOperation.SendMessageBatch,
                ("QueueUrl", QueueUrl("standard")),
                ("SendMessageBatchRequestEntry.1.Id", "one"),
                ("SendMessageBatchRequestEntry.1.MessageBody", "first"),
                ("SendMessageBatchRequestEntry.1.MessageGroupId", "tenant-a"),
                ("SendMessageBatchRequestEntry.1.DelaySeconds", "0"),
                ("SendMessageBatchRequestEntry.2.Id", "two"),
                ("SendMessageBatchRequestEntry.2.MessageBody", "second"),
                ("SendMessageBatchRequestEntry.2.MessageGroupId", "tenant-b"),
                ("SendMessageBatchRequestEntry.2.DelaySeconds", "0")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        using var envelope = JsonDocument.Parse(request.Body);
        Assert.Equal(2, envelope.RootElement.GetArrayLength());
        AssertStandardBatchEntry(envelope.RootElement[0], "tenant-a");
        AssertStandardBatchEntry(envelope.RootElement[1], "tenant-b");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rest_ReceiveMessage_only_returns_standard_group_when_requested(bool requestGroup)
    {
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello")),
            };
            response.Headers.TryAddWithoutValidation(
                "BrokerProperties",
                """
                {"MessageId":"message-1","LockToken":"lock-1","SequenceNumber":1,"DeliveryCount":1}
                """);
            response.Headers.TryAddWithoutValidation(
                SqsMessageGroupMetadata.PropertyName,
                "tenant-a");
            return response;
        });
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();
        var parameters = new List<(string Name, string Value)>
        {
            ("QueueUrl", QueueUrl("standard")),
            ("MaxNumberOfMessages", "1"),
            ("WaitTimeSeconds", "0"),
        };
        if (requestGroup)
            parameters.Add(("AttributeName.1", "MessageGroupId"));

        await ReceiveMessageHandlers.HandleAsync(
            context,
            QueryParsed(SqsOperation.ReceiveMessage, [.. parameters]),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Equal(requestGroup, body.Contains("MessageGroupId", StringComparison.Ordinal));
        Assert.Equal(requestGroup, body.Contains("tenant-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rest_FIFO_send_keeps_session_mapping_and_omits_standard_metadata()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);
        var context = NewContext();

        await SendMessageHandlers.HandleAsync(
            context,
            QueryParsed(
                SqsOperation.SendMessage,
                ("QueueUrl", QueueUrl("orders.fifo")),
                ("MessageBody", "ordered"),
                ("MessageGroupId", "group-1"),
                ("MessageDeduplicationId", "dedup-1"),
                ("DelaySeconds", "0")),
            serviceBus,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        using var brokerProperties = JsonDocument.Parse(request.BrokerProperties!);
        Assert.Equal("group-1", brokerProperties.RootElement.GetProperty("SessionId").GetString());
        Assert.False(request.Headers.ContainsKey(SqsMessageGroupMetadata.PropertyName));
    }

    private static void AssertStandardBatchEntry(JsonElement entry, string expectedGroupId)
    {
        Assert.False(entry.GetProperty("BrokerProperties").TryGetProperty("SessionId", out _));
        Assert.Equal(
            expectedGroupId,
            entry.GetProperty("UserProperties")
                .GetProperty(SqsMessageGroupMetadata.PropertyName)
                .GetString());
    }

    private static string QueueUrl(string queueName) =>
        $"https://sqs.us-east-1.amazonaws.com/000000000000/{queueName}";

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
        params (string Name, string Value)[] parameters)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
            dictionary[name] = value;
        return new SqsParseResult(
            SqsWireProtocol.Query,
            operation,
            dictionary,
            JsonBody: null,
            Error: null);
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private sealed record CapturedRequest(
        string? BrokerProperties,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in request.Headers)
                headers[header.Key] = string.Join(",", header.Value);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("BrokerProperties", out var brokerProperties);
            Requests.Add(new CapturedRequest(
                brokerProperties is null ? null : string.Join(",", brokerProperties),
                headers,
                body));
            return _responseFactory(request);
        }
    }
}
