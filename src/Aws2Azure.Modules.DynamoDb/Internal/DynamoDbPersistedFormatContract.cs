namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Stable identities for proxy-owned DynamoDB persisted and client-held formats.
/// Versions advance independently; readers retain every version listed by the
/// published inventory for the supported adjacent-minor rollback span.
/// </summary>
internal static class DynamoDbPersistedFormatContract
{
    public const int InventoryVersion = 2;

    public const int LegacyItemEnvelopeVersion = 1;
    public const int CurrentItemDocumentVersion = 2;

    public const int TableMetadataVersion = 1;
    public const int DerivedFieldVersion = 1;
    public const int ContinuationVersion = 1;
    public const int OrderedContinuationVersion = 1;
    public const int StoredProcedureIdentityVersion = 2;

    public const string ContinuationSentinelAttribute = "__a2a_continuation";
    public const string OrderedContinuationDiscriminator = "a2acpob1";

    public const string AtomicWriteStoredProcedureId = "atomicWrite_v2";
    public const string LegacyAtomicTransactWriteStoredProcedureId = "atomicTransactWrite_v2";
    public const string AtomicTransactWriteStoredProcedureId = "atomicTransactWrite_v3";
    public const string AtomicTransactGetStoredProcedureId = "atomicTransactGet_v1";

    // Frozen by the v1 inventory. Never update these identities or hashes.
    public const string AtomicWriteBodySha256 =
        "68bb5745f1725ed43b2b06bf195cc34ffeb37c4b30fb56b446faf5444747b06a";
    public const string LegacyAtomicTransactWriteBodySha256 =
        "592335a445a63d7722f859955e1124ebff0f5c02a2ba038273e2f3d19c4cc5f1";

    // Frozen by the v2 inventory. Body changes require another new ID.
    public const string AtomicTransactWriteBodySha256 =
        "26d95f96fe3a00fa6e0dcd30b2df8c2b55a1d48cac22959ac489c253679d6541";
    public const string AtomicTransactGetBodySha256 =
        "355d2c74187d3d7c9c84b88f07992bca8954489e2e43f613a2934d978174db7b";
}
