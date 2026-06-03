using System;
using System.Diagnostics;
using System.Text;
using Aws2Azure.Core.SigV4;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.SigV4;

/// <summary>
/// Allocation micro-measurement isolating the SigV4 canonical-request +
/// string-to-sign construction (no network, no HttpContext) so the win is
/// attributable to the byte pipe and not drowned by emulator noise — per the
/// design-review measurement methodology.
///
/// Compares the string oracle (<c>Build → StringToSign(string) →
/// UTF8.GetBytes</c>, which re-hashes the canonical string) against the
/// allocation-light byte pipe (<see cref="CanonicalRequest.HashCanonicalRequest"/>
/// → span <c>StringToSign</c>) using
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Gated by
/// <c>AWS2AZURE_PERF=1</c> so CI stays fast.
/// </summary>
public class CanonicalRequestBytePipeAllocBench
{
    private readonly ITestOutputHelper _output;

    public CanonicalRequestBytePipeAllocBench(ITestOutputHelper output) => _output = output;

    private static bool PerfEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_PERF"), "1", StringComparison.Ordinal);

    [Fact]
    public void Byte_pipe_allocates_less_per_op()
    {
        if (!PerfEnabled)
        {
            _output.WriteLine("Skipped (set AWS2AZURE_PERF=1 to run).");
            return;
        }

        const string method = "POST";
        const string rawPath = "/";
        const string rawQuery = "";
        const string amzDate = "20240115T101112Z";
        const string scope = "20240115/us-east-1/dynamodb/aws4_request";
        const string payloadHash = "9b6c1e2f3a4d5b6c7e8f90112233445566778899aabbccddeeff001122334455";
        var headers = new[]
        {
            new KeyValuePair<string, string>("Host", "dynamodb.us-east-1.amazonaws.com"),
            new KeyValuePair<string, string>("X-Amz-Date", amzDate),
            new KeyValuePair<string, string>("X-Amz-Target", "DynamoDB_20120810.GetItem"),
            new KeyValuePair<string, string>("Content-Type", "application/x-amz-json-1.0"),
            new KeyValuePair<string, string>("X-Amz-Content-Sha256", payloadHash),
            new KeyValuePair<string, string>("Content-Length", "128"),
            new KeyValuePair<string, string>("User-Agent", "aws-sdk-dotnet/3.7"),
            new KeyValuePair<string, string>("Accept-Encoding", "identity"),
        };
        string[] signedHeaders =
            ["content-type", "host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"];

        const int iters = 100_000;

        for (int i = 0; i < 1000; i++) { _ = OldPath(); NewPath(); }

        var sw = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) _ = OldPath();
        var oldBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var oldNs = sw.Elapsed.TotalNanoseconds / iters;

