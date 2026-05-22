using System;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis;

/// <summary>
/// Kinesis Data Streams → Azure Event Hubs module. Phase-4 Slice 1
/// lands routing + AWS-JSON-1.1 parsing + AAD/SAS credential gating;
/// every recognised operation currently dispatches to a stub handler
/// that returns <c>InternalFailure</c> with HTTP 501. Slices 2-7
/// replace stub handlers with real Event Hubs translations.
/// </summary>
public sealed class KinesisServiceModule : IServiceModule
{
    private readonly ICredentialResolver _credentials;

    public KinesisServiceModule(
        ICredentialResolver credentials,
        CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _credentials = credentials;
        Capabilities = capabilities;
    }

    public string ServiceName => "kinesis";
    public bool RequiresSigV4 => true;
    public bool BuffersRequestBodyForSigV4 => true;
    // Dispatch is keyed on X-Amz-Target — refuse signatures that don't cover it
    // so the operation can't be tampered with after-signature.
    public IReadOnlyList<string> RequiredSignedHeaders { get; } = new[] { "x-amz-target" };
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Json;
    public CapabilityMatrix Capabilities { get; }

    public ValueTask EmitAuthErrorAsync(HttpContext context, int statusCode, string code, string message)
        => new(KinesisErrorResponse.WriteAsync(context, statusCode, code, message));

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        return host.StartsWith("kinesis.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("kinesis-", StringComparison.OrdinalIgnoreCase)
            || host.Equals("kinesis", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var parsed = await KinesisWireProtocolParser.ParseAsync(context, context.RequestAborted)
            .ConfigureAwait(false);

        if (parsed.Error is not null)
        {
            await KinesisErrorResponse.WriteAsync(context,
                parsed.Error.StatusCode, parsed.Error.Code, parsed.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        var accessKey = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrEmpty(accessKey))
        {
            await KinesisErrorResponse.WriteAsync(context,
                StatusCodes.Status403Forbidden,
                "MissingAuthenticationTokenException",
                "Request is missing AWS credentials.").ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKey, AzureService.EventHubs) is not EventHubsCredentials)
        {
            await KinesisErrorResponse.WriteAsync(context,
                StatusCodes.Status403Forbidden,
                "AccessDeniedException",
                "No Event Hubs credentials configured for the supplied AWS access key.").ConfigureAwait(false);
            return;
        }

        // Slice 1: every recognised op is a 501 stub. Slices 2-7 will
        // replace the catch-all with per-op dispatch.
        await StubHandlers.HandleNotImplementedAsync(context, parsed.Operation).ConfigureAwait(false);
    }
}
