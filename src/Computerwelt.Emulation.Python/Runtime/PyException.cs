using System.Numerics;
using System.Text;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>A Python exception instance.</summary>
public sealed class PyException : PyObject
{
    /// <summary>Creates an exception of <paramref name="type"/>.</summary>
    public PyException(PyExceptionType type, string message, IReadOnlyList<PyObject>? arguments = null)
    {
        ExceptionType = type;
        Message = message;
        Arguments = arguments ?? (message.Length > 0 ? [new PyStr(message)] : []);
    }

    /// <summary>Which exception class this is.</summary>
    public PyExceptionType ExceptionType { get; }

    /// <summary>The message, as <c>str(e)</c> shows it when no <c>__str__</c> overrides it.</summary>
    public string Message { get; private set; }

    /// <summary><c>e.args</c>.</summary>
    public IReadOnlyList<PyObject> Arguments { get; private set; }

    /// <summary>
    /// The Python class this exception is an instance of, when that is a class defined in
    /// Python (<c>class MyError(ValueError)</c>) rather than a built-in exception.
    /// </summary>
    public PyClass? UserClass { get; init; }

    /// <summary>The exception being handled when this one was raised (<c>__context__</c>).</summary>
    public PyException? Context { get; set; }

    /// <summary>True after <c>raise ... from None</c>, which hides <see cref="Context"/> in a traceback.</summary>
    public bool SuppressContext { get; set; }

    /// <summary>The explicit <c>raise ... from ...</c> cause.</summary>
    public PyException? Cause { get; set; }

    /// <summary>Traceback frames, innermost last.</summary>
    public List<TracebackFrame> Traceback { get; } = [];

    /// <summary>
    /// Attributes attached to the exception — by a host library (the <c>msg</c>, <c>doc</c>
    /// and <c>pos</c> of a <c>JSONDecodeError</c>) or by the program (<c>self.code = 3</c>).
    /// </summary>
    public Dictionary<string, PyObject>? Fields { get; set; }

    /// <inheritdoc />
    public override string TypeName => ExceptionType.Name;

    /// <inheritdoc />
    public override string Display() =>
        UserMethod("__str__") is { } str ? Text(str, "__str__") : Message;

    /// <inheritdoc />
    public override string Repr() =>
        UserMethod("__repr__") is { } repr ? Text(repr, "__repr__") : BaseRepr();

    /// <summary><c>BaseException.__str__</c>: the message the arguments make.</summary>
    public string BaseDisplay() => Message;

    /// <summary><c>BaseException.__repr__</c>.</summary>
    public string BaseRepr() =>
        Arguments.Count == 0
            ? $"{ExceptionType.Name}()"
            : $"{ExceptionType.Name}({string.Join(", ", Arguments.Select(static a => a.Repr()))})";

    /// <summary>A method a Python subclass defines, bound to this exception.</summary>
    private PyObject? UserMethod(string name) =>
        UserClass is { Machine: { } machine } type && type.Lookup(name) is { } method
            ? ClassModel.BindToInstance(machine, method, this, type)
            : null;

    private string Text(PyObject method, string name) =>
        UserClass!.Machine!.Call(method, []) switch
        {
            PyStr text => text.Value,
            var other => throw new PyRaise(PyErrors.TypeError(
                $"{name} returned non-string (type {other.TypeName})")),
        };

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        if (Fields is not null && Fields.TryGetValue(name, out var field))
        {
            return field;
        }

        if (name == "__class__")
        {
            return (PyObject?)UserClass ?? ExceptionType;
        }

        if (UserClass is { Machine: { } machine } type && type.Lookup(name) is { } member)
        {
            return ClassModel.BindToInstance(machine, member, this, type);
        }

