namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Stable identities for proxy-owned DynamoDB persisted and client-held formats.
/// Versions advance independently; readers retain every version listed by the
/// published inventory for the supported adjacent-minor rollback span.
/// </summary>
internal static class DynamoDbPersistedFormatContract
{
    public const int InventoryVersion = 1;

    public const int LegacyItemEnvelopeVersion = 1;
    public const int CurrentItemDocumentVersion = 2;

    public const int TableMetadataVersion = 1;
    public const int DerivedFieldVersion = 1;
    public const int ContinuationVersion = 1;
    public const int OrderedContinuationVersion = 1;
    public const int StoredProcedureIdentityVersion = 1;

    public const string ContinuationSentinelAttribute = "__a2a_continuation";
    public const string OrderedContinuationDiscriminator = "a2acpob1";

    public const string AtomicWriteStoredProcedureId = "atomicWrite_v2";
    public const string AtomicTransactWriteStoredProcedureId = "atomicTransactWrite_v2";

    // Frozen by the v1 inventory. If a body changes, provision a new ID and
    // publish a new identity-set version rather than updating these in place.
    public const string AtomicWriteBodySha256 =
        "68bb5745f1725ed43b2b06bf195cc34ffeb37c4b30fb56b446faf5444747b06a";
    public const string AtomicTransactWriteBodySha256 =
        "592335a445a63d7722f859955e1124ebff0f5c02a2ba038273e2f3d19c4cc5f1";
}
