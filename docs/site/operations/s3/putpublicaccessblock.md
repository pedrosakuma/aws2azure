# s3 / PutPublicAccessBlock {#operation-s3-putpublicaccessblock}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putpublicaccessblock`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Conditional container-metadata update (persisted compatibility intent only)`

## Sub-features

### four-flag configuration storage {#sub-feature-four-flag-configuration-storage}

- **Capability ID:** `sub-feature:s3:putpublicaccessblock:four-flag-configuration-storage`
- **Status:** ✅ implemented

Persists the four boolean fields with bounded ETag/If-Match retry while preserving unrelated metadata; omitted fields default to false.

### public-access enforcement {#sub-feature-public-access-enforcement}

- **Capability ID:** `sub-feature:s3:putpublicaccessblock:public-access-enforcement`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Does not change Azure container access level, account AllowBlobPublicAccess, RBAC, SAS, or network policy.

## Behaviour differences

- The AWS document round-trips for compatibility but has no enforcement effect.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutPublicAccessBlock.html>

