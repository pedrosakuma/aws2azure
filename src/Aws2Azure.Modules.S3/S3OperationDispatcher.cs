namespace Aws2Azure.Modules.S3;

internal enum S3DispatchTarget
{
    NotImplemented,
    Object,
    ObjectList,
    DeleteObjects,
    Multipart,
    Subresource,
    BucketCrud,
}

internal static class S3OperationDispatcher
{
    public static S3DispatchTarget GetTarget(S3Operation operation)
        => operation switch
        {
            S3Operation.Unknown or S3Operation.Unsupported => S3DispatchTarget.NotImplemented,

            S3Operation.PutObject or
            S3Operation.GetObject or
            S3Operation.HeadObject or
            S3Operation.DeleteObject or
            S3Operation.CopyObject => S3DispatchTarget.Object,

            S3Operation.ListObjects or
            S3Operation.ListObjectsV2 => S3DispatchTarget.ObjectList,

            S3Operation.DeleteObjects => S3DispatchTarget.DeleteObjects,

            S3Operation.CreateMultipartUpload or
            S3Operation.UploadPart or
            S3Operation.UploadPartCopy or
            S3Operation.CompleteMultipartUpload or
            S3Operation.AbortMultipartUpload or
            S3Operation.ListParts or
            S3Operation.ListMultipartUploads => S3DispatchTarget.Multipart,

            S3Operation.GetObjectTagging or
            S3Operation.PutObjectTagging or
            S3Operation.DeleteObjectTagging or
            S3Operation.GetBucketTagging or
            S3Operation.PutBucketTagging or
            S3Operation.DeleteBucketTagging or
            S3Operation.GetBucketAcl or
            S3Operation.PutBucketAcl or
            S3Operation.GetObjectAcl or
            S3Operation.PutObjectAcl or
            S3Operation.GetBucketLifecycleConfiguration or
            S3Operation.PutBucketLifecycleConfiguration or
            S3Operation.DeleteBucketLifecycle or
            S3Operation.GetBucketCors or
            S3Operation.PutBucketCors or
            S3Operation.DeleteBucketCors or
            S3Operation.GetBucketWebsite or
            S3Operation.PutBucketWebsite or
            S3Operation.DeleteBucketWebsite or
            S3Operation.GetBucketReplication or
            S3Operation.PutBucketReplication or
            S3Operation.DeleteBucketReplication or
            S3Operation.GetBucketEncryption or
            S3Operation.PutBucketEncryption or
            S3Operation.DeleteBucketEncryption or
            S3Operation.GetBucketLogging or
            S3Operation.PutBucketLogging or
            S3Operation.GetBucketVersioning or
            S3Operation.PutBucketVersioning or
            S3Operation.GetBucketRequestPayment or
            S3Operation.PutBucketRequestPayment or
            S3Operation.GetObjectLockConfiguration or
            S3Operation.PutObjectLockConfiguration or
            S3Operation.GetPublicAccessBlock or
            S3Operation.PutPublicAccessBlock or
            S3Operation.DeletePublicAccessBlock or
            S3Operation.GetBucketPolicy or
            S3Operation.PutBucketPolicy or
            S3Operation.DeleteBucketPolicy or
            S3Operation.GetBucketPolicyStatus or
            S3Operation.GetBucketNotificationConfiguration or
            S3Operation.PutBucketNotificationConfiguration or
            S3Operation.GetBucketAccelerateConfiguration or
            S3Operation.PutBucketAccelerateConfiguration or
            S3Operation.GetBucketOwnershipControls or
            S3Operation.PutBucketOwnershipControls or
            S3Operation.DeleteBucketOwnershipControls or
            S3Operation.GetObjectTorrent or
            S3Operation.RestoreObject or
            S3Operation.GetObjectRetention or
            S3Operation.PutObjectRetention or
            S3Operation.GetObjectLegalHold or
            S3Operation.PutObjectLegalHold => S3DispatchTarget.Subresource,

            S3Operation.ListBuckets or
            S3Operation.CreateBucket or
            S3Operation.DeleteBucket or
            S3Operation.HeadBucket => S3DispatchTarget.BucketCrud,

            _ => S3DispatchTarget.NotImplemented,
        };
}
