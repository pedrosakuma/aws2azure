using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class S3ObjectKeyTests
{
    [Theory]
    [InlineData("simple.txt", true)]
    [InlineData("folder/sub/file.txt", true)]
    [InlineData("name with spaces.txt", true)]
    [InlineData("a+b%c.txt", true)]
    [InlineData("файл.txt", true)]
    [InlineData("", false)]
    [InlineData("with\0nul", false)]
    [InlineData("with\nnewline", false)]
    [InlineData("../escape.txt", false)]
    [InlineData("a/../b.txt", false)]
    [InlineData("./relative.txt", false)]
    [InlineData("/leading-slash.txt", false)]
    [InlineData("double//slash.txt", false)]
    public void Validates_keys(string key, bool expected)
    {
        Assert.Equal(expected, S3ObjectKey.IsValid(key));
    }

    [Fact]
    public void Rejects_overlong_keys()
    {
        var key = new string('a', S3ObjectKey.MaxBytes + 1);
        Assert.False(S3ObjectKey.IsValid(key));
    }

    [Theory]
    [InlineData("simple.txt", "simple.txt")]
    [InlineData("folder/sub/file.txt", "folder/sub/file.txt")]
    [InlineData("a b c.txt", "a%20b%20c.txt")]
    [InlineData("a+b.txt", "a%2Bb.txt")]
    [InlineData("a%b.txt", "a%25b.txt")]
    public void Encodes_for_blob_url_preserving_slashes(string key, string expected)
    {
        Assert.Equal(expected, S3ObjectKey.EncodeForBlobUrl(key));
    }

    [Fact]
    public void Encodes_unicode_via_utf8()
    {
        // 'é' is U+00E9 → UTF-8 0xC3 0xA9 → %C3%A9
        Assert.Equal("caf%C3%A9.txt", S3ObjectKey.EncodeForBlobUrl("café.txt"));
    }
}
