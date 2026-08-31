using System.Net;
using System.Net.Http;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ServiceBusTopicsAuthenticatorTests
{
    [Fact]
    public void GenerateSharedAccessSignature_scopes_default_rule_sas_to_namespace_root()
    {
        var expiry = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var resourceUri = new Uri("https://myns.servicebus.windows.net/orders/subscriptions/sub123/rules/%24Default?api-version=2021-05");

        var signature = ServiceBusTopicsAuthenticator.GenerateSharedAccessSignature(
            resourceUri,
            "RootManageSharedAccessKey",
            "secret",
            expiry,
            ServiceBusTopicsAuthenticator.UseNamespaceScopedSasAudience(resourceUri));

        Assert.Contains(
            "sr=https%3a%2f%2fmyns.servicebus.windows.net",
            signature,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("orders%2fsubscriptions", signature, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateSharedAccessSignature_keeps_non_default_rule_sas_entity_scoped()
    {
        var expiry = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var resourceUri = new Uri("https://myns.servicebus.windows.net/orders/subscriptions/sub123?api-version=2021-05");

        var signature = ServiceBusTopicsAuthenticator.GenerateSharedAccessSignature(
            resourceUri,
            "RootManageSharedAccessKey",
            "secret",
            expiry,
            ServiceBusTopicsAuthenticator.UseNamespaceScopedSasAudience(resourceUri));

        Assert.Contains(
            "sr=https%3a%2f%2fmyns.servicebus.windows.net%2forders%2fsubscriptions%2fsub123",
            signature,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UseNamespaceScopedSasAudience_matches_only_reserved_default_rule()
    {
        Assert.True(ServiceBusTopicsAuthenticator.UseNamespaceScopedSasAudience(
            new Uri("https://myns.servicebus.windows.net/orders/subscriptions/sub123/rules/%24Default?api-version=2021-05")));
        Assert.False(ServiceBusTopicsAuthenticator.UseNamespaceScopedSasAudience(
            new Uri("https://myns.servicebus.windows.net/orders/subscriptions/sub123/rules/custom?api-version=2021-05")));
        Assert.False(ServiceBusTopicsAuthenticator.UseNamespaceScopedSasAudience(
            new Uri("https://myns.servicebus.windows.net/orders/subscriptions/sub123?api-version=2021-05")));
    }

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
