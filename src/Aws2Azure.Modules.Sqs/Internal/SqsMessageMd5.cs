using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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

        // Sort by name in ordinal byte order (matches AWS canonicalisation).
        var names = new List<string>(attributes.Keys);
        names.Sort(StringComparer.Ordinal);

        using var buffer = new MemoryStream();
        Span<byte> len = stackalloc byte[4];

        foreach (var name in names)
        {
            var attr = attributes[name];

            var nameBytes = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)nameBytes.Length);
            buffer.Write(len);
            buffer.Write(nameBytes);

            var typeBytes = Encoding.UTF8.GetBytes(attr.DataType);
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)typeBytes.Length);
            buffer.Write(len);
            buffer.Write(typeBytes);

            if (attr.IsBinary)
            {
                buffer.WriteByte(2);
                BinaryPrimitives.WriteUInt32BigEndian(len, (uint)attr.BinaryValue.Length);
                buffer.Write(len);
                buffer.Write(attr.BinaryValue.Span);
            }
            else
            {
                buffer.WriteByte(1);
                var valueBytes = Encoding.UTF8.GetBytes(attr.StringValue ?? string.Empty);
                BinaryPrimitives.WriteUInt32BigEndian(len, (uint)valueBytes.Length);
                buffer.Write(len);
                buffer.Write(valueBytes);
            }
        }

        Span<byte> digest = stackalloc byte[16];
        if (!buffer.TryGetBuffer(out var seg))
        {
            // MemoryStream backed by an exposable buffer; should always succeed
            // because we constructed it with the default ctor.
            throw new InvalidOperationException("MD5 buffer is not exposable.");
        }
        MD5.HashData(seg.AsSpan(), digest);
        return Convert.ToHexStringLower(digest);
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
