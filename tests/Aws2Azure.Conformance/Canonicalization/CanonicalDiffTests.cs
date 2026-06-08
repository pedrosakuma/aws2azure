using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;

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
    public void Content_type_charset_mismatch_is_tagged_header_value()
    {
        var expected = Make(403, "application/xml", Xml);
        var actual = Make(403, "application/xml; charset=utf-8", Xml);
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
    public void Real_s3_gap_docs_load_without_error()
    {
        // Smoke: the allow-list can parse the committed S3 gap docs. Tag set may
        // be empty until divergences are documented — loading must not throw.
        var allow = ConformanceAllowList.FromGapDocs("s3");
        Assert.NotNull(allow.Tags);
    }
}
