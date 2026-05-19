using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Core;

/// <summary>
/// Reflection-free registry of <see cref="IServiceModule"/> instances and the
/// pipeline that drives each request: <em>route → SigV4 → module</em>.
/// </summary>
public sealed class ServiceModuleRegistry
{
    private readonly IServiceModule[] _modules;
    private readonly SigV4Validator? _sigV4;

    public ServiceModuleRegistry(IServiceModule[] modules, SigV4Validator? sigV4Validator = null)
    {
        _modules = modules;
        _sigV4 = sigV4Validator;
    }

    public IReadOnlyList<IServiceModule> Modules => _modules;

    public IServiceModule? Resolve(string host)
    {
        foreach (var module in _modules)
        {
            if (module.MatchesHost(host))
            {
                return module;
            }
        }
        return null;
    }

    public async Task DispatchAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        var module = Resolve(host);
        if (module is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                $"aws2azure: no service module matched host '{host}'\n");
            return;
        }

        if (module.RequiresSigV4)
        {
            if (_sigV4 is null)
            {
                await module.EmitAuthErrorAsync(context,
                    StatusCodes.Status500InternalServerError,
                    code: "InternalError",
                    message: "SigV4 validator is not configured but module requires SigV4.");
                return;
            }

            var payloadHash = ResolvePayloadHash(context, module);
            var sigRequest = context.BuildSigV4Request(payloadHash, s3PathStyle: module.ServiceName == "s3");
            var result = _sigV4.Validate(sigRequest);
            if (!result.IsValid)
            {
                await EmitAuthError(context, module, result);
                return;
            }

            // Surface the authenticated identity for downstream consumers.
            context.Items["aws2azure.accessKeyId"] = result.AccessKeyId;
        }

        await module.HandleAsync(context);
    }

    private static string ResolvePayloadHash(HttpContext context, IServiceModule module)
    {
        if (context.Request.Headers.TryGetValue(SigV4Constants.AmzContentSha256Header, out var values)
            && values.Count > 0 && !string.IsNullOrEmpty(values[0]))
        {
            return values[0]!;
        }

        // Presigned URLs (X-Amz-Signature in the query) are signed against
        // UNSIGNED-PAYLOAD because the client cannot know the body hash
        // ahead of time. Per-module payload-hash strategies (buffer-and-hash,
        // streaming chunk verification, S3 multipart) land with each Phase-1+
        // module owning its body handling.
        if (context.Request.Query.ContainsKey(SigV4Constants.AmzSignatureQuery))
        {
            return SigV4Constants.UnsignedPayload;
        }

        // For signed header-based requests without a body, the empty-payload
        // hash is the correct default; for non-empty bodies a module that sets
        // RequiresSigV4=true MUST attach x-amz-content-sha256 itself (or
        // buffer + hash in middleware before dispatching). Until any module
        // does so, refuse the request rather than silently rejecting valid
        // signatures with the wrong payload hash.
        if (context.Request.ContentLength is > 0)
        {
            // Surface a deterministic value the validator will fail against,
            // making the misconfiguration obvious in logs. (No real Phase-0
            // module reaches this branch — stubs opt out of SigV4.)
            return "__aws2azure_unresolved_payload_hash__";
        }

        return SigV4Constants.EmptyPayloadSha256;
    }

    private static ValueTask EmitAuthError(HttpContext context, IServiceModule module, SigV4ValidationResult result)
    {
        var (status, code) = result.Status switch
        {
            SigV4ValidationStatus.InvalidSignature   => (StatusCodes.Status403Forbidden,   "SignatureDoesNotMatch"),
            SigV4ValidationStatus.UnknownAccessKey   => (StatusCodes.Status403Forbidden,   "InvalidAccessKeyId"),
            SigV4ValidationStatus.Expired            => (StatusCodes.Status403Forbidden,   "AccessDenied"),
            SigV4ValidationStatus.ClockSkewTooLarge  => (StatusCodes.Status403Forbidden,   "RequestTimeTooSkewed"),
            _                                        => (StatusCodes.Status400BadRequest,  "InvalidRequest"),
        };

        return module.EmitAuthErrorAsync(context, status, code, result.Reason ?? code);
    }
}
