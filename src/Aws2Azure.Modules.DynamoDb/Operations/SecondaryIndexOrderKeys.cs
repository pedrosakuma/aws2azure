using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Builds the per-item order-preserving numeric keys that let ordered secondary
/// index queries sort high-precision (<c>{"_a2a:N":…}</c> envelope) values
/// correctly (#482). For every Number-typed GSI or LSI sort attribute present in the
/// item as a valid <c>{"N":…}</c> value, emits an <see cref="OrderKeyField"/>
/// (<c>_a2a$ord$&lt;attr&gt;</c> → digits-only order key) that the write path
/// stores alongside the item; a Cosmos <c>ORDER BY c._a2a$ord$&lt;attr&gt;</c>
/// then compares them numerically where <c>ORDER BY c.&lt;attr&gt;</c> would
/// order the envelope objects structurally.
///
/// <para>Driven by the table's declared index schema
/// (<see cref="TableMetadata.NumericIndexSortKeys"/>), so it is a no-op — returns
/// <see langword="null"/>, allocating nothing — for the overwhelmingly common
/// table with no N-typed secondary-index sort key.</para>
/// </summary>
internal static class SecondaryIndexOrderKeys
{
    /// <summary>
    /// Computes the order-key fields for <paramref name="item"/> (a DynamoDB
    /// AttributeValue map). Returns <see langword="null"/> when the table
    /// declares no N-typed secondary-index sort key or none of them is present as a valid
    /// Number in the item (sparse index), so the write path appends nothing.
    /// </summary>
    public static OrderKeyField[]? Compute(TableMetadata meta, JsonElement item)
    {
        var specs = meta.NumericIndexSortKeys;
        if (specs.Count == 0 || item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        List<OrderKeyField>? fields = null;
        foreach (var spec in specs)
        {
            if (!item.TryGetProperty(spec.AttributeName, out var av)
                || av.ValueKind != JsonValueKind.Object
                || !av.TryGetProperty("N", out var nEl)
                || nEl.ValueKind != JsonValueKind.String)
            {
                // Attribute absent or not a Number value: the item is not
                // indexed on this sort key (sparse-index semantics), so no
                // order key is stored for it.
                continue;
            }

            var raw = nEl.GetString();
            if (raw is null
                || !KeyScalarCodec.TryEncodeNumberOrderKey(raw, out var encoded, out _))
            {
                // A malformed / out-of-range number would already have been
                // rejected by the attribute encoder on the main write; skip
                // defensively rather than double-report here.
                continue;
            }

            fields ??= new List<OrderKeyField>();
            fields.Add(new OrderKeyField(spec.OrderPropertyUtf8, Encoding.ASCII.GetBytes(encoded)));
        }

        return fields?.ToArray();
    }
}
