using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public class SharedKeyAuthenticatorTests
{
    private const string AccountName = "devstoreaccount1";
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    [Fact]
    public void ComputeSignature_ProducesExpectedHmac()
    {
        var auth = new SharedKeyAuthenticator(AccountName, AccountKey);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://" + AccountName + ".blob.core.windows.net/container?restype=container&comp=list");
        request.Headers.TryAddWithoutValidation("x-ms-date", "Fri, 26 Jun 2015 23:39:12 GMT");
        request.Headers.TryAddWithoutValidation("x-ms-version", "2015-02-21");

        var sig = auth.ComputeSignature(request);

        // Re-derive the signature using the same algorithm against the documented StringToSign layout.
        var stringToSign =
            "GET\n\n\n\n\n\n\n\n\n\n\n\n" +
            "x-ms-date:Fri, 26 Jun 2015 23:39:12 GMT\n" +
            "x-ms-version:2015-02-21\n" +
            "/devstoreaccount1/container\ncomp:list\nrestype:container";
        using var hmac = new HMACSHA256(Convert.FromBase64String(AccountKey));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        Assert.Equal(expected, sig);
    }

    [Fact]
    public void CanonicalizedResource_SortsAndLowercasesQuery()
    {
        var uri = new Uri("https://acct.blob.core.windows.net/foo?Z=1&a=2&a=1");
        var canonical = SharedKeyAuthenticator.BuildCanonicalizedResource(uri, "acct");
        Assert.Equal("/acct/foo\na:1,2\nz:1", canonical);
    }

    [Fact]
    public void CanonicalizedHeaders_FoldsAndSortsXmsHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://acct.blob.core.windows.net/");
        request.Headers.TryAddWithoutValidation("X-MS-Date", "Fri, 26 Jun 2015 23:39:12 GMT");
        request.Headers.TryAddWithoutValidation("x-ms-meta-Author", "  John  Doe  ");
        var headers = SharedKeyAuthenticator.BuildCanonicalizedHeaders(request);
        Assert.Equal("x-ms-date:Fri, 26 Jun 2015 23:39:12 GMT\nx-ms-meta-author:John Doe\n", headers);
    }

    [Fact]
    public async System.Threading.Tasks.Task AuthenticateAsync_SetsHeaderAndDefaults()
    {
        var auth = new SharedKeyAuthenticator(AccountName, AccountKey);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://" + AccountName + ".blob.core.windows.net/c?restype=container");
        await auth.AuthenticateAsync(request);
        Assert.True(request.Headers.Contains("x-ms-date"));
        Assert.True(request.Headers.Contains("x-ms-version"));
        Assert.True(request.Headers.Contains("Authorization"));
        var value = string.Join(",", request.Headers.GetValues("Authorization"));
        Assert.StartsWith("SharedKey " + AccountName + ":", value);
    }

    [Theory]
    [InlineData("John Doe")]
    [InlineData("application/json")]
    [InlineData("Fri, 26 Jun 2015 23:39:12 GMT")]
    [InlineData("")]
    [InlineData("x")]
    public void CollapseWhitespace_NoCollapseNeeded_ReturnsOriginalReference(string input)
    {
        var result = SharedKeyAuthenticator.CollapseWhitespace(input);
        if (input.Length == 0)
        {
            Assert.Same(string.Empty, result);
        }
        else
        {
            Assert.Same(input, result);
        }
    }

    [Theory]
    [InlineData("  John  Doe  ", "John Doe")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a  b   c", "a b c")]
    [InlineData("   ", "")]
    public void CollapseWhitespace_CollapsesAndTrims(string input, string expected)
    {
        Assert.Equal(expected, SharedKeyAuthenticator.CollapseWhitespace(input));
    }

    [Fact]
    public void ComputeSignature_StableUnderMultipleInvocations()
    {
        var auth = new SharedKeyAuthenticator(AccountName, AccountKey);
        var request = new HttpRequestMessage(HttpMethod.Put, "https://" + AccountName + ".blob.core.windows.net/c/o");
        request.Headers.TryAddWithoutValidation("x-ms-date", "Fri, 26 Jun 2015 23:39:12 GMT");
        request.Headers.TryAddWithoutValidation("x-ms-version", "2021-12-02");
        request.Headers.TryAddWithoutValidation("x-ms-meta-key", "value");
        request.Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var s1 = auth.ComputeSignature(request);
        var s2 = auth.ComputeSignature(request);
        Assert.Equal(s1, s2);
        Assert.Equal(44, s1.Length);
    }

    [Fact]
    public void CanonicalizedResource_SortsRepeatedKeysAcrossOccurrences()
    {
        var uri = new Uri("https://acct.blob.core.windows.net/foo?a=2&z=1&a=1&A=3");
        var canonical = SharedKeyAuthenticator.BuildCanonicalizedResource(uri, "acct");
        // Repeated keys (case-folded) collapse into a single sorted comma-joined value list.
        Assert.Equal("/acct/foo\na:1,2,3\nz:1", canonical);
    }

    [Fact]
    public void ComputeSignature_MatchesPreviousStringBasedImplementation()
    {
        var auth = new SharedKeyAuthenticator(AccountName, AccountKey);
        var request = new HttpRequestMessage(HttpMethod.Put, "https://" + AccountName + ".blob.core.windows.net/c/path/to/obj?comp=block&blockid=xyz");
        request.Headers.TryAddWithoutValidation("x-ms-date", "Fri, 26 Jun 2015 23:39:12 GMT");
        request.Headers.TryAddWithoutValidation("x-ms-version", "2021-12-02");
        request.Headers.TryAddWithoutValidation("x-ms-meta-author", "  John  Doe  ");
        request.Content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var actual = auth.ComputeSignature(request);

        // Hand-built reference StringToSign per the Azure Storage SharedKey spec.
        var stringToSign =
            "PUT\n" +
            "\n\n" +
            "3\n" +
            "\napplication/octet-stream\n" +
            "\n\n\n\n\n\n" +
            "x-ms-date:Fri, 26 Jun 2015 23:39:12 GMT\n" +
            "x-ms-meta-author:John Doe\n" +
            "x-ms-version:2021-12-02\n" +
            "/devstoreaccount1/c/path/to/obj\nblockid:xyz\ncomp:block";
        using var hmac = new HMACSHA256(Convert.FromBase64String(AccountKey));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        Assert.Equal(expected, actual);
    }
}
