using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Fixtures that reset SqsQueueMetadataCache must share this collection:
/// setup/teardown clears the process-wide cache, including other fixtures'
/// remembered writes. Disable parallelization with other collections too,
/// because SQS handlers can use the cache indirectly (see issue #1049).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqsQueueMetadataTestCollection
{
    public const string Name = "SqsQueueMetadata";
}
