# Adopter real-Azure conformance kit

This guide repackages the repository's existing real-Azure execution engine
for **external adopters** who want to validate `aws2azure` against **their own
Azure subscription** before production rollout. It reuses the same moving parts
this repo's nightly job already trusts:

- [`eng/repro-real-azure.sh`](../../eng/repro-real-azure.sh) for provision / export / teardown
- [`deploy/realazure/main.bicep`](../../deploy/realazure/main.bicep) for the ephemeral Azure resources
- the existing `Category=RealAzure` xUnit suite in `tests/Aws2Azure.IntegrationTests`
- `tools/Aws2Azure.GapDocs` to turn the resulting TRX into an exportable YAML report

It does **not** change this repository's own CI cadence, concurrency group, or
nightly subscription usage. The repo's `.github/workflows/integration-real-azure.yml`
remains the canonical CI path; this guide is only the adopter-facing, local/manual
entrypoint requested by issue **#945**.

## What you get

Running the steps below produces a YAML report shaped for the same evidence-led
qualification workflow used under `docs/workloads/evidence/`, but aimed at
self-validation:

- top-level `schema_version`, `artifact_kind`, and overall `verdict`
- candidate identity (`candidate.id`, optional git/artifact/config digests)
- provenance (`run_id`, region, resource group, execution engine, matrix path)
- per-service / per-operation `passed`, `failed`, or `inconclusive` verdicts
- the underlying scenario/blocker details and any `unmapped_tests`

A `passed` operation means every blocking matrix scenario for that operation
passed and at least one passing scenario supplied positive `real_azure`
verification evidence. `failed` means at least one blocking scenario failed.
`inconclusive` means the operation was skipped, never ran, or lacked positive
real-Azure verification evidence.

## Prerequisites

- Azure CLI (`az`) installed and already authenticated with `az login` against
  **your own** subscription. The identity you use needs **Contributor** on the
  target subscription/resource-group scope because the script creates a resource
  group and deploys [`deploy/realazure/main.bicep`](../../deploy/realazure/main.bicep).
- `jq` installed.
- .NET SDK matching this repo.
- A full checkout of this repository (or an equivalent downstream copy that
  still contains the gap/workload docs, `eng/repro-real-azure.sh`,
  `docs/testing/real-azure-conformance.yaml`, `tests/Aws2Azure.IntegrationTests`,
  `tests/Aws2Azure.UnitTests`, and `tools/Aws2Azure.GapDocs`).

> **Cost / cleanup warning.** This provisions **real, billed** Azure resources:
> one Standard LRS Storage account (+ a Storage Queue), one Standard Service Bus
> namespace, one serverless Cosmos DB account + database, one capacity-1
> Standard Event Hubs namespace + hub, one Event Grid custom topic + event
> subscription, and one Key Vault. Cosmos DB account creation alone typically
> takes 5–10 minutes. Nothing here auto-expires for adopters: always run the
> teardown step, and treat the repo's shared reaper as a best-effort backstop,
> not your primary cleanup plan.

## Provision ephemeral Azure resources

```bash
RG=aws2azure-adopter-self-validation
REGION=eastus2

eng/repro-real-azure.sh up --yes \
  --resource-group "$RG" \
  --location "$REGION"
```

This creates a fresh resource group in **your** subscription, deploys the same
Bicep the nightly job uses, and writes a sourceable env file (default
`.local/real-azure.env`) containing the backend coordinates the integration
fixture reads.

## Run the matrix evidence set

```bash
source .local/real-azure.env

RESULTS_DIR=TestResults/adopter-real-azure
rm -rf "$RESULTS_DIR"
PLAN="$RESULTS_DIR/conformance-plan.json"
mkdir -p "$RESULTS_DIR"

dotnet build -c Release
dotnet run --project tools/Aws2Azure.GapDocs --no-build -c Release -- \
  plan-conformance --output "$PLAN"
dotnet run --project tools/Aws2Azure.GapDocs --no-build -c Release -- \
  validate-conformance-discovery --plan "$PLAN" --configuration Release --no-build

filter_for() {
  jq -r --arg project "$1" '
    [.test_projects[]
     | select(.project == $project)
     | .tests[]
     | "FullyQualifiedName=" + .]
    | join("|")
  ' "$PLAN"
}

UNIT_FILTER="$(filter_for tests/Aws2Azure.UnitTests)"
INTEGRATION_FILTER="$(filter_for tests/Aws2Azure.IntegrationTests)"

if [ -n "$UNIT_FILTER" ]; then
  dotnet test tests/Aws2Azure.UnitTests/Aws2Azure.UnitTests.csproj \
    -c Release --no-build \
    --filter "$UNIT_FILTER" \
    --logger "trx;LogFileName=matrix-unit.trx" \
    --results-directory "$RESULTS_DIR"
fi

if [ -n "$INTEGRATION_FILTER" ]; then
  dotnet test tests/Aws2Azure.IntegrationTests/Aws2Azure.IntegrationTests.csproj \
    -c Release --no-build \
    --filter "$INTEGRATION_FILTER" \
    --logger "trx;LogFileName=matrix-integration.trx" \
    --results-directory "$RESULTS_DIR"
fi
```

