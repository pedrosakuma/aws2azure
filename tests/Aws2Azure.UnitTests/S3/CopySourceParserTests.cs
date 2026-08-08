using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class CopySourceParserTests
{
    [Theory]
    [InlineData("/bucket/key.txt",      "bucket", "key.txt", null)]
    [InlineData("bucket/key.txt",       "bucket", "key.txt", null)]
    [InlineData("/b/a/deep/key.txt",    "b",      "a/deep/key.txt", null)]
    [InlineData("/b/with%20space.txt",  "b",      "with space.txt", null)]
    [InlineData("/b/%E2%9C%93-check",   "b",      "✓-check", null)]
    // AWS SDKs percent-encode the separator (and the whole value) when
    // marshalling CopyObjectRequest — this is the default wire form.
    [InlineData("bucket%2Fkey.txt",            "bucket", "key.txt", null)]
    [InlineData("bucket%2fkey.txt",            "bucket", "key.txt", null)]
    [InlineData("perf-bkt%2Fperf-copy-src%2F0", "perf-bkt", "perf-copy-src/0", null)]
    [InlineData("b%2Fwith%20space.txt",        "b",      "with space.txt", null)]
    [InlineData("/bucket/key.txt?versionId=v1", "bucket", "key.txt", "v1")]
    [InlineData("bucket%2Fkey.txt?versionId=v2", "bucket", "key.txt", "v2")]
    [InlineData("bucket%2Fkey%3FversionId%3Dliteral.txt", "bucket", "key?versionId=literal.txt", null)]
    public void Parses_well_formed_sources(string raw, string expectedBucket, string expectedKey, string? expectedVersionId)
    {
        var r = CopySourceParser.Parse(raw);
        Assert.True(r.Success, r.Error);
        Assert.Equal(expectedBucket, r.Bucket);
        Assert.Equal(expectedKey, r.Key);
        Assert.Equal(expectedVersionId, r.VersionId);
    }

    [Fact]
    public void Parses_and_decodes_encoded_version_id()
    {
        var encoded = S3VersionIdCodec.Encode("2024-01-15T10:30:00.1234567Z");

        var result = CopySourceParser.Parse("/bucket/key.txt?versionId=" + encoded);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2024-01-15T10:30:00.1234567Z", result.VersionId);
    }

    [Theory]
    [InlineData(null,                 "required")]
    [InlineData("",                   "required")]
    [InlineData("/",                  "bucket")]
    [InlineData("/bucket-only",       "bucket")]
    [InlineData("/bucket/",           "bucket")]
    [InlineData("bucket%2F",          "bucket")]
    [InlineData("%2Fbucket%2Fkey",    "bucket")]
    [InlineData("arn:aws:s3:::b/k",   "ARN")]
    [InlineData("/b/%ZZ-bad",         "percent")]
    [InlineData("b%2F%ZZ-bad",        "percent")]
    public void Rejects_invalid_sources(string? raw, string expectFragment)
    {
        var r = CopySourceParser.Parse(raw);
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
        Assert.Contains(expectFragment, r.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
