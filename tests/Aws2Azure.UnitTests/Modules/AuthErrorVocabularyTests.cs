using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Modules;

/// <summary>
/// Unit coverage for the protocol-aware SigV4 auth-error vocabulary (issue
/// #241). AWS REST-XML services (S3) answer SigV4 failures with HTTP 403 and the
/// S3 error codes; AWS-JSON services (DynamoDB, Kinesis, modern SQS) answer with
/// HTTP 400 and the JSON exception vocabulary. The proxy must render the shape
/// matching the caller's wire protocol or an AWS SDK sees a non-faithful error.
/// </summary>
public class AuthErrorVocabularyTests
{
    [Theory]
    [InlineData(SigV4ValidationStatus.InvalidSignature, 400, "InvalidSignatureException")]
    [InlineData(SigV4ValidationStatus.ClockSkewTooLarge, 400, "InvalidSignatureException")]
    [InlineData(SigV4ValidationStatus.Expired, 400, "InvalidSignatureException")]
    [InlineData(SigV4ValidationStatus.UnknownAccessKey, 400, "UnrecognizedClientException")]
    [InlineData(SigV4ValidationStatus.Malformed, 400, "IncompleteSignatureException")]
    public void Json_maps_to_400_with_json_exception_vocabulary(
        SigV4ValidationStatus status, int expectedStatus, string expectedCode)
    {
        var (statusCode, code) = AuthErrorVocabulary.Resolve(AwsErrorFormat.Json, status);
        Assert.Equal(expectedStatus, statusCode);
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData(SigV4ValidationStatus.InvalidSignature, 403, "SignatureDoesNotMatch")]
    [InlineData(SigV4ValidationStatus.UnknownAccessKey, 403, "InvalidAccessKeyId")]
    [InlineData(SigV4ValidationStatus.Expired, 403, "AccessDenied")]
    [InlineData(SigV4ValidationStatus.ClockSkewTooLarge, 403, "RequestTimeTooSkewed")]
    [InlineData(SigV4ValidationStatus.Malformed, 400, "InvalidRequest")]
    public void Xml_keeps_the_s3_403_vocabulary(
        SigV4ValidationStatus status, int expectedStatus, string expectedCode)
    {
        var (statusCode, code) = AuthErrorVocabulary.Resolve(AwsErrorFormat.Xml, status);
        Assert.Equal(expectedStatus, statusCode);
        Assert.Equal(expectedCode, code);
    }
}

/// <summary>
/// Covers the <see cref="IServiceModule.EmitSigV4FailureAsync"/> default
/// implementation: it must resolve the protocol-aware vocabulary from the
/// module's <see cref="IServiceModule.ErrorFormat"/> and render it.
/// </summary>
public class EmitSigV4FailureDefaultTests
{
    private sealed class VocabularyModule : IServiceModule
    {
        public string ServiceName => "vocab";
        public bool MatchesHost(string host) => true;
        public CapabilityMatrix Capabilities { get; } = new("vocab", []);
        public bool RequiresSigV4 => true;
        public AwsErrorFormat ErrorFormat { get; init; }
        public ValueTask HandleAsync(HttpContext context) => ValueTask.CompletedTask;
    }

    private static DefaultHttpContext NewContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task Json_module_renders_400_invalid_signature_exception()
    {
        IServiceModule module = new VocabularyModule { ErrorFormat = AwsErrorFormat.Json };
        var ctx = NewContext();

        await module.EmitSigV4FailureAsync(ctx, SigV4ValidationStatus.InvalidSignature, "bad sig");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal("InvalidSignatureException", doc.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task Json_module_renders_400_unrecognized_client_for_unknown_key()
    {
        IServiceModule module = new VocabularyModule { ErrorFormat = AwsErrorFormat.Json };
        var ctx = NewContext();

        await module.EmitSigV4FailureAsync(ctx, SigV4ValidationStatus.UnknownAccessKey, "no such key");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal("UnrecognizedClientException", doc.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task Xml_module_keeps_403_signature_does_not_match()
    {
        IServiceModule module = new VocabularyModule { ErrorFormat = AwsErrorFormat.Xml };
        var ctx = NewContext();

        await module.EmitSigV4FailureAsync(ctx, SigV4ValidationStatus.InvalidSignature, "bad sig");

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var doc = XDocument.Parse(ReadBody(ctx));
        Assert.Equal("SignatureDoesNotMatch", doc.Root!.Element("Code")!.Value);
    }
}

/// <summary>
/// End-to-end registry coverage: an unknown access key driven through the real
/// <see cref="ServiceModuleRegistry"/> + <see cref="SigV4Validator"/> must reach
/// the caller in the module's protocol-correct shape. The unknown key
/// short-circuits the validator before signature/skew checks, so a syntactically
/// valid Authorization header with a bogus signature is enough.
/// </summary>
public class RegistryAuthErrorVocabularyTests
{
    private static ICredentialResolver ResolverWithKey(string accessKeyId) =>
        new StaticCredentialResolver(new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = accessKeyId,
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials(),
                },
            },
        });

    private sealed class RegistryTestModule : IServiceModule
    {
        public string ServiceName => "fake";
        public bool MatchesHost(string host) => host.StartsWith("fake.", StringComparison.OrdinalIgnoreCase);
        public CapabilityMatrix Capabilities { get; } = new("fake", []);
        public bool RequiresSigV4 => true;
        public AwsErrorFormat ErrorFormat { get; init; }
        public bool Invoked { get; private set; }
        public ValueTask HandleAsync(HttpContext context)
        {
            Invoked = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return ValueTask.CompletedTask;
        }
    }

    private static DefaultHttpContext UnknownKeyRequest()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("fake.example.com");
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = "/";
        ctx.Request.Headers["Authorization"] =
            "AWS4-HMAC-SHA256 Credential=AKIAUNKNOWN/20200101/us-east-1/fake/aws4_request, " +
            "SignedHeaders=host, Signature=00";
        ctx.Request.Headers["X-Amz-Date"] = "20200101T000000Z";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task Json_module_unknown_key_returns_400_unrecognized_client()
    {
        var module = new RegistryTestModule { ErrorFormat = AwsErrorFormat.Json };
        var registry = new ServiceModuleRegistry([module], new SigV4Validator(ResolverWithKey("AKIA")));
        var ctx = UnknownKeyRequest();

        await registry.DispatchAsync(ctx);

        Assert.False(module.Invoked);
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal("UnrecognizedClientException", doc.RootElement.GetProperty("__type").GetString());
    }

    [Fact]
    public async Task Xml_module_unknown_key_returns_403_invalid_access_key()
    {
        var module = new RegistryTestModule { ErrorFormat = AwsErrorFormat.Xml };
        var registry = new ServiceModuleRegistry([module], new SigV4Validator(ResolverWithKey("AKIA")));
        var ctx = UnknownKeyRequest();

        await registry.DispatchAsync(ctx);

        Assert.False(module.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var doc = XDocument.Parse(ReadBody(ctx));
        Assert.Equal("InvalidAccessKeyId", doc.Root!.Element("Code")!.Value);
    }
}
