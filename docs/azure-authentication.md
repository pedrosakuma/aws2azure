# Azure authentication: Managed Identity & Workload Identity

By default each AAD-capable backend block authenticates to Azure with a
**client-secret** service principal (`tenantId` + `clientId` + `clientSecret`).
That secret is long-lived and has to be stored in the proxy config. On Azure
compute you can drop the secret entirely and let the platform mint short-lived
tokens for you via **Managed Identity** (IMDS) or **AKS Workload Identity**
(federated service-account token). This page documents how to configure each
mode per hosting scenario.

> **Scope.** Token-based AAD auth — and therefore Managed / Workload Identity —
> applies to the **five AAD-capable backends**:
>
> | AWS service | Azure backend | Config block |
> |---|---|---|
> | DynamoDB | Cosmos DB | `azure.cosmos` |
> | Kinesis | Event Hubs | `azure.eventHubs` |
> | SNS (topics) | Service Bus topics | `azure.serviceBusTopics` |
> | SNS (topics) | Event Grid | `azure.eventGrid` |
> | Secrets Manager | Key Vault | `azure.keyVault` |
>
> S3 → Blob (`azure.blob`, account key) and SQS → Service Bus queues
> (`azure.serviceBus`, SAS) do **not** yet support token-based AAD auth; they
> remain key/SAS-only. Those are tracked separately and are out of scope here.

---

## The three auth modes

Every AAD-capable block carries an `authMode` field:

```jsonc
"authMode": "clientSecret"     // default — service principal + secret
"authMode": "managedIdentity"  // IMDS (system- or user-assigned MI)
"authMode": "workloadIdentity" // AKS federated service-account token
```

The JSON value is camel-case and case-insensitive. Omitting `authMode` is
identical to `"clientSecret"`, so existing configs keep working unchanged.

### 1. `clientSecret` (default)

The classic Entra ID *client credentials* flow. Requires all three fields
together:

```jsonc
"cosmos": {
  "endpoint": "https://my-cosmos.documents.azure.com:443/",
  "databaseName": "aws2azure",
  "authMode": "clientSecret",        // optional; this is the default
  "tenantId":     "00000000-0000-0000-0000-000000000000",
  "clientId":     "11111111-1111-1111-1111-111111111111",
  "clientSecret": "the-app-registration-secret"
}
```

No Azure compute required — works anywhere, including local development. The
trade-off is a long-lived secret in config that you must rotate.

### 2. `managedIdentity` (IMDS)

Acquires tokens from the Azure Instance Metadata Service (IMDS) at
`http://169.254.169.254/metadata/identity/oauth2/token`. Requires the proxy to
run on **Azure compute with a Managed Identity assigned** (VM, VMSS, App
Service, Container Apps, AKS pod with the kubelet identity, …).

- **System-assigned MI** — omit `clientId` (and `tenantId`/`clientSecret`):

  ```jsonc
  "cosmos": {
    "endpoint": "https://my-cosmos.documents.azure.com:443/",
    "databaseName": "aws2azure",
    "authMode": "managedIdentity"
  }
  ```

- **User-assigned MI** — set `clientId` to the user-assigned identity's client
  id so IMDS knows which identity to issue a token for:

  ```jsonc
  "cosmos": {
    "endpoint": "https://my-cosmos.documents.azure.com:443/",
    "databaseName": "aws2azure",
    "authMode": "managedIdentity",
    "clientId": "22222222-2222-2222-2222-222222222222"
  }
  ```

`tenantId` and `clientSecret` **must not** be set for `managedIdentity` —
startup validation rejects them (the platform supplies the tenant; there is no
secret).

> **App Service / Container Apps variant.** These platforms expose a per-app
> token endpoint via the `IDENTITY_ENDPOINT` and `IDENTITY_HEADER` environment
> variables instead of the IMDS IP. The proxy auto-detects them: when both are
> present it uses that endpoint, otherwise it falls back to the IMDS address. No
> extra config is needed — the same `"authMode": "managedIdentity"` works.

### 3. `workloadIdentity` (AKS)

Implements the AKS **Workload Identity** federated-credential exchange: the
proxy reads a kubelet-projected service-account JWT and exchanges it for an
Entra ID token (no secret). The tenant, client id and token-file path all come
from the standard `AZURE_*` environment variables that the Workload Identity
webhook injects into the pod — so the config block carries **only**
`"authMode": "workloadIdentity"`:

```jsonc
"cosmos": {
  "endpoint": "https://my-cosmos.documents.azure.com:443/",
  "databaseName": "aws2azure",
  "authMode": "workloadIdentity"
}
```

