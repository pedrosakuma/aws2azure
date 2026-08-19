# s3 / PresignedUrl {#operation-s3-presignedurl}

[← s3 operation index](../../s3.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:s3:presignedurl`
- **Status:** ✅ implemented
- **Azure equivalent:** `(no operation — feature-flag; presigned URLs reuse GetObject / PutObject / HeadObject / DeleteObject paths)`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447675330)

## Sub-features

### Presigned GET {#sub-feature-presigned-get}

- **Capability ID:** `sub-feature:s3:presignedurl:presigned-get`
- **Status:** ✅ implemented

Generated via boto3.generate_presigned_url('get_object', ...) or AWSSDK.S3 GetPreSignedURL with endpoint_url pointing at the proxy; the proxy validates the SigV4 signature and executes GetBlob against Azure.

### Presigned PUT {#sub-feature-presigned-put}

- **Capability ID:** `sub-feature:s3:presignedurl:presigned-put`
- **Status:** ✅ implemented

Body integrity relies on TLS — SigV4 presigned PUT is signed with UNSIGNED-PAYLOAD on AWS too, so the proxy can only authenticate the request envelope (method, path, query, host, expiry).

### Presigned HEAD {#sub-feature-presigned-head}

- **Capability ID:** `sub-feature:s3:presignedurl:presigned-head`
- **Status:** ✅ implemented

### Presigned DELETE {#sub-feature-presigned-delete}

- **Capability ID:** `sub-feature:s3:presignedurl:presigned-delete`
- **Status:** ✅ implemented

### X-Amz-Expires window {#sub-feature-x-amz-expires-window}

- **Capability ID:** `sub-feature:s3:presignedurl:x-amz-expires-window`
- **Status:** ✅ implemented

Enforced by SigV4Validator: integer in [1, 604800] seconds; clock skew of ±15 min tolerated for X-Amz-Date.

### X-Amz-Security-Token (STS session credentials) {#sub-feature-x-amz-security-token--sts-session-credentials}

- **Capability ID:** `sub-feature:s3:presignedurl:x-amz-security-token--sts-session-credentials`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** aws2azure does not implement STS — credentials are static config entries. If a client includes X-Amz-Security-Token in a presigned URL it is part of the canonical query (and therefore signature-protected), but aws2azure does not validate it as a session token.

### Presigned POST (browser form uploads) {#sub-feature-presigned-post--browser-form-uploads}

- **Capability ID:** `sub-feature:s3:presignedurl:presigned-post--browser-form-uploads`
- **Status:** ✅ implemented

Browser-style multipart/form-data POST policies are validated proxy-side from form fields and then uploaded to Azure as a normal Put Blob. success_action_status=201 and success_action_redirect remain unsupported.

### Presigned multipart upload subresources {#sub-feature-presigned-multipart-upload-subresources}

- **Capability ID:** `sub-feature:s3:presignedurl:presigned-multipart-upload-subresources`
- **Status:** ✅ implemented

Presigned query authentication now has end-to-end coverage for ?uploads, UploadPart, and CompleteMultipartUpload.

### response-content-* query overrides {#sub-feature-response-content--query-overrides}

- **Capability ID:** `sub-feature:s3:presignedurl:response-content--query-overrides`
- **Status:** ✅ implemented

Presigned GetObject requests honour the same response-content-* overrides as header-authenticated GetObject.

### Azure Blob SAS issuance / redirect mode {#sub-feature-azure-blob-sas-issuance---redirect-mode}

- **Capability ID:** `sub-feature:s3:presignedurl:azure-blob-sas-issuance---redirect-mode`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** By design — aws2azure operates in proxy mode and never hands Azure SAS tokens to the client, so storage-account keys never leave the proxy. Clients always hit the proxy URL, not the Azure Blob endpoint.

### Host-rewrite mode (opt-in) {#sub-feature-host-rewrite-mode--opt-in}

- **Capability ID:** `sub-feature:s3:presignedurl:host-rewrite-mode--opt-in`
- **Status:** ✅ implemented

Opt-in via s3.presignedTrustedSigningHosts. A presigned URL signed against a trusted AWS S3 endpoint host and then host-rewritten to a path-style proxy request is re-validated against the listed origin host(s), covering both path-style (host = listed value) and virtual-hosted (host = {bucket}.{listed value}, bucket stripped from the path) origins. Empty (default) keeps strict host binding. The signature still requires the correct secret and every other signed parameter — only the host binding is relaxed to the allowlist. See docs/presigned-urls.md.

## Behaviour differences

- By default (empty s3.presignedTrustedSigningHosts) presigned URLs MUST be signed against the proxy host (set the AWS SDK's endpoint_url / ServiceURL to the proxy). A URL signed against s3.amazonaws.com cannot be replayed at the proxy — the host is a signed header.
- Opt-in host-rewrite mode (s3.presignedTrustedSigningHosts) additionally accepts presigned URLs signed against listed AWS origin hosts and rewritten to a path-style proxy request; see docs/presigned-urls.md for the per-topology tradeoffs.
- The proxy operates in 'Option A — proxy mode': it validates the presigned signature, then proxies the operation to Azure Blob using its configured Azure credentials. No Azure SAS is returned or redirected.
- Body content for presigned PUT is not signature-protected (UNSIGNED-PAYLOAD) — identical to AWS S3 semantics.
- Tampering with any signed query parameter (including X-Amz-Date, X-Amz-Expires, X-Amz-Credential, X-Amz-SignedHeaders, or the path/method) yields 403 SignatureDoesNotMatch.
- Expired URLs (now > X-Amz-Date + X-Amz-Expires) yield 403 AccessDenied (mirrors S3's 'Request has expired' error).

## References

- <https://docs.aws.amazon.com/AmazonS3/latest/userguide/ShareObjectPreSignedURL.html>
- <https://docs.aws.amazon.com/IAM/UserGuide/create-signed-request.html>
- <https://learn.microsoft.com/azure/storage/common/storage-sas-overview>
- <docs/presigned-urls.md>

