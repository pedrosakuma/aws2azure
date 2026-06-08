using Aws2Azure.Conformance.Canonicalization;

namespace Aws2Azure.Conformance.Canonicalization;

public sealed class AwsErrorCanonicalizerTests
{
    private static KeyValuePair<string, string> H(string n, string v) => new(n, v);

    private const string NoSuchKeyXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message>" +
        "<Key>missing.txt</Key><RequestId>ABC123REQ</RequestId><HostId>aGVsbG8=</HostId></Error>";

    [Fact]
    public void Masks_volatile_body_elements_and_message_but_keeps_code()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(
            404,
            new[] { H("Content-Type", "application/xml") },
            NoSuchKeyXml);

        Assert.Equal(404, c.StatusCode);
        Assert.Equal(CanonicalResponse.BodyKindXmlError, c.BodyKind);
        var fields = c.BodyFields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("NoSuchKey", fields["Code"]);
        Assert.Equal(CanonicalResponse.Masked, fields["Message"]);
        Assert.Equal(CanonicalResponse.Masked, fields["RequestId"]);
        Assert.Equal(CanonicalResponse.Masked, fields["HostId"]);
        Assert.Equal("missing.txt", fields["Key"]);
        Assert.Equal("Error", fields["(root)"]);
    }

    [Fact]
    public void Two_responses_differing_only_in_request_id_canonicalize_equal()
    {
        var a = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml"), H("x-amz-request-id", "AAA") },
            NoSuchKeyXml.Replace("ABC123REQ", "AAA"));
        var b = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml"), H("x-amz-request-id", "BBB") },
            NoSuchKeyXml.Replace("ABC123REQ", "BBB"));

        Assert.Equal(a.Render(), b.Render());
    }

    [Fact]
    public void Different_error_code_produces_different_canonical_form()
    {
        var a = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, NoSuchKeyXml);
        var b = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") },
            NoSuchKeyXml.Replace("NoSuchKey", "NoSuchBucket"));

        Assert.NotEqual(a.Render(), b.Render());
    }

    [Fact]
    public void Different_status_produces_different_canonical_form()
    {
        var a = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, NoSuchKeyXml);
        var b = AwsErrorCanonicalizer.Canonicalize(403,
            new[] { H("Content-Type", "application/xml") }, NoSuchKeyXml);

        Assert.NotEqual(a.Render(), b.Render());
    }

    [Fact]
    public void Transport_headers_are_dropped_but_x_amz_headers_are_kept()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(403,
            new[]
            {
                H("Content-Type", "application/xml"),
                H("Server", "AmazonS3"),
                H("Date", "Sun, 08 Jun 2026 00:00:00 GMT"),
                H("Content-Length", "215"),
                H("Connection", "close"),
                H("x-amz-request-id", "REQ1"),
                H("x-amz-id-2", "longopaquehostid"),
            },
            NoSuchKeyXml);

        var names = c.Headers.Select(h => h.Name).ToHashSet();
        Assert.Contains("content-type", names);
        Assert.Contains("x-amz-request-id", names);
        Assert.Contains("x-amz-id-2", names);
        Assert.DoesNotContain("server", names);
        Assert.DoesNotContain("date", names);
        Assert.DoesNotContain("content-length", names);
        Assert.DoesNotContain("connection", names);
    }

    [Fact]
    public void Volatile_header_values_are_masked()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(403,
            new[] { H("x-amz-request-id", "REQ1"), H("x-amz-id-2", "hostid2") },
            NoSuchKeyXml);

        Assert.All(c.Headers.Where(h => h.Name is "x-amz-request-id" or "x-amz-id-2"),
            h => Assert.Equal(CanonicalResponse.Masked, h.Value));
    }

    [Fact]
    public void Missing_x_amz_id_2_header_surfaces_as_a_divergence()
    {
        // Real AWS sends x-amz-id-2; a proxy that omits it should NOT canonicalize
        // equal to the AWS response — this is exactly the kind of header drift the
        // harness must catch.
        var aws = AwsErrorCanonicalizer.Canonicalize(403,
            new[] { H("Content-Type", "application/xml"), H("x-amz-request-id", "R"), H("x-amz-id-2", "H") },
            NoSuchKeyXml);
        var proxy = AwsErrorCanonicalizer.Canonicalize(403,
            new[] { H("Content-Type", "application/xml"), H("x-amz-request-id", "R") },
            NoSuchKeyXml);

        Assert.NotEqual(aws.Render(), proxy.Render());
    }

    [Fact]
    public void Content_type_charset_difference_is_preserved()
    {
        var withCharset = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml; charset=utf-8") }, NoSuchKeyXml);
        var without = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, NoSuchKeyXml);

        Assert.NotEqual(withCharset.Render(), without.Render());
    }

    [Fact]
    public void Content_type_is_normalized_case_and_spacing_insensitively()
    {
        var a = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "Application/XML;charset=UTF-8") }, NoSuchKeyXml);
        var b = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("content-type", "application/xml; charset=utf-8") }, NoSuchKeyXml);

        Assert.Equal(a.Render(), b.Render());
    }

    [Fact]
    public void Element_ordering_does_not_affect_canonical_form()
    {
        var ordered =
            "<Error><Code>NoSuchKey</Code><Message>m</Message><RequestId>r</RequestId></Error>";
        var shuffled =
            "<Error><RequestId>r</RequestId><Message>m</Message><Code>NoSuchKey</Code></Error>";
        var a = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, ordered);
        var b = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, shuffled);

        Assert.Equal(a.Render(), b.Render());
    }

    [Fact]
    public void Empty_body_canonicalizes_to_empty_kind()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(204,
            new[] { H("x-amz-request-id", "R") }, string.Empty);

        Assert.Equal(CanonicalResponse.BodyKindEmpty, c.BodyKind);
        Assert.Empty(c.BodyFields);
    }

    [Fact]
    public void Malformed_xml_is_surfaced_as_opaque_not_thrown()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(500,
            new[] { H("Content-Type", "application/xml") }, "<Error><Code>oops");

        Assert.Equal(CanonicalResponse.BodyKindOpaque, c.BodyKind);
    }

    [Fact]
    public void Wrong_envelope_root_surfaces_as_a_divergence()
    {
        var error = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") },
            "<Error><Code>NoSuchKey</Code></Error>");
        var wrong = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") },
            "<Fault><Code>NoSuchKey</Code></Fault>");

        Assert.NotEqual(error.Render(), wrong.Render());
    }

    [Fact]
    public void Does_not_resolve_external_entities()
    {
        // A DTD with an external entity must not be expanded (XXE guard). The
        // canonicalizer prohibits DTD processing, so this is reported opaque.
        var xxe =
            "<?xml version=\"1.0\"?><!DOCTYPE Error [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" +
            "<Error><Code>&x;</Code></Error>";
        var c = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, xxe);

        Assert.Equal(CanonicalResponse.BodyKindOpaque, c.BodyKind);
    }

    [Fact]
    public void Nested_element_yields_marker_and_preserves_sibling_fields()
    {
        // An element with nested element children must degrade to a structural
        // marker WITHOUT collapsing the whole envelope to opaque or dropping the
        // sibling Code field (regression guard for the canonicalizer being reused
        // by the Tier-2 differential).
        var xml =
            "<Error><Code>SlowDown</Code>" +
            "<Detail><Inner>x</Inner><Other>y</Other></Detail>" +
            "<Resource>/bucket</Resource></Error>";
        var c = AwsErrorCanonicalizer.Canonicalize(503,
            new[] { H("Content-Type", "application/xml") }, xml);

        Assert.Equal(CanonicalResponse.BodyKindXmlError, c.BodyKind);
        var fields = c.BodyFields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("SlowDown", fields["Code"]);
        Assert.Equal("<nested>", fields["Detail"]);
        Assert.Equal("/bucket", fields["Resource"]);
    }

    [Fact]
    public void Empty_child_element_yields_empty_value_and_keeps_siblings()
    {
        var xml = "<Error><Code>X</Code><Resource/><RequestId>r</RequestId></Error>";
        var c = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, xml);

        var fields = c.BodyFields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("X", fields["Code"]);
        Assert.Equal(string.Empty, fields["Resource"]);
        Assert.Equal(CanonicalResponse.Masked, fields["RequestId"]);
    }

    [Fact]
    public void ParseRendered_round_trips_so_diff_against_a_golden_is_empty()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(403,
            new[]
            {
                H("Content-Type", "application/xml; charset=utf-8"),
                H("x-amz-request-id", "R"),
                H("x-amz-id-2", "H"),
            },
            NoSuchKeyXml);

        var reparsed = CanonicalResponse.ParseRendered(c.Render());

        Assert.Equal(c.Render(), reparsed.Render());
        Assert.Empty(CanonicalDiff.Compare(c, reparsed));
    }
}
