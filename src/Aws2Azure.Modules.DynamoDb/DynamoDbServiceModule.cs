using System;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.DynamoDb.Errors;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb;

/// <summary>
/// DynamoDB → Cosmos DB Core (SQL) module. Routes JSON 1.0 requests
/// matched by Host to per-op handlers; unimplemented operations return
/// a DynamoDB-shaped <c>InternalServerError</c> so SDK callers surface
/// a clean exception.
/// </summary>
public sealed class DynamoDbServiceModule : IServiceModule
{
    private readonly AzureHttpClient _http;
    private readonly ICredentialResolver _credentials;
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly ILogger? _scanLogger;
    private readonly DynamoDbSettings _settings;
    private readonly SprocManager? _sprocManager;

    public DynamoDbServiceModule(
        AzureHttpClient http,
        ICredentialResolver credentials,
        CapabilityMatrix capabilities,
        DynamoDbSettings? settings = null,
        EntraIdTokenProvider? tokenProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _http = http;
        _credentials = credentials;
        _settings = settings ?? new DynamoDbSettings();
        // Token provider is only required for AAD-credentialled tenants;
        // master-key callers never touch it. Default to a self-contained
        // instance so callers that don't share Entra caches still work.
        _tokenProvider = tokenProvider ?? new EntraIdTokenProvider(http);
        // Logger is optional — when null, hot paths simply skip emission;
        // tests construct the module without a factory.
        _scanLogger = loggerFactory?.CreateLogger("Aws2Azure.Modules.DynamoDb.Scan");
        Capabilities = capabilities;

        // Create SprocManager if sprocs are enabled
        if (_settings.UseStoredProcedures != StoredProcedureMode.Disabled)
        {
            var sprocLogger = loggerFactory?.CreateLogger<SprocManager>();
            _sprocManager = sprocLogger is not null ? new SprocManager(sprocLogger) : null;
        }
        _sprocContext = new SprocContext(_settings.UseStoredProcedures, _sprocManager);
    }

    // Sproc context for atomic conditional writes
    private readonly SprocContext _sprocContext;

    public string ServiceName => "dynamodb";
    public bool RequiresSigV4 => true;
    public bool BuffersRequestBodyForSigV4 => true;
    // Dispatch is keyed on X-Amz-Target — refuse signatures that don't cover it
    // so the operation can't be tampered with after-signature.
    public IReadOnlyList<string> RequiredSignedHeaders { get; } = new[] { "x-amz-target" };
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Json;
    public CapabilityMatrix Capabilities { get; }

    public ValueTask EmitAuthErrorAsync(HttpContext context, int statusCode, string code, string message)
        => new(DynamoDbErrorResponse.WriteAsync(context, statusCode, code, message, isProtocolLevel: true));

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
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

        if (_credentials.GetAzureCredentialsFor(accessKey, AzureService.Cosmos) is not CosmosCredentials cosmosCreds)
        {
            await DynamoDbErrorResponse.WriteAsync(context,
                StatusCodes.Status403Forbidden,
                "AccessDeniedException",
                "No Cosmos DB credentials configured for the supplied AWS access key.").ConfigureAwait(false);
            return;
        }

        var auth = CreateAuthenticator(cosmosCreds);
        var cosmos = new CosmosClient(_http, cosmosCreds, auth);

        switch (parsed.Operation)
        {
            case DynamoDbOperation.CreateTable:
                await TableLifecycleHandlers.HandleCreateTableAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.DeleteTable:
                await TableLifecycleHandlers.HandleDeleteTableAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.DescribeTable:
                await TableLifecycleHandlers.HandleDescribeTableAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.ListTables:
                await TableLifecycleHandlers.HandleListTablesAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.PutItem:
                await ItemHandlers.HandlePutItemAsync(context, parsed.Body, cosmos, _sprocContext, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.GetItem:
                await ItemHandlers.HandleGetItemAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.DeleteItem:
                await ItemHandlers.HandleDeleteItemAsync(context, parsed.Body, cosmos, _sprocContext, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.UpdateItem:
                await UpdateItemHandler.HandleUpdateItemAsync(context, parsed.Body, cosmos, _sprocContext, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.Query:
                await QueryHandler.HandleQueryAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.Scan:
                await ScanHandler.HandleScanAsync(context, parsed.Body, cosmos, _scanLogger, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.BatchGetItem:
                await BatchGetItemHandler.HandleBatchGetItemAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.BatchWriteItem:
                await BatchWriteItemHandler.HandleBatchWriteItemAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.TransactGetItems:
                await TransactGetItemsHandler.HandleTransactGetItemsAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.TransactWriteItems:
                await TransactWriteItemsHandler.HandleTransactWriteItemsAsync(context, parsed.Body, cosmos, _sprocContext, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.DescribeTimeToLive:
                await TimeToLiveHandlers.HandleDescribeTimeToLiveAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.UpdateTimeToLive:
                await TimeToLiveHandlers.HandleUpdateTimeToLiveAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.TagResource:
                await TaggingHandlers.HandleTagResourceAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.UntagResource:
                await TaggingHandlers.HandleUntagResourceAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
            case DynamoDbOperation.ListTagsOfResource:
                await TaggingHandlers.HandleListTagsOfResourceAsync(context, parsed.Body, cosmos, context.RequestAborted).ConfigureAwait(false);
                return;
        }

        await DynamoDbErrorResponse.WriteAsync(context,
            StatusCodes.Status501NotImplemented,
            "InternalServerError",
            $"Operation {DynamoDbOperationNames.ToShortName(parsed.Operation)} is not yet implemented.")
            .ConfigureAwait(false);
    }

    private ICosmosAuthenticator CreateAuthenticator(CosmosCredentials creds)
    {
        // Validation already guarantees exactly one shape — guard
        // anyway so a misconfigured shadow build never silently picks
        // the wrong scheme.
        if (!string.IsNullOrEmpty(creds.PrimaryKey))
        {
            return new MasterKeyCosmosAuthenticator(creds.PrimaryKey);
        }
        return new AadCosmosAuthenticator(
            _tokenProvider,
            creds.TenantId!,
            creds.ClientId!,
            creds.ClientSecret!);
    }
}

