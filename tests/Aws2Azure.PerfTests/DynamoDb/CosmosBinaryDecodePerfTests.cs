using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class CosmosBinaryDecodePerfTests
{
    [SkippableFact]
    public async Task CosmosBinary_synthetic_query_page_decode_cost()
    {
        Skip.IfNot(PerfGate.Enabled, "AWS2AZURE_PERF=1 not set.");

        string textJson = BuildSyntheticQueryPage(documentCount: 50, payloadBytes: 512);
        byte[] textBody = Encoding.UTF8.GetBytes(textJson);
        byte[] binaryBody = TestBinaryJsonEncoder.Encode(textJson);
        string notes = FormattableString.Invariant(
            $"synthetic Cosmos query page; text={textBody.Length} B, CosmosBinary={binaryBody.Length} B, emulator-unverified decode CPU only");

        var textResult = await PerfRunner.RunAsync(
            scenario: "dynamodb.CosmosJsonParse (synthetic page)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(10),
            warmup: TimeSpan.FromSeconds(1),
            action: (_, _) =>
            {
                using var doc = JsonDocument.Parse(textBody);
                _ = doc.RootElement.GetProperty("Documents").GetArrayLength();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        PerfReport.Append(textResult, notes);
        textResult.AssertHealthy();
        textResult.AssertNoRegression();

        var binaryResult = await PerfRunner.RunAsync(
            scenario: "dynamodb.CosmosBinaryDecode (synthetic page)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(10),
            warmup: TimeSpan.FromSeconds(1),
            action: (_, _) =>
            {
                using var decoded = new PooledByteBufferWriter(textBody.Length);
                CosmosBinaryDecoder.Decode(binaryBody, decoded);
                using var doc = JsonDocument.Parse(decoded.WrittenMemory);
                _ = doc.RootElement.GetProperty("Documents").GetArrayLength();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        PerfReport.Append(binaryResult, notes);
        binaryResult.AssertHealthy();
        binaryResult.AssertNoRegression();
    }

    private static string BuildSyntheticQueryPage(int documentCount, int payloadBytes)
    {
        var payload = new string('x', payloadBytes);
        var sb = new StringBuilder();
        sb.Append("{\"_rid\":\"synthetic\",\"Documents\":[");
        for (int i = 0; i < documentCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"item-").Append(i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\",\"_a2a\":\"item\",\"pk\":\"p")
                .Append((i % 10).ToString("D2", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\",\"sk\":\"s")
                .Append(i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\",\"payload\":\"")
                .Append(payload)
                .Append("\",\"n\":")
                .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('}');
        }
        sb.Append("],\"_count\":").Append(documentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('}');
        return sb.ToString();
    }

    private static class TestBinaryJsonEncoder
    {
        public static byte[] Encode(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var writer = new ArrayBufferWriter<byte>();
            WriteByte(writer, 0x80);
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
                    WriteByte(writer, 0xDA);
                    Span<byte> number = writer.GetSpan(4);
                    BinaryPrimitives.WriteInt32LittleEndian(number, element.GetInt32());
                    writer.Advance(4);
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
            WriteByte(writer, obj ? (byte)0xEE : (byte)0xE6);
            Span<byte> prefix = writer.GetSpan(4);
            BinaryPrimitives.WriteUInt16LittleEndian(prefix, checked((ushort)payload.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(prefix.Slice(2), checked((ushort)count));
            writer.Advance(4);
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
                BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)utf8.Length));
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
