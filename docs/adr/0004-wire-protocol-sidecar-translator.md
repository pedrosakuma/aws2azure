# ADR 0004 — Wire-protocol sidecar translator

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

AWS SDKs in every supported language already know how to sign and send the AWS
wire protocol. Building an in-process compatibility library would require one
library per language and would couple migration behaviour to application runtime
choices. Deployment topologies also vary: some users will run the proxy beside a
workload, some behind a service mesh, and some in shared infrastructure.

## Decision

`aws2azure` is a transparent **HTTP wire-protocol translator** intended to run as
a sidecar or separately deployed proxy. Applications point their AWS SDK
`endpoint_url` at the proxy; no application language binding is provided.
Topology-dependent behaviour must be configurable and documented, never assumed.

## Consequences

- One proxy binary serves clients written in any AWS-SDK-supported language.
- Service boundaries remain HTTP: route, validate SigV4, translate, call Azure,
and return an AWS-shaped response.
- Features whose correctness depends on topology, region, or operator ownership
must be opt-in with documented tradeoffs.
- The request path now includes another process, so observability, readiness, and
resource footprint are architectural concerns.

## Revisit triggers

Revisit if a concrete workload proves that over-the-wire translation cannot meet
its correctness requirements and an issue accepts the cost of per-language
bindings.
