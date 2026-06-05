using System.Net;
using System.Net.Http;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class EventHubsAuthenticatorTests
{
    // A token-endpoint failure during AAD auth must be converted into the module's
    // status-carrying EventHubsManagementException so the existing
    // KinesisMetadataSupport mapping renders the faithful Kinesis error
    // (429 -> LimitExceededException, transient -> InternalFailure, auth ->
    // AccessDeniedException) instead of a bare HTTP 500. (#213)
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)]
    public async Task AuthenticateAsync_maps_token_endpoint_failure_to_management_exception(
        HttpStatusCode tokenStatus, HttpStatusCode expectedBackendStatus)
    {
        using var tokenHttp = new AzureHttpClient(
            new ScriptedHandler(_ => new HttpResponseMessage(tokenStatus)), ownsHandler: true);
        var tokenProvider = new EntraIdTokenProvider(tokenHttp, authority: new Uri("https://login.test/"));
        var authenticator = new EventHubsAuthenticator(tokenProvider);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://myns.servicebus.windows.net/orders");

        var ex = await Assert.ThrowsAsync<EventHubsManagementException>(() =>
            authenticator.AuthenticateAsync(
                request,
                new EventHubsCredentials
                {
                    Namespace = "myns",
                    TenantId = "tenant",
                    ClientId = "client",
                    ClientSecret = "secret",
                },
                CancellationToken.None).AsTask());

        Assert.Equal(expectedBackendStatus, ex.StatusCode);
        // The token-endpoint body must never be carried toward the client.
        Assert.Null(ex.ResponseBody);
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
