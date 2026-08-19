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

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketAcl.html>

