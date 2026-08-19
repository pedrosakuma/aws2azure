# s3 / DeleteObjects {#operation-s3-deleteobjects}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:deleteobjects`
- **Status:** ✅ implemented
- **Azure equivalent:** `Multiple DELETEs against Blob (no native batch endpoint)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### quiet-mode {#sub-feature-quiet-mode}

- **Capability ID:** `sub-feature:s3:deleteobjects:quiet-mode`
- **Status:** ✅ implemented

### versionid {#sub-feature-versionid}

- **Capability ID:** `sub-feature:s3:deleteobjects:versionid`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Returns top-level MalformedXML when <VersionId> is present in the request body.

### mfa-delete {#sub-feature-mfa-delete}

- **Capability ID:** `sub-feature:s3:deleteobjects:mfa-delete`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- Fanned out as parallel single-blob DELETEs (cap of 16 concurrent) — no transactional guarantee across keys.
- Missing key is reported as Deleted (matching S3 idempotency); missing bucket short-circuits the fan-out with a top-level NoSuchBucket error.
- Request body is capped at 2 MiB and Content-MD5 (or x-amz-sdk-checksum-algorithm) is required; mismatched digests surface as BadDigest.
- Returns top-level MalformedXML when <VersionId> is present in the request body (no versioning support).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteObjects.html>
- <https://learn.microsoft.com/rest/api/storageservices/delete-blob>

