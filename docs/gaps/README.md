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
```

Status meanings for design gaps:

| Status        | Meaning |
|---------------|---------|
| `by_design`   | A deliberate, permanent divergence (usually a locked decision / ADR). |
| `partial`     | Partially bridged; caveats apply. |
| `unsupported` | No Azure equivalent; surfaced but not translated. |
| `planned`     | A known gap with intended future work. |
