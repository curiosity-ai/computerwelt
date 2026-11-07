using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules.Numpy;

/// <summary>
/// The element-wise functions — numpy's ufuncs — with broadcasting and type promotion.
/// </summary>
/// <remarks>
/// <para>
/// Integer arithmetic is 64-bit and wraps on overflow, as numpy's does; integer division
/// and remainder by zero give 0 (numpy warns and gives 0). Floats follow IEEE, so
/// <c>1.0 / 0</c> is <c>inf</c> rather than an exception — numpy prints a
/// <c>RuntimeWarning</c> there, which this sandbox has no channel for and omits.
/// </para>
/// <para>
/// The result of a ufunc whose operands are all scalars (or 0-d arrays) is a Python scalar,
/// which is where numpy would hand back a numpy scalar.
/// </para>
/// </remarks>
internal static class Ufuncs
{
    /// <summary>The ufunc behind each binary operator.</summary>
    public static string? NameOfOperator(string op) => op switch
    {
        "+" => "add",
        "-" => "subtract",
        "*" => "multiply",
        "/" => "true_divide",
        "//" => "floor_divide",
        "%" => "remainder",
        "**" => "power",
        "&" => "bitwise_and",
        "|" => "bitwise_or",
        "^" => "bitwise_xor",
        "<<" => "left_shift",
        ">>" => "right_shift",
        "==" => "equal",
        "!=" => "not_equal",
        "<" => "less",
        "<=" => "less_equal",
        ">" => "greater",
        ">=" => "greater_equal",
        _ => null,
    };

    private static string OperatorOf(string name) => name switch
    {
        "add" => "+",
        "subtract" => "-",
        "multiply" => "*",
        "true_divide" or "divide" => "/",
        "floor_divide" => "//",
        "remainder" or "mod" => "%",
        "power" => "**",
        "bitwise_and" => "&",
        "bitwise_or" => "|",
        "bitwise_xor" => "^",
        "left_shift" => "<<",
        "right_shift" => ">>",
        _ => name,
    };

    public static readonly string[] BinaryNames =
    [
        "add", "subtract", "multiply", "true_divide", "divide", "floor_divide", "remainder", "mod", "fmod",
        "power", "float_power", "maximum", "minimum", "fmax", "fmin", "arctan2", "hypot", "copysign",
        "bitwise_and", "bitwise_or", "bitwise_xor", "left_shift", "right_shift", "logical_and",
        "logical_or", "logical_xor", "equal", "not_equal", "less", "less_equal", "greater", "greater_equal",
    ];

    public static readonly string[] UnaryNames =
    [
        "negative", "positive", "absolute", "abs", "fabs", "sign", "square", "sqrt", "cbrt", "exp", "exp2",
        "expm1", "log", "log2", "log10", "log1p", "sin", "cos", "tan", "arcsin", "arccos", "arctan", "sinh",
        "cosh", "tanh", "arcsinh", "arccosh", "arctanh", "degrees", "radians", "deg2rad", "rad2deg", "floor",
        "ceil", "trunc", "rint", "fix", "isnan", "isinf", "isfinite", "logical_not", "invert",
        "bitwise_not", "reciprocal", "signbit", "isposinf", "isneginf",
    ];

    /// <summary>A binary ufunc applied to two values of any array-like shape.</summary>
    public static PyObject Binary(string name, PyObject left, PyObject right) =>
        NdConvert.Wrap(BinaryArray(name, NdConvert.ToArray(left), NdConvert.ToArray(right)));

    /// <summary>A comparison operator, element-wise.</summary>
    public static PyObject Compare(string op, PyObject left, PyObject right) =>
        Binary(NameOfOperator(op)!, left, right);

    public static NdArray BinaryArray(string name, NdArray x, NdArray y)
    {
        var shape = NdArray.BroadcastShapes(x.Shape, y.Shape);
        var n = NdArray.CheckedSize(shape);

        if (NdSimd.TryBinary(name, x, y, shape, n) is { } vectorised)
        {
            return vectorised;
        }

        // The position lists are scratch for this one loop, so they are rented.
        var ox = x.BroadcastTo(shape).RentOffsets();
        var oy = y.BroadcastTo(shape).RentOffsets();

        try
        {
            return BinaryArray(name, x, y, shape, ox, oy, n);
        }
        finally
        {
            NdArray.ReturnOffsets(ox);
            NdArray.ReturnOffsets(oy);
        }
    }

