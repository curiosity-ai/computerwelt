using Computerwelt.Emulation.Python.Compilation;
using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Parsing;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python;

/// <summary>
/// A Python session that runs a sequence of cells against one namespace — the shape of a
/// notebook.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="PythonRunner.Run"/> builds a machine, runs one program and throws it away.
/// A session keeps the machine: a function defined in one cell is called in the next, a
/// module imported once stays imported, and a DataFrame built early is still there to
/// plot later. What does <i>not</i> carry over is the budget — each cell starts with the
/// full instruction allowance, call depth and output size, so a long notebook is not
/// starved by its own history, and one runaway cell is stopped by the same limits a script
/// would be.
/// </para>
/// <para>
/// Output is a list in the order it happened: text the cell printed, every
/// <c>display(...)</c>, and the value of a trailing expression, each a MIME bundle with at
/// least <c>text/plain</c> (see <see cref="DisplayModule.Format"/>).
/// </para>
/// <para>
/// One session is one tenant's state, used by one caller at a time. Two sessions share
/// nothing.
/// </para>
/// </remarks>
public sealed class PythonSession : IDisposable
{
    private readonly VirtualMachine _machine;
    private readonly List<CellOutput> _outputs = [];
    private readonly PyDict _history = new();
    private int _stdoutMark;
    private int _stderrMark;
    private bool _disposed;

    internal PythonSession(VirtualMachine machine)
    {
        _machine = machine;
        _machine.DisplayHook = OnDisplay;

        var display = DisplayModule.Create(machine);
        machine.Modules["IPython.display"] = display;
        machine.Modules["IPython"] = new PyModuleObject("IPython")
            .Add("display", display)
            .Add("get_ipython", static _ => PyNone.Instance);
        machine.Builtins.Set(new PyStr("display"), display.GetAttribute("display")!);

        machine.Globals.Set(new PyStr("Out"), _history);
    }

    /// <summary>How many cells have run, which also numbers the next one.</summary>
    public int ExecutionCount { get; private set; }

    /// <summary>The namespace the cells share.</summary>
    public PyDict Globals => _machine.Globals;

    /// <summary>The machine the cells run on, for host code that needs to call into it.</summary>
    public VirtualMachine Machine => _machine;

    /// <summary>Runs one cell.</summary>
    /// <param name="source">The cell's source.</param>
    /// <param name="cancellationToken">
    /// Interrupts the cell: it stops at the next instruction boundary and reports
    /// <c>KeyboardInterrupt</c>, as interrupting a notebook kernel does. The session stays
    /// usable.
    /// </param>
    public CellResult Run(string source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        var number = ++ExecutionCount;
        var fileName = $"<cell {number}>";

        _machine.ResetForNextRun();
        _machine.Cancellation = cancellationToken;
        _outputs.Clear();
        _stdoutMark = 0;
        _stderrMark = 0;

        PyObject value = PyNone.Instance;
        string? error = null;
        string? errorName = null;
        PyException? exception = null;

        try
        {
            var module = Parser.Parse(source);
            var code = Compiler.CompileModule(module, fileName);
            value = _machine.RunModule(code, _machine.Globals);
        }
        catch (PythonSyntaxError syntax)
        {
            errorName = "SyntaxError";
            error = $"  File \"{fileName}\", line {syntax.Line}\nSyntaxError: {syntax.Message}";
        }
        catch (PyRaise raise) when (raise.Exception.IsInstanceOf(PyExceptionType.SystemExit))
        {
            // `sys.exit()` in a notebook ends the cell, not the session.
            exception = raise.Exception;
            errorName = "SystemExit";
            error = "SystemExit: " + raise.Exception.Display();
        }
        catch (PyRaise raise)
        {
            exception = raise.Exception;
            errorName = raise.Exception.TypeName;
            error = raise.Exception.FormatTraceback(fileName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errorName = "KeyboardInterrupt";
            error = "KeyboardInterrupt";
        }
        finally
        {
            _machine.Cancellation = default;
        }

        FlushStreams();

        // A trailing expression's value is the cell's result, unless the line ends in `;`
        // — the notebook convention for "run this, but do not show it".
        if (error is null && value is not PyNone && !EndsWithSemicolon(source))
        {
            _outputs.Add(new CellOutput(CellOutputKind.Result, Bundle(value)));
            _machine.Globals.Set(new PyStr("_"), value);
            _machine.Globals.Set(new PyStr($"_{number}"), value);
            _history.Set(PyInt.From(number), value);
        }

        if (error is not null)
        {
            _outputs.Add(new CellOutput(CellOutputKind.Error, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text/plain"] = error,
            }) { ErrorName = errorName });
        }

        return new CellResult
        {
            ExecutionCount = number,
            Succeeded = error is null,
            Stdout = _machine.Stdout,
            Stderr = _machine.Stderr,
            Value = value,
            Outputs = [.. _outputs],
            Exception = exception,
            ErrorName = errorName,
            Traceback = error,
        };
    }

