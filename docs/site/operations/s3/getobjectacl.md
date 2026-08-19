# s3 / GetObjectAcl {#operation-s3-getobjectacl}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getobjectacl`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no Azure equivalent — synthetic ownership-only response)`

## Sub-features

### owner reporting {#sub-feature-owner-reporting}

- **Capability ID:** `sub-feature:s3:getobjectacl:owner-reporting`
- **Status:** ✅ implemented

Identical to GetBucketAcl.

### per-object grants {#sub-feature-per-object-grants}

- **Capability ID:** `sub-feature:s3:getobjectacl:per-object-grants`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Reports owner FULL_CONTROL only.

### versionId existence check {#sub-feature-versionid-existence-check}

- **Capability ID:** `sub-feature:s3:getobjectacl:versionid-existence-check`
- **Status:** ✅ implemented

The synthetic ACL is returned only after HEAD verifies the selected Azure blob version exists; x-ms-version-id is returned as x-amz-version-id.

## Behaviour differences

- The versionId selects the object existence check, but ACL grants remain synthetic owner-only state.
- Azure BlobVersionNotFound maps to S3 NoSuchVersion.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectAcl.html>