    private static NdArray BinaryArray(string name, NdArray x, NdArray y, int[] shape, int[] ox, int[] oy, int n)
    {
        switch (name)
        {
            case "equal" or "not_equal" or "less" or "less_equal" or "greater" or "greater_equal":
                return FromBools(Comparison(name, x, y, ox, oy, n), shape);

            case "logical_and" or "logical_or" or "logical_xor":
            {
                var result = new bool[n];

                for (var k = 0; k < n; k++)
                {
                    var (a, b) = (x.GetBool(ox[k]), y.GetBool(oy[k]));
                    result[k] = name switch
                    {
                        "logical_and" => a && b,
                        "logical_or" => a || b,
                        _ => a ^ b,
                    };
                }

                return FromBools(result, shape);
            }
        }

        var kind = NdDType.Promote(x.Kind, y.Kind);

        if (x.Kind == DKind.Str && y.Kind == DKind.Str && name == "add")
        {
            var joined = new PyObject[n];

            for (var k = 0; k < n; k++)
            {
                joined[k] = new PyStr(((PyStr)x.GetBoxed(ox[k])).Value + ((PyStr)y.GetBoxed(oy[k])).Value);
            }

            return NdArray.FromObjects(DKind.Str, joined, shape);
        }

        if (x.Kind == DKind.Str || y.Kind == DKind.Str)
        {
            throw NoLoop(name, x, y);
        }

        if (kind == DKind.Object)
        {
            var op = OperatorOf(name);
            var values = new PyObject[n];

            for (var k = 0; k < n; k++)
            {
                values[k] = name switch
                {
                    "maximum" or "fmax" => Max(x.GetBoxed(ox[k]), y.GetBoxed(oy[k])),
                    "minimum" or "fmin" => Min(x.GetBoxed(ox[k]), y.GetBoxed(oy[k])),
                    _ when op != name => Operators.Binary(op, x.GetBoxed(ox[k]), y.GetBoxed(oy[k])),
                    _ => new PyFloat(FloatOp(name, x.GetDouble(ox[k]), y.GetDouble(oy[k]))),
                };
            }

            return NdArray.FromObjects(DKind.Object, values, shape);
        }

        // Functions whose result is a float whatever the inputs are.
        if (name is "true_divide" or "divide" or "arctan2" or "hypot" or "copysign" or "float_power")
        {
            kind = DKind.Float;
        }

        if (kind == DKind.Bool)
        {
            switch (name)
            {
                case "add" or "bitwise_or" or "maximum" or "fmax":
                    return FromBools(Zip(x, y, ox, oy, n, static (a, b) => a || b), shape);
                case "multiply" or "bitwise_and" or "minimum" or "fmin":
                    return FromBools(Zip(x, y, ox, oy, n, static (a, b) => a && b), shape);
                case "bitwise_xor":
                    return FromBools(Zip(x, y, ox, oy, n, static (a, b) => a ^ b), shape);
                case "subtract":
                    throw new PyRaise(PyErrors.TypeError(
                        "numpy boolean subtract, the `-` operator, is not supported, use the bitwise_xor, "
                        + "the `^` operator, or the logical_xor function instead."));
                default:
                    kind = DKind.Int;
                    break;
            }
        }

        if (kind == DKind.Int)
        {
            if (name is "power" && Enumerable.Range(0, n).Any(k => y.GetLong(oy[k]) < 0))
            {
                throw new PyRaise(PyErrors.ValueError("Integers to negative integer powers are not allowed."));
            }

            var result = new long[n];

            for (var k = 0; k < n; k++)
            {
                result[k] = IntOp(name, x.GetLong(ox[k]), y.GetLong(oy[k]));
            }

            return NdArray.FromLongs(result, shape);
        }

        if (name is "bitwise_and" or "bitwise_or" or "bitwise_xor" or "left_shift" or "right_shift")
        {
            throw new PyRaise(PyErrors.TypeError(
                $"ufunc '{name}' not supported for the input types, and the inputs could not be safely coerced "
                + "to any supported types according to the casting rule ''safe''"));
        }

        var floats = new double[n];

        for (var k = 0; k < n; k++)
        {
            floats[k] = FloatOp(name, x.GetDouble(ox[k]), y.GetDouble(oy[k]));
        }

        return NdArray.FromDoubles(floats, shape);
    }

