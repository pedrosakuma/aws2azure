# s3 / GetObjectLockConfiguration {#operation-s3-getobjectlockconfiguration}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getobjectlockconfiguration`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(bucket-level WORM is ARM/management-plane only; proxy returns 404 ObjectLockConfigurationNotFoundError)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getobjectlockconfiguration:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Object-level retention/legal-hold ARE supported (see PutObjectRetention/PutObjectLegalHold). Bucket-level ObjectLockConfiguration maps to Azure container/account WORM, which is an ARM (management-plane, Entra-token) surface unreachable with storage account keys, so it stays unsupported.

## Behaviour differences

- GET returns HTTP 404 with code ObjectLockConfigurationNotFoundError so clients receive the same shape as a never-configured S3 bucket instead of InternalError.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectLockConfiguration.html>
- <https://learn.microsoft.com/en-us/rest/api/storagerp/blob-containers/create-or-update-immutability-policy>

