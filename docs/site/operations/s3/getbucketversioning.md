# s3 / GetBucketVersioning {#operation-s3-getbucketversioning}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbucketversioning`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Container metadata (per-bucket toggle); reflects stored PutBucketVersioning intent`
- **Real-Azure verified:** ✅ 2026-06-30 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131)

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:getbucketversioning:configuration-storage`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-30 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131)

The bucket-level versioning intent (Enabled/Suspended) is stored in container metadata and echoed back. Returns the empty 'never configured' document when unset.

## Behaviour differences

- GET returns the per-bucket toggle persisted by PutBucketVersioning. Azure Blob versioning itself is account-scoped: actual version retention requires account-level versioning enabled out-of-band by the operator.
- MFADelete is never reported (not supported).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketVersioning.html>

