# s3 / DeletePublicAccessBlock {#operation-s3-deletepublicaccessblock}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deletepublicaccessblock`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Conditional container-metadata update`

## Sub-features

### idempotent intent clear {#sub-feature-idempotent-intent-clear}

- **Capability ID:** `sub-feature:s3:deletepublicaccessblock:idempotent-intent-clear`
- **Status:** ✅ implemented

Removes only the proxy-owned public-access-block intent key and returns 204.

### public-access enforcement {#sub-feature-public-access-enforcement}

- **Capability ID:** `sub-feature:s3:deletepublicaccessblock:public-access-enforcement`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure public-access settings are unchanged.

## Behaviour differences

- The operation clears persisted proxy intent only.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeletePublicAccessBlock.html>

