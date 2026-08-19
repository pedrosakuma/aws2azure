# s3 / AbortMultipartUpload {#operation-s3-abortmultipartupload}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:abortmultipartupload`
- **Status:** ✅ implemented
- **Azure equivalent:** `Lease state record + delete proxy-owned multipart state blob`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### lease-based-convergence {#sub-feature-lease-based-convergence}

- **Capability ID:** `sub-feature:s3:abortmultipartupload:lease-based-convergence`
- **Status:** ✅ implemented

Abort acquires a 60-second lease on the multipart state blob before deleting it so concurrent Abort/Complete operations converge on one winner.

### bounded-lease-work {#sub-feature-bounded-lease-work}

- **Capability ID:** `sub-feature:s3:abortmultipartupload:bounded-lease-work`
- **Status:** ✅ implemented

Lease-protected work is bounded by a 45-second linked deadline. On timeout the proxy returns RequestTimeout and lets the lease expire naturally instead of risking a late release race.

## Behaviour differences

- Abort does not delete Azure's uncommitted blocks eagerly; removing the durable state record is what invalidates the UploadId immediately. Azure later garbage-collects the abandoned blocks on its normal schedule.
- Subsequent UploadPart/ListParts/CompleteMultipartUpload calls on the same UploadId return NoSuchUpload because the proxy state record is gone, even if Azure still retains the uncommitted blocks temporarily.

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_AbortMultipartUpload.html>
- <https://learn.microsoft.com/rest/api/storageservices/lease-blob>

