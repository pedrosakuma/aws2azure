using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.PerfTests.DynamoDb;

public sealed class BatchWriteExperimentRelayTests
{
    private static readonly Uri Backend = new("http://backend.invalid/");

    [Theory]
    [InlineData(201, "201")]
    [InlineData(429, "429")]
    [InlineData(503, "503")]
    [InlineData(599, "other")]
    public async Task Completed_statuses_preserve_legacy_semantics_and_dispose_content(int status, string bucket)
    {
        var content = new FaultContent();
        var handler = new ControlledHandler((_, _) => Task.FromResult(Response(status, content)));
        await using var relay = new BatchWriteExperimentRelay(handler);
        relay.Begin();
        await relay.ForwardAsync(Context(), Backend);
        using var report = Report(relay);
        var writes = Writes(report);
        foreach (var name in new[] { "arrived", "dispatchStarted", "responseHeadersReceived", "bodyCompleted", "responseWriteCompleted" })
            Assert.Equal(1, writes.GetProperty(name).GetInt32());
        Assert.Equal(1, writes.GetProperty("statusCounts").GetProperty(bucket).GetInt32());
        Assert.Equal(0, writes.GetProperty("failed").GetInt32());
        Assert.Equal(0, writes.GetProperty("activeAtSnapshot").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("writeRestAttempts").GetInt32());
        Assert.Equal(status >= 400 ? 1 : 0, report.RootElement.GetProperty("nonSuccessResponses").GetInt32());
        Assert.True(content.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.Request!.Content!.ReadAsByteArrayAsync());
        AssertSafe(report);
    }

