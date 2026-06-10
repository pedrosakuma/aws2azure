using System.Text;
using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Goldens;
using Xunit;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Tier-2 (LocalStack differential, Docker-gated, nightly/labeled, non-blocking)
/// S3 backend-error conformance. For each backend-mapped error scenario it sends
/// the same validly-signed request to the proxy (translating Azurite's failure)
/// and to LocalStack S3 (authoritative real-S3 shape), canonicalizes both, and:
/// <list type="number">
///   <item>asserts the proxy meets the AWS S3 <em>contract</em> (status + Error
///   envelope + Code + Message);</item>
///   <item>asserts LocalStack produced the same contract-level outcome (so the
///   reference is sane);</item>
///   <item>diffs proxy vs LocalStack and fails on any divergence not documented
///   in a gap-doc <c>[conformance:&lt;tag&gt;]</c> note (allow-list).</item>
/// </list>
/// In record mode (<c>AWS2AZURE_CONFORMANCE_RECORD=1</c>) the LocalStack
/// canonical response is written as the committed golden so the Tier-1 replay
/// can also diff against it offline on every PR.
/// </summary>
[Collection(S3BackendDifferentialCollection.Name)]
public sealed class S3BackendConformanceTests
{
    private const string SkipDisabled =
        "Tier-2 LocalStack differential is opt-in (set AWS2AZURE_CONFORMANCE_TIER2=1); " +
        "it runs in the dedicated conformance.yml workflow, not the blocking every-PR job.";

    private readonly S3BackendDifferentialFixture _fx;

    public S3BackendConformanceTests(S3BackendDifferentialFixture fx) => _fx = fx;

    public static IEnumerable<object[]> CaseNames() =>
        S3BackendErrorMatrix.Cases.Select(c => new object[] { c.Name });

    [SkippableTheory]
    [MemberData(nameof(CaseNames))]
    public async Task Proxy_backend_error_matches_localstack(string caseName)
    {
        Skip.IfNot(S3BackendDifferentialFixture.Tier2Enabled, SkipDisabled);

        // When Tier-2 is enabled the fixture must have booted both containers;
        // a startup failure should already have failed the collection, so this
        // is a hard assertion (not a skip) to keep the signal honest.
        Assert.True(_fx.DockerAvailable,
            "Tier-2 is enabled but Azurite/LocalStack did not start.");

        var testCase = S3BackendErrorMatrix.Cases.Single(c => c.Name == caseName);

        var bucket = "conf-" + Guid.NewGuid().ToString("N")[..16];
        if (testCase.RequiresExistingBucket)
        {
            await _fx.CreateBucketOnBothAsync(bucket);
        }

        string path;
        if (testCase.TargetsBucketRoot)
        {
            // CreateBucket-style cases address the bucket root (no key).
            path = $"/{bucket}";
        }
        else if (testCase.RequiresExistingObject)
        {
            await _fx.PutObjectOnBothAsync(
                bucket, S3BackendErrorMatrix.ExistingKey,
                Encoding.UTF8.GetBytes("conformance conditional object"));
            path = $"/{bucket}/{S3BackendErrorMatrix.ExistingKey}";
        }
        else
        {
            path = $"/{bucket}/{S3BackendErrorMatrix.MissingKey}";
        }

        var proxy = await SendAsync(_fx.ProxyClient, _fx.ProxyBaseUri, testCase, path);
        var localStack = await SendAsync(_fx.LocalStackClient, _fx.LocalStackBaseUri, testCase, path);

        // (1) Proxy AWS-contract oracle.
        Assert.Equal(testCase.ExpectedStatus, proxy.StatusCode);
        Assert.Equal(CanonicalResponse.BodyKindXmlError, proxy.BodyKind);
        Assert.Equal("Error", RootOf(proxy));
        Assert.Equal(testCase.ExpectedCode, CodeOf(proxy));
        Assert.Contains(proxy.BodyFields, f => f.Name == "Message");

        // (2) LocalStack reference sanity — the differential is only meaningful
        // when the authoritative side produced the same logical error.
        Assert.True(
            localStack.StatusCode == testCase.ExpectedStatus
            && CodeOf(localStack) == testCase.ExpectedCode,
            $"LocalStack reference did not produce {testCase.ExpectedCode}/{testCase.ExpectedStatus} " +
            $"(got {CodeOf(localStack)}/{localStack.StatusCode}). Reference environment is unhealthy.\n" +
            localStack.Render());

        // (3) Faithfulness diff: proxy (actual) vs LocalStack (expected/golden),
        // allow-list aware (service-wide + <case>::<tag>).
        var allow = ConformanceAllowList.FromGapDocs("s3");
        var (_, unexpected) = allow.Partition(
            CanonicalDiff.Compare(expected: localStack, actual: proxy), testCase.Name);
        Assert.True(unexpected.Count == 0, BuildReport(testCase, unexpected, localStack, proxy));

        if (GoldenStore.RecordMode)
        {
            GoldenStore.ForService("s3").Save(
                testCase.Name,
                localStack,
                new GoldenProvenance(
                    GoldenProvenance.SourceLocalStack,
                    testCase.TargetsBucketRoot ? "CreateBucket" : "GetObject",
                    DateTimeOffset.UtcNow,
                    "Captured from LocalStack S3 by the Tier-2 differential job (emulator-derived)."));
        }
    }

