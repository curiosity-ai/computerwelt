using System.Text;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>csv</c> module.</summary>
/// <remarks>
/// <para>
/// A port of <c>_csv.c</c> and <c>csv.py</c>: the reader is the same state machine, one
/// character at a time, so every quirk of CPython's parser is a quirk of this one —
/// <c>\r</c> handling, what a quote in the middle of an unquoted field means, the field size
/// limit, <c>strict</c>. The writer makes the same two-step decision about quoting.
/// </para>
/// <para>
/// Nothing here touches a file. <c>reader</c> pulls lines from any iterable of strings and
/// <c>writer</c> calls the <c>write</c> of any object that has one, so it works over the
/// sandbox's own <c>open()</c>, over <c>io.StringIO</c>, and over a list. A script that
/// writes <c>open(path, newline='')</c> hits the same refusal it does everywhere else in
/// this interpreter; without it the result is identical, because no layer here translates
/// line endings.
/// </para>
/// <para>
/// Classes in this interpreter have no bases, so a script cannot subclass <c>csv.Dialect</c>.
/// A dialect is whatever has the right attributes — a string naming a registered one, one of
/// the built-in classes, or a class of the script's own.
/// </para>
/// </remarks>
public static class CsvModule
{
    internal const int QuoteMinimal = 0;
    internal const int QuoteAll = 1;
    internal const int QuoteNonNumeric = 2;
    internal const int QuoteNone = 3;
    internal const int QuoteStrings = 4;
    internal const int QuoteNotNull = 5;

    /// <summary>
    /// <c>csv.Error</c>. Shared by every run: it is an immutable class, not state.
    /// </summary>
    internal static readonly PyExceptionType Error = PyExceptionType.DefineHostException("Error");

    private static readonly string[] DialectAttributes =
        ["delimiter", "doublequote", "escapechar", "lineterminator", "quotechar", "quoting", "skipinitialspace", "strict"];

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var state = new CsvState(machine);
        var module = new PyModuleObject("csv");

        module.Add("Error", Error);
        module.Add("__version__", new PyStr("1.0"));
        module.Add("QUOTE_MINIMAL", PyInt.From(QuoteMinimal));
        module.Add("QUOTE_ALL", PyInt.From(QuoteAll));
        module.Add("QUOTE_NONNUMERIC", PyInt.From(QuoteNonNumeric));
        module.Add("QUOTE_NONE", PyInt.From(QuoteNone));
        module.Add("QUOTE_STRINGS", PyInt.From(QuoteStrings));
        module.Add("QUOTE_NOTNULL", PyInt.From(QuoteNotNull));

        // `Dialect()` with nothing to go on fails the way CPython's base class does.
        var dialectType = new PyType(
            "csv.Dialect",
            static value => value is CsvDialectObject,
            static (_, _) => throw new PyRaise(new PyException(Error, "\"delimiter\" must be a 1-character string")));

        module.Add("Dialect", dialectType);
        module.Add("excel", state.BuiltIn("excel", ",", true, "\r\n", QuoteMinimal));
        module.Add("excel_tab", state.BuiltIn("excel-tab", "\t", true, "\r\n", QuoteMinimal));
        module.Add("unix_dialect", state.BuiltIn("unix", ",", true, "\n", QuoteAll));

        module.Add("reader", new PyBuiltinFunction("reader", (arguments, keywords) =>
        {
            Arity.Between("reader", arguments, 1, 2);
            var bound = Bind("reader", arguments, keywords, ["csvfile", "dialect"], out var rest);
            var dialect = state.Resolve(bound[1], rest);
            return new CsvReader(state, VirtualMachine.Puller(arguments[0]), dialect);
        }));

        module.Add("writer", new PyBuiltinFunction("writer", (arguments, keywords) =>
        {
            Arity.Between("writer", arguments, 1, 2);
            var bound = Bind("writer", arguments, keywords, ["csvfile", "dialect"], out var rest);
            var dialect = state.Resolve(bound[1], rest);
            return new CsvWriter(state, arguments[0], dialect);
        }));

        module.Add("register_dialect", new PyBuiltinFunction("register_dialect", (arguments, keywords) =>
        {
            Arity.Between("register_dialect", arguments, 1, 2);
            var bound = Bind("register_dialect", arguments, keywords, ["name", "dialect"], out var rest);

            var name = bound[0] is PyStr text
                ? text.Value
                : throw new PyRaise(PyErrors.TypeError("dialect name must be a string"));

            state.Registry[name] = state.Resolve(bound[1], rest);
            return PyNone.Instance;
        }));

        module.Add("unregister_dialect", new PyBuiltinFunction("unregister_dialect", arguments =>
        {
            Arity.Exact("unregister_dialect", arguments, 1);

            return arguments[0] is PyStr name && state.Registry.Remove(name.Value)
                ? PyNone.Instance
                : throw UnknownDialect();
        }));

        module.Add("get_dialect", new PyBuiltinFunction("get_dialect", arguments =>
        {
            Arity.Exact("get_dialect", arguments, 1);
            return state.Lookup(arguments[0]);
        }));

        module.Add("list_dialects", new PyBuiltinFunction("list_dialects", arguments =>
        {
            Arity.Exact("list_dialects", arguments, 0);
            return new PyList([.. state.Registry.Keys.Select(static name => (PyObject)new PyStr(name))]);
        }));

        module.Add("field_size_limit", new PyBuiltinFunction("field_size_limit", (arguments, keywords) =>
        {
            var bound = Bind("field_size_limit", arguments, keywords, ["new_limit"], out _);
            var previous = state.FieldLimit;

            if (bound[0] is { } limit)
            {
                state.FieldLimit = limit is PyInt integer
                    ? (long)System.Numerics.BigInteger.Min(integer.Value, long.MaxValue)
                    : throw new PyRaise(PyErrors.TypeError("limit must be an integer"));
            }

            return PyInt.From(previous);
        }));

