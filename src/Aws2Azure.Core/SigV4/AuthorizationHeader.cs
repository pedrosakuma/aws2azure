namespace Aws2Azure.Core.SigV4;

/// <summary>
/// Parsed <c>Authorization: AWS4-HMAC-SHA256 Credential=..., SignedHeaders=..., Signature=...</c>
/// header.
/// </summary>
public readonly record struct AuthorizationHeader(
    CredentialScope Credential,
    string[] SignedHeaders,
    string Signature)
{
    public static bool TryParse(string headerValue, out AuthorizationHeader parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(headerValue))
        {
            return false;
        }

        // "AWS4-HMAC-SHA256 " prefix.
        if (!headerValue.StartsWith(SigV4Constants.Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        var body = headerValue.AsSpan(SigV4Constants.Algorithm.Length).TrimStart();

        string? credential = null;
        string? signedHeaders = null;
        string? signature = null;

        foreach (var rawPart in body.ToString().Split(','))
        {
            var part = rawPart.Trim();
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];

            if (key.Equals("Credential", StringComparison.Ordinal))
            {
                credential = value;
            }
            else if (key.Equals("SignedHeaders", StringComparison.Ordinal))
            {
                signedHeaders = value;
            }
            else if (key.Equals("Signature", StringComparison.Ordinal))
            {
                signature = value;
            }
        }

        if (credential is null || signedHeaders is null || signature is null)
        {
            return false;
        }

        if (!CredentialScope.TryParse(credential, out var scope))
        {
            return false;
        }

        var headers = signedHeaders.Split(';');
        // Headers in SignedHeaders MUST be lowercase and sorted (spec). Verify.
        for (var i = 0; i < headers.Length; i++)
        {
            var h = headers[i];
            for (var j = 0; j < h.Length; j++)
            {
                var c = h[j];
                if (c >= 'A' && c <= 'Z')
                {
                    return false;
                }
            }
            if (i > 0 && string.CompareOrdinal(headers[i - 1], h) >= 0)
            {
                return false;
            }
        }

        parsed = new AuthorizationHeader(scope, headers, signature);
        return true;
    }
}
