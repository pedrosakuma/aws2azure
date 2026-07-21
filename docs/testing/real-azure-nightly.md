# Nightly real-Azure integration tests

`aws2azure`'s default integration suite runs against **emulators** (Azurite,
the Service Bus / Event Hubs / Cosmos emulators) for a fast PR feedback loop.
Emulators are a **necessary but not sufficient** signal — they diverge from
real Azure on auth, default ports, throttling and feature surface. The
[`integration-real-azure`](../../.github/workflows/integration-real-azure.yml)
workflow therefore runs the bounded, six-service
[`real-azure-conformance.yaml`](real-azure-conformance.yaml) matrix through the
proxy against **live Azure**, nightly, to catch real-Azure-only regressions
(e.g. the AMQP port-defaulting bug fixed in `1560d11` that no emulator
surfaced).

This implements roadmap issue **#153**; the ephemeral provisioning flow below
implements **#257**, and the complete conformance/evidence layer implements
**#533**.

## How it runs: validate → deterministic → provision → real → evidence → deallocate

The nightly job is **self-contained** — there are no standing Azure resources
and no long-lived account-key secrets:

1. **Build and validate** the gap docs and declarative matrix.
2. **Run credential-independent checks**: deterministic HTTP failure injection
   plus only the exact Kinesis, SNS, and SQS AMQP mapping test identities named
   by the matrix. These checks produce TRX even when OIDC is unavailable. They
   verify AWS envelopes/retryability, but are **not real-Azure observations**.
3. **Log in to Azure via OIDC** (`azure/login@v2`, federated credentials — no
   stored client secret).
4. **Provision** a per-run, uniquely-named resource group
   (`aws2azure-it-<run_id>-<attempt>`) from
   [`deploy/realazure/main.bicep`](../../deploy/realazure/main.bicep): a Storage
   account, a Service Bus namespace, a serverless Cosmos DB account + database,
   an Event Hubs namespace + hub, an Event Grid custom topic + Storage Queue
   event subscription, and an RBAC Key Vault.
5. **Export** the freshly-minted connection details (fetched with
   `az … keys list`, masked) into the test environment.
6. **Run** the real-Azure matrix tests and write a separate TRX. If OIDC is
   unavailable, the tests execute their normal skip gates, so evidence records
   `skipped`/`not_run` rather than pretending Azure was observed.
7. **Generate evidence** and the divergence report from every available TRX.
   Scheduled and PR executions are explicit source validation: they do not emit
   a workload correctness candidate.
8. **Upload** source-validation TRX/evidence as
   `source-validation-real-azure-conformance`. A manual profile run that selects
   an exact sealed producer instead uploads `real-azure-conformance` with the
   sealed candidate identity, runtime hashes, config manifest, and one matching
   correctness candidate.
9. **Deallocate** — the shared cleanup first permanently deletes every Blob
   version (required because immutable storage with versioning protects
   non-empty accounts from deletion), deletes/purges Key Vault, requests
   resource-group deletion, and waits for Azure to confirm it. This runs in an
   `if: always()` teardown, so cleanup is attempted even when provisioning or
   tests fail, and a rejected or incomplete deletion fails visibly. A final
   gate then restores the captured test/report exit codes; report generation
   and cleanup never turn a failing test green.

A [`real-azure-reaper`](../../.github/workflows/real-azure-reaper.yml) workflow
runs every 6 hours as a backstop, permanently deleting blob versions from
immutable-versioned storage accounts and deleting any
`purpose=aws2azure-nightly` resource group older than `MAX_AGE_HOURS` (default
6) — covering the rare case where a force-cancelled run skips its teardown.

Cosmos DB account creation dominates the run (normally ~5–10 min). The job has
a hard **60-minute timeout**, global fixed concurrency
(`integration-real-azure`, no cancellation of the active run), and no unbounded
load: pagination uses a few entities/pages, batches contain fixed small entry
counts, and concurrency checks use a fixed handful of writers/consumers. This
is conformance, not throughput testing. Under normal Azure control-plane
conditions the full run should finish in roughly 15–30 minutes. The ephemeral
resource group, asynchronous teardown, six-hour reaper, and subscription Cost
Management budget cap both duration and cost; investigate any run approaching
the timeout rather than increasing workloads or the timeout.

