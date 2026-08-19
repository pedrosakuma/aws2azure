# s3 / ListObjectVersions {#operation-s3-listobjectversions}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:listobjectversions`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `GET {container}?restype=container&comp=list&include=versions`
- **Real-Azure verified:** ✅ 2026-06-30 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131)

## Sub-features

### version listing (Version entries) {#sub-feature-version-listing--version-entries}

- **Capability ID:** `sub-feature:s3:listobjectversions:version-listing--version-entries`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-30 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131)

Azure blob versions map to S3 <Version> entries (Key, VersionId, IsLatest, LastModified, ETag, Size). Requires account-level Blob versioning; otherwise each key lists once as IsLatest with VersionId 'null'.

### prefix / delimiter / max-keys / key-marker pagination {#sub-feature-prefix---delimiter---max-keys---key-marker-pagination}

- **Capability ID:** `sub-feature:s3:listobjectversions:prefix---delimiter---max-keys---key-marker-pagination`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-06-30 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28477441131)

### delete markers {#sub-feature-delete-markers}

- **Capability ID:** `sub-feature:s3:listobjectversions:delete-markers`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure delete markers do not map 1:1 to S3 delete markers; <DeleteMarker> entries are not emitted.

### version-id-marker pagination {#sub-feature-version-id-marker-pagination}

- **Capability ID:** `sub-feature:s3:listobjectversions:version-id-marker-pagination`
- **Status:** ✅ implemented

The proxy carries both key-marker and version-id-marker through pagination and emits NextVersionIdMarker alongside NextKeyMarker.

## Behaviour differences

- VersionId 'null' is used when account versioning is off (the blob has no version id).
- Owner element omitted.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectVersions.html>

