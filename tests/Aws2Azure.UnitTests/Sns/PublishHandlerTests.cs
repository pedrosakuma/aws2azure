using System.Text;
using System.Net.Http;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Management;
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
             RejectingManagementClient(),
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
    public async Task HandleAsync_accepts_legacy_TargetArn_as_alias_for_TopicArn()
    {
        // Real AWS SNS's Publish API has accepted TargetArn as a
        // backward-compatible alias for publishing to a topic since before
        // TopicArn existed. Airflow's SnsPublishOperator (and other older
        // SNS clients) always send TargetArn, never TopicArn.
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TargetArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello world")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("hello world", Encoding.UTF8.GetString(sender.SingleCall!.Value.Message.Body.Span));
    }

    [Fact]
    public async Task HandleAsync_rejects_when_both_TopicArn_and_TargetArn_are_missing()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("Message", "hello world")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Null(sender.SingleCall);
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
             RejectingManagementClient(),
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
             RejectingManagementClient(),
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
             RejectingManagementClient(),
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
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        var appProperties = sender.SingleCall!.Value.Message.ApplicationProperties!;
        Assert.Equal("blue", appProperties["color"]);
        Assert.Equal("String", appProperties["color.DataType"]);
        Assert.Equal("AQID", appProperties["payload"]);
        Assert.Equal("Binary", appProperties["payload.DataType"]);
        Assert.Equal("blue", appProperties["aws2azure_sns_attr_636f6c6f72"]);
        Assert.Equal("AQID", appProperties["aws2azure_sns_attr_7061796c6f6164"]);
    }

    [Fact]
    public async Task HandleAsync_projects_string_array_and_leading_zero_number_attributes_for_filtering()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("Message", "hello"),
                ("MessageAttributes.entry.1.Name", "sports"),
                ("MessageAttributes.entry.1.Value.DataType", "String.Array"),
                ("MessageAttributes.entry.1.Value.StringValue", "[\"rugby\",\"tennis\"]"),
                ("MessageAttributes.entry.2.Name", "priority"),
                ("MessageAttributes.entry.2.Value.DataType", "Number"),
                ("MessageAttributes.entry.2.Value.StringValue", "001")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        var appProperties = sender.SingleCall!.Value.Message.ApplicationProperties!;
        Assert.Equal("[\"rugby\",\"tennis\"]", appProperties["aws2azure_sns_attr_73706f727473"]);
        Assert.Equal(true, appProperties["aws2azure_sns_attr_73706f727473_arr"]);
        Assert.Equal(1L, appProperties["aws2azure_sns_attr_7072696f72697479_num"]);
    }

    [Fact]
    public async Task HandleAsync_projects_large_integer_number_attributes_without_double_rounding()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("Message", "hello"),
                ("MessageAttributes.entry.1.Name", "id"),
                ("MessageAttributes.entry.1.Value.DataType", "Number"),
                ("MessageAttributes.entry.1.Value.StringValue", "9007199254740993")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        var appProperties = sender.SingleCall!.Value.Message.ApplicationProperties!;
        Assert.Equal(9007199254740993L, appProperties["aws2azure_sns_attr_6964_num"]);
    }

    [Fact]
    public async Task HandleAsync_projects_message_body_fields_into_filter_properties()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"),
                ("Message", "{\"detail\":{\"tenant\":\"blue\",\"priority\":5,\"active\":true}}")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        var appProperties = sender.SingleCall!.Value.Message.ApplicationProperties!;
        Assert.Equal("blue", appProperties["aws2azure_sns_body_363a64657461696c7c363a74656e616e74"]);
        Assert.Equal(5L, appProperties["aws2azure_sns_body_363a64657461696c7c383a7072696f72697479"]);
        Assert.Equal(true, appProperties["aws2azure_sns_body_363a64657461696c7c363a616374697665"]);
    }

    [Fact]
    public async Task HandleAsync_propagates_fifo_fields()
    {
        SnsFifoPublishSupport.InvalidateServiceBusTopicState(SnsManagementClientTestSupport.NewCredentials(), "orders.fifo");
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();
        var metadata = System.Text.Json.JsonSerializer.Serialize(
            new SnsTopicMetadata
            {
                ContentBasedDeduplication = false,
            },
            SnsTopicJsonContext.Default.SnsTopicMetadata);

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
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
               Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.fifo", subscriptionCount: 0, requiresDuplicateDetection: true, userMetadata: metadata), Encoding.UTF8, "application/atom+xml"),
            })),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal("orders.fifo", sender.SingleCall!.Value.TopicName);
        Assert.Equal("group-1", sender.SingleCall.Value.Message.Properties.GroupId);
        Assert.Equal("dedup-1", sender.SingleCall.Value.Message.Properties.MessageId);
    }

    [Fact]
    public async Task HandleAsync_uses_content_based_deduplication_when_fifo_topic_allows_it()
    {
        SnsFifoPublishSupport.InvalidateServiceBusTopicState(SnsManagementClientTestSupport.NewCredentials(), "orders.fifo");
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();
        var metadata = System.Text.Json.JsonSerializer.Serialize(
            new SnsTopicMetadata
            {
                ContentBasedDeduplication = true,
            },
            SnsTopicJsonContext.Default.SnsTopicMetadata);

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders.fifo"),
                ("Message", "hello"),
                ("MessageGroupId", "group-1")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.fifo", subscriptionCount: 0, requiresDuplicateDetection: true, userMetadata: metadata), Encoding.UTF8, "application/atom+xml"),
            })),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", sender.SingleCall!.Value.Message.Properties.MessageId);
    }

    [Fact]
    public async Task HandleAsync_uses_legacy_fifo_duplicate_detection_as_content_based_deduplication_fallback()
    {
        SnsFifoPublishSupport.InvalidateServiceBusTopicState(SnsManagementClientTestSupport.NewCredentials(), "orders.fifo");
        var context = NewContext();
        var sender = new FakeSnsAmqpSender();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders.fifo"),
                ("Message", "hello"),
                ("MessageGroupId", "group-1")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.fifo", subscriptionCount: 0, requiresDuplicateDetection: true), Encoding.UTF8, "application/atom+xml"),
            })),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", sender.SingleCall!.Value.Message.Properties.MessageId);
    }

    [Fact]
    public async Task HandleAsync_rejects_missing_fifo_group_id()
    {
        SnsFifoPublishSupport.InvalidateServiceBusTopicState(SnsManagementClientTestSupport.NewCredentials(), "orders.fifo");
        var context = NewContext();
        var metadata = System.Text.Json.JsonSerializer.Serialize(
            new SnsTopicMetadata
            {
                ContentBasedDeduplication = true,
            },
            SnsTopicJsonContext.Default.SnsTopicMetadata);

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders.fifo"),
                ("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.fifo", subscriptionCount: 0, requiresDuplicateDetection: true, userMetadata: metadata), Encoding.UTF8, "application/atom+xml"),
            })),
            new FakeSnsAmqpSender(),
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("MessageGroupId", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_fifo_publish_on_event_grid_backend()
    {
        var context = NewContext();

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(
                ("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders.fifo"),
                ("Message", "hello"),
                ("MessageGroupId", "group-1"),
                ("MessageDeduplicationId", "dedup-1")),
            NewCredentials(),
            eventGridCredentials: new EventGridCredentials
            {
                Endpoint = "https://default.eastus-1.eventgrid.azure.net/api/events",
                AccessKey = "global-key",
            },
            new SnsSettings { DefaultBackend = SnsTopicBackend.EventGrid },
            RejectingManagementClient(),
            new FakeSnsAmqpSender(),
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Event Grid backend cannot honor SNS FIFO semantics", ReadBody(context));
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
             RejectingManagementClient(),
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
             RejectingManagementClient(),
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
             RejectingManagementClient(),
            new FakeSnsAmqpSender(),
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", ReadBody(context));
        Assert.Contains("Message", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_amqp_entity_unavailable_to_sns_not_found_error()
    {
        // Regression test for a real-Azure finding (PR #762): Azure Service
        // Bus Topics' CBS put-token fails claim validation for a sender link
        // against a nonexistent topic before the link ever attaches,
        // surfacing as HTTP 404 (not 401/403) on the put-token response.
        // Since this deployment always authenticates AMQP sends with a
        // namespace-scoped, full-rights credential, that 404 at the CBS
        // layer can only mean the topic is missing — SnsAmqpSender
        // reclassifies it as EntityUnavailable so PublishHandler renders
        // SNS's native NotFound shape instead of an authorization error.
        var context = NewContext();
        var sender = new FakeSnsAmqpSender(sendHandler: (_, _, _, _, _) => throw new SnsAmqpException(
            "not found",
            new InvalidOperationException(),
            SnsAmqpFailureKind.EntityUnavailable,
            condition: null,
            description: "The messaging entity 'sb://ns.servicebus.windows.net/missing-topic' could not be found."));

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:missing-topic"), ("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains("NotFound", ReadBody(context));
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
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains("InternalFailure", ReadBody(context));
        Assert.Contains("link detached", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_amqp_timeout_to_retryable_sns_error()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender(sendHandler: (_, _, _, _, _) => throw new SnsAmqpException(
            "timed out",
            new TimeoutException(),
            SnsAmqpFailureKind.Transient,
            condition: "amqp:timeout"));

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("InternalFailure", body);
        Assert.Contains("<Type>Receiver</Type>", body);
    }

    [Fact]
    public async Task HandleAsync_maps_amqp_throttle_to_sns_throttled_error()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender(sendHandler: (_, _, _, _, _) => throw new SnsAmqpException(
            "throttled",
            new InvalidOperationException(),
            SnsAmqpFailureKind.Throttled));

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("Throttled", body);
        Assert.Contains("<Type>Sender</Type>", body);
    }

    [Fact]
    public async Task HandleAsync_maps_amqp_auth_failure_to_non_retryable_sns_error()
    {
        var context = NewContext();
        var sender = new FakeSnsAmqpSender(sendHandler: (_, _, _, _, _) => throw new SnsAmqpException(
            "denied",
            new InvalidOperationException(),
            SnsAmqpFailureKind.Auth));

        await PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("AuthorizationError", body);
        Assert.Contains("<Type>Sender</Type>", body);
    }

    [Fact]
    public async Task HandleAsync_propagates_amqp_cancellation_without_success_body()
    {
        var context = NewContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var requestObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new FakeSnsAmqpSender(sendHandler: async (_, _, _, _, cancellationToken) =>
        {
            requestObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        var pending = PublishHandler.HandleAsync(
            context,
            NewParseResult(("TopicArn", "arn:aws:sns:us-west-2:000000000000:orders"), ("Message", "hello")),
            NewCredentials(),
            eventGridCredentials: null,
            new SnsSettings(),
             RejectingManagementClient(),
            sender,
            new FakeEventGridPublisher(),
            cancellation.Token);

        await requestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(context.Response.HasStarted);
        Assert.Equal(0, context.Response.Body.Length);
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

    private static ServiceBusTopicsManagementClient RejectingManagementClient()
        => SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called."));

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
