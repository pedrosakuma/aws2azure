using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Parses SQS message attributes out of either the Query-protocol flat
/// parameter dictionary (keys like <c>MessageAttribute.1.Name</c>,
/// <c>MessageAttribute.1.Value.DataType</c>, <c>MessageAttribute.1.Value.StringValue</c>,
/// <c>MessageAttribute.1.Value.BinaryValue</c>) or out of an AWS-JSON
/// <c>MessageAttributes</c> object.
///
/// <para>Validation matches the SQS contract:</para>
/// <list type="bullet">
///   <item>Names must be 1..256 characters, alphanumeric / hyphen / underscore / dot.</item>
///   <item>Names cannot start with <c>AWS.</c> or <c>Amazon.</c> (reserved).</item>
///   <item>Names cannot be repeated (case-sensitive).</item>
///   <item>Data type is mandatory; recognised bases are <c>String</c>,
///         <c>Number</c>, <c>Binary</c> — plus optional <c>.suffix</c>
///         custom labels of up to 255 chars total.</item>
///   <item>Binary values arrive base64-encoded in both wire protocols.</item>
/// </list>
/// </summary>
internal static class SqsMessageAttributeParser
{
    public const int MaxAttributeNameLength = 256;
    public const int MaxDataTypeLength = 256;

    public readonly record struct ParseResult(
        IReadOnlyDictionary<string, SqsMessageAttribute>? Attributes,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public bool IsError => ErrorCode is not null;
    }

    public static ParseResult FromQuery(
        IReadOnlyDictionary<string, string> parameters,
        string prefix = "MessageAttribute")
    {
        // Group by index. Keys look like "MessageAttribute.<index>.Name",
        // "MessageAttribute.<index>.Value.DataType", etc.
        var groups = new SortedDictionary<int, Dictionary<string, string>>();
        var dotPrefix = prefix + ".";

        foreach (var kv in parameters)
        {
            if (!kv.Key.StartsWith(dotPrefix, StringComparison.Ordinal)) continue;
            var rest = kv.Key.AsSpan(dotPrefix.Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                continue;
            var sub = rest[(dot + 1)..].ToString();
            if (!groups.TryGetValue(idx, out var bag))
            {
                bag = new Dictionary<string, string>(StringComparer.Ordinal);
                groups[idx] = bag;
            }
            bag[sub] = kv.Value;
        }

        var attrs = new Dictionary<string, SqsMessageAttribute>(StringComparer.Ordinal);
        foreach (var bag in groups.Values)
        {
            if (!bag.TryGetValue("Name", out var name))
                return Error("InvalidParameterValue", "MessageAttribute is missing Name.");
            if (!bag.TryGetValue("Value.DataType", out var dataType))
                return Error("InvalidParameterValue", $"MessageAttribute '{name}' is missing Value.DataType.");

            var nameError = ValidateName(name);
            if (nameError is not null) return Error("InvalidParameterValue", nameError);
            var typeError = ValidateDataType(dataType);
            if (typeError is not null) return Error("InvalidParameterValue", typeError);

            if (attrs.ContainsKey(name))
                return Error("InvalidParameterValue", $"Duplicate MessageAttribute '{name}'.");

            SqsMessageAttribute attr;
            if (dataType.StartsWith("Binary", StringComparison.Ordinal))
            {
                if (!bag.TryGetValue("Value.BinaryValue", out var b64))
                    return Error("InvalidParameterValue", $"MessageAttribute '{name}' of type Binary requires BinaryValue.");
                if (!TryDecodeBase64(b64, out var bytes))
                    return Error("InvalidParameterValue", $"MessageAttribute '{name}' BinaryValue is not valid base64.");
                attr = new SqsMessageAttribute { DataType = dataType, BinaryValue = bytes };
            }
            else
            {
                if (!bag.TryGetValue("Value.StringValue", out var sv))
                    return Error("InvalidParameterValue", $"MessageAttribute '{name}' requires StringValue.");
                attr = new SqsMessageAttribute { DataType = dataType, StringValue = sv };
            }
            attrs[name] = attr;
        }

        return new ParseResult(attrs, null, null);
    }

    public static ParseResult FromJson(JsonElement attrs)
    {
        if (attrs.ValueKind != JsonValueKind.Object)
            return Error("InvalidParameterValue", "MessageAttributes must be a JSON object.");

        var dict = new Dictionary<string, SqsMessageAttribute>(StringComparer.Ordinal);

        foreach (var prop in attrs.EnumerateObject())
        {
            var name = prop.Name;
            var nameError = ValidateName(name);
            if (nameError is not null) return Error("InvalidParameterValue", nameError);

            if (prop.Value.ValueKind != JsonValueKind.Object)
                return Error("InvalidParameterValue", $"MessageAttribute '{name}' must be an object.");
            if (!prop.Value.TryGetProperty("DataType", out var dt) || dt.ValueKind != JsonValueKind.String)
                return Error("InvalidParameterValue", $"MessageAttribute '{name}' is missing DataType.");
            var dataType = dt.GetString()!;
            var typeError = ValidateDataType(dataType);
            if (typeError is not null) return Error("InvalidParameterValue", typeError);

            SqsMessageAttribute attr;
            if (dataType.StartsWith("Binary", StringComparison.Ordinal))
            {
                if (!prop.Value.TryGetProperty("BinaryValue", out var bv) || bv.ValueKind != JsonValueKind.String)
                    return Error("InvalidParameterValue",
                        $"MessageAttribute '{name}' of type Binary requires BinaryValue.");
                if (!TryDecodeBase64(bv.GetString()!, out var bytes))
                    return Error("InvalidParameterValue", $"MessageAttribute '{name}' BinaryValue is not valid base64.");
                attr = new SqsMessageAttribute { DataType = dataType, BinaryValue = bytes };
            }
            else
            {
                if (!prop.Value.TryGetProperty("StringValue", out var sv) || sv.ValueKind != JsonValueKind.String)
                    return Error("InvalidParameterValue", $"MessageAttribute '{name}' requires StringValue.");
                attr = new SqsMessageAttribute { DataType = dataType, StringValue = sv.GetString() };
            }

            if (!dict.TryAdd(name, attr))
                return Error("InvalidParameterValue", $"Duplicate MessageAttribute '{name}'.");
        }
        return new ParseResult(dict, null, null);
    }

    private static string? ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxAttributeNameLength)
            return $"MessageAttribute name '{name}' is invalid (1-256 chars).";
        if (name.StartsWith("AWS.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Amazon.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".") || name.EndsWith("."))
            return $"MessageAttribute name '{name}' uses a reserved prefix or layout.";
        if (name.Contains("..", StringComparison.Ordinal))
            return $"MessageAttribute name '{name}' must not contain '..'.";
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                return $"MessageAttribute name '{name}' contains invalid character '{c}'.";
        }
        return null;
    }

