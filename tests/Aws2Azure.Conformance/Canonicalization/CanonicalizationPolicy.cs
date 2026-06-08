namespace Aws2Azure.Conformance.Canonicalization;

/// <summary>
/// The masking / projection policy applied when canonicalizing an AWS error
/// response. Encodes <em>which</em> fields are part of the faithful contract
/// surface and which carry non-deterministic values that must be masked before
/// comparison.
///
/// Defaults target the S3 XML error envelope and AWS-semantic headers. The sets
/// are case-insensitive. This is deliberately conservative for the first
/// vertical slice (issue #228): we compare the AWS-semantic surface and ignore
/// transport/server headers, rather than diffing every raw byte (which would be
/// pure noise).
/// </summary>
public sealed class CanonicalizationPolicy
{
    /// <summary>
    /// Header names compared at all. Any response header not matching one of
    /// these (exact, case-insensitive) or the <see cref="SignificantHeaderPrefixes"/>
    /// is dropped as transport/server noise (Server, Date, Connection,
    /// Content-Length, Transfer-Encoding, …).
    /// </summary>
    public HashSet<string> SignificantHeaders { get; } =
        new(StringComparer.OrdinalIgnoreCase) { "content-type" };

    /// <summary>
    /// Header-name prefixes whose presence is part of the contract surface. All
    /// <c>x-amz-*</c> headers are compared so a missing or extra one (e.g. the
    /// server-side <c>x-amz-id-2</c>) surfaces as a divergence.
    /// </summary>
    public List<string> SignificantHeaderPrefixes { get; } = new() { "x-amz-" };

    /// <summary>
    /// Header names whose <em>value</em> is non-deterministic. The header's
    /// presence is still compared; its value is replaced with the mask.
    /// </summary>
    public HashSet<string> VolatileHeaderValues { get; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "x-amz-request-id",
            "x-amz-id-2",
            "date",
        };

    /// <summary>
    /// Error-envelope element names whose <em>value</em> is non-deterministic
    /// (request/host correlation ids). Presence is compared; value is masked.
    /// </summary>
    public HashSet<string> VolatileBodyElements { get; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "RequestId",
            "HostId",
        };

    /// <summary>
    /// Error-envelope element names whose value is informational and not part of
    /// the contract. AWS clients dispatch on HTTP status + <c>Code</c>, never on
    /// the human-readable <c>Message</c>, and the wording differs across AWS
    /// implementations — so the message text is masked while the element's
    /// presence is still asserted.
    /// </summary>
    public HashSet<string> NonContractualBodyElements { get; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Message",
        };

    public bool IsSignificantHeader(string name)
    {
        if (SignificantHeaders.Contains(name))
        {
            return true;
        }
        foreach (var prefix in SignificantHeaderPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static CanonicalizationPolicy Default { get; } = new();
}