        module.Add("DictReader", new PyType(
            "csv.DictReader",
            static value => value is CsvDictReader,
            (arguments, keywords) =>
            {
                var bound = Bind("DictReader", arguments, keywords,
                    ["f", "fieldnames", "restkey", "restval", "dialect"], out var rest);

                var source = bound[0] ?? throw MissingArgument("DictReader", "f");
                var dialectArgument = bound[4] ?? new PyStr("excel");

                return new CsvDictReader(
                    state,
                    new CsvReader(state, VirtualMachine.Puller(source), state.Resolve(dialectArgument, rest)),
                    bound[1] is null or PyNone ? null : bound[1],
                    bound[2] ?? PyNone.Instance,
                    bound[3] ?? PyNone.Instance,
                    dialectArgument);
            }));

        module.Add("DictWriter", new PyType(
            "csv.DictWriter",
            static value => value is CsvDictWriter,
            (arguments, keywords) =>
            {
                var bound = Bind("DictWriter", arguments, keywords,
                    ["f", "fieldnames", "restval", "extrasaction", "dialect"], out var rest);

                var destination = bound[0] ?? throw MissingArgument("DictWriter", "f");
                var fieldnames = bound[1] ?? throw MissingArgument("DictWriter", "fieldnames");
                var extras = bound[3] is PyStr action ? action.Value : "raise";

                if (!extras.Equals("raise", StringComparison.OrdinalIgnoreCase)
                    && !extras.Equals("ignore", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PyRaise(PyErrors.ValueError($"extrasaction ({extras}) must be 'raise' or 'ignore'"));
                }

                var dialect = state.Resolve(bound[4] ?? new PyStr("excel"), rest);

                return new CsvDictWriter(
                    new CsvWriter(state, destination, dialect),
                    fieldnames,
                    bound[2] ?? new PyStr(string.Empty),
                    extras.Equals("raise", StringComparison.OrdinalIgnoreCase));
            }));

        module.Add("Sniffer", new PyType(
            "csv.Sniffer",
            static value => value is CsvSniffer,
            (arguments, keywords) =>
            {
                PyBuiltinFunction.RejectKeywords("Sniffer", keywords);
                Arity.Exact("Sniffer", arguments, 0);
                return new CsvSniffer(state);
            }));

        return module;
    }

    internal static PyRaise UnknownDialect() => new(new PyException(Error, "unknown dialect"));

    internal static PyRaise MissingArgument(string function, string name) =>
        new(PyErrors.TypeError($"{function}() missing 1 required positional argument: '{name}'"));

