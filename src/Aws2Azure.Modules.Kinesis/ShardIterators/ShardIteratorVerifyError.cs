namespace Aws2Azure.Modules.Kinesis.ShardIterators;

public enum ShardIteratorVerifyError
{
    None = 0,
    MalformedFormat = 1,
    MalformedPayload = 2,
    BadSignature = 3,
    Expired = 4,
}
