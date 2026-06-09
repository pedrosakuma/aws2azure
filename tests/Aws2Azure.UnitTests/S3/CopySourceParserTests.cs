using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class CopySourceParserTests
{
    [Theory]
    [InlineData("/bucket/key.txt",      "bucket", "key.txt")]
    [InlineData("bucket/key.txt",       "bucket", "key.txt")]
    [InlineData("/b/a/deep/key.txt",    "b",      "a/deep/key.txt")]
    [InlineData("/b/with%20space.txt",  "b",      "with space.txt")]
    [InlineData("/b/%E2%9C%93-check",   "b",      "✓-check")]
    // AWS SDKs percent-encode the separator (and the whole value) when
    // marshalling CopyObjectRequest — this is the default wire form.
    [InlineData("bucket%2Fkey.txt",            "bucket", "key.txt")]
    [InlineData("bucket%2fkey.txt",            "bucket", "key.txt")]
    [InlineData("perf-bkt%2Fperf-copy-src%2F0", "perf-bkt", "perf-copy-src/0")]
    [InlineData("b%2Fwith%20space.txt",        "b",      "with space.txt")]
    public void Parses_well_formed_sources(string raw, string expectedBucket, string expectedKey)
    {
        var r = CopySourceParser.Parse(raw);
        Assert.True(r.Success, r.Error);
        Assert.Equal(expectedBucket, r.Bucket);
        Assert.Equal(expectedKey, r.Key);
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
    [InlineData("/b/k?versionId=abc", "versionId")]
    [InlineData("b%2Fk?versionId=abc", "versionId")]
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
