namespace Aws2Azure.Modules.S3;

/// <summary>
/// Operations recognised by the S3 module. New ops are appended as later
/// Phase-1 slices implement them.
/// </summary>
public enum S3Operation
{
    Unknown,
    Unsupported,
    ListBuckets,
    CreateBucket,
    DeleteBucket,
    HeadBucket,
    PutObject,
    GetObject,
    HeadObject,
    DeleteObject,
    ListObjects,
    ListObjectsV2,
}
