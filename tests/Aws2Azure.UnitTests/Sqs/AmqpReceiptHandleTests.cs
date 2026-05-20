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

    // --- v3 (session-bound) ----------------------------------------------

    [Fact]
    public void V3_round_trip_preserves_session_id()
    {
        var lockToken = Guid.NewGuid();
        var when = DateTimeOffset.UtcNow.AddMinutes(5);
        var encoded = AmqpReceiptHandle.Encode("fifo-q", lockToken, when, sessionId: "group-42");

        Assert.True(AmqpReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal("fifo-q", d.QueueName);
        Assert.Equal(lockToken, d.LockToken);
        Assert.Equal(when.ToUnixTimeSeconds(), d.LockedUntilUtc.ToUnixTimeSeconds());
        Assert.Equal("group-42", d.SessionId);
    }

    [Fact]
    public void V3_handle_has_distinct_base64_prefix()
    {
        var v2 = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var v3 = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), DateTimeOffset.UtcNow, sessionId: "s");
        Assert.StartsWith("Mjo", v2, StringComparison.Ordinal);
        Assert.StartsWith("Mzo", v3, StringComparison.Ordinal);

        // Both flavours must route through LooksLikeAmqpHandle so the
        // dispatcher hands a session settle back to the AMQP handler.
        Assert.True(AmqpReceiptHandle.LooksLikeAmqpHandle(v2));
        Assert.True(AmqpReceiptHandle.LooksLikeAmqpHandle(v3));
    }

    [Fact]
    public void Encode_with_null_or_empty_session_falls_back_to_v2()
    {
        var enc1 = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), DateTimeOffset.UtcNow, sessionId: null);
        var enc2 = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), DateTimeOffset.UtcNow, sessionId: "");
        Assert.StartsWith("Mjo", enc1, StringComparison.Ordinal);
        Assert.StartsWith("Mjo", enc2, StringComparison.Ordinal);

        Assert.True(AmqpReceiptHandle.TryDecode(enc1, out var d1));
        Assert.Null(d1.SessionId);
    }

    [Fact]
    public void V2_handle_decodes_with_null_session_id()
    {
        // Back-compat: receipts minted before this slice (pre-v3) must
        // continue to decode after upgrade. SessionId reads as null.
        var v2 = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.True(AmqpReceiptHandle.TryDecode(v2, out var d));
        Assert.Null(d.SessionId);
    }

    [Theory]
    [InlineData("session:with:colons")]
    [InlineData("session|with|pipes")]
    [InlineData("session/with/slashes")]
    [InlineData("ünicodesëssion")]
    public void V3_round_trip_preserves_session_id_with_metachars(string sessionId)
    {
        var encoded = AmqpReceiptHandle.Encode("q", Guid.NewGuid(), DateTimeOffset.UtcNow, sessionId);
        Assert.True(AmqpReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal(sessionId, d.SessionId);
    }

    [Fact]
    public void V3_decode_rejects_missing_session_field()
    {
        // Hand-craft a v3 payload that's missing the session-id field
        // (mimics a truncated handle). Must fail rather than silently
        // return SessionId=null.
        var raw = "3:" + "1:q" + "36:11111111-2222-3333-4444-555555555555" + "0:";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        Assert.False(AmqpReceiptHandle.TryDecode(encoded, out _));
    }

    [Fact]
    public void V3_decode_rejects_trailing_bytes()
    {
        // v3 must consume queue + token + when + session — anything left
        // is corruption / version drift.
        var raw = "3:" + "1:q" + "36:11111111-2222-3333-4444-555555555555" + "0:" + "1:s" + "1:x";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        Assert.False(AmqpReceiptHandle.TryDecode(encoded, out _));
    }
}
