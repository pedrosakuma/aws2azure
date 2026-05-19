namespace Aws2Azure.Modules.S3.Errors;

/// <summary>
/// Maps Azure Blob storage HTTP failures to the corresponding S3 error code
/// and status. Drives bucket-lifecycle handlers in slice 1; expanded as later
/// slices add object-scoped operations.
/// </summary>
internal static class S3ErrorMapping
{
    public readonly record struct Mapping(int StatusCode, string Code, string Message);

    /// <summary>
    /// Translates an Azure Blob storage failure response. The Azure error code
    /// header (<c>x-ms-error-code</c>) drives the mapping when present, falling
    /// back to status code for generic 4xx/5xx.
    /// </summary>
    public static Mapping FromAzure(HttpResponseMessage response, S3Operation op)
    {
        var azureCode = TryGetAzureErrorCode(response);
        return MapAzure((int)response.StatusCode, azureCode, op);
    }

    internal static Mapping MapAzure(int azureStatus, string? azureCode, S3Operation op)
    {
        return (azureStatus, azureCode) switch
        {
            (404, "ContainerNotFound") =>
                new Mapping(404, "NoSuchBucket", "The specified bucket does not exist."),

            (404, "BlobNotFound") =>
                new Mapping(404, "NoSuchKey", "The specified key does not exist."),

            // Azure surfaces "source not reachable" during a Copy Blob /
            // Put Block From URL with CannotVerifyCopySource; from S3's
            // perspective that's a source bucket/key the caller doesn't
            // have access to or doesn't exist. Azure documents the HTTP
            // status as 404 for Copy Blob and 500 for some Put-from-URL
            // paths, so we match by error code only and surface NoSuchKey
            // either way (a 500 InternalError would otherwise look like a
            // retryable server fault).
            (_, "CannotVerifyCopySource") =>
                new Mapping(404, "NoSuchKey",
                    "The specified copy source does not exist or is not accessible."),

            (409, "ContainerAlreadyExists") =>
                // S3 distinguishes "owned by you" from "owned by someone else"; without ownership
                // signals from Azure we surface the conservative variant.
                new Mapping(409, "BucketAlreadyOwnedByYou",
                    "Your previous request to create the named bucket succeeded and you already own it."),

            (409, "ContainerBeingDeleted") =>
                new Mapping(409, "OperationAborted",
                    "A conflicting conditional operation is currently in progress against this resource."),

            (412, _) =>
                new Mapping(412, "PreconditionFailed",
                    "At least one of the pre-conditions you specified did not hold."),

            (413, _) =>
                new Mapping(413, "EntityTooLarge",
                    "Your proposed upload exceeds the maximum allowed size."),

            (416, _) =>
                new Mapping(416, "InvalidRange",
                    "The requested range is not satisfiable."),

            (400, "InvalidResourceName") or (400, "OutOfRangeInput") =>
                new Mapping(400, "InvalidBucketName", "The specified bucket is not valid."),

            (400, "InvalidHeaderValue") or (400, "InvalidInput") or (400, "InvalidQueryParameterValue") =>
                new Mapping(400, "InvalidArgument", "Invalid argument."),

            (403, _) =>
                new Mapping(403, "AccessDenied", "Access Denied."),

            (429, _) =>
                new Mapping(503, "SlowDown", "Please reduce your request rate."),

            // Azure surfaces throttling as 503 ServerBusy; S3 clients key
            // backoff/retry off the SlowDown code, not the status alone.
            (503, "ServerBusy") =>
                new Mapping(503, "SlowDown", "Please reduce your request rate."),

            (503, _) =>
                new Mapping(503, "ServiceUnavailable", "Reduce your request rate."),

            (500, _) =>
                new Mapping(500, "InternalError", "We encountered an internal error. Please try again."),

            _ when azureStatus >= 500 =>
                new Mapping(azureStatus, "InternalError", "We encountered an internal error. Please try again."),

            _ =>
                new Mapping(azureStatus, "InvalidRequest",
                    azureCode is null ? "The request is not valid." : $"Azure returned error code '{azureCode}'."),
        };
    }

    public static Mapping InvalidBucketName() =>
        new(400, "InvalidBucketName", "The specified bucket is not valid.");

    public static Mapping InvalidObjectKey() =>
        new(400, "InvalidArgument", "The specified object key is not valid.");

    public static Mapping InvalidArgument(string message) =>
        new(400, "InvalidArgument", message);

    public static Mapping NotImplemented(S3Operation op) =>
        new(501, "NotImplemented",
            $"aws2azure: operation {op} is not implemented in this build.");

    public static Mapping NoSuchConfiguration(string code, string what) =>
        new(404, code, $"The {what} does not exist.");

    public static Mapping AccessControlListNotSupported() =>
        new(400, "AccessControlListNotSupported",
            "The bucket does not allow ACLs. Only canned ACL 'private' (or an equivalent FULL_CONTROL grant to the bucket owner) is accepted by this proxy.");

    public static Mapping MalformedXml(string message) =>
        new(400, "MalformedXML", message);

    public static Mapping VirtualHostedNotSupported() =>
        new(400, "InvalidRequest",
            "aws2azure: virtual-hosted-style S3 URLs are not supported. Use path-style addressing.");

    public static Mapping NoCredentials() =>
        new(403, "AccessDenied",
            "No Azure Blob credentials are configured for the authenticated AWS access key.");

    private static string? TryGetAzureErrorCode(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ms-error-code", out var values))
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrEmpty(v))
                {
                    return v;
                }
            }
        }
        return null;
    }
}
