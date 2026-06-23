using System;
using System.Collections.Generic;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Shared resolution of a DynamoDB <c>IndexName</c> against a table's
/// secondary-index schemas, used by both <see cref="QueryHandler"/> and
/// <see cref="ScanHandler"/>. Only Local Secondary Index (LSI) access is
/// supported in the current slice; Global Secondary Index (GSI) and unknown
/// indices are surfaced to the caller for a loud rejection.
/// </summary>
internal static class SecondaryIndexResolver
{
    internal enum IndexResolution
    {
        Lsi,
        Gsi,
        NotFound,
    }

    /// <summary>
    /// Resolves <paramref name="indexName"/> against the table's secondary
    /// index schemas. Returns <see cref="IndexResolution.Lsi"/> (with
    /// <paramref name="lsi"/> set), <see cref="IndexResolution.Gsi"/>, or
    /// <see cref="IndexResolution.NotFound"/>.
    /// </summary>
    internal static IndexResolution ResolveIndex(
        TableMetadata meta, string indexName, out TableIndexDefinition? lsi)
    {
        lsi = null;
        if (meta.LocalSecondaryIndexes is { } lsis)
        {
            foreach (var ix in lsis)
            {
                if (string.Equals(ix.IndexName, indexName, StringComparison.Ordinal))
                {
                    lsi = ix;
                    return IndexResolution.Lsi;
                }
            }
        }
        if (meta.GlobalSecondaryIndexes is { } gsis)
        {
            foreach (var ix in gsis)
            {
                if (string.Equals(ix.IndexName, indexName, StringComparison.Ordinal))
                    return IndexResolution.Gsi;
            }
        }
        return IndexResolution.NotFound;
    }

    /// <summary>
    /// Extracts the LSI's alternate sort attribute (the RANGE key-schema
    /// element) and its declared scalar type from the table's
    /// AttributeDefinitions.
    /// </summary>
    internal static bool TryGetLsiSortKey(
        TableMetadata meta, TableIndexDefinition lsi,
        out string sortName, out string sortType, out string error)
    {
        sortName = string.Empty;
        sortType = string.Empty;
        error = string.Empty;
        foreach (var k in lsi.KeySchema)
        {
            if (string.Equals(k.KeyType, "RANGE", StringComparison.OrdinalIgnoreCase))
            {
                sortName = k.Name;
                break;
            }
        }
        if (string.IsNullOrEmpty(sortName))
        {
            error = $"Local secondary index '{lsi.IndexName}' declares no RANGE key.";
            return false;
        }
        if (!ItemKeyFormatter.TryGetDeclaredKeyType(meta, sortName, out sortType))
        {
            error = $"Index sort key '{sortName}' is not declared in the table's AttributeDefinitions.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves the in-process projection paths for an index's
    /// <c>ALL_PROJECTED_ATTRIBUTES</c> projection: <c>ALL</c> → null (all
    /// attributes); <c>KEYS_ONLY</c> → base HASH + base RANGE + index sort
    /// attribute; <c>INCLUDE</c> → those keys plus the index's
    /// NonKeyAttributes.
    /// </summary>
    internal static IReadOnlyList<string>? ResolveIndexProjection(
        TableMetadata meta, TableIndexDefinition lsi, string lsiSortName)
    {
        if (string.Equals(lsi.ProjectionType, "ALL", StringComparison.OrdinalIgnoreCase))
            return null;

        var paths = new List<string>();
        void Add(string? name)
        {
            if (!string.IsNullOrEmpty(name) && !paths.Contains(name)) paths.Add(name!);
        }

        foreach (var k in meta.KeySchema) Add(k.Name);
        Add(lsiSortName);

        if (string.Equals(lsi.ProjectionType, "INCLUDE", StringComparison.OrdinalIgnoreCase)
            && lsi.NonKeyAttributes is { } extra)
        {
            foreach (var a in extra) Add(a);
        }
        return paths;
    }
}
