using System.Text;
using Aws2Azure.Conformance.Canonicalization;

namespace Aws2Azure.Conformance.Goldens;

/// <summary>
/// Shared text format for canonical conformance captures. Both replay goldens
/// and real-Azure evidence are intentionally plain text: a tiny
/// <c># key: value</c> metadata header followed by the verbatim
/// <see cref="CanonicalResponse.Render"/> payload so PR review can diff them
/// directly.
/// </summary>
internal static class CanonicalCaptureFileFormat
{
    public static string Serialize(
        string artifactKind,
        CanonicalResponse response,
        GoldenProvenance provenance)
    {
        var sb = new StringBuilder();
        sb.Append("# aws2azure conformance ").Append(artifactKind).Append('\n');
        sb.Append("# source: ").Append(provenance.Source).Append('\n');
        sb.Append("# operation: ").Append(provenance.Operation).Append('\n');
        sb.Append("# captured: ").Append(provenance.CapturedAtUtc.ToString("O")).Append('\n');
        if (!string.IsNullOrEmpty(provenance.Note))
        {
            sb.Append("# note: ").Append(provenance.Note).Append('\n');
        }
        sb.Append("# ---\n");
        sb.Append(response.Render());
        return sb.ToString();
    }

    public static GoldenFile Parse(string text)
    {
        string source = "unknown";
        string operation = "unknown";
        DateTimeOffset captured = default;
        string? note = null;
        var body = new StringBuilder();

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("# ---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var kv = line[2..];
                var idx = kv.IndexOf(':');
                if (idx > 0)
                {
                    var key = kv[..idx].Trim();
                    var value = kv[(idx + 1)..].Trim();
                    switch (key)
                    {
                        case "source":
                            source = value;
                            break;
                        case "operation":
                            operation = value;
                            break;
                        case "captured":
                            _ = DateTimeOffset.TryParse(value, out captured);
                            break;
                        case "note":
                            note = value;
                            break;
                    }
                }

                continue;
            }

            body.Append(line).Append('\n');
        }

        return new GoldenFile(
            new GoldenProvenance(source, operation, captured, note),
            body.ToString());
    }
}
