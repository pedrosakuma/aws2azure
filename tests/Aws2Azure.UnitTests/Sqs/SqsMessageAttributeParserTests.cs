using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class SqsMessageAttributeParserTests
{
    [Fact]
    public void Query_parses_string_attribute()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "foo",
            ["MessageAttribute.1.Value.DataType"] = "String",
            ["MessageAttribute.1.Value.StringValue"] = "bar",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.False(r.IsError);
        Assert.Single(r.Attributes!);
        Assert.Equal("String", r.Attributes!["foo"].DataType);
        Assert.Equal("bar", r.Attributes["foo"].StringValue);
    }

    [Fact]
    public void Query_parses_binary_attribute()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "payload",
            ["MessageAttribute.1.Value.DataType"] = "Binary",
            ["MessageAttribute.1.Value.BinaryValue"] = "aGVsbG8=", // "hello"
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.False(r.IsError);
        var attr = r.Attributes!["payload"];
        Assert.True(attr.IsBinary);
        Assert.Equal(5, attr.BinaryValue.Length);
    }

    [Fact]
    public void Query_rejects_reserved_name()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "AWS.Reserved",
            ["MessageAttribute.1.Value.DataType"] = "String",
            ["MessageAttribute.1.Value.StringValue"] = "x",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.True(r.IsError);
        Assert.Equal("InvalidParameterValue", r.ErrorCode);
    }

    [Fact]
    public void Query_rejects_invalid_base64_binary()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "p",
            ["MessageAttribute.1.Value.DataType"] = "Binary",
            ["MessageAttribute.1.Value.BinaryValue"] = "!!!not base64!!!",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.True(r.IsError);
    }

    [Fact]
    public void Query_rejects_unknown_data_type()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "x",
            ["MessageAttribute.1.Value.DataType"] = "Bogus",
            ["MessageAttribute.1.Value.StringValue"] = "1",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.True(r.IsError);
    }

    [Fact]
    public void Query_accepts_custom_string_suffix()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "ratio",
            ["MessageAttribute.1.Value.DataType"] = "Number.float",
            ["MessageAttribute.1.Value.StringValue"] = "3.14",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.False(r.IsError);
        Assert.Equal("Number.float", r.Attributes!["ratio"].DataType);
    }

    [Fact]
    public void Json_parses_mixed_attributes()
    {
        var json = """
        {
            "foo": {"DataType":"String","StringValue":"bar"},
            "n":   {"DataType":"Number","StringValue":"42"},
            "p":   {"DataType":"Binary","BinaryValue":"aGVsbG8="}
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var r = SqsMessageAttributeParser.FromJson(doc.RootElement);
        Assert.False(r.IsError);
        Assert.Equal(3, r.Attributes!.Count);
        Assert.True(r.Attributes["p"].IsBinary);
    }

    [Theory]
    [InlineData("aws.lowercase")]
    [InlineData("amazon.lowercase")]
    [InlineData("Aws.Mixed")]
    public void Query_rejects_reserved_prefix_case_insensitively(string name)
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = name,
            ["MessageAttribute.1.Value.DataType"] = "String",
            ["MessageAttribute.1.Value.StringValue"] = "x",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.True(r.IsError);
    }

    [Theory]
    [InlineData("String.")]                 // empty suffix
    [InlineData("String.bad,comma")]        // comma breaks Aws2Azure-AttrTypes header
    [InlineData("String.bad=equals")]       // equals is the k/v separator
    [InlineData("String.with\nnewline")]    // CR/LF would break the header
    public void Query_rejects_invalid_data_type_suffix(string dataType)
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "x",
            ["MessageAttribute.1.Value.DataType"] = dataType,
            ["MessageAttribute.1.Value.StringValue"] = "1",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.True(r.IsError);
    }

    [Fact]
    public void Query_accepts_long_but_valid_suffix()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "x",
            ["MessageAttribute.1.Value.DataType"] = "String." + new string('a', 249),
            ["MessageAttribute.1.Value.StringValue"] = "1",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.False(r.IsError);
    }

    [Fact]
    public void Query_rejects_oversized_suffix()
    {
        var parameters = new Dictionary<string, string>
        {
            ["MessageAttribute.1.Name"] = "x",
            ["MessageAttribute.1.Value.DataType"] = "String." + new string('a', 251),
            ["MessageAttribute.1.Value.StringValue"] = "1",
        };
        var r = SqsMessageAttributeParser.FromQuery(parameters);
        Assert.True(r.IsError);
    }
}
