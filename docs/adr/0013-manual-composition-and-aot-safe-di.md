# ADR 0013 — Manual composition and AOT-safe dependency injection

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (host baseline)

## Context

Reflection-based dependency injection features rely on assembly scanning,
unknown-type activation, and convention-based registration. Those patterns are
fragile under trimming and make it harder to see which service modules and
helpers are included in a given sidecar build.

## Decision

Production composition is explicit. The host uses manual wiring or a
source-generation-friendly DI subset; it does not use reflection-based scanning,
unknown-type activation, or convention registration.

## Consequences

- `Program.cs` and module registries show exactly which services are available.
- Native AOT publish does not depend on preserving constructors discovered only
through reflection.
- Adding a module requires explicit registration and tests for routing and
configuration.
- Some boilerplate is accepted in exchange for predictability and trim safety.

## Revisit triggers

Revisit if a source-generated DI approach proves it can preserve explicit
registration, AOT safety, and clear failure modes with less boilerplate.
