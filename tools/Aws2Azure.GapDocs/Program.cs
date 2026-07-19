using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Aws2Azure.GapDocs;
using YamlDotNet.Core;

var repoRoot = FindRepoRoot();
var gapsRoot = Path.Combine(repoRoot, "docs", "gaps");
var workloadsRoot = Path.Combine(repoRoot, "docs", "workloads");
var siteRoot = Path.Combine(repoRoot, "docs", "site");
var generatedCode = Path.Combine(repoRoot, "src", "Aws2Azure.Core", "Generated", "CapabilityRegistry.g.cs");

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && args[0] == "check-workload")
{
    return CheckWorkload(args[1..], gapsRoot);
}

if (args.Length > 0 && args[0] == "certify-workload")
{
    return CertifyWorkload(args[1..], repoRoot, gapsRoot);
}

if (args.Length > 0 && args[0] == "plan-conformance")
{
    return PlanConformance(
        args[1..],
        gapsRoot,
        Path.Combine(repoRoot, "docs", "testing", "real-azure-conformance.yaml"));
}

if (args.Length > 0 && args[0] == "validate-qualification")
{
    return ValidateQualification(args[1..]);
}

if (args.Length > 0 && args[0] == "generate-emulator-qualification")
{
    return GenerateEmulatorQualification(args[1..]);
}

if (args.Length > 0 && args[0] == "generate-real-azure-workload-qualification")
{
    return GenerateRealAzureWorkloadQualification(args[1..], repoRoot);
}

if (args.Length > 0 && args[0] == "generate-real-azure-load-qualification")
{
    return GenerateRealAzureLoadQualification(args[1..], repoRoot);
}

if (args.Length > 0 && args[0] == "export-approved-runtime")
{
    return ExportApprovedRuntime(args[1..], repoRoot);
}

if (args.Length > 0 && args[0] == "generate-rc-observation")
{
    return GenerateRcObservation(args[1..], repoRoot);
}

if (args.Length > 0 && args[0] == "validate-rc-observation")
{
    return ValidateRcObservation(args[1..]);
}

if (args.Length > 0 && args[0] == "validate-rc-calibration-report")
{
    return ValidateRcCalibrationReport(args[1..]);
}

return RunGapDocs(args, repoRoot, gapsRoot, workloadsRoot, siteRoot, generatedCode);

