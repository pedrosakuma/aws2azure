using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// GetQueueUrl request-validation coverage. The Tier-3 "queue-attributes-roundtrip"
/// conformance case (tests/Aws2Azure.Conformance/Sqs/SqsHappyPathMatrix.cs) relies
/// on this handler resolving the queue name it just created, so this fixture pins
/// the surrounding failure modes: missing QueueName, an invalid queue name, and
/// the SQS-native NonExistentQueue response — none of which had dedicated unit
/// coverage before this change (QueueLifecycleHandlers.GetQueueUrlAsync).
/// </summary>
public sealed class GetQueueUrlHandlerTests : IDisposable
{
    private const string AtomNs = AtomQueueXmlReader.AtomNs;

    private static readonly ServiceBusCredentials Creds = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]),
    };

    public GetQueueUrlHandlerTests()
    {
        SqsQueueMetadataCache.ResetForTesting();
    }

    public void Dispose()
    {
        SqsQueueMetadataCache.ResetForTesting();
    }

    [Fact]
    public async Task Missing_queue_name_returns_invalid_parameter_value()
    {
        var ctx = NewCtx();
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        await QueueLifecycleHandlers.HandleAsync(
            ctx, QueryParsed(SqsOperation.GetQueueUrl), sb, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameterValue", ReadBody(ctx));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Invalid_queue_name_is_rejected_before_any_service_bus_call()
    {
        var ctx = NewCtx();
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        await QueueLifecycleHandlers.HandleAsync(
            ctx,
            QueryParsed(SqsOperation.GetQueueUrl, ("QueueName", "not a valid name!")),
            sb,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameterValue", ReadBody(ctx));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Unknown_queue_maps_to_non_existent_queue()
    {
        var ctx = NewCtx();
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        await QueueLifecycleHandlers.HandleAsync(
            ctx,
            QueryParsed(SqsOperation.GetQueueUrl, ("QueueName", "missing-queue")),
            sb,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("AWS.SimpleQueueService.NonExistentQueue", ReadBody(ctx));
    }

    [Fact]
    public async Task Emulator_feed_fallback_for_unknown_queue_maps_to_non_existent_queue()
    {
        // Regression test for issue #955: the local Service Bus Emulator
        // answers management-plane GET /{queueName} for an unknown queue
        // with 200 OK and a generic Atom "service document" <feed> (no
        // matching <entry>), instead of the 404 real Azure Service Bus
        // returns. GetQueueUrl must not treat that phantom 200 as "queue
        // exists" — doing so breaks check-then-create clients (e.g. kombu's
        // SQS transport) that skip CreateQueue once GetQueueUrl "succeeds".
        var ctx = NewCtx();
        var handler = new ScriptedHandler();
        for (var i = 0; i < 15; i++)
        {
            handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<feed xmlns=\"" + AtomNs + "\"><title type=\"text\">Publicly Listed Services</title></feed>",
                    Encoding.UTF8,
                    "application/atom+xml"),
            });
        }
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        await QueueLifecycleHandlers.HandleAsync(
            ctx,
            QueryParsed(SqsOperation.GetQueueUrl, ("QueueName", "totally-fresh-queue-xyz")),
            sb,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("AWS.SimpleQueueService.NonExistentQueue", ReadBody(ctx));
    }

    [Fact]
    public async Task Existing_queue_resolves_to_placeholder_account_url()
    {
        var ctx = NewCtx();
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/atom+xml"),
        });
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var sb = new ServiceBusClient(http, Creds);

        await QueueLifecycleHandlers.HandleAsync(
            ctx,
            QueryParsed(SqsOperation.GetQueueUrl, ("QueueName", "existing-queue")),
            sb,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains(
            "<QueueUrl>https://sqs.us-east-1.amazonaws.com/000000000000/existing-queue</QueueUrl>",
            body);
    }

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

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();

        public List<HttpRequestMessage> Calls { get; } = new();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responses.Enqueue(request => Task.FromResult(responder(request)));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            Assert.True(_responses.Count > 0, $"No scripted response left for {request.Method} {request.RequestUri}");
            return await _responses.Dequeue()(request).ConfigureAwait(false);
        }
    }
}
