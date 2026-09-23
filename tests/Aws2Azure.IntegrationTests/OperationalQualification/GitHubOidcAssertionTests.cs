using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class GitHubOidcAssertionTests
{
    private const string RequestUrl = "https://oidc.invalid/assertion?private=never-log-url";
    private const string RequestToken = "never-log-request-token";
    private const string Assertion = "never-log-assertion";

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Transient_response_retries_only_assertion_and_keeps_diagnostics_sanitized(int status)
    {
        var requests = new List<HttpRequestMessage>();
        using var diagnostics = new StringWriter();
        using var handler = new Handler((attempt, request, _) =>
        {
            requests.Add(request);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(RequestToken, request.Headers.Authorization?.Parameter);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(RequestUrl + "&audience=api%3A%2F%2FAzureADTokenExchange", request.RequestUri!.OriginalString);
            return Task.FromResult(attempt == 1 ? Failure(status, TimeSpan.Zero) : Success());
        });
        using var client = new HttpClient(handler);

        var assertion = await AcquireAsync(client, diagnostics, new Clock());

        Assert.Equal(Assertion, assertion);
        Assert.Equal(2, handler.Attempts);
        Assert.NotSame(requests[0], requests[1]);
        Assert.Contains($"attempt 1/3 returned HTTP {status}", diagnostics.ToString());
        Assert.Contains("acquired on attempt 2/3", diagnostics.ToString());
        Assert.DoesNotContain("never-log", diagnostics.ToString());
        Assert.DoesNotContain("oidc.invalid", diagnostics.ToString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    public async Task Terminal_status_is_not_retried(int status)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Failure(status, TimeSpan.Zero)));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => AcquireAsync(client, diagnostics, new Clock()));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, handler.Attempts);
        Assert.DoesNotContain("never-log", error.ToString() + diagnostics);
    }

    [Fact]
    public async Task Exhaustion_stops_after_three_attempts_and_preserves_status()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Failure(503, TimeSpan.Zero)));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => AcquireAsync(client, diagnostics, new Clock()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Contains("after 3 attempt(s)", error.Message);
        Assert.Equal(3, handler.Attempts);
        Assert.DoesNotContain("acquired", diagnostics.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":12}")]
    [InlineData("{\"value\":\" \"}")]
    public async Task Invalid_success_is_not_retried(string body)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(client, diagnostics, new Clock()));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Malformed_json_and_transport_errors_are_not_retried()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") }));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();
        await Assert.ThrowsAnyAsync<JsonException>(() => AcquireAsync(client, diagnostics, new Clock()));
        Assert.Equal(1, handler.Attempts);

        using var transport = new Handler((_, _, _) => throw new HttpRequestException("transport failure"));
        using var transportClient = new HttpClient(transport);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => AcquireAsync(transportClient, diagnostics, new Clock()));
        Assert.Equal(1, transport.Attempts);
    }

    [Fact]
    public async Task Default_backoff_is_one_then_two_seconds_with_one_deadline()
    {
        var clock = new Clock();
        using var handler = new Handler((attempt, _, _) => Task.FromResult(
            attempt < 3 ? Failure(503) : Success()));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();

        var pending = AcquireAsync(client, diagnostics, clock);
        await clock.ExpectTimerAsync(TimeSpan.FromSeconds(30));
        await clock.ExpectTimerAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, handler.Attempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        await clock.ExpectTimerAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, handler.Attempts);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(Assertion, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, clock.GetTimestamp());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_after_delta_or_date_takes_precedence(bool useDate)
    {
        var clock = new Clock();
        using var handler = new Handler((attempt, _, _) =>
        {
            var response = attempt == 1 ? Failure(429) : Success();
            if (attempt == 1)
                response.Headers.RetryAfter = useDate
                    ? new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(4))
                    : new RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();

        var pending = AcquireAsync(client, diagnostics, clock);
        await clock.ExpectTimerAsync(TimeSpan.FromSeconds(30));
        await clock.ExpectTimerAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(1, handler.Attempts);
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(Assertion, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(0, 60)]
    [InlineData(29, 1)]
    public async Task Retry_after_cannot_extend_or_restart_the_total_budget(int requestSeconds, int delaySeconds)
    {
        var clock = new Clock();
        using var handler = new Handler((_, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(requestSeconds));
            return Task.FromResult(Failure(503, TimeSpan.FromSeconds(delaySeconds)));
        });
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => AcquireAsync(client, diagnostics, clock));

        Assert.Contains("remaining 30-second", error.Message);
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_or_total_deadline_interrupts_request(bool expireBudget)
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success();
        });
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();
        var pending = AcquireAsync(client, diagnostics, clock, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (expireBudget)
            clock.Advance(TimeSpan.FromSeconds(30));
        else
            cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_backoff_without_another_request()
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, _, _) => Task.FromResult(Failure(503)));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();
        var pending = AcquireAsync(client, diagnostics, clock, cancellation.Token);
        await clock.ExpectTimerAsync(TimeSpan.FromSeconds(30));
        await clock.ExpectTimerAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Deadline_also_cancels_successful_response_body_read()
    {
        var clock = new Clock();
        using var content = new WaitingContent();
        using var handler = new Handler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();
        var pending = AcquireAsync(client, diagnostics, clock);
        await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Attempts);
        Assert.DoesNotContain("acquired", diagnostics.ToString());
    }

    [Fact]
    public async Task Cancelled_caller_does_not_start_a_request()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new Handler((_, _, _) => Task.FromResult(Success()));
        using var client = new HttpClient(handler);
        using var diagnostics = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AcquireAsync(client, diagnostics, new Clock(), cancellation.Token));

        Assert.Equal(0, handler.Attempts);
    }

    private static Task<string> AcquireAsync(HttpClient client, TextWriter diagnostics, Clock clock,
        CancellationToken token = default) =>
        GitHubOidcAssertion.AcquireAsync(client, RequestUrl, RequestToken, diagnostics, clock, token);

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"value\":\"{Assertion}\"}}"),
    };

    private static HttpResponseMessage Failure(int status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("never-log-error-body"),
        };
        if (retryAfter is { } delay)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    private sealed class Handler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(Interlocked.Increment(ref _attempts), request, token);
    }

    private sealed class WaitingContent : HttpContent
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The response body read must pass its cancellation token.");
        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public async Task ExpectTimerAsync(TimeSpan expected) =>
            Assert.Equal(expected, await _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
            Timer[] snapshot;
            lock (_timers) snapshot = _timers.ToArray();
            foreach (var timer in snapshot)
                timer.FireIfDue(GetTimestamp());
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new Timer(this, callback, state, dueTime);
            lock (_timers) _timers.Add(timer);
            Assert.True(_created.Writer.TryWrite(dueTime));
            return timer;
        }

        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private long _due = clock.GetTimestamp() + due.Ticks;
            public void FireIfDue(long now)
            {
                var expected = Interlocked.Read(ref _due);
                if (expected <= now && Interlocked.CompareExchange(ref _due, long.MaxValue, expected) == expected)
                    callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _due, long.MaxValue);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