    private static PyRaise NoLoop(string name, NdArray x, NdArray y) =>
        new(PyErrors.TypeError(
            $"ufunc '{name}' did not contain a loop with signature matching types "
            + $"(<class 'numpy.dtypes.{DTypeClass(x)}'>, <class 'numpy.dtypes.{DTypeClass(y)}'>) -> None"));

    private static string DTypeClass(NdArray a) => a.Kind switch
    {
        DKind.Bool => "BoolDType",
        DKind.Int => "Int64DType",
        DKind.Float => "Float64DType",
        DKind.Str => "StrDType",
        _ => "ObjectDType",
    };

    private static PyObject Max(PyObject a, PyObject b) => Operators.Compare("<", a, b).IsTruthy() ? b : a;

    private static PyObject Min(PyObject a, PyObject b) => Operators.Compare(">", a, b).IsTruthy() ? b : a;

    private static NdArray FromBools(bool[] values, int[] shape) => NdArray.FromBools(values, shape);

    private static bool[] Zip(NdArray x, NdArray y, int[] ox, int[] oy, int n, Func<bool, bool, bool> f)
    {
        var result = new bool[n];

        for (var k = 0; k < result.Length; k++)
        {
            result[k] = f(x.GetBool(ox[k]), y.GetBool(oy[k]));
        }

        return result;
    }

    private static bool[] Comparison(string name, NdArray x, NdArray y, int[] ox, int[] oy, int n)
    {
        var result = new bool[n];
        var kind = NdDType.Promote(x.Kind, y.Kind);

        if (x.Kind == DKind.Str && y.Kind == DKind.Str)
        {
            for (var k = 0; k < n; k++)
            {
                var order = string.CompareOrdinal(((PyStr)x.GetBoxed(ox[k])).Value, ((PyStr)y.GetBoxed(oy[k])).Value);
                result[k] = Decide(name, order);
            }

            return result;
        }

        if (kind is DKind.Object || x.Kind == DKind.Str || y.Kind == DKind.Str)
        {
            var op = name switch
            {
                "equal" => "==",
                "not_equal" => "!=",
                "less" => "<",
                "less_equal" => "<=",
                "greater" => ">",
                _ => ">=",
            };

            for (var k = 0; k < n; k++)
            {
                result[k] = Operators.Compare(op, x.GetBoxed(ox[k]), y.GetBoxed(oy[k])).IsTruthy();
            }

            return result;
        }

        if (kind == DKind.Float)
        {
            for (var k = 0; k < n; k++)
            {
                var (a, b) = (x.GetDouble(ox[k]), y.GetDouble(oy[k]));
                result[k] = name switch
                {
                    "equal" => a == b,
                    "not_equal" => a != b,
                    "less" => a < b,
                    "less_equal" => a <= b,
                    "greater" => a > b,
                    _ => a >= b,
                };
            }

            return result;
        }

        for (var k = 0; k < n; k++)
        {
            result[k] = Decide(name, x.GetLong(ox[k]).CompareTo(y.GetLong(oy[k])));
        }

        return result;
    }

    private static bool Decide(string name, int order) => name switch
    {
        "equal" => order == 0,
        "not_equal" => order != 0,
        "less" => order < 0,
        "less_equal" => order <= 0,
        "greater" => order > 0,
        _ => order >= 0,
    };

    /// <summary>numpy's int64 floor division: rounds toward minus infinity, and 0 for a zero divisor.</summary>
    internal static long FloorDivide(long a, long b)
    {
        unchecked
        {
            if (b == 0)
            {
                return 0;
            }

            if (b == -1)
            {
                return -a;
            }

            var quotient = a / b;
            return (a % b != 0) && ((a < 0) != (b < 0)) ? quotient - 1 : quotient;
        }
    }

    /// <summary>numpy's int64 remainder: the sign of the divisor, and 0 for a zero divisor.</summary>
    internal static long Remainder(long a, long b)
    {
        if (b == 0 || b == -1)
        {
            return 0;
        }

        var remainder = a % b;
        return remainder != 0 && ((remainder < 0) != (b < 0)) ? remainder + b : remainder;
    }

    /// <summary>numpy's int64 <c>fmod</c>: the sign of the dividend.</summary>
    internal static long Fmod(long a, long b) => b == 0 || b == -1 ? 0 : a % b;

