namespace Aws2Azure.Conformance.S3;

internal static class ConformanceSigV4Signer
{
    public static void SignHeader(
        HttpRequestMessage request,
        byte[] body,
        string accessKey,
        string secret,
        string region = "us-east-1",
        string service = "s3",
        DateTimeOffset? now = null,
        IReadOnlyList<string>? extraSignedHeaders = null,
        bool? s3PathStyle = null)
        => Aws2Azure.TestSupport.SigV4.TestSigV4Signer.SignHeader(
            request,
            body,
            accessKey,
            secret,
            region,
            service,
            now,
            extraSignedHeaders,
            s3PathStyle);
}
