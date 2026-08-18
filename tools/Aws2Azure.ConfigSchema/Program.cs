using Aws2Azure.ConfigSchema;

var repoRoot = FindRepoRoot();
var outputPath = Path.Combine(repoRoot, ConfigSchemaGenerator.ArtifactRelativePath);
var generated = ConfigSchemaGenerator.Generate();

if (args.Length == 1 && args[0].Equals("--check", StringComparison.Ordinal))
{
    if (!File.Exists(outputPath)
        || !File.ReadAllText(outputPath).Equals(generated, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"[config-schema] {ConfigSchemaGenerator.ArtifactRelativePath} is out of date. " +
            "Run: dotnet run --project tools/Aws2Azure.ConfigSchema");
        return 1;
    }

    Console.WriteLine($"[config-schema] {ConfigSchemaGenerator.ArtifactRelativePath} is current.");
    return 0;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/Aws2Azure.ConfigSchema [--check]");
    return 2;
}

File.WriteAllText(outputPath, generated);
Console.WriteLine($"[config-schema] wrote {outputPath}");
return 0;

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
            && File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Could not find the aws2azure repository root.");
}
