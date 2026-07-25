using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class ItemHandlers
{
    internal const int MaximumPartitionKeyBytes = 2048;
    internal const int MaximumSortKeyBytes = 1024;

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    /// <summary>
    /// Validates every attribute in the Item is a single-property typed
    /// value (per the DynamoDB JSON wire format) AND that each payload's
    /// shape matches its declared type tag (S/N/B → string, BOOL →
    /// boolean, NULL → true, M → object, L → array, SS/NS/BS → array
    /// of strings). Catches malformed inputs early so a write can't
    /// poison the partition with a doc that GetItem cannot parse and so
    /// the encoder's invariants always hold by the time we call it.
    /// </summary>
    internal static bool ValidateItemShape(JsonElement item, out string error)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            error = "Item must be a JSON object.";
            return false;
        }

        foreach (var prop in item.EnumerateObject())
        {
            if (InferredAttributeStorage.IsReservedTopLevelName(prop.Name)
                && !InferredAttributeStorage.IsShadowEncodableName(prop.Name))
            {
                error = $"Attribute '{prop.Name}' uses a reserved name and would collide with proxy metadata.";
                return false;
            }
            if (!ValidateAttributePayload(prop.Name, prop.Value, out error))
            {
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    internal static bool ValidateKeyShape(JsonElement key, out string error)
    {
        if (key.ValueKind != JsonValueKind.Object)
        {
            error = "Key must be a JSON object.";
            return false;
        }

        foreach (var property in key.EnumerateObject())
        {
            if (!ValidateAttributePayload(
                    $"Key attribute '{property.Name}'",
                    property.Value,
                    out error))
            {
                return false;
            }
            if (!ParsedAttributeValue.TryParse(property.Value, out var parsed)
                || !AttributeValueTypes.IsScalarKeyType(parsed.TypeTag))
            {
                error =
                    $"Key attribute '{property.Name}' must use scalar type S, N, or B.";
                return false;
            }

            if ((parsed.TypeTag is AttributeValueTypes.String
                    or AttributeValueTypes.Binary)
                && parsed.Value.GetString()!.Length == 0)
            {
                error = $"Key attribute '{property.Name}' value must not be empty.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    internal static bool ValidateExpressionAttributeValues(
        JsonElement values,
        out string error)
    {
        if (values.ValueKind != JsonValueKind.Object)
        {
            error = "ExpressionAttributeValues must be a JSON object.";
            return false;
        }

        foreach (var property in values.EnumerateObject())
        {
            if (!ValidateAttributePayload(
                    $"ExpressionAttributeValues['{property.Name}']",
                    property.Value,
                    out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Recursive shape validator for a single DDB AttributeValue. Mirrors
    /// the type discipline the inferred encoder relies on, so any
    /// rejection here surfaces as a client <c>ValidationException</c>
    /// instead of an encoder <c>ArgumentException</c> deeper down the
    /// stack. Number / Binary / Set payloads must be strings; sets are
    /// arrays of strings; maps recurse; lists recurse.
    /// </summary>
    private static bool ValidateAttributePayload(
        string attrName,
        JsonElement attr,
        out string error)
    {
        if (!ParsedAttributeValue.TryParse(attr, out var parsed))
        {
            error = $"{attrName} must be a single-property typed attribute value.";
            return false;
        }

        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                if (parsed.Value.ValueKind != JsonValueKind.String)
                {
                    error =
                        $"{attrName} payload for type {parsed.TypeTag} must be a JSON string.";
                    return false;
                }
                break;

            case AttributeValueTypes.Number:
                if (parsed.Value.ValueKind != JsonValueKind.String)
                {
                    error =
                        $"{attrName} payload for type N must be a JSON string.";
                    return false;
                }
                if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                        parsed.Value.GetString()!,
                        out _,
                        out _,
                        out var numberError))
                {
                    error = $"{attrName} has an invalid Number value: {numberError}";
                    return false;
                }
                break;

            case AttributeValueTypes.Binary:
                if (parsed.Value.ValueKind != JsonValueKind.String)
                {
                    error =
                        $"{attrName} payload for type B must be a JSON string.";
                    return false;
                }
                if (!IsValidBase64(parsed.Value.GetString()!))
                {
                    error = $"{attrName} binary value is not valid base64.";
                    return false;
                }
                break;

            case AttributeValueTypes.Bool:
                if (parsed.Value.ValueKind != JsonValueKind.True && parsed.Value.ValueKind != JsonValueKind.False)
                {
                    error = $"{attrName} payload for type BOOL must be a JSON boolean.";
                    return false;
                }
                break;

            case AttributeValueTypes.Null:
                if (parsed.Value.ValueKind != JsonValueKind.True)
                {
                    error = $"{attrName} payload for type NULL must be the literal true.";
                    return false;
                }
                break;

            case AttributeValueTypes.Map:
                if (parsed.Value.ValueKind != JsonValueKind.Object)
                {
                    error = $"{attrName} payload for type M must be a JSON object.";
                    return false;
                }
                foreach (var entry in parsed.Value.EnumerateObject())
                {
                    if (entry.Name.StartsWith(
                            InferredAttributeStorage.EnvelopeTagPrefix, StringComparison.Ordinal))
                    {
                        // Encoder enforces this too (InferredAttributeStorage.cs:293)
                        // but raising here keeps the error surface as
                        // ValidationException at the API boundary instead of
                        // an encoder ArgumentException deeper in the stack.
                        error = $"{attrName}.{entry.Name} uses the reserved '"
                            + InferredAttributeStorage.EnvelopeTagPrefix
                            + "' prefix.";
                        return false;
                    }
                    if (!ValidateAttributePayload(
                            $"{attrName}.{entry.Name}",
                            entry.Value,
                            out error))
                        return false;
                }
                break;

            case AttributeValueTypes.List:
                if (parsed.Value.ValueKind != JsonValueKind.Array)
                {
                    error = $"{attrName} payload for type L must be a JSON array.";
                    return false;
                }
                int li = 0;
                foreach (var entry in parsed.Value.EnumerateArray())
                {
                    if (!ValidateAttributePayload($"{attrName}[{li}]", entry, out error))
                        return false;
                    li++;
                }
                break;

            case AttributeValueTypes.StringSet:
            case AttributeValueTypes.NumberSet:
            case AttributeValueTypes.BinarySet:
                if (parsed.Value.ValueKind != JsonValueKind.Array)
                {
                    error =
                        $"{attrName} payload for type {parsed.TypeTag} must be a JSON array.";
                    return false;
                }
                if (parsed.Value.GetArrayLength() == 0)
                {
                    error = $"{attrName} set must not be empty.";
                    return false;
                }

                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in parsed.Value.EnumerateArray())
                {
                    if (member.ValueKind != JsonValueKind.String)
                    {
                        error =
                            $"{attrName} members of {parsed.TypeTag} must be JSON strings.";
                        return false;
                    }

                    var raw = member.GetString()!;
                    string canonical;
                    if (parsed.TypeTag == AttributeValueTypes.NumberSet)
                    {
                        if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                                raw,
                                out canonical,
                                out _,
                                out var setNumberError))
                        {
                            error =
                                $"{attrName} has an invalid Number set member: {setNumberError}";
                            return false;
                        }
                    }
                    else if (parsed.TypeTag == AttributeValueTypes.BinarySet)
                    {
                        if (!TryCanonicalizeBase64(raw, out canonical))
                        {
                            error =
                                $"{attrName} has a binary set member that is not valid base64.";
                            return false;
                        }
                    }
                    else
                    {
                        canonical = raw;
                    }

                    if (!unique.Add(canonical))
                    {
                        error = $"{attrName} set must not contain duplicate members.";
                        return false;
                    }
                }
                break;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidBase64(string value)
    {
        if (!TryDecodeBase64(value, out var bytes, out _))
        {
            return false;
        }

        ArrayPool<byte>.Shared.Return(bytes);
        return true;
    }

    private static bool TryCanonicalizeBase64(
        string value,
        out string canonical)
    {
        canonical = string.Empty;
        if (!TryDecodeBase64(value, out var bytes, out var written))
        {
            return false;
        }

        try
        {
            canonical = Convert.ToBase64String(bytes, 0, written);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static bool TryDecodeBase64(
        string value,
        out byte[] bytes,
        out int written)
    {
        var maximumLength = checked(((value.Length + 3) / 4) * 3);
        bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumLength));
        if (Convert.TryFromBase64Chars(value.AsSpan(), bytes, out written))
        {
            return true;
        }

        ArrayPool<byte>.Shared.Return(bytes);
        bytes = Array.Empty<byte>();
        written = 0;
        return false;
    }

    internal static bool ValidateKeyAttributesInItem(
        JsonElement item,
        TableMetadata meta,
        out string error)
    {
        foreach (var key in meta.KeySchema)
        {
            if (!item.TryGetProperty(key.Name, out var attribute))
            {
                error =
                    $"Item is missing required key attribute '{key.Name}'.";
                return false;
            }
            if (!ValidateItemKeyAttribute(
                    attribute,
                    meta,
                    key,
                    out error))
            {
                return false;
            }
        }

        if (!ValidatePresentSecondaryIndexKeys(
                item,
                meta,
                meta.GlobalSecondaryIndexes,
                out error)
            || !ValidatePresentSecondaryIndexKeys(
                item,
                meta,
                meta.LocalSecondaryIndexes,
                out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidatePresentSecondaryIndexKeys(
        JsonElement item,
        TableMetadata meta,
        List<TableIndexDefinition>? indexes,
        out string error)
    {
        if (indexes is null)
        {
            error = string.Empty;
            return true;
        }

        foreach (var index in indexes)
        {
            foreach (var key in index.KeySchema)
            {
                if (!item.TryGetProperty(key.Name, out var attribute))
                {
                    continue;
                }
                if (!ValidateItemKeyAttribute(
                        attribute,
                        meta,
                        key,
                        out error))
                {
                    error =
                        $"Secondary index '{index.IndexName}' {error}";
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateItemKeyAttribute(
        JsonElement attribute,
        TableMetadata meta,
        TableKeySchemaElement key,
        out string error)
    {
        if (!ItemKeyFormatter.TryGetDeclaredKeyType(
                meta,
                key.Name,
                out var declaredType))
        {
            error =
                $"key attribute '{key.Name}' is not declared in the table's AttributeDefinitions.";
            return false;
        }
        if (!AttributeValueTypes.IsScalarKeyType(declaredType))
        {
            error =
                $"key attribute '{key.Name}' has unsupported declared type '{declaredType}'; DynamoDB keys must use S, N, or B.";
            return false;
        }
        if (!ParsedAttributeValue.TryParse(attribute, out var parsed))
        {
            error =
                $"key attribute '{key.Name}' must be a single-property typed attribute value.";
            return false;
        }
        if (!string.Equals(
                parsed.TypeTag,
                declaredType,
                StringComparison.Ordinal))
        {
            error =
                $"key attribute '{key.Name}' has type {parsed.TypeTag} but the table declares {declaredType}.";
            return false;
        }
        if (parsed.Value.ValueKind != JsonValueKind.String)
        {
            error =
                $"key attribute '{key.Name}' value must be a JSON string per DynamoDB wire format.";
            return false;
        }

        var raw = parsed.Value.GetString()!;
        int byteCount;
        switch (declaredType)
        {
            case AttributeValueTypes.String:
                if (raw.Length == 0)
                {
                    error =
                        $"key attribute '{key.Name}' value must not be empty.";
                    return false;
                }
                try
                {
                    byteCount = StrictUtf8.GetByteCount(raw);
                }
                catch (EncoderFallbackException)
                {
                    error =
                        $"key attribute '{key.Name}' must contain valid Unicode scalar values.";
                    return false;
                }
                break;

            case AttributeValueTypes.Number:
                if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                        raw,
                        out _,
                        out _,
                        out var numberError))
                {
                    error =
                        $"key attribute '{key.Name}' has an invalid Number value: {numberError}";
                    return false;
                }
                byteCount = raw.Length;
                break;

            case AttributeValueTypes.Binary:
                if (!TryDecodeBase64(
                        raw,
                        out var bytes,
                        out byteCount))
                {
                    error =
                        $"key attribute '{key.Name}' binary value is not valid base64.";
                    return false;
                }
                ArrayPool<byte>.Shared.Return(bytes);
                if (byteCount == 0)
                {
                    error =
                        $"key attribute '{key.Name}' binary value must not be empty.";
                    return false;
                }
                break;

            default:
                error =
                    $"key attribute '{key.Name}' has unsupported type '{declaredType}'.";
                return false;
        }

        var isPartitionKey = string.Equals(
            key.KeyType,
            "HASH",
            StringComparison.OrdinalIgnoreCase);
        var isSortKey = string.Equals(
            key.KeyType,
            "RANGE",
            StringComparison.OrdinalIgnoreCase);
        if (!isPartitionKey && !isSortKey)
        {
            error =
                $"key attribute '{key.Name}' has unsupported key role '{key.KeyType}'.";
            return false;
        }

        var maximumBytes = isPartitionKey
            ? MaximumPartitionKeyBytes
            : MaximumSortKeyBytes;
        if (byteCount > maximumBytes)
        {
            error =
                $"key attribute '{key.Name}' is {byteCount} bytes and exceeds DynamoDB's {maximumBytes}-byte {(isPartitionKey ? "partition" : "sort")} key limit.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateKeyAttributesInKey(JsonElement key, TableMetadata meta, out string error)
    {
        if (key.ValueKind != JsonValueKind.Object)
        {
            error = "Key must be a JSON object.";
            return false;
        }
        foreach (var k in meta.KeySchema)
        {
            if (!key.TryGetProperty(k.Name, out var attr))
            {
                error = $"Key is missing required attribute '{k.Name}'.";
                return false;
            }
            if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
            {
                error = typeError;
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool HasContent(string? s) => !string.IsNullOrEmpty(s);
    private static bool HasContent(JsonElement? el)
    {
        if (el is not { } v) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Object => v.EnumerateObject().MoveNext(),
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrEmpty(v.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    private static bool IsAllowedReturnValuesForWrite(string? rv, out string error)
    {
        if (string.IsNullOrEmpty(rv) || rv == "NONE")
        {
            error = string.Empty;
            return true;
        }
        error = $"ReturnValues='{rv}' is not supported in this slice (only NONE).";
        return false;
    }

    private static bool IsAllowedRvccf(string? raw, out string canonical, out string error)
    {
        canonical = string.IsNullOrEmpty(raw) ? "NONE" : raw!;
        if (canonical is "NONE" or "ALL_OLD")
        {
            error = string.Empty;
            return true;
        }
        error = $"ReturnValuesOnConditionCheckFailure='{raw}' must be NONE or ALL_OLD.";
        return false;
    }

    private static IReadOnlyDictionary<string, string>? TryMaterialiseNames(JsonElement? el)
    {
        if (el is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in v.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                throw new ExpressionSyntaxException(0,
                    $"ExpressionAttributeNames['{prop.Name}'] must be a string.");
            dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, JsonElement>? TryMaterialiseValues(JsonElement? el)
    {
        if (el is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in v.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }
}
