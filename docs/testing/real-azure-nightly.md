# Nightly real-Azure integration tests

`aws2azure`'s default integration suite runs against **emulators** (Azurite,
the Service Bus / Event Hubs / Cosmos emulators) for a fast PR feedback loop.
Emulators are a **necessary but not sufficient** signal — they diverge from
real Azure on auth, default ports, throttling and feature surface. The
[`integration-real-azure`](../../.github/workflows/integration-real-azure.yml)
workflow therefore runs a small set of CRUD **smoke round-trips** through the
proxy against **live Azure**, nightly, to catch real-Azure-only regressions
(e.g. the AMQP port-defaulting bug fixed in `1560d11` that no emulator
surfaced).

This implements roadmap issue **#153**; the ephemeral provisioning flow below
implements **#257**.

## How it runs: provision → test → deallocate

The nightly job is **self-contained** — there are no standing Azure resources
and no long-lived account-key secrets:

1. **Log in to Azure via OIDC** (`azure/login@v2`, federated credentials — no
   stored client secret).
2. **Provision** a per-run, uniquely-named resource group
   (`aws2azure-it-<run_id>-<attempt>`) from
   [`deploy/realazure/main.bicep`](../../deploy/realazure/main.bicep): a Storage
   account, a Service Bus namespace, a serverless Cosmos DB account + database,
   and an Event Hubs namespace + hub.
3. **Export** the freshly-minted connection details (fetched with
   `az … keys list`, masked) into the test environment.
4. **Run** the `Category=RealAzure` suite against them.
5. **Deallocate** — `az group delete --yes --no-wait` in an `if: always()`
   teardown, so the resource group is removed even when provisioning or the
   tests fail.

A [`real-azure-reaper`](../../.github/workflows/real-azure-reaper.yml) workflow
runs every 6 hours as a backstop, deleting any `purpose=aws2azure-nightly`
resource group older than `MAX_AGE_HOURS` (default 6) — covering the rare case
where a force-cancelled run skips its teardown.

Cosmos DB account creation dominates the run (~5–10 min); the full
provision → test → deallocate cycle is well within a nightly budget.

## What runs

Each service drives the proxy with the **official AWS SDK** (exactly as a
migrated application would) and verifies a self-contained lifecycle. Tests are
tagged `[Trait("Category", "RealAzure")]` and share a single out-of-process
proxy ([`RealAzureProxyFixture`](../../tests/Aws2Azure.IntegrationTests/Fixtures/RealAzureProxyFixture.cs)).

| AWS service | Azure backend | Round-trip | Provisioning |
|---|---|---|---|
| S3 | Blob Storage | CreateBucket → Put/Get/Delete object → DeleteBucket | Ephemeral (Bicep); the proxy creates the container |
| DynamoDB | Cosmos DB | CreateTable → Put/Get/Delete item → DeleteTable | Ephemeral (Bicep) account + **database**; the test owns the table (container) |
| SQS | Service Bus | CreateQueue → Send/Receive/Delete → DeleteQueue | Ephemeral (Bicep); the proxy creates the queue |
| Kinesis | Event Hubs | PutRecord | Ephemeral (Bicep) namespace + **hub** (`CreateStream` is not implemented) |
| Secrets Manager | Key Vault | secret lifecycle | **Standing** — runs only when `AZURE_KEYVAULT_*` secrets are set (see below) |

Every backend is gated **independently** on its environment: a backend whose
values are absent **skips** (it does not fail), so fork PRs, local
`dotnet test`, and the no-OIDC case stay green. When no backend at all is
configured the proxy process is never started.

## When it runs

- **Nightly** at 05:00 UTC (one hour after the emulator `integration` job, so a
  failure here points at real-Azure-only divergence).
- On **`workflow_dispatch`**.
- On PRs labelled **`run-real-azure`** (apply the label to validate a change
  against real Azure before merge).

A concurrency guard (`group: integration-real-azure`,
`cancel-in-progress: false`) prevents two runs from racing.

## One-time operator setup (OIDC)

The job authenticates to Azure with **workload-identity federation** — no
client secret is stored in the repo. Run once against the target subscription:

```bash
SUB=<subscription-id>
TENANT=<tenant-id>
REPO=pedrosakuma/aws2azure

# 1. App registration + service principal for GitHub Actions.
APP_ID=$(az ad app create --display-name aws2azure-github-oidc --query appId -o tsv)
az ad sp create --id "$APP_ID"

# 2. Federated credentials for the contexts the workflow runs in.
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$REPO"':ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-pull-request",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$REPO"':pull_request",
  "audiences": ["api://AzureADTokenExchange"]
}'

# 3. Subscription-scoped Contributor (needs resource-group create/delete).
SP_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv)
az role assignment create --assignee-object-id "$SP_ID" \
  --assignee-principal-type ServicePrincipal \
  --role Contributor --scope "/subscriptions/$SUB"

# 4. Repo secrets consumed by azure/login.
gh secret set AZURE_CLIENT_ID --body "$APP_ID"
gh secret set AZURE_TENANT_ID --body "$TENANT"
gh secret set AZURE_SUBSCRIPTION_ID --body "$SUB"
```

