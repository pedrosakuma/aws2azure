# s3 / ListObjectsV2 {#operation-s3-listobjectsv2}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:listobjectsv2`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{account}.blob.core.windows.net/{container}?restype=container&comp=list`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### prefix {#sub-feature-prefix}

- **Capability ID:** `sub-feature:s3:listobjectsv2:prefix`
- **Status:** ✅ implemented

### delimiter (CommonPrefixes via Azure BlobPrefix) {#sub-feature-delimiter--commonprefixes-via-azure-blobprefix}

- **Capability ID:** `sub-feature:s3:listobjectsv2:delimiter--commonprefixes-via-azure-blobprefix`
- **Status:** ✅ implemented

### max-keys (default 1000, cap 1000) {#sub-feature-max-keys--default-1000-cap-1000}

- **Capability ID:** `sub-feature:s3:listobjectsv2:max-keys--default-1000-cap-1000`
- **Status:** ✅ implemented

**Gap.** Azure's maxresults cap is 5000; we clamp to S3's 1000 so wire semantics match.

### continuation-token (base64url-encoded Azure NextMarker) {#sub-feature-continuation-token--base64url-encoded-azure-nextmarker}

- **Capability ID:** `sub-feature:s3:listobjectsv2:continuation-token--base64url-encoded-azure-nextmarker`
- **Status:** ✅ implemented

**Gap.** Opaque token; clients must round-trip the exact value. Stateless on the proxy.

### start-after (used as initial Azure marker when no continuation-token) {#sub-feature-start-after--used-as-initial-azure-marker-when-no-continuation-token}

- **Capability ID:** `sub-feature:s3:listobjectsv2:start-after--used-as-initial-azure-marker-when-no-continuation-token`
- **Status:** ✅ implemented

### encoding-type=url (percent-encodes Key/Prefix/Delimiter/StartAfter) {#sub-feature-encoding-typeurl--percent-encodes-key-prefix-delimiter-startafter}

- **Capability ID:** `sub-feature:s3:listobjectsv2:encoding-typeurl--percent-encodes-key-prefix-delimiter-startafter`
- **Status:** ✅ implemented

### KeyCount / IsTruncated {#sub-feature-keycount---istruncated}

- **Capability ID:** `sub-feature:s3:listobjectsv2:keycount---istruncated`
- **Status:** ✅ implemented

### fetch-owner {#sub-feature-fetch-owner}

- **Capability ID:** `sub-feature:s3:listobjectsv2:fetch-owner`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Owner element is omitted; Azure does not expose per-blob ownership.

## Behaviour differences

- Pagination is server-driven against Azure; the proxy paginates internally to fill max-keys.
- Blob storage is flat — delimiter-based grouping is computed by Azure and surfaced as CommonPrefixes.
- CommonPrefixes are de-duplicated across Azure pages.
- NextContinuationToken / ContinuationToken use a proxy-defined opaque encoding of Azure's marker rather than AWS's token format; clients must treat them as opaque. [conformance:list-objects-v2-pagination::field-value:NextContinuationToken] [conformance:list-objects-v2-pagination::field-value:ContinuationToken]
- Offline Tier-3 real-AWS vs real-Azure diffs normalize the echoed bucket Name field when captures were recorded against different ephemeral bucket names for the same case. [conformance:list-objects-v2-pagination::field-value:Name]

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html>
- <https://learn.microsoft.com/rest/api/storageservices/list-blobs>

