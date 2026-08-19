using System;
using System.Collections.Generic;
using System.Globalization;
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
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public sealed class QueueMetadataHandlersTests
{
    private const string AtomNs = AtomQueueXmlReader.AtomNs;
    private const string SbNs = AtomQueueXmlReader.SbNs;

    private static readonly ServiceBusCredentials Creds = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]),
    };

    [Fact]
    public void QueueMetadata_round_trips_tags_and_defaults()
    {
        var metadata = new SqsQueueTagStore.QueueMetadata(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["env"] = "prod",
            })
        {
            DelaySeconds = 12,
            ReceiveMessageWaitTimeSeconds = 7,
        };

        Assert.True(SqsQueueTagStore.TryEncodeMetadata(metadata, out var encoded));
        var decoded = SqsQueueTagStore.DecodeMetadata(encoded);

        Assert.Equal(12, decoded.DelaySeconds);
        Assert.Equal(7, decoded.ReceiveMessageWaitTimeSeconds);
        Assert.Equal("prod", decoded.Tags["env"]);
    }

    [Fact]
    public void Query_protocol_tag_parsers_accept_documented_single_item_shapes()
    {
        var createParsed = QueryParsed(SqsOperation.CreateQueue,
            ("QueueName", "meta-q"),
            ("Tag.Key", "env"),
            ("Tag.Value", "prod"));
        Assert.True(SqsQueueTagStore.TryParseCreateQueueTags(createParsed, out var createTags, out var createError));
        Assert.Null(createError);
        Assert.Equal("prod", createTags["env"]);

        var tagParsed = QueryParsed(SqsOperation.TagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/meta-q"),
            ("Tag.Key", "owner"),
            ("Tag.Value", "platform"));
        Assert.True(SqsQueueTagStore.TryParseTagQueueRequest(tagParsed, out var tagTags, out var tagError));
        Assert.Null(tagError);
        Assert.Equal("platform", tagTags["owner"]);

        var untagParsed = QueryParsed(SqsOperation.UntagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/meta-q"),
            ("TagKey", "owner"));
        Assert.True(SqsQueueTagStore.TryParseUntagQueueRequest(untagParsed, out var tagKeys, out var untagError));
        Assert.Null(untagError);
        Assert.Equal(["owner"], tagKeys);
    }

    [Fact]
    public async Task GetAsync_retries_unparseable_2xx_body_then_reports_queue_does_not_exist()
    {
        // Regression test for a real-Azure finding (PR #762): Azure Service
        // Bus's management-plane GetQueue can briefly answer a just-deleted
        // queue with a 2xx whose body isn't a well-formed Atom <entry> before
        // it settles into a clean 404. SqsQueueMetadataCache.GetAsync should
        // poll through that window and surface QueueDoesNotExist rather than
        // an InternalError, matching what AWS SQS itself does for a recently
        // deleted queue.
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<feed xmlns=\"" + AtomNs + "\"></feed>",
                Encoding.UTF8,
                "application/atom+xml"),
        });
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        var result = await SqsQueueMetadataCache.GetAsync(sb, "gone-queue", CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("AWS.SimpleQueueService.NonExistentQueue", result.Error!.Value.Code);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task GetQueueAttributes_returns_defaults_from_user_metadata()
    {
        var metadata = new SqsQueueTagStore.QueueMetadata
        {
            DelaySeconds = 9,
            ReceiveMessageWaitTimeSeconds = 4,
        };
        Assert.True(SqsQueueTagStore.TryEncodeMetadata(metadata, out var userMetadata));

        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("meta-q", userMetadata: userMetadata));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        var ctx = NewCtx();

        await QueueLifecycleHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.GetQueueAttributes,            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/meta-q"),            ("AttributeName.1", "DelaySeconds"),
            ("AttributeName.2", "ReceiveMessageWaitTimeSeconds")), sb, CancellationToken.None);

        var body = ReadBody(ctx);
        Assert.Contains("<Name>DelaySeconds</Name><Value>9</Value>", body);
        Assert.Contains("<Name>ReceiveMessageWaitTimeSeconds</Name><Value>4</Value>", body);
    }

    [Fact]
    public async Task CreateQueue_conflict_with_different_redrive_policy_returns_queue_name_exists()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        handler.Enqueue(_ => Atom200("meta-q", dlqTarget: "other-dlq", maxDeliveryCount: 5));

        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        var ctx = NewCtx();
        var redrivePolicy = JsonSerializer.Serialize(new
        {
            deadLetterTargetArn = "arn:aws:sqs:us-east-1:000000000000:expected-dlq",
            maxReceiveCount = 3,
        });

        await QueueLifecycleHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.CreateQueue,
            ("QueueName", "meta-q"),
            ("Attribute.1.Name", "RedrivePolicy"),
            ("Attribute.1.Value", redrivePolicy)), sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("QueueNameExists", ReadBody(ctx));
    }

    [Fact]
    public async Task ListQueueTags_query_path_without_queueurl_is_accepted()
    {
        var metadata = new SqsQueueTagStore.QueueMetadata(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["env"] = "prod",
            });
        Assert.True(SqsQueueTagStore.TryEncodeMetadata(metadata, out var userMetadata));

        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("path-q", userMetadata: userMetadata));

        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        var ctx = NewCtx("/000000000000/path-q");

        await TailHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.ListQueueTags), sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("<Key>env</Key>", ReadBody(ctx));
    }

    [Fact]
    public async Task SetQueueAttributes_preserves_existing_delay_when_only_wait_time_changes()
    {
        var existing = new SqsQueueTagStore.QueueMetadata(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner"] = "platform",
            })
        {
            DelaySeconds = 6,
            ReceiveMessageWaitTimeSeconds = 2,
        };
        Assert.True(SqsQueueTagStore.TryEncodeMetadata(existing, out var existingUserMetadata));

        string? updatedUserMetadata = null;
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("meta-q", userMetadata: existingUserMetadata));
        handler.Enqueue(async req =>
        {
            updatedUserMetadata = ReadElementValue(await req.Content!.ReadAsStringAsync().ConfigureAwait(false), "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        var ctx = NewCtx();

        await BatchAdminHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.SetQueueAttributes,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/meta-q"),
            ("Attribute.1.Name", "ReceiveMessageWaitTimeSeconds"),
            ("Attribute.1.Value", "11")), sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var updated = SqsQueueTagStore.DecodeMetadata(updatedUserMetadata);
        Assert.Equal(6, updated.DelaySeconds);
        Assert.Equal(11, updated.ReceiveMessageWaitTimeSeconds);
        Assert.Equal("platform", updated.Tags["owner"]);
    }

    [Fact]
    public async Task SendMessage_uses_stored_queue_default_delay_when_request_omits_delay()
    {
        var metadata = new SqsQueueTagStore.QueueMetadata
        {
            DelaySeconds = 4,
        };
        Assert.True(SqsQueueTagStore.TryEncodeMetadata(metadata, out var userMetadata));

        var before = DateTimeOffset.UtcNow;
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("meta-q", userMetadata: userMetadata));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Created));

        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        var ctx = NewCtx();

        await SendMessageHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/meta-q"),
            ("MessageBody", "hello")), sb, CancellationToken.None);

        var sendCall = Assert.Single(handler.Calls.FindAll(static call => call.Method == HttpMethod.Post));
        using var brokerProperties = JsonDocument.Parse(sendCall.BrokerProperties!);
        var scheduled = brokerProperties.RootElement.GetProperty("ScheduledEnqueueTimeUtc").GetDateTimeOffset();
        Assert.InRange(scheduled, before.AddSeconds(2), before.AddSeconds(8));
    }

    [Fact]
    public async Task ReceiveMessage_uses_stored_queue_default_wait_when_request_omits_wait()
    {
        const string queueName = "meta-receive-q";
        var metadata = new SqsQueueTagStore.QueueMetadata
        {
            ReceiveMessageWaitTimeSeconds = 4,
        };
        Assert.True(SqsQueueTagStore.TryEncodeMetadata(metadata, out var userMetadata));

        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200(queueName, userMetadata: userMetadata));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        var ctx = NewCtx();

        await ReceiveMessageHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{queueName}")), sb, CancellationToken.None);

        var receiveCall = Assert.Single(handler.Calls.FindAll(static call => call.Method == HttpMethod.Post));
        Assert.Contains("timeout=4", receiveCall.RequestUri);
    }

    [Fact]
    public async Task TagQueue_requires_tags_member()
    {
        var ctx = NewCtx();
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("meta-q"));
        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);

        await TailHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.TagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/meta-q")), sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MissingParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task UntagQueue_requires_tag_keys_member()
    {
        var ctx = NewCtx();
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("meta-q"));
        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);

        await TailHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.UntagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/meta-q")), sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MissingParameter", ReadBody(ctx));
    }

    private static AzureHttpClient NewHttpClient(HttpMessageHandler handler) =>
        new(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2),
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
        });

    private static HttpContext NewCtx(string path = "/")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("sqs.us-east-1.amazonaws.com");
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static SqsParseResult QueryParsed(SqsOperation op, params (string Name, string Value)[] kv)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in kv)
        {
            dict[k] = v;
        }

        return new SqsParseResult(SqsWireProtocol.Query, op, dict, JsonBody: null, Error: null);
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
    }

    private static HttpResponseMessage Atom200(
        string queueName,
        string? userMetadata = null,
        string etag = "\"etag-q\"",
        string? dlqTarget = null,
        int maxDeliveryCount = 10)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<entry xmlns=\"" + AtomNs + "\">" +
              "<title>" + queueName + "</title>" +
              "<updated>2026-07-17T01:02:03Z</updated>" +
              "<published>2026-07-16T01:02:03Z</published>" +
              "<content type=\"application/xml\">" +
                "<QueueDescription xmlns=\"" + SbNs + "\">" +
                  "<LockDuration>PT30S</LockDuration>" +
                  "<DefaultMessageTimeToLive>PT345600S</DefaultMessageTimeToLive>" +
                  "<MaxMessageSizeInKilobytes>1024</MaxMessageSizeInKilobytes>" +
                  "<MaxDeliveryCount>" + maxDeliveryCount.ToString(CultureInfo.InvariantCulture) + "</MaxDeliveryCount>" +
                  (dlqTarget is null ? string.Empty : "<ForwardDeadLetteredMessagesTo>" + dlqTarget + "</ForwardDeadLetteredMessagesTo>") +
                  (userMetadata is null ? string.Empty : "<UserMetadata>" + userMetadata + "</UserMetadata>") +
                "</QueueDescription>" +
              "</content>" +
            "</entry>";

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/atom+xml"),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag) },
        };
    }

    private static string ReadElementValue(string xml, string elementName)
    {
        var start = xml.IndexOf("<" + elementName + ">", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Element <{elementName}> was not found.");
        start += elementName.Length + 2;
        var end = xml.IndexOf("</" + elementName + ">", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Closing element </{elementName}> was not found.");
        return xml[start..end];
    }

    private sealed record CapturedRequest(HttpMethod Method, string? BrokerProperties, string? RequestUri);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();

        public List<CapturedRequest> Calls { get; } = new();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responses.Enqueue(request => Task.FromResult(responder(request)));

        public void Enqueue(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            => _responses.Enqueue(responder);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? brokerProperties = null;
            if (request.Headers.TryGetValues("BrokerProperties", out var values))
            {
                foreach (var value in values)
                {
                    brokerProperties = value;
                    break;
                }
            }

            Calls.Add(new CapturedRequest(request.Method, brokerProperties, request.RequestUri?.ToString()));
            Assert.True(_responses.Count > 0, $"No scripted response left for {request.Method} {request.RequestUri}");
            return await _responses.Dequeue()(request).ConfigureAwait(false);
        }
    }
}
