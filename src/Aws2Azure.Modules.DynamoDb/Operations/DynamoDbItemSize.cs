using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static class DynamoDbItemSize
{
    public const int MaximumBytes = 400 * 1024;

    public static bool TryValidateWriteSize(
        JsonElement item,
        TableMetadata metadata,
        string itemName,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!TryCalculate(item, out var baseItemSize, out error))
        {
            return false;
        }

        if (baseItemSize > MaximumBytes)
        {
            error =
                $"{itemName} is {baseItemSize} bytes; DynamoDB items must not exceed " +
                $"{MaximumBytes} bytes (400 KiB).";
            return false;
        }

        if (!TryCalculateWithLocalSecondaryIndexes(
                item,
                metadata,
                baseItemSize,
                out var combinedSize,
                out error))
        {
            return false;
        }

        if (combinedSize > MaximumBytes)
        {
            error =
                $"{itemName} plus its local secondary index entries is {combinedSize} bytes; " +
                $"the combined DynamoDB limit is {MaximumBytes} bytes (400 KiB).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryCalculate(
        JsonElement item,
        out long size,
        out string error)
    {
        size = 0;
        if (item.ValueKind != JsonValueKind.Object)
        {
            error = "Item must be a JSON object.";
            return false;
        }

        foreach (var attribute in item.EnumerateObject())
        {
            size += Encoding.UTF8.GetByteCount(attribute.Name);
            if (!TryAddValueSize(attribute.Value, ref size, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool TryCalculateWithLocalSecondaryIndexes(
        JsonElement item,
        TableMetadata metadata,
        long baseItemSize,
        out long combinedSize,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        combinedSize = baseItemSize;
        if (metadata.LocalSecondaryIndexes is not { Count: > 0 } indexes)
        {
            error = string.Empty;
            return true;
        }

        foreach (var index in indexes)
        {
            if (!TryAddLocalSecondaryIndexEntry(
                    item,
                    metadata,
                    index,
                    baseItemSize,
                    ref combinedSize,
                    out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryAddLocalSecondaryIndexEntry(
        JsonElement item,
        TableMetadata metadata,
        TableIndexDefinition index,
        long baseItemSize,
        ref long combinedSize,
        out string error)
    {
        var hasRangeKey = false;
        var indexKeyNames = new string?[index.KeySchema.Count];
        for (var keyIndex = 0; keyIndex < index.KeySchema.Count; keyIndex++)
        {
            var key = index.KeySchema[keyIndex];
            indexKeyNames[keyIndex] = key.Name;
            if (string.Equals(key.KeyType, "RANGE", StringComparison.Ordinal))
            {
                hasRangeKey = true;
            }
        }

        if (!hasRangeKey)
        {
            error =
                $"Local secondary index '{index.IndexName}' has invalid metadata: a RANGE key is required.";
            return false;
        }

        for (var keyIndex = 0; keyIndex < indexKeyNames.Length; keyIndex++)
        {
            if (!item.TryGetProperty(indexKeyNames[keyIndex]!, out _))
            {
                error = string.Empty;
                return true;
            }
        }

        if (string.Equals(
                index.ProjectionType,
                "ALL",
                StringComparison.OrdinalIgnoreCase))
        {
            combinedSize += baseItemSize;
            error = string.Empty;
            return true;
        }
        if (!string.Equals(
                index.ProjectionType,
                "KEYS_ONLY",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                index.ProjectionType,
                "INCLUDE",
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                $"Local secondary index '{index.IndexName}' has unsupported projection type '{index.ProjectionType}'.";
            return false;
        }

        var projected = SecondaryIndexResolver.ResolveIndexProjection(
            metadata,
            index,
            indexKeyNames)!;
        for (var attributeIndex = 0;
             attributeIndex < projected.Count;
             attributeIndex++)
        {
            var attributeName = projected[attributeIndex];
            if (!item.TryGetProperty(attributeName, out var attributeValue))
            {
                continue;
            }

            combinedSize += Encoding.UTF8.GetByteCount(attributeName);
            if (!TryAddValueSize(
                    attributeValue,
                    ref combinedSize,
                    out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryAddValueSize(
        JsonElement attribute,
        ref long size,
        out string error)
    {
        if (!ParsedAttributeValue.TryParse(attribute, out var parsed))
        {
            error = "AttributeValue must contain exactly one supported type tag.";
            return false;
        }

        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                size += Encoding.UTF8.GetByteCount(parsed.Value.GetString()!);
                break;

            case AttributeValueTypes.Number:
                if (!TryAddNumberSize(parsed.Value.GetString()!, ref size, out error))
                {
                    return false;
                }
                break;

            case AttributeValueTypes.Binary:
                if (!TryAddBinarySize(parsed.Value.GetString()!, ref size))
                {
                    error = "Binary attribute value is not valid base64.";
                    return false;
                }
                break;

            case AttributeValueTypes.Bool:
            case AttributeValueTypes.Null:
                size++;
                break;

            case AttributeValueTypes.Map:
                size += 3;
                foreach (var entry in parsed.Value.EnumerateObject())
                {
                    size += 1 + Encoding.UTF8.GetByteCount(entry.Name);
                    if (!TryAddValueSize(entry.Value, ref size, out error))
                    {
                        return false;
                    }
                }
                break;

            case AttributeValueTypes.List:
                size += 3;
                foreach (var entry in parsed.Value.EnumerateArray())
                {
                    size++;
                    if (!TryAddValueSize(entry, ref size, out error))
                    {
                        return false;
                    }
                }
                break;

            case AttributeValueTypes.StringSet:
                foreach (var entry in parsed.Value.EnumerateArray())
                {
                    size += Encoding.UTF8.GetByteCount(entry.GetString()!);
                }
                break;

            case AttributeValueTypes.NumberSet:
                foreach (var entry in parsed.Value.EnumerateArray())
                {
                    if (!TryAddNumberSize(
                            entry.GetString()!,
                            ref size,
                            out error))
                    {
                        return false;
                    }
                }
                break;

            case AttributeValueTypes.BinarySet:
                foreach (var entry in parsed.Value.EnumerateArray())
                {
                    if (!TryAddBinarySize(entry.GetString()!, ref size))
                    {
                        error = "Binary set member is not valid base64.";
                        return false;
                    }
                }
                break;

            default:
                error = $"Unsupported AttributeValue type '{parsed.TypeTag}'.";
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryAddNumberSize(
        string value,
        ref long size,
        out string error)
    {
        if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                value,
                out var canonical,
                out _,
                out error))
        {
            return false;
        }

        var firstSignificant = -1;
        var lastSignificant = -1;
        for (var index = 0; index < canonical.Length; index++)
        {
            var character = canonical[index];
            if (character is < '1' or > '9')
            {
                continue;
            }
            firstSignificant = firstSignificant < 0 ? index : firstSignificant;
            lastSignificant = index;
        }

        var significantDigits = 0;
        if (firstSignificant >= 0)
        {
            for (var index = firstSignificant; index <= lastSignificant; index++)
            {
                if (canonical[index] is >= '0' and <= '9')
                {
                    significantDigits++;
                }
            }
        }
        else
        {
            significantDigits = 1;
        }

        size += ((significantDigits + 1) / 2) + 1;
        error = string.Empty;
        return true;
    }

    private static bool TryAddBinarySize(string value, ref long size)
    {
        var maximumLength = checked(((value.Length + 3) / 4) * 3);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumLength));
        try
        {
            if (!Convert.TryFromBase64Chars(value.AsSpan(), buffer, out var written))
            {
                return false;
            }
            size += written;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
