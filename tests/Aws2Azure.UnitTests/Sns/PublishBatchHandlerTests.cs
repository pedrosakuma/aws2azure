using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Amqp;
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
            sender,
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
            sender,
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
            new FakeSnsAmqpSender(),
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
            new FakeSnsAmqpSender(),
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
            sender,
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
}
