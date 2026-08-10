using Aws2Azure.Conformance.DynamoDb;
using Aws2Azure.Conformance.Kinesis;
using Aws2Azure.Conformance.S3;
using Aws2Azure.Conformance.SecretsManager;
using Aws2Azure.Conformance.Sns;
using Aws2Azure.Conformance.Sqs;

namespace Aws2Azure.Conformance.Cases;

/// <summary>
/// Service-qualified lookup entry for a conformance case. The offline Tier-3
/// diff iterates this catalog so every service matrix — error and happy-path —
/// participates through one shared abstraction.
/// </summary>
public sealed record ConformanceCaseDescriptor(string Service, IConformanceCase Case);

/// <summary>
/// Aggregates every currently declared conformance case across services. This is
/// intentionally the single fan-in point for future capture, evidence-export,
/// and credential-free diff jobs so new cases become visible to all three by
/// default.
/// </summary>
public static class ConformanceCaseCatalog
{
    public static IReadOnlyList<ConformanceCaseDescriptor> All { get; } =
    [
        .. ForService("s3", S3ErrorMatrix.Cases),
        .. ForService("s3", S3HappyPathMatrix.Cases),
        .. ForService("dynamodb", DynamoDbErrorMatrix.Cases),
        .. ForService("dynamodb", DynamoDbHappyPathMatrix.Cases),
        .. ForService("kinesis", KinesisErrorMatrix.Cases),
        .. ForService("kinesis", KinesisHappyPathMatrix.Cases),
        .. ForService("sns", SnsErrorMatrix.Cases),
        .. ForService("sns", SnsHappyPathMatrix.Cases),
        .. ForService("sqs", SqsErrorMatrix.Cases),
        .. ForService("sqs", SqsHappyPathMatrix.Cases),
        .. ForService("secretsmanager", SecretsManagerHappyPathMatrix.Cases),
    ];

    public static ConformanceCaseDescriptor Get(string service, string caseName) =>
        All.Single(entry =>
            string.Equals(entry.Service, service, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Case.Name, caseName, StringComparison.Ordinal));

    private static IEnumerable<ConformanceCaseDescriptor> ForService(
        string service,
        IEnumerable<IConformanceCase> cases) =>
        cases.Select(testCase => new ConformanceCaseDescriptor(service, testCase));
}
