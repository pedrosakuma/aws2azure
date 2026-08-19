# s3 / PutBucketVersioning {#operation-s3-putbucketversioning}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putbucketversioning`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Container metadata (per-bucket toggle); account-level Blob versioning assumed pre-enabled`
- **Real-Azure verified:** ✅ 2026-06-30 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131)

## Sub-features

### configuration storage {#sub-feature-configuration-storage}

- **Capability ID:** `sub-feature:s3:putbucketversioning:configuration-storage`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-30 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131)

Persists the bucket-level Status (Enabled/Suspended) in container metadata. Malformed/unrecognised bodies rejected with MalformedXML.

### MFADelete {#sub-feature-mfadelete}

- **Capability ID:** `sub-feature:s3:putbucketversioning:mfadelete`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

MFADelete is silently ignored / unsupported.

## Behaviour differences

- Stores the S3 bucket-level intent only; does not toggle account-level Azure Blob versioning (no control-plane). Operators must enable account versioning out-of-band for versionId retention to function (opt-in, documented per topology).
- Container metadata updates use bounded ETag/If-Match retry and re-merge fresh metadata on conflict so concurrent tagging/versioning/compatibility-intent updates do not silently clobber unrelated entries.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutBucketVersioning.html>

