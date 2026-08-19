using Aws2Azure.DocsQuality;

if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
{
    Console.WriteLine(
        """
        Usage: dotnet run --project tools/Aws2Azure.DocsQuality

        Deterministically validates the hand-authored documentation corpus:

          - Every committed configuration example under
            docs/configuration/examples/*.json, and every fenced json/jsonc
            snippet in docs/**/*.md that looks like a full operator config
            document (top-level "services" or "bindings"), against the
            canonical config.schema.json.
          - Every dotnet project/test/publish command and eng/ or
            .github/scripts/ script path referenced in a fenced shell code
            block, proving the referenced repository path still exists.

        No network access is used. Exits 0 when clean, non-zero on any
        detected drift.
        """);
    return 0;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("docs-quality: unrecognized arguments. Use --help for usage.");
    return 2;
}

var repoRoot = FindRepoRoot();
var violations = new List<string>();

try
{
    violations.AddRange(ConfigExampleValidator.Validate(repoRoot));
}
catch (Exception exception) when (exception is IOException or InvalidOperationException)
{
    Console.Error.WriteLine($"docs-quality: {exception.Message}");
    return 2;
}

violations.AddRange(CommandSnippetValidator.Validate(repoRoot));

if (violations.Count > 0)
{
    Console.WriteLine("Violations:");
    foreach (var violation in violations.OrderBy(v => v, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {violation}");
    }
    Console.WriteLine();
    Console.WriteLine($"docs-quality: FAILED ({violations.Count} violation(s)).");
    return 1;
}

Console.WriteLine("docs-quality: clean.");
return 0;

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            return directory.FullName;
        }
        directory = directory.Parent;
    }
    throw new InvalidOperationException("Could not find repository root (aws2azure.slnx not found).");
}
