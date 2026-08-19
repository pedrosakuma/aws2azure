# s3 / GetBucketReplication {#operation-s3-getbucketreplication}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketreplication`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design
- **Azure equivalent:** `(no equivalent — proxy returns 404 ReplicationConfigurationNotFoundError)`

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketreplication:configuration-storage`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure Object Replication is configured via the management plane and is asynchronous and account-scoped.

## Behaviour differences

- GET returns HTTP 404 with code ReplicationConfigurationNotFoundError so clients receive the same shape as a never-configured S3 bucket instead of InternalError.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketReplication.html>

