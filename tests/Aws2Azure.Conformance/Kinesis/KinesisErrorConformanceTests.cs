using System.Text;
using System.Text.Json;
using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Kinesis;

/// <summary>
/// Tier-1 (offline, every-PR, blocking) Kinesis error conformance. Kinesis
/// speaks AWS JSON 1.1, so this is the second matrix (after DynamoDB) to exercise
/// the JSON-envelope path of <see cref="AwsErrorCanonicalizer"/> end-to-end
/// against a real service module — and the first against the prefix-free
/// <c>__type</c> form and the default (un-overridden)
/// <c>EmitSigV4FailureAsync</c>. For each proxy-side rejection it:
/// <list type="number">
///   <item>asserts the proxy's response matches the AWS Kinesis
///   <em>contract</em> (HTTP status + JSON error envelope + <c>__type</c> short
///   code) — an oracle derived from the AWS API, not the proxy's own output, so
///   this catches drift even with no golden present;</item>
///   <item>when a committed golden exists (captured from LocalStack/AWS by the
///   Tier-2 job), additionally asserts allow-list-aware canonical equality so
///   header/charset faithfulness gaps surface unless documented in a gap doc.</item>
/// </list>
/// </summary>
public sealed class KinesisErrorConformanceTests : IClassFixture<KinesisConformanceFixture>
{
    private readonly KinesisConformanceFixture _fixture;

    public KinesisErrorConformanceTests(KinesisConformanceFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> CaseNames() =>
        KinesisErrorMatrix.Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Proxy_error_matches_aws_contract_and_golden(string caseName)
    {
        var testCase = KinesisErrorMatrix.Cases.Single(c => c.Name == caseName);

        using var request = testCase.BuildRequest(
            KinesisConformanceFixture.AccessKeyId, KinesisConformanceFixture.Secret);
        using var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var canonical = AwsErrorCanonicalizer.Canonicalize(
            (int)response.StatusCode, FlattenHeaders(response), body);

        // (1) AWS-contract oracle — always enforced, offline. Pin the Kinesis
        // JSON error envelope shape: status, json-error body kind, the required
        // Code (short __type) + Message fields, that the dispatch code matches,
        // and that the content type is the AWS-JSON media type SDKs expect.
        Assert.Equal(testCase.ExpectedStatus, canonical.StatusCode);
        Assert.Equal(CanonicalResponse.BodyKindJsonError, canonical.BodyKind);
        Assert.Contains(canonical.BodyFields, f => f.Name == "Message");
        var code = canonical.BodyFields.FirstOrDefault(f => f.Name == "Code");
        Assert.Equal(testCase.ExpectedCode, code.Value);

        // (1b) Kinesis-specific raw-wire assertions. The canonicalized Code and
        // the "application/x-amz-json" prefix above are deliberately normalized
        // (they mirror how SDKs dispatch), so on their own they would still pass
        // if the proxy regressed to a coral-prefixed __type or the wrong AWS-JSON
        // version. Pin the faithful Kinesis shape directly: __type is the BARE
        // error code (no com.amazonaws…# namespace prefix) and the media type is
        // exactly application/x-amz-json-1.1.
        Assert.Equal("application/x-amz-json-1.1",
            response.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.True(doc.RootElement.TryGetProperty("__type", out var typeProp),
                "Kinesis error envelope must carry a __type field.");
            Assert.Equal(testCase.ExpectedCode, typeProp.GetString());
        }

        // (2) Golden faithfulness diff — active once a golden is committed for
        // this case (Kinesis SigV4 errors cannot be sourced from LocalStack,
        // which ignores signatures, but parser-stage goldens can be).
        var store = GoldenStore.ForService("kinesis");
        if (store.TryLoad(testCase.Name, out var golden))
        {
            var expected = CanonicalResponse.ParseRendered(golden.CanonicalText);
            var allow = ConformanceAllowList.FromGapDocs("kinesis");
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
        KinesisErrorCase testCase, GoldenFile golden,
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