    private async Task<CanonicalResponse> SendAsync(
        HttpClient client, Uri baseUri, S3BackendErrorCase testCase, string path)
    {
        using var request = new HttpRequestMessage(
            testCase.Method ?? HttpMethod.Get, new Uri(baseUri, path));
        // Most cases sign for us-east-1 (the fixture's default); region-sensitive
        // cases (e.g. BucketAlreadyOwnedByYou) sign for another region so the
        // proxy's region-aware branch (#236) is exercised. The signed scope
        // region is independent of the request Host.
        ConformanceSigV4Signer.SignHeader(
            request, Array.Empty<byte>(), _fx.AccessKeyId, _fx.Secret,
            region: testCase.SignRegion ?? "us-east-1");
        // Conditional headers (e.g. If-Match) are not part of the signed header
        // set, so attach them after signing — exactly as a real SDK leaves
        // unsigned headers off the canonical request.
        testCase.ConfigureRequest?.Invoke(request);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return AwsErrorCanonicalizer.Canonicalize(
            (int)response.StatusCode, FlattenHeaders(response), body);
    }

    private static string RootOf(CanonicalResponse r) =>
        r.BodyFields.FirstOrDefault(f => f.Name == "(root)").Value;

    private static string CodeOf(CanonicalResponse r) =>
        r.BodyFields.FirstOrDefault(f => f.Name == "Code").Value;

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
        S3BackendErrorCase testCase,
        IReadOnlyList<Divergence> unexpected,
        CanonicalResponse localStack,
        CanonicalResponse proxy)
    {
        var sb = new StringBuilder();
        sb.Append("Tier-2 backend-error divergence for case '").Append(testCase.Name).Append("'.\n");
        sb.Append("Undocumented divergences (proxy vs LocalStack). To accept one, add a\n");
        sb.Append("[conformance:<tag>] note to a docs/gaps/s3/*.yaml behavior_differences entry\n");
        sb.Append("(service-wide) or [conformance:").Append(testCase.Name).Append("::<tag>] (case-scoped):\n");
        foreach (var d in unexpected)
        {
            sb.Append("  - [").Append(d.Tag).Append("] ").Append(d.Description).Append('\n');
        }
        sb.Append("\n--- expected (LocalStack) ---\n").Append(localStack.Render());
        sb.Append("\n--- actual (proxy) ---\n").Append(proxy.Render());
        return sb.ToString();
    }
}
