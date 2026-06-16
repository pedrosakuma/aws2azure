using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.SecretsManager.Operations;
using Aws2Azure.Modules.SecretsManager.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager;

/// <summary>
/// Secrets Manager → Key Vault module. Routes the core Secrets Manager
/// operations to Key Vault using AAD client credentials and emits AWS-shaped
/// JSON responses so SDK callers can use the same wire contract.
/// </summary>
public sealed class SecretsManagerServiceModule : IServiceModule
{
    private readonly AzureHttpClient _http;
    private readonly ICredentialResolver _credentials;
    private readonly EntraIdTokenProvider _tokenProvider;

    public SecretsManagerServiceModule(
        AzureHttpClient http,
        ICredentialResolver credentials,
        CapabilityMatrix capabilities,
        EntraIdTokenProvider? tokenProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _http = http;
        _credentials = credentials;
        _tokenProvider = tokenProvider ?? new EntraIdTokenProvider(http);
        Capabilities = capabilities;
    }

    public string ServiceName => "secretsmanager";
    public bool RequiresSigV4 => true;
    public IReadOnlyList<string> RequiredSignedHeaders { get; } = ["x-amz-target"];
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Json;
    public IReadOnlySet<string> KnownOperations => _knownOperations;
    // Derived from the wire-protocol target table (single source of truth) so
    // the metrics allowlist cannot drift from the support/dispatch gate.
    private static readonly FrozenSet<string> _knownOperations =
        SecretsManagerOperationNames.Names.ToFrozenSet(StringComparer.Ordinal);
    public CapabilityMatrix Capabilities { get; }

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.StartsWith("secretsmanager.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("secretsmanager-", StringComparison.OrdinalIgnoreCase)
            || host.Equals("secretsmanager", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var operationName = ExtractOperationName(context);
        var operation = SecretsManagerOperationNames.Resolve(operationName);
        if (operation == SecretsManagerOperation.Unknown)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status501NotImplemented, "NotImplementedException", $"Secrets Manager operation '{operationName}' is not implemented yet.").ConfigureAwait(false);
            return;
        }

        var accessKeyId = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrWhiteSpace(accessKeyId))
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status403Forbidden, "MissingAuthenticationTokenException", "Request is missing AWS credentials.").ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKeyId, AzureService.KeyVault) is not KeyVaultCredentials keyVault)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status403Forbidden, "AccessDeniedException", "No Key Vault credentials configured for the supplied AWS access key.").ConfigureAwait(false);
            return;
        }

        var client = new KeyVaultSecretClient(_http, _tokenProvider, keyVault);
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            using var document = string.IsNullOrWhiteSpace(body) ? JsonDocument.Parse("{}") : JsonDocument.Parse(body);

            switch (operation)
            {
                case SecretsManagerOperation.GetSecretValue:
                    await GetSecretValueHandler.HandleAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case SecretsManagerOperation.CreateSecret:
                    await CreateSecretHandler.HandleAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case SecretsManagerOperation.UpdateSecret:
                    await UpdateSecretHandler.HandleAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case SecretsManagerOperation.PutSecretValue:
                    await PutSecretValueHandler.HandleAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case SecretsManagerOperation.DeleteSecret:
                    await DeleteSecretHandler.HandleAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case SecretsManagerOperation.ListSecrets:
                    await ListSecretsHandler.HandleAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case SecretsManagerOperation.DescribeSecret:
                    await DescribeSecretHandler.HandleAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
            }

            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status501NotImplemented, "NotImplementedException", $"Secrets Manager operation '{operationName}' is not implemented yet.").ConfigureAwait(false);
            return;
        }
        catch (EntraIdTokenException ex)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, (int)ex.BackendStatus, ex.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => "ThrottlingException",
                _ when ex.BackendStatus == System.Net.HttpStatusCode.ServiceUnavailable => "InternalServiceError",
                _ => "AccessDeniedException",
            }, ex.Message).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "InternalServiceError", ex.Message).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
    }

    private static string ExtractOperationName(HttpContext context)
    {
        var target = context.Request.Headers["X-Amz-Target"].ToString();
        if (string.IsNullOrWhiteSpace(target))
        {
            return context.Request.Path.Value ?? "SecretsManager";
        }

        var dot = target.LastIndexOf('.');
        return dot < 0
            ? target
            : target[(dot + 1)..];
    }

    internal static int MapStatusCode(System.Net.HttpStatusCode statusCode)
        => SecretsManagerOperationSupport.MapStatusCode(statusCode);

    internal static string MapErrorCode(System.Net.HttpStatusCode statusCode)
        => SecretsManagerOperationSupport.MapErrorCode(statusCode);
}
