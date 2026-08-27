# secretsmanager design gap / Synthetic ARNs use a proxy-specific namespace {#design-gap-secretsmanager-synthetic-arns-use-a-proxy-specific-namespace}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:synthetic-arns-use-a-proxy-specific-namespace`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

The proxy has no real AWS region/account/random-suffix identity for an Azure Key Vault secret, so it emits a synthetic `arn:aws:secretsmanager:azure:keyvault:secret:{name}` shape and parses any inbound ARN by taking the segment after `:secret:`.

**Impact.** Callers that validate exact AWS ARN region/account/suffix structure, persist AWS account IDs from ARNs, or depend on partial-ARN matching edge cases can observe non-AWS shapes. Offline Tier-3 diffs between independently captured real AWS and real Azure evidence therefore cannot use exact ARN byte equality as a meaningful gate signal.

**Workaround.** Treat the returned ARN as an opaque proxy identifier and prefer friendly secret names or full proxy-emitted ARNs when making subsequent requests.