    /// <summary>
    /// Binds a call to named parameters, all of them positional-or-keyword, and hands back
    /// the keywords that named none of them.
    /// </summary>
    internal static PyObject?[] Bind(string function, PyObject[] arguments, PyDict? keywords, string[] names, out PyDict? rest)
    {
        if (arguments.Length > names.Length)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function}() takes at most {names.Length} arguments ({arguments.Length} given)"));
        }

        var bound = new PyObject?[names.Length];
        arguments.CopyTo(bound, 0);
        rest = null;

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            var at = Array.IndexOf(names, key.Display());

            if (at < 0)
            {
                rest ??= new PyDict();
                rest.Set(key, value);
                continue;
            }

            if (bound[at] is not null)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{function}() got multiple values for argument '{key.Display()}'"));
            }

            bound[at] = value;
        }

        return bound;
    }

    /// <summary>The per-run state: the machine, the registered dialects and the field limit.</summary>
    internal sealed class CsvState
    {
        public CsvState(VirtualMachine machine)
        {
            Machine = machine;

            Registry["excel"] = new CsvDialectObject(new CsvDialect(',', '"', -1, true, false, "\r\n", QuoteMinimal, false));
            Registry["excel-tab"] = new CsvDialectObject(new CsvDialect('\t', '"', -1, true, false, "\r\n", QuoteMinimal, false));
            Registry["unix"] = new CsvDialectObject(new CsvDialect(',', '"', -1, true, false, "\n", QuoteAll, false));
        }

        public VirtualMachine Machine { get; }

        public Dictionary<string, CsvDialectObject> Registry { get; } = new(StringComparer.Ordinal);

        /// <summary>The longest field a reader accepts, in characters. CPython's default.</summary>
        public long FieldLimit { get; set; } = 131072;

        /// <summary>A built-in dialect, as the class <c>csv.excel</c> and friends are.</summary>
        public PyType BuiltIn(string name, string delimiter, bool doubleQuote, string terminator, int quoting)
        {
            var type = new PyType(
                $"csv.{name.Replace('-', '_')}",
                static value => value is CsvDialectObject,
                (_, keywords) => keywords is null ? Registry[name] : Resolve(Registry[name], keywords));

            type.Members["delimiter"] = new PyStr(delimiter);
            type.Members["quotechar"] = new PyStr("\"");
            type.Members["doublequote"] = PyBool.Of(doubleQuote);
            type.Members["skipinitialspace"] = PyBool.False;
            type.Members["lineterminator"] = new PyStr(terminator);
            type.Members["quoting"] = PyInt.From(quoting);
            type.Members["escapechar"] = PyNone.Instance;
            type.Members["strict"] = PyBool.False;
            type.Members["_name"] = new PyStr(name);
            return type;
        }

        public CsvDialectObject Lookup(PyObject name) =>
            name is PyStr text && Registry.TryGetValue(text.Value, out var found) ? found : throw UnknownDialect();

        /// <summary>
        /// Builds the dialect a call asks for from a name, an object with the right
        /// attributes, or nothing, and the keywords that override any of it.
        /// </summary>
        public CsvDialectObject Resolve(PyObject? dialect, PyDict? overrides)
        {
            foreach (var (key, _) in overrides?.Entries ?? [])
            {
                if (!DialectAttributes.Contains(key.Display(), StringComparer.Ordinal))
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"this function got an unexpected keyword argument '{key.Display()}'"));
                }
            }

            if (dialect is PyStr name)
            {
                dialect = Lookup(name);
            }

            if (dialect is CsvDialectObject existing && overrides is null)
            {
                return existing;
            }

            PyObject? Attribute(string attribute) =>
                overrides is not null && overrides.TryGetValue(new PyStr(attribute), out var given)
                    ? given
                    : dialect is null or PyNone ? null : Attributes.TryGet(Machine, dialect, attribute);

            var quoting = Attribute("quoting");
            var quotechar = Attribute("quotechar");

            var quotingValue = quoting switch
            {
                null => QuoteMinimal,
                PyInt integer when integer.Value >= 0 && integer.Value <= 5 => (int)integer.Value,
                PyInt => throw new PyRaise(PyErrors.TypeError("bad \"quoting\" value")),
                _ => throw new PyRaise(PyErrors.TypeError("\"quoting\" must be an integer")),
            };

            var delimiter = ReadChar("delimiter", Attribute("delimiter"), ',', allowNone: false);
            var quote = ReadChar("quotechar", quotechar, '"', allowNone: true);
            var escape = ReadChar("escapechar", Attribute("escapechar"), -1, allowNone: true);

            var terminator = Attribute("lineterminator") switch
            {
                null => "\r\n",
                PyStr text => text.Value,
                PyNone => throw new PyRaise(PyErrors.TypeError("lineterminator must be set")),
                _ => throw new PyRaise(PyErrors.TypeError("\"lineterminator\" must be a string")),
            };

            // Saying there is no quote character, and nothing about quoting, means no quoting.
            if (quotechar is PyNone && quoting is null)
            {
                quotingValue = QuoteNone;
            }

            if (quotingValue != QuoteNone && quote < 0)
            {
                throw new PyRaise(PyErrors.TypeError("quotechar must be set if quoting enabled"));
            }

            return new CsvDialectObject(new CsvDialect(
                (char)delimiter,
                quote,
                escape,
                Attribute("doublequote")?.IsTruthy() ?? true,
                Attribute("skipinitialspace")?.IsTruthy() ?? false,
                terminator,
                quotingValue,
                Attribute("strict")?.IsTruthy() ?? false));
        }

        private static int ReadChar(string name, PyObject? value, int fallback, bool allowNone)
        {
            switch (value)
            {
                case null:
                    return fallback;

                case PyNone when allowNone:
                    return -1;

                case PyStr { Value.Length: 1 } single:
                    return single.Value[0];

                case PyStr:
                    throw new PyRaise(PyErrors.TypeError($"\"{name}\" must be a 1-character string"));

                default:
                    throw new PyRaise(PyErrors.TypeError(
                        $"\"{name}\" must be string{(allowNone ? " or None" : string.Empty)}, not {value.TypeName}"));
            }
        }
    }
}

/// <summary>The settings a reader or writer was made with.</summary>
/// <param name="Delimiter">The field separator.</param>
/// <param name="QuoteChar">The quote character, or -1 for none.</param>
/// <param name="EscapeChar">The escape character, or -1 for none.</param>
/// <param name="DoubleQuote">Whether a quote inside a quoted field is written as two.</param>
/// <param name="SkipInitialSpace">Whether a space right after a delimiter is dropped.</param>
/// <param name="LineTerminator">What ends a written row.</param>
/// <param name="Quoting">One of the <c>QUOTE_*</c> constants.</param>
/// <param name="Strict">Whether malformed input raises rather than being tolerated.</param>
internal sealed record CsvDialect(
    char Delimiter,
    int QuoteChar,
    int EscapeChar,
    bool DoubleQuote,
    bool SkipInitialSpace,
    string LineTerminator,
    int Quoting,
    bool Strict);

/// <summary>A dialect as a script sees it: read-only attributes.</summary>
internal sealed class CsvDialectObject(CsvDialect dialect) : PyObject
{
    public CsvDialect Dialect { get; } = dialect;

    public override string TypeName => "Dialect";

    public override string Repr() => "<_csv.Dialect object>";

    public override System.Numerics.BigInteger PyHash() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override PyObject? GetAttribute(string name) => name switch
    {
        "delimiter" => new PyStr(Dialect.Delimiter.ToString()),
        "quotechar" => Dialect.QuoteChar < 0 ? PyNone.Instance : new PyStr(((char)Dialect.QuoteChar).ToString()),
        "escapechar" => Dialect.EscapeChar < 0 ? PyNone.Instance : new PyStr(((char)Dialect.EscapeChar).ToString()),
        "doublequote" => PyBool.Of(Dialect.DoubleQuote),
        "skipinitialspace" => PyBool.Of(Dialect.SkipInitialSpace),
        "lineterminator" => new PyStr(Dialect.LineTerminator),
        "quoting" => PyInt.From(Dialect.Quoting),
        "strict" => PyBool.Of(Dialect.Strict),
        _ => null,
    };
}

/// <summary>The object <c>csv.reader</c> returns: an iterator over parsed rows.</summary>
internal sealed class CsvReader : PyIterator
{
    private const int Eol = -2;

    private enum State
    {
        StartRecord,
        StartField,
        EscapedChar,
        InField,
        InQuotedField,
        EscapeInQuotedField,
        QuoteInQuotedField,
        EatCrNl,
        AfterEscapedCrNl,
    }

