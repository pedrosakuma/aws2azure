using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.SecretsManager;

/// <summary>
/// Seed Secrets Manager happy-path matrix for issue #708. Secrets Manager maps
/// to Azure Key Vault secrets, so — unlike SNS/Kinesis/SQS, whose success paths
/// need a live Service Bus/Event Hubs backend — a real round-trip additionally
/// depends on the exact Key Vault version/stage reconciliation documented in
/// <c>docs/gaps/secretsmanager/*.yaml</c> (see GetSecretValue.yaml and
/// PutSecretValue.yaml for the AWSCURRENT/token semantics asserted below). The
/// current Tier-1 fixture has no Key Vault oracle, so the cases are presently
/// deferred after plan validation, matching every other service's happy-path
/// seed matrix.
/// </summary>
public static class SecretsManagerHappyPathMatrix
{
    private static readonly Uri DefaultBaseAddress = new("http://secretsmanager.us-east-1.amazonaws.com/");

    private const string Tier1SkipReason =
        "Tier-1 Secrets Manager happy-path replay is deferred by issue #708: SecretsManagerConformanceFixture " +
        "uses a dummy Key Vault client-secret credential and cannot complete a real secret CRUD round-trip offline.";

    public static IReadOnlyList<IConformanceCase> Cases { get; } =
    [
        CreateRoundTripCase(),
        CreateDescribeCase(),
        CreatePaginationCase(),
    ];

