using System;
using System.Collections.Generic;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class TableLifecycleHandlers
{
    private static bool ValidateAttributeDefinitions(
        List<AttributeDefinitionDto> attrs, out string error)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in attrs)
        {
            if (string.IsNullOrEmpty(a.AttributeName))
            {
                error = "AttributeDefinitions entries must include AttributeName.";
                return false;
            }
            if (a.AttributeType is not ("S" or "N" or "B"))
            {
                error = $"AttributeDefinitions[{a.AttributeName}].AttributeType must be one of S, N, B.";
                return false;
            }
            if (!seen.Add(a.AttributeName))
            {
                error = $"AttributeDefinitions contains duplicate AttributeName '{a.AttributeName}'.";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateKeyConsistency(
        List<KeySchemaElementDto> keys,
        List<AttributeDefinitionDto> attrs,
        out string error)
    {
        // Order matters: KeySchema[0] must be HASH, KeySchema[1] (if any) must be RANGE.
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (string.IsNullOrEmpty(k.AttributeName) || string.IsNullOrEmpty(k.KeyType))
            {
                error = "KeySchema entries must include AttributeName and KeyType.";
                return false;
            }
            var expected = i == 0 ? "HASH" : "RANGE";
            if (!string.Equals(k.KeyType, expected, StringComparison.Ordinal))
            {
                error = i == 0
                    ? "KeySchema[0].KeyType must be HASH."
                    : "KeySchema[1].KeyType must be RANGE.";
                return false;
            }
        }

        if (keys.Count == 2
            && string.Equals(keys[0].AttributeName, keys[1].AttributeName, StringComparison.Ordinal))
        {
            error = "KeySchema HASH and RANGE attributes must differ.";
            return false;
        }

        // Every base key must reference a declared attribute. The
        // reverse check (no orphan AttributeDefinitions) is deferred to
        // ValidateNoOrphanAttributeDefinitions so secondary-index key
        // attributes are also accepted.
        foreach (var k in keys)
        {
            bool found = false;
            foreach (var a in attrs)
            {
                if (string.Equals(a.AttributeName, k.AttributeName, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                error = $"KeySchema attribute '{k.AttributeName}' has no matching AttributeDefinition.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates that every declared <see cref="AttributeDefinitionDto"/> is
    /// referenced by either the base key schema or some secondary index key
    /// schema. DynamoDB rejects attribute definitions that no key consumes.
    /// </summary>
    private static bool ValidateNoOrphanAttributeDefinitions(
        List<KeySchemaElementDto> baseKeys,
        HashSet<string> indexKeyNames,
        List<AttributeDefinitionDto> attrs,
        out string error)
    {
        var referenced = new HashSet<string>(indexKeyNames, StringComparer.Ordinal);
        foreach (var k in baseKeys)
        {
            if (!string.IsNullOrEmpty(k.AttributeName)) referenced.Add(k.AttributeName!);
        }

        foreach (var a in attrs)
        {
            if (!referenced.Contains(a.AttributeName!))
            {
                error = $"AttributeDefinition '{a.AttributeName}' is not referenced by any "
                        + "key schema (base table or secondary index).";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates the GSI/LSI schemas and returns the set of attribute names
    /// referenced by any index key (so the orphan check can accept them).
    /// </summary>
    private static bool ValidateSecondaryIndexes(
        CreateTableRequest req,
        out HashSet<string> indexKeyNames,
        out string error)
    {
        indexKeyNames = new HashSet<string>(StringComparer.Ordinal);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in req.AttributeDefinitions!) declared.Add(a.AttributeName!);

        var baseHash = req.KeySchema![0].AttributeName!;
        string? baseRange = req.KeySchema.Count == 2 ? req.KeySchema[1].AttributeName : null;

        // Index names must be unique across both GSIs and LSIs.
        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        // INCLUDE non-key attributes are capped per-index and in aggregate.
        int totalNonKeyAttributes = 0;

        var lsis = req.LocalSecondaryIndexes;
        if (lsis is { Count: > 0 })
        {
            if (lsis.Count > MaxLocalSecondaryIndexes)
            {
                error = $"A table can have at most {MaxLocalSecondaryIndexes} local secondary indexes.";
                return false;
            }
            if (baseRange is null)
            {
                error = "LocalSecondaryIndexes require the table to have a composite (HASH + RANGE) key.";
                return false;
            }
            foreach (var lsi in lsis)
            {
                if (!ValidateIndexCommon(lsi, "LocalSecondaryIndex", declared, indexNames, indexKeyNames,
                        ref totalNonKeyAttributes, out var hash, out var range, out error))
                {
                    return false;
                }
                if (!string.Equals(hash, baseHash, StringComparison.Ordinal))
                {
                    error = $"LocalSecondaryIndex '{lsi.IndexName}' HASH key must match the table HASH key '{baseHash}'.";
                    return false;
                }
                if (range is null)
                {
                    error = $"LocalSecondaryIndex '{lsi.IndexName}' must declare a RANGE key.";
                    return false;
                }
                if (string.Equals(range, baseRange, StringComparison.Ordinal))
                {
                    error = $"LocalSecondaryIndex '{lsi.IndexName}' RANGE key must differ from the table RANGE key '{baseRange}'.";
                    return false;
                }
            }
        }

        var gsis = req.GlobalSecondaryIndexes;
        if (gsis is { Count: > 0 })
        {
            if (gsis.Count > MaxGlobalSecondaryIndexes)
            {
                error = $"A table can have at most {MaxGlobalSecondaryIndexes} global secondary indexes.";
                return false;
            }
            foreach (var gsi in gsis)
            {
                if (!ValidateIndexCommon(gsi, "GlobalSecondaryIndex", declared, indexNames, indexKeyNames,
                        ref totalNonKeyAttributes, out _, out _, out error))
                {
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateIndexCommon(
        SecondaryIndexDto idx,
        string kind,
        HashSet<string> declared,
        HashSet<string> indexNames,
        HashSet<string> indexKeyNames,
        ref int totalNonKeyAttributes,
        out string? hashName,
        out string? rangeName,
        out string error)
    {
        hashName = null;
        rangeName = null;

        if (!DynamoDbNames.IsValidIndexName(idx.IndexName))
        {
            error = $"{kind} IndexName must match [a-zA-Z0-9_.-]{{3,255}}.";
            return false;
        }
        if (!indexNames.Add(idx.IndexName!))
        {
            error = $"Duplicate index name '{idx.IndexName}'.";
            return false;
        }
        if (idx.KeySchema is null || idx.KeySchema.Count is < 1 or > 2)
        {
            error = $"{kind} '{idx.IndexName}' KeySchema must contain 1 (HASH) or 2 (HASH + RANGE) elements.";
            return false;
        }
        for (int i = 0; i < idx.KeySchema.Count; i++)
        {
            var k = idx.KeySchema[i];
            if (string.IsNullOrEmpty(k.AttributeName) || string.IsNullOrEmpty(k.KeyType))
            {
                error = $"{kind} '{idx.IndexName}' KeySchema entries must include AttributeName and KeyType.";
                return false;
            }
            var expected = i == 0 ? "HASH" : "RANGE";
            if (!string.Equals(k.KeyType, expected, StringComparison.Ordinal))
            {
                error = i == 0
                    ? $"{kind} '{idx.IndexName}' KeySchema[0].KeyType must be HASH."
                    : $"{kind} '{idx.IndexName}' KeySchema[1].KeyType must be RANGE.";
                return false;
            }
            if (!declared.Contains(k.AttributeName!))
            {
                error = $"{kind} '{idx.IndexName}' key attribute '{k.AttributeName}' has no matching AttributeDefinition.";
                return false;
            }
            indexKeyNames.Add(k.AttributeName!);
            if (i == 0) hashName = k.AttributeName; else rangeName = k.AttributeName;
        }
        if (rangeName is not null && string.Equals(hashName, rangeName, StringComparison.Ordinal))
        {
            error = $"{kind} '{idx.IndexName}' HASH and RANGE attributes must differ.";
            return false;
        }
        // DynamoDB requires the Projection member on every secondary index
        // (only ProjectionType inside it may be omitted, defaulting to ALL).
        if (idx.Projection is null)
        {
            error = $"{kind} '{idx.IndexName}' requires a Projection.";
            return false;
        }
        return ValidateProjection(idx.Projection, kind, idx.IndexName!, ref totalNonKeyAttributes, out error);
    }

    private static bool ValidateProjection(
        ProjectionDto? projection, string kind, string indexName,
        ref int totalNonKeyAttributes, out string error)
    {
        var type = projection?.ProjectionType;
        // ProjectionType defaults to ALL when omitted.
        if (string.IsNullOrEmpty(type))
        {
            error = string.Empty;
            return true;
        }
        if (type is not ("ALL" or "KEYS_ONLY" or "INCLUDE"))
        {
            error = $"{kind} '{indexName}' Projection.ProjectionType must be one of ALL, KEYS_ONLY, INCLUDE.";
            return false;
        }
        var nonKey = projection!.NonKeyAttributes;
        if (type == "INCLUDE")
        {
            if (nonKey is null || nonKey.Count == 0)
            {
                error = $"{kind} '{indexName}' Projection.NonKeyAttributes is required when ProjectionType is INCLUDE.";
                return false;
            }
            if (nonKey.Count > MaxIndexNonKeyAttributes)
            {
                error = $"{kind} '{indexName}' Projection.NonKeyAttributes cannot exceed {MaxIndexNonKeyAttributes} attributes.";
                return false;
            }
            foreach (var n in nonKey)
            {
                if (string.IsNullOrEmpty(n))
                {
                    error = $"{kind} '{indexName}' Projection.NonKeyAttributes entries must be non-empty.";
                    return false;
                }
                if (n.Length > MaxAttributeNameLength)
                {
                    error = $"{kind} '{indexName}' Projection.NonKeyAttributes names cannot exceed {MaxAttributeNameLength} characters.";
                    return false;
                }
            }
            totalNonKeyAttributes += nonKey.Count;
            if (totalNonKeyAttributes > MaxTotalIndexNonKeyAttributes)
            {
                error = $"The total number of INCLUDE non-key attributes across all secondary indexes cannot exceed {MaxTotalIndexNonKeyAttributes}.";
                return false;
            }
        }
        else if (nonKey is { Count: > 0 })
        {
            error = $"{kind} '{indexName}' Projection.NonKeyAttributes is only valid when ProjectionType is INCLUDE.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
