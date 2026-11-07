using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// The <c>decimal</c> module: arbitrary-precision decimal floating point, following the
/// General Decimal Arithmetic specification as CPython 3.13 implements it.
/// </summary>
/// <remarks>
/// <para>
/// A value is a sign, an integer coefficient and a power-of-ten exponent, so
/// <c>Decimal('1.10')</c> keeps its trailing zero and prints as it was written. Every
/// operation is exact and then rounded once to the context's precision, with the context's
/// rounding mode — the algorithms are <c>_pydecimal</c>'s, so the coefficient and exponent
/// of each result are CPython's.
/// </para>
/// <para>
/// The context is per run: <c>getcontext()</c> hands back this run's, and a change to it is
/// one tenant's. Signals always trap — <c>InvalidOperation</c>, <c>DivisionByZero</c> and
/// <c>Overflow</c> raise, as under CPython's default context; there are no flags, and a
/// context that does not trap (<c>ExtendedContext</c>) is not offered.
/// </para>
/// </remarks>
public static class DecimalModule
{
    /// <summary><c>decimal.DecimalException</c>.</summary>
    public static PyExceptionType DecimalException { get; } =
        PyExceptionType.DefineHostException("DecimalException", PyExceptionType.ArithmeticError);

    /// <summary><c>decimal.InvalidOperation</c>.</summary>
    public static PyExceptionType InvalidOperation { get; } =
        PyExceptionType.DefineHostException("InvalidOperation", DecimalException);

    /// <summary><c>decimal.DivisionByZero</c>, also a <c>ZeroDivisionError</c>.</summary>
    public static PyExceptionType DivisionByZero { get; } =
        PyExceptionType.DefineHostException("DivisionByZero", DecimalException, PyExceptionType.ZeroDivisionError);

    /// <summary><c>decimal.Inexact</c>.</summary>
    public static PyExceptionType Inexact { get; } = PyExceptionType.DefineHostException("Inexact", DecimalException);

    /// <summary><c>decimal.Rounded</c>.</summary>
    public static PyExceptionType Rounded { get; } = PyExceptionType.DefineHostException("Rounded", DecimalException);

    /// <summary><c>decimal.Overflow</c>.</summary>
    public static PyExceptionType Overflow { get; } = PyExceptionType.DefineHostException("Overflow", Inexact, Rounded);

    /// <summary><c>decimal.Underflow</c>.</summary>
    public static PyExceptionType Underflow { get; } = PyExceptionType.DefineHostException("Underflow", Inexact, Rounded);

    /// <summary><c>decimal.Clamped</c>.</summary>
    public static PyExceptionType Clamped { get; } = PyExceptionType.DefineHostException("Clamped", DecimalException);

    /// <summary><c>decimal.Subnormal</c>.</summary>
    public static PyExceptionType Subnormal { get; } = PyExceptionType.DefineHostException("Subnormal", DecimalException);

    /// <summary><c>decimal.FloatOperation</c>, also a <c>TypeError</c>.</summary>
    public static PyExceptionType FloatOperation { get; } =
        PyExceptionType.DefineHostException("FloatOperation", DecimalException, PyExceptionType.TypeError);

    /// <summary><c>decimal.DecimalTuple</c>, what <c>as_tuple</c> returns.</summary>
    internal static PyNamedTupleType DecimalTuple { get; } =
        new("DecimalTuple", ["sign", "digits", "exponent"], module: new PyStr("decimal"));

    /// <summary>The names of the rounding modes, which are the modes' values too.</summary>
    internal static readonly string[] RoundingModes =
    [
        "ROUND_DOWN", "ROUND_HALF_UP", "ROUND_HALF_EVEN", "ROUND_CEILING",
        "ROUND_FLOOR", "ROUND_UP", "ROUND_HALF_DOWN", "ROUND_05UP",
    ];

    /// <summary>Raises a decimal signal, worded as CPython's C implementation words it.</summary>
    internal static PyRaise Signal(PyExceptionType type) =>
        new(new PyException(type, $"[<class 'decimal.{type.Name}'>]"));

    /// <summary>Builds the module, with a fresh context for this run.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("decimal");
        var state = new DecimalState();

