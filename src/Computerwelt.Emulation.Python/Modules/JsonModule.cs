using System.Globalization;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>json</c> module.</summary>
/// <remarks>
/// <para>
/// Written directly rather than over <c>System.Text.Json</c>: Python's encoder emits
/// <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> as bare tokens, coerces non-string keys,
/// sorts keys on request, and has its own separator and indent rules — and its decoder
/// reports positions as line/column pairs. None of that is what the .NET reader and writer
/// do, and scripts match on the exact messages.
/// </para>
/// <para>
/// Beyond upstream: <c>dump</c> and <c>load</c> over any object with <c>write</c> / <c>read</c>,
/// the <c>default</c> fallback and <c>check_circular</c> on the encoder, every decoder hook
/// (<c>object_hook</c>, <c>object_pairs_hook</c>, <c>parse_float</c>, <c>parse_int</c>,
/// <c>parse_constant</c>, <c>strict</c>), the <c>JSONEncoder</c> and <c>JSONDecoder</c>
/// classes, and the <c>msg</c>/<c>doc</c>/<c>pos</c>/<c>lineno</c>/<c>colno</c> attributes of
/// <c>JSONDecodeError</c>. A class a script defines cannot derive from these — classes here
/// have no bases — so <c>cls=</c> accepts the two built-in classes, or any callable that
/// builds an object with an <c>encode</c> / <c>decode</c> method.
/// </para>
/// </remarks>
public static class JsonModule
{
    /// <summary>The digit limit CPython applies to decimal integer conversion.</summary>
    private const int MaxIntegerDigits = PyInt.MaxStringDigits;

    private static readonly string[] EncoderKeywords =
        ["skipkeys", "ensure_ascii", "check_circular", "allow_nan", "indent", "separators", "default", "sort_keys"];

