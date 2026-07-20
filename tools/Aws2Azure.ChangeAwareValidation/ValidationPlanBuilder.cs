namespace Aws2Azure.ChangeAwareValidation;

public static class ValidationPlanBuilder
{
    private static readonly GateDefinition[] GateDefinitions =
    [
        new("build", ["pwsh ./eng/validate.ps1 fast"], []),
        new("unit", ["pwsh ./eng/validate.ps1 unit"], []),
        new("conformance", ["pwsh ./eng/validate.ps1 conformance"], []),
        new("integration", ["pwsh ./eng/validate.ps1 integration"], ["run-integration"]),
        new("perf", ["pwsh ./eng/validate.ps1 perf"], ["run-perf"]),
        new("footprint", ["pwsh ./eng/validate.ps1 footprint"], ["run-footprint"]),
        new("real-azure", [], ["run-real-azure"])
    ];

    private static readonly string[] FailurePolicy =
    [
        "Compare a failing gate with main under the same runner, dependency, and configuration conditions before declaring a PR regression.",
        "Do not automatically bump a budget, baseline, qualification, or threshold after a failure.",
        "Do not automatically rerun a failing gate repeatedly; investigate first and allow at most one evidence-backed diagnostic rerun."
    ];

    public static ValidationPlan Build(
        IEnumerable<string> changedPaths,
        BaseComparison? comparison = null)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);

        var normalizedPaths = changedPaths
            .Select(NormalizePath)
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var decisions = GateDefinitions.ToDictionary(
            static definition => definition.Name,
            static _ => new MutableDecision(),
            StringComparer.Ordinal);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in normalizedPaths)
        {
            ClassifyPath(path, decisions, warnings);
        }

        if (decisions
            .Where(static pair => pair.Key != "build")
            .Any(static pair => pair.Value.Status == GateStatus.Required))
        {
            decisions["build"].Add(
                GateStatus.Required,
                "required validation gates depend on compiled Release outputs");
        }

        var gates = GateDefinitions
            .Select(definition =>
            {
                var decision = decisions[definition.Name];
                return new GateDecision(
                    definition.Name,
                    decision.Status.ToText(),
                    decision.Reasons.ToArray(),
                    definition.Commands,
                    definition.Labels);
            })
            .ToArray();
        var requiredLabels = gates
            .Where(static gate => gate.Status == "required")
            .SelectMany(static gate => gate.Labels)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ValidationPlan(
            1,
            comparison,
            normalizedPaths,
            gates,
            requiredLabels,
            warnings.ToArray(),
            FailurePolicy);
    }

    private static void ClassifyPath(
        string path,
        Dictionary<string, MutableDecision> decisions,
        SortedSet<string> warnings)
    {
        var isSource = path.StartsWith("src/", StringComparison.Ordinal);
        var isTool = path.StartsWith("tools/", StringComparison.Ordinal);
        var isTests = path.StartsWith("tests/", StringComparison.Ordinal);
        var isDotnet = HasAnySuffix(path, ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx");
        var isBuildGraph = IsBuildGraph(path);
        var isProductSource = isSource && isDotnet;
        var isModuleSource = path.StartsWith("src/Aws2Azure.Modules.", StringComparison.Ordinal);
        var isProtocol = ContainsAny(
            path,
            "/SigV4/",
            "/WireProtocol/",
            "/Codec/",
            "/Framing/",
            "/Xml/");
        var isAuthOrTransport = IsAuthOrTransport(path);
        var isHotPath = IsHotPath(path);
        var isStartupOrFootprint = IsStartupOrFootprint(path, isBuildGraph);
        var isUnitTest = path.StartsWith("tests/Aws2Azure.UnitTests/", StringComparison.Ordinal);
        var isConformanceTest = path.StartsWith("tests/Aws2Azure.Conformance/", StringComparison.Ordinal);
        var isIntegrationTest = path.StartsWith("tests/Aws2Azure.IntegrationTests/", StringComparison.Ordinal);
        var isPerfTest = path.StartsWith("tests/Aws2Azure.PerfTests/", StringComparison.Ordinal);
        var isFootprintTest = path.StartsWith("tests/Aws2Azure.FootprintTests/", StringComparison.Ordinal);
        var isRealAzureTest = isIntegrationTest &&
            ContainsAny(
                path,
                "/RealAzure/",
                "RealAzure",
                "/OperationalQualification/");
        var isWorkflow = path.StartsWith(".github/workflows/", StringComparison.Ordinal);
        var isRealAzureMatrix = path == "docs/testing/real-azure-conformance.yaml";
        var isQualificationPolicy =
            path.StartsWith("docs/workloads/qualification/", StringComparison.Ordinal);
        var isObservationPolicy =
            path.StartsWith("docs/workloads/observation/", StringComparison.Ordinal);
        var isApprovedRuntimeLedger =
            path.StartsWith("docs/workloads/approved-runtimes/", StringComparison.Ordinal);
        var isQualificationWorkflow =
            IsWorkflow(path, "qualification-real-azure.yml");
        var isWorkloadLoadWorkflow =
            IsWorkflow(path, "workload-load-real-azure.yml");
        var isRcObservationWorkflow =
            IsWorkflow(path, "rc-observation-real-azure.yml");
        var isWorkloadLoadProducer = isWorkloadLoadWorkflow
            || path == "deploy/realazure/secretsmanager-load.bicep"
            || path == "deploy/realazure/s3-load.bicep"
            || path == "deploy/realazure/dynamodb-load.bicep"
            || path
                == "tests/Aws2Azure.IntegrationTests/SecretsManager/SecretsManagerRealAzureLoadQualificationTests.cs"
            || path
                == "tests/Aws2Azure.IntegrationTests/S3/S3RealAzureLoadQualificationTests.cs"
            || path
                == "tests/Aws2Azure.IntegrationTests/DynamoDb/DynamoDbRealAzureLoadQualificationTests.cs"
            || path
                == "tests/Aws2Azure.IntegrationTests/OperationalQualification/RealAzureWorkloadLoadEvidence.cs";
        var isRcObservationProducer = isRcObservationWorkflow
            || isObservationPolicy
            || path
                == "tests/Aws2Azure.IntegrationTests/SecretsManager/SecretsManagerRealAzureRcObservationTests.cs"
            || path
                == "tests/Aws2Azure.IntegrationTests/S3/S3RealAzureRcObservationTests.cs"
            || path
                == "tests/Aws2Azure.IntegrationTests/OperationalQualification/RcObservationCaptureEvidence.cs";
        var isMicrobenchBaseline = path == "docs/perf/microbench-reference.json";
        var isValidationEntrypoint = path == "eng/validate.ps1";

        if (isDotnet || isBuildGraph || isValidationEntrypoint || IsWorkflow(path, "ci.yml"))
        {
            Require(decisions, "build", path, isBuildGraph ? "build graph changed" : "compiled code changed");
        }
        else
        {
            Optional(decisions, "build", path, "non-compiled repository change");
        }

        if (isProductSource ||
            (isTool && isDotnet) ||
            (isUnitTest && isDotnet) ||
            isBuildGraph ||
            isMicrobenchBaseline ||
            isValidationEntrypoint ||
            IsWorkflow(path, "ci.yml"))
        {
            Require(decisions, "unit", path, "unit-covered code or build graph changed");
        }
        else if (isTests)
        {
            Optional(decisions, "unit", path, "non-unit test surface changed");
        }

        if (isProtocol ||
            isModuleSource ||
            isConformanceTest ||
            isValidationEntrypoint ||
            IsWorkflow(path, "ci.yml") ||
            IsWorkflow(path, "conformance.yml"))
        {
            Require(decisions, "conformance", path, "AWS protocol or service behavior changed");
        }
        else if (isProductSource)
        {
            Optional(decisions, "conformance", path, "product code changed outside a mapped protocol surface");
        }

        if (isModuleSource ||
            isAuthOrTransport ||
            isIntegrationTest ||
            isValidationEntrypoint ||
            IsWorkflow(path, "integration.yml") ||
            path == "docker-compose.yml")
        {
            Require(decisions, "integration", path, "backend, authentication, or transport behavior changed");
        }
        else if (isProductSource)
        {
            Optional(decisions, "integration", path, "product code changed outside a mapped integration surface");
        }

        if (isHotPath ||
            isPerfTest ||
            isValidationEntrypoint ||
            IsWorkflow(path, "perf.yml") ||
            IsWorkflow(path, "perf-real-azure.yml") ||
            isQualificationWorkflow ||
            isWorkloadLoadProducer ||
            isRcObservationProducer ||
            isApprovedRuntimeLedger ||
            IsPerfBaseline(path))
        {
            Require(decisions, "perf", path, "request hot path or performance gate changed");
        }
        else if (isProductSource)
        {
            Optional(decisions, "perf", path, "product code changed outside a mapped hot path");
        }

        if (isStartupOrFootprint ||
            isFootprintTest ||
            isValidationEntrypoint ||
            IsWorkflow(path, "footprint.yml") ||
            IsWorkflow(path, "container.yml") ||
            IsWorkflow(path, "release.yml") ||
            IsFootprintBaseline(path))
        {
            Require(decisions, "footprint", path, "startup, packaging, build graph, or footprint gate changed");
        }
        else if (isProductSource)
        {
            Optional(decisions, "footprint", path, "product code changed outside a mapped footprint surface");
        }

        if (isAuthOrTransport ||
            isRealAzureTest ||
            isRealAzureMatrix ||
            IsWorkflow(path, "integration-real-azure.yml") ||
            IsWorkflow(path, "perf-real-azure.yml") ||
            isQualificationWorkflow ||
            isWorkloadLoadWorkflow ||
            isRcObservationProducer ||
            isApprovedRuntimeLedger ||
            IsWorkflow(path, "real-azure-reaper.yml") ||
            path.StartsWith("deploy/realazure/", StringComparison.Ordinal) ||
            isQualificationPolicy ||
            isObservationPolicy)
        {
            Require(decisions, "real-azure", path, "authentication or transport behavior needs live-Azure coverage");
        }
        else if (isModuleSource || isIntegrationTest)
        {
            Optional(decisions, "real-azure", path, "backend behavior may differ from emulators");
        }

        if (IsPerfBaseline(path) || IsFootprintBaseline(path))
        {
            warnings.Add(
                "A threshold or baseline changed. Require explicit justification plus comparable main and PR evidence; never normalize a failure by raising the threshold.");
        }

        if (path.StartsWith("docs/site/", StringComparison.Ordinal) ||
            path == "src/Aws2Azure.Core/Generated/CapabilityRegistry.g.cs")
        {
            warnings.Add(
                "Generated gap-doc output changed. Rebase onto current main, discard generated conflict edits, and regenerate from source; never hand-merge generated output.");
        }

        if (isWorkflow ||
            path.StartsWith("deploy/", StringComparison.Ordinal) ||
            path.StartsWith("docs/testing/", StringComparison.Ordinal) ||
            path.StartsWith("docs/perf/", StringComparison.Ordinal))
        {
            warnings.Add(
                "A shared coordination surface changed. Confirm coordinator ownership and single-writer assignment before merge.");
        }
    }

    private static bool IsBuildGraph(string path)
    {
        return path == "Directory.Build.props" ||
            path == "aws2azure.slnx" ||
            path == "global.json" ||
            path == "Dockerfile" ||
            path == ".dockerignore" ||
            path.EndsWith(".csproj", StringComparison.Ordinal) ||
            path.EndsWith(".props", StringComparison.Ordinal) ||
            path.EndsWith(".targets", StringComparison.Ordinal);
    }

    private static bool IsStartupOrFootprint(string path, bool isBuildGraph)
    {
        return isBuildGraph ||
            path == "src/Aws2Azure.Proxy/Program.cs" ||
            path == "src/Aws2Azure.Core/ServiceModuleRegistry.cs" ||
            path == "src/Aws2Azure.Core/IServiceModule.cs" ||
            path.StartsWith("src/Aws2Azure.Core/Modules/", StringComparison.Ordinal) ||
            path.StartsWith("docker/", StringComparison.Ordinal);
    }

    private static bool IsAuthOrTransport(string path)
    {
        return path.StartsWith("src/Aws2Azure.Core/SigV4/", StringComparison.Ordinal) ||
            path.StartsWith("src/Aws2Azure.Core/Azure/", StringComparison.Ordinal) ||
            path.StartsWith("src/Aws2Azure.Amqp/", StringComparison.Ordinal) ||
            path == "src/Aws2Azure.Proxy/Program.cs" ||
            path.Contains("Authenticator", StringComparison.OrdinalIgnoreCase) ||
            (path.StartsWith("src/Aws2Azure.Modules.", StringComparison.Ordinal) &&
             (path.EndsWith("Client.cs", StringComparison.Ordinal) ||
              path.EndsWith("Publisher.cs", StringComparison.Ordinal))) ||
            ContainsAny(
                path,
                "/Auth/",
                "/Transport/",
                "/Connection/",
                "/Amqp/",
                "/EventGrid/",
                "/EventHubsAmqp/",
                "/EventHubsRest/",
                "/Management/",
                "/Streaming/");
    }

    private static bool IsHotPath(string path)
    {
        return path.StartsWith("src/Aws2Azure.Modules.", StringComparison.Ordinal) ||
            path.StartsWith("src/Aws2Azure.Amqp/", StringComparison.Ordinal) ||
            path.StartsWith("src/Aws2Azure.Core/SigV4/", StringComparison.Ordinal) ||
            path.StartsWith("src/Aws2Azure.Core/Buffers/", StringComparison.Ordinal) ||
            path == "src/Aws2Azure.Core/Azure/AzureHttpClient.cs" ||
            path == "src/Aws2Azure.Proxy/Program.cs";
    }

    private static bool IsWorkflow(string path, string fileName)
    {
        return path == $".github/workflows/{fileName}";
    }

    private static bool IsPerfBaseline(string path)
    {
        return path is
            "docs/perf/baseline-reference.json" or
            "docs/perf/baseline-latest.json" or
            "docs/perf/baseline-latest.md" or
            "docs/perf/history.csv" or
            "docs/perf/microbench-reference.json";
    }

    private static bool IsFootprintBaseline(string path)
    {
        return path is
            "docs/perf/footprint-reference.json" or
            "docs/perf/footprint-latest.md" or
            "docs/perf/footprint-history.csv";
    }

    private static bool ContainsAny(string value, params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            if (value.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnySuffix(string value, params string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static void Require(
        Dictionary<string, MutableDecision> decisions,
        string gate,
        string path,
        string reason)
    {
        decisions[gate].Add(GateStatus.Required, $"{path}: {reason}");
    }

    private static void Optional(
        Dictionary<string, MutableDecision> decisions,
        string gate,
        string path,
        string reason)
    {
        decisions[gate].Add(GateStatus.Optional, $"{path}: {reason}");
    }

    private sealed record GateDefinition(string Name, string[] Commands, string[] Labels);

    private sealed class MutableDecision
    {
        public GateStatus Status { get; private set; }

        public SortedSet<string> Reasons { get; } = new(StringComparer.Ordinal);

        public void Add(GateStatus status, string reason)
        {
            if (status > Status)
            {
                Status = status;
            }

            Reasons.Add(reason);
        }
    }

    private enum GateStatus
    {
        NotApplicable,
        Optional,
        Required
    }

    private static string ToText(this GateStatus status)
    {
        return status switch
        {
            GateStatus.Required => "required",
            GateStatus.Optional => "optional",
            _ => "not-applicable"
        };
    }
}