The operational cost ceiling is **one active ephemeral resource group**:
Standard LRS Storage (including a Storage Queue used only as Event Grid
delivery evidence), one Standard Service Bus namespace, one serverless Cosmos
account, one capacity-1 Standard Event Hubs namespace with a two-partition hub,
one Event Grid custom topic + event subscription, and one Key Vault. No test
scales SKU/capacity or derives request count from input. Azure prices vary by
agreement and region, so the currency-denominated limit belongs in the
subscription Cost Management budget; set that budget to the operator-approved
nightly amount and do not raise these SKUs/counts to make a conformance test
pass.

## What runs

Every matrix scenario declares a required `evidence_source` and an explicit
`establishes_verification` boolean:

- `real_azure` means the scenario reaches a live Azure data plane or identity
  endpoint. Core/read/write/list/pagination/batch/concurrency scenarios and the
  isolated invalid-credential probes use this source.
- `deterministic` means the scenario uses an injected HTTP/AMQP or unit-test
  seam. Throttling, timeout, 503/service-unavailable, and cancellation probes
  use this source and must never be presented as observations of live Azure.
- `establishes_verification: true` is reserved for positive live-Azure
  core/read/write/list/pagination/batch/concurrency scenarios that demonstrate
  successful operation behavior. Deterministic and real-Azure
  invalid-credential/failure-only scenarios set it to `false`.

Validation rejects a missing or unknown source, a missing boolean, or a `true`
value on a non-positive/non-live scenario. Generated JSON and Markdown carry
both fields for each scenario. An operation is eligible for a future
`verified_real_azure` seal only if **all** scenarios referencing it pass
(including deterministic scenarios) and at least one passing reference is
`real_azure` with `establishes_verification: true`. Otherwise evidence records
`no_positive_real_azure_evidence`. Evidence generation never changes a seal.

Each service drives the proxy with the **official AWS SDK** (exactly as a
migrated application would) and verifies a self-contained lifecycle. Tests are
tagged `[Trait("Category", "RealAzure")]` and share a single out-of-process
proxy ([`RealAzureProxyFixture`](../../tests/Aws2Azure.IntegrationTests/Fixtures/RealAzureProxyFixture.cs)).