        return BaseAttribute(name);
    }

    /// <summary>The attributes every exception has, whatever its class defines.</summary>
    public PyObject? BaseAttribute(string name) => name switch
    {
        "args" => new PyTuple([.. Arguments]),
        "__context__" => (PyObject?)Context ?? PyNone.Instance,
        "__cause__" => (PyObject?)Cause ?? PyNone.Instance,
        "__suppress_context__" => PyBool.Of(SuppressContext || Cause is not null),
        "__traceback__" => PyNone.Instance,
        "__dict__" => DictView(),
        "with_traceback" => new PyBuiltinFunction("with_traceback", _ => this),
        "add_note" => new PyBuiltinFunction("add_note", arguments =>
        {
            Arity.Exact("add_note", arguments, 1);

            if (arguments[0] is not PyStr)
            {
                throw new PyRaise(PyErrors.TypeError($"note must be a str, not '{arguments[0].TypeName}'"));
            }

            Fields ??= new(StringComparer.Ordinal);

            if (!Fields.TryGetValue("__notes__", out var notes) || notes is not PyList list)
            {
                list = new PyList([]);
                Fields["__notes__"] = list;
            }

            list.Items.Add(arguments[0]);
            return PyNone.Instance;
        }),
        _ => null,
    };

    private PyDict DictView()
    {
        var dict = new PyDict();

        foreach (var (key, value) in Fields ?? [])
        {
            dict.Set(new PyStr(key), value);
        }

        return dict;
    }

    /// <inheritdoc />
    /// <remarks>Any exception accepts attributes, as CPython's do.</remarks>
    public override bool SetAttribute(string name, PyObject value)
    {
        switch (name)
        {
            case "args":
                Arguments = [.. VirtualMachine.RequireIterable(value)];
                Message = ExceptionType.MessageFor([.. Arguments]);
                return true;
            case "__cause__":
                Cause = value as PyException;
                SuppressContext = true;
                return true;
            case "__context__":
                Context = value as PyException;
                return true;
            case "__suppress_context__":
                SuppressContext = value.IsTruthy();
                return true;
        }

        if (UserClass is { Machine: { } machine } type && type.Lookup(name) is PyProperty { Setter: { } setter })
        {
            machine.Call(setter, [this, value]);
            return true;
        }

        Fields ??= new(StringComparer.Ordinal);
        Fields[name] = value;
        return true;
    }

    /// <inheritdoc />
    public override bool DeleteAttribute(string name) =>
        Fields?.Remove(name) == true ? true : throw new PyRaise(PyErrors.AttributeError(TypeName, name));

    /// <summary>
    /// <c>BaseException.__init__</c> run again with new arguments — what
    /// <c>super().__init__(message)</c> does inside a subclass's initializer.
    /// </summary>
    internal void Reinitialize(VirtualMachine machine, PyObject[] arguments)
    {
        _ = machine;
        var template = ExceptionType.NearestBuiltin().Instantiate(arguments);

        Arguments = template.Arguments;
        Message = template.Message;

        foreach (var (key, value) in template.Fields ?? [])
        {
            Fields ??= new(StringComparer.Ordinal);
            Fields[key] = value;
        }
    }

    /// <summary>True when this exception is an instance of <paramref name="type"/> or a subtype.</summary>
    public bool IsInstanceOf(PyExceptionType type) => ExceptionType.IsSubclassOf(type);

    /// <summary>Formats the traceback the way CPython prints it to stderr.</summary>
    public string FormatTraceback(string fileName)
    {
        var builder = new StringBuilder();

        if (Cause is { } cause)
        {
            builder.Append(cause.FormatTraceback(fileName));
            builder.Append("\nThe above exception was the direct cause of the following exception:\n\n");
        }
        else if (Context is { } context && !SuppressContext)
        {
            builder.Append(context.FormatTraceback(fileName));
            builder.Append("\nDuring handling of the above exception, another exception occurred:\n\n");
        }

        builder.Append("Traceback (most recent call last):\n");

        foreach (var frame in Traceback)
        {
            builder.Append("  File \"").Append(fileName).Append("\", line ")
                .Append(frame.Line).Append(", in ").Append(frame.FunctionName).Append('\n');

            if (frame.SourceLine is { Length: > 0 } source)
            {
                builder.Append("    ").Append(source.Trim()).Append('\n');
            }
        }

        builder.Append(ExceptionType.Name);

        var text = Display();

        if (text.Length > 0)
        {
            builder.Append(": ").Append(text);
        }

        return builder.ToString();
    }
}

/// <summary>One traceback entry.</summary>
/// <param name="FunctionName">The function the frame was executing, or <c>&lt;module&gt;</c>.</param>
/// <param name="Line">The 1-based source line.</param>
/// <param name="SourceLine">The text of that line, when the source is available.</param>
public readonly record struct TracebackFrame(string FunctionName, int Line, string? SourceLine);

