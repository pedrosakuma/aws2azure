# Before you migrate Secrets Manager {#before-you-migrate-secretsmanager}

[← Workload compatibility](../workload-compatibility.md#secretsmanager) · [Design gaps](../design-gaps.md#secretsmanager)

Answer each question with **yes** or **no**.
If you answer **yes**, read the linked design gap and confirm its workaround
fits your workload before migrating.

1. **Do you need DeleteSecret to work for certificate-backed secrets without using the Key Vault certificate API?** → [Certificate-backed secrets require the certificate API for deletion](../design-gaps/secretsmanager/certificate-backed-secrets-require-the-certificate-api-for-deletion.md)
2. **Do you need DeleteSecret recovery windows or force-delete behavior to match AWS exactly?** → [Deletion recovery semantics differ](../design-gaps/secretsmanager/deletion-recovery-semantics-differ.md)
3. **Does your rotation or fallback logic treat every disabled-version error as an AWS-style not found?** → [Disabled Key Vault secret versions use a backend-specific 403](../design-gaps/secretsmanager/disabled-key-vault-secret-versions-use-a-backend-specific-403.md)
4. **Are you planning to point Secrets Manager traffic at Azure Managed HSM instead of a standard Key Vault?** → [Managed HSM endpoints do not implement the secrets API](../design-gaps/secretsmanager/managed-hsm-endpoints-do-not-implement-the-secrets-api.md)
5. **Do you require Secrets Manager resource policies or cross-account secret sharing?** → [No resource policies or cross-account access](../design-gaps/secretsmanager/no-resource-policies-or-cross-account-access.md)
6. **Does your workload require RotateSecret to run an AWS Lambda-compatible rotation workflow?** → [Rotation has no Lambda equivalent](../design-gaps/secretsmanager/rotation-has-no-lambda-equivalent.md)
7. **Do your callers validate exact AWS Secrets Manager ARN structure or persist AWS account IDs from ARNs?** → [Synthetic ARNs use a proxy-specific namespace](../design-gaps/secretsmanager/synthetic-arns-use-a-proxy-specific-namespace.md)
8. **Do you require fully atomic, cross-instance version-stage updates under concurrent writers?** → [Versioning and staging modelled on Key Vault version tags](../design-gaps/secretsmanager/versioning-and-staging-modelled-on-key-vault-version-tags.md)
