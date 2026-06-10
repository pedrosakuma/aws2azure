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
