using System.Net;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public sealed class PurgeQueueHandlerTests
{
    private static readonly ServiceBusCredentials Credentials = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
    };

    [Fact]
    public async Task Empty_queue_succeeds_and_immediate_repeat_hits_cooldown_without_upstream_call()
    {
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var serviceBus = new ServiceBusClient(http, Credentials);

        var first = NewContext();
        await BatchAdminHandlers.HandleAsync(
            first, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(1, handler.CallCount);

        var second = NewContext();
        await BatchAdminHandlers.HandleAsync(
            second, PurgeRequest(), serviceBus, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, second.Response.StatusCode);
        Assert.Contains("PurgeQueueInProgress", ReadBody(second));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Nonexistent_queue_failure_releases_cooldown_reservation()
    {
        BatchAdminHandlers.ResetPurgeCoolDownForTesting();
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));
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
        Assert.Equal(2, handler.CallCount);
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

    private sealed class ScriptedHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses =
            new(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
