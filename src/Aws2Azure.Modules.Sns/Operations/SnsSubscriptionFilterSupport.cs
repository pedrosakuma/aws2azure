using System.Globalization;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.Sns.Management;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsSubscriptionFilterSupport
{
    internal const string DefaultRuleName = "$Default";
    private const string AttributePropertyPrefix = "aws2azure_sns_attr_";
    private const string AttributeNumericPropertySuffix = "_num";
    private const string BodyPropertyPrefix = "aws2azure_sns_body_";
    private const int MaxSqlExpressionLength = 1024;

    public static bool TryBuildRuleDescription(
        SnsSubscriptionMetadata metadata,
        out ServiceBusSubscriptionRuleDescription description,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        description = new ServiceBusSubscriptionRuleDescription(DefaultRuleName, null);
        error = null;

        if (string.IsNullOrWhiteSpace(metadata.FilterPolicyJson))
        {
            return true;
        }

        var scope = string.IsNullOrWhiteSpace(metadata.FilterPolicyScope)
            ? SnsSubscriptionMetadata.MessageAttributesScope
            : metadata.FilterPolicyScope!;

        if (!string.Equals(scope, SnsSubscriptionMetadata.MessageAttributesScope, StringComparison.Ordinal)
            && !string.Equals(scope, SnsSubscriptionMetadata.MessageBodyScope, StringComparison.Ordinal))
        {
            error = "Attribute 'FilterPolicyScope' must be 'MessageAttributes' or 'MessageBody'.";
            return false;
        }

        if (!TryCompileSqlExpression(metadata.FilterPolicyJson!, scope, out var sqlExpression, out error))
        {
            return false;
        }

        description = new ServiceBusSubscriptionRuleDescription(DefaultRuleName, sqlExpression);
        return true;
    }

    public static void AddFilterProperties(
        Dictionary<string, object?> applicationProperties,
        IReadOnlyList<SnsMessageAttribute> messageAttributes,
        string messageBody)
    {
        ArgumentNullException.ThrowIfNull(applicationProperties);
        ArgumentNullException.ThrowIfNull(messageAttributes);

        AddAttributeFilterProperties(applicationProperties, messageAttributes);
        AddBodyFilterProperties(applicationProperties, messageBody);
    }

    private static void AddAttributeFilterProperties(
        Dictionary<string, object?> applicationProperties,
        IReadOnlyList<SnsMessageAttribute> messageAttributes)
    {
        for (var i = 0; i < messageAttributes.Count; i++)
        {
            var attribute = messageAttributes[i];
            var value = attribute.StringValue ?? attribute.BinaryValue;
            if (value is null)
            {
                continue;
            }

            applicationProperties[BuildAttributePropertyName(attribute.Name)] = value;
            if (attribute.StringValue is not null
                && attribute.DataType.StartsWith("Number", StringComparison.OrdinalIgnoreCase)
                && TryParseJsonNumber(attribute.StringValue, out var numericValue))
            {
                applicationProperties[BuildAttributeNumericPropertyName(attribute.Name)] = numericValue;
            }
        }
    }

    private static void AddBodyFilterProperties(Dictionary<string, object?> applicationProperties, string messageBody)
    {
        if (string.IsNullOrWhiteSpace(messageBody))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(messageBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var path = new List<string>(4);
            AddBodyFilterPropertiesRecursive(document.RootElement, path, applicationProperties);
        }
        catch (JsonException)
        {
        }
    }

    private static void AddBodyFilterPropertiesRecursive(
        JsonElement element,
        List<string> path,
        Dictionary<string, object?> applicationProperties)
    {
        foreach (var property in element.EnumerateObject())
        {
            path.Add(property.Name);
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    AddBodyFilterPropertiesRecursive(property.Value, path, applicationProperties);
                    break;
                case JsonValueKind.String:
                    applicationProperties[BuildBodyPropertyName(path)] = property.Value.GetString()!;
                    break;
                case JsonValueKind.Number:
                    if (TryGetNumericValue(property.Value, out var numericValue))
                    {
                        applicationProperties[BuildBodyPropertyName(path)] = numericValue;
                    }

                    break;
                case JsonValueKind.True:
                    applicationProperties[BuildBodyPropertyName(path)] = true;
                    break;
                case JsonValueKind.False:
                    applicationProperties[BuildBodyPropertyName(path)] = false;
                    break;
            }

            path.RemoveAt(path.Count - 1);
        }
    }

    private static bool TryCompileSqlExpression(
        string filterPolicyJson,
        string scope,
        out string sqlExpression,
        out string? error)
    {
        sqlExpression = string.Empty;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(filterPolicyJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Attribute 'FilterPolicy' must be a JSON object.";
                return false;
            }

            var clauses = new List<string>();
            if (string.Equals(scope, SnsSubscriptionMetadata.MessageAttributesScope, StringComparison.Ordinal))
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!TryCompileLeafClause(
                            BuildAttributePropertyName(property.Name),
                            BuildAttributeNumericPropertyName(property.Name),
                            property.Name,
                            property.Value,
                            allowNestedObjects: false,
                            clauses,
                            out error))
                    {
                        return false;
                    }
                }
            }
            else
            {
                var path = new List<string>(4);
                if (!TryCompileBodyClauses(document.RootElement, path, clauses, out error))
                {
                    return false;
                }
            }

            if (clauses.Count == 0)
            {
                error = "Attribute 'FilterPolicy' must contain at least one supported property.";
                return false;
            }

            sqlExpression = string.Join(" AND ", clauses);
            if (sqlExpression.Length > MaxSqlExpressionLength)
            {
                error = $"Translated subscription filter exceeds the Azure Service Bus SQL filter limit of {MaxSqlExpressionLength} characters.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "Attribute 'FilterPolicy' must contain valid JSON.";
            return false;
        }
    }

    private static bool TryCompileBodyClauses(
        JsonElement objectElement,
        List<string> path,
        List<string> clauses,
        out string? error)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            path.Add(property.Name);
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (!TryCompileBodyClauses(property.Value, path, clauses, out error))
                {
                    path.RemoveAt(path.Count - 1);
                    return false;
                }
            }
            else
            {
                if (!TryCompileLeafClause(
                        BuildBodyPropertyName(path),
                        BuildBodyPropertyName(path),
                        string.Join('.', path),
                        property.Value,
                        allowNestedObjects: true,
                        clauses,
                        out error))
                {
                    path.RemoveAt(path.Count - 1);
                    return false;
                }
            }

            path.RemoveAt(path.Count - 1);
        }

        error = null;
        return true;
    }

    private static bool TryCompileLeafClause(
        string scalarPropertyName,
        string numericPropertyName,
        string propertyDisplayName,
        JsonElement propertyValue,
        bool allowNestedObjects,
        List<string> clauses,
        out string? error)
    {
        if (propertyValue.ValueKind != JsonValueKind.Array)
        {
            error = allowNestedObjects
                ? $"FilterPolicy property '{propertyDisplayName}' must be an array of conditions or a nested JSON object."
                : $"FilterPolicy property '{propertyDisplayName}' must be an array of conditions.";
            return false;
        }

        var matchers = new List<string>();
        foreach (var item in propertyValue.EnumerateArray())
        {
            if (!TryCompileMatcher(scalarPropertyName, numericPropertyName, propertyDisplayName, item, matchers, out error))
            {
                return false;
            }
        }

        if (matchers.Count == 0)
        {
            error = $"FilterPolicy property '{propertyDisplayName}' must contain at least one supported matcher.";
            return false;
        }

        clauses.Add("(" + string.Join(" OR ", matchers) + ")");
        error = null;
        return true;
    }

    private static bool TryCompileMatcher(
        string scalarPropertyName,
        string numericPropertyName,
        string propertyDisplayName,
        JsonElement matcher,
        List<string> compiledMatchers,
        out string? error)
    {
        switch (matcher.ValueKind)
        {
            case JsonValueKind.String:
                compiledMatchers.Add($"{scalarPropertyName} = {FormatStringLiteral(matcher.GetString()!)}");
                error = null;
                return true;
            case JsonValueKind.Number:
                if (!TryGetNumericLiteral(matcher, out var numericLiteral))
                {
                    error = $"FilterPolicy numeric matcher for '{propertyDisplayName}' is not supported.";
                    return false;
                }

                compiledMatchers.Add($"{numericPropertyName} = {numericLiteral}");
                error = null;
                return true;
            case JsonValueKind.True:
                compiledMatchers.Add($"{numericPropertyName} = true");
                error = null;
                return true;
            case JsonValueKind.False:
                compiledMatchers.Add($"{numericPropertyName} = false");
                error = null;
                return true;
            case JsonValueKind.Object:
                return TryCompileOperatorMatcher(scalarPropertyName, numericPropertyName, propertyDisplayName, matcher, compiledMatchers, out error);
            default:
                error = $"FilterPolicy matcher for '{propertyDisplayName}' uses an unsupported JSON kind '{matcher.ValueKind}'.";
                return false;
        }
    }

    private static bool TryCompileOperatorMatcher(
        string scalarPropertyName,
        string numericPropertyName,
        string propertyDisplayName,
        JsonElement matcher,
        List<string> compiledMatchers,
        out string? error)
    {
        if (matcher.TryGetProperty("prefix", out var prefix))
        {
            if (prefix.ValueKind != JsonValueKind.String)
            {
                error = $"FilterPolicy prefix matcher for '{propertyDisplayName}' must be a string.";
                return false;
            }

            compiledMatchers.Add($"{scalarPropertyName} LIKE {FormatStringLiteral(EscapeLikePattern(prefix.GetString()!) + "%")}");
            error = null;
            return true;
        }

        if (matcher.TryGetProperty("exists", out var exists))
        {
            if (exists.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = $"FilterPolicy exists matcher for '{propertyDisplayName}' must be true or false.";
                return false;
            }

            compiledMatchers.Add(exists.ValueKind == JsonValueKind.True
                ? $"{scalarPropertyName} IS NOT NULL"
                : $"{scalarPropertyName} IS NULL");
            error = null;
            return true;
        }

        if (matcher.TryGetProperty("anything-but", out var anythingBut))
        {
            if (!TryCompileAnythingButMatcher(scalarPropertyName, numericPropertyName, propertyDisplayName, anythingBut, out var expression, out error))
            {
                return false;
            }

            compiledMatchers.Add(expression);
            return true;
        }

        if (matcher.TryGetProperty("numeric", out var numeric))
        {
            if (!TryCompileNumericMatcher(numericPropertyName, propertyDisplayName, numeric, out var expression, out error))
            {
                return false;
            }

            compiledMatchers.Add(expression);
            return true;
        }

        error = $"FilterPolicy matcher for '{propertyDisplayName}' uses an unsupported operator.";
        return false;
    }

    private static bool TryCompileAnythingButMatcher(
        string scalarPropertyName,
        string numericPropertyName,
        string propertyDisplayName,
        JsonElement anythingBut,
        out string expression,
        out string? error)
    {
        var disallowed = new List<string>();
        if (anythingBut.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in anythingBut.EnumerateArray())
            {
                if (!TryCompileDisallowedValue(scalarPropertyName, numericPropertyName, propertyDisplayName, item, disallowed, out error))
                {
                    expression = string.Empty;
                    return false;
                }
            }
        }
        else
        {
            if (!TryCompileDisallowedValue(scalarPropertyName, numericPropertyName, propertyDisplayName, anythingBut, disallowed, out error))
            {
                expression = string.Empty;
                return false;
            }
        }

        if (disallowed.Count == 0)
        {
            expression = string.Empty;
            error = $"FilterPolicy anything-but matcher for '{propertyDisplayName}' must contain at least one supported scalar.";
            return false;
        }

        expression = $"({scalarPropertyName} IS NOT NULL AND {string.Join(" AND ", disallowed)})";
        error = null;
        return true;
    }

    private static bool TryCompileDisallowedValue(
        string scalarPropertyName,
        string numericPropertyName,
        string propertyDisplayName,
        JsonElement value,
        List<string> disallowed,
        out string? error)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                disallowed.Add($"{scalarPropertyName} <> {FormatStringLiteral(value.GetString()!)}");
                error = null;
                return true;
            case JsonValueKind.Number:
                if (!TryGetNumericLiteral(value, out var numericLiteral))
                {
                    error = $"FilterPolicy anything-but matcher for '{propertyDisplayName}' uses an unsupported numeric value.";
                    return false;
                }

                disallowed.Add($"{numericPropertyName} <> {numericLiteral}");
                error = null;
                return true;
            case JsonValueKind.True:
                disallowed.Add($"{numericPropertyName} <> true");
                error = null;
                return true;
            case JsonValueKind.False:
                disallowed.Add($"{numericPropertyName} <> false");
                error = null;
                return true;
            default:
                error = $"FilterPolicy anything-but matcher for '{propertyDisplayName}' uses an unsupported value.";
                return false;
        }
    }

    private static bool TryCompileNumericMatcher(
        string numericPropertyName,
        string propertyDisplayName,
        JsonElement numeric,
        out string expression,
        out string? error)
    {
        expression = string.Empty;
        if (numeric.ValueKind != JsonValueKind.Array)
        {
            error = $"FilterPolicy numeric matcher for '{propertyDisplayName}' must be an array.";
            return false;
        }

        var comparisons = new List<string>();
        using var enumerator = numeric.EnumerateArray();
        while (enumerator.MoveNext())
        {
            if (enumerator.Current.ValueKind != JsonValueKind.String)
            {
                error = $"FilterPolicy numeric matcher for '{propertyDisplayName}' must alternate operator strings and numeric values.";
                return false;
            }

            var op = enumerator.Current.GetString();
            if (!enumerator.MoveNext() || !TryGetNumericLiteral(enumerator.Current, out var numericLiteral))
            {
                error = $"FilterPolicy numeric matcher for '{propertyDisplayName}' must alternate operator strings and numeric values.";
                return false;
            }

            var normalizedOperator = op switch
            {
                "=" => "=",
                ">" => ">",
                ">=" => ">=",
                "<" => "<",
                "<=" => "<=",
                _ => null,
            };

            if (normalizedOperator is null)
            {
                error = $"FilterPolicy numeric matcher for '{propertyDisplayName}' uses unsupported operator '{op}'.";
                return false;
            }

            comparisons.Add($"{numericPropertyName} {normalizedOperator} {numericLiteral}");
        }

        if (comparisons.Count == 0)
        {
            error = $"FilterPolicy numeric matcher for '{propertyDisplayName}' must contain at least one comparison.";
            return false;
        }

        expression = $"({numericPropertyName} IS NOT NULL AND {string.Join(" AND ", comparisons)})";
        error = null;
        return true;
    }

    private static string BuildAttributePropertyName(string attributeName)
        => AttributePropertyPrefix + EncodeName(attributeName);

    private static string BuildAttributeNumericPropertyName(string attributeName)
        => AttributePropertyPrefix + EncodeName(attributeName) + AttributeNumericPropertySuffix;

    private static string BuildBodyPropertyName(IReadOnlyList<string> path)
        => BodyPropertyPrefix + EncodeName(string.Join('.', path));

    private static string EncodeName(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FormatStringLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string EscapeLikePattern(string value)
        => value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

    private static bool TryGetNumericLiteral(JsonElement element, out string numericLiteral)
    {
        numericLiteral = string.Empty;
        if (!TryGetNumericValue(element, out var numericValue))
        {
            return false;
        }

        numericLiteral = numericValue switch
        {
            long longValue => longValue.ToString(CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("R", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

        return numericLiteral.Length > 0;
    }

    private static bool TryGetNumericValue(JsonElement element, out object numericValue)
    {
        if (element.TryGetInt64(out var int64))
        {
            numericValue = int64;
            return true;
        }

        if (element.TryGetDouble(out var doubleValue) && !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue))
        {
            numericValue = doubleValue;
            return true;
        }

        numericValue = 0L;
        return false;
    }

    private static bool TryParseJsonNumber(string value, out object numericValue)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return TryGetNumericValue(document.RootElement, out numericValue);
        }
        catch (JsonException)
        {
            numericValue = 0L;
            return false;
        }
    }
}
