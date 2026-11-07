using System.Globalization;
using System.Numerics;

namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// <c>complex</c>: a pair of doubles, with CPython's arithmetic, formatting and hashing.
/// </summary>
/// <remarks>
/// Upstream Monty rejects complex literals at parse time
/// (<c>.reference/monty/limitations/language.md</c>; <c>monty-spec/parse_error__complex.py</c>
/// pins that refusal and is marked <c>xfail=cpython</c>, so it is not part of the ratchet).
/// </remarks>
public sealed class PyComplex : PyObject
{
    /// <summary>Creates a complex number.</summary>
    public PyComplex(double real, double imaginary)
    {
        Real = real;
        Imaginary = imaginary;
    }

    /// <summary>The real part.</summary>
    public double Real { get; }

    /// <summary>The imaginary part.</summary>
    public double Imaginary { get; }

    /// <summary>The value as a .NET complex.</summary>
    public Complex Value => new(Real, Imaginary);

    /// <inheritdoc />
    public override string TypeName => "complex";

    /// <inheritdoc />
    public override bool IsTruthy() => Real != 0 || Imaginary != 0;

    /// <inheritdoc />
    /// <remarks>
    /// <c>2j</c>, <c>(1+2j)</c>, <c>(-0-1j)</c>, <c>(1+nanj)</c>: the real part is omitted
    /// only when it is a positive zero, and the parts use <c>repr(float)</c> without a
    /// trailing <c>.0</c>.
    /// </remarks>
    public override string Repr()
    {
        if (Real == 0 && !double.IsNegative(Real))
        {
            return Part(Imaginary) + "j";
        }

        var imaginary = Part(Imaginary);
        var sign = imaginary.StartsWith('-') || imaginary.StartsWith('+') ? string.Empty : "+";

        if (double.IsNaN(Imaginary))
        {
            sign = "+";
        }

        return $"({Part(Real)}{sign}{imaginary}j)";
    }

    private static string Part(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        var text = new PyFloat(value).Repr();
        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }

    /// <inheritdoc />
    public override bool PyEquals(PyObject other) => From(other) is { } value && value == Value;

    /// <inheritdoc />
    /// <remarks>CPython's formula, so a complex with no imaginary part hashes as its real part does.</remarks>
    public override BigInteger PyHash()
    {
        var combined = Numbers.Hash(Real) + (1000003 * Numbers.Hash(Imaginary));
        var modulus = (BigInteger.One << 64);
        combined %= modulus;

        if (combined >= modulus / 2)
        {
            combined -= modulus;
        }
        else if (combined < -(modulus / 2))
        {
            combined += modulus;
        }

        return combined == -1 ? -2 : combined;
    }

    /// <inheritdoc />
    public override PyObject? GetAttribute(string name) => name switch
    {
        "real" => new PyFloat(Real),
        "imag" => new PyFloat(Imaginary),
        "conjugate" => new PyBuiltinFunction("conjugate", _ => new PyComplex(Real, -Imaginary)),
        _ => null,
    };

    /// <inheritdoc />
    public override string? PyFormat(string spec)
    {
        if (spec.Length == 0)
        {
            return Display();
        }

        // `format(z, '.2f')` formats each part with the float rules.
        var real = StringFormatter.Format(new PyFloat(Real), spec);
        var imaginary = StringFormatter.Format(new PyFloat(Imaginary), spec.StartsWith('+') ? spec : "+" + spec);
        return $"{real}{imaginary}j";
    }

    /// <summary>Converts a number to a complex, or null when it is not one.</summary>
    public static Complex? From(PyObject value) => value switch
    {
        PyComplex complex => complex.Value,
        PyBool flag => new Complex(flag.Value ? 1 : 0, 0),
        PyInt integer => new Complex(Numbers.ToDouble(integer.Value), 0),
        PyFloat number => new Complex(number.Value, 0),
        _ => null,
    };

    /// <summary>The binary arithmetic of complex numbers, or null when neither operand is one.</summary>
    internal static PyObject? Binary(string op, PyObject left, PyObject right)
    {
        if (left is not PyComplex && right is not PyComplex)
        {
            return null;
        }

        if (From(left) is not { } a || From(right) is not { } b)
        {
            return null;
        }

        return op switch
        {
            "+" => Of(a + b),
            "-" => Of(a - b),
            "*" => Of(a * b),
            "/" => b == Complex.Zero
                ? throw new PyRaise(new PyException(PyExceptionType.ZeroDivisionError, "division by zero"))
                : Of(a / b),
            "**" => Power(a, b, right),
            "//" or "%" => throw new PyRaise(PyErrors.TypeError(
                $"unsupported operand type(s) for {op}: '{left.TypeName}' and '{right.TypeName}'")),
            _ => null,
        };
    }