/// <summary>
/// An exception class.
/// </summary>
/// <remarks>
/// Exception classes form the one inheritance hierarchy this interpreter has, because
/// <c>except ValueError</c> has to catch <c>ValueError</c> subclasses to be useful at all.
/// User classes remain flat, as upstream documents.
/// </remarks>
public sealed class PyExceptionType : PyObject
{
    private PyExceptionType(string name, PyExceptionType? baseType, PyExceptionType? secondBase = null)
    {
        Name = name;
        BaseType = baseType;
        SecondBase = secondBase;
    }

    /// <summary>The class name.</summary>
    public string Name { get; }

    /// <summary>The base class, or null for <c>BaseException</c>.</summary>
    public PyExceptionType? BaseType { get; }

    /// <summary>
    /// How calling the class builds an instance, for a class whose constructor takes more
    /// than a message — null for the ordinary one-message shape.
    /// </summary>
    public Func<PyObject[], PyException>? Factory { get; set; }

    /// <summary>
    /// A second base, for the one class in the hierarchy that has two.
    /// </summary>
    /// <remarks>
    /// <c>io.UnsupportedOperation</c> derives from both <c>OSError</c> and
    /// <c>ValueError</c> in CPython, and scripts catch it either way.
    /// </remarks>
    public PyExceptionType? SecondBase { get; }

    /// <inheritdoc />
    public override string TypeName => "type";

    /// <inheritdoc />
    public override string Repr() => $"<class '{Name}'>";

    /// <inheritdoc />
    /// <remarks>A class is a first-class value, hashable by identity like any other.</remarks>
    public override System.Numerics.BigInteger PyHash() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>The Python class this type stands behind, when it was defined in Python.</summary>
    public PyClass? UserClass { get; private set; }

    /// <summary>The nearest ancestor that is a built-in (or host) exception rather than a Python class.</summary>
    public PyExceptionType NearestBuiltin()
    {
        var current = this;

        while (current.UserClass is not null && current.BaseType is { } parent)
        {
            current = parent;
        }

        return current;
    }

    /// <summary>The message <c>str(e)</c> shows for these arguments.</summary>
    /// <remarks>
    /// A KeyError shows its key's repr rather than its text, which is what makes
    /// <c>KeyError('x')</c> print as <c>'x'</c> and a missing key visible when it is blank;
    /// with several arguments it is the repr of the argument tuple.
    /// </remarks>
    public string MessageFor(PyObject[] arguments)
    {
        var single = arguments.Length > 0 ? arguments[0] : null;

        return single is null
            ? string.Empty
            : arguments.Length > 1
                ? new PyTuple(arguments).Repr()
                : ReferenceEquals(NearestBuiltin(), KeyError)
                    ? single.Repr()
                    : single.Display();
    }

