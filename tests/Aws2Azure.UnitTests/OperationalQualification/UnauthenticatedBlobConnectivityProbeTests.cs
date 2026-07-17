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

    [Theory]
    [InlineData(
        "<Error><Details><Code>AuthenticationFailed</Code></Details></Error>")]
    [InlineData(
        "<Error><Code>AuthenticationFailed</Code><Code>AuthenticationFailed</Code></Error>")]
    public async Task ValidateResponseAsync_rejects_non_unique_direct_child_code(string body)
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

    [Fact]
    public async Task MeasureHeaderLatenciesAsync_times_out_stalled_error_body()
    {
        using var client = new HttpClient(new StalledBodyHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => UnauthenticatedBlobConnectivityProbe.MeasureHeaderLatenciesAsync(
                client,
                new Uri("https://example.invalid/?comp=list"),
                1,
                TimeSpan.FromMilliseconds(50)));
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

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StreamContent(new StalledReadStream()),
            });
        }
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException("Synchronous reads are not allowed.");
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
