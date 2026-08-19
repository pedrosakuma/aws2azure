# s3 / CreateBucket {#operation-s3-createbucket}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:createbucket`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{account}.blob.core.windows.net/{container}?restype=container`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### CreateBucketConfiguration.LocationConstraint {#sub-feature-createbucketconfigurationlocationconstraint}

- **Capability ID:** `sub-feature:s3:createbucket:createbucketconfigurationlocationconstraint`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Region/location is fixed by the configured Azure storage account.

### x-amz-acl / x-amz-grant-* {#sub-feature-x-amz-acl---x-amz-grant}

- **Capability ID:** `sub-feature:s3:createbucket:x-amz-acl---x-amz-grant`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Public-access semantics differ; only Azure 'private' is exposed for now.

### ObjectLock / ObjectOwnership headers {#sub-feature-objectlock---objectownership-headers}

- **Capability ID:** `sub-feature:s3:createbucket:objectlock---objectownership-headers`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- Bucket name == container name; Azure container naming is stricter than S3 (3–63 lowercase, digits, single hyphens, no leading/trailing hyphen).
- Location response header is host-relative ('/{bucket}') since the proxy doesn't know its public hostname.
- Region-sensitive idempotency is reproduced from the signed credential scope: in us-east-1 re-creating a bucket you already own returns 200 OK (idempotent), matching real S3; other regions return 409 BucketAlreadyOwnedByYou. Azure's ContainerAlreadyExists drives this; Azure does not expose 'owned by someone else' separately, so BucketAlreadyExists (foreign owner) cannot be distinguished and is always treated as owned-by-you.
- BucketAlreadyOwnedByYou error envelopes omit the informational <BucketName> element real S3 includes. [conformance:bucketalreadyownedbyyou-recreate::missing-field:BucketName]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_CreateBucket.html>
- <https://learn.microsoft.com/rest/api/storageservices/create-container>

