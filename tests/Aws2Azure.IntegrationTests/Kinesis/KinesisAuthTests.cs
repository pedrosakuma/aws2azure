using Amazon.Runtime;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Kinesis;

[Trait("Category", "Integration")]
[Trait("Category", "Kinesis")]
[Collection(KinesisEmulatorProxyCollection.Name)]
public sealed class KinesisAuthTests
{
    private readonly KinesisEmulatorProxyFixture _fixture;

    public KinesisAuthTests(KinesisEmulatorProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Invalid_aws_access_key_is_rejected()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason ?? "Docker not available.");

        using var client = _fixture.CreateClient("AKIA-INVALID", "wrong-secret");
        var ex = await Assert.ThrowsAnyAsync<AmazonServiceException>(() => client.DescribeStreamSummaryAsync(new Amazon.Kinesis.Model.DescribeStreamSummaryRequest
        {
            StreamName = KinesisEmulatorProxyFixture.StreamName,
        })).ConfigureAwait(false);
        Assert.Contains(ex.ErrorCode, new[] { "MissingAuthenticationTokenException", "AccessDeniedException", "InvalidAccessKeyId", "UnrecognizedClientException" });
    }
}
