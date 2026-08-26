# Secrets Manager basic lifecycle profile

This version 1 profile covers `CreateSecret`, `DescribeSecret`,
`GetSecretValue`, `PutSecretValue`, `UpdateSecret`, `ListSecrets`, and
`DeleteSecret` against Azure Key Vault.

Its current generated verdict is `candidate` because the previously reviewed
real-Azure qualification evidence is stale. Check
[workload GA certification](../site/workload-ga.md) before adoption; historical
qualification and approved-runtime records do not override the live verdict.

## Required deployment contract

- Configure Entra authentication and grant the binding's identity the required
  Key Vault data-plane permissions: `Key Vault Secrets Officer` for this
  read/write/delete profile, or the legacy access-policy permissions
  `secrets: get/set/list/delete`. `Key Vault Secrets User` is read-only, and
  `Key Vault Reader` / `Key Vault Contributor` do not grant secret-value
  access.
- Configure Key Vault soft-delete retention for the intended recovery posture.
  `RecoveryWindowInDays` and `ForceDeleteWithoutRecovery` cannot override vault
  policy, purge protection, or missing purge permission.
- Treat proxy-owned `aws2azure-*` version tags as reserved implementation data.
  Out-of-band edits to those tags are unsupported.
- Use a single writer per secret when the application requires ordering
  stronger than Key Vault can provide without an external coordinator.

## Version and stage semantics

Key Vault versions and tags model AWS version stages. `PutSecretValue` accepts
the profile's documented partial semantics: version creation, inventory, and
stage-tag updates are not one transaction. Contended cross-instance writes can
return `ResourceExistsException`; callers should retry or read after propagation
settles.

Before adoption, exercise token idempotency, rapid successive writes, custom
stage movement, restart replay, credential rotation, pagination, deletion and
purge permissions, throttling, timeout, cancellation, and rollback against the
exact vault configuration.

Lambda-driven `RotateSecret`, resource policies, and cross-account sharing are
outside this profile. Run rotation through an external Azure Function or
pipeline that writes the new version through the proxy.

## Adoption decision

Adopt only when the generated profile verdict is acceptable and fresh
real-Azure evidence exists for the release and topology being deployed. Review
the accepted deletion-recovery and version-stage design gaps in the generated
[Secrets Manager capability page](../site/secretsmanager.md).
