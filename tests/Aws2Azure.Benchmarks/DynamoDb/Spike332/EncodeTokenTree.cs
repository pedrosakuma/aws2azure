using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike332;

/// <summary>
/// Spike #332 — a <b>pre-materialized</b> document token tree plus a text and a
/// binary encoder that both walk it, so the benchmark isolates the pure
/// <i>formatter</i> cost.
///
/// The earlier attempt drove the encoders straight off a <see cref="JsonElement"/>
/// and so paid <c>GetString()</c> / <c>GetBytes()</c> per property — a string
/// materialization tax that is identical for both formats but dominates the
/// timing and dilutes the text-vs-binary delta. Here every name and string value
/// is decoded to UTF-8 bytes <i>once</i> in setup; the hot path walks the tree
/// with zero allocation, so the difference measured is escaping / number
/// formatting / structural framing — the only things that actually differ
/// between text and CosmosBinary.
/// </summary>
internal abstract class Node
{
    public static Node FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return From(doc.RootElement);
    }

    private static Node From(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => new ObjNode(
            e.EnumerateObject().Select(p => (Encoding.UTF8.GetBytes(p.Name), From(p.Value))).ToArray()),
        JsonValueKind.Array => new ArrNode(e.EnumerateArray().Select(From).ToArray()),
        JsonValueKind.String => new StrNode(Encoding.UTF8.GetBytes(e.GetString()!)),
        JsonValueKind.Number => new IntNode(e.GetInt64()),
        JsonValueKind.True => BoolNode.True,
        JsonValueKind.False => BoolNode.False,
        JsonValueKind.Null => NullNode.Instance,
        _ => throw new NotSupportedException(),
    };
}

internal sealed class ObjNode((byte[] Name, Node Value)[] props) : Node
{
    public (byte[] Name, Node Value)[] Props { get; } = props;
}

internal sealed class ArrNode(Node[] items) : Node
{
    public Node[] Items { get; } = items;
}

internal sealed class StrNode(byte[] utf8) : Node
{
    public byte[] Utf8 { get; } = utf8;
}

internal sealed class IntNode(long value) : Node
{
    public long Value { get; } = value;
}

internal sealed class BoolNode(bool value) : Node
{
    public static readonly BoolNode True = new(true);
    public static readonly BoolNode False = new(false);
    public bool Value { get; } = value;
}

internal sealed class NullNode : Node
{
    public static readonly NullNode Instance = new();
}

/// <summary>Walks a <see cref="Node"/> tree emitting canonical UTF-8 JSON text
/// via <see cref="Utf8JsonWriter"/> — the optimal zero-materialization text
/// encoder (names/strings written from UTF-8 spans, integers from a parsed
/// <see cref="long"/>). Escaping is still performed by the writer, an intrinsic
/// text cost binary does not pay.</summary>
internal static class TextTokenEncoder
{
    public static void Write(Utf8JsonWriter w, Node n)
    {
        switch (n)
        {
            case ObjNode o:
                w.WriteStartObject();
                foreach (var (name, value) in o.Props)
                {
                    w.WritePropertyName(name);
                    Write(w, value);
                }

                w.WriteEndObject();
                break;
            case ArrNode a:
                w.WriteStartArray();
                foreach (var item in a.Items)
                {
                    Write(w, item);
                }

                w.WriteEndArray();
                break;
            case StrNode s:
                w.WriteStringValue(s.Utf8);
                break;
            case IntNode i:
                w.WriteNumberValue(i.Value);
                break;
            case BoolNode b:
                w.WriteBooleanValue(b.Value);
                break;
            case NullNode:
                w.WriteNullValue();
                break;
        }
    }
}

/// <summary>Single-buffer, backpatching CosmosBinary encoder walking the same
/// <see cref="Node"/> tree. Names/string values are copied verbatim (no escape),
/// integers written as little-endian int32 (no ASCII formatting); containers
/// reserve an LC4 prefix and backpatch the length+count after the children. This
/// is the realistic shape a production <c>CosmosBinaryWriter</c> would take.</summary>
internal sealed class BinaryTokenEncoder
{
    private byte[] _buf;
    private int _pos;

    public BinaryTokenEncoder(int capacity) => _buf = new byte[capacity];

    public ReadOnlySpan<byte> Written => _buf.AsSpan(0, _pos);

    public void Encode(Node root)
    {
        _pos = 0;
        WriteByte(0x80); // CosmosBinary format marker.
        Write(root);
    }

    private void Write(Node n)
    {
        switch (n)
        {
            case ObjNode o:
            {
                WriteByte(0xEF); // LC4 object.
                int prefix = Reserve(8);
                int childStart = _pos;
                foreach (var (name, value) in o.Props)
                {
                    WriteString(name);
                    Write(value);
                }

                Backpatch(prefix, _pos - childStart, o.Props.Length);
                break;
            }

            case ArrNode a:
            {
                WriteByte(0xE7); // LC4 array.
                int prefix = Reserve(8);
                int childStart = _pos;
                foreach (var item in a.Items)
                {
                    Write(item);
                }

                Backpatch(prefix, _pos - childStart, a.Items.Length);
                break;
            }

            case StrNode s:
                WriteString(s.Utf8);
                break;
            case IntNode i:
                if (i.Value >= int.MinValue && i.Value <= int.MaxValue)
                {
                    WriteByte(0xDA);
                    BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(Reserve(4)), (int)i.Value);
                }
                else
                {
                    WriteByte(0xCC);
                    BinaryPrimitives.WriteDoubleLittleEndian(_buf.AsSpan(Reserve(8)), i.Value);
                }

                break;
            case BoolNode b:
                WriteByte(b.Value ? (byte)0xD2 : (byte)0xD1);
                break;
            case NullNode:
                WriteByte(0xD0);
                break;
        }
    }

    private void WriteString(byte[] utf8)
    {
        if (utf8.Length < 64)
        {
            WriteByte((byte)(0x80 + utf8.Length));
        }
        else
        {
            WriteByte(0xC1);
            BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(Reserve(2)), checked((ushort)utf8.Length));
        }

        utf8.CopyTo(_buf.AsSpan(Reserve(utf8.Length)));
    }

    private void Backpatch(int prefixOffset, int payloadLength, int count)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(prefixOffset), (uint)payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(prefixOffset + 4), (uint)count);
    }

    private void WriteByte(byte value)
    {
        _buf[Reserve(1)] = value;
    }

    private int Reserve(int n)
    {
        if (_pos + n > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
        }

        int offset = _pos;
        _pos += n;
        return offset;
    }
}
