using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static class DynamoDbItemSize
{
    public const int MaximumBytes = 400 * 1024;

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
