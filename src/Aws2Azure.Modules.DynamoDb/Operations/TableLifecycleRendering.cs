using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class TableLifecycleHandlers
{
    private static TableDescription BuildTableDescription(TableMetadata meta, string status)
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
            TableArn = DynamoDbNames.BuildTableArn(string.Empty, meta.TableName),
            BillingModeSummary = string.IsNullOrEmpty(meta.BillingMode)
                ? null
                : new BillingModeSummary { BillingMode = meta.BillingMode },
            GlobalSecondaryIndexes = BuildIndexDescriptions(meta.TableName, meta.GlobalSecondaryIndexes, isGlobal: true),
            LocalSecondaryIndexes = BuildIndexDescriptions(meta.TableName, meta.LocalSecondaryIndexes, isGlobal: false),
        };
    }

    private static List<SecondaryIndexDescriptionDto>? BuildIndexDescriptions(
        string tableName, List<TableIndexDefinition>? indexes, bool isGlobal)
    {
        if (indexes is null || indexes.Count == 0) return null;
        var dst = new List<SecondaryIndexDescriptionDto>(indexes.Count);
        foreach (var idx in indexes)
        {
            var keys = new List<KeySchemaElementDto>(idx.KeySchema.Count);
            foreach (var k in idx.KeySchema)
                keys.Add(new KeySchemaElementDto { AttributeName = k.Name, KeyType = k.KeyType });

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
}
