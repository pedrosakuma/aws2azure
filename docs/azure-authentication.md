# Azure authentication: Managed Identity & Workload Identity

Each Azure backend in a binding splits its **non-secret** topology (`target`)
from its **secret** (`auth`). The `auth.mode` discriminator selects the auth
shape. For AAD-capable backends the default is a **client-secret** service
principal (`tenantId` + `clientId` + `clientSecret`). That secret is long-lived
and has to be stored in the proxy config. On Azure compute you can drop the
secret entirely and let the platform mint short-lived tokens for you via
**Managed Identity** (IMDS) or **AKS Workload Identity** (federated
service-account token). This page documents how to configure each mode per
hosting scenario.

> **Scope.** Token-based AAD auth — and therefore Managed / Workload Identity —
> applies to the **five AAD-capable backends**:
>
> | AWS service | Azure backend (`kind`) | Binding path |
> |---|---|---|
> | DynamoDB | Cosmos DB (`cosmos`) | `bindings[].azure.dynamodb` |
> | Kinesis | Event Hubs (`eventHubs`) | `bindings[].azure.kinesis` |
> | SNS (topics) | Service Bus topics (`serviceBusTopics`) | `bindings[].azure.sns` |
> | SNS (topics) | Event Grid (`eventGrid`) | `bindings[].azure.sns` |
> | Secrets Manager | Key Vault (`keyVault`) | `bindings[].azure.secretsmanager` |
>
> S3 → Blob (`kind: blob`, `auth.mode: "sharedKey"`) and SQS → Service Bus
> queues (`kind: serviceBus`, `auth.mode: "sas"`) do **not** yet support
> token-based AAD auth; they remain key/SAS-only. Those are tracked separately
> and are out of scope here.

---

## The `auth.mode` selector

Every backend's `auth` block carries a `mode` field. The full set of modes is:

```jsonc
"auth": { "mode": "sharedKey" }       // account/master/access key (blob, cosmos, eventGrid)
"auth": { "mode": "sas" }             // SAS keyName + key (serviceBus, serviceBusTopics, eventHubs)
"auth": { "mode": "clientSecret" }    // AAD service principal + secret
"auth": { "mode": "managedIdentity" } // AAD via IMDS (system- or user-assigned MI)
"auth": { "mode": "workloadIdentity" }// AAD via AKS federated service-account token
"auth": { "mode": "reference" }       // reuse a shared azureIdentities pool entry
```

The JSON value is camel-case and case-insensitive. The three AAD modes
(`clientSecret`, `managedIdentity`, `workloadIdentity`) are covered below; they
apply only to the five AAD-capable backends in the scope table above.

### 1. `clientSecret`

The classic Entra ID *client credentials* flow. Requires all three fields
together:

```jsonc
"dynamodb": {
  "kind": "cosmos",
  "target": {
    "endpoint": "https://my-cosmos.documents.azure.com:443/",
    "databaseName": "aws2azure"
  },
  "auth": {
    "mode": "clientSecret",
    "tenantId":     "00000000-0000-0000-0000-000000000000",
    "clientId":     "11111111-1111-1111-1111-111111111111",
    "clientSecret": "the-app-registration-secret"
  }
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
  "dynamodb": {
    "kind": "cosmos",
    "target": { "endpoint": "https://my-cosmos.documents.azure.com:443/", "databaseName": "aws2azure" },
    "auth":   { "mode": "managedIdentity" }
  }
  ```

- **User-assigned MI** — set `clientId` to the user-assigned identity's client
  id so IMDS knows which identity to issue a token for:

  ```jsonc
  "dynamodb": {
    "kind": "cosmos",
    "target": { "endpoint": "https://my-cosmos.documents.azure.com:443/", "databaseName": "aws2azure" },
    "auth":   { "mode": "managedIdentity", "clientId": "22222222-2222-2222-2222-222222222222" }
  }
  ```

`tenantId` and `clientSecret` **must not** be set for `managedIdentity` —
startup validation rejects them (the platform supplies the tenant; there is no
secret).

> **App Service / Container Apps variant.** These platforms expose a per-app
> token endpoint via the `IDENTITY_ENDPOINT` and `IDENTITY_HEADER` environment
> variables instead of the IMDS IP. The proxy auto-detects them: when both are
> present it uses that endpoint, otherwise it falls back to the IMDS address. No
> extra config is needed — the same `"mode": "managedIdentity"` works.

### 3. `workloadIdentity` (AKS)

Implements the AKS **Workload Identity** federated-credential exchange: the
proxy reads a kubelet-projected service-account JWT and exchanges it for an
Entra ID token (no secret). The tenant, client id and token-file path all come
from the standard `AZURE_*` environment variables that the Workload Identity
webhook injects into the pod — so the `auth` block carries **only**
`"mode": "workloadIdentity"`:

```jsonc
"dynamodb": {
  "kind": "cosmos",
  "target": { "endpoint": "https://my-cosmos.documents.azure.com:443/", "databaseName": "aws2azure" },
  "auth":   { "mode": "workloadIdentity" }
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
`"mode": "workloadIdentity"` is a startup error — they would be ignored, so
the proxy refuses to start rather than mislead you. Startup validation also
fails loudly if `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` or
`AZURE_FEDERATED_TOKEN_FILE` are missing.

---

## Sharing one identity across backends: the `azureIdentities` pool

If several backends use the **same** identity you can name it once in a
top-level `azureIdentities` map and reference it from each backend's `auth`
block with `"mode": "reference"` + `identity`, instead of repeating the AAD
shape everywhere:

```jsonc
{
  "azureIdentities": {
    "prod-mi": {
      "authMode": "managedIdentity",
      "clientId": "22222222-2222-2222-2222-222222222222"
    }
  },
  "bindings": [
    {
      "aws": {
        "accessKeyId": "AKIA...",
        "secretAccessKey": "..."
      },
      "azure": {
        "dynamodb": {
          "kind": "cosmos",
          "target": { "endpoint": "https://my-cosmos.documents.azure.com:443/", "databaseName": "aws2azure" },
          "auth":   { "mode": "reference", "identity": "prod-mi" }
        },
        "secretsmanager": {
          "kind": "keyVault",
          "target": { "vaultUrl": "https://my-vault.vault.azure.net/" },
          "auth":   { "mode": "reference", "identity": "prod-mi" }
        }
      }
    }
  ]
}
```

Rules (all enforced at startup):

- A named identity carries the AAD shape under `authMode`, `tenantId`,
  `clientId`, `clientSecret`. It is resolved into each referencing backend
  before the modules read the config.
- Name lookup is **case-sensitive**. A `"mode": "reference"` whose `identity`
  is not present in `azureIdentities` fails startup (dangling reference).
- A backend uses **either** `"mode": "reference"` **or** an inline AAD mode —
  not both.

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
- `reference`: `auth.identity` must resolve to an entry in `azureIdentities`
  and must not be combined with inline AAD fields.

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
