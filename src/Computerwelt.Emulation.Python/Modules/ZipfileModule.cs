using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>zipfile</c> module: reading and writing ZIP archives in the sandbox's virtual
/// filesystem.
/// </summary>
/// <remarks>
/// <para>
/// An archive is read from the filesystem when it is opened and written back when it is
/// closed (or when the run ends). Members are inflated only when read, each through the
/// sandbox's cap, and checked against their CRC-32 — so a zip bomb raises
/// <c>MemoryError</c> instead of exhausting the host, and a corrupt member raises
/// <c>BadZipFile</c> instead of reading as truncated data.
/// </para>
/// <para>
/// <c>extract</c> and <c>extractall</c> sanitise member names as CPython does — a leading
/// <c>/</c>, <c>.</c> and <c>..</c> components are dropped — so an archive cannot write
/// outside the directory it is extracted into. Only <c>ZIP_STORED</c> and
/// <c>ZIP_DEFLATED</c> are supported, and there is no encryption.
/// </para>
/// </remarks>
public static class ZipfileModule
{
    /// <summary><c>zipfile.BadZipFile</c>.</summary>
    public static PyExceptionType BadZipFile { get; } = PyExceptionType.DefineHostException("BadZipFile");

    /// <summary><c>zipfile.LargeZipFile</c>.</summary>
    public static PyExceptionType LargeZipFile { get; } = PyExceptionType.DefineHostException("LargeZipFile");

    /// <summary>The most members one archive may hold.</summary>
    internal const int MaxEntries = 100_000;

    /// <summary>The most bytes one <c>extractall</c> may write: 256 MiB.</summary>
    internal const long MaxExtracted = 1L << 28;

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(IPyFileSystem? fileSystem, VirtualMachine machine, TimeProvider timeProvider)
    {
        var module = new PyModuleObject("zipfile");

        module.Add("BadZipFile", BadZipFile);
        module.Add("BadZipfile", BadZipFile);
        module.Add("error", BadZipFile);
        module.Add("LargeZipFile", LargeZipFile);
        module.Add("ZIP_STORED", PyInt.From(0));
        module.Add("ZIP_DEFLATED", PyInt.From(8));
        module.Add("ZIP_BZIP2", PyInt.From(12));
        module.Add("ZIP_LZMA", PyInt.From(14));

        module.Add("ZipInfo", new PyType("ZipInfo", static value => value is PyZipInfo, (arguments, keywords) =>
        {
            var name = Compression.Argument(arguments, keywords, 0, "filename") is PyStr text ? text.Value : "NoName";
            var dateTime = Compression.Argument(arguments, keywords, 1, "date_time") is { } given
                ? [.. VirtualMachine.RequireIterable(given).Select(static v => (int)Builtins.BuiltinNamespace.RequireInt(v, "ZipInfo"))]
                : new[] { 1980, 1, 1, 0, 0, 0 };

            if (dateTime.Length < 6 || dateTime[0] < 1980)
            {
                throw new PyRaise(PyErrors.ValueError("ZIP does not support timestamps before 1980"));
            }

            return new PyZipInfo(name, dateTime[..6]);
        }));

        if (fileSystem is not null)
        {
            module.Add("ZipFile", new PyType("ZipFile", static value => value is PyZipFile, (arguments, keywords) =>
            {
                var target = Compression.Argument(arguments, keywords, 0, "file")
                    ?? throw new PyRaise(PyErrors.TypeError("ZipFile.__init__() missing 1 required positional argument: 'file'"));
                var mode = Compression.Argument(arguments, keywords, 1, "mode") is PyStr given ? given.Value : "r";
                var compression = Compression.Argument(arguments, keywords, 2, "compression") is { } method
                    ? (int)Builtins.BuiltinNamespace.RequireInt(method, "ZipFile")
                    : 0;
                var level = Compression.Argument(arguments, keywords, 4, "compresslevel") is { } value and not PyNone
                    ? (int?)(int)Builtins.BuiltinNamespace.RequireInt(value, "ZipFile")
                    : null;

                return new PyZipFile(fileSystem, machine, timeProvider, Compression.PathText(target), mode, compression, level);
            }));

            module.Add("is_zipfile", arguments =>
            {
                Arity.ExactCount("is_zipfile", arguments, 1);

                try
                {
                    var path = Compression.PathText(arguments[0]);

                    if (!fileSystem.IsFile(path))
                    {
                        return PyBool.False;
                    }

                    return PyBool.Of(CentralDirectory.Find(fileSystem.Read(path)) is not null);
                }
                catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.OSError))
                {
                    return PyBool.False;
                }
            });
        }

        return module;
    }

    /// <summary>Checks a compression method, refusing the ones this port does not write.</summary>
    internal static void RequireSupported(int method)
    {
        // BZIP2 (12) and LZMA (14) are real methods with no decoder here; anything else is
        // not a method at all, and CPython words both the same way.
        if (method is not (0 or 8))
        {
            throw new PyRaise(new PyException(PyExceptionType.NotImplementedError, "That compression method is not supported"));
        }
    }

    /// <summary>The central directory's facts .NET does not expose: each member's method.</summary>
    internal static class CentralDirectory
    {
        /// <summary>The compression method of each member, in order, or null when this is not a ZIP.</summary>
        public static List<(string Name, int Method)>? Find(byte[] data)
        {
            var end = -1;

            for (var i = data.Length - 22; i >= Math.Max(0, data.Length - 22 - 65535); i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == 0x06054B50)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                return null;
            }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(end + 10));
            var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end + 16));
            var entries = new List<(string, int)>();

            // A ZIP64 directory is beyond what this reads for methods; .NET reads its members.
            if (offset == uint.MaxValue || count == ushort.MaxValue)
            {
                return entries;
            }

            for (var i = 0; i < count; i++)
            {
                if (offset + 46 > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offset)) != 0x02014B50)
                {
                    return entries.Count == 0 && count > 0 ? null : entries;
                }

                var at = (int)offset;
                int method = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10));
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 28));
                int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 30));
                int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 32));
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 8));
                var nameBytes = data.AsSpan(at + 46, Math.Min(nameLength, data.Length - at - 46));
                var name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);
                entries.Add((name, method));
                offset += 46 + nameLength + extraLength + commentLength;
            }

            return entries;
        }
    }
}

