# s3 design gap / Multipart upload keeps bounded durable proxy state {#design-gap-s3-multipart-upload-keeps-bounded-durable-proxy-state}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:s3:multipart-upload-keeps-bounded-durable-proxy-state`
- **Status:** 🔵 by design

Azure Blob Storage has no native cross-blob multipart-upload enumeration primitive, so aws2azure persists one bounded state record per active multipart upload in a hidden Azure container. Record names begin with the zero-padded initiation timestamp, record bodies store only the headers needed to finish the upload (16 KiB cap), and Create/List opportunistically delete a bounded number of expired records.

**Impact.** Multipart enumeration and metadata fidelity now survive proxy restart, but the proxy deliberately owns a small amount of durable control-plane state.

**Workaround.** Treat the hidden multipart-state container as proxy-owned implementation data; do not manage or replicate it through S3.

