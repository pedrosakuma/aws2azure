using Aws2Azure.GapDocs;
using System.Text.RegularExpressions;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class MarkdownRendererTests
{
    [Fact]
    public void Render_writes_granular_pages_with_stable_identifiers_and_compatibility_anchors()
    {
        var operation = Operation("PutObject");
        operation.SubFeatures.Add(new SubFeature
        {
            Name = "Content-Type / metadata",
            Status = "partial",
            Disposition = "by_design",
            Notes = "Metadata is translated.",
            Gap = "Some headers differ.",
            Workaround = "Use supported headers."
        });
        var design = Design("No IAM / ACL authorization");
        var output = TemporaryDirectory();

        try
        {
            MarkdownRenderer.Render([operation], [design], new RealAzureMigrationDoc(), output);

            var serviceIndex = Read(output, "s3.md");
            Assert.Contains("# s3 {#service-s3}", serviceIndex, StringComparison.Ordinal);
            Assert.Contains("`service:s3`", serviceIndex, StringComparison.Ordinal);
            Assert.Contains("<a id=\"putobject\"></a>[PutObject](operations/s3/putobject.md)", serviceIndex, StringComparison.Ordinal);
            Assert.Contains("`operation:s3:putobject`", serviceIndex, StringComparison.Ordinal);

            var operationPage = Read(output, "operations", "s3", "putobject.md");
            Assert.Contains("{#operation-s3-putobject}", operationPage, StringComparison.Ordinal);
            Assert.Contains("`operation:s3:putobject`", operationPage, StringComparison.Ordinal);
            Assert.Contains("{#sub-feature-content-type---metadata}", operationPage, StringComparison.Ordinal);
            Assert.Contains("`sub-feature:s3:putobject:content-type---metadata`", operationPage, StringComparison.Ordinal);
            Assert.Contains("**Gap.** Some headers differ.", operationPage, StringComparison.Ordinal);

            var designIndex = Read(output, "design-gaps.md");
            Assert.Contains(
                "<a id=\"s3-no-iam---acl-authorization\"></a>" +
                "<a id=\"no-iam-acl-authorization\" data-legacy-fragment=\"true\"></a>" +
                "[No IAM / ACL authorization](design-gaps/s3/no-iam---acl-authorization.md)",
                designIndex,
                StringComparison.Ordinal);

            var designPage = Read(output, "design-gaps", "s3", "no-iam---acl-authorization.md");
            Assert.Contains("{#design-gap-s3-no-iam---acl-authorization}", designPage, StringComparison.Ordinal);
            Assert.Contains("`design-gap:s3:no-iam---acl-authorization`", designPage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Render_preserves_every_legacy_design_gap_fragment_for_the_canonical_corpus()
    {
        var repoRoot = FindRepoRoot();
        var designs = Loader.LoadDesignDocs(Path.Combine(repoRoot, "docs", "gaps"));
        var expected = LegacyDesignGapFragments.Create(designs)
            .Select(fragment => fragment.Fragment)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var markdown = File.ReadAllText(Path.Combine(repoRoot, "docs", "site", "design-gaps.md"));
        var actual = Regex.Matches(
                markdown,
                """<a id="([^"]+)" data-legacy-fragment="true"></a>""",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Contains("no-aws-region-account-namespace", actual);
        Assert.Contains("no-aws-region-account-namespace_1", actual);
    }

    [Theory]
    [InlineData("Secondary indexes (GSI / LSI)", "secondary-indexes-gsi-lsi")]
    [InlineData("Already_1", "already_1")]
    [InlineData("- Edge -", "-edge-")]
    public void LegacyHeadingIds_matches_python_markdown_slugification(
        string heading,
        string expected)
    {
        var headings = new LegacyHeadingIds();

        Assert.Equal(expected, headings.Add(heading));
        Assert.Equal(
            expected.EndsWith("_1", StringComparison.Ordinal)
                ? expected[..^1] + "2"
                : expected + "_1",
            headings.Add(heading));
    }

    [Fact]
    public void Render_preserves_every_legacy_service_fragment_for_the_canonical_corpus()
    {
        var repoRoot = FindRepoRoot();
        var operations = Loader.LoadAll(Path.Combine(repoRoot, "docs", "gaps"));

        foreach (var group in operations
                     .GroupBy(operation => operation.Service.ToLowerInvariant())
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(operation => operation.Operation, StringComparer.Ordinal)
                .ToList();
            var expected = LegacyServiceFragments.Create(group.Key, ordered)
                .Select(fragment => fragment.Fragment)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var markdown = File.ReadAllText(
                Path.Combine(repoRoot, "docs", "site", DocumentationLinks.ServicePage(group.Key)));
            var actual = Regex.Matches(
                    markdown,
                    """<a id="([^"]+)" data-legacy-fragment="true"></a>""",
                    RegexOptions.CultureInvariant)
                .Select(match => match.Groups[1].Value)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
        }

        var s3 = File.ReadAllText(Path.Combine(repoRoot, "docs", "site", "s3.md"));
        Assert.Contains("id=\"sub-features\" data-legacy-fragment=\"true\"", s3);
        Assert.Contains("id=\"sub-features_1\" data-legacy-fragment=\"true\"", s3);
    }

    [Fact]
    public void Render_is_byte_deterministic_and_removes_stale_granular_files()
    {
        var firstOperation = Operation("PutObject");
        var secondOperation = Operation("GetObject");
        var firstDesign = Design("First gap");
        firstDesign.DesignGaps.Add(new DesignGap
        {
            Area = "Second gap",
            Status = "planned",
            Disposition = "feasible_backlog",
            TrackingIssue = "#771",
            Summary = "Second."
        });
        var output = TemporaryDirectory();

        try
        {
            MarkdownRenderer.Render(
                [firstOperation, secondOperation],
                [firstDesign],
                new RealAzureMigrationDoc(),
                output);
            var expected = Snapshot(output);

            var staleOperation = Path.Combine(output, "operations", "s3", "stale.md");
            var staleDesignGap = Path.Combine(output, "design-gaps", "s3", "stale.md");
            var staleService = Path.Combine(output, "removed-service.md");
            File.WriteAllText(staleOperation, "hand edited");
            File.WriteAllText(staleDesignGap, "hand edited");
            File.WriteAllText(staleService, "hand edited");
            File.WriteAllText(Path.Combine(output, "operations", "s3", "putobject.md"), "hand edited");

            MarkdownRenderer.Render(
                [secondOperation, firstOperation],
                [firstDesign],
                new RealAzureMigrationDoc(),
                output);

            Assert.Equal(expected, Snapshot(output));
            Assert.False(File.Exists(staleOperation));
            Assert.False(File.Exists(staleDesignGap));
            Assert.False(File.Exists(staleService));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Render_uses_the_same_normalized_service_path_for_indexes_and_backlinks()
    {
        var operation = Operation("PutObject");
        operation.Service = "foo.bar";
        var output = TemporaryDirectory();

        try
        {
            MarkdownRenderer.Render([operation], [], new RealAzureMigrationDoc(), output);

            Assert.True(File.Exists(Path.Combine(output, "foobar.md")));
            Assert.Contains(
                "[← foo.bar operation index](../../foobar.md)",
                Read(output, "operations", "foobar", "putobject.md"),
                StringComparison.Ordinal);
            Assert.Contains(
                "[foo.bar](foobar.md)",
                Read(output, "index.md"),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static OperationDoc Operation(string name) => new()
    {
        Service = "s3",
        Operation = name,
        AzureEquivalent = "Azure Blob Storage",
        Status = "partial",
        Disposition = "by_design",
        SourceFile = Path.Combine("repo", "docs", "gaps", "s3", name + ".yaml")
    };

    private static ServiceDesignDoc Design(string area) => new()
    {
        Service = "s3",
        SourceFile = Path.Combine("repo", "docs", "gaps", "s3", "_design.yaml"),
        DesignGaps =
        [
            new DesignGap
            {
                Area = area,
                Status = "by_design",
                Summary = "Known limitation."
            }
        ]
    };

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aws2azure-gapdocs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

    private static SortedDictionary<string, string> Snapshot(string root) =>
        new(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                    path => Convert.ToHexString(File.ReadAllBytes(path)),
                    StringComparer.Ordinal),
            StringComparer.Ordinal);

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

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
