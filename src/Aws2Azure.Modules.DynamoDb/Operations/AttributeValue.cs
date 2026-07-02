using System;
using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

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
            || !TryFormatScalar(hashEl, hash, meta, out partitionKey, out error))
        {
            if (string.IsNullOrEmpty(error))
                error = $"Key value for HASH attribute '{hash.Name}' is missing or malformed.";
            return false;
        }

        if (range is not null)
        {
            if (!keyMap.TryGetProperty(range.Name, out var rangeEl)
                || !TryFormatScalar(rangeEl, range, meta, out var rangeStr, out error))
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
        if (!TryFormatScalar(hashEl, hash, meta, out partitionKey, out error)) return false;

        var range = FindKeyDef(meta, "RANGE");
        if (range is not null)
        {
            if (!item.TryGetProperty(range.Name, out var rangeEl))
            {
                error = $"Item is missing required RANGE attribute '{range.Name}'.";
                return false;
            }
            if (!TryFormatScalar(rangeEl, range, meta, out var rangeStr, out error)) return false;
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

    /// <summary>
    /// Resolves the declared AttributeDefinition type for a key attribute
    /// and formats its value into the Cosmos routing scalar via
    /// <see cref="KeyScalarCodec"/>. Encoding is chosen by the *declared*
    /// schema type (not the wire tag) so a mismatched tag can never route
    /// to a different partition/id — the codec rejects it instead.
    /// </summary>
    private static bool TryFormatScalar(
        JsonElement attrEl,
        TableKeySchemaElement schemaEntry,
        TableMetadata meta,
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

        string? declaredType = null;
        foreach (var a in meta.AttributeDefinitions)
        {
            if (string.Equals(a.Name, schemaEntry.Name, StringComparison.Ordinal))
            {
                declaredType = a.Type;
                break;
            }
        }
        if (declaredType is null)
        {
            error = $"Key attribute '{schemaEntry.Name}' is not declared in the table's AttributeDefinitions.";
            return false;
        }

        return KeyScalarCodec.TryEncode(declaredType, parsed, schemaEntry.Name, out formatted, out error);
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

    /// <summary>
    /// Looks up the declared AttributeDefinition type (S/N/B) for a key
    /// attribute. Returns false when the attribute is not declared.
    /// </summary>
    public static bool TryGetDeclaredKeyType(TableMetadata meta, string attributeName, out string declaredType)
    {
        declaredType = string.Empty;
        foreach (var a in meta.AttributeDefinitions)
        {
            if (string.Equals(a.Name, attributeName, StringComparison.Ordinal))
            {
                declaredType = a.Type;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Encodes a DynamoDB key attribute value into the scalar string used as
/// the Cosmos <c>id</c> / partition-key, applying an <b>order-preserving,
/// Cosmos-safe</b> transform chosen by the table's declared key type:
///
/// <list type="bullet">
///   <item><b>S</b> → lowercase hex of the UTF-8 bytes. DynamoDB sorts
///     strings by UTF-8 byte order; fixed-width hex preserves that order
///     lexically and is prefix-preserving on byte boundaries (so
///     <c>begins_with</c> maps to <c>STARTSWITH</c> exactly). Because the
///     id becomes pure ASCII hex, Cosmos' native string collation no
///     longer influences <c>ORDER BY c.id</c>.</item>
///   <item><b>B</b> → lowercase hex of the raw bytes (the wire value is
///     base64; it is decoded first). DynamoDB sorts binary by unsigned
///     byte order, which hex preserves — fixing the previous base64
///     ordering bug.</item>
///   <item><b>N</b> → order-preserving digits-only key (sign flag +
///     fixed-width biased decimal exponent + fixed-width mantissa, with the
///     negative branch nine's-complemented). Numeric order is preserved for
///     <c>c.id</c> range / BETWEEN / ORDER BY, and numerically-equal values
///     (<c>42</c>, <c>42.0</c>, <c>4.2e1</c>) collapse to one id. See
///     <see cref="KeyScalarCodec"/>'s numeric helpers for the exact layout.</item>
/// </list>
///
/// <para>Encoding is driven by the <i>declared</i> schema type rather than
/// the request's wire tag, and the wire tag must match — so a
/// <c>{"B":"YQ=="}</c> value can never collide with a <c>{"S":"a"}</c>
/// value (both would otherwise hex to <c>61</c>) on a table whose key is
/// declared as one specific type.</para>
/// </summary>
internal static class KeyScalarCodec
{
    // Strict UTF-8: throw rather than silently substitute U+FFFD for any
    // malformed input. System.Text.Json already guarantees valid Unicode
    // for a parsed string, so this is defence-in-depth against collisions.
    private static readonly System.Text.Encoding StrictUtf8 =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Cosmos hard limit on document id length (characters).
    /// </summary>
    private const int MaxCosmosIdChars = 255;

    /// <summary>
    /// Encodes <paramref name="parsed"/> (whose wire tag must equal
    /// <paramref name="declaredType"/>) into the routing scalar. Returns
    /// false with <paramref name="error"/> populated on type mismatch,
    /// non-string payload, empty value, invalid base64 (for B), an
    /// unsafe character (for N), or an encoded length exceeding Cosmos'
    /// id limit.
    /// </summary>
    public static bool TryEncode(
        string declaredType,
        ParsedAttributeValue parsed,
        string attributeName,
        out string encoded,
        out string error)
    {
        encoded = string.Empty;
        error = string.Empty;

        if (!string.Equals(parsed.TypeTag, declaredType, StringComparison.Ordinal))
        {
            error = $"Key attribute '{attributeName}' has type {parsed.TypeTag} but the table declares {declaredType}.";
            return false;
        }
        if (parsed.Value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            error = $"Key attribute '{attributeName}' value must be a JSON string per DynamoDB wire format.";
            return false;
        }

        var raw = parsed.Value.GetString() ?? string.Empty;
        if (raw.Length == 0)
        {
            error = $"Key attribute '{attributeName}' value must not be empty.";
            return false;
        }

        switch (declaredType)
        {
            case AttributeValueTypes.String:
                encoded = Convert.ToHexStringLower(StrictUtf8.GetBytes(raw));
                break;

            case AttributeValueTypes.Binary:
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(raw);
                }
                catch (FormatException)
                {
                    error = $"Key attribute '{attributeName}' binary value is not valid base64.";
                    return false;
                }
                if (bytes.Length == 0)
                {
                    error = $"Key attribute '{attributeName}' binary value must not be empty.";
                    return false;
                }
                encoded = Convert.ToHexStringLower(bytes);
                break;

            case AttributeValueTypes.Number:
                // Encode into an order-preserving, digits-only string so
                // sort-key range / BETWEEN and ORDER BY c.id compare in
                // numeric order, and so numerically-equal values
                // (42, 42.0, 4.2e1) collapse to the same id/partition-key.
                if (!InferredAttributeStorage.TryParseDdbNumber(raw, out var numParts, out var numError))
                {
                    error = $"Key attribute '{attributeName}' has an invalid Number value: {numError}";
                    return false;
                }
                encoded = EncodeNumberKey(numParts);
                break;

            default:
                error = $"Key attribute '{attributeName}' has unsupported key type '{declaredType}'.";
                return false;
        }

        if (encoded.Length > MaxCosmosIdChars)
        {
            error = $"Key attribute '{attributeName}' encoded value exceeds the {MaxCosmosIdChars}-character Cosmos id limit.";
            return false;
        }
        return true;
    }

    // ---- Order-preserving numeric (N) key encoding -------------------
    //
    // Layout (digits-only so Cosmos string collation is irrelevant, the
    // same property the S/B hex path relies on):
    //   zero      -> "1"
    //   positive  -> '2' + dec3(E + Bias)        + rightPad(D,  '0', 38)
    //   negative  -> '0' + dec3(255 - (E + Bias)) + rightPad(comp(D), '9', 38)
    // where E = msdExponent in [-130,125], Bias = 130 so (E + Bias) in
    // [0,255]; D = significant digits MSD-first; comp(d) = '9' - (d - '0').
    //
    // Ordering proof sketch:
    //  * Sign flags '0' < '1' < '2'  => negatives < zero < positives.
    //  * Positive: the fixed-width offset-decimal exponent field dominates
    //    (larger E = larger magnitude); within equal E the right-'0'-padded
    //    mantissa compares ordinally == numeric mantissa order. Equal
    //    numbers share one canonical (E, D) so they encode identically.
    //  * Negative: both fields are inverted — the exponent via 255-offset
    //    (larger magnitude sorts first/smaller) and the mantissa via the
    //    nine's-complement with '9' padding. Fixed width removes any
    //    prefix ambiguity, so no terminator is needed.
    // begins_with on a Number sort key is invalid in DynamoDB (rejected in
    // KeyConditionAnalyser), so prefix-preservation is not required.

    private const int NumberMantissaWidth = 38; // == max DDB significant digits
    private const int NumberExponentBias = 130;  // msdExponent in [-130,125]

    /// <summary>
    /// Encodes a raw DDB Number string into the order-preserving, digits-only
    /// key used for secondary-index <c>ORDER BY</c> (#482) — the same encoding
    /// the base-table numeric RANGE key already applies to the Cosmos <c>id</c>
    /// (see <see cref="EncodeNumberKey"/>). Because the result is pure digits,
    /// lexical string order equals numeric order across the bare/envelope
    /// storage boundary, so a per-partition <c>ORDER BY c._a2a$ord$&lt;attr&gt;</c>
    /// sorts high-precision (<c>{"_a2a:N":…}</c> envelope) values correctly where
    /// <c>ORDER BY c.&lt;attr&gt;</c> would order them as objects. Returns
    /// <see langword="false"/> with <paramref name="error"/> populated on a
    /// malformed or out-of-range number.
    /// </summary>
    public static bool TryEncodeNumberOrderKey(string raw, out string encoded, out string error)
    {
        encoded = string.Empty;
        if (!InferredAttributeStorage.TryParseDdbNumber(raw, out var parts, out error))
        {
            return false;
        }
        encoded = EncodeNumberKey(parts);
        return true;
    }

    private static string EncodeNumberKey(in InferredAttributeStorage.DdbNumberParts parts)
    {
        if (parts.IsZero) return "1";

        int offsetE = parts.MsdExponent + NumberExponentBias; // [0,255] for valid input
        var sb = new System.Text.StringBuilder(1 + 3 + NumberMantissaWidth);
        if (!parts.Negative)
        {
            sb.Append('2');
            AppendDec3(sb, offsetE);
            AppendMantissa(sb, parts.Digits, parts.SignificantDigits, pad: '0', complement: false);
        }
        else
        {
            sb.Append('0');
            AppendDec3(sb, 255 - offsetE);
            AppendMantissa(sb, parts.Digits, parts.SignificantDigits, pad: '9', complement: true);
        }
        return sb.ToString();
    }

    // Fixed 3-digit decimal (MSD-first) of a value in [0,255]; order-preserving.
    private static void AppendDec3(System.Text.StringBuilder sb, int v)
    {
        sb.Append((char)('0' + (v / 100) % 10));
        sb.Append((char)('0' + (v / 10) % 10));
        sb.Append((char)('0' + v % 10));
    }

    private static void AppendMantissa(
        System.Text.StringBuilder sb, string digits, int count, char pad, bool complement)
    {
        for (int k = 0; k < NumberMantissaWidth; k++)
        {
            if (k < count)
            {
                char d = digits[k];
                sb.Append(complement ? (char)('0' + ('9' - d)) : d);
            }
            else
            {
                sb.Append(pad);
            }
        }
    }
}
