using System.Text;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.UnitTests.SigV4;

/// <summary>
/// Proves the allocation-light signing-key + expected-signature primitives
/// (<see cref="SigningKey.Derive(string,string,string,string,Span{byte})"/> and
/// <see cref="SigningKey.ComputeExpectedSignatureHex"/>) are byte-identical to
/// the array/string oracle
/// (<see cref="SigningKey.Derive(string,string,string,string)"/> +
/// <see cref="CanonicalRequest.StringToSign(string,string,string)"/> +
/// <see cref="SigningKey.HmacSha256(byte[],byte[])"/>) over adversarial inputs,
/// including values large enough to force the ArrayPool fallback and non-ASCII
/// values. The array/string path stays the correctness oracle.
/// </summary>
public class SigningKeyBytePipeGoldenTests
{
    public static IEnumerable<object[]> DeriveCorpus()
    {
        // secret, date, region, service
        yield return ["wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20150830", "us-east-1", "service"];
        yield return ["", "20240101", "us-east-1", "s3"];
        yield return ["short", "20240101", "eu-west-1", "dynamodb"];
        // Non-ASCII secret/region/service must UTF-8-encode identically.
        yield return ["s\u00e9cr\u00e8t-\u4e2d\u6587-\U0001F600", "20240101", "r\u00e9gion", "serv\u00efce"];
        // Long secret -> forces the kSecret ArrayPool fallback (> 256 bytes).
        yield return [new string('K', 400), "20240101", "us-east-1", "s3"];
        // Long attacker-controlled region/service -> forces the HmacUtf8 fallback.
        yield return ["secret", "20240101", new string('r', 600), new string('s', 600)];
    }

    [Theory]
    [MemberData(nameof(DeriveCorpus))]
    public void Derive_span_matches_array_oracle(string secret, string date, string region, string service)
    {
        var oracle = SigningKey.Derive(secret, date, region, service);

        Span<byte> pipe = stackalloc byte[32];
        SigningKey.Derive(secret, date, region, service, pipe);

        Assert.Equal(SigningKey.ToLowerHex(oracle), SigningKey.ToLowerHex(pipe));
    }

    public static IEnumerable<object[]> SignatureCorpus()
    {
        // canonicalRequest text, amzDate, accessKey, date, region, service, secret
        yield return [
            "GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n"
            + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "20150830T123600Z", "AKIDEXAMPLE", "20150830", "us-east-1", "service",
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"];
        yield return [
            "POST\n/\n\nhost:dynamodb.us-east-1.amazonaws.com\nx-amz-date:20240115T101112Z\n\nhost;x-amz-date\nUNSIGNED-PAYLOAD",
            "20240115T101112Z", "AKIAEXAMPLE2", "20240115", "us-east-1", "dynamodb",
            new string('K', 400)];
        yield return [
            "PUT\n/obj\n\nhost:svc\n\nhost\nabc",
            "20240101T000000Z", "AKIA", "20240101", new string('r', 600), new string('s', 600),
            "s\u00e9cr\u00e8t"];
    }

    [Theory]
    [MemberData(nameof(SignatureCorpus))]
    public void Expected_signature_hex_matches_string_oracle(
        string canonicalRequest, string amzDate, string accessKey,
        string date, string region, string service, string secret)
    {
        var scope = new CredentialScope(accessKey, date, region, service);

        // Oracle: hash canonical -> string-to-sign -> derive -> HMAC -> hex.
        var oracleSts = CanonicalRequest.StringToSign(amzDate, scope.ToScopeString(), canonicalRequest);
        var oracleKey = SigningKey.Derive(secret, scope.Date, scope.Region, scope.Service);
        var oracleHex = SigningKey.ToLowerHex(
            SigningKey.HmacSha256(oracleKey, Encoding.UTF8.GetBytes(oracleSts)));

        // Byte pipe: feed the precomputed canonical digest.
        Span<byte> canonicalHash = stackalloc byte[32];
        SHA256Hash(canonicalRequest, canonicalHash);
        Span<byte> pipeHex = stackalloc byte[64];
        SigningKey.ComputeExpectedSignatureHex(secret, scope, amzDate, canonicalHash, pipeHex);

        Assert.Equal(oracleHex, Encoding.ASCII.GetString(pipeHex));
    }

    private static void SHA256Hash(string text, Span<byte> dest)
        => System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text), dest);
}
