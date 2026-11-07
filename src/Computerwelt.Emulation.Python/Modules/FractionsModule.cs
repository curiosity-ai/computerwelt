using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>The <c>fractions</c> module: exact rational arithmetic, as CPython 3.13 has it.</summary>
public static partial class FractionsModule
{
    /// <summary>The <c>Fraction</c> class.</summary>
    /// <remarks>Every instance names it as its type, so <c>type(Fraction(1, 2)) is Fraction</c> holds.</remarks>
    public static PyType Type { get; } = MakeType();

    /// <summary>Builds the module.</summary>
    public static PyModuleObject Create()
    {
        var module = new PyModuleObject("fractions");
        module.Add("Fraction", Type);
        return module;
    }

    private static PyType MakeType()
    {
        var type = new PyType("Fraction", static value => value is PyFraction, static (arguments, keywords) => Construct(arguments, keywords));

        type.Members["from_float"] = new PyBuiltinFunction("from_float", static arguments =>
        {
            Arity.ExactCount("from_float", arguments, 1);

            return arguments[0] switch
            {
                PyInt integer => new PyFraction(integer.Value, BigInteger.One),
                PyFloat number => FromDouble(number.Value),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"Fraction.from_float() only takes floats, not {other.Repr()} ({other.TypeName})")),
            };
        });

        type.Members["from_decimal"] = new PyBuiltinFunction("from_decimal", static arguments =>
        {
            Arity.ExactCount("from_decimal", arguments, 1);

            return arguments[0] switch
            {
                PyInt integer => new PyFraction(integer.Value, BigInteger.One),
                PyDecimal number => FromDecimal(number),
                var other => throw new PyRaise(PyErrors.TypeError(
                    $"Fraction.from_decimal() only takes Decimals, not {other.Repr()} ({other.TypeName})")),
            };
        });

        return type;
    }

    [GeneratedRegex(
        @"\A\s*(?<sign>[-+]?)(?=[0-9]|\.[0-9])(?<num>[0-9]*|[0-9]+(_[0-9]+)*)(?:(?:\s*/\s*(?<denom>[0-9]+(_[0-9]+)*))?|(?:\.(?<decimal>[0-9]*|[0-9]+(_[0-9]+)*))?(?:[eE](?<exp>[-+]?[0-9]+(_[0-9]+)*))?)\s*\z")]
    private static partial Regex RationalFormat();

    /// <summary>The largest power of ten a literal's exponent may ask for.</summary>
    /// <remarks>
    /// <c>Fraction('1e999999999')</c> would build a billion-digit integer; the instruction
    /// limit does not count work inside a builtin, so the size is capped where it is asked for.
    /// </remarks>
    private const int MaxExponent = 1_000_000;

    private static PyObject Construct(PyObject[] arguments, PyDict? keywords)
    {
        PyObject? numerator = arguments.Length > 0 ? arguments[0] : null;
        PyObject? denominator = arguments.Length > 1 ? arguments[1] : null;

        if (arguments.Length > 2)
        {
            throw new PyRaise(PyErrors.TypeError(
                $"Fraction.__new__() takes from 1 to 3 positional arguments but {arguments.Length + 1} were given"));
        }

        foreach (var (key, value) in keywords?.Entries ?? [])
        {
            switch (key.Display())
            {
                case "numerator" when numerator is null: numerator = value; break;
                case "denominator" when denominator is null: denominator = value; break;
                default:
                    throw new PyRaise(PyErrors.TypeError(
                        $"Fraction.__new__() got an unexpected keyword argument '{key.Display()}'"));
            }
        }

        numerator ??= PyInt.From(0);

        if (denominator is null or PyNone)
        {
            return numerator switch
            {
                PyFraction fraction => fraction,
                PyInt integer => new PyFraction(integer.Value, BigInteger.One),
                PyFloat number => FromDouble(number.Value),
                PyDecimal number => FromDecimal(number),
                PyStr text => Parse(text.Value),
                _ => throw new PyRaise(PyErrors.TypeError(
                    "argument should be a string or a Rational instance")),
            };
        }

        if (Rational(numerator) is not { } top || Rational(denominator) is not { } bottom)
        {
            throw new PyRaise(PyErrors.TypeError("both arguments should be Rational instances"));
        }

        var n = top.Numerator * bottom.Denominator;
        var d = top.Denominator * bottom.Numerator;

        if (d.IsZero)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError($"Fraction({PyInt.Decimal(n)}, 0)"));
        }

        return PyFraction.Normalized(n, d);
    }

    /// <summary>The exact rational value of an int or Fraction, or null for anything else.</summary>
    internal static (BigInteger Numerator, BigInteger Denominator)? Rational(PyObject value) => value switch
    {
        PyFraction fraction => (fraction.Numerator, fraction.Denominator),
        PyInt integer => (integer.Value, BigInteger.One),
        _ => null,
    };

    private static PyFraction Parse(string text)
    {
        var match = RationalFormat().Match(text);

        if (!match.Success)
        {
            throw new PyRaise(PyErrors.ValueError($"Invalid literal for Fraction: {PyStr.Quote(text)}"));
        }

        static BigInteger Digits(string digits) =>
            digits.Length == 0 ? BigInteger.Zero : BigInteger.Parse(digits.Replace("_", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);

        var numerator = Digits(match.Groups["num"].Value);
        var denominator = BigInteger.One;

        if (match.Groups["denom"].Success)
        {
            denominator = Digits(match.Groups["denom"].Value);
        }
        else
        {
            if (match.Groups["decimal"].Success && match.Groups["decimal"].Value.Length > 0)
            {
                var fraction = match.Groups["decimal"].Value.Replace("_", string.Empty, StringComparison.Ordinal);
                var scale = BigInteger.Pow(10, fraction.Length);
                numerator = (numerator * scale) + Digits(fraction);
                denominator *= scale;
            }

            if (match.Groups["exp"].Success)
            {
                var exponent = Digits(match.Groups["exp"].Value.TrimStart('+', '-'));

                if (exponent > MaxExponent)
                {
                    throw new PyRaise(new PyException(PyExceptionType.OverflowError, "exponent too large"));
                }

                if (match.Groups["exp"].Value.StartsWith('-'))
                {
                    denominator *= BigInteger.Pow(10, (int)exponent);
                }
                else
                {
                    numerator *= BigInteger.Pow(10, (int)exponent);
                }
            }
        }

        if (match.Groups["sign"].Value == "-")
        {
            numerator = -numerator;
        }

        if (denominator.IsZero)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError($"Fraction({PyInt.Decimal(numerator)}, 0)"));
        }

        return PyFraction.Normalized(numerator, denominator);
    }

    /// <summary>The exact value of a float.</summary>
    internal static PyFraction FromDouble(double value)
    {
        if (double.IsNaN(value))
        {
            throw new PyRaise(PyErrors.ValueError("cannot convert NaN to integer ratio"));
        }

        if (double.IsInfinity(value))
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "cannot convert Infinity to integer ratio"));
        }

        var (numerator, denominator) = Ratio(value);
        return PyFraction.Normalized(numerator, denominator);
    }

    /// <summary>A finite double as an exact numerator and power-of-two denominator.</summary>
    internal static (BigInteger Numerator, BigInteger Denominator) Ratio(double value)
    {
        if (value == 0)
        {
            return (BigInteger.Zero, BigInteger.One);
        }

        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;

        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        exponent -= 1075;
        BigInteger numerator = mantissa;
        var denominator = BigInteger.One;

        if (exponent > 0)
        {
            numerator <<= exponent;
        }
        else
        {
            denominator <<= -exponent;
        }

        return (negative ? -numerator : numerator, denominator);
    }

    private static PyFraction FromDecimal(PyDecimal value)
    {
        if (value.IsNaN)
        {
            throw new PyRaise(PyErrors.ValueError($"cannot convert {value.Display()} to integer ratio"));
        }

        if (value.IsInfinite)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, $"cannot convert {value.Display()} to integer ratio"));
        }

        var (numerator, denominator) = value.Ratio();
        return PyFraction.Normalized(numerator, denominator);
    }
}

