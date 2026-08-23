using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
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

    // Canonicalizes the parsed UpdateExpression AST (SET/REMOVE only — the
    // only clauses SprocEligibility.TryValidateTransactionUpdate admits) into
    // the idempotency fingerprint hash. Paths use their display form (stable,
    // already alias-resolved) and literal operands reuse
    // WriteCanonicalAttributeValue so a JSON-encoding-order variance in
    // ExpressionAttributeValues does not change the fingerprint.
    private static void WriteCanonicalUpdate(
        CanonicalFingerprintWriter writer,
        UpdateExpressionAst ast)
    {
        writer.WriteByte((byte)'{');
        var wrote = false;
        if (ast.Set is { Actions.Count: > 0 })
        {
            writer.WriteRaw("\"set\":["u8);
            for (var i = 0; i < ast.Set.Actions.Count; i++)
            {
                if (i > 0)
                {
                    writer.WriteByte((byte)',');
                }
                var action = ast.Set.Actions[i];
                writer.WriteByte((byte)'{');
                writer.WriteJsonString("p");
                writer.WriteByte((byte)':');
                writer.WriteJsonString(action.Path.Display);
                writer.WriteByte((byte)',');
                writer.WriteJsonString("v");
                writer.WriteByte((byte)':');
                WriteCanonicalUpdateOperand(writer, action.Value);
                writer.WriteByte((byte)'}');
            }
            writer.WriteByte((byte)']');
            wrote = true;
        }
        if (ast.Remove is { Paths.Count: > 0 })
        {
            if (wrote)
            {
                writer.WriteByte((byte)',');
            }
            writer.WriteRaw("\"remove\":["u8);
            for (var i = 0; i < ast.Remove.Paths.Count; i++)
            {
                if (i > 0)
                {
                    writer.WriteByte((byte)',');
                }
                writer.WriteJsonString(ast.Remove.Paths[i].Display);
            }
            writer.WriteByte((byte)']');
        }
        writer.WriteByte((byte)'}');
    }

    private static void WriteCanonicalUpdateOperand(
        CanonicalFingerprintWriter writer,
        ValueOperand operand)
    {
        switch (operand)
        {
            case ValueRefOperand valueRef:
                writer.WriteByte((byte)'{');
                writer.WriteJsonString("lit");
                writer.WriteByte((byte)':');
                WriteCanonicalAttributeValue(writer, valueRef.Value);
                writer.WriteByte((byte)'}');
                break;
            case PathOperand pathOperand:
                writer.WriteByte((byte)'{');
                writer.WriteJsonString("path");
                writer.WriteByte((byte)':');
                writer.WriteJsonString(pathOperand.Path.Display);
                writer.WriteByte((byte)'}');
                break;
            case ArithmeticOperand arithmetic:
                writer.WriteByte((byte)'{');
                writer.WriteJsonString("op");
                writer.WriteByte((byte)':');
                writer.WriteJsonString(
                    arithmetic.Op == ArithmeticOp.Add ? "+" : "-");
                writer.WriteByte((byte)',');
                writer.WriteJsonString("l");
                writer.WriteByte((byte)':');
                WriteCanonicalUpdateOperand(writer, arithmetic.Left);
                writer.WriteByte((byte)',');
                writer.WriteJsonString("r");
                writer.WriteByte((byte)':');
                WriteCanonicalUpdateOperand(writer, arithmetic.Right);
                writer.WriteByte((byte)'}');
                break;
            case IfNotExistsOperand ifNotExists:
                writer.WriteByte((byte)'{');
                writer.WriteJsonString("ifne");
                writer.WriteByte((byte)':');
                writer.WriteJsonString(ifNotExists.Path.Display);
                writer.WriteByte((byte)',');
                writer.WriteJsonString("f");
                writer.WriteByte((byte)':');
                WriteCanonicalUpdateOperand(writer, ifNotExists.Fallback);
                writer.WriteByte((byte)'}');
                break;
            case ListAppendOperand listAppend:
                writer.WriteByte((byte)'{');
                writer.WriteJsonString("lap");
                writer.WriteByte((byte)':');
                writer.WriteByte((byte)'[');
                WriteCanonicalUpdateOperand(writer, listAppend.Left);
                writer.WriteByte((byte)',');
                WriteCanonicalUpdateOperand(writer, listAppend.Right);
                writer.WriteByte((byte)']');
                writer.WriteByte((byte)'}');
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported update operand type '{operand.GetType().Name}'.");
        }
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

        public IncrementalHash Hash => _hash;

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
