using System.Text;
using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Tier-1 (offline, every-PR, blocking) S3 error conformance. For each
/// proxy-side auth-error scenario it:
/// <list type="number">
///   <item>asserts the proxy's response matches the AWS S3 <em>contract</em>
///   (HTTP status + error <c>Code</c> + XML error envelope) — an oracle derived
///   from the AWS API, not the proxy's own output, so this catches drift even
///   with no golden present;</item>
///   <item>when a committed golden exists (captured from LocalStack/AWS by the
///   Tier-2 job), additionally asserts allow-list-aware canonical equality so
///   header/charset faithfulness gaps surface unless documented in a gap doc.</item>
/// </list>
/// </summary>
public sealed class S3ErrorConformanceTests : IClassFixture<ConformanceProxyFixture>
{
    private readonly ConformanceProxyFixture _fixture;

    public S3ErrorConformanceTests(ConformanceProxyFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> CaseNames() =>
        S3ErrorMatrix.Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Proxy_error_matches_aws_contract_and_golden(string caseName)
    {
        var testCase = S3ErrorMatrix.Cases.Single(c => c.Name == caseName);

        using var request = testCase.BuildRequest();
        using var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var canonical = AwsErrorCanonicalizer.Canonicalize(
            (int)response.StatusCode, FlattenHeaders(response), body);

        // (1) AWS-contract oracle — always enforced, offline.
        Assert.Equal(testCase.ExpectedStatus, canonical.StatusCode);
        Assert.Equal(CanonicalResponse.BodyKindXmlError, canonical.BodyKind);
        var code = canonical.BodyFields.FirstOrDefault(f => f.Name == "Code");
        Assert.Equal(testCase.ExpectedCode, code.Value);

        // (2) Golden faithfulness diff — active once Tier-2 commits goldens.
        var store = GoldenStore.ForService("s3");
        if (store.TryLoad(testCase.Name, out var golden))
        {
            var expected = CanonicalResponse.ParseRendered(golden.CanonicalText);
            var allow = ConformanceAllowList.FromGapDocs("s3");
            var (_, unexpected) = allow.Partition(CanonicalDiff.Compare(expected, canonical));
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
        S3ErrorCase testCase, GoldenFile golden,
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
