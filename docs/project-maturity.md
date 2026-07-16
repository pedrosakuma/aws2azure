# Project maturity and support terms

The [Project Roadmap](https://github.com/pedrosakuma/aws2azure/issues/16)
defines the durable project scope and records completed phases. The active
production-maturity plan is
[Phase 9: workload-level GA](https://github.com/pedrosakuma/aws2azure/issues/542).
Maturity is assessed per workload profile, not by total AWS service parity.

## Current baseline

Six service modules are available:

| AWS protocol | Azure backend |
|---|---|
| S3 | Blob Storage |
| SQS | Service Bus |
| DynamoDB | Cosmos DB |
| Kinesis | Event Hubs |
| SNS | Service Bus Topics or Event Grid |
| Secrets Manager | Key Vault |

Configuration is binding-centric. Each binding maps one AWS signing identity to
one or more Azure service backends. Each backend separates its topology
(`kind` and `target`) from credentials (`auth`). Azure authentication includes
backend-appropriate shared keys/SAS and, where supported, Entra client-secret,
Managed Identity, and Workload Identity modes.

The generated [workload compatibility](./site/workload-compatibility.md) and
[operation coverage](./site/coverage.md) pages are authoritative for current
capability and real-Azure verification counts. Operational readiness includes
liveness, readiness, module inventory, Prometheus metrics, a production
runbook, emulator regression suites, real-Azure conformance, performance and
footprint gates, and release/container workflows.

The current public release is `v0.1.0` prerelease. A `v1.0` release requires the
announced workload profiles to pass the gates in the active maturity roadmap;
it does not require every documented AWS operation to be implemented.

## Normative terms

These terms are project-level claims and must not be used interchangeably:

| Term | Meaning |
|---|---|
| **module available** | The repository contains a service module that can recognize and route the service's AWS wire protocol. A particular artifact must also compile the module in, and configuration must enable it. Availability says nothing about total operation coverage or AWS parity. |
| **operation implemented** | The operation gap document has `status: implemented`: the operation's documented translation contract is present. This is a capability claim, not by itself a real-Azure evidence, SLO, release, or workload-GA claim. |
| **real-Azure sealed** | The operation's canonical gap YAML records reviewed positive evidence from real Azure. A seal proves the recorded functional scenario; it is not a permanent Azure-capacity, reliability, or workload-GA claim. |
| **workload conditional** | The workload's required surface can proceed only after explicitly accepting documented `partial` behavior, design gaps, topology requirements, or additional staging validation. Conditional is not GA. |
| **workload GA** | A versioned workload profile has passed the mechanical GA gate: no required `stub`/`unsupported` operation, every required partial behavior accepted, required operations real-Azure sealed, production-shaped SLO and resilience evidence passing, operational procedures proven, and an immutable reproducible artifact qualified. |

An operation can be implemented but not real-Azure sealed. A workload can be
conditional even when every operation it uses is implemented. Conversely, a
workload may reach GA while unrelated operations remain partial, stubbed,
unsupported, or structurally incompatible with Azure.

## Governance

- Phases 0–5 are completed historical delivery milestones.
- Phase 6 production-readiness work is complete.
- Phase 7's original feature-gap checklist is reconciled in
  [#165](https://github.com/pedrosakuma/aws2azure/issues/165); permanent
  incompatibilities remain documented rather than hidden.
- Expansion to additional service modules under
  [#166](https://github.com/pedrosakuma/aws2azure/issues/166) waits until the
  first GA workload profiles are complete.
- [#542](https://github.com/pedrosakuma/aws2azure/issues/542) owns active
  conformance closure, SLO/reliability qualification, workload certification,
  and release readiness.

The gap YAMLs remain the single source of truth for operation and design-gap
status. Emulator success is necessary regression evidence but cannot create a
real-Azure seal or a workload-GA claim.
