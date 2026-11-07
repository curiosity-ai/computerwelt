using System.Globalization;
using System.Numerics;
using System.Text;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python.Modules;

/// <summary>
/// A <c>decimal.Decimal</c>: a sign, a non-negative integer coefficient and an exponent.
/// </summary>
/// <remarks>
/// The arithmetic follows <c>_pydecimal</c>: compute exactly, then round once with
/// <see cref="Fix"/>. A value carries the run's module state, because the context an
/// operation rounds to is the one current when it runs, as <c>getcontext()</c> is in CPython.
/// </remarks>
public sealed class PyDecimal : PyObject, IPyNumber, IPyHostTyped
{
    /// <summary>The most digits an exact intermediate (a power, a rescale) may grow to.</summary>
    /// <remarks>
    /// The instruction limit does not count work done inside a builtin, so a request whose
    /// exact answer would run to millions of digits is refused, or approximated where
    /// CPython would round anyway, rather than computed.
    /// </remarks>
    private const int MaxExactDigits = 100_000;

    private PyDecimal(DecimalState state, DecimalKind kind, bool negative, BigInteger coefficient, long exponent)
    {
        State = state;
        Kind = kind;
        Negative = negative;
        Coefficient = coefficient;
        Exponent = exponent;
    }

    internal static PyDecimal Finite(DecimalState state, bool negative, BigInteger coefficient, long exponent) =>
        new(state, DecimalKind.Finite, negative, coefficient, exponent);

    internal static PyDecimal Special(DecimalState state, DecimalKind kind, bool negative, BigInteger payload) =>
        new(state, kind, negative, payload, 0);

    internal DecimalState State { get; }

    /// <summary>Finite, infinite or a NaN.</summary>
    public DecimalKind Kind { get; }

    /// <summary>The sign.</summary>
    public bool Negative { get; }

    /// <summary>The coefficient, or a NaN's diagnostic payload.</summary>
    public BigInteger Coefficient { get; }

    /// <summary>The power of ten the coefficient is scaled by.</summary>
    public long Exponent { get; }

    /// <summary>True for anything but a finite number.</summary>
    public bool IsSpecial => Kind != DecimalKind.Finite;

    /// <summary>True for a quiet or a signaling NaN.</summary>
    public bool IsNaN => Kind is DecimalKind.NaN or DecimalKind.SignalingNaN;

    /// <summary>True for an infinity.</summary>
    public bool IsInfinite => Kind == DecimalKind.Infinity;

    private bool IsZero => Kind == DecimalKind.Finite && Coefficient.IsZero;

    private DecimalContext Context => State.Current;

    /// <inheritdoc />
    PyObject IPyHostTyped.Type => State.Type ?? TypeRegistry.Object;

    /// <inheritdoc />
    public override string TypeName => "decimal.Decimal";

    // ---- digits ----

    /// <summary>The number of decimal digits in a coefficient; zero has one.</summary>
    internal static int DigitCount(BigInteger value)
    {
        if (value.IsZero)
        {
            return 1;
        }

        value = BigInteger.Abs(value);
        var estimate = (int)((value.GetBitLength() - 1) * 0.30102999566398120) + 1;
        var power = BigInteger.Pow(10, estimate - 1);

        while (value < power)
        {
            estimate--;
            power /= 10;
        }

        while (value >= power * 10)
        {
            estimate++;
            power *= 10;
        }

        return estimate;
    }

    private static BigInteger Pow10(long exponent) =>
        exponent > MaxExactDigits * 2L
            ? throw new PyRaise(new PyException(PyExceptionType.OverflowError, "exponent too large"))
            : BigInteger.Pow(10, (int)exponent);

    /// <summary>The exponent of the most significant digit.</summary>
    public long Adjusted => IsSpecial ? 0 : Exponent + DigitCount(Coefficient) - 1;

    private PyDecimal With(bool negative, BigInteger coefficient, long exponent) =>
        Finite(State, negative, coefficient, exponent);

    internal PyDecimal WithExponent(long exponent) => With(Negative, Coefficient, exponent);

    // ---- rounding ----

    /// <summary>
    /// Splits off the lowest <paramref name="drop"/> digits and decides, by the rounding
    /// mode, whether what is kept goes up by one.
    /// </summary>
    private static (BigInteger Kept, bool Inexact) RoundOff(BigInteger coefficient, long drop, string mode, bool negative)
    {
        if (drop <= 0)
        {
            return (coefficient, false);
        }

        BigInteger kept;
        int half;
        bool inexact;

        if (drop > DigitCount(coefficient) + 1)
        {
            // Everything goes, and what goes is nonzero but less than half a unit.
            kept = BigInteger.Zero;
            inexact = !coefficient.IsZero;
            half = inexact ? -1 : -2;
        }
        else
        {
            var unit = BigInteger.Pow(10, (int)drop);
            kept = BigInteger.DivRem(coefficient, unit, out var remainder);
            inexact = !remainder.IsZero;
            half = !inexact ? -2 : (remainder * 2).CompareTo(unit);
        }

        // half: -2 nothing dropped, -1 below a half, 0 exactly a half, 1 above.
        var up = mode switch
        {
            "ROUND_DOWN" => false,
            "ROUND_UP" => inexact,
            "ROUND_HALF_UP" => half >= 0,
            "ROUND_HALF_DOWN" => half > 0,
            "ROUND_HALF_EVEN" => half > 0 || (half == 0 && !kept.IsEven),
            "ROUND_CEILING" => inexact && !negative,
            "ROUND_FLOOR" => inexact && negative,
            "ROUND_05UP" => inexact && (kept % 10 == 0 || kept % 10 == 5),
            _ => false,
        };

        return (up ? kept + 1 : kept, inexact);
    }

