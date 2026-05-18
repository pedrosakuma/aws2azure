using Aws2Azure.Modules.S3;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

public class S3RouterTests
{
    [Theory]
    [InlineData("s3.amazonaws.com", "GET", "/", S3Operation.ListBuckets, null)]
    [InlineData("s3.us-east-1.amazonaws.com", "GET", "/", S3Operation.ListBuckets, null)]
    [InlineData("s3-us-west-2.amazonaws.com", "GET", "/", S3Operation.ListBuckets, null)]
    [InlineData("s3.amazonaws.com", "HEAD", "/my-bucket", S3Operation.HeadBucket, "my-bucket")]
    [InlineData("s3.amazonaws.com", "PUT",  "/my-bucket", S3Operation.CreateBucket, "my-bucket")]
    [InlineData("s3.amazonaws.com", "DELETE", "/my-bucket", S3Operation.DeleteBucket, "my-bucket")]
    [InlineData("s3.amazonaws.com", "GET", "/my-bucket", S3Operation.Unsupported, "my-bucket")] // ListObjectsV2 later
    [InlineData("s3.amazonaws.com", "GET", "/my-bucket/key.txt", S3Operation.Unsupported, "my-bucket")]
    [InlineData("s3.amazonaws.com", "POST", "/my-bucket", S3Operation.Unknown, "my-bucket")]
    public void Path_style_classification(string host, string method, string path, S3Operation expected, string? bucket)
    {
        var ctx = BuildContext(host, method, path);
        var result = S3Router.Classify(ctx);
        Assert.Equal(expected, result.Operation);
        Assert.Equal(bucket, result.Bucket);
        Assert.False(result.VirtualHosted);
    }

    [Theory]
    [InlineData("DELETE", "/my-bucket", "tagging")]
    [InlineData("DELETE", "/my-bucket", "policy")]
    [InlineData("DELETE", "/my-bucket", "lifecycle")]
    [InlineData("PUT",    "/my-bucket", "acl")]
    [InlineData("PUT",    "/my-bucket", "cors")]
    [InlineData("PUT",    "/my-bucket", "versioning")]
    public void Bucket_subresource_queries_are_unsupported(string method, string path, string subresource)
    {
        var ctx = BuildContext("s3.amazonaws.com", method, path, query: "?" + subresource);
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.Unsupported, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
    }

    [Fact]
    public void Benign_query_does_not_trigger_subresource_block()
    {
        var ctx = BuildContext("s3.amazonaws.com", "PUT", "/my-bucket", query: "?x-id=CreateBucket");
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.CreateBucket, result.Operation);
    }

    [Theory]
    [InlineData("my-bucket.s3.amazonaws.com", "/", "my-bucket")]
    [InlineData("my-bucket.s3.us-east-1.amazonaws.com", "/key.txt", "my-bucket")]
    [InlineData("my-bucket.s3-us-west-2.amazonaws.com", "/", "my-bucket")]
    public void Virtual_hosted_style_is_detected(string host, string path, string bucket)
    {
        var ctx = BuildContext(host, "GET", path);
        var result = S3Router.Classify(ctx);
        Assert.True(result.VirtualHosted);
        Assert.Equal(bucket, result.Bucket);
    }

    private static DefaultHttpContext BuildContext(string host, string method, string path, string? query = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (!string.IsNullOrEmpty(query))
        {
            ctx.Request.QueryString = new QueryString(query);
        }
        return ctx;
    }
}
