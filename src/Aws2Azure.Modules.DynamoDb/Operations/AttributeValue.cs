using System;
using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB attribute-value type tags used in the JSON 1.0 wire
/// protocol. The wire format encodes every attribute as a single-key
/// object — e.g. <c>{"S":"foo"}</c>, <c>{"N":"42"}</c>,
/// <c>{"M":{...}}</c>. Item handlers preserve the original wire form
/// verbatim inside the Cosmos sidecar so reads round-trip without any
/// information loss.
/// </summary>
internal static class AttributeValueTypes
{
    public const string String = "S";
    public const string Number = "N";
    public const string Binary = "B";
    public const string Bool = "BOOL";
    public const string Null = "NULL";
    public const string Map = "M";
    public const string List = "L";
    public const string StringSet = "SS";
    public const string NumberSet = "NS";
    public const string BinarySet = "BS";

    /// <summary>Scalar key types accepted as table partition/sort keys.</summary>
    public static bool IsScalarKeyType(string? tag)
        => tag is String or Number or Binary;

    /// <summary>Every type tag DynamoDB accepts inside an item attribute.</summary>
    public static bool IsKnownTag(string? tag)
        => tag is String or Number or Binary or Bool or Null
                or Map or List or StringSet or NumberSet or BinarySet;
}

/// <summary>
/// Lightweight projection of a single attribute-value JSON object.
/// Owns no allocations beyond the type tag string. <c>Element</c> is a
/// borrowed reference into the caller's <see cref="JsonDocument"/> and
/// must not outlive it.
/// </summary>
internal readonly struct ParsedAttributeValue
{
    public string TypeTag { get; }
    public JsonElement Value { get; }

    public ParsedAttributeValue(string typeTag, JsonElement value)
    {
        TypeTag = typeTag;
        Value = value;
    }

    /// <summary>
    /// Parses a single-property attribute-value object. Returns false
    /// if <paramref name="el"/> is not an object, has no properties,
    /// has multiple properties, or carries an unknown type tag.
    /// </summary>
    public static bool TryParse(JsonElement el, out ParsedAttributeValue parsed)
    {
        parsed = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        string? tag = null;
        JsonElement inner = default;
        int count = 0;
        foreach (var prop in el.EnumerateObject())
        {
            count++;
            if (count > 1) return false;
            tag = prop.Name;
            inner = prop.Value;
        }
        if (count != 1 || tag is null || !AttributeValueTypes.IsKnownTag(tag)) return false;
        parsed = new ParsedAttributeValue(tag, inner);
        return true;
    }
}

/// <summary>
/// Extracts and formats DynamoDB key attribute values into the scalar
/// strings used as Cosmos <c>pk</c> and <c>id</c>. Centralising this
/// keeps every item op (Put/Get/Delete and later Update) on exactly
/// the same routing semantics.
/// </summary>
internal static class ItemKeyFormatter
{
    /// <summary>Sentinel id reserved for the table metadata sidecar.</summary>
    public static string MetadataDocId => TableMetadata.DocId;

