namespace Aws2Azure.DocsEval;

/// <summary>
/// Optional, out-of-scope-by-default extension point for benchmarking an actual
/// language model against <see cref="EvalDataset"/>. This type intentionally
/// contains no model integration: wiring an LLM here would require external
/// credentials and network access, which the deterministic gate
/// (<see cref="Evaluator"/>) must never require. A future, separately-invoked
/// tool/script can implement <see cref="RunAsync"/> to send
/// <see cref="EvalCase.Question"/> to a model and grade
/// <see cref="EvalCase.ExpectedAnswer"/> / <see cref="EvalCase.ProhibitedConclusions"/>
/// against its response. It is never called by <c>Program.Main</c> or by CI.
/// </summary>
public static class ModelBenchmarkPlaceholder
{
    public static Task RunAsync(EvalDataset dataset, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Optional model benchmarking is not implemented. It is an explicitly out-of-scope " +
            "extension point (see docs/testing/retrieval-eval.md) and must not be required to " +
            "build, test, or run the deterministic evaluator.");
}
