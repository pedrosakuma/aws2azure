# ADR 0008 — Direct Azure REST integration

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

Official Azure SDKs are designed for application developers and bring broad
dependency graphs, shared abstractions, retries, diagnostics, and protocol stacks.
The proxy needs a narrow, AOT-safe translation layer that controls every emitted
HTTP request and AWS-shaped response.

## Decision

Service modules call Azure using **direct REST requests**. Production code does
not depend on Azure SDK packages.

## Consequences

- Azure authentication, request signing, headers, query parameters, and error
mapping are implemented in the relevant modules or shared core helpers.
- Dependency closure stays small and compatible with Native AOT.
- The project owns any Azure REST API quirks it relies on and must document them
in gap docs and tests.
- SDK conveniences such as generated clients, automatic retries, and model
serializers are intentionally unavailable in production code.

## Revisit triggers

Revisit only for a specific Azure capability that cannot be reached through a
stable REST API and whose SDK dependency cost is accepted by a new ADR.
