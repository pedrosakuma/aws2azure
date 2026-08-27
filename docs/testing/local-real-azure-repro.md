# Local real-Azure/real-AWS reproduction

This doc is the local-reproduction companion to
[Nightly real-Azure integration tests](real-azure-nightly.md) and
[Real-AWS capture for Tier-3 differential](real-aws-capture.md). It implements
roadmap issue **#839**, a follow-up identified while investigating a real-Azure
flake in **#838**: provisioning the ephemeral Cosmos DB + Service Bus backends
by hand and wiring together ad-hoc environment files was slow and error-prone.
[`eng/repro-real-azure.sh`](../../eng/repro-real-azure.sh) scripts that flow so
a contributor (or agent) can reproduce the `integration-real-azure` nightly job
locally in a few commands.

> **External adopters:** if you are validating `aws2azure` against **your own**
> Azure subscription before rollout, use
> [Adopter real-Azure conformance kit](adopter-conformance-kit.md). This page
> stays focused on contributor/agent reproduction of the repository's nightly path.

> **Non-goal.** This is purely about local/agent reproducibility. It does not
> change CI cadence or labels, and it does not add nightly-flake tracking
> (tracked separately per #838).

## Real-Azure: provision → test → teardown

### Prerequisites

- Azure CLI (`az`) installed and logged in (`az login`) against a subscription
  where your account (or a service principal you're using) has **Contributor**
  — the script creates a resource group and deploys
  [`deploy/realazure/main.bicep`](../../deploy/realazure/main.bicep) into it,
  the same template CI's `integration-real-azure` workflow uses.
- `jq` installed.
- .NET SDK matching the repo (`dotnet build -c Release` must already succeed).

> **Cost/safety warning.** This provisions **real, billed** Azure resources: a
> Standard LRS Storage account (+ a Storage Queue), a Standard Service Bus
> namespace, a serverless Cosmos DB account + database, a capacity-1 Standard
> Event Hubs namespace + hub, an Event Grid custom topic + event subscription,
> and a Key Vault. Cosmos DB account creation alone typically takes 5–10
> minutes. Nothing here auto-expires — always run the teardown step below, or
> at minimum let the shared `real-azure-reaper` catch it (it deletes anything
> tagged `purpose=aws2azure-nightly` older than its age threshold, but this
> script tags resources `purpose=aws2azure-local-repro` instead, precisely so
> it is *not* silently swept up by that automation and so you can tell your own
> ephemeral resource groups apart from CI's). The script itself prints this
> same warning and asks for confirmation unless you pass `--yes`.

### Provision

```bash
eng/repro-real-azure.sh up
# or, non-interactively:
eng/repro-real-azure.sh up --yes --resource-group my-repro-rg
```

This creates the resource group (default name
`aws2azure-repro-<unix-epoch>`), deploys the Bicep template, reads back the
deployment outputs and account keys, and writes a sourceable env file (default
`.local/real-azure.env`, `chmod 0600`, git-ignored). The script prints the
exact resource group name and env file path it used — copy them for the
teardown step.

### Run tests

```bash
source .local/real-azure.env

# Full real-Azure suite (shared-key/SAS backends only — see the Workload
# Identity / Secrets Manager caveat below):
dotnet build -c Release
dotnet test tests/Aws2Azure.IntegrationTests -c Release --no-build \
  --filter "Category=RealAzure"

# One test class, e.g. only the SQS round-trip smoke:
dotnet test tests/Aws2Azure.IntegrationTests -c Release --no-build \
  --filter "FullyQualifiedName~Sqs.SqsRealAzureRoundTripTests"

# One test method:
dotnet test tests/Aws2Azure.IntegrationTests -c Release --no-build \
  --filter "FullyQualifiedName~DynamoDbRealAzureSmokeTests.PutItem_and_GetItem_round_trip"
```

Every real backend gates **independently** on its own environment variables
(`RealAzureProxyFixture.cs`): a backend whose values are absent skips rather
than fails, so you can provision (and pay for) only what you need by leaving
the rest unset, or by re-running with only a subset of the Bicep's outputs
exported. There is no per-service flag on the provisioning script itself
because the Bicep template always provisions the full shared six-service set
in one deployment (cheap relative to Cosmos DB's provisioning time, and it
keeps local and CI provisioning identical) — but you can still choose to run
only a subset of tests against it.

### Teardown

```bash
eng/repro-real-azure.sh down --resource-group my-repro-rg
```

This reuses the exact same
[`cleanup-real-azure-resource-groups.sh`](../../.github/scripts/cleanup-real-azure-resource-groups.sh)
script the CI workflow's `if: always()` teardown step calls: it deletes every
Blob version first (required because immutable storage with versioning
protects non-empty accounts from deletion), deletes and purges the Key Vault
(soft-delete is mandatory), then requests resource-group deletion and waits
for Azure to confirm it.

### Workload Identity / Secrets Manager scenarios (not automated)

The Workload-Identity DynamoDB/Kinesis scenarios and the entire Secrets
Manager (Key Vault) suite additionally require
`AZURE_FEDERATED_TOKEN_FILE` / `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` pointing
at a valid federated-credential token (see
[Workload Identity end-to-end](real-azure-nightly.md#workload-identity-end-to-end-issue-307)).
In CI this is a **second** GitHub Actions OIDC token exchanged against the
same federated credentials `azure/login` uses; there is no equivalent
zero-setup local source for that token. To exercise these scenarios locally
you need your own app registration + federated credential (or a client-secret
based token acquisition adapted for local use) and to pass
`--principal-id <object-id>` to `eng/repro-real-azure.sh up` so the Bicep
grants that principal the Workload Identity data-plane roles (Event Hubs /
Service Bus Data Owner, Cosmos DB Built-in Data Contributor, Key Vault
Secrets Officer). Leave `--principal-id` unset (the default) to provision only
the shared-key/SAS smoke matrix, exactly as CI does when
`AZURE_CLIENT_OBJECT_ID` is unset. This is intentionally left as a manual,
documented gap rather than guessed-at automation.

### Known sharp edges this script papers over

These were hit first-hand while investigating #838 and are exactly why the
script exists instead of a one-off snippet:

- **Windows `az` CLI under WSL emits CRLF.** If your WSL environment resolves
  `az` to the Windows executable (e.g.
  `/mnt/c/Program Files/Microsoft SDKs/Azure/CLI2/wbin/az`), every captured
  value — even with `-o tsv` — carries a trailing `\r`. A `\r` embedded in a
  connection string or endpoint URL silently breaks JSON parsing and
  request signing downstream. The script strips `\r` from every captured `az`
  value (`strip_crlf` / the `az_tsv` helper) before it is written anywhere,
  the same defense `cleanup-real-azure-resource-groups.sh` already uses for
  the CI teardown path.
- **Unquoted values with `;` are truncated by `source`.** Service Bus and
  Event Hubs connection strings look like
  `Endpoint=sb://...;SharedAccessKeyName=...;SharedAccessKey=...`. Writing
  `VAR=$value` unquoted into a file meant to be `source`d/`.`-ed by bash is
  silently truncated at the first unescaped `;`, because bash treats it as a
  command separator — you'd end up with only `AZURE_SB_CONNSTR=Endpoint=sb://...`
  and no key at all, with no error. The script instead writes every value with
  `printf 'export VAR=%q\n' "$value"`, which produces a shell-safe quoted/escaped
  literal that round-trips exactly through `source`.

## Real-AWS: golden-evidence capture (already covered, no new script needed)

The real-AWS side of Tier-3 differential testing is **not** "run the proxy
against real AWS" — per the project's AWS→Azure-only design, and as documented
in [Real-AWS capture for Tier-3 differential](real-aws-capture.md), it is a
**capture-only** flow: the test fixture drives the AWS SDK directly against
real AWS (no proxy involved) to record canonical golden evidence that the
credential-free `OfflineConformanceDiffRunner` later diffs against
proxy-over-real-Azure evidence. That flow already has a documented,
`workflow_dispatch`-triggerable CI path
([`capture-real-aws.yml`](../../.github/workflows/capture-real-aws.yml)), and
reproducing it locally needs no Bicep/ARM-equivalent provisioning script: the
tests themselves create (and, on their own success path, delete) the ephemeral
`aws2azure-it-*`-prefixed S3 bucket / DynamoDB table / Kinesis stream / SNS
topic / SQS queue they exercise, exactly as `capture-real-aws.yml`'s own
`dotnet test --filter "Category=RealAws"` step does — see that workflow for the
authoritative invocation.

To reproduce locally:

```bash
# Any AWS credential source works (long-lived access key/secret, SSO profile,
# or an assumed role) as long as it has the IAM permissions documented in
# "One-time operator setup already completed" in real-aws-capture.md (the
# aws2azure-it-* prefix-scoped policy). For example:
export AWS_ACCESS_KEY_ID=...
export AWS_SECRET_ACCESS_KEY=...
export AWS_DEFAULT_REGION=us-east-1   # or your dedicated account's region

dotnet build -c Release
dotnet test tests/Aws2Azure.IntegrationTests/Aws2Azure.IntegrationTests.csproj \
  -c Release --no-build \
  --filter "Category=RealAws"
```

Without AWS credentials set, these tests skip (the same behavior
`capture-real-aws.yml` relies on when `AWS_ROLE_ARN` is unset). If you leak an
ephemeral resource (e.g. a cancelled local run), rely on
[`real-aws-reaper.yml`](../../.github/workflows/real-aws-reaper.yml)'s
`aws2azure-it-*`-prefix sweep, or delete it by hand with the resource-specific
`aws` CLI delete command — there is no separate local teardown script for the
AWS side because there is no separate resource group / deployment to tear
down; each test's own naming and (where the test itself doesn't already clean
up) the reaper are the only cleanup surfaces.

## Related documents

- [Nightly real-Azure integration tests](real-azure-nightly.md)
- [Adopter real-Azure conformance kit](adopter-conformance-kit.md)
- [Real-AWS capture for Tier-3 differential](real-aws-capture.md)
- [`eng/repro-real-azure.sh`](../../eng/repro-real-azure.sh)
- [`deploy/realazure/main.bicep`](../../deploy/realazure/main.bicep)
- [`RealAzureProxyFixture.cs`](../../tests/Aws2Azure.IntegrationTests/Fixtures/RealAzureProxyFixture.cs)
- [`.github/scripts/cleanup-real-azure-resource-groups.sh`](../../.github/scripts/cleanup-real-azure-resource-groups.sh)
- Issues [#838](https://github.com/pedrosakuma/aws2azure/issues/838) and
  [#839](https://github.com/pedrosakuma/aws2azure/issues/839)
