namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// A DEFLATE decoder (RFC 1951), after Mark Adler's <c>puff.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// .NET's <c>DeflateStream</c> cannot be used to decompress here, for two reasons a sandbox
/// cares about: it ends a truncated stream silently, handing back part of the data as
/// though it were all of it, and it does not say where the stream ended, so neither the
/// zlib/gzip trailer that follows nor <c>unused_data</c> can be found. This decoder reports
/// both, and stops at a byte limit rather than inflating a bomb.
/// </para>
/// <para>
/// It decodes canonical Huffman codes a bit at a time — simple, and fast enough for the
/// sizes the cap allows.
/// </para>
/// </remarks>
internal sealed class Inflater
{
    private static readonly short[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static readonly short[] LengthExtra =
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static readonly short[] DistanceBase =
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    private static readonly short[] DistanceExtra =
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static readonly short[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly (Huffman Lengths, Huffman Distances) Fixed = BuildFixed();

    private readonly byte[] _input;
    private readonly int _limit;
    private int _position;
    private int _bitBuffer;
    private int _bitCount;
    private byte[] _output = new byte[4096];
    private int _outputCount;

    private Inflater(byte[] input, int start, int limit)
    {
        _input = input;
        _position = start;
        _limit = limit;
    }

    /// <summary>The outcome of decoding a stream.</summary>
    /// <param name="Output">Everything decoded — all of it, or as far as the input went.</param>
    /// <param name="End">The offset of the first byte after the stream, when complete.</param>
    /// <param name="Complete">Whether the final block was reached.</param>
    internal readonly record struct Result(byte[] Output, int End, bool Complete);

    /// <summary>A malformed stream, with zlib's wording for what was wrong.</summary>
    internal sealed class FormatException(string message) : Exception(message);

    private sealed class TruncatedException : Exception;

    /// <summary>Decodes the raw deflate stream starting at <paramref name="start"/>.</summary>
    public static Result Inflate(byte[] input, int start, int limit = Compression.MaxOutput)
    {
        var inflater = new Inflater(input, start, limit);

        try
        {
            int last;

            do
            {
                last = inflater.Bits(1);
                var type = inflater.Bits(2);

                switch (type)
                {
                    case 0: inflater.Stored(); break;
                    case 1: inflater.Codes(Fixed.Lengths, Fixed.Distances); break;
                    case 2: inflater.Dynamic(); break;
                    default: throw new FormatException("invalid block type");
                }
            }
            while (last == 0);

            return new Result(inflater.Output(), inflater._position, true);
        }
        catch (TruncatedException)
        {
            return new Result(inflater.Output(), input.Length, false);
        }
    }

    private byte[] Output() => _output.AsSpan(0, _outputCount).ToArray();

    private int Bits(int need)
    {
        var value = _bitBuffer;

        while (_bitCount < need)
        {
            if (_position >= _input.Length)
            {
                throw new TruncatedException();
            }

            value |= _input[_position++] << _bitCount;
            _bitCount += 8;
        }

        _bitBuffer = value >> need;
        _bitCount -= need;
        return value & ((1 << need) - 1);
    }

    private void Emit(byte value)
    {
        if (_outputCount == _output.Length)
        {
            if (_outputCount >= _limit)
            {
                throw Compression.TooLarge();
            }

            Array.Resize(ref _output, (int)Math.Min((long)_output.Length * 2, Math.Max(_limit, 1)));
        }

        _output[_outputCount++] = value;
    }

    private void Stored()
    {
        _bitBuffer = 0;
        _bitCount = 0;

        if (_position + 4 > _input.Length)
        {
            throw new TruncatedException();
        }

        var length = _input[_position] | (_input[_position + 1] << 8);
        var complement = _input[_position + 2] | (_input[_position + 3] << 8);
        _position += 4;

        if (length != (~complement & 0xFFFF))
        {
            throw new FormatException("invalid stored block lengths");
        }

        for (var i = 0; i < length; i++)
        {
            if (_position >= _input.Length)
            {
                throw new TruncatedException();
            }

            Emit(_input[_position++]);
        }
    }

    /// <summary>A canonical Huffman code: how many codes of each length, and the symbols in order.</summary>
    private sealed class Huffman(int symbols)
    {
        public short[] Count { get; } = new short[16];

        public short[] Symbol { get; } = new short[symbols];
    }

    /// <summary>Builds a code from its lengths; returns the number of unused codes (negative when over-subscribed).</summary>
    private static int Construct(Huffman code, ReadOnlySpan<short> lengths)
    {
        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            code.Count[lengths[symbol]]++;
        }

        if (code.Count[0] == lengths.Length)
        {
            return 0;
        }

        var left = 1;

        for (var length = 1; length < 16; length++)
        {
            left <<= 1;
            left -= code.Count[length];

            if (left < 0)
            {
                return left;
            }
        }

        var offsets = new short[16];

        for (var length = 1; length < 15; length++)
        {
            offsets[length + 1] = (short)(offsets[length] + code.Count[length]);
        }

        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                code.Symbol[offsets[lengths[symbol]]++] = (short)symbol;
            }
        }

