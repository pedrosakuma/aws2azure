using System.Text;
using System.Text.Json;
using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Sqs;

/// <summary>
/// Tier-1 (offline, every-PR, blocking) SQS error conformance. SQS is the only
/// dual-protocol module, so this matrix exercises BOTH the AWS-JSON
/// <c>{"__type":…}</c> envelope (third JSON-protocol service after DynamoDB and
/// Kinesis) and — for the first time in the harness — the AWS Query
/// <c>&lt;ErrorResponse&gt;&lt;Error&gt;…</c> XML envelope through the
/// canonicalizer's unwrap path. The Query+JSON auth pairs pin both branches of the
/// issue #241 SQS <c>EmitSigV4FailureAsync</c> override. For each proxy-side
/// rejection it:
/// <list type="number">
///   <item>asserts the proxy's response matches the AWS SQS <em>contract</em>
///   (HTTP status + protocol-correct error envelope + short dispatch code) — an
///   oracle derived from the AWS API, not the proxy's own output, so this catches
///   drift even with no golden present;</item>
///   <item>when a committed golden exists (captured from LocalStack/AWS by the
///   Tier-2 job), additionally asserts allow-list-aware canonical equality.</item>
/// </list>
/// </summary>
public sealed class SqsErrorConformanceTests : IClassFixture<SqsConformanceFixture>
{
    private readonly SqsConformanceFixture _fixture;

    public SqsErrorConformanceTests(SqsConformanceFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> CaseNames() =>
        SqsErrorMatrix.Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Proxy_error_matches_aws_contract_and_golden(string caseName)
    {
        var testCase = SqsErrorMatrix.Cases.Single(c => c.Name == caseName);

        using var request = testCase.BuildRequest(
            SqsConformanceFixture.AccessKeyId, SqsConformanceFixture.Secret);
        using var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var canonical = AwsErrorCanonicalizer.Canonicalize(
            (int)response.StatusCode, FlattenHeaders(response), body);

        // (1) AWS-contract oracle — always enforced, offline. Status + the short
        // dispatch code (which AWS SDKs switch on) are protocol-independent.
        Assert.Equal(testCase.ExpectedStatus, canonical.StatusCode);
        Assert.Contains(canonical.BodyFields, f => f.Name == "Message");
        var code = canonical.BodyFields.FirstOrDefault(f => f.Name == "Code");
        Assert.Equal(testCase.ExpectedCode, code.Value);

        // (1b) Protocol-specific raw-wire assertions. The SQS module negotiates
        // the wire shape per request (issue #241), so pin the faithful envelope
        // for each protocol rather than only the normalized canonical surface.
        if (testCase.Protocol == SqsCaseProtocol.Json)
        {
            Assert.Equal(CanonicalResponse.BodyKindJsonError, canonical.BodyKind);
            Assert.Contains("application/x-amz-json",
                response.Content.Headers.ContentType?.ToString() ?? string.Empty);
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("__type", out var typeProp),
                "SQS AWS-JSON error envelope must carry a __type field.");
            // SQS JSON renders __type as <namespace>#<Code>; SDKs dispatch on the
            // segment after '#'. Tolerate the prefix but require the code.
            var rawType = typeProp.GetString() ?? string.Empty;
            Assert.EndsWith(testCase.ExpectedCode, rawType, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(CanonicalResponse.BodyKindXmlError, canonical.BodyKind);
            Assert.Contains("text/xml",
                response.Content.Headers.ContentType?.ToString() ?? string.Empty);
            // Faithful Query envelope: the AWS <ErrorResponse> wrapper (not S3's
            // bare <Error> root), unwrapped by the canonicalizer to the same
            // top-level Code/Message surface.
            var root = canonical.BodyFields.FirstOrDefault(f => f.Name == "(root)");
            Assert.Equal("ErrorResponse", root.Value);
        }

        // (2) Golden faithfulness diff — active once a golden is committed for
        // this case (SQS SigV4 errors cannot be sourced from LocalStack, which
        // ignores signatures, but parser-stage goldens can be).
        var store = GoldenStore.ForService("sqs");
        if (store.TryLoad(testCase.Name, out var golden))
        {
            var expected = CanonicalResponse.ParseRendered(golden.CanonicalText);
            var allow = ConformanceAllowList.FromGapDocs("sqs");
            var (_, unexpected) = allow.Partition(
                CanonicalDiff.Compare(expected, canonical), testCase.Name);
            Assert.True(unexpected.Count == 0, BuildReport(testCase, golden, unexpected, canonical));
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> FlattenHeaders(HttpResponseMessage response)
    {
        foreach (var h in response.Headers)
        {
            yield return new(h.Key, string.Join(", ", h.Value));
        }
        foreach (var h in response.Content.Headers)
        {
            yield return new(h.Key, string.Join(", ", h.Value));
        }
    }

    private static string BuildReport(
        SqsErrorCase testCase, GoldenFile golden,
        IReadOnlyList<Divergence> unexpected, CanonicalResponse actual)
    {
        var sb = new StringBuilder();
        sb.Append("Conformance divergence for case '").Append(testCase.Name)
          .Append("' (golden source: ").Append(golden.Provenance.Source).Append(").\n");
        sb.Append("Undocumented divergences (add a [conformance:<tag>] note to the gap doc to accept):\n");
        foreach (var d in unexpected)
        {
            sb.Append("  - [").Append(d.Tag).Append("] ").Append(d.Description).Append('\n');
        }
        sb.Append("\n--- expected (golden) ---\n").Append(golden.CanonicalText);
        sb.Append("\n--- actual (proxy) ---\n").Append(actual.Render());
        return sb.ToString();
    }
}
