using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// AOT-safe source-generated logging for the DynamoDB module.
/// </summary>
internal static partial class DynamoDbLog
{
    [LoggerMessage(
        EventId = 551435922,
        Level = LogLevel.Information,
        Message = "Cosmos account {Endpoint} default consistency is {Level}; DynamoDB ConsistentRead is honored.")]
    public static partial void Honored(ILogger logger, string endpoint, string level);

    [LoggerMessage(
        EventId = 63313435,
        Level = LogLevel.Warning,
        Message = "Cosmos account {Endpoint} default consistency is {Level}; DynamoDB ConsistentRead / read-your-write will NOT be honored (Cosmos only relaxes consistency per request, never strengthens it). Set the account default to Strong.")]
    public static partial void BelowStrong(ILogger logger, string endpoint, string level);

    [LoggerMessage(
        EventId = 1492089352,
        Level = LogLevel.Warning,
        Message = "Cosmos account {Endpoint} consistency probe could not determine the default level; DynamoDB ConsistentRead may not be honored.")]
    public static partial void Indeterminate(ILogger logger, string endpoint);

    [LoggerMessage(
        EventId = 353337968,
        Level = LogLevel.Warning,
        Message = "Cosmos account {Endpoint} consistency probe failed: {Reason}")]
    public static partial void ProbeFailed(ILogger logger, string endpoint, string reason);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Cross-partition DynamoDB Scan against {Table} — Scan walks every partition and is expensive on Cosmos; prefer Query when a partition key is known.")]
    public static partial void CrossPartitionScan(ILogger logger, string table);

    [LoggerMessage(
        EventId = 393774940,
        Level = LogLevel.Information,
        Message = "Cosmos account {Endpoint} regions discovered. Readable: {ReadableLocations}. Writable: {WritableLocations}. Multi-write: {EnableMultipleWriteLocations}.")]
    public static partial void DiscoveredRegions(
        ILogger logger,
        string endpoint,
        string readableLocations,
        string writableLocations,
        bool enableMultipleWriteLocations);

    [LoggerMessage(
        EventId = 86962373,
        Level = LogLevel.Debug,
        Message = "Selected Cosmos {OperationKind} endpoint {Endpoint}.")]
    public static partial void SelectedEndpoint(ILogger logger, string operationKind, string endpoint);

    [LoggerMessage(
        EventId = 563663975,
        Level = LogLevel.Warning,
        Message = "Failing over Cosmos endpoint from {FromEndpoint} to {ToEndpoint}; trigger status {StatusCode}.")]
    public static partial void Failover(ILogger logger, string fromEndpoint, string toEndpoint, int statusCode);

    [LoggerMessage(
        EventId = 1859797362,
        Level = LogLevel.Information,
        Message = "Created atomicWrite sproc in container {ContainerName}")]
    public static partial void LogSprocCreated(ILogger logger, string containerName);

    [LoggerMessage(
        EventId = 789634110,
        Level = LogLevel.Debug,
        Message = "atomicWrite sproc already exists in container {ContainerName}")]
    public static partial void LogSprocAlreadyExists(ILogger logger, string containerName);

    [LoggerMessage(
        EventId = 1729926049,
        Level = LogLevel.Warning,
        Message = "Failed to create sproc in container {ContainerName}: HTTP {StatusCode} - {ErrorBody}")]
    public static partial void LogSprocCreateFailed(
        ILogger logger,
        string containerName,
        int statusCode,
        string errorBody);

    // Message text is deliberately just ex.Message ("Entra ID token request
    // failed with HTTP {code}."), never ex.ResponseBody — the response body can
    // carry Azure auth diagnostics and must not be logged/echoed (see
    // EntraIdTokenException.ResponseBody). Surfacing this distinguishing string
    // to the proxy's console output lets integration-real-azure.yml's transient
    // Entra 401 retry (issue #922) detect and retry failures whose synthetic
    // Cosmos response otherwise renders only as a generic DynamoDB
    // AccessDeniedException/Forbidden with no trace of the underlying cause.
    [LoggerMessage(
        EventId = 1074823881,
        Level = LogLevel.Warning,
        Message = "Cosmos request against {Endpoint} failed before it was sent: {Reason}")]
    public static partial void TokenAcquisitionFailed(ILogger logger, string endpoint, string reason);
}
