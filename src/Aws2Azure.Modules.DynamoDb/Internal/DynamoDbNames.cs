using System;
using System.Globalization;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// DynamoDB table-naming rules: 3..255 chars, set
/// <c>[a-zA-Z0-9_.-]</c>. Names are case-sensitive and unique within
/// an AWS region. Cosmos container ids accept a wider set, so we
/// enforce the DynamoDB constraints at the proxy boundary to preserve
/// the AWS error surface.
/// </summary>
internal static class DynamoDbNames
{
    public const int MinTableNameLength = 3;
    public const int MaxTableNameLength = 255;

    public static bool IsValidTableName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Length < MinTableNameLength || name.Length > MaxTableNameLength) return false;
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            // ASCII fast-path; DynamoDB rejects non-ASCII.
            if ((c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '_' || c == '-' || c == '.')
            {
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Synthetic Table ARN. Real ARNs encode region + account id; we
    /// surface a stable string of the form
    /// <c>arn:aws:dynamodb:azure:{accountId}:table/{tableName}</c> so
    /// operators can correlate cross-cloud without leaking the master key.
    /// <paramref name="accountIdOrEmpty"/> falls back to a zero-filled
    /// placeholder when not supplied.
    /// </summary>
    public static string BuildTableArn(string accountIdOrEmpty, string tableName)
    {
        var account = string.IsNullOrEmpty(accountIdOrEmpty) ? "000000000000" : accountIdOrEmpty;
        return string.Format(
            CultureInfo.InvariantCulture,
            "arn:aws:dynamodb:azure:{0}:table/{1}",
            account, tableName);
    }
}
