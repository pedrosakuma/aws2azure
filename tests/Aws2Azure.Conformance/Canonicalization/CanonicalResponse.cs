using System.Text;

namespace Aws2Azure.Conformance.Canonicalization;

/// <summary>
/// A normalized, implementation-independent view of an HTTP error response,
/// reduced to the surface AWS clients actually contract on: the HTTP status,
/// the AWS-semantic headers, and the structural fields of the error envelope.
///
/// Non-deterministic values (request ids, host ids, dates) are replaced with
/// <see cref="Masked"/> so two responses produced by different implementations
/// (the proxy vs an AWS implementation such as LocalStack) — or by the same
/// implementation on two different requests — compare equal whenever they are
/// *faithfully equivalent*. The non-contractual message wording is masked too
/// (clients switch on status + Code, never on the human-readable message).
/// </summary>
public sealed record CanonicalResponse(
    int StatusCode,
    IReadOnlyList<CanonicalField> Headers,
    string BodyKind,
    IReadOnlyList<CanonicalField> BodyFields,
    string RawBody)
{
    /// <summary>Placeholder substituted for any masked (non-deterministic) value.</summary>
    public const string Masked = "<MASKED>";

    public const string BodyKindXmlError = "xml-error";
    public const string BodyKindEmpty = "empty";
    public const string BodyKindOpaque = "opaque";

    /// <summary>
    /// Deterministic, human-diffable rendering used both for equality assertions
    /// (Tier 1) and for differential reporting (Tier 2). Stable across runs:
    /// headers and body fields are emitted in sorted order so element ordering —
    /// which AWS does not contract on — never produces a false divergence.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("HTTP ").Append(StatusCode).Append('\n');
        foreach (var h in Headers)
        {
            sb.Append("[header] ").Append(h.Name).Append(": ").Append(h.Value).Append('\n');
        }
        sb.Append("[body:").Append(BodyKind).Append("]\n");
        foreach (var f in BodyFields)
        {
            sb.Append("  ").Append(f.Name).Append(": ").Append(f.Value).Append('\n');
        }
        return sb.ToString();
    }

    public override string ToString() => Render();

    /// <summary>
    /// Reconstructs a <see cref="CanonicalResponse"/> from the text produced by
    /// <see cref="Render"/> (e.g. a golden file's body). The inverse of
    /// <see cref="Render"/> for everything except <see cref="RawBody"/>, which is
    /// not part of the canonical surface and is left empty.
    /// </summary>
    public static CanonicalResponse ParseRendered(string rendered)
    {
        int status = 0;
        var headers = new List<CanonicalField>();
        var bodyFields = new List<CanonicalField>();
        var bodyKind = BodyKindEmpty;

        using var reader = new StringReader(rendered);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith("HTTP ", StringComparison.Ordinal))
            {
                _ = int.TryParse(line.AsSpan(5).Trim(), out status);
            }
            else if (line.StartsWith("[header] ", StringComparison.Ordinal))
            {
                headers.Add(SplitField(line["[header] ".Length..]));
            }
            else if (line.StartsWith("[body:", StringComparison.Ordinal))
            {
                var end = line.IndexOf(']');
                bodyKind = end > 6 ? line[6..end] : BodyKindEmpty;
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                bodyFields.Add(SplitField(line[2..]));
            }
        }

        return new CanonicalResponse(status, headers, bodyKind, bodyFields, string.Empty);
    }

    private static CanonicalField SplitField(string text)
    {
        var idx = text.IndexOf(':');
        return idx < 0
            ? new CanonicalField(text.Trim(), string.Empty)
            : new CanonicalField(text[..idx].Trim(), text[(idx + 1)..].Trim());
    }
}

/// <summary>A single normalized name/value pair (header or body element).</summary>
public readonly record struct CanonicalField(string Name, string Value);
