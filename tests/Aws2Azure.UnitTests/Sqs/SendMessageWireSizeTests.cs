using System.Collections.Generic;
using System.Text;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Pins SQS-faithful wire-size accounting: the 1 MiB cap (raised from
/// 256 KiB in August 2025) covers the message body <em>plus</em> attributes
/// (name + data type + value bytes per attribute). These tests cover the
/// helper used by both SendMessage and SendMessageBatch.
/// </summary>
public sealed class SendMessageWireSizeTests
{
    [Fact]
    public void Body_only_counts_body_bytes()
    {
        var size = SendMessageHandlers.ComputeWireSize(1024, attributes: null);
        Assert.Equal(1024, size);
    }

    [Fact]
    public void String_attribute_adds_name_type_and_value_bytes()
    {
        var attrs = new Dictionary<string, SqsMessageAttribute>
        {
            ["TraceId"] = new() { DataType = "String", StringValue = "abc" },
        };

        // 0 body + 7 (name) + 6 (type "String") + 3 (value)
        Assert.Equal(0 + 7 + 6 + 3, SendMessageHandlers.ComputeWireSize(0, attrs));
    }

    [Fact]
    public void Binary_attribute_uses_raw_byte_length_for_value()
    {
        var blob = new byte[100];
        var attrs = new Dictionary<string, SqsMessageAttribute>
        {
            ["Bin"] = new() { DataType = "Binary", BinaryValue = blob },
        };

        // 10 body + 3 (name) + 6 (type "Binary") + 100 (raw bytes)
        Assert.Equal(10 + 3 + 6 + 100, SendMessageHandlers.ComputeWireSize(10, attrs));
    }

    [Fact]
    public void Multibyte_name_and_value_count_utf8_bytes_not_chars()
    {
        var attrs = new Dictionary<string, SqsMessageAttribute>
        {
            // 'á' is 2 UTF-8 bytes, '😀' is 4 UTF-8 bytes.
            ["áb"] = new() { DataType = "String", StringValue = "😀" },
        };

        // (2+1) name + 6 type + 4 value
        Assert.Equal(0 + 3 + 6 + 4, SendMessageHandlers.ComputeWireSize(0, attrs));
    }

    [Fact]
    public void Body_just_at_limit_with_attributes_exceeds_cap()
    {
        // Body alone equal to the 1 MiB cap, plus any attribute, must push
        // the wire size past the cap — proving attributes are counted.
        var attrs = new Dictionary<string, SqsMessageAttribute>
        {
            ["X"] = new() { DataType = "String", StringValue = "y" },
        };

        var size = SendMessageHandlers.ComputeWireSize(SendMessageHandlers.MaxBodyBytes, attrs);

        Assert.True(size > SendMessageHandlers.MaxBodyBytes);
    }

    [Fact]
    public void Sum_of_multiple_attributes_is_additive()
    {
        var attrs = new Dictionary<string, SqsMessageAttribute>
        {
            ["a"] = new() { DataType = "String", StringValue = "1" },
            ["b"] = new() { DataType = "Number", StringValue = "42" },
        };

        // a: 1+6+1 = 8 ; b: 1+6+2 = 9 ; body 100 => 117
        Assert.Equal(100 + 8 + 9, SendMessageHandlers.ComputeWireSize(100, attrs));
    }

    [Fact]
    public void Empty_attribute_dictionary_counts_only_body()
    {
        var size = SendMessageHandlers.ComputeWireSize(
            512, new Dictionary<string, SqsMessageAttribute>());
        Assert.Equal(512, size);
    }
}
