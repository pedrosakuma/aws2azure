using System.Buffers.Binary;
using System.Text;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Codec for the AMQP 1.0 <c>multiple&lt;symbol&gt;</c> type (§1.6.30):
/// a value that may be encoded on the wire as <c>null</c>, a single
/// <c>symbol</c>, or an <c>array</c> of <c>symbol</c>. The latter two
/// MUST round-trip to the same logical list of zero-or-more symbol
/// strings; this codec normalises both into a <see cref="string"/>
/// array on read and always emits the array form on write so the
/// wire shape is deterministic.
/// </summary>
/// <remarks>
/// Used by every <c>capabilities</c> / <c>locales</c> field on
/// <see cref="Aws2Azure.Amqp.Framing.AmqpOpen"/>,
/// <see cref="Aws2Azure.Amqp.Framing.AmqpBegin"/>,
/// <see cref="Aws2Azure.Amqp.Framing.AmqpAttach"/>, and by
/// <see cref="Aws2Azure.Amqp.Framing.SaslMechanisms"/>
/// <c>server-mechanisms</c>.
/// </remarks>
internal static class AmqpMultipleSymbol
{
    /// <summary>
    /// Encodes <paramref name="symbols"/> as a symbol8 array. The empty
    /// case writes an <c>array8</c> with count=0 (callers that need to
    /// elide the field entirely should write a null marker instead).
    /// </summary>
    public static void Write(Span<byte> destination, ReadOnlySpan<string> symbols, out int written)
    {
        Span<byte> elementData = stackalloc byte[512];
        var dataLen = 0;
        for (var i = 0; i < symbols.Length; i++)
        {
            var s = symbols[i] ?? string.Empty;
            for (var j = 0; j < s.Length; j++)
            {
                if (s[j] > 0x7F)
                {
                    throw new ArgumentException("Symbol must be ASCII.", nameof(symbols));
                }
            }
            if (s.Length > byte.MaxValue)
            {
                // The 1-byte length prefix on symbol8 caps each entry at 255 bytes;
                // none of the AMQP/SB capabilities or mechanisms approach that.
                throw new ArgumentException("Symbol exceeds 255 bytes.", nameof(symbols));
            }
            if (dataLen + 1 + s.Length > elementData.Length)
            {
                throw new ArgumentException("multiple<symbol> exceeds scratch buffer.", nameof(symbols));
            }
            elementData[dataLen++] = (byte)s.Length;
            for (var j = 0; j < s.Length; j++)
            {
                elementData[dataLen++] = (byte)s[j];
            }
        }
        ReadOnlySpan<byte> symbolConstructor = stackalloc byte[] { AmqpFormatCode.Symbol8 };
        AmqpCompoundWriter.WriteArray(
            destination,
            symbolConstructor,
            elementData[..dataLen],
            symbols.Length,
            out written);
    }

