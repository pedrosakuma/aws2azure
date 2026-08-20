# s3 / DeleteObjectTagging {#operation-s3-deleteobjecttagging}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deleteobjecttagging`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT {blob}?comp=tags with an empty <TagSet/>`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### idempotent clear {#sub-feature-idempotent-clear}

- **Capability ID:** `sub-feature:s3:deleteobjecttagging:idempotent-clear`
- **Status:** ✅ implemented

Azure has no DELETE for blob index tags; clearing is modelled as a PUT of an empty tag set.

### versionId qualifier {#sub-feature-versionid-qualifier}

- **Capability ID:** `sub-feature:s3:deleteobjecttagging:versionid-qualifier`
- **Status:** ✅ implemented

Maps the opaque S3 versionId to Azure versionid. Unqualified requests HEAD and pin the current version before clearing tags and returning x-amz-version-id.

## Behaviour differences

- Returns 204 No Content matching the S3 spec.
- Version selection requires account-level Blob versioning enabled out-of-band.
- Azure BlobVersionNotFound maps to S3 NoSuchVersion.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteObjectTagging.html>

