using System.Text.Json;
using System.Text.Json.Nodes;
using Aws2Azure.GapDocs;

namespace Aws2Azure.DocsEval;

public sealed record EvalViolation(string CaseId, string Message);

public sealed record EvalResult(
    int TotalCases,
    int PassedCases,
    IReadOnlyList<EvalViolation> Violations,
    IReadOnlyList<string> MaturityClaimViolations)
{
    public bool IsClean => Violations.Count == 0 && MaturityClaimViolations.Count == 0;
}

/// <summary>
/// Deterministic, offline evaluator for <see cref="EvalDataset"/>. Every check is
/// a mechanical comparison against the current repository state (workload-ga.json
/// verdicts, gap-doc operation status, the canonical config JSON Schema,
/// generated per-operation pages, and cited files/text) — it never calls a
/// language model. This is the CI-safe half of retrieval evaluation; invoking an
/// actual model against <see cref="EvalCase.Question"/> is a separate, optional,
/// explicitly-labeled extension point (see <c>ModelBenchmarkPlaceholder</c>).
/// </summary>
public static class Evaluator
{
    private static readonly string[] BareMaturityTerms =
    [
        "generally available",
        "production-ready",
        "production ready",
    ];

    // Every hand-authored doc under README.md and docs/ can go stale relative
    // to live workload certification, so the scan covers the whole tree.
    // Generated pages under docs/site/** are excluded (see
    // <see cref="IsExcludedFromMaturityScan"/>): they are produced by
    // tools/Aws2Azure.GapDocs from the same canonical inputs this evaluator
    // already cross-checks, and are out of scope to re-validate here.
    private static readonly string[] MaturityScanRoots =
    [
        "README.md",
        "docs",
    ];

    // Relative-path (repo-root-rooted, '/'-separated) prefixes excluded from
    // the maturity scan even though they live under a scanned root.
    private static readonly string[] MaturityScanExclusions =
    [
        "docs/site/",
    ];

    public static EvalResult Run(string repoRoot, EvalDataset dataset)
    {
        var violations = new List<EvalViolation>();

        var workloadGa = LoadWorkloadGa(repoRoot);
        var configSchema = LoadConfigSchema(repoRoot);
        var operations = Loader.LoadAll(Path.Combine(repoRoot, "docs", "gaps"));
        var operationsByKey = operations.ToDictionary(
            op => OperationKey(op.Service, op.Operation),
            StringComparer.Ordinal);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var passed = 0;
        foreach (var evalCase in dataset.Cases)
        {
            if (string.IsNullOrWhiteSpace(evalCase.Id))
            {
                violations.Add(new EvalViolation("<missing-id>", "Case is missing an id."));
                continue;
            }
            if (!seenIds.Add(evalCase.Id))
            {
                violations.Add(new EvalViolation(evalCase.Id, "Duplicate case id."));
                continue;
            }
            if (evalCase.ExpectedAnswer.CanonicalSources.Count == 0)
            {
                violations.Add(new EvalViolation(evalCase.Id, "Expected answer cites no canonical source."));
            }
            if (string.IsNullOrWhiteSpace(evalCase.ExpectedAnswer.Precedence))
            {
                violations.Add(new EvalViolation(evalCase.Id, "Expected answer states no precedence rule."));
            }
            foreach (var source in evalCase.ExpectedAnswer.CanonicalSources)
            {
                if (!File.Exists(Path.Combine(repoRoot, ToNativePath(source))))
                {
                    violations.Add(new EvalViolation(
                        evalCase.Id,
                        $"Canonical source '{source}' does not exist in the repository."));
                }
            }

            var caseViolations = new List<string>();
            foreach (var check in evalCase.Checks)
            {
                var error = RunCheck(repoRoot, workloadGa, configSchema, operationsByKey, check);
                if (error is not null)
                {
                    caseViolations.Add(error);
                }
            }

            if (caseViolations.Count == 0)
            {
                passed++;
            }
            else
            {
                foreach (var error in caseViolations)
                {
                    violations.Add(new EvalViolation(evalCase.Id, error));
                }
            }
        }

        var maturityViolations = ScanForUncitedMaturityClaims(repoRoot);

        return new EvalResult(dataset.Cases.Count, passed, violations, maturityViolations);
    }

