using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class PublishBatchHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_successful_entries()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishBatchHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("PublishBatchRequestEntries.member.1.Id", "a"),
                ("PublishBatchRequestEntries.member.1.Message", "one"),
                ("PublishBatchRequestEntries.member.2.Id", "b"),
                ("PublishBatchRequestEntries.member.2.Message", "two")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(2, sender.BatchCall!.Value.Messages.Count);
        var body = ReadBody(context);
        Assert.Contains("<Successful>", body);
        Assert.Contains("<Id>a</Id>", body);
        Assert.Contains("<Id>b</Id>", body);
        Assert.DoesNotContain("<Failed><member>", body);
    }

    [Fact]
    public async Task HandleAsync_routes_to_event_grid_when_topic_backend_matches()
    {
        var context = NewContext();
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

        await PublishBatchHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("PublishBatchRequestEntries.member.1.Id", "a"),
                ("PublishBatchRequestEntries.member.1.Message", "one")),
            credentials,
            eventGridCredentials: null,
            new SnsSettings(),
            new FakeSnsAmqpSender(),
            eventGridPublisher,
            CancellationToken.None);

        Assert.NotNull(eventGridPublisher.BatchCall);
        Assert.Equal("https://orders.eastus-1.eventgrid.azure.net/api/events", eventGridPublisher.BatchCall!.Value.Destination.Endpoint);
        Assert.Equal("per-topic-key", eventGridPublisher.BatchCall.Value.Destination.AccessKey);
        Assert.Single(eventGridPublisher.BatchCall.Value.Messages);
    }

    [Fact]
    public async Task HandleAsync_returns_per_entry_outcomes_for_partial_failures()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender((_, _, _, messages, _) => Task.FromResult(new SnsBatchSendResult(
        [
            new SnsBatchSendOutcome(true, null, null, false),
            new SnsBatchSendOutcome(false, "InternalFailure", "broker rejected", false),
        ])));

        await PublishBatchHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("PublishBatchRequestEntries.member.1.Id", "a"),
                ("PublishBatchRequestEntries.member.1.Message", "one"),
                ("PublishBatchRequestEntries.member.2.Id", "b"),
                ("PublishBatchRequestEntries.member.2.Message", "two")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("<Successful>", body);
        Assert.Contains("<Failed>", body);
        Assert.Contains("<Id>a</Id>", body);
        Assert.Contains("<Id>b</Id>", body);
        Assert.Contains("broker rejected", body);
    }

    [Fact]
    public async Task HandleAsync_rejects_duplicate_ids_as_request_error()
    {
        var context = NewContext();

        await PublishBatchHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("PublishBatchRequestEntries.member.1.Id", "dup"),
                ("PublishBatchRequestEntries.member.1.Message", "one"),
                ("PublishBatchRequestEntries.member.2.Id", "dup"),
                ("PublishBatchRequestEntries.member.2.Message", "two")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            new FakeSnsAmqpSender(),
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(context));
        Assert.Contains("duplicate Id", ReadBody(context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task HandleAsync_rejects_invalid_entry_counts_as_request_error(int entryCount)
    {
        var pairs = new List<(string Key, string Value)> { ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders") };
        for (var i = 1; i <= entryCount; i++)
        {
            pairs.Add(($"PublishBatchRequestEntries.member.{i}.Id", $"id-{i}"));
            pairs.Add(($"PublishBatchRequestEntries.member.{i}.Message", $"msg-{i}"));
        }

        var context = NewContext();
        await PublishBatchHandler.HandleAsync(
            context,
            NewParseResult(pairs.ToArray()),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            new FakeSnsAmqpSender(),
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_marks_all_entries_failed_when_connection_fails()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender((_, _, _, _, _) => throw new SnsAmqpException(
            "failed",
            new InvalidOperationException(),
            SnsAmqpFailureKind.Transient,
            description: "connection dropped"));

        await PublishBatchHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("PublishBatchRequestEntries.member.1.Id", "a"),
                ("PublishBatchRequestEntries.member.1.Message", "one"),
                ("PublishBatchRequestEntries.member.2.Id", "b"),
                ("PublishBatchRequestEntries.member.2.Message", "two")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.DoesNotContain("<Successful><member>", body);
        Assert.Contains("<Failed>", body);
        Assert.Contains("<Id>a</Id>", body);
        Assert.Contains("<Id>b</Id>", body);
        Assert.Contains("connection dropped", body);
        Assert.DoesNotContain("<ErrorResponse", body);
    }

    [Fact]
    public async Task HandleAsync_renders_throttled_batch_outcomes_as_sns_throttled_failures()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender((_, _, _, messages, _) =>
        {
            var outcome = new SnsBatchSendOutcome(
                false,
                "Throttled",
                "Azure Service Bus Topics throttled the publish request; retry with back-off.",
                SenderFault: true);
            var outcomes = new SnsBatchSendOutcome[messages.Count];
            Array.Fill(outcomes, outcome);
            return Task.FromResult(new SnsBatchSendResult(outcomes));
        });

        await PublishBatchHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("PublishBatchRequestEntries.member.1.Id", "a"),
                ("PublishBatchRequestEntries.member.1.Message", "one"),
                ("PublishBatchRequestEntries.member.2.Id", "b"),
                ("PublishBatchRequestEntries.member.2.Message", "two")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("<Failed>", body);
        Assert.Contains("<Code>Throttled</Code>", body);
        Assert.Contains("<SenderFault>true</SenderFault>", body);
        Assert.DoesNotContain("<ErrorResponse", body);
    }

    [Theory]
    [InlineData("Throttled", "Throttled", true)]
    [InlineData("ClientFatal", "InvalidParameter", true)]
    [InlineData("Transient", "InternalFailure", false)]
    [InlineData("ServerFatal", "InternalFailure", false)]
    public void CreateBatchOutcome_maps_failure_kind_to_faithful_wire_code(
        string kind,
        string expectedCode,
        bool expectedSenderFault)
    {
        var outcome = SnsAmqpSender.CreateBatchOutcome(new SnsAmqpException(
            "failed",
            new InvalidOperationException(),
            Enum.Parse<SnsAmqpFailureKind>(kind)));

        Assert.False(outcome.Succeeded);
        Assert.Equal(expectedCode, outcome.ErrorCode);
        Assert.Equal(expectedSenderFault, outcome.SenderFault);
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

        return new SnsParseResult(SnsOperation.PublishBatch, parameters, null);
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

    private sealed class FakeSnsAmqpSender(
        Func<ServiceBusTopicsCredentials, string, string, IReadOnlyList<SnsAmqpSendMessage>, CancellationToken, Task<SnsBatchSendResult>>? batchHandler = null)
        : ISnsAmqpSender
    {
        public (string NamespaceFqdn, string TopicName, IReadOnlyList<SnsAmqpSendMessage> Messages)? BatchCall { get; private set; }

        public Task SendAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, SnsAmqpSendMessage message, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SnsBatchSendResult> SendBatchAsync(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, IReadOnlyList<SnsAmqpSendMessage> messages, CancellationToken cancellationToken)
        {
            BatchCall = (namespaceFqdn, topicName, messages);
            return batchHandler?.Invoke(credentials, namespaceFqdn, topicName, messages, cancellationToken)
                ?? Task.FromResult(new SnsBatchSendResult(messages.Select(_ => new SnsBatchSendOutcome(true, null, null, false)).ToArray()));
        }
    }

    private sealed class FakeEventGridPublisher(
        Func<EventGridPublishDestination, IReadOnlyList<EventGridPublishMessage>, CancellationToken, Task<SnsBatchSendResult>>? batchHandler = null)
        : IEventGridPublisher
    {
        public (EventGridPublishDestination Destination, IReadOnlyList<EventGridPublishMessage> Messages)? BatchCall { get; private set; }

        public Task PublishAsync(EventGridPublishDestination destination, EventGridPublishMessage message, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SnsBatchSendResult> PublishBatchAsync(EventGridPublishDestination destination, IReadOnlyList<EventGridPublishMessage> messages, CancellationToken cancellationToken)
        {
            BatchCall = (destination, messages);
            return batchHandler?.Invoke(destination, messages, cancellationToken)
                ?? Task.FromResult(new SnsBatchSendResult(messages.Select(_ => new SnsBatchSendOutcome(true, null, null, false)).ToArray()));
        }
    }
}
