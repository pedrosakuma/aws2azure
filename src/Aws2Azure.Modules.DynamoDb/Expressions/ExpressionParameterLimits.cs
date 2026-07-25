using System.Collections.Generic;
using System.Text;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

internal static class ExpressionParameterLimits
{
    internal const int MaxExpressionUtf8Bytes = 4 * 1024;
    internal const int MaxPlaceholderUtf8Bytes = 255;

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    internal static void ValidateEncodedLength(
        string expression,
        string parameterName)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(expression);
        }
        catch (EncoderFallbackException)
        {
            throw new ExpressionSyntaxException(
                0,
                $"{parameterName} must contain valid Unicode scalar values.");
        }

        if (byteCount > MaxExpressionUtf8Bytes)
        {
            throw new ExpressionSyntaxException(
                0,
                $"{parameterName} exceeds the maximum encoded length of {MaxExpressionUtf8Bytes} bytes (4 KiB).");
        }
    }

    internal static bool TryValidatePlaceholderLength(
        string placeholder,
        out string error)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(placeholder);
        }
        catch (EncoderFallbackException)
        {
            error =
                $"Expression placeholder '{placeholder}' must contain valid Unicode scalar values.";
            return false;
        }

        if (byteCount > MaxPlaceholderUtf8Bytes)
        {
            error =
                $"Expression placeholder '{placeholder}' exceeds the maximum encoded length of {MaxPlaceholderUtf8Bytes} bytes.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static void ValidateAttributeNamePlaceholders(
        IReadOnlyDictionary<string, string>? names)
    {
        if (names is null)
        {
            return;
        }

        foreach (var placeholder in names.Keys)
        {
            if (!TryValidatePlaceholderLength(placeholder, out var error))
            {
                throw new ExpressionSyntaxException(0, error);
            }
        }
    }
}
