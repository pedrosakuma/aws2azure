using System.Text.Json.Serialization;

namespace Aws2Azure.ChangeAwareValidation;

public sealed record ValidationPlan(
    int SchemaVersion,
    BaseComparison? Comparison,
    string[] ChangedPaths,
    GateDecision[] Gates,
    string[] RequiredLabels,
    string[] Warnings,
    string[] FailurePolicy)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewBinding? ReviewBinding { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceAcceptance? EvidenceAcceptance { get; init; }
}

public sealed record BaseComparison(
    string RequestedRef,
    string ResolvedRef,
    string BaseCommit,
    string MergeBase,
    string HeadCommit,
    string DiffRange,
    bool IncludesWorkingTree);

public sealed record GateDecision(
    string Name,
    string Status,
    string[] Reasons,
    string[] Commands,
    string[] Labels);

[JsonSerializable(typeof(ValidationPlan))]
[JsonSerializable(typeof(EvidenceDecision))]
[JsonSerializable(typeof(EvidenceSnapshot))]
[JsonSerializable(typeof(ReviewBinding))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
internal sealed partial class ValidationJsonContext : JsonSerializerContext;
