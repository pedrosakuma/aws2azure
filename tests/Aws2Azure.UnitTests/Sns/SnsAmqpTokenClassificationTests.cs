using System.Net;
using System.Text;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Amqp;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SnsAmqpTokenClassificationTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Throttled")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Transient")]
    [InlineData(HttpStatusCode.InternalServerError, "Transient")]
    [InlineData(HttpStatusCode.RequestTimeout, "Transient")]
    [InlineData(HttpStatusCode.Forbidden, "Auth")]
    [InlineData(HttpStatusCode.Unauthorized, "Auth")]
    [InlineData(HttpStatusCode.BadRequest, "Auth")]
    public void Sender_classifies_token_endpoint_failure_by_backend_status(
        HttpStatusCode tokenStatus,
        string expectedKind)
    {
        var token = new EntraIdTokenException(tokenStatus, responseBody: null);

        var handled = SnsAmqpSender.TryWrap(token, out var wrapped);

        Assert.True(handled);
        Assert.Equal(expectedKind, wrapped.Kind.ToString());
        Assert.Same(token, wrapped.InnerException);
    }

    [Fact]
    public async Task Sender_rejects_sends_after_dispose()
    {
        using var http = new AzureHttpClient();
        var sender = new SnsAmqpSender(
            new EntraIdTokenProvider(http),
            new AmqpConnectionSettings { ContainerId = "test-sns", Hostname = "ns.servicebus.windows.net" });

        await sender.DisposeAsync();

        var credentials = new ServiceBusTopicsCredentials
        {
            Namespace = "ns",
            SasKeyName = "RootManageSharedAccessKey",
            SasKey = "secret",
        };
        var message = new SnsAmqpSendMessage(
            Encoding.UTF8.GetBytes("payload"),
            new AmqpProperties { MessageId = "mid" },
            ApplicationProperties: null);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sender.SendAsync(credentials, "ns.servicebus.windows.net", "topic", message, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sender.SendBatchAsync(credentials, "ns.servicebus.windows.net", "topic", [message], CancellationToken.None));
    }
}
