using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.S3.Errors;

namespace Aws2Azure.UnitTests.S3;

public class S3ErrorMappingTests
{
    [Theory]
    [InlineData(404, "ContainerNotFound", 404, "NoSuchBucket")]
    [InlineData(409, "ContainerAlreadyExists", 409, "BucketAlreadyOwnedByYou")]
    [InlineData(409, "ContainerBeingDeleted", 409, "OperationAborted")]
    [InlineData(400, "InvalidResourceName", 400, "InvalidBucketName")]
    [InlineData(400, "OutOfRangeInput", 400, "InvalidBucketName")]
    [InlineData(403, "AuthenticationFailed", 403, "AccessDenied")]
    [InlineData(429, null, 503, "SlowDown")]
    [InlineData(503, "ServerBusy", 503, "SlowDown")]
    [InlineData(503, "OperationTimedOut", 503, "ServiceUnavailable")]
    [InlineData(500, "InternalError", 500, "InternalError")]
    [InlineData(504, null, 504, "InternalError")]
    public void Maps_azure_to_s3(int azureStatus, string? azureCode, int expectedStatus, string expectedCode)
    {
        var mapping = S3ErrorMapping.MapAzure(azureStatus, azureCode, S3Operation.HeadBucket);
        Assert.Equal(expectedStatus, mapping.StatusCode);
        Assert.Equal(expectedCode, mapping.Code);
        Assert.False(string.IsNullOrWhiteSpace(mapping.Message));
    }
}
