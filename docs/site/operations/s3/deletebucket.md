# s3 / DeleteBucket {#operation-s3-deletebucket}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deletebucket`
- **Status:** ✅ implemented
- **Azure equivalent:** `DELETE https://{account}.blob.core.windows.net/{container}?restype=container`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Behaviour differences

- Azure container delete is asynchronous; subsequent CreateContainer on the same name may return ContainerBeingDeleted (mapped to OperationAborted).
- S3 BucketNotEmpty is mapped from Azure ConditionNotMet/Conflict cases that surface only when the container retention policy intervenes.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteBucket.html>
- <https://learn.microsoft.com/rest/api/storageservices/delete-container>

