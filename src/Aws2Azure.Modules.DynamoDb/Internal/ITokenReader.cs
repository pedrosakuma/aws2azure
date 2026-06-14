using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// The subset of the <see cref="Utf8JsonReader"/> token-stream surface that the
/// DynamoDB response transforms consume. Abstracting the reader lets a single
/// generic transform be driven by either JSON text
/// (<see cref="Utf8JsonTokenReader"/>) or the Cosmos binary JSON format
/// (<c>CosmosBinaryReader</c>), so the binary path can skip the intermediate
/// JSON-text materialization without duplicating the transform logic.
///
/// <para>Consumed with the <c>allows ref struct</c> anti-constraint so the
/// generic transform monomorphizes per concrete reader and the JIT
/// devirtualizes these calls — no boxing, no reflection, AOT-clean.</para>
/// </summary>
internal interface ITokenReader
{
    /// <summary>The token the reader is currently positioned on.</summary>
    JsonTokenType TokenType { get; }

    /// <summary>Whether the current string/property-name token contains JSON
    /// escapes (i.e. <see cref="ValueSpan"/> is the raw, still-escaped form).</summary>
    bool ValueIsEscaped { get; }

    /// <summary>Whether the current value spans multiple segments and must be
    /// read via <see cref="ValueSequence"/> rather than <see cref="ValueSpan"/>.</summary>
    bool HasValueSequence { get; }

    /// <summary>The current value's contiguous UTF-8 bytes (valid only when
    /// <see cref="HasValueSequence"/> is <c>false</c>).</summary>
    [UnscopedRef] ReadOnlySpan<byte> ValueSpan { get; }

    /// <summary>The current value's multi-segment UTF-8 bytes (valid only when
    /// <see cref="HasValueSequence"/> is <c>true</c>).</summary>
    ReadOnlySequence<byte> ValueSequence { get; }

    /// <summary>Advances to the next token. Returns <c>false</c> at end of input.</summary>
    bool Read();

    /// <summary>When positioned on a container start token, advances to the
    /// matching end token; otherwise a no-op. Mirrors <see cref="Utf8JsonReader.Skip"/>.</summary>
    void Skip();

    /// <summary>Compares the current string/property-name token against the
    /// given UTF-8 text, transparently handling escapes.</summary>
    bool ValueTextEquals(ReadOnlySpan<byte> utf8Text);

    /// <summary>Copies the current string token's unescaped UTF-8 value into
    /// <paramref name="destination"/> and returns the number of bytes written.</summary>
    int CopyString(scoped Span<byte> destination);
}

/// <summary>
/// <see cref="ITokenReader"/> adapter over a real <see cref="Utf8JsonReader"/>.
/// Forwards every member 1:1, so JSON text drives the same generic transform as
/// the binary reader. This is the production text path.
/// </summary>
internal ref struct Utf8JsonTokenReader : ITokenReader
{
    private Utf8JsonReader _reader;

    public Utf8JsonTokenReader(ReadOnlySpan<byte> utf8Json) => _reader = new Utf8JsonReader(utf8Json);

    public readonly JsonTokenType TokenType => _reader.TokenType;
    public readonly bool ValueIsEscaped => _reader.ValueIsEscaped;
    public readonly bool HasValueSequence => _reader.HasValueSequence;
    [UnscopedRef] public readonly ReadOnlySpan<byte> ValueSpan => _reader.ValueSpan;
    public readonly ReadOnlySequence<byte> ValueSequence => _reader.ValueSequence;

    public bool Read() => _reader.Read();
    public void Skip() => _reader.Skip();
    public readonly bool ValueTextEquals(ReadOnlySpan<byte> utf8Text) => _reader.ValueTextEquals(utf8Text);
    public readonly int CopyString(scoped Span<byte> destination) => _reader.CopyString(destination);
}