    /// <summary>Clears the namespace, keeping the session's machine and its imported modules' code.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var keep = new[] { "__name__", "__file__", "Out" };

        foreach (var key in _machine.Globals.Entries.Select(static e => e.Key).ToList())
        {
            if (!keep.Contains(key.Display()))
            {
                _machine.Globals.Remove(key);
            }
        }

        _history.Clear();
        ExecutionCount = 0;
    }

    /// <summary>Ends the session, handing back anything the program's libraries held.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _machine.CompleteRun();
    }

    private void OnDisplay(PyObject value, IReadOnlyDictionary<string, string>? bundle)
    {
        FlushStreams();

        if (ReferenceEquals(bundle, DisplayModule.ClearOutput))
        {
            _outputs.Clear();
            return;
        }

        _outputs.Add(new CellOutput(CellOutputKind.Display, bundle ?? Bundle(value)));
    }

    private Dictionary<string, string> Bundle(PyObject value) => DisplayModule.Format(_machine, value);

    /// <summary>Turns text printed since the last output into stream outputs, so order is kept.</summary>
    private void FlushStreams()
    {
        if (_machine.StdoutLength > _stdoutMark)
        {
            _outputs.Add(CellOutput.Stream("stdout", _machine.StdoutFrom(_stdoutMark)));
            _stdoutMark = _machine.StdoutLength;
        }

        if (_machine.StderrLength > _stderrMark)
        {
            _outputs.Add(CellOutput.Stream("stderr", _machine.StderrFrom(_stderrMark)));
            _stderrMark = _machine.StderrLength;
        }
    }

    private static bool EndsWithSemicolon(string source)
    {
        var lines = source.TrimEnd().Split('\n');
        var last = lines[^1];
        var comment = last.IndexOf('#', StringComparison.Ordinal);

        return (comment >= 0 ? last[..comment] : last).TrimEnd().EndsWith(';');
    }
}

/// <summary>What one notebook cell produced.</summary>
public sealed record CellResult
{
    /// <summary>The cell's number, as <c>In [n]</c> shows it.</summary>
    public required int ExecutionCount { get; init; }

    /// <summary>True when the cell ran to the end.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Everything the cell printed.</summary>
    public required string Stdout { get; init; }

    /// <summary>Everything the cell wrote to standard error.</summary>
    public required string Stderr { get; init; }

    /// <summary>The value of the cell's trailing expression, or None.</summary>
    public PyObject Value { get; init; } = PyNone.Instance;

    /// <summary>The outputs, in the order they happened.</summary>
    public required IReadOnlyList<CellOutput> Outputs { get; init; }

    /// <summary>The uncaught exception, if one ended the cell.</summary>
    public PyException? Exception { get; init; }

    /// <summary>The error's type name: <c>ValueError</c>, <c>SyntaxError</c>, <c>KeyboardInterrupt</c>.</summary>
    public string? ErrorName { get; init; }

    /// <summary>The formatted traceback, if the cell failed.</summary>
    public string? Traceback { get; init; }
}

/// <summary>What kind of output a cell produced.</summary>
public enum CellOutputKind
{
    /// <summary>Text written to stdout or stderr.</summary>
    Stream,

    /// <summary>A <c>display(...)</c>.</summary>
    Display,

    /// <summary>The value of the cell's trailing expression.</summary>
    Result,

    /// <summary>The error that ended the cell.</summary>
    Error,
}

/// <summary>One output of a cell: a MIME bundle, or a stream's text.</summary>
/// <param name="Kind">What produced it.</param>
/// <param name="Data">
/// The bundle, keyed by MIME type: <c>text/plain</c> always, and <c>text/html</c>,
/// <c>text/markdown</c>, <c>image/svg+xml</c>, <c>image/png</c> (base64), … when the value
/// offered them.
/// </param>
public sealed record CellOutput(CellOutputKind Kind, IReadOnlyDictionary<string, string> Data)
{
    /// <summary>For a stream output, which one: <c>stdout</c> or <c>stderr</c>.</summary>
    public string? StreamName { get; init; }

    /// <summary>For an error output, the exception's type name.</summary>
    public string? ErrorName { get; init; }

    /// <summary>The plain-text form, which every output has.</summary>
    public string Text => Data.TryGetValue("text/plain", out var text) ? text : string.Empty;

    internal static CellOutput Stream(string name, string text) =>
        new(CellOutputKind.Stream, new Dictionary<string, string>(StringComparer.Ordinal) { ["text/plain"] = text })
        {
            StreamName = name,
        };
}
