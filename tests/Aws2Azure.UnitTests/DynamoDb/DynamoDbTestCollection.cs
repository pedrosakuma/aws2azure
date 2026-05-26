using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Defines a collection for DynamoDB handler tests that use the static
/// <see cref="CosmosOpsShared.MetadataCache"/>. Tests in this collection
/// run sequentially to avoid cache interference.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class DynamoDbTestCollection : ICollectionFixture<DynamoDbCacheFixture>
{
    public const string Name = "DynamoDB Handler Tests";
}

/// <summary>
/// Fixture that clears the metadata cache before each test collection run.
/// </summary>
public class DynamoDbCacheFixture
{
    public DynamoDbCacheFixture()
    {
        CosmosOpsShared.MetadataCache.Clear();
    }
}
