namespace Aws2Azure.ChangeAwareValidation;

public sealed record ReviewBinding(
    string ToolVersion,
    string BaseCommit,
    string MergeBase,
    string HeadCommit,
    string DiffSha256,
    string OriginalPlanSha256,
    string? EvidenceSourceCommit,
    string? EvidenceDeltaSha256);

public sealed record EvidenceDecision(
    int SchemaVersion,
    string Status,
    string Gate,
    string Mode,
    ReviewBinding Binding,
    string[] OriginalReasons,
    string Rationale,
    string ApprovedBy,
    string ApprovalReference,
    DateTimeOffset ReviewedAtUtc,
    DateTimeOffset ValidUntilUtc,
    string EvidencePath,
    string EvidenceSha256);

public sealed record EvidenceSnapshot(
    int SchemaVersion,
    string Kind,
    string Repository,
    string SourceCommit,
    string Scope,
    string VerifiedBy,
    string VerificationReference,
    DateTimeOffset VerifiedAtUtc,
    bool Revoked,
    OfflineVerification? Offline,
    RunEvidence? Run);

public sealed record OfflineVerification(
    string Verifier,
    string VerifierVersion,
    string Command,
    string Result,
    EvidenceFile Report);

public sealed record EvidenceFile(string Path, string Sha256);

public sealed record RunEvidence(
    long RunId,
    int Attempt,
    string WorkflowPath,
    string Event,
    string Status,
    string Conclusion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset CompletedAtUtc,
    RunArtifact[] Artifacts);

public sealed record RunArtifact(
    long Id,
    string Name,
    DateTimeOffset ExpiresAtUtc,
    bool Expired,
    EvidenceFile Archive);

public sealed record EvidenceAcceptance(
    string Gate,
    string Disposition,
    string DecisionSha256,
    EvidenceDecision Decision,
    EvidenceSnapshot Evidence,
    bool NewlyExecuted,
    bool LiveProvenanceVerified,
    string TrustBoundary);
