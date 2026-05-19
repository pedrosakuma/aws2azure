# ADR 0002 — Hand-rolled AMQP 1.0 client library

- **Status:** Accepted
- **Date:** 2026-05-19
- **Phase:** 2.5 (AMQP transport)

## Context

ADR-0001 locked the SQS runtime onto Azure Service Bus **REST** for Phase 2
on the basis that REST is the smallest API surface that covers the SQS
operations we need. That call shipped Phase 2 (Slices 0–5) on schedule,
but it leaves three SQS semantics that REST cannot honour:

1. **Strict per-`MessageGroupId` FIFO receive ordering.** SQS FIFO queues
   guarantee that messages with the same `MessageGroupId` are delivered
   to *one* consumer in arrival order until that consumer settles them.
   SB REST has no equivalent: peek-lock returns arbitrary messages and
   does not bind a group to a connection. Only AMQP **sessions** (linked
   receivers scoped to a session-id) reproduce this guarantee.
2. **Native long-poll.** REST receive uses a server-side timeout that
   caps at 60s and re-establishes a new HTTP request on every cycle.
   AMQP receivers stay attached: the credit-flow primitive lets the
   client park a single link and drain messages as they arrive.
3. **Connection multiplexing.** SQS clients open one logical endpoint
   and pay one TCP/TLS handshake. REST forces N connection pools (one
   per backend host), one TLS handshake per pool. AMQP multiplexes
   many sessions/links over a single connection — material when one
   process is fan-out reading hundreds of FIFO message-groups.

These motivate a per-namespace AMQP runtime alongside the REST runtime.

## Decision

Implement a **greenfield AMQP 1.0 client library** as
`src/Aws2Azure.Amqp` (a project inside the solution for now, slated to
be extracted to its own repo + NuGet later). The library is consumed by
the SQS module's receive path as an alternative transport behind a
feature flag.

### Why not reuse an existing AMQP library

| Candidate | Why rejected |
|---|---|
| **Azure.Messaging.ServiceBus** | Whole-cloud SDK; forbidden by the project's "no Azure SDK" rule (Locked decisions). Brings Azure.Core + dependency closure. |
| **AMQPNetLite** | The reference open-source .NET AMQP 1.0 stack. Heavily reflection-based (`Activator.CreateInstance`, `Type.GetMethod`, runtime-loaded described-type maps). Trim warnings cannot be suppressed without breaking codec generality; AOT publish fails. |
| **Apache.NMS.AMQP** | Wraps AMQPNetLite. Same problem. |
| **rabbitmq-client** | AMQP 0-9-1 only — wrong protocol. |

None of the available .NET AMQP 1.0 implementations are AOT-clean, and
none of them are trimmable to the subset of the protocol we actually
need (a handful of performatives, one terminus shape, one settle mode).
Reuse is not viable under the project's locked AOT rules.

### Library scope (minimum viable)

The lib is **not** a general-purpose AMQP 1.0 client. It implements
only what the SQS-over-Service-Bus path requires:

- AMQP 1.0 type system: fixed-width and variable-width primitives,
  lists/maps/arrays, described types.
- Frame codec (header + AMQP/SASL frames).
- Performatives: `open`, `begin`, `attach`, `flow`, `transfer`,
  `disposition`, `detach`, `end`, `close`.
- SASL `ANONYMOUS` + SB CBS (Claim-Based-Security) auth over a
  management link using SAS tokens (computed locally, no Azure SDK).
- Session + receiver link with credit-based flow control and
  session-scoped receivers (the FIFO primitive).
- No sender link in the first cut (REST `SendMessage` already works);
  added later only if profiling justifies it.

### AOT constraints (binding)

- No `System.Reflection.Emit`, `Activator.CreateInstance(Type)`,
  `Type.GetType(string)`, `Assembly.Load*`.
- Described-type registry is a **closed switch** keyed by the
  descriptor symbol/ulong — not a reflection-discovered map.
- Buffers use `Span<T>` / `ReadOnlySpan<T>` and pooled `byte[]` via
  `ArrayPool<byte>.Shared`.
- Tests live in the existing `Aws2Azure.UnitTests` project (xUnit,
  hand-rolled fakes — no Moq/Castle).

## Consequences

**Positive**

- FIFO ordering becomes implementable: each SB session is bound to one
  AMQP receiver link, so per-`MessageGroupId` order is server-enforced.
- One TCP/TLS handshake per namespace instead of one per backend host.
- Lib is small (~few thousand LOC) and trimmable; AOT publish stays
  clean.

**Negative**

- We own a wire-protocol implementation. Interop drift is on us.
  Mitigated by conformance tests against known-good capture vectors
  and integration tests against real Service Bus (nightly).
- Two transports for receive (REST and AMQP) means more code paths.
  The feature flag is per-queue at the proxy config level so the two
  do not contend for the same SB resources.

## Out of scope (explicitly)

- Direct AWS SDK linkage. The AMQP lib speaks AMQP 1.0 only.
- AMQP-over-WebSockets. SB exposes raw AMQP on port 5671; we use it.
- Generic broker compatibility (RabbitMQ, ActiveMQ Artemis). Service
  Bus quirks (e.g. session-receiver re-attach behaviour) are
  acceptable special cases.

## Follow-ups

- Phase 2.5 slices 0–N implement the lib bottom-up (primitives → frames
  → state machine → SB integration).
- After integration, gap docs for `ReceiveMessage` (FIFO),
  `SendMessage` (perf), and others gain a `transport: amqp` field
  documenting which guarantees apply per transport.
- Library extraction to its own repo + NuGet is **not** part of Phase
  2.5; deferred until the API stabilises.
