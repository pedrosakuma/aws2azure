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

This implements roadmap issue **#153**.

## What runs

Each service drives the proxy with the **official AWS SDK** (exactly as a
migrated application would) and verifies a self-contained lifecycle. Tests are
tagged `[Trait("Category", "RealAzure")]` and share a single out-of-process
proxy ([`RealAzureProxyFixture`](../../tests/Aws2Azure.IntegrationTests/Fixtures/RealAzureProxyFixture.cs)).

| AWS service | Azure backend | Round-trip | Provisioning |
|---|---|---|---|
| S3 | Blob Storage | CreateBucket → Put/Get/Delete object → DeleteBucket | Self-contained (the proxy creates the container) |
| DynamoDB | Cosmos DB | CreateTable → Put/Get/Delete item → DeleteTable | The Cosmos **database** must pre-exist; the test owns the table (container) |
| SQS | Service Bus | CreateQueue → Send/Receive/Delete → DeleteQueue | Self-contained (the proxy creates the queue) |
| Kinesis | Event Hubs | PutRecord | The Event Hub **must pre-exist** (`CreateStream` is not implemented) |
| Secrets Manager | Key Vault | secret lifecycle | Self-contained |

Every backend is gated **independently** on its own secret(s): a backend whose
secrets are absent **skips** (it does not fail), so fork PRs, local
`dotnet test`, and partially-provisioned environments stay green. When no
backend at all is configured the proxy process is never started.

## When it runs

- **Nightly** at 05:00 UTC (one hour after the emulator `integration` job, so a
  failure here points at real-Azure-only divergence).
- On **`workflow_dispatch`**.
- On PRs labelled **`run-real-azure`** (apply the label to validate a change
  against real Azure before merge).

A concurrency guard (`group: integration-real-azure`,
`cancel-in-progress: false`) prevents two runs from racing on the same Azure
resources.

## Required repository secrets

Add these under **Settings → Secrets and variables → Actions**. Omit a group to
skip that service.

| Secret | Service | Notes |
|---|---|---|
| `AZURE_BLOB_ACCOUNT` | S3 | Storage account name |
| `AZURE_BLOB_KEY` | S3 | Storage account key |
| `AZURE_BLOB_ENDPOINT` | S3 | *(optional)* override; defaults to `https://{account}.blob.core.windows.net` |
| `AZURE_COSMOS_ENDPOINT` | DynamoDB | e.g. `https://{acct}.documents.azure.com:443/` |
| `AZURE_COSMOS_KEY` | DynamoDB | Cosmos primary key |
| `AZURE_COSMOS_DATABASE` | DynamoDB | **Pre-existing** database id (e.g. `aws2azure-it`) |
| `AZURE_SB_CONNSTR` | SQS | Service Bus namespace connection string (`Endpoint=sb://…;SharedAccessKeyName=…;SharedAccessKey=…`) |
| `AZURE_EVENTHUBS_CONNSTR` | Kinesis | *(optional)* Event Hubs namespace connection string — preferred over the discrete fields below |
| `AZURE_EVENTHUBS_NAMESPACE` | Kinesis | Short namespace name (if not using the connstr) |
| `AZURE_EVENTHUBS_SAS_KEYNAME` | Kinesis | SAS rule name (if not using the connstr) |
| `AZURE_EVENTHUBS_SAS_KEY` | Kinesis | SAS key (if not using the connstr) |
| `AZURE_EVENTHUBS_STREAM` | Kinesis | **Pre-existing** Event Hub entity name |
| `AZURE_KEYVAULT_URL` | Secrets Manager | Vault URL |
| `AZURE_KEYVAULT_TENANT_ID` | Secrets Manager | Service principal tenant |
| `AZURE_KEYVAULT_CLIENT_ID` | Secrets Manager | Service principal app id |
| `AZURE_KEYVAULT_CLIENT_SECRET` | Secrets Manager | Service principal secret |

## Provisioning the Azure side

Create a dedicated, disposable resource group (e.g. `aws2azure-nightly`) and
provision **one** of each backend. The SAS-key shapes above keep CI simple; the
Cosmos and Event Hubs modules also support an Entra service-principal shape if
preferred.

1. **Service principal** with **Contributor** on the resource group (used at
   minimum for the Key Vault data plane and for any AAD-shaped backend). Grant
   it Key Vault **Secrets Officer** on the vault.
2. **Pre-create** the two resources the proxy does not provision on the data
   plane:
   - a Cosmos **database** named per `AZURE_COSMOS_DATABASE`;
   - an Event Hub **entity** named per `AZURE_EVENTHUBS_STREAM` (1 partition is
     enough for `PutRecord`).
3. **Budget alert** — add a Cost Management budget on the resource group (e.g.
   a few USD/month) with an email action group so a runaway loop is caught
   early. The smoke tests create and delete their own buckets/queues/tables, so
   steady-state cost is dominated by the always-on namespaces, not test data.

## Cleanup

Every test deletes the resources it creates in a `finally` block, so a failed
assertion **still** tears down its bucket / queue / table. The pre-existing
Cosmos database and Event Hub entity are intentionally **not** deleted (they are
shared, operator-owned fixtures). Because resource names are GUID-suffixed, a
crash that bypasses cleanup leaks at most one uniquely-named, empty entity per
run — periodically prune the resource group if desired.

## Running locally

```bash
# Export the secrets for the backends you want to exercise, then:
dotnet build -c Release
dotnet test tests/Aws2Azure.IntegrationTests -c Release --no-build \
  --filter "Category=RealAzure"
```

Backends without exported secrets skip; with none exported, every real-Azure
test skips.
