using System;
using System.Text;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// Translates a DynamoDB <see cref="DocumentPath"/> into a Cosmos DB
/// SQL property-access expression rooted at a caller-supplied alias
/// (defaults to <c>c</c>). Attribute names are always emitted in
/// bracket-string form (<c>c["name"]</c>) — this is the only form that
/// safely handles arbitrary DDB attribute names (which may contain
/// dots, hyphens, spaces, quotes, backslashes, Unicode, etc.) without
/// risking SQL-identifier ambiguity or injection. Indexes are emitted
/// as <c>[N]</c>.
/// </summary>
/// <remarks>
/// The translator yields the path to the <em>bare</em> stored value.
/// The number-envelope branch (<c>["_a2a:N"]</c>) is added by the
/// filter visitor, not here, because the envelope wrap is operator-
/// dependent (only meaningful for numeric comparisons).
/// </remarks>
internal static class CosmosPathTranslator
{
    /// <summary>Default Cosmos query root alias used in our queries.</summary>
    internal const string DefaultRootAlias = "c";

    /// <summary>
    /// Renders <paramref name="path"/> rooted at <paramref name="rootAlias"/>
    /// (e.g. <c>c</c>) as a Cosmos SQL property-access expression
    /// (e.g. <c>c["a"]["b"][0]["c"]</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException">If <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="rootAlias"/> is empty or contains chars
    /// outside <c>[A-Za-z_][A-Za-z0-9_]*</c> (safety check — alias is emitted unquoted).</exception>
    public static string Translate(DocumentPath path, string rootAlias = DefaultRootAlias)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        ValidateRootAlias(rootAlias);

        var sb = new StringBuilder();
        sb.Append(rootAlias);
        for (int i = 0; i < path.Segments.Count; i++)
        {
            switch (path.Segments[i])
            {
                case AttributePathSegment a:
                    AppendBracketString(sb, a.Name);
                    break;
                case IndexPathSegment idx:
                    sb.Append('[').Append(idx.Index).Append(']');
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown DocumentPath segment kind: {path.Segments[i].GetType().Name}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends <c>["..."]</c> with the same string-escape rules Cosmos
    /// uses for SQL string literals: backslash and double-quote are
    /// escaped, control characters are emitted via <c>\uXXXX</c>.
    /// </summary>
    internal static void AppendBracketString(StringBuilder sb, string name)
    {
        sb.Append('[').Append('"');
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            switch (c)
            {
                case '"': sb.Append('\\').Append('"'); break;
                case '\\': sb.Append('\\').Append('\\'); break;
                case '\b': sb.Append('\\').Append('b'); break;
                case '\f': sb.Append('\\').Append('f'); break;
                case '\n': sb.Append('\\').Append('n'); break;
                case '\r': sb.Append('\\').Append('r'); break;
                case '\t': sb.Append('\\').Append('t'); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append('\\').Append('u')
                          .Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"').Append(']');
    }

    private static void ValidateRootAlias(string alias)
    {
        if (string.IsNullOrEmpty(alias))
            throw new ArgumentException("Root alias must not be empty.", nameof(alias));
        char first = alias[0];
        if (!(first == '_' || (first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z')))
        {
            throw new ArgumentException(
                $"Root alias '{alias}' must start with a letter or underscore.", nameof(alias));
        }
        for (int i = 1; i < alias.Length; i++)
        {
            char c = alias[i];
            bool ok = c == '_'
                      || (c >= 'A' && c <= 'Z')
                      || (c >= 'a' && c <= 'z')
                      || (c >= '0' && c <= '9');
            if (!ok)
            {
                throw new ArgumentException(
                    $"Root alias '{alias}' contains invalid character '{c}'.", nameof(alias));
            }
        }
    }
}
