# secretsmanager design gap / Deletion recovery semantics differ {#design-gap-secretsmanager-deletion-recovery-semantics-differ}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:deletion-recovery-semantics-differ`
- **Status:** 🔵 by design

DeleteSecret's RecoveryWindowInDays / ForceDeleteWithoutRecovery map onto Key Vault soft-delete and purge, whose retention model, purgeability, and reported deletion timestamps are governed by the vault rather than by AWS request parameters.

**Impact.** Recovery-window timing and force-delete behaviour follow the Key Vault soft-delete configuration rather than the exact AWS window semantics. If purge protection is enabled, ForceDeleteWithoutRecovery cannot succeed until the retention window expires regardless of RBAC role or access-policy grants. DescribeSecret also leaves deletion timestamps empty until deletion is actually initiated; it does not predict a future DeletionDate from the vault's recoverable-days setting.

**Workaround.** Configure the Key Vault soft-delete retention to match the intended recovery window. Use ForceDeleteWithoutRecovery only on vaults where purge protection is disabled and the configured identity has purge permission; there is no Azure permission level that overrides purge protection during the retention window.

## References

- <https://learn.microsoft.com/en-us/azure/key-vault/general/soft-delete-overview#purge-protection>