        // Built per run, because a Decimal carries the run's context with it; each instance
        // names this object as its type (IPyHostTyped), so `type(d) is Decimal` holds.
        var decimalType = new PyType("Decimal", static value => value is PyDecimal, (arguments, keywords) =>
        {
            var value = arguments.Length > 0 ? arguments[0]
                : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "value").Value ?? new PyStr("0");
            var context = arguments.Length > 1 ? arguments[1]
                : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "context").Value;

            if (arguments.Length > 2)
            {
                throw new PyRaise(PyErrors.TypeError("Decimal() takes at most 2 arguments"));
            }

            return Construct(state, value, context as DecimalContext);
        });

        decimalType.Members["from_float"] = new PyBuiltinFunction("from_float", arguments =>
        {
            Arity.ExactCount("from_float", arguments, 1);

            return arguments[0] is PyFloat or PyInt
                ? Construct(state, arguments[0], null)
                : throw new PyRaise(PyErrors.TypeError("argument must be int or float"));
        });

        state.Type = decimalType;

        module.Add("Decimal", decimalType);
        module.Add("Context", new PyBuiltinFunction("Context", (arguments, keywords) =>
        {
            var context = new DecimalContext(state);

            string[] names = ["prec", "rounding", "Emin", "Emax", "capitals", "clamp", "flags", "traps"];

            for (var i = 0; i < arguments.Length && i < names.Length; i++)
            {
                if (arguments[i] is not PyNone)
                {
                    context.SetAttribute(names[i], arguments[i]);
                }
            }

            foreach (var (key, value) in keywords?.Entries ?? [])
            {
                if (value is not PyNone)
                {
                    context.SetAttribute(key.Display(), value);
                }
            }

            return context;
        }));

        module.Add("getcontext", new PyBuiltinFunction("getcontext", _ => state.Current));
        module.Add("setcontext", new PyBuiltinFunction("setcontext", arguments =>
        {
            Arity.ExactCount("setcontext", arguments, 1);
            state.Current = arguments[0] as DecimalContext
                ?? throw new PyRaise(PyErrors.TypeError("argument must be a context"));
            return PyNone.Instance;
        }));

        module.Add("localcontext", new PyBuiltinFunction("localcontext", (arguments, keywords) =>
        {
            var basis = arguments.Length > 0 && arguments[0] is DecimalContext given ? given : state.Current;
            var local = basis.Copy();

            foreach (var (key, value) in keywords?.Entries ?? [])
            {
                if (!local.SetAttribute(key.Display(), value))
                {
                    throw new PyRaise(PyErrors.TypeError($"'{key.Display()}' is an invalid keyword argument for this function"));
                }
            }

            return new LocalContext(state, local);
        }));

        module.Add("DefaultContext", new DecimalContext(state));
        module.Add("MAX_PREC", PyInt.From(999_999_999_999_999_999L));
        module.Add("MAX_EMAX", PyInt.From(999_999_999_999_999_999L));
        module.Add("MIN_EMIN", PyInt.From(-999_999_999_999_999_999L));
        module.Add("MIN_ETINY", PyInt.From(-1_999_999_999_999_999_997L));
        module.Add("HAVE_THREADS", PyBool.True);
        module.Add("HAVE_CONTEXTVAR", PyBool.True);

        foreach (var mode in RoundingModes)
        {
            module.Add(mode, new PyStr(mode));
        }

        module.Add("DecimalException", DecimalException);
        module.Add("InvalidOperation", InvalidOperation);
        module.Add("DivisionByZero", DivisionByZero);
        module.Add("Inexact", Inexact);
        module.Add("Rounded", Rounded);
        module.Add("Overflow", Overflow);
        module.Add("Underflow", Underflow);
        module.Add("Clamped", Clamped);
        module.Add("Subnormal", Subnormal);
        module.Add("FloatOperation", FloatOperation);
        module.Add("DecimalTuple", DecimalTuple);

        return module;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "SYSLIB1045", Justification = "One pattern, built once.")]
    private static readonly Regex Numeric = new(
        @"\A(?<sign>[-+])?(?:(?:(?<int>[0-9](?:_?[0-9])*)?(?:\.(?<frac>[0-9](?:_?[0-9])*)?)?(?:[eE](?<exp>[-+]?[0-9](?:_?[0-9])*))?)|(?<inf>Inf(?:inity)?)|(?<nan>s?NaN)(?<diag>[0-9]*))\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Builds a Decimal from what the constructor was given. Exact: no rounding.</summary>
    internal static PyDecimal Construct(DecimalState state, PyObject value, DecimalContext? context)
    {
        switch (value)
        {
            case PyDecimal number:
                return number;

            case PyBool flag:
                return PyDecimal.Finite(state, false, flag.Value ? BigInteger.One : BigInteger.Zero, 0);

            case PyInt integer:
                return PyDecimal.Finite(state, integer.Value.Sign < 0, BigInteger.Abs(integer.Value), 0);

            case PyFloat number:
                return FromDouble(state, number.Value);

            case PyStr text:
                return Parse(state, text.Value, context);

            case PyTuple tuple:
                return FromTuple(state, tuple);

            case PyList list:
                return FromTuple(state, new PyTuple(list.Items));

            default:
                throw new PyRaise(PyErrors.TypeError($"conversion from {value.TypeName} to Decimal is not supported"));
        }
    }

    /// <summary>The exact value of a double, which is how CPython converts a float.</summary>
    internal static PyDecimal FromDouble(DecimalState state, double value)
    {
        if (double.IsNaN(value))
        {
            return PyDecimal.Special(state, DecimalKind.NaN, false, BigInteger.Zero);
        }

        if (double.IsInfinity(value))
        {
            return PyDecimal.Special(state, DecimalKind.Infinity, value < 0, BigInteger.Zero);
        }

        var negative = double.IsNegative(value);
        var (numerator, denominator) = FractionsModule.Ratio(Math.Abs(value));
        var common = BigInteger.GreatestCommonDivisor(numerator, denominator);
        (numerator, denominator) = common.IsZero ? (numerator, denominator) : (numerator / common, denominator / common);

        // The denominator is a power of two, 2**k, and n / 2**k is n * 5**k / 10**k exactly.
        var k = (int)(denominator.GetBitLength() - 1);
        return PyDecimal.Finite(state, negative, numerator * BigInteger.Pow(5, k), -k);
    }

    private static PyDecimal FromTuple(DecimalState state, PyTuple tuple)
    {
        if (tuple.Items.Count != 3)
        {
            throw new PyRaise(PyErrors.ValueError("argument must be a sequence of length 3"));
        }

        if (tuple.Items[0] is not PyInt { Value: var sign } || sign < 0 || sign > 1)
        {
            throw new PyRaise(PyErrors.ValueError("sign must be an integer with the value 0 or 1"));
        }

        var digits = new StringBuilder();

        foreach (var digit in VirtualMachine.RequireIterable(tuple.Items[1]))
        {
            if (digit is not PyInt { Value: var d } || d < 0 || d > 9)
            {
                throw new PyRaise(PyErrors.ValueError("coefficient must be a tuple of digits"));
            }

            digits.Append((char)('0' + (int)d));
        }

        var coefficient = digits.Length == 0 ? BigInteger.Zero : BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);

        return tuple.Items[2] switch
        {
            PyInt exponent => PyDecimal.Finite(state, sign == 1, coefficient, (long)BigInteger.Clamp(exponent.Value, long.MinValue / 4, long.MaxValue / 4)),
            PyStr { Value: "F" } => PyDecimal.Special(state, DecimalKind.Infinity, sign == 1, BigInteger.Zero),
            PyStr { Value: "n" } => PyDecimal.Special(state, DecimalKind.NaN, sign == 1, coefficient),
            PyStr { Value: "N" } => PyDecimal.Special(state, DecimalKind.SignalingNaN, sign == 1, coefficient),
            _ => throw new PyRaise(PyErrors.ValueError("exponent must be an integer")),
        };
    }

    private static PyDecimal Parse(DecimalState state, string text, DecimalContext? context)
    {
        var match = Numeric.Match(text.Trim());

        if (!match.Success || (!match.Groups["int"].Success && !match.Groups["frac"].Success
            && !match.Groups["inf"].Success && !match.Groups["nan"].Success))
        {
            throw new PyRaise(new PyException(InvalidOperation, "[<class 'decimal.ConversionSyntax'>]"));
        }

        var negative = match.Groups["sign"].Value == "-";

        if (match.Groups["inf"].Success)
        {
            return PyDecimal.Special(state, DecimalKind.Infinity, negative, BigInteger.Zero);
        }

        if (match.Groups["nan"].Success)
        {
            var diagnostic = match.Groups["diag"].Value.TrimStart('0');
            var payload = diagnostic.Length == 0 ? BigInteger.Zero : BigInteger.Parse(diagnostic, CultureInfo.InvariantCulture);
            var kind = match.Groups["nan"].Value.StartsWith('s') || match.Groups["nan"].Value.StartsWith('S')
                ? DecimalKind.SignalingNaN
                : DecimalKind.NaN;
            return PyDecimal.Special(state, kind, negative, payload);
        }

        var integerPart = match.Groups["int"].Value.Replace("_", string.Empty, StringComparison.Ordinal);
        var fractionPart = match.Groups["frac"].Value.Replace("_", string.Empty, StringComparison.Ordinal);
        var exponentText = match.Groups["exp"].Value.Replace("_", string.Empty, StringComparison.Ordinal);
        var exponent = exponentText.Length == 0 ? BigInteger.Zero : BigInteger.Parse(exponentText, CultureInfo.InvariantCulture);
        exponent -= fractionPart.Length;

        // An exponent past what a context could ever hold has no meaning here; refusing it
        // keeps a later rescale from being asked to build a number of that many digits.
        if (BigInteger.Abs(exponent) > 999_999_999_999_999_999L)
        {
            throw Signal(InvalidOperation);
        }

        var digits = (integerPart + fractionPart).TrimStart('0');
        var coefficient = digits.Length == 0 ? BigInteger.Zero : BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        _ = context;
        return PyDecimal.Finite(state, negative, coefficient, (long)exponent);
    }

    /// <summary><c>with localcontext() as ctx:</c> — swaps the run's context in and back out.</summary>
    private sealed class LocalContext(DecimalState state, DecimalContext local) : PyObject
    {
        private DecimalContext? _saved;

        public override string TypeName => "decimal.ContextManager";

        public override string Repr() => "<decimal.ContextManager object>";

        public override PyObject? GetAttribute(string name) => name switch
        {
            "__enter__" => new PyBuiltinFunction("__enter__", _ =>
            {
                _saved = state.Current;
                state.Current = local;
                return local;
            }),
            "__exit__" => new PyBuiltinFunction("__exit__", _ =>
            {
                state.Current = _saved ?? state.Current;
                return PyBool.False;
            }),
            _ => null,
        };
    }
}

