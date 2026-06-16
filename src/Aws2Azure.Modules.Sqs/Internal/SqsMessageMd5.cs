using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Computes the two MD5 digests SQS embeds in every SendMessage /
/// SendMessageBatch / ReceiveMessage response so clients can detect
/// transport corruption:
///
/// <list type="bullet">
///   <item><term>MD5OfMessageBody</term>
///         <description>plain MD5 of the UTF-8 body bytes.</description></item>
///   <item><term>MD5OfMessageAttributes</term>
///         <description>MD5 over a length-prefixed, lexicographically-sorted
///         encoding of the message attributes, exactly as documented in the
///         AWS SQS Developer Guide.</description></item>
/// </list>
///
/// <para>Both digests are returned as 32-char lowercase hex strings to match
/// the SQS wire format.</para>
/// </summary>
internal static class SqsMessageMd5
{
    public static string OfBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return OfBody(Encoding.UTF8.GetBytes(body));
    }

    public static string OfBody(ReadOnlySpan<byte> bodyBytes)
    {
        Span<byte> digest = stackalloc byte[16];
        MD5.HashData(bodyBytes, digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Computes the MD5 over an SQS message attribute bag. The encoding is
    /// (in order, sorted by attribute name in lexicographic byte order):
    /// <code>
    /// for each attribute:
    ///   UInt32BE(name.Length) || UTF8(name)
    ///   UInt32BE(dataType.Length) || UTF8(dataType)
    ///   1 byte transport type: 1 = String/Number value, 2 = Binary value
    ///   if String/Number:  UInt32BE(value.Length) || UTF8(value)
    ///   if Binary:         UInt32BE(value.Length) || raw bytes
    /// </code>
    /// </summary>
    public static string OfAttributes(IReadOnlyDictionary<string, SqsMessageAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count == 0) return string.Empty;

        var names = ArrayPool<string>.Shared.Rent(attributes.Count);
        var rentedBuffer = Array.Empty<byte>();
        try
        {
            var nameCount = 0;
            foreach (var name in attributes.Keys)
                names[nameCount++] = name;

            Array.Sort(names, 0, nameCount, StringComparer.Ordinal);

            var totalLength = 0;
            for (var i = 0; i < nameCount; i++)
            {
                var name = names[i];
                var attr = attributes[name];
                totalLength += 4 + Encoding.UTF8.GetByteCount(name);
                totalLength += 4 + Encoding.UTF8.GetByteCount(attr.DataType);
                totalLength += 1 + 4 + (attr.IsBinary
                    ? attr.BinaryValue.Length
                    : Encoding.UTF8.GetByteCount(attr.StringValue ?? string.Empty));
            }

            rentedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
            var buffer = rentedBuffer.AsSpan(0, totalLength);
            var pos = 0;
            for (var i = 0; i < nameCount; i++)
            {
                var name = names[i];
                var attr = attributes[name];
                WriteUtf8Field(buffer, ref pos, name);
                WriteUtf8Field(buffer, ref pos, attr.DataType);
                if (attr.IsBinary)
                {
                    buffer[pos++] = 2;
                    WriteBinaryField(buffer, ref pos, attr.BinaryValue.Span);
                }
                else
                {
                    buffer[pos++] = 1;
                    WriteUtf8Field(buffer, ref pos, attr.StringValue ?? string.Empty);
                }
            }

            Span<byte> digest = stackalloc byte[16];
            MD5.HashData(buffer, digest);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            ArrayPool<string>.Shared.Return(names, clearArray: true);
            if (rentedBuffer.Length != 0)
                ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static void WriteUtf8Field(Span<byte> buffer, ref int pos, string value)
    {
        var lenSpan = buffer.Slice(pos, 4);
        pos += 4;
        var written = Encoding.UTF8.GetBytes(value, buffer[pos..]);
        BinaryPrimitives.WriteUInt32BigEndian(lenSpan, (uint)written);
        pos += written;
    }

    private static void WriteBinaryField(Span<byte> buffer, ref int pos, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(pos, 4), (uint)value.Length);
        pos += 4;
        value.CopyTo(buffer[pos..]);
        pos += value.Length;
    }
}

/// <summary>
/// Typed view of a single SQS message attribute. The DataType is the raw
/// SQS type string (e.g. "String", "Number", "Binary", "String.Custom").
/// String/Number attributes carry their value in <see cref="StringValue"/>;
/// Binary attributes carry it in <see cref="BinaryValue"/>.
/// </summary>
internal sealed class SqsMessageAttribute
{
    public string DataType { get; init; } = "String";
    public string? StringValue { get; init; }
    public ReadOnlyMemory<byte> BinaryValue { get; init; }

    /// <summary>
    /// True when the attribute's base type is Binary (i.e. DataType starts
    /// with "Binary"). String/Number attributes — including custom subtypes
    /// like "String.X" — are not binary.
    /// </summary>
    public bool IsBinary =>
        DataType.StartsWith("Binary", StringComparison.Ordinal);
}
