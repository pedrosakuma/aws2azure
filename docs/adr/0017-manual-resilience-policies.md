# ADR 0017 — Manual resilience policies instead of Polly

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (runtime baseline)

## Context

Outbound Azure calls need timeouts, retry decisions, and error mapping, but the
proxy also needs a small dependency closure and behaviour that is easy to audit
per service. A general-purpose resilience framework can obscure which failures
are retried and add dependency and configuration surface area.

## Decision

Resilience is implemented with small, explicit policies in the relevant modules
or shared core helpers. Production code does not depend on Polly.

## Consequences

- Retry and timeout behaviour is visible in code and tailored to each translated
operation.
- The dependency graph remains smaller and easier to keep AOT-clean.
- Developers must avoid duplicating policy logic by extracting focused shared
helpers when patterns repeat.
- Behavioural differences caused by retries, throttling, or partial failures are
documented in gap docs.

## Revisit triggers

Revisit if manual policies become unmaintainable and a proposed resilience
library demonstrates AOT compatibility, low overhead, and clearer semantics than
the in-repo helpers.
