# secretsmanager design gap / No resource policies or cross-account access {#design-gap-secretsmanager-no-resource-policies-or-cross-account-access}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:no-resource-policies-or-cross-account-access`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Secrets Manager resource policies and cross-account secret sharing have no Key Vault equivalent in the wire-protocol path; authorization is the static AWS-key-to-Azure-credential mapping, not server-side IAM.

**Impact.** Policy-based or cross-account access patterns cannot be expressed through the proxy.

**Workaround.** Use Key Vault RBAC / access policies at the Azure level for authorization. For write/delete lifecycle workloads, grant the proxy identity a data-plane role such as Key Vault Secrets Officer; Key Vault Secrets User is read-only, and Key Vault Reader / Key Vault Contributor do not grant secret-value data-plane access. In legacy access-policy mode, grant the secrets get/set/list/delete permissions (plus purge only when purge protection is disabled and force-delete is intentionally allowed).

## See also

- [Authorization migration guide](../../../authorization-migration.md#secrets-manager-resource-policies-and-cross-account-sharing-move-to-key-vault-access-design)

## References

- <https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide>
- <https://learn.microsoft.com/en-us/azure/key-vault/general/assign-access-policy>

