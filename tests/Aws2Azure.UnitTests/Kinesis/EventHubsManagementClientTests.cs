using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class EventHubsManagementClientTests
{
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

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
