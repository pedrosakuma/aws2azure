# ADR 0007 — Single binary with service multiplexing

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

The proxy fronts several AWS-compatible service endpoints. Splitting each service
into a separate executable would complicate deployment, readiness, configuration,
and credential mapping for users that need more than one service. A single host
can route by the same cues AWS SDKs already send: Host header and request path.

## Decision

`aws2azure` ships as a **single binary**. Service modules are registered in that
host and multiplexed by Host header and path before SigV4 validation and module
handling.

## Consequences

- Operators deploy one sidecar or proxy process per workload shape instead of one
process per AWS service.
- Module registration is explicit and centralized in the host.
- Build-time module selection can trim unused modules, but selected modules still
run inside the same process model.
- Routing bugs can affect multiple services, so routing tests are part of the
shared core contract.

## Revisit triggers

Revisit if operational evidence shows independent service binaries are required
for isolation and the added deployment complexity is accepted.
