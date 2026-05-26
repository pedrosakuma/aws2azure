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

    [Theory]
    [InlineData("simple.txt")]
    [InlineData("a/b/c/d/e.txt")]
    [InlineData("UUID-123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("file_name-with.dots_and~tilde")]
    [InlineData("A.B.C/D.E.F/G_H-I.JKL")]
    public void Encode_fast_path_returns_input_reference_when_already_safe(string key)
    {
        // The encoder's fast-path must return the same string instance for
        // already-URL-safe inputs so callers don't pay an allocation per
        // blob request (S3 GetObject hot path).
        var encoded = S3ObjectKey.EncodeForBlobUrl(key);
        Assert.Same(key, encoded);
    }

    [Theory]
    [InlineData("name with spaces.txt")]
    [InlineData("a+b.txt")]
    [InlineData("café.txt")]
    [InlineData("ascii-but-with-#-hash")]
    public void Encode_slow_path_allocates_new_string_when_encoding_needed(string key)
    {
        var encoded = S3ObjectKey.EncodeForBlobUrl(key);
        Assert.NotSame(key, encoded);
        Assert.NotEqual(key, encoded);
    }

    [Fact]
    public void Encode_empty_returns_empty()
    {
        Assert.Equal(string.Empty, S3ObjectKey.EncodeForBlobUrl(string.Empty));
    }
}
