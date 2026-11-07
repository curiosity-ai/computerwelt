using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>zlib</c> module: zlib, raw deflate and gzip streams, and the two checksums.
/// </summary>
/// <remarks>
/// Decompression is this repository's own decoder (<see cref="Inflater"/>), so a truncated
/// stream is an error as it is in CPython, and output stops at the sandbox's cap.
/// Compression is .NET's; the bytes differ from CPython's but decompress identically.
/// </remarks>
public static class ZlibModule
{
    /// <summary><c>zlib.error</c>.</summary>
    public static PyExceptionType Error { get; } = PyExceptionType.DefineHostException("error");

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("zlib");

        module.Add("error", Error);
        module.Add("MAX_WBITS", PyInt.From(15));
        module.Add("DEFLATED", PyInt.From(8));
        module.Add("DEF_MEM_LEVEL", PyInt.From(8));
        module.Add("DEF_BUF_SIZE", PyInt.From(16384));
        module.Add("Z_NO_COMPRESSION", PyInt.From(0));
        module.Add("Z_BEST_SPEED", PyInt.From(1));
        module.Add("Z_BEST_COMPRESSION", PyInt.From(9));
        module.Add("Z_DEFAULT_COMPRESSION", PyInt.From(-1));
        module.Add("Z_DEFAULT_STRATEGY", PyInt.From(0));
        module.Add("Z_FILTERED", PyInt.From(1));
        module.Add("Z_HUFFMAN_ONLY", PyInt.From(2));
        module.Add("Z_RLE", PyInt.From(3));
        module.Add("Z_FIXED", PyInt.From(4));
        module.Add("Z_NO_FLUSH", PyInt.From(0));
        module.Add("Z_PARTIAL_FLUSH", PyInt.From(1));
        module.Add("Z_SYNC_FLUSH", PyInt.From(2));
        module.Add("Z_FULL_FLUSH", PyInt.From(3));
        module.Add("Z_FINISH", PyInt.From(4));
        module.Add("Z_BLOCK", PyInt.From(5));
        module.Add("ZLIB_VERSION", new PyStr("1.3.1"));
        module.Add("ZLIB_RUNTIME_VERSION", new PyStr("1.3.1"));

        module.Add("crc32", arguments =>
        {
            Arity.Between("crc32", arguments, 1, 2);
            var start = arguments.Length > 1 ? (uint)(Builtins.BuiltinNamespace.RequireInt(arguments[1], "crc32") & uint.MaxValue) : 0u;
            return PyInt.From(Compression.Crc32(Compression.Data(arguments[0], "crc32"), start));
        });

        module.Add("adler32", arguments =>
        {
            Arity.Between("adler32", arguments, 1, 2);
            var start = arguments.Length > 1 ? (uint)(Builtins.BuiltinNamespace.RequireInt(arguments[1], "adler32") & uint.MaxValue) : 1u;
            return PyInt.From(Compression.Adler32(Compression.Data(arguments[0], "adler32"), start));
        });

        module.Add("compress", (arguments, keywords) =>
        {
            var data = Compression.Data(Compression.Argument(arguments, keywords, 0, "data")
                ?? throw new PyRaise(PyErrors.TypeError("compress() missing required argument 'data' (pos 1)")), "compress");
            var level = Compression.Argument(arguments, keywords, 1, "level") is { } given
                ? Builtins.BuiltinNamespace.RequireInt(given, "compress")
                : -1;
            var wbits = Compression.Argument(arguments, keywords, 2, "wbits") is { } bits
                ? (int)Builtins.BuiltinNamespace.RequireInt(bits, "compress")
                : 15;
            return new PyBytes(Compress(data, level, wbits));
        });

