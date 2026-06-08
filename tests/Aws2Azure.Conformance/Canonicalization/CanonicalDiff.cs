namespace Aws2Azure.Conformance.Canonicalization;

/// <summary>
/// A single structural difference between two canonical responses, carrying a
/// machine-readable <see cref="Tag"/> that gap-doc <c>behavior_differences</c>
/// can opt into accepting (see the allow-list), plus a human-readable
/// <see cref="Description"/> for the failure report.
/// </summary>
public readonly record struct Divergence(string Tag, string Description);

/// <summary>
/// Structured comparison of two <see cref="CanonicalResponse"/> values. Unlike a
/// raw string diff of <see cref="CanonicalResponse.Render"/>, this yields tagged
/// divergences so the harness can distinguish a documented, accepted gap from an
/// unexpected regression.
/// </summary>
public static class CanonicalDiff
{
    /// <param name="expected">The reference (golden / AWS) response.</param>
    /// <param name="actual">The response under test (the proxy).</param>
    public static IReadOnlyList<Divergence> Compare(
        CanonicalResponse expected, CanonicalResponse actual)
    {
        var diffs = new List<Divergence>();

        if (expected.StatusCode != actual.StatusCode)
        {
            diffs.Add(new Divergence(
                "status",
                $"HTTP status differs: expected {expected.StatusCode}, actual {actual.StatusCode}"));
        }

        if (!string.Equals(expected.BodyKind, actual.BodyKind, StringComparison.Ordinal))
        {
            diffs.Add(new Divergence(
                "body-kind",
                $"Body kind differs: expected '{expected.BodyKind}', actual '{actual.BodyKind}'"));
        }

        CompareFields(diffs, expected.Headers, actual.Headers, "header");
        CompareFields(diffs, expected.BodyFields, actual.BodyFields, "field");

        return diffs;
    }

    private static void CompareFields(
        List<Divergence> diffs,
        IReadOnlyList<CanonicalField> expected,
        IReadOnlyList<CanonicalField> actual,
        string kind)
    {
        var expectedMap = ToMap(expected);
        var actualMap = ToMap(actual);

        foreach (var (name, value) in expectedMap)
        {
            if (!actualMap.TryGetValue(name, out var actualValue))
            {
                diffs.Add(new Divergence(
                    $"missing-{kind}:{name}",
                    $"Expected {kind} '{name}' is absent in actual response"));
            }
            else if (!string.Equals(value, actualValue, StringComparison.Ordinal))
            {
                diffs.Add(new Divergence(
                    $"{kind}-value:{name}",
                    $"{kind} '{name}' value differs: expected '{value}', actual '{actualValue}'"));
            }
        }

        foreach (var (name, _) in actualMap)
        {
            if (!expectedMap.ContainsKey(name))
            {
                diffs.Add(new Divergence(
                    $"extra-{kind}:{name}",
                    $"Actual response has unexpected {kind} '{name}'"));
            }
        }
    }

    private static Dictionary<string, string> ToMap(IReadOnlyList<CanonicalField> fields)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            map[f.Name] = f.Value;
        }
        return map;
    }
}
