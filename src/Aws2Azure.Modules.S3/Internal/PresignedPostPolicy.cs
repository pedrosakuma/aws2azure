using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.S3.Errors;

namespace Aws2Azure.Modules.S3.Internal;

internal static class PresignedPostPolicy
{
    public readonly record struct ValidationResult(
        bool Success,
        string? AccessKeyId,
        string? Region,
        int SuccessStatusCode,
        S3ErrorMapping.Mapping? Error);

    public static ValidationResult Validate(
        string bucket,
        IReadOnlyDictionary<string, string> fields,
        long fileLength,
        ICredentialResolver credentials,
        bool skipContentLengthRange = false,
        DateTimeOffset? now = null)
    {
        if (!TryGetField(fields, "policy", out var policyBase64)
            || !TryGetField(fields, "x-amz-algorithm", out var algorithm)
            || !TryGetField(fields, "x-amz-credential", out var credentialRaw)
            || !TryGetField(fields, "x-amz-date", out var amzDate)
            || !TryGetField(fields, "x-amz-signature", out var signature))
        {
            return Fail(SignatureFailure("Presigned POST form is missing one of policy, x-amz-algorithm, x-amz-credential, x-amz-date, or x-amz-signature."));
        }

        if (!string.Equals(algorithm, SigV4Constants.Algorithm, StringComparison.Ordinal))
        {
            return Fail(SignatureFailure("Only AWS4-HMAC-SHA256 presigned POST policies are supported."));
        }

        if (!CredentialScope.TryParse(credentialRaw, out var scope))
        {
            return Fail(SignatureFailure("x-amz-credential is not a valid scope."));
        }

        if (!credentials.TryGetAwsSecret(scope.AccessKeyId, out var secret))
        {
            return Fail(new S3ErrorMapping.Mapping(403, "InvalidAccessKeyId",
                "The AWS Access Key Id you provided does not exist in our records."));
        }

        if (!string.Equals(scope.Service, "s3", StringComparison.Ordinal))
        {
            return Fail(SignatureFailure("Presigned POST credential scope must target the s3 service."));
        }

        if (!SigningKey.TryParseAmzDate(amzDate, out _))
        {
            return Fail(SignatureFailure("x-amz-date is not a valid ISO 8601 basic timestamp."));
        }

        var signingKey = SigningKey.Derive(secret, scope.Date, scope.Region, scope.Service);
        var expectedSignature = SigningKey.ToLowerHex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(policyBase64)));
        if (!FixedTimeHexEquals(expectedSignature, signature))
        {
            return Fail(SignatureFailure("The request signature we calculated does not match the signature you provided."));
        }

        byte[] policyBytes;
        try
        {
            policyBytes = Convert.FromBase64String(policyBase64);
        }
        catch (FormatException)
        {
            return Fail(SignatureFailure("policy is not valid base64."));
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(policyBytes);
        }
        catch (JsonException)
        {
            return Fail(SignatureFailure("policy is not valid JSON."));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("expiration", out var expirationElement)
                || expirationElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(expirationElement.GetString(), out var expiration))
            {
                return Fail(SignatureFailure("policy is missing a valid expiration."));
            }

            var effectiveNow = now ?? DateTimeOffset.UtcNow;
            if (effectiveNow > expiration)
            {
                return Fail(new S3ErrorMapping.Mapping(403, "AccessDenied", "Request has expired."));
            }

            if (!root.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array)
            {
                return Fail(SignatureFailure("policy is missing a valid conditions array."));
            }

            var authorizedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var startsWithConstraints = new List<(string Name, string Prefix)>();
            foreach (var condition in conditions.EnumerateArray())
            {
                RecordAuthorizedFields(condition, authorizedFields, startsWithConstraints);
                if (!ConditionMatches(condition, bucket, fields, fileLength, skipContentLengthRange))
                {
                    return Fail(SignatureFailure("The POST policy conditions were not satisfied."));
                }
            }

            if (!AllSubmittedFieldsAreAuthorized(fields, authorizedFields, startsWithConstraints))
            {
                return Fail(SignatureFailure("The POST policy does not authorize one or more submitted form fields."));
            }
        }

        if (TryGetField(fields, "success_action_redirect", out _))
        {
            return Fail(S3ErrorMapping.InvalidArgument(
                "success_action_redirect is not supported for presigned POST."));
        }

        var successStatusCode = 204;
        if (TryGetField(fields, "success_action_status", out var successStatusRaw)
            && int.TryParse(successStatusRaw, out var requestedStatus))
        {
            if (requestedStatus == 201)
            {
                return Fail(S3ErrorMapping.InvalidArgument(
                    "success_action_status=201 is not supported for presigned POST."));
            }

            if (requestedStatus is 200 or 204)
            {
                successStatusCode = requestedStatus;
            }
        }

        return new ValidationResult(true, scope.AccessKeyId, scope.Region, successStatusCode, null);
    }

    private static bool ConditionMatches(
        JsonElement condition,
        string bucket,
        IReadOnlyDictionary<string, string> fields,
        long fileLength,
        bool skipContentLengthRange)
    {
        if (condition.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in condition.EnumerateObject())
            {
                if (!TryResolveFieldValue(bucket, fields, property.Name, out var actual)
                    || !string.Equals(actual, property.Value.GetString(), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        if (condition.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new JsonElement[3];
        var count = 0;
        foreach (var item in condition.EnumerateArray())
        {
            if (count == items.Length)
            {
                return false;
            }

            items[count++] = item;
        }

        if (count == 0 || items[0].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var op = items[0].GetString();
        if (string.Equals(op, "content-length-range", StringComparison.Ordinal))
        {
            if (skipContentLengthRange)
            {
                return true;
            }

            return count == 3
                && items[1].TryGetInt64(out var min)
                && items[2].TryGetInt64(out var max)
                && fileLength >= min
                && fileLength <= max;
        }

        if (count != 3 || items[1].ValueKind != JsonValueKind.String || items[2].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var fieldName = items[1].GetString() ?? string.Empty;
        if (fieldName.Length == 0 || fieldName[0] != '$')
        {
            return false;
        }

        fieldName = fieldName[1..];
        if (!TryResolveFieldValue(bucket, fields, fieldName, out var actualValue))
        {
            return false;
        }

        var expected = items[2].GetString() ?? string.Empty;
        if (string.Equals(op, "eq", StringComparison.Ordinal))
        {
            return string.Equals(actualValue, expected, StringComparison.Ordinal);
        }

        if (string.Equals(op, "starts-with", StringComparison.Ordinal))
        {
            return actualValue.StartsWith(expected, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool TryResolveFieldValue(
        string bucket,
        IReadOnlyDictionary<string, string> fields,
        string name,
        out string value)
    {
        if (string.Equals(name, "bucket", StringComparison.Ordinal))
        {
            value = bucket;
            return true;
        }

        return TryGetField(fields, name, out value);
    }

    private static bool TryGetField(IReadOnlyDictionary<string, string> fields, string name, out string value)
    {
        foreach (var entry in fields)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static void RecordAuthorizedFields(
        JsonElement condition,
        ISet<string> authorizedFields,
        ICollection<(string Name, string Prefix)> startsWithConstraints)
    {
        if (condition.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in condition.EnumerateObject())
            {
                authorizedFields.Add(property.Name);
            }
            return;
        }

        if (condition.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var items = new JsonElement[3];
        var count = 0;
        foreach (var item in condition.EnumerateArray())
        {
            if (count == items.Length)
            {
                return;
            }

            items[count++] = item;
        }

        if (count != 3 || items[0].ValueKind != JsonValueKind.String || items[1].ValueKind != JsonValueKind.String)
        {
            return;
        }

        var op = items[0].GetString();
        var fieldName = items[1].GetString() ?? string.Empty;
        if (fieldName.Length == 0 || fieldName[0] != '$')
        {
            return;
        }

        fieldName = fieldName[1..];
        if (string.Equals(op, "eq", StringComparison.Ordinal))
        {
            authorizedFields.Add(fieldName);
        }
        else if (string.Equals(op, "starts-with", StringComparison.Ordinal) && items[2].ValueKind == JsonValueKind.String)
        {
            startsWithConstraints.Add((fieldName, items[2].GetString() ?? string.Empty));
        }
    }

    private static bool AllSubmittedFieldsAreAuthorized(
        IReadOnlyDictionary<string, string> fields,
        ISet<string> authorizedFields,
        IReadOnlyCollection<(string Name, string Prefix)> startsWithConstraints)
    {
        foreach (var field in fields)
        {
            if (string.Equals(field.Key, "file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(field.Key, "policy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(field.Key, "x-amz-signature", StringComparison.OrdinalIgnoreCase)
                || field.Key.StartsWith("x-ignore-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (authorizedFields.Contains(field.Key))
            {
                continue;
            }

            var authorizedByPrefix = false;
            foreach (var (name, prefix) in startsWithConstraints)
            {
                if (string.Equals(name, field.Key, StringComparison.OrdinalIgnoreCase)
                    && field.Value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    authorizedByPrefix = true;
                    break;
                }
            }

            if (!authorizedByPrefix)
            {
                return false;
            }
        }

        return true;
    }

    private static bool FixedTimeHexEquals(string expected, string actual)
    {
        if (expected.Length != 64 || actual.Length != 64)
        {
            return false;
        }

        Span<byte> expectedBytes = stackalloc byte[64];
        Span<byte> actualBytes = stackalloc byte[64];
        Encoding.ASCII.GetBytes(expected, expectedBytes);
        Encoding.ASCII.GetBytes(actual, actualBytes);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static S3ErrorMapping.Mapping SignatureFailure(string message) =>
        new(403, "SignatureDoesNotMatch", message);

    private static ValidationResult Fail(S3ErrorMapping.Mapping error) =>
        new(false, null, null, 0, error);
}
