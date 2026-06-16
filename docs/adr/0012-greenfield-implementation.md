# ADR 0012 — Greenfield implementation with no reused proxy code

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

Existing projects such as s3proxy and MinIO are useful references for protocol
behaviour, but importing or forking them would bring their architectures,
dependencies, licensing boundaries, and compatibility assumptions into this
project. Those assumptions do not match a Native-AOT sidecar that translates AWS
requests to Azure REST.

## Decision

`aws2azure` is built from scratch. Existing proxies, servers, and SDKs may be
used as conceptual references, but their code is not imported, vendored, or
forked into the repository.

## Consequences

- The codebase remains tailored to the locked AOT, no-SDK, and sidecar-footprint
constraints.
- Behaviour copied from references must be reimplemented, tested, and cited where
appropriate rather than pasted.
- The project owns maintenance of its translation logic and cannot rely on
upstream proxy fixes landing automatically.
- License and supply-chain review stay simpler because production code is
first-party plus deliberate package dependencies.

## Revisit triggers

Revisit only if a dependency is proposed through normal review with explicit
license, AOT, footprint, and maintenance analysis.
