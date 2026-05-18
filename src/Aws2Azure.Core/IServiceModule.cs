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
}
