using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public sealed class CosmosBinaryDecoderTests
{
    [Fact]
    public void IsBinary_checks_first_byte_only()
    {
        Assert.True(CosmosBinaryDecoder.IsBinary([0x80, 0xE8]));
        Assert.False(CosmosBinaryDecoder.IsBinary("{\"id\":\"x\"}"u8));
        Assert.False(CosmosBinaryDecoder.IsBinary([]));
    }

    [Fact]
    public void Decode_supports_system_strings_inline_strings_and_string_references()
    {
        byte[] body =
        [
            0x80,
            0xED, 0x0C, 0x02,
            0x2C,
            0x83, (byte)'a', (byte)'b', (byte)'c',
            0x84, (byte)'c', (byte)'o', (byte)'p', (byte)'y',
            0xC3, 0x05,
        ];

        Assert.Equal("{\"id\":\"abc\",\"copy\":\"abc\"}", Decode(body));
    }

    [Fact]
    public void Decode_supports_user_string_dictionary_markers_when_dictionary_is_supplied()
    {
        byte[] body = [0x80, 0xE9, 0x40, 0x60, 0x00];
        string?[] dictionary = new string?[33];
        dictionary[0] = "userName";
        dictionary[32] = "value-from-dictionary";

        using var output = new PooledByteBufferWriter(128);
        CosmosBinaryDecoder.Decode(body, output, dictionary);

        Assert.Equal("{\"userName\":\"value-from-dictionary\"}", Encoding.UTF8.GetString(output.WrittenMemory.Span));
    }

    [Fact]
    public void Decode_reports_missing_external_user_string_dictionary_as_json_error()
    {
        byte[] body = [0x80, 0xE9, 0x40, 0x60, 0x00];

        Assert.IsType<JsonException>(Record.Exception(() => Decode(body)));
    }

    [Fact]
    public void Decode_supports_uniform_number_arrays()
    {
        Assert.Equal("[-1,0,1]", Decode([0x80, 0xF0, 0xD8, 0x03, 0xFF, 0x00, 0x01]));

        byte[] c2 =
        [
            0x80, 0xF1, 0xD9, 0x02, 0x00,
            0x01, 0x00,
            0xFE, 0xFF,
        ];
        Assert.Equal("[1,-2]", Decode(c2));

        byte[] nestedC1 = [0x80, 0xF2, 0xF0, 0xD7, 0x02, 0x02, 0x01, 0x02, 0x03, 0x04];
        Assert.Equal("[[1,2],[3,4]]", Decode(nestedC1));

        byte[] nestedC2 =
        [
            0x80, 0xF3, 0xF1, 0xD9, 0x02, 0x00, 0x02, 0x00,
            0x01, 0x00,
            0x02, 0x00,
            0x03, 0x00,
            0x04, 0x00,
        ];
        Assert.Equal("[[1,2],[3,4]]", Decode(nestedC2));
    }

    [Fact]
    public void Decode_supports_binary_blob_markers_as_base64_json_strings()
    {
        Assert.Equal("\"AQID\"", Decode([0x80, 0xDD, 0x03, 0x01, 0x02, 0x03]));

        byte[] twoByteLength = [0x80, 0xDE, 0x03, 0x00, 0x04, 0x05, 0x06];
        Assert.Equal("\"BAUG\"", Decode(twoByteLength));

        byte[] fourByteLength = [0x80, 0xDF, 0x03, 0x00, 0x00, 0x00, 0x07, 0x08, 0x09];
        Assert.Equal("\"BwgJ\"", Decode(fourByteLength));
    }

    [Fact]
    public void Decode_supports_nested_document_and_query_page_shapes()
    {
        const string json = "{\"_rid\":\"x\",\"Documents\":[{\"id\":\"1\",\"tags\":{\"_a2a:BS\":[\"AQI=\",\"AwQ=\"]},\"scores\":[1,2,3]}],\"_count\":1}";

        AssertJsonEqual(json, Decode(TestBinaryJsonEncoder.Encode(json)));
    }

    [Fact]
    public async Task Binary_decoded_get_item_output_matches_text_path_output()
    {
        const string itemJson = "{\"pk\":{\"S\":\"customer-1\"},\"payload\":{\"B\":\"AQID\"},\"blobs\":{\"BS\":[\"BAU=\",\"Bgc=\"]},\"nums\":{\"NS\":[\"1\",\"2.5\"]}}";
        using var item = JsonDocument.Parse(itemJson);
        string cosmosDoc = InferredAttributeStorage.BuildCosmosDocument("order-1", "customer-1", item.RootElement);
        byte[] binaryDoc = TestBinaryJsonEncoder.Encode(cosmosDoc);

        string textResponse = await WriteGetItemAsync(Encoding.UTF8.GetBytes(cosmosDoc));
        string binaryResponse = await WriteGetItemAsync(binaryDoc);

        Assert.Equal(textResponse, binaryResponse);
    }

    [Fact]
    public void Decode_rejects_excessive_nesting_without_stack_overflow()
    {
        // A long chain of single-item array (0xE1) markers must surface as a
        // JsonException from the depth guard, never an uncatchable
        // StackOverflowException that would terminate the sidecar.
        var body = new byte[1 + 5000 + 1];
        body[0] = 0x80;
        for (int i = 1; i <= 5000; i++) body[i] = 0xE1;
        body[^1] = 0x00; // innermost scalar

        Assert.IsType<JsonException>(Record.Exception(() => Decode(body)));
    }

    [Fact]
    public void Decode_rejects_oversized_length_as_json_error_not_overflow()
    {
        // ArrL4 (0xE4) declaring 0xFFFFFFFF payload bytes. Must be a
        // JsonException (malformed body), not an OverflowException leaking from
        // a checked int cast.
        byte[] body = [0x80, 0xE4, 0xFF, 0xFF, 0xFF, 0xFF];

        Assert.IsType<JsonException>(Record.Exception(() => Decode(body)));
    }

    [Fact]
    public void Decode_rejects_child_value_exceeding_container_bounds()
    {
        // ArrL1 (0xE2) declaring a 1-byte payload but whose single element is a
        // 2-byte fixed number (0xC8 + value). The element must not be allowed to
        // read past the container's declared payload.
        byte[] body = [0x80, 0xE2, 0x01, 0xC8, 0x05];

        Assert.IsType<JsonException>(Record.Exception(() => Decode(body)));
    }

    [Fact]
    public void Decode_rejects_truncated_body()
    {
        // ArrL1 (0xE2) declaring 4 payload bytes but the buffer ends early.
        byte[] body = [0x80, 0xE2, 0x04, 0xC8];

        Assert.IsType<JsonException>(Record.Exception(() => Decode(body)));
    }

    private static string Decode(byte[] body)
    {
        using var output = new PooledByteBufferWriter(256);
        CosmosBinaryDecoder.Decode(body, output);
        return Encoding.UTF8.GetString(output.WrittenMemory.Span);
    }

    private static async Task<string> WriteGetItemAsync(byte[] cosmosBody)
    {
        var ctx = new DefaultHttpContext();
        var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;

        await using var stream = new MemoryStream(cosmosBody);
        await CosmosOpsShared.WriteGetItemEnvelopeAsync(ctx, stream, CancellationToken.None);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDoc = JsonDocument.Parse(expected);
        using var actualDoc = JsonDocument.Parse(actual);
        Assert.Equal(expectedDoc.RootElement.GetRawText(), actualDoc.RootElement.GetRawText());
    }

    private static class TestBinaryJsonEncoder
    {
        public static byte[] Encode(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var writer = new ArrayBufferWriter<byte>();
            writer.GetSpan(1)[0] = 0x80;
            writer.Advance(1);
            WriteElement(doc.RootElement, writer);
            return writer.WrittenSpan.ToArray();
        }

        private static void WriteElement(JsonElement element, ArrayBufferWriter<byte> writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject(element, writer);
                    break;
                case JsonValueKind.Array:
                    WriteArray(element, writer);
                    break;
                case JsonValueKind.String:
                    WriteString(element.GetString()!, writer);
                    break;
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue))
                    {
                        WriteByte(writer, 0xDA);
                        Span<byte> bytes = writer.GetSpan(4);
                        BinaryPrimitives.WriteInt32LittleEndian(bytes, intValue);
                        writer.Advance(4);
                    }
                    else
                    {
                        WriteByte(writer, 0xCC);
                        Span<byte> bytes = writer.GetSpan(8);
                        BinaryPrimitives.WriteDoubleLittleEndian(bytes, element.GetDouble());
                        writer.Advance(8);
                    }
                    break;
                case JsonValueKind.True:
                    WriteByte(writer, 0xD2);
                    break;
                case JsonValueKind.False:
                    WriteByte(writer, 0xD1);
                    break;
                case JsonValueKind.Null:
                    WriteByte(writer, 0xD0);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported test JSON token.");
            }
        }

        private static void WriteObject(JsonElement element, ArrayBufferWriter<byte> writer)
        {
            var payload = new ArrayBufferWriter<byte>();
            int count = 0;
            foreach (var property in element.EnumerateObject())
            {
                WriteString(property.Name, payload);
                WriteElement(property.Value, payload);
                count++;
            }
            WriteContainer(writer, payload.WrittenSpan, count, obj: true);
        }

        private static void WriteArray(JsonElement element, ArrayBufferWriter<byte> writer)
        {
            var payload = new ArrayBufferWriter<byte>();
            int count = 0;
            foreach (var item in element.EnumerateArray())
            {
                WriteElement(item, payload);
                count++;
            }
            WriteContainer(writer, payload.WrittenSpan, count, obj: false);
        }

        private static void WriteContainer(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> payload, int count, bool obj)
        {
            if (payload.Length <= byte.MaxValue && count <= byte.MaxValue)
            {
                WriteByte(writer, obj ? (byte)0xED : (byte)0xE5);
                WriteByte(writer, (byte)payload.Length);
                WriteByte(writer, (byte)count);
            }
            else
            {
                WriteByte(writer, obj ? (byte)0xEE : (byte)0xE6);
                Span<byte> prefix = writer.GetSpan(4);
                BinaryPrimitives.WriteUInt16LittleEndian(prefix, (ushort)payload.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(prefix.Slice(2), (ushort)count);
                writer.Advance(4);
            }
            writer.Write(payload);
        }

        private static void WriteString(string value, ArrayBufferWriter<byte> writer)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            if (utf8.Length < 64)
            {
                WriteByte(writer, (byte)(0x80 + utf8.Length));
            }
            else
            {
                WriteByte(writer, 0xC1);
                Span<byte> length = writer.GetSpan(2);
                BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)utf8.Length);
                writer.Advance(2);
            }
            writer.Write(utf8);
        }

        private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
        {
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }
    }
}
