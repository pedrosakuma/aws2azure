using System.Text.Json.Nodes;

namespace Aws2Azure.DocsEval;

/// <summary>
/// Resolves dotted field paths (e.g. "bindings.azure.s3.target.endpoint") against
/// the canonical <c>config.schema.json</c> JSON Schema document, walking
/// <c>$ref</c>, <c>oneOf</c>/<c>anyOf</c> branches, array <c>items</c>, and
/// <c>additionalProperties</c> maps. Used to mechanically prove that a
/// configuration field a dataset case cites (or a fabricated field a dataset
/// case deliberately invents) does or does not exist in the real schema.
/// </summary>
public static class SchemaPathResolver
{
    public static bool PathExists(JsonObject schemaRoot, string dottedPath)
    {
        var segments = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = ResolveRef(schemaRoot, schemaRoot);
        foreach (var segment in segments)
        {
            current = DescendArray(current, schemaRoot);
            var next = TryDescendProperty(current, segment, schemaRoot);
            if (next is null)
            {
                return false;
            }
            current = next;
        }
        return true;
    }

    /// <summary>
    /// True if any schema node anywhere in the document declares
    /// <c>"x-canonical-value": "&lt;value&gt;"</c> (the marker the config
    /// generator emits for discriminator/enum-like literal values, e.g. auth
    /// <c>mode</c> or backend <c>kind</c>).
    /// </summary>
    public static bool CanonicalValueExists(JsonObject schemaRoot, string value)
    {
        return ContainsCanonicalValue(schemaRoot, value, new HashSet<JsonObject>(ReferenceEqualityComparer.Instance));
    }

    private static bool ContainsCanonicalValue(JsonNode? node, string value, HashSet<JsonObject> visited)
    {
        switch (node)
        {
            case JsonObject obj:
                if (!visited.Add(obj))
                {
                    return false;
                }
                if (obj.TryGetPropertyValue("x-canonical-value", out var canonical)
                    && canonical is JsonValue canonicalValue
                    && canonicalValue.TryGetValue(out string? canonicalString)
                    && string.Equals(canonicalString, value, StringComparison.Ordinal))
                {
                    return true;
                }
                foreach (var (_, child) in obj)
                {
                    if (ContainsCanonicalValue(child, value, visited))
                    {
                        return true;
                    }
                }
                return false;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (ContainsCanonicalValue(child, value, visited))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static JsonObject? TryDescendProperty(JsonObject? current, string segment, JsonObject schemaRoot)
    {
        if (current is null)
        {
            return null;
        }

        foreach (var branch in ExpandBranches(current, schemaRoot))
        {
            if (branch.TryGetPropertyValue("properties", out var propertiesNode)
                && propertiesNode is JsonObject properties
                && properties.TryGetPropertyValue(segment, out var propertySchema)
                && propertySchema is JsonObject propertyObject)
            {
                return ResolveRef(propertyObject, schemaRoot);
            }

            if (branch.TryGetPropertyValue("additionalProperties", out var additional)
                && additional is JsonObject additionalObject)
            {
                return ResolveRef(additionalObject, schemaRoot);
            }
        }

        return null;
    }

    private static JsonObject? DescendArray(JsonObject? current, JsonObject schemaRoot)
    {
        if (current is null)
        {
            return null;
        }

        foreach (var branch in ExpandBranches(current, schemaRoot))
        {
            if (branch.TryGetPropertyValue("type", out var typeNode)
                && typeNode is JsonValue typeValue
                && typeValue.TryGetValue(out string? typeString)
                && string.Equals(typeString, "array", StringComparison.Ordinal)
                && branch.TryGetPropertyValue("items", out var itemsNode)
                && itemsNode is JsonObject itemsObject)
            {
                return ResolveRef(itemsObject, schemaRoot);
            }
        }

        return current;
    }

    /// <summary>Expands `oneOf`/`anyOf` into every non-null candidate branch, resolving `$ref` on each.</summary>
    private static IEnumerable<JsonObject> ExpandBranches(JsonObject schema, JsonObject schemaRoot)
    {
        var resolved = ResolveRef(schema, schemaRoot);
        if (resolved is null)
        {
            yield break;
        }

        var combinators = new[] { "oneOf", "anyOf" };
        var hasCombinator = false;
        foreach (var combinatorName in combinators)
        {
            if (resolved.TryGetPropertyValue(combinatorName, out var combinatorNode)
                && combinatorNode is JsonArray combinatorArray)
            {
                hasCombinator = true;
                foreach (var candidate in combinatorArray)
                {
                    if (candidate is not JsonObject candidateObject)
                    {
                        continue;
                    }
                    var candidateResolved = ResolveRef(candidateObject, schemaRoot);
                    if (candidateResolved is null || IsNullType(candidateResolved))
                    {
                        continue;
                    }
                    foreach (var branch in ExpandBranches(candidateResolved, schemaRoot))
                    {
                        yield return branch;
                    }
                }
            }
        }

        if (!hasCombinator)
        {
            yield return resolved;
        }
    }

    private static bool IsNullType(JsonObject schema) =>
        schema.TryGetPropertyValue("type", out var typeNode)
        && typeNode is JsonValue typeValue
        && typeValue.TryGetValue(out string? typeString)
        && string.Equals(typeString, "null", StringComparison.Ordinal);

    private static JsonObject? ResolveRef(JsonObject? schema, JsonObject schemaRoot)
    {
        if (schema is null)
        {
            return null;
        }

        if (!schema.TryGetPropertyValue("$ref", out var refNode)
            || refNode is not JsonValue refValue
            || !refValue.TryGetValue(out string? refString))
        {
            return schema;
        }

        const string prefix = "#/$defs/";
        if (!refString.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported $ref shape: {refString}");
        }

        var defName = refString[prefix.Length..];
        if (!schemaRoot.TryGetPropertyValue("$defs", out var defsNode)
            || defsNode is not JsonObject defs
            || !defs.TryGetPropertyValue(defName, out var target)
            || target is not JsonObject targetObject)
        {
            throw new InvalidOperationException($"Unresolvable $ref: {refString}");
        }

        return targetObject;
    }
}
