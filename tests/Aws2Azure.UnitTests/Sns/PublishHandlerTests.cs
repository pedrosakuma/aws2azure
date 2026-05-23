using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class PublishHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_message_id_and_sends_utf8_body()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello world")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        var messageId = ReadElementValue(body, "MessageId");
        Assert.True(Guid.TryParse(messageId, out _));
        Assert.Equal(messageId, sender.SingleCall!.Value.Message.Properties.MessageId);
        Assert.Equal("hello world", Encoding.UTF8.GetString(sender.SingleCall.Value.Message.Body.Span));
        Assert.Contains("<SequenceNumber />", body);
    }

    [Fact]
    public async Task HandleAsync_routes_to_event_grid_when_topic_backend_matches()
    {
        var context = NewContext();
        var amqpSender = new FakeSnsAmqpSender();
        var eventGridPublisher = new FakeEventGridPublisher();
        var credentials = NewCredentials();
        credentials.Topics = new Dictionary<string, SnsTopicSettings>
        {
            ["orders"] = new()
            {
                Backend = SnsTopicBackend.EventGrid,
                EventGridTopicEndpoint = "https://orders.eastus-1.eventgrid.azure.net/api/events",
                EventGridAccessKey = "per-topic-key",
            },
        };

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello world")),
            credentials,
            eventGridCredentials: null,
            new SnsSettings(),
            amqpSender,
            eventGridPublisher,
            CancellationToken.None);

        Assert.Null(amqpSender.SingleCall);
        Assert.NotNull(eventGridPublisher.SingleCall);
        Assert.Equal("https://orders.eastus-1.eventgrid.azure.net/api/events", eventGridPublisher.SingleCall!.Value.Destination.Endpoint);
        Assert.Equal("per-topic-key", eventGridPublisher.SingleCall.Value.Destination.AccessKey);
        Assert.Equal("arn:aws:sns:us-west-2:000000000000:orders", eventGridPublisher.SingleCall.Value.Message.TopicArn);
    }

    [Fact]
    public async Task HandleAsync_routes_to_event_grid_when_default_backend_is_event_grid()
    {
        var context = NewContext();
        var eventGridPublisher = new FakeEventGridPublisher();
        var eventGridCredentials = new EventGridCredentials
        {
            Endpoint = "https://default.eastus-1.eventgrid.azure.net/api/events",
            AccessKey = "global-key",
        };

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello world")),
            NewCredentials(),
            eventGridCredentials,
            new SnsSettings { DefaultBackend = SnsTopicBackend.EventGrid },
            new FakeSnsAmqpSender(),
            eventGridPublisher,
            CancellationToken.None);

        Assert.NotNull(eventGridPublisher.SingleCall);
        Assert.Equal("https://default.eastus-1.eventgrid.azure.net/api/events", eventGridPublisher.SingleCall!.Value.Destination.Endpoint);
        Assert.Equal("global-key", eventGridPublisher.SingleCall.Value.Destination.AccessKey);
    }

    [Fact]
    public async Task HandleAsync_maps_subject_to_properties_and_application_properties()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("Message", "hello"),
                ("Subject", "subject-line")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal("subject-line", sender.SingleCall!.Value.Message.Properties.Subject);
        Assert.Equal("subject-line", sender.SingleCall.Value.Message.ApplicationProperties![SnsPublishSupport.SubjectPropertyName]);
    }

    [Fact]
    public async Task HandleAsync_maps_message_attributes_to_application_properties()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("Message", "hello"),
                ("MessageAttributes.entry.1.Name", "color"),
                ("MessageAttributes.entry.1.Value.DataType", "String"),
                ("MessageAttributes.entry.1.Value.StringValue", "blue"),
                ("MessageAttributes.entry.2.Name", "payload"),
                ("MessageAttributes.entry.2.Value.DataType", "Binary"),
                ("MessageAttributes.entry.2.Value.BinaryValue", "AQID")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        var appProperties = sender.SingleCall!.Value.Message.ApplicationProperties!;
        Assert.Equal("blue", appProperties["color"]);
        Assert.Equal("String", appProperties["color.DataType"]);
        Assert.Equal("AQID", appProperties["payload"]);
        Assert.Equal("Binary", appProperties["payload.DataType"]);
    }

    [Fact]
    public async Task HandleAsync_propagates_fifo_fields()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders.fifo"),
                ("Message", "hello"),
                ("MessageGroupId", "group-1"),
                ("MessageDeduplicationId", "dedup-1")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal("orders.fifo", sender.SingleCall!.Value.TopicName);
        Assert.Equal("group-1", sender.SingleCall.Value.Message.Properties.GroupId);
        Assert.Equal("dedup-1", sender.SingleCall.Value.Message.ApplicationProperties![SnsPublishSupport.DeduplicationPropertyName]);
    }

    [Fact]
    public async Task HandleAsync_preserves_message_structure_json_payload()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();
        const string payload = "{\"default\":\"hello\",\"email\":\"hola\"}";

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("Message", payload),
                ("MessageStructure", "json")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(payload, Encoding.UTF8.GetString(sender.SingleCall!.Value.Message.Body.Span));
    }

    [Fact]
    public async Task HandleAsync_returns_request_error_when_topic_arn_missing()
    {
        var context = NewContext();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            new FakeSnsAmqpSender(),
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(context));
        Assert.Contains("TopicArn", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_returns_request_error_when_message_empty()
    {
        var context = NewContext();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            new FakeSnsAmqpSender(),
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(context));
        Assert.Contains("Message", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_amqp_send_failure_to_sns_error()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender(sendHandler: (_, _, _, _, _) => throw new SnsAmqpException(
            "failed",
            new InvalidOperationException(),
            SnsAmqpFailureKind.Transient,
            description: "link detached"));

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains("InternalFailure", ReadBody(context));
        Assert.Contains("link detached", ReadBody(context));
    }

    private static ServiceBusTopicsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = "secret",
    };

    private static SnsParseResult NewParseResult(params (string Key, string Value)[] pairs)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            parameters[pair.Key] = pair.Value;
        }

        return new SnsParseResult(SnsOperation.Publish, parameters, null);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private static string ReadElementValue(string xml, string elementName)
    {
        var startTag = "<" + elementName + ">";
        var endTag = "</" + elementName + ">";
        var start = xml.IndexOf(startTag, StringComparison.Ordinal);
        var end = xml.IndexOf(endTag, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Element '{elementName}' not found in XML: {xml}");
        return xml[(start + startTag.Length)..end];
    }

    private sealed class FakeSnsAmqpSender(
        Func<ServiceBusTopicsCredentials, string, string, SnsAmqpSendMessage, CancellationToken, Task>? sendHandler = null,
        Func<ServiceBusTopicsCredentials, string, string, IReadOnlyList<SnsAmqpSendMessage>, CancellationToken, Task<SnsBatchSendResult>>? batchHandler = null)
        : ISnsAmqpSender
    {
        public (string NamespaceFqdn, string TopicName, SnsAmqpSendMessage Message)? SingleCall { get; private set; }

        public Task SendAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, SnsAmqpSendMessage message, CancellationToken cancellationToken)
        {
            SingleCall = (namespaceFqdn, topicName, message);
            return sendHandler?.Invoke(credentials, namespaceFqdn, topicName, message, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<SnsBatchSendResult> SendBatchAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, IReadOnlyList<SnsAmqpSendMessage> messages, CancellationToken cancellationToken)
            => batchHandler?.Invoke(credentials, namespaceFqdn, topicName, messages, cancellationToken)
                ?? Task.FromResult(new SnsBatchSendResult(messages.Select(_ => new SnsBatchSendOutcome(true, null, null, false)).ToArray()));
    }

    private sealed class FakeEventGridPublisher(
        Func<EventGridPublishDestination, EventGridPublishMessage, CancellationToken, Task>? sendHandler = null)
        : IEventGridPublisher
    {
        public (EventGridPublishDestination Destination, EventGridPublishMessage Message)? SingleCall { get; private set; }

        public Task PublishAsync(EventGridPublishDestination destination, EventGridPublishMessage message, CancellationToken cancellationToken)
        {
            SingleCall = (destination, message);
            return sendHandler?.Invoke(destination, message, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<SnsBatchSendResult> PublishBatchAsync(EventGridPublishDestination destination, IReadOnlyList<EventGridPublishMessage> messages, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
