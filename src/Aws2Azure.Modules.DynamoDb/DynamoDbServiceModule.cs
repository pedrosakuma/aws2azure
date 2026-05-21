using System;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.DynamoDb.Errors;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb;

/// <summary>
/// DynamoDB → Cosmos DB Core (SQL) module. Slice 0 wires routing,
/// AWS JSON 1.0 parsing, error rendering, and the Cosmos REST client.
/// Every operation currently surfaces a <c>NotImplemented</c> response
/// in the DynamoDB JSON envelope; per-op handlers land in Slice 1+.
///
/// <para>DynamoDB callers always use a single wire format
/// (POST <c>/</c> with <c>X-Amz-Target: DynamoDB_20120810.&lt;Op&gt;</c>
/// and a JSON body), so the module is simpler than SQS, which had to
/// negotiate between query-protocol and AWS JSON.</para>
/// </summary>
public sealed class DynamoDbServiceModule : IServiceModule
{
    private readonly AzureHttpClient _http;
    private readonly ICredentialResolver _credentials;

    public DynamoDbServiceModule(
        AzureHttpClient http,
        ICredentialResolver credentials,
        CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _http = http;
        _credentials = credentials;
        Capabilities = capabilities;
    }

    public string ServiceName => "dynamodb";
    public bool RequiresSigV4 => true;
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Json;
    public CapabilityMatrix Capabilities { get; }

    public ValueTask EmitAuthErrorAsync(HttpContext context, int statusCode, string code, string message)
        => new(DynamoDbErrorResponse.WriteAsync(context, statusCode, code, message, isProtocolLevel: true));

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        // DynamoDB endpoints: dynamodb.<region>.amazonaws.com,
        // dynamodb-fips.<region>.amazonaws.com, and the bare
        // dynamodb.<region>.api.aws variant used by some SDKs.
        return host.StartsWith("dynamodb.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("dynamodb-", StringComparison.OrdinalIgnoreCase)
            || host.Equals("dynamodb", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var parsed = await DynamoDbWireProtocolParser.ParseAsync(context, context.RequestAborted)
            .ConfigureAwait(false);

        if (parsed.Error is not null)
        {
            await DynamoDbErrorResponse.WriteAsync(context,
                parsed.Error.StatusCode, parsed.Error.Code, parsed.Error.Message,
                isProtocolLevel: true).ConfigureAwait(false);
            return;
        }

        var accessKey = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrEmpty(accessKey))
        {
            await DynamoDbErrorResponse.WriteAsync(context,
                StatusCodes.Status403Forbidden,
                "MissingAuthenticationTokenException",
                "Request is missing AWS credentials.").ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKey, AzureService.Cosmos) is not CosmosCredentials cosmos)
        {
            await DynamoDbErrorResponse.WriteAsync(context,
                StatusCodes.Status403Forbidden,
                "AccessDeniedException",
                "No Cosmos DB credentials configured for the supplied AWS access key.").ConfigureAwait(false);
            return;
        }

        // Construct per-request; the underlying AzureHttpClient + breaker are
        // shared across modules so retries/circuit-state survive instance churn.
        _ = new CosmosClient(_http, cosmos);

        // Slice 0 has no per-op handlers yet — every recognised target
        // returns NotImplemented in DynamoDB's JSON envelope so SDKs can
        // surface a clean exception.
        await DynamoDbErrorResponse.WriteAsync(context,
            StatusCodes.Status501NotImplemented,
            "InternalServerError",
            $"Operation {DynamoDbOperationNames.ToShortName(parsed.Operation)} is not yet implemented.")
            .ConfigureAwait(false);
    }
}
