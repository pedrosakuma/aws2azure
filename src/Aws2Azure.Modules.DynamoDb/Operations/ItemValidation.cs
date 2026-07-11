using System;
using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class ItemHandlers
{
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

    /// <summary>
    /// Recursive shape validator for a single DDB AttributeValue. Mirrors
    /// the type discipline the inferred encoder relies on, so any
    /// rejection here surfaces as a client <c>ValidationException</c>
    /// instead of an encoder <c>ArgumentException</c> deeper down the
    /// stack. Number / Binary / Set payloads must be strings; sets are
    /// arrays of strings; maps recurse; lists recurse.
    /// </summary>
    private static bool ValidateAttributePayload(string attrName, JsonElement attr, out string error)
    {
        if (!ParsedAttributeValue.TryParse(attr, out var parsed))
        {
            error = $"Attribute '{attrName}' must be a single-property typed attribute value.";
            return false;
        }

        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
            case AttributeValueTypes.Number:
            case AttributeValueTypes.Binary:
                if (parsed.Value.ValueKind != JsonValueKind.String)
                {
                    error = $"Attribute '{attrName}' payload for type {parsed.TypeTag} must be a JSON string.";
                    return false;
                }
                break;

            case AttributeValueTypes.Bool:
                if (parsed.Value.ValueKind != JsonValueKind.True && parsed.Value.ValueKind != JsonValueKind.False)
                {
                    error = $"Attribute '{attrName}' payload for type BOOL must be a JSON boolean.";
                    return false;
                }
                break;

            case AttributeValueTypes.Null:
                if (parsed.Value.ValueKind != JsonValueKind.True)
                {
                    error = $"Attribute '{attrName}' payload for type NULL must be the literal true.";
                    return false;
                }
                break;

            case AttributeValueTypes.Map:
                if (parsed.Value.ValueKind != JsonValueKind.Object)
                {
                    error = $"Attribute '{attrName}' payload for type M must be a JSON object.";
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
                        error = $"Attribute '{attrName}.{entry.Name}' uses the reserved '"
                            + InferredAttributeStorage.EnvelopeTagPrefix
                            + "' prefix.";
                        return false;
                    }
                    if (!ValidateAttributePayload($"{attrName}.{entry.Name}", entry.Value, out error))
                        return false;
                }
                break;

            case AttributeValueTypes.List:
                if (parsed.Value.ValueKind != JsonValueKind.Array)
                {
                    error = $"Attribute '{attrName}' payload for type L must be a JSON array.";
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
                    error = $"Attribute '{attrName}' payload for type {parsed.TypeTag} must be a JSON array.";
                    return false;
                }
                foreach (var member in parsed.Value.EnumerateArray())
                {
                    if (member.ValueKind != JsonValueKind.String)
                    {
                        error = $"Attribute '{attrName}' members of {parsed.TypeTag} must be JSON strings.";
                        return false;
                    }
                }
                break;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateKeyAttributesInItem(JsonElement item, TableMetadata meta, out string error)
    {
        foreach (var k in meta.KeySchema)
        {
            if (!item.TryGetProperty(k.Name, out var attr))
            {
                error = $"Item is missing required key attribute '{k.Name}'.";
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
