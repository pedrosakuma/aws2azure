namespace Aws2Azure.DocsEval;

/// <summary>
/// A single deterministic retrieval-evaluation case: a documentation question a
/// careful reader (or retrieval-augmented model) must answer correctly. Every
/// case names the canonical source(s) that hold the answer, the precedence that
/// must be applied when sources disagree, the conclusions that must never be
/// drawn, and a small set of mechanical <see cref="Checks"/> that verify the
/// expected answer still matches the live repository state (not an LLM call).
/// </summary>
public sealed class EvalCase
{
    /// <summary>Stable, human-assigned identifier (e.g. "adopt-s3-001"). Must be unique in the dataset.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>One of: adoption_status, configuration, operation_gaps, authentication, deployment, rollback.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Lowercase AWS service token (s3, sqs, sns, dynamodb, kinesis, secretsmanager), or empty for cross-cutting cases.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Whether this case is deliberately adversarial (stale/inconsistent bait present in the corpus).</summary>
    public bool Adversarial { get; set; }

    /// <summary>The natural-language question a retriever/model would be asked.</summary>
    public string Question { get; set; } = string.Empty;

    public ExpectedAnswer ExpectedAnswer { get; set; } = new();

    /// <summary>Conclusions the answer must NOT draw, even though a superficial reading might suggest them.</summary>
    public List<string> ProhibitedConclusions { get; set; } = new();

    /// <summary>Mechanical, deterministic checks that verify the expected answer still holds against the current repo.</summary>
    public List<EvalCheck> Checks { get; set; } = new();
}

public sealed class ExpectedAnswer
{
    /// <summary>Short canonical answer summary (not graded verbatim by the deterministic gate; documents intent for a model benchmark).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Repository-relative paths (or generated-page paths) that are the canonical source(s) for this answer.</summary>
    public List<string> CanonicalSources { get; set; } = new();

    /// <summary>The precedence rule that must be applied, in prose (e.g. "workload-ga.json overrides historical release notes").</summary>
    public string Precedence { get; set; } = string.Empty;
}

public sealed class EvalCheck
{
    /// <summary>
    /// Check kind. One of: profile_verdict, operation_status, schema_path_exists,
    /// schema_canonical_value_exists, source_exists, text_contains, finding_disposition,
    /// operation_reference_exists.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    // profile_verdict
    public string? ProfileId { get; set; }
    public string? ExpectedVerdict { get; set; }

    // operation_status / finding_disposition / operation_reference_exists
    public string? Service { get; set; }
    public string? Operation { get; set; }
    public string? ExpectedStatus { get; set; }

    // finding_disposition
    public string? Subject { get; set; }
    public string? Code { get; set; }
    public string? ExpectedDisposition { get; set; }

    // schema_path_exists
    public string? SchemaPath { get; set; }

    // schema_canonical_value_exists
    public string? CanonicalValue { get; set; }

    // schema_path_exists / schema_canonical_value_exists / source_exists / text_contains
    public bool? ExpectedExists { get; set; }

    // source_exists / text_contains
    public string? Path { get; set; }

    // text_contains
    public string? MustContain { get; set; }
}

public sealed class EvalDataset
{
    public int SchemaVersion { get; set; }
    public List<EvalCase> Cases { get; set; } = new();
}