        sw.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) NewPath();
        var newBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var newNs = sw.Elapsed.TotalNanoseconds / iters;

        var reduction = oldBytes <= 0 ? 0 : (1 - newBytes / oldBytes) * 100;
        var speedup = newNs <= 0 ? 0 : oldNs / newNs;
        _output.WriteLine(
            $"old(string)={oldBytes,8:F0} B/op {oldNs,7:F0} ns/op   "
            + $"new(bytepipe)={newBytes,8:F0} B/op {newNs,7:F0} ns/op ({reduction,5:F1}% {speedup,4:F2}x)");

        Assert.True(newBytes < oldBytes,
            $"byte pipe allocated {newBytes:F0} B/op vs string path {oldBytes:F0} B/op");

        // Local functions capture the corpus above.
        byte[] OldPath()
        {
            var canonical = CanonicalRequest.Build(
                method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle: false);
            var sts = CanonicalRequest.StringToSign(amzDate, scope, canonical);
            return Encoding.UTF8.GetBytes(sts);
        }

        void NewPath()
        {
            Span<byte> hash = stackalloc byte[32];
            CanonicalRequest.HashCanonicalRequest(
                method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle: false, hash);
            var sts = CanonicalRequest.StringToSign(amzDate, scope, hash);
            _ = Encoding.UTF8.GetBytes(sts);
        }
    }

    [Fact]
    public void Full_validation_tail_allocates_less_per_op()
    {
        if (!PerfEnabled)
        {
            _output.WriteLine("Skipped (set AWS2AZURE_PERF=1 to run).");
            return;
        }

        const string method = "POST";
        const string rawPath = "/";
        const string rawQuery = "";
        const string amzDate = "20240115T101112Z";
        const string secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        var scope = new CredentialScope("AKIAEXAMPLE", "20240115", "us-east-1", "dynamodb");
        const string payloadHash = "9b6c1e2f3a4d5b6c7e8f90112233445566778899aabbccddeeff001122334455";
        const string clientSignature = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var headers = new[]
        {
            new KeyValuePair<string, string>("Host", "dynamodb.us-east-1.amazonaws.com"),
            new KeyValuePair<string, string>("X-Amz-Date", amzDate),
            new KeyValuePair<string, string>("X-Amz-Target", "DynamoDB_20120810.GetItem"),
            new KeyValuePair<string, string>("Content-Type", "application/x-amz-json-1.0"),
            new KeyValuePair<string, string>("X-Amz-Content-Sha256", payloadHash),
        };
        string[] signedHeaders =
            ["content-type", "host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"];

        const int iters = 100_000;

        for (int i = 0; i < 1000; i++) { OldTail(); NewTail(); }

        var sw = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) OldTail();
        var oldBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var oldNs = sw.Elapsed.TotalNanoseconds / iters;

        sw.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) NewTail();
        var newBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var newNs = sw.Elapsed.TotalNanoseconds / iters;

        var reduction = oldBytes <= 0 ? 0 : (1 - newBytes / oldBytes) * 100;
        var speedup = newNs <= 0 ? 0 : oldNs / newNs;
        _output.WriteLine(
            $"old(tail)={oldBytes,8:F0} B/op {oldNs,7:F0} ns/op   "
            + $"new(tail)={newBytes,8:F0} B/op {newNs,7:F0} ns/op ({reduction,5:F1}% {speedup,4:F2}x)");

        Assert.True(newBytes < oldBytes,
            $"new tail allocated {newBytes:F0} B/op vs old {oldBytes:F0} B/op");

        // Old tail: Build -> StringToSign -> Derive(byte[]) -> HMAC -> hex -> ASCII x2.
        void OldTail()
        {
            var canonical = CanonicalRequest.Build(
                method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle: false);
            var sts = CanonicalRequest.StringToSign(amzDate, scope.ToScopeString(), canonical);
            var key = SigningKey.Derive(secret, scope.Date, scope.Region, scope.Service);
            var expectedHex = SigningKey.ToLowerHex(SigningKey.HmacSha256(key, Encoding.UTF8.GetBytes(sts)));
            var clientBytes = Encoding.ASCII.GetBytes(clientSignature);
            var expectedBytes = Encoding.ASCII.GetBytes(expectedHex);
            _ = clientBytes.Length == expectedBytes.Length
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(clientBytes, expectedBytes);
        }

        // New tail: HashCanonicalRequest -> ComputeExpectedSignatureHex -> ASCII compare.
        void NewTail()
        {
            Span<byte> canonicalHash = stackalloc byte[32];
            CanonicalRequest.HashCanonicalRequest(
                method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle: false, canonicalHash);
            Span<byte> expectedHex = stackalloc byte[64];
            SigningKey.ComputeExpectedSignatureHex(secret, scope, amzDate, canonicalHash, expectedHex);
            Span<byte> clientBytes = stackalloc byte[64];
            Encoding.ASCII.GetBytes(clientSignature, clientBytes);
            _ = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(clientBytes, expectedHex);
        }
    }
}
