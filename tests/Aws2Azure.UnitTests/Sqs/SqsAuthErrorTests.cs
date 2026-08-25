using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.Sqs;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public sealed class SqsAuthErrorTests
{
    [Fact]
    public async Task AwsJson_invalid_signature_uses_403_forbidden()
    {
        var module = new SqsServiceModule(
            new AzureHttpClient(),
            new StubCredentialResolver(),
            CapabilityRegistry.Sqs);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Amz-Target"] = "AmazonSQS.ListQueues";
        context.Request.ContentType = "application/x-amz-json-1.0";
        context.Response.Body = new MemoryStream();

        await module.EmitSigV4FailureAsync(
            context,
            SigV4ValidationStatus.InvalidSignature,
            "signature mismatch");

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.EndsWith("#InvalidSignatureException", document.RootElement.GetProperty("__type").GetString());
    }

    private sealed class StubCredentialResolver : ICredentialResolver
    {
        public bool TryGetAwsSecret(string awsAccessKeyId, out string awsSecretAccessKey)
        {
            awsSecretAccessKey = string.Empty;
            return false;
        }

        public object? GetAzureCredentialsFor(string awsAccessKeyId, AzureService service) => null;
    }
}
