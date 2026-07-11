using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class ItemHandlers
{
    /// <summary>
    /// Extracts the DDB attribute map from a Cosmos doc, projecting
    /// every non-reserved root property back into AttributeValue form
    /// via <see cref="InferredAttributeStorage.ExtractItem(Stream)"/>.
    /// </summary>
    internal static Dictionary<string, JsonElement>? ExtractItemFromCosmosDoc(Stream cosmosDocBody)
        => InferredAttributeStorage.ExtractItem(cosmosDocBody);

    internal static Dictionary<string, JsonElement>? ExtractItemFromCosmosDoc(ReadOnlyMemory<byte> cosmosDocBody)
    {
        using var doc = JsonDocument.Parse(cosmosDocBody);
        return InferredAttributeStorage.ExtractItem(doc.RootElement);
    }
}
