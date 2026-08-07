namespace Aws2Azure.Conformance.Goldens;

public sealed class ConformanceProjectPathsTests
{
    [Fact]
    public void ProjectRoot_resolves_from_the_real_test_run_directory()
    {
        var root = ConformanceProjectPaths.ProjectRoot();

        Assert.True(File.Exists(Path.Combine(root, "Aws2Azure.Conformance.csproj")));
    }

    [Fact]
    public void ProjectRoot_resolves_from_a_sibling_test_projects_bin_directory()
    {
        // Aws2Azure.IntegrationTests references some of this project's
        // sources directly (not via ProjectReference to the whole project),
        // so when tests run from *its* bin/ directory,
        // Aws2Azure.Conformance.csproj is never an ancestor path — the walk
        // must fall back through the repo-root marker (aws2azure.slnx) and
        // descend into the known relative path instead. Simulate that shape
        // by starting the walk from a directory that mirrors
        // tests/Aws2Azure.IntegrationTests/bin/Debug/net10.0 relative to the
        // real repo root (it does not need to actually exist on disk).
        var actualRoot = ConformanceProjectPaths.ProjectRoot();
        var repoRoot = new DirectoryInfo(actualRoot).Parent!.Parent!.FullName;

        var simulatedSiblingBinDir = new DirectoryInfo(
            Path.Combine(repoRoot, "tests", "Aws2Azure.IntegrationTests", "bin", "Debug", "net10.0"));

        var resolved = ConformanceProjectPaths.ProjectRoot(simulatedSiblingBinDir);

        Assert.Equal(actualRoot, resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "Aws2Azure.Conformance.csproj")));
    }

    [Fact]
    public void ProjectRoot_throws_when_no_marker_is_found()
    {
        var detachedRoot = new DirectoryInfo(Path.GetTempPath());

        Assert.Throws<InvalidOperationException>(() => ConformanceProjectPaths.ProjectRoot(detachedRoot));
    }
}
