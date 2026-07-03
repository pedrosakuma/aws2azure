using System;
using System.IO;
using Aws2Azure.GapDocs;

var repoRoot = FindRepoRoot();
var gapsRoot = Path.Combine(repoRoot, "docs", "gaps");
var siteRoot = Path.Combine(repoRoot, "docs", "site");
var generatedCode = Path.Combine(repoRoot, "src", "Aws2Azure.Core", "Generated", "CapabilityRegistry.g.cs");

var validateOnly = false;
foreach (var a in args)
{
    if (a == "--validate") validateOnly = true;
}

Console.WriteLine($"[gap-docs] loading YAMLs under {gapsRoot}");
var docs = Loader.LoadAll(gapsRoot);
var designDocs = Loader.LoadDesignDocs(gapsRoot);
var errors = new System.Collections.Generic.List<string>();
errors.AddRange(Validator.Validate(docs));
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

MarkdownRenderer.Render(docs, designDocs, siteRoot);
Console.WriteLine($"[gap-docs] markdown written under {siteRoot}");
CodeRenderer.Render(docs, generatedCode);
Console.WriteLine($"[gap-docs] generated {generatedCode}");
return 0;

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
