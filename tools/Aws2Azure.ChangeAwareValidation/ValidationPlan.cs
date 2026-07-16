using System.Text.Json.Serialization;

namespace Aws2Azure.ChangeAwareValidation;

public sealed record ValidationPlan(
    int SchemaVersion,
    BaseComparison? Comparison,
    string[] ChangedPaths,
    GateDecision[] Gates,
    string[] RequiredLabels,
    string[] Warnings,
    string[] FailurePolicy);

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
internal sealed partial class ValidationJsonContext : JsonSerializerContext;
