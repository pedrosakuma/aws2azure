using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class WorkloadManifest
{
    public int SchemaVersion { get; set; }
    public string Workload { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
    public Dictionary<string, bool> Requirements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkloadCompatibilityReport
{
    public int SchemaVersion { get; set; } = 1;
    public string Workload { get; set; } = string.Empty;
    public string Compatibility { get; set; } = "compatible";
    public List<WorkloadCompatibilityFinding> Findings { get; set; } = new();
}

public sealed class WorkloadCompatibilityFinding
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Compatibility { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Guidance { get; set; } = string.Empty;
    public string Documentation { get; set; } = string.Empty;
    public List<string> Details { get; set; } = new();
    public List<string> Workarounds { get; set; } = new();
    public List<WorkloadDesignGapFinding> DesignGaps { get; set; } = new();
}

public sealed class WorkloadDesignGapFinding
{
    public string Area { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string Workaround { get; set; } = string.Empty;
    public string Documentation { get; set; } = string.Empty;
    public List<string> References { get; set; } = new();
}

public static class WorkloadManifestLoader
{
    public static WorkloadManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Workload manifest not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        using var reader = new StreamReader(path);
        var manifest = deserializer.Deserialize<WorkloadManifest>(reader);
        return manifest ?? throw new InvalidDataException($"{path}: empty document");
    }
}

public static class WorkloadManifestValidator
{
    public const int CurrentSchemaVersion = 1;

    public static IReadOnlyList<string> Validate(
        WorkloadManifest manifest,
        IReadOnlyList<OperationDoc> operationDocs,
        IReadOnlyList<ServiceDesignDoc> designDocs)
    {
        var errors = new List<string>();
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add(
                $"unsupported schema_version '{manifest.SchemaVersion}'; expected {CurrentSchemaVersion}");
        }
        if (string.IsNullOrWhiteSpace(manifest.Workload))
        {
            errors.Add("missing required field 'workload'");
        }

        var manifestOperations = manifest.Operations;
        if (manifestOperations is null)
        {
            errors.Add("'operations' must be a list when specified");
            manifestOperations = new List<string>();
        }
        var manifestRequirements = manifest.Requirements;
        if (manifestRequirements is null)
        {
            errors.Add("'requirements' must be a mapping when specified");
            manifestRequirements = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        var operationsByKey = operationDocs.ToDictionary(
            doc => OperationKey(doc.Service, doc.Operation),
            StringComparer.OrdinalIgnoreCase);
        var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in manifestOperations)
        {
            if (!TryParseOperation(value, out var service, out var operation))
            {
                errors.Add(
                    $"invalid operation '{value}'; expected 'service:Operation'");
                continue;
            }

            var key = OperationKey(service, operation);
            if (!seenOperations.Add(key))
            {
                errors.Add($"duplicate operation '{value}'");
            }
            if (!operationsByKey.ContainsKey(key))
            {
                errors.Add($"unknown operation '{value}'");
            }
        }

        var patternsById = designDocs
            .SelectMany(doc => doc.WorkloadPatterns)
            .ToDictionary(pattern => pattern.Id, StringComparer.OrdinalIgnoreCase);
        var seenRequirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in manifestRequirements.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!seenRequirements.Add(requirement))
            {
                errors.Add($"duplicate requirement '{requirement}'");
            }
            if (!patternsById.ContainsKey(requirement))
            {
                errors.Add($"unknown requirement '{requirement}'");
            }
        }

        if (manifestOperations.Count == 0 && !manifestRequirements.Any(r => r.Value))
        {
            errors.Add("manifest must declare at least one operation or enabled requirement");
        }

        return errors;
    }

    internal static bool TryParseOperation(string value, out string service, out string operation)
    {
        service = string.Empty;
        operation = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator != value.LastIndexOf(':') || separator == value.Length - 1)
        {
            return false;
        }

        service = value[..separator].Trim();
        operation = value[(separator + 1)..].Trim();
        return service.Length > 0 && operation.Length > 0;
    }

    internal static string OperationKey(string service, string operation) =>
        service.ToLowerInvariant() + ":" + operation.ToLowerInvariant();
}