    private readonly CsvModule.CsvState _state;
    private readonly Func<PyObject?> _lines;
    private readonly CsvDialectObject _dialectObject;
    private readonly CsvDialect _dialect;
    private readonly StringBuilder _field = new();
    private List<PyObject> _fields = [];
    private State _mode = State.StartRecord;
    private bool _numeric;
    private bool _quoted;

    public CsvReader(CsvModule.CsvState state, Func<PyObject?> lines, CsvDialectObject dialect)
        : base(static () => null, "_csv.reader")
    {
        _state = state;
        _lines = lines;
        _dialectObject = dialect;
        _dialect = dialect.Dialect;
    }

    public long LineNumber { get; private set; }

    public override string Repr() => "<_csv.reader object>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "dialect" => _dialectObject,
        "line_num" => PyInt.From(LineNumber),
        "__next__" => new PyBuiltinFunction("__next__", _ => Next() ?? throw new PyRaise(PyErrors.StopIteration())),
        "__iter__" => new PyBuiltinFunction("__iter__", _ => this),
        _ => null,
    };

    /// <summary>Parses the next record, or returns null at the end of the input.</summary>
    public override PyObject? Next()
    {
        _fields = [];
        _field.Clear();
        _mode = State.StartRecord;
        _numeric = false;
        _quoted = false;

        do
        {
            var line = _lines();

            if (line is null)
            {
                // Input that ends inside a quoted field still has a record in it, unless the
                // dialect says that is an error.
                if (_field.Length != 0 || _mode == State.InQuotedField)
                {
                    if (_dialect.Strict)
                    {
                        throw Fail("unexpected end of data");
                    }

                    SaveField();
                    break;
                }

                return null;
            }

            if (line is not PyStr text)
            {
                throw Fail($"iterator should return strings, not {line.TypeName} (the file should be opened in text mode)");
            }

            LineNumber++;

            foreach (var c in text.Value)
            {
                Process(c);
            }

            Process(Eol);
        }
        while (_mode != State.StartRecord);

        return new PyList(_fields);
    }

    private static PyRaise Fail(string message) => new(new PyException(CsvModule.Error, message));

    private void Process(int c)
    {
        var d = _dialect;

        switch (_mode)
        {
            case State.StartRecord:
                if (c == Eol)
                {
                    // An empty line is an empty record.
                    return;
                }

                if (c is '\n' or '\r')
                {
                    _mode = State.EatCrNl;
                    return;
                }

                _mode = State.StartField;
                goto case State.StartField;

            case State.StartField:
                if (c is '\n' or '\r' or Eol)
                {
                    SaveField();
                    _mode = c == Eol ? State.StartRecord : State.EatCrNl;
                }
                else if (c == d.QuoteChar && d.Quoting != CsvModule.QuoteNone)
                {
                    _quoted = true;
                    _mode = State.InQuotedField;
                }
                else if (c == d.EscapeChar)
                {
                    _mode = State.EscapedChar;
                }
                else if (c == ' ' && d.SkipInitialSpace)
                {
                    // Ignored.
                }
                else if (c == d.Delimiter)
                {
                    SaveField();
                }
                else
                {
                    if (d.Quoting is CsvModule.QuoteNonNumeric or CsvModule.QuoteStrings)
                    {
                        _numeric = true;
                    }

                    AddChar(c);
                    _mode = State.InField;
                }

                return;

            case State.EscapedChar:
                if (c is '\n' or '\r')
                {
                    AddChar(c);
                    _mode = State.AfterEscapedCrNl;
                    return;
                }

                AddChar(c == Eol ? '\n' : c);
                _mode = State.InField;
                return;

            case State.AfterEscapedCrNl:
                if (c == Eol)
                {
                    return;
                }

                goto case State.InField;

            case State.InField:
                if (c is '\n' or '\r' or Eol)
                {
                    SaveField();
                    _mode = c == Eol ? State.StartRecord : State.EatCrNl;
                }
                else if (c == d.EscapeChar)
                {
                    _mode = State.EscapedChar;
                }
                else if (c == d.Delimiter)
                {
                    SaveField();
                    _mode = State.StartField;
                }
                else
                {
                    AddChar(c);
                }

                return;

            case State.InQuotedField:
                if (c == Eol)
                {
                    return;
                }

                if (c == d.EscapeChar)
                {
                    _mode = State.EscapeInQuotedField;
                }
                else if (c == d.QuoteChar && d.Quoting != CsvModule.QuoteNone)
                {
                    _mode = d.DoubleQuote ? State.QuoteInQuotedField : State.InField;
                }
                else
                {
                    AddChar(c);
                }

                return;

            case State.EscapeInQuotedField:
                AddChar(c == Eol ? '\n' : c);
                _mode = State.InQuotedField;
                return;

            case State.QuoteInQuotedField:
                if (d.Quoting != CsvModule.QuoteNone && c == d.QuoteChar)
                {
                    AddChar(c);
                    _mode = State.InQuotedField;
                }
                else if (c == d.Delimiter)
                {
                    SaveField();
                    _mode = State.StartField;
                }
                else if (c is '\n' or '\r' or Eol)
                {
                    SaveField();
                    _mode = c == Eol ? State.StartRecord : State.EatCrNl;
                }
                else if (!d.Strict)
                {
                    AddChar(c);
                    _mode = State.InField;
                }
                else
                {
                    throw Fail($"'{d.Delimiter}' expected after '{(char)d.QuoteChar}'");
                }

                return;

            case State.EatCrNl:
                if (c is '\n' or '\r')
                {
                    return;
                }

                if (c == Eol)
                {
                    _mode = State.StartRecord;
                    return;
                }

                throw Fail("new-line character seen in unquoted field - do you need to open the file with newline=''?");
        }
    }

    private void AddChar(int c)
    {
        if (_field.Length >= _state.FieldLimit)
        {
            throw Fail($"field larger than field limit ({_state.FieldLimit})");
        }

        _field.Append((char)c);
    }

    private void SaveField()
    {
        var text = _field.ToString();
        _field.Clear();

        PyObject value;

        if (_numeric)
        {
            _numeric = false;
            value = Builtins.Conversions.ToFloat([new PyStr(text)]);
        }
        else if (!_quoted && text.Length == 0 && _dialect.Quoting is CsvModule.QuoteNotNull or CsvModule.QuoteStrings)
        {
            value = PyNone.Instance;
        }
        else
        {
            value = new PyStr(text);
        }

        _quoted = false;
        _fields.Add(value);
    }
}

