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
    public void Content_type_charset_is_normalized_out()
    {
        // The charset parameter is not part of the AWS error wire contract that
        // SDK clients depend on (they parse the XML body regardless), so it is
        // normalized away: a bare media type and one carrying charset canonicalize
        // identically, leaving only a genuine media-type change detectable.
        var withCharset = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml; charset=utf-8") }, NoSuchKeyXml);
        var without = AwsErrorCanonicalizer.Canonicalize(404,
            new[] { H("Content-Type", "application/xml") }, NoSuchKeyXml);

        Assert.Equal(withCharset.Render(), without.Render());
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

    // --- JSON-protocol error envelope (DynamoDB / Kinesis / modern SQS) ---

    private const string ResourceNotFoundJson =
        "{\"__type\":\"com.amazonaws.dynamodb.v20120810#ResourceNotFoundException\"," +
        "\"message\":\"Requested resource not found: Table: t not found\"}";

    [Fact]
    public void Json_envelope_maps_type_to_short_code_and_masks_message()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(
            400,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            ResourceNotFoundJson);

        Assert.Equal(CanonicalResponse.BodyKindJsonError, c.BodyKind);
        var fields = c.BodyFields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("ResourceNotFoundException", fields["Code"]);
        Assert.Equal(CanonicalResponse.Masked, fields["Message"]);
    }

    [Fact]
    public void Json_envelope_is_detected_by_leading_brace_without_content_type()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(
            400, Array.Empty<KeyValuePair<string, string>>(), ResourceNotFoundJson);

        Assert.Equal(CanonicalResponse.BodyKindJsonError, c.BodyKind);
    }

    [Fact]
    public void Json_short_code_is_namespace_prefix_independent()
    {
        // SDK clients dispatch on the short error-code name; the coral namespace
        // prefix differs across AWS implementations and must not be a divergence.
        var prefixed = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.1") },
            "{\"__type\":\"com.amazon.coral.service#ValidationException\",\"message\":\"m\"}");
        var bare = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.1") },
            "{\"__type\":\"ValidationException\",\"Message\":\"different wording\"}");

        Assert.Equal(prefixed.Render(), bare.Render());
    }

    [Fact]
    public void Two_json_responses_differing_only_in_message_canonicalize_equal()
    {
        var a = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            "{\"__type\":\"ResourceNotFoundException\",\"message\":\"Table A not found\"}");
        var b = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            "{\"__type\":\"ResourceNotFoundException\",\"message\":\"Table B not found\"}");

        Assert.Equal(a.Render(), b.Render());
    }

    [Fact]
    public void Different_json_error_code_produces_different_canonical_form()
    {
        var a = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            "{\"__type\":\"ResourceNotFoundException\",\"message\":\"m\"}");
        var b = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            "{\"__type\":\"ValidationException\",\"message\":\"m\"}");

        Assert.NotEqual(a.Render(), b.Render());
    }

    [Fact]
    public void Json_nested_value_yields_marker_and_keeps_scalar_siblings()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            "{\"__type\":\"Throttling\",\"message\":\"slow\"," +
            "\"detail\":{\"inner\":1},\"retryable\":true}");

        var fields = c.BodyFields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("Throttling", fields["Code"]);
        Assert.Equal("<nested>", fields["detail"]);
        Assert.Equal("true", fields["retryable"]);
    }

    [Fact]
    public void Malformed_json_is_surfaced_as_opaque_not_thrown()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(500,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            "{\"__type\":\"oops");

        Assert.Equal(CanonicalResponse.BodyKindOpaque, c.BodyKind);
        Assert.Contains(c.BodyFields, f => f.Name == "(unparseable-json)");
    }

    [Fact]
    public void Non_object_json_is_surfaced_as_opaque()
    {
        var c = AwsErrorCanonicalizer.Canonicalize(500,
            new[] { H("Content-Type", "application/json") }, "[1,2,3]");

        Assert.Equal(CanonicalResponse.BodyKindOpaque, c.BodyKind);
    }

    [Fact]
    public void Json_and_xml_envelopes_with_same_code_share_the_code_field_name()
    {
        // Cross-protocol normalization: both surface the dispatch key under
        // "Code", so the Tier-2 driver's CodeOf() works uniformly. The body
        // KIND still differs (xml-error vs json-error), preserving protocol
        // observability.
        var xml = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/xml") },
            "<Error><Code>ValidationException</Code><Message>m</Message></Error>");
        var json = AwsErrorCanonicalizer.Canonicalize(400,
            new[] { H("Content-Type", "application/x-amz-json-1.0") },
            "{\"__type\":\"ValidationException\",\"message\":\"m\"}");

        Assert.Equal("ValidationException",
            xml.BodyFields.Single(f => f.Name == "Code").Value);
        Assert.Equal("ValidationException",
            json.BodyFields.Single(f => f.Name == "Code").Value);
        Assert.NotEqual(xml.BodyKind, json.BodyKind);
    }
}
