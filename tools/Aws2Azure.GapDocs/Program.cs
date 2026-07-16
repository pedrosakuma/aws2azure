using System;
using System.Collections.Generic;
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
    return GenerateRealAzureWorkloadQualification(args[1..]);
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
        var errors = new List<string>();
        errors.AddRange(Validator.Validate(docs, migration, DateOnly.FromDateTime(DateTime.UtcNow)));
        errors.AddRange(Validator.ValidateDesign(designDocs, docs));
        errors.AddRange(ConformanceMatrixValidator.Validate(matrix, docs));
        foreach (var manifest in workloadManifests)
        {
            errors.AddRange(WorkloadGaManifestValidator.Validate(manifest, docs, designDocs));
        }
        if (errors.Count > 0)
        {
            WriteErrors("gap-doc validation", errors);
            return 1;
        }

        Console.WriteLine(
            $"[gap-docs] {docs.Count} operation(s), {designDocs.Count} service design doc(s), " +
            "and real-Azure conformance matrix validated OK");
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

static int GenerateRealAzureWorkloadQualification(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var operations = new List<RealAzureWorkloadOperation>();
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
            or "--profile-id"
            or "--profile-version"
            or "--git-sha"
            or "--artifact-digest"
            or "--config-digest"
            or "--region"
            or "--backend-description")
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