/// <summary>The object <c>csv.writer</c> returns.</summary>
internal sealed class CsvWriter : PyObject
{
    private readonly CsvModule.CsvState _state;
    private readonly CsvDialectObject _dialectObject;
    private readonly CsvDialect _dialect;
    private readonly PyObject _write;

    public CsvWriter(CsvModule.CsvState state, PyObject destination, CsvDialectObject dialect)
    {
        _state = state;
        _dialectObject = dialect;
        _dialect = dialect.Dialect;

        _write = Attributes.TryGet(state.Machine, destination, "write")
            ?? throw new PyRaise(PyErrors.TypeError("argument 1 must have a \"write\" method"));
    }

    public override string TypeName => "_csv.writer";

    public override string Repr() => "<_csv.writer object>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "dialect" => _dialectObject,
        "writerow" => new PyBuiltinFunction("writerow", arguments =>
        {
            Arity.Exact("writerow", arguments, 1);
            return WriteRow(arguments[0]);
        }),
        "writerows" => new PyBuiltinFunction("writerows", arguments =>
        {
            Arity.Exact("writerows", arguments, 1);
            WriteRows(arguments[0]);
            return PyNone.Instance;
        }),
        _ => null,
    };

    public void WriteRows(PyObject rows)
    {
        foreach (var row in rows.Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                     $"'{rows.TypeName}' object is not iterable")))
        {
            WriteRow(row);
        }
    }

    public PyObject WriteRow(PyObject row)
    {
        var fields = row.Iterate() ?? throw new PyRaise(new PyException(
            CsvModule.Error, $"iterable expected, not {row.TypeName}"));

        var record = new StringBuilder();
        var count = 0;

        foreach (var field in fields)
        {
            var quoted = _dialect.Quoting switch
            {
                CsvModule.QuoteNonNumeric => field is not (PyInt or PyFloat),
                CsvModule.QuoteAll => true,
                CsvModule.QuoteStrings => field is PyStr,
                CsvModule.QuoteNotNull => field is not PyNone,
                _ => false,
            };

            Append(record, count, field is PyNone ? string.Empty : field is PyStr text ? text.Value : field.Display(), quoted);
            count++;
        }

        // A row of one empty field would write as a blank line, which reads back as no row
        // at all — so it is written quoted.
        if (count == 1 && record.Length == 0)
        {
            if (_dialect.Quoting == CsvModule.QuoteNone)
            {
                throw new PyRaise(new PyException(CsvModule.Error, "single empty field record must be quoted"));
            }

            record.Append((char)_dialect.QuoteChar).Append((char)_dialect.QuoteChar);
        }

        record.Append(_dialect.LineTerminator);
        return _state.Machine.Call(_write, [new PyStr(record.ToString())]);
    }

    private void Append(StringBuilder record, int index, string field, bool quoted)
    {
        var d = _dialect;

        if (index > 0)
        {
            record.Append(d.Delimiter);
        }

        var body = new StringBuilder(field.Length);

        foreach (var c in field)
        {
            if (c == d.Delimiter || c == d.EscapeChar || c == d.QuoteChar || d.LineTerminator.Contains(c, StringComparison.Ordinal))
            {
                var escape = false;

                if (d.Quoting == CsvModule.QuoteNone)
                {
                    escape = true;
                }
                else
                {
                    if (c == d.QuoteChar)
                    {
                        if (d.DoubleQuote)
                        {
                            body.Append((char)d.QuoteChar);
                        }
                        else
                        {
                            escape = true;
                        }
                    }
                    else if (c == d.EscapeChar)
                    {
                        escape = true;
                    }

                    if (!escape)
                    {
                        quoted = true;
                    }
                }

                if (escape)
                {
                    if (d.EscapeChar < 0)
                    {
                        throw new PyRaise(new PyException(CsvModule.Error, "need to escape, but no escapechar set"));
                    }

                    body.Append((char)d.EscapeChar);
                }
            }

            body.Append(c);
        }

        if (quoted)
        {
            record.Append((char)d.QuoteChar).Append(body).Append((char)d.QuoteChar);
        }
        else
        {
            record.Append(body);
        }
    }
}

/// <summary>The object <c>csv.DictReader</c> builds: rows as dictionaries.</summary>
internal sealed class CsvDictReader : PyIterator
{
    private readonly CsvReader _reader;
    private readonly PyObject _restKey;
    private readonly PyObject _restValue;
    private readonly PyObject _dialect;
    private PyObject? _fieldnames;
    private long _lineNumber;

    public CsvDictReader(CsvModule.CsvState state, CsvReader reader, PyObject? fieldnames, PyObject restKey, PyObject restValue, PyObject dialect)
        : base(static () => null, "csv.DictReader")
    {
        _ = state;
        _reader = reader;
        _fieldnames = fieldnames;
        _restKey = restKey;
        _restValue = restValue;
        _dialect = dialect;
    }

