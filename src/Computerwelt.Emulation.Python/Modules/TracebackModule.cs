using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// <c>traceback</c>: the exception being handled, formatted as the interpreter would print it.
/// </summary>
/// <remarks>
/// Every frame of a traceback here names one file — the program being run — because that
/// is all a run has. Frame objects and <c>extract_tb</c>'s <c>FrameSummary</c> list are not
/// provided; the formatted text is what programs (and logging) actually use.
/// </remarks>
internal static class TracebackModule
{
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("traceback");

        module.Add("format_exc", (arguments, keywords) =>
        {
            _ = arguments;
            _ = keywords;
            return new PyStr(Handled(machine) is { } exception ? Format(machine, exception) : "NoneType: None\n");
        });

        module.Add("print_exc", (_, keywords) =>
        {
            var text = Handled(machine) is { } exception ? Format(machine, exception) : "NoneType: None\n";
            Write(machine, keywords, text);
            return PyNone.Instance;
        });

        module.Add("format_exception", (arguments, _) =>
        {
            var exception = ExceptionArgument(arguments, "format_exception");
            return new PyList(exception is null ? [new PyStr("NoneType: None\n")] : Lines(Format(machine, exception)));
        });

        module.Add("print_exception", (arguments, keywords) =>
        {
            var exception = ExceptionArgument(arguments, "print_exception");
            Write(machine, keywords, exception is null ? "NoneType: None\n" : Format(machine, exception));
            return PyNone.Instance;
        });

        module.Add("format_exception_only", arguments =>
        {
            var exception = ExceptionArgument(arguments, "format_exception_only");
            return new PyList([new PyStr(exception is null ? "NoneType: None\n" : Only(exception))]);
        });

        module.Add("format_tb", arguments =>
        {
            var exception = arguments.Length > 0 ? Find(arguments[0]) : null;
            return new PyList(exception is null ? [] : Lines(Frames(machine, exception)));
        });

        module.Add("format_stack", _ => new PyList([.. machine.CallStack
            .Select(frame => (PyObject)new PyStr($"  File \"{frame.Code.FileName}\", line {frame.CurrentLine}, in {frame.Code.Name}\n"))]));

        module.Add("print_stack", (_, keywords) =>
        {
            var text = string.Concat(machine.CallStack.Select(frame =>
                $"  File \"{frame.Code.FileName}\", line {frame.CurrentLine}, in {frame.Code.Name}\n"));
            Write(machine, keywords, text);
            return PyNone.Instance;
        });

        return module;
    }

    /// <summary>The exception being handled, searching outward from the innermost frame.</summary>
    internal static PyException? Handled(VirtualMachine machine)
    {
        for (var i = machine.CallStack.Count - 1; i >= 0; i--)
        {
            if (machine.CallStack[i].CurrentException is { } exception)
            {
                return exception;
            }
        }

        return null;
    }

    private static PyException? ExceptionArgument(PyObject[] arguments, string name)
    {
        if (arguments.Length == 0)
        {
            throw new PyRaise(PyErrors.TypeError($"{name}() missing required argument 'exc'"));
        }

        // `format_exception(exc)` and the older `format_exception(type, value, tb)`.
        return Find(arguments.Length >= 2 ? arguments[1] : arguments[0]);
    }

    private static PyException? Find(PyObject value) => value as PyException;

    private static string FileName(VirtualMachine machine) =>
        machine.CallStack.Count > 0 ? machine.CallStack[0].Code.FileName : "<string>";

    private static string Format(VirtualMachine machine, PyException exception) =>
        exception.FormatTraceback(FileName(machine)) + "\n";

    private static string Only(PyException exception)
    {
        var text = exception.Display();
        return text.Length == 0 ? $"{exception.TypeName}\n" : $"{exception.TypeName}: {text}\n";
    }

    private static string Frames(VirtualMachine machine, PyException exception)
    {
        var builder = new StringBuilder();

        foreach (var frame in exception.Traceback)
        {
            builder.Append("  File \"").Append(FileName(machine)).Append("\", line ")
                .Append(frame.Line).Append(", in ").Append(frame.FunctionName).Append('\n');
        }

        return builder.ToString();
    }

    private static List<PyObject> Lines(string text) =>
        [.. text.Split('\n').Where(static l => l.Length > 0).Select(static l => (PyObject)new PyStr(l + "\n"))];

    private static void Write(VirtualMachine machine, PyDict? keywords, string text)
    {
        if (keywords is not null && keywords.TryGetValue(new PyStr("file"), out var file) && file is not PyNone)
        {
            var write = Attributes.Get(machine, file, "write");
            machine.Call(write, [new PyStr(text)]);
            return;
        }

        machine.WriteError(text);
    }
}
