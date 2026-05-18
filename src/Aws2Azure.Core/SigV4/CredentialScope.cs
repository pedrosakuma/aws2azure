namespace Aws2Azure.Core.SigV4;

/// <summary>
/// The <c>Credential</c> scope parsed from an Authorization header or
/// presigned URL. Format: <c>{accessKeyId}/{date}/{region}/{service}/aws4_request</c>.
/// </summary>
public readonly record struct CredentialScope(
    string AccessKeyId,
    string Date,
    string Region,
    string Service)
{
    /// <summary>Returns the <c>{date}/{region}/{service}/aws4_request</c> portion.</summary>
    public string ToScopeString() => $"{Date}/{Region}/{Service}/{SigV4Constants.TerminationString}";

    /// <summary>Returns the full <c>{accessKey}/{scope}</c> string.</summary>
    public string ToCredentialString() => $"{AccessKeyId}/{ToScopeString()}";

    public static bool TryParse(string value, out CredentialScope scope)
    {
        scope = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split('/');
        if (parts.Length != 5)
        {
            return false;
        }
        if (!string.Equals(parts[4], SigV4Constants.TerminationString, StringComparison.Ordinal))
        {
            return false;
        }
        if (parts[1].Length != 8) // yyyyMMdd
        {
            return false;
        }

        scope = new CredentialScope(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }
}