    public override string Repr() => "<csv.DictReader object>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "fieldnames" => Fieldnames(),
        "restkey" => _restKey,
        "restval" => _restValue,
        "reader" => _reader,
        "dialect" => _dialect,
        "line_num" => PyInt.From(_lineNumber),
        "__next__" => new PyBuiltinFunction("__next__", _ => Next() ?? throw new PyRaise(PyErrors.StopIteration())),
        "__iter__" => new PyBuiltinFunction("__iter__", _ => this),
        _ => null,
    };

    public override bool SetAttribute(string name, PyObject value)
    {
        if (name != "fieldnames")
        {
            return false;
        }

        _fieldnames = value;
        return true;
    }

    /// <summary>The field names, read from the first row when none were given.</summary>
    private PyObject? Fieldnames()
    {
        if (_fieldnames is null)
        {
            _fieldnames = _reader.Next();
        }

        _lineNumber = _reader.LineNumber;
        return _fieldnames ?? PyNone.Instance;
    }

    public override PyObject? Next()
    {
        if (_lineNumber == 0)
        {
            Fieldnames();
        }

        var row = _reader.Next();
        _lineNumber = _reader.LineNumber;

        if (row is null)
        {
            return null;
        }

        // Blank rows are skipped: they would only make a dictionary of defaults.
        while (row is PyList { Items.Count: 0 })
        {
            row = _reader.Next();
            _lineNumber = _reader.LineNumber;

            if (row is null)
            {
                return null;
            }
        }

        var names = (_fieldnames?.Iterate() ?? throw new PyRaise(PyErrors.TypeError("'NoneType' object is not iterable"))).ToList();
        var values = ((PyList)row).Items;
        var result = new PyDict();

        for (var i = 0; i < Math.Min(names.Count, values.Count); i++)
        {
            result.Set(names[i], values[i]);
        }

        if (names.Count < values.Count)
        {
            result.Set(_restKey, new PyList(values.GetRange(names.Count, values.Count - names.Count)));
        }
        else if (names.Count > values.Count)
        {
            for (var i = values.Count; i < names.Count; i++)
            {
                result.Set(names[i], _restValue);
            }
        }

        return result;
    }
}

/// <summary>The object <c>csv.DictWriter</c> builds: dictionaries as rows.</summary>
internal sealed class CsvDictWriter(CsvWriter writer, PyObject fieldnames, PyObject restValue, bool raiseOnExtras) : PyObject
{
    private PyObject _fieldnames = fieldnames;

    public override string TypeName => "DictWriter";

    public override string Repr() => "<csv.DictWriter object>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "fieldnames" => _fieldnames,
        "restval" => restValue,
        "extrasaction" => new PyStr(raiseOnExtras ? "raise" : "ignore"),
        "writer" => writer,

        "writeheader" => new PyBuiltinFunction("writeheader", arguments =>
        {
            Arity.Exact("writeheader", arguments, 0);

            var header = new PyDict();

            foreach (var field in Names())
            {
                header.Set(field, field);
            }

            return writer.WriteRow(Row(header));
        }),

        "writerow" => new PyBuiltinFunction("writerow", arguments =>
        {
            Arity.Exact("writerow", arguments, 1);
            return writer.WriteRow(Row(arguments[0]));
        }),

        "writerows" => new PyBuiltinFunction("writerows", arguments =>
        {
            Arity.Exact("writerows", arguments, 1);

            foreach (var row in arguments[0].Iterate() ?? throw new PyRaise(PyErrors.TypeError(
                         $"'{arguments[0].TypeName}' object is not iterable")))
            {
                writer.WriteRow(Row(row));
            }

            return PyNone.Instance;
        }),

        _ => null,
    };

    public override bool SetAttribute(string name, PyObject value)
    {
        if (name != "fieldnames")
        {
            return false;
        }

        _fieldnames = value;
        return true;
    }

    private List<PyObject> Names() =>
        [.. _fieldnames.Iterate() ?? throw new PyRaise(PyErrors.TypeError(
            $"'{_fieldnames.TypeName}' object is not iterable"))];

    /// <summary>Turns a dictionary into the list of its values, in field order.</summary>
    private PyList Row(PyObject row)
    {
        var dict = row as PyDict ?? throw new PyRaise(PyErrors.AttributeError(row.TypeName, "keys"));
        var names = Names();

        if (raiseOnExtras)
        {
            var extra = dict.Entries.Select(static e => e.Key).Where(key => !names.Any(name => PyObject.SameOrEqual(name, key))).ToList();

            if (extra.Count > 0)
            {
                throw new PyRaise(PyErrors.ValueError(
                    "dict contains fields not in fieldnames: " + string.Join(", ", extra.Select(static key => key.Repr()))));
            }
        }

        return new PyList([.. names.Select(name => dict.TryGetValue(name, out var value) ? value : restValue)]);
    }
}