/// <summary>A <c>zipfile.ZipInfo</c>: one member's name, timestamp, sizes and method.</summary>
internal sealed class PyZipInfo(string filename, int[] dateTime) : PyObject
{
    public string Filename { get; set; } = filename;

    public int[] DateTime { get; set; } = dateTime;

    public int CompressType { get; set; }

    public long FileSize { get; set; }

    public long CompressSize { get; set; }

    public uint Crc { get; set; }

    public bool IsDirectory => Filename.EndsWith('/');

    public override string TypeName => "ZipInfo";

    public override string Repr()
    {
        var builder = new StringBuilder($"<ZipInfo filename={PyStr.Quote(Filename)}");

        if (CompressType != 0)
        {
            builder.Append(CompressType == 8 ? " compress_type=deflate" : $" compress_type={CompressType}");
        }

        builder.Append(IsDirectory ? " filemode='drwxr-xr-x'" : " filemode='-rw-r--r--'");
        builder.Append($" file_size={FileSize}");

        if (CompressSize != FileSize)
        {
            builder.Append($" compress_size={CompressSize}");
        }

        return builder.Append('>').ToString();
    }

    public override PyObject? GetAttribute(string name) => name switch
    {
        "filename" or "orig_filename" => new PyStr(Filename),
        "date_time" => new PyTuple([.. DateTime.Select(static v => (PyObject)PyInt.From(v))]),
        "compress_type" => PyInt.From(CompressType),
        "file_size" => PyInt.From(FileSize),
        "compress_size" => PyInt.From(CompressSize),
        "CRC" => PyInt.From(Crc),
        "comment" or "extra" => new PyBytes([]),
        "create_system" => PyInt.From(3),
        "external_attr" => PyInt.From(IsDirectory ? (0x41EDL << 16) | 0x10 : 0x81A4L << 16),
        "is_dir" => new PyBuiltinFunction("is_dir", _ => PyBool.Of(IsDirectory)),
        _ => null,
    };

