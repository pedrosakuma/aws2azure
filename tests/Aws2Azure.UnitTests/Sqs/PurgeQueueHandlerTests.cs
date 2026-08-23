using System.Net;
using System.Net.Http.Headers;
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
/// Covers the two cooperating cool-down layers behind <c>PurgeQueue</c>:
/// the bounded in-process tracker (same-replica fast reject, no network
/// call) and the Service-Bus-persisted, ETag compare-and-swap cool-down
/// deadline (cross-replica source of truth — see
/// docs/gaps/sqs/PurgeQueue.yaml).
/// </summary>
public sealed class PurgeQueueHandlerTests
{
    private const string AtomNs = AtomQueueXmlReader.AtomNs;
    private const string SbNs = AtomQueueXmlReader.SbNs;

    private static readonly ServiceBusCredentials Credentials = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
    };

    [Fact]
    public async Task Empty_queue_succeeds_and_immediate_repeat_hits_local_cooldown_without_upstream_call()
    {
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", userMetadata: null));                 // GET (distributed CAS read)
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));        // PUT (cool-down persisted)
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent)); // peek-lock: queue empty
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);

        var first = NewContext();
        await BatchAdminHandlers.HandleAsync(
            first, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(3, handler.CallCount);

        var second = NewContext();
        await BatchAdminHandlers.HandleAsync(
            second, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, second.Response.StatusCode);
        Assert.Contains("PurgeQueueInProgress", ReadBody(second));
        // The local in-process tracker rejects the immediate repeat before
        // any Service Bus call is made.
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Nonexistent_queue_failure_releases_cooldown_reservation()
    {
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound)); // GET: queue missing
        handler.Enqueue(_ => Atom200("q1", userMetadata: null));                // retry: GET
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));       // retry: PUT
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent)); // retry: peek-lock empty
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);

        var missing = NewContext();
        await BatchAdminHandlers.HandleAsync(
            missing, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, missing.Response.StatusCode);
        Assert.Contains("NonExistentQueue", ReadBody(missing));

        var retry = NewContext();
        await BatchAdminHandlers.HandleAsync(
            retry, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, retry.Response.StatusCode);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task Distributed_cooldown_rejects_a_second_replica_even_after_the_local_tracker_is_reset()
    {
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        var handler = new ScriptedHandler();
        string? persistedUserMetadata = null;
        handler.Enqueue(_ => Atom200("q1", userMetadata: null));
        handler.Enqueue(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
            persistedUserMetadata = ReadElementValue(body, "UserMetadata");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);

        var first = NewContext();
        await BatchAdminHandlers.HandleAsync(
            first, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(persistedUserMetadata));

        // Simulate a request landing on a different replica (or this
        // replica having restarted): the in-process tracker no longer has
        // any record of the purge, but the Service-Bus-persisted deadline
        // is still active and must still reject it.
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        handler.Enqueue(_ => Atom200("q1", userMetadata: persistedUserMetadata));

        var second = NewContext();
        await BatchAdminHandlers.HandleAsync(
            second, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, second.Response.StatusCode);
        Assert.Contains("PurgeQueueInProgress", ReadBody(second));
    }

    [Fact]
    public async Task Foreign_user_metadata_degrades_to_local_only_coordination_and_still_purges()
    {
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Atom200("q1", userMetadata: "plain operator metadata")); // GET: foreign metadata
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));      // peek-lock: empty
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);

        var first = NewContext();
        await BatchAdminHandlers.HandleAsync(
            first, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        // No PUT is attempted when UserMetadata is foreign — degrade to the
        // local in-process tracker instead of risking clobbering
        // operator-owned content.
        Assert.Equal(2, handler.CallCount);

        var second = NewContext();
        await BatchAdminHandlers.HandleAsync(
            second, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, second.Response.StatusCode);
        Assert.Contains("PurgeQueueInProgress", ReadBody(second));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Distributed_cooldown_retries_precondition_failures_and_still_persists()
    {
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        var handler = new ScriptedHandler();
        var putAttempts = 0;
        handler.Enqueue(_ => Atom200("q1", userMetadata: null, eTag: "\"etag-1\""));
        handler.Enqueue(req =>
        {
            putAttempts++;
            Assert.Equal("\"etag-1\"", Assert.Single(req.Headers.IfMatch).Tag);
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        });
        handler.Enqueue(_ => Atom200("q1", userMetadata: null, eTag: "\"etag-2\""));
        handler.Enqueue(req =>
        {
            putAttempts++;
            Assert.Equal("\"etag-2\"", Assert.Single(req.Headers.IfMatch).Tag);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);

        var ctx = NewContext();
        await BatchAdminHandlers.HandleAsync(
            ctx, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(2, putAttempts);
    }

    private static SqsParseResult PurgeRequest() => new(
        SqsWireProtocol.Query,
        SqsOperation.PurgeQueue,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QueueUrl"] = "https://sqs.us-east-1.amazonaws.com/000000000000/q1",
        },
        JsonBody: null,
        Error: null);

    private static HttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("sqs.us-east-1.amazonaws.com");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private static HttpResponseMessage Atom200(
        string name,
        string? userMetadata,
        string eTag = "\"etag-q1\"")
    {
        var qd = "<QueueDescription xmlns=\"" + SbNs + "\">" +
                 "<LockDuration>PT30S</LockDuration>" +
                 (userMetadata is null ? string.Empty :
                    "<UserMetadata>" + System.Net.WebUtility.HtmlEncode(userMetadata) + "</UserMetadata>") +
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

    private static string ReadElementValue(string xml, string name)
    {
        var startTag = "<" + name + ">";
        var endTag = "</" + name + ">";
        var start = xml.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += startTag.Length;
        var end = xml.IndexOf(endTag, start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : System.Net.WebUtility.HtmlDecode(xml[start..end]);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();

        public int CallCount { get; private set; }

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> builder) =>
            _responses.Enqueue(request => Task.FromResult(builder(request)));

        public void Enqueue(Func<HttpRequestMessage, Task<HttpResponseMessage>> builder) => _responses.Enqueue(builder);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
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