    /// <summary><c>_rescale</c>: the value at exponent <paramref name="exponent"/>, rounded.</summary>
    internal PyDecimal Rescale(long exponent, string rounding)
    {
        if (IsSpecial)
        {
            return this;
        }

        if (Coefficient.IsZero)
        {
            return With(Negative, BigInteger.Zero, exponent);
        }

        if (Exponent >= exponent)
        {
            if (Exponent - exponent + DigitCount(Coefficient) > MaxExactDigits)
            {
                throw DecimalModule.Signal(DecimalModule.InvalidOperation);
            }

            return With(Negative, Coefficient * Pow10(Exponent - exponent), exponent);
        }

        var (kept, _) = RoundOff(Coefficient, exponent - Exponent, rounding, Negative);
        return With(Negative, kept, exponent);
    }

    /// <summary><c>_round</c>: the value rounded to <paramref name="places"/> significant digits.</summary>
    internal PyDecimal RoundToDigits(int places, string rounding)
    {
        if (IsSpecial || IsZero)
        {
            return this;
        }

        var answer = Rescale(Adjusted + 1 - places, rounding);

        if (answer.Adjusted != Adjusted)
        {
            answer = answer.Rescale(answer.Adjusted + 1 - places, rounding);
        }

        return answer;
    }

    /// <summary>
    /// <c>_fix</c>: rounds to the context's precision and checks the exponent limits.
    /// </summary>
    internal PyDecimal Fix(DecimalContext context)
    {
        if (IsNaN)
        {
            // A payload longer than the precision allows is truncated to its low digits.
            var limit = context.Precision - context.Clamp;
            return DigitCount(Coefficient) > limit && !Coefficient.IsZero
                ? Special(State, Kind, Negative, Coefficient % BigInteger.Pow(10, limit))
                : this;
        }

        if (IsInfinite)
        {
            return this;
        }

        var etiny = context.Etiny;
        var etop = context.Etop;

        if (Coefficient.IsZero)
        {
            var expMax = context.Clamp == 0 ? context.Emax : etop;
            var clamped = Math.Min(Math.Max(Exponent, etiny), expMax);
            return clamped == Exponent ? this : With(Negative, BigInteger.Zero, clamped);
        }

        var digits = DigitCount(Coefficient);
        var expMin = digits + Exponent - context.Precision;

        if (expMin > etop)
        {
            throw DecimalModule.Signal(DecimalModule.Overflow);
        }

        if (expMin < etiny)
        {
            expMin = etiny;
        }

        if (Exponent < expMin)
        {
            var (kept, _) = RoundOff(Coefficient, expMin - Exponent, context.Rounding, Negative);

            if (DigitCount(kept) > context.Precision)
            {
                kept /= 10;
                expMin++;
            }

            var rounded = With(Negative, kept, expMin);

            if (!kept.IsZero && rounded.Adjusted > context.Emax)
            {
                throw DecimalModule.Signal(DecimalModule.Overflow);
            }

            return rounded;
        }

        if (context.Clamp == 1 && Exponent > etop)
        {
            return With(Negative, Coefficient * Pow10(Exponent - etop), etop);
        }

        return this;
    }

    // ---- conversion ----

    /// <summary>The operand as a Decimal, for an int or a Decimal; null for anything else.</summary>
    private PyDecimal? Convert(PyObject other) => other switch
    {
        PyDecimal number => number,
        PyBool flag => Finite(State, false, flag.Value ? BigInteger.One : BigInteger.Zero, 0),
        PyInt integer => Finite(State, integer.Value.Sign < 0, BigInteger.Abs(integer.Value), 0),
        _ => null,
    };

    /// <summary>The operand as a Decimal for a comparison, which also takes a float exactly.</summary>
    private PyDecimal? ConvertForComparison(PyObject other) => other switch
    {
        PyFloat number => DecimalModule.FromDouble(State, number.Value),
        _ => Convert(other),
    };

    /// <summary>The exact value as a fraction.</summary>
    internal (BigInteger Numerator, BigInteger Denominator) Ratio()
    {
        var signed = Negative ? -Coefficient : Coefficient;

        if (Exponent >= 0)
        {
            return (signed * Pow10(Exponent), BigInteger.One);
        }

        var denominator = Pow10(-Exponent);
        var divisor = BigInteger.GreatestCommonDivisor(signed, denominator);
        return (signed / divisor, denominator / divisor);
    }

    /// <summary>A quiet NaN result for a NaN operand, raising for a signaling one.</summary>
    private PyDecimal? NaNResult(PyDecimal? other, DecimalContext context)
    {
        if (Kind == DecimalKind.SignalingNaN || other?.Kind == DecimalKind.SignalingNaN)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        if (IsNaN)
        {
            return Fix(context);
        }

        return other is { IsNaN: true } ? other.Fix(context) : null;
    }

    // ---- text ----

    /// <inheritdoc />
    public override string Repr() => $"Decimal('{ToText(engineering: false)}')";

    /// <inheritdoc />
    public override string Display() => ToText(engineering: false);

