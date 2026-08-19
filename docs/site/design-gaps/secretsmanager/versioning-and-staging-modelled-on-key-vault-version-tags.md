# secretsmanager design gap / Versioning and staging modelled on Key Vault version tags {#design-gap-secretsmanager-versioning-and-staging-modelled-on-key-vault-version-tags}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:versioning-and-staging-modelled-on-key-vault-version-tags`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Secrets Manager version stages (AWSCURRENT / AWSPREVIOUS / custom labels) are modelled with Key Vault secret versions plus per-version tags. Key Vault's created timestamp has one-second granularity, so deterministic resolution uses created time plus version id and relies on tag bookkeeping rather than a native staging concept.

**Impact.** Version creation, inventory, and per-version tag patches cannot be one Key Vault transaction. The proxy uses empty-stage creation, loser-first publication, fresh tag merges, and bounded verification/repair, but strict cross-instance atomicity remains structurally impossible without an external coordinator. Key Vault secret PATCH does not expose a contractual ETag/If-Match primitive, so a tag edit that lands in the narrow interval between the proxy's fresh GET and PATCH can still be overwritten.

**Workaround.** Retry an explicit ResourceExistsException after propagation settles and use a single writer when stronger ordering is required. Unrelated out-of-band tags are preserved by fresh merges, but out-of-band edits to proxy-owned aws2azure-* tags are unsupported. Before rollback, drain writes and let the candidate runtime finish or repair every pending publication; the previous runtime can read completed versions but cannot interpret the candidate-only pending-publication intent metadata.

