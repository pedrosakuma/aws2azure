# s3 / PutBucketAcl {#operation-s3-putbucketacl}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbucketacl`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no Azure equivalent — validates owner-only intent and replies 200)`

## Sub-features

### canned ACL 'private' {#sub-feature-canned-acl-private}

- **Capability ID:** `sub-feature:s3:putbucketacl:canned-acl-private`
- **Status:** ✅ implemented

Accepted as no-op.

### other canned ACLs (public-read, public-read-write, log-delivery-write, …) {#sub-feature-other-canned-acls--public-read-public-read-write-log-delivery-write}

- **Capability ID:** `sub-feature:s3:putbucketacl:other-canned-acls--public-read-public-read-write-log-delivery-write`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Rejected with AccessControlListNotSupported.

### x-amz-grant-* headers and explicit ACL bodies {#sub-feature-x-amz-grant--headers-and-explicit-acl-bodies}

- **Capability ID:** `sub-feature:s3:putbucketacl:x-amz-grant--headers-and-explicit-acl-bodies`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Rejected unless they describe a single FULL_CONTROL grant to the bucket owner.

## Behaviour differences

- Successful PutBucketAcl responses are intent-only compatibility no-ops. After the initial container existence probe, the proxy performs no Azure-side ACL mutation because Blob Storage has no bucket ACL document compatible with S3 grants.
- Real-Azure evidence is therefore not a distinct verification target for the 200 path: live Azure only contributes the bucket-existence check, which is already exercised by real-Azure bucket CRUD/read scenarios and dedicated NoSuchBucket coverage.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketAcl.html>

