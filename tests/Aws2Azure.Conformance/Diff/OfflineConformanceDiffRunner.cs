using System.Text;
using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Diff;

/// <summary>
/// High-level outcome of a credential-free Tier-3 diff case.
/// </summary>
public enum OfflineConformanceDiffStatus
{
    Passed,
    Skipped,
    Failed,
}

/// <summary>
/// One unexpected divergence plus the step it came from. Multi-step happy-path
/// cases compare each recorded response independently, so the step name is part
/// of the diagnostic surface.
/// </summary>
public sealed record OfflineConformanceStepDifference(string StepName, Divergence Divergence);

/// <summary>
/// One successfully loaded pair of recorded captures.
/// </summary>
public sealed record OfflineConformanceComparedStep(
    string StepName,
    GoldenFile RealAwsGolden,
    GoldenFile RealAzureEvidence);

/// <summary>
/// Structured result of a credential-free Tier-3 diff case.
/// </summary>
public sealed class OfflineConformanceDiffResult
{
    public OfflineConformanceDiffResult(
        OfflineConformanceDiffStatus status,
        string message,
        IReadOnlyList<OfflineConformanceComparedStep>? comparedSteps = null,
        IReadOnlyList<OfflineConformanceStepDifference>? unexpectedDifferences = null)
    {
        Status = status;
        Message = message;
        ComparedSteps = comparedSteps ?? [];
        UnexpectedDifferences = unexpectedDifferences ?? [];
    }

    public OfflineConformanceDiffStatus Status { get; }

    public string Message { get; }

    public IReadOnlyList<OfflineConformanceComparedStep> ComparedSteps { get; }

    public IReadOnlyList<OfflineConformanceStepDifference> UnexpectedDifferences { get; }
}

