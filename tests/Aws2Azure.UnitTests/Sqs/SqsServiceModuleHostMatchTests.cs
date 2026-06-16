using Aws2Azure.Modules.Sqs;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class SqsServiceModuleHostMatchTests
{
    [Theory]
    [InlineData("sqs.us-east-1.amazonaws.com", true)]
    [InlineData("sqs-fips.us-east-1.amazonaws.com", true)]
    [InlineData("sqs.cn-north-1.amazonaws.com.cn", true)]
    [InlineData("queue.amazonaws.com", false)]
    [InlineData("s3.us-east-1.amazonaws.com", false)]
    [InlineData("", false)]
    public void MatchesHost_recognises_sqs_endpoints(string host, bool expected)
    {
        var module = new SqsServiceModule(
            new Aws2Azure.Core.Azure.AzureHttpClient(),
            new StubCredentialResolver(),
            Aws2Azure.Core.Modules.CapabilityRegistry.Sqs);

        Assert.Equal(expected, module.MatchesHost(host));
    }

    [Fact]
    public void KnownOperations_is_derived_from_the_wire_protocol_action_table()
    {
        var module = new SqsServiceModule(
            new Aws2Azure.Core.Azure.AzureHttpClient(),
            new StubCredentialResolver(),
            Aws2Azure.Core.Modules.CapabilityRegistry.Sqs);

        // The metrics allowlist must equal the parser's action table so the two
        // cannot drift (a hand-maintained list previously omitted parseable ops
        // such as TagQueue / ListQueueTags / AddPermission).
        Assert.Equal(
            Aws2Azure.Modules.Sqs.WireProtocol.SqsOperationNames.Names.OrderBy(n => n, System.StringComparer.Ordinal),
            module.KnownOperations.OrderBy(n => n, System.StringComparer.Ordinal));
    }

    private sealed class StubCredentialResolver : Aws2Azure.Core.Configuration.ICredentialResolver
    {
        public bool TryGetAwsSecret(string awsAccessKeyId, out string awsSecretAccessKey)
        { awsSecretAccessKey = string.Empty; return false; }
        public object? GetAzureCredentialsFor(string awsAccessKeyId, Aws2Azure.Core.Configuration.AzureService service) => null;
    }
}
