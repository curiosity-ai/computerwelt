using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The importable module set.
/// </summary>
/// <remarks>
/// This is the complete list. A module absent here cannot be imported at all — there is no
/// search path and no fallback to the host's Python, which is what makes the sandbox's
/// reach a closed, reviewable set.
/// </remarks>
public static class StandardLibrary
{
    /// <summary>
    /// Readies the standard library for a machine. Nothing is built here: every standard
    /// module is built on first import (<see cref="CreateOnDemand"/>), so a run pays for
    /// the modules it uses and not for the forty it does not.
    /// </summary>
    /// <param name="machine">The machine the modules call back into.</param>
    /// <param name="timeProvider">The clock, or null for the system one.</param>
    /// <param name="fileSystem">
    /// The filesystem <c>os</c> and <c>pathlib</c> are built over. With none, the
    /// filesystem modules are absent rather than present and always failing.
    /// </param>
    /// <param name="arguments">What <c>sys.argv</c> reports, program name first.</param>
    /// <param name="standardInput">
    /// The stream <c>sys.stdin</c> reads, or null when the program has no standard input —
    /// in which case the attribute is absent rather than an empty stream.
    /// </param>
    /// <returns>The modules that must exist before the program runs: none.</returns>
    public static Dictionary<string, PyObject> Create(
        VirtualMachine machine,
        TimeProvider? timeProvider = null,
        IPyFileSystem? fileSystem = null,
        IReadOnlyList<string>? arguments = null,
        PyMemoryStream? standardInput = null)
    {
        ArgumentNullException.ThrowIfNull(machine);

        machine.StandardLibrary = new StandardLibraryContext(
            timeProvider ?? TimeProvider.System, fileSystem, arguments, standardInput);

        return new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds the standard module <paramref name="name"/> for <paramref name="machine"/>, or
    /// returns null when there is no such module (or, for the filesystem modules, nothing
    /// to build them over).
    /// </summary>
    /// <remarks>
    /// The result is cached by the machine (<see cref="VirtualMachine.Modules"/>), so a
    /// module is built once per run and every importer sees the same object, as in CPython.
    /// </remarks>
    public static PyObject? CreateOnDemand(VirtualMachine machine, string name, IPyFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(machine);

        var context = machine.StandardLibrary ?? new StandardLibraryContext(TimeProvider.System, fileSystem, null, null);
        var storage = context.FileSystem ?? fileSystem;
        var clock = context.Clock;

        // One cap, read from the run's limits, shared by everything that walks a tree —
        // so `glob`, `os.walk` and `Path.walk` cannot disagree about how deep is too deep.
        var maxDepth = machine.Limits.MaxDirectoryDepth;

        switch (name)
        {
            case "re": return ReModule.Create();
            case "dataclasses": return DataclassesModule.Create(machine);
            case "datetime": return DatetimeModule.Create(clock);
            case "math": return MathModule.Create();
            case "cmath": return CmathModule.Create();
            case "sys": return SysModule.Create(machine, context.Arguments, context.StandardInput);
            case "json": return JsonModule.Create(machine);
            case "random": return RandomModule.Create();
            case "csv": return CsvModule.Create(machine);
            case "heapq": return HeapqModule.CreateHeapq(machine);
            case "bisect": return HeapqModule.CreateBisect(machine);
            case "copy": return CopyModule.Create(machine);
            case "hashlib": return HashingModules.CreateHashlib();
            case "hmac": return HashingModules.CreateHmac();
            case "binascii": return EncodingModules.CreateBinascii();
            case "base64": return EncodingModules.CreateBase64();
            case "uuid": return UuidModule.Create();
            case "decimal": return DecimalModule.Create();
            case "fractions": return FractionsModule.Create();
            case "struct": return StructModule.Create();
            case "types": return TypesModule.Create(machine);
            case "zoneinfo": return ZoneinfoModule.Create();
            case "enum": return EnumModule.Create(machine);
            case "abc": return AbcModule.Create(machine);
            case "collections.abc": return AbcModule.CreateCollectionsAbc();
            case "traceback": return TracebackModule.Create(machine);
            case "asyncio": return AsyncioModule.Create(machine);
            case "collections": return SupportModules.CreateCollections(machine);
            case "itertools": return SupportModules.CreateItertools(machine);
            case "gc": return SupportModules.CreateGc();
            case "unicodedata": return UnicodedataModule.Create();
            case "typing": return SupportModules.CreateTyping();
            case "__future__": return new PyModuleObject("__future__").Add("annotations", PyNone.Instance);

            // Exception classes the modules written in Python raise, from when Python
            // source here could not define an exception class of its own.
            case "_hostexc":
                return new PyModuleObject("_hostexc")
                    .Add("StatisticsError", HostExceptions.StatisticsError)
                    .Add("IllegalMonthError", HostExceptions.IllegalMonthError)
                    .Add("IllegalWeekdayError", HostExceptions.IllegalWeekdayError);

            case "_warnings": return WarningsSupport.Create(machine);

            // `urllib.parse` is written in Python and built on first import; the package only
            // has to answer for it once it exists.
            case "urllib": return new PyPackageObject("urllib", machine);

            // `secrets.SystemRandom` is `random.SystemRandom`, the same class object.
            case "secrets": return SecretsModule.Create(machine.Import("random").GetAttribute("SystemRandom"));

            // The compression modules work on bytes without a filesystem; `gzip.open` and
            // `ZipFile` are present only when there is one to open files in.
            case "zlib": return ZlibModule.Create();
            case "gzip": return GzipModule.Create(storage, machine, clock);
            case "zipfile": return ZipfileModule.Create(storage, machine, clock);

            // Time comes from the run's clock, so the module is built over it.
            case "time": return TimeModule.Create(clock);

            case "pathlib": return PathlibModule.Create(storage, maxDepth);
            case "io": return IoModule.Create(storage);

            // Pattern matching over plain strings needs nothing from the host, so it is here
            // even for a sandbox with no storage.
            case "fnmatch": return FnmatchModule.Create();

            case "os": return storage is null ? null : OsModule.Create(storage, machine);
            case "glob": return storage is null ? null : GlobModule.Create(storage, maxDepth);

            // `import os.path` and `import posixpath` are two more names for the object
            // `os.path` already is — one module, three ways to reach it, as CPython has it.
            case "os.path" or "posixpath": return storage is null ? null : machine.Import("os").GetAttribute("path");

            // `from json.decoder import JSONDecodeError` is the commonest spelling there is,
            // so the submodules are importable by name as well as reachable as attributes.
            case "json.decoder": return machine.Import("json").GetAttribute("decoder");
            case "json.encoder": return machine.Import("json").GetAttribute("encoder");

            // pandas reads and writes through the same filesystem as `open`, and without one
            // still works in memory.
            case "pandas": return Pandas.PdModule.Create(machine, storage);

            case "numpy" or "numpy.linalg" or "numpy.random":
            {
                if (!machine.Modules.TryGetValue("numpy", out var package))
                {
                    package = Numpy.NumpyModule.Create(machine);
                    machine.Modules["numpy"] = package;
                }

                return name == "numpy" ? package : package.GetAttribute(name["numpy.".Length..]);
            }

            default:
                return null;
        }
    }


}

/// <summary>The exception classes that modules written in Python raise.</summary>
internal static class HostExceptions
{
    /// <summary><c>statistics.StatisticsError</c>, a <c>ValueError</c>.</summary>
    public static PyExceptionType StatisticsError { get; } =
        PyExceptionType.DefineHostException("StatisticsError", PyExceptionType.ValueError);

    /// <summary><c>calendar.IllegalMonthError</c>, a <c>ValueError</c> and an <c>IndexError</c>.</summary>
    public static PyExceptionType IllegalMonthError { get; } =
        PyExceptionType.DefineHostException("IllegalMonthError", PyExceptionType.ValueError, PyExceptionType.IndexError);

    /// <summary><c>calendar.IllegalWeekdayError</c>, a <c>ValueError</c>.</summary>
    public static PyExceptionType IllegalWeekdayError { get; } =
        PyExceptionType.DefineHostException("IllegalWeekdayError", PyExceptionType.ValueError);
}

/// <summary>What a run's standard modules are built over, kept until each is first imported.</summary>
/// <param name="Clock">The run's clock.</param>
/// <param name="FileSystem">The run's virtual filesystem, or null for none.</param>
/// <param name="Arguments">What <c>sys.argv</c> reports.</param>
/// <param name="StandardInput">What <c>sys.stdin</c> reads, or null for none.</param>
public sealed record StandardLibraryContext(
    TimeProvider Clock,
    IPyFileSystem? FileSystem,
    IReadOnlyList<string>? Arguments,
    PyMemoryStream? StandardInput);
