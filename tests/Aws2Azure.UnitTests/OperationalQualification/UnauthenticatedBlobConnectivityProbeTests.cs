using System.Net;
using System.Text;
using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.UnitTests.OperationalQualification;

public sealed class UnauthenticatedBlobConnectivityProbeTests
{
    [Theory]
    [InlineData("AuthenticationFailed")]
    [InlineData("AuthorizationFailure")]
    [InlineData("AuthorizationPermissionMismatch")]
    public async Task ValidateResponseAsync_accepts_known_403_authentication_denial(string code)
    {
        using var response = Response(HttpStatusCode.Forbidden, Error(code));

        await UnauthenticatedBlobConnectivityProbe.ValidateResponseAsync(response);
    }

    [Fact]
    public async Task ValidateResponseAsync_accepts_409_public_access_not_permitted()
    {
        using var response = Response(
            HttpStatusCode.Conflict,
            Error("PublicAccessNotPermitted"));

        await UnauthenticatedBlobConnectivityProbe.ValidateResponseAsync(response);
    }

    [Fact]
    public async Task ValidateResponseAsync_rejects_unrelated_409()
    {
        using var response = Response(HttpStatusCode.Conflict, Error("ContainerBeingDeleted"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => UnauthenticatedBlobConnectivityProbe.ValidateResponseAsync(response));
    }

    [Theory]
    [InlineData("<Error><Code>AuthenticationFailed</Error>")]
    [InlineData("<Error><Message>denied</Message></Error>")]
    public async Task ValidateResponseAsync_rejects_malformed_error_body(string body)
    {
        using var response = Response(HttpStatusCode.Forbidden, body);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => UnauthenticatedBlobConnectivityProbe.ValidateResponseAsync(response));
    }

    [Fact]
    public async Task ValidateResponseAsync_rejects_oversized_error_body()
    {
        var body = Error(
            "AuthenticationFailed",
            new string('x', UnauthenticatedBlobConnectivityProbe.MaximumErrorBodyBytes));
        using var response = Response(HttpStatusCode.Forbidden, body);
        response.Content.Headers.ContentLength = null;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => UnauthenticatedBlobConnectivityProbe.ValidateResponseAsync(response));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task ValidateResponseAsync_rejects_non_denial_status(HttpStatusCode statusCode)
    {
        using var response = Response(statusCode, string.Empty);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => UnauthenticatedBlobConnectivityProbe.ValidateResponseAsync(response));
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
    }

    private static string Error(string code, string message = "denied")
    {
        return $"<Error><Code>{code}</Code><Message>{message}</Message></Error>";
    }
}