    [Fact]
    public async Task Failure_before_headers_is_not_a_completed_response()
    {
        var handler = new ControlledHandler((_, _) => throw new HttpRequestException("secret-error-message"));
        await using var relay = new BatchWriteExperimentRelay(handler);
        relay.Begin();
        await Assert.ThrowsAsync<HttpRequestException>(() => relay.ForwardAsync(Context(), Backend));
        using var report = Report(relay);
        var writes = Writes(report);
        Assert.Equal(1, writes.GetProperty("dispatchStarted").GetInt32());
        Assert.Equal(0, writes.GetProperty("responseHeadersReceived").GetInt32());
        Assert.Equal(1, writes.GetProperty("failed").GetInt32());
        Assert.Equal(1, writes.GetProperty("faultsByPhaseAndType").GetProperty("dispatch:HttpRequestException").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("writeRestAttempts").GetInt32());
        Assert.Equal(0, writes.GetProperty("activeAtSnapshot").GetInt32());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.Request!.Content!.ReadAsByteArrayAsync());
        AssertSafe(report);
    }

    [Fact]
    public async Task Interrupted_body_retains_headers_without_inventing_completed_response()
    {
        var content = new FaultContent(interrupt: true);
        await using var relay = new BatchWriteExperimentRelay(new ControlledHandler((_, _) => Task.FromResult(Response(200, content))));
        relay.Begin();
        await Assert.ThrowsAnyAsync<Exception>(() => relay.ForwardAsync(Context(), Backend));
        using var report = Report(relay);
        var writes = Writes(report);
        Assert.Equal(1, writes.GetProperty("responseHeadersReceived").GetInt32());
        Assert.Equal(0, writes.GetProperty("bodyCompleted").GetInt32());
        Assert.Equal(1, writes.GetProperty("failed").GetInt32());
        Assert.Contains(writes.GetProperty("faultsByPhaseAndType").EnumerateObject(),
            x => x.Name is "response-body:HttpRequestException" or "response-body:IOException");
        Assert.Equal(0, report.RootElement.GetProperty("writeRestAttempts").GetInt32());
        Assert.True(content.Disposed);
        AssertSafe(report);
    }

    [Fact]
    public async Task Http_timeout_is_distinct_from_client_abort()
    {
        await using var relay = new BatchWriteExperimentRelay(new ControlledHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }), timeout: TimeSpan.FromMilliseconds(100));
        relay.Begin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.ForwardAsync(Context(), Backend).WaitAsync(TimeSpan.FromSeconds(10)));
        using var report = Report(relay);
        var writes = Writes(report);
        Assert.Equal(1, writes.GetProperty("timeouts").GetInt32());
        Assert.Equal(1, writes.GetProperty("cancelled").GetInt32());
        Assert.Equal(0, writes.GetProperty("clientDisconnects").GetInt32());
        Assert.Equal(0, writes.GetProperty("activeAtSnapshot").GetInt32());
    }

    [Fact]
    public async Task Caller_abort_before_headers_is_cancelled_not_backend_status()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var relay = new BatchWriteExperimentRelay(new ControlledHandler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }));
        using var cancellation = new CancellationTokenSource();
        var context = Context();
        context.RequestAborted = cancellation.Token;
        relay.Begin();
        var forwarding = relay.ForwardAsync(context, Backend);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forwarding);
        using var report = Report(relay);
        var writes = Writes(report);
        Assert.Equal(1, writes.GetProperty("clientDisconnects").GetInt32());
        Assert.Equal(1, writes.GetProperty("cancelled").GetInt32());
        Assert.Equal(0, writes.GetProperty("timeouts").GetInt32());
        Assert.Equal(0, writes.GetProperty("responseHeadersReceived").GetInt32());
        Assert.Equal(0, writes.GetProperty("activeAtSnapshot").GetInt32());
    }

    [Fact]
    public async Task Disconnect_writing_to_client_does_not_erase_completed_backend_body()
    {
        using var cancellation = new CancellationTokenSource();
        var context = Context();
        context.RequestAborted = cancellation.Token;
        using var output = new DisconnectingStream(cancellation);
        context.Response.Body = output;
        var content = new FaultContent();
        await using var relay = new BatchWriteExperimentRelay(new ControlledHandler((_, _) => Task.FromResult(Response(200, content))));
        relay.Begin();
        await Assert.ThrowsAsync<IOException>(() => relay.ForwardAsync(context, Backend));
        using var report = Report(relay);
        var writes = Writes(report);
        Assert.Equal(1, writes.GetProperty("bodyCompleted").GetInt32());
        Assert.Equal(0, writes.GetProperty("responseWriteCompleted").GetInt32());
        Assert.Equal(1, writes.GetProperty("clientDisconnects").GetInt32());
        Assert.Equal(1, writes.GetProperty("faultsByPhaseAndType").GetProperty("response-write:IOException").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("writeRestAttempts").GetInt32());
        Assert.True(content.Disposed);
        AssertSafe(report);
    }

    [Fact]
    public async Task Empty_arrivals_and_bounded_overflow_are_explicit_and_do_not_prevent_forwarding()
    {
        var calls = 0;
        await using var relay = new BatchWriteExperimentRelay(new ControlledHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(200, new FaultContent()));
        }), observationLimit: 2);
        relay.Begin();
        using (var empty = Report(relay)) Assert.Equal(0, Writes(empty).GetProperty("arrived").GetInt32());
        Assert.Throws<InvalidOperationException>(relay.Begin);
        // End closes admission; existing in-flight observations still finish.
        await using var bounded = new BatchWriteExperimentRelay(new ControlledHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(200, new FaultContent()));
        }), observationLimit: 2);
        bounded.Begin();
        for (var i = 0; i < 3; i++) await bounded.ForwardAsync(Context(), Backend);
        using var report = Report(bounded);
        Assert.Equal(3, calls);
        Assert.Equal(2, Writes(report).GetProperty("arrived").GetInt32());
        Assert.Equal(1, Writes(report).GetProperty("repeatedDispatches").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("boundaries").GetProperty("droppedArrivals").GetInt32());
        Assert.True(await bounded.WaitForIdleAsync());
    }

    [Fact]
    public async Task Arrival_survives_failure_reading_request_before_dispatch()
    {
        await using var relay = new BatchWriteExperimentRelay(new ControlledHandler((_, _) =>
            throw new InvalidOperationException("Backend must not be invoked.")));
        var context = Context();
        context.Request.Body = new UnreadableStream();
        relay.Begin();
        await Assert.ThrowsAsync<IOException>(() => relay.ForwardAsync(context, Backend));
        using var report = Report(relay);
        var writes = Writes(report);
        Assert.Equal(1, writes.GetProperty("arrived").GetInt32());
        Assert.Equal(0, writes.GetProperty("dispatchStarted").GetInt32());
        Assert.Equal(1, writes.GetProperty("faultsByPhaseAndType").GetProperty("request-read:IOException").GetInt32());
        Assert.Equal("Other", BatchWriteExperimentRelay.SafeExceptionType(new InvalidOperationException("secret-error")));
        AssertSafe(report);
    }

    [Fact]
    public async Task Inflight_snapshot_is_explicit_and_admitted_attempt_finishes_after_end()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var relay = new BatchWriteExperimentRelay(new ControlledHandler((_, _) => response.Task));
        relay.Begin();
        var forwarding = relay.ForwardAsync(Context(), Backend);
        using (var pending = Report(relay))
            Assert.Equal(1, Writes(pending).GetProperty("activeAtSnapshot").GetInt32());
        response.SetResult(Response(200, new FaultContent()));
        await forwarding;
        using var complete = Report(relay);
        Assert.Equal(0, Writes(complete).GetProperty("activeAtSnapshot").GetInt32());
        Assert.Equal(1, Writes(complete).GetProperty("bodyCompleted").GetInt32());
    }

    [Fact]
    public async Task Failure_snapshots_keep_available_process_data_when_endpoint_is_unavailable()
    {
        var snapshot = await BatchWriteFinalSnapshots.CaptureAsync(Environment.ProcessId, "http://127.0.0.1:0");
        Assert.NotNull(snapshot.Proxy);
        Assert.NotNull(snapshot.Driver);
        Assert.Null(snapshot.Stages);
        Assert.Null(snapshot.Memory);
        Assert.Contains("stages:missing", snapshot.Unavailable);
        Assert.Contains("memory:missing", snapshot.Unavailable);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/dbs/secret-table/colls/items/docs";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"id":"secret-key","payload":"secret-body"}"""));
        context.Request.Headers.Authorization = "secret-authorization";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static HttpResponseMessage Response(int status, HttpContent content)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        response.Headers.Add("x-ms-request-charge", "1");
        return response;
    }

    private static JsonDocument Report(BatchWriteExperimentRelay relay) => JsonDocument.Parse(JsonSerializer.Serialize(relay.End()));
    private static JsonElement Writes(JsonDocument report) => report.RootElement.GetProperty("boundaries").GetProperty("writes");
    private static void AssertSafe(JsonDocument report) => Assert.DoesNotContain("secret-", report.RootElement.GetRawText());

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return send(request, ct);
        }
    }

    private sealed class FaultContent(bool interrupt = false) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = interrupt ? 8 : 4; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("part"u8.ToArray());
            if (interrupt) throw new IOException("secret-body-error");
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class DisconnectingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            cancellation.Cancel();
            throw new IOException("secret-client-error");
        }

    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            throw new IOException("secret-request-read");
    }
}