        module.Add("decompress", (arguments, keywords) =>
        {
            var data = Compression.Data(Compression.Argument(arguments, keywords, 0, "data")
                ?? throw new PyRaise(PyErrors.TypeError("decompress() missing required argument 'data' (pos 1)")), "decompress");
            var wbits = Compression.Argument(arguments, keywords, 1, "wbits") is { } bits
                ? (int)Builtins.BuiltinNamespace.RequireInt(bits, "decompress")
                : 15;
            var (output, _, complete) = Decompress(data, wbits);

            return complete
                ? new PyBytes(output)
                : throw new PyRaise(new PyException(Error, "Error -5 while decompressing data: incomplete or truncated stream"));
        });

        module.Add("compressobj", (arguments, keywords) =>
        {
            var level = Compression.Argument(arguments, keywords, 0, "level") is { } given
                ? Builtins.BuiltinNamespace.RequireInt(given, "compressobj")
                : -1;
            var wbits = Compression.Argument(arguments, keywords, 2, "wbits") is { } bits
                ? (int)Builtins.BuiltinNamespace.RequireInt(bits, "compressobj")
                : 15;
            _ = Compression.Level(level, Error);
            return new Compressor(level, wbits);
        });

        module.Add("decompressobj", (arguments, keywords) =>
        {
            var wbits = Compression.Argument(arguments, keywords, 0, "wbits") is { } bits
                ? (int)Builtins.BuiltinNamespace.RequireInt(bits, "decompressobj")
                : 15;
            return new Decompressor(wbits);
        });