    /// <summary>Calls the class: builds an instance from constructor arguments.</summary>
    public PyException Instantiate(PyObject[] arguments) =>
        Factory is { } factory ? factory(arguments) : new PyException(this, MessageFor(arguments), arguments);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "__name__" or "__qualname__" => new PyStr(Name),
        "__module__" => new PyStr("builtins"),
        "__doc__" => PyNone.Instance,
        "__bases__" => new PyTuple(BaseType is null ? [TypeRegistry.Object] : SecondBase is null ? [BaseType] : [BaseType, SecondBase]),
        "__base__" => (PyObject?)BaseType ?? TypeRegistry.Object,
        "__mro__" => Mro(),
        "__init__" => new PyBuiltinFunction("__init__", (arguments, _) =>
        {
            if (arguments.Length == 0 || arguments[0] is not PyException self)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"descriptor '__init__' of '{Name}' object needs an argument"));
            }

            self.Reinitialize(null!, arguments[1..]);
            return PyNone.Instance;
        }),
        "__str__" => new PyBuiltinFunction("__str__", arguments =>
            new PyStr(arguments.Length > 0 && arguments[0] is PyException self ? self.BaseDisplay() : string.Empty)),
        "__repr__" => new PyBuiltinFunction("__repr__", arguments =>
            new PyStr(arguments.Length > 0 && arguments[0] is PyException self ? self.BaseRepr() : string.Empty)),
        "__new__" => new PyBuiltinFunction("__new__", arguments =>
            arguments.Length > 0 && arguments[0] is PyExceptionType type
                ? type.Instantiate(arguments[1..])
                : throw new PyRaise(PyErrors.TypeError("BaseException.__new__(X): X is not a type object"))),
        _ => null,
    };

    private PyTuple Mro()
    {
        var items = new List<PyObject>();

        for (var current = this; current is not null; current = current.BaseType)
        {
            items.Add(current);
        }

        items.Add(TypeRegistry.Object);
        return new PyTuple([.. items]);
    }

    /// <summary>
    /// Defines the exception class standing behind a Python class that derives from an
    /// exception, so that <c>raise</c>, <c>except</c> and tracebacks treat it as one.
    /// </summary>
    internal static PyExceptionType DefineUserException(
        PyClass owner,
        PyExceptionType baseType,
        PyExceptionType? secondBase)
    {
        return new PyExceptionType(owner.Name, baseType, secondBase) { UserClass = owner };
    }

    /// <summary>True when this class is <paramref name="other"/> or derives from it.</summary>
    public bool IsSubclassOf(PyExceptionType other)
    {
        for (var current = this; current is not null; current = current.BaseType)
        {
            if (ReferenceEquals(current, other) || current.SecondBase?.IsSubclassOf(other) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Defines an exception class for a host library.
    /// </summary>
    /// <remarks>
    /// Deliberately <i>not</i> added to <see cref="Registry"/>, and so not a builtin name: a
    /// program reaches it only through the module that exposes it, exactly as it reaches
    /// anything else a host registered. <c>except</c> matching, <c>raise</c> and
    /// <c>isinstance</c> all work on it because they ask this class about its bases rather
    /// than about its name.
    /// </remarks>
    /// <param name="name">The class name, as a traceback and <c>__name__</c> report it.</param>
    /// <param name="baseType">What it derives from. Defaults to <c>Exception</c>.</param>
    public static PyExceptionType DefineHostException(string name, PyExceptionType? baseType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new PyExceptionType(name, baseType ?? Exception);
    }

    /// <summary>
    /// Defines a host exception class with two bases — <c>calendar.IllegalMonthError</c>
    /// is both a <c>ValueError</c> and an <c>IndexError</c>, <c>decimal.DivisionByZero</c>
    /// both a <c>DecimalException</c> and a <c>ZeroDivisionError</c> — so a script catches
    /// it by either.
    /// </summary>
    /// <param name="name">The class name.</param>
    /// <param name="baseType">The first base.</param>
    /// <param name="secondBase">The second base.</param>
    public static PyExceptionType DefineHostException(string name, PyExceptionType baseType, PyExceptionType secondBase)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new PyExceptionType(name, baseType, secondBase);
    }

    private static PyExceptionType Define(string name, PyExceptionType? baseType, PyExceptionType? secondBase = null)
    {
        var type = new PyExceptionType(name, baseType, secondBase);
        Registry[name] = type;
        return type;
    }

    /// <summary>Every built-in exception class, by name.</summary>
    public static Dictionary<string, PyExceptionType> Registry { get; } = new(StringComparer.Ordinal);

    /// <summary><c>BaseException</c>.</summary>
    public static PyExceptionType BaseException { get; } = Define("BaseException", null);

    /// <summary><c>Exception</c>.</summary>
    public static PyExceptionType Exception { get; } = Define("Exception", BaseException);

    /// <summary><c>ArithmeticError</c>.</summary>
    public static PyExceptionType ArithmeticError { get; } = Define("ArithmeticError", Exception);

    /// <summary><c>LookupError</c>.</summary>
    public static PyExceptionType LookupError { get; } = Define("LookupError", Exception);

    /// <summary><c>ValueError</c>.</summary>
    public static PyExceptionType ValueError { get; } = Define("ValueError", Exception);

    /// <summary><c>TypeError</c>.</summary>
    public static PyExceptionType TypeError { get; } = Define("TypeError", Exception);

    /// <summary><c>json.JSONDecodeError</c>, which is a <c>ValueError</c>.</summary>
    public static PyExceptionType JsonDecodeError { get; } = Define("JSONDecodeError", ValueError);

    /// <summary><c>SyntaxError</c>, raised by <c>eval</c>, <c>exec</c> and <c>compile</c> on bad source.</summary>
    public static PyExceptionType SyntaxError { get; } = Define("SyntaxError", Exception);

    /// <summary><c>IndentationError</c>.</summary>
    public static PyExceptionType IndentationError { get; } = Define("IndentationError", SyntaxError);

    /// <summary><c>NameError</c>.</summary>
    public static PyExceptionType NameError { get; } = Define("NameError", Exception);

    /// <summary><c>UnboundLocalError</c>.</summary>
    public static PyExceptionType UnboundLocalError { get; } = Define("UnboundLocalError", NameError);

    /// <summary><c>AttributeError</c>.</summary>
    public static PyExceptionType AttributeError { get; } = Define("AttributeError", Exception);

    /// <summary><c>IndexError</c>.</summary>
    public static PyExceptionType IndexError { get; } = Define("IndexError", LookupError);

    /// <summary><c>KeyError</c>.</summary>
    public static PyExceptionType KeyError { get; } = Define("KeyError", LookupError);

    /// <summary><c>ZeroDivisionError</c>.</summary>
    public static PyExceptionType ZeroDivisionError { get; } = Define("ZeroDivisionError", ArithmeticError);

    /// <summary><c>OverflowError</c>.</summary>
    public static PyExceptionType OverflowError { get; } = Define("OverflowError", ArithmeticError);

    /// <summary><c>StopIteration</c>.</summary>
    public static PyExceptionType StopIteration { get; } = Define("StopIteration", Exception);

    /// <summary><c>EOFError</c>, raised by <c>input()</c> past the end of standard input.</summary>
    public static PyExceptionType EOFError { get; } = Define("EOFError", Exception);

    /// <summary><c>AssertionError</c>.</summary>
    public static PyExceptionType AssertionError { get; } = Define("AssertionError", Exception);

    /// <summary><c>RuntimeError</c>.</summary>
    public static PyExceptionType RuntimeError { get; } = Define("RuntimeError", Exception);

    /// <summary><c>RecursionError</c>.</summary>
    public static PyExceptionType RecursionError { get; } = Define("RecursionError", RuntimeError);

    /// <summary><c>NotImplementedError</c>.</summary>
    public static PyExceptionType NotImplementedError { get; } = Define("NotImplementedError", RuntimeError);

    /// <summary><c>ImportError</c>.</summary>
    public static PyExceptionType ImportError { get; } = Define("ImportError", Exception);

    /// <summary><c>ModuleNotFoundError</c>.</summary>
    public static PyExceptionType ModuleNotFoundError { get; } = Define("ModuleNotFoundError", ImportError);

    /// <summary><c>OSError</c>.</summary>
    public static PyExceptionType OSError { get; } = Define("OSError", Exception);

    /// <summary><c>FileNotFoundError</c>.</summary>
    public static PyExceptionType FileNotFoundError { get; } = Define("FileNotFoundError", OSError);

    /// <summary><c>FileExistsError</c>.</summary>
    public static PyExceptionType FileExistsError { get; } = Define("FileExistsError", OSError);

    /// <summary><c>IsADirectoryError</c>.</summary>
    public static PyExceptionType IsADirectoryError { get; } = Define("IsADirectoryError", OSError);

    /// <summary><c>NotADirectoryError</c>.</summary>
    public static PyExceptionType NotADirectoryError { get; } = Define("NotADirectoryError", OSError);

    /// <summary>
    /// <c>io.UnsupportedOperation</c>, raised by an operation the file's mode forbids.
    /// </summary>
    public static PyExceptionType UnsupportedOperation { get; } =
        Define("io.UnsupportedOperation", OSError, ValueError);

    /// <summary><c>PermissionError</c>.</summary>
    public static PyExceptionType PermissionError { get; } = Define("PermissionError", OSError);

    /// <summary><c>TimeoutError</c>, an <c>OSError</c> since Python 3.3.</summary>
    public static PyExceptionType TimeoutError { get; } = Define("TimeoutError", OSError);

    /// <summary><c>InterruptedError</c>.</summary>
    public static PyExceptionType InterruptedError { get; } = Define("InterruptedError", OSError);

    /// <summary><c>ConnectionError</c>.</summary>
    public static PyExceptionType ConnectionError { get; } = Define("ConnectionError", OSError);

    /// <summary><c>BlockingIOError</c>.</summary>
    public static PyExceptionType BlockingIOError { get; } = Define("BlockingIOError", OSError);

    /// <summary><c>re.PatternError</c>, also spelled <c>re.error</c>.</summary>
    public static PyExceptionType PatternError { get; } = Define("PatternError", ValueError);

    /// <summary><c>UnicodeError</c>.</summary>
    public static PyExceptionType UnicodeError { get; } = Define("UnicodeError", ValueError);

    /// <summary><c>UnicodeDecodeError</c>.</summary>
    public static PyExceptionType UnicodeDecodeError { get; } = Define("UnicodeDecodeError", UnicodeError);

    /// <summary><c>UnicodeEncodeError</c>.</summary>
    public static PyExceptionType UnicodeEncodeError { get; } = Define("UnicodeEncodeError", UnicodeError);

    /// <summary><c>KeyboardInterrupt</c>.</summary>
    public static PyExceptionType KeyboardInterrupt { get; } = Define("KeyboardInterrupt", BaseException);

    /// <summary><c>GeneratorExit</c>, thrown into a generator that is being closed.</summary>
    public static PyExceptionType GeneratorExit { get; } = Define("GeneratorExit", BaseException);

    /// <summary><c>SystemExit</c>.</summary>
    public static PyExceptionType SystemExit { get; } = Define("SystemExit", BaseException);

    /// <summary><c>MemoryError</c>.</summary>
    public static PyExceptionType MemoryError { get; } = Define("MemoryError", Exception);

    // The warning categories are builtins in CPython, and the `warnings` module filters,
    // raises and records them by class — so they are defined here, with the rest.

    /// <summary><c>Warning</c>, the base of every warning category.</summary>
    public static PyExceptionType Warning { get; } = Define("Warning", Exception);

    /// <summary><c>UserWarning</c>, the default category of <c>warnings.warn</c>.</summary>
    public static PyExceptionType UserWarning { get; } = Define("UserWarning", Warning);

    /// <summary><c>DeprecationWarning</c>.</summary>
    public static PyExceptionType DeprecationWarning { get; } = Define("DeprecationWarning", Warning);

    /// <summary><c>PendingDeprecationWarning</c>.</summary>
    public static PyExceptionType PendingDeprecationWarning { get; } = Define("PendingDeprecationWarning", Warning);

    /// <summary><c>SyntaxWarning</c>.</summary>
    public static PyExceptionType SyntaxWarning { get; } = Define("SyntaxWarning", Warning);

    /// <summary><c>RuntimeWarning</c>.</summary>
    public static PyExceptionType RuntimeWarning { get; } = Define("RuntimeWarning", Warning);

    /// <summary><c>FutureWarning</c>.</summary>
    public static PyExceptionType FutureWarning { get; } = Define("FutureWarning", Warning);

    /// <summary><c>ImportWarning</c>.</summary>
    public static PyExceptionType ImportWarning { get; } = Define("ImportWarning", Warning);

    /// <summary><c>UnicodeWarning</c>.</summary>
    public static PyExceptionType UnicodeWarning { get; } = Define("UnicodeWarning", Warning);

    /// <summary><c>BytesWarning</c>.</summary>
    public static PyExceptionType BytesWarning { get; } = Define("BytesWarning", Warning);

    /// <summary><c>ResourceWarning</c>.</summary>
    public static PyExceptionType ResourceWarning { get; } = Define("ResourceWarning", Warning);

    /// <summary><c>EncodingWarning</c>.</summary>
    public static PyExceptionType EncodingWarning { get; } = Define("EncodingWarning", Warning);
}

/// <summary>
/// The .NET exception that carries a Python exception up the call stack.
/// </summary>
/// <remarks>
/// Using a .NET exception for Python's <c>raise</c> means <c>finally</c> blocks in the
/// interpreter's own C# unwind correctly, and it keeps the VM loop from threading an error
/// flag through every instruction.
/// </remarks>
public sealed class PyRaise : Exception
{
    /// <summary>Wraps a Python exception.</summary>
    public PyRaise(PyException exception) : base(exception.Message) => Exception = exception;

    /// <summary>The Python exception being raised.</summary>
    public PyException Exception { get; }
}

/// <summary>Factories for the built-in exceptions, with CPython's message wording.</summary>
public static class PyErrors
{
    /// <summary>Creates a <c>TypeError</c>.</summary>
    public static PyException TypeError(string message) => new(PyExceptionType.TypeError, message);

    /// <summary>Creates a <c>ValueError</c>.</summary>
    public static PyException ValueError(string message) => new(PyExceptionType.ValueError, message);

    /// <summary>Creates a <c>NameError</c> with CPython's wording.</summary>
    public static PyException NameError(string name) =>
        new(PyExceptionType.NameError, $"name '{name}' is not defined");

    /// <summary>Creates an <c>UnboundLocalError</c>.</summary>
    public static PyException UnboundLocalError(string name) =>
        new(PyExceptionType.UnboundLocalError, $"cannot access local variable '{name}' where it is not associated with a value");

    /// <summary>
    /// Creates the <c>NameError</c> for reading a captured variable that has no value.
    /// </summary>
    /// <remarks>
    /// A frame that merely captured the cell gets this rather than an
    /// <c>UnboundLocalError</c>: the variable is not its local, and the frame that owns it
    /// has not assigned it yet.
    /// </remarks>
    /// <param name="name">The variable's name.</param>
    /// <returns>The exception.</returns>
    public static PyException UnboundFreeVariable(string name) =>
        new(
            PyExceptionType.NameError,
            $"cannot access free variable '{name}' where it is not associated with a value in enclosing scope");

    /// <summary>Creates an <c>AttributeError</c>.</summary>
    public static PyException AttributeError(string typeName, string attribute) =>
        new(PyExceptionType.AttributeError, $"'{typeName}' object has no attribute '{attribute}'");

    /// <summary>Creates an <c>IndexError</c>.</summary>
    public static PyException IndexError(string message) => new(PyExceptionType.IndexError, message);

    /// <summary>
    /// Creates a <c>KeyError</c>. Its message is the key's <c>repr</c>, not a sentence —
    /// scripts print it and expect exactly that.
    /// </summary>
    public static PyException KeyError(PyObject key)
    {
        string text;

        try
        {
            text = key.Repr();
        }
        catch (PyRaise)
        {
            // A key too large to render — a huge integer — still names a missing key, and
            // reporting that is more useful than replacing it with the rendering failure.
            text = $"<{key.TypeName}>";
        }

        return new PyException(PyExceptionType.KeyError, text, [key]);
    }

    /// <summary>Creates an <c>OverflowError</c>.</summary>
    public static PyException OverflowErrorFor(string message) => new(PyExceptionType.OverflowError, message);

    /// <summary>Creates a <c>ZeroDivisionError</c>.</summary>
    public static PyException ZeroDivisionError(string message) =>
        new(PyExceptionType.ZeroDivisionError, message);

    /// <summary>Creates a <c>StopIteration</c>.</summary>
    public static PyException StopIteration() => new(PyExceptionType.StopIteration, string.Empty);

    /// <summary>Creates an <c>AssertionError</c>.</summary>
    public static PyException AssertionError(string message) =>
        new(PyExceptionType.AssertionError, message);

    /// <summary>Creates a <c>RuntimeError</c>.</summary>
    public static PyException RuntimeError(string message) => new(PyExceptionType.RuntimeError, message);

    /// <summary>Creates an <c>ImportError</c>.</summary>
    public static PyException ModuleNotFound(string module) =>
        new(PyExceptionType.ModuleNotFoundError, $"No module named '{module}'");

    /// <summary>Creates an exception by class name, for <c>raise</c> of a builtin.</summary>
    public static PyException Create(string typeName, string message) =>
        new(PyExceptionType.Registry.TryGetValue(typeName, out var type) ? type : PyExceptionType.Exception, message);

    /// <summary>The hash of a value, or a <c>TypeError</c> when it is unhashable.</summary>
    public static BigInteger RequireHash(PyObject value) => value.PyHash();
}
