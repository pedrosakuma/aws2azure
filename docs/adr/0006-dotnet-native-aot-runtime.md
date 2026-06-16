# ADR 0006 — .NET with Native AOT

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

The proxy needs a production runtime with strong HTTP support, good diagnostics,
and a path to small single-file deployment. It also needs predictable startup and
trim-friendly code so sidecar replicas can come and go cheaply.

## Decision

Production assemblies target **.NET Native AOT** with AOT and trim warnings
treated as errors. Code that depends on reflection, dynamic loading, runtime code
generation, or unknown-type activation is not allowed in the production path.

## Consequences

- All production code must remain compatible with `<PublishAot>true</PublishAot>`
and `<IsAotCompatible>true</IsAotCompatible>`.
- APIs that are convenient but reflection-heavy are rejected unless a
source-generated or closed-world alternative exists.
- The build is expected to fail loudly on IL2026, IL2046, IL3050, and related
warnings rather than shipping latent publish failures.
- Tests and tools may have their own project settings, but they must not pull
non-AOT-safe dependencies into production assemblies.

## Revisit triggers

Revisit only if Native AOT cannot support a required production capability and a
tracked issue accepts the footprint and startup regression of a different
runtime mode.
