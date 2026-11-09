using Computerwelt.Emulation.Python.Compilation;
using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Parsing;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Builtins;

/// <summary>
/// The builtins that look at the running program itself: <c>dir</c>, <c>globals</c>,
/// <c>locals</c>, <c>vars</c>, <c>eval</c> and <c>exec</c>.
/// </summary>
/// <remarks>
/// <para>
/// Upstream Monty has none of them. They are here because a notebook is interactive:
/// <c>dir(obj)</c> is how a person finds out what an object can do, and <c>eval</c> /
/// <c>exec</c> are how a program builds code from text. None of them widens the sandbox —
/// source passed to <c>exec</c> is compiled by the same compiler and run on the same
/// machine, under the same limits, as the program that passed it; there is no way to name
/// anything the program could not already reach.
/// </para>
/// </remarks>
internal static class Introspection
{
    /// <summary>Registers the introspection builtins.</summary>
    public static void Register(VirtualMachine machine, PyDict builtins)
    {
        builtins.Set(new PyStr("dir"), new PyBuiltinFunction("dir", arguments =>
        {
            Arity.Between("dir", arguments, 0, 1);
            return arguments.Length == 0 ? SortedNames(Locals(machine).Entries.Select(static e => e.Key.Display())) : Dir(machine, arguments[0]);
        }));

        builtins.Set(new PyStr("globals"), new PyBuiltinFunction("globals", arguments =>
        {
            Arity.Exact("globals", arguments, 0);
            return Current(machine)?.Globals ?? machine.Globals;
        }));

        builtins.Set(new PyStr("locals"), new PyBuiltinFunction("locals", arguments =>
        {
            Arity.Exact("locals", arguments, 0);
            return Locals(machine);
        }));

        builtins.Set(new PyStr("vars"), new PyBuiltinFunction("vars", arguments =>
        {
            Arity.Between("vars", arguments, 0, 1);

            if (arguments.Length == 0)
            {
                return Locals(machine);
            }

            return Attributes.TryGet(machine, arguments[0], "__dict__") is PyDict dict
                ? dict
                : throw new PyRaise(PyErrors.TypeError("vars() argument must have __dict__ attribute"));
        }));

        builtins.Set(new PyStr("eval"), new PyBuiltinFunction("eval", (arguments, keywords) =>
            Evaluate(machine, "eval", arguments, keywords, expressionOnly: true)));

        builtins.Set(new PyStr("compile"), new PyBuiltinFunction("compile", (arguments, keywords) =>
        {
            Arity.Between("compile", arguments, 3, 6);
            var source = arguments[0] switch
            {
                PyStr text => text.Value,
                PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"compile() arg 1 must be a string, bytes or AST object, not {other.TypeName}")),
            };

            var mode = arguments[2].Display();

            if (mode is not ("exec" or "eval" or "single"))
            {
                throw new PyRaise(PyErrors.ValueError("compile() mode must be 'exec', 'eval' or 'single'"));
            }

            try
            {
                var module = Parser.Parse(mode == "eval" ? source.TrimStart(' ', '\t') : source);

                if (mode == "eval" && module.Body is not [ExpressionStatement])
                {
                    throw new PyRaise(new PyException(PyExceptionType.SyntaxError, "invalid syntax", [new PyStr("invalid syntax")]));
                }

                _ = Compiler.CompileModule(module, arguments[1].Display());
            }
            catch (PythonSyntaxError error)
            {
                throw new PyRaise(new PyException(PyExceptionType.SyntaxError, error.Message, [new PyStr(error.Message)]));
            }

            return new PyCodeValue(source, arguments[1].Display(), mode);
        }));

        builtins.Set(new PyStr("exec"), new PyBuiltinFunction("exec", (arguments, keywords) =>
        {
            Evaluate(machine, "exec", arguments, keywords, expressionOnly: false);
            return PyNone.Instance;
        }));
    }

    private static VirtualMachine.Frame? Current(VirtualMachine machine) =>
        machine.CallStack.Count > 0 ? machine.CallStack[^1] : null;

    /// <summary>
    /// <c>locals()</c>: the module namespace at module level, a snapshot of the frame's
    /// variables inside a function — which is CPython's behaviour too: writing to the dict
    /// does not change the function's variables.
    /// </summary>
    private static PyDict Locals(VirtualMachine machine)
    {
        if (Current(machine) is not { } frame)
        {
            return machine.Globals;
        }

        if (frame.Code.Name == "<module>")
        {
            return frame.Globals;
        }

        var snapshot = new PyDict();

        for (var i = 0; i < frame.Code.LocalNames.Count && i < frame.Locals.Length; i++)
        {
            var name = frame.Code.LocalNames[i];

            if (frame.TryGetCell(name, out var cell))
            {
                if (cell.Value is { } captured)
                {
                    snapshot.Set(new PyStr(name), captured);
                }

                continue;
            }

            if (frame.Locals[i] is { } value)
            {
                snapshot.Set(new PyStr(name), value);
            }
        }

        foreach (var name in frame.Code.CellNames)
        {
            if (!snapshot.Contains(new PyStr(name)) && frame.TryGetCell(name, out var cell) && cell.Value is { } value)
            {
                snapshot.Set(new PyStr(name), value);
            }
        }

        return snapshot;
    }

    private static PyObject Evaluate(
        VirtualMachine machine,
        string function,
        PyObject[] arguments,
        PyDict? keywords,
        bool expressionOnly)
    {
        Arity.Between(function, arguments, 1, 3);

        var globals = Mapping(arguments.Length > 1 ? arguments[1] : Keyword(keywords, "globals"), function, "globals");
        var locals = Mapping(arguments.Length > 2 ? arguments[2] : Keyword(keywords, "locals"), function, "locals");

        var source = arguments[0] switch
        {
            PyStr text => text.Value,
            PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value),
            PyCodeValue compiled => compiled.Source,
            var other => throw new PyRaise(PyErrors.TypeError(
                $"{function}() arg 1 must be a string, bytes or code object, not {other.TypeName}")),
        };

        // `eval` strips leading whitespace, as CPython does, so an indented expression from
        // a template still parses.
        if (expressionOnly)
        {
            source = source.TrimStart(' ', '\t');
        }

        var frame = Current(machine);

        // With no namespace given, the code runs where it was called from: the caller's
        // globals, with its local variables visible.
        var namespaceGlobals = globals ?? frame?.Globals ?? machine.Globals;
        var namespaceLocals = locals ?? (globals is null && frame is not null && frame.Code.Name != "<module>" ? Locals(machine) : null);

        PyModule module;

        try
        {
            module = Parser.Parse(source);
        }
        catch (PythonSyntaxError error)
        {
            throw new PyRaise(new PyException(PyExceptionType.SyntaxError, error.Message, [new PyStr(error.Message)]));
        }

        if (expressionOnly && module.Body is not [ExpressionStatement])
        {
            throw new PyRaise(new PyException(PyExceptionType.SyntaxError, "invalid syntax", [new PyStr("invalid syntax")]));
        }

        CodeObject code;

        try
        {
            code = Compiler.CompileModule(module, "<string>");
        }
        catch (PythonSyntaxError error)
        {
            throw new PyRaise(new PyException(PyExceptionType.SyntaxError, error.Message, [new PyStr(error.Message)]));
        }

        if (!expressionOnly)
        {
            // `exec` returns None whatever the code ends with.
            code = Compiler.CompileModule(new PyModule([.. module.Body, new Pass { Line = 0 }]), "<string>");
        }

        // Each level of evaluated source is a level of host recursion, so `exec` of a
        // string that execs itself reaches RecursionError rather than the host's stack.
        using var level = machine.EnterRecursion();

        if (namespaceLocals is null || ReferenceEquals(namespaceLocals, namespaceGlobals))
        {
            return machine.RunModule(code, namespaceGlobals);
        }

        // Separate locals: the code runs in the locals mapping, seeing the globals through
        // it; names it binds stay in the locals, as in a class body.
        var added = new List<KeyValuePair<PyObject, PyObject>>();

        foreach (var (key, value) in namespaceGlobals.Entries)
        {
            if (!namespaceLocals.Contains(key))
            {
                added.Add(new(key, value));
            }
        }

        foreach (var (key, value) in added)
        {
            namespaceLocals.Set(key, value);
        }

        try
        {
            return machine.RunModule(code, namespaceLocals);
        }
        finally
        {
            foreach (var (key, original) in added)
            {
                if (namespaceLocals.TryGetValue(key, out var now) && ReferenceEquals(now, original))
                {
                    namespaceLocals.Remove(key);
                }
            }
        }
    }

    private static PyObject? Keyword(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

    private static PyDict? Mapping(PyObject? value, string function, string name) => value switch
    {
        null or PyNone => null,
        PyDict dict => dict,
        PyInstance { BuiltinValue: PyDict inner } => inner,
        _ => throw new PyRaise(PyErrors.TypeError(
            name == "globals"
                ? $"{function}() globals must be a dict, not {value.TypeName}"
                : $"locals must be a mapping")),
    };

    /// <summary><c>dir(obj)</c>: the sorted attribute names of an object.</summary>
    public static PyList Dir(VirtualMachine machine, PyObject value)
    {
        if (value is PyInstance instance && instance.Class.Lookup("__dir__") is { } custom)
        {
            var names = machine.Call(ClassModel.BindToInstance(machine, custom, instance, instance.Class), []);
            return SortedNames(VirtualMachine.RequireIterable(names).Select(static n => n.Display()));
        }

        var collected = new HashSet<string>(StringComparer.Ordinal);

        switch (value)
        {
            case PyInstance target:
                AddKeys(collected, target.Fields);
                AddClass(collected, target.Class);

                if (target.BuiltinValue is { } inner)
                {
                    AddBuiltin(machine, collected, inner);
                }

                break;

            case PyClass type:
                AddClass(collected, type);
                break;

            case PyModuleObject module:
                collected.UnionWith(module.AttributeNames);
                collected.Add("__name__");
                break;

            case PySourceModule source:
                collected.UnionWith(source.AttributeNames);
                break;

            case HostObjects.IPyAttributeNames host:
                collected.UnionWith(host.AttributeNames);
                break;

            case PyException exception:
                collected.UnionWith(["args", "with_traceback", "add_note", "__cause__", "__context__", "__traceback__", "__suppress_context__"]);

                if (exception.Fields is { } fields)
                {
                    collected.UnionWith(fields.Keys);
                }

                if (exception.UserClass is { } owner)
                {
                    AddClass(collected, owner);
                }

                break;

            case PyFunction function:
                collected.UnionWith(["__name__", "__qualname__", "__doc__", "__call__"]);
                collected.UnionWith(function.Fields.Keys);
                break;

            default:
                AddBuiltin(machine, collected, value);
                break;
        }

        return SortedNames(collected);
    }

    private static void AddClass(HashSet<string> collected, PyClass type)
    {
        foreach (var owner in type.Mro)
        {
            AddKeys(collected, owner.Members);
        }

        collected.UnionWith(ObjectNames);
    }

    private static void AddKeys(HashSet<string> collected, PyDict dict)
    {
        foreach (var (key, _) in dict.Entries)
        {
            collected.Add(key.Display());
        }
    }

    /// <summary>
    /// The public names of a built-in value: every candidate the method binder actually
    /// answers for, so the list cannot claim a method that does not exist.
    /// </summary>
    private static void AddBuiltin(VirtualMachine machine, HashSet<string> collected, PyObject value)
    {
        foreach (var name in CandidateNames)
        {
            if (Attributes.TryGet(machine, value, name) is not null)
            {
                collected.Add(name);
            }
        }

        collected.UnionWith(ObjectNames);
    }

    private static PyList SortedNames(IEnumerable<string> names)
    {
        var sorted = names.Distinct(StringComparer.Ordinal).ToList();
        sorted.Sort(StringComparer.Ordinal);
        return new PyList([.. sorted.Select(static n => (PyObject)new PyStr(n))]);
    }

    private static readonly string[] ObjectNames =
    [
        "__class__", "__delattr__", "__dir__", "__doc__", "__eq__", "__format__", "__ge__",
        "__getattribute__", "__gt__", "__hash__", "__init__", "__init_subclass__", "__le__",
        "__lt__", "__ne__", "__new__", "__repr__", "__setattr__", "__str__",
    ];

    /// <summary>Every public method name a built-in type in this interpreter might have.</summary>
    private static readonly string[] CandidateNames =
    [
        // str
        "capitalize", "casefold", "center", "count", "encode", "endswith", "expandtabs", "find", "format",
        "format_map", "index", "isalnum", "isalpha", "isascii", "isdecimal", "isdigit", "isidentifier",
        "islower", "isnumeric", "isprintable", "isspace", "istitle", "isupper", "join", "ljust", "lower",
        "lstrip", "maketrans", "partition", "removeprefix", "removesuffix", "replace", "rfind", "rindex",
        "rjust", "rpartition", "rsplit", "rstrip", "split", "splitlines", "startswith", "strip", "swapcase",
        "title", "translate", "upper", "zfill",
        // list, dict, set, tuple
        "append", "clear", "copy", "extend", "insert", "pop", "remove", "reverse", "sort",
        "fromkeys", "get", "items", "keys", "popitem", "setdefault", "update", "values",
        "add", "difference", "difference_update", "discard", "intersection", "intersection_update",
        "isdisjoint", "issubset", "issuperset", "symmetric_difference", "symmetric_difference_update", "union",
        // bytes, int, float
        "decode", "hex", "fromhex", "bit_length", "bit_count", "to_bytes", "from_bytes", "as_integer_ratio",
        "conjugate", "is_integer", "real", "imag", "numerator", "denominator",
    ];
}

/// <summary>The result of <c>compile()</c>: source checked for syntax, kept for <c>exec</c>/<c>eval</c>.</summary>
internal sealed class PyCodeValue(string source, string fileName, string mode) : PyObject
{
    /// <summary>The source text.</summary>
    public string Source { get; } = source;

    /// <summary>The mode compile was called with.</summary>
    public string Mode { get; } = mode;

    /// <inheritdoc />
    public override string TypeName => "code";

    /// <inheritdoc />
    public override string Repr() => $"<code object <module> at 0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x8}, file \"{fileName}\">";
}