    private static string? ValidateDataType(string dataType)
    {
        if (string.IsNullOrEmpty(dataType) || dataType.Length > MaxDataTypeLength)
            return $"MessageAttribute DataType '{dataType}' is invalid (1-256 chars).";
        var dot = dataType.IndexOf('.', StringComparison.Ordinal);
        string baseType;
        if (dot < 0)
        {
            baseType = dataType;
        }
        else
        {
            baseType = dataType[..dot];
            var suffix = dataType.AsSpan(dot + 1);
            // SQS requires the custom suffix to be non-empty and ≤ 250 chars.
            // We additionally reject characters that would break the
            // comma-separated Aws2Azure-AttrTypes side-channel header
            // (',', '=', CR, LF, and any non-printable / non-ASCII byte).
            if (suffix.Length == 0 || suffix.Length > 250)
                return $"MessageAttribute DataType '{dataType}' has an invalid custom suffix length.";
            foreach (var c in suffix)
            {
                if (c is ',' or '=' or '\r' or '\n' || c < 0x20 || c > 0x7E)
                    return $"MessageAttribute DataType '{dataType}' suffix contains an unsupported character.";
            }
        }
        if (baseType is not ("String" or "Number" or "Binary"))
            return $"MessageAttribute DataType '{dataType}' base must be String, Number, or Binary.";
        return null;
    }

    private static bool TryDecodeBase64(string text, out ReadOnlyMemory<byte> bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = default;
            return false;
        }
    }

    private static ParseResult Error(string code, string message) =>
        new(null, code, message);
}
