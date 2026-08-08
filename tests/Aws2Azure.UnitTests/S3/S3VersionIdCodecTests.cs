using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public sealed class S3VersionIdCodecTests
{
    [Theory]
    [InlineData("2024-01-15T10:30:00.1234567Z")]
    [InlineData("2026-08-07T23:54:00.4853610+00:00")]
    public void Encode_and_decode_round_trip(string azureVersionId)
    {
        var encoded = S3VersionIdCodec.Encode(azureVersionId);

        Assert.True(S3VersionIdCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(azureVersionId, decoded);
        Assert.DoesNotContain(":", encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("not!!!base64")]
    public void TryDecode_rejects_invalid_tokens(string token)
    {
        Assert.False(S3VersionIdCodec.TryDecode(token, out _));
    }
}
