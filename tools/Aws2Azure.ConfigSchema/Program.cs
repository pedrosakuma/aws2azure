using Aws2Azure.ConfigSchema;

var repoRoot = FindRepoRoot();
var artifacts = new[]
{
    new GeneratedArtifact(
        ConfigSchemaGenerator.ArtifactRelativePath,
        ConfigSchemaGenerator.Generate()),
    new GeneratedArtifact(
        ConfigurationReferenceGenerator.ArtifactRelativePath,
        ConfigurationReferenceGenerator.Generate()),
};

if (args.Length == 1 && args[0].Equals("--check", StringComparison.Ordinal))
{
    var stale = artifacts
        .Where(artifact =>
        {
            var path = Path.Combine(repoRoot, artifact.RelativePath);
            return !File.Exists(path)
                || !File.ReadAllText(path).Equals(artifact.Content, StringComparison.Ordinal);
        })
        .Select(static artifact => artifact.RelativePath)
        .ToArray();
    if (stale.Length > 0)
    {
        Console.Error.WriteLine(
            $"[config-schema] {string.Join(", ", stale)} is out of date. " +
            "Run: dotnet run --project tools/Aws2Azure.ConfigSchema");
        return 1;
    }

    Console.WriteLine(
        $"[config-schema] {string.Join(", ", artifacts.Select(static artifact => artifact.RelativePath))} are current.");
    return 0;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/Aws2Azure.ConfigSchema [--check]");
    return 2;
}

foreach (var artifact in artifacts)
{
    var outputPath = Path.Combine(repoRoot, artifact.RelativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, artifact.Content);
    Console.WriteLine($"[config-schema] wrote {outputPath}");
}
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

internal sealed record GeneratedArtifact(string RelativePath, string Content);