| AWS service | Azure backend | Round-trip | Provisioning |
|---|---|---|---|
| S3 | Blob Storage | Bucket/object lifecycle, ListObjectsV2 pagination, multi-object delete | Ephemeral (Bicep); the proxy creates the container |
| DynamoDB | Cosmos DB | Table/item lifecycle, indexed query pagination, batch read/write, concurrent conditional update | Ephemeral (Bicep) account + **database**; the test owns the table (container) |
| SQS | Service Bus | Queue/message lifecycle, queue pagination, send/delete batch, concurrent FIFO ordering | Ephemeral (Bicep); the proxy creates the queue |
| Kinesis | Event Hubs | Put/Get records, shard pagination, record batch, concurrent consumer progress | Ephemeral (Bicep) namespace + **hub** (`CreateStream` is not implemented) |
| SNS | Service Bus Topics | Topic/subscription lifecycle, list pagination, PublishBatch results | Ephemeral (Bicep); the test owns topics and subscriptions |
| SNS *(Event Grid backend, issue #630)* | Event Grid | Publish/PublishBatch via a per-topic `backend=EventGrid` override; subject, message attributes, and a genuine per-entry partial failure (oversized entry) verified end to end through the topic's Storage Queue event subscription — not only HTTP-level publish acceptance | Ephemeral (Bicep) custom topic + Storage Queue + event subscription; topic administration still uses the Service Bus Topics backend above |
| Secrets Manager | Key Vault | Secret lifecycle, version write/idempotency, list pagination | Ephemeral (Bicep) + data-plane RBAC; authenticates via a **federated token**, no client secret (issue #307) |
| DynamoDB *(Workload Identity)* | Cosmos DB | Put/Get/Delete item (table provisioned with the shared key — Cosmos rejects container DDL over an AAD token) | Ephemeral (Bicep) + data-plane RBAC; item CRUD via a **federated token** (issue #307) |
| Kinesis *(Workload Identity)* | Event Hubs | PutRecord | Ephemeral (Bicep) + data-plane RBAC; authenticates via a **federated token**, no SAS key (issue #307) |

Every real backend is gated **independently** on its environment: a backend
whose values are absent **skips** (it does not fail), so fork PRs, local
`dotnet test`, and the no-OIDC case stay honest and green when the
credential-independent checks pass. Selected deterministic tests still run.
When no backend at all is configured the proxy process is never started.

## When it runs

- **Nightly** at 05:00 UTC (one hour after the emulator `integration` job, so a
  failure here points at real-Azure-only divergence).
- On **`workflow_dispatch`**, optionally limited to one service and one
  scenario id. Scenario ids reused by multiple services require the service
  selector. `require_real_azure` defaults to true and rejects a run that
  produces no passing live-Azure scenario eligible to establish verification.
- On PRs labelled **`run-real-azure`** (apply the label to validate a change
  against real Azure before merge).

A concurrency guard (`group: integration-real-azure`,
`cancel-in-progress: false`) prevents two runs from racing.

The nightly and labelled-PR paths always execute the full matrix. A directed
manual run writes the selected plan into the evidence artifact. Before any
filter runs, the workflow discovers tests from each planned project and fails
if a matrix identity matches no test; this prevents VSTest's zero-match
success behavior from hiding stale references. Nightly runs also require
positive real-Azure verification evidence. Labelled PR runs keep the gate off
so forks without secrets remain truthful, green non-evidence runs.

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

# 3b. (Only for the Workload-Identity E2E scenario, issue #307.) Contributor
#     EXCLUDES Microsoft.Authorization/*/Write, so it cannot create the Event
#     Hubs / Service Bus data-plane role assignments the Bicep emits. Grant the
#     SP role-assignment-management rights too. (Cosmos uses its own SQL RBAC
#     plane, which Contributor can already write — this is only needed for the
#     Azure-RBAC namespaces.) Skip this if you leave AZURE_CLIENT_OBJECT_ID unset.
az role assignment create --assignee-object-id "$SP_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope "/subscriptions/$SUB"

# 4. Repo secrets consumed by azure/login.
gh secret set AZURE_CLIENT_ID --body "$APP_ID"
gh secret set AZURE_TENANT_ID --body "$TENANT"
gh secret set AZURE_SUBSCRIPTION_ID --body "$SUB"

# 5. (Optional) Object id of the SP, for the Workload-Identity E2E scenario
#    (issue #307): Bicep grants this principal the AAD data-plane roles
#    (Cosmos / Event Hubs / Service Bus) the proxy needs when a backend block
#    uses `authMode: workloadIdentity`. Leave unset to keep the shared-key
#    smoke matrix only — the data-plane role assignments are then skipped.
gh secret set AZURE_CLIENT_OBJECT_ID --body "$SP_ID"
```

The federated-credential subjects above cover scheduled / `workflow_dispatch`
runs on `main` and label-gated pull requests **from the repository itself**.
Fork PRs receive no OIDC token (and no secrets), so the job's gate skips
provisioning and real-Azure tests skip. Deterministic and focused AMQP tests
still run, and the evidence report contains the canonical run URL plus
`skipped`/`not_run` blockers. No operation becomes seal-eligible from such a
run.

> **Budget guardrail.** Add a Cost Management budget on the subscription with an
> email/action-group alert. Ephemeral resources only exist for the duration of a
> run, but the budget catches a stuck teardown or a misbehaving reaper.

## Workload Identity end-to-end (issue #307)

The shared-key smoke matrix proves the **translation** layer, but it never
exercises the **AAD token** auth flows shipped for #290 — emulators don't
validate bearer tokens and the shared-key path bypasses Entra entirely. The
nightly therefore also runs the AAD-data-plane-capable backends in this fixture
(Cosmos for DynamoDB, Event Hubs for Kinesis) under `authMode: workloadIdentity`
via a **second AWS credential entry** in the same proxy, so the proxy's
`WorkloadIdentityTokenSource` mints a **real** Entra token that **real Azure
RBAC** must accept. The shared-key and federated-token smokes run side by side
against the same live backends
([`DynamoDbRealAzureWorkloadIdentityTests`](../../tests/Aws2Azure.IntegrationTests/DynamoDb/DynamoDbRealAzureWorkloadIdentityTests.cs),
[`KinesisRealAzureWorkloadIdentityTests`](../../tests/Aws2Azure.IntegrationTests/Kinesis/KinesisRealAzureWorkloadIdentityTests.cs)).

> **Cosmos: only the data path goes through Workload Identity.** Cosmos native
> RBAC authorizes only *data-plane* actions over an AAD token; creating/deleting
> containers (what `CreateTable`/`DeleteTable` map to) is *control-plane* and is
> rejected with `cannot be authorized by AAD token in data plane`
> (https://aka.ms/cosmos-native-rbac). The DynamoDB WI smoke therefore provisions
> the table with the shared-key client and exercises only the item CRUD
> (Put/Get/Delete) — the actual AAD flow a migrated workload runs — through the
> federated-token client. Event Hubs has no such split: PutRecord (send) is a
> data-plane action the AAD token authorizes directly.

This reuses the workload-identity federation the job already relies on for
`azure/login` — no extra Entra setup beyond the two prerequisites:

1. **`AZURE_CLIENT_OBJECT_ID`** secret set (step 5 above) → the Bicep grants the
   OIDC SP the data-plane roles (Event Hubs / Service Bus Data Owner, Cosmos DB
   Built-in Data Contributor), and the SP holds **Role Based Access Control
   Administrator** so it can create those assignments.
2. The job requests a **second** GitHub OIDC token (audience
   `api://AzureADTokenExchange`, same `sub` as the login token, so the existing
   federated credentials validate it), writes it to
   `$RUNNER_TEMP/azure-federated-token.jwt`, and exports
   `AZURE_FEDERATED_TOKEN_FILE` / `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` — exactly
   the projected-token contract `WorkloadIdentityTokenSource` reads.

Both are gated: when `AZURE_CLIENT_OBJECT_ID` is unset the
`steps.gate.outputs.wi_enabled` flag is false, the token file is never minted,
and the Workload-Identity tests skip — the shared-key matrix is unchanged.

> **Managed Identity is out of scope here.** `authMode: managedIdentity` needs a
> real IMDS endpoint (an Azure VM/AKS pod), which a GitHub-hosted runner does
> not have. Covering it would require a self-hosted Azure runner; tracked
> separately.

## Secrets Manager (Key Vault) — ephemeral vault

Like the four data-plane backends, the Key Vault is provisioned ephemerally by
`deploy/realazure/main.bicep` (a uniquely-named, RBAC-authorized vault per run)
and the proxy authenticates to it with the **same GitHub OIDC workload-identity
service principal** the other AAD scenarios use — no standing vault and no client
secret to manage. The Bicep grants the SP the **Key Vault Secrets Officer** role
on the vault (gated on `principalId`, i.e. `AZURE_CLIENT_OBJECT_ID`), and the
workflow exports the vault URL from the deployment output as `AZURE_KEYVAULT_URL`.

The SecretsManager smoke therefore runs whenever the Workload-Identity scenario
is enabled (`AZURE_CLIENT_OBJECT_ID` set); it skips otherwise. Key Vault
soft-delete is mandatory, so the teardown explicitly deletes **and purges** the
vault (retention pinned to the 7-day minimum, purge protection off) to free the
unique name immediately rather than leaking a reserved name.

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
dotnet run --project tools/Aws2Azure.GapDocs --no-build -c Release -- --validate

# Credential-independent deterministic failures (all six exact methods in this class).
dotnet test tests/Aws2Azure.IntegrationTests -c Release --no-build \
  --filter "FullyQualifiedName~FailureConformance.DeterministicHttpFailureConformanceTests" \
  --results-directory TestResults/real-azure \
  --logger "trx;LogFileName=deterministic-failures.trx"

# Exact AMQP identities referenced by the matrix (11 executions: one method
# has three xUnit theory rows).
AMQP_FILTER='FullyQualifiedName=Aws2Azure.UnitTests.Sqs.AmqpSendMessageHandlersTests.SendMessage_maps_server_busy_to_503_so_aws_sdk_retries|FullyQualifiedName=Aws2Azure.UnitTests.Sqs.AmqpSendMessageHandlersTests.SendMessage_propagates_broker_reject_as_error|FullyQualifiedName=Aws2Azure.UnitTests.Kinesis.PutRecordHandlerTests.HandleAsync_maps_amqp_throttling_to_provisioned_throughput_exceeded|FullyQualifiedName=Aws2Azure.UnitTests.Kinesis.PutRecordHandlerTests.HandleAsync_maps_amqp_transient_failures_to_retryable_kinesis_error|FullyQualifiedName=Aws2Azure.UnitTests.Kinesis.PutRecordHandlerTests.HandleAsync_propagates_amqp_cancellation_without_success_body|FullyQualifiedName=Aws2Azure.UnitTests.Sns.PublishHandlerTests.HandleAsync_maps_amqp_throttle_to_sns_throttled_error|FullyQualifiedName=Aws2Azure.UnitTests.Sns.PublishHandlerTests.HandleAsync_maps_amqp_timeout_to_retryable_sns_error|FullyQualifiedName=Aws2Azure.UnitTests.Sns.PublishHandlerTests.HandleAsync_maps_amqp_send_failure_to_sns_error|FullyQualifiedName=Aws2Azure.UnitTests.Sns.PublishHandlerTests.HandleAsync_propagates_amqp_cancellation_without_success_body'
dotnet test tests/Aws2Azure.UnitTests -c Release --no-build \
  --filter "$AMQP_FILTER" \
  --results-directory TestResults/real-azure \
  --logger "trx;LogFileName=matrix-amqp-unit.trx"

# Live-Azure tests; excludes the deterministic class already run above.
dotnet test tests/Aws2Azure.IntegrationTests -c Release --no-build \
  --filter "Category=RealAzure&FullyQualifiedName!~FailureConformance.DeterministicHttpFailureConformanceTests" \
  --results-directory TestResults/real-azure \
  --logger "trx;LogFileName=real-azure.trx"

az group delete -n "$RG" --yes --no-wait    # always tear down
```

Backends without exported environment values skip; with none exported, every
real-Azure test skips while deterministic failures still execute. The CI
workflow additionally uses exact `FullyQualifiedName=` filters for the nine
unique AMQP methods named by the matrix; copy that filter when reproducing the
complete workflow locally. Each test deletes the bucket / queue / topic / table
it creates in a `finally` block, so the ephemeral resource group is the only
cleanup the harness itself does not perform.

## Managed Identity validation (manual)

The nightly covers shared-key/SAS backends and the testable
[`workloadIdentity` auth mode](../azure-authentication.md) with a GitHub OIDC
federated token. It does **not** cover `managedIdentity`, deliberately:

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

### Workload Identity (AKS, optional manual parity check)

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

## Divergence report & real-Azure seal (Theme C, #467)

The `integration-real-azure` workflow regenerates the gap-doc site after the
suite and uploads `docs/site/divergences.md` as the **`real-azure-divergences`**
artifact, with a one-line summary in the run's job summary. The report lists
every documented behaviour difference and which operations carry a real-Azure
seal vs. are implemented-but-unsealed.

To **seal** an operation as real-Azure verified, first download the
`source-validation-real-azure-conformance` artifact (or the sealed manual
`real-azure-conformance` artifact) from a successful, OIDC-enabled run and check
the service report shows every scenario for that operation passed and at least
one passing scenario has `real_azure` evidence with
`establishes_verification: true`. Then add
`verified_real_azure` to its gap-doc YAML with the date and canonical workflow
URL, e.g.:

```yaml
verified_real_azure: "2026-07-15 (https://github.com/pedrosakuma/aws2azure/actions/runs/123456789)"
```

It renders a ✅ in the coverage matrix / service page and removes the op from the
"implemented without a seal" list. Never promote from deterministic injection,
unit tests, emulator results, skipped/not-run evidence, or an unsuccessful
Azure run. Evidence generation itself never edits a seal.

## Declarative conformance evidence

[`real-azure-conformance.yaml`](real-azure-conformance.yaml) is the
machine-readable coverage plan for all registered services. Each scenario has a
priority, category, strict `evidence_source`, explicit
`establishes_verification`, affected gap-doc operations, and one or more
fully-qualified xUnit test identities. Optional topology/encoding coverage may
set `optional_coverage: true`; it remains visible in evidence but a skip or
failure does not block the base operation's seal. Optional coverage is accepted
only for positive real-Azure categories that do not establish verification, so
deterministic and failure-path guards cannot be downgraded. Planned identities
are allowed: until they appear in a TRX file they are reported as `not_run`.
`--validate` checks the matrix schema, the complete six-service set, and every
operation reference together with the normal gap docs.

Inspect the executable plan for the full matrix, one service, or one scenario:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- plan-conformance
dotnet run --project tools/Aws2Azure.GapDocs -- plan-conformance --service s3
dotnet run --project tools/Aws2Azure.GapDocs -- \
  plan-conformance --service s3 --scenario object-lifecycle
```

The deterministic JSON output includes selected scenarios and operations,
whether the selection contains positive real-Azure verification evidence, and
deduplicated test identities grouped by test project. A scenario id may be used
without `--service` only when it is unique across the matrix. The workflow
integration must verify that every planned identity is discovered before
constructing `dotnet test` filters; VSTest otherwise exits successfully when a
filter matches zero tests.

Generate immutable run evidence from one or more TRX files with:

```bash
dotnet run --project tools/Aws2Azure.GapDocs --no-build -c Release -- \
  --generate-evidence \
  --trx TestResults/real-azure \
  --run-id "$GITHUB_RUN_ID" \
  --run-url "$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID" \
  --evidence-output TestResults/real-azure-conformance \
  --service s3 \
  --require-real-azure
```

`--trx` accepts a file or directory and may be repeated. `--service` and
`--scenario` apply the same selector rules as `plan-conformance`.
`--require-real-azure` writes the evidence and then exits non-zero unless at
least one selected, passing `real_azure` scenario has
`establishes_verification: true`. The output contains
`real-azure-evidence.json`, an overall `summary.md`, and one Markdown report per
service under `services/`. An operation is only listed as eligible when every
referencing scenario passed and at least one passing reference is `real_azure`
with `establishes_verification: true`. Deterministic failures, skipped tests,
and missing results block eligibility; deterministic or failure-only passes
alone yield `no_positive_real_azure_evidence`.
Evidence generation never edits `verified_real_azure`; promotion remains a
reviewed gap-doc change.

Every workflow run uploads:

```text
real-azure-conformance/
  real-azure/
    evidence-bootstrap.trx
    deterministic-failures.trx
    matrix-amqp-unit.trx
    real-azure.trx
  real-azure-conformance/
    real-azure-evidence.json
    summary.md
    services/{s3,dynamodb,sqs,kinesis,sns,secretsmanager}.md

real-azure-divergences/
  divergences.md
```

The first artifact combines raw TRX with generated JSON/Markdown evidence; the
second preserves the repository-wide divergence dossier. `summary.md` is also
appended verbatim to the GitHub job summary. Both generators use the current
`github.run_id` and
`$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID`, so evidence
links to the immutable canonical run rather than a mutable branch URL.