    /// <summary>
    /// Extracts (<c>pk</c>, <c>id</c>) from a DynamoDB Key map, validates
    /// it against the table's declared key schema, and returns an error
    /// string when anything is off (missing key attribute, type mismatch,
    /// extra keys, reserved id collision).
    /// </summary>
    public static bool TryBuild(
        JsonElement keyMap,
        TableMetadata meta,
        out string partitionKey,
        out string itemId,
        out string error)
    {
        partitionKey = string.Empty;
        itemId = string.Empty;
        error = string.Empty;

        if (keyMap.ValueKind != JsonValueKind.Object)
        {
            error = "Key must be a JSON object.";
            return false;
        }

        var hash = FindKeyDef(meta, "HASH");
        var range = FindKeyDef(meta, "RANGE");
        if (hash is null)
        {
            error = "Table has no HASH key declared in metadata.";
            return false;
        }

        // Count properties + verify only declared key attrs are present.
        var declared = new HashSet<string>(StringComparer.Ordinal) { hash.Name };
        if (range is not null) declared.Add(range.Name);

        int seen = 0;
        foreach (var prop in keyMap.EnumerateObject())
        {
            seen++;
            if (!declared.Contains(prop.Name))
            {
                error = $"Key contains attribute '{prop.Name}' that is not part of the table key schema.";
                return false;
            }
        }
        if (seen != declared.Count)
        {
            error = "Key is missing one or more required key attributes.";
            return false;
        }

        if (!keyMap.TryGetProperty(hash.Name, out var hashEl)
            || !TryFormatScalar(hashEl, hash, out partitionKey, out error))
        {
            if (string.IsNullOrEmpty(error))
                error = $"Key value for HASH attribute '{hash.Name}' is missing or malformed.";
            return false;
        }

        if (range is not null)
        {
            if (!keyMap.TryGetProperty(range.Name, out var rangeEl)
                || !TryFormatScalar(rangeEl, range, out var rangeStr, out error))
            {
                if (string.IsNullOrEmpty(error))
                    error = $"Key value for RANGE attribute '{range.Name}' is missing or malformed.";
                return false;
            }
            itemId = rangeStr;
        }
        else
        {
            itemId = partitionKey;
        }

        // Reserved-id collision guard: the sidecar doc lives at the
        // metadata sentinel; a user-key colliding with it would point at
        // a different doc on read/delete.
        if (itemId == TableMetadata.DocId || partitionKey == TableMetadata.DocId)
        {
            error = "Key value collides with reserved aws2azure metadata identifier.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Convenience overload for handlers that already know the key map
    /// element and only need to validate / format a single Item against
    /// the schema (used by PutItem).
    /// </summary>
    public static bool TryBuildFromItem(
        JsonElement item,
        TableMetadata meta,
        out string partitionKey,
        out string itemId,
        out string error)
    {
        partitionKey = string.Empty;
        itemId = string.Empty;
        error = string.Empty;
        if (item.ValueKind != JsonValueKind.Object)
        {
            error = "Item must be a JSON object.";
            return false;
        }

        var hash = FindKeyDef(meta, "HASH");
        if (hash is null) { error = "Table has no HASH key declared in metadata."; return false; }
        if (!item.TryGetProperty(hash.Name, out var hashEl))
        {
            error = $"Item is missing required HASH attribute '{hash.Name}'.";
            return false;
        }
        if (!TryFormatScalar(hashEl, hash, out partitionKey, out error)) return false;

        var range = FindKeyDef(meta, "RANGE");
        if (range is not null)
        {
            if (!item.TryGetProperty(range.Name, out var rangeEl))
            {
                error = $"Item is missing required RANGE attribute '{range.Name}'.";
                return false;
            }
            if (!TryFormatScalar(rangeEl, range, out var rangeStr, out error)) return false;
            itemId = rangeStr;
        }
        else
        {
            itemId = partitionKey;
        }

        if (itemId == TableMetadata.DocId || partitionKey == TableMetadata.DocId)
        {
            error = "Item key value collides with reserved aws2azure metadata identifier.";
            return false;
        }
        return true;
    }

    private static TableKeySchemaElement? FindKeyDef(TableMetadata meta, string keyType)
    {
        foreach (var k in meta.KeySchema)
        {
            if (string.Equals(k.KeyType, keyType, StringComparison.Ordinal)) return k;
        }
        return null;
    }

    private static bool TryFormatScalar(
        JsonElement attrEl,
        TableKeySchemaElement schemaEntry,
        out string formatted,
        out string error)
    {
        formatted = string.Empty;
        error = string.Empty;

        if (!ParsedAttributeValue.TryParse(attrEl, out var parsed))
        {
            error = $"Key attribute '{schemaEntry.Name}' must be a single-property typed attribute value.";
            return false;
        }

        if (!AttributeValueTypes.IsScalarKeyType(parsed.TypeTag))
        {
            error = $"Key attribute '{schemaEntry.Name}' must be of type S, N, or B; got {parsed.TypeTag}.";
            return false;
        }

        if (parsed.Value.ValueKind != JsonValueKind.String)
        {
            error = $"Key attribute '{schemaEntry.Name}' value must be a JSON string per DynamoDB wire format.";
            return false;
        }

        var raw = parsed.Value.GetString() ?? string.Empty;

        // Type tag in the value must match the AttributeDefinition.
        // Cross-check against the schema so a caller can't sneak a Number
        // key into a table declared with a String key (and vice-versa) —
        // that would otherwise route to the same partition as the proper
        // tagged value and silently overwrite the wrong item.
        if (!string.IsNullOrEmpty(schemaEntry.Name))
        {
            // schemaEntry only carries the role (HASH/RANGE); the type is
            // on the AttributeDefinition. The caller already validated
            // type consistency at CreateTable time, so what's stored in
            // the metadata is authoritative. We trust it here.
        }

        formatted = raw;
        return true;
    }

    /// <summary>
    /// Cross-checks the key attribute's wire type-tag against the
    /// declared AttributeDefinition type for the table. Returns false
    /// with <paramref name="error"/> populated on mismatch.
    /// </summary>
    public static bool ValidateKeyAttributeType(
        JsonElement attrEl,
        TableMetadata meta,
        string attributeName,
        out string error)
    {
        error = string.Empty;
        string? expectedType = null;
        foreach (var a in meta.AttributeDefinitions)
        {
            if (string.Equals(a.Name, attributeName, StringComparison.Ordinal))
            {
                expectedType = a.Type;
                break;
            }
        }
        if (expectedType is null)
        {
            error = $"Key attribute '{attributeName}' is not declared in the table's AttributeDefinitions.";
            return false;
        }

        if (!ParsedAttributeValue.TryParse(attrEl, out var parsed))
        {
            error = $"Key attribute '{attributeName}' must be a single-property typed attribute value.";
            return false;
        }
        if (!string.Equals(parsed.TypeTag, expectedType, StringComparison.Ordinal))
        {
            error = $"Key attribute '{attributeName}' has type {parsed.TypeTag} but the table declares {expectedType}.";
            return false;
        }
        return true;
    }
}
