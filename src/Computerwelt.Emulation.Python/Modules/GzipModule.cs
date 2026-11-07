using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>gzip</c> module: <c>compress</c>, <c>decompress</c>, and <c>open</c> over the
/// sandbox's virtual filesystem.
/// </summary>
/// <remarks>
/// A file opened for reading is decompressed whole when opened (under the sandbox's cap); one
/// opened for writing collects what is written and is compressed into the filesystem when it
/// is closed — or when the run ends, if the program never closed it.
/// </remarks>
public static class GzipModule
{
    /// <summary><c>gzip.BadGzipFile</c>, an <c>OSError</c>.</summary>
    public static PyExceptionType BadGzipFile { get; } =
        PyExceptionType.DefineHostException("BadGzipFile", PyExceptionType.OSError);

    /// <summary>Builds the module.</summary>
    /// <param name="fileSystem">Where <c>open</c> reads and writes, or null for none.</param>
    /// <param name="machine">The run, which closes forgotten writers when it ends.</param>
    /// <param name="timeProvider">The clock a written member's timestamp comes from.</param>
    public static PyModuleObject Create(IPyFileSystem? fileSystem, VirtualMachine machine, TimeProvider timeProvider)
    {
        var module = new PyModuleObject("gzip");

        module.Add("BadGzipFile", BadGzipFile);
        module.Add("READ", new PyStr("rb"));
        module.Add("WRITE", new PyStr("wb"));

        module.Add("compress", (arguments, keywords) =>
        {
            var data = Compression.Data(Compression.Argument(arguments, keywords, 0, "data")
                ?? throw new PyRaise(PyErrors.TypeError("compress() missing 1 required positional argument: 'data'")), "compress");
            var level = Compression.Argument(arguments, keywords, 1, "compresslevel") is { } given
                ? Builtins.BuiltinNamespace.RequireInt(given, "compress")
                : 9;
            var mtime = Compression.Argument(arguments, keywords, 99, "mtime") switch
            {
                null or PyNone => timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                var value => (long)Builtins.BuiltinNamespace.RequireInt(value, "compress"),
            };
            return new PyBytes(Member(data, Compression.Deflate(data, Compression.Level(level, ZlibModule.Error)), level, mtime));
        });

        module.Add("decompress", arguments =>
        {
            Arity.ExactCount("decompress", arguments, 1);
            return new PyBytes(Decompress(Compression.Data(arguments[0], "decompress")));
        });

        if (fileSystem is not null)
        {
            PyObject Open(PyObject[] arguments, PyDict? keywords, bool textAllowed)
            {
                var target = Compression.Argument(arguments, keywords, 0, textAllowed ? "filename" : "filename")
                    ?? throw new PyRaise(PyErrors.TypeError("open() missing required argument 'filename'"));
                var mode = Compression.Argument(arguments, keywords, 1, "mode") is PyStr given ? given.Value : "rb";
                var level = Compression.Argument(arguments, keywords, 2, "compresslevel") is { } value and not PyNone
                    ? Builtins.BuiltinNamespace.RequireInt(value, "open")
                    : 9;

                if (!textAllowed && Compression.Argument(arguments, keywords, 3, "fileobj") is { } fileObject and not PyNone)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"GzipFile over a file object ({fileObject.TypeName}) is not supported in this sandbox; pass a filename"));
                }

                if (textAllowed)
                {
                    var encoding = Compression.Argument(arguments, keywords, 3, "encoding");

                    if (encoding is PyStr { Value: var name } && name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() != "utf8")
                    {
                        throw new PyRaise(PyErrors.ValueError($"only the utf-8 encoding is supported, not {PyStr.Quote(name)}"));
                    }
                }

