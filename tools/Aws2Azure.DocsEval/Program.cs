using Aws2Azure.DocsEval;

var repoRoot = FindRepoRoot();
var datasetPath = Path.Combine(repoRoot, "tools", "Aws2Azure.DocsEval", "Dataset", "retrieval-eval-dataset.json");

if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
{
    Console.WriteLine(
        """
        Usage: dotnet run --project tools/Aws2Azure.DocsEval

        Deterministically evaluates the retrieval-evaluation dataset
        (tools/Aws2Azure.DocsEval/Dataset/retrieval-eval-dataset.json) against
        the current repository state: workload-ga.json verdicts, gap-doc
        operation status, config.schema.json fields, generated operation
        reference pages, and cited documentation text. No network access or
        model credentials are used. Exits 0 when clean, non-zero on any
        detected drift.
        """);
    return 0;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("docs-eval: unrecognized arguments. Use --help for usage.");
    return 2;
}

try
{
    var dataset = Evaluator.LoadDataset(datasetPath);
    var result = Evaluator.Run(repoRoot, dataset);

    Console.WriteLine($"docs-eval: {result.PassedCases}/{result.TotalCases} cases passed.");

    if (result.Violations.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Violations:");
        foreach (var violation in result.Violations)
        {
            Console.WriteLine($"  [{violation.CaseId}] {violation.Message}");
        }
    }

    if (result.MaturityClaimViolations.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Uncited maturity claims:");
        foreach (var claim in result.MaturityClaimViolations)
        {
            Console.WriteLine($"  {claim}");
        }
    }

    if (!result.IsClean)
    {
        Console.WriteLine();
        Console.WriteLine("docs-eval: FAILED.");
        return 1;
    }

    Console.WriteLine("docs-eval: clean.");
    return 0;
}
catch (Exception exception) when (
    exception is IOException or InvalidDataException or InvalidOperationException)
{
    Console.Error.WriteLine($"docs-eval: {exception.Message}");
    return 2;
}

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
