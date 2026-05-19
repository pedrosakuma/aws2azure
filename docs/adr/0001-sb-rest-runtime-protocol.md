# ADR 0001 — Service Bus runtime protocol: REST, not AMQP

- **Status:** Accepted
- **Date:** 2026-05-19
- **Phase:** 2 (SQS module bootstrap)

## Context

`aws2azure` translates AWS wire protocols into equivalent calls against
Azure REST APIs. The SQS module (Phase 2) targets Azure Service Bus as the
backing service.

Azure Service Bus exposes two runtime protocols:

1. **AMQP 1.0** on port 5671 — the protocol every official Azure SDK
   uses. Connections are long-lived; entities are addressed by sender /
   receiver links opened on a session multiplexed over one AMQP
   connection.
2. **REST** on port 443 (`*.servicebus.windows.net/{queue}/...`) — a
   stateless HTTP API covering send / peek-lock / complete / abandon /
   renew-lock / management. Each operation is a single HTTPS request.

Slices 0–4 of the SQS module are built on the REST runtime. This ADR
records why, what we give up, and how we cover the gaps.

## Decision

**The SQS module talks to Service Bus over the REST runtime API. We do
not introduce an AMQP 1.0 stack.**

This applies to every operation: queue management (Atom feed), send,
peek-lock receive, complete, abandon, renew-lock, dead-letter, purge
(emulated), batch (emulated via parallel REST fan-out).

## Drivers

### Connection model fits a multi-tenant HTTP proxy

The proxy is fronted by a Kestrel HTTP listener and serves arbitrarily
many AWS clients simultaneously. Each request is short-lived and
independent.

- REST: one outbound HTTPS connection from the pooled `HttpClient` per
  in-flight request, returned to the pool on completion. Connection
  count scales with concurrency, not with the cardinality of SQS clients
  or message groups.
- AMQP: one persistent TCP+TLS connection per Service Bus namespace, plus
  one sender/receiver **link per queue and per session**. SQS FIFO
  semantics with N distinct `MessageGroupId` values would require N
  receiver links per consumer to preserve per-group ordering on Service
  Bus sessions. For a proxy fronting many independent AWS clients this
  multiplies into thousands of long-lived links per namespace, with
  link-lifecycle, link-credit, and heartbeat management we would have to
  implement and tune.

### Native AOT, no Azure SDK, no reflection

The repo's locked constraints (see `.github/copilot-instructions.md`)
forbid the Azure SDK and any reflection-heavy runtime. The mature AMQP
client (`AMQPNetLite`, MIT-licensed and maintained by the Azure team) is
not AOT-clean: its codec calls `Activator.CreateInstance(Type)` and
`FormatterServices.GetUninitializedObject(Type)` per frame
(`src/Net/TypeExtensions.cs`). Adopting it would require either:

1. mass IL2026 / IL3050 suppressions in our module assembly (violates
   "zero AOT warnings"); or
2. forking the library to add `[DynamicDependency]` to every
   `DescribedList` subtype (recurring maintenance burden); or
3. hand-rolling AMQP 1.0 over a binary TCP transport from scratch
   (multi-month scope; vastly outsizes the SQS feature surface we need).

REST uses `System.Net.Http`, which is a first-class AOT-supported BCL
component.

### Operability

REST requests carry their own correlation IDs, are visible to standard
HTTP tracing / WAF / network logs, and reuse the same metric and circuit
breaker plumbing as the S3 module (Azure Blob, also REST). AMQP would
require a parallel observability stack.

## Consequences

### Accepted limitations

- **No native peek-from-DLQ over REST runtime.** The REST API addresses
  dead-letter sub-entities by URL suffix `/$deadletterqueue`, which
  works for receive/delete but the surface is narrower than the AMQP
  management plane. Phase 2 Slice 5 will rely on this; gaps will be
  documented per operation.
- **No native batch.** SQS `*Batch` ops are emulated by parallel REST
  fan-out (bounded `SemaphoreSlim`). This costs one HTTPS round-trip
  per entry. For the proxy's expected workload this is acceptable;
  if a heavy batch user appears we can revisit (likely with a small
  hand-rolled AMQP transport behind a feature flag rather than ripping
  out REST).
- **No native FIFO sessions over REST runtime.** SB sessions exist only
  in the AMQP runtime; REST cannot acquire a session lock. Slice 5
  will document how `MessageGroupId` is mapped onto SB session IDs at
  send time (so AMQP consumers downstream still see correct ordering),
  but the proxy itself cannot offer ordered receive over REST. SQS FIFO
  consumers via the proxy will receive in best-effort SB order; strict
  per-group ordering on the receive path is a documented gap.
- **No transactional sends.** REST has no transactional batch. SQS
  `SendMessageBatch` is not transactional either, so this is a wash;
  callers wanting cross-queue atomicity were never supported by SQS.

### The Service Bus emulator does not cover our hot path

`mcr.microsoft.com/azure-messaging/servicebus-emulator` exposes
**AMQP on 5672 and a management HTTP shim on 5300**. It does **not**
implement the SB **runtime** REST API (`POST /{queue}/messages`,
`POST /messages/head`, `DELETE /messages/{id}/{token}`). Spinning the
emulator into our Testcontainers fixture would therefore not exercise a
single line of the SQS module's send/receive code path — connections to
our REST endpoints would be refused.

We therefore **do not ship a Service Bus emulator fixture.** The
emulator-fixture work item (`p2-sb-emulator-fixture`) is closed as
**won't-do** with a pointer to this ADR.

### How we get integration coverage instead

Two complementary mechanisms cover the integration-test gap:

1. **In-process REST fakes.** Unit tests (`tests/Aws2Azure.UnitTests`)
   already drive the full handler chain against `HttpMessageHandler`
   fakes that mimic SB REST semantics (status codes, headers,
   `BrokerProperties` JSON). 401+ tests today; every new slice adds
   handler-level coverage here. This is the fast feedback loop.
2. **Real-Azure nightly job.** A new workflow
   (`.github/workflows/integration-real-azure.yml`) runs the integration
   suite tagged `[RealAzure]` against a Service Bus namespace whose
   connection string is supplied via the `AZURE_SB_CONNSTR` repository
   secret. Triggered on `schedule` (nightly, 05:00 UTC, after the
   emulator-based `integration` job), on `workflow_dispatch`, and on PRs
   carrying the `run-real-azure` label. Tests are skipped (not failed)
   when the secret is unavailable, so forks and unprivileged PRs do not
   break.

The combination of in-process fakes (every PR) + real-Azure nightly
(default branch) gives us the same coverage shape the emulator would
have offered, against the only backend that actually implements our
target API.

### Status of related work items

- `p2-sb-emulator-fixture` → **closed as won't-do**. See this ADR.
- `p2-sb-real-azure-nightly` → **created** in this PR; tracks
  bring-up of the workflow and the first `[RealAzure]`-tagged
  smoke test.

## Revisit triggers

This decision should be revisited if any of the following becomes true:

- A heavy batch / high-throughput user appears for whom REST fan-out is
  measurably worse than AMQP credit-based flow (NFR phase will
  benchmark; if delta exceeds ~3× we revisit).
- Microsoft ships the SB runtime REST API in the emulator (the
  emulator's roadmap is public; we'll pick it up automatically because
  our REST client only needs a working endpoint).
- Strict FIFO ordering on the receive path becomes a hard requirement
  for a real workload (currently a documented gap, not a blocker).

A revisit means a new ADR superseding this one, not an in-place edit.
