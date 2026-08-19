# secretsmanager design gap / Deletion recovery semantics differ {#design-gap-secretsmanager-deletion-recovery-semantics-differ}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:deletion-recovery-semantics-differ`
- **Status:** 🔵 by design

DeleteSecret's RecoveryWindowInDays / ForceDeleteWithoutRecovery map onto Key Vault soft-delete and purge, whose retention model and immediate-purge permissions are governed by the vault, not by AWS parameters.

**Impact.** Recovery-window timing and force-delete behaviour follow the Key Vault soft-delete configuration rather than the exact AWS window semantics.

**Workaround.** Configure the Key Vault soft-delete retention to match the intended recovery window; grant purge permission only where force-delete is needed.

