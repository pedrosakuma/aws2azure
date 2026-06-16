# ADR 0009 — AWS wire protocol without AWS SDK dependencies

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

The proxy accepts requests from AWS SDK clients, but it is not itself an AWS
client. Pulling AWS SDK packages into the proxy would add a large dependency
closure while still leaving the proxy responsible for receiving raw HTTP,
validating SigV4, and shaping service-specific responses.

## Decision

Production code speaks the **AWS wire protocol directly** and does not depend on
AWS SDK packages. SigV4 validation, request parsing, and AWS-shaped errors are
implemented in the proxy and service modules.

## Consequences

- Compatibility is tested at the wire level rather than by reusing SDK internals.
- The proxy can serve clients from any AWS SDK language because HTTP is the
contract.
- AWS service quirks must be captured in conformance tests and gap docs.
- SDK package updates cannot silently change proxy behaviour.

## Revisit triggers

Revisit if AWS exposes an official, small, AOT-safe wire-protocol component that
solves validation or parsing without pulling in client-side SDK behaviour.
