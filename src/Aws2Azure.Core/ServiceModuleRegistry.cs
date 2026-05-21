using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
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

            // For modules that opt in (AWS-JSON services with bounded bodies)
            // we buffer the request body up front so we can compute the SHA-256
            // ourselves. Modern SDKs always send x-amz-content-sha256, but the
            // SigV4 spec allows omitting it for non-S3 services — and a client
            // doing so still signs with the real hash. Without buffering, the
            // ResolvePayloadHash sentinel makes those valid signatures fail.
            string? bufferedHash = null;
            if (module.BuffersRequestBodyForSigV4
                && !context.Request.Headers.ContainsKey(SigV4Constants.AmzContentSha256Header)
                && !context.Request.Query.ContainsKey(SigV4Constants.AmzSignatureQuery))
            {
                var bufferResult = await BufferAndHashRequestBodyAsync(context).ConfigureAwait(false);
                if (bufferResult.TooLarge)
                {
                    await module.EmitAuthErrorAsync(context,
                        StatusCodes.Status413PayloadTooLarge,
                        code: "RequestEntityTooLarge",
                        message: $"Request body exceeds the per-module buffering limit of {MaxBufferedBodyBytes} bytes.");
                    return;
                }
                bufferedHash = bufferResult.PayloadHash;
            }

            var payloadHash = bufferedHash ?? ResolvePayloadHash(context, module);
            var sigRequest = context.BuildSigV4Request(payloadHash, s3PathStyle: module.ServiceName == "s3");
            var result = _sigV4.Validate(sigRequest);
            if (!result.IsValid)
            {
                await EmitAuthError(context, module, result);
                return;
            }

            // Enforce module-specific signed-header requirements (e.g. DynamoDB
            // dispatches on X-Amz-Target; if the client didn't sign it, the
            // header could be swapped after-signature without invalidating the
            // signature — which would let a caller redirect a signed payload
            // to a different operation).
            var required = module.RequiredSignedHeaders;
            if (required.Count > 0)
            {
                var signed = result.SignedHeaders ?? Array.Empty<string>();
                foreach (var name in required)
                {
                    var found = false;
                    for (var i = 0; i < signed.Length; i++)
                    {
                        if (string.Equals(signed[i], name, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        await module.EmitAuthErrorAsync(context,
                            StatusCodes.Status403Forbidden,
                            code: "SignatureDoesNotMatch",
                            message: $"Required header '{name}' must be included in SignedHeaders.");
                        return;
                    }
                }
            }

            // Surface the authenticated identity for downstream consumers.
            context.Items["aws2azure.accessKeyId"] = result.AccessKeyId;
        }

        await module.HandleAsync(context);
    }

    /// <summary>
    /// Upper bound on bodies the registry will buffer for pre-validation
    /// SHA-256 hashing. Generous (16 MiB) to cover DynamoDB BatchWriteItem
    /// (16 MiB) and similar AWS-JSON ops without rejecting legal traffic.
    /// </summary>
    public const int MaxBufferedBodyBytes = 16 * 1024 * 1024;

    private static async Task<(string? PayloadHash, bool TooLarge)> BufferAndHashRequestBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is 0)
        {
            // Empty body: short-circuit to the well-known empty-payload hash.
            return (SigV4Constants.EmptyPayloadSha256, false);
        }

        if (context.Request.ContentLength is long cl && cl > MaxBufferedBodyBytes)
        {
            // Reject up front so we don't accept-then-truncate.
            return (null, true);
        }

        var contentLength = context.Request.ContentLength;
        var capacity = contentLength is > 0 ? (int)contentLength.Value : 8192;
        var ms = new MemoryStream(capacity);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            int total = 0;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length))
                .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBufferedBodyBytes)
                {
                    return (null, true);
                }
                ms.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);

        // Replace the request body with a rewound copy so the module's parser
        // sees the same bytes it would have read from the original stream.
        ms.Position = 0;
        context.Request.Body = ms;

        return (Convert.ToHexStringLower(hash), false);
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
