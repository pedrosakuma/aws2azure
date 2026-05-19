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
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Slice-5 long-tail handler tests: <c>ListDeadLetterSourceQueues</c> walks
/// SB pages and filters by <c>ForwardDeadLetteredMessagesTo</c>, while the
/// tag / permission stubs all gate on queue existence.
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

    [Theory]
    [InlineData(SqsOperation.TagQueue, "TagQueueResponse")]
    [InlineData(SqsOperation.UntagQueue, "UntagQueueResponse")]
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

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
    }

    private static HttpResponseMessage Atom200(string name, string? dlqTarget)
    {
        var qd = "<QueueDescription xmlns=\"" + SbNs + "\">" +
                 "<LockDuration>PT30S</LockDuration>" +
                 (dlqTarget is null ? string.Empty :
                    "<ForwardDeadLetteredMessagesTo>" + dlqTarget + "</ForwardDeadLetteredMessagesTo>") +
                 "</QueueDescription>";
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<entry xmlns=\"" + AtomNs + "\">" +
              "<title>" + name + "</title>" +
              "<content type=\"application/xml\">" + qd + "</content>" +
            "</entry>";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/atom+xml"),
        };
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

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> builder) => _responses.Enqueue(builder);

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
            return Task.FromResult(build(request));
        }
    }
}