    public override bool SetAttribute(string name, PyObject value)
    {
        switch (name)
        {
            case "compress_type":
                CompressType = (int)Builtins.BuiltinNamespace.RequireInt(value, name);
                return true;
            case "date_time":
                DateTime = [.. VirtualMachine.RequireIterable(value).Select(static v => (int)Builtins.BuiltinNamespace.RequireInt(v, "date_time"))];
                return true;
            case "filename":
                Filename = value.Display();
                return true;
            default:
                return false;
        }
    }
}

/// <summary>A <c>zipfile.ZipFile</c>.</summary>
internal sealed class PyZipFile : PyObject
{
    private readonly IPyFileSystem _fileSystem;
    private readonly VirtualMachine _machine;
    private readonly TimeProvider _timeProvider;
    private readonly int _compression;
    private readonly int? _level;
    private readonly List<Member> _members = [];
    private bool _closed;
    private bool _modified;

    /// <summary>One member: its info and either its bytes or where to inflate them from.</summary>
    private sealed class Member(PyZipInfo info)
    {
        public PyZipInfo Info { get; } = info;

        public byte[]? Data { get; set; }

        public ZipArchiveEntry? Source { get; set; }
    }

    public PyZipFile(IPyFileSystem fileSystem, VirtualMachine machine, TimeProvider timeProvider, string path, string mode, int compression, int? level)
    {
        _fileSystem = fileSystem;
        _machine = machine;
        _timeProvider = timeProvider;
        Path = path;
        Mode = mode;
        _compression = compression;
        _level = level;

        ZipfileModule.RequireSupported(compression);

        switch (mode)
        {
            case "r":
                Load(fileSystem.Read(path));
                break;
            case "w":
                fileSystem.Write(path, []);
                _modified = true;
                break;
            case "x":
                if (fileSystem.Exists(path))
                {
                    throw new PyRaise(new PyException(PyExceptionType.FileExistsError, $"[Errno 17] File exists: '{path}'"));
                }

                fileSystem.Write(path, []);
                _modified = true;
                break;
            case "a":
                if (fileSystem.Exists(path) && fileSystem.Read(path) is { Length: > 0 } existing)
                {
                    Load(existing);
                }

                _modified = true;
                break;
            default:
                throw new PyRaise(PyErrors.ValueError("ZipFile requires mode 'r', 'w', 'x', or 'a'"));
        }

        if (mode != "r")
        {
            machine.WhenRunCompleted(Close);
        }
    }

    public string Path { get; }

    public string Mode { get; }

    public override string TypeName => "ZipFile";

    public override string Repr() => _closed
        ? "<zipfile.ZipFile [closed]>"
        : $"<zipfile.ZipFile filename={PyStr.Quote(Path)} mode={PyStr.Quote(Mode)}>";