    private static readonly string[] DecoderKeywords =
        ["object_hook", "parse_float", "parse_int", "parse_constant", "strict", "object_pairs_hook"];

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create(VirtualMachine machine)
    {
        var module = new PyModuleObject("json");

        // Calling `JSONDecodeError(msg, doc, pos)` builds the message CPython builds, and
        // keeps the three arguments as attributes.
        PyExceptionType.JsonDecodeError.Factory = static arguments =>
        {
            if (arguments.Length != 3)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"JSONDecodeError.__init__() takes exactly 3 arguments ({arguments.Length} given)"));
            }

            return DecodeError(
                arguments[0].Display(),
                arguments[1].Display(),
                arguments[2] is PyInt position ? position.ToIndex() : throw new PyRaise(PyErrors.TypeError(
                    $"'{arguments[2].TypeName}' object cannot be interpreted as an integer")));
        };

        var encoderType = new PyType(
            "json.encoder.JSONEncoder",
            static value => value is JsonEncoderObject,
            (arguments, keywords) =>
            {
                RejectPositional("JSONEncoder.__init__", arguments);
                return new JsonEncoderObject(machine, EncoderOptions.Read(keywords, allowCls: false), keywords);
            });

        var decoderType = new PyType(
            "json.decoder.JSONDecoder",
            static value => value is JsonDecoderObject,
            (arguments, keywords) =>
            {
                RejectPositional("JSONDecoder.__init__", arguments);
                return new JsonDecoderObject(machine, DecoderOptions.Read(keywords, allowCls: false));
            });

        module.Add("JSONDecodeError", PyExceptionType.JsonDecodeError);
        module.Add("JSONEncoder", encoderType);
        module.Add("JSONDecoder", decoderType);

        module.Add("dumps", (arguments, keywords) =>
        {
            Positional("dumps", "obj", arguments, keywords, 1);
            return new PyStr(Dumps(machine, encoderType, arguments[0], keywords));
        });

        module.Add("dump", (arguments, keywords) =>
        {
            if (arguments.Length < 2)
            {
                throw new PyRaise(PyErrors.TypeError(arguments.Length == 0
                    ? "dump() missing 2 required positional arguments: 'obj' and 'fp'"
                    : "dump() missing 1 required positional argument: 'fp'"));
            }

            if (arguments.Length > 2)
            {
                throw new PyRaise(PyErrors.TypeError(ArityMessage(
                    "dump", 2, arguments.Length, keywords?.Count ?? 0)));
            }

            var text = Dumps(machine, encoderType, arguments[0], keywords);
            var write = Attributes.TryGet(machine, arguments[1], "write")
                ?? throw new PyRaise(PyErrors.AttributeError(arguments[1].TypeName, "write"));

            machine.Call(write, [new PyStr(text)]);
            return PyNone.Instance;
        });

        module.Add("loads", (arguments, keywords) =>
        {
            Positional("loads", "s", arguments, keywords, 1);
            return Loads(machine, decoderType, arguments[0], keywords);
        });

        module.Add("load", (arguments, keywords) =>
        {
            Positional("load", "fp", arguments, keywords, 1);

            var read = Attributes.TryGet(machine, arguments[0], "read")
                ?? throw new PyRaise(PyErrors.AttributeError(arguments[0].TypeName, "read"));

            return Loads(machine, decoderType, machine.Call(read, []), keywords);
        });

        // `json.decoder` and `json.encoder` exist as attributes, and as modules a program
        // can import from, because `from json.decoder import JSONDecodeError` is how a great
        // many scripts spell it.
        module.Add("decoder", new PyModuleObject("json.decoder")
            .Add("JSONDecodeError", PyExceptionType.JsonDecodeError)
            .Add("JSONDecoder", decoderType));

        module.Add("encoder", new PyModuleObject("json.encoder")
            .Add("JSONEncoder", encoderType)
            .Add("encode_basestring_ascii", new PyBuiltinFunction("encode_basestring_ascii", arguments =>
                new PyStr(Quote(Text(arguments, "encode_basestring_ascii"), ensureAscii: true))))
            .Add("encode_basestring", new PyBuiltinFunction("encode_basestring", arguments =>
                new PyStr(Quote(Text(arguments, "encode_basestring"), ensureAscii: false)))));

        return module;
    }

    private static string Text(PyObject[] arguments, string function)
    {
        Arity.Exact(function, arguments, 1);

        return arguments[0] is PyStr text
            ? text.Value
            : throw new PyRaise(PyErrors.TypeError(
                $"first argument must be a string, not {arguments[0].TypeName}"));
    }

    // ---- argument shapes ----

    /// <summary>Requires exactly <paramref name="count"/> positional arguments, worded as CPython words it.</summary>
    private static void Positional(string function, string name, PyObject[] arguments, PyDict? keywords, int count)
    {
        if (arguments.Length == count)
        {
            return;
        }

        throw new PyRaise(PyErrors.TypeError(arguments.Length < count
            ? $"{function}() missing {count - arguments.Length} required positional argument{(count - arguments.Length == 1 ? string.Empty : "s")}: '{name}'"
            : ArityMessage(function, count, arguments.Length, keywords?.Count ?? 0)));
    }

    /// <summary>Words the arity error a function with a keyword-only tail reports.</summary>
    private static string ArityMessage(string function, int expected, int positional, int keywords) =>
        keywords == 0
            ? $"{function}() takes {expected} positional argument{(expected == 1 ? string.Empty : "s")} but {positional} {(positional == 1 ? "was" : "were")} given"
            : $"{function}() takes {expected} positional argument{(expected == 1 ? string.Empty : "s")} but {positional} positional arguments "
              + $"(and {keywords} keyword-only argument{(keywords == 1 ? string.Empty : "s")}) were given";

    private static void RejectPositional(string function, PyObject[] arguments)
    {
        if (arguments.Length > 0)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"{function}() takes 1 positional argument but {arguments.Length + 1} were given"));
        }
    }

    /// <summary>A keyword's value, null when absent, and <c>None</c> as itself.</summary>
    private static PyObject? Raw(PyDict? keywords, string name) =>
        keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

    /// <summary>A keyword's value, null when absent or <c>None</c>.</summary>
    private static PyObject? Keyword(PyDict? keywords, string name) =>
        Raw(keywords, name) is { } value and not PyNone ? value : null;

    private static void RejectUnknown(PyDict? keywords, string[] known, string owner, bool allowCls)
    {
        foreach (var (key, _) in keywords?.Entries ?? [])
        {
            var name = key.Display();

            if (!known.Contains(name, StringComparer.Ordinal) && !(allowCls && name == "cls"))
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"{owner}.__init__() got an unexpected keyword argument '{name}'"));
            }
        }
    }

    /// <summary>Whether <c>cls=</c> names something other than the built-in class.</summary>
    private static bool IsCustom(PyDict? keywords, PyType builtIn, out PyObject custom)
    {
        custom = Keyword(keywords, "cls")!;
        return custom is not null && !ReferenceEquals(custom, builtIn);
    }

    // ---- entry points ----

    private static string Dumps(VirtualMachine machine, PyType encoderType, PyObject value, PyDict? keywords)
    {
        if (IsCustom(keywords, encoderType, out var cls))
        {
            // The class is built the way CPython builds it: every encoder option by name,
            // then whatever else the call carried, so a class with its own options gets them.
            var forwarded = new PyDict();
            var defaults = new Dictionary<string, PyObject>
            {
                ["skipkeys"] = PyBool.False,
                ["ensure_ascii"] = PyBool.True,
                ["check_circular"] = PyBool.True,
                ["allow_nan"] = PyBool.True,
                ["indent"] = PyNone.Instance,
                ["separators"] = PyNone.Instance,
                ["default"] = PyNone.Instance,
                ["sort_keys"] = PyBool.False,
            };

            foreach (var name in EncoderKeywords)
            {
                forwarded.Set(new PyStr(name), Raw(keywords, name) ?? defaults[name]);
            }

            foreach (var (key, argument) in keywords!.Entries)
            {
                if (key.Display() != "cls" && !EncoderKeywords.Contains(key.Display(), StringComparer.Ordinal))
                {
                    forwarded.Set(key, argument);
                }
            }

            var instance = machine.Call(cls, [], forwarded);
            var encode = Attributes.TryGet(machine, instance, "encode")
                ?? throw new PyRaise(PyErrors.AttributeError(instance.TypeName, "encode"));

            return machine.Call(encode, [value]) is PyStr text
                ? text.Value
                : throw new PyRaise(PyErrors.TypeError("encode() must return a str"));
        }

        var builder = new StringBuilder();
        new Encoder(machine, builder, EncoderOptions.Read(keywords, allowCls: true)).Write(value, 0, []);
        return builder.ToString();
    }

    private static PyObject Loads(VirtualMachine machine, PyType decoderType, PyObject source, PyDict? keywords)
    {
        if (IsCustom(keywords, decoderType, out var cls))
        {
            var forwarded = new PyDict();

            foreach (var (key, argument) in keywords!.Entries)
            {
                if (key.Display() != "cls" && argument is not PyNone)
                {
                    forwarded.Set(key, argument);
                }
            }

            var instance = machine.Call(cls, [], forwarded);
            var decode = Attributes.TryGet(machine, instance, "decode")
                ?? throw new PyRaise(PyErrors.AttributeError(instance.TypeName, "decode"));

            return machine.Call(decode, [source]);
        }

        return new Reader(machine, Decoded(source), DecoderOptions.Read(keywords, allowCls: true)).Decode();
    }

    /// <summary>Turns the argument <c>loads</c> was given into text.</summary>
    private static string Decoded(PyObject source)
    {
        switch (source)
        {
            case PyStr text:
                // The BOM is refused rather than skipped: it is the one thing a file read
                // with the wrong encoding reliably starts with.
                if (text.Value.StartsWith('﻿'))
                {
                    throw new PyRaise(DecodeError("Unexpected UTF-8 BOM (decode using utf-8-sig)", text.Value, 0));
                }

                return text.Value;

            case PyBytes bytes:
                return Codecs.Decode(bytes.Value, DetectEncoding(bytes.Value), "strict");

            default:
                throw new PyRaise(PyErrors.TypeError(
                    $"the JSON object must be str, bytes or bytearray, not {source.TypeName}"));
        }
    }

    /// <summary>Guesses the encoding of JSON bytes the way <c>json.detect_encoding</c> does.</summary>
    private static string DetectEncoding(byte[] b)
    {
        bool Starts(params byte[] prefix) => b.AsSpan().StartsWith(prefix);

        if (Starts(0xff, 0xfe, 0, 0) || Starts(0, 0, 0xfe, 0xff))
        {
            return "utf-32";
        }

        if (Starts(0xfe, 0xff) || Starts(0xff, 0xfe))
        {
            return "utf-16";
        }

        if (Starts(0xef, 0xbb, 0xbf))
        {
            return "utf-8-sig";
        }

        if (b.Length >= 4)
        {
            if (b[0] == 0)
            {
                return b[1] != 0 ? "utf-16-be" : "utf-32-be";
            }

            if (b[1] == 0)
            {
                return b[2] != 0 || b[3] != 0 ? "utf-16-le" : "utf-32-le";
            }
        }
        else if (b.Length == 2)
        {
            if (b[0] == 0)
            {
                return "utf-16-be";
            }

            if (b[1] == 0)
            {
                return "utf-16-le";
            }
        }

        return "utf-8";
    }

    // ---- the classes ----

    /// <summary>A <c>JSONEncoder</c> instance.</summary>
    private sealed class JsonEncoderObject(VirtualMachine machine, EncoderOptions options, PyDict? keywords) : PyObject
    {
        public override string TypeName => "JSONEncoder";

        public override string Repr() => "<json.encoder.JSONEncoder object>";

        public override System.Numerics.BigInteger PyHash() =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        public override PyObject? GetAttribute(string name) => name switch
        {
            "skipkeys" => PyBool.Of(options.SkipKeys),
            "ensure_ascii" => PyBool.Of(options.EnsureAscii),
            "check_circular" => PyBool.Of(options.CheckCircular),
            "allow_nan" => PyBool.Of(options.AllowNan),
            "sort_keys" => PyBool.Of(options.SortKeys),
            "indent" => Raw(keywords, "indent") ?? PyNone.Instance,
            "item_separator" => new PyStr(options.ItemSeparator),
            "key_separator" => new PyStr(options.KeySeparator),

            // An instance's own `default` shadows the method, as `self.default = default`
            // does upstream.
            "default" => options.Default ?? new PyBuiltinFunction("default", static arguments =>
                throw new PyRaise(PyErrors.TypeError(
                    $"Object of type {(arguments.Length > 0 ? arguments[0].TypeName : "NoneType")} is not JSON serializable"))),

            "encode" => new PyBuiltinFunction("encode", arguments =>
            {
                Arity.Exact("encode", arguments, 1);
                var builder = new StringBuilder();
                new Encoder(machine, builder, options).Write(arguments[0], 0, []);
                return new PyStr(builder.ToString());
            }),

            // The whole document comes back as one chunk. The chunks of a streaming encoder
            // are an implementation detail nothing is entitled to depend on; joining them
            // is what every caller does.
            "iterencode" => new PyBuiltinFunction("iterencode", (arguments, _) =>
            {
                Arity.AtLeast("iterencode", arguments, 1);
                var builder = new StringBuilder();
                new Encoder(machine, builder, options).Write(arguments[0], 0, []);
                return new PyIterator([new PyStr(builder.ToString())], "generator");
            }),

            _ => null,
        };
    }

    /// <summary>A <c>JSONDecoder</c> instance.</summary>
    private sealed class JsonDecoderObject(VirtualMachine machine, DecoderOptions options) : PyObject
    {
        public override string TypeName => "JSONDecoder";

        public override string Repr() => "<json.decoder.JSONDecoder object>";

        public override System.Numerics.BigInteger PyHash() =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        public override PyObject? GetAttribute(string name) => name switch
        {
            "strict" => PyBool.Of(options.Strict),
            "object_hook" => options.ObjectHook ?? PyNone.Instance,
            "object_pairs_hook" => options.PairsHook ?? PyNone.Instance,
            "parse_float" => options.ParseFloat ?? PyNone.Instance,
            "parse_int" => options.ParseInt ?? PyNone.Instance,
            "parse_constant" => options.ParseConstant ?? PyNone.Instance,

            "decode" => new PyBuiltinFunction("decode", arguments =>
            {
                Arity.Exact("decode", arguments, 1);
                return new Reader(machine, Decoded(arguments[0]), options).Decode();
            }),

            "raw_decode" => new PyBuiltinFunction("raw_decode", arguments =>
            {
                Arity.Between("raw_decode", arguments, 1, 2);

                var text = arguments[0] is PyStr s
                    ? s.Value
                    : throw new PyRaise(PyErrors.TypeError(
                        $"first argument must be a string, not {arguments[0].TypeName}"));

                var start = arguments.Length > 1 && arguments[1] is PyInt index ? index.ToIndex() : 0;
                var reader = new Reader(machine, text, options);
                var value = reader.RawDecode(start);

                return new PyTuple([value, PyInt.From(reader.Position)]);
            }),

            _ => null,
        };
    }

    // ---- writing ----

    /// <summary>The encoder settings <c>dumps</c> or <c>JSONEncoder</c> was called with.</summary>
    private sealed record EncoderOptions
    {
        public string? Indent { get; private init; }

        public string ItemSeparator { get; private init; } = ", ";

        public string KeySeparator { get; private init; } = ": ";

        public bool SortKeys { get; private init; }

        public bool SkipKeys { get; private init; }

        public bool EnsureAscii { get; private init; } = true;

        public bool AllowNan { get; private init; } = true;

        public bool CheckCircular { get; private init; } = true;

        public PyObject? Default { get; private init; }

        public static EncoderOptions Read(PyDict? keywords, bool allowCls)
        {
            RejectUnknown(keywords, EncoderKeywords, "JSONEncoder", allowCls);

            var indent = ReadIndent(Keyword(keywords, "indent"));

            // Indenting changes the default item separator: a trailing space before a
            // newline would be invisible trailing whitespace.
            var item = indent is null ? ", " : ",";
            var keySeparator = ": ";

            if (Keyword(keywords, "separators") is { } separators)
            {
                (item, keySeparator) = ReadSeparators(separators);
            }

            return new EncoderOptions
            {
                Indent = indent,
                ItemSeparator = item,
                KeySeparator = keySeparator,
                SortKeys = Flag(keywords, "sort_keys", false),
                SkipKeys = Flag(keywords, "skipkeys", false),
                EnsureAscii = Flag(keywords, "ensure_ascii", true),
                AllowNan = Flag(keywords, "allow_nan", true),
                CheckCircular = Flag(keywords, "check_circular", true),
                Default = Keyword(keywords, "default"),
            };
        }

        // An explicit `None` is falsy, not "use the default" — `ensure_ascii=None` is `False`.
        private static bool Flag(PyDict? keywords, string name, bool fallback) =>
            Raw(keywords, name) is { } value ? value.IsTruthy() : fallback;

        /// <summary>
        /// Reads the <c>indent</c> argument, which may be a count, a string, or a boolean.
        /// </summary>
        /// <remarks>
        /// A negative count means no indentation but still one item per line, which is why
        /// this returns an empty string rather than null for it.
        /// </remarks>
        private static string? ReadIndent(PyObject? value) => value switch
        {
            null => null,
            PyStr text => text.Value,
            PyBool flag => flag.Value ? " " : string.Empty,
            PyInt count => new string(' ', Math.Max(0, count.ToIndex())),
            _ => throw new PyRaise(PyErrors.TypeError(
                $"can't multiply sequence by non-int of type '{value.TypeName}'")),
        };

        private static (string Item, string Key) ReadSeparators(PyObject value)
        {
            var items = value switch
            {
                PyTuple tuple => tuple.Items,
                PyList list => list.Items,
                _ => throw new PyRaise(PyErrors.TypeError(
                    $"cannot unpack non-iterable {value.TypeName} object")),
            };

            if (items.Count > 2)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"too many values to unpack (expected 2, got {items.Count})"));
            }

            if (items.Count < 2)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"not enough values to unpack (expected 2, got {items.Count})"));
            }

            // The encoder takes them the other way round, and names them by position.
            if (items[0] is not PyStr item)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"make_encoder() argument 6 must be str, not {items[0].TypeName}"));
            }

            if (items[1] is not PyStr key)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"make_encoder() argument 5 must be str, not {items[1].TypeName}"));
            }

            return (item.Value, key.Value);
        }
    }

    /// <summary>Serializes one value tree.</summary>
    private sealed class Encoder(VirtualMachine machine, StringBuilder builder, EncoderOptions options)
    {
        private readonly int _maxDepth = machine.RecursionLimit;

        // How many `default` calls enclose the value being written. They nest without
        // indenting, so `depth` cannot count them — and a `default` that keeps returning a
        // fresh object would otherwise never run out of stack to be refused.
        private int _fallbacks;

        public void Write(PyObject value, int depth, List<PyObject> stack)
        {
            if (depth + _fallbacks > _maxDepth)
            {
                throw new PyRaise(new PyException(PyExceptionType.RecursionError,
                    "maximum recursion depth exceeded while encoding a JSON object"));
            }

            switch (value)
            {
                case PyNone:
                    builder.Append("null");
                    return;

                case PyBool flag:
                    builder.Append(flag.Value ? "true" : "false");
                    return;

                case PyInt integer:
                    builder.Append(Digits(integer.Value));
                    return;

                case PyFloat number:
                    builder.Append(Number(number.Value));
                    return;

                case PyStr text:
                    builder.Append(Quote(text.Value, options.EnsureAscii));
                    return;

                case PyList list:
                    WriteSequence(value, list.Items, depth, stack);
                    return;

                case PyTuple tuple:
                    WriteSequence(value, tuple.Items, depth, stack);
                    return;

                case PyDict dict:
                    WriteMapping(dict, depth, stack);
                    return;

                default:
                    WriteFallback(value, depth, stack);
                    return;
            }
        }

        /// <summary>Hands a value of no JSON type to <c>default</c>, and writes what it returns.</summary>
        private void WriteFallback(PyObject value, int depth, List<PyObject> stack)
        {
            if (options.Default is not { } fallback)
            {
                throw new PyRaise(PyErrors.TypeError(
                    $"Object of type {value.TypeName} is not JSON serializable"));
            }

            Enter(value, stack);
            _fallbacks++;

            try
            {
                Write(machine.Call(fallback, [value]), depth, stack);
            }
            finally
            {
                _fallbacks--;
                Leave(stack);
            }
        }

        private void WriteSequence(PyObject owner, IReadOnlyList<PyObject> items, int depth, List<PyObject> stack)
        {
            if (items.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            Enter(owner, stack);

            try
            {
                builder.Append('[');

                // A snapshot of the length only: a `default` that appends to the list being
                // written must not turn the loop into an unbounded one.
                var count = items.Count;

                for (var i = 0; i < count && i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(options.ItemSeparator);
                    }

                    Break(depth + 1);
                    Write(items[i], depth + 1, stack);
                }

                Break(depth);
                builder.Append(']');
            }
            finally
            {
                Leave(stack);
            }
        }

        private void WriteMapping(PyDict dict, int depth, List<PyObject> stack)
        {
            if (dict.Count == 0)
            {
                builder.Append("{}");
                return;
            }

            var entries = new List<KeyValuePair<PyObject, PyObject>>(dict.Entries);

            // Sorted before any key is skipped or coerced, as the encoder does it, so a
            // `skipkeys` run still fails on keys that cannot be ordered.
            if (options.SortKeys)
            {
                Sort(entries);
            }

            Enter(dict, stack);

            try
            {
                builder.Append('{');
                Break(depth + 1);

                var first = true;

                foreach (var (key, value) in entries)
                {
                    if (KeyText(key) is not { } text)
                    {
                        continue;
                    }

                    if (!first)
                    {
                        builder.Append(options.ItemSeparator);
                        Break(depth + 1);
                    }

                    first = false;
                    builder.Append(Quote(text, options.EnsureAscii));
                    builder.Append(options.KeySeparator);
                    Write(value, depth + 1, stack);
                }

                Break(depth);
                builder.Append('}');
            }
            finally
            {
                Leave(stack);
            }
        }

        /// <summary>
        /// Sorts the entries by their original keys.
        /// </summary>
        /// <remarks>
        /// Keys are ordered as the Python values they were, not as the strings they become,
        /// so sorting <c>{1: ..., 'b': ...}</c> fails exactly as <c>1 &lt; 'b'</c> does. The
        /// sort is written out rather than handed to <c>List.Sort</c>, which would wrap the
        /// raised <c>TypeError</c> in a host exception.
        /// </remarks>
        private static void Sort(List<KeyValuePair<PyObject, PyObject>> entries)
        {
            for (var i = 1; i < entries.Count; i++)
            {
                var entry = entries[i];
                var j = i - 1;

                // Compared this way round, an incomparable pair is named in the order
                // CPython's sort names it.
                while (j >= 0 && Compare(entry.Key, entries[j].Key) < 0)
                {
                    entries[j + 1] = entries[j];
                    j--;
                }

                entries[j + 1] = entry;
            }
        }

        private static int Compare(PyObject left, PyObject right) =>
            left.PyCompare(right) ?? throw new PyRaise(PyErrors.TypeError(
                $"'<' not supported between instances of '{left.TypeName}' and '{right.TypeName}'"));

        /// <summary>Pushes a container, refusing one that already encloses itself.</summary>
        private void Enter(PyObject container, List<PyObject> stack)
        {
            if (!options.CheckCircular)
            {
                return;
            }

            if (stack.Any(entry => ReferenceEquals(entry, container)))
            {
                throw new PyRaise(PyErrors.ValueError("Circular reference detected"));
            }

            stack.Add(container);
        }

        private void Leave(List<PyObject> stack)
        {
            if (options.CheckCircular)
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        /// <summary>
        /// The JSON text of a dictionary key, or null when the key is skipped.
        /// </summary>
        /// <remarks>
        /// JSON keys are strings, so Python coerces the few key types that have an obvious
        /// spelling and rejects the rest — unless <c>skipkeys</c> asks for them to be
        /// dropped instead.
        /// </remarks>
        private string? KeyText(PyObject key)
        {
            switch (key)
            {
                case PyStr text: return text.Value;
                case PyBool flag: return flag.Value ? "true" : "false";
                case PyInt integer: return Digits(integer.Value);
                case PyFloat number: return Number(number.Value);
                case PyNone: return "null";
            }

            return options.SkipKeys
                ? null
                : throw new PyRaise(PyErrors.TypeError(
                    $"keys must be str, int, float, bool or None, not {key.TypeName}"));
        }

        private string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                if (!options.AllowNan)
                {
                    throw new PyRaise(PyErrors.ValueError(
                        $"Out of range float values are not JSON compliant: {PyFloat.Format(value)}"));
                }

                return double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity" : "-Infinity";
            }

            return PyFloat.Format(value);
        }

        /// <summary>Writes the break between items, when indenting.</summary>
        private void Break(int depth)
        {
            if (options.Indent is { } indent)
            {
                builder.Append('\n');

                for (var i = 0; i < depth; i++)
                {
                    builder.Append(indent);
                }
            }
        }
    }

    /// <summary>Quotes and escapes a string the way the encoder does.</summary>
    private static string Quote(string value, bool ensureAscii)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    // Surrogates are escaped one half at a time, which is what produces
                    // the `😀` pair CPython emits for an astral character.
                    if (c < 0x20 || (ensureAscii && c > 0x7e))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>Renders an integer, refusing one wider than the decimal conversion limit.</summary>
    private static string Digits(BigInteger value) => PyInt.Decimal(value);

    // ---- reading ----

    /// <summary>Builds a <c>JSONDecodeError</c> locating <paramref name="at"/> by line and column.</summary>
    private static PyException DecodeError(string message, string document, int at)
    {
        var line = 1;
        var lastBreak = -1;

        for (var i = 0; i < at && i < document.Length; i++)
        {
            if (document[i] == '\n')
            {
                line++;
                lastBreak = i;
            }
        }

        var column = at - lastBreak;

        return new PyException(
            PyExceptionType.JsonDecodeError,
            $"{message}: line {line} column {column} (char {at})")
        {
            Fields = new Dictionary<string, PyObject>(StringComparer.Ordinal)
            {
                ["msg"] = new PyStr(message),
                ["doc"] = new PyStr(document),
                ["pos"] = PyInt.From(at),
                ["lineno"] = PyInt.From(line),
                ["colno"] = PyInt.From(column),
            },
        };
    }

    /// <summary>The decoder settings <c>loads</c> or <c>JSONDecoder</c> was called with.</summary>
    private sealed record DecoderOptions
    {
        public bool Strict { get; private init; } = true;

        public PyObject? ObjectHook { get; private init; }

        public PyObject? PairsHook { get; private init; }

        public PyObject? ParseFloat { get; private init; }

        public PyObject? ParseInt { get; private init; }

        public PyObject? ParseConstant { get; private init; }

        public static DecoderOptions Read(PyDict? keywords, bool allowCls)
        {
            RejectUnknown(keywords, DecoderKeywords, "JSONDecoder", allowCls);

            return new DecoderOptions
            {
                Strict = Raw(keywords, "strict") is { } strict ? strict.IsTruthy() : true,
                ObjectHook = Keyword(keywords, "object_hook"),
                PairsHook = Keyword(keywords, "object_pairs_hook"),
                ParseFloat = Keyword(keywords, "parse_float"),
                ParseInt = Keyword(keywords, "parse_int"),
                ParseConstant = Keyword(keywords, "parse_constant"),
            };
        }
    }

    /// <summary>A decoder that reports positions the way CPython's does.</summary>
    private sealed class Reader(VirtualMachine machine, string text, DecoderOptions options)
    {
        private static readonly (string Literal, int Kind)[] Literals =
        [
            ("true", 0),
            ("false", 1),
            ("null", 2),
            ("NaN", 3),
            ("Infinity", 4),
            ("-Infinity", 5),
        ];

        private int _depth;

        public int Position { get; private set; }

        private bool AtEnd => Position >= text.Length;

        private char Current => text[Position];

        /// <summary>Decodes a whole document: one value, and only whitespace after it.</summary>
        public PyObject Decode()
        {
            SkipWhitespace();
            var value = ReadValue();
            SkipWhitespace();

            return AtEnd ? value : throw Fail("Extra data", Position);
        }

        /// <summary>Decodes the value starting at <paramref name="index"/>, wherever it ends.</summary>
        public PyObject RawDecode(int index)
        {
            Position = index;
            return ReadValue();
        }

        private PyObject ReadValue()
        {
            if (AtEnd)
            {
                throw Fail("Expecting value", Position);
            }

            switch (Current)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return new PyStr(ReadString());
            }

            foreach (var (literal, kind) in Literals)
            {
                if (Position + literal.Length <= text.Length
                    && text.AsSpan(Position, literal.Length).SequenceEqual(literal))
                {
                    Position += literal.Length;
                    return Literal(literal, kind);
                }
            }

            return ReadNumber();
        }

        private PyObject Literal(string literal, int kind)
        {
            if (kind >= 3 && options.ParseConstant is { } hook)
            {
                return machine.Call(hook, [new PyStr(literal)]);
            }

            return kind switch
            {
                0 => PyBool.True,
                1 => PyBool.False,
                2 => PyNone.Instance,
                3 => new PyFloat(double.NaN),
                4 => new PyFloat(double.PositiveInfinity),
                _ => new PyFloat(double.NegativeInfinity),
            };
        }

        /// <summary>Counts one more level of nesting, refusing one the stack cannot hold.</summary>
        private void Descend(string kind)
        {
            if (++_depth > machine.RecursionLimit)
            {
                throw new PyRaise(new PyException(
                    PyExceptionType.RecursionError,
                    $"maximum recursion depth exceeded while decoding a JSON {kind} from a unicode string"));
            }
        }

        private PyObject ReadObject()
        {
            Descend("object");
            Position++;
            SkipWhitespace();

            var pairs = new List<KeyValuePair<PyObject, PyObject>>();

            if (!AtEnd && Current == '}')
            {
                Position++;
                return Finish(pairs);
            }

            if (AtEnd || Current != '"')
            {
                throw Fail("Expecting property name enclosed in double quotes", Position);
            }

            while (true)
            {
                var key = new PyStr(ReadString());
                SkipWhitespace();

                if (AtEnd || Current != ':')
                {
                    throw Fail("Expecting ':' delimiter", Position);
                }

                Position++;
                SkipWhitespace();
                pairs.Add(new KeyValuePair<PyObject, PyObject>(key, ReadValue()));
                SkipWhitespace();

                if (AtEnd)
                {
                    throw Fail("Expecting ',' delimiter", Position);
                }

                var next = Current;
                Position++;

                if (next == '}')
                {
                    return Finish(pairs);
                }

                if (next != ',')
                {
                    throw Fail("Expecting ',' delimiter", Position - 1);
                }

                var comma = Position - 1;
                SkipWhitespace();

                if (AtEnd || Current != '"')
                {
                    throw !AtEnd && Current == '}'
                        ? Fail("Illegal trailing comma before end of object", comma)
                        : Fail("Expecting property name enclosed in double quotes", Position);
                }
            }
        }

        /// <summary>Builds the value an object decodes to, once all its pairs are read.</summary>
        private PyObject Finish(List<KeyValuePair<PyObject, PyObject>> pairs)
        {
            _depth--;

            // The pairs hook outranks the object hook, and sees the pairs in source order
            // with duplicates intact — the reason it exists.
            if (options.PairsHook is { } pairsHook)
            {
                return machine.Call(pairsHook, [new PyList([.. pairs.Select(static p => (PyObject)new PyTuple([p.Key, p.Value]))])]);
            }

            var dict = new PyDict();

            foreach (var (key, value) in pairs)
            {
                dict.Set(key, value);
            }

            return options.ObjectHook is { } hook ? machine.Call(hook, [dict]) : dict;
        }

        private PyObject ReadArray()
        {
            Descend("array");
            Position++;
            SkipWhitespace();

            var items = new List<PyObject>();

            if (!AtEnd && Current == ']')
            {
                Position++;
                _depth--;
                return new PyList(items);
            }

            while (true)
            {
                items.Add(ReadValue());
                SkipWhitespace();

                if (AtEnd)
                {
                    throw Fail("Expecting ',' delimiter", Position);
                }

                var next = Current;
                Position++;

                if (next == ']')
                {
                    _depth--;
                    return new PyList(items);
                }

                if (next != ',')
                {
                    throw Fail("Expecting ',' delimiter", Position - 1);
                }

                var comma = Position - 1;
                SkipWhitespace();

                if (!AtEnd && Current == ']')
                {
                    throw Fail("Illegal trailing comma before end of array", comma);
                }
            }
        }

        private string ReadString()
        {
            var start = Position;
            Position++;
            var builder = new StringBuilder();

            while (!AtEnd && Current != '"')
            {
                var c = Current;

                if (c != '\\')
                {
                    // Raw control characters are not legal inside a JSON string; `strict=False`
                    // is the switch that tolerates them.
                    if (c < 0x20 && options.Strict)
                    {
                        throw Fail("Invalid control character at", Position);
                    }

                    builder.Append(c);
                    Position++;
                    continue;
                }

                var escape = Position;
                Position++;

                if (AtEnd)
                {
                    break;
                }

                switch (Current)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;

                    case 'u':
                    {
                        var code = 0;

                        if (Position + 4 >= text.Length)
                        {
                            throw Fail("Invalid \\uXXXX escape", escape + 1);
                        }

                        for (var i = 1; i <= 4; i++)
                        {
                            var digit = HexValue(text[Position + i]);

                            if (digit < 0)
                            {
                                throw Fail("Invalid \\uXXXX escape", escape + 1);
                            }

                            code = (code << 4) | digit;
                        }

                        builder.Append((char)code);
                        Position += 4;
                        break;
                    }

                    default:
                        throw Fail("Invalid \\escape", escape);
                }

                Position++;
            }

            if (AtEnd)
            {
                throw Fail("Unterminated string starting at", start);
            }

            Position++;
            return builder.ToString();
        }

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        /// <summary>
        /// Reads a number by the JSON grammar: an optional minus, an integer part with no
        /// leading zero, and optional fraction and exponent parts.
        /// </summary>
        /// <remarks>
        /// A fraction or exponent that is not followed by digits is left unread, so
        /// <c>1.</c> is the integer <c>1</c> followed by extra data — which is the answer
        /// CPython gives.
        /// </remarks>
        private PyObject ReadNumber()
        {
            var start = Position;
            var i = Position;

            if (i < text.Length && text[i] == '-')
            {
                i++;
            }

            if (i >= text.Length || !char.IsAsciiDigit(text[i]))
            {
                throw Fail("Expecting value", start);
            }

            if (text[i] == '0')
            {
                i++;
            }
            else
            {
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }

            var isFloat = false;

            if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
            {
                isFloat = true;
                i += 2;

                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }

            if (i < text.Length && text[i] is 'e' or 'E')
            {
                var j = i + 1;

                if (j < text.Length && text[j] is '+' or '-')
                {
                    j++;
                }

                if (j < text.Length && char.IsAsciiDigit(text[j]))
                {
                    isFloat = true;

                    while (j < text.Length && char.IsAsciiDigit(text[j]))
                    {
                        j++;
                    }

                    i = j;
                }
            }

            Position = i;
            var token = text[start..i];

            if (isFloat)
            {
                return options.ParseFloat is { } floatHook
                    ? machine.Call(floatHook, [new PyStr(token)])
                    : new PyFloat(double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));
            }

            if (options.ParseInt is { } intHook)
            {
                return machine.Call(intHook, [new PyStr(token)]);
            }

            // The digit count is checked before parsing, so a huge literal is rejected
            // without ever allocating the number it names.
            var digits = token.Length - (token[0] == '-' ? 1 : 0);

            if (digits > MaxIntegerDigits)
            {
                throw new PyRaise(PyErrors.ValueError(
                    $"Exceeds the limit ({MaxIntegerDigits} digits) for integer string conversion: "
                    + $"value has {digits} digits"));
            }

            return PyInt.From(BigInteger.Parse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        // JSON whitespace is four characters, not everything `char.IsWhiteSpace` accepts: a
        // no-break space between tokens is an error in a document, not formatting.
        private void SkipWhitespace()
        {
            while (!AtEnd && Current is ' ' or '\t' or '\n' or '\r')
            {
                Position++;
            }
        }

        private PyRaise Fail(string message, int at) => new(DecodeError(message, text, at));
    }
}
