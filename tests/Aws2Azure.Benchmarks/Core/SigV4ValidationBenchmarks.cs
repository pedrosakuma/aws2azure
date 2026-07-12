using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.SigV4;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Aws2Azure.Benchmarks.Core;

/// <summary>
/// Benchmarks full SigV4 validation (header-auth and presigned) using synthetic,
/// valid requests so canonicalization, HMAC, and final signature comparison all
/// stay in the measured path.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SigV4ValidationBenchmarks
{
    private const string AccessKeyId = "AKIAEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset SignedAt = new(2026, 7, 11, 12, 34, 56, TimeSpan.Zero);

    private SigV4Validator _validator = null!;
    private SigV4Request _simpleHeaderGet = null!;
    private SigV4Request _complexQueryHeaderGet = null!;
    private SigV4Request _manyHeadersPost = null!;
    private SigV4Request _presignedGet = null!;

    [GlobalSetup]
    public void Setup()
    {
        _validator = new SigV4Validator(new StaticCredentialResolver(new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = AccessKeyId,
                    AwsSecretAccessKey = SecretKey,
                    Azure = new AzureCredentials(),
                },
            },
        }));

        _simpleHeaderGet = BuildHeaderSignedRequest(new RequestSpec(
            Name: "header-simple-get",
            Method: "GET",
            Host: "example.amazonaws.com",
            Path: "/",
            Query: string.Empty,
            Payload: Array.Empty<byte>(),
            Region: "us-east-1",
            Service: "service",
            S3PathStyle: true,
            ExtraHeaders:
            [
            ]));

        _complexQueryHeaderGet = BuildHeaderSignedRequest(new RequestSpec(
            Name: "header-s3-listobjects-query",
            Method: "GET",
            Host: "s3.us-east-1.amazonaws.com",
            Path: "/benchmark-bucket",
            Query: string.Join('&',
            [
                "list-type=2",
                "prefix=photos%2F2026%2F%E6%97%A5%E6%9C%AC%2F",
                "delimiter=%2F",
                "continuation-token=opaque%2Btoken%3D%3D",
                "encoding-type=url",
                "fetch-owner=true",
                "max-keys=500",
                "start-after=photos%2F2025%2Fsummary.csv",
                "x-id=ListObjectsV2",
                "part-number-marker=9",
                "tagging",
                "metadata=owner%3Dteam-a%26tier%3Dhot",
                "optional-object-attributes=RestoreStatus",
            ]),
            Payload: Array.Empty<byte>(),
            Region: "us-east-1",
            Service: "s3",
            S3PathStyle: true,
            ExtraHeaders:
            [
            ]));

        _manyHeadersPost = BuildHeaderSignedRequest(new RequestSpec(
            Name: "header-dynamodb-many-headers",
            Method: "POST",
            Host: "dynamodb.us-east-1.amazonaws.com",
            Path: "/",
            Query: string.Empty,
            Payload: Encoding.UTF8.GetBytes(
                "{\"TableName\":\"BenchTable\",\"Key\":{\"pk\":{\"S\":\"tenant#42\"},\"sk\":{\"S\":\"item#9001\"}},\"ConsistentRead\":true}"),
            Region: "us-east-1",
            Service: "dynamodb",
            S3PathStyle: false,
            ExtraHeaders:
            [
                new("Content-Type", "application/x-amz-json-1.0"),
                new("X-Amz-Target", "DynamoDB_20120810.GetItem"),
                new("X-Amz-User-Agent", "aws-sdk-dotnet-50/3.7.400.12 ua/2.1 os/linux lang/.NET_10.0 md/ARCH_x64"),
                new("X-Amz-Security-Token", "session-token-for-benchmark-suite"),
                new("Amz-Sdk-Invocation-Id", "b8d7d54c-0b59-4aa2-a819-5d7fa0c0f530"),
                new("Amz-Sdk-Request", "attempt=1; max=4"),
                new("Accept-Encoding", "identity"),
                new("X-Amzn-Trace-Id", "Root=1-686fd530-abcdef0123456789abcdef01"),
            ]));

        _presignedGet = BuildPresignedRequest(new RequestSpec(
            Name: "presigned-s3-get",
            Method: "GET",
            Host: "proxy.local",
            Path: "/benchmark-bucket/reports/2026/july.csv",
            Query: string.Join('&',
            [
                "partNumber=9",
                "response-content-disposition=attachment%3B%20filename%3Djuly.csv",
                "response-content-type=text%2Fcsv",
                "x-id=GetObject",
                "marker=hello%2Bworld",
                "versionId=3L4kqtJlcpXroDTDmJ%2B3DcR%2F5dY",
            ]),
            Payload: Array.Empty<byte>(),
            Region: "us-east-1",
            Service: "s3",
            S3PathStyle: true,
            ExtraHeaders:
            [
            ]),
            expiresIn: TimeSpan.FromMinutes(15));

        ValidateOnce(_simpleHeaderGet, nameof(HeaderSimpleGet));
        ValidateOnce(_complexQueryHeaderGet, nameof(HeaderComplexQueryHeaderGet));
        ValidateOnce(_manyHeadersPost, nameof(HeaderManySignedHeadersPost));
        ValidateOnce(_presignedGet, nameof(PresignedGet));
    }

    [Benchmark(Baseline = true)]
    public SigV4ValidationStatus HeaderSimpleGet() => _validator.Validate(_simpleHeaderGet).Status;

    [Benchmark]
    public SigV4ValidationStatus HeaderComplexQueryHeaderGet() => _validator.Validate(_complexQueryHeaderGet).Status;

    [Benchmark]
    public SigV4ValidationStatus HeaderManySignedHeadersPost() => _validator.Validate(_manyHeadersPost).Status;

    [Benchmark]
    public SigV4ValidationStatus PresignedGet() => _validator.Validate(_presignedGet).Status;

    private void ValidateOnce(SigV4Request request, string scenario)
    {
        var result = _validator.Validate(request);
        if (!result.IsValid)
        {
            throw new InvalidOperationException($"SigV4 benchmark setup failed for {scenario}: {result.Status} {result.Reason}");
        }
    }

    private static SigV4Request BuildHeaderSignedRequest(RequestSpec spec)
    {
        var shortDate = SignedAt.UtcDateTime.ToString(SigV4Constants.AmzShortDateFormat, CultureInfo.InvariantCulture);
        var amzDate = SignedAt.UtcDateTime.ToString(SigV4Constants.AmzDateFormat, CultureInfo.InvariantCulture);
        var scope = $"{shortDate}/{spec.Region}/{spec.Service}/{SigV4Constants.TerminationString}";
        var payloadHash = spec.Payload.Length == 0
            ? SigV4Constants.EmptyPayloadSha256
            : SigningKey.Sha256Hex(spec.Payload);

        var headers = new List<KeyValuePair<string, string>>(spec.ExtraHeaders.Length + 3)
        {
            new("Host", spec.Host),
            new(SigV4Constants.AmzDateHeader, amzDate),
            new(SigV4Constants.AmzContentSha256Header, payloadHash),
        };
        headers.AddRange(spec.ExtraHeaders);

        var signedHeaders = headers
            .Select(static h => h.Key.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static h => h, StringComparer.Ordinal)
            .ToArray();

        var canonical = CanonicalRequest.Build(
            spec.Method,
            spec.Path,
            spec.Query,
            headers,
            signedHeaders,
            payloadHash,
            spec.S3PathStyle);
        var stringToSign = CanonicalRequest.StringToSign(amzDate, scope, canonical);
        var signingKey = SigningKey.Derive(SecretKey, shortDate, spec.Region, spec.Service);
        var signature = SigningKey.ToLowerHex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
        var authorization =
            $"{SigV4Constants.Algorithm} " +
            $"Credential={AccessKeyId}/{scope}, " +
            $"SignedHeaders={string.Join(';', signedHeaders)}, " +
            $"Signature={signature}";
        headers.Add(new KeyValuePair<string, string>(SigV4Constants.AuthorizationHeader, authorization));

        return new SigV4Request
        {
            HttpMethod = spec.Method,
            RawPath = spec.Path,
            RawQueryString = spec.Query,
            Headers = headers,
            PayloadHash = payloadHash,
            S3PathStyle = spec.S3PathStyle,
            Now = SignedAt,
        };
    }

    private static SigV4Request BuildPresignedRequest(RequestSpec spec, TimeSpan expiresIn)
    {
        var shortDate = SignedAt.UtcDateTime.ToString(SigV4Constants.AmzShortDateFormat, CultureInfo.InvariantCulture);
        var amzDate = SignedAt.UtcDateTime.ToString(SigV4Constants.AmzDateFormat, CultureInfo.InvariantCulture);
        var scope = $"{shortDate}/{spec.Region}/{spec.Service}/{SigV4Constants.TerminationString}";
        var credential = $"{AccessKeyId}/{scope}";
        var payloadHash = SigV4Constants.UnsignedPayload;

        var headers = new List<KeyValuePair<string, string>>(spec.ExtraHeaders.Length + 1)
        {
            new("Host", spec.Host),
        };
        headers.AddRange(spec.ExtraHeaders);

        var signedHeaders = headers
            .Select(static h => h.Key.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static h => h, StringComparer.Ordinal)
            .ToArray();

        var queryBuilder = new StringBuilder(spec.Query);
        AppendQueryParameter(queryBuilder, SigV4Constants.AmzAlgorithmQuery, SigV4Constants.Algorithm);
        AppendQueryParameter(queryBuilder, SigV4Constants.AmzCredentialQuery, credential);
        AppendQueryParameter(queryBuilder, SigV4Constants.AmzDateQuery, amzDate);
        AppendQueryParameter(queryBuilder, SigV4Constants.AmzExpiresQuery,
            ((int)expiresIn.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        AppendQueryParameter(queryBuilder, SigV4Constants.AmzSignedHeadersQuery, string.Join(';', signedHeaders));
        var queryWithoutSignature = queryBuilder.ToString();

        var canonical = CanonicalRequest.Build(
            spec.Method,
            spec.Path,
            queryWithoutSignature,
            headers,
            signedHeaders,
            payloadHash,
            spec.S3PathStyle);
        var stringToSign = CanonicalRequest.StringToSign(amzDate, scope, canonical);
        var signingKey = SigningKey.Derive(SecretKey, shortDate, spec.Region, spec.Service);
        var signature = SigningKey.ToLowerHex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        queryBuilder.Append('&')
            .Append(SigV4Constants.AmzSignatureQuery)
            .Append('=')
            .Append(signature);

        return new SigV4Request
        {
            HttpMethod = spec.Method,
            RawPath = spec.Path,
            RawQueryString = queryBuilder.ToString(),
            Headers = headers,
            PayloadHash = payloadHash,
            S3PathStyle = spec.S3PathStyle,
            Now = SignedAt,
        };
    }

    private static void AppendQueryParameter(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append('&');
        }

        builder.Append(Uri.EscapeDataString(key))
            .Append('=')
            .Append(Uri.EscapeDataString(value));
    }

    private sealed record RequestSpec(
        string Name,
        string Method,
        string Host,
        string Path,
        string Query,
        byte[] Payload,
        string Region,
        string Service,
        bool S3PathStyle,
        KeyValuePair<string, string>[] ExtraHeaders);
}
