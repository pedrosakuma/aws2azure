# ADR 0014 — System.Text.Json source generation

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (serialization baseline)

## Context

The proxy parses configuration and service payloads in a Native-AOT process.
Reflection-based JSON serialization can create trim warnings, preserve too much
metadata, and hide payload shape changes until runtime.

## Decision

JSON serialization uses `System.Text.Json` with `JsonSerializerContext` source
generation for production payloads and configuration models.

## Consequences

- Serializable types are declared in source-generated contexts rather than
resolved dynamically.
- AOT and trim warnings surface during build.
- Payload shape changes are reviewed alongside context updates and tests.
- Reflection-heavy serializers and dynamic JSON model binding are not used in the
production path.

## Revisit triggers

Revisit only if a required JSON feature cannot be implemented with
`System.Text.Json` source generation and an alternative can meet the same AOT and
footprint constraints.
