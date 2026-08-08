using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Diff;

namespace Aws2Azure.Conformance.Canonicalization;

public sealed class CanonicalDiffTests
{
    private static CanonicalResponse Make(int status, string contentType, string xml,
        bool withId2 = true)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Content-Type", contentType),
            new("x-amz-request-id", "R"),
        };
        if (withId2)
        {
            headers.Add(new("x-amz-id-2", "H"));
        }
        return AwsErrorCanonicalizer.Canonicalize(status, headers, xml);
    }

    private const string Xml = "<Error><Code>SignatureDoesNotMatch</Code><Message>m</Message></Error>";

    [Fact]
    public void Identical_responses_have_no_divergences()
    {
        var a = Make(403, "application/xml", Xml);
        var b = Make(403, "application/xml", Xml);
        Assert.Empty(CanonicalDiff.Compare(a, b));
    }

    [Fact]
    public void Status_difference_is_tagged_status()
    {
        var diffs = CanonicalDiff.Compare(Make(403, "application/xml", Xml), Make(400, "application/xml", Xml));
        Assert.Contains(diffs, d => d.Tag == "status");
    }

    [Fact]
    public void Missing_header_in_actual_is_tagged_missing_header()
    {
        var expected = Make(403, "application/xml", Xml, withId2: true);
        var actual = Make(403, "application/xml", Xml, withId2: false);
        var diffs = CanonicalDiff.Compare(expected, actual);
        Assert.Contains(diffs, d => d.Tag == "missing-header:x-amz-id-2");
    }

    [Fact]
    public void Content_type_charset_mismatch_produces_no_divergence()
    {
        // charset is normalized out of the canonical form, so a charset-only
        // difference must not surface as a content-type divergence.
        var expected = Make(403, "application/xml", Xml);
        var actual = Make(403, "application/xml; charset=utf-8", Xml);
        var diffs = CanonicalDiff.Compare(expected, actual);
        Assert.DoesNotContain(diffs, d => d.Tag == "header-value:content-type");
    }

    [Fact]
    public void Content_type_media_type_mismatch_is_tagged_header_value()
    {
        // A genuine media-type change (not just charset) must still be detected.
        var expected = Make(403, "application/xml", Xml);
        var actual = Make(403, "application/json", Xml);
        var diffs = CanonicalDiff.Compare(expected, actual);
        Assert.Contains(diffs, d => d.Tag == "header-value:content-type");
    }

    [Fact]
    public void Missing_body_field_is_tagged_missing_field()
    {
        var expected = Make(403, "application/xml",
            "<Error><Code>X</Code><HostId>h</HostId></Error>");
        var actual = Make(403, "application/xml", "<Error><Code>X</Code></Error>");
        var diffs = CanonicalDiff.Compare(expected, actual);
        Assert.Contains(diffs, d => d.Tag == "missing-field:HostId");
    }

    [Fact]
    public void Different_code_value_is_tagged_field_value()
    {
        var expected = Make(403, "application/xml", "<Error><Code>A</Code></Error>");
        var actual = Make(403, "application/xml", "<Error><Code>B</Code></Error>");
        var diffs = CanonicalDiff.Compare(expected, actual);
        Assert.Contains(diffs, d => d.Tag == "field-value:Code");
    }
}

