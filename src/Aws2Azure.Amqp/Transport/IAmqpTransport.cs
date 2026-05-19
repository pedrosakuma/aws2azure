using System.IO.Pipelines;

namespace Aws2Azure.Amqp.Transport;

/// <summary>
/// Duplex byte-stream transport used by the AMQP connection layer.
/// Backed by <see cref="System.IO.Pipelines.Pipe"/> on both sides so callers
/// can read incoming bytes without per-call buffer allocations.
/// </summary>
/// <remarks>
/// The transport is half-duplex-safe: <see cref="Input"/> and
/// <see cref="Output"/> can be driven concurrently. Disposal completes both
/// sides and (optionally) closes the underlying stream.
/// </remarks>
internal interface IAmqpTransport : IAsyncDisposable
{
    PipeReader Input { get; }
    PipeWriter Output { get; }
}
