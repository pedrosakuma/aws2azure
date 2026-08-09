using Aws2Azure.Conformance.DynamoDb;
using Aws2Azure.Conformance.Kinesis;
using Aws2Azure.Conformance.S3;
using Aws2Azure.Conformance.Sns;
using Aws2Azure.Conformance.Sqs;

namespace Aws2Azure.Conformance.Cases;

public sealed class ErrorMatrixBaseAddressTests
{
    private static readonly Uri ProxyS3BaseAddress = new("http://s3.127.0.0.1.nip.io:7777/");
    private static readonly Uri ProxyJsonBaseAddress = new("http://service.127.0.0.1.nip.io:7777/");

    [Fact]
    public async Task Error_matrices_use_context_base_address_when_provided()
    {
        await AssertHostAsync(S3ErrorMatrix.Cases[0], ProxyS3BaseAddress, "s3.127.0.0.1.nip.io");
        await AssertHostAsync(DynamoDbErrorMatrix.Cases[0], ProxyJsonBaseAddress, "service.127.0.0.1.nip.io");
        await AssertHostAsync(KinesisErrorMatrix.Cases[0], ProxyJsonBaseAddress, "service.127.0.0.1.nip.io");
        await AssertHostAsync(SnsErrorMatrix.Cases[0], ProxyJsonBaseAddress, "service.127.0.0.1.nip.io");
        await AssertHostAsync(SqsErrorMatrix.Cases[0], ProxyJsonBaseAddress, "service.127.0.0.1.nip.io");
    }

    [Fact]
    public async Task Error_matrices_fall_back_to_real_aws_endpoints_when_base_address_missing()
    {
        await AssertHostAsync(S3ErrorMatrix.Cases[0], null, "s3.us-east-1.amazonaws.com");
        await AssertHostAsync(DynamoDbErrorMatrix.Cases[0], null, "dynamodb.us-east-1.amazonaws.com");
        await AssertHostAsync(KinesisErrorMatrix.Cases[0], null, "kinesis.us-east-1.amazonaws.com");
        await AssertHostAsync(SnsErrorMatrix.Cases[0], null, "sns.us-east-1.amazonaws.com");
        await AssertHostAsync(SqsErrorMatrix.Cases[0], null, "sqs.us-east-1.amazonaws.com");
    }

    private static async Task AssertHostAsync(IConformanceCase testCase, Uri? baseAddress, string expectedHost)
    {
        var context = new ConformanceCaseContext(
            "AKIATESTKEY00000001",
            "test-secret-key-0000000000000000000000000001",
            baseAddress,
            SessionToken: "session-token");

        var plan = await testCase.CreatePlanAsync(context);
        using var request = await Assert.Single(plan.Steps).BuildRequestAsync(new ConformanceExecutionState(context));
        Assert.Equal(expectedHost, request.RequestUri?.Host);
        if (baseAddress is not null)
        {
            Assert.DoesNotContain("amazonaws.com", request.RequestUri?.Host ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
