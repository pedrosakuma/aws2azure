namespace Aws2Azure.Modules.Kinesis.ShardIterators;

public enum ShardIteratorType
{
    TrimHorizon = 0,
    Latest = 1,
    AtSequenceNumber = 2,
    AfterSequenceNumber = 3,
    AtTimestamp = 4,
}
