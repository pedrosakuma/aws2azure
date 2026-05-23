using System;
using System.Collections.Generic;
using System.Linq;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Aws2Azure.Modules.Sqs.Xml;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Issue #99 — round-trip parity for MessageAttributes on the AMQP
/// receive path. The send side (AmqpSendMessageHandlers / batch sibling)
/// already serialises each SQS attribute as an ApplicationProperty[name]
/// + a side-channel registry on ApplicationProperty[Aws2Azure-AttrTypes].
/// These tests pin the reconstruction in AmqpReceiveMessageHandlers so
/// the receive path matches the REST round-trip and emits
/// MD5OfMessageAttributes via the shared SqsMessageMd5 helper.
/// </summary>
public sealed class AmqpReceiveMessageAttributesTests
{
    [Fact]
    public void BuildMessageAttributes_returns_null_when_filter_is_null()
    {
        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps: new Dictionary<string, object?> { ["foo"] = "bar" },
            filter: null);

        Assert.Null(attrs);
        Assert.Null(md5);
    }

    [Fact]
    public void BuildMessageAttributes_returns_null_when_app_props_missing_registry()
    {
        // No Aws2Azure-AttrTypes header → SQS attributes cannot be reconstructed
        // (they would be indistinguishable from arbitrary AMQP properties).
        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps: new Dictionary<string, object?> { ["foo"] = "bar" },
            filter: new HashSet<string>(StringComparer.Ordinal) { "All" });

        Assert.Null(attrs);
        Assert.Null(md5);
    }

    [Fact]
    public void BuildMessageAttributes_returns_null_when_app_props_is_null()
    {
        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps: null,
            filter: new HashSet<string>(StringComparer.Ordinal) { "All" });

        Assert.Null(attrs);
        Assert.Null(md5);
    }

    [Fact]
    public void BuildMessageAttributes_reconstructs_string_number_and_binary_under_All_filter()
    {
        var blobBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var blobBase64 = Convert.ToBase64String(blobBytes);
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "alice",
            ["score"] = "42",
            ["blob"] = blobBase64,
            [SendMessageHandlers.AttrTypesHeader] = "name=String,score=Number,blob=Binary",
        };

        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps,
            new HashSet<string>(StringComparer.Ordinal) { "All" });

        Assert.NotNull(attrs);
        Assert.NotNull(md5);
        Assert.Equal(3, attrs!.Count);

        var name = attrs["name"];
        Assert.Equal("String", name.DataType);
        Assert.Equal("alice", name.StringValue);
        Assert.False(name.IsBinary);

        var score = attrs["score"];
        Assert.Equal("Number", score.DataType);
        Assert.Equal("42", score.StringValue);

        var blob = attrs["blob"];
        Assert.Equal("Binary", blob.DataType);
        Assert.Null(blob.StringValue);
        Assert.Equal(blobBase64, blob.BinaryValueBase64);
        Assert.True(blob.IsBinary);

        // MD5 must match the REST path's algorithm on the same inputs so
        // SDKs that compare MD5OfMessageAttributes pass on both transports.
        var expected = SqsMessageMd5.OfAttributes(new SortedDictionary<string, SqsMessageAttribute>(StringComparer.Ordinal)
        {
            ["name"] = new() { DataType = "String", StringValue = "alice" },
            ["score"] = new() { DataType = "Number", StringValue = "42" },
            ["blob"] = new() { DataType = "Binary", BinaryValue = blobBytes },
        });
        Assert.Equal(expected, md5);
    }

    [Fact]
    public void BuildMessageAttributes_respects_explicit_filter_subset()
    {
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["keep"] = "yes",
            ["drop"] = "no",
            [SendMessageHandlers.AttrTypesHeader] = "keep=String,drop=String",
        };

        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps,
            new HashSet<string>(StringComparer.Ordinal) { "keep" });

        Assert.NotNull(attrs);
        Assert.Single(attrs!);
        Assert.True(attrs.ContainsKey("keep"));
        Assert.False(attrs.ContainsKey("drop"));
        Assert.NotNull(md5);
    }

    [Fact]
    public void BuildMessageAttributes_skips_unknown_filter_entries()
    {
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["a"] = "1",
            [SendMessageHandlers.AttrTypesHeader] = "a=String",
        };

        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps,
            new HashSet<string>(StringComparer.Ordinal) { "nope" });

        Assert.Null(attrs);
        Assert.Null(md5);
    }

    [Fact]
    public void BuildMessageAttributes_preserves_custom_type_suffix()
    {
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["v"] = "x",
            [SendMessageHandlers.AttrTypesHeader] = "v=String.custom",
        };

        var (attrs, _) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps,
            new HashSet<string>(StringComparer.Ordinal) { "All" });

        Assert.NotNull(attrs);
        Assert.Equal("String.custom", attrs!["v"].DataType);
    }

    [Fact]
    public void BuildMessageAttributes_treats_binary_subtype_as_binary_for_decoding()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        var b64 = Convert.ToBase64String(bytes);
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["b"] = b64,
            [SendMessageHandlers.AttrTypesHeader] = "b=Binary.custom",
        };

        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps,
            new HashSet<string>(StringComparer.Ordinal) { "All" });

        Assert.NotNull(attrs);
        var b = attrs!["b"];
        Assert.Equal("Binary.custom", b.DataType);
        Assert.Null(b.StringValue);
        Assert.Equal(b64, b.BinaryValueBase64);

        var expected = SqsMessageMd5.OfAttributes(new SortedDictionary<string, SqsMessageAttribute>(StringComparer.Ordinal)
        {
            ["b"] = new() { DataType = "Binary.custom", BinaryValue = bytes },
        });
        Assert.Equal(expected, md5);
    }

    [Fact]
    public void BuildMessageAttributes_invalid_base64_in_binary_yields_empty_bytes_for_md5()
    {
        // FormatException is intentionally swallowed so a malformed binary
        // payload still produces a deterministic MD5 (matching REST path).
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["b"] = "not-base64!@#",
            [SendMessageHandlers.AttrTypesHeader] = "b=Binary",
        };

        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps,
            new HashSet<string>(StringComparer.Ordinal) { "All" });

        Assert.NotNull(attrs);
        Assert.Equal("Binary", attrs!["b"].DataType);
        Assert.NotNull(md5);

        var expected = SqsMessageMd5.OfAttributes(new SortedDictionary<string, SqsMessageAttribute>(StringComparer.Ordinal)
        {
            ["b"] = new() { DataType = "Binary", BinaryValue = Array.Empty<byte>() },
        });
        Assert.Equal(expected, md5);
    }

    [Fact]
    public void BuildMessageAttributes_orders_md5_sources_alphabetically_regardless_of_registry_order()
    {
        // Registry pairs deliberately out of alphabetical order. AWS's
        // attribute MD5 algorithm is order-sensitive on attribute name,
        // so SortedDictionary on the receive side must produce the same
        // hash as the send side did.
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["zeta"] = "z",
            ["alpha"] = "a",
            [SendMessageHandlers.AttrTypesHeader] = "zeta=String,alpha=String",
        };

        var (attrs, md5) = AmqpReceiveMessageHandlers.BuildMessageAttributes(
            appProps,
            new HashSet<string>(StringComparer.Ordinal) { "All" });

        Assert.NotNull(attrs);
        Assert.Equal(new[] { "alpha", "zeta" }, attrs!.Keys.ToArray());

        var expected = SqsMessageMd5.OfAttributes(new SortedDictionary<string, SqsMessageAttribute>(StringComparer.Ordinal)
        {
            ["alpha"] = new() { DataType = "String", StringValue = "a" },
            ["zeta"] = new() { DataType = "String", StringValue = "z" },
        });
        Assert.Equal(expected, md5);
    }
}
