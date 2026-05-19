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
            // Multipart upload subresources (?uploads, ?uploadId, ?partNumber)
            // are first-class operations layered on the same path. Classify
            // them before the catch-all subresource gate so they reach the
            // multipart handler instead of returning Unsupported.
            if (IsMultipartObjectRequest(method, request, out var multipartOp))
            {
                return new S3RouteResult(multipartOp, bucket, key, virtualHosted);
            }

            // Object subresources (?acl, ?tagging, ?torrent, ?restore,
            // ?legal-hold, ?retention) are distinct S3 operations. Classify
            // them to dedicated ops (Slice 9: tagging is real; the rest are
            // ownership-only / NotImplemented stubs) so clients get coherent
            // S3-shaped responses instead of generic 501.
            if (TryClassifyObjectSubresource(method, request.Query, out var objSubOp))
            {
                return new S3RouteResult(objSubOp, bucket, key, virtualHosted);
            }
            if (HasObjectSubresource(request.Query))
            {
                return new S3RouteResult(S3Operation.Unsupported, bucket, key, virtualHosted);
            }
            return method switch
            {
                var m when m == HttpMethods.Get    => new S3RouteResult(S3Operation.GetObject,    bucket, key, virtualHosted),
                var m when m == HttpMethods.Head   => new S3RouteResult(S3Operation.HeadObject,   bucket, key, virtualHosted),
                var m when m == HttpMethods.Put    => new S3RouteResult(ClassifyPutObject(request), bucket, key, virtualHosted),
                var m when m == HttpMethods.Delete => new S3RouteResult(S3Operation.DeleteObject, bucket, key, virtualHosted),
                _ => new S3RouteResult(S3Operation.Unknown, bucket, key, virtualHosted),
            };
        }

        // Bucket subresources (?tagging, ?policy, ?lifecycle, …). Slice 9
        // classifies them to dedicated ops; ?uploads stays as
        // ListMultipartUploads and is handled below.
        if (TryClassifyBucketSubresource(method, request.Query, out var bucketSubOp))
        {
            return new S3RouteResult(bucketSubOp, bucket, null, virtualHosted);
        }
        if (HasBucketSubresource(request.Query) &&
            !(method == HttpMethods.Get && request.Query.ContainsKey("uploads")))
        {
            return new S3RouteResult(S3Operation.Unsupported, bucket, null, virtualHosted);
        }

        // Bucket-scoped ops: HEAD/PUT/DELETE on /{bucket}. GET /{bucket} is
        // the object listing — V2 when the client sets ?list-type=2,
        // otherwise legacy V1. POST /{bucket}?delete is the multi-object
        // delete batch. GET /{bucket}?uploads enumerates in-progress
        // multipart uploads.
        return method switch
        {
            var m when m == HttpMethods.Head   => new S3RouteResult(S3Operation.HeadBucket,   bucket, null, virtualHosted),
            var m when m == HttpMethods.Put    => new S3RouteResult(S3Operation.CreateBucket, bucket, null, virtualHosted),
            var m when m == HttpMethods.Delete => new S3RouteResult(S3Operation.DeleteBucket, bucket, null, virtualHosted),
            var m when m == HttpMethods.Get && request.Query.ContainsKey("uploads")
                                              => new S3RouteResult(S3Operation.ListMultipartUploads, bucket, null, virtualHosted),
            var m when m == HttpMethods.Get    => new S3RouteResult(ClassifyListOperation(request.Query), bucket, null, virtualHosted),
            var m when m == HttpMethods.Post && request.Query.ContainsKey("delete")
                                              => new S3RouteResult(S3Operation.DeleteObjects, bucket, null, virtualHosted),
            _ => new S3RouteResult(S3Operation.Unknown, bucket, null, virtualHosted),
        };
    }

    /// <summary>
    /// S3 multipart upload subresources hang off the object path with their
    /// own verb mapping:
    ///   POST /{b}/{k}?uploads                       → CreateMultipartUpload
    ///   PUT  /{b}/{k}?uploadId=X&amp;partNumber=N   → UploadPart (or UploadPartCopy
    ///                                                 when x-amz-copy-source is set)
    ///   POST /{b}/{k}?uploadId=X                    → CompleteMultipartUpload
    ///   DELETE /{b}/{k}?uploadId=X                  → AbortMultipartUpload
    ///   GET  /{b}/{k}?uploadId=X                    → ListParts
    /// </summary>
    private static bool IsMultipartObjectRequest(string method, HttpRequest request, out S3Operation op)
    {
        op = S3Operation.Unknown;
        var query = request.Query;
        var hasUploads = query.ContainsKey("uploads");
        var hasUploadId = query.ContainsKey("uploadId");
        var hasPartNumber = query.ContainsKey("partNumber");

        if (!hasUploads && !hasUploadId && !hasPartNumber)
        {
            return false;
        }

        if (hasUploads && method == HttpMethods.Post)
        {
            op = S3Operation.CreateMultipartUpload;
            return true;
        }
        if (hasUploadId && hasPartNumber && method == HttpMethods.Put)
        {
            op = HasCopySourceHeader(request)
                ? S3Operation.UploadPartCopy
                : S3Operation.UploadPart;
            return true;
        }
        if (hasUploadId && !hasPartNumber && method == HttpMethods.Post)
        {
            op = S3Operation.CompleteMultipartUpload;
            return true;
        }
        if (hasUploadId && !hasPartNumber && method == HttpMethods.Delete)
        {
            op = S3Operation.AbortMultipartUpload;
            return true;
        }
        if (hasUploadId && !hasPartNumber && method == HttpMethods.Get)
        {
            op = S3Operation.ListParts;
            return true;
        }
        // Recognised subresource but not a routable verb combo — fall through
        // to Unsupported so the catch-all subresource gate maps it to a
        // NotImplemented error rather than silently mishandling.
        op = S3Operation.Unsupported;
        return true;
    }

    private static S3Operation ClassifyListOperation(IQueryCollection query)
    {
        // S3 promotes a GET on the bucket to ListObjectsV2 when list-type=2
        // is present. Any other value (including absent) is the legacy V1.
        if (query.TryGetValue("list-type", out var values))
        {
            foreach (var v in values)
            {
                if (string.Equals(v, "2", StringComparison.Ordinal))
                {
                    return S3Operation.ListObjectsV2;
                }
            }
        }
        return S3Operation.ListObjects;
    }

    /// <summary>
    /// S3 overloads PUT /{bucket}/{key} on a header: when
    /// <c>x-amz-copy-source</c> is present the request is a CopyObject
    /// instead of a PutObject upload. Detecting the header at the router
    /// keeps the per-operation dispatch table flat.
    /// </summary>
    private static S3Operation ClassifyPutObject(HttpRequest request)
    {
        return HasCopySourceHeader(request) ? S3Operation.CopyObject : S3Operation.PutObject;
    }

    /// <summary>
    /// True when the request carries a non-empty <c>x-amz-copy-source</c>
    /// header. Used to disambiguate PUT-on-object (PutObject vs CopyObject)
    /// and PUT-on-multipart-part (UploadPart vs UploadPartCopy).
    /// </summary>
    private static bool HasCopySourceHeader(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("x-amz-copy-source", out var values))
        {
            return false;
        }
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
            {
                return true;
            }
        }
        return false;
    }

    // Well-known S3 object subresources. Membership is checked case-insensitively.
    private static readonly System.Collections.Generic.HashSet<string> ObjectSubresources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "acl", "attributes", "tagging", "torrent", "uploads", "uploadId",
            "partNumber", "restore", "select", "select-type",
            "legal-hold", "retention", "versions", "versionId",
        };

    private static bool HasObjectSubresource(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return false;
        }
        foreach (var kv in query)
        {
            if (ObjectSubresources.Contains(kv.Key))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Maps an object-scoped subresource query key + HTTP method to the
    /// specific <see cref="S3Operation"/> the long-tail handler will dispatch
    /// to. Returns false when the subresource is not recognised or the verb
    /// combination has no S3 equivalent (caller falls back to the
    /// catch-all <see cref="S3Operation.Unsupported"/>).
    /// </summary>
    private static bool TryClassifyObjectSubresource(string method, IQueryCollection query, out S3Operation op)
    {
        op = S3Operation.Unknown;
        if (query.Count == 0) return false;

        if (query.ContainsKey("tagging"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetObjectTagging,
                var m when m == HttpMethods.Put    => S3Operation.PutObjectTagging,
                var m when m == HttpMethods.Delete => S3Operation.DeleteObjectTagging,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("acl"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetObjectAcl,
                var m when m == HttpMethods.Put => S3Operation.PutObjectAcl,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("torrent"))
        {
            op = method == HttpMethods.Get ? S3Operation.GetObjectTorrent : S3Operation.Unsupported;
            return true;
        }
        if (query.ContainsKey("restore"))
        {
            op = method == HttpMethods.Post ? S3Operation.RestoreObject : S3Operation.Unsupported;
            return true;
        }
        if (query.ContainsKey("legal-hold"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetObjectLegalHold,
                var m when m == HttpMethods.Put => S3Operation.PutObjectLegalHold,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("retention"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetObjectRetention,
                var m when m == HttpMethods.Put => S3Operation.PutObjectRetention,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        return false;
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

    /// <summary>
    /// Maps a bucket-scoped subresource query key + HTTP method to its
    /// specific <see cref="S3Operation"/>. Returns false when no recognised
    /// subresource is present (caller falls back to the catch-all
    /// <see cref="S3Operation.Unsupported"/>). Order matters when more than
    /// one recognised key is present, mirroring AWS precedence; in practice
    /// SDKs send exactly one.
    /// </summary>
    private static bool TryClassifyBucketSubresource(string method, IQueryCollection query, out S3Operation op)
    {
        op = S3Operation.Unknown;
        if (query.Count == 0) return false;

        // ?uploads belongs to multipart (handled separately).
        if (query.ContainsKey("uploads")) return false;

        if (query.ContainsKey("tagging"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketTagging,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketTagging,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketTagging,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("acl"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetBucketAcl,
                var m when m == HttpMethods.Put => S3Operation.PutBucketAcl,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("lifecycle"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketLifecycleConfiguration,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketLifecycleConfiguration,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketLifecycle,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("cors"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketCors,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketCors,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketCors,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("website"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketWebsite,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketWebsite,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketWebsite,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("replication"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketReplication,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketReplication,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketReplication,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("encryption"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketEncryption,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketEncryption,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketEncryption,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("logging"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetBucketLogging,
                var m when m == HttpMethods.Put => S3Operation.PutBucketLogging,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("versioning"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetBucketVersioning,
                var m when m == HttpMethods.Put => S3Operation.PutBucketVersioning,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("requestPayment"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetBucketRequestPayment,
                var m when m == HttpMethods.Put => S3Operation.PutBucketRequestPayment,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("object-lock"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetObjectLockConfiguration,
                var m when m == HttpMethods.Put => S3Operation.PutObjectLockConfiguration,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("publicAccessBlock"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetPublicAccessBlock,
                var m when m == HttpMethods.Put    => S3Operation.PutPublicAccessBlock,
                var m when m == HttpMethods.Delete => S3Operation.DeletePublicAccessBlock,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("policy"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketPolicy,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketPolicy,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketPolicy,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("policyStatus"))
        {
            op = method == HttpMethods.Get ? S3Operation.GetBucketPolicyStatus : S3Operation.Unsupported;
            return true;
        }
        if (query.ContainsKey("notification"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetBucketNotificationConfiguration,
                var m when m == HttpMethods.Put => S3Operation.PutBucketNotificationConfiguration,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("accelerate"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get => S3Operation.GetBucketAccelerateConfiguration,
                var m when m == HttpMethods.Put => S3Operation.PutBucketAccelerateConfiguration,
                _ => S3Operation.Unsupported,
            };
            return true;
        }
        if (query.ContainsKey("ownershipControls"))
        {
            op = method switch
            {
                var m when m == HttpMethods.Get    => S3Operation.GetBucketOwnershipControls,
                var m when m == HttpMethods.Put    => S3Operation.PutBucketOwnershipControls,
                var m when m == HttpMethods.Delete => S3Operation.DeleteBucketOwnershipControls,
                _ => S3Operation.Unsupported,
            };
            return true;
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
