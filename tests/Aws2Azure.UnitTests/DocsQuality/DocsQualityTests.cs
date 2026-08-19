using Aws2Azure.DocsQuality;

namespace Aws2Azure.UnitTests.DocsQuality;

public sealed class DocsQualityTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Live_documentation_corpus_is_clean()
    {
        var violations = ConfigExampleValidator.Validate(RepoRoot)
            .Concat(CommandSnippetValidator.Validate(RepoRoot))
            .ToList();

        Assert.True(violations.Count == 0, "Violations: " + string.Join("; ", violations));
    }

    [Fact]
    public void Config_example_validator_flags_a_fabricated_configuration_field()
    {
        var directory = CreateTempDirectory();
        try
        {
            var repoRoot = Path.Combine(directory, "repo");
            var examplesDir = Path.Combine(repoRoot, "docs", "configuration", "examples");
            Directory.CreateDirectory(examplesDir);
            File.Copy(
                Path.Combine(RepoRoot, "config.schema.json"),
                Path.Combine(repoRoot, "config.schema.json"));
            File.WriteAllText(
                Path.Combine(examplesDir, "broken.json"),
                """
                {
                  "services": { "s3": { "enabled": true, "thisFieldDoesNotExist": true } },
                  "bindings": [
                    {
                      "aws": { "accessKeyId": "AKIADEVEXAMPLE", "secretAccessKey": "dev-secret" },
                      "azure": {
                        "s3": {
                          "kind": "blob",
                          "target": { "accountName": "devstoreaccount1" },
                          "auth": { "mode": "sharedKey", "key": "..." }
                        }
                      }
                    }
                  ]
                }
                """);

            var violations = ConfigExampleValidator.Validate(repoRoot);

            Assert.Contains(violations, v => v.Contains("broken.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Config_example_validator_passes_a_schema_valid_example()
    {
        var directory = CreateTempDirectory();
        try
        {
            var repoRoot = Path.Combine(directory, "repo");
            var examplesDir = Path.Combine(repoRoot, "docs", "configuration", "examples");
            Directory.CreateDirectory(examplesDir);
            File.Copy(
                Path.Combine(RepoRoot, "config.schema.json"),
                Path.Combine(repoRoot, "config.schema.json"));
            File.Copy(
                Path.Combine(RepoRoot, "docs", "configuration", "examples", "blob-shared-key.json"),
                Path.Combine(examplesDir, "blob-shared-key.json"));

            var violations = ConfigExampleValidator.Validate(repoRoot);

            Assert.Empty(violations);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Command_snippet_validator_flags_a_broken_dotnet_project_reference()
    {
        var directory = CreateTempDirectory();
        try
        {
            var repoRoot = Path.Combine(directory, "repo");
            var docsDir = Path.Combine(repoRoot, "docs");
            Directory.CreateDirectory(docsDir);
            File.WriteAllText(
                Path.Combine(docsDir, "example.md"),
                """
                # Example

                ```bash
                dotnet run --project tools/Aws2Azure.DoesNotExist
                ```
                """);

            var violations = CommandSnippetValidator.Validate(repoRoot);

            Assert.Contains(violations, v => v.Contains("tools/Aws2Azure.DoesNotExist", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Command_snippet_validator_ignores_placeholders_and_passes_real_references()
    {
        var directory = CreateTempDirectory();
        try
        {
            var repoRoot = Path.Combine(directory, "repo");
            var docsDir = Path.Combine(repoRoot, "docs");
            var toolDir = Path.Combine(repoRoot, "tools", "Real.Tool");
            Directory.CreateDirectory(docsDir);
            Directory.CreateDirectory(toolDir);
            File.WriteAllText(Path.Combine(toolDir, "Real.Tool.csproj"), "<Project />");
            File.WriteAllText(
                Path.Combine(docsDir, "example.md"),
                """
                # Example

                ```bash
                dotnet run --project tools/Real.Tool -- certify-workload docs/gaps/<service>/<Operation>.yaml
                ```
                """);

            var violations = CommandSnippetValidator.Validate(repoRoot);

            Assert.Empty(violations);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Fenced_code_block_extractor_handles_indented_mkdocs_tab_fences()
    {
        var text = """
            # Doc

            === "Bash"

                ```bash
                echo hi
                ```
            """;

        var blocks = FencedCodeBlockExtractor.Extract(text);

        Assert.Single(blocks);
        Assert.Equal("bash", blocks[0].Language);
        Assert.Contains("echo hi", blocks[0].Content);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aws2azure-docsquality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepoRoot()
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
        throw new InvalidOperationException("Could not find repository root.");
    }
}
