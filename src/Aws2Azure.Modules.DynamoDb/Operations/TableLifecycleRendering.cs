using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class TableLifecycleHandlers
{
    private static TableDescription BuildTableDescription(
        TableMetadata meta,
        string status,
        TableUsageMetrics? tableMetrics = null,
        IReadOnlyDictionary<string, SecondaryIndexLiveMetrics>? indexMetrics = null)
    {
        var attrs = new List<AttributeDefinitionDto>(meta.AttributeDefinitions.Count);
        foreach (var a in meta.AttributeDefinitions)
            attrs.Add(new AttributeDefinitionDto { AttributeName = a.Name, AttributeType = a.Type });
        var keys = new List<KeySchemaElementDto>(meta.KeySchema.Count);
        foreach (var k in meta.KeySchema)
            keys.Add(new KeySchemaElementDto { AttributeName = k.Name, KeyType = k.KeyType });

        return new TableDescription
        {
            TableName = meta.TableName,
            TableStatus = status,
            CreationDateTime = meta.CreationDateTime > 0 ? meta.CreationDateTime : null,
            AttributeDefinitions = attrs.Count > 0 ? attrs : null,
            KeySchema = keys.Count > 0 ? keys : null,
            ItemCount = tableMetrics?.ItemCount,
            TableSizeBytes = tableMetrics?.TableSizeBytes,
            TableArn = DynamoDbNames.BuildTableArn(string.Empty, meta.TableName),
            BillingModeSummary = string.IsNullOrEmpty(meta.BillingMode)
                ? null
                : new BillingModeSummary { BillingMode = meta.BillingMode },
            GlobalSecondaryIndexes = BuildIndexDescriptions(
                meta.TableName, meta.GlobalSecondaryIndexes, isGlobal: true, indexMetrics,
                zeroMetricsForEmptyUserTable: tableMetrics?.ItemCount == 0),
            LocalSecondaryIndexes = BuildIndexDescriptions(
                meta.TableName, meta.LocalSecondaryIndexes, isGlobal: false, indexMetrics,
                zeroMetricsForEmptyUserTable: tableMetrics?.ItemCount == 0),
        };
    }

    private static List<SecondaryIndexDescriptionDto>? BuildIndexDescriptions(
        string tableName,
        List<TableIndexDefinition>? indexes,
        bool isGlobal,
        IReadOnlyDictionary<string, SecondaryIndexLiveMetrics>? indexMetrics,
        bool zeroMetricsForEmptyUserTable)
    {
        if (indexes is null || indexes.Count == 0) return null;
        var dst = new List<SecondaryIndexDescriptionDto>(indexes.Count);
        foreach (var idx in indexes)
        {
            var keys = new List<KeySchemaElementDto>(idx.KeySchema.Count);
            foreach (var k in idx.KeySchema)
                keys.Add(new KeySchemaElementDto { AttributeName = k.Name, KeyType = k.KeyType });

            var liveMetrics = default(SecondaryIndexLiveMetrics);
            if (!(indexMetrics?.TryGetValue(idx.IndexName, out liveMetrics) ?? false)
                && zeroMetricsForEmptyUserTable)
            {
                liveMetrics = new SecondaryIndexLiveMetrics(0, 0);
            }
            dst.Add(new SecondaryIndexDescriptionDto
            {
                IndexName = idx.IndexName,
                KeySchema = keys.Count > 0 ? keys : null,
                Projection = new ProjectionDto
                {
                    ProjectionType = string.IsNullOrEmpty(idx.ProjectionType) ? "ALL" : idx.ProjectionType,
                    NonKeyAttributes = idx.NonKeyAttributes is { Count: > 0 } ? idx.NonKeyAttributes : null,
                },
                // GSIs carry a lifecycle status; LSIs do not (null is omitted by the JSON context).
                IndexStatus = isGlobal ? "ACTIVE" : null,
                ItemCount = liveMetrics.ItemCount,
                IndexSizeBytes = liveMetrics.IndexSizeBytes,
                IndexArn = DynamoDbNames.BuildIndexArn(string.Empty, tableName, idx.IndexName),
            });
        }
        return dst;
    }

    private static async Task<TableMetadata?> TryReadMetadataAsync(CosmosClient cosmos, string tableName, CancellationToken ct)
    {
        using var result = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, tableName, ct).ConfigureAwait(false);
        // Lifecycle handlers only need the metadata when present; they
        // already issue the authoritative container call separately and
        // surface 429/auth failures from that path. Treat any non-Found
        // outcome (NotFound or CosmosError) as "no sidecar available".
        return result.Status == CosmosOpsShared.TableMetadataReadStatus.Found ? result.Metadata : null;
    }

    private static TableUsageMetrics? TryReadTableUsageMetrics(
        HttpResponseMessage collResp,
        bool metadataDocumentPresent)
    {
        if (!TryGetHeaderValue(collResp, "x-ms-resource-usage", out var usageHeader)
            || string.IsNullOrWhiteSpace(usageHeader))
        {
            return null;
        }

        long? documentsCount = null;
        long? documentsSizeKb = null;
        long? collectionSizeKb = null;
        foreach (var segment in usageHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            var equals = trimmed.IndexOf('=');
            if (equals <= 0 || equals == trimmed.Length - 1)
            {
                continue;
            }

            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            if (key.Equals("documentsCount", StringComparison.OrdinalIgnoreCase))
            {
                documentsCount = parsed;
            }
            else if (key.Equals("documentsSize", StringComparison.OrdinalIgnoreCase))
            {
                documentsSizeKb = parsed;
            }
            else if (key.Equals("collectionSize", StringComparison.OrdinalIgnoreCase))
            {
                collectionSizeKb = parsed;
            }
        }

        long? itemCount = null;
        if (documentsCount.HasValue)
        {
            itemCount = documentsCount.Value - (metadataDocumentPresent ? 1L : 0L);
            if (itemCount < 0)
            {
                itemCount = 0;
            }
        }

        long? tableSizeBytes = null;
        if (itemCount == 0)
        {
            tableSizeBytes = 0;
        }
        else if (documentsSizeKb.HasValue)
        {
            tableSizeBytes = KiBToBytes(documentsSizeKb.Value);
        }
        else if (collectionSizeKb.HasValue)
        {
            tableSizeBytes = KiBToBytes(collectionSizeKb.Value);
        }

        return itemCount.HasValue || tableSizeBytes.HasValue
            ? new TableUsageMetrics(itemCount, tableSizeBytes)
            : null;
    }

    private static Task<Dictionary<string, SecondaryIndexLiveMetrics>?> TryReadSecondaryIndexMetricsAsync(
        CosmosClient cosmos,
        TableMetadata meta,
        TableUsageMetrics? tableMetrics,
        CancellationToken ct)
    {
        if ((meta.GlobalSecondaryIndexes is not { Count: > 0 })
            && (meta.LocalSecondaryIndexes is not { Count: > 0 }))
        {
            return Task.FromResult<Dictionary<string, SecondaryIndexLiveMetrics>?>(null);
        }

        var metrics = new Dictionary<string, SecondaryIndexLiveMetrics>(StringComparer.Ordinal);
        if (tableMetrics?.ItemCount == 0)
        {
            AddZeroMetrics(meta.GlobalSecondaryIndexes, metrics);
            AddZeroMetrics(meta.LocalSecondaryIndexes, metrics);
            return Task.FromResult<Dictionary<string, SecondaryIndexLiveMetrics>?>(metrics.Count > 0 ? metrics : null);
        }

        _ = cosmos;
        _ = ct;
        return Task.FromResult<Dictionary<string, SecondaryIndexLiveMetrics>?>(null);
    }

    private static void AddZeroMetrics(
        List<TableIndexDefinition>? indexes,
        Dictionary<string, SecondaryIndexLiveMetrics> metrics)
    {
        if (indexes is null)
        {
            return;
        }

        foreach (var index in indexes)
        {
            metrics[index.IndexName] = new SecondaryIndexLiveMetrics(0, 0);
        }
    }

    private static IReadOnlyDictionary<string, SecondaryIndexLiveMetrics>? BuildCreateTableIndexMetrics(TableMetadata meta)
    {
        if ((meta.GlobalSecondaryIndexes is not { Count: > 0 })
            && (meta.LocalSecondaryIndexes is not { Count: > 0 }))
        {
            return null;
        }

        var metrics = new Dictionary<string, SecondaryIndexLiveMetrics>(StringComparer.Ordinal);
        AddZeroMetrics(meta.GlobalSecondaryIndexes, metrics);
        AddZeroMetrics(meta.LocalSecondaryIndexes, metrics);
        return metrics;
    }

    private static bool TryGetHeaderValue(HttpResponseMessage response, string name, out string? value)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (var candidate in values)
            {
                value = candidate;
                return true;
            }
        }

        if (response.Content is not null && response.Content.Headers.TryGetValues(name, out values))
        {
            foreach (var candidate in values)
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static long KiBToBytes(long kibibytes)
    {
        try
        {
            return checked(kibibytes * 1024L);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    internal static List<string> ParseContainerNames(Stream cosmosListBody)
    {
        var names = new List<string>();
        ParseContainerNamesInto(cosmosListBody, names);
        return names;
    }

    internal static void ParseContainerNamesInto(Stream cosmosListBody, List<string> names)
    {
        // Cosmos returns: { "_rid":"...", "DocumentCollections":[ {"id":"name", ...}, ... ], "_count":N }
        using var doc = JsonDocument.Parse(cosmosListBody);
        if (!doc.RootElement.TryGetProperty("DocumentCollections", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                var id = idEl.GetString();
                if (!string.IsNullOrEmpty(id)) names.Add(id);
            }
        }
    }

    private readonly record struct TableUsageMetrics(long? ItemCount, long? TableSizeBytes);
    private readonly record struct SecondaryIndexLiveMetrics(long? ItemCount, long? IndexSizeBytes);
}
