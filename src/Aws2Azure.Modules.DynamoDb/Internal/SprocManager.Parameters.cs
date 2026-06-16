using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

internal sealed partial class SprocManager
{
    /// <summary>
    /// Writes the single-write sproc parameter list
    /// <c>[op, docId, payload, conditionAst, updateAst]</c> straight into a
    /// pooled UTF-8 buffer. The document <paramref name="payload"/> (and the
    /// already-serialized condition/update ASTs) are spliced as raw JSON bytes,
    /// so there is no <c>byte[] → string → byte[]</c> round-trip. Output is
    /// byte-identical to <see cref="BuildParamsJson"/> (verified by tests).
    /// </summary>
    internal static void WriteSingleWriteParams(
        PooledByteBufferWriter buf,
        SprocOperation op,
        string docId,
        ReadOnlyMemory<byte>? payload,
        string? conditionAst,
        string? updateAst)
    {
        WriteByte(buf, (byte)'[');
        WriteRaw(buf, OpLiteral(op));
        WriteByte(buf, (byte)',');
        WriteMinimallyEscapedJsonString(buf, docId);
        WriteByte(buf, (byte)',');
        WriteRawFragment(buf, payload);
        WriteByte(buf, (byte)',');
        WriteRawFragment(buf, conditionAst);
        WriteByte(buf, (byte)',');
        WriteRawFragment(buf, updateAst);
        WriteByte(buf, (byte)']');
    }

    private static ReadOnlySpan<byte> OpLiteral(SprocOperation op) => op switch
    {
        SprocOperation.Put => "\"PUT\""u8,
        SprocOperation.Update => "\"UPDATE\""u8,
        SprocOperation.Delete => "\"DELETE\""u8,
        _ => "\"PUT\""u8,
    };

    private static void WriteByte(IBufferWriter<byte> buf, byte value)
    {
        var span = buf.GetSpan(1);
        span[0] = value;
        buf.Advance(1);
    }

    private static void WriteRaw(IBufferWriter<byte> buf, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }
        var span = buf.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        buf.Advance(bytes.Length);
    }

    // Splices a raw JSON byte fragment, or the literal `null` when absent —
    // matching the legacy `payload ?? "null"` semantics.
    private static void WriteRawFragment(IBufferWriter<byte> buf, ReadOnlyMemory<byte>? fragment)
    {
        if (fragment.HasValue)
        {
            WriteRaw(buf, fragment.Value.Span);
        }
        else
        {
            WriteRaw(buf, "null"u8);
        }
    }

    private static void WriteRawFragment(IBufferWriter<byte> buf, string? fragment)
    {
        if (fragment is null)
        {
            WriteRaw(buf, "null"u8);
            return;
        }
        var span = buf.GetSpan(Encoding.UTF8.GetMaxByteCount(fragment.Length));
        int written = Encoding.UTF8.GetBytes(fragment, span);
        buf.Advance(written);
    }

    // Writes a quoted JSON string escaping only `\` and `"`, matching
    // <see cref="EscapeJsonString"/> exactly (byte-for-byte). Escaping at the
    // UTF-8 byte level is safe because both characters are single-byte ASCII
    // that never appear as a multi-byte continuation octet.
    private static void WriteMinimallyEscapedJsonString(IBufferWriter<byte> buf, string s)
    {
        WriteByte(buf, (byte)'"');
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        byte[] tmp = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int n = Encoding.UTF8.GetBytes(s, tmp);
            var span = buf.GetSpan(n * 2);
            int pos = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = tmp[i];
                if (b == (byte)'\\' || b == (byte)'"')
                {
                    span[pos++] = (byte)'\\';
                }
                span[pos++] = b;
            }
            buf.Advance(pos);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
        WriteByte(buf, (byte)'"');
    }

    // Legacy string-based parameter encoder, retained as the byte-identity
    // reference for <see cref="WriteSingleWriteParams"/> tests. Not used on the
    // request path (which is now single-pass / zero-copy).
    internal static string BuildParamsJson(SprocOperation op, string docId, string? payload, string? conditionAst, string? updateAst)
    {
        var sb = new StringBuilder(256);
        sb.Append('[');
        sb.Append('"').Append(op.ToString().ToUpperInvariant()).Append('"');
        sb.Append(',').Append('"').Append(EscapeJsonString(docId)).Append('"');
        sb.Append(',').Append(payload ?? "null");
        sb.Append(',').Append(conditionAst ?? "null");
        sb.Append(',').Append(updateAst ?? "null");
        sb.Append(']');
        return sb.ToString();
    }

    private static string EscapeJsonString(string s)
    {
        // Simple JSON string escaping for partition key values
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
