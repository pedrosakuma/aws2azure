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
    DeleteObjects,
    CopyObject,
    ListObjects,
    ListObjectsV2,
    ListObjectVersions,
    CreateMultipartUpload,
    UploadPart,
    CompleteMultipartUpload,
    AbortMultipartUpload,
    ListParts,
    ListMultipartUploads,
    UploadPartCopy,

    // ───────── Phase 1 Slice 9 — long-tail stubs ─────────
    // Tagging (real translation: object → blob index tags; bucket → container metadata)
    GetObjectTagging,
    PutObjectTagging,
    DeleteObjectTagging,
    GetBucketTagging,
    PutBucketTagging,
    DeleteBucketTagging,

    // ACL (ownership-only stubs)
    GetBucketAcl,
    PutBucketAcl,
    GetObjectAcl,
    PutObjectAcl,

    // Bucket-scoped configuration stubs.
    // GET → NoSuch* 404; PUT → NotImplemented; DELETE → 204 (idempotent)
    GetBucketLifecycleConfiguration,
    PutBucketLifecycleConfiguration,
    DeleteBucketLifecycle,
    GetBucketCors,
    PutBucketCors,
    DeleteBucketCors,
    GetBucketWebsite,
    PutBucketWebsite,
    DeleteBucketWebsite,
    GetBucketReplication,
    PutBucketReplication,
    DeleteBucketReplication,
    GetBucketEncryption,
    PutBucketEncryption,
    DeleteBucketEncryption,
    GetBucketLogging,
    PutBucketLogging,
    GetBucketVersioning,
    PutBucketVersioning,
    GetBucketRequestPayment,
    PutBucketRequestPayment,
    GetObjectLockConfiguration,
    PutObjectLockConfiguration,
    GetPublicAccessBlock,
    PutPublicAccessBlock,
    DeletePublicAccessBlock,
    GetBucketPolicy,
    PutBucketPolicy,
    DeleteBucketPolicy,
    GetBucketPolicyStatus,
    GetBucketNotificationConfiguration,
    PutBucketNotificationConfiguration,
    GetBucketAccelerateConfiguration,
    PutBucketAccelerateConfiguration,
    GetBucketOwnershipControls,
    PutBucketOwnershipControls,
    DeleteBucketOwnershipControls,

    // Object-scoped stubs
    GetObjectTorrent,
    RestoreObject,
    GetObjectRetention,
    PutObjectRetention,
    GetObjectLegalHold,
    PutObjectLegalHold,
}