    /// <summary>The to-scientific-string (or engineering) conversion.</summary>
    internal string ToText(bool engineering)
    {
        var sign = Negative ? "-" : string.Empty;

        switch (Kind)
        {
            case DecimalKind.Infinity:
                return sign + "Infinity";
            case DecimalKind.NaN:
                return sign + "NaN" + (Coefficient.IsZero ? string.Empty : Coefficient.ToString(CultureInfo.InvariantCulture));
            case DecimalKind.SignalingNaN:
                return sign + "sNaN" + (Coefficient.IsZero ? string.Empty : Coefficient.ToString(CultureInfo.InvariantCulture));
        }

        var digits = Coefficient.ToString(CultureInfo.InvariantCulture);
        var leftDigits = Exponent + digits.Length;
        long dotPlace;

        if (Exponent <= 0 && leftDigits > -6)
        {
            dotPlace = leftDigits;
        }
        else if (!engineering)
        {
            dotPlace = 1;
        }
        else if (Coefficient.IsZero)
        {
            dotPlace = Mod(leftDigits + 1, 3) - 1;
        }
        else
        {
            dotPlace = Mod(leftDigits - 1, 3) + 1;
        }

        string integerPart;
        string fractionPart;

        if (dotPlace <= 0)
        {
            integerPart = "0";
            fractionPart = "." + new string('0', (int)-dotPlace) + digits;
        }
        else if (dotPlace >= digits.Length)
        {
            integerPart = digits + new string('0', (int)(dotPlace - digits.Length));
            fractionPart = string.Empty;
        }
        else
        {
            integerPart = digits[..(int)dotPlace];
            fractionPart = "." + digits[(int)dotPlace..];
        }

        var exponent = leftDigits == dotPlace
            ? string.Empty
            : (Context.Capitals ? "E" : "e") + (leftDigits - dotPlace >= 0 ? "+" : "-")
                + Math.Abs(leftDigits - dotPlace).ToString(CultureInfo.InvariantCulture);

        return sign + integerPart + fractionPart + exponent;
    }

    private static long Mod(long value, long divisor) => ((value % divisor) + divisor) % divisor;

    /// <inheritdoc />
    public override string? PyFormat(string spec) => NumericFormat.FormatDecimal(this, spec, Context);

    /// <inheritdoc />
    public override bool IsTruthy() => IsSpecial || !Coefficient.IsZero;

    // ---- comparison and hashing ----

    /// <summary>Orders two non-NaN values exactly.</summary>
    private static int CompareValues(PyDecimal a, PyDecimal b)
    {
        if (a.IsInfinite || b.IsInfinite)
        {
            var left = a.IsInfinite ? (a.Negative ? -1 : 1) : 0;
            var right = b.IsInfinite ? (b.Negative ? -1 : 1) : 0;

            if (left != right)
            {
                return left.CompareTo(right);
            }

            if (left != 0)
            {
                return 0;
            }
        }

        if (a.IsZero || b.IsZero)
        {
            var left = a.IsZero ? 0 : (a.Negative ? -1 : 1);
            var right = b.IsZero ? 0 : (b.Negative ? -1 : 1);
            return left.CompareTo(right);
        }

        if (a.Negative != b.Negative)
        {
            return a.Negative ? -1 : 1;
        }

        var sign = a.Negative ? -1 : 1;

        if (a.Adjusted != b.Adjusted)
        {
            return a.Adjusted > b.Adjusted ? sign : -sign;
        }

        // Equal adjusted exponents: the exponents differ by no more than the digit counts,
        // so aligning the coefficients is cheap.
        var shift = a.Exponent - b.Exponent;
        var x = shift > 0 ? a.Coefficient * BigInteger.Pow(10, (int)shift) : a.Coefficient;
        var y = shift < 0 ? b.Coefficient * BigInteger.Pow(10, (int)-shift) : b.Coefficient;
        return sign * x.CompareTo(y);
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other)
    {
        if (other is PyFraction fraction)
        {
            if (IsNaN)
            {
                return false;
            }

            if (IsInfinite)
            {
                return false;
            }

            var (n, d) = Ratio();
            return n == fraction.Numerator && d == fraction.Denominator;
        }

        if (ConvertForComparison(other) is not { } operand)
        {
            return false;
        }

        if (Kind == DecimalKind.SignalingNaN || operand.Kind == DecimalKind.SignalingNaN)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        return !IsNaN && !operand.IsNaN && CompareValues(this, operand) == 0;
    }

    /// <inheritdoc />
    public override int? PyCompare(PyObject other)
    {
        if (other is PyFraction fraction)
        {
            if (IsNaN)
            {
                throw DecimalModule.Signal(DecimalModule.InvalidOperation);
            }

            if (IsInfinite)
            {
                return Negative ? -1 : 1;
            }

            var (n, d) = Ratio();
            return (n * fraction.Denominator).CompareTo(fraction.Numerator * d);
        }

        if (ConvertForComparison(other) is not { } operand)
        {
            return null;
        }

        if (IsNaN || operand.IsNaN)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        return CompareValues(this, operand);
    }

    /// <inheritdoc />
    public override BigInteger PyHash()
    {
        switch (Kind)
        {
            case DecimalKind.SignalingNaN:
                throw new PyRaise(PyErrors.TypeError("Cannot hash a signaling NaN value."));
            case DecimalKind.NaN:
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
            case DecimalKind.Infinity:
                return Negative ? -314159 : 314159;
        }

        var modulus = (BigInteger.One << 61) - 1;
        var exponentHash = Exponent >= 0
            ? BigInteger.ModPow(10, Exponent, modulus)
            : BigInteger.ModPow(BigInteger.ModPow(10, modulus - 2, modulus), -Exponent, modulus);
        var hash = Coefficient * exponentHash % modulus;
        var answer = Negative ? -hash : hash;
        return answer == BigInteger.MinusOne ? -2 : answer;
    }

    // ---- IPyNumber ----

