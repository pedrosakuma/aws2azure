using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class UploadIdCodecTests
{
    private static readonly byte[] KeyA = Convert.FromBase64String("dGVzdC1rZXktQS0xMjM0NTY3ODkwYWJjZGVm");
    private static readonly byte[] KeyB = Convert.FromBase64String("ZGlmZmVyZW50LWtleS0wMTIzNDU2Nzg5YWJjZA==");

    [Fact]
    public void Issued_token_round_trips()
    {
        var t = UploadIdCodec.Issue("acct", "bucket", "object/key.bin", KeyA);
        Assert.Equal(43, t.Encoded.Length); // base64url of 32 bytes
        var decoded = UploadIdCodec.TryDecode(t.Encoded, "acct", "bucket", "object/key.bin", KeyA);
        Assert.NotNull(decoded);
        Assert.Equal(t.NonceHex, decoded!.Value.NonceHex);
    }

    [Fact]
    public void Token_bound_to_account_rejected_on_other_account()
    {
        var t = UploadIdCodec.Issue("acctA", "bucket", "key", KeyA);
        Assert.Null(UploadIdCodec.TryDecode(t.Encoded, "acctB", "bucket", "key", KeyA));
    }

    [Fact]
    public void Token_bound_to_container_rejected_on_other_container()
    {
        var t = UploadIdCodec.Issue("acct", "b1", "key", KeyA);
        Assert.Null(UploadIdCodec.TryDecode(t.Encoded, "acct", "b2", "key", KeyA));
    }

    [Fact]
    public void Token_bound_to_key_rejected_on_other_key()
    {
        var t = UploadIdCodec.Issue("acct", "bucket", "k1", KeyA);
        Assert.Null(UploadIdCodec.TryDecode(t.Encoded, "acct", "bucket", "k2", KeyA));
    }

    [Fact]
    public void Token_signed_with_different_key_is_rejected()
    {
        var t = UploadIdCodec.Issue("acct", "bucket", "key", KeyA);
        Assert.Null(UploadIdCodec.TryDecode(t.Encoded, "acct", "bucket", "key", KeyB));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var past = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);
        var t = UploadIdCodec.Issue("acct", "bucket", "key", KeyA, now: past);
        Assert.Null(UploadIdCodec.TryDecode(t.Encoded, "acct", "bucket", "key", KeyA));
    }

    [Fact]
    public void Garbage_token_is_rejected()
    {
        Assert.Null(UploadIdCodec.TryDecode("not-base64-url!", "a", "b", "k", KeyA));
        Assert.Null(UploadIdCodec.TryDecode("", "a", "b", "k", KeyA));
        Assert.Null(UploadIdCodec.TryDecode("AAAA", "a", "b", "k", KeyA));
    }

    [Fact]
    public void BlockId_format_is_fixed_length()
    {
        // All block IDs for a blob must be the same encoded length (Azure constraint).
        var nonce = new string('0', 16);
        var id1 = UploadIdCodec.BlockId(nonce, 1);
        var id100 = UploadIdCodec.BlockId(nonce, 100);
        var id10000 = UploadIdCodec.BlockId(nonce, 10000);
        Assert.Equal(id1.Length, id100.Length);
        Assert.Equal(id1.Length, id10000.Length);
    }

    [Fact]
    public void BlockId_rejects_invalid_part_numbers()
    {
        var nonce = new string('a', 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadIdCodec.BlockId(nonce, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadIdCodec.BlockId(nonce, 10001));
    }

    [Fact]
    public void BlockIds_differ_between_uploads_on_same_blob()
    {
        // Different nonces → different block IDs → concurrent uploads don't clobber each other.
        var n1 = new string('1', 16);
        var n2 = new string('2', 16);
        Assert.NotEqual(UploadIdCodec.BlockId(n1, 1), UploadIdCodec.BlockId(n2, 1));
    }
}