The federated-credential subjects above cover scheduled / `workflow_dispatch`
runs on `main` and label-gated pull requests **from the repository itself**.
Fork PRs receive no OIDC token (and no secrets), so the job's gate skips
provisioning and the tests skip — fork contributors never see red CI for a
check they cannot run.

> **Budget guardrail.** Add a Cost Management budget on the subscription with an
> email/action-group alert. Ephemeral resources only exist for the duration of a
> run, but the budget catches a stuck teardown or a misbehaving reaper.

## Secrets Manager (Key Vault) — standing secrets

Unlike the four data-plane backends, the proxy authenticates to Key Vault with a
**service principal** (client id + secret), not an account key, so it is not
auto-provisioned. The SecretsManager smoke runs only when these standing repo
secrets are present (otherwise it skips):

| Secret | Notes |
|---|---|
| `AZURE_KEYVAULT_URL` | Vault URL |
| `AZURE_KEYVAULT_TENANT_ID` | Service principal tenant |
| `AZURE_KEYVAULT_CLIENT_ID` | Service principal app id |
| `AZURE_KEYVAULT_CLIENT_SECRET` | Service principal secret |

## Running locally

You can run the same flow on a workstation with the Azure CLI logged in:

```bash
RG=aws2azure-it-local
az group create -n "$RG" -l eastus2 --tags purpose=aws2azure-nightly
az deployment group create -g "$RG" -n aws2azure-realazure \
  -f deploy/realazure/main.bicep \
  -p cosmosDatabaseName=dynamodb eventHubName=kinesis-smoke

# Export the connection details (see the workflow's "Export connection info"
# step for the exact az queries), then:
dotnet build -c Release
dotnet test tests/Aws2Azure.IntegrationTests -c Release --no-build \
  --filter "Category=RealAzure"

az group delete -n "$RG" --yes --no-wait    # always tear down
```

Backends without exported environment values skip; with none exported, every
real-Azure test skips. Each test also deletes the bucket / queue / table it
creates in a `finally` block, so the ephemeral resource group is the only
cleanup the harness itself does not perform.

## Managed / Workload Identity validation (manual)

The nightly job authenticates the proxy to the data-plane backends with
**account keys / SAS / a service-principal secret** — shapes that work from any
runner. It does **not** cover the secret-less
[`managedIdentity` / `workloadIdentity` auth modes](../azure-authentication.md),
and deliberately so:

- **Emulators have no IMDS / federation.** Azurite and the Service Bus / Cosmos
  emulators can't issue Managed Identity tokens, so the emulator `integration`
  job can't exercise these modes at all.
- **The CI runner's identity is the wrong principal.** GitHub-hosted runners are
  themselves Azure VMs, so IMDS at `169.254.169.254` *exists* — but the token it
  returns is the runner's identity, which is **not** our provisioned identity and
  has **no RBAC** on the test resources. A Managed Identity smoke launched from
  the nightly job would either fail authorization or silently authenticate as the
  wrong principal, so an automated assertion there would be misleading. We
  therefore do **not** ship an always-skipping CI harness for it.

Instead, validate these modes with the following **manual procedure on real Azure
compute** whenever the token-source or auth-mode wiring changes
(`EntraIdTokenProvider`, `ImdsTokenSource`, `WorkloadIdentityTokenSource`,
`ProxyConfigValidator` identity resolution).

### Managed Identity (VM / VMSS / App Service / Container Apps)

1. Provision a target backend (e.g. a Cosmos DB account + database) and a piece
   of Azure compute with a Managed Identity — system-assigned, or a user-assigned
   identity attached to it.
2. Grant the identity the data-plane role for that backend (see the RBAC table in
   [Azure authentication](../azure-authentication.md#required-azure-rbac)). For
   Cosmos, assign the SQL `Cosmos DB Built-in Data Contributor` data role to the
   identity's principal id.
3. On that compute, run the proxy (the published AOT binary or the container)
   with a config whose backend block uses Managed Identity and **no secret**:

   ```jsonc
   "cosmos": {
     "endpoint": "https://<acct>.documents.azure.com:443/",
     "databaseName": "aws2azure",
     "authMode": "managedIdentity"
     // add "clientId": "<user-assigned-client-id>" for a user-assigned identity
   }
   ```

4. Drive it with the AWS SDK, e.g. a DynamoDB `PutItem` + `GetItem` round-trip
   against the proxy's Host-routed endpoint, and confirm the item lands in
   Cosmos. Success proves the proxy acquired a token from IMDS and called Azure
   with **no `clientSecret` in config** — the #290 acceptance criterion.

### Workload Identity (AKS)

1. On an AKS cluster with the Workload Identity webhook enabled, create an Entra
   app registration with a **federated credential** trusting the cluster's OIDC
   issuer + the pod's service-account subject.
2. Grant that app registration the data-plane role on the target backend.
3. Label the pod's service account for Workload Identity and annotate it with the
   app's client id; deploy the proxy with a backend block using
   `"authMode": "workloadIdentity"` (no inline tenant/client/secret — they come
   from the injected `AZURE_*` env vars).
4. Drive it with the AWS SDK as above and confirm the round-trip. Success proves
   the federated service-account token was exchanged for an Entra token and used
   against Azure with **no stored secret**.

Record any real-Azure divergence from emulator behaviour in the relevant gap doc
or here, per the project's emulator caveat.
