# ADR 0016 — LoggerMessage source-generated logging

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (observability baseline)

## Context

The proxy is in the request path, so logging must provide useful diagnostics
without adding avoidable allocations or boxing on hot paths. Conventional
structured logging extension methods can allocate arrays and box value types even
when a log level is disabled.

## Decision

Production logging uses the `LoggerMessage` source generator for recurring log
messages and hot paths. Log events should have stable event IDs, templates, and
strongly typed parameters.

## Consequences

- Disabled log paths stay cheap and allocation-conscious.
- Event IDs and message templates are discoverable in source.
- New hot-path logs require a generated partial method rather than ad-hoc string
interpolation.
- One-off diagnostics may still use normal logging when they are clearly outside
hot paths and do not threaten the sidecar budget.

## Revisit triggers

Revisit if the BCL logging stack offers an equally AOT-safe and allocation-free
pattern with less ceremony.
