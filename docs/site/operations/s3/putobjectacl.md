# s3 / PutObjectAcl {#operation-s3-putobjectacl}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putobjectacl`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no Azure equivalent — validates owner-only intent and replies 200)`

## Sub-features

### canned ACL 'private' {#sub-feature-canned-acl-private}

- **Capability ID:** `sub-feature:s3:putobjectacl:canned-acl-private`
- **Status:** ✅ implemented

Accepted as no-op.

### other canned ACLs / x-amz-grant-* headers / non-owner grants {#sub-feature-other-canned-acls---x-amz-grant--headers---non-owner-grants}

- **Capability ID:** `sub-feature:s3:putobjectacl:other-canned-acls---x-amz-grant--headers---non-owner-grants`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Rejected with AccessControlListNotSupported.

### versionId existence check {#sub-feature-versionid-existence-check}

- **Capability ID:** `sub-feature:s3:putobjectacl:versionid-existence-check`
- **Status:** ✅ implemented

The no-op owner-only ACL update verifies the selected Azure blob version exists and returns x-amz-version-id before replying.

## Behaviour differences

- The versionId selects the object existence check only; Azure stores no S3 ACL document.
- Azure BlobVersionNotFound maps to S3 NoSuchVersion.
- Successful PutObjectAcl responses are intent-only compatibility no-ops. After HEAD verifies the selected blob/blob version exists, the proxy performs no Azure-side ACL mutation because Blob Storage has no object ACL document compatible with S3 grants.
- Real-Azure evidence is therefore not a distinct verification target for the 200 path: live Azure only contributes the object/version existence probe already covered by real-Azure object lifecycle/versioning scenarios and NoSuchVersion error coverage.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObjectAcl.html>

