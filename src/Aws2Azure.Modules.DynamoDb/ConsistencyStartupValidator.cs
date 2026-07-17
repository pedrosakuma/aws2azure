using System;

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