/// <summary>A <c>fractions.Fraction</c>: a numerator and a positive denominator in lowest terms.</summary>
public sealed class PyFraction : PyObject, IPyNumber, IPyHostTyped
{
    /// <inheritdoc />
    PyObject IPyHostTyped.Type => FractionsModule.Type;

    private PyFraction(BigInteger numerator, BigInteger denominator, bool normalized)
    {
        _ = normalized;
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>Creates a fraction already in lowest terms with a positive denominator.</summary>
    internal PyFraction(BigInteger numerator, BigInteger denominator)
        : this(numerator, denominator, normalized: true)
    {
    }

    /// <summary>Reduces <paramref name="numerator"/>/<paramref name="denominator"/> to lowest terms.</summary>
    internal static PyFraction Normalized(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new PyRaise(PyErrors.ZeroDivisionError($"Fraction({PyInt.Decimal(numerator)}, 0)"));
        }

        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);

        if (denominator.Sign < 0)
        {
            divisor = -divisor;
        }

        return new PyFraction(numerator / divisor, denominator / divisor);
    }

    /// <summary>The numerator, carrying the sign.</summary>
    public BigInteger Numerator { get; }

    /// <summary>The denominator, always positive.</summary>
    public BigInteger Denominator { get; }

    /// <inheritdoc />
    public override string TypeName => "Fraction";

    /// <inheritdoc />
    public override string Repr() =>
        $"Fraction({PyInt.Decimal(Numerator)}, {PyInt.Decimal(Denominator)})";

    /// <inheritdoc />
    public override string Display() =>
        Denominator.IsOne ? PyInt.Decimal(Numerator) : $"{PyInt.Decimal(Numerator)}/{PyInt.Decimal(Denominator)}";

    /// <inheritdoc />
    public override bool IsTruthy() => !Numerator.IsZero;

    /// <inheritdoc />
    public override BigInteger PyHash() => RationalHash(Numerator, Denominator);

    /// <summary>
    /// CPython's hash of a rational: the value modulo 2**61-1, so a Fraction equal to an int
    /// or a float hashes as that number does and the three share a dict key.
    /// </summary>
    internal static BigInteger RationalHash(BigInteger numerator, BigInteger denominator)
    {
        var modulus = (BigInteger.One << 61) - 1;
        var inverse = BigInteger.ModPow(denominator, modulus - 2, modulus);
        BigInteger hash = inverse.IsZero
            ? 314159
            : BigInteger.Abs(numerator) % modulus * inverse % modulus;

        var result = numerator.Sign >= 0 ? hash : -hash;
        return result == BigInteger.MinusOne ? -2 : result;
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => other switch
    {
        PyFraction fraction => Numerator == fraction.Numerator && Denominator == fraction.Denominator,
        PyInt integer => Denominator.IsOne && Numerator == integer.Value,
        PyFloat number => double.IsFinite(number.Value) && CompareTo(FractionsModule.Ratio(number.Value)) == 0,
        PyDecimal number => number.PyEquals(this),
        _ => false,
    };

    /// <inheritdoc />
    public override int? PyCompare(PyObject other)
    {
        switch (other)
        {
            case PyFraction fraction:
                return CompareTo((fraction.Numerator, fraction.Denominator));
            case PyInt integer:
                return CompareTo((integer.Value, BigInteger.One));
            case PyFloat number when double.IsNaN(number.Value):
                return null;
            case PyFloat number when double.IsInfinity(number.Value):
                return number.Value > 0 ? -1 : 1;
            case PyFloat number:
                return CompareTo(FractionsModule.Ratio(number.Value));
            case PyDecimal number:
                return -number.PyCompare(this);
            default:
                return null;
        }
    }

    private int CompareTo((BigInteger Numerator, BigInteger Denominator) other) =>
        (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    /// <inheritdoc />
    public double ToDouble()
    {
        // Correctly rounded: scale so the integer quotient carries 55+ bits, fold any
        // remainder into a sticky bit, and let the integer conversion round once.
        if (Numerator.IsZero)
        {
            return 0.0;
        }

        var magnitude = BigInteger.Abs(Numerator);
        var shift = 55 - ((long)magnitude.GetBitLength() - (long)Denominator.GetBitLength());
        var scaledNumerator = shift > 0 ? magnitude << (int)shift : magnitude;
        var scaledDenominator = shift < 0 ? Denominator << (int)-shift : Denominator;
        var quotient = BigInteger.DivRem(scaledNumerator, scaledDenominator, out var remainder);

        if (!remainder.IsZero)
        {
            quotient |= BigInteger.One;
        }

        double result;

        try
        {
            result = Numbers.ToDouble(quotient);
        }
        catch (PyRaise)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "integer division result too large for a float"));
        }

        result = Math.ScaleB(result, (int)Math.Clamp(-shift, int.MinValue / 2, int.MaxValue / 2));

        if (double.IsInfinity(result))
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "integer division result too large for a float"));
        }

        return Numerator.Sign < 0 ? -result : result;
    }

    /// <inheritdoc />
    public BigInteger Floor() => FloorDivide(Numerator, Denominator);

    /// <inheritdoc />
    public BigInteger Ceiling() => -FloorDivide(-Numerator, Denominator);

    /// <inheritdoc />
    public BigInteger Truncate() => BigInteger.Divide(Numerator, Denominator);

    /// <inheritdoc />
    public PyObject Round(PyObject? digits)
    {
        if (digits is null)
        {
            return PyInt.From(RoundHalfEven(Numerator, Denominator));
        }

        var places = Builtins.BuiltinNamespace.RequireInt(digits, "round");
        var shift = BigInteger.Pow(10, (int)BigInteger.Min(BigInteger.Abs(places), 100_000));

        if (places >= 0)
        {
            return PyFraction.Normalized(RoundHalfEven(Numerator * shift, Denominator), shift);
        }

        return PyFraction.Normalized(RoundHalfEven(Numerator, Denominator * shift) * shift, BigInteger.One);
    }

    private static BigInteger FloorDivide(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return !remainder.IsZero && (remainder.Sign < 0) != (denominator.Sign < 0) ? quotient - 1 : quotient;
    }

    private static BigInteger RoundHalfEven(BigInteger numerator, BigInteger denominator)
    {
        var floor = FloorDivide(numerator, denominator);
        var remainder = numerator - (floor * denominator);
        var twice = remainder * 2;

        return twice < denominator ? floor
            : twice > denominator ? floor + 1
            : floor.IsEven ? floor : floor + 1;
    }

    /// <inheritdoc />
    public PyObject? Unary(string op) => op switch
    {
        "-" => new PyFraction(-Numerator, Denominator),
        "+" => this,
        "abs" => new PyFraction(BigInteger.Abs(Numerator), Denominator),
        _ => null,
    };

    /// <inheritdoc />
    public PyObject? Binary(string op, PyObject other, bool reflected)
    {
        if (other is PyFloat number)
        {
            var mine = new PyFloat(ToDouble());
            return reflected ? Operators.Binary(op, number, mine) : Operators.Binary(op, mine, number);
        }

        if (FractionsModule.Rational(other) is not { } rational)
        {
            return null;
        }

        var (a, b) = reflected
            ? (rational, (Numerator, Denominator))
            : ((Numerator, Denominator), rational);

        return op switch
        {
            "+" => PyFraction.Normalized((a.Item1 * b.Item2) + (b.Item1 * a.Item2), a.Item2 * b.Item2),
            "-" => PyFraction.Normalized((a.Item1 * b.Item2) - (b.Item1 * a.Item2), a.Item2 * b.Item2),
            "*" => PyFraction.Normalized(a.Item1 * b.Item1, a.Item2 * b.Item2),
            "/" => Divide(a, b),
            "//" => PyInt.From(FloorDivide(a.Item1 * b.Item2, NonZero(a.Item2 * b.Item1, a.Item1 * b.Item2))),
            "%" => Modulo(a, b),
            "**" => Power(a, b, reflected),
            _ => null,
        };
    }

    private static BigInteger NonZero(BigInteger divisor, BigInteger numerator) =>
        divisor.IsZero ? throw new PyRaise(PyErrors.ZeroDivisionError($"Fraction({PyInt.Decimal(numerator)}, 0)")) : divisor;

    private static PyFraction Divide((BigInteger, BigInteger) a, (BigInteger, BigInteger) b) =>
        PyFraction.Normalized(a.Item1 * b.Item2, NonZero(a.Item2 * b.Item1, a.Item1 * b.Item2));

    private static PyFraction Modulo((BigInteger, BigInteger) a, (BigInteger, BigInteger) b)
    {
        var denominator = a.Item2 * b.Item2;
        var left = a.Item1 * b.Item2;
        var right = NonZero(b.Item1 * a.Item2, left);
        var remainder = left - (FloorDivide(left, right) * right);
        return PyFraction.Normalized(remainder, denominator);
    }

    private static PyObject Power((BigInteger, BigInteger) a, (BigInteger, BigInteger) b, bool reflected)
    {
        if (b.Item2.IsOne)
        {
            var exponent = b.Item1;

            if (BigInteger.Abs(exponent) > 1_000_000 && BigInteger.Abs(a.Item1) != a.Item2 && !a.Item1.IsZero)
            {
                throw new PyRaise(new PyException(PyExceptionType.OverflowError, "exponent too large"));
            }

            var power = (int)BigInteger.Clamp(BigInteger.Abs(exponent), 0, int.MaxValue);
            var numerator = BigInteger.Pow(a.Item1, power);
            var denominator = BigInteger.Pow(a.Item2, power);

            // An int raised to a non-negative integral Fraction is still an int, as CPython's
            // `__rpow__` answers.
            if (reflected && a.Item2.IsOne && exponent.Sign >= 0)
            {
                return PyInt.From(numerator);
            }

            if (exponent.Sign >= 0)
            {
                return new PyFraction(numerator, denominator);
            }

            if (numerator.IsZero)
            {
                throw new PyRaise(PyErrors.ZeroDivisionError($"Fraction({PyInt.Decimal(denominator)}, 0)"));
            }

            return PyFraction.Normalized(denominator, numerator);
        }

        // A fractional exponent leaves the rationals, and CPython answers in float.
        var baseValue = new PyFraction(a.Item1, a.Item2).ToDouble();
        var power2 = new PyFraction(b.Item1, b.Item2).ToDouble();
        return Operators.Binary("**", new PyFloat(baseValue), new PyFloat(power2));
    }

    /// <inheritdoc />
    public override string? PyFormat(string spec) => NumericFormat.FormatFraction(this, spec);

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "numerator" => PyInt.From(Numerator),
        "denominator" => PyInt.From(Denominator),
        "real" => this,
        "imag" => PyInt.From(0),
        "as_integer_ratio" => new PyBuiltinFunction("as_integer_ratio", _ =>
            new PyTuple([PyInt.From(Numerator), PyInt.From(Denominator)])),
        "is_integer" => new PyBuiltinFunction("is_integer", _ => PyBool.Of(Denominator.IsOne)),
        "conjugate" => new PyBuiltinFunction("conjugate", _ => this),
        "limit_denominator" => new PyBuiltinFunction("limit_denominator", (arguments, keywords) =>
        {
            var limit = arguments.Length > 0 ? arguments[0]
                : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "max_denominator").Value;
            return LimitDenominator(limit is null ? 1_000_000 : Builtins.BuiltinNamespace.RequireInt(limit, "limit_denominator"));
        }),
        "__floor__" => new PyBuiltinFunction("__floor__", _ => PyInt.From(Floor())),
        "__ceil__" => new PyBuiltinFunction("__ceil__", _ => PyInt.From(Ceiling())),
        "__trunc__" => new PyBuiltinFunction("__trunc__", _ => PyInt.From(Truncate())),
        "__round__" => new PyBuiltinFunction("__round__", arguments => Round(arguments.Length > 0 && arguments[0] is not PyNone ? arguments[0] : null)),
        "__class__" => FractionsModule.Type,
        _ => null,
    };

    /// <summary>The closest fraction with a denominator at most <paramref name="maximum"/>.</summary>
    private PyFraction LimitDenominator(BigInteger maximum)
    {
        if (maximum < 1)
        {
            throw new PyRaise(PyErrors.ValueError("max_denominator should be at least 1"));
        }

        if (Denominator <= maximum)
        {
            return this;
        }

        BigInteger p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        var (n, d) = (Numerator, Denominator);

        while (true)
        {
            var a = FloorDivide(n, d);
            var q2 = q0 + (a * q1);

            if (q2 > maximum)
            {
                break;
            }

            (p0, q0, p1, q1) = (p1, q1, p0 + (a * p1), q2);
            (n, d) = (d, n - (a * d));
        }

        var k = FloorDivide(maximum - q0, q1);

        return 2 * d * (q0 + (k * q1)) <= Denominator
            ? new PyFraction(p1, q1)
            : new PyFraction(p0 + (k * p1), q0 + (k * q1));
    }

    /// <summary>The digits of a rounded rational, for the format presentations.</summary>
    internal static (bool Negative, BigInteger Significand) RoundToExponent(
        BigInteger n, BigInteger d, int exponent, bool noNegativeZero = false)
    {
        if (exponent >= 0)
        {
            d *= BigInteger.Pow(10, exponent);
        }
        else
        {
            n *= BigInteger.Pow(10, -exponent);
        }

        var q = FloorDivide(n + (d >> 1), d);
        var r = n + (d >> 1) - (q * d);

        if (r.IsZero && d.IsEven)
        {
            q &= -2;
        }

        var negative = noNegativeZero ? q.Sign < 0 : n.Sign < 0;
        return (negative, BigInteger.Abs(q));
    }

    /// <summary>Rounds to a number of significant figures, ties to even.</summary>
    internal static (bool Negative, BigInteger Significand, int Exponent) RoundToFigures(
        BigInteger n, BigInteger d, int figures)
    {
        if (n.IsZero)
        {
            return (false, BigInteger.Zero, 1 - figures);
        }

        var textN = BigInteger.Abs(n).ToString(CultureInfo.InvariantCulture);
        var textD = d.ToString(CultureInfo.InvariantCulture);
        // CPython compares the digit strings as strings, lexicographically; either answer
        // is a valid bracket, and matching it keeps the rounding identical.
        var m = textN.Length - textD.Length + (string.CompareOrdinal(textD, textN) <= 0 ? 1 : 0);

        var exponent = m - figures;
        var (negative, significand) = RoundToExponent(n, d, exponent);

        if (significand.ToString(CultureInfo.InvariantCulture).Length == figures + 1)
        {
            significand /= 10;
            exponent++;
        }

        return (negative, significand, exponent);
    }

    /// <summary>Renders the value with a thousands separator, as the general presentation does.</summary>
    internal string Body(string separator, bool alternate)
    {
        var top = NumericFormat.Group(BigInteger.Abs(Numerator).ToString(CultureInfo.InvariantCulture), separator);

        if (Denominator > 1 || alternate)
        {
            var builder = new StringBuilder(top).Append('/');
            builder.Append(NumericFormat.Group(Denominator.ToString(CultureInfo.InvariantCulture), separator));
            return builder.ToString();
        }

        return top;
    }
}
