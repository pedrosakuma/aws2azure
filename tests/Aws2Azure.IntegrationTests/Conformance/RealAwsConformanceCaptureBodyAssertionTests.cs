using Aws2Azure.Conformance.Canonicalization;
using Xunit;

namespace Aws2Azure.IntegrationTests.Conformance;

/// <summary>
/// Offline, fast unit coverage for
/// <see cref="RealAwsConformanceCaptureTests.BodyAssertionSatisfied"/> — the
/// body-path evaluator used by <c>capture-real-aws.yml</c> to enforce
/// <c>RequiredBodyAssertions</c> on real-AWS responses. This directly guards
/// against the two path-syntax bugs found while investigating a
/// <c>capture-real-aws.yml</c> regression: array-index segments
/// (<c>Records[0].SequenceNumber</c>) were never recognized (the literal
/// property name "Records[0]" was looked up instead), and an XML path whose
/// first segment named the document's own root element (e.g.
/// <c>ListBucketResult.IsTruncated</c> against a <c>&lt;ListBucketResult&gt;</c>
/// root) always failed because <c>Descendants()</c> excludes the element
/// itself.
/// </summary>
public sealed class RealAwsConformanceCaptureBodyAssertionTests
{
    private static readonly CanonicalResponse EmptyCanonical =
        new(200, [], CanonicalResponse.BodyKindOpaque, [], string.Empty);

    [Fact]
    public void JsonPath_finds_explicit_array_index_field()
    {
        const string body = """{"Records":[{"SequenceNumber":"1"},{"SequenceNumber":"2"}]}""";

        Assert.True(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Records[0].SequenceNumber"));
        Assert.True(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Records[1].SequenceNumber"));
    }

    [Fact]
    public void JsonPath_out_of_range_array_index_fails()
    {
        const string body = """{"Records":[{"SequenceNumber":"1"}]}""";

        Assert.False(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Records[5].SequenceNumber"));
    }

    [Fact]
    public void JsonPath_missing_field_on_indexed_element_fails()
    {
        const string body = """{"Records":[{"NotSequenceNumber":"1"}]}""";

        Assert.False(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Records[0].SequenceNumber"));
    }

    [Fact]
    public void JsonPath_without_explicit_index_still_defaults_to_first_element()
    {
        const string body = """{"Records":[{"SequenceNumber":"1"}]}""";

        Assert.True(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Records.SequenceNumber"));
    }

    [Fact]
    public void JsonPath_malformed_non_numeric_index_fails_fast()
    {
        const string body = """{"Records":[{"SequenceNumber":"1"}]}""";

        // "Records[abc]" must not silently degrade to a bracket-less
        // "Records" lookup (which would default to index 0 and mask the
        // typo) — it must fail outright.
        Assert.False(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Records[abc].SequenceNumber"));
    }

    [Fact]
    public void JsonPath_malformed_nested_bracket_index_fails_fast()
    {
        const string body = """{"Records":[{"SequenceNumber":"1"}]}""";

        Assert.False(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Records[0][1].SequenceNumber"));
    }

    [Fact]
    public void XmlPath_matches_when_first_segment_is_the_document_root()
    {
        const string body =
            """<ListBucketResult><IsTruncated>true</IsTruncated></ListBucketResult>""";

        Assert.True(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "ListBucketResult.IsTruncated"));
    }

    [Fact]
    public void XmlPath_still_matches_nested_elements_below_the_root()
    {
        const string body =
            """<ListBucketResult><Contents><Key>a</Key></Contents></ListBucketResult>""";

        Assert.True(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "ListBucketResult.Contents"));
    }

    [Fact]
    public void XmlPath_fails_when_segment_absent_anywhere_in_document()
    {
        const string body =
            """<ListBucketResult><IsTruncated>true</IsTruncated></ListBucketResult>""";

        Assert.False(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "ListBucketResult.NextContinuationToken"));
    }

    [Fact]
    public void XmlPath_matches_a_present_but_empty_self_closing_element()
    {
        // Real AWS answers GetObjectTagging with an empty <TagSet/> once all
        // tags are removed. The assertion only needs to confirm the element
        // is present on the wire, not that it has content — a self-closing
        // (present-but-empty) element must satisfy the path.
        const string body = """<Tagging><TagSet/></Tagging>""";

        Assert.True(RealAwsConformanceCaptureTests.BodyAssertionSatisfied(
            EmptyCanonical, body, "Tagging.TagSet"));
    }
}
