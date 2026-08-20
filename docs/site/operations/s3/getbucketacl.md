# s3 / GetBucketAcl {#operation-s3-getbucketacl}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketacl`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no Azure equivalent — synthetic ownership-only response)`

## Sub-features

### owner reporting {#sub-feature-owner-reporting}

- **Capability ID:** `sub-feature:s3:getbucketacl:owner-reporting`
- **Status:** ✅ implemented

Owner ID = SHA-256(accountName) hex; DisplayName = accountName.

### non-owner grants {#sub-feature-non-owner-grants}

- **Capability ID:** `sub-feature:s3:getbucketacl:non-owner-grants`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Always reports a single FULL_CONTROL grant to the owner.

## Behaviour differences

- Azure Blob Storage's authorisation model (RBAC + Shared Key + SAS) does not map onto S3 canonical-user grants. The proxy reports the ownership-only shape that matches BucketOwnerEnforced ObjectOwnership.
- Successful GetBucketAcl responses are synthetic owner-only compatibility documents derived from the configured Azure account identity after confirming the container exists; Azure itself exposes no S3-compatible ACL body to round-trip.
- Real-Azure evidence is therefore not a distinct verification target for the 200 path: live Azure only contributes the bucket-existence probe, while the returned grant document is intentionally proxy-owned and stable across backends.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketAcl.html>

