# Copilot bootstrap — aws2azure

Follow the repository-wide instructions in [`../AGENTS.md`](../AGENTS.md).
They are the canonical development contract for every human and coding agent.

When Copilot tools are available, use a read-only code-review agent as the
independent reviewer for non-trivial changes. The reviewer implementation and
model may vary; the required outcome is an independent diff review with all
real findings addressed.

For parallel agentic work, one session/worktree owns exactly one branch and one
PR. The coordinator is the only merge owner and assigns a single writer per
wave for workflows, Bicep, generated gap-doc output, qualification matrices,
and performance/footprint baselines. Keep 3–4 PRs active, fan out only
independent work, and stack only real dependencies.

Before regenerating `docs/site/*` or
`src/Aws2Azure.Core/Generated/CapabilityRegistry.g.cs`, rebase onto current
`origin/main`, discard generated conflict edits, regenerate from source, and
never hand-merge generated output.

After fetching `main`, run:

```text
dotnet run --project tools/Aws2Azure.ChangeAwareValidation -- --base main --pretty
```

Apply every required label in its JSON plan. Hot paths require `run-perf`;
authentication/transport require `run-integration` and `run-real-azure`;
startup/build-graph changes require `run-footprint`. Compare failures with
`main` under equivalent conditions before declaring a regression. Never
automatically raise thresholds/baselines or repeatedly rerun a failing gate.