    private static string? RunCheck(
        string repoRoot,
        JsonArray workloadGa,
        JsonObject configSchema,
        IReadOnlyDictionary<string, OperationDoc> operationsByKey,
        EvalCheck check)
    {
        switch (check.Type)
        {
            case "profile_verdict":
            {
                var profile = FindProfile(workloadGa, check.ProfileId!);
                if (profile is null)
                {
                    return $"Unknown workload profile '{check.ProfileId}' in workload-ga.json.";
                }
                var verdict = profile["verdict"]?.GetValue<string>();
                if (!string.Equals(verdict, check.ExpectedVerdict, StringComparison.Ordinal))
                {
                    return $"Profile '{check.ProfileId}' verdict is '{verdict}', expected '{check.ExpectedVerdict}'. " +
                           "A stale dataset case or a stale documentation claim now disagrees with live workload certification.";
                }
                return null;
            }
            case "operation_status":
            {
                var key = OperationKey(check.Service!, check.Operation!);
                if (!operationsByKey.TryGetValue(key, out var doc))
                {
                    return $"Unknown gap-doc operation '{check.Service}/{check.Operation}'.";
                }
                if (!string.Equals(doc.Status, check.ExpectedStatus, StringComparison.Ordinal))
                {
                    return $"Operation '{check.Service}/{check.Operation}' status is '{doc.Status}', expected '{check.ExpectedStatus}'.";
                }
                return null;
            }
            case "finding_disposition":
            {
                var profile = FindProfile(workloadGa, check.ProfileId!);
                if (profile is null)
                {
                    return $"Unknown workload profile '{check.ProfileId}' in workload-ga.json.";
                }
                var findings = profile["findings"]?.AsArray() ?? [];
                var match = findings.FirstOrDefault(finding =>
                    string.Equals(finding?["code"]?.GetValue<string>(), check.Code, StringComparison.Ordinal)
                    && string.Equals(finding?["subject"]?.GetValue<string>(), check.Subject, StringComparison.Ordinal));
                if (match is null)
                {
                    return $"Profile '{check.ProfileId}' has no finding '{check.Code}' for subject '{check.Subject}'.";
                }
                var disposition = match["disposition"]?.GetValue<string>();
                if (!string.Equals(disposition, check.ExpectedDisposition, StringComparison.Ordinal))
                {
                    return $"Finding '{check.Code}'/'{check.Subject}' on profile '{check.ProfileId}' has disposition " +
                           $"'{disposition}', expected '{check.ExpectedDisposition}'.";
                }
                return null;
            }
            case "schema_path_exists":
            {
                var exists = SchemaPathResolver.PathExists(configSchema, check.SchemaPath!);
                if (exists != check.ExpectedExists)
                {
                    return $"Config schema path '{check.SchemaPath}' exists={exists}, expected {check.ExpectedExists}. " +
                           (check.ExpectedExists == true
                               ? "A cited configuration field is missing from config.schema.json."
                               : "A fabricated configuration field unexpectedly resolves against config.schema.json.");
                }
                return null;
            }
            case "schema_canonical_value_exists":
            {
                var exists = SchemaPathResolver.CanonicalValueExists(configSchema, check.CanonicalValue!);
                if (exists != check.ExpectedExists)
                {
                    return $"Canonical value '{check.CanonicalValue}' exists={exists}, expected {check.ExpectedExists}.";
                }
                return null;
            }
            case "source_exists":
            {
                var exists = File.Exists(Path.Combine(repoRoot, ToNativePath(check.Path!)));
                if (exists != check.ExpectedExists)
                {
                    return $"Source '{check.Path}' exists={exists}, expected {check.ExpectedExists}.";
                }
                return null;
            }
            case "operation_reference_exists":
            {
                var relative = $"docs/site/operations/{check.Service!.ToLowerInvariant()}/{check.Operation!.ToLowerInvariant()}.md";
                var exists = File.Exists(Path.Combine(repoRoot, ToNativePath(relative)));
                if (!exists)
                {
                    return $"Generated operation reference page '{relative}' is missing.";
                }
                return null;
            }
            case "text_contains":
            {
                var fullPath = Path.Combine(repoRoot, ToNativePath(check.Path!));
                if (!File.Exists(fullPath))
                {
                    return $"Source '{check.Path}' does not exist.";
                }
                var text = NormalizeWhitespace(File.ReadAllText(fullPath));
                var contains = text.Contains(NormalizeWhitespace(check.MustContain!), StringComparison.Ordinal);
                if (contains != check.ExpectedExists)
                {
                    return $"'{check.Path}' contains '{check.MustContain}'={contains}, expected {check.ExpectedExists}.";
                }
                return null;
            }
            default:
                return $"Unknown check type '{check.Type}'.";
        }
    }

    private static JsonObject? FindProfile(JsonArray workloadGa, string profileId) =>
        workloadGa
            .Select(node => node?.AsObject())
            .FirstOrDefault(profile =>
                string.Equals(profile?["profile_id"]?.GetValue<string>(), profileId, StringComparison.Ordinal));

