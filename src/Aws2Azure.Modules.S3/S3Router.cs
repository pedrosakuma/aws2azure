using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3;

/// <summary>
/// Classifies an incoming HTTP request as a specific <see cref="S3Operation"/>
/// using only HTTP method + path + query, mirroring the wire-level decisions a
/// real S3 endpoint makes. Reflection-free and allocation-light.
/// </summary>
/// <remarks>
/// Slice 1 (bucket lifecycle) supports path-style only. Virtual-hosted URLs
/// — <c>{bucket}.s3.amazonaws.com</c> — are detected and surfaced via
/// <see cref="S3RouteResult.VirtualHosted"/>; the handler returns
/// <see cref="S3Operation.Unsupported"/> until later slices opt in.
/// </remarks>
public static class S3Router
{
    public static S3RouteResult Classify(HttpContext context)
    {
        var request = context.Request;
        var host = request.Host.Host ?? string.Empty;
        var virtualHosted = IsVirtualHostedStyle(host, out var hostBucket);

        // Path-style: everything is encoded in the URI path.
        // /                → ListBuckets (host-only ops carry no bucket)
        // /{bucket}        → bucket-scoped op
        // /{bucket}/{key…} → object-scoped op
        var path = request.Path.HasValue ? request.Path.Value!.AsSpan() : ReadOnlySpan<char>.Empty;
        var (bucket, key) = SplitPath(path);

        if (virtualHosted)
        {
            // Host-encoded bucket wins for virtual-hosted requests.
            // Everything in the path is then the key (possibly empty).
            bucket = hostBucket;
            key = path.Length > 1 ? path[1..].ToString() : null;
        }

        var method = request.Method;

        if (string.IsNullOrEmpty(bucket))
        {
            return method == HttpMethods.Get
                ? new S3RouteResult(S3Operation.ListBuckets, null, null, virtualHosted)
                : new S3RouteResult(S3Operation.Unknown, null, null, virtualHosted);
        }

        if (!string.IsNullOrEmpty(key))
        {
            // Object-scoped ops are not part of slice 1.
            return new S3RouteResult(S3Operation.Unsupported, bucket, key, virtualHosted);
        }

        // Bucket subresources (?tagging, ?policy, ?lifecycle, …) are distinct
        // S3 operations even though they share the verb + path with the
        // lifecycle ops. Refuse them up-front so DELETE /b?tagging never
        // reaches DeleteBucket and drops the container.
        if (HasBucketSubresource(request.Query))
        {
            return new S3RouteResult(S3Operation.Unsupported, bucket, null, virtualHosted);
        }

        // Bucket-scoped ops: HEAD/PUT/DELETE on /{bucket}. GET /{bucket} is
        // ListObjectsV2 (later slice) — explicitly Unsupported here so we
        // don't accidentally route it to ListBuckets.
        return method switch
        {
            var m when m == HttpMethods.Head   => new S3RouteResult(S3Operation.HeadBucket,   bucket, null, virtualHosted),
            var m when m == HttpMethods.Put    => new S3RouteResult(S3Operation.CreateBucket, bucket, null, virtualHosted),
            var m when m == HttpMethods.Delete => new S3RouteResult(S3Operation.DeleteBucket, bucket, null, virtualHosted),
            var m when m == HttpMethods.Get    => new S3RouteResult(S3Operation.Unsupported,  bucket, null, virtualHosted),
            _ => new S3RouteResult(S3Operation.Unknown, bucket, null, virtualHosted),
        };
    }

    // Well-known S3 bucket subresources. Membership is checked case-insensitively
    // since SDKs and SigV4 canonicalization both normalise query keys.
    private static readonly System.Collections.Generic.HashSet<string> BucketSubresources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "accelerate", "acl", "analytics", "cors", "encryption", "intelligent-tiering",
            "inventory", "lifecycle", "logging", "metrics", "notification",
            "object-lock", "ownershipControls", "policy", "policyStatus",
            "publicAccessBlock", "replication", "requestPayment", "tagging",
            "uploads", "versioning", "versions", "website",
        };

    private static bool HasBucketSubresource(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return false;
        }
        foreach (var kv in query)
        {
            if (BucketSubresources.Contains(kv.Key))
            {
                return true;
            }
        }
        return false;
    }

    private static (string? bucket, string? key) SplitPath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || (path.Length == 1 && path[0] == '/'))
        {
            return (null, null);
        }

        // Strip the leading '/'.
        var trimmed = path[0] == '/' ? path[1..] : path;
        if (trimmed.IsEmpty)
        {
            return (null, null);
        }

        var slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            return (trimmed.ToString(), null);
        }
        var bucket = trimmed[..slash].ToString();
        var key = trimmed[(slash + 1)..].ToString();
        return (bucket, string.IsNullOrEmpty(key) ? null : key);
    }

    private static bool IsVirtualHostedStyle(string host, out string? bucket)
    {
        bucket = null;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Path-style anchors: host starts with "s3." or "s3-" (e.g.
        // s3.amazonaws.com, s3.us-east-1.amazonaws.com, s3-us-west-2…).
        if (host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("s3-", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Virtual-hosted: {bucket}.s3.… or {bucket}.s3-…
        var dotS3 = host.IndexOf(".s3.", StringComparison.OrdinalIgnoreCase);
        if (dotS3 < 0)
        {
            dotS3 = host.IndexOf(".s3-", StringComparison.OrdinalIgnoreCase);
        }
        if (dotS3 > 0)
        {
            bucket = host[..dotS3];
            return true;
        }

        return false;
    }
}
