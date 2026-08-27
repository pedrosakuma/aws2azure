# Authorization migration: AWS policy concepts to Azure RBAC and Entra scoping

aws2azure intentionally does **not** implement an AWS IAM, bucket-policy, ACL, or
resource-policy evaluation engine. The proxy validates SigV4, resolves the
configured binding, and then calls Azure with that binding's configured backend
credentials. That is a **design choice, not a missing roadmap item**.

Use this guide when a workload currently relies on AWS-side authorization
surfaces that the proxy does not translate. For backend authentication modes
(Shared Key, SAS, Entra client secret, Managed Identity, Workload Identity),
see [Azure authentication](./azure-authentication.md).

## S3: bucket policies, bucket ACLs, and object ACLs

This is **by design, not planned**. See
[S3 design gap: No IAM / ACL / bucket-policy authorization model](./site/design-gaps/s3/no-iam---acl---bucket-policy-authorization-model.md).

aws2azure does not evaluate S3 bucket policies or ACLs. Authorization is the
static AWS-key-to-Azure-credential mapping, and ACLs are synthesized as
owner-only. If you previously used bucket policies or ACLs to separate teams,
apps, or environments, move that separation to Azure resource boundaries.

Use Azure-native controls instead:

- Put each trust boundary on its own storage account or at least its own
  container.
- Give Azure operators or direct Azure clients only container-scoped Blob RBAC.
- Use SAS only for direct temporary Blob access outside the proxy.
- Remember that S3 → Blob currently authenticates from the proxy with Storage
  Shared Key; this guide is about **authorization boundaries**, not changing the
  proxy's Blob auth mode.

Minimal example: grant a single Entra principal container-scoped access.

```bash
CONTAINER_SCOPE="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$ACCOUNT/blobServices/default/containers/$CONTAINER"

az role assignment create \
  --assignee-object-id "$PRINCIPAL_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" \
  --scope "$CONTAINER_SCOPE"
```

Treat the container as the Azure-side boundary that replaces an S3 bucket policy
statement. If two workloads should not share authorization, give them different
containers or different storage accounts instead of expecting the proxy to
interpret per-request policy documents.

## SNS: topic policies become Entra-scoped publisher identities plus Azure RBAC

This is **by design, not planned**. See
[SNS design gap: No IAM-backed policy surface](./site/design-gaps/sns/no-iam-backed-policy-surface.md).

aws2azure does not evaluate SNS topic policies and does not expose an IAM-backed
policy surface. If a workload previously relied on SNS policies to decide which
publisher could send to which topic, move that decision to Azure identity scope
and Azure RBAC.

Use Azure-native controls instead:

- Give each publisher boundary its own Entra application, service principal,
  Managed Identity, or Workload Identity.
- Configure that identity in the binding as documented in
  [Azure authentication](./azure-authentication.md).
- Assign only the send role on the specific Azure backend resource that should
  receive events.
- Prefer one Azure principal per app or environment instead of one broad shared
  identity.

Minimal example for the Service Bus Topics backend: grant one publisher identity
send rights to one topic.

```bash
TOPIC_SCOPE="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.ServiceBus/namespaces/$NAMESPACE/topics/$TOPIC"

az role assignment create \
  --assignee-object-id "$PUBLISHER_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Azure Service Bus Data Sender" \
  --scope "$TOPIC_SCOPE"
```

If this binding targets Event Grid instead of Service Bus Topics, use the same
pattern with the Event Grid topic resource id and the `EventGrid Data Sender`
role.

## Secrets Manager: resource policies and cross-account sharing move to Key Vault access design

This is **by design, not planned**. See
[Secrets Manager design gap: No resource policies or cross-account access](./site/design-gaps/secretsmanager/no-resource-policies-or-cross-account-access.md).

aws2azure does not translate Secrets Manager resource policies or cross-account
sharing. If you previously used a resource policy to let another AWS account or
role read a secret, redesign that access on the Azure side with explicit vault
scope and Entra principals.

Use Azure-native controls instead:

- Put different trust boundaries in different vaults when you need hard
  separation.
- Give each consuming or mutating workload its own Entra application, service
  principal, Managed Identity, or Workload Identity.
- In RBAC mode, assign `Key Vault Secrets User` for read-only access or
  `Key Vault Secrets Officer` for read/write/delete flows.
- If the vault still uses legacy access policies, grant only the required secret
  permissions on that vault.

Minimal example in RBAC mode: grant one principal secret read/write/delete
rights on one vault.

```bash
VAULT_ID=$(az keyvault show --resource-group "$RG" --name "$VAULT" --query id --output tsv)

az role assignment create \
  --assignee-object-id "$PRINCIPAL_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets Officer" \
  --scope "$VAULT_ID"
```

Legacy access-policy mode stays possible when a vault has not moved to RBAC:

```bash
az keyvault set-policy \
  --resource-group "$RG" \
  --name "$VAULT" \
  --object-id "$PRINCIPAL_OBJECT_ID" \
  --secret-permissions get list set delete
```

Cross-account sharing becomes a vault and principal design problem, not a
secret-attached policy document. Prefer separate vaults or separate principals
per tenant, environment, or application boundary.