    private void Load(byte[] data)
    {
        var methods = ZipfileModule.CentralDirectory.Find(data)
            ?? throw new PyRaise(new PyException(ZipfileModule.BadZipFile, "File is not a zip file"));
        ZipArchive archive;

        try
        {
            archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new PyRaise(new PyException(ZipfileModule.BadZipFile, "File is not a zip file"));
        }

        if (archive.Entries.Count > ZipfileModule.MaxEntries)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError,
                $"the archive has more than {ZipfileModule.MaxEntries} members"));
        }

        for (var i = 0; i < archive.Entries.Count; i++)
        {
            var entry = archive.Entries[i];
            var stamp = entry.LastWriteTime.DateTime;
            var info = new PyZipInfo(entry.FullName, [stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, stamp.Second])
            {
                FileSize = entry.Length,
                CompressSize = entry.CompressedLength,
                Crc = entry.Crc32,
                CompressType = i < methods.Count ? methods[i].Method : (entry.CompressedLength == entry.Length ? 0 : 8),
            };
            _members.Add(new Member(info) { Source = entry });
        }
    }

    private void RequireOpen()
    {
        if (_closed)
        {
            throw new PyRaise(PyErrors.ValueError("Attempt to use ZIP archive that was already closed"));
        }
    }

    private void RequireWritable(string function)
    {
        RequireOpen();

        if (Mode == "r")
        {
            throw new PyRaise(PyErrors.ValueError($"{function}() requires mode 'w', 'x', or 'a'"));
        }
    }

    private Member Find(PyObject name)
    {
        var text = name is PyZipInfo info ? info.Filename : name.Display();

        for (var i = _members.Count - 1; i >= 0; i--)
        {
            if (_members[i].Info.Filename == text)
            {
                return _members[i];
            }
        }

        throw new PyRaise(PyErrors.KeyError(new PyStr($"There is no item named {PyStr.Quote(text)} in the archive")));
    }

    /// <summary>A member's bytes, inflated under the cap and checked against its CRC.</summary>
    private byte[] Read(Member member)
    {
        if (member.Data is { } data)
        {
            return data;
        }

        var entry = member.Source!;

        if (member.Info.CompressType is not (0 or 8))
        {
            throw new PyRaise(new PyException(PyExceptionType.NotImplementedError, "That compression method is not supported"));
        }

        byte[] content;

        try
        {
            using var stream = entry.Open();
            content = Compression.Inflate(stream);
        }
        catch (InvalidDataException)
        {
            throw new PyRaise(new PyException(ZipfileModule.BadZipFile, $"Bad CRC-32 for file {PyStr.Quote(member.Info.Filename)}"));
        }

        if (content.Length != member.Info.FileSize || Compression.Crc32(content) != member.Info.Crc)
        {
            throw new PyRaise(new PyException(ZipfileModule.BadZipFile, $"Bad CRC-32 for file {PyStr.Quote(member.Info.Filename)}"));
        }

        return content;
    }

    private int[] Now()
    {
        var now = _timeProvider.GetUtcNow();
        return [now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second];
    }

    private void Add(PyZipInfo info, byte[] data, int? compressType)
    {
        RequireWritable("write");

        if (_members.Count >= ZipfileModule.MaxEntries)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError,
                $"the archive has more than {ZipfileModule.MaxEntries} members"));
        }

        if (compressType is { } method)
        {
            ZipfileModule.RequireSupported(method);
            info.CompressType = method;
        }

        info.FileSize = data.Length;
        info.Crc = Compression.Crc32(data);
        info.CompressSize = info.CompressType == 0 ? data.Length : Compression.Deflate(data, LevelFor(_level)).Length;
        _members.Add(new Member(info) { Data = data });
    }

    private static CompressionLevel LevelFor(int? level) => level switch
    {
        null => CompressionLevel.Optimal,
        0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    private static int? CompressArgument(PyObject[] arguments, PyDict? keywords, int position) =>
        Compression.Argument(arguments, keywords, position, "compress_type") is { } value and not PyNone
            ? (int)Builtins.BuiltinNamespace.RequireInt(value, "compress_type")
            : null;

    /// <summary>Writes the archive back to the filesystem.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        if (!_modified)
        {
            return;
        }

        using var output = new MemoryStream();

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var member in _members)
            {
                var info = member.Info;
                var level = info.CompressType == 0 ? CompressionLevel.NoCompression : LevelFor(_level);
                var entry = archive.CreateEntry(info.Filename, level);
                var stamp = info.DateTime;
                entry.LastWriteTime = new DateTimeOffset(
                    Math.Max(1980, stamp[0]), Math.Clamp(stamp[1], 1, 12), Math.Clamp(stamp[2], 1, 28 + 3),
                    Math.Clamp(stamp[3], 0, 23), Math.Clamp(stamp[4], 0, 59), Math.Clamp(stamp[5], 0, 59), TimeSpan.Zero);

                if (!info.IsDirectory)
                {
                    using var stream = entry.Open();
                    stream.Write(Read(member));
                }
            }
        }

        _fileSystem.Write(Path, output.ToArray());
    }

    /// <summary>Where a member lands under <paramref name="root"/>, with the names CPython drops dropped.</summary>
    private string Target(string root, string memberName)
    {
        var parts = memberName.Replace('\\', '/').Split('/')
            .Where(static part => part is not ("" or "." or ".."));
        var relative = string.Join('/', parts);
        var basis = root.Length == 0 ? _fileSystem.WorkingDirectory : root;
        return PyPath.Normalize(relative.Length == 0 ? basis : PyPath.Join(basis, relative));
    }

    private string Extract(Member member, string root, ref long budget)
    {
        var target = Target(root, member.Info.Filename);
        var slash = target.LastIndexOf('/');
        var parent = slash > 0 ? target[..slash] : "/";

        if (!_fileSystem.IsDirectory(parent))
        {
            _fileSystem.CreateDirectory(parent, parents: true, existsOk: true);
        }

        if (member.Info.IsDirectory)
        {
            if (!_fileSystem.IsDirectory(target))
            {
                _fileSystem.CreateDirectory(target, parents: true, existsOk: true);
            }

            return target;
        }

        var data = Read(member);
        budget -= data.Length;

        if (budget < 0)
        {
            throw new PyRaise(new PyException(PyExceptionType.MemoryError,
                $"extracting would write more than the sandbox limit of {ZipfileModule.MaxExtracted} bytes"));
        }

        _fileSystem.Write(target, data);
        return target;
    }

    private static string RootArgument(PyObject[] arguments, PyDict? keywords, int position) =>
        Compression.Argument(arguments, keywords, position, "path") is { } value and not PyNone
            ? Compression.PathText(value)
            : string.Empty;

    public override PyObject? GetAttribute(string name)
    {
        switch (name)
        {
            case "filename":
                return new PyStr(Path);
            case "mode":
                return new PyStr(Mode);
            case "comment":
                return new PyBytes([]);
            case "filelist":
                return new PyList([.. _members.Select(static m => (PyObject)m.Info)]);
        }

        return name switch
        {
            "__enter__" => new PyBuiltinFunction(name, _ => this),
            "__exit__" => new PyBuiltinFunction(name, _ =>
            {
                Close();
                return PyBool.False;
            }),
            "close" => new PyBuiltinFunction(name, _ =>
            {
                Close();
                return PyNone.Instance;
            }),
            "namelist" => new PyBuiltinFunction(name, _ =>
            {
                return new PyList([.. _members.Select(static m => (PyObject)new PyStr(m.Info.Filename))]);
            }),
            "infolist" => new PyBuiltinFunction(name, _ =>
            {
                return new PyList([.. _members.Select(static m => (PyObject)m.Info)]);
            }),
            "getinfo" => new PyBuiltinFunction(name, arguments =>
            {
                Arity.ExactCount("getinfo", arguments, 1);
                return Find(arguments[0]).Info;
            }),
            "read" => new PyBuiltinFunction(name, arguments =>
            {
                Arity.Between("read", arguments, 1, 2);
                RequireOpen();
                return new PyBytes(Read(Find(arguments[0])));
            }),
            "testzip" => new PyBuiltinFunction(name, _ =>
            {
                RequireOpen();

                foreach (var member in _members)
                {
                    try
                    {
                        Read(member);
                    }
                    catch (PyRaise raise) when (raise.Exception.IsInstanceOf(ZipfileModule.BadZipFile))
                    {
                        return new PyStr(member.Info.Filename);
                    }
                }

                return PyNone.Instance;
            }),
            "open" => new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                RequireOpen();
                var target = Compression.Argument(arguments, keywords, 0, "name")
                    ?? throw new PyRaise(PyErrors.TypeError("open() missing required argument 'name'"));
                var mode = Compression.Argument(arguments, keywords, 1, "mode") is PyStr given ? given.Value : "r";

                if (mode == "r")
                {
                    var member = Find(target);
                    return new MemoryFile("zipfile.ZipExtFile", member.Info.Filename, "r", Read(member), binary: true, onClose: null, _machine);
                }

                if (mode != "w")
                {
                    throw new PyRaise(PyErrors.ValueError("open() requires mode \"r\" or \"w\""));
                }

                RequireWritable("open");
                var info = target as PyZipInfo ?? new PyZipInfo(target.Display(), Now()) { CompressType = _compression };
                return new MemoryFile("zipfile._ZipWriteFile", info.Filename, "w", [], binary: true,
                    onClose: data => Add(info, data, null), _machine);
            }),
            "writestr" => new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                var target = Compression.Argument(arguments, keywords, 0, "zinfo_or_arcname")
                    ?? throw new PyRaise(PyErrors.TypeError("writestr() missing required argument 'zinfo_or_arcname'"));
                var content = Compression.Argument(arguments, keywords, 1, "data")
                    ?? throw new PyRaise(PyErrors.TypeError("writestr() missing required argument 'data'"));
                var data = content switch
                {
                    PyStr text => Encoding.UTF8.GetBytes(text.Value),
                    PyBytes bytes => bytes.Value,
                    _ => throw new PyRaise(PyErrors.TypeError($"a bytes-like object is required, not '{content.TypeName}'")),
                };

                var info = target as PyZipInfo ?? new PyZipInfo(target.Display(), Now()) { CompressType = _compression };
                Add(info, data, CompressArgument(arguments, keywords, 2));
                return PyNone.Instance;
            }),
            "write" => new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                var source = Compression.PathText(Compression.Argument(arguments, keywords, 0, "filename")
                    ?? throw new PyRaise(PyErrors.TypeError("write() missing required argument 'filename'")));
                var archiveName = Compression.Argument(arguments, keywords, 1, "arcname") is { } given and not PyNone
                    ? Compression.PathText(given)
                    : source;

                // CPython stores the name without a leading slash or a `./`.
                archiveName = string.Join('/', archiveName.Split('/').Where(static p => p is not ("" or ".")));
                var stamp = DateTimeOffset.FromUnixTimeSeconds((long)_fileSystem.ModifiedAt(source)).UtcDateTime;
                int[] dateTime = [Math.Max(1980, stamp.Year), stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, stamp.Second];

                if (_fileSystem.IsDirectory(source))
                {
                    var directory = new PyZipInfo(archiveName + "/", dateTime);
                    Add(directory, [], 0);
                    return PyNone.Instance;
                }

                var info = new PyZipInfo(archiveName, dateTime) { CompressType = _compression };
                Add(info, _fileSystem.Read(source), CompressArgument(arguments, keywords, 2));
                return PyNone.Instance;
            }),
            "mkdir" => new PyBuiltinFunction(name, arguments =>
            {
                Arity.Between("mkdir", arguments, 1, 2);
                var info = arguments[0] as PyZipInfo ?? new PyZipInfo(arguments[0].Display().TrimEnd('/') + "/", Now());
                Add(info, [], 0);
                return PyNone.Instance;
            }),
            "extract" => new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                RequireOpen();
                var member = Find(Compression.Argument(arguments, keywords, 0, "member")
                    ?? throw new PyRaise(PyErrors.TypeError("extract() missing required argument 'member'")));
                var budget = ZipfileModule.MaxExtracted;
                return new PyStr(Extract(member, RootArgument(arguments, keywords, 1), ref budget));
            }),
            "extractall" => new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                RequireOpen();
                var root = RootArgument(arguments, keywords, 0);
                var selected = Compression.Argument(arguments, keywords, 1, "members") is { } names and not PyNone
                    ? [.. VirtualMachine.RequireIterable(names).Select(Find)]
                    : _members;
                var budget = ZipfileModule.MaxExtracted;

                foreach (var member in selected)
                {
                    Extract(member, root, ref budget);
                }

                return PyNone.Instance;
            }),
            "printdir" => new PyBuiltinFunction(name, _ =>
            {
                var builder = new StringBuilder();
                builder.Append($"{"File Name",-46} {"Modified    ",19} {"Size",12}\n");

                foreach (var member in _members)
                {
                    var d = member.Info.DateTime;
                    builder.Append($"{member.Info.Filename,-46} {d[0]}-{d[1]:00}-{d[2]:00} {d[3]:00}:{d[4]:00}:{d[5]:00} {member.Info.FileSize,12}\n");
                }

                _machine.Write(builder.ToString());
                return PyNone.Instance;
            }),
            _ => null,
        };
    }
}