/// <summary>The per-run state of the <c>decimal</c> module: its current context.</summary>
internal sealed class DecimalState
{
    public DecimalState() => Current = new DecimalContext(this);

    /// <summary>What <c>getcontext()</c> returns.</summary>
    public DecimalContext Current { get; set; }

    /// <summary>The run's <c>Decimal</c> class object.</summary>
    public PyType? Type { get; set; }
}

/// <summary>A <c>decimal.Context</c>: precision, rounding and exponent limits.</summary>
public sealed class DecimalContext : PyObject
{
    internal DecimalContext(DecimalState state) => State = state;

    internal DecimalState State { get; }

    /// <summary>Significant digits a result is rounded to.</summary>
    public int Precision { get; private set; } = 28;

    /// <summary>The rounding mode's name.</summary>
    public string Rounding { get; private set; } = "ROUND_HALF_EVEN";

    /// <summary>The largest adjusted exponent.</summary>
    public long Emax { get; private set; } = 999_999;

    /// <summary>The smallest adjusted exponent of a normal number.</summary>
    public long Emin { get; private set; } = -999_999;

    /// <summary>Whether an exponent is written with a capital E.</summary>
    public bool Capitals { get; private set; } = true;

    /// <summary>The clamp flag.</summary>
    public int Clamp { get; private set; }