/// <summary>The object <c>csv.Sniffer</c> builds: guesses a dialect from a sample.</summary>
internal sealed class CsvSniffer(CsvModule.CsvState state) : PyObject
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] Preferred = [",", "\t", ";", " ", ":"];

    private static readonly Regex[] QuotePatterns =
    [
        new(@"(?<delim>[^\w\n""'])(?<space> ?)(?<quote>[""']).*?\k<quote>\k<delim>", RegexOptions.Singleline | RegexOptions.Multiline, MatchTimeout),
        new(@"(?:^|\n)(?<quote>[""']).*?\k<quote>(?<delim>[^\w\n""'])(?<space> ?)", RegexOptions.Singleline | RegexOptions.Multiline, MatchTimeout),
        new(@"(?<delim>[^\w\n""'])(?<space> ?)(?<quote>[""']).*?\k<quote>(?:$|\n)", RegexOptions.Singleline | RegexOptions.Multiline, MatchTimeout),
        new(@"(?:^|\n)(?<quote>[""']).*?\k<quote>(?:$|\n)", RegexOptions.Singleline | RegexOptions.Multiline, MatchTimeout),
    ];

    public override string TypeName => "Sniffer";

    public override string Repr() => "<csv.Sniffer object>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "preferred" => new PyList([.. Preferred.Select(static p => (PyObject)new PyStr(p))]),

        "sniff" => new PyBuiltinFunction("sniff", (arguments, keywords) =>
        {
            var bound = CsvModule.Bind("sniff", arguments, keywords, ["sample", "delimiters"], out _);
            var sample = Sample(bound[0]);
            var delimiters = bound[1] is null or PyNone ? null : bound[1]!.Display();

            return Sniff(sample, delimiters);
        }),

        "has_header" => new PyBuiltinFunction("has_header", arguments =>
        {
            Arity.Exact("has_header", arguments, 1);
            return PyBool.Of(HasHeader(Sample(arguments[0])));
        }),

        _ => null,
    };

    private static string Sample(PyObject? value) => value switch
    {
        PyStr text => text.Value,
        null => throw CsvModule.MissingArgument("sniff", "sample"),
        _ => throw new PyRaise(PyErrors.TypeError(
            $"expected str, got {value.TypeName}")),
    };

    private PyObject Sniff(string sample, string? delimiters)
    {
        try
        {
            var (quote, doubleQuote, delimiter, skipSpace) = GuessQuoteAndDelimiter(sample, delimiters);

            if (delimiter.Length == 0)
            {
                (delimiter, skipSpace) = GuessDelimiter(sample, delimiters);
            }

            if (delimiter.Length == 0)
            {
                throw new PyRaise(new PyException(CsvModule.Error, "Could not determine delimiter"));
            }

            // What CPython returns is a class derived from `Dialect`; here it is the same
            // thing under the built-in classes' shape, so `.delimiter` and passing it to
            // `reader` work, and so does calling it.
            var type = new PyType(
                "csv.sniffed",
                static value => value is CsvDialectObject,
                (_, _) => state.Resolve(null, Overrides(delimiter, quote, doubleQuote, skipSpace)));

            foreach (var (key, value) in Overrides(delimiter, quote, doubleQuote, skipSpace).Entries)
            {
                type.Members[key.Display()] = value;
            }

            type.Members["_name"] = new PyStr("sniffed");
            type.Members["escapechar"] = PyNone.Instance;
            type.Members["strict"] = PyBool.False;
            return type;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new PyRaise(new PyException(CsvModule.Error, "Could not determine delimiter"));
        }
    }

    private static PyDict Overrides(string delimiter, string quote, bool doubleQuote, bool skipSpace)
    {
        var dict = new PyDict();
        dict.Set(new PyStr("delimiter"), new PyStr(delimiter));
        dict.Set(new PyStr("quotechar"), new PyStr(quote.Length > 0 ? quote : "\""));
        dict.Set(new PyStr("doublequote"), PyBool.Of(doubleQuote));
        dict.Set(new PyStr("skipinitialspace"), PyBool.Of(skipSpace));
        dict.Set(new PyStr("lineterminator"), new PyStr("\r\n"));
        dict.Set(new PyStr("quoting"), PyInt.From(CsvModule.QuoteMinimal));
        return dict;
    }

    private static (string Quote, bool DoubleQuote, string Delimiter, bool SkipSpace) GuessQuoteAndDelimiter(string data, string? delimiters)
    {
        MatchCollection? matches = null;
        Regex? used = null;

        foreach (var pattern in QuotePatterns)
        {
            var found = pattern.Matches(data);

            if (found.Count > 0)
            {
                matches = found;
                used = pattern;
                break;
            }
        }

        if (matches is null || used is null)
        {
            return (string.Empty, false, string.Empty, false);
        }

        var quotes = new Dictionary<string, int>(StringComparer.Ordinal);
        var delims = new Dictionary<string, int>(StringComparer.Ordinal);
        var spaces = 0;
        var hasDelimiter = used.GetGroupNames().Contains("delim");

        foreach (Match match in matches)
        {
            var quote = match.Groups["quote"].Value;

            if (quote.Length > 0)
            {
                quotes[quote] = quotes.GetValueOrDefault(quote) + 1;
            }

            // The last pattern has no delimiter group: a single column of quoted data.
            if (!hasDelimiter)
            {
                continue;
            }

            var delim = match.Groups["delim"].Value;

            if (delim.Length > 0 && (delimiters is null || delimiters.Contains(delim, StringComparison.Ordinal)))
            {
                delims[delim] = delims.GetValueOrDefault(delim) + 1;
            }

            if (match.Groups["space"].Value.Length > 0)
            {
                spaces++;
            }
        }

        // First of the most common, as `max` over a dict gives.
        var quoteChar = quotes.Aggregate((best, next) => next.Value > best.Value ? next : best).Key;
        string delimiter;
        bool skipInitialSpace;

        if (delims.Count > 0)
        {
            var best = delims.Aggregate((a, b) => b.Value > a.Value ? b : a);
            delimiter = best.Key;
            skipInitialSpace = best.Value == spaces;

            // A single column: the newline is not a delimiter.
            if (delimiter == "\n")
            {
                delimiter = string.Empty;
            }
        }
        else
        {
            delimiter = string.Empty;
            skipInitialSpace = false;
        }

        // An extra quote between delimiters means the quote is doubled to escape it.
        var escaped = Regex.Escape(delimiter);
        var quoteEscaped = Regex.Escape(quoteChar);
        var doubled = new Regex(
            $@"(({escaped})|^)\W*{quoteEscaped}[^{escaped}\n]*{quoteEscaped}[^{escaped}\n]*{quoteEscaped}\W*(({escaped})|$)",
            RegexOptions.Multiline,
            MatchTimeout);

        return (quoteChar, doubled.IsMatch(data), delimiter, skipInitialSpace);
    }

    private static (string Delimiter, bool SkipSpace) GuessDelimiter(string sample, string? delimiters)
    {
        var data = sample.Split('\n').Where(static line => line.Length > 0).ToList();
        var chunk = Math.Min(10, data.Count);
        var iteration = 0;

        // For each ASCII character: how many lines had it how many times.
        var frequencies = new Dictionary<char, List<(int Count, int Lines)>>();
        var modes = new Dictionary<char, (int Count, int Lines)>();
        var delims = new Dictionary<char, (int Count, int Lines)>();
        int start = 0, end = chunk;

        while (start < data.Count)
        {
            iteration++;

            foreach (var line in data.Skip(start).Take(end - start))
            {
                for (var code = 0; code < 127; code++)
                {
                    var c = (char)code;

                    if (!frequencies.TryGetValue(c, out var meta))
                    {
                        meta = [];
                        frequencies[c] = meta;
                    }

                    var count = line.Count(x => x == c);
                    var at = meta.FindIndex(m => m.Count == count);

                    if (at >= 0)
                    {
                        meta[at] = (count, meta[at].Lines + 1);
                    }
                    else
                    {
                        meta.Add((count, 1));
                    }
                }
            }

            foreach (var (c, meta) in frequencies)
            {
                if (meta.Count == 1 && meta[0].Count == 0)
                {
                    continue;
                }

                if (meta.Count > 1)
                {
                    // The mode, less everything that disagrees with it.
                    var top = meta[0];

                    foreach (var item in meta)
                    {
                        if (item.Lines > top.Lines)
                        {
                            top = item;
                        }
                    }

                    var others = meta.Where(item => item != top).Sum(static item => item.Lines);
                    modes[c] = (top.Count, top.Lines - others);
                }
                else
                {
                    modes[c] = meta[0];
                }
            }

            var total = (double)Math.Min(chunk * iteration, data.Count);
            var consistency = 1.0;

            while (delims.Count == 0 && consistency >= 0.9)
            {
                foreach (var (k, v) in modes)
                {
                    if (v.Count > 0 && v.Lines > 0
                        && (v.Lines / total) >= consistency
                        && (delimiters is null || delimiters.Contains(k, StringComparison.Ordinal)))
                    {
                        delims[k] = v;
                    }
                }

                consistency -= 0.01;
            }

            if (delims.Count == 1)
            {
                var only = delims.Keys.First();
                return (only.ToString(), data[0].Count(x => x == only) == CountSpaced(data[0], only));
            }

            start = end;
            end += chunk;
        }

        if (delims.Count == 0)
        {
            return (string.Empty, false);
        }

        if (delims.Count > 1)
        {
            foreach (var preferred in Preferred)
            {
                if (delims.ContainsKey(preferred[0]))
                {
                    return (preferred, data[0].Count(x => x == preferred[0]) == CountSpaced(data[0], preferred[0]));
                }
            }
        }

        var chosen = delims.Select(static e => (e.Value, e.Key)).OrderBy(static e => e.Value.Count).ThenBy(static e => e.Value.Lines).ThenBy(static e => e.Key).Last().Key;

        return (chosen.ToString(), data[0].Count(x => x == chosen) == CountSpaced(data[0], chosen));
    }

    /// <summary>How many times <paramref name="c"/> appears followed by a space.</summary>
    private static int CountSpaced(string line, char c)
    {
        var count = 0;

        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == c && line[i + 1] == ' ')
            {
                count++;
                i++;
            }
        }

        return count;
    }

    private bool HasHeader(string sample)
    {
        var dialect = state.Resolve(Sniff(sample, null), null);
        var lines = SplitKeepingNewlines(sample).Select(static line => (PyObject)new PyStr(line)).GetEnumerator();
        var reader = new CsvReader(state, () => lines.MoveNext() ? lines.Current : null, dialect);

        var header = reader.Next() as PyList ?? throw new PyRaise(PyErrors.StopIteration());
        var columns = header.Items.Count;

        // Per column: null while unknown, -1 for "parses as a number", else the length every
        // row's value has. Removed once two rows disagree.
        var types = new SortedDictionary<int, int?>();

        for (var i = 0; i < columns; i++)
        {
            types[i] = null;
        }

        var checkedRows = 0;

        while (reader.Next() is PyList row)
        {
            if (checkedRows > 20)
            {
                break;
            }

            checkedRows++;

            if (row.Items.Count != columns)
            {
                continue;
            }

            foreach (var column in types.Keys.ToList())
            {
                var text = row.Items[column].Display();
                var kind = LooksNumeric(text) ? -1 : text.Length;

                if (types[column] != kind)
                {
                    if (types[column] is null)
                    {
                        types[column] = kind;
                    }
                    else
                    {
                        types.Remove(column);
                    }
                }
            }
        }

        var votes = 0;

        foreach (var (column, kind) in types)
        {
            var value = header.Items[column].Display();

            if (kind is >= 0)
            {
                votes += value.Length != kind ? 1 : -1;
            }
            else
            {
                votes += LooksNumeric(value) ? -1 : 1;
            }
        }

        return votes > 0;
    }

    private static IEnumerable<string> SplitKeepingNewlines(string text)
    {
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>Whether <c>complex(text)</c> would succeed.</summary>
    private static bool LooksNumeric(string text) => Complex.IsMatch(text.Trim());

    private static readonly Regex Complex = new(
        @"^(?=.)(?:[+-]?(?:inf|infinity|nan)|[+-]?(?:\d[\d_]*\.?[\d_]*|\.\d[\d_]*)(?:e[+-]?\d+)?)?"
        + @"(?:[+-]?(?:(?:inf|infinity|nan)|(?:\d[\d_]*\.?[\d_]*|\.\d[\d_]*)(?:e[+-]?\d+)?)?j)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
}
