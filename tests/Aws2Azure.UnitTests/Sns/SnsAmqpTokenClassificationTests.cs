using System.Net;
using Aws2Azure.Core.Azure;
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
}