    /// <summary>The smallest exponent a subnormal may have.</summary>
    public long Etiny => Emin - Precision + 1;

    /// <summary>The largest exponent a clamped result may have.</summary>
    public long Etop => Emax - Precision + 1;

    /// <inheritdoc />
    public override string TypeName => "decimal.Context";

    /// <inheritdoc />
    public override string Repr() =>
        $"Context(prec={Precision}, rounding={Rounding}, Emin={Emin}, Emax={Emax}, capitals={(Capitals ? 1 : 0)}, clamp={Clamp}, "
        + "flags=[], traps=[InvalidOperation, DivisionByZero, Overflow])";

    /// <summary>A copy with the same settings.</summary>
    internal DecimalContext Copy() => new(State)
    {
        Precision = Precision,
        Rounding = Rounding,
        Emax = Emax,
        Emin = Emin,
        Capitals = Capitals,
        Clamp = Clamp,
    };

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "prec" => PyInt.From(Precision),
        "rounding" => new PyStr(Rounding),
        "Emax" => PyInt.From(Emax),
        "Emin" => PyInt.From(Emin),
        "capitals" => PyInt.From(Capitals ? 1 : 0),
        "clamp" => PyInt.From(Clamp),
        "Etiny" => new PyBuiltinFunction("Etiny", _ => PyInt.From(Etiny)),
        "Etop" => new PyBuiltinFunction("Etop", _ => PyInt.From(Etop)),
        "copy" => new PyBuiltinFunction("copy", _ => Copy()),
        "create_decimal" => new PyBuiltinFunction("create_decimal", arguments =>
        {
            var value = DecimalModule.Construct(State, arguments.Length > 0 ? arguments[0] : new PyStr("0"), this);
            return value.Fix(this);
        }),
        "create_decimal_from_float" => new PyBuiltinFunction("create_decimal_from_float", arguments =>
            DecimalModule.Construct(State, arguments[0], this).Fix(this)),
        "clear_flags" => new PyBuiltinFunction("clear_flags", _ => PyNone.Instance),
        "clear_traps" => new PyBuiltinFunction("clear_traps", _ =>
            throw new PyRaise(PyErrors.ValueError("decimal contexts in this sandbox always trap"))),
        "flags" => new PyDict(),
        _ => null,
    };

    /// <inheritdoc />
    public override bool SetAttribute(string name, PyObject value)
    {
        switch (name)
        {
            case "prec":
                var precision = Builtins.BuiltinNamespace.RequireInt(value, "prec");

                // A precision is a promise to compute that many digits; one beyond what any
                // program could print is a way to make the host compute forever.
                if (precision < 1 || precision > 100_000)
                {
                    throw new PyRaise(PyErrors.ValueError("valid range for prec is [1, 100000] in this sandbox"));
                }

                Precision = (int)precision;
                return true;

            case "rounding":
                Rounding = value is PyStr { Value: var mode } && DecimalModule.RoundingModes.Contains(mode)
                    ? mode
                    : throw new PyRaise(PyErrors.TypeError("valid values for rounding are:\n  [ROUND_CEILING, ROUND_FLOOR, ROUND_UP, ROUND_DOWN,\n   ROUND_HALF_UP, ROUND_HALF_DOWN, ROUND_HALF_EVEN,\n   ROUND_05UP]"));
                return true;

            case "Emax":
                var emax = Builtins.BuiltinNamespace.RequireInt(value, "Emax");
                Emax = emax < 0 || emax > 999_999_999_999_999_999L
                    ? throw new PyRaise(PyErrors.ValueError("valid range for Emax is [0, MAX_EMAX]"))
                    : (long)emax;
                return true;

            case "Emin":
                var emin = Builtins.BuiltinNamespace.RequireInt(value, "Emin");
                Emin = emin > 0 || emin < -999_999_999_999_999_999L
                    ? throw new PyRaise(PyErrors.ValueError("valid range for Emin is [MIN_EMIN, 0]"))
                    : (long)emin;
                return true;

            case "capitals":
                Capitals = Builtins.BuiltinNamespace.RequireInt(value, "capitals") != 0;
                return true;

            case "clamp":
                Clamp = (int)Builtins.BuiltinNamespace.RequireInt(value, "clamp");
                return true;

            case "flags" or "traps":
                return true;

            default:
                return false;
        }
    }
}

/// <summary>What kind of value a Decimal is.</summary>
public enum DecimalKind
{
    /// <summary>A number.</summary>
    Finite,

    /// <summary>Positive or negative infinity.</summary>
    Infinity,

    /// <summary>A quiet NaN.</summary>
    NaN,

    /// <summary>A signaling NaN.</summary>
    SignalingNaN,
}
