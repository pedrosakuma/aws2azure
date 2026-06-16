using System.IO;
using System.Text;

namespace Aws2Azure.Core.Xml;

/// <summary>
/// A <see cref="StringWriter"/> that reports UTF-8 as its encoding so
/// <see cref="System.Xml.XmlWriter"/> emits <c>encoding="utf-8"</c> in the XML
/// declaration. The default <see cref="StringWriter"/> reports UTF-16 (because
/// it buffers into a <see cref="StringBuilder"/>), which would otherwise leak a
/// <c>utf-16</c> declaration into AWS-shaped XML response bodies that are then
/// written to the (UTF-8) HTTP response.
/// </summary>
/// <remarks>
/// Shared across every module that renders AWS XML envelopes (SQS, SNS, S3 error
/// bodies, …). Previously each module carried its own private copy of this
/// three-line workaround.
/// </remarks>
public sealed class Utf8StringWriter : StringWriter
{
    public Utf8StringWriter(StringBuilder builder) : base(builder)
    {
    }

    public override Encoding Encoding => Encoding.UTF8;
}
