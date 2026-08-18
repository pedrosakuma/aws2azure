# ADR 0018 — Service Bus transport and emulator evolution

- **Status:** Accepted
- **Date:** 2026-08-18
- **Phase:** Architecture maintenance
- **Supersedes:** ADR-0001's emulator-fixture decision
- **Clarifies:** ADR-0008's direct-REST scope

## Context

ADR-0001 selected the Service Bus runtime REST API for the initial SQS
implementation and concluded that the Service Bus emulator could not exercise
that REST hot path. ADR-0002 subsequently introduced a greenfield AMQP 1.0
transport for semantics unavailable through REST. Sender links and additional
receive and settlement paths were added as that transport matured.

The Service Bus emulator still does not implement the runtime REST API, but it
does expose AMQP. The repository now ships a Testcontainers fixture that
exercises the AMQP send, receive, settlement, FIFO-session, and DLQ paths.

ADR-0008 remains correct about avoiding Azure SDK dependencies, but direct REST
is not a universal transport requirement: AMQP is the accepted Service Bus
exception.

## Decision

- Retain REST for Service Bus operations whose required semantics and resource
  budget are satisfied by the stable REST API.
- Use the hand-rolled, AOT-safe AMQP 1.0 client for Service Bus behavior that
  requires broker links, sessions, connection-affine settlement, or native
  AMQP flow.
- Ship and maintain the Service Bus emulator fixture for AMQP integration
  coverage. Do not claim that it validates the runtime REST path.
- Keep real-Azure Service Bus tests as the authority for transport behavior the
  emulator cannot reproduce faithfully.
- Continue to forbid Azure SDK dependencies unless a future ADR explicitly
  accepts their dependency and Native AOT cost.

## Consequences

- The SQS module intentionally owns two Service Bus transports, and workload
  guidance must state which guarantees apply to each.
- Emulator coverage now exercises a production transport hot path, but it
  remains necessary rather than sufficient evidence.
- Baseline architecture descriptions must say "direct Azure protocols without
  Azure SDK dependencies," not imply that every backend call uses REST.
- ADR-0001 remains the historical record for the Phase-2 REST baseline; only its
  no-emulator-fixture conclusion is superseded.
