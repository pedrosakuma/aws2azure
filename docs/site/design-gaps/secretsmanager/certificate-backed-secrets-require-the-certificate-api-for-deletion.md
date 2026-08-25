# secretsmanager design gap / Certificate-backed secrets require the certificate API for deletion {#design-gap-secretsmanager-certificate-backed-secrets-require-the-certificate-api-for-deletion}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:certificate-backed-secrets-require-the-certificate-api-for-deletion`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Azure Key Vault marks certificate-backed secrets as `managed: true` and rejects `DELETE /secrets/{name}` with HTTP 405. AWS Secrets Manager does not expose that certificate distinction on DeleteSecret.

**Impact.** A caller can request DeleteSecret for a certificate-backed secret name and receive a failure even though the same name looks like a normal secret in list/describe flows.

**Workaround.** Delete the backing certificate through the Azure Key Vault certificate API or portal. aws2azure returns InvalidRequestException with a certificate-specific message when this backend rule is hit.

