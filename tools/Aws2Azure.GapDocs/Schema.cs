using System.Collections.Generic;

namespace Aws2Azure.GapDocs;

public sealed class OperationDoc
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string AzureEquivalent { get; set; } = string.Empty;
    public string Status { get; set; } = "stub";
    public List<SubFeature> SubFeatures { get; set; } = new();
    public List<string> BehaviorDifferences { get; set; } = new();
    public List<string> References { get; set; } = new();

    public RealAzureVerification? VerifiedRealAzure { get; set; }

    // Provenance — set by loader.
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class SubFeature
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "unsupported";
    public string Notes { get; set; } = string.Empty;
    public string Gap { get; set; } = string.Empty;
    public string Workaround { get; set; } = string.Empty;

    public RealAzureVerification? VerifiedRealAzure { get; set; }
}

public sealed class RealAzureVerification
{
    public string Date { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string WorkflowRun { get; set; } = string.Empty;
}

public sealed class RealAzureMigrationDoc
{
    public List<RealAzureMigrationService> Services { get; set; } = new();
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class RealAzureMigrationService
{
    public string Service { get; set; } = string.Empty;
    public string TrackingIssue { get; set; } = string.Empty;
    public string ExpiresOn { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
}

/// <summary>
/// A cross-cutting, architectural gap for a whole service — the kind of
/// limitation that does not map to a single operation (e.g. "no cross-partition
/// transactions", "eventual consistency", "no server-side IAM"). Authored in
/// <c>docs/gaps/&lt;service&gt;/_design.yaml</c> and aggregated into
/// <c>docs/site/design-gaps.md</c>. Kept separate from <see cref="OperationDoc"/>
/// so the per-operation coverage matrix and the design-level story each stay
/// readable on their own (drill-down, not one overwhelming page).
/// </summary>
public sealed class ServiceDesignDoc
{
    public string Service { get; set; } = string.Empty;
    public List<DesignGap> DesignGaps { get; set; } = new();
    public List<WorkloadPattern> WorkloadPatterns { get; set; } = new();

    // Provenance — set by loader.
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class DesignGap
{
    public string Area { get; set; } = string.Empty;
    public string Status { get; set; } = "by_design";
    public string Summary { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string Workaround { get; set; } = string.Empty;
    public List<string> References { get; set; } = new();
}

/// <summary>
/// A representative adoption pattern composed from operation docs and design
/// gaps. Authored beside the service design gaps so generated compatibility
/// guidance cannot drift from the gap-doc source of truth.
/// </summary>
public sealed class WorkloadPattern
{
    public string Name { get; set; } = string.Empty;
    public string Compatibility { get; set; } = "conditional";
    public string Summary { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
    public List<string> DesignGaps { get; set; } = new();
    public string Guidance { get; set; } = string.Empty;
}

public static class StatusValues
{
    public static readonly HashSet<string> Operation = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "implemented", "partial", "stub", "unsupported"
    };
    public static readonly HashSet<string> SubFeature = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "implemented", "partial", "unsupported"
    };
    // Design gaps describe intent, not implementation completeness:
    //   by_design   — a deliberate, permanent divergence (locked decision)
    //   partial     — partially bridged; caveats apply
    //   unsupported — no Azure equivalent; surfaced but not translated
    //   planned     — a known gap with intended future work
    public static readonly HashSet<string> DesignGap = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "by_design", "partial", "unsupported", "planned"
    };
    public static readonly HashSet<string> WorkloadCompatibility = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "supported", "conditional", "blocked"
    };
}
