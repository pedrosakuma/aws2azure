using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

/// <summary>
/// Slice-5 long-tail handler tests: <c>ListDeadLetterSourceQueues</c> walks
/// SB pages and filters by <c>ForwardDeadLetteredMessagesTo</c>, queue tags
/// round-trip through QueueDescription.UserMetadata, and permission stubs
/// gate on queue existence.
/// </summary>
public sealed class TailHandlersTests
{
    private const string AtomNs = AtomQueueXmlReader.AtomNs;
    private const string SbNs = AtomQueueXmlReader.SbNs;

    private static readonly ServiceBusCredentials Creds = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
    };

    [Fact]
    public async Task ListDeadLetterSourceQueues_returns_only_queues_whose_dlq_matches_target()
    {
        var handler = new ScriptedHandler();
        // 1) GET queue (existence probe) → 200 OK
        handler.Enqueue(req => Atom200("my-dlq", dlqTarget: null));
        // 2) GET /$Resources/queues?$skip=0&$top=100 → feed with 3 entries
        handler.Enqueue(req =>
        {
            Assert.Contains("$Resources/queues", req.RequestUri!.ToString());
            var feed = BuildFeed(
                ("source-a", "my-dlq"),     // ✓ matches
                ("unrelated", "other-dlq"), // ✗ mismatch
                ("source-b", "my-dlq"));    // ✓ matches
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(feed, System.Text.Encoding.UTF8, "application/atom+xml"),
            };
        });

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ListDeadLetterSourceQueues,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/my-dlq"));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("source-a", body);
        Assert.Contains("source-b", body);
        Assert.DoesNotContain("unrelated", body);
    }

    [Fact]
    public async Task ListDeadLetterSourceQueues_returns_NonExistentQueue_when_target_missing()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ListDeadLetterSourceQueues,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/ghost"));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("NonExistentQueue", ReadBody(ctx));
    }

    [Fact]
    public async Task ListQueueTags_returns_empty_tag_set_for_existing_queue()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ListQueueTags,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("ListQueueTagsResult", body);
        Assert.DoesNotContain("<Tag>", body);
    }

    [Fact]
    public void SqsQueueTagStore_round_trips_special_characters()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["env"] = "prod",
            ["unicode-✓"] = "a=b&c<d>\"'",
        };

        Assert.True(SqsQueueTagStore.TryEncode(tags, out var metadata));
        var decoded = SqsQueueTagStore.Decode(metadata);

        Assert.Equal(tags.Count, decoded.Count);
        Assert.Equal("prod", decoded["env"]);
        Assert.Equal("a=b&c<d>\"'", decoded["unicode-✓"]);
    }

    [Fact]
    public async Task TagQueue_ListQueueTags_UntagQueue_round_trips_through_user_metadata()
    {
        var userMetadata = string.Empty;
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));
        handler.Enqueue(async req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Equal("\"etag-q1\"", Assert.Single(req.Headers.IfMatch).Tag);
            userMetadata = ReadElementValue(await req.Content!.ReadAsStringAsync().ConfigureAwait(false), "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));
        handler.Enqueue(async req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            userMetadata = ReadElementValue(await req.Content!.ReadAsStringAsync().ConfigureAwait(false), "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        var tagCtx = NewCtx();
        await TailHandlers.HandleAsync(tagCtx, QueryParsed(SqsOperation.TagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"),
            ("Tag.1.Key", "env"),
            ("Tag.1.Value", "prod"),
            ("Tag.2.Key", "owner"),
            ("Tag.2.Value", "platform")), sb, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, tagCtx.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(userMetadata));

        var listCtx = NewCtx();
        await TailHandlers.HandleAsync(listCtx, QueryParsed(SqsOperation.ListQueueTags,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1")), sb, CancellationToken.None);
        var listBody = ReadBody(listCtx);
        Assert.Contains("<Key>env</Key>", listBody);
        Assert.Contains("<Value>prod</Value>", listBody);
        Assert.Contains("<Key>owner</Key>", listBody);

        var untagCtx = NewCtx();
        await TailHandlers.HandleAsync(untagCtx, QueryParsed(SqsOperation.UntagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"),
            ("TagKey.1", "env")), sb, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, untagCtx.Response.StatusCode);

        var finalListCtx = NewCtx();
        await TailHandlers.HandleAsync(finalListCtx, QueryParsed(SqsOperation.ListQueueTags,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1")), sb, CancellationToken.None);
        var finalBody = ReadBody(finalListCtx);
        Assert.DoesNotContain("<Key>env</Key>", finalBody);
        Assert.Contains("<Key>owner</Key>", finalBody);
    }

    [Fact]
    public async Task TagQueue_uses_etag_and_retries_precondition_failures_without_dropping_properties()
    {
        var handler = new ScriptedHandler();
        var putAttempts = 0;
        handler.Enqueue(_ => Atom200("q1", "old-dlq", string.Empty, "\"etag-1\"", maxDeliveryCount: 12));
        handler.Enqueue(req =>
        {
            putAttempts++;
            Assert.Equal("\"etag-1\"", Assert.Single(req.Headers.IfMatch).Tag);
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        });
        Assert.True(SqsQueueTagStore.TryEncode(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "platform" },
            out var concurrentMetadata));
        handler.Enqueue(_ => Atom200("q1", "new-dlq", concurrentMetadata, "\"etag-2\"", maxDeliveryCount: 7));
        handler.Enqueue(async req =>
        {
            putAttempts++;
            Assert.Equal("\"etag-2\"", Assert.Single(req.Headers.IfMatch).Tag);
            var body = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Contains("<LockDuration", body);
            Assert.Contains("<MaxDeliveryCount", body);
            Assert.Contains(">7<", body);
            Assert.Contains("<ForwardDeadLetteredMessagesTo", body);
            Assert.Contains(">new-dlq<", body);

            var decoded = SqsQueueTagStore.Decode(ReadElementValue(body, "UserMetadata"));
            Assert.Equal("prod", decoded["env"]);
            Assert.Equal("platform", decoded["owner"]);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ctx = NewCtx();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, QueryParsed(SqsOperation.TagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"),
            ("Tag.1.Key", "env"),
            ("Tag.1.Value", "prod")), sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(2, putAttempts);
    }

    [Theory]
    [InlineData(SqsOperation.TagQueue)]
    [InlineData(SqsOperation.UntagQueue)]
    public async Task Tag_mutations_reject_pre_existing_foreign_user_metadata(SqsOperation op)
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata: "plain operator metadata"));

        var parsed = op == SqsOperation.TagQueue
            ? QueryParsed(op,
                ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"),
                ("Tag.1.Key", "env"),
                ("Tag.1.Value", "prod"))
            : QueryParsed(op,
                ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"),
                ("TagKey.1", "env"));

        var ctx = NewCtx();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("InvalidParameterValue", body);
        Assert.Contains("UserMetadata", body);
        Assert.Contains("already in use", body);
    }

    [Fact]
    public async Task TagQueue_and_ListQueueTags_support_aws_json_protocol()
    {
        var userMetadata = string.Empty;
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));
        handler.Enqueue(async req =>
        {
            userMetadata = ReadElementValue(await req.Content!.ReadAsStringAsync().ConfigureAwait(false), "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        var tagCtx = NewCtx();
        await TailHandlers.HandleAsync(tagCtx, JsonParsed(SqsOperation.TagQueue,
            "{\"QueueUrl\":\"https://sqs.us-east-1.amazonaws.com/000000000000/q1\",\"Tags\":{\"env\":\"prod\"}}"),
            sb, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, tagCtx.Response.StatusCode);

        var listCtx = NewCtx();
        await TailHandlers.HandleAsync(listCtx, JsonParsed(SqsOperation.ListQueueTags,
            "{\"QueueUrl\":\"https://sqs.us-east-1.amazonaws.com/000000000000/q1\"}"),
            sb, CancellationToken.None);

        var body = ReadBody(listCtx);
        Assert.Contains("\"Tags\"", body);
        Assert.Contains("\"env\"", body);
        Assert.Contains("\"prod\"", body);
    }

    [Fact]
    public async Task UntagQueue_supports_aws_json_protocol()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["env"] = "prod",
            ["owner"] = "platform",
        };
        Assert.True(SqsQueueTagStore.TryEncode(tags, out var userMetadata));
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));
        handler.Enqueue(async req =>
        {
            userMetadata = ReadElementValue(await req.Content!.ReadAsStringAsync().ConfigureAwait(false), "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null, userMetadata));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        var untagCtx = NewCtx();
        await TailHandlers.HandleAsync(untagCtx, JsonParsed(SqsOperation.UntagQueue,
            "{\"QueueUrl\":\"https://sqs.us-east-1.amazonaws.com/000000000000/q1\",\"TagKeys\":[\"env\"]}"),
            sb, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, untagCtx.Response.StatusCode);

        var listCtx = NewCtx();
        await TailHandlers.HandleAsync(listCtx, JsonParsed(SqsOperation.ListQueueTags,
            "{\"QueueUrl\":\"https://sqs.us-east-1.amazonaws.com/000000000000/q1\"}"),
            sb, CancellationToken.None);

        var body = ReadBody(listCtx);
        Assert.DoesNotContain("\"env\"", body);
        Assert.Contains("\"owner\"", body);
    }

    [Fact]
    public async Task TagQueue_rejects_tags_that_exceed_user_metadata_limit()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.TagQueue,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"),
            ("Tag.1.Key", "large"),
            ("Tag.1.Value", new string('x', 256)),
            ("Tag.2.Key", "large2"),
            ("Tag.2.Value", new string('y', 256)),
            ("Tag.3.Key", "large3"),
            ("Tag.3.Value", new string('z', 256)));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("UserMetadata limit", ReadBody(ctx));
    }

    [Theory]
    [InlineData(SqsOperation.AddPermission, "AddPermissionResponse")]
    [InlineData(SqsOperation.RemovePermission, "RemovePermissionResponse")]
    public async Task Stub_handlers_succeed_for_existing_queue(SqsOperation op, string envelope)
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", dlqTarget: null));

        var ctx = NewCtx();
        var parsed = QueryParsed(op,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/q1"));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains(envelope, ReadBody(ctx));
    }

    [Theory]
    [InlineData(SqsOperation.ListQueueTags)]
    [InlineData(SqsOperation.TagQueue)]
    [InlineData(SqsOperation.UntagQueue)]
    [InlineData(SqsOperation.AddPermission)]
    [InlineData(SqsOperation.RemovePermission)]
    public async Task Stub_handlers_return_NonExistentQueue_when_queue_missing(SqsOperation op)
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ctx = NewCtx();
        var parsed = QueryParsed(op,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/ghost"));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("NonExistentQueue", ReadBody(ctx));
    }

    [Fact]
    public async Task ListDeadLetterSourceQueues_honors_MaxResults_and_returns_NextToken()
    {
        var handler = new ScriptedHandler();
        // 1) Probe target
        handler.Enqueue(_ => Atom200("my-dlq", dlqTarget: null));
        // 2) First list page ($skip=0): two matches
        handler.Enqueue(req =>
        {
            Assert.Contains("$skip=0", req.RequestUri!.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildFeed(("src-a", "my-dlq"), ("src-b", "my-dlq")),
                    System.Text.Encoding.UTF8, "application/atom+xml"),
            };
        });
        // 3) Probe-next page ($skip=1, $top=1) → at least one more queue exists.
        handler.Enqueue(req =>
        {
            Assert.Contains("$skip=1", req.RequestUri!.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildFeed(("anything", null)),
                    System.Text.Encoding.UTF8, "application/atom+xml"),
            };
        });

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ListDeadLetterSourceQueues,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/my-dlq"),
            ("MaxResults", "1"));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("src-a", body);
        Assert.DoesNotContain("src-b", body);
        Assert.Contains("<NextToken>1</NextToken>", body);
    }

    [Fact]
    public async Task ListDeadLetterSourceQueues_resumes_from_NextToken()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("my-dlq", dlqTarget: null));
        // Caller supplies NextToken=5 → first list call must use $skip=5.
        handler.Enqueue(req =>
        {
            Assert.Contains("$skip=5", req.RequestUri!.Query);
            // Short page: signals end of namespace, no NextToken in response.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildFeed(("src-c", "my-dlq")),
                    System.Text.Encoding.UTF8, "application/atom+xml"),
            };
        });

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ListDeadLetterSourceQueues,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/my-dlq"),
            ("NextToken", "5"));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);
        await TailHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("src-c", body);
        Assert.DoesNotContain("<NextToken>", body);
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

    private static SqsParseResult JsonParsed(SqsOperation op, string jsonBody)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(jsonBody);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }
        return new SqsParseResult(SqsWireProtocol.AwsJson, op, dict, JsonBody: jsonBody, Error: null);
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
    }

    private static HttpResponseMessage Atom200(
        string name,
        string? dlqTarget,
        string? userMetadata = null,
        string eTag = "\"etag-q1\"",
        int? maxDeliveryCount = null)
    {
        var qd = "<QueueDescription xmlns=\"" + SbNs + "\">" +
                 "<LockDuration>PT30S</LockDuration>" +
                 (maxDeliveryCount is null ? string.Empty :
                   "<MaxDeliveryCount>" + maxDeliveryCount.Value + "</MaxDeliveryCount>") +
                 (dlqTarget is null ? string.Empty :
                    "<ForwardDeadLetteredMessagesTo>" + dlqTarget + "</ForwardDeadLetteredMessagesTo>") +
                 (userMetadata is null ? string.Empty :
                    "<UserMetadata>" + WebUtility.HtmlEncode(userMetadata) + "</UserMetadata>") +
                 "</QueueDescription>";
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<entry xmlns=\"" + AtomNs + "\">" +
              "<title>" + name + "</title>" +
              "<content type=\"application/xml\">" + qd + "</content>" +
            "</entry>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/atom+xml"),
        };
        response.Headers.ETag = new EntityTagHeaderValue(eTag);
        return response;
    }

    private static string BuildFeed(params (string Name, string? DlqTarget)[] entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<feed xmlns=\"").Append(AtomNs).Append("\">");
        foreach (var (name, dlq) in entries)
        {
            sb.Append("<entry>");
            sb.Append("<title>").Append(name).Append("</title>");
            sb.Append("<content type=\"application/xml\">");
            sb.Append("<QueueDescription xmlns=\"").Append(SbNs).Append("\">");
            if (dlq is not null)
                sb.Append("<ForwardDeadLetteredMessagesTo>").Append(dlq).Append("</ForwardDeadLetteredMessagesTo>");
            sb.Append("</QueueDescription></content></entry>");
        }
        sb.Append("</feed>");
        return sb.ToString();
    }

    private static string ReadElementValue(string xml, string name)
    {
        var startTag = "<" + name + ">";
        var endTag = "</" + name + ">";
        var start = xml.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0)
        {
            var emptyTag = "<" + name + " />";
            if (xml.Contains(emptyTag, StringComparison.Ordinal)) return string.Empty;
            return string.Empty;
        }
        start += startTag.Length;
        var end = xml.IndexOf(endTag, start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : WebUtility.HtmlDecode(xml[start..end]);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> builder) =>
            _responses.Enqueue(request => Task.FromResult(builder(request)));

        public void Enqueue(Func<HttpRequestMessage, Task<HttpResponseMessage>> builder) => _responses.Enqueue(builder);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("ScriptedHandler ran out of scripted responses for " + request.RequestUri),
                });
            }
            var build = _responses.Dequeue();
            return build(request);
        }
    }
}