static int RunGapDocs(
    string[] args,
    string repoRoot,
    string gapsRoot,
    string workloadsRoot,
    string siteRoot,
    string generatedCode)
{
    try
    {
        var options = CommandLineOptions.Parse(args);
        var matrixPath = options.MatrixPath is null
            ? Path.Combine(repoRoot, "docs", "testing", "real-azure-conformance.yaml")
            : Path.GetFullPath(options.MatrixPath);

        Console.WriteLine($"[gap-docs] loading YAMLs under {gapsRoot}");
        var docs = Loader.LoadAll(gapsRoot);
        var designDocs = Loader.LoadDesignDocs(gapsRoot);
        var migration = Loader.LoadRealAzureMigration(gapsRoot);
        var matrix = ConformanceMatrixLoader.Load(matrixPath);
        var workloadManifests = WorkloadGaManifestLoader.LoadAll(workloadsRoot);
        var approvedRuntimes = ApprovedRuntimeLedgerLoader.LoadAll(
            Path.Combine(workloadsRoot, "approved-runtimes"));
        var observationPolicies = RcObservationPolicyLoader.LoadAll(
            Path.Combine(workloadsRoot, "observation"));
        var errors = new List<string>();
        errors.AddRange(Validator.Validate(docs, migration, DateOnly.FromDateTime(DateTime.UtcNow)));
        errors.AddRange(Validator.ValidateDesign(designDocs, docs));
        errors.AddRange(ConformanceMatrixValidator.Validate(matrix, docs));
        foreach (var manifest in workloadManifests)
        {
            errors.AddRange(WorkloadGaManifestValidator.Validate(manifest, docs, designDocs));
        }
        errors.AddRange(ApprovedRuntimeLedgerValidator.Validate(
            approvedRuntimes,
            workloadManifests,
            DateTimeOffset.UtcNow));
        errors.AddRange(RcObservationPolicyValidator.Validate(
            observationPolicies,
            workloadManifests,
            approvedRuntimes,
            workloadsRoot));
        if (errors.Count > 0)
        {
            WriteErrors("gap-doc validation", errors);
            return 1;
        }

        Console.WriteLine(
            $"[gap-docs] {docs.Count} operation(s), {designDocs.Count} service design doc(s), " +
            $"{approvedRuntimes.Count} approved-runtime record(s), " +
            $"{observationPolicies.Count} RC observation policy/policies, and real-Azure " +
            "conformance matrix validated OK");
        if (options.ValidateOnly)
        {
            return 0;
        }

        if (options.GenerateEvidence)
        {
            var selection = ConformanceMatrixSelector.Select(
                matrix,
                options.Service,
                options.Scenario);
            var trxFiles = ExpandTrxPaths(options.TrxPaths);
            var trxResults = TrxParser.ParseFiles(trxFiles);
            var evidence = ConformanceEvidenceGenerator.Generate(
                selection.Matrix,
                trxResults,
                options.RunId!,
                options.RunUrl!,
                selectedService: selection.Service,
                selectedScenario: selection.Scenario);
            evidence.TrxFiles = trxFiles
                .Select(path => Path.GetFileName(path)!)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            var outputRoot = options.EvidenceOutput is null
                ? Path.Combine(repoRoot, "TestResults", "real-azure-conformance")
                : Path.GetFullPath(options.EvidenceOutput);
            ConformanceEvidenceRenderer.Render(evidence, outputRoot);
            Console.WriteLine(
                $"[gap-docs] ingested {trxResults.Count} result(s) from {trxFiles.Count} TRX file(s)");
            Console.WriteLine($"[gap-docs] real-Azure evidence written under {outputRoot}");
            if (options.RequireRealAzure && !evidence.HasPositiveRealAzureEvidence)
            {
                Console.Error.WriteLine(
                    "[gap-docs] required positive real-Azure verification evidence was not produced.");
                return 3;
            }
            return 0;
        }

        MarkdownRenderer.Render(docs, designDocs, migration, siteRoot);
        Console.WriteLine($"[gap-docs] markdown written under {siteRoot}");
        var workloadReports = workloadManifests
            .Select(manifest => WorkloadGaEvaluator.Evaluate(
                manifest,
                docs,
                designDocs,
                repoRoot,
                DateOnly.FromDateTime(DateTime.UtcNow)))
            .ToList();
        WorkloadGaRenderer.RenderIndex(
            workloadReports,
            Path.Combine(siteRoot, "workload-ga.md"),
            Path.Combine(siteRoot, "workload-ga.json"));
        Console.WriteLine($"[gap-docs] workload GA verdicts written under {siteRoot}");
        CodeRenderer.Render(docs, generatedCode);
        Console.WriteLine($"[gap-docs] generated {generatedCode}");
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or UnauthorizedAccessException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int CertifyWorkload(string[] args, string repoRoot, string gapsRoot)
{
    if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Usage: Aws2Azure.GapDocs certify-workload <manifest.yaml> [--format markdown|json] [--output <path>] [--require-verdict blocked|conditional|candidate|ga]");
        return 1;
    }

    var manifestPath = args[0];
    var format = "markdown";
    string? outputPath = null;
    string? requiredVerdict = null;
    for (var index = 1; index < args.Length; index++)
    {
        switch (args[index])
        {
            case "--format" when index + 1 < args.Length:
                format = args[++index].ToLowerInvariant();
                break;
            case "--output" when index + 1 < args.Length:
                outputPath = args[++index];
                break;
            case "--require-verdict" when index + 1 < args.Length:
                requiredVerdict = args[++index].ToLowerInvariant();
                break;
            default:
                Console.Error.WriteLine($"Unknown or incomplete option '{args[index]}'.");
                return 1;
        }
    }
    if (format is not ("markdown" or "json"))
    {
        Console.Error.WriteLine($"Unknown format '{format}'; expected markdown or json.");
        return 1;
    }
    if (requiredVerdict is not null
        && requiredVerdict is not ("blocked" or "conditional" or "candidate" or "ga"))
    {
        Console.Error.WriteLine($"Unknown required verdict '{requiredVerdict}'.");
        return 1;
    }

    try
    {
        var docs = Loader.LoadAll(gapsRoot);
        var designDocs = Loader.LoadDesignDocs(gapsRoot);
        var migration = Loader.LoadRealAzureMigration(gapsRoot);
        var errors = new List<string>();
        errors.AddRange(Validator.Validate(docs, migration, DateOnly.FromDateTime(DateTime.UtcNow)));
        errors.AddRange(Validator.ValidateDesign(designDocs, docs));
        var manifest = WorkloadGaManifestLoader.Load(manifestPath);
        errors.AddRange(WorkloadGaManifestValidator.Validate(manifest, docs, designDocs));
        if (errors.Count > 0)
        {
            WriteErrors("workload GA certification", errors);
            return 1;
        }

        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            docs,
            designDocs,
            repoRoot,
            DateOnly.FromDateTime(DateTime.UtcNow));
        var content = format == "json"
            ? WorkloadGaRenderer.RenderJson(report) + Environment.NewLine
            : WorkloadGaRenderer.RenderMarkdown(report);
        if (outputPath is null)
        {
            Console.Write(content);
        }
        else
        {
            File.WriteAllText(outputPath, content);
        }
        return requiredVerdict is null || report.Verdict == requiredVerdict ? 0 : 3;
    }
    catch (Exception exception) when (exception is FileNotFoundException
                                      or DirectoryNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int CheckWorkload(string[] args, string gapsRoot)
{
    if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Usage: Aws2Azure.GapDocs check-workload <manifest.yaml> [--format markdown|json] [--output <path>] [--fail-on-blocked]");
        return 1;
    }

    var manifestPath = args[0];
    var format = "markdown";
    string? outputPath = null;
    var failOnBlocked = false;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--format" when i + 1 < args.Length
                                 && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                format = args[++i].ToLowerInvariant();
                break;
            case "--output" when i + 1 < args.Length
                                 && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                outputPath = args[++i];
                break;
            case "--fail-on-blocked":
                failOnBlocked = true;
                break;
            default:
                Console.Error.WriteLine($"Unknown or incomplete option '{args[i]}'.");
                return 1;
        }
    }

    if (format is not ("markdown" or "json"))
    {
        Console.Error.WriteLine($"Unknown format '{format}'; expected markdown or json.");
        return 1;
    }

    IReadOnlyList<OperationDoc> docs;
    IReadOnlyList<ServiceDesignDoc> designDocs;
    WorkloadManifest manifest;
    try
    {
        docs = Loader.LoadAll(gapsRoot);
        designDocs = Loader.LoadDesignDocs(gapsRoot);
        var migration = Loader.LoadRealAzureMigration(gapsRoot);
        var gapErrors = new List<string>();
        gapErrors.AddRange(Validator.Validate(docs, migration, DateOnly.FromDateTime(DateTime.UtcNow)));
        gapErrors.AddRange(Validator.ValidateDesign(designDocs, docs));
        if (gapErrors.Count > 0)
        {
            WriteErrors("gap-doc validation", gapErrors);
            return 1;
        }

        manifest = WorkloadManifestLoader.Load(manifestPath);
    }
    catch (YamlException exception)
    {
        Console.Error.WriteLine($"Invalid YAML: {exception.Message}");
        return 1;
    }
    catch (InvalidDataException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
    catch (UnauthorizedAccessException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }

    var manifestErrors = WorkloadManifestValidator.Validate(manifest, docs, designDocs);
    if (manifestErrors.Count > 0)
    {
        WriteErrors("workload manifest", manifestErrors);
        return 1;
    }

    var report = WorkloadCompatibilityEvaluator.Evaluate(manifest, docs, designDocs);
    var content = format == "json"
        ? WorkloadReportRenderer.RenderJson(report) + Environment.NewLine
        : WorkloadReportRenderer.RenderMarkdown(report);

    try
    {
        if (outputPath is null)
        {
            Console.Write(content);
        }
        else
        {
            File.WriteAllText(outputPath, content);
        }
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
    catch (UnauthorizedAccessException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }

    return failOnBlocked && report.Compatibility == "blocked" ? 2 : 0;
}

static int PlanConformance(string[] args, string gapsRoot, string defaultMatrixPath)
{
    string? matrixPath = null;
    string? service = null;
    string? scenario = null;
    string? outputPath = null;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--matrix" when i + 1 < args.Length
                                 && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                matrixPath = args[++i];
                break;
            case "--service" when i + 1 < args.Length
                                  && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                service = args[++i];
                break;
            case "--scenario" when i + 1 < args.Length
                                   && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                scenario = args[++i];
                break;
            case "--output" when i + 1 < args.Length
                                 && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                outputPath = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unknown or incomplete option '{args[i]}'.");
                return 1;
        }
    }

    try
    {
        var docs = Loader.LoadAll(gapsRoot);
        var resolvedMatrixPath = Path.GetFullPath(matrixPath ?? defaultMatrixPath);
        var matrix = ConformanceMatrixLoader.Load(resolvedMatrixPath);
        var errors = ConformanceMatrixValidator.Validate(matrix, docs);
        if (errors.Count > 0)
        {
            WriteErrors("real-Azure conformance matrix", errors);
            return 1;
        }

        var plan = ConformancePlanGenerator.Generate(matrix, service, scenario);
        var content = ConformancePlanRenderer.RenderJson(plan) + Environment.NewLine;
        if (outputPath is null)
        {
            Console.Write(content);
        }
        else
        {
            File.WriteAllText(outputPath, content);
        }
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or InvalidOperationException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int ValidateQualification(string[] args)
{
    if (args.Length != 1 || args[0].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Usage: Aws2Azure.GapDocs validate-qualification <artifact.yaml>");
        return 1;
    }

    try
    {
        var document = SloQualificationLoader.Load(args[0]);
        var errors = SloQualificationValidator.Validate(document, DateTimeOffset.UtcNow);
        if (errors.Count > 0)
        {
            WriteErrors("SLO qualification artifact", errors);
            return 1;
        }

        Console.WriteLine("[gap-docs] SLO qualification artifact validated OK");
        return 0;
    }
    catch (Exception exception) when (exception is FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int GenerateEmulatorQualification(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var maxRowAgeHours = 2;
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (option == "--max-row-age-hours")
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{option} requires a value.");
                return 1;
            }
            var raw = args[index];
            if (!int.TryParse(raw, out maxRowAgeHours))
            {
                Console.Error.WriteLine($"{option} requires an integer.");
                return 1;
            }
            continue;
        }
        if (option is "--reference"
            or "--latest"
            or "--output"
            or "--run-id"
            or "--run-url"
            or "--git-sha"
            or "--artifact-digest"
            or "--config-digest")
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{option} requires a value.");
                return 1;
            }
            values[option] = args[index];
            continue;
        }

        Console.Error.WriteLine($"Unknown option '{option}'.");
        return 1;
    }

    var required = new[]
    {
        "--reference",
        "--latest",
        "--output",
        "--run-id",
        "--run-url",
        "--git-sha",
        "--artifact-digest",
        "--config-digest"
    };
    var missing = required.Where(option => !values.ContainsKey(option)).ToList();
    if (missing.Count > 0)
    {
        Console.Error.WriteLine(
            "Missing required option(s): " + string.Join(", ", missing));
        return 1;
    }

    try
    {
        var document = EmulatorQualificationGenerator.Generate(
            values["--reference"],
            values["--latest"],
            new EmulatorQualificationMetadata
            {
                RunId = values["--run-id"],
                RunUrl = values["--run-url"],
                GitSha = values["--git-sha"],
                ArtifactDigest = values["--artifact-digest"],
                ConfigDigest = values["--config-digest"],
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                MaxRowAgeHours = maxRowAgeHours
            });
        var errors = SloQualificationValidator.Validate(document, DateTimeOffset.UtcNow);
        if (errors.Count > 0)
        {
            WriteErrors("generated emulator qualification", errors);
            return 1;
        }

        EmulatorQualificationGenerator.RenderYaml(document, values["--output"]);
        Console.WriteLine(
            $"[gap-docs] emulator qualification '{document.Verdict}' written to {values["--output"]}");
        return document.Verdict == "failed" ? 3 : 0;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or JsonException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int GenerateRealAzureWorkloadQualification(string[] args, string repoRoot)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var operations = new List<RealAzureWorkloadOperation>();
    var requiredScenarioIds = new List<string>();
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (option == "--operation")
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{option} requires a service:Operation value.");
                return 1;
            }
            var separator = args[index].IndexOf(':');
            if (separator <= 0 || separator == args[index].Length - 1)
            {
                Console.Error.WriteLine($"{option} requires a service:Operation value.");
                return 1;
            }
            operations.Add(new RealAzureWorkloadOperation
            {
                Service = args[index][..separator],
                Operation = args[index][(separator + 1)..]
            });
            continue;
        }
        if (option is "--evidence"
            or "--output"
            or "--manifest"
            or "--profile-id"
            or "--profile-version"
            or "--git-sha"
            or "--artifact-digest"
            or "--config-digest"
            or "--sealed-runtime-identity"
            or "--region"
            or "--backend-description"
            or "--run-attempt")
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{option} requires a value.");
                return 1;
            }
            values[option] = args[index];
            continue;
        }

        Console.Error.WriteLine($"Unknown option '{option}'.");
        return 1;
    }

    if (values.TryGetValue("--manifest", out var manifestPath))
    {
        if (values.ContainsKey("--profile-id")
            || values.ContainsKey("--profile-version")
            || operations.Count > 0)
        {
            Console.Error.WriteLine(
                "--manifest cannot be combined with --profile-id, --profile-version, or --operation.");
            return 1;
        }
        try
        {
            var manifest = WorkloadGaManifestLoader.Load(manifestPath);
            values["--profile-id"] = manifest.Id;
            values["--profile-version"] = manifest.Version.ToString(CultureInfo.InvariantCulture);
            requiredScenarioIds.AddRange(manifest.Evidence.RequiredScenarios);
            foreach (var operationReference in manifest.Operations)
            {
                if (!WorkloadManifestValidator.TryParseOperation(
                        operationReference,
                        out var service,
                        out var operation))
                {
                    Console.Error.WriteLine(
                        $"Manifest contains invalid operation reference '{operationReference}'.");
                    return 1;
                }
                operations.Add(new RealAzureWorkloadOperation
                {
                    Service = service,
                    Operation = operation,
                });
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or YamlException)
        {
            Console.Error.WriteLine("[gap-docs] " + exception.Message);
            return 2;
        }
    }

    var required = new[]
    {
        "--evidence",
        "--output",
        "--profile-id",
        "--profile-version",
        "--git-sha",
        "--artifact-digest",
        "--config-digest",
        "--region",
        "--backend-description"
    };
    var missing = required.Where(option => !values.ContainsKey(option)).ToList();
    if (missing.Count > 0 || operations.Count == 0)
    {
        if (operations.Count == 0)
        {
            missing.Add("--operation");
        }

        Console.Error.WriteLine(
            "Missing required option(s): " + string.Join(", ", missing));
        return 1;
    }
    if (!int.TryParse(values["--profile-version"], out var profileVersion))
    {
        Console.Error.WriteLine("--profile-version requires an integer.");
        return 1;
    }
    if (values.TryGetValue("--run-attempt", out var rawRunAttempt)
        && (!int.TryParse(rawRunAttempt, out var parsedRunAttempt) || parsedRunAttempt <= 0))
    {
        Console.Error.WriteLine("--run-attempt requires a positive integer.");
        return 1;
    }

    try
    {
        var document = RealAzureWorkloadQualificationGenerator.Generate(
            values["--evidence"],
            operations,
            new RealAzureWorkloadQualificationMetadata
            {
                ProfileId = values["--profile-id"],
                ProfileVersion = profileVersion,
                GitSha = values["--git-sha"],
                ArtifactDigest = values["--artifact-digest"],
                ConfigDigest = values["--config-digest"],
                Region = values["--region"],
                BackendDescription = values["--backend-description"],
                QualificationMode = values.ContainsKey("--sealed-runtime-identity")
                    ? "sealed"
                    : "source_validation",
                Runtime = values.TryGetValue(
                        "--sealed-runtime-identity",
                        out var sealedRuntimeIdentity)
                    ? SealedRuntimeEvidenceLoader.LoadRuntime(sealedRuntimeIdentity)
                    : null,
                RunAttempt = values.TryGetValue("--run-attempt", out var runAttemptValue)
                    && int.TryParse(runAttemptValue, out var runAttempt)
                    ? runAttempt
                    : 1,
                RequiredScenarioIds = requiredScenarioIds,
                GeneratedAtUtc = DateTimeOffset.UtcNow
            });
        var errors = SloQualificationValidator.Validate(document, DateTimeOffset.UtcNow);
        if (errors.Count > 0)
        {
            WriteErrors("generated real-Azure workload qualification", errors);
            return 1;
        }

        SloQualificationRenderer.RenderYaml(document, values["--output"]);
        Console.WriteLine(
            $"[gap-docs] real-Azure workload qualification '{document.Verdict}' " +
            $"written to {values["--output"]}");
        return document.Verdict switch
        {
            "blocked" => 3,
            "inconclusive" => 4,
            _ => 0
        };
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or JsonException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int GenerateRealAzureLoadQualification(string[] args, string repoRoot)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var evidencePaths = new List<string>();
    var evidenceSelectionPaths = new List<string>();
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (option == "--evidence")
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{option} requires a value.");
                return 1;
            }

            evidencePaths.Add(args[index]);
            continue;
        }
        if (option == "--evidence-selection")
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{option} requires a value.");
                return 1;
            }
            evidenceSelectionPaths.Add(args[index]);
            continue;
        }
        if (option is "--manifest"
            or "--candidate"
            or "--policy"
            or "--output"
            or "--trend-output"
            or "--run-id"
            or "--run-url"
            or "--run-attempt"
            or "--correctness-selection")
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{option} requires a value.");
                return 1;
            }
            values[option] = args[index];
            continue;
        }

        Console.Error.WriteLine($"Unknown option '{option}'.");
        return 1;
    }

    var required = new[]
    {
        "--manifest",
        "--candidate",
        "--policy",
        "--output",
        "--trend-output",
        "--run-id",
        "--run-url",
        "--run-attempt",
        "--correctness-selection"
    };
    var missing = required.Where(option => !values.ContainsKey(option)).ToList();
    if (evidencePaths.Count == 0)
    {
        missing.Add("--evidence");
    }
    if (evidenceSelectionPaths.Count != evidencePaths.Count)
    {
        missing.Add("one --evidence-selection per --evidence");
    }
    if (missing.Count > 0)
    {
        Console.Error.WriteLine("Missing required option(s): " + string.Join(", ", missing));
        return 1;
    }
    if (!int.TryParse(values["--run-attempt"], out var qualificationRunAttempt)
        || qualificationRunAttempt <= 0)
    {
        Console.Error.WriteLine("--run-attempt requires a positive integer.");
        return 1;
    }

    try
    {
        var manifest = WorkloadGaManifestLoader.Load(values["--manifest"]);
        var candidate = SloQualificationLoader.Load(values["--candidate"]);
        var candidateErrors = SloQualificationValidator.Validate(candidate, DateTimeOffset.UtcNow);
        if (candidateErrors.Count > 0)
        {
            WriteErrors("real-Azure correctness candidate", candidateErrors);
            return 1;
        }
        var policy = WorkloadQualificationPolicyLoader.Load(values["--policy"]);
        var evidence = evidencePaths
            .Select(RealAzureLoadQualificationGenerator.LoadEvidence)
            .ToList();
        var correctnessSelection = SealedRuntimeEvidenceLoader.LoadRunArtifact(
            values["--correctness-selection"]);
        var loadSelections = evidenceSelectionPaths
            .Select(SealedRuntimeEvidenceLoader.LoadRunArtifact)
            .ToList();
        ApprovedRuntimeRecord? priorRuntime = null;
        if (policy.Scenarios.Any(item => item.Id == "rollback"))
        {
            var runtimePath = Path.Combine(
                repoRoot,
                "docs",
                "workloads",
                "approved-runtimes",
                manifest.Id + ".yaml");
            priorRuntime = ApprovedRuntimeLedgerLoader.Load(runtimePath);
            var ledgerErrors = ApprovedRuntimeLedgerValidator.Validate(
                [priorRuntime],
                [manifest],
                DateTimeOffset.UtcNow);
            if (ledgerErrors.Count > 0)
            {
                WriteErrors("profile approved-runtime ledger", ledgerErrors);
                return 1;
            }
        }
        var document = RealAzureLoadQualificationGenerator.Generate(
            manifest,
            candidate,
            policy,
            evidence,
            new RealAzureLoadQualificationMetadata
            {
                RunId = values["--run-id"],
                RunUrl = values["--run-url"],
                RunAttempt = qualificationRunAttempt,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
            },
            priorRuntime,
            correctnessSelection,
            loadSelections);
        var errors = SloQualificationValidator.Validate(document, DateTimeOffset.UtcNow);
        if (errors.Count > 0)
        {
            WriteErrors("generated real-Azure load qualification", errors);
            return 1;
        }

        SloQualificationRenderer.RenderYaml(document, values["--output"]);
        RealAzureLoadQualificationGenerator.RenderTrend(evidence, values["--trend-output"]);
        Console.WriteLine(
            $"[gap-docs] real-Azure load qualification '{document.Verdict}' " +
            $"written to {values["--output"]}");
        return document.Verdict == "qualified" ? 0 : 4;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or JsonException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int ExportApprovedRuntime(string[] args, string repoRoot)
{
    string? profileId = null;
    string? outputPath = null;
    string? candidatePath = null;
    string? ledgerJsonPath = null;
    var rollbackTarget = false;
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (option == "--rollback-target")
        {
            rollbackTarget = true;
            continue;
        }
        if (option is not ("--profile" or "--output" or "--candidate"
            or "--ledger-json"))
        {
            Console.Error.WriteLine($"Unknown option '{option}'.");
            return 1;
        }
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{option} requires a value.");
            return 1;
        }
        if (option == "--profile")
        {
            profileId = args[index];
        }
        else if (option == "--output")
        {
            outputPath = args[index];
        }
        else
        {
            if (option == "--candidate")
            {
                candidatePath = args[index];
            }
            else
            {
                ledgerJsonPath = args[index];
            }
        }
    }

    if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(outputPath))
    {
        Console.Error.WriteLine(
            "export-approved-runtime requires --profile <id> --output <path> " +
            "[--rollback-target --candidate <candidate-runtime.json> " +
            "[--ledger-json <approved-runtime.json>]].");
        return 1;
    }
    if (rollbackTarget != !string.IsNullOrWhiteSpace(candidatePath))
    {
        Console.Error.WriteLine(
            "--rollback-target and --candidate must be supplied together.");
        return 1;
    }
    if (!string.IsNullOrWhiteSpace(ledgerJsonPath) && !rollbackTarget)
    {
        Console.Error.WriteLine(
            "--ledger-json is accepted only with --rollback-target.");
        return 1;
    }

    try
    {
        var workloadsRoot = Path.Combine(repoRoot, "docs", "workloads");
        var profiles = WorkloadGaManifestLoader.LoadAll(workloadsRoot);
        IReadOnlyList<ApprovedRuntimeRecord> records;
        ApprovedRuntimeRecord record;
        if (string.IsNullOrWhiteSpace(ledgerJsonPath))
        {
            records = ApprovedRuntimeLedgerLoader.LoadAll(
                Path.Combine(workloadsRoot, "approved-runtimes"));
            record = records.SingleOrDefault(
                item => item.Profile.Id.Equals(profileId, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"No approved-runtime ledger record exists for profile '{profileId}'.");
        }
        else
        {
            record = ApprovedRuntimeLedgerExport.Load(ledgerJsonPath).Record;
            records = [record];
        }
        var errors = ApprovedRuntimeLedgerValidator.Validate(
            records,
            profiles,
            DateTimeOffset.UtcNow);
        if (errors.Count > 0)
        {
            WriteErrors("approved-runtime ledger", errors);
            return 1;
        }

        if (!record.Profile.Id.Equals(profileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Approved-runtime export does not match profile '{profileId}'.");
        }
        ApprovedRuntimeLedgerExport export;
        if (rollbackTarget)
        {
            var candidate = SealedRuntimeEvidenceLoader.LoadRuntime(candidatePath!);
            SealedRuntimeEvidenceValidator.ValidateApprovedCandidate(
                candidate,
                record,
                DateTimeOffset.UtcNow);
            var trustedPrior = record.Qualification?.RollbackTarget
                ?? throw new InvalidDataException(
                    "Approved runtime does not contain a trusted rollback target.");
            SealedRuntimeEvidenceValidator.ValidateTrustedRollbackTarget(
                trustedPrior,
                record.Profile.Id,
                record.Profile.Version,
                record.Qualification!.RollbackTargetRuntimeDigest,
                DateTimeOffset.UtcNow);
            export = ApprovedRuntimeLedgerExport.CreateRollbackTarget(record);
        }
        else
        {
            export = ApprovedRuntimeLedgerExport.Create(record);
        }
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        File.WriteAllText(
            fullOutputPath,
            JsonSerializer.Serialize(
                export,
                ApprovedRuntimeLedgerJsonContext.Default.ApprovedRuntimeLedgerExport));
        Console.WriteLine(
            $"[gap-docs] approved runtime " +
            $"'{(rollbackTarget ? export.Record.Status + " rollback target" : record.Status)}' " +
            $"for '{profileId}' written to {fullOutputPath}");
        return 0;
    }
    catch (Exception exception) when (exception is FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or JsonException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int GenerateRcObservation(string[] args, string repoRoot)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "--capture",
        "--capture-selection",
        "--archive-selection",
        "--ghcr-selection",
        "--identity-selection",
        "--approved-runtime",
        "--candidate",
        "--prior",
        "--workload",
        "--qualification-policy",
        "--observation-policy",
        "--release-candidate-id",
        "--owner",
        "--output",
        "--binding-output",
    };
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (!allowed.Contains(option)
            || index + 1 >= args.Length
            || args[index + 1].StartsWith("--", StringComparison.Ordinal)
            || !values.TryAdd(option, args[++index]))
        {
            Console.Error.WriteLine($"Unknown, duplicate, or incomplete option '{option}'.");
            return 1;
        }
    }
    if (allowed.Any(option => !values.ContainsKey(option)))
    {
        Console.Error.WriteLine(
            "Usage: generate-rc-observation --capture <json> --capture-selection <json> " +
            "--archive-selection <json> --ghcr-selection <json> " +
            "--identity-selection <json> --approved-runtime <json> --candidate <json> " +
            "--prior <json> --workload <yaml> " +
            "--qualification-policy <yaml> --observation-policy <yaml> " +
            "--release-candidate-id <id> " +
            "--owner <actor> --output <yaml> --binding-output <json>");
        return 1;
    }

    try
    {
        var output = Path.GetFullPath(values["--output"]);
        var bindingOutput = Path.GetFullPath(values["--binding-output"]);
        if (File.Exists(output) || File.Exists(bindingOutput))
        {
            throw new IOException(
                "RC observation output and binding paths must not already exist.");
        }

        var capture = RcObservationCaptureLoader.Load(values["--capture"]);
        var selection = RcObservationCaptureLoader.LoadSelection(
            values["--capture-selection"]);
        var archiveSelection = RcObservationCaptureLoader.LoadArchiveSelection(
            values["--archive-selection"]);
        var ghcrSelection = RcObservationCaptureLoader.LoadGhcrSelection(
            values["--ghcr-selection"]);
        var identitySelection = RcObservationCaptureLoader.LoadIdentitySelection(
            values["--identity-selection"]);
        var candidate = SealedRuntimeEvidenceLoader.LoadRuntime(values["--candidate"]);
        var prior = SealedRuntimeEvidenceLoader.LoadRuntime(values["--prior"]);
        var approvedRuntime = ApprovedRuntimeLedgerExport.Load(
            values["--approved-runtime"]);
        var workload = WorkloadGaManifestLoader.Load(values["--workload"]);
        var qualification = WorkloadQualificationPolicyLoader.Load(
            values["--qualification-policy"]);
        var policy = RcObservationPolicyLoader.Load(values["--observation-policy"]);
        var generatedAt = DateTimeOffset.UtcNow;
        var result = RcObservationGenerator.Generate(
            capture,
            policy,
            qualification,
            workload,
            candidate,
            prior,
            approvedRuntime.Record,
            archiveSelection,
            ghcrSelection,
            identitySelection,
            selection,
            new RcObservationGenerationInput
            {
                ReleaseCandidateId = values["--release-candidate-id"],
                DecisionOwner = values["--owner"],
                CandidateIdentityDigest =
                    RcObservationRenderer.DigestFile(values["--candidate"]),
                PriorIdentityDigest = RcObservationRenderer.DigestFile(values["--prior"]),
                ApprovedRuntimeLedgerDigest = approvedRuntime.LedgerRecordDigest,
                WorkloadManifestDigest =
                    RcObservationRenderer.DigestFile(values["--workload"]),
                QualificationPolicyDigest =
                    RcObservationRenderer.DigestFile(values["--qualification-policy"]),
                ObservationPolicyDigest =
                    RcObservationRenderer.DigestFile(values["--observation-policy"]),
                GeneratedAtUtc = generatedAt,
            });
        RcObservationRenderer.Render(result.Evidence, output);
        RcObservationRenderer.RenderBinding(result.Binding, bindingOutput);

        var loaded = RcObservationLoader.Load(output);
        var errors = RcObservationValidator.Validate(loaded, result.Binding, generatedAt);
        if (errors.Count > 0)
        {
            WriteErrors("RC observation generation", errors);
            return 3;
        }
        Console.WriteLine(
            $"[gap-docs] RC observation '{loaded.Decision.Verdict}' for " +
            $"'{loaded.Profile.Id}' written to {output}");
        Console.WriteLine($"evidence_digest={loaded.EvidenceDigest}");
        Console.WriteLine($"verdict={loaded.Decision.Verdict}");
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or JsonException
                                      or UnauthorizedAccessException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int ValidateRcObservation(string[] args)
{
    string? evidencePath = null;
    string? bindingPath = null;
    string? expectedDigest = null;
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Incomplete option '{option}'.");
            return 1;
        }
        var value = args[index];
        switch (option)
        {
            case "--evidence" when evidencePath is null:
                evidencePath = value;
                break;
            case "--binding" when bindingPath is null:
                bindingPath = value;
                break;
            case "--expected-evidence-digest" when expectedDigest is null:
                expectedDigest = value;
                break;
            default:
                Console.Error.WriteLine($"Unknown or duplicate option '{option}'.");
                return 1;
        }
    }
    if (evidencePath is null || bindingPath is null || expectedDigest is null)
    {
        Console.Error.WriteLine(
            "Usage: validate-rc-observation --evidence <yaml> --binding <json> " +
            "--expected-evidence-digest <sha256>");
        return 1;
    }

    try
    {
        var evidence = RcObservationLoader.Load(evidencePath);
        var binding = RcObservationCaptureLoader.LoadBinding(bindingPath);
        if (binding.ExpectedEvidenceDigest != expectedDigest)
        {
            throw new InvalidDataException(
                "Trusted expected evidence digest does not match the observation binding.");
        }
        var errors = RcObservationValidator.Validate(
            evidence,
            binding,
            DateTimeOffset.UtcNow);
        if (errors.Count > 0)
        {
            WriteErrors("RC observation validation", errors);
            return 3;
        }
        Console.WriteLine(
            $"[gap-docs] RC observation '{evidence.Decision.Verdict}' validated " +
            $"for '{evidence.Profile.Id}' ({evidence.EvidenceDigest})");
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or JsonException
                                      or UnauthorizedAccessException
                                      or YamlException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static int ValidateRcCalibrationReport(string[] args)
{
    string? reportPath = null;
    for (var index = 0; index < args.Length; index++)
    {
        var option = args[index];
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Incomplete option '{option}'.");
            return 1;
        }
        var value = args[index];
        switch (option)
        {
            case "--report" when reportPath is null:
                reportPath = value;
                break;
            default:
                Console.Error.WriteLine($"Unknown or duplicate option '{option}'.");
                return 1;
        }
    }
    if (reportPath is null)
    {
        Console.Error.WriteLine("Usage: validate-rc-calibration-report --report <json>");
        return 1;
    }

    try
    {
        RcCalibrationReportValidator.ValidateFile(reportPath);
        Console.WriteLine("[gap-docs] non-promotable RC calibration report validated");
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException
                                      or IOException
                                      or JsonException
                                      or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("[gap-docs] " + exception.Message);
        return 2;
    }
}

static IReadOnlyList<string> ExpandTrxPaths(IReadOnlyList<string> paths)
{
    var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var value in paths)
    {
        var path = Path.GetFullPath(value);
        if (File.Exists(path))
        {
            if (!path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"TRX input is not a .trx file: {value}");
            }
            files.Add(path);
        }
        else if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.trx", SearchOption.AllDirectories))
            {
                files.Add(Path.GetFullPath(file));
            }
        }
        else
        {
            throw new FileNotFoundException("TRX input path not found", value);
        }
    }

    if (files.Count == 0)
    {
        throw new InvalidDataException("No .trx files were found in the supplied --trx inputs.");
    }

    return files.OrderBy(path => path, StringComparer.Ordinal).ToList();
}

