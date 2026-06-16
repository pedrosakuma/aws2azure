# ADR 0015 — Streaming XML with XmlReader and XmlWriter

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0 (serialization baseline)

## Context

Several AWS services, especially S3, use XML request or response bodies.
`XmlSerializer` and similar serializers depend on reflection and generated
metadata that are poorly aligned with Native AOT and hot-path allocation goals.

## Decision

Production XML handling uses `XmlReader` and `XmlWriter` directly. The codebase
does not use `XmlSerializer`, `DataContractSerializer`, or other
reflection-heavy XML serializers in production code.

## Consequences

- XML parsing and emission are explicit, streaming, and allocation-conscious.
- AWS service response formats can be matched precisely, including error shapes
and optional elements.
- Developers write more serialization code by hand, backed by golden tests for
wire compatibility.
- AOT publish avoids serializer reflection metadata and dynamic code generation.

## Revisit triggers

Revisit if a source-generated XML serializer becomes available and demonstrates
AOT safety, wire-shape control, and lower maintenance cost.
