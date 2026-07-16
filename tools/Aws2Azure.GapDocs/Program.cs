using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aws2Azure.GapDocs;

return Run(args);

static int Run(string[] args)
{
    try
    {
        var options = CommandLineOptions.Parse(args);
        var repoRoot = FindRepoRoot();
        var gapsRoot = Path.Combine(repoRoot, "docs", "gaps");
        var siteRoot = Path.Combine(repoRoot, "docs", "site");
        var generatedCode = Path.Combine(repoRoot, "src", "Aws2Azure.Core", "Generated", "CapabilityRegistry.g.cs");
        var matrixPath = options.MatrixPath is null
            ? Path.Combine(repoRoot, "docs", "testing", "real-azure-conformance.yaml")
            : Path.GetFullPath(options.MatrixPath);

        Console.WriteLine($"[gap-docs] loading YAMLs under {gapsRoot}");
        var docs = Loader.LoadAll(gapsRoot);
        var designDocs = Loader.LoadDesignDocs(gapsRoot);
        var matrix = ConformanceMatrixLoader.Load(matrixPath);
        var errors = new List<string>();
        errors.AddRange(Validator.Validate(docs));
        errors.AddRange(Validator.ValidateDesign(designDocs, docs));
        errors.AddRange(ConformanceMatrixValidator.Validate(matrix, docs));
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"[gap-docs] {errors.Count} validation error(s):");
            foreach (var error in errors) Console.Error.WriteLine("  - " + error);
            return 1;
        }

        Console.WriteLine($"[gap-docs] {docs.Count} operation(s), {designDocs.Count} service design doc(s), and real-Azure conformance matrix validated OK");
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
            evidence.TrxFiles = trxFiles.Select(path => Path.GetFileName(path)!).OrderBy(path => path, StringComparer.Ordinal).ToList();
            var outputRoot = options.EvidenceOutput is null
                ? Path.Combine(repoRoot, "TestResults", "real-azure-conformance")
                : Path.GetFullPath(options.EvidenceOutput);
            ConformanceEvidenceRenderer.Render(evidence, outputRoot);
            Console.WriteLine($"[gap-docs] ingested {trxResults.Count} result(s) from {trxFiles.Count} TRX file(s)");
            Console.WriteLine($"[gap-docs] real-Azure evidence written under {outputRoot}");
            return 0;
        }

        MarkdownRenderer.Render(docs, designDocs, siteRoot);
        Console.WriteLine($"[gap-docs] markdown written under {siteRoot}");
        CodeRenderer.Render(docs, generatedCode);
        Console.WriteLine($"[gap-docs] generated {generatedCode}");
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or FileNotFoundException
                                      or InvalidDataException)
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
            if (options.TrxPaths.Count == 0) throw new ArgumentException("--generate-evidence requires at least one --trx <path>.");
            if (string.IsNullOrWhiteSpace(options.RunId)) throw new ArgumentException("--generate-evidence requires --run-id <id>.");
            if (string.IsNullOrWhiteSpace(options.RunUrl)) throw new ArgumentException("--generate-evidence requires --run-url <url>.");
        }
        else if (options.TrxPaths.Count > 0
                 || options.RunId is not null
                 || options.RunUrl is not null
                 || options.EvidenceOutput is not null)
        {
            throw new ArgumentException("--trx, --run-id, --run-url, and --evidence-output require --generate-evidence.");
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
