# Gap docs — authoring guide

These YAML files are the **single source of truth** for what the proxy translates
and how it diverges from AWS. The [`Aws2Azure.GapDocs`](../../tools/Aws2Azure.GapDocs)
tool validates them, renders the browsable site under [`../site/`](../site/), and
generates the runtime `CapabilityRegistry`. CI fails if the committed site or
generated code drifts from the YAML.

Regenerate after any edit:

```bash
dotnet run --project tools/Aws2Azure.GapDocs
```

There are two document kinds, so the per-operation matrix and the cross-cutting
story each stay readable on their own (drill-down, not one overwhelming page).

## 1. Operation docs — `docs/gaps/<service>/<Operation>.yaml`

One file per AWS operation the proxy handles (implemented, partial, stub, or
unsupported). Unknown keys **fail validation** (a typo must not silently drop
documented content).

The operation statuses below are capability metadata. Their relationship to
module availability, real-Azure seals, conditional workloads, and workload GA
is defined in [Project maturity and support terms](../project-maturity.md).

```yaml
service: dynamodb            # required — must match the directory name
operation: Query             # required — AWS operation name
azure_equivalent: "Azure Cosmos DB (Core SQL API)"   # required
status: partial              # implemented | partial | stub | unsupported
verified_real_azure: ""      # optional — date / run URL; empty == emulator-only
sub_features:
  - name: KeyConditionExpression on HASH+RANGE tables
    status: implemented      # implemented | partial | unsupported
    notes: "…"
    gap: "…"                 # what is missing
    workaround: "…"
    verified_real_azure: ""
behavior_differences:
  - "Cosmos 429 is surfaced as ProvisionedThroughputExceededException."
references:
  - https://docs.aws.amazon.com/…
```

## 2. Design-gap docs — `docs/gaps/<service>/_design.yaml`

**Cross-cutting** architectural limitations that do *not* map to a single
operation — the consistency model, transaction scope, and control-plane surfaces
that differ between the AWS service and its Azure target. Optional, at most one
per service; rendered into [`../site/design-gaps.md`](../site/design-gaps.md).
Files whose name starts with `_` are excluded from the operation loader.

```yaml
service: sqs                 # required — must match the directory name and have operation docs
design_gaps:                 # required — at least one entry
  - area: FIFO ordering requires the AMQP transport   # required
    status: partial          # by_design | partial | unsupported | planned
    summary: "…"             # required — the gap in one paragraph
    impact: "…"              # optional — what breaks for the caller
    workaround: "…"          # optional — how to live with it
    references:
      - https://learn.microsoft.com/…
workload_patterns:             # optional — machine-checkable adoption profiles
  - id: sqs_fifo               # required, globally unique [a-z][a-z0-9_]*
    name: FIFO queue messaging
    compatibility: conditional # supported | conditional | blocked
    summary: "…"
    operations:
      - SendMessage
      - ReceiveMessage
    design_gaps:
      - FIFO ordering requires the AMQP transport
    guidance: "Set transport: Amqp and validate the settle lifecycle."
```

Status meanings for design gaps:

| Status        | Meaning |
|---------------|---------|
| `by_design`   | A deliberate, permanent divergence (usually a locked decision / ADR). |
| `partial`     | Partially bridged; caveats apply. |
| `unsupported` | No Azure equivalent; surfaced but not translated. |
| `planned`     | A known gap with intended future work. |

Workload pattern IDs are the stable machine contract consumed by workload
manifests. Rename one only as a deliberate schema-breaking change. A profile
must reference at least one operation or design gap, and a `supported` profile
may reference only implemented operations and no design gaps.

## Workload compatibility checker

Create a YAML manifest using schema version 1:

```yaml
schema_version: 1
workload: checkout
operations:
  - dynamodb:TransactWriteItems
  - sqs:SendMessage
  - s3:PutObject
requirements:
  sqs_fifo: true
  cross_partition_transactions: true
```

Operations use `service:Operation` and must exist in the operation gap docs.
Requirement keys must be IDs declared by `workload_patterns`; unknown keys fail
validation even when their value is `false`. False requirements are validated
but omitted from the report.

Generate the Markdown discovery report on stdout:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- check-workload workload.yaml
```

Generate deterministic JSON for CI and fail only when a blocker is present:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- check-workload workload.yaml \
  --format json --output compatibility.json --fail-on-blocked
```

The checker returns `0` after producing a compatible or conditional report,
`1` for an invalid command/manifest, and `2` for a blocked report when
`--fail-on-blocked` is enabled. Without that option, blocked reports are still
rendered and return `0` so discovery remains inspectable.
