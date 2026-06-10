using System.Text;
using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Sns;

/// <summary>
/// Tier-1 (offline, every-PR, blocking) SNS error conformance. SNS is a
/// single-protocol (legacy AWS Query) service, so this matrix drives the AWS
/// Query <c>&lt;ErrorResponse&gt;&lt;Error&gt;…</c> XML envelope through the
/// canonicalizer's unwrap path — the second service to do so after SQS, this time
/// via a module that uses the <em>default</em> <c>EmitSigV4FailureAsync</c> (the
/// REST-XML 403 vocabulary, no per-request override). It completes the #234
/// "templatize the error matrix" checklist. For each proxy-side rejection it:
/// <list type="number">
///   <item>asserts the proxy's response matches the AWS SNS <em>contract</em>
///   (HTTP status + the Query XML envelope + short dispatch code) — an oracle
///   derived from the AWS API, not the proxy's own output, so this catches drift
///   even with no golden present;</item>
///   <item>when a committed golden exists (captured from LocalStack/AWS by the
///   Tier-2 job), additionally asserts allow-list-aware canonical equality.</item>
/// </list>
/// </summary>
public sealed class SnsErrorConformanceTests : IClassFixture<SnsConformanceFixture>
{
    private readonly SnsConformanceFixture _fixture;

    public SnsErrorConformanceTests(SnsConformanceFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> CaseNames() =>
        SnsErrorMatrix.Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Proxy_error_matches_aws_contract_and_golden(string caseName)
    {
        var testCase = SnsErrorMatrix.Cases.Single(c => c.Name == caseName);

        using var request = testCase.BuildRequest(
            SnsConformanceFixture.AccessKeyId, SnsConformanceFixture.Secret);
        using var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var canonical = AwsErrorCanonicalizer.Canonicalize(
            (int)response.StatusCode, FlattenHeaders(response), body);

        // (1) AWS-contract oracle — always enforced, offline.
        Assert.Equal(testCase.ExpectedStatus, canonical.StatusCode);
        Assert.Contains(canonical.BodyFields, f => f.Name == "Message");
        var code = canonical.BodyFields.FirstOrDefault(f => f.Name == "Code");
        Assert.Equal(testCase.ExpectedCode, code.Value);

        // (1b) Faithful Query envelope: the AWS <ErrorResponse> wrapper (not S3's
        // bare <Error> root), unwrapped by the canonicalizer to the same top-level
        // Code/Message surface, served as text/xml.
        Assert.Equal(CanonicalResponse.BodyKindXmlError, canonical.BodyKind);
        Assert.Contains("text/xml",
            response.Content.Headers.ContentType?.ToString() ?? string.Empty);
        var root = canonical.BodyFields.FirstOrDefault(f => f.Name == "(root)");
        Assert.Equal("ErrorResponse", root.Value);

        // (2) Golden faithfulness diff — active once a golden is committed for
        // this case (SNS SigV4 errors cannot be sourced from LocalStack, which
        // ignores signatures, but parser-stage goldens can be).
        var store = GoldenStore.ForService("sns");
        if (store.TryLoad(testCase.Name, out var golden))
        {
            var expected = CanonicalResponse.ParseRendered(golden.CanonicalText);
            var allow = ConformanceAllowList.FromGapDocs("sns");
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
        SnsErrorCase testCase, GoldenFile golden,
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
