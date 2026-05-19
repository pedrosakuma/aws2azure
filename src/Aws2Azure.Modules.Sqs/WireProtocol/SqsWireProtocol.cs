namespace Aws2Azure.Modules.Sqs.WireProtocol;

/// <summary>
/// Identifies which SQS on-the-wire form the caller used.
/// <para>
/// SQS supports two protocols on the same endpoint:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Query protocol</b> (<c>POST /</c>, form-encoded body or query
///       string) — used by legacy AWS SDKs and signed-URL callers. The
///       operation is conveyed by the <c>Action</c> parameter.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>AWS JSON 1.0</b> (<c>POST /</c>, JSON body) — used by v2/v3 SDKs
///       that negotiate JSON. The operation is conveyed by the
///       <c>X-Amz-Target</c> header (<c>AmazonSQS.&lt;Op&gt;</c>).
///     </description>
///   </item>
/// </list>
/// The proxy must respond in the *same* protocol the caller used (XML for
/// query, JSON for AWS JSON) so SDKs deserialise the response correctly.
/// </summary>
public enum SqsWireProtocol
{
    Unknown,
    Query,
    AwsJson,
}
