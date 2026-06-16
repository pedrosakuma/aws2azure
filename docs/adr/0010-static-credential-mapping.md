# ADR 0010 — Static credential mapping

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (configuration baseline)

## Context

The proxy terminates AWS SigV4 at the edge and then calls Azure with credentials
that are valid for the target service. The mapping must be deterministic at
request time and safe to validate before the process starts serving traffic.

## Decision

Credential resolution is based on static configuration: each AWS access key ID
maps to its AWS signing secret and to Azure credentials per supported backend
service. Startup validation fails loud on missing, duplicate, or ambiguous
mappings.

## Consequences

- `ICredentialResolver` can validate SigV4 by access key ID and retrieve the
Azure credential for the selected service without discovery calls.
- Operators own rotation and deployment of the config or identity binding.
- The proxy does not infer tenant, subscription, or region from topology.
- Partial configuration is a startup error, not a runtime best effort.

## Revisit triggers

Revisit if a dynamic credential provider is proposed with explicit startup and
runtime failure semantics that preserve fail-loud validation.
