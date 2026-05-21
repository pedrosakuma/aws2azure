namespace Aws2Azure.Modules.DynamoDb.WireProtocol;

/// <summary>
/// Operations recognised by the DynamoDB wire-protocol parser. The
/// enum spans the full DynamoDB 2012-08-10 surface even though Phase 3
/// implements only a subset per slice — every reachable
/// <c>X-Amz-Target</c> resolves to either a real op or a documented
/// stub so callers always get an SDK-shaped response.
/// </summary>
public enum DynamoDbOperation
{
    Unknown = 0,

    // Item-level
    GetItem,
    PutItem,
    UpdateItem,
    DeleteItem,
    BatchGetItem,
    BatchWriteItem,
    TransactGetItems,
    TransactWriteItems,

    // Query / Scan
    Query,
    Scan,

    // Table lifecycle
    CreateTable,
    DeleteTable,
    DescribeTable,
    ListTables,
    UpdateTable,

    // TTL
    DescribeTimeToLive,
    UpdateTimeToLive,

    // Tagging
    TagResource,
    UntagResource,
    ListTagsOfResource,

    // Streams / backups / global tables — stubbed; never mapped past Phase 3.
    DescribeStream,
    GetRecords,
    GetShardIterator,
    ListStreams,
    CreateBackup,
    DeleteBackup,
    DescribeBackup,
    ListBackups,
    RestoreTableFromBackup,
    RestoreTableToPointInTime,
}
