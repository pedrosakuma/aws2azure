namespace Aws2Azure.Modules.DynamoDb.Persistence;

/// <summary>
/// A precomputed secondary-index order-key property the write path appends to a
/// Cosmos document (#482): the reserved property name
/// (<c>_a2a$ord$&lt;attr&gt;</c>) and its order-preserving, digits-only numeric
/// value, both already UTF-8 encoded so the shared token walk emits them
/// verbatim for either the JSON-text or the CosmosBinary back-end. The value is
/// pure ASCII digits, so no JSON escaping is required.
/// </summary>
internal readonly struct OrderKeyField
{
    public OrderKeyField(byte[] propertyNameUtf8, byte[] valueUtf8)
    {
        PropertyNameUtf8 = propertyNameUtf8;
        ValueUtf8 = valueUtf8;
    }

    /// <summary>UTF-8 bytes of the reserved property name (unescaped).</summary>
    public byte[] PropertyNameUtf8 { get; }

    /// <summary>UTF-8 bytes of the order-preserving digits-only value.</summary>
    public byte[] ValueUtf8 { get; }
}