        return left;
    }

    private int Decode(Huffman code)
    {
        int value = 0, first = 0, index = 0;

        for (var length = 1; length < 16; length++)
        {
            value |= Bits(1);
            int count = code.Count[length];

            if (value - count < first)
            {
                return code.Symbol[index + (value - first)];
            }

            index += count;
            first += count;
            first <<= 1;
            value <<= 1;
        }

        return -1;
    }

    private void Codes(Huffman lengths, Huffman distances)
    {
        while (true)
        {
            var symbol = Decode(lengths);

            if (symbol < 0)
            {
                throw new FormatException("invalid literal/length code");
            }

            if (symbol < 256)
            {
                Emit((byte)symbol);
                continue;
            }

            if (symbol == 256)
            {
                return;
            }

            symbol -= 257;

            if (symbol >= 29)
            {
                throw new FormatException("invalid literal/length code");
            }

            var length = LengthBase[symbol] + Bits(LengthExtra[symbol]);
            symbol = Decode(distances);

            if (symbol is < 0 or >= 30)
            {
                throw new FormatException("invalid distance code");
            }

            var distance = DistanceBase[symbol] + Bits(DistanceExtra[symbol]);

            if (distance > _outputCount)
            {
                throw new FormatException("invalid distance too far back");
            }

            for (var i = 0; i < length; i++)
            {
                Emit(_output[_outputCount - distance]);
            }
        }
    }

    private static (Huffman, Huffman) BuildFixed()
    {
        var lengths = new short[288];

        for (var symbol = 0; symbol < 144; symbol++)
        {
            lengths[symbol] = 8;
        }

        for (var symbol = 144; symbol < 256; symbol++)
        {
            lengths[symbol] = 9;
        }

        for (var symbol = 256; symbol < 280; symbol++)
        {
            lengths[symbol] = 7;
        }

        for (var symbol = 280; symbol < 288; symbol++)
        {
            lengths[symbol] = 8;
        }

        var literal = new Huffman(288);
        Construct(literal, lengths);

        var distances = new short[30];
        Array.Fill(distances, (short)5);
        var distance = new Huffman(30);
        Construct(distance, distances);

        return (literal, distance);
    }

    private void Dynamic()
    {
        var literalCount = Bits(5) + 257;
        var distanceCount = Bits(5) + 1;
        var codeCount = Bits(4) + 4;

        if (literalCount > 286 || distanceCount > 30)
        {
            throw new FormatException("too many length or distance symbols");
        }

        var lengths = new short[320];

        for (var index = 0; index < codeCount; index++)
        {
            lengths[CodeLengthOrder[index]] = (short)Bits(3);
        }

        var lengthCode = new Huffman(19);

        if (Construct(lengthCode, lengths.AsSpan(0, 19)) != 0)
        {
            throw new FormatException("invalid code lengths set");
        }

        Array.Clear(lengths);
        var position = 0;

        while (position < literalCount + distanceCount)
        {
            var symbol = Decode(lengthCode);

            if (symbol < 0)
            {
                throw new FormatException("invalid code lengths set");
            }

            if (symbol < 16)
            {
                lengths[position++] = (short)symbol;
                continue;
            }

            short repeated = 0;
            int count;

            if (symbol == 16)
            {
                if (position == 0)
                {
                    throw new FormatException("invalid bit length repeat");
                }

                repeated = lengths[position - 1];
                count = 3 + Bits(2);
            }
            else if (symbol == 17)
            {
                count = 3 + Bits(3);
            }
            else
            {
                count = 11 + Bits(7);
            }

            if (position + count > literalCount + distanceCount)
            {
                throw new FormatException("invalid bit length repeat");
            }

            while (count-- > 0)
            {
                lengths[position++] = repeated;
            }
        }

        if (lengths[256] == 0)
        {
            throw new FormatException("invalid code -- missing end-of-block");
        }

        var literal = new Huffman(286);
        var literalLeft = Construct(literal, lengths.AsSpan(0, literalCount));

        if (literalLeft < 0 || (literalLeft > 0 && literalCount - literal.Count[0] != 1))
        {
            throw new FormatException("invalid literal/lengths set");
        }

        var distance = new Huffman(30);
        var distanceLeft = Construct(distance, lengths.AsSpan(literalCount, distanceCount));

        if (distanceLeft < 0 || (distanceLeft > 0 && distanceCount - distance.Count[0] != 1))
        {
            throw new FormatException("invalid distances set");
        }

        Codes(literal, distance);
    }
}
