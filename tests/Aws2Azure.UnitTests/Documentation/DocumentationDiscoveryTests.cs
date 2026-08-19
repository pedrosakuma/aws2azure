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
