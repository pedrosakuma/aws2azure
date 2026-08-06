using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Goldens;

public sealed class GoldenStoreTests
{
    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "golden-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static CanonicalResponse Sample() => AwsErrorCanonicalizer.Canonicalize(
        403,
        new[]
        {
            new KeyValuePair<string, string>("Content-Type", "application/xml"),
            new KeyValuePair<string, string>("x-amz-request-id", "REQ"),
        },
        "<Error><Code>SignatureDoesNotMatch</Code><Message>m</Message>" +
        "<RequestId>REQ</RequestId></Error>");

    [Fact]
    public void Serialize_then_parse_round_trips_canonical_text()
    {
        var response = Sample();
        var prov = new GoldenProvenance(
            GoldenProvenance.SourceLocalStack, "s3:SignatureDoesNotMatch",
            DateTimeOffset.UtcNow, "emulator-derived");

        var serialized = GoldenStore.Serialize(response, prov);
        var parsed = GoldenStore.Parse(serialized);

        Assert.Equal(response.Render(), parsed.CanonicalText);
        Assert.Equal(GoldenProvenance.SourceLocalStack, parsed.Provenance.Source);
        Assert.Equal("s3:SignatureDoesNotMatch", parsed.Provenance.Operation);
        Assert.Equal("emulator-derived", parsed.Provenance.Note);
    }

    [Fact]
    public void Provenance_authoritativeness_reflects_source()
    {
        Assert.True(new GoldenProvenance(GoldenProvenance.SourceRealAws, "o", default).IsAuthoritative);
        Assert.False(new GoldenProvenance(GoldenProvenance.SourceLocalStack, "o", default).IsAuthoritative);
        Assert.False(new GoldenProvenance(GoldenProvenance.SourceProxySelf, "o", default).IsAuthoritative);
    }

    [Fact]
    public void Save_then_TryLoad_round_trips_via_disk()
    {
        var tmp = CreateScratchDirectory();
        try
        {
            var store = new GoldenStore(tmp);
            var response = Sample();
            var prov = new GoldenProvenance(
                GoldenProvenance.SourceLocalStack, "s3:Case", DateTimeOffset.UtcNow);

            Assert.False(store.Exists("case1"));
            store.Save("case1", response, prov);
            Assert.True(store.Exists("case1"));
            Assert.True(store.TryLoad("case1", out var loaded));
            Assert.Equal(response.Render(), loaded.CanonicalText);
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }

    [Fact]
    public void TryLoad_missing_returns_false()
    {
        var tmp = CreateScratchDirectory();
        try
        {
            var store = new GoldenStore(tmp);
            Assert.False(store.TryLoad("nope", out _));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_prefers_real_aws_over_localstack_and_proxy_self()
    {
        var tmp = CreateScratchDirectory();
        try
        {
            var store = new GoldenStore(tmp);

            store.Save("case1", Sample(), new GoldenProvenance(
                GoldenProvenance.SourceProxySelf,
                "s3:Case",
                DateTimeOffset.UtcNow,
                "proxy snapshot"));
            store.Save("case1", Sample(), new GoldenProvenance(
                GoldenProvenance.SourceLocalStack,
                "s3:Case",
                DateTimeOffset.UtcNow,
                "emulator-derived"));
            store.Save("case1", Sample(), new GoldenProvenance(
                GoldenProvenance.SourceRealAws,
                "s3:Case",
                DateTimeOffset.UtcNow,
                "authoritative oracle"));

            Assert.True(File.Exists(store.PathFor("case1")));
            Assert.True(File.Exists(store.PathFor("case1", GoldenProvenance.SourceProxySelf)));
            Assert.True(File.Exists(store.PathFor("case1", GoldenProvenance.SourceRealAws)));

            Assert.True(store.TryLoad("case1", out var loaded));
            Assert.Equal(GoldenProvenance.SourceRealAws, loaded.Provenance.Source);
            Assert.True(loaded.Provenance.IsAuthoritative);
            Assert.Equal("authoritative oracle", loaded.Provenance.Note);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_reads_legacy_single_file_without_migration()
    {
        var tmp = CreateScratchDirectory();
        try
        {
            var store = new GoldenStore(tmp);
            var provenance = new GoldenProvenance(
                GoldenProvenance.SourceLocalStack,
                "s3:Case",
                DateTimeOffset.UtcNow,
                "legacy localstack file");

            File.WriteAllText(store.PathFor("case1"), GoldenStore.Serialize(Sample(), provenance));

            Assert.True(store.TryLoad("case1", out var loaded));
            Assert.Equal(GoldenProvenance.SourceLocalStack, loaded.Provenance.Source);
            Assert.False(loaded.Provenance.IsAuthoritative);
            Assert.Equal("legacy localstack file", loaded.Provenance.Note);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Save_real_aws_uses_distinct_authoritative_path()
    {
        var tmp = CreateScratchDirectory();
        try
        {
            var store = new GoldenStore(tmp);
            var provenance = new GoldenProvenance(
                GoldenProvenance.SourceRealAws,
                "s3:GetObject",
                DateTimeOffset.UtcNow,
                "Captured from real AWS by the future Tier-3 workflow; authoritative oracle.");

            store.Save("missing-key", Sample(), provenance);

            Assert.False(File.Exists(store.PathFor("missing-key")));
            Assert.True(File.Exists(store.PathFor("missing-key", GoldenProvenance.SourceRealAws)));
            Assert.True(store.TryLoad("missing-key", out var loaded));
            Assert.Equal(GoldenProvenance.SourceRealAws, loaded.Provenance.Source);
            Assert.Equal(provenance.Note, loaded.Provenance.Note);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