public static class WorkloadCompatibilityEvaluator
{
    public static WorkloadCompatibilityReport Evaluate(
        WorkloadManifest manifest,
        IReadOnlyList<OperationDoc> operationDocs,
        IReadOnlyList<ServiceDesignDoc> designDocs)
    {
        var operationDocsByKey = operationDocs.ToDictionary(
            doc => WorkloadManifestValidator.OperationKey(doc.Service, doc.Operation),
            StringComparer.OrdinalIgnoreCase);
        var patternsById = designDocs
            .SelectMany(doc => doc.WorkloadPatterns.Select(pattern => (doc, pattern)))
            .ToDictionary(item => item.pattern.Id, StringComparer.OrdinalIgnoreCase);

        var report = new WorkloadCompatibilityReport
        {
            Workload = manifest.Workload,
        };

        foreach (var operationReference in manifest.Operations.OrderBy(o => o, StringComparer.OrdinalIgnoreCase))
        {
            WorkloadManifestValidator.TryParseOperation(
                operationReference,
                out var service,
                out var operation);
            var operationDoc = operationDocsByKey[
                WorkloadManifestValidator.OperationKey(service, operation)];
            report.Findings.Add(CreateOperationFinding(operationDoc));
        }

        foreach (var requirement in manifest.Requirements
                     .Where(requirement => requirement.Value)
                     .OrderBy(requirement => requirement.Key, StringComparer.Ordinal))
        {
            var (designDoc, pattern) = patternsById[requirement.Key];
            report.Findings.Add(CreateRequirementFinding(designDoc, pattern));
        }

        report.Compatibility = report.Findings
            .Select(finding => finding.Compatibility)
            .OrderByDescending(Severity)
            .FirstOrDefault() ?? "compatible";
        return report;
    }

    private static WorkloadCompatibilityFinding CreateOperationFinding(OperationDoc operation)
    {
        var compatibility = operation.Status.ToLowerInvariant() switch
        {
            "implemented" => "compatible",
            "partial" => "conditional",
            "stub" or "unsupported" => "blocked",
            _ => throw new InvalidOperationException(
                $"Validated operation '{operation.Service}:{operation.Operation}' has unknown status '{operation.Status}'")
        };
        var finding = new WorkloadCompatibilityFinding
        {
            Kind = "operation",
            Id = operation.Service.ToLowerInvariant() + ":" + operation.Operation,
            Name = operation.Operation,
            Compatibility = compatibility,
            Summary =
                $"Operation is documented as {operation.Status} with Azure equivalent '{operation.AzureEquivalent}'.",
            Documentation =
                $"docs/site/{operation.Service.ToLowerInvariant()}.md#{DocumentationLinks.Anchor(operation.Operation)}",
        };
        finding.Details.AddRange(operation.BehaviorDifferences);

        foreach (var subFeature in operation.SubFeatures
                     .Where(feature => !feature.Status.Equals("implemented", StringComparison.OrdinalIgnoreCase)))
        {
            var detail = !string.IsNullOrWhiteSpace(subFeature.Gap)
                ? subFeature.Gap
                : subFeature.Notes;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                finding.Details.Add($"{subFeature.Name}: {detail}");
            }
            if (!string.IsNullOrWhiteSpace(subFeature.Workaround))
            {
                finding.Workarounds.Add($"{subFeature.Name}: {subFeature.Workaround}");
            }
        }

