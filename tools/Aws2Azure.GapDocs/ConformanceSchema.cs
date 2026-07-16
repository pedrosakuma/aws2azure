using System.Collections.Generic;

namespace Aws2Azure.GapDocs;

public sealed class RealAzureConformanceMatrix
{
    public int SchemaVersion { get; set; }
    public List<RealAzureService> Services { get; set; } = new();
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class RealAzureService
{
    public string Service { get; set; } = string.Empty;
    public List<RealAzureScenario> Scenarios { get; set; } = new();
}

public sealed class RealAzureScenario
{
    public string Id { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public bool? EstablishesVerification { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
    public List<string> Tests { get; set; } = new();
}

public static class RealAzureConformanceValues
{
    public static readonly HashSet<string> RegisteredServices = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "s3", "dynamodb", "sqs", "kinesis", "sns", "secretsmanager"
    };

    public static readonly HashSet<string> Priorities = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "p0", "p1", "p2"
    };

    public static readonly HashSet<string> Categories = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "core", "read", "write", "list", "pagination", "batch",
        "throttling", "timeout", "service_unavailable", "invalid_credentials",
        "cancellation", "concurrency"
    };

    public static readonly HashSet<string> EvidenceSources = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "real_azure", "deterministic"
    };
}