    /// <inheritdoc />
    public double ToDouble()
    {
        if (IsNaN)
        {
            return Kind == DecimalKind.SignalingNaN
                ? throw new PyRaise(PyErrors.ValueError("cannot convert signaling NaN to float"))
                : Negative ? -double.NaN : double.NaN;
        }

        if (IsInfinite)
        {
            return Negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (Coefficient.IsZero)
        {
            return Negative ? -0.0 : 0.0;
        }

        // Far outside the double range the answer is decided by the exponent alone.
        if (Adjusted > 400)
        {
            return Negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (Adjusted < -400)
        {
            return Negative ? -0.0 : 0.0;
        }

        var text = Coefficient.ToString(CultureInfo.InvariantCulture) + "E" + Exponent.ToString(CultureInfo.InvariantCulture);
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return Negative ? -value : value;
    }

    private void RequireFinite()
    {
        if (IsNaN)
        {
            throw new PyRaise(PyErrors.ValueError("cannot convert NaN to integer"));
        }

        if (IsInfinite)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "cannot convert Infinity to integer"));
        }
    }

    private (BigInteger Quotient, bool Exact) Integral()
    {
        RequireFinite();

        if (Exponent >= 0)
        {
            return (Coefficient * Pow10(Exponent), true);
        }

        if (-Exponent > DigitCount(Coefficient))
        {
            return (BigInteger.Zero, Coefficient.IsZero);
        }

        var quotient = BigInteger.DivRem(Coefficient, BigInteger.Pow(10, (int)-Exponent), out var remainder);
        return (quotient, remainder.IsZero);
    }

    /// <inheritdoc />
    public BigInteger Truncate()
    {
        var (quotient, _) = Integral();
        return Negative ? -quotient : quotient;
    }

    /// <inheritdoc />
    public BigInteger Floor()
    {
        var (quotient, exact) = Integral();
        return Negative ? -quotient - (exact ? 0 : 1) : quotient;
    }

    /// <inheritdoc />
    public BigInteger Ceiling()
    {
        var (quotient, exact) = Integral();
        return Negative ? -quotient : quotient + (exact ? 0 : 1);
    }

    /// <inheritdoc />
    public PyObject Round(PyObject? digits)
    {
        if (digits is null)
        {
            if (IsNaN)
            {
                throw new PyRaise(PyErrors.ValueError("cannot round a NaN"));
            }

            if (IsInfinite)
            {
                throw new PyRaise(new PyException(PyExceptionType.OverflowError, "cannot round an infinity"));
            }

            return PyInt.From(Rescale(0, "ROUND_HALF_EVEN").Truncate());
        }

        var places = Builtins.BuiltinNamespace.RequireInt(digits, "round");
        var target = Finite(State, false, BigInteger.One, -(long)BigInteger.Clamp(places, -999_999_999_999L, 999_999_999_999L));
        return Quantize(target, null);
    }

    /// <inheritdoc />
    public PyObject? Unary(string op)
    {
        var context = Context;

        switch (op)
        {
            case "-":
            {
                if (NaNResult(null, context) is { } nan)
                {
                    return nan;
                }

                var negated = IsZero && context.Rounding != "ROUND_FLOOR"
                    ? With(false, Coefficient, Exponent)
                    : IsInfinite ? Special(State, Kind, !Negative, BigInteger.Zero) : With(!Negative, Coefficient, Exponent);
                return negated.Fix(context);
            }

            case "+":
                return Plus(context);

            case "abs":
                return Negative ? Unary("-") : Plus(context);

            default:
                return null;
        }
    }

    private PyDecimal Plus(DecimalContext context)
    {
        if (NaNResult(null, context) is { } nan)
        {
            return nan;
        }

        var positive = IsZero && context.Rounding != "ROUND_FLOOR" ? With(false, Coefficient, Exponent) : this;
        return positive.Fix(context);
    }

    /// <inheritdoc />
    public PyObject? Binary(string op, PyObject other, bool reflected)
    {
        if (op is not ("+" or "-" or "*" or "/" or "//" or "%" or "**"))
        {
            return null;
        }

        if (Convert(other) is not { } operand)
        {
            return null;
        }

        var (a, b) = reflected ? (operand, this) : (this, operand);
        var context = Context;

        return op switch
        {
            "+" => Add(a, b, negateSecond: false, context),
            "-" => Add(a, b, negateSecond: true, context),
            "*" => Multiply(a, b, context),
            "/" => Divide(a, b, context),
            "//" => DivideInteger(a, b, context).Quotient,
            // A remainder by zero is undefined rather than infinite, so it is an
            // InvalidOperation where `//` is a DivisionByZero.
            "%" => b.IsZero && !a.IsNaN
                ? throw DecimalModule.Signal(DecimalModule.InvalidOperation)
                : DivideInteger(a, b, context).Remainder,
            _ => Power(a, b, context),
        };
    }

    // ---- arithmetic ----

    private static PyDecimal Add(PyDecimal a, PyDecimal b, bool negateSecond, DecimalContext context)
    {
        if (a.NaNResult(b, context) is { } nan)
        {
            return nan;
        }

        var bNegative = negateSecond ? !b.Negative : b.Negative;

        if (a.IsInfinite)
        {
            return b.IsInfinite && a.Negative != bNegative
                ? throw DecimalModule.Signal(DecimalModule.InvalidOperation)
                : a;
        }

        if (b.IsInfinite)
        {
            return Special(a.State, DecimalKind.Infinity, bNegative, BigInteger.Zero);
        }

        var negativeZero = context.Rounding == "ROUND_FLOOR" && a.Negative != bNegative;
        var exponent = Math.Min(a.Exponent, b.Exponent);

        if (a.Coefficient.IsZero && b.Coefficient.IsZero)
        {
            var sign = (a.Negative && bNegative) || negativeZero;
            return Finite(a.State, sign, BigInteger.Zero, exponent).Fix(context);
        }

        if (a.Coefficient.IsZero)
        {
            exponent = Math.Max(exponent, b.Exponent - context.Precision - 1);
            return Finite(b.State, bNegative, b.Coefficient, b.Exponent).Rescale(exponent, context.Rounding).Fix(context);
        }

        if (b.Coefficient.IsZero)
        {
            exponent = Math.Max(exponent, a.Exponent - context.Precision - 1);
            return a.Rescale(exponent, context.Rounding).Fix(context);
        }

        // _normalize: when one operand is far below the other's last digit, only its sign
        // and the fact that it is there matter, so it is replaced by a single digit just
        // below — which rounds identically without building the aligned coefficient.
        var (x, xExp) = (a.Coefficient, a.Exponent);
        var (y, yExp) = (b.Coefficient, b.Exponent);
        var xIsTop = xExp >= yExp;
        var (topInt, topExp, otherInt, otherExp) = xIsTop ? (x, xExp, y, yExp) : (y, yExp, x, xExp);
        var limit = topExp + Math.Min(-1, DigitCount(topInt) - context.Precision - 2);

        if (DigitCount(otherInt) + otherExp - 1 < limit)
        {
            otherInt = BigInteger.One;
            otherExp = limit;
        }

        topInt *= Pow10(topExp - otherExp);
        topExp = otherExp;
        (x, xExp, y, yExp) = xIsTop ? (topInt, topExp, otherInt, otherExp) : (otherInt, otherExp, topInt, topExp);
        _ = yExp;

        var signedX = a.Negative ? -x : x;
        var signedY = bNegative ? -y : y;
        var sum = signedX + signedY;

        if (sum.IsZero)
        {
            return Finite(a.State, negativeZero, BigInteger.Zero, xExp).Fix(context);
        }

        return Finite(a.State, sum.Sign < 0, BigInteger.Abs(sum), xExp).Fix(context);
    }

    private static PyDecimal Multiply(PyDecimal a, PyDecimal b, DecimalContext context)
    {
        if (a.NaNResult(b, context) is { } nan)
        {
            return nan;
        }

        var negative = a.Negative != b.Negative;

        if (a.IsInfinite || b.IsInfinite)
        {
            return (a.IsInfinite && b.IsZero) || (b.IsInfinite && a.IsZero)
                ? throw DecimalModule.Signal(DecimalModule.InvalidOperation)
                : Special(a.State, DecimalKind.Infinity, negative, BigInteger.Zero);
        }

        return Finite(a.State, negative, a.Coefficient * b.Coefficient, a.Exponent + b.Exponent).Fix(context);
    }

    private static PyDecimal Divide(PyDecimal a, PyDecimal b, DecimalContext context)
    {
        if (a.NaNResult(b, context) is { } nan)
        {
            return nan;
        }

        var negative = a.Negative != b.Negative;

        if (a.IsInfinite)
        {
            return b.IsInfinite
                ? throw DecimalModule.Signal(DecimalModule.InvalidOperation)
                : Special(a.State, DecimalKind.Infinity, negative, BigInteger.Zero);
        }

        if (b.IsInfinite)
        {
            return Finite(a.State, negative, BigInteger.Zero, context.Etiny);
        }

        if (b.IsZero)
        {
            throw DecimalModule.Signal(a.IsZero ? DecimalModule.InvalidOperation : DecimalModule.DivisionByZero);
        }

        BigInteger coefficient;
        long exponent;

        if (a.IsZero)
        {
            exponent = a.Exponent - b.Exponent;
            coefficient = BigInteger.Zero;
        }
        else
        {
            var shift = DigitCount(b.Coefficient) - DigitCount(a.Coefficient) + context.Precision + 1;
            exponent = a.Exponent - b.Exponent - shift;
            BigInteger remainder;

            if (shift >= 0)
            {
                coefficient = BigInteger.DivRem(a.Coefficient * BigInteger.Pow(10, shift), b.Coefficient, out remainder);
            }
            else
            {
                coefficient = BigInteger.DivRem(a.Coefficient, b.Coefficient * BigInteger.Pow(10, -shift), out remainder);
            }

            if (!remainder.IsZero)
            {
                if (coefficient % 5 == 0)
                {
                    coefficient += 1;
                }
            }
            else
            {
                var ideal = a.Exponent - b.Exponent;

                while (exponent < ideal && coefficient % 10 == 0)
                {
                    coefficient /= 10;
                    exponent++;
                }
            }
        }

        return Finite(a.State, negative, coefficient, exponent).Fix(context);
    }

    /// <summary><c>//</c> and <c>%</c>: the quotient truncated towards zero and its remainder.</summary>
    private static (PyDecimal Quotient, PyDecimal Remainder) DivideInteger(PyDecimal a, PyDecimal b, DecimalContext context)
    {
        if (a.NaNResult(b, context) is { } nan)
        {
            return (nan, nan);
        }

        var negative = a.Negative != b.Negative;

        if (a.IsInfinite)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        if (b.IsZero)
        {
            throw DecimalModule.Signal(a.IsZero ? DecimalModule.InvalidOperation : DecimalModule.DivisionByZero);
        }

        var ideal = Math.Min(a.Exponent, b.Exponent);

        if (b.IsInfinite)
        {
            return (Finite(a.State, negative, BigInteger.Zero, 0), a.Fix(context));
        }

        var difference = a.Adjusted - b.Adjusted;

        if (a.IsZero || difference <= -2)
        {
            return (Finite(a.State, negative, BigInteger.Zero, 0),
                a.Rescale(ideal, context.Rounding).Fix(context));
        }

        if (difference <= context.Precision)
        {
            var x = a.Coefficient;
            var y = b.Coefficient;

            if (a.Exponent >= b.Exponent)
            {
                x *= BigInteger.Pow(10, (int)(a.Exponent - b.Exponent));
            }
            else
            {
                y *= BigInteger.Pow(10, (int)(b.Exponent - a.Exponent));
            }

            var quotient = BigInteger.DivRem(x, y, out var remainder);

            if (quotient < BigInteger.Pow(10, context.Precision))
            {
                return (Finite(a.State, negative, quotient, 0).Fix(context),
                    Finite(a.State, a.Negative, remainder, ideal).Fix(context));
            }
        }

        // The quotient would not fit the precision: CPython signals DivisionImpossible,
        // which is an InvalidOperation.
        throw DecimalModule.Signal(DecimalModule.InvalidOperation);
    }

    private bool IsInteger =>
        Kind == DecimalKind.Finite && (Exponent >= 0 || Coefficient.IsZero
            || (-Exponent <= DigitCount(Coefficient) && (Coefficient % BigInteger.Pow(10, (int)-Exponent)).IsZero));

    private static PyDecimal Power(PyDecimal a, PyDecimal b, DecimalContext context)
    {
        if (a.NaNResult(b, context) is { } nan)
        {
            return nan;
        }

        var state = a.State;

        if (b.IsZero)
        {
            return a.IsZero ? throw DecimalModule.Signal(DecimalModule.InvalidOperation) : Finite(state, false, BigInteger.One, 0);
        }

        var negative = false;

        if (a.Negative)
        {
            if (!b.IsInteger)
            {
                if (!a.IsZero)
                {
                    throw DecimalModule.Signal(DecimalModule.InvalidOperation);
                }
            }
            else
            {
                negative = !b.Truncate().IsEven;
            }
        }

        if (a.IsZero)
        {
            return b.Negative
                ? Special(state, DecimalKind.Infinity, negative, BigInteger.Zero)
                : Finite(state, negative, BigInteger.Zero, 0);
        }

        if (a.IsInfinite)
        {
            return b.Negative
                ? Finite(state, negative, BigInteger.Zero, 0)
                : Special(state, DecimalKind.Infinity, negative, BigInteger.Zero);
        }

        if (b.IsInfinite)
        {
            var magnitude = CompareValues(Finite(state, false, a.Coefficient, a.Exponent), Finite(state, false, BigInteger.One, 0));

            if (magnitude == 0)
            {
                return Finite(state, false, BigInteger.One, 0).Rescale(-context.Precision + 1, context.Rounding).Fix(context);
            }

            return (magnitude > 0) != b.Negative
                ? Special(state, DecimalKind.Infinity, false, BigInteger.Zero)
                : Finite(state, false, BigInteger.Zero, 0);
        }

        if (b.IsInteger)
        {
            var exponent = b.Truncate();
            var size = BigInteger.Abs(exponent);
            var baseDigits = DigitCount(a.Coefficient);

            // A result far past the exponent limits overflows or vanishes without being built.
            var estimate = (double)exponent * a.Log10Estimate();

            if (estimate > context.Emax + 1)
            {
                throw DecimalModule.Signal(DecimalModule.Overflow);
            }

            if (estimate < context.Etiny - 2)
            {
                return Finite(state, negative, BigInteger.Zero, context.Etiny);
            }

            PyDecimal exact;

            if (size * baseDigits <= MaxExactDigits)
            {
                var n = (int)size;
                exact = Finite(state, negative, BigInteger.Pow(a.Coefficient, n), a.Exponent * n);
            }
            else
            {
                // Too many digits to hold exactly: square and multiply at a working precision
                // with guard digits, rounding as it goes.
                var working = context.Copy();
                working.SetAttribute("prec", PyInt.From(Math.Min(100_000, context.Precision + DigitCount(size) + 10)));
                var result = Finite(state, false, BigInteger.One, 0);
                var square = Finite(state, false, a.Coefficient, a.Exponent);
                var remaining = size;

                while (!remaining.IsZero)
                {
                    if (!remaining.IsEven)
                    {
                        result = Multiply(result, square, working);
                    }

                    remaining >>= 1;

                    if (!remaining.IsZero)
                    {
                        square = Multiply(square, square, working);
                    }
                }

                exact = Finite(state, negative, result.Coefficient, result.Exponent);
            }

            return exponent.Sign >= 0
                ? exact.Fix(context)
                : Divide(Finite(state, false, BigInteger.One, 0), exact, context);
        }

        // A fractional exponent: a square root exactly, anything else through a double,
        // which is correctly rounded only up to its own 17 digits.
        if (b is { Negative: false, Coefficient: var c, Exponent: var e } && c * 2 == BigInteger.Pow(10, (int)-e))
        {
            return a.SquareRoot(context);
        }

        var approximate = Math.Pow(a.ToDouble(), b.ToDouble());

        if (double.IsInfinity(approximate))
        {
            throw DecimalModule.Signal(DecimalModule.Overflow);
        }

        var digits = approximate.ToString("E16", CultureInfo.InvariantCulture);
        return DecimalModule.Construct(state, new PyStr(digits), null).Fix(context);
    }

    /// <summary>log10 of the magnitude, to a double's accuracy, for sizing a result.</summary>
    private double Log10Estimate()
    {
        var digits = Coefficient.ToString(CultureInfo.InvariantCulture);
        var leading = digits.Length > 15 ? digits[..15] : digits;
        var mantissa = double.Parse(leading, CultureInfo.InvariantCulture) / Math.Pow(10, leading.Length - 1);
        return Adjusted + Math.Log10(mantissa);
    }

    /// <summary><c>sqrt</c>, correctly rounded with ROUND_HALF_EVEN, exactly as <c>_pydecimal</c> computes it.</summary>
    internal PyDecimal SquareRoot(DecimalContext context)
    {
        if (NaNResult(null, context) is { } nan)
        {
            return nan;
        }

        if (IsInfinite && !Negative)
        {
            return this;
        }

        if (IsZero)
        {
            return Finite(State, Negative, BigInteger.Zero, FloorHalf(Exponent)).Fix(context);
        }

        if (Negative)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        var precision = context.Precision + 1;
        var exponent = FloorHalf(Exponent);
        BigInteger c;
        int l;
        var digits = DigitCount(Coefficient);

        if ((Exponent & 1) != 0)
        {
            c = Coefficient * 10;
            l = (digits >> 1) + 1;
        }
        else
        {
            c = Coefficient;
            l = (digits + 1) >> 1;
        }

        var shift = precision - l;
        bool exact;

        if (shift >= 0)
        {
            c *= BigInteger.Pow(100, shift);
            exact = true;
        }
        else
        {
            c = BigInteger.DivRem(c, BigInteger.Pow(100, -shift), out var remainder);
            exact = remainder.IsZero;
        }

        exponent -= shift;
        var n = BigInteger.Pow(10, precision);

        while (true)
        {
            var q = c / n;

            if (n <= q)
            {
                break;
            }

            n = (n + q) >> 1;
        }

        exact = exact && n * n == c;

        if (exact)
        {
            if (shift >= 0)
            {
                n /= BigInteger.Pow(10, shift);
            }
            else
            {
                n *= BigInteger.Pow(10, -shift);
            }

            exponent += shift;
        }
        else if (n % 5 == 0)
        {
            n += 1;
        }

        var halfEven = context.Copy();
        halfEven.SetAttribute("rounding", new PyStr("ROUND_HALF_EVEN"));
        return Finite(State, false, n, exponent).Fix(halfEven);
    }

    private static long FloorHalf(long value) => value >= 0 ? value / 2 : -((-value + 1) / 2);

    /// <summary><c>quantize</c>: the value at the exponent of <paramref name="target"/>.</summary>
    internal PyDecimal Quantize(PyDecimal target, string? rounding)
    {
        var context = Context;
        var mode = rounding ?? context.Rounding;

        if (NaNResult(target, context) is { } nan)
        {
            return nan;
        }

        if (IsInfinite || target.IsInfinite)
        {
            return IsInfinite && target.IsInfinite ? this : throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        if (target.Exponent < context.Etiny || target.Exponent > context.Emax)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        if (IsZero)
        {
            return With(Negative, BigInteger.Zero, target.Exponent).Fix(context);
        }

        if (Adjusted > context.Emax || Adjusted - target.Exponent + 1 > context.Precision)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        var answer = Rescale(target.Exponent, mode);

        if (answer.Adjusted > context.Emax || DigitCount(answer.Coefficient) > context.Precision)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        return answer.Fix(context);
    }

    private PyDecimal Normalize(DecimalContext context)
    {
        if (NaNResult(null, context) is { } nan)
        {
            return nan;
        }

        var fixedValue = Fix(context);

        if (fixedValue.IsInfinite)
        {
            return fixedValue;
        }

        if (fixedValue.Coefficient.IsZero)
        {
            return With(fixedValue.Negative, BigInteger.Zero, 0);
        }

        var expMax = context.Clamp == 0 ? context.Emax : context.Etop;
        var coefficient = fixedValue.Coefficient;
        var exponent = fixedValue.Exponent;

        while (coefficient % 10 == 0 && exponent < expMax)
        {
            coefficient /= 10;
            exponent++;
        }

        return With(fixedValue.Negative, coefficient, exponent);
    }

    private PyDecimal ToIntegral(string? rounding)
    {
        if (NaNResult(null, Context) is { } nan)
        {
            return nan;
        }

        return IsInfinite || Exponent >= 0 ? this : Rescale(0, rounding ?? Context.Rounding);
    }

    private PyDecimal RequireOperand(PyObject value, string name) =>
        Convert(value) ?? throw new PyRaise(PyErrors.TypeError($"conversion from {value.TypeName} to Decimal is not supported"));

    private static string? RoundingArgument(PyObject[] arguments, PyDict? keywords, int position)
    {
        var value = arguments.Length > position ? arguments[position]
            : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "rounding").Value;

        return value switch
        {
            null or PyNone => null,
            PyStr { Value: var mode } when DecimalModule.RoundingModes.Contains(mode) => mode,
            _ => throw new PyRaise(PyErrors.TypeError("valid values for rounding are: [ROUND_CEILING, ROUND_FLOOR, ROUND_UP, ROUND_DOWN, ROUND_HALF_UP, ROUND_HALF_DOWN, ROUND_HALF_EVEN, ROUND_05UP]")),
        };
    }

    private PyDecimal Extreme(PyDecimal other, bool maximum)
    {
        var context = Context;

        if (Kind == DecimalKind.SignalingNaN || other.Kind == DecimalKind.SignalingNaN)
        {
            throw DecimalModule.Signal(DecimalModule.InvalidOperation);
        }

        if (IsNaN && other.IsNaN)
        {
            return Fix(context);
        }

        if (IsNaN)
        {
            return other.Fix(context);
        }

        if (other.IsNaN)
        {
            return Fix(context);
        }

        var order = CompareValues(this, other);

        if (order == 0)
        {
            // Equal values: the sign, then the exponent, decides (compare_total's order).
            order = Negative != other.Negative ? (Negative ? -1 : 1) : Exponent.CompareTo(other.Exponent) * (Negative ? -1 : 1);
        }

        var chosen = maximum ? (order >= 0 ? this : other) : (order <= 0 ? this : other);
        return chosen.Fix(context);
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name)
    {
        switch (name)
        {
            case "real":
                return this;
            case "imag":
                return Finite(State, false, BigInteger.Zero, 0);
            case "__class__":
                return ((IPyHostTyped)this).Type;
        }

        return name switch
        {
            "adjusted" => Method(name, _ => PyInt.From(Adjusted)),
            "as_tuple" => Method(name, _ => AsTuple()),
            "as_integer_ratio" => Method(name, _ =>
            {
                RequireFiniteRatio();
                var (n, d) = Ratio();
                return new PyTuple([PyInt.From(n), PyInt.From(d)]);
            }),
            "conjugate" => Method(name, _ => this),
            "copy_abs" => Method(name, _ => IsSpecial ? Special(State, Kind, false, Coefficient) : With(false, Coefficient, Exponent)),
            "copy_negate" => Method(name, _ => IsSpecial ? Special(State, Kind, !Negative, Coefficient) : With(!Negative, Coefficient, Exponent)),
            "copy_sign" => Method(name, arguments =>
            {
                var other = RequireOperand(arguments[0], name);
                return IsSpecial ? Special(State, Kind, other.Negative, Coefficient) : With(other.Negative, Coefficient, Exponent);
            }),
            "is_nan" => Method(name, _ => PyBool.Of(IsNaN)),
            "is_qnan" => Method(name, _ => PyBool.Of(Kind == DecimalKind.NaN)),
            "is_snan" => Method(name, _ => PyBool.Of(Kind == DecimalKind.SignalingNaN)),
            "is_infinite" => Method(name, _ => PyBool.Of(IsInfinite)),
            "is_finite" => Method(name, _ => PyBool.Of(!IsSpecial)),
            "is_zero" => Method(name, _ => PyBool.Of(IsZero)),
            "is_signed" => Method(name, _ => PyBool.Of(Negative)),
            "is_canonical" => Method(name, _ => PyBool.True),
            "is_normal" => Method(name, _ => PyBool.Of(!IsSpecial && !IsZero && Adjusted >= Context.Emin)),
            "is_subnormal" => Method(name, _ => PyBool.Of(!IsSpecial && !IsZero && Adjusted < Context.Emin)),
            "normalize" => Method(name, _ => Normalize(Context)),
            "sqrt" => Method(name, _ => SquareRoot(Context)),
            "to_eng_string" => Method(name, _ => new PyStr(ToText(engineering: true))),
            "quantize" => new PyBuiltinFunction(name, (arguments, keywords) =>
            {
                var target = arguments.Length > 0 ? arguments[0]
                    : keywords?.Entries.FirstOrDefault(static e => e.Key.Display() == "exp").Value
                    ?? throw new PyRaise(PyErrors.TypeError("quantize() missing required argument 'exp' (pos 1)"));
                return Quantize(RequireOperand(target, name), RoundingArgument(arguments, keywords, 1));
            }),
            "to_integral_value" or "to_integral" or "to_integral_exact" => new PyBuiltinFunction(name, (arguments, keywords) =>
                ToIntegral(RoundingArgument(arguments, keywords, 0))),
            "compare" => Method(name, arguments =>
            {
                var other = RequireOperand(arguments[0], name);

                if (NaNResult(other, Context) is { } nan)
                {
                    return nan;
                }

                return Finite(State, false, BigInteger.Zero, 0) is var zero
                    ? CompareValues(this, other) switch
                    {
                        0 => zero,
                        > 0 => Finite(State, false, BigInteger.One, 0),
                        _ => Finite(State, true, BigInteger.One, 0),
                    }
                    : zero;
            }),
            "max" => Method(name, arguments => Extreme(RequireOperand(arguments[0], name), maximum: true)),
            "min" => Method(name, arguments => Extreme(RequireOperand(arguments[0], name), maximum: false)),
            "__floor__" => Method(name, _ => PyInt.From(Floor())),
            "__ceil__" => Method(name, _ => PyInt.From(Ceiling())),
            "__trunc__" => Method(name, _ => PyInt.From(Truncate())),
            "__round__" => Method(name, arguments => Round(arguments.Length > 0 && arguments[0] is not PyNone ? arguments[0] : null)),
            _ => null,
        };
    }

    private void RequireFiniteRatio()
    {
        if (IsNaN)
        {
            throw new PyRaise(PyErrors.ValueError("cannot convert NaN to integer ratio"));
        }

        if (IsInfinite)
        {
            throw new PyRaise(new PyException(PyExceptionType.OverflowError, "cannot convert Infinity to integer ratio"));
        }
    }

    private PyBuiltinFunction Method(string name, Func<PyObject[], PyObject> body) => new(name, body);

    private PyNamedTuple AsTuple()
    {
        var digits = Kind == DecimalKind.Infinity
            ? [PyInt.From(0)]
            : Coefficient.IsZero && IsNaN
                ? []
                : Coefficient.ToString(CultureInfo.InvariantCulture).Select(static c => (PyObject)PyInt.From(c - '0')).ToList();

        PyObject exponent = Kind switch
        {
            DecimalKind.Infinity => new PyStr("F"),
            DecimalKind.NaN => new PyStr("n"),
            DecimalKind.SignalingNaN => new PyStr("N"),
            _ => PyInt.From(Exponent),
        };

        return new PyNamedTuple(DecimalModule.DecimalTuple, [PyInt.From(Negative ? 1 : 0), new PyTuple(digits), exponent]);
    }
}
