using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Microsoft.Extensions.Logging.Abstractions;
using Aws2Azure.TestSupport.Http;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class EventHubsManagementClientTests
{
    [Fact]
    public async Task GetEventHubAsync_uses_configured_partition_count_when_available()
    {
        using var httpClient = new AzureHttpClient(new ScriptedHandler(_ => throw new InvalidOperationException("HTTP should not be called.")), ownsHandler: false);
        var client = new EventHubsManagementClient(
            httpClient,
            new TestAuthenticator(),
            NullLogger<EventHubsManagementClient>.Instance);

        var description = await client.GetEventHubAsync(
            new EventHubsCredentials
            {
                Namespace = "myns",
                SasKeyName = "Root",
                SasKey = "secret",
                Streams = new Dictionary<string, KinesisStreamSettings>
                {
                    ["orders"] = new() { EventHubName = "orders-eh", PartitionCount = 4 },
                },
            },
            "myns.servicebus.windows.net",
            "orders-eh",
            CancellationToken.None);

        Assert.Equal(4, description.PartitionCount);
        Assert.Equal(["0", "1", "2", "3"], description.PartitionIds.ToArray());
        Assert.Equal(1, description.MessageRetentionDays);
        Assert.Equal(DateTimeOffset.UnixEpoch, description.CreatedAt);
    }

    [Fact]
    public async Task GetEventHubAsync_parses_atom_event_hub_description()
    {
        var handler = new ScriptedHandler(request =>
        {
            Assert.Equal("https://myns.servicebus.windows.net/orders?api-version=2014-01", request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("Authorization", out var authValues));
            Assert.Equal("TestAuth", Assert.Single(authValues));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleAtomPayload, Encoding.UTF8, "application/atom+xml"),
            };
        });
        using var httpClient = new AzureHttpClient(handler, ownsHandler: false);
        var client = new EventHubsManagementClient(
            httpClient,
            new TestAuthenticator(),
            NullLogger<EventHubsManagementClient>.Instance);

        var description = await client.GetEventHubAsync(
            new EventHubsCredentials { Namespace = "myns", SasKeyName = "Root", SasKey = "secret" },
            "myns.servicebus.windows.net",
            "orders",
            CancellationToken.None);

        Assert.Equal(4, description.PartitionCount);
        Assert.Equal(["0", "1", "2", "3"], description.PartitionIds.ToArray());
        Assert.Equal(7, description.MessageRetentionDays);
        Assert.Equal(new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero), description.CreatedAt);
    }

    [Fact]
    public async Task GetEventHubAsync_treats_2xx_response_without_description_as_not_found()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EmptyFeedPayload, Encoding.UTF8, "application/atom+xml"),
        });
        using var httpClient = new AzureHttpClient(handler, ownsHandler: false);
        var client = new EventHubsManagementClient(
            httpClient,
            new TestAuthenticator(),
            NullLogger<EventHubsManagementClient>.Instance);

        var exception = await Assert.ThrowsAsync<EventHubsManagementException>(() => client.GetEventHubAsync(
            new EventHubsCredentials { Namespace = "myns", SasKeyName = "Root", SasKey = "secret" },
            "myns.servicebus.windows.net",
            "missing-eh",
            CancellationToken.None).AsTask());

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task GetEventHubAsync_does_not_reclassify_a_present_but_malformed_description_as_not_found()
    {
        // A malformed field inside an EventHubDescription that IS present
        // (e.g. a non-integer PartitionCount) means the stream exists but
        // the payload is broken — a genuinely different problem than the
        // "description element missing entirely" ambiguity this fix targets.
        // It must not be silently reclassified as a 404.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MalformedPartitionCountPayload, Encoding.UTF8, "application/atom+xml"),
        });
        using var httpClient = new AzureHttpClient(handler, ownsHandler: false);
        var client = new EventHubsManagementClient(
            httpClient,
            new TestAuthenticator(),
            NullLogger<EventHubsManagementClient>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetEventHubAsync(
            new EventHubsCredentials { Namespace = "myns", SasKeyName = "Root", SasKey = "secret" },
            "myns.servicebus.windows.net",
            "orders",
            CancellationToken.None).AsTask());
    }

    private const string EmptyFeedPayload = """
<?xml version="1.0" encoding="utf-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title type="text">Event Hubs</title>
  <id>https://mynamespace.servicebus.windows.net/missing-eh</id>
  <updated>2024-06-20T08:45:00Z</updated>
</feed>
""";

    private const string MalformedPartitionCountPayload = """
<?xml version="1.0" encoding="utf-8"?>
<entry xmlns="http://www.w3.org/2005/Atom">
  <id>https://mynamespace.servicebus.windows.net/orders</id>
  <title type="text">orders</title>
  <updated>2024-06-20T08:45:00Z</updated>
  <author><name>Microsoft.ServiceBus</name></author>
  <content type="application/xml">
    <EventHubDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">
      <MessageRetentionInDays>7</MessageRetentionInDays>
      <PartitionCount>not-a-number</PartitionCount>
      <CreatedAt>2024-06-20T08:45:00Z</CreatedAt>
    </EventHubDescription>
  </content>
</entry>
""";

    private const string SampleAtomPayload = """
<?xml version="1.0" encoding="utf-8"?>
<entry xmlns="http://www.w3.org/2005/Atom">
  <id>https://mynamespace.servicebus.windows.net/orders</id>
  <title type="text">orders</title>
  <updated>2024-06-20T08:45:00Z</updated>
  <author><name>Microsoft.ServiceBus</name></author>
  <content type="application/xml">
    <EventHubDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">
      <MessageRetentionInDays>7</MessageRetentionInDays>
      <PartitionCount>4</PartitionCount>
      <PartitionIds>
        <string>0</string>
        <string>1</string>
        <string>2</string>
        <string>3</string>
      </PartitionIds>
      <CreatedAt>2024-06-20T08:45:00Z</CreatedAt>
    </EventHubDescription>
  </content>
</entry>
""";

    private sealed class TestAuthenticator : IEventHubsAuthenticator
    {
        public ValueTask AuthenticateAsync(HttpRequestMessage request, EventHubsCredentials credentials, CancellationToken cancellationToken = default)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "TestAuth");
            return ValueTask.CompletedTask;
        }
    }

}
