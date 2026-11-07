using System.IO.Compression;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// What the compression modules share: the checksums, the decompression cap, and an
/// in-memory file object.
/// </summary>
/// <remarks>
/// <para>
/// Decompression is where a sandbox meets a bomb: a few kilobytes of deflate can stand for
/// gigabytes, and inflating them happens inside one builtin call, which the instruction
/// limit does not see. So every inflate in <c>zlib</c>, <c>gzip</c> and <c>zipfile</c> reads
/// through <see cref="Inflate"/>, which stops at <see cref="MaxOutput"/> and raises rather
/// than returning a truncated result — a partial file reported as the whole would be the
/// silent failure CLAUDE.md rules out.
/// </para>
/// <para>
/// The compressors are .NET's (zlib-ng underneath), so compressed bytes are valid and
/// interoperable but not byte-identical to CPython's for the same level.
/// </para>
/// </remarks>
internal static class Compression
{
    /// <summary>The most bytes one decompression may produce: 64 MiB.</summary>
    public const int MaxOutput = 1 << 26;

    /// <summary>Raised when a decompression would pass <see cref="MaxOutput"/>.</summary>
    public static PyRaise TooLarge() => new(new PyException(
        PyExceptionType.MemoryError,
        $"decompressed data would exceed the sandbox limit of {MaxOutput} bytes"));

    /// <summary>Reads a decompressing stream to its end, refusing more than the cap.</summary>
    public static byte[] Inflate(Stream stream, long limit = MaxOutput)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > limit)
            {
                throw TooLarge();
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>CRC-32, continuing from <paramref name="start"/>, as <c>zlib.crc32</c> computes it.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data, uint start = 0)
    {
        var crc = ~start;

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    /// <summary>Adler-32, continuing from <paramref name="start"/>.</summary>
    public static uint Adler32(ReadOnlySpan<byte> data, uint start = 1)
    {
        uint a = start & 0xFFFF;
        uint b = start >> 16;

        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>Maps a zlib level (-1 to 9) to the nearest .NET compression level.</summary>
    public static CompressionLevel Level(BigInteger level, PyExceptionType error) => (int)BigInteger.Clamp(level, -2, 10) switch
    {
        -1 => CompressionLevel.Optimal,
        0 => CompressionLevel.NoCompression,
        1 or 2 or 3 => CompressionLevel.Fastest,
        4 or 5 or 6 => CompressionLevel.Optimal,
        7 or 8 or 9 => CompressionLevel.SmallestSize,
        _ => throw new PyRaise(new PyException(error, "Bad compression level")),
    };

    /// <summary>Raw deflate of <paramref name="data"/>.</summary>
    public static byte[] Deflate(byte[] data, CompressionLevel level)
    {
        using var output = new MemoryStream();

        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            deflate.Write(data);
        }

        // .NET writes nothing at all for empty input, which is not a deflate stream; zlib
        // writes one empty final block, and so does this.
        return output.Length == 0 ? [0x03, 0x00] : output.ToArray();
    }

    /// <summary>Raw inflate, with the cap, translating a corrupt stream to <paramref name="error"/>.</summary>
    public static byte[] InflateRaw(byte[] data, int offset, PyExceptionType error, string action)
    {
        try
        {
            using var input = new MemoryStream(data, offset, data.Length - offset);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            return Inflate(inflate);
        }
        catch (InvalidDataException)
        {
            throw new PyRaise(new PyException(error, $"Error -3 while {action} data: invalid block type"));
        }
    }

    /// <summary>The bytes of a bytes-like argument.</summary>
    public static byte[] Data(PyObject value, string function) => value switch
    {
        PyBytes bytes => bytes.Value,
        _ => throw new PyRaise(PyErrors.TypeError($"a bytes-like object is required, not '{value.TypeName}'")),
    };

    /// <summary>A path argument: a str, a <c>Path</c> or a <c>DirEntry</c>.</summary>
    public static string PathText(PyObject value) => value switch
    {
        PyStr text => text.Value,
        PyPath path => path.Value,
        PyDirEntry entry => entry.Path,
        _ => throw new PyRaise(PyErrors.TypeError(
            $"expected str, bytes or os.PathLike object, not {value.TypeName}")),
    };

    /// <summary>An argument given positionally or by keyword.</summary>
    public static PyObject? Argument(PyObject[] arguments, PyDict? keywords, int position, string name) =>
        arguments.Length > position ? arguments[position]
        : keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value
        : null;
}

/// <summary>
/// A file object over bytes held in memory — what <c>gzip.open</c> and
/// <c>ZipFile.open</c> return.
/// </summary>
/// <remarks>
/// A reader holds the whole decompressed content (capped); a writer collects what is
/// written and hands it to <c>onClose</c> when closed — or when the run ends, so a program
/// that forgot to close still leaves its file behind.
/// </remarks>
internal sealed class MemoryFile : PyObject
{
    private readonly byte[] _content;
    private readonly string? _text;
    private readonly bool _binary;
    private readonly Action<byte[]>? _onClose;
    private readonly MemoryStream? _written;
    private int _position;
    private bool _closed;