    private static long IntOp(string name, long a, long b)
    {
        unchecked
        {
            switch (name)
            {
                case "add": return a + b;
                case "subtract": return a - b;
                case "multiply": return a * b;
                case "floor_divide": return FloorDivide(a, b);
                case "remainder" or "mod": return Remainder(a, b);
                case "fmod": return Fmod(a, b);
                case "power":
                {
                    long result = 1;
                    var power = a;

                    for (var e = b; e > 0; e >>= 1)
                    {
                        if ((e & 1) != 0)
                        {
                            result *= power;
                        }

                        power *= power;
                    }

                    return result;
                }

                case "maximum" or "fmax": return Math.Max(a, b);
                case "minimum" or "fmin": return Math.Min(a, b);
                case "bitwise_and": return a & b;
                case "bitwise_or": return a | b;
                case "bitwise_xor": return a ^ b;
                case "left_shift": return b is < 0 or >= 64 ? 0 : a << (int)b;
                case "right_shift": return b is < 0 or >= 64 ? (a < 0 ? -1 : 0) : a >> (int)b;
                default: return (long)FloatOp(name, a, b);
            }
        }
    }

    /// <summary>Python's float floor division and remainder, which numpy shares.</summary>
    internal static (double Floor, double Mod) DivMod(double a, double b)
    {
        if (b == 0)
        {
            return (a / b, double.NaN);
        }

        var mod = a % b;
        var div = (a - mod) / b;

        if (mod != 0)
        {
            if ((b < 0) != (mod < 0))
            {
                mod += b;
                div -= 1.0;
            }
        }
        else
        {
            mod = Math.CopySign(0.0, b);
        }

        double floor;

        if (div != 0)
        {
            floor = Math.Floor(div);

            if (div - floor > 0.5)
            {
                floor += 1.0;
            }
        }
        else
        {
            floor = Math.CopySign(0.0, a / b);
        }

        return (floor, mod);
    }

