namespace Aws2Azure.Modules.Kinesis.WireProtocol;

/// <summary>
/// Operations recognised by the Kinesis wire-protocol parser. The
/// enum spans the Phase 4 scope (data plane + metadata). Operations
/// outside this set (e.g. <c>CreateStream</c>, enhanced fan-out,
/// resource policies) resolve to <see cref="Unknown"/> and surface as
/// <c>UnknownOperationException</c>.
/// </summary>
public enum KinesisOperation
{
    Unknown = 0,

    // Data plane
    PutRecord,
    PutRecords,
    GetRecords,
    GetShardIterator,

    // Metadata
    DescribeStream,
    DescribeStreamSummary,
    ListShards,
}
