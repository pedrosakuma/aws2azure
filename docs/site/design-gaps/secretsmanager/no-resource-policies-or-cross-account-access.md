# secretsmanager design gap / No resource policies or cross-account access {#design-gap-secretsmanager-no-resource-policies-or-cross-account-access}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:no-resource-policies-or-cross-account-access`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Secrets Manager resource policies and cross-account secret sharing have no Key Vault equivalent in the wire-protocol path; authorization is the static AWS-key-to-Azure-credential mapping, not server-side IAM.

**Impact.** Policy-based or cross-account access patterns cannot be expressed through the proxy.

**Workaround.** Use Key Vault RBAC / access policies at the Azure level for authorization.

