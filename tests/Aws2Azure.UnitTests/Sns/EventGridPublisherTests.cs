using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Sns;

public sealed class EventGridPublisherTests
{
    [Fact]
    public async Task PublishAsync_serializes_single_event_and_uses_sas_header()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var publisher = NewPublisher(handler, clock: clock);
        var messageId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        await publisher.PublishAsync(
            new EventGridPublishDestination(
                "https://orders.eastus-1.eventgrid.azure.net/api/events",
                "sas-key",
                null,
                null,
                null),
            NewMessage(messageId, subject: "hello-subject"),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://orders.eastus-1.eventgrid.azure.net/api/events?api-version=2018-01-01", request.RequestUri!.ToString());
        Assert.True(request.Headers.TryGetValues("aeg-sas-key", out var sasHeader));
        Assert.Equal("sas-key", Assert.Single(sasHeader));

        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetArrayLength());
        var envelope = document.RootElement[0];
        Assert.Equal(messageId.ToString(), envelope.GetProperty("id").GetString());
        Assert.Equal("aws.sns.Message", envelope.GetProperty("eventType").GetString());
        Assert.Equal("arn:aws:sns:us-west-2:000000000000:orders", envelope.GetProperty("subject").GetString());
        Assert.Equal("2025-01-01T00:00:00+00:00", envelope.GetProperty("eventTime").GetString());
        Assert.Equal("1.0", envelope.GetProperty("dataVersion").GetString());

        var data = envelope.GetProperty("data");
        Assert.Equal("hello-subject", data.GetProperty("Subject").GetString());
        Assert.Equal("hello world", data.GetProperty("Message").GetString());
        Assert.Equal("json", data.GetProperty("MessageStructure").GetString());
        Assert.Equal("arn:aws:sns:us-west-2:000000000000:orders", data.GetProperty("TopicArn").GetString());
        var attributes = data.GetProperty("MessageAttributes");
        Assert.Equal("String", attributes.GetProperty("color").GetProperty("Type").GetString());
        Assert.Equal("blue", attributes.GetProperty("color").GetProperty("Value").GetString());
    }

    [Fact]
    public async Task PublishBatchAsync_serializes_batch_in_single_post()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var publisher = NewPublisher(handler);

        var result = await publisher.PublishBatchAsync(
            new EventGridPublishDestination(
                "https://orders.eastus-1.eventgrid.azure.net/api/events",
                "sas-key",
                null,
                null,
                null),
            [
                NewMessage(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                NewMessage(Guid.Parse("22222222-2222-2222-2222-222222222222"), message: "two")
            ],
            CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, outcome => Assert.True(outcome.Succeeded));
        var request = Assert.Single(handler.Requests);
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task PublishAsync_requests_aad_token_and_sends_bearer_header()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host == "login.test")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"aad-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var publisher = NewPublisher(handler, authority: new Uri("https://login.test/"));

        await publisher.PublishAsync(
            new EventGridPublishDestination(
                "https://orders.eastus-1.eventgrid.azure.net/api/events",
                null,
                "tenant",
                "client",
                "secret"),
            NewMessage(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var publishRequest = handler.Requests[1];
        Assert.Equal("Bearer", publishRequest.Headers.Authorization!.Scheme);
        Assert.Equal("aad-token", publishRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task PublishBatchAsync_marks_all_entries_failed_on_http_failure()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
        });
        var publisher = NewPublisher(handler);

        var result = await publisher.PublishBatchAsync(
            new EventGridPublishDestination(
                "https://orders.eastus-1.eventgrid.azure.net/api/events",
                "sas-key",
                null,
                null,
                null),
            [NewMessage(Guid.NewGuid()), NewMessage(Guid.NewGuid(), message: "two")],
            CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, outcome =>
        {
            Assert.False(outcome.Succeeded);
            Assert.Equal("InternalFailure", outcome.ErrorCode);
            Assert.Contains("HTTP 500", outcome.ErrorMessage);
        });
    }

    [Fact]
    public async Task PublishBatchAsync_splits_batches_when_limits_are_exceeded()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var publisher = NewPublisher(handler, options: new EventGridPublisherOptions
        {
            MaxEventsPerRequest = 2,
            MaxBytesPerEvent = 1024 * 1024,
            MaxBytesPerRequest = 1024 * 1024,
        });

        var result = await publisher.PublishBatchAsync(
            new EventGridPublishDestination(
                "https://orders.eastus-1.eventgrid.azure.net/api/events",
                "sas-key",
                null,
                null,
                null),
            [
                NewMessage(Guid.Parse("11111111-1111-1111-1111-111111111111"), message: "one"),
                NewMessage(Guid.Parse("22222222-2222-2222-2222-222222222222"), message: "two"),
                NewMessage(Guid.Parse("33333333-3333-3333-3333-333333333333"), message: "three")
            ],
            CancellationToken.None);

        Assert.All(result.Outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal(2, handler.Requests.Count);
        using var first = JsonDocument.Parse(await handler.Requests[0].Content!.ReadAsStringAsync());
        using var second = JsonDocument.Parse(await handler.Requests[1].Content!.ReadAsStringAsync());
        Assert.Equal(2, first.RootElement.GetArrayLength());
        Assert.Equal(1, second.RootElement.GetArrayLength());
    }

    private static EventGridPublisher NewPublisher(
        HttpMessageHandler handler,
        EventGridPublisherOptions? options = null,
        Uri? authority = null,
        TimeProvider? clock = null)
    {
        var http = new AzureHttpClient(handler, ownsHandler: false, new AzureHttpClientOptions
        {
            MaxAttempts = 1,
            CircuitBreaker = new CircuitBreakerOptions { Enabled = false },
        });
        var tokenProvider = new EntraIdTokenProvider(http, authority: authority, clock: clock);
        return new EventGridPublisher(http, tokenProvider, NullLogger<EventGridPublisher>.Instance, options, clock);
    }

    private static EventGridPublishMessage NewMessage(Guid messageId, string message = "hello world", string? subject = null)
        => new(
            messageId,
            "arn:aws:sns:us-west-2:000000000000:orders",
            message,
            subject,
            "json",
            null,
            null,
            [new SnsMessageAttribute("color", "String", "blue", null)]);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(responder(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var bytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                clone.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
