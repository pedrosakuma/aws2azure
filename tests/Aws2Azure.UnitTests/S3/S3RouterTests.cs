using Aws2Azure.Modules.S3;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

public class S3RouterTests
{
    [Fact]
    public void Every_known_s3_operation_is_wired_to_a_dispatch_target()
    {
        foreach (var operation in Enum.GetValues<S3Operation>())
        {
            var target = S3OperationDispatcher.GetTarget(operation);

            if (operation is S3Operation.Unknown or S3Operation.Unsupported)
            {
                Assert.Equal(S3DispatchTarget.NotImplemented, target);
            }
            else
            {
                Assert.NotEqual(S3DispatchTarget.NotImplemented, target);
            }
        }
    }

    [Theory]
    [InlineData("s3.amazonaws.com", "GET", "/", S3Operation.ListBuckets, null)]
    [InlineData("s3.us-east-1.amazonaws.com", "GET", "/", S3Operation.ListBuckets, null)]
    [InlineData("s3-us-west-2.amazonaws.com", "GET", "/", S3Operation.ListBuckets, null)]
    [InlineData("s3.amazonaws.com", "HEAD", "/my-bucket", S3Operation.HeadBucket, "my-bucket")]
    [InlineData("s3.amazonaws.com", "PUT",  "/my-bucket", S3Operation.CreateBucket, "my-bucket")]
    [InlineData("s3.amazonaws.com", "DELETE", "/my-bucket", S3Operation.DeleteBucket, "my-bucket")]
    [InlineData("s3.amazonaws.com", "GET", "/my-bucket", S3Operation.ListObjects, "my-bucket")]
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
    [InlineData("DELETE", "/my-bucket", "tagging", S3Operation.DeleteBucketTagging)]
    [InlineData("DELETE", "/my-bucket", "policy", S3Operation.DeleteBucketPolicy)]
    [InlineData("DELETE", "/my-bucket", "lifecycle", S3Operation.DeleteBucketLifecycle)]
    [InlineData("PUT",    "/my-bucket", "acl", S3Operation.PutBucketAcl)]
    [InlineData("PUT",    "/my-bucket", "cors", S3Operation.PutBucketCors)]
    [InlineData("PUT",    "/my-bucket", "versioning", S3Operation.PutBucketVersioning)]
    [InlineData("GET",    "/my-bucket", "tagging", S3Operation.GetBucketTagging)]
    [InlineData("GET",    "/my-bucket", "lifecycle", S3Operation.GetBucketLifecycleConfiguration)]
    [InlineData("GET",    "/my-bucket", "cors", S3Operation.GetBucketCors)]
    [InlineData("GET",    "/my-bucket", "website", S3Operation.GetBucketWebsite)]
    [InlineData("GET",    "/my-bucket", "replication", S3Operation.GetBucketReplication)]
    [InlineData("GET",    "/my-bucket", "encryption", S3Operation.GetBucketEncryption)]
    [InlineData("GET",    "/my-bucket", "logging", S3Operation.GetBucketLogging)]
    [InlineData("GET",    "/my-bucket", "versioning", S3Operation.GetBucketVersioning)]
    [InlineData("GET",    "/my-bucket", "requestPayment", S3Operation.GetBucketRequestPayment)]
    [InlineData("GET",    "/my-bucket", "object-lock", S3Operation.GetObjectLockConfiguration)]
    [InlineData("GET",    "/my-bucket", "publicAccessBlock", S3Operation.GetPublicAccessBlock)]
    [InlineData("GET",    "/my-bucket", "policy", S3Operation.GetBucketPolicy)]
    [InlineData("GET",    "/my-bucket", "policyStatus", S3Operation.GetBucketPolicyStatus)]
    [InlineData("GET",    "/my-bucket", "notification", S3Operation.GetBucketNotificationConfiguration)]
    [InlineData("GET",    "/my-bucket", "accelerate", S3Operation.GetBucketAccelerateConfiguration)]
    [InlineData("GET",    "/my-bucket", "ownershipControls", S3Operation.GetBucketOwnershipControls)]
    [InlineData("GET",    "/my-bucket", "acl", S3Operation.GetBucketAcl)]
    public void Bucket_subresource_queries_classify_to_dedicated_op(string method, string path, string subresource, S3Operation expected)
    {
        var ctx = BuildContext("s3.amazonaws.com", method, path, query: "?" + subresource);
        var result = S3Router.Classify(ctx);
        Assert.Equal(expected, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
    }

    [Theory]
    [InlineData("GET",    "tagging", S3Operation.GetObjectTagging)]
    [InlineData("PUT",    "tagging", S3Operation.PutObjectTagging)]
    [InlineData("DELETE", "tagging", S3Operation.DeleteObjectTagging)]
    [InlineData("GET",    "acl", S3Operation.GetObjectAcl)]
    [InlineData("PUT",    "acl", S3Operation.PutObjectAcl)]
    [InlineData("GET",    "torrent", S3Operation.GetObjectTorrent)]
    [InlineData("POST",   "restore", S3Operation.RestoreObject)]
    [InlineData("GET",    "legal-hold", S3Operation.GetObjectLegalHold)]
    [InlineData("PUT",    "legal-hold", S3Operation.PutObjectLegalHold)]
    [InlineData("GET",    "retention", S3Operation.GetObjectRetention)]
    [InlineData("PUT",    "retention", S3Operation.PutObjectRetention)]
    public void Object_subresource_queries_classify_to_dedicated_op(string method, string subresource, S3Operation expected)
    {
        var ctx = BuildContext("s3.amazonaws.com", method, "/my-bucket/key.txt", query: "?" + subresource);
        var result = S3Router.Classify(ctx);
        Assert.Equal(expected, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
        Assert.Equal("key.txt", result.Key);
    }

    [Theory]
    [InlineData("PUT",    "/my-bucket/key.txt", S3Operation.PutObject)]
    [InlineData("GET",    "/my-bucket/key.txt", S3Operation.GetObject)]
    [InlineData("HEAD",   "/my-bucket/key.txt", S3Operation.HeadObject)]
    [InlineData("DELETE", "/my-bucket/key.txt", S3Operation.DeleteObject)]
    [InlineData("GET",    "/my-bucket/deep/folder/key.txt", S3Operation.GetObject)]
    public void Object_path_classification(string method, string path, S3Operation expected)
    {
        var ctx = BuildContext("s3.amazonaws.com", method, path);
        var result = S3Router.Classify(ctx);
        Assert.Equal(expected, result.Operation);
    }

    [Fact]
    public void Put_with_x_amz_copy_source_routes_to_CopyObject()
    {
        var ctx = BuildContext("s3.amazonaws.com", "PUT", "/dest-bucket/dest.txt");
        ctx.Request.Headers["x-amz-copy-source"] = "/src-bucket/src.txt";
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.CopyObject, result.Operation);
        Assert.Equal("dest-bucket", result.Bucket);
        Assert.Equal("dest.txt", result.Key);
    }

    [Fact]
    public void Put_without_copy_source_stays_PutObject()
    {
        var ctx = BuildContext("s3.amazonaws.com", "PUT", "/dest-bucket/dest.txt");
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.PutObject, result.Operation);
    }

