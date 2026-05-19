using System.IO.Pipelines;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Transport;

/// <summary>
/// Test helper: a duplex in-memory transport built on two
/// <see cref="Pipe"/>s, paired so what one side writes the peer reads.
/// Use <see cref="CreatePair"/> to get (local, peer).
/// </summary>
internal sealed class PipePairTransport : IAmqpTransport
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;

    private PipePairTransport(PipeReader input, PipeWriter output)
    {
        _input = input;
        _output = output;
    }

    public PipeReader Input => _input;
    public PipeWriter Output => _output;

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public static (IAmqpTransport Local, IAmqpTransport Peer) CreatePair()
    {
        var localToPeer = new Pipe();
        var peerToLocal = new Pipe();
        var local = new PipePairTransport(peerToLocal.Reader, localToPeer.Writer);
        var peer = new PipePairTransport(localToPeer.Reader, peerToLocal.Writer);
        return (local, peer);
    }
}
