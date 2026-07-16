using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.S3.Errors;

namespace Aws2Azure.UnitTests.S3;

public class S3ErrorMappingTests
{
    [Theory]
    [InlineData(404, "ContainerNotFound", 404, "NoSuchBucket")]
    [InlineData(404, "BlobNotFound", 404, "NoSuchKey")]
    [InlineData(412, "ConditionNotMet", 412, "PreconditionFailed")]
    [InlineData(416, "InvalidRange", 416, "InvalidRange")]
    [InlineData(400, "InvalidHeaderValue", 400, "InvalidArgument")]
    [InlineData(409, "ContainerAlreadyExists", 409, "BucketAlreadyOwnedByYou")]
    [InlineData(409, "ContainerBeingDeleted", 409, "OperationAborted")]
    [InlineData(400, "InvalidResourceName", 400, "InvalidBucketName")]
    [InlineData(400, "OutOfRangeInput", 400, "InvalidBucketName")]
    [InlineData(403, "AuthenticationFailed", 403, "AccessDenied")]
    [InlineData(408, null, 400, "RequestTimeout")]
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

    // issue #237: path-style lookup bucket-name classification. Only the 3-63
    // length rule yields 400 InvalidBucketName; length-legal but Azure-illegal
    // names (uppercase, '_', '.', leading '.', "--") resolve to 404 NoSuchBucket
    // because no such Azure container can exist; valid container names proceed.
    [Theory]
    [InlineData(null, 400, "InvalidBucketName")]
    [InlineData("", 400, "InvalidBucketName")]
    [InlineData("a", 400, "InvalidBucketName")]
    [InlineData("ab", 400, "InvalidBucketName")]
    [InlineData("this-bucket-name-is-way-too-long-to-be-valid-because-it-exceeds-63", 400, "InvalidBucketName")]
    [InlineData("conformance_invalid_bucket", 404, "NoSuchBucket")]
    [InlineData("Conformance-Invalid", 404, "NoSuchBucket")]
    [InlineData("bad..name", 404, "NoSuchBucket")]
    [InlineData(".badname", 404, "NoSuchBucket")]
    [InlineData("bad--name", 404, "NoSuchBucket")]
    public void ClassifyLookupBucketName_splits_invalid_from_nonexistent(
        string? bucket, int expectedStatus, string expectedCode)
    {
        var mapping = S3ErrorMapping.ClassifyLookupBucketName(bucket);
        Assert.NotNull(mapping);
        Assert.Equal(expectedStatus, mapping!.Value.StatusCode);
        Assert.Equal(expectedCode, mapping.Value.Code);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("my-valid-bucket")]
    [InlineData("bucket123")]
    public void ClassifyLookupBucketName_returns_null_for_azure_addressable_names(string bucket)
    {
        Assert.Null(S3ErrorMapping.ClassifyLookupBucketName(bucket));
    }
}
