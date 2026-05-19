using Aws2Azure.Core.Modules;
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
}
