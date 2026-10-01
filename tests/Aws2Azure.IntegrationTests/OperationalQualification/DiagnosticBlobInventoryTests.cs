using System.Net;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class DiagnosticBlobInventoryTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static DiagnosticBlobInventory Inventory(HttpClient http) =>
        new(http, new Uri("https://diagnostic.blob.core.windows.net/"), "diagnostic", Key);

    [Fact]
    public async Task Counts_all_pages_and_encodes_markers_without_following_urls()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("SharedKey", request.Headers.Authorization?.Scheme);
            Assert.Equal("diagnostic.blob.core.windows.net", request.RequestUri!.Host);
            Assert.True(request.Headers.Contains("x-ms-version"));
            if (++calls == 1)
                return Xml("<EnumerationResults><Containers><Container><Name>one</Name></Container></Containers><NextMarker>opaque+/=</NextMarker></EnumerationResults>");
            Assert.Contains("marker=opaque%2B%2F%3D", request.RequestUri.Query, StringComparison.Ordinal);
            return Xml("<EnumerationResults><Containers><Container><Name>two</Name></Container></Containers><NextMarker /></EnumerationResults>");
        }));
        Assert.Equal(2, await Inventory(http).CountAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("<Error />")]
    [InlineData("<EnumerationResults />")]
    [InlineData("<EnumerationResults><Containers><Container /></Containers></EnumerationResults>")]
    [InlineData("<EnumerationResults><Containers /><Containers /></EnumerationResults>")]
    [InlineData("<EnumerationResults><Containers /><NextMarker /><NextMarker /></EnumerationResults>")]
    public async Task Malformed_response_is_not_an_empty_baseline(string body)
    {
        using var http = new HttpClient(new Handler(_ => Xml(body)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Inventory(http).CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Repeated_marker_is_bounded()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Xml("<EnumerationResults><Containers /><NextMarker>again</NextMarker></EnumerationResults>");
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Inventory(http).CountAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task Http_failure_is_not_empty_inventory(int status)
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Inventory(http).CountAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
    }

    [Theory]
    [InlineData("false", "false", false)]
    [InlineData("true", "false", true)]
    [InlineData("false", "true", true)]
    [InlineData("", "", true)]
    public async Task Soft_delete_settings_are_explicitly_checked(string blob, string container, bool invalid)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("?restype=service&comp=properties", request.RequestUri!.Query);
            return Xml($"<StorageServiceProperties><DeleteRetentionPolicy><Enabled>{blob}</Enabled></DeleteRetentionPolicy><ContainerDeleteRetentionPolicy><Enabled>{container}</Enabled></ContainerDeleteRetentionPolicy></StorageServiceProperties>");
        }));
        if (invalid)
            await Assert.ThrowsAsync<InvalidDataException>(() => Inventory(http).VerifySettingsAsync(CancellationToken.None));
        else
            await Inventory(http).VerifySettingsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_propagates_without_request()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("must not send")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Inventory(http).CountAsync(cancellation.Token));
    }

    [Fact]
    public void Endpoint_mismatch_is_rejected_before_signing()
    {
        using var http = new HttpClient();
        Assert.Throws<InvalidDataException>(() => new DiagnosticBlobInventory(
            http, new Uri("https://other.blob.core.windows.net/"), "diagnostic", Key));
    }

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
