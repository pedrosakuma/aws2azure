<!--
THESIS: One evidence trail replaces the repository scavenger hunt.
OWN-WORLD: Restrained slate and cyan, strong document rhythm, and ruled journey rows rather than a marketing card grid.
STORY: Readers identify their role, follow the shortest trustworthy path, and search the same surface for exact evidence.
FIRST VIEWPORT: A concise migration promise leads directly into evaluator, developer, and operator routes, with search always in the header.
FORM: Read-mode technical field guide; persona pathways organize existing source material without duplicating generated capability content.
-->

# One migration, one evidence trail

<div class="portal-hero" markdown>

`aws2azure` accepts AWS wire-protocol requests and translates them into direct
Azure REST calls. This portal brings adoption evidence, integration guidance,
operation-level gaps, and production procedures into one searchable surface.

[Evaluate a workload](site/workload-compatibility.md){ .md-button .md-button--primary }
[Run the quickstart](getting-started.md){ .md-button }

</div>

## Choose the question you are answering

<div class="portal-paths">
  <section class="portal-path">
    <div class="portal-path__role">Evaluator</div>
    <div>
      <h3>Can this workload move safely?</h3>
      <p>Start with maturity and workload verdicts, then verify every required operation and semantic difference.</p>
    </div>
    <div class="portal-path__links">
      <a href="project-maturity.md">Maturity terms</a>
      <a href="site/workload-compatibility.md">Workload verdicts</a>
      <a href="site/coverage.md">Operation coverage</a>
    </div>
  </section>
  <section class="portal-path">
    <div class="portal-path__role">Developer</div>
    <div>
      <h3>How do I point an AWS client at Azure?</h3>
      <p>Run the local round-trip, select authentication, and match your application to a versioned workload profile.</p>
    </div>
    <div class="portal-path__links">
      <a href="getting-started.md">Getting started</a>
      <a href="azure-authentication.md">Authentication</a>
      <a href="workloads/README.md">Workload profiles</a>
    </div>
  </section>
  <section class="portal-path">
    <div class="portal-path__role">Operator</div>
    <div>
      <h3>How do I deploy, prove, and recover it?</h3>
      <p>Choose the topology, qualify against real Azure, set workload gates, and retain an exact rollback target.</p>
    </div>
    <div class="portal-path__links">
      <a href="deployment/sidecar.md">Deploy</a>
      <a href="deployment/production-runbook.md">Production runbook</a>
      <a href="versioning-and-compatibility.md">Versioning</a>
    </div>
  </section>
</div>

## Search the source of truth

Use the search field in the header for an AWS operation such as `CreateBucket`,
a configuration concept such as `azureIdentities`, a workload verdict such as
`conditional`, or an operator procedure such as `rollback`.

The portal has two intentionally different content layers:

- **Hand-authored guides** explain evaluation, configuration, authentication,
  deployment, operations, versioning, performance, and architecture.
- **Generated capability reference** under
  [Operation reference](site/index.md) is rendered from the gap YAML. It remains
  generated and is never duplicated or hand-edited for publication.

## Fast reference

| Need | Start here |
|---|---|
| Determine whether a workload is supportable | [Workload compatibility](site/workload-compatibility.md) |
| Check one AWS API | [Coverage matrix](site/coverage.md) |
| Understand a cross-cutting incompatibility | [Design gaps](site/design-gaps.md) |
| Verify real-Azure evidence | [Real-Azure conformance and divergences](site/divergences.md) |
| Prepare a production rollout | [Production runbook](deployment/production-runbook.md) |
| Understand a foundational constraint | [Architecture Decision Records](adr/README.md) |