public sealed class ConformanceAllowListTests
{
    [Fact]
    public void Extracts_conformance_tags_from_behavior_differences()
    {
        var tags = ConformanceAllowList.ExtractTags(new[]
        {
            "Proxy omits the server-side x-amz-id-2 header [conformance:missing-header:x-amz-id-2]",
            "Content-Type carries charset=utf-8 unlike AWS [conformance:header-value:content-type]",
            "A purely prose difference with no tag",
        }).ToList();

        Assert.Contains("missing-header:x-amz-id-2", tags);
        Assert.Contains("header-value:content-type", tags);
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void Partition_separates_accepted_from_unexpected()
    {
        var allow = new ConformanceAllowList(new[] { "missing-header:x-amz-id-2" });
        var divergences = new[]
        {
            new Divergence("missing-header:x-amz-id-2", "documented"),
            new Divergence("field-value:Code", "regression!"),
        };

        var (accepted, unexpected) = allow.Partition(divergences);

        Assert.Single(accepted);
        Assert.Single(unexpected);
        Assert.Equal("field-value:Code", unexpected[0].Tag);
    }

    [Fact]
    public void Case_scoped_tag_does_not_suppress_other_cases()
    {
        // A waiver scoped to one case must not accept the same divergence in a
        // different case (gpt-5.5 finding: service-global tags over-suppress).
        var allow = new ConformanceAllowList(new[] { "signature-does-not-match::field-value:Code" });
        var divergence = new Divergence("field-value:Code", "Code mismatch");

        Assert.True(allow.Accepts(divergence, "signature-does-not-match"));
        Assert.False(allow.Accepts(divergence, "invalid-access-key-id"));
        Assert.False(allow.Accepts(divergence, caseName: null));
    }

    [Fact]
    public void Service_wide_tag_accepts_in_every_case()
    {
        var allow = new ConformanceAllowList(new[] { "missing-header:x-amz-id-2" });
        var divergence = new Divergence("missing-header:x-amz-id-2", "header omitted");

        Assert.True(allow.Accepts(divergence, "signature-does-not-match"));
        Assert.True(allow.Accepts(divergence, "invalid-access-key-id"));
        Assert.True(allow.Accepts(divergence, caseName: null));
    }

    [Fact]
    public void Real_s3_gap_docs_load_without_error()
    {
        // Smoke: the allow-list can parse the committed S3 gap docs. Tag set may
        // be empty until divergences are documented — loading must not throw.
        var allow = ConformanceAllowList.FromGapDocs("s3");
        Assert.NotNull(allow.Tags);
    }

    [Fact]
    public async Task Offline_diff_normalizes_ephemeral_list_objects_bucket_name()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "offline-diff-normalized");
        Directory.CreateDirectory(root);
        var goldens = new Aws2Azure.Conformance.Goldens.GoldenStore(Path.Combine(root, "goldens"));
        var evidence = new Aws2Azure.Conformance.Goldens.EvidenceStore(Path.Combine(root, "evidence"));

        var responseA = CanonicalResponse.ParseRendered("""
HTTP 200
[header] x-amz-request-id: <MASKED>
[body:xml-error]
  (root): ListBucketResult
  Name: aws2azure-it-111-aaa-s3bucket
""");
        var responseB = CanonicalResponse.ParseRendered("""
HTTP 200
[header] x-amz-request-id: <MASKED>
[body:xml-error]
  (root): ListBucketResult
  Name: aws2azure-it-222-bbb-s3bucket
""");

        goldens.SaveStep("list-objects-v2-pagination", "create-bucket", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "create-bucket", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "enable-versioning", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "enable-versioning", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "seed-object-1", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "seed-object-1", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "seed-object-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "seed-object-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "list-page-1", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "list-page-1", responseB,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "list-page-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "list-page-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "delete-object-1", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "delete-object-1", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "delete-object-version-1", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "delete-object-version-1", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "delete-object-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "delete-object-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        goldens.SaveStep("list-objects-v2-pagination", "delete-object-version-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("aws", "s3:ListObjectsV2", DateTimeOffset.UtcNow));
        evidence.SaveStep("list-objects-v2-pagination", "delete-object-version-2", responseA,
            new Aws2Azure.Conformance.Goldens.GoldenProvenance("azure", "s3:ListObjectsV2", DateTimeOffset.UtcNow));

        var result = await OfflineConformanceDiffRunner.CompareAsync(
            "s3",
            Aws2Azure.Conformance.S3.S3HappyPathMatrix.Cases.Single(c => c.Name == "list-objects-v2-pagination"),
            goldens,
            evidence,
            new ConformanceAllowList(Array.Empty<string>()));

        Assert.Equal(OfflineConformanceDiffStatus.Passed, result.Status);
    }
}
