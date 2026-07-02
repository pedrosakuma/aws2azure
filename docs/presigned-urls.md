# Presigned URLs

Presigned URLs let an application hand a time-limited, signed URL to a third
party (a browser, a partner service) so it can `GET`/`PUT` an object **without**
AWS credentials. aws2azure supports presigned GET/PUT/HEAD/DELETE in **proxy
mode**: the proxy validates the SigV4 query-string signature and then performs
the equivalent Azure Blob operation with its own configured Azure credentials.
No Azure SAS token is ever returned to the client (see `docs/gaps/s3/PresignedUrl.yaml`).

## The host-binding requirement (and the bypass risk)

A presigned URL is a self-contained SigV4 request. The `host` header is part of
the canonical request, so the URL's signature is **bound to the host it was
signed against**. When the holder later dereferences the URL, the request must
arrive at that host with the same signed parameters, or the signature check
fails with `403 SignatureDoesNotMatch`.

This creates the core risk (Theme B / 🔴): if an application generates a
presigned URL **without** pointing the AWS SDK's `endpoint_url` /
`ServiceURL` at the proxy, the URL is signed against — and points at — an AWS S3
endpoint host. Two things then go wrong:

1. **Bypass** — the URL points at real AWS, so the object read/write silently
   skips the proxy (and, in an AWS→Azure deployment, lands on an AWS account
   that has no data).
2. **Non-replayable** — even if a front-end forwards the request to the proxy,
   the signature was computed over the AWS host, so the proxy rejects it.

### The correct fix: sign against the proxy

The robust, zero-tradeoff answer is to **generate presigned URLs with the SDK
pointed at the proxy**, exactly like every other call:

```python
s3 = boto3.client("s3", endpoint_url="http://s3.proxy.internal:8080", ...)
url = s3.generate_presigned_url("get_object", Params={"Bucket": b, "Key": k})
# -> http://s3.proxy.internal:8080/b/k?X-Amz-...  (signed against the proxy host)
```

The resulting URL is signed against the proxy host and validates natively — no
special configuration required. **Prefer this whenever you control the code that
generates the URL.**

## Rewrite mode (opt-in) — when you can't sign against the proxy

Sometimes you don't control URL generation: a legacy service, a third-party
SaaS, or a shared library emits presigned URLs against AWS hosts, and a
front-end (ingress, API gateway, CDN) rewrites the host to the proxy. In that
case the signature was computed over an AWS host and won't validate.

`s3.presignedTrustedSigningHosts` lets the operator declare an **allowlist of
trusted AWS origin signing hosts**. When a presigned request fails the strict
host check, the validator re-checks the signature against each listed host,
covering both URL styles:

| Origin style    | Signed host                     | Signed path      | What the proxy receives (path-style) |
|-----------------|---------------------------------|------------------|--------------------------------------|
| Path-style      | `s3.us-east-1.amazonaws.com`    | `/bucket/key`    | `/bucket/key`                        |
| Virtual-hosted  | `bucket.s3.us-east-1.amazonaws.com` | `/key`       | `/bucket/key`                        |

```jsonc
{
  "s3": {
    // Empty (default) = strict host binding. List the AWS host(s) your URLs
    // were signed against — regional and/or global, path- and virtual-hosted
    // are both derived from each entry.
    "presignedTrustedSigningHosts": [
      "s3.amazonaws.com",
      "s3.us-east-1.amazonaws.com"
    ]
  }
}
```

### Why this is safe

The signature **still requires the correct AWS secret and every other signed
parameter** (method, path, query, expiry, `X-Amz-Date`). Rewrite mode only
relaxes the *host* binding to an explicit, operator-declared allowlist — it does
not weaken authentication. A party who can produce a valid signature for
`s3.amazonaws.com` necessarily holds the same secret that signs for the proxy
host, so accepting it grants no capability they didn't already have.

### Tradeoffs and topology notes

- **Opt-in.** Empty list (default) keeps strict host binding — a rewritten URL
  is rejected. Enable only when a front-end genuinely rewrites AWS-signed URLs
  to the proxy.
- **Proxy must terminate path-style.** The virtual-hosted origin is
  reconstructed from a leading `/{bucket}/…` path segment, so the rewriter must
  present the request to the proxy in **path-style** (`proxy/bucket/key`). This
  is the standard behavior of host-rewriting front-ends. A proxy endpoint that
  is *itself* virtual-hosted works natively only when DNS preserves the original
  `bucket.s3….amazonaws.com` Host (no rewrite needed); rewriting to a
  virtual-hosted proxy host is not covered.
- **DNS-override topology needs no rewrite.** If you transparently intercept
  `*.s3.amazonaws.com` at DNS and forward to the proxy, the `Host` header is
  preserved and the signature validates natively — leave the allowlist empty.
- **List every host you expect.** Regional (`s3.<region>.amazonaws.com`) and
  the legacy global (`s3.amazonaws.com`) hosts are distinct signing hosts; add
  each one your clients actually use. Entries must be bare, lowercase hosts
  (no scheme, no path); startup validation rejects malformed entries.

## Related limitations

See `docs/gaps/s3/PresignedUrl.yaml` for the full capability matrix, including
presigned POST (browser form uploads, unsupported), `X-Amz-Security-Token` /
STS session credentials (unsupported — credentials are static config), and
`response-content-*` query overrides (unsupported).
