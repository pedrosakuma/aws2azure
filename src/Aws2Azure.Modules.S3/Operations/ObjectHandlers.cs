using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handlers for slice-2 object operations:
/// <see cref="S3Operation.PutObject"/>, <see cref="S3Operation.GetObject"/>,
/// <see cref="S3Operation.HeadObject"/>, <see cref="S3Operation.DeleteObject"/>.
/// </summary>
internal static class ObjectHandlers
{
    public static async Task HandleAsync(
        HttpContext context,
        S3RouteResult route,
        BlobClient blob,
        CancellationToken cancellationToken)
    {
        var bucket = route.Bucket!;
        var key = route.Key!;

        if (!BlobClient.IsValidContainerName(bucket))
        {
            await EmitErrorAsync(context, S3ErrorMapping.InvalidBucketName(), route.Operation).ConfigureAwait(false);
            return;
        }
        if (!S3ObjectKey.IsValid(key))
        {
            await EmitErrorAsync(context, S3ErrorMapping.InvalidObjectKey(), route.Operation).ConfigureAwait(false);
            return;
        }

        switch (route.Operation)
        {
            case S3Operation.PutObject:
                await PutAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.GetObject:
                await GetAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.HeadObject:
                await HeadAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.DeleteObject:
                await DeleteAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static async Task PutAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        // Refuse aws-chunked uploads explicitly — decoding the AWS chunk format
        // (with or without trailing checksum) is not part of slice 2 and
        // silently forwarding the bytes would corrupt the blob.
        if (context.Request.Headers.TryGetValue("x-amz-content-sha256", out var contentSha))
        {
            foreach (var raw in contentSha)
            {
                var v = (raw ?? string.Empty).Trim();
                if (v.StartsWith("STREAMING-", StringComparison.Ordinal))
                {
                    await WriteErrorAsync(context,
                        new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                            "NotImplemented",
                            "aws2azure: aws-chunked payload uploads are not supported yet."))
                        .ConfigureAwait(false);
                    return;
                }
            }
        }

        using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(bucket, key))
        {
            Content = new StreamContent(context.Request.Body),
        };
        // The request body is a non-replayable client stream; the Azure client's
        // retry loop would otherwise have to buffer the entire upload.
        azureReq.Options.Set(Aws2Azure.Core.Azure.AzureHttpClient.NoRetryOption, true);
        azureReq.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);
        if (context.Request.ContentLength is { } len)
        {
            azureReq.Content.Headers.ContentLength = len;
        }

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.PutObject)).ConfigureAwait(false);
            return;
        }

        // S3 PUT object response is empty with ETag in the header.
        context.Response.StatusCode = StatusCodes.Status200OK;
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
        context.Response.ContentLength = 0;
    }

    private static async Task GetAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Get, blob.BuildBlobUri(bucket, key));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode && azureResp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            await WriteErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.GetObject)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = (int)azureResp.StatusCode;
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);

        if (azureResp.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            // 304 carries no body; ASP.NET will refuse a body anyway.
            return;
        }

        await using var azureStream = await azureResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await azureStream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    private static async Task HeadAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Head, blob.BuildBlobUri(bucket, key));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            await EmitHeadErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.HeadObject)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
        // HEAD must not emit a body even though Content-Length was set above.
    }

    private static async Task DeleteAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Delete, blob.BuildBlobUri(bucket, key));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (azureResp.IsSuccessStatusCode)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.ContentLength = 0;
            return;
        }

        // S3 DeleteObject is idempotent for missing blobs only — a missing
        // *container* must still surface as NoSuchBucket so callers don't
        // silently overlook a config / typo problem.
        if (azureResp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var mapping = S3ErrorMapping.FromAzure(azureResp, S3Operation.DeleteObject);
            if (mapping.Code == "NoSuchKey")
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                context.Response.ContentLength = 0;
                return;
            }
            await WriteErrorAsync(context, mapping).ConfigureAwait(false);
            return;
        }

        await WriteErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.DeleteObject)).ConfigureAwait(false);
    }

    private static Task EmitErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping, S3Operation op) =>
        op == S3Operation.HeadObject
            ? EmitHeadErrorAsync(context, mapping)
            : WriteErrorAsync(context, mapping);

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
        context.Response.Headers["x-amz-error-code"] = mapping.Code;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}
