using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// xUnit collection to serialize SQS AMQP handler tests.
/// These tests spin up in-process broker simulators that, under
/// specific parallel execution ordering, can exhibit rare race
/// conditions (see issue #123).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqsAmqpTestCollection : ICollectionFixture<SqsAmqpTestCollection.Fixture>
{
    public const string Name = "SqsAmqp";

    public sealed class Fixture { }
}
