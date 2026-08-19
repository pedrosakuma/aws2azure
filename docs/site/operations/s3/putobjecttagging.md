# s3 / PutObjectTagging {#operation-s3-putobjecttagging}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:putobjecttagging`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT {blob}?comp=tags`

## Sub-features

### replace existing tag set {#sub-feature-replace-existing-tag-set}

- **Capability ID:** `sub-feature:s3:putobjecttagging:replace-existing-tag-set`
- **Status:** ✅ implemented

Mirrors S3 semantics: PUT replaces the tag set atomically.

### 10-tag limit per object {#sub-feature-10-tag-limit-per-object}

- **Capability ID:** `sub-feature:s3:putobjecttagging:10-tag-limit-per-object`
- **Status:** ✅ implemented

Enforced before the Azure call.

### 128-char key / 256-char value limits {#sub-feature-128-char-key---256-char-value-limits}

- **Capability ID:** `sub-feature:s3:putobjecttagging:128-char-key---256-char-value-limits`
- **Status:** ✅ implemented

Enforced before the Azure call.

### versionId qualifier {#sub-feature-versionid-qualifier}

- **Capability ID:** `sub-feature:s3:putobjecttagging:versionid-qualifier`
- **Status:** ✅ implemented

Maps the opaque S3 versionId to Azure versionid. Unqualified requests HEAD and pin the current Azure version before Set Blob Tags so x-amz-version-id identifies the updated version.

## Behaviour differences

- AWS uses 200 OK with empty body; the proxy matches that.
- Version selection depends on Azure Blob versioning being enabled by the operator.
- Azure BlobVersionNotFound maps to S3 NoSuchVersion.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObjectTagging.html>
- <https://learn.microsoft.com/rest/api/storageservices/set-blob-tags>

