using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.Sqs.WireProtocol;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsQueueTagStore
{
    internal const int MaxTags = 50;
    internal const int MaxTagKeyLength = 128;
    internal const int MaxTagValueLength = 256;
    internal const int UserMetadataMaxLength = 1024;

    private static readonly byte[] LegacyMagic = "A2ZSQST1"u8.ToArray();
    private static readonly byte[] MetadataMagic = "A2ZSQSM2"u8.ToArray();

    internal sealed class QueueMetadata
    {
        public QueueMetadata()
            : this(new Dictionary<string, string>(StringComparer.Ordinal))
        {
        }

        public QueueMetadata(Dictionary<string, string> tags)
        {
            Tags = tags;
        }

        public Dictionary<string, string> Tags { get; }

        public int? DelaySeconds { get; set; }

        public int? ReceiveMessageWaitTimeSeconds { get; set; }
    }

    internal static bool TryParseTagQueueRequest(
        SqsParseResult parsed,
        out Dictionary<string, string> tags,
        out string? error)
    {
        tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            AddJsonTags(parsed.JsonBody, tags, out error, "Tags", "tags");
            return error is null;
        }

        AddQueryTags(parsed.Parameters, tags);
        error = null;
        return true;
    }

    internal static bool TryParseCreateQueueTags(
        SqsParseResult parsed,
        out Dictionary<string, string> tags,
        out string? error)
    {
        tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            AddJsonTags(parsed.JsonBody, tags, out error, "tags", "Tags");
            return error is null;
        }

        AddCreateQueueQueryTags(parsed.Parameters, tags);
        error = null;
        return true;
    }

    internal static bool TryParseUntagQueueRequest(
        SqsParseResult parsed,
        out List<string> tagKeys,
        out string? error)
    {
        tagKeys = new List<string>();
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            AddJsonTagKeys(parsed.JsonBody, tagKeys, out error);
            return error is null;
        }

        var keyByIndex = new SortedDictionary<int, string>();
        foreach (var kv in parsed.Parameters)
        {
            if (string.Equals(kv.Key, "TagKey", StringComparison.Ordinal))
            {
                tagKeys.Add(kv.Value);
                continue;
            }

            if (!kv.Key.StartsWith("TagKey.", StringComparison.Ordinal)) continue;
            var suffix = kv.Key.AsSpan("TagKey.".Length);
            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                keyByIndex[idx] = kv.Value;
            }
        }

        foreach (var kv in keyByIndex)
        {
            tagKeys.Add(kv.Value);
        }

        error = null;
        return true;
    }

    internal static string? ValidateTagMap(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.Count > MaxTags)
        {
            return $"A queue can have at most {MaxTags} tags.";
        }

        foreach (var kv in tags)
        {
            var err = ValidateTagKey(kv.Key);
            if (err is not null) return err;
            if (kv.Value.Length > MaxTagValueLength)
            {
                return $"Tag value for key '{kv.Key}' exceeds the {MaxTagValueLength}-character limit.";
            }
        }

        return null;
    }

    internal static string? ValidateTagKeys(IReadOnlyList<string> tagKeys)
    {
        for (var i = 0; i < tagKeys.Count; i++)
        {
            var err = ValidateTagKey(tagKeys[i]);
            if (err is not null) return err;
        }

        return null;
    }

    internal static Dictionary<string, string> Decode(string? userMetadata)
        => DecodeMetadata(userMetadata).Tags;

    internal static QueueMetadata DecodeMetadata(string? userMetadata)
    {
        TryDecode(userMetadata, failOnForeignMetadata: false, out var metadata, out _);
        return metadata;
    }

    internal static bool TryDecodeForMutation(
        string? userMetadata,
        out QueueMetadata metadata,
        out string? error) =>
        TryDecode(userMetadata, failOnForeignMetadata: true, out metadata, out error);

    internal static QueueMetadata CloneMetadata(QueueMetadata metadata)
    {
        var clone = new QueueMetadata(new Dictionary<string, string>(metadata.Tags, StringComparer.Ordinal))
        {
            DelaySeconds = metadata.DelaySeconds,
            ReceiveMessageWaitTimeSeconds = metadata.ReceiveMessageWaitTimeSeconds,
        };
        return clone;
    }

    private static bool TryDecode(
        string? userMetadata,
        bool failOnForeignMetadata,
        out QueueMetadata metadata,
        out string? error)
    {
        metadata = new QueueMetadata();
        error = null;
        if (string.IsNullOrWhiteSpace(userMetadata))
        {
            return true;
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(userMetadata);
        }
        catch (FormatException)
        {
            return HandleForeignMetadata(failOnForeignMetadata, out error);
        }

        if (raw.Length >= MetadataMagic.Length + 2 &&
            raw.AsSpan(0, MetadataMagic.Length).SequenceEqual(MetadataMagic))
        {
            return TryDecodeMetadata(raw, failOnForeignMetadata, metadata, out error);
        }

        if (raw.Length < LegacyMagic.Length + 1 ||
            !raw.AsSpan(0, LegacyMagic.Length).SequenceEqual(LegacyMagic))
        {
            return HandleForeignMetadata(failOnForeignMetadata, out error);
        }

        var offset = LegacyMagic.Length;
        var count = raw[offset++];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadUtf8(raw, ref offset, out var key) ||
                !TryReadUtf8(raw, ref offset, out var value))
            {
                metadata.Tags.Clear();
                return HandleMalformedMetadata(failOnForeignMetadata, out error);
            }

            metadata.Tags[key] = value;
        }

        if (offset == raw.Length)
        {
            return true;
        }

        metadata.Tags.Clear();
        return HandleMalformedMetadata(failOnForeignMetadata, out error);
    }

    private static bool TryDecodeMetadata(
        byte[] raw,
        bool failOnForeignMetadata,
        QueueMetadata metadata,
        out string? error)
    {
        error = null;
        var offset = MetadataMagic.Length;
        var flags = raw[offset++];
        if ((flags & 0xFC) != 0)
        {
            return HandleMalformedMetadata(failOnForeignMetadata, out error);
        }

        if ((flags & 0x01) != 0)
        {
            if (offset + 2 > raw.Length)
            {
                return HandleMalformedMetadata(failOnForeignMetadata, out error);
            }

            metadata.DelaySeconds = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset, 2));
            offset += 2;
        }

        if ((flags & 0x02) != 0)
        {
            if (offset >= raw.Length)
            {
                return HandleMalformedMetadata(failOnForeignMetadata, out error);
            }

            metadata.ReceiveMessageWaitTimeSeconds = raw[offset++];
        }

        if (offset >= raw.Length)
        {
            return HandleMalformedMetadata(failOnForeignMetadata, out error);
        }

        var count = raw[offset++];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadUtf8(raw, ref offset, out var key) ||
                !TryReadUtf8(raw, ref offset, out var value))
            {
                metadata.Tags.Clear();
                return HandleMalformedMetadata(failOnForeignMetadata, out error);
            }

            metadata.Tags[key] = value;
        }

        if (offset == raw.Length)
        {
            return true;
        }

        metadata.Tags.Clear();
        return HandleMalformedMetadata(failOnForeignMetadata, out error);
    }

    private static bool HandleForeignMetadata(bool failOnForeignMetadata, out string? error)
    {
        if (failOnForeignMetadata)
        {
            error = "Azure Service Bus QueueDescription.UserMetadata is already in use by non-aws2azure content; " +
                    "aws2azure cannot safely persist SQS queue metadata without overwriting it.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool HandleMalformedMetadata(bool failOnForeignMetadata, out string? error)
    {
        if (failOnForeignMetadata)
        {
            error = "aws2azure queue metadata stored in Azure Service Bus QueueDescription.UserMetadata is malformed; " +
                    "refusing to overwrite it.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool TryEncode(IReadOnlyDictionary<string, string> tags, out string userMetadata)
        => TryEncodeMetadata(
            new QueueMetadata(new Dictionary<string, string>(tags, StringComparer.Ordinal)),
            out userMetadata);

    internal static bool TryEncodeMetadata(QueueMetadata metadata, out string userMetadata)
    {
        if (metadata.Tags.Count == 0 &&
            metadata.DelaySeconds is null &&
            metadata.ReceiveMessageWaitTimeSeconds is null)
        {
            userMetadata = string.Empty;
            return true;
        }

        using var stream = new MemoryStream();
        if (metadata.DelaySeconds is null && metadata.ReceiveMessageWaitTimeSeconds is null)
        {
            stream.Write(LegacyMagic, 0, LegacyMagic.Length);
            stream.WriteByte((byte)metadata.Tags.Count);
        }
        else
        {
            stream.Write(MetadataMagic, 0, MetadataMagic.Length);
            byte flags = 0;
            if (metadata.DelaySeconds is not null) flags |= 0x01;
            if (metadata.ReceiveMessageWaitTimeSeconds is not null) flags |= 0x02;
            stream.WriteByte(flags);
            if (metadata.DelaySeconds is { } delay)
            {
                Span<byte> delayBytes = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(delayBytes, checked((ushort)delay));
                stream.Write(delayBytes);
            }
            if (metadata.ReceiveMessageWaitTimeSeconds is { } wait)
            {
                stream.WriteByte((byte)wait);
            }
            stream.WriteByte((byte)metadata.Tags.Count);
        }

        var keys = new List<string>(metadata.Tags.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            WriteUtf8(stream, key);
            WriteUtf8(stream, metadata.Tags[key]);
        }

        userMetadata = Convert.ToBase64String(stream.ToArray());
        return userMetadata.Length <= UserMetadataMaxLength;
    }

    private static void AddQueryTags(
        IReadOnlyDictionary<string, string> parameters,
        Dictionary<string, string> tags)
    {
        if (parameters.TryGetValue("Tag.Key", out var singleKey))
        {
            tags[singleKey] = parameters.TryGetValue("Tag.Value", out var singleValue)
                ? singleValue
                : string.Empty;
        }

        var keyByIndex = new SortedDictionary<int, string>();
        var valueByIndex = new SortedDictionary<int, string>();
        foreach (var kv in parameters)
        {
            if (!kv.Key.StartsWith("Tag.", StringComparison.Ordinal)) continue;
            var rest = kv.Key.AsSpan("Tag.".Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                continue;

            var sub = rest[(dot + 1)..];
            if (sub.SequenceEqual("Key")) keyByIndex[idx] = kv.Value;
            else if (sub.SequenceEqual("Value")) valueByIndex[idx] = kv.Value;
        }

        foreach (var kv in keyByIndex)
        {
            tags[kv.Value] = valueByIndex.TryGetValue(kv.Key, out var value) ? value : string.Empty;
        }
    }

    private static void AddCreateQueueQueryTags(
        IReadOnlyDictionary<string, string> parameters,
        Dictionary<string, string> tags)
    {
        AddQueryTags(parameters, tags);

        var keyByIndex = new SortedDictionary<int, string>();
        var valueByIndex = new SortedDictionary<int, string>();
        foreach (var kv in parameters)
        {
            if (!TryParseCreateQueueTagPart(kv.Key, out var idx, out var isKey))
            {
                continue;
            }

            if (isKey)
            {
                keyByIndex[idx] = kv.Value;
            }
            else
            {
                valueByIndex[idx] = kv.Value;
            }
        }

        foreach (var kv in keyByIndex)
        {
            tags[kv.Value] = valueByIndex.TryGetValue(kv.Key, out var value) ? value : string.Empty;
        }
    }

    private static bool TryParseCreateQueueTagPart(string key, out int index, out bool isKey)
    {
        index = 0;
        isKey = false;

        const string Prefix = "tags.";
        if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = key.AsSpan(Prefix.Length);
        if (rest.StartsWith("entry.", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["entry.".Length..];
        }

        var dot = rest.IndexOf('.');
        if (dot <= 0 ||
            !int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        var suffix = rest[(dot + 1)..];
        if (suffix.Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            isKey = true;
            return true;
        }

        return suffix.Equals("value", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddJsonTags(
        string jsonBody,
        Dictionary<string, string> tags,
        out string? error,
        params string[] propertyNames)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!TryGetObjectProperty(doc.RootElement, propertyNames, out var tagsElement) ||
                tagsElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var prop in tagsElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    error = "Every Tags value must be a string.";
                    return;
                }

                tags[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            error = "Tags must be a JSON object.";
        }
    }

    private static void AddJsonTagKeys(string jsonBody, List<string> tagKeys, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!doc.RootElement.TryGetProperty("TagKeys", out var keysElement) ||
                keysElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in keysElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = "Every TagKeys entry must be a string.";
                    return;
                }

                tagKeys.Add(item.GetString() ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            error = "TagKeys must be a JSON array.";
        }
    }

    private static bool TryGetObjectProperty(
        JsonElement element,
        IReadOnlyList<string> propertyNames,
        out JsonElement property)
    {
        for (var i = 0; i < propertyNames.Count; i++)
        {
            if (element.TryGetProperty(propertyNames[i], out property))
            {
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string? ValidateTagKey(string key)
    {
        if (key.Length == 0 || key.Length > MaxTagKeyLength)
        {
            return $"Tag keys must be 1..{MaxTagKeyLength} characters long.";
        }

        return null;
    }

    private static bool TryReadUtf8(byte[] raw, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + 2 > raw.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset, 2));
        offset += 2;
        if (offset + length > raw.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(raw, offset, length);
        offset += length;
        return true;
    }

    private static void WriteUtf8(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        stream.Write(length);
        stream.Write(bytes, 0, bytes.Length);
    }
}
