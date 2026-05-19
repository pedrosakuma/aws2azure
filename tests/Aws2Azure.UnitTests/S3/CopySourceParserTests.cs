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
    [InlineData("arn:aws:s3:::b/k",   "ARN")]
    [InlineData("/b/k?versionId=abc", "versionId")]
    [InlineData("/b/%ZZ-bad",         "percent")]
    public void Rejects_invalid_sources(string? raw, string expectFragment)
    {
        var r = CopySourceParser.Parse(raw);
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
        Assert.Contains(expectFragment, r.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