This reuses the same `plan-conformance` + `validate-conformance-discovery`
mechanism the repository's own nightly workflow uses to avoid VSTest's
zero-match success behavior and to run **only** the exact matrix-backed test
identities. The integration filter includes both the deterministic HTTP failure
coverage and the live-Azure tests referenced by the matrix; the unit filter
covers the matrix's deterministic/fail-closed DynamoDB, SQS, Kinesis, and SNS
contracts.

You are **not** running against the repo's nightly subscription or any special
CI-only template; you are running against the ephemeral resources just created
in **your** Azure account.

> The integration fixture synthesizes the proxy bindings from the exported env
> vars, so you do not need to hand-author a second config file just to run the
> live-Azure suite. If you already have a candidate `aws2azure` config file and
> want the report to record its identity, pass that file to the report generator
> in the next step so it can hash it into `config_digest`.

## Export the adopter-facing YAML report

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- \
  generate-adopter-real-azure-report \
  --trx "$RESULTS_DIR" \
  --output "$RESULTS_DIR/self-validation.yaml" \
  --candidate-id "team-a-staging-sidecar" \
  --git-sha "$(git rev-parse HEAD 2>/dev/null || echo unknown)" \
  --config path/to/aws2azure.config.json \
  --artifact path/to/Aws2Azure.Proxy \
  --region "$REGION" \
  --resource-group "$RG" \
  --execution-engine "eng/repro-real-azure.sh + plan-conformance + validate-conformance-discovery + planned unit/integration test filters"
```

Useful optional flags:

- `--run-id <id>`: override the default `local-<utc timestamp>` identifier.
- `--run-url <url>`: point at your own CI/build URL if this adopter run was driven elsewhere.
- `--artifact-digest <sha256:...>` / `--config-digest <sha256:...>`: provide a precomputed digest instead of a file path.
- `--azure-subscription-id <id>`: record which subscription hosted the run.
- `--backend-description <text>`: replace the default ephemeral-resource description.

The generator reuses the repository's real-Azure conformance matrix and TRX
parser. Extra `Category=RealAzure` tests that are intentionally outside the
shared conformance matrix are reported under `unmapped_tests` instead of being
silently discarded.
The YAML is written before the command exits: status `0` means the selected
operations passed, `3` means at least one operation failed, and `4` means the
run was inconclusive (for example only skipped / not-run evidence, or no
positive real-Azure verification evidence).

## Tear everything down

```bash
eng/repro-real-azure.sh down --resource-group "$RG"
```

Do this even when tests fail. The teardown path deletes Blob versions, purges
Key Vault, and waits for Azure to confirm resource-group deletion.

## Caveats

- The report is **evidence**, not an automatic certification or seal mutation.
- Emulator-backed passes are still useful for fast feedback, but this kit exists
  because emulators are not behavior-equivalent to real Azure.
- Secrets Manager and the Workload-Identity-only scenarios still need the extra
  federated-token prerequisites described in
  [Nightly real-Azure integration tests](real-azure-nightly.md#workload-identity-end-to-end-issue-307).
- No container image is required for this flow; documented `az` CLI + `dotnet`
  invocations are the supported scope for issue #945.

## Related documents

- [Local real-Azure/real-AWS reproduction](local-real-azure-repro.md)
- [Nightly real-Azure integration tests](real-azure-nightly.md)
- [`eng/repro-real-azure.sh`](../../eng/repro-real-azure.sh)
- [`deploy/realazure/main.bicep`](../../deploy/realazure/main.bicep)
