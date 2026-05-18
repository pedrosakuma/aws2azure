using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class ContinuationTokenCodecTests
{
    [Theory]
    [InlineData("simple-marker")]
    [InlineData("2!000000!some/blob/key.txt!00009")]
    [InlineData("with spaces and / slashes")]
    [InlineData("unicode-✓-and-emoji-🚀")]
    [InlineData("a")] // padding edge case
    [InlineData("ab")] // padding edge case
    [InlineData("abc")] // padding edge case
    public void Round_trips_arbitrary_markers(string marker)
    {
        var token = ContinuationTokenCodec.Encode(marker);
        Assert.NotEqual(marker, token); // surface looks opaque
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);

        var decoded = ContinuationTokenCodec.TryDecode(token);
        Assert.Equal(marker, decoded);
    }

    [Theory]
    [InlineData("not!!!base64")]      // illegal chars
    [InlineData("a")]                  // length % 4 == 1 (invalid)
    public void TryDecode_returns_null_for_malformed_tokens(string token)
    {
        Assert.Null(ContinuationTokenCodec.TryDecode(token));
    }

    [Fact]
    public void TryDecode_empty_returns_null()
    {
        Assert.Null(ContinuationTokenCodec.TryDecode(string.Empty));
    }
}
