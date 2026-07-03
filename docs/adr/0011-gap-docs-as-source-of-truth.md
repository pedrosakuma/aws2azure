# ADR 0011 — Gap docs as the capability source of truth

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (documentation baseline)

## Context

The proxy is not intended to be feature-complete with AWS. If gaps live only in
code comments or release notes, users cannot safely decide whether a workload is
compatible. The runtime also needs a consistent capability view for modules and
health surfaces.

## Decision

Every implemented or stubbed operation has a YAML gap document under
`docs/gaps/<service>/<Operation>.yaml`. Those YAML files are the source of truth
for operation status, sub-features, behavioural differences, references, rendered
Markdown, and generated capability constants.

Cross-cutting limitations that do not belong to any single operation — the
consistency model, transaction scope, or absent control-plane surfaces of a
service — are captured in an optional `docs/gaps/<service>/_design.yaml` and
rendered into `docs/site/design-gaps.md`, so the per-operation matrix stays
focused and the architectural story is still checked-in and validated. See
[`docs/gaps/README.md`](../gaps/README.md) for the schema.

## Consequences

- Changing operation support requires updating the YAML in the same PR as the
code change.
- The gap-docs tool validates YAML, renders `docs/site/`, and generates the
capability registry consumed by modules.
- Unknown or invalid metadata must fail loud during validation rather than being
silently dropped or defaulted.
- Documentation drift is treated as a build/CI problem, not a post-release task.

## Revisit triggers

Revisit if capability metadata moves to another checked-in source format that can
still validate, render docs, and generate runtime constants from one source.
