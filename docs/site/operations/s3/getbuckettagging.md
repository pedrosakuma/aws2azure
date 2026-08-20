# s3 / GetBucketTagging {#operation-s3-getbuckettagging}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:getbuckettagging`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `GET {container}?restype=container&comp=metadata (single opaque metadata blob)`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### tag round-trip {#sub-feature-tag-round-trip}

- **Capability ID:** `sub-feature:s3:getbuckettagging:tag-round-trip`
- **Status:** ✅ implemented

Tags are stored as a base64-encoded S3 <Tagging> XML in a single container metadata key (aws2azurebuckettags). Atomic, no key-name mangling.

### NoSuchTagSet when never set {#sub-feature-nosuchtagset-when-never-set}

- **Capability ID:** `sub-feature:s3:getbuckettagging:nosuchtagset-when-never-set`
- **Status:** ✅ implemented

Returned 404 with code NoSuchTagSet matching AWS.

### server-side enforcement (cost allocation, IAM tag conditions) {#sub-feature-server-side-enforcement--cost-allocation-iam-tag-conditions}

- **Capability ID:** `sub-feature:s3:getbuckettagging:server-side-enforcement--cost-allocation-iam-tag-conditions`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure has no native bucket-tagging surface to enforce these; tags are pure metadata.

## Behaviour differences

- Bucket tags survive process restarts because they live on the container metadata, but they are invisible to any Azure-native tooling that does not know about the aws2azurebuckettags key.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetBucketTagging.html>
- <https://learn.microsoft.com/rest/api/storageservices/get-container-metadata>

