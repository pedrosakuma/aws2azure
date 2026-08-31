# secretsmanager design gap / Hierarchical (slash-containing) secret names require Key Vault name translation {#design-gap-secretsmanager-hierarchical--slash-containing--secret-names-require-key-vault-name-translation}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:secretsmanager:hierarchical--slash-containing--secret-names-require-key-vault-name-translation`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

Azure Key Vault secret names must match `^[0-9a-zA-Z-]+$` and be at most 127 characters, while AWS Secrets Manager names allow `/_+=.@-` and up to 512 characters. The proxy deterministically maps any non-Key-Vault-legal AWS name to a sanitized-prefix-plus-hash Key Vault name (a non-cryptographic FNV-1a 64-bit hash, chosen over a cryptographic hash since the only actor able to trigger a collision already owns every secret under that AWS account) before every Key Vault REST call, and preserves the exact original AWS name in the `aws2azure-secret-name` internal tag so all operations still report the AWS name the caller used.

**Impact.** Without this translation, any AWS-style hierarchical name would 404 against real Key Vault with a raw IIS error instead of a clean AWS error. The residual gap is names between 257 and 512 characters, which are past Key Vault's 256-character tag-value limit: CreateSecret still succeeds and echoes the exact AWS name in its own response, but ListSecrets can only recover the Key-Vault-legal encoded name for that specific secret (not the original AWS name) until it is renamed shorter.

**Workaround.** Keep hierarchical secret names at or below 256 characters to retain exact-name recovery in ListSecrets. Any length is otherwise supported end-to-end for CreateSecret/GetSecretValue/DescribeSecret/PutSecretValue/UpdateSecret/DeleteSecret/TagResource/UntagResource, which always receive and echo the caller-supplied AWS name directly.