    public static double FloatOp(string name, double a, double b) => name switch
    {
        "add" => a + b,
        "subtract" => a - b,
        "multiply" => a * b,
        "true_divide" or "divide" => a / b,
        "floor_divide" => DivMod(a, b).Floor,
        "remainder" or "mod" => DivMod(a, b).Mod,
        "fmod" => a % b,
        "power" or "float_power" => Math.Pow(a, b),
        "maximum" => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Max(a, b),
        "minimum" => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Min(a, b),
        "fmax" => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b),
        "fmin" => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Min(a, b),
        "arctan2" => Math.Atan2(a, b),
        "hypot" => Hypot(a, b),
        "copysign" => Math.CopySign(a, b),
        _ => throw new PyRaise(PyErrors.TypeError($"ufunc '{name}' is not supported")),
    };

    private static double Hypot(double a, double b)
    {
        if (double.IsInfinity(a) || double.IsInfinity(b))
        {
            return double.PositiveInfinity;
        }

        (a, b) = (Math.Abs(a), Math.Abs(b));
        var max = Math.Max(a, b);
        var min = Math.Min(a, b);

        if (max == 0)
        {
            return 0;
        }

        var ratio = min / max;
        return max * Math.Sqrt(1 + (ratio * ratio));
    }

    /// <summary>A unary ufunc applied to an array-like.</summary>
    public static PyObject Unary(string name, PyObject value) =>
        NdConvert.Wrap(UnaryArray(name, NdConvert.ToArray(value)));

    public static NdArray UnaryArray(string name, NdArray x)
    {
        if (NdSimd.TryUnary(name, x) is { } vectorised)
        {
            return vectorised;
        }

        var shape = (int[])x.Shape.Clone();
        var n = x.Size;

        switch (name)
        {
            case "isnan" or "isinf" or "isfinite" or "signbit" or "isposinf" or "isneginf":
            {
                if (x.Kind is DKind.Str or DKind.Object)
                {
                    throw new PyRaise(PyErrors.TypeError(
                        $"ufunc '{name}' not supported for the input types, and the inputs could not be safely "
                        + "coerced to any supported types according to the casting rule ''safe''"));
                }

                var values = x.ToDoubles();
                var result = new bool[n];

                for (var k = 0; k < n; k++)
                {
                    var v = values[k];
                    result[k] = name switch
                    {
                        "isnan" => double.IsNaN(v),
                        "isinf" => double.IsInfinity(v),
                        "isfinite" => double.IsFinite(v),
                        "isposinf" => double.IsPositiveInfinity(v),
                        "isneginf" => double.IsNegativeInfinity(v),
                        _ => double.IsNegative(v),
                    };
                }

                return NdArray.FromBools(result, shape);
            }

            case "logical_not":
                return NdArray.FromBools([.. x.ToBools().Select(static b => !b)], shape);
        }

        if (x.Kind == DKind.Str)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"ufunc '{name}' did not contain a loop with signature matching types "
                + "<class 'numpy.dtypes.StrDType'> -> None"));
        }

        if (x.Kind == DKind.Object)
        {
            var source = x.ToBoxed();
            var values = new PyObject[n];

            for (var k = 0; k < n; k++)
            {
                values[k] = name switch
                {
                    "negative" => Operators.Unary("-", source[k]),
                    "positive" => Operators.Unary("+", source[k]),
                    "absolute" or "abs" => source[k] switch
                    {
                        PyInt integer => PyInt.From(System.Numerics.BigInteger.Abs(integer.Value)),
                        PyFloat number => new PyFloat(Math.Abs(number.Value)),
                        var other => Operators.Unary("abs", other),
                    },
                    "square" => Operators.Binary("*", source[k], source[k]),
                    _ => new PyFloat(FloatUnary(name, NdArray.ToDouble(source[k]))),
                };
            }

            return NdArray.FromObjects(DKind.Object, values, shape);
        }

        if (x.Kind == DKind.Bool)
        {
            switch (name)
            {
                case "negative":
                    throw new PyRaise(PyErrors.TypeError(
                        "The numpy boolean negative, the `-` operator, is not supported, use the `~` operator or "
                        + "the logical_not function instead."));
                case "invert" or "bitwise_not":
                    return NdArray.FromBools([.. x.ToBools().Select(static b => !b)], shape);
                case "positive" or "absolute" or "abs":
                    return NdArray.FromBools(x.ToBools(), shape);
            }
        }

        if (x.Kind is DKind.Int or DKind.Bool && name is "negative" or "positive" or "absolute" or "abs" or "sign"
            or "square" or "invert" or "bitwise_not")
        {
            var values = x.ToLongs();
            var result = new long[n];

            unchecked
            {
                for (var k = 0; k < n; k++)
                {
                    var v = values[k];
                    result[k] = name switch
                    {
                        "negative" => -v,
                        "positive" => v,
                        "absolute" or "abs" => v < 0 ? -v : v,
                        "sign" => Math.Sign(v),
                        "square" => v * v,
                        _ => ~v,
                    };
                }
            }

            return NdArray.FromLongs(result, shape);
        }

        if (name is "invert" or "bitwise_not")
        {
            throw new PyRaise(PyErrors.TypeError(
                "ufunc 'invert' not supported for the input types, and the inputs could not be safely coerced to "
                + "any supported types according to the casting rule ''safe''"));
        }

        var input = x.ToDoubles();
        var output = new double[n];

        for (var k = 0; k < n; k++)
        {
            output[k] = FloatUnary(name, input[k]);
        }

        return NdArray.FromDoubles(output, shape);
    }

    public static double FloatUnary(string name, double v) => name switch
    {
        "negative" => -v,
        "positive" => v,
        "absolute" or "abs" or "fabs" => Math.Abs(v),
        "sign" => double.IsNaN(v) ? double.NaN : Math.Sign(v),
        "square" => v * v,
        "sqrt" => Math.Sqrt(v),
        "cbrt" => Math.Cbrt(v),
        "exp" => Math.Exp(v),
        "exp2" => Math.Pow(2, v),
        "expm1" => ExpM1(v),
        "log" => Math.Log(v),
        "log2" => Math.Log2(v),
        "log10" => Math.Log10(v),
        "log1p" => Log1P(v),
        "sin" => Math.Sin(v),
        "cos" => Math.Cos(v),
        "tan" => Math.Tan(v),
        "arcsin" => Math.Asin(v),
        "arccos" => Math.Acos(v),
        "arctan" => Math.Atan(v),
        "sinh" => Math.Sinh(v),
        "cosh" => Math.Cosh(v),
        "tanh" => Math.Tanh(v),
        "arcsinh" => Math.Asinh(v),
        "arccosh" => Math.Acosh(v),
        "arctanh" => Math.Atanh(v),
        "degrees" or "rad2deg" => v * (180.0 / Math.PI),
        "radians" or "deg2rad" => v * (Math.PI / 180.0),
        "floor" => Math.Floor(v),
        "ceil" => Math.Ceiling(v),
        "trunc" or "fix" => Math.Truncate(v),
        "rint" => Math.Round(v, MidpointRounding.ToEven),
        "reciprocal" => 1.0 / v,
        _ => throw new PyRaise(PyErrors.TypeError($"ufunc '{name}' is not supported")),
    };

    private static double ExpM1(double v) =>
        Math.Abs(v) < 1e-5 ? v + (v * v / 2) + (v * v * v / 6) : Math.Exp(v) - 1;

    private static double Log1P(double v)
    {
        if (v == -1)
        {
            return double.NegativeInfinity;
        }

        if (v < -1 || double.IsNaN(v))
        {
            return double.NaN;
        }

        // log(1 + v) loses v's low digits when v is tiny; this correction recovers them.
        var u = 1 + v;
        return u == 1 ? v : Math.Log(u) * v / (u - 1);
    }

    /// <summary><c>np.round(a, decimals)</c>: half to even, as <c>rint(a * 10**d) / 10**d</c>.</summary>
    public static NdArray Round(NdArray x, int decimals)
    {
        switch (x.Kind)
        {
            case DKind.Bool:
                return x.Copy();

            case DKind.Int:
            {
                if (decimals >= 0)
                {
                    return x.Copy();
                }

                var factor = (long)Math.Pow(10, Math.Min(-decimals, 18));
                return NdArray.FromLongs(
                    [.. x.ToLongs().Select(v => (long)Math.Round((double)v / factor, MidpointRounding.ToEven) * factor)],
                    (int[])x.Shape.Clone());
            }

            case DKind.Float:
            {
                var values = x.ToDoubles();

                if (decimals >= 0)
                {
                    var factor = Math.Pow(10, decimals);

                    for (var k = 0; k < values.Length; k++)
                    {
                        var scaled = values[k] * factor;
                        values[k] = double.IsInfinity(scaled) ? values[k] : Math.Round(scaled, MidpointRounding.ToEven) / factor;
                    }
                }
                else
                {
                    var factor = Math.Pow(10, -decimals);

                    for (var k = 0; k < values.Length; k++)
                    {
                        values[k] = Math.Round(values[k] / factor, MidpointRounding.ToEven) * factor;
                    }
                }

                return NdArray.FromDoubles(values, (int[])x.Shape.Clone());
            }

            default:
                throw new PyRaise(PyErrors.TypeError("ufunc 'rint' not supported for the input types"));
        }
    }

    /// <summary><c>np.where(cond, x, y)</c>.</summary>
    public static PyObject Where(PyObject condition, PyObject whenTrue, PyObject whenFalse)
    {
        var c = NdConvert.ToArray(condition);
        var x = NdConvert.ToArray(whenTrue);
        var y = NdConvert.ToArray(whenFalse);
        var shape = NdArray.BroadcastShapes(c.Shape, x.Shape, y.Shape);
        var n = NdArray.CheckedSize(shape);

        if (NdSimd.TryWhere(c, x, y, shape, n) is { } selected)
        {
            return NdConvert.Wrap(selected);
        }

        var oc = c.BroadcastTo(shape).Offsets();
        var ox = x.BroadcastTo(shape).Offsets();
        var oy = y.BroadcastTo(shape).Offsets();
        var kind = x.Kind == DKind.Str && y.Kind == DKind.Str ? DKind.Str : NdDType.Promote(x.Kind, y.Kind);
        var result = NdArray.Zeros(shape, kind == DKind.Str ? NdDType.Str(Math.Max(x.DType.Width, y.DType.Width)) : NdDType.Of(kind));

        for (var k = 0; k < oc.Length; k++)
        {
            if (c.GetBool(oc[k]))
            {
                result.SetFrom(k, x, ox[k]);
            }
            else
            {
                result.SetFrom(k, y, oy[k]);
            }
        }

        return NdConvert.Wrap(result);
    }

    /// <summary><c>np.clip(a, lo, hi)</c>, either bound optional.</summary>
    public static PyObject Clip(PyObject value, PyObject? low, PyObject? high)
    {
        if (low is null && high is null)
        {
            throw new PyRaise(PyErrors.ValueError("One of max or min must be given"));
        }

        var result = NdConvert.ToArray(value);

        if (low is not null)
        {
            result = BinaryArray("maximum", result, NdConvert.ToArray(low));
        }

        if (high is not null)
        {
            result = BinaryArray("minimum", result, NdConvert.ToArray(high));
        }

        return NdConvert.Wrap(result);
    }
}
