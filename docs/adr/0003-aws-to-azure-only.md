# ADR 0003 — AWS-to-Azure translation only

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

`aws2azure` exists to let workloads that already speak AWS service protocols run
against Azure targets without application code changes. That migration path has a
clear source and target. Supporting additional directions would multiply every
routing, authentication, gap-documentation, and test matrix.

## Decision

The proxy translates **AWS wire protocols to Azure REST APIs only**. It does not
provide Azure-to-AWS translation, bidirectional synchronization, or support for
other cloud targets.

## Consequences

- Service modules model AWS request and response semantics at the edge and map
them onto Azure backend APIs.
- Authentication starts with AWS SigV4 validation and then selects configured
Azure credentials for the downstream call.
- Product and gap documentation can stay explicit about one migration direction
instead of claiming cloud-neutral compatibility.
- Requests for reverse translation or another cloud require a new project or a
superseding ADR, not incremental scope creep in this codebase.

## Revisit triggers

Revisit only if the project charter changes through a tracked issue that accepts
the cost of a second protocol direction and its full conformance matrix.
