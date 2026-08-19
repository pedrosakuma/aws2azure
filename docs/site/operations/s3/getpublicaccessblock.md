# s3 / GetPublicAccessBlock {#operation-s3-getpublicaccessblock}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getpublicaccessblock`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Container metadata (persisted compatibility intent only)`

## Sub-features

### four-flag configuration round-trip {#sub-feature-four-flag-configuration-round-trip}

- **Capability ID:** `sub-feature:s3:getpublicaccessblock:four-flag-configuration-round-trip`
- **Status:** ✅ implemented

Returns the stored BlockPublicAcls, IgnorePublicAcls, BlockPublicPolicy, and RestrictPublicBuckets values.

### public-access enforcement {#sub-feature-public-access-enforcement}

- **Capability ID:** `sub-feature:s3:getpublicaccessblock:public-access-enforcement`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure container/account public-access controls are not changed or evaluated.

## Behaviour differences

- A missing intent returns 404 NoSuchPublicAccessBlockConfiguration; a present intent is not an Azure access-control boundary.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetPublicAccessBlock.html>