    public MemoryFile(string typeName, string name, string mode, byte[] content, bool binary, Action<byte[]>? onClose, VirtualMachine? machine)
    {
        TypeNameValue = typeName;
        Name = name;
        Mode = mode;
        _content = content;
        _binary = binary;
        _onClose = onClose;

        if (onClose is not null)
        {
            _written = new MemoryStream();
            machine?.WhenRunCompleted(Close);
        }
        else if (!binary)
        {
            _text = Encoding.UTF8.GetString(content);
        }
    }

    private string TypeNameValue { get; }

    public string Name { get; }

    public string Mode { get; }

    public override string TypeName => TypeNameValue;

    public override string Repr() => $"<{TypeNameValue} name={PyStr.Quote(Name)} mode={PyStr.Quote(Mode)}>";

    private bool Writable => _written is not null;

    private int Available => _binary ? _content.Length : _text!.Length;

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        if (_written is not null)
        {
            _onClose!(_written.ToArray());
        }
    }

    private PyObject Slice(int start, int length) =>
        _binary ? new PyBytes(_content.AsSpan(start, length).ToArray()) : new PyStr(_text!.Substring(start, length));

    private PyObject? ReadLine(int limit)
    {
        if (_position >= Available)
        {
            return null;
        }

        var end = _binary
            ? Array.IndexOf(_content, (byte)'\n', _position)
            : _text!.IndexOf('\n', _position);
        var length = end < 0 ? Available - _position : end - _position + 1;

        if (limit >= 0 && limit < length)
        {
            length = limit;
        }

        var line = Slice(_position, length);
        _position += length;
        return line;
    }

    public override IEnumerable<PyObject>? Iterate()
    {
        RequireReadable();

        while (ReadLine(-1) is { } line)
        {
            yield return line;
        }
    }

    private void RequireOpen()
    {
        if (_closed)
        {
            throw new PyRaise(PyErrors.ValueError("I/O operation on closed file."));
        }
    }

    private void RequireReadable()
    {
        RequireOpen();

        if (Writable)
        {
            throw new PyRaise(new PyException(PyExceptionType.UnsupportedOperation, "read"));
        }
    }

    private void Write(PyObject value)
    {
        RequireOpen();

        if (!Writable)
        {
            throw new PyRaise(new PyException(PyExceptionType.UnsupportedOperation, "write"));
        }

        var bytes = (_binary, value) switch
        {
            (true, PyBytes raw) => raw.Value,
            (false, PyStr text) => Encoding.UTF8.GetBytes(text.Value),
            (true, _) => throw new PyRaise(PyErrors.TypeError($"memoryview: a bytes-like object is required, not '{value.TypeName}'")),
            _ => throw new PyRaise(PyErrors.TypeError($"write() argument must be str, not {value.TypeName}")),
        };

        if (_written!.Length + bytes.Length > Compression.MaxOutput)
        {
            throw Compression.TooLarge();
        }

        _written.Write(bytes);
    }

    public override PyObject? GetAttribute(string name) => name switch
    {
        "name" => new PyStr(Name),
        "mode" => new PyStr(Mode),
        "closed" => PyBool.Of(_closed),
        "readable" => new PyBuiltinFunction(name, _ => PyBool.Of(!Writable)),
        "writable" => new PyBuiltinFunction(name, _ => PyBool.Of(Writable)),
        "seekable" => new PyBuiltinFunction(name, _ => PyBool.False),
        "flush" => new PyBuiltinFunction(name, _ => PyNone.Instance),
        "close" => new PyBuiltinFunction(name, _ =>
        {
            Close();
            return PyNone.Instance;
        }),
        "__enter__" => new PyBuiltinFunction(name, _ =>
        {
            RequireOpen();
            return this;
        }),
        "__exit__" => new PyBuiltinFunction(name, _ =>
        {
            Close();
            return PyBool.False;
        }),
        "__iter__" => new PyBuiltinFunction(name, _ => new PyIterator(Iterate()!)),
        "read" or "read1" => new PyBuiltinFunction(name, arguments =>
        {
            RequireReadable();
            var size = arguments.Length > 0 && arguments[0] is not PyNone
                ? (long)Builtins.BuiltinNamespace.RequireInt(arguments[0], name)
                : -1;
            var length = size < 0 ? Available - _position : (int)Math.Min(size, Math.Max(0, Available - _position));
            length = Math.Max(0, length);
            var chunk = Slice(Math.Min(_position, Available), length);
            _position += length;
            return chunk;
        }),
        "readline" => new PyBuiltinFunction(name, arguments =>
        {
            RequireReadable();
            var limit = arguments.Length > 0 && arguments[0] is not PyNone
                ? (int)BigInteger.Clamp(Builtins.BuiltinNamespace.RequireInt(arguments[0], name), -1, int.MaxValue)
                : -1;
            return ReadLine(limit) ?? (_binary ? new PyBytes([]) : new PyStr(string.Empty));
        }),
        "readlines" => new PyBuiltinFunction(name, _ => new PyList([.. Iterate()!])),
        "write" => new PyBuiltinFunction(name, arguments =>
        {
            Arity.ExactCount("write", arguments, 1);
            Write(arguments[0]);
            return PyInt.From(arguments[0] is PyBytes raw ? raw.Value.Length : arguments[0].Length() ?? 0);
        }),
        "writelines" => new PyBuiltinFunction(name, arguments =>
        {
            foreach (var line in VirtualMachine.RequireIterable(arguments[0]))
            {
                Write(line);
            }

            return PyNone.Instance;
        }),
        _ => null,
    };
}
