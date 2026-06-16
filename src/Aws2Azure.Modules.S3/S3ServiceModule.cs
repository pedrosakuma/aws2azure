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
/// S3 service module. Slice 1 implements the bucket CRUD operations
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

    public bool MatchesHost(string host) => S3Router.MatchesHost(host);

    public async ValueTask HandleAsync(HttpContext context)
    {
        var route = S3Router.Classify(context);

        if (route.VirtualHosted)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.VirtualHostedNotSupported()).ConfigureAwait(false);
            return;
        }

        var target = S3OperationDispatcher.GetTarget(route.Operation);
        if (target == S3DispatchTarget.NotImplemented)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(route.Operation)).ConfigureAwait(false);
            return;
        }

        var accessKey = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrEmpty(accessKey))
        {
            await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(
                StatusCodes.Status403Forbidden, "AccessDenied",
                "aws2azure: SigV4 must run before the S3 module dispatch.")).ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKey, AzureService.Blob) is not BlobCredentials blobCreds)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoCredentials()).ConfigureAwait(false);
            return;
        }

        var blob = new BlobClient(_http, blobCreds);
        switch (target)
        {
            case S3DispatchTarget.Object:
                await ObjectHandlers.HandleAsync(context, route, blob, context.RequestAborted, _credentials).ConfigureAwait(false);
                break;
            case S3DispatchTarget.ObjectList:
                await ObjectListHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
                break;
            case S3DispatchTarget.DeleteObjects:
                await DeleteObjectsHandler.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
                break;
            case S3DispatchTarget.Multipart:
                await MultipartHandlers.HandleAsync(context, route, blob, context.RequestAborted, _credentials).ConfigureAwait(false);
                break;
            case S3DispatchTarget.Subresource:
                await SubresourceHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
                break;
            case S3DispatchTarget.BucketCrud:
                await BucketCrudHandlers.HandleAsync(context, route, blob, context.RequestAborted).ConfigureAwait(false);
                break;
        }
    }
}