                return OpenFile(fileSystem, machine, timeProvider, Compression.PathText(target), mode, level, textAllowed);
            }

            module.Add("open", (arguments, keywords) => Open(arguments, keywords, textAllowed: true));
            module.Add("GzipFile", (arguments, keywords) => Open(arguments, keywords, textAllowed: false));
        }

        return module;
    }

    private static PyObject OpenFile(
        IPyFileSystem fileSystem,
        VirtualMachine machine,
        TimeProvider timeProvider,
        string path,
        string mode,
        BigInteger level,
        bool textAllowed)
    {
        var text = mode.Contains('t', StringComparison.Ordinal);
        var action = mode.Replace("b", string.Empty, StringComparison.Ordinal).Replace("t", string.Empty, StringComparison.Ordinal);

        if ((text && mode.Contains('b', StringComparison.Ordinal)) || action is not ("r" or "w" or "a" or "x") || (text && !textAllowed))
        {
            throw new PyRaise(PyErrors.ValueError($"Invalid mode: {PyStr.Quote(mode)}"));
        }

        var shownMode = text ? mode : action + "b";

        if (action == "r")
        {
            var content = Decompress(fileSystem.Read(path));
            return new MemoryFile(text ? "_io.TextIOWrapper" : "gzip.GzipFile", path, shownMode, content, binary: !text, onClose: null, machine);
        }

        if (action == "x" && fileSystem.Exists(path))
        {
            throw new PyRaise(new PyException(PyExceptionType.FileExistsError, $"[Errno 17] File exists: '{path}'"));
        }

        _ = Compression.Level(level, ZlibModule.Error);
        var existing = action == "a" && fileSystem.Exists(path) ? fileSystem.Read(path) : [];

        if (action != "a")
        {
            fileSystem.Write(path, []);
        }

        // A virtual path is POSIX; System.IO.Path would read it by the host's rules.
        var name = path[(path.LastIndexOf('/') + 1)..];

        if (name.EndsWith(".gz", StringComparison.Ordinal))
        {
            name = name[..^3];
        }

        return new MemoryFile(text ? "_io.TextIOWrapper" : "gzip.GzipFile", path, shownMode, [], binary: !text, onClose: data =>
        {
            var member = Member(data, Compression.Deflate(data, Compression.Level(level, ZlibModule.Error)), level,
                timeProvider.GetUtcNow().ToUnixTimeSeconds(), name);
            fileSystem.Write(path, [.. existing, .. member]);
        }, machine);
    }

    /// <summary>Wraps a deflate body as one gzip member: the header, the body, CRC and size.</summary>
    internal static byte[] Member(byte[] data, byte[] body, BigInteger level, long mtime, string? fileName = null, byte system = 255)
    {
        var name = fileName is { Length: > 0 } ? Encoding.Latin1.GetBytes(fileName) : null;
        var header = new List<byte>
        {
            0x1F, 0x8B, 8, (byte)(name is null ? 0 : 8),
            (byte)mtime, (byte)(mtime >> 8), (byte)(mtime >> 16), (byte)(mtime >> 24),
            (byte)(level == 9 ? 2 : level == 1 ? 4 : 0),
            system,
        };

        if (name is not null)
        {
            header.AddRange(name);
            header.Add(0);
        }

        var output = new byte[header.Count + body.Length + 8];
        header.CopyTo(output);
        body.CopyTo(output, header.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(output.Length - 8), Compression.Crc32(data));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(output.Length - 4), (uint)data.Length);
        return output;
    }

    /// <summary>
    /// Where a member's deflate data starts, or -1 when the header is cut short and -2 when
    /// the bytes are not a gzip header at all.
    /// </summary>
    internal static int HeaderLength(byte[] data, int offset)
    {
        var remaining = data.Length - offset;

        if (remaining >= 2 && (data[offset] != 0x1F || data[offset + 1] != 0x8B))
        {
            return -2;
        }

        if (remaining < 10)
        {
            return -1;
        }

        if (data[offset + 2] != 8)
        {
            throw new PyRaise(new PyException(BadGzipFile, "Unknown compression method"));
        }

        var flags = data[offset + 3];
        var position = offset + 10;

        if ((flags & 4) != 0)
        {
            if (position + 2 > data.Length)
            {
                return -1;
            }

            position += 2 + (data[position] | (data[position + 1] << 8));
        }

        foreach (var flag in new[] { 8, 16 })
        {
            if ((flags & flag) != 0)
            {
                while (position < data.Length && data[position] != 0)
                {
                    position++;
                }

                position++;
            }
        }

        if ((flags & 2) != 0)
        {
            position += 2;
        }

        return position > data.Length ? -1 : position;
    }

    /// <summary>Decompresses every member, as <c>gzip.decompress</c> does.</summary>
    internal static byte[] Decompress(byte[] data)
    {
        using var output = new MemoryStream();
        var offset = 0;

        while (offset < data.Length)
        {
            var start = HeaderLength(data, offset);

            if (start == -2)
            {
                // Padding of zeros after the last member is allowed; anything else is not.
                if (offset > 0 && data.AsSpan(offset).IndexOfAnyExcept((byte)0) < 0)
                {
                    break;
                }

                throw new PyRaise(new PyException(BadGzipFile,
                    $"Not a gzipped file ({new PyBytes(data.AsSpan(offset, Math.Min(2, data.Length - offset)).ToArray()).Repr()})"));
            }

            if (start == -1)
            {
                throw Truncated();
            }

            Inflater.Result member;

            try
            {
                member = Inflater.Inflate(data, start, Compression.MaxOutput - (int)output.Length);
            }
            catch (Inflater.FormatException error)
            {
                throw new PyRaise(new PyException(ZlibModule.Error, $"Error -3 while decompressing data: {error.Message}"));
            }

            if (!member.Complete || member.End + 8 > data.Length)
            {
                throw Truncated();
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(member.End)) != Compression.Crc32(member.Output))
            {
                throw new PyRaise(new PyException(BadGzipFile, "CRC check failed"));
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(member.End + 4)) != (uint)member.Output.Length)
            {
                throw new PyRaise(new PyException(BadGzipFile, "Incorrect length of data produced"));
            }

            output.Write(member.Output);
            offset = member.End + 8;
        }

        return output.ToArray();
    }

    private static PyRaise Truncated() => new(new PyException(
        PyExceptionType.EOFError, "Compressed file ended before the end-of-stream marker was reached"));
}