Required pod environment (injected automatically when the pod's service account
is annotated and labelled for Workload Identity):

| Env var | Purpose |
|---|---|
| `AZURE_TENANT_ID` | Entra tenant of the federated app registration |
| `AZURE_CLIENT_ID` | Client id of the federated app registration |
| `AZURE_FEDERATED_TOKEN_FILE` | Path to the projected SA token (re-read every refresh) |
| `AZURE_AUTHORITY_HOST` | *(optional)* authority; defaults to `https://login.microsoftonline.com/` |

Setting `tenantId`/`clientId`/`clientSecret` inline alongside
`"authMode": "workloadIdentity"` is a startup error — they would be ignored, so
the proxy refuses to start rather than mislead you. Startup validation also
fails loudly if `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` or
`AZURE_FEDERATED_TOKEN_FILE` are missing.

---

## Sharing one identity across backends: the `azureIdentities` pool

If several backends use the **same** identity you can name it once in a
top-level `azureIdentities` map and reference it by name from each block's
`identity` field, instead of repeating `authMode`/`clientId` everywhere:

```jsonc
{
  "azureIdentities": {
    "prod-mi": {
      "authMode": "managedIdentity",
      "clientId": "22222222-2222-2222-2222-222222222222"
    }
  },
  "credentials": [
    {
      "awsAccessKeyId": "AKIA...",
      "awsSecretAccessKey": "...",
      "azure": {
        "cosmos": {
          "endpoint": "https://my-cosmos.documents.azure.com:443/",
          "databaseName": "aws2azure",
          "identity": "prod-mi"
        },
        "keyVault": {
          "vaultUrl": "https://my-vault.vault.azure.net/",
          "identity": "prod-mi"
        }
      }
    }
  ]
}
```

Rules (all enforced at startup):

- A named identity carries exactly the AAD shape: `authMode`, `tenantId`,
  `clientId`, `clientSecret`. It is resolved into each referencing block before
  the modules read the config.
- Name lookup is **case-sensitive**. A reference to a name not present in
  `azureIdentities` fails startup (dangling reference).
- A block may use **either** an `identity` reference **or** inline AAD fields —
  not both. Combining them is a startup error.

---

## Required Azure RBAC

Managed / Workload Identity replaces *how* the proxy authenticates, not *what*
it is authorized to do. The identity (system MI, user MI, or the federated app
registration) still needs a role assignment on each target resource. Typical
data-plane roles:

| Backend | Role (data-plane) | Scope |
|---|---|---|
| Cosmos DB (NoSQL) | `Cosmos DB Built-in Data Contributor` (SQL role, assigned via `az cosmosdb sql role assignment`) | Account / database |
| Event Hubs | `Azure Event Hubs Data Sender` (+ `Data Receiver` if consuming) | Namespace / hub |
| Service Bus topics | `Azure Service Bus Data Sender` / `Data Receiver` (+ management role if the proxy creates topics) | Namespace |
| Event Grid | `EventGrid Data Sender` | Topic |
| Key Vault | `Key Vault Secrets User` (or Officer for write) under RBAC, or an access policy under the legacy model | Vault |

> Cosmos DB's control-plane `Contributor` role does **not** grant data-plane
> access. Use the SQL-specific data role assignment.

---

## Startup-validation summary

The config is validated once at startup and the proxy **fails loud** rather than
booting with an ambiguous credential shape:

- `clientSecret`: requires `tenantId` + `clientId` + `clientSecret` together;
  mutually exclusive with the block's SAS/account-key shape.
- `managedIdentity`: `clientSecret` and `tenantId` must be absent; `clientId` is
  optional (present ⇒ user-assigned, absent ⇒ system-assigned). A blank/
  whitespace `clientId` is treated as system-assigned.
- `workloadIdentity`: `tenantId`/`clientId`/`clientSecret` must be absent;
  `AZURE_TENANT_ID` + `AZURE_CLIENT_ID` + `AZURE_FEDERATED_TOKEN_FILE` must be
  present in the environment.
- `identity` reference: must resolve to an entry in `azureIdentities` and must
  not be combined with inline AAD fields.

---

## Validating against real Azure

Managed Identity and Workload Identity **cannot be exercised by the emulators**
(Azurite, the Service Bus / Cosmos emulators) — there is no IMDS endpoint and no
federation. They also can't be meaningfully verified in the nightly CI job,
because GitHub-hosted runners are themselves Azure VMs whose IMDS identity is
**not** our provisioned identity and has no RBAC on the test resources. The
authoritative validation is therefore a **manual procedure run on real Azure
compute** — see
[Managed / Workload Identity validation](./testing/real-azure-nightly.md#managed--workload-identity-validation-manual).
