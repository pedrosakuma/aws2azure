using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class TransactWriteItemsHandler
{
    private const string FingerprintVersion = "a2a-ddb-transact-write-v1";

    private static bool TryValidateClientRequestToken(
        string? token,
        out string error)
    {
        if (token is null)
        {
            error = string.Empty;
            return true;
        }
        if (token.Length is < 1 or > 36)
        {
            error =
                "ClientRequestToken must have a length between 1 and 36 characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static string BuildIdempotencyRecordId(string token)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(token))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return DynamoDbPersistedFormatContract.TransactionIdempotencyRecordIdPrefix
            + encoded;
    }

    private static string ComputeRequestFingerprint(
        string tableName,
        string partitionKey,
        byte[] body,
        PreparedRequestOp[] operations)
    {
        using var writer = new CanonicalFingerprintWriter();
        writer.WriteRaw("{\"version\":"u8);
        writer.WriteJsonString(FingerprintVersion);
        writer.WriteRaw(",\"table\":"u8);
        writer.WriteJsonString(tableName);
        writer.WriteRaw(",\"partition\":"u8);
        writer.WriteJsonString(partitionKey);
        writer.WriteRaw(",\"operations\":["u8);
        for (var index = 0; index < operations.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }

            var operation = operations[index];
            writer.WriteRaw("{\"type\":"u8);
            writer.WriteJsonString(
                operation.Kind switch
                {
                    OpKind.Put => "PUT",
                    OpKind.Delete => "DELETE",
                    _ => "CHECK",
                });
            writer.WriteRaw(",\"id\":"u8);
            writer.WriteJsonString(operation.Id);
            if (operation.Kind == OpKind.Put)
            {
                writer.WriteRaw(",\"item\":"u8);
                using var operationDocument = JsonDocument.Parse(
                    body.AsMemory(
                        operation.Range.Start,
                        operation.Range.Length),
                    TransactItemParseOptions);
                WriteCanonicalAttributeMap(
                    writer,
                    operationDocument.RootElement.GetProperty("Item"));
            }

            writer.WriteRaw(",\"condition\":"u8);
            if (operation.ConditionJson is null)
            {
                writer.WriteRaw("null"u8);
            }
            else
            {
                writer.WriteRawUtf8(operation.ConditionJson);
            }
            writer.WriteByte((byte)'}');
        }
        writer.WriteRaw("]}"u8);
        return writer.Complete();
    }

    private static void WriteCanonicalAttributeMap(
        CanonicalFingerprintWriter writer,
        JsonElement map)
    {
        var properties = new List<JsonProperty>();
        foreach (var property in map.EnumerateObject())
        {
            properties.Add(property);
        }
        properties.Sort(static (left, right) =>
            string.CompareOrdinal(left.Name, right.Name));

        writer.WriteByte((byte)'{');
        for (var index = 0; index < properties.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }
            var property = properties[index];
            writer.WriteJsonString(property.Name);
            writer.WriteByte((byte)':');
            WriteCanonicalAttributeValue(writer, property.Value);
        }
        writer.WriteByte((byte)'}');
    }

    private static void WriteCanonicalAttributeValue(
        CanonicalFingerprintWriter writer,
        JsonElement attribute)
    {
        if (!ParsedAttributeValue.TryParse(attribute, out var parsed))
        {
            throw new ArgumentException(
                "AttributeValue must contain exactly one supported type tag.");
        }

        writer.WriteByte((byte)'{');
        writer.WriteJsonString(parsed.TypeTag);
        writer.WriteByte((byte)':');
        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                writer.WriteJsonString(parsed.Value.GetString()!);
                break;
            case AttributeValueTypes.Number:
                if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                        parsed.Value.GetString() ?? string.Empty,
                        out var canonical,
                        out _,
                        out var numberError))
                {
                    throw new ArgumentException(numberError);
                }
                writer.WriteJsonString(canonical);
                break;
            case AttributeValueTypes.Binary:
                writer.WriteJsonString(
                    Convert.ToBase64String(
                        Convert.FromBase64String(
                            parsed.Value.GetString() ?? string.Empty)));
                break;
            case AttributeValueTypes.Bool:
                writer.WriteRaw(
                    parsed.Value.GetBoolean() ? "true"u8 : "false"u8);
                break;
            case AttributeValueTypes.Null:
                writer.WriteRaw("true"u8);
                break;
            case AttributeValueTypes.Map:
                WriteCanonicalAttributeMap(writer, parsed.Value);
                break;
            case AttributeValueTypes.List:
                writer.WriteByte((byte)'[');
                var listIndex = 0;
                foreach (var element in parsed.Value.EnumerateArray())
                {
                    if (listIndex++ > 0)
                    {
                        writer.WriteByte((byte)',');
                    }
                    WriteCanonicalAttributeValue(writer, element);
                }
                writer.WriteByte((byte)']');
                break;
            case AttributeValueTypes.StringSet:
                WriteSortedStringSet(writer, parsed.Value);
                break;
            case AttributeValueTypes.NumberSet:
                WriteSortedNumberSet(writer, parsed.Value);
                break;
            case AttributeValueTypes.BinarySet:
                WriteSortedBinarySet(writer, parsed.Value);
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported AttributeValue type '{parsed.TypeTag}'.");
        }
        writer.WriteByte((byte)'}');
    }

    private static void WriteSortedStringSet(
        CanonicalFingerprintWriter writer,
        JsonElement values)
    {
        var sorted = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            sorted.Add(value.GetString() ?? string.Empty);
        }
        sorted.Sort(StringComparer.Ordinal);
        writer.WriteByte((byte)'[');
        for (var index = 0; index < sorted.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }
            writer.WriteJsonString(sorted[index]);
        }
        writer.WriteByte((byte)']');
    }

    private static void WriteSortedNumberSet(
        CanonicalFingerprintWriter writer,
        JsonElement values)
    {
        var sorted = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                    value.GetString() ?? string.Empty,
                    out var canonical,
                    out _,
                    out var error))
            {
                throw new ArgumentException(error);
            }
            sorted.Add(canonical);
        }
        sorted.Sort(StringComparer.Ordinal);
        writer.WriteByte((byte)'[');
        for (var index = 0; index < sorted.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }
            writer.WriteJsonString(sorted[index]);
        }
        writer.WriteByte((byte)']');
    }

    private static void WriteSortedBinarySet(
        CanonicalFingerprintWriter writer,
        JsonElement values)
    {
        var sorted = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            sorted.Add(
                Convert.ToBase64String(
                    Convert.FromBase64String(
                        value.GetString() ?? string.Empty)));
        }
        sorted.Sort(StringComparer.Ordinal);
        writer.WriteByte((byte)'[');
        for (var index = 0; index < sorted.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }
            writer.WriteJsonString(sorted[index]);
        }
        writer.WriteByte((byte)']');
    }

    private sealed class CanonicalFingerprintWriter : IDisposable
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHashTextWriter _textWriter;

        public CanonicalFingerprintWriter()
        {
            _textWriter = new IncrementalHashTextWriter(_hash);
        }

        public void WriteRaw(ReadOnlySpan<byte> value)
            => _hash.AppendData(value);

        public void WriteByte(byte value)
        {
            Span<byte> buffer = stackalloc byte[1];
            buffer[0] = value;
            _hash.AppendData(buffer);
        }

        public void WriteJsonString(string value)
        {
            WriteByte((byte)'"');
            JavaScriptEncoder.Default.Encode(_textWriter, value);
            _textWriter.CompleteSegment();
            WriteByte((byte)'"');
        }

        public void WriteRawUtf8(string value)
        {
            _textWriter.Write(value);
            _textWriter.CompleteSegment();
        }

        public string Complete()
        {
            _textWriter.CompleteSegment();
            var digest = _hash.GetHashAndReset();
            return Convert.ToHexStringLower(digest);
        }

        public void Dispose()
        {
            _textWriter.Dispose();
            _hash.Dispose();
        }
    }

    private sealed class IncrementalHashTextWriter : TextWriter
    {
        private readonly IncrementalHash _hash;
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1024);

        public IncrementalHashTextWriter(IncrementalHash hash)
        {
            _hash = hash;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            Span<char> character = stackalloc char[1];
            character[0] = value;
            Write(character);
        }

        public override void Write(char[] buffer, int index, int count)
            => Write(buffer.AsSpan(index, count));

        public override void Write(string? value)
        {
            if (value is not null)
            {
                Write(value.AsSpan());
            }
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            while (!buffer.IsEmpty)
            {
                _encoder.Convert(
                    buffer,
                    _buffer,
                    flush: false,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                if (bytesUsed > 0)
                {
                    _hash.AppendData(_buffer.AsSpan(0, bytesUsed));
                }
                buffer = buffer[charsUsed..];
            }
        }

        public void CompleteSegment()
        {
            _encoder.Convert(
                ReadOnlySpan<char>.Empty,
                _buffer,
                flush: true,
                out _,
                out var bytesUsed,
                out _);
            if (bytesUsed > 0)
            {
                _hash.AppendData(_buffer.AsSpan(0, bytesUsed));
            }
            _encoder.Reset();
        }

        protected override void Dispose(bool disposing)
        {
            if (_buffer.Length != 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = [];
            }
            base.Dispose(disposing);
        }
    }
}
