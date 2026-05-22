using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class ShardIteratorTokenCodecTests
{
    private static readonly byte[] DefaultKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<ShardIteratorToken> RoundTripTokens => new()
    {
        new ShardIteratorToken("orders", "shardId-000000000000", ShardIteratorType.TrimHorizon, null, FixedNow.ToUnixTimeSeconds()),
        new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.Latest, null, FixedNow.ToUnixTimeSeconds()),
        new ShardIteratorToken("orders", "shardId-000000000002", ShardIteratorType.AtSequenceNumber, "49646986683135544286507457936321625675700192471156785154", FixedNow.ToUnixTimeSeconds()),
        new ShardIteratorToken("orders", "shardId-000000000003", ShardIteratorType.AfterSequenceNumber, "49646986683135544286507457936321625675700192471156785155", FixedNow.ToUnixTimeSeconds()),
        new ShardIteratorToken("orders", "shardId-000000000004", ShardIteratorType.AtTimestamp, "2026-01-01T11:59:30Z", FixedNow.ToUnixTimeSeconds()),
    };

    [Theory]
    [MemberData(nameof(RoundTripTokens))]
    public void Encode_and_decode_round_trip_each_iterator_type(ShardIteratorToken expected)
    {
        var codec = NewCodec();

        var encoded = codec.Encode(expected);

        Assert.StartsWith(ShardIteratorTokenCodec.Prefix, encoded, StringComparison.Ordinal);
        Assert.True(codec.TryDecode(encoded, out var decoded, out var error));
        Assert.Equal(ShardIteratorVerifyError.None, error);
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Round_trip_preserves_escaped_fields_exactly()
    {
        var codec = NewCodec();
        var expected = new ShardIteratorToken(
            "stream|with\\pipes",
            "shard\\name|01",
            ShardIteratorType.AtSequenceNumber,
            "sequence\\value|42",
            FixedNow.ToUnixTimeSeconds());

        var encoded = codec.Encode(expected);

        Assert.True(codec.TryDecode(encoded, out var decoded, out var error));
        Assert.Equal(ShardIteratorVerifyError.None, error);
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Tampered_payload_returns_bad_signature()
    {
        var codec = NewCodec();
        var token = new ShardIteratorToken("orders", "shardId-000000000000", ShardIteratorType.Latest, null, FixedNow.ToUnixTimeSeconds());
        var encoded = codec.Encode(token);

        var tampered = FlipChar(encoded, ShardIteratorTokenCodec.Prefix.Length + 5);

        Assert.False(codec.TryDecode(tampered, out _, out var error));
        Assert.Equal(ShardIteratorVerifyError.BadSignature, error);
    }

    [Fact]
    public void Tampered_signature_returns_bad_signature()
    {
        var codec = NewCodec();
        var token = new ShardIteratorToken("orders", "shardId-000000000000", ShardIteratorType.Latest, null, FixedNow.ToUnixTimeSeconds());
        var encoded = codec.Encode(token);

        var tampered = FlipChar(encoded, encoded.Length - 1);

        Assert.False(codec.TryDecode(tampered, out _, out var error));
        Assert.Equal(ShardIteratorVerifyError.BadSignature, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("aws2az-it-")]
    [InlineData("aws2az-it-no-dot")]
    [InlineData("not-aws2az-it-abc.def")]
    [InlineData("aws2az-it-***.***")]
    public void Malformed_wire_format_returns_malformed_format(string encoded)
    {
        var codec = NewCodec();

        Assert.False(codec.TryDecode(encoded, out _, out var error));
        Assert.Equal(ShardIteratorVerifyError.MalformedFormat, error);
    }

    [Fact]
    public void Valid_signature_with_malformed_payload_returns_malformed_payload()
    {
        var codec = NewCodec();
        var encoded = SignRawPayload("v1|orders|shardId-000000000000|1|unexpected|" + FixedNow.ToUnixTimeSeconds(), DefaultKey);

        Assert.False(codec.TryDecode(encoded, out _, out var error));
        Assert.Equal(ShardIteratorVerifyError.MalformedPayload, error);
    }

    [Fact]
    public void Expired_token_returns_expired()
    {
        var clock = new ManualTimeProvider(FixedNow);
        var codec = NewCodec(clock);
        var token = new ShardIteratorToken("orders", "shardId-000000000000", ShardIteratorType.Latest, null, FixedNow.AddSeconds(-301).ToUnixTimeSeconds());

        var encoded = codec.Encode(token);

        Assert.False(codec.TryDecode(encoded, out _, out var error));
        Assert.Equal(ShardIteratorVerifyError.Expired, error);
    }

    [Fact]
    public void Token_at_five_minute_boundary_is_valid()
    {
        var clock = new ManualTimeProvider(FixedNow);
        var codec = NewCodec(clock);
        var expected = new ShardIteratorToken("orders", "shardId-000000000000", ShardIteratorType.Latest, null, FixedNow.AddSeconds(-300).ToUnixTimeSeconds());

        var encoded = codec.Encode(expected);

        Assert.True(codec.TryDecode(encoded, out var decoded, out var error));
        Assert.Equal(ShardIteratorVerifyError.None, error);
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Codecs_with_different_keys_reject_each_others_tokens()
    {
        var codecA = NewCodec();
        var codecB = new ShardIteratorTokenCodec(Encoding.UTF8.GetBytes("abcdef0123456789abcdef0123456789"), new ManualTimeProvider(FixedNow));
        var token = new ShardIteratorToken("orders", "shardId-000000000000", ShardIteratorType.AtTimestamp, "2026-01-01T11:59:30Z", FixedNow.ToUnixTimeSeconds());

        var encodedA = codecA.Encode(token);
        var encodedB = codecB.Encode(token);

        Assert.False(codecB.TryDecode(encodedA, out _, out var errorA));
        Assert.Equal(ShardIteratorVerifyError.BadSignature, errorA);
        Assert.False(codecA.TryDecode(encodedB, out _, out var errorB));
        Assert.Equal(ShardIteratorVerifyError.BadSignature, errorB);
    }

    [Fact]
    public void Factory_rejects_signing_keys_shorter_than_32_bytes()
    {
        var factory = NewFactory();
        var credentials = new EventHubsCredentials
        {
            Namespace = "myns",
            ShardIteratorSigningKey = Convert.ToBase64String(new byte[31]),
        };

        Assert.Throws<ArgumentException>(() => factory.Create(credentials));
    }

    [Fact]
    public void Factory_fallback_uses_same_process_key_for_multiple_codecs()
    {
        var credentials = new EventHubsCredentials { Namespace = "myns" };
        var token = new ShardIteratorToken("orders", "shardId-000000000000", ShardIteratorType.Latest, null, FixedNow.ToUnixTimeSeconds());

        var codecA = NewFactory().Create(credentials);
        var codecB = NewFactory().Create(credentials);
        var encodedA = codecA.Encode(token);
        var encodedB = codecB.Encode(token);

        Assert.True(codecA.TryDecode(encodedB, out var decodedB, out var errorB));
        Assert.Equal(ShardIteratorVerifyError.None, errorB);
        Assert.Equal(token, decodedB);
        Assert.True(codecB.TryDecode(encodedA, out var decodedA, out var errorA));
        Assert.Equal(ShardIteratorVerifyError.None, errorA);
        Assert.Equal(token, decodedA);
    }

    private static ShardIteratorTokenCodec NewCodec(TimeProvider? clock = null)
        => new(DefaultKey, clock ?? new ManualTimeProvider(FixedNow));

    private static ShardIteratorTokenCodecFactory NewFactory(TimeProvider? clock = null)
        => new(NullLogger<ShardIteratorTokenCodecFactory>.Instance, clock ?? new ManualTimeProvider(FixedNow));

    private static string FlipChar(string value, int index)
    {
        var chars = value.ToCharArray();
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    private static string SignRawPayload(string payload, byte[] key)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(key);
        var signatureBytes = hmac.ComputeHash(payloadBytes);
        return ShardIteratorTokenCodec.Prefix + Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signatureBytes);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
