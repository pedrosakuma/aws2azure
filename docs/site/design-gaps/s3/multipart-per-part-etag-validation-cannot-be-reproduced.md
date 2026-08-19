# s3 design gap / Multipart per-part ETag validation cannot be reproduced {#design-gap-s3-multipart-per-part-etag-validation-cannot-be-reproduced}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:multipart-per-part-etag-validation-cannot-be-reproduced`
- **Status:** 🔵 by design

Even with durable multipart state, Azure still exposes no primitive that lets the proxy re-read the true MD5/ETag of each staged uncommitted block. CompleteMultipartUpload and ListParts therefore cannot validate or replay AWS's per-part ETag contract exactly.

**Impact.** Workloads that overwrite a PartNumber with different bytes and rely on AWS rejecting the stale ETag at CompleteMultipartUpload will observe different behaviour.

**Workaround.** Gate those workloads out or avoid re-uploading the same PartNumber with different content inside one multipart session.

