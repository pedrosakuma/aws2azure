using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Goldens;

public sealed class GoldenStoreTests
{
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
        var tmp = Path.Combine(Path.GetTempPath(), "a2a-golden-" + Guid.NewGuid().ToString("N"));
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
        var store = new GoldenStore(Path.Combine(Path.GetTempPath(), "a2a-missing-" + Guid.NewGuid().ToString("N")));
        Assert.False(store.TryLoad("nope", out _));
    }
}