        return module;
    }

    /// <summary>Compresses in the container <paramref name="wbits"/> names.</summary>
    internal static byte[] Compress(byte[] data, BigInteger level, int wbits)
    {
        var compressionLevel = Compression.Level(level, Error);
        var body = Compression.Deflate(data, compressionLevel);

        switch (wbits)
        {
            case >= 9 and <= 15:
            {
                var output = new byte[body.Length + 6];
                output[0] = 0x78;
                output[1] = (int)BigInteger.Clamp(level, -1, 9) switch
                {
                    0 or 1 => 0x01,
                    2 or 3 or 4 or 5 => 0x5E,
                    -1 or 6 => 0x9C,
                    _ => 0xDA,
                };
                body.CopyTo(output, 2);
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(output.Length - 4), Compression.Adler32(data));
                return output;
            }

            case >= -15 and <= -9:
                return body;

            case >= 25 and <= 31:
                return GzipModule.Member(data, body, level, mtime: 0, system: 3);

            default:
                throw new PyRaise(PyErrors.ValueError("Invalid initialization option"));
        }
    }

    /// <summary>Decompresses one stream in the container <paramref name="wbits"/> names.</summary>
    /// <returns>The output, where the stream (with its trailer) ended, and whether it was complete.</returns>
    internal static (byte[] Output, int End, bool Complete) Decompress(byte[] data, int wbits)
    {
        try
        {
            switch (wbits)
            {
                case 0 or (>= 8 and <= 15):
                    return Zlib(data);
                case >= -15 and <= -8:
                {
                    var raw = Inflater.Inflate(data, 0);
                    return (raw.Output, raw.End, raw.Complete);
                }

                case >= 24 and <= 31:
                    return Gzip(data);
                case >= 40 and <= 47:
                    return data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B ? Gzip(data) : Zlib(data);
                default:
                    throw new PyRaise(PyErrors.ValueError("Invalid initialization option"));
            }
        }
        catch (Inflater.FormatException error)
        {
            throw new PyRaise(new PyException(Error, $"Error -3 while decompressing data: {error.Message}"));
        }
    }

    private static (byte[], int, bool) Zlib(byte[] data)
    {
        if (data.Length < 2)
        {
            return ([], data.Length, false);
        }

        if ((data[0] & 0x0F) != 8 || ((data[0] << 8) | data[1]) % 31 != 0)
        {
            throw new PyRaise(new PyException(Error, "Error -3 while decompressing data: incorrect header check"));
        }

        if ((data[1] & 0x20) != 0)
        {
            throw new PyRaise(new PyException(Error, "Error 2 while decompressing data"));
        }

        var result = Inflater.Inflate(data, 2);

        if (!result.Complete || result.End + 4 > data.Length)
        {
            return (result.Output, data.Length, false);
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(result.End)) != Compression.Adler32(result.Output))
        {
            throw new PyRaise(new PyException(Error, "Error -3 while decompressing data: incorrect data check"));
        }

        return (result.Output, result.End + 4, true);
    }

    private static (byte[], int, bool) Gzip(byte[] data)
    {
        var header = GzipModule.HeaderLength(data, 0);

        if (header < 0)
        {
            if (header == -1)
            {
                return ([], data.Length, false);
            }

            throw new PyRaise(new PyException(Error, "Error -3 while decompressing data: incorrect header check"));
        }

        var result = Inflater.Inflate(data, header);

        if (!result.Complete || result.End + 8 > data.Length)
        {
            return (result.Output, data.Length, false);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(result.End)) != Compression.Crc32(result.Output))
        {
            throw new PyRaise(new PyException(Error, "Error -3 while decompressing data: incorrect data check"));
        }

        return (result.Output, result.End + 8, true);
    }

    /// <summary>A <c>zlib.compressobj</c>: collects input and compresses it whole at <c>flush</c>.</summary>
    private sealed class Compressor(BigInteger level, int wbits) : PyObject
    {
        private readonly MemoryStream _input = new();
        private bool _finished;

        public override string TypeName => "zlib.Compress";

        public override string Repr() => "<zlib.Compress object>";

        public override PyObject? GetAttribute(string name) => name switch
        {
            "compress" => new PyBuiltinFunction(name, arguments =>
            {
                Arity.ExactCount("compress", arguments, 1);

                if (_finished)
                {
                    throw new PyRaise(new PyException(Error, "Error -2 while compressing data: inconsistent stream state"));
                }

                var data = Compression.Data(arguments[0], name);

                if (_input.Length + data.Length > Compression.MaxOutput)
                {
                    throw Compression.TooLarge();
                }

                _input.Write(data);
                return new PyBytes([]);
            }),
            "flush" => new PyBuiltinFunction(name, arguments =>
            {
                var mode = arguments.Length > 0 ? Builtins.BuiltinNamespace.RequireInt(arguments[0], name) : 4;

                if (mode != 4 || _finished)
                {
                    return new PyBytes([]);
                }

                _finished = true;
                return new PyBytes(Compress(_input.ToArray(), level, wbits));
            }),
            _ => null,
        };
    }

    /// <summary>
    /// A <c>zlib.decompressobj</c>: takes the stream in pieces and hands back what each
    /// piece makes decodable.
    /// </summary>
    private sealed class Decompressor(int wbits) : PyObject
    {
        private readonly MemoryStream _input = new();
        private int _returned;
        private bool _eof;
        private byte[] _unused = [];

        public override string TypeName => "zlib.Decompress";

        public override string Repr() => "<zlib.Decompress object>";

        private byte[] Feed(byte[] data)
        {
            if (_eof)
            {
                _unused = [.. _unused, .. data];
                return [];
            }

            _input.Write(data);
            var buffer = _input.ToArray();
            var (output, end, complete) = Decompress(buffer, wbits);
            var fresh = output.AsSpan(_returned).ToArray();
            _returned = output.Length;

            if (complete)
            {
                _eof = true;
                _unused = buffer.AsSpan(end).ToArray();
            }

            return fresh;
        }

        public override PyObject? GetAttribute(string name) => name switch
        {
            "eof" => PyBool.Of(_eof),
            "unused_data" => new PyBytes(_unused),
            "unconsumed_tail" => new PyBytes([]),
            "decompress" => new PyBuiltinFunction(name, arguments =>
            {
                Arity.Between("decompress", arguments, 1, 2);
                return new PyBytes(Feed(Compression.Data(arguments[0], name)));
            }),
            "flush" => new PyBuiltinFunction(name, _ => new PyBytes([])),
            _ => null,
        };
    }
}
