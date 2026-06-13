using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// AOT-safe source-generated logging for Cosmos region discovery, routing and
/// failover decisions.
/// </summary>
internal static partial class CosmosRegionLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Cosmos account {Endpoint} regions discovered. Readable: {ReadableLocations}. Writable: {WritableLocations}. Multi-write: {EnableMultipleWriteLocations}.")]
    public static partial void DiscoveredRegions(
        ILogger logger,
        string endpoint,
        string readableLocations,
        string writableLocations,
        bool enableMultipleWriteLocations);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Selected Cosmos {OperationKind} endpoint {Endpoint}.")]
    public static partial void SelectedEndpoint(ILogger logger, string operationKind, string endpoint);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failing over Cosmos endpoint from {FromEndpoint} to {ToEndpoint}; trigger status {StatusCode}.")]
    public static partial void Failover(ILogger logger, string fromEndpoint, string toEndpoint, int statusCode);
}
