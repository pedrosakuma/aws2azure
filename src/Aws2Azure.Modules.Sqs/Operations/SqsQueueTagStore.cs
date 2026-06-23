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

    private static readonly byte[] Magic = "A2ZSQST1"u8.ToArray();

    internal static bool TryParseTagQueueRequest(
        SqsParseResult parsed,
        out Dictionary<string, string> tags,
        out string? error)
    {
        tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            AddJsonTags(parsed.JsonBody, tags, out error);
            return error is null;
        }

        AddQueryTags(parsed.Parameters, tags);
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
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(userMetadata))
        {
            return tags;
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(userMetadata);
        }
        catch (FormatException)
        {
            return tags;
        }

        if (raw.Length < Magic.Length + 1 || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            return tags;
        }

        var offset = Magic.Length;
        var count = raw[offset++];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadUtf8(raw, ref offset, out var key) ||
                !TryReadUtf8(raw, ref offset, out var value))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            tags[key] = value;
        }

        return offset == raw.Length
            ? tags
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static bool TryEncode(IReadOnlyDictionary<string, string> tags, out string userMetadata)
    {
        if (tags.Count == 0)
        {
            userMetadata = string.Empty;
            return true;
        }

        using var stream = new MemoryStream();
        stream.Write(Magic, 0, Magic.Length);
        stream.WriteByte((byte)tags.Count);

        var keys = new List<string>(tags.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            WriteUtf8(stream, key);
            WriteUtf8(stream, tags[key]);
        }

        userMetadata = Convert.ToBase64String(stream.ToArray());
        return userMetadata.Length <= UserMetadataMaxLength;
    }

    private static void AddQueryTags(
        IReadOnlyDictionary<string, string> parameters,
        Dictionary<string, string> tags)
    {
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

    private static void AddJsonTags(string jsonBody, Dictionary<string, string> tags, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!doc.RootElement.TryGetProperty("Tags", out var tagsElement) ||
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
