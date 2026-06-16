using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Operations;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3;

/// <summary>
/// S3 service module. Slice 1 implements the bucket-lifecycle operations
/// (List/Create/Delete/Head) over Azure Blob Storage; other operations
/// surface a 501 NotImplemented S3-shaped error.
/// </summary>
public sealed class S3ServiceModule : IServiceModule
{
    private readonly AzureHttpClient _http;
    private readonly ICredentialResolver _credentials;

    public S3ServiceModule(AzureHttpClient http, ICredentialResolver credentials, CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _http = http;
        _credentials = credentials;
        Capabilities = capabilities;
    }

    public string ServiceName => "s3";
    public bool RequiresSigV4 => true;
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Xml;

    // S3 is the documented exception to the XML auth vocabulary: an unknown
    // access key returns the bespoke InvalidAccessKeyId/403 rather than the
    // AWS Query front door's InvalidClientTokenId. See issue #247.
    public AwsAuthErrorDialect AuthErrorDialect => AwsAuthErrorDialect.S3Xml;

    public CapabilityMatrix Capabilities { get; }

    // S3 encodes the operation in the HTTP method + path, not in an X-Amz-Target
    // header or Action query parameter, so it derives the metric operation name
    // itself rather than relying on the default KnownOperations allowlist.
    //
    // This deliberately diverges from the pre-refactor registry for one
    // unrealistic case: that code checked X-Amz-Target / Action FIRST for every
    // service and, because S3's allowlist was an unconditional `true`, returned
    // such a candidate verbatim — an unbounded-cardinality hole. Legitimate S3
    // traffic never carries either, so realistic behavior is unchanged while the
    // metric label stays bounded against crafted requests (the original intent).
    public string ExtractOperationName(HttpContext context)
        => context.Request.Method switch
        {
            "GET" when context.Request.Path.Value?.Contains('/') == true => "GetObject",
            "PUT" when context.Request.Path.Value?.Contains('/') == true => "PutObject",
            "DELETE" when context.Request.Path.Value?.Contains('/') == true => "DeleteObject",
            "HEAD" when context.Request.Path.Value?.Contains('/') == true => "HeadObject",
            _ => context.Request.Method,
        };

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }
        if (host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("s3-", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Virtual-hosted style: matched here so the request reaches us and we
        // can return an explicit, S3-shaped error pointing at path-style.
        return host.Contains(".s3.", StringComparison.OrdinalIgnoreCase)
            || host.Contains(".s3-", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var route = S3Router.Classify(context);

        if (route.VirtualHosted)
        {
            await WriteErrorAsync(context, S3ErrorMapping.VirtualHostedNotSupported()).ConfigureAwait(false);
            return;
        }

        if (route.Operation is S3Operation.Unknown or S3Operation.Unsupported)
        {
            await WriteErrorAsync(context, S3ErrorMapping.NotImplemented(route.Operation)).ConfigureAwait(false);
            return;
        }

        var accessKey = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrEmpty(accessKey))
        {
            await WriteErrorAsync(context, new S3ErrorMapping.Mapping(
                StatusCodes.Status403Forbidden, "AccessDenied",
                "aws2azure: SigV4 must run before the S3 module dispatch.")).ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKey, AzureService.Blob) is not BlobCredentials blobCreds)
        {
            await WriteErrorAsync(context, S3ErrorMapping.NoCredentials()).ConfigureAwait(false);
            return;
        }

        var blob = new BlobClient(_http, blobCreds);
        if (route.Operation is S3Operation.PutObject
            or S3Operation.GetObject
            or S3Operation.HeadObject
            or S3Operation.DeleteObject
            or S3Operation.CopyObject)
        {
            await ObjectHandlers.HandleAsync(context, route, blob, context.RequestAborted, _credentials).ConfigureAwait(false);
        }
        else if (route.Operation is S3Operation.ListObjects or S3Operation.ListObjectsV2)
        {
            await ObjectListHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
        }
        else if (route.Operation is S3Operation.DeleteObjects)
        {
            await DeleteObjectsHandler.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
        }
        else if (route.Operation is S3Operation.CreateMultipartUpload
            or S3Operation.UploadPart
            or S3Operation.UploadPartCopy
            or S3Operation.CompleteMultipartUpload
            or S3Operation.AbortMultipartUpload
            or S3Operation.ListParts
            or S3Operation.ListMultipartUploads)
        {
            await MultipartHandlers.HandleAsync(context, route, blob, context.RequestAborted, _credentials).ConfigureAwait(false);
        }
        else if (IsLongTailSubresource(route.Operation))
        {
            await SubresourceHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            await BucketLifecycleHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Phase-1 Slice-9 long-tail subresources (tagging, ACL, lifecycle/cors/
    /// website/etc. stubs, object-lock/legal-hold/retention/torrent/restore
    /// stubs). Grouped here so the central dispatcher stays a flat switch.
    /// </summary>
    private static bool IsLongTailSubresource(S3Operation op) => op is
        S3Operation.GetObjectTagging or S3Operation.PutObjectTagging or S3Operation.DeleteObjectTagging or
        S3Operation.GetBucketTagging or S3Operation.PutBucketTagging or S3Operation.DeleteBucketTagging or
        S3Operation.GetBucketAcl or S3Operation.PutBucketAcl or
        S3Operation.GetObjectAcl or S3Operation.PutObjectAcl or
        S3Operation.GetBucketLifecycleConfiguration or S3Operation.PutBucketLifecycleConfiguration or S3Operation.DeleteBucketLifecycle or
        S3Operation.GetBucketCors or S3Operation.PutBucketCors or S3Operation.DeleteBucketCors or
        S3Operation.GetBucketWebsite or S3Operation.PutBucketWebsite or S3Operation.DeleteBucketWebsite or
        S3Operation.GetBucketReplication or S3Operation.PutBucketReplication or S3Operation.DeleteBucketReplication or
        S3Operation.GetBucketEncryption or S3Operation.PutBucketEncryption or S3Operation.DeleteBucketEncryption or
        S3Operation.GetBucketLogging or S3Operation.PutBucketLogging or
        S3Operation.GetBucketVersioning or S3Operation.PutBucketVersioning or
        S3Operation.GetBucketRequestPayment or S3Operation.PutBucketRequestPayment or
        S3Operation.GetObjectLockConfiguration or S3Operation.PutObjectLockConfiguration or
        S3Operation.GetPublicAccessBlock or S3Operation.PutPublicAccessBlock or S3Operation.DeletePublicAccessBlock or
        S3Operation.GetBucketPolicy or S3Operation.PutBucketPolicy or S3Operation.DeleteBucketPolicy or
        S3Operation.GetBucketPolicyStatus or
        S3Operation.GetBucketNotificationConfiguration or S3Operation.PutBucketNotificationConfiguration or
        S3Operation.GetBucketAccelerateConfiguration or S3Operation.PutBucketAccelerateConfiguration or
        S3Operation.GetBucketOwnershipControls or S3Operation.PutBucketOwnershipControls or S3Operation.DeleteBucketOwnershipControls or
        S3Operation.GetObjectTorrent or S3Operation.RestoreObject or
        S3Operation.GetObjectRetention or S3Operation.PutObjectRetention or
        S3Operation.GetObjectLegalHold or S3Operation.PutObjectLegalHold;

    private static Task WriteErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping) =>
        AwsErrorResponse.WriteAsync(
            context,
            AwsErrorFormat.Xml,
            mapping.StatusCode,
            mapping.Code,
            mapping.Message);
}