static void WriteErrors(string subject, IReadOnlyList<string> errors)
{
    Console.Error.WriteLine($"{subject} has {errors.Count} error(s):");
    foreach (var error in errors)
    {
        Console.Error.WriteLine("  - " + error);
    }
}

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            return directory.FullName;
        }
        directory = directory.Parent;
    }
    throw new InvalidOperationException("Could not locate repo root (aws2azure.slnx not found in any ancestor)");
}

file sealed class CommandLineOptions
{
    public bool ValidateOnly { get; private set; }
    public bool GenerateEvidence { get; private set; }
    public string? MatrixPath { get; private set; }
    public List<string> TrxPaths { get; } = new();
    public string? RunId { get; private set; }
    public string? RunUrl { get; private set; }
    public string? EvidenceOutput { get; private set; }
    public string? Service { get; private set; }
    public string? Scenario { get; private set; }
    public bool RequireRealAzure { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--validate":
                    options.ValidateOnly = true;
                    break;
                case "--generate-evidence":
                    options.GenerateEvidence = true;
                    break;
                case "--matrix":
                    options.MatrixPath = ReadValue(args, ref index, "--matrix");
                    break;
                case "--trx":
                    options.TrxPaths.Add(ReadValue(args, ref index, "--trx"));
                    break;
                case "--run-id":
                    options.RunId = ReadValue(args, ref index, "--run-id");
                    break;
                case "--run-url":
                    options.RunUrl = ReadValue(args, ref index, "--run-url");
                    break;
                case "--evidence-output":
                    options.EvidenceOutput = ReadValue(args, ref index, "--evidence-output");
                    break;
                case "--service":
                    options.Service = ReadValue(args, ref index, "--service");
                    break;
                case "--scenario":
                    options.Scenario = ReadValue(args, ref index, "--scenario");
                    break;
                case "--require-real-azure":
                    options.RequireRealAzure = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (options.ValidateOnly && options.GenerateEvidence)
        {
            throw new ArgumentException("--validate and --generate-evidence cannot be combined.");
        }
        if (options.GenerateEvidence)
        {
            if (options.TrxPaths.Count == 0)
            {
                throw new ArgumentException("--generate-evidence requires at least one --trx <path>.");
            }
            if (string.IsNullOrWhiteSpace(options.RunId))
            {
                throw new ArgumentException("--generate-evidence requires --run-id <id>.");
            }
            if (string.IsNullOrWhiteSpace(options.RunUrl))
            {
                throw new ArgumentException("--generate-evidence requires --run-url <url>.");
            }
        }
        else if (options.TrxPaths.Count > 0
                 || options.RunId is not null
                 || options.RunUrl is not null
                 || options.EvidenceOutput is not null
                 || options.Service is not null
                 || options.Scenario is not null
                 || options.RequireRealAzure)
        {
            throw new ArgumentException(
                "--trx, --run-id, --run-url, --evidence-output, --service, --scenario, and " +
                "--require-real-azure require --generate-evidence.");
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        return args[index];
    }
}
