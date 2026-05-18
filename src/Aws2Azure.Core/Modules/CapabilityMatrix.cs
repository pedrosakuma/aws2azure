using System.Text.Json.Serialization;

namespace Aws2Azure.Core.Modules;

/// <summary>
/// Declarative coverage statement for a service module. Hand-authored in
/// Phase 0; will be generated from the YAML gap docs (issue #6) once that
/// pipeline lands.
/// </summary>
public sealed record CapabilityMatrix(
    string ServiceName,
    IReadOnlyList<OperationCapability> Operations);

public sealed record OperationCapability(
    string Name,
    OperationStatus Status,
    IReadOnlyList<string>? Notes = null);

[JsonConverter(typeof(JsonStringEnumConverter<OperationStatus>))]
public enum OperationStatus
{
    /// <summary>Returns a stub response; no real translation.</summary>
    Stub,
    /// <summary>Subset of the operation works; gap doc lists what's missing.</summary>
    Partial,
    /// <summary>Operation works for the documented surface.</summary>
    Implemented,
    /// <summary>Explicitly out of scope.</summary>
    Unsupported,
}