/// <summary>
/// Offline file-based Tier-3 differential. It never talks to AWS, Azure, or the
/// proxy; it only loads already-canonicalized captures from disk, compares them
/// step-by-step, and filters documented divergences through the existing gap-doc
/// allow-list.
/// </summary>
public static class OfflineConformanceDiffRunner
{
    private static readonly ConformanceCaseContext PlanningContext = new(
        "AKIAOFFLINETIER3001",
        "offline-tier3-secret",
        new Uri("http://localhost/"),
        Properties: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tableName"] = "conformance-table",
            ["streamName"] = "conformance-stream",
        });

    public static async Task<OfflineConformanceDiffResult> CompareAsync(
        string service,
        IConformanceCase testCase,
        GoldenStore goldenStore,
        EvidenceStore evidenceStore,
        ConformanceAllowList allowList,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(goldenStore);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(allowList);

        var plan = await testCase.CreatePlanAsync(PlanningContext, cancellationToken);
        var singleStep = plan.Steps.Count == 1;
        var missingArtifacts = new List<string>();
        var comparedSteps = new List<OfflineConformanceComparedStep>(plan.Steps.Count);

        foreach (var step in plan.Steps)
        {
            var hasGolden = TryLoadGolden(
                goldenStore,
                testCase.Name,
                step.Name,
                singleStep,
                out var golden,
                out var goldenPathDescription);
            var hasEvidence = TryLoadEvidence(
                evidenceStore,
                testCase.Name,
                step.Name,
                singleStep,
                out var evidence,
                out var evidencePathDescription);

            if (!hasGolden)
            {
                missingArtifacts.Add(
                    $"real-AWS golden for step '{step.Name}' is missing at {goldenPathDescription}");
            }

            if (!hasEvidence)
            {
                missingArtifacts.Add(
                    $"real-Azure evidence for step '{step.Name}' is missing at {evidencePathDescription}");
            }

            if (hasGolden && hasEvidence)
            {
                comparedSteps.Add(new OfflineConformanceComparedStep(step.Name, golden, evidence));
            }
        }

        // Intentional whole-case gating: Tier-3 compares fully materialized
        // recorded response sets. If any expected step file is missing on either
        // side, the case remains a skip until capture/export catches up.
        if (missingArtifacts.Count > 0)
        {
            return new OfflineConformanceDiffResult(
                OfflineConformanceDiffStatus.Skipped,
                BuildSkipMessage(service, testCase, missingArtifacts));
        }

        var unexpected = new List<OfflineConformanceStepDifference>();
        foreach (var step in comparedSteps)
        {
            var expected = NormalizeForComparison(
                testCase.Name,
                CanonicalResponse.ParseRendered(step.RealAwsGolden.CanonicalText));
            var actual = NormalizeForComparison(
                testCase.Name,
                CanonicalResponse.ParseRendered(step.RealAzureEvidence.CanonicalText));
            var (_, unexpectedForStep) = allowList.Partition(
                CanonicalDiff.Compare(expected, actual),
                testCase.Name);

            unexpected.AddRange(
                unexpectedForStep.Select(diff => new OfflineConformanceStepDifference(step.StepName, diff)));
        }

        return unexpected.Count == 0
            ? new OfflineConformanceDiffResult(
                OfflineConformanceDiffStatus.Passed,
                BuildSuccessMessage(service, testCase, comparedSteps.Count),
                comparedSteps)
            : new OfflineConformanceDiffResult(
                OfflineConformanceDiffStatus.Failed,
                BuildFailureMessage(service, testCase, comparedSteps, unexpected),
                comparedSteps,
                unexpected);
    }

    internal static CanonicalResponse NormalizeForComparison(string _caseName, CanonicalResponse response)
    {
        var fields = new List<CanonicalField>(response.BodyFields.Count);
        foreach (var field in response.BodyFields)
        {
            fields.Add(field.Name is "Name" or "BucketArn"
                ? new CanonicalField(field.Name, NormalizeBucketNameWithinValue(field.Value))
                : field);
        }
        var headers = new List<CanonicalField>(response.Headers.Count);
        foreach (var header in response.Headers)
        {
            headers.Add(header.Name == "x-amz-bucket-arn"
                ? new CanonicalField(header.Name, NormalizeBucketNameWithinValue(header.Value))
                : header);
        }
        return response with { BodyFields = fields, Headers = headers };
    }

    private static string NormalizeBucketNameWithinValue(string value)
    {
        const string arnPrefix = "arn:aws:s3:::";
        if (value.StartsWith(arnPrefix, StringComparison.Ordinal))
        {
            return arnPrefix + NormalizeBucketName(value[arnPrefix.Length..]);
        }

        return NormalizeBucketName(value);
    }

    private static string NormalizeBucketName(string value)
    {
        return LooksLikeBucketName(value) ? "<bucket>" : value;
    }

    private static bool LooksLikeBucketName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 3 || value.Length > 63)
        {
            return false;
        }

        if (value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryLoadGolden(
        GoldenStore store,
        string caseName,
        string stepName,
        bool singleStep,
        out GoldenFile golden,
        out string pathDescription)
    {
        if (singleStep)
        {
            var flatPath = store.PathFor(caseName, GoldenProvenance.SourceRealAws);
            if (store.TryLoad(caseName, GoldenProvenance.SourceRealAws, out golden))
            {
                pathDescription = "'" + flatPath + "'";
                return true;
            }

            var stepPath = store.PathForStep(caseName, stepName, GoldenProvenance.SourceRealAws);
            if (store.TryLoadStep(caseName, stepName, GoldenProvenance.SourceRealAws, out golden))
            {
                pathDescription = "'" + stepPath + "'";
                return true;
            }

            pathDescription = $"'{flatPath}' or '{stepPath}'";
            return false;
        }

        pathDescription = "'" + store.PathForStep(caseName, stepName, GoldenProvenance.SourceRealAws) + "'";
        return store.TryLoadStep(caseName, stepName, GoldenProvenance.SourceRealAws, out golden);
    }

    private static bool TryLoadEvidence(
        EvidenceStore store,
        string caseName,
        string stepName,
        bool singleStep,
        out GoldenFile evidence,
        out string pathDescription)
    {
        if (singleStep)
        {
            var flatPath = store.PathFor(caseName);
            if (store.TryLoad(caseName, out evidence))
            {
                pathDescription = "'" + flatPath + "'";
                return true;
            }

            var stepPath = store.PathForStep(caseName, stepName);
            if (store.TryLoadStep(caseName, stepName, out evidence))
            {
                pathDescription = "'" + stepPath + "'";
                return true;
            }

            pathDescription = $"'{flatPath}' or '{stepPath}'";
            return false;
        }

        pathDescription = "'" + store.PathForStep(caseName, stepName) + "'";
        return store.TryLoadStep(caseName, stepName, out evidence);
    }

    private static string BuildSuccessMessage(
        string service,
        IConformanceCase testCase,
        int comparedStepCount) =>
        $"Tier-3 credential-free diff matched {comparedStepCount} recorded step(s) " +
        $"for '{service}/{testCase.Name}'.";

    private static string BuildSkipMessage(
        string service,
        IConformanceCase testCase,
        IReadOnlyList<string> missingArtifacts)
    {
        var sb = new StringBuilder();
        sb.Append("Tier-3 credential-free diff skipped for '")
          .Append(service).Append('/').Append(testCase.Name).Append("'. ")
          .Append("This offline comparer only activates once both the separate real-AWS ")
          .Append("capture workflow and the real-Azure evidence-export workflow have ")
          .Append("materialized the expected files.\n");

        foreach (var missing in missingArtifacts)
        {
            sb.Append("  - ").Append(missing).Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildFailureMessage(
        string service,
        IConformanceCase testCase,
        IReadOnlyList<OfflineConformanceComparedStep> comparedSteps,
        IReadOnlyList<OfflineConformanceStepDifference> unexpectedDifferences)
    {
        var byStep = unexpectedDifferences
            .GroupBy(entry => entry.StepName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var sb = new StringBuilder();

        sb.Append("Tier-3 credential-free divergence for '")
          .Append(service).Append('/').Append(testCase.Name).Append("'.\n")
          .Append("Undocumented divergences (accept only via gap-doc ")
          .Append("[conformance:<tag>] or [conformance:")
          .Append(testCase.Name).Append("::<tag>] notes):\n");

        foreach (var step in unexpectedDifferences)
        {
            sb.Append("  - step '").Append(step.StepName).Append("' [")
              .Append(step.Divergence.Tag).Append("] ")
              .Append(step.Divergence.Description).Append('\n');
        }

        foreach (var comparedStep in comparedSteps)
        {
            if (!byStep.TryGetValue(comparedStep.StepName, out var stepDiffs))
            {
                continue;
            }

            sb.Append("\n--- step ").Append(comparedStep.StepName).Append(" expected (real AWS) ---\n")
              .Append(comparedStep.RealAwsGolden.CanonicalText)
              .Append("\n--- step ").Append(comparedStep.StepName).Append(" actual (proxy over real Azure) ---\n")
              .Append(comparedStep.RealAzureEvidence.CanonicalText);

            if (stepDiffs.Length == 0)
            {
                continue;
            }
        }

        return sb.ToString();
    }
}
