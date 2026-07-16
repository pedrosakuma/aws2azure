using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Aws2Azure.GapDocs;
using YamlDotNet.Core;

var repoRoot = FindRepoRoot();
var gapsRoot = Path.Combine(repoRoot, "docs", "gaps");
var siteRoot = Path.Combine(repoRoot, "docs", "site");
var generatedCode = Path.Combine(repoRoot, "src", "Aws2Azure.Core", "Generated", "CapabilityRegistry.g.cs");

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && args[0] == "check-workload")
{
    return CheckWorkload(args[1..], gapsRoot);
}

return RunGapDocs(args, repoRoot, gapsRoot, siteRoot, generatedCode);

static int RunGapDocs(
    string[] args,
    string repoRoot,
    string gapsRoot,
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
        var errors = new List<string>();
        errors.AddRange(Validator.Validate(docs, migration, DateOnly.FromDateTime(DateTime.UtcNow)));
        errors.AddRange(Validator.ValidateDesign(designDocs, docs));
        errors.AddRange(ConformanceMatrixValidator.Validate(matrix, docs));
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
            var trxFiles = ExpandTrxPaths(options.TrxPaths);
            var trxResults = TrxParser.ParseFiles(trxFiles);
            var evidence = ConformanceEvidenceGenerator.Generate(
                matrix,
                trxResults,
                options.RunId!,
                options.RunUrl!);
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
            return 0;
        }

        MarkdownRenderer.Render(docs, designDocs, migration, siteRoot);
        Console.WriteLine($"[gap-docs] markdown written under {siteRoot}");
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
                 || options.EvidenceOutput is not null)
        {
            throw new ArgumentException(
                "--trx, --run-id, --run-url, and --evidence-output require --generate-evidence.");
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
