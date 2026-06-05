using System.Net;
using System.Net.Http;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ServiceBusTopicsAuthenticatorTests
{
    // A token-endpoint failure during AAD auth must be converted into the module's
    // status-carrying ServiceBusTopicsManagementException so the existing
    // SnsTopicSupport mapping renders the faithful SNS error (429 -> Throttled,
    // transient -> InternalFailure, auth -> AuthorizationError) instead of a bare
    // HTTP 500. (#213)
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
        var authenticator = new ServiceBusTopicsAuthenticator(tokenProvider);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://myns.servicebus.windows.net/topic1");

        var ex = await Assert.ThrowsAsync<ServiceBusTopicsManagementException>(() =>
            authenticator.AuthenticateAsync(
                request,
                new ServiceBusTopicsCredentials
                {
                    Namespace = "myns",
                    TenantId = "tenant",
                    ClientId = "client",
                    ClientSecret = "secret",
                },
                CancellationToken.None).AsTask());

        Assert.Equal(expectedBackendStatus, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
