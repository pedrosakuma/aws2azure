using System.Text.Json;
using Aws2Azure.Documentation;
using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.Documentation;

public sealed class DocumentationDiscoveryTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Committed_artifacts_match_deterministic_generation()
    {
        var manifest = DocumentationDiscoveryGenerator.Build(RepoRoot);

        Assert.Equal(
            File.ReadAllText(Path.Combine(
                RepoRoot,
                DocumentationDiscoveryGenerator.ManifestRelativePath)),
            DocumentationDiscoveryGenerator.RenderManifest(manifest));
        Assert.Equal(
            File.ReadAllText(Path.Combine(
                RepoRoot,
                DocumentationDiscoveryGenerator.LlmsRelativePath)),
            DocumentationDiscoveryGenerator.RenderLlms(manifest));
    }

    [Fact]
    public void Generation_is_byte_identical_across_runs()
    {
        var first = DocumentationDiscoveryGenerator.Build(RepoRoot);
        var second = DocumentationDiscoveryGenerator.Build(RepoRoot);

        Assert.Equal(
            DocumentationDiscoveryGenerator.RenderManifest(first),
            DocumentationDiscoveryGenerator.RenderManifest(second));
        Assert.Equal(
            DocumentationDiscoveryGenerator.RenderLlms(first),
            DocumentationDiscoveryGenerator.RenderLlms(second));
    }

    [Fact]
    public void Manifest_indexes_every_structured_source_and_stable_generated_artifact()
    {
        var manifest = DocumentationDiscoveryGenerator.Build(RepoRoot);
        var paths = manifest.Documents.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var structuredSources = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "docs"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".yaml" or ".json")
            .Select(path => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(structuredSources.Except(paths));
        Assert.Equal(manifest.Documents.Count, manifest.Documents.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal(manifest.Documents.Count, manifest.Documents.Select(entry => entry.Path).Distinct().Count());

        var operations = Loader.LoadAll(Path.Combine(RepoRoot, "docs", "gaps"));
        Assert.Equal(
            operations.Count,
            manifest.Documents.Count(entry => entry.Type == "operation-gap-source"));
        Assert.Equal(
            operations.Count,
            manifest.Documents.Count(entry => entry.Type == "operation-reference"));

        var designGapCount = Loader.LoadDesignDocs(Path.Combine(RepoRoot, "docs", "gaps"))
            .Sum(design => design.DesignGaps.Count);
        Assert.Equal(
            designGapCount,
            manifest.Documents.Count(entry => entry.Type == "design-gap-reference"));
    }

    [Fact]
    public void Current_verdict_wins_over_v1_release_history()
    {
        var manifest = DocumentationDiscoveryGenerator.Build(RepoRoot);
        var current = Assert.Single(
            manifest.Documents,
            entry => entry.Id == "workload-certification:current:machine");
        var release = Assert.Single(
            manifest.Documents,
            entry => entry.Id == "release:v1.0.0:notes");

        Assert.Equal("docs/site/workload-ga.json", current.Path);
        Assert.Equal("current", current.Authority);
        Assert.Equal("point-in-time", current.Freshness.Mode);
        Assert.Equal("docs/releases/v1.0.0.md", release.Path);
        Assert.Equal("historical", release.Authority);
        Assert.Equal("immutable", release.Freshness.Mode);
        Assert.Equal("live_workload_certification", manifest.AuthorityPrecedence[0].Source);
        Assert.Equal("release_notes", manifest.AuthorityPrecedence[3].Source);
        Assert.Equal("explanatory_guides", manifest.AuthorityPrecedence[4].Source);
    }

    [Fact]
    public void Retrieval_witnesses_locate_config_service_operation_and_profile_sources()
    {
        var documents = DocumentationDiscoveryGenerator.Build(RepoRoot)
            .Documents
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        Assert.Equal("config.schema.json", documents["configuration:schema"].Path);
        Assert.Equal(
            "docs/configuration-schema.md",
            documents["configuration:guide:configuration-schema"].Path);
        Assert.Equal(
            "docs/gaps/s3/GetObject.yaml",
            documents["operation:s3:getobject:source"].Path);
        Assert.Equal(
            "docs/site/operations/s3/getobject.md",
            documents["operation:s3:getobject:reference"].Path);
        Assert.Equal("s3", documents["operation:s3:getobject:source"].Service);
        Assert.Equal("GetObject", documents["operation:s3:getobject:source"].Operation);
        Assert.Equal(
            "docs/workloads/s3-basic-object-crud.yaml",
            documents["profile:s3-basic-object-crud:manifest"].Path);
        Assert.Equal(
            "docs/workloads/s3-basic-object-crud.md",
            documents["profile:s3-basic-object-crud:guide"].Path);
        Assert.Equal(
            "s3-basic-object-crud",
            documents["profile:s3-basic-object-crud:manifest"].Profile);
    }

    [Fact]
    public void Validation_rejects_duplicate_ids_missing_paths_and_coverage_gaps()
    {
        var manifest = DocumentationDiscoveryGenerator.Build(RepoRoot);
        manifest.Documents[1].Id = manifest.Documents[0].Id;
        manifest.Documents.Single(entry => entry.Id == "configuration:schema").Path =
            "docs/missing-config-schema.json";
        manifest.Documents.Remove(
            manifest.Documents.Single(entry => entry.Id == "operation:s3:getobject:reference"));

        var errors = DocumentationDiscoveryGenerator.Validate(RepoRoot, manifest);

        Assert.Contains(errors, error => error.Contains("duplicate document id", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("does not resolve", StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains(
                "docs/site/operations/s3/getobject.md",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_excludes_volatile_metadata_transient_paths_and_secret_values()
    {
        var manifest = DocumentationDiscoveryGenerator.Build(RepoRoot);
        var json = DocumentationDiscoveryGenerator.RenderManifest(manifest);

        Assert.DoesNotContain(RepoRoot, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"run_id\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"build_id\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"generated_at\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TestResults", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AKIA", json, StringComparison.Ordinal);
        Assert.All(manifest.Documents, entry =>
            Assert.Matches("^sha256:[0-9a-f]{64}$", entry.Revision));
    }

    [Fact]
    public void Llms_map_distinguishes_canonical_generated_historical_and_explanatory_sources()
    {
        var text = DocumentationDiscoveryGenerator.RenderLlms(
            DocumentationDiscoveryGenerator.Build(RepoRoot));

        var liveAt = text.IndexOf("Live workload certification", StringComparison.Ordinal);
        var manifestsAt = text.IndexOf("Versioned workload manifests", StringComparison.Ordinal);
        var gapsAt = text.IndexOf(
            "Gap YAML and generated operation/design-gap artifacts",
            StringComparison.Ordinal);
        var releasesAt = text.IndexOf("Immutable historical release notes", StringComparison.Ordinal);
        var guidesAt = text.IndexOf("Explanatory guides", StringComparison.Ordinal);

        Assert.True(liveAt < manifestsAt);
        Assert.True(manifestsAt < gapsAt);
        Assert.True(gapsAt < releasesAt);
        Assert.True(releasesAt < guidesAt);
        Assert.Contains("**Canonical/current:**", text, StringComparison.Ordinal);
        Assert.Contains("**Generated:**", text, StringComparison.Ordinal);
        Assert.Contains("**Historical:**", text, StringComparison.Ordinal);
        Assert.Contains("**Explanatory:**", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_symlink_to_external_tree_is_rejected_before_enumeration()
    {
        var isolatedRepo = CreateIsolatedRepository();
        var externalRoot = CreateTempDirectory("aws2azure-documentation-external");
        var link = Path.Combine(isolatedRepo, "docs", "gaps", "external-tree");
        try
        {
            File.WriteAllText(
                Path.Combine(externalRoot, "must-not-be-read.yaml"),
                "this is deliberately not a valid gap document");
            if (!TryCreateDirectorySymbolicLink(link, externalRoot))
            {
                return;
            }

            var exception = Assert.Throws<InvalidDataException>(() =>
                DocumentationDiscoveryGenerator.Build(isolatedRepo));

            Assert.Contains("symbolic link or reparse point", exception.Message);
            Assert.Contains("docs/gaps/external-tree", exception.Message);
            Assert.DoesNotContain("must-not-be-read", exception.Message);
        }
        finally
        {
            DeleteDirectoryLink(link);
            DeleteDirectory(isolatedRepo);
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void Canonical_file_below_symlinked_parent_is_rejected()
    {
        var isolatedRepo = CreateIsolatedRepository();
        var externalRoot = CreateTempDirectory("aws2azure-documentation-parent");
        var linkedParent = Path.Combine(isolatedRepo, "docs", "configuration", "examples");
        try
        {
            File.WriteAllText(
                Path.Combine(externalRoot, "external-example.json"),
                """{"external":true}""");
            Directory.Delete(linkedParent, recursive: true);
            if (!TryCreateDirectorySymbolicLink(linkedParent, externalRoot))
            {
                return;
            }

            var exception = Assert.Throws<InvalidDataException>(() =>
                DocumentationDiscoveryGenerator.Build(isolatedRepo));

            Assert.Contains("symbolic link or reparse point", exception.Message);
            Assert.Contains("docs/configuration/examples", exception.Message);
        }
        finally
        {
            DeleteDirectoryLink(linkedParent);
            DeleteDirectory(isolatedRepo);
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void Canonical_file_symlink_is_rejected_before_hashing()
    {
        var isolatedRepo = CreateIsolatedRepository();
        var externalRoot = CreateTempDirectory("aws2azure-documentation-file");
        var target = Path.Combine(externalRoot, "external-schema.json");
        var link = Path.Combine(isolatedRepo, "config.schema.json");
        try
        {
            File.WriteAllText(target, """{"external_bytes_must_not_enter_manifest":true}""");
            File.Delete(link);
            if (!TryCreateFileSymbolicLink(link, target))
            {
                return;
            }

            var exception = Assert.Throws<InvalidDataException>(() =>
                DocumentationDiscoveryGenerator.Build(isolatedRepo));

            Assert.Contains("symbolic link or reparse point", exception.Message);
            Assert.Contains("config.schema.json", exception.Message);
            Assert.DoesNotContain("external_bytes_must_not_enter_manifest", exception.Message);
        }
        finally
        {
            File.Delete(link);
            DeleteDirectory(isolatedRepo);
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public async Task Directory_symlink_loop_is_rejected_without_recursion()
    {
        var isolatedRepo = CreateIsolatedRepository();
        var loopRoot = Path.Combine(isolatedRepo, "docs", "gaps", "loop");
        var link = Path.Combine(loopRoot, "self");
        try
        {
            Directory.CreateDirectory(loopRoot);
            if (!TryCreateDirectorySymbolicLink(link, loopRoot))
            {
                return;
            }

            var build = Task.Run(() =>
                Record.Exception(() => DocumentationDiscoveryGenerator.Build(isolatedRepo)));

            var result = await build.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = Assert.IsType<InvalidDataException>(result);
            Assert.Contains("symbolic link or reparse point", exception.Message);
            Assert.Contains("docs/gaps/loop/self", exception.Message);
        }
        finally
        {
            DeleteDirectoryLink(link);
            DeleteDirectory(isolatedRepo);
        }
    }

    [Fact]
    public void Snapshot_rejects_file_swap_after_validation_before_open()
    {
        var isolatedRepo = CreateIsolatedRepository();
        var externalRoot = CreateTempDirectory("aws2azure-documentation-race-file");
        var target = Path.Combine(externalRoot, "external-schema.json");
        var canonical = Path.Combine(isolatedRepo, "config.schema.json");
        var probe = Path.Combine(isolatedRepo, "symlink-probe");
        try
        {
            const string externalContent = """{"external_race_bytes":true}""";
            File.WriteAllText(target, externalContent);
            if (!TryCreateFileSymbolicLink(probe, target))
            {
                return;
            }
            File.Delete(probe);

            var swapped = false;
            var hooks = new DocumentationDiscoveryGenerator.DocumentationIoHooks
            {
                AfterPathValidationBeforeIo = ioEvent =>
                {
                    if (swapped
                        || ioEvent.Operation
                        != DocumentationDiscoveryGenerator.DocumentationIoOperation.Read
                        || ioEvent.RelativePath != "config.schema.json")
                    {
                        return;
                    }

                    File.Delete(canonical);
                    File.CreateSymbolicLink(canonical, target);
                    swapped = true;
                },
            };

            var exception = Assert.Throws<InvalidDataException>(() =>
                DocumentationDiscoveryGenerator.Build(isolatedRepo, hooks));

            Assert.True(swapped);
            Assert.Contains("resolves outside the repository root", exception.Message);
            Assert.DoesNotContain(externalContent, exception.Message);
        }
        finally
        {
            File.Delete(probe);
            File.Delete(canonical);
            DeleteDirectory(isolatedRepo);
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void Snapshot_rejects_parent_swap_after_validation_before_open()
    {
        var isolatedRepo = CreateIsolatedRepository();
        var externalRoot = CreateTempDirectory("aws2azure-documentation-race-parent");
        var parent = Path.Combine(isolatedRepo, "docs", "configuration", "examples");
        var originalParent = parent + "-original";
        var targetRelativePath = Directory.EnumerateFiles(parent, "*.json")
            .Select(path => Path.GetRelativePath(isolatedRepo, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .First();
        var targetFileName = Path.GetFileName(targetRelativePath);
        var probe = Path.Combine(isolatedRepo, "directory-symlink-probe");
        try
        {
            File.WriteAllText(
                Path.Combine(externalRoot, targetFileName),
                """{"external_parent_race_bytes":true}""");
            if (!TryCreateDirectorySymbolicLink(probe, externalRoot))
            {
                return;
            }
            DeleteDirectoryLink(probe);

            var swapped = false;
            var hooks = new DocumentationDiscoveryGenerator.DocumentationIoHooks
            {
                AfterPathValidationBeforeIo = ioEvent =>
                {
                    if (swapped
                        || ioEvent.Operation
                        != DocumentationDiscoveryGenerator.DocumentationIoOperation.Read
                        || ioEvent.RelativePath != targetRelativePath)
                    {
                        return;
                    }

                    Directory.Move(parent, originalParent);
                    Directory.CreateSymbolicLink(parent, externalRoot);
                    swapped = true;
                },
            };

            var exception = Assert.Throws<InvalidDataException>(() =>
                DocumentationDiscoveryGenerator.Build(isolatedRepo, hooks));

            Assert.True(swapped);
            Assert.Contains("resolves outside the repository root", exception.Message);
        }
        finally
        {
            DeleteDirectoryLink(probe);
            DeleteDirectoryLink(parent);
            DeleteDirectory(isolatedRepo);
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void Output_handle_does_not_follow_leaf_swapped_after_validation()
    {
        var isolatedRepo = CreateIsolatedRepository();
        var externalRoot = CreateTempDirectory("aws2azure-documentation-race-output");
        var externalTarget = Path.Combine(externalRoot, "external-llms.txt");
        var output = Path.Combine(isolatedRepo, DocumentationDiscoveryGenerator.LlmsRelativePath);
        var probe = Path.Combine(isolatedRepo, "output-symlink-probe");
        try
        {
            const string externalContent = "external output must remain unchanged";
            const string generatedContent = "generated content\n";
            File.WriteAllText(externalTarget, externalContent);
            File.WriteAllText(output, "old generated content");
            if (!TryCreateFileSymbolicLink(probe, externalTarget))
            {
                return;
            }
            File.Delete(probe);

            var swapped = false;
            var hooks = new DocumentationDiscoveryGenerator.DocumentationIoHooks
            {
                AfterPathValidationBeforeIo = ioEvent =>
                {
                    if (swapped
                        || ioEvent.Operation
                        != DocumentationDiscoveryGenerator.DocumentationIoOperation.Write
                        || ioEvent.RelativePath != DocumentationDiscoveryGenerator.LlmsRelativePath)
                    {
                        return;
                    }

                    File.Delete(output);
                    File.CreateSymbolicLink(output, externalTarget);
                    swapped = true;
                },
            };

            var exception = Assert.Throws<InvalidDataException>(() =>
                DocumentationDiscoveryGenerator.WriteRepositoryFile(
                    isolatedRepo,
                    DocumentationDiscoveryGenerator.LlmsRelativePath,
                    System.Text.Encoding.UTF8.GetBytes(generatedContent),
                    hooks));

            Assert.True(swapped);
            Assert.Contains("symbolic link or reparse point", exception.Message);
            Assert.Equal(externalContent, File.ReadAllText(externalTarget));
            Assert.Equal(externalContent, File.ReadAllText(output));
            Assert.NotNull(new FileInfo(output).LinkTarget);
        }
        finally
        {
            File.Delete(probe);
            File.Delete(output);
            DeleteDirectory(isolatedRepo);
            DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void Snapshot_supports_repository_paths_with_a_symlinked_ancestor()
    {
        var container = CreateTempDirectory("aws2azure-documentation-ancestor");
        var isolatedRepo = CreateIsolatedRepository();
        var realParent = Path.Combine(container, "real");
        var aliasParent = Path.Combine(container, "alias");
        var movedRepo = Path.Combine(realParent, "repo");
        try
        {
            Directory.CreateDirectory(realParent);
            Directory.Move(isolatedRepo, movedRepo);
            isolatedRepo = movedRepo;
            if (!TryCreateDirectorySymbolicLink(aliasParent, realParent))
            {
                return;
            }

            var manifest = DocumentationDiscoveryGenerator.Build(
                Path.Combine(aliasParent, "repo"));

            Assert.NotEmpty(manifest.Documents);
        }
        finally
        {
            DeleteDirectoryLink(aliasParent);
            DeleteDirectory(container);
            DeleteDirectory(isolatedRepo);
        }
    }

    private static string CreateIsolatedRepository()
    {
        var isolatedRepo = CreateTempDirectory("aws2azure-documentation-repo");
        File.Copy(
            Path.Combine(RepoRoot, "aws2azure.slnx"),
            Path.Combine(isolatedRepo, "aws2azure.slnx"));

        var manifest = DocumentationDiscoveryGenerator.Build(RepoRoot);
        foreach (var relativePath in manifest.Documents
                     .Select(document => document.Path)
                     .Distinct(StringComparer.Ordinal))
        {
            var source = Path.Combine(
                RepoRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destination = Path.Combine(
                isolatedRepo,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        return isolatedRepo;
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateDirectorySymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (SymbolicLinksUnavailable(exception))
        {
            return false;
        }
    }

    private static bool TryCreateFileSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (SymbolicLinksUnavailable(exception))
        {
            return false;
        }
    }

    private static bool SymbolicLinksUnavailable(Exception exception) =>
        exception is UnauthorizedAccessException
            or PlatformNotSupportedException
        || OperatingSystem.IsWindows() && exception is IOException;

    private static void DeleteDirectoryLink(string path)
    {
        if (Directory.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
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