    private static PyComplex Power(Complex a, Complex b, PyObject exponent)
    {
        if (b == Complex.Zero)
        {
            return new PyComplex(1, 0);
        }

        if (a == Complex.Zero)
        {
            return b.Imaginary != 0 || b.Real < 0
                ? throw new PyRaise(new PyException(PyExceptionType.ZeroDivisionError, "zero to a negative or complex power"))
                : new PyComplex(0, 0);
        }

        // An integral exponent is repeated multiplication in CPython, which is exact where
        // the polar form would round: (1+1j)**2 is exactly 2j.
        if (exponent is PyInt integer && BigInteger.Abs(integer.Value) <= 100)
        {
            var power = (int)integer.Value;
            var result = Complex.One;

            for (var i = 0; i < Math.Abs(power); i++)
            {
                result *= a;
            }

            return Of(power < 0 ? Complex.One / result : result);
        }

        return Of(Complex.Pow(a, b));
    }

    /// <summary>The unary operators, or null when the operand is not complex.</summary>
    internal static PyObject? Unary(string op, PyObject operand) => operand is not PyComplex complex
        ? null
        : op switch
        {
            "-" => new PyComplex(-complex.Real, -complex.Imaginary),
            "+" => complex,
            "abs" => new PyFloat(Complex.Abs(complex.Value)),
            _ => null,
        };

    private static PyComplex Of(Complex value) => new(value.Real, value.Imaginary);

    /// <summary><c>complex(real=0, imag=0)</c> and <c>complex('1+2j')</c>.</summary>
    internal static PyObject Construct(PyObject[] arguments, PyDict? keywords)
    {
        PyObject? Argument(int position, string name) =>
            arguments.Length > position
                ? arguments[position]
                : keywords is not null && keywords.TryGetValue(new PyStr(name), out var value) ? value : null;

        var real = Argument(0, "real");
        var imaginary = Argument(1, "imag");

        if (real is PyStr text)
        {
            return imaginary is null
                ? Parse(text.Value)
                : throw new PyRaise(PyErrors.TypeError("complex() can't take second arg if first is a string"));
        }

        var a = real is null ? Complex.Zero : From(real) ?? throw new PyRaise(PyErrors.TypeError(
            $"complex() first argument must be a string or a number, not '{real.TypeName}'"));
        var b = imaginary is null ? Complex.Zero : From(imaginary) ?? throw new PyRaise(PyErrors.TypeError(
            $"complex() second argument must be a number, not '{imaginary.TypeName}'"));

        return Of(a + (b * Complex.ImaginaryOne));
    }

    private static PyComplex Parse(string text)
    {
        var source = text.Trim();

        if (source.StartsWith('(') && source.EndsWith(')'))
        {
            source = source[1..^1].Trim();
        }

        var error = new PyRaise(PyErrors.ValueError("complex() arg is a malformed string"));

        if (source.Length == 0)
        {
            throw error;
        }

        if (!source.EndsWith('j') && !source.EndsWith('J'))
        {
            return new PyComplex(Number(source) ?? throw error, 0);
        }

        var body = source[..^1];

        // The split point is the last sign that is not part of an exponent.
        for (var i = body.Length - 1; i > 0; i--)
        {
            if (body[i] is '+' or '-' && char.ToLowerInvariant(body[i - 1]) != 'e')
            {
                var realPart = Number(body[..i]) ?? throw error;
                var imaginaryText = body[i..];
                var imaginaryPart = imaginaryText is "+" or "-" ? (imaginaryText == "-" ? -1 : 1) : Number(imaginaryText) ?? throw error;
                return new PyComplex(realPart, imaginaryPart);
            }
        }

        return new PyComplex(0, body.Length == 0 || body is "+" ? 1 : body == "-" ? -1 : Number(body) ?? throw error);

        static double? Number(string part) =>
            part.ToLowerInvariant() switch
            {
                "inf" or "+inf" or "infinity" => double.PositiveInfinity,
                "-inf" or "-infinity" => double.NegativeInfinity,
                "nan" or "+nan" or "-nan" => double.NaN,
                _ => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null,
            };
    }
}