    private static PlannedConformanceCase CreateRoundTripCase()
        => new(
            "create-get-update-delete-secret-roundtrip",
            "secretsmanager:CreateSecret/GetSecretValue/UpdateSecret/GetSecretValue/DeleteSecret",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("ARN", "Returned by CreateSecret."), new("VersionId", "Initial AWSCURRENT version id.")]),
                new(200, RequiredBodyAssertions: [new("SecretString", "Matches the value written by CreateSecret.")]),
                new(200, RequiredBodyAssertions: [new("VersionId", "New AWSCURRENT version id published by UpdateSecret.")]),
                new(200, RequiredBodyAssertions: [new("SecretString", "Matches the value written by UpdateSecret."), new("VersionId", "Differs from the VersionId observed after CreateSecret.")]),
                new(200),
            ],
            semanticAssertion:
            "GetSecretValue must return the CreateSecret payload before the update and the UpdateSecret payload " +
            "after it, with the AWSCURRENT VersionId changing between the two reads (see GetSecretValue.yaml's " +
            "ClientRequestToken/AWSCURRENT resolution semantics)."),
            static (context, _) =>
            {
                // Distinct property key from CreateDescribeCase below: DeleteSecret
                // (called at the end of this case) schedules the secret for
                // deletion rather than removing it immediately, so reusing the
                // same secret name across sequential cases would make the next
                // case's CreateSecret fail with "already scheduled for
                // deletion" even though the cases never run concurrently.
                var secretName = context.GetProperty("secretName") ?? ("conf-happy-secret-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-secret", _ => BuildRequest(context, "CreateSecret",
                        // Real AWS strictly requires ClientRequestToken on
                        // CreateSecret for a non-SDK caller (the SDKs auto-fill
                        // it as an idempotency token; this harness sends raw
                        // wire-protocol JSON, so nothing does that for us). A
                        // fresh UUID is generated per invocation (not hoisted
                        // to a case-level constant) so a retried/re-captured
                        // request never replays a stale token that could
                        // collide with AWS-side idempotency caching across
                        // real-AWS runs.
                        $$"""{"Name":"{{secretName}}","SecretString":"conformance-initial-value","ClientRequestToken":"{{Guid.NewGuid()}}"}""")),
                    new ConformanceRequestStep("get-secret-value-initial", _ => BuildRequest(context, "GetSecretValue",
                        $$"""{"SecretId":"{{secretName}}"}""")),
                    new ConformanceRequestStep("update-secret", _ => BuildRequest(context, "UpdateSecret",
                        // Real AWS also strictly requires ClientRequestToken on
                        // UpdateSecret for a raw (non-SDK) caller, same as
                        // CreateSecret above. A fresh UUID per invocation, never
                        // a fixed constant, avoids idempotency collisions on
                        // repeated real-AWS captures.
                        $$"""{"SecretId":"{{secretName}}","SecretString":"conformance-updated-value","ClientRequestToken":"{{Guid.NewGuid()}}"}""")),
                    new ConformanceRequestStep("get-secret-value-updated", _ => BuildRequest(context, "GetSecretValue",
                        $$"""{"SecretId":"{{secretName}}"}""")),
                    new ConformanceRequestStep("delete-secret", _ => BuildRequest(context, "DeleteSecret",
                        $$"""{"SecretId":"{{secretName}}"}""")),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateDescribeCase()
        => new(
            "describe-secret-roundtrip",
            "secretsmanager:CreateSecret/DescribeSecret/DeleteSecret",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("ARN", "Returned by CreateSecret.")]),
                new(200, RequiredBodyAssertions: [
                    new("Name", "Matches the name passed to CreateSecret."),
                    new("ARN", "Matches the ARN returned by CreateSecret."),
                    new("VersionIdsToStages", "Contains the AWSCURRENT version created by CreateSecret."),
                ]),
                new(200),
            ],
            semanticAssertion:
            "DescribeSecret must report the same Name/ARN as CreateSecret and expose the AWSCURRENT version in " +
            "VersionIdsToStages before the secret is deleted."),
            static (context, _) =>
            {
                // Distinct property key from CreateRoundTripCase above: both cases
                // read a "secretName"-shaped property, and reusing the identical
                // key/value would make this case's CreateSecret collide with the
                // roundtrip case's DeleteSecret (which only schedules deletion,
                // not an immediate removal) even though the two cases run
                // sequentially rather than concurrently.
                var secretName = context.GetProperty("describeSecretName") ?? ("conf-happy-describe-" + Guid.NewGuid().ToString("N")[..12]);
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-secret", _ => BuildRequest(context, "CreateSecret",
                        // See CreateRoundTripCase above: real AWS requires
                        // ClientRequestToken on a raw (non-SDK) CreateSecret call.
                        $$"""{"Name":"{{secretName}}","SecretString":"conformance-describe-value","ClientRequestToken":"{{Guid.NewGuid()}}"}""")),
                    new ConformanceRequestStep("describe-secret", _ => BuildRequest(context, "DescribeSecret",
                        $$"""{"SecretId":"{{secretName}}"}""")),
                    new ConformanceRequestStep("delete-secret", _ => BuildRequest(context, "DeleteSecret",
                        $$"""{"SecretId":"{{secretName}}"}""")),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePaginationCase()
        => new(
            "list-secrets-pagination",
            "secretsmanager:CreateSecret/ListSecrets/DeleteSecret",
            ConformanceCaseExpectation.Success(
            [
                new(200, RequiredBodyAssertions: [new("ARN", "Returned by the first CreateSecret.")]),
                new(200, RequiredBodyAssertions: [new("ARN", "Returned by the second CreateSecret.")]),
                new(200, RequiredBodyAssertions: [new("NextToken", "Present because MaxResults=1 truncates the first page (see ListSecrets.yaml's $skiptoken mapping).")]),
                new(200, RequiredBodyAssertions: [new("SecretList", "Returns the remaining secret(s) on the follow-up page.")]),
                new(200),
                new(200),
            ],
            semanticAssertion:
            "Across both pages the harness should observe both secrets created in this run exactly once, matching " +
            "the MaxResults/NextToken -> Key Vault maxresults/$skiptoken mapping documented in ListSecrets.yaml."),
            static (context, _) =>
            {
                var runId = Guid.NewGuid().ToString("N")[..12];
                var secretNameOne = context.GetProperty("secretNameOne") ?? $"conf-happy-list-{runId}-1";
                var secretNameTwo = context.GetProperty("secretNameTwo") ?? $"conf-happy-list-{runId}-2";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-secret-1", _ => BuildRequest(context, "CreateSecret",
                        // See CreateRoundTripCase above: real AWS requires
                        // ClientRequestToken on a raw (non-SDK) CreateSecret call.
                        $$"""{"Name":"{{secretNameOne}}","SecretString":"conformance-list-value-1","ClientRequestToken":"{{Guid.NewGuid()}}"}""")),
                    new ConformanceRequestStep("create-secret-2", _ => BuildRequest(context, "CreateSecret",
                        $$"""{"Name":"{{secretNameTwo}}","SecretString":"conformance-list-value-2","ClientRequestToken":"{{Guid.NewGuid()}}"}""")),
                    new ConformanceRequestStep("list-secrets-page-1", _ => BuildRequest(context, "ListSecrets",
                        """{"MaxResults":1}""")),
                    new ConformanceRequestStep("list-secrets-page-2", state =>
                    {
                        var token = state.RequireJsonString("list-secrets-page-1", "NextToken");
                        return BuildRequest(context, "ListSecrets",
                            $$"""{"NextToken":"{{token}}","MaxResults":1}""");
                    }),
                    new ConformanceRequestStep("delete-secret-1", _ => BuildRequest(context, "DeleteSecret",
                        $$"""{"SecretId":"{{secretNameOne}}"}""")),
                    new ConformanceRequestStep("delete-secret-2", _ => BuildRequest(context, "DeleteSecret",
                        $$"""{"SecretId":"{{secretNameTwo}}"}""")),
                ], Tier1SkipReason));
            });

    private static HttpRequestMessage BuildRequest(
        ConformanceCaseContext context,
        string operation,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ResolveBaseAddress(context), "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-amz-json-1.1");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", "secretsmanager." + operation);
        ConformanceSigV4Signer.SignHeader(
            request,
            bytes,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            service: "secretsmanager",
            extraSignedHeaders: ["x-amz-target"],
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
