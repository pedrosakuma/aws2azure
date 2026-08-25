# secretsmanager design gap / Managed HSM endpoints do not implement the secrets API {#design-gap-secretsmanager-managed-hsm-endpoints-do-not-implement-the-secrets-api}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:managed-hsm-endpoints-do-not-implement-the-secrets-api`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Azure Managed HSM stores and serves keys only. It does not expose the Key Vault secrets or certificates data-plane APIs that aws2azure's Secrets Manager translation requires.

**Impact.** Pointing the Secrets Manager backend at a `*.managedhsm.azure.net` endpoint cannot work, even though the URL shape resembles a Key Vault hostname.

**Workaround.** Use a standard Azure Key Vault vault endpoint for Secrets Manager bindings. Startup validation rejects Managed HSM URLs with a clear configuration error instead of failing later at runtime.

