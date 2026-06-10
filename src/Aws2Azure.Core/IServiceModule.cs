using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Core;

/// <summary>
/// Contract every translated AWS service implements. Modules are registered
/// manually in <c>Program.cs</c> — no reflection-based discovery.
/// </summary>
public interface IServiceModule
{
    /// <summary>Short identifier, e.g. <c>"s3"</c>, <c>"sqs"</c>.</summary>
    string ServiceName { get; }

    /// <summary>Routing predicate against the request's HTTP <c>Host</c>.</summary>
    bool MatchesHost(string host);

    /// <summary>Capability matrix surfaced via <c>/_aws2azure/capabilities</c>.</summary>
    CapabilityMatrix Capabilities { get; }

    /// <summary>
    /// When <c>true</c>, the registry validates the request's SigV4
    /// signature before dispatching to <see cref="HandleAsync"/>.
    /// </summary>
    bool RequiresSigV4 { get; }

    /// <summary>
    /// When <c>true</c>, the registry buffers the entire request body
    /// into memory (bounded) and computes its SHA-256 hash before
    /// invoking SigV4 validation. The hash is fed to the validator
    /// even when the client omits the <c>x-amz-content-sha256</c>
    /// header — modern AWS SDKs (boto3, AWSSDK.NET, Java v2) always
    /// send the header, but the SigV4 spec permits omitting it for
    /// non-S3 services since the hash is still part of the canonical
    /// request. Without buffering, a spec-compliant client without
    /// the header signs with the real hash while the proxy validates
    /// against an unresolved sentinel and rejects with
    /// <c>SignatureDoesNotMatch</c>.
    ///
    /// <para>Defaults to <c>false</c> for backward compatibility. S3
    /// keeps it off because S3 has its own streaming/multipart payload
    /// strategy. Services that speak AWS JSON 1.0/1.1 with bounded
    /// request bodies (DynamoDB, SQS-Json) should enable it.</para>
    ///
    /// <para>The registry re-points
    /// <see cref="HttpRequest.Body"/> at the buffered copy so the
    /// module's parser sees the same bytes it would have read from
    /// the original stream.</para>
    /// </summary>
    bool BuffersRequestBodyForSigV4 => false;

    /// <summary>
    /// Headers (lowercase) that MUST appear in the request's
    /// <c>SignedHeaders</c> list for the signature to be accepted.
    /// Used by services whose dispatch / routing depends on a header
    /// that isn't part of the canonical request by default — most
    /// notably AWS-JSON services that dispatch on <c>X-Amz-Target</c>.
    /// An empty list (default) means no additional headers beyond
    /// SigV4's intrinsic ones are required to be signed.
    /// </summary>
    IReadOnlyList<string> RequiredSignedHeaders => Array.Empty<string>();

    /// <summary>Format used to render error responses (XML for S3, JSON elsewhere).</summary>
    AwsErrorFormat ErrorFormat { get; }

    /// <summary>Entry point invoked after routing and SigV4 validation.</summary>
    ValueTask HandleAsync(HttpContext context);

    /// <summary>
    /// Renders an authentication/authorization error in a format the module's
    /// on-the-wire callers can parse. The default implementation uses the
    /// module-level <see cref="ErrorFormat"/>, which is correct for modules
    /// whose callers speak a single protocol (S3 → XML, modern AWS-JSON
    /// services → JSON). Modules whose callers split across multiple wire
    /// protocols (e.g. SQS Query vs AWS-JSON 1.0) override this to negotiate
    /// the per-request protocol before rendering.
    /// </summary>
    ValueTask EmitAuthErrorAsync(HttpContext context, int statusCode, string code, string message)
        => new(AwsErrorResponse.WriteAsync(context, ErrorFormat, statusCode, code, message));

    /// <summary>
    /// Renders a SigV4 validation failure using the (HTTP status, error code)
    /// vocabulary real AWS returns for this module's wire protocol. The default
    /// keys the vocabulary on <see cref="ErrorFormat"/> via
    /// <see cref="AuthErrorVocabulary"/> — REST-XML services (S3) keep the
    /// 403 + S3-code shape, AWS-JSON services (DynamoDB, Kinesis) get the
    /// 400 + <c>InvalidSignatureException</c>/<c>UnrecognizedClientException</c>
    /// shape — then delegates rendering to <see cref="EmitAuthErrorAsync"/>.
    /// Modules whose callers split across wire protocols (SQS Query vs
    /// AWS-JSON) override this to negotiate the per-request format before
    /// resolving the vocabulary. See issue #241.
    /// </summary>
    ValueTask EmitSigV4FailureAsync(HttpContext context, SigV4ValidationStatus status, string reason)
    {
        var (statusCode, code) = AuthErrorVocabulary.Resolve(ErrorFormat, status);
        return EmitAuthErrorAsync(context, statusCode, code,
            string.IsNullOrEmpty(reason) ? code : reason);
    }
}
