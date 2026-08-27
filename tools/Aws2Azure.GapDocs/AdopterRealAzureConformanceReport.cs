using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class AdopterRealAzureConformanceReport
{
    public int SchemaVersion { get; set; } = 1;
    public string ArtifactKind { get; set; } = "adopter_real_azure_conformance_report";
    public string Verdict { get; set; } = string.Empty;
    public AdopterRealAzureCandidate Candidate { get; set; } = new();
    public AdopterRealAzureConformanceProvenance Provenance { get; set; } = new();
    public bool HasPositiveRealAzureEvidence { get; set; }
    public EvidenceTotals Summary { get; set; } = new();
    public List<string> TrxFiles { get; set; } = new();
    public List<AdopterRealAzureServiceReport> Services { get; set; } = new();
    public List<string> UnmappedTests { get; set; } = new();
}

public sealed class AdopterRealAzureCandidate
{
    public string Id { get; set; } = string.Empty;
    public string QualificationMode { get; set; } = "adopter_self_validation";
    public string? GitSha { get; set; }
    public string? ArtifactDigest { get; set; }
    public string? ConfigDigest { get; set; }
}

public sealed class AdopterRealAzureConformanceProvenance
{
    public string RunId { get; set; } = string.Empty;
    public string? RunUrl { get; set; }
    public int RunAttempt { get; set; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string ExecutionEngine { get; set; } = string.Empty;
    public string MatrixPath { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? BackendDescription { get; set; }
    public string? AzureSubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
}

public sealed class AdopterRealAzureServiceReport
{
    public string Service { get; set; } = string.Empty;
    public EvidenceTotals Summary { get; set; } = new();
    public List<ScenarioEvidence> Scenarios { get; set; } = new();
    public List<AdopterRealAzureOperationReport> Operations { get; set; } = new();
}

public sealed class AdopterRealAzureOperationReport
{
    public string Operation { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public bool EligibleForVerifiedRealAzure { get; set; }
    public List<string> Scenarios { get; set; } = new();
    public List<string> BlockingOutcomes { get; set; } = new();
}

public sealed class AdopterRealAzureConformanceReportMetadata
{
    public string CandidateId { get; set; } = string.Empty;
    public string? GitSha { get; set; }
    public string? ArtifactDigest { get; set; }
    public string? ConfigDigest { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string? RunUrl { get; set; }
    public int RunAttempt { get; set; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string ExecutionEngine { get; set; } =
        "caller-supplied TRX inputs parsed by generate-adopter-real-azure-report";
    public string MatrixPath { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? BackendDescription { get; set; } =
        "Ephemeral Azure resources provisioned from deploy/realazure/main.bicep";
    public string? AzureSubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
}

public static class AdopterRealAzureConformanceReportGenerator
{
    private const string DefaultRunUrl =
        "https://aws2azure.invalid/adopter-real-azure-self-validation";

    public static AdopterRealAzureConformanceReport Generate(
        RealAzureConformanceMatrix matrix,
        IReadOnlyList<TrxTestResult> trxResults,
        AdopterRealAzureConformanceReportMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(trxResults);
        ArgumentNullException.ThrowIfNull(metadata);

        if (string.IsNullOrWhiteSpace(metadata.CandidateId))
        {
            throw new ArgumentException("Candidate ID must not be empty.", nameof(metadata));
        }
        if (string.IsNullOrWhiteSpace(metadata.RunId))
        {
            throw new ArgumentException("Run ID must not be empty.", nameof(metadata));
        }
        if (metadata.RunAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "Run attempt must be positive.");
        }
        if (string.IsNullOrWhiteSpace(metadata.MatrixPath))
        {
            throw new ArgumentException("Matrix path must not be empty.", nameof(metadata));
        }

        var generatedAtUtc = metadata.GeneratedAtUtc == default
            ? DateTimeOffset.UtcNow
            : metadata.GeneratedAtUtc.ToUniversalTime();
        var evidence = ConformanceEvidenceGenerator.Generate(
            matrix,
            trxResults,
            metadata.RunId,
            string.IsNullOrWhiteSpace(metadata.RunUrl) ? DefaultRunUrl : metadata.RunUrl!,
            generatedAtUtc);

        var report = new AdopterRealAzureConformanceReport
        {
            Verdict = DetermineOverallVerdict(evidence.Services),
            Candidate = new AdopterRealAzureCandidate
            {
                Id = metadata.CandidateId,
                GitSha = NormalizeOptional(metadata.GitSha),
                ArtifactDigest = NormalizeOptional(metadata.ArtifactDigest),
                ConfigDigest = NormalizeOptional(metadata.ConfigDigest)
            },
            Provenance = new AdopterRealAzureConformanceProvenance
            {
                RunId = metadata.RunId,
                RunUrl = NormalizeOptional(metadata.RunUrl),
                RunAttempt = metadata.RunAttempt,
                GeneratedAtUtc = generatedAtUtc,
                ExecutionEngine = metadata.ExecutionEngine,
                MatrixPath = metadata.MatrixPath,
                Region = NormalizeOptional(metadata.Region),
                BackendDescription = NormalizeOptional(metadata.BackendDescription),
                AzureSubscriptionId = NormalizeOptional(metadata.AzureSubscriptionId),
                ResourceGroup = NormalizeOptional(metadata.ResourceGroup)
            },
            HasPositiveRealAzureEvidence = evidence.HasPositiveRealAzureEvidence,
            Summary = CloneTotals(evidence.Summary),
            TrxFiles = evidence.TrxFiles.ToList(),
            Services = evidence.Services.Select(CloneService).ToList(),
            UnmappedTests = evidence.UnmappedTests.ToList()
        };

        return report;
    }

    private static AdopterRealAzureServiceReport CloneService(ServiceEvidence service) => new()
    {
        Service = service.Service,
        Summary = CloneTotals(service.Summary),
        Scenarios = service.Scenarios.Select(CloneScenario).ToList(),
        Operations = service.Operations
            .Select(operation => new AdopterRealAzureOperationReport
            {
                Operation = operation.Operation,
                Verdict = DetermineOperationVerdict(operation),
                EligibleForVerifiedRealAzure = operation.EligibleForVerifiedRealAzure,
                Scenarios = operation.Scenarios.ToList(),
                BlockingOutcomes = operation.BlockingOutcomes.ToList()
            })
            .ToList()
    };

    private static ScenarioEvidence CloneScenario(ScenarioEvidence scenario) => new()
    {
        Id = scenario.Id,
        Priority = scenario.Priority,
        Category = scenario.Category,
        EvidenceSource = scenario.EvidenceSource,
        EstablishesVerification = scenario.EstablishesVerification,
        OptionalCoverage = scenario.OptionalCoverage,
        Description = scenario.Description,
        Operations = scenario.Operations.ToList(),
        Tests = scenario.Tests
            .Select(test => new TestEvidence
            {
                Identity = test.Identity,
                Outcome = test.Outcome,
                Executions = test.Executions,
                DurationMilliseconds = test.DurationMilliseconds
            })
            .ToList(),
        Outcome = scenario.Outcome,
        DurationMilliseconds = scenario.DurationMilliseconds
    };

    private static EvidenceTotals CloneTotals(EvidenceTotals totals) => new()
    {
        Passed = totals.Passed,
        Failed = totals.Failed,
        Skipped = totals.Skipped,
        NotRun = totals.NotRun,
        DurationMilliseconds = totals.DurationMilliseconds
    };

    private static string DetermineOverallVerdict(IEnumerable<ServiceEvidence> services)
    {
        var operationVerdicts = services
            .SelectMany(service => service.Operations)
            .Select(DetermineOperationVerdict)
            .ToList();
        if (operationVerdicts.Count == 0)
        {
            return "inconclusive";
        }
        if (operationVerdicts.Contains("failed", StringComparer.Ordinal))
        {
            return "failed";
        }
        return operationVerdicts.All(verdict => verdict == "passed")
            ? "passed"
            : "inconclusive";
    }

    private static string DetermineOperationVerdict(OperationEvidence operation)
    {
        if (operation.EligibleForVerifiedRealAzure)
        {
            return "passed";
        }

        foreach (var blockingOutcome in operation.BlockingOutcomes)
        {
            if (blockingOutcome.EndsWith(":failed", StringComparison.Ordinal))
            {
                return "failed";
            }
        }

        return "inconclusive";
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public static class AdopterRealAzureConformanceReportRenderer
{
    public static void RenderYaml(AdopterRealAzureConformanceReport report, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DateTimeOffsetYamlTypeConverter())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, serializer.Serialize(report));
    }

    private sealed class DateTimeOffsetYamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(DateTimeOffset);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            return DateTimeOffset.Parse(
                parser.Consume<Scalar>().Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
        }

        public void WriteYaml(
            IEmitter emitter,
            object? value,
            Type type,
            ObjectSerializer serializer)
        {
            var timestamp = ((DateTimeOffset)value!).ToUniversalTime();
            emitter.Emit(new Scalar(timestamp.ToString("O", CultureInfo.InvariantCulture)));
        }
    }
}
