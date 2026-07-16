using System;
using System.IO;
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

var validateOnly = args.Length == 1 && args[0] == "--validate";
if (args.Length > 0 && !validateOnly)
{
    Console.Error.WriteLine("Usage: Aws2Azure.GapDocs [--validate]");
    Console.Error.WriteLine(
        "       Aws2Azure.GapDocs check-workload <manifest.yaml> [--format markdown|json] [--output <path>] [--fail-on-blocked]");
    return 1;
}

Console.WriteLine($"[gap-docs] loading YAMLs under {gapsRoot}");
var docs = Loader.LoadAll(gapsRoot);
var designDocs = Loader.LoadDesignDocs(gapsRoot);
var migration = Loader.LoadRealAzureMigration(gapsRoot);
var errors = new System.Collections.Generic.List<string>();
errors.AddRange(Validator.Validate(docs, migration, DateOnly.FromDateTime(DateTime.UtcNow)));
errors.AddRange(Validator.ValidateDesign(designDocs, docs));
if (errors.Count > 0)
{
    Console.Error.WriteLine($"[gap-docs] {errors.Count} validation error(s):");
    foreach (var e in errors) Console.Error.WriteLine("  - " + e);
    return 1;
}
Console.WriteLine($"[gap-docs] {docs.Count} operation(s) and {designDocs.Count} service design doc(s) validated OK");

if (validateOnly)
{
    return 0;
}

MarkdownRenderer.Render(docs, designDocs, migration, siteRoot);
Console.WriteLine($"[gap-docs] markdown written under {siteRoot}");
CodeRenderer.Render(docs, generatedCode);
Console.WriteLine($"[gap-docs] generated {generatedCode}");
return 0;

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
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "aws2azure.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not locate repo root (aws2azure.slnx not found in any ancestor)");
}
