using System.Globalization;
using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.Management;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsTopicSupport
{
    public const string PlaceholderAccountId = "000000000000";
    public const int ListTopicsPageSize = 100;

    public static bool TryGetRequiredParameter(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out string value,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (parameters.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            error = null;
            return true;
        }

        value = string.Empty;
        error = $"Parameter '{name}' is required.";
        return false;
    }

    public static string ResolveNamespaceFqdn(ServiceBusTopicsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            return endpointUri.IsDefaultPort ? endpointUri.Host : endpointUri.Authority;
        }

        return credentials.Namespace + ".servicebus.windows.net";
    }

    public static string BuildTopicArn(HttpContext context, string topicName)
        => BuildTopicArn(ResolveRegion(context), topicName);

    public static string BuildTopicArn(string region, string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return $"arn:aws:sns:{region}:{PlaceholderAccountId}:{topicName}";
    }

    public static string ResolveRegion(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.TryGetValue("Authorization", out var values))
        {
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (!string.IsNullOrWhiteSpace(value)
                    && AuthorizationHeader.TryParse(value, out var authorization)
                    && !string.IsNullOrWhiteSpace(authorization.Credential.Region))
                {
                    return authorization.Credential.Region;
                }
            }
        }

        var host = context.Request.Host.Host;
        if (!string.IsNullOrWhiteSpace(host)
            && host.StartsWith("sns.", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = host[4..];
            var separator = remainder.IndexOf('.');
            if (separator > 0)
            {
                return remainder[..separator];
            }
        }

        return "us-east-1";
    }

    public static bool IsValidTopicName(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName)
            || topicName.Length > 256)
        {
            return false;
        }

        for (var i = 0; i < topicName.Length; i++)
        {
            var c = topicName[i];
            if ((c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '_'
                || c == '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static bool TryParseTopicArn(string topicArn, out string topicName, out string? error)
    {
        topicName = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(topicArn))
        {
            error = "Parameter 'TopicArn' is required.";
            return false;
        }

        var parts = topicArn.Split(':', 6, StringSplitOptions.None);
        if (parts.Length != 6
            || !string.Equals(parts[0], "arn", StringComparison.Ordinal)
            || !string.Equals(parts[1], "aws", StringComparison.Ordinal)
            || !string.Equals(parts[2], "sns", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[3])
            || string.IsNullOrWhiteSpace(parts[4])
            || string.IsNullOrWhiteSpace(parts[5]))
        {
            error = "TopicArn must be a valid SNS topic ARN of the form 'arn:aws:sns:{region}:{accountId}:{topicName}'.";
            return false;
        }

        if (!IsValidTopicName(parts[5]))
        {
            error = "TopicArn contained an invalid topic name.";
            return false;
        }

        topicName = parts[5];
        return true;
    }

    public static string EncodeNextToken(int skip)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(skip.ToString(CultureInfo.InvariantCulture)));
    }

    public static bool TryDecodeNextToken(string nextToken, out int skip)
    {
        skip = 0;
        if (string.IsNullOrWhiteSpace(nextToken))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(nextToken));
            return int.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out skip) && skip >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static Task WriteInvalidParameterAsync(HttpContext context, string message)
        => SnsErrorResponse.WriteErrorAsync(
            context,
            StatusCodes.Status400BadRequest,
            errorType: "Sender",
            errorCode: "InvalidParameter",
            message);

    public static Task WriteManagementErrorAsync(HttpContext context, ServiceBusTopicsManagementException ex)
    {
        return ex.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => SnsErrorResponse.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                errorType: "Sender",
                errorCode: "AuthorizationError",
                message: "Access denied when calling the Azure Service Bus Topics management API."),
            _ => SnsErrorResponse.WriteErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                errorType: "Receiver",
                errorCode: "InternalFailure",
                message: $"Azure Service Bus Topics management API returned HTTP {(int)ex.StatusCode}.")
        };
    }
}
