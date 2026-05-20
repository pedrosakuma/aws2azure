using System;
using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class AmqpReceiptHandleTests
{
    [Fact]
    public void Round_trip_preserves_components()
    {
        var lockToken = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var when = DateTimeOffset.UtcNow.AddMinutes(5);
        var encoded = AmqpReceiptHandle.Encode("my-queue", lockToken, when);
        Assert.True(AmqpReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal("my-queue", d.QueueName);
        Assert.Equal(lockToken, d.LockToken);
        Assert.Equal(when.ToUnixTimeSeconds(), d.LockedUntilUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public void Round_trip_accepts_zero_lockedUntil()
    {
        var encoded = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), default);
        Assert.True(AmqpReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal(default, d.LockedUntilUtc);
    }

    [Fact]
    public void Encoded_handle_is_distinguishable_from_REST_v1_handle()
    {
        // The dispatcher must be able to route Delete back to the right
        // handler. v1 prefix is "1:", v2 prefix is "2:" — guard the
        // canonical 3-char base64 prefix used by LooksLikeAmqpHandle.
        var amqp = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var rest = ReceiptHandle.Encode("msg", "tok", "1", DateTimeOffset.UtcNow);
        Assert.True(AmqpReceiptHandle.LooksLikeAmqpHandle(amqp));
        Assert.False(AmqpReceiptHandle.LooksLikeAmqpHandle(rest));
    }

    [Theory]
    [InlineData("not base64!!!")]
    [InlineData("")]
    public void Decode_rejects_malformed_input(string input)
    {
        Assert.False(AmqpReceiptHandle.TryDecode(input, out _));
    }

    [Fact]
    public void Decode_rejects_REST_v1_handle()
    {
        var v1 = ReceiptHandle.Encode("msg", "tok", "1", DateTimeOffset.UtcNow);
        Assert.False(AmqpReceiptHandle.TryDecode(v1, out _));
    }

    [Fact]
    public void Decode_rejects_non_guid_lock_token()
    {
        var raw = "1:2" + "1:q" + "12:not-a-guid--" + "0:";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        Assert.False(AmqpReceiptHandle.TryDecode(encoded, out _));
    }

    [Theory]
    [InlineData("queue|with|pipes")]
    [InlineData("queue:with:colons")]
    [InlineData("queue/with/slashes")]
    public void Round_trip_preserves_queue_name_with_metachars(string queueName)
    {
        var encoded = AmqpReceiptHandle.Encode(queueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.True(AmqpReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal(queueName, d.QueueName);
    }

    [Fact]
    public void Encode_requires_non_empty_queue_name()
    {
        Assert.Throws<ArgumentException>(() =>
            AmqpReceiptHandle.Encode("", Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
}