    [Fact]
    public void Post_to_bucket_with_delete_query_routes_to_DeleteObjects()
    {
        var ctx = BuildContext("s3.amazonaws.com", "POST", "/my-bucket", query: "?delete");
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.DeleteObjects, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
        Assert.Null(result.Key);
    }

    [Fact]
    public void Post_to_bucket_without_delete_query_is_Unknown()
    {
        var ctx = BuildContext("s3.amazonaws.com", "POST", "/my-bucket");
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.Unknown, result.Operation);
    }

    [Theory]
    [InlineData("DELETE", "/b/k.txt", "versionId=abc")]
    [InlineData("PUT",    "/b/k.txt", "uploads")]
    [InlineData("GET",    "/b/k.txt", "uploadId=xyz&partNumber=1")]
    [InlineData("GET",    "/b/k.txt", "attributes")]
    public void Object_subresource_queries_are_unsupported(string method, string path, string query)
    {
        var ctx = BuildContext("s3.amazonaws.com", method, path, query: "?" + query);
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.Unsupported, result.Operation);
        Assert.Equal("b", result.Bucket);
        Assert.Equal("k.txt", result.Key);
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

    [Theory]
    [InlineData("?list-type=2", S3Operation.ListObjectsV2)]
    [InlineData("?list-type=2&prefix=foo/", S3Operation.ListObjectsV2)]
    [InlineData("?prefix=foo/", S3Operation.ListObjects)]
    [InlineData("?list-type=1", S3Operation.ListObjects)]
    [InlineData("", S3Operation.ListObjects)]
    public void Get_on_bucket_routes_by_list_type(string query, S3Operation expected)
    {
        var ctx = BuildContext("s3.amazonaws.com", "GET", "/my-bucket", query: query);
        var result = S3Router.Classify(ctx);
        Assert.Equal(expected, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
    }

    [Theory]
    [InlineData("POST",   "/my-bucket/key.txt", "?uploads",                         S3Operation.CreateMultipartUpload)]
    [InlineData("PUT",    "/my-bucket/key.txt", "?uploadId=ABC&partNumber=3",        S3Operation.UploadPart)]
    [InlineData("POST",   "/my-bucket/key.txt", "?uploadId=ABC",                     S3Operation.CompleteMultipartUpload)]
    [InlineData("DELETE", "/my-bucket/key.txt", "?uploadId=ABC",                     S3Operation.AbortMultipartUpload)]
    [InlineData("GET",    "/my-bucket/key.txt", "?uploadId=ABC",                     S3Operation.ListParts)]
    [InlineData("GET",    "/my-bucket/key.txt", "?uploadId=ABC&max-parts=10",        S3Operation.ListParts)]
    public void Multipart_object_subresources_are_routed(string method, string path, string query, S3Operation expected)
    {
        var ctx = BuildContext("s3.amazonaws.com", method, path, query: query);
        var result = S3Router.Classify(ctx);
        Assert.Equal(expected, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
        Assert.Equal("key.txt", result.Key);
    }

    [Theory]
    // Wrong verb for ?uploads (PUT/GET/HEAD) → recognised subresource but unroutable.
    [InlineData("PUT",    "/my-bucket/key.txt", "?uploads")]
    [InlineData("GET",    "/my-bucket/key.txt", "?uploads")]
    // partNumber without uploadId is nonsensical.
    [InlineData("PUT",    "/my-bucket/key.txt", "?partNumber=1")]
    public void Multipart_invalid_combos_are_unsupported(string method, string path, string query)
    {
        var ctx = BuildContext("s3.amazonaws.com", method, path, query: query);
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.Unsupported, result.Operation);
    }

    [Fact]
    public void Get_on_bucket_with_uploads_routes_to_ListMultipartUploads()
    {
        var ctx = BuildContext("s3.amazonaws.com", "GET", "/my-bucket", query: "?uploads");
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.ListMultipartUploads, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
    }

    [Fact]
    public void Put_part_with_x_amz_copy_source_routes_to_UploadPartCopy()
    {
        var ctx = BuildContext("s3.amazonaws.com", "PUT", "/my-bucket/key.txt", query: "?uploadId=ABC&partNumber=3");
        ctx.Request.Headers["x-amz-copy-source"] = "/src-bucket/src.txt";
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.UploadPartCopy, result.Operation);
        Assert.Equal("my-bucket", result.Bucket);
        Assert.Equal("key.txt", result.Key);
    }

    [Fact]
    public void Put_part_with_empty_copy_source_header_routes_to_UploadPart()
    {
        // Empty header MUST NOT promote the request to a copy; otherwise a
        // misconfigured SDK would silently turn a body upload into a copy of
        // an undefined source.
        var ctx = BuildContext("s3.amazonaws.com", "PUT", "/my-bucket/key.txt", query: "?uploadId=ABC&partNumber=3");
        ctx.Request.Headers["x-amz-copy-source"] = "";
        var result = S3Router.Classify(ctx);
        Assert.Equal(S3Operation.UploadPart, result.Operation);
    }

    [Fact]
    public void Get_on_bucket_with_uploads_and_prefix_routes_to_ListMultipartUploads()
    {
        var ctx = BuildContext("s3.amazonaws.com", "GET", "/my-bucket", query: "?uploads&prefix=foo/");
        Assert.Equal(S3Operation.ListMultipartUploads, S3Router.Classify(ctx).Operation);
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