        return finding;
    }

    private static WorkloadCompatibilityFinding CreateRequirementFinding(
        ServiceDesignDoc designDoc,
        WorkloadPattern pattern)
    {
        var finding = new WorkloadCompatibilityFinding
        {
            Kind = "requirement",
            Id = pattern.Id,
            Name = pattern.Name,
            Compatibility = NormalizePatternCompatibility(pattern.Compatibility),
            Summary = pattern.Summary,
            Guidance = pattern.Guidance,
            Documentation =
                $"docs/site/workload-compatibility.md#{DocumentationLinks.Anchor(designDoc.Service)}",
        };
        var designGapsByArea = designDoc.DesignGaps.ToDictionary(
            gap => gap.Area,
            StringComparer.OrdinalIgnoreCase);
        foreach (var area in pattern.DesignGaps)
        {
            var gap = designGapsByArea[area];
            finding.DesignGaps.Add(new WorkloadDesignGapFinding
            {
                Area = gap.Area,
                Status = gap.Status,
                Summary = gap.Summary,
                Impact = gap.Impact,
                Workaround = gap.Workaround,
                Documentation =
                    $"docs/site/design-gaps.md#{DocumentationLinks.Anchor(designDoc.Service + "-" + gap.Area)}",
                References = new List<string>(gap.References),
            });
        }

        return finding;
    }

    private static string NormalizePatternCompatibility(string compatibility) =>
        compatibility.Equals("supported", StringComparison.OrdinalIgnoreCase)
            ? "compatible"
            : compatibility.ToLowerInvariant();

    private static int Severity(string compatibility) => compatibility switch
    {
        "compatible" => 0,
        "conditional" => 1,
        "blocked" => 2,
        _ => throw new InvalidOperationException(
            $"Unknown workload compatibility '{compatibility}'")
    };
}

public static class WorkloadReportRenderer
{
    public static string RenderMarkdown(WorkloadCompatibilityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Workload compatibility: {report.Workload}");
        sb.AppendLine();
        sb.AppendLine($"**Overall:** {Badge(report.Compatibility)}");
        sb.AppendLine();
        sb.AppendLine("| Kind | Item | Assessment |");
        sb.AppendLine("|---|---|---|");
        foreach (var finding in report.Findings)
        {
            sb.AppendLine(
                $"| {finding.Kind} | [{Esc(finding.Name)}]({finding.Documentation}) | {Badge(finding.Compatibility)} |");
        }

        foreach (var finding in report.Findings)
        {
            sb.AppendLine();
            sb.AppendLine($"## {finding.Kind}: {finding.Name}");
            sb.AppendLine();
            sb.AppendLine($"- **ID:** `{finding.Id}`");
            sb.AppendLine($"- **Assessment:** {Badge(finding.Compatibility)}");
            sb.AppendLine($"- **Why:** {finding.Summary}");
            if (!string.IsNullOrWhiteSpace(finding.Guidance))
            {
                sb.AppendLine($"- **Guidance:** {finding.Guidance}");
            }
            foreach (var detail in finding.Details)
            {
                sb.AppendLine($"- **Detail:** {detail}");
            }
            foreach (var workaround in finding.Workarounds)
            {
                sb.AppendLine($"- **Workaround:** {workaround}");
            }
            foreach (var gap in finding.DesignGaps)
            {
                sb.AppendLine(
                    $"- **Design gap:** [{gap.Area}]({gap.Documentation}) — {gap.Summary}");
                if (!string.IsNullOrWhiteSpace(gap.Impact))
                {
                    sb.AppendLine($"  Impact: {gap.Impact}");
                }
                if (!string.IsNullOrWhiteSpace(gap.Workaround))
                {
                    sb.AppendLine($"  Workaround: {gap.Workaround}");
                }
            }
        }

        return sb.ToString();
    }

    public static string RenderJson(WorkloadCompatibilityReport report) =>
        JsonSerializer.Serialize(report, WorkloadJsonContext.Default.WorkloadCompatibilityReport);

    private static string Badge(string compatibility) => compatibility switch
    {
        "compatible" => "✅ compatible",
        "conditional" => "🟡 conditional",
        "blocked" => "⛔ blocked",
        _ => compatibility
    };

    private static string Esc(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

public static class DocumentationLinks
{
    public static string Anchor(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-')
            {
                sb.Append(c);
            }
            else if (c == ' ' || c == '/' || c == '(' || c == ')')
            {
                sb.Append('-');
            }
        }
        return sb.ToString().Trim('-');
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(WorkloadCompatibilityReport))]
internal sealed partial class WorkloadJsonContext : JsonSerializerContext;
