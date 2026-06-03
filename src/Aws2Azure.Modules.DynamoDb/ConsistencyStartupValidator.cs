using System;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb
{
    /// <summary>
    /// Thrown when the #204 startup consistency probe runs under
    /// <c>ConsistencyCheck=Required</c> and a Cosmos account cannot honor
    /// DynamoDB <c>ConsistentRead</c> (default consistency below Strong, or the
    /// level could not be determined). The proxy host
    /// catches this and exits non-zero, mirroring the configuration-validation
    /// failure path.
    /// </summary>
    public sealed class CosmosConsistencyValidationException : Exception
    {
        public CosmosConsistencyValidationException(string message) : base(message) { }
    }
}

namespace Aws2Azure.Modules.DynamoDb.Internal
{
    /// <summary>
    /// <see cref="LoggerMessage"/> source-generated log entries for the #204
    /// startup consistency probe. Kept in a dedicated partial type so the
    /// generator does not have to make the large service-module class partial.
    /// </summary>
    internal static partial class ConsistencyLog
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Cosmos account {Endpoint} default consistency is {Level}; DynamoDB ConsistentRead is honored.")]
        public static partial void Honored(ILogger logger, string endpoint, string level);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cosmos account {Endpoint} default consistency is {Level}; DynamoDB ConsistentRead / read-your-write will NOT be honored (Cosmos only relaxes consistency per request, never strengthens it). Set the account default to Strong.")]
        public static partial void BelowStrong(ILogger logger, string endpoint, string level);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cosmos account {Endpoint} consistency probe could not determine the default level; DynamoDB ConsistentRead may not be honored.")]
        public static partial void Indeterminate(ILogger logger, string endpoint);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Cosmos account {Endpoint} consistency probe failed: {Reason}")]
        public static partial void ProbeFailed(ILogger logger, string endpoint, string reason);
    }
}
