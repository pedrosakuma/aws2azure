# secretsmanager design gap / Disabled Key Vault secret versions use a backend-specific 403 {#design-gap-secretsmanager-disabled-key-vault-secret-versions-use-a-backend-specific-403}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:disabled-key-vault-secret-versions-use-a-backend-specific-403`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

Key Vault returns HTTP 403 `Forbidden` with the message `Operation is disabled for this secret version.` when a secret version is disabled, whereas AWS callers expect disabled or deprecated versions to behave like not found during rotation fallback.

**Impact.** Without a targeted translation layer, SDK rotation code that catches ResourceNotFoundException and falls back to an older version would stop on an AccessDeniedException instead.

**Workaround.** aws2azure remaps only that exact disabled-version signature to ResourceNotFoundException. Other 403 responses still mean authorization failed and must be fixed in Azure RBAC or Key Vault access policy.

