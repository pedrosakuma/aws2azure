using System;
using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class ReceiptHandleTests
{
    [Fact]
    public void Round_trip_preserves_components()
    {
        var when = DateTimeOffset.UtcNow.AddMinutes(5);
        var encoded = ReceiptHandle.Encode("msg-1", "tok-2", "123", when);
        Assert.True(ReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal("msg-1", d.MessageId);
        Assert.Equal("tok-2", d.LockToken);
        Assert.Equal("123", d.SequenceNumber);
        // ISO-8601 round-trip is exact to the tick once normalised to UTC.
        Assert.Equal(when.ToUnixTimeSeconds(), d.LockedUntilUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public void Encode_is_opaque_base64()
    {
        var encoded = ReceiptHandle.Encode("a", "b", "c", DateTimeOffset.UtcNow);
        var raw = Convert.FromBase64String(encoded);
        Assert.NotEmpty(raw);
    }

    [Theory]
    [InlineData("not base64!!!")]
    [InlineData("")]
    public void Decode_rejects_malformed_input(string input)
    {
        Assert.False(ReceiptHandle.TryDecode(input, out _));
    }

    [Fact]
    public void Decode_rejects_wrong_version()
    {
        // A handle from a hypothetical v2 format must not be silently accepted by v1 code.
        var raw = "1:2" + "3:msg" + "3:tok" + "3:seq" + "20:2026-01-01T00:00:00Z";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        Assert.False(ReceiptHandle.TryDecode(encoded, out _));
    }

    [Fact]
    public void Decode_rejects_truncated_payload()
    {
        var raw = "1:1" + "9:onlytwo";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        Assert.False(ReceiptHandle.TryDecode(encoded, out _));
    }

    [Theory]
    [InlineData("contains|pipe")]
    [InlineData("with:colon:everywhere")]
    [InlineData("123:lookslikeprefix")]
    [InlineData("multi\nline\rid")]
    public void Round_trip_preserves_messageId_with_metachars(string messageId)
    {
        // FIFO MessageDeduplicationId is caller-controlled and can contain
        // any UTF-8 — including separators we used to delimit on. Verify
        // every field comes back intact regardless of content.
        var when = DateTimeOffset.UtcNow;
        var encoded = ReceiptHandle.Encode(messageId, "lock-token", "42", when);
        Assert.True(ReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal(messageId, d.MessageId);
        Assert.Equal("lock-token", d.LockToken);
        Assert.Equal("42", d.SequenceNumber);
    }

    [Fact]
    public void Round_trip_preserves_multibyte_utf8_components()
    {
        var encoded = ReceiptHandle.Encode("ｍｅｓｓａｇｅ-😀", "tok", "1", DateTimeOffset.UtcNow);
        Assert.True(ReceiptHandle.TryDecode(encoded, out var d));
        Assert.Equal("ｍｅｓｓａｇｅ-😀", d.MessageId);
    }

    [Fact]
    public void Encode_requires_non_empty_messageId_and_lockToken()
    {
        Assert.Throws<ArgumentException>(() => ReceiptHandle.Encode("", "tok", "1", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => ReceiptHandle.Encode("msg", "", "1", DateTimeOffset.UtcNow));
    }
}
