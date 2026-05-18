using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handlers for slice-1 bucket-lifecycle operations:
/// <see cref="S3Operation.ListBuckets"/>, <see cref="S3Operation.CreateBucket"/>,
/// <see cref="S3Operation.DeleteBucket"/>, <see cref="S3Operation.HeadBucket"/>.
/// </summary>
internal static class BucketLifecycleHandlers
{
    public static async Task HandleAsync(
        HttpContext context,
        S3RouteResult route,
        BlobClient blob,
        CancellationToken cancellationToken)
    {
        switch (route.Operation)
        {
            case S3Operation.ListBuckets:
                await ListBucketsAsync(context, blob, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.CreateBucket:
                await CreateBucketAsync(context, blob, route.Bucket!, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.DeleteBucket:
                await DeleteBucketAsync(context, blob, route.Bucket!, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.HeadBucket:
                await HeadBucketAsync(context, blob, route.Bucket!, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await WriteErrorAsync(context, S3ErrorMapping.NotImplemented(route.Operation)).ConfigureAwait(false);
                break;
        }
    }

    private static async Task ListBucketsAsync(HttpContext context, BlobClient blob, CancellationToken cancellationToken)
    {
        // Azure container listings are segmented; iterate until NextMarker is
        // empty so S3 clients always see the full bucket set.
        var containers = new List<AzureBlobXmlReader.ContainerEntry>();
        string? marker = null;
        do
        {
            using var response = await blob.ListContainersAsync(marker, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.ListBuckets)).ConfigureAwait(false);
                return;
            }
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = AzureBlobXmlReader.ParseContainerListPage(xml);
            for (var i = 0; i < page.Containers.Count; i++)
            {
                containers.Add(page.Containers[i]);
            }
            marker = page.NextMarker;
        } while (!string.IsNullOrEmpty(marker));

        var buckets = new S3XmlWriter.BucketInfo[containers.Count];
        for (var i = 0; i < containers.Count; i++)
        {
            buckets[i] = new S3XmlWriter.BucketInfo(containers[i].Name, containers[i].LastModified);
        }

        // Slice 1 has no notion of "AWS account identity", so synthesize a
        // stable owner from the authenticated AWS access key.
        var accessKey = context.Items["aws2azure.accessKeyId"] as string ?? "aws2azure";
        var owner = new S3XmlWriter.OwnerInfo(accessKey, accessKey);

        var body = S3XmlWriter.ListAllMyBucketsResult(owner, buckets);
        await WriteXmlAsync(context, StatusCodes.Status200OK, body).ConfigureAwait(false);
    }

    private static async Task CreateBucketAsync(HttpContext context, BlobClient blob, string bucket, CancellationToken cancellationToken)
    {
        if (!BlobClient.IsValidContainerName(bucket))
        {
            await WriteErrorAsync(context, S3ErrorMapping.InvalidBucketName()).ConfigureAwait(false);
            return;
        }

        using var response = await blob.CreateContainerAsync(bucket, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.CreateBucket)).ConfigureAwait(false);
            return;
        }

        // S3 returns 200 OK with empty body and a Location header pointing
        // at the new bucket. The Location is host-relative since we lack
        // per-account host configuration in slice 1.
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["Location"] = "/" + bucket;
        context.Response.ContentLength = 0;
    }

    private static async Task DeleteBucketAsync(HttpContext context, BlobClient blob, string bucket, CancellationToken cancellationToken)
    {
        if (!BlobClient.IsValidContainerName(bucket))
        {
            await WriteErrorAsync(context, S3ErrorMapping.InvalidBucketName()).ConfigureAwait(false);
            return;
        }

        using var response = await blob.DeleteContainerAsync(bucket, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.DeleteBucket)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.ContentLength = 0;
    }

    private static async Task HeadBucketAsync(HttpContext context, BlobClient blob, string bucket, CancellationToken cancellationToken)
    {
        if (!BlobClient.IsValidContainerName(bucket))
        {
            // HEAD must never carry a body — use the header-only emitter even
            // for local validation failures so SDKs can still see x-amz-error-code.
            await EmitHeadErrorAsync(context, S3ErrorMapping.InvalidBucketName()).ConfigureAwait(false);
            return;
        }

        using var response = await blob.GetContainerPropertiesAsync(bucket, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // HEAD must not have a body; mirror S3 which returns just the
            // status + x-amz-request-id with an empty body on 404.
            await EmitHeadErrorAsync(context, S3ErrorMapping.FromAzure(response, S3Operation.HeadBucket)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = 0;
    }

    private static Task WriteXmlAsync(HttpContext context, int statusCode, string body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/xml; charset=utf-8";
        return context.Response.WriteAsync(body);
    }

    private static Task WriteErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping) =>
        AwsErrorResponse.WriteAsync(
            context,
            AwsErrorFormat.Xml,
            mapping.StatusCode,
            mapping.Code,
            mapping.Message);

    private static Task EmitHeadErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping)
    {
        context.Response.StatusCode = mapping.StatusCode;
        context.Response.Headers["x-amz-request-id"] = context.TraceIdentifier;
        // S3 surfaces the error code in a header on HEAD so SDKs can map it.
        context.Response.Headers["x-amz-error-code"] = mapping.Code;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}
