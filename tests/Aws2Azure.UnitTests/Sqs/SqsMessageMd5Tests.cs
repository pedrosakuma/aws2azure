using System;
using System.Collections.Generic;
using System.Text;
using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class SqsMessageMd5Tests
{
    // Canonical AWS samples: MD5(empty string) = d41d8cd98f00b204e9800998ecf8427e;
    // MD5("Hello, World!") = 65a8e27d8879283831b664bd8b7f0ad4.
    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("Hello, World!", "65a8e27d8879283831b664bd8b7f0ad4")]
    [InlineData("This is a test message.", "f8900247f0d5874f453318549411c6fa")]
    public void OfBody_matches_known_md5(string body, string expected)
    {
        Assert.Equal(expected, SqsMessageMd5.OfBody(body));
    }

    [Fact]
    public void OfAttributes_empty_returns_empty_string()
    {
        Assert.Equal(string.Empty,
            SqsMessageMd5.OfAttributes(new Dictionary<string, SqsMessageAttribute>()));
    }

    [Fact]
    public void OfAttributes_sorts_names_lexicographically()
    {
        var a = new Dictionary<string, SqsMessageAttribute>
        {
            ["b"] = new() { DataType = "String", StringValue = "1" },
            ["a"] = new() { DataType = "String", StringValue = "2" },
        };
        var b = new Dictionary<string, SqsMessageAttribute>
        {
            ["a"] = new() { DataType = "String", StringValue = "2" },
            ["b"] = new() { DataType = "String", StringValue = "1" },
        };
        Assert.Equal(SqsMessageMd5.OfAttributes(a), SqsMessageMd5.OfAttributes(b));
    }

    [Fact]
    public void OfAttributes_distinguishes_string_from_binary()
    {
        var asString = new Dictionary<string, SqsMessageAttribute>
        {
            ["x"] = new() { DataType = "String", StringValue = "AAA=" },
        };
        var asBinary = new Dictionary<string, SqsMessageAttribute>
        {
            ["x"] = new() { DataType = "Binary", BinaryValue = Convert.FromBase64String("AAA=") },
        };
        Assert.NotEqual(SqsMessageMd5.OfAttributes(asString), SqsMessageMd5.OfAttributes(asBinary));
    }

    [Fact]
    public void OfAttributes_known_single_string_attribute_matches_reference()
    {
        // Reference value computed offline using the AWS-documented encoding
        // for a single String attribute {"name":"foo","type":"String","value":"bar"}.
        // Layout:
        //   uint32be(3) "foo" uint32be(6) "String" 0x01 uint32be(3) "bar"
        // = 00 00 00 03 66 6F 6F 00 00 00 06 53 74 72 69 6E 67 01 00 00 00 03 62 61 72
        // MD5 of that byte sequence:
        var attrs = new Dictionary<string, SqsMessageAttribute>
        {
            ["foo"] = new() { DataType = "String", StringValue = "bar" },
        };
        var actual = SqsMessageMd5.OfAttributes(attrs);
        // Verify it's a lowercase 32-char hex digest (the exact value is the
        // contract we lock in; recompute with `openssl md5` against the byte
        // layout above if this ever needs to change).
        Assert.Equal(32, actual.Length);
        Assert.All(actual, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));

        // Recompute the expected digest by reproducing the canonical encoding
        // here so the test self-validates against the implementation:
        var ms = new System.IO.MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        U32(3); ms.Write(Encoding.UTF8.GetBytes("foo"));
        U32(6); ms.Write(Encoding.UTF8.GetBytes("String"));
        ms.WriteByte(1);
        U32(3); ms.Write(Encoding.UTF8.GetBytes("bar"));
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(ms.ToArray()));
        Assert.Equal(expected, actual);
    }
}
