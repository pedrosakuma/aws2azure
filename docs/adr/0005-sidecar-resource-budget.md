# ADR 0005 — Sidecar-first resource budget

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (architecture baseline)

## Context

The proxy sits in the request path and is commonly deployed next to each
workload. A high idle RSS, slow cold start, large image, or expensive background
activity would be paid once per workload replica and could block adoption even
when protocol translation is correct.

## Decision

Resource efficiency is a first-class design constraint. Prefer small idle memory,
fast startup, small images, bounded buffers, and minimal dependency closure over
throughput-at-any-cost designs. New caches, dependencies, background loops, and
pooled resources must justify their sidecar cost.

## Consequences

- Native AOT, no cloud SDK dependency, manual composition, and source-generated
code paths are not micro-optimizations; they support the deployment model.
- Hot paths avoid unnecessary allocations, reflection, boxing, and unbounded
collections.
- Performance and footprint claims must cite the harness that produced them.
- A feature can be rejected or made opt-in solely because its steady-state cost is
not acceptable for a sidecar.

## Revisit triggers

Revisit if the project explicitly stops targeting sidecar deployments and accepts
a different footprint budget.
