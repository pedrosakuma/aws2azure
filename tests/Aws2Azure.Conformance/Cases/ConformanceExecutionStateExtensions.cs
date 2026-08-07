using System.Text.Json;
using System.Xml.Linq;

namespace Aws2Azure.Conformance.Cases;

/// <summary>
/// Small helpers for the multi-step happy-path plans. They let a later request
/// reuse an ARN, queue URL, pagination token, or raw JSON key emitted by an
/// earlier response without every service re-implementing the same parsing
/// boilerplate.
/// </summary>
internal static class ConformanceExecutionStateExtensions
{
    public static string RequireHeaderValue(
        this ConformanceExecutionState state,
        string stepName,
        string headerName)
    {
        var exchange = state.GetRequiredExchange(stepName);
        foreach (var header in exchange.Headers)
        {
            if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        throw new InvalidOperationException(
            $"Response from step '{stepName}' did not contain header '{headerName}'.");
    }

    public static string RequireXmlValue(
        this ConformanceExecutionState state,
        string stepName,
        string localName)
    {
        var body = state.GetRequiredExchange(stepName).Body;
        var document = XDocument.Parse(body);
        return document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value
            ?? throw new InvalidOperationException(
                $"Response body from step '{stepName}' did not contain XML element '{localName}'.");
    }

    /// <summary>
    /// Parses a <c>ListVersionsResult</c> body from an earlier <c>?versions</c>
    /// step and returns the <c>VersionId</c> of the single &lt;Version&gt;
    /// entry that does not match <paramref name="excludedVersionId"/> — used
    /// to discover the delete-marker version created by a plain DELETE on a
    /// versioned Azure container.
    /// </summary>
    public static string RequireXmlVersionIdExcluding(
        this ConformanceExecutionState state,
        string stepName,
        string excludedVersionId)
    {
        var body = state.GetRequiredExchange(stepName).Body;
        var document = XDocument.Parse(body);
        var match = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Version")
            .Select(version => version.Elements().FirstOrDefault(e => e.Name.LocalName == "VersionId")?.Value)
            .FirstOrDefault(versionId => !string.IsNullOrEmpty(versionId)
                && !string.Equals(versionId, excludedVersionId, StringComparison.Ordinal));

        return match
            ?? throw new InvalidOperationException(
                $"Response body from step '{stepName}' did not contain a Version entry other than '{excludedVersionId}'.");
    }

    public static string RequireJsonString(
        this ConformanceExecutionState state,
        string stepName,
        params string[] path)
    {
        using var document = JsonDocument.Parse(state.GetRequiredExchange(stepName).Body);
        var element = TraversePath(document.RootElement, path);
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()
                ?? throw new InvalidOperationException(
                    $"JSON path '{string.Join('.', path)}' in step '{stepName}' was null."),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => throw new InvalidOperationException(
                $"JSON path '{string.Join('.', path)}' in step '{stepName}' was not a scalar value."),
        };
    }

    public static string RequireJsonRaw(
        this ConformanceExecutionState state,
        string stepName,
        params string[] path)
    {
        using var document = JsonDocument.Parse(state.GetRequiredExchange(stepName).Body);
        return TraversePath(document.RootElement, path).GetRawText();
    }

    private static JsonElement TraversePath(JsonElement current, IReadOnlyList<string> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            if (int.TryParse(segment, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                {
                    throw new InvalidOperationException(
                        $"JSON path segment '{segment}' addressed a missing array item.");
                }

                current = current[index];
                continue;
            }

            if (!current.TryGetProperty(segment, out var next))
            {
                throw new InvalidOperationException(
                    $"JSON path segment '{segment}' was not present.");
            }

            current = next;
        }

        return current;
    }
}
