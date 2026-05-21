using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Phase 2.6 idempotency-keys slice: SQS SendMessage / SendMessageBatch must
/// mint a stable SB MessageId per logical send so AzureHttpClient retries
/// don't double-publish on dedup-enabled queues. Asserts that the
/// BrokerProperties header carries a MessageId, that the same value is
/// echoed in the SQS response, and that retries see the identical header.
/// </summary>
public sealed class SqsIdempotencyKeyTests
{
    private static readonly ServiceBusCredentials Creds = new()
    {
        Namespace = "fake-ns",
        SasKeyName = "RootManageSharedAccessKey",
        SasKey = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
    };

    [Fact]
    public async Task SendMessage_on_standard_queue_carries_brokerproperties_messageid_and_echoes_it()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/plain"),
            ("MessageBody", "hello"));

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.Single(handler.Calls);
        var bp = ExtractBrokerProperties(handler.Calls[0]);
        Assert.True(bp.RootElement.TryGetProperty("MessageId", out var midElem),
            "BrokerProperties must carry a MessageId for retry idempotency.");
        var wireMessageId = midElem.GetString();
        Assert.False(string.IsNullOrEmpty(wireMessageId));

        // Same id is echoed back in the SQS response so clients log a
        // stable identifier even when the broker dedupes a retry-induced
        // duplicate behind the scenes.
        var bodyStr = ReadBody(ctx);
        Assert.Contains(wireMessageId!, bodyStr);
    }

    [Fact]
    public async Task SendMessage_messageid_is_stable_across_azurehttpclient_retries()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/plain"),
            ("MessageBody", "hello"));

        var first503 = true;
        var handler = new CapturingHandler(_ =>
        {
            if (first503)
            {
                first503 = false;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        // Must have retried at least once.
        Assert.True(handler.Calls.Count >= 2,
            $"expected ≥ 2 attempts (one 503 + one 201), got {handler.Calls.Count}");

        var first = ExtractBrokerProperties(handler.Calls[0]).RootElement.GetProperty("MessageId").GetString();
        var second = ExtractBrokerProperties(handler.Calls[1]).RootElement.GetProperty("MessageId").GetString();
        Assert.Equal(first, second); // retries must NOT mint a new MessageId.
    }

    [Fact]
    public async Task SendMessage_on_fifo_queue_with_dedup_id_uses_dedup_id_as_messageid()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/orders.fifo"),
            ("MessageBody", "hello"),
            ("MessageGroupId", "g1"),
            ("MessageDeduplicationId", "dedup-42"));

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        var bp = ExtractBrokerProperties(handler.Calls[0]);
        Assert.Equal("dedup-42", bp.RootElement.GetProperty("MessageId").GetString());
    }

    [Fact]
    public async Task SendMessageBatch_each_entry_carries_unique_stable_messageid()
    {
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/plain"),
            ("SendMessageBatchRequestEntry.1.Id", "a"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "body-a"),
            ("SendMessageBatchRequestEntry.2.Id", "b"),
            ("SendMessageBatchRequestEntry.2.MessageBody", "body-b"));

        var first503 = true;
        var handler = new CapturingHandler(_ =>
        {
            if (first503)
            {
                first503 = false;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var http = NewHttpClient(handler);
        var sb = new ServiceBusClient(http, Creds);
        await SendMessageHandlers.HandleAsync(ctx, parsed, sb, CancellationToken.None);

        Assert.True(handler.Calls.Count >= 2);

        // The batch envelope is the request body itself (POST JSON array).
        var firstBody = handler.Calls[0].BodyText;
        var secondBody = handler.Calls[1].BodyText;
        Assert.Equal(firstBody, secondBody); // identical body across retries (incl. MessageIds).

        // Pull the two MessageIds out of the JSON array — they must be
        // distinct (per-entry) but stable across the retry above.
        using var doc = JsonDocument.Parse(firstBody);
        var arr = doc.RootElement;
        Assert.Equal(2, arr.GetArrayLength());
        var id0 = arr[0].GetProperty("BrokerProperties").GetProperty("MessageId").GetString();
        var id1 = arr[1].GetProperty("BrokerProperties").GetProperty("MessageId").GetString();
        Assert.False(string.IsNullOrEmpty(id0));
        Assert.False(string.IsNullOrEmpty(id1));
        Assert.NotEqual(id0, id1);
    }

    // --- helpers ---------------------------------------------------------

    private static AzureHttpClient NewHttpClient(HttpMessageHandler handler) =>
        new(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2),
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
        });

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

    private static JsonDocument ExtractBrokerProperties(CapturedRequest captured)
    {
        Assert.True(captured.BrokerProperties is not null,
            "BrokerProperties header must be set on the SB request.");
        return JsonDocument.Parse(captured.BrokerProperties!);
    }

    private sealed record CapturedRequest(string? BrokerProperties, string BodyText);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<CapturedRequest> Calls { get; } = new();
        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) { _responder = responder; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? bp = null;
            if (request.Headers.TryGetValues("BrokerProperties", out var values))
            {
                foreach (var v in values) { bp = v; break; }
            }
            var body = request.Content is null ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Calls.Add(new CapturedRequest(bp, body));
            return _responder(request);
        }
    }
}
