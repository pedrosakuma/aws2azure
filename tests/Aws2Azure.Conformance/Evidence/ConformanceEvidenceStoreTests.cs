using Aws2Azure.Conformance.Canonicalization;

namespace Aws2Azure.Conformance.Evidence;

public sealed class ConformanceEvidenceStoreTests
{
    private static CanonicalResponse Sample() => AwsErrorCanonicalizer.Canonicalize(
        200,
        [new KeyValuePair<string, string>("Content-Type", "application/json")],
        """{"TableNames":["example"]}""");

    [Fact]
    public void Serialize_then_parse_round_trips_canonical_text()
    {
        var response = Sample();
        var metadata = new ConformanceEvidenceMetadata(
            ConformanceEvidenceMetadata.SourceRealAzureProxy,
            "dynamodb",
            "scan-pagination",
            "dynamodb:Scan",
            "03-scan-page-1",
            DateTimeOffset.UtcNow,
            "captured from real Azure through the proxy");

        var serialized = ConformanceEvidenceStore.Serialize(response, metadata);
        var parsed = ConformanceEvidenceStore.Parse(serialized);

        Assert.Equal(response.Render(), parsed.CanonicalText);
        Assert.Equal(metadata.Source, parsed.Metadata.Source);
        Assert.Equal(metadata.Service, parsed.Metadata.Service);
        Assert.Equal(metadata.CaseName, parsed.Metadata.CaseName);
        Assert.Equal(metadata.Operation, parsed.Metadata.Operation);
        Assert.Equal(metadata.Step, parsed.Metadata.Step);
        Assert.Equal(metadata.Note, parsed.Metadata.Note);
    }

    [Fact]
    public void Save_writes_service_case_step_path()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            nameof(ConformanceEvidenceStoreTests),
            Guid.NewGuid().ToString("N"));

        try
        {
            var store = new ConformanceEvidenceStore(root);
            var metadata = new ConformanceEvidenceMetadata(
                ConformanceEvidenceMetadata.SourceRealAzureProxy,
                "s3",
                "put-get-delete-object-roundtrip",
                "s3:PutObject/GetObject/DeleteObject",
                "02-put-object",
                DateTimeOffset.UtcNow);

            store.Save(Sample(), metadata);

            var expectedPath = Path.Combine(
                root,
                "s3",
                "put-get-delete-object-roundtrip",
                "02-put-object.evidence");
            Assert.True(File.Exists(expectedPath));
            Assert.Contains("# service: s3", File.ReadAllText(expectedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveRoot_without_override_finds_repository_default_location()
    {
        var root = ConformanceEvidenceStore.ResolveRoot();
        var repositoryRoot = Directory.GetParent(root)!.Parent!.Parent!.FullName;

        Assert.EndsWith(
            Path.Combine("TestResults", "real-azure-conformance", "canonical-cases"),
            root,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "aws2azure.slnx")));
    }
}
