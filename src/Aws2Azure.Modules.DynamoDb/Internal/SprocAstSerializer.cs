using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Serializes the C# condition/update AST into canonical JSON that the atomic
/// stored procedures interpret.
/// </summary>
internal static class SprocAstSerializer
{
    private static readonly JavaScriptEncoder JsonEncoder =
        JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static void WriteCondition(
        IBufferWriter<byte> output,
        ConditionNode node,
        IncrementalHash? fingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(node);
        using var writer = new CanonicalJsonWriter(output, fingerprint);
        WriteCondition(writer, node);
    }

    public static void WriteUpdate(
        IBufferWriter<byte> output,
        UpdateExpressionAst ast)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ast);
        using var writer = new CanonicalJsonWriter(output, fingerprint: null);
        writer.WriteByte((byte)'{');
        var wroteProperty = false;

        if (ast.Set is { Actions.Count: > 0 })
        {
            writer.WriteRaw("\"set\":["u8);
            for (var index = 0; index < ast.Set.Actions.Count; index++)
            {
                if (index > 0)
                {
                    writer.WriteByte((byte)',');
                }
                WriteSetAction(writer, ast.Set.Actions[index]);
            }
            writer.WriteByte((byte)']');
            wroteProperty = true;
        }

        if (ast.Remove is { Paths.Count: > 0 })
        {
            WritePropertySeparator(writer, wroteProperty);
            writer.WriteRaw("\"remove\":["u8);
            for (var index = 0; index < ast.Remove.Paths.Count; index++)
            {
                if (index > 0)
                {
                    writer.WriteByte((byte)',');
                }
                writer.WriteJsonString(PathToString(ast.Remove.Paths[index]));
            }
            writer.WriteByte((byte)']');
            wroteProperty = true;
        }

        if (ast.Add is { Actions.Count: > 0 })
        {
            WritePropertySeparator(writer, wroteProperty);
            writer.WriteRaw("\"add\":["u8);
            for (var index = 0; index < ast.Add.Actions.Count; index++)
            {
                if (index > 0)
                {
                    writer.WriteByte((byte)',');
                }
                WriteAddAction(writer, ast.Add.Actions[index]);
            }
            writer.WriteByte((byte)']');
            wroteProperty = true;
        }

        if (ast.Delete is { Actions.Count: > 0 })
        {
            WritePropertySeparator(writer, wroteProperty);
            writer.WriteRaw("\"delete\":["u8);
            for (var index = 0; index < ast.Delete.Actions.Count; index++)
            {
                if (index > 0)
                {
                    writer.WriteByte((byte)',');
                }
                WriteDeleteAction(writer, ast.Delete.Actions[index]);
            }
            writer.WriteByte((byte)']');
        }

        writer.WriteByte((byte)'}');
    }

    private static void WritePropertySeparator(
        CanonicalJsonWriter writer,
        bool wroteProperty)
    {
        if (wroteProperty)
        {
            writer.WriteByte((byte)',');
        }
    }

    private static void WriteCondition(
        CanonicalJsonWriter writer,
        ConditionNode node)
    {
        switch (node)
        {
            case AndCondition and:
                writer.WriteRaw("{\"type\":\"AND\",\"left\":"u8);
                WriteCondition(writer, and.Left);
                writer.WriteRaw(",\"right\":"u8);
                WriteCondition(writer, and.Right);
                writer.WriteByte((byte)'}');
                break;

            case OrCondition or:
                writer.WriteRaw("{\"type\":\"OR\",\"left\":"u8);
                WriteCondition(writer, or.Left);
                writer.WriteRaw(",\"right\":"u8);
                WriteCondition(writer, or.Right);
                writer.WriteByte((byte)'}');
                break;

            case NotCondition not:
                writer.WriteRaw("{\"type\":\"NOT\",\"operand\":"u8);
                WriteCondition(writer, not.Inner);
                writer.WriteByte((byte)'}');
                break;

            case AttributeExistsCondition exists:
                writer.WriteRaw("{\"type\":\"ATTR_EXISTS\",\"attr\":"u8);
                writer.WriteJsonString(PathToString(exists.Path));
                writer.WriteByte((byte)'}');
                break;

            case AttributeNotExistsCondition notExists:
                writer.WriteRaw("{\"type\":\"ATTR_NOT_EXISTS\",\"attr\":"u8);
                writer.WriteJsonString(PathToString(notExists.Path));
                writer.WriteByte((byte)'}');
                break;

            case AttributeTypeCondition attributeType:
                writer.WriteRaw("{\"type\":\"ATTR_TYPE\",\"attr\":"u8);
                writer.WriteJsonString(PathToString(attributeType.Path));
                writer.WriteRaw(",\"attrType\":"u8);
                WriteValue(writer, attributeType.TypeTag.Value);
                writer.WriteByte((byte)'}');
                break;

            case BeginsWithCondition beginsWith:
                writer.WriteRaw("{\"type\":\"BEGINS_WITH\",\"attr\":"u8);
                WriteOperand(writer, beginsWith.Path);
                writer.WriteRaw(",\"prefix\":"u8);
                WriteOperand(writer, beginsWith.Prefix);
                writer.WriteByte((byte)'}');
                break;

            case ContainsCondition contains:
                writer.WriteRaw("{\"type\":\"CONTAINS\",\"attr\":"u8);
                WriteOperand(writer, contains.Container);
                writer.WriteRaw(",\"value\":"u8);
                WriteOperand(writer, contains.Item);
                writer.WriteByte((byte)'}');
                break;

            case CompareCondition compare:
                writer.WriteRaw("{\"type\":\"COMPARE\",\"attr\":"u8);
                WriteOperand(writer, compare.Left);
                writer.WriteRaw(",\"op\":"u8);
                writer.WriteJsonString(OpToString(compare.Op));
                writer.WriteRaw(",\"value\":"u8);
                WriteOperand(writer, compare.Right);
                writer.WriteByte((byte)'}');
                break;

            case BetweenCondition between:
                writer.WriteRaw("{\"type\":\"BETWEEN\",\"value\":"u8);
                WriteOperand(writer, between.Value);
                writer.WriteRaw(",\"low\":"u8);
                WriteOperand(writer, between.Lower);
                writer.WriteRaw(",\"high\":"u8);
                WriteOperand(writer, between.Upper);
                writer.WriteByte((byte)'}');
                break;

            case InCondition @in:
                writer.WriteRaw("{\"type\":\"IN\",\"attr\":"u8);
                WriteOperand(writer, @in.Value);
                writer.WriteRaw(",\"values\":["u8);
                for (var index = 0; index < @in.Set.Count; index++)
                {
                    if (index > 0)
                    {
                        writer.WriteByte((byte)',');
                    }
                    WriteOperand(writer, @in.Set[index]);
                }
                writer.WriteRaw("]}"u8);
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported condition AST node '{node.GetType().Name}'.");
        }
    }

    private static void WriteOperand(
        CanonicalJsonWriter writer,
        ConditionOperand operand)
    {
        switch (operand)
        {
            case ConditionPathOperand path:
                writer.WriteRaw("{\"path\":"u8);
                writer.WriteJsonString(PathToString(path.Path));
                writer.WriteByte((byte)'}');
                break;
            case ConditionValueOperand value:
                WriteValue(writer, value.Value.Value);
                break;
            case SizeOperand size:
                writer.WriteRaw("{\"size\":"u8);
                writer.WriteJsonString(PathToString(size.Path));
                writer.WriteByte((byte)'}');
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported condition operand '{operand.GetType().Name}'.");
        }
    }

    private static void WriteSetAction(
        CanonicalJsonWriter writer,
        SetAction action)
    {
        writer.WriteRaw("{\"path\":"u8);
        writer.WriteJsonString(PathToString(action.Path));
        writer.WriteRaw(",\"value\":"u8);
        WriteValueOperand(writer, action.Value);
        writer.WriteByte((byte)'}');
    }

    private static void WriteAddAction(
        CanonicalJsonWriter writer,
        AddAction action)
    {
        writer.WriteRaw("{\"path\":"u8);
        writer.WriteJsonString(PathToString(action.Path));
        writer.WriteRaw(",\"value\":"u8);
        WriteValue(writer, action.Value.Value);
        writer.WriteByte((byte)'}');
    }

    private static void WriteDeleteAction(
        CanonicalJsonWriter writer,
        DeleteAction action)
    {
        writer.WriteRaw("{\"path\":"u8);
        writer.WriteJsonString(PathToString(action.Path));
        writer.WriteRaw(",\"value\":"u8);
        WriteValue(writer, action.Value.Value);
        writer.WriteByte((byte)'}');
    }

    private static void WriteValueOperand(
        CanonicalJsonWriter writer,
        ValueOperand operand)
    {
        switch (operand)
        {
            case ValueRefOperand value:
                writer.WriteRaw("{\"$k\":\"lit\",\"v\":"u8);
                WriteValue(writer, value.Value);
                writer.WriteByte((byte)'}');
                break;
            case PathOperand path:
                writer.WriteRaw("{\"$k\":\"path\",\"p\":"u8);
                writer.WriteJsonString(PathToString(path.Path));
                writer.WriteByte((byte)'}');
                break;
            case ArithmeticOperand arithmetic:
                writer.WriteRaw("{\"$k\":\"op\",\"o\":"u8);
                writer.WriteJsonString(
                    arithmetic.Op == ArithmeticOp.Add ? "+" : "-");
                writer.WriteRaw(",\"l\":"u8);
                WriteValueOperand(writer, arithmetic.Left);
                writer.WriteRaw(",\"r\":"u8);
                WriteValueOperand(writer, arithmetic.Right);
                writer.WriteByte((byte)'}');
                break;
            case IfNotExistsOperand ifNotExists:
                writer.WriteRaw("{\"$k\":\"ifne\",\"p\":"u8);
                writer.WriteJsonString(PathToString(ifNotExists.Path));
                writer.WriteRaw(",\"f\":"u8);
                WriteValueOperand(writer, ifNotExists.Fallback);
                writer.WriteByte((byte)'}');
                break;
            case ListAppendOperand listAppend:
                writer.WriteRaw("{\"$k\":\"lap\",\"l\":"u8);
                WriteValueOperand(writer, listAppend.Left);
                writer.WriteRaw(",\"r\":"u8);
                WriteValueOperand(writer, listAppend.Right);
                writer.WriteByte((byte)'}');
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported update operand '{operand.GetType().Name}'.");
        }
    }

    private static void WriteValue(
        CanonicalJsonWriter writer,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "S":
                        writer.WriteJsonString(property.Value.GetString() ?? "");
                        return;
                    case "N":
                        if (!InferredAttributeStorage.TryGetCanonicalBareJsonNumber(
                                property.Value.GetString(),
                                out var canonicalNumber))
                        {
                            throw new NotSupportedException(
                                "Stored-procedure operands cannot contain enveloped DynamoDB numbers.");
                        }
                        writer.WriteRawUtf8(canonicalNumber);
                        return;
                    case "BOOL":
                        writer.WriteRaw(
                            property.Value.GetBoolean() ? "true"u8 : "false"u8);
                        return;
                    case "NULL":
                        writer.WriteRaw("null"u8);
                        return;
                    case "B":
                        writer.WriteRaw("{\"_a2a:B\":"u8);
                        writer.WriteJsonString(property.Value.GetString() ?? "");
                        writer.WriteByte((byte)'}');
                        return;
                    case "M":
                        WriteMapValue(writer, property.Value);
                        return;
                    case "L":
                        WriteListValue(writer, property.Value);
                        return;
                    case "SS":
                        writer.WriteRaw("{\"_a2a:SS\":"u8);
                        WriteStringArray(writer, property.Value);
                        writer.WriteByte((byte)'}');
                        return;
                    case "NS":
                        writer.WriteRaw("{\"_a2a:NS\":"u8);
                        WriteStringArray(writer, property.Value);
                        writer.WriteByte((byte)'}');
                        return;
                    case "BS":
                        writer.WriteRaw("{\"_a2a:BS\":"u8);
                        WriteStringArray(writer, property.Value);
                        writer.WriteByte((byte)'}');
                        return;
                }
            }
        }

        WriteJsonElement(writer, value);
    }

    private static void WriteMapValue(
        CanonicalJsonWriter writer,
        JsonElement map)
    {
        writer.WriteByte((byte)'{');
        var index = 0;
        foreach (var property in map.EnumerateObject())
        {
            if (index++ > 0)
            {
                writer.WriteByte((byte)',');
            }
            writer.WriteJsonString(property.Name);
            writer.WriteByte((byte)':');
            WriteValue(writer, property.Value);
        }
        writer.WriteByte((byte)'}');
    }

    private static void WriteListValue(
        CanonicalJsonWriter writer,
        JsonElement list)
    {
        writer.WriteByte((byte)'[');
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (index++ > 0)
            {
                writer.WriteByte((byte)',');
            }
            WriteValue(writer, item);
        }
        writer.WriteByte((byte)']');
    }

    private static void WriteStringArray(
        CanonicalJsonWriter writer,
        JsonElement array)
    {
        writer.WriteByte((byte)'[');
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (index++ > 0)
            {
                writer.WriteByte((byte)',');
            }
            writer.WriteJsonString(item.GetString() ?? "");
        }
        writer.WriteByte((byte)']');
    }

    private static void WriteJsonElement(
        CanonicalJsonWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteByte((byte)'{');
                var propertyIndex = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (propertyIndex++ > 0)
                    {
                        writer.WriteByte((byte)',');
                    }
                    writer.WriteJsonString(property.Name);
                    writer.WriteByte((byte)':');
                    WriteJsonElement(writer, property.Value);
                }
                writer.WriteByte((byte)'}');
                break;
            case JsonValueKind.Array:
                writer.WriteByte((byte)'[');
                var itemIndex = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (itemIndex++ > 0)
                    {
                        writer.WriteByte((byte)',');
                    }
                    WriteJsonElement(writer, item);
                }
                writer.WriteByte((byte)']');
                break;
            case JsonValueKind.String:
                writer.WriteJsonString(value.GetString() ?? "");
                break;
            case JsonValueKind.Number:
                writer.WriteRawUtf8(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteRaw("true"u8);
                break;
            case JsonValueKind.False:
                writer.WriteRaw("false"u8);
                break;
            case JsonValueKind.Null:
                writer.WriteRaw("null"u8);
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported JSON value kind '{value.ValueKind}'.");
        }
    }

    private static string PathToString(DocumentPath path)
    {
        var builder = new StringBuilder();
        foreach (var segment in path.Segments)
        {
            switch (segment)
            {
                case AttributePathSegment attribute:
                    if (builder.Length > 0)
                    {
                        builder.Append('.');
                    }
                    builder.Append(attribute.Name);
                    break;
                case IndexPathSegment index:
                    builder.Append('[').Append(index.Index).Append(']');
                    break;
            }
        }
        return builder.ToString();
    }

    private static string OpToString(CompareOp op) => op switch
    {
        CompareOp.Equal => "=",
        CompareOp.NotEqual => "<>",
        CompareOp.Less => "<",
        CompareOp.LessEqual => "<=",
        CompareOp.Greater => ">",
        CompareOp.GreaterEqual => ">=",
        _ => "=",
    };

    private sealed class CanonicalJsonWriter : IDisposable
    {
        private readonly IBufferWriter<byte> _output;
        private readonly IncrementalHash? _fingerprint;
        private readonly BufferWriterTextWriter _textWriter;

        public CanonicalJsonWriter(
            IBufferWriter<byte> output,
            IncrementalHash? fingerprint)
        {
            _output = output;
            _fingerprint = fingerprint;
            _textWriter = new BufferWriterTextWriter(this);
        }

        public void WriteByte(byte value)
        {
            var span = _output.GetSpan(1);
            span[0] = value;
            _output.Advance(1);
            if (_fingerprint is not null)
            {
                Span<byte> fingerprintByte = stackalloc byte[1];
                fingerprintByte[0] = value;
                _fingerprint.AppendData(fingerprintByte);
            }
        }

        public void WriteRaw(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty)
            {
                return;
            }
            var span = _output.GetSpan(value.Length);
            value.CopyTo(span);
            _output.Advance(value.Length);
            _fingerprint?.AppendData(value);
        }

        public void WriteJsonString(string value)
        {
            WriteByte((byte)'"');
            JsonEncoder.Encode(_textWriter, value);
            _textWriter.CompleteSegment();
            WriteByte((byte)'"');
        }

        public void WriteRawUtf8(string value)
        {
            _textWriter.Write(value);
            _textWriter.CompleteSegment();
        }

        private void WriteEncoded(ReadOnlySpan<byte> value)
            => WriteRaw(value);

        public void Dispose()
            => _textWriter.Dispose();

        private sealed class BufferWriterTextWriter : TextWriter
        {
            private readonly CanonicalJsonWriter _owner;
            private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
            private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1024);

            public BufferWriterTextWriter(CanonicalJsonWriter owner)
            {
                _owner = owner;
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
                        _owner.WriteEncoded(_buffer.AsSpan(0, bytesUsed));
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
                    _owner.WriteEncoded(_buffer.AsSpan(0, bytesUsed));
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
}