    /// <summary>
    /// Decodes a <c>multiple&lt;symbol&gt;</c> value into a string array.
    /// Returns an empty array when the value is <c>null</c>. A single
    /// <c>symbol</c> primitive yields a one-element array; an
    /// <c>array</c> of symbol yields one entry per element.
    /// </summary>
    public static string[] Read(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty || source[0] == AmqpFormatCode.Null)
        {
            return Array.Empty<string>();
        }
        var code = source[0];
        if (code == AmqpFormatCode.Symbol8 || code == AmqpFormatCode.Symbol32)
        {
            var single = AmqpVariableReader.ReadSymbol(source, out _);
            return new[] { single };
        }
        if (code != AmqpFormatCode.Array8 && code != AmqpFormatCode.Array32)
        {
            throw new InvalidDataException(
                $"Expected symbol or array of symbol, got AMQP format code 0x{code:X2}.");
        }
        var arr = AmqpCompoundReader.ReadArray(source, out _);
        if (arr.ElementConstructor != AmqpFormatCode.Symbol8
            && arr.ElementConstructor != AmqpFormatCode.Symbol32)
        {
            throw new InvalidDataException(
                $"multiple<symbol> array element constructor must be symbol, got 0x{arr.ElementConstructor:X2}.");
        }
        if (arr.Count == 0) return Array.Empty<string>();
        var result = new string[arr.Count];
        var data = arr.ElementData;
        var offset = 0;
        for (var i = 0; i < arr.Count; i++)
        {
            int len, headerLen;
            if (arr.ElementConstructor == AmqpFormatCode.Symbol8)
            {
                len = data[offset];
                headerLen = 1;
            }
            else
            {
                len = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
                headerLen = 4;
            }
            var bytes = data.Slice(offset + headerLen, len);
            for (var j = 0; j < bytes.Length; j++)
            {
                if (bytes[j] > 0x7F)
                {
                    throw new InvalidDataException("Symbol contains non-ASCII byte.");
                }
            }
            result[i] = Encoding.ASCII.GetString(bytes);
            offset += headerLen + len;
        }
        return result;
    }

    /// <summary>
    /// Allocation-free enumerator over a <c>multiple&lt;symbol&gt;</c>
    /// value: yields each symbol as a <see cref="ReadOnlySpan{T}"/> of
    /// its on-wire ASCII bytes without ever materialising a
    /// <see cref="string"/>. Suitable for capability / mechanism
    /// membership checks against <c>"..."u8</c> literals; use
    /// <see cref="Read"/> when you genuinely need a managed string
    /// array (e.g. for logging / error messages).
    /// </summary>
    /// <remarks>
    /// Mirrors the three accepted wire shapes from <see cref="Read"/>:
    /// <c>null</c> → empty enumerator, single <c>symbol</c> → one
    /// element, <c>array</c> of symbol → one element per array entry.
    /// Throws <see cref="InvalidDataException"/> on malformed input;
    /// validation rules match <see cref="Read"/>.
    /// </remarks>
    public static Enumerator Enumerate(ReadOnlySpan<byte> source) => new(source);

    /// <summary>
    /// Returns <c>true</c> when the <c>multiple&lt;symbol&gt;</c>
    /// encoded in <paramref name="source"/> contains a symbol byte-
    /// equal to <paramref name="needle"/>. Allocation-free; intended
    /// for "does the peer offer SASL X?" / "did the broker advertise
    /// capability Y?" checks where the answer is the only thing
    /// needed.
    /// </summary>
    public static bool ContainsSymbol(ReadOnlySpan<byte> source, ReadOnlySpan<byte> needle)
    {
        var enumerator = Enumerate(source);
        while (enumerator.MoveNext())
        {
            if (enumerator.Current.SequenceEqual(needle)) return true;
        }
        return false;
    }

    /// <summary>
    /// Walks a <c>multiple&lt;symbol&gt;</c> wire value one symbol at a
    /// time. <see cref="Current"/> is a span into the source buffer; do
    /// not retain it past further <see cref="MoveNext"/> calls or past
    /// the lifetime of the source span.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _source;
        private readonly bool _isSingleSymbol;
        private readonly byte _elementConstructor;
        private readonly int _count;
        private ReadOnlySpan<byte> _elementData;
        private int _offset;
        private int _index;
        private ReadOnlySpan<byte> _current;
        private bool _exhausted;

        internal Enumerator(ReadOnlySpan<byte> source)
        {
            _current = default;
            _exhausted = false;
            _index = 0;
            _offset = 0;

            if (source.IsEmpty || source[0] == AmqpFormatCode.Null)
            {
                _source = default;
                _isSingleSymbol = false;
                _elementConstructor = 0;
                _count = 0;
                _elementData = default;
                _exhausted = true;
                return;
            }

            var code = source[0];
            if (code == AmqpFormatCode.Symbol8 || code == AmqpFormatCode.Symbol32)
            {
                _source = source;
                _isSingleSymbol = true;
                _elementConstructor = 0;
                _count = 1;
                _elementData = default;
                return;
            }
            if (code != AmqpFormatCode.Array8 && code != AmqpFormatCode.Array32)
            {
                throw new InvalidDataException(
                    $"Expected symbol or array of symbol, got AMQP format code 0x{code:X2}.");
            }

            var arr = AmqpCompoundReader.ReadArray(source, out _);
            if (arr.ElementConstructor != AmqpFormatCode.Symbol8
                && arr.ElementConstructor != AmqpFormatCode.Symbol32)
            {
                throw new InvalidDataException(
                    $"multiple<symbol> array element constructor must be symbol, got 0x{arr.ElementConstructor:X2}.");
            }

            _source = default;
            _isSingleSymbol = false;
            _elementConstructor = arr.ElementConstructor;
            _count = arr.Count;
            _elementData = arr.ElementData;
        }

        public ReadOnlySpan<byte> Current => _current;

        public bool MoveNext()
        {
            if (_exhausted || _index >= _count) { _exhausted = true; return false; }

            if (_isSingleSymbol)
            {
                _current = AmqpVariableReader.ReadSymbolBytes(_source, out _);
                _index++;
                return true;
            }

            int len, headerLen;
            if (_elementConstructor == AmqpFormatCode.Symbol8)
            {
                len = _elementData[_offset];
                headerLen = 1;
            }
            else
            {
                len = checked((int)BinaryPrimitives.ReadUInt32BigEndian(_elementData[_offset..]));
                headerLen = 4;
            }
            var bytes = _elementData.Slice(_offset + headerLen, len);
            for (var j = 0; j < bytes.Length; j++)
            {
                if (bytes[j] > 0x7F)
                {
                    throw new InvalidDataException("Symbol contains non-ASCII byte.");
                }
            }
            _current = bytes;
            _offset += headerLen + len;
            _index++;
            return true;
        }

        public Enumerator GetEnumerator() => this;
    }
}
