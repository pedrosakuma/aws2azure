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
    public CapabilityMatrix Capabilities { get; }

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
            await ObjectHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
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
            await MultipartHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            await BucketLifecycleHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping) =>
        AwsErrorResponse.WriteAsync(
            context,
            AwsErrorFormat.Xml,
            mapping.StatusCode,
            mapping.Code,
            mapping.Message);
}