    private static string OperationKey(string service, string operation) =>
        $"{service.ToLowerInvariant()}/{operation}";

    /// <summary>
    /// Flags hand-authored documentation that states an unhedged bare maturity
    /// claim (GA, production-ready, generally available) with no nearby signal
    /// that the status is qualified (candidate/conditional/blocked, a
    /// "requires"/"until" hedge, or a citation to live workload certification).
    /// This is a heuristic, per-match window check, not a whole-file scan: a
    /// file may legitimately discuss what GA requires (hedged) while still
    /// containing the word "GA" elsewhere without a citation.
    /// </summary>
    internal static IReadOnlyList<string> ScanForUncitedMaturityClaims(string repoRoot)
    {
        var violations = new List<string>();
        foreach (var root in MaturityScanRoots)
        {
            var fullRoot = Path.Combine(repoRoot, ToNativePath(root));
            IEnumerable<string> files = Directory.Exists(fullRoot)
                ? Directory.EnumerateFiles(fullRoot, "*.md", SearchOption.AllDirectories)
                : File.Exists(fullRoot)
                    ? [fullRoot]
                    : [];

            foreach (var file in files.OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (MaturityScanExclusions.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                var hasFileWideCitation = FileWideCitationTerms.Any(term =>
                    text.Contains(term, StringComparison.OrdinalIgnoreCase));
                foreach (var match in FindMaturityClaimMatches(text))
                {
                    if (hasFileWideCitation
                        || IsHeadingLine(text, match.Index)
                        || IsKebabCaseToken(text, match.Index, match.Length)
                        || HasNearbyHedge(text, match.Index, match.Length))
                    {
                        continue;
                    }
                    var line = text[..match.Index].Count(c => c == '\n') + 1;
                    violations.Add(
                        $"{relative}:{line}: states an unhedged maturity claim ('{match.Value}') with no nearby " +
                        "candidate/conditional/blocked qualifier, hedge, or citation to docs/site/workload-ga.json.");
                }
            }
        }
        return violations;
    }

    private static IEnumerable<System.Text.RegularExpressions.Match> FindMaturityClaimMatches(string text)
    {
        var pattern = string.Join(
            "|",
            BareMaturityTerms.Select(System.Text.RegularExpressions.Regex.Escape).Append(@"\bGA\b"));
        return System.Text.RegularExpressions.Regex.Matches(
            text,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Cast<System.Text.RegularExpressions.Match>();
    }

    // Checked anywhere in the file (not window-limited): a single disclaimer
    // near the top of a doc legitimately covers a historical table further down.
    private static readonly string[] FileWideCitationTerms =
    [
        "workload-ga.json", "workload-ga.md", "live workload certification",
        "live_workload_certification",
    ];

    // Checked only in a local window around the match: a nearby qualifier that
    // shows the claim is not being asserted as an unconditional current fact.
    private static readonly string[] MaturityHedgeTerms =
    [
        "candidate", "conditional", "blocked", "requires", "until", " not ",
        "cannot", "kubernetes", "tracked in", "defined in", "historical", "never",
    ];

    private static bool IsHeadingLine(string text, int matchIndex)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, matchIndex - 1)) + 1;
        return lineStart < text.Length && text[lineStart] == '#';
    }

    // Skips kebab-case/identifier occurrences (e.g. "workload-ga-evaluator",
    // filenames, marker tokens) that are not English-language maturity claims.
    private static bool IsKebabCaseToken(string text, int matchIndex, int matchLength)
    {
        var before = matchIndex > 0 ? text[matchIndex - 1] : '\0';
        var afterIndex = matchIndex + matchLength;
        var after = afterIndex < text.Length ? text[afterIndex] : '\0';
        return before is '-' or '_' || after is '-' or '_';
    }

    private static bool HasNearbyHedge(string text, int matchIndex, int matchLength)
    {
        const int window = 160;
        var start = Math.Max(0, matchIndex - window);
        var end = Math.Min(text.Length, matchIndex + matchLength + window);
        var slice = text[start..end];
        return MaturityHedgeTerms.Any(term => slice.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonArray LoadWorkloadGa(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "docs", "site", "workload-ga.json");
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)!.AsArray();
    }

    private static JsonObject LoadConfigSchema(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "config.schema.json");
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string ToNativePath(string repositoryRelativePath) =>
        repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar);

    private static string NormalizeWhitespace(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    public static EvalDataset LoadDataset(string path)
    {
        var text = File.ReadAllText(path);
        var dataset = JsonSerializer.Deserialize<EvalDataset>(text, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        return dataset ?? throw new InvalidDataException($"{path}: empty or invalid dataset.");
    }
}
